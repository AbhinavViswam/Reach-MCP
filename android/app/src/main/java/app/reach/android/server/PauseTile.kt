package app.reach.android.server

import android.content.ComponentName
import android.content.Context
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import app.reach.android.Settings

/**
 * Quick Settings tile "Reach": on while an agent may use the phone; tap to pause (every call refused, a running
 * action stopped) and tap again to resume. The kill switch one swipe away from any app.
 */
class PauseTile : TileService() {

    override fun onStartListening() = show()

    override fun onClick() {
        if (!ServerService.status.value.running) {
            // Nothing to pause: say so instead of pretending.
            qsTile?.apply { subtitle = "Server stopped"; updateTile() }
            return
        }
        Settings.setPaused(!Settings.paused.value, "quick settings")
        show()
    }

    private fun show() {
        val t = qsTile ?: return
        val running = ServerService.status.value.running
        val paused = Settings.paused.value
        t.state = when {
            !running -> Tile.STATE_UNAVAILABLE
            paused -> Tile.STATE_INACTIVE
            else -> Tile.STATE_ACTIVE
        }
        t.label = "Reach"
        t.subtitle = when {
            !running -> "Stopped"
            paused -> "Paused"
            else -> "Agent can use phone"
        }
        t.updateTile()
    }

    companion object {
        fun refresh(c: Context) {
            runCatching { requestListeningState(c, ComponentName(c, PauseTile::class.java)) }
        }
    }
}
