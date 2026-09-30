using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Строка редактора: перевод правится на месте, пометки обновляются привязкой.</summary>
internal sealed class TranslationItem(TranslationRow row) : INotifyPropertyChanged
{
    private string _translation = row.Translation;
    private string _problem = "";
    private bool _edited = row.Edited;

    public string Key { get; } = row.Key;

    public string Original { get; } = row.Original;

    /// <summary>Перевод на момент открытия: по нему видно, что правили.</summary>
    public string Initial { get; private set; } = row.Translation;

    /// <summary>Перевод пришёл от модели заново: пометку «правлено руками» надо снять.</summary>
    public bool Retranslated { get; private set; }

    public string Translation
    {
        get => _translation;
        set
        {
            if (_translation == value)
            {
                return;
            }

            _translation = value;
            Problem = "";
            Notify(nameof(Translation));
        }
    }

    public bool Changed => !string.Equals(Translation.Trim(), Initial.Trim(), StringComparison.Ordinal);

    public string EditedMark => _edited || (Changed && !Retranslated) ? Loc.Get("S.TranslationEditor.Edited") : "";

    public string Problem
    {
        get => _problem;
        set
        {
            _problem = value;
            Notify(nameof(Problem));
        }
    }

    public void SetFromModel(string value)
    {
        Initial = value;
        Retranslated = true;
        _edited = false;
        Translation = value;
        Notify(nameof(EditedMark));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Translation))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedMark)));
        }
    }
}

/// <summary>
/// Правка машинного перевода (I4): список «ключ / оригинал / перевод», поиск, «перевести заново»
/// у строки. Сохранение проверяет плейсхолдеры <c>{0}</c> и помечает поправленное руками —
/// следующий перевод этого языка его не тронет.
/// </summary>
public partial class TranslationEditorOverlay : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private List<TranslationItem> _items = [];
    private string _code = "";

    public TranslationEditorOverlay()
    {
        InitializeComponent();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Filter();
        };
    }

    /// <summary>Перевести одну строку заново: оригинал → перевод или null. Ставит окно.</summary>
    internal Func<string, string, Task<string?>>? Retranslate { get; set; }

    /// <summary>Файл языка сохранён — окно перечитывает словарь.</summary>
    internal event Action<string>? Saved;

    internal void Show(string code, string languageName)
    {
        _code = code;
        TitleText.Text = Loc.Format("S.TranslationEditor.Title", languageName);
        _items = TranslationEdits.Rows(StringsRu.Values, UserLanguageStore.ReadMap(code))
            .Select(row => new TranslationItem(row))
            .ToList();
        SearchBox.Text = "";
        StatusText.Text = "";
        Filter();
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal IReadOnlyList<TranslationItem> Items => _items;

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        Rows.ItemsSource = _items
            .Where(item => TranslationEdits.Matches(new TranslationRow(item.Key, item.Original, item.Translation, false), query))
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void Retranslate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TranslationItem item } button || Retranslate is not { } retranslate)
        {
            return;
        }

        button.IsEnabled = false;
        StatusText.Text = Loc.Get("S.TranslationEditor.Translating");
        try
        {
            if (await retranslate(item.Key, item.Original).ConfigureAwait(true) is { } value)
            {
                item.SetFromModel(value);
                StatusText.Text = "";
            }
            else
            {
                StatusText.Text = Loc.Get("S.TranslationEditor.TranslateFailed");
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Сохраняет. Строка, где правка потеряла плейсхолдер, не сохраняется — вместо этого
    /// под ней причина, и список показывает именно такие строки.
    /// </summary>
    internal bool Save()
    {
        var changed = _items.Where(item => item.Changed || item.Retranslated).ToList();
        // Правкой руками считается только отличие от того, что было (или что дала модель).
        var edits = changed
            .Where(item => item.Changed)
            .ToDictionary(item => item.Key, item => item.Translation, StringComparer.Ordinal);
        var problems = TranslationEdits.PlaceholderProblems(StringsRu.Values, edits);
        if (problems.Count > 0)
        {
            foreach (var item in _items.Where(item => problems.Contains(item.Key)))
            {
                item.Problem = Loc.Get("S.TranslationEditor.Placeholders");
            }

            SearchBox.Text = "";
            Rows.ItemsSource = _items.Where(item => problems.Contains(item.Key)).ToList();
            StatusText.Text = Loc.Format("S.TranslationEditor.Problems", problems.Count);
            return false;
        }

        var map = UserLanguageStore.ReadMap(_code) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in changed.Where(item => item.Retranslated))
        {
            map[item.Key] = item.Initial;
            map = TranslationEdits.Unmark(map, item.Key);
        }

        map = TranslationEdits.Apply(map, edits);
        UserLanguageStore.Save(_code, map);
        Saved?.Invoke(_code);
        Visibility = Visibility.Collapsed;
        return true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.Format("S.Language.Failed", ex.Message);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Visibility = Visibility.Collapsed;

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Visibility = Visibility.Collapsed;
        }
    }
}
