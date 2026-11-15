using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Amarin.ModelLogoImporter;

/// <summary>Преобразует проверенные локальные логотипы в автономные ресурсы WPF.</summary>
internal static partial class Program
{
    private const string BeginMarker = "    <!-- BEGIN GENERATED MODEL LOGOS (tools/model-logos/import.ps1) -->";
    private const string EndMarker = "    <!-- END GENERATED MODEL LOGOS -->";
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = PresentationNamespace;
    private static readonly XNamespace Xaml = XamlNamespace;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly char[] ViewBoxSeparators = [' ', '\t', '\r', '\n', ','];
    private static readonly HashSet<string> SvgElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "path", "rect", "circle", "ellipse", "polygon", "polyline", "line",
        "linearGradient", "radialGradient", "stop", "clipPath", "mask", "use", "title", "desc", "style"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var check = args.Contains("--check", StringComparer.Ordinal);
            var positional = args.Where(argument => argument != "--check").ToArray();
            if (positional.Length != 1 || positional[0].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Usage: VectorImporter <repository-root> [--check]");
                return 1;
            }

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var repositoryRoot = Path.GetFullPath(positional[0]);
            var manifestDirectory = Path.Combine(repositoryRoot, "tools", "model-logos");
            var assets = ReadManifest(Path.Combine(manifestDirectory, "assets.json"));
            var bitmaps = PrepareBitmaps(repositoryRoot, manifestDirectory, assets);
            var outputs = new List<(string Path, string Text, bool Changed)>();
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var path = Path.Combine(repositoryRoot, "Amarin Admin AI", "UI", "Theme", $"AiLogos.{theme}.xaml");
                var original = File.ReadAllText(path, Encoding.UTF8);
                var output = Generate(original, assets, manifestDirectory, theme == "Dark");
                outputs.Add((path, output, !string.Equals(original, output, StringComparison.Ordinal)));
            }

            foreach (var output in outputs)
            {
                if (output.Changed && !check)
                {
                    File.WriteAllText(output.Path, output.Text, Utf8);
                }

                Console.WriteLine($"{(output.Changed ? check ? "STALE" : "UPDATED" : "OK")} {output.Path}");
            }

            foreach (var bitmap in bitmaps.Where(bitmap => bitmap.Changed))
            {
                if (!check)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(bitmap.Path)
                        ?? throw new InvalidDataException("Отсутствует папка bitmap-ресурса."));
                    File.WriteAllBytes(bitmap.Path, bitmap.Bytes);
                }

                Console.WriteLine($"{(check ? "STALE" : "UPDATED")} {bitmap.Path}");
            }

            Console.WriteLine($"Проверено {assets.Count} дополнительных логотипов в двух темах.");
            return check && (outputs.Any(output => output.Changed) || bitmaps.Any(bitmap => bitmap.Changed)) ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Не удалось подготовить логотипы: {exception.Message}");
            return 1;
        }
    }

    private static List<Asset> ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Неизвестная версия assets.json.");
        }

        var assets = new List<Asset>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var key = RequiredString(item, "key");
            if (!KeyPattern().IsMatch(key) || !keys.Add(key))
            {
                throw new InvalidDataException($"Неверный или повторный ключ логотипа: {key}.");
            }

            assets.Add(new Asset(key, RequiredString(item, "file"), RequiredString(item, "kind"),
                item.TryGetProperty("monochrome", out var monochrome) && monochrome.GetBoolean(),
                item.TryGetProperty("darkFile", out var darkFile) ? darkFile.GetString() : null,
                item.TryGetProperty("foreground", out var foreground) ? foreground.GetString() : null,
                item.TryGetProperty("inkColors", out var inkColors)
                    ? inkColors.EnumerateArray().Select(color => color.GetString()
                        ?? throw new InvalidDataException($"Неверный inkColors у {key}.")).ToArray()
                    : [],
                item.TryGetProperty("stripColorSchemeStyle", out var stripColorSchemeStyle) && stripColorSchemeStyle.GetBoolean()));
        }

        if (assets.Count == 0)
        {
            throw new InvalidDataException("assets.json не содержит логотипов.");
        }

        assets.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return assets;
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 } value
            ? value : throw new InvalidDataException($"В assets.json отсутствует {name}.");

    private static string Generate(string original, IReadOnlyList<Asset> assets, string directory, bool dark)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var baseline = RemoveGeneratedBlock(original);
        var document = XDocument.Parse(baseline);
        var root = document.Root ?? throw new InvalidDataException("Отсутствует ResourceDictionary.");
        if (root.Name != Presentation + "ResourceDictionary")
        {
            throw new InvalidDataException("Ожидался ResourceDictionary WPF.");
        }

        var existingKeys = root.Elements().Select(element => (string?)element.Attribute(Xaml + "Key"))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var asset in assets)
        {
            Console.WriteLine($"CONVERT {(dark ? "Dark" : "Light")}/{asset.Key}");
            if (existingKeys.Contains(asset.Key))
            {
                throw new InvalidDataException($"{asset.Key} уже существует вне генерируемого блока.");
            }

            var file = LocalFile(directory, dark && !string.IsNullOrEmpty(asset.DarkFile) ? asset.DarkFile : asset.File);
            var entry = asset.Kind switch
            {
                "svg" => ConvertSvg(file, asset, dark),
                "png" or "bitmap" => BitmapEntry(file, asset.Key),
                _ => throw new InvalidDataException($"Неизвестный kind у {asset.Key}: {asset.Kind}.")
            };
            entries.Add(Indent(entry, newline));
        }

        var closing = baseline.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new InvalidDataException("Не найдена граница ResourceDictionary.");
        }

        var prefix = baseline[..closing].TrimEnd('\r', '\n');
        var block = newline + newline + BeginMarker + newline + string.Join(newline, entries) + newline + EndMarker + newline;
        var output = prefix + block + baseline[closing..];
        _ = XDocument.Parse(output);
        return output;
    }

    private static string RemoveGeneratedBlock(string text)
    {
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 && end < 0)
        {
            return text;
        }

        if (begin < 0 || end < begin || text.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Повреждены границы генерируемых логотипов.");
        }

        var after = end + EndMarker.Length;
        while (after < text.Length && text[after] is '\r' or '\n')
        {
            after++;
        }

        return text[..begin] + text[after..];
    }

    private static string LocalFile(string directory, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(directory, relativePath));
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relativePath) || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Файл ресурса должен находиться внутри tools/model-logos: {relativePath}.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Не найден исходник логотипа.", full);
        }

        return full;
    }

    private static List<(string Path, byte[] Bytes, bool Changed)> PrepareBitmaps(
        string repositoryRoot, string directory, IReadOnlyList<Asset> assets)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.Where(asset => asset.Kind is "png" or "bitmap"))
        {
            string[] sources = string.IsNullOrEmpty(asset.DarkFile)
                ? [asset.File] : [asset.File, asset.DarkFile];
            foreach (var source in sources)
            {
                var path = LocalFile(directory, source);
                ValidateBitmapExtension(path);
                var filename = Path.GetFileName(path);
                var bytes = File.ReadAllBytes(path);
                if (files.TryGetValue(filename, out var previous) && !previous.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException($"Разные bitmap-ресурсы имеют одинаковое имя: {filename}.");
                }

                files[filename] = bytes;
            }
        }

        return files.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
        {
            var destination = Path.Combine(repositoryRoot, "Amarin Admin AI", "Assets", "ModelLogos", item.Key);
            var changed = !File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(item.Value);
            return (destination, item.Value, changed);
        }).ToList();
    }

    private static XElement ConvertSvg(string path, Asset asset, bool dark)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        // Стандартная декларация SVG 1.1 не нужна рисунку; её удаление не разрешает загрузку DTD или entities.
        var sourceText = Svg11DoctypePattern().Replace(File.ReadAllText(path, Encoding.UTF8), "");
        using var sourceReader = new StringReader(sourceText);
        using var source = XmlReader.Create(sourceReader, settings);
        var svg = XDocument.Load(source);
        var root = svg.Root ?? throw new InvalidDataException($"Пустой SVG: {path}.");
        if (root.Name.LocalName != "svg" || root.Name.NamespaceName != "http://www.w3.org/2000/svg")
        {
            throw new InvalidDataException($"Неверный корень SVG: {path}.");
        }

        NormalizeViewport(root, path);
        var ink = asset.Foreground ?? (dark ? "#FFFFFF" : "#16161A");
        if (asset.StripColorSchemeStyle)
        {
            var styles = root.Descendants().Where(element => element.Name.LocalName == "style").ToList();
            if (styles.Count == 0 || styles.Any(element => !ColorSchemeStylePattern().IsMatch(element.Value)))
            {
                throw new InvalidDataException($"SVG {asset.Key}: stripColorSchemeStyle не соответствует проверенному CSS.");
            }

            // В этом проверенном формате CSS задаёт только color, а currentColor ниже становится явной кистью.
            foreach (var style in styles)
            {
                style.Remove();
            }
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!SvgElements.Contains(element.Name.LocalName) || element.Name.Namespace != root.Name.Namespace)
            {
                throw new InvalidDataException($"Непроверенный элемент SVG {element.Name} в {path}.");
            }

            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                ValidateReference(attribute.Name.LocalName, attribute.Value, path);
                attribute.Value = CurrentColorPattern().Replace(attribute.Value, ink);
                if (attribute.Name.LocalName is "fill" or "stroke" or "color" or "stop-color" &&
                    asset.InkColors.Contains(attribute.Value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    attribute.Value = ink;
                }
                else if (attribute.Name.LocalName == "style")
                {
                    attribute.Value = ReplaceStyleInk(attribute.Value, asset.InkColors, ink);
                }
            }

            if (element.Name.LocalName == "style")
            {
                ValidateReference("style", element.Value, path);
                element.Value = ReplaceStyleInk(CurrentColorPattern().Replace(element.Value, ink), asset.InkColors, ink);
            }
        }

        root.SetAttributeValue("color", ink);
        if (asset.Monochrome)
        {
            root.SetAttributeValue("fill", ink);
        }

        // Только геометрия: готовое приложение не должно загружать SharpVectors или системный шрифт.
        var drawingSettings = new WpfDrawingSettings
        {
            IncludeRuntime = false,
            EnsureViewboxPosition = true,
            EnsureViewboxSize = true,
            CultureInfo = CultureInfo.InvariantCulture
        };
        using var reader = new FileSvgReader(drawingSettings);
        using var textReader = new StringReader(svg.ToString(SaveOptions.DisableFormatting));
        var drawing = reader.Read(textReader);
        if (drawing is null || drawing.Bounds.IsEmpty || drawing.Bounds.Width <= 0 || drawing.Bounds.Height <= 0)
        {
            throw new InvalidDataException($"SVG {asset.Key} дал пустой рисунок.");
        }

        var result = XElement.Parse(XamlWriter.Save(new DrawingImage(drawing)));
        if (result.DescendantsAndSelf().Any(element => element.Name.NamespaceName != PresentationNamespace ||
                element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration &&
                    attribute.Name.NamespaceName.Length != 0 && attribute.Name.NamespaceName != XamlNamespace &&
                    attribute.Name.Namespace != XNamespace.Xml)))
        {
            throw new InvalidDataException($"SVG {asset.Key} требует внешние типы XAML.");
        }

        result.SetAttributeValue(XNamespace.Xmlns + "x", XamlNamespace);
        result.SetAttributeValue(Xaml + "Key", asset.Key);
        return result;
    }

    private static void NormalizeViewport(XElement root, string path)
    {
        if (root.Attribute("viewBox") is not { } viewBox)
        {
            return;
        }

        var parts = viewBox.Value.Split(ViewBoxSeparators, StringSplitOptions.RemoveEmptyEntries);
        var values = new double[4];
        if (parts.Length != values.Length)
        {
            throw new InvalidDataException($"Неверный viewBox в SVG {path}.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index]))
            {
                throw new InvalidDataException($"Неверный viewBox в SVG {path}.");
            }
        }

        if (values[2] <= 0 || values[3] <= 0)
        {
            throw new InvalidDataException($"Пустой viewBox в SVG {path}.");
        }

        // Размеры экспорта вроде 1000x1000 не должны добавлять поле к собственной сетке знака 335x335.
        root.SetAttributeValue("width", values[2].ToString("R", CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", values[3].ToString("R", CultureInfo.InvariantCulture));
    }

    private static string ReplaceStyleInk(string value, IReadOnlyCollection<string> colors, string ink) =>
        StyleInkPattern().Replace(value, match =>
            colors.Contains(match.Groups["color"].Value.Trim(), StringComparer.OrdinalIgnoreCase)
                ? match.Groups["prefix"].Value + ink : match.Value);

    private static void ValidateReference(string name, string value, string path)
    {
        if ((name is "href" or "src") && !value.StartsWith('#'))
        {
            throw new InvalidDataException($"Внешняя ссылка в SVG {path}.");
        }

        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Внешний CSS в SVG {path}.");
        }

        if (value.Contains("@media", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SVG {path} содержит непроверенный CSS @media.");
        }

        foreach (Match match in UrlPattern().Matches(value))
        {
            if (!match.Groups[1].Value.Trim().Trim('"', '\'').StartsWith('#'))
            {
                throw new InvalidDataException($"Внешний url() в SVG {path}.");
            }
        }
    }

    private static XElement BitmapEntry(string path, string key)
    {
        ValidateBitmapExtension(path);

        return new XElement(Presentation + "BitmapImage",
            new XAttribute(XNamespace.Xmlns + "x", XamlNamespace),
            new XAttribute(Xaml + "Key", key),
            new XAttribute("CacheOption", "OnLoad"),
            new XAttribute("UriSource", "/Amarin Admin AI;component/Assets/ModelLogos/" + Path.GetFileName(path)));
    }

    private static void ValidateBitmapExtension(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".ico" or
            ".bmp" or ".gif" or ".tif" or ".tiff"))
        {
            throw new InvalidDataException($"Формат bitmap-ресурса не поддерживается WPF: {path}.");
        }
    }

    private static string Indent(XElement element, string newline) =>
        string.Join(newline, element.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => "    " + line));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("currentColor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentColorPattern();

    [GeneratedRegex("url\\(\\s*([^)]*)\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("(?<prefix>\\b(?:fill|stroke|color|stop-color)\\s*:\\s*)(?<color>[^;}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleInkPattern();

    [GeneratedRegex("\\A\\s*svg\\s*\\{\\s*color\\s*:\\s*#[a-f0-9]{3,8}\\s*;?\\s*\\}\\s*" +
        "@media\\s*\\(\\s*prefers-color-scheme\\s*:\\s*dark\\s*\\)\\s*\\{\\s*" +
        "svg\\s*\\{\\s*color\\s*:\\s*#[a-f0-9]{3,8}\\s*;?\\s*\\}\\s*\\}\\s*\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColorSchemeStylePattern();

    [GeneratedRegex("\\A\\s*<!DOCTYPE\\s+svg\\s+PUBLIC\\s+\"-//W3C//DTD SVG 1.1//EN\"\\s+" +
        "\"http://www\\.w3\\.org/Graphics/SVG/1\\.1/DTD/svg11\\.dtd\"\\s*>\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Svg11DoctypePattern();

    private sealed record Asset(string Key, string File, string Kind, bool Monochrome, string? DarkFile,
        string? Foreground, string[] InkColors, bool StripColorSchemeStyle);
}
