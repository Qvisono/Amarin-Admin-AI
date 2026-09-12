using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

internal static class ModelBrand
{
    /// <summary>Only Auto: inset inside the logo slot (does not grow the box).</summary>
    public static readonly Thickness AutoLogoMargin = new(2);

    private static readonly DependencyProperty LogoSlotSizeProperty =
        DependencyProperty.RegisterAttached(
            "LogoSlotSize",
            typeof(double),
            typeof(ModelBrand),
            new PropertyMetadata(double.NaN));

    public static Thickness LogoMargin(string? resourceKey) =>
        string.Equals(resourceKey, "Auto", StringComparison.Ordinal)
            ? AutoLogoMargin
            : new Thickness(0);

    public static void ApplyLogoBox(Image image, string? resourceKey)
    {
        var slot = SlotSize(image);
        var margin = LogoMargin(resourceKey);
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
        image.Stretch = Stretch.Uniform;
        image.Width = Math.Max(1, slot - margin.Left - margin.Right);
        image.Height = Math.Max(1, slot - margin.Top - margin.Bottom);
        image.Margin = margin;
    }

    public static void Apply(
        FrameworkElement host,
        string modelId,
        Image? image,
        TextBlock? letter,
        System.Windows.Shapes.Path? lightning)
    {
        if (lightning is not null)
        {
            lightning.Visibility = Visibility.Collapsed;
        }

        var key = VeniceModelCatalog.GetLogoResourceKey(modelId);
        if (key is not null && host.TryFindResource(key) is ImageSource source)
        {
            if (image is not null)
            {
                ThemeImages.Assign(image, key, source);
                ApplyLogoBox(image, key);
                image.Visibility = Visibility.Visible;
            }

            if (letter is not null)
            {
                letter.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (image is not null)
        {
            ApplyLogoBox(image, null);
            image.Visibility = Visibility.Collapsed;
        }

        if (letter is not null)
        {
            letter.Text = VeniceModelCatalog.GetLogoLetter(modelId);
            letter.Visibility = Visibility.Visible;
        }
    }

    private static double SlotSize(Image image)
    {
        var stored = (double)image.GetValue(LogoSlotSizeProperty);
        if (!double.IsNaN(stored) && stored > 0)
        {
            return stored;
        }

        var size = image.Width;
        if (double.IsNaN(size) || size <= 0)
        {
            size = image.ActualWidth;
        }

        if (double.IsNaN(size) || size <= 0)
        {
            size = 20;
        }

        image.SetValue(LogoSlotSizeProperty, size);
        return size;
    }
}
