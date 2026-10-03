using System.Text.RegularExpressions;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Всё, что человек читает, идёт через <c>Loc</c>: русский литерал в коде — это подпись, до
/// которой не дойдёт выбранный язык.
/// </summary>
/// <remarks>
/// До 1.30.0 проверка жила в оконном классе локализации и шла только на Windows, хотя окна ей не
/// нужно — она читает файлы. И держала список файлов поимённо: страница обновлений и оба её файла
/// в Core доехали до релиза целиком по-русски именно потому, что в списке их не было. Теперь у
/// папок, где русских литералов нет совсем, правило на всю папку — новый файл в ней проверяется
/// сам. Поимённо остаются файлы из папок, где литералы законны: промпты для модели и строки,
/// которые она читает, пишутся по-русски намеренно.
/// </remarks>
public sealed partial class SourceLiteralTests
{
    [Theory]
    [InlineData("UI", "PasswordWindow.xaml.cs")]
    [InlineData("UI", "MainWindow.Account.cs")]
    [InlineData("UI", "MainWindow.Updates.cs")]
    [InlineData("UI", "MainWindow.DataBundle.cs")]
    [InlineData("UI", "MainWindow.About.cs")]
    [InlineData("UI", "SettingsInfoPage.xaml.cs")]
    [InlineData("UI", "GuideShot.xaml.cs")]
    [InlineData("UI", "SettingsInstructionsPage.xaml.cs")]
    [InlineData("UI", "MainWindow.Instructions.cs")]
    [InlineData("Chat", "InstructionLibrary.cs")]
    [InlineData("Chat", "InstructionBriefing.cs")]
    [InlineData("Tools", "ReadInstructionTool.cs")]
    // 1.28.0: окно подтверждения, статусы агента, «Поделиться», просмотр картинок, отчёт о
    // сбое, экран блокировки и удаление данных — всё, что человек читает, идёт через Loc.
    [InlineData("UI", "CodeBlockView.cs")]
    [InlineData("UI", "MainWindow.Sharing.cs")]
    [InlineData("UI", "MainWindow.ImageViewer.cs")]
    [InlineData("UI", "ImageBlockView.cs")]
    [InlineData("UI", "MainWindow.Summarize.cs")]
    [InlineData("UI", "CostBreakdownTooltip.cs")]
    [InlineData("UI", "ColorPickerField.xaml.cs")]
    [InlineData("UI", "CrashHandler.cs")]
    [InlineData("UI", "CrashWindow.xaml.cs")]
    [InlineData("UI", "MainWindow.Wipe.cs")]
    [InlineData("UI", "MainWindow.Lock.cs")]
    [InlineData("UI", "LockScreen.xaml.cs")]
    [InlineData("UI", "MainWindow.Appearance.cs")]
    [InlineData("UI", "ReasoningPicker.xaml.cs")]
    [InlineData("UI", "ChatMessageViews.cs")]
    [InlineData("UI", "SettingsSecurityPage.xaml.cs")]
    [InlineData("UI", "MainWindow.Notice.cs")]
    [InlineData("UI", "MainWindow.Hotkeys.cs")]
    [InlineData("UI", "HotkeyField.xaml.cs")]
    [InlineData("Tools", "DangerousActionGuard.cs")]
    [InlineData("Agents", "Agent.cs")]
    [InlineData("Agents", "AgentUiAdapter.cs")]
    [InlineData("Chat", "ChatEngineInfographic.cs")]
    public void Screens_and_their_logic_hold_no_literal_russian(string folder, string file)
    {
        // Эти экраны написали до локализации, и подписи так и остались литералами: при
        // японском интерфейсе «Войти», «Локальный режим» и «Задан» показывались по-русски.
        var relative = Path.Combine(folder, file);
        var offenders = Offenders(SourceTree.ProjectFile(relative), relative);

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("Updates")]
    [InlineData("Data")]
    [InlineData("ChatList")]
    [InlineData("Turns")]
    public void Whole_core_folders_hold_no_literal_russian(string folder)
    {
        var files = SourceTree.CoreFolder(folder);
        Assert.NotEmpty(files);

        var offenders = files.SelectMany(path => Offenders(path, Path.Combine(folder, Path.GetFileName(path)))).ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static List<string> Offenders(string path, string shown)
    {
        var lines = File.ReadAllLines(path);
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in LiteralWithCyrillic().Matches(lines[i]))
            {
                offenders.Add($"{shown}:{i + 1} {match.Value}");
            }
        }

        return offenders;
    }

    [GeneratedRegex("\"[^\"\n]*[А-Яа-яЁё][^\"\n]*\"")]
    private static partial Regex LiteralWithCyrillic();
}
