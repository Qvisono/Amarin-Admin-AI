using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кнопка «new language» отдаёт весь каталог строк модели. Ответ модели — грязный JSON: она
/// заворачивает его в ограждение, дописывает пояснения и теряет ключи. Эти тесты держат отбор:
/// в перевод попадает только то, чему можно верить, а остальное остаётся русским.
/// </summary>
public sealed class LanguageTranslatorTests
{
    private static Dictionary<string, string> Source(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => $"S.Test.K{i:000}", i => $"строка {i}");

    [Fact]
    public void The_catalogue_goes_out_in_batches()
    {
        // Одним куском ответ упёрся бы в предел длины, а сбой стоил бы всего перевода.
        var batches = LanguageTranslator.Split(Source(95)).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal(LanguageTranslator.BatchSize, batches[0].Count);
        Assert.Equal(15, batches[2].Count);
        Assert.Equal(95, batches.Sum(b => b.Count));
    }

    [Fact]
    public void A_fenced_answer_with_chatter_after_it_still_parses()
    {
        var asked = new Dictionary<string, string> { ["S.A"] = "Привет", ["S.B"] = "Пока" };
        const string reply = """
            Sure, here is the translation:

            ```json
            {"S.A": "Hello", "S.B": "Bye"}
            ```

            Let me know if you need anything else.
            """;

        var accepted = LanguageTranslator.Accept(asked, reply);

        Assert.Equal("Hello", accepted["S.A"]);
        Assert.Equal("Bye", accepted["S.B"]);
    }

    [Fact]
    public void Extra_keys_from_the_model_are_ignored()
    {
        var asked = new Dictionary<string, string> { ["S.A"] = "Привет" };

        var accepted = LanguageTranslator.Accept(
            asked, """{"S.A": "Hello", "S.Invented": "Nope"}""");

        Assert.Equal(["S.A"], accepted.Keys);
    }

    [Fact]
    public void A_missing_key_simply_stays_russian()
    {
        var asked = new Dictionary<string, string> { ["S.A"] = "Привет", ["S.B"] = "Пока" };

        var accepted = LanguageTranslator.Accept(asked, """{"S.A": "Hello"}""");

        Assert.Equal(["S.A"], accepted.Keys);
    }

    [Fact]
    public void A_key_that_lost_its_placeholder_is_thrown_away()
    {
        // Расхождение по {0} — не «звучит криво», а исключение при форматировании в интерфейсе.
        var asked = new Dictionary<string, string>
        {
            ["S.Good"] = "Пропущено файлов: {0}",
            ["S.Lost"] = "Осталось попыток: {0}",
            ["S.Swapped"] = "{0} из {1}"
        };

        var accepted = LanguageTranslator.Accept(asked, """
            {
              "S.Good": "Files skipped: {0}",
              "S.Lost": "Attempts left",
              "S.Swapped": "{1} of {0}"
            }
            """);

        Assert.Equal("Files skipped: {0}", accepted["S.Good"]);
        Assert.DoesNotContain("S.Lost", accepted.Keys);

        // Переставленные местами подстановки допустимы: порядок слов в языках разный.
        Assert.Equal("{1} of {0}", accepted["S.Swapped"]);
    }

    [Fact]
    public void An_empty_translation_is_not_accepted()
    {
        var asked = new Dictionary<string, string> { ["S.A"] = "Привет" };

        Assert.Empty(LanguageTranslator.Accept(asked, """{"S.A": "   "}"""));
    }

    [Fact]
    public void Prose_instead_of_json_costs_one_batch_not_the_run()
    {
        var asked = new Dictionary<string, string> { ["S.A"] = "Привет" };

        Assert.Empty(LanguageTranslator.Accept(asked, "I cannot do that."));
    }

    [Theory]
    [InlineData("Deutsch", "de")]
    [InlineData("Français", "fr")]
    [InlineData("日本語", "ja")]
    [InlineData("Українська", "uk")]
    [InlineData("Klingon", "klingon")]
    public void The_language_gets_a_stable_file_name(string name, string code)
    {
        Assert.Equal(code, LanguageTranslator.CodeFor(name));

        // Повторный перевод того же языка обязан лечь в тот же файл, а не плодить копии.
        Assert.Equal(LanguageTranslator.CodeFor(name), LanguageTranslator.CodeFor(name));
    }

    [Fact]
    public async Task A_refusal_from_the_model_is_a_plain_sentence_not_an_exception()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"model not found: openai-gpt-56-luna-pro"}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var translator = new LanguageTranslator(new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        }));

        var result = await translator.TranslateAsync(
            new Dictionary<string, string> { ["S.A"] = "Привет" },
            "Deutsch",
            progress: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains(LanguageTranslator.TranslationModelId, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_is_reported_once_per_batch()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"role":"assistant","content":"{}"}}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var translator = new LanguageTranslator(new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        }));

        var steps = new List<TranslationProgress>();
        var result = await translator.TranslateAsync(
            Source(95), "Deutsch", steps.Add, CancellationToken.None);

        Assert.Equal(3, steps.Count);
        Assert.Equal(new TranslationProgress(3, 3), steps[^1]);

        // Модель вернула пустые объекты — переводить нечего, и это отказ, а не «успех с нулём».
        Assert.False(result.Success);
    }

    [Fact]
    public void A_user_language_file_round_trips()
    {
        var code = "tst" + Guid.NewGuid().ToString("N")[..4];
        try
        {
            UserLanguageStore.Save(code, new Dictionary<string, string>
            {
                ["S.Common.Save"] = "Speichern",
                [UserLanguageStore.NameKey] = "Deutsch"
            });

            Assert.True(UserLanguageStore.Exists(code));
            Assert.Equal("Deutsch", UserLanguageStore.NameOf(code));
            Assert.Contains(code, UserLanguageStore.List());
        }
        finally
        {
            File.Delete(UserLanguageStore.FileFor(code));
        }
    }

    [Fact]
    public void A_created_language_can_be_deleted()
    {
        var code = "tst" + Guid.NewGuid().ToString("N")[..4];
        UserLanguageStore.Save(code, new Dictionary<string, string>
        {
            [UserLanguageStore.NameKey] = "Deutsch"
        });

        Assert.True(UserLanguageStore.Exists(code));
        Assert.True(UserLanguageStore.Delete(code));

        Assert.False(UserLanguageStore.Exists(code));
        Assert.DoesNotContain(code, UserLanguageStore.List());

        // Удалённый код больше не считается живым языком — интерфейс уйдёт в русский.
        Assert.Equal(LanguageManager.DefaultCode, LanguageManager.Normalize(code));
    }

    [Fact]
    public void Deleting_something_that_is_not_there_is_not_an_error()
    {
        Assert.False(UserLanguageStore.Delete("tst" + Guid.NewGuid().ToString("N")[..4]));
        Assert.False(UserLanguageStore.Delete(""));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("EN")]
    public void The_built_in_languages_are_never_deleted(string code)
    {
        // Они лежат в сборке, а не файлом, но одноимённый файл на диске лежать может:
        // Available() его не показывает, и стирать его было бы удалением вслепую.
        Assert.False(UserLanguageStore.Delete(code));
        Assert.True(LanguageManager.IsBuiltIn(code));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
