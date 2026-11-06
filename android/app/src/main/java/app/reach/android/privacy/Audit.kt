package app.reach.android.privacy

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * The activity log (spec/POLICY.md): every call, its result and every approval, one JSON line each in the app's
 * private files (audit.jsonl). Stays on the phone; shown in the app. Codes are masked and long text is cut, so the
 * log never holds more than the agent saw. Same fields as Reach for Windows' audit.jsonl.
 */
object Audit {
    @Volatile private var file: File? = null
    private const val MAX_BYTES = 2L * 1024 * 1024

    fun init(dir: File) { file = File(dir, "audit.jsonl") }

    data class Entry(val time: String, val kind: String, val tool: String, val args: String, val ok: Boolean, val result: String, val ms: Long)

    private fun now() = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())

    fun call(tool: String, args: JsonObject, ok: Boolean, firstLine: String, ms: Long, via: String = "http") = write(buildJsonObject {
        put("t", now()); put("kind", "call"); put("tool", tool); put("args", summary(args)); put("ok", ok)
        put("result", clip(Mask.apply(firstLine).text, 200)); put("ms", ms); put("via", via)
    })

    fun approval(what: String, detail: String, allowed: Boolean, from: String) = write(buildJsonObject {
        put("t", now()); put("kind", "approval"); put("tool", what); put("args", clip(Mask.apply(detail.replace('\n', ' ')).text, 300))
        put("ok", allowed); put("result", (if (allowed) "allowed" else "denied") + " · asked by $from")
    })

    fun note(what: String) = write(buildJsonObject { put("t", now()); put("kind", "note"); put("tool", what); put("ok", true) })

    /** The arguments in short: each value cut to 120 characters, codes hidden. */
    fun summary(args: JsonObject): String {
        val s = args.entries.joinToString(" ") { (k, v) ->
            val text = if (v is JsonPrimitive && v.isString) "\"" + clip(v.content, 120) + "\"" else clip(v.toString(), 120)
            "$k=$text"
        }
        return clip(Mask.apply(s).text, 400)
    }

    private fun clip(s: String, n: Int) = if (s.length <= n) s else s.take(n - 1) + "…"

    @Synchronized
    private fun write(line: JsonObject) {
        val f = file ?: return
        runCatching {
            if (f.exists() && f.length() > MAX_BYTES) f.renameTo(File(f.parentFile, "audit.1.jsonl"))
            f.appendText(line.toString() + "\n")
        }
    }

    /** The newest [max] entries, newest first. */
    @Synchronized
    fun read(max: Int = 300): List<Entry> {
        val f = file ?: return emptyList()
        if (!f.exists()) return emptyList()
        return runCatching { f.readLines() }.getOrDefault(emptyList()).asReversed().asSequence().take(max).mapNotNull { l ->
            runCatching {
                val o = Json.parseToJsonElement(l) as JsonObject
                fun s(k: String) = (o[k] as? JsonPrimitive)?.content.orEmpty()
                Entry(s("t"), s("kind"), s("tool"), s("args"), o["ok"]?.jsonPrimitive?.boolean ?: false, s("result"),
                    (o["ms"] as? JsonElement)?.jsonPrimitive?.long ?: 0)
            }.getOrNull()
        }.toList()
    }

    @Synchronized
    fun clear() {
        val f = file ?: return
        f.delete()
        File(f.parentFile, "audit.1.jsonl").delete()
    }
}
