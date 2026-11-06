package app.reach.android

import android.app.Application
import android.content.Context
import android.util.Base64
import app.reach.android.privacy.AppRule
import app.reach.android.privacy.Audit
import app.reach.android.privacy.Privacy
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.security.SecureRandom

class ReachApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Settings.init(this)
    }
}

/** The user's choices. The token is made on the phone and never leaves it except when the user copies it. */
object Settings {
    private lateinit var ctx: Context
    private val prefs by lazy { ctx.getSharedPreferences("reach", Context.MODE_PRIVATE) }

    fun init(c: Context) {
        ctx = c.applicationContext
        app.reach.android.access.Access.app = ctx
        Audit.init(ctx.filesDir)
        Privacy.userRules = loadRules()
        _paused.value = prefs.getBoolean("paused", false)
    }

    val token: String
        get() = prefs.getString("token", null) ?: newToken()

    fun newToken(): String {
        val bytes = ByteArray(24).also { SecureRandom().nextBytes(it) }
        val t = Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        prefs.edit().putString("token", t).apply()
        return t
    }

    var port: Int
        get() = prefs.getInt("port", DEFAULT_PORT)
        set(v) = prefs.edit().putInt("port", v).apply()

    /** Off: only 127.0.0.1 (USB with adb forward). On: the phone's Wi-Fi address too. */
    var wifi: Boolean
        get() = prefs.getBoolean("wifi", false)
        set(v) = prefs.edit().putBoolean("wifi", v).apply()

    /** On: devices on your Tailscale network (100.x) may connect too, nothing else beyond USB. */
    var tailscale: Boolean
        get() = prefs.getBoolean("tailscale", false)
        set(v) = prefs.edit().putBoolean("tailscale", v).apply()

    /** The PC this phone's agent may use (Reach for Windows, reach.exe --http). USB: adb reverse tcp:8766 tcp:8766. */
    var pcUrl: String
        get() = prefs.getString("pc_url", null) ?: "http://127.0.0.1:8766"
        set(v) = prefs.edit().putString("pc_url", v).apply()
    var pcToken: String
        get() = prefs.getString("pc_token", null).orEmpty()
        set(v) = prefs.edit().putString("pc_token", v).apply()

    fun feature(key: String, default: Boolean): Boolean = prefs.getBoolean("feature.$key", default)
    fun setFeature(key: String, on: Boolean) = prefs.edit().putBoolean("feature.$key", on).apply()

    /** The kill switch: while paused every call is refused (spec/POLICY.md). */
    private val _paused = MutableStateFlow(false)
    val paused: StateFlow<Boolean> = _paused.asStateFlow()

    fun setPaused(on: Boolean, by: String) {
        if (_paused.value == on) return
        prefs.edit().putBoolean("paused", on).apply()
        _paused.value = on
        Audit.note(if (on) "paused by the user ($by)" else "resumed by the user ($by)")
    }

    /** App rules the user set, package → rule ("pkg=rule" lines). */
    private fun loadRules(): Map<String, AppRule> =
        prefs.getString("apps", "").orEmpty().lineSequence().mapNotNull { l ->
            val i = l.indexOf('=')
            if (i <= 0) null else l.substring(0, i) to AppRule.of(l.substring(i + 1))
        }.toMap()

    fun setRule(pkg: String, rule: AppRule?) {
        val m = Privacy.userRules.toMutableMap()
        if (rule == null) m.remove(pkg) else m[pkg] = rule
        prefs.edit().putString("apps", m.entries.joinToString(LINE) { "${it.key}=${it.value.key}" }).apply()
        Privacy.userRules = m
    }

    private val LINE = 10.toChar().toString()

    const val DEFAULT_PORT = 8765
}
