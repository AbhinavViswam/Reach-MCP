package app.reach.android.deep

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Reach's notification listener (the user turns it on in Notification access). Keeps the last notifications
 * posted since it connected in memory only, so the agent can wait for a new one ("the OTP has arrived").
 */
class NotifyService : NotificationListenerService() {

    override fun onListenerConnected() {
        instance = this
    }

    override fun onListenerDisconnected() {
        if (instance === this) instance = null
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == packageName) return
        val p = Posted.of(sbn) ?: return
        synchronized(history) {
            history.addLast(p)
            while (history.size > 300) history.removeFirst()
        }
        _posted.value = _posted.value + 1
    }

    /** One notification as plain data. */
    data class Posted(val key: String, val pkg: String, val title: String, val text: String, val time: Long, val ongoing: Boolean) {
        companion object {
            fun of(sbn: StatusBarNotification): Posted? {
                val n = sbn.notification ?: return null
                // Group summaries repeat their children.
                if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return null
                val e = n.extras
                val title = e.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
                val big = e.getCharSequence(Notification.EXTRA_BIG_TEXT)?.toString()
                val lines = e.getCharSequenceArray(Notification.EXTRA_TEXT_LINES)?.joinToString(" / ")
                val text = (big ?: lines ?: e.getCharSequence(Notification.EXTRA_TEXT)?.toString()).orEmpty()
                if (title.isBlank() && text.isBlank()) return null
                return Posted(sbn.key, sbn.packageName, title, text, sbn.postTime, sbn.isOngoing)
            }
        }
    }

    companion object {
        @Volatile var instance: NotifyService? = null
            private set
        private val history = ArrayDeque<Posted>()
        private val _posted = MutableStateFlow(0L)
        /** Bumped on every new notification (wait). */
        val posted: StateFlow<Long> = _posted.asStateFlow()

        fun history(): List<Posted> = synchronized(history) { history.toList() }
    }
}
