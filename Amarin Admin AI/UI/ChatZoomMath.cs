using System.Windows;

namespace Amarin.UI
{
    /// <summary>
    /// Вся арифметика лупы над лентой чата: шаг колеса, догон масштаба, якорь под курсором,
    /// границы сдвига — и решение, берём ли мы колесо себе.
    /// </summary>
    /// <remarks>
    /// Отдельно от интерфейса по той же причине, что и <see cref="PanZoom"/>: так это проверяется
    /// тестами без окна и без потока интерфейса. Для решения о колесе это не удобство, а
    /// необходимость — <c>Keyboard.Modifiers</c> в тестах не подделать, <c>RaiseEvent</c> его не
    /// ставит, и проверить условие можно только там, где оно живёт чистой функцией.
    /// <para>
    /// Единицы. «Лента» (document) — координаты неувеличенной <c>MessagesPanel</c>; «вид» (view) —
    /// то, чем считает <c>ScrollViewer</c>, то есть лента, умноженная на масштаб. Точка ленты
    /// <c>d</c> оказывается в виде на <c>d * scale</c>, а на экране — на <c>d * scale - offsetY</c>.
    /// </para>
    /// </remarks>
    internal static class ChatZoomMath
    {
        /// <summary>Обычный вид. Ниже не опускаемся: лупа только приближает.</summary>
        public const double MinScale = 1.0;

        /// <summary>Множитель на один щелчок колеса.</summary>
        /// <remarks>
        /// Геометрически, а не прибавкой: иначе у дальнего края шаг ощущался бы мельче ближнего.
        /// </remarks>
        public const double WheelBase = 1.2;

        /// <summary>Скорость, с которой нарисованный масштаб догоняет заданный колесом.</summary>
        public const double ZoomFollow = 18.0;

        /// <summary>Трение горизонтального броска. Как у <see cref="SmoothScroll"/> — чтобы обе оси ощущались одинаково.</summary>
        public const double Friction = 5.6;

        /// <summary>Ниже этой скорости бросок считается кончившимся. Как у <see cref="SmoothScroll"/>.</summary>
        public const double StopVelocity = 16.0;

        /// <summary>Границы шага времени. Как у <see cref="SmoothScroll"/>: провал кадра не должен швырять ленту.</summary>
        public const double MinDt = 1.0 / 240.0;

        /// <inheritdoc cref="MinDt"/>
        public const double MaxDt = 1.0 / 30.0;

        /// <summary>Ближе этого к цели масштаб можно защёлкнуть: разницы уже не видно.</summary>
        public const double SettleScale = 0.0005;

        /// <summary>Зажимает шаг времени между кадрами.</summary>
        public static double StepTime(double seconds) =>
            seconds < MinDt ? MinDt : seconds > MaxDt ? MaxDt : seconds;

        /// <summary>Куда колесо двигает заданный масштаб.</summary>
        public static double StepScale(double target, int delta) =>
            Math.Clamp(target * Math.Pow(WheelBase, delta / 120.0), MinScale, ChatZoomHost.MaxScale);

        /// <summary>
        /// Один кадр догона: нарисованный масштаб подтягивается к заданному.
        /// </summary>
        /// <remarks>
        /// Через <c>Exp(-k*dt)</c>, а не долей за кадр: иначе на 144 Гц приближение ехало бы вдвое
        /// быстрее, чем на 60. Та же форма, что у трения <see cref="SmoothScroll"/>.
        /// </remarks>
        public static double FollowScale(double current, double target, double dt) =>
            target + ((current - target) * Math.Exp(-ZoomFollow * dt));

        /// <summary>Докуда можно домотать вниз при этом масштабе.</summary>
        public static double MaxOffsetY(double documentHeight, double scale, double viewportHeight)
        {
            var max = (documentHeight * scale) - viewportHeight;
            return max > 0 ? max : 0;
        }

        /// <summary>Какая точка ленты сейчас под курсором.</summary>
        public static double DocumentY(double offsetY, double cursorY, double scale) =>
            (offsetY + cursorY) / scale;

        /// <inheritdoc cref="DocumentY"/>
        public static double DocumentX(double panX, double cursorX, double scale) =>
            (cursorX - panX) / scale;

        /// <summary>Смещение, при котором точка ленты снова окажется под тем же пикселем.</summary>
        public static double OffsetForDocumentY(
            double documentY,
            double cursorY,
            double scale,
            double documentHeight,
            double viewportHeight) =>
            Math.Clamp(
                (documentY * scale) - cursorY,
                0,
                MaxOffsetY(documentHeight, scale, viewportHeight));

        /// <inheritdoc cref="OffsetForDocumentY"/>
        public static double PanForDocumentX(double documentX, double cursorX, double scale, double viewportWidth) =>
            ClampPanX(cursorX - (documentX * scale), scale, viewportWidth);

        /// <summary>
        /// Держит полотно в границах: за левый и правый край его не утащить.
        /// </summary>
        /// <remarks>
        /// При обычном виде полотно ровно по ширине — сдвигать некуда, и диапазон вырождается
        /// в ноль. Резинки по горизонтали нет намеренно: вбок лента ездит только там, где её
        /// раздвинула лупа, и отскок на этом краю читался бы как рывок.
        /// </remarks>
        public static double ClampPanX(double panX, double scale, double viewportWidth)
        {
            var min = viewportWidth * (1 - scale);
            if (min >= 0)
            {
                return 0;
            }

            return Math.Clamp(panX, min, 0);
        }

        /// <summary>Один кадр горизонтального броска. Отдаёт новую скорость.</summary>
        public static double FollowVelocity(double velocity, double dt) => velocity * Math.Exp(-Friction * dt);

        /// <summary>
        /// Колесо наше: жест начат с Ctrl, и курсор вправду над лентой.
        /// </summary>
        /// <param name="ctrlDown">
        /// Любой Ctrl, а не только правый.
        /// <para>
        /// Просили правый, и сперва он и стоял — <c>Keyboard.IsKeyDown(Key.RightCtrl)</c>. На живой
        /// машине эта проверка молчала, и жест не срабатывал вовсе: колесо уходило обычной
        /// прокрутке, как будто лупы нет. Всё остальное в цепочке при этом работало, так что
        /// различение левого и правого и было единственной точкой отказа. Держаться за него не за
        /// чем: Ctrl с колесом в программе больше никто не занимает, а левый Ctrl под той же рукой,
        /// что и колесо, — им приближать даже удобнее.
        /// </para>
        /// </param>
        /// <param name="blocked">Поверх чата открыт оверлей — настройки или просмотр картинки.</param>
        /// <param name="sameSource">
        /// Событие пришло из этого же окна. Выпадашка модели геометрически лежит над лентой, но
        /// живёт отдельным окном, и колесо в ней — не наше дело.
        /// </param>
        public static bool ClaimsWheel(bool ctrlDown, bool blocked, bool sameSource, Point cursor, Size viewport) =>
            ctrlDown && !blocked && sameSource && Inside(cursor, viewport);

        /// <summary>Курсор над лентой. Этим же и отсекается всё, что вокруг неё: композер, шапка, колонка чатов.</summary>
        public static bool Inside(Point cursor, Size viewport) =>
            viewport.Width > 0 && viewport.Height > 0 &&
            cursor.X >= 0 && cursor.X <= viewport.Width &&
            cursor.Y >= 0 && cursor.Y <= viewport.Height;
    }
}
