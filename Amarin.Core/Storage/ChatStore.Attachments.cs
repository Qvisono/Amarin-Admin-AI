using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;
public sealed partial class ChatStore
{
    /// <summary>
    /// Кладёт вложения, которых ещё нет. Файл назван отпечатком содержимого, поэтому уже
    /// лежащий не переписывается: иначе картинка писалась бы на диск на каждом сохранении хода.
    /// </summary>
    private bool WriteBlobs(string id, IReadOnlyList<AttachmentBlob> blobs, bool encrypt)
    {
        if (blobs.Count == 0 || !IsSafeId(id))
        {
            return blobs.Count == 0;
        }

        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        Directory.CreateDirectory(folder);
        foreach (var blob in blobs)
        {
            var file = ChatAttachmentFiles.BlobPath(folder, blob.Sha);
            if (File.Exists(file))
            {
                continue;
            }

            // Отказ Windows шифровать — не повод класть картинку открытой вопреки галочке.
            var content = encrypt ? AtRestCipher.EncryptBytes(blob.Bytes) : blob.Bytes;
            if (content is null)
            {
                PerfLog.Write("chat_store encrypt_failed");
                return false;
            }

            AppDataFile.WriteAtomicBytes(file, content);
        }

        return true;
    }

    /// <summary>Убирает вложения, на которые чат больше не ссылается (сообщение удалили, ветку стёрли).</summary>
    private void RemoveOrphans(string id, IReadOnlyList<AttachmentBlob> blobs)
    {
        var key = string.Join(',', blobs.Select(blob => blob.Sha).Order(StringComparer.Ordinal));
        lock (_gate)
        {
            if (_blobSets.TryGetValue(id, out var previous) && previous == key)
            {
                return;
            }

            _blobSets[id] = key;
        }

        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        if (!IsSafeId(id) || !Directory.Exists(folder))
        {
            return;
        }

        var keep = blobs.Select(blob => blob.Sha).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*.bin"))
        {
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                TryDeleteFile(file);
            }
        }
    }

    /// <summary>Разворачивает ссылки на вынесенные вложения; чат прежнего формата — как есть.</summary>
    private string InlineAttachments(string id, string text) =>
        !ChatAttachmentFiles.HasRefs(text) || !IsSafeId(id)
            ? text
            : ChatAttachmentFiles.Inline(text, sha => ReadBlob(ChatAttachmentFiles.FolderOf(_chatsDirectory, id), sha));

    internal static byte[]? ReadBlob(string folder, string sha)
    {
        try
        {
            var file = ChatAttachmentFiles.BlobPath(folder, sha);
            return File.Exists(file) ? AtRestCipher.DecryptBytes(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Имя чата годится в путь: папка вложений строится из него.</summary>
    private static bool IsSafeId(string id) =>
        id.Length > 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..", StringComparison.Ordinal);

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Выносит вложения из чатов, записанных до F4, — байтами и под замком файла, как и
    /// перешифровка: разбор в <see cref="ChatSession"/> и сохранение затёрли бы свежий ответ
    /// идущего хода копией с диска. Формат файла (открытый или зашифрованный) остаётся прежним.
    /// </summary>
    /// <returns>Сколько чатов переписано.</returns>
    public int ExternalizeExisting(CancellationToken cancellationToken)
    {
        Flush();
        string[] files;
        try
        {
            files = Directory.Exists(_chatsDirectory) ? Directory.GetFiles(_chatsDirectory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(id, "index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                lock (FileLock(file))
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var bytes = File.ReadAllBytes(file);
                    if (AtRestCipher.DecryptFile(bytes) is not { } text)
                    {
                        continue;
                    }

                    var stored = ChatAttachmentFiles.Externalize(text, out var blobs);
                    if (blobs.Count == 0)
                    {
                        continue;
                    }

                    var encrypted = AtRestCipher.IsEncrypted(bytes);
                    if (WriteBlobs(id, blobs, encrypted) && WriteQuietly(file, stored, encrypted))
                    {
                        count++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занятый файл останется прежним — читается он и так.
            }
        }

        return count;
    }
}
