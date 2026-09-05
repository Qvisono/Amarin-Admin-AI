using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Maps short handles like <c>amarin-image:6f2a91c4</c> to the picture they stand for.
/// <para>
/// A generated image is a megabyte of base64, which the model plainly cannot retype into its
/// answer — but a handle it can. The tool hands one back, the model writes it as an ordinary
/// markdown image wherever the picture belongs, and the renderer looks it up here. That is what
/// lets the assistant place an illustration mid-paragraph rather than only at the end.
/// </para>
/// <para>
/// Process-wide and in-memory only: the bytes themselves live on the chat record, and the
/// registry is repopulated from there whenever a conversation is opened.
/// </para>
/// </summary>
public static class ChatImageRegistry
{
    /// <summary>URL scheme the model is told to use.</summary>
    public const string Scheme = "amarin-image:";

    private static readonly Dictionary<string, ImageAttachment> Images = [];
    private static readonly Lock Gate = new();

    /// <summary>Mints a handle for a freshly produced image and remembers it.</summary>
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
    /// Re-registers an image under the handle it was already given. Used when a stored chat is
    /// reopened, so links written in a previous run still resolve.
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

    /// <summary>Re-registers every tool image in a conversation. Cheap and idempotent.</summary>
    public static void RestoreAll(ChatSession? session)
    {
        if (session is null)
        {
            return;
        }

        foreach (var message in session.Messages)
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
