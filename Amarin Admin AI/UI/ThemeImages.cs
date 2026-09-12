using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Реестр картинок, которым <see cref="Image.Source"/> присвоен из ресурса по ключу: иконки
/// действий и логотипы моделей. При смене темы их надо перечитать руками.
/// </summary>
/// <remarks>
/// <see cref="ImageSource"/>, присвоенный из кода, держит объект прежней палитры и за
/// <c>DynamicResource</c> не следует. Раньше это лечилось полной перерисовкой открытого чата
/// (<c>RenderSession</c>) — то есть ради десятка картинок заново строились все сообщения, с
/// повторным разбором разметки; на длинном разговоре это и был тот самый лаг при переключении
/// темы. Всё остальное в ленте живёт на <c>SetResourceReference</c> и перекрашивается само.
/// <para>
/// Реестр, а не обход визуального дерева: попапы (выбор модели) лежат в собственных окнах и в
/// дерево главного не входят, а обход длинной ленты стоил бы ровно того, от чего уходим.
/// Ссылки слабые — картинка удалённого сообщения не должна держаться в памяти из-за списка.
/// Только UI-поток.
/// </para>
/// </remarks>
internal static class ThemeImages
{
    private static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(ThemeImages),
            new PropertyMetadata(null));

    private static readonly List<WeakReference<Image>> Registered = [];

    /// <summary>Через сколько добавлений выметать мёртвые ссылки, если темы никто не менял.</summary>
    private const int SweepEvery = 512;

    private static int _sinceSweep;

    /// <summary>Ставит картинку и запоминает, из какого ключа она взята.</summary>
    public static void Assign(Image image, string key, ImageSource source)
    {
        ArgumentNullException.ThrowIfNull(image);

        image.Source = source;
        if (image.GetValue(KeyProperty) is null)
        {
            if (++_sinceSweep >= SweepEvery)
            {
                Sweep();
            }

            Registered.Add(new WeakReference<Image>(image));
        }

        image.SetValue(KeyProperty, key);
    }

    /// <summary>Перечитывает все живые картинки из текущей палитры.</summary>
    /// <param name="host">Чей словарь ресурсов спрашивать — обычно главное окно.</param>
    public static void Refresh(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var live = 0;
        for (var i = 0; i < Registered.Count; i++)
        {
            if (!Registered[i].TryGetTarget(out var image))
            {
                continue;
            }

            Registered[live++] = Registered[i];
            if (image.GetValue(KeyProperty) is string key &&
                host.TryFindResource(key) is ImageSource source)
            {
                image.Source = source;
            }
        }

        Registered.RemoveRange(live, Registered.Count - live);
        _sinceSweep = 0;
    }

    private static void Sweep()
    {
        _sinceSweep = 0;
        var live = 0;
        for (var i = 0; i < Registered.Count; i++)
        {
            if (Registered[i].TryGetTarget(out _))
            {
                Registered[live++] = Registered[i];
            }
        }

        Registered.RemoveRange(live, Registered.Count - live);
    }
}
