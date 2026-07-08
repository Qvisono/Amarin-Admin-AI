using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

public static class SessionSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static bool TryLoad(out SessionMode mode)
    {
        mode = SessionMode.Continuous;

        if (!File.Exists(SettingsPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(SettingsPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Session", out var session) ||
                !session.TryGetProperty("Mode", out var modeElement))
            {
                return false;
            }

            return SessionModeParser.TryParse(modeElement.GetString(), out mode);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(SessionMode mode)
    {
        JsonObject root;

        if (File.Exists(SettingsPath))
        {
            var text = File.ReadAllText(SettingsPath);
            root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["Session"] is not JsonObject session)
        {
            session = new JsonObject();
            root["Session"] = session;
        }

        session["Mode"] = SessionModeParser.ToConfigValue(mode);

        File.WriteAllText(SettingsPath, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }
}