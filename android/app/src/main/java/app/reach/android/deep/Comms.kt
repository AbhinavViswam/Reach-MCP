package app.reach.android.deep

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ContentProviderOperation
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.Uri
import android.provider.CallLog
import android.provider.ContactsContract
import android.provider.Telephony
import android.telecom.TelecomManager
import android.telephony.SmsManager
import app.reach.android.Feature
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.bool
import app.reach.android.mcp.int
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.privacy.Approvals
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonObject

// ================================================================================================ sms

class SmsTool(private val c: Context) : Tool {
    override val name = "sms"
    override val description = "Text messages: list recent ones (optionally from one contact or containing words), or send one. " +
        "Sending asks the user on the phone first. One-time codes are hidden unless reveal=true (also asks)."
    override val timeoutMs = 60_000L
    override val inputSchema = schema {
        prop("action", "string", "list or send", enum = listOf("list", "send"))
        prop("with", "string", "list: only messages with this contact or number; send: who to send to (name or number)")
        prop("query", "string", "list: only messages containing these words")
        prop("text", "string", "send: the message")
        prop("limit", "integer", "list: how many (default 15, max 50)")
        prop("reveal", "boolean", "list: show hidden codes (asks the user)")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.SMS.require(c)
        return when (args.str("action")) {
            "list" -> list(args)
            "send" -> send(args)
            else -> throw ToolError("bad_args", "action must be list or send")
        }
    }

    private suspend fun list(args: JsonObject): ToolResult {
        val limit = (args.int("limit") ?: 15).coerceIn(1, 50)
        val with = args.str("with")?.let { runCatching { Deep.numberFor(c, it).first }.getOrElse { _ -> it } }
        val query = args.str("query")?.lowercase()
        val rows = ArrayList<String>()
        val names = HashMap<String, String?>()
        c.contentResolver.query(
            Telephony.Sms.CONTENT_URI,
            arrayOf(Telephony.Sms.ADDRESS, Telephony.Sms.BODY, Telephony.Sms.DATE, Telephony.Sms.TYPE, Telephony.Sms.READ),
            null, null, "${Telephony.Sms.DATE} DESC",
        )?.use { cur ->
            while (cur.moveToNext() && rows.size < limit) {
                val addr = cur.getString(0).orEmpty()
                val body = cur.getString(1).orEmpty()
                if (with != null && !sameNumber(addr, with) && !addr.equals(with, true)) continue
                if (query != null && !body.lowercase().contains(query)) continue
                val who = names.getOrPut(addr) { Deep.nameFor(c, addr) }?.let { "$it ($addr)" } ?: addr
                val dir = if (cur.getInt(3) == Telephony.Sms.MESSAGE_TYPE_SENT) "to" else "from"
                val unread = if (cur.getInt(4) == 0 && dir == "from") " · unread" else ""
                rows += "$dir $who · ${Deep.ago(cur.getLong(2))}$unread: ${body.replace('\n', ' ').take(300)}"
            }
        }
        if (rows.isEmpty()) return ToolResult.text("no messages" + (with?.let { " with $it" } ?: "") + (query?.let { " containing \"$it\"" } ?: ""))
        return ToolResult.text("${rows.size} messages, newest first\n" + Deep.reveal(c, rows, args.bool("reveal") == true, "your messages"))
    }

    private fun sameNumber(a: String, b: String): Boolean {
        val x = a.filter(Char::isDigit)
        val y = b.filter(Char::isDigit)
        return x.isNotEmpty() && y.isNotEmpty() && (x.endsWith(y.takeLast(10)) || y.endsWith(x.takeLast(10)))
    }

    private suspend fun send(args: JsonObject): ToolResult {
        val to = args.str("with") ?: throw ToolError("bad_args", "send needs with (who) and text")
        val text = args.str("text")?.takeIf { it.isNotBlank() } ?: throw ToolError("bad_args", "send needs text")
        val (number, name) = Deep.numberFor(c, to)
        val who = name?.let { "$it ($number)" } ?: number
        Approvals.require(c, "send an SMS to $who", "\"${text.take(300)}\"")
        val sms = c.getSystemService(SmsManager::class.java) ?: throw ToolError("not_supported", "this phone can't send SMS")
        val parts = sms.divideMessage(text)
        val action = "app.reach.android.SMS_SENT." + System.nanoTime()
        val done = CompletableDeferred<Int>()
        var left = parts.size
        val rx = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, i: Intent) {
                if (resultCode != android.app.Activity.RESULT_OK) done.complete(resultCode)
                else if (--left <= 0) done.complete(android.app.Activity.RESULT_OK)
            }
        }
        c.registerReceiver(rx, IntentFilter(action), Context.RECEIVER_NOT_EXPORTED)
        try {
            val sent = ArrayList(parts.mapIndexed { i, _ ->
                PendingIntent.getBroadcast(c, i, Intent(action).setPackage(c.packageName), PendingIntent.FLAG_IMMUTABLE)
            })
            sms.sendMultipartTextMessage(number, null, parts, sent, null)
            val code = withTimeoutOrNull(20_000) { done.await() }
            return when (code) {
                android.app.Activity.RESULT_OK -> ToolResult.text("verdict: done · sent to $who (${parts.size} part${if (parts.size > 1) "s" else ""})")
                null -> ToolResult.text("verdict: unknown · handed to the phone for $who; no sent report within 20 s")
                else -> ToolResult.error("not_sent", "the phone couldn't send it (code $code: no signal, no SIM credit or airplane mode?)")
            }
        } finally {
            runCatching { c.unregisterReceiver(rx) }
        }
    }
}

// ================================================================================================ calls

class CallsTool(private val c: Context) : Tool {
    override val name = "calls"
    override val description = "Phone calls: log (recent calls), dial (opens the dialer with a number, no call), " +
        "call (places the call; asks the user first), answer, hang_up."
    override val timeoutMs = 45_000L
    override val inputSchema = schema {
        prop("action", "string", "log, dial, call, answer or hang_up", enum = listOf("log", "dial", "call", "answer", "hang_up"))
        prop("who", "string", "dial/call: contact name or number")
        prop("limit", "integer", "log: how many (default 15)")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.CALLS.require(c)
        val telecom = c.getSystemService(TelecomManager::class.java)
        return when (args.str("action")) {
            "log" -> {
                val limit = (args.int("limit") ?: 15).coerceIn(1, 50)
                val rows = ArrayList<String>()
                c.contentResolver.query(
                    CallLog.Calls.CONTENT_URI,
                    arrayOf(CallLog.Calls.NUMBER, CallLog.Calls.CACHED_NAME, CallLog.Calls.TYPE, CallLog.Calls.DATE, CallLog.Calls.DURATION),
                    null, null, "${CallLog.Calls.DATE} DESC",
                )?.use { cur ->
                    while (cur.moveToNext() && rows.size < limit) {
                        val num = cur.getString(0).orEmpty()
                        val who = cur.getString(1)?.takeIf { it.isNotBlank() }?.let { "$it ($num)" } ?: num
                        val kind = when (cur.getInt(2)) {
                            CallLog.Calls.INCOMING_TYPE -> "incoming"; CallLog.Calls.OUTGOING_TYPE -> "outgoing"
                            CallLog.Calls.MISSED_TYPE -> "missed"; CallLog.Calls.REJECTED_TYPE -> "rejected"; else -> "other"
                        }
                        val secs = cur.getLong(4)
                        rows += "$kind · $who · ${Deep.ago(cur.getLong(3))}" + if (secs > 0) " · ${secs / 60}m ${secs % 60}s" else ""
                    }
                }
                ToolResult.text(if (rows.isEmpty()) "no calls in the log" else "${rows.size} calls, newest first\n" + rows.joinToString("\n"))
            }
            "dial" -> {
                val (number, name) = Deep.numberFor(c, args.str("who") ?: throw ToolError("bad_args", "dial needs who"))
                Deep.start(c, Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + Uri.encode(number))))
                ToolResult.text("verdict: done · dialer open with ${name?.let { "$it ($number)" } ?: number}; nothing called yet")
            }
            "call" -> {
                val (number, name) = Deep.numberFor(c, args.str("who") ?: throw ToolError("bad_args", "call needs who"))
                val who = name?.let { "$it ($number)" } ?: number
                Approvals.require(c, "call $who", "Your agent wants to place a phone call to $who.")
                Deep.start(c, Intent(Intent.ACTION_CALL, Uri.parse("tel:" + Uri.encode(number))))
                ToolResult.text("verdict: done · calling $who")
            }
            "answer" -> {
                @Suppress("DEPRECATION") telecom.acceptRingingCall()
                ToolResult.text("verdict: unknown · asked the phone to answer the ringing call")
            }
            "hang_up" -> {
                @Suppress("DEPRECATION") val ok = telecom.endCall()
                ToolResult.text(if (ok) "verdict: done · call ended" else "verdict: no_effect · no call to end (or this phone doesn't let apps end calls)")
            }
            else -> throw ToolError("bad_args", "action must be log, dial, call, answer or hang_up")
        }
    }
}

// ================================================================================================ contacts

internal object Contacts {
    data class Person(val id: Long, val name: String, val phones: List<String>, val emails: List<String>)

    fun search(c: Context, q: String, limit: Int): List<Person> {
        val uri = Uri.withAppendedPath(ContactsContract.Contacts.CONTENT_FILTER_URI, Uri.encode(q))
        val people = ArrayList<Pair<Long, String>>()
        c.contentResolver.query(uri, arrayOf(ContactsContract.Contacts._ID, ContactsContract.Contacts.DISPLAY_NAME), null, null, null)?.use {
            while (it.moveToNext() && people.size < limit) people += it.getLong(0) to it.getString(1).orEmpty()
        }
        return people.map { (id, name) -> Person(id, name, data(c, id, ContactsContract.CommonDataKinds.Phone.CONTENT_ITEM_TYPE), data(c, id, ContactsContract.CommonDataKinds.Email.CONTENT_ITEM_TYPE)) }
    }

    private fun data(c: Context, id: Long, mime: String): List<String> {
        val out = LinkedHashSet<String>()
        c.contentResolver.query(
            ContactsContract.Data.CONTENT_URI, arrayOf(ContactsContract.Data.DATA1),
            "${ContactsContract.Data.CONTACT_ID}=? AND ${ContactsContract.Data.MIMETYPE}=?", arrayOf(id.toString(), mime), null,
        )?.use { while (it.moveToNext()) it.getString(0)?.let { v -> out += v.replace(" ", "") } }
        return out.toList()
    }
}

class ContactsTool(private val c: Context) : Tool {
    override val name = "contacts"
    override val description = "Contacts: search by name or number (returns numbers and emails), or add one (asks the user first)."
    override val timeoutMs = 45_000L
    override val inputSchema = schema {
        prop("action", "string", "search or add", enum = listOf("search", "add"))
        prop("query", "string", "search: a name or part of a number")
        prop("name", "string", "add: full name")
        prop("phone", "string", "add: phone number")
        prop("email", "string", "add: email")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.CONTACTS.require(c)
        return when (args.str("action")) {
            "search" -> {
                val q = args.str("query") ?: throw ToolError("bad_args", "search needs query")
                val hits = Contacts.search(c, q, 10)
                if (hits.isEmpty()) ToolResult.text("no contact matches \"$q\"")
                else ToolResult.text(app.reach.android.screen.ViewText.FENCE + "\n" + hits.joinToString("\n") { p ->
                    p.name + (if (p.phones.isNotEmpty()) " · " + p.phones.joinToString() else "") + (if (p.emails.isNotEmpty()) " · " + p.emails.joinToString() else "")
                })
            }
            "add" -> {
                val name = args.str("name") ?: throw ToolError("bad_args", "add needs name")
                val phone = args.str("phone")
                val email = args.str("email")
                Approvals.require(c, "add a contact", "$name${phone?.let { " · $it" } ?: ""}${email?.let { " · $it" } ?: ""}")
                val ops = arrayListOf(
                    ContentProviderOperation.newInsert(ContactsContract.RawContacts.CONTENT_URI)
                        .withValue(ContactsContract.RawContacts.ACCOUNT_TYPE, null)
                        .withValue(ContactsContract.RawContacts.ACCOUNT_NAME, null).build(),
                    ContentProviderOperation.newInsert(ContactsContract.Data.CONTENT_URI)
                        .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                        .withValue(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.StructuredName.CONTENT_ITEM_TYPE)
                        .withValue(ContactsContract.CommonDataKinds.StructuredName.DISPLAY_NAME, name).build(),
                )
                phone?.let {
                    ops += ContentProviderOperation.newInsert(ContactsContract.Data.CONTENT_URI)
                        .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                        .withValue(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.Phone.CONTENT_ITEM_TYPE)
                        .withValue(ContactsContract.CommonDataKinds.Phone.NUMBER, it)
                        .withValue(ContactsContract.CommonDataKinds.Phone.TYPE, ContactsContract.CommonDataKinds.Phone.TYPE_MOBILE).build()
                }
                email?.let {
                    ops += ContentProviderOperation.newInsert(ContactsContract.Data.CONTENT_URI)
                        .withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0)
                        .withValue(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.Email.CONTENT_ITEM_TYPE)
                        .withValue(ContactsContract.CommonDataKinds.Email.ADDRESS, it).build()
                }
                c.contentResolver.applyBatch(ContactsContract.AUTHORITY, ops)
                ToolResult.text("verdict: done · added $name")
            }
            else -> throw ToolError("bad_args", "action must be search or add")
        }
    }
}
