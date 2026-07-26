using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

public static class VeniceSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static void SaveModel(string model)
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

        if (root["Venice"] is not JsonObject venice)
        {
            venice = new JsonObject();
            root["Venice"] = venice;
        }

        venice["Model"] = model;
        // Never persist API keys in appsettings.json (env / user-secrets only).
        venice.Remove("ApiKey");

        File.WriteAllText(SettingsPath, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }
}