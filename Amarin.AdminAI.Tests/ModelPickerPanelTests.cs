using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Плашка выбора модели: провайдеры слева, модели посередине, ключи справа.
/// </summary>
/// <remarks>
/// До версии 1.23.0 плашка показывала модели одного провайдера — того, чей ключ активен, —
/// и о ключах не знала вовсе. Смешать провайдеров по слотам было негде: заголовки чатов
/// уходили туда же, куда и разговор.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ModelPickerPanelTests
{
    private readonly WpfFixture _wpf;

    public ModelPickerPanelTests(WpfFixture wpf) => _wpf = wpf;

    private static IReadOnlyList<ApiKeyEntry> BothProviders() =>
    [
        new("v1", "Основной", "vk-secret-1", ApiKeySource.Stored, true, LlmProvider.Venice),
        new("v2", "Запасной", "vk-secret-2", ApiKeySource.Stored, false, LlmProvider.Venice),
        new("o1", "Роутер", "sk-or-v1-secret", ApiKeySource.Stored, false, LlmProvider.OpenRouter)
    ];

    private static List<VeniceModelInfo> Many(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new VeniceModelInfo { Id = $"{prefix}{i:000}" })];

    /// <summary>
    /// Собирает плашку внутри живого окна: невидимая она списков не строит, а сборка
    /// откладывается до кадра — в тесте её просим сразу.
    /// </summary>
    private T Shown<T>(Action<ModelPickerPanel> arrange, Func<ModelPickerPanel, T> read) =>
        _wpf.Ui.Invoke(() =>
        {
            var host = (Panel)Application.Current.Windows
                .OfType<MainWindow>()
                .Single()
                .FindName("MessagesPanel")!;

            var panel = new ModelPickerPanel();
            host.Children.Add(panel);
            try
            {
                arrange(panel);
                panel.RebuildNow();
                host.UpdateLayout();
                return read(panel);
            }
            finally
            {
                host.Children.Remove(panel);
            }
        });

    private static Panel Part(ModelPickerPanel panel, string name) =>
        (Panel)panel.FindName(name)!;

    private static ItemsControl Models(ModelPickerPanel panel) =>
        (ItemsControl)panel.FindName("ModelItems")!;

    private static IEnumerable<string> Labels(Panel panel) =>
        panel.Children
            .OfType<ContentControl>()
            .Select(item => item.Content)
            .OfType<TextBlock>()
            .Select(block => block.Text);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>Слева — по кнопке на провайдера, и ни один не спрятан из-за отсутствия ключа.</summary>
    [Fact]
    public void Every_provider_gets_a_button()
    {
        var names = Shown(
            panel => panel.SetKeys(BothProviders()),
            panel => Labels(Part(panel, "ProviderItems")).ToList());

        Assert.Equal(ProviderSpec.All.Select(spec => spec.Name), names);
    }

    /// <summary>
    /// Справа — только имена ключей этого провайдера и строка «по умолчанию». Сам ключ здесь
    /// не показывается: столбец служит выбору, а не чтению секрета.
    /// </summary>
    [Fact]
    public void The_key_column_lists_this_provider_only()
    {
        var names = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, [new VeniceModelInfo { Id = "grok-4-6" }]);
                panel.SetSelected("grok-4-6", null);
                panel.ShowSelectedProvider();
            },
            panel => Labels(Part(panel, "KeyItems")).ToList());

        Assert.Equal([Loc.Get("S.Models.KeyDefault"), "Основной", "Запасной"], names);
    }

    /// <summary>Выбрана модель OpenRouter — справа ключи OpenRouter, а не соседа.</summary>
    [Fact]
    public void The_key_column_follows_the_model_provider()
    {
        var names = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(
                    LlmProvider.OpenRouter,
                    [new VeniceModelInfo { Id = "openrouter:openai/gpt-5" }]);
                panel.SetSelected("openrouter:openai/gpt-5", null);
                panel.ShowSelectedProvider();
            },
            panel => Labels(Part(panel, "KeyItems")).ToList());

        Assert.Equal([Loc.Get("S.Models.KeyDefault"), "Роутер"], names);
    }

    /// <summary>
    /// У провайдера без ключа вместо списка моделей — кнопка «добавить ключ». Запрос без ключа
    /// вернулся бы четырёхсоткой, и человек прочитал бы её как поломку программы.
    /// </summary>
    [Fact]
    public void A_provider_without_a_key_offers_to_add_one()
    {
        var (pane, models) = Shown(
            panel =>
            {
                panel.SetKeys(
                [
                    new ApiKeyEntry("v1", "Основной", "vk-secret-1", ApiKeySource.Stored, true, LlmProvider.Venice)
                ]);
                panel.SetSelected("openrouter:openai/gpt-5", null);
                panel.ShowSelectedProvider();
            },
            panel => (
                ((UIElement)panel.FindName("NoKeyPane")!).Visibility,
                Models(panel).Visibility));

        Assert.Equal(Visibility.Visible, pane);
        Assert.Equal(Visibility.Collapsed, models);
    }

    /// <summary>«Авто» стоит первой строкой и только там, где ей есть смысл.</summary>
    [Fact]
    public void Auto_sits_on_top_and_only_where_it_belongs()
    {
        var (chat, slot) = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, [new VeniceModelInfo { Id = "grok-4-6" }]);
                panel.SetSelected("grok-4-6", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                var auto = (UIElement)panel.FindName("AutoItem")!;
                var visible = auto.Visibility;
                panel.AllowAuto = false;
                panel.RebuildNow();
                return (visible, auto.Visibility);
            });

        Assert.Equal(Visibility.Visible, chat);
        Assert.Equal(Visibility.Collapsed, slot);
    }

    /// <summary>
    /// Выбор ключа отдаётся отдельным событием и не считается выбором модели.
    /// </summary>
    /// <remarks>
    /// Пока оба шли одним событием, плашка захлопывалась от нажатия на ключ: хозяин закрывает
    /// её по <c>ModelPicked</c>, считая, что выбор сделан.
    /// </remarks>
    [Fact]
    public void Picking_a_key_does_not_count_as_picking_a_model()
    {
        var (key, model) = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, [new VeniceModelInfo { Id = "grok-4-6" }]);
                panel.SetSelected("grok-4-6", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                ModelBinding? picked = null;
                ModelBinding? chosen = null;
                panel.KeyPicked += (_, binding) => picked = binding;
                panel.ModelPicked += (_, binding) => chosen = binding;

                var keys = Part(panel, "KeyItems").Children.OfType<RadioButton>().ToList();
                keys[2].IsChecked = true;
                return (picked, chosen);
            });

        Assert.NotNull(key);
        Assert.Equal("grok-4-6", key!.Value.ModelId);
        Assert.Equal("v2", key.Value.KeyId);
        Assert.Null(model);
    }

    /// <summary>
    /// Столбец остаётся там, куда его поставил человек, даже когда подъезжает каталог.
    /// </summary>
    /// <remarks>
    /// Хозяин после загрузки каталога раздаёт всем плашкам текущие привязки, и прежняя
    /// редакция возвращала столбец к провайдеру выбранной модели прямо под рукой человека —
    /// со стороны это выглядело так, будто переключение срабатывает через раз.
    /// </remarks>
    [Fact]
    public void A_chosen_provider_survives_a_catalogue_arriving()
    {
        var names = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, [new VeniceModelInfo { Id = "grok-4-6" }]);
                panel.SetSelected("grok-4-6", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                // Человек ушёл в столбец соседа...
                Part(panel, "ProviderItems").Children.OfType<RadioButton>().Last().IsChecked = true;
                panel.RebuildNow();

                // ...и в этот миг доехал каталог, а хозяин пересказал плашке выбранную модель.
                panel.SetCatalog(
                    LlmProvider.OpenRouter,
                    [new VeniceModelInfo { Id = "openrouter:openai/gpt-5" }]);
                panel.SetSelected("grok-4-6", null);
                panel.RebuildNow();

                return Labels(Part(panel, "KeyItems")).ToList();
            });

        Assert.Equal([Loc.Get("S.Models.KeyDefault"), "Роутер"], names);
    }

    /// <summary>
    /// Список виртуализован: из четырёхсот моделей строк создаётся десяток.
    /// </summary>
    /// <remarks>
    /// У OpenRouter моделей сотни. Прежняя редакция строила их все кнопками — и на каждый
    /// символ, набранный в поиске, заново. Это и было подвисание.
    /// </remarks>
    [Fact]
    public void A_long_list_builds_only_what_is_on_screen()
    {
        var (total, realized) = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, Many("model-", 400));
                panel.SetSelected("model-000", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                var list = Models(panel);
                return (list.Items.Count, Descendants(list).OfType<Button>().Count());
            });

        Assert.Equal(400, total);
        Assert.InRange(realized, 1, 60);
    }

    /// <summary>
    /// Три столбца видно и глазами: снимок плашки не должен оказаться пустым прямоугольником.
    /// </summary>
    /// <remarks>
    /// Через <see cref="RenderTargetBitmap"/>, а не запуском программы: окно вылезло бы поверх
    /// того, чем человек занят, а проверить надо именно отрисовку.
    /// </remarks>
    [Fact]
    public void The_panel_actually_draws()
    {
        var opaque = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(
                    LlmProvider.Venice,
                    [
                        new VeniceModelInfo { Id = "grok-4-6" },
                        new VeniceModelInfo { Id = "claude-sonnet-5" }
                    ]);
                panel.SetSelected("grok-4-6", "v2");
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                var target = new RenderTargetBitmap(
                    (int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                target.Render(panel);

                var pixels = new byte[target.PixelWidth * target.PixelHeight * 4];
                target.CopyPixels(pixels, target.PixelWidth * 4, 0);

                var count = 0;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 200)
                    {
                        count++;
                    }
                }

                return count;
            });

        Assert.True(opaque > 20000, $"плашка нарисовала слишком мало: {opaque}");
    }
}
