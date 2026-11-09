using System.Net;
using System.Text;

namespace pxdEventViewer.Core;

/// <summary>The snapshot and digest as ticket-ready text, or as a self-contained HTML page.</summary>
public static class Report
{
    public static string WindowName(TimeSpan w) => w.TotalDays >= 1 ? $"{(int)w.TotalDays} day{(w.TotalDays >= 2 ? "s" : "")}" : $"{(int)w.TotalHours} hours";

    public static IEnumerable<(string Label, string Value)> PcRows(Snapshot s)
    {
        yield return ("Computer", $"{s.ComputerName} ({(s.DomainJoined ? "domain " : "")}{s.Domain})");
        yield return ("Signed in", s.User);
        yield return ("Windows", $"{s.OsName} {s.OsVersion} (build {s.OsBuild}){(s.InstallDate is { } i ? $", installed {i:yyyy-MM-dd}" : "")}");
        yield return ("Uptime", $"{Snapshot.Ago(s.Uptime)} (last boot {s.LastBoot:yyyy-MM-dd HH:mm})");
        yield return ("Pending reboot", s.PendingReboot.Count == 0 ? "No" : "Yes: " + string.Join(", ", s.PendingReboot));
        if (s.Manufacturer.Length + s.Model.Length > 0) yield return ("Model", $"{s.Manufacturer} {s.Model}".Trim());
        if (s.Serial.Length > 0) yield return ("Serial", s.Serial);
        if (s.Bios.Length > 0) yield return ("BIOS", s.Bios);
        if (s.Cpu.Length > 0) yield return ("Processor", $"{s.Cpu} ({s.Cores} cores, {s.Threads} threads)");
        if (s.RamTotal > 0) yield return ("Memory", $"{Snapshot.Size(s.RamTotal)}, {s.RamUsedPercent:0}% in use");
        foreach (var d in s.Disks)
            yield return ($"Disk {d.Name}", $"{Snapshot.Size(d.Free)} free of {Snapshot.Size(d.Total)} ({d.FreePercent:0}%){(d.Label.Length > 0 ? $", {d.Label}" : "")}");
        foreach (var a in s.Adapters.Where(a => a.Kind != "Virtual"))
            yield return ($"{a.Kind} ({a.Name})", $"{a.Ipv4}{(a.Dhcp ? " DHCP" : " static")}, gateway {(a.Gateway.Length > 0 ? a.Gateway : "none")}, DNS {(a.Dns.Length > 0 ? a.Dns : "none")}, MAC {a.Mac}{(a.Speed.Length > 0 ? ", " + a.Speed : "")}");
        yield return ("Antivirus", s.Antivirus + (s.DefenderSignatures is { } sig ? $" ({sig})" : ""));
        if (s.Hotfixes.Count > 0)
            yield return ("Last updates", string.Join(", ", s.Hotfixes.Take(3).Select(h => $"{h.Id}{(h.InstalledOn is { } d ? $" {d:yyyy-MM-dd}" : "")}")));
    }

    /// <summary>Plain text to paste into a ticket.</summary>
    public static string TicketText(Snapshot s, Digest? d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {s.ComputerName}: PC snapshot {s.TakenAt:yyyy-MM-dd HH:mm} ===");
        foreach (var (l, v) in PcRows(s)) sb.AppendLine($"{l + ":",-18} {v}");
        if (d is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"=== Problems in the last {WindowName(d.Window)} ===");
            var any = false;
            foreach (var r in d.Results.Where(r => r.Count > 0 && r.Rule.Severity != Severity.Info).OrderBy(r => r.Rule.Severity).ThenByDescending(r => r.Count))
            {
                any = true;
                sb.AppendLine($"- {r.Rule.Title}: {r.Count}{(r.Capped ? "+" : "")} (last {r.Last:MM-dd HH:mm}). " + string.Join("; ", r.Top(3).Select(k => k.Count > 1 ? $"{k.Key} x{k.Count}" : k.Key)));
            }

            if (!any) sb.AppendLine("- None found.");
            if (d["restarts"] is { Count: > 0 } rs)
                sb.AppendLine($"- Last restart: {rs.Items[0].Time:MM-dd HH:mm}, {rs.Items[0].Summary}");
            var skipped = d.Results.Where(r => r.Error is not null).Select(r => r.Rule.Title).ToList();
            if (skipped.Count > 0) sb.AppendLine($"(Not checked: {string.Join(", ", skipped)}.)");
        }

        return sb.ToString();
    }

    /// <summary>A self-contained HTML report (no scripts, no external files) to attach to a ticket.</summary>
    public static string Html(Snapshot s, Digest? d)
    {
        static string H(string x) => WebUtility.HtmlEncode(x);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(H($"{s.ComputerName} — pxd Event Viewer report")).Append("</title><style>")
            .Append("body{font:14px/1.45 Segoe UI,system-ui,sans-serif;background:#0e1015;color:#e8eaf0;margin:0;padding:28px}")
            .Append("h1{font-size:22px;margin:0 0 2px}h2{font-size:15px;letter-spacing:.06em;text-transform:uppercase;color:#8a92a6;margin:28px 0 10px}")
            .Append(".sub{color:#8a92a6;margin-bottom:18px}table{border-collapse:collapse;width:100%;background:#161922;border:1px solid #272c39;border-radius:10px;overflow:hidden}")
            .Append("td,th{padding:8px 12px;border-top:1px solid #222736;vertical-align:top;text-align:left}th{color:#8a92a6;font-weight:600;width:190px}")
            .Append(".crit{color:#f2666a}.err{color:#f5b14c}.warn{color:#f5d76e}.info{color:#00c8ff}.ok{color:#19f58c}.muted{color:#8a92a6}")
            .Append(".card{background:#161922;border:1px solid #272c39;border-radius:10px;padding:14px 16px;margin-bottom:10px}.card b{font-size:15px}")
            .Append("ul{margin:6px 0 0 18px;padding:0}li{margin:2px 0}@media print{body{background:#fff;color:#000}table,.card{background:#fff;border-color:#ccc}}")
            .Append("</style></head><body>");
        sb.Append("<h1>").Append(H(s.ComputerName)).Append("</h1><div class=\"sub\">PC snapshot ").Append(H(s.TakenAt.ToString("yyyy-MM-dd HH:mm"))).Append(" · pxd Event Viewer</div>");
        sb.Append("<table>");
        foreach (var (l, v) in PcRows(s))
        {
            var cls = l == "Pending reboot" && v != "No" ? " class=\"err\"" : "";
            sb.Append("<tr><th>").Append(H(l)).Append("</th><td").Append(cls).Append('>').Append(H(v)).Append("</td></tr>");
        }

        sb.Append("</table>");
        if (d is not null)
        {
            sb.Append("<h2>Problems in the last ").Append(H(WindowName(d.Window))).Append("</h2>");
            var found = d.Results.Where(r => r.Count > 0).OrderBy(r => r.Rule.Severity).ThenByDescending(r => r.Count).ToList();
            if (found.Count == 0) sb.Append("<div class=\"card ok\">Nothing found.</div>");
            foreach (var r in found)
            {
                var cls = r.Rule.Severity switch { Severity.Critical => "crit", Severity.Error => "err", Severity.Warning => "warn", _ => "info" };
                sb.Append("<div class=\"card\"><b class=\"").Append(cls).Append("\">").Append(H(r.Rule.Title)).Append(": ").Append(r.Count).Append(r.Capped ? "+" : "").Append("</b>")
                    .Append(" <span class=\"muted\">last ").Append(H($"{r.Last:yyyy-MM-dd HH:mm}")).Append("</span>")
                    .Append("<div class=\"muted\">").Append(H(r.Rule.Explain)).Append("</div><ul>");
                foreach (var k in r.Top(6)) sb.Append("<li>").Append(H(k.Key)).Append(k.Count > 1 ? $" <span class=\"muted\">×{k.Count}</span>" : "").Append("</li>");
                sb.Append("</ul><div style=\"margin-top:6px\"><b style=\"font-size:13px\">Try:</b> ").Append(H(r.Rule.Fix)).Append("</div></div>");
            }

            var skipped = d.Results.Where(r => r.Error is not null).ToList();
            if (skipped.Count > 0)
                sb.Append("<div class=\"muted\">Not checked: ").Append(H(string.Join(", ", skipped.Select(r => $"{r.Rule.Title} ({r.Error})")))).Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
