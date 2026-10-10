using System.Security.Cryptography;

namespace Amarin.Core;

/// <summary>
/// Свой сервер человека — перепродавец или собственный шлюз, заданный адресом API.
/// </summary>
/// <remarks>
/// Лежит в <c>provider-keys.json</c> рядом с ключами к нему. Отдельной записью, а не полями
/// ключа: у одного сервера бывает несколько ключей (разные пакеты токенов), и адрес, сменённый
/// человеком, должен смениться у всех сразу. Идентификатор выдаёт программа и не меняет:
/// он сидит в идентификаторах моделей слотов (<c>custom-3f2a9c:claude-sonnet-5-5</c>), и смена
/// адреса не должна отвязывать слоты.
/// </remarks>
public sealed class CustomEndpoint
{
    public string Id { get; set; } = NewId();

    /// <summary>Как сервер называется у человека. По умолчанию — домен адреса.</summary>
    public string Name { get; set; } = "";

    /// <summary>Корень API — с <c>/v1</c>, если сервер живёт под ним.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Понимает ли сервер OpenAI-совместимый <c>/chat/completions</c>.</summary>
    public bool OpenAiChat { get; set; } = true;

    /// <summary>Понимает ли сервер <c>/messages</c> Anthropic.</summary>
    public bool AnthropicMessages { get; set; }

    /// <summary>Провайдер, которым сервер числится в программе.</summary>
    public LlmProvider Provider => LlmProvider.Custom(Id);

    public ProviderEndpoint ToEndpoint() => new(Name, BaseUrl, OpenAiChat, AnthropicMessages);

    /// <summary>
    /// Шесть шестнадцатеричных знаков: коротко для идентификатора модели и без двоеточий,
    /// по которым он разбирается.
    /// </summary>
    internal static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
}

/// <summary>
/// Адрес и протоколы своего сервера — то, что едет вместе с ключом в каждый запрос.
/// </summary>
/// <remarks>
/// Запись, а не класс: учётные данные сравниваются по значению (смена набора ключей у
/// <see cref="ApiKeyProvider"/>, «тот ли это выбранный ключ»), и два одинаковых адреса обязаны
/// быть равны.
/// </remarks>
public sealed record ProviderEndpoint(string Name, string BaseUrl, bool OpenAiChat, bool AnthropicMessages)
{
    /// <summary>
    /// Идёт ли эта модель протоколом Anthropic. Модели Claude — да, если сервер его понимает:
    /// там подписанное размышление и родные инструменты. Остальные — OpenAI-совместимым путём,
    /// если он есть.
    /// </summary>
    public bool UsesAnthropicFor(string bareModel) =>
        AnthropicMessages &&
        (!OpenAiChat || bareModel.StartsWith("claude", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Проверка и приведение адреса своего сервера.</summary>
internal static class EndpointAddress
{
    /// <summary>
    /// Адрес, который можно предъявлять ключом, или <c>null</c> с причиной.
    /// </summary>
    /// <remarks>
    /// Только https, кроме этого же компьютера: ключ по открытому каналу к чужому серверу
    /// читается любым посредником. Локальный шлюз (Ollama, LM Studio) живёт на http.
    /// </remarks>
    public static string? Normalize(string? text, out string? problem)
    {
        problem = null;
        var value = (text ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            problem = "empty";
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            problem = "invalid";
            return null;
        }

        if (uri.Scheme == "http" && !IsLocal(uri))
        {
            problem = "insecure";
            return null;
        }

        // Запрос и фрагмент в адресе API не нужны, а в него дописываются пути запросов.
        var clean = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return clean;
    }

    /// <summary>Домен адреса — название сервера по умолчанию.</summary>
    public static string HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

    /// <summary>
    /// Адреса, по которым стоит попробовать корень API: как дан и с <c>/v1</c>, если его нет.
    /// </summary>
    /// <remarks>
    /// Перепродавцы пишут на сайте адрес то с <c>/v1</c>, то без, а запросы живут под ним
    /// (у ai.starimg.ru <c>/models</c> — 404, <c>/v1/models</c> — есть).
    /// </remarks>
    public static IReadOnlyList<string> Candidates(string baseUrl) =>
        baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? [baseUrl]
            : [baseUrl + "/v1", baseUrl];

    private static bool IsLocal(Uri uri) =>
        uri.IsLoopback ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
