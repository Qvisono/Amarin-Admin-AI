using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Chats are one JSON file each, written and read by reflection, so a new field is only safe in
/// both directions if it round-trips AND if a file written before it existed still loads. The
/// second half is the one that would bite: every chat already on disk predates all of these.
/// </summary>
public sealed class NewFieldPersistenceTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }

    [Fact]
    public void The_remark_and_the_call_clock_survive_a_save_and_a_reload()
    {
        var root = TempRoot();
        try
        {
            var store = new ChatStore(root);
            var startedAt = new DateTime(2026, 3, 1, 12, 30, 45);
            var session = new ChatSession { Id = "s1", Title = "Тест" };
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "m1",
                ToolRounds =
                [
                    new ToolRound
                    {
                        ModelNote = "Гляну, что там со службами.",
                        FollowUpNote = "Вижу новое сообщение — учитываю",
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "c1",
                                Name = "windows_service",
                                StartedAt = startedAt,
                                Duration = TimeSpan.FromMilliseconds(1234),
                                Status = ToolCallStatus.Done,
                                Success = true
                            }
                        ]
                    }
                ]
            });

            store.Save(session);
            var round = store.TryLoad("s1")!.Messages[0].ToolRounds[0];

            Assert.Equal("Гляну, что там со службами.", round.ModelNote);
            Assert.Equal("Вижу новое сообщение — учитываю", round.FollowUpNote);
            Assert.Equal(startedAt, round.Calls[0].StartedAt);
            Assert.Equal(TimeSpan.FromMilliseconds(1234), round.Calls[0].Duration);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_context_measurement_survives_a_save_and_a_reload()
    {
        var root = TempRoot();
        try
        {
            var store = new ChatStore(root);
            store.Save(new ChatSession
            {
                Id = "s2",
                LastPromptTokens = 12_345,
                LastPromptTokensApiIndex = 7
            });

            var loaded = store.TryLoad("s2")!;

            Assert.Equal(12_345, loaded.LastPromptTokens);
            Assert.Equal(7, loaded.LastPromptTokensApiIndex);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_chat_written_before_any_of_these_fields_existed_still_loads()
    {
        var root = TempRoot();
        try
        {
            // Exactly the shape the program used to write: no modelNote, no startedAt, no duration,
            // no token count anywhere.
            File.WriteAllText(Path.Combine(root, "chats", "old.json"), """
                {
                  "id": "old",
                  "title": "Старый чат",
                  "messages": [
                    {
                      "role": "assistant",
                      "id": "m1",
                      "text": "готово",
                      "toolRounds": [
                        {
                          "infoLine": "Инструменты завершены",
                          "calls": [
                            { "id": "c1", "name": "registry", "argumentsJson": "{}", "success": true, "status": 2 }
                          ]
                        }
                      ]
                    }
                  ],
                  "apiMessages": []
                }
                """);

            var loaded = new ChatStore(root).TryLoad("old");

            Assert.NotNull(loaded);
            var round = loaded.Messages[0].ToolRounds[0];
            Assert.Equal("", round.ModelNote);
            Assert.Equal("", round.FollowUpNote);
            Assert.Equal(default, round.Calls[0].StartedAt);
            Assert.Equal(TimeSpan.Zero, round.Calls[0].Duration);
            Assert.Equal(0, loaded.LastPromptTokens);

            // And the journal still places it, using the message's time instead of the call's.
            Assert.True(Assert.Single(ActionJournal.FromSession(loaded)).TimeIsApproximate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
