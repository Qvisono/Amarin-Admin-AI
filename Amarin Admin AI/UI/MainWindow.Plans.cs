using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// План агента на одобрение (C1): карточка поверх ленты, по одному плану за раз.
    /// </summary>
    /// <remarks>
    /// Устроено как вопрос об опасном действии: очередь адресная, ответ уходит показанному
    /// запросу, а не голове очереди. Отличие одно — план не держит ленту: пока человек читает,
    /// других вопросов у хода нет, и спешить некуда.
    /// </remarks>
    public partial class MainWindow
    {
        private void OnPlanReviewChanged() => Ui(ShowNextPlan);

        private void ShowNextPlan()
        {
            if (_services is null || !IsLoaded)
            {
                return;
            }

            if (!_services.PlanReviews.TryPeek(out var request))
            {
                PlanOverlay.Clear();
                PlanOverlay.Visibility = Visibility.Collapsed;
                ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible;
                return;
            }

            PlanOverlay.Show(request);
            PlanOverlay.Visibility = Visibility.Visible;
            ChatBlocked = true;
        }

        private void OnPlanDecided(PlanReviewRequest request, PlanDecision decision) =>
            _services?.PlanReviews.Complete(request, decision);
    }
}
