package leontec.jp.synclogs.data

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

/** One created sample CSV (monitor/pallet). [sent] flips true once a sync has delivered it. */
data class PendingCsvSample(
    val id: String,
    val type: String,      // "monitor_log" | "pallet_log"
    val csvText: String,
    val termId: String,
    val index: Int,        // per-type upload index, assigned at CREATION time
    val date: String,      // log day yyyyMMdd, assigned at CREATION time
    val sent: Boolean = false
)

/**
 * Persists sample CSVs created by "Tạo CSV mẫu" **until a sync actually sends them** ("Đồng bộ
 * ngay" / Auto). Stored as a JSON array in SharedPreferences so they survive process death.
 *
 * The per-type `index` and `date` are assigned **when the sample is created** and kept here, so
 * creating several samples bumps the index correctly (#1, #2, #3 …) regardless of when they're
 * sent. A successful send flips the sample's `sent` flag (kept in the list so the UI can show a
 * "đã gửi" status); only "Reset tất cả" / [clear] removes everything.
 */
class PendingSampleStore(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val tag = "PENDING_SAMPLE_STORE"

    @Synchronized
    fun add(sample: PendingCsvSample) {
        val arr = readArray()
        arr.put(sample.toJson())
        prefs.edit().putString(KEY, arr.toString()).apply()
        Log.i(tag, "Tạo mẫu chờ gửi: type=${sample.type} idx=${sample.index} date=${sample.date} (tổng ${arr.length()}).")
    }

    @Synchronized
    fun getAll(): List<PendingCsvSample> {
        val arr = readArray()
        return (0 until arr.length()).map { fromJson(arr.getJSONObject(it)) }
    }

    /**
     * Re-queue an already-sent sample: flip sent=false so the next sync sends it AGAIN with the
     * SAME envelope (type/date/term/index) → the PC receives a duplicate filename. Used for the
     * "re-transmit an already-sent file" feature.
     */
    @Synchronized
    fun requeue(id: String) {
        val arr = readArray()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (o.optString("id") == id) o.put("sent", false)
        }
        prefs.edit().putString(KEY, arr.toString()).apply()
        Log.i(tag, "Xếp lại để GỬI LẠI mẫu id=$id (trùng tên).")
    }

    /** Flip the given samples to sent=true (kept in the list; the UI shows them as "đã gửi"). */
    @Synchronized
    fun markSent(ids: Collection<String>) {
        if (ids.isEmpty()) return
        val arr = readArray()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (o.optString("id") in ids) o.put("sent", true)
        }
        prefs.edit().putString(KEY, arr.toString()).apply()
        Log.i(tag, "Đánh dấu ${ids.size} mẫu = đã gửi.")
    }

    @Synchronized
    fun clear() {
        prefs.edit().remove(KEY).apply()
        Log.i(tag, "Xoá toàn bộ mẫu.")
    }

    /** Number of samples still waiting to be sent (sent == false). */
    @Synchronized
    fun pendingCount(): Int = getAll().count { !it.sent }

    private fun readArray(): JSONArray =
        runCatching { JSONArray(prefs.getString(KEY, "[]")) }.getOrElse { JSONArray() }

    private fun PendingCsvSample.toJson() = JSONObject().apply {
        put("id", id)
        put("type", type)
        put("csvText", csvText)
        put("termId", termId)
        put("index", index)
        put("date", date)
        put("sent", sent)
    }

    private fun fromJson(o: JSONObject) = PendingCsvSample(
        id = o.optString("id"),
        type = o.optString("type"),
        csvText = o.optString("csvText"),
        termId = o.optString("termId"),
        index = o.optInt("index"),
        date = o.optString("date"),
        sent = o.optBoolean("sent", false)
    )

    companion object {
        private const val PREFS = "synclogs_pending_samples"
        private const val KEY = "pending_samples"
    }
}
