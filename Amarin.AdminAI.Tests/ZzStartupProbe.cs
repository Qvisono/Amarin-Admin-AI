using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ZzStartupProbe
{
    private readonly WpfFixture _wpf;

    public ZzStartupProbe(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Probe()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_PROBE");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var report = _wpf.Ui.Invoke(() =>
        {
            var text = new StringBuilder();
            var items = new (string, Func<object>)[]
            {
                ("Resources.xaml", () => new ResourceDictionary { Source = new Uri("/Amarin Admin AI;component/UI/Resources.xaml", UriKind.Relative) }),
                ("SettingsPageStyles.xaml", () => new ResourceDictionary { Source = new Uri("/Amarin Admin AI;component/UI/SettingsPageStyles.xaml", UriKind.Relative) }),
                ("Strings.ru.xaml", () => new ResourceDictionary { Source = new Uri("/Amarin Admin AI;component/UI/Lang/Strings.ru.xaml", UriKind.Relative) }),
                ("SettingsKeyPage", () => new SettingsKeyPage()),
                ("SettingsInfoPage", () => new SettingsInfoPage()),
                ("SettingsSecurityPage", () => new SettingsSecurityPage()),
                ("SettingsInstructionsPage", () => new SettingsInstructionsPage()),
                ("SettingsAutomationPage", () => new SettingsAutomationPage()),
                ("HealthPanel", () => new HealthPanel()),
                ("LockScreen", () => new LockScreen()),
                ("ModelPickerPanel", () => new ModelPickerPanel()),
                ("MainWindow", () => new MainWindow()),
            };

            foreach (var (name, create) in items)
            {
                var times = new List<double>();
                for (var i = 0; i < 4; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var made = create();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    if (made is Window window)
                    {
                        window.Close();
                    }
                }

                text.AppendLine($"{name,-28} first {times[0],7:0.0} ms   warm {times.Skip(1).Average(),7:0.0} ms");
            }

            return text.ToString();
        });

        File.WriteAllText(output, report);
    }
}
