package app.reach.android.tools

import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.arr
import app.reach.android.mcp.bool
import app.reach.android.mcp.int
import app.reach.android.mcp.obj
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * The flows tool (spec/CONTRACT.md §5.2), same as Reach for Windows: record the agent's successful calls, save them
 * in the app's files (flows/NAME.json), replay step by step with no model in the loop. A step whose element id has
 * changed is found again by its words (and the flow is updated); the first step that goes wrong stops the run and
 * hands the screen back. [call]: runs one tool as the agent would (pause, switches, activity log).
 */
class FlowsTool(
    private val dir: File,
    private val call: suspend (String, JsonObject) -> ToolResult,
) : Tool {
    override val name = "flows"
    override val description =
        "Saved tasks that replay with no model calls. record_start name=…, do the task (act, apps, wait_for, sms…), then " +
            "record_stop with inputs={\"name\": \"a value you typed\"} for the parts that change. run name=… inputs={…} replays it: " +
            "steps whose ids changed are found again by their words; the first step that goes wrong stops the run and shows the screen, " +
            "then from_step=N finishes it. Also list, show, save (write steps yourself), delete."
    override val inputSchema = schema {
        prop("action", "string", "what to do", listOf("list", "show", "run", "record_start", "record_stop", "save", "delete"))
        prop("name", "string", "the flow's name, e.g. morning_news")
        prop("description", "string", "record_start/record_stop/save: what the flow does")
        any("inputs", "record_stop: {input: example value seen in the steps}; run: {input: value}")
        prop("from_step", "integer", "run: start at this step (after fixing a stopped run)")
        prop("discard", "boolean", "record_stop: throw the recording away")
        any("steps", "save: [{\"tool\": \"act\", \"args\": {…}}], {{input}} in values")
        required("action")
    }
    override val timeoutMs = 600_000L

    private class Recording(val name: String, val description: String) { val steps = ArrayList<JsonObject>() }

    @Volatile private var rec: Recording? = null
    private val pretty = Json { prettyPrint = true }

    /** Told about every call the agent makes; adds it to the recording if one is on. */
    fun saw(tool: String, args: JsonObject, r: ToolResult) {
        val rc = rec ?: return
        synchronized(rc) { Flow.stepsOf(tool, args, r).forEach { if (rc.steps.size < Flow.MAX_STEPS) rc.steps += it } }
    }

    private fun now() = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.US).format(Date())

    override suspend fun call(args: JsonObject): ToolResult = when (args.str("action") ?: "list") {
        "record_start" -> {
            val n = Flow.cleanName(args.str("name"))
            rec = Recording(n, args.str("description").orEmpty())
            ToolResult.text("recording flow \"$n\": do the task now (act, apps, wait_for, …); observe/find are not recorded. " +
                "Then flows record_stop, with inputs={\"name\": \"example value you typed\"} for parts that change each time.")
        }
        "record_stop" -> {
            val r = rec ?: throw ToolError("not_recording", "no recording is on; flows record_start name=… first")
            rec = null
            if (args.bool("discard") == true) ToolResult.text("recording \"${r.name}\" thrown away")
            else {
                val steps = synchronized(r) { r.steps.toList() }
                if (steps.isEmpty()) throw ToolError("empty", "nothing was recorded (only successful act/apps/wait_for/… calls count)")
                val (withSlots, inputs) = Flow.makeInputs(steps, args.obj("inputs"))
                val flow = flowJson(r.name, args.str("description") ?: r.description, inputs, withSlots)
                save(flow)
                ToolResult.text("saved flow \"${r.name}\" · ${steps.size} steps" +
                    (if (inputs.isNotEmpty()) " · inputs ${inputs.keys.joinToString()}" else "") + "\n" + show(flow))
            }
        }
        "save" -> {
            val n = Flow.cleanName(args.str("name"))
            val steps = args.arr("steps") ?: throw ToolError("bad_args", "save needs steps: [{\"tool\": \"act\", \"args\": {…}}]")
            val list = steps.map { s ->
                val o = s as? JsonObject
                val t = o?.str("tool")
                if (o == null || t == null || o["args"] !is JsonObject || t in Flow.NOT_RECORDED)
                    throw ToolError("bad_args", "each step is {\"tool\": name, \"args\": {…}} and can't be observe/find/device/flows")
                o
            }
            val flow = flowJson(n, args.str("description").orEmpty(), args.obj("inputs") ?: JsonObject(emptyMap()), list)
            save(flow)
            ToolResult.text("saved flow \"$n\" · ${list.size} steps\n" + show(flow))
        }
        "list" -> {
            val rows = all().map { f ->
                val inputs = f.obj("inputs")?.keys.orEmpty()
                "${f.str("name")} · ${f.arr("steps")?.size ?: 0} steps" + (if (inputs.isNotEmpty()) " · inputs ${inputs.joinToString()}" else "") +
                    (f.str("description")?.takeIf { it.isNotEmpty() }?.let { " · $it" } ?: "")
            }
            val now = rec?.let { "\n(recording \"${it.name}\" now, ${it.steps.size} steps so far)" }.orEmpty()
            ToolResult.text((if (rows.isEmpty()) "no flows saved yet; flows record_start name=… to make one" else rows.joinToString("\n")) + now)
        }
        "show" -> ToolResult.text(show(load(Flow.cleanName(args.str("name")))))
        "delete" -> {
            val n = Flow.cleanName(args.str("name"))
            if (!file(n).delete()) throw ToolError("not_found", "no flow \"$n\"")
            ToolResult.text("deleted flow \"$n\"")
        }
        "run" -> run(load(Flow.cleanName(args.str("name"))), args.obj("inputs"), args.int("from_step") ?: 1)
        else -> throw ToolError("bad_args", "flows actions: list, show, run, record_start, record_stop, save, delete")
    }

    private suspend fun run(flow: JsonObject, given: JsonObject?, from: Int): ToolResult {
        val name = flow.str("name")
        val steps = (flow.arr("steps") ?: JsonArray(emptyList())).map { it as JsonObject }.toMutableList()
        val defaults = flow.obj("inputs") ?: JsonObject(emptyMap())
        if (from < 1 || from > steps.size) throw ToolError("bad_args", "from_step must be 1–${steps.size}")
        val lines = ArrayList<String>()
        var healed = false
        var last: ToolResult? = null
        for (i in from - 1 until steps.size) {
            val step = steps[i]
            val tool = step.str("tool")!!
            val args = Flow.fill(step.obj("args")!!, defaults, given)
            var note = ""
            val act = Flow.actArgs(tool, args)
            val t = act?.obj("target")
            val id = t?.str("id")
            val r: ToolResult
            if (act != null && id != null) {
                // The recorded id first; if it's gone, the element's words.
                var res = call(tool, Flow.withAct(tool, args, JsonObject(act + ("target" to JsonPrimitive(id)))))
                val words = t.str("text")
                if (Flow.errorCode(res) in setOf("stale_id", "not_found") && words != null) {
                    res = call(tool, Flow.withAct(tool, args, JsonObject(act + ("target" to buildJsonObject { put("text", words) }))))
                    if (!res.isError) {
                        note = " (found again by \"$words\")"
                        val savedArgs = step.obj("args")!!
                        val savedAct = Flow.actArgs(tool, savedArgs)
                        Flow.idIn(Flow.firstText(res), words)?.takeIf { savedAct != null }?.let { newId ->
                            val newAct = JsonObject(savedAct!! + ("target" to buildJsonObject { put("id", newId); put("text", words) }))
                            steps[i] = JsonObject(step + ("args" to Flow.withAct(tool, savedArgs, newAct)))
                            healed = true
                        }
                    }
                }
                r = res
            } else r = call(tool, args)
            last = r
            val first = Flow.firstText(r).lineSequence().first()
            if (!Flow.good(step, r)) {
                if (healed) save(JsonObject(flow + ("steps" to JsonArray(steps))))
                val screen = call("observe", JsonObject(emptyMap()))
                return ToolResult.text("flow \"$name\" stopped at step ${i + 1}/${steps.size} (${step.str("what")}): $first\n" +
                    (if (lines.isNotEmpty()) lines.joinToString("\n") + "\n" else "") +
                    "take over from here; when the screen is right, flows run name=$name from_step=${i + 2} finishes the rest.\n" +
                    (if (screen.isError) "" else Flow.firstText(screen)))
            }
            lines += "${i + 1} ok · ${first.removePrefix("verdict: ").take(120)}$note"
        }
        if (healed) save(JsonObject(flow + ("steps" to JsonArray(steps))))
        val body = last?.let { Flow.firstText(it) }.orEmpty()
        val rest = body.substringAfter('\n', "")
        return ToolResult.text("verdict: done · flow \"$name\" ran ${steps.size - from + 1} steps with no model calls" +
            (if (healed) " (updated ids saved)" else "") + "\n" + lines.joinToString("\n") +
            (if (rest.isNotEmpty()) "\nlast step:\n" + rest.take(2_000) else ""))
    }

    private fun flowJson(n: String, description: String, inputs: JsonObject, steps: List<JsonObject>) = buildJsonObject {
        put("name", n); put("description", description); put("platform", "android"); put("created", now())
        put("inputs", inputs); put("steps", JsonArray(steps))
    }

    private fun show(f: JsonObject): String {
        val inputs = f.obj("inputs")
        val steps = f.arr("steps").orEmpty().mapIndexed { i, s ->
            val o = s as JsonObject
            "${i + 1}. ${o.str("tool")} ${o["args"].toString().take(160)}"
        }
        return "flow ${f.str("name")}" + (f.str("description")?.takeIf { it.isNotEmpty() }?.let { " · $it" } ?: "") + "\n" +
            (if (!inputs.isNullOrEmpty()) "inputs: " + inputs.entries.joinToString { (k, v) -> "$k (e.g. $v)" } + "\n" else "") +
            steps.joinToString("\n")
    }

    private fun file(n: String) = File(dir, "$n.json")

    private fun save(flow: JsonObject) {
        dir.mkdirs()
        file(flow.str("name")!!).writeText(pretty.encodeToString(JsonObject.serializer(), flow))
    }

    private fun load(n: String): JsonObject {
        val f = file(n)
        if (!f.exists()) throw ToolError("not_found", "no flow \"$n\"; saved: ${all().joinToString { it.str("name").orEmpty() }}")
        return runCatching { Json.parseToJsonElement(f.readText()) as JsonObject }.getOrElse { throw ToolError("broken", "$n.json can't be read") }
    }

    private fun all(): List<JsonObject> =
        (dir.listFiles { f -> f.extension == "json" } ?: emptyArray()).sortedBy { it.name }
            .mapNotNull { runCatching { Json.parseToJsonElement(it.readText()) as JsonObject }.getOrNull() }
}
