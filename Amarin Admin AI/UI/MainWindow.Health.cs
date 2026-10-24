using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Мост между панелью «Состояние ПК» и главным окном.</summary>
    public partial class MainWindow
    {
        private void HealthButton_Click(object sender, RoutedEventArgs e) => OpenHealth();

        /// <summary>Открывает панель. Её же откроют горячая клавиша, трей и список переходов.</summary>
        internal void OpenHealth()
        {
            if (_services is null)
            {
                return;
            }

            HealthOverlay.Attach(_services.Health, () => ActiveDateFormat);
            HealthOverlay.Visibility = Visibility.Visible;
            ChatBlocked = true;

            // Только что показанная панель ещё не разложена — без этого ширина была бы нулевой.
            HealthOverlay.UpdateLayout();
            PlaceHealth();
            HealthOverlay.SizeChanged -= HealthOverlay_SizeChanged;
            HealthOverlay.SizeChanged += HealthOverlay_SizeChanged;
            HealthOverlay.Open();
        }

        /// <summary>
        /// Плашка стоит под своей кнопкой (1.37.0): заголовок окна с кнопкой живёт в том же
        /// ScaledRoot, так что место меряется в одной системе и с масштабом сходится само.
        /// </summary>
        private void PlaceHealth()
        {
            if (HealthButton.IsVisible && HealthButton.ActualWidth > 0)
            {
                var button = HealthButton.TransformToVisual(HealthOverlay)
                    .TransformBounds(new Rect(new Point(0, 0), HealthButton.RenderSize));
                HealthOverlay.Place(button);
            }
        }

        private void HealthOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (HealthOverlay.Visibility == Visibility.Visible)
            {
                PlaceHealth();
            }
        }

        private void CloseHealth()
        {
            HealthOverlay.SizeChanged -= HealthOverlay_SizeChanged;
            HealthOverlay.Cancel();
            HealthOverlay.Visibility = Visibility.Collapsed;
            ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible ||
                          PlanOverlay.Visibility == Visibility.Visible;
            FocusMessageInput();
        }

        /// <summary>
        /// «Разобраться»: новый чат, контекст карточки в поле. Не отправляется — как и остальные
        /// заготовки, текст сначала видит человек.
        /// </summary>
        private void OnHealthAskRequested(string context)
        {
            CloseHealth();
            StartNewChatFromUi();
            PlaceIncomingPrompt(context, send: false);
        }
    }
}
