package leontec.jp.synclogs.ui

import android.app.Application
import android.os.Build
import android.provider.Settings
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import leontec.jp.synclogs.bluetooth.BluetoothSyncManager
import leontec.jp.synclogs.config.SyncConfig
import leontec.jp.synclogs.data.AppDatabase
import leontec.jp.synclogs.data.BatchSummary
import leontec.jp.synclogs.data.CsvFileEntry
import leontec.jp.synclogs.data.CsvFileStore
import leontec.jp.synclogs.data.JobLog
import leontec.jp.synclogs.data.PendingCsvSample
import leontec.jp.synclogs.data.PendingSampleStore
import leontec.jp.synclogs.geofence.GeofenceManager
import leontec.jp.synclogs.worker.SyncScheduler
import android.util.Log

/**
 * Backs [MainActivity]. Exposes log/pending state and the operator actions used
 * during bring-up and field testing (create a sample scan, trigger a sync,
 * register the office geofence).
 */
class MainViewModel(app: Application) : AndroidViewModel(app) {

    private val TAG = "MAIN_VIEW_MODEL"
    private val dao = AppDatabase.getDatabase(app).jobLogDao()
    private val geofenceManager = GeofenceManager(app)
    private val bluetooth = BluetoothSyncManager(app)
    private val config = SyncConfig.get(app)
    private val pendingStore = PendingSampleStore(app)
    private val fileStore = CsvFileStore(app)

    /** Bluetooth name of the currently selected target PC ("" if none chosen yet). */
    val selectedPcName: String get() = config.pcBluetoothName

    /** Bluetooth MAC of the currently selected target PC. */
    val selectedPcAddress: String get() = config.pcBluetoothAddress

    /** True once a PC has been chosen/synced — used to drive the UI state. */
    val hasPcTarget: Boolean get() = config.hasPcTarget

    /**
     * Khi CHƯA lưu PC mục tiêu, TỰ nhận thiết bị PC đã ghép đôi (vd người dùng vừa pair bằng Cài
     * đặt hệ thống rồi quay lại app). Lưu name+addr vào [config] để UI hết báo "chưa pair" và lần
     * gửi sau dùng đúng máy này. Trả true nếu vừa nhận được PC.
     */
    fun autoAdoptPc(): Boolean {
        if (config.hasPcTarget) return false
        val cand = bluetooth.detectPcCandidate() ?: return false
        config.pcBluetoothName = cand.name
        config.pcBluetoothAddress = cand.address
        Log.i(TAG, "Tự nhận PC đã ghép đôi: '${cand.name}' (${cand.address}).")
        return true
    }

    // Wi-Fi (parked): kept for when the REST fallback is re-enabled.
    val pcIpAddress: String get() = config.pcIpAddress

    /** CSV batches (one per "Tạo log mẫu") shown as the app's main list. */
    val batches = dao.observeBatches()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList<BatchSummary>())

    val pendingCount = dao.observePendingCount()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)

    /** Reflects the persisted geofence on/off so the UI switch survives restarts. */
    val geofenceEnabled: Boolean get() = config.geofenceEnabled

    /**
     * Tên máy/thiết bị này — dùng làm workerId cho mọi log. Lấy tên người dùng đặt cho
     * thiết bị (Settings "device_name"), fallback về model máy.
     */
    val deviceName: String
        get() = runCatching {
            Settings.Global.getString(getApplication<Application>().contentResolver, "device_name")
        }.getOrNull()?.takeIf { it.isNotBlank() } ?: (Build.MODEL ?: "Android")

    /** Số mẫu CSV đã tạo nhưng CHƯA gửi (sent == false). */
    fun pendingSampleCount(): Int = pendingStore.pendingCount()

    /** Danh sách mẫu CSV đang chờ gửi (cho UI hiển thị). */
    fun pendingSamples(): List<PendingCsvSample> = pendingStore.getAll()

    // ---- Màn hình "ログ送信" (file-based: outbox = chưa gửi, backup = đã gửi) ----

    /** File CHƯA gửi của một ngày (outbox). */
    fun unsentFiles(date: String): List<CsvFileEntry> = fileStore.unsentForDay(date)

    /** File ĐÃ gửi của một ngày (backup/{date}/). */
    fun sentFiles(date: String): List<CsvFileEntry> = fileStore.sentForDay(date)

    /**
     * **TẠO** 3 file CSV mẫu (monitor + pallet + direct) cho [date] và ghi vào **outbox** (chưa
     * gửi) — KHÔNG gửi. Tạo lại cùng ngày sẽ ghi đè file cũ. index KHÔNG gán lúc này; index được
     * tính lúc GỬI (= 1 + max index của type đó trong backup/{date}/).
     */
    fun createSampleFiles(date: String, onDone: () -> Unit = {}) {
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                val now = System.currentTimeMillis()
                // monitor (nhận hàng) và pallet (đóng pallet từ hàng đã nhận) phải KHỚP nhau.
                val set = generateSampleCsvSet(now)
                fileStore.writeOutbox("monitor_log", date, set.monitor)
                fileStore.writeOutbox("pallet_log", date, set.pallet)
                fileStore.writeOutbox("direct_log", date, set.direct)
            }
            Log.i(TAG, "Đã tạo 3 file mẫu CHƯA gửi cho ngày=$date (outbox).")
            onDone()
        }
    }

    /**
     * Gửi **từng** file đã chọn (1 ngày) lên PC, mỗi file một kết nối SPP để biết file nào thành
     * công. Với mỗi file: `index = 1 + max(index của type trong backup/{date}/)`, gửi lên PC tên
     * `{type}_{date}_{term}_{index}.txt`; **gửi thành công** thì DI CHUYỂN file từ outbox sang
     * `backup/{date}/` với đúng tên đã gửi. [onDone] trả [SendOutcome] nghiêm trọng nhất gặp phải
     * (để hiển thị 1 trong 5 thông báo); file thành công vẫn được chuyển dù file khác lỗi.
     */
    fun sendDay(
        entries: List<CsvFileEntry>,
        onDone: (BluetoothSyncManager.SendOutcome) -> Unit
    ) {
        if (entries.isEmpty()) return
        val term = deviceName.replace("\\s+".toRegex(), "")
        val safeTerm = term.ifBlank { "unknown" }
        // Tạm dừng heartbeat ping để không tranh chấp kết nối trong cả đợt gửi.
        BluetoothSyncManager.pausePing(30_000L)
        viewModelScope.launch {
            // Chuẩn bị từng file: index (đếm backup +1, tránh trùng trong cùng batch) + đọc CSV +
            // dựng filename gửi PC. Lưu lại để move sau khi PC trả OK.
            data class Prep(val entry: CsvFileEntry, val sentName: String)
            val prepared = ArrayList<Prep>()
            val outFiles = ArrayList<BluetoothSyncManager.OutFile>()
            val usedIndex = HashMap<String, Int>()
            for (e in entries) {
                val base = withContext(Dispatchers.IO) { fileStore.nextIndex(e.type, e.date) }
                val idx = usedIndex[e.type]?.plus(1) ?: base
                usedIndex[e.type] = idx
                val csv = withContext(Dispatchers.IO) { fileStore.readCsv(e) }
                val sentName = "${e.type}_${e.date}_${safeTerm}_${idx}.txt"
                prepared.add(Prep(e, sentName))
                outFiles.add(BluetoothSyncManager.OutFile(sentName, csv))
            }

            // Gửi cả batch 1 lần; PC trả kết quả từng file trong 1 frame.
            val br = bluetooth.sendBatch(outFiles, selectedPcName, selectedPcAddress)

            // Chỉ MOVE sang backup những file PC xác nhận OK → không mất dữ liệu.
            var moved = 0
            for (p in prepared) {
                if (br.okFiles.contains(p.sentName)) {
                    withContext(Dispatchers.IO) { fileStore.moveToBackup(p.entry, p.sentName) }
                    moved++
                }
            }
            Log.i(TAG, "sendDay xong: gửi OK $moved/${entries.size} file, kết quả hiển thị=${br.outcome}.")
            onDone(br.outcome)
        }
    }

    /** Độ nghiêm trọng để chọn thông báo hiển thị khi gửi nhiều file (cao = ưu tiên hiển thị). */
    private fun severity(o: BluetoothSyncManager.SendOutcome): Int = when (o) {
        BluetoothSyncManager.SendOutcome.SUCCESS -> 0
        BluetoothSyncManager.SendOutcome.SEND_FAILED -> 1
        BluetoothSyncManager.SendOutcome.CONNECT_FAILED -> 2
        BluetoothSyncManager.SendOutcome.NOT_PAIRED -> 3
        BluetoothSyncManager.SendOutcome.BLUETOOTH_OFF -> 4
    }

    /**
     * Xếp lại 1 mẫu ĐÃ GỬI để **gửi lại** (cùng tên file — type/date/term/index không đổi). Lần
     * sync sau sẽ truyền lại, PC nhận file TRÙNG TÊN. Trả tổng số mẫu đang chờ gửi sau khi xếp lại.
     */
    fun resendSample(id: String): Int {
        pendingStore.requeue(id)
        val total = pendingStore.pendingCount()
        Log.i(TAG, "Yêu cầu GỬI LẠI mẫu id=$id (tổng chờ gửi: $total).")
        return total
    }

    // ====================== Sinh dữ liệu CSV mẫu (đúng logic nghiệp vụ) ======================
    // Luồng kho: **monitor = NHẬN hàng** (mỗi dòng = 1 品目 nhận theo 箱数 × 数量/箱) → **pallet =
    // ĐÓNG PALLET từ hàng đã nhận** (lấy số thùng đã nhận, tách bỏ lên pallet; 1 pallet sức chứa có
    // hạn nên 1 品目 có thể trải qua NHIỀU pallet, và có thể đóng ÍT hơn số đã nhận). direct (直送)
    // độc lập. Mỗi file ~30–100 dòng.

    private data class SampleCsvSet(val monitor: String, val pallet: String, val direct: String)

    /** 1 品目 + số 数量/箱 (収容数) cố định cho mã đó → 数量 = 箱数 × 数量/箱 nhất quán. */
    private data class Product(val code: String, val qtyPerBox: Int)

    /** 1 dòng NHẬN (monitor). */
    private data class MonitorRow(
        val start: Int, val end: Int, val slip: Long, val customer: String,
        val code: String, val boxes: Int, val qtyPerBox: Int, val status: Int
    )

    private val customers = listOf("Y01680", "Y02407")
    private val runs = listOf("カリツー1便", "カリツー3便")

    private fun hms(base: Long, addSec: Int): String =
        java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date(base + addSec * 1000L))

    /** Một bộ mã 品目 (8–12 mã), mỗi mã có 数量/箱 (収容数) riêng. */
    private fun buildProducts(): List<Product> {
        val pool = listOf("50524", "77729", "69663", "80012", "41250", "33890",
            "60145", "72310", "58820", "91006", "44557", "67421")
        return pool.shuffled().take((8..12).random()).map { Product(it, (5..50).random()) }
    }

    /**
     * Sinh đồng thời 3 CSV sao cho **pallet bắt nguồn từ monitor**: pallet chỉ chứa các 品目 đã
     * NHẬN ở monitor (状態 0), tổng số thùng đóng pallet ≤ số đã nhận.
     */
    private fun generateSampleCsvSet(now: Long): SampleCsvSet {
        val products = buildProducts()
        val monitorRows = buildMonitorRows(products)
        // Số thùng ĐÃ NHẬN theo từng 品目 (chỉ 状態 0 mới tính là nhận thật).
        val receivedBoxes = monitorRows.filter { it.status == 0 }
            .groupBy { it.code }
            .mapValues { (_, rs) -> rs.sumOf { it.boxes } }
        return SampleCsvSet(
            monitor = renderMonitorCsv(now, monitorRows),
            pallet = renderPalletCsv(now, products, receivedBoxes),
            direct = renderDirectCsv(now, products)
        )
    }

    /** ~30–100 dòng nhận, gom theo phiếu (3–6 dòng/phiếu), ~8% 状態 9 (削除), bảo đảm có ≥1 dòng 9. */
    private fun buildMonitorRows(products: List<Product>): List<MonitorRow> {
        val rows = ArrayList<MonitorRow>()
        val target = (32..96).random()
        var t = 0
        var slip = 4932360000L + (0..9999).random()
        var customer = customers.random()
        var linesInSlip = (3..6).random()
        var li = 0
        while (rows.size < target) {
            if (li >= linesInSlip) { // sang phiếu mới
                slip = 4932360000L + (0..9999).random()
                customer = customers.random()
                linesInSlip = (3..6).random()
                li = 0
            }
            val p = products.random()
            val boxes = (5..30).random()
            val status = if ((1..100).random() <= 8) 9 else 0
            val dur = (8..20).random()
            rows.add(MonitorRow(t, t + dur, slip, customer, p.code, boxes, p.qtyPerBox, status))
            t += dur + (2..6).random()
            li++
        }
        if (rows.none { it.status == 9 }) {
            val i = rows.indices.random()
            rows[i] = rows[i].copy(status = 9)
        }
        return rows
    }

    /**
     * monitor (モニタリスト, 8 cột): 開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,状態.
     * 数量 = 箱数 × 数量/箱. Lặp 2–3 dòng (状態 0) ở cuối để test tô màu trùng ở PC.
     */
    private fun renderMonitorCsv(now: Long, rows: List<MonitorRow>): String {
        val sb = StringBuilder()
        sb.append("開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,状態\r\n")
        fun line(r: MonitorRow) =
            "${hms(now, r.start)},${hms(now, r.end)},${r.slip},${r.customer}," +
                "${r.code},${r.boxes},${r.boxes * r.qtyPerBox},${r.status}"
        rows.forEach { sb.append(line(it)).append("\r\n") }
        rows.filter { it.status == 0 }.shuffled().take((2..3).random())
            .forEach { sb.append(line(it)).append("\r\n") } // dòng TRÙNG → test màu
        return sb.toString()
    }

    /**
     * pallet (パレット, 7 cột): 開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細,状態(0正常/1移動/9削除).
     * Đóng pallet từ số thùng đã nhận: mỗi pallet sức chứa `cap` thùng (suy ra để ~30–100 pallet),
     * 1 品目 có thể trải nhiều pallet; đôi khi đóng ít hơn đã nhận (75–100%). Vẫn giữ các case test
     * lọc của PC: PL1 có 状態 0 rồi 状態 1 (終了時刻 mới hơn) cùng key → PC chỉ hiện 状態 1; thêm 1 dòng 状態 9.
     */
    private fun renderPalletCsv(now: Long, products: List<Product>, receivedBoxes: Map<String, Int>): String {
        val sb = StringBuilder()
        sb.append("開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態\r\n")
        val qtyByCode = products.associate { it.code to it.qtyPerBox }
        val cust = customers.random()
        val run = runs.random()

        // Số thùng đem đóng pallet theo từng 品目 (có thể ít hơn đã nhận).
        data class Pack(val code: String, val boxes: Int)
        val toPack = receivedBoxes.mapNotNull { (code, boxes) ->
            val packed = maxOf(1, boxes * (75..100).random() / 100)
            Pack(code, packed)
        }
        val totalPack = toPack.sumOf { it.boxes }.coerceAtLeast(1)
        // Sức chứa/pallet suy ra để số pallet rơi vào ~30–100.
        val targetPallets = (30..96).random()
        val cap = maxOf(3, Math.ceil(totalPack.toDouble() / targetPallets).toInt())

        // Xếp thùng vào pallet, tách 品目 qua nhiều pallet khi đầy.
        val pallets = ArrayList<MutableList<Pair<String, Int>>>()
        var cur = ArrayList<Pair<String, Int>>()
        var curBoxes = 0
        loop@ for (pk in toPack) {
            var rem = pk.boxes
            while (rem > 0) {
                if (curBoxes >= cap) { pallets.add(cur); cur = ArrayList(); curBoxes = 0 }
                if (pallets.size >= 100) break@loop
                val take = minOf(rem, cap - curBoxes)
                cur.add(pk.code to take)
                curBoxes += take
                rem -= take
            }
        }
        if (cur.isNotEmpty() && pallets.size < 100) pallets.add(cur)

        var t = 0
        pallets.forEachIndexed { idx, items ->
            val plNo = "PL${idx + 1}"
            val detail = items.joinToString(" ") { (code, boxes) -> "$code:${boxes}x${qtyByCode[code] ?: 1}" }
            val dur = (6..14).random()
            if (idx == 0) {
                sb.append("${hms(now, t)},${hms(now, t + dur)},$plNo,$cust,$run,\"$detail\",0\r\n")
                sb.append("${hms(now, t + 60)},${hms(now, t + 60 + dur)},$plNo,$cust,$run,\"$detail\",1\r\n")
            } else {
                sb.append("${hms(now, t)},${hms(now, t + dur)},$plNo,$cust,$run,\"$detail\",0\r\n")
            }
            t += dur + (3..8).random()
        }
        // 1 dòng 状態 9 (削除) trên 1 PLNo. riêng → PC ẩn.
        val delCode = receivedBoxes.keys.firstOrNull() ?: products.first().code
        sb.append("${hms(now, t)},${hms(now, t + 5)},PL${pallets.size + 1},$cust,$run,\"$delCode:1x${qtyByCode[delCode] ?: 1}\",9\r\n")
        return sb.toString()
    }

    /**
     * direct (直送管理, 11 cột, 1 dòng = 1 照合 hoàn tất, KHÔNG có 状態 → PC hiện tất cả): ~30–100 dòng.
     * 開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番. 納入数 = 箱数 × 収容数.
     */
    private fun renderDirectCsv(now: Long, products: List<Product>): String {
        val sb = StringBuilder()
        sb.append("開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番\r\n")
        val custNames = listOf("林テレンプ", "トヨタ", "デンソー", "アイシン")
        val dests = listOf("—", "Q4810-1", "A1203-2", "K7782-3")
        val factories = listOf("1001", "1002", "1003", "1004")
        val target = (32..100).random()
        var t = 0
        repeat(target) {
            val p = products.random()
            val cust = custNames.random()
            val dest = dests.random()
            val shipDate = "2026/06/${(10..28).random()}"
            val partNo = "${(80000..99999).random()}-${(10000..99999).random()}"
            val capacity = p.qtyPerBox
            val boxes = (1..20).random()
            val delivered = boxes * capacity
            val factory = factories.random()
            val yokoo = "YK${p.code}${(100..999).random()}"
            val dur = (8..18).random()
            sb.append("${hms(now, t)},${hms(now, t + dur)},$cust,$dest,$shipDate,$partNo," +
                    "$capacity,$boxes,$delivered,$factory,$yokoo\r\n")
            t += dur + (2..6).random()
        }
        return sb.toString()
    }

    fun triggerSync() {
        // Tạm dừng ping ngay khi yêu cầu sync (bịt khoảng trễ enqueue WorkManager → sync chạy).
        BluetoothSyncManager.pausePing(10_000L)
        SyncScheduler.enqueueOnce(getApplication())
    }

    /**
     * Một nhịp heartbeat tới PC để kiểm tra listener còn sống & phản hồi (PING/PONG qua SPP).
     * Dùng cho thẻ trạng thái "Listener PC" trên màn hình chính. Trả null nếu nhịp này bị bỏ
     * qua (đang đồng bộ) — khi đó UI giữ nguyên trạng thái gần nhất.
     */
    suspend fun checkListener(): BluetoothSyncManager.HeartbeatResult? {
        val result = bluetooth.heartbeat(deviceName, selectedPcName, selectedPcAddress)
        if (result == null) {
            Log.d(TAG, "checkListener -> bỏ qua (đang đồng bộ).")
        } else {
            Log.d(TAG, "checkListener -> ok=${result.ok}, connected=${result.connected}, " +
                    "ack=${result.acknowledged}, radio='${result.pcRadioName}', err='${result.error}'")
        }
        return result
    }

    /**
     * Reset toàn bộ trạng thái app: huỷ pair mọi thiết bị, xoá PC đã lưu, xoá hết log,
     * tắt geofence. (Quyền: Android không cho thu hồi bằng code — UI sẽ mở màn hình Settings.)
     */
    fun resetAll(onDone: (Int) -> Unit) {
        Log.w(TAG, ">>> RESET ALL: huỷ pair + xoá PC + xoá log + tắt geofence.")
        viewModelScope.launch {
            val unpaired = runCatching { bluetooth.unpairAll() }.getOrDefault(0)
            config.clearPcTarget()
            config.geofenceEnabled = false
            // Reset bộ đếm index về 0 + xoá mẫu chờ gửi → test lại từ đầu, khớp khi xoá DB ở PC.
            config.resetUploadIndexes()
            pendingStore.clear()
            fileStore.clearAll() // xoá outbox + backup (file CSV trên đĩa)
            runCatching { geofenceManager.removeOfficeGeofence() }
            runCatching { dao.deleteAll() }
            Log.i(TAG, "RESET ALL hoàn tất (đã huỷ pair $unpaired thiết bị; đã reset index + xoá mẫu chờ gửi).")
            onDone(unpaired)
        }
    }

    /** Persists the chosen PC (name + address) as the Bluetooth sync target and syncs. */
    fun selectPcDevice(name: String, address: String) {
        Log.i(TAG, "Chọn PC mục tiêu: '$name' ($address)")
        config.pcBluetoothName = name
        config.pcBluetoothAddress = address
        triggerSync()
    }

    // Wi-Fi (parked).
    fun updateIpAddress(ip: String) {
        config.pcIpAddress = ip
    }

    /**
     * Bật/tắt geofence (switch). Khi BẬT: đăng ký geofence cho lần ENTER sau, ĐỒNG THỜI
     * chạy sync ngay (vì geofence chỉ bắn khi *đi vào* vùng — nếu đã ở trong sẽ không có
     * sự kiện ENTER, nên phải sync ngay để bạn thấy hoạt động liền).
     */
    fun setGeofenceEnabled(enabled: Boolean, onError: (Throwable) -> Unit) {
        Log.i(TAG, "Geofence switch -> $enabled")
        config.geofenceEnabled = enabled
        viewModelScope.launch {
            runCatching {
                if (enabled) {
                    geofenceManager.registerOfficeGeofence()
                    SyncScheduler.ensurePeriodicSync(getApplication())
                    triggerSync() // sync ngay khi bật, không chờ ENTER
                } else {
                    geofenceManager.removeOfficeGeofence()
                }
            }.onFailure {
                Log.e(TAG, "setGeofenceEnabled($enabled) lỗi: ${it.message}", it)
                config.geofenceEnabled = !enabled // revert on failure
                onError(it)
            }
        }
    }
}
