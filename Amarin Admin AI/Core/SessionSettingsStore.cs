namespace Amarin.Core;

public static class SessionSettingsStore
{
    public static bool TryLoad(out SessionMode mode)
    {
        var store = new AppSettingsStore();
        if (store.Exists)
        {
            mode = store.Load().SessionMode;
            return true;
        }

        mode = SessionMode.Continuous;

        if (!File.Exists(ExeSettingsPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(ExeSettingsPath);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Session", out var session) ||
                !session.TryGetProperty("Mode", out var modeElement))
            {
                return false;
            }

            if (!SessionModeParser.TryParse(modeElement.GetString(), out mode))
            {
                return false;
            }

            Save(mode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(SessionMode mode)
    {
        new AppSettingsStore().Update(settings => settings.SessionMode = mode);
    }

    private static string ExeSettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}
