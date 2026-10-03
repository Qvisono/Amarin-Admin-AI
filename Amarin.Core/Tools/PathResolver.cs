using System.Text.RegularExpressions;

namespace Amarin.Tools;

internal static class PathResolver
{
    private static readonly Regex UsersWildcardSegment = new(
        @"[/\\]Users[/\\]\*[/\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryResolve(string? path, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is empty.";
            return false;
        }

        var expanded = ExpandKnownAliases(path.Trim());
        expanded = Environment.ExpandEnvironmentVariables(expanded);
        expanded = ExpandTilde(expanded);
        expanded = ExpandUsersWildcard(expanded);
        expanded = NormalizeDesktopPath(expanded);

        if (ContainsUnresolvedWildcard(expanded))
        {
            error =
                $"Path contains unresolved wildcards: {expanded}. " +
                $"Use exact paths from the system prompt (Desktop: {DownloadPaths.DesktopDirectory}).";
            return false;
        }

        try
        {
            resolved = Path.GetFullPath(expanded);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid path '{expanded}': {ex.Message}";
            return false;
        }
    }

    private static string ExpandKnownAliases(string path)
    {
        if (path.Equals("desktop", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("~desktop", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadPaths.DesktopDirectory;
        }

        if (path.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("~downloads", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadPaths.DownloadsDirectory;
        }

        if (path.Equals("~", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path;
    }

    private static string ExpandTilde(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
        {
            return userProfile;
        }

        if (path[1] is '/' or '\\')
        {
            return Path.Combine(userProfile, path[2..]);
        }

        return path;
    }

    private static string ExpandUsersWildcard(string path)
    {
        if (!path.Contains('*', StringComparison.Ordinal))
        {
            return path;
        }

        var userName = Environment.UserName;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var expanded = UsersWildcardSegment.Replace(
            path,
            $"{Path.DirectorySeparatorChar}Users{Path.DirectorySeparatorChar}{userName}{Path.DirectorySeparatorChar}");

        if (expanded.Contains('*', StringComparison.Ordinal))
        {
            expanded = expanded.Replace("*", userName, StringComparison.Ordinal);
        }

        if (expanded.Contains('*', StringComparison.Ordinal))
        {
            return expanded;
        }

        var desktopSuffix = $"{Path.DirectorySeparatorChar}Desktop";
        if (expanded.EndsWith(desktopSuffix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetDirectoryName(expanded),
                Path.GetDirectoryName(userProfile),
                StringComparison.OrdinalIgnoreCase) == false)
        {
            // Redirect to the real Desktop folder (OneDrive redirection, localized paths).
            return DownloadPaths.DesktopDirectory;
        }

        return expanded;
    }

    private static string NormalizeDesktopPath(string path)
    {
        var desktop = DownloadPaths.DesktopDirectory;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.Equals($"{userProfile}\\Desktop", StringComparison.OrdinalIgnoreCase) ||
            path.Equals($"{userProfile}/Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return desktop;
        }

        return path;
    }

    private static bool ContainsUnresolvedWildcard(string path) =>
        path.Contains('*', StringComparison.Ordinal) ||
        path.Contains('?', StringComparison.Ordinal);
}