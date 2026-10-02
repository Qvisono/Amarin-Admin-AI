using System.Xml.Linq;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Попап и подсказка — отдельные окна. Кисть, которую «стекло» делает полупрозрачной, показала бы
/// сквозь них страницу и рабочий стол: так выпадашки настроек читались плохо до 1.29.0.
/// </summary>
public sealed class PopupOpacityTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Transparent_popups_and_tooltips_are_backed_by_a_brush_glass_never_thins()
    {
        var glass = AppearanceManager.GlassKeys.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var file in SourceTree.SourceFiles().Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var roots = document.Descendants(Presentation + "Popup")
                .Where(popup => (string?)popup.Attribute("AllowsTransparency") == "True")
                .Concat(document.Descendants(Presentation + "ControlTemplate")
                    .Where(template => (string?)template.Attribute("TargetType") == "ToolTip"));

            foreach (var root in roots)
            {
                var backing = root.Descendants().FirstOrDefault(element =>
                    element.Attribute("Background") is not null || element.Attribute("Fill") is not null);
                var value = (string?)backing?.Attribute("Background") ?? (string?)backing?.Attribute("Fill") ?? "";
                var key = value.StartsWith("{DynamicResource ", StringComparison.Ordinal)
                    ? value["{DynamicResource ".Length..].TrimEnd('}').Trim()
                    : "";
                if (glass.Contains(key))
                {
                    var line = ((System.Xml.IXmlLineInfo)backing!).LineNumber;
                    offenders.Add($"{Path.GetFileName(file)}:{line} {key}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_settings_dropdown_is_one_of_them()
    {
        var file = Path.Combine(SourceTree.ProjectDirectory, "UI", "SettingsPageStyles.xaml");
        var combo = XDocument.Load(file).Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key") == "DarkComboBox");

        var popup = combo.Descendants(Presentation + "Popup").Single();
        Assert.Equal("True", (string?)popup.Attribute("AllowsTransparency"));
        Assert.Equal("{DynamicResource Bg.Panel}", (string?)popup.Elements().First().Attribute("Background"));
    }
}
