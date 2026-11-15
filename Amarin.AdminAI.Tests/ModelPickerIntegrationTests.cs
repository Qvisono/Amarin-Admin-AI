using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ModelPickerIntegrationTests(WpfFixture wpf)
{
    private const string ModelId = "test-effort-model";
    private const BindingFlags InstanceFields = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Picking_and_setting_effort_keep_the_menu_open_and_save_to_the_current_profile(bool inSettings)
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-picker-" + Guid.NewGuid().ToString("N"));
        try
        {
            await wpf.Ui.Invoke(async () =>
            {
                var window = Application.Current.Windows.OfType<MainWindow>().Single();
                var serviceField = typeof(MainWindow).GetField("_services", InstanceFields)!;
                var sessionField = typeof(MainWindow).GetField("_session", InstanceFields)!;
                var previousServices = serviceField.GetValue(window);
                var previousSession = sessionField.GetValue(window);
                var session = new ChatSession { SelectedModelId = "initial-model", DisableThinking = false, ReasoningEffort = "low" };
                using var services = UiServices.Build(root, "test-key", new ModelCatalogHandler());
                var catalog = await services.Models.LoadAsync(LlmProvider.Venice);
                Assert.Null(catalog.Error);
                Assert.Equal(ModelId, Assert.Single(catalog.Models!).Id);
                serviceField.SetValue(window, services);
                sessionField.SetValue(window, session);
                var settingsOverlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
                var previousVisibility = settingsOverlay.Visibility;
                try
                {
                    VerifyPicker(window, services, session, inSettings);
                }
                finally
                {
                    PopupManager.CloseAll();
                    settingsOverlay.Visibility = previousVisibility;
                    serviceField.SetValue(window, previousServices);
                    sessionField.SetValue(window, previousSession);
                    var chat = (ModelPickerPanel)window.FindName("ChatModelPicker")!;
                    chat.SetFavorites(new HashSet<string>());
                    chat.SetKeys([]);
                    chat.SetCatalog(LlmProvider.Venice, []);
                    chat.SetSelected("", null);
                    chat.SetReasoning(null);
                    if (inSettings)
                    {
                        var field = (ModelPickerField)window.FindSetting("FastModelPicker")!;
                        field.SetFavorites(new HashSet<string>());
                        field.SetKeys([]);
                        field.SetCatalog(LlmProvider.Venice, []);
                        field.SetSelected("", null);
                    }

                    services.SettingsStore.Flush();
                }

                return true;
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void VerifyPicker(MainWindow window, AppServices services, ChatSession session, bool inSettings)
    {
        var field = inSettings ? OpenSettingsModels(window) : null;
        var panel = field?.Panel ?? (ModelPickerPanel)window.FindName("ChatModelPicker")!;
        var trigger = field is null
            ? (ToggleButton)window.FindName("ModelButton")!
            : (ToggleButton)field.FindName("OpenButton")!;
        var popup = field is null
            ? (Popup)window.FindName("ModelPicker")!
            : (Popup)field.FindName("PickerPopup")!;
        ApiKeyEntry[] keys = [new("test", "Test", "fake", ApiKeySource.Stored, true, LlmProvider.Venice)];
        VeniceModelInfo[] catalog = [TestModel()];
        if (field is null)
        {
            panel.SetKeys(keys);
            panel.SetCatalog(LlmProvider.Venice, catalog);
        }
        else
        {
            field.SetKeys(keys);
            field.SetCatalog(LlmProvider.Venice, catalog);
            field.SetSelected("initial-model", null, new ReasoningChoice(false, "low"));
        }

        window.UpdateLayout();
        trigger.IsChecked = true;
        panel.Dispatcher.Invoke(panel.RebuildNow, DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        Assert.True(popup.IsOpen);

        var rows = (ItemsControl)panel.FindName("ModelItems")!;
        Assert.Single(rows.Items);
        var item = Assert.IsType<ModelPickerRow>(rows.Items[0]);
        var rowName = item.Display;
        var row = Assert.IsAssignableFrom<DependencyObject>(rows.ItemContainerGenerator.ContainerFromIndex(0));
        var pick = Descendants(row).OfType<Button>().Single(button => Equals(button.Tag, ModelId));
        Assert.Equal(rowName, AutomationProperties.GetName(pick));
        pick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(popup.IsOpen);
        Assert.Equal(ModelId, inSettings ? services.Settings.FastModelId : session.SelectedModelId);

        var slider = (Slider)panel.FindName("EffortSlider")!;
        panel.RebuildNow();
        Assert.True(slider.IsVisible);
        slider.Value = slider.Maximum;
        var reasoning = inSettings ? services.Settings.FastReasoning : services.Settings.ChatReasoning;
        Assert.Equal("high", reasoning.ReasoningEffort);
        Assert.False(reasoning.DisableThinking);
        Assert.True(popup.IsOpen);
        Assert.True(item.IsSelected);
        Assert.Equal(rowName, item.Display);
        Assert.Equal(rowName, AutomationProperties.GetName(pick));
        var label = field is null
            ? (TextBlock)trigger.Template.FindName("ModelButtonLabel", trigger)!
            : (TextBlock)field.FindName("SelectedLabel")!;
        Assert.Equal(rowName + " · high", label.Text);
        Assert.Equal(rowName + " · high", ((TextBlock)panel.FindName("ChoiceModelLabel")!).Text);

        var star = Descendants(row).OfType<ToggleButton>().Single(button => button.Name == "FavoriteStar");
        star.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(ModelId, Assert.Single(services.Settings.FavoriteModelIds));
        Assert.Equal(ModelId, Assert.Single(services.SettingsStore.Load().FavoriteModelIds));
        Assert.True(popup.IsOpen);
    }

    private static ModelPickerField OpenSettingsModels(MainWindow window)
    {
        ((FrameworkElement)window.FindSetting("SettingsOverlay")!).Visibility = Visibility.Visible;
        ((RadioButton)window.FindSetting("NavModels")!).IsChecked = true;
        return (ModelPickerField)window.FindSetting("FastModelPicker")!;
    }

    private static VeniceModelInfo TestModel() => new()
    {
        Id = ModelId,
        ModelSpec = new VeniceModelSpec
        {
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoning = true,
                SupportsReasoningEffort = true,
                ReasoningEffortOptions = ["low", "high"]
            }
        }
    };

    private sealed class ModelCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var models = new VeniceModelsListResponse { Data = [TestModel()] };
            var json = JsonSerializer.Serialize(models, VeniceJsonContext.Default.VeniceModelsListResponse);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
