using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Обновления, часть вторая (H4): бета-канал, заметки к релизу до установки, возврат к прошлой
    /// версии и «Что нового» после обновления.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Заполняет строки страницы, которые зависят от диска: есть ли прошлая версия.</summary>
        private void LoadReleaseExtrasUi()
        {
            if (_services is not null)
            {
                // Под флагом загрузки: страницу перечитывают и вне LoadSettingsUi (смена языка), а
                // сработавший обработчик забыл бы найденный релиз и пошёл бы в сеть.
                var loading = _settingsUiLoading;
                _settingsUiLoading = true;
                try
                {
                    BetaChannelToggle.IsChecked = _services.Settings.BetaChannel;
                }
                finally
                {
                    _settingsUiLoading = loading;
                }
            }

            ShowRollbackButton();
        }

        private void ShowRollbackButton()
        {
            var previous = UpdateInstaller.PreviousVersionPath(Environment.ProcessPath);
            if (previous is null || !OperatingSystem.IsWindows())
            {
                RollbackButton.Visibility = Visibility.Collapsed;
                return;
            }

            RollbackButton.Content = UpdateInstaller.FileVersionOf(previous) is { } version
                ? Loc.Format("S.Updates.Rollback", version)
                : Loc.Get("S.Updates.RollbackUnknown");
            RollbackButton.Visibility = Visibility.Visible;
        }

        private void BetaChannelToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.BetaChannel = BetaChannelToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);

            // Найденное по прежнему каналу автомат забывает целиком — и скачанное, и качающееся,
            // кем бы оно ни было начато, — и проверяет заново.
            Updates.ChangeChannel();
        }

        /// <summary>Заметки к релизу в окне подтверждения; нет заметок — блока нет.</summary>
        internal void ShowConfirmNotes(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                UpdateConfirmNotesPanel.Visibility = Visibility.Collapsed;
                UpdateConfirmNotes.Document = new System.Windows.Documents.FlowDocument();
                return;
            }

            ChatMarkdown.Write(UpdateConfirmNotes, this, notes, 12.5, 19, fillAvailableWidth: false);
            UpdateConfirmNotesPanel.Visibility = Visibility.Visible;
        }

        private void RollbackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || Environment.ProcessPath is not { } exe)
            {
                return;
            }

            if (AnyTurnRunning)
            {
                Updates.ShowNotice(UpdateNoticeKind.WaitForTurns);
                return;
            }

            // Обновление, которого ждёт человек, — не время для отката.
            if (Updates.State is { Download.Wanted: true } or { Installing: not null })
            {
                return;
            }

            Detached.Run(RollBackAsync(exe), "rollback_update");
        }

        /// <summary>
        /// Спрашивает и ставит прошлую версию. Версию, от которой ушли, автообновление больше
        /// само не ставит (<see cref="AppSettings.DeclinedUpdate"/>); если прошлая об этом поле не
        /// знает — автообновление выключается, иначе оно вернуло бы отвергнутое при закрытии.
        /// </summary>
        private async Task RollBackAsync(string exe)
        {
            if (_services is null || UpdateInstaller.PreviousVersionPath(exe) is not { } previousPath)
            {
                ShowRollbackButton();
                return;
            }

            var previous = UpdateInstaller.FileVersionOf(previousPath);
            var current = CurrentRelease;
            var elevate = !UpdateInstaller.CanSwapWithoutElevation(exe);
            var keepsAuto = UpdateInstaller.KnowsDeclinedUpdate(previous);

            var text = Loc.Format(
                "S.Updates.RollbackText",
                current,
                previous is { } known ? "v" + known : Loc.Get("S.Updates.PreviousUnknown"));
            if (!keepsAuto && _services.Settings.AutoCheckUpdates)
            {
                text += "\n\n" + Loc.Format("S.Updates.RollbackAutoOff", current);
            }

            if (elevate)
            {
                text += "\n\n" + Loc.Get("S.Updates.RollbackAdmin");
            }

            if (!await ShowNoticeAsync(
                    Loc.Get("S.Updates.RollbackTitle"),
                    text,
                    Loc.Get("S.Updates.RollbackConfirm"),
                    Loc.Get("S.Common.Cancel"),
                    NoticeTone.Warning))
            {
                return;
            }

            // Пока спрашивали, мог начаться ход — перезапуск оборвал бы его.
            if (AnyTurnRunning)
            {
                Updates.ShowNotice(UpdateNoticeKind.WaitForTurns);
                return;
            }

            RollbackButton.IsEnabled = false;
            try
            {
                // Вперёд (прошлая оказалась новее — после отката откатываются обратно) отказ
                // снимается: человек сам к ней вернулся.
                _services.Settings.DeclinedUpdate = previous is { } target && target > current ? null : current.ToString();
                if (!keepsAuto)
                {
                    _services.Settings.AutoCheckUpdates = false;
                }

                _services.SettingsStore.Save(_services.Settings);

                // Скачанное в фоне поставил бы выход — а выход здесь ради отката, не обновления.
                Updates.BeginRollback();
                PersistCurrent();

                var result = elevate
                    ? await ElevatedRun.RunAsync(exe, ["--rollback-update"])
                    : await Task.Run(() => UpdateInstaller.RollBack(exe));

                if (!result.Ok)
                {
                    Updates.FailRollback(result.Error ?? "");
                    return;
                }

                // Прошлая версия уже на месте: выход не должен принять её за недоведённое обновление.
                Updates.CompleteRollback();
                if (AppRelaunch.StartSuccessor(exe) is { } error)
                {
                    Updates.ReportRestartFailed(error);
                    return;
                }

                RequestExit();
            }
            finally
            {
                RollbackButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Раз в версию показывает её заметки. Только в настоящей программе: оконные тесты
        /// поднимают это же окно, и открытое уведомление перехватывало бы их клики.
        /// </summary>
        private void ScheduleWhatsNew()
        {
            if (!IsRealApp || _services is not { } services)
            {
                return;
            }

            var settings = services.Settings;
            var current = CurrentRelease;
            if (ReleaseVersion.Parse(settings.LastSeenVersion) is { } seen && seen == current)
            {
                return;
            }

            var usedBefore = settings.LastUpdateCheckUtc is not null ||
                             File.Exists(Path.Combine(services.ChatStore.Folder, "index.json"));
            var show = WhatsNew.ShouldShow(settings.LastSeenVersion, current, usedBefore);

            // Запоминаем сразу, а не по закрытию окна: упади программа на первом же кадре, при
            // следующем запуске заметки к той же версии полезли бы снова.
            settings.LastSeenVersion = current.ToString();
            services.SettingsStore.Save(settings);

            if (!show)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => Detached.Run(ShowWhatsNewAsync(current), "whats_new")), DispatcherPriority.ApplicationIdle);
        }

        private async Task ShowWhatsNewAsync(ReleaseVersion version)
        {
            // Читать ресурс — на рабочем потоке: заметки к большому выпуску — это десятки килобайт.
            var notes = await Task.Run(WhatsNew.Embedded);
            if (notes is null || Exit.Exiting)
            {
                return;
            }

            // Поверх чужого вопроса не встаём: новый вопрос снял бы прежний ответом «нет».
            if (NoticeOverlay.Visibility == Visibility.Visible)
            {
                return;
            }

            var box = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 0, 8, 0),
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ContextMenu = null
            };
            box.SetResourceReference(Control.BackgroundProperty, "Bg.Card");
            box.SetResourceReference(Control.ForegroundProperty, "Text.Secondary");
            box.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.Updates.NotesTitle");
            ChatMarkdown.Write(box, this, notes, 12.5, 19, fillAvailableWidth: false);

            var frame = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 2, 8),
                Child = box
            };
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

            await ShowNoticeAsync(
                Loc.Format("S.Updates.WhatsNewTitle", version),
                Loc.Get("S.Updates.WhatsNewText"),
                Loc.Get("S.Updates.WhatsNewOk"),
                null,
                NoticeTone.Info,
                frame);
        }
    }
}
