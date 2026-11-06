# Reach

Let any AI agent (Claude Code, Claude Desktop, Cursor, Codex…) see and control your **Android phone** and your
**Windows PC** through MCP. Every action is checked, IDs don't break, nothing hangs, and private things stay private.

| Part | Folder | What it is |
|---|---|---|
| Reach for Android | [android/](android/) | An app (APK). The app itself is the MCP server. Uses an accessibility service, so it works on Xiaomi/HyperOS with no adb input. |
| Reach for Windows | [windows/](windows/) | One `reach.exe` (C#/.NET 10, no install). UI Automation in a worker that can't freeze the server. |
| Shared contract | [spec/](spec/) | Tool names, reply shapes, policy file, test tasks. Both servers follow it. |

## Connect (Android, USB)

1. Install the APK, open Reach, turn on its accessibility service, press **Start**.
2. On the PC: `adb forward tcp:8765 tcp:8765`
3. `claude mcp add --transport http reach-android http://127.0.0.1:8765/mcp --header "Authorization: Bearer <token from the app>"`

Wi-Fi: turn on **Wi-Fi access** in the app and use the phone's address it shows instead of `127.0.0.1`.

## Connect (Windows)

`claude mcp add reach-windows -- "C:\path\to\reach.exe"` (details in [windows/README.md](windows/README.md)).

## Licence

MIT. No telemetry.
