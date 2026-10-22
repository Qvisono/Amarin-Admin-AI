using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Общие папки, сетевые диски и сетевое обнаружение — для «не видит компьютер в сети».
/// </summary>
/// <remarks>
/// <para>
/// Общие папки этого ПК инструмент только показывает: открыть папку в сеть — решение о данных,
/// а не починка видимости. Гостевой доступ и подпись SMB тоже только показываются: их ослабление
/// Windows выключила намеренно, ради защиты от подмены сервера.
/// </para>
/// <para>
/// Паролей инструмент не принимает. Сетевой диск подключается теми учётными данными, что уже
/// есть у Windows; если сервер просит другие, человек вводит их сам.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SharesTool : ITool
{
    public const string ToolName = "shares";

    public string Name => ToolName;

    public string Description =>
        "Network sharing for problems where computers or shared folders are not seen on the network. status shows " +
        "the network type of each connection, Network Discovery and File and Printer Sharing firewall rules, the " +
        "services behind them, SMB client and server settings and NetBIOS. shares lists this PC's shared folders, " +
        "drives lists mapped network drives, check_host tests another computer: name, ping, port 445 and its shared " +
        "folders. set_private makes a connection private, enable_discovery and enable_file_sharing turn them on for " +
        "private networks, map_drive and unmap_drive connect and disconnect a network drive with the credentials " +
        "Windows already has. Changes need user confirmation; this tool never takes passwords.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "shares", "drives", "check_host", "set_private", "enable_discovery", "enable_file_sharing", "map_drive", "unmap_drive"],
              "description": "Network sharing operation"
            },
            "host": {
              "type": "string",
              "description": "check_host: computer name or IP address"
            },
            "adapter": {
              "type": "string",
              "description": "set_private: connection name exactly as status shows it"
            },
            "letter": {
              "type": "string",
              "description": "map_drive and unmap_drive: drive letter"
            },
            "remote_path": {
              "type": "string",
              "description": "map_drive: network path \\\\server\\share"
            },
            "persistent": {
              "type": "boolean",
              "description": "map_drive: reconnect at every sign-in (default true)"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        switch (action)
        {
            case "status":
                return Output(await PowerShellHelper.RunAsync(SharesCommands.StatusScript, 120, cancellationToken).ConfigureAwait(false));
            case "shares":
                return Output(await PowerShellHelper.RunAsync(SharesCommands.SharesScript, 60, cancellationToken).ConfigureAwait(false));
            case "drives":
                return Output(await PowerShellHelper.RunAsync(SharesCommands.DrivesScript, 60, cancellationToken).ConfigureAwait(false));
            case "check_host":
                return await CheckHostAsync(FileToolPaths.String(arguments, "host")?.Trim(), cancellationToken).ConfigureAwait(false);
            case "set_private":
                var adapter = FileToolPaths.String(arguments, "adapter")?.Trim();
                if (!UndoCommands.IsInterfaceAlias(adapter))
                {
                    return ToolResult.Fail("adapter is required: the connection name exactly as status shows it, without wildcards.");
                }

                return Marked(await PowerShellHelper.RunAsync(SharesCommands.SetPrivateScript(adapter!), 60, cancellationToken).ConfigureAwait(false),
                    $"{adapter} is now a private network.");
            case "enable_discovery" or "enable_file_sharing":
                var sharing = action == "enable_file_sharing";
                return Marked(await PowerShellHelper.RunAsync(SharesCommands.EnableScript(sharing), 120, cancellationToken).ConfigureAwait(false),
                    sharing
                        ? "File and Printer Sharing is on for private networks; the Server service runs and starts automatically."
                        : "Network Discovery is on for private networks; its services run, and this PC is published on the network.");
            case "map_drive":
                return await MapAsync(arguments, cancellationToken).ConfigureAwait(false);
            case "unmap_drive":
                if (!SharesCommands.TryLetter(FileToolPaths.String(arguments, "letter"), out var letter))
                {
                    return ToolResult.Fail("letter is required: one drive letter.");
                }

                return Marked(await PowerShellHelper.RunAsync(SharesCommands.UnmapScript(letter), 60, cancellationToken).ConfigureAwait(false),
                    $"Network drive {letter}: disconnected.");
            default:
                return ToolResult.Fail($"Unknown action: {action}");
        }
    }

    private static async Task<ToolResult> MapAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!SharesCommands.TryLetter(FileToolPaths.String(arguments, "letter"), out var letter))
        {
            return ToolResult.Fail("letter is required: one drive letter.");
        }

        var remote = FileToolPaths.String(arguments, "remote_path")?.Trim();
        if (!SharesCommands.IsUncPath(remote))
        {
            return ToolResult.Fail("remote_path is required: a network path \\\\server\\share without wildcards or quotes.");
        }

        if (DriveInfo.GetDrives().Any(drive => char.ToUpperInvariant(drive.Name[0]) == letter))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Shares.LetterTaken", letter));
        }

        var persistent = !arguments.TryGetProperty("persistent", out var value) || value.ValueKind != JsonValueKind.False;
        return Marked(await PowerShellHelper.RunAsync(SharesCommands.MapScript(letter, remote!, persistent), 90, cancellationToken).ConfigureAwait(false),
            $"{remote} connected as {letter}:{(persistent ? ", reconnected at every sign-in" : "")}.");
    }

    private static async Task<ToolResult> CheckHostAsync(string? host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(host) || !DnsConfigTool.IsHostName(host))
        {
            return ToolResult.Fail("host is required: a computer name or an IP address. Do not retry with the same value.");
        }

        var sb = new StringBuilder();
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            sb.AppendLine($"Name: {host} does not resolve (DNS, LLMNR and NetBIOS gave no address).");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        sb.AppendLine($"Name: {host} -> {string.Join(", ", addresses.Select(item => item.ToString()))}");
        if (address is null)
        {
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        using (var ping = new Ping())
        {
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2), null, null, cancellationToken).ConfigureAwait(false);
                sb.AppendLine(reply.Status == IPStatus.Success ? $"Ping: answers in {reply.RoundtripTime} ms" : $"Ping: {reply.Status} (a firewall may block ping)");
            }
            catch (PingException)
            {
                sb.AppendLine("Ping: failed");
            }
        }

        var smb = await PortOpenAsync(address, 445, cancellationToken).ConfigureAwait(false);
        sb.AppendLine($"Port 445 (SMB): {(smb ? "open" : "closed or filtered")}");
        if (!smb)
        {
            sb.AppendLine("File sharing on that computer is off, its firewall blocks it, or the device speaks only SMB1.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        sb.Append(await Task.Run(() => SharesOf(host), cancellationToken).ConfigureAwait(false));
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<bool> PortOpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Общие папки другого компьютера — теми учётными данными, что есть у Windows.</summary>
    private static string SharesOf(string host)
    {
        var resume = 0;
        var code = NetShareEnum(@"\\" + host, 1, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (code != 0 && code != 234)
            {
                return code switch
                {
                    5 => Loc.Get("S.Tool.Shares.Credentials"),
                    1326 => Loc.Get("S.Tool.Shares.Credentials"),
                    53 => "Shared folders: the computer does not answer as a file server (network path not found).",
                    _ => $"Shared folders: could not be listed (code {code})."
                };
            }

            var sb = new StringBuilder($"Shared folders ({read}):\n");
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var share = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * size));
                var kind = (share.Type & 0xFF) switch { 0 => "folder", 1 => "printer", 3 => "IPC", _ => "device" };
                var hidden = (share.Type & 0x80000000) != 0 ? ", hidden admin share" : "";
                sb.AppendLine($"  {share.Name} ({kind}{hidden}){(string.IsNullOrEmpty(share.Remark) ? "" : " " + share.Remark)}");
            }

            return sb.ToString();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    private static ToolResult Output(ToolResult result) =>
        result.Success
            ? ToolResult.Ok(PowerShellHelper.ExtractStdout(result.Output))
            : PowerShellHelper.IsAccessDenied(result.Output) ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")) : result;

    /// <summary>Итог скрипта записи по его меткам: «OK» — успех, остальное — понятный отказ.</summary>
    private static ToolResult Marked(ToolResult result, string success)
    {
        var text = PowerShellHelper.ExtractStdout(result.Output);

        // Раньше PUBLIC: метка начинается так же.
        const string publicRules = "PUBLIC_RULES";
        if (text.IndexOf(publicRules, StringComparison.Ordinal) is var at and >= 0)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Shares.PublicRules", text[(at + publicRules.Length)..].Trim()));
        }

        if (text.StartsWith("PUBLIC", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Shares.Public", text[6..].Trim()));
        }

        if (text.Contains("DOMAIN", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Get("S.Tool.Shares.Domain"));
        }

        if (text.Contains("NOT_MAPPED", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Get("S.Tool.Shares.NotMapped"));
        }

        if (result.Success && text.EndsWith("OK", StringComparison.Ordinal))
        {
            return ToolResult.Ok(success);
        }

        if (PowerShellHelper.IsAccessDenied(result.Output))
        {
            return ToolResult.Fail(Loc.Get(result.Output.Contains("SmbMapping", StringComparison.Ordinal)
                ? "S.Tool.Shares.Credentials"
                : "S.Tool.NeedsAdmin"));
        }

        return result.Output.Contains("0x8007052E", StringComparison.OrdinalIgnoreCase)
            ? ToolResult.Fail(Loc.Get("S.Tool.Shares.Credentials"))
            : ToolResult.Fail(result.Output);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string server, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref int resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
