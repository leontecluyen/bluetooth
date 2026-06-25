package leontec.jp.synclogs.data

import android.content.Context
import android.util.Log
import java.io.File

/**
 * Một file CSV trên đĩa: đang ở **outbox** (chưa gửi) hoặc **backup** (đã gửi).
 *  - [displayName] : tên hiển thị — `{type}_{date}.txt` khi chưa gửi, hoặc tên đã gửi lên PC
 *    `{type}_{date}_{term}_{index}.txt` khi đã ở backup.
 *  - [index]       : chỉ có ý nghĩa cho file đã gửi (lấy từ tên trong backup); 0 nếu chưa gửi.
 */
data class CsvFileEntry(
    val type: String,        // "monitor_log" | "pallet_log" | "direct_log"
    val date: String,        // yyyyMMdd
    val displayName: String,
    val file: File,
    val sent: Boolean,
    val index: Int = 0
)

/**
 * Kho file CSV theo đúng yêu cầu nghiệp vụ "ログ送信":
 *
 *  - **Chưa gửi (未送信)** nằm ở `filesDir/outbox/{type}_{yyyyMMdd}.txt` — mỗi loại 1 file/ngày
 *    (tạo lại sẽ ghi đè). "Tạo log mẫu" ghi vào đây.
 *  - **Đã gửi (送信済)** nằm ở `filesDir/backup/{yyyyMMdd}/{type}_{yyyyMMdd}_{term}_{index}.txt`.
 *
 *  Khi gửi: `index = 1 + max(index của type đó trong backup/{yyyyMMdd}/)` (thư mục tạo mới nếu
 *  chưa có → bắt đầu từ 1). Gửi **từng file**; file nào gửi THÀNH CÔNG mới được di chuyển từ
 *  outbox sang `backup/{yyyyMMdd}/` với **đúng tên đã gửi lên PC**.
 */
class CsvFileStore(context: Context) {

    private val root = context.applicationContext.filesDir
    private val outboxDir = File(root, OUTBOX)
    private val backupRoot = File(root, BACKUP)
    private val tag = "CSV_FILE_STORE"

    private fun ensure(dir: File) { if (!dir.exists()) dir.mkdirs() }

    /** Ghi (đè) 1 file CSV chưa gửi: `outbox/{type}_{date}.txt`. */
    fun writeOutbox(type: String, date: String, csv: String): File {
        ensure(outboxDir)
        val f = File(outboxDir, "${type}_${date}.txt")
        f.writeText(csv, Charsets.UTF_8)
        Log.i(tag, "Tạo file CHƯA gửi: ${f.name} (${csv.toByteArray(Charsets.UTF_8).size} bytes).")
        return f
    }

    /** Các file CHƯA gửi (outbox) của một ngày, theo thứ tự direct → monitor → pallet. */
    fun unsentForDay(date: String): List<CsvFileEntry> {
        ensure(outboxDir)
        return TYPES.mapNotNull { type ->
            val f = File(outboxDir, "${type}_${date}.txt")
            if (f.exists()) CsvFileEntry(type, date, f.name, f, sent = false) else null
        }
    }

    /** Các file ĐÃ gửi (backup/{date}/) của một ngày. */
    fun sentForDay(date: String): List<CsvFileEntry> {
        val dir = File(backupRoot, date)
        if (!dir.exists()) return emptyList()
        return (dir.listFiles { f -> f.isFile && f.name.endsWith(".txt") } ?: emptyArray())
            .mapNotNull { f ->
                val parsed = parseSentName(f.name) ?: return@mapNotNull null
                CsvFileEntry(parsed.first, date, f.name, f, sent = true, index = parsed.second)
            }
            .sortedWith(compareBy({ TYPES.indexOf(it.type) }, { it.index }))
    }

    /** index kế tiếp cho [type] trong ngày = `1 + max(index hiện có trong backup/{date}/)`. */
    fun nextIndex(type: String, date: String): Int {
        val dir = File(backupRoot, date)
        ensure(dir)
        val max = (dir.listFiles { f -> f.isFile } ?: emptyArray())
            .mapNotNull { f -> parseSentName(f.name)?.takeIf { it.first == type }?.second }
            .maxOrNull() ?: 0
        val next = max + 1
        Log.d(tag, "nextIndex($type, $date) = $next (max hiện có = $max).")
        return next
    }

    /** Di chuyển file outbox sang `backup/{date}/{sentName}` sau khi gửi thành công. */
    fun moveToBackup(entry: CsvFileEntry, sentName: String): Boolean {
        val dir = File(backupRoot, entry.date)
        ensure(dir)
        val dest = File(dir, sentName)
        if (entry.file.renameTo(dest)) {
            Log.i(tag, "Đã chuyển ${entry.file.name} → backup/${entry.date}/$sentName.")
            return true
        }
        // renameTo có thể thất bại (khác filesystem…) → fallback copy + delete.
        return runCatching {
            entry.file.copyTo(dest, overwrite = true)
            entry.file.delete()
            Log.i(tag, "Đã copy+xoá ${entry.file.name} → backup/${entry.date}/$sentName.")
            true
        }.getOrElse {
            Log.e(tag, "moveToBackup lỗi ${entry.file.name} → $sentName: ${it.message}")
            false
        }
    }

    /** Đọc nội dung CSV của một file (rỗng nếu lỗi đọc). */
    fun readCsv(entry: CsvFileEntry): String =
        runCatching { entry.file.readText(Charsets.UTF_8) }.getOrDefault("")

    /** Xoá toàn bộ outbox + backup (dùng cho "Reset tất cả"). */
    fun clearAll() {
        runCatching { outboxDir.deleteRecursively() }
        runCatching { backupRoot.deleteRecursively() }
        Log.i(tag, "Đã xoá toàn bộ outbox + backup.")
    }

    /** `monitor_log_20260625_GalaxyS10_3.txt` → ("monitor_log", 3). null nếu không khớp. */
    private fun parseSentName(name: String): Pair<String, Int>? {
        val base = name.removeSuffix(".txt")
        val m = SENT_NAME.find(base) ?: return null
        val index = m.groupValues[3].toIntOrNull() ?: return null
        return m.groupValues[1] to index
    }

    companion object {
        private const val OUTBOX = "outbox"
        private const val BACKUP = "backup"
        private val TYPES = listOf("direct_log", "monitor_log", "pallet_log")
        // {type}_{8 chữ số ngày}_{term}_{index}
        private val SENT_NAME = Regex("^([a-zA-Z]+_log)_(\\d{8})_.+_(\\d+)$")
    }
}
