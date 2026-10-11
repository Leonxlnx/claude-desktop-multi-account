// Claude accounts: a small window and tray icon that lists every account window with
// the plan usage it last saved, opens account windows, and gives each one its own
// taskbar button. Built with the C# compiler that ships with Windows (see `ccm build`).
//
// It reads only what Claude itself saves in plain files: the usage history each
// desktop window writes, and the usage snapshot Claude Code caches. It never reads a
// login and never talks to Anthropic.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Claude accounts")]
[assembly: System.Reflection.AssemblyProduct("claude-desktop-multi-account")]

namespace ClaudeAccounts
{
    static class Paths
    {
        static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string Root = Environment.GetEnvironmentVariable("CCM_HOME") ?? Path.Combine(Home, ".claude-accounts");
        public static readonly string MainData =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        public static readonly string App =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\claude-desktop.exe");
        public static readonly string CodeState = Path.Combine(Home, ".claude.json");
        public static readonly string Exe = Process.GetCurrentProcess().MainModule.FileName;

        public static System.Drawing.Icon Icon()
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(Exe); }
            catch { return System.Drawing.SystemIcons.Application; }
        }
    }

    // One number the desktop app saved: when, for which organisation, and the two percentages.
    class Sample
    {
        public DateTime Time;
        public string Org;
        public double? FiveHour;
        public double? Week;
    }

    // One limit from the snapshot Claude Code caches, which does include the reset time.
    class CachedLimit
    {
        public string Kind;      // "session" is the 5-hour window
        public string Model;     // set for a per-model weekly limit, such as Fable
        public double Percent;
        public DateTime ResetsAt;
    }

    class Folder
    {
        public string Name;      // null for the normal Claude app
        public string Dir;
        public List<Sample> Samples = new List<Sample>();
        public string Account;   // the account and organisation this window used last
        public string Org;
    }

    // One card in the window. Everything the template binds to is a public property.
    public class AccountRow
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Label { get; set; }
        public Brush TagBrush { get; set; }
        public string Status { get; set; }
        public Brush StatusBrush { get; set; }
        public Visibility UsageVisibility { get; set; }
        public Visibility WeekVisibility { get; set; }
        public string FiveText { get; set; }
        public GridLength FiveFill { get; set; }
        public GridLength FiveRest { get; set; }
        public Brush FiveBrush { get; set; }
        public string WeekText { get; set; }
        public GridLength WeekFill { get; set; }
        public GridLength WeekRest { get; set; }
        public Brush WeekBrush { get; set; }
        public List<string> Lines { get; set; }
    }

    static class Accounts
    {
        // Same order and colours as the icons `ccm` draws.
        static readonly string[] Colors = { "#2563EB", "#16A34A", "#7C3AED", "#DB2777", "#0D9488", "#475569", "#B45309", "#DC2626" };
        static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
        static readonly TimeSpan Week = TimeSpan.FromDays(7);

        static Brush Solid(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public static string LabelFor(string name)
        {
            var match = Regex.Match(name, @"^([A-Za-z])[A-Za-z]*(\d.*)$");
            if (match.Success) return match.Groups[1].Value.ToUpperInvariant() + match.Groups[2].Value;
            return name.Substring(0, Math.Min(3, name.Length));
        }

        public static string DataDir(string name)
        {
            return name == null ? Paths.MainData : Path.Combine(Paths.Root, name);
        }

        public static List<string> Names()
        {
            if (!Directory.Exists(Paths.Root)) return new List<string>();
            return new DirectoryInfo(Paths.Root).GetDirectories()
                .OrderBy(d => d.CreationTimeUtc).ThenBy(d => d.Name).Select(d => d.Name).ToList();
        }

        // The app keeps `lockfile` open for as long as a window on that folder is running.
        static bool IsRunning(string dir)
        {
            var file = Path.Combine(dir, "lockfile");
            if (!File.Exists(file)) return false;
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        static Dictionary<string, object> ReadJson(string file)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
        }

        static double? Number(Dictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        static Folder ReadFolder(string name)
        {
            var folder = new Folder { Name = name, Dir = DataDir(name) };
            try
            {
                var file = Path.Combine(folder.Dir, "plan-usage-history.json");
                if (File.Exists(file))
                {
                    foreach (Dictionary<string, object> entry in (ArrayList)ReadJson(file)["samples"])
                    {
                        var usage = entry["u"] as Dictionary<string, object>;
                        folder.Samples.Add(new Sample
                        {
                            Time = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(entry["t"])).LocalDateTime,
                            Org = entry.ContainsKey("org") ? entry["org"] as string : null,
                            FiveHour = Number(usage, "fh"),
                            Week = Number(usage, "sd")
                        });
                    }
                }
            }
            catch { /* the app may be rewriting the file; use what was read */ }
            folder.Samples = folder.Samples.OrderBy(s => s.Time).ToList();
            if (folder.Samples.Count > 0) folder.Org = folder.Samples[folder.Samples.Count - 1].Org;

            // Sessions are stored under <account id>\<organisation id>; the folder for the
            // current organisation that was written last tells us the account.
            try
            {
                var sessions = new DirectoryInfo(Path.Combine(folder.Dir, "claude-code-sessions"));
                if (sessions.Exists && folder.Org != null)
                {
                    var current = sessions.GetDirectories()
                        .Select(account => new DirectoryInfo(Path.Combine(account.FullName, folder.Org)))
                        .Where(org => org.Exists).OrderByDescending(org => org.LastWriteTimeUtc).FirstOrDefault();
                    if (current != null) folder.Account = current.Parent.Name;
                }
            }
            catch { }
            return folder;
        }

        // Claude Code caches its latest usage answer, reset times included, for one account.
        static List<CachedLimit> ReadCachedLimits(out string account)
        {
            account = null;
            var limits = new List<CachedLimit>();
            try
            {
                var cache = ReadJson(Paths.CodeState)["cachedUsageUtilization"] as Dictionary<string, object>;
                account = cache["accountUuid"] as string;
                var utilization = cache["utilization"] as Dictionary<string, object>;
                foreach (Dictionary<string, object> entry in (ArrayList)utilization["limits"])
                {
                    var resets = entry["resets_at"] as string;
                    if (resets == null) continue;
                    var limit = new CachedLimit
                    {
                        Kind = entry["kind"] as string,
                        Percent = Number(entry, "percent") ?? 0,
                        ResetsAt = DateTimeOffset.Parse(resets, CultureInfo.InvariantCulture).LocalDateTime
                    };
                    var scope = entry.ContainsKey("scope") ? entry["scope"] as Dictionary<string, object> : null;
                    var model = scope != null && scope.ContainsKey("model") ? scope["model"] as Dictionary<string, object> : null;
                    if (model != null) limit.Model = model["display_name"] as string;
                    limits.Add(limit);
                }
            }
            catch { }
            return limits;
        }

        static string When(DateTime time)
        {
            return time.ToString(time.Date == DateTime.Today ? "HH:mm" : "ddd d MMM, HH:mm");
        }

        // A limit only falls when its window resets. Walking back from the newest sample
        // while the number never falls finds the oldest moment we know the current window
        // already existed; it cannot last longer than `length` past that. `before` is the
        // sample just ahead of a fall, which brackets the reset from the other side.
        static DateTime? WindowKnownSince(List<Sample> samples, Func<Sample, double?> pick, TimeSpan length, out DateTime? before)
        {
            before = null;
            var last = samples[samples.Count - 1];
            if (!pick(last).HasValue || pick(last).Value <= 0) return null;
            var since = last.Time;
            for (int i = samples.Count - 2; i >= 0; i--)
            {
                var value = pick(samples[i]);
                if (!value.HasValue || last.Time - samples[i].Time >= length) break;
                if (value.Value > pick(samples[i + 1]).Value + 4) { before = samples[i].Time; break; }
                if (value.Value > 0) since = samples[i].Time;
            }
            return since;
        }

        static Brush BarBrush(double percent)
        {
            return Solid(percent >= 90 ? "#F2555A" : percent >= 70 ? "#F0B429" : "#E7E7EA");
        }

        static void SetBar(double percent, out GridLength fill, out GridLength rest, out Brush brush)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            fill = new GridLength(percent, GridUnitType.Star);
            rest = new GridLength(100 - percent, GridUnitType.Star);
            brush = BarBrush(percent);
        }

        static AccountRow Row(Folder folder, List<Sample> samples, int colorIndex, List<CachedLimit> cached)
        {
            var running = IsRunning(folder.Dir);
            var row = new AccountRow
            {
                Name = folder.Name,
                Title = folder.Name ?? "Main app",
                Label = folder.Name == null ? "C" : LabelFor(folder.Name),
                TagBrush = Solid(folder.Name == null ? "#D97757" : Colors[colorIndex % Colors.Length]),
                Status = running ? "Open" : "Closed",
                StatusBrush = Solid(running ? "#4ADE80" : "#6B6B73"),
                UsageVisibility = Visibility.Collapsed,
                WeekVisibility = Visibility.Collapsed,
                Lines = new List<string>()
            };
            if (samples.Count == 0)
            {
                row.Lines.Add("Not signed in yet. Click to open and sign in.");
                return row;
            }

            var now = DateTime.Now;
            var last = samples[samples.Count - 1];
            row.UsageVisibility = Visibility.Visible;
            row.Lines.Add("Updated " + When(last.Time));

            GridLength fill, rest;
            Brush brush;

            // 5-hour limit. A number saved more than five hours ago describes a window that is over.
            var session = cached.FirstOrDefault(limit => limit.Kind == "session" && limit.ResetsAt > now);
            if (now - last.Time >= FiveHours)
            {
                SetBar(0, out fill, out rest, out brush);
                row.FiveText = "reset";
            }
            else
            {
                SetBar(last.FiveHour ?? 0, out fill, out rest, out brush);
                row.FiveText = last.FiveHour.HasValue ? (last.FiveHour.Value).ToString("0") + "%" : "n/a";
                DateTime? before;
                var since = WindowKnownSince(samples, s => s.FiveHour, FiveHours, out before);
                if (session != null) row.Lines.Add("5-hour limit resets at " + When(session.ResetsAt));
                else if (since.HasValue) row.Lines.Add("5-hour limit resets by " + When(since.Value + FiveHours) + " at the latest");
            }
            row.FiveFill = fill; row.FiveRest = rest; row.FiveBrush = brush;

            // Weekly limit.
            if (last.Week.HasValue)
            {
                row.WeekVisibility = Visibility.Visible;
                SetBar(last.Week.Value, out fill, out rest, out brush);
                row.WeekText = last.Week.Value.ToString("0") + "%";
                DateTime? before;
                var since = WindowKnownSince(samples, s => s.Week, Week, out before);
                var weekly = cached.FirstOrDefault(limit => limit.Kind != "session" && limit.Model == null && limit.ResetsAt > now);
                if (weekly != null) row.Lines.Add("Weekly limit resets " + When(weekly.ResetsAt));
                else if (since.HasValue)
                {
                    var latest = since.Value + Week;
                    if (latest <= now)
                    {
                        SetBar(0, out fill, out rest, out brush);
                        row.WeekText = "reset";
                    }
                    else if (before.HasValue && before.Value + Week > now && latest - (before.Value + Week) <= TimeSpan.FromHours(24))
                        row.Lines.Add("Weekly limit resets between " + When(before.Value + Week) + " and " + When(latest));
                    else row.Lines.Add("Weekly limit resets by " + When(latest) + " at the latest");
                }
                row.WeekFill = fill; row.WeekRest = rest; row.WeekBrush = brush;
            }

            // Per-model weekly limits, such as Fable, exist only in Claude Code's snapshot.
            foreach (var limit in cached.Where(limit => limit.Model != null && limit.ResetsAt > now))
                row.Lines.Add(limit.Model + " weekly limit: " + limit.Percent.ToString("0") + "% used, resets " + When(limit.ResetsAt));
            return row;
        }

        public static List<AccountRow> Load()
        {
            var names = Names();
            var main = ReadFolder(null);
            var folders = names.Select(ReadFolder).ToList();
            string cachedAccount;
            var cached = ReadCachedLimits(out cachedAccount);
            var none = new List<CachedLimit>();

            var rows = new List<AccountRow>();
            for (int i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                var samples = folder.Samples.Where(s => s.Org == folder.Org).ToList();
                // Numbers this account saved while it was signed in to the normal app count
                // too, unless several of your windows share the organisation (team seats).
                if (folder.Org != null && folders.Count(other => other.Org == folder.Org) == 1)
                    samples = samples.Concat(main.Samples.Where(s => s.Org == folder.Org)).OrderBy(s => s.Time).ToList();
                rows.Add(Row(folder, samples, i, folder.Account != null && folder.Account == cachedAccount ? cached : none));
            }
            rows = rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
            rows.Insert(0, Row(main, main.Samples.Where(s => s.Org == main.Org).ToList(), 0,
                main.Account != null && main.Account == cachedAccount ? cached : none));
            return rows;
        }

        public static void Open(string name)
        {
            var start = new ProcessStartInfo(Paths.App) { UseShellExecute = true };
            if (name != null) start.Arguments = "--user-data-dir=\"" + DataDir(name) + "\"";
            Process.Start(start);
        }
    }

    // Windows groups taskbar buttons by app id, and every Claude window reports the same
    // one. Writing our own id onto an account's window gives it a separate, pinnable
    // button with the account's icon.
    static class Taskbar
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey { public Guid FormatId; public uint PropertyId; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        delegate bool WindowVisitor(IntPtr window, IntPtr state);

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr state);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        // System.AppUserModel properties: 5 = app id, 2 = relaunch command, 3 = icon, 4 = display name.
        static readonly Guid AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        const ushort VT_LPWSTR = 31;
        const uint GW_OWNER = 4;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_CAPTION_COLOR = 35;

        static readonly Dictionary<uint, string> accountByProcess = new Dictionary<uint, string>();
        static readonly HashSet<IntPtr> tagged = new HashSet<IntPtr>();
        static readonly object gate = new object();

        static void Set(IPropertyStore store, uint id, string text)
        {
            var key = new PropertyKey { FormatId = AppUserModel, PropertyId = id };
            var value = new PropVariant { Type = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(text) };
            try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
        }

        static void Tag(IntPtr window, string name)
        {
            var iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
            Set(store, 5, "ClaudeAccounts." + name);
            Set(store, 3, Path.Combine(Accounts.DataDir(name), "ccm-icon.ico"));
            Set(store, 2, "\"" + Paths.Exe + "\" open " + name);
            Set(store, 4, "Claude " + name);
            store.Commit();
            Marshal.ReleaseComObject(store);
        }

        // Which account a process belongs to, read from its command line. "" = none.
        static string AccountOf(uint processId)
        {
            try
            {
                if (!string.Equals(Process.GetProcessById((int)processId).ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) return "";
                using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId))
                {
                    foreach (ManagementObject process in search.Get())
                    {
                        var match = Regex.Match(Convert.ToString(process["CommandLine"]),
                            "--user-data-dir=\"?" + Regex.Escape(Paths.Root) + @"\\([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase);
                        if (match.Success) return match.Groups[1].Value;
                    }
                }
            }
            catch { }
            return "";
        }

        // Tags every visible account window that is not tagged yet. Cheap enough to run
        // every few seconds: the command line is looked up once per process.
        public static void TagAccountWindows()
        {
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                var seen = new HashSet<uint>();
                EnumWindows(delegate(IntPtr window, IntPtr state)
                {
                    if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER) != IntPtr.Zero) return true;
                    uint processId;
                    GetWindowThreadProcessId(window, out processId);
                    seen.Add(processId);
                    string name;
                    if (!accountByProcess.TryGetValue(processId, out name)) accountByProcess[processId] = name = AccountOf(processId);
                    if (name.Length == 0 || tagged.Contains(window)) return true;
                    try { Tag(window, name); tagged.Add(window); } catch { }
                    return true;
                }, IntPtr.Zero);
                foreach (var gone in accountByProcess.Keys.Where(id => !seen.Contains(id)).ToList()) accountByProcess.Remove(gone);
                tagged.RemoveWhere(window => !IsWindow(window));
            }
            finally { Monitor.Exit(gate); }
        }

        // Dark title bar in the same colour as the window, so it reads as one surface.
        public static void MatchTitleBar(Window window, Color color)
        {
            var handle = new WindowInteropHelper(window).Handle;
            int on = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int colorRef = color.R | (color.G << 8) | (color.B << 16);
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }
    }

    class UsageWindow
    {
        const string Layout = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Claude accounts' Width='400' SizeToContent='Height' ResizeMode='CanMinimize'
        Background='#0B0B0C' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13'
        WindowStartupLocation='Manual' UseLayoutRounding='True' TextOptions.TextFormattingMode='Display'>
  <ScrollViewer VerticalScrollBarVisibility='Auto'>
  <StackPanel Margin='12,2,12,12'>
    <ItemsControl x:Name='List'>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Border CornerRadius='10' Padding='14,12,14,12' Margin='0,8,0,0' Cursor='Hand' BorderThickness='1'>
            <Border.Style>
              <Style TargetType='Border'>
                <Setter Property='Background' Value='#131315'/>
                <Setter Property='BorderBrush' Value='#1E1E21'/>
                <Style.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'>
                    <Setter Property='Background' Value='#18181B'/>
                    <Setter Property='BorderBrush' Value='#2A2A2F'/>
                  </Trigger>
                </Style.Triggers>
              </Style>
            </Border.Style>
            <StackPanel>
              <DockPanel LastChildFill='False'>
                <Border DockPanel.Dock='Left' Background='{Binding TagBrush}' CornerRadius='5' Padding='6,1,6,2' VerticalAlignment='Center'>
                  <TextBlock Text='{Binding Label}' Foreground='White' FontWeight='Bold' FontSize='10.5'/>
                </Border>
                <TextBlock DockPanel.Dock='Left' Text='{Binding Title}' Foreground='#F4F4F5' FontWeight='SemiBold' FontSize='14' Margin='9,0,0,0' VerticalAlignment='Center'/>
                <TextBlock DockPanel.Dock='Right' Text='{Binding Status}' Foreground='{Binding StatusBrush}' FontSize='12' VerticalAlignment='Center'/>
                <Ellipse DockPanel.Dock='Right' Width='6' Height='6' Fill='{Binding StatusBrush}' Margin='0,1,6,0' VerticalAlignment='Center'/>
              </DockPanel>
              <Grid Margin='0,12,0,0' Visibility='{Binding UsageVisibility}'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='58'/><ColumnDefinition Width='*'/><ColumnDefinition Width='44'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='5-hour' Foreground='#A1A1AA' FontSize='12'/>
                <Border Grid.Column='1' Height='4' CornerRadius='2' Background='#232326' VerticalAlignment='Center'>
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='{Binding FiveFill}'/><ColumnDefinition Width='{Binding FiveRest}'/>
                    </Grid.ColumnDefinitions>
                    <Border CornerRadius='2' Background='{Binding FiveBrush}'/>
                  </Grid>
                </Border>
                <TextBlock Grid.Column='2' Text='{Binding FiveText}' Foreground='#F4F4F5' FontSize='12' HorizontalAlignment='Right'/>
              </Grid>
              <Grid Margin='0,7,0,0' Visibility='{Binding WeekVisibility}'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='58'/><ColumnDefinition Width='*'/><ColumnDefinition Width='44'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='Weekly' Foreground='#A1A1AA' FontSize='12'/>
                <Border Grid.Column='1' Height='4' CornerRadius='2' Background='#232326' VerticalAlignment='Center'>
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='{Binding WeekFill}'/><ColumnDefinition Width='{Binding WeekRest}'/>
                    </Grid.ColumnDefinitions>
                    <Border CornerRadius='2' Background='{Binding WeekBrush}'/>
                  </Grid>
                </Border>
                <TextBlock Grid.Column='2' Text='{Binding WeekText}' Foreground='#F4F4F5' FontSize='12' HorizontalAlignment='Right'/>
              </Grid>
              <ItemsControl ItemsSource='{Binding Lines}' Margin='0,9,0,0'>
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <TextBlock Text='{Binding}' Foreground='#71717A' FontSize='11.5' Margin='0,1,0,0' TextWrapping='Wrap'/>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>
          </Border>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Foreground='#52525B' FontSize='11' Margin='2,10,2,0' TextWrapping='Wrap'
               Text='Numbers are what each Claude window last saved. Click a card to open that account.'/>
  </StackPanel>
  </ScrollViewer>
</Window>";

        public readonly Window Window;
        readonly ItemsControl list;
        bool quitting;

        public UsageWindow()
        {
            Window = (Window)XamlReader.Parse(Layout);
            list = (ItemsControl)Window.FindName("List");
            Window.Icon = Imaging.CreateBitmapSourceFromHIcon(
                Paths.Icon().Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            Window.SourceInitialized += delegate { Taskbar.MatchTitleBar(Window, ((SolidColorBrush)Window.Background).Color); };
            Window.SizeChanged += delegate { Dock(); };
            Window.Activated += delegate { Refresh(); };
            // Closing the window keeps the tray icon; Quit is in the tray menu.
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (quitting) return;
                e.Cancel = true;
                Window.Hide();
            };
            list.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                var element = e.OriginalSource as FrameworkElement;
                var row = element == null ? null : element.DataContext as AccountRow;
                if (row == null && element != null)
                {
                    // A click on one of the small text lines lands on a string; walk up to the card.
                    var parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
                    while (parent != null && row == null) { row = parent.DataContext as AccountRow; parent = VisualTreeHelper.GetParent(parent) as FrameworkElement; }
                }
                if (row != null) Accounts.Open(row.Name);
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += delegate { if (Window.IsVisible) Refresh(); };
            timer.Start();
        }

        // Bottom right, above the tray, like other tray apps; scrolls if the screen is short.
        void Dock()
        {
            var area = SystemParameters.WorkArea;
            Window.MaxHeight = area.Height - 24;
            Window.Left = area.Right - Window.ActualWidth - 12;
            Window.Top = area.Bottom - Window.ActualHeight - 12;
        }

        public void Refresh()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var rows = Accounts.Load();
                Window.Dispatcher.BeginInvoke(new Action(delegate { list.ItemsSource = rows; }));
            });
        }

        public void Show()
        {
            Refresh();
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        public void Quit()
        {
            quitting = true;
            Window.Close();
        }
    }

    static class Program
    {
        // `ClaudeAccounts.exe`              show the window (starts the tray icon if needed)
        // `ClaudeAccounts.exe tray`         start in the tray without showing the window
        // `ClaudeAccounts.exe open <name>`  open that account's Claude window
        [STAThread]
        static void Main(string[] args)
        {
            var command = args.Length > 0 ? args[0] : "";
            if (command == "open" && args.Length > 1)
            {
                Accounts.Open(args[1]);
                // The tray instance tags the new window so it gets its own taskbar button.
                Process.Start(new ProcessStartInfo(Paths.Exe, "tray") { UseShellExecute = false });
                return;
            }

            bool first;
            using (var mutex = new Mutex(true, "ClaudeAccounts.Tray", out first))
            using (var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudeAccounts.Show"))
            {
                if (!first)
                {
                    if (command != "tray") showRequest.Set();
                    return;
                }
                Run(command != "tray", showRequest);
            }
        }

        static void Run(bool showWindow, EventWaitHandle showRequest)
        {
            Taskbar.SetCurrentProcessExplicitAppUserModelID("ClaudeAccounts.Usage");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var usage = new UsageWindow();

            var tray = new Forms.NotifyIcon { Icon = Paths.Icon(), Text = "Claude accounts", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show usage", null, delegate { usage.Show(); });
            menu.Items.Add("Quit", null, delegate { tray.Visible = false; usage.Quit(); app.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button != Forms.MouseButtons.Left) return;
                if (usage.Window.IsVisible && usage.Window.IsActive) usage.Window.Hide(); else usage.Show();
            };

            // A second start of this program asks the running one to show its window.
            var listener = new Thread(delegate()
            {
                while (showRequest.WaitOne()) app.Dispatcher.BeginInvoke(new Action(usage.Show));
            }) { IsBackground = true };
            listener.Start();

            var tagger = new System.Threading.Timer(delegate { Taskbar.TagAccountWindows(); }, null, 0, 2000);

            if (showWindow) usage.Show();
            app.Run();
            tagger.Dispose();
            tray.Dispose();
        }
    }
}
