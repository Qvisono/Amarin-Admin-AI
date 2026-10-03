using System.Text.Json;
using Amarin.Core;
using BenchmarkDotNet.Attributes;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Список чатов: чтение описи при запуске, поиск по тексту всех чатов (на каждое нажатие в поле
/// поиска) и запись <c>search.idx</c>.
/// </summary>
public class ChatListBenchmarks
{
    private string _root = "";
    private ChatTextIndex? _textIndex;

    private ChatTextIndex TextIndex => _textIndex ?? throw new InvalidOperationException("GlobalSetup не выполнялся.");

    [GlobalSetup]
    public void Setup()
    {
        _root = Fixtures.NewRoot();

        // Опись на тысячу чатов — тем же сериализатором, что и программа.
        var index = new ChatIndex { Items = [.. Enumerable.Range(0, 1000).Select(Fixtures.IndexEntry)] };
        File.WriteAllText(
            Path.Combine(_root, "chats", "index.json"),
            JsonSerializer.Serialize(index, AppJson.Options) + Environment.NewLine);

        // Индекс текста: триста чатов по сорок вопросов и ответов.
        var textIndex = new ChatTextIndex(_root, static () => false);
        var sessions = Enumerable.Range(0, 300)
            .Select(i => Fixtures.Session("chat" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), turns: 40, images: 0, seed: i))
            .ToDictionary(session => session.Id, StringComparer.Ordinal);
        var entries = sessions.Values
            .Select(session => new ChatIndexEntry { Id = session.Id, Title = session.Title, UpdatedAt = session.UpdatedAt })
            .ToList();
        textIndex.Build(entries, id => sessions.GetValueOrDefault(id), CancellationToken.None);
        _textIndex = textIndex;
    }

    [GlobalCleanup]
    public void Cleanup() => Fixtures.Delete(_root);

    /// <summary>Опись при запуске: чтение и разбор <c>index.json</c>.</summary>
    [Benchmark]
    public int LoadIndex() => new ChatStore(_root).List().Count;

    /// <summary>Частое слово: находок больше, чем показывается.</summary>
    [Benchmark]
    public int SearchCommonWord() => TextIndex.Search("обновления").Count;

    /// <summary>Редкая строка: просмотр всего текста без раннего выхода.</summary>
    [Benchmark]
    public int SearchRareWord() => TextIndex.Search("KB5000039").Count;

    /// <summary>Запись <c>search.idx</c> — пять секунд тишины после каждого сохранения чата.</summary>
    [Benchmark]
    public void SaveTextIndex() => TextIndex.SaveNow();
}
