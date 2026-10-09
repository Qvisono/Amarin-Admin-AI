namespace Amarin.Core;

/// <summary>
/// Откуда читается документ: файл на диске или вложение чата, которое есть только в памяти.
/// </summary>
/// <remarks>
/// Вложение без исходного файла (вставлено из буфера, чат приехал с другой машины) до 1.33.0
/// разворачивалось копией во <c>%TEMP%</c>: открытым текстом при включённом шифровании чатов,
/// общей для всех профилей и никогда не удаляемой. Теперь читатели берут поток, а у вложения он
/// из памяти — на диск ничего не ложится.
/// </remarks>
/// <param name="Path">
/// Путь файла или ручка вложения (<c>amarin-attachment:…/имя.docx</c>): по расширению выбирается
/// читатель, и он же стоит в заголовке чтения.
/// </param>
/// <param name="Content">Содержимое вложения; null — читать файл по <paramref name="Path"/>.</param>
internal sealed record DocumentSource(string Path, byte[]? Content = null)
{
    public static DocumentSource File(string path) => new(path);

    /// <summary>Вложение из памяти, а не файл: править его на месте нечего.</summary>
    public bool InMemory => Content is not null;

    public Stream OpenRead() =>
        Content is { } bytes ? new MemoryStream(bytes, writable: false) : System.IO.File.OpenRead(Path);

    public byte[] ReadAllBytes() => Content ?? System.IO.File.ReadAllBytes(Path);
}
