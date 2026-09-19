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

        /// <summary>Что сделать с набранным названием по «Сохранить».</summary>
        private Action<string>? _keyRenameCommit;

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
        /// Сбрасывает всё, что принадлежало прежнему набору ключей.
        /// </summary>
        /// <remarks>
        /// Остаток на плашке — деньги того ключа, которым платили; список моделей — те, к
        /// которым ключ пускал. Оставить их значило бы показать человеку чужие цифры и модели,
        /// на которых он немедленно получит отказ.
        /// <para>
        /// Каталоги гасятся только у провайдера, которого смена касается: с версии 1.23.0 их
        /// несколько, и добавленный ключ OpenRouter не повод перечитывать каталог Venice —
        /// от чужого ключа он не меняется.
        /// </para>
        /// </remarks>
        /// <param name="provider">
        /// Провайдер добавленного, удалённого или выбранного ключа. <c>null</c> — задет весь
        /// набор сразу, например при смене профиля.
        /// </param>
        internal void OnActiveKeyChanged(LlmProvider? provider = null)
        {
            // Ключи сменились — сумма считается по новому списку, а у новых ключей цифру
            // ещё предстоит спросить.
            _balance?.Refresh();
            Detached.Run(RefreshStaleBalancesAsync(), "balances");
            _services?.Models.Invalidate(provider);

            // Каталог погашен — значит его снова надо разложить по плашкам и перелечить слоты.
            if (provider is { } one)
            {
                _appliedCatalogs.Remove(one);
            }
            else
            {
                _appliedCatalogs.Clear();
            }

            PushKeysToPickers();
            Detached.Run(LoadModelCatalogAsync(), "reload_models_after_key_change");
        }

        /// <summary>
        /// Человек выбрал другой ключ кружком на странице «Key &amp; Info».
        /// </summary>
        /// <remarks>
        /// Каталоги здесь не гасятся и слоты не лечатся намеренно: с версии 1.23.0 выбранный
        /// ключ не решает, какими моделями работает программа, — у каждого слота свой ключ.
        /// Он решает лишь, чей остаток стоит на плашке и чьи траты показывает график, поэтому
        /// сбрасывается ровно остаток.
        /// </remarks>
        internal void OnSelectedKeyChanged()
        {
            // Остаток больше не принадлежит выбранному ключу: плашка складывает все. Стирать
            // нечего — достаточно пересчитать сумму по новому списку.
            _balance?.Refresh();
            PushKeysToPickers();
        }

        /// <summary>
        /// Дозапрашивает остаток у ключей, чья цифра неизвестна или устарела.
        /// </summary>
        /// <remarks>
        /// Venice называет остаток даром, на заголовках ответа, а OpenRouter — только на прямой
        /// вопрос: без этого прохода его ключи не попадали бы в сумму вовсе, и плашка на них
        /// стояла бы пустой. Последовательно и с порогом внутри книги: у человека с тремя
        /// ключами иначе выходило бы три лишних запроса на каждое сообщение.
        /// </remarks>
        private async Task RefreshStaleBalancesAsync()
        {
            if (_services is null)
            {
                return;
            }

            foreach (var entry in _services.Balances.Stale(_services.KeyStore.List()))
            {
                try
                {
                    await _services.Venice
                        .GetRateLimitsAsync(entry.Credential, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    exception is VeniceApiException or HttpRequestException)
                {
                    // Не ответил — цифра этого ключа просто останется прежней. Плашка скажет,
                    // что сумма неполная, а ход из-за остатка ломать незачем.
                }
            }
        }

        // ───────────────────────── добавление ключа ─────────────────────────

        /// <summary>Провайдер, выбранный в диалоге. Venice — как было до появления второго.</summary>
        private LlmProvider KeyDialogProvider =>
            KeyDialogOpenRouter.IsChecked == true ? LlmProvider.OpenRouter : LlmProvider.Venice;

        /// <summary>
        /// Подсказка о том, где взять ключ, идёт за выбором провайдера: отправлять человека
        /// на venice.ai за ключом OpenRouter значило бы врать ему прямо в диалоге.
        /// </summary>
        private void KeyDialogProvider_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            KeyDialogHint.SetResourceReference(
                TextBlock.TextProperty,
                KeyDialogProvider == LlmProvider.OpenRouter
                    ? "S.Key.AddHint.OpenRouter"
                    : "S.Key.AddHint.Venice");
            KeyDialogError.Visibility = Visibility.Collapsed;
        }

        private void KeyDialogKeysLink_Click(object sender, RoutedEventArgs e) =>
            OpenExternalLink(ProviderSpec.For(KeyDialogProvider).KeysUrl);

        internal void OpenKeyDialog()
        {
            KeyDialogLabel.Text = "";
            KeyDialogValue.Text = "";
            KeyDialogVenice.IsChecked = true;
            KeyDialogProvider_Checked(KeyDialogVenice, new RoutedEventArgs());
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
        /// Проверяет ключ у провайдера и только потом сохраняет.
        /// </summary>
        /// <remarks>
        /// Опечатку в ключе иначе человек обнаружил бы не здесь, а посреди следующего ответа —
        /// отказом, который читается как сетевой сбой.
        /// <para>
        /// Пока идёт ход, добавлять нельзя: сохранённый ключ тут же становится выбранным, а
        /// слоты, которым ключ не назначен, берут именно его — половина раундов того же хода
        /// оплатилась бы одним ключом, половина другим, и в графике трат это уже не разделить.
        /// </para>
        /// </remarks>
        private async Task SaveKeyAsync()
        {
            if (_services is null)
            {
                return;
            }

            if (HasRunningTurns)
            {
                ShowKeyDialogError(Loc.Get("S.Key.BusyTurns"));
                return;
            }

            var secret = (KeyDialogValue.Text ?? "").Trim();
            if (secret.Length == 0)
            {
                ShowKeyDialogError(Loc.Get("S.Key.Empty"));
                return;
            }

            // Убранный ключ окружения программа тоже считает своим, но в списке его нет, и
            // «такой ключ уже есть» читалось бы как издёвка. Класть его копию на диск нельзя:
            // человек держал его снаружи программы сознательно.
            if (_services.KeyStore.IsRemovedEnvironmentKey(secret))
            {
                ShowKeyDialogError(Loc.Get("S.Key.DuplicateRemoved"));
                return;
            }

            if (_services.KeyStore.Contains(secret))
            {
                ShowKeyDialogError(Loc.Get("S.Key.Duplicate"));
                return;
            }

            var provider = KeyDialogProvider;
            SetKeyDialogBusy(true);
            try
            {
                await _services.Venice
                    .GetRateLimitsAsync(new ApiCredential(provider, secret), CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
            {
                SetKeyDialogBusy(false);
                ShowKeyDialogError(Loc.Get("S.Key.Invalid"));
                return;
            }

            SetKeyDialogBusy(false);

            if (_services.KeyStore.Add(KeyDialogLabel.Text ?? "", secret, provider) is null)
            {
                ShowKeyDialogError(Loc.Get("S.Key.Invalid"));
                return;
            }

            _services.ApplyActiveKey();
            OnActiveKeyChanged(provider);
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
        /// <param name="descriptionKey">
        /// Ключ пояснения под вопросом: у ключа из окружения удаление лишь перестаёт его брать,
        /// и обещать большее нельзя.
        /// </param>
        internal void AskKeyRemoval(string label, string descriptionKey, Action commit)
        {
            _keyRemovalCommit = commit;
            KeyRemoveTitle.Text = Loc.Format("S.Key.DeleteConfirm", label);
            KeyRemoveText.Text = Loc.Get(descriptionKey);
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

        // ───────────────────────── переименование ключа ─────────────────────────

        /// <summary>
        /// Спрашивает новое название ключа.
        /// </summary>
        /// <remarks>
        /// Своим оверлеем, а не полем прямо в строке списка: строки собираются кодом и
        /// пересобираются на каждое обновление статистики, и поле ввода теряло бы фокус вместе
        /// с набранным, когда придёт ответ сети.
        /// </remarks>
        internal void AskKeyRename(string label, Action<string> commit)
        {
            _keyRenameCommit = commit;
            KeyRenameValue.Text = label ?? "";
            KeyRenameValue.SelectAll();
            KeyRenameOverlay.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(
                new Action(() => KeyRenameValue.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void KeyRenameCancelButton_Click(object sender, RoutedEventArgs e)
        {
            KeyRenameOverlay.Visibility = Visibility.Collapsed;
            _keyRenameCommit = null;
        }

        private void KeyRenameSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var commit = _keyRenameCommit;
            var name = KeyRenameValue.Text ?? "";
            KeyRenameOverlay.Visibility = Visibility.Collapsed;
            _keyRenameCommit = null;
            commit?.Invoke(name);
        }

        private void KeyRenameValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                KeyRenameSaveButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                KeyRenameCancelButton_Click(sender, e);
            }
        }
    }
}
