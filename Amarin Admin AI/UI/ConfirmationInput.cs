namespace Amarin.UI;

internal static class ConfirmationInput
{
    public static bool? TryParse(string? input)
    {
        if (input is null)
        {
            return null;
        }

        input = input.Trim();
        if (input.Length == 0)
        {
            return false;
        }

        input = input.ToLowerInvariant();
        return input switch
        {
            "1" or "да" or "yes" or "y" or "д" => true,
            "2" or "нет" or "no" or "n" or "н" => false,
            _ => null
        };
    }
}