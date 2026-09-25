using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Инструкции»: список инструкций пользователя и их редактор.
/// </summary>
/// <remarks>
/// Отдельным <see cref="UserControl"/>, как страницы Key и Info: разметка главного окна и так
/// на три тысячи строк.
/// <para>
/// Редактор живёт в самой странице, а не модалкой. Модалки обязаны жить в главном окне (второе
/// окно рисовалось бы в системном масштабе мимо <see cref="UiScale"/>), а у страницы есть вся
/// её высота — длинному тексту инструкции она нужнее, чем карточке на полэкрана. Спрашивать
/// «удалить?» и «отменить правки?» страница всё же ходит к окну — его оверлеем.
/// </para>
/// <para>
/// Контролы нигде не читаются как состояние: правда лежит в <see cref="InstructionLibrary"/>,
/// и список после каждой записи перестраивается из неё.
/// </para>
/// </remarks>
public partial class SettingsInstructionsPage : UserControl
{
    /// <summary>С этого числа инструкций над списком появляется поиск.</summary>
    internal const int SearchThreshold = 6;

    /// <summary>Сколько триггеров помещается в карточку; остальные — «+N».</summary>
    internal const int CardTriggerLimit = 5;

    private AppServices? _services;
    private IReadOnlyList<Instruction> _all = [];

    /// <summary>Правится эта инструкция; null — заводится новая.</summary>
    private Instruction? _editing;

    private List<string> _triggers = [];

    /// <summary>Черновик в момент открытия редактора — с ним сверяется «есть ли правки».</summary>
    private DraftState _baseline = DraftState.Empty;

    /// <summary>Поля заполняются из кода: их <c>TextChanged</c> в это время правкой не считается.</summary>
    private bool _filling;

    public SettingsInstructionsPage()
    {
        InitializeComponent();

        SmoothScroll.SetIsEnabled(ListScroll, true);
        SmoothScroll.SetDragScroll(ListScroll, true);

        // Список бывает и в одну карточку: тогда листать нечего, и без резинки страница под
        // зажатой кнопкой казалась бы неживой рядом с длинными соседками.
        SmoothScroll.SetBounceWhenShort(ListScroll, true);
        NameBox.MaxLength = InstructionLibrary.NameLimit;
        UpdateCounter();
    }

    /// <summary>Открыт ли редактор. Для окна и тестов.</summary>
    internal bool IsEditing => EditorPane.Visibility == Visibility.Visible;

    /// <summary>Идентификатор правящейся инструкции; null у новой и при закрытом редакторе.</summary>
    internal string? EditingId => IsEditing ? _editing?.Id : null;

    /// <summary>Ставится главным окном, когда службы уже собраны.</summary>
    internal void Attach(AppServices services) => _services = services;

    /// <summary>
    /// Зовётся при заходе именно на эту страницу: обход папки ради страницы, куда человек
    /// в этом запуске может и не зайти, на открытие настроек не вешается.
    /// </summary>
    internal void Activate() => Reload();

    /// <summary>
    /// Профиль сменился или импорт заменил данные: черновик принадлежал прежней папке, и
    /// сохранить его теперь значило бы записать чужую инструкцию в новый профиль.
    /// </summary>
    internal void ResetForProfile()
    {
        CloseEditor();
        SearchBox.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
        if (_services is not null)
        {
            Reload();
        }
    }

    /// <summary>
    /// Открывает инструкцию в редакторе — по отметке «по инструкции» под ответом модели.
    /// </summary>
    internal void OpenInstruction(string id)
    {
        Reload();
        var found = _all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return;
        }

        if (IsEditing && string.Equals(_editing?.Id, found.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ConfirmLeave(() => OpenEditor(found));
    }

    // ───────────────────────── список ─────────────────────────

    private void Reload()
    {
        if (_services is null)
        {
            return;
        }

        _all = _services.Instructions.Snapshot();
        RefreshList();
    }

    private void RefreshList()
    {
        var query = SearchBox.Text.Trim();
        var shown = query.Length == 0
            ? _all
            : _all.Where(item => Matches(item, query)).ToList();

        InstructionItems.ItemsSource = shown.Select(InstructionRow.From).ToList();

        var any = _all.Count > 0;
        var searchable = _all.Count >= SearchThreshold || query.Length > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SearchFrame.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        CountText.Visibility = any && !searchable ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = Loc.Format("S.Instructions.Count", _all.Count, _all.Count(item => item.Enabled));
        NothingFoundText.Visibility = any && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Поиск по названию, триггерам и тексту: человек помнит то одно, то другое.</summary>
    internal static bool Matches(Instruction instruction, string query) =>
        instruction.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        instruction.Triggers.Any(trigger => trigger.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
        instruction.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private Instruction? FindLoaded(object sender) =>
        sender is FrameworkElement { Tag: string id }
            ? _all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (FindLoaded(sender) is { } instruction)
        {
            OpenEditor(instruction);
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        // Щелчок тумблера всплыл бы до карточки и открыл редактор.
        e.Handled = true;
        if (_services is null || sender is not CheckBox { Tag: string id } toggle)
        {
            return;
        }

        if (_services.Instructions.SetEnabled(id, toggle.IsChecked == true) is null)
        {
            ShowStatus(Loc.Get("S.Instructions.SaveFailed"), error: true);
        }

        Reload();
    }

    private void ExportRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (FindLoaded(sender) is { } instruction)
        {
            Export(instruction);
        }
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (FindLoaded(sender) is { } instruction)
        {
            AskDelete(instruction);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("S.Instructions.ImportFilter"),
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var result = _services.Instructions.Import(dialog.FileNames);
        Reload();
        ShowStatus(
            result.Skipped == 0
                ? Loc.Format("S.Instructions.ImportDoneAll", result.Imported.Count)
                : Loc.Format("S.Instructions.ImportDone", result.Imported.Count, result.Skipped),
            error: result.Imported.Count == 0);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var folder = _services.Instructions.Folder;
            Directory.CreateDirectory(folder);
            using var process = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                // Кавычки обязательны: в пути бывают пробелы, а explorer режет аргумент по ним.
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException)
        {
            ShowStatus(Loc.Get("S.Instructions.FolderFailed"), error: true);
        }
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Status.Danger" : "Text.Muted");
        StatusText.Visibility = Visibility.Visible;
    }

    // ───────────────────────── редактор ─────────────────────────

    private void OpenEditor(Instruction? instruction)
    {
        _editing = instruction;
        _filling = true;
        try
        {
            NameBox.Text = instruction?.Name ?? "";
            BodyBox.Text = instruction?.Text ?? "";
            EnabledToggle.IsChecked = instruction?.Enabled ?? true;
            TriggerInput.Text = "";
            _triggers = [.. instruction?.Triggers ?? []];
            RebuildChips();
        }
        finally
        {
            _filling = false;
        }

        EditorTitle.SetResourceReference(
            TextBlock.TextProperty,
            instruction is null ? "S.Instructions.NewTitle" : "S.Instructions.EditTitle");
        DeleteButton.Visibility = instruction is null ? Visibility.Collapsed : Visibility.Visible;
        HideEditorMessage();
        UpdatePlaceholders();
        UpdateCounter();
        _baseline = CurrentDraft();

        ListScroll.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        BodyBox.ScrollToHome();

        // Фокус — после того как редактор разложится: в свёрнутое поле он не встанет.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (instruction is null)
                {
                    NameBox.Focus();
                }
            }));
    }

    private void CloseEditor()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Visible;
        _editing = null;
        HideEditorMessage();
    }

    /// <summary>Уходит из редактора, спросив, если в нём есть несохранённые правки.</summary>
    private void ConfirmLeave(Action then)
    {
        if (!IsEditing || !IsDirty)
        {
            then();
            return;
        }

        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.AskConfirm(
                Loc.Get("S.Instructions.DiscardConfirm"),
                Loc.Get("S.Instructions.DiscardDesc"),
                then);
            return;
        }

        then();
    }

    internal bool IsDirty => CurrentDraft() != _baseline;

    private DraftState CurrentDraft() => new(
        NameBox.Text.Trim(),
        string.Join("\n", _triggers) + "\n" + TriggerInput.Text.Trim(),
        BodyBox.Text.Trim(),
        EnabledToggle.IsChecked == true);

    private void Back_Click(object sender, RoutedEventArgs e) => ConfirmLeave(CloseEditor);

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ConfirmLeave(CloseEditor);
            return;
        }

        // Enter в названии ведёт к триггерам, а не сохраняет: у инструкции без текста
        // сохранять нечего, а человек привык идти по полям сверху вниз.
        if (e.Key == Key.Enter && ReferenceEquals(e.OriginalSource, NameBox))
        {
            e.Handled = true;
            TriggerInput.Focus();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>
    /// Проверяет черновик и пишет его в библиотеку.
    /// </summary>
    /// <returns>Записалось ли. Для тестов.</returns>
    internal bool Save()
    {
        if (_services is null)
        {
            return false;
        }

        CommitTriggerInput();
        if (ValidateDraft(out var name) is { } problem)
        {
            ShowEditorMessage(problem, error: true);
            return false;
        }

        var draft = (_editing ?? new Instruction { CreatedAt = DateTime.Now }) with
        {
            Name = name,
            Triggers = [.. _triggers],
            Text = BodyBox.Text,
            Enabled = EnabledToggle.IsChecked == true
        };

        if (_services.Instructions.Save(draft) is null)
        {
            ShowEditorMessage(Loc.Get("S.Instructions.SaveFailed"), error: true);
            return false;
        }

        CloseEditor();
        Reload();
        return true;
    }

    /// <summary>Текст ошибки черновика или null, если его можно сохранять.</summary>
    private string? ValidateDraft(out string name)
    {
        name = InstructionLibrary.TrimName(NameBox.Text);
        if (name.Length == 0)
        {
            NameBox.Focus();
            return Loc.Get("S.Instructions.NameEmpty");
        }

        var text = BodyBox.Text.Trim();
        if (text.Length == 0)
        {
            BodyBox.Focus();
            return Loc.Get("S.Instructions.TextEmpty");
        }

        if (text.Length > InstructionLibrary.TextLimit)
        {
            BodyBox.Focus();
            return Loc.Format("S.Instructions.TextTooLong", FormatNumber(InstructionLibrary.TextLimit));
        }

        var candidate = name;
        var editingId = _editing?.Id;
        var taken = _services?.Instructions.Snapshot().Any(other =>
            !string.Equals(other.Id, editingId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(other.Name, candidate, StringComparison.OrdinalIgnoreCase)) == true;
        if (taken)
        {
            NameBox.Focus();
            return Loc.Get("S.Instructions.NameTaken");
        }

        return null;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is { } instruction)
        {
            AskDelete(instruction);
        }
    }

    private void AskDelete(Instruction instruction)
    {
        if (Window.GetWindow(this) is not MainWindow owner)
        {
            return;
        }

        owner.AskConfirm(
            Loc.Format("S.Instructions.DeleteConfirm", instruction.Name),
            Loc.Get("S.Instructions.DeleteDesc"),
            () => Delete(instruction));
    }

    private void Delete(Instruction instruction)
    {
        if (_services is null)
        {
            return;
        }

        if (!_services.Instructions.Delete(instruction.Id))
        {
            if (IsEditing)
            {
                ShowEditorMessage(Loc.Get("S.Instructions.DeleteFailed"), error: true);
            }
            else
            {
                ShowStatus(Loc.Get("S.Instructions.DeleteFailed"), error: true);
            }

            return;
        }

        // Редактор мог остаться открытым на удалённой инструкции.
        if (IsEditing && string.Equals(_editing?.Id, instruction.Id, StringComparison.OrdinalIgnoreCase))
        {
            CloseEditor();
        }

        Reload();
    }

    /// <summary>
    /// Экспорт из редактора берёт черновик как он есть: человек часто сохраняет в файл именно
    /// то, что только что написал, и заставлять его сперва сохранять — лишний шаг.
    /// </summary>
    private void ExportEditor_Click(object sender, RoutedEventArgs e)
    {
        CommitTriggerInput();
        var name = InstructionLibrary.TrimName(NameBox.Text);
        if (name.Length == 0 || BodyBox.Text.Trim().Length == 0)
        {
            ShowEditorMessage(
                Loc.Get(name.Length == 0 ? "S.Instructions.NameEmpty" : "S.Instructions.TextEmpty"),
                error: true);
            return;
        }

        Export((_editing ?? new Instruction { CreatedAt = DateTime.Now }) with
        {
            Name = name,
            Triggers = [.. _triggers],
            Text = BodyBox.Text,
            Enabled = EnabledToggle.IsChecked == true
        });
    }

    private void Export(Instruction instruction)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName(instruction.Name) + InstructionLibrary.Extension,
            DefaultExt = InstructionLibrary.Extension,
            Filter = Loc.Get("S.Instructions.ExportFilter"),
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var saved = InstructionLibrary.Export(instruction, dialog.FileName);
        var message = saved
            ? Loc.Format("S.Instructions.ExportDone", dialog.FileName)
            : Loc.Get("S.Instructions.ExportFailed");
        if (IsEditing)
        {
            ShowEditorMessage(message, error: !saved);
        }
        else
        {
            ShowStatus(message, error: !saved);
        }
    }

    /// <summary>Имя файла из названия: без знаков, которых Windows в имени не терпит.</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
            .ToHashSet();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "instruction" : cleaned;
    }

    private void ShowEditorMessage(string text, bool error)
    {
        ErrorText.Text = text;
        ErrorText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Status.Danger" : "Text.Muted");
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideEditorMessage() => ErrorText.Visibility = Visibility.Collapsed;

    private void Draft_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling)
        {
            HideEditorMessage();
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        if (!_filling)
        {
            HideEditorMessage();
        }
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        UpdateCounter();
        if (!_filling)
        {
            HideEditorMessage();
        }
    }

    private void UpdatePlaceholders()
    {
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyPlaceholder.Visibility = BodyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TriggerPlaceholder.Visibility = TriggerInput.Text.Length == 0 && _triggers.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Счётчик знаков. Поле не режет текст по пределу: вставленная длинная инструкция молча
    /// потеряла бы хвост. Вместо этого счётчик краснеет, а сохранение объясняет, что сократить.
    /// </summary>
    private void UpdateCounter()
    {
        var length = BodyBox.Text.Length;
        CounterText.Text = FormatNumber(length) + " / " + FormatNumber(InstructionLibrary.TextLimit);
        var key = length > InstructionLibrary.TextLimit
            ? "Status.Danger"
            : length > InstructionLibrary.TextLimit * 9 / 10
                ? "Status.Warning"
                : "Text.Faint";
        CounterText.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private static string FormatNumber(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ───────────────────────── триггеры ─────────────────────────

    /// <summary>Добавляет триггеры, отбрасывая повторы и лишние сверх предела.</summary>
    /// <returns>Прибавилось ли хоть что-то.</returns>
    internal bool AddTriggers(IEnumerable<string> raw)
    {
        var merged = InstructionLibrary.NormalizeTriggers(_triggers.Concat(raw));
        var added = merged.Count > _triggers.Count;
        _triggers = [.. merged];
        RebuildChips();
        return added;
    }

    internal IReadOnlyList<string> DraftTriggers => _triggers;

    private void RemoveTrigger(string trigger)
    {
        _triggers.RemoveAll(item => string.Equals(item, trigger, StringComparison.Ordinal));
        RebuildChips();
        TriggerInput.Focus();
    }

    /// <summary>Добавляет набранное в поле, если там что-то есть.</summary>
    private void CommitTriggerInput()
    {
        var text = TriggerInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AddTriggers([text]);
        TriggerInput.Text = "";
    }

    private void RebuildChips()
    {
        // Пилюли стоят перед полем ввода в том же WrapPanel: так поле всегда последнее,
        // и набранное слово встаёт туда, где его ждут.
        for (var i = TriggerPanel.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(TriggerPanel.Children[i], TriggerInputHost))
            {
                TriggerPanel.Children.RemoveAt(i);
            }
        }

        var index = 0;
        foreach (var trigger in _triggers)
        {
            TriggerPanel.Children.Insert(index++, BuildChip(trigger));
        }

        var full = _triggers.Count >= InstructionLibrary.TriggerLimit;
        TriggerHint.Text = full
            ? Loc.Format("S.Instructions.TriggersFull", InstructionLibrary.TriggerLimit)
            : Loc.Get("S.Instructions.TriggersHint");
        TriggerHint.SetResourceReference(TextBlock.ForegroundProperty, full ? "Status.Warning" : "Text.Faint");
        UpdatePlaceholders();
    }

    private Border BuildChip(string trigger)
    {
        var label = new TextBlock
        {
            Text = trigger,
            FontSize = 11.5,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

        var remove = new Button
        {
            Style = (Style)FindResource("ChipRemove"),
            Tag = trigger,
            VerticalAlignment = VerticalAlignment.Center
        };
        remove.SetResourceReference(ToolTipProperty, "S.Common.Remove");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            RemoveTrigger(trigger);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(remove);

        return new Border
        {
            Style = (Style)FindResource("EditorChip"),
            Child = row,
            Cursor = Cursors.Arrow
        };
    }

    private void TriggerInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                CommitTriggerInput();
                break;

            // Tab добавляет набранное и уходит дальше как обычно — отнимать у него переход
            // по полям незачем.
            case Key.Tab:
                CommitTriggerInput();
                break;

            case Key.Back when TriggerInput.Text.Length == 0 && _triggers.Count > 0:
                e.Handled = true;
                _triggers.RemoveAt(_triggers.Count - 1);
                RebuildChips();
                break;
        }
    }

    /// <summary>Запятая или точка с запятой в наборе закрывают триггер, как Enter.</summary>
    private void TriggerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = TriggerInput.Text;
        var cut = text.LastIndexOfAny([',', ';']);
        if (cut >= 0)
        {
            AddTriggers([text[..cut]]);
            TriggerInput.Text = text[(cut + 1)..].TrimStart();
            TriggerInput.CaretIndex = TriggerInput.Text.Length;
        }

        UpdatePlaceholders();
    }

    /// <summary>
    /// Вставка списка «a, b, c» или столбика строк заводит все триггеры разом: однострочное
    /// поле иначе оставило бы от столбика одну строку.
    /// </summary>
    private void TriggerInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted ||
            pasted.IndexOfAny([',', ';', '\r', '\n']) < 0)
        {
            return;
        }

        e.CancelCommand();
        var pieces = pasted.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        AddTriggers([TriggerInput.Text, .. pieces]);
        TriggerInput.Text = "";
    }

    private void TriggerInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_filling && IsEditing)
        {
            CommitTriggerInput();
        }
    }

    /// <summary>Щелчок по рамке ставит курсор в поле: рамка выглядит полем целиком.</summary>
    private void TriggerFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TriggerInput.IsKeyboardFocusWithin)
        {
            TriggerInput.Focus();
            TriggerInput.CaretIndex = TriggerInput.Text.Length;
        }
    }

    private readonly record struct DraftState(string Name, string Triggers, string Text, bool Enabled)
    {
        public static DraftState Empty => new("", "", "", true);
    }
}

/// <summary>
/// Строка списка инструкций: всё, что рисует карточка, уже разложенное по полям.
/// </summary>
/// <remarks>
/// Отдельный объект, а не сама инструкция: шаблону нужны готовые видимости и «+N», а считать
/// их конвертерами на каждую перерисовку незачем.
/// </remarks>
internal sealed class InstructionRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> Triggers { get; init; }

    public string MoreTriggers { get; init; } = "";

    public required string Preview { get; init; }

    public bool Enabled { get; init; }

    public double ContentOpacity => Enabled ? 1 : 0.5;

    public Visibility TriggersVisibility => Triggers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoTriggersVisibility => Triggers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MoreVisibility => MoreTriggers.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static InstructionRow From(Instruction instruction)
    {
        var extra = instruction.Triggers.Count - SettingsInstructionsPage.CardTriggerLimit;
        return new InstructionRow
        {
            Id = instruction.Id,
            Name = instruction.Name,
            Triggers = instruction.Triggers.Take(SettingsInstructionsPage.CardTriggerLimit).ToList(),
            MoreTriggers = extra > 0 ? "+" + extra.ToString(CultureInfo.CurrentCulture) : "",
            Preview = InstructionLibrary.Preview(instruction.Text),
            Enabled = instruction.Enabled
        };
    }
}
