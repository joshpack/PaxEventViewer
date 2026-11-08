using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PaxEventViewer.Core;

namespace PaxEventViewer;

internal static class Program
{
    public const string AppId = "PaxEventViewer";

    /// <summary>%APPDATA%\PaxEventViewer (window placement), or PAXEV_DATA (tests).</summary>
    public static string DataFolder =>
        Environment.GetEnvironmentVariable("PAXEV_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxEventViewer");

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? After(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (After(args, "--write-icon") is { } icon)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            BrandIcon.Write(icon);
            return 0;
        }

        if (After(args, "--self-test") is { } testDir)
        {
            SelfTest.Dir = Path.GetFullPath(testDir);
            SelfTest.Sample = Has(args, "--sample");
            Directory.CreateDirectory(SelfTest.Dir);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAXEV_DATA")))
                Environment.SetEnvironmentVariable("PAXEV_DATA", Path.Combine(SelfTest.Dir, "data"));
            PaxStandard.PaxChrome.Configure(AppId, DataFolder);
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            using var done = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                await SelfTest.RunAsync();
                done.Cancel();
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return SelfTest.ExitCode;
        }

        // --report file.html|file.txt [--days N]: no window, for scripts and remote sessions.
        if (After(args, "--report") is { } report)
        {
            var days = int.TryParse(After(args, "--days"), out var n) && n > 0 ? n : 7;
            try
            {
                var snap = Snapshot.Take();
                var digest = Events.Read(TimeSpan.FromDays(days), Elevation.IsElevated);
                var text = report.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Report.TicketText(snap, digest) : Report.Html(snap, digest);
                File.WriteAllText(Path.GetFullPath(report), text);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        if (!SingleInstance.Claim(SingleInstance.Name(AppId), args))
            return 0;

        PaxStandard.PaxChrome.Configure(AppId, DataFolder);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
