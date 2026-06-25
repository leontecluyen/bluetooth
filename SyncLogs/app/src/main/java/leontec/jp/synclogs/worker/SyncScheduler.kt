package leontec.jp.synclogs.worker

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import java.util.concurrent.TimeUnit

/**
 * Single entry point for enqueuing [SyncWorker]. Centralises the EXPONENTIAL
 * backoff (10 s initial delay) and the unique-work names so triggers from the
 * UI, the geofence receiver and the periodic schedule never stack up duplicates.
 */
object SyncScheduler {

    private const val PERIODIC_WORK_NAME = "synclogs_sync_periodic"
    private const val BACKOFF_SECONDS = 10L

    /** Fire-and-forget sync, e.g. from a manual button or geofence ENTER. */
    fun enqueueOnce(context: Context) {
        val request = OneTimeWorkRequestBuilder<SyncWorker>()
            .setBackoffCriteria(
                BackoffPolicy.EXPONENTIAL,
                BACKOFF_SECONDS,
                TimeUnit.SECONDS
            )
            .build()

        WorkManager.getInstance(context).enqueueUniqueWork(
            SyncWorker.UNIQUE_WORK_NAME,
            ExistingWorkPolicy.REPLACE,
            request
        )
    }

    /**
     * Periodic safety net so logs eventually drain even with no geofence/manual
     * trigger. WorkManager's minimum period is 15 minutes.
     */
    fun ensurePeriodicSync(context: Context) {
        val request = PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().build())
            .setBackoffCriteria(
                BackoffPolicy.EXPONENTIAL,
                BACKOFF_SECONDS,
                TimeUnit.SECONDS
            )
            .build()

        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            PERIODIC_WORK_NAME,
            ExistingPeriodicWorkPolicy.KEEP,
            request
        )
    }
}
