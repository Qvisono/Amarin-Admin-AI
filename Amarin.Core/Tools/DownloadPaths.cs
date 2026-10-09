using System.Runtime.InteropServices;
using System.Text;

namespace Amarin.Tools;

internal static class DownloadPaths
{
    public static string DesktopDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// Настоящая папка «Загрузки» — та, что видна в Проводнике, даже если её перенесли на другой диск.
    /// </summary>
    /// <remarks>
    /// У <see cref="Environment.SpecialFolder"/> «Загрузок» нет, и прежде путь собирался как
    /// <c>%USERPROFILE%\Downloads</c>. С 1.33.0 от этой папки раскрываются все относительные пути
    /// файловых инструментов, и у человека, перенёсшего «Загрузки» на D:, файлы ложились бы в
    /// пустую папку, которую он не открывает.
    /// </remarks>
    public static string DownloadsDirectory =>
        KnownFolder(DownloadsFolderId) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>«Документы» — для пути вида <c>Documents\отчёт.docx</c>.</summary>
    public static string DocumentsDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var buffer) != 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(buffer) is { Length: > 0 } path ? path : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static bool TryResolveDestination(
        string? destination,
        string? folder,
        Uri url,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;

        var downloads = DownloadsDirectory;
        var desktop = DesktopDirectory;
        var targetDir = string.Equals(folder, "desktop", StringComparison.OrdinalIgnoreCase)
            ? desktop
            : downloads;

        var urlFileName = GetFileNameFromUrl(url);
        if (!string.IsNullOrWhiteSpace(urlFileName))
        {
            // Keep the exact filename from the URL — do not rename or shorten.
            if (!string.IsNullOrWhiteSpace(destination) && Path.IsPathRooted(destination.Trim()))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(destination.Trim())) ?? targetDir;
                fullPath = Path.Combine(dir, urlFileName);
            }
            else
            {
                fullPath = Path.Combine(targetDir, urlFileName);
            }
        }
        else if (!string.IsNullOrWhiteSpace(destination))
        {
            destination = destination.Trim();
            fullPath = Path.IsPathRooted(destination)
                ? Path.GetFullPath(destination)
                : Path.GetFullPath(Path.Combine(targetDir, destination));
        }
        else
        {
            error = "URL has no filename in path - provide destination.";
            return false;
        }

        if (!IsUnderAllowedDirectory(fullPath, downloads) && !IsUnderAllowedDirectory(fullPath, desktop))
        {
            error =
                $"Downloads are allowed only under Desktop ({desktop}) or Downloads ({downloads}). " +
                $"Got: {fullPath}";
            return false;
        }

        return true;
    }

    private static string GetFileNameFromUrl(Uri url)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(url.LocalPath));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return string.Empty;
        }

        return SanitizeFileName(name);
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        }

        return sb.ToString().Trim();
    }

    private static bool IsUnderAllowedDirectory(string filePath, string directoryPath)
    {
        var fullFile = Path.GetFullPath(filePath);
        var fullDir = Path.GetFullPath(directoryPath);
        if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDir += Path.DirectorySeparatorChar;
        }

        return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}