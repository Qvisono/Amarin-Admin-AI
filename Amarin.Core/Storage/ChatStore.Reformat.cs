using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;
public sealed partial class ChatStore
{
    /// <summary>
    /// Приводит файлы чатов и описи к текущему значению <see cref="Encrypt"/> — в фоне, по
    /// одному файлу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Файлы перекладываются байтами, а не через разбор в <see cref="ChatSession"/> и обратное
    /// сохранение: копия, прочитанная с диска, могла бы лечь поверх свежей записи идущего хода.
    /// Здесь же читается и пишется то, что на диске сейчас, под замком файла.
    /// </para>
    /// <para>
    /// Цель запоминается при вызове: после смены профиля <see cref="Encrypt"/> смотрит уже на
    /// настройки другого профиля. Новый вызов отменяет прежний проход. Файл, который здесь не
    /// расшифровать (перенесён с другой машины), остаётся как есть.
    /// </para>
    /// </remarks>
    /// <returns>Проход — ради тестов; программа его не ждёт.</returns>
    public Task EnsureFormat()
    {
        var target = Encrypt();
        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _reformat, cancel)?.Cancel();
        return Task.Run(() => Reformat(target, cancel.Token));
    }

    /// <summary>Останавливает перешифровку: хранилище больше не ведёт эту папку.</summary>
    public void StopReformat() => Interlocked.Exchange(ref _reformat, null)?.Cancel();

    private void Reformat(bool encrypt, CancellationToken cancellationToken)
    {
        string[] files;
        try
        {
            files = Directory.Exists(_chatsDirectory) ? Directory.GetFiles(_chatsDirectory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Вложения — отдельно от самого чата: прерванный прежний проход мог перевести файл
            // чата и не успеть с его вложениями.
            ReformatBlobs(file, encrypt);

            try
            {
                lock (FileLock(file))
                {
                    // Файл удалили, пока до него шла очередь: воскрешать его нельзя.
                    if (!File.Exists(file) || HasFormat(file, encrypt))
                    {
                        continue;
                    }

                    if (AtRestCipher.DecryptFile(File.ReadAllBytes(file)) is not { } text)
                    {
                        continue;
                    }

                    if (encrypt)
                    {
                        if (AtRestCipher.EncryptFile(text) is { } sealedBytes)
                        {
                            AppDataFile.WriteAtomicBytes(file, sealedBytes);
                        }
                    }
                    else
                    {
                        AppDataFile.WriteAtomic(file, text);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл занят — останется в прежнем формате, читается он и так.
            }
        }
    }

    /// <summary>Вложения чата (F4) — в тот же формат, что и сам чат, под тем же замком.</summary>
    private void ReformatBlobs(string chatFile, bool encrypt)
    {
        var id = Path.GetFileNameWithoutExtension(chatFile);
        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        if (!IsSafeId(id) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            lock (FileLock(chatFile))
            {
                foreach (var blob in Directory.GetFiles(folder, "*.bin"))
                {
                    if (HasFormat(blob, encrypt) || AtRestCipher.DecryptBytes(File.ReadAllBytes(blob)) is not { } plain)
                    {
                        continue;
                    }

                    if ((encrypt ? AtRestCipher.EncryptBytes(plain) : plain) is { } content)
                    {
                        AppDataFile.WriteAtomicBytes(blob, content);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Формат файла уже тот: смотрим только на первые байты, не читая чат целиком.</summary>
    private static bool HasFormat(string file, bool encrypted)
    {
        Span<byte> head = stackalloc byte[AtRestCipher.FileMagic.Length];
        using var stream = File.OpenRead(file);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return AtRestCipher.IsEncrypted(head[..read]) == encrypted;
    }
}
