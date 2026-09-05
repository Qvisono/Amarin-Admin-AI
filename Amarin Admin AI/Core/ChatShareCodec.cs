using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>
/// Packs a conversation into a single self-contained string that can be pasted into another
/// copy of the app (the sidebar search box decodes it). Deliberately unencrypted: the payload
/// is gzip + base64url only, so anyone holding the code can read the chat.
/// </summary>
internal static class ChatShareCodec
{
    /// <summary>Format marker. Bump the digit if the payload shape ever changes.</summary>
    public const string Prefix = "AMRN1:";

    /// <summary>File extension used when a code is too long to be comfortable in a clipboard.</summary>
    public const string FileExtension = ".amrnchat";

    /// <summary>Refuse to inflate more than this — a share code is untrusted input.</summary>
    private const int MaxDecodedBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Indented, and with relaxed escaping so Cyrillic stays readable instead of becoming
    /// \u04xx escapes — the export is meant to be opened and read, not just re-imported.
    /// Safe here because the output is a file, never embedded in HTML or a script.
    /// </summary>
    private static readonly JsonSerializerOptions Export = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Encodes <paramref name="session"/> up to and including <paramref name="upToMessageId"/>.
    /// A null id shares the whole conversation.
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

    /// <summary>Decodes a share code into a fresh session, or null if it is not one.</summary>
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

            // CopyTo would happily inflate a zip bomb; copy with a hard ceiling instead.
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

            // A shared chat becomes a normal local chat with full access — new id so it can
            // never collide with, or overwrite, an existing conversation.
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
    /// Cheap prefix check for the sidebar search box, which runs it on every keystroke.
    /// The length floor matters: without it, typing the prefix by hand would be treated as a
    /// broken code on each character and pop an error per keypress. Even a one-message chat
    /// compresses to far more than this.
    /// </summary>
    public static bool LooksLikeShareCode(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length >= Prefix.Length + MinPayloadLength;
    }

    private const int MinPayloadLength = 40;

    /// <summary>Bare, unencrypted JSON dump of the dialog, model ids included.</summary>
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
        var name = string.IsNullOrWhiteSpace(title) ? "Общий чат" : title.Trim();
        return name.EndsWith("(общий)", StringComparison.Ordinal) ? name : name + " (общий)";
    }

    /// <summary>
    /// Copies the session up to the chosen message. Display and API histories are cut
    /// independently: the API list holds extra tool traffic that has no display counterpart,
    /// so it is trimmed by matching assistant/user turn count rather than by index.
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
            if (message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                seenUserTurns++;
                if (seenUserTurns > keptUserTurns)
                {
                    break;
                }
            }

            api.Add(ChatMessageCloner.CloneForStorage(message));
        }

        return new ChatSession
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            SelectedModelId = session.SelectedModelId,
            Messages = kept,
            ApiMessages = api
        };
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var normalized = new StringBuilder(text.Trim().Replace('-', '+').Replace('_', '/'));

        // Strip whitespace a paste through chat apps or e-mail may have introduced.
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
