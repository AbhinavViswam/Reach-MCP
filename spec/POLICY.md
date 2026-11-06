# Reach policy file (v0.1)

The same rules on both platforms, edited only on the device: the Reach app on Android (stored in its private
settings), the settings window on Windows (`%LOCALAPPDATA%\Reach\config.json`). The server reads them before every
call. The model never sees them and can't change them. The JSON below is the idea; each platform stores its own
fields (see windows/README.md for the PC file).

```json
{
  "version": 1,
  "defaults": { "unknownApp": "allow" },
  "risk": {
    "read": "allow",
    "act": "allow",
    "risky": "ask",
    "blocked": "deny"
  },
  "apps": {
    "com.google.android.apps.nbu.paisa.user": "block",
    "com.x8bit.bitwarden": "block",
    "com.whatsapp": "allow",
    "com.android.settings": "read_only"
  },
  "features": {
    "remote": false,
    "wifi": false,
    "shell": false,
    "elevated": false,
    "sms": false,
    "calls": false,
    "files": []
  },
  "masking": { "passwords": true, "otp": true, "cards": true },
  "approvals": { "timeoutSeconds": 30, "approver": "this_device" }
}
```

## App rules

| Rule | Meaning |
|---|---|
| `allow` | Read and act. Risky actions still ask. |
| `read_only` | Can be read, scrolled and left (back/home); every other action is refused (`read_only_app`). |
| `block` | Never read, never acted on. The screen view shows only `app <name> is blocked by Reach privacy rules`. |

Default blocked list: banking, payments/UPI, wallets, password managers and authenticator apps. Matched by package
name or app name words (`bank`, `pay`, `wallet`, `password`, `authenticator`, `vault`). The user can change any of them: their own rule for an app wins.

## Kill switch

Pause stops a running action and refuses every call (`paused`) until the user resumes. Phone: app, notification,
Quick Settings tile. PC: tray icon, Ctrl+Alt+Backspace, settings window, `reach.exe --pause`. Stop (phone) or Quit
(PC tray) ends the server.

## Risk levels

| Level | Examples | Default |
|---|---|---|
| `read` | observe, find, device, apps list | allow |
| `act` | tap, type, scroll, open app | allow in allowed apps |
| `risky` | send message, pay, delete, install, call, change settings, shell | ask the user |
| `blocked` | anything in a blocked app | deny |

"Ask" shows an approval on the device (a notification on the phone, an always-on-top box on Windows), or on the
phone for PC actions when the user turns that on (`POST /approve`; if the phone can't be reached the PC asks). No answer before `timeoutSeconds` = **no**.

## Masking (always before anything leaves the device)

- Password fields: value never read.
- OTP: 4–8 digit codes next to words like "OTP", "code", "verification" → `••••••`.
- Card numbers (Luhn-valid 13–19 digits) → `•••• 1234`.
- Secure screens (Android `FLAG_SECURE`) are never captured.

## Audit log

Every call, its result line and every approval: `audit.jsonl` on the device, shown in the app / settings window
(Activity). Codes masked, values cut short, rotated at a few MB. Nothing is sent anywhere.
