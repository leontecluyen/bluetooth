package leontec.jp.synclogs.data

import androidx.room.TypeConverter

/**
 * Room cannot persist Kotlin enums natively, so we store them as their `name`
 * string and rebuild them on read. Without these converters the project does
 * not compile ("Cannot figure out how to save this field into database").
 */
class Converters {

    @TypeConverter
    fun fromSyncStatus(status: SyncStatus): String = status.name

    @TypeConverter
    fun toSyncStatus(value: String): SyncStatus = SyncStatus.valueOf(value)

    @TypeConverter
    fun fromSyncMethod(method: SyncMethod?): String? = method?.name

    @TypeConverter
    fun toSyncMethod(value: String?): SyncMethod? = value?.let { SyncMethod.valueOf(it) }
}
