package app.reach.android

import app.reach.android.mcp.Content
import app.reach.android.mcp.HttpServer
import app.reach.android.mcp.McpHandler
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.tools.Flow
import app.reach.android.tools.Net
import app.reach.android.tools.PcTool
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.URL

// The same cases as windows/tests/LinkTests.cs, the other way round.
class LinkTest {
    @Test fun tailscaleAddresses() {
        fun a(s: String) = InetAddress.getByName(s)
        assertTrue(Net.localOrTailscale(a("127.0.0.1")))
        assertTrue(Net.localOrTailscale(a("100.101.7.3")))
        assertFalse(Net.localOrTailscale(a("100.128.0.1")))
        assertFalse(Net.localOrTailscale(a("192.168.1.20")))
        assertTrue(Net.localOrTailscale(a("fd7a:115c:a1e0::1")))
        assertFalse(Net.localOrTailscale(a("fd00::1")))
    }

    private class Fake(override val name: String, val reply: (JsonObject) -> ToolResult) : Tool {
        override val description = "The PC's $name. More words."
        override val inputSchema = schema {}
        override suspend fun call(args: JsonObject) = reply(args)
    }

    private fun text(r: ToolResult) = (r.content.first() as Content.Text).text

    @Test fun phoneUsesThePc() = runBlocking {
        val pcHandler = McpHandler(listOf(
            Fake("observe") { ToolResult.text("window Notepad · v2\ne1aa field \"Text editor\"") },
            Fake("act") { a -> ToolResult.text("verdict: done · type into ${a.str("target")} \"Text editor\"\nchanges") },
        ), serverName = "reach-windows", serverVersion = "0", instructions = "")
        val pc = HttpServer(pcHandler, { "pc-secret" }, 0, allNetworks = false)
        pc.start()
        try {
            var token = "pc-secret"
            val tool = PcTool { "http://127.0.0.1:${pc.boundPort}" to token }
            val list = text(tool.call(Json.parseToJsonElement("""{"tool":"tools"}""").jsonObject))
            assertTrue(list, list.contains("observe: The PC's observe.") && !list.contains("More words"))
            val args = Json.parseToJsonElement("""{"tool":"act","args":{"action":"type","target":"e1aa","text":"482913"}}""").jsonObject
            val r = tool.call(args)
            assertFalse(r.isError)
            assertTrue(text(r), text(r).startsWith("verdict: done · type into e1aa \"Text editor\" · on the PC\nchanges"))
            val step = Flow.stepsOf("pc", args, r)
            assertEquals("Text editor", step[0]["args"]!!.jsonObject["args"]!!.jsonObject["target"]!!.jsonObject.str("text"))
            assertTrue(Flow.stepsOf("pc", Json.parseToJsonElement("""{"tool":"observe"}""").jsonObject, r).isEmpty())

            token = "wrong"
            val bad = runCatching { PcTool { "http://127.0.0.1:${pc.boundPort}" to token }.call(args) }.exceptionOrNull() as ToolError
            assertEquals("bad_token", bad.code)
            token = ""
            assertEquals("no_pc", (runCatching { PcTool { "http://x" to token }.call(args) }.exceptionOrNull() as ToolError).code)
        } finally {
            pc.stop()
        }
    }

    @Test fun tailscaleModeTurnsOthersAway() {
        val h = McpHandler(emptyList(), serverName = "t", serverVersion = "0", instructions = "")
        val s = HttpServer(h, { "s" }, 0, allNetworks = false, allow = { false })
        s.start()
        try {
            assertTrue(runCatching { URL("http://127.0.0.1:${s.boundPort}/health").readText() }.isFailure)
        } finally {
            s.stop()
        }
    }
}
