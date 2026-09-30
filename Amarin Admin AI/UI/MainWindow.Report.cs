using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Отчёт о работе (D7): факты — сразу и дословно, выводы модели — когда придут.
    /// </summary>
    /// <remarks>
    /// Разбор идёт служебной статьёй расхода и может не случиться (нет ключа, сеть): тогда отчёт
    /// остаётся из одних фактов, и это честно сказано в строке состояния.
    /// </remarks>
    public partial class MainWindow
    {
        private CancellationTokenSource? _reportCancel;
        private string _reportTitle = "";

        private void WireWorkReport()
        {
            ReportOverlay.Closed += () => _reportCancel?.Cancel();
            ReportOverlay.SaveRequested += OpenReportSaveMenu;
        }

        private void StartWorkReport(ChatSession session, string? upToMessageId) =>
            Detached.Run(WorkReportAsync(session, upToMessageId), "work_report");

        private async Task WorkReportAsync(ChatSession session, string? upToMessageId)
        {
            if (_services is null)
            {
                return;
            }

            _reportCancel?.Cancel();
            var cancel = _reportCancel = new CancellationTokenSource();
            var services = _services;
            var title = _reportTitle = DisplayTitle(session.Title);

            // Журнал аудита и список снимков — это чтение диска, поэтому в фоне.
            var (facts, snapshots) = await Task.Run(() =>
            {
                var audit = services.Audit?.ReadAll(cancel.Token);
                var collected = WorkReport.Collect(session, upToMessageId, audit);
                IReadOnlyList<WorkSnapshot> taken = [];
                if (OperatingSystem.IsWindows() && collected.Any(fact => fact.Changes))
                {
                    try
                    {
                        taken = WorkReport.SnapshotsDuring(
                            collected,
                            RollbackSnapshots.List().Select(entry => new WorkSnapshot(entry.Id, entry.Created, entry.Label)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Без списка снимков отчёт всё равно полон по действиям.
                    }
                }

                return (collected, taken);
            });

            var request = WorkReport.Request(session);
            var factsMarkdown = WorkReport.FactsMarkdown(title, DateTime.Now, request, facts, snapshots);
            if (facts.Count == 0)
            {
                ReportOverlay.Show(this, factsMarkdown, Loc.Get("S.Report.NothingStatus"));
                return;
            }

            if (!HasUsableKey())
            {
                ReportOverlay.Show(this, factsMarkdown, Loc.Get("S.Report.NoKey"));
                return;
            }

            ReportOverlay.Show(this, factsMarkdown, Loc.Get("S.Report.Analyzing"));
            try
            {
                var draft = await services.WorkReports.WriteAsync(request, facts, cancel.Token);
                if (cancel.IsCancellationRequested || ReportOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                var price = draft.Cost is { HasData: true } cost ? ChatExport.Money(cost.Usd) : "";
                if (draft.Analysis is null)
                {
                    ReportOverlay.SetStatus(Loc.Get("S.Report.AnalysisFailed"));
                    return;
                }

                ReportOverlay.Update(
                    factsMarkdown.TrimEnd() + "\n\n" + draft.Analysis + "\n",
                    price.Length > 0 ? Loc.Format("S.Report.Ready", price) : "");
            }
            catch (OperationCanceledException)
            {
                // Окно закрыли, не дождавшись разбора.
            }
            catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException)
            {
                if (!cancel.IsCancellationRequested)
                {
                    ReportOverlay.SetStatus(Loc.Get("S.Report.AnalysisFailed"));
                }
            }
        }

        private void OpenReportSaveMenu(string markdown, FrameworkElement anchor)
        {
            var menu = NewMenu(anchor);
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Markdown"), () => Detached.Run(SaveReportAsync(markdown, html: false), "report_md")));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Export.Html"), () => Detached.Run(SaveReportAsync(markdown, html: true), "report_html")));
            menu.IsOpen = true;
        }

        private async Task SaveReportAsync(string markdown, bool html)
        {
            var extension = html ? ".html" : ".md";
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("S.Report.WindowTitle"),
                Filter = (html ? "HTML|*.html" : "Markdown|*.md") + $"|{Loc.Get("S.Common.AllFiles")}|*.*",
                DefaultExt = extension,
                FileName = SafeFileName(Loc.Format("S.Report.Title", _reportTitle)) + extension
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var text = html
                ? ChatExport.MarkdownPage(Loc.Get("S.Report.WindowTitle"), markdown, (latex, display) => ChatPrint.FormulaPng(this, latex, display))
                : markdown;
            var path = dialog.FileName;
            try
            {
                await Task.Run(() => File.WriteAllText(path, text, new System.Text.UTF8Encoding(false)));
                ReportOverlay.SetStatus(Loc.Format("S.Report.Saved", path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportOverlay.SetStatus(Loc.Format("S.Export.Failed", ex.Message));
            }
        }
    }
}
