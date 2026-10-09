using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Экспорт и импорт всех данных программы одним архивом.
    /// </summary>
    /// <remarks>
    /// До этого вынести из программы можно было только один чат. Всё остальное — настройки,
    /// профили, аватар и фон, добавленные переводы — жило в <c>%APPDATA%</c> и при переустановке
    /// Windows пропадало молча, хотя кнопка «Удалить все данные» на этой же странице уже была.
    ///
    /// Сама работа с архивом лежит в Core (<see cref="DataBundleExporter"/>,
    /// <see cref="DataBundleImporter"/>); здесь только два окна и перечитывание интерфейса после
    /// импорта.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly List<(DataCategory Category, CheckBox Box)> _exportRows = [];
        private readonly List<(DataCategory Category, CheckBox Box)> _importRows = [];

        /// <summary>Подписи категорий импорта: в слиянии часть из них меняет текст.</summary>
        private readonly Dictionary<DataCategory, TextBlock> _importHints = [];

        private DataBundlePlan _exportPlan = DataBundlePlan.Empty;
        private DataBundleInspection? _importReport;
        private string? _importArchive;

        /// <summary>Идёт сборка или раскладка. Второе нажатие в это время сломало бы обе.</summary>
        private bool _bundleBusy;

        /// <summary>
        /// Расшифрованная копия архива под паролем — во временной папке, пока открыто окно
        /// импорта. Открытый текст на диске держится ровно столько, сколько нужен.
        /// </summary>
        private string? _importPlainCopy;

        private string ActiveProfileId => _services?.ProfileRegistry.ActiveProfileId ?? "";

        // ───────────────────────── экспорт ─────────────────────────

        private void DataExportButton_Click(object sender, RoutedEventArgs e) => Detached.Run(OpenExportAsync(), "open_export");

        private async Task OpenExportAsync()
        {
            if (_services is null || _bundleBusy)
            {
                return;
            }

            ExportErrorText.Visibility = Visibility.Collapsed;
            ExportCategoryList.Children.Clear();
            _exportRows.Clear();
            ExportProtectToggle.IsChecked = false;
            ClearExportPasswords();
            ExportSummaryText.Text = Loc.Get("S.Data.Usage.Counting");
            DataExportSaveButton.IsEnabled = false;
            ShowOverlay(DataExportOverlay);

            // Ход по диску считает и вложения внутри файлов чатов — на большой истории это
            // заметное время, а окно должно открыться сразу.
            var root = AppPaths.Root;
            var profileId = ActiveProfileId;
            var plan = await Task.Run(() => new DataBundleExporter(root, profileId).Plan(DataCategory.All));

            if (DataExportOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            _exportPlan = plan;
            foreach (var info in plan.ByCategory)
            {
                var box = BuildCategoryRow(
                    info.Category,
                    Loc.Get(DataBundle.DescriptionKeyOf(info.Category)),
                    AttachmentTypes.FormatSize(info.Bytes),
                    Loc.Format("S.Data.Usage.Files", info.Items),
                    out _);

                box.Checked += ExportCategory_Changed;
                box.Unchecked += ExportCategory_Changed;
                ExportCategoryList.Children.Add(box);
                _exportRows.Add((info.Category, box));
            }

            UpdateExportSummary();
        }

        private void ExportCategory_Changed(object sender, RoutedEventArgs e) => UpdateExportSummary();

        private void UpdateExportSummary()
        {
            if (_exportRows.Count == 0)
            {
                ExportSummaryText.Text = Loc.Get("S.Bundle.Export.Empty");
                DataExportSaveButton.IsEnabled = false;
                return;
            }

            var chosen = Chosen(_exportRows);
            if (chosen == DataCategory.None)
            {
                ExportSummaryText.Text = Loc.Get("S.Bundle.NothingChosen");
                DataExportSaveButton.IsEnabled = false;
                return;
            }

            var picked = _exportPlan.ByCategory.Where(info => chosen.HasFlag(info.Category)).ToList();
            ExportSummaryText.Text = Loc.Format(
                "S.Bundle.Export.Summary",
                AttachmentTypes.FormatSize(picked.Sum(info => info.Bytes)),
                picked.Sum(info => info.Items));
            DataExportSaveButton.IsEnabled = true;
        }

        private void DataExportSaveButton_Click(object sender, RoutedEventArgs e) => Detached.Run(SaveExportAsync(), "save_export");

        private void ExportProtect_Changed(object sender, RoutedEventArgs e)
        {
            var on = ExportProtectToggle.IsChecked == true;
            ExportPasswordPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ExportErrorText.Visibility = Visibility.Collapsed;
            if (on)
            {
                Dispatcher.BeginInvoke(new Action(() => ExportPasswordBox.Focus()), DispatcherPriority.Input);
            }
            else
            {
                ClearExportPasswords();
            }
        }

        private void ClearExportPasswords()
        {
            ExportPasswordBox.Clear();
            ExportPasswordRepeatBox.Clear();
        }

        /// <summary>
        /// Пароль на архив, если его просили: null — архив без пароля; пустая строка — просили,
        /// но ввели не так (ошибка уже на экране).
        /// </summary>
        private string? ExportPassword()
        {
            if (ExportProtectToggle.IsChecked != true)
            {
                return null;
            }

            var password = ExportPasswordBox.Password;
            var error = password.Length == 0
                ? "S.Bundle.Export.PasswordEmpty"
                : password != ExportPasswordRepeatBox.Password
                    ? "S.Bundle.Export.PasswordMismatch"
                    : null;
            if (error is null)
            {
                return password;
            }

            ExportErrorText.Text = Loc.Get(error);
            ExportErrorText.Visibility = Visibility.Visible;
            return "";
        }

        private async Task SaveExportAsync()
        {
            if (_services is null || _bundleBusy)
            {
                return;
            }

            var chosen = Chosen(_exportRows);
            if (chosen == DataCategory.None)
            {
                return;
            }

            // Пароль проверяется до выбора файла: сначала спросить, куда сохранить, а потом
            // сказать «пароли не совпадают» — лишний круг через системное окно.
            var password = ExportPassword();
            if (password is { Length: 0 })
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("S.Bundle.Export.DialogTitle"),

                // Фильтр собирается из кусков: перевод целиком сломал бы разметку «имя|маска».
                Filter = $"{Loc.Get("S.Bundle.FileKind")}|*{DataBundle.FileExtension}|" +
                         $"{Loc.Get("S.Common.AllFiles")}|*.*",
                DefaultExt = DataBundle.FileExtension,
                FileName = DataBundle.SuggestedFileName(DateTime.Now)
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var path = dialog.FileName;
            var root = AppPaths.Root;
            var profileId = ActiveProfileId;

            _bundleBusy = true;
            DataExportSaveButton.IsEnabled = false;
            ExportErrorText.Visibility = Visibility.Collapsed;
            ExportSummaryText.Text = Loc.Get("S.Bundle.Export.Working");

            try
            {
                // Экспорт читает чаты прямо с диска, а хранилище пишет их в фоне: без этого
                // в архив попала бы переписка без последнего ответа.
                _services.ChatStore.Flush();
                await Task.Run(() => WriteExport(root, profileId, path, chosen, password));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           System.Security.Cryptography.CryptographicException)
            {
                // Человеку — что делать дальше, а не текст исключения.
                ExportErrorText.Text = Loc.Get("S.Bundle.Export.Failed");
                ExportErrorText.Visibility = Visibility.Visible;
                UpdateExportSummary();
                return;
            }
            finally
            {
                _bundleBusy = false;
            }

            ClearExportPasswords();
            CloseOverlay(DataExportOverlay);
            if (await ShowNoticeAsync(Loc.Get("S.Bundle.Export.DoneTitle"), path, Loc.Get("S.Export.OpenFolder"), Loc.Get("S.Common.Close"), NoticeTone.Info))
            {
                AttachmentOpener.RevealInExplorer(path);
            }
        }

        /// <summary>
        /// Пишет архив; с паролем — сначала обычный во временную папку, затем конверт на место.
        /// </summary>
        /// <remarks>
        /// Открытая копия ложится во временную папку, а не рядом с целью: цель бывает папкой
        /// облачного диска, и синхронизация успела бы унести открытый архив раньше, чем он
        /// будет удалён.
        /// </remarks>
        private static void WriteExport(string root, string profileId, string path, DataCategory chosen, string? password)
        {
            if (password is null)
            {
                new DataBundleExporter(root, profileId).Write(path, chosen);
                return;
            }

            var plain = Path.Combine(Path.GetTempPath(), "amarin-export-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                new DataBundleExporter(root, profileId).Write(plain, chosen);
                PasswordEnvelope.SealFile(plain, path, password);
            }
            finally
            {
                TryDeleteFile(plain);
            }
        }

        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временный файл во временной папке — Windows уберёт его сама.
            }
        }

        private void DataExportCancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_bundleBusy)
            {
                ClearExportPasswords();
                CloseOverlay(DataExportOverlay);
            }
        }

        private void DataExport_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DataExportCancelButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && DataExportSaveButton.IsEnabled)
            {
                Detached.Run(SaveExportAsync(), "save_export");
                e.Handled = true;
            }
        }

        // ───────────────────────── импорт ─────────────────────────

        private void DataImportButton_Click(object sender, RoutedEventArgs e) => Detached.Run(OpenImportAsync(), "open_import");

        private async Task OpenImportAsync()
        {
            if (_services is null || _bundleBusy)
            {
                return;
            }

            // Тот же мотив, что и у смены профиля: идущий ход допишет свой чат уже поверх
            // только что импортированных.
            if (AnyTurnRunning)
            {
                Inform(Loc.Get("S.Bundle.Import.BusyTitle"), Loc.Get("S.Bundle.Import.Busy"));
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("S.Bundle.Import.DialogTitle"),

                // .zip в фильтре намеренно: файлы переименовывают, а «не вижу своего файла» —
                // худшая из ошибок.
                Filter = $"{Loc.Get("S.Bundle.FileKind")}|*{DataBundle.FileExtension};*.zip|" +
                         $"{Loc.Get("S.Common.AllFiles")}|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            ForgetImportPlainCopy();
            _importArchive = dialog.FileName;

            // Архив под паролем открывается во временную копию раньше всего остального: опись
            // лежит внутри шифра.
            if (PasswordEnvelope.IsSealed(_importArchive))
            {
                if (await OpenSealedArchiveAsync(_importArchive) is not { } plain)
                {
                    _importArchive = null;
                    return;
                }

                _importPlainCopy = plain;
                _importArchive = plain;
            }

            ResetImportDialog();
            ShowOverlay(DataImportOverlay);

            var root = AppPaths.Root;
            var report = await Task.Run(() => new DataBundleImporter(root).Inspect(_importArchive));

            if (DataImportOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            _importReport = report;
            if (!report.Ok)
            {
                ShowImportError(DataBundle.ErrorKey(report.Error));
                return;
            }

            ImportMetaText.Text = Loc.Format(
                "S.Bundle.Import.Meta",
                ChatFormat.DateTimeShort(report.CreatedAt, ActiveDateFormat),
                report.AppVersion,
                AttachmentTypes.FormatSize(report.TotalBytes));

            foreach (var info in report.Categories)
            {
                var box = BuildCategoryRow(
                    info.Category,
                    Loc.Get(DataBundle.DescriptionKeyOf(info.Category)),
                    CountLabel(info),
                    "",
                    out var hint);

                ImportCategoryList.Children.Add(box);
                _importRows.Add((info.Category, box));
                _importHints[info.Category] = hint;
            }

            UpdateImportHints();
            DataImportApplyButton.IsEnabled = true;
        }

        /// <summary>Человеческое число в строке категории: чатов, профилей, языков.</summary>
        private static string CountLabel(DataBundleCategoryInfo info) => info.Category switch
        {
            DataCategory.Chats => Loc.Format("S.Bundle.Import.ChatsCount", info.Items),
            DataCategory.Profiles => Loc.Format("S.Bundle.Import.ProfilesCount", info.Items),
            DataCategory.Languages => Loc.Format("S.Bundle.Import.LanguagesCount", info.Items),
            _ => AttachmentTypes.FormatSize(info.Bytes)
        };

        private void ImportModeChoice_Checked(object sender, RoutedEventArgs e) => UpdateImportHints();

        /// <summary>
        /// В слиянии настройки и оформление активного профиля не меняются вовсе, и строка обязана
        /// сказать об этом прямо — иначе отмеченная галочка выглядит как обещание, которого
        /// импорт не выполнит.
        /// </summary>
        private void UpdateImportHints()
        {
            if (_importHints.Count == 0)
            {
                return;
            }

            var merging = ImportMergeChoice.IsChecked == true;
            foreach (var (category, hint) in _importHints)
            {
                var kept = merging && category is DataCategory.Settings or DataCategory.Appearance;
                hint.Text = kept
                    ? Loc.Get("S.Bundle.Import.SettingsKeptHint")
                    : Loc.Get(DataBundle.DescriptionKeyOf(category));
            }
        }

        private void DataImportApplyButton_Click(object sender, RoutedEventArgs e) => Detached.Run(ApplyImportAsync(), "apply_import");

        private async Task ApplyImportAsync()
        {
            if (_services is null || _bundleBusy || _importArchive is null || _importReport?.Ok != true)
            {
                return;
            }

            var chosen = Chosen(_importRows);
            if (chosen == DataCategory.None)
            {
                ShowImportError("S.Bundle.NothingChosen");
                return;
            }

            if (AnyTurnRunning)
            {
                ShowImportError("S.Bundle.Import.Busy");
                return;
            }

            var mode = ImportReplaceChoice.IsChecked == true ? DataImportMode.Replace : DataImportMode.Merge;
            var archive = _importArchive;
            var root = AppPaths.Root;
            var profileId = ActiveProfileId;

            // Открытый чат ещё не на диске, а замена вот-вот снесёт папку чатов.
            PersistCurrent();

            _bundleBusy = true;
            DataImportApplyButton.IsEnabled = false;
            DataImportCancelButton.IsEnabled = false;

            // Пока идёт раскладка, выбор остаётся на экране, но не принимает нажатий: убрать его
            // насовсем можно только после успеха — на отказе человек вернётся к тем же галочкам.
            ImportChoicePanel.IsEnabled = false;
            ImportErrorText.Visibility = Visibility.Collapsed;
            ImportDoneText.Text = Loc.Get("S.Bundle.Import.Working");
            ImportDoneText.Visibility = Visibility.Visible;

            DataImportResult result;
            try
            {
                // Раскладка подменяет файлы чатов мимо хранилища. Отложенная запись, застигнутая
                // ею врасплох, легла бы поверх только что импортированного.
                _services.ChatStore.Flush();
                result = await Task.Run(() =>
                    new DataBundleImporter(root, profileId).Apply(archive, chosen, mode));
            }
            finally
            {
                _bundleBusy = false;
                DataImportCancelButton.IsEnabled = true;
                ImportChoicePanel.IsEnabled = true;
            }

            if (!result.Ok)
            {
                ImportDoneText.Visibility = Visibility.Collapsed;
                DataImportApplyButton.IsEnabled = true;
                ShowImportError(DataBundle.ErrorKey(result.Error));
                return;
            }

            ReloadAfterImport();
            ShowImportDone(Describe(result));

            // Раскладка кончилась, кнопка «Импорт» спрятана — расшифрованная копия больше не нужна.
            ForgetImportPlainCopy();
        }

        /// <summary>Итог одной строкой: «Готово. Перенесено: чатов 42, профилей 2».</summary>
        private static string Describe(DataImportResult result)
        {
            if (!result.ChangedAnything)
            {
                return Loc.Get("S.Bundle.Import.DoneNothing");
            }

            var parts = new List<string>();
            var chats = result.ChatsAdded + result.ChatsReplaced;
            if (chats > 0)
            {
                parts.Add(Loc.Format("S.Bundle.Import.ChatsCount", chats));
            }

            if (result.ProfilesAdded > 0)
            {
                parts.Add(Loc.Format("S.Bundle.Import.ProfilesCount", result.ProfilesAdded));
            }

            if (result.LanguagesAdded > 0)
            {
                parts.Add(Loc.Format("S.Bundle.Import.LanguagesCount", result.LanguagesAdded));
            }

            var text = parts.Count > 0
                ? Loc.Format("S.Bundle.Import.Done", string.Join(", ", parts))
                : Loc.Format("S.Bundle.Import.Done", Loc.Get("S.Bundle.Cat.Settings"));

            if (result.Skipped > 0)
            {
                text += Environment.NewLine + Loc.Format("S.Bundle.Import.Skipped", result.Skipped);
            }

            if (result.ProfilesAdded > 0)
            {
                text += Environment.NewLine + Loc.Get("S.Bundle.Import.ProfilesNote");
            }

            return text;
        }

        /// <summary>
        /// Перечитывает всё, что импорт мог поменять под работающим окном.
        /// </summary>
        /// <remarks>
        /// Перезапуск не нужен: тем же набором вызовов обходится смена профиля, где корень чатов
        /// и настроек тоже уезжает целиком.
        /// </remarks>
        private void ReloadAfterImport()
        {
            if (_services is null)
            {
                return;
            }

            _services.ProfileRegistry = _services.Profiles.Load();
            _services.UseProfile(ProfileStore.DataRootFor(_services.ProfileRegistry.ActiveProfileId));
            OnDeferredProfileChanged();
            StartTextIndexBuild();

            // Кэш держит прежний фон объектом, и без сброса на экране остался бы старый.
            AppearanceImageCache.Clear();
            ClearPendingAttachments();

            // Открытый чат мог быть удалён режимом «Заменить».
            StartNewSession(persist: false);
            ResetChatListView();
            RefreshChatList();
            LoadSettingsUi();
            BuiltPage<SettingsInstructionsPage>()?.ResetForProfile();

            // Страница «Общие» только выделяет язык в списке; применяет его LanguageManager.
            // force: архив мог принести другие строки под тем же кодом языка.
            LanguageManager.Apply(_services.Settings.LanguageCode, force: true);
            BuiltPage<SettingsGeneralPage>()?.LanguagePicker.Rebuild();
            ApplyAppearance(save: false);
            RefreshProfileList();
            Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");
        }

        private void DataImportCancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_bundleBusy)
            {
                return;
            }

            CloseOverlay(DataImportOverlay);
            _importArchive = null;
            _importReport = null;
            ForgetImportPlainCopy();
        }

        /// <summary>Удаляет расшифрованную копию архива, если она есть.</summary>
        private void ForgetImportPlainCopy()
        {
            TryDeleteFile(_importPlainCopy);
            _importPlainCopy = null;
        }

        /// <summary>
        /// Спрашивает пароль и расшифровывает архив во временную копию. Неверный пароль —
        /// спросить снова; испорченный файл — сказать об этом и не продолжать.
        /// </summary>
        /// <returns>Путь к открытой копии; null — человек передумал или файл не открыть.</returns>
        private async Task<string?> OpenSealedArchiveAsync(string sealedPath)
        {
            string? problem = null;
            while (true)
            {
                var input = new PasswordBox
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 13
                };
                input.SetResourceReference(ForegroundProperty, "Text.Bright");
                input.SetResourceReference(PasswordBox.CaretBrushProperty, "Text.Bright");
                var frame = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 7, 10, 7),
                    Child = input
                };
                frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
                frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

                var answer = ShowNoticeAsync(
                    Loc.Get("S.Bundle.Import.PasswordTitle"),
                    problem ?? Loc.Get("S.Bundle.Import.PasswordText"),
                    Loc.Get("S.Bundle.Import.Open"),
                    Loc.Get("S.Common.Cancel"),
                    problem is null ? NoticeTone.Info : NoticeTone.Warning,
                    frame);
                input.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        CloseNotice(true);
                    }
                };
                _ = Dispatcher.BeginInvoke(new Action(() => input.Focus()), DispatcherPriority.Input);

                if (!await answer)
                {
                    return null;
                }

                var password = input.Password;
                var plain = Path.Combine(Path.GetTempPath(), "amarin-import-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    await Task.Run(() => PasswordEnvelope.OpenFile(sealedPath, plain, password));
                    return plain;
                }
                catch (EnvelopeException ex) when (ex.Failure == EnvelopeFailure.WrongPassword)
                {
                    problem = Loc.Get("S.Bundle.Import.PasswordWrong");
                }
                catch (Exception ex) when (ex is EnvelopeException or IOException or UnauthorizedAccessException)
                {
                    await ShowNoticeAsync(
                        Loc.Get("S.Bundle.Import.DamagedTitle"),
                        Loc.Get(ex is EnvelopeException ? "S.Bundle.Import.Damaged" : "S.Bundle.Import.Unreadable"),
                        Loc.Get("S.Common.Close"),
                        secondary: null);
                    return null;
                }
            }
        }

        private void DataImport_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DataImportCancelButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && DataImportApplyButton.IsEnabled)
            {
                Detached.Run(ApplyImportAsync(), "apply_import");
                e.Handled = true;
            }
        }

        private void ResetImportDialog()
        {
            _importRows.Clear();
            _importHints.Clear();
            _importReport = null;
            ImportCategoryList.Children.Clear();
            ImportMetaText.Text = Loc.Get("S.Data.Usage.Counting");
            ImportChoicePanel.Visibility = Visibility.Visible;
            ImportChoicePanel.IsEnabled = true;
            ImportDoneText.Visibility = Visibility.Collapsed;
            ImportErrorText.Visibility = Visibility.Collapsed;

            // Прошлый импорт мог закончиться экраном «готово», где кнопка «Импортировать» убрана,
            // а «Отмена» переименована в «Закрыть». Открытое во второй раз окно обязано выглядеть
            // как в первый.
            DataImportApplyButton.Visibility = Visibility.Visible;
            DataImportApplyButton.IsEnabled = false;
            DataImportCancelButton.IsEnabled = true;
            DataImportCancelButton.SetResourceReference(ContentControl.ContentProperty, "S.Common.Cancel");
        }

        private void ShowImportError(string key)
        {
            ImportErrorText.Text = Loc.Get(key);
            ImportErrorText.Visibility = Visibility.Visible;
        }

        /// <summary>Окно превращается в экран «готово»: выбор уже применён, повторять нечего.</summary>
        private void ShowImportDone(string text)
        {
            ImportChoicePanel.Visibility = Visibility.Collapsed;
            ImportDoneText.Text = text;
            ImportDoneText.Visibility = Visibility.Visible;
            DataImportApplyButton.Visibility = Visibility.Collapsed;
            DataImportCancelButton.SetResourceReference(ContentControl.ContentProperty, "S.Common.Close");
        }

        // ───────────────────────── общее ─────────────────────────

        /// <summary>
        /// Строка категории с галочкой.
        /// </summary>
        /// <param name="hint">
        /// Подпись под названием: окно импорта переписывает её при смене режима.
        /// </param>
        private static CheckBox BuildCategoryRow(
            DataCategory category,
            string description,
            string right,
            string under,
            out TextBlock hint)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = Label(Loc.Get(DataBundle.LabelKeyOf(category)), 12.5, "Text.Body");
            hint = Label(description, 11.5, "Text.Muted");
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 2, 0, 0);

            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            left.Children.Add(title);
            left.Children.Add(hint);

            var numbers = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var size = Label(right, 12, "Text.Secondary");
            size.HorizontalAlignment = HorizontalAlignment.Right;
            numbers.Children.Add(size);

            if (!string.IsNullOrEmpty(under))
            {
                var files = Label(under, 11, "Text.Faint");
                files.HorizontalAlignment = HorizontalAlignment.Right;
                files.Margin = new Thickness(0, 1, 0, 0);
                numbers.Children.Add(files);
            }

            Grid.SetColumn(numbers, 1);
            grid.Children.Add(left);
            grid.Children.Add(numbers);

            var box = new CheckBox { IsChecked = true, Content = grid };

            // SetResourceReference, а не Style=: стиль, присвоенный объектом, переживёт смену
            // темы, но перестанет на неё отзываться.
            box.SetResourceReference(FrameworkElement.StyleProperty, "DialogCheckBox");
            return box;
        }

        private static TextBlock Label(string text, double fontSize, string brushKey)
        {
            var block = new TextBlock { Text = text, FontSize = fontSize };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }

        private static DataCategory Chosen(List<(DataCategory Category, CheckBox Box)> rows)
        {
            var chosen = DataCategory.None;
            foreach (var (category, box) in rows)
            {
                if (box.IsChecked == true)
                {
                    chosen |= category;
                }
            }

            return chosen;
        }

        private void ShowOverlay(UIElement overlay)
        {
            overlay.Visibility = Visibility.Visible;

            // «Модальность» здесь руками: чат под затемнением не должен ловить мышь.
            ChatBlocked = true;
            Dispatcher.BeginInvoke(() => overlay.Focus(), DispatcherPriority.Input);
        }

        private void CloseOverlay(UIElement overlay)
        {
            overlay.Visibility = Visibility.Collapsed;
            ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible;
        }
    }
}
