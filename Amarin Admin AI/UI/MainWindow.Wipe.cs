using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// «Удалить все данные» на странице «Данные»: два вопроса, затем перезапуск, и стирает уже
    /// преемник (см. <see cref="PendingWipe"/>).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Программа выходит ради стирания: обновление на выходе не доводится.</summary>
        private bool _wipeRestart;

        private void DeleteAllDataButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(DeleteAllDataAsync(), "wipe");

        private async Task DeleteAllDataAsync()
        {
            if (_services is null)
            {
                return;
            }

            if (await AskWipeAsync() is { } includeAudit)
            {
                BeginWipe(includeAudit);
            }
        }

        /// <summary>
        /// Два вопроса перед стиранием: что пропадёт (с галочкой «и журнал аудита»), затем
        /// слово-подтверждение.
        /// </summary>
        /// <returns>null — человек передумал; иначе — стирать ли журнал аудита.</returns>
        /// <remarks>
        /// Слово, а не вторая кнопка «Да»: стирание необратимо, и две кнопки подряд нажимаются
        /// по инерции. Основная кнопка второго шага неактивна, пока слово не набрано.
        /// </remarks>
        internal async Task<bool?> AskWipeAsync()
        {
            var audit = new CheckBox { Content = Loc.Get("S.Wipe.IncludeAudit"), IsChecked = false };
            audit.SetResourceReference(StyleProperty, "DialogCheckBox");

            var profile = _services is null ? "" : ActiveProfile.Name;
            var next = await ShowNoticeAsync(
                Loc.Format("S.Wipe.Title", profile),
                Loc.Get("S.Wipe.Text"),
                Loc.Get("S.Wipe.Next"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger,
                audit);
            if (!next)
            {
                return null;
            }

            var includeAudit = audit.IsChecked == true;
            var word = Loc.Get("S.Wipe.Word");
            var input = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                MaxLength = 40
            };
            input.SetResourceReference(ForegroundProperty, "Text.Body");
            input.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text.Bright");
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Child = input
            };
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

            var answer = ShowNoticeAsync(
                Loc.Get("S.Wipe.ConfirmTitle"),
                Loc.Format("S.Wipe.ConfirmText", word),
                Loc.Get("S.Wipe.Go"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger,
                frame);

            NoticePrimaryButton.IsEnabled = false;
            input.TextChanged += (_, _) => NoticePrimaryButton.IsEnabled = ProfileDataWiper.WordMatches(input.Text, word);
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && ProfileDataWiper.WordMatches(input.Text, word))
                {
                    e.Handled = true;
                    CloseNotice(true);
                }
            };

            // Уведомление само ставит фокус на основную кнопку, но она неактивна; поле
            // получает его следом — в той же очереди, после.
            _ = Dispatcher.BeginInvoke(new Action(() => input.Focus()), DispatcherPriority.Input);

            var confirmed = await answer;
            return confirmed && ProfileDataWiper.WordMatches(input.Text, word) ? includeAudit : null;
        }

        /// <summary>
        /// Оставляет просьбу о стирании, запускает преемника и выходит.
        /// </summary>
        /// <remarks>
        /// Ходы останавливаются сразу: вопрос о запуске скрипта, оставшийся висеть под
        /// уходящим окном, не на что было бы потом ответить. Не запустился преемник — просьба
        /// убирается, и данные остаются как были: стирать без того, кто начнёт с чистого листа,
        /// значило бы оставить программу в полуразобранном виде.
        /// </remarks>
        private void BeginWipe(bool includeAudit)
        {
            if (_services is null)
            {
                return;
            }

            CancelAllTurns();

            string token;
            try
            {
                token = PendingWipe.Request(AppPaths.Root, ActiveProfile.Id, includeAudit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowWipeRestartFailure();
                return;
            }

            if (!StartWipeSuccessor(token))
            {
                PendingWipe.TryTake(AppPaths.Root, token: null, DateTime.UtcNow);
                ShowWipeRestartFailure();
                return;
            }

            _wipeRestart = true;
            RequestExit();
        }

        /// <remarks>
        /// <c>--await-exit</c> по той же причине, что и у перезапуска после обновления: замок
        /// единственного экземпляра держится до конца этого процесса, и без ожидания преемник
        /// отдал бы запрос живому владельцу и вышел.
        /// </remarks>
        private static bool StartWipeSuccessor(string token)
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
            {
                return false;
            }

            var start = new ProcessStartInfo { FileName = exe, UseShellExecute = true };
            start.ArgumentList.Add("--await-exit");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--wipe");
            start.ArgumentList.Add(token);

            try
            {
                using var process = Process.Start(start);
                return process is not null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        private void ShowWipeRestartFailure() => Detached.Run(
            ShowNoticeAsync(
                Loc.Get("S.Wipe.FailedTitle"),
                Loc.Get("S.Wipe.RestartFailed"),
                Loc.Get("S.Common.Close"),
                secondary: null),
            "wipe_failed");

        /// <summary>Отчёт преемника: данные стёрты, или что осталось на диске.</summary>
        private void ShowWipeReport(WipeResult result) => Detached.Run(
            result.Ok
                ? ShowNoticeAsync(
                    Loc.Get("S.Wipe.DoneTitle"),
                    Loc.Get("S.Wipe.Done"),
                    Loc.Get("S.Common.Close"),
                    secondary: null,
                    NoticeTone.Info)
                : ShowNoticeAsync(
                    Loc.Get("S.Wipe.PartialTitle"),
                    Loc.Format("S.Wipe.Partial", string.Join("\n", result.Failed.Select(item => "• " + item))),
                    Loc.Get("S.Common.Close"),
                    secondary: null),
            "wipe_report");
    }
}
