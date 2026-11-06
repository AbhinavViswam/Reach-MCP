# Reach for Windows

One `reach.exe` (C# / .NET 10, self-contained: nothing else to install).

## Use it

Claude Code (stdio, the simplest):

```
claude mcp add reach-windows -- "C:\path\to\reach.exe"
```

Over HTTP (other agents, or later the phone):

```
reach.exe --http            # http://127.0.0.1:8766/mcp
reach.exe --token           # the token to send as "Authorization: Bearer <token>"
claude mcp add --transport http reach-windows http://127.0.0.1:8766/mcp --header "Authorization: Bearer <token>"
```

Other switches: `--settings`, `--pause`, `--resume`, `--background-only` (never touch the real mouse/keyboard), `--lan` (HTTP on every network),
`--selftest` (read the front window once and print it; changes nothing).

## Abilities and safety

**Settings window:** double-click `reach.exe`, run `reach.exe --settings`, or use the tray icon. It has:

- **Pause** (the kill switch): every call is refused and a running action is stopped at once. Also from the tray
  icon, the hotkey **Ctrl+Alt+Backspace**, or `reach.exe --pause` / `--resume`. Works for every Reach on the PC.
- **Abilities:** Office, Browser, Files, Window layout, File Explorer, Notifications, Programs, Clipboard,
  PowerShell (**off** by default), and "Real mouse and keyboard" (off = background only).
- **Folders** the files ability may use.
- **App rules:** a program name (`excel`) or words in a window title (`hdfc bank`) → Allow / Read only / Block.
  Read only = can be read and scrolled, never clicked or typed into. Your rules win over the built-in blocks
  (password managers, authenticators, banking and PayPal windows).
- **Approvals:** on this PC, or on your phone (Reach for Android, `POST /approve` with the phone's token). If the
  phone can't be reached, this PC asks.
- **Activity:** every call, its result and every approval (`%LOCALAPPDATA%\Reach\audit.jsonl`, codes masked).

Changes are saved to `%LOCALAPPDATA%\Reach\config.json` and count from the next call; reconnect the agent to see
tools you turned on. Risky actions (sending mail, shell commands, killing a program, replacing or deleting files,
launching a program file, showing hidden codes) always ask; no answer in 30 s means no. Shell commands and their
output also go to `shell.log`.

The browser tool needs Edge or Chrome with a debugging port. `browser` action `start` opens Edge with its own Reach
profile (Chrome and Edge no longer allow the debugging port on your everyday profile).

## Your phone and remote use

- **Phones** (settings › Phones): add a phone running Reach for Android by name, address and token. Your agent
  here then has a `phone` tool that runs phone tools ("read the OTP on my phone, type it here"). The phone can
  also answer this PC's approvals (settings › Approvals).
- **The phone using this PC:** run `reach.exe --http`, copy the token (settings › Copy HTTP token) into the phone's
  Reach app (Your PC). USB: `adb reverse tcp:8766 tcp:8766`.
- **Away from home:** settings › Connect › "This PC and my Tailscale devices" (or `--tailscale`) lets only your
  Tailscale devices in, plus this PC. "Every network" (`--lan`) is for trusted networks only.

## Flows and undo

- **Flows:** ask your agent to "record a flow" while it does a task; later "run the flow" replays it with no model
  calls (`flows` tool). Saved in `%LOCALAPPDATA%\Reach\flows\`. Settings › Flows and undo lists and deletes them.
- **Undo:** every change Reach makes (files, window places, clipboard, Excel cells) can be put back: ask your agent
  to undo, or use Settings › "Undo Reach's last change". Sent mail, commands and closed programs can't be undone and
  are listed as such.

## How it works

- `reach.exe` is the MCP server. All UI Automation work runs in a second copy of itself (`reach.exe --worker`).
  If a window freezes, the call times out, the worker is killed and restarted, and the server keeps going.
- Each window is read with one cached UI Automation request (all properties at once), with UIA's own time limits on.
- Actions are background first: Invoke / Toggle / Select / Expand / SetValue / Scroll patterns don't move the
  user's mouse. Real mouse/keyboard only when needed: Reach waits until the user pauses, puts the cursor back after,
  and the reply says it used them. A background press that changed nothing gets one retry with the mouse.
- Per-monitor DPI aware, so positions are right on scaled and multiple monitors.
- Element IDs use the same hash as Reach for Android (tests check both give `igaz` for `abc`).

## Build

```
dotnet test tests/Reach.Tests
dotnet publish src/Reach -c Release -o publish     # publish/reach.exe
```
