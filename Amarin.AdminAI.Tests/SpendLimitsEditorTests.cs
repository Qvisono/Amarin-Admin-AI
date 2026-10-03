using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Правка лимитов и порогов и строки о них — без окна. До 1.30.0 это был код блока настроек и
/// окна, и проверить его можно было только оконным тестом.
/// </summary>
public sealed class SpendLimitsEditorTests
{
    private AppSettings _settings = new();
    private int _saves;

    private SpendLimitsEditor Editor() => new(() => _settings, _ => _saves++);

    [Fact]
    public void A_change_replaces_the_limits_object_instead_of_editing_it_in_place()
    {
        // Объект лимитов читает SpendGuard из потока хода: словарь, меняющийся под чтением, мог бы бросить.
        var editor = Editor();
        var changed = 0;
        editor.Changed += () => changed++;
        var before = _settings.SpendLimits;
        var keysBefore = before.Keys;

        editor.SetProfileDay(5m);
        editor.SetKeyMonth("fp", 3m);

        Assert.NotSame(before, _settings.SpendLimits);
        Assert.Empty(keysBefore);
        Assert.Null(before.DayUsd);
        Assert.Equal(5m, _settings.SpendLimits.DayUsd);
        Assert.Equal(3m, _settings.SpendLimits.Keys["fp"].MonthUsd);
        Assert.Equal(2, _saves);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void A_key_limit_without_either_sum_leaves_no_entry()
    {
        var editor = Editor();

        editor.SetKeyDay("fp", 2m);
        editor.SetKeyMonth("fp", 7m);
        editor.SetKeyDay("fp", null);
        Assert.Equal(7m, _settings.SpendLimits.Keys["fp"].MonthUsd);

        editor.SetKeyMonth("fp", null);
        Assert.Empty(_settings.SpendLimits.Keys);
    }

    [Fact]
    public void Editing_follows_the_settings_object_the_services_hold_now()
    {
        // Импорт данных и смена профиля подставляют службам новый объект настроек.
        var editor = Editor();
        var old = _settings;
        _settings = new AppSettings();

        editor.SetTurn(1.5m);
        editor.SetBalanceCritical(0.1m);

        Assert.Equal(1.5m, _settings.SpendLimits.TurnUsd);
        Assert.Equal(0.1m, _settings.BalanceThresholds.CriticalUsd);
        Assert.Null(old.SpendLimits.TurnUsd);
    }

    [Fact]
    public void Settings_with_null_limits_read_as_all_off()
    {
        // Файл с "SpendLimits": null разбирается в null, хотя свойство и не помечено nullable.
        _settings.SpendLimits = null!;
        _settings.BalanceThresholds = null!;
        var editor = Editor();

        Assert.Null(editor.Limits.DayUsd);
        Assert.Equal(SpendLimitsEditor.DefaultWarnPercent, editor.Limits.WarnPercent);

        editor.SetProfileMonth(9m);
        editor.SetKeyBalanceLow("fp", 2m);
        Assert.Equal(9m, _settings.SpendLimits.MonthUsd);
        Assert.Equal(2m, _settings.BalanceThresholds.Keys["fp"]);
    }

    [Theory]
    [InlineData("five dollars", "5", "5.00", "5", false)]
    [InlineData("$7,5", null, "7.50", "7.5", true)]
    [InlineData("0", "5", "", null, true)]
    [InlineData("-3", "2", "", null, true)]
    [InlineData("   ", null, "", null, false)]
    [InlineData("5.001", "5", "5.00", "5", false)]
    public void A_money_field_keeps_the_old_value_on_junk_and_zero_means_no_limit(
        string text, string? current, string shown, string? value, bool changed)
    {
        var edit = SpendLimitsEditor.AcceptMoney(text, Money(current));

        Assert.Equal(shown, edit.Text);
        Assert.Equal(Money(value), edit.Value);
        Assert.Equal(changed, edit.Changed);
    }

    [Fact]
    public void The_warning_share_takes_only_1_to_99()
    {
        var editor = Editor();

        Assert.Equal(80, editor.SetWarnPercent("150"));
        Assert.Equal(80, editor.SetWarnPercent("half"));
        Assert.Equal(0, _saves);

        Assert.Equal(50, editor.SetWarnPercent(" 50 "));
        Assert.Equal(50, _settings.SpendLimits.WarnPercent);
        Assert.Equal(1, _saves);

        // То же значение — без записи.
        Assert.Equal(50, editor.SetWarnPercent("50"));
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void A_key_balance_threshold_set_to_nothing_is_removed()
    {
        var editor = Editor();
        editor.SetKeyBalanceLow("fp", 3m);
        var withKey = _settings.BalanceThresholds;

        editor.SetKeyBalanceLow("fp", null);

        Assert.Empty(_settings.BalanceThresholds.Keys);
        Assert.Equal(3m, withKey.Keys["fp"]);
    }

    [Fact]
    public void Key_rows_are_one_per_secret_and_skip_keys_that_cannot_be_read()
    {
        ApiKeyEntry[] keys =
        [
            new("env", "Из окружения", "secret-one", ApiKeySource.Environment, true),
            new("copy", "Копия", "secret-one", ApiKeySource.Stored, false),
            new("broken", "Чужой", null, ApiKeySource.Stored, false),
            new("router", "Роутер", "secret-two", ApiKeySource.Stored, false, LlmProvider.OpenRouter)
        ];

        var rows = SpendLimitsEditor.KeyRows(keys);

        Assert.Equal(["Из окружения", "Роутер"], rows.Select(row => row.Label));
        Assert.Equal(ApiKeyStore.Fingerprint("secret-one"), rows[0].Fingerprint);
        Assert.Equal(LlmProvider.OpenRouter, rows[1].Provider);
    }

    [Fact]
    public void The_turn_question_names_what_the_turn_spent_and_the_period_question_the_period()
    {
        var turn = SpendPrompts.Question(new SpendQuestion(new SpendBreach(SpendLimitKind.Turn, 0.5m, 0.6m), 0.4m));
        Assert.Equal(Loc.Get("S.Limit.TurnTitle"), turn.Title);
        Assert.Contains("$0.40", turn.Text, StringComparison.Ordinal);
        Assert.Contains("$0.50", turn.Text, StringComparison.Ordinal);

        var period = SpendPrompts.Question(new SpendQuestion(new SpendBreach(SpendLimitKind.ProfileDay, 1m, 1.2m), 0.4m));
        Assert.Equal(Loc.Get("S.Limit.PeriodTitle"), period.Title);
        Assert.Contains("$1.00", period.Text, StringComparison.Ordinal);
        Assert.Contains("$1.20", period.Text, StringComparison.Ordinal);
        Assert.Contains(SpendRules.KindName(SpendLimitKind.ProfileDay), period.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.40", period.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_balance_alert_names_the_key_or_how_bad_the_total_is()
    {
        var key = SpendPrompts.BalanceAlert(new BalanceAlert("Рабочий", 0.8m, 1m, BalanceLevel.Low));
        Assert.Contains("Рабочий", key, StringComparison.Ordinal);

        Assert.Equal(
            Loc.Format("S.Balance.AlertCritical", "$0.10", "$0.25"),
            SpendPrompts.BalanceAlert(new BalanceAlert(null, 0.1m, 0.25m, BalanceLevel.Critical)));
        Assert.Equal(
            Loc.Format("S.Balance.AlertLow", "$0.90", "$1.00"),
            SpendPrompts.BalanceAlert(new BalanceAlert(null, 0.9m, 1m, BalanceLevel.Low)));
    }

    [Fact]
    public void The_summary_shows_the_most_visible_limit()
    {
        Assert.Equal(Loc.Get("S.Common.Off"), SpendPrompts.LimitsSummary(null));
        Assert.Equal(Loc.Get("S.Common.Off"), SpendPrompts.LimitsSummary(new SpendLimits()));
        Assert.Equal(
            Loc.Format("S.Limit.Short.Day", "$5.00"),
            SpendPrompts.LimitsSummary(new SpendLimits { DayUsd = 5m, MonthUsd = 50m, TurnUsd = 1m }));
        Assert.Equal(
            Loc.Format("S.Limit.Short.Month", "$50.00"),
            SpendPrompts.LimitsSummary(new SpendLimits { MonthUsd = 50m, TurnUsd = 1m }));
        Assert.Equal(
            Loc.Get("S.Limit.Short.Keys"),
            SpendPrompts.LimitsSummary(new SpendLimits { Keys = { ["fp"] = new KeySpendLimit { DayUsd = 1m } } }));
    }

    private static decimal? Money(string? text) =>
        text is null ? null : decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
}
