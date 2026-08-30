namespace Amarin.Core;

public static class VeniceSettingsStore
{
    public static void SaveModel(string model)
    {
        new AppSettingsStore().Update(settings => settings.ChatModelId = model);
    }
}
