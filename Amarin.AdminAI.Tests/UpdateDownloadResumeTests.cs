using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Загрузка обновления попытками с докачкой (<see cref="ResumableDownload"/>). До 1.32.0 файл
/// качался одним запросом: плохое первое соединение на запуске тянулось минутами, и лечили это
/// только «Отмена» и «Обновить», открывавшие новое. Часы ручные, сервер — по сценарию.
/// </summary>
public sealed class UpdateDownloadResumeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-resume-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new();
    private readonly byte[] _payload = RandomNumberGenerator.GetBytes(50_000);

    public UpdateDownloadResumeTests() => Directory.CreateDirectory(_root);

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
    public async Task A_hung_connection_is_replaced_and_the_new_one_continues_from_the_same_byte()
    {
        var hung = new TaskCompletionSource();
        var server = new ScriptedServer(_payload, Reply.Hang(1000, hung), Reply.Rest());

        var download = Start(server, Policy(minBytes: 1));
        await hung.Task.WaitAsync(Wait);
        Pass(25);
        var (result, file) = await download.WaitAsync(Wait);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(file!));
        Assert.Equal(new long?[] { null, 1000 }, server.RangeStarts);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_sends_the_file_again_and_it_is_taken_from_the_start()
    {
        var hung = new TaskCompletionSource();
        var server = new ScriptedServer(_payload, Reply.Hang(1000, hung), Reply.Whole());

        var download = Start(server, Policy(minBytes: 1));
        await hung.Task.WaitAsync(Wait);
        Pass(25);
        var (result, file) = await download.WaitAsync(Wait);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(file!));
    }

    [Fact]
    public async Task A_dropped_connection_resumes()
    {
        var server = new ScriptedServer(_payload, Reply.Break(1000), Reply.Rest());

        var (result, file) = await Start(server, Policy(minBytes: 1)).WaitAsync(Wait);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(file!));
        Assert.Equal(new long?[] { null, 1000 }, server.RangeStarts);
    }

    [Fact]
    public async Task A_slow_connection_is_replaced_and_after_the_last_replacement_the_download_keeps_going()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var third = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var server = new ScriptedServer(_payload, Reply.Hang(10, first), Reply.Hang(10, second), Reply.Pause(10, third, release));

        var download = Start(server, Policy(minBytes: 100_000, maxRestarts: 2, silence: TimeSpan.FromSeconds(60)));
        await first.Task.WaitAsync(Wait);
        Pass(11);
        await second.Task.WaitAsync(Wait);
        Pass(11);
        await third.Task.WaitAsync(Wait);

        // Смены кончились: скорость больше не меряется, и медленная, но живая загрузка идёт дальше.
        Pass(30);
        Assert.False(download.IsCompleted);
        release.SetResult();
        var (result, file) = await download.WaitAsync(Wait);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(file!));
        Assert.Equal(new long?[] { null, 10, 20 }, server.RangeStarts);
    }

    [Fact]
    public async Task A_connection_silent_after_the_last_replacement_is_given_up_with_a_reason()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var server = new ScriptedServer(_payload, Reply.Hang(0, first), Reply.Hang(0, second));

        var download = Start(server, Policy(minBytes: 1, maxRestarts: 1));
        await first.Task.WaitAsync(Wait);
        Pass(11);
        await second.Task.WaitAsync(Wait);
        Pass(25);
        var (result, file) = await download.WaitAsync(Wait);

        Assert.False(result.Ok);
        Assert.Null(file);
        Assert.Equal(Loc.Get("S.Updates.Stalled"), result.Error);
        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_client_timeout_is_another_attempt_and_not_a_cancel()
    {
        // Срок клиента приходит той же отменой, что и отказ человека. До 1.32.0 загрузку это
        // молча гасило до следующей проверки через пять часов.
        var server = new ScriptedServer(_payload, Reply.Timeout(), Reply.Whole());

        var (result, file) = await Start(server, Policy(minBytes: 1)).WaitAsync(Wait);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(file!));
        Assert.Equal(2, server.RangeStarts.Count);
    }

    [Fact]
    public async Task Cancelling_is_a_cancel_and_leaves_no_partial_file()
    {
        var hung = new TaskCompletionSource();
        var server = new ScriptedServer(_payload, Reply.Hang(1000, hung));
        var cancel = new CancellationTokenSource();
        try
        {
            var download = Start(server, Policy(minBytes: 1), cancel.Token);
            await hung.Task.WaitAsync(Wait);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(Wait));
        }
        finally
        {
            cancel.Dispose();
        }

        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    // ── вспомогательное ──────────────────────────────────────────────────────────

    /// <summary>
    /// Секунды идут по одной, как у настоящих часов: сторож раз в секунду сверяет, сколько
    /// пришло, а сдвиг одним шагом показал бы ему все тики сразу в последнем мгновении.
    /// </summary>
    private void Pass(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private DownloadPolicy Policy(long minBytes, int maxRestarts = 4, TimeSpan? silence = null) =>
        new(silence ?? TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), minBytes, maxRestarts, _time);

    private Task<(UpdateStepResult Result, string? File)> Start(ScriptedServer server, DownloadPolicy policy, CancellationToken cancellationToken = default)
    {
        var asset = new ReleaseAsset(
            "Amarin-Admin-AI-v1.32.0-win-x64.exe",
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.32.0/app.exe",
            _payload.Length,
            Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant());
        var plan = new UpdatePlan(asset, Path.Combine(_root, "app.exe"), Path.Combine(_root, ".update"));
        var http = new HttpClient(server);

        // На пуле потоков: часы двигает тест, и загрузка не должна ждать его потока.
        return Task.Run(() => UpdateInstaller.DownloadAsync(plan, http, null, cancellationToken, allowUnverified: false, policy), cancellationToken);
    }

    /// <summary>Ответ сервера на одну попытку.</summary>
    private sealed record Reply(Func<byte[], long, CancellationToken, Task<HttpResponseMessage>> Respond)
    {
        /// <summary>Весь файл целиком, даже если просили с середины.</summary>
        public static Reply Whole() => new((payload, _, _) => Task.FromResult(Response(payload, 0, End.Normally, null, null)));

        /// <summary>Остаток с того байта, с которого просили.</summary>
        public static Reply Rest() => new((payload, from, _) => Task.FromResult(Response(payload, from, End.Normally, null, null)));

        /// <summary>Отдаёт <paramref name="bytes"/> и молчит до обрыва.</summary>
        public static Reply Hang(int bytes, TaskCompletionSource silent) =>
            new((payload, from, _) => Task.FromResult(Response(payload, from, End.Hang, silent, null, bytes)));

        /// <summary>Отдаёт <paramref name="bytes"/>, ждёт <paramref name="release"/> и отдаёт остальное.</summary>
        public static Reply Pause(int bytes, TaskCompletionSource paused, TaskCompletionSource release) =>
            new((payload, from, _) => Task.FromResult(Response(payload, from, End.Pause, paused, release, bytes)));

        /// <summary>Отдаёт <paramref name="bytes"/> и обрывает соединение.</summary>
        public static Reply Break(int bytes) =>
            new((payload, from, _) => Task.FromResult(Response(payload, from, End.Break, null, null, bytes)));

        /// <summary>Клиент не дождался заголовков в свой срок.</summary>
        public static Reply Timeout() => new((_, _, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));

        private static HttpResponseMessage Response(byte[] payload, long from, End end, TaskCompletionSource? signal, TaskCompletionSource? release, int? first = null)
        {
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new ScriptedStream(payload, (int)from, first ?? payload.Length, end, signal, release))
            };
            response.Content.Headers.ContentLength = payload.Length - from;
            if (from > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            }

            return response;
        }
    }

    private enum End
    {
        Normally,
        Hang,
        Pause,
        Break
    }

    /// <summary>Сервер по сценарию: каждая попытка получает свой ответ, просьбы о докачке записываются.</summary>
    private sealed class ScriptedServer(byte[] payload, params Reply[] replies) : HttpMessageHandler
    {
        private int _next;

        public List<long?> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.First().From;
            lock (RangeStarts)
            {
                RangeStarts.Add(from);
            }

            var reply = replies[Math.Min(Interlocked.Increment(ref _next) - 1, replies.Length - 1)];
            return reply.Respond(payload, from ?? 0, cancellationToken);
        }
    }

    /// <summary>Тело ответа: отдаёт кусок порциями, а дальше — как велит сценарий.</summary>
    private sealed class ScriptedStream(byte[] payload, int from, int first, End end, TaskCompletionSource? signal, TaskCompletionSource? release) : Stream
    {
        private int _position = from;
        private int _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= first && _position < payload.Length)
            {
                switch (end)
                {
                    case End.Hang:
                        signal?.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        break;
                    case End.Pause when release is not null && !release.Task.IsCompleted:
                        signal?.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        break;
                    case End.Break:
                        throw new IOException("Connection reset by peer.");
                }
            }

            var left = end == End.Normally || (end == End.Pause && release?.Task.IsCompleted == true)
                ? payload.Length - _position
                : Math.Min(first - _served, payload.Length - _position);
            var count = Math.Min(Math.Min(buffer.Length, 4096), left);
            if (count <= 0)
            {
                return 0;
            }

            payload.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            _served += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
