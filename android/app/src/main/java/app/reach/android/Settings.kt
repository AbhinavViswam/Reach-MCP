package app.reach.android

import android.app.Application
import android.content.Context
import android.util.Base64
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

    fun init(c: Context) { ctx = c.applicationContext }

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

    fun feature(key: String, default: Boolean): Boolean = prefs.getBoolean("feature.$key", default)
    fun setFeature(key: String, on: Boolean) = prefs.edit().putBoolean("feature.$key", on).apply()

    const val DEFAULT_PORT = 8765
}
