using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Как программа говорит с сервером MCP.</summary>
public enum McpTransportKind
{
    /// <summary>Локальный процесс: JSON-RPC строками через stdin/stdout.</summary>
    Stdio,

    /// <summary>Streamable HTTP: POST, ответ JSON или поток SSE.</summary>
    Http
}

/// <summary>Сервер MCP из «Подключений».</summary>
public sealed class McpServerConfig
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;

    public string Command { get; set; } = "";

    public List<string> Arguments { get; set; } = [];

    /// <summary>Переменные окружения процесса: имя → значение под DPAPI (там обычно токены).</summary>
    public Dictionary<string, string> ProtectedEnvironment { get; set; } = [];

    public string Url { get; set; } = "";

    /// <summary>Значение заголовка Authorization под DPAPI; null — без него.</summary>
    public string? ProtectedAuthorization { get; set; }

    /// <summary>Инструменты, которые человек отметил «только чтение», — их не спрашивают.</summary>
    public List<string> ReadOnlyTools { get; set; } = [];
}

/// <summary>Инструмент, который объявил сервер.</summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema);

/// <summary>Ошибка разговора с сервером — текстом, который можно показать человеку.</summary>
public sealed class McpException(string message) : Exception(message);

/// <summary>Способ доставки сообщений JSON-RPC.</summary>
internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Отправляет запрос и ждёт ответ с тем же id.</summary>
    Task<JsonElement> RequestAsync(JsonObject message, int id, CancellationToken cancellationToken);

    /// <summary>Отправляет уведомление — ответа на него не бывает.</summary>
    Task NotifyAsync(JsonObject message, CancellationToken cancellationToken);

    /// <summary>Согласованная версия протокола — HTTP-транспорт шлёт её заголовком.</summary>
    string? ProtocolVersion { get; set; }
}

/// <summary>
/// Разговор с одним сервером MCP: <c>initialize</c> → <c>notifications/initialized</c> →
/// <c>tools/list</c> → <c>tools/call</c>. Свой клиент, без SDK: он добавил бы к exe больше, чем
/// весь этот файл, а нужны нам четыре метода.
/// </summary>
internal sealed class McpSession : IAsyncDisposable
{
    /// <summary>Версии, которые мы знаем; первая — та, что предлагаем.</summary>
    internal static readonly string[] KnownVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly IMcpTransport _transport;
    private int _nextId;

    public McpSession(IMcpTransport transport) => _transport = transport;

    public string? ServerName { get; private set; }

    public string? ProtocolVersion => _transport.ProtocolVersion;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = KnownVersions[0],
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Amarin Admin AI", ["version"] = typeof(McpSession).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" }
        }, cancellationToken).ConfigureAwait(false);

        // Сервер отвечает своей версией. Незнакомую тоже принимаем: четыре метода, которые нам
        // нужны, от версии к версии не менялись, а отказ оставил бы человека без сервера.
        _transport.ProtocolVersion = result.TryGetProperty("protocolVersion", out var version) &&
                                     version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : KnownVersions[0];
        ServerName = result.TryGetProperty("serverInfo", out var info) && info.TryGetProperty("name", out var name) &&
                     name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

        await _transport.NotifyAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var result = await CallAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
            tools.AddRange(McpProtocol.ParseTools(result));
            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return tools;
    }

    public async Task<ToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var result = await CallAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Object ? JsonNode.Parse(arguments.GetRawText()) : new JsonObject()
        }, cancellationToken).ConfigureAwait(false);
        return McpProtocol.ToToolResult(result);
    }

    private async Task<JsonElement> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var response = await _transport.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }, id, cancellationToken).ConfigureAwait(false);
        return McpProtocol.ResultOf(response);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>Разбор ответов JSON-RPC и MCP — чистые функции, их проверяют тесты без сервера.</summary>
internal static partial class McpProtocol
{
    /// <summary>Сколько текста ответа отдавать модели.</summary>
    public const int ResultLimit = 20_000;

    public static JsonElement ResultOf(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : error.GetRawText();
            throw new McpException(message ?? "error");
        }

        return response.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    public static IEnumerable<McpToolInfo> ParseTools(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } toolName)
            {
                var description = tool.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? ""
                    : "";
                var schema = tool.TryGetProperty("inputSchema", out var input) && input.ValueKind == JsonValueKind.Object
                    ? input.Clone()
                    : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
                yield return new McpToolInfo(toolName, description, schema);
            }
        }
    }

    /// <summary>Текстовые части ответа — одной строкой; картинки и ресурсы — пометкой, что они были.</summary>
    public static ToolResult ToToolResult(JsonElement result)
    {
        var text = new StringBuilder();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var value))
                {
                    text.AppendLine(value.GetString());
                }
                else if (type is not null)
                {
                    text.AppendLine($"[{type} content omitted]");
                }
            }
        }

        if (text.Length == 0 && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("structuredContent", out var structured))
        {
            text.Append(structured.GetRawText());
        }

        var output = text.ToString().TrimEnd();
        if (output.Length > ResultLimit)
        {
            output = output[..ResultLimit] + "\n…";
        }

        var failed = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("isError", out var isError) &&
                     isError.ValueKind == JsonValueKind.True;
        return failed ? ToolResult.Fail(output) : ToolResult.Ok(output);
    }

    /// <summary>
    /// Имя инструмента для модели: <c>сервер__инструмент</c>, только <c>[A-Za-z0-9_-]</c> и не длиннее
    /// 64 — так требуют провайдеры, иначе запрос отвергается целиком.
    /// </summary>
    public static string ToolName(string server, string tool)
    {
        var name = Sanitize(server) + McpNames.Separator + Sanitize(tool);
        return name.Length <= 64 ? name : name[..64];
    }

    private static string Sanitize(string value)
    {
        var clean = Unsafe().Replace(value.Trim(), "_").Trim('_');
        return clean.Length == 0 ? "x" : clean.Length > 30 ? clean[..30] : clean;
    }

    /// <summary>
    /// Разбор тела ответа HTTP: JSON целиком или поток SSE, из которого берётся ответ с нашим id.
    /// </summary>
    public static JsonElement? FindResponse(string body, bool eventStream, int id)
    {
        if (!eventStream)
        {
            return Match(body, id);
        }

        var data = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart()).Append('\n');
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (Match(data.ToString(), id) is { } found)
                {
                    return found;
                }

                data.Clear();
            }
        }

        return data.Length > 0 ? Match(data.ToString(), id) : null;
    }

    private static JsonElement? Match(string json, int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var message in doc.RootElement.ValueKind == JsonValueKind.Array
                         ? doc.RootElement.EnumerateArray().ToArray()
                         : [doc.RootElement])
            {
                if (message.TryGetProperty("id", out var messageId) && messageId.ValueKind == JsonValueKind.Number &&
                    messageId.GetInt32() == id && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
                {
                    return message.Clone();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex Unsafe();
}

internal static class McpNames
{
    public const string Separator = "__";

    public static bool IsMcp(string tool) => tool.Contains(Separator, StringComparison.Ordinal);
}

/// <summary>Процесс-сервер: строки JSON через stdin/stdout.</summary>
internal sealed class McpStdioTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _reader;
    private readonly StringBuilder _stderr = new();

    public string? ProtocolVersion { get; set; }

    private McpStdioTransport(Process process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
        _ = Task.Run(ReadErrorsAsync);
    }

    public static McpStdioTransport Start(string command, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new McpException(Loc.Get("S.Mcp.StartFailed"));
        return new McpStdioTransport(process);
    }

    public async Task<JsonElement> RequestAsync(JsonObject message, int id, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(JsonObject message, CancellationToken cancellationToken) => WriteAsync(message, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            throw new McpException(Loc.Format("S.Mcp.Exited", LastErrors()));
        }

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Одна строка на сообщение: перевод строки внутри сломал бы разбор на той стороне.
            await _process.StandardInput.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    message = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Сервер пишет в stdout не только JSON (так бывает с журналами) — пропускаем.
                    continue;
                }

                await HandleAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new McpException(Loc.Format("S.Mcp.Exited", LastErrors())));
        }
    }

    private async Task HandleAsync(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id);
        if (hasId && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
        {
            if (id.ValueKind == JsonValueKind.Number && _pending.TryGetValue(id.GetInt32(), out var completion))
            {
                completion.TrySetResult(message);
            }

            return;
        }

        // Встречный запрос сервера (ping, sampling, roots): ping отвечаем, остальное не умеем.
        if (hasId && message.TryGetProperty("method", out var method))
        {
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
            if (method.GetString() == "ping")
            {
                reply["result"] = new JsonObject();
            }
            else
            {
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" };
            }

            try
            {
                await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
            }
            catch (McpException)
            {
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 4000)
                    {
                        _stderr.Remove(0, _stderr.Length - 4000);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private string LastErrors()
    {
        lock (_stderr)
        {
            var text = _stderr.ToString().Trim();
            return text.Length == 0 ? "—" : text.Length > 400 ? text[^400..] : text;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                // Дерево целиком: npx и uvx запускают сервер дочерним процессом.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }

        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _process.Dispose();
        _write.Dispose();
    }
}

/// <summary>Streamable HTTP: каждое сообщение — POST, ответ JSON или SSE, сессия — заголовком.</summary>
internal sealed class McpHttpTransport(HttpClient http, Uri endpoint, string? authorization) : IMcpTransport
{
    private string? _sessionId;

    public string? ProtocolVersion { get; set; }

    public async Task<JsonElement> RequestAsync(JsonObject message, int id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(Loc.Format("S.Mcp.HttpFailed", (int)response.StatusCode, Trim(body)));
        }

        var eventStream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
        return McpProtocol.FindResponse(body, eventStream, id) ?? throw new McpException(Loc.Get("S.Mcp.NoResponse"));
    }

    public async Task NotifyAsync(JsonObject message, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        if (ProtocolVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        }

        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values) && values.FirstOrDefault() is { Length: > 0 } session)
        {
            _sessionId = session;
        }

        return response;
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Инструмент сервера MCP для агента: вызов уходит на сервер, ответ — пометкой «данные».</summary>
internal sealed class McpToolAdapter(string server, McpToolInfo tool, Func<CancellationToken, Task<McpSession>> session) : ITool
{
    public string Name { get; } = McpProtocol.ToolName(server, tool.Name);

    public string Description { get; } = $"[MCP: {server}] {tool.Description}".Trim();

    public JsonElement ParametersSchema { get; } = tool.InputSchema;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var connected = await session(cancellationToken).ConfigureAwait(false);
            var result = await connected.CallToolAsync(tool.Name, arguments, cancellationToken).ConfigureAwait(false);

            // Текст с чужого сервера — данные, а не указания: тот же агент вызывает PowerShell.
            var marked = Loc.Format("S.Mcp.DataNotice", server) + "\n" + result.Output;
            return result.Success ? ToolResult.Ok(marked) : ToolResult.Fail(marked);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or TaskCanceledException)
        {
            return ToolResult.Fail(Loc.Format("S.Mcp.CallFailed", server, ex.Message));
        }
    }
}

/// <summary>
/// Серверы профиля: <c>mcp.json</c>, живые сессии и инструменты для агента.
/// </summary>
/// <remarks>
/// Сессия поднимается при первом вызове и живёт до выключения сервера, смены профиля или выхода.
/// Список инструментов кэшируется после «Проверить связь» в самом файле: агенту не нужно
/// запускать все серверы, чтобы собрать свои инструменты.
/// </remarks>
internal sealed class McpHost : IAsyncDisposable
{
    internal const string FileName = "mcp.json";

    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<McpSession>> _sessions = [];
    private string _root;

    public McpHost(string root, HttpClient http)
    {
        _root = root;
        _http = http;
    }

    /// <summary>Инструменты серверов, как их объявили при последней проверке: id сервера → список.</summary>
    public sealed class Catalog
    {
        public List<McpServerConfig> Servers { get; set; } = [];

        public Dictionary<string, List<CachedTool>> Tools { get; set; } = [];
    }

    public sealed record CachedTool(string Name, string Description, JsonElement InputSchema);

    public void UseRoot(string root)
    {
        StopAll();
        lock (_gate)
        {
            _root = root;
        }
    }

    public Catalog Load()
    {
        string path;
        lock (_gate)
        {
            path = Path.Combine(_root, FileName);
        }

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), AppJson.Options) ?? new Catalog()
                : new Catalog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Catalog();
        }
    }

    public bool Save(Catalog catalog)
    {
        try
        {
            lock (_gate)
            {
                AppDataFile.WriteAtomic(Path.Combine(_root, FileName), JsonSerializer.Serialize(catalog, AppJson.Options));
            }

            ReadOnlyNames = BuildReadOnly(catalog);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Имена инструментов для модели, отмеченные «только чтение».</summary>
    public IReadOnlySet<string> ReadOnlyNames { get; private set; } = new HashSet<string>();

    private static HashSet<string> BuildReadOnly(Catalog catalog) =>
        catalog.Servers
            .SelectMany(server => server.ReadOnlyTools.Select(tool => McpProtocol.ToolName(server.Name, tool)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Перечитывает файл — после смены профиля и при запуске.</summary>
    public void Refresh() => ReadOnlyNames = BuildReadOnly(Load());

    /// <summary>Инструменты включённых серверов для агента — по кэшу, без запуска серверов.</summary>
    public IReadOnlyList<ITool> Tools()
    {
        var catalog = Load();
        var tools = new List<ITool>();
        foreach (var server in catalog.Servers.Where(server => server.Enabled))
        {
            if (!catalog.Tools.TryGetValue(server.Id, out var cached))
            {
                continue;
            }

            var config = server;
            tools.AddRange(cached.Select(tool => new McpToolAdapter(
                server.Name,
                new McpToolInfo(tool.Name, tool.Description, tool.InputSchema),
                token => SessionFor(config, token))));
        }

        return tools;
    }

    /// <summary>«Проверить связь»: поднимает сессию заново и записывает список инструментов.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ProbeAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        await StopAsync(server.Id).ConfigureAwait(false);
        var session = await SessionFor(server, cancellationToken).ConfigureAwait(false);
        var tools = await session.ListToolsAsync(cancellationToken).ConfigureAwait(false);

        var catalog = Load();
        catalog.Tools[server.Id] = tools.Select(tool => new CachedTool(tool.Name, tool.Description, tool.InputSchema)).ToList();
        Save(catalog);
        return tools;
    }

    private Task<McpSession> SessionFor(McpServerConfig server, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(server.Id, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
            {
                return existing;
            }

            var started = OpenAsync(server, cancellationToken);
            _sessions[server.Id] = started;
            return started;
        }
    }

    internal async Task<McpSession> OpenAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        IMcpTransport transport = server.Transport == McpTransportKind.Http
            ? new McpHttpTransport(_http, new Uri(server.Url), DataProtector.Unprotect(server.ProtectedAuthorization))
            : McpStdioTransport.Start(server.Command, server.Arguments, Environment(server));

        var session = new McpSession(transport);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await session.InitializeAsync(timeout.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Dictionary<string, string> Environment(McpServerConfig server)
    {
        // Значение, которое не расшифровалось (ключ DPAPI другого пользователя), не передаётся вовсе.
        var environment = new Dictionary<string, string>();
        foreach (var (name, sealedValue) in server.ProtectedEnvironment)
        {
            if (DataProtector.Unprotect(sealedValue) is { } value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    public async Task StopAsync(string serverId)
    {
        Task<McpSession>? session;
        lock (_gate)
        {
            _sessions.Remove(serverId, out session);
        }

        if (session is { IsCompletedSuccessfully: true })
        {
            await session.Result.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void StopAll()
    {
        List<Task<McpSession>> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions.Where(session => session.IsCompletedSuccessfully))
        {
            session.Result.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }

    public ValueTask DisposeAsync()
    {
        StopAll();
        return ValueTask.CompletedTask;
    }
}
