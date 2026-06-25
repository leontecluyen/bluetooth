package leontec.jp.synclogs.geofence

import android.Manifest
import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat
import com.google.android.gms.location.Geofence
import com.google.android.gms.location.GeofencingClient
import com.google.android.gms.location.GeofencingRequest
import com.google.android.gms.location.LocationServices
import kotlinx.coroutines.suspendCancellableCoroutine
import leontec.jp.synclogs.config.SyncConfig
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * Registers/removes the office geofence (CLAUDE.md §4): a 50 m radius around the
 * configured office coordinates. On ENTER, [GeofenceBroadcastReceiver] powers on
 * Bluetooth and kicks off a sync.
 *
 * Requires ACCESS_FINE_LOCATION and, for the geofence to fire while the app is
 * backgrounded, ACCESS_BACKGROUND_LOCATION (API 29+).
 */
class GeofenceManager(private val context: Context) {

    private val client: GeofencingClient = LocationServices.getGeofencingClient(context)
    private val config = SyncConfig.get(context)

    private val pendingIntent: PendingIntent by lazy {
        val intent = Intent(context, GeofenceBroadcastReceiver::class.java).apply {
            action = GeofenceBroadcastReceiver.ACTION_GEOFENCE_EVENT
        }
        // FLAG_MUTABLE is required because Play Services adds the geofencing extras.
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
        } else {
            PendingIntent.FLAG_UPDATE_CURRENT
        }
        PendingIntent.getBroadcast(context, 0, intent, flags)
    }

    fun hasLocationPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_FINE_LOCATION) ==
            PackageManager.PERMISSION_GRANTED

    /**
     * Registers the office geofence. Safe to call repeatedly — the request uses
     * the fixed [SyncConfig.GEOFENCE_REQUEST_ID] so re-registration replaces it.
     *
     * @throws SecurityException if location permission is missing (caller should
     *         gate on [hasLocationPermission] first).
     */
    @SuppressLint("MissingPermission")
    suspend fun registerOfficeGeofence() {
        if (!hasLocationPermission()) {
            throw SecurityException("ACCESS_FINE_LOCATION not granted")
        }

        val geofence = Geofence.Builder()
            .setRequestId(SyncConfig.GEOFENCE_REQUEST_ID)
            .setCircularRegion(
                config.geofenceLatitude,
                config.geofenceLongitude,
                config.geofenceRadiusMeters
            )
            .setExpirationDuration(Geofence.NEVER_EXPIRE)
            .setTransitionTypes(Geofence.GEOFENCE_TRANSITION_ENTER)
            .build()

        val request = GeofencingRequest.Builder()
            .setInitialTrigger(GeofencingRequest.INITIAL_TRIGGER_ENTER)
            .addGeofence(geofence)
            .build()

        await { client.addGeofences(request, pendingIntent) }
        Log.i(TAG, "Office geofence registered (r=${config.geofenceRadiusMeters}m).")
    }

    suspend fun removeOfficeGeofence() {
        await { client.removeGeofences(listOf(SyncConfig.GEOFENCE_REQUEST_ID)) }
        Log.i(TAG, "Office geofence removed.")
    }

    /** Bridges a Play Services [com.google.android.gms.tasks.Task] to a coroutine. */
    private suspend inline fun await(crossinline block: () -> com.google.android.gms.tasks.Task<Void>) =
        suspendCancellableCoroutine { cont ->
            block()
                .addOnSuccessListener { cont.resume(Unit) }
                .addOnFailureListener { cont.resumeWithException(it) }
        }

    companion object {
        private const val TAG = "GeofenceManager"
    }
}
