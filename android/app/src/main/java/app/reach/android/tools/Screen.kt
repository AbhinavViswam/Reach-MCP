package app.reach.android.tools

import app.reach.android.Settings
import app.reach.android.privacy.Approvals
import app.reach.android.privacy.Privacy
import android.view.accessibility.AccessibilityNodeInfo
import app.reach.android.access.Access
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.int
import app.reach.android.mcp.str
import app.reach.android.screen.Actor
import app.reach.android.screen.Diff
import app.reach.android.screen.Element
import app.reach.android.screen.Ids
import app.reach.android.screen.Live
import app.reach.android.screen.Ocr
import app.reach.android.screen.Vision
import app.reach.android.screen.ScreenReader
import app.reach.android.screen.ScreenState
import app.reach.android.screen.Snapshot
import app.reach.android.screen.Verdict
import app.reach.android.screen.Verdicts
import app.reach.android.screen.ViewText
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** What the core tools share: the stored views, reading, finding targets, and running one checked action. */
object Screen {
    val state = ScreenState()

    /** The last screenshot sent, so {x, y, in: "screenshot"} points can be turned into screen pixels. */
    @Volatile var lastShot: Actor.Shot? = null

    enum class OcrMode { AUTO, ON }

    /**
     * The app whose last look needed OCR. Later reads there include it too, so the OCR elements' IDs keep working
     * for act; it is dropped when another app comes to the front with a readable tree.
     */
    @Volatile private var ocrApp: String? = null

    /**
     * Reads the screen and stores it; the returned live view carries its version. OCR is added when asked
     * ([OcrMode.ON]), when the tree has too little in it (Flutter, games, canvas), or when this app needed it before.
     */
    suspend fun read(allWindows: Boolean = false, ocr: OcrMode = OcrMode.AUTO): Live {
        val tree = ScreenReader.read(allWindows)
        val s = tree.snap
        val thin = Vision.thin(s.elements)
        val want = s.blockedApp == null && (ocr == OcrMode.ON || thin || (ocrApp != null && ocrApp == s.app))
        if (want) ocrApp = s.app else if (ocrApp != s.app) ocrApp = null
        val live = if (want) withOcr(tree) else tree
        return Live(state.store(live.snap), live.nodes)
    }

    private suspend fun withOcr(tree: Live): Live {
        val full = try { Actor.capture() } catch (e: ToolError) { return tree }
        val lines = try { Ocr.lines(full) } finally { full.recycle() }
        val extra = Vision.extra(tree.snap.elements, lines)
        val elements = if (extra.isEmpty()) tree.snap.elements else Ids.assign(tree.snap.app, tree.snap.elements.map { it.raw } + extra)
        return Live(tree.snap.copy(elements = elements, ocr = true), tree.nodes)
    }

    /** Whole view, or only the changes since [since] when that's shorter. */
    fun render(now: Snapshot, since: Int?, bounds: Boolean = false): String {
        val base = since?.let { state.get(it) } ?: return ViewText.full(now, bounds)
        if (base.version == now.version) return "${ViewText.header(now)}\nno changes since v$since"
        val d = Diff.of(base, now)
        return if (d.mostlyNew) ViewText.full(now, bounds) else "${ViewText.header(now)}\n${ViewText.FENCE}\n${d.text(bounds)}"
    }

    // -------------------------------------------------------------------------------------------- targets

    class Target(val element: Element?, val node: AccessibilityNodeInfo?, val x: Int, val y: Int, val note: String?) {
        fun describe(): String = element?.let { "${it.id} \"${it.label}\"" } ?: "point $x,$y"
    }

    /**
     * An ID ("e4k2"), {"id": …}, {"text": …} (exact first, then contains) or {"x", "y"} (screen pixels, or
     * screenshot pixels with "in": "screenshot").
     */
    fun resolve(live: Live, target: JsonElement?): Target {
        when (target) {
            null -> throw ToolError("bad_args", "target is needed: an element id like \"e4k2\", {\"text\": \"…\"} or {\"x\": …, \"y\": …}")
            is JsonPrimitive -> return byId(live, target.content)
            is JsonObject -> {
                target.str("id")?.let { return byId(live, it) }
                target.str("text")?.let { return byText(live, it) }
                val x = target.int("x")
                val y = target.int("y")
                if (x != null && y != null) {
                    val shot = if (target.str("in") == "screenshot") lastShot ?: throw ToolError("bad_args", "no screenshot taken yet") else null
                    val px = if (shot != null) shot.originX + (x / shot.scale).toInt() else x
                    val py = if (shot != null) shot.originY + (y / shot.scale).toInt() else y
                    val under = live.snap.elements.filter { it.bounds.contains(px, py) }.minByOrNull { it.bounds.area }
                    return Target(under, under?.let { live.nodes[it.id] }, px, py, null)
                }
                throw ToolError("bad_args", "target object needs id, text, or x and y")
            }
            else -> throw ToolError("bad_args", "target must be an id string or an object")
        }
    }

    private fun byId(live: Live, id: String): Target {
        live.snap.byId[id]?.let { return Target(it, live.nodes[id], it.bounds.cx, it.bounds.cy, null) }
        val old = state.knownElement(id)
            ?: throw ToolError("stale_id", "$id was never seen; call observe for current ids")
        val now = Ids.refind(old, live.snap)
            ?: throw ToolError("stale_id", "$id (${old.role.word} \"${old.label}\") is not on screen any more; observe again")
        return Target(now, live.nodes[now.id], now.bounds.cx, now.bounds.cy, "refound $id → ${now.id}")
    }

    private fun byText(live: Live, text: String): Target {
        val t = Ids.normalize(text)
        val els = live.snap.elements
        val exact = els.filter { Ids.normalize(it.label) == t }
        val pool = exact.ifEmpty { els.filter { Ids.normalize(it.label).contains(t) } }
        if (pool.isEmpty()) throw ToolError("not_found", "nothing labelled \"$text\" on screen; try find with scroll=true")
        // Something that can be pressed beats plain text with the same words.
        val best = pool.firstOrNull { it.role.pressable } ?: pool.first()
        val note = if (pool.size > 1) "${pool.size} matches for \"$text\", used ${best.id}" else null
        return Target(best, live.nodes[best.id], best.bounds.cx, best.bounds.cy, note)
    }

    // -------------------------------------------------------------------------------------------- checked actions

    class Outcome(val verdict: Verdict, val text: String, val after: Snapshot)

    /**
     * Runs [perform] and judges it (spec/CONTRACT.md §4): waits for the screen to settle, reads it again, and
     * reports the verdict and what changed. [expectText]: for typing, what the field should hold.
     */
    suspend fun checked(
        kind: String,
        before: Live,
        target: Target?,
        what: String,
        gesture: Boolean = false,
        expectText: String? = null,
        perform: suspend () -> Boolean,
    ): Outcome {
        if (before.snap.blockedApp != null) throw ToolError("blocked_app", "${before.snap.blockedApp} is blocked by Reach privacy rules")
        // Read-only apps: reading more (scroll) and leaving (back, home…) are fine; nothing else.
        val leaving = kind == "key" && what.removePrefix("key ") in NAV_KEYS || what.startsWith("go home")
        if (kind != "scroll" && !leaving) Privacy.requireCanAct(before.snap.app)
        if (Settings.paused.value) throw ToolError("paused", Approvals.PAUSED_HINT)
        if (target != null) {
            Actor.coveringWindow(target.element, target.x, target.y, gesture = gesture || target.node == null)?.let { w ->
                val title = w.title?.toString() ?: w.root?.packageName?.toString() ?: "a window"
                val text = "verdict: blocked_by_popup · $what is covered by \"$title\"; nothing was done\n" +
                    "close or answer it first (key back, or tap one of its buttons):\n" + render(before.snap, null)
                return Outcome(Verdict.BLOCKED_BY_POPUP, text, before.snap)
            }
        }
        val counts = Access.counts()
        val started = System.currentTimeMillis()
        val ok = perform()
        if (!ok) throw ToolError("refused", "Android refused to $what; observe and try another way (e.g. a point target)")
        Actor.settle(started)
        val after = read()
        val diff = Diff.of(before.snap, after.snap)
        val verdict = Verdicts.judge(kind, diff, Access.counts() - counts, target?.element?.id, expectText)
        val body = when {
            diff.mostlyNew -> ViewText.full(after.snap)
            else -> "${ViewText.FENCE}\n${diff.text()}"
        }
        val head = buildString {
            append("verdict: ").append(verdict.word).append(" · ").append(what)
            target?.note?.let { append(" (").append(it).append(')') }
        }
        return Outcome(verdict, "$head\n$body", after.snap)
    }

    private val NAV_KEYS = setOf("back", "home", "recents", "notifications", "quick_settings", "lock")
}
