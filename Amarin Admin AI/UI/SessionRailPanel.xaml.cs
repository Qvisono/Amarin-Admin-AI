using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Статический концепт правой панели сеанса: при нехватке высоты первыми уступают место нижние
/// карточки, а при узком окне всю панель скрывает <see cref="MainWindow"/>.
/// </summary>
public partial class SessionRailPanel : UserControl
{
    internal const double MinWindowWidth = 1120;

    // Демонстрационная переписка окна сжатия: счётчик «оставить последние» считает от неё.
    internal const int DemoMessages = 44;
    internal const int DefaultKeep = 10;
    private const int MinKeep = 2;
    private const int MaxKeep = 40;

    private bool _fittingCards;
    private int _keep = DefaultKeep;

    public SessionRailPanel()
    {
        InitializeComponent();

        // Те же каталог и тематические ресурсы, что у модели в ответе и в блоке агента.
        ModelBrand.Apply(this, "anthropic:claude-haiku-5-5", ResearchModelLogo, ResearchModelLetter, null);
        ModelBrand.Apply(this, "anthropic:claude-haiku-5-5", EditorModelLogo, EditorModelLetter, null);
        ModelBrand.Apply(this, "zai:glm-5.3-flash", ReviewModelLogo, ReviewModelLetter, null);
        ModelBrand.Apply(this, "anthropic:claude-haiku-5-5", CompactModelLogo, CompactModelLetter, null);

        ShowKeep();

        SizeChanged += (_, _) => FitCards();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && IsCompactOpen)
            {
                e.Handled = true;
                ShowCompact(false);
            }
        };
    }

    internal static bool FitsWindow(double width) => width >= MinWindowWidth;

    internal bool IsCompactOpen => CompactView.Visibility == Visibility.Visible;

    private void CompactOpen_Click(object sender, RoutedEventArgs e) => ShowCompact(true);

    // Концепт: «Сжать» пока только возвращает карточки, как и «Назад».
    private void CompactBack_Click(object sender, RoutedEventArgs e) => ShowCompact(false);

    private void KeepLess_Click(object sender, RoutedEventArgs e) => SetKeep(_keep - 1);

    private void KeepMore_Click(object sender, RoutedEventArgs e) => SetKeep(_keep + 1);

    private void ShowCompact(bool open)
    {
        CompactView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CardsPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (!open)
        {
            FitCards();
        }
    }

    private void SetKeep(int value)
    {
        _keep = Math.Clamp(value, MinKeep, MaxKeep);
        ShowKeep();
    }

    private void ShowKeep()
    {
        KeepValue.Text = _keep.ToString(System.Globalization.CultureInfo.CurrentCulture);
        KeepHint.Text = Loc.Format("S.SessionRail.CompactKeepHint", DemoMessages - _keep, DemoMessages);
        KeepLessButton.IsEnabled = _keep > MinKeep;
        KeepMoreButton.IsEnabled = _keep < MaxKeep;
    }

    private void FitCards()
    {
        if (_fittingCards || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _fittingCards = true;
        try
        {
            var cards = new[] { ContextCard, SettingsCard, FilesCard, AgentsCard };
            foreach (var card in cards)
            {
                // Измеряем исходный набор, иначе после увеличения окна скрытая карточка не
                // сообщила бы свой прежний размер и осталась бы выключенной навсегда.
                card.Visibility = Visibility.Visible;
                card.Measure(new Size(ActualWidth, double.PositiveInfinity));
            }

            var remaining = ActualHeight;
            var overflow = false;
            foreach (var card in cards)
            {
                var fits = !overflow && card.DesiredSize.Height <= remaining;
                card.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
                if (fits)
                {
                    remaining -= card.DesiredSize.Height;
                }
                else
                {
                    overflow = true;
                }
            }
        }
        finally
        {
            _fittingCards = false;
        }
    }
}
