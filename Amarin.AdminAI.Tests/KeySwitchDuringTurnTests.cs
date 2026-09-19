using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Смена активного ключа посреди хода.
/// </summary>
/// <remarks>
/// Слот, которому ключ не назначен, берёт его по провайдеру, и исчезнувший ключ увёл бы
/// следующий раунд хода в отказ, который читается как сетевой сбой. Поэтому добавление и
/// удаление посреди хода заперты: <c>Add</c> делает новый ключ выбранным сам, а <c>Remove</c>
/// обнуляет выбор, и выбранным становится следующий годный.
/// <para>
/// А вот выбор кружком с версии 1.23.0 не заперт: ключ хода снят при его начале и лежит на
/// самом ходе, а выбор на странице решает только, чьи траты показать.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class KeySwitchDuringTurnTests
{
    private readonly WpfFixture _wpf;

    public KeySwitchDuringTurnTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static Dictionary<string, RunningTurn> Turns(MainWindow window) =>
        (Dictionary<string, RunningTurn>)typeof(MainWindow)
            .GetField("_turns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    /// <summary>Заводит живой ход и убирает его, что бы ни случилось внутри.</summary>
    private T WithRunningTurn<T>(Func<MainWindow, T> body) => _wpf.Ui.Invoke(() =>
    {
        var window = Window();
        var turns = Turns(window);
        var turn = new RunningTurn
        {
            Session = new ChatSession
            {
                Id = "turn-guard",
                Title = "Чат",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            },
            Cancellation = new CancellationTokenSource(),
            Kind = TurnKind.Send,
            StartedAt = DateTime.Now
        };

        turns["turn-guard"] = turn;
        try
        {
            return body(window);
        }
        finally
        {
            turns.Remove("turn-guard");
            turn.Cancellation.Dispose();
        }
    });

    /// <summary>Пока идёт ход, окно обязано сознаваться в этом — на этом стоят все три запрета.</summary>
    [Fact]
    public void A_running_turn_is_visible_to_the_key_page()
    {
        var duringTurn = WithRunningTurn(window => window.HasRunningTurns);
        var afterwards = _wpf.Ui.Invoke(() => Window().HasRunningTurns);

        Assert.True(duringTurn);
        Assert.False(afterwards);
    }

    /// <summary>
    /// Удаление ключа меняет активный — следующим годным становится другой, возможно чужого
    /// провайдера. Посреди хода окно подтверждения даже не открывается.
    /// </summary>
    [Fact]
    public void Removing_a_key_waits_for_the_turn_to_finish()
    {
        var duringTurn = WithRunningTurn(_ => AskedToConfirmRemoval());

        Assert.Equal(Visibility.Collapsed, duringTurn);
    }

    /// <summary>
    /// Вторая половина той же проверки: без хода подтверждение обязано открыться. Иначе первая
    /// осталась бы зелёной и после того, как удаление сломали совсем.
    /// </summary>
    [Fact]
    public void Without_a_turn_the_confirmation_opens()
    {
        var shown = _wpf.Ui.Invoke(AskedToConfirmRemoval);

        Assert.Equal(Visibility.Visible, shown);
    }

    /// <summary>
    /// Выбор кружком посреди хода разрешён и ни одного слота не двигает.
    /// </summary>
    /// <remarks>
    /// Прежде кружок гас на время хода, потому что выбранный ключ задавал провайдера всем
    /// девяти слотам сразу. Теперь он не задаёт ничего, кроме того, чьи траты показать, —
    /// и запрет стал бы запретом посмотреть на график.
    /// </remarks>
    [Fact]
    public void Choosing_a_key_stays_available_during_a_turn()
    {
        var enabled = WithRunningTurn(_ => ChoiceEnabled());

        Assert.True(enabled, "кружок выбора ключа не должен гаснуть на время хода");
    }

    /// <summary>Собирает строку ключа и говорит, доступен ли в ней кружок выбора.</summary>
    private static bool ChoiceEnabled()
    {
        var page = (SettingsKeyPage)Window().FindName("KeyPage")!;
        var row = typeof(SettingsKeyPage)
            .GetMethod("BuildKeyRow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [new ApiKeyEntry(
                "k1", "Роутер", "sk-or-v1-abcdef", ApiKeySource.Stored, false,
                LlmProvider.OpenRouter)]);

        return Descendants((DependencyObject)row!).OfType<RadioButton>().First().IsEnabled;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>Просит удалить ключ и возвращает, открылось ли подтверждение.</summary>
    private static Visibility AskedToConfirmRemoval()
    {
        var window = Window();
        var overlay = (Grid)window.FindName("KeyRemoveOverlay")!;
        overlay.Visibility = Visibility.Collapsed;

        var page = (SettingsKeyPage)window.FindName("KeyPage")!;
        typeof(SettingsKeyPage)
            .GetMethod("RemoveKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [new ApiKeyEntry(
                "k1", "Роутер", "sk-or-v1-abcdef", ApiKeySource.Stored, false,
                LlmProvider.OpenRouter)]);

        var shown = overlay.Visibility;
        overlay.Visibility = Visibility.Collapsed;
        return shown;
    }
}
