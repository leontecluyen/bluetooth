package leontec.jp.synclogs

import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.withTimeoutOrNull
import leontec.jp.synclogs.bluetooth.BluetoothScanner
import leontec.jp.synclogs.bluetooth.BluetoothSyncManager
import leontec.jp.synclogs.bluetooth.BluetoothSyncManager.SendOutcome
import leontec.jp.synclogs.config.SyncConfig
import leontec.jp.synclogs.data.CsvFileEntry
import leontec.jp.synclogs.ui.MainViewModel
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/**
 * Màn hình "ログ送信" (gửi log lên PC) — mở từ nút menu ở [MainActivity].
 * Cho chọn NGÀY (mặc định hôm nay, chặn ngày tương lai), lọc file 未送信/送信済, chọn file rồi
 * bấm 送信 để gửi qua Bluetooth SPP. Kết quả gửi hiển thị đúng 5 thông báo (BT off / chưa pair /
 * lỗi kết nối / lỗi gửi / thành công). Có nút tạo log mẫu cho ngày đang chọn và menu ☰ (chọn PC /
 * ngôn ngữ / reset).
 */
class LogSendActivity : ComponentActivity() {

    private val TAG = "LOG_SEND_ACTIVITY"
    private val promptBtEnable = mutableStateOf(false)

    override fun attachBaseContext(newBase: Context) {
        val lang = SyncConfig.get(newBase).appLanguage
        super.attachBaseContext(LocaleHelper.wrap(newBase, lang))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        promptBtEnable.value = intent?.getBooleanExtra(MainActivity.EXTRA_PROMPT_BT_ENABLE, false) == true
        Log.d(TAG, "onCreate: LogSend started (promptBtEnable=${promptBtEnable.value})")
        setContent {
            MaterialTheme {
                LogSendScreen(promptBtEnable = promptBtEnable, onBack = { finish() })
            }
        }
    }
}

// ---- Permission helpers (BT cần để mở socket; SCAN cần khi quét chọn PC) ----

private fun requiredPermissions(): Array<String> = buildList {
    add(android.Manifest.permission.ACCESS_FINE_LOCATION)
    add(android.Manifest.permission.ACCESS_COARSE_LOCATION)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        add(android.Manifest.permission.BLUETOOTH_CONNECT)
        add(android.Manifest.permission.BLUETOOTH_SCAN)
    }
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        add(android.Manifest.permission.POST_NOTIFICATIONS)
    }
}.toTypedArray()

private fun hasBtConnectPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
        ContextCompat.checkSelfPermission(context, android.Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

private fun hasBtScanPermission(context: Context): Boolean {
    val perm = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) android.Manifest.permission.BLUETOOTH_SCAN else android.Manifest.permission.ACCESS_FINE_LOCATION
    return ContextCompat.checkSelfPermission(context, perm) == PackageManager.PERMISSION_GRANTED
}

private fun hasAllPermissions(context: Context): Boolean =
    hasBtConnectPermission(context) && hasBtScanPermission(context)

// ---- Date helpers (yyyyMMdd; so sánh chuỗi = so sánh thời gian) ----

private fun todayYmd(): String =
    SimpleDateFormat("yyyyMMdd", Locale.US).format(Date())

private fun shiftYmd(ymd: String, deltaDays: Int): String {
    val fmt = SimpleDateFormat("yyyyMMdd", Locale.US)
    val cal = Calendar.getInstance()
    cal.time = runCatching { fmt.parse(ymd) }.getOrNull() ?: Date()
    cal.add(Calendar.DAY_OF_MONTH, deltaDays)
    return fmt.format(cal.time)
}

/** "20260625" → "2026/06/25" cho hiển thị. */
private fun ymdToDisplay(ymd: String): String =
    if (ymd.length == 8) "${ymd.substring(0, 4)}/${ymd.substring(4, 6)}/${ymd.substring(6, 8)}" else ymd

@Composable
private fun LogSendScreen(
    promptBtEnable: State<Boolean>,
    onBack: () -> Unit,
    viewModel: MainViewModel = viewModel()
) {
    val context = LocalContext.current
    val bluetooth = remember { BluetoothSyncManager(context) }
    val scanner = remember { BluetoothScanner(context) }

    var refresh by remember { mutableIntStateOf(0) }
    var selectedDate by remember { mutableStateOf(todayYmd()) }
    var showSent by remember { mutableStateOf(false) }
    var sending by remember { mutableStateOf(false) }

    // Chọn PC
    val deviceMap = remember { mutableStateMapOf<String, BluetoothSyncManager.PairedDevice>() }
    var showPicker by remember { mutableStateOf(false) }
    var scanning by remember { mutableStateOf(false) }
    var showResetConfirm by remember { mutableStateOf(false) }
    var menuOpen by remember { mutableStateOf(false) }

    // Danh sách file của ngày: outbox (chưa gửi) hoặc backup (đã gửi).
    val rows: List<CsvFileEntry> = remember(selectedDate, showSent, refresh) {
        if (showSent) viewModel.sentFiles(selectedDate) else viewModel.unsentFiles(selectedDate)
    }
    // Chọn file (key = đường dẫn file): mặc định chọn TẤT CẢ; reset khi danh sách đổi.
    val selectedIds = remember { mutableStateMapOf<String, Boolean>() }
    LaunchedEffect(rows) {
        selectedIds.clear()
        rows.forEach { selectedIds[it.file.absolutePath] = true }
    }
    val selectedCount = rows.count { selectedIds[it.file.absolutePath] == true }
    val allSelected = rows.isNotEmpty() && selectedCount == rows.size

    // Quyền + bật Bluetooth
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        refresh++
        if (!result.values.all { it }) {
            Toast.makeText(context, context.getString(R.string.toast_missing_permission), Toast.LENGTH_LONG).show()
        }
    }
    val enableBtLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { refresh++ }

    // Xin quyền ngay khi vào màn hình nếu còn thiếu (cần cho mọi thao tác Bluetooth).
    LaunchedEffect(Unit) {
        if (!hasAllPermissions(context)) permissionLauncher.launch(requiredPermissions())
    }
    // Mở từ thông báo geofence → bật Bluetooth ngay.
    LaunchedEffect(promptBtEnable.value) {
        if (promptBtEnable.value && bluetooth.isDisabled()) {
            enableBtLauncher.launch(bluetooth.enableRequestIntent())
        }
    }

    // Quay lại app (ON_RESUME) → làm mới: bắt được việc vừa pair PC bằng Cài đặt hệ thống.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val obs = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_RESUME) refresh++ }
        lifecycleOwner.lifecycle.addObserver(obs)
        onDispose { lifecycleOwner.lifecycle.removeObserver(obs) }
    }
    // Chưa lưu PC nhưng đã pair (bằng hệ thống) → TỰ nhận thiết bị PC để hết báo "chưa pair".
    LaunchedEffect(refresh) {
        if (!viewModel.hasPcTarget && hasBtConnectPermission(context) && viewModel.autoAdoptPc()) {
            refresh++
        }
    }

    val openDevicePicker = {
        deviceMap.clear()
        bluetooth.pairedDevices().forEach { deviceMap[it.address] = it }
        showPicker = true
    }
    LaunchedEffect(showPicker) {
        if (showPicker) {
            scanning = true
            withTimeoutOrNull(15_000) {
                scanner.discover().collect { device -> deviceMap[device.address] = device }
            }
            scanning = false
        }
    }

    if (showPicker) {
        DevicePickerDialog(
            devices = deviceMap.values.toList().sortedByDescending { it.bonded },
            scanning = scanning,
            currentName = viewModel.selectedPcName,
            onSelect = { device ->
                showPicker = false
                if (!device.bonded) {
                    bluetooth.bond(device.address)
                    Toast.makeText(context, context.getString(R.string.toast_pairing, device.name), Toast.LENGTH_SHORT).show()
                }
                viewModel.selectPcDevice(device.name, device.address)
                refresh++
            },
            onDismiss = { showPicker = false }
        )
    }

    if (showResetConfirm) {
        AlertDialog(
            onDismissRequest = { showResetConfirm = false },
            title = { Text(stringResource(R.string.dialog_reset_title)) },
            text = { Text(stringResource(R.string.dialog_reset_body)) },
            confirmButton = {
                TextButton(onClick = {
                    showResetConfirm = false
                    viewModel.resetAll { unpaired ->
                        refresh++
                        Toast.makeText(context, context.getString(R.string.toast_reset_done, unpaired), Toast.LENGTH_LONG).show()
                    }
                }) { Text(stringResource(R.string.btn_reset), color = Color(0xFFB00020)) }
            },
            dismissButton = { TextButton(onClick = { showResetConfirm = false }) { Text(stringResource(R.string.btn_cancel)) } }
        )
    }

    Scaffold { innerPadding ->
        Column(
            modifier = Modifier.fillMaxSize().padding(innerPadding).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // Thanh tiêu đề: ← + tiêu đề + ☰
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                TextButton(onClick = onBack) { Text("←") }
                Text(stringResource(R.string.logsend_title), style = MaterialTheme.typography.headlineSmall)
                Box {
                    TextButton(onClick = { menuOpen = true }) { Text("☰") }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.btn_choose_pc)) },
                            onClick = { menuOpen = false; openDevicePicker() }
                        )
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.btn_reset_all), color = Color(0xFFB00020)) },
                            onClick = { menuOpen = false; showResetConfirm = true }
                        )
                        HorizontalDivider()
                        listOf(
                            "system" to R.string.lang_system,
                            "en" to R.string.lang_en,
                            "vi" to R.string.lang_vi,
                            "ja" to R.string.lang_ja
                        ).forEach { (code, labelRes) ->
                            DropdownMenuItem(
                                text = { Text("🌐 " + stringResource(labelRes)) },
                                onClick = {
                                    menuOpen = false
                                    val cfg = SyncConfig.get(context)
                                    if (cfg.appLanguage != code) {
                                        cfg.appLanguage = code
                                        (context as? Activity)?.recreate()
                                    }
                                }
                            )
                        }
                    }
                }
            }

            // Tên PC mục tiêu (để biết đang gửi tới đâu).
            Text(
                stringResource(R.string.status_ready, viewModel.selectedPcName.ifBlank { stringResource(R.string.pc_saved) }),
                style = MaterialTheme.typography.bodySmall,
                color = Color(0xFF616161)
            )

            // 送信日 (ngày gửi) — chặn ngày tương lai (cho chọn tới hôm nay).
            val nextEnabled = selectedDate < todayYmd()
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.logsend_date_label))
                TextButton(onClick = { selectedDate = shiftYmd(selectedDate, -1) }) { Text("◀") }
                Text(ymdToDisplay(selectedDate), style = MaterialTheme.typography.titleMedium)
                TextButton(onClick = { if (nextEnabled) selectedDate = shiftYmd(selectedDate, +1) }, enabled = nextEnabled) { Text("▶") }
            }
            Text(stringResource(R.string.logsend_date_note), style = MaterialTheme.typography.labelSmall, color = Color(0xFF616161))

            // 対象ファイル: 未送信 / 送信済
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(stringResource(R.string.logsend_target_label))
                Spacer(Modifier.width(8.dp))
                RadioButton(selected = !showSent, onClick = { showSent = false })
                Text(stringResource(R.string.logsend_filter_unsent))
                RadioButton(selected = showSent, onClick = { showSent = true })
                Text(stringResource(R.string.logsend_filter_sent))
            }

            // 全選択 + 選択 N 件
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = allSelected,
                        enabled = rows.isNotEmpty(),
                        onCheckedChange = { checked -> rows.forEach { selectedIds[it.file.absolutePath] = checked } }
                    )
                    Text(stringResource(R.string.logsend_select_all))
                }
                Text(stringResource(R.string.logsend_selected_count, selectedCount), color = Color(0xFF616161))
            }

            // Danh sách file của ngày (mỗi loại 1 dòng).
            LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                if (rows.isEmpty()) {
                    item { Text(stringResource(R.string.logsend_empty), style = MaterialTheme.typography.bodySmall, color = Color(0xFF616161)) }
                }
                items(rows) { entry ->
                    val key = entry.file.absolutePath
                    Card(modifier = Modifier.fillMaxWidth().clickable { selectedIds[key] = !(selectedIds[key] == true) }) {
                        Row(
                            modifier = Modifier.fillMaxWidth().padding(8.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Checkbox(
                                checked = selectedIds[key] == true,
                                onCheckedChange = { selectedIds[key] = it }
                            )
                            Text(entry.displayName, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
            }

            // Nút tạo log mẫu (3 file chưa gửi) cho NGÀY đang chọn.
            TextButton(onClick = {
                viewModel.createSampleFiles(selectedDate) {
                    showSent = false // file mẫu mới nằm ở nhóm 未送信
                    refresh++
                    Toast.makeText(context, context.getString(R.string.logsend_sample_created, ymdToDisplay(selectedDate)), Toast.LENGTH_LONG).show()
                }
            }) { Text(stringResource(R.string.logsend_btn_create_sample)) }

            // 送信 — chỉ gửi từ nhóm 未送信 (file chưa gửi trong outbox).
            Button(
                onClick = {
                    val toSend = rows.filter { selectedIds[it.file.absolutePath] == true }
                    if (toSend.isEmpty()) {
                        Toast.makeText(context, context.getString(R.string.logsend_no_selection), Toast.LENGTH_SHORT).show()
                        return@Button
                    }
                    sending = true
                    viewModel.sendDay(toSend) { outcome ->
                        sending = false
                        val msgRes = when (outcome) {
                            SendOutcome.BLUETOOTH_OFF -> R.string.send_result_bt_off
                            SendOutcome.NOT_PAIRED -> R.string.send_result_not_paired
                            SendOutcome.CONNECT_FAILED -> R.string.send_result_connect_failed
                            SendOutcome.SEND_FAILED -> R.string.send_result_send_failed
                            SendOutcome.SUCCESS -> R.string.send_result_success
                        }
                        Toast.makeText(context, context.getString(msgRes), Toast.LENGTH_LONG).show()
                        refresh++ // file gửi thành công đã chuyển sang backup (送信済); file lỗi vẫn ở 未送信
                    }
                },
                enabled = !sending && !showSent && selectedCount > 0,
                modifier = Modifier.fillMaxWidth().height(52.dp)
            ) { Text(if (sending) stringResource(R.string.logsend_sending) else stringResource(R.string.logsend_btn_send)) }

            Text(stringResource(R.string.logsend_note_oneday), style = MaterialTheme.typography.labelSmall, color = Color(0xFF616161))
            Text(stringResource(R.string.logsend_note_backup), style = MaterialTheme.typography.labelSmall, color = Color(0xFF616161))
        }
    }
}

@Composable
private fun DevicePickerDialog(
    devices: List<BluetoothSyncManager.PairedDevice>,
    scanning: Boolean,
    currentName: String,
    onSelect: (BluetoothSyncManager.PairedDevice) -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (scanning) stringResource(R.string.picker_scanning_title) else stringResource(R.string.picker_title)) },
        text = {
            LazyColumn {
                if (devices.isEmpty() && !scanning) {
                    item { Text(stringResource(R.string.picker_none)) }
                }
                items(devices) { device ->
                    Column(modifier = Modifier.fillMaxWidth().clickable { onSelect(device) }.padding(8.dp)) {
                        val color = if (device.bonded) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
                        val current = if (device.name == currentName) "  ✓" else ""
                        Text(device.name + current, color = color)
                        Text(if (device.bonded) stringResource(R.string.device_bonded) else stringResource(R.string.device_available), style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.btn_close)) } }
    )
}
