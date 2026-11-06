package app.reach.android

import app.reach.android.mcp.HttpServer
import app.reach.android.mcp.McpHandler
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.schema
import app.reach.android.privacy.AppRule
import app.reach.android.privacy.Audit
import app.reach.android.privacy.Privacy
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files

class SafetyTest {
    @Test fun userRulesBeatTheBuiltinList() {
        val none = emptyMap<String, AppRule>()
        assertEquals(AppRule.BLOCK, Privacy.rule("com.phonepe.app", null, none))
        assertEquals(AppRule.BLOCK, Privacy.rule("com.hdfcbank.payzapp", null, none))
        assertEquals(AppRule.ALLOW, Privacy.rule("com.whatsapp", null, none))
        val user = mapOf("com.phonepe.app" to AppRule.ALLOW, "com.whatsapp" to AppRule.READ_ONLY, "com.android.chrome" to AppRule.BLOCK)
        assertEquals(AppRule.ALLOW, Privacy.rule("com.phonepe.app", null, user))
        assertEquals(AppRule.READ_ONLY, Privacy.rule("com.whatsapp", null, user))
        assertEquals(AppRule.BLOCK, Privacy.rule("com.android.chrome", null, user))
        assertEquals(AppRule.READ_ONLY, AppRule.of("read_only"))
        assertEquals(AppRule.ALLOW, AppRule.of("nonsense"))
    }

    @Test fun readOnlyRefusesActions() {
        Privacy.userRules = mapOf("com.whatsapp" to AppRule.READ_ONLY)
        try {
            val e = runCatching { Privacy.requireCanAct("com.whatsapp") }.exceptionOrNull() as ToolError
            assertEquals("read_only_app", e.code)
            Privacy.requireCanAct("com.example.notes")
        } finally {
            Privacy.userRules = emptyMap()
        }
    }

    private class Echo : Tool {
        override val name = "echo"
        override val description = "echo"
        override val inputSchema = schema {}
        override suspend fun call(args: JsonObject): ToolResult = ToolResult.text("hi\nmore")
    }

    @Test fun gateRefusesAndEveryCallIsReported() = runBlocking {
        var paused = true
        val seen = ArrayList<McpHandler.Call>()
        val h = McpHandler(listOf(Echo()), serverName = "t", serverVersion = "0", instructions = "",
            onCall = { seen += it }, gate = { if (paused) throw ToolError("paused", "wait") })
        val call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"x":"1"}}}"""
        val r1 = Json.parseToJsonElement(h.handle(call)!!).jsonObject["result"]!!.jsonObject
        assertTrue(r1["isError"]!!.jsonPrimitive.content.toBoolean())
        assertTrue(r1["content"]!!.jsonArray[0].jsonObject["text"]!!.jsonPrimitive.content.startsWith("error: paused"))
        paused = false
        val r2 = Json.parseToJsonElement(h.handle(call)!!).jsonObject["result"]!!.jsonObject
        assertFalse(r2["isError"]!!.jsonPrimitive.content.toBoolean())
        assertEquals(2, seen.size)
        assertTrue(seen[0].result.isError)
        assertEquals("1", (seen[1].args["x"] as JsonPrimitive).content)
    }

    @Test fun auditHidesCodesNewestFirst() {
        val dir = Files.createTempDirectory("reach-audit").toFile()
        Audit.init(dir)
        Audit.call("act", Json.parseToJsonElement("""{"action":"type","text":"OTP is 482913"}""").jsonObject, true, "verdict: done", 12)
        Audit.approval("send an SMS", "to Mum", false, "this phone")
        val rows = Audit.read()
        assertEquals(2, rows.size)
        assertEquals("approval", rows[0].kind)
        assertFalse(rows[0].ok)
        assertFalse(rows[1].args.contains("482913"))
        assertEquals(12, rows[1].ms)
        Audit.clear()
        assertTrue(Audit.read().isEmpty())
        dir.deleteRecursively()
    }

    private fun approve(port: Int, token: String?, body: String): Pair<Int, String> {
        val c = URL("http://127.0.0.1:$port/approve").openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json")
        token?.let { c.setRequestProperty("Authorization", "Bearer $it") }
        c.outputStream.use { it.write(body.toByteArray()) }
        val code = c.responseCode
        return code to ((if (code < 400) c.inputStream else c.errorStream)?.bufferedReader()?.readText().orEmpty())
    }

    @Test fun pcCanAskThisPhone() {
        var asked: Triple<String, String, String>? = null
        val h = McpHandler(listOf(Echo()), serverName = "t", serverVersion = "0", instructions = "")
        val server = HttpServer(h, { "secret" }, 0, allNetworks = false, approve = { w, d, f -> asked = Triple(w, d, f); w.contains("mail") })
        server.start()
        try {
            val p = server.boundPort
            assertEquals(401, approve(p, null, """{"what":"send mail"}""").first)
            assertEquals(401, approve(p, "nope", """{"what":"send mail"}""").first)
            assertEquals(400, approve(p, "secret", """{"detail":"x"}""").first)
            val (code, body) = approve(p, "secret", """{"what":"send mail","detail":"to Bob","from":"DESKTOP-1"}""")
            assertEquals(200, code)
            assertTrue(body.contains("\"allowed\":true"))
            assertEquals(Triple("send mail", "to Bob", "DESKTOP-1"), asked)
            assertTrue(approve(p, "secret", """{"what":"run a command"}""").second.contains("\"allowed\":false"))
        } finally {
            server.stop()
        }
    }
}
