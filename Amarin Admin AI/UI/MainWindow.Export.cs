using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Выгрузка чата (D6): меню форматов у кнопки под ответом и в меню «⋯» списка — Markdown,
    /// HTML, PDF, печать и прежний JSON.
    /// </summary>
    /// <remarks>
    /// Всё выгружаемое — открытым текстом, поэтому меню подчиняется той же настройке, что и
    /// «Поделиться» (<see cref="SharingEnabled"/>): кто выключил обмен, не хочет и файлов.
    /// </remarks>
    public partial class MainWindow
    {
        internal enum ExportFormat
        {
            Markdown,
            Html,
            Json
        }

        private void OpenExportMenu(
            FrameworkElement anchor,
            ChatSession session,
            string? upToMessageId,
            System.Windows.Controls.Primitives.PlacementMode placement = System.Windows.Controls.Primitives.PlacementMode.Bottom)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var menu = NewMenu(anchor, placement);
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Markdown"), () => Detached.Run(ExportAsAsync(session, upToMessageId, ExportFormat.Markdown), "export_md")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Html"), () => Detached.Run(ExportAsAsync(session, upToMessageId, ExportFormat.Html), "export_html")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Pdf"), () => PrintChat(session, upToMessageId, pdf: true)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Print"), () => PrintChat(session, upToMessageId, pdf: false)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.ExportJson"), () => ExportSession(session, upToMessageId)));
            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.Report.MenuItem"), () => StartWorkReport(session, upToMessageId)));
            AddExportOptions(menu, () => OpenExportMenu(anchor, session, upToMessageId, placement));
            menu.IsOpen = true;
        }

        /// <summary>
        /// Выгрузка нескольких чатов: Markdown, HTML и JSON — по файлу на чат в выбранную папку,
        /// PDF и печать — одним документом, каждый чат с новой страницы. Отчёт о работе — про один
        /// чат, его здесь нет.
        /// </summary>
        private void OpenExportManyMenu(
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            System.Windows.Controls.Primitives.PlacementMode placement)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var menu = NewMenu(anchor, placement);
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Markdown"), () => Detached.Run(ExportManyAsync(ids, ExportFormat.Markdown), "export_many_md")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Html"), () => Detached.Run(ExportManyAsync(ids, ExportFormat.Html), "export_many_html")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Pdf"), () => Detached.Run(PrintChatsAsync(ids, pdf: true), "print_many_pdf")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Print"), () => Detached.Run(PrintChatsAsync(ids, pdf: false), "print_many")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.ExportJson"), () => Detached.Run(ExportManyAsync(ids, ExportFormat.Json), "export_many_json")));
            AddExportOptions(menu, () => OpenExportManyMenu(anchor, ids, placement));
            menu.IsOpen = true;
        }

        /// <summary>
        /// Галочки «с инструментами» и «с ценами». Меняют настройку и открывают меню заново: пункт
        /// меню закрывает его, а выбирать формат человек будет уже с новыми галочками.
        /// </summary>
        private void AddExportOptions(ContextMenu menu, Action reopen)
        {
            var settings = _services!.Settings;
            menu.Items.Add(Divider());
            menu.Items.Add(CheckItem(Loc.Get("S.Export.WithTools"), settings.ExportIncludeTools, null, () =>
            {
                settings.ExportIncludeTools = !settings.ExportIncludeTools;
                _services.SettingsStore.Save(settings);
                reopen();
            }));
            menu.Items.Add(CheckItem(Loc.Get("S.Export.WithCosts"), settings.ExportIncludeCosts, null, () =>
            {
                settings.ExportIncludeCosts = !settings.ExportIncludeCosts;
                _services.SettingsStore.Save(settings);
                reopen();
            }));
        }

        /// <summary>
        /// Чаты для пакетного действия. Открытый — из памяти, со всеми несохранёнными правками;
        /// остальные читаются с диска в фоне: на сотне чатов это заметное время, и потоку окна
        /// его ждать незачем. Удалённый тем временем чат просто пропускается.
        /// </summary>
        private async Task<List<ChatSession>> LoadChatsAsync(IReadOnlyList<string> ids)
        {
            var store = _services!.ChatStore;
            var open = _session;
            var loaded = await Task.Run(() => ids
                .Select(id => id == open.Id ? open : store.TryLoad(id))
                .ToList()).ConfigureAwait(true);
            return [.. loaded.OfType<ChatSession>()];
        }

        /// <summary>Выгрузка нескольких чатов по файлу на чат в папку, которую выберет человек.</summary>
        private async Task ExportManyAsync(IReadOnlyList<string> ids, ExportFormat format)
        {
            if (_services is null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("S.Export.ChooseFolder") };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var folder = dialog.FolderName;
            var sessions = await LoadChatsAsync(ids);
            var extension = format switch
            {
                ExportFormat.Html => ".html",
                ExportFormat.Json => ".json",
                _ => ".md"
            };

            var files = new List<(string Path, string Text)>(sessions.Count);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in sessions)
            {
                var text = format switch
                {
                    // Формулы для HTML рисует WPF — на потоке окна, по чату, с передышкой между ними.
                    ExportFormat.Html => ChatExport.ToHtml(BuildExport(session, null), (latex, display) => ChatPrint.FormulaPng(this, latex, display)),
                    ExportFormat.Json => ChatShareCodec.ExportJson(session),
                    _ => ChatExport.ToMarkdown(BuildExport(session, null))
                };
                files.Add((FreeFileName(folder, SafeFileName(DisplayTitle(session.Title)), extension, taken), text));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }

            try
            {
                await Task.Run(() =>
                {
                    foreach (var (path, text) in files)
                    {
                        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowNoticeAsync(Loc.Get("S.Export.FailedTitle"), Loc.Format("S.Export.Failed", ex.Message), Loc.Get("S.Common.Close"), null);
                return;
            }

            var open = await ShowNoticeAsync(
                Loc.Get("S.Export.SavedManyTitle"),
                Loc.Format("S.Export.SavedMany", files.Count, folder),
                Loc.Get("S.Export.OpenFolder"),
                Loc.Get("S.Common.Close"),
                NoticeTone.Info);
            if (open && files.Count > 0)
            {
                AttachmentOpener.RevealInExplorer(files[0].Path);
            }
        }

        /// <summary>
        /// Свободное имя файла в папке: два чата с одним названием не затирают друг друга, а
        /// получают « (2)», « (3)».
        /// </summary>
        private static string FreeFileName(string folder, string name, string extension, HashSet<string> taken)
        {
            for (var n = 1; ; n++)
            {
                var candidate = Path.Combine(folder, (n == 1 ? name : $"{name} ({n})") + extension);
                if (!File.Exists(candidate) && taken.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>PDF или печать нескольких чатов одним документом, каждый чат с новой страницы.</summary>
        private async Task PrintChatsAsync(IReadOnlyList<string> ids, bool pdf)
        {
            if (_services is null)
            {
                return;
            }

            var sessions = await LoadChatsAsync(ids);
            if (sessions.Count == 0)
            {
                return;
            }

            var dialog = new PrintDialog();
            if (pdf && FindPdfPrinter() is { } queue)
            {
                dialog.PrintQueue = queue;
            }

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var exports = sessions.Select(session => BuildExport(session, null)).ToList();
                var document = ChatPrint.BuildMany(this, exports, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, exports[0].Title);
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or IOException)
            {
                await ShowNoticeAsync(Loc.Get("S.Export.FailedTitle"), Loc.Format("S.Export.Failed", ex.Message), Loc.Get("S.Common.Close"), null);
            }
        }

        private ChatExportDocument BuildExport(ChatSession session, string? upToMessageId)
        {
            var settings = _services!.Settings;
            return ChatExport.Build(
                session,
                new ChatExportOptions(settings.ExportIncludeTools, settings.ExportIncludeCosts, upToMessageId),
                DateTime.Now);
        }

        /// <summary>
        /// Документ собирается на потоке интерфейса (формулы рисует WPF), а пишется на диск в
        /// фоне: с картинками внутри файл бывает в десятки мегабайт.
        /// </summary>
        private async Task ExportAsAsync(ChatSession session, string? upToMessageId, ExportFormat format)
        {
            if (_services is null)
            {
                return;
            }

            var (extension, filter) = format switch
            {
                ExportFormat.Html => (".html", "HTML|*.html"),
                _ => (".md", "Markdown|*.md")
            };
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("S.Share.ExportTitle"),
                Filter = $"{filter}|{Loc.Get("S.Common.AllFiles")}|*.*",
                DefaultExt = extension,
                FileName = SafeFileName(session.Title) + extension
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var export = BuildExport(session, upToMessageId);
            var text = format == ExportFormat.Html
                ? ChatExport.ToHtml(export, (latex, display) => ChatPrint.FormulaPng(this, latex, display))
                : ChatExport.ToMarkdown(export);
            var path = dialog.FileName;
            try
            {
                await Task.Run(() => File.WriteAllText(path, text, new System.Text.UTF8Encoding(false)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowNoticeAsync(Loc.Get("S.Export.FailedTitle"), Loc.Format("S.Export.Failed", ex.Message), Loc.Get("S.Common.Close"), null);
                return;
            }

            var open = await ShowNoticeAsync(
                Loc.Get("S.Export.SavedTitle"),
                path,
                Loc.Get("S.Export.OpenFolder"),
                Loc.Get("S.Common.Close"),
                NoticeTone.Info);
            if (open)
            {
                AttachmentOpener.RevealInExplorer(path);
            }
        }

        /// <summary>
        /// Печать или PDF. Размер страницы берётся у выбранного принтера — поэтому документ
        /// собирается после диалога, а не до.
        /// </summary>
        private void PrintChat(ChatSession session, string? upToMessageId, bool pdf)
        {
            if (_services is null)
            {
                return;
            }

            var dialog = new PrintDialog();
            if (pdf && FindPdfPrinter() is { } queue)
            {
                dialog.PrintQueue = queue;
            }

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var export = BuildExport(session, upToMessageId);
                var document = ChatPrint.Build(this, export, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, export.Title);
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or IOException)
            {
                Detached.Run(
                    ShowNoticeAsync(Loc.Get("S.Export.FailedTitle"), Loc.Format("S.Export.Failed", ex.Message), Loc.Get("S.Common.Close"), null),
                    "print_failed");
            }
        }

        /// <summary>
        /// Встроенный в Windows принтер в PDF, по его обычному имени. Не нашёлся (компонент удалён
        /// или назван иначе) — диалог откроется на принтере по умолчанию, и PDF выберут в нём.
        /// </summary>
        private static PrintQueue? FindPdfPrinter()
        {
            try
            {
                using var server = new LocalPrintServer();
                return server.GetPrintQueues().FirstOrDefault(queue =>
                    string.Equals(queue.Name, "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
