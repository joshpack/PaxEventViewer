using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PaxEventViewer.Core;
using PaxEventViewer.Views;

namespace PaxEventViewer;

/// <summary>
/// --self-test DIR [--sample]: checks the rules (every query runs against this PC's logs, read only), the message
/// parsing on recorded events, the snapshot, the ticket text and HTML report, window placement and the single-instance
/// hand-off, and renders every page to a PNG on the headless platform (nothing on screen). --sample shows made-up data,
/// for README screenshots. Exit code 0 = all passed.
/// </summary>
internal static class SelfTest
{
    public static string? Dir { get; set; }
    public static bool Sample { get; set; }
    public static int ExitCode { get; private set; } = 1;

    private static readonly StringBuilder R = new();
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        R.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : ": " + detail)}");
        if (!ok) _failed++;
    }

    public static async Task RunAsync()
    {
        R.AppendLine($"PAX Event Viewer {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)} self-test, {(Sample ? "sample data" : "this PC")}, {DateTime.Now:yyyy-MM-dd HH:mm}");
        R.AppendLine($"Elevated: {Elevation.IsElevated}");
        R.AppendLine();
        try
        {
            RuleChecks();
            Parsing();
            var (snap, dig) = Sample ? (Snapshot.Sample(), Events.Sample(TimeSpan.FromDays(7))) : await RealData();
            Reports(snap, dig);
            await Render(snap, dig);
            await Placement();
            SingleInstanceCheck();
        }
        catch (Exception ex)
        {
            Check("no crash", false, ex.ToString());
        }

        R.AppendLine();
        R.AppendLine(_failed == 0 ? "RESULT: all checks passed" : $"RESULT: {_failed} check(s) FAILED");
        ExitCode = _failed == 0 ? 0 : 1;
        await File.WriteAllTextAsync(Path.Combine(Dir!, "report.txt"), R.ToString());
    }

    private static void RuleChecks()
    {
        R.AppendLine("== Rules ==");
        var rules = Events.Rules;
        Check($"{rules.Length} rules", rules.Length >= 15);
        Check("Rule ids are unique", rules.Select(r => r.Id).Distinct().Count() == rules.Length);
        Check("Every rule explains itself and says what to try", rules.All(r => r.Explain.Length > 30 && r.Fix.Length > 30));
        var failures = new List<string>();
        foreach (var r in rules.Where(r => !r.NeedsAdmin || Elevation.IsElevated))
        {
            try
            {
                _ = Events.Query(r.Log, r.XPath(TimeSpan.FromDays(1)), 1).ToList();
            }
            catch (System.Diagnostics.Eventing.Reader.EventLogNotFoundException)
            {
            }
            catch (Exception ex)
            {
                failures.Add($"{r.Id}: {ex.Message}");
            }
        }

        Check("Every rule's query is accepted by Windows", failures.Count == 0, string.Join("; ", failures));
        R.AppendLine();
    }

    private static EventItem Ev(string ruleId, string message, params string[] props)
    {
        var r = Events.Rules.First(x => x.Id == ruleId);
        var e = new EventItem { Message = message, Properties = props.ToList(), Provider = r.Providers[0], Id = r.Ids[0], Log = r.Log, Time = DateTime.Now };
        Events.Describe(r, e);
        return e;
    }

    private static void Parsing()
    {
        R.AppendLine("== Reading messages (recorded examples) ==");
        var e = Ev("bsod", "The computer has rebooted from a bugcheck.  The bugcheck was: 0x0000009f (0x0000000000000003, 0xffffcb0fa9f4e060). A dump was saved in: C:\\WINDOWS\\MEMORY.DMP.");
        Check("Blue screen code and name", e.Summary == "Blue screen 0x0000009f DRIVER_POWER_STATE_FAILURE", e.Summary);
        e = Ev("unexpected-shutdown", "x", "0", "0", "0", "0", "0", "0", "133458812345");
        Check("Unexpected shutdown: power button", e.Key == "power button held", e.Summary);
        e = Ev("unexpected-shutdown", "x", "278", "0", "0", "0", "0", "0", "0");
        Check("Unexpected shutdown after a blue screen", e.Summary.Contains("0x00000116") && e.Summary.Contains("VIDEO_TDR_FAILURE"), e.Summary);
        e = Ev("app-crash", "Faulting application name: OUTLOOK.EXE", "OUTLOOK.EXE", "16.0.1", "0", "mso20win32client.dll", "16.0", "0", "c0000005", "0x1");
        Check("App crash: app, module and code", e.Summary == "OUTLOOK.EXE crashed in mso20win32client.dll (0xc0000005, access violation)", e.Summary);
        e = Ev("service", "The Print Spooler service terminated unexpectedly.  It has done this 2 time(s).");
        e.Id = 7031;
        Events.Describe(Events.Rules.First(r => r.Id == "service"), e);
        Check("Service failure", e.Key == "Print Spooler" && e.Summary == "Print Spooler stopped unexpectedly", e.Summary);
        e = Ev("update-failed", "Installation Failure: Windows failed to install the following update with error 0x800f0922: 2025-10 Cumulative Update for Windows 11 (KB5066835).");
        Check("Failed update: title and code", e.Key.Contains("KB5066835") && e.Summary.EndsWith("0x800f0922"), e.Summary);
        e = Ev("disk", "The device, \\Device\\Harddisk1\\DR1, has a bad block.");
        Check("Disk error device", e.Key == "Disk 1", e.Key);
        e = Ev("gpu-reset", "Display driver nvlddmkm stopped responding and has successfully recovered.");
        Check("Graphics driver reset", e.Key == "nvlddmkm", e.Key);
        e = Ev("network", "Name resolution for the name fileserver.contoso.local timed out after none of the configured DNS servers responded.");
        Check("DNS timeout name", e.Key == "fileserver.contoso.local", e.Key);
        e = Ev("failed-logon", "An account failed to log on.", "", "", "", "", "", "alex", "CONTOSO", "0xc000006d", "", "0xc000006a", "3", "", "", "", "", "", "", "", "", "192.168.10.88");
        Check("Failed sign-in reason and address", e.Summary == "alex wrong password from 192.168.10.88", e.Summary);
        e = Ev("restarts", "x", @"C:\WINDOWS\servicing\TrustedInstaller.exe (PC)", "PC", "Operating System: Upgrade (Planned)", "0x80020003", "restart", "", @"NT AUTHORITY\SYSTEM");
        Check("Restart: who and why", e.Summary.StartsWith(@"restart by NT AUTHORITY\SYSTEM via TrustedInstaller.exe"), e.Summary);
        e = Ev("app-crash", "garbage");
        Check("A message in an unexpected shape doesn't break", e.Key.Length > 0 && e.Summary.Length > 0, e.Summary);
        R.AppendLine();
    }

    private static async Task<(Snapshot, Digest)> RealData()
    {
        R.AppendLine("== This PC (read only) ==");
        var clock = Stopwatch.StartNew();
        var snap = await Task.Run(Snapshot.Take);
        Check($"Snapshot in {clock.ElapsedMilliseconds} ms", snap.OsName.Length > 0 && snap.Disks.Count > 0 && snap.RamTotal > 0,
            snap.Problems.Count > 0 ? "partly: " + string.Join("; ", snap.Problems) : null);
        R.AppendLine($"    {snap.OsName} {snap.OsVersion} build {snap.OsBuild}, up {Snapshot.Ago(snap.Uptime)}, pending reboot: {(snap.PendingReboot.Count == 0 ? "no" : string.Join(", ", snap.PendingReboot))}");
        R.AppendLine($"    {snap.Disks.Count} disk(s), {snap.Adapters.Count} adapter(s), {snap.Hotfixes.Count} hotfix(es), antivirus: {snap.Antivirus}");
        clock.Restart();
        var dig = await Task.Run(() => Events.Read(TimeSpan.FromDays(7), Elevation.IsElevated));
        Check($"7-day digest in {clock.ElapsedMilliseconds} ms", clock.ElapsedMilliseconds < 30000);
        foreach (var r in dig.Results)
        {
            R.AppendLine($"    {r.Rule.Id,-20} {(r.Error is not null ? r.Error : r.Count + (r.Capped ? "+" : ""))}{(r.Count > 0 ? "  e.g. " + r.Items[0].Summary : "")}");
            // PAXEV_DEBUG=1: the raw text of each first match, for checking the parsing against this PC's events.
            if (Environment.GetEnvironmentVariable("PAXEV_DEBUG") == "1" && r.Count > 0)
                R.AppendLine("        msg: " + Events.Trim(r.Items[0].Message.Replace("\n", " / "), 200) + " props: " + string.Join("|", r.Items[0].Properties.Take(4)));
        }
        var unknown = dig.Results.Where(r => r.Error is not null && !(r.Rule.NeedsAdmin && !Elevation.IsElevated)).Select(r => $"{r.Rule.Id}: {r.Error}").ToList();
        Check("Every allowed rule could be read", unknown.Count == 0, string.Join("; ", unknown));
        clock.Restart();
        var browse = await Task.Run(() => Events.Browse("System", [1, 2, 3], TimeSpan.FromDays(7), null, 500));
        Check($"Browse System (errors and warnings, 7 days): {browse.Count} in {clock.ElapsedMilliseconds} ms", true);
        R.AppendLine();
        return (snap, dig);
    }

    private static void Reports(Snapshot s, Digest d)
    {
        R.AppendLine("== Ticket text and report ==");
        var text = Report.TicketText(s, d);
        Check("Ticket text has the PC, Windows, disks and problems", text.Contains(s.ComputerName) && text.Contains("Windows:") && text.Contains("Disk ") && text.Contains("Problems in the last 7 days"));
        File.WriteAllText(Path.Combine(Dir!, "ticket.txt"), text);
        var html = Report.Html(s, d);
        Check("HTML report is self-contained", html.StartsWith("<!doctype html>") && !html.Contains("<script") && !html.Contains("http"));
        File.WriteAllText(Path.Combine(Dir!, "report.html"), html);
        R.AppendLine();
    }

    private static async Task Render(Snapshot s, Digest d)
    {
        R.AppendLine("== Window (headless renderer: nothing is shown on screen) ==");
        var window = new MainWindow(elevated: Sample ? false : null, autoLoad: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            ShowInTaskbar = false,
            SelfTestMode = true,
        };
        window.Show();
        window.SetData(s, d);
        if (Sample)
        {
            var items = d.Results.SelectMany(r => r.Items).OrderByDescending(e => e.Time).ToList();
            items.Insert(2, new EventItem { Time = DateTime.Now.AddMinutes(-40), Log = "System", Provider = "Microsoft-Windows-Kernel-General", Id = 16, Level = 3, Message = "The access history in hive \\??\\C:\\Users\\alex\\ntuser.dat was cleared updating 12 keys and creating 3 modified pages." });
            window.SetBrowse(items);
        }
        else
        {
            await window.BrowseAsync();
        }

        string[] files = ["dashboard", "problems", "all-events"];
        for (var i = 0; i < MainWindow.PageNames.Length; i++)
        {
            window.ShowPage(MainWindow.PageNames[i]);
            window.Width = 1180;
            window.Height = 800;
            window.UpdateLayout();
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var bmp = new RenderTargetBitmap(new PixelSize(1180, 800));
            bmp.Render(window);
            var file = Path.Combine(Dir!, files[i] + ".png");
            bmp.Save(file, PngBitmapEncoderOptions.Default);
            Check($"Rendered {MainWindow.PageNames[i]}", new FileInfo(file).Length > 10_000, file);
        }

        var all = window.GetLogicalDescendants().ToList();
        Check("Title bar: icon, name, three caption buttons", window.WindowDecorations == WindowDecorations.BorderOnly
                                                                && all.OfType<TextBlock>().Any(t => t.Text == window.Title)
                                                                && all.OfType<Button>().Count(b => b.Classes.Contains("pax-caption")) == 3);
        window.Close();
        R.AppendLine();
    }

    private static MainWindow OffScreen() => new(elevated: false, autoLoad: false)
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(-30000, -30000),
        ShowActivated = false,
        ShowInTaskbar = false,
        SelfTestMode = true,
    };

    private static async Task Placement()
    {
        R.AppendLine("== Window placement ==");
        const string key = "selftest";
        var file = Path.Combine(Program.DataFolder, "windows.json");
        if (File.Exists(file)) File.Delete(file);
        var a = OffScreen();
        PaxStandard.PaxChrome.RememberPlacement(a, key);
        a.Show();
        await Task.Delay(50);
        a.Width = 1000;
        a.Height = 640;
        a.Position = new PixelPoint(-29000, -29000);
        a.UpdateLayout();
        await Task.Delay(50);
        a.Close();
        var json = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
        Check("Placement saved", json.Contains("\"selftest\"") && json.Contains("\"Width\":1000") && json.Contains("\"Height\":640"), json);
        var b = OffScreen();
        PaxStandard.PaxChrome.RememberPlacement(b, key);
        Check("Placement restored (size), off-screen spot ignored", b.Width == 1000 && b.Height == 640 && b.Position == new PixelPoint(-30000, -30000), $"{b.Width}x{b.Height} at {b.Position}");
        R.AppendLine();
    }

    private static void SingleInstanceCheck()
    {
        R.AppendLine("== Single instance ==");
        var name = "PaxEventViewer-selftest-" + Guid.NewGuid().ToString("N")[..8];
        string[]? got = null;
        using var done = new ManualResetEventSlim();
        using (SingleInstance.Listen(name, args => { got = args; done.Set(); }))
        {
            var sent = SingleInstance.Send(name, ["--from-second-start"]);
            done.Wait(3000);
            Check("Second start reaches the running copy", sent && got is ["--from-second-start"], got is null ? "nothing received" : string.Join("|", got));
        }

        R.AppendLine();
    }
}
