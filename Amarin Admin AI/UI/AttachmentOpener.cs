using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Открывает документ, прикреплённый к сообщению, в системном приложении.
/// </summary>
/// <remarks>
/// Содержимое вложения лежит в самом чате, поэтому открыть его можно всегда — даже когда
/// исходный файл переехал или чат открыт на другой машине: в этом случае документ разворачивается
/// во временную папку. Исполняемые расширения открываются только через проводник: <c>.ps1</c>,
/// <c>.bat</c>, <c>.reg</c> есть в списке поддерживаемых документов, а ShellExecute на них не
/// показывает файл, а запускает его — от одного клика по карточке этого никто не ждёт.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AttachmentOpener
{
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".reg", ".sh", ".exe", ".com", ".msi", ".vbs", ".js", ".jse", ".wsf"
    };

    /// <summary>Открывает вложение по клику, а на отказ показывает внятную строку, не исключение.</summary>
    internal static void Open(FrameworkElement host, FileAttachment attachment)
    {
        if (TryOpen(attachment))
        {
            return;
        }

        var owner = Window.GetWindow(host);
        var message = Loc.Format("S.Attach.OpenFailed", attachment.FileName);
        if (owner is null)
        {
            MessageBox.Show(message, attachment.FileName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(owner, message, owner.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Открывает вложение; возвращает <c>false</c>, если показать его не удалось.</summary>
    internal static bool TryOpen(FileAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        try
        {
            var path = attachment.SourcePath is { Length: > 0 } source && File.Exists(source)
                ? source
                : Materialize(attachment);

            if (path is null)
            {
                return false;
            }

            if (RunnableExtensions.Contains(Path.GetExtension(path)))
            {
                return Reveal(path);
            }

            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Показывает файл в проводнике, выделив его. Возвращает <c>false</c>, если показывать нечего.
    /// </summary>
    /// <remarks>
    /// Именно показать, а не открыть: карточку рисуют для всего, что инструмент положил на диск,
    /// включая <c>.exe</c> и <c>.ps1</c>, — от клика по ней никто не ждёт запуска скачанного.
    /// </remarks>
    internal static bool RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return Reveal(path);
        }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Путь для подсказки карточки: настоящий, пока файл на месте.</summary>
    internal static string? DescribeLocation(FileAttachment attachment) =>
        attachment.SourcePath is { Length: > 0 } source && File.Exists(source) ? source : null;

    /// <summary>
    /// Разворачивает вложение во временную папку, когда исходного файла на месте уже нет.
    /// </summary>
    private static string? Materialize(FileAttachment attachment)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Amarin Admin AI", "attachments");
        Directory.CreateDirectory(directory);

        // Имя приходит из чата, который мог приехать по ссылке с чужой машины: берём только
        // имя файла, чтобы "..\\..\\autorun.inf" не увёл запись за пределы временной папки.
        var name = Path.GetFileName(attachment.FileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Convert.FromBase64String(attachment.Base64));
        return path;
    }

    private static bool Reveal(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            // Кавычки обязательны: в пути бывают пробелы, а explorer режет аргумент по ним.
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
        return true;
    }

    private static bool IsOpenFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException
            or ExternalException or InvalidOperationException or NotSupportedException;
}
