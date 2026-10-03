using Amarin.Core;
using BenchmarkDotNet.Attributes;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Запись чата во время ответа: ход сохраняет переписку по таймеру дважды в секунду, и каждый раз
/// меняется время последнего сообщения. Две половины пути — то, что достаётся потоку окна
/// (<see cref="SaveOnUiThread"/>: опись, цена, индекс поиска), и запись целиком до диска.
/// </summary>
public class ChatStoreBenchmarks
{
    private Fixture? _fixture;
    private int _tick;

    /// <summary>Вопросов и ответов в переписке.</summary>
    [Params(60)]
    public int Turns { get; set; }

    private Fixture Ready => _fixture ?? throw new InvalidOperationException("GlobalSetup не выполнялся.");

    [GlobalSetup]
    public void Setup() => _fixture = new Fixture(Turns);

    [GlobalCleanup]
    public void Cleanup()
    {
        Ready.Store.Flush();
        Fixtures.Delete(Ready.Root);
    }

    /// <summary>Синхронная часть сохранения — то, что ждёт поток окна.</summary>
    [Benchmark]
    public void SaveOnUiThread()
    {
        var ready = Ready;
        ready.Touch(++_tick);
        ready.Store.Save(ready.Session);
        if (_tick % 64 == 0)
        {
            ready.Store.Flush();
        }
    }

    /// <summary>Сохранение до диска: сериализация, вынос вложений, запись файлов.</summary>
    [Benchmark]
    public void SaveAndWrite()
    {
        var ready = Ready;
        ready.Touch(++_tick);
        ready.Store.Save(ready.Session);
        ready.Store.Flush();
    }

    /// <summary>Открытие чата с диска: чтение, развёртывание вложений, разбор.</summary>
    [Benchmark]
    public int Load() => Ready.Store.TryLoad(Ready.Session.Id)?.Messages.Count ?? 0;

    /// <summary>Хранилище с описью на полтысячи чатов и большая переписка, которую дописывает ход.</summary>
    private sealed class Fixture
    {
        private readonly string[] _answers;

        public Fixture(int turns)
        {
            Root = Fixtures.NewRoot();
            Store = new ChatStore(Root);
            var textIndex = new ChatTextIndex(Root, static () => false);
            Store.Saved += session => textIndex.Update(session);

            // Опись с полутысячей чатов: её пересобирает каждое сохранение, где что-то поменялось.
            for (var i = 0; i < 500; i++)
            {
                Store.Save(Fixtures.Session("small" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), turns: 2, images: 0, seed: i));
            }

            Session = Fixtures.Session("current", turns, images: 2);
            var last = Session.Messages[^1].Text;
            _answers = [last + "a", last + "b"];
            Store.Save(Session);
            Store.Flush();
        }

        public string Root { get; }

        public ChatStore Store { get; }

        public ChatSession Session { get; }

        /// <summary>Ход дописал ответ: время и текст последнего сообщения поменялись.</summary>
        public void Touch(int tick)
        {
            Session.UpdatedAt = Session.UpdatedAt.AddSeconds(1);
            Session.Messages[^1].Text = _answers[tick & 1];
        }
    }
}
