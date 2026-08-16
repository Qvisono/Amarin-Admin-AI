namespace Amarin.UI;

/// <summary>
/// CLI flags used by the quick-chat bar and scripts.
/// </summary>
internal sealed class StartupArgs
{
    public string? Model { get; private set; }
    public string? Prompt { get; private set; }
    public bool CenterWindow { get; private set; }
    public bool SmokeTools { get; private set; }

    public static StartupArgs Parse(string[] args)
    {
        var result = new StartupArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--smoke-tools", StringComparison.OrdinalIgnoreCase))
            {
                result.SmokeTools = true;
                continue;
            }

            if (a.Equals("--center", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--center-window", StringComparison.OrdinalIgnoreCase))
            {
                result.CenterWindow = true;
                continue;
            }

            if (TryTakeValue(args, ref i, "--model", "-m", out var model))
            {
                result.Model = model;
                continue;
            }

            if (TryTakeValue(args, ref i, "--prompt", "-p", out var prompt))
            {
                result.Prompt = prompt;
                continue;
            }

            if (TryTakeValue(args, ref i, "--prompt-file", out var promptFile))
            {
                result.Prompt = ReadPromptFile(promptFile);
            }
        }

        return result;
    }

    private static bool TryTakeValue(
        string[] args, ref int i, string longName, out string value) =>
        TryTakeValue(args, ref i, longName, shortName: null, out value);

    private static bool TryTakeValue(
        string[] args, ref int i, string longName, string? shortName, out string value)
    {
        value = string.Empty;
        var a = args[i];

        if (a.Equals(longName, StringComparison.OrdinalIgnoreCase) ||
            (shortName is not null && a.Equals(shortName, StringComparison.OrdinalIgnoreCase)))
        {
            if (i + 1 >= args.Length)
                return false;
            value = args[++i];
            return true;
        }

        var prefix = longName + "=";
        if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = a[prefix.Length..];
            return true;
        }

        if (shortName is not null)
        {
            var shortPrefix = shortName + "=";
            if (a.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = a[shortPrefix.Length..];
                return true;
            }
        }

        return false;
    }

    private static string? ReadPromptFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Temp file cleanup is best-effort.
            }

            return text;
        }
        catch
        {
            return null;
        }
    }
}
