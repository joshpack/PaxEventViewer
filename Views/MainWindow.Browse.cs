using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using PaxEventViewer.Core;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace PaxEventViewer.Views;

public sealed partial class MainWindow
{
    private readonly ComboBox _logBox = new() { MinWidth = 230 };
    private readonly ComboBox _browseWindow = new() { MinWidth = 140 };
    private readonly CheckBox[] _levels =
    [
        new() { Content = "Critical", IsChecked = true }, new() { Content = "Error", IsChecked = true },
        new() { Content = "Warning", IsChecked = true }, new() { Content = "Information", IsChecked = false },
    ];
    private readonly TextBox _browseSearch = new() { PlaceholderText = "Text, source or event ID", Width = 210 };
    private readonly ListBox _browseList = new() { Classes = { "log" } };
    private readonly TextBlock _browseCount = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _detail = new() { Spacing = 8 };
    private List<EventItem> _browseItems = [];
    private bool _browsing;

    private static readonly (string Name, TimeSpan Span)[] BrowseWindows =
        [("Last hour", TimeSpan.FromHours(1)), ("Last 24 hours", TimeSpan.FromHours(24)), ("Last 7 days", TimeSpan.FromDays(7)), ("Last 30 days", TimeSpan.FromDays(30)), ("Last year", TimeSpan.FromDays(365))];

    private void BuildBrowse()
    {
        foreach (var l in Events.CommonLogs) _logBox.Items.Add(l);
        _logBox.SelectedIndex = 0;
        foreach (var w in BrowseWindows) _browseWindow.Items.Add(w.Name);
        _browseWindow.SelectedIndex = 1;
        var load = Btn("Show", primary: true);
        load.Click += async (_, _) => await BrowseAsync();
        _browseSearch.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) await BrowseAsync();
        };

        var filters = new WrapPanel { ItemSpacing = 10, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        filters.Children.Add(_logBox);
        filters.Children.Add(_browseWindow);
        foreach (var c in _levels)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            filters.Children.Add(c);
        }

        filters.Children.Add(_browseSearch);
        filters.Children.Add(load);
        filters.Children.Add(_browseCount);

        _browseList.ItemTemplate = new FuncDataTemplate<EventItem>((e, _) => EventRow(e));
        _browseList.SelectionChanged += (_, _) => ShowDetail(_browseList.SelectedItem as EventItem);
        ShowDetail(null);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*") };
        split.Children.Add(_browseList);
        var detailCard = Card(new ScrollViewer { Content = _detail });
        Grid.SetColumn(detailCard, 2);
        split.Children.Add(detailCard);

        var top = new StackPanel();
        top.Children.Add(Header("All events", "Any event log, newest first. Events this app knows get a plain-English line and advice."));
        top.Children.Add(filters);
        _pages["All events"] = PageOf(top, split);
    }

    private static Control EventRow(EventItem? e)
    {
        if (e is null) return new TextBlock();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,140,*") };
        var color = e.Level switch { 1 => "#F2666A", 2 => "#F5B14C", 3 => "#F5D76E", _ => "#00C8FF" };
        g.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var time = new TextBlock { Text = e.Time.ToString("dd MMM HH:mm:ss"), Classes = { "mono" }, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 1);
        g.Children.Add(time);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = e.RuleId is null ? Events.Trim(e.FirstLine, 160) : e.Summary, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{e.Provider} · {e.Id}", Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        g.Children.Add(text);
        return g;
    }

    private void ShowDetail(EventItem? e)
    {
        _detail.Children.Clear();
        if (e is null)
        {
            _detail.Children.Add(new TextBlock { Text = "Pick an event to see all of it.", Classes = { "muted" } });
            return;
        }

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Pill(e.LevelName, e.Level switch { 1 => "bad", 2 or 3 => "warn", _ => "info" }));
        head.Children.Add(new TextBlock { Text = $"Event {e.Id}", FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        _detail.Children.Add(head);
        _detail.Children.Add(new TextBlock { Text = $"{e.Time:dddd dd MMM yyyy, HH:mm:ss}\n{e.Log} · {e.Provider}", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (e.RuleId is { } id && Events.Rules.FirstOrDefault(r => r.Id == id) is { } rule)
        {
            var box = new StackPanel { Spacing = 4 };
            box.Children.Add(new TextBlock { Text = e.Summary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            box.Children.Add(new TextBlock { Text = rule.Explain, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            box.Children.Add(new TextBlock { Text = "Try: " + rule.Fix, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Good });
            _detail.Children.Add(new Border { Background = Brush("#0E1A16"), BorderBrush = Brush("#146B44"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = box });
        }

        _detail.Children.Add(new SelectableTextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        var copy = Btn("Copy event");
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } cb)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, $"{e.Time:yyyy-MM-dd HH:mm:ss} {e.LevelName} {e.Log}/{e.Provider} event {e.Id}\n{e.Message}");
        };
        _detail.Children.Add(copy);
    }

    public async Task BrowseAsync()
    {
        if (_browsing) return;
        _browsing = true;
        var log = _logBox.SelectedItem as string ?? "System";
        var span = BrowseWindows[Math.Max(0, _browseWindow.SelectedIndex)].Span;
        var levels = Enumerable.Range(0, 4).Where(i => _levels[i].IsChecked == true).Select(i => i + 1).ToArray();
        var search = _browseSearch.Text;
        _browseCount.Text = "Reading…";
        try
        {
            var items = await Task.Run(() => Events.Browse(log, levels, span, search, 2000));
            SetBrowse(items);
        }
        catch (UnauthorizedAccessException)
        {
            SetBrowse([]);
            _browseCount.Text = $"Reading {log} needs administrator rights (Restart as administrator in the sidebar).";
        }
        catch (Exception ex)
        {
            SetBrowse([]);
            _browseCount.Text = $"Couldn't read {log}: {ex.Message}";
        }
        finally
        {
            _browsing = false;
        }
    }

    public void SetBrowse(List<EventItem> items)
    {
        _browseItems = items;
        _browseList.ItemsSource = items;
        _browseCount.Text = items.Count >= 2000 ? "Newest 2,000 shown; narrow it down to see older ones." : $"{items.Count:N0} event(s)";
        if (items.Count > 0) _browseList.SelectedIndex = 0;
        else ShowDetail(null);
    }
}
