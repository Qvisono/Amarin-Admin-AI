using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Management.Automation.Language;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>Что разбор скрипта PowerShell о нём узнал.</summary>
/// <param name="IsWrite">Скрипт что-нибудь меняет — или понять, что он делает, не вышло.</param>
/// <param name="Reasons">Что именно сочтено записью — для окна подтверждения.</param>
/// <param name="Refusal">Жёсткий отказ для модели; null — запрета нет.</param>
internal sealed record PowerShellVerdict(bool IsWrite, IReadOnlyList<string> Reasons, string? Refusal);

/// <summary>
/// Читает скрипт PowerShell по дереву разбора и решает, только ли он читает.
/// </summary>
/// <remarks>
/// <para>
/// Прежде защита искала в тексте регуляркой список опасных команд — список запрещённого.
/// Всё, о чём в нём забыли, шло без вопроса: <c>Set-Content</c>, <c>Remove-Item</c>,
/// <c>[IO.File]::WriteAllText</c>, <c>winget install</c>, <c>schtasks /create</c>, скачивание
/// с чужого сайта. Теперь наоборот — список разрешённого: без вопроса проходят только команды
/// чтения, а всё остальное, в том числе незнакомое и неразобранное, считается записью.
/// </para>
/// <para>
/// Разбирает парсер PowerShell 7 (<c>System.Management.Automation</c>), а исполняет скрипт
/// <c>powershell.exe</c> 5.1. Поэтому псевдонимы — по 5.1: там <c>sc</c> — это Set-Content,
/// а <c>curl</c> и <c>wget</c> — Invoke-WebRequest. Разбор кэшируется по тексту: одну и ту же
/// команду шлюз спрашивает несколько раз за вызов. Проверки, зависящие от настроек (белый
/// список загрузок), в кэш не попадают и считаются на каждый вызов.
/// </para>
/// </remarks>
internal static class PowerShellAnalysis
{
    private const int CacheLimit = 256;

    private static readonly ConcurrentDictionary<string, Facts> Cache = new(StringComparer.Ordinal);

    /// <summary>Глаголы командлетов, которые только читают.</summary>
    private static readonly FrozenSet<string> ReadVerbs = new[]
    {
        "get", "test", "measure", "select", "where", "sort", "group", "compare", "find", "resolve",
        "convertto", "convertfrom", "search"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Командлеты чтения с другими глаголами. Format-Volume среди «Format» нет намеренно.</summary>
    private static readonly FrozenSet<string> ReadCommands = new[]
    {
        "format-table", "format-list", "format-wide", "format-custom", "format-hex",
        "join-path", "split-path", "join-string",
        "out-string", "out-null", "out-host", "out-default",
        "write-output", "write-host", "write-verbose", "write-warning", "write-information", "write-debug",
        "write-error", "write-progress",
        "foreach-object", "where-object", "select-string", "import-csv", "import-clixml",
        "set-location", "push-location", "pop-location", "start-sleep", "clear-host",
        "set-variable", "new-variable", "remove-variable", "clear-variable", "new-timespan",
        "new-object", "tee-object", "add-member", "get-help", "help"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ключи, превращающие командлет чтения в запись: <c>Get-WindowsUpdate -Install</c>,
    /// <c>Test-ComputerSecureChannel -Repair</c>.
    /// </summary>
    private static readonly FrozenSet<string> WriteSwitches = new[]
    {
        "install", "download", "repair", "fix", "update", "uninstall", "reset", "delete", "remove",
        "clear", "kill", "stop", "start", "restart", "enable", "disable", "acceptall", "autoreboot"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Псевдонимы Windows PowerShell 5.1 — того, что исполняет скрипт.</summary>
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["%"] = "foreach-object", ["?"] = "where-object", ["ac"] = "add-content", ["cat"] = "get-content",
        ["cd"] = "set-location", ["chdir"] = "set-location", ["clc"] = "clear-content", ["clear"] = "clear-host",
        ["cli"] = "clear-item", ["clp"] = "clear-itemproperty", ["cls"] = "clear-host", ["clv"] = "clear-variable",
        ["compare"] = "compare-object", ["copy"] = "copy-item", ["cp"] = "copy-item", ["cpi"] = "copy-item",
        ["cpp"] = "copy-itemproperty", ["curl"] = "invoke-webrequest", ["cvpa"] = "convert-path",
        ["del"] = "remove-item", ["diff"] = "compare-object", ["dir"] = "get-childitem", ["echo"] = "write-output",
        ["epal"] = "export-alias", ["epcsv"] = "export-csv", ["erase"] = "remove-item", ["fc"] = "format-custom",
        ["fhx"] = "format-hex", ["fl"] = "format-list", ["foreach"] = "foreach-object", ["ft"] = "format-table",
        ["fw"] = "format-wide", ["gal"] = "get-alias", ["gc"] = "get-content", ["gcb"] = "get-clipboard",
        ["gci"] = "get-childitem", ["gcm"] = "get-command", ["gcs"] = "get-pscallstack", ["gdr"] = "get-psdrive",
        ["ghy"] = "get-history", ["gi"] = "get-item", ["gin"] = "get-computerinfo", ["gjb"] = "get-job",
        ["gl"] = "get-location", ["gm"] = "get-member", ["gmo"] = "get-module", ["gp"] = "get-itemproperty",
        ["gps"] = "get-process", ["gpv"] = "get-itempropertyvalue", ["group"] = "group-object",
        ["gsn"] = "get-pssession", ["gsv"] = "get-service", ["gtz"] = "get-timezone", ["gu"] = "get-unique",
        ["gv"] = "get-variable", ["gwmi"] = "get-wmiobject", ["h"] = "get-history", ["history"] = "get-history",
        ["icm"] = "invoke-command", ["iex"] = "invoke-expression", ["ihy"] = "invoke-history",
        ["ii"] = "invoke-item", ["ipal"] = "import-alias", ["ipcsv"] = "import-csv", ["ipmo"] = "import-module",
        ["irm"] = "invoke-restmethod", ["iwmi"] = "invoke-wmimethod", ["iwr"] = "invoke-webrequest",
        ["kill"] = "stop-process", ["lp"] = "out-printer", ["ls"] = "get-childitem", ["man"] = "help",
        ["md"] = "mkdir", ["measure"] = "measure-object", ["mi"] = "move-item", ["mount"] = "new-psdrive",
        ["move"] = "move-item", ["mp"] = "move-itemproperty", ["mv"] = "move-item", ["nal"] = "new-alias",
        ["ndr"] = "new-psdrive", ["ni"] = "new-item", ["nmo"] = "new-module", ["nsn"] = "new-pssession",
        ["nv"] = "new-variable", ["ogv"] = "out-gridview", ["oh"] = "out-host", ["popd"] = "pop-location",
        ["ps"] = "get-process", ["pushd"] = "push-location", ["pwd"] = "get-location", ["r"] = "invoke-history",
        ["rd"] = "remove-item", ["rdr"] = "remove-psdrive", ["ren"] = "rename-item", ["ri"] = "remove-item",
        ["rjb"] = "remove-job", ["rm"] = "remove-item", ["rmdir"] = "remove-item", ["rmo"] = "remove-module",
        ["rni"] = "rename-item", ["rnp"] = "rename-itemproperty", ["rp"] = "remove-itemproperty",
        ["rsn"] = "remove-pssession", ["rv"] = "remove-variable", ["rvpa"] = "resolve-path",
        ["rwmi"] = "remove-wmiobject", ["sajb"] = "start-job", ["sal"] = "set-alias", ["saps"] = "start-process",
        ["sasv"] = "start-service", ["sc"] = "set-content", ["scb"] = "set-clipboard", ["select"] = "select-object",
        ["set"] = "set-variable", ["shcm"] = "show-command", ["si"] = "set-item", ["sl"] = "set-location",
        ["sleep"] = "start-sleep", ["sls"] = "select-string", ["sort"] = "sort-object",
        ["sp"] = "set-itemproperty", ["spjb"] = "stop-job", ["spps"] = "stop-process", ["spsv"] = "stop-service",
        ["start"] = "start-process", ["stz"] = "set-timezone", ["sv"] = "set-variable",
        ["swmi"] = "set-wmiinstance", ["tee"] = "tee-object", ["type"] = "get-content",
        ["wget"] = "invoke-webrequest", ["where"] = "where-object", ["wjb"] = "wait-job",
        ["write"] = "write-output"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Программы, которые только читают, с какими бы аргументами их ни звали.</summary>
    private static readonly FrozenSet<string> ReadPrograms = new[]
    {
        "whoami", "hostname", "systeminfo", "tasklist", "getmac", "driverquery", "where", "findstr",
        "ping", "tracert", "pathping", "nslookup", "netstat", "quser", "qwinsta", "query", "ver"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Методы экземпляра, которые ничего не меняют вне памяти скрипта.</summary>
    private static readonly FrozenSet<string> ReadMethods = new[]
    {
        "tostring", "gettype", "equals", "gethashcode", "compareto", "contains", "containskey", "containsvalue",
        "startswith", "endswith", "indexof", "lastindexof", "indexofany", "substring", "split", "trim",
        "trimstart", "trimend", "toupper", "tolower", "toupperinvariant", "tolowerinvariant", "replace",
        "padleft", "padright", "normalize", "toarray", "tochararray", "getenumerator", "movenext", "where",
        "foreach", "getvalue", "getvaluenames", "getsubkeynames", "getvaluekind", "opensubkey", "close",
        "dispose", "getstring", "getbytes", "adddays", "addhours", "addminutes", "addseconds", "addmonths",
        "addyears", "addmilliseconds", "addticks", "subtract", "toshortdatestring", "tolongdatestring",
        "toshorttimestring", "tolongtimestring", "tolocaltime", "touniversaltime", "tofiletime", "match",
        "matches", "ismatch", "getowner", "readtoend", "readline", "read", "getfiles", "getdirectories",
        "getfilesysteminfos", "enumeratefiles", "enumeratedirectories", "getaccesscontrol", "getprocessesbyname",
        "item", "get_item", "clone", "copyto", "getlength", "getupperbound", "getlowerbound", "totalseconds"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Начала имён статических методов, которые только читают или считают.</summary>
    private static readonly string[] ReadStaticPrefixes =
    [
        "get", "read", "exists", "is", "parse", "tryparse", "to", "format", "combine", "join", "equals",
        "compare", "concat", "round", "abs", "max", "min", "floor", "ceiling", "sqrt", "pow", "truncate",
        "escape", "unescape", "new", "sign", "log", "expand", "hasextension", "changeextension"
    ];

    /// <summary>Методы, которые скачивают из сети.</summary>
    private static readonly FrozenSet<string> DownloadMethods = new[]
    {
        "downloadfile", "downloadstring", "downloaddata", "downloadfileasync", "downloadstringasync",
        "downloaddataasync", "openread", "getstringasync", "getbytearrayasync", "getstreamasync"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Разбирает скрипт. Не бросает: всё, что не разобрано, — запись.</summary>
    public static PowerShellVerdict Analyze(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new PowerShellVerdict(true, ["пустая команда"], null);
        }

        Facts facts;
        try
        {
            facts = Cache.TryGetValue(script, out var cached) ? cached : Remember(script, Collect(script));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Разборщик не загрузился или упал на входе — это не повод пропустить скрипт молча.
            return new PowerShellVerdict(true, ["скрипт не удалось разобрать"], null);
        }

        string? refusal;
        try
        {
            refusal = RefusalOf(facts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Проверка учётной записи или пути сорвалась — вопрос человеку всё равно будет:
            // такой скрипт уже записан в запись, и молча он не пройдёт.
            return new PowerShellVerdict(true, [.. facts.Reasons, "проверка целей не удалась"], null);
        }

        return new PowerShellVerdict(facts.IsWrite, facts.Reasons, refusal);
    }

    /// <summary>Аргумент <c>command</c> из аргументов инструмента.</summary>
    public static PowerShellVerdict Analyze(JsonElement arguments) =>
        Analyze(arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String
            ? command.GetString()
            : null);

    private static Facts Remember(string script, Facts facts)
    {
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[script] = facts;
        return facts;
    }

    /// <summary>Отказы — по порядку тяжести: чужой секрет, остановка ядра, загрузка, учётные записи.</summary>
    private static string? RefusalOf(Facts facts)
    {
        foreach (var path in facts.Paths)
        {
            if (SensitivePaths.IsSensitive(path, out var reason))
            {
                return reason;
            }
        }

        if (facts.IsWrite)
        {
            foreach (var path in facts.Paths)
            {
                if (SensitivePaths.IsProgramData(path))
                {
                    return SensitivePaths.ProgramDataRefusal(path);
                }
            }
        }

        foreach (var name in facts.Processes)
        {
            if (ProtectedSystemTargets.IsProtectedProcess(name))
            {
                return ProtectedSystemTargets.ProcessRefusal(ProtectedSystemTargets.NormalizeProcessName(name));
            }
        }

        foreach (var name in facts.Services)
        {
            if (ProtectedSystemTargets.IsProtectedService(name))
            {
                return ProtectedSystemTargets.ServiceRefusal(name);
            }
        }

        foreach (var url in facts.Downloads)
        {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !DownloadValidator.IsDomainAllowed(uri))
            {
                return DownloadRefusal(url);
            }
        }

        foreach (var op in facts.AccountOps)
        {
            if (op is null)
            {
                return AccountRefusal;
            }

            using var document = JsonDocument.Parse(op);
            if (LocalUsersSafety.TryGetHardBlockReason(document.RootElement, out var reason))
            {
                return reason;
            }
        }

        return null;
    }

    private static string DownloadRefusal(string? url) =>
        (url is null
            ? "ЗАПРЕЩЕНО: скрипт скачивает файл по адресу, который вычисляется при исполнении, — проверить его нельзя. "
            : $"ЗАПРЕЩЕНО: скрипт скачивает с {url}, а этого сайта нет в белом списке загрузок. ") +
        "Скачивание через PowerShell отклонено. Используй инструмент download_file: он проверяет адрес и спросит " +
        "пользователя, можно ли добавить сайт в список. Не повторяй попытку другой командой PowerShell.";

    private const string AccountRefusal =
        "ЗАПРЕЩЕНО: скрипт отключает учётную запись или убирает её из группы, а имя вычисляется при исполнении, — " +
        "проверить, не последний ли это администратор и не текущий ли пользователь, нельзя. Используй инструмент " +
        "local_users: он проверит это до изменения. Не повторяй попытку через PowerShell.";

    private static Facts Collect(string script)
    {
        var ast = Parser.ParseInput(script, out _, out var errors);
        var facts = new Facts();
        if (errors.Length > 0)
        {
            facts.Write("скрипт с синтаксической ошибкой");
            return facts;
        }

        // Свои функции скрипта: их тела разбираются отдельно, а вызов по имени — не внешняя
        // программа и не незнакомый командлет.
        var functions = ast.FindAll(node => node is FunctionDefinitionAst, searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>()
            .Select(function => function.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in ast.FindAll(_ => true, searchNestedScriptBlocks: true))
        {
            switch (node)
            {
                case CommandAst command when command.GetCommandName() is { } called && functions.Contains(called):
                    break;
                case CommandAst command:
                    Command(command, facts);
                    break;
                case FileRedirectionAst redirection:
                    Redirection(redirection, facts);
                    break;
                case InvokeMemberExpressionAst invoke:
                    Member(invoke, facts);
                    break;
                case StringConstantExpressionAst constant:
                    facts.Path(constant.Value);
                    break;
                case ExpandableStringExpressionAst expandable when Expand(expandable) is { } expanded:
                    facts.Path(expanded);
                    break;
            }
        }

        return facts;
    }

    private static void Command(CommandAst command, Facts facts)
    {
        var raw = command.GetCommandName();
        if (raw is null)
        {
            facts.Write("вызов команды, имя которой вычисляется при исполнении");
            return;
        }

        var name = Canonical(raw);
        var args = Arguments(command);

        if (name is "invoke-webrequest" or "invoke-restmethod" or "start-bitstransfer")
        {
            Web(name, command, args, facts);
            return;
        }

        if (name is "stop-process" or "stop-service" or "restart-service" or "suspend-service" or "set-service")
        {
            Targets(name, command, facts);
            facts.Write(raw);
            return;
        }

        if (name is "disable-localuser" or "remove-localgroupmember")
        {
            Accounts(name, command, facts);
            facts.Write(raw);
            return;
        }

        if (IsReadCmdlet(name, command))
        {
            return;
        }

        if (name.Contains('-', StringComparison.Ordinal))
        {
            facts.Write(raw);
            return;
        }

        Program(name, raw, args, facts);
    }

    /// <summary>Имя команды без модуля, пути, расширения и псевдонима — в нижнем регистре.</summary>
    private static string Canonical(string raw)
    {
        var name = raw.Trim();
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            // «sc.exe» — служебная программа, а не псевдоним Set-Content: с расширением
            // псевдоним не срабатывает.
            return name[..^4].ToLowerInvariant() + ".exe";
        }

        name = name.ToLowerInvariant();
        return Aliases.TryGetValue(name, out var target) ? target : name;
    }

    private static bool IsReadCmdlet(string name, CommandAst command)
    {
        if (name is "new-object")
        {
            // COM-объект — это Shell.Application, WScript.Shell и прочие двери наружу.
            return !HasParameter(command, "comobject", prefix: 1);
        }

        if (name is "tee-object")
        {
            return !HasParameter(command, "filepath", prefix: 1) && !HasParameter(command, "literalpath", prefix: 1) &&
                   !HasParameter(command, "append", prefix: 1) && command.CommandElements.Count <= 3;
        }

        var dash = name.IndexOf('-', StringComparison.Ordinal);
        var readVerb = dash > 0 && ReadVerbs.Contains(name[..dash]);
        if (!readVerb && !ReadCommands.Contains(name))
        {
            return false;
        }

        return !command.CommandElements.OfType<CommandParameterAst>()
            .Any(parameter => WriteSwitches.Any(name => Names(parameter.ParameterName, name, prefix: 3)));
    }

    /// <summary>Внешняя программа: читающая — только с аргументами чтения.</summary>
    private static void Program(string name, string raw, IReadOnlyList<string> args, Facts facts)
    {
        var program = name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
        var first = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        var lower = args.Select(arg => arg.ToLowerInvariant()).ToList();

        var read = program switch
        {
            _ when ReadPrograms.Contains(program) => true,
            "ipconfig" => !lower.Any(arg => arg.TrimStart('/', '-') is "release" or "release6" or "renew" or "renew6"
                or "flushdns" or "registerdns" or "setclassid" or "setclassid6"),
            "arp" => first is "-a" or "/a" or "-g" or "/g" or "",
            "route" => first == "print",
            "netsh" => lower.Contains("show") && !lower.Any(arg => arg is "set" or "add" or "delete" or "del"
                or "reset" or "install" or "uninstall" or "import" or "export" or "connect" or "disconnect"
                or "start" or "stop" or "flush" or "clear" or "remove" or "trace"),
            "sc" => first is "query" or "queryex" or "qc" or "qdescription" or "qfailure" or "qtriggerinfo"
                or "qprivs" or "qsidtype" or "getdisplayname" or "getkeyname" or "enumdepend" or "sdshow",
            "schtasks" => lower.Contains("/query") && !lower.Any(arg => arg is "/create" or "/delete"
                or "/change" or "/run" or "/end"),
            "reg" => first is "query" or "compare",
            "winget" => first is "list" or "search" or "show" or "--version" or "-v" or "--info",
            "bcdedit" => args.Count == 0 || first is "/enum" or "-enum",
            "net" => NetReads(first, lower),
            "certutil" => first is "-hashfile" or "-dump" or "-verify" or "-store" or "-viewstore",
            "wmic" => lower.Any(arg => arg is "get" or "list") &&
                      !lower.Any(arg => arg is "call" or "delete" or "set" or "create"),
            _ => false
        };

        if (program is "net" or "net1")
        {
            NetTargets(first, args, facts);
        }

        if (program is "taskkill")
        {
            for (var i = 0; i + 1 < args.Count; i++)
            {
                if (args[i].TrimStart('/', '-').Equals("im", StringComparison.OrdinalIgnoreCase))
                {
                    facts.Processes.Add(args[i + 1]);
                }
            }
        }

        if (program is "sc" && first is "stop" or "config" or "delete" && args.Count > 1)
        {
            facts.Services.Add(args[1]);
        }

        if (program is "curl" && lower.Any(arg => arg is "-o" or "-O" or "--output" or "--remote-name"))
        {
            facts.Downloads.Add(args.FirstOrDefault(arg => arg.Contains("://", StringComparison.Ordinal)));
        }

        if (program is "certutil" && lower.Contains("-urlcache"))
        {
            facts.Downloads.Add(args.FirstOrDefault(arg => arg.Contains("://", StringComparison.Ordinal)));
        }

        if (program is "bitsadmin" && lower.Contains("/transfer"))
        {
            facts.Downloads.Add(args.FirstOrDefault(arg => arg.Contains("://", StringComparison.Ordinal)));
        }

        if (!read)
        {
            facts.Write(raw);
        }
    }

    private static bool NetReads(string first, List<string> lower)
    {
        var rest = lower.Skip(1).ToList();
        var switches = rest.Where(arg => arg.StartsWith('/')).ToList();
        if (switches.Any(arg => arg != "/domain"))
        {
            return false;
        }

        var plain = rest.Count(arg => !arg.StartsWith('/'));
        return first switch
        {
            "view" or "statistics" or "config" => true,
            "accounts" or "use" or "session" => plain == 0,
            "user" or "localgroup" or "share" or "group" => plain <= 1 && !rest.Any(arg => arg.Contains('=')),
            _ => false
        };
    }

    /// <summary><c>net stop X</c>, <c>net user X /active:no</c>, <c>net localgroup administrators X /delete</c>.</summary>
    private static void NetTargets(string first, IReadOnlyList<string> args, Facts facts)
    {
        if (first is "stop" && args.Count > 1)
        {
            facts.Services.Add(args[1]);
        }

        if (first is "user" && args.Count > 1 &&
            args.Any(arg => arg.StartsWith("/active:no", StringComparison.OrdinalIgnoreCase)))
        {
            facts.AccountOps.Add(JsonSerializer.Serialize(new { action = "disable_user", user = args[1] }));
        }

        if (first is "localgroup" && args.Count > 2 &&
            args.Any(arg => arg.Equals("/delete", StringComparison.OrdinalIgnoreCase)))
        {
            facts.AccountOps.Add(JsonSerializer.Serialize(new { action = "remove_from_group", group = args[1], user = args[2] }));
        }
    }

    private static void Web(string name, CommandAst command, IReadOnlyList<string> args, Facts facts)
    {
        facts.Write(command.GetCommandName() ?? name);
        var saves = name == "start-bitstransfer" || HasParameter(command, "outfile", prefix: 4);
        if (!saves)
        {
            return;
        }

        var url = ParameterValue(command, "uri", prefix: 2) ?? ParameterValue(command, "source") ?? Positional(command);
        facts.Downloads.Add(url is { IsConstant: true } ? url.Value.Text : null);
    }

    /// <summary>Имя цели у остановки процесса или службы — само или из начала конвейера.</summary>
    private static void Targets(string name, CommandAst command, Facts facts)
    {
        var isProcess = name == "stop-process";
        var target = ParameterValue(command, "name") ?? ParameterValue(command, "processname") ??
                     ParameterValue(command, "displayname") ?? Positional(command);

        if (target is { IsConstant: true } constant)
        {
            Add(constant.Text);
            return;
        }

        // Get-Process lsass | Stop-Process — цель названа не у остановки, а у её поставщика.
        if (command.Parent is PipelineAst pipeline)
        {
            foreach (var element in pipeline.PipelineElements.OfType<CommandAst>())
            {
                if (ReferenceEquals(element, command))
                {
                    break;
                }

                var source = element.GetCommandName() is { } sourceName ? Canonical(sourceName) : "";
                if (source is "get-process" or "get-service" &&
                    (ParameterValue(element, "name") ?? Positional(element)) is { IsConstant: true } named)
                {
                    Add(named.Text);
                }
            }
        }

        void Add(string value)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (isProcess ? facts.Processes : facts.Services).Add(part);
            }
        }
    }

    private static void Accounts(string name, CommandAst command, Facts facts)
    {
        if (name == "disable-localuser")
        {
            var user = ParameterValue(command, "name") ?? ParameterValue(command, "sid") ?? Positional(command);
            facts.AccountOps.Add(user is { IsConstant: true }
                ? JsonSerializer.Serialize(new { action = "disable_user", user = user.Value.Text })
                : null);
            return;
        }

        var group = ParameterValue(command, "group") ?? ParameterValue(command, "name") ?? Positional(command);
        var member = ParameterValue(command, "member");
        facts.AccountOps.Add(group is { IsConstant: true } && member is { IsConstant: true }
            ? JsonSerializer.Serialize(new { action = "remove_from_group", group = group.Value.Text, user = member.Value.Text })
            : null);
    }

    private static void Redirection(FileRedirectionAst redirection, Facts facts)
    {
        if (redirection.Location is VariableExpressionAst { VariablePath.UserPath: var variable } &&
            variable.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        facts.Write("перенаправление вывода в файл");
        if (redirection.Location is StringConstantExpressionAst target)
        {
            facts.Path(target.Value);
        }
    }

    private static void Member(InvokeMemberExpressionAst invoke, Facts facts)
    {
        var name = (invoke.Member as StringConstantExpressionAst)?.Value;
        if (name is null)
        {
            facts.Write("вызов метода, имя которого вычисляется при исполнении");
            return;
        }

        if (DownloadMethods.Contains(name))
        {
            facts.Write("." + name + "()");
            var url = invoke.Arguments?.FirstOrDefault();
            facts.Downloads.Add(url is StringConstantExpressionAst constant ? constant.Value : null);
            return;
        }

        if (invoke.Static)
        {
            if (!ReadStaticPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("GetDelegateForFunctionPointer", StringComparison.OrdinalIgnoreCase))
            {
                facts.Write(invoke.Expression.Extent.Text + "::" + name);
            }

            return;
        }

        if (!ReadMethods.Contains(name))
        {
            facts.Write("." + name + "()");
        }
    }

    private readonly record struct ArgumentValue(bool IsConstant, string Text);

    private static ArgumentValue? ParameterValue(CommandAst command, string parameter, int prefix = 1)
    {
        var elements = command.CommandElements;
        for (var i = 1; i < elements.Count; i++)
        {
            if (elements[i] is not CommandParameterAst named || !Names(named.ParameterName, parameter, prefix))
            {
                continue;
            }

            var value = named.Argument ?? (i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst
                ? elements[i + 1]
                : null);
            return value is null ? null : ValueOf(value);
        }

        return null;
    }

    /// <summary>Первый позиционный аргумент — первый элемент после имени, не ключ и не значение ключа.</summary>
    private static ArgumentValue? Positional(CommandAst command)
    {
        var elements = command.CommandElements;
        for (var i = 1; i < elements.Count; i++)
        {
            if (elements[i] is CommandParameterAst named)
            {
                // Значение ключа, написанное через пробел, позиционным не считается.
                if (named.Argument is null && i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst &&
                    !IsSwitchLike(named.ParameterName))
                {
                    i++;
                }

                continue;
            }

            return ValueOf(elements[i]);
        }

        return null;
    }

    private static bool IsSwitchLike(string parameter) =>
        parameter.ToLowerInvariant() is "force" or "passthru" or "whatif" or "confirm" or "recurse" or "verbose"
            or "usebasicparsing" or "nonewwindow" or "wait";

    private static ArgumentValue ValueOf(CommandElementAst element) => element switch
    {
        StringConstantExpressionAst constant => new ArgumentValue(true, constant.Value),
        ExpandableStringExpressionAst expandable when Expand(expandable) is { } text => new ArgumentValue(true, text),
        ArrayLiteralAst array when array.Elements.All(item => item is StringConstantExpressionAst) =>
            new ArgumentValue(true, string.Join(",", array.Elements.Cast<StringConstantExpressionAst>().Select(item => item.Value))),
        _ => new ArgumentValue(false, element.Extent.Text)
    };

    /// <summary>Строка с переменными окружения — раскрытая; с чем-то ещё внутри — null.</summary>
    private static string? Expand(ExpandableStringExpressionAst expandable)
    {
        var text = expandable.Value;
        foreach (var nested in expandable.NestedExpressions)
        {
            if (nested is not VariableExpressionAst { VariablePath: { IsDriveQualified: true } path } ||
                !path.DriveName.Equals("env", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var name = path.UserPath[(path.DriveName.Length + 1)..];
            text = text.Replace(nested.Extent.Text, Environment.GetEnvironmentVariable(name) ?? "", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>Аргументы внешней программы строками — как их увидит сама программа.</summary>
    private static List<string> Arguments(CommandAst command) =>
        command.CommandElements.Skip(1)
            .Select(element => element switch
            {
                StringConstantExpressionAst constant => constant.Value,
                ExpandableStringExpressionAst expandable => Expand(expandable) ?? expandable.Value,
                CommandParameterAst parameter => parameter.Extent.Text,
                _ => element.Extent.Text
            })
            .ToList();

    private static bool HasParameter(CommandAst command, string name, int prefix = 0) =>
        command.CommandElements.OfType<CommandParameterAst>()
            .Any(parameter => Names(parameter.ParameterName, name, prefix));

    /// <summary>
    /// Ключ написан полностью или сокращён. PowerShell принимает любое однозначное начало
    /// имени ключа: <c>-OutF</c> — это <c>-OutFile</c>, и точное сравнение пропустило бы загрузку.
    /// </summary>
    /// <param name="prefix">С какой длины сокращение однозначно; 0 — только полное имя.</param>
    private static bool Names(string written, string full, int prefix) =>
        written.Equals(full, StringComparison.OrdinalIgnoreCase) ||
        (prefix > 0 && written.Length >= prefix && full.StartsWith(written, StringComparison.OrdinalIgnoreCase));

    /// <summary>Собранное о скрипте без проверок, зависящих от настроек.</summary>
    private sealed class Facts
    {
        private readonly List<string> _reasons = [];

        public bool IsWrite { get; private set; }

        public IReadOnlyList<string> Reasons => _reasons;

        public List<string> Paths { get; } = [];

        public List<string> Processes { get; } = [];

        public List<string> Services { get; } = [];

        /// <summary>Адреса загрузок; null — адрес вычисляется при исполнении.</summary>
        public List<string?> Downloads { get; } = [];

        /// <summary>Операции с учётными записями аргументами <c>local_users</c>; null — имя не константа.</summary>
        public List<string?> AccountOps { get; } = [];

        public void Write(string reason)
        {
            IsWrite = true;

            // Причины идут в строку окна подтверждения: «команда» из четырёхсот символов
            // растянула бы её, а весь скрипт человек и так видит целиком ниже.
            reason = reason.Length <= 48 ? reason : reason[..48] + "…";
            if (_reasons.Count < 8 && !_reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
            {
                _reasons.Add(reason);
            }
        }

        /// <summary>Строка, похожая на путь, — для проверки секретов и данных программы.</summary>
        public void Path(string? value)
        {
            var text = Environment.ExpandEnvironmentVariables(value ?? "").Trim();
            if (text.StartsWith('~'))
            {
                text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
            }

            if (text.Length is > 2 and < 1024 &&
                (text.Contains('\\', StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal) ||
                 text.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase)))
            {
                Paths.Add(text);
            }
        }
    }
}
