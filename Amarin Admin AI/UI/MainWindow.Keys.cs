using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Мост между страницей «Key &amp; Info» и главным окном: модалки и то, что после смены
    /// ключа врёт.
    /// </summary>
    /// <remarks>
    /// Диалоги живут здесь, а не на самой странице: оверлеем, а не отдельным окном, потому что
    /// <see cref="UiScale"/> подменяет DPI одному HWND, и второе окно рисовалось бы в системном
    /// масштабе, разъезжаясь с остальной программой.
    /// </remarks>
    public partial class MainWindow
    {
        private Action? _keyRemovalCommit;

        /// <summary>Идёт ли сейчас хоть один ход. Смена ключа на середине разорвала бы его.</summary>
        internal bool HasRunningTurns => AnyTurnRunning;

        private void NavKey_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            KeyPage.Attach(_services);
            KeyPage.Activate();
        }

        /// <summary>
        /// Сбрасывает всё, что принадлежало прежнему ключу.
        /// </summary>
        /// <remarks>
        /// Остаток на плашке — деньги того ключа, которым платили; список моделей — те, к
        /// которым тот ключ пускал. Оставить их значило бы показать человеку чужие цифры и
        /// модели, на которых он немедленно получит отказ.
        /// </remarks>
        internal void OnActiveKeyChanged()
        {
            _balance?.Forget();
            _services?.Models.Invalidate();
            Detached.Run(LoadModelCatalogAsync(), "reload_models_after_key_change");
        }

        // ───────────────────────── добавление ключа ─────────────────────────

        internal void OpenKeyDialog()
        {
            KeyDialogLabel.Text = "";
            KeyDialogValue.Text = "";
            KeyDialogError.Visibility = Visibility.Collapsed;
            SetKeyDialogBusy(false);
            KeyOverlay.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(
                new Action(() => KeyDialogValue.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void KeyDialogCancelButton_Click(object sender, RoutedEventArgs e) =>
            KeyOverlay.Visibility = Visibility.Collapsed;

        private void KeyDialogValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                KeyDialogSaveButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                KeyOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void KeyDialogSaveButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(SaveKeyAsync(), "add_venice_key");

        /// <summary>
        /// Проверяет ключ у Venice и только потом сохраняет.
        /// </summary>
        /// <remarks>
        /// Опечатку в ключе иначе человек обнаружил бы не здесь, а посреди следующего ответа —
        /// отказом, который читается как сетевой сбой.
        /// </remarks>
        private async Task SaveKeyAsync()
        {
            if (_services is null)
            {
                return;
            }

            var secret = (KeyDialogValue.Text ?? "").Trim();
            if (secret.Length == 0)
            {
                ShowKeyDialogError(Loc.Get("S.Key.Empty"));
                return;
            }

            if (_services.KeyStore.Contains(secret))
            {
                ShowKeyDialogError(Loc.Get("S.Key.Duplicate"));
                return;
            }

            SetKeyDialogBusy(true);
            try
            {
                await _services.Venice
                    .GetRateLimitsAsync(secret, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
            {
                SetKeyDialogBusy(false);
                ShowKeyDialogError(Loc.Get("S.Key.Invalid"));
                return;
            }

            SetKeyDialogBusy(false);

            if (_services.KeyStore.Add(KeyDialogLabel.Text ?? "", secret) is null)
            {
                ShowKeyDialogError(Loc.Get("S.Key.Invalid"));
                return;
            }

            _services.ApplyActiveKey();
            OnActiveKeyChanged();
            KeyOverlay.Visibility = Visibility.Collapsed;
            KeyPage.OnKeyAdded();
        }

        private void ShowKeyDialogError(string text)
        {
            KeyDialogError.Text = text;
            KeyDialogError.Visibility = Visibility.Visible;
        }

        private void SetKeyDialogBusy(bool busy)
        {
            KeyDialogSaveButton.IsEnabled = !busy;
            KeyDialogBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────────────────── удаление ключа ─────────────────────────

        /// <summary>
        /// Спрашивает подтверждение на удаление ключа. Системный <c>MessageBox</c> здесь не
        /// годится: он рисуется средствами Windows и рядом с настройками выглядел бы чужим.
        /// </summary>
        internal void AskKeyRemoval(string label, Action commit)
        {
            _keyRemovalCommit = commit;
            KeyRemoveTitle.Text = Loc.Format("S.Key.DeleteConfirm", label);
            KeyRemoveText.Text = Loc.Get("S.Key.DeleteDesc");
            KeyRemoveOverlay.Visibility = Visibility.Visible;
        }

        private void KeyRemoveNoButton_Click(object sender, RoutedEventArgs e)
        {
            KeyRemoveOverlay.Visibility = Visibility.Collapsed;
            _keyRemovalCommit = null;
        }

        private void KeyRemoveYesButton_Click(object sender, RoutedEventArgs e)
        {
            var commit = _keyRemovalCommit;
            KeyRemoveOverlay.Visibility = Visibility.Collapsed;
            _keyRemovalCommit = null;
            commit?.Invoke();
        }
    }
}
