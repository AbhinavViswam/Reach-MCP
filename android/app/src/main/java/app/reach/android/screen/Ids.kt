package app.reach.android.screen

/**
 * Stable element IDs (spec/CONTRACT.md §2): a hash of app + role + resource id + class + label. Value and position
 * are left out, so typing in a field or scrolling a list keeps every ID. Identical elements (three "Reply" buttons)
 * get -2, -3 in reading order.
 */
object Ids {
    fun assign(app: String?, raws: List<RawElement>): List<Element> {
        val seen = HashMap<String, Int>()
        return raws.map { r ->
            val base = "e" + hash(key(app, r))
            val n = (seen[base] ?: 0) + 1
            seen[base] = n
            Element(if (n == 1) base else "$base-$n", r)
        }
    }

    internal fun key(app: String?, r: RawElement): String =
        listOf(app.orEmpty(), r.role.word, r.resourceId.orEmpty(), r.className.substringAfterLast('.'), normalize(r.label))
            .joinToString("\u0001")

    /** Lower case, spaces squeezed. Numbers are kept: "Page 2" and "Page 3" are different buttons. */
    internal fun normalize(s: String) = s.lowercase().replace(Regex("\\s+"), " ").trim()

    /** 4 base-36 characters from FNV-1a 32. Clashes inside one screen are rare and still get a -2 suffix. */
    internal fun hash(s: String): String {
        var h = 0x811C9DC5.toInt()
        for (c in s) {
            h = h xor c.code
            h *= 0x01000193
        }
        val n = (h.toLong() and 0xFFFFFFFFL) % (36L * 36 * 36 * 36)
        return n.toString(36).padStart(4, '0')
    }

    /**
     * An ID that is gone: the same element as it is now (label changed, e.g. "3 unread" → "4 unread").
     * Same role and resource id/class are required; then the closest label and position win.
     */
    fun refind(old: Element, now: Snapshot): Element? {
        val candidates = now.elements.filter {
            it.role == old.role && it.raw.className == old.raw.className && it.raw.resourceId == old.raw.resourceId
        }
        if (candidates.isEmpty()) return null
        val oldLabel = normalize(old.label)
        val scored = candidates.map { c ->
            val sim = similarity(oldLabel, normalize(c.label))
            val dist = Math.hypot((c.bounds.cx - old.bounds.cx).toDouble(), (c.bounds.cy - old.bounds.cy).toDouble())
            val near = 1.0 - (dist / 1500.0).coerceAtMost(1.0)
            c to (sim * 0.7 + near * 0.3)
        }
        val (best, score) = scored.maxBy { it.second }
        // Without a resource id the label is all there is; ask for more of it.
        val need = if (old.raw.resourceId != null) 0.45 else 0.6
        return best.takeIf { score >= need }
    }

    /** 1 = same, 0 = nothing alike (shared-bigram Dice score). */
    internal fun similarity(a: String, b: String): Double {
        if (a == b) return 1.0
        if (a.length < 2 || b.length < 2) return 0.0
        val ga = a.windowed(2).groupingBy { it }.eachCount()
        val gb = b.windowed(2).groupingBy { it }.eachCount()
        val shared = ga.entries.sumOf { (k, v) -> minOf(v, gb[k] ?: 0) }
        return 2.0 * shared / (a.length - 1 + b.length - 1)
    }
}
