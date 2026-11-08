using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PaxEventViewer.Core;

/// <summary>Runs as the user; the Security log and a few event details need administrator rights, so the app can restart itself elevated.</summary>
public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static ProcessStartInfo RestartInfo(string[] args) => new()
    {
        FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PaxEventViewer.exe"),
        Arguments = string.Join(" ", args.Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)).Select(Quote).Append("--elevated")),
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = AppContext.BaseDirectory,
    };

    private static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    /// <summary>Starts an elevated copy. Null when it started; otherwise why not (1223 = the prompt was cancelled).</summary>
    public static string? RestartElevated(string[] args)
    {
        try
        {
            using var p = Process.Start(RestartInfo(args));
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "cancelled";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
