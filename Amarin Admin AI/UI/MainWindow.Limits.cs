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
            services.SpendGuard.Warned = breach => Ui(() => ShowTransientNotice(
                _session.Id,
                Loc.Format("S.Limit.Warn", SpendRules.KindName(breach.Kind), SpendRules.Money(breach.Spent), SpendRules.Money(breach.Limit))));
        }

        /// <summary>Остаток перешёл порог вниз (E4).</summary>
        private void OnBalanceAlert(BalanceAlert alert)
        {
            var text = alert.KeyLabel is { } key
                ? Loc.Format("S.Balance.AlertKey", key, SpendRules.Money(alert.Usd), SpendRules.Money(alert.Threshold))
                : Loc.Format(alert.Level == BalanceLevel.Critical ? "S.Balance.AlertCritical" : "S.Balance.AlertLow",
                    SpendRules.Money(alert.Usd), SpendRules.Money(alert.Threshold));
            ShowTransientNotice(_session.Id, text);
        }

        /// <summary>Вопрос человеку. True — продолжать ход.</summary>
        private Task<bool> AskSpendAsync(SpendQuestion question, CancellationToken cancellationToken) =>
            Dispatcher.InvokeAsync(() => AskSpendOnUiAsync(question, cancellationToken)).Task.Unwrap();

        private async Task<bool> AskSpendOnUiAsync(SpendQuestion question, CancellationToken cancellationToken)
        {
            var breach = question.Breach;
            var turn = breach.Kind == SpendLimitKind.Turn;
            var text = turn
                ? Loc.Format("S.Limit.TurnAsk", SpendRules.Money(question.TurnSpent), SpendRules.Money(breach.Limit))
                : Loc.Format("S.Limit.PeriodAsk", SpendRules.KindName(breach.Kind), SpendRules.Money(breach.Limit), SpendRules.Money(breach.Spent));

            // «Изменить лимиты» — ссылкой под текстом, а не третьей кнопкой: окно уведомлений
            // умеет две, а поднять лимит — не ответ на вопрос, а уход туда, где его решают.
            var edit = new Button
            {
                Content = Loc.Get("S.Limit.Edit"),
                Style = (Style)FindResource("InlineLinkButton"),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var answer = ShowNoticeAsync(
                Loc.Get(turn ? "S.Limit.TurnTitle" : "S.Limit.PeriodTitle"),
                text,
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
                OpenSettingsPage(NavKey);
                KeyPage.ShowLimits();
            }

            return go;
        }
    }
}
