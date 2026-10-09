using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Настройки в окне: оболочка и страницы, которые строятся после первого кадра, и подписка
    /// их контролов на обработчики окна.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разметка настроек живёт в <c>UI/Settings</c>: оболочка <see cref="SettingsView"/> и по
    /// UserControl на страницу, а в <c>MainWindow.xaml</c> от неё остался пустой хост
    /// <c>SettingsOverlay</c>. До выноса весь оверлей — 2300 строк разметки — разбирался в
    /// конструкторе окна, хотя на первом кадре его не видно. Теперь конструктор его не строит
    /// вовсе: после первого кадра очередь в простое (<see cref="ScheduleSettingsPrewarm"/>)
    /// собирает оболочку и по странице за порцию, а открытие раньше прогрева достраивает нужное
    /// сразу.
    /// </para>
    /// <para>
    /// Обработчики остались в тематических partial-файлах окна — переехала только разметка,
    /// поэтому события подписываются кодом, когда страница создана
    /// (<see cref="OnSettingsPageCreated"/>), а не атрибутами разметки: те искали бы методы у
    /// самой страницы. Там же страница один раз заполняется из настроек.
    /// </para>
    /// <para>
    /// К контролу код окна обращается через страницу (<c>GeneralPage.AutoScrollToggle</c>):
    /// свойство страницы создаёт её при первом обращении, так что обработчик никогда не видит
    /// пустоты. Код, которому писать в страницу не срочно (запуск, фоновые события, смена языка),
    /// берёт её через <see cref="BuiltPage{T}"/> и страницу не создаёт: созданная позже, она
    /// заполнится из настроек сама — правда лежит в <see cref="AppSettings"/>, а не в контролах.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        private SettingsView? _settingsView;

        /// <summary>
        /// Страницы, заполненные из прежних настроек: открытие настроек, смена профиля и импорт
        /// перечитывают <c>settings.json</c>, и такая страница освежается, когда её покажут.
        /// </summary>
        private readonly HashSet<FrameworkElement> _staleSettingsPages = [];

        /// <summary>Страницу создаёт прогрев: заполнение уходит в следующую порцию, а не в эту же.</summary>
        private bool _prewarmCreating;

        /// <summary>
        /// Тяжёлое содержимое подстраниц (карточки тем, строки сочетаний): своими порциями прогрева или
        /// при первом заходе на подстраницу — что раньше. Со страницей одной порцией оно держало
        /// поток окна дольше кадра.
        /// </summary>
        private readonly Queue<Action> _settingsDeferred = new();

        /// <summary>Оболочка настроек; создаётся при первом обращении.</summary>
        internal SettingsView SettingsUi => _settingsView ?? CreateSettingsView();

        private SettingsGeneralPage GeneralPage => SettingsUi.Page<SettingsGeneralPage>();

        private SettingsAppearancePage AppearancePage => SettingsUi.Page<SettingsAppearancePage>();

        private SettingsProfilePage ProfilePage => SettingsUi.Page<SettingsProfilePage>();

        private SettingsModelsPage ModelsPage => SettingsUi.Page<SettingsModelsPage>();

        private SettingsPromptsPage PromptsPage => SettingsUi.Page<SettingsPromptsPage>();

        private SettingsInstructionsPage InstructionsPage => SettingsUi.Page<SettingsInstructionsPage>();

        private SettingsAutomationPage AutomationPage => SettingsUi.Page<SettingsAutomationPage>();

        private SettingsSecurityPage SecurityPage => SettingsUi.Page<SettingsSecurityPage>();

        private SettingsKeyPage KeyPage => SettingsUi.Page<SettingsKeyPage>();

        private SettingsDataPage DataPage => SettingsUi.Page<SettingsDataPage>();

        private SettingsAboutPage AboutPage => SettingsUi.Page<SettingsAboutPage>();

        /// <summary>Страница, если она уже создана, — для кода, который пишет в неё из фона.</summary>
        private T? BuiltPage<T>() where T : FrameworkElement => _settingsView?.Built<T>();

        /// <summary>Видна ли сейчас страница настроек этого типа.</summary>
        private bool SettingsPageShown<T>() where T : FrameworkElement =>
            SettingsOverlay.Visibility == Visibility.Visible && BuiltPage<T>() is { Visibility: Visibility.Visible };

        /// <summary>
        /// Открывает настройки — на той странице, где их закрыли, или на <paramref name="page"/>.
        /// </summary>
        /// <remarks>
        /// Единый вход для шестерёнки, Ctrl+, и ссылок на страницу. Слой показывается первым, а
        /// заполнение идёт следом: прежде вся загрузка шла до показа, и человек несколько кадров
        /// смотрел на замерший интерфейс. Чего не успел построить прогрев, достраивается здесь
        /// же — оболочка и одна страница.
        /// </remarks>
        internal void OpenSettings(RadioButton? page = null)
        {
            MeasureSettingsToFrame();
            using var timer = PerfLog.Measure("settings_show");
            var view = SettingsUi;
            if (SettingsOverlay.Visibility != Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Visible;
                LoadSettingsUi();
            }

            // Checked заводит и наполняет страницу. Первое открытие без цели — на «Общих».
            if (page is not null)
            {
                page.IsChecked = true;
            }
            else if (view.CurrentPage is null)
            {
                view.DefaultNav.IsChecked = true;
            }
        }

        /// <summary>
        /// Замер «от нажатия до кадра» для <c>AMARIN_PERF_LOG</c>: открытие кончается не выходом
        /// из обработчика, а кадром с настройками — раскладку новой страницы WPF делает уже в нём.
        /// </summary>
        /// <remarks>
        /// <c>Rendering</c> приходит в начале кадра, до раскладки и отрисовки; итог пишется
        /// следующей операцией очереди, то есть сразу за этим кадром.
        /// </remarks>
        private void MeasureSettingsToFrame()
        {
            if (!PerfLog.IsEnabled)
            {
                return;
            }

            var toFrame = PerfLog.Measure("settings_to_frame");
            EventHandler? onFrame = null;
            onFrame = (_, _) =>
            {
                CompositionTarget.Rendering -= onFrame;
                Dispatcher.BeginInvoke(toFrame.Dispose, DispatcherPriority.Send);
            };
            CompositionTarget.Rendering += onFrame;
        }

        /// <summary>
        /// Прогрев настроек после первого кадра: в простое, по оболочке или странице за раз.
        /// </summary>
        /// <remarks>
        /// <c>ApplicationIdle</c> — ниже всего, что окно делает само (каталоги, индекс поиска,
        /// дорисовка ленты), поэтому порция встаёт в очередь, только когда той нечем заняться.
        /// Порция — либо создание одной страницы, либо её заполнение, но не оба сразу: страница
        /// моделей с девятью пикерами, каталогами и заготовками держала бы поток окна дольше
        /// кадра, и ввод ждал. Окно, закрытое раньше, чем прогрев догнал, его бросает: тестовые
        /// окна закрываются сразу.
        /// </remarks>
        private void ScheduleSettingsPrewarm() =>
            Dispatcher.BeginInvoke(PrewarmSettingsStep, DispatcherPriority.ApplicationIdle);

        private void PrewarmSettingsStep()
        {
            if (_windowClosed || _services is null)
            {
                return;
            }

            using var timer = PerfLog.Measure("settings_prewarm_step");
            if (_settingsView is null)
            {
                _ = SettingsUi;
                ScheduleSettingsPrewarm();
                return;
            }

            // Сперва заполнить созданную прошлой порцией — тогда к первой странице, которую
            // откроют, прогрев приходит целиком, а не только разметкой.
            if (_staleSettingsPages.FirstOrDefault() is { } unfilled)
            {
                _staleSettingsPages.Remove(unfilled);
                LoadSettingsPage(unfilled);
                ScheduleSettingsPrewarm();
                return;
            }

            if (_settingsDeferred.TryDequeue(out var deferred))
            {
                deferred();
                ScheduleSettingsPrewarm();
                return;
            }

            _prewarmCreating = true;
            bool more;
            try
            {
                more = _settingsView.BuildNext();
            }
            finally
            {
                _prewarmCreating = false;
            }

            if (more || _staleSettingsPages.Count > 0 || _settingsDeferred.Count > 0)
            {
                ScheduleSettingsPrewarm();
            }
        }

        private SettingsView CreateSettingsView()
        {
            using var timer = PerfLog.Measure("settings_shell");
            var view = new SettingsView();
            _settingsView = view;
            view.SettingsVersionText.Text = $"v{RuntimeContext.AppVersion}";
            view.ScrimCloseButton.Click += SettingsCloseButton_Click;
            view.SettingsCloseButton.Click += SettingsCloseButton_Click;
            view.GithubLinkButton.Click += GithubLinkButton_Click;
            view.NavInstructions.Checked += NavInstructions_Checked;
            view.NavAutomation.Checked += NavAutomation_Checked;
            view.NavSecurity.Checked += NavSecurity_Checked;
            view.NavKey.Checked += NavKey_Checked;
            view.NavData.Checked += NavData_Checked;
            view.PageCreated += OnSettingsPageCreated;
            view.PageShown += page =>
            {
                if (_staleSettingsPages.Remove(page))
                {
                    LoadSettingsPage(page);
                }
            };
            SettingsOverlay.Children.Add(view);

            // Плашка версии с найденным обновлением — то, что автомат уже знает.
            if (_updates is not null)
            {
                RenderUpdates();
            }

            return view;
        }

        /// <summary>
        /// Страница создана: контролы получают обработчики окна, страница — то, что ей нужно один
        /// раз, и наполнение из настроек.
        /// </summary>
        private void OnSettingsPageCreated(FrameworkElement page)
        {
            using var timer = PerfLog.Measure("settings_page_" + page.GetType().Name);
            switch (page)
            {
                case SettingsGeneralPage general:
                    Wire(general);
                    EnablePageScroll(general.GeneralPageScroll);
                    SettingsDrill.AddOpenedHandler(general.GeneralHotkeysSub, (_, _) => EnsureHotkeyRows());
                    _settingsDeferred.Enqueue(EnsureHotkeyRows);
                    general.WindowsSettings.Applied = ApplyWindowsIntegration;
                    general.WindowsSettings.Changed += RefreshBehaviorLinks;
                    general.VoiceSettings.Changed += RefreshBehaviorLinks;
                    general.GlobalHotkeysSettings.Applied = () =>
                    {
                        ApplyWindowsIntegration();
                        RefreshBehaviorLinks();
                        return GlobalHotkeyProblems;
                    };
                    break;
                case SettingsAppearancePage appearance:
                    Wire(appearance);
                    EnablePageScroll(appearance.AppearancePageScroll);
                    SettingsDrill.AddOpenedHandler(appearance.AppearanceThemeSub, (_, _) => EnsureThemeCards());
                    _settingsDeferred.Enqueue(EnsureThemeCards);
                    BuildGradientPresets();
                    WireAppearanceControls();
                    appearance.AccessibilitySettings.Changed ??= ApplyAccessibility;
                    break;
                case SettingsProfilePage profile:
                    Wire(profile);
                    EnablePageScroll(profile.ProfilePageScroll);
                    break;
                case SettingsModelsPage models:
                    Wire(models);
                    EnablePageScroll(models.ModelsPageScroll);
                    models.LiteReasoningPicker.SetUsesTools(true);
                    models.HeavyReasoningPicker.SetUsesTools(true);
                    models.RouterReasoningPicker.SetUsesTools(false);
                    models.TitleReasoningPicker.SetUsesTools(false);
                    models.AgentFastReasoningPicker.SetUsesTools(true);
                    models.AgentLiteReasoningPicker.SetUsesTools(true);
                    models.AgentHeavyReasoningPicker.SetUsesTools(true);
                    break;
                case SettingsPromptsPage prompts:
                    Wire(prompts);
                    EnablePageScroll(prompts.PromptsPageScroll);
                    break;
                case SettingsAutomationPage automation:
                    automation.AgentRequested += OnRecipeAgentRequested;
                    automation.Machines.MachinesChanged += OnMachinesChanged;
                    automation.Schedule.RunNow = RunScheduledJobNowAsync;
                    automation.Schedule.OpenChatRequested += chatId =>
                    {
                        SettingsOverlay.Visibility = Visibility.Collapsed;
                        OpenChat(chatId);
                    };
                    break;
                case SettingsSecurityPage security:
                    Wire(security);

                    // Защитник отвечает одним словом и инструментов не получает.
                    security.SynGuardReasoningPicker.SetUsesTools(false);

                    // Подпись — и без служб: пустая строка со стрелкой рядом не объясняет, что за ней прячется.
                    ShowSynGuardModelName();
                    break;
                case SettingsDataPage data:
                    Wire(data);
                    EnablePageScroll(data.DataPageScroll);
                    break;
                case SettingsAboutPage about:
                    Wire(about);
                    EnablePageScroll(about.AboutPageScroll);
                    break;
            }

            // Созданную прогревом заполнит его следующая порция; остальные — сразу.
            if (_prewarmCreating)
            {
                _staleSettingsPages.Add(page);
                return;
            }

            LoadSettingsPage(page);
        }

        /// <summary>
        /// Страница настроек — тем же скроллом, что колонка и чат, и перетаскиванием зажатой
        /// кнопкой. В ленте чата левая кнопка занята выделением текста и лупой, в колонке чатов —
        /// перетаскиванием чатов по папкам, а здесь свободна. Средняя кнопка — автопрокрутка, как
        /// в ленте и колонке.
        /// </summary>
        internal static void EnablePageScroll(ScrollViewer scroll)
        {
            SmoothScroll.SetIsEnabled(scroll, true);
            SmoothScroll.SetDragScroll(scroll, true);
            SmoothScroll.SetAutoScroll(scroll, true);
        }

        /// <summary>Строки «Сочетаний клавиш» — тринадцать полей записи, если их ещё нет.</summary>
        private void EnsureHotkeyRows()
        {
            if (_services is null || BuiltPage<SettingsGeneralPage>() is not { HotkeyList.Children.Count: 0 })
            {
                return;
            }

            var loading = _settingsUiLoading;
            _settingsUiLoading = true;
            try
            {
                LoadHotkeysUi(_services.Settings);
            }
            finally
            {
                _settingsUiLoading = loading;
            }
        }

        /// <summary>Пятьдесят три карточки тем на подстранице «Тема», если их ещё нет.</summary>
        private void EnsureThemeCards()
        {
            if (BuiltPage<SettingsAppearancePage>() is not { ThemeCardsHost.Children.Count: 0 })
            {
                return;
            }

            BuildThemeCards();
            if (_services is null)
            {
                return;
            }

            var loading = _settingsUiLoading;
            _settingsUiLoading = true;
            try
            {
                SyncThemeCards(_services.Settings.Theme);
            }
            finally
            {
                _settingsUiLoading = loading;
            }
        }

        /// <summary>
        /// Достраивает настройки целиком, не дожидаясь простоя: все страницы и их отложенное
        /// содержимое. Для тестов, которые обходят настройки так, будто прогрев уже прошёл.
        /// </summary>
        internal SettingsView CompleteSettingsBuild()
        {
            var view = SettingsUi;
            while (view.BuildNext())
            {
            }

            while (_settingsDeferred.TryDequeue(out var deferred))
            {
                deferred();
            }

            return view;
        }

        /// <summary>
        /// Ставит в контролы страницы сохранённые значения, не поднимая их обработчиков.
        /// </summary>
        /// <remarks>
        /// Страницы с собственным кодом (ключи, инструкции, автоматизация) наполняются сами,
        /// когда их показывают пунктом навигации, — здесь только те, чьи контролы ведёт окно.
        /// Флаг загрузки сохраняется и возвращается, а не сбрасывается: страница может
        /// создаться посреди чужой загрузки, и сброс отпустил бы обработчики раньше времени.
        /// </remarks>
        private void LoadSettingsPage(FrameworkElement page)
        {
            if (_services is null)
            {
                return;
            }

            var settings = _services.Settings;
            var loading = _settingsUiLoading;
            _settingsUiLoading = true;
            try
            {
                switch (page)
                {
                    case SettingsGeneralPage general:
                        general.AutoScrollToggle.IsChecked = settings.AutoScroll;
                        general.CodeLineNumbersToggle.IsChecked = settings.CodeLineNumbers;
                        general.NotifyOnCompleteToggle.IsChecked = settings.NotifyOnResponseComplete;
                        general.NotifySoundToggle.IsChecked = settings.NotifySound;
                        general.RememberWindowSizeToggle.IsChecked = settings.RememberWindowSize;
                        LoadIntegrationUi(_services);

                        // Строки сочетаний строит их подстраница или прогрев (EnsureHotkeyRows);
                        // построенные перечитываются вместе со страницей.
                        if (general.HotkeyList.Children.Count > 0)
                        {
                            LoadHotkeysUi(settings);
                        }

                        general.LanguagePicker.SetSelected(settings.LanguageCode);
                        UpdateTranslationEditButton();
                        SelectDateFormat(settings.DateFormat);
                        break;
                    case SettingsAppearancePage appearance:
                        appearance.AccessibilitySettings.Load(_services);
                        appearance.HighContrastToggle.IsChecked = settings.FollowHighContrast;
                        LoadAppearanceUi(settings);
                        SelectUiScale(settings.UiScalePercent);
                        SelectWindowCorners(settings.WindowCorners);
                        break;
                    case SettingsProfilePage:
                        LoadAccountUi();
                        break;
                    case SettingsModelsPage:
                        // Ключи до выбора моделей: без них правый столбец плашки пуст, а в подписи
                        // поля вместо имени ключа осталась бы пустота.
                        PushKeysToPickers();
                        ApplyCachedCatalogs();
                        ShowSettingsModelSelections();
                        BindSettingsReasoningPickers();
                        break;
                    case SettingsPromptsPage prompts:
                        prompts.MainPromptTextBox.Text = settings.MainPrompt ?? "";
                        LoadPromptLibrary();
                        prompts.TechAiPromptTextBox.Text = string.IsNullOrWhiteSpace(settings.TechAiPrompt)
                            ? ChatEngine.DefaultTechPrompt
                            : settings.TechAiPrompt;
                        prompts.TechAgentPromptTextBox.Text = string.IsNullOrWhiteSpace(settings.TechAgentPrompt)
                            ? Agent.BaseSystemPrompt
                            : settings.TechAgentPrompt;
                        RefreshPromptLinks();
                        break;
                    case SettingsSecurityPage security:
                        // Сама страница наполняется при показе (NavSecurity_Checked); показана
                        // сейчас — значит, сюда пришли смена профиля или импорт.
                        if (security.IsVisible)
                        {
                            security.Attach(_services);
                            security.Load(settings);
                        }

                        security.SynGuardToggle.IsChecked = settings.SynGuardEnabled;

                        // Модель SynGuard — тем же путём, что слоты «Моделей»: ключи, каталог, выбор.
                        PushKeysToPickers();
                        ApplyCachedCatalogs();
                        ShowSettingsModelSelections();
                        BindSettingsReasoningPickers();
                        RefreshAllowedDomainsUi();
                        break;
                    case SettingsDataPage data:
                        data.ChatSharingToggle.IsChecked = settings.ChatSharingEnabled;
                        break;
                    case SettingsAboutPage:
                        LoadUpdatesUi();
                        break;
                }
            }
            finally
            {
                _settingsUiLoading = loading;
            }
        }

        /// <summary>
        /// Каталоги, доехавшие раньше, чем создана страница моделей: ApplyCatalog раздаёт их только
        /// уже построенным плашкам.
        /// </summary>
        private void ApplyCachedCatalogs()
        {
            if (_services is null)
            {
                return;
            }

            foreach (var spec in ProviderSpec.All)
            {
                if (_services.Models.CachedFor(spec.Provider) is not { } models)
                {
                    continue;
                }

                foreach (var field in SettingsPickers())
                {
                    field.SetCatalog(spec.Provider, models, error: null);
                }

                foreach (var picker in SettingsReasoningPickers())
                {
                    picker.SetCatalog(models);
                }
            }
        }

        private void Wire(SettingsGeneralPage p)
        {
            p.LanguagePicker.LanguagePicked += LanguagePicker_LanguagePicked;
            p.LanguagePicker.NewLanguageRequested += LanguagePicker_NewLanguageRequested;
            p.LanguagePicker.LanguageDeleteRequested += LanguagePicker_LanguageDeleteRequested;
            p.EditTranslationButton.Click += EditTranslationButton_Click;
            p.DateFormatComboBox.SelectionChanged += DateFormatComboBox_SelectionChanged;
            p.NotifyOnCompleteToggle.Checked += NotifyOnCompleteToggle_Changed;
            p.NotifyOnCompleteToggle.Unchecked += NotifyOnCompleteToggle_Changed;
            p.NotifySoundToggle.Checked += NotifySoundToggle_Changed;
            p.NotifySoundToggle.Unchecked += NotifySoundToggle_Changed;
            p.NotifyStyleCombo.SelectionChanged += NotifyStyleCombo_SelectionChanged;
            p.AutoScrollToggle.Checked += AutoScrollToggle_Changed;
            p.AutoScrollToggle.Unchecked += AutoScrollToggle_Changed;
            p.CodeLineNumbersToggle.Checked += CodeLineNumbersToggle_Changed;
            p.CodeLineNumbersToggle.Unchecked += CodeLineNumbersToggle_Changed;
            p.RememberWindowSizeToggle.Checked += RememberWindowSizeToggle_Changed;
            p.RememberWindowSizeToggle.Unchecked += RememberWindowSizeToggle_Changed;
        }

        private void Wire(SettingsAppearancePage p)
        {
            p.UiScaleComboBox.SelectionChanged += UiScaleComboBox_SelectionChanged;
            p.FontCombo.SelectionChanged += FontCombo_SelectionChanged;
            p.ChatWidthCombo.SelectionChanged += ChatWidthCombo_SelectionChanged;
            p.WindowCornersComboBox.SelectionChanged += WindowCornersComboBox_SelectionChanged;
            p.AnimationsToggle.Checked += AnimationsToggle_Changed;
            p.AnimationsToggle.Unchecked += AnimationsToggle_Changed;
            p.CompactComposerToggle.Checked += CompactComposerToggle_Changed;
            p.CompactComposerToggle.Unchecked += CompactComposerToggle_Changed;
            p.CompactHoverToggle.Checked += CompactHoverToggle_Changed;
            p.CompactHoverToggle.Unchecked += CompactHoverToggle_Changed;
            p.ResetAppearanceButton.Click += ResetAppearanceButton_Click;
            p.ThemeFollowSystemToggle.Checked += ThemeFollowSystemToggle_Changed;
            p.ThemeFollowSystemToggle.Unchecked += ThemeFollowSystemToggle_Changed;
            p.HighContrastToggle.Checked += HighContrastToggle_Changed;
            p.HighContrastToggle.Unchecked += HighContrastToggle_Changed;
            p.AppearanceEnabledToggle.Checked += AppearanceEnabledToggle_Changed;
            p.AppearanceEnabledToggle.Unchecked += AppearanceEnabledToggle_Changed;
            p.BackdropNone.Checked += BackdropMode_Checked;
            p.BackdropGradient.Checked += BackdropMode_Checked;
            p.BackdropImage.Checked += BackdropMode_Checked;
            p.MotionCombo.SelectionChanged += MotionCombo_SelectionChanged;
            p.RemoveBackgroundButton.Click += RemoveBackgroundButton_Click;
            p.ChooseBackgroundButton.Click += ChooseBackgroundButton_Click;
            p.ImageFitCombo.SelectionChanged += ImageFitCombo_SelectionChanged;
            p.GlassSheenToggle.Checked += GlassSheenToggle_Changed;
            p.GlassSheenToggle.Unchecked += GlassSheenToggle_Changed;
            p.VignetteToggle.Checked += VignetteToggle_Changed;
            p.VignetteToggle.Unchecked += VignetteToggle_Changed;
        }

        private void Wire(SettingsProfilePage p)
        {
            p.SwitchAccountButton.Click += SwitchAccountButton_Click;
            p.RemoveAvatarButton.Click += RemoveAvatarButton_Click;
            p.ChangeAvatarButton.Click += ChangeAvatarButton_Click;
            p.ChangeNameButton.Click += ChangeNameButton_Click;
            p.RemovePasswordButton.Click += RemovePasswordButton_Click;
            p.ChangePasswordButton.Click += ChangePasswordButton_Click;
            p.LockOnStartupToggle.Checked += LockOnStartupToggle_Changed;
            p.LockOnStartupToggle.Unchecked += LockOnStartupToggle_Changed;
            p.AutoLockCombo.SelectionChanged += AutoLockCombo_SelectionChanged;
            p.LockNowButton.Click += LockNowButton_Click;
        }

        private void Wire(SettingsModelsPage p)
        {
            foreach (var (picker, reasoning) in new (ModelPickerField, ReasoningPicker)[]
                     {
                         (p.LiteModelPicker, p.LiteReasoningPicker),
                         (p.HeavyModelPicker, p.HeavyReasoningPicker),
                         (p.RouterModelPicker, p.RouterReasoningPicker),
                         (p.AgentFastModelPicker, p.AgentFastReasoningPicker),
                         (p.AgentLiteModelPicker, p.AgentLiteReasoningPicker),
                         (p.AgentHeavyModelPicker, p.AgentHeavyReasoningPicker),
                         (p.TitleModelPicker, p.TitleReasoningPicker),
                         (p.SummaryModelPicker, p.SummaryReasoningPicker)
                     })
            {
                WireSlot(picker, reasoning);
            }

            p.WebSearchField.Changed += WebSearchTargetChanged;
            p.WebSearchField.AddKeyRequested += SettingsPickerAddKeyRequested;
        }

        private void WireSlot(ModelPickerField picker, ReasoningPicker reasoning)
        {
            reasoning.ChoiceChanged += SettingsReasoningChanged;
            picker.ModelPicked += SettingsModelPicked;
            picker.AddKeyRequested += SettingsPickerAddKeyRequested;
            picker.ProviderShown += SettingsPickerProviderShown;
        }

        /// <summary>
        /// SynGuard и его модель (слоты моделей ведёт окно, как у остальных пикеров) и белый список
        /// загрузок (его же дополняет вопрос «разрешить домен?» из чата).
        /// </summary>
        private void Wire(SettingsSecurityPage p)
        {
            p.SynGuardToggle.Checked += SynGuardToggle_Changed;
            p.SynGuardToggle.Unchecked += SynGuardToggle_Changed;
            WireSlot(p.SynGuardModelPicker, p.SynGuardReasoningPicker);
            p.AddDomainRequested += (_, _) => OpenDomainDialog("");
            p.RemoveDomainRequested += (_, domain) =>
            {
                if (_services is null)
                {
                    return;
                }

                AllowedDomains.RemoveAll(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
                SaveAllowedDomains();
            };
        }

        private void Wire(SettingsPromptsPage p)
        {
            p.SaveMainPromptButton.Click += SaveMainPromptButton_Click;
            p.PromptCreateButton.Click += PromptCreateButton_Click;
            p.ResetTechAiPromptButton.Click += ResetTechAiPromptButton_Click;
            p.SaveTechAiPromptButton.Click += SaveTechAiPromptButton_Click;
            p.ResetTechAgentPromptButton.Click += ResetTechAgentPromptButton_Click;
            p.SaveTechAgentPromptButton.Click += SaveTechAgentPromptButton_Click;
        }

        private void Wire(SettingsDataPage p)
        {
            p.ChatSharingToggle.Checked += ChatSharingToggle_Changed;
            p.ChatSharingToggle.Unchecked += ChatSharingToggle_Changed;
            p.ImportChatButton.Click += ImportChatButton_Click;
            p.DataExportButton.Click += DataExportButton_Click;
            p.DataImportButton.Click += DataImportButton_Click;
            p.OpenAppFilesButton.Click += OpenAppFilesButton_Click;
            p.DeleteAllChatsButton.Click += DeleteAllChatsButton_Click;
            p.DeleteAllDataButton.Click += DeleteAllDataButton_Click;
            p.UsageRefreshButton.Click += UsageRefreshButton_Click;

            // Разбивка «Занято на диске» лежит наверху «Хранения и очистки» и считается, когда туда
            // заходят, а не на каждое открытие «Данных»: обход читает все файлы чатов, а смотрят
            // место там, где его освобождают.
            p.CareLinkRow.Click += (_, _) => Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");
        }

        private void Wire(SettingsAboutPage p)
        {
            p.CheckUpdatesButton.Click += CheckUpdatesButton_Click;
            p.UpdateNowButton.Click += UpdateNowButton_Click;
            p.UpdateCancelDownloadButton.Click += UpdateBadgeCancelButton_Click;
            p.OpenReleaseButton.Click += OpenReleaseButton_Click;
            p.RollbackButton.Click += RollbackButton_Click;
            p.AutoUpdateToggle.Checked += AutoUpdateToggle_Changed;
            p.AutoUpdateToggle.Unchecked += AutoUpdateToggle_Changed;
            p.BetaChannelToggle.Checked += BetaChannelToggle_Changed;
            p.BetaChannelToggle.Unchecked += BetaChannelToggle_Changed;
            p.AboutDocsButton.Click += AboutDocsButton_Click;
            p.AboutRepoButton.Click += AboutRepoButton_Click;
            p.ReportBugButton.Click += ReportBugButton_Click;
            p.OpenLogsButton.Click += OpenLogsButton_Click;
        }
    }
}
