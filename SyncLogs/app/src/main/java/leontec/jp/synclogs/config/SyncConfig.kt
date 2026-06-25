package leontec.jp.synclogs.config

import android.content.Context
import androidx.core.content.edit

/**
 * Single source of truth for runtime-configurable deployment settings.
 *
 * Defaults are sensible for the office LAN but every field is overridable at
 * runtime (e.g. from a settings screen or MDM-pushed config) and persisted in
 * SharedPreferences so the worker, Bluetooth and geofence layers all agree.
 */
class SyncConfig private constructor(context: Context) {

    private val prefs = context.applicationContext
        .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** Bluetooth-bonded name of the PC running the SPP receiver (empty until first chosen). */
    var pcBluetoothName: String
        get() = prefs.getString(KEY_PC_BT_NAME, DEFAULT_PC_BT_NAME) ?: DEFAULT_PC_BT_NAME
        set(value) = prefs.edit { putString(KEY_PC_BT_NAME, value) }

    /**
     * Bluetooth MAC of the last PC we connected & synced to successfully. Preferred over the
     * name when reconnecting because it's exact; remembered so auto-sync targets it directly.
     */
    var pcBluetoothAddress: String
        get() = prefs.getString(KEY_PC_BT_ADDR, "") ?: ""
        set(value) = prefs.edit { putString(KEY_PC_BT_ADDR, value) }

    /** True once a PC has been chosen/confirmed as the sync target. */
    val hasPcTarget: Boolean
        get() = pcBluetoothName.isNotBlank() || pcBluetoothAddress.isNotBlank()

    /** Whether the office geofence auto-sync is currently enabled (drives the UI switch). */
    var geofenceEnabled: Boolean
        get() = prefs.getBoolean(KEY_GEOFENCE_ON, false)
        set(value) = prefs.edit { putBoolean(KEY_GEOFENCE_ON, value) }

    /**
     * UI language override: "system" (default — follow the device locale, English fallback),
     * or an explicit language code "en" / "vi" / "ja". Applied in MainActivity.attachBaseContext
     * via [leontec.jp.synclogs.LocaleHelper]; changing it recreates the activity.
     */
    var appLanguage: String
        get() = prefs.getString(KEY_APP_LANG, DEFAULT_APP_LANG) ?: DEFAULT_APP_LANG
        set(value) = prefs.edit { putString(KEY_APP_LANG, value) }

    /**
     * Bộ đếm "đã gửi CSV type này lần mấy", theo từng type. Tăng & trả về số kế tiếp; nhúng
     * vào tên file gửi lên PC để PC biết bản nào mới hơn (supersede bản cũ cùng term+type).
     */
    fun nextUploadIndex(type: String): Int {
        val key = "upload_idx_$type"
        val next = prefs.getInt(key, 0) + 1
        prefs.edit { putInt(key, next) }
        return next
    }

    /**
     * Reset MỌI bộ đếm upload index về 0 (mẫu kế tiếp lại bắt đầu từ #1). Dùng khi "Reset tất cả"
     * để test lại từ đầu cho khớp với việc xoá DB ở PC.
     */
    fun resetUploadIndexes() {
        val keys = prefs.all.keys.filter { it.startsWith("upload_idx_") }
        prefs.edit { keys.forEach { remove(it) } }
    }

    /** Clears the saved PC target (used by "Reset all"). */
    fun clearPcTarget() {
        prefs.edit {
            remove(KEY_PC_BT_NAME)
            remove(KEY_PC_BT_ADDR)
        }
    }

    // --- Wi-Fi fallback (parked for now; kept for when the REST layer is re-enabled) ---
    /** LAN IP of the PC running the REST receiver (Wi-Fi fallback). */
    var pcIpAddress: String
        get() = prefs.getString(KEY_PC_IP, DEFAULT_PC_IP) ?: DEFAULT_PC_IP
        set(value) = prefs.edit { putString(KEY_PC_IP, value) }

    var pcPort: Int
        get() = prefs.getInt(KEY_PC_PORT, DEFAULT_PC_PORT)
        set(value) = prefs.edit { putInt(KEY_PC_PORT, value) }

    /** Base URL the Retrofit client targets, derived from IP + port. */
    val restBaseUrl: String
        get() = "http://$pcIpAddress:$pcPort/"

    var geofenceLatitude: Double
        get() = Double.fromBits(prefs.getLong(KEY_GEO_LAT, DEFAULT_GEO_LAT.toRawBits()))
        set(value) = prefs.edit { putLong(KEY_GEO_LAT, value.toRawBits()) }

    var geofenceLongitude: Double
        get() = Double.fromBits(prefs.getLong(KEY_GEO_LNG, DEFAULT_GEO_LNG.toRawBits()))
        set(value) = prefs.edit { putLong(KEY_GEO_LNG, value.toRawBits()) }

    var geofenceRadiusMeters: Float
        get() = prefs.getFloat(KEY_GEO_RADIUS, DEFAULT_GEO_RADIUS)
        set(value) = prefs.edit { putFloat(KEY_GEO_RADIUS, value) }

    companion object {
        private const val PREFS_NAME = "synclogs_config"

        private const val KEY_PC_BT_NAME = "pc_bt_name"
        private const val KEY_PC_BT_ADDR = "pc_bt_addr"
        private const val KEY_GEOFENCE_ON = "geofence_on"
        private const val KEY_APP_LANG = "app_lang"
        /** Follow the device locale (English fallback) until the user picks a language. */
        const val DEFAULT_APP_LANG = "system"
        private const val KEY_PC_IP = "pc_ip"
        private const val KEY_PC_PORT = "pc_port"
        private const val KEY_GEO_LAT = "geo_lat"
        private const val KEY_GEO_LNG = "geo_lng"
        private const val KEY_GEO_RADIUS = "geo_radius"

        // --- Defaults (override per deployment) ---
        // Empty: the user picks the PC once; we then remember it (name + address).
        const val DEFAULT_PC_BT_NAME = ""
        const val DEFAULT_PC_IP = "192.168.0.100"
        const val DEFAULT_PC_PORT = 8080

        // Office coordinates (placeholder – set to the real warehouse location).
        const val DEFAULT_GEO_LAT = 35.681236   // Tokyo Station, example
        const val DEFAULT_GEO_LNG = 139.767125
        const val DEFAULT_GEO_RADIUS = 50f       // 50 m radius per spec

        const val GEOFENCE_REQUEST_ID = "office_geofence"

        @Volatile
        private var INSTANCE: SyncConfig? = null

        fun get(context: Context): SyncConfig =
            INSTANCE ?: synchronized(this) {
                INSTANCE ?: SyncConfig(context).also { INSTANCE = it }
            }
    }
}
