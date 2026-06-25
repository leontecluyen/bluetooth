package leontec.jp.synclogs

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.Modifier
import leontec.jp.synclogs.config.SyncConfig

/**
 * Màn hình MENU (entry point). Theo yêu cầu: chỉ còn MỘT nút dạng thẻ "④ ログ送信" — bấm vào mở
 * [LogSendActivity] (màn hình gửi log lên PC). Mọi thao tác chọn PC / tạo mẫu / gửi đã chuyển sang
 * [LogSendActivity].
 */
class MainActivity : ComponentActivity() {
    private val TAG = "MAIN_ACTIVITY_DEBUG"

    /** Mở từ thông báo geofence (bật Bluetooth) → chuyển tiếp cờ này sang màn hình gửi log. */
    private val promptBtEnable = mutableStateOf(false)

    /** Apply the user's chosen UI language to this activity's resources (follows system by default). */
    override fun attachBaseContext(newBase: Context) {
        val lang = SyncConfig.get(newBase).appLanguage
        super.attachBaseContext(LocaleHelper.wrap(newBase, lang))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Log.d(TAG, "onCreate: Menu started")
        promptBtEnable.value = intent?.getBooleanExtra(EXTRA_PROMPT_BT_ENABLE, false) == true
        setContent {
            MaterialTheme {
                MenuScreen(promptBtEnable = promptBtEnable)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        Log.d(TAG, "onNewIntent: Received new intent")
        promptBtEnable.value = intent.getBooleanExtra(EXTRA_PROMPT_BT_ENABLE, false)
    }

    companion object {
        const val EXTRA_PROMPT_BT_ENABLE = "leontec.jp.synclogs.extra.PROMPT_BT_ENABLE"
    }
}

@Composable
private fun MenuScreen(promptBtEnable: State<Boolean>) {
    val context = LocalContext.current

    val openLogSend = { promptEnable: Boolean ->
        Log.d("MAIN_ACTIVITY_DEBUG", "Mở LogSendActivity (promptBtEnable=$promptEnable)")
        context.startActivity(
            Intent(context, LogSendActivity::class.java)
                .putExtra(MainActivity.EXTRA_PROMPT_BT_ENABLE, promptEnable)
        )
    }

    // Được mở từ thông báo geofence → vào thẳng màn hình gửi để xử lý bật Bluetooth.
    LaunchedEffect(promptBtEnable.value) {
        if (promptBtEnable.value) openLogSend(true)
    }

    Scaffold { innerPadding ->
        Column(
            modifier = Modifier.fillMaxSize().padding(innerPadding).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text(stringResource(R.string.screen_title), style = MaterialTheme.typography.headlineSmall)
            MenuCardButton(
                title = stringResource(R.string.menu_log_send_title),
                subtitle = stringResource(R.string.menu_log_send_subtitle),
                onClick = { openLogSend(false) }
            )
        }
    }
}

/** Nút dạng thẻ menu: ô icon màu bên trái + tiêu đề + mô tả (giống hình 1). */
@Composable
private fun MenuCardButton(title: String, subtitle: String, onClick: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth().clickable { onClick() },
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Surface(
                color = Color(0xFF3F51B5),
                shape = RoundedCornerShape(8.dp),
                modifier = Modifier.size(44.dp).clip(RoundedCornerShape(8.dp))
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Text("≣", color = Color.White, style = MaterialTheme.typography.titleLarge)
                }
            }
            Column {
                Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = Color(0xFF616161))
            }
        }
    }
}
