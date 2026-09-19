using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Amarin.UI
{
    /// <summary>
    /// Прослойка между прокруткой чата и лентой сообщений: увеличивает нарисованное, не трогая
    /// раскладку.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Публичный — ради разметки: лента заводится в <c>MainWindow.xaml</c>, а оттуда виден только
    /// публичный тип. Тем же и <see cref="SmoothScroll"/> с <see cref="RoundedClip"/>.
    /// </para>
    /// <para>
    /// Наверх хост сообщает <b>увеличенную</b> высоту, а ребёнка меряет и раскладывает
    /// по-прежнему. Отсюда всё и следует: <c>ExtentHeight</c> у прокрутки растёт вместе с
    /// масштабом, поэтому полоса честна, а инерция и резинка <see cref="SmoothScroll"/> покрывают
    /// приближённую ленту без единой правки в своей логике. Ребёнок же всегда меряется одной и
    /// той же шириной — неувеличенной, — и потому текст не переносится заново: ни
    /// <c>FlowDocument</c> не пересобирается, ни высоты сообщений не устаревают. Увеличение
    /// шрифта или <c>LayoutTransform</c> делали бы ровно обратное.
    /// </para>
    /// <para>
    /// Горизонталь хост не отдаёт прокрутке: у ленты <c>HorizontalScrollBarVisibility</c> остаётся
    /// <c>Disabled</c>, и приближённое полотно ездит вбок через <see cref="PanX"/>.
    /// </para>
    /// </remarks>
    public sealed class ChatZoomHost : Decorator
    {
        /// <summary>Дальше трёхкратного увеличивать нечего: читают текст, а не разглядывают пиксели.</summary>
        public const double MaxScale = 3.0;

        private readonly ScaleTransform _zoom = new(1, 1);
        private readonly TranslateTransform _pan = new();
        private double _factor = 1.0;

        public ChatZoomHost()
        {
            var group = new TransformGroup();
            group.Children.Add(_zoom);
            group.Children.Add(_pan);
            RenderTransform = group;
            RenderTransformOrigin = new Point(0, 0);

            // Обрезает ScrollContentPresenter у ленты. Своя обрезка срезала бы всё, что вылезло
            // за увеличенную ширину, — то есть ровно то, ради чего есть PanX.
            ClipToBounds = false;
        }

        /// <summary>
        /// Высота ленты без лупы, по последнему измерению.
        /// </summary>
        /// <remarks>
        /// Нужна снаружи: <c>ExtentHeight</c> публикуется только в конце прохода раскладки, и
        /// внутри кадра, в котором масштаб уже поменялся, он ещё старый. Тот, кто считает, докуда
        /// можно домотать, обязан брать высоту отсюда.
        /// </remarks>
        public double DocumentHeight { get; private set; }

        /// <summary>Во сколько раз лента увеличена. Единица — обычный вид.</summary>
        public double Scale
        {
            get => _factor;
            set
            {
                var clamped = Math.Clamp(value, 1.0, MaxScale);
                if (Math.Abs(clamped - _factor) < 0.00001)
                {
                    return;
                }

                _factor = clamped;
                _zoom.ScaleX = clamped;
                _zoom.ScaleY = clamped;

                // Меняет высоту, которую видит прокрутка, — а значит, и весь ход вертикали.
                InvalidateMeasure();
            }
        }

        /// <summary>Сдвиг приближённого полотна вбок. Ноль — левый край на месте.</summary>
        /// <remarks>Только отрисовка: раскладку не трогает, поэтому и кадр не стоит ничего.</remarks>
        public double PanX
        {
            get => _pan.X;
            set
            {
                if (_pan.X != value)
                {
                    _pan.X = value;
                }
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null)
            {
                DocumentHeight = 0;
                return default;
            }

            // Ширина — всегда неувеличенная: именно она и не даёт тексту перенестись заново.
            // Бесконечность сюда прийти не должна, но вернуть её наружу — исключение раскладки.
            var width = double.IsInfinity(constraint.Width) ? 0 : constraint.Width;
            Child.Measure(new Size(width, double.PositiveInfinity));
            DocumentHeight = Child.DesiredSize.Height;
            return new Size(width, DocumentHeight * _factor);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child is null)
            {
                return finalSize;
            }

            // Не просто DesiredSize: на коротком чате презентер растягивал ленту на всю видимую
            // высоту, и без этого она схлопнулась бы к своему содержимому.
            var height = Math.Max(Child.DesiredSize.Height, finalSize.Height / _factor);
            Child.Arrange(new Rect(0, 0, finalSize.Width, height));
            return finalSize;
        }
    }
}
