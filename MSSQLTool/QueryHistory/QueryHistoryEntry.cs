using System;

namespace MSSQLTool
{
    /// <summary>
    /// One finished query execution as handed to the history store.  It is the transport type
    /// between the editor's execution tracking and the SQLite (or JSONL) persistence layer.
    /// </summary>
    public class QueryHistoryEntry
    {
        public DateTime StartTime;
        public DateTime FinishTime;
        public string ElapsedTime;
        public long TotalRowsReturned;
        public string ExecResult;
        public string QueryText;
        public string DataSource;
        public string DatabaseName;
        public string LoginName;
        public string WorkstationId;
        public int RetryCount;
        public Guid ClientExecutionId = Guid.NewGuid();
    }
}
