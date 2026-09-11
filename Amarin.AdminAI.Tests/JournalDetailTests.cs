using System.Net;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The details screen and the explanation behind it. The explanation deliberately does not go
/// through <see cref="ChatEngine"/>: a model asked "what did this call do" while holding a registry
/// editor is liable to answer by going and looking.
/// </summary>
public sealed class JournalDetailTests
{
    private static JournalEntry Entry(string tool = "registry", string result = "готово") => new(
        "chat-1", "Разговор", new DateTime(2026, 3, 1, 12, 0, 0), TimeSpan.FromMilliseconds(340),
        tool, """{"action":"write","path":"HKLM\\SOFTWARE\\X"}""", "готово", result, true,
        ToolCallStatus.Done, null);

    [Fact]
    public void The_whole_output_is_kept_but_capped()
    {
        var huge = new string('x', 10_000);

        var kept = ChatToolPreview.ForJournal(ToolResult.Ok(huge));

        Assert.True(kept.Length < huge.Length);
        Assert.StartsWith(new string('x', 4_000), kept, StringComparison.Ordinal);
        Assert.Contains("обрезано для журнала", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_that_fits_is_kept_untouched()
    {
        Assert.Equal("готово\nвторая строка", ChatToolPreview.ForJournal(ToolResult.Ok("готово\nвторая строка")));
    }

    [Fact]
    public void A_call_recorded_before_the_field_existed_falls_back_to_its_summary()
    {
        var session = new ChatSession { Id = "c", Title = "Старый" };
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            CreatedAt = new DateTime(2026, 2, 1, 9, 0, 0),
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "c1",
                            Name = "registry",
                            ResultPreview = "готово",
                            ResultText = "",
                            Status = ToolCallStatus.Done,
                            Success = true
                        }
                    ]
                }
            ]
        });

        Assert.Equal("готово", Assert.Single(ActionJournal.FromSession(session)).ResultText);
    }

    [Fact]
    public async Task The_question_carries_no_system_prompt_and_no_tools()
    {
        string? body = null;
        var handler = new CapturingHandler(text => body = text);
        var options = new AgentOptions { ApiKey = "k", BaseUrl = "https://api.venice.ai/api/v1" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };

        var answer = await JournalExplainer.ExplainAsync(
            new VeniceClient(http, options), "fast-model", Entry());

        Assert.Equal("Это запись в реестр.", answer);
        Assert.NotNull(body);

        // The two things that make it a clean question rather than another agent turn.
        Assert.DoesNotContain("\"tools\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"system\"", body, StringComparison.Ordinal);
        Assert.Contains("fast-model", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_question_quotes_the_call_it_is_about()
    {
        var prompt = JournalExplainer.BuildPrompt(Entry());

        Assert.Contains("registry", prompt, StringComparison.Ordinal);
        Assert.Contains("HKLM", prompt, StringComparison.Ordinal);
        Assert.Contains("готово", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_result_is_quoted_only_as_far_as_it_is_useful()
    {
        var prompt = JournalExplainer.BuildPrompt(Entry(result: new string('y', 9_000)));

        Assert.True(prompt.Length < 6_000);
        Assert.Contains("…", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_is_an_answer_a_person_can_read()
    {
        var handler = new FailingHandler();
        var options = new AgentOptions { ApiKey = "k", BaseUrl = "https://api.venice.ai/api/v1" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };

        var answer = await JournalExplainer.ExplainAsync(
            new VeniceClient(http, options), "fast-model", Entry());

        // Not an exception message, and not an empty box.
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotContain("Exception", answer, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            capture(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"Это запись в реестр."}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }
}
