// Claude accounts: a small window and tray icon that lists every account window with
// the plan usage it last saved, opens account windows, and gives each one its own
// taskbar button. Built with the C# compiler that ships with Windows (see `ccm build`).
//
// It reads only the usage file each Claude window writes for itself. It never reads a
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
        public static readonly string Root = Environment.GetEnvironmentVariable("CCM_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-accounts");
        public static readonly string MainData =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        public static readonly string App =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\claude-desktop.exe");
        public static readonly string Exe = Process.GetCurrentProcess().MainModule.FileName;

        public static System.Drawing.Icon Icon()
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(Exe); }
            catch { return System.Drawing.SystemIcons.Application; }
        }
    }

    class Sample
    {
        public DateTime Time;
        public string Org;
        public double? FiveHour;
        public double? Week;
    }

    // One card in the window. Everything the template binds to is a public property.
    public class AccountRow
    {
        public string Name { get; set; }          // folder name, or null for the normal Claude app
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
        public string Note { get; set; }
    }

    static class Accounts
    {
        // Same order and colours as the icons `ccm` draws.
        static readonly string[] Colors = { "#2563EB", "#16A34A", "#7C3AED", "#DB2777", "#0D9488", "#475569", "#B45309", "#DC2626" };

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

        static double? Number(Dictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        static List<Sample> ReadSamples(string dir)
        {
            var samples = new List<Sample>();
            try
            {
                var file = Path.Combine(dir, "plan-usage-history.json");
                if (!File.Exists(file)) return samples;
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                foreach (Dictionary<string, object> entry in (ArrayList)root["samples"])
                {
                    var usage = entry["u"] as Dictionary<string, object>;
                    samples.Add(new Sample
                    {
                        Time = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(entry["t"])).LocalDateTime,
                        Org = entry.ContainsKey("org") ? entry["org"] as string : null,
                        FiveHour = Number(usage, "fh"),
                        Week = Number(usage, "sd")
                    });
                }
            }
            catch { /* the app may be rewriting the file; show what was read */ }
            return samples.OrderBy(s => s.Time).ToList();
        }

        // The app does not save reset times. The weekly figure only goes down when the
        // week resets, so the last drop between two saved samples brackets that moment,
        // and the next reset is seven days after it.
        static string EstimateWeeklyReset(List<Sample> samples)
        {
            if (samples.Count == 0) return null;
            var org = samples[samples.Count - 1].Org;
            var mine = samples.Where(s => s.Org == org && s.Week.HasValue).ToList();
            for (int i = mine.Count - 1; i > 0; i--)
            {
                if (mine[i].Week.Value > mine[i - 1].Week.Value - 5) continue;
                var earliest = mine[i - 1].Time.AddDays(7);
                var latest = mine[i].Time.AddDays(7);
                while (latest < DateTime.Now) { earliest = earliest.AddDays(7); latest = latest.AddDays(7); }
                // Samples days apart give a range too wide to be useful.
                if (latest - earliest > TimeSpan.FromHours(24)) return null;
                if (latest - earliest <= TimeSpan.FromHours(3))
                    return "Weekly reset around " + earliest.AddTicks((latest - earliest).Ticks / 2).ToString("ddd d MMM, HH:mm") + " (estimate)";
                if (earliest <= DateTime.Now)
                    return "Weekly reset by " + latest.ToString("ddd d MMM, HH:mm") + " (estimate)";
                return "Weekly reset between " + earliest.ToString("ddd HH:mm") + " and " + latest.ToString("ddd d MMM, HH:mm") + " (estimate)";
            }
            return null;
        }

        static Brush BarBrush(double percent)
        {
            return Solid(percent >= 90 ? "#E5484D" : percent >= 70 ? "#E2A336" : "#D97757");
        }

        static AccountRow Row(string name, int index)
        {
            var dir = DataDir(name);
            var running = IsRunning(dir);
            var samples = ReadSamples(dir);
            var last = samples.Count > 0 ? samples[samples.Count - 1] : null;
            var row = new AccountRow
            {
                Name = name,
                Title = name ?? "Claude (normal app)",
                Label = name == null ? "C" : LabelFor(name),
                TagBrush = Solid(name == null ? "#D97757" : Colors[index % Colors.Length]),
                Status = running ? "Open" : "Closed",
                StatusBrush = Solid(running ? "#6BCB8B" : "#8A8880"),
                UsageVisibility = last == null ? Visibility.Collapsed : Visibility.Visible,
                WeekVisibility = last != null && last.Week.HasValue ? Visibility.Visible : Visibility.Collapsed
            };
            if (last == null)
            {
                row.Note = "Not signed in yet. Click to open and sign in.";
                return row;
            }

            var five = Math.Max(0, Math.Min(100, last.FiveHour ?? 0));
            row.FiveText = last.FiveHour.HasValue ? five.ToString("0") + "%" : "n/a";
            row.FiveFill = new GridLength(five, GridUnitType.Star);
            row.FiveRest = new GridLength(100 - five, GridUnitType.Star);
            row.FiveBrush = BarBrush(five);

            var week = Math.Max(0, Math.Min(100, last.Week ?? 0));
            row.WeekText = week.ToString("0") + "%";
            row.WeekFill = new GridLength(week, GridUnitType.Star);
            row.WeekRest = new GridLength(100 - week, GridUnitType.Star);
            row.WeekBrush = BarBrush(week);

            var note = "Updated " + last.Time.ToString(last.Time.Date == DateTime.Today ? "HH:mm" : "d MMM, HH:mm");
            var reset = last.Week.HasValue ? EstimateWeeklyReset(samples) : "No weekly limit reported for this plan";
            row.Note = reset == null ? note : note + "  ·  " + reset;
            return row;
        }

        // Colours follow creation order (like the icons); the list is alphabetical.
        public static List<AccountRow> Load()
        {
            var names = Names();
            var rows = names.Select((name, index) => Row(name, index)).OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
            rows.Insert(0, Row(null, 0));
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

        public static void UseDarkTitleBar(Window window)
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
    }

    class UsageWindow
    {
        const string Layout = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Claude accounts' Width='420' SizeToContent='Height' ResizeMode='CanMinimize'
        Background='#1F1E1D' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13'
        WindowStartupLocation='Manual' UseLayoutRounding='True' TextOptions.TextFormattingMode='Display'>
  <ScrollViewer VerticalScrollBarVisibility='Auto'>
  <StackPanel Margin='14,8,14,14'>
    <ItemsControl x:Name='List'>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Border CornerRadius='10' Padding='14,10,14,10' Margin='0,6,0,0' Cursor='Hand'>
            <Border.Style>
              <Style TargetType='Border'>
                <Setter Property='Background' Value='#2B2A28'/>
                <Style.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'>
                    <Setter Property='Background' Value='#353431'/>
                  </Trigger>
                </Style.Triggers>
              </Style>
            </Border.Style>
            <StackPanel>
              <DockPanel LastChildFill='False'>
                <Border DockPanel.Dock='Left' Background='{Binding TagBrush}' CornerRadius='5' Padding='7,1,7,2' VerticalAlignment='Center'>
                  <TextBlock Text='{Binding Label}' Foreground='White' FontWeight='Bold' FontSize='11'/>
                </Border>
                <TextBlock DockPanel.Dock='Left' Text='{Binding Title}' Foreground='#F3F2EE' FontWeight='SemiBold' FontSize='14' Margin='9,0,0,0' VerticalAlignment='Center'/>
                <TextBlock DockPanel.Dock='Right' Text='{Binding Status}' Foreground='{Binding StatusBrush}' FontSize='12' VerticalAlignment='Center'/>
              </DockPanel>
              <Grid Margin='0,9,0,0' Visibility='{Binding UsageVisibility}'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='84'/><ColumnDefinition Width='*'/><ColumnDefinition Width='46'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='5-hour limit' Foreground='#B5B3AC' FontSize='12'/>
                <Border Grid.Column='1' Height='6' CornerRadius='3' Background='#3D3C39' VerticalAlignment='Center'>
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='{Binding FiveFill}'/><ColumnDefinition Width='{Binding FiveRest}'/>
                    </Grid.ColumnDefinitions>
                    <Border CornerRadius='3' Background='{Binding FiveBrush}'/>
                  </Grid>
                </Border>
                <TextBlock Grid.Column='2' Text='{Binding FiveText}' Foreground='#F3F2EE' FontSize='12' HorizontalAlignment='Right'/>
              </Grid>
              <Grid Margin='0,5,0,0' Visibility='{Binding WeekVisibility}'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='84'/><ColumnDefinition Width='*'/><ColumnDefinition Width='46'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='Weekly limit' Foreground='#B5B3AC' FontSize='12'/>
                <Border Grid.Column='1' Height='6' CornerRadius='3' Background='#3D3C39' VerticalAlignment='Center'>
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='{Binding WeekFill}'/><ColumnDefinition Width='{Binding WeekRest}'/>
                    </Grid.ColumnDefinitions>
                    <Border CornerRadius='3' Background='{Binding WeekBrush}'/>
                  </Grid>
                </Border>
                <TextBlock Grid.Column='2' Text='{Binding WeekText}' Foreground='#F3F2EE' FontSize='12' HorizontalAlignment='Right'/>
              </Grid>
              <TextBlock Text='{Binding Note}' Foreground='#8A8880' FontSize='11.5' Margin='0,7,0,0' TextWrapping='Wrap'/>
            </StackPanel>
          </Border>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Foreground='#8A8880' FontSize='11.5' Margin='2,12,2,0' TextWrapping='Wrap'
               Text='Click an account to open its window. The numbers are the ones each Claude window last saved; exact reset times are in that window under Settings &gt; Usage.'/>
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
            Window.SourceInitialized += delegate { Taskbar.UseDarkTitleBar(Window); };
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

            var tray = new Forms.NotifyIcon
            {
                Icon = Paths.Icon(),
                Text = "Claude accounts",
                Visible = true
            };
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
