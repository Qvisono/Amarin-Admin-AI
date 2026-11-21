using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Сферическая развёртка. Движение указателя меняет только камеру.</summary>
internal sealed class PanoramaBackdrop
{
    private const double PointerResponseMilliseconds = 30;
    private const double PointerTransitionMilliseconds = 500;
    private const double PointerSettleDegrees = 0.02;
    private const double PointerSettleSquared = PointerSettleDegrees * PointerSettleDegrees;
    private static readonly Lazy<MeshGeometry3D> Sphere = new(BuildSphere);
    private readonly PerspectiveCamera _camera = new() { NearPlaneDistance = 0.01, FarPlaneDistance = 2 };
    private readonly GeometryModel3D _surface;
    private Material? _material;
    private double _yaw;
    private double _pitch;
    private Vector _pointerOffset;
    private Vector _pointerTarget;
    private Vector _pointerVelocity;
    private Vector? _cursorTarget;
    private Vector _pointerTransitionStart;
    private double _pointerTransitionProgress = 1;
    private long? _pointerStartedAt;
    private TimeSpan? _lastPointerFrame;

    public Viewport3D View { get; }
    internal bool IsPointerMoving => _pointerStartedAt is not null;

    public PanoramaBackdrop()
    {
        _surface = new GeometryModel3D { Geometry = Sphere.Value };
        View = new Viewport3D
        {
            Camera = _camera,
            ClipToBounds = false,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        // Сфера заполняет весь фон, а границы уже обрезает BackdropHost.
        RenderOptions.SetEdgeMode(View, EdgeMode.Aliased);
        var light = new AmbientLight(Colors.White);
        light.Freeze();
        View.Children.Add(new ModelVisual3D { Content = light });
        View.Children.Add(new ModelVisual3D { Content = _surface });
    }

    public void Show(BitmapSource image, bool windowVisible = true)
    {
        var material = new DiffuseMaterial(new ImageBrush(image));
        material.Freeze();
        _material = material;
        SetWindowVisible(windowVisible);
    }

    public void SetWindowVisible(bool visible)
    {
        var material = visible ? _material : null;
        if (material == _surface.Material)
        {
            return;
        }

        if (material is null)
        {
            // Оставляем последний ракурс для восстановления. Пока окно скрыто, WPF
            // может освободить ресурс текстуры в композиции, а готовый битмап ждёт в памяти.
            StopPointerMotion();
            _cursorTarget = null;
            _pointerTarget = _pointerOffset;
            _pointerTransitionStart = _pointerOffset;
            _pointerTransitionProgress = 0;
            View.Visibility = Visibility.Collapsed;
            _surface.Material = null;
            return;
        }

        _surface.Material = material;
        View.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        SetWindowVisible(false);
        _material = null;
        ResetPointer();
    }

    public void SetView(double yaw, double pitch, double fieldOfView)
    {
        if (_yaw != yaw || _pitch != pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
            UpdateLookDirection();
        }
        if (_camera.FieldOfView != fieldOfView)
        {
            _camera.FieldOfView = fieldOfView;
        }
    }

    public void SetPointerTarget(Vector? offset)
    {
        if (_surface.Material is null)
        {
            return;
        }

        if (offset is { } target)
        {
            target.Y = Math.Clamp(target.Y, -85 - _pitch, 85 - _pitch);
            offset = target;
        }

        if (_cursorTarget.HasValue != offset.HasValue)
        {
            _pointerTransitionStart = _pointerTarget;
            _pointerTransitionProgress = 0;
        }

        _cursorTarget = offset;
        if (IsPointerMoving)
        {
            return;
        }

        if ((_pointerOffset - offset.GetValueOrDefault()).LengthSquared <= PointerSettleSquared)
        {
            _pointerTransitionProgress = 1;
            return;
        }

        _pointerStartedAt = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnPointerFrame;
    }

    public void ResetPointer()
    {
        StopPointerMotion();
        _cursorTarget = null;
        _pointerTarget = default;
        _pointerTransitionProgress = 1;
        if (_pointerOffset != default)
        {
            _pointerOffset = default;
            UpdateLookDirection();
        }
    }

    private void OnPointerFrame(object? sender, EventArgs e)
    {
        if (_pointerStartedAt is not { } startedAt || e is not RenderingEventArgs frame)
        {
            return;
        }

        var now = frame.RenderingTime;
        if (_lastPointerFrame is { } previous && now <= previous)
        {
            return;
        }

        var elapsed = _lastPointerFrame is { } last ? now - last : Stopwatch.GetElapsedTime(startedAt);
        _lastPointerFrame = now;
        AdvancePointerMotion(elapsed);
    }

    internal void AdvancePointerMotion(TimeSpan elapsed)
    {
        if (!IsPointerMoving || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        // Критическое демпфирование сохраняет скорость при смене цели. Считаем решение
        // за реальное время кадра, ограничивая шаг после задержки потока интерфейса.
        var milliseconds = Math.Min(elapsed.TotalMilliseconds, 50);
        _pointerTarget = _cursorTarget.GetValueOrDefault();
        if (_pointerTransitionProgress < 1)
        {
            // На границе окна цель разгоняется и тормозит без скачка ускорения.
            // Движение курсора внутри окна обновляет её, сохраняя ход перехода.
            var progress = Math.Min(1, _pointerTransitionProgress + milliseconds / PointerTransitionMilliseconds);
            _pointerTransitionProgress = progress;
            var blend = progress * progress * progress * (10 + progress * (6 * progress - 15));
            _pointerTarget = _pointerTransitionStart + (_pointerTarget - _pointerTransitionStart) * blend;
        }

        var step = milliseconds / PointerResponseMilliseconds;
        var decay = Math.Exp(-step);
        var error = _pointerOffset - _pointerTarget;
        var slope = _pointerVelocity + error / PointerResponseMilliseconds;
        _pointerOffset = _pointerTarget + (error + slope * milliseconds) * decay;
        _pointerVelocity = (_pointerVelocity - slope * step) * decay;
        if (_pointerTransitionProgress >= 1 && (_pointerTarget - _pointerOffset).LengthSquared <= PointerSettleSquared &&
            (_pointerVelocity * PointerResponseMilliseconds).LengthSquared <= PointerSettleSquared)
        {
            _pointerOffset = _pointerTarget;
            StopPointerMotion();
        }

        UpdateLookDirection();
    }

    private void StopPointerMotion()
    {
        if (IsPointerMoving)
        {
            CompositionTarget.Rendering -= OnPointerFrame;
            _pointerStartedAt = null;
            _lastPointerFrame = null;
            _pointerVelocity = default;
        }
    }

    private void UpdateLookDirection()
    {
        var yaw = _yaw + _pointerOffset.X;
        var pitch = Math.Clamp(_pitch + _pointerOffset.Y, -85, 85);
        var look = Direction(yaw * Math.PI / 180, pitch * Math.PI / 180);
        if (_camera.LookDirection != look)
        {
            _camera.LookDirection = look;
        }
    }

    internal static bool HasPanoramaRatio(int width, int height) =>
        width > 1 && height > 0 && Math.Abs(width - 2L * height) <= 1;

    internal static bool IsSupportedFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > DataBundle.MaxImageBytes)
            {
                return false;
            }

            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return HasPanoramaRatio(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or
                                  ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static Vector3D Direction(double longitude, double latitude) =>
        new(Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude), -Math.Cos(latitude) * Math.Cos(longitude));

    private static MeshGeometry3D BuildSphere()
    {
        const int columns = 96;
        const int rows = 48;
        var mesh = new MeshGeometry3D();
        for (var row = 0; row <= rows; row++)
        {
            var v = (double)row / rows;
            for (var column = 0; column <= columns; column++)
            {
                var u = (double)column / columns;
                var direction = Direction((u - 0.5) * 2 * Math.PI, (0.5 - v) * Math.PI);
                mesh.Positions.Add((Point3D)direction);
                mesh.TextureCoordinates.Add(new Point(u, v));

                if (row < rows && column < columns)
                {
                    // Камера внутри сферы, поэтому лицевые стороны направлены к центру.
                    var top = row * (columns + 1) + column;
                    var bottom = top + columns + 1;
                    mesh.TriangleIndices.Add(top);
                    mesh.TriangleIndices.Add(bottom);
                    mesh.TriangleIndices.Add(top + 1);
                    mesh.TriangleIndices.Add(top + 1);
                    mesh.TriangleIndices.Add(bottom);
                    mesh.TriangleIndices.Add(bottom + 1);
                }
            }
        }

        mesh.Freeze();
        return mesh;
    }
}
