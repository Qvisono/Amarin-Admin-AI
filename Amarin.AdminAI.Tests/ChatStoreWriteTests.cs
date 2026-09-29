using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Переписка обязана ложиться на диск и читаться обратно: глубоко вложенная и в момент записи.
/// </summary>
/// <remarks>
/// Заводской предел System.Text.Json — 64 уровня. Упёршийся в него чат не записывался (исключение
/// роняло весь проход записи, а чат уже был снят с очереди) и не читался — в боковой панели он
/// оставался, а открыть его было нельзя. Варианты ответа внутри вариантов добавляют уровни.
/// </remarks>
public sealed class ChatStoreWriteTests
{
    [Fact]
    public void Two_threads_saving_one_file_at_once_do_not_collide()
    {
        // Общий временный «settings.json.tmp» сталкивал привязку служб окна с фоновым переносом
        // трат: второй писатель получал IOException, и тест на раннере оставлял после себя
        // открытое окно — за ним валились три сотни соседних.
        var root = Path.Combine(Path.GetTempPath(), "amarin-atomic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var errors = 0;
            Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    AppDataFile.WriteAtomic(path, $"{{\"n\":{i}}}");
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref errors);
                }
            });

            Assert.Equal(0, errors);
            Assert.StartsWith("{\"n\":", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task A_chat_being_written_right_now_is_still_found()
    {
        // Фоновая запись снимает чат с очереди раньше, чем кладёт его на диск. В это окно
        // TryLoad не видел его ни там, ни там и отвечал «такого нет» — переименование сразу
        // после сохранения молча не срабатывало.
        var root = Path.Combine(Path.GetTempPath(), "amarin-inflight-" + Guid.NewGuid().ToString("N"));
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var store = new ChatStore(root)
            {
                WritingChat = _ =>
                {
                    writing.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            };

            var session = store.CreateNew("grok-4-6");
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u", Text = "привет" });
            store.Save(session);
            Assert.True(writing.Wait(TimeSpan.FromSeconds(10)), "запись не началась");

            var load = Task.Run(() => store.TryLoad(session.Id));
            await Task.Delay(100);
            release.Set();

            var loaded = await load;
            Assert.NotNull(loaded);
            Assert.Equal("привет", loaded!.Messages[0].Text);
        }
        finally
        {
            release.Set();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_deeply_nested_chat_is_written_and_read_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-depth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("grok-4-6");
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u", Text = "глубоко" });
            var assistant = new ChatDisplayMessage { Role = "assistant", Id = "a", Text = "ответ" };
            session.Messages.Add(assistant);

            // Каждый вложенный агент добавляет шесть уровней: раунд, список вызовов, вызов,
            // запись агента, её раунды. Двадцать — это за сотню уровней, далеко за 64.
            var rounds = assistant.ToolRounds;
            for (var depth = 0; depth < 20; depth++)
            {
                var agent = new AgentRunRecord { DisplayName = $"агент {depth}" };
                rounds.Add(new ToolRound
                {
                    Calls = [new ToolCallRecord { Id = $"c{depth}", Name = "init_agent", NestedAgent = agent }]
                });
                rounds = agent.ToolRounds;
            }

            store.Save(session);
            store.Flush();

            var loaded = store.TryLoad(session.Id);

            Assert.NotNull(loaded);
            var nested = loaded!.Messages[1].ToolRounds[0].Calls[0].NestedAgent;
            for (var depth = 1; depth < 20; depth++)
            {
                nested = nested!.ToolRounds[0].Calls[0].NestedAgent;
            }

            Assert.Equal("агент 19", nested!.DisplayName);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
