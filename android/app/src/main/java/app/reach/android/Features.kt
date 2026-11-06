package app.reach.android

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Environment
import android.provider.Settings as AndroidSettings
import app.reach.android.deep.NotifyService
import app.reach.android.mcp.ToolError

/**
 * The extra abilities (spec/CONTRACT.md §6), each behind a switch in the app and its Android permission
 * (spec/POLICY.md: SMS, calls and files are off until the user turns them on). A tool whose ability is off is not
 * even listed to the agent, which also saves tokens.
 */
enum class Feature(
    val key: String,
    val label: String,
    val about: String,
    val permissions: List<String>,
    val defaultOn: Boolean,
    val special: Special = Special.NONE,
) {
    NOTIFICATIONS("notifications", "Notifications", "read, open, reply to and dismiss notifications; media info",
        emptyList(), true, Special.NOTIFICATION_ACCESS),
    SMS("sms", "SMS", "read messages and send them (each send asks you)",
        listOf(Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS, Manifest.permission.READ_CONTACTS), false),
    CALLS("calls", "Calls", "call log, place calls (each call asks you), answer and hang up",
        listOf(Manifest.permission.READ_CALL_LOG, Manifest.permission.CALL_PHONE, Manifest.permission.ANSWER_PHONE_CALLS,
            Manifest.permission.READ_CONTACTS), false),
    CONTACTS("contacts", "Contacts", "search contacts; adding one asks you",
        listOf(Manifest.permission.READ_CONTACTS, Manifest.permission.WRITE_CONTACTS), true),
    CALENDAR("calendar", "Calendar", "read and add events",
        listOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR), true),
    FILES("files", "Files", "Download, Documents, Pictures, DCIM and Reach folders only; deleting asks you",
        emptyList(), false, Special.ALL_FILES),
    ALARMS("alarms", "Alarms and timers", "set alarms and timers, see the next alarm", emptyList(), true),
    MEDIA("media", "Media", "play, pause, skip and volume; what's playing (needs Notification access)",
        emptyList(), true, Special.NOTIFICATION_ACCESS),
    LINKS("links", "Links and sharing", "open web links, app links and settings pages; share text", emptyList(), true),
    CLIPBOARD("clipboard", "Clipboard", "read and set the clipboard (codes hidden)", emptyList(), true),
    PC("pc", "Your PC", "use Reach for Windows on your PC from this phone's agent (set it up under Your PC)", emptyList(), true);

    enum class Special { NONE, NOTIFICATION_ACCESS, ALL_FILES }

    fun switchedOn(): Boolean = Settings.feature(key, defaultOn)

    fun missingPermissions(c: Context): List<String> =
        permissions.filter { c.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }

    fun specialGranted(c: Context): Boolean = when (special) {
        Special.NONE -> true
        Special.ALL_FILES -> Environment.isExternalStorageManager()
        Special.NOTIFICATION_ACCESS -> {
            val flat = AndroidSettings.Secure.getString(c.contentResolver, "enabled_notification_listeners").orEmpty()
            flat.split(':').any { ComponentName.unflattenFromString(it) == ComponentName(c, NotifyService::class.java) }
        }
    }

    fun ready(c: Context) = switchedOn() && missingPermissions(c).isEmpty() && specialGranted(c)

    /** For tools: a clear next step instead of a crash when the ability isn't ready. */
    fun require(c: Context) {
        if (!switchedOn()) throw ToolError("feature_off", "'$label' is off; the user can turn it on in the Reach app (Extra abilities)")
        val missing = missingPermissions(c)
        if (missing.isNotEmpty()) throw ToolError("no_permission", "'$label' needs Android permission (${missing.joinToString { it.substringAfterLast('.') }}); the user can grant it in the Reach app")
        if (!specialGranted(c)) throw ToolError("no_permission", "'$label' needs ${if (special == Special.ALL_FILES) "All files access" else "Notification access"}; the user can grant it in the Reach app")
    }
}
