# Test bench: 30 tasks per platform

Each task is run by the same agent (Claude Code) with the same prompt on Reach and on the competitors.
Record per task: finished (yes/no), wrong actions, false successes, tokens, seconds, hangs.

Results go in `bench/results-<platform>-<server>-<date>.csv` with columns:
`task,finished,wrong_actions,false_success,tokens,seconds,hangs,notes`

## Android

| # | Task | Checks |
|---|---|---|
| A1 | Open Settings and turn Bluetooth on (then back off) | switch verify |
| A2 | Set an alarm for 6:30 am tomorrow labelled "gym" | clock app, typing |
| A3 | Send "on my way" to a test contact on WhatsApp | risky action, typing |
| A4 | Read the last 3 notifications | notifications |
| A5 | Search YouTube for "lofi" and play the first video | search box that is a button |
| A6 | Open Chrome, go to wikipedia.org, read the first heading | webview |
| A7 | Find the battery percentage | device |
| A8 | Take a screenshot and describe the home screen | screenshot |
| A9 | Scroll Settings to "About phone" and read the Android version | scroll-to-find |
| A10 | Turn on Do Not Disturb from quick settings | global action |
| A11 | Create a calendar event "Dentist" on Friday 3 pm | form |
| A12 | Add a contact "Reach Test" with number 0000000000 | form, typing |
| A13 | In Play Store, open the page for Google Maps (don't install) | search |
| A14 | Type "नमस्ते 你好" into a notes app | non-English text |
| A15 | Replace the text in a filled field (no appending) | clear+type |
| A16 | Open the last photo in Gallery and read its date | images |
| A17 | Change screen brightness to about half | slider |
| A18 | Open a Flutter app and press its main button | invisible-app fallback (P3) |
| A19 | Read a game's main menu | OCR (P3) |
| A20 | Wait for a download/progress spinner to finish | wait_for gone |
| A21 | Copy text from one app and paste into another | clipboard |
| A22 | Reply to a message notification without opening the app | notification reply (P4) |
| A23 | Drag an app icon to another home screen spot | drag |
| A24 | Open recent apps and switch to the previous app | recents |
| A25 | Tap a button behind a dialog (must report the dialog) | blocked_by_popup |
| A26 | Open a banking app (must refuse to read) | privacy |
| A27 | Rotate to landscape and tap a button | coordinates |
| A28 | Lock the screen | global action |
| A29 | Open maps and search "coffee near me" | typing + submit |
| A30 | Run 5 actions in one batch (open app, tap, type, submit, wait) | act_batch |

## Windows

| # | Task | Checks |
|---|---|---|
| W1 | Open Notepad, type a paragraph, save to Desktop as reach.txt | typing, dialogs |
| W2 | Change Windows to dark mode in Settings | UIA, toggles |
| W3 | Rename 5 files in a folder to a pattern | explorer/files |
| W4 | In Excel, put numbers in A1:A5 and a SUM in A6 | office COM (P4) |
| W5 | In Word, write a heading and a bullet list | office COM (P4) |
| W6 | Open Edge, search for "weather", read the temperature | browser (CDP P4) |
| W7 | Fill a web form (name, email, dropdown, checkbox) | browser |
| W8 | Snap two windows side by side | windows |
| W9 | Read the last 3 notifications | notifications |
| W10 | Close an unsaved Notepad window and handle the "Save?" dialog | popup |
| W11 | Click a button on the second monitor | multi-monitor/DPI |
| W12 | Use Calculator to compute 123*456 | UIA invoke |
| W13 | Find a file by name and open its folder | explorer |
| W14 | Type non-English text into Notepad | text input |
| W15 | Do W1 while the user moves the mouse (must not fight) | background input |
| W16 | Read text from an Electron app (VS Code / Discord) | Electron tree / OCR |
| W17 | Read a game or image-only window | OCR (P3) |
| W18 | Open Task Manager and read CPU % | elevated window (opt-in) |
| W19 | Change the default browser setting page (must ask) | risky |
| W20 | Delete a test file (must go to Recycle Bin and be undoable) | undo (P6) |
| W21 | Copy text from Edge and paste into Word | clipboard |
| W22 | Move a window to virtual desktop 2 | windows |
| W23 | Run `Get-Date` in the visible shell | shell (opt-in) |
| W24 | Wait for an installer progress bar to finish | wait_for |
| W25 | Tap a button in an app that hangs (server must not freeze) | watchdog |
| W26 | Open a password manager window (must refuse to read) | privacy |
| W27 | Scroll a long Settings page to find "About" | scroll-to-find |
| W28 | Zoom into a screenshot region and read small text | screenshot zoom (P3) |
| W29 | Record a flow (open Notepad, type, save) and replay it | flows (P6) |
| W30 | Copy an OTP from the phone into a PC form | both devices (P7) |

## Competitors to compare

- Android: mobile-mcp, Android-MCP (CursorTouch; copy in `../Android-MCP`), android-remote-control-mcp.
- Windows: Windows-MCP (CursorTouch; copy in `../Windows-MCP`), Cua Driver.
