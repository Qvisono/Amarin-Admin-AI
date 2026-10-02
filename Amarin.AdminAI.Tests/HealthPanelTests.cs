using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Состояние ПК» (C4): пороги, разбор ответов PowerShell 5.1 и сбор, который переживает сбой пробы.
/// </summary>
public sealed class HealthRulesTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void A_nearly_full_drive_is_a_problem_and_a_filling_one_needs_attention()
    {
        var card = HealthRules.Disks(
        [
            new DriveHealth("C:", 500 * Gb, 20 * Gb),
            new DriveHealth("D:", 1000 * Gb, 500 * Gb)
        ], []);
        var tight = HealthRules.Disks([new DriveHealth("C:", 500 * Gb, 80 * Gb)], []);

        Assert.Equal(HealthStatus.Problem, card.Status);
        Assert.Equal(HealthStatus.Attention, tight.Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.Disks([new DriveHealth("C:", 500 * Gb, 300 * Gb)], []).Status);
    }

    [Fact]
    public void A_small_drive_is_judged_by_bytes_too()
    {
        // 30 % свободно, но это 3 ГБ: на системном диске этого мало, проценты тут врут.
        Assert.Equal(HealthStatus.Problem, HealthRules.Disks([new DriveHealth("C:", 10 * Gb, 3 * Gb)], []).Status);
    }

    [Fact]
    public void An_unhealthy_physical_disk_is_a_problem_whatever_the_free_space()
    {
        var card = HealthRules.Disks([new DriveHealth("C:", 500 * Gb, 400 * Gb)],
            [new PhysicalDiskHealth("Samsung SSD", "Warning", "Predictive Failure")]);

        Assert.Equal(HealthStatus.Problem, card.Status);
        Assert.Contains(card.Facts, fact => fact.Contains("Samsung SSD", StringComparison.Ordinal));
    }

    [Fact]
    public void Unread_drive_health_is_said_out_loud_not_assumed_fine()
    {
        var card = HealthRules.Disks([new DriveHealth("C:", 500 * Gb, 400 * Gb)], physical: null);

        Assert.Contains(Loc.Get("S.Health.SmartUnknown"), card.Facts);
    }

    [Fact]
    public void Long_uptime_and_full_memory_need_attention()
    {
        Assert.Equal(HealthStatus.Attention, HealthRules.System(new SystemHealth(10, 50, TimeSpan.FromDays(20))).Status);
        Assert.Equal(HealthStatus.Attention, HealthRules.System(new SystemHealth(10, 95, TimeSpan.FromDays(1))).Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.System(new SystemHealth(10, 50, TimeSpan.FromDays(1))).Status);
    }

    [Fact]
    public void Protection_off_is_a_problem_and_another_antivirus_is_not()
    {
        var off = HealthRules.Security(new SecurityHealth(new DefenderHealth(true, true, false, 1), [new FirewallHealth("Domain", true)]));
        var firewallOff = HealthRules.Security(new SecurityHealth(new DefenderHealth(true, true, true, 1), [new FirewallHealth("Public", false)]));
        var other = HealthRules.Security(new SecurityHealth(new DefenderHealth(false, null, null, null), [new FirewallHealth("Public", true)]));

        Assert.Equal(HealthStatus.Problem, off.Status);
        Assert.Equal(HealthStatus.Problem, firewallOff.Status);
        Assert.Equal(HealthStatus.Ok, other.Status);
        Assert.Equal(HealthStatus.Unknown, HealthRules.Security(null).Status);
    }

    [Fact]
    public void Old_signatures_need_attention() =>
        Assert.Equal(HealthStatus.Attention,
            HealthRules.Security(new SecurityHealth(new DefenderHealth(true, true, true, 30), [])).Status);

    [Fact]
    public void Critical_events_are_a_problem_and_crashes_need_attention()
    {
        Assert.Equal(HealthStatus.Problem, HealthRules.Stability(new EventHealth(1, 0, 0)).Status);
        Assert.Equal(HealthStatus.Attention, HealthRules.Stability(new EventHealth(0, 3, 2)).Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.Stability(new EventHealth(0, 3, 0)).Status);
        Assert.Equal(HealthStatus.Unknown, HealthRules.Stability(null).Status);
    }

    [Fact]
    public void A_pending_restart_needs_attention_even_when_the_update_list_is_unknown()
    {
        Assert.Equal(HealthStatus.Attention, HealthRules.Updates(new UpdateHealth(true, null)).Status);
        Assert.Equal(HealthStatus.Unknown, HealthRules.Updates(new UpdateHealth(false, null)).Status);
        Assert.Equal(HealthStatus.Attention, HealthRules.Updates(new UpdateHealth(false, 3)).Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.Updates(new UpdateHealth(false, 0)).Status);
    }

    [Fact]
    public void The_chat_gets_the_card_as_context()
    {
        var context = HealthRules.Context(new HealthCard
        {
            Area = HealthArea.Disks,
            Status = HealthStatus.Problem,
            Facts = ["C: свободно 2 из 500 ГБ (0%)"]
        });

        Assert.Contains(Loc.Get("S.Health.Area.Disks"), context, StringComparison.Ordinal);
        Assert.Contains("- C: свободно 2 из 500 ГБ (0%)", context, StringComparison.Ordinal);
    }

    /// <summary>
    /// «Разобраться» говорит, что и где проверено, а не «панель, раздел такой-то»: модель панели
    /// не видит и гадала, откуда числа (так было в 1.28.0 до правки).
    /// </summary>
    [Fact]
    public void The_request_names_the_logs_and_the_numbers_and_never_the_panel()
    {
        var context = HealthRules.Context(HealthRules.Stability(new Tools.EventHealth(0, 260, 2)));

        Assert.DoesNotContain("панел", context, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("раздел", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("260", context, StringComparison.Ordinal);
        Assert.Contains("24", context, StringComparison.Ordinal);
        Assert.Contains("«Система»", context, StringComparison.Ordinal);
        Assert.Contains(Loc.Get("S.Health.Ask.StabilityTask"), context, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drive_line_has_a_single_colon_after_the_letter()
    {
        var card = HealthRules.Disks([new Tools.DriveHealth("C:", 500L << 30, 120L << 30)], []);

        Assert.StartsWith("C: ", card.Facts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("C::", card.Facts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_waits_for_every_area_and_then_takes_the_worst()
    {
        var ok = new HealthCard { Status = HealthStatus.Ok };
        var attention = new HealthCard { Area = HealthArea.Stability, Status = HealthStatus.Attention };
        Dictionary<HealthArea, HealthCard?> Cards(HealthCard? stability) =>
            Enum.GetValues<HealthArea>().ToDictionary(area => area, area => area == HealthArea.Stability ? stability : ok);

        Assert.Equal("Loading", UI.HealthPanel.Summarize(Cards(null)).Tone);
        Assert.Equal("Ok", UI.HealthPanel.Summarize(Cards(ok)).Tone);
        Assert.Equal("Attention", UI.HealthPanel.Summarize(Cards(attention)).Tone);
    }

    /// <summary>
    /// Итог называет разделы, где есть на что смотреть, — худшие первыми; прежде было общее «Есть
    /// на что взглянуть», и раздел приходилось искать глазами.
    /// </summary>
    [Fact]
    public void The_summary_names_the_areas_that_need_a_look()
    {
        var cards = Enum.GetValues<HealthArea>().ToDictionary(area => area, area => (HealthCard?)new HealthCard
        {
            Area = area,
            Status = area switch
            {
                HealthArea.Updates => HealthStatus.Attention,
                HealthArea.Security => HealthStatus.Problem,
                _ => HealthStatus.Ok
            }
        });

        var summary = UI.HealthPanel.Summarize(cards);

        Assert.Equal("Problem", summary.Tone);
        var security = Loc.Get("S.Health.Area.Security").ToLowerInvariant();
        var updates = Loc.Get("S.Health.Area.Updates").ToLowerInvariant();
        Assert.Equal(Loc.Format("S.Health.Summary.Issues", security + ", " + updates), summary.Title);
    }

    /// <summary>
    /// Панель показывает подпись и значение, а цвет — только у значения, которое требует внимания:
    /// 260 ошибок выделены, ноль критических — обычным текстом.
    /// </summary>
    [Fact]
    public void Only_the_value_that_needs_a_look_is_coloured()
    {
        var card = HealthRules.Stability(new EventHealth(0, 260, 0));

        var errors = card.Items.Single(item => item.Label == Loc.Get("S.Health.Item.Errors"));
        var critical = card.Items.Single(item => item.Label == Loc.Get("S.Health.Item.Critical"));
        Assert.Equal(("260", HealthStatus.Attention), (errors.Value, errors.Status));
        Assert.Equal(("0", HealthStatus.Ok), (critical.Value, critical.Status));
    }

    /// <summary>Диск — буква, полоска занятого и «свободно из», как в Проводнике.</summary>
    [Fact]
    public void A_drive_row_has_a_bar_and_explorer_style_sizes()
    {
        var card = HealthRules.Disks([new DriveHealth("D:", 2L << 40, 1L << 39)], []);

        var drive = card.Items[0];
        Assert.Equal("D:", drive.Label);
        Assert.Equal(0.75, drive.Used!.Value, 3);
        Assert.Equal(Loc.Format("S.Health.Item.DriveFree", Loc.Format("S.Health.Item.Gb", 512), Loc.Format("S.Health.Item.Tb", 2, 0)), drive.Value);
        Assert.Equal(Loc.Format("S.Health.Item.Gb", 465), HealthRules.Size(465L << 30));
        Assert.Equal(Loc.Format("S.Health.Item.GbTenths", 4, 2), HealthRules.Size((long)(4.2 * (1L << 30))));
    }

    /// <summary>Свежая проверка — одно время: с датой строка итога в русском не помещалась в ширину.</summary>
    [Fact]
    public void Today_shows_only_the_time_and_earlier_the_date_too()
    {
        var now = new DateTime(2026, 10, 2, 15, 0, 0);

        Assert.Equal(Loc.Format("S.Health.UpdatedAt", "01:12"), HealthPanel.Updated(new DateTime(2026, 10, 2, 1, 12, 0), now, DateFormat.DayMonthShort));
        Assert.Equal(
            Loc.Format("S.Health.Updated", ChatFormat.DateTimeShort(new DateTime(2026, 10, 1, 1, 12, 0), DateFormat.DayMonthShort)),
            HealthPanel.Updated(new DateTime(2026, 10, 1, 1, 12, 0), now, DateFormat.DayMonthShort));
    }

    /// <summary>Снимок прежней версии без строк показывает свои факты, а не пустой раздел.</summary>
    [Fact]
    public void An_old_snapshot_without_rows_shows_its_facts()
    {
        var row = new HealthCardRow(HealthArea.Updates, new HealthCard { Area = HealthArea.Updates, Status = HealthStatus.Attention, Facts = ["x", "y"] });

        Assert.Equal(["x", "y"], row.Items.Select(item => item.Label));
        Assert.All(row.Items, item => Assert.Equal(3, item.LabelSpan));
    }

    // ───────────────────────── разбор ответов PowerShell ─────────────────────────

    [Fact]
    public void A_single_disk_comes_back_as_an_object_and_is_still_a_list()
    {
        // ConvertTo-Json в 5.1 разворачивает массив из одного элемента в объект.
        var one = HealthProbes.ParsePhysicalDisks("""{"FriendlyName":"SSD","HealthStatus":"Healthy","OperationalStatus":"OK"}""");
        var two = HealthProbes.ParsePhysicalDisks("""[{"FriendlyName":"A","HealthStatus":"Healthy"},{"FriendlyName":"B","HealthStatus":"Unhealthy"}]""");

        Assert.True(Assert.Single(one!).Healthy);
        Assert.Equal(2, two!.Count);
        Assert.False(two[1].Healthy);
        Assert.Null(HealthProbes.ParsePhysicalDisks("not json"));
    }

    [Fact]
    public void Security_without_defender_reads_as_absent_not_as_off()
    {
        var parsed = HealthProbes.ParseSecurity("""{"Defender":null,"Firewall":{"Name":"Public","Enabled":true}}""")!;

        Assert.False(parsed.Defender.Available);
        Assert.Equal("Public", Assert.Single(parsed.Firewall).Profile);
    }

    [Fact]
    public void Security_with_defender_is_read_field_by_field()
    {
        var parsed = HealthProbes.ParseSecurity(
            """{"Defender":{"AntivirusEnabled":true,"RealTimeProtectionEnabled":false,"AntivirusSignatureAge":3},"Firewall":[]}""")!;

        Assert.Equal(new DefenderHealth(true, true, false, 3), parsed.Defender);
    }

    [Fact]
    public void Event_counts_are_read_and_garbage_is_unknown()
    {
        Assert.Equal(new EventHealth(1, 7, 2), HealthProbes.ParseEvents("""{"Critical":1,"Errors":7,"AppCrashes":2}"""));
        Assert.Null(HealthProbes.ParseEvents(""));
    }

    // ───────────────────────── сбор ─────────────────────────

    [Fact]
    public async Task A_failing_probe_leaves_its_card_unchecked_and_the_rest_are_filled()
    {
        var ready = new List<HealthArea>();
        Func<CancellationToken, Task<HealthCard>>[] probes =
        [
            _ => Task.FromResult(new HealthCard { Area = HealthArea.Disks, Status = HealthStatus.Ok }),
            _ => throw new InvalidOperationException("no perf counters"),
            _ => Task.FromResult(new HealthCard { Area = HealthArea.Security, Status = HealthStatus.Problem }),
            _ => Task.FromResult(new HealthCard { Area = HealthArea.Stability, Status = HealthStatus.Ok }),
            _ => Task.FromResult(new HealthCard { Area = HealthArea.Updates, Status = HealthStatus.Attention })
        ];

        var report = await HealthCollector.CollectAsync(card =>
        {
            lock (ready)
            {
                ready.Add(card.Area);
            }
        }, CancellationToken.None, probes);

        Assert.Equal(Enum.GetValues<HealthArea>(), report.Cards.Select(card => card.Area));
        Assert.Equal(HealthStatus.Unknown, report.Cards.Single(card => card.Area == HealthArea.System).Status);
        Assert.Equal(HealthStatus.Problem, report.Overall);
        Assert.Equal(5, ready.Count);
    }

    [Fact]
    public void The_last_report_survives_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            new HealthCache(root).Store(new HealthReport
            {
                At = new DateTime(2026, 9, 30, 10, 0, 0),
                Cards = [new HealthCard { Area = HealthArea.Updates, Status = HealthStatus.Attention, Facts = ["x"] }]
            });

            var last = new HealthCache(root).Last!;

            Assert.Equal(HealthStatus.Attention, last.Overall);
            Assert.Equal(["x"], Assert.Single(last.Cards).Facts);
            Assert.Contains(HealthCache.FileName, ProfileDataWiper.DefaultProfileFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cards_not_ready_yet_say_so_and_offer_nothing_to_ask()
    {
        var rows = HealthPanel.Rows(new Dictionary<HealthArea, HealthCard?>
        {
            [HealthArea.Disks] = new() { Area = HealthArea.Disks, Status = HealthStatus.Problem }
        });

        Assert.Equal(5, rows.Count);
        Assert.Equal(Visibility.Visible, rows[0].AskVisibility);
        Assert.Equal("Loading", rows[1].Tone);
        Assert.Equal(Loc.Get("S.Health.Checking"), Assert.Single(rows[1].Items).Label);
        Assert.Equal(Visibility.Collapsed, rows[1].AskVisibility);
    }
}

/// <summary>Панель на живом WPF: карточки на месте и не вылезают за рамку.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class HealthPanelUiTests
{
    private readonly WpfFixture _wpf;

    public HealthPanelUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public async Task Cards_fill_in_from_the_probes_and_stay_inside_the_frame()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-health-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (count, inside) = await _wpf.Ui.Invoke(async () =>
            {
                var panel = new HealthPanel { Width = 900, Height = 700 };
                panel.Probes = Enum.GetValues<HealthArea>()
                    .Select(area => (Func<CancellationToken, Task<HealthCard>>)(_ => Task.FromResult(new HealthCard
                    {
                        Area = area,
                        Status = HealthStatus.Attention,
                        Facts = ["строка один", "строка два, подлиннее, чтобы перенестись на вторую строку карточки"]
                    })))
                    .ToList();
                panel.Attach(new HealthCache(root), () => DateFormat.DayMonthShort);
                panel.Open();

                for (var i = 0; i < 100 && new HealthCache(root).Last is null; i++)
                {
                    await Task.Delay(20);
                }

                panel.Measure(new Size(900, 700));
                panel.Arrange(new Rect(0, 0, 900, 700));
                panel.UpdateLayout();

                var cards = (ItemsControl)panel.FindName("Cards");
                var card = (FrameworkElement)panel.FindName("Card");
                var scroll = (FrameworkElement)panel.FindName("CardsScroll");
                var bottom = scroll.TransformToAncestor(card).Transform(new Point(0, scroll.ActualHeight)).Y;
                panel.Cancel();
                return (cards.Items.Count, bottom <= card.ActualHeight + 0.5);
            });

            Assert.Equal(5, count);
            Assert.True(inside, "прокручиваемая часть выходит за карточку");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// «Проверить снова» не стирает прежние значения до прихода новых: сброс всех строк в
    /// «Проверяю…» схлопывал панель, и она дёргалась, вырастая обратно.
    /// </summary>
    [Fact]
    public async Task A_refresh_keeps_the_old_values_until_new_ones_arrive()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-health-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (labels, summary) = await _wpf.Ui.Invoke(async () =>
            {
                var cache = new HealthCache(root);
                cache.Store(new HealthReport
                {
                    At = DateTime.Now.AddHours(-1),
                    Cards = Enum.GetValues<HealthArea>()
                        .Select(area => new HealthCard { Area = area, Status = HealthStatus.Ok, Items = [new HealthItem { Label = "old " + area, Value = "1" }] })
                        .ToList()
                });

                var never = new TaskCompletionSource<HealthCard>();
                var panel = new HealthPanel
                {
                    Probes = Enum.GetValues<HealthArea>().Select(_ => (Func<CancellationToken, Task<HealthCard>>)(_ => never.Task)).ToList()
                };
                panel.Attach(cache, () => DateFormat.DayMonthShort);
                panel.Open();
                await Task.Delay(50);

                var rows = (IEnumerable<HealthCardRow>)((ItemsControl)panel.FindName("Cards")).ItemsSource;
                var shown = rows.SelectMany(row => row.Items).Select(item => item.Label).ToList();
                var tone = ((HealthSummary)((FrameworkElement)panel.FindName("Summary")).DataContext).Tone;
                panel.Cancel();
                return (shown, tone);
            });

            Assert.All(Enum.GetValues<HealthArea>(), area => Assert.Contains("old " + area, labels));
            Assert.Equal("Loading", summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
