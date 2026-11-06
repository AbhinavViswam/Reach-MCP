package app.reach.android.screen

/*
 * Seeing what the accessibility tree can't (spec/CONTRACT.md §5 observe modes): text read from the screenshot
 * becomes ordinary elements with IDs, so a Flutter app, a game menu or a web view can be tapped by ID like any
 * button. Plain Kotlin, tested on the PC; the same rules as windows/src/Reach/Core/Vision.cs.
 */

/** One line of text found on the screenshot, in screen pixels. */
data class OcrLine(val text: String, val bounds: Box)

object Vision {
    const val OCR_CLASS = "ocr"

    /**
     * Too little to work with: fewer than 3 elements with words, outside [ignoreTop] px (a title bar). Then the
     * screen is read with OCR without being asked.
     */
    fun thin(elements: List<Element>, ignoreTop: Int = 0): Boolean =
        elements.count { it.label.isNotBlank() && it.bounds.top >= ignoreTop && it.raw.className != OCR_CLASS } < 3

    /**
     * OCR lines as elements, minus what the tree already says: a line whose words are already in the label or
     * value of the element it sits on is left out, and so are stray marks (fewer than 2 letters or digits).
     */
    fun extra(tree: List<Element>, lines: List<OcrLine>): List<RawElement> {
        val out = ArrayList<RawElement>()
        for (l in lines) {
            val text = l.text.replace(Regex("\\s+"), " ").trim()
            if (text.count { it.isLetterOrDigit() } < 2) continue
            val norm = Ids.normalize(text)
            val cx = l.bounds.cx
            val cy = l.bounds.cy
            val known = tree.any { e ->
                e.bounds.contains(cx, cy) && (Ids.normalize(e.label).contains(norm) || Ids.normalize(e.value.orEmpty()).contains(norm))
            }
            if (known) continue
            out += RawElement(Role.TEXT, text.take(ScreenReader.MAX_LABEL), null, null, OCR_CLASS, l.bounds)
        }
        return out
    }

    /**
     * The elements worth a numbered box on a marks screenshot: everything pressable or with words, at most [max].
     * Boxes over 40% of [screenArea] (whole panes) are left out: they hide everything else.
     */
    fun markable(elements: List<Element>, screenArea: Long, max: Int = 80): List<Element> =
        elements.filter { !it.bounds.isEmpty && it.bounds.area * 10 <= screenArea * 4 && (it.role.pressable || it.label.isNotBlank()) }.take(max)
}
