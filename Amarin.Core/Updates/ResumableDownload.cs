using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Amarin.Core;

/// <summary>
/// Когда загрузку обновления пора перевести на новое соединение. Подменяется в тестах.
/// </summary>
/// <param name="Silence">Ни байта столько времени — соединение повисло. Заголовков ждём столько же.</param>
/// <param name="Window">Отрезок, за который меряется скорость.</param>
/// <param name="MinBytesPerWindow">Меньше за отрезок — соединение медленное.</param>
/// <param name="MaxRestarts">
/// Сколько раз соединение меняется. Дальше медленная загрузка качает как есть, а повисшая сдаётся.
/// </param>
/// <param name="Time">Часы сторожа.</param>
public sealed record DownloadPolicy(TimeSpan Silence, TimeSpan Window, long MinBytesPerWindow, int MaxRestarts, TimeProvider Time)
{
    /// <summary>
    /// Двадцать секунд тишины, 128 КБ/с за десять секунд, четыре смены соединения.
    /// </summary>
    /// <remarks>
    /// Порог скорости — не про медленную сеть, а про плохое соединение: на живой сети сборка
    /// в девяносто мегабайт приходит за секунды, а застрявшая на холодном узле CDN тянулась
    /// минутами. Медленной сети перезапуски не вредят: докачка продолжает с того же байта, а
    /// после четырёх смен загрузка качает как есть.
    /// </remarks>
    public static DownloadPolicy Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(10),
        128 * 1024 * 10,
        4,
        TimeProvider.System);
}

/// <summary>
/// Загрузка файла обновления попытками с докачкой: повисшее или медленное соединение меняется
/// на новое, и новое продолжает с того байта, на котором остановилось прежнее.
/// </summary>
/// <remarks>
/// <para>
/// До 1.32.0 файл качался одним запросом. Автозагрузка открывает первое соединение в первые
/// секунды запуска — у свежего выпуска холодный кэш CDN, сеть после входа в систему ещё
/// устанавливается, — и плохое соединение тянулось минутами: сторож зависания срабатывал лишь
/// ниже нескольких килобайт в секунду, а заменить соединение было нечем. «Отмена» и
/// «Обновить» лечили это ровно потому, что открывали новое.
/// </para>
/// <para>
/// Каждая попытка — HTTP/1.1: обрыв тела закрывает соединение, и следующая попытка уходит по
/// новому. По HTTP/2 новый запрос встал бы в то же повисшее соединение. Байты пишутся в файл
/// раньше, чем их учитывает сумма и счётчик: сторож может оборвать попытку в любой миг, и
/// учтённое обязано совпадать с записанным.
/// </para>
/// </remarks>
internal sealed class ResumableDownload : IAsyncDisposable
{
    private const int BufferSize = 1024 * 1024;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly long _expected;
    private readonly long _maxBytes;
    private readonly IProgress<double>? _progress;
    private readonly DownloadPolicy _policy;
    private readonly FileStream _file;
    private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _received;
    private long _total;
    private double _lastReported = -1;

    /// <param name="expected">Заявленный размер; 0 — неизвестен, тогда его скажет ответ.</param>
    public ResumableDownload(
        HttpClient http,
        string url,
        string path,
        long expected,
        long maxBytes,
        IProgress<double>? progress,
        DownloadPolicy policy)
    {
        _http = http;
        _url = url;
        _expected = expected;
        _total = expected;
        _maxBytes = maxBytes;
        _progress = progress;
        _policy = policy;

        // Асинхронный дескриптор: запись на синхронном уходила бы в пул потоков на каждую
        // порцию, а на запуске пул занят — загрузка ждала бы свою очередь тысячи раз.
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>Сколько раз соединение меняли.</summary>
    public int Restarts { get; private set; }

    /// <summary>Качает до конца. Возвращает SHA-256 скачанного, шестнадцатерично, строчными.</summary>
    /// <exception cref="OperationCanceledException">Только отмена человеком.</exception>
    /// <exception cref="IOException">Отказ сервера, диск, размер или исчерпанные попытки — текст для человека.</exception>
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var (end, error) = await AttemptAsync(cancellationToken).ConfigureAwait(false);
            if (end == AttemptEnd.Finished)
            {
                _progress?.Report(1);
                return Convert.ToHexString(_hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (Restarts >= _policy.MaxRestarts)
            {
                // Медленной попытка после последней смены не бывает — скорость больше не меряется.
                throw new IOException(end == AttemptEnd.Broken && error is not null ? error : Loc.Get("S.Updates.Stalled"));
            }

            Restarts++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _file.DisposeAsync().ConfigureAwait(false);
        _hasher.Dispose();
    }

    private async Task<(AttemptEnd End, string? Error)> AttemptAsync(CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watch = new Watchdog(_policy, () => Interlocked.Read(ref _received), checkSpeed: Restarts < _policy.MaxRestarts, attempt);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.UserAgent.ParseAdd("Amarin-Admin-AI-Updater");
            if (_received > 0)
            {
                request.Headers.Range = new RangeHeaderValue(_received, null);
            }

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);

            if (_received > 0 && !ContinuesFrom(response, _received))
            {
                // Сервер не докачивает с места: отдал файл целиком (200) или не понял диапазон.
                // Начинаем заново — сумма считается по всему файлу, склеивать нечего.
                Rewind();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return (AttemptEnd.Broken, Loc.Format("S.Updates.Status", (int)response.StatusCode));
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException(Loc.Format("S.Updates.Status", (int)response.StatusCode));
            }

            // Размер не заявлен — его скажет ответ: у докачки длина в Content-Range, а
            // Content-Length у неё — только остаток.
            if (_total <= 0)
            {
                _total = response.StatusCode == HttpStatusCode.PartialContent
                    ? response.Content.Headers.ContentRange?.Length ?? 0
                    : response.Content.Headers.ContentLength ?? 0;
            }

            if (_total > _maxBytes)
            {
                throw new IOException(Loc.Get("S.Updates.TooBig"));
            }

            await CopyAsync(response, attempt.Token, cancellationToken).ConfigureAwait(false);
            return (AttemptEnd.Finished, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Оборвал сторож — или клиент не дождался заголовков в свой срок: это тоже тишина,
            // а не «человек отменил».
            return (watch.Verdict ?? AttemptEnd.Silent, null);
        }
        catch (HttpRequestException ex)
        {
            return (AttemptEnd.Broken, ex.Message);
        }
        catch (BrokenConnection ex)
        {
            return (AttemptEnd.Broken, ex.Message);
        }
    }

    /// <summary>Ответ продолжает файл ровно с <paramref name="offset"/>.</summary>
    private static bool ContinuesFrom(HttpResponseMessage response, long offset) =>
        response.StatusCode == HttpStatusCode.PartialContent &&
        response.Content.Headers.ContentRange is { From: { } from } &&
        from == offset;

    private void Rewind()
    {
        _file.SetLength(0);
        _file.Position = 0;
        _ = _hasher.GetHashAndReset();
        Interlocked.Exchange(ref _received, 0);
        _total = _expected;
        _lastReported = -1;
    }

    /// <param name="attempt">Обрывает чтение: сторож, отмена.</param>
    /// <param name="cancellationToken">
    /// Только отмена человеком. Запись в файл сторож не обрывает: недописанная порция разошлась
    /// бы с учтённой, и сумма не сошлась бы в конце.
    /// </param>
    private async Task CopyAsync(HttpResponseMessage response, CancellationToken attempt, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(attempt).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, attempt).ConfigureAwait(false);
                }
                catch (IOException ex) when (!attempt.IsCancellationRequested)
                {
                    // Обрыв соединения — повод для новой попытки. Ошибка диска ниже — нет.
                    throw new BrokenConnection(ex.Message, ex);
                }

                if (read == 0)
                {
                    return;
                }

                if (_received + read > _maxBytes)
                {
                    throw new IOException(Loc.Get("S.Updates.TooBig"));
                }

                await _file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                _hasher.AppendData(buffer, 0, read);
                Interlocked.Add(ref _received, read);
                Report();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Доля — раз в процент: иначе на девяноста мегабайтах интерфейс получил бы десятки тысяч
    /// обновлений подряд. После перезапуска с нуля доля честно идёт назад.
    /// </summary>
    private void Report()
    {
        if (_progress is null || _total <= 0)
        {
            return;
        }

        var share = Math.Min(1.0, (double)_received / _total);
        if (Math.Abs(share - _lastReported) >= 0.01)
        {
            _lastReported = share;
            _progress.Report(share);
        }
    }

    private enum AttemptEnd
    {
        Finished,

        /// <summary>Ни байта за <see cref="DownloadPolicy.Silence"/>.</summary>
        Silent,

        /// <summary>Меньше <see cref="DownloadPolicy.MinBytesPerWindow"/> за окно.</summary>
        Slow,

        /// <summary>Соединение оборвалось или сервер ответил отказом на докачку.</summary>
        Broken
    }

    /// <summary>Обрыв чтения из сети, в отличие от ошибки записи на диск.</summary>
    private sealed class BrokenConnection(string message, Exception inner) : Exception(message, inner);

    /// <summary>
    /// Сторож одной попытки: раз в секунду смотрит, сколько пришло, и обрывает попытку, если
    /// поток затих или идёт медленнее порога.
    /// </summary>
    private sealed class Watchdog : IDisposable
    {
        private readonly DownloadPolicy _policy;
        private readonly Func<long> _received;
        private readonly bool _checkSpeed;
        private readonly CancellationTokenSource _attempt;
        private readonly ITimer _timer;
        private DateTimeOffset _lastMove;
        private long _lastSeen;
        private DateTimeOffset _windowStart;
        private long _windowFrom;
        private int _verdict = -1;

        public Watchdog(DownloadPolicy policy, Func<long> received, bool checkSpeed, CancellationTokenSource attempt)
        {
            _policy = policy;
            _received = received;
            _checkSpeed = checkSpeed;
            _attempt = attempt;
            _lastMove = _windowStart = policy.Time.GetUtcNow();
            _lastSeen = _windowFrom = received();
            _timer = policy.Time.CreateTimer(_ => Check(), null, Tick, Tick);
        }

        /// <summary>Почему сторож оборвал попытку; null — не обрывал.</summary>
        public AttemptEnd? Verdict => Volatile.Read(ref _verdict) is var verdict and >= 0 ? (AttemptEnd)verdict : null;

        public void Dispose() => _timer.Dispose();

        private void Check()
        {
            var now = _policy.Time.GetUtcNow();
            var received = _received();
            if (received != _lastSeen)
            {
                _lastSeen = received;
                _lastMove = now;
            }

            if (now - _lastMove >= _policy.Silence)
            {
                Trip(AttemptEnd.Silent);
                return;
            }

            if (!_checkSpeed || now - _windowStart < _policy.Window)
            {
                return;
            }

            if (received - _windowFrom < _policy.MinBytesPerWindow)
            {
                Trip(AttemptEnd.Slow);
                return;
            }

            _windowStart = now;
            _windowFrom = received;
        }

        private void Trip(AttemptEnd verdict)
        {
            if (Interlocked.CompareExchange(ref _verdict, (int)verdict, -1) != -1)
            {
                return;
            }

            try
            {
                _attempt.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Попытка уже кончилась и прибрана.
            }
        }
    }
}
