using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Автоматизация»: рецепты — сохранённые вызовы инструментов, которые повторяют без
/// модели (C7), — расписание, удалённые компьютеры и серверы MCP вкладками.
/// </summary>
/// <remarks>
/// Три состояния на одной странице — список, редактор, запуск, — а не модалки: модалки живут в
/// главном окне, а здесь всей высоты страницы едва хватает на аргументы и вывод. Вопрос о
/// записи при запуске задаёт обычное окно подтверждения поверх настроек: рецепт идёт через тот
/// же шлюз, что и вызов модели.
/// </remarks>
public partial class SettingsAutomationPage : UserControl
{
    /// <summary>Сколько вывода показывать: дальше это уже не читают, а поле тормозит.</summary>
    internal const int OutputLimit = 20_000;

    private AppServices? _services;
    private IReadOnlyList<Recipe> _all = [];
    private Recipe? _editing;
    private Recipe? _running;
    private CancellationTokenSource? _runCancel;

    /// <summary>Сколько рецептов, прежде чем над списком появляется поиск: на трёх-четырёх он только мешает.</summary>
    internal const int SearchFrom = 6;

    public SettingsAutomationPage()
    {
        InitializeComponent();
        SchedulePane.EditingChanged += ShowHeader;
        MachinesPane.EditingChanged += ShowHeader;
        McpPane.EditingChanged += ShowHeader;
    }

    /// <summary>У редактора вкладки своя шапка «‹» — заголовок страницы и вкладки над ним лишние.</summary>
    private void ShowHeader(bool editing)
    {
        var visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        PageTitleText.Visibility = visibility;
        TabsRow.Visibility = visibility;
    }

    /// <summary>
    /// «Через агента»: хозяин открывает новый чат с этим текстом в поле. Не отправляет —
    /// человек видит постановку и решает сам.
    /// </summary>
    internal event Action<string>? AgentRequested;

    internal void Attach(AppServices services)
    {
        _services = services;
        SchedulePane.Attach(services);
        MachinesPane.Attach(services);
        McpPane.Attach(services);
    }

    /// <summary>Вкладка «Компьютеры» — для хозяина: список машин меняет выбор цели в чате.</summary>
    internal MachinesPanel Machines => MachinesPane;

    /// <summary>Вкладка «Расписание» — для хозяина: «Запустить сейчас», переход в чат прогона.</summary>
    internal SchedulePanel Schedule => SchedulePane;

    /// <summary>Прогон закончился — перечитать список, если он на экране.</summary>
    internal void RefreshSchedule()
    {
        if (SchedulePane.IsVisible)
        {
            SchedulePane.Refresh();
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (McpPane is null || RecipesContent is null)
        {
            // Checked у заранее отмеченной вкладки приходит из InitializeComponent, до полей.
            return;
        }

        CancelRun();
        ShowHeader(false);
        ShowTabContent();
        UiMotion.Enter(ActiveTabContent(), dy: 4, milliseconds: 120);
    }

    private void ShowTabContent()
    {
        RecipesContent.Visibility = RecipesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SchedulePane.Visibility = ScheduleTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MachinesPane.Visibility = MachinesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        McpPane.Visibility = McpTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SchedulePane.Visibility == Visibility.Visible)
        {
            SchedulePane.Load();
        }
        else if (MachinesPane.Visibility == Visibility.Visible)
        {
            MachinesPane.Load();
        }
        else if (McpPane.Visibility == Visibility.Visible)
        {
            McpPane.Load();
        }
    }

    private FrameworkElement ActiveTabContent() =>
        ScheduleTab.IsChecked == true ? SchedulePane
        : MachinesTab.IsChecked == true ? MachinesPane
        : McpTab.IsChecked == true ? McpPane
        : RecipesContent;

    /// <summary>Перечитывает рецепты и показывает список. Зовётся при входе на страницу.</summary>
    internal void Load()
    {
        CancelRun();
        ShowList();
        ShowHeader(false);
        Detached.Run(ReloadAsync(), "recipes_load");
        ShowTabContent();
    }

    /// <summary>Открывает вкладку «Расписание».</summary>
    internal void ShowScheduleTab() => ScheduleTab.IsChecked = true;

    /// <summary>Открывает вкладку «Компьютеры» — из выбора цели чата («Управлять…»).</summary>
    internal void ShowMachinesTab() => MachinesTab.IsChecked = true;

    /// <summary>Форма запуска рецепта — для команды <c>/recipe</c> (D9). False — такого нет.</summary>
    internal bool Run(string id)
    {
        if (_services?.Recipes.Find(id) is not { } recipe)
        {
            return false;
        }

        RecipesTab.IsChecked = true;
        OpenRun(recipe);
        return true;
    }

    /// <summary>Открывает редактор рецепта — после «Сохранить как рецепт» в журнале.</summary>
    internal void Edit(string id)
    {
        RecipesTab.IsChecked = true;
        if (_services?.Recipes.Find(id) is { } recipe)
        {
            OpenEditor(recipe);
        }
    }

    private async Task ReloadAsync()
    {
        if (_services is null)
        {
            return;
        }

        var library = _services.Recipes;
        _all = await Task.Run(library.All).ConfigureAwait(true);
        ApplyFilter();
    }

    // ───────────────────────── список ─────────────────────────

    internal static IReadOnlyList<RecipeRow> Filter(IEnumerable<Recipe> recipes, string? query)
    {
        var text = query?.Trim() ?? "";
        return recipes
            .Where(recipe => text.Length == 0 ||
                             recipe.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                             recipe.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                             recipe.Tool.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(recipe => new RecipeRow(recipe))
            .ToList();
    }

    private void ApplyFilter()
    {
        SearchFrame.Visibility = _all.Count >= SearchFrom || SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = Filter(_all, SearchBox.Text);
        RecipeItems.ItemsSource = rows;
        EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NothingFoundText.Visibility = _all.Count > 0 && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipeItems.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ShowList()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        RunPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private Recipe? RecipeOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _all.FirstOrDefault(recipe => recipe.Id == id) : null;

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (RecipeOf(sender) is { } recipe)
        {
            OpenEditor(recipe);
        }
    }

    private void RunRow_Click(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри карточки-кнопки: без этого щелчок дошёл бы и до карточки и открыл правку.
        e.Handled = true;
        if (RecipeOf(sender) is { } recipe)
        {
            OpenRun(recipe);
        }
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (RecipeOf(sender) is { } recipe)
        {
            await DeleteAsync(recipe);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(new Recipe { Tool = "run_powershell", Arguments = "{\n  \"command\": \"\"\n}" });

    // ───────────────────────── редактор ─────────────────────────

    private List<ParameterValue> _defaults = [];

    private void OpenEditor(Recipe recipe)
    {
        CancelRun();
        _editing = recipe;
        EditorTitle.SetResourceReference(TextBlock.TextProperty,
            string.IsNullOrEmpty(recipe.Id) ? "S.Recipe.NewTitle" : "S.Recipe.EditTitle");
        NameBox.Text = recipe.Name;
        ToolBox.Text = recipe.Tool;
        DescriptionBox.Text = recipe.Description;
        _defaults = recipe.Parameters.Select(parameter => new ParameterValue(parameter.Name, parameter.Default)).ToList();
        ArgumentsBox.Text = recipe.Arguments;
        RefreshDefaults();
        DeleteButton.Visibility = string.IsNullOrEmpty(recipe.Id) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;

        ListPane.Visibility = Visibility.Collapsed;
        RunPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    private void ArgumentsBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDefaults();

    /// <summary>Строки значений по умолчанию — по параметрам, что сейчас есть в аргументах.</summary>
    private void RefreshDefaults()
    {
        var names = RecipeTemplate.Placeholders(ArgumentsBox.Text);
        if (names.SequenceEqual(_defaults.Select(value => value.Name)))
        {
            DefaultsPanel.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (DefaultsList.ItemsSource is null && names.Count > 0)
            {
                DefaultsList.ItemsSource = _defaults;
            }

            return;
        }

        var known = _defaults.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        _defaults = names.Select(name => new ParameterValue(name, known.GetValueOrDefault(name, ""))).ToList();
        DefaultsList.ItemsSource = _defaults;
        DefaultsPanel.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Что не так с черновиком рецепта; null — можно сохранять.</summary>
    internal static string? Validate(string name, string tool, string arguments)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Loc.Get("S.Recipe.NeedName");
        }

        if (!ToolCatalog.All.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Loc.Format("S.Recipe.UnknownTool", tool.Trim());
        }

        if (!RecipeRules.IsRunnable(tool))
        {
            return Loc.Format("S.Recipe.NotRunnable", tool.Trim());
        }

        return RecipeTemplate.IsValid(arguments) ? null : Loc.Get("S.Recipe.BadArguments");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editing is null)
        {
            return;
        }

        if (Validate(NameBox.Text, ToolBox.Text, ArgumentsBox.Text) is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _editing.Name = NameBox.Text.Trim();
        _editing.Tool = ToolCatalog.All.First(tool => tool.Equals(ToolBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        _editing.Description = DescriptionBox.Text.Trim();
        _editing.Arguments = ArgumentsBox.Text;
        _editing.Parameters = _defaults.Select(value => new RecipeParameter(value.Name, value.Value)).ToList();

        if (_services.Recipes.Save(_editing) is null)
        {
            ErrorText.Text = Loc.Get("S.Recipe.SaveFailed");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        StatusText.Visibility = Visibility.Collapsed;
        Load();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is { Id.Length: > 0 } recipe)
        {
            await DeleteAsync(recipe);
        }
    }

    private async Task DeleteAsync(Recipe recipe)
    {
        if (_services is null || Window.GetWindow(this) is not MainWindow window)
        {
            return;
        }

        var confirmed = await window.ShowNoticeAsync(
            Loc.Get("S.Recipe.DeleteTitle"),
            Loc.Format("S.Recipe.DeleteText", recipe.Name),
            Loc.Get("S.Common.Delete"),
            Loc.Get("S.Common.Cancel"),
            NoticeTone.Danger);
        if (!confirmed)
        {
            return;
        }

        if (!_services.Recipes.Delete(recipe.Id))
        {
            ShowStatus(Loc.Get("S.Recipe.DeleteFailed"));
        }

        Load();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        CancelRun();
        ShowList();
    }

    // ───────────────────────── запуск ─────────────────────────

    private List<ParameterValue> _values = [];

    private void OpenRun(Recipe recipe)
    {
        CancelRun();
        _running = recipe;
        RunTitle.Text = recipe.Name;
        RunTechnical.Text = RecipeRow.TechnicalLine(recipe);
        _values = recipe.Parameters.Select(parameter => new ParameterValue(parameter.Name, parameter.Default)).ToList();
        RunParameters.ItemsSource = _values;
        RunParametersPanel.Visibility = _values.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunResultHost.Visibility = Visibility.Collapsed;
        RunOutputBox.Text = "";
        RunButton.IsEnabled = true;
        ViaAgentButton.IsEnabled = true;

        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Collapsed;
        RunPane.Visibility = Visibility.Visible;
    }

    private Dictionary<string, string> CurrentValues() =>
        _values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _running is not { } recipe)
        {
            return;
        }

        CancelRun();
        var cancel = _runCancel = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        RunResultHost.Visibility = Visibility.Visible;
        RunStatusText.Text = Loc.Get("S.Recipe.Running");
        RunStatusText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        RunOutputBox.Text = "";

        try
        {
            var outcome = await _services.RecipeRunner.RunAsync(recipe, CurrentValues(), cancel.Token);
            if (!ReferenceEquals(cancel, _runCancel))
            {
                return;
            }

            RunStatusText.Text = Loc.Get(outcome.Result.Success
                ? "S.Recipe.Done"
                : outcome.Ran ? "S.Recipe.Failed" : "S.Recipe.NotRun");
            RunStatusText.SetResourceReference(TextBlock.ForegroundProperty,
                outcome.Result.Success ? "Status.Success" : "Status.Danger");
            RunOutputBox.Text = ClipOutput(outcome.Result.Output);

            if (outcome.Ran)
            {
                recipe.LastRun = DateTime.Now;
                _services.Recipes.Save(recipe);
            }
        }
        catch (OperationCanceledException)
        {
            // Ушли со страницы или запустили заново — показывать нечего.
        }
        finally
        {
            if (ReferenceEquals(cancel, _runCancel))
            {
                RunButton.IsEnabled = true;
            }
        }
    }

    internal static string ClipOutput(string output) =>
        output.Length <= OutputLimit ? output : output[..OutputLimit] + Environment.NewLine + "…";

    private void ViaAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_running is not { } recipe)
        {
            return;
        }

        var arguments = RecipeTemplate.Fill(recipe.Arguments, CurrentValues());
        AgentRequested?.Invoke(AgentPrompt(recipe, arguments));
    }

    /// <summary>
    /// Постановка агенту: что за рецепт и какой вызов в нём. Агент идёт через свои вопросы —
    /// рецепт не даёт ему разрешений.
    /// </summary>
    internal static string AgentPrompt(Recipe recipe, JsonElement? arguments) =>
        Loc.Format(
            "S.Recipe.AgentPrompt",
            recipe.Name,
            recipe.Tool,
            arguments is { } filled
                ? JsonSerializer.Serialize(filled, new JsonSerializerOptions { WriteIndented = true })
                : recipe.Arguments);

    private void EditFromRun_Click(object sender, RoutedEventArgs e)
    {
        if (_running is { } recipe)
        {
            OpenEditor(recipe);
        }
    }

    /// <summary>Уход со страницы снимает ожидание вопроса и сам запуск.</summary>
    internal void CancelRun()
    {
        var cancel = _runCancel;
        _runCancel = null;
        if (cancel is null)
        {
            return;
        }

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancel.Dispose();
    }
}

/// <summary>Значение параметра в поле формы.</summary>
internal sealed class ParameterValue(string name, string value) : INotifyPropertyChanged
{
    private string _value = value;

    public string Name { get; } = name;

    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Карточка рецепта в списке.</summary>
internal sealed class RecipeRow(Recipe recipe)
{
    public string Id { get; } = recipe.Id;

    public string Name { get; } = recipe.Name;

    public string Description { get; } = recipe.Description;

    public Visibility DescriptionVisibility =>
        string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

    public string Technical { get; } = TechnicalLine(recipe);

    public IReadOnlyList<string> Parameters { get; } = recipe.Parameters.Select(parameter => "{{" + parameter.Name + "}}").ToList();

    /// <summary>«инструмент · действие» — чтобы по карточке было видно, что рецепт делает.</summary>
    internal static string TechnicalLine(Recipe recipe)
    {
        var action = RecipeTemplate.Fill(recipe.Arguments, new Dictionary<string, string>()) is { } arguments
            ? Tools.DangerousActionGuard.ActionOf(arguments)
            : "";
        return string.IsNullOrEmpty(action) ? recipe.Tool : recipe.Tool + " · " + action;
    }
}
