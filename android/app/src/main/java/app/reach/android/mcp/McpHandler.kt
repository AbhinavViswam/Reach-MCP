package app.reach.android.mcp

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * MCP over JSON-RPC 2.0: initialize, ping, tools/list, tools/call. One message in, one reply out (null for
 * notifications). Plain Kotlin, tested on the PC (McpHandlerTest).
 */
class McpHandler(
    private val tools: List<Tool>,
    /** Tools listed to the agent right now (abilities switched off are left out; calling one still explains why). */
    private val listed: (Tool) -> Boolean = { true },
    private val serverName: String,
    private val serverVersion: String,
    private val instructions: String,
    /** Told about each call when it ends: tool name, whether it failed, milliseconds (for the app's activity line). */
    private val onCall: (Call) -> Unit = {},
    /** Runs before every call; throws a ToolError to refuse it (the kill switch). */
    private val gate: (String) -> Unit = {},
) {
    class Call(val name: String, val args: JsonObject, val result: ToolResult, val ms: Long)

    /** The kill switch: stops whatever is running now (the agent gets an error for it). */
    fun stopAll() = work.coroutineContext.cancelChildren()

    private val byName = tools.associateBy { it.name }

    /** What each tool's replies cost the agent so far (device shows it; spec/TASKS.md measures it). */
    val tokens = Tokens()

    /**
     * Tool work runs outside the request, so a call stuck inside Android (a frozen app's accessibility tree) can't
     * hold the reply: the agent gets a timeout error on time and the stuck work is cancelled.
     */
    private val work = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    suspend fun handle(body: String): String? {
        val msg = try {
            Json.parseToJsonElement(body)
        } catch (e: Exception) {
            return error(JsonNull, -32700, "Parse error")
        }
        if (msg is JsonArray) return error(JsonNull, -32600, "Batches are not supported")
        if (msg !is JsonObject) return error(JsonNull, -32600, "Invalid request")
        val id = msg["id"]
        val method = (msg["method"] as? JsonPrimitive)?.content
        if (method == null) {
            // A reply from the client to something we asked: we never ask, so ignore it.
            return null
        }
        val params = msg["params"] as? JsonObject ?: JsonObject(emptyMap())
        if (id == null) return null // notifications/initialized, notifications/cancelled, ...

        return when (method) {
            "initialize" -> result(id, initialize(params))
            "ping" -> result(id, JsonObject(emptyMap()))
            "tools/list" -> result(id, buildJsonObject { put("tools", buildJsonArray { tools.filter(listed).forEach { add(describe(it)) } }) })
            "tools/call" -> result(id, call(params))
            "resources/list" -> result(id, buildJsonObject { put("resources", JsonArray(emptyList())) })
            "prompts/list" -> result(id, buildJsonObject { put("prompts", JsonArray(emptyList())) })
            else -> error(id, -32601, "Method not found: $method")
        }
    }

    private fun initialize(params: JsonObject): JsonObject {
        val asked = (params["protocolVersion"] as? JsonPrimitive)?.content
        val version = if (asked in SUPPORTED) asked!! else DEFAULT_VERSION
        return buildJsonObject {
            put("protocolVersion", version)
            put("capabilities", buildJsonObject { put("tools", buildJsonObject { put("listChanged", false) }) })
            put("serverInfo", buildJsonObject {
                put("name", serverName)
                put("version", serverVersion)
            })
            put("instructions", instructions)
        }
    }

    private fun describe(t: Tool) = buildJsonObject {
        put("name", t.name)
        put("description", t.description)
        put("inputSchema", t.inputSchema)
    }

    private suspend fun call(params: JsonObject): JsonObject {
        val name = (params["name"] as? JsonPrimitive)?.content
        val tool = byName[name]
            ?: return render(ToolResult.error("not_found", "no tool named '$name'; tools: ${byName.keys.joinToString()}"))
        val args = params["arguments"] as? JsonObject ?: JsonObject(emptyMap())
        val started = System.currentTimeMillis()
        val r = try {
            gate(tool.name)
            val job = work.async { tool.call(args) }
            try {
                withTimeout(tool.timeoutMs) { job.await() }
            } catch (e: TimeoutCancellationException) {
                job.cancel()
                throw e
            } catch (e: CancellationException) {
                // Cancelled by stopAll (the user paused), not by the agent hanging up.
                if (job.isCancelled && currentCoroutineContext().isActive) throw ToolError("paused", "the user paused Reach; the running action was stopped")
                throw e
            }
        } catch (e: TimeoutCancellationException) {
            ToolResult.error("timeout", "${tool.name} took longer than ${tool.timeoutMs / 1000} s and was stopped; observe, then try again")
        } catch (e: CancellationException) {
            throw e
        } catch (e: ToolError) {
            ToolResult.error(e.code, e.hint)
        } catch (e: Exception) {
            ToolResult.error("internal", (e.message ?: e.javaClass.simpleName).take(300))
        }
        tokens.add(tool.name, r)
        onCall(Call(tool.name, args, r, System.currentTimeMillis() - started))
        return render(r)
    }

    private fun render(r: ToolResult) = buildJsonObject {
        put("content", buildJsonArray {
            r.content.forEach { c ->
                add(when (c) {
                    is Content.Text -> buildJsonObject { put("type", "text"); put("text", c.text) }
                    is Content.Image -> buildJsonObject { put("type", "image"); put("data", c.base64); put("mimeType", c.mime) }
                })
            }
        })
        put("isError", r.isError)
    }

    private fun result(id: JsonElement, result: JsonElement) = buildJsonObject {
        put("jsonrpc", "2.0")
        put("id", id)
        put("result", result)
    }.toString()

    private fun error(id: JsonElement, code: Int, message: String) = buildJsonObject {
        put("jsonrpc", "2.0")
        put("id", id)
        put("error", buildJsonObject {
            put("code", code)
            put("message", message)
        })
    }.toString()

    companion object {
        val SUPPORTED = setOf("2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05")
        const val DEFAULT_VERSION = "2025-06-18"
    }
}

/**
 * A rough count of the tokens each tool's replies cost: text at ~4 characters a token, images at width × height / 750
 * (how Claude counts image tokens). Good enough to compare modes and servers; not a bill.
 */
class Tokens {
    private class Row { var calls = 0; var tokens = 0L; var images = 0 }
    private val rows = LinkedHashMap<String, Row>()

    @Synchronized
    fun add(tool: String, r: ToolResult) {
        val row = rows.getOrPut(tool) { Row() }
        row.calls++
        for (c in r.content) when (c) {
            is Content.Text -> row.tokens += estimate(c.text)
            is Content.Image -> { row.images++; row.tokens += if (c.width > 0) c.width.toLong() * c.height / 750 else 1_600 }
        }
    }

    @Synchronized
    fun text(): String {
        if (rows.isEmpty()) return "replies so far: none"
        return "replies so far (≈tokens per call): " + rows.entries.joinToString(" · ") { (name, r) ->
            "$name ${r.calls}× ~${r.tokens / r.calls}" + if (r.images > 0) " (${r.images} images)" else ""
        }
    }

    companion object {
        fun estimate(text: String): Long = (text.length + 3) / 4L
    }
}
