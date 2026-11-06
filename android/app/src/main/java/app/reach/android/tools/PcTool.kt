package app.reach.android.tools

import app.reach.android.mcp.Content
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.obj
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL
import java.util.concurrent.atomic.AtomicLong

/**
 * Talks MCP to another Reach (Reach for Windows, reach.exe --http): initialize once, then tools/list and tools/call
 * over POST /mcp with its token. Plain java.net, tested on the PC (LinkTest).
 */
class McpClient(url: String, private val token: String) {
    val url = url.trimEnd('/')
    private var session: String? = null
    @Volatile private var ready = false
    private val ids = AtomicLong()

    private suspend fun rpc(method: String, params: JsonObject?, timeoutMs: Int): JsonObject = withContext(Dispatchers.IO) {
        val body = buildJsonObject {
            put("jsonrpc", "2.0"); put("id", ids.incrementAndGet()); put("method", method)
            if (params != null) put("params", params)
        }.toString().toByteArray()
        val c = try {
            (URL("$url/mcp").openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                doOutput = true
                connectTimeout = 5_000
                readTimeout = timeoutMs
                setRequestProperty("Content-Type", "application/json")
                setRequestProperty("Accept", "application/json, text/event-stream")
                setRequestProperty("Authorization", "Bearer $token")
                session?.let { setRequestProperty("Mcp-Session-Id", it) }
                outputStream.use { it.write(body) }
            }
        } catch (e: java.io.IOException) {
            throw ToolError("unreachable", "can't reach $url (${e.message}); is reach.exe --http running there, and for USB is adb reverse set up?")
        }
        val code = try { c.responseCode } catch (e: java.net.SocketTimeoutException) {
            throw ToolError("timeout", "the PC didn't answer in ${timeoutMs / 1000} s")
        } catch (e: java.io.IOException) {
            throw ToolError("unreachable", "can't reach $url (${e.message})")
        }
        if (code == 401) throw ToolError("bad_token", "$url refused the token; copy it again (reach.exe --token, or Reach settings › Copy HTTP token)")
        if (code !in 200..299) throw ToolError("unreachable", "$url answered $code")
        c.getHeaderField("Mcp-Session-Id")?.let { session = it }
        val text = c.inputStream.bufferedReader().use { it.readText() }
        val o = runCatching { Json.parseToJsonElement(text) as JsonObject }.getOrNull() ?: throw ToolError("bad_reply", "the PC sent something that isn't JSON")
        (o["error"] as? JsonObject)?.let { throw ToolError("remote_error", it.str("message") ?: "error") }
        o["result"] as? JsonObject ?: JsonObject(emptyMap())
    }

    private suspend fun ensureReady() {
        if (ready) return
        rpc("initialize", buildJsonObject {
            put("protocolVersion", "2025-06-18")
            put("capabilities", JsonObject(emptyMap()))
            put("clientInfo", buildJsonObject { put("name", "reach-android"); put("version", "0") })
        }, 10_000)
        ready = true
    }

    suspend fun tools(): List<Pair<String, String>> {
        ensureReady()
        val r = rpc("tools/list", null, 10_000)
        return (r["tools"] as? JsonArray).orEmpty().mapNotNull { t -> (t as? JsonObject)?.let { (it.str("name") ?: "") to (it.str("description") ?: "") } }
    }

    suspend fun call(tool: String, args: JsonObject, timeoutMs: Int): ToolResult {
        ensureReady()
        val r = try {
            rpc("tools/call", buildJsonObject { put("name", tool); put("arguments", args) }, timeoutMs)
        } catch (e: ToolError) {
            if (e.code == "remote_error" || e.code == "bad_reply") ready = false
            throw e
        }
        val content = (r["content"] as? JsonArray).orEmpty().mapNotNull { c ->
            val o = c as? JsonObject ?: return@mapNotNull null
            if (o.str("type") == "image") Content.Image(o.str("data").orEmpty(), o.str("mimeType") ?: "image/jpeg")
            else Content.Text(o.str("text").orEmpty())
        }
        return ToolResult(content, (r["isError"] as? JsonPrimitive)?.content == "true")
    }
}

/** Who may reach the phone's server in Tailscale mode: this phone, or a device on your Tailscale network. */
object Net {
    fun localOrTailscale(a: InetAddress): Boolean {
        if (a.isLoopbackAddress) return true
        val b = a.address
        if (b.size == 4) return (b[0].toInt() and 0xff) == 100 && (b[1].toInt() and 0xC0) == 64
        if (b.size == 16) {
            // IPv4 inside IPv6 (::ffff:100.x.y.z)
            if ((0 until 10).all { b[it].toInt() == 0 } && (b[10].toInt() and 0xff) == 0xff && (b[11].toInt() and 0xff) == 0xff)
                return (b[12].toInt() and 0xff) == 100 && (b[13].toInt() and 0xC0) == 64
            val p = listOf(0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0)
            return p.indices.all { (b[it].toInt() and 0xff) == p[it] }
        }
        return false
    }
}

/**
 * The pc tool (spec/CONTRACT.md §6.3): an agent connected to this phone also uses the PC (Reach for Windows over
 * HTTP). The PC's own rules apply there (its switches, app rules, approvals on the PC). [link]: the PC's address and
 * token as the user set them in the app, read on every call.
 */
class PcTool(private val link: () -> Pair<String, String>) : Tool {
    override val name = "pc"
    override val description =
        "Use your PC (Reach for Windows) from here: tool = a PC tool (observe, act, apps, office, browser, files…), args = its " +
            "arguments. tool=tools lists the PC's tools. The PC's own rules apply (its approvals ask on the PC)."
    override val inputSchema = schema {
        prop("tool", "string", "the PC tool to run, or tools to list them")
        any("args", "that tool's arguments")
        required("tool")
    }
    override val timeoutMs = 600_000L

    private var client: McpClient? = null

    private fun client(): McpClient {
        val (url, token) = link()
        if (token.isBlank()) throw ToolError("no_pc", "no PC is linked; the user adds it in the Reach app (Your PC)")
        val c = client
        if (c != null && c.url == url.trimEnd('/')) return c
        return McpClient(url, token).also { client = it }
    }

    override suspend fun call(args: JsonObject): ToolResult {
        val c = client()
        val tool = args.str("tool") ?: "tools"
        if (tool == "tools" || tool == "list") {
            val tools = c.tools()
            return ToolResult.text("PC tools — call pc tool=<name> args={…}:\n" + tools.joinToString("\n") { (n, d) ->
                val i = d.indexOf(". ")
                "$n: " + if (i in 1..159) d.take(i + 1) else d.take(160)
            })
        }
        val timeout = when (tool) { "flows" -> 590_000; "wait_for", "shell", "office" -> 170_000; else -> 60_000 }
        val r = c.call(tool, args.obj("args") ?: JsonObject(emptyMap()), timeout)
        // Which device answered goes at the end of the first line, so replies still start with their verdict.
        val first = r.content.firstOrNull() as? Content.Text ?: return r
        val t = first.text
        val nl = t.indexOf('\n')
        val tagged = if (nl < 0) "$t · on the PC" else t.substring(0, nl) + " · on the PC" + t.substring(nl)
        return r.copy(content = listOf(Content.Text(tagged)) + r.content.drop(1))
    }
}
