using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Кладёт текст в буфер обмена — кроме секретов.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ClipboardWriteTool : ITool
{
    /// <summary>Больше этого в буфер не кладётся: это уже не фрагмент, а файл.</summary>
    internal const int MaxLength = 100_000;

    private readonly Func<IEnumerable<string?>> _knownSecrets;

    public ClipboardWriteTool()
        : this(() => [])
    {
    }

    /// <param name="knownSecrets">Ключи API программы: их в буфер не положит ни одна просьба.</param>
    internal ClipboardWriteTool(Func<IEnumerable<string?>> knownSecrets) => _knownSecrets = knownSecrets;

    public string Name => "write_clipboard";

    public string Description =>
        "Put plain text on the Windows clipboard so the user can paste it. Secrets - BitLocker recovery keys, " +
        "passwords, Wi-Fi keys, API keys - are refused. Requires user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Text to place on the clipboard" }
          },
          "required": ["text"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var text = arguments.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String
            ? textProp.GetString() ?? ""
            : "";

        if (text.Length == 0)
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: text"));
        }

        if (text.Length > MaxLength)
        {
            return Task.FromResult(ToolResult.Fail(Loc.Format("S.Tool.Clipboard.TooLong", MaxLength)));
        }

        if (SecretReason(text, _knownSecrets()) is { } reason)
        {
            return Task.FromResult(ToolResult.Fail(Loc.Format("S.Tool.Clipboard.Secret", reason)));
        }

        return Task.FromResult(ClipboardNative.TrySetText(text)
            ? ToolResult.Ok(Loc.Format("S.Tool.Clipboard.Written", text.Length))
            : ToolResult.Fail(Loc.Get("S.Tool.Clipboard.Busy")));
    }

    /// <summary>Что в тексте похоже на секрет, или null.</summary>
    /// <remarks>
    /// Проверка по виду, а не по источнику: ключ BitLocker, строка «пароль: …», содержимое ключа
    /// Wi-Fi из <c>netsh … key=clear</c> и ключи API самой программы. Буфер читает любое
    /// приложение на машине, и секрет, положенный туда «на минутку», уходит дальше вставкой.
    /// </remarks>
    internal static string? SecretReason(string text, IEnumerable<string?> knownSecrets)
    {
        if (BitLockerKey().IsMatch(text))
        {
            return "BitLocker recovery key";
        }

        if (WifiKey().IsMatch(text))
        {
            return "Wi-Fi key";
        }

        if (PasswordLine().IsMatch(text))
        {
            return "password";
        }

        foreach (var secret in knownSecrets)
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 8 &&
                text.Contains(secret, StringComparison.Ordinal))
            {
                return "API key";
            }
        }

        return ApiKeyShape().IsMatch(text) ? "API key" : null;
    }

    [GeneratedRegex(@"\b\d{6}(?:-\d{6}){7}\b")]
    private static partial Regex BitLockerKey();

    [GeneratedRegex(@"(?im)^\s*(?:Key Content|Содержимое ключа)\s*:\s*\S+")]
    private static partial Regex WifiKey();

    [GeneratedRegex(@"(?i)\b(?:password|passwd|pwd|passphrase|пароль|kennwort|contraseña|mot de passe)\s*[:=]\s*\S+")]
    private static partial Regex PasswordLine();

    /// <summary>Узнаваемые по виду ключи: OpenAI/OpenRouter <c>sk-…</c>, GitHub <c>ghp_…</c>, AWS <c>AKIA…</c>.</summary>
    [GeneratedRegex(@"\b(?:sk-(?:or-v1-)?[A-Za-z0-9_-]{24,}|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16})\b")]
    private static partial Regex ApiKeyShape();
}
