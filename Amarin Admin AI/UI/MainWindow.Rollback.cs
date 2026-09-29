using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>Строка плана или итога отката в журнале.</summary>
    internal sealed record RollbackLineView(string Glyph, string Text, string Tone);

    /// <summary>
    /// Откат снимка из журнала: сначала план — что вернётся и чего откат не тронет, — и только
    /// после второго нажатия применение.
    /// </summary>
    /// <remarks>
    /// Прежде вернуть снимок мог только агент, вызовом <c>change_rollback restore</c>, и делал это
    /// вслепую. Теперь человек видит тот же план, что и модель (<see cref="RollbackText.Summary"/>
    /// собран из тех же строк), и нажимает «Откатить» под ним.
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Самый длинный показываемый план: дальше — строка «и ещё N».</summary>
        private const int RollbackLinesShown = 300;

        /// <summary>План, ждущий подтверждения; null, пока его не построили или после применения.</summary>
        private RollbackPlan? _rollbackPlan;

        private void ResetRollbackUi(JournalRow row)
        {
            _rollbackPlan = null;
            JournalRollbackBox.Visibility = Visibility.Collapsed;
            JournalRollbackLines.ItemsSource = null;
            JournalRollbackNotes.ItemsSource = null;

            JournalRollbackButton.Visibility = row.Snapshot is not null && OperatingSystem.IsWindows()
                ? Visibility.Visible
                : Visibility.Collapsed;
            JournalRollbackButton.IsEnabled = true;
            JournalRollbackButton.Content = Loc.Get("S.Journal.Rollback.Preview");

            JournalRestoreSystemButton.Visibility =
                row.RestorePoint is not null || (row.Entry is { } entry && JournalView.IsRestorePointCreate(entry))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private async void JournalRollbackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_journalDetail?.Snapshot is not { } snapshot || !OperatingSystem.IsWindows())
            {
                return;
            }

            if (AnyTurnRunning)
            {
                ShowRollbackMessage(Loc.Get("S.Journal.Rollback.Busy"));
                return;
            }

            var row = _journalDetail;
            JournalRollbackButton.IsEnabled = false;
            JournalRollbackButton.Content = Loc.Get("S.Journal.Rollback.Planning");
            JournalAnswerBox.Visibility = Visibility.Collapsed;

            RollbackPlan? plan;
            string? error;
            try
            {
                (plan, error) = await Task.Run(() =>
                {
                    var built = ChangeRollbackOperations.Plan(snapshot.Id, out var why);
                    return (built, why);
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or AggregateException)
            {
                (plan, error) = (null, ex.Message);
            }

            // Человек мог уйти к другой строке, пока шло сравнение.
            if (!ReferenceEquals(_journalDetail, row))
            {
                return;
            }

            JournalRollbackButton.Content = Loc.Get("S.Journal.Rollback.Preview");
            if (plan is null)
            {
                JournalRollbackButton.IsEnabled = true;
                ShowRollbackMessage(Loc.Format("S.Journal.Rollback.Failed", error ?? ""));
                return;
            }

            ShowRollbackPlan(plan);
        }

        private void ShowRollbackPlan(RollbackPlan plan)
        {
            _rollbackPlan = plan.IsEmpty ? null : plan;

            JournalRollbackTitle.Text = plan.IsEmpty
                ? Loc.Get("S.Rollback.Nothing")
                : Loc.Get("S.Journal.Rollback.PlanTitle");
            JournalRollbackLines.ItemsSource = Capped(plan.Lines().Select(line => new RollbackLineView("•", line, "")));
            JournalRollbackNotes.ItemsSource = plan.Notes;
            JournalRollbackNotesTitle.Visibility = plan.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            JournalRollbackHint.Visibility = plan.IsEmpty ? Visibility.Collapsed : Visibility.Visible;

            JournalRollbackApplyButton.Content = Loc.Format("S.Journal.Rollback.Apply", plan.Count);
            JournalRollbackApplyButton.Visibility = plan.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            JournalRollbackApplyButton.IsEnabled = true;
            JournalRollbackCancelButton.Visibility = Visibility.Visible;
            JournalRollbackCancelButton.IsEnabled = true;
            JournalRollbackCancelButton.Content = Loc.Get(plan.IsEmpty ? "S.Common.Close" : "S.Journal.Rollback.Cancel");
            JournalRollbackActions.Visibility = Visibility.Visible;

            JournalRollbackBox.Visibility = Visibility.Visible;
            JournalRollbackButton.Visibility = Visibility.Collapsed;
            JournalRollbackBox.BringIntoView();
        }

        private async void JournalRollbackApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rollbackPlan is not { } plan || !OperatingSystem.IsWindows())
            {
                return;
            }

            // Между планом и нажатием мог начаться ход — его агент менял бы систему вместе с нами.
            if (AnyTurnRunning)
            {
                ShowRollbackMessage(Loc.Get("S.Journal.Rollback.Busy"));
                return;
            }

            var row = _journalDetail;
            _rollbackPlan = null;
            JournalRollbackApplyButton.IsEnabled = false;
            JournalRollbackCancelButton.IsEnabled = false;
            JournalRollbackApplyButton.Content = Loc.Get("S.Journal.Rollback.Applying");

            IReadOnlyList<RollbackOutcome> outcomes;
            try
            {
                outcomes = await Task.Run(() => ChangeRollbackOperations.Apply(plan)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                outcomes = [new RollbackOutcome(Loc.Get("S.Journal.Rollback.ResultTitle"), false, ex.Message)];
            }

            if (!ReferenceEquals(_journalDetail, row))
            {
                return;
            }

            var ok = outcomes.Count(outcome => outcome.Success);
            JournalRollbackTitle.Text = Loc.Get("S.Journal.Rollback.ResultTitle") + "  ·  " +
                                        Loc.Format("S.Rollback.Done", ok, outcomes.Count);
            JournalRollbackLines.ItemsSource = Capped(outcomes.Select(outcome => outcome.Success
                ? new RollbackLineView("✓", outcome.Line, "Success")
                : new RollbackLineView("✕", outcome.Line + " — " + outcome.Error, "Failure")));
            JournalRollbackHint.Visibility = Visibility.Collapsed;
            JournalRollbackApplyButton.Visibility = Visibility.Collapsed;
            JournalRollbackCancelButton.Content = Loc.Get("S.Common.Close");
            JournalRollbackCancelButton.IsEnabled = true;
        }

        private void JournalRollbackCancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_journalDetail is { } row)
            {
                ResetRollbackUi(row);
            }
        }

        private void JournalRestoreSystemButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Мастер просит права администратора сам; отказ в окне UAC — обычный ответ.
                Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true })?.Dispose();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: человек закрыл запрос прав — ничего не случилось.
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                ShowRollbackMessage(Loc.Format("S.Journal.RestoreSystem.Failed", ex.Message));
            }
        }

        private void ShowRollbackMessage(string text)
        {
            JournalAnswerBox.Visibility = Visibility.Visible;
            JournalAnswerText.Text = text;
        }

        private static List<RollbackLineView> Capped(IEnumerable<RollbackLineView> lines)
        {
            var list = lines.ToList();
            if (list.Count <= RollbackLinesShown)
            {
                return list;
            }

            var shown = list.Take(RollbackLinesShown).ToList();
            shown.Add(new RollbackLineView("…", Loc.Format("S.Journal.Rollback.More", list.Count - RollbackLinesShown), ""));
            return shown;
        }
    }
}
