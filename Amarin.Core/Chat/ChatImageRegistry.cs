using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Сопоставляет короткие ссылки вида <c>amarin-image:6f2a91c4</c> с картинками.
/// <para>
/// Нарисованная картинка — мегабайт base64, перепечатать его в ответ модель не может, а ссылку —
/// может. Инструмент отдаёт ссылку, модель пишет её обычной картинкой Markdown там, где ей место,
/// а лента ищет её здесь. Так иллюстрация встаёт посреди абзаца, а не только в конце.
/// </para>
/// <para>
/// Один на процесс и только в памяти: сами байты лежат в записи чата, и реестр наполняется оттуда
/// при каждом открытии переписки.
/// </para>
/// </summary>
public static class ChatImageRegistry
{
    /// <summary>URL scheme the model is told to use.</summary>
    public const string Scheme = "amarin-image:";

    private static readonly Dictionary<string, ImageAttachment> Images = [];
    private static readonly Lock Gate = new();

    /// <summary>Заводит ссылку для только что сделанной картинки и запоминает её.</summary>
    public static string Register(ImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var handle = Scheme + Guid.NewGuid().ToString("N")[..8];
        lock (Gate)
        {
            Images[handle] = image;
        }

        return handle;
    }

    /// <summary>
    /// Регистрирует картинку под уже выданной ей ссылкой — при открытии сохранённого чата, чтобы
    /// ссылки из прошлого запуска разрешались.
    /// </summary>
    public static void Restore(ImageAttachment image)
    {
        if (image?.Label is not { Length: > 0 } handle || !IsHandle(handle))
        {
            return;
        }

        lock (Gate)
        {
            Images[handle] = image;
        }
    }

    /// <summary>Регистрирует все картинки инструментов переписки. Дёшево, повтор безвреден.</summary>
    public static void RestoreAll(ChatSession? session)
    {
        if (session is null)
        {
            return;
        }

        // Со спрятанными вариантами: переключившись на них, лента откроет и их картинки.
        foreach (var message in ChatBranches.AllMessages(session))
        {
            foreach (var round in message.ToolRounds)
            {
                foreach (var call in round.Calls)
                {
                    foreach (var image in call.Images)
                    {
                        Restore(image);
                    }
                }
            }
        }
    }

    public static ImageAttachment? Find(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        lock (Gate)
        {
            return Images.TryGetValue(handle.Trim(), out var image) ? image : null;
        }
    }

    public static bool IsHandle(string? url) =>
        url is not null && url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Больше этого исходник в памяти не держится — сохранится ужатая копия.</summary>
    private const int MaxOriginalBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Исходные байты принесённых картинок — по строке base64 их ужатой копии. Ключ — сама строка
    /// (сравнение по ссылке): <c>with</c> при выдаче ручки её не копирует, а после перезапуска
    /// исходника всё равно нет — тогда сохраняется то, что лежит в переписке.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, byte[]> Originals = [];

    /// <summary>
    /// Запоминает исходник картинки, которую переписка держит ужатой: <c>save_image</c> сохранит
    /// его, а не копию в 1920 точек с белым вместо прозрачности.
    /// </summary>
    public static void KeepOriginal(ImageAttachment image, byte[] original)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Length <= MaxOriginalBytes)
        {
            Originals.AddOrUpdate(image.Base64, original);
        }
    }

    /// <summary>Исходные байты картинки, если они ещё в памяти этого запуска; иначе null.</summary>
    public static byte[]? OriginalOf(ImageAttachment image) =>
        image is not null && Originals.TryGetValue(image.Base64, out var original) ? original : null;
}
