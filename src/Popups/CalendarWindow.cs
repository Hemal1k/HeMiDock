using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HeMiDock;

/// <summary>
/// The clock pop-up: the notifications of Windows (dismiss one or all, Do not disturb), a strip with
/// today (a click goes back to today, the arrow folds the calendar away) and an own calendar – the month and the year are
/// clickable (months / years to pick), the arrows and the wheel move, week numbers when chosen, a dot under the days with
/// appointments of Outlook / an internet calendar and the appointments of the chosen day under it.
/// (An own calendar: the CalendarView of WinUI put the weeks on wrong days in an app without a package and left out the
/// names of the days.)
/// </summary>
sealed class CalendarWindow : PopupWindow
{
    enum Pick { Days, Months, Years }

    readonly StackPanel _notifications = new();
    readonly ScrollViewer _notifScroll;
    readonly TextBlock _empty = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 26) };
    readonly Button _clearAll, _dnd;
    readonly Line _dndSlash;
    readonly TextBlock _dateHeader = new() { FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon _foldGlyph = new() { Glyph = "", FontSize = 11 };
    readonly Border _todayStrip, _calendarBody;
    readonly TextBlock _monthText = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    readonly TextBlock _yearText = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    readonly Button _yearPart;
    readonly Grid _dayNames = new(), _dayGrid = new() { Margin = new Thickness(0, 2, 0, 0) }, _pickGrid = new() { Visibility = Visibility.Collapsed };
    readonly StackPanel _daysView = new();
    readonly StackPanel _eventsPanel = new() { Margin = new Thickness(4, 8, 0, 2), Visibility = Visibility.Collapsed };
    readonly TextBlock _eventsHeader = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    readonly StackPanel _eventsList = new();
    UserNotificationListener _listener;
    DateTime _view;                                                   // first day of the month shown
    DateTime? _selected;
    Pick _pick;
    static bool _folded;                                              // the calendar folded away (until the app ends)

    static DockSettings S => DockSettings.Current;
    static Brush Res(string key) => ThemeBrush.Get(key);
    static Style FlyButton => (Style)Application.Current.Resources["DockButton"];

    public CalendarWindow() : base("calendar")
    {
        Host.IsDismissed = IsDismissed;                               // the badge at the clock does not count them

        // ───── notifications ─────
        _dndSlash = new Line { X1 = 2, Y1 = 2, X2 = 16, Y2 = 16, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = Res("TextFillColorPrimaryBrush"), Visibility = Visibility.Collapsed };
        var bell = new Microsoft.UI.Xaml.Shapes.Path
        {
            Fill = Res("TextFillColorPrimaryBrush"),
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M9,1.5 C6.2,1.5 4.2,3.6 4.2,6.3 L4.2,9.5 L2.5,12 L2.5,13 L15.5,13 L15.5,12 L13.8,9.5 L13.8,6.3 C13.8,3.6 11.8,1.5 9,1.5 Z M7.2,14.2 C7.2,15.2 8,16 9,16 C10,16 10.8,15.2 10.8,14.2 Z")
        };
        _dnd = new Button { Style = FlyButton, Padding = new Thickness(8, 6, 8, 6), Content = new Grid { Width = 18, Height = 18, Children = { bell, _dndSlash } } };
        _dnd.Click += (_, _) => { Dnd.Set(!Dnd.IsOn); UpdateDnd(); };
        _clearAll = new Button { Style = FlyButton, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed, Content = new TextBlock { Text = L.T("Clear all"), FontSize = 12 } };
        ToolTipService.SetToolTip(_clearAll, L.T("Clear all notifications"));
        _clearAll.Click += (_, _) => ClearAll();
        var notifHead = new Grid();
        notifHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        notifHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        notifHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        notifHead.Children.Add(new TextBlock { Text = L.T("Notifications"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_clearAll, 1);
        Grid.SetColumn(_dnd, 2);
        notifHead.Children.Add(_clearAll);
        notifHead.Children.Add(_dnd);
        _notifScroll = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, -4, 0), Visibility = Visibility.Collapsed, Content = _notifications };
        _notifications.Margin = new Thickness(0, 0, 4, 0);
        // head on top, the notifications (or "no new notifications" in the middle) in the rest – the card keeps its height
        // when notifications go (KeepSize)
        var notifBody = new Grid();
        notifBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        notifBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_empty, 1);
        Grid.SetRow(_notifScroll, 1);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _notifScroll.VerticalAlignment = VerticalAlignment.Top;
        notifBody.Children.Add(notifHead);
        notifBody.Children.Add(_empty);
        notifBody.Children.Add(_notifScroll);
        var notifCard = _notifCard = Card(notifBody, new Thickness(12, 6, 6, 8));
        notifCard.Margin = new Thickness(0, 0, 0, 10);

        // ───── today, with the arrow that folds the calendar away ─────
        var fold = new Button { Style = FlyButton, Padding = new Thickness(9, 6, 9, 6), Content = _foldGlyph, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(fold, L.T("Show or hide the calendar"));
        fold.Click += (_, _) => { _folded = !_folded; ApplyFold(); };
        var dateButton = new Button { Style = FlyButton, Padding = new Thickness(0), Content = _dateHeader, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTipService.SetToolTip(dateButton, L.T("Back to today"));
        dateButton.Click += (_, _) => { _view = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); _selected = null; BuildMonth(); };
        _todayStrip = new Border { Padding = new Thickness(12, 3, 6, 3), BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = new Grid { Children = { dateButton, fold } } };

        // ───── the calendar ─────
        var monthPart = LabelPart(_monthText);
        monthPart.Click += (_, _) => { if (_pick == Pick.Months) BuildYears(); else if (_pick == Pick.Years) BuildMonth(); else BuildMonths(); };
        _yearPart = LabelPart(_yearText);
        _yearPart.Click += (_, _) => BuildYears();
        var prev = new Button { Style = FlyButton, Padding = new Thickness(9, 5, 9, 5), Content = new FontIcon { Glyph = "", FontSize = 12 } };
        var next = new Button { Style = FlyButton, Padding = new Thickness(9, 5, 9, 5), Content = new FontIcon { Glyph = "", FontSize = 12 } };
        ToolTipService.SetToolTip(prev, L.T("Previous month"));
        ToolTipService.SetToolTip(next, L.T("Next month"));
        prev.Click += (_, _) => Shift(-1);
        next.Click += (_, _) => Shift(1);
        var nav = new Grid { Margin = new Thickness(4, 0, 0, 4) };
        nav.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { monthPart, _yearPart } });
        nav.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { prev, next } });

        int columns = S.CalendarWeekNumbers ? 8 : 7;
        for (int c = 0; c < columns; c++) { _dayNames.ColumnDefinitions.Add(new ColumnDefinition()); _dayGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
        for (int r = 0; r < 6; r++) _dayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < 4; c++) _pickGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 3; r++) _pickGrid.RowDefinitions.Add(new RowDefinition());
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            var name = new TextBlock { Text = dtf.AbbreviatedDayNames[(FirstDay + i) % 7], FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };   // "po", "út" like Windows
            Grid.SetColumn(name, i + columns - 7);
            _dayNames.Children.Add(name);
        }
        _daysView.Children.Add(_dayNames);
        _daysView.Children.Add(_dayGrid);
        _eventsPanel.Children.Add(new Border { Height = 1, Background = Res("DividerStrokeColorDefaultBrush"), Margin = new Thickness(-4, 0, -10, 8) });
        _eventsPanel.Children.Add(_eventsHeader);
        _eventsPanel.Children.Add(new ScrollViewer { MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _eventsList });
        _calendarBody = new Border
        {
            Padding = new Thickness(10, 8, 10, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new StackPanel { Children = { nav, new Grid { Children = { _daysView, _pickGrid } }, _eventsPanel } }
        };
        _calendarBody.PointerWheelChanged += (_, e) => { Shift(e.GetCurrentPoint(_calendarBody).Properties.MouseWheelDelta > 0 ? -1 : 1); e.Handled = true; };
        var calendarCard = Card(new StackPanel { Children = { _todayStrip, _calendarBody } }, new Thickness(0));

        Content = new StackPanel { Width = 372, Padding = new Thickness(12), Children = { notifCard, calendarCard } };
        _view = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Fresh();
    }

    readonly FrameworkElement _notifCard;

    /// <summary>
    /// While it is open, removing notifications does not make the window smaller: changing the size of a window of WinUI
    /// shows one empty frame (the window flashed at Clear all). The card of the notifications keeps its height (the
    /// calendar under it stays where it was – with the window kept tall it moved up and left an empty place under it);
    /// it gets its own size again at the next opening.
    /// </summary>
    void KeepSize()
    {
        if (_notifCard.ActualHeight > 0) _notifCard.MinHeight = Math.Max(_notifCard.MinHeight, _notifCard.ActualHeight);
    }

    protected override void Reopen()
    {
        _notifCard.MinHeight = 0;
        _view = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selected = null;
        _events = null;                                                // appointments may have changed meanwhile
        Fresh();
    }

    void Fresh()
    {
        BuildHeader();
        BuildMonth();
        UpdateDnd();
        ApplyFold();
        _ = LoadNotificationsAsync();
    }

    static Border Card(UIElement child, Thickness padding) => new()
    {
        Background = Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = padding, Child = child
    };

    /// <summary>The month / the year above the calendar: clickable, lit up under the mouse.</summary>
    static Button LabelPart(TextBlock text) => new() { Style = FlyButton, Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(5), Content = text };

    // ───────────────────────── calendar ─────────────────────────

    void BuildHeader()
    {
        var c = CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        string md = today.ToString(c.DateTimeFormat.MonthDayPattern, c);
        string sep = md.Length > 0 && char.IsDigit(md[0]) ? " " : ", ";   // "úterý 3. listopadu" (day first) / "Tuesday, November 3"
        _dateHeader.Text = today.ToString("dddd", c) + sep + md;
    }

    void ApplyFold()
    {
        _calendarBody.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        _todayStrip.BorderThickness = new Thickness(0, 0, 0, _folded ? 0 : 1);
        _foldGlyph.Glyph = _folded ? "" : "";
    }

    /// <summary>First day of the week: from the settings or the region of Windows.</summary>
    static int FirstDay => S.CalendarFirstDay switch { 1 => 1, 2 => 0, _ => (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek };

    void BuildMonth()
    {
        var c = CultureInfo.CurrentCulture;
        _pick = Pick.Days;
        _daysView.Opacity = 1;
        _daysView.IsHitTestVisible = true;
        _pickGrid.Visibility = Visibility.Collapsed;
        // two parts: a click on the month opens the months, on the year the years
        _monthText.Text = c.DateTimeFormat.GetMonthName(_view.Month);
        _yearText.Text = _view.Year.ToString();
        _yearPart.Visibility = Visibility.Visible;

        int offset = ((int)_view.DayOfWeek - FirstDay + 7) % 7;
        var start = _view.AddDays(-offset);
        bool weeks = S.CalendarWeekNumbers;
        _dayGrid.Children.Clear();
        for (int i = 0; i < 42; i++)
        {
            int row = i / 7, col = i % 7 + (weeks ? 1 : 0);
            if (i % 7 == 0 && weeks)
            {
                var week = new TextBlock { Text = ISOWeek.GetWeekOfYear(start.AddDays(i + 3)).ToString(), Foreground = Res("TextFillColorTertiaryBrush"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(week, row);
                _dayGrid.Children.Add(week);
            }
            var day = MakeDay(start.AddDays(i));
            Grid.SetRow(day, row);
            Grid.SetColumn(day, col);
            _dayGrid.Children.Add(day);
        }
        ShowEvents(start);
    }

    FrameworkElement MakeDay(DateTime d)
    {
        bool inMonth = d.Month == _view.Month, today = d.Date == DateTime.Today, selected = _selected?.Date == d.Date;
        Brush text = today ? Res("TextOnAccentFillColorPrimaryBrush") : Res(inMonth ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        var circle = new Button
        {
            Style = FlyButton, Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = today ? Res("AccentFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = selected && !today ? Res("TextFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Text = d.Day.ToString(), Foreground = text, FontSize = 12.5, FontWeight = today ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal }
        };
        if (today) circle.Resources["ButtonBackgroundPointerOver"] = Res("AccentFillColorSecondaryBrush");
        circle.Click += (_, _) =>
        {
            _selected = d;
            if (d.Month != _view.Month) _view = new DateTime(d.Year, d.Month, 1);
            BuildMonth();
        };
        var cell = new Grid { Height = 33, Children = { circle } };
        // a dot under the number: the day has appointments
        if (EventsOn && _events != null && _events.Any(e => OnDay(e, d.Date)))
            cell.Children.Add(new Ellipse
            {
                Width = 4, Height = 4, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5),
                Fill = today ? text : Res(inMonth ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush")
            });
        return cell;
    }

    /// <summary>The arrows and the wheel: a month, a year (months shown) or twelve years (years shown).</summary>
    void Shift(int step)
    {
        switch (_pick)
        {
            case Pick.Months: _view = _view.AddYears(step); BuildMonths(); break;
            case Pick.Years: _view = _view.AddYears(12 * step); BuildYears(); break;
            default: _view = _view.AddMonths(step); BuildMonth(); break;
        }
    }

    void ShowPicker(string label)
    {
        _monthText.Text = label;
        _yearPart.Visibility = Visibility.Collapsed;
        _daysView.Opacity = 0;                                        // keeps its size – the calendar does not jump
        _daysView.IsHitTestVisible = false;
        _pickGrid.Visibility = Visibility.Visible;
        _pickGrid.Children.Clear();
    }

    void BuildMonths()
    {
        _pick = Pick.Months;
        ShowPicker(_view.Year.ToString());
        var names = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;   // whole names – "čvn" / "čvc" are hard to tell apart
        for (int m = 1; m <= 12; m++)
        {
            int month = m;
            AddPick(m - 1, names[m - 1].TrimEnd('.'), _view.Year == DateTime.Today.Year && m == DateTime.Today.Month, m == _view.Month, () => { _view = new DateTime(_view.Year, month, 1); BuildMonth(); });
        }
    }

    void BuildYears()
    {
        _pick = Pick.Years;
        int from = _view.Year - _view.Year % 12;                     // always the same twelve years for a year
        ShowPicker($"{from} – {from + 11}");
        for (int y = from; y < from + 12; y++)
        {
            int year = y;
            AddPick(y - from, y.ToString(), y == DateTime.Today.Year, y == _view.Year, () => { _view = new DateTime(year, _view.Month, 1); BuildMonths(); });
        }
    }

    /// <summary>A month / year to pick: like a day – the current one in the accent colour, the shown one outlined.</summary>
    void AddPick(int index, string text, bool now, bool shown, Action pick)
    {
        var cell = new Button
        {
            Style = FlyButton, Margin = new Thickness(3), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Background = now ? Res("AccentFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = shown && !now ? Res("TextFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(1.5),
            Content = new TextBlock { Text = text, FontSize = 12.5, FontWeight = now ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = Res(now ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush") }
        };
        cell.Click += (_, _) => pick();
        Grid.SetRow(cell, index / 4);
        Grid.SetColumn(cell, index % 4);
        _pickGrid.Children.Add(cell);
    }

    // ───────────────────────── appointments (Outlook or an internet calendar, optional) ─────────────────────────

    static bool EventsOn => S.CalendarSource switch { 1 => OutlookCalendar.Available, 2 => S.CalendarIcs.Length > 0, _ => false };

    List<CalendarEvent> _events;                                     // of the page shown (from _eventsFrom, 42 days)
    DateTime _eventsFrom;
    bool _eventsFailed;

    static bool OnDay(CalendarEvent e, DateTime day) =>
        e.Start < day.AddDays(1) && (e.End > day || e.End == e.Start && e.Start.Date == day);   // also appointments without length

    /// <summary>The appointments of the selected day (today by default) below the days – loads the page first when needed.</summary>
    async void ShowEvents(DateTime pageStart)
    {
        _eventsPanel.Visibility = EventsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!EventsOn) return;
        var day = (_selected ?? DateTime.Today).Date;
        var c = CultureInfo.CurrentCulture;
        _eventsHeader.Text = day == DateTime.Today ? L.T("Today") : day.ToString(c.DateTimeFormat.MonthDayPattern, c);
        if (_events == null || _eventsFrom != pageStart)
        {
            _eventsList.Children.Clear();
            _eventsList.Children.Add(EventNote(L.T("Loading…")));
            var view = _view;
            List<CalendarEvent> events = null;
            try { events = S.CalendarSource == 2 ? await IcsCalendar.GetAsync(pageStart, pageStart.AddDays(42)) : await OutlookCalendar.GetAsync(pageStart, pageStart.AddDays(42)); }
            catch (Exception ex) { Log.Write("Calendar", ex); }
            if (_pick != Pick.Days || view != _view) return;           // the user moved on meanwhile (that page loads itself)
            _eventsFailed = events == null;
            _events = events ?? new List<CalendarEvent>();
            _eventsFrom = pageStart;
            BuildMonth();                                              // again with the dots of the days that have appointments
            return;
        }
        _eventsList.Children.Clear();
        if (_eventsFailed) { _eventsList.Children.Add(EventNote(L.T("Outlook did not answer."))); return; }
        var list = _events.Where(e => OnDay(e, day)).OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToList();
        if (list.Count == 0) { _eventsList.Children.Add(EventNote(L.T("No appointments"))); return; }
        foreach (var e in list) _eventsList.Children.Add(MakeEvent(e, day));
    }

    static TextBlock EventNote(string text) => new() { Text = text, FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>The colour of a calendar: the first one in the accent colour, the others each their own.</summary>
    static Brush CalendarBrush(int calendar)
    {
        if (calendar <= 0) return Res("AccentFillColorDefaultBrush");
        uint[] colors = { 0xFF3FB950, 0xFFD29922, 0xFFE5534B, 0xFFAF7BFF, 0xFF36D1C4, 0xFFFF7AB6 };
        uint c = colors[(calendar - 1) % colors.Length];
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }

    /// <summary>One appointment: a line in the colour of its calendar, the time (or "All day"), its subject and place; a click opens it in Outlook.</summary>
    FrameworkElement MakeEvent(CalendarEvent e, DateTime day)
    {
        var c = CultureInfo.CurrentCulture;
        string time = e.AllDay ? L.T("All day") : (e.Start.Date < day ? "…" : e.Start.ToString("t", c)) + " – " + (e.End.Date > day ? "…" : e.End.ToString("t", c));
        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = e.Subject, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        string sub = string.IsNullOrWhiteSpace(e.Location) ? time : time + " · " + e.Location;
        text.Children.Add(new TextBlock { Text = sub, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextFillColorSecondaryBrush") });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = CalendarBrush(e.Calendar) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = new Button { Style = FlyButton, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = row, IsEnabled = e.Id != null };
        ToolTipService.SetToolTip(button, L.T("Open in Outlook"));
        button.Click += (_, _) => { CloseNow(); OutlookCalendar.Open(e); };
        return button;
    }

    // ───────────────────────── do not disturb ─────────────────────────

    void UpdateDnd()
    {
        bool on = Dnd.IsOn;
        _dndSlash.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_dnd, on ? L.T("Do not disturb: on (notifications are muted)") : L.T("Do not disturb: off"));
        _empty.Text = on ? L.T("Do not disturb is on") : L.T("No new notifications");
    }

    // ───────────────────────── notifications ─────────────────────────

    /// <summary>
    /// Notifications dismissed here (ids, kept in the settings folder). Windows does not always let an app without an
    /// installer package remove them – these are then not shown again, so "Clear all" really clears.
    /// </summary>
    static HashSet<uint> _dismissed;
    static string DismissedFile => AppPaths.Of("dismissed-notifications.json");

    static HashSet<uint> DismissedIds
    {
        get
        {
            if (_dismissed != null) return _dismissed;
            try { _dismissed = System.Text.Json.JsonSerializer.Deserialize<HashSet<uint>>(File.ReadAllText(DismissedFile)) ?? new(); }
            catch { _dismissed = new(); }
            return _dismissed;
        }
    }

    internal static bool IsDismissed(uint id) => DismissedIds.Contains(id);

    static void SaveDismissed()
    {
        try
        {
            if (DismissedIds.Count == 0) { File.Delete(DismissedFile); return; }   // nothing to remember – no file
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(DismissedFile, System.Text.Json.JsonSerializer.Serialize(DismissedIds.TakeLast(500)));
        }
        catch (Exception ex) { Log.Write("Dismissed notifications", ex); }
    }

    void Dismiss(IEnumerable<uint> ids)
    {
        foreach (var id in ids)
        {
            DismissedIds.Add(id);
            try { _listener?.RemoveNotification(id); } catch { }
        }
        SaveDismissed();
    }

    /// <summary>The notifications of Windows (the ones in its notification center), newest first – Windows asks once for access.</summary>
    async Task LoadNotificationsAsync()
    {
        try
        {
            _listener ??= UserNotificationListener.Current;
            if (await _listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed)
            {
                var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                NotificationBadge.MarkSeen();                           // they are on screen now – the badge at the clock goes away
                _notifications.Children.Clear();
                // newest first, without the ones dismissed here; ids that Windows no longer has are forgotten
                var present = list.Select(n => n.Id).ToHashSet();
                if (DismissedIds.RemoveWhere(id => !present.Contains(id)) > 0) SaveDismissed();
                foreach (var n in list.Where(n => !DismissedIds.Contains(n.Id)).OrderByDescending(n => n.CreationTime).Take(30))
                    if (MakeNotification(n) is { } card) _notifications.Children.Add(card);
            }
        }
        catch (Exception ex) { Log.Write("Notifications", ex); }
        ShowNotificationState();
    }

    void ShowNotificationState()
    {
        bool any = _notifications.Children.Count > 0;
        _notifScroll.Visibility = _clearAll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The icon of the app of a notification; Windows gives none for many classic programs – then the icon of the app of the same name from Start.</summary>
    static async void LoadLogo(Image image, UserNotification n)
    {
        int px = (int)Math.Ceiling(image.Width * (image.XamlRoot?.RasterizationScale ?? Native.MonitorDpi(Monitors.Primary().Handle) ?? 1));
        // the icon of the app as the shell has it (as in Start): the logo of a notification is the tile picture with a wide
        // transparent margin, so it looked tiny
        try
        {
            if (n.AppInfo?.AppUserModelId is { Length: > 0 } aumid && await Pictures.OfAsync("shell:AppsFolder\\" + aumid, false, px) is { } shell)
            {
                image.Source = shell;
                return;
            }
        }
        catch { }
        try
        {
            if (n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64)) is { } reference)
            {
                using var stream = await reference.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                var picture = Pictures.Trim(new IconImage(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight));   // without its empty margin
                image.Source = Pictures.From(picture, px);
                return;
            }
        }
        catch { }
        string name = null;
        try { name = n.AppInfo?.DisplayInfo?.DisplayName; } catch { }
        var app = name == null ? null : AppCatalog.Apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (app == null) image.Visibility = Visibility.Collapsed;
        else Pictures.Into(image, app.Key);
    }

    FrameworkElement MakeNotification(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        if (texts.Count == 0) return null;
        string app = "";
        try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }

        var close = new Button { Style = FlyButton, Padding = new Thickness(6, 4, 6, 4), Content = new FontIcon { Glyph = "", FontSize = 10 } };
        ToolTipService.SetToolTip(close, L.T("Dismiss"));
        var logo = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        LoadLogo(logo, n);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var appText = new TextBlock { Text = app, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        var time = new TextBlock { Text = n.CreationTime.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), FontSize = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorTertiaryBrush") };
        Grid.SetColumn(appText, 1);
        Grid.SetColumn(time, 2);
        Grid.SetColumn(close, 3);
        head.Children.Add(logo);
        head.Children.Add(appText);
        head.Children.Add(time);
        head.Children.Add(close);

        var body = new StackPanel { Children = { head } };
        body.Children.Add(new TextBlock { Text = texts[0], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (texts.Count > 1)
            body.Children.Add(new TextBlock { Text = string.Join("\n", texts.Skip(1)), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 2, 0, 0) });
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 4, 8), Margin = new Thickness(0, 0, 0, 6), Background = Res("SubtleFillColorSecondaryBrush"), Child = body, Tag = n.Id };
        close.Click += (_, _) =>
        {
            Dismiss(new[] { n.Id });
            KeepSize();
            _notifications.Children.Remove(card);
            ShowNotificationState();
        };
        return card;
    }

    void ClearAll()
    {
        KeepSize();
        Dismiss(_notifications.Children.OfType<FrameworkElement>().Select(c => c.Tag).OfType<uint>().ToList());
        try { _listener?.ClearNotifications(); } catch { }
        _notifications.Children.Clear();
        ShowNotificationState();
    }
}
