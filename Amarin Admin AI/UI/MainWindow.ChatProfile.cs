using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Профиль чата (D11): свой промпт и набор инструкций у одного чата. Открывается из меню «⋯»
    /// чата и с чипа у поля ввода.
    /// </summary>
    public partial class MainWindow
    {
        private void WireChatProfile()
        {
            ChatSettings.Saved += profile =>
            {
                _session.Profile = profile;
                PersistCurrent();
                UpdateProfileChip();
                RefreshContextRing();
            };
        }

        internal void OpenChatSettings()
        {
            if (_services is null)
            {
                return;
            }

            ChatSettings.Show(_session.Profile, _services.Instructions.EnabledSnapshot());
        }

        private void UpdateProfileChip()
        {
            if (ProfileChip is not null)
            {
                ProfileChip.Visibility = _session.Profile is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ProfileChip_Click(object sender, RoutedEventArgs e) => OpenChatSettings();
    }
}
