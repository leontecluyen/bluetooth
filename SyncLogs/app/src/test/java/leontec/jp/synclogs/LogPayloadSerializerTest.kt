package leontec.jp.synclogs

import leontec.jp.synclogs.data.JobLog
import leontec.jp.synclogs.sync.LogPayloadSerializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LogPayloadSerializerTest {

    private fun log(id: String, barcode: String) = JobLog(
        id = id,
        workerId = "W-1",
        jobType = JobLog.JobType.KENPIN,
        barcodeData = barcode,
        startTime = 1000L,
        endTime = 2000L
    )

    @Test
    fun csv_hasHeaderAndOneRowPerLog() {
        val csv = LogPayloadSerializer.toCsv(listOf(log("a", "BC1"), log("b", "BC2")))
        val lines = csv.trim().split("\r\n")
        assertEquals(3, lines.size) // header + 2 rows
        assertEquals("id,workerId,jobType,barcodeData,startTime,endTime", lines[0])
    }

    @Test
    fun csv_quotesAndEscapesFieldsWithCommasAndQuotes() {
        // A barcode containing a comma and a quote must not break the column layout.
        val csv = LogPayloadSerializer.toCsv(listOf(log("a", "BC,\"X\"")))
        val row = csv.trim().split("\r\n")[1]
        assertTrue(row.contains("\"BC,\"\"X\"\"\""))
        // The row must still have exactly 6 logical columns despite the embedded comma.
        // (id, workerId, jobType, barcodeData, startTime, endTime)
        assertTrue(row.startsWith("a,W-1,"))
        assertTrue(row.endsWith(",1000,2000"))
    }

    @Test
    fun csv_emptyListStillEmitsHeader() {
        val csv = LogPayloadSerializer.toCsv(emptyList())
        assertEquals("id,workerId,jobType,barcodeData,startTime,endTime", csv.trim())
    }
}
