package app.reach.android.screen

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ClipData
import android.content.ClipboardManager
import android.graphics.Bitmap
import android.graphics.Path
import android.graphics.Rect
import android.os.Bundle
import android.util.Base64
import android.view.Display
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import app.reach.android.access.Access
import app.reach.android.mcp.ToolError
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import kotlin.coroutines.resume

/**
 * Doing things on screen. Views are pressed through accessibility actions first (works behind the keyboard, needs
 * no exact position); a finger gesture is the fallback. Gestures need no adb, so Xiaomi/HyperOS's INJECT_EVENTS
 * block does not apply.
 */
object Actor {

    // -------------------------------------------------------------------------------------------- waiting

    /**
     * Waits until the screen has settled after an action that started at [startedAt]: events have gone quiet for
     * [quietMs], or nothing has happened at all for 500 ms, at most [maxMs].
     */
    suspend fun settle(startedAt: Long, quietMs: Long = 300, maxMs: Long = 3_000) {
        delay(120)
        while (true) {
            val now = System.currentTimeMillis()
            if (now - startedAt >= maxMs) return
            val last = Access.lastEventAt
            if (last < startedAt) {
                if (now - startedAt >= 500) return
            } else if (now - last >= quietMs) {
                return
            }
            delay(40)
        }
    }

    // -------------------------------------------------------------------------------------------- popups

    /**
     * A window drawn over [target] (a dialog, a chat head, a system prompt): the action would land on it, so the
     * agent is told instead (verdict blocked_by_popup). The keyboard counts only for finger gestures.
     */
    fun coveringWindow(target: Element?, x: Int, y: Int, gesture: Boolean): AccessibilityWindowInfo? {
        val s = Access.service.value ?: return null
        val windows = runCatching { s.windows }.getOrDefault(emptyList())
        val own = target?.raw?.windowId?.let { id -> windows.firstOrNull { it.id == id } }
            ?: windows.firstOrNull { w -> Rect().also(w::getBoundsInScreen).contains(x, y) && w.type == AccessibilityWindowInfo.TYPE_APPLICATION }
            ?: return null
        val (sw, sh) = ScreenReader.screenSize(s)
        return windows.firstOrNull { w ->
            if (w.id == own.id || w.layer <= own.layer) return@firstOrNull false
            val b = Rect().also(w::getBoundsInScreen)
            if (!b.contains(x, y)) return@firstOrNull false
            when (w.type) {
                AccessibilityWindowInfo.TYPE_APPLICATION -> true
                AccessibilityWindowInfo.TYPE_SYSTEM -> {
                    val pkg = w.root?.packageName?.toString()
                    !(pkg == "com.android.systemui" && (b.height() < sh / 6 || b.width() < sw / 6))
                }
                AccessibilityWindowInfo.TYPE_INPUT_METHOD -> gesture
                else -> false
            }
        }
    }

    // -------------------------------------------------------------------------------------------- pressing

    /** Presses the view (or the nearest pressable thing around it); a finger on its middle when that fails. */
    suspend fun tap(node: AccessibilityNodeInfo?, x: Int, y: Int, long: Boolean = false): Boolean {
        if (node != null && node.refresh()) {
            val action = if (long) AccessibilityNodeInfo.ACTION_LONG_CLICK else AccessibilityNodeInfo.ACTION_CLICK
            var n: AccessibilityNodeInfo? = node
            var hops = 0
            while (n != null && hops < 5) {
                if ((if (long) n.isLongClickable else n.isClickable) && n.isEnabled && n.performAction(action)) return true
                n = n.parent
                hops++
            }
        }
        return gesture(listOf(Stroke(x.toFloat(), y.toFloat(), x.toFloat(), y.toFloat(), if (long) 700 else 60)))
    }

    suspend fun doubleTap(x: Int, y: Int): Boolean {
        val fx = x.toFloat()
        val fy = y.toFloat()
        return gesture(listOf(Stroke(fx, fy, fx, fy, 50, startMs = 0), Stroke(fx, fy, fx, fy, 50, startMs = 120)))
    }

    // -------------------------------------------------------------------------------------------- typing

    /**
     * Puts [text] in [field]. replace = what's there is replaced (the default, so text is never appended by
     * accident); append = added at the end. Fields that ignore "set text" get it pasted instead, and the user's
     * clipboard is put back afterwards.
     */
    suspend fun type(field: AccessibilityNodeInfo, text: String, append: Boolean, submit: Boolean): Boolean {
        if (!field.refresh()) return false
        if (field.isPassword) throw ToolError("blocked", "Reach does not type into password fields; ask the user to enter it")
        field.performAction(AccessibilityNodeInfo.ACTION_FOCUS)
        val now = field.text?.toString()?.takeUnless { it == field.hintText?.toString() }.orEmpty()
        val wanted = if (append) now + text else text
        val args = Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, wanted) }
        if (!field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) {
            if (!append) clearByKeys(field)
            if (!paste(field, text)) return false
        }
        if (submit) {
            delay(150)
            return field.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        }
        return true
    }

    private fun clearByKeys(field: AccessibilityNodeInfo) {
        val len = field.text?.length ?: 0
        if (len == 0) return
        val sel = Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, 0)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, len)
        }
        field.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, sel)
        field.performAction(AccessibilityNodeInfo.ACTION_CUT)
    }

    private suspend fun paste(node: AccessibilityNodeInfo, text: String): Boolean {
        val s = Access.service.value ?: return false
        val cm = s.getSystemService(ClipboardManager::class.java) ?: return false
        return withContext(Dispatchers.Main) {
            val saved = runCatching { cm.primaryClip }.getOrNull()
            val ok = runCatching {
                cm.setPrimaryClip(ClipData.newPlainText("Reach", text))
                node.performAction(AccessibilityNodeInfo.ACTION_FOCUS)
                node.performAction(AccessibilityNodeInfo.ACTION_PASTE)
            }.getOrDefault(false)
            delay(150)
            if (saved != null) runCatching { cm.setPrimaryClip(saved) }
            ok
        }
    }

    /** The focused text box, else the first one on screen. */
    fun focusedField(): AccessibilityNodeInfo? {
        val s = Access.service.value ?: return null
        val root = s.rootInActiveWindow ?: return null
        root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)?.takeIf { it.isEditable && it.isVisibleToUser }?.let { return it }
        val queue = ArrayDeque<Pair<AccessibilityNodeInfo, Int>>().apply { add(root to 0) }
        while (queue.isNotEmpty()) {
            val (n, d) = queue.removeFirst()
            if (n.isEditable && n.isVisibleToUser) return n
            if (d < 40) for (i in 0 until n.childCount) n.getChild(i)?.let { queue.add(it to d + 1) }
        }
        return null
    }

    /** A text box in or under [n] (a search "box" is often a layout around the real EditText). */
    fun editableIn(n: AccessibilityNodeInfo, depth: Int = 0): AccessibilityNodeInfo? {
        if (n.isEditable) return n
        if (depth > 4) return null
        for (i in 0 until n.childCount) n.getChild(i)?.let { c -> editableIn(c, depth + 1)?.let { return it } }
        return null
    }

    // -------------------------------------------------------------------------------------------- scrolling

    enum class Dir { UP, DOWN, LEFT, RIGHT }

    /** "down" = show what's further down, as a person means it. A swipe when the list won't scroll itself. */
    suspend fun scroll(target: AccessibilityNodeInfo?, area: Box, dir: Dir): Boolean {
        val list = target?.let(::scrollableAround)
        if (list != null) {
            val action = when (dir) {
                Dir.DOWN -> AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_DOWN
                Dir.UP -> AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_UP
                Dir.RIGHT -> AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_RIGHT
                Dir.LEFT -> AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_LEFT
            }
            if (list.performAction(action.id)) return true
            val generic = if (dir == Dir.DOWN || dir == Dir.RIGHT) AccessibilityNodeInfo.ACTION_SCROLL_FORWARD else AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD
            if (list.performAction(generic)) return true
        }
        val box = list?.let { Rect().also(it::getBoundsInScreen).toBox() }?.takeUnless { it.isEmpty } ?: area
        val cx = box.cx.toFloat()
        val cy = box.cy.toFloat()
        val dy = box.height * 0.3f
        val dx = box.width * 0.3f
        return when (dir) {
            Dir.DOWN -> swipe(cx, cy + dy, cx, cy - dy, 350)
            Dir.UP -> swipe(cx, cy - dy, cx, cy + dy, 350)
            Dir.RIGHT -> swipe(cx + dx, cy, cx - dx, cy, 300)
            Dir.LEFT -> swipe(cx - dx, cy, cx + dx, cy, 300)
        }
    }

    fun scrollableAround(n: AccessibilityNodeInfo): AccessibilityNodeInfo? {
        var c: AccessibilityNodeInfo? = n
        var hops = 0
        while (c != null && hops < 8) {
            if (c.isScrollable) return c
            c = c.parent
            hops++
        }
        return null
    }

    suspend fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long) = gesture(listOf(Stroke(x1, y1, x2, y2, ms)))

    /** Press and hold at the start, then move: what launchers and lists need to pick something up. */
    suspend fun drag(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long): Boolean {
        val hold = GestureDescription.StrokeDescription(Path().apply { moveTo(x1, y1); lineTo(x1, y1) }, 0, 650, true)
        if (!dispatch(GestureDescription.Builder().addStroke(hold).build())) return false
        val move = hold.continueStroke(Path().apply { moveTo(x1, y1); lineTo(x2, y2) }, 0, ms.coerceAtLeast(200), false)
        return dispatch(GestureDescription.Builder().addStroke(move).build())
    }

    // -------------------------------------------------------------------------------------------- keys

    val KEYS = listOf("back", "home", "recents", "notifications", "quick_settings", "enter", "backspace", "lock", "power_menu", "screenshot")

    fun key(name: String): Boolean {
        val s = Access.service.value ?: return false
        val global = when (name) {
            "back" -> AccessibilityService.GLOBAL_ACTION_BACK
            "home" -> AccessibilityService.GLOBAL_ACTION_HOME
            "recents" -> AccessibilityService.GLOBAL_ACTION_RECENTS
            "notifications" -> AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS
            "quick_settings" -> AccessibilityService.GLOBAL_ACTION_QUICK_SETTINGS
            "lock" -> AccessibilityService.GLOBAL_ACTION_LOCK_SCREEN
            "power_menu" -> AccessibilityService.GLOBAL_ACTION_POWER_DIALOG
            "screenshot" -> AccessibilityService.GLOBAL_ACTION_TAKE_SCREENSHOT
            else -> null
        }
        if (global != null) return s.performGlobalAction(global)
        val field = focusedField() ?: throw ToolError("not_found", "no text box has focus; tap one first")
        return when (name) {
            "enter" -> field.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
            "backspace" -> {
                val t = field.text?.toString().orEmpty()
                if (t.isEmpty()) false
                else field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply {
                    putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, t.dropLast(1))
                })
            }
            else -> throw ToolError("not_supported", "unknown key '$name'; keys: ${KEYS.joinToString()}")
        }
    }

    // -------------------------------------------------------------------------------------------- gestures

    private class Stroke(val x1: Float, val y1: Float, val x2: Float, val y2: Float, val ms: Long, val startMs: Long = 0)

    private suspend fun gesture(strokes: List<Stroke>): Boolean {
        val b = GestureDescription.Builder()
        strokes.forEach { st ->
            val path = Path().apply {
                moveTo(st.x1.coerceAtLeast(1f), st.y1.coerceAtLeast(1f))
                lineTo(st.x2.coerceAtLeast(1f), st.y2.coerceAtLeast(1f))
            }
            b.addStroke(GestureDescription.StrokeDescription(path, st.startMs, st.ms.coerceAtLeast(1)))
        }
        return dispatch(b.build())
    }

    private suspend fun dispatch(g: GestureDescription): Boolean {
        val s = Access.service.value ?: return false
        return withContext(Dispatchers.Main) {
            suspendCancellableCoroutine { cont ->
                val ok = s.dispatchGesture(g, object : AccessibilityService.GestureResultCallback() {
                    override fun onCompleted(d: GestureDescription?) { if (cont.isActive) cont.resume(true) }
                    override fun onCancelled(d: GestureDescription?) { if (cont.isActive) cont.resume(false) }
                }, null)
                if (!ok && cont.isActive) cont.resume(false)
            }
        }
    }

    // -------------------------------------------------------------------------------------------- screenshots

    class Shot(val base64: String, val width: Int, val height: Int, val scale: Float, val originX: Int = 0, val originY: Int = 0)

    /** The whole screen at full size (software bitmap: ML Kit and Canvas can use it). The caller recycles it. */
    suspend fun capture(): Bitmap {
        val s = ScreenReader.service()
        for (attempt in 0..1) {
            val r = capture(s)
            if (r.first != null) return r.first!!
            when (r.second) {
                AccessibilityService.ERROR_TAKE_SCREENSHOT_INTERVAL_TIME_SHORT -> delay(400)
                AccessibilityService.ERROR_TAKE_SCREENSHOT_SECURE_WINDOW ->
                    throw ToolError("blocked_app", "this screen is marked secure by its app; it can't be captured or read by OCR")
                else -> throw ToolError("internal", "Android refused the screenshot (code ${r.second})")
            }
        }
        throw ToolError("internal", "Android refused the screenshot twice; try again")
    }

    /**
     * A JPEG of [full] (or of [region] of it, for zooming in) whose long side is at most [maxSide] px, which fits
     * every client's image limits. Does not recycle [full].
     */
    fun encode(full: Bitmap, region: Box? = null, maxSide: Int = 1280, quality: Int = 70): Shot {
        val r = region?.let {
            Box(it.left.coerceIn(0, full.width - 1), it.top.coerceIn(0, full.height - 1), it.right.coerceIn(1, full.width), it.bottom.coerceIn(1, full.height))
        }?.takeUnless { it.isEmpty }
        val src = if (r != null) Bitmap.createBitmap(full, r.left, r.top, r.width, r.height) else full
        val scale = minOf(1f, maxSide.toFloat() / maxOf(src.width, src.height))
        val out = if (scale < 1f) Bitmap.createScaledBitmap(src, (src.width * scale).toInt().coerceAtLeast(1), (src.height * scale).toInt().coerceAtLeast(1), true) else src
        val bytes = ByteArrayOutputStream().also { out.compress(Bitmap.CompressFormat.JPEG, quality, it) }.toByteArray()
        val shot = Shot(Base64.encodeToString(bytes, Base64.NO_WRAP), out.width, out.height, scale, r?.left ?: 0, r?.top ?: 0)
        if (out !== src) out.recycle()
        if (src !== full) src.recycle()
        return shot
    }

    private suspend fun capture(s: AccessibilityService): Pair<Bitmap?, Int> = suspendCancellableCoroutine { cont ->
        s.takeScreenshot(Display.DEFAULT_DISPLAY, s.mainExecutor, object : AccessibilityService.TakeScreenshotCallback {
            override fun onSuccess(result: AccessibilityService.ScreenshotResult) {
                val hw = Bitmap.wrapHardwareBuffer(result.hardwareBuffer, result.colorSpace)
                val soft = hw?.copy(Bitmap.Config.ARGB_8888, false)
                hw?.recycle()
                result.hardwareBuffer.close()
                if (cont.isActive) cont.resume(soft to 0)
            }
            override fun onFailure(errorCode: Int) { if (cont.isActive) cont.resume(null to errorCode) }
        })
    }
}
