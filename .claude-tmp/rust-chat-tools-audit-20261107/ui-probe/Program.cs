using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.UI;

internal static class Program
{
    private const double Width = 760;
    private const int PreviewHeight = 900;
    private static object? _sink;
    private static readonly Func<string, FrameworkElement, bool, double> MeasureCodeWidth =
        typeof(CodeBlockView).GetMethod("MeasureWidest", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, FrameworkElement, bool, double>>();

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-results.json");
        var samples = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 7;
        if (samples < 3 || samples > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        var sizes = args.Length > 2
            ? args[2].Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray()
            : new[] { 2_000, 20_000, 100_000 };
        var kinds = args.Length > 3 ? args[3].Split(',') : new[] { "rich_markdown", "csharp_block" };

        var setupClock = Stopwatch.StartNew();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResource(app, "UI/Theme/Palette.Dark.xaml");
        LoadResource(app, "UI/Theme/Icons.Dark.xaml");
        LoadResource(app, "UI/Resources.xaml");
        var host = new Grid { Width = Width, Background = Brushes.Black };
        var box = CreateBox();
        host.Children.Add(box);
        var setupMs = setupClock.Elapsed.TotalMilliseconds;

        // Ни MainWindow, ни служебные графы, ни профили здесь не создаются. Гиперссылок,
        // картинок, вложений и интерактивных действий в синтетическом тексте нет.
        var rows = new List<object>();
        foreach (var size in sizes)
        {
            foreach (var kind in kinds)
            {
                var code = RepeatToLength("var item = new Item(42, \"synthetic\"); // local fixture\n", size);
                var text = kind switch
                {
                    "csharp_block" => "# Синтетический ответ\n\n```csharp\n" + code + "\n```\n",
                    "rich_markdown" => RepeatToLength("## Заголовок\n\nОбычный текст с **выделением**, *курсивом* и `именем`.\n\n- первый пункт\n- второй пункт\n\n", size),
                    "plain_prose" => RepeatToLength(string.Concat(Enumerable.Repeat("Обычный синтетический текст для измерения раскладки длинного сообщения. ", 10)) + "\n\n", size),
                    _ => throw new ArgumentException("Unknown fixture " + kind)
                };
                var parsed = ChatMarkdown.Parse(text);
                var stages = new List<Measurement>();

                stages.Add(Measure("parse_only", () => _sink = ChatMarkdown.Parse(text), samples));
                if (kind == "csharp_block")
                {
                    stages.Add(Measure("highlight_uncached", () => _sink = CodeHighlighter.Highlight(code, "csharp", cache: false), samples));
                    _sink = CodeHighlighter.Highlight(code, "csharp", cache: true);
                    stages.Add(Measure("highlight_cached_hit", () => _sink = CodeHighlighter.Highlight(code, "csharp", cache: true), samples));
                    stages.Add(Measure("code_line_width_uncached", () => _sink = MeasureCodeWidth(code, host, false), samples));
                    _sink = MeasureCodeWidth(code, host, true);
                    stages.Add(Measure("code_line_width_cached_hit", () => _sink = MeasureCodeWidth(code, host, true), samples));
                }

                foreach (var streaming in new[] { true, false })
                {
                    var mode = streaming ? "streaming" : "finished_cached";
                    void Write() => _sink = ChatMarkdown.Write(box, host, parsed, 13.5, 21, streaming: streaming);
                    void WriteAndLayout()
                    {
                        Write();
                        Layout(host);
                    }

                    stages.Add(Measure("build_from_parsed_" + mode, Write, samples));
                    stages.Add(Measure("layout_after_build_" + mode, () => Layout(host), samples, Write));
                    stages.Add(Measure("build_and_layout_" + mode, WriteAndLayout, samples));
                }

                ChatMarkdown.Write(box, host, parsed, 13.5, 21, streaming: true);
                Layout(host);
                var bitmap = new RenderTargetBitmap((int)Width, PreviewHeight, 96, 96, PixelFormats.Pbgra32);
                stages.Add(Measure("render_visible_900px_after_layout", () => bitmap.Render(host), samples));
                var preview = Path.Combine(Path.GetDirectoryName(output)!, $"preview-{kind}-{size}.png");
                SavePng(bitmap, preview);
                var pixelBytes = new byte[(int)Width * PreviewHeight * 4];
                bitmap.CopyPixels(pixelBytes, (int)Width * 4, 0);
                var nonBlack = 0;
                for (var p = 0; p < pixelBytes.Length; p += 4)
                {
                    if (pixelBytes[p] > 0 || pixelBytes[p + 1] > 0 || pixelBytes[p + 2] > 0)
                    {
                        nonBlack++;
                    }
                }

                var row = new
                {
                    kind,
                    inputChars = text.Length,
                    codeChars = kind == "csharp_block" ? code.Length : 0,
                    codeLines = kind == "csharp_block" ? code.Count(character => character == '\n') + 1 : 0,
                    blocks = parsed.Blocks.Count,
                    desiredHeight = host.DesiredSize.Height,
                    boxHeight = box.ActualHeight,
                    nonBlackPixels = nonBlack,
                    preview,
                    stages
                };
                rows.Add(row);
                Console.WriteLine(JsonSerializer.Serialize(row));
                box.Document = new System.Windows.Documents.FlowDocument();
                ForceGc();
            }
        }

        var report = new
        {
            measuredAtUtc = DateTime.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            culture = CultureInfo.CurrentCulture.Name,
            uiCulture = CultureInfo.CurrentUICulture.Name,
            cpuCount = Environment.ProcessorCount,
            appVersion = typeof(ChatMarkdown).Assembly.GetName().Version?.ToString(),
            setupMs,
            samples,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
            warmup = 1,
            widthDip = Width,
            bodyFont = "Segoe UI 13.5 DIP",
            codeFont = ChatFonts.Mono.Source,
            pixelsPerDip = VisualTreeHelper.GetDpi(host).PixelsPerDip,
            previewHeightPx = PreviewHeight,
            visibleWindows = app.Windows.Count,
            notes = new[]
            {
                "Standalone synthetic WPF probe; no Window objects, app.Run, AppComposition, UiServices, settings or profile stores.",
                "Application.Resources loads only embedded palette, icons and styles; ThemeManager/LanguageManager initialization is not called.",
                "No network images, URLs, files from user storage, tool execution, clipboard or input actions.",
                "Warm exploratory medians, not a cold startup measurement and not a Rust comparison.",
                "Allocations are managed bytes on the STA thread only; native WPF and GPU allocations are not counted.",
                "Render captures only top 900px after full disconnected Measure/Arrange; no presentation target or monitor frame.",
                "finished_cached repeats identical text, matching repeated rebuilds of completed code blocks; changing input will miss caches."
            },
            scenarios = rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("REPORT " + output);
        app.Shutdown();
        return 0;
    }

    private static RichTextBox CreateBox()
    {
        var box = new RichTextBox
        {
            IsDocumentEnabled = true,
            IsReadOnly = true,
            IsUndoEnabled = false,
            IsReadOnlyCaretVisible = false,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.5,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            CaretBrush = Brushes.Transparent,
            ContextMenu = null,
            FocusVisualStyle = null
        };
        box.SetResourceReference(Control.ForegroundProperty, "Text.Secondary");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Bg.Elevated");
        return box;
    }

    private static void LoadResource(Application app, string relative)
    {
        var name = Uri.EscapeDataString(typeof(ChatMarkdown).Assembly.GetName().Name!);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{name};component/{relative}", UriKind.Absolute)
        });
    }

    private static void Layout(Grid host)
    {
        host.Measure(new Size(Width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, Width, Math.Max(1, host.DesiredSize.Height)));
        host.UpdateLayout();
    }

    private static Measurement Measure(string name, Action action, int samples, Action? setup = null)
    {
        setup?.Invoke();
        action();
        ForceGc();
        var times = new double[samples];
        var allocations = new long[samples];
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        for (var i = 0; i < samples; i++)
        {
            setup?.Invoke();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        var sortedTimes = times.Order().ToArray();
        var sortedAlloc = allocations.Order().ToArray();
        var result = new Measurement(name, sortedTimes[samples / 2], sortedTimes[0], sortedTimes[^1],
            sortedAlloc[samples / 2], GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1,
            GC.CollectionCount(2) - gc2, times, allocations);
        Console.WriteLine($"{name}: {result.MedianMs:F3} ms, {result.MedianAllocatedBytes} B");
        return result;
    }

    private static string RepeatToLength(string value, int length)
    {
        var builder = new StringBuilder(length + value.Length);
        while (builder.Length < length)
        {
            builder.Append(value);
        }

        return builder.ToString(0, length);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record Measurement(string Name, double MedianMs, double MinMs, double MaxMs,
        long MedianAllocatedBytes, int Gen0, int Gen1, int Gen2, double[] SamplesMs, long[] SamplesAllocatedBytes);
}
