package leontec.jp.synclogs.sync

import leontec.jp.synclogs.data.JobLog
import android.util.Log

/**
 * Serializes pending logs into the two wire formats the PC accepts:
 *
 *  - [toCsv]  – used by the Bluetooth SPP layer (CSV file payload).
 *  - JSON is produced by Gson/Retrofit directly for the Wi-Fi REST layer.
 */
object LogPayloadSerializer {
    private const val TAG = "LogPayloadSerializer"

    /** CSV column order. Kept in one place so the PC parser and tests stay in sync. */
    val CSV_HEADER = listOf(
        "id",
        "workerId",
        "jobType",
        "barcodeData",
        "startTime",
        "endTime"
    )

    private const val CRLF = "\r\n"

    fun toCsv(logs: List<JobLog>): String {
        Log.d(TAG, "Serializing ${logs.size} logs to CSV...")
        val sb = StringBuilder()
        sb.append(CSV_HEADER.joinToString(",") { escape(it) }).append(CRLF)
        for (log in logs) {
            sb.append(
                listOf(
                    // Cột "id" gửi đi = logId nếu có (cho phép trùng để test dedup),
                    // nếu trống thì dùng khoá chính Room (hành vi bình thường).
                    log.logId.ifBlank { log.id },
                    log.workerId,
                    log.jobType,
                    log.barcodeData,
                    log.startTime.toString(),
                    log.endTime.toString()
                ).joinToString(",") { escape(it) }
            ).append(CRLF)
        }
        val result = sb.toString()
        Log.d(TAG, "CSV serialization complete. Length: ${result.length}")
        return result
    }

    private fun escape(field: String): String {
        val mustQuote = field.any { it == ',' || it == '"' || it == '\n' || it == '\r' }
        if (!mustQuote) return field
        val doubled = field.replace("\"", "\"\"")
        return "\"$doubled\""
    }
}
