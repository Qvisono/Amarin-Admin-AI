using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Печать и PDF чата (D6): документ WPF из <see cref="ChatExportDocument"/> и картинки формул
/// для HTML-выгрузки.
/// </summary>
/// <remarks>
/// <para>
/// Текст сообщений рисует тот же <see cref="ChatMarkdown"/>, что и лента, — таблицы, код и
/// формулы на бумаге такие же, как на экране. Цвета берутся из ресурсов, поэтому в ресурсы
/// самого документа кладётся светлая палитра: при тёмной теме иначе на бумагу ушёл бы светлый
/// текст. Кнопки внутри блоков кода («Копировать») на бумаге не нужны и прячутся.
/// </para>
/// <para>
/// PDF — через принтер «Microsoft Print to PDF»: ноль байт к программе. Своя библиотека PDF
/// потребовала бы заново сверстать и Markdown, и формулы.
/// </para>
/// </remarks>
internal static class ChatPrint
{
    private const double BodySize = 12.5;
    private const double BodyLine = 19;

    /// <summary>
    /// Несколько чатов одним документом — для печати и PDF выбранных в списке: каждый чат с новой
    /// страницы, оформление и палитра — у первого.
    /// </summary>
    public static FlowDocument BuildMany(FrameworkElement host, IReadOnlyList<ChatExportDocument> exports, Size page)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var merged = Build(host, exports[0], page);
        for (var i = 1; i < exports.Count; i++)
        {
            var next = Build(host, exports[i], page);
            var blocks = next.Blocks.ToList();
            next.Blocks.Clear();
            for (var j = 0; j < blocks.Count; j++)
            {
                if (j == 0)
                {
                    blocks[j].BreakPageBefore = true;
                }

                merged.Blocks.Add(blocks[j]);
            }
        }

        return merged;
    }

    public static FlowDocument Build(FrameworkElement host, ChatExportDocument export, Size page)
    {
        var document = new FlowDocument
        {
            PageWidth = page.Width,
            PageHeight = page.Height,
            PagePadding = new Thickness(56, 52, 56, 52),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = BodySize,
            LineHeight = BodyLine,
            TextAlignment = TextAlignment.Left,
            Background = Brushes.White
        };
        document.Resources.MergedDictionaries.Add(ThemeManager.LoadPrintPalette());
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Text.Secondary");

        var title = new Paragraph(new Run(export.Title)) { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        document.Blocks.Add(title);
        var subtitle = new Paragraph(new Run(ChatExport.Subtitle(export))) { FontSize = 10.5, Margin = new Thickness(0, 0, 0, 14) };
        subtitle.SetResourceReference(TextElement.ForegroundProperty, "Text.Dim");
        document.Blocks.Add(subtitle);

        foreach (var message in export.Messages)
        {
            var meta = new Paragraph(new Run(ChatExport.MessageMeta(message, export.IncludeCosts)))
            {
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 4),
                KeepWithNext = true,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0)
            };
            meta.SetResourceReference(TextElement.ForegroundProperty, message.Role == "user" ? "Accent.Fill" : "Text.Dim");
            meta.SetResourceReference(Block.BorderBrushProperty, "Border.Default");
            document.Blocks.Add(meta);

            foreach (var quote in message.Quotes)
            {
                var quoted = new Paragraph(new Run(quote.Text))
                {
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(10, 2, 0, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0)
                };
                quoted.SetResourceReference(Block.BorderBrushProperty, "Accent.Fill");
                quoted.SetResourceReference(TextElement.ForegroundProperty, "Text.Dim");
                document.Blocks.Add(quoted);
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                // Разметку строит лента в свой RichTextBox; блоки переезжают в печатный документ,
                // и их ссылки на ресурсы переразрешаются уже по светлой палитре.
                var box = new RichTextBox { Width = Math.Max(200, page.Width - 112) };
                ChatMarkdown.Write(box, host, message.Text, BodySize, BodyLine, fillAvailableWidth: false);
                var blocks = box.Document.Blocks.ToList();
                box.Document.Blocks.Clear();
                document.Blocks.AddRange(blocks);
            }

            foreach (var image in message.Images)
            {
                if (Decode(image.Base64) is { } bitmap)
                {
                    var width = Math.Min(bitmap.PixelWidth, page.Width - 112);
                    document.Blocks.Add(new BlockUIContainer(new Image { Source = bitmap, Width = width, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left })
                    {
                        Margin = new Thickness(0, 4, 0, 8)
                    });
                }
            }

            if (message.Files.Count > 0)
            {
                var files = new Paragraph { FontSize = 10.5, Margin = new Thickness(0, 2, 0, 6) };
                files.Inlines.Add(new Bold(new Run(Loc.Get("S.Export.Files") + " ")));
                files.Inlines.Add(new Run(string.Join(", ", message.Files)));
                files.SetResourceReference(TextElement.ForegroundProperty, "Text.Dim");
                document.Blocks.Add(files);
            }

            foreach (var tool in message.Tools)
            {
                var head = new Paragraph(new Run(Loc.Get("S.Export.Tool") + ": " + tool.Name + (tool.Success ? "" : " — " + Loc.Get("S.Export.ToolFailed"))))
                {
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2),
                    KeepWithNext = true
                };
                head.SetResourceReference(TextElement.ForegroundProperty, tool.Success ? "Text.Dim" : "Status.Danger");
                document.Blocks.Add(head);
                document.Blocks.Add(Mono(tool.Arguments));
                if (!string.IsNullOrWhiteSpace(tool.Result))
                {
                    document.Blocks.Add(Mono(tool.Result));
                }
            }
        }

        HideButtons(document);
        return document;
    }

    private static Paragraph Mono(string text)
    {
        var paragraph = new Paragraph(new Run(text.TrimEnd()))
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 9.5,
            LineHeight = 13,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 4)
        };
        paragraph.SetResourceReference(Block.BackgroundProperty, "Bg.Panel");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Text.Secondary");
        return paragraph;
    }

    private static BitmapImage? Decode(string base64)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or InvalidOperationException)
        {
            // Битая картинка не повод отказываться печатать весь разговор.
            return null;
        }
    }

    /// <summary>Кнопки блоков кода и карточек файлов — экранные; на бумаге они только шум.</summary>
    internal static void HideButtons(FlowDocument document)
    {
        var stack = new Stack<object>(LogicalTreeHelper.GetChildren(document).Cast<object>());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ButtonBase button)
            {
                button.Visibility = Visibility.Collapsed;
                continue;
            }

            if (node is DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent))
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// Картинка формулы для HTML: PNG вдвое плотнее экрана, тёмные чернила на прозрачном.
    /// Null — не нарисовалась, и в выгрузке останется LaTeX.
    /// </summary>
    internal static string? FormulaPng(FrameworkElement host, string latex, bool display)
    {
        try
        {
            var holder = new Border { Padding = new Thickness(1, 2, 1, 2), Background = Brushes.Transparent };
            holder.Resources["Export.Ink"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
            holder.Child = MathRenderer.Build(host, latex, display ? 17 : 15, display, "Export.Ink");
            holder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            holder.Arrange(new Rect(holder.DesiredSize));
            holder.UpdateLayout();

            const double scale = 2;
            var width = (int)Math.Ceiling(holder.ActualWidth * scale);
            var height = (int)Math.Ceiling(holder.ActualHeight * scale);
            if (width < 1 || height < 1 || width > 8000 || height > 8000)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(holder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Формула, которую движок не разобрал, в выгрузке остаётся текстом.
            return null;
        }
    }
}
