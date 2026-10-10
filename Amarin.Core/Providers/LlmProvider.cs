using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>Чей сервер отвечает на запрос к модели.</summary>
/// <remarks>
/// До 1.34.0 это было перечисление из двух членов. Свой сервер человека (перепродавец по
/// Base URL) перечислением не описать: таких серверов может быть сколько угодно, и каждый
/// обязан быть отдельным провайдером — со своим каталогом моделей, столбцом в плашке выбора и
/// ключами. Поэтому провайдер — значение с текстовым <see cref="Id"/>, а встроенные провайдеры
/// — его готовые экземпляры. Всё, что держало провайдера ключом словаря или сравнивало через
/// <c>==</c>, продолжает работать без правок.
/// <para>
/// <see cref="Id"/> пишется в <c>keys.json</c> и <c>settings.json</c> ровно той строкой, какую
/// писал <c>JsonStringEnumConverter</c> с <c>CamelCase</c> (<c>venice</c>, <c>openRouter</c>),
/// поэтому файлы прежних версий читаются как были, а прежние версии читают новые файлы, пока в
/// них только эти два провайдера. Переименовывать идентификаторы нельзя: у людей на диске лежат
/// записи со старыми.
/// </para>
/// <para>
/// <c>default</c> — это Venice, как и у перечисления: незаполненное поле старого файла и
/// необязательный параметр без значения означают то же, что и раньше.
/// </para>
/// </remarks>
[JsonConverter(typeof(LlmProviderJsonConverter))]
public readonly struct LlmProvider : IEquatable<LlmProvider>
{
    private const string VeniceId = "venice";
    private const string CustomPrefix = "custom-";

    /// <summary>Идентификаторы встроенных провайдеров и их имена в коде и журналах.</summary>
    private static readonly (string Id, string Name)[] Known =
    [
        (VeniceId, "Venice"),
        ("openRouter", "OpenRouter"),
        ("openAI", "OpenAI"),
        ("anthropic", "Anthropic"),
        ("google", "Google"),
        ("xAi", "XAi"),
        ("deepSeek", "DeepSeek"),
        ("moonshot", "Moonshot"),
        ("mistral", "Mistral"),
        ("zAi", "ZAi"),
        ("qwen", "Qwen"),
        ("groq", "Groq")
    ];

    private readonly string? _id;

    private LlmProvider(string id) => _id = id;

    /// <summary>Venice — единственный провайдер до версии 1.23.0.</summary>
    public static LlmProvider Venice => default;

    /// <summary>OpenRouter: OpenAI-совместимый API и общий счёт на модели разных производителей.</summary>
    public static LlmProvider OpenRouter { get; } = new("openRouter");

    public static LlmProvider OpenAI { get; } = new("openAI");

    /// <summary>Anthropic (Claude) — единственный встроенный со своим протоколом, Messages API.</summary>
    public static LlmProvider Anthropic { get; } = new("anthropic");

    /// <summary>Google Gemini через его OpenAI-совместимый вход.</summary>
    public static LlmProvider Google { get; } = new("google");

    public static LlmProvider XAi { get; } = new("xAi");

    public static LlmProvider DeepSeek { get; } = new("deepSeek");

    /// <summary>Moonshot AI — модели Kimi.</summary>
    public static LlmProvider Moonshot { get; } = new("moonshot");

    public static LlmProvider Mistral { get; } = new("mistral");

    /// <summary>Z.ai — модели GLM.</summary>
    public static LlmProvider ZAi { get; } = new("zAi");

    /// <summary>Alibaba Cloud Model Studio — модели Qwen.</summary>
    public static LlmProvider Qwen { get; } = new("qwen");

    public static LlmProvider Groq { get; } = new("groq");

    /// <summary>Строка, которой провайдер записан в файлах.</summary>
    public string Id => _id ?? VeniceId;

    /// <summary>Свой сервер человека, заданный адресом, а не встроенный провайдер.</summary>
    public bool IsCustom => Id.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>Идентификатор своего сервера без приставки. <c>null</c> — провайдер встроенный.</summary>
    public string? CustomId => IsCustom ? Id[CustomPrefix.Length..] : null;

    /// <summary>Провайдер-сервер человека с этим идентификатором.</summary>
    public static LlmProvider Custom(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new LlmProvider(CustomPrefix + id.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Провайдер по строке из файла. Неизвестная строка не бросает: её записала, может быть,
    /// более новая версия, и стереть такую запись при чтении значило бы потерять ключ.
    /// </summary>
    public static LlmProvider Parse(string? text)
    {
        var id = (text ?? "").Trim();
        if (id.Length == 0)
        {
            return Venice;
        }

        // Номер вместо имени JsonStringEnumConverter читал наравне с именем, и такие записи
        // могли появиться при правке файла руками: 0 — Venice, 1 — OpenRouter.
        if (id == "0")
        {
            return Venice;
        }

        if (id == "1")
        {
            return OpenRouter;
        }

        foreach (var (known, _) in Known)
        {
            if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase))
            {
                return known == VeniceId ? Venice : new LlmProvider(known);
            }
        }

        return id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? new LlmProvider(id.ToLowerInvariant())
            : new LlmProvider(id);
    }

    public bool Equals(LlmProvider other) =>
        string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is LlmProvider other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Id);

    /// <summary>Имя в коде и в журналах: «Venice», «OpenRouter» — как писало перечисление.</summary>
    public override string ToString()
    {
        foreach (var (known, name) in Known)
        {
            if (string.Equals(known, Id, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return Id;
    }

    public static bool operator ==(LlmProvider left, LlmProvider right) => left.Equals(right);

    public static bool operator !=(LlmProvider left, LlmProvider right) => !left.Equals(right);
}

/// <summary>
/// Провайдер в JSON — строкой <see cref="LlmProvider.Id"/>, как его писало перечисление.
/// </summary>
internal sealed class LlmProviderJsonConverter : JsonConverter<LlmProvider>
{
    public override LlmProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => LlmProvider.Parse(reader.GetString()),
            JsonTokenType.Number => LlmProvider.Parse(reader.GetInt64().ToString(CultureInfo.InvariantCulture)),
            _ => throw new JsonException("A provider is written as a string.")
        };

    public override void Write(Utf8JsonWriter writer, LlmProvider value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Id);

    public override LlmProvider ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        LlmProvider.Parse(reader.GetString());

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        LlmProvider value,
        JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Id);
}
