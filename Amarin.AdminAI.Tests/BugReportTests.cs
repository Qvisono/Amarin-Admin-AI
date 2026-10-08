using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>«Сообщить об ошибке» (H5): что уходит в публичный трекер и как.</summary>
public sealed class BugReportTests
{
    private const string Profile = @"C:\Users\ivan.petrov";

    [Fact]
    public void The_profile_path_the_user_and_the_computer_are_removed()
    {
        const string report = """
            at Amarin.Core.ChatStore.Save() in C:\Users\ivan.petrov\AppData\Roaming\Amarin Admin AI\chats\a.json
            file:///C:/Users/ivan.petrov/Desktop/x.txt
            user IVAN.PETROV on DESKTOP-IVAN42
            """;

        var clean = BugReport.Sanitize(report, Profile, "ivan.petrov", "DESKTOP-IVAN42");

        Assert.Contains(@"%USERPROFILE%\AppData\Roaming", clean, StringComparison.Ordinal);
        Assert.Contains("file:///%USERPROFILE%/Desktop", clean, StringComparison.Ordinal);
        Assert.Contains("user " + BugReport.UserMark + " on " + BugReport.MachineMark, clean, StringComparison.Ordinal);
        Assert.DoesNotContain("ivan", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_inside_a_longer_word_or_a_very_short_name_is_left_alone()
    {
        // «ivan» внутри «ivanov» — чужая фамилия, не наш человек; «me» заменило бы полтекста.
        Assert.Equal("ivanov wrote", BugReport.Sanitize("ivanov wrote", null, "ivan", null));
        Assert.Equal("me and some", BugReport.Sanitize("me and some", null, "me", null));
    }

    [Fact]
    public void Bearer_tokens_are_scrubbed_again()
    {
        var clean = BugReport.Sanitize("Authorization: Bearer abcdefghijklmnop123", null, null, null);

        Assert.Contains("Bearer ***", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop123", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_names_the_exception_and_its_message()
    {
        const string report = """
            === Amarin Admin AI - сбой (UI) 2026-09-30 10:00:00 ===
            Версия: 1.28.0

            Исключение: System.InvalidOperationException
            Сообщение: Collection was modified
            """;

        Assert.Equal("Crash: InvalidOperationException - Collection was modified", BugReport.TitleFrom(report));
        Assert.Equal("Crash", BugReport.TitleFrom("nothing here"));
        Assert.True(BugReport.TitleFrom("Исключение: X\nСообщение: " + new string('a', 500)).Length <= 120);
    }

    [Fact]
    public void The_report_fence_is_longer_than_any_backticks_inside()
    {
        var body = BugReport.Body("1.28.0", "line ```` with ticks");

        Assert.Contains("`````text", body, StringComparison.Ordinal);
        Assert.Contains("- Version: 1.28.0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_report_the_body_has_no_report_section()
    {
        Assert.DoesNotContain("### Report", BugReport.Body("1.28.0", null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_report_goes_in_the_link_itself()
    {
        var link = BugReport.Link("Crash: X", "body & more");

        Assert.Null(link.ClipboardBody);
        Assert.StartsWith(UpdateChecker.RepositoryUrl + "/issues/new?title=Crash%3A%20X&body=", link.Url, StringComparison.Ordinal);
        Assert.Contains("body%20%26%20more", link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_report_goes_to_the_clipboard_and_the_link_asks_to_paste_it()
    {
        var body = new string('я', 3000);

        var link = BugReport.Link("t", body);

        Assert.Equal(body, link.ClipboardBody);
        Assert.True(link.Url.Length <= BugReport.MaxUrlLength);
        Assert.Contains(Uri.EscapeDataString(BugReport.PasteNote), link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_last_crash_of_the_log_is_reported()
    {
        const string log = """
            === Amarin Admin AI - сбой (UI) 2026-09-01 ===
            первый
            === Amarin Admin AI - сбой (UI) 2026-09-30 ===
            второй
            """;

        var last = BugReport.LastCrash(log)!;

        Assert.StartsWith("=== Amarin Admin AI - сбой (UI) 2026-09-30", last, StringComparison.Ordinal);
        Assert.DoesNotContain("первый", last, StringComparison.Ordinal);
        Assert.Null(BugReport.LastCrash("  "));
    }
}
