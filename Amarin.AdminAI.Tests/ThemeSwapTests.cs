using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Во что обходится переключение предустановленной темы.
/// </summary>
/// <remarks>
/// Раньше смена темы сносила и строила заново весь открытый чат: единственное, что за
/// <c>DynamicResource</c> не следует, — это картинки, присвоенные из кода, а ради них
/// перестраивалась вся лента с повторным разбором разметки каждого сообщения. На длинном
/// разговоре это и был тот самый лаг, на который пожаловались. Здесь закреплено и то, что
/// лента переживает переключение, и то, что картинки всё-таки обновляются.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ThemeSwapTests
{
    private readonly WpfFixture _wpf;

    public ThemeSwapTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatDisplayMessage Assistant() => new()
    {
        Role = "assistant",
        Id = "theme-swap-probe",
        CreatedAt = DateTime.Now,
        Text = "проба",
        ResolvedModelId = "grok-4-6",
        Status = AssistantStatus.Complete
    };

    /// <summary>Любая другая палитра: <see cref="ThemeManager.Apply"/> на своей же выходит сразу.</summary>
    private static AppTheme Other(AppTheme current) =>
        ThemeCatalog.Presets.First(preset => preset.Theme != current).Theme;

    [Fact]
    public void Changing_the_theme_keeps_the_messages_that_are_already_built()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var window = Window();
            var panel = (Panel)window.FindName("MessagesPanel")!;
            var original = ThemeManager.Current.Theme;
            var view = ChatMessageViews.CreateAssistant(window, Assistant(), new MessageActions());
            panel.Children.Add(view.Root);

            try
            {
                ThemeManager.Apply(Other(original));

                // До правки здесь стоял RenderSession(): панель очищалась и заполнялась заново
                // из сессии, то есть этот элемент просто исчезал.
                Assert.Contains(view.Root, panel.Children.Cast<UIElement>());
            }
            finally
            {
                panel.Children.Remove(view.Root);
                ThemeManager.Apply(original);
            }

            return null;
        });
    }

    [Fact]
    public void The_model_logo_is_re_read_from_the_new_palette()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var window = Window();
            var panel = (Panel)window.FindName("MessagesPanel")!;
            var original = ThemeManager.Current.Theme;

            ThemeManager.Apply(AppTheme.Dark);
            var view = ChatMessageViews.CreateAssistant(window, Assistant(), new MessageActions());
            panel.Children.Add(view.Root);
            panel.UpdateLayout();

            try
            {
                var dark = view.LogoImage.Source;
                Assert.NotNull(dark);

                ThemeManager.Apply(AppTheme.Light);

                // Логотипы светлой и тёмной раскладки — разные объекты; ImageSource из кода за
                // ресурсом не ходит, и без реестра картинок здесь осталась бы прежняя.
                Assert.NotSame(dark, view.LogoImage.Source);
            }
            finally
            {
                panel.Children.Remove(view.Root);
                ThemeManager.Apply(original);
            }

            return null;
        });
    }

    [Fact]
    public void Icons_and_logos_are_not_re_parsed_between_two_dark_themes()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var original = ThemeManager.Current.Theme;
            var dictionaries = Application.Current.Resources.MergedDictionaries;

            try
            {
                ThemeManager.Apply(AppTheme.Dark);
                var icons = dictionaries[1];
                var logos = dictionaries[2];
                var darkPalette = dictionaries[0];

                // Каждая подмена словаря — свой обход дерева с инвалидацией всех DynamicResource,
                // поэтому важно не только что́ в слоте оказалось, но и трогали ли слот вообще.
                var touched = new List<int>();
                void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
                    touched.Add(e.NewStartingIndex);

                ((INotifyCollectionChanged)dictionaries).CollectionChanged += OnChanged;
                try
                {
                    ThemeManager.Apply(AppTheme.Midnight);
                }
                finally
                {
                    ((INotifyCollectionChanged)dictionaries).CollectionChanged -= OnChanged;
                }

                // Светлота не изменилась — значит это те же самые 87 КБ BAML, и слоты иконок и
                // логотипов остаются нетронутыми.
                Assert.Contains(0, touched);
                Assert.DoesNotContain(1, touched);
                Assert.DoesNotContain(2, touched);
                Assert.Same(icons, dictionaries[1]);
                Assert.Same(logos, dictionaries[2]);
                Assert.NotSame(darkPalette, dictionaries[0]);

                ThemeManager.Apply(AppTheme.Light);
                Assert.NotSame(icons, dictionaries[1]);
                Assert.NotSame(logos, dictionaries[2]);

                // А палитра, к которой вернулись, берётся из кэша.
                ThemeManager.Apply(AppTheme.Dark);
                Assert.Same(darkPalette, dictionaries[0]);
            }
            finally
            {
                ThemeManager.Apply(original);
            }

            return null;
        });
    }

    [Fact]
    public void Applying_the_same_appearance_twice_changes_nothing()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var window = Window();
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var before = dictionaries[ThemeManager.OverrideSlot];

            using var manager = new AppearanceManager(window, new Grid());
            var settings = new AppearanceSettings();

            try
            {
                manager.Apply(settings);
                var written = dictionaries[ThemeManager.OverrideSlot];

                manager.Apply(settings);

                // Открытие настроек прогоняло оформление заново — с перекраской фона и подменой
                // словаря приложения, то есть с инвалидацией всех DynamicResource в дереве.
                Assert.Same(written, dictionaries[ThemeManager.OverrideSlot]);
            }
            finally
            {
                dictionaries[ThemeManager.OverrideSlot] = before;
            }

            return null;
        });
    }

    [Fact]
    public void A_theme_change_does_not_touch_empty_appearance_overrides()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var original = ThemeManager.Current.Theme;
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var before = dictionaries[ThemeManager.OverrideSlot];

            try
            {
                // Оформление выключено — надстроек нет ни при какой палитре, а слот всё равно
                // переписывался на каждую смену темы, и это был ещё один обход всего дерева.
                var written = dictionaries[ThemeManager.OverrideSlot];
                Assert.Empty(written);

                ThemeManager.Apply(Other(original));

                Assert.Same(written, dictionaries[ThemeManager.OverrideSlot]);
            }
            finally
            {
                dictionaries[ThemeManager.OverrideSlot] = before;
                ThemeManager.Apply(original);
            }

            return null;
        });
    }
}
