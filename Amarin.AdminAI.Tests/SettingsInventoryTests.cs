using System.Windows;
using System.Windows.Controls;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Опись настроек: каждая настройка из таблицы переезда (1.30 → 11 страниц) существует и лежит на
/// своей новой странице.
/// </summary>
/// <remarks>
/// Страницы пересобраны из кусков старых, и потерять при этом строку легко — она просто не
/// попала бы ни на одну страницу, а заметили бы это через месяц. Здесь перечислено по контролу на
/// каждую строку таблицы; проверка — что контрол есть и что его страница видна, когда выбран её
/// пункт навигации (вложенные подстраницы при этом могут быть свёрнуты — важна страница).
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SettingsInventoryTests
{
    private readonly WpfFixture _wpf;

    public SettingsInventoryTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>Пункт навигации → контролы его страницы. «Security:» и «Key:» — имена внутри страниц-UserControl.</summary>
    private static readonly (string Nav, string[] Names)[] Inventory =
    [
        ("NavGeneral",
        [
            "LanguagePicker", "EditTranslationButton", "DateFormatComboBox",
            "NotifyOnCompleteToggle", "NotifySoundToggle", "NotifyStyleCombo", "DeferredSoundToggle", "DeferredSoundListenButton",
            "AutoScrollToggle", "CodeLineNumbersToggle", "RememberWindowSizeToggle",
            "HotkeysLinkRow", "HotkeyList", "GlobalHotkeysSettings",
            "WindowsLinkRow", "WindowsSettings", "VoiceLinkRow", "VoiceSettings"
        ]),
        ("NavAppearance",
        [
            "ThemeLinkRow", "ThemeCardsHost", "ThemeFollowSystemToggle", "HighContrastToggle",
            "BackdropLinkRow", "AppearanceEnabledToggle", "AccentPicker",
            "BackdropNone", "BackdropGradient", "BackdropImage",
            "GradientPresetsHost", "GradientColor1", "GradientColor2", "GradientColor3", "GradientColor4",
            "GradientAngleSlider", "MotionCombo", "MotionSpeedSlider",
            "ChooseBackgroundButton", "RemoveBackgroundButton", "ImageFitCombo",
            "ImageBrightnessSlider", "ImageSaturationSlider", "ImageBlurSlider",
            "GlassOpacitySlider", "GlassFrostSlider", "GlassSheenToggle", "VignetteToggle",
            "UiScaleComboBox", "FontCombo", "AccessibilitySettings", "ChatWidthCombo", "WindowCornersComboBox",
            "CornerRadiusSlider", "GrainSlider", "AnimationsToggle",
            "CompactComposerToggle", "CompactHoverToggle", "CompactDelaySlider", "CompactWidthSlider", "CompactHoverSlider",
            "ResetAppearanceButton"
        ]),
        ("NavProfile",
        [
            "SwitchAccountButton", "ChangeAvatarButton", "RemoveAvatarButton", "ChangeNameButton",
            "ChangePasswordButton", "RemovePasswordButton", "LockOnStartupToggle", "AutoLockCombo", "LockNowButton"
        ]),
        ("NavModels",
        [
            "LiteModelPicker", "LiteReasoningPicker", "HeavyModelPicker", "HeavyReasoningPicker",
            "RouterModelPicker", "RouterReasoningPicker",
            "AgentFastModelPicker", "AgentFastReasoningPicker", "AgentLiteModelPicker", "AgentLiteReasoningPicker",
            "AgentHeavyModelPicker", "AgentHeavyReasoningPicker",
            "TitleModelPicker", "TitleReasoningPicker", "SummaryModelPicker", "SummaryReasoningPicker", "WebSearchField"
        ]),
        ("NavPrompts",
        [
            "MainPromptTextBox", "SaveMainPromptButton", "PromptCreateButton", "PromptLibraryHost",
            "TechPromptsLinkRow", "TechAiPromptTextBox", "SaveTechAiPromptButton", "ResetTechAiPromptButton",
            "TechAgentPromptTextBox", "SaveTechAgentPromptButton", "ResetTechAgentPromptButton"
        ]),
        ("NavInstructions", ["InstructionsPage"]),
        ("NavAutomation", ["AutomationPage"]),
        ("NavSecurity",
        [
            "Security:ModeCombo", "Security:SynGuardToggle", "Security:SynGuardModelToggle",
            "Security:SynGuardReasoningPicker", "Security:SynGuardModelPicker",
            "Security:PlanLiteToggle", "Security:PlanFastToggle", "Security:PlanHeavyToggle",
            "Security:EncryptChatsToggle", "Security:ToolsLinkRow", "Security:EnableAllToolsButton",
            "Security:SourcesLinkRow", "Security:AddDomainButton", "Security:AllowedDomainsList"
        ]),
        ("NavKey", ["Key:AddKeyButton", "Key:LimitsLinkRow", "Key:LimitsBlock"]),
        ("NavData",
        [
            "ChatSharingToggle", "ImportChatButton", "DataExportButton", "DataImportButton",
            "BackupLinkRow", "BackupPanel", "CareLinkRow", "DataCarePanel", "UsageRefreshButton", "UsageList",
            "OpenAppFilesButton", "DeleteAllChatsButton", "DeleteAllDataButton"
        ]),
        ("NavAbout",
        [
            "UpdateVersionText", "CheckUpdatesButton", "UpdateNowButton", "UpdateCancelDownloadButton",
            "OpenReleaseButton", "RollbackButton", "AutoUpdateToggle", "BetaChannelToggle",
            "GuideLinkRow", "GuidePage", "AboutDocsButton", "AboutRepoButton", "ReportBugButton", "OpenLogsButton"
        ])
    ];

    [Fact]
    public void Every_setting_lives_on_its_new_page()
    {
        var problems = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            var content = LogicalTreeHelper.GetParent((DependencyObject)window.FindSetting("SettingsCloseButton")!)!;
            var security = (FrameworkElement)window.FindSetting("SecurityPage")!;
            var key = (FrameworkElement)window.FindSetting("KeyPage")!;
            var wasVisible = overlay.Visibility;
            var wasChecked = SettingsNavNames.All.Select(name => (RadioButton)window.FindSetting(name)!)
                .FirstOrDefault(radio => radio.IsChecked == true);
            var found = new List<string>();
            overlay.Visibility = Visibility.Visible;
            try
            {
                foreach (var (nav, names) in Inventory)
                {
                    ((RadioButton)window.FindSetting(nav)!).IsChecked = true;
                    window.UpdateLayout();
                    foreach (var entry in names)
                    {
                        var element = entry switch
                        {
                            _ when entry.StartsWith("Security:", StringComparison.Ordinal) => security.FindName(entry[9..]),
                            _ when entry.StartsWith("Key:", StringComparison.Ordinal) => key.FindName(entry[4..]),
                            _ => window.FindSetting(entry)
                        } as DependencyObject;
                        if (element is null)
                        {
                            found.Add($"{nav}: нет «{entry}»");
                            continue;
                        }

                        // Страница — ребёнок сетки содержимого настроек, в котором лежит контрол.
                        var page = element;
                        while (page is not null && !ReferenceEquals(LogicalTreeHelper.GetParent(page), content))
                        {
                            page = LogicalTreeHelper.GetParent(page);
                        }

                        if (page is not FrameworkElement { Visibility: Visibility.Visible })
                        {
                            found.Add($"{nav}: «{entry}» не на этой странице");
                        }
                    }
                }
            }
            finally
            {
                wasChecked?.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                overlay.Visibility = wasVisible;
            }

            return found;
        });

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
