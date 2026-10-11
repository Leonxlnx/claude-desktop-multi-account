# ccm - keep several Claude accounts open side by side in the desktop app.
#
# Each account gets its own data folder under ~\.claude-accounts\<name> and a Start
# menu shortcut that opens the normal Claude desktop app on that folder. Every window
# still uses the same ~\.claude, so Code sessions from all accounts land in one
# history and the usage stats count all of them.
# ccm only starts the app. Signing in happens inside each window, and ccm never
# reads or copies a login.

$ErrorActionPreference = 'Stop'

$Root = if ($env:CCM_HOME) { $env:CCM_HOME } else { Join-Path $HOME '.claude-accounts' }
$Shortcuts = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude accounts'
$App = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\claude-desktop.exe'
$MainData = Join-Path $env:APPDATA 'Claude'
$Reserved = 'add', 'list', 'ls', 'remove', 'rm', 'rename', 'shortcuts', 'tray', 'install', 'help'
$Colors = '#2563EB', '#16A34A', '#7C3AED', '#DB2777', '#0D9488', '#475569', '#B45309', '#DC2626'

function Show-Help {
    @'
ccm - keep several Claude accounts open side by side in the desktop app

  ccm add <name>     create an account window and its Start menu shortcut
  ccm <name>         open that account's window (same as clicking "Claude <name>")
  ccm list           show accounts, which are open, and their last known plan usage
  ccm tray           the same list as a tray icon; click an account to open it
  ccm remove <name>  delete an account window and its saved sign-in (history is kept)
  ccm rename <a> <b> give an account a different name (its window must be closed)
  ccm shortcuts      recreate the shortcuts and icons, and re-tag open windows
  ccm install        make the ccm command available in every terminal
'@
}

function Get-AccountDir($name) {
    if (-not $name) { throw 'Missing account name. See: ccm help' }
    if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $Reserved -contains $name) {
        throw "'$name' is not a usable account name (letters, digits, . _ - only)."
    }
    return Join-Path $Root $name
}

function Get-ExistingAccountDir($name) {
    $data = Get-AccountDir $name
    if (-not (Test-Path -LiteralPath $data)) { throw "No account '$name'. Create it with: ccm add $name" }
    return $data
}

function Get-AppProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='claude.exe'" |
        Where-Object { $_.ExecutablePath -like '*\WindowsApps\*' })
}

function Get-AccountProcesses($data, $processes) {
    $pattern = [regex]::Escape($data) + '(["\s]|$)'
    @($processes | Where-Object { $_.CommandLine -match $pattern })
}

function Test-Open($data, $processes) {
    return [bool](Get-AccountProcesses $data $processes)
}

# Windows groups taskbar buttons by app id, and every Claude window reports the same
# one. Writing our own id onto a window (and onto its shortcut) gives that account a
# separate taskbar button with its own icon, which can then be pinned.
function Import-ShellHelper {
    if ('CcmShell' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class CcmShell
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

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    // System.AppUserModel properties: 5 = app id, 2 = relaunch command, 3 = icon, 4 = display name.
    static readonly Guid AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const ushort VT_LPWSTR = 31;
    const int GPS_READWRITE = 2;

    static void Set(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { FormatId = AppUserModel, PropertyId = id };
        var value = new PropVariant { Type = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    public static void TagWindow(IntPtr window, string appId, string command, string icon, string name)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
        Set(store, 5, appId);
        Set(store, 3, icon);
        Set(store, 2, command);
        Set(store, 4, name);
        store.Commit();
        Marshal.ReleaseComObject(store);
    }

    public static void TagShortcut(string path, string appId)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_READWRITE, ref iid, out store));
        Set(store, 5, appId);
        Marshal.ThrowExceptionForHR(store.Commit());
        Marshal.ReleaseComObject(store);
    }
}
'@
}

# The command a shortcut or pinned taskbar icon runs: this script, without a console window.
function Get-LaunchCommand($name) {
    $console = Join-Path $env:SystemRoot 'System32\conhost.exe'
    $shell = (Get-Process -Id $PID).Path
    $arguments = "--headless `"$shell`" -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'ccm.ps1')`" $name"
    return $console, $arguments
}

# Tag the account's window once it exists. The app can swap its first window for
# another while starting, so keep checking until the handle has stopped changing.
function Set-TaskbarIdentity($name, $data, $seconds = 20) {
    Import-ShellHelper
    $console, $arguments = Get-LaunchCommand $name
    $tagged = [IntPtr]::Zero
    $stable = 0
    $main = $null
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline -and $stable -lt 4) {
        if (-not $main) {
            $main = Get-AccountProcesses $data (Get-AppProcesses) | Where-Object { $_.CommandLine -notmatch '--type=' } | Select-Object -First 1
        }
        $window = if ($main) { (Get-Process -Id $main.ProcessId -ErrorAction SilentlyContinue).MainWindowHandle } else { [IntPtr]::Zero }
        if ($window -and $window -ne [IntPtr]::Zero -and $window -ne $tagged) {
            [CcmShell]::TagWindow($window, "ClaudeAccounts.$name", "`"$console`" $arguments", (Join-Path $data 'ccm-icon.ico'), "Claude $name")
            $tagged = $window
            $stable = 0
        }
        elseif ($tagged -ne [IntPtr]::Zero) { $stable++ }
        Start-Sleep -Milliseconds 500
    }
}

# team1 -> T1, max20a -> M20a: short enough to read on a taskbar icon.
function Get-Label($name) {
    if ($name -match '^([A-Za-z])[A-Za-z]*(\d.*)$') { return $Matches[1].ToUpper() + $Matches[2] }
    return $name.Substring(0, [Math]::Min(3, $name.Length))
}

# Draw the Claude icon with a coloured name tag so pinned shortcuts can be told apart.
# The file is kept in the account folder, which survives app updates.
function Get-AppExe {
    if (-not $script:AppExe) {
        $script:AppExe = Join-Path (Get-AppxPackage -Name Claude -WarningAction SilentlyContinue).InstallLocation 'app\Claude.exe'
    }
    return $script:AppExe
}

function New-AccountIcon($name, $data) {
    try {
        Add-Type -AssemblyName System.Drawing
        $order = @(Get-ChildItem -LiteralPath $Root -Directory | Sort-Object CreationTime, Name | ForEach-Object FullName)
        $color = [Drawing.ColorTranslator]::FromHtml($Colors[[Array]::IndexOf($order, $data) % $Colors.Count])

        $bitmap = [Drawing.Icon]::ExtractIcon((Get-AppExe), 0, 256).ToBitmap()
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.TextRenderingHint = 'AntiAlias'

        $tag = [Drawing.Rectangle]::new(8, 118, 240, 130)
        $corner = 88
        $shape = [Drawing.Drawing2D.GraphicsPath]::new()
        $shape.AddArc($tag.X, $tag.Y, $corner, $corner, 180, 90)
        $shape.AddArc($tag.Right - $corner, $tag.Y, $corner, $corner, 270, 90)
        $shape.AddArc($tag.Right - $corner, $tag.Bottom - $corner, $corner, $corner, 0, 90)
        $shape.AddArc($tag.X, $tag.Bottom - $corner, $corner, $corner, 90, 90)
        $shape.CloseFigure()
        $graphics.FillPath([Drawing.SolidBrush]::new($color), $shape)

        $label = Get-Label $name
        $size = 104
        do {
            $size -= 4
            $font = [Drawing.Font]::new('Segoe UI', $size, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
        } while ($graphics.MeasureString($label, $font).Width -gt $tag.Width -and $size -gt 24)
        $format = [Drawing.StringFormat]::new()
        $format.Alignment = 'Center'
        $format.LineAlignment = 'Center'
        $graphics.DrawString($label, $font, [Drawing.Brushes]::White, [Drawing.RectangleF]$tag, $format)
        $graphics.Dispose()

        # A .ico file holding one 256px PNG: 6-byte header, 16-byte entry, then the image.
        $png = [IO.MemoryStream]::new()
        $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
        $header = [byte[]](0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 32, 0) +
            [BitConverter]::GetBytes([int]$png.Length) + [BitConverter]::GetBytes(22)
        $icon = Join-Path $data 'ccm-icon.ico'
        [IO.File]::WriteAllBytes($icon, $header + $png.ToArray())
        return $icon
    }
    catch { return $null }
}

function Set-Shortcut($name, $data) {
    New-Item -ItemType Directory -Force $Shortcuts | Out-Null
    $path = Join-Path $Shortcuts "Claude $name.lnk"
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $console, $arguments = Get-LaunchCommand $name
    $link.TargetPath = $console
    $link.Arguments = $arguments
    $link.Description = "Claude desktop app, account '$name'"
    $icon = New-AccountIcon $name $data
    if ($icon) { $link.IconLocation = "$icon,0" }
    $link.Save()
    # Same app id as the window, so a pinned shortcut and the running window share one button.
    try { Import-ShellHelper; [CcmShell]::TagShortcut($path, "ClaudeAccounts.$name") } catch { }
}

# Starts the tray icon, so usage can be checked without a terminal.
function Set-UsageShortcut {
    New-Item -ItemType Directory -Force $Shortcuts | Out-Null
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $Shortcuts 'Claude accounts usage.lnk'))
    $console, $arguments = Get-LaunchCommand 'tray'
    $link.TargetPath = $console
    $link.Arguments = $arguments
    $link.Description = 'Tray icon with every Claude account and its last saved plan usage'
    $link.Save()
}

function Update-Shortcuts {
    $processes = Get-AppProcesses
    Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Set-Shortcut $_.Name $_.FullName
        # Windows sitting in the tray have no window to tag; they get tagged when next opened.
        if (Test-Open $_.FullName $processes) { Set-TaskbarIdentity $_.Name $_.FullName 3 }
    }
    Set-UsageShortcut
    "Shortcuts are in the Start menu under 'Claude accounts'."
}

function New-Account($name) {
    $data = Get-AccountDir $name
    if (Test-Path -LiteralPath $data) { throw "Account '$name' already exists." }
    if (-not (Test-Path -LiteralPath $App)) { throw 'The Claude desktop app is not installed (claude-desktop.exe not found).' }
    New-Item -ItemType Directory -Force $data | Out-Null
    Set-Shortcut $name $data
    Set-UsageShortcut
    "Created '$name'. Open it from the Start menu (`"Claude $name`") or with: ccm $name"
}

function Open-Account($name) {
    $data = Get-ExistingAccountDir $name
    Start-Process $App -ArgumentList "--user-data-dir=`"$data`""
    "Opening Claude for '$name'."
    Set-TaskbarIdentity $name $data
}

# The desktop app records its own plan usage samples per window; show the latest one.
function Get-Row($label, $data, $processes) {
    $row = [ordered]@{
        Account  = $label
        Status   = if (Test-Open $data $processes) { 'open' } else { 'closed' }
        '5h %'   = $null
        'Week %' = $null
        Checked  = 'never signed in'
    }
    try {
        $samples = (Get-Content -LiteralPath (Join-Path $data 'plan-usage-history.json') -Raw | ConvertFrom-Json).samples
        $last = $samples | Sort-Object t | Select-Object -Last 1
        $row['5h %'] = $last.u.fh
        $row['Week %'] = $last.u.sd
        $row.Checked = [DateTimeOffset]::FromUnixTimeMilliseconds($last.t).LocalDateTime.ToString('g')
    }
    catch { }
    [pscustomobject]$row
}

function Get-Rows {
    $processes = Get-AppProcesses
    Get-Row '(main app)' $MainData $processes
    Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Row $_.Name $_.FullName $processes }
}

function Show-Accounts {
    Get-Rows | Format-Table -AutoSize
}

function Update-TrayMenu($menu, $tray) {
    $menu.Items.Clear()
    foreach ($row in Get-Rows) {
        # Some plans report no weekly figure.
        $fiveHour, $week = $row.'5h %', $row.'Week %' | ForEach-Object { if ($null -ne $_) { "$_%" } else { 'n/a' } }
        $usage = if ($row.Checked -eq 'never signed in') { 'not signed in yet' }
            else { "5h $fiveHour  |  week $week  |  as of $($row.Checked)" }
        $item = $menu.Items.Add("$($row.Account)    $usage")
        if ($row.Status -eq 'open') { $item.Font = [Drawing.Font]::new($item.Font, [Drawing.FontStyle]::Bold) }
        $target, $arguments = if ($row.Account -eq '(main app)') { $App, '' } else { Get-LaunchCommand $row.Account }
        $item.add_Click({ if ($arguments) { Start-Process $target -ArgumentList $arguments } else { Start-Process $target } }.GetNewClosure())
    }
    [void]$menu.Items.Add('-')
    $note = $menu.Items.Add('Numbers are what each window last saved. Bold = open.')
    $note.Enabled = $false
    [void]$menu.Items.Add('Quit tray icon', $null, { $tray.Visible = $false; [Windows.Forms.Application]::Exit() }.GetNewClosure())
}

# A tray icon whose menu lists every account with its last saved usage. Clicking an
# account opens (or brings forward) its window. The rows are rebuilt each time the
# menu opens, so they are as fresh as the files the app has written.
function Start-Tray {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $isFirst = $false
    $mutex = [Threading.Mutex]::new($true, 'ccm-tray', [ref]$isFirst)
    if (-not $isFirst) { return 'The tray icon is already running.' }

    $tray = [Windows.Forms.NotifyIcon]::new()
    $tray.Text = 'Claude accounts'
    try { $tray.Icon = [Drawing.Icon]::ExtractIcon((Get-AppExe), 0, 32) } catch { $tray.Icon = [Drawing.SystemIcons]::Application }
    $menu = [Windows.Forms.ContextMenuStrip]::new()
    $tray.ContextMenuStrip = $menu

    $menu.add_Opening({
        Update-TrayMenu $menu $tray
        # WinForms cancels opening a menu that was empty when the click came in.
        $_.Cancel = $false
    })
    # Left click opens the same menu as right click.
    $tray.add_MouseUp({
        if ($_.Button -eq [Windows.Forms.MouseButtons]::Left) {
            [Windows.Forms.NotifyIcon].GetMethod('ShowContextMenu', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($tray, $null)
        }
    })

    $tray.Visible = $true
    [Windows.Forms.Application]::Run()
    $tray.Dispose()
    $mutex.ReleaseMutex()
}

function Remove-Account($name, $skipPrompt) {
    $data = Get-ExistingAccountDir $name
    if (Test-Open $data (Get-AppProcesses)) {
        throw "The Claude window for '$name' is still running. Quit it first (tray icon > Quit)."
    }
    if (-not $skipPrompt) {
        $answer = Read-Host "Delete the '$name' window, its saved sign-in and local app data? Code history is kept. Type the name to confirm"
        if ($answer -ne $name) { return 'Cancelled.' }
    }
    Remove-Item -LiteralPath (Join-Path $Shortcuts "Claude $name.lnk") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $data -Recurse -Force
    "Removed '$name'."
}

# The sign-in lives inside the folder, so it moves with it.
function Rename-Account($old, $new) {
    $from = Get-ExistingAccountDir $old
    $to = Get-AccountDir $new
    if (Test-Path -LiteralPath $to) { throw "Account '$new' already exists." }
    if (Test-Open $from (Get-AppProcesses)) {
        throw "The Claude window for '$old' is still running. Quit it first (tray icon > Quit)."
    }
    Remove-Item -LiteralPath (Join-Path $Shortcuts "Claude $old.lnk") -Force -ErrorAction SilentlyContinue
    Rename-Item -LiteralPath $from -NewName $new
    Set-Shortcut $new $to
    "Renamed '$old' to '$new'."
}

# Drop small launchers next to claude-desktop.exe, a folder Windows already has on PATH.
function Install-Ccm {
    $bin = Split-Path $App
    $script = Join-Path $PSScriptRoot 'ccm.ps1'
    Set-Content -LiteralPath (Join-Path $bin 'ccm.ps1') -Value "& '$script' @args`r`nexit `$LASTEXITCODE"
    Set-Content -LiteralPath (Join-Path $bin 'ccm.cmd') -Value "@`"$(Join-Path $PSScriptRoot 'ccm.cmd')`" %*"
    "Installed. 'ccm' now works in any terminal (launchers are in $bin)."
}

$command = if ($args.Count) { [string]$args[0] } else { 'help' }
$rest = @($args | Select-Object -Skip 1)

try {
    switch ($command) {
        { $_ -in 'help', '-h', '--help' } { Show-Help }
        'add' { New-Account $rest[0] }
        { $_ -in 'list', 'ls' } { Show-Accounts }
        { $_ -in 'remove', 'rm' } { Remove-Account $rest[0] ($rest -contains '--yes') }
        'rename' { Rename-Account $rest[0] $rest[1] }
        'shortcuts' { Update-Shortcuts }
        'tray' { Start-Tray }
        'install' { Install-Ccm }
        default { Open-Account $command }
    }
    exit 0
}
catch {
    Write-Host "ccm: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
