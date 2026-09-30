using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Языки блоков кода (D13): имя, расширение, можно ли выполнить.</summary>
public sealed class CodeLanguagesTests
{
    [Theory]
    [InlineData("PS1", "powershell", ".ps1", true)]
    [InlineData("pwsh", "powershell", ".ps1", true)]
    [InlineData("bat", "batch", ".cmd", true)]
    [InlineData("py", "python", ".py", false)]
    [InlineData("bash", "bash", ".sh", false)]
    [InlineData(null, "text", ".txt", false)]
    [InlineData("brainfuck", "brainfuck", ".txt", false)]
    public void A_language_has_one_name_an_extension_and_a_runnable_flag(string? given, string name, string extension, bool runnable)
    {
        Assert.Equal(name, CodeLanguages.Normalize(given));
        Assert.Equal(extension, CodeLanguages.Extension(given));
        Assert.Equal(runnable, CodeLanguages.IsRunnableScript(given));
    }
}

/// <summary>Кнопки блока кода на живом WPF (D13).</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class CodeBlockActionsUiTests
{
    private readonly WpfFixture _wpf;

    public CodeBlockActionsUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                stack.Push(child);
            }
        }
    }

    private static Button ButtonNamed(FrameworkElement block, string key) =>
        Logical(block).OfType<Button>().Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == Loc.Get(key));

    [Fact]
    public void Only_windows_scripts_get_the_run_button_and_it_carries_the_code()
    {
        var (python, code, language) = _wpf.Ui.Invoke(() =>
        {
            var host = Window();
            var ps = CodeBlockView.Create(host, "Get-Service Spooler", "ps1");
            var py = CodeBlockView.Create(host, "print(1)", "python");
            var pythonHasRun = Logical(py).OfType<Button>().Any(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == Loc.Get("S.Code.RunViaAgent"));

            CodeBlockView.RunScriptEventArgs? raised = null;
            ps.AddHandler(CodeBlockView.RunScriptEvent, new RoutedEventHandler((_, e) => raised = (CodeBlockView.RunScriptEventArgs)e));
            ButtonNamed(ps, "S.Code.RunViaAgent").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (pythonHasRun, raised?.Code, raised?.Language);
        });

        Assert.False(python);
        Assert.Equal("Get-Service Spooler", code);
        Assert.Equal("powershell", language);
    }

    [Fact]
    public void Wrapping_lets_the_text_follow_the_column()
    {
        var (before, after, back) = _wpf.Ui.Invoke(() =>
        {
            var block = CodeBlockView.Create(Window(), new string('x', 400), "text");
            var box = Logical(block).OfType<RichTextBox>().Single();
            var wrap = ButtonNamed(block, "S.Code.Wrap");
            var fixedWidth = box.Width;
            wrap.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var wrapped = box.Width;
            wrap.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (fixedWidth, wrapped, box.Width);
        });

        Assert.False(double.IsNaN(before));
        Assert.True(double.IsNaN(after));
        Assert.Equal(before, back);
    }

    [Fact]
    public void Line_numbers_follow_the_setting()
    {
        var gutter = _wpf.Ui.Invoke(() =>
        {
            var previous = CodeBlockView.ShowLineNumbers;
            CodeBlockView.ShowLineNumbers = () => true;
            try
            {
                var block = CodeBlockView.Create(Window(), "a\nb\nc", "text");
                return Logical(block).OfType<TextBlock>().FirstOrDefault(text => text.Text == "1\n2\n3")?.Text;
            }
            finally
            {
                CodeBlockView.ShowLineNumbers = previous;
            }
        });

        Assert.Equal("1\n2\n3", gutter);
    }
}
