package leontec.jp.synclogs.notification

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import leontec.jp.synclogs.MainActivity
import leontec.jp.synclogs.R

/**
 * Bridges a background trigger (geofence ENTER on Android 13+) to the foreground
 * `ACTION_REQUEST_ENABLE` popup, which can only be shown from an Activity.
 *
 * Posts a tap-to-act notification; tapping opens [MainActivity] with
 * [MainActivity.EXTRA_PROMPT_BT_ENABLE], which then launches the system dialog.
 */
object BluetoothEnableNotifier {

    private const val CHANNEL_ID = "bluetooth_enable"
    private const val NOTIFICATION_ID = 1001

    fun notifyEnableRequest(context: Context) {
        // On API 33+ posting requires the runtime POST_NOTIFICATIONS grant.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        ensureChannel(context)

        val tapIntent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            putExtra(MainActivity.EXTRA_PROMPT_BT_ENABLE, true)
        }
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        val pendingIntent = PendingIntent.getActivity(context, 0, tapIntent, flags)

        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(context.getString(R.string.bt_enable_title))
            .setContentText(context.getString(R.string.bt_enable_text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(pendingIntent)
            .build()

        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, notification)
    }

    private fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                context.getString(R.string.bt_enable_channel),
                NotificationManager.IMPORTANCE_HIGH
            )
            val manager = context.getSystemService(NotificationManager::class.java)
            manager?.createNotificationChannel(channel)
        }
    }
}
