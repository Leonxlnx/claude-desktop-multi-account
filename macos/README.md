# claude-desktop-multi-account for macOS

Run several Claude accounts side by side in the Claude desktop app on macOS: one window per account, each with its own labelled Dock icon, and no signing out and back in.

This is the macOS version. It works like the [Windows version](../README.md) and reads the same files; only the launching, the icons and the usage window are built for macOS.

> Unofficial. Not affiliated with or endorsed by Anthropic.

## What it does

- **One window per account.** Each account gets its own data folder, so each window keeps its own sign-in. It is the regular Claude desktop app; its code is not modified.
- **Its own Dock icon.** Each account window shows the Claude icon with a coloured label (`W1`, `P2`, `M5`, ...) in the Dock and the app switcher.
- **A launcher per account.** `Claude work.app`, `Claude personal.app`, ... in `~/Applications/Claude accounts`, with the same label. Find them in Spotlight or keep them in the Dock.
- **Usage in the menu bar.** `Claude accounts` lists every account with its 5-hour and weekly limit as bars, which windows are open, when each number was last updated, and when the limits reset as far as that can be worked out. Click an account to open its window.
- **One Claude Code history.** All windows share `~/.claude`, so the usage stats in the Code tab count every account.

## What it does not do

- It never reads, copies or stores a login, and never talks to Anthropic itself. Signing in happens inside each window through Anthropic's own sign-in.
- It does not show live usage. The numbers are the ones each Claude window last saved for itself, and a 5-hour number older than five hours is shown as reset.
- It cannot always show exact reset times, because the desktop app does not save them. It works out the latest moment a limit can reset from the saved numbers ("resets by ... at the latest"), and shows the exact time, including per-model weekly limits such as Fable, when Claude Code's own cached snapshot has it. The exact times are always in each window under **Settings > Usage**.
- It does not switch accounts for you.

## Requirements

- macOS 13 or later
- The Claude desktop app for macOS, in `/Applications` or `~/Applications`
- The Xcode Command Line Tools, for the Swift compiler: `xcode-select --install` (a full Xcode works too)

The menu bar app (`Claude accounts.app`) is compiled on your Mac from `ClaudeAccounts.swift`. No prebuilt binaries are downloaded.

## Install

```bash
git clone https://github.com/Leonxlnx/claude-desktop-multi-account
cd claude-desktop-multi-account/macos
./ccm install
```

`install` builds `Claude accounts.app` into `~/Applications/Claude accounts`, creates the launchers, and links `ccm` into a folder that is already on your `PATH` (`~/.local/bin`, `~/bin`, `/opt/homebrew/bin` or `/usr/local/bin`, whichever is writable). Keep the cloned folder where it is; the `ccm` link points into it. The launchers and the menu bar app do not need it.

## Use

1. Create one entry per extra account. Your normal Claude app stays as it is and can hold one more account.

   ```bash
   ccm add work
   ```

   ```bash
   ccm add personal
   ```

2. Open `Claude work` from Spotlight, or run `ccm work`.
3. Sign in with **Continue with email**, then type the verification code from the email into that window. See the note on Google below.
4. To keep it in the Dock, drag `Claude work.app` from `~/Applications/Claude accounts` into the Dock. Click it after a restart; every window is still signed in. Do not use **Options > Keep in Dock** on the running window instead; see the limitations below.
5. Open `Claude accounts` from Spotlight, or run `ccm usage`, for the usage window. It stays in the menu bar. To have it there after every login, add it under **System Settings > General > Login Items**; started that way it waits in the menu bar instead of opening.

### Signing in with Google

Google sign-in does not work reliably in the extra windows. It finishes in your browser and returns through a `claude://` link, and macOS hands that link to one Claude window of its choosing, usually the one that started first. Use **Continue with email** with the same address instead. When the browser shows a verification code, enter it in the window you are signing in.

## Commands

| Command | What it does |
| --- | --- |
| `ccm add <name>` | Create an account window and its launcher app |
| `ccm <name>` | Open that account's window, or bring it forward |
| `ccm usage` | Open the usage window in the menu bar |
| `ccm list` | The same numbers as text |
| `ccm rename <old> <new>` | Rename an account (quit its window first; the sign-in moves with it) |
| `ccm remove <name>` | Delete an account window and its saved sign-in (Claude Code history is kept) |
| `ccm apps` | Recreate the launcher apps and their icons |
| `ccm build` | Rebuild `Claude accounts.app` |
| `ccm install` | Build, create the launchers, and make `ccm` available in every terminal |

## How it works

- Each account window is the Claude app started as a separate instance (like `open -n`) with Chromium's `--user-data-dir` switch, pointing at `~/.claude-accounts/<name>`.
- The Dock takes a running app's icon from the app bundle it was started from. So each account runs from its own copy of `Claude.app` in `~/.claude-accounts/.apps`. The copy is an APFS clone: it shares its data with the original and takes no extra disk space. The only addition is a custom icon, set the way Finder's **Get Info** does it, so Claude's code signature stays valid. When Claude is updated, the copy is made again the next time that account's window opens.
- A label is the name's first letter plus the digits in it (`team1` → `T1`, `max20a` → `M20a`). A name without digits gets its first letter plus the account's place in the list (`work` → `W1`).
- A launcher is a tiny app bundle with a shell script inside. It asks `Claude accounts.app` to open the account, which also handles bringing an open window forward.
- Claude does not stop two copies from opening the same data folder, which could damage that account's data. So an account whose window is already running is brought forward, the same way a Dock click does, instead of being started again.
- A window counts as open while a Claude process is running with that folder as its `--user-data-dir`.
- Usage comes from the `plan-usage-history.json` file the app writes in each data folder.
- A limit only falls when its window resets, so the oldest saved number in an unbroken run marks a moment the current window already existed; it ends at most five hours (or seven days) later. Exact reset times come from the `cachedUsageUtilization` entry Claude Code keeps in `~/.claude.json`, when it belongs to that account.

## Known limitations

- **Keep in Dock:** pin the launcher, not the running window. **Options > Keep in Dock** on a running account window pins its copy of Claude, and clicking that later starts it without its data folder, as your normal Claude account. With the launcher pinned, an open account shows two icons with its label: the launcher and the running window.
- **Dock icons on other disks:** the labelled Dock icon needs `~/.claude-accounts` and `Claude.app` on the same APFS volume. Otherwise the window runs from the installed app and shows the plain Claude icon; the launcher still has the label.
- **Renumbering:** removing an account renumbers the ones after it (`Z3` becomes `Z2`). An open window shows its new icon after it is quit and opened again.
- **The normal Claude icon in the Dock:** if an account window is running and the normal app is not, clicking Claude in the Dock may bring the account window forward instead of starting the normal app. Open it from the **Main app** card in the menu bar instead.
- **Links:** `claude://` links open in whichever Claude window macOS picks.
- **Claude in Chrome:** the browser extension is likely to connect to whichever window registered last, as on Windows.
- **Code tab chat list:** each new window starts with an empty list. The app's built-in import skips sessions that belong to another account's list.
- **Closing a window:** this keeps that Claude instance running, as with any Mac app. Quit it with **Claude > Quit Claude** (⌘Q) in that window to free memory.
- **Disk space:** each account folder holds its own copy of the app's data, including anything large the app downloads for itself.
- **App updates:** this relies on a standard Chromium switch, not a documented Claude feature, so an app update could stop it working. If an update restarts Claude from an account window, the window may come back as the normal app; reopen the account from its launcher. Your normal Claude app is not affected.

## Terms

Check Anthropic's [Consumer Terms](https://www.anthropic.com/legal/consumer-terms) and [Usage Policy](https://www.anthropic.com/legal/aup) for your accounts. This tool is meant for people who hold separate accounts, such as work and personal. Do not share accounts or resell access. Anthropic notes that plan usage limits assume ordinary, individual use.

## Uninstall

1. Quit `Claude accounts` from its menu bar icon (right-click > Quit), and quit the account windows.
2. Run `ccm remove <name>` for each account.
3. Remove `Claude accounts` from Login Items if you added it, and take any launchers out of the Dock.
4. Delete `~/Applications/Claude accounts` and the `ccm` link that `ccm install` reported.
5. Delete what is left of `~/.claude-accounts` (including the hidden `.apps` folder with the Claude copies) and the cloned folder.

## License

MIT, see [LICENSE](../LICENSE).
