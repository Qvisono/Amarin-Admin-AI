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
[Trait(WpfCollection.Category, WpfCollection.Trait)]
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

    [Theory]
    [InlineData(ModelSlot.Router, true)]
    [InlineData(ModelSlot.SynGuard, true)]
    [InlineData(ModelSlot.Chat, false)]
    public void Decision_models_appear_and_keep_selection_only_in_the_router_catalog(ModelSlot slot, bool expected)
    {
        const string id = "openrouter:typesafe/jev";
        var rows = Shown(panel =>
        {
            panel.SetKeys(BothProviders());
            panel.SetCatalog(LlmProvider.OpenRouter, VeniceModelCatalog.ForSlot(
                [new VeniceModelInfo { Id = id, Type = "decisions" },
                 new VeniceModelInfo { Id = "openrouter:vendor/chat", Type = "text" }], slot));
            panel.SetSelected(id, "o1");
            panel.ShowSelectedProvider();
        }, panel => Models(panel).Items.OfType<ModelPickerRow>().Select(row => (row.Id, row.IsSelected)).ToList());
        Assert.Equal(expected, rows.Any(row => row.Id == id));
        if (expected)
        {
            Assert.True(rows.Single(row => row.Id == id).IsSelected);
        }
    }

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
                Part(panel, "ProviderItems").Children.OfType<RadioButton>()
                    .First(item => item.Content is TextBlock { Text: "OpenRouter" }).IsChecked = true;
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

    private static readonly ProviderEndpoint Star =
        new("ai.starimg.ru", "https://ai.starimg.ru/v1", OpenAiChat: true, AnthropicMessages: true);

    private static IReadOnlyList<ApiKeyEntry> WithServer() =>
    [
        new("a1", "Claude", "sk-ant-secret", ApiKeySource.Stored, true, LlmProvider.Anthropic),
        new("s1", "Star", "sk-star-secret", ApiKeySource.Stored, false, LlmProvider.Custom("a1b2c3"), Star)
    ];

    /// <summary>
    /// Провайдеры с ключами — сверху, в порядке встроенных, свой сервер — следом под своим
    /// названием; остальные ниже. Иначе у человека с одним ключом Anthropic он терялся бы среди
    /// дюжины бледных строк.
    /// </summary>
    [Fact]
    public void Providers_with_keys_come_first_and_a_server_goes_by_its_name()
    {
        var names = Shown(
            panel => panel.SetKeys(WithServer()),
            panel => Labels(Part(panel, "ProviderItems")).ToList());

        Assert.Equal("Anthropic", names[0]);
        Assert.Equal("ai.starimg.ru", names[1]);
        Assert.Equal(ProviderSpec.All.Count + 1, names.Count);
        Assert.Equal("Venice", names[2]);
    }

    /// <summary>
    /// Новый свой сервер добавляет кнопку, удалённый убирает свою, а живые кнопки остаются теми же
    /// объектами: пересоздание поднимало <c>Checked</c> внутри обработчика соседки.
    /// </summary>
    [Fact]
    public void A_server_comes_and_goes_without_rebuilding_the_others()
    {
        var (same, withServer, without) = Shown(
            panel => panel.SetKeys(BothProviders()),
            panel =>
            {
                var venice = Part(panel, "ProviderItems").Children.OfType<RadioButton>()
                    .Single(item => item.Content is TextBlock { Text: "Venice" });

                panel.SetKeys([.. BothProviders(), .. WithServer()]);
                panel.RebuildNow();
                var added = Labels(Part(panel, "ProviderItems")).Contains("ai.starimg.ru");

                panel.SetKeys(BothProviders());
                panel.RebuildNow();
                var removed = !Labels(Part(panel, "ProviderItems")).Contains("ai.starimg.ru");

                var still = Part(panel, "ProviderItems").Children.OfType<RadioButton>()
                    .Single(item => item.Content is TextBlock { Text: "Venice" });
                return (ReferenceEquals(venice, still), added, removed);
            });

        Assert.True(same, "кнопку Venice пересоздали");
        Assert.True(withServer);
        Assert.True(without);
    }

    /// <summary>
    /// Показанный свой сервер исчез вместе с ключом — столбец переходит к первому провайдеру, а не
    /// остаётся ничьим.
    /// </summary>
    [Fact]
    public void A_shown_server_that_disappears_hands_over_the_column()
    {
        var shown = Shown(
            panel =>
            {
                panel.SetKeys(WithServer());
                panel.SetSelected("custom-a1b2c3:gpt-6-sol", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.RebuildNow();
                return panel.ShownProvider;
            });

        Assert.Equal(LlmProvider.Venice, shown);
    }

    /// <summary>«Добавить ключ» у бледного провайдера называет его — окно ключа откроется на нём.</summary>
    [Fact]
    public void Add_key_names_the_shown_provider()
    {
        var asked = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetSelected("openai:gpt-6-sol", null);
                panel.ShowSelectedProvider();
            },
            panel =>
            {
                LlmProvider? provider = null;
                panel.AddKeyRequested += (_, value) => provider = value;
                var button = (Button)panel.FindName("AddKeyButton")!;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
                return provider;
            });

        Assert.Equal(LlmProvider.OpenAI, asked);
    }

    /// <summary>
    /// Новая попытка загрузки не прячет уже загруженные модели.
    /// </summary>
    [Fact]
    public void A_new_attempt_keeps_the_loaded_list()
    {
        var count = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetCatalog(LlmProvider.Venice, [new VeniceModelInfo { Id = "grok-4-6" }]);
                panel.SetSelected("grok-4-6", null);
                panel.ShowSelectedProvider();
                panel.ShowLoading(LlmProvider.Venice);
            },
            panel => Models(panel).Items.Count);

        Assert.Equal(1, count);
    }

    /// <summary>
    /// Отказ показан словами и кнопкой «Повторить», а она просит хозяина загрузить список снова.
    /// </summary>
    /// <remarks>
    /// Повторное нажатие на уже выбранного провайдера ничего не делает, и до 1.34.0 повторить
    /// загрузку из открытой плашки было нечем.
    /// </remarks>
    [Fact]
    public void A_refusal_offers_to_retry()
    {
        var (text, retry, asked) = Shown(
            panel =>
            {
                panel.SetKeys(BothProviders());
                panel.SetSelected("openrouter:openai/gpt-5", null);
                panel.ShowSelectedProvider();
                panel.SetCatalogError(LlmProvider.OpenRouter, "OpenRouter отказал в доступе (403).");
            },
            panel =>
            {
                LlmProvider? requested = null;
                panel.ProviderShown += (_, provider) => requested = provider;

                var status = ((TextBlock)panel.FindName("ModelStatus")!).Text;
                var button = (Button)panel.FindName("RetryButton")!;
                var visible = button.Visibility;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return (status, visible, requested);
            });

        Assert.Equal("OpenRouter отказал в доступе (403).", text);
        Assert.Equal(Visibility.Visible, retry);
        Assert.Equal(LlmProvider.OpenRouter, asked);
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
