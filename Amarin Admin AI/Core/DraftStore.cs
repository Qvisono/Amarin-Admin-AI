using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Недописанное сообщение чата: текст, цитаты и вложения (D12).</summary>
internal sealed record ChatDraftContent(
    string Text,
    IReadOnlyList<MessageQuote> Quotes,
    IReadOnlyList<ImageAttachment> Images,
    IReadOnlyList<FileAttachment> Files)
{
    public bool IsEmpty => Text.Trim().Length == 0 && Quotes.Count == 0 && Images.Count == 0 && Files.Count == 0;
}

/// <summary>
/// Черновики чатов (D12): <c>drafts/&lt;id&gt;.json</c> в папке профиля, крупные вложения —
/// отдельными файлами <c>drafts/files/&lt;sha256&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// У каждого чата свой черновик: переключение чата больше не уносит набранное в соседний, а
/// прикреплённое не пропадает. Новый, ещё не сохранённый чат пишет под ключом
/// <see cref="NewChatKey"/> — так недописанное в нём переживает и перезапуск.
/// </para>
/// <para>
/// При шифровании чатов черновики шифруются тем же DPAPI: иначе недописанное лежало бы открытым
/// рядом с зашифрованной перепиской. Крупное вложение выносится в файл один раз и не
/// переписывается на каждую букву — по содержимому (sha256) оно и узнаётся.
/// </para>
/// </remarks>
internal sealed class DraftStore
{
    internal const string FolderName = "drafts";
    internal const string NewChatKey = "_new";

    /// <summary>Вложение больше этого (в знаках base64) едет в черновике ссылкой на файл.</summary>
    internal const int InlineLimit = 256 * 1024;

    private readonly Lock _gate = new();
    private string _folder;
    private Func<bool> _encrypt;

    public DraftStore(string root, Func<bool> encrypt)
    {
        _folder = Path.Combine(root, FolderName);
        _encrypt = encrypt;
    }

    public void UseRoot(string root, Func<bool> encrypt)
    {
        lock (_gate)
        {
            _folder = Path.Combine(root, FolderName);
            _encrypt = encrypt;
        }
    }

    private sealed class Stored
    {
        public string Text { get; set; } = "";

        public List<MessageQuote> Quotes { get; set; } = [];

        public List<StoredImage> Images { get; set; } = [];

        public List<StoredFile> Files { get; set; } = [];

        public DateTime SavedAt { get; set; }
    }

    private sealed class StoredImage
    {
        public string? Base64 { get; set; }

        public string? Blob { get; set; }

        public string MimeType { get; set; } = "image/png";

        public string? Label { get; set; }
    }

    private sealed class StoredFile
    {
        public string? Base64 { get; set; }

        public string? Blob { get; set; }

        public string MimeType { get; set; } = "";

        public string FileName { get; set; } = "";

        public long SizeBytes { get; set; }

        public string? SourcePath { get; set; }
    }

    /// <summary>Годится ли ключ в имя файла: id чатов — буквы, цифры, «-» и «_».</summary>
    internal static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= 64 && key.All(symbol => char.IsAsciiLetterOrDigit(symbol) || symbol is '-' or '_');

    /// <summary>Сохраняет черновик; пустой — удаляет. Для рабочего потока: вложения бывают большими.</summary>
    public void Save(string key, ChatDraftContent draft)
    {
        if (!IsValidKey(key))
        {
            return;
        }

        if (draft.IsEmpty)
        {
            Delete(key);
            return;
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var stored = new Stored
                {
                    Text = draft.Text,
                    Quotes = [.. draft.Quotes],
                    Images = draft.Images.Select(image =>
                    {
                        var (inline, blob) = Place(image.Base64);
                        return new StoredImage { Base64 = inline, Blob = blob, MimeType = image.MimeType, Label = image.Label };
                    }).ToList(),
                    Files = draft.Files.Select(file =>
                    {
                        var (inline, blob) = Place(file.Base64);
                        return new StoredFile
                        {
                            Base64 = inline, Blob = blob, MimeType = file.MimeType, FileName = file.FileName,
                            SizeBytes = file.SizeBytes, SourcePath = file.SourcePath
                        };
                    }).ToList(),
                    SavedAt = DateTime.Now
                };
                WriteText(Path.Combine(_folder, key + ".json"), JsonSerializer.Serialize(stored, AppJson.Options));
                CollectBlobsLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Черновик — удобство: не записался — останется в поле до закрытия программы.
            }
        }
    }

    public ChatDraftContent? TryLoad(string key)
    {
        if (!IsValidKey(key))
        {
            return null;
        }

        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_folder, key + ".json");
                if (!File.Exists(path) || ReadText(path) is not { } json ||
                    JsonSerializer.Deserialize<Stored>(json, AppJson.Options) is not { } stored)
                {
                    return null;
                }

                var images = new List<ImageAttachment>();
                foreach (var image in stored.Images)
                {
                    if (Resolve(image.Base64, image.Blob) is { } data)
                    {
                        images.Add(new ImageAttachment(data, image.MimeType, image.Label));
                    }
                }

                var files = new List<FileAttachment>();
                foreach (var file in stored.Files)
                {
                    if (Resolve(file.Base64, file.Blob) is { } data)
                    {
                        files.Add(new FileAttachment(data, file.MimeType, file.FileName, file.SizeBytes, file.SourcePath));
                    }
                }

                return new ChatDraftContent(stored.Text, stored.Quotes, images, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void Delete(string key)
    {
        if (!IsValidKey(key))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_folder, key + ".json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    CollectBlobsLocked();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Черновики чатов, которых больше нет (удалены из другого места, в другом профиле…), —
    /// вон; черновик нового чата остаётся. Для рабочего потока, на запуске.
    /// </summary>
    public void Prune(IReadOnlyCollection<string> alive)
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_folder))
                {
                    return;
                }

                foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    var key = Path.GetFileNameWithoutExtension(path);
                    if (key != NewChatKey && !alive.Contains(key))
                    {
                        File.Delete(path);
                    }
                }

                CollectBlobsLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private (string? Inline, string? Blob) Place(string base64)
    {
        if (base64.Length <= InlineLimit)
        {
            return (base64, null);
        }

        var id = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(base64))).ToLowerInvariant();
        var files = Path.Combine(_folder, "files");
        Directory.CreateDirectory(files);
        var path = Path.Combine(files, id);
        if (!File.Exists(path))
        {
            WriteText(path, base64);
        }

        return (null, id);
    }

    private string? Resolve(string? inline, string? blob)
    {
        if (!string.IsNullOrEmpty(inline))
        {
            return inline;
        }

        if (blob is null || blob.Length != 64 || !blob.All(char.IsAsciiHexDigitLower))
        {
            return null;
        }

        var path = Path.Combine(_folder, "files", blob);
        return File.Exists(path) ? ReadText(path) : null;
    }

    /// <summary>Файлы вложений, на которые не ссылается ни один черновик, — удаляются.</summary>
    private void CollectBlobsLocked()
    {
        var files = Path.Combine(_folder, "files");
        if (!Directory.Exists(files))
        {
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                if (ReadText(path) is { } json && JsonSerializer.Deserialize<Stored>(json, AppJson.Options) is { } stored)
                {
                    used.UnionWith(stored.Images.Select(image => image.Blob).OfType<string>());
                    used.UnionWith(stored.Files.Select(file => file.Blob).OfType<string>());
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (var blob in Directory.EnumerateFiles(files))
        {
            if (!used.Contains(Path.GetFileName(blob)))
            {
                File.Delete(blob);
            }
        }
    }

    private void WriteText(string path, string text)
    {
        var bytes = _encrypt() ? AtRestCipher.EncryptFile(text) : Encoding.UTF8.GetBytes(text);
        if (bytes is not null)
        {
            AppDataFile.WriteAtomicBytes(path, bytes);
        }
    }

    private static string? ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return AtRestCipher.IsEncrypted(bytes) ? AtRestCipher.DecryptFile(bytes) : Encoding.UTF8.GetString(bytes);
    }
}
