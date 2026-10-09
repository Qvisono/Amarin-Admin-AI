using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Ручки вида <c>amarin-attachment:3f9c0a1b2c4d/отчёт.docx</c> для документов, приложенных к
/// сообщениям, — чтобы модель дочитывала и копировала вложение, у которого нет файла на диске.
/// </summary>
/// <remarks>
/// <para>
/// Ручка выводится из содержимого (первые 12 знаков SHA-256) и имени, а не выдаётся случайно:
/// она попадает в текст сообщения, а тот лежит в истории модели и после перезапуска должен
/// указывать на то же вложение. Поэтому реестр ничего не хранит на диске — он только узнаёт
/// вложения открытых переписок.
/// </para>
/// <para>
/// Хэш считается по первому вопросу, а не при открытии чата: открытие идёт на потоке окна, и
/// десятимегабайтный PDF держал бы его. Сначала отбираются вложения с тем же именем, хэш — только
/// у них. Ссылки слабые: закрытая переписка не держит свои документы в памяти через реестр.
/// </para>
/// </remarks>
internal static class ChatAttachmentRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<WeakReference<FileAttachment>> Known = [];
    private static readonly ConditionalWeakTable<FileAttachment, string> Handles = [];

    /// <summary>Запоминает вложения переписки, включая спрятанные варианты. Дёшево, повтор безвреден.</summary>
    public static void RestoreAll(ChatSession? session)
    {
        if (session is null)
        {
            return;
        }

        foreach (var message in ChatBranches.AllMessages(session))
        {
            foreach (var file in message.Files)
            {
                Remember(file);
            }
        }
    }

    public static void Remember(FileAttachment file)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (Gate)
        {
            Known.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (!Known.Any(reference => reference.TryGetTarget(out var known) && ReferenceEquals(known, file)))
            {
                Known.Add(new WeakReference<FileAttachment>(file));
            }
        }
    }

    /// <summary>Ручка вложения. Считает хэш содержимого — зовите с рабочего потока.</summary>
    public static string HandleOf(FileAttachment file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Remember(file);
        return Handles.GetValue(file, Compute);
    }

    /// <summary>Вложение по ручке: имя и содержимое. Null — такого среди открытых переписок нет.</summary>
    public static (string Name, byte[] Content)? Find(string? handle)
    {
        if (!FileToolPaths.IsAttachmentHandle(handle))
        {
            return null;
        }

        var wanted = handle.Trim();
        var name = Path.GetFileName(wanted);
        List<FileAttachment> candidates;
        lock (Gate)
        {
            candidates = Known
                .Select(reference => reference.TryGetTarget(out var file) ? file : null)
                .OfType<FileAttachment>()
                .Where(file => string.Equals(SafeName(file.FileName), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var file in candidates)
        {
            if (string.Equals(Handles.GetValue(file, Compute), wanted, StringComparison.OrdinalIgnoreCase) &&
                Decode(file.Base64) is { } content)
            {
                return (file.FileName, content);
            }
        }

        return null;
    }

    private static string Compute(FileAttachment file)
    {
        var content = Decode(file.Base64) ?? [];
        var stamp = Convert.ToHexString(SHA256.HashData(content))[..12].ToLowerInvariant();
        return FileToolPaths.AttachmentScheme + stamp + "/" + SafeName(file.FileName);
    }

    /// <summary>
    /// Имя как имя: чат мог приехать с чужой машины, и <c>..\</c> или двоеточие в нём не должны
    /// превращать ручку в путь.
    /// </summary>
    private static string SafeName(string? name)
    {
        var bare = Path.GetFileName((name ?? "").Replace('/', '\\'));
        var safe = string.Concat(bare.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
