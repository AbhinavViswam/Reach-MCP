package app.reach.android.deep

import android.app.ActivityOptions
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.ContactsContract
import app.reach.android.privacy.Approvals
import app.reach.android.privacy.Mask
import app.reach.android.screen.ViewText
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small helpers the deep tools share. */
internal object Deep {

    /** Text read from messages and notifications: masked, unless the user allows the real text this once. */
    suspend fun reveal(context: Context, lines: List<String>, reveal: Boolean, what: String): String {
        val masked = lines.map { Mask.apply(it) }
        val any = masked.any { it.masked }
        val body = if (any && reveal) {
            Approvals.require(context, "see hidden codes in $what", "Your agent wants to read one-time codes or card numbers shown in $what.")
            lines
        } else {
            masked.map { it.text }
        }
        return buildString {
            append(ViewText.FENCE).append('\n')
            append(body.joinToString("\n"))
            if (any && !reveal) append('\n').append(Mask.NOTE)
        }
    }

    fun ago(t: Long, now: Long = System.currentTimeMillis()): String {
        val s = (now - t) / 1000
        return when {
            s < 60 -> "just now"
            s < 3600 -> "${s / 60} min ago"
            s < 86_400 -> "${s / 3600} h ago"
            s < 7 * 86_400 -> "${s / 86_400} d ago"
            else -> SimpleDateFormat("d MMM yyyy", Locale.getDefault()).format(Date(t))
        }
    }

    fun time(t: Long): String = SimpleDateFormat("EEE d MMM HH:mm", Locale.getDefault()).format(Date(t))

    /** A contact's name for a number, when contacts may be read. */
    fun nameFor(context: Context, number: String): String? = runCatching {
        val uri = Uri.withAppendedPath(ContactsContract.PhoneLookup.CONTENT_FILTER_URI, Uri.encode(number))
        context.contentResolver.query(uri, arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        }
    }.getOrNull()

    /** A number for "Mom" or "+91 98…": numbers pass through, names are looked up (exactly one match needed). */
    fun numberFor(context: Context, who: String): Pair<String, String?> {
        val digits = who.filter { it.isDigit() || it == '+' }
        if (who.none { it.isLetter() } && digits.count(Char::isDigit) >= 3) return digits to nameFor(context, digits)
        val hits = Contacts.search(context, who, 6).filter { it.phones.isNotEmpty() }
        val exact = hits.filter { it.name.equals(who, ignoreCase = true) }
        val pick = when {
            exact.size == 1 -> exact[0]
            hits.size == 1 -> hits[0]
            hits.isEmpty() -> throw app.reach.android.mcp.ToolError("not_found", "no contact with a number matches \"$who\"; give the number")
            else -> throw app.reach.android.mcp.ToolError("ambiguous", "\"$who\" matches ${hits.joinToString { it.name }}; say which (or give the number)")
        }
        if (pick.phones.size > 1) {
            throw app.reach.android.mcp.ToolError("ambiguous", "${pick.name} has several numbers: ${pick.phones.joinToString()}; give the number")
        }
        return pick.phones[0] to pick.name
    }

    /** Starts an activity from the background (allowed for Reach while its accessibility service is on). */
    fun start(context: Context, intent: Intent) {
        val s = app.reach.android.access.Access.service.value ?: context
        s.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    /** Sends another app's PendingIntent (a notification's tap or button), allowed to open its screen. */
    fun send(context: Context, pi: PendingIntent, fill: Intent? = null) {
        if (Build.VERSION.SDK_INT >= 34) {
            val opts = ActivityOptions.makeBasic().setPendingIntentBackgroundActivityStartMode(ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
            pi.send(context, 0, fill, null, null, null, opts.toBundle())
        } else {
            pi.send(context, 0, fill)
        }
    }
}
