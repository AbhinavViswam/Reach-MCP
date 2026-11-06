package app.reach.android.screen

/**
 * The views the agent has been sent, so `since: 7` can return only the changes after v7, and every element ID seen
 * recently, so a gone ID can be found again (Ids.refind). Plain Kotlin, tested on the PC.
 */
class ScreenState(private val keepViews: Int = 8, private val keepIds: Int = 3000) {
    private val views = ArrayDeque<Snapshot>()
    private val known = object : LinkedHashMap<String, Element>(256, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Element>?) = size > keepIds
    }
    private var next = 1

    /** The read, with its version. The same screen as the last one keeps the last version number. */
    @Synchronized
    fun store(s: Snapshot): Snapshot {
        val last = views.lastOrNull()
        if (last != null && last.signature == s.signature && last.overlays == s.overlays && last.keyboardOpen == s.keyboardOpen) {
            return last
        }
        val v = s.copy(version = next++)
        views.addLast(v)
        while (views.size > keepViews) views.removeFirst()
        v.elements.forEach { known[it.id] = it }
        return v
    }

    @Synchronized fun get(version: Int): Snapshot? = views.firstOrNull { it.version == version }
    @Synchronized fun latest(): Snapshot? = views.lastOrNull()
    @Synchronized fun knownElement(id: String): Element? = known[id]

    @Synchronized fun clear() {
        views.clear()
        known.clear()
    }
}
