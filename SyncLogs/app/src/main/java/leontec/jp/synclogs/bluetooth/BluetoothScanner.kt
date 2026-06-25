package leontec.jp.synclogs.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow

/**
 * Quét thiết bị Bluetooth Classic (Discovery).
 */
class BluetoothScanner(private val context: Context) {

    private val TAG = "BT_SCANNER_DEBUG"
    private val adapter: BluetoothAdapter? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
        } else {
            @Suppress("DEPRECATION")
            BluetoothAdapter.getDefaultAdapter()
        }

    @SuppressLint("MissingPermission")
    fun discover(): Flow<BluetoothSyncManager.PairedDevice> = callbackFlow {
        Log.d(TAG, ">>> Bắt đầu Flow discover()")
        
        val btAdapter = adapter
        if (btAdapter == null) {
            Log.e(TAG, "Lỗi: Thiết bị không hỗ trợ Bluetooth adapter.")
            close()
            return@callbackFlow
        }

        if (!btAdapter.isEnabled) {
            Log.w(TAG, "Cảnh báo: Bluetooth đang tắt, không thể quét.")
            close()
            return@callbackFlow
        }

        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                val action = intent.action
                Log.v(TAG, "Nhận Broadcast action: $action")
                
                when (action) {
                    BluetoothDevice.ACTION_FOUND -> {
                        val device = extractDevice(intent)
                        if (device != null) {
                            val name = runCatching { device.name }.getOrNull() ?: "Unknown"
                            val address = device.address
                            Log.i(TAG, "--- TÌM THẤY THIẾT BỊ: $name [$address] ---")
                            
                            trySend(
                                BluetoothSyncManager.PairedDevice(
                                    name = name,
                                    address = address,
                                    bonded = false
                                )
                            )
                        }
                    }
                    BluetoothAdapter.ACTION_DISCOVERY_STARTED -> {
                        Log.d(TAG, "Discovery: Bắt đầu quét (System message)")
                    }
                    BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                        Log.d(TAG, "Discovery: Kết thúc quét (System message)")
                    }
                }
            }
        }

        val filter = IntentFilter().apply {
            addAction(BluetoothDevice.ACTION_FOUND)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_STARTED)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
        }

        // Đăng ký receiver. Với Android 14+, hệ thống yêu cầu RECEIVER_EXPORTED cho system broadcast như Bluetooth
        Log.d(TAG, "Đang đăng ký BroadcastReceiver...")
        context.registerReceiver(receiver, filter)

        // Nếu đang quét thì hủy để bắt đầu lại
        if (btAdapter.isDiscovering) {
            Log.d(TAG, "Đang quét dở, hủy để restart...")
            btAdapter.cancelDiscovery()
        }

        val success = btAdapter.startDiscovery()
        Log.i(TAG, "Lệnh startDiscovery() trả về: $success")
        if (!success) {
            Log.e(TAG, "Không thể khởi động Discovery. Kiểm tra quyền SCAN và Location (GPS) phải BẬT.")
        }

        awaitClose {
            Log.d(TAG, "<<< Đóng Flow discover(), hủy Receiver và dừng quét.")
            runCatching { btAdapter.cancelDiscovery() }
            runCatching { context.unregisterReceiver(receiver) }
        }
    }

    @Suppress("DEPRECATION")
    private fun extractDevice(intent: Intent): BluetoothDevice? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
        } else {
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
        }
}
