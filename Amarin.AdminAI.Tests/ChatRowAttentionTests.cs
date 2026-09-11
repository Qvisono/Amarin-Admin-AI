using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Метка «ответ доспел, пока смотрели другой чат» на строке списка.
/// </summary>
/// <remarks>
/// Ходов идёт до трёх, а тост о готовом ответе глушится, пока окно в фокусе, — фоновый ответ
/// до сих пор не оставлял по себе вообще никакого следа. Здесь проверяется и проводка триггера,
/// и то, что вспышка действительно видна в каждом пресете из <see cref="ThemeCatalog"/>, а не
/// только в тёмном: цвет у неё один на все темы и берётся из палитры.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ChatRowAttentionTests
{
    private readonly WpfFixture _wpf;

    public ChatRowAttentionTests(WpfFixture wpf) => _wpf = wpf;

    private const double RowWidth = 184;
    private const double RowHeight = 32;

    /// <summary>Пик вспышки — то же значение, что стоит в анимации шаблона.</summary>
    private const double FlashPeak = 0.22;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    /// <summary>Строка списка вне панели: стиль лежит в ресурсах боковой колонки.</summary>
    private static (Border Host, Button Row) BuildRow()
    {
        var panel = (Panel)Window().FindName("ChatListPanel")!;
        var row = new Button
        {
            Style = (Style)panel.FindResource("ChatItem"),
            Content = "Разговор про диски",
            Tag = "test"
        };

        var host = new Border { Width = RowWidth, Height = RowHeight, Child = row };
        host.SetResourceReference(Border.BackgroundProperty, "Bg.Sidebar");
        return (host, row);
    }

    private static byte[] Paint(FrameworkElement element)
    {
        element.Measure(new Size(RowWidth, RowHeight));
        element.Arrange(new Rect(0, 0, RowWidth, RowHeight));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)RowWidth, (int)RowHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>Средняя разница по каналам, 0..255.</summary>
    private static double MeanDifference(byte[] first, byte[] second)
    {
        var total = 0L;
        for (var i = 0; i < first.Length; i++)
        {
            total += Math.Abs(first[i] - second[i]);
        }

        return (double)total / first.Length;
    }

    [Fact]
    public void The_flash_is_visible_in_every_preset()
    {
        var problems = _wpf.Ui.Invoke(() =>
        {
            var failures = new List<string>();
            var previous = ThemeManager.Current.Theme;

            try
            {
                foreach (var preset in ThemeCatalog.Presets)
                {
                    ThemeManager.Apply(preset.Theme);

                    var (host, row) = BuildRow();
                    host.Measure(new Size(RowWidth, RowHeight));
                    host.Arrange(new Rect(0, 0, RowWidth, RowHeight));
                    host.UpdateLayout();
                    row.ApplyTemplate();

                    if (row.Template.FindName("Flash", row) is not FrameworkElement flash)
                    {
                        failures.Add($"{preset.PaletteName}: в шаблоне нет Flash");
                        continue;
                    }

                    // Значение ставится руками, а не через триггер: анимация длится 0,45 с, и
                    // снимок в произвольный её момент поймал бы что угодно. Проверяется пик —
                    // то, что человек и должен увидеть.
                    flash.Opacity = 0;
                    var idle = Paint(host);

                    flash.Opacity = FlashPeak;
                    var flashed = Paint(host);

                    var difference = MeanDifference(idle, flashed);
                    if (difference < 6)
                    {
                        failures.Add($"{preset.PaletteName}: вспышка почти не видна ({difference:F1})");
                    }
                }
            }
            finally
            {
                ThemeManager.Apply(previous);
            }

            return failures;
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void The_marker_shows_up_only_when_the_chat_is_flagged()
    {
        var (idle, flagged, afterClearing) = _wpf.Ui.Invoke(() =>
        {
            var (host, row) = BuildRow();
            host.Measure(new Size(RowWidth, RowHeight));
            host.Arrange(new Rect(0, 0, RowWidth, RowHeight));
            host.UpdateLayout();
            row.ApplyTemplate();

            var dot = (FrameworkElement)row.Template.FindName("Done", row)!;
            var before = dot.Visibility;

            ChatRowState.SetNeedsAttention(row, true);
            row.UpdateLayout();
            var during = dot.Visibility;

            ChatRowState.SetNeedsAttention(row, false);
            row.UpdateLayout();
            return (before, during, dot.Visibility);
        });

        Assert.Equal(Visibility.Collapsed, idle);
        Assert.Equal(Visibility.Visible, flagged);
        Assert.Equal(Visibility.Collapsed, afterClearing);
    }

    [Fact]
    public void Lighting_the_marker_makes_the_list_redraw_itself()
    {
        // Ловушка, на которой это и ломалось: RefreshChatList пересобирает панель целиком и
        // коротко замыкает на неизменной подписи. Флаг, не вошедший в подпись, зажигался бы
        // ровно до первой перерисовки — а перерисовку зовёт сам же ход, по несколько раз за
        // секунду. Подпись обязана меняться вместе с набором.
        var (without, with) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            const string id = "чат-которого-нет";

            var attention = (HashSet<string>)typeof(MainWindow)
                .GetField("_attention", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window)!;

            var build = typeof(MainWindow).GetMethod(
                "BuildChatListSignature",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            var items = new List<ChatIndexEntry>
            {
                new() { Id = id, Title = "Разговор про диски", UpdatedAt = new DateTime(2026, 1, 1) }
            };

            var restore = attention.Contains(id);
            try
            {
                attention.Remove(id);
                var quiet = (string)build.Invoke(window, ["", items])!;

                attention.Add(id);
                return (quiet, (string)build.Invoke(window, ["", items])!);
            }
            finally
            {
                if (restore)
                {
                    attention.Add(id);
                }
                else
                {
                    attention.Remove(id);
                }
            }
        });

        Assert.NotEqual(without, with);
    }
}
