using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

public sealed class ServiceTool : ITool
{
    public string Name => "windows_service";
    public string Description =>
        "Query and control Windows services: list, status, dependencies, start, stop, restart, " +
        "set_start_type (automatic, automatic_delayed, manual, disabled).";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "status", "dependencies", "start", "stop", "restart", "set_start_type"],
              "description": "Service operation"
            },
            "service_name": {
              "type": "string",
              "description": "Service name (required for status/start/stop/restart)"
            },
            "filter": {
              "type": "string",
              "description": "Optional substring filter for list action"
            },
            "start_type": {
              "type": "string",
              "enum": ["automatic", "automatic_delayed", "manual", "disabled"],
              "description": "New start type for set_start_type"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (DangerousActionGuard.ActionOf(arguments) == "set_start_type")
        {
            // Отдельно от try ниже: там всё синхронно, а здесь sc.exe с отменой хода.
            return await SetStartTypeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteSyncAsync(arguments).ConfigureAwait(false);
    }

    private static Task<ToolResult> ExecuteSyncAsync(JsonElement arguments)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionProp.GetString()?.Trim().ToLowerInvariant();
            arguments.TryGetProperty("service_name", out var nameProp);
            var serviceName = nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;
            arguments.TryGetProperty("filter", out var filterProp);
            var filter = filterProp.ValueKind == JsonValueKind.String ? filterProp.GetString() : null;

            // Второй рубеж после шлюза — см. ProtectedSystemTargets.
            if (ProtectedSystemTargets.TryGetServiceBlock(arguments, out var blocked))
            {
                return Task.FromResult(ToolResult.Fail(blocked));
            }

            return action switch
            {
                "list" => Task.FromResult(ListServices(filter)),
                "status" => Task.FromResult(GetStatus(serviceName)),
                "dependencies" => Task.FromResult(GetDependencies(serviceName)),
                "start" => Task.FromResult(ControlService(serviceName, ServiceControllerStatus.Running)),
                "stop" => Task.FromResult(ControlService(serviceName, ServiceControllerStatus.Stopped)),
                "restart" => Task.FromResult(RestartService(serviceName)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Service error: {ex.Message}"));
        }
    }

    private static async Task<ToolResult> SetStartTypeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (ProtectedSystemTargets.TryGetServiceBlock(arguments, out var blocked))
        {
            return ToolResult.Fail(blocked);
        }

        var serviceName = arguments.TryGetProperty("service_name", out var nameProp) &&
                          nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString()?.Trim()
            : null;
        var startType = arguments.TryGetProperty("start_type", out var typeProp) &&
                        typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToolResult.Fail("service_name is required for set_start_type");
        }

        if (!ServiceCommands.TryStartType(startType, out var scValue))
        {
            return ToolResult.Fail(
                "start_type must be one of: automatic, automatic_delayed, manual, disabled. Do not retry with the same value.");
        }

        // Имя — только существующей службы: sc.exe получает его списком аргументов, но и так
        // незачем передавать ему то, чего в системе нет.
        string name;
        try
        {
            using var service = new ServiceController(serviceName);
            name = service.ServiceName;
        }
        catch (InvalidOperationException)
        {
            return ToolResult.Fail($"Service '{serviceName}' was not found.");
        }

        var result = await NativeProcess.RunAsync("sc.exe", ServiceCommands.ConfigArguments(name, scValue), 60,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? ToolResult.Ok($"Service '{name}' start type set to {startType}.")
            : ToolResult.Fail($"sc.exe config failed: {result.Output}");
    }

    private static ToolResult ListServices(string? filter)
    {
        var services = ServiceController.GetServices()
            .Where(s => filter is null ||
                        s.ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.ServiceName)
            .Take(200)
            .Select(s => $"{s.ServiceName} | {s.DisplayName} | {s.Status}")
            .ToList();

        if (services.Count == 0)
        {
            return ToolResult.Ok("No services matched the filter.");
        }

        return ToolResult.Ok(string.Join(Environment.NewLine, services));
    }

    private static ToolResult GetStatus(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToolResult.Fail("service_name is required for status");
        }

        using var service = new ServiceController(serviceName);
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {service.ServiceName}");
        sb.AppendLine($"Display: {service.DisplayName}");
        sb.AppendLine($"Status: {service.Status}");
        sb.AppendLine($"Start type: {service.StartType}");
        sb.AppendLine($"Can stop: {service.CanStop}");
        sb.AppendLine($"Can pause: {service.CanPauseAndContinue}");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult GetDependencies(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToolResult.Fail("service_name is required for dependencies");
        }

        using var service = new ServiceController(serviceName);
        var sb = new StringBuilder();
        sb.AppendLine($"Service: {service.ServiceName} ({service.DisplayName})");
        sb.AppendLine();
        sb.AppendLine("Depends on:");
        AppendServiceList(sb, service.ServicesDependedOn);
        sb.AppendLine();
        sb.AppendLine("Dependent services:");
        AppendServiceList(sb, service.DependentServices);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static void AppendServiceList(StringBuilder sb, ServiceController[] services)
    {
        if (services.Length == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }

        foreach (var svc in services.OrderBy(s => s.ServiceName))
        {
            sb.AppendLine($"  {svc.ServiceName} | {svc.DisplayName} | {svc.Status}");
        }
    }

    private static ToolResult ControlService(string? serviceName, ServiceControllerStatus target)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToolResult.Fail("service_name is required");
        }

        using var service = new ServiceController(serviceName);

        if (target == ServiceControllerStatus.Running)
        {
            if (service.Status == ServiceControllerStatus.Running)
            {
                return ToolResult.Ok($"Service '{serviceName}' is already running.");
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return ToolResult.Ok($"Service '{serviceName}' started.");
        }

        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return ToolResult.Ok($"Service '{serviceName}' is already stopped.");
        }

        if (!service.CanStop)
        {
            return ToolResult.Fail($"Service '{serviceName}' cannot be stopped.");
        }

        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        return ToolResult.Ok($"Service '{serviceName}' stopped.");
    }

    private static ToolResult RestartService(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToolResult.Fail("service_name is required");
        }

        using var service = new ServiceController(serviceName);

        if (service.Status != ServiceControllerStatus.Stopped && service.CanStop)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }

        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        return ToolResult.Ok($"Service '{serviceName}' restarted.");
    }
}