using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace pxdEventViewer.Core;

public sealed record DiskInfo(string Name, string Label, long Total, long Free, string Format)
{
    public double FreePercent => Total > 0 ? 100.0 * Free / Total : 0;
}

public sealed record AdapterInfo(string Name, string Kind, string Ipv4, string Gateway, string Dns, string Mac, string Speed, bool Dhcp);

public sealed record HotfixInfo(string Id, string Description, DateTime? InstalledOn);

/// <summary>Everything a support ticket needs to know about the PC, read in one go (WMI parts are slow: run off the UI thread).</summary>
public sealed class Snapshot
{
    public DateTime TakenAt { get; set; } = DateTime.Now;
    public string ComputerName { get; set; } = "";
    public string Domain { get; set; } = "";
    public bool DomainJoined { get; set; }
    public string User { get; set; } = "";
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime LastBoot { get; set; }
    public TimeSpan Uptime { get; set; }
    public List<string> PendingReboot { get; set; } = [];
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public long RamTotal { get; set; }
    public long RamFree { get; set; }
    public List<DiskInfo> Disks { get; set; } = [];
    public List<AdapterInfo> Adapters { get; set; } = [];
    public List<HotfixInfo> Hotfixes { get; set; } = [];
    public string Antivirus { get; set; } = "";
    public string? DefenderSignatures { get; set; }
    public List<string> Problems { get; set; } = [];

    public double RamUsedPercent => RamTotal > 0 ? 100.0 * (RamTotal - RamFree) / RamTotal : 0;

    public static Snapshot Take()
    {
        var s = new Snapshot();
        void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { s.Problems.Add($"{what}: {ex.Message}"); }
        }

        s.ComputerName = Environment.MachineName;
        s.User = $@"{Environment.UserDomainName}\{Environment.UserName}";
        Try("Signed-in users", () => s.User = DescribeUsers(SignedInUsers(), s.User));
        Try("Windows", () => ReadWindows(s));
        Try("Uptime", () =>
        {
            s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            s.LastBoot = DateTime.Now - s.Uptime;
        });
        Try("Pending reboot", () => s.PendingReboot = PendingRebootReasons());
        Try("Computer", () => ReadComputer(s));
        Try("Processor", () => ReadCpu(s));
        Try("Memory", () => ReadMemory(s));
        Try("Disks", () => ReadDisks(s));
        Try("Network", () => ReadNetwork(s));
        Try("Updates", () => ReadHotfixes(s));
        Try("Antivirus", () => ReadAntivirus(s));
        return s;
    }

    private static string RegString(string path, string name) =>
        Registry.GetValue(path, name, null)?.ToString() ?? "";

    private static void ReadWindows(Snapshot s)
    {
        const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = RegString(cv, "ProductName");
        var build = RegString(cv, "CurrentBuildNumber");
        var ubr = Registry.GetValue(cv, "UBR", null);
        // Windows 11 still reports "Windows 10" in ProductName.
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        s.OsName = product;
        s.OsVersion = RegString(cv, "DisplayVersion") is { Length: > 0 } dv ? dv : RegString(cv, "ReleaseId");
        s.OsBuild = ubr is null ? build : $"{build}.{ubr}";
        if (Registry.GetValue(cv, "InstallDate", null) is int secs and > 0)
            s.InstallDate = DateTimeOffset.FromUnixTimeSeconds((uint)secs).LocalDateTime;
        var ip = IPGlobalProperties.GetIPGlobalProperties();
        s.DomainJoined = !string.IsNullOrEmpty(ip.DomainName);
        s.Domain = s.DomainJoined ? ip.DomainName : Environment.UserDomainName == Environment.MachineName ? "WORKGROUP" : Environment.UserDomainName;
    }

    /// <summary>
    /// The people signed in at the PC (console and Remote Desktop): the owners of each explorer.exe, falling back to
    /// the console user. Run from a remote PowerShell, this is the person at the PC, not the admin running the report.
    /// </summary>
    public static List<string> SignedInUsers()
    {
        var users = new List<string>();
        foreach (var o in Wmi("SELECT Handle FROM Win32_Process WHERE Name = 'explorer.exe'"))
        {
            try
            {
                using var p = (ManagementObject)o;
                var args = new object?[] { null, null };
                if (Convert.ToInt32(p.InvokeMethod("GetOwner", args), CultureInfo.InvariantCulture) == 0 && args[0] is string user && user.Length > 0)
                    users.Add(args[1] is string dom && dom.Length > 0 ? $@"{dom}\{user}" : user);
            }
            catch
            {
                // that process ended, or its owner can't be read without administrator rights
            }
        }

        if (users.Count == 0)
            foreach (var o in Wmi("SELECT UserName FROM Win32_ComputerSystem"))
                if (Str(o, "UserName") is { Length: > 0 } u) users.Add(u);
        return users.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"CONTOSO\alex", or "CONTOSO\alex (report run by CONTOSO\admin)" when someone else runs it.</summary>
    public static string DescribeUsers(List<string> signedIn, string runBy)
    {
        if (signedIn.Count == 0) return $"Nobody signed in (report run by {runBy})";
        var list = string.Join(", ", signedIn);
        return signedIn.Any(u => u.Equals(runBy, StringComparison.OrdinalIgnoreCase)) && signedIn.Count == 1 ? list : $"{list} (report run by {runBy})";
    }

    /// <summary>Why Windows wants a restart, from the usual places installers and updates leave a flag.</summary>
    public static List<string> PendingRebootReasons()
    {
        var reasons = new List<string>();
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        bool KeyExists(string p)
        {
            using var k = lm.OpenSubKey(p);
            return k is not null;
        }

        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("Windows component servicing");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update");
        using (var sm = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            if (sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("Files waiting to be replaced");
        using (var a = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName"))
        using (var p = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName"))
            if (a?.GetValue("ComputerName") is string an && p?.GetValue("ComputerName") is string pn && !an.Equals(pn, StringComparison.OrdinalIgnoreCase))
                reasons.Add("Computer rename");
        if (KeyExists(@"SOFTWARE\Microsoft\Updates\UpdateExeVolatile")) reasons.Add("Software update");
        return reasons;
    }

    private static IEnumerable<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, query) { Options = { Timeout = TimeSpan.FromSeconds(15) } };
        foreach (var o in searcher.Get()) yield return o;
    }

    private static string Str(ManagementBaseObject o, string p) => o[p]?.ToString()?.Trim() ?? "";

    private static void ReadComputer(Snapshot s)
    {
        foreach (var o in Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            s.Manufacturer = Str(o, "Manufacturer");
            s.Model = Str(o, "Model");
        }

        foreach (var o in Wmi("SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            s.Serial = Str(o, "SerialNumber");
            var date = Str(o, "ReleaseDate");
            s.Bios = Str(o, "SMBIOSBIOSVersion") + (date.Length >= 8 ? $" ({date[..4]}-{date[4..6]}-{date[6..8]})" : "");
        }

        if (s.Serial is "To be filled by O.E.M." or "Default string" or "System Serial Number" or "0") s.Serial = "";
        // Self-built PCs carry placeholder names; the motherboard says more.
        if (s.Model is "" or "System Product Name" or "To be filled by O.E.M." or "Default string")
            foreach (var o in Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            {
                s.Manufacturer = Str(o, "Manufacturer");
                s.Model = Str(o, "Product") + " (motherboard)";
            }
    }

    private static void ReadCpu(Snapshot s)
    {
        s.Cpu = RegString(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim();
        s.Threads = Environment.ProcessorCount;
        foreach (var o in Wmi("SELECT NumberOfCores FROM Win32_Processor"))
            s.Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0, CultureInfo.InvariantCulture);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    private static void ReadMemory(Snapshot s)
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        s.RamTotal = (long)m.TotalPhys;
        s.RamFree = (long)m.AvailPhys;
    }

    private static void ReadDisks(Snapshot s)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            s.Disks.Add(new DiskInfo(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace, d.DriveFormat));
        }
    }

    private static void ReadNetwork(Snapshot s)
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var p = n.GetIPProperties();
            var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (v4.Count == 0) continue;
            var ip = string.Join(", ", v4.Select(a => $"{a.Address}/{a.PrefixLength}"));
            var gw = string.Join(", ", p.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork));
            var dns = string.Join(", ", p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
            var mac = string.Join("-", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            bool dhcp;
            try { dhcp = p.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { dhcp = false; }
            var kind = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                NetworkInterfaceType.Ppp => "VPN",
                _ => n.NetworkInterfaceType.ToString(),
            };
            if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) kind = "Virtual";
            var speed = n.Speed > 0 ? n.Speed >= 1_000_000_000 ? $"{n.Speed / 1_000_000_000.0:0.#} Gbps" : $"{n.Speed / 1_000_000} Mbps" : "";
            s.Adapters.Add(new AdapterInfo(n.Name, kind, ip, gw, dns, mac, speed, dhcp));
        }

        // Real adapters with a gateway first.
        s.Adapters = s.Adapters.OrderBy(a => a.Kind == "Virtual").ThenBy(a => a.Gateway.Length == 0).ToList();
    }

    private static void ReadHotfixes(Snapshot s)
    {
        foreach (var o in Wmi("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"))
        {
            DateTime? when = null;
            var raw = Str(o, "InstalledOn");
            if (DateTime.TryParseExact(raw.Split(' ')[0], ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) when = d;
            else if (raw.Length == 16 && long.TryParse(raw, NumberStyles.HexNumber, null, out var ft)) when = DateTime.FromFileTime(ft);
            s.Hotfixes.Add(new HotfixInfo(Str(o, "HotFixID"), Str(o, "Description"), when));
        }

        s.Hotfixes = s.Hotfixes.OrderByDescending(h => h.InstalledOn ?? DateTime.MinValue).ToList();
    }

    private static void ReadAntivirus(Snapshot s)
    {
        var names = new List<string>();
        try
        {
            foreach (var o in Wmi("SELECT displayName FROM AntiVirusProduct", @"root\SecurityCenter2"))
                names.Add(Str(o, "displayName"));
        }
        catch
        {
            // Server editions have no Security Center.
        }

        s.Antivirus = names.Count == 0 ? "None reported" : string.Join(", ", names.Distinct());
        try
        {
            foreach (var o in Wmi("SELECT AntivirusSignatureLastUpdated, RealTimeProtectionEnabled FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender"))
            {
                var raw = Str(o, "AntivirusSignatureLastUpdated");
                var rt = o["RealTimeProtectionEnabled"] is bool b && b;
                if (raw.Length >= 14 && DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    s.DefenderSignatures = $"Defender {(rt ? "real-time on" : "real-time OFF")}, signatures {Ago(DateTime.Now - d)} old";
            }
        }
        catch
        {
            // Defender not present or turned off by another antivirus.
        }
    }

    // ---------- Text ----------

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public static string Ago(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, (int)t.TotalMinutes)}m";

    /// <summary>A made-up workstation for screenshots and the self-test (never this PC's details in a public README).</summary>
    public static Snapshot Sample() => new()
    {
        ComputerName = "WORKSTATION-07",
        Domain = "contoso.local",
        DomainJoined = true,
        User = @"CONTOSO\alex",
        OsName = "Windows 11 Pro",
        OsVersion = "24H2",
        OsBuild = "26100.4652",
        InstallDate = new DateTime(2025, 3, 14),
        Uptime = TimeSpan.FromHours(77.5),
        LastBoot = DateTime.Now.AddHours(-77.5),
        PendingReboot = ["Windows Update"],
        Manufacturer = "Dell Inc.",
        Model = "OptiPlex 7010",
        Serial = "7XK2Q93",
        Bios = "1.14.0 (2025-06-02)",
        Cpu = "13th Gen Intel(R) Core(TM) i7-13700",
        Cores = 16,
        Threads = 24,
        RamTotal = 32L << 30,
        RamFree = 11L << 30,
        Disks = [new DiskInfo("C:", "Windows", 476L << 30, 38L << 30, "NTFS"), new DiskInfo("D:", "Data", 931L << 30, 512L << 30, "NTFS")],
        Adapters = [new AdapterInfo("Ethernet", "Ethernet", "192.168.10.57/24", "192.168.10.1", "192.168.10.5, 192.168.10.6", "3C-7C-3F-1A-22-9B", "1 Gbps", true)],
        Hotfixes = [new HotfixInfo("KB5062553", "Security Update", DateTime.Today.AddDays(-9)), new HotfixInfo("KB5063666", "Update", DateTime.Today.AddDays(-9)), new HotfixInfo("KB5058499", "Security Update", DateTime.Today.AddDays(-38))],
        Antivirus = "Microsoft Defender Antivirus",
        DefenderSignatures = "Defender real-time on, signatures 6h 12m old",
    };
}
