package app.reach.android.screen

/** What changed between two views (spec/CONTRACT.md §3 "Changes"). */
data class Diff(
    val from: Snapshot,
    val to: Snapshot,
    val added: List<Element>,
    val removed: List<Element>,
    val changed: List<Pair<Element, Element>>,
) {
    val appChanged: Boolean get() = from.app != to.app || from.blockedApp != to.blockedApp
    val keyboardChanged: Boolean get() = from.keyboardOpen != to.keyboardOpen
    val isEmpty: Boolean get() = added.isEmpty() && removed.isEmpty() && changed.isEmpty() && !appChanged && !keyboardChanged

    /** A new page or app: better to send the whole view than a long list of changes. */
    val mostlyNew: Boolean
        get() = appChanged || (to.elements.isNotEmpty() && added.size * 10 >= to.elements.size * 6 && added.size > 4)

    fun text(bounds: Boolean = false): String = buildString {
        append("changes v").append(from.version).append(" → v").append(to.version)
        if (isEmpty) {
            append(": none")
            return@buildString
        }
        append('\n')
        if (appChanged) append("app ").append(from.app).append(" → ").append(to.app).append('\n')
        if (keyboardChanged) append("keyboard ").append(if (to.keyboardOpen) "opened" else "closed").append('\n')
        added.forEach { append("+ ").append(ViewText.line(it, bounds)).append('\n') }
        removed.forEach { append("- ").append(it.id).append(' ').append(it.role.word).append(" \"").append(it.label).append("\"\n") }
        changed.forEach { (a, b) ->
            append("~ ").append(ViewText.line(b, bounds))
            if (a.state != b.state) append("   (was").append(a.state.ifEmpty { " plain" }).append(')')
            append('\n')
        }
    }.trimEnd()

    companion object {
        fun of(from: Snapshot, to: Snapshot): Diff {
            val a = from.byId
            val b = to.byId
            return Diff(
                from, to,
                added = to.elements.filter { it.id !in a },
                removed = from.elements.filter { it.id !in b },
                changed = to.elements.mapNotNull { e -> a[e.id]?.takeIf { it.state != e.state }?.let { it to e } },
            )
        }
    }
}

/** Contract §4. */
enum class Verdict(val word: String) {
    DONE("done"), NO_EFFECT("no_effect"), BLOCKED_BY_POPUP("blocked_by_popup"), UNKNOWN("unknown")
}

/** What the screen events said happened while an action ran. */
data class EventCounts(val any: Long = 0, val scrolled: Long = 0, val textChanged: Long = 0, val windows: Long = 0) {
    operator fun minus(o: EventCounts) = EventCounts(any - o.any, scrolled - o.scrolled, textChanged - o.textChanged, windows - o.windows)
}

object Verdicts {
    /**
     * [expectText]: for typing, the text the field should now hold (null when it can't be read back, e.g. a
     * password). [events]: events seen while acting.
     */
    /** Does a field hold [expect]? Line ends and trailing spaces don't count; null when the value was cut short. */
    fun sameText(now: String, expect: String): Boolean? {
        if (now.length > 500 && now.endsWith("…")) return null
        fun n(s: String) = s.replace("\r\n", "\n").replace('\r', '\n').trimEnd()
        return n(now) == n(expect)
    }

    fun judge(kind: String, diff: Diff, events: EventCounts, targetId: String?, expectText: String? = null): Verdict {
        val unreadable = diff.from.elements.isEmpty() && diff.to.elements.isEmpty()
        return when (kind) {
            "type", "clear" -> {
                val now = targetId?.let { diff.to.byId[it] }
                val held = now?.value
                val same = if (expectText != null && held != null) sameText(held, expectText) else null
                when {
                    same != null -> if (same) Verdict.DONE else Verdict.NO_EFFECT
                    events.textChanged > 0 || !diff.isEmpty -> Verdict.DONE
                    else -> Verdict.NO_EFFECT
                }
            }
            "scroll" -> when {
                !diff.isEmpty -> Verdict.DONE
                events.scrolled > 0 -> Verdict.DONE
                unreadable -> Verdict.UNKNOWN
                else -> Verdict.NO_EFFECT
            }
            else -> when {
                !diff.isEmpty -> Verdict.DONE
                unreadable -> Verdict.UNKNOWN
                // Events but no visible change: an animation, a sound, a web page that isn't in the tree.
                events.any > 0 -> Verdict.UNKNOWN
                else -> Verdict.NO_EFFECT
            }
        }
    }
}
