package leontec.jp.synclogs.geofence

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.google.android.gms.location.Geofence
import com.google.android.gms.location.GeofencingEvent
import leontec.jp.synclogs.bluetooth.BluetoothSyncManager
import leontec.jp.synclogs.notification.BluetoothEnableNotifier
import leontec.jp.synclogs.worker.SyncScheduler

/**
 * Fires on GEOFENCE_TRANSITION_ENTER (CLAUDE.md §4). Powers on the Bluetooth
 * radio (best-effort; silently only on API ≤ 32 — see [BluetoothSyncManager.enableAdapter])
 * and enqueues a sync so logs upload as the worker walks into the office.
 */
class GeofenceBroadcastReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_GEOFENCE_EVENT) return

        val event = GeofencingEvent.fromIntent(intent)
        if (event == null) {
            Log.w(TAG, "Null GeofencingEvent.")
            return
        }
        if (event.hasError()) {
            Log.e(TAG, "Geofencing error code ${event.errorCode}.")
            return
        }

        if (event.geofenceTransition == Geofence.GEOFENCE_TRANSITION_ENTER) {
            Log.i(TAG, "Entered office geofence -> enabling Bluetooth and enqueuing sync.")

            val bluetooth = BluetoothSyncManager(context)
            // Try the silent enable (works only on API <= 32). If it could not turn
            // the radio on, surface a tap-to-enable notification so the user can
            // approve the system popup from the foreground (Android 13+).
            val enabled = bluetooth.enableAdapter()
            if (!enabled && bluetooth.isDisabled()) {
                BluetoothEnableNotifier.notifyEnableRequest(context)
            }

            // Enqueue regardless: if Bluetooth is still off the worker falls back to Wi-Fi.
            SyncScheduler.enqueueOnce(context)
        }
    }

    companion object {
        private const val TAG = "GeofenceReceiver"
        const val ACTION_GEOFENCE_EVENT = "leontec.jp.synclogs.action.GEOFENCE_EVENT"
    }
}
