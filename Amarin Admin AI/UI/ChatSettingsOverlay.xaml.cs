using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Строка списка инструкций в настройках чата.</summary>
internal sealed class InstructionChoice(string id, string name, bool chosen) : INotifyPropertyChanged
{
    private bool _chosen = chosen;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public bool Chosen
    {
        get => _chosen;
        set
        {
            if (_chosen != value)
            {
                _chosen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chosen)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Настройки одного чата (D11): свой промпт и какие инструкции он видит. Модель и размышление
/// выбираются как всегда — у поля ввода.
/// </summary>
public partial class ChatSettingsOverlay : UserControl
{
    private List<InstructionChoice> _choices = [];

    public ChatSettingsOverlay() => InitializeComponent();

    /// <summary>Сохранить; null — профиль пуст, чат берёт общее.</summary>
    internal event Action<ChatProfile?>? Saved;

    internal void Show(ChatProfile? profile, IReadOnlyList<Instruction> enabled)
    {
        PromptBox.Text = profile?.Prompt ?? "";
        var chosen = profile?.InstructionIds;
        _choices = enabled.Select(instruction =>
            new InstructionChoice(instruction.Id, instruction.Name, chosen is null || chosen.Contains(instruction.Id, StringComparer.Ordinal))).ToList();
        InstructionList.ItemsSource = _choices;
        AllInstructionsBox.IsChecked = chosen is null;
        NoInstructionsText.Visibility = _choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllInstructionsBox.IsEnabled = _choices.Count > 0;
        SyncList();
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => PromptBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Что сейчас набрано в окне — профилем; пустое — null.</summary>
    internal ChatProfile? Current()
    {
        var profile = new ChatProfile
        {
            Prompt = string.IsNullOrWhiteSpace(PromptBox.Text) ? null : PromptBox.Text.Trim(),
            InstructionIds = AllInstructionsBox.IsChecked == true
                ? null
                : _choices.Where(choice => choice.Chosen).Select(choice => choice.Id).ToList()
        };
        return profile.IsEmpty ? null : profile;
    }

    private void SyncList() =>
        InstructionList.Visibility = AllInstructionsBox.IsChecked == true || _choices.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void AllInstructionsBox_Changed(object sender, RoutedEventArgs e) => SyncList();

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        PromptPlaceholder.Visibility = PromptBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        Saved?.Invoke(Current());
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Text = "";
        AllInstructionsBox.IsChecked = true;
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
