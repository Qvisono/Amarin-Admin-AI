using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class CredentialsTool : ITool
{
    public string Name => "credentials";
    public string Description =>
        "Read-only credential and certificate inventory: Windows Credential Manager (cmdkey) and certificate stores.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_cmdkey", "list_certs", "expiring_certs"],
              "description": "Credentials diagnostic action"
            },
            "store": {
              "type": "string",
              "enum": ["My", "Root", "CA", "TrustedPublisher", "All"],
              "description": "Certificate store for list_certs (default My)"
            },
            "days": {
              "type": "integer",
              "description": "Days ahead for expiring_certs (default 30, max 365)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("action", out var actionProp))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
        }

        var store = arguments.TryGetProperty("store", out var storeProp) &&
                    storeProp.ValueKind == JsonValueKind.String
            ? storeProp.GetString() ?? "My"
            : "My";

        var days = GetInt(arguments, "days", 30, 1, 365);

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "list_cmdkey" => "cmdkey /list 2>&1",
            "list_certs" => ListCertsScript(store),
            "expiring_certs" => ExpiringCertsScript(days),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 120));
    }

    private static string ListCertsScript(string store) => store.Equals("All", StringComparison.OrdinalIgnoreCase)
        ? """
            $stores = 'My','Root','CA','TrustedPublisher','TrustedPeople','AuthRoot'
            foreach ($s in $stores) {
              "--- $s ---"
              Get-ChildItem "Cert:\LocalMachine\$s" -ErrorAction SilentlyContinue |
                Select-Object Subject, Issuer, NotAfter, Thumbprint |
                Format-Table -Wrap -AutoSize
              Get-ChildItem "Cert:\CurrentUser\$s" -ErrorAction SilentlyContinue |
                Select-Object Subject, Issuer, NotAfter, Thumbprint |
                Format-Table -Wrap -AutoSize
            }
            """
        : "'LocalMachine\\" + store + "'\n" +
          "Get-ChildItem \"Cert:\\LocalMachine\\" + store + "\" -ErrorAction SilentlyContinue |\n" +
          "  Select-Object Subject, Issuer, NotAfter, Thumbprint | Format-Table -Wrap -AutoSize\n" +
          "'CurrentUser\\" + store + "'\n" +
          "Get-ChildItem \"Cert:\\CurrentUser\\" + store + "\" -ErrorAction SilentlyContinue |\n" +
          "  Select-Object Subject, Issuer, NotAfter, Thumbprint | Format-Table -Wrap -AutoSize";

    private static string ExpiringCertsScript(int days) =>
        "$deadline = (Get-Date).AddDays(" + days + ")\n" +
        "$paths = @('Cert:\\LocalMachine\\My','Cert:\\CurrentUser\\My','Cert:\\LocalMachine\\Root')\n" +
        "foreach ($p in $paths) {\n" +
        "  \"--- $p ---\"\n" +
        "  Get-ChildItem $p -ErrorAction SilentlyContinue |\n" +
        "    Where-Object { $_.NotAfter -le $deadline } |\n" +
        "    Select-Object Subject, NotAfter, @{N='DaysLeft';E={[math]::Round(($_.NotAfter - (Get-Date)).TotalDays,0)}}, Thumbprint |\n" +
        "    Sort-Object NotAfter | Format-Table -Wrap -AutoSize\n" +
        "}";

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}