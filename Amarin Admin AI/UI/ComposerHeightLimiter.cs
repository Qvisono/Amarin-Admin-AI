using System.Windows;
using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>
/// Держит композер в пределах доли области чата: поле ввода растёт до этой границы, а дальше
/// текст прокручивается внутри поля, и лента сообщений остаётся на экране.
/// </summary>
/// <remarks>
/// <para>
/// Без ограничения длинный промпт (несколько тысяч знаков) раздувал композер на всё окно: лента
/// исчезала, каретка уезжала за нижний край, и писать становилось нечем. Вся цепочка разметки
/// сверху донизу — <c>ComposerRow</c> (Auto), <c>ComposerInputRowDef</c> (звёздочка),
/// <c>MessageTextBox</c> — размера не ограничивала, поэтому объявленный у поля
/// <c>ScrollViewer.VerticalScrollBarVisibility="Auto"</c> не включался никогда.
/// </para>
/// <para>
/// Предел ставится на <c>MaxHeight</c> самого <see cref="TextBox"/>, а не на строку разметки и не
/// на <c>ComposerBorder</c>. Строки композера анимирует <see cref="ComposerCompactMode"/>
/// (<c>MinHeight</c> 45 ↔ 26 и 125 ↔ 66), а при <c>MaxHeight</c> меньше <c>MinHeight</c> WPF
/// слушается минимума — в компактном режиме ограничение бы просто не работало. Высоту самого
/// поля не трогает никто, конфликта нет.
/// </para>
/// <para>
/// Доля считается от текущего размера, а не записана числом в разметке: масштаб интерфейса — это
/// поддельный DPI (<see cref="UiScale"/>), и фиксированные пиксели при 150 % дали бы поле в
/// полтора раза выше относительно окна. Доля же одинаково ведёт себя и в окне минимального
/// размера, и развёрнутом на 4K.
/// </para>
/// </remarks>
internal sealed class ComposerHeightLimiter
{
    /// <summary>Доля высоты области чата, которую можно отдать композеру целиком.</summary>
    internal const double ChatShare = 0.45;

    /// <summary>Пол в пикселях — примерно пять строк; ниже не опускаемся даже в крошечном окне.</summary>
    internal const double MinimumInput = 96;

    private readonly Grid _chat;
    private readonly FrameworkElement _attachments;
    private readonly TextBox _input;

    // Снято с разметки один раз, до того как компактный режим что-нибудь заанимировал: в пилюле
    // тулбар ужат до нуля, а поля другие, и посчитанная там «обвязка» врала бы весь сеанс.
    private readonly double _chrome;

    public ComposerHeightLimiter(
        Grid chat,
        Border composer,
        Grid layout,
        Grid inputRow,
        Grid toolbar,
        FrameworkElement attachments,
        TextBox input)
    {
        _chat = chat;
        _attachments = attachments;
        _input = input;

        _chrome =
            composer.Margin.Top + composer.Margin.Bottom +
            composer.BorderThickness.Top + composer.BorderThickness.Bottom +
            layout.Margin.Top + layout.Margin.Bottom +
            inputRow.Margin.Top + inputRow.Margin.Bottom +
            (double.IsNaN(toolbar.Height) ? toolbar.ActualHeight : toolbar.Height);

        // Оба источника размера от высоты поля не зависят, поэтому обратной связи не возникает:
        // область чата задана строкой-звёздочкой внешней сетки, полоса вложений — числом
        // миниатюр.
        _chat.SizeChanged += (_, _) => Update();
        _attachments.SizeChanged += (_, _) => Update();
        _attachments.IsVisibleChanged += (_, _) => Update();

        Update();
    }

    /// <summary>
    /// Сколько пикселей остаётся полю ввода. <paramref name="reserved"/> — всё, что композер
    /// тратит помимо самого поля: рамка, отступы, тулбар и полоса вложений. Доля меряется по
    /// композеру целиком, потому что человек видит именно его: поле «на 45 %» плюс тулбар со
    /// всеми отступами закрывали бы уже больше половины чата.
    /// </summary>
    internal static double Limit(double chatHeight, double reserved) =>
        Math.Max(MinimumInput, chatHeight * ChatShare - reserved);

    /// <summary>Пересчитывает предел по текущим размерам. Открыт для тестов.</summary>
    internal void Update()
    {
        // До первой раскладки высота нулевая; ставить по ней пол незачем — SizeChanged придёт
        // следом и посчитает по-настоящему.
        if (_chat.ActualHeight <= 0)
        {
            return;
        }

        var attachments = _attachments.IsVisible ? _attachments.ActualHeight : 0;
        _input.MaxHeight = Limit(_chat.ActualHeight, _chrome + attachments);
    }
}
