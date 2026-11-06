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

Other switches: `--background-only` (never touch the real mouse/keyboard), `--lan` (HTTP on every network),
`--selftest` (read the front window once and print it; changes nothing).

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
