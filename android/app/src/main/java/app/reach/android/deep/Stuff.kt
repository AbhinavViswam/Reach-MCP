package app.reach.android.deep

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.provider.Settings as AndroidSettings
import app.reach.android.Feature
import app.reach.android.mcp.Content
import app.reach.android.mcp.Tool
import app.reach.android.mcp.ToolError
import app.reach.android.mcp.ToolResult
import app.reach.android.mcp.bool
import app.reach.android.mcp.int
import app.reach.android.mcp.schema
import app.reach.android.mcp.str
import app.reach.android.privacy.Approvals
import app.reach.android.privacy.Mask
import app.reach.android.screen.Actor
import app.reach.android.screen.ViewText
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonObject
import java.io.File

// ================================================================================================ links

class LinksTool(private val c: Context) : Tool {
    override val name = "links"
    override val description = "Open things directly instead of tapping through menus: a web page or deep link (open), " +
        "share text to an app (share), or a Settings page by name (settings: wifi, bluetooth, display, sound, battery, apps, " +
        "notifications, location, accessibility, date, language, storage, developer, app=<package> for one app's info)."
    override val inputSchema = schema {
        prop("action", "string", "open, share or settings", enum = listOf("open", "share", "settings"))
        prop("url", "string", "open: https://…, geo:…, tel:…, mailto:… or an app's own link (whatsapp://…)")
        prop("package", "string", "open/share: send it to this app; settings: the app whose info page to open")
        prop("text", "string", "share: what to share")
        prop("page", "string", "settings: which page")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult = when (args.str("action")) {
        "open" -> {
            val url = args.str("url") ?: throw ToolError("bad_args", "open needs url")
            val i = Intent(Intent.ACTION_VIEW, Uri.parse(url))
            args.str("package")?.let { i.setPackage(it) }
            if (i.resolveActivity(c.packageManager) == null && args.str("package") == null) {
                throw ToolError("not_found", "no app on the phone opens '$url'")
            }
            Deep.start(c, i)
            ToolResult.text("verdict: done · opened $url (observe to see the screen)")
        }
        "share" -> {
            val text = args.str("text") ?: throw ToolError("bad_args", "share needs text")
            val i = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text)
            val pkg = args.str("package")
            if (pkg != null) { i.setPackage(pkg); Deep.start(c, i) } else Deep.start(c, Intent.createChooser(i, null))
            ToolResult.text("verdict: done · share sheet open${pkg?.let { " in $it" } ?: ""}; the app's own send button still has to be pressed")
        }
        "settings" -> {
            val pkg = args.str("package")
            val page = args.str("page")?.lowercase()
            val i = if (pkg != null || page == "app") {
                Intent(AndroidSettings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${pkg ?: throw ToolError("bad_args", "page app needs package")}"))
            } else {
                Intent(PAGES[page] ?: throw ToolError("bad_args", "page must be one of ${PAGES.keys.joinToString()}"))
            }
            Deep.start(c, i)
            ToolResult.text("verdict: done · opened settings ${pkg ?: page}")
        }
        else -> throw ToolError("bad_args", "action must be open, share or settings")
    }

    companion object {
        val PAGES = linkedMapOf(
            "wifi" to AndroidSettings.ACTION_WIFI_SETTINGS, "bluetooth" to AndroidSettings.ACTION_BLUETOOTH_SETTINGS,
            "display" to AndroidSettings.ACTION_DISPLAY_SETTINGS, "sound" to AndroidSettings.ACTION_SOUND_SETTINGS,
            "battery" to Intent.ACTION_POWER_USAGE_SUMMARY, "apps" to AndroidSettings.ACTION_APPLICATION_SETTINGS,
            "notifications" to AndroidSettings.ACTION_ALL_APPS_NOTIFICATION_SETTINGS, "location" to AndroidSettings.ACTION_LOCATION_SOURCE_SETTINGS,
            "accessibility" to AndroidSettings.ACTION_ACCESSIBILITY_SETTINGS, "date" to AndroidSettings.ACTION_DATE_SETTINGS,
            "language" to AndroidSettings.ACTION_LOCALE_SETTINGS, "storage" to AndroidSettings.ACTION_INTERNAL_STORAGE_SETTINGS,
            "developer" to AndroidSettings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS, "network" to AndroidSettings.ACTION_WIRELESS_SETTINGS,
            "main" to AndroidSettings.ACTION_SETTINGS,
        )
    }
}

// ================================================================================================ files

/** Which paths the files tool may touch (spec/POLICY.md "files outside chosen folders: off"). Plain Kotlin, tested. */
internal object Fence {
    val FOLDERS = listOf("Download", "Documents", "Pictures", "DCIM", "Movies", "Music", "Reach")

    /** [path] ("Download/a.txt", "/storage/emulated/0/Download/a.txt") as a file inside an allowed folder, else an error. */
    fun resolve(root: File, path: String): File {
        val clean = path.trim().removePrefix(root.path).trimStart('/')
        if (clean.isEmpty()) throw ToolError("bad_args", "give a path inside ${FOLDERS.joinToString()}")
        val f = File(root, clean).canonicalFile
        val ok = FOLDERS.any { d -> val dir = File(root, d).canonicalFile; f == dir || f.path.startsWith(dir.path + File.separator) }
        if (!ok) throw ToolError("blocked", "Reach only works in ${FOLDERS.joinToString()} (on the phone's storage)")
        return f
    }

    fun show(root: File, f: File): String = f.path.removePrefix(root.canonicalPath).trimStart('/')
}

class FilesTool(private val c: Context) : Tool {
    override val name = "files"
    override val description = "Files in Download, Documents, Pictures, DCIM, Movies, Music and Reach: list, read (text, or a picture as an image), " +
        "write (text), move, delete (asks the user), recent_photos. Paths are like Download/report.txt."
    override val timeoutMs = 45_000L
    override val inputSchema = schema {
        prop("action", "string", "list, read, write, move, delete or recent_photos", enum = listOf("list", "read", "write", "move", "delete", "recent_photos"))
        prop("path", "string", "e.g. Download, Documents/notes.txt")
        prop("to", "string", "move: new path")
        prop("text", "string", "write: the text")
        prop("append", "boolean", "write: add to the end instead of replacing (replacing a file asks the user)")
        prop("limit", "integer", "list/recent_photos: how many (default 30)")
        required("action")
    }

    private val root: File get() = Environment.getExternalStorageDirectory().canonicalFile

    override suspend fun call(args: JsonObject): ToolResult {
        Feature.FILES.require(c)
        val limit = (args.int("limit") ?: 30).coerceIn(1, 200)
        return when (args.str("action")) {
            "list" -> {
                val p = args.str("path")
                if (p == null) return ToolResult.text("folders: " + Fence.FOLDERS.filter { File(root, it).exists() }.joinToString())
                val d = Fence.resolve(root, p)
                if (!d.isDirectory) throw ToolError("not_found", "${Fence.show(root, d)} is not a folder")
                val items = d.listFiles().orEmpty().sortedByDescending { it.lastModified() }
                ToolResult.text("${Fence.show(root, d)} · ${items.size} items, newest first\n" + items.take(limit).joinToString("\n") {
                    (if (it.isDirectory) "folder " else "file ") + it.name + (if (it.isFile) " · ${size(it.length())}" else "") + " · " + Deep.ago(it.lastModified())
                })
            }
            "read" -> {
                val f = Fence.resolve(root, args.str("path") ?: throw ToolError("bad_args", "read needs path"))
                if (!f.isFile) throw ToolError("not_found", "${Fence.show(root, f)} not found")
                val ext = f.extension.lowercase()
                if (ext in IMAGES) {
                    val bmp = BitmapFactory.decodeFile(f.path) ?: throw ToolError("internal", "couldn't open the picture")
                    val shot = try { Actor.encode(bmp) } finally { bmp.recycle() }
                    return ToolResult(listOf(Content.Text("${Fence.show(root, f)} · ${size(f.length())}"), Content.Image(shot.base64, "image/jpeg", shot.width, shot.height)))
                }
                if (f.length() > 200_000 && ext !in TEXT) throw ToolError("not_supported", "${Fence.show(root, f)} is ${size(f.length())} and not text")
                val bytes = f.inputStream().use { it.readNBytes(64_000) }
                val text = bytes.toString(Charsets.UTF_8)
                if (text.count { it == '�' } > text.length / 20) throw ToolError("not_supported", "${Fence.show(root, f)} is not a text file")
                val m = Mask.apply(text)
                ToolResult.text("${Fence.show(root, f)} · ${size(f.length())}${if (f.length() > 64_000) " (first 64 KB)" else ""}\n${ViewText.FENCE}\n${m.text}" +
                    if (m.masked) "\n${Mask.NOTE}" else "")
            }
            "write" -> {
                val f = Fence.resolve(root, args.str("path") ?: throw ToolError("bad_args", "write needs path"))
                val text = args.str("text") ?: throw ToolError("bad_args", "write needs text")
                val append = args.bool("append") == true
                if (f.exists() && !append) Approvals.require(c, "replace a file", "${Fence.show(root, f)} (${size(f.length())}) will be overwritten.")
                f.parentFile?.mkdirs()
                if (append) f.appendText(text) else f.writeText(text)
                ToolResult.text("verdict: done · ${if (append) "added to" else "wrote"} ${Fence.show(root, f)} (${size(f.length())})")
            }
            "move" -> {
                val from = Fence.resolve(root, args.str("path") ?: throw ToolError("bad_args", "move needs path and to"))
                val to = Fence.resolve(root, args.str("to") ?: throw ToolError("bad_args", "move needs to"))
                if (!from.exists()) throw ToolError("not_found", "${Fence.show(root, from)} not found")
                if (to.exists()) throw ToolError("exists", "${Fence.show(root, to)} already exists; pick another name")
                to.parentFile?.mkdirs()
                if (!from.renameTo(to)) throw ToolError("internal", "couldn't move it")
                ToolResult.text("verdict: done · moved to ${Fence.show(root, to)}")
            }
            "delete" -> {
                val f = Fence.resolve(root, args.str("path") ?: throw ToolError("bad_args", "delete needs path"))
                if (!f.exists()) throw ToolError("not_found", "${Fence.show(root, f)} not found")
                if (Fence.FOLDERS.any { File(root, it).canonicalFile == f }) throw ToolError("blocked", "Reach won't delete a whole main folder")
                val what = if (f.isDirectory) "the folder ${Fence.show(root, f)} and everything in it" else Fence.show(root, f)
                Approvals.require(c, "delete $what", "This can't be undone on the phone.")
                if (!(if (f.isDirectory) f.deleteRecursively() else f.delete())) throw ToolError("internal", "couldn't delete it")
                ToolResult.text("verdict: done · deleted $what")
            }
            "recent_photos" -> {
                val dirs = listOf("DCIM/Camera", "Pictures/Screenshots", "DCIM/Screenshots", "Pictures").map { File(root, it) }.filter { it.isDirectory }
                val photos = dirs.flatMap { d -> d.listFiles().orEmpty().filter { it.isFile && it.extension.lowercase() in IMAGES } }
                    .distinctBy { it.canonicalPath }.sortedByDescending { it.lastModified() }.take(limit)
                ToolResult.text(if (photos.isEmpty()) "no photos found" else photos.joinToString("\n") { "${Fence.show(root, it)} · ${size(it.length())} · ${Deep.ago(it.lastModified())}" })
            }
            else -> throw ToolError("bad_args", "action must be list, read, write, move, delete or recent_photos")
        }
    }

    private fun size(n: Long) = when {
        n < 1024 -> "$n B"; n < 1024 * 1024 -> "${n / 1024} KB"; else -> "%.1f MB".format(n / 1048576.0)
    }

    companion object {
        val IMAGES = setOf("jpg", "jpeg", "png", "webp", "heic", "gif", "bmp")
        val TEXT = setOf("txt", "md", "csv", "json", "log", "xml", "html", "htm", "kt", "py", "js", "ts")
    }
}

// ================================================================================================ clipboard

class ClipboardTool(private val c: Context) : Tool {
    override val name = "clipboard"
    override val description = "The phone's clipboard: get (Android only lets the app in front read it, so Reach flashes a blank screen for a moment) or set. " +
        "Codes are hidden unless reveal=true (asks)."
    override val inputSchema = schema {
        prop("action", "string", "get or set", enum = listOf("get", "set"))
        prop("text", "string", "set: the text")
        prop("reveal", "boolean", "get: show hidden codes (asks the user)")
        required("action")
    }

    override suspend fun call(args: JsonObject): ToolResult = when (args.str("action")) {
        "set" -> {
            val text = args.str("text") ?: throw ToolError("bad_args", "set needs text")
            kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Main) {
                c.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Reach", text))
            }
            ToolResult.text("verdict: done · clipboard set (${text.length} characters)")
        }
        "get" -> {
            val done = CompletableDeferred<String?>()
            if (app.reach.android.access.Access.service.value == null)
                throw ToolError("no_access", "reading the clipboard needs Reach's screen access on this phone: " + app.reach.android.access.Access.offHint(c))
            ClipActivity.waiting = done
            Deep.start(c, Intent(c, ClipActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NO_ANIMATION))
            val text = withTimeoutOrNull(5_000) { done.await() }
            ClipActivity.waiting = null
            when {
                text == null -> ToolResult.text("clipboard is empty (or Android didn't let Reach read it)")
                else -> ToolResult.text("clipboard\n" + Deep.reveal(c, listOf(text.take(20_000)), args.bool("reveal") == true, "the clipboard"))
            }
        }
        else -> throw ToolError("bad_args", "action must be get or set")
    }
}

/** A see-through screen that reads the clipboard the moment it has focus (the only time Android allows it), then closes. */
class ClipActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus) return
        val clip = runCatching { getSystemService(ClipboardManager::class.java).primaryClip }.getOrNull()
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        waiting?.complete(text)
        finish()
        @Suppress("DEPRECATION") overridePendingTransition(0, 0)
    }

    companion object {
        @Volatile var waiting: CompletableDeferred<String?>? = null
    }
}
