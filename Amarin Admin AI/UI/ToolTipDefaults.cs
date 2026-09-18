using System.Windows;
using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>
/// Общие для всей программы задержка и время показа подсказок.
/// </summary>
/// <remarks>
/// Стилем это не задать: и <see cref="ToolTipService.InitialShowDelayProperty"/>, и
/// <see cref="ToolTipService.ShowDurationProperty"/> — вложенные свойства, которые читаются
/// с элемента-хозяина, а не с самой подсказки. Прописывать их у каждой кнопки значило бы
/// повторить одну и ту же пару строк в сорока местах и забыть в сорок первом.
/// <para>
/// Заводские значения Windows здесь не годятся: полсекунды ожидания на панели из мелких
/// значков читается как «подсказки нет», а пять секунд показа не хватает, чтобы дочитать
/// путь к файлу.
/// </para>
/// </remarks>
internal static class ToolTipDefaults
{
    private static bool _applied;

    /// <summary>
    /// Вызывается один раз при запуске, до первого окна: <c>OverrideMetadata</c> нельзя звать
    /// после того, как свойство впервые прочитали, — система бросит исключение.
    /// </summary>
    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(220));
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
    }
}
