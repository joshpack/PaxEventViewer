using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using pxdEventViewer.Core;

namespace pxdEventViewer;

internal static class Program
{
    public const string AppId = "pxdEventViewer";

    /// <summary>Where updates come from: the release asset this app is installed from (the Toolkit catalog's asset).</summary>
    public const string UpdateRepo = "pxdtek/pxdEventViewer";
    public const string UpdateAsset = @"^pxdEventViewer-.*-win-x64\.zip$";

    /// <summary>%APPDATA%\pxdEventViewer (window placement), or PXDEV_DATA (tests).</summary>
    public static string DataFolder =>
        Environment.GetEnvironmentVariable("PXDEV_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

    /// <summary>
    /// One-time migration (1.1.0): moves the settings folder an older version kept under its former name in
    /// <paramref name="appDataRoot"/> to <c>&lt;root&gt;\pxdEventViewer</c>. Safe to repeat; never throws. True if it moved.
    /// The app has no autostart, file associations or shortcuts of its own; the mutex and pipe names just change.
    /// </summary>
    public static bool MigrateData(string appDataRoot) =>
        pxdStandard.pxdMigrate.Folder(Path.Combine(appDataRoot, pxdStandard.pxdMigrate.FormerName(AppId)), Path.Combine(appDataRoot, AppId));

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? After(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // pxd standard rule 6: an installed copy updates itself before it opens (a no-op for dev builds, --self-test,
        // PXD_NO_UPDATE=1, offline). Not for the build step (--write-icon), scripted reports (--report: the caller
        // waits for this process) or the elevated restart (--elevated: the first start just checked).
        if (!Has(args, "--write-icon") && !Has(args, "--report") && !Has(args, "--elevated")
            && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return 0;

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
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDEV_DATA")))
                Environment.SetEnvironmentVariable("PXDEV_DATA", Path.Combine(SelfTest.Dir, "data"));
            pxdStandard.pxdChrome.Configure(AppId, DataFolder);
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

        // Before anything reads settings; skipped when PXDEV_DATA points elsewhere (tests)
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDEV_DATA")))
            MigrateData(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        pxdStandard.pxdChrome.Configure(AppId, DataFolder);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
