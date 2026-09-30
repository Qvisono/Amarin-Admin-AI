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

        private void OpenExportMenu(FrameworkElement anchor, ChatSession session, string? upToMessageId)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var settings = _services.Settings;
            var menu = NewMenu(anchor);
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Markdown"), () => Detached.Run(ExportAsAsync(session, upToMessageId, ExportFormat.Markdown), "export_md")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Html"), () => Detached.Run(ExportAsAsync(session, upToMessageId, ExportFormat.Html), "export_html")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Pdf"), () => PrintChat(session, upToMessageId, pdf: true)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Print"), () => PrintChat(session, upToMessageId, pdf: false)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.ExportJson"), () => ExportSession(session, upToMessageId)));
            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.Report.MenuItem"), () => StartWorkReport(session, upToMessageId)));
            menu.Items.Add(Divider());

            // Галочки меняют настройку и открывают меню заново: пункт меню закрывает его, а
            // выбирать формат человек будет уже с новыми галочками.
            menu.Items.Add(CheckItem(Loc.Get("S.Export.WithTools"), settings.ExportIncludeTools, null, () =>
            {
                settings.ExportIncludeTools = !settings.ExportIncludeTools;
                _services.SettingsStore.Save(settings);
                OpenExportMenu(anchor, session, upToMessageId);
            }));
            menu.Items.Add(CheckItem(Loc.Get("S.Export.WithCosts"), settings.ExportIncludeCosts, null, () =>
            {
                settings.ExportIncludeCosts = !settings.ExportIncludeCosts;
                _services.SettingsStore.Save(settings);
                OpenExportMenu(anchor, session, upToMessageId);
            }));
            menu.IsOpen = true;
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
