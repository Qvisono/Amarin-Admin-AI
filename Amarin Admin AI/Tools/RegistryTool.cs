using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Amarin.Tools;

public sealed class RegistryTool : ITool
{
    public string Name => "registry";
    public string Description =>
        "Read or write Windows Registry keys and values. " +
        "Supports HKLM, HKCU, HKCR, HKU, HKCC hive prefixes.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["read", "write", "delete_value", "delete_key", "list_subkeys"],
              "description": "Registry operation to perform"
            },
            "path": {
              "type": "string",
              "description": "Registry path like HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion"
            },
            "value_name": {
              "type": "string",
              "description": "Value name (omit or empty for default value)"
            },
            "value_type": {
              "type": "string",
              "enum": ["string", "expand_string", "dword", "qword", "binary", "multi_string"],
              "description": "Value type for write operations"
            },
            "value_data": {
              "type": "string",
              "description": "Value data for write. For dword/qword use decimal or 0x hex. For multi_string use JSON array."
            }
          },
          "required": ["action", "path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp) ||
                !arguments.TryGetProperty("path", out var pathProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameters: action, path"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            var path = pathProp.GetString() ?? string.Empty;

            if (!TryParseHive(path, out var hive, out var subKey))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"Invalid registry path. Use HKLM\\..., HKCU\\..., etc. Got: {path}"));
            }

            arguments.TryGetProperty("value_name", out var valueNameProp);
            var valueName = valueNameProp.ValueKind == JsonValueKind.String
                ? valueNameProp.GetString()
                : null;

            return action switch
            {
                "read" => Task.FromResult(ReadValue(hive, subKey, valueName)),
                "write" => Task.FromResult(WriteValue(hive, subKey, valueName, arguments)),
                "delete_value" => Task.FromResult(DeleteValue(hive, subKey, valueName)),
                "delete_key" => Task.FromResult(DeleteKey(hive, subKey)),
                "list_subkeys" => Task.FromResult(ListSubkeys(hive, subKey)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Registry error: {ex.Message}"));
        }
    }

    private static ToolResult ReadValue(RegistryKey hive, string subKey, string? valueName)
    {
        using var key = hive.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return ToolResult.Fail($"Registry key not found: {subKey}");
        }

        if (string.IsNullOrEmpty(valueName))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Key: {subKey}");
            sb.AppendLine("Values:");
            foreach (var name in key.GetValueNames())
            {
                var displayName = string.IsNullOrEmpty(name) ? "(default)" : name;
                sb.AppendLine($"  {displayName} = {FormatValue(key.GetValue(name), key.GetValueKind(name))}");
            }

            sb.AppendLine("Subkeys:");
            foreach (var child in key.GetSubKeyNames())
            {
                sb.AppendLine($"  {child}");
            }

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        var value = key.GetValue(valueName);
        if (value is null && !key.GetValueNames().Contains(valueName ?? string.Empty))
        {
            return ToolResult.Fail($"Value not found: {valueName}");
        }

        var kind = key.GetValueKind(valueName ?? string.Empty);
        return ToolResult.Ok($"{valueName} ({kind}) = {FormatValue(value, kind)}");
    }

    private static ToolResult WriteValue(RegistryKey hive, string subKey, string? valueName, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("value_type", out var typeProp) ||
            !arguments.TryGetProperty("value_data", out var dataProp))
        {
            return ToolResult.Fail("Write requires value_type and value_data");
        }

        using var key = hive.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot open or create key: {subKey}");

        var valueType = typeProp.GetString()?.ToLowerInvariant();
        var name = valueName ?? string.Empty;
        object? data = valueType switch
        {
            "string" => dataProp.GetString(),
            "expand_string" => dataProp.GetString(),
            "dword" => ParseInteger(dataProp.GetString()),
            "qword" => ParseLong(dataProp.GetString()),
            "binary" => Convert.FromHexString(dataProp.GetString() ?? string.Empty),
            "multi_string" => dataProp.ValueKind == JsonValueKind.Array
                ? dataProp.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : (dataProp.GetString() ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            _ => null
        };

        if (data is null)
        {
            return ToolResult.Fail($"Unsupported or missing value_type: {valueType}");
        }

        var regKind = valueType switch
        {
            "string" => RegistryValueKind.String,
            "expand_string" => RegistryValueKind.ExpandString,
            "dword" => RegistryValueKind.DWord,
            "qword" => RegistryValueKind.QWord,
            "binary" => RegistryValueKind.Binary,
            "multi_string" => RegistryValueKind.MultiString,
            _ => RegistryValueKind.Unknown
        };

        key.SetValue(name, data, regKind);
        return ToolResult.Ok($"Written {name} ({regKind}) to {subKey}");
    }

    private static ToolResult DeleteValue(RegistryKey hive, string subKey, string? valueName)
    {
        using var key = hive.OpenSubKey(subKey, writable: true);
        if (key is null)
        {
            return ToolResult.Fail($"Registry key not found: {subKey}");
        }

        key.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
        return ToolResult.Ok($"Deleted value '{valueName ?? "(default)"}' from {subKey}");
    }

    private static ToolResult DeleteKey(RegistryKey hive, string subKey)
    {
        var lastSlash = subKey.LastIndexOf('\\');
        if (lastSlash < 0)
        {
            return ToolResult.Fail("Cannot delete root hive key.");
        }

        var parentPath = subKey[..lastSlash];
        var childName = subKey[(lastSlash + 1)..];

        using var parent = hive.OpenSubKey(parentPath, writable: true);
        if (parent is null)
        {
            return ToolResult.Fail($"Parent key not found: {parentPath}");
        }

        parent.DeleteSubKeyTree(childName, throwOnMissingSubKey: false);
        return ToolResult.Ok($"Deleted key tree: {subKey}");
    }

    private static ToolResult ListSubkeys(RegistryKey hive, string subKey)
    {
        using var key = hive.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return ToolResult.Fail($"Registry key not found: {subKey}");
        }

        var names = key.GetSubKeyNames();
        return ToolResult.Ok(names.Length == 0
            ? "No subkeys."
            : string.Join(Environment.NewLine, names));
    }

    private static bool TryParseHive(string path, out RegistryKey hive, out string subKey)
    {
        hive = Registry.LocalMachine;
        subKey = string.Empty;

        var separator = path.IndexOf('\\');
        if (separator <= 0)
        {
            return false;
        }

        var hiveName = path[..separator].ToUpperInvariant();
        subKey = path[(separator + 1)..];

        hive = hiveName switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKU" or "HKEY_USERS" => Registry.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => Registry.LocalMachine
        };

        return hiveName is "HKLM" or "HKEY_LOCAL_MACHINE"
            or "HKCU" or "HKEY_CURRENT_USER"
            or "HKCR" or "HKEY_CLASSES_ROOT"
            or "HKU" or "HKEY_USERS"
            or "HKCC" or "HKEY_CURRENT_CONFIG";
    }

    private static int ParseInteger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(text, 16)
            : int.Parse(text);
    }

    private static long ParseLong(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(text, 16)
            : long.Parse(text);
    }

    private static string FormatValue(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => value is byte[] bytes
            ? BitConverter.ToString(bytes)
            : value?.ToString() ?? "(null)",
        RegistryValueKind.MultiString => value is string[] arr
            ? string.Join(" | ", arr)
            : value?.ToString() ?? "(null)",
        _ => value?.ToString() ?? "(null)"
    };
}