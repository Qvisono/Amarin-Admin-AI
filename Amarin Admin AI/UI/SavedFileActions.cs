using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Что можно сделать с файлом, который ИИ положил на диск: открыть, показать в папке, скопировать в
/// буфер, сохранить копию в другое место, перетащить в другую программу.
/// </summary>
/// <remarks>
/// <para>
/// До 1.33.0 щелчок по карточке только показывал файл в проводнике: чтобы отправить готовый
/// документ в мессенджер, его искали в «Загрузках» руками. Теперь щелчок открывает файл, а
/// меню и перетаскивание отдают его дальше одним действием.
/// </para>
/// <para>
/// Исполняемое (<c>.exe</c>, <c>.ps1</c> и прочее) по щелчку не запускается, а показывается в
/// папке — как и у вложений (<see cref="AttachmentOpener"/>): от щелчка по карточке никто не ждёт
/// запуска того, что скачал или написал ИИ.
/// </para>
/// </remarks>
internal static class SavedFileActions
{
    /// <summary>
    /// Метка своего переноса в данных перетаскивания: окно не открывает на него зону приёма
    /// вложений — иначе карточка, протащенная над лентой, прикрепилась бы к сообщению.
    /// </summary>
    internal const string OwnDragFormat = "Amarin.SavedFileDrag";

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".reg", ".sh", ".exe", ".com", ".msi", ".vbs", ".js", ".jse", ".wsf", ".lnk"
    };

    /// <summary>Подключает к карточке щелчок, меню по правой кнопке и перетаскивание.</summary>
    public static void Attach(FrameworkElement card, Tools.SavedFile file)
    {
        Point? pressed = null;
        var dragged = false;

        // Без этого щелчок по карточке внутри ленты начинает тянуть выделение текста —
        // тот же приём стоит на картинке в ответе, см. ImageBlockView.
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pressed = e.GetPosition(card);
            dragged = false;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed || !Exists(file.Path))
            {
                return;
            }

            var now = e.GetPosition(card);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            dragged = true;
            pressed = null;
            Drag(card, file.Path);
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pressed = null;
            if (!dragged)
            {
                Open(card, file);
            }
        };
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(card, file);
        };
    }

    /// <summary>Открывает файл программой по умолчанию; папку — в проводнике; исполняемое — показывает в папке.</summary>
    public static void Open(FrameworkElement host, Tools.SavedFile file)
    {
        if (Directory.Exists(file.Path))
        {
            Start(host, "explorer.exe", Quoted(file.Path), file.FileName);
            return;
        }

        if (!File.Exists(file.Path))
        {
            MainWindow.Inform(host, Loc.Get("S.Links.OpenFailed"), Loc.Format("S.Attach.RevealFailed", file.FileName));
            return;
        }

        if (RunnableExtensions.Contains(Path.GetExtension(file.Path)))
        {
            Reveal(host, file);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Для расширения нет программы — покажем файл, а не ошибку: дальше человек решит сам.
            Reveal(host, file);
        }
    }

    public static void Reveal(FrameworkElement host, Tools.SavedFile file)
    {
        if (!AttachmentOpener.RevealInExplorer(file.Path) && Directory.Exists(file.Path))
        {
            Start(host, "explorer.exe", Quoted(file.Path), file.FileName);
            return;
        }

        if (!Exists(file.Path))
        {
            MainWindow.Inform(host, Loc.Get("S.Links.OpenFailed"), Loc.Format("S.Attach.RevealFailed", file.FileName));
        }
    }

    /// <summary>Кладёт файл в буфер обмена файлом, а не путём: вставка в мессенджер или папку его прикрепит.</summary>
    public static void Copy(FrameworkElement host, Tools.SavedFile file)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { file.Path });
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            // Буфер держит другая программа: повторить через миг — дело человека, ронять окно незачем.
            MainWindow.Inform(host, Loc.Get("S.SavedFile.CopyFailedTitle"), Loc.Get("S.SavedFile.CopyFailed"));
        }
    }

    /// <summary>Копия файла туда, куда укажет человек; сам файл остаётся на месте.</summary>
    public static void SaveAs(FrameworkElement host, Tools.SavedFile file)
    {
        var extension = Path.GetExtension(file.FileName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = file.FileName,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = extension.Length > 1
                ? $"{extension.TrimStart('.').ToUpperInvariant()} (*{extension})|*{extension}|{Loc.Get("S.SavedFile.AllFiles")} (*.*)|*.*"
                : $"{Loc.Get("S.SavedFile.AllFiles")} (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(host)) != true)
        {
            return;
        }

        try
        {
            if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file.Path, dialog.FileName, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MainWindow.Inform(host, Loc.Get("S.SavedFile.SaveFailedTitle"), Loc.Format("S.SavedFile.SaveFailed", file.FileName));
        }
    }

    private static void ShowMenu(FrameworkElement card, Tools.SavedFile file) => BuildMenu(card, file).IsOpen = true;

    /// <summary>Меню карточки: у файла — открыть, показать, скопировать, сохранить как; у папки — первые два.</summary>
    internal static ContextMenu BuildMenu(FrameworkElement card, Tools.SavedFile file)
    {
        var present = Exists(file.Path);
        var folder = Directory.Exists(file.Path);
        var menu = AppMenu.At(card, PlacementMode.MousePoint);
        menu.Items.Add(AppMenu.Item(card, Loc.Get("S.SavedFile.Open"), () => Open(card, file), icon: "Icon.Menu.Open", enabled: present));
        menu.Items.Add(AppMenu.Item(card, Loc.Get("S.SavedFile.Reveal"), () => Reveal(card, file), icon: "Icon.Menu.Folder", enabled: present));
        if (!folder)
        {
            menu.Items.Add(AppMenu.Divider(card));
            menu.Items.Add(AppMenu.Item(card, Loc.Get("S.SavedFile.Copy"), () => Copy(card, file), icon: "Icon.Menu.Copy", enabled: present));
            menu.Items.Add(AppMenu.Item(card, Loc.Get("S.SavedFile.SaveAs"), () => SaveAs(card, file), icon: "Icon.Menu.Export", enabled: present));
        }

        return menu;
    }

    private static void Drag(FrameworkElement card, string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetData(OwnDragFormat, true);
        try
        {
            DragDrop.DoDragDrop(card, data, DragDropEffects.Copy);
        }
        catch (COMException)
        {
            // Приёмник перетаскивания упал у себя — файл на месте, повторить можно.
        }
    }

    private static void Start(FrameworkElement host, string program, string arguments, string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MainWindow.Inform(host, Loc.Get("S.Links.OpenFailed"), Loc.Format("S.Attach.RevealFailed", name));
        }
    }

    private static string Quoted(string path) => "\"" + path + "\"";

    private static bool Exists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
