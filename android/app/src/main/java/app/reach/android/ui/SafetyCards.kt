package app.reach.android.ui

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import app.reach.android.Settings
import app.reach.android.privacy.AppRule
import app.reach.android.privacy.Audit
import app.reach.android.privacy.Privacy

private data class InstalledApp(val label: String, val pkg: String)

private fun installedApps(c: Context): List<InstalledApp> {
    val pm = c.packageManager
    val main = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
    return pm.queryIntentActivities(main, 0)
        .map { InstalledApp(it.loadLabel(pm).toString(), it.activityInfo.packageName) }
        .filter { it.pkg != c.packageName }
        .distinctBy { it.pkg }
        .sortedBy { it.label.lowercase() }
}

/** App rules: the user's own allow / read only / block per app, over the built-in blocked list (privacy/Privacy.kt). */
@Composable
internal fun AppRules() {
    val ctx = LocalContext.current
    var rules by remember { mutableStateOf(Privacy.userRules) }
    var picking by remember { mutableStateOf(false) }
    val apps = remember(picking) { if (picking) installedApps(ctx) else emptyList() }
    val labels = remember(rules) { installedApps(ctx).associate { it.pkg to it.label } }

    fun set(pkg: String, r: AppRule?) {
        Settings.setRule(pkg, r)
        rules = Privacy.userRules
    }

    Section("5 · App rules", ok = null) {
        Text("Allow: read and use. Read only: your agent can read it and scroll, but never taps or types in it. " +
            "Block: never read or touched. Banking, payment, wallet, password and authenticator apps are blocked unless you allow them here.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (rules.isEmpty()) Text("No rules of your own yet.", style = MaterialTheme.typography.bodyMedium)
        rules.entries.sortedBy { (labels[it.key] ?: it.key).lowercase() }.forEach { (pkg, r) ->
            Column {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(labels[pkg] ?: pkg, style = MaterialTheme.typography.titleSmall)
                        Text(pkg, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    TextButton(onClick = { set(pkg, null) }) { Text("Remove") }
                }
                RuleChips(r) { set(pkg, it) }
            }
        }
        OutlinedButton(onClick = { picking = true }) { Text("Add an app") }
    }

    if (picking) {
        var query by remember { mutableStateOf("") }
        var chosen by remember { mutableStateOf<InstalledApp?>(null) }
        var rule by remember { mutableStateOf(AppRule.READ_ONLY) }
        AlertDialog(
            onDismissRequest = { picking = false },
            title = { Text(chosen?.label ?: "Choose an app") },
            text = {
                val c = chosen
                if (c == null) {
                    Column {
                        OutlinedTextField(query, { query = it }, label = { Text("Search") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                        LazyColumn(Modifier.heightIn(max = 360.dp)) {
                            items(apps.filter { query.isBlank() || it.label.contains(query, true) || it.pkg.contains(query, true) }) { a ->
                                Column(Modifier.fillMaxWidth().clickable {
                                    chosen = a
                                    rule = if (Privacy.builtinBlocked(a.pkg, a.label)) AppRule.ALLOW else AppRule.READ_ONLY
                                }.padding(vertical = 10.dp)) {
                                    Text(a.label, style = MaterialTheme.typography.bodyLarge)
                                    Text(a.pkg + if (Privacy.builtinBlocked(a.pkg, a.label)) " · blocked by default" else "",
                                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                                HorizontalDivider()
                            }
                        }
                    }
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(c.pkg, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        RuleChips(rule) { rule = it }
                    }
                }
            },
            confirmButton = {
                val c = chosen
                if (c != null) TextButton(onClick = { set(c.pkg, rule); picking = false }) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { picking = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun RuleChips(current: AppRule, pick: (AppRule) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        AppRule.entries.forEach { r ->
            FilterChip(selected = r == current, onClick = { pick(r) }, label = { Text(r.label) })
        }
    }
}

/** The activity log (privacy/Audit.kt): what the agent did and every approval, newest first. */
@Composable
internal fun Activity() {
    var tick by remember { mutableStateOf(0) }
    var all by remember { mutableStateOf(false) }
    androidx.lifecycle.compose.LifecycleResumeEffect(Unit) {
        tick++
        onPauseOrDispose { }
    }
    val rows = remember(tick, all) { Audit.read(if (all) 200 else 6) }

    Section("6 · Activity", ok = null) {
        Text("Every call your agent made, its result, and every approval. Kept on this phone only.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (rows.isEmpty()) Text("Nothing yet.", style = MaterialTheme.typography.bodyMedium)
        rows.forEach { e ->
            val color = when {
                !e.ok -> MaterialTheme.colorScheme.error
                e.kind != "call" -> MaterialTheme.colorScheme.primary
                else -> MaterialTheme.colorScheme.onSurface
            }
            Column {
                Text(
                    (if (e.kind == "approval") "asked: " else "") + e.tool + (if (e.kind == "call") " · ${e.ms} ms" else ""),
                    style = MaterialTheme.typography.titleSmall, color = color,
                )
                val detail = listOf(e.result, e.args).filter { it.isNotBlank() }.joinToString(" · ")
                if (detail.isNotBlank()) Text(detail, style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(e.time, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            TextButton(onClick = { tick++ }) { Text("Refresh") }
            TextButton(onClick = { all = !all }) { Text(if (all) "Show less" else "Show more") }
            TextButton(onClick = { Audit.clear(); tick++ }) { Text("Clear") }
        }
    }
}

/** Saved flows (tools/FlowsTool.kt): what the agent can replay without the model, with delete. */
@Composable
internal fun Flows() {
    val ctx = LocalContext.current
    var tick by remember { mutableStateOf(0) }
    androidx.lifecycle.compose.LifecycleResumeEffect(Unit) {
        tick++
        onPauseOrDispose { }
    }
    val files = remember(tick) {
        (java.io.File(ctx.filesDir, "flows").listFiles { f -> f.extension == "json" } ?: emptyArray()).sortedBy { it.name }
    }
    Section("7 · Flows", ok = null) {
        Text("Tasks your agent saved to replay without the model. Ask it to \"record a flow\" while it does something.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (files.isEmpty()) Text("No flows saved yet.", style = MaterialTheme.typography.bodyMedium)
        files.forEach { f ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(f.nameWithoutExtension, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                TextButton(onClick = { f.delete(); tick++ }) { Text("Delete") }
            }
        }
    }
}

/** The PC this phone's agent may use (tools/PcTool.kt): Reach for Windows running reach.exe --http. */
@Composable
internal fun YourPc() {
    var url by remember { mutableStateOf(Settings.pcUrl) }
    var token by remember { mutableStateOf(Settings.pcToken) }
    var result by remember { mutableStateOf("") }
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    Section("8 · Your PC", ok = if (token.isBlank()) null else true, okText = "Linked") {
        Text("Lets an agent connected to this phone also use your PC (the pc tool). On the PC run reach.exe --http and copy " +
            "its token (Reach settings › Copy HTTP token). Over USB, run adb reverse tcp:8766 tcp:8766 on the PC and keep the " +
            "address below; over Tailscale use the PC's 100.x address. The PC's own rules and approvals still apply there.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        OutlinedTextField(url, { url = it; Settings.pcUrl = it.trim() }, label = { Text("PC address") }, singleLine = true,
            modifier = Modifier.fillMaxWidth())
        OutlinedTextField(token, { token = it; Settings.pcToken = it.trim() }, label = { Text("PC token") }, singleLine = true,
            visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            TextButton(onClick = {
                result = "Asking the PC…"
                scope.launch {
                    result = try {
                        val tools = app.reach.android.tools.McpClient(Settings.pcUrl, Settings.pcToken).tools()
                        "Works: ${tools.size} tools (${tools.take(6).joinToString { it.first }}…). Reconnect your agent to see the pc tool."
                    } catch (e: app.reach.android.mcp.ToolError) { e.hint } catch (e: Exception) { e.message ?: "failed" }
                }
            }) { Text("Test") }
            if (token.isNotBlank()) TextButton(onClick = { token = ""; Settings.pcToken = "" }) { Text("Unlink") }
        }
        if (result.isNotEmpty()) Text(result, style = MaterialTheme.typography.bodySmall)
    }
}
