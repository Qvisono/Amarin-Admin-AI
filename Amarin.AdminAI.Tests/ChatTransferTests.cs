using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Отправить» и «Переместить» в меню чата (1.33.0): переписка ложится в другой профиль своим
/// хранилищем — со вложениями, под шифрованием того профиля и без того, что принадлежит этому.
/// </summary>
public sealed class ChatTransferTests : IDisposable
{
    /// <summary>Латиницей: JSON пишет кириллицу кодами, и поиск по байтам её бы не нашёл.</summary>
    private const string Secret = "transfer-secret 4Kq-unique";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-chat-transfer-" + Guid.NewGuid().ToString("N"));

    public ChatTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_copy_lands_in_the_other_profile_under_a_new_id_and_the_original_stays()
    {
        var source = Store("home");
        var session = Session("c1");
        source.Save(session);
        source.Flush();
        var target = Profile("work");

        var outcome = ChatTransfer.Send(session, target, keepId: false);

        Assert.True(outcome.Sent);
        Assert.NotEqual("c1", outcome.ChatId);
        var stored = Assert.Single(Store("work").List());
        Assert.Equal(outcome.ChatId, stored.Id);
        Assert.Equal("Роутер", stored.Title);
        var copy = Store("work").TryLoad(outcome.ChatId!);
        Assert.NotNull(copy);
        Assert.Equal(Secret, copy.Messages[0].Text);
        Assert.NotNull(Store("home").TryLoad("c1"));
    }

    [Fact]
    public void Sending_twice_gives_two_chats_rather_than_overwriting_the_first()
    {
        var target = Profile("twice");
        var session = Session("c1");

        var first = ChatTransfer.Send(session, target, keepId: false);
        var second = ChatTransfer.Send(session, target, keepId: false);

        Assert.True(first.Sent && second.Sent);
        Assert.NotEqual(first.ChatId, second.ChatId);
        Assert.Equal(2, Store("twice").List().Count);
    }

    [Fact]
    public void A_move_keeps_the_id_while_it_is_free_there()
    {
        var target = Profile("move");

        var outcome = ChatTransfer.Send(Session("keep-me"), target, keepId: true);

        Assert.Equal("keep-me", outcome.ChatId);
        Assert.NotNull(Store("move").TryLoad("keep-me"));
    }

    [Fact]
    public void A_move_onto_a_taken_id_gets_a_new_one_instead_of_replacing_the_chat_there()
    {
        var target = Profile("taken");
        var there = Store("taken");
        var existing = Session("same");
        existing.Title = "Своё";
        there.Save(existing);
        there.Flush();

        var outcome = ChatTransfer.Send(Session("same"), target, keepId: true);

        Assert.True(outcome.Sent);
        Assert.NotEqual("same", outcome.ChatId);
        Assert.Equal("Своё", Store("taken").TryLoad("same")!.Title);
    }

    [Fact]
    public void What_belongs_to_this_profile_does_not_travel()
    {
        var session = Session("bound");
        session.TargetMachineId = "server-1";
        session.SelectedKeyId = "key-7";
        session.ScheduleJobId = "job-3";

        var outcome = ChatTransfer.Send(session, Profile("clean"), keepId: true);

        var copy = Store("clean").TryLoad(outcome.ChatId!)!;
        Assert.Null(copy.TargetMachineId);
        Assert.Null(copy.SelectedKeyId);
        Assert.Null(copy.ScheduleJobId);

        // Сама переписка — не копия: её поля остались как были.
        Assert.Equal("server-1", session.TargetMachineId);
        Assert.Equal("key-7", session.SelectedKeyId);
    }

    [Fact]
    public void A_reply_caught_mid_stream_arrives_closed_rather_than_spinning_forever()
    {
        var session = Session("live");
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            Text = "пишу…",
            Status = AssistantStatus.Streaming
        });

        var outcome = ChatTransfer.Send(session, Profile("closed"), keepId: false);

        var copy = Store("closed").TryLoad(outcome.ChatId!)!;
        Assert.Equal(AssistantStatus.Cancelled, copy.Messages[1].Status);
        Assert.Equal(AssistantStatus.Streaming, session.Messages[1].Status);
    }

    [Fact]
    public void Attachments_travel_with_the_chat()
    {
        var image = Convert.ToBase64String(Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray());
        var session = Session("pics");
        session.Messages[0].Images.Add(new ImageAttachment(image, "image/png"));

        var outcome = ChatTransfer.Send(session, Profile("pics"), keepId: true);

        var copy = Store("pics").TryLoad(outcome.ChatId!)!;
        Assert.Equal(image, Assert.Single(copy.Messages[0].Images).Base64);
    }

    [Fact]
    public void The_copy_is_encrypted_by_the_setting_of_the_profile_it_lands_in()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Profile("secret");
        var settings = new AppSettingsStore(target);
        var loaded = settings.Load();
        loaded.EncryptChats = true;
        settings.Save(loaded);
        AppSettingsStore.FlushAll();

        var outcome = ChatTransfer.Send(Session("enc"), target, keepId: true);

        var bytes = File.ReadAllBytes(Path.Combine(target, "chats", outcome.ChatId + ".json"));
        Assert.True(bytes.AsSpan().StartsWith(AtRestCipher.FileMagic));
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)) < 0);
        Assert.Equal(Secret, new ChatStore(target).TryLoad(outcome.ChatId!)!.Messages[0].Text);
    }

    [Fact]
    public void A_profile_that_is_gone_is_reported_rather_than_created()
    {
        var gone = Path.Combine(_root, "profiles", "deleted");

        var outcome = ChatTransfer.Send(Session("x"), gone, keepId: false);

        Assert.Equal(ChatTransferResult.NoProfile, outcome.Result);
        Assert.False(Directory.Exists(gone));
    }

    private static ChatSession Session(string id)
    {
        var session = new ChatSession
        {
            Id = id,
            Title = "Роутер",
            CreatedAt = DateTime.Now.AddHours(-1),
            UpdatedAt = DateTime.Now
        };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = Secret });
        return session;
    }

    private string Profile(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private ChatStore Store(string name) => new(Profile(name));
}
