package leontec.jp.synclogs.data

import androidx.room.Entity
import androidx.room.PrimaryKey
import java.util.UUID

/**
 * Enterprise Job Log Entity.
 * Idempotency is guaranteed by using a client-side UUID as the Primary Key.
 */
@Entity(tableName = "job_logs")
data class JobLog(
    @PrimaryKey val id: String = UUID.randomUUID().toString(),
    val workerId: String,
    val jobType: String, // '検品', '出荷', '直送'
    val barcodeData: String,
    val startTime: Long,
    val endTime: Long,
    var syncStatus: SyncStatus = SyncStatus.PENDING,
    var syncMethod: SyncMethod? = null,
    // Logs created together form one "CSV" batch (shown as one row in the app list).
    val batchId: String = "",
    val batchName: String = "",
    /**
     * Id GỬI TRONG CSV (cột "id" PC dùng làm khoá dedup). Tách khỏi khoá chính Room [id] để
     * có thể tạo logId TRÙNG (test dedup) trong khi Room vẫn lưu đủ các dòng (PK [id] khác nhau).
     * Để trống → khi serialize sẽ dùng [id] (hành vi bình thường, không trùng).
     */
    val logId: String = ""
) {
    object JobType {
        const val KENPIN = "検品"
        const val SHUKKA = "出荷"
        const val CHOKUSO = "直送"
        val ALL = listOf(KENPIN, SHUKKA, CHOKUSO)
    }
}

enum class SyncStatus {
    PENDING, SUCCESS
}

enum class SyncMethod {
    BLUETOOTH, WIFI
}
