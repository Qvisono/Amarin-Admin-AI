using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>
/// Настройки одного чата (D11): свой промпт вместо общего и свой набор инструкций. Модель и
/// размышление у чата свои и раньше (<see cref="ChatSession.SelectedModelId"/>).
/// </summary>
public sealed class ChatProfile
{
    /// <summary>Промпт этого чата — вместо общего «основного». Пусто — общий.</summary>
    public string? Prompt { get; set; }

    /// <summary>Какие инструкции видит этот чат; null — все включённые, как у остальных.</summary>
    public List<string>? InstructionIds { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Prompt) && InstructionIds is null;

    public ChatProfile Clone() => new() { Prompt = Prompt, InstructionIds = InstructionIds is null ? null : [.. InstructionIds] };

    /// <summary>Инструкции хода с учётом профиля: из включённых — только выбранные.</summary>
    public static IReadOnlyList<Instruction> Filter(IReadOnlyList<Instruction> enabled, ChatProfile? profile) =>
        profile?.InstructionIds is not { } chosen
            ? enabled
            : enabled.Where(instruction => chosen.Contains(instruction.Id, StringComparer.Ordinal)).ToList();
}

/// <summary>Заготовка нового чата (D11): промпт, модель, размышление, инструкции.</summary>
public sealed class ChatTemplate
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Prompt { get; set; }

    public string? ModelId { get; set; }

    public bool? DisableThinking { get; set; }

    public string? ReasoningEffort { get; set; }

    public List<string>? InstructionIds { get; set; }

    /// <summary>Шаблон из настроек открытого чата.</summary>
    public static ChatTemplate From(string name, ChatSession session) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..10],
        Name = name.Trim(),
        Prompt = session.Profile?.Prompt,
        ModelId = session.SelectedModelId,
        DisableThinking = session.DisableThinking,
        ReasoningEffort = session.ReasoningEffort,
        InstructionIds = session.Profile?.InstructionIds is { } ids ? [.. ids] : null
    };

    /// <summary>Переносит заготовку в только что созданный чат.</summary>
    public void ApplyTo(ChatSession session)
    {
        var profile = new ChatProfile { Prompt = Prompt, InstructionIds = InstructionIds is null ? null : [.. InstructionIds] };
        session.Profile = profile.IsEmpty ? null : profile;
        if (!string.IsNullOrWhiteSpace(ModelId))
        {
            session.SelectedModelId = ModelId;
        }

        if (DisableThinking is { } disable)
        {
            session.DisableThinking = disable;
        }

        if (ReasoningEffort is not null)
        {
            session.ReasoningEffort = ReasoningEffort;
        }
    }
}

/// <summary>Шаблоны чатов профиля — файл <c>chat-templates.json</c>.</summary>
internal sealed class ChatTemplateBook
{
    internal const string FileName = "chat-templates.json";

    private readonly Lock _gate = new();
    private string _path;

    public ChatTemplateBook(string root) => _path = Path.Combine(root, FileName);

    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _path = Path.Combine(root, FileName);
        }
    }

    public IReadOnlyList<ChatTemplate> All()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    public void Save(ChatTemplate template)
    {
        lock (_gate)
        {
            var all = Read();
            all.RemoveAll(item => item.Id == template.Id);
            all.Add(template);
            Write(all);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.RemoveAll(item => item.Id == id) > 0)
            {
                Write(all);
            }
        }
    }

    private List<ChatTemplate> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ChatTemplate>>(File.ReadAllText(_path), AppJson.Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Write(List<ChatTemplate> all)
    {
        try
        {
            AppDataFile.WriteAtomic(_path, JsonSerializer.Serialize(all, AppJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Шаблон не записался — останется до конца сеанса; не повод прерывать работу.
        }
    }
}
