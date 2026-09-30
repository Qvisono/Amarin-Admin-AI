using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Вложение, вынесенное из файла чата: отпечаток и байты.</summary>
internal readonly record struct AttachmentBlob(string Sha, byte[] Bytes);

/// <summary>
/// Вложения отдельными файлами (F4): <c>chats/&lt;id&gt;/attachments/&lt;sha256&gt;.bin</c>, а в JSON
/// чата — ссылка <c>amrn-att:…</c>.
/// </summary>
/// <remarks>
/// <para>
/// Вынос делается на границе <see cref="ChatStore"/>, а не в модели: в памяти вложение остаётся
/// base64, и всё, что с ним работает (запрос к модели, отрисовка, «Поделиться», экспорт), ничего
/// не знает о файлах. Прежде картинка лежала в чате дважды — в ленте голым base64 и в истории
/// модели <c>data:</c>-URI — и переписывалась целиком на каждом сохранении, то есть дважды
/// в секунду, пока идёт ответ.
/// </para>
/// <para>
/// Правка — по байтам: меняются только сами строки вложений, остальной текст файла остаётся тем,
/// что написал сериализатор. Годится любая длинная строка, которая целиком base64 или
/// <c>data:…;base64,…</c>, — по полям не перечисляем: вложения лежат в нескольких местах
/// разметки, и новое место не должно выпадать молча. Вынос обратим до байта: строка, которую
/// <see cref="Convert.ToBase64String(byte[])"/> не воспроизвела бы в точности, остаётся на месте.
/// </para>
/// <para>
/// Одинаковые байты — один файл: картинка из ленты и она же в истории модели дают один отпечаток.
/// Старые файлы с base64 внутри читаются как есть.
/// </para>
/// </remarks>
internal static class ChatAttachmentFiles
{
    public const string RefPrefix = "amrn-att:";

    public const string FolderName = "attachments";

    /// <summary>Строки короче не выносятся: файл ради пары килобайт дороже, чем они сами.</summary>
    public const int MinChars = 2048;

    private static readonly byte[] RefPrefixBytes = Encoding.ASCII.GetBytes(RefPrefix);

    /// <summary>Папка вложений чата.</summary>
    public static string FolderOf(string chatsFolder, string chatId) =>
        Path.Combine(chatsFolder, chatId, FolderName);

    public static string BlobPath(string folder, string sha) => Path.Combine(folder, sha + ".bin");

    /// <summary>Есть ли в тексте чата ссылки — быстрый отсев перед разбором.</summary>
    public static bool HasRefs(string json) => json.Contains(RefPrefix, StringComparison.Ordinal);

    /// <summary>Выносит вложения. Возвращает JSON со ссылками и сами вложения (без повторов).</summary>
    public static string Externalize(string json, out IReadOnlyList<AttachmentBlob> blobs)
    {
        var found = new Dictionary<string, AttachmentBlob>(StringComparer.Ordinal);
        var result = Rewrite(json, value =>
        {
            if (value.Length < MinChars || !TrySplit(value, out var mime, out var base64))
            {
                return null;
            }

            if (Decode(base64) is not { } bytes)
            {
                return null;
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            found.TryAdd(sha, new AttachmentBlob(sha, bytes));
            return mime is null ? RefPrefix + sha : RefPrefix + sha + "|" + mime;
        }, onlyLong: true);

        blobs = found.Values.ToList();
        return result;
    }

    /// <summary>
    /// Разворачивает ссылки. Вложение, которого нет на месте, остаётся ссылкой: пустая строка
    /// на его месте при следующем сохранении стёрла бы и саму ссылку — а файл мог просто ещё не
    /// доехать из резервной копии.
    /// </summary>
    public static string Inline(string json, Func<string, byte[]?> load)
    {
        if (!HasRefs(json))
        {
            return json;
        }

        return Rewrite(json, value =>
        {
            if (!value.StartsWith(RefPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var body = value[RefPrefix.Length..];
            var bar = body.IndexOf('|');
            var sha = bar < 0 ? body : body[..bar];
            if (sha.Length != 64 || load(sha) is not { } bytes)
            {
                return null;
            }

            var base64 = Convert.ToBase64String(bytes);
            return bar < 0 ? base64 : "data:" + body[(bar + 1)..] + ";base64," + base64;
        }, onlyLong: false);
    }

    /// <summary>Отпечатки, на которые ссылается чат, — чтобы убрать осиротевшие файлы.</summary>
    public static HashSet<string> Referenced(string json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!HasRefs(json))
        {
            return set;
        }

        Rewrite(json, value =>
        {
            if (value.StartsWith(RefPrefix, StringComparison.Ordinal))
            {
                var body = value[RefPrefix.Length..];
                var bar = body.IndexOf('|');
                set.Add(bar < 0 ? body : body[..bar]);
            }

            return null;
        }, onlyLong: false);
        return set;
    }

    /// <summary>
    /// Проходит строки JSON и заменяет те, для которых <paramref name="map"/> вернула не null.
    /// Всё между заменами копируется байтами.
    /// </summary>
    private static string Rewrite(string json, Func<string, string?> map, bool onlyLong)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        ArrayBufferWriter<byte>? output = null;
        var copied = 0;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }

                // Длина до разбора: обходить каждую короткую строку ради GetString незачем.
                if (onlyLong ? reader.ValueSpan.Length < MinChars : !reader.ValueSpan.StartsWith(RefPrefixBytes))
                {
                    continue;
                }

                var replacement = map(reader.GetString() ?? "");
                if (replacement is null)
                {
                    continue;
                }

                output ??= new ArrayBufferWriter<byte>(bytes.Length);
                var start = (int)reader.TokenStartIndex;
                output.Write(bytes.AsSpan(copied, start - copied));
                output.Write(Quote(replacement));
                copied = (int)reader.BytesConsumed;
            }
        }
        catch (JsonException)
        {
            // Повреждённый JSON не трогаем: пусть его разбирает тот, кто и так с ним разбирается.
            return json;
        }

        if (output is null)
        {
            return json;
        }

        output.Write(bytes.AsSpan(copied));
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    /// <summary>
    /// Строка в кавычках. Без экранирования «+» и «/», как у сериализатора по умолчанию: base64
    /// ими полон, а JSON их не требует.
    /// </summary>
    private static byte[] Quote(string value)
    {
        var encoded = JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        var utf8 = encoded.EncodedUtf8Bytes;
        var result = new byte[utf8.Length + 2];
        result[0] = (byte)'"';
        utf8.CopyTo(result.AsSpan(1));
        result[^1] = (byte)'"';
        return result;
    }

    private static bool TrySplit(string value, out string? mime, out string base64)
    {
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            var marker = value.IndexOf(";base64,", StringComparison.Ordinal);
            if (marker > 5 && value.IndexOf(',', 5) == marker + 7)
            {
                mime = value[5..marker];
                base64 = value[(marker + 8)..];
                return mime.Length > 0 && !mime.Contains('|');
            }

            mime = null;
            base64 = "";
            return false;
        }

        mime = null;
        base64 = value;
        return true;
    }

    /// <summary>Байты base64 — только если строка ровно та, что дал бы <see cref="Convert.ToBase64String(byte[])"/>.</summary>
    private static byte[]? Decode(string base64)
    {
        if (base64.Length == 0 || base64.Length % 4 != 0)
        {
            return null;
        }

        var bytes = new byte[base64.Length / 4 * 3];
        if (!Convert.TryFromBase64String(base64, bytes, out var written))
        {
            return null;
        }

        var exact = bytes.AsSpan(0, written).ToArray();
        return string.Equals(Convert.ToBase64String(exact), base64, StringComparison.Ordinal) ? exact : null;
    }
}
