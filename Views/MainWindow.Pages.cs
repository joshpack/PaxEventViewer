using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxEventViewer.Core;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace PaxEventViewer.Views;

public sealed partial class MainWindow
{
    private readonly WrapPanel _tiles = new() { ItemSpacing = 10, LineSpacing = 10, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _dashLeft = new() { Spacing = 12 };
    private readonly StackPanel _dashRight = new() { Spacing = 12 };
    private TextBlock? _dashSub;
    private readonly StackPanel _problemList = new() { Spacing = 10, Margin = new Thickness(0, 0, 14, 8) };
    private TextBlock? _problemSub;
    private readonly TextBox _problemSearch = new() { PlaceholderText = "Filter (app, service, code…)", Width = 240 };
    private readonly CheckBox _showInfo = new() { Content = "Restarts too", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Tile → rule id it opens on the Problems page.</summary>
    private static readonly (string Rule, string Label)[] TileRules =
    [
        ("bsod", "Blue screens"), ("unexpected-shutdown", "Unexpected shutdowns"), ("app-crash", "App crashes"),
        ("disk", "Disk errors"), ("update-failed", "Failed updates"), ("hardware", "Hardware errors"),
    ];

    // ---------- Dashboard ----------

    private void BuildDashboard()
    {
        var copy = Btn("Copy for ticket", primary: true, "Copies the PC snapshot and a summary of problems as plain text.");
        copy.Click += async (_, _) => await CopyTicketAsync();
        var save = Btn("Save report…", tip: "An HTML page (or .txt) with everything on this page and the Problems page.");
        save.Click += async (_, _) => await SaveReportAsync();
        var refresh = Btn("Refresh", tip: "F5");
        refresh.Click += async (_, _) => await RefreshAsync();
        var header = Header("Dashboard", "Reading…", WindowBox(), refresh, save, copy);
        _dashSub = header.Tag as TextBlock;

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        cols.Children.Add(_dashLeft);
        Grid.SetColumn(_dashRight, 2);
        cols.Children.Add(_dashRight);
        var body = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        body.Children.Add(_tiles);
        body.Children.Add(cols);
        _pages["Dashboard"] = PageOf(header, new ScrollViewer { Content = body });
    }

    private void FillDashboard()
    {
        if (Snap is not { } s) return;
        _dashSub!.Text = $"{s.ComputerName} · {s.User} · snapshot at {s.TakenAt:HH:mm}";

        _tiles.Children.Clear();
        foreach (var (rule, label) in TileRules)
        {
            var res = Dig?[rule];
            var n = res?.Count ?? 0;
            var sev = Events.Rules.First(r => r.Id == rule).Severity;
            _tiles.Children.Add(Tile(n == 0 ? "0" : n + (res!.Capped ? "+" : ""), label, n == 0 ? Good : SeverityBrush(sev),
                res?.Error ?? (n == 0 ? "None" : $"Last {res!.Last:ddd HH:mm}"), () => OpenProblem(rule)));
        }

        var reboot = s.PendingReboot.Count > 0;
        _tiles.Children.Add(Tile(reboot ? "Yes" : "No", "Pending reboot", reboot ? Brush("#F5B14C") : Good,
            reboot ? string.Join(", ", s.PendingReboot) : $"Up {Snapshot.Ago(s.Uptime)}", null));

        _dashLeft.Children.Clear();
        _dashRight.Children.Clear();

        var pc = Section("THIS PC");
        Row(pc, "Computer", $"{s.ComputerName}");
        Row(pc, s.DomainJoined ? "Domain" : "Workgroup", s.Domain);
        Row(pc, "Signed in", s.User);
        Row(pc, "Windows", $"{s.OsName} {s.OsVersion}");
        Row(pc, "Build", s.OsBuild + (s.InstallDate is { } i ? $" · installed {i:yyyy-MM-dd}" : ""));
        Row(pc, "Uptime", $"{Snapshot.Ago(s.Uptime)} (since {s.LastBoot:ddd dd MMM HH:mm})", s.Uptime.TotalDays > 14 ? "warn" : null);
        Row(pc, "Pending reboot", reboot ? "Yes: " + string.Join(", ", s.PendingReboot) : "No", reboot ? "warn" : null);
        _dashLeft.Children.Add(Card(pc));

        var hw = Section("HARDWARE");
        Row(hw, "Model", $"{s.Manufacturer} {s.Model}".Trim());
        Row(hw, "Serial", s.Serial);
        Row(hw, "BIOS", s.Bios);
        Row(hw, "Processor", s.Cpu.Length == 0 ? "" : $"{s.Cpu} · {s.Cores} cores / {s.Threads} threads");
        if (s.RamTotal > 0) Bar(hw, "Memory", $"{Snapshot.Size(s.RamTotal - s.RamFree)} of {Snapshot.Size(s.RamTotal)} in use", s.RamUsedPercent, s.RamUsedPercent > 90);
        foreach (var d in s.Disks)
            Bar(hw, $"Disk {d.Name}", $"{Snapshot.Size(d.Free)} free of {Snapshot.Size(d.Total)}{(d.Label.Length > 0 ? " · " + d.Label : "")}", 100 - d.FreePercent, d.FreePercent < 10);
        _dashLeft.Children.Add(Card(hw));

        var net = Section("NETWORK");
        var adapters = s.Adapters.Where(a => a.Kind != "Virtual").ToList();
        if (adapters.Count == 0) net.Children.Add(new TextBlock { Text = "No connected network adapter.", Classes = { "muted" } });
        foreach (var a in adapters)
        {
            net.Children.Add(new TextBlock { Text = $"{a.Name} · {a.Kind}{(a.Speed.Length > 0 ? " · " + a.Speed : "")}", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
            Row(net, "IPv4", a.Ipv4 + (a.Dhcp ? " (DHCP)" : " (static)"));
            Row(net, "Gateway", a.Gateway.Length > 0 ? a.Gateway : "none", a.Gateway.Length == 0 ? "warn" : null);
            Row(net, "DNS", a.Dns);
            Row(net, "MAC", a.Mac);
        }

        _dashRight.Children.Add(Card(net));

        var sec = Section("SECURITY & UPDATES");
        Row(sec, "Antivirus", s.Antivirus, s.Antivirus == "None reported" ? "bad" : null);
        if (s.DefenderSignatures is { } sig) Row(sec, "Defender", sig, sig.Contains("OFF") ? "bad" : null);
        if (s.Hotfixes.Count == 0) Row(sec, "Updates", "None listed");
        foreach (var h in s.Hotfixes.Take(4)) Row(sec, h.Id, $"{h.Description}{(h.InstalledOn is { } d ? $" · {d:yyyy-MM-dd}" : "")}");
        if (s.Hotfixes.FirstOrDefault()?.InstalledOn is { } last && (DateTime.Now - last).TotalDays > 45)
            Row(sec, "Note", $"No update installed for {(int)(DateTime.Now - last).TotalDays} days", "warn");
        _dashRight.Children.Add(Card(sec));

        if (Dig?["restarts"] is { Count: > 0 } rs)
        {
            var rst = Section("RECENT RESTARTS");
            foreach (var e in rs.Items.Take(4)) Row(rst, e.Time.ToString("ddd dd MMM HH:mm"), e.Summary);
            _dashRight.Children.Add(Card(rst));
        }

        if (s.Problems.Count > 0)
        {
            var p = Section("COULDN'T READ");
            foreach (var x in s.Problems) p.Children.Add(new TextBlock { Text = x, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            _dashRight.Children.Add(Card(p));
        }
    }

    private Control Tile(string value, string label, IBrush color, string sub, Action? click)
    {
        var sp = new StackPanel { Spacing = 1 };
        sp.Children.Add(new TextBlock { Text = value, FontSize = 30, FontWeight = FontWeight.Bold, Foreground = color });
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        sp.Children.Add(new TextBlock { Text = sub, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Width = 196, Child = sp, Cursor = click is null ? null : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        if (click is not null)
        {
            ToolTip.SetTip(b, "Open on the Problems page");
            b.PointerReleased += (_, _) => click();
        }

        return b;
    }

    private static StackPanel Section(string title)
    {
        var sp = new StackPanel { Spacing = 5 };
        sp.Children.Add(new TextBlock { Text = title, Classes = { "label" }, Margin = new Thickness(0, 0, 0, 4) });
        return sp;
    }

    private static void Row(StackPanel p, string label, string value, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
        g.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12.5 });
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        if (kind is not null) v.Foreground = Brush(kind == "bad" ? "#F2666A" : "#F5B14C");
        v.ContextMenu = CopyMenu(value);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        p.Children.Add(g);
    }

    private static ContextMenu CopyMenu(string text)
    {
        var item = new MenuItem { Header = "Copy" };
        item.Click += async (s, _) =>
        {
            if (TopLevel.GetTopLevel(s as Visual)?.Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
        };
        return new ContextMenu { Items = { item } };
    }

    private static void Bar(StackPanel p, string label, string value, double percent, bool warn)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        g.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12.5 });
        var v = new TextBlock { Text = value, FontSize = 12.5 };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, Margin = new Thickness(0, 4, 0, 2), MinHeight = 6, Height = 6 };
        if (warn) bar.Foreground = Brush("#F5B14C");
        Grid.SetColumn(bar, 1);
        Grid.SetRow(bar, 1);
        g.Children.Add(bar);
        p.Children.Add(g);
    }

    // ---------- Problems ----------

    private void BuildProblems()
    {
        var refresh = Btn("Refresh", tip: "F5");
        refresh.Click += async (_, _) => await RefreshAsync();
        var copy = Btn("Copy for ticket", primary: true);
        copy.Click += async (_, _) => await CopyTicketAsync();
        var header = Header("Problems", "What went wrong on this PC, in plain English, with what to try.", WindowBox(), refresh, copy);
        _problemSub = header.Tag as TextBlock;
        _problemSearch.TextChanged += (_, _) => FillProblems();
        _showInfo.IsCheckedChanged += (_, _) => FillProblems();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        filters.Children.Add(_problemSearch);
        filters.Children.Add(_showInfo);
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(filters);
        _pages["Problems"] = PageOf(top, new ScrollViewer { Content = _problemList });
    }

    private string? _openRule;

    private void OpenProblem(string rule)
    {
        _openRule = rule;
        _problemSearch.Text = "";
        FillProblems();
        ShowPage("Problems");
    }

    private void FillProblems()
    {
        _problemList.Children.Clear();
        if (Dig is not { } d)
        {
            _problemList.Children.Add(new TextBlock { Text = "Reading the event logs…", Classes = { "muted" } });
            return;
        }

        var q = _problemSearch.Text?.Trim() ?? "";
        bool Match(EventItem e) => q.Length == 0 || e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase);
        var found = d.Results.Where(r => r.Count > 0 && (r.Rule.Severity != Severity.Info || _showInfo.IsChecked == true))
            .Select(r => (r, items: r.Items.Where(Match).ToList())).Where(x => x.items.Count > 0)
            .OrderBy(x => x.r.Rule.Severity).ThenByDescending(x => x.items.Count).ToList();
        var total = found.Where(x => x.r.Rule.Severity != Severity.Info).Sum(x => x.items.Count);
        _problemSub!.Text = $"{total} problem event(s) of {found.Count(x => x.r.Rule.Severity != Severity.Info)} kind(s) in the {Report.WindowName(d.Window).Replace("1 day", "24 hours")}, read at {d.TakenAt:HH:mm}. Click one to see each event.";

        foreach (var (r, items) in found) _problemList.Children.Add(ProblemCard(r, items, r.Rule.Id == _openRule || found.Count == 1));
        _openRule = null;

        var clear = d.Results.Where(r => r.Count == 0 && r.Error is null && r.Rule.Severity != Severity.Info).ToList();
        if (clear.Count > 0 && q.Length == 0)
        {
            var wp = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var r in clear) wp.Children.Add(Pill("✓ " + r.Rule.Title, "good"));
            var sp = new StackPanel { Spacing = 8 };
            sp.Children.Add(new TextBlock { Text = "ALL CLEAR", Classes = { "label" } });
            sp.Children.Add(wp);
            _problemList.Children.Add(Card(sp));
        }

        var errors = d.Results.Where(r => r.Error is not null).ToList();
        if (errors.Count > 0 && q.Length == 0)
        {
            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = "NOT CHECKED", Classes = { "label" } });
            foreach (var r in errors) sp.Children.Add(new TextBlock { Text = $"{r.Rule.Title}: {r.Error}", Classes = { "muted" }, FontSize = 12 });
            if (errors.Any(r => r.Rule.NeedsAdmin) && !_elevated)
            {
                var b = Btn("Restart as administrator");
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Click += (_, _) => RestartElevated();
                sp.Children.Add(b);
            }

            _problemList.Children.Add(Card(sp));
        }

        if (found.Count == 0 && q.Length > 0)
            _problemList.Children.Insert(0, new TextBlock { Text = $"Nothing matches \"{q}\".", Classes = { "muted" } });
    }

    private Control ProblemCard(RuleResult r, List<EventItem> items, bool open)
    {
        var rule = r.Rule;
        var sp = new StackPanel { Spacing = 8 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var dot = new Ellipse { Width = 10, Height = 10, Fill = SeverityBrush(rule.Severity), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        title.Children.Add(dot);
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = rule.Title, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(Pill($"{items.Count}{(r.Capped ? "+" : "")}", SeverityClass(rule.Severity)));
        name.Children.Add(Pill(rule.Severity.ToString(), SeverityClass(rule.Severity)));
        Grid.SetColumn(name, 1);
        title.Children.Add(name);
        var last = new TextBlock { Text = $"last {items.Max(i => i.Time):ddd dd MMM HH:mm}", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(last, 2);
        title.Children.Add(last);
        sp.Children.Add(title);
        sp.Children.Add(new TextBlock { Text = rule.Explain, Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });

        var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var k in items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(8))
        {
            var chip = new Border { Classes = { "pill" }, Child = new TextBlock { Text = k.Count() > 1 ? $"{k.Key}  ×{k.Count()}" : k.Key } };
            chips.Children.Add(chip);
        }

        sp.Children.Add(chips);
        var fix = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        fix.Inlines!.Add(new Avalonia.Controls.Documents.Run("Try: ") { FontWeight = FontWeight.Bold, Foreground = Good });
        fix.Inlines.Add(new Avalonia.Controls.Documents.Run(rule.Fix));
        sp.Children.Add(fix);

        // Each event, newest first; clicking one shows its full text.
        var detail = new TextBox { Classes = { "mono" }, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxHeight = 240, Margin = new Thickness(0, 6, 0, 0) };
        var list = new StackPanel { Spacing = 0 };
        foreach (var e in items.Take(60))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 3) };
            g.Children.Add(new TextBlock { Text = e.Time.ToString("ddd dd MMM HH:mm:ss"), Classes = { "mono" }, Foreground = Muted });
            var t = new TextBlock { Text = e.Summary, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var row = new Border { Child = g, Background = Brushes.Transparent, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(6) };
            row.PointerEntered += (_, _) => row.Background = Brush("#1C2230");
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            row.PointerReleased += (_, _) =>
            {
                detail.Text = $"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.LevelName}  {e.Log} / {e.Provider}  event {e.Id}\n\n{e.Message}";
                detail.IsVisible = true;
            };
            list.Children.Add(row);
        }

        if (items.Count > 60) list.Children.Add(new TextBlock { Text = $"… and {items.Count - 60} more (All events shows every one).", Classes = { "muted" }, FontSize = 12, Margin = new Thickness(6, 4) });
        var expander = new Expander
        {
            Header = $"Show the {Math.Min(items.Count, 60)} event(s)",
            IsExpanded = open,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Children = { list, detail } },
        };
        sp.Children.Add(expander);
        return Card(sp);
    }
}
