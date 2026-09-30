using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Info» в настройках: как подключить Venice.ai или OpenRouter и как устроена сама программа.
/// </summary>
/// <remarks>
/// Ключ задаётся на странице «Key &amp; Info» и хранится зашифрованным средствами Windows
/// (см. <c>ApiKeyStore</c>). Переменная окружения <c>VENICE_API_KEY</c> осталась как способ
/// вообще не отдавать ключ программе на хранение, и эта страница объясняет оба пути:
/// до неё программа без ключа просто молчала.
/// </remarks>
public partial class SettingsInfoPage : UserControl
{
    // Адреса Venice собраны здесь, а не в разметке: если сайт переедет, чинить одно место.
    private const string VeniceUrl = "https://venice.ai";

    /// <remarks>
    /// Страницы «настройки Venice» вообще нет — есть страница тарифов и страница API. Раньше
    /// кнопка вела на venice.ai/settings, которая не открывается.
    /// </remarks>
    private const string PricingUrl = "https://venice.ai/pricing";

    /// <remarks>Тот же адрес, что docs.venice.ai даёт ссылкой «Venice API Settings».</remarks>
    private const string ApiKeysUrl = "https://venice.ai/settings/api";

    private const string DocsUrl = "https://docs.venice.ai";

    private const string OpenRouterUrl = "https://openrouter.ai";

    public SettingsInfoPage()
    {
        InitializeComponent();

        // Та же плавная прокрутка, что у боковой колонки и ленты чата: страница настроек
        // не должна рывками отличаться от остальной программы.
        SmoothScroll.SetIsEnabled(InfoPageScroll, true);
        SmoothScroll.SetDragScroll(InfoPageScroll, true);
    }

    private void OpenVeniceButton_Click(object sender, RoutedEventArgs e) => Open(VeniceUrl);

    private void OpenPricingButton_Click(object sender, RoutedEventArgs e) => Open(PricingUrl);

    private void OpenApiKeysButton_Click(object sender, RoutedEventArgs e) => Open(ApiKeysUrl);

    private void OpenDocsButton_Click(object sender, RoutedEventArgs e) => Open(DocsUrl);

    private void OpenOpenRouterButton_Click(object sender, RoutedEventArgs e) => Open(OpenRouterUrl);

    /// <summary>Тот же адрес, что у ссылки «Получить ключ» в окне добавления ключа.</summary>
    private void OpenOpenRouterKeysButton_Click(object sender, RoutedEventArgs e) =>
        Open(ProviderSpec.For(LlmProvider.OpenRouter).KeysUrl);

    /// <summary>
    /// Путь Venice или OpenRouter (E6). Выбор не запоминается: страницу открывают, чтобы
    /// прочитать один раз, а не возвращаться к ней.
    /// </summary>
    private void GuideProvider_Checked(object sender, RoutedEventArgs e)
    {
        if (VeniceGuide is null || OpenRouterGuide is null)
        {
            return;
        }

        var openRouter = GuideOpenRouter.IsChecked == true;
        VeniceGuide.Visibility = openRouter ? Visibility.Collapsed : Visibility.Visible;
        OpenRouterGuide.Visibility = openRouter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenRepoButton_Click(object sender, RoutedEventArgs e) => Open(UpdateChecker.RepositoryUrl);

    /// <remarks>
    /// Через главное окно, а не своим Process.Start: там уже есть перехват отказа оболочки и
    /// понятное сообщение вместо текста исключения. В конструкторе XAML окна нет — молча выходим.
    /// </remarks>
    private void Open(string url)
    {
        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.OpenExternalLink(url);
        }
    }
}
