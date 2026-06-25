package leontec.jp.synclogs.data

import androidx.room.*
import kotlinx.coroutines.flow.Flow

/** One CSV batch summarised for the app's list (name, #rows, #synced, created-at). */
data class BatchSummary(
    val batchId: String,
    val batchName: String,
    val rowCount: Int,
    val syncedCount: Int,
    val createdAt: Long
)

@Dao
interface JobLogDao {
    @Query("SELECT * FROM job_logs ORDER BY startTime DESC")
    fun observeAllLogs(): Flow<List<JobLog>>

    /** CSV batches newest-first, with how many of their rows are already synced. */
    @Query(
        """
        SELECT batchId,
               batchName,
               COUNT(*) AS rowCount,
               SUM(CASE WHEN syncStatus = 'SUCCESS' THEN 1 ELSE 0 END) AS syncedCount,
               MIN(startTime) AS createdAt
        FROM job_logs
        WHERE batchId <> ''
        GROUP BY batchId, batchName
        ORDER BY createdAt DESC
        """
    )
    fun observeBatches(): Flow<List<BatchSummary>>

    @Query("SELECT COUNT(*) FROM job_logs WHERE syncStatus = 'PENDING'")
    fun observePendingCount(): Flow<Int>

    @Query("SELECT * FROM job_logs WHERE syncStatus = 'PENDING'")
    suspend fun getUnsyncedLogs(): List<JobLog>

    /** A few existing CSV logIds — used to craft cross-file duplicates for dedup testing. */
    @Query("SELECT DISTINCT logId FROM job_logs WHERE logId <> '' ORDER BY startTime DESC LIMIT :limit")
    suspend fun recentLogIds(limit: Int): List<String>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insert(log: JobLog)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertAll(logs: List<JobLog>)

    /** Wipes all logs (used by the "Reset all" action). */
    @Query("DELETE FROM job_logs")
    suspend fun deleteAll()

    @Update
    suspend fun updateLog(log: JobLog)

    @Query("UPDATE job_logs SET syncStatus = :status, syncMethod = :method WHERE id = :id")
    suspend fun markAsSynced(id: String, status: SyncStatus, method: SyncMethod)

    @Query("DELETE FROM job_logs WHERE syncStatus = 'SUCCESS' AND startTime < :threshold")
    suspend fun deleteSyncedOlderThan(threshold: Long): Int
}
