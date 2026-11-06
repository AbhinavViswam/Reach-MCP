package app.reach.android.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings as AndroidSettings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.reach.android.BuildConfig
import app.reach.android.Feature
import app.reach.android.Settings
import app.reach.android.access.Access
import app.reach.android.server.ServerService

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { ReachTheme { Home() } }
    }
}

@Composable
private fun ReachTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val ctx = LocalContext.current
    val scheme = when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> if (dark) dynamicDarkColorScheme(ctx) else dynamicLightColorScheme(ctx)
        dark -> darkColorScheme(primary = Color(0xFF5EE6C8), onPrimary = Color(0xFF00382E))
        else -> lightColorScheme(primary = Color(0xFF006B58), onPrimary = Color.White)
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

@Composable
private fun Home() {
    val ctx = LocalContext.current
    val access by Access.service.collectAsState()
    val status by ServerService.status.collectAsState()
    val paused by Settings.paused.collectAsState()
    var wifi by remember { mutableStateOf(Settings.wifi) }
    var tailscale by remember { mutableStateOf(Settings.tailscale) }
    var token by remember { mutableStateOf(Settings.token) }
    var showToken by remember { mutableStateOf(false) }
    val notifyAsk = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ServerService.start(ctx) }

    fun copy(label: String, text: String) {
        ctx.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText(label, text))
        Toast.makeText(ctx, "Copied", Toast.LENGTH_SHORT).show()
    }

    fun start() {
        if (Build.VERSION.SDK_INT >= 33) notifyAsk.launch(Manifest.permission.POST_NOTIFICATIONS) else ServerService.start(ctx)
    }

    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            Modifier.safeDrawingPadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("Reach", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
            Text("Let your AI agent see and use this phone. Version ${BuildConfig.VERSION_NAME}",
                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)

            // ---------------------------------------------------------------------------------- step 1
            Section("1 · Screen access", ok = access != null, okText = "On", offText = "Off") {
                Text(
                    when {
                        access != null -> "Reach can read the screen and tap, type and scroll when your agent asks."
                        Access.enabledInSettings(ctx) -> "Android stopped Reach's screen access (this happens after changing a permission " +
                            "such as All files access). In Accessibility, turn Reach off and on again."
                        else -> "Turn on Reach in Accessibility. It only reads the screen when your agent asks, and never reads passwords."
                    },
                    style = MaterialTheme.typography.bodyMedium,
                )
                if (access == null) {
                    Button(onClick = { ctx.startActivity(Intent(AndroidSettings.ACTION_ACCESSIBILITY_SETTINGS)) }) { Text("Open Accessibility settings") }
                    Text("Android says \"Restricted setting\"? Open App info, tap ⋮ (top right), choose \"Allow restricted settings\", then try again.",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    TextButton(onClick = {
                        ctx.startActivity(Intent(AndroidSettings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${ctx.packageName}")))
                    }) { Text("Open App info") }
                }
            }

            // ---------------------------------------------------------------------------------- step 2
            Section("2 · Server", ok = status.running && !paused, okText = "Running", offText = if (status.running) "Paused" else "Stopped") {
                val line = when {
                    status.error != null -> status.error!!
                    status.running && paused -> "Paused: every call is refused until you resume. Also from the notification or the Reach Quick Settings tile."
                    status.running -> "Port ${status.port} · ${status.calls} calls" + (status.lastCall?.let { " · last: $it" } ?: "")
                    else -> "Start it when you want your agent to use the phone. Stop it any time here or from the notification."
                }
                Text(line, style = MaterialTheme.typography.bodyMedium,
                    color = if (status.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (status.running) {
                        if (paused) Button(onClick = { Settings.setPaused(false, "app") }) { Text("Resume") }
                        else Button(onClick = { Settings.setPaused(true, "app") }) { Text("Pause") }
                        OutlinedButton(onClick = { ServerService.stop(ctx) }) { Text("Stop") }
                    } else Button(onClick = { start() }) { Text("Start") }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Wi-Fi access", style = MaterialTheme.typography.titleSmall)
                        Text(if (wifi) "Devices on this Wi-Fi with the token can connect." else "Off: only USB (adb forward) can connect.",
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(checked = wifi, onCheckedChange = {
                        wifi = it
                        Settings.wifi = it
                        if (status.running) ServerService.restart(ctx)
                    })
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Tailscale access", style = MaterialTheme.typography.titleSmall)
                        Text(if (tailscale) "Your own Tailscale devices with the token can connect, from anywhere." +
                                (status.tailscaleAddress?.let { " This phone: $it" } ?: "")
                            else "Off. Needs the Tailscale app; safer than Wi-Fi access when you're away from home.",
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(checked = tailscale, onCheckedChange = {
                        tailscale = it
                        Settings.tailscale = it
                        if (status.running) ServerService.restart(ctx)
                    })
                }
            }

            // ---------------------------------------------------------------------------------- step 3
            Section("3 · Connect your agent", ok = null) {
                val port = Settings.port
                Text("Token", style = MaterialTheme.typography.titleSmall)
                Mono(if (showToken) token else "•".repeat(12) + token.takeLast(4))
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TextButton(onClick = { showToken = !showToken }) { Text(if (showToken) "Hide" else "Show") }
                    TextButton(onClick = { copy("Reach token", token) }) { Text("Copy") }
                    TextButton(onClick = { token = Settings.newToken() }) { Text("New token") }
                }
                Text("USB (on the PC)", style = MaterialTheme.typography.titleSmall)
                val usb = "adb forward tcp:$port tcp:$port\n" +
                    "claude mcp add --transport http reach-android http://127.0.0.1:$port/mcp --header \"Authorization: Bearer $token\""
                Mono(usb.replace(token, if (showToken) token else "<token>"))
                TextButton(onClick = { copy("Reach USB setup", usb) }) { Text("Copy commands") }
                if (wifi) {
                    val ip = status.wifiAddress ?: ServerService.wifiAddress(ctx) ?: "<phone address>"
                    val cmd = "claude mcp add --transport http reach-android http://$ip:$port/mcp --header \"Authorization: Bearer $token\""
                    Text("Wi-Fi", style = MaterialTheme.typography.titleSmall)
                    Mono(cmd.replace(token, if (showToken) token else "<token>"))
                    TextButton(onClick = { copy("Reach Wi-Fi setup", cmd) }) { Text("Copy command") }
                }
                Text("Approve PC actions here", style = MaterialTheme.typography.titleSmall)
                Text("In Reach for Windows settings, turn on \"Ask on my phone\" and paste this token. Over USB keep " +
                    "http://127.0.0.1:$port (after adb forward); over Wi-Fi use this phone's address.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            Abilities()
            AppRules()
            Activity()
            Flows()
            YourPc()

            Text("Never read: password fields. Blocked unless you allow them: banking, payment, wallet, password and authenticator apps. " +
                "Sending, calling, replying and deleting always ask you here first. Codes in messages are hidden. No telemetry.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.size(8.dp))
        }
    }
}

@Composable
internal fun Section(title: String, ok: Boolean?, okText: String = "", offText: String = "", body: @Composable () -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                if (ok != null) {
                    val c = if (ok) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
                    Box(Modifier.size(8.dp).background(c, CircleShape))
                    Spacer(Modifier.width(6.dp))
                    Text(if (ok) okText else offText, style = MaterialTheme.typography.labelLarge, color = c)
                }
            }
            body()
        }
    }
}

@Composable
private fun Mono(text: String) {
    SelectionContainer {
        Text(
            text,
            fontFamily = FontFamily.Monospace,
            fontSize = 12.sp,
            modifier = Modifier.fillMaxWidth()
                .background(MaterialTheme.colorScheme.surfaceContainerHighest, RoundedCornerShape(10.dp))
                .padding(10.dp),
        )
    }
}

/** Step 4: the extra abilities, each with its own switch and the Android permission it needs (Features.kt). */
@Composable
private fun Abilities() {
    val ctx = LocalContext.current
    // Permissions and special access change in other screens: read them again whenever this one comes back.
    var tick by remember { mutableStateOf(0) }
    androidx.lifecycle.compose.LifecycleResumeEffect(Unit) {
        tick++
        onPauseOrDispose { }
    }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { tick++ }

    fun grant(f: Feature) {
        val missing = f.missingPermissions(ctx)
        if (missing.isNotEmpty()) { ask.launch(missing.toTypedArray()); return }
        when (f.special) {
            Feature.Special.NOTIFICATION_ACCESS -> ctx.startActivity(Intent(AndroidSettings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            Feature.Special.ALL_FILES -> ctx.startActivity(
                Intent(AndroidSettings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:${ctx.packageName}")))
            Feature.Special.NONE -> {}
        }
    }

    Section("4 · Extra abilities", ok = null) {
        Text("Turn on only what you want your agent to use. After changing these, reconnect your agent to see the new tools.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        key(tick) {
            Feature.entries.forEach { f ->
                var on by remember { mutableStateOf(f.switchedOn()) }
                val ready = on && f.ready(ctx)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(f.label, style = MaterialTheme.typography.titleSmall)
                        Text(f.about, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        if (on && !ready) {
                            TextButton(onClick = { grant(f) }) { Text(if (f.special == Feature.Special.NONE) "Allow permission" else "Open Android setting") }
                        }
                    }
                    Switch(checked = on, onCheckedChange = {
                        on = it
                        Settings.setFeature(f.key, it)
                        if (it && !f.ready(ctx)) grant(f)
                    })
                }
            }
        }
    }
}
