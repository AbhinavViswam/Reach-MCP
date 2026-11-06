package app.reach.android.mcp

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/** One MCP tool. */
interface Tool {
    val name: String
    val description: String
    val inputSchema: JsonObject
    /** Longest this tool may run before the server gives up and says so. */
    val timeoutMs: Long get() = 20_000
    suspend fun call(args: JsonObject): ToolResult
}

sealed interface Content {
    data class Text(val text: String) : Content
    /** [width]/[height] in pixels, when known: used for the token estimate (Tokens). */
    data class Image(val base64: String, val mime: String, val width: Int = 0, val height: Int = 0) : Content
}

data class ToolResult(val content: List<Content>, val isError: Boolean = false) {
    companion object {
        fun text(s: String) = ToolResult(listOf(Content.Text(s)))
        /** Contract §4: an error code, then one line saying what to do next. */
        fun error(code: String, hint: String) = ToolResult(listOf(Content.Text("error: $code · $hint")), isError = true)
    }
}

/** A tool's failure that should reach the agent as a tool error, not a crash. */
class ToolError(val code: String, val hint: String) : Exception("$code: $hint")

// ---------------------------------------------------------------------------------------------- argument helpers

fun JsonObject.str(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
fun JsonObject.int(key: String): Int? = (this[key] as? JsonPrimitive)?.let { it.intOrNull ?: it.contentOrNull?.toIntOrNull() }
fun JsonObject.long(key: String): Long? = (this[key] as? JsonPrimitive)?.let { it.longOrNull ?: it.contentOrNull?.toLongOrNull() }
fun JsonObject.bool(key: String): Boolean? = (this[key] as? JsonPrimitive)?.let { it.booleanOrNull ?: it.contentOrNull?.toBooleanStrictOrNull() }
fun JsonObject.obj(key: String): JsonObject? = this[key] as? JsonObject
fun JsonObject.arr(key: String): JsonArray? = this[key] as? JsonArray

// ---------------------------------------------------------------------------------------------- schema helpers

/** `schema { prop("text", "string", "What to type") ; required("text") }` */
class SchemaBuilder {
    private val props = LinkedHashMap<String, JsonElement>()
    private val req = ArrayList<String>()

    fun prop(name: String, type: String, description: String, enum: List<String>? = null, extra: (JsonObjectBuilder.() -> Unit)? = null) {
        props[name] = buildJsonObject {
            put("type", type)
            put("description", description)
            if (enum != null) put("enum", buildJsonArray { enum.forEach { add(JsonPrimitive(it)) } })
            extra?.invoke(this)
        }
    }

    /** A free-form value (string id, or an object with text / x,y). */
    fun any(name: String, description: String) {
        props[name] = buildJsonObject { put("description", description) }
    }

    fun required(vararg names: String) { req += names }

    fun build(): JsonObject = buildJsonObject {
        put("type", "object")
        put("properties", JsonObject(props))
        if (req.isNotEmpty()) put("required", buildJsonArray { req.forEach { add(JsonPrimitive(it)) } })
    }
}

fun schema(block: SchemaBuilder.() -> Unit): JsonObject = SchemaBuilder().apply(block).build()

val EMPTY_SCHEMA: JsonObject = schema {}
