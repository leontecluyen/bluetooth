package leontec.jp.synclogs

import android.app.Application
import leontec.jp.synclogs.worker.SyncScheduler

/**
 * App entry point. Ensures the periodic sync safety-net is scheduled on every
 * cold start so pending logs eventually drain even without a geofence or manual
 * trigger.
 */
class SyncLogsApplication : Application() {

    override fun onCreate() {
        super.onCreate()
        SyncScheduler.ensurePeriodicSync(this)
    }
}
