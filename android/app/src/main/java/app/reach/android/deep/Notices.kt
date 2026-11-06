package app.reach.android.deep

import android.app.RemoteInput
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.AudioManager
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Bundle
import android.service.notification.StatusBarNotification
import android.view.KeyEvent
import app.reach.android.Feature
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.bool
import app.reach.android.mcp.int
import app.reach.android.mcp.long
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.privacy.Approvals
import app.reach.android.privacy.Privacy
import app.reach.android.screen.Ids
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonObject

// ================================================================================================ notifications

class NotificationsTool(private val c: Context) : Tool {
    override val name = "notifications"
    override val description = "Notifications: list (showing now), history (posted since Reach connected), wait (for a new one, e.g. an OTP), " +
        "open (tap it), reply (to a message; asks the user), act (press one of its buttons), dismiss. Codes are hidden unless reveal=true (asks)."
    override val timeoutMs = 70_000L
    override val inputSchema = schema {
        prop("action", "string", "list, history, wait, open, reply, act or dismiss", enum = listOf("list", "history", "wait", "open", "reply", "act", "dismiss"))
        prop("id", "string", "open/reply/act/dismiss: the notification id (n…) from list")
        prop("app", "string", "list/history/wait: only from this app (name or package)")
        prop("text", "string", "reply: the reply")
        prop("button", "string", "act: the button's words, e.g. 'Mark as read'")
        prop("timeout_ms", "integer", "wait: give up after this long (default 30000, max 60000)")
        prop("reveal", "boolean", "show hidden codes (asks the user)")
        required("action")
    }

    private val pm: PackageManager get() = c.packageManager
    private fun label(pkg: String) = runCatching { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg)
    private fun idOf(key: String) = "n" + Ids.hash(key)

    private fun matchesApp(pkg: String, filter: String?): Boolean {
        if (filter == null) return true
        val f = filter.lowercase()
        return pkg.lowercase().contains(f) || label(pkg).lowercase().contains(f)
    }

    private fun service(): NotifyService = NotifyService.instance
        ?: throw ToolError("no_access", "Notification access is off for Reach; the user can turn it on in the Reach app")

    private fun active(): List<StatusBarNotification> = service().activeNotifications.orEmpty()
        .filter { it.packageName != c.packageName && !Privacy.isBlocked(it.packageName, label(it.packageName)) }

    private fun line(p: NotifyService.Posted, sbn: StatusBarNotification?): String {
        val actions = sbn?.notification?.actions.orEmpty().mapNotNull { it.title?.toString() }
        val canReply = sbn?.notification?.actions.orEmpty().any { !it.remoteInputs.isNullOrEmpty() }
        return "${idOf(p.key)} · ${label(p.pkg)} · ${p.title}: ${p.text.replace('\n', ' ').take(300)} · ${Deep.ago(p.time)}" +
            (if (p.ongoing) " · ongoing" else "") +
            (if (actions.isNotEmpty()) " · buttons: ${actions.joinToString()}" else "") + (if (canReply) " · can reply" else "")
    }

    private fun find(id: String): StatusBarNotification =
        active().firstOrNull { idOf(it.key) == id } ?: throw ToolError("not_found", "notification $id is gone; list again")

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.NOTIFICATIONS.require(c)
        val reveal = args.bool("reveal") == true
        val app = args.str("app")
        return when (args.str("action")) {
            "list" -> {
                val rows = active().filter { matchesApp(it.packageName, app) }.mapNotNull { s -> NotifyService.Posted.of(s)?.let { line(it, s) } }
                if (rows.isEmpty()) ToolResult.text("no notifications showing" + (app?.let { " from $it" } ?: ""))
                else ToolResult.text("${rows.size} notifications\n" + Deep.reveal(c, rows, reveal, "your notifications"))
            }
            "history" -> {
                val rows = NotifyService.history().filter { matchesApp(it.pkg, app) && !Privacy.isBlocked(it.pkg, label(it.pkg)) }
                    .takeLast(30).reversed().map { line(it, null) }
                if (rows.isEmpty()) ToolResult.text("nothing posted since Reach connected")
                else ToolResult.text("${rows.size} posted since Reach connected, newest first\n" + Deep.reveal(c, rows, reveal, "your notifications"))
            }
            "wait" -> {
                service()
                val timeout = (args.long("timeout_ms") ?: 30_000L).coerceIn(1_000, 60_000)
                val since = System.currentTimeMillis()
                val start = NotifyService.posted.value
                val hit = withTimeoutOrNull(timeout) {
                    var seen = start
                    while (true) {
                        NotifyService.posted.first { it != seen }.also { seen = it }
                        val p = NotifyService.history().lastOrNull { it.time >= since - 1_000 && matchesApp(it.pkg, app) && !Privacy.isBlocked(it.pkg, label(it.pkg)) }
                        if (p != null) return@withTimeoutOrNull p
                    }
                    @Suppress("UNREACHABLE_CODE")
                    null
                }
                if (hit == null) ToolResult.text("verdict: timeout · no new notification${app?.let { " from $it" } ?: ""} in ${timeout / 1000} s")
                else ToolResult.text("new notification\n" + Deep.reveal(c, listOf(line(hit, active().firstOrNull { it.key == hit.key })), reveal, "a notification"))
            }
            "open" -> {
                val s = find(args.str("id") ?: throw ToolError("bad_args", "open needs id"))
                val pi = s.notification.contentIntent ?: throw ToolError("not_supported", "this notification does nothing when tapped")
                Deep.send(c, pi)
                ToolResult.text("verdict: done · opened ${label(s.packageName)}'s notification (observe to see the screen)")
            }
            "reply" -> {
                val s = find(args.str("id") ?: throw ToolError("bad_args", "reply needs id"))
                val text = args.str("text")?.takeIf { it.isNotBlank() } ?: throw ToolError("bad_args", "reply needs text")
                val action = s.notification.actions.orEmpty().firstOrNull { !it.remoteInputs.isNullOrEmpty() }
                    ?: throw ToolError("not_supported", "this notification has no reply box; open it instead")
                val title = NotifyService.Posted.of(s)?.title.orEmpty()
                Approvals.require(c, "reply on ${label(s.packageName)}", "To $title: \"${text.take(300)}\"")
                val fill = Intent()
                val results = Bundle()
                action.remoteInputs.forEach { results.putCharSequence(it.resultKey, text) }
                RemoteInput.addResultsToIntent(action.remoteInputs, fill, results)
                Deep.send(c, action.actionIntent, fill)
                ToolResult.text("verdict: done · replied to $title on ${label(s.packageName)}")
            }
            "act" -> {
                val s = find(args.str("id") ?: throw ToolError("bad_args", "act needs id"))
                val want = args.str("button")?.lowercase() ?: throw ToolError("bad_args", "act needs button")
                val action = s.notification.actions.orEmpty().firstOrNull { it.title?.toString()?.lowercase() == want }
                    ?: s.notification.actions.orEmpty().firstOrNull { it.title?.toString()?.lowercase()?.contains(want) == true }
                    ?: throw ToolError("not_found", "no button '$want'; buttons: ${s.notification.actions.orEmpty().joinToString { it.title.toString() }}")
                val name = action.title.toString()
                if (!SAFE.any { name.lowercase().contains(it) }) {
                    Approvals.require(c, "press \"$name\" on a ${label(s.packageName)} notification", NotifyService.Posted.of(s)?.let { "${it.title}: ${it.text.take(200)}" }.orEmpty())
                }
                Deep.send(c, action.actionIntent)
                ToolResult.text("verdict: done · pressed \"$name\"")
            }
            "dismiss" -> {
                val s = find(args.str("id") ?: throw ToolError("bad_args", "dismiss needs id"))
                service().cancelNotification(s.key)
                ToolResult.text("verdict: done · dismissed")
            }
            else -> throw ToolError("bad_args", "action must be list, history, wait, open, reply, act or dismiss")
        }
    }

    companion object {
        /** Buttons that can't hurt: pressed without asking. Everything else (delete, pay, call back…) asks. */
        private val SAFE = listOf("mark as read", "mark read", "snooze", "later", "mute", "dismiss")
    }
}

// ================================================================================================ media

class MediaTool(private val c: Context) : Tool {
    override val name = "media"
    override val description = "Music and video: play_pause, next, previous, stop, volume (0–100), and now_playing (needs Notification access)."
    override val inputSchema = schema {
        prop("action", "string", "play_pause, next, previous, stop, volume or now_playing",
            enum = listOf("play_pause", "next", "previous", "stop", "volume", "now_playing"))
        prop("level", "integer", "volume: 0–100")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val audio = c.getSystemService(AudioManager::class.java)
        fun key(code: Int) {
            audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, code))
            audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_UP, code))
        }
        return when (args.str("action")) {
            "play_pause" -> { key(KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE); ToolResult.text("verdict: done · play/pause sent") }
            "next" -> { key(KeyEvent.KEYCODE_MEDIA_NEXT); ToolResult.text("verdict: done · next") }
            "previous" -> { key(KeyEvent.KEYCODE_MEDIA_PREVIOUS); ToolResult.text("verdict: done · previous") }
            "stop" -> { key(KeyEvent.KEYCODE_MEDIA_STOP); ToolResult.text("verdict: done · stop") }
            "volume" -> {
                val level = args.int("level")?.coerceIn(0, 100) ?: throw ToolError("bad_args", "volume needs level 0–100")
                val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                audio.setStreamVolume(AudioManager.STREAM_MUSIC, Math.round(level / 100f * max), 0)
                ToolResult.text("verdict: done · media volume $level%")
            }
            "now_playing" -> {
                if (NotifyService.instance == null) throw ToolError("no_access", "now_playing needs Notification access for Reach (the Reach app has the switch)")
                val msm = c.getSystemService(MediaSessionManager::class.java)
                val sessions: List<MediaController> = msm.getActiveSessions(ComponentName(c, NotifyService::class.java))
                if (sessions.isEmpty()) return ToolResult.text("nothing playing")
                ToolResult.text(sessions.take(3).joinToString("\n") { m ->
                    val md = m.metadata
                    val title = md?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE).orEmpty()
                    val artist = md?.getString(android.media.MediaMetadata.METADATA_KEY_ARTIST).orEmpty()
                    val state = when (m.playbackState?.state) {
                        PlaybackState.STATE_PLAYING -> "playing"; PlaybackState.STATE_PAUSED -> "paused"; else -> "stopped"
                    }
                    val app = runCatching { c.packageManager.getApplicationLabel(c.packageManager.getApplicationInfo(m.packageName, 0)) }.getOrDefault(m.packageName)
                    "$app · $state · $title${if (artist.isNotEmpty()) " — $artist" else ""}"
                })
            }
            else -> throw ToolError("bad_args", "unknown media action")
        }
    }
}

