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
            KeyPage.ShowProblem ??= text => Detached.Run(
                ShowNoticeAsync(Loc.Get("S.Spend.ExportFailedTitle"), text, Loc.Get("S.Common.Close"), null),
                "spend_export_failed");
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
                catch (BalanceNotReportedException)
                {
                    // Провайдер остатка не называет — запоминаем, чтобы не спрашивать после
                    // каждого ответа и не держать ключ в «цифры пока нет».
                    _services.Balances.MarkSilent(entry.Credential);
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

        private KeyAddDialog? _keyDialog;

        /// <summary>Окно нового ключа; создаётся при первом открытии.</summary>
        internal KeyAddDialog KeyDialog
        {
            get
            {
                if (_keyDialog is null)
                {
                    _keyDialog = new KeyAddDialog();
                    _keyDialog.Submitted += (_, _) => Detached.Run(SaveKeyAsync(), "add_key");
                    _keyDialog.Cancelled += (_, _) => CloseKeyDialog();
                    _keyDialog.LinkRequested += (_, url) => OpenExternalLink(url);
                    KeyOverlay.Children.Add(_keyDialog);
                }

                return _keyDialog;
            }
        }

        /// <param name="provider">
        /// Провайдер, у которого человек нажал «Добавить ключ» в плашке выбора модели. <c>null</c> —
        /// окно открыто со страницы ключей, и провайдер угадается по ключу.
        /// </param>
        internal void OpenKeyDialog(LlmProvider? provider = null)
        {
            var dialog = KeyDialog;
            dialog.Reset(provider);
            KeyOverlay.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(
                new Action(dialog.FocusKey),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CloseKeyDialog() => KeyOverlay.Visibility = Visibility.Collapsed;

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
            if (_services is null || _keyDialog is not { } dialog)
            {
                return;
            }

            if (HasRunningTurns)
            {
                dialog.ShowError(Loc.Get("S.Key.BusyTurns"));
                return;
            }

            var secret = dialog.Secret;
            if (secret.Length == 0)
            {
                dialog.ShowError(Loc.Get("S.Key.Empty"));
                return;
            }

            // Убранный ключ окружения программа тоже считает своим, но в списке его нет, и
            // «такой ключ уже есть» читалось бы как издёвка. Класть его копию на диск нельзя:
            // человек держал его снаружи программы сознательно.
            if (_services.KeyStore.IsRemovedEnvironmentKey(secret))
            {
                dialog.ShowError(Loc.Get("S.Key.DuplicateRemoved"));
                return;
            }

            if (_services.KeyStore.Contains(secret))
            {
                dialog.ShowError(Loc.Get("S.Key.Duplicate"));
                return;
            }

            var record = dialog.Provider is { } provider
                ? await AddProviderKeyAsync(dialog, provider, secret).ConfigureAwait(true)
                : await AddServerKeyAsync(dialog, secret).ConfigureAwait(true);
            if (record is null)
            {
                return;
            }

            _services.ApplyActiveKey();
            OnActiveKeyChanged(record.Provider);
            CloseKeyDialog();
            BuiltPage<SettingsKeyPage>()?.OnKeyAdded();
        }

        /// <summary>Ключ встроенного провайдера: проверка тем запросом, который у провайдера есть.</summary>
        private async Task<ApiKeyRecord?> AddProviderKeyAsync(KeyAddDialog dialog, LlmProvider provider, string secret)
        {
            if (_services is null)
            {
                return null;
            }

            var spec = ProviderSpec.For(provider);
            dialog.SetBusy(true, Loc.Get("S.Key.Checking"));
            try
            {
                await _services.Venice
                    .CheckKeyAsync(new ApiCredential(provider, secret), CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is VeniceApiException or HttpRequestException or TaskCanceledException)
            {
                dialog.SetBusy(false, null);
                dialog.ShowError(KeyRefusal(exception, spec.Name));
                return null;
            }

            dialog.SetBusy(false, null);
            var record = _services.KeyStore.Add(dialog.KeyLabel, secret, provider);
            if (record is null)
            {
                dialog.ShowError(Loc.Get("S.Key.Invalid"));
            }

            return record;
        }

        /// <summary>
        /// Ключ своего сервера: проба находит корень API и протоколы, ни одной модели не запуская.
        /// </summary>
        private async Task<ApiKeyRecord?> AddServerKeyAsync(KeyAddDialog dialog, string secret)
        {
            if (_services is null)
            {
                return null;
            }

            if (EndpointAddress.Normalize(dialog.Address, out var problem) is not { } address)
            {
                dialog.ShowError(Loc.Get(problem switch
                {
                    "empty" => "S.Key.AddressEmpty",
                    "insecure" => "S.Key.AddressInsecure",
                    _ => "S.Key.AddressInvalid"
                }));
                return null;
            }

            dialog.SetBusy(true, Loc.Get("S.Key.CheckingServer"));
            var probe = await _services.Venice
                .ProbeServerAsync(address, secret, CancellationToken.None)
                .ConfigureAwait(true);
            dialog.SetBusy(false, null);

            if (probe.Endpoint is not { } endpoint)
            {
                dialog.ShowError(probe.Failure switch
                {
                    ServerProbeFailure.BadAddress => Loc.Get("S.Key.AddressInvalid"),
                    ServerProbeFailure.KeyRejected => Loc.Get("S.Key.Invalid"),
                    ServerProbeFailure.NotAnApi => Loc.Get("S.Key.NotAnApi"),
                    _ => Loc.Format("S.Key.Unreachable", EndpointAddress.HostOf(address))
                });
                return null;
            }

            var record = _services.KeyStore.AddCustom(dialog.KeyLabel, secret, endpoint);
            if (record is null)
            {
                dialog.ShowError(Loc.Get("S.Key.Invalid"));
            }

            return record;
        }

        /// <summary>
        /// Отказ проверки — коротко, чтобы строка уместилась в подвал. «Не принял ключ» — только на
        /// 401: на 403 OpenRouter отвечает и из закрытой страны, и отправлять человека искать
        /// опечатку в верном ключе нечестно.
        /// </summary>
        private static string KeyRefusal(Exception exception, string provider) => exception switch
        {
            VeniceApiException { StatusCode: 401 } => Loc.Get("S.Key.Invalid"),
            VeniceApiException { StatusCode: 403 } => Loc.Format("S.Key.Denied", provider, 403),
            VeniceApiException { StatusCode: 429 } => Loc.Format("S.Models.LoadBusy", provider),
            VeniceApiException { StatusCode: >= 500 and var code } => Loc.Format("S.Models.LoadServer", provider, code),
            VeniceApiException => Loc.Get("S.Key.Invalid"),
            _ => Loc.Format("S.Key.Unreachable", provider)
        };

        // ───────────────────────── лимит токенов ─────────────────────────

        /// <summary>
        /// Спрашивает лимит токенов ключа. Пустое поле — лимита нет.
        /// </summary>
        /// <remarks>
        /// Тем же окном с одним полем, что и названия: свой оверлей ради одного числа был бы
        /// копией того же окна.
        /// </remarks>
        internal void AskTokenLimit(long? current, Action<long?> commit) =>
            OpenNameDialog(
                Loc.Get("S.Key.TokenLimitTitle"),
                current is { } limit
                    ? limit.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ')
                    : "",
                text => commit(TokenCount.TryParseLimit(text, out var parsed) ? parsed : null),
                placeholder: Loc.Get("S.Key.TokenLimitPlaceholder"),
                hint: Loc.Get("S.Key.TokenLimitText"),
                validate: text => TokenCount.TryParseLimit(text, out _) ? null : Loc.Get("S.Key.TokenLimitInvalid"));

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
