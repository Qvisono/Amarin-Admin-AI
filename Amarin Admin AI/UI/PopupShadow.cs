using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Amarin.UI;

/// <summary>
/// Одна тень у всех всплывающих окон — меню, выпадашек, пикеров — и одни поля под неё.
/// </summary>
/// <remarks>
/// <para>
/// У попапа своё прозрачное окно ровно по размеру содержимого, и тень, нарисованная за край
/// карточки, видна только в пределах полей вокруг неё. Поля стояли на глаз (у меню 10, 6, 10,
/// 12), а тень 24/6 на деле уходит на 12 точек влево и вверх и на 20 вправо и вниз (замер —
/// <c>PopupShadowTests</c>): она обрезалась прямой линией, особенно справа и снизу. У части
/// пикеров полей не было вовсе. Теперь поля общие, с запасом в две точки, и меряются тестом, а
/// смещение каждого попапа учитывает их так, чтобы сама карточка вставала там же, где и раньше.
/// </para>
/// <para>
/// Через <c>x:Static</c>, а не ресурс: ресурс <c>UserControl</c> не видел бы без своего
/// подключения <c>Resources.xaml</c> (на этом уже падали оконные тесты), а замороженную тень
/// можно делить между всеми попапами разом.
/// </para>
/// </remarks>
public static class PopupShadow
{
    /// <summary>Поля вокруг карточки — место, куда тень рисуется наружу.</summary>
    public static readonly Thickness Margin = new(14, 14, 22, 22);

    /// <summary>Тень карточки.</summary>
    public static readonly DropShadowEffect Effect = Create();

    private static DropShadowEffect Create()
    {
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5 };
        shadow.Freeze();
        return shadow;
    }
}
