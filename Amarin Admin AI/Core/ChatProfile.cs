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
