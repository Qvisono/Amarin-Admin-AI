namespace Amarin.Tools;

public sealed record ImageAttachment(string Base64, string MimeType, string? Label = null);