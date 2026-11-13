using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace HeMiDock;

/// <summary>Settings folder (%AppData%\HeMiDock).</summary>
static class AppPaths
{
    public static string Folder { get; } = DataFolder.Resolve();

    public static string Of(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Log files (only in debug mode, see <see cref="Diag"/>).</summary>
    public static string Logs => Path.Combine(Folder, "Logs");
}

/// <summary>Author, version and build of the app (Settings → About).</summary>
static class AppInfo
{

    // HeMiDock.exe, where the version is set (this library has none of its own)
    static readonly System.Reflection.Assembly App = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

    /// <summary>"Beta 1.0.0" (the version in HeMiDock.csproj).</summary>
    public static string Version
    {
        get
        {
            var v = App.GetName().Version;
            return v == null ? "–" : $"Beta {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>When this build was made (written into the app by the project file), null when unknown.</summary>
    public static DateTime? BuildDate =>
        App.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value is string s
        && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

/// <summary>A setting that lives in Windows itself (e.g. the registry) – not written to settings.json.</summary>
[AttributeUsage(AttributeTargets.Property)]
sealed class NotStoredAttribute : Attribute { }

/// <summary>
/// Starting HeMiDock when the user signs in, two ways at once, both with <see cref="StartupArg"/>:
///  • a task of the Task Scheduler with a sign-in trigger – Windows runs it right at the sign-in, beside Explorer (the Run key
///    is started by Explorer only once it has come up, the dock came seconds later);
///  • the Run value of the registry – so the dock is in Task Manager → Startup apps, where it can be switched off too.
/// Whichever comes second ends at once (see Program.Main). Switched off in Task Manager (StartupApproved), the task does not
/// start the dock either, and the settings show the start at sign-in off.
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "HeMiDock", BetaName = "HeMiDock.WinUI";
    public const string StartupArg = "--startup";

    /// <summary>One task per user (tasks of all users share one folder).</summary>
    static string TaskName => $"HeMiDock ({Environment.UserName})";

    static string RunCommand(string exe) => $"\"{exe}\" {StartupArg}";

    /// <summary>
    /// Switched off in Task Manager → Startup apps: Windows keeps the Run value and marks it in StartupApproved (the first
    /// byte odd = off).
    /// </summary>
    public static bool DisabledByUser
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return k?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
            }
            catch { return false; }
        }
    }

    static void ClearDisabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
            k?.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Write("Autostart", ex); }
    }

    /// <summary>
    /// At every start, when the start at sign-in is on: both ways there and up to date – the Run value of an earlier version
    /// (also of the test builds before 1.0) gets its task, a task of 1.4 its Run value, both get <see cref="StartupArg"/>; a
    /// program no longer there (a build removed) is replaced by this one. A switch-off in Task Manager stays as it is.
    /// </summary>
    public static void TakeOver()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            string run = k.GetValue(ValueName) as string ?? k.GetValue(BetaName) as string;
            string task = TaskXml();
            if (run == null && task == null) return;                   // the start at sign-in is off
            string exe = ExeOf(task != null ? TaskCommand(task) : run);
            if (exe == null || !File.Exists(exe)) exe = Environment.ProcessPath;
            if (task == null || !task.Contains(StartupArg) || !string.Equals(ExeOf(TaskCommand(task)), exe, StringComparison.OrdinalIgnoreCase)) RegisterTask(exe);
            k.DeleteValue(BetaName, false);
            if (!string.Equals(k.GetValue(ValueName) as string, RunCommand(exe), StringComparison.OrdinalIgnoreCase)) k.SetValue(ValueName, RunCommand(exe));
            NoStartupDelay(true);
        }
        catch (Exception ex) { Log.Write("Autostart", ex); }
    }

    /// <summary>The program of a command line ("C:\…\HeMiDock.exe" --startup → C:\…\HeMiDock.exe); null for none.</summary>
    static string ExeOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"')) { int end = command.IndexOf('"', 1); return end > 0 ? command[1..end] : command.Trim('"'); }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    static dynamic TaskFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        return service.GetFolder("\\");
    }

    /// <summary>The definition of the task; null without the task.</summary>
    static string TaskXml()
    {
        try { return TaskFolder().GetTask(TaskName).Xml; }
        catch { return null; }                                          // no task (GetTask throws)
    }

    /// <summary>The program and the arguments the task starts.</summary>
    static string TaskCommand(string xml)
    {
        string Part(string tag) => System.Text.RegularExpressions.Regex.Match(xml, $"<{tag}>(.*?)</{tag}>") is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        return (Part("Command") + " " + Part("Arguments")).Trim();
    }

    /// <summary>
    /// The task: at the sign-in of this user, as this user without raised rights (no UAC), normal priority (tasks get a lower
    /// one by default), also on battery, never ended after a time (by default after 3 days).
    /// </summary>
    static bool RegisterTask(string exe)
    {
        try
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string E(string s) => System.Security.SecurityElement.Escape(s);
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>HeMiDock at sign-in</Description></RegistrationInfo>
                  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{E(user)}</UserId></LogonTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{E(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>"{E(exe)}"</Command><Arguments>{StartupArg}</Arguments><WorkingDirectory>{E(Path.GetDirectoryName(exe))}</WorkingDirectory></Exec></Actions>
                </Task>
                """;
            TaskFolder().RegisterTask(TaskName, xml, 6 /*TASK_CREATE_OR_UPDATE*/, null, null, 3 /*TASK_LOGON_INTERACTIVE_TOKEN*/);
            return true;
        }
        catch (Exception ex) { Log.Write("Autostart task", ex); return false; }
    }

    static void DeleteTask()
    {
        try { TaskFolder().DeleteTask(TaskName, 0); }
        catch { }                                                       // there was none
    }

    /// <summary>Windows delays the apps of sign-in by some seconds; without the delay the dock replaces the taskbar sooner.</summary>
    static void NoStartupDelay(bool on)
    {
        try
        {
            using var s = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize");
            if (!on) s.DeleteValue("StartupDelayInMSec", false);
            else if (s.GetValue("StartupDelayInMSec") is not int d || d != 0) s.SetValue("StartupDelayInMSec", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Write("Autostart delay", ex); }
    }

    /// <summary>On: the task or the Run value is there and the start is not switched off in Task Manager.</summary>
    public static bool IsOn
    {
        get
        {
            if (DisabledByUser) return false;
            if (TaskXml() != null) return true;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                ClearDisabled();                                        // switched here: a switch-off of Task Manager is gone too
                NoStartupDelay(value);                                  // the Run value: without the delay of Windows
                if (value)
                {
                    RegisterTask(Environment.ProcessPath);               // the task could not be made: the Run value alone
                    k.SetValue(ValueName, RunCommand(Environment.ProcessPath));
                }
                else
                {
                    DeleteTask();
                    k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("Autostart", ex); }
        }
    }
}

/// <summary>
/// Size, look and behaviour of the dock. The dock, the Start menu and the settings window are bound to this class,
/// so every change applies immediately (and is written to settings.json on Save).
/// All public properties with a setter (bool / int / double / string) are stored – new options only need to be added here.
/// </summary>
public partial class DockSettings : INotifyPropertyChanged
{
    // Order matters: static initializers run top to bottom and the Current constructor uses Stored.
    static readonly PropertyInfo[] Stored = typeof(DockSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<NotStoredAttribute>() == null
                    && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(double) || p.PropertyType == typeof(string)))
        .ToArray();

    public static DockSettings Current { get; } = new(true);

    public event PropertyChangedEventHandler PropertyChanged;

    bool _suspend;

    // ───────────────────────── dock ─────────────────────────
    double _iconSize = 30, _iconSpacing = 18, _magnification = 1.10, _rounding = 8, _backgroundOpacity = 30, _bottomMargin = 5;
    bool _showStartButton = true, _showSearchButtons = false, _showRunningIndicator = true, _launchAnimation = true,
         _showDesktopButton = true, _autoHide, _showPreviews = true;
    int _themeMode, _startIconLook, _glassMode;
    bool _showSeparators;

    /// <summary>Thin lines between the Start buttons, the apps and the tray (the gaps stay either way).</summary>
    public bool ShowSeparators { get => _showSeparators; set => Set(ref _showSeparators, value); }

    int _language, _dockPosition;

    int _hoverEffect = 1;
    /// <summary>What shows the icon under the mouse: 0 = magnification, 1 = highlight (a background behind it), 2 = both.</summary>
    public int HoverEffect { get => _hoverEffect; set => Set(ref _hoverEffect, Math.Clamp(value, 0, 2)); }

    // ───────────────────────── search (Start menu and quick search) ─────────────────────────
    bool _searchApps = true, _calendarWeekNumbers, _searchWindowsSettings = true, _searchDockSettings = true, _searchFiles = true;
    /// <summary>Windows settings and tools in the results.</summary>
    public bool SearchWindowsSettings { get => _searchWindowsSettings; set => Set(ref _searchWindowsSettings, value); }
    /// <summary>Settings of HeMiDock itself in the results.</summary>
    public bool SearchDockSettings { get => _searchDockSettings; set => Set(ref _searchDockSettings, value); }
    /// <summary>Files and folders (Windows Search index) in the results.</summary>
    public bool SearchFiles { get => _searchFiles; set => Set(ref _searchFiles, value); }
    /// <summary>"Pin to Start (HeMiDock)" in the right-click menu of files and folders in Explorer.</summary>
    /// <summary>Apps in the results of the search.</summary>
    public bool SearchApps { get => _searchApps; set => Set(ref _searchApps, value); }
    int _dateOrder;
    /// <summary>Order of the day and the month in the date: 0 = by the region of Windows, 1 = day first, 2 = month first.</summary>
    public int DateOrder { get => _dateOrder; set => Set(ref _dateOrder, Math.Clamp(value, 0, 2)); }
    /// <summary>Numbers of the weeks in the calendar.</summary>
    public bool CalendarWeekNumbers { get => _calendarWeekNumbers; set => Set(ref _calendarWeekNumbers, value); }
    int _calendarFirstDay, _clockLine;
    /// <summary>First day of the week in the calendar: 0 = by the region of Windows, 1 = Monday, 2 = Sunday.</summary>
    public int CalendarFirstDay { get => _calendarFirstDay; set => Set(ref _calendarFirstDay, Math.Clamp(value, 0, 2)); }
    /// <summary>Clock: 0 = the date under the time, 1 = on one line.</summary>
    public int ClockLine { get => _clockLine; set => Set(ref _clockLine, Math.Clamp(value, 0, 1)); }
    /// <summary>Where the calendar takes appointments from: 0 = nowhere, 1 = Outlook, 2 = an internet calendar (.ics).</summary>
    public int CalendarSource { get => _calendarSource; set => Set(ref _calendarSource, Math.Clamp(value, 0, 2)); }
    int _calendarSource;
    /// <summary>One size for the icons of the tray and the status icons.</summary>
    [NotStored] public double StatusAndTrayIconSize { get => StatusIconSize; set { StatusIconSize = value; TrayIconSize = value; Notify(); } }
    bool _searchConversions = true;
    /// <summary>Units and currencies in the results ("10 km in mi", "100 EUR"); see <see cref="Converter"/>.</summary>
    public bool SearchConversions { get => _searchConversions; set => Set(ref _searchConversions, value); }

    // ───────────────────────── keyboard shortcuts (Settings → Keyboard shortcuts) ─────────────────────────
    bool _weatherOn, _searchClipboard;
    string _appRules = "", _plannedShutdown = "", _weatherCity = "";
    /// <summary>Where the windows of apps open (see <see cref="HeMiDock.AppRules"/>).</summary>
    public string AppRules { get => _appRules; set => Set(ref _appRules, value ?? ""); }
    /// <summary>A planned shutdown, so a restart of the dock keeps showing it ("set|at" in ticks, see <see cref="KeepAwake"/>).</summary>
    public string PlannedShutdown { get => _plannedShutdown; set => Set(ref _plannedShutdown, value ?? ""); }
    /// <summary>The weather among the status icons (the city goes to the service Open-Meteo, see <see cref="Weather"/>).</summary>
    public bool WeatherOn { get => _weatherOn; set => Set(ref _weatherOn, value); }
    public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value ?? ""); }
    /// <summary>Texts copied since the start of the app in the results of the search (see <see cref="ClipboardHistory"/>).</summary>
    public bool SearchClipboard { get => _searchClipboard; set => Set(ref _searchClipboard, value); }

    string _hotkeys = "";
    /// <summary>Own shortcuts of the actions ("id=modifiers:key|…", see <see cref="HeMiDock.Hotkeys"/>; empty = defaults).</summary>
    public string Hotkeys { get => _hotkeys; set => Set(ref _hotkeys, value ?? ""); }
    bool _keyStart = true, _keySearch = true, _keyPowerMenu = true;
    /// <summary>The Win key alone and Ctrl+Esc open the HeMiDock Start menu.</summary>
    public bool KeyStart { get => _keyStart; set => Set(ref _keyStart, value); }
    /// <summary>Win+S opens the Start menu with the search.</summary>
    public bool KeySearch { get => _keySearch; set => Set(ref _keySearch, value); }
    /// <summary>Win+X opens the menu of the Start button.</summary>
    public bool KeyPowerMenu { get => _keyPowerMenu; set => Set(ref _keyPowerMenu, value); }
    bool _keyControl;
    /// <summary>Win+A opens the control center of HeMiDock instead of the quick settings of Windows (off by default).</summary>
    public bool KeyControl { get => _keyControl; set => Set(ref _keyControl, value); }

    bool _dockAllMonitors = true;
    /// <summary>With several monitors a dock on each of them (otherwise only on <see cref="DockMonitor"/>).</summary>
    public bool DockAllMonitors { get => _dockAllMonitors; set => Set(ref _dockAllMonitors, value); }
    double _dockMinLength;
    /// <summary>The dock made longer by hand (points; 0 = as long as its content): the free room comes after the apps.</summary>
    public double DockMinLength { get => _dockMinLength; set => Set(ref _dockMinLength, Math.Clamp(Math.Round(value), 0, 8000)); }
    bool _widenSpreadsIcons;
    /// <summary>Dragging the ends of the dock changes the spacing of the icons instead of adding free room after them.</summary>
    public bool WidenSpreadsIcons { get => _widenSpreadsIcons; set => Set(ref _widenSpreadsIcons, value); }
    int _statusMonitor;
    /// <summary>
    /// With a dock on each monitor, which of them have the clock, the status icons and the notification area: 0 the main dock,
    /// 1 all of them, 2 + n the dock of monitor n.
    /// </summary>
    public int StatusMonitor { get => _statusMonitor; set => Set(ref _statusMonitor, Math.Clamp(value, 0, 10)); }
    int _dockMonitor;
    /// <summary>Monitor of the dock: 0 = the main monitor, 1, 2 … = the other monitors (in the order of <see cref="Monitors.All"/>).</summary>
    public int DockMonitor { get => _dockMonitor; set => Set(ref _dockMonitor, Math.Clamp(value, 0, 8)); }

    /// <summary>Screen edge of the dock: 0 = bottom, 1 = left, 2 = right, 3 = top.</summary>
    public int DockPosition { get => _dockPosition; set => Set(ref _dockPosition, value == 3 && TopBar ? 0 : Math.Clamp(value, 0, 3)); }   // the top edge belongs to the top bar
    public bool IsVertical => DockPosition is 1 or 2;

    /// <summary>Language of the app: 0 = follow Windows, 1 = English, 2 = Czech.</summary>
    public int Language { get => _language; set => Set(ref _language, Math.Clamp(value, 0, 2)); }

    /// <summary>0 = follow Windows (light / dark mode), 1 = always light, 2 = always dark.</summary>
    public int ThemeMode { get => _themeMode; set => Set(ref _themeMode, Math.Clamp(value, 0, 2)); }
    /// <summary>
    /// Look of the Start button: 0 = logo, 1 = logo in one colour, 2 = app grid, 3 = app grid in one colour, 4 = own picture.
    /// </summary>
    public int StartIconLook { get => _startIconLook; set => Set(ref _startIconLook, Math.Clamp(value, 0, 4)); }
    string _startButtonImage = "";
    /// <summary>Own picture of the Start button (a copy in the settings folder, or an .exe / shortcut whose icon is used).</summary>
    public string StartButtonImage { get => _startButtonImage; set => Set(ref _startButtonImage, value ?? ""); }

    /// <summary>Size of app icons (px).</summary>
    public double IconSize { get => _iconSize; set => Set(ref _iconSize, Math.Clamp(value, 24, 96)); }
    /// <summary>Gap between icons (px).</summary>
    public double IconSpacing { get => _iconSpacing; set => Set(ref _iconSpacing, Math.Clamp(value, 0, 40)); }
    double _iconScale = 90;
    bool _magnifyInside;
    /// <summary>Size of the icons in the dock (% of <see cref="IconSize"/>, which sets the size of the dock itself).</summary>
    public double IconScale { get => _iconScale; set => Set(ref _iconScale, Math.Clamp(value, 50, 150)); }

    int _accentMode;
    /// <summary>Colour of the parts of the app: 0 = the accent colour of Windows, 1 = <see cref="AccentColor"/>.</summary>
    public int AccentMode { get => _accentMode; set => Set(ref _accentMode, Math.Clamp(value, 0, 1)); }
    string _accentColor = "#0078D4";
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

    bool _showDownloads, _sysIconsOnBar = true;
    /// <summary>The Downloads button among Search, Task view and Recycle Bin.</summary>
    public bool ShowDownloads { get => _showDownloads; set => Set(ref _showDownloads, value); }
    /// <summary>With the top bar on: Search, Task view, Recycle Bin and Downloads are on it (otherwise in the dock).</summary>
    public bool SysIconsOnBar { get => _sysIconsOnBar; set => Set(ref _sysIconsOnBar, value); }
    [NotStored] public int SysIconsPlace { get => SysIconsOnBar ? 1 : 0; set { SysIconsOnBar = value == 1; Notify(); } }

    string _topBarWidgets = "";
    /// <summary>Quick buttons of the control center moved to the top bar (ids separated by |).</summary>
    public string TopBarWidgets { get => _topBarWidgets; set => Set(ref _topBarWidgets, value ?? ""); }
    int _classicSideWidth = 208;
    /// <summary>Width of the side column of the two-column Start (edit mode: with the mouse).</summary>
    public int ClassicSideWidth { get => _classicSideWidth; set => Set(ref _classicSideWidth, Math.Clamp(value, 160, 320)); }

    int _zoneKey;
    /// <summary>The key held while dragging a window into a zone: 0 = Shift, 1 = Ctrl, 2 = Alt.</summary>
    public int ZoneKey { get => _zoneKey; set => Set(ref _zoneKey, Math.Clamp(value, 0, 2)); }

    bool _topBarSystemStart = true;
    /// <summary>A click on the system menu of the top bar opens Start (right click: its menu).</summary>
    public bool TopBarSystemStart { get => _topBarSystemStart; set => Set(ref _topBarSystemStart, value); }
    string _zoneCustom = "", _calendarIcs = "";
    /// <summary>
    /// Own zone layouts, one per line: "name TAB zones", the zones x,y,width,height in percent separated by ;. Earlier versions
    /// had one own layout without a name (only the zones).
    /// </summary>
    public string ZoneCustom { get => _zoneCustom; set => Set(ref _zoneCustom, value ?? ""); }

    /// <summary>The own zone layouts (name, zones); a layout without a name is shown as "Custom".</summary>
    [NotStored] public List<(string Name, string Zones)> OwnZoneLayouts
    {
        get => _zoneCustom.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t') is { Length: 2 } p ? (p[0], p[1]) : ("", line)).Where(l => l.Item2.Length > 0).ToList();
        set => ZoneCustom = string.Join("\n", value.Select(l => l.Name.Replace('\t', ' ').Replace('\n', ' ').Trim() + "\t" + l.Zones));
    }
    /// <summary>Address or file of an internet calendar (.ics).</summary>
    public string CalendarIcs { get => _calendarIcs; set => Set(ref _calendarIcs, value ?? ""); }

    bool _zonesOn;
    int _zoneLayout;
    /// <summary>Window manager: a window dragged with Shift held falls into a zone of the screen.</summary>
    public bool ZonesOn { get => _zonesOn; set => Set(ref _zonesOn, value); }
    /// <summary>0 = two columns, 1 = three columns, 2 = four quarters, 3 = large on the left and two on the right.</summary>
    string _trayHidden = "";
    /// <summary>Programs whose tray icons are not shown at all (paths separated by |; Settings → Status icons).</summary>
    public string TrayHidden { get => _trayHidden; set => Set(ref _trayHidden, value ?? ""); }

    /// <summary>
    /// 0 two columns, 1 three columns, 2 four quarters, 3 large left and two right, 5 large right and two left; 10 + n the own
    /// layout n (4 = the one own layout of earlier versions, the same as 10).
    /// </summary>
    public int ZoneLayout { get => _zoneLayout; set => Set(ref _zoneLayout, Math.Clamp(value, 0, 10 + 999)); }

    /// <summary>The own layout chosen now (its number in <see cref="OwnZoneLayouts"/>), or -1 for a ready-made one.</summary>
    [NotStored] public int OwnZoneLayout => ZoneLayout == 4 ? 0 : ZoneLayout >= 10 ? ZoneLayout - 10 : -1;

    /// <summary>The order of the choice in the settings: the ready-made layouts, then the own ones (the stored numbers stay as they were).</summary>
    [NotStored] public int ZoneLayoutChoice
    {
        get => OwnZoneLayout is >= 0 and var own ? ZoneLayoutNames.Length + own : ZoneLayout == 5 ? 4 : ZoneLayout;
        set { ZoneLayout = value >= ZoneLayoutNames.Length ? 10 + value - ZoneLayoutNames.Length : value == 4 ? 5 : value; Notify(); }
    }

    /// <summary>The names of the ready-made zone layouts in the order of <see cref="ZoneLayoutChoice"/>.</summary>
    public static readonly string[] ZoneLayoutNames = { "Two columns", "Three columns", "Four quarters", "Large on the left, two on the right", "Large on the right, two on the left" };

    /// <summary>All layouts to choose from: the ready-made ones and the own ones by their names.</summary>
    public string[] ZoneLayoutChoices() => ZoneLayoutNames.Concat(OwnZoneLayouts.Select(l => l.Name.Length > 0 ? l.Name : "Custom")).ToArray();

    double _sysIconScale = 90;
    /// <summary>The same for the icons that are not apps: Start, Search, Task view and Recycle Bin.</summary>
    public double SysIconScale { get => _sysIconScale; set => Set(ref _sysIconScale, Math.Clamp(value, 50, 150)); }
    /// <summary>Magnification of the icon under the cursor (1 = none).</summary>
    public double Magnification { get => _magnification; set => Set(ref _magnification, Math.Clamp(value, 1.0, 2.0)); }
    /// <summary>Magnified icons stay inside the dock (they grow only into the room smaller icons leave, nothing is pushed aside).</summary>
    public bool MagnifyInside { get => _magnifyInside; set => Set(ref _magnifyInside, value); }
    /// <summary>Corner radius of the dock (px).</summary>
    public double Rounding { get => _rounding; set => Set(ref _rounding, Math.Clamp(value, 0, 40)); }
    /// <summary>Opacity of the dock tint (%).</summary>
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 10, 100)); }
    /// <summary>The same as transparency for the settings window (100 − opacity; stored as <see cref="BackgroundOpacity"/>).</summary>
    [NotStored]
    public double Transparency { get => 100 - BackgroundOpacity; set => BackgroundOpacity = 100 - value; }
    /// <summary>Distance of the dock from the bottom screen edge (px).</summary>
    public double BottomMargin { get => _bottomMargin; set => Set(ref _bottomMargin, Math.Clamp(value, 0, 60)); }
    public bool ShowStartButton { get => _showStartButton; set => Set(ref _showStartButton, value); }
    /// <summary>Search and Task view buttons next to Start.</summary>
    public bool ShowSearchButtons { get => _showSearchButtons; set => Set(ref _showSearchButtons, value); }
    bool _showTaskViewButton;
    /// <summary>Task view button next to Start (the search button is <see cref="ShowSearchButtons"/>).</summary>
    public bool ShowTaskViewButton { get => _showTaskViewButton; set => Set(ref _showTaskViewButton, value); }
    public bool ShowRunningIndicator { get => _showRunningIndicator; set => Set(ref _showRunningIndicator, value); }
    /// <summary>Icon bounces while an app starts and when a window asks for attention.</summary>
    public bool LaunchAnimation { get => _launchAnimation; set => Set(ref _launchAnimation, value); }
    int _animationSpeed = 2;                                          // fast
    /// <summary>Speed of all animations (magnification, reordering, pop-ups): 0 = slow, 1 = normal, 2 = fast, 3 = off.</summary>
    public int AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, Math.Clamp(value, 0, 3)); }
    public bool ShowDesktopButton { get => _showDesktopButton; set => Set(ref _showDesktopButton, value); }
    /// <summary>The dock slides away and does not reserve screen space.</summary>
    public bool AutoHide { get => _autoHide; set => Set(ref _autoHide, value); }
    bool _autoHideDodge;
    /// <summary>Auto-hide only while a window covers the dock – otherwise it stays visible.</summary>
    public bool AutoHideDodge { get => _autoHideDodge; set => Set(ref _autoHideDodge, value); }
    bool _showDesktops;
    /// <summary>Virtual desktops of Windows in the dock: the current one, a click switches (off by default).</summary>
    public bool ShowDesktops { get => _showDesktops; set => Set(ref _showDesktops, value); }
    /// <summary>HeMiDock starts when the user signs in to Windows (stored by Windows, applied at once).</summary>
    [NotStored]
    public bool StartAtSignIn
    {
        get => Autostart.IsOn;
        set { if (value != Autostart.IsOn) { Autostart.IsOn = value; Notify(); } }
    }
    /// <summary>Frosted glass: 0 = follow Windows ("Transparency effects"), 1 = always on, 2 = off.</summary>
    public int GlassMode { get => _glassMode; set => Set(ref _glassMode, Math.Clamp(value, 0, 2)); }
    /// <summary>Frosted glass behind the dock, Start menu and pop-ups right now (from <see cref="GlassMode"/>).</summary>
    public bool Blur => !NoTransparency && !LowGraphics && GlassMode switch { 1 => true, 2 => false, _ => Theme.WindowsTransparency };
    /// <summary>Transparency effects are off in Windows and followed: panels are solid like the Windows ones.</summary>
    bool Solid => NoTransparency || LowGraphics || GlassMode == 0 && !Theme.WindowsTransparency;
    bool _showRemovable = true;
    bool _lowGraphicsOn, _lowGraphicsOnBattery = true, _batteryLow;
    /// <summary>No transparency anywhere: all panels of the app are solid and there is no frosted glass.</summary>
    public bool NoTransparency => GlassMode == 2;
    /// <summary>Lighter drawing switched on by the user (off by default).</summary>
    public bool LowGraphicsOn { get => _lowGraphicsOn; set => Set(ref _lowGraphicsOn, value); }
    /// <summary>Lighter drawing also while the battery is low (on by default): battery saver, or on battery at 20 % or less.</summary>
    public bool LowGraphicsOnBattery { get => _lowGraphicsOnBattery; set { Set(ref _lowGraphicsOnBattery, value); CheckBattery(); } }
    /// <summary>The two switches as one choice of the settings: 0 = off, 1 = always, 2 = with a low battery (battery saver or 20 %).</summary>
    [NotStored]
    public int LowGraphicsChoice
    {
        get => LowGraphicsOn ? 1 : LowGraphicsOnBattery ? 2 : 0;
        set { LowGraphicsOn = value == 1; LowGraphicsOnBattery = value == 2; Notify(); }
    }
    /// <summary>Lighter drawing now: solid panels, no shadows and no frosted glass.</summary>
    public bool LowGraphics => LowGraphicsOn || LowGraphicsOnBattery && _batteryLow || LegacyGpu;
    static bool? _legacyGpu;
    /// <summary>
    /// Graphics drivers of the old model (WDDM 1.x, version below 20): the transparent panels of the app were black on them –
    /// the lighter drawing is used instead.
    /// </summary>
    public static bool LegacyGpu => _legacyGpu ??= DetectLegacyGpu();

    static bool DetectLegacyGpu()
    {
#if DEBUG
        if (Environment.GetCommandLineArgs().SkipWhile(a => a != "--simulate").Skip(1).FirstOrDefault()?.Contains("oldgpu") == true) return true;
#endif
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var versions = key?.GetSubKeyNames().Where(n => n.All(char.IsDigit))
                .Select(n => { using var k = key.OpenSubKey(n); return k?.GetValue("DriverVersion") as string; })
                .Where(v => v != null).Select(v => int.TryParse(v.Split('.')[0], out int major) ? major : 99).ToList();
            return versions is { Count: > 0 } && versions.All(major => major < 20);
        }
        catch { return false; }
    }
    /// <summary>Called regularly: the look changes when the battery becomes low or is charged again.</summary>
    public void CheckBattery()
    {
        bool low = _lowGraphicsOnBattery && Native.GetSystemPowerStatus(out var st) && st.BatteryFlag != 128 && st.BatteryLifePercent != 255
                   && (st.SystemStatusFlag == 1 || st.ACLineStatus != 1 && st.BatteryLifePercent <= 20);
        if (low == _batteryLow) return;
        _batteryLow = low;
        Diag.Write("Battery", low ? "low – lighter drawing" : "no longer low");
        Notify();
    }
    /// <summary>Icon among the status icons while a removable drive is connected (a click: open / eject).</summary>
    public bool ShowRemovable { get => _showRemovable; set => Set(ref _showRemovable, value); }
    string _quickSections = "", _quickTiles = "", _quickHidden = "";
    /// <summary>Order of the parts of the control center (ids of <see cref="QuickLayout.Sections"/>, separated by |; empty = default).</summary>
    public string QuickSections { get => _quickSections; set => Set(ref _quickSections, value ?? ""); }
    /// <summary>Order of the quick buttons of the control center (ids of <see cref="QuickLayout.Tiles"/>).</summary>
    public string QuickTiles { get => _quickTiles; set => Set(ref _quickTiles, value ?? ""); }
    /// <summary>Hidden parts and quick buttons of the control center.</summary>
    public string QuickHidden { get => _quickHidden; set => Set(ref _quickHidden, value ?? ""); }

    bool _dockEditMode;
    /// <summary>
    /// Edit mode of the dock, which also shows all options of the settings: spaces between icons, own names and pictures of pinned apps in the menus;
    /// spaces are outlined so they can be found.
    /// </summary>
    public bool DockEditMode { get => _dockEditMode; set => Set(ref _dockEditMode, value); }
    bool _topBar, _topBarAppName = true;
    int _topBarClockPos = 2, _toastPosition;
    string _topBarOrder = "";
    double _topBarHeight = 32;
    /// <summary>
    /// Top bar (like macOS / GNOME, off by default): the notification area, status icons, keyboard language, clock and
    /// virtual desktops move from the dock to a bar along the top edge – the dock keeps Start and the apps.
    /// </summary>
    public bool TopBar { get => _topBar; set { Set(ref _topBar, value); if (value && DockPosition == 3) DockPosition = 0; } }   // the dock leaves the top edge
    /// <summary>Clock in the top bar: 0 = left, 1 = in the middle (GNOME), 2 = right (macOS).</summary>
    public int TopBarClockPos { get => _topBarClockPos; set => Set(ref _topBarClockPos, Math.Clamp(value, 0, 2)); }
    /// <summary>Parts of the top bar moved in edit mode: "left ids;middle ids;right ids" (ids separated by |; empty = default).</summary>
    public string TopBarOrder { get => _topBarOrder; set => Set(ref _topBarOrder, value ?? ""); }
    /// <summary>Name of the active app on the left of the top bar (a click: its windows and actions).</summary>
    public bool TopBarAppName { get => _topBarAppName; set => Set(ref _topBarAppName, value); }
    /// <summary>Height of the top bar (px).</summary>
    public double TopBarHeight { get => _topBarHeight; set => Set(ref _topBarHeight, Math.Clamp(value, 24, 44)); }
    /// <summary>The top bar is shown: switched on and the dock not at the top edge itself.</summary>
    public bool TopBarOn => TopBar && DockPosition != 3;

    bool _topBarSystemMenu = true, _topBarAutoHide, _topBarMedia = true,
         _showCapsLock, _showNumLock, _showStatusSigns = true;
    /// <summary>System menu on the very left of the top bar (settings, Task Manager, lock, sleep, restart, shut down).</summary>
    public bool TopBarSystemMenu { get => _topBarSystemMenu; set => Set(ref _topBarSystemMenu, value); }
    /// <summary>Search button in the top bar (opens the quick search).</summary>

    /// <summary>The top bar is clear (only its content) while no window touches it, like in GNOME.</summary>

    /// <summary>The top bar hides and shows when the mouse reaches the top screen edge (off by default).</summary>
    public bool TopBarAutoHide { get => _topBarAutoHide; set => Set(ref _topBarAutoHide, value); }
    /// <summary>What is playing (title, play / pause) in the top bar.</summary>
    public bool TopBarMedia { get => _topBarMedia; set => Set(ref _topBarMedia, value); }
    /// <summary>Caps Lock while it is on (in the dock or the top bar; off by default).</summary>

    public bool ShowCapsLock { get => _showCapsLock; set => Set(ref _showCapsLock, value); }
    /// <summary>Num Lock while it is on (in the dock or the top bar; off by default).</summary>
    public bool ShowNumLock { get => _showNumLock; set => Set(ref _showNumLock, value); }
    bool _monitorCpu = true, _monitorRam = true, _monitorGpu = true, _monitorVram = true, _monitorNet = true;
    /// <summary>Video memory of the graphics card in the system monitor.</summary>
    public bool MonitorVram { get => _monitorVram; set => Set(ref _monitorVram, value); }
    /// <summary>What the system monitor shows: processor, memory, graphics card, network speed.</summary>
    public bool MonitorCpu { get => _monitorCpu; set => Set(ref _monitorCpu, value); }
    public bool MonitorRam { get => _monitorRam; set => Set(ref _monitorRam, value); }
    public bool MonitorGpu { get => _monitorGpu; set => Set(ref _monitorGpu, value); }
    public bool MonitorNet { get => _monitorNet; set => Set(ref _monitorNet, value); }

    string _outlookCalendarsHidden = "";
    /// <summary>Calendars of Outlook that are not shown (names, separated by |).</summary>
    public string OutlookCalendarsHidden { get => _outlookCalendarsHidden; set => Set(ref _outlookCalendarsHidden, value ?? ""); }
    int _dockFull;
    /// <summary>Width of a dock at the bottom / top: 0 = as long as its icons, 1 = full width with the icons on the left, 2 = full width with the icons in the middle.</summary>
    public int DockFull { get => _dockFull; set => Set(ref _dockFull, Math.Clamp(value, 0, 2)); }
    bool _setupDone;
    /// <summary>The guide of the first start was shown (settings of earlier versions count as done).</summary>
    public bool SetupDone { get => _setupDone; set => Set(ref _setupDone, value); }

    /// <summary>Appointments of the classic Outlook in the calendar of the clock (off by default, see <see cref="OutlookCalendar"/>).</summary>
    public bool CalendarEvents => CalendarSource == 1;

    bool _showSystemMonitor;
    /// <summary>Processor, memory, graphics card and network: a small grid in the dock, one line in the top bar (off by default).</summary>
    public bool ShowSystemMonitor { get => _showSystemMonitor; set => Set(ref _showSystemMonitor, value); }
    /// <summary>Do not disturb, VPN and muted microphone among the status icons (on by default – shown only while so).</summary>
    public bool ShowStatusSigns { get => _showStatusSigns; set => Set(ref _showStatusSigns, value); }

    /// <summary>
    /// Where the notifications of Windows appear (see <see cref="ToastMover"/>): 0 = automatic, 1 = at the clock,
    /// 2 = top right, 3 = top left, 4 = bottom right (where Windows has them), 5 = bottom left.
    /// </summary>
    public int ToastPosition { get => _toastPosition; set => Set(ref _toastPosition, Math.Clamp(value, 0, 5)); }
    /// <summary>The place used now: automatic = at the clock of a bottom dock, top right with the top bar, otherwise where Windows has them.</summary>
    public int ToastPlace
    {
        get
        {
            bool clockFits = TopBarOn || DockPosition == 0;            // a pop-up fits only above / below the clock
            if (ToastPosition == 0) return TopBarOn ? 2 : clockFits ? 1 : 4;
            return ToastPosition == 1 && !clockFits ? 4 : ToastPosition;
        }
    }
    /// <summary>The notifications are moved now (bottom right is where Windows itself shows them).</summary>
    public bool ToastsMoved => ToastPlace != 4;    bool _debugMode;
    /// <summary>Debug mode: what the app does is written into debug-log.txt (see <see cref="Diag"/>; off by default).</summary>
    public bool DebugMode { get => _debugMode; set => Set(ref _debugMode, value); }
    string _dockOrder = "";
    /// <summary>Order of the parts of the dock, moved with the mouse in edit mode (ids of <see cref="MainWindow"/>; empty = default).</summary>
    public string DockOrder { get => _dockOrder; set => Set(ref _dockOrder, value ?? ""); }
    string _classicPlacesOrder = "";
    /// <summary>Order of the side column of the two-column Start menu ("a|b;c|d" – groups by ';'; empty = default).</summary>
    public string ClassicPlacesOrder { get => _classicPlacesOrder; set => Set(ref _classicPlacesOrder, value ?? ""); }
    bool _showPrivacy = true, _showCameraUse = true, _showLocationUse;
    /// <summary>Orange microphone icon while an app uses it (the name stays from when it was also the camera).</summary>
    public bool ShowPrivacy { get => _showPrivacy; set => Set(ref _showPrivacy, value); }
    /// <summary>Orange camera icon while an app uses it (on as the microphone one of earlier versions).</summary>
    public bool ShowCameraUse { get => _showCameraUse; set => Set(ref _showCameraUse, value); }
    /// <summary>Orange location pin while an app uses the location (new: off by default).</summary>
    public bool ShowLocationUse { get => _showLocationUse; set => Set(ref _showLocationUse, value); }
    bool _checkUpdates = true;
    /// <summary>New versions looked for on GitHub after the start and once a day (on: the update is what the user asked for).</summary>
    public bool CheckUpdates { get => _checkUpdates; set => Set(ref _checkUpdates, value); }
    string _skippedUpdate = "";
    /// <summary>A version the user chose to skip ("1.6.0"): not offered by the automatic check.</summary>
    public string SkippedUpdate { get => _skippedUpdate; set => Set(ref _skippedUpdate, value ?? ""); }
    bool _showSecurity;
    /// <summary>The shield of Windows Security among the status icons instead of its icon in the notification area (new: off by default).</summary>
    public bool ShowSecurity { get => _showSecurity; set => Set(ref _showSecurity, value); }
    bool _showRecycleBin;
    /// <summary>Recycle Bin at the end of the apps (empty / full, drop files on it to delete them).</summary>
    public bool ShowRecycleBin { get => _showRecycleBin; set => Set(ref _showRecycleBin, value); }
    bool _appShortcuts;
    /// <summary>Win+1 … Win+9, Win+0 start / switch to the apps of the dock in their order (like the Windows taskbar).</summary>
    public bool AppShortcuts { get => _appShortcuts; set => Set(ref _appShortcuts, value); }
    /// <summary>Live window thumbnails when hovering a running app.</summary>
    public bool ShowPreviews { get => _showPreviews; set => Set(ref _showPreviews, value); }

    // detailed look and behaviour – the defaults are exactly the original look
    double _dockShadow = 35, _endPadding = 3, _magnificationSpread = 2.5, _autoHideDelay = 0.8;
    bool _dockOutline, _showAppNames = true;

    /// <summary>Strength of the shadow under the dock (%).</summary>
    public double DockShadow { get => _dockShadow; set => Set(ref _dockShadow, Math.Clamp(value, 0, 100)); }
    /// <summary>Thin outline around the dock.</summary>
    /// <summary>The line around the dock and around all windows of Windows (see <see cref="WindowOutlines"/>).</summary>
    public bool DockOutline { get => _dockOutline; set => Set(ref _dockOutline, value); }
    /// <summary>Room between the ends of the dock and the first / last icon (px).</summary>
    public double EndPadding { get => _endPadding; set => Set(ref _endPadding, Math.Clamp(value, 0, 32)); }
    /// <summary>How many icons around the cursor grow with the magnification (width of the wave, in icons).</summary>
    public double MagnificationSpread { get => _magnificationSpread; set => Set(ref _magnificationSpread, Math.Clamp(value, 1.5, 6)); }
    /// <summary>Name of the app above its icon while the mouse is over it.</summary>
    public bool ShowAppNames { get => _showAppNames; set => Set(ref _showAppNames, value); }
    /// <summary>How long the dock waits before it hides itself (s, auto-hide).</summary>
    public double AutoHideDelay { get => _autoHideDelay; set => Set(ref _autoHideDelay, Math.Clamp(value, 0.2, 3)); }

    // ───────────────────────── right side ─────────────────────────
    double _trayIconSize = 16, _statusIconSize = 15, _clockFontSize = 13, _dateFontSize = 11.5;
    bool _showTrayArea = true, _showWifi = true, _showVolume = true, _showBattery = true, _showBatteryPercent = true, _showBluetooth = true,
         _showClock = true, _showDate = true, _showSeconds;
    int _clockMode, _dateMode = 2;
    string _customDateFormat = "d MMM yyyy";

    public bool ShowTrayArea { get => _showTrayArea; set => Set(ref _showTrayArea, value); }
    public double TrayIconSize { get => _trayIconSize; set => Set(ref _trayIconSize, Math.Clamp(value, 12, 32)); }
    public bool ShowWifi { get => _showWifi; set => Set(ref _showWifi, value); }
    public bool ShowVolume { get => _showVolume; set => Set(ref _showVolume, value); }
    bool _showVolumePercent;
    /// <summary>Exact volume (0–100) next to the volume icon.</summary>
    public bool ShowVolumePercent { get => _showVolumePercent; set => Set(ref _showVolumePercent, value); }
    /// <summary>Bluetooth icon in the dock and the control center – only on devices that have Bluetooth.</summary>
    public bool ShowBluetooth { get => _showBluetooth; set => Set(ref _showBluetooth, value); }
    bool _showBluetoothBattery;
    /// <summary>The top bar shows the charge of the connected Bluetooth devices next to their icon.</summary>
    public bool ShowBluetoothBattery { get => _showBluetoothBattery; set => Set(ref _showBluetoothBattery, value); }
    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }
    public bool ShowBatteryPercent { get => _showBatteryPercent; set => Set(ref _showBatteryPercent, value); }
    bool _showInputLanguage = true, _showNotificationCount = true;
    /// <summary>Language of the keyboard (CES, ENG …) – only with more than one keyboard; a click switches it.</summary>
    public bool ShowInputLanguage { get => _showInputLanguage; set => Set(ref _showInputLanguage, value); }
    /// <summary>Number of new notifications next to the clock (since the calendar was last opened).</summary>
    public bool ShowNotificationCount { get => _showNotificationCount; set => Set(ref _showNotificationCount, value); }
    int _osThemeSchedule;
    string _osThemeLightAt = "07:00", _osThemeDarkAt = "19:00";
    /// <summary>The light / dark mode of Windows by itself: 0 = by hand (Windows), 1 = sunrise and sunset, 2 = own times.</summary>
    public int OsThemeSchedule { get => _osThemeSchedule; set { Set(ref _osThemeSchedule, Math.Clamp(value, 0, 2)); if (!_suspend) ThemeSchedule.Apply(); } }

    /// <summary>
    /// The choice "Windows theme" of the settings: 0 light, 1 dark (Windows is switched at once, no schedule), 2 by sunrise
    /// and sunset, 3 at own times. Light and dark show what Windows is now.
    /// </summary>
    [NotStored] public int OsTheme
    {
        get => OsThemeSchedule switch { 1 => 2, 2 => 3, _ => ThemeSchedule.WindowsLight ? 0 : 1 };
        set
        {
            if (value <= 1) { OsThemeSchedule = 0; ThemeSchedule.SetWindows(value == 0); }
            else OsThemeSchedule = value == 2 ? 1 : 2;
            Notify();
        }
    }
    /// <summary>Own times of the schedule ("HH:mm"): light from, dark from.</summary>
    public string OsThemeLightAt { get => _osThemeLightAt; set { Set(ref _osThemeLightAt, value ?? "07:00"); if (!_suspend) ThemeSchedule.Apply(); } }
    public string OsThemeDarkAt { get => _osThemeDarkAt; set { Set(ref _osThemeDarkAt, value ?? "19:00"); if (!_suspend) ThemeSchedule.Apply(); } }
    bool _removeReadMail;
    /// <summary>A notification of Outlook goes from the notification center once its message is read (off by default).</summary>
    public bool RemoveReadMail { get => _removeReadMail; set => Set(ref _removeReadMail, value); }
    public double StatusIconSize { get => _statusIconSize; set => Set(ref _statusIconSize, Math.Clamp(value, 12, 28)); }
    public bool ShowClock { get => _showClock; set => Set(ref _showClock, value); }
    public bool ShowDate { get => _showDate; set => Set(ref _showDate, value); }
    /// <summary>0 = follow Windows, 1 = 24-hour, 2 = 12-hour.</summary>
    public int ClockMode { get => _clockMode; set => Set(ref _clockMode, Math.Clamp(value, 0, 2)); }
    public bool ShowSeconds { get => _showSeconds; set => Set(ref _showSeconds, value); }
    /// <summary>0 = weekday + date without year, 1 = long date, 2 = short date, 3 = custom format.</summary>
    public int DateMode { get => _dateMode; set => Set(ref _dateMode, Math.Clamp(value, 0, 3)); }
    public string CustomDateFormat { get => _customDateFormat; set => Set(ref _customDateFormat, value ?? ""); }
    public double ClockFontSize { get => _clockFontSize; set { Set(ref _clockFontSize, Math.Clamp(value, 10, 28)); DateFontSize = _clockFontSize - 1.5; } }   // the date follows
    public double DateFontSize { get => _dateFontSize; set => Set(ref _dateFontSize, Math.Clamp(value, 8, 20)); }
    /// <summary>0 = time above date right-aligned, 1 = centered, 2 = time and date on one line.</summary>


    // ───────────────────────── Start menu and flyouts ─────────────────────────
    bool _replaceStartMenu = true, _startShowRecent = false, _startAppsGrid,
         _startShowUser = true;
    double _startWidth = 570, _startHeight = 580, _startIconSize = 36;

    string _powerMenuHidden = "";

    /// <summary>Items hidden in the Start button right-click menu (ids from <see cref="PowerMenu"/>, separated by |).</summary>
    public string PowerMenuHidden { get => _powerMenuHidden; set => Set(ref _powerMenuHidden, value ?? ""); }
    string _powerMenuOrder = "", _startPowerOrder = "";
    /// <summary>Order of the Start button menu, set in its settings ("a|b;c|d" – groups by ';'; empty = default).</summary>
    public string PowerMenuOrder { get => _powerMenuOrder; set => Set(ref _powerMenuOrder, value ?? ""); }
    /// <summary>Order of the power items (Start and "Shut down or sign out"), same form; empty = default.</summary>
    public string StartPowerOrder { get => _startPowerOrder; set => Set(ref _startPowerOrder, value ?? ""); }

    int _startLayout = 1;
    /// <summary>Look of the Start menu: 0 = modern (grid of pinned apps), 1 = two columns (programs and places, the default), 2 = full screen (app overview).</summary>
    public int StartLayout { get => _startLayout; set => Set(ref _startLayout, Math.Clamp(value, 0, 2)); }
    bool _classicSideLeft = true;
    /// <summary>Classic Start menu: the column with the user, places and power on the left (otherwise on the right).</summary>
    public bool ClassicSideLeft { get => _classicSideLeft; set => Set(ref _classicSideLeft, value); }
    string _classicPlacesHidden = StartSide.DefaultPlacesHidden, _startPowerHidden = StartSide.DefaultPowerHidden;
    /// <summary>Items hidden in the side column of the two-column Start menu (ids from <see cref="StartSide"/>, separated by |).</summary>
    public string ClassicPlacesHidden { get => _classicPlacesHidden; set => Set(ref _classicPlacesHidden, value ?? ""); }
    /// <summary>Items hidden in the power menu of the Start menu (ids from <see cref="StartSide"/>, separated by |).</summary>
    public string StartPowerHidden { get => _startPowerHidden; set => Set(ref _startPowerHidden, value ?? ""); }

    /// <summary>Size of the Start menu (px).</summary>
    public double StartWidth { get => _startWidth; set => Set(ref _startWidth, Math.Clamp(value, 460, 960)); }
    public double StartHeight { get => _startHeight; set => Set(ref _startHeight, Math.Clamp(value, 440, 960)); }
    /// <summary>Icon size of pinned apps in the Start menu (px).</summary>
    public double StartIconSize { get => _startIconSize; set => Set(ref _startIconSize, Math.Clamp(value, 24, 56)); }
    /// <summary>The user (picture and name) in the Start menu.</summary>
    public bool StartShowUser { get => _startShowUser; set => Set(ref _startShowUser, value); }
    bool _startShowPower = true;
    /// <summary>The power button in the Start menu (was one switch with the user before).</summary>
    public bool StartShowPower { get => _startShowPower; set => Set(ref _startShowPower, value); }

    /// <summary>Own Start menu instead of Windows (Start button, Win key, Ctrl+Esc, Win+S).</summary>
    public bool ReplaceStartMenu { get => _replaceStartMenu; set => Set(ref _replaceStartMenu, value); }
    public bool StartShowRecent { get => _startShowRecent; set => Set(ref _startShowRecent, value); }
    /// <summary>All apps in the Start menu as a grid of icons (like the pinned ones) instead of a list.</summary>
    public bool StartAppsGrid { get => _startAppsGrid; set => Set(ref _startAppsGrid, value); }
    /// <summary>Click on the clock opens the calendar flyout (otherwise the Windows notification panel).</summary>
    

    // ───────────────────────── derived values ─────────────────────────

    /// <summary>The icon as it is drawn (the dock keeps the size from <see cref="IconSize"/>).</summary>
    public double DrawnIconSize => Math.Round(IconSize * IconScale / 100);
    public double DrawnSysIconSize => Math.Round(IconSize * SysIconScale / 100);
    /// <summary>The icon under the mouse only gets the highlight: chosen so, or the dock is full width (it has no room to grow).</summary>
    public bool HighlightOnly => HoverEffect == 1 || (DockFull != 0 && !IsVertical);

    /// <summary>The largest magnification right now: inside the dock only up to the room around the icon.</summary>
    public double EffectiveMagnification =>
        HighlightOnly ? 1 : MagnifyInside ? Math.Min(Magnification, Math.Max(1, (IconSize + 4) / DrawnIconSize)) : Magnification;

    // ───────────────────────── values ─────────────────────────

    DockSettings(bool load)
    {
        if (load) Load();
    }

    void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (!_suspend) Diag.SettingChanged(name, value);
        Notify();
    }

    void Notify()
    {
        if (_suspend) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // empty name = refresh all bindings
    }

    public Dictionary<string, object> Snapshot() => Stored.ToDictionary(p => p.Name, p => p.GetValue(this));

    /// <summary>Applies values from a dictionary (unknown keys are skipped) and refreshes the look once at the end.</summary>
    public void Restore(Dictionary<string, object> values)
    {
        _suspend = true;
        try
        {
            foreach (var p in Stored)
            {
                if (!values.TryGetValue(p.Name, out var v) || v == null) continue;
                try { p.SetValue(this, Convert.ChangeType(v, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture)); }
                catch { }
            }
        }
        finally { _suspend = false; }
        Notify();
    }

    public void ResetToDefaults()
    {
        Host.Backup?.Invoke("before reset");
        var defaults = new DockSettings(false).Snapshot();
        defaults[nameof(SetupDone)] = true;                           // the guide is not shown again
        Restore(defaults);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(AppPaths.Of("settings.json"), JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Settings.Save", ex); }
    }

    void Load()
    {
        try
        {
            var file = AppPaths.Of("settings.json");
            if (File.Exists(file)) Apply(File.ReadAllText(file));
        }
        catch (Exception ex) { Log.Write("Settings.Load", ex); }
    }

    /// <summary>Writes the settings to a file of the user's choice (Settings → Export), optionally with the pinned apps.</summary>
    public void Export(string file, bool pins)
    {
        var values = Snapshot();
        if (pins) Host.AddPins?.Invoke(values);
        File.WriteAllText(file, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The exported file also has the pinned apps of the dock and Start.</summary>
    public static bool HasPins(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return Host.HasPins?.Invoke(doc.RootElement) == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Takes over the settings from an exported file and saves them – with <paramref name="pins"/> also its pinned apps
    /// (instead of the current ones); false when the file is not HeMiDock settings.
    /// </summary>
    public bool Import(string file, bool pins, bool backup = true)
    {
        try
        {
            if (backup) Host.Backup?.Invoke("before import");
            string json = File.ReadAllText(file);
            if (!Apply(json)) return false;
            Save();
            if (pins)
            {
                using var doc = JsonDocument.Parse(json);
                Host.ApplyPins?.Invoke(doc.RootElement);
            }
            return true;
        }
        catch (Exception ex) { Log.Write("Settings.Import", ex); return false; }
    }

    /// <summary>Applies settings from JSON (unknown keys are skipped); false when it is not a settings object.</summary>
    bool Apply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

        var values = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            object v = prop.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => prop.Value.GetString(),
                _ => null
            };
            if (v != null) values[prop.Name] = v;
        }
        // older versions had an on / off switch: on now follows Windows, off stays off
        if (!values.ContainsKey(nameof(GlassMode)) && values.TryGetValue("Blur", out var blur) && blur is bool on)
            values[nameof(GlassMode)] = on ? 0 : 2;
        // older versions had one switch for the search and the Task view button
        if (!values.ContainsKey(nameof(ShowTaskViewButton)) && values.TryGetValue(nameof(ShowSearchButtons), out var both))
            values[nameof(ShowTaskViewButton)] = both;
        // older versions had an on / off switch and three shortcuts for the quick search (0 = Alt, 1 = Win, 2 = Ctrl + Space)
        if ((!values.TryGetValue(nameof(Hotkeys), out var keys) || keys is "") && values.TryGetValue("Spotlight", out var quick) && quick is bool quickOn)
        {
            int mods = values.TryGetValue("SpotlightKey", out var k) && k is double d ? (int)d switch { 1 => HeMiDock.Hotkeys.Win, 2 => HeMiDock.Hotkeys.Ctrl, _ => HeMiDock.Hotkeys.Alt } : HeMiDock.Hotkeys.Alt;
            values[nameof(Hotkeys)] = quickOn ? $"spotlight={mods}:32" : "spotlight=0:0";
        }
        if (!Stored.Any(p => values.ContainsKey(p.Name))) return false;   // some other JSON file
        if (!values.ContainsKey(nameof(SetupDone))) values[nameof(SetupDone)] = true;
        // earlier versions: other looks of the Start button (those that are gone become the monochrome logo)
        if (!values.ContainsKey(nameof(StartIconLook)) && values.TryGetValue("StartButtonStyle", out var oldLook))
            values[nameof(StartIconLook)] = (oldLook is double look ? (int)look : 0) switch { 1 => 2, 2 or 3 or 4 or 5 => 1, 6 => 4, _ => 0 };
        if (!values.ContainsKey(nameof(CalendarSource)) && values.TryGetValue("CalendarEvents", out var outlookOn) && outlookOn is true) values[nameof(CalendarSource)] = 1;
        // earlier versions: separate switches and choices that are one now
        if (values.TryGetValue("NoTransparency", out var solid) && solid is true) values[nameof(GlassMode)] = 2;
        // earlier versions: one switch for the microphone and the camera
        if (!values.ContainsKey(nameof(ShowCameraUse)) && values.TryGetValue(nameof(ShowPrivacy), out var privacyOn)) values[nameof(ShowCameraUse)] = privacyOn;
        // earlier versions: one switch for the user and the power button of Start
        if (!values.ContainsKey(nameof(StartShowPower)) && values.TryGetValue(nameof(StartShowUser), out var user)) values[nameof(StartShowPower)] = user;
        // earlier versions: a switch "US date" (MM/DD/YYYY), before it a fifth date format; now the order of the day and the month
        if (!values.ContainsKey(nameof(DateOrder)))
        {
            if (values.TryGetValue(nameof(DateMode), out var oldDate) && oldDate is 4.0) { values[nameof(DateOrder)] = 2.0; values[nameof(DateMode)] = 2.0; }
            if (values.TryGetValue("DateUs", out var us) && us is true) values[nameof(DateOrder)] = 2.0;
        }
        if (!values.ContainsKey(nameof(ClockLine)) && values.TryGetValue("ClockLayout", out var oldLine) && oldLine is 2.0) values[nameof(ClockLine)] = 1;
        // earlier versions: one size for all icons of the dock
        if (!values.ContainsKey(nameof(SysIconScale)) && values.TryGetValue(nameof(IconScale), out var oneScale)) values[nameof(SysIconScale)] = oneScale;
        // earlier versions: the lighter drawing had one switch, then three modes (1 = on)
        if (!values.ContainsKey(nameof(LowGraphicsOn)) && (values.TryGetValue("LowGraphics", out var low) && low is true
            || values.TryGetValue("LowGraphicsMode", out var mode) && mode is double m && m == 1)) values[nameof(LowGraphicsOn)] = true;
        // older versions: the clock of the top bar was a switch; the corners without glass were rounder than with it
        if (!values.ContainsKey(nameof(TopBarClockPos)))
        {
            if (values.TryGetValue("TopBarClockCenter", out var center) && center is true) values[nameof(TopBarClockPos)] = 1.0;
            if (values.TryGetValue(nameof(Rounding), out var round) && round is double rd && rd == 16) values[nameof(Rounding)] = 8.0;
        }
        // earlier versions: the room at the ends of the dock was wider by default (8); an own value stays
        if (!values.ContainsKey(nameof(EndPadding)) && values.TryGetValue("DockPadding", out var oldPad) && oldPad is double op && op != 8) values[nameof(EndPadding)] = op;
        Restore(values);
        return true;
    }
}
