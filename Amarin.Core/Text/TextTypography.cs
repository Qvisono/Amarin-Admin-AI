namespace Amarin.Core;

/// <summary>Типографика текста программы.</summary>
internal static class TextTypography
{
    /// <summary>
    /// Длинное и среднее тире — на дефис (1.32.0).
    /// </summary>
    /// <remarks>
    /// «—» и «–» — первая примета текста, написанного моделью, и человек их не печатает. В своих
    /// строках их нет (сторожит <c>TypographyTests</c>), а в машинном переводе интерфейса модель
    /// ставит их по привычке, даже когда ей это запрещено, — поэтому перевод проходит через эту
    /// замену и при приёме, и при чтении сохранённого.
    /// </remarks>
    public static string PlainDashes(string text) =>
        text.Replace('—', '-').Replace('–', '-');
}
