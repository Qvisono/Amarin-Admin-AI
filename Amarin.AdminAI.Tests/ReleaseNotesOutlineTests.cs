using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Что нового» (1.37.0): заметки разбираются на вступление и разделы «##» — из них окно строит
/// колонку разделов, — и переводятся моделью кусками, не теряя разделов.
/// </summary>
public sealed class ReleaseNotesOutlineTests
{
    [Fact]
    public void The_version_heading_is_dropped_and_the_intro_becomes_the_lead()
    {
        var outline = ReleaseNotesOutline.Parse("# 1.37.0: big\n\nIntro line.\n\n## Chat\n\n- One.\n\n## Voice\n\n- Two.\n");

        Assert.Equal("Intro line.", outline.Lead);
        Assert.Equal(["Chat", "Voice"], outline.Sections.Select(section => section.Title));
        Assert.Equal("- One.", outline.Sections[0].Body);
        Assert.Equal("- Two.", outline.Sections[1].Body);
    }

    [Fact]
    public void Subheadings_and_headings_inside_code_stay_in_their_section()
    {
        const string notes = "# 1.0.0\n\n## Tools\n\n### Sound\n\n- A.\n\n```\n## not a section\n```\n\n## Rosa\n\n- B.";

        var outline = ReleaseNotesOutline.Parse(notes);

        Assert.Equal(["Tools", "Rosa"], outline.Sections.Select(section => section.Title));
        Assert.Contains("### Sound", outline.Sections[0].Body, StringComparison.Ordinal);
        Assert.Contains("## not a section", outline.Sections[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_without_sections_go_whole_into_the_lead()
    {
        var outline = ReleaseNotesOutline.Parse("# 1.37.0-beta.1\r\n\r\nNew beta.\r\n\r\n- One.\r\n");

        Assert.Empty(outline.Sections);
        Assert.Equal("New beta.\n\n- One.", outline.Lead);
    }

    [Fact]
    public void The_notes_of_this_release_have_a_lead_and_sections()
    {
        // Те самые заметки, что окно покажет после обновления на 1.37.0.
        var outline = ReleaseNotesOutline.Parse(File.ReadAllText(SourceTree.RepositoryFile(".github/release-notes/v1.37.0.md")));

        Assert.NotEmpty(outline.Lead);
        Assert.InRange(outline.Sections.Count, 8, 14);
        Assert.All(outline.Sections, section => Assert.NotEmpty(section.Body));
    }

    [Fact]
    public void Pieces_go_to_the_model_and_come_back_into_the_same_sections()
    {
        var outline = ReleaseNotesOutline.Parse("# 1.0.0\n\nIntro.\n\n## Chat\n\n- One.\n\n## Voice\n\n- Two.");
        var strings = ReleaseNotesTranslator.ToStrings(outline);

        Assert.Equal(["lead", "s0.title", "s0.body", "s1.title", "s1.body"], strings.Keys);

        // Перевод вернул не всё: недостающее остаётся как было, разделов столько же.
        var back = ReleaseNotesTranslator.FromStrings(outline, new Dictionary<string, string>
        {
            ["s0.title"] = "Чат",
            ["s1.body"] = "- Два."
        });

        Assert.Equal("Intro.", back.Lead);
        Assert.Equal(["Чат", "Voice"], back.Sections.Select(section => section.Title));
        Assert.Equal("- One.", back.Sections[0].Body);
        Assert.Equal("- Два.", back.Sections[1].Body);
    }

    [Fact]
    public void Short_notes_go_in_one_request()
    {
        var notes = "# 1.0.0\n\nIntro.\n\n## Chat\n\n- One.\n\n## Voice\n\n- Two.";

        var parts = ReleaseNotesTranslator.Parts(ReleaseNotesTranslator.ToStrings(ReleaseNotesOutline.Parse(notes)));

        Assert.Single(parts);
        Assert.Equal(5, parts[0].Count);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(13, 4)]
    [InlineData(40, 4)]
    public void Long_notes_are_shared_by_up_to_four_requests_in_even_parts(int sections, int expected)
    {
        // Раздел по ~700 знаков: по части на 1500, но не больше четырёх.
        var body = string.Concat(Enumerable.Range(0, 15).Select(k => $"- Point {k} of a section, long enough to count.\n"));
        var notes = "# 1.0.0\n\nIntro.\n" + string.Concat(Enumerable.Range(0, sections).Select(i => $"\n## S{i}\n\n{body}"));
        var strings = ReleaseNotesTranslator.ToStrings(ReleaseNotesOutline.Parse(notes));

        var parts = ReleaseNotesTranslator.Parts(strings);

        Assert.Equal(expected, parts.Count);
        Assert.InRange(parts.Count, 1, ReleaseNotesTranslator.MaxParts);

        // Ничего не потеряно, и порядок заметок сохранён.
        Assert.Equal(strings.Keys, parts.SelectMany(part => part.Keys));

        // Раздел не делится: заголовок и тело — в одной части.
        foreach (var part in parts)
        {
            foreach (var key in part.Keys.Where(key => key.EndsWith(".title", StringComparison.Ordinal)))
            {
                Assert.Contains(key.Replace(".title", ".body", StringComparison.Ordinal), part.Keys);
            }
        }

        // Части ровные: самая длинная больше самой короткой не больше чем на два раздела.
        var sizes = parts.Select(part => part.Values.Sum(value => value.Length)).ToList();
        Assert.True(sizes.Max() - sizes.Min() <= 2 * (body.Length + 10), string.Join(", ", sizes));
    }

    [Fact]
    public async Task Every_part_is_its_own_request_and_all_come_back_together()
    {
        var body = string.Concat(Enumerable.Range(0, 15).Select(k => $"- Point {k} of a section, long enough to count.\n"));
        var outline = ReleaseNotesOutline.Parse(
            "# 1.0.0\n" + string.Concat(Enumerable.Range(0, 8).Select(i => $"\n## S{i}\n\n{body}")));
        var requests = 0;
        using var http = Client(asked =>
        {
            Interlocked.Increment(ref requests);

            // Ответ — заголовки той части, что пришла: ключи s<N>.title из самого запроса.
            var titles = System.Text.RegularExpressions.Regex.Matches(asked, @"s(\d+)\.title")
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .ToDictionary(index => $"s{index}.title", index => $"Раздел {index}");
            return Reply(JsonSerializer.Serialize(titles));
        });

        var result = await ReleaseNotesTranslator.TranslateAsync(http.Client, "model", null, outline, "Русский", CancellationToken.None);

        Assert.Equal(ReleaseNotesTranslator.MaxParts, requests);
        Assert.NotNull(result);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"Раздел {i}"), result.Sections.Select(section => section.Title));
    }

    [Fact]
    public async Task A_model_answer_replaces_the_pieces_it_translated()
    {
        var outline = ReleaseNotesOutline.Parse("# 1.0.0\n\n## Chat\n\n- One.");
        var answer = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["s0.title"] = "Чат",
            ["s0.body"] = "- Один — и всё."
        });
        string? asked = null;
        using var http = Client(body =>
        {
            asked = body;
            return Reply(answer);
        });

        var result = await ReleaseNotesTranslator.TranslateAsync(http.Client, "model", null, outline, "Русский", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Чат", result.Sections[0].Title);

        // Длинное тире заменяет тот же приём ответа, что у перевода интерфейса.
        Assert.Equal("- Один - и всё.", result.Sections[0].Body);
        Assert.Contains("Target language:", asked, StringComparison.Ordinal);
        Assert.Contains("s0.body", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_gives_no_translation_instead_of_an_exception()
    {
        var outline = ReleaseNotesOutline.Parse("# 1.0.0\n\n## Chat\n\n- One.");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"model not found"}""", Encoding.UTF8, "application/json")
        });

        var result = await ReleaseNotesTranslator.TranslateAsync(http.Client, "model", null, outline, "Русский", CancellationToken.None);

        Assert.Null(result);
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }),
            Encoding.UTF8,
            "application/json")
    };

    private static StubClient Client(Func<string, HttpResponseMessage> reply) => new(reply);

    private sealed class StubClient : IDisposable
    {
        private readonly HttpClient _http;

        public StubClient(Func<string, HttpResponseMessage> reply)
        {
            _http = new HttpClient(new StubHandler(reply)) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
            Client = new VeniceClient(_http, new AgentOptions
            {
                ApiKey = "test",
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = "grok-4-6"
            });
        }

        public VeniceClient Client { get; }

        public void Dispose() => _http.Dispose();
    }

    private sealed class StubHandler(Func<string, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            reply(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
    }
}
