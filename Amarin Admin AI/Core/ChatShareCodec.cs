using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Amarin.Core;

/// <summary>
/// Упаковывает переписку в одну самодостаточную строку, которую можно вставить в другую копию
/// программы (её разбирает поле поиска в боковой панели). Намеренно без шифрования — только
/// gzip + base64url: кто держит код, тот читает чат.
/// </summary>
internal static class ChatShareCodec
{
    /// <summary>Метка формата. Изменится форма содержимого — увеличить цифру.</summary>
    public const string Prefix = "AMRN1:";

    /// <summary>Расширение файла для кода, слишком длинного для буфера обмена.</summary>
    public const string FileExtension = ".amrnchat";

    /// <summary>Больше этого не распаковываем: код «Поделиться» — чужие входные данные.</summary>
    private const int MaxDecodedBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = AppJson.MaxDepth,
        TypeInfoResolver = ShownBranchOnly(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// С отступами и мягким экранированием, чтобы кириллица читалась, а не превращалась в \u04xx:
    /// экспорт открывают и читают, а не только импортируют обратно. Безопасно — результат файл,
    /// он не встраивается ни в HTML, ни в скрипт.
    /// </summary>
    private static readonly JsonSerializerOptions Export = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = AppJson.MaxDepth,
        TypeInfoResolver = ShownBranchOnly(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Делятся только показанным вариантом переписки: спрятанные не пишутся и не читаются.
    /// </summary>
    /// <remarks>
    /// Срез «до этого сообщения» режет показанную ветку, а спрятанные варианты на якорях до
    /// среза утащили бы за собой целые продолжения — то, чем человек делиться не выбирал.
    /// Вырезаются при записи, а не обнулением на копии: <see cref="Trim"/> держит те же объекты
    /// сообщений, что и открытый чат, и обнуление стёрло бы варианты у живой переписки.
    /// Архив данных копирует файлы чатов целиком и сохраняет всё.
    /// </remarks>
    private static DefaultJsonTypeInfoResolver ShownBranchOnly() => new()
    {
        Modifiers =
        {
            static info =>
            {
                if (info.Type != typeof(ChatDisplayMessage))
                {
                    return;
                }

                for (var i = info.Properties.Count - 1; i >= 0; i--)
                {
                    if (info.Properties[i].AttributeProvider is MemberInfo
                        {
                            Name: nameof(ChatDisplayMessage.Variants) or nameof(ChatDisplayMessage.VariantIndex)
                        })
                    {
                        info.Properties.RemoveAt(i);
                    }
                }
            }
        }
    };

    /// <summary>
    /// Кодирует <paramref name="session"/> до <paramref name="upToMessageId"/> включительно; null —
    /// всю переписку.
    /// </summary>
    public static string Encode(ChatSession session, string? upToMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var trimmed = Trim(session, upToMessageId);
        var json = JsonSerializer.SerializeToUtf8Bytes(trimmed, Compact);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json, 0, json.Length);
        }

        return Prefix + ToBase64Url(output.ToArray());
    }

    /// <summary>Разбирает код в новую сессию; null — это не код.</summary>
    public static ChatSession? TryDecode(string? code)
    {
        var text = code?.Trim() ?? "";
        if (!LooksLikeShareCode(text))
        {
            return null;
        }

        try
        {
            var payload = FromBase64Url(text[Prefix.Length..]);
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            // CopyTo распаковал бы и zip-бомбу — копируем с жёстким потолком.
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            var session = JsonSerializer.Deserialize<ChatSession>(output.ToArray(), Compact);
            if (session is null || session.Messages.Count == 0)
            {
                return null;
            }

            // Присланный чат становится обычным своим; новый id — чтобы он не столкнулся с
            // существующей перепиской и не затёр её.
            session.Id = Guid.NewGuid().ToString("N");
            session.CreatedAt = DateTime.Now;
            session.UpdatedAt = DateTime.Now;
            session.Title = MarkShared(session.Title);
            return session;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or
                                       ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Дешёвая проверка начала строки для поля поиска — оно зовёт её на каждое нажатие. Нижняя
    /// граница длины важна: без неё набранная руками метка считалась бы битым кодом на каждом
    /// знаке и выдавала ошибку на каждое нажатие. Даже чат из одного сообщения сжимается в
    /// гораздо большее.
    /// </summary>
    public static bool LooksLikeShareCode(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length >= Prefix.Length + MinPayloadLength;
    }

    private const int MinPayloadLength = 40;

    /// <summary>Открытая выгрузка переписки в JSON, с идентификаторами моделей.</summary>
    public static string ExportJson(ChatSession session, string? upToMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return JsonSerializer.Serialize(Trim(session, upToMessageId), Export);
    }

    /// <summary>Reads back a file written by <see cref="ExportJson"/>.</summary>
    public static ChatSession? TryImportJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<ChatSession>(json, Export);
            if (session is null || session.Messages.Count == 0)
            {
                return null;
            }

            session.Id = Guid.NewGuid().ToString("N");
            session.CreatedAt = DateTime.Now;
            session.UpdatedAt = DateTime.Now;
            session.Title = MarkShared(session.Title);
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MarkShared(string? title)
    {
        // Пометка уже на месте — прежней русской или нынешней, — второй раз не ставится.
        var suffix = Loc.Get("S.Share.SharedSuffix");
        var name = string.IsNullOrWhiteSpace(title) ? Loc.Get("S.Share.SharedChat") : title.Trim();
        return name.EndsWith(suffix, StringComparison.Ordinal) || name.EndsWith("(общий)", StringComparison.Ordinal)
            ? name
            : name + " " + suffix;
    }

    /// <summary>
    /// Копирует сессию до выбранного сообщения. Ленту и историю API режем отдельно: в истории API
    /// есть служебный обмен с инструментами без пары в ленте, поэтому она обрезается по числу
    /// ходов человека и модели, а не по номеру.
    /// </summary>
    private static ChatSession Trim(ChatSession session, string? upToMessageId)
    {
        var messages = session.Messages;
        var cut = string.IsNullOrWhiteSpace(upToMessageId)
            ? messages.Count
            : messages.FindIndex(m => m.Id == upToMessageId) + 1;
        if (cut <= 0)
        {
            cut = messages.Count;
        }

        var kept = messages.Take(cut).ToList();
        var keptUserTurns = kept.Count(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

        var api = new List<ChatMessage>();
        var seenUserTurns = 0;
        foreach (var message in session.ApiMessages)
        {
            // Картинки инструментов ходами не считаются: иначе срез обрывал бы историю раньше.
            if (ChatContent.IsTurnStart(message))
            {
                seenUserTurns++;
                if (seenUserTurns > keptUserTurns)
                {
                    break;
                }
            }

            api.Add(ChatMessageCloner.CloneForStorage(message));
        }

        // Summary сюда намеренно не переносится: делиться перепиской — не то же, что делиться
        // пересказом, а у получателя она соберётся заново при первом же ответе.
        return new ChatSession
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            SelectedModelId = session.SelectedModelId,
            DisableThinking = session.DisableThinking,
            ReasoningEffort = session.ReasoningEffort,
            Messages = kept,
            ApiMessages = api
        };
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var normalized = new StringBuilder(text.Trim().Replace('-', '+').Replace('_', '/'));

        // Убираем пробелы и переносы, которые добавляют мессенджеры и почта.
        for (var i = normalized.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(normalized[i]))
            {
                normalized.Remove(i, 1);
            }
        }

        normalized.Append('=', (4 - (normalized.Length % 4)) % 4);
        return Convert.FromBase64String(normalized.ToString());
    }
}
