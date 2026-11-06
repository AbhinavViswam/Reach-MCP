package app.reach.android.tools

import app.reach.android.access.Access
import app.reach.android.mcp.Content
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.arr
import app.reach.android.mcp.bool
import app.reach.android.mcp.int
import app.reach.android.mcp.long
import app.reach.android.mcp.obj
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.privacy.Privacy
import app.reach.android.screen.Actor
import app.reach.android.screen.Element
import app.reach.android.screen.Ids
import app.reach.android.screen.Ocr
import app.reach.android.screen.Vision
import app.reach.android.screen.Role
import app.reach.android.screen.Verdict
import app.reach.android.screen.ViewText
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonObject

// ================================================================================================ observe

class ObserveTool : Tool {
    override val name = "observe"
    override val description = "See the phone screen. Default: compact text, one element per line with a stable id " +
        "(e.g. e4k2) to use in act; when an app shows almost nothing readable (Flutter, games), text is read from the " +
        "screen (OCR) and added as tappable lines marked (ocr). since=<version> returns only what changed. " +
        "mode=ocr always adds OCR text; mode=marks returns a screenshot with numbered boxes plus the list of ids; " +
        "mode=screenshot returns a JPEG (region zooms in). Prefer the text view: images cost far more tokens."
    override val inputSchema = schema {
        prop("mode", "string", "tree (default), ocr, marks or screenshot", enum = listOf("tree", "ocr", "marks", "screenshot"))
        prop("since", "integer", "A view version (the vN in an earlier reply): return only the changes after it")
        prop("bounds", "boolean", "Add each element's centre point @x,y (screen pixels)")
        prop("all_windows", "boolean", "Also read other app windows (split screen, picture-in-picture)")
        prop("region", "object", "screenshot: zoom into {x, y, width, height} in screen pixels (sharper small text)")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val mode = args.str("mode") ?: "tree"
        val live = Screen.read(args.bool("all_windows") == true, if (mode == "ocr") Screen.OcrMode.ON else Screen.OcrMode.AUTO)
        if (mode != "screenshot" && mode != "marks") {
            return ToolResult.text(Screen.render(live.snap, args.int("since"), args.bool("bounds") == true))
        }
        if (live.snap.blockedApp != null) throw ToolError("blocked_app", "${live.snap.blockedApp} is blocked by Reach privacy rules; no screenshot")
        val full = Actor.capture()
        try {
            if (mode == "marks") {
                val marked = Vision.markable(live.snap.elements, live.snap.width.toLong() * live.snap.height)
                val drawn = Ocr.marks(full, marked)
                val shot = try { Actor.encode(drawn) } finally { drawn.recycle() }
                Screen.lastShot = shot
                val list = marked.mapIndexed { i, e -> "${i + 1} = ${ViewText.line(e)}" }.joinToString("\n")
                val text = "${ViewText.header(live.snap)}\nmarks: box number = element (act with its id)\n${ViewText.FENCE}\n$list"
                return ToolResult(listOf(Content.Text(text), Content.Image(shot.base64, "image/jpeg", shot.width, shot.height)))
            }
            val region = args.obj("region")?.let { r ->
                val x = r.int("x") ?: 0
                val y = r.int("y") ?: 0
                app.reach.android.screen.Box(x, y, x + (r.int("width") ?: 0), y + (r.int("height") ?: 0))
            }
            val shot = Actor.encode(full, region)
            Screen.lastShot = shot
            val area = region?.let { "region ${it.left},${it.top} ${it.width}×${it.height}" } ?: "${live.snap.width}×${live.snap.height}"
            val hint = if (shot.scale < 1f || region != null) " · to tap a point from this image use target {\"x\":…, \"y\":…, \"in\":\"screenshot\"}" else ""
            return ToolResult(listOf(
                Content.Text("${ViewText.header(live.snap)}\nscreenshot ${shot.width}×${shot.height} of $area$hint"),
                Content.Image(shot.base64, "image/jpeg", shot.width, shot.height),
            ))
        } finally {
            full.recycle()
        }
    }
}

// ================================================================================================ act

private val ACTIONS = listOf("tap", "long_press", "double_tap", "type", "clear", "scroll", "swipe", "drag", "key")

private val ACT_SCHEMA = schema {
    prop("action", "string", "What to do", enum = ACTIONS)
    any("target", "Element id string (\"e4k2\"), or {\"text\": \"Wi-Fi\"}, or {\"x\": 540, \"y\": 1200} in screen pixels " +
        "(add \"in\": \"screenshot\" for screenshot pixels). type: optional (focused box). scroll: optional (biggest list). key: not used")
    prop("text", "string", "type: the text to enter")
    prop("mode", "string", "type: replace (default) or append", enum = listOf("replace", "append"))
    prop("submit", "boolean", "type: press the keyboard's enter/search after typing")
    prop("direction", "string", "scroll/swipe: which way to move the content view; down = show what's further down",
        enum = listOf("up", "down", "left", "right"))
    any("to", "drag/swipe: where to end, same forms as target")
    prop("key", "string", "key: ${Actor.KEYS.joinToString()}", enum = Actor.KEYS)
    prop("duration_ms", "integer", "swipe/drag: how long the move takes (default 350)")
    required("action")
}

/** One act, used by act and act_batch. */
private suspend fun actOnce(args: JsonObject): Screen.Outcome {
    val action = args.str("action") ?: throw ToolError("bad_args", "action is needed: ${ACTIONS.joinToString()}")
    val live = Screen.read()
    when (action) {
        "tap", "long_press" -> {
            val t = Screen.resolve(live, args["target"])
            val long = action == "long_press"
            return Screen.checked(action, live, t, "${if (long) "long-press" else "tap"} ${t.describe()}") {
                Actor.tap(t.node, t.x, t.y, long)
            }
        }
        "double_tap" -> {
            val t = Screen.resolve(live, args["target"])
            return Screen.checked(action, live, t, "double-tap ${t.describe()}", gesture = true) { Actor.doubleTap(t.x, t.y) }
        }
        "type", "clear" -> {
            val text = if (action == "clear") "" else args.str("text") ?: throw ToolError("bad_args", "type needs text")
            val append = args.str("mode") == "append"
            val t = args["target"]?.let { Screen.resolve(live, it) }
            if (t?.element?.role == Role.PASSWORD) throw ToolError("blocked", "Reach does not type into password fields; ask the user")
            // A search "box" that is really a button: press it, then use the box that opens.
            var field = t?.node?.let(Actor::editableIn) ?: if (t == null) Actor.focusedField() else null
            var opened = false
            if (field == null && t != null) {
                Actor.tap(t.node, t.x, t.y)
                repeat(10) { if (field == null) { kotlinx.coroutines.delay(250); field = Actor.focusedField() } }
                opened = true
            }
            val box = field ?: throw ToolError("not_found", "no text box found${t?.let { " at ${it.describe()}" } ?: " with focus"}; tap a field first")
            val expectId = t?.element?.takeIf { !opened && it.role == Role.FIELD }?.id
            val expect = when {
                expectId == null -> null
                append -> t!!.element!!.value.orEmpty() + text
                else -> text
            }
            val what = (if (action == "clear") "clear " else "type \"${text.take(40)}\" into ") +
                (t?.describe() ?: "the focused box") + if (opened) " (opened its text box)" else ""
            return Screen.checked("type", live, t, what, expectText = expect) {
                Actor.type(box, text, append, args.bool("submit") == true)
            }
        }
        "scroll" -> {
            val dir = dir(args.str("direction") ?: "down")
            val t = args["target"]?.let { Screen.resolve(live, it) }
            val node = t?.node ?: live.snap.elements.filter { it.raw.scrollable }.maxByOrNull { it.bounds.area }?.let { live.nodes[it.id] }
            val area = t?.element?.bounds ?: app.reach.android.screen.Box(0, 0, live.snap.width, live.snap.height)
            return Screen.checked("scroll", live, null, "scroll ${dir.name.lowercase()}${t?.let { " in " + it.describe() } ?: ""}") {
                Actor.scroll(node, area, dir)
            }
        }
        "swipe", "drag" -> {
            val from = args["target"]?.let { Screen.resolve(live, it) }
            val ms = (args.long("duration_ms") ?: 350L).coerceIn(50, 5_000)
            val (x1, y1, x2, y2) = if (args["to"] != null) {
                val to = Screen.resolve(live, args["to"])
                val f = from ?: throw ToolError("bad_args", "$action with 'to' also needs a target to start from")
                listOf(f.x, f.y, to.x, to.y)
            } else {
                val d = dir(args.str("direction") ?: throw ToolError("bad_args", "$action needs 'to' or 'direction'"))
                val cx = from?.x ?: (live.snap.width / 2)
                val cy = from?.y ?: (live.snap.height / 2)
                val dx = live.snap.width / 3
                val dy = live.snap.height / 4
                // Swipe direction is the finger's: "up" moves the finger up (shows what's further down).
                when (d) {
                    Actor.Dir.UP -> listOf(cx, cy + dy / 2, cx, cy - dy / 2)
                    Actor.Dir.DOWN -> listOf(cx, cy - dy / 2, cx, cy + dy / 2)
                    Actor.Dir.LEFT -> listOf(cx + dx / 2, cy, cx - dx / 2, cy)
                    Actor.Dir.RIGHT -> listOf(cx - dx / 2, cy, cx + dx / 2, cy)
                }
            }
            val what = "$action from $x1,$y1 to $x2,$y2"
            return Screen.checked(action, live, from, what, gesture = true) {
                if (action == "drag") Actor.drag(x1.toFloat(), y1.toFloat(), x2.toFloat(), y2.toFloat(), ms)
                else Actor.swipe(x1.toFloat(), y1.toFloat(), x2.toFloat(), y2.toFloat(), ms)
            }
        }
        "key" -> {
            val k = args.str("key") ?: throw ToolError("bad_args", "key needs key: ${Actor.KEYS.joinToString()}")
            if (k !in Actor.KEYS) throw ToolError("not_supported", "unknown key '$k'; keys: ${Actor.KEYS.joinToString()}")
            return Screen.checked("key", live, null, "key $k") { Actor.key(k) }
        }
        else -> throw ToolError("bad_args", "unknown action '$action'; actions: ${ACTIONS.joinToString()}")
    }
}

private fun dir(s: String) = when (s.lowercase()) {
    "up" -> Actor.Dir.UP
    "down" -> Actor.Dir.DOWN
    "left" -> Actor.Dir.LEFT
    "right" -> Actor.Dir.RIGHT
    else -> throw ToolError("bad_args", "direction must be up, down, left or right")
}

class ActTool : Tool {
    override val name = "act"
    override val description = "Do one thing on the phone: tap, long_press, double_tap, type, clear, scroll, swipe, drag, key " +
        "(back, home, recents, enter…). Every reply starts with a checked verdict (done / no_effect / blocked_by_popup / unknown) " +
        "and lists what changed on screen, so there is no need to observe again after 'done'."
    override val inputSchema = ACT_SCHEMA
    override suspend fun call(args: JsonObject): ToolResult = ToolResult.text(actOnce(args).text)
}

class ActBatchTool : Tool {
    override val name = "act_batch"
    override val description = "Several act steps in one call (same arguments as act, in a list). Stops at the first step " +
        "that is not 'done' and says which one."
    override val timeoutMs = 90_000L
    override val inputSchema = schema {
        prop("actions", "array", "List of act arguments, e.g. [{\"action\":\"tap\",\"target\":\"e4k2\"},{\"action\":\"type\",\"text\":\"hi\",\"submit\":true}]") {
            put("items", ACT_SCHEMA)
        }
        required("actions")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val steps = args.arr("actions")?.map { it as? JsonObject ?: throw ToolError("bad_args", "each action must be an object") }
            ?: throw ToolError("bad_args", "actions list is needed")
        if (steps.isEmpty()) throw ToolError("bad_args", "actions list is empty")
        if (steps.size > 20) throw ToolError("bad_args", "at most 20 steps per batch")
        val first = Screen.read().snap
        val lines = ArrayList<String>()
        var last: Screen.Outcome? = null
        for ((i, step) in steps.withIndex()) {
            val o = try {
                if (app.reach.android.Settings.paused.value) throw ToolError("paused", app.reach.android.privacy.Approvals.PAUSED_HINT)
                actOnce(step)
            } catch (e: ToolError) {
                lines += "step ${i + 1}: error ${e.code} · ${e.hint}"
                lines += "stopped; steps ${i + 2}–${steps.size} not run"
                break
            }
            last = o
            lines += "step ${i + 1}: " + o.text.lineSequence().first().removePrefix("verdict: ")
            if (o.verdict != Verdict.DONE) {
                if (i + 1 < steps.size) lines += "stopped; steps ${i + 2}–${steps.size} not run"
                break
            }
        }
        val now = last?.after ?: Screen.read().snap
        lines += Screen.render(now, first.version)
        return ToolResult.text(lines.joinToString("\n"))
    }
}

// ================================================================================================ wait_for

class WaitForTool : Tool {
    override val name = "wait_for"
    override val description = "Wait until something is true on screen, driven by screen events (no fixed sleeps): " +
        "text appears, text is gone, an element id is there, an app is in front, or the screen is idle."
    override val timeoutMs = 35_000L
    override val inputSchema = schema {
        prop("text", "string", "Wait until an element with this text appears")
        prop("gone", "string", "Wait until no element shows this text (a spinner, 'Loading…')")
        prop("id", "string", "Wait until this element id is on screen")
        prop("app", "string", "Wait until this package is in front")
        prop("idle", "boolean", "Wait until the screen stops changing")
        prop("idle_ms", "integer", "How long without changes counts as idle (default 800)")
        prop("timeout_ms", "integer", "Give up after this long (default 5000, max 30000)")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val timeout = (args.long("timeout_ms") ?: 5_000L).coerceIn(100, 30_000)
        val text = args.str("text")?.let(Ids::normalize)
        val gone = args.str("gone")?.let(Ids::normalize)
        val id = args.str("id")
        val app = args.str("app")
        val idle = args.bool("idle") == true
        val idleMs = (args.long("idle_ms") ?: 800L).coerceIn(100, 10_000)
        if (text == null && gone == null && id == null && app == null && !idle) {
            throw ToolError("bad_args", "give one of text, gone, id, app, idle")
        }
        val start = System.currentTimeMillis()
        val before = Screen.state.latest()?.version
        fun met(els: List<Element>, pkg: String?): Boolean {
            if (text != null && els.none { Ids.normalize(it.label).contains(text) || Ids.normalize(it.value.orEmpty()).contains(text) }) return false
            if (gone != null && els.any { Ids.normalize(it.label).contains(gone) }) return false
            if (id != null && els.none { it.id == id }) return false
            if (app != null && pkg != app) return false
            if (idle && System.currentTimeMillis() - Access.lastEventAt < idleMs) return false
            return true
        }
        while (true) {
            val tick = Access.tick.value
            val live = Screen.read()
            if (met(live.snap.elements, live.snap.app)) {
                val s = (System.currentTimeMillis() - start) / 100 / 10.0
                return ToolResult.text("met after $s s\n" + Screen.render(live.snap, before))
            }
            val left = timeout - (System.currentTimeMillis() - start)
            if (left <= 0) {
                return ToolResult.text("verdict: timeout · not met after ${timeout / 1000.0} s\n" + Screen.render(live.snap, before))
            }
            // Next screen event, or a short tick for idle checks and apps that send none.
            withTimeoutOrNull(minOf(left, if (idle) 150L else 500L)) { Access.tick.first { it != tick } }
            kotlinx.coroutines.delay(60)
        }
    }
}

// ================================================================================================ find

class FindTool : Tool {
    override val name = "find"
    override val description = "Find elements by text, role or resource id. scroll=true also scrolls the biggest list " +
        "to look further down (e.g. find 'About phone' in Settings)."
    override val timeoutMs = 45_000L
    override val inputSchema = schema {
        prop("text", "string", "Words in the label or value (case does not matter)")
        prop("role", "string", "button, field, switch, item, text, …")
        prop("resource_id", "string", "Android resource id, e.g. 'search_bar'")
        prop("scroll", "boolean", "Scroll down the main list until found (up to 10 scrolls)")
        prop("limit", "integer", "Most matches to return (default 20)")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val text = args.str("text")?.let(Ids::normalize)
        val role = args.str("role")?.lowercase()
        val rid = args.str("resource_id")
        val limit = (args.int("limit") ?: 20).coerceIn(1, 100)
        if (text == null && role == null && rid == null) throw ToolError("bad_args", "give text, role or resource_id")
        fun match(e: Element) = (text == null || Ids.normalize(e.label).contains(text) || Ids.normalize(e.value.orEmpty()).contains(text)) &&
            (role == null || e.role.word == role) && (rid == null || e.raw.resourceId == rid)

        var live = Screen.read()
        var scrolls = 0
        if (args.bool("scroll") == true) {
            while (live.snap.elements.none(::match) && scrolls < 10 && live.snap.blockedApp == null) {
                val list = live.snap.elements.filter { it.raw.scrollable }.maxByOrNull { it.bounds.area } ?: break
                val sig = live.snap.signature
                val started = System.currentTimeMillis()
                if (!Actor.scroll(live.nodes[list.id], list.bounds, Actor.Dir.DOWN)) break
                Actor.settle(started)
                scrolls++
                live = Screen.read()
                if (live.snap.signature == sig) break // end of the list
            }
        }
        val hits = live.snap.elements.filter(::match)
        val head = ViewText.header(live.snap) + (if (scrolls > 0) " · scrolled $scrolls×" else "")
        if (hits.isEmpty()) return ToolResult.text("$head\nno match" + if (args.bool("scroll") != true) " (scroll=true looks further down)" else "")
        return ToolResult.text("$head\n${ViewText.FENCE}\n" + hits.take(limit).joinToString("\n") { ViewText.line(it, bounds = true) } +
            if (hits.size > limit) "\n… ${hits.size - limit} more" else "")
    }
}

internal fun blockedNote(pkg: String?) = if (Privacy.isBlocked(pkg)) " (blocked by Reach privacy rules)" else ""
