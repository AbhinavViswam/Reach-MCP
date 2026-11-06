package app.reach.android.access

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.view.accessibility.AccessibilityEvent
import app.reach.android.screen.EventCounts
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.atomic.AtomicLong

/**
 * Reach's accessibility service. It reads nothing by itself: the screen is read only when an agent call asks
 * (screen/ScreenReader). While the server runs it listens to screen events, so actions can wait for the screen to
 * settle and wait_for can react at once instead of sleeping.
 */
class ReachAccessService : AccessibilityService() {

    override fun onServiceConnected() {
        Access.attach(this)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        Access.onEvent(event)
    }

    override fun onInterrupt() {}

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        Access.detach(this)
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        Access.detach(this)
        super.onDestroy()
    }

    /** Screen events on while the server runs, off otherwise (nothing to do, nothing to spend). */
    fun listen(on: Boolean) {
        val info = serviceInfo ?: return
        info.eventTypes = if (on) EVENTS else 0
        info.notificationTimeout = 50
        serviceInfo = info
    }

    companion object {
        const val EVENTS = AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED or
            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED or
            AccessibilityEvent.TYPE_WINDOWS_CHANGED or
            AccessibilityEvent.TYPE_VIEW_SCROLLED or
            AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED or
            AccessibilityEvent.TYPE_VIEW_CLICKED
    }
}

/** The service while Android has it running, and the event counters actions are judged by. */
object Access {
    private val _service = MutableStateFlow<ReachAccessService?>(null)
    val service: StateFlow<ReachAccessService?> = _service.asStateFlow()
    val on: Boolean get() = _service.value != null

    private val any = AtomicLong()
    private val scrolled = AtomicLong()
    private val text = AtomicLong()
    private val windows = AtomicLong()

    /** Bumped on every event; wait_for and settle watch it. */
    private val _tick = MutableStateFlow(0L)
    val tick: StateFlow<Long> = _tick.asStateFlow()

    @Volatile var lastEventAt: Long = 0L
        private set

    /** The last activity class Android reported (for "current app/activity"). */
    @Volatile var lastWindowClass: String? = null
        private set

    @Volatile private var listening = false

    fun counts() = EventCounts(any.get(), scrolled.get(), text.get(), windows.get())

    /** Reach is ticked in Settings › Accessibility (whether or not Android is running it right now). */
    fun enabledInSettings(c: android.content.Context): Boolean {
        val flat = android.provider.Settings.Secure.getString(c.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES).orEmpty()
        val me = android.content.ComponentName(c, ReachAccessService::class.java)
        return flat.split(':').any { android.content.ComponentName.unflattenFromString(it) == me }
    }

    /**
     * Why there is no screen access, and the fix. "Stopped" is the case where it is ticked but Android isn't running
     * it: Android does this when the app is restarted after a permission change (All files access) or force-stopped,
     * and it only comes back when the user turns it off and on.
     */
    fun offHint(c: android.content.Context?): String =
        if (c != null && enabledInSettings(c))
            "Android stopped Reach's screen access (it does this after a permission such as All files access changes). " +
                "The user turns Reach off and on again in Settings › Accessibility"
        else "turn on Reach in Settings › Accessibility (open the Reach app for a button)"

    /** The app's context, for hints from places that have none. */
    @Volatile var app: android.content.Context? = null

    internal fun attach(s: ReachAccessService) {
        _service.value = s
        s.listen(listening)
    }

    internal fun detach(s: ReachAccessService) {
        if (_service.value === s) _service.value = null
    }

    fun listen(on: Boolean) {
        listening = on
        _service.value?.listen(on)
    }

    internal fun onEvent(e: AccessibilityEvent) {
        when (e.eventType) {
            AccessibilityEvent.TYPE_VIEW_SCROLLED -> scrolled.incrementAndGet()
            AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED -> text.incrementAndGet()
            AccessibilityEvent.TYPE_WINDOWS_CHANGED -> windows.incrementAndGet()
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED -> {
                windows.incrementAndGet()
                e.className?.toString()?.takeIf { it.contains('.') && !it.startsWith("android.widget") }?.let { lastWindowClass = it }
            }
        }
        lastEventAt = System.currentTimeMillis()
        _tick.value = any.incrementAndGet()
    }
}
