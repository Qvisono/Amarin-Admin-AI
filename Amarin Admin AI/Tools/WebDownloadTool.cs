using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

public sealed class WebDownloadTool : ITool
{
    private const int CopyBufferSize = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly DownloadOptions _options;

    public WebDownloadTool(HttpClient http, DownloadOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => "download_file";
    public string Description =>
        "Download a file over http(s) from a host on the user's download allowlist. " +
        "Saves to Downloads or Desktop. Filename is taken from the URL path as-is (not renamed). " +
        "The allowlist is enforced by the app: a host outside it makes the app ask the user whether to " +
        "add it, and the download simply proceeds when they agree. If the result still starts with " +
        "'DOMAIN_BLOCKED:', the user refused — STOP. Do not retry, do not try mirrors, proxies or " +
        "another URL. Just tell the user the domain stayed blocked.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "URL to download (HTTPS recommended)"
            },
            "destination": {
              "type": "string",
              "description": "Optional. Only when URL has no filename — bare name or full path under Downloads/Desktop."
            },
            "folder": {
              "type": "string",
              "enum": ["downloads", "desktop"],
              "description": "Target folder for bare filename. Default: downloads"
            },
            "expected_sha256": {
              "type": "string",
              "description": "Optional expected SHA-256 hash (hex)"
            },
            "max_size_mb": {
              "type": "integer",
              "description": "Optional per-download size limit in MB"
            }
          },
          "required": ["url"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("url", out var urlProp))
        {
            return ToolResult.Fail("Missing required parameter: url");
        }

        var urlText = urlProp.GetString();
        var destinationInput = arguments.TryGetProperty("destination", out var destProp) &&
                               destProp.ValueKind == JsonValueKind.String
            ? destProp.GetString()
            : null;
        var folder = arguments.TryGetProperty("folder", out var folderProp) &&
                     folderProp.ValueKind == JsonValueKind.String
            ? folderProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(urlText))
        {
            return ToolResult.Fail("URL cannot be empty");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
        {
            return ToolResult.Fail("Invalid URL");
        }

        if (!DownloadValidator.TryValidateUrl(uri, out var urlError))
        {
            return ToolResult.Fail(urlError);
        }

        if (!DownloadValidator.IsDomainAllowed(uri))
        {
            // Приложение спрашивает пользователя, добавить ли домен. Согласился — список уже
            // обновлён, и загрузка идёт дальше как обычная; отказался — остаётся прежний отказ.
            var allowed = await DownloadAccessBroker.RequestAsync(uri.Host, cancellationToken)
                .ConfigureAwait(false);

            if (!allowed || !DownloadValidator.IsDomainAllowed(uri))
            {
                return ToolResult.Fail(
                    $"{DomainList.BlockedMarker} {uri.Host} не в белом списке загрузок. " +
                    "Пользователю уже показали запрос на добавление домена, и он его отклонил. " +
                    "Не повторяй попытку, не ищи зеркала и не пробуй другой URL — просто скажи, " +
                    "что домен остался заблокированным.");
            }
        }

        if (!DownloadPaths.TryResolveDestination(destinationInput, folder, uri, out var destination, out var pathError))
        {
            return ToolResult.Fail(pathError!);
        }

        var maxBytes = _options.MaxSizeBytes;
        if (arguments.TryGetProperty("max_size_mb", out var maxProp) && maxProp.TryGetInt32(out var maxMb))
        {
            maxBytes = Math.Min(maxBytes, maxMb * 1024L * 1024L);
        }

        var expectedHash = arguments.TryGetProperty("expected_sha256", out var hashProp) &&
                           hashProp.ValueKind == JsonValueKind.String
            ? hashProp.GetString()?.Trim().ToLowerInvariant()
            : null;
        var verifyHash = !string.IsNullOrWhiteSpace(expectedHash);

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Fail($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxBytes)
            {
                return ToolResult.Fail($"File too large: {contentLength} bytes (limit {maxBytes}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(destination);
            using var hasher = verifyHash ? SHA256.Create() : null;

            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            long total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        await file.DisposeAsync();
                        File.Delete(destination);
                        return ToolResult.Fail($"Download exceeded size limit ({maxBytes} bytes).");
                    }

                    hasher?.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            hasher?.TransformFinalBlock([], 0, 0);
            var actualHash = hasher is null
                ? null
                : Convert.ToHexString(hasher.Hash!).ToLowerInvariant();

            if (verifyHash && !actualHash!.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                return ToolResult.Fail(
                    $"SHA-256 mismatch. Expected {expectedHash}, got {actualHash}. File deleted.");
            }

            var saved = new SavedFile(destination, Path.GetFileName(destination), total);
            return hasher is null
                ? ToolResult.WithFile($"Downloaded {total} bytes to {destination}", saved)
                : ToolResult.WithFile(
                    $"Downloaded {total} bytes to {destination}\nSHA-256: {actualHash}", saved);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Download error: {ex.Message}");
        }
    }
}