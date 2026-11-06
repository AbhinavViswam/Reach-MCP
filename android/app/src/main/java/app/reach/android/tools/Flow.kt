package app.reach.android.tools

import app.reach.android.mcp.Content
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.str
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/**
 * Flows (spec/CONTRACT.md §5.2): a task done once, saved as steps, replayed without the model. The same file format
 * and rules as Reach for Windows' Core/Flow.cs. Plain Kotlin, tested on the PC (FlowTest); FlowsTool runs them.
 */
object Flow {
    /** Calls that read or manage, never part of a flow. */
    val NOT_RECORDED = setOf("observe", "find", "device", "doctor", "flows", "undo")
    const val MAX_STEPS = 100

    private val idLabel = Regex("""\b(e[0-9a-z]{3,5}(?:-\d+)?) "([^"]*)"""")
    private val nameOk = Regex("^[a-z0-9][a-z0-9_-]{0,47}$")
    private val slot = Regex("""\{\{(\w+)\}\}""")

    fun cleanName(name: String?): String {
        val n = (name ?: "").trim().lowercase().replace(Regex("[^a-z0-9_-]+"), "_").trim('_')
        if (!nameOk.matches(n)) throw ToolError("bad_args", "give the flow a short name, e.g. send_report (letters, digits, _ and -)")
        return n
    }

    fun firstText(r: ToolResult): String = (r.content.firstOrNull() as? Content.Text)?.text.orEmpty()

    fun verdictOf(firstLine: String): String? {
        if (!firstLine.startsWith("verdict: ")) return null
        return firstLine.removePrefix("verdict: ").substringBefore(' ')
    }

    fun idIn(reply: String, words: String): String? =
        idLabel.findAll(reply.lineSequence().first()).firstOrNull { it.groupValues[2] == words }?.groupValues?.get(1)

    /** The steps one successful call adds to a recording: ids get their label, act_batch becomes single acts. */
    fun stepsOf(tool: String, args: JsonObject, r: ToolResult): List<JsonObject> {
        if (r.isError || tool in NOT_RECORDED) return emptyList()
        // Another device's tool (pc on the phone, phone on the PC): the same rules for its inner call.
        if ((tool == "pc" || tool == "phone") && args.str("tool").let { it == null || it == "tools" || it == "list" || it in NOT_RECORDED }) return emptyList()
        val text = firstText(r)
        val acts = args["actions"] as? JsonArray
        if (tool == "act_batch" && acts != null) {
            val lines = text.lines().filter { it.startsWith("step ") }
            val out = ArrayList<JsonObject>()
            for (i in acts.indices) {
                if (i >= lines.size) break
                val one = acts[i] as? JsonObject ?: continue
                val line = lines[i]
                if (line.contains(": error ")) break
                val what = line.substringAfter(": ")
                out += step("act", one, verdictOf("verdict: $what"), what)
            }
            return out
        }
        val first = text.lineSequence().first()
        return listOf(step(tool, args, verdictOf(first), first.removePrefix("verdict: ")))
    }

    /** The act arguments inside a step: the step's own for act, the inner ones for pc/phone tool=act. */
    fun actArgs(tool: String, args: JsonObject): JsonObject? = when {
        tool == "act" -> args
        (tool == "pc" || tool == "phone") && args.str("tool") == "act" -> args["args"] as? JsonObject
        else -> null
    }

    /** [args] with the act arguments inside replaced by [act]. */
    fun withAct(tool: String, args: JsonObject, act: JsonObject): JsonObject =
        if (tool == "act") act else JsonObject(args + ("args" to act))

    private fun step(tool: String, args: JsonObject, verdict: String?, what: String): JsonObject {
        var a = args
        val act = actArgs(tool, args)
        val id = (act?.get("target") as? JsonPrimitive)?.takeIf { it.isString }?.content
        if (act != null && id != null) {
            val label = idLabel.findAll(what).firstOrNull { it.groupValues[1] == id }?.groupValues?.get(2)
            a = withAct(tool, args, JsonObject(act + ("target" to buildJsonObject { put("id", id); if (!label.isNullOrEmpty()) put("text", label) })))
        }
        return buildJsonObject {
            put("tool", tool)
            put("args", a)
            put("what", what.take(160))
            if (verdict != null) put("verdict", verdict)
        }
    }

    /** Every string argument containing an input's example value gets {{name}} instead. Returns the inputs found. */
    fun makeInputs(steps: List<JsonObject>, wanted: JsonObject?): Pair<List<JsonObject>, JsonObject> {
        var out = steps
        val found = LinkedHashMap<String, JsonElement>()
        wanted?.forEach { (name, v) ->
            val example = (v as? JsonPrimitive)?.takeIf { it.isString }?.content ?: return@forEach
            if (example.isEmpty()) return@forEach
            var hit = false
            out = out.map { s ->
                val a = s["args"] as? JsonObject ?: return@map s
                val (na, h) = replace(a, example, "{{$name}}")
                hit = hit || h
                JsonObject(s + ("args" to na))
            }
            if (hit) found[name] = JsonPrimitive(example)
        }
        return out to JsonObject(found)
    }

    private fun replace(e: JsonElement, example: String, slotText: String, key: String? = null): Pair<JsonElement, Boolean> = when (e) {
        is JsonObject -> {
            var hit = false
            val m = e.mapValues { (k, v) -> val (nv, h) = replace(v, example, slotText, k); hit = hit || h; nv }
            JsonObject(m) to hit
        }
        is JsonArray -> {
            var hit = false
            val l = e.map { v -> val (nv, h) = replace(v, example, slotText); hit = hit || h; nv }
            JsonArray(l) to hit
        }
        is JsonPrimitive -> if (e.isString && key != "id" && e.content.contains(example)) JsonPrimitive(e.content.replace(example, slotText)) to true else e to false
        else -> e to false
    }

    /** The step's arguments with {{name}} filled from [given], else the flow's example value. */
    fun fill(args: JsonObject, defaults: JsonObject, given: JsonObject?): JsonObject {
        val missing = ArrayList<String>()
        val filled = slot.replace(args.toString()) { m ->
            val n = m.groupValues[1]
            val v = given?.let { g -> (g[n] as? JsonPrimitive)?.content ?: g[n]?.toString() } ?: (defaults[n] as? JsonPrimitive)?.content
            // Inside a JSON string: escaped the JSON way, without the quotes. (The lambda's text is used as is.)
            if (v == null) { missing += n; m.value } else JsonPrimitive(v).toString().drop(1).dropLast(1)
        }
        if (missing.isNotEmpty()) throw ToolError("bad_args", "the flow needs inputs: ${missing.distinct().joinToString()}")
        return Json.parseToJsonElement(filled) as JsonObject
    }

    /** Errors and popups are never as recorded; no_effect only if the recording had it too (or the step is optional). */
    fun good(step: JsonObject, r: ToolResult): Boolean {
        if (r.isError) return false
        val now = verdictOf(firstText(r).lineSequence().first()) ?: return true
        if (now == "done" || now == "unknown") return true
        if (now == "blocked_by_popup" || now == "needs_foreground") return false
        return step.str("verdict") == now || (step["optional"] as? JsonPrimitive)?.boolean == true
    }

    fun errorCode(r: ToolResult): String = firstText(r).let { if (it.startsWith("error: ")) it.removePrefix("error: ").substringBefore(' ') else "" }
}
