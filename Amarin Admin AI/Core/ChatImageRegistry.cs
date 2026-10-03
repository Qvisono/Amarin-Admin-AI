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
}
