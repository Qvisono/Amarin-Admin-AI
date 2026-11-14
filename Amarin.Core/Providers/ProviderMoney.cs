namespace Amarin.Core;

/// <summary>Перевод цены провайдера в валюту общего журнала трат.</summary>
public static class ProviderMoney
{
    /// <summary>Доллары по заданному человеком числу рублей за доллар; без курса цены в долларах нет.</summary>
    public static decimal? ToUsd(decimal rubles, decimal? rate)
    {
        if (rubles < 0m || rate is not > 0m)
        {
            return null;
        }

        try
        {
            return rubles / rate.Value;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
