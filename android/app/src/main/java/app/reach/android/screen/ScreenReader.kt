package app.reach.android.screen

import android.accessibilityservice.AccessibilityService
import android.graphics.Rect
import android.hardware.display.DisplayManager
import android.view.Display
import android.view.WindowManager
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import app.reach.android.access.Access
import app.reach.android.mcp.ToolError
import app.reach.android.privacy.Privacy
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.withContext
import java.util.concurrent.Executors

/** A read of the screen, plus the live views behind each ID (to act on them). */
class Live(val snap: Snapshot, val nodes: Map<String, AccessibilityNodeInfo>)

/**
 * Reads the screen through the accessibility service. All screen work runs on one thread, one call at a time, so
 * two agent calls can never tap over each other.
 */
object ScreenReader {
    val thread = Executors.newSingleThreadExecutor { r -> Thread(r, "reach-screen").apply { isDaemon = true } }
        .asCoroutineDispatcher()

    private const val MAX_ELEMENTS = 250
    private const val MAX_DEPTH = 50
    const val MAX_LABEL = 120

    fun service(): AccessibilityService =
        Access.service.value ?: throw ToolError("no_access", Access.offHint(Access.app))

    suspend fun read(allWindows: Boolean = false): Live = withContext(thread) {
        val s = service()
        val (w, h) = screenSize(s)
        val windows = runCatching { s.windows }.getOrDefault(emptyList())
        val keyboard = windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
        val root = s.rootInActiveWindow
        val app = root?.packageName?.toString()
        if (Privacy.isBlocked(app)) {
            return@withContext Live(Snapshot(app, emptyList(), w, h, w > h, keyboard, blockedApp = app), emptyMap())
        }

        val raws = ArrayList<RawElement>()
        val nodes = ArrayList<AccessibilityNodeInfo>()
        if (root != null) walk(root, raws, nodes, 0, root.windowId, w, h)

        val activeWindow = windows.firstOrNull { it.id == root?.windowId }
        val overlays = ArrayList<Overlay>()
        for (win in windows) {
            if (win.id == root?.windowId) continue
            val isApp = win.type == AccessibilityWindowInfo.TYPE_APPLICATION
            val isSystem = win.type == AccessibilityWindowInfo.TYPE_SYSTEM
            if (!isApp && !isSystem) continue
            val wr = win.root ?: continue
            val pkg = wr.packageName?.toString()
            val b = Rect().also(win::getBoundsInScreen).toBox()
            val above = activeWindow == null || win.layer > activeWindow.layer
            if (isSystemBar(pkg, b, w, h)) continue
            if (Privacy.isBlocked(pkg)) continue
            if (above) overlays += Overlay(win.id, pkg, b, win.title?.toString())
            // Windows over the app (chat heads, system prompts) are always read; other app windows on request.
            if (above || (allWindows && isApp)) walk(wr, raws, nodes, 0, win.id, w, h)
        }

        val elements = Ids.assign(app, raws)
        val map = HashMap<String, AccessibilityNodeInfo>(elements.size)
        elements.forEachIndexed { i, e -> map[e.id] = nodes[i] }
        Live(Snapshot(app, elements, w, h, w > h, keyboard, overlays = overlays), map)
    }

    /** Status and navigation bars are system windows too: thin strips along an edge, never popups. */
    private fun isSystemBar(pkg: String?, b: Box, w: Int, h: Int): Boolean =
        pkg == "com.android.systemui" && (b.height < h / 6 || b.width < w / 6)

    fun screenSize(s: AccessibilityService): Pair<Int, Int> {
        runCatching {
            val wm = s.getSystemService(WindowManager::class.java)
            val r = wm.maximumWindowMetrics.bounds
            if (r.width() > 0) return r.width() to r.height()
        }
        @Suppress("DEPRECATION")
        runCatching {
            val d = s.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
            val p = android.graphics.Point()
            d.getRealSize(p)
            if (p.x > 0) return p.x to p.y
        }
        val m = s.resources.displayMetrics
        return m.widthPixels to m.heightPixels
    }

    // -------------------------------------------------------------------------------------------- walking the tree

    private fun walk(
        n: AccessibilityNodeInfo, out: MutableList<RawElement>, nodes: MutableList<AccessibilityNodeInfo>,
        depth: Int, windowId: Int, w: Int, h: Int, pressableOnly: Boolean = false,
    ) {
        if (out.size >= MAX_ELEMENTS || depth > MAX_DEPTH || !n.isVisibleToUser) return
        val role = roleOf(n)
        if (role != null && !(pressableOnly && !role.pressable && role != Role.FIELD)) {
            val b = Rect().also(n::getBoundsInScreen).let {
                Box(it.left.coerceIn(0, w), it.top.coerceIn(0, h), it.right.coerceIn(0, w), it.bottom.coerceIn(0, h))
            }
            if (!b.isEmpty) {
                out += raw(n, role, b, windowId)
                nodes += n
                // A button's own words are its label: inside it, only other pressable things get their own line.
                if (role != Role.TEXT && role != Role.LIST) {
                    for (i in 0 until n.childCount) n.getChild(i)?.let { walk(it, out, nodes, depth + 1, windowId, w, h, pressableOnly = true) }
                    return
                }
            }
        }
        for (i in 0 until n.childCount) n.getChild(i)?.let { walk(it, out, nodes, depth + 1, windowId, w, h, pressableOnly) }
    }

    private fun raw(n: AccessibilityNodeInfo, role: Role, b: Box, windowId: Int): RawElement {
        val password = n.isPassword
        val label = when (role) {
            Role.FIELD, Role.PASSWORD -> n.hintText?.toString()?.takeIf { it.isNotBlank() }
                ?: n.contentDescription?.toString()
                ?: labelFor(n)
                ?: ""
            else -> ownWords(n).ifBlank { innerWords(n) }
        }.clean()
        val value = when {
            password -> null // never read (spec/POLICY.md)
            role == Role.FIELD -> n.text?.toString()?.takeUnless { it == n.hintText?.toString() }.orEmpty().take(500)
            n.rangeInfo != null -> n.rangeInfo!!.current.let { if (it == it.toInt().toFloat()) it.toInt().toString() else "%.2f".format(it) }
            else -> null
        }
        return RawElement(
            role = if (password) Role.PASSWORD else role,
            label = label,
            value = value,
            resourceId = n.viewIdResourceName?.substringAfter(":id/"),
            className = n.className?.toString().orEmpty(),
            bounds = b,
            checked = if (n.isCheckable) n.isChecked else null,
            selected = n.isSelected,
            enabled = n.isEnabled,
            focused = n.isFocused && (role == Role.FIELD || password),
            scrollable = n.isScrollable,
            windowId = windowId,
        )
    }

    private fun roleOf(n: AccessibilityNodeInfo): Role? {
        val cls = n.className?.toString().orEmpty()
        return when {
            n.isPassword -> Role.PASSWORD
            n.isEditable || cls.endsWith("EditText") -> Role.FIELD
            cls.endsWith("Switch") || cls.endsWith("SwitchCompat") || cls.endsWith("ToggleButton") -> Role.SWITCH
            cls.endsWith("CheckBox") -> Role.CHECKBOX
            cls.endsWith("RadioButton") -> Role.RADIO
            cls.endsWith("Tab") || cls.endsWith("TabView") -> Role.TAB
            cls.endsWith("SeekBar") || n.rangeInfo != null -> Role.ITEM
            n.isScrollable && !n.isClickable -> Role.LIST
            cls.endsWith("Button") || cls.endsWith("ImageButton") -> Role.BUTTON
            n.isCheckable -> Role.CHECKBOX
            n.isClickable || n.isLongClickable -> Role.ITEM
            cls.endsWith("ImageView") && !n.contentDescription.isNullOrBlank() -> Role.IMAGE
            !n.text.isNullOrBlank() || !n.contentDescription.isNullOrBlank() -> Role.TEXT
            else -> null
        }
    }

    private fun ownWords(n: AccessibilityNodeInfo): String =
        n.text?.toString()?.takeIf { it.isNotBlank() } ?: n.contentDescription?.toString().orEmpty()

    private fun labelFor(n: AccessibilityNodeInfo): String? =
        runCatching { n.labeledBy?.let { ownWords(it) } }.getOrNull()?.takeIf { it.isNotBlank() }

    /** The words inside a pressable thing with none of its own (a list row: name · last message). */
    private fun innerWords(n: AccessibilityNodeInfo, out: StringBuilder = StringBuilder(), depth: Int = 0): String {
        if (depth > 6 || out.length > MAX_LABEL) return out.toString()
        for (i in 0 until n.childCount) {
            val c = n.getChild(i) ?: continue
            if (!c.isVisibleToUser || c.isPassword) continue
            val words = ownWords(c)
            if (words.isNotBlank()) {
                if (out.isNotEmpty()) out.append(" · ")
                out.append(words)
            }
            innerWords(c, out, depth + 1)
        }
        return out.toString()
    }

    private fun String.clean() = replace(Regex("\\s+"), " ").trim().take(MAX_LABEL)
}

fun Rect.toBox() = Box(left, top, right, bottom)
