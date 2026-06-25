package leontec.jp.synclogs.network

import leontec.jp.synclogs.data.JobLog
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.POST

/**
 * Wi-Fi fallback transport (CLAUDE.md §3). Posts the pending logs as a JSON
 * array to `http://<PC_IP>:<port>/api/sync`.
 */
interface SyncApiService {

    @POST("api/sync")
    suspend fun syncLogs(@Body logs: List<JobLog>): Response<Unit>
}
