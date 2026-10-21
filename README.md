# claude-desktop-multi-account

Run several Claude accounts side by side in the Claude desktop app on Windows: one window per account, each with its own taskbar icon, and no signing out and back in.

On a Mac, see [macos/README.md](macos/README.md).

> Unofficial. Not affiliated with or endorsed by Anthropic.

## What it does

- **One window per account.** Each account gets its own data folder, so each window keeps its own sign-in. It is the regular Claude desktop app, unmodified.
- **Its own taskbar icon.** Each window gets a separate taskbar button with a coloured label (`T1`, `M5`, ...) that you can pin.
- **Usage window.** `Claude accounts` lists every account with its 5-hour and weekly limit as bars, which windows are open, when each number was last updated, and when the limits reset as far as that can be worked out. It also sits in the tray. Click an account to open its window.
- **One Claude Code history.** All windows share `~\.claude`, so the usage stats in the Code tab count every account.

## What it does not do

- It never reads, copies or stores a login, and never talks to Anthropic itself. Signing in happens inside each window through Anthropic's own sign-in.
- It does not show live usage. The numbers are the ones each Claude window last saved for itself, and a 5-hour number older than five hours is shown as reset.
- It cannot always show exact reset times, because the desktop app does not save them. It works out the latest moment a limit can reset from the saved numbers ("resets by ... at the latest"), and shows the exact time, including per-model weekly limits such as Fable, when Claude Code's own cached snapshot has it. The exact times are always in each window under **Settings > Usage**.
- It does not switch accounts for you.

## Requirements

- Windows 10 or 11
- The Claude desktop app for Windows (the current installer, which provides `claude-desktop.exe`)
- PowerShell 7: `winget install Microsoft.PowerShell`

The usage window is a small program (`ClaudeAccounts.exe`) that is compiled on your PC from `ClaudeAccounts.cs`, using the C# compiler that ships with Windows. No prebuilt binaries are downloaded.

## Install

```powershell
git clone https://github.com/Leonxlnx/claude-desktop-multi-account
cd claude-desktop-multi-account
.\ccm.cmd install
```

`install` builds `ClaudeAccounts.exe`, creates the Start menu shortcuts, and puts a small `ccm` launcher next to `claude-desktop.exe` so `ccm` works in any new terminal. Keep the cloned folder where it is; the shortcuts start from it.

## Use

1. Create one entry per extra account. Your normal Claude app stays as it is and can hold one more account.

   ```powershell
   ccm add work
   ccm add personal
   ```

2. Open `Claude work` from the Start menu.
3. Sign in with **Continue with email**, then type the verification code from the email link into that window. See the note on Google below.
4. Right-click its taskbar icon and choose **Pin to taskbar**. After a restart, click the pinned icons; every window is still signed in.
5. Open `Claude accounts` from the Start menu for the usage window. You can pin that too.

### Signing in with Google

Google sign-in does not work in the extra windows. It finishes in your browser and returns through a `claude://` link, and Windows always hands that link to the normal Claude app. Use **Continue with email** with the same address instead. When the browser shows a verification code, enter it in the window you are signing in.

## Commands

| Command | What it does |
| --- | --- |
| `ccm add <name>` | Create an account window and its Start menu shortcut |
| `ccm <name>` | Open that account's window, or bring it forward |
| `ccm usage` | Open the usage window |
| `ccm list` | The same numbers as text |
| `ccm rename <old> <new>` | Rename an account (quit its window first; the sign-in moves with it) |
| `ccm remove <name>` | Delete an account window and its saved sign-in (Claude Code history is kept) |
| `ccm shortcuts` | Recreate the shortcuts and icons, including pinned ones |
| `ccm build` | Rebuild `ClaudeAccounts.exe` |
| `ccm install` | Build, create shortcuts, and make `ccm` available in every terminal |

## How it works

- Each account window is the Claude app started with Chromium's `--user-data-dir` switch, pointing at `~\.claude-accounts\<name>`.
- Windows groups taskbar buttons by app id. While `ClaudeAccounts.exe` runs, it gives each account window, and its shortcut, its own id. That way each account gets a separate, pinnable button.
- Usage comes from the `plan-usage-history.json` file the app writes in each data folder. A window counts as open while the app holds that folder's `lockfile`.
- A limit only falls when its window resets, so the oldest saved number in an unbroken run marks a moment the current window already existed; it ends at most five hours (or seven days) later. Exact reset times come from the `cachedUsageUtilization` entry Claude Code keeps in `~\.claude.json`, when it belongs to that account.

## Known limitations

- **Links:** `claude://` links always open in the normal Claude app.
- **Claude in Chrome:** the browser extension connects to whichever window registered last.
- **Separate taskbar buttons:** these need `ClaudeAccounts.exe` running. It starts by itself when you open an account through its shortcut and stays in the tray.
- **Code tab chat list:** each new window starts with an empty list. The app's built-in import skips sessions that belong to another account's list.
- **Closing a window:** this hides it to the tray instead of quitting. Quit it from its tray icon to free memory.
- **App updates:** this relies on a standard Chromium switch, not a documented Claude feature, so an app update could stop it working. Your normal Claude app is not affected.
- **Platforms:** this page is about Windows. The macOS version is in [`macos`](macos/README.md).

## Terms

Check Anthropic's [Consumer Terms](https://www.anthropic.com/legal/consumer-terms) and [Usage Policy](https://www.anthropic.com/legal/aup) for your accounts. This tool is meant for people who hold separate accounts, such as work and personal. Do not share accounts or resell access. Anthropic notes that plan usage limits assume ordinary, individual use.

## Uninstall

1. Quit `Claude accounts` from its tray icon, and quit the account windows.
2. Run `ccm remove <name>` for each account.
3. Delete the `Claude accounts` folder in the Start menu, and unpin any pinned account icons.
4. Delete `ccm.ps1` and `ccm.cmd` from `%LOCALAPPDATA%\Microsoft\WindowsApps`.
5. Delete what is left of `%USERPROFILE%\.claude-accounts` and the cloned folder.

## License

MIT
