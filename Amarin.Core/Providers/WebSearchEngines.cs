namespace Amarin.Core;

/// <summary>
/// Движок поиска у OpenRouter — готовое сочетание <c>engine</c> и <c>mode</c>.
/// </summary>
/// <remarks>
/// Двумя полями на проводе, но одной строкой в настройках: перебирать движки и режимы по
/// отдельности значит предлагать человеку два десятка сочетаний, из которых осмысленны шесть.
/// </remarks>
/// <param name="LabelKey">
/// Ключ подписи либо <c>null</c> — тогда подпись берётся из <paramref name="Name"/>: имена
/// движков не переводятся, это названия служб.
/// </param>
/// <param name="Price">
/// Цена за запрос по прайсу OpenRouter на день выпуска. Показывается человеку, потому что
/// разница между движками здесь десятикратная, — но она может измениться, и об этом говорит
/// подсказка строки. Пусто — цену называет не OpenRouter.
/// </param>
public sealed record WebSearchEngineOption(
    string? LabelKey,
    string Name,
    string? Engine,
    string? Mode,
    string Price)
{
    public string Label => LabelKey is null ? Name : Loc.Get(LabelKey);

    public bool Matches(string? engine, string? mode) =>
        string.Equals(Engine, Blank(engine), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Mode, Blank(mode), StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Движки поиска, между которыми выбирает человек.</summary>
/// <remarks>
/// Не весь список OpenRouter: у Exa шесть режимов, у Parallel четыре, и выложить их полным
/// перебором значило бы спросить человека о том, в чём он не разбирается. Оставлены крайние
/// точки по цене и качеству — от самого дешёвого до самого дотошного.
/// </remarks>
public static class WebSearchEngines
{
    public static readonly WebSearchEngineOption[] All =
    [
        new("S.Models.Auto", "Auto", null, null, ""),
        new(null, "Parallel Turbo", "parallel", "turbo", "$0.001"),
        new(null, "Perplexity", "perplexity", null, "$0.005"),
        new(null, "Exa", "exa", "auto", "$0.007"),
        new(null, "Exa Deep", "exa", "deep", "$0.012"),
        new("S.Search.Engine.Native", "Native", "native", null, "")
    ];

    /// <summary>
    /// Строка списка под сохранённый выбор. Неизвестное сочетание — «Авто»: его мог записать
    /// выпуск новее этого, и падать из-за незнакомого движка незачем.
    /// </summary>
    public static WebSearchEngineOption Match(string? engine, string? mode)
    {
        foreach (var option in All)
        {
            if (option.Matches(engine, mode))
            {
                return option;
            }
        }

        return All[0];
    }
}
