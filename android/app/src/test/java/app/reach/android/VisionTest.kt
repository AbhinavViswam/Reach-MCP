package app.reach.android

import app.reach.android.mcp.Content
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.Tokens
import app.reach.android.screen.Box
import app.reach.android.screen.Ids
import app.reach.android.screen.OcrLine
import app.reach.android.screen.RawElement
import app.reach.android.screen.Role
import app.reach.android.screen.Vision
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VisionTest {
    private fun el(role: Role, label: String, box: Box) = RawElement(role, label, null, null, "android.widget.X", box)

    @Test fun thinTreesGetOcr() {
        assertTrue(Vision.thin(emptyList()))
        // A Flutter app: one big unlabeled view.
        assertTrue(Vision.thin(Ids.assign("a", listOf(el(Role.ITEM, "", Box(0, 0, 1080, 2400))))))
        val normal = Ids.assign("a", (1..5).map { el(Role.BUTTON, "B$it", Box(0, it * 100, 100, it * 100 + 50)) })
        assertFalse(Vision.thin(normal))
    }

    @Test fun ocrSkipsWhatTheTreeSays() {
        val tree = Ids.assign("a", listOf(el(Role.BUTTON, "Send message", Box(0, 0, 400, 100))))
        val lines = listOf(
            OcrLine("Send message", Box(10, 10, 300, 90)),   // already the button's label
            OcrLine("Play", Box(500, 500, 700, 600)),        // only on the picture
            OcrLine("·", Box(0, 900, 10, 910)),              // stray mark
        )
        val extra = Vision.extra(tree, lines)
        assertEquals(listOf("Play"), extra.map { it.label })
        assertEquals(Vision.OCR_CLASS, extra[0].className)
    }

    @Test fun ocrIdsAreStableAndTreeIdsUnchanged() {
        val tree = listOf(el(Role.BUTTON, "Menu", Box(0, 0, 100, 100)))
        val withOcr = Ids.assign("game", tree + Vision.extra(Ids.assign("game", tree), listOf(OcrLine("Start game", Box(200, 800, 800, 900)))))
        val again = Ids.assign("game", tree + Vision.extra(Ids.assign("game", tree), listOf(OcrLine("Start game", Box(210, 820, 810, 910)))))
        assertEquals(Ids.assign("game", tree)[0].id, withOcr[0].id)
        assertEquals(withOcr[1].id, again[1].id)
    }

    @Test fun tokenEstimate() {
        val t = Tokens()
        t.add("observe", ToolResult(listOf(Content.Text("x".repeat(400)))))
        t.add("observe", ToolResult(listOf(Content.Text("x".repeat(800)))))
        t.add("observe", ToolResult(listOf(Content.Image("", "image/jpeg", 750, 1000))))
        assertTrue(t.text(), t.text().contains("observe 3× ~"))
        assertTrue(t.text().contains("(1 images)"))
    }
}
