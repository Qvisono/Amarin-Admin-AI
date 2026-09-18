using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI;

/// <summary>
/// Выбор языка интерфейса: список готовых плюс кнопка «new language», по которой модель
/// переводит интерфейс сама.
/// </summary>
/// <remarks>
/// Штатный <c>DarkComboBox</c> здесь не годится: внизу списка нужна кнопка-действие со своим
/// видом и строкой хода перевода, а не ещё один <c>ComboBoxItem</c>.
/// </remarks>
public partial class LanguagePickerField : UserControl
{
    private string _code = LanguageManager.DefaultCode;

    public LanguagePickerField()
    {
        InitializeComponent();

        // Без этой строки StaysOpen="False" гасил бы попап тем же нажатием, которым его
        // открывает ClickMode="Press", и не видел бы кликов внутри своего окна.
        PopupManager.Register(PickerPopup, OpenButton);
        Rebuild();
    }

    /// <summary>Человек выбрал язык из списка.</summary>
    public event EventHandler<string>? LanguagePicked;

    /// <summary>Нажата «new language» — окно спросит название и запустит перевод.</summary>
    public event EventHandler? NewLanguageRequested;

    /// <summary>Нажат крестик у переведённого языка — окно спросит подтверждение и удалит его.</summary>
    internal event EventHandler<UiLanguage>? LanguageDeleteRequested;

    /// <summary>
    /// Имена элементов строки. Тесты ищут галку и крестик по ним, а не по числу детей: детей
    /// у строки стало переменное количество, и счёт разъезжался бы от языка к языку.
    /// </summary>
    internal const string TickName = "LanguageTick";

    internal const string RemoveName = "LanguageRemove";

    public void SetSelected(string code)
    {
        _code = LanguageManager.Normalize(code);
        Rebuild();
    }

    /// <summary>Строка хода перевода. Пустая — строка прячется.</summary>
    public void ShowProgress(string? text)
    {
        ProgressText.Text = text ?? "";
        ProgressText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Перестраивает список: языки могли добавиться переводом прямо сейчас.</summary>
    public void Rebuild()
    {
        if (LanguageList is null)
        {
            return;
        }

        // Один вызов на перестройку. Каждый Available() заново перечитывает папку языков и ради
        // одного названия разбирает словарь перевода целиком, а сюда заходят на каждое открытие
        // настроек — второй такой проход был чистой платой ни за что.
        var available = LanguageManager.Available();

        LanguageList.Children.Clear();
        foreach (var language in available)
        {
            LanguageList.Children.Add(CreateItem(language));
        }

        var current = available
            .FirstOrDefault(item => string.Equals(item.Code, _code, StringComparison.OrdinalIgnoreCase));
        SelectedLabel.Text = current?.NativeName ?? _code;
    }

    private Button CreateItem(UiLanguage language)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = language.NativeName,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");
        row.Children.Add(name);

        var selected = string.Equals(language.Code, _code, StringComparison.OrdinalIgnoreCase);
        if (selected)
        {
            var check = new Path
            {
                Data = Geometry.Parse("M0,3.5 L3,6.5 L8,0"),
                Width = 9,
                Height = 7,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center
            };
            check.SetResourceReference(Shape.StrokeProperty, "Accent.Fill");
            check.Name = TickName;
            Grid.SetColumn(check, 1);
            row.Children.Add(check);
        }

        // Встроенные ru/en удалять нечего — они лежат в сборке, а не файлом на диске, и кнопки
        // у них просто нет: так же, как её нет у профиля по умолчанию.
        if (!language.BuiltIn)
        {
            var remove = new Button
            {
                Style = (Style)FindResource("LanguageRemove"),
                Name = RemoveName,
                Margin = new Thickness(6, 0, -4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.Get("S.Language.Delete")
            };

            var removed = language;
            remove.Click += (_, args) =>
            {
                // Без этого клик дошёл бы до кнопки строки и переключил язык, который удаляют.
                args.Handled = true;
                PickerPopup.IsOpen = false;
                LanguageDeleteRequested?.Invoke(this, removed);
            };

            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
        }

        var button = new Button
        {
            Style = (Style)FindResource("LanguageItem"),
            Content = row,
            ToolTip = language.Code
        };

        var code = language.Code;
        button.Click += (_, _) =>
        {
            PickerPopup.IsOpen = false;
            if (!string.Equals(code, _code, StringComparison.OrdinalIgnoreCase))
            {
                LanguagePicked?.Invoke(this, code);
            }
        };

        return button;
    }

    private void NewLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = false;
        NewLanguageRequested?.Invoke(this, EventArgs.Empty);
    }
}
