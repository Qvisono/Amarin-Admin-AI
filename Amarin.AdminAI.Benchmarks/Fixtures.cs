using System.Globalization;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Данные замеров: переписки и поток ответа размером с настоящие. Случайность с постоянным
/// зерном — «до» и «после» меряют одно и то же.
/// </summary>
internal static class Fixtures
{
    private static readonly DateTime Start = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Local);

    private const string Paragraph =
        "Проверил службу обновления Windows: она запущена, но последние три установки закончились " +
        "ошибкой 0x80070643. Это обычно значит, что повреждён компонент .NET или переполнен раздел " +
        "восстановления. Ниже — команды для проверки и порядок действий, если ошибка повторится. ";

    /// <summary>Временная папка данных профиля; удаляется в конце замера.</summary>
    public static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }

    public static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Фоновая запись могла ещё держать файл — папка временная, её уберёт система.
        }
    }

    /// <summary>
    /// Переписка: <paramref name="turns"/> вопросов и ответов, ответы — по несколько абзацев с
    /// раундом инструментов у каждого третьего, и <paramref name="images"/> картинок по 150 КБ.
    /// </summary>
    public static ChatSession Session(string id, int turns, int images, int seed = 1)
    {
        var random = new Random(seed);
        var session = new ChatSession
        {
            Id = id,
            Title = "Ошибка обновления Windows " + id[..Math.Min(6, id.Length)],
            CreatedAt = Start,
            UpdatedAt = Start.AddMinutes(turns),
            SelectedModelId = "grok-4-6"
        };

        for (var turn = 0; turn < turns; turn++)
        {
            var asked = Start.AddMinutes(turn);
            var question = new ChatDisplayMessage
            {
                Role = "user",
                Id = $"{id}-u{turn}",
                CreatedAt = asked,
                Text = "Почему не ставится обновление KB" + (5_000_000 + turn).ToString(CultureInfo.InvariantCulture) + "? Что проверить?"
            };

            if (turn < images)
            {
                question.Images.Add(new ImageAttachment(Image(random), "image/png", "screenshot.png"));
            }

            var answer = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = $"{id}-a{turn}",
                CreatedAt = asked.AddSeconds(30),
                Text = Answer(random, paragraphs: 3 + random.Next(4)),
                Cost = new VeniceCost { Usd = 0.0123m, HasData = true },
                ResolvedModelId = "grok-4-6"
            };

            if (turn % 3 == 0)
            {
                answer.ToolRounds.Add(new ToolRound
                {
                    InfoLine = "Запускаю инструменты",
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = $"call-{turn}",
                            Name = "run_powershell",
                            ArgumentsJson = "{\"script\":\"Get-WindowsUpdateLog; Get-HotFix | Select-Object -First 20\"}",
                            ResultPreview = "Готово",
                            ResultText = Answer(random, paragraphs: 2),
                            Success = true,
                            StartedAt = asked.AddSeconds(5),
                            Duration = TimeSpan.FromSeconds(3)
                        }
                    ]
                });
            }

            session.Messages.Add(question);
            session.Messages.Add(answer);
            session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text(question.Text) });
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text(answer.Text) });
        }

        return session;
    }

    /// <summary>Строка описи — то, что лежит в <c>chats/index.json</c> о каждом чате.</summary>
    public static ChatIndexEntry IndexEntry(int number) => new()
    {
        Id = number.ToString("x32", CultureInfo.InvariantCulture),
        Title = "Чат номер " + number.ToString(CultureInfo.InvariantCulture),
        CreatedAt = Start.AddHours(-number),
        UpdatedAt = Start.AddMinutes(-number),
        Summary = "Разобрали ошибку обновления и проверили журнал событий.",
        TotalCost = 0.5m
    };

    /// <summary>
    /// Тело потокового ответа OpenRouter: <paramref name="chunks"/> кусочков текста, кусок с ценой
    /// и <c>[DONE]</c>. В байтах, как его отдаёт сеть.
    /// </summary>
    public static byte[] Sse(int chunks)
    {
        var random = new Random(7);
        var words = Paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var body = new StringBuilder(chunks * 90);
        for (var i = 0; i < chunks; i++)
        {
            var piece = words[random.Next(words.Length)] + (i % 40 == 39 ? "\\n\\n" : " ");
            body.Append("data: {\"id\":\"gen-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"")
                .Append(piece)
                .Append("\"}}]}\n\n");
        }

        body.Append("data: {\"id\":\"gen-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        body.Append("data: {\"id\":\"gen-1\",\"choices\":[],\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":900,\"total_tokens\":2100,\"cost\":0.0042}}\n\n");
        body.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    private static string Answer(Random random, int paragraphs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < paragraphs; i++)
        {
            text.Append("### Шаг ").Append(i + 1).Append('\n').Append(Paragraph).Append(Paragraph[..(40 + random.Next(120))]).Append("\n\n");
            if (i % 2 == 1)
            {
                text.Append("```powershell\nGet-Service wuauserv | Restart-Service -Force\nsfc /scannow\n```\n\n");
            }
        }

        return text.ToString();
    }

    /// <summary>Картинка 150 КБ в base64 — как снимок экрана, приложенный к вопросу.</summary>
    private static string Image(Random random)
    {
        var bytes = new byte[150 * 1024];
        random.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}
