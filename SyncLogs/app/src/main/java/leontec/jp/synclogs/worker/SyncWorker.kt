package leontec.jp.synclogs.worker

import android.content.Context
import android.util.Log
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import leontec.jp.synclogs.bluetooth.BluetoothSyncManager
import leontec.jp.synclogs.config.SyncConfig
import leontec.jp.synclogs.data.AppDatabase
import leontec.jp.synclogs.data.PendingSampleStore
import leontec.jp.synclogs.data.SyncMethod
import leontec.jp.synclogs.data.SyncStatus
import leontec.jp.synclogs.network.RetrofitFactory
import leontec.jp.synclogs.network.SyncApiService

class SyncWorker(
    context: Context,
    params: WorkerParameters
) : CoroutineWorker(context, params) {

    private val TAG = "SYNC_WORKER"
    private val database = AppDatabase.getDatabase(context)
    private val dao = database.jobLogDao()
    private val bluetoothManager = BluetoothSyncManager(context)
    private val pendingStore = PendingSampleStore(context)

    companion object {
        const val UNIQUE_WORK_NAME = "synclogs_sync_worker"
    }

    override suspend fun doWork(): Result {
        Log.i(TAG, ">>> [START] Bắt đầu tiến trình đồng bộ (Run Attempt: $runAttemptCount)")

        val config = SyncConfig.get(applicationContext)
        val targetPcName = config.pcBluetoothName
        val targetPcAddr = config.pcBluetoothAddress
        val wifiBaseUrl = config.restBaseUrl

        // Chỉ gửi các mẫu CHƯA gửi.
        val pendingSamples = pendingStore.getAll().filter { !it.sent }
        val unsyncedLogs = dao.getUnsyncedLogs()
        if (pendingSamples.isEmpty() && unsyncedLogs.isEmpty()) {
            Log.i(TAG, ">>> [FINISH] Không có mẫu CSV / log nào cần đồng bộ.")
            return Result.success()
        }

        var success = true

        // Giữ cờ "đang đồng bộ" quanh CẢ batch (+ tạm dừng heartbeat ping) để PC không bị
        // PING chen vào giữa các lần gửi → tránh tranh chấp kết nối khiến PC tưởng mất liên lạc.
        BluetoothSyncManager.pausePing(30_000L)
        BluetoothSyncManager.beginSync()
        try {

        // --- Mẫu CSV typed (monitor/pallet) — chỉ qua Bluetooth ---
        if (pendingSamples.isNotEmpty()) {
            Log.i(TAG, "--- Gửi ${pendingSamples.size} CSV mẫu qua Bluetooth (Target: '$targetPcName' / $targetPcAddr) ---")
            val sentIds = mutableListOf<String>()
            for (s in pendingSamples) {
                val ok = try {
                    bluetoothManager.sendCsvFile(s.type, s.csvText, s.termId, s.index, targetPcName, targetPcAddr, s.date)
                } catch (e: Exception) {
                    Log.e(TAG, "Lỗi gửi mẫu ${s.type}#${s.index}: ${e.message}"); false
                }
                if (ok) sentIds.add(s.id) else success = false
            }
            // Đánh dấu ĐÃ GỬI (giữ lại trong list để UI hiển thị trạng thái, không xoá).
            if (sentIds.isNotEmpty()) pendingStore.markSent(sentIds)
            Log.i(TAG, ">>> Đã gửi ${sentIds.size}/${pendingSamples.size} CSV mẫu.")
        }

        // --- Log legacy trong Room — Bluetooth, fallback Wi-Fi ---
        if (unsyncedLogs.isNotEmpty()) {
            Log.d(TAG, "Tìm thấy ${unsyncedLogs.size} logs đang ở trạng thái PENDING.")
            Log.i(TAG, "--- THỬ ĐỒNG BỘ LEGACY QUA BLUETOOTH SPP ---")
            val btSuccess = try {
                bluetoothManager.sync(unsyncedLogs, targetPcName, targetPcAddr)
            } catch (e: Exception) {
                Log.e(TAG, "Lỗi nghiêm trọng khi gọi Bluetooth sync: ${e.message}"); false
            }

            if (btSuccess) {
                Log.i(TAG, ">>> [SUCCESS] Đồng bộ Bluetooth (legacy) thành công. Cập nhật DB...")
                unsyncedLogs.forEach { dao.markAsSynced(it.id, SyncStatus.SUCCESS, SyncMethod.BLUETOOTH) }
            } else {
                Log.w(TAG, "Bluetooth thất bại. Chuyển sang Wi-Fi REST API (Target: $wifiBaseUrl)...")
                val wifiSuccess = try {
                    val response = RetrofitFactory.create(wifiBaseUrl).syncLogs(unsyncedLogs)
                    if (response.isSuccessful) {
                        Log.i(TAG, ">>> [SUCCESS] Đồng bộ Wi-Fi thành công. Cập nhật DB...")
                        unsyncedLogs.forEach { dao.markAsSynced(it.id, SyncStatus.SUCCESS, SyncMethod.WIFI) }
                        true
                    } else {
                        Log.e(TAG, "Wi-Fi Sync thất bại: HTTP ${response.code()} - ${response.message()}"); false
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Lỗi khi đồng bộ Wi-Fi: ${e.message}", e); false
                }
                if (!wifiSuccess) success = false
            }
        }

        return if (success) {
            Result.success()
        } else {
            Log.w(TAG, ">>> [RETRY] Còn mục chưa gửi được. Sẽ thử lại theo chính sách WorkManager.")
            Result.retry()
        }

        } finally {
            BluetoothSyncManager.endSync()
        }
    }
}
