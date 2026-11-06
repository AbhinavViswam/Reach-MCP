package app.reach.android

import app.reach.android.mcp.HttpServer
import app.reach.android.mcp.McpHandler
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.HttpURLConnection
import java.net.URL

private val CRLF = String(charArrayOf(13.toChar(), 10.toChar()))

class McpTest {
    private class Echo : Tool {
        override val name = "echo"
        override val description = "echo"
        override val inputSchema = schema { prop("text", "string", "text"); required("text") }
        override suspend fun call(args: JsonObject): ToolResult {
            val t = args.str("text") ?: throw ToolError("bad_args", "text needed")
            return ToolResult.text(t)
        }
    }

    private class Slow : Tool {
        override val name = "slow"
        override val description = "never ends in time"
        override val inputSchema = schema {}
        override val timeoutMs = 200L
        override suspend fun call(args: JsonObject): ToolResult {
            // Blocking, like a stuck Android call: the reply must still come on time.
            Thread.sleep(2_000)
            return ToolResult.text("late")
        }
    }

    private val handler = McpHandler(listOf(Echo(), Slow()), serverName = "reach-test", serverVersion = "0", instructions = "hi")

    private fun obj(s: String?) = Json.parseToJsonElement(s!!).jsonObject

    @Test fun initializeEchoesKnownVersion() = runBlocking {
        val r = obj(handler.handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""))
        assertEquals("2025-03-26", r["result"]!!.jsonObject["protocolVersion"]!!.jsonPrimitive.content)
        val u = obj(handler.handle("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}"""))
        assertEquals(McpHandler.DEFAULT_VERSION, u["result"]!!.jsonObject["protocolVersion"]!!.jsonPrimitive.content)
    }

    @Test fun notificationsGetNoReply() = runBlocking {
        assertNull(handler.handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""))
    }

    @Test fun listAndCall() = runBlocking {
        val list = obj(handler.handle("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}"""))
        assertEquals(2, list["result"]!!.jsonObject["tools"]!!.jsonArray.size)
        val call = obj(handler.handle("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{"text":"नमस्ते"}}}"""))
        val content = call["result"]!!.jsonObject["content"]!!.jsonArray[0].jsonObject
        assertEquals("नमस्ते", content["text"]!!.jsonPrimitive.content)
    }

    @Test fun switchedOffToolsAreNotListed() = runBlocking {
        val h = McpHandler(listOf(Echo(), Slow()), { it.name != "slow" }, "reach-test", "0", "hi")
        val list = obj(h.handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))
        assertEquals(listOf("echo"), list["result"]!!.jsonObject["tools"]!!.jsonArray.map { it.jsonObject["name"]!!.jsonPrimitive.content })
    }

    @Test fun toolErrorsAreToolResults() = runBlocking {
        val call = obj(handler.handle("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":{}}}"""))
        val res = call["result"]!!.jsonObject
        assertEquals("true", res["isError"]!!.jsonPrimitive.content)
        assertTrue(res["content"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content.startsWith("error: bad_args"))
    }

    @Test fun stuckToolTimesOutOnTime() = runBlocking {
        val t0 = System.currentTimeMillis()
        val call = obj(handler.handle("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"slow"}}"""))
        val took = System.currentTimeMillis() - t0
        assertTrue("took $took ms", took < 1_500)
        assertTrue(call.toString().contains("error: timeout"))
    }

    @Test fun badJsonAndUnknownMethod() = runBlocking {
        assertEquals(-32700, obj(handler.handle("{nope"))["error"]!!.jsonObject["code"]!!.jsonPrimitive.content.toInt())
        assertEquals(-32601, obj(handler.handle("""{"jsonrpc":"2.0","id":7,"method":"x/y"}"""))["error"]!!.jsonObject["code"]!!.jsonPrimitive.content.toInt())
    }

    // -------------------------------------------------------------------------------------------- HTTP

    private fun post(port: Int, body: String, token: String?, origin: String? = null): Pair<Int, String> {
        val c = URL("http://127.0.0.1:$port/mcp").openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json")
        c.setRequestProperty("Accept", "application/json, text/event-stream")
        token?.let { c.setRequestProperty("Authorization", "Bearer $it") }
        origin?.let { c.setRequestProperty("Origin", it) }
        c.outputStream.use { it.write(body.toByteArray()) }
        val code = c.responseCode
        val text = (if (code < 400) c.inputStream else c.errorStream)?.bufferedReader()?.readText().orEmpty()
        return code to text
    }

    /** Raw HTTP: Java's HttpURLConnection silently drops the Origin header. */
    private fun rawPost(port: Int, body: String, headers: List<String>): Int {
        java.net.Socket("127.0.0.1", port).use { s ->
            val b = body.toByteArray()
            val head = listOf("POST /mcp HTTP/1.1", "Host: x", "Content-Type: application/json") + headers +
                listOf("Content-Length: ${b.size}", "Connection: close", "", "")
            s.getOutputStream().write(head.joinToString(CRLF).toByteArray() + b)
            val first = s.getInputStream().bufferedReader().readLine()
            return first.split(' ')[1].toInt()
        }
    }

    @Test fun httpNeedsTokenAndRefusesWebPages() {
        val server = HttpServer(handler, { "secret" }, 0, allNetworks = false)
        server.start()
        try {
            val port = server.boundPort
            val ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}"""
            assertEquals(401, post(port, ping, null).first)
            assertEquals(401, post(port, ping, "wrong").first)
            assertEquals(403, rawPost(port, ping, listOf("Authorization: Bearer secret", "Origin: https://evil.example")))
            assertEquals(200, rawPost(port, ping, listOf("Authorization: Bearer secret", "Origin: http://localhost:3000")))
            val (code, body) = post(port, ping, "secret")
            assertEquals(200, code)
            assertTrue(body.contains("\"result\""))
            assertEquals(202, post(port, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", "secret").first)
            // Several calls on one keep-alive connection.
            repeat(5) { assertEquals(200, post(port, ping, "secret").first) }
            val health = URL("http://127.0.0.1:$port/health").readText()
            assertEquals("reach ok", health)
        } finally {
            server.stop()
        }
    }
}
