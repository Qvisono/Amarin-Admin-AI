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
            HealthOverlay.Open();
        }

        private void CloseHealth()
        {
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
