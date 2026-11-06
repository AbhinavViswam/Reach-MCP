package app.reach.android

import app.reach.android.deep.Fence
import app.reach.android.deep.When
import app.reach.android.mcp.ToolError
import app.reach.android.privacy.Mask
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.time.LocalDate
import java.time.LocalDateTime

class DeepTest {
    @Test fun otpsAreHidden() {
        val m = Mask.apply("Your OTP is 482913. Do not share it.")
        assertTrue(m.masked)
        assertFalse(m.text.contains("482913"))
        assertTrue(m.text.contains("••••••"))
    }

    @Test fun ordinaryNumbersStay() {
        val m = Mask.apply("Meeting at 1530 in room 4021, see you in 2026")
        assertFalse(m.masked)
        assertEquals("Meeting at 1530 in room 4021, see you in 2026", m.text)
        // A year near the word "code" is still a year.
        assertTrue(Mask.apply("Dress code for 2026 party").text.contains("2026"))
    }

    @Test fun cardsShowLastFour() {
        val m = Mask.apply("Card 4111 1111 1111 1111 was charged")
        assertTrue(m.masked)
        assertTrue(m.text.contains("•••• 1111"))
        assertFalse(m.text.contains("4111 1111"))
        // Not a valid card number (fails Luhn): left alone.
        assertFalse(Mask.apply("Order 1234 5678 9012 3456").masked)
    }

    @Test fun hindiOtp() {
        assertTrue(Mask.apply("आपका ओटीपी 7788 है").masked)
    }

    @Test fun filesStayInsideAllowedFolders() {
        val root = Files.createTempDirectory("reach").toFile()
        File(root, "Download").mkdirs()
        assertEquals(File(root, "Download/a.txt").canonicalFile, Fence.resolve(root, "Download/a.txt"))
        assertEquals(File(root, "Download/a.txt").canonicalFile, Fence.resolve(root, root.path + "/Download/a.txt"))
        assertThrows(ToolError::class.java) { Fence.resolve(root, "Android/data/x") }
        assertThrows(ToolError::class.java) { Fence.resolve(root, "Download/../Android/data") }
        assertThrows(ToolError::class.java) { Fence.resolve(root, "DownloadEvil/x") }
        assertThrows(ToolError::class.java) { Fence.resolve(root, "") }
    }

    @Test fun datesAgentsSend() {
        val today = LocalDate.of(2026, 11, 6)
        assertEquals(LocalDate.of(2026, 11, 7), When.date("tomorrow", today))
        assertEquals(LocalDateTime.of(2026, 11, 7, 15, 0), When.dateTime("2026-11-07T15:00", today))
        assertEquals(LocalDateTime.of(2026, 11, 7, 15, 0), When.dateTime("2026-11-07 15:00", today))
        assertEquals(LocalDateTime.of(2026, 11, 6, 6, 30), When.dateTime("6:30", today))
        assertThrows(ToolError::class.java) { When.date("next blursday", today) }
    }
}
