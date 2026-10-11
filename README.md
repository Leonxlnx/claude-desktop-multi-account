# claude-desktop-multi-account

Run several Claude accounts side by side in the Claude desktop app on Windows: one window per account, each with its own taskbar icon, and no signing out and back in.

> Unofficial. Not affiliated with or endorsed by Anthropic.

## What it does

- **One window per account.** Each account gets its own data folder, so each window keeps its own sign-in. It is the regular Claude desktop app, unmodified.
- **Its own taskbar icon.** Each window gets a separate taskbar button with a coloured label (`T1`, `M5`, ...) that you can pin.
- **Start menu shortcuts.** `Claude <name>` for every account, plus `Claude accounts usage` for the tray icon.
- **Usage tray icon (optional).** Lists every account with the 5-hour and weekly usage the app itself last saved. Click an account to open its window.
- **One Claude Code history.** All windows share `~\.claude`, so the usage stats in the Code tab count every account.

## What it does not do

- It never reads, copies or stores a login, and never talks to Anthropic itself. Signing in happens inside each window through Anthropic's own sign-in.
- It does not show live usage or reset times. The tray shows what each window last saved, with the time it was saved.
- It does not switch accounts for you.

## Requirements

- Windows 10 or 11
- The Claude desktop app for Windows (the current installer, which provides `claude-desktop.exe`)
- PowerShell 7: `winget install Microsoft.PowerShell`

## Install

```powershell
git clone https://github.com/Leonxlnx/claude-desktop-multi-account
cd claude-desktop-multi-account
.\ccm.cmd install
```

`install` puts a small `ccm` launcher next to `claude-desktop.exe`, so `ccm` works in any new terminal. Keep the cloned folder where it is; the shortcuts start from it.

## Use

1. Create one entry per extra account. Your normal Claude app stays as it is and can hold one more account.

   ```powershell
   ccm add work
   ccm add personal
   ```

2. Open `Claude work` from the Start menu (or run `ccm work`).
3. Sign in with **Continue with email**, then type the verification code from the email link into that window. See the note on Google below.
4. Right-click its taskbar icon and choose **Pin to taskbar**. After a restart, click the pinned icons; every window is still signed in.
5. Optional: open `Claude accounts usage` from the Start menu for the tray icon.

### Signing in with Google

Google sign-in does not work in the extra windows. It finishes in your browser and returns through a `claude://` link, and Windows always hands that link to the normal Claude app. Use **Continue with email** with the same address instead. When the browser shows a verification code, enter it in the window you are signing in.

## Commands

| Command | What it does |
| --- | --- |
| `ccm add <name>` | Create an account window and its Start menu shortcut |
| `ccm <name>` | Open that account's window, or bring it forward |
| `ccm list` | Show every account, which are open, and their last saved usage |
| `ccm tray` | The same list as a tray icon |
| `ccm rename <old> <new>` | Rename an account (quit its window first; the sign-in moves with it) |
| `ccm remove <name>` | Delete an account window and its saved sign-in (Claude Code history is kept) |
| `ccm shortcuts` | Recreate the shortcuts and icons |
| `ccm install` | Make `ccm` available in every terminal |

## How it works

- Each account window is the Claude app started with Chromium's `--user-data-dir` switch, pointing at `~\.claude-accounts\<name>`.
- Windows groups taskbar buttons by app id. `ccm` gives each account window and its shortcut its own id, so they get separate, pinnable buttons.
- Usage comes from the `plan-usage-history.json` file the app writes in each data folder.

## Known limitations

- **Links:** `claude://` links always open in the normal Claude app.
- **Claude in Chrome:** the browser extension connects to whichever window registered last.
- **Taskbar grouping:** a window reopened from its tray icon can fall back under the normal Claude taskbar button. Clicking its pinned icon fixes that.
- **Code tab chat list:** each new window starts with an empty list. The app's built-in import skips sessions that belong to another account's list.
- **App updates:** this relies on a standard Chromium switch, not a documented Claude feature, so an app update could stop it working. Your normal Claude app is not affected.
- **Platforms:** Windows only for now.

## Terms

Check Anthropic's [Consumer Terms](https://www.anthropic.com/legal/consumer-terms) and [Usage Policy](https://www.anthropic.com/legal/aup) for your accounts. This tool is meant for people who hold separate accounts, such as work and personal. Do not share accounts or resell access. Anthropic notes that plan usage limits assume ordinary, individual use.

## Uninstall

1. Quit the account windows.
2. Run `ccm remove <name>` for each account.
3. Delete the `Claude accounts` folder in the Start menu.
4. Delete `ccm.ps1` and `ccm.cmd` from `%LOCALAPPDATA%\Microsoft\WindowsApps`.

## License

MIT
