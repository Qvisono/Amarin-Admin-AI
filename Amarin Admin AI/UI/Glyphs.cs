using System.Collections.Concurrent;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Значки, которые рисуются кодом: путь разбирается один раз и замораживается.
/// </summary>
/// <remarks>
/// Ленту строят сотнями сообщений, и <see cref="Geometry.Parse"/> на каждое — это разбор строки
/// и новая незамороженная геометрия, за изменениями которой следит каждый её <c>Path</c>.
/// Только для постоянных путей: набор ключей обязан быть конечным.
/// </remarks>
internal static class Glyphs
{
    private static readonly ConcurrentDictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public static Geometry Get(string data) => Cache.GetOrAdd(data, static path =>
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    });
}
