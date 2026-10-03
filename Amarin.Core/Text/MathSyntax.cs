namespace Amarin.Core;

/// <summary>Что за символ стоит в узле — от этого зависит начертание и воздух вокруг него.</summary>
public enum MathTokenKind
{
    /// <summary>Переменная: одна буква, курсивом.</summary>
    Variable,

    /// <summary>Цифры и всё, что набирается прямым шрифтом.</summary>
    Number,

    /// <summary>Прямое слово: <c>sin</c>, <c>lim</c>, содержимое <c>\text{}</c>.</summary>
    Upright,

    /// <summary>Бинарная операция: <c>+ − × ÷</c>.</summary>
    Binary,

    /// <summary>Отношение: <c>= ≤ ≈ →</c>.</summary>
    Relation,

    /// <summary>Запятая, точка с запятой.</summary>
    Punctuation,

    /// <summary>Скобка или разделитель, набранный как обычный символ.</summary>
    Fence,

    /// <summary>Крупный оператор: <c>∑ ∏ ∫ ⋃</c>. Пределы у него ставятся сверху и снизу.</summary>
    BigOperator
}

public enum MathAccentKind
{
    Hat,
    Bar,
    Vector,
    Tilde,
    Dot,
    DoubleDot,
    Overline,
    Underline
}

public enum MathStyleKind
{
    Bold,
    Blackboard,
    Roman,
    Italic,
    Calligraphic
}

public abstract record MathNode;

/// <summary>Последовательность узлов — тело формулы, группы <c>{}</c>, ячейки матрицы.</summary>
public sealed record MathRow(IReadOnlyList<MathNode> Items) : MathNode
{
    public static readonly MathRow Empty = new([]);
}

public sealed record MathSymbol(string Text, MathTokenKind Kind) : MathNode;

/// <summary>Дробь. <paramref name="Line"/> ложно у <c>\binom</c> — там черты нет.</summary>
public sealed record MathFraction(MathNode Numerator, MathNode Denominator, bool Line = true) : MathNode;

public sealed record MathRadical(MathNode Body, MathNode? Index) : MathNode;

/// <summary>
/// Основание с индексами. <paramref name="Limits"/> — пределы просятся под и над знаком
/// (это про <c>∑</c> и <c>\lim</c>); окончательное решение принимает разметка по стилю.
/// </summary>
public sealed record MathScripts(MathNode Base, MathNode? Sub, MathNode? Sup, bool Limits = false) : MathNode;

/// <summary>Тело в скобках, растянутых по его высоте. Пустая строка — скобки нет (<c>\right.</c>).</summary>
public sealed record MathFenced(string Left, string Right, MathNode Body) : MathNode;

public sealed record MathAccent(MathNode Body, MathAccentKind Kind) : MathNode;

public sealed record MathStyled(MathNode Body, MathStyleKind Style) : MathNode;

/// <summary>Таблица: матрицы, <c>cases</c>, выключные выкладки со знаком равенства в столбик.</summary>
public sealed record MathMatrix(
    IReadOnlyList<IReadOnlyList<MathNode>> Rows,
    string Left,
    string Right,
    bool LeftAligned = false) : MathNode;

/// <summary>Явный пробел в долях кегля: <c>\,</c>, <c>\quad</c> и прочие.</summary>
public sealed record MathSpace(double Ems) : MathNode;

/// <summary>Строки выключной формулы, разделённые <c>\\</c>.</summary>
public sealed record MathLines(IReadOnlyList<MathNode> Rows) : MathNode;
