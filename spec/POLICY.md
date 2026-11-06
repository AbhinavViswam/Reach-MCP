# Reach policy file (v0.1)

One format on both platforms. Stored on the device (`reach-policy.json`), edited from the app. The server reads it
before every call. The model never sees it and can't change it.

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
| `read_only` | Can be read; every action is refused. |
| `block` | Never read, never acted on. The screen view shows only `app <name> is blocked by Reach privacy rules`. |

Default blocked list: banking, payments/UPI, wallets, password managers and authenticator apps. Matched by package
name or app name words (`bank`, `pay`, `wallet`, `password`, `authenticator`, `vault`). The user can change any of them.

## Risk levels

| Level | Examples | Default |
|---|---|---|
| `read` | observe, find, device, apps list | allow |
| `act` | tap, type, scroll, open app | allow in allowed apps |
| `risky` | send message, pay, delete, install, call, change settings, shell | ask the user |
| `blocked` | anything in a blocked app | deny |

"Ask" shows an approval on the device (a sheet on the phone, a toast on Windows; later the phone can approve PC
actions). No answer before `timeoutSeconds` = **no**.

## Masking (always before anything leaves the device)

- Password fields: value never read.
- OTP: 4–8 digit codes next to words like "OTP", "code", "verification" → `••••••`.
- Card numbers (Luhn-valid 13–19 digits) → `•••• 1234`.
- Secure screens (Android `FLAG_SECURE`) are never captured.

## Audit log

Every call, its verdict and who approved it: `audit.jsonl` on the device, shown in the app. Nothing is sent anywhere.
