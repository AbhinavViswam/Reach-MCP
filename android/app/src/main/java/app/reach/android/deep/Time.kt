package app.reach.android.deep

import android.app.AlarmManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.provider.AlarmClock
import android.provider.CalendarContract
import app.reach.android.Feature
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.arr
import app.reach.android.mcp.int
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeParseException
import java.util.TimeZone

/** Dates the agent gives: "today", "tomorrow", "2026-11-07", "2026-11-07T15:00", "15:00" (today). Plain Kotlin, tested. */
internal object When {
    fun date(s: String, today: LocalDate = LocalDate.now()): LocalDate = when (s.trim().lowercase()) {
        "today" -> today
        "tomorrow" -> today.plusDays(1)
        "yesterday" -> today.minusDays(1)
        else -> try { LocalDate.parse(s.trim().take(10)) } catch (e: DateTimeParseException) {
            throw ToolError("bad_args", "date '$s' not understood; use YYYY-MM-DD, today or tomorrow")
        }
    }

    fun dateTime(s: String, today: LocalDate = LocalDate.now()): LocalDateTime {
        val t = s.trim()
        return try {
            when {
                t.contains('T') -> LocalDateTime.parse(t.take(16))
                t.contains(' ') && t.length >= 16 -> LocalDateTime.parse(t.take(16).replace(' ', 'T'))
                t.matches(Regex("\\d{1,2}:\\d{2}")) -> LocalDateTime.of(today, LocalTime.parse(t.padStart(5, '0')))
                else -> date(t, today).atStartOfDay()
            }
        } catch (e: DateTimeParseException) {
            throw ToolError("bad_args", "time '$s' not understood; use YYYY-MM-DDTHH:MM (phone's local time)")
        }
    }

    fun millis(t: LocalDateTime): Long = t.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
}

// ================================================================================================ calendar

class CalendarTool(private val c: Context) : Tool {
    override val name = "calendar"
    override val description = "Calendar: list events (default the next 7 days) or add one. Times are the phone's local time, e.g. 2026-11-07T15:00."
    override val inputSchema = schema {
        prop("action", "string", "list or add", enum = listOf("list", "add"))
        prop("from", "string", "list: first day (today, tomorrow or YYYY-MM-DD; default today)")
        prop("days", "integer", "list: how many days (default 7, max 60)")
        prop("query", "string", "list: only events whose title contains this")
        prop("title", "string", "add: what it is")
        prop("start", "string", "add: start, YYYY-MM-DDTHH:MM, or a date for an all-day event")
        prop("end", "string", "add: end (default start + duration_min)")
        prop("duration_min", "integer", "add: length in minutes (default 60)")
        prop("location", "string", "add: where")
        prop("notes", "string", "add: description")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.CALENDAR.require(c)
        return when (args.str("action")) {
            "list" -> list(args)
            "add" -> add(args)
            else -> throw ToolError("bad_args", "action must be list or add")
        }
    }

    private fun list(args: JsonObject): ToolResult {
        val from = When.date(args.str("from") ?: "today")
        val days = (args.int("days") ?: 7).coerceIn(1, 60)
        val q = args.str("query")?.lowercase()
        val begin = When.millis(from.atStartOfDay())
        val end = When.millis(from.plusDays(days.toLong()).atStartOfDay())
        val uri = CalendarContract.Instances.CONTENT_URI.buildUpon().also {
            android.content.ContentUris.appendId(it, begin)
            android.content.ContentUris.appendId(it, end)
        }.build()
        val rows = ArrayList<String>()
        c.contentResolver.query(
            uri,
            arrayOf(CalendarContract.Instances.TITLE, CalendarContract.Instances.BEGIN, CalendarContract.Instances.END,
                CalendarContract.Instances.ALL_DAY, CalendarContract.Instances.EVENT_LOCATION, CalendarContract.Instances.CALENDAR_DISPLAY_NAME),
            null, null, "${CalendarContract.Instances.BEGIN} ASC",
        )?.use { cur ->
            while (cur.moveToNext() && rows.size < 100) {
                val title = cur.getString(0).orEmpty()
                if (q != null && !title.lowercase().contains(q)) continue
                val allDay = cur.getInt(3) == 1
                val whenText = if (allDay) Deep.time(cur.getLong(1)).substringBeforeLast(' ') + " (all day)"
                else Deep.time(cur.getLong(1)) + "–" + Deep.time(cur.getLong(2)).substringAfterLast(' ')
                val loc = cur.getString(4)?.takeIf { it.isNotBlank() }?.let { " · $it" } ?: ""
                rows += "$whenText · $title$loc · ${cur.getString(5).orEmpty()}"
            }
        }
        val head = "events $from to ${from.plusDays(days - 1L)}"
        return ToolResult.text(if (rows.isEmpty()) "$head: none" else "$head\n${app.reach.android.screen.ViewText.FENCE}\n" + rows.joinToString("\n"))
    }

    private fun add(args: JsonObject): ToolResult {
        val title = args.str("title") ?: throw ToolError("bad_args", "add needs title")
        val startText = args.str("start") ?: throw ToolError("bad_args", "add needs start")
        val allDay = !startText.contains('T') && !startText.contains(':')
        val start = When.dateTime(startText)
        val end = args.str("end")?.let { When.dateTime(it) }
            ?: if (allDay) start.plusDays(1) else start.plusMinutes((args.int("duration_min") ?: 60).toLong())
        val cal = writableCalendar() ?: throw ToolError("not_found", "no calendar on the phone can take new events")
        val v = ContentValues().apply {
            put(CalendarContract.Events.CALENDAR_ID, cal.first)
            put(CalendarContract.Events.TITLE, title)
            put(CalendarContract.Events.DTSTART, if (allDay) start.toLocalDate().atStartOfDay(ZoneId.of("UTC")).toInstant().toEpochMilli() else When.millis(start))
            put(CalendarContract.Events.DTEND, if (allDay) end.toLocalDate().atStartOfDay(ZoneId.of("UTC")).toInstant().toEpochMilli() else When.millis(end))
            put(CalendarContract.Events.ALL_DAY, if (allDay) 1 else 0)
            put(CalendarContract.Events.EVENT_TIMEZONE, if (allDay) "UTC" else TimeZone.getDefault().id)
            args.str("location")?.let { put(CalendarContract.Events.EVENT_LOCATION, it) }
            args.str("notes")?.let { put(CalendarContract.Events.DESCRIPTION, it) }
        }
        c.contentResolver.insert(CalendarContract.Events.CONTENT_URI, v) ?: throw ToolError("internal", "the calendar refused the event")
        return ToolResult.text("verdict: done · added \"$title\" ${if (allDay) "on ${start.toLocalDate()} (all day)" else "$start to $end"} to ${cal.second}")
    }

    /** The primary calendar the user can write to, else any writable visible one. */
    private fun writableCalendar(): Pair<Long, String>? {
        var best: Pair<Long, String>? = null
        c.contentResolver.query(
            CalendarContract.Calendars.CONTENT_URI,
            arrayOf(CalendarContract.Calendars._ID, CalendarContract.Calendars.CALENDAR_DISPLAY_NAME, CalendarContract.Calendars.IS_PRIMARY,
                CalendarContract.Calendars.CALENDAR_ACCESS_LEVEL, CalendarContract.Calendars.VISIBLE),
            null, null, null,
        )?.use { cur ->
            while (cur.moveToNext()) {
                if (cur.getInt(3) < CalendarContract.Calendars.CAL_ACCESS_CONTRIBUTOR || cur.getInt(4) != 1) continue
                val row = cur.getLong(0) to cur.getString(1).orEmpty()
                if (cur.getInt(2) == 1) return row
                if (best == null) best = row
            }
        }
        return best
    }
}

// ================================================================================================ alarms

class AlarmsTool(private val c: Context) : Tool {
    override val name = "alarms"
    override val description = "Alarms and timers through the phone's clock app: set an alarm, start a timer, show alarms, or read the next alarm."
    override val inputSchema = schema {
        prop("action", "string", "set, timer, show or next", enum = listOf("set", "timer", "show", "next"))
        prop("time", "string", "set: HH:MM, 24-hour (e.g. 06:30)")
        prop("days", "array", "set: repeat on these days (mon, tue, …); empty = once") { put("items", buildJsonObjectString()) }
        prop("seconds", "integer", "timer: length in seconds")
        prop("label", "string", "set/timer: a name for it")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult = when (args.str("action")) {
        "set" -> {
            val t = args.str("time") ?: throw ToolError("bad_args", "set needs time HH:MM")
            val lt = try { LocalTime.parse(t.padStart(5, '0')) } catch (e: DateTimeParseException) { throw ToolError("bad_args", "time must be HH:MM, 24-hour") }
            val days = args.arr("days")?.mapNotNull { (it as? JsonPrimitive)?.content?.let(::dayOf) }.orEmpty()
            val i = Intent(AlarmClock.ACTION_SET_ALARM)
                .putExtra(AlarmClock.EXTRA_HOUR, lt.hour).putExtra(AlarmClock.EXTRA_MINUTES, lt.minute)
                .putExtra(AlarmClock.EXTRA_SKIP_UI, true)
            args.str("label")?.let { i.putExtra(AlarmClock.EXTRA_MESSAGE, it) }
            if (days.isNotEmpty()) i.putIntegerArrayListExtra(AlarmClock.EXTRA_DAYS, ArrayList(days))
            Deep.start(c, i)
            val label = args.str("label")?.let { " \"$it\"" } ?: ""
            val repeat = if (days.isNotEmpty()) " repeating ${args.arr("days")}" else ""
            ToolResult.text("verdict: done · asked the clock app for an alarm at $lt$label$repeat (alarms next shows the next one)")
        }
        "timer" -> {
            val s = args.int("seconds")?.takeIf { it in 1..86_400 } ?: throw ToolError("bad_args", "timer needs seconds (1–86400)")
            val i = Intent(AlarmClock.ACTION_SET_TIMER).putExtra(AlarmClock.EXTRA_LENGTH, s).putExtra(AlarmClock.EXTRA_SKIP_UI, true)
            args.str("label")?.let { i.putExtra(AlarmClock.EXTRA_MESSAGE, it) }
            Deep.start(c, i)
            ToolResult.text("verdict: done · timer for ${s / 60} min ${s % 60} s started")
        }
        "show" -> {
            Deep.start(c, Intent(AlarmClock.ACTION_SHOW_ALARMS))
            ToolResult.text("verdict: done · clock app opened on its alarms (observe to read them)")
        }
        "next" -> {
            val next = c.getSystemService(AlarmManager::class.java).nextAlarmClock
            ToolResult.text(next?.let { "next alarm: ${Deep.time(it.triggerTime)}" } ?: "no alarm set")
        }
        else -> throw ToolError("bad_args", "action must be set, timer, show or next")
    }

    private fun dayOf(s: String): Int? = when (s.lowercase().take(3)) {
        "sun" -> java.util.Calendar.SUNDAY; "mon" -> java.util.Calendar.MONDAY; "tue" -> java.util.Calendar.TUESDAY
        "wed" -> java.util.Calendar.WEDNESDAY; "thu" -> java.util.Calendar.THURSDAY; "fri" -> java.util.Calendar.FRIDAY
        "sat" -> java.util.Calendar.SATURDAY; else -> null
    }

    private fun buildJsonObjectString() = kotlinx.serialization.json.buildJsonObject { put("type", JsonPrimitive("string")) }
}
