using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace pxdEventViewer.Core;

public enum Severity { Critical, Error, Warning, Info }

/// <summary>One event, with the parts this app shows.</summary>
public sealed class EventItem
{
    public DateTime Time { get; set; }
    public string Log { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Id { get; set; }
    /// <summary>1 critical, 2 error, 3 warning, 4/0 information.</summary>
    public int Level { get; set; }
    public string Message { get; set; } = "";
    public List<string> Properties { get; set; } = [];
    /// <summary>What it's about (an app, a service, a disk): occurrences are grouped by it.</summary>
    public string Key { get; set; } = "";
    /// <summary>One plain-English line.</summary>
    public string Summary { get; set; } = "";
    public string? RuleId { get; set; }

    public string LevelName => Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" };
    public string FirstLine => Message.Split('\n', 2)[0].Trim();
    public string Prop(int i) => i < Properties.Count ? Properties[i] : "";
}

/// <summary>A kind of problem: which events mean it, what it means in plain English and what to try.</summary>
public sealed class Rule
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }
    public string Log { get; init; } = "System";
    public required string[] Providers { get; init; }
    public required int[] Ids { get; init; }
    public required string Explain { get; init; }
    public required string Fix { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>When set, only these source + event ID pairs count (instead of every source with every ID).</summary>
    public (string Provider, int[] Ids)[]? Pairs { get; init; }
    /// <summary>Fills Key and Summary of a matched event.</summary>
    public Action<EventItem>? Describe { get; init; }

    public string XPath(TimeSpan window)
    {
        var time = $"TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]";
        static string IdList(IEnumerable<int> ids) => string.Join(" or ", ids.Select(i => $"EventID={i}"));
        if (Pairs is { } pairs)
            return $"*[System[({string.Join(" or ", pairs.Select(p => $"(Provider[@Name='{p.Provider}'] and ({IdList(p.Ids)}))"))}) and {time}]]";
        var prov = string.Join(" or ", Providers.Select(p => $"Provider[@Name='{p}']"));
        return $"*[System[({prov}) and ({IdList(Ids)}) and {time}]]";
    }
}

public sealed record KeyCount(string Key, int Count, DateTime Last);

/// <summary>What one rule found in the chosen time window.</summary>
public sealed class RuleResult
{
    public required Rule Rule { get; init; }
    public List<EventItem> Items { get; init; } = [];
    public string? Error { get; set; }
    public bool Capped { get; set; }
    public int Count => Items.Count;
    public DateTime? Last => Items.Count == 0 ? null : Items.Max(i => i.Time);

    public List<KeyCount> Top(int n = 5) => Items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new KeyCount(g.First().Key, g.Count(), g.Max(x => x.Time)))
        .OrderByDescending(k => k.Count).ThenByDescending(k => k.Last).Take(n).ToList();
}

public sealed class Digest
{
    public DateTime TakenAt { get; set; } = DateTime.Now;
    public TimeSpan Window { get; set; }
    public List<RuleResult> Results { get; set; } = [];
    public RuleResult? this[string id] => Results.FirstOrDefault(r => r.Rule.Id == id);
    public int Count(string id) => this[id]?.Count ?? 0;
}

/// <summary>Reads Windows event logs and turns the events that matter into a short, plain-English digest.</summary>
public static class Events
{
    public const int MaxPerRule = 400;

    // ---------- Rules ----------

    public static readonly Rule[] Rules =
    [
        new()
        {
            Id = "bsod", Title = "Blue screens", Severity = Severity.Critical,
            Providers = ["Microsoft-Windows-WER-SystemErrorReporting", "BugCheck"], Ids = [1001],
            Explain = "Windows crashed and restarted (a stop error). The code says roughly what failed; the same code again and again points at one driver or bad hardware.",
            Fix = "Update or roll back the driver named in the code (graphics, network, storage are the usual ones), update the BIOS, run Windows Memory Diagnostic, and check the Hardware errors and Disk errors sections. Dumps are in C:\\Windows\\Minidump.",
            Describe = e =>
            {
                var code = Regex.Match(e.Message + " " + string.Join(" ", e.Properties), @"0x[0-9a-fA-F]{8}").Value;
                var name = StopCodeName(code);
                e.Key = code.Length == 0 ? "unknown code" : name is null ? code.ToLowerInvariant() : $"{code.ToLowerInvariant()} {name}";
                e.Summary = $"Blue screen {e.Key}";
            },
        },
        new()
        {
            Id = "unexpected-shutdown", Title = "Unexpected shutdowns", Severity = Severity.Critical,
            Providers = ["Microsoft-Windows-Kernel-Power"], Ids = [41],
            Explain = "Windows started again without having shut down properly: the power went off, the power button was held, the PC froze and was reset, or it blue-screened.",
            Fix = "If the power button was held, ask what the user saw before (a freeze?). Otherwise check the power supply and cables, a UPS, overheating (dust, fans), and the Blue screens section.",
            Describe = e =>
            {
                var bug = ParseLong(e.Prop(0));
                var button = ParseLong(e.Prop(6));
                (e.Key, e.Summary) = bug != 0
                    ? ("after a blue screen", $"Restarted after a blue screen 0x{bug:x8}{(StopCodeName($"0x{bug:x8}") is { } n ? " " + n : "")}")
                    : button != 0
                        ? ("power button held", "Turned off by holding the power button")
                        : ("power lost or reset", "Power was cut or the PC was reset (no blue screen recorded)");
            },
        },
        new()
        {
            Id = "hardware", Title = "Hardware errors", Severity = Severity.Critical,
            Providers = ["Microsoft-Windows-WHEA-Logger"], Ids = [1, 17, 18, 19, 20, 46, 47],
            Explain = "The processor, memory or a PCIe device reported a hardware error. Corrected ones are warnings; fatal ones cause blue screens (0x124).",
            Fix = "Undo any overclock or XMP/EXPO memory profile, update the BIOS and chipset drivers, reseat the graphics card and memory, check temperatures. Repeated errors on one device point at that part.",
            Describe = e =>
            {
                var comp = Regex.Match(e.Message, @"Component:\s*(.+)").Groups[1].Value.Trim();
                var src = Regex.Match(e.Message, @"Error Source:\s*(.+)").Groups[1].Value.Trim();
                e.Key = comp.Length > 0 ? comp : e.FirstLine;
                e.Summary = $"{(e.Id == 18 ? "Fatal" : e.Id == 19 ? "Corrected" : "")} hardware error: {e.Key}{(src.Length > 0 ? $" ({src})" : "")}".Trim();
            },
        },
        new()
        {
            Id = "disk-failing", Title = "Drive predicted to fail", Severity = Severity.Critical,
            Providers = ["disk"], Ids = [52],
            Explain = "A drive's own health monitoring (SMART) says it is about to fail.",
            Fix = "Back up that drive now, then replace it. Don't run long scans on it first.",
            Describe = e => { e.Key = DeviceOf(e.Message); e.Summary = $"SMART failure warning on {e.Key}"; },
        },
        new()
        {
            Id = "memory-test", Title = "Memory test failures", Severity = Severity.Critical,
            Providers = ["Microsoft-Windows-MemoryDiagnostics-Results"], Ids = [1102],
            Explain = "Windows Memory Diagnostic found errors in the RAM.",
            Fix = "Turn off XMP/EXPO, test one memory stick at a time, and replace the faulty one.",
            Describe = e => { e.Key = "RAM"; e.Summary = "Windows Memory Diagnostic found memory errors"; },
        },
        new()
        {
            Id = "disk", Title = "Disk and file system errors", Severity = Severity.Error,
            Providers = ["disk", "Ntfs", "Microsoft-Windows-Ntfs", "stornvme", "storahci", "volmgr"], Ids = [7, 11, 51, 55, 129, 137, 153, 154, 161],
            Explain = "Reads or writes failed, a drive stopped answering and was reset, or the file system found damage. A few after a power cut can be harmless; a steady stream means a failing drive or cable.",
            Fix = "Check the drive's health (CrystalDiskInfo or 'Get-PhysicalDisk'), reseat or replace the SATA cable, update storage drivers and firmware, then run 'chkdsk C: /scan'. Back up first if errors keep coming.",
            Describe = e =>
            {
                e.Key = DeviceOf(e.Message);
                e.Summary = e.Id switch
                {
                    7 => $"Bad block on {e.Key}",
                    11 or 51 or 153 or 154 => $"I/O error on {e.Key}",
                    129 => $"Drive reset (stopped responding) on {e.Key}",
                    55 or 137 => $"File system damage on {e.Key}",
                    161 => "Crash dump couldn't be written",
                    _ => e.FirstLine,
                };
            },
        },
        new()
        {
            Id = "app-crash", Title = "App crashes", Severity = Severity.Error, Log = "Application",
            Providers = ["Application Error", ".NET Runtime"], Ids = [1000, 1026],
            // .NET Runtime 1000 is ordinary app logging (ASP.NET Core and others), not a crash.
            Pairs = [("Application Error", [1000]), (".NET Runtime", [1026])],
            Explain = "Programs that closed because of an error. The faulting module says where: the app itself, a plug-in, a graphics driver (nvwgf2umx.dll, atidxx64.dll, igd*.dll) or Windows (ntdll.dll, KERNELBASE.dll).",
            Fix = "Update or repair the app. If the module is a graphics driver, update that driver. Many apps crashing in ntdll.dll suggests system file damage (run SFC and DISM) or bad memory.",
            Describe = e =>
            {
                if (e.Provider == ".NET Runtime")
                {
                    var app = Regex.Match(e.Message, @"Application:\s*(\S+)").Groups[1].Value;
                    var ex = Regex.Match(e.Message, @"Exception Info:\s*([\w\.]+)").Groups[1].Value;
                    e.Key = app.Length > 0 ? app : "a .NET app";
                    e.Summary = $"{e.Key} crashed{(ex.Length > 0 ? $" with {ex}" : "")}";
                    return;
                }

                e.Key = e.Prop(0).Length > 0 ? e.Prop(0) : "unknown app";
                var module = e.Prop(3);
                var code = e.Prop(6).Replace("0x", "");
                var codeName = ExceptionName(code);
                e.Summary = $"{e.Key} crashed{(module.Length > 0 ? " in " + module : "")}{(code.Length > 0 ? $" (0x{code}{(codeName is null ? "" : ", " + codeName)})" : "")}";
            },
        },
        new()
        {
            Id = "app-hang", Title = "Apps that stopped responding", Severity = Severity.Warning, Log = "Application",
            Providers = ["Application Hang"], Ids = [1002],
            Explain = "Programs that froze and were closed by the user or by Windows.",
            Fix = "Look for a pattern: the same app (update it, clear its cache), or every app at once (low memory, a slow or failing disk, antivirus scanning).",
            Describe = e => { e.Key = e.Prop(0).Length > 0 ? e.Prop(0) : "unknown app"; e.Summary = $"{e.Key} stopped responding"; },
        },
        new()
        {
            Id = "gpu-reset", Title = "Graphics driver resets", Severity = Severity.Warning,
            Providers = ["Display"], Ids = [4101],
            Explain = "The graphics driver stopped responding and Windows restarted it (the screen goes black for a moment).",
            Fix = "Clean-install the latest graphics driver (or roll back if it started after an update), check GPU temperatures, and undo any GPU overclock.",
            Describe = e =>
            {
                var drv = Regex.Match(e.Message, @"Display driver (\S+)").Groups[1].Value;
                e.Key = drv.Length > 0 ? drv : "graphics driver";
                e.Summary = $"{e.Key} stopped responding and was recovered";
            },
        },
        new()
        {
            Id = "service", Title = "Service failures", Severity = Severity.Error,
            Providers = ["Service Control Manager"], Ids = [7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034],
            Explain = "Windows services that failed to start, timed out, or stopped unexpectedly.",
            Fix = "Check the service in services.msc: its startup type, the account it runs as, and whether its program still exists. A service that keeps crashing usually needs its program reinstalled or updated.",
            Describe = e =>
            {
                var svc = Regex.Match(e.Message, @"^The (.+?) service", RegexOptions.Multiline).Groups[1].Value;
                if (svc.Length == 0) svc = Regex.Match(e.Message, @"for the (.+?) service").Groups[1].Value;
                e.Key = svc.Length > 0 ? svc : e.FirstLine;
                e.Summary = e.Id switch
                {
                    7031 or 7034 => $"{e.Key} stopped unexpectedly",
                    7009 or 7011 => $"{e.Key} timed out",
                    7001 => $"{e.Key} couldn't start because a service it needs failed",
                    _ => $"{e.Key} failed to start",
                };
            },
        },
        new()
        {
            Id = "update-failed", Title = "Failed Windows updates", Severity = Severity.Error,
            Providers = ["Microsoft-Windows-WindowsUpdateClient"], Ids = [20, 25, 31],
            Explain = "Updates that Windows Update couldn't download or install. One failure that later succeeds is normal; the same update failing again and again is not.",
            Fix = "Restart and try again, free up disk space, then reset Windows Update (pxd Windows Tweaks → Features & fixes) and run SFC and DISM. Look up the 0x8… error code for specifics.",
            Describe = e =>
            {
                var m = Regex.Match(e.Message, @"error (0x[0-9A-Fa-f]{8}):\s*(.+?)\.?\s*$", RegexOptions.Singleline);
                var title = m.Success ? m.Groups[2].Value.Trim() : e.Prop(1);
                e.Key = title.Length > 0 ? title : "an update";
                e.Summary = $"{e.Key} failed{(m.Success ? " with " + m.Groups[1].Value : "")}";
            },
        },
        new()
        {
            Id = "install-failed", Title = "Failed program installs", Severity = Severity.Warning, Log = "Application",
            Providers = ["MsiInstaller"], Ids = [11708, 11725],
            Explain = "Windows Installer (.msi) setups that failed or were rolled back.",
            Fix = "Run the installer again as administrator, check free space, and remove any older version first. Its log (msiexec /l*v) shows the exact step.",
            Describe = e =>
            {
                var prod = Regex.Match(e.Message, @"Product:\s*(.+?)\s*--").Groups[1].Value;
                e.Key = prod.Length > 0 ? prod : e.FirstLine;
                e.Summary = $"{e.Key}: {(e.Id == 11725 ? "removal failed" : "installation failed")}";
            },
        },
        new()
        {
            Id = "low-space", Title = "Low disk space", Severity = Severity.Warning,
            Providers = ["srv"], Ids = [2013],
            Explain = "A drive was at or near full. Full drives cause failed updates, crashes and slow logins.",
            Fix = "Free space: Disk Cleanup (including system files), empty Downloads and the Recycle Bin, move large files, or remove old user profiles.",
            Describe = e =>
            {
                var d = Regex.Match(e.Message, @"The ([A-Z]:) disk").Groups[1].Value;
                e.Key = d.Length > 0 ? d : "a drive";
                e.Summary = $"{e.Key} was nearly full";
            },
        },
        new()
        {
            Id = "network", Title = "Network problems", Severity = Severity.Warning,
            Providers = ["Microsoft-Windows-DNS-Client", "Tcpip"], Ids = [1014, 4199],
            Explain = "DNS lookups that timed out, or another device on the network using this PC's IP address.",
            Fix = "For DNS timeouts: check the DNS servers (router, domain controller) and the connection. For an IP conflict: find the other device by its MAC address, or give one of them a reserved address.",
            Describe = e =>
            {
                if (e.Id == 4199)
                {
                    var ip = Regex.Match(e.Message, @"\d+\.\d+\.\d+\.\d+").Value;
                    e.Key = "IP conflict " + ip;
                    e.Summary = $"Another device is using {ip}";
                    return;
                }

                var name = Regex.Match(e.Message, @"name (\S+) timed out").Groups[1].Value;
                e.Key = name.Length > 0 ? name : "DNS";
                e.Summary = $"DNS lookup for {e.Key} timed out";
            },
        },
        new()
        {
            Id = "group-policy", Title = "Group Policy errors", Severity = Severity.Warning,
            Providers = ["Microsoft-Windows-GroupPolicy"], Ids = [1085, 1096, 1125, 1129, 7016, 7320],
            Explain = "Domain policies that couldn't be read or applied, usually because the domain controller couldn't be reached at the time.",
            Fix = "Check the network and DNS (the PC must use the domain's DNS servers), then run 'gpupdate /force' and 'gpresult /h report.html'.",
            Describe = e => { e.Key = Trim(e.FirstLine, 90); e.Summary = e.Key; },
        },
        new()
        {
            Id = "time", Title = "Clock sync problems", Severity = Severity.Warning,
            Providers = ["Microsoft-Windows-Time-Service"], Ids = [36, 129, 134],
            Explain = "Windows couldn't sync its clock. A wrong clock breaks domain logins, websites (certificate errors) and Kerberos.",
            Fix = "Run 'w32tm /resync'. On a domain, check the PC can reach a domain controller; at home, pxd Windows Tweaks can switch it to pool.ntp.org.",
            Describe = e => { e.Key = Trim(e.FirstLine, 90); e.Summary = e.Key; },
        },
        new()
        {
            Id = "failed-logon", Title = "Failed sign-ins", Severity = Severity.Warning, Log = "Security", NeedsAdmin = true,
            Providers = ["Microsoft-Windows-Security-Auditing"], Ids = [4625],
            Explain = "Sign-ins that failed: wrong passwords, unknown accounts, locked or disabled accounts. Many in a row from one address can be an attack or a saved old password.",
            Fix = "Wrong password: check saved credentials (Credential Manager, mapped drives, phones with mail). Many from a remote address: block it and turn off exposed RDP.",
            Describe = e =>
            {
                var user = e.Prop(5);
                var ip = e.Prop(19);
                var why = e.Prop(9).ToLowerInvariant() switch
                {
                    "0xc000006a" => "wrong password",
                    "0xc0000064" => "no such account",
                    "0xc0000234" => "account locked out",
                    "0xc0000072" => "account disabled",
                    "0xc0000071" => "password expired",
                    "0xc0000193" => "account expired",
                    "0xc000006f" => "outside allowed hours",
                    "0xc0000224" => "must change password",
                    _ => "failed",
                };
                e.Key = $"{(user.Length > 0 ? user : "?")}: {why}";
                e.Summary = $"{user} {why}{(ip.Length > 1 ? " from " + ip : "")}";
            },
        },
        new()
        {
            Id = "restarts", Title = "Restarts and shutdowns", Severity = Severity.Info,
            Providers = ["User32"], Ids = [1074],
            Explain = "Who or what restarted or shut down the PC, and why. Answers \"it restarted on its own\".",
            Fix = "Restarts by TrustedInstaller or MoUsoCoreWorker are Windows Update; set active hours or use Security-only updates to control them.",
            Describe = e =>
            {
                var proc = Path.GetFileName(e.Prop(0).Split(' ')[0]);
                var user = e.Prop(6);
                var kind = e.Prop(4);
                var reason = e.Prop(2);
                e.Key = proc.Length > 0 ? proc : "unknown";
                e.Summary = $"{(kind.Length > 0 ? kind : "restart")} by {(user.Length > 0 ? user : "?")} via {e.Key}{(reason.Length > 0 ? ": " + Trim(reason, 70) : "")}";
            },
        },
    ];

    public static Rule? RuleFor(EventItem e) => Rules.FirstOrDefault(r =>
        r.Log.Equals(e.Log, StringComparison.OrdinalIgnoreCase)
        && (r.Pairs is { } pairs
            ? pairs.Any(p => p.Provider.Equals(e.Provider, StringComparison.OrdinalIgnoreCase) && p.Ids.Contains(e.Id))
            : r.Ids.Contains(e.Id) && r.Providers.Contains(e.Provider, StringComparer.OrdinalIgnoreCase)));

    // ---------- Reading ----------

    /// <summary>Runs every rule over the last <paramref name="window"/>. Rules that can't run (no access) carry an Error.</summary>
    public static Digest Read(TimeSpan window, bool includeAdminRules, CancellationToken ct = default)
    {
        var d = new Digest { Window = window };
        var tasks = Rules.Select(r => Task.Run(() =>
        {
            var res = new RuleResult { Rule = r };
            if (r.NeedsAdmin && !includeAdminRules)
            {
                res.Error = "Needs administrator rights";
                return res;
            }

            try
            {
                foreach (var e in Query(r.Log, r.XPath(window), MaxPerRule + 1, ct))
                {
                    Describe(r, e);
                    res.Items.Add(e);
                }

                if (res.Items.Count > MaxPerRule)
                {
                    res.Capped = true;
                    res.Items.RemoveAt(res.Items.Count - 1);
                }
            }
            catch (UnauthorizedAccessException)
            {
                res.Error = "Needs administrator rights";
            }
            catch (EventLogNotFoundException)
            {
                res.Items.Clear();
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }

            return res;
        }, ct)).ToArray();
        Task.WaitAll(tasks, ct);
        d.Results = tasks.Select(t => t.Result).ToList();
        return d;
    }

    public static void Describe(Rule r, EventItem e)
    {
        e.RuleId = r.Id;
        try
        {
            r.Describe?.Invoke(e);
        }
        catch
        {
            // a message in an unexpected shape: fall back below
        }

        if (e.Key.Length == 0) e.Key = Trim(e.FirstLine, 90);
        if (e.Summary.Length == 0) e.Summary = Trim(e.FirstLine, 140);
    }

    /// <summary>Newest first.</summary>
    public static IEnumerable<EventItem> Query(string log, string xpath, int max, CancellationToken ct = default)
    {
        var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        for (var n = 0; n < max; n++)
        {
            ct.ThrowIfCancellationRequested();
            using var rec = reader.ReadEvent();
            if (rec is null) yield break;
            yield return From(rec, log);
        }
    }

    private static EventItem From(EventRecord r, string log)
    {
        var e = new EventItem
        {
            Time = r.TimeCreated ?? DateTime.MinValue,
            Log = log,
            Provider = r.ProviderName ?? "",
            Id = r.Id,
            Level = r.Level ?? 4,
        };
        try { e.Properties = r.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "").ToList(); } catch { }
        try { e.Message = r.FormatDescription() ?? ""; } catch { }
        if (e.Message.Length == 0)
            e.Message = e.Properties.Count > 0 ? string.Join(" | ", e.Properties) : $"(No description for event {e.Id} from {e.Provider}.)";
        e.Message = e.Message.Replace("\r\n", "\n").Trim();
        return e;
    }

    /// <summary>Browse: newest events of one log, filtered by level, age and text.</summary>
    public static List<EventItem> Browse(string log, int[] levels, TimeSpan window, string? search, int max, CancellationToken ct = default)
    {
        var lv = levels.Length is 0 or 4 ? "" : " and (" + string.Join(" or ", levels.SelectMany(l => l == 4 ? new[] { 0, 4 } : [l]).Select(l => $"Level={l}")) + ")";
        var xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]{lv}]]";
        var list = new List<EventItem>();
        var scanned = 0;
        foreach (var e in Query(log, xpath, string.IsNullOrWhiteSpace(search) ? max : 50_000, ct))
        {
            scanned++;
            if (!string.IsNullOrWhiteSpace(search) && !(e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                         || e.Provider.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                         || e.Id.ToString(CultureInfo.InvariantCulture) == search.Trim()))
                continue;
            if (RuleFor(e) is { } r) Describe(r, e);
            list.Add(e);
            if (list.Count >= max) break;
        }

        return list;
    }

    public static readonly string[] CommonLogs = ["System", "Application", "Security", "Setup",
        "Microsoft-Windows-PowerShell/Operational", "Microsoft-Windows-TaskScheduler/Operational",
        "Microsoft-Windows-WindowsUpdateClient/Operational", "Microsoft-Windows-Windows Defender/Operational",
        "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", "Microsoft-Windows-Diagnostics-Performance/Operational"];

    // ---------- Helpers ----------

    private static long ParseLong(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], NumberStyles.HexNumber, null, out var h) ? h : 0;
        return long.TryParse(s, out var v) ? v : 0;
    }

    private static string DeviceOf(string message)
    {
        var m = Regex.Match(message, @"\\Device\\(Harddisk\d+|HarddiskVolume\d+|RaidPort\d+)");
        if (m.Success) return m.Groups[1].Value.Replace("Harddisk", "Disk ").Replace("Disk Volume", "Volume ");
        var vol = Regex.Match(message, @"volume ([A-Z]:|\\\\\?\\Volume\{[^}]+\})", RegexOptions.IgnoreCase);
        return vol.Success ? vol.Groups[1].Value : "a drive";
    }

    public static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static readonly Dictionary<long, string> StopCodes = new()
    {
        [0x0A] = "IRQL_NOT_LESS_OR_EQUAL", [0x19] = "BAD_POOL_HEADER", [0x1A] = "MEMORY_MANAGEMENT", [0x1E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x3B] = "SYSTEM_SERVICE_EXCEPTION", [0x50] = "PAGE_FAULT_IN_NONPAGED_AREA", [0x7A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x7E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", [0x7F] = "UNEXPECTED_KERNEL_MODE_TRAP", [0x9F] = "DRIVER_POWER_STATE_FAILURE",
        [0xA0] = "INTERNAL_POWER_ERROR", [0xC2] = "BAD_POOL_CALLER", [0xC5] = "DRIVER_CORRUPTED_EXPOOL", [0xD1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0xEF] = "CRITICAL_PROCESS_DIED", [0xF4] = "CRITICAL_OBJECT_TERMINATION", [0xFC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x101] = "CLOCK_WATCHDOG_TIMEOUT", [0x109] = "CRITICAL_STRUCTURE_CORRUPTION", [0x113] = "VIDEO_DXGKRNL_FATAL_ERROR",
        [0x116] = "VIDEO_TDR_FAILURE", [0x117] = "VIDEO_TDR_TIMEOUT_DETECTED", [0x119] = "VIDEO_SCHEDULER_INTERNAL_ERROR",
        [0x124] = "WHEA_UNCORRECTABLE_ERROR", [0x133] = "DPC_WATCHDOG_VIOLATION", [0x139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x13A] = "KERNEL_MODE_HEAP_CORRUPTION", [0x154] = "UNEXPECTED_STORE_EXCEPTION", [0x18B] = "SECURE_KERNEL_ERROR",
        [0x1C8] = "MANUALLY_INITIATED_POWER_BUTTON_HOLD", [0x7B] = "INACCESSIBLE_BOOT_DEVICE", [0xED] = "UNMOUNTABLE_BOOT_VOLUME",
        [0x3D] = "INTERRUPT_EXCEPTION_NOT_HANDLED", [0x1D] = "TRAP_CAUSE_UNKNOWN", [0xC4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0x10E] = "VIDEO_MEMORY_MANAGEMENT_INTERNAL", [0x1E0] = "?",
    };

    public static string? StopCodeName(string code)
    {
        if (code.Length < 3) return null;
        var v = ParseLong(code);
        return StopCodes.TryGetValue(v, out var n) && n != "?" ? n : null;
    }

    public static string? ExceptionName(string hex) => hex.ToLowerInvariant().TrimStart('0', 'x') switch
    {
        "c0000005" => "access violation",
        "c0000409" => "stack buffer overrun / fail-fast",
        "c0000374" => "heap corruption",
        "c00000fd" => "stack overflow",
        "c000001d" => "illegal instruction",
        "e0434352" => ".NET exception",
        "e06d7363" => "C++ exception",
        "80000003" => "breakpoint",
        "40000015" => "abort",
        "c0000602" => "fail-fast",
        "c0000142" => "DLL failed to load",
        "c0000135" => "DLL not found",
        "c000041d" => "exception in a callback",
        _ => null,
    };

    // ---------- Sample data ----------

    /// <summary>Made-up events for screenshots and the self-test.</summary>
    public static Digest Sample(TimeSpan window)
    {
        var now = DateTime.Now;
        var d = new Digest { Window = window };
        var rnd = new Random(7);
        EventItem Ev(Rule r, int id, string provider, string msg, double hoursAgo, string[]? props = null, int level = 2)
        {
            var e = new EventItem { Time = now.AddHours(-hoursAgo), Log = r.Log, Provider = provider, Id = id, Level = level, Message = msg, Properties = (props ?? []).ToList() };
            Describe(r, e);
            return e;
        }

        foreach (var r in Rules)
        {
            var res = new RuleResult { Rule = r };
            switch (r.Id)
            {
                case "bsod":
                    res.Items.Add(Ev(r, 1001, r.Providers[0], "The computer has rebooted from a bugcheck.  The bugcheck was: 0x00000116 (0xffffb20e6c2a1010, 0xfffff8064b5e9b30, 0xffffffffc000009a, 0x0000000000000004). A dump was saved in: C:\\WINDOWS\\Minidump\\110525-9218-01.dmp.", 52, level: 2));
                    break;
                case "unexpected-shutdown":
                    res.Items.Add(Ev(r, 41, r.Providers[0], "The system has rebooted without cleanly shutting down first.", 52, ["278", "0", "0", "0", "0", "0", "0"], 1));
                    res.Items.Add(Ev(r, 41, r.Providers[0], "The system has rebooted without cleanly shutting down first.", 121, ["0", "0", "0", "0", "0", "0", "133458812345"], 1));
                    break;
                case "gpu-reset":
                    for (var i = 0; i < 4; i++) res.Items.Add(Ev(r, 4101, "Display", "Display driver nvlddmkm stopped responding and has successfully recovered.", 30 + i * 9, level: 3));
                    break;
                case "app-crash":
                    foreach (var (app, mod, code, n) in new[] { ("OUTLOOK.EXE", "mso20win32client.dll", "c0000005", 6), ("Teams.exe", "ntdll.dll", "c0000409", 3), ("chrome.exe", "chrome.dll", "80000003", 2), ("AcroRd32.exe", "AcroRd32.dll", "c0000005", 1) })
                        for (var i = 0; i < n; i++)
                            res.Items.Add(Ev(r, 1000, "Application Error", $"Faulting application name: {app}", rnd.Next(2, 160), [app, "16.0", "0", mod, "1.0", "0", code, "0x1a2b3c"]));
                    break;
                case "app-hang":
                    for (var i = 0; i < 3; i++) res.Items.Add(Ev(r, 1002, "Application Hang", "The program EXCEL.EXE stopped interacting with Windows.", 20 + i * 30, ["EXCEL.EXE"], 2));
                    break;
                case "disk":
                    for (var i = 0; i < 2; i++) res.Items.Add(Ev(r, 129, "stornvme", "Reset to device, \\Device\\RaidPort0, was issued.", 50 + i * 2, level: 3));
                    break;
                case "service":
                    res.Items.Add(Ev(r, 7031, "Service Control Manager", "The Print Spooler service terminated unexpectedly.  It has done this 2 time(s).", 26));
                    res.Items.Add(Ev(r, 7031, "Service Control Manager", "The Print Spooler service terminated unexpectedly.  It has done this 1 time(s).", 27));
                    res.Items.Add(Ev(r, 7000, "Service Control Manager", "The Contoso VPN Helper service failed to start due to the following error: \nThe system cannot find the file specified.", 77));
                    break;
                case "update-failed":
                    res.Items.Add(Ev(r, 20, r.Providers[0], "Installation Failure: Windows failed to install the following update with error 0x800f0922: 2025-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835).", 140));
                    break;
                case "network":
                    for (var i = 0; i < 5; i++) res.Items.Add(Ev(r, 1014, r.Providers[0], "Name resolution for the name fileserver.contoso.local timed out after none of the configured DNS servers responded.", 3 + i * 11, level: 3));
                    break;
                case "restarts":
                    res.Items.Add(Ev(r, 1074, "User32", "The process C:\\WINDOWS\\servicing\\TrustedInstaller.exe has initiated the restart.", 78, ["C:\\WINDOWS\\servicing\\TrustedInstaller.exe (WORKSTATION-07)", "WORKSTATION-07", "Operating System: Upgrade (Planned)", "0x80020003", "restart", "", "NT AUTHORITY\\SYSTEM"], 4));
                    break;
                case "failed-logon":
                    for (var i = 0; i < 3; i++) res.Items.Add(Ev(r, 4625, r.Providers[0], "An account failed to log on.", 5 + i, ["", "", "", "", "", "alex", "CONTOSO", "0xc000006d", "", "0xc000006a", "3", "", "", "", "", "", "", "", "", "192.168.10.88"], 0));
                    break;
            }

            d.Results.Add(res);
        }

        return d;
    }
}
