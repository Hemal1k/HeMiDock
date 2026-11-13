using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>An appointment of the calendar (Outlook).</summary>
sealed record CalendarEvent(string Subject, DateTime Start, DateTime End, bool AllDay, string Location, string Id, string Store, int Calendar);

/// <summary>
/// Appointments from the classic Outlook for the calendar of the clock (Settings → Clock, off by default): read from the
/// installed Outlook through its automation interface – no sign-in, no internet. The new Outlook and Outlook on the web
/// have no such interface. Outlook is asked on a background thread (it may take a moment to start); a month is kept for a
/// few minutes, so paging back and forth does not ask again.
/// </summary>
static class OutlookCalendar
{
    const string ProgId = "Outlook.Application";
    const int olFolderCalendar = 9;

    /// <summary>The classic Outlook is installed (its automation interface is registered).</summary>
    public static bool Available { get; } = Type.GetTypeFromProgID(ProgId) != null;

    static readonly Dictionary<string, (long At, List<CalendarEvent> Events)> Cache = new();
    static readonly object Gate = new();

    /// <summary>
    /// The appointments from <paramref name="from"/> until <paramref name="to"/> (a calendar page), from the cache when
    /// read in the last 5 minutes; null when Outlook cannot be asked.
    /// </summary>
    public static Task<List<CalendarEvent>> GetAsync(DateTime from, DateTime to)
    {
        lock (Gate)
            if (Cache.TryGetValue(CacheKey(from), out var c) && Environment.TickCount64 - c.At < 5 * 60_000) return Task.FromResult(c.Events);
        return OnSta(() =>
        {
            var events = Read(from, to);
            if (events != null) lock (Gate) Cache[CacheKey(from)] = (Environment.TickCount64, events);
            return events;
        });
    }

    static string CacheKey(DateTime from) => from.Ticks + "|" + DockSettings.Current.OutlookCalendarsHidden;

    static bool IsHidden(string name) => DockSettings.Current.OutlookCalendarsHidden.Split('|').Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The calendars of Outlook by name (the default one of every account and the calendars inside it); null when it cannot be asked.</summary>
    public static Task<List<string>> ListAsync() => OnSta(() =>
    {
        object app = null, ns = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId));
            ns = ((dynamic)app).GetNamespace("MAPI");
            var names = new List<string>();
            foreach (dynamic folder in Calendars(ns))
            {
                names.Add((string)folder.Name is var n && names.Contains(n) ? $"{n} ({names.Count + 1})" : n);
                Marshal.FinalReleaseComObject(folder);
            }
            return names;
        }
        catch (Exception ex) { Log.Write("Outlook calendars", ex); return null; }
        finally { Release(ns, app); }
    });

    /// <summary>Shows / hides a calendar (the months read so far are read again).</summary>
    public static void SetHidden(string name, bool hidden)
    {
        var list = DockSettings.Current.OutlookCalendarsHidden.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hidden) list.Add(name);
        DockSettings.Current.OutlookCalendarsHidden = string.Join("|", list);
    }

    /// <summary>Calendars of all accounts: each default calendar and the calendars in it.</summary>
    static IEnumerable<object> Calendars(object ns)
    {
        // by index: the collections of Outlook do not need to be enumerable here
        var result = new List<object>();
        dynamic stores = ((dynamic)ns).Stores;
        for (int i = 1; i <= (int)stores.Count; i++)
        {
            dynamic root = null;
            try { root = stores.Item(i).GetDefaultFolder(olFolderCalendar); }
            catch (Exception ex) { Diag.Write("Outlook", "store " + i + " has no calendar: " + ex.Message); }
            if (root == null) continue;
            result.Add(root);
            try
            {
                dynamic subs = root.Folders;
                for (int j = 1; j <= (int)subs.Count; j++)
                {
                    dynamic sub = subs.Item(j);
                    if ((int)sub.DefaultItemType == 1) result.Add(sub);    // olAppointmentItem
                }
            }
            catch (Exception ex) { Diag.Write("Outlook", "sub-calendars: " + ex.Message); }
        }
        return result;
    }

    /// <summary>Opens an appointment in Outlook.</summary>
    public static void Open(CalendarEvent e) => _ = OnSta(() =>
    {
        object app = null, ns = null, item = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId));
            ns = ((dynamic)app).GetNamespace("MAPI");
            item = ((dynamic)ns).GetItemFromID(e.Id, e.Store);
            ((dynamic)item).Display();
        }
        catch (Exception ex)
        {
            Log.Write("Outlook open", ex);
            Fg.Open("outlook.exe", "/select outlook:calendar");      // at least the calendar
        }
        finally { Release(item, ns, app); }
        return true;
    });

    /// <summary>Unread messages in the inbox (0 when Outlook does not answer).</summary>
    public static Task<int> UnreadAsync() => OnSta(() =>
    {
        object app = null, ns = null, inbox = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId));
            ns = ((dynamic)app).GetNamespace("MAPI");
            inbox = ((dynamic)ns).GetDefaultFolder(6);                   // olFolderInbox
            return (int)((dynamic)inbox).UnReadItemCount;
        }
        catch { return 0; }
        finally { Release(inbox, ns, app); }
    });

    /// <summary>
    /// The subjects of the messages of the inbox from the last two days that are read now – their notifications in the
    /// notification center can go (Settings → Clock and calendar). Only while Outlook runs; empty when it does not answer.
    /// </summary>
    public static Task<HashSet<string>> ReadSubjectsAsync() => OnSta(() =>
    {
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object app = null, ns = null, inbox = null, items = null, recent = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId));
            ns = ((dynamic)app).GetNamespace("MAPI");
            inbox = ((dynamic)ns).GetDefaultFolder(6);                   // olFolderInbox
            items = ((dynamic)inbox).Items;
            // the newest first, compared by their dates here: a filter with a date in text depends on the format of the
            // language of Windows and Outlook (it could find nothing)
            ((dynamic)items).Sort("[ReceivedTime]", true);
            recent = items;
            var since = DateTime.Now.AddDays(-2);
            int n = 0;
            foreach (dynamic mail in (System.Collections.IEnumerable)recent)
            {
                bool older = false;
                try
                {
                    older = mail.ReceivedTime is DateTime received && received < since;
                    if (!older && mail.UnRead is false && mail.Subject is string s && s.Trim().Length > 0) subjects.Add(s.Trim());
                }
                catch { }
                finally { Release((object)mail); }
                if (older || ++n >= 300) break;
            }
        }
        catch { }
        finally { Release(recent, items, inbox, ns, app); }
        return subjects;
    });

    /// <summary>Outlook's automation is a single-threaded COM server – it is called from a thread of its own.</summary>
    static Task<T> OnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Outlook" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    static List<CalendarEvent> Read(DateTime from, DateTime to)
    {
        object app = null, ns = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId));   // the running Outlook, or one started in the background
            ns = ((dynamic)app).GetNamespace("MAPI");
            var result = new List<CalendarEvent>();
            int index = 0;
            foreach (dynamic folder in Calendars(ns))
            {
                int calendar = index++;
                string name = folder.Name, store = folder.StoreID;
                if (!IsHidden(name) && result.Count < 400) ReadFolder(folder, store, calendar, from, to, result);
                Marshal.FinalReleaseComObject(folder);
            }
            Diag.Write("Outlook", $"{result.Count} appointments in {index} calendars {from:d} – {to:d}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Write("Outlook calendar", ex);
            return null;
        }
        finally { Release(ns, app); }
    }

    static void ReadFolder(dynamic folder, string store, int calendar, DateTime from, DateTime to, List<CalendarEvent> result)
    {
        object items = null, found = null;
        try
        {
            items = folder.Items;
            dynamic list = items;
            list.Sort("[Start]");                                       // Outlook wants this order: first sorted, then with repeated meetings
            list.IncludeRecurrences = true;                             // repeated meetings as their single occurrences
            try
            {
                // Outlook wants the dates in the format of the Windows region (without seconds)
                found = list.Restrict($"[Start] < '{to:g}' AND [End] > '{from:g}'");
            }
            catch (Exception ex)
            {
                Diag.Write("Outlook", "filter by region failed: " + ex.Message);   // the same without any region format
                found = list.Restrict($"@SQL=\"urn:schemas:calendar:dtstart\" < '{to.ToUniversalTime():yyyy-MM-dd HH:mm}' AND \"urn:schemas:calendar:dtend\" > '{from.ToUniversalTime():yyyy-MM-dd HH:mm}'");
            }
            dynamic results = found;
            for (dynamic a = results.GetFirst(); a != null; a = results.GetNext())
            {
                bool stop = false;
                try
                {
                    DateTime start = a.Start, end = a.End;
                    stop = start >= to;                                   // sorted by start – the rest is later
                    if (!stop) result.Add(new CalendarEvent((string)a.Subject ?? "", start, end, (bool)a.AllDayEvent, (string)a.Location ?? "", (string)a.EntryID, store, calendar));
                }
                catch (Exception ex) { Diag.Write("Outlook", "appointment: " + ex.Message); }
                if (stop || result.Count >= 400) break;
            }
        }
        catch (Exception ex) { Log.Write("Outlook calendar", ex); }
        finally { Release(found, items); }
    }

    static void Release(params object[] objects)
    {
        foreach (var o in objects)
            if (o != null && Marshal.IsComObject(o)) try { Marshal.FinalReleaseComObject(o); } catch { }
    }
}

/// <summary>Appointments from an internet calendar: an address (https, webcal) or a file .ics. Read again after 15 minutes.</summary>
static class IcsCalendar
{
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static string _source;
    static long _at;
    static List<(string Summary, string Location, DateTime Start, DateTime End, bool AllDay, string Rule)> _all;

    public static async Task<List<CalendarEvent>> GetAsync(DateTime from, DateTime to)
    {
        string source = DockSettings.Current.CalendarIcs.Trim();
        try
        {
            if (_all == null || source != _source || Environment.TickCount64 - _at > 15 * 60_000)
            {
                string text = source.StartsWith("http", StringComparison.OrdinalIgnoreCase) || source.StartsWith("webcal", StringComparison.OrdinalIgnoreCase)
                    ? await Http.GetStringAsync(System.Text.RegularExpressions.Regex.Replace(source, "^webcal", "https", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    : await File.ReadAllTextAsync(source);
                _all = Parse(text);
                _source = source;
                _at = Environment.TickCount64;
            }
            var result = new List<CalendarEvent>();
            foreach (var e in _all)
                foreach (var start in Occurrences(e.Start, e.Rule, to))
                {
                    var end = start + (e.End - e.Start);
                    if (start < to && end > from && result.Count < 400) result.Add(new CalendarEvent(e.Summary, start, end, e.AllDay, e.Location, null, null, 0));
                }
            return result.OrderBy(e => e.Start).ToList();
        }
        catch (Exception ex) { Log.Write("Internet calendar", ex); return null; }
    }

    static List<(string, string, DateTime, DateTime, bool, string)> Parse(string text)
    {
        var list = new List<(string, string, DateTime, DateTime, bool, string)>();
        text = text.Replace("\r\n ", "").Replace("\n ", "").Replace("\r\n\t", "").Replace("\n\t", "");   // long lines go on with a space
        string summary = "", location = "", rule = null;
        DateTime? start = null, end = null;
        bool allDay = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { summary = location = ""; rule = null; start = end = null; allDay = false; continue; }
            if (line == "END:VEVENT")
            {
                if (start != null) list.Add((summary, location, start.Value, end ?? (allDay ? start.Value.AddDays(1) : start.Value), allDay, rule));
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon], value = line[(colon + 1)..];
            string key = name.Split(';')[0];
            if (key == "SUMMARY") summary = Unescape(value);
            else if (key == "LOCATION") location = Unescape(value);
            else if (key == "RRULE") rule = value;
            else if (key == "DTSTART") { start = When(value); allDay = value.Length == 8; }
            else if (key == "DTEND") end = When(value);
        }
        return list;
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    static DateTime? When(string v)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (v.EndsWith('Z') && DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss'Z'", c, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)) return utc.ToLocalTime();
        if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", c, System.Globalization.DateTimeStyles.None, out var local)) return local;
        return DateTime.TryParseExact(v, "yyyyMMdd", c, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
    }

    /// <summary>The event itself, or with a simple repeat rule its repeats (daily, weekly, monthly, yearly with interval, count, until).</summary>
    static IEnumerable<DateTime> Occurrences(DateTime start, string rule, DateTime to)
    {
        if (rule == null) { yield return start; yield break; }
        var parts = rule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        parts.TryGetValue("FREQ", out var freq);
        int interval = parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, out int iv) && iv > 0 ? iv : 1;
        int count = parts.TryGetValue("COUNT", out var n) && int.TryParse(n, out int cn) ? cn : int.MaxValue;
        var until = parts.TryGetValue("UNTIL", out var u) ? When(u) : null;
        var at = start;
        for (int k = 0; k < count && k < 5000 && at < to && (until == null || at <= until); k++)
        {
            yield return at;
            at = freq switch
            {
                "DAILY" => start.AddDays(interval * (k + 1)),
                "WEEKLY" => start.AddDays(7 * interval * (k + 1)),
                "MONTHLY" => start.AddMonths(interval * (k + 1)),
                "YEARLY" => start.AddYears(interval * (k + 1)),
                _ => to
            };
        }
    }
}
