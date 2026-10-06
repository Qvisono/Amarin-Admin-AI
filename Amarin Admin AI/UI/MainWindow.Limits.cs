using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Лимиты трат (E1): вопрос «продолжить?» посреди хода и предупреждение у порога.
    /// </summary>
    /// <remarks>
    /// Решает <see cref="SpendGuard"/>, окно только спрашивает. Спрашивает из сетевого потока хода,
    /// поэтому всё — через диспетчер, а ход ждёт ответа, не держа поток интерфейса.
    /// </remarks>
    public partial class MainWindow
    {
        private void WireSpendLimits()
        {
            if (_services is not { } services)
            {
                return;
            }

            services.SpendGuard.Ask = AskSpendAsync;
            services.SpendGuard.Warned = breach => Ui(() => NotifyStatus(SpendPrompts.Warning(breach)));
        }

        /// <summary>Остаток перешёл порог вниз (E4).</summary>
        private void OnBalanceAlert(BalanceAlert alert) => NotifyStatus(SpendPrompts.BalanceAlert(alert));

        /// <summary>Вопрос человеку. True — продолжать ход.</summary>
        private Task<bool> AskSpendAsync(SpendQuestion question, CancellationToken cancellationToken) =>
            Dispatcher.InvokeAsync(() => AskSpendOnUiAsync(question, cancellationToken)).Task.Unwrap();

        internal async Task<bool> AskSpendOnUiAsync(SpendQuestion question, CancellationToken cancellationToken)
        {
            var prompt = SpendPrompts.Question(question);

            // «Изменить лимиты» — ссылкой под текстом, а не третьей кнопкой: окно уведомлений
            // умеет две, а поднять лимит — не ответ на вопрос, а уход туда, где его решают.
            var edit = new Button
            {
                Content = Loc.Get("S.Limit.Edit"),
                Style = (Style)FindResource("InlineLinkButton"),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var answer = ShowNoticeAsync(
                prompt.Title,
                prompt.Text,
                Loc.Get("S.Limit.Continue"),
                Loc.Get("S.Limit.Stop"),
                NoticeTone.Warning,
                edit);

            var editing = false;
            edit.Click += (_, _) =>
            {
                editing = true;
                CloseNotice(false);
            };

            // «Стоп» у хода, пока окно открыто: вопрос больше некому задавать.
            using var registration = cancellationToken.Register(() => Ui(() =>
            {
                if (ReferenceEquals(_notice?.Task, answer))
                {
                    CloseNotice(false);
                }
            }));

            var go = await answer.ConfigureAwait(true);
            if (editing)
            {
                OpenSettings(SettingsUi.NavKey);
                KeyPage.ShowLimits();
            }

            return go;
        }
    }
}
