package app.reach.android.screen

/*
 * The screen as plain data (no Android types), so IDs, diffs and verdicts can be tested on the PC.
 * Contract: spec/CONTRACT.md §2–§4.
 */

data class Box(val left: Int, val top: Int, val right: Int, val bottom: Int) {
    val cx: Int get() = (left + right) / 2
    val cy: Int get() = (top + bottom) / 2
    val width: Int get() = right - left
    val height: Int get() = bottom - top
    val area: Long get() = if (isEmpty) 0 else width.toLong() * height
    val isEmpty: Boolean get() = width <= 0 || height <= 0
    fun contains(x: Int, y: Int) = x in left until right && y in top until bottom
}

enum class Role(val word: String) {
    BUTTON("button"), FIELD("field"), PASSWORD("password"), SWITCH("switch"), CHECKBOX("checkbox"),
    RADIO("radio"), TAB("tab"), ITEM("item"), LIST("list"), TEXT("text"), IMAGE("image"), LINK("link");

    val pressable: Boolean get() = this != TEXT && this != LIST && this != IMAGE
}

/** One element as read, before it has an ID. */
data class RawElement(
    val role: Role,
    val label: String,
    val value: String?,
    val resourceId: String?,
    val className: String,
    val bounds: Box,
    val checked: Boolean? = null,
    val selected: Boolean = false,
    val enabled: Boolean = true,
    val focused: Boolean = false,
    val scrollable: Boolean = false,
    val windowId: Int = 0,
)

/** One element with its stable ID. */
data class Element(val id: String, val raw: RawElement) {
    val role get() = raw.role
    val label get() = raw.label
    val value get() = raw.value
    val bounds get() = raw.bounds

    /** The parts a diff compares (everything that can change without the element becoming a different one). */
    val state: String
        get() = buildString {
            raw.value?.let { append("=").append(it) }
            raw.checked?.let { append(if (it) " on" else " off") }
            if (raw.selected) append(" selected")
            if (!raw.enabled) append(" disabled")
            if (raw.focused) append(" focused")
        }
}

/** One read of the screen. [version] is set when it is stored (ScreenState). */
data class Snapshot(
    val app: String?,
    val elements: List<Element>,
    val width: Int,
    val height: Int,
    val landscape: Boolean,
    val keyboardOpen: Boolean,
    val blockedApp: String? = null,
    val version: Int = 0,
    /** Windows drawn over the app (dialogs, permission prompts), by window id, with their bounds. */
    val overlays: List<Overlay> = emptyList(),
    /** Text read from the screenshot was added (elements marked (ocr)). */
    val ocr: Boolean = false,
) {
    val byId: Map<String, Element> by lazy { elements.associateBy { it.id } }

    /** Changes whenever anything a person could notice changes. */
    val signature: Int by lazy { (app.hashCode() * 31) + elements.joinToString("|") { it.id + it.state }.hashCode() }
}

data class Overlay(val windowId: Int, val app: String?, val bounds: Box, val title: String?)
