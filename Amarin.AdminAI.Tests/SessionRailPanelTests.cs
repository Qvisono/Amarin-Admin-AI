using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SessionRailPanelTests
{
    private readonly WpfFixture _wpf;

    public SessionRailPanelTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_rail_appears_only_when_the_window_is_wide_enough()
    {
        Assert.False(SessionRailPanel.FitsWindow(SessionRailPanel.MinWindowWidth - 1));
        Assert.True(SessionRailPanel.FitsWindow(SessionRailPanel.MinWindowWidth));
    }

    [Fact]
    public void The_rail_fits_only_to_the_right_of_both_chat_and_composer()
    {
        Assert.True(MainWindow.RailFits(1600, 1320, 1330, 250, 20));
        Assert.False(MainWindow.RailFits(1600, 1340, 1330, 250, 20));
        Assert.False(MainWindow.RailFits(1600, 1320, 1340, 250, 20));
        Assert.False(MainWindow.RailFits(0, 0, 0, 250, 20));
    }

    [Fact]
    public void The_rail_collects_successful_file_changes_and_nested_agents()
    {
        var round = new ToolRound
        {
            Calls =
            [
                new ToolCallRecord
                {
                    Name = "init_agent", Success = true,
                    NestedAgent = new AgentRunRecord
                    {
                        AgentId = "a1", DisplayName = "agent", Status = AgentRunStatus.Running,
                        ToolRounds = [new ToolRound { Calls =
                        [
                            new ToolCallRecord { Name = "write_file", Success = true, ArgumentsJson = "{\"path\":\"C:\\\\test\\\\created.txt\"}" },
                            new ToolCallRecord { Name = "edit_file", Success = false, ArgumentsJson = "{\"path\":\"C:\\\\test\\\\failed.txt\"}" }
                        ] }]
                    }
                }
            ]
        };
        var agents = new List<SessionRailAgent>();
        var files = new List<SessionRailFile>();

        MainWindow.CollectRailCalls(round, "m:0", agents, files);

        Assert.Single(agents);
        Assert.Equal("a1", agents[0].Id);
        Assert.True(agents[0].IsWorking);
        Assert.Single(files);
        Assert.EndsWith("created.txt", files[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_rail_does_not_invent_context_files_or_agents()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel);

            Assert.Equal(Visibility.Visible, panel.ContextUnavailable.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.ContextBar.Visibility);
            Assert.Equal("0", panel.FilesCount.Text);
            Assert.Equal("0", panel.AgentsCount.Text);
            Assert.Empty(panel.FilesItems.Children);
            Assert.Empty(panel.AgentItems.Children);
            Assert.False(panel.CompactButton.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void The_rail_renders_only_the_supplied_structured_session_snapshot()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            var context = new SessionRailContext(
                120,
                1_000,
                [
                    new(SessionRailContextCategory.Conversation, 70),
                    new(SessionRailContextCategory.Tools, 25),
                    new(SessionRailContextCategory.Files, 15),
                    new(SessionRailContextCategory.System, 10)
                ]);
            panel.ShowSession(new SessionRailSnapshot(
                context,
                [
                    new SessionRailAgent("a1", "Research", "anthropic:claude-haiku-5-5", true),
                    new SessionRailAgent("a2", "Review", "zai:glm-5.3-flash", false)
                ],
                [
                    new SessionRailFile(@"C:\work\answer.txt", SessionRailFileChange.Created),
                    new SessionRailFile(@"C:\work\answer.txt", SessionRailFileChange.Modified),
                    new SessionRailFile(@"C:\work\report.md", SessionRailFileChange.Created)
                ],
                new SessionRailSettings(true, false),
                true));
            Arrange(panel);

            Assert.Equal(Visibility.Collapsed, panel.ContextUnavailable.Visibility);
            Assert.Equal(Visibility.Visible, panel.ContextBar.Visibility);
            Assert.Equal(120, panel.ContextUsedColumn.Width.Value);
            Assert.Equal(880, panel.ContextFreeColumn.Width.Value);
            Assert.Equal(4, panel.ContextParts.Children.Count);

            // Категории показывают долю от занятого, а не токены.
            var shares = panel.ContextParts.Children.OfType<Grid>()
                .Select(row => row.Children.OfType<TextBlock>().Last().Text).ToArray();
            Assert.Equal((70d / 120).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), shares[0]);
            Assert.DoesNotContain(shares, share => share.Contains('≈'));

            // В строке агента только имя модели.
            var agentName = ((Grid)panel.AgentItems.Children[0]).Children.OfType<TextBlock>().First().Text;
            Assert.Equal(VeniceModelCatalog.GetDisplayName("anthropic:claude-haiku-5-5"), agentName);
            Assert.Equal("2", panel.FilesCount.Text);
            Assert.Equal(2, panel.FilesItems.Children.Count);
            Assert.Equal("2", panel.AgentsCount.Text);
            Assert.Equal(2, panel.AgentItems.Children.Count);
            Assert.True(panel.ComputerToggle.IsChecked);
            Assert.False(panel.BrowserToggle.IsChecked);
            Assert.True(panel.CompactButton.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void A_long_agent_list_remains_available_in_the_scrollable_panel()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250, Height = 300 };
            var agents = Enumerable.Range(0, 30)
                .Select(i => new SessionRailAgent("a" + i, "agent", "", false)).ToArray();
            panel.ShowSession(new SessionRailSnapshot(null, agents, [], new(false, false), false));
            panel.Measure(new Size(250, 300));
            panel.Arrange(new Rect(0, 0, 250, 300));
            panel.UpdateLayout();

            Assert.Equal(30, panel.AgentItems.Children.Count);
            Assert.Equal(Visibility.Visible, panel.AgentsCard.Visibility);
            Assert.IsType<ScrollViewer>(panel.Content);
            return true;
        });
    }

    [Fact]
    public void Local_settings_and_compaction_are_returned_to_the_session_owner()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            var changes = new List<SessionRailSettings>();
            var requests = new List<SessionRailCompactRequestedEventArgs>();
            var asked = new List<SessionRailCompactOptions>();
            panel.SettingsChanged += (_, e) => changes.Add(e.Settings);
            panel.CompactionRequested += (_, e) => requests.Add(e);
            panel.ShowSession(new SessionRailSnapshot(
                new SessionRailContext(800, 1_000, []), [], [], new SessionRailSettings(false, false), true, false,
                new SessionRailCompaction("grok-4-6", null, options =>
                {
                    asked.Add(options);
                    return options.KeepMessages > 10 ? null : new SessionRailCompactPreview(options.CompactFirst ? 200 : 300, 6, 16);
                })));
            Arrange(panel);

            panel.BrowserToggle.IsChecked = true;
            Click(panel.CompactButton);

            // Экран сжатия встаёт на место карточек, а не раскрывает список под кнопкой.
            Assert.Equal(Visibility.Visible, panel.CompactView.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.CardsPanel.Visibility);
            Assert.Equal("10", panel.KeepValue.Text);
            Assert.True(panel.CompactFirstToggle.IsChecked);
            Assert.Equal("grok-4-6", panel.CompactModelField.SelectedModelId);

            // Шкала меряет занятое сейчас (800), а не окно модели: видна освобождаемая доля.
            Assert.Equal(200, panel.CompactKeptColumn.Width.Value);
            Assert.Equal(600, panel.CompactFreedColumn.Width.Value);
            Assert.Equal(
                Loc.Format("S.SessionRail.CompactFrees", 0.75.ToString("P0", System.Globalization.CultureInfo.CurrentCulture)),
                panel.CompactFrees.Text);
            Assert.Equal(Loc.Format("S.SessionRail.CompactKeepHint", 6, 16), panel.KeepHint.Text);

            // Шапка следует за настройками.
            panel.CompactFirstToggle.IsChecked = false;
            Assert.Equal(300, panel.CompactKeptColumn.Width.Value);
            Click(panel.KeepMoreButton);
            Assert.Equal("11", panel.KeepValue.Text);
            Assert.False(panel.CompactRunButton.IsEnabled);
            Assert.Equal(Loc.Get("S.Compact.Nothing"), panel.KeepHint.Text);
            Assert.Equal(0, panel.CompactFreedColumn.Width.Value);
            Assert.Equal(Visibility.Collapsed, panel.CompactFrees.Visibility);
            Click(panel.KeepLessButton);
            Click(panel.KeepLessButton);
            Assert.True(panel.CompactRunButton.IsEnabled);

            panel.CompactInstructionsBox.Text = "  keep the paths  ";
            Click(panel.CompactRunButton);

            Assert.Single(changes);
            Assert.Equal(new SessionRailSettings(false, true), changes[0]);
            var request = Assert.Single(requests);
            Assert.Equal(new SessionRailCompactOptions(9, false), request.Options);
            Assert.Equal("grok-4-6", request.ModelId);
            Assert.Equal("keep the paths", request.Instructions);
            Assert.Equal(new SessionRailCompactOptions(9, false), asked[^1]);
            Assert.Equal(Visibility.Collapsed, panel.CompactView.Visibility);
            Assert.Equal(Visibility.Visible, panel.CardsPanel.Visibility);
            return true;
        });
    }

    [Fact]
    public void The_inheritance_toggle_is_local_and_keeps_availability()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            SessionRailSettings? changed = null;
            panel.SettingsChanged += (_, e) => changed = e.Settings;
            panel.ShowSession(new SessionRailSnapshot(
                null, [], [], new SessionRailSettings(true, true, false, true, false), false));
            Arrange(panel);

            Assert.False(panel.InheritAgentToggle.IsChecked);
            Assert.False(panel.ComputerToggle.IsEnabled);
            Assert.True(panel.BrowserToggle.IsEnabled);

            panel.InheritAgentToggle.IsChecked = true;

            Assert.NotNull(changed);
            Assert.True(changed!.InheritAgentEnabled);

            // Переключатель наследования меняет только своё поле: доступы и их флаги остаются.
            Assert.True(changed.ComputerEnabled);
            Assert.True(changed.BrowserEnabled);
            Assert.False(changed.ComputerAvailable);
            Assert.True(changed.BrowserAvailable);
            return true;
        });
    }

    [Fact]
    public void The_compaction_screen_goes_back_and_offers_undo_only_for_a_compacted_chat()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            var undone = 0;
            panel.CompactionUndoRequested += (_, _) => undone++;
            panel.ShowSession(new SessionRailSnapshot(null, [], [], new SessionRailSettings(false, false), true));
            Arrange(panel);

            Click(panel.CompactButton);
            Assert.Equal(Visibility.Collapsed, panel.CompactUndoButton.Visibility);
            Click(panel.CompactBackButton);
            Assert.Equal(Visibility.Visible, panel.CardsPanel.Visibility);

            panel.ShowSession(new SessionRailSnapshot(null, [], [], new SessionRailSettings(false, false), true, true));
            Click(panel.CompactButton);
            Assert.Equal(Visibility.Visible, panel.CompactUndoButton.Visibility);
            Click(panel.CompactUndoButton);
            Assert.Equal(1, undone);
            Assert.Equal(Visibility.Collapsed, panel.CompactView.Visibility);

            // Чат занялся ответом: экран сжатия закрывается сам.
            panel.ShowSession(new SessionRailSnapshot(null, [], [], new SessionRailSettings(false, false), true));
            Click(panel.CompactButton);
            panel.ShowSession(new SessionRailSnapshot(null, [], [], new SessionRailSettings(false, false), false));
            Assert.Equal(Visibility.Collapsed, panel.CompactView.Visibility);
            return true;
        });
    }

    [Fact]
    public void A_file_row_asks_the_owner_to_show_the_file_and_cards_carry_no_empty_notes()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            var opened = new List<string>();
            panel.FileOpenRequested += (_, e) => opened.Add(e.Path);
            panel.ShowSession(new SessionRailSnapshot(
                null, [], [new SessionRailFile(@"C:\work\report.md", SessionRailFileChange.Created)], new(false, false), false));
            Arrange(panel);

            Click((Button)panel.FilesItems.Children[0]);

            Assert.Equal([@"C:\work\report.md"], opened);

            // Пустые карточки состоят из заголовка и счётчика: пояснений в них нет.
            panel.ShowSession(SessionRailSnapshot.Empty);
            Arrange(panel);
            Assert.Null(panel.FindName("FilesEmpty"));
            Assert.Null(panel.FindName("AgentsEmpty"));
            return true;
        });
    }

    /// <summary>Снимки для просмотра глазами: <c>AMARIN_RAIL_SHOTS=&lt;папка&gt;</c>; обычный прогон ничего не пишет.</summary>
    [Fact]
    public void Write_shots_when_requested()
    {
        var directory = Environment.GetEnvironmentVariable("AMARIN_RAIL_SHOTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _wpf.Ui.Invoke(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeManager.Apply(theme);
                    var panel = new SessionRailPanel { Width = 250 };
                    panel.ShowSession(new SessionRailSnapshot(
                        new SessionRailContext(74_000, 200_000,
                        [
                            new(SessionRailContextCategory.Conversation, 41_000),
                            new(SessionRailContextCategory.Tools, 22_000),
                            new(SessionRailContextCategory.Files, 6_000),
                            new(SessionRailContextCategory.System, 5_000)
                        ]),
                        [
                            new SessionRailAgent("a1", "agent", "anthropic:claude-haiku-5-5", true),
                            new SessionRailAgent("a2", "agent", "zai:glm-5.3-flash", false)
                        ],
                        [
                            new SessionRailFile(@"C:\work\report.md", SessionRailFileChange.Created),
                            new SessionRailFile(@"C:\work\a-very-long-file-name-for-the-narrow-column.xlsx", SessionRailFileChange.Modified)
                        ],
                        new SessionRailSettings(true, false),
                        true,
                        true,
                        new SessionRailCompaction("anthropic:claude-haiku-5-5", null,
                            options => new SessionRailCompactPreview(options.CompactFirst ? 28_000 : 31_000, 34, 44))));
                    Arrange(panel);
                    var background = (Brush)Application.Current.Resources["Bg.Window"];
                    var name = theme.ToString().ToLowerInvariant();
                    Save(panel, Path.Combine(directory, $"rail-{name}.png"), background);

                    Click(panel.CompactButton);
                    Arrange(panel);
                    Save(panel, Path.Combine(directory, $"compact-{name}.png"), background);
                }
            }
            finally
            {
                ThemeManager.Apply(previous);
            }

            return true;
        });
    }

    private static void Save(FrameworkElement element, string path, Brush background)
    {
        const double scale = 2;
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            context.DrawRectangle(background, null, area);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Fact]
    public void Unknown_and_exceeded_context_limits_do_not_break_the_panel()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            panel.ShowSession(new SessionRailSnapshot(new SessionRailContext(100, 0, []), [], [], new(false, false), false));
            Assert.Equal(Visibility.Collapsed, panel.ContextBar.Visibility);
            panel.ShowSession(new SessionRailSnapshot(new SessionRailContext(120, 100, []), [], [], new(false, false), false));
            Assert.Equal(100, panel.ContextUsedColumn.Width.Value);
            Assert.Equal(0, panel.ContextFreeColumn.Width.Value);
            return true;
        });
    }

    [Fact]
    public void Negative_context_numbers_are_rejected()
    {
        var context = new SessionRailContext(-1, 100, []);
        Assert.Throws<ArgumentOutOfRangeException>(() => _wpf.Ui.Invoke(() =>
        {
            new SessionRailPanel().ShowSession(new SessionRailSnapshot(context, [], [], new SessionRailSettings(false, false), false));
            return true;
        }));
    }

    private static void Arrange(SessionRailPanel panel)
    {
        panel.Height = 800;
        panel.Measure(new Size(250, 800));
        panel.Arrange(new Rect(0, 0, 250, 800));
        panel.UpdateLayout();
    }
}
