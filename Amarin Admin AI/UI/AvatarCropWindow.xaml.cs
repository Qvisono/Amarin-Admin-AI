using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Amarin.UI;

/// <summary>
/// Выбор части картинки под аватар: перетаскивание двигает, колесо и ползунок приближают, а
/// неподвижный квадрат показывает, что останется. Открыть край квадрата картинка не может —
/// аватар всегда закрашен целиком.
/// </summary>
public partial class AvatarCropWindow : Window
{
    /// <summary>Сторона сцены под фото, в независимых от устройства единицах.</summary>
    private const double StageSize = 360;

    /// <summary>Сторона сохраняемого квадрата; остальная сцена — только окружение.</summary>
    private const double CropSize = 300;

    /// <summary>Отступ квадрата внутри сцены.</summary>
    private const double CropOrigin = (StageSize - CropSize) / 2;

    /// <summary>Во сколько раз можно приблизить сверх «картинка ровно закрывает квадрат».</summary>
    private const double MaxZoomFactor = 8;

    private readonly BitmapSource _image;
    private readonly double _minScale;
    private double _scale;
    private Point _dragStart;
    private Point _panStart;
    private bool _dragging;
    private bool _sliderEcho;

    private AvatarCropWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;

        // Одна единица WPF на пиксель картинки, чтобы расчёты ниже шли в её координатах: битмап с
        // DPI не 96 в метаданных иначе лёг бы другого размера, чем хранится.
        Source.Source = image;
        Source.Width = image.PixelWidth;
        Source.Height = image.PixelHeight;

        // Наименьший масштаб, при котором квадрат закрыт целиком.
        _minScale = Math.Max(CropSize / image.PixelWidth, CropSize / image.PixelHeight);
        _scale = _minScale;

        ApplyScale(_scale);
        // Начинаем с середины картинки — обычно нужна она.
        SetPan(
            CropOrigin + (CropSize - (image.PixelWidth * _scale)) / 2,
            CropOrigin + (CropSize - (image.PixelHeight * _scale)) / 2);
        SyncSlider();
    }

    /// <summary>
    /// Выбранная область в пикселях картинки; null — окно закрыли без выбора.
    /// </summary>
    public Int32Rect? Selection { get; private set; }

    /// <summary>
    /// Просит выбрать кадр из <paramref name="image"/>. Возвращает квадрат в пикселях картинки
    /// или null, если выбор отменили.
    /// </summary>
    public static Int32Rect? Choose(BitmapSource image, Window owner)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(owner);

        var window = new AvatarCropWindow(image) { Owner = owner };
        return window.ShowDialog() == true ? window.Selection : null;
    }

    // ───────────────────────── Перемещение ─────────────────────────

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Stage);
        _panStart = new Point(Pan.X, Pan.Y);
        _dragging = Stage.CaptureMouse();
        e.Handled = true;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var now = e.GetPosition(Stage);
        SetPan(_panStart.X + (now.X - _dragStart.X), _panStart.Y + (now.Y - _dragStart.Y));
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Stage.ReleaseMouseCapture();
    }

    // ───────────────────────── Масштаб ─────────────────────────

    private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Относительно курсора: то, что под ним, остаётся на месте.
        ZoomTo(_scale * Math.Pow(1.0015, e.Delta), e.GetPosition(Stage));
        e.Handled = true;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderEcho)
        {
            return;
        }

        // Геометрически: шаг ползунка ощущается одинаковым на обоих концах шкалы.
        ZoomTo(
            _minScale * Math.Pow(MaxZoomFactor, e.NewValue),
            new Point(StageSize / 2, StageSize / 2));
    }

    private void ZoomTo(double scale, Point anchor)
    {
        var clamped = Math.Clamp(scale, _minScale, _minScale * MaxZoomFactor);
        if (Math.Abs(clamped - _scale) < 0.0000001)
        {
            return;
        }

        // Пиксель картинки под точкой привязки остаётся под ней и после.
        var sourceX = (anchor.X - Pan.X) / _scale;
        var sourceY = (anchor.Y - Pan.Y) / _scale;

        _scale = clamped;
        ApplyScale(_scale);
        SetPan(anchor.X - (sourceX * _scale), anchor.Y - (sourceY * _scale));
        SyncSlider();
    }

    private void ApplyScale(double scale)
    {
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
    }

    private void SyncSlider()
    {
        _sliderEcho = true;
        ZoomSlider.Value = Math.Log(_scale / _minScale) / Math.Log(MaxZoomFactor);
        _sliderEcho = false;
    }

    /// <summary>Сдвигает картинку, не давая открыть край квадрата.</summary>
    private void SetPan(double x, double y)
    {
        Pan.X = ClampAxis(x, _image.PixelWidth);
        Pan.Y = ClampAxis(y, _image.PixelHeight);
    }

    private double ClampAxis(double offset, int sourceLength)
    {
        var painted = sourceLength * _scale;
        // Левый край картинки не правее левого края квадрата, правый — не левее правого. На
        // наименьшем масштабе по короткой оси обе границы совпадают.
        var min = CropOrigin + CropSize - painted;
        var max = CropOrigin;
        return min >= max ? min : Math.Clamp(offset, min, max);
    }

    // ───────────────────────── Итог ─────────────────────────

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Selection = CurrentSelection();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private Int32Rect CurrentSelection()
    {
        var side = (int)Math.Round(CropSize / _scale);
        var x = (int)Math.Round((CropOrigin - Pan.X) / _scale);
        var y = (int)Math.Round((CropOrigin - Pan.Y) / _scale);

        // Округление может вынести квадрат на пиксель за край, а такой прямоугольник
        // CroppedBitmap отвергнет — возвращаем его внутрь.
        side = Math.Max(1, Math.Min(side, Math.Min(_image.PixelWidth, _image.PixelHeight)));
        x = Math.Clamp(x, 0, _image.PixelWidth - side);
        y = Math.Clamp(y, 0, _image.PixelHeight - side);
        return new Int32Rect(x, y, side, side);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // У окна без рамки заголовок — сама карточка. Сцена свои нажатия гасит сама, поэтому
        // перетаскивание фото окно не двигает.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
