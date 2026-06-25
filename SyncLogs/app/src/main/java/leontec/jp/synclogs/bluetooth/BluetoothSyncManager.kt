package leontec.jp.synclogs.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.random.Random
import leontec.jp.synclogs.config.SyncConfig
import leontec.jp.synclogs.data.JobLog
import leontec.jp.synclogs.sync.LogPayloadSerializer
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.util.UUID

/**
 * Quản lý đồng bộ Bluetooth SPP (CLAUDE.md requirement).
 */
class BluetoothSyncManager(private val context: Context) {

    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val adapter: BluetoothAdapter? = bluetoothManager.adapter
    
    private val TAG = "BT_SYNC_DEBUG"
    private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    private val STX: Byte = 0x02 // Start of Text
    private val ETX: Byte = 0x03 // End of Text

    // Heartbeat (liveness) control frames — xem BluetoothSppServer phía PC. App định kỳ mở 1
    // kết nối SPP ngắn, gửi "PING,<deviceName>,<epochMillis>" và chờ PC trả
    // "PONG,<radioName>,<epochMillis>" để biết listener còn sống & phản hồi.
    private val PING = "PING"
    private val PONG = "PONG"
    private val HEARTBEAT_REPLY_TIMEOUT_MS = 3_000L

    // Data class để hiển thị trên UI
    data class PairedDevice(val name: String, val address: String, val bonded: Boolean = true)

    /**
     * Kết quả một nhịp heartbeat tới PC.
     *  - [connected]    : mở được socket SPP tới PC.
     *  - [acknowledged] : nhận được PONG hợp lệ (listener thực sự đọc & phản hồi).
     *  - [pcRadioName]  : tên radio PC lấy từ PONG (nếu có).
     *  - [error]        : mô tả lỗi ngắn để hiển thị (null nếu OK).
     * [ok] = listener PC sống & phản hồi.
     */
    data class HeartbeatResult(
        val connected: Boolean,
        val acknowledged: Boolean,
        val pcRadioName: String?,
        val error: String?
    ) {
        val ok: Boolean get() = connected && acknowledged
    }

    /**
     * Kết quả gửi file lên PC, phân loại đúng 5 trường hợp màn hình "ログ送信" cần hiển thị:
     *  - [BLUETOOTH_OFF]  ①: adapter null / Bluetooth chưa bật.
     *  - [NOT_PAIRED]     ②: chưa chọn PC hoặc không tìm thấy thiết bị PC đã ghép đôi.
     *  - [CONNECT_FAILED] ③: mở socket SPP thất bại (đã thử lại).
     *  - [SEND_FAILED]    ④: kết nối được nhưng ghi/flush dữ liệu thất bại.
     *  - [SUCCESS]        ⑤: tất cả file đã gửi xong.
     */
    enum class SendOutcome { BLUETOOTH_OFF, NOT_PAIRED, CONNECT_FAILED, SEND_FAILED, SUCCESS }

    companion object {
        /**
         * Độ sâu "đang đồng bộ" (đếm lồng nhau) ở cấp tiến trình (companion) vì `SyncWorker` và UI
         * dùng các instance khác nhau. > 0 ⇒ đang có luồng giữ/truyền SPP. Heartbeat đọc
         * [isSyncing] để **tuyệt đối không ping chen vào** trong SUỐT cả đợt sync (tránh tranh
         * chấp kết nối BR/EDR — chỉ 1 kết nối/lúc). Dùng bộ đếm (không phải bool) để khi
         * `SyncWorker` giữ cờ quanh CẢ batch mà bên trong mỗi `sendCsvFile`/`sync` lại begin/end,
         * cờ vẫn > 0 liên tục giữa các file (không bị rớt về 0 ở khe giữa 2 file → không lọt ping).
         */
        private val syncDepth = java.util.concurrent.atomic.AtomicInteger(0)
        fun beginSync() { syncDepth.incrementAndGet() }
        fun endSync() { if (syncDepth.decrementAndGet() < 0) syncDepth.set(0) }
        fun isSyncing(): Boolean = syncDepth.get() > 0

        /**
         * Thời điểm sync thành công gần nhất + tên PC. Một lần sync thành công ĐÃ chứng minh
         * listener PC sống & nhận được dữ liệu, nên trong cửa sổ [SYNC_OK_TTL_MS] coi như
         * "ping OK" luôn — KHỎI mở kết nối ping (ping chỉ là phụ, không được tranh chấp với sync).
         */
        @Volatile private var lastSyncOkUtc: Long = 0L
        @Volatile private var lastSyncPcName: String? = null
        private const val SYNC_OK_TTL_MS = 12_000L

        /**
         * Tên radio PC lấy từ PONG gần nhất (nguồn ĐÁNG TIN để hiển thị). Dùng để khỏi nhấp nháy
         * giữa tên đã-ghép-đôi (vd "LUYEN") khi vừa sync xong và tên radio thật (vd "LUYEN Front")
         * khi nhận PONG — luôn hiển thị tên radio nếu đã từng nhận PONG.
         */
        @Volatile private var lastPongRadioName: String? = null

        /** Đánh dấu một lần sync vừa thành công (gọi từ trong sync()). */
        fun markSyncOk(pcName: String?) {
            lastSyncOkUtc = System.currentTimeMillis()
            lastSyncPcName = pcName
        }

        /**
         * Tạm dừng ping tới thời điểm này. Gọi NGAY khi người dùng/auto yêu cầu sync, để bịt
         * khoảng trễ giữa lúc enqueue WorkManager và lúc `sync()` thực sự bật [syncInProgress]
         * — trong khoảng đó ping không được mở kết nối tranh chấp.
         */
        @Volatile private var pingPauseUntilUtc: Long = 0L
        fun pausePing(durationMs: Long) {
            pingPauseUntilUtc = System.currentTimeMillis() + durationMs
        }
    }

    fun isSupported(): Boolean = adapter != null
    fun isEnabled(): Boolean = adapter?.isEnabled == true
    fun isDisabled(): Boolean = isSupported() && !isEnabled()
    fun enableRequestIntent(): Intent = Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)

    @SuppressLint("MissingPermission")
    fun pairedDevices(): List<PairedDevice> {
        val btAdapter = adapter ?: return emptyList()
        if (!btAdapter.isEnabled) return emptyList()
        return btAdapter.bondedDevices.map { 
            PairedDevice(name = it.name ?: it.address, address = it.address, bonded = true) 
        }.sortedBy { it.name }
    }

    @SuppressLint("MissingPermission")
    fun bond(address: String): Boolean {
        Log.d(TAG, "Đang bắt đầu ghép đôi với thiết bị: $address")
        val device = adapter?.getRemoteDevice(address) ?: return false
        return device.createBond()
    }

    /**
     * Huỷ ghép đôi (unpair) tất cả thiết bị đã bonded. `removeBond` là API ẩn nên gọi qua
     * reflection; trả về số thiết bị đã huỷ thành công.
     */
    @SuppressLint("MissingPermission")
    fun unpairAll(): Int {
        val btAdapter = adapter ?: return 0
        val bonded = btAdapter.bondedDevices ?: return 0
        var removed = 0
        bonded.forEach { device ->
            runCatching {
                device.javaClass.getMethod("removeBond").invoke(device)
                removed++
                Log.i(TAG, "Đã huỷ ghép đôi: '${device.name}' (${device.address})")
            }.onFailure {
                Log.w(TAG, "Huỷ ghép đôi thất bại cho ${device.address}: ${it.message}")
            }
        }
        Log.i(TAG, "unpairAll: đã huỷ $removed/${bonded.size} thiết bị.")
        return removed
    }

    /**
     * Tự động bật Bluetooth adapter (chỉ hỗ trợ đến API 32). 
     * Từ Android 13 (API 33), app không thể bật BT âm thầm mà không có sự cho phép của người dùng.
     */
    @SuppressLint("MissingPermission")
    fun enableAdapter(): Boolean {
        if (adapter == null || adapter.isEnabled) return true
        
        return if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.S_V2) {
            @Suppress("DEPRECATION")
            adapter.enable()
        } else {
            // Android 13+ requires user interaction.
            false
        }
    }

    /**
     * Đồng bộ dữ liệu qua Bluetooth SPP (CSV format + STX/ETX).
     */
    @SuppressLint("MissingPermission")
    suspend fun sync(
        logs: List<JobLog>,
        targetName: String? = null,
        targetAddress: String? = null
    ): Boolean = withContext(Dispatchers.IO) {
        val btAdapter = adapter
        if (btAdapter == null || !btAdapter.isEnabled) {
            Log.w(TAG, "Bluetooth chưa sẵn sàng, bỏ qua SPP.")
            return@withContext false
        }

        val pcName = (targetName ?: "").trim()
        val pcAddr = (targetAddress ?: "").trim()
        Log.d(TAG, "Bắt đầu đồng bộ ${logs.size} logs (PC name='$pcName', addr='$pcAddr')")

        val target = resolveTarget(pcName, pcAddr, logBonded = true)
        if (target == null) {
            Log.w(TAG, "Chưa chọn/không tìm thấy PC (name='$pcName', addr='$pcAddr').")
            return@withContext false
        }
        Log.i(TAG, "Đã chọn PC mục tiêu: '${target.name}' (${target.address})")

        var socket: BluetoothSocket? = null
        beginSync() // báo heartbeat tạm ngừng ping trong lúc truyền dữ liệu
        try {
            socket = connectWithRetry(target)

            // GỬI MỖI BATCH = MỘT KHUNG CSV RIÊNG (STX + CSV + ETX). Nhờ vậy nếu có 4 CSV
            // thì PC nhận 4 frame -> hiện 4 dòng (không gộp thành 1).
            val out = socket.outputStream ?: throw IOException("Output stream null")
            val groups = logs.groupBy { it.batchId.ifBlank { it.id } } // log lẻ không batch -> mỗi cái 1 nhóm
            Log.i(TAG, "Sẽ gửi ${groups.size} CSV (batch) cho PC.")

            var totalBytes = 0
            for ((batchId, rows) in groups) {
                val csv = LogPayloadSerializer.toCsv(rows)
                val body = csv.toByteArray(Charsets.UTF_8)
                val packet = ByteArray(body.size + 2)
                packet[0] = STX
                System.arraycopy(body, 0, packet, 1, body.size)
                packet[packet.size - 1] = ETX

                out.write(packet)
                out.flush()
                totalBytes += packet.size
                Log.d(TAG, "Đã gửi CSV batch '$batchId' (${rows.size} dòng, ${packet.size} bytes).")
            }

            // Nhớ lại PC đã sync thành công để lần sau (kể cả auto) tự dùng đúng máy này.
            runCatching {
                val cfg = SyncConfig.get(context)
                cfg.pcBluetoothName = target.name ?: cfg.pcBluetoothName
                cfg.pcBluetoothAddress = target.address
            }

            // Sync thành công ⇒ listener PC chắc chắn sống & nhận được dữ liệu: tính luôn là
            // "ping OK" để heartbeat khỏi cần mở kết nối ping trong cửa sổ tiếp theo.
            markSyncOk(target.name)

            Log.i(TAG, "Đã gửi ${groups.size} CSV ($totalBytes bytes) tới '${target.name}' (${target.address}).")
            true
        } catch (e: IOException) {
            Log.e(TAG, "Lỗi truyền tin Bluetooth: ${e.message}")
            false
        } finally {
            runCatching { socket?.close() }
            endSync()
        }
    }

    /** Ngày hôm nay dạng yyyyMMdd (dùng làm ngày log mặc định trong tên file CSV gửi đi). */
    private fun todayYmd(): String =
        java.text.SimpleDateFormat("yyyyMMdd", java.util.Locale.US).format(java.util.Date())

    /**
     * Gửi MỘT file CSV (đã có sẵn dòng header ở row 1) tới PC. Dòng ĐẦU của khung là **tên file**
     * `{type}_{yyyyMMdd}_{termId}_{index}.txt` để PC suy ra type / NGÀY LOG / term_id / index.
     * Ví dụ: `monitor_log_20260622_GalaxyS10_3.txt` — **termID đứng TRƯỚC, index đứng SAU**.
     * `index` là cụm số ở cuối; `termId` (tên máy, đã bỏ khoảng trắng) nằm giữa ngày và index.
     * PC dùng NGÀY này cho bộ lọc "log theo ngày". Sau tên file là toàn bộ CSV.
     * Khung = STX + (filename\r\n + csv) + ETX.
     *
     * @param date ngày log dạng yyyyMMdd (mặc định = hôm nay — đúng cho dữ liệu sinh tại chỗ).
     */
    @SuppressLint("MissingPermission")
    suspend fun sendCsvFile(
        type: String,
        csvText: String,
        termId: String,
        index: Int,
        targetName: String? = null,
        targetAddress: String? = null,
        date: String = todayYmd()
    ): Boolean = withContext(Dispatchers.IO) {
        val btAdapter = adapter
        if (btAdapter == null || !btAdapter.isEnabled) {
            Log.w(TAG, "Bluetooth chưa sẵn sàng, bỏ gửi CSV file.")
            return@withContext false
        }
        val target = resolveTarget((targetName ?: "").trim(), (targetAddress ?: "").trim(), logBonded = false)
        if (target == null) {
            Log.w(TAG, "sendCsvFile: chưa chọn/không tìm thấy PC.")
            return@withContext false
        }

        val safeTerm = termId.ifBlank { "unknown" }
        val filename = "${type}_${date}_${safeTerm}_${index}.txt"
        val payload = filename + "\r\n" + csvText
        val body = payload.toByteArray(Charsets.UTF_8)
        val packet = ByteArray(body.size + 2)
        packet[0] = STX
        System.arraycopy(body, 0, packet, 1, body.size)
        packet[packet.size - 1] = ETX

        var socket: BluetoothSocket? = null
        beginSync()
        try {
            socket = connectWithRetry(target)
            socket.outputStream.write(packet)
            socket.outputStream.flush()
            markSyncOk(target.name)
            Log.i(TAG, "Đã gửi CSV file '$filename' (${packet.size} bytes) tới '${target.name}'.")
            true
        } catch (e: IOException) {
            Log.e(TAG, "Lỗi gửi CSV file '$filename': ${e.message}")
            false
        } finally {
            runCatching { socket?.close() }
            endSync()
        }
    }

    /**
     * Gửi **MỘT** file CSV lên PC trên một kết nối SPP riêng và phân loại kết quả theo
     * [SendOutcome] (đúng 5 trường hợp màn hình "ログ送信" cần). Gửi từng file riêng để biết
     * **file nào** thành công. Khung = `STX + (filename\r\n + csv) + ETX`, filename =
     * `{type}_{date}_{termId}_{index}.txt` (giống [sendCsvFile]) nên PC vẫn suy ra
     * type / ngày / term / index như cũ. Không tự bật Bluetooth: nếu tắt thì trả
     * [SendOutcome.BLUETOOTH_OFF] để UI nhắc người dùng bật.
     */
    @SuppressLint("MissingPermission")
    suspend fun sendOneCsv(
        type: String,
        csvText: String,
        termId: String,
        index: Int,
        date: String,
        targetName: String? = null,
        targetAddress: String? = null
    ): SendOutcome = withContext(Dispatchers.IO) {
        val btAdapter = adapter
        if (btAdapter == null || !btAdapter.isEnabled) {
            Log.w(TAG, "sendOneCsv: Bluetooth chưa bật → BLUETOOTH_OFF.")
            return@withContext SendOutcome.BLUETOOTH_OFF
        }

        val pcName = (targetName ?: "").trim()
        val pcAddr = (targetAddress ?: "").trim()
        val target = resolveTarget(pcName, pcAddr, logBonded = false)
        if (target == null) {
            Log.w(TAG, "sendOneCsv: chưa ghép đôi/không tìm thấy PC (name='$pcName', addr='$pcAddr') → NOT_PAIRED.")
            return@withContext SendOutcome.NOT_PAIRED
        }

        val safeTerm = termId.ifBlank { "unknown" }
        val filename = "${type}_${date}_${safeTerm}_${index}.txt"
        val payload = filename + "\r\n" + csvText
        val body = payload.toByteArray(Charsets.UTF_8)
        val packet = ByteArray(body.size + 2)
        packet[0] = STX
        System.arraycopy(body, 0, packet, 1, body.size)
        packet[packet.size - 1] = ETX

        var socket: BluetoothSocket? = null
        beginSync() // chặn heartbeat ping chen vào trong lúc truyền
        try {
            val sock = try {
                connectWithRetry(target)
            } catch (e: IOException) {
                Log.e(TAG, "sendOneCsv '$filename': kết nối SPP thất bại (${e.message}) → CONNECT_FAILED.")
                return@withContext SendOutcome.CONNECT_FAILED
            }
            socket = sock

            try {
                sock.outputStream.write(packet)
                sock.outputStream.flush()
            } catch (e: IOException) {
                Log.e(TAG, "sendOneCsv '$filename': ghi/flush thất bại (${e.message}) → SEND_FAILED.")
                return@withContext SendOutcome.SEND_FAILED
            }

            // Gửi xong → listener PC chắc chắn sống & nhận được dữ liệu.
            runCatching {
                val cfg = SyncConfig.get(context)
                cfg.pcBluetoothName = target.name ?: cfg.pcBluetoothName
                cfg.pcBluetoothAddress = target.address
            }
            markSyncOk(target.name)
            Log.i(TAG, "sendOneCsv: đã gửi '$filename' (${packet.size} bytes) tới '${target.name}' → SUCCESS.")
            SendOutcome.SUCCESS
        } finally {
            runCatching { socket?.close() }
            endSync()
        }
    }

    /**
     * Một nhịp heartbeat: mở kết nối SPP ngắn tới PC, gửi PING và chờ PONG để xác nhận
     * listener PC còn sống & phản hồi. Đây cũng là cách app biết "listener OK" (không chỉ
     * "PC đã ghép đôi"). Không gửi dữ liệu log nên không ảnh hưởng tới đồng bộ.
     */
    @SuppressLint("MissingPermission")
    suspend fun heartbeat(
        deviceName: String,
        targetName: String? = null,
        targetAddress: String? = null
    ): HeartbeatResult? = withContext(Dispatchers.IO) {
        // Ping là phụ, TUYỆT ĐỐI không được tranh chấp với luồng sync:
        //  - Nếu vừa sync thành công gần đây ⇒ coi như listener OK luôn, KHỎI mở kết nối ping.
        //  - Nếu đang sync ⇒ bỏ qua nhịp này (trả null, UI giữ trạng thái cũ).
        val sinceSyncOk = System.currentTimeMillis() - lastSyncOkUtc
        if (lastSyncOkUtc > 0L && sinceSyncOk in 0..SYNC_OK_TTL_MS) {
            Log.d(TAG, "Heartbeat: bỏ ping — vừa sync OK ${sinceSyncOk}ms trước (tính là listener OK).")
            // Ưu tiên tên radio từ PONG để hiển thị nhất quán (xem lastPongRadioName).
            return@withContext HeartbeatResult(true, true, lastPongRadioName ?: lastSyncPcName, null)
        }
        if (isSyncing() || System.currentTimeMillis() < pingPauseUntilUtc) {
            Log.d(TAG, "Heartbeat: bỏ qua (đang đồng bộ hoặc vừa yêu cầu sync).")
            return@withContext null
        }

        val btAdapter = adapter
        if (btAdapter == null || !btAdapter.isEnabled) {
            Log.w(TAG, "Heartbeat: Bluetooth chưa sẵn sàng.")
            return@withContext HeartbeatResult(false, false, null, "Bluetooth tắt")
        }

        val pcName = (targetName ?: "").trim()
        val pcAddr = (targetAddress ?: "").trim()
        val target = resolveTarget(pcName, pcAddr, logBonded = false)
        if (target == null) {
            Log.w(TAG, "Heartbeat: không tìm thấy PC (name='$pcName', addr='$pcAddr').")
            return@withContext HeartbeatResult(false, false, null, "Không tìm thấy PC")
        }

        var socket: BluetoothSocket? = null
        try {
            socket = connect(target)
            val out = socket.outputStream ?: throw IOException("Output stream null")

            val ts = System.currentTimeMillis()
            val ping = "$PING,$deviceName,$ts"
            val body = ping.toByteArray(Charsets.UTF_8)
            val packet = ByteArray(body.size + 2)
            packet[0] = STX
            System.arraycopy(body, 0, packet, 1, body.size)
            packet[packet.size - 1] = ETX
            out.write(packet)
            out.flush()
            Log.d(TAG, "Heartbeat: đã gửi PING tới '${target.name}'. Chờ PONG (timeout ${HEARTBEAT_REPLY_TIMEOUT_MS}ms)...")

            // Đọc PONG có giới hạn thời gian. read() là blocking; nếu timeout, coroutine bị
            // huỷ và finally đóng socket -> bung khỏi read.
            val input = socket.inputStream
            val reply = withTimeoutOrNull(HEARTBEAT_REPLY_TIMEOUT_MS) { readFrame(input) }

            if (reply.isNullOrBlank()) {
                Log.w(TAG, "Heartbeat: không nhận được PONG (timeout). Listener có thể không phản hồi.")
                return@withContext HeartbeatResult(true, false, null, "PC không phản hồi")
            }

            val parts = reply.split(",")
            if (parts.isEmpty() || !parts[0].trim().equals(PONG, ignoreCase = true)) {
                Log.w(TAG, "Heartbeat: phản hồi không hợp lệ: '$reply'.")
                return@withContext HeartbeatResult(true, false, null, "Phản hồi lạ")
            }

            val pcRadio = parts.getOrNull(1)?.trim()
            if (!pcRadio.isNullOrBlank()) lastPongRadioName = pcRadio   // nhớ tên radio thật của PC
            Log.i(TAG, "Heartbeat OK: listener PC '${pcRadio ?: "?"}' đã phản hồi PONG.")
            HeartbeatResult(true, true, pcRadio, null)
        } catch (e: IOException) {
            Log.w(TAG, "Heartbeat: không kết nối được tới PC: ${e.message}")
            HeartbeatResult(false, false, null, "Không kết nối được")
        } finally {
            runCatching { socket?.close() }
        }
    }

    /**
     * Tìm thiết bị PC mục tiêu: ưu tiên MAC đã lưu (chính xác), sau đó khớp tên linh hoạt
     * (tên PC trên Windows thường có hậu tố, vd "LUYEN - Front").
     */
    @SuppressLint("MissingPermission")
    private fun resolveTarget(pcName: String, pcAddr: String, logBonded: Boolean): BluetoothDevice? {
        val btAdapter = adapter ?: return null
        val bonded = btAdapter.bondedDevices.toList()
        if (logBonded) {
            Log.d(TAG, "Có ${bonded.size} thiết bị đã ghép đôi:")
            bonded.forEach { Log.d(TAG, "  - '${it.name}' (${it.address})") }
        }
        return when {
            pcAddr.isNotBlank() ->
                bonded.firstOrNull { it.address.equals(pcAddr, ignoreCase = true) }
                    ?: runCatching { btAdapter.getRemoteDevice(pcAddr) }.getOrNull()
            pcName.isNotBlank() ->
                bonded.firstOrNull { it.name.equals(pcName, ignoreCase = true) }
                    ?: bonded.firstOrNull { dev ->
                        val n = dev.name ?: return@firstOrNull false
                        n.contains(pcName, ignoreCase = true) || pcName.contains(n, ignoreCase = true)
                    }
            // Chưa lưu PC nào (vd vừa Reset rồi pair lại bằng Cài đặt hệ thống): TỰ chọn thiết bị
            // đã ghép đôi có khả năng là PC → bấm gửi là chạy luôn, không bắt chọn PC thủ công.
            else -> autoPickBondedPc(bonded)
        }
    }

    /**
     * Chọn thiết bị đã ghép đôi có khả năng là PC khi app chưa lưu PC mục tiêu: ưu tiên thiết bị
     * **lớp Máy tính** (`BluetoothClass` major = COMPUTER), nếu không có thì lấy thiết bị bonded
     * **duy nhất** (tránh đoán sai khi có nhiều thiết bị). Trả null nếu không xác định được.
     */
    @SuppressLint("MissingPermission")
    private fun autoPickBondedPc(bonded: List<BluetoothDevice>): BluetoothDevice? =
        bonded.firstOrNull { it.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.COMPUTER }
            ?: bonded.singleOrNull()

    /**
     * Dò một thiết bị PC đã ghép đôi (qua [autoPickBondedPc]) để app **tự nhận** sau khi người dùng
     * pair bằng Cài đặt hệ thống. Trả null nếu Bluetooth tắt hoặc không xác định được.
     */
    @SuppressLint("MissingPermission")
    fun detectPcCandidate(): PairedDevice? {
        val a = adapter ?: return null
        if (!a.isEnabled) return null
        val pc = autoPickBondedPc(a.bondedDevices?.toList().orEmpty()) ?: return null
        return PairedDevice(pc.name ?: pc.address, pc.address, bonded = true)
    }

    /**
     * Mở socket SPP có **thử lại + jitter**. Bluetooth Classic chỉ thiết lập được MỘT kết nối
     * tại một thời điểm ở tầng radio, nên khi 2 máy bấm đồng bộ cùng lúc thì 1 máy sẽ bị
     * từ chối/timeout. Thử lại nhanh vài lần (backoff ngẫu nhiên để 2 máy lệch pha) giúp máy
     * "thua" kết nối được ngay trong vài giây, thay vì phải chờ WorkManager retry (10s).
     */
    @SuppressLint("MissingPermission")
    private suspend fun connectWithRetry(target: BluetoothDevice, attempts: Int = 4): BluetoothSocket {
        var last: IOException? = null
        for (i in 0 until attempts) {
            try {
                return connect(target)
            } catch (e: IOException) {
                last = e
                if (i == attempts - 1) break
                val backoff = 250L * (i + 1) + Random.nextLong(0, 450)
                Log.w(TAG, "Kết nối lần ${i + 1}/$attempts thất bại (${e.message}); " +
                        "thử lại sau ${backoff}ms (tránh đụng độ 2 máy cùng kết nối).")
                delay(backoff)
            }
        }
        throw last ?: IOException("Không kết nối được sau $attempts lần")
    }

    /** Mở socket SPP: thử Secure trước, fallback Insecure nếu thất bại. */
    @SuppressLint("MissingPermission")
    private fun connect(target: BluetoothDevice): BluetoothSocket {
        if (adapter?.isDiscovering == true) adapter.cancelDiscovery()
        return try {
            target.createRfcommSocketToServiceRecord(SPP_UUID).also {
                it.connect()
                Log.i(TAG, "Kết nối Bluetooth SPP (Secure) thành công tới '${target.name}'.")
            }
        } catch (e: IOException) {
            Log.w(TAG, "Kết nối Secure thất bại: ${e.message}. Thử Insecure fallback...")
            target.createInsecureRfcommSocketToServiceRecord(SPP_UUID).also {
                it.connect()
                Log.i(TAG, "Kết nối Bluetooth SPP (Insecure) thành công tới '${target.name}'.")
            }
        }
    }

    /**
     * Đọc một khung STX..ETX từ stream (blocking) và trả phần text bên trong (UTF-8).
     * Trả null nếu stream kết thúc trước khi có khung hoàn chỉnh.
     */
    private fun readFrame(input: InputStream): String? {
        val out = ByteArrayOutputStream()
        var started = false
        while (true) {
            val b = input.read()
            if (b == -1) return if (started && out.size() > 0) out.toString("UTF-8").trim() else null
            when (b.toByte()) {
                STX -> { started = true; out.reset() }
                ETX -> if (started) return out.toString("UTF-8").trim()
                else -> if (started) out.write(b)
            }
        }
    }
}
