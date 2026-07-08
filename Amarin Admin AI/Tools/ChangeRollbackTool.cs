using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ChangeRollbackTool : ITool
{
    public string Name => "change_rollback";
    public string Description =>
        "Snapshot system state before changes and restore services/tasks/registry/startup later. " +
        "Actions: snapshot, list_snapshots, snapshot_info, compare, restore.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["snapshot", "list_snapshots", "snapshot_info", "compare", "restore"],
              "description": "Rollback operation"
            },
            "snapshot_id": {
              "type": "string",
              "description": "Snapshot folder name (timestamp id)"
            },
            "label": {
              "type": "string",
              "description": "Optional label for new snapshot"
            },
            "include_registry_paths": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Extra registry paths to export (e.g. HKLM\\SOFTWARE\\MyApp)"
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

        Directory.CreateDirectory(ChangeRollbackStore.Root);

        var action = actionProp.GetString()?.ToLowerInvariant();
        try
        {
            return action switch
            {
                "snapshot" => Task.FromResult(CreateSnapshot(arguments)),
                "list_snapshots" => Task.FromResult(ChangeRollbackOperations.ListSnapshots()),
                "snapshot_info" => Task.FromResult(SnapshotInfo(arguments)),
                "compare" => Task.FromResult(CompareSnapshot(arguments)),
                "restore" => Task.FromResult(RestoreSnapshot(arguments)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Rollback error: {ex.Message}"));
        }
    }

    private static ToolResult CreateSnapshot(JsonElement arguments)
    {
        var label = arguments.TryGetProperty("label", out var labelProp) &&
                    labelProp.ValueKind == JsonValueKind.String
            ? labelProp.GetString() ?? ""
            : "";

        var result = ChangeRollbackOperations.CreateSnapshot(
            label,
            ChangeRollbackOperations.ParseExtraRegistryPaths(arguments));

        return result.Success
            ? ToolResult.Ok(result.Message)
            : ToolResult.Fail(result.Message);
    }

    private static ToolResult SnapshotInfo(JsonElement arguments)
    {
        if (!TryGetSnapshotId(arguments, out var snapshotId, out var error))
        {
            return ToolResult.Fail(error!);
        }

        return ChangeRollbackOperations.SnapshotInfo(snapshotId!);
    }

    private static ToolResult CompareSnapshot(JsonElement arguments)
    {
        if (!TryGetSnapshotId(arguments, out var snapshotId, out var error))
        {
            return ToolResult.Fail(error!);
        }

        return ChangeRollbackOperations.CompareSnapshot(snapshotId!);
    }

    private static ToolResult RestoreSnapshot(JsonElement arguments)
    {
        if (!TryGetSnapshotId(arguments, out var snapshotId, out var error))
        {
            return ToolResult.Fail(error!);
        }

        return ChangeRollbackOperations.RestoreSnapshot(snapshotId!);
    }

    private static bool TryGetSnapshotId(JsonElement arguments, out string? snapshotId, out string? error)
    {
        snapshotId = null;
        error = null;

        if (!arguments.TryGetProperty("snapshot_id", out var idProp) ||
            idProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            error = "Missing required parameter: snapshot_id";
            return false;
        }

        snapshotId = idProp.GetString()!.Trim();
        return true;
    }
}