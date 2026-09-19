using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Info» в настройках: как подключить Venice.ai и как устроена сама программа.
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

    public SettingsInfoPage()
    {
        InitializeComponent();

        // Та же плавная прокрутка, что у боковой колонки и ленты чата: страница настроек
        // не должна рывками отличаться от остальной программы.
        SmoothScroll.SetIsEnabled(InfoPageScroll, true);
    }

    private void OpenVeniceButton_Click(object sender, RoutedEventArgs e) => Open(VeniceUrl);

    private void OpenPricingButton_Click(object sender, RoutedEventArgs e) => Open(PricingUrl);

    private void OpenApiKeysButton_Click(object sender, RoutedEventArgs e) => Open(ApiKeysUrl);

    private void OpenDocsButton_Click(object sender, RoutedEventArgs e) => Open(DocsUrl);

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
