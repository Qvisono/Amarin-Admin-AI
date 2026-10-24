using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shape = System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Сколько денег осталось — плашка рядом с кнопкой отправки.
/// </summary>
/// <remarks>
/// <para>
/// До версии 1.23.0 платил один ключ, и плашка показывала его остаток: Venice называет его на
/// заголовках каждого ответа, так что цифра приезжала даром. Теперь платят несколько — разговор
/// одним ключом, заголовки чатов другим, поиск третьим, — и одно число перестало отвечать на
/// вопрос «сколько у меня осталось». Поэтому здесь сумма по всем ключам, а кто сколько — под
/// курсором.
/// </para>
/// <para>
/// Цифра запаздывает по своей природе: она говорит, сколько осталось после последнего ответа.
/// Это ровно тот миг, который человеку и нужен.
/// </para>
/// <para>
/// Цвета смешиваются здесь, а не объявляются в палитрах. У плашки три состояния, а палитр
/// восемнадцать: пятьдесят четыре токена ради оттенка, который механически выводится из одной
/// кисти, пришлось бы править руками в каждой новой теме.
/// </para>
/// <para>
/// Цвет у плашки один, что бы ни показывала цифра: это показание, а не тревога, и число,
/// краснеющее по мере убывания, заставляет композер мигать о том, что человек и так видит.
/// Предупреждение живёт в подсказке, где можно сказать, чем именно это грозит.
/// </para>
/// </remarks>
internal sealed class BalanceBadge
{
    /// <summary>
    /// Пороги «мало» и «почти кончились» (E4) — из настроек. Функцией, как книга и ключи: службы
    /// привязываются позже конструктора. Null — заводские пороги.
    /// </summary>
    private readonly Func<BalanceThresholds?> _thresholds;

    private readonly BalanceWatch _watch = new();

    /// <summary>Остаток перешёл порог вниз. Окно показывает уведомление.</summary>
    public event Action<BalanceAlert>? Alerted;

    private readonly Border _plate;
    private readonly Shape.Path _coin;
    private readonly TextBlock _amount;
    private readonly BalanceStore? _store;

    /// <summary>
    /// Книга остатков. Через функцию, а не ссылкой: плашка собирается в конструкторе окна,
    /// а службы привязываются позже — на этом порядке уже спотыкались.
    /// </summary>
    private readonly Func<BalanceBook?> _book;

    /// <summary>Книга, на события которой мы подписаны. Меняется при смене профиля.</summary>
    private BalanceBook? _subscribed;

    /// <summary>
    /// Живой список ключей. Через функцию, а не снимком: ключи заводят и удаляют, не закрывая
    /// окна, а сумма считается только по тем, что есть сейчас.
    /// </summary>
    private readonly Func<IReadOnlyList<ApiKeyEntry>> _keys;

    /// <summary>Сумма, навязанная тестом вместо книги. <c>null</c> — обычная работа.</summary>
    private BalanceTotal? _forced;

    /// <summary>
    /// Сколько токенов прошло через ключ по счёту программы. У ключа, выданного пакетом токенов,
    /// долларов нет, и в подсказке вместо них — сколько токенов осталось.
    /// </summary>
    private readonly Func<string?, long> _tokensUsed;

    public BalanceBadge(
        Border plate,
        Shape.Path coin,
        TextBlock amount,
        Func<BalanceBook?> book,
        Func<IReadOnlyList<ApiKeyEntry>> keys,
        BalanceStore? store = null,
        Func<BalanceThresholds?>? thresholds = null,
        Func<string?, long>? tokensUsed = null)
    {
        _thresholds = thresholds ?? (() => null);
        _tokensUsed = tokensUsed ?? (_ => 0);
        _plate = plate;
        _coin = coin;
        _amount = amount;
        _book = book;
        _keys = keys;
        _store = store;

        // Палитра живёт в подменяемых словарях, поэтому смешанные кисти надо пересобирать при
        // смене темы — DynamicResource справился бы, но эти кисти выводятся из другой.
        ThemeManager.EffectiveThemeChanged += Render;
        Render();
    }

    /// <summary>
    /// Книга, на события которой мы подписаны. Подписка отложена до её появления: в конструкторе
    /// окна служб ещё нет, а сохранённое с прошлого запуска кладётся в неё тогда же.
    /// </summary>
    private BalanceBook? Book()
    {
        var book = _book();
        if (ReferenceEquals(book, _subscribed))
        {
            return book;
        }

        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnBookChanged;
        }

        _subscribed = book;
        if (book is not null)
        {
            book.Changed += OnBookChanged;
            if (_store?.Load() is { Count: > 0 } saved)
            {
                book.Restore(saved);
            }
        }

        return book;
    }

    /// <summary>Перерисовывает плашку по тому, что уже известно книге.</summary>
    public void Refresh()
    {
        _forced = null;
        Render();
    }

    /// <summary>
    /// Показывает названную сумму, минуя книгу. Только для тестов.
    /// </summary>
    /// <remarks>
    /// Цвета плашки смешиваются из палитры, и проверять их надо на восемнадцати темах — заводить
    /// ради этого ключи и книгу в каждом прогоне значило бы проверять не цвета, а обвязку.
    /// </remarks>
    internal void ShowForShot(decimal? usd, decimal? diem = null, int unknown = 0)
    {
        _forced = new BalanceTotal(usd, diem, unknown);
        Render();
    }

    /// <summary>
    /// Забывает остатки вместе с их кэшем на диске.
    /// </summary>
    /// <remarks>
    /// Зовётся, когда в программе не осталось ключей: показывать деньги нечьи.
    /// </remarks>
    public void Forget()
    {
        _store?.Forget();
        Render();
    }

    private void OnBookChanged()
    {
        if (_plate.Dispatcher.CheckAccess())
        {
            Save();
            Render();
            return;
        }

        // Книгу наполняют потоки ходов: до трёх сразу, и ни один из них не UI.
        _plate.Dispatcher.BeginInvoke(new Action(() =>
        {
            Save();
            Render();
        }));
    }

    private void Save()
    {
        if (_subscribed is { } book)
        {
            _store?.Save(book.Snapshot());
        }
    }

    private void Render()
    {
        _plate.Visibility = Visibility.Visible;
        Paint();

        var book = Book();
        var keys = _keys();
        var total = _forced ?? book?.Total(keys) ?? default;
        var tokens = TokenRows(keys);

        if (total.Usd is null && total.Diem is null)
        {
            // До первого ответа самого первого запуска цифры нет нигде. Прочерк честен;
            // «$0.00» читалось бы как «деньги кончились». У человека с одними пакетами токенов
            // долларов нет вовсе — тогда подсказка говорит о токенах.
            _amount.Text = FormatUsd(null);
            ShowTooltip(
                new TipRow(Loc.Get("S.Balance.Total"), FormatUsd(null)),
                tokens,
                tokens.Count > 0 ? [] : [new TipNote(Loc.Get("S.Balance.Unknown"))]);
            return;
        }

        var rows = _forced is null && book is not null ? book.Breakdown(keys) : [];
        var thresholds = _thresholds() ?? new BalanceThresholds();
        _amount.Text = FormatUsd(total.Usd);
        var (lines, notes) = BuildTooltip(total, rows, thresholds, tokens);
        ShowTooltip(new TipRow(Loc.Get("S.Balance.Total"), FormatUsd(total.Usd)), lines, notes);

        if (_forced is null)
        {
            foreach (var alert in _watch.Observe(thresholds, total, rows))
            {
                Alerted?.Invoke(alert);
            }
        }
    }

    private void Paint()
    {
        var accent = _plate.TryFindResource("Text.Muted") as Brush ?? Frozen(Colors.Gray);
        _amount.Foreground = accent;
        _coin.Fill = accent;

        // Оттенок той же краски, а не поверхность из палитры: плашка читается одним предметом
        // и сохраняет смысл и на почти чёрной теме, и на почти белой.
        var colour = accent is SolidColorBrush { Color: var value } ? value : Colors.Gray;
        _plate.Background = Frozen(Color.FromArgb(0x24, colour.R, colour.G, colour.B));
        _plate.BorderBrush = Frozen(Color.FromArgb(0x40, colour.R, colour.G, colour.B));
    }

    /// <summary>
    /// Деньги, поэтому копейки показываются всегда — «$18,4» читается как сломанное число,
    /// а не как остаток. Только после тысячи они уходят: там плашка теснила бы кнопку отправки,
    /// а копейки перестали значить хоть что-то.
    /// </summary>
    internal static string FormatUsd(decimal? usd) =>
        usd is null
            ? "-"
            : "$" + usd.Value.ToString(usd.Value < 1000m ? "0.00" : "#,0", CultureInfo.InvariantCulture)
                .Replace(",", " ")
                .Replace('.', ',');

    /// <summary>
    /// Итог, а под ним — кто сколько. Разбивка нужна ровно потому, что сумма её прячет: увидев
    /// «$4.86», человек не знает, лежат ли они на том ключе, которым идёт разговор.
    /// </summary>
    private List<TipRow> TokenRows(IReadOnlyList<ApiKeyEntry> keys) =>
    [
        .. keys
            .Where(key => key.TokenLimit is not null && !key.IsBroken)
            .Select(key => new TipRow(
                key.Label,
                Loc.Format(
                    "S.Balance.Tokens",
                    TokenCount.Format(Math.Max(0, (key.TokenLimit ?? 0) - _tokensUsed(key.Secret))))))
    ];

    /// <summary>
    /// Подсказка — таблица (1.37.0): итог сверху, ключи столбцом справа, пояснения тусклыми
    /// строками под чертой. Одна на плашку и только перезаполняется: подсказку, которую человек
    /// держит открытой, пересоздание закрывало бы под курсором.
    /// </summary>
    private void ShowTooltip(TipRow total, IReadOnlyList<TipRow> rows, IReadOnlyList<TipNote> notes)
    {
        _tip ??= CostBreakdownTooltip.CreateEmpty();
        CostBreakdownTooltip.Fill(_tip, total, rows, notes);
        _plate.ToolTip = _tip;
    }

    private ToolTip? _tip;

    private static (List<TipRow> Rows, List<TipNote> Notes) BuildTooltip(
        BalanceTotal total,
        IReadOnlyList<BalanceRow> rows,
        BalanceThresholds thresholds,
        IReadOnlyList<TipRow> tokens)
    {
        var lines = new List<TipRow>();
        if (total.Diem is { } diem and > 0)
        {
            // DIEM бывают только у Venice: строка называет провайдера, число — в своей единице.
            lines.Add(new TipRow(
                ProviderSpec.For(LlmProvider.Venice).Name,
                Loc.Format("S.Key.Balance.Diem", diem.ToString("0.##", CultureInfo.InvariantCulture))));
        }

        // Разбивка нужна ровно потому, что сумма её прячет: увидев «$4.86», человек не знает,
        // лежат ли они на том ключе, которым идёт разговор. У одного ключа она повторила бы итог.
        if (rows.Count > 1)
        {
            lines.AddRange(rows.Select(row => new TipRow(row.Label, FormatUsd(row.Usd))));
        }

        lines.AddRange(tokens);

        var notes = new List<TipNote>();
        switch (BalanceWatch.Level(total.Usd, thresholds.LowUsd, thresholds.CriticalUsd))
        {
            case BalanceLevel.Critical:
                notes.Add(new TipNote(Loc.Get("S.Balance.AlmostOut"), TipNoteTone.Danger));
                break;
            case BalanceLevel.Low:
                notes.Add(new TipNote(Loc.Get("S.Balance.Low"), TipNoteTone.Warning));
                break;
        }

        if (total.Unknown > 0)
        {
            notes.Add(new TipNote(Loc.Format(
                "S.Balance.Unknown.Some",
                total.Unknown.ToString(CultureInfo.InvariantCulture))));
        }

        notes.Add(new TipNote(Loc.Get("S.Balance.Refresh")));
        return (lines, notes);
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
