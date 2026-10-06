using System.Windows;
using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>
/// Оболочка настроек: навигация и страницы, которые создаются по первому требованию.
/// </summary>
/// <remarks>
/// <para>
/// Страница — отдельный UserControl, и пока её не попросили, её нет: ни разметки, ни контролов.
/// Попросить можно тремя путями — пунктом навигации, прогревом в простое
/// (<see cref="BuildNext"/>) и из кода окна (<see cref="Page{T}"/>), которому нужен её контрол.
/// Созданная страница сразу получает подписки окна через <see cref="PageCreated"/>, поэтому
/// разницы между «построили заранее» и «построили сейчас» для обработчиков нет.
/// </para>
/// <para>
/// Пункт навигации выбирается кодом, а не <c>IsChecked</c> в разметке: <c>Checked</c> пришёл бы
/// внутри <c>InitializeComponent</c>, до того как окно подписалось на создание страниц.
/// </para>
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    private readonly (RadioButton Nav, Type Type, Func<FrameworkElement> Create)[] _registry;
    private readonly Dictionary<Type, FrameworkElement> _pages = [];

    public SettingsView()
    {
        InitializeComponent();

        // В порядке навигации: в нём же прогрев строит страницы, а тесты обходят их.
        _registry =
        [
            (NavGeneral, typeof(SettingsGeneralPage), () => new SettingsGeneralPage()),
            (NavAppearance, typeof(SettingsAppearancePage), () => new SettingsAppearancePage()),
            (NavProfile, typeof(SettingsProfilePage), () => new SettingsProfilePage()),
            (NavModels, typeof(SettingsModelsPage), () => new SettingsModelsPage()),
            (NavPrompts, typeof(SettingsPromptsPage), () => new SettingsPromptsPage()),
            (NavInstructions, typeof(SettingsInstructionsPage), () => new SettingsInstructionsPage()),
            (NavAutomation, typeof(SettingsAutomationPage), () => new SettingsAutomationPage()),
            (NavSecurity, typeof(SettingsSecurityPage), () => new SettingsSecurityPage()),
            (NavKey, typeof(SettingsKeyPage), () => new SettingsKeyPage()),
            (NavData, typeof(SettingsDataPage), () => new SettingsDataPage()),
            (NavAbout, typeof(SettingsAboutPage), () => new SettingsAboutPage())
        ];

        // Подписка раньше, чем у окна: обработчик окна (NavSecurity_Checked и прочие) берёт
        // страницу, и к его вызову она уже обязана существовать и быть видимой.
        foreach (var (nav, _, _) in _registry)
        {
            nav.Checked += Nav_Checked;
        }
    }

    /// <summary>Страница создана и положена в хост — окно подписывает её контролы.</summary>
    internal event Action<FrameworkElement>? PageCreated;

    /// <summary>Страницу показали пунктом навигации — окно освежает её, если она устарела.</summary>
    internal event Action<FrameworkElement>? PageShown;

    /// <summary>Пункт навигации, с которого настройки открываются впервые.</summary>
    internal RadioButton DefaultNav => NavGeneral;

    /// <summary>Уже созданные страницы, в порядке навигации.</summary>
    internal IEnumerable<FrameworkElement> BuiltPages =>
        _registry.Where(entry => _pages.ContainsKey(entry.Type)).Select(entry => _pages[entry.Type]);

    /// <summary>Видимая страница или <c>null</c>, пока ни один пункт не выбран.</summary>
    internal FrameworkElement? CurrentPage =>
        _registry.FirstOrDefault(entry => entry.Nav.IsChecked == true) is { Nav: not null } current
            ? Ensure(current)
            : null;

    /// <summary>Страница нужного типа: созданная раньше или создаваемая сейчас.</summary>
    internal T Page<T>() where T : FrameworkElement =>
        (T)Ensure(_registry.First(entry => entry.Type == typeof(T)));

    /// <summary>Страница, если она уже создана; не создаёт её.</summary>
    internal T? Built<T>() where T : FrameworkElement =>
        _pages.TryGetValue(typeof(T), out var page) ? (T)page : null;

    /// <summary>Создана ли уже каждая страница.</summary>
    internal bool AllBuilt => _pages.Count == _registry.Length;

    /// <summary>Пункт навигации страницы.</summary>
    internal RadioButton NavOf(FrameworkElement page) => _registry.First(entry => entry.Type == page.GetType()).Nav;

    /// <summary>Создаёт следующую ещё не созданную страницу; <c>false</c> — созданы все.</summary>
    /// <remarks>
    /// Прогрев в простое зовёт по одному разу за порцию: страница — единица работы, и ввод ждёт
    /// не дольше одной страницы. Первой строится та, с которой настройки откроются, — выбранная
    /// или заводская: её постройка и есть разница между мгновенным и задумчивым открытием.
    /// </remarks>
    internal bool BuildNext()
    {
        var first = _registry.FirstOrDefault(entry => entry.Nav.IsChecked == true) is { Nav: not null } checkedEntry
            ? checkedEntry
            : _registry.First(entry => ReferenceEquals(entry.Nav, DefaultNav));
        foreach (var entry in new[] { first }.Concat(_registry))
        {
            if (!_pages.ContainsKey(entry.Type))
            {
                Ensure(entry);
                return !AllBuilt;
            }
        }

        return false;
    }

    private FrameworkElement Ensure((RadioButton Nav, Type Type, Func<FrameworkElement> Create) entry)
    {
        if (_pages.TryGetValue(entry.Type, out var page))
        {
            return page;
        }

        page = entry.Create();
        page.Visibility = entry.Nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // Страницы со своим кодом (ключи, защита, инструкции, автоматизация) — сами UserControl
        // без прокрутки-обёртки с проявлением; у остальных FadeOnShow стоит на их прокрутке.
        if (page is SettingsKeyPage or SettingsSecurityPage or SettingsInstructionsPage or SettingsAutomationPage)
        {
            UiMotion.SetFadeOnShow(page, true);
        }

        _pages[entry.Type] = page;

        // Перед крестиком, а не после: поверх страниц его держит ZIndex, а порядок обхода
        // клавишей Tab — порядок в дереве, и крестик остаётся последним.
        PageHost.Children.Insert(PageHost.Children.IndexOf(SettingsCloseButton), page);
        PageCreated?.Invoke(page);
        return page;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        FrameworkElement? shown = null;
        foreach (var entry in _registry)
        {
            if (ReferenceEquals(entry.Nav, sender))
            {
                shown = Ensure(entry);
                shown.Visibility = Visibility.Visible;
            }
            else if (_pages.TryGetValue(entry.Type, out var other))
            {
                other.Visibility = Visibility.Collapsed;
            }
        }

        if (shown is not null)
        {
            PageShown?.Invoke(shown);
        }
    }
}
