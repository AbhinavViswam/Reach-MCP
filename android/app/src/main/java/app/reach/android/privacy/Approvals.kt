package app.reach.android.privacy

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import app.reach.android.R
import app.reach.android.mcp.ToolError
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Asks the user before a risky action (send, call, delete, reply…; spec/POLICY.md): a heads-up notification
 * with Allow and Deny. No answer in time means no. The model never sees or changes this.
 */
object Approvals {
    private const val CHANNEL = "approvals"
    private const val TIMEOUT_MS = 30_000L
    private val waiting = ConcurrentHashMap<Int, CompletableDeferred<Boolean>>()
    private val next = AtomicInteger(1000)

    /** Throws when the user says no or doesn't answer; returns when they allow it. */
    suspend fun require(context: Context, what: String, detail: String) {
        if (!ask(context, what, detail)) throw ToolError("denied", "the user did not allow: $what (or didn't answer within 30 s); don't retry unless they ask")
    }

    suspend fun ask(context: Context, what: String, detail: String): Boolean {
        val id = next.getAndIncrement()
        val answer = CompletableDeferred<Boolean>()
        waiting[id] = answer
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Approvals", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "Your AI agent asks before sending, calling, deleting and other risky actions"
        })
        fun action(allow: Boolean) = PendingIntent.getBroadcast(
            context, id * 2 + (if (allow) 1 else 0),
            Intent(context, Answer::class.java).putExtra("id", id).putExtra("allow", allow),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_fg)
            .setContentTitle("Allow your agent to $what?")
            .setContentText(detail)
            .setStyle(NotificationCompat.BigTextStyle().bigText(detail))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setTimeoutAfter(TIMEOUT_MS)
            .setDeleteIntent(action(false))
            .addAction(0, "Deny", action(false))
            .addAction(0, "Allow", action(true))
            .build()
        nm.notify(id, n)
        val ok = withTimeoutOrNull(TIMEOUT_MS) { answer.await() } ?: false
        waiting.remove(id)
        nm.cancel(id)
        return ok
    }

    class Answer : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val id = intent.getIntExtra("id", -1)
            waiting[id]?.complete(intent.getBooleanExtra("allow", false))
            context.getSystemService(NotificationManager::class.java).cancel(id)
        }
    }
}
