using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ScreenshotTool : ITool
{
    private const int MaxDimension = 1920;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public string Name => "capture_screenshot";
    public string Description =>
        "Capture a screenshot of the user's Windows desktop. " +
        "Use when the user asks to see the screen or when visual context helps diagnose UI errors, dialogs, or settings.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "scope": {
              "type": "string",
              "enum": ["primary", "all_monitors"],
              "description": "primary = main monitor, all_monitors = full virtual desktop"
            },
            "save_path": {
              "type": "string",
              "description": "Optional path to save PNG. Must not overwrite an existing file."
            }
          }
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = "primary";
            if (arguments.TryGetProperty("scope", out var scopeProp) &&
                scopeProp.ValueKind == JsonValueKind.String)
            {
                scope = scopeProp.GetString() ?? "primary";
            }

            string? savePath = null;
            if (arguments.TryGetProperty("save_path", out var pathProp) &&
                pathProp.ValueKind == JsonValueKind.String)
            {
                savePath = Path.GetFullPath(pathProp.GetString()!);
            }

            using var bitmap = CaptureScreen(scope);
            using var resized = DownscaleIfNeeded(bitmap);

            if (savePath is not null)
            {
                if (File.Exists(savePath))
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"Файл уже существует: {savePath}. Перезапись запрещена."));
                }

                var directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                resized.Save(savePath, ImageFormat.Png);
            }

            using var stream = new MemoryStream();
            resized.Save(stream, ImageFormat.Png);
            var base64 = Convert.ToBase64String(stream.ToArray());

            if (savePath is null)
            {
                return Task.FromResult(ToolResult.WithImage(
                    $"Скриншот {resized.Width}×{resized.Height} ({scope})", base64));
            }

            var saved = new SavedFile(savePath, Path.GetFileName(savePath), new FileInfo(savePath).Length);
            return Task.FromResult(
                ToolResult.WithImage($"Скриншот {resized.Width}×{resized.Height}, сохранён: {savePath}", base64)
                    with { Files = [saved] });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Screenshot error: {ex.Message}"));
        }
    }

    private static Bitmap CaptureScreen(string scope)
    {
        var bounds = scope.Equals("all_monitors", StringComparison.OrdinalIgnoreCase)
            ? MonitorBounds.CaptureVirtualScreenBounds()
            : MonitorBounds.CapturePrimaryScreenBounds();

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Bitmap DownscaleIfNeeded(Bitmap source)
    {
        var maxSide = Math.Max(source.Width, source.Height);
        if (maxSide <= MaxDimension)
        {
            return (Bitmap)source.Clone();
        }

        var ratio = MaxDimension / (double)maxSide;
        var width = (int)Math.Round(source.Width * ratio);
        var height = (int)Math.Round(source.Height * ratio);

        var resized = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static class MonitorBounds
    {
        private static Rectangle _virtual = Rectangle.Empty;

        public static Rectangle CapturePrimaryScreenBounds()
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            return new Rectangle(0, 0, width, height);
        }

        public static Rectangle CaptureVirtualScreenBounds()
        {
            _virtual = Rectangle.Empty;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);

            if (_virtual == Rectangle.Empty)
            {
                return CapturePrimaryScreenBounds();
            }

            return _virtual;
        }

        private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            var rect = Rectangle.FromLTRB(lprcMonitor.Left, lprcMonitor.Top, lprcMonitor.Right, lprcMonitor.Bottom);
            _virtual = _virtual == Rectangle.Empty ? rect : Rectangle.Union(_virtual, rect);
            return true;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}