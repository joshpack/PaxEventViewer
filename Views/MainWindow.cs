using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using pxdEventViewer.Core;

namespace pxdEventViewer.Views;

/// <summary>
/// The whole app: a sidebar with Dashboard (PC snapshot + health tiles), Problems (plain-English event digest) and
/// All events (browse any log). Built in code, pxd neon theme.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static IBrush Brush(string c) => new SolidColorBrush(Color.Parse(c));
    private static readonly IBrush Muted = Brush("#8A92A6");
    private static readonly IBrush Good = Brush("#19F58C");
    private static readonly IBrush TextB = Brush("#E8EAF0");

    public static readonly string[] PageNames = ["Dashboard", "Problems", "All events"];
    private static readonly string[] PageGlyphs = ["▦", "⚠", "≡"];
    public static readonly (string Name, TimeSpan Span)[] Windows =
        [("Last 24 hours", TimeSpan.FromHours(24)), ("Last 7 days", TimeSpan.FromDays(7)), ("Last 30 days", TimeSpan.FromDays(30))];

    private readonly bool _elevated;
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, Button> _nav = [];
    private readonly Border _pageHost = new();
    private readonly Border _notice = new() { Classes = { "notice" }, IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Width = 160, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private string _page = "Dashboard";

    public Snapshot? Snap { get; private set; }
    public Digest? Dig { get; private set; }
    public TimeSpan Span { get; private set; } = TimeSpan.FromDays(7);

    public MainWindow() : this(elevated: null, autoLoad: true)
    {
    }

    public MainWindow(bool? elevated, bool autoLoad)
    {
        _elevated = elevated ?? Elevation.IsElevated;
        Title = _elevated ? "pxd Event Viewer (Administrator)" : "pxd Event Viewer";
        Icon = BrandIcon.Window();
        Width = 1180;
        Height = 800;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildDashboard();
        BuildProblems();
        BuildBrowse();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        body.Children.Add(BuildSidebar());
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22, 16, 22, 10) };
        Grid.SetColumn(main, 1);
        body.Children.Add(main);

        var ng = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        ng.Children.Add(_noticeText);
        var dismiss = new Button { Content = "Dismiss", Classes = { "ghost" }, MinHeight = 28, Padding = new Thickness(10, 2), Margin = new Thickness(8, 0, 0, 0) };
        dismiss.Click += (_, _) => _notice.IsVisible = false;
        Grid.SetColumn(dismiss, 1);
        ng.Children.Add(dismiss);
        _notice.Child = ng;
        main.Children.Add(_notice);
        Grid.SetRow(_pageHost, 1);
        main.Children.Add(_pageHost);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        bar.Children.Add(_progress);
        Grid.SetRow(bar, 2);
        main.Children.Add(bar);

        Content = pxdStandard.pxdChrome.Wrap(this, body, Title, BrandIcon.Render(36));
        ShowPage("Dashboard");
        _status.Text = "Ready.";
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) _ = RefreshAsync();
        };
        if (autoLoad) Opened += async (_, _) => await RefreshAsync();
    }

    // ---------- Shell ----------

    private Control BuildSidebar()
    {
        var dock = new DockPanel { Background = Brush("#0B0E13") };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 16, 16, 18) };
        head.Children.Add(new Image { Source = BrandIcon.Render(72), Width = 36, Height = 36 });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "pxd EVENT", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        names.Children.Add(new TextBlock { Text = "VIEWER", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        head.Children.Add(names);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var foot = new StackPanel { Spacing = 8, Margin = new Thickness(12, 8, 12, 14) };
        var pill = Pill(_elevated ? "Administrator" : "Standard user", _elevated ? "good" : "info");
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(pill, _elevated ? "Running as administrator: the Security log (failed sign-ins) can be read." : "Everything but the Security log can be read. Restart as administrator to include failed sign-ins.");
        foot.Children.Add(pill);
        if (!_elevated)
        {
            var re = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(re, "Adds failed sign-ins and the Security log.");
            re.Click += (_, _) => RestartElevated();
            foot.Children.Add(re);
        }

        foot.Children.Add(new TextBlock { Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(4, 2, 0, 0) });
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0) };
        for (var i = 0; i < PageNames.Length; i++)
        {
            var name = PageNames[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new TextBlock { Text = PageGlyphs[i], Width = 18, TextAlignment = TextAlignment.Center, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button
            {
                Content = row, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8), MinHeight = 40,
            };
            b.Click += (_, _) => ShowPage(name);
            _nav[name] = b;
            nav.Children.Add(b);
        }

        dock.Children.Add(new ScrollViewer { Content = nav });
        return dock;
    }

    public void RestartElevated()
    {
        var why = Elevation.RestartElevated([]);
        if (why is null)
        {
            SingleInstance.Release();
            Close();
        }
        else ShowNotice(why == "cancelled" ? "The administrator prompt was cancelled." : "Couldn't restart as administrator: " + why);
    }

    public void ShowPage(string name)
    {
        _page = name;
        _pageHost.Child = _pages[name];
        foreach (var (n, b) in _nav)
        {
            b.Background = n == name ? Brush("#142027") : Brushes.Transparent;
            b.Foreground = n == name ? Good : TextB;
        }

        if (name == "All events" && _browseItems.Count == 0 && !SelfTestMode && !_browsing) _ = BrowseAsync();
    }

    public bool SelfTestMode { get; set; }

    public void ShowNotice(string text, bool good = false)
    {
        _notice.Background = Brush(good ? "#0C2A1D" : "#2A2210");
        _notice.BorderBrush = Brush(good ? "#146B44" : "#7A5A1A");
        _noticeText.Foreground = Brush(good ? "#7DF5BC" : "#F5C97A");
        _noticeText.Text = text;
        _notice.IsVisible = true;
    }

    private void Busy(string? what)
    {
        _progress.IsVisible = what is not null;
        _status.Text = what ?? "Ready.";
    }

    // ---------- Loading ----------

    public async Task RefreshAsync()
    {
        if (_progress.IsVisible) return;
        Busy("Reading the PC and the event logs…");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var span = Span;
            var snapTask = Task.Run(Snapshot.Take);
            var digTask = Task.Run(() => Events.Read(span, _elevated));
            await Task.WhenAll(snapTask, digTask);
            SetData(snapTask.Result, digTask.Result);
            Busy(null);
            _status.Text = $"Read at {DateTime.Now:HH:mm} in {clock.Elapsed.TotalSeconds:0.0} s. F5 reads again.";
        }
        catch (Exception ex)
        {
            Busy(null);
            ShowNotice("Couldn't read everything: " + ex.Message);
        }
    }

    public void SetData(Snapshot s, Digest d)
    {
        Snap = s;
        Dig = d;
        Span = d.Window;
        FillDashboard();
        FillProblems();
    }

    private async Task ChangeWindowAsync(int index)
    {
        if (index < 0 || Windows[index].Span == Span) return;
        Span = Windows[index].Span;
        foreach (var box in _windowBoxes) box.SelectedIndex = index;
        if (SelfTestMode && Snap is not null) return;
        Busy($"Reading events from the {Windows[index].Name.ToLowerInvariant()}…");
        try
        {
            var span = Span;
            var d = await Task.Run(() => Events.Read(span, _elevated));
            Dig = d;
            FillDashboard();
            FillProblems();
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't read the event logs: " + ex.Message);
        }
        finally
        {
            Busy(null);
        }
    }

    private readonly List<ComboBox> _windowBoxes = [];

    private ComboBox WindowBox()
    {
        var box = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        foreach (var w in Windows) box.Items.Add(w.Name);
        box.SelectedIndex = 1;
        box.SelectionChanged += async (_, _) => await ChangeWindowAsync(box.SelectedIndex);
        _windowBoxes.Add(box);
        return box;
    }

    // ---------- Copy / save ----------

    public async Task CopyTicketAsync()
    {
        if (Snap is null) return;
        try
        {
            if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, Report.TicketText(Snap, Dig));
            ShowNotice("Snapshot and problem summary copied. Paste it into the ticket.", good: true);
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't copy to the clipboard: " + ex.Message);
        }
    }

    private async Task SaveReportAsync()
    {
        if (Snap is null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save report",
                SuggestedFileName = $"{Snap.ComputerName}-{DateTime.Now:yyyyMMdd-HHmm}.html",
                FileTypeChoices = [new FilePickerFileType("Web page") { Patterns = ["*.html"] }, new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            await File.WriteAllTextAsync(path, path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Report.TicketText(Snap, Dig) : Report.Html(Snap, Dig));
            ShowNotice("Report saved to " + path, good: true);
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't save the report: " + ex.Message);
        }
    }

    // ---------- Building blocks ----------

    public static Border Pill(string text, string kind) => new() { Classes = { "pill", kind }, Child = new TextBlock { Text = text } };

    private static Control Header(string title, string subtitle, params Control[] right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold });
        var sub = new TextBlock { Text = subtitle, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        left.Children.Add(sub);
        g.Children.Add(left);
        var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var c in right) r.Children.Add(c);
        Grid.SetColumn(r, 1);
        g.Children.Add(r);
        g.Tag = sub;
        return g;
    }

    private static Button Btn(string text, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = text };
        if (primary) b.Classes.Add("primary");
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    private static Border Card(Control child, Thickness? padding = null) => new() { Classes = { "card" }, Child = child, Padding = padding ?? new Thickness(16) };

    private static Control PageOf(Control header, Control body)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        g.Children.Add(header);
        Grid.SetRow(body, 1);
        g.Children.Add(body);
        return g;
    }

    private static string SeverityClass(Severity s) => s switch { Severity.Critical => "bad", Severity.Error => "warn", Severity.Warning => "warn", _ => "info" };

    private static IBrush SeverityBrush(Severity s) => s switch
    {
        Severity.Critical => Brush("#F2666A"),
        Severity.Error => Brush("#F5B14C"),
        Severity.Warning => Brush("#F5D76E"),
        _ => Brush("#00C8FF"),
    };
}
