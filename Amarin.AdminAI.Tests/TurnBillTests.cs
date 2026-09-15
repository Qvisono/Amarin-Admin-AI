using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Счёт хода складывается из разных карманов, и перепутать знак здесь значит соврать в «Итого» —
/// числе, которому человек как раз и должен верить.
/// </summary>
/// <remarks>
/// Деньги маршрутизатора приходят тем же клиентом, что и разговор, то есть уже сидят в общем
/// счёте хода: их вычитают. Деньги заголовка платятся отдельным клиентом и в счёт не входят:
/// их прибавляют. Проверка обоих направлений — единственное, что держит эту разницу.
/// </remarks>
public sealed class TurnBillTests
{
    private static VeniceCost Usd(decimal amount) => new() { Usd = amount, HasData = true };

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "amarin-bill-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_router_charge_leaves_the_model_row()
    {
        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            RouterCost = Usd(0.001m)
        };

        ChatEngine.ApplyCosts(assistant, Usd(0.021m));

        Assert.Equal(0.02m, assistant.ModelCost?.Usd);
        Assert.Equal(0.021m, assistant.Cost?.Usd);
    }

    [Fact]
    public void The_title_charge_is_added_not_subtracted()
    {
        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            TitleCost = Usd(0.0004m)
        };

        ChatEngine.ApplyCosts(assistant, Usd(0.02m));

        Assert.Equal(0.02m, assistant.ModelCost?.Usd);
        Assert.Equal(0.0204m, assistant.Cost?.Usd);
    }

    [Fact]
    public void Settling_the_same_message_twice_does_not_double_the_bill()
    {
        // По сообщению можно пройти второй раз: отмена приходит после того, как ответ уже закрыт
        // ради дописанного сообщения. Пересчёт вместо прибавления — то, на чём это держится.
        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            RouterCost = Usd(0.001m),
            TitleCost = Usd(0.0004m)
        };

        ChatEngine.ApplyCosts(assistant, Usd(0.021m));
        ChatEngine.ApplyCosts(assistant, Usd(0.021m));

        Assert.Equal(0.02m, assistant.ModelCost?.Usd);
        Assert.Equal(0.0214m, assistant.Cost?.Usd);
    }

    [Fact]
    public void The_router_and_title_charges_survive_a_save_and_a_reload()
    {
        var root = TempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew();
            session.TitleCost = Usd(0.0004m);
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "a1",
                Text = "готово",
                RouterCost = Usd(0.001m),
                TitleCost = Usd(0.0004m),
                ModelCost = Usd(0.02m),
                Cost = Usd(0.0214m)
            });
            store.Save(session);

            var loaded = store.TryLoad(session.Id);

            Assert.NotNull(loaded);
            Assert.Equal(0.0004m, loaded.TitleCost?.Usd);
            var answer = loaded.Messages.Single();
            Assert.Equal(0.001m, answer.RouterCost?.Usd);
            Assert.Equal(0.0004m, answer.TitleCost?.Usd);
            Assert.Equal(0.0214m, answer.Cost?.Usd);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void A_chat_saved_before_the_two_charges_existed_still_loads()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "chats"));
            File.WriteAllText(
                Path.Combine(root, "chats", "old.json"),
                """
                {
                  "id": "old",
                  "title": "Старый чат",
                  "messages": [
                    { "role": "assistant", "id": "a1", "text": "готово",
                      "cost": { "usd": 0.02, "hasData": true } }
                  ]
                }
                """);

            var answer = new ChatStore(root).TryLoad("old")?.Messages.Single();

            Assert.NotNull(answer);
            Assert.Null(answer.RouterCost);
            Assert.Null(answer.TitleCost);
            Assert.Equal(0.02m, answer.Cost?.Usd);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
