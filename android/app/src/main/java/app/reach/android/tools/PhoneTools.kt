package app.reach.android.tools

import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import app.reach.android.BuildConfig
import app.reach.android.access.Access
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.privacy.Privacy
import app.reach.android.screen.Actor
import app.reach.android.screen.Ids
import app.reach.android.screen.ScreenReader
import app.reach.android.screen.ViewText
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject

// ================================================================================================ apps

class AppsTool(private val context: Context) : Tool {
    override val name = "apps"
    override val description = "The phone's apps: list (optionally filtered by query), open by name or package " +
        "(replies with the opened screen), current (app in front), info, close."
    override val inputSchema = schema {
        prop("action", "string", "list, open, current, info or close", enum = listOf("list", "open", "current", "info", "close"))
        prop("name", "string", "open/info: app name as shown on the phone, e.g. 'WhatsApp'")
        prop("package", "string", "open/info: package name, e.g. 'com.whatsapp'")
        prop("query", "string", "list: only apps whose name or package contains this")
        required("action")
    }

    private data class App(val label: String, val pkg: String)

    private fun launchable(): List<App> {
        val pm = context.packageManager
        val main = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return pm.queryIntentActivities(main, 0)
            .map { App(it.loadLabel(pm).toString(), it.activityInfo.packageName) }
            .distinctBy { it.pkg }
            .sortedBy { it.label.lowercase() }
    }

    private fun pick(args: JsonObject): App {
        val apps = launchable()
        args.str("package")?.let { p -> return apps.firstOrNull { it.pkg == p } ?: App(p, p) }
        val name = args.str("name")?.let(Ids::normalize) ?: throw ToolError("bad_args", "give name or package")
        val exact = apps.filter { Ids.normalize(it.label) == name }
        val starts = apps.filter { Ids.normalize(it.label).startsWith(name) }
        val contains = apps.filter { Ids.normalize(it.label).contains(name) || it.pkg.lowercase().contains(name.replace(" ", "")) }
        val pool = exact.ifEmpty { starts.ifEmpty { contains } }
        if (pool.isEmpty()) {
            val close = apps.sortedByDescending { Ids.similarity(Ids.normalize(it.label), name) }.take(5)
            throw ToolError("not_found", "no app named \"$name\"; closest: ${close.joinToString { "${it.label} (${it.pkg})" }}")
        }
        if (pool.size > 1 && exact.isEmpty()) {
            throw ToolError("ambiguous", "\"$name\" matches ${pool.take(6).joinToString { "${it.label} (${it.pkg})" }}; use package")
        }
        return pool.first()
    }

    override suspend fun call(args: JsonObject): ToolResult = when (args.str("action")) {
        "list" -> {
            val q = args.str("query")?.lowercase()
            val apps = launchable().filter { q == null || it.label.lowercase().contains(q) || it.pkg.contains(q) }
            ToolResult.text("${apps.size} apps\n" + apps.joinToString("\n") { "${it.label} · ${it.pkg}${blockedNote(it.pkg)}" })
        }
        "open" -> {
            val app = pick(args)
            if (Privacy.isBlocked(app.pkg, app.label)) throw ToolError("blocked_app", "${app.label} is blocked by Reach privacy rules")
            val intent = context.packageManager.getLaunchIntentForPackage(app.pkg)
                ?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED)
                ?: throw ToolError("not_found", "${app.pkg} has no screen to open")
            val before = Screen.read()
            val started = System.currentTimeMillis()
            // From the accessibility service: Android lets it start activities from the background.
            withContext(Dispatchers.Main) { (Access.service.value ?: context).startActivity(intent) }
            Actor.settle(started, quietMs = 500, maxMs = 5_000)
            val after = Screen.read()
            val ok = after.snap.app == app.pkg
            val head = if (ok) "verdict: done · opened ${app.label} (${app.pkg})"
            else "verdict: unknown · asked Android to open ${app.label}; in front now: ${after.snap.app}"
            ToolResult.text("$head\n" + ViewText.full(after.snap).takeIf { before.snap.app != after.snap.app || ok }.orEmpty())
        }
        "current" -> {
            val live = Screen.read()
            ToolResult.text("app ${live.snap.app}${Access.lastWindowClass?.let { " · screen $it" } ?: ""} · v${live.snap.version}")
        }
        "info" -> {
            val app = pick(args)
            val pm = context.packageManager
            val info = try { pm.getPackageInfo(app.pkg, 0) } catch (e: PackageManager.NameNotFoundException) {
                throw ToolError("not_found", "${app.pkg} is not installed")
            }
            ToolResult.text("${app.label} · ${app.pkg} · version ${info.versionName} · " +
                "enabled ${info.applicationInfo?.enabled}${blockedNote(app.pkg)}")
        }
        "close" -> {
            val live = Screen.read()
            // Android does not let one app close another; going home is the honest equivalent.
            val front = live.snap.app
            val target = args.str("package") ?: args.str("name")?.let { runCatching { pick(args).pkg }.getOrNull() }
            if (target != null && target != front) {
                ToolResult.text("verdict: no_effect · $target is not in front ($front is); Android doesn't let apps close other apps")
            } else {
                val o = Screen.checked("key", live, null, "go home (Android doesn't let apps close other apps)") { Actor.key("home") }
                ToolResult.text(o.text)
            }
        }
        else -> throw ToolError("bad_args", "action must be list, open, current, info or close")
    }
}

// ================================================================================================ device

class DeviceTool(private val context: Context, private val serverInfo: () -> String) : Tool {
    override val name = "device"
    override val description = "Phone status: screen on/locked, battery, network, display size and rotation, Android version, " +
        "Reach's own permissions and connection."
    override val inputSchema = schema {}

    override suspend fun call(args: JsonObject): ToolResult {
        val power = context.getSystemService(PowerManager::class.java)
        val lock = context.getSystemService(KeyguardManager::class.java)
        val battery = context.getSystemService(BatteryManager::class.java)
        val level = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        val charging = battery.isCharging
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        val net = when {
            caps == null -> "offline"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "mobile data"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> "vpn"
            else -> "other"
        }
        val s = Access.service.value
        val size = s?.let { ScreenReader.screenSize(it) }
        val lines = listOf(
            "phone ${Build.MANUFACTURER} ${Build.MODEL} · Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})",
            "screen ${if (power.isInteractive) "on" else "off"} · ${if (lock.isKeyguardLocked) "locked" else "unlocked"}" +
                (size?.let { " · ${it.first}×${it.second} ${if (it.first > it.second) "landscape" else "portrait"}" } ?: ""),
            "battery $level%${if (charging) " charging" else ""}",
            "network $net",
            "reach ${BuildConfig.VERSION_NAME} · accessibility ${if (s != null) "on" else "OFF (turn on in Settings › Accessibility › Reach)"}",
            serverInfo(),
            "screen control: the agent shares the screen with the user; actions are visible on the phone",
        )
        val warn = if (lock.isKeyguardLocked) "\nnote: the phone is locked; most actions need the user to unlock it first" else ""
        return ToolResult.text(lines.joinToString("\n") + warn)
    }
}
