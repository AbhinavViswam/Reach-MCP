package app.reach.android

import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.str
import app.reach.android.tools.Flow
import app.reach.android.tools.FlowsTool
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.file.Files

// The same cases as windows/tests/FlowTests.cs: both platforms record and replay alike.
class FlowTest {
    private fun j(s: String) = Json.parseToJsonElement(s).jsonObject
    private fun text(r: ToolResult) = Flow.firstText(r)

    @Test fun recordedTapsKeepTheirWords() {
        val s = Flow.stepsOf("act", j("""{"action":"tap","target":"e4k2"}"""), ToolResult.text("verdict: done · tap e4k2 \"Send\"\nchanges"))
        assertEquals(1, s.size)
        val t = s[0]["args"]!!.jsonObject["target"]!!.jsonObject
        assertEquals("Send", t.str("text"))
        assertEquals("e4k2", t.str("id"))
        assertEquals("done", s[0].str("verdict"))
        assertTrue(Flow.stepsOf("observe", JsonObject(emptyMap()), ToolResult.text("app x")).isEmpty())
        val batch = Flow.stepsOf("act_batch",
            j("""{"actions":[{"action":"tap","target":"e1aa"},{"action":"type","target":"e2bb","text":"hi"},{"action":"key","key":"enter"}]}"""),
            ToolResult.text("verdict: no_effect · 2 of 3\nstep 1: done · tap e1aa \"Search\"\nstep 2: no_effect · type into e2bb \"Box\"\nstopped"))
        assertEquals(2, batch.size)
        assertEquals("no_effect", batch[1].str("verdict"))
    }

    @Test fun inputsAreFilledIn() {
        val steps = listOf(j("""{"tool":"act","args":{"action":"type","target":{"id":"e9zz","text":"Message"},"text":"Dear Bob, hi $5"}}"""))
        val (out, inputs) = Flow.makeInputs(steps, j("""{"who":"Bob","missing":"nowhere"}"""))
        assertEquals("Bob", inputs.str("who"))
        assertNull(inputs["missing"])
        val args = out[0]["args"]!!.jsonObject
        assertEquals("Dear {{who}}, hi $5", args.str("text"))
        assertEquals("Dear Ann \"A\" \$1, hi $5", Flow.fill(args, inputs, j("""{"who":"Ann \"A\" ${'$'}1"}""")).str("text"))
        assertEquals("Dear Bob, hi $5", Flow.fill(args, inputs, null).str("text"))
        assertEquals("bad_args", runCatching { Flow.fill(j("""{"text":"{{nope}}"}"""), JsonObject(emptyMap()), null) }.exceptionOrNull().let { (it as ToolError).code })
    }

    @Test fun goodMeansAsRecorded() {
        val done = j("""{"verdict":"done"}""")
        assertTrue(Flow.good(done, ToolResult.text("verdict: done · x")))
        assertTrue(Flow.good(done, ToolResult.text("opened WhatsApp")))
        assertFalse(Flow.good(done, ToolResult.text("verdict: no_effect · x")))
        assertTrue(Flow.good(j("""{"verdict":"no_effect"}"""), ToolResult.text("verdict: no_effect · x")))
        assertFalse(Flow.good(done, ToolResult.error("stale_id", "x")))
        assertEquals("weekly_report", Flow.cleanName("Weekly report"))
    }

    @Test fun recordReplayAndHeal() = runBlocking {
        val dir = Files.createTempDirectory("reach-flows").toFile()
        var sendId = "e1aa"
        val calls = ArrayList<String>()
        suspend fun device(tool: String, a: JsonObject): ToolResult {
            calls += "$tool $a"
            if (tool == "observe") return ToolResult.text("app com.whatsapp\ne3cc button \"Send\"")
            if (tool == "apps") return ToolResult.text("opened WhatsApp")
            val t = a["target"]
            if (t is JsonPrimitive && t.content != sendId) return ToolResult.error("stale_id", "gone")
            if (t is JsonObject && sendId == "zzzz" && a.str("action") == "tap") return ToolResult.error("not_found", "no Send")
            if (a.str("action") == "type") return ToolResult.text("verdict: done · type into e2bb \"Message\"")
            return ToolResult.text("verdict: done · tap $sendId \"Send\"")
        }
        val flows = FlowsTool(dir) { n, a -> device(n, a) }
        flows.call(j("""{"action":"record_start","name":"Say hi"}"""))
        for ((tool, args) in listOf(
            "apps" to j("""{"action":"open","name":"whatsapp"}"""),
            "observe" to JsonObject(emptyMap()),
            "act" to j("""{"action":"type","target":{"text":"Message"},"text":"hi Mum"}"""),
            "act" to j("""{"action":"tap","target":"e1aa"}"""),
        )) flows.saw(tool, args, device(tool, args))
        val stop = text(flows.call(j("""{"action":"record_stop","inputs":{"msg":"hi Mum"}}""")))
        assertTrue(stop, stop.contains("3 steps") && stop.contains("inputs msg"))

        sendId = "e7ff"
        calls.clear()
        val run = text(flows.call(j("""{"action":"run","name":"say_hi","inputs":{"msg":"hello"}}""")))
        assertTrue(run, run.startsWith("verdict: done · flow \"say_hi\" ran 3 steps"))
        assertTrue(run, run.contains("found again by \"Send\""))
        assertTrue(calls.any { it.contains("hello") })
        assertTrue(java.io.File(dir, "say_hi.json").readText().contains("e7ff"))

        sendId = "zzzz"
        val broken = text(flows.call(j("""{"action":"run","name":"say_hi"}""")))
        assertTrue(broken, broken.contains("stopped at step 3/3") && broken.contains("from_step=4") && broken.contains("e3cc button"))
        dir.deleteRecursively()
        Unit
    }

    @Suppress("unused") private fun p(s: String) = Json.parseToJsonElement(s).jsonPrimitive
}
