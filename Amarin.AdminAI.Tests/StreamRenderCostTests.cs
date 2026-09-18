using System.Linq;
using System.Windows;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Что перестало происходить на каждый токен ответа: поиск логотипа по дереву ресурсов и
/// вытеснение кэша подсветки промежуточными состояниями растущего блока кода.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class StreamRenderCostTests
{
    private readonly WpfFixture _wpf;

    public StreamRenderCostTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    [Fact]
    public void Branding_is_applied_once_per_model()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var host = Window();
            var message = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "a1",
                Text = "ответ",
                ResolvedModelId = "grok-4-6"
            };

            var view = ChatMessageViews.CreateAssistant(host, message);
            var expectedName = view.ModelName.Text;
            var expectedLogo = view.LogoImage.Source;

            // Тот же идентификатор — работы быть не должно: ни поиска ресурса по дереву
            // словарей, ни переустановки размеров картинки. Раньше это шло на каждую дельту.
            view.LogoImage.Source = null;
            view.ModelName.Text = "затёрто";
            view.ApplyBranding(host, "grok-4-6");

            Assert.Null(view.LogoImage.Source);
            Assert.Equal("затёрто", view.ModelName.Text);

            // Другая модель — применяется.
            view.ApplyBranding(host, "openai-gpt-56-luna");
            Assert.NotEqual("затёрто", view.ModelName.Text);

            view.ApplyBranding(host, "grok-4-6");
            Assert.Equal(expectedName, view.ModelName.Text);
            Assert.Equal(expectedLogo, view.LogoImage.Source);
            return null;
        });
    }

    [Fact]
    public void A_growing_block_does_not_evict_the_finished_ones()
    {
        // Ключ кэша — весь текст блока. Растущий блок за время одного ответа кладёт в кэш
        // десятки промежуточных состояний и вымывает оттуда подсветку дописанных сообщений.
        const string finished = "Get-Process | Where-Object { $_.CPU -gt 10 }";
        var warm = CodeHighlighter.Highlight(finished, "powershell");

        var growing = "";
        for (var i = 0; i < 200; i++)
        {
            growing += "Write-Host 'строка " + i + "'\n";
            CodeHighlighter.Highlight(growing, "powershell", cache: false);
        }

        // Тот же экземпляр списка означает попадание в кэш: его никто не вытеснил.
        Assert.Same(warm, CodeHighlighter.Highlight(finished, "powershell"));
    }

    [Fact]
    public void A_finished_block_is_still_remembered()
    {
        const string code = "int main() { return 0; }";
        var first = CodeHighlighter.Highlight(code, "cpp");
        Assert.Same(first, CodeHighlighter.Highlight(code, "cpp"));
    }
}
