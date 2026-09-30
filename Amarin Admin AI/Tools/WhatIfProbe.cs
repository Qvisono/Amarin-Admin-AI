using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Итог пробного прогона: что покажет <c>-WhatIf</c>, или что показать нечего.</summary>
internal sealed record WhatIfOutcome(bool Supported, string Text);

/// <summary>
/// Пробный прогон перед подтверждением: тот же вызов с <c>-WhatIf</c>, чтобы над текстом команды
/// стояло, что именно она изменит — какие файлы, службы, правила подошли под её условия.
/// </summary>
/// <remarks>
/// <para>
/// Прогон идёт только там, где он заведомо ничего не меняет. Для скрипта это значит: по дереву
/// разбора (<see cref="PowerShellAnalysis"/>) <b>каждая</b> запись в нём — командлет, и у каждого
/// такого командлета есть <c>-WhatIf</c>, что проверяется на месте отдельным запуском. Внешняя
/// программа, метод .NET или перенаправление в файл — и прогона нет: <c>$WhatIfPreference</c>
/// их не касается, и «пробный» запуск выполнил бы их по-настоящему.
/// </para>
/// <para>
/// Из функций допускаются только сгенерированные CDXML-модулями Windows (сеть, DNS,
/// брандмауэр, устройства, диски): они честно проходят через ShouldProcess. Функция стороннего
/// модуля может объявить <c>-WhatIf</c> и всё равно выполнить внутри что угодно.
/// </para>
/// </remarks>
internal static class WhatIfProbe
{
    /// <summary>Сколько ждать пробного прогона: кнопки его не ждут, но висеть вечно он не должен.</summary>
    public const int TimeoutSeconds = 20;

    /// <summary>Код выхода «у командлета нет -WhatIf» — прогон не показывается.</summary>
    internal const int UnsupportedExitCode = 3;

    internal const string UnsupportedMarker = "WHATIF_UNSUPPORTED";

    /// <summary>Модули Windows, чьи функции-командлеты сгенерированы из CDXML.</summary>
    internal static readonly string[] CdxmlModules =
    [
        "NetAdapter", "NetTCPIP", "DnsClient", "NetSecurity", "NetConnection", "ScheduledTasks",
        "Storage", "PnpDevice", "Defender", "SmbShare", "PrintManagement", "NetQos"
    ];

    /// <summary>Скрипт пробного прогона или null, если пробовать нельзя.</summary>
    public static string? ScriptFor(string toolName, JsonElement arguments)
    {
        var tool = toolName.Trim().ToLowerInvariant();
        if (tool == "run_powershell")
        {
            var command = arguments.ValueKind == JsonValueKind.Object &&
                          arguments.TryGetProperty("command", out var value) &&
                          value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            return command is null ? null : ForScript(command);
        }

        var action = DangerousActionGuard.ActionOf(arguments);
        return (tool, action) switch
        {
            ("windows_service", "start" or "stop" or "restart") when Text(arguments, "service_name") is { } name =>
                Checked($"{Verb(action)}-Service -Name '{PowerShellHelper.QuoteLiteral(name)}'" +
                        (action == "stop" ? " -Force" : "") + " -WhatIf"),
            ("devices", "enable" or "disable") when Text(arguments, "instance_id") is { } id && DeviceCommands.IsInstanceId(id) =>
                Checked($"{(action == "enable" ? "Enable" : "Disable")}-PnpDevice -InstanceId '{PowerShellHelper.QuoteLiteral(id)}' -WhatIf"),
            ("network", "adapter_enable" or "adapter_disable") when Text(arguments, "adapter") is { } alias &&
                                                                   UndoCommands.IsInterfaceAlias(alias) =>
                Checked($"{(action == "adapter_enable" ? "Enable" : "Disable")}-NetAdapter -Name '{PowerShellHelper.QuoteLiteral(alias)}' -WhatIf"),
            ("dns_config", "reset_dns") when Text(arguments, "adapter") is { } alias && UndoCommands.IsInterfaceAlias(alias) =>
                Checked($"Set-DnsClientServerAddress -InterfaceAlias '{PowerShellHelper.QuoteLiteral(alias)}' -ResetServerAddresses -WhatIf"),
            ("firewall_rules", "enable" or "disable" or "delete") when Text(arguments, "name") is { } rule &&
                                                                       FirewallRulesTool.TryGetSafeName(arguments, out var safe, out _) =>
                Checked($"Get-NetFirewallRule -DisplayName '{safe}' -ErrorAction SilentlyContinue | " +
                        $"{FirewallVerb(action)}-NetFirewallRule -WhatIf"),
            ("local_users", "enable_user" or "disable_user") when Text(arguments, "user") is { } user &&
                                                                  LocalUsersSafety.TryValidateAccountName(user, out var account, out _) =>
                Checked($"{(action == "enable_user" ? "Enable" : "Disable")}-LocalUser -Name '{PowerShellHelper.QuoteLiteral(account)}' -WhatIf"),
            _ => null
        };
    }

    /// <summary>
    /// Пробный прогон скрипта. Командлеты, давшие запись, сначала проверяются на наличие
    /// <c>-WhatIf</c>; нет хоть у одного — выход с кодом <see cref="UnsupportedExitCode"/>.
    /// </summary>
    internal static string? ForScript(string script)
    {
        var verdict = PowerShellAnalysis.Analyze(script);
        if (!verdict.IsWrite || verdict.Refusal is not null || verdict.WriteCmdlets is not { Count: > 0 } cmdlets)
        {
            return null;
        }

        var names = string.Join(",", cmdlets.Select(name => "'" + PowerShellHelper.QuoteLiteral(name) + "'"));
        return Guard(names) + "$WhatIfPreference = $true\n" + script;
    }

    /// <summary>Готовая команда инструмента, у которой -WhatIf уже стоит.</summary>
    private static string Checked(string command)
    {
        var first = command.Split(' ', 2)[0];
        var cmdlets = new List<string> { first };
        var pipe = command.LastIndexOf("| ", StringComparison.Ordinal);
        if (pipe >= 0)
        {
            cmdlets.Add(command[(pipe + 2)..].Split(' ', 2)[0]);
        }

        return Guard(string.Join(",", cmdlets.Select(name => "'" + name + "'"))) + command;
    }

    /// <summary>Проверка на месте: у каждого командлета есть -WhatIf и он из доверенных.</summary>
    private static string Guard(string quotedNames)
    {
        var modules = string.Join(",", CdxmlModules.Select(name => "'" + name + "'"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $unsupported = @(@({{quotedNames}}) | Where-Object {
              $c = Get-Command -Name $_ -ErrorAction SilentlyContinue | Select-Object -First 1
              -not $c -or -not $c.Parameters.ContainsKey('WhatIf') -or
                ($c.CommandType -ne 'Cmdlet' -and -not ($c.CommandType -eq 'Function' -and @({{modules}}) -contains $c.ModuleName))
            })
            if ($unsupported.Count -gt 0) { '{{UnsupportedMarker}}: ' + ($unsupported -join ', '); exit {{UnsupportedExitCode}} }

            """;
    }

    /// <summary>Запускает пробный прогон. null — пробовать нельзя или прогон не удался.</summary>
    public static async Task<WhatIfOutcome?> RunAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (ScriptFor(toolName, arguments) is not { } script)
        {
            return null;
        }

        var result = await PowerShellHelper.RunAsync(script, TimeoutSeconds, cancellationToken, maxOutput: 6000)
            .ConfigureAwait(false);
        return Interpret(result);
    }

    /// <summary>Из вывода прогона — то, что стоит показать человеку.</summary>
    internal static WhatIfOutcome? Interpret(ToolResult result)
    {
        var stdout = PowerShellHelper.ExtractStdout(result.Output);
        if (stdout.Contains(UnsupportedMarker, StringComparison.Ordinal))
        {
            return new WhatIfOutcome(false, "");
        }

        if (!result.Success && stdout.Length == 0)
        {
            return null;
        }

        var text = stdout.Trim();
        return new WhatIfOutcome(true, text.Length == 0 ? Loc.Get("S.Confirm.WhatIfNothing") : text);
    }

    private static string Verb(string action) => action switch
    {
        "start" => "Start",
        "stop" => "Stop",
        _ => "Restart"
    };

    private static string FirewallVerb(string action) => action switch
    {
        "enable" => "Enable",
        "disable" => "Disable",
        _ => "Remove"
    };

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
