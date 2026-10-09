using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MSSQLTool
{
    internal sealed class FeatureDiagnostic
    {
        public DateTime TimestampUtc { get; set; }
        public string Feature { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
    }

    internal static class FeatureDiagnostics
    {
        private const int Capacity = 100;
        private static readonly object SyncRoot = new object();
        private static readonly Queue<FeatureDiagnostic> Items = new Queue<FeatureDiagnostic>();

        public static void Report(string feature, string message, Exception exception = null)
        {
            var item = new FeatureDiagnostic
            {
                TimestampUtc = DateTime.UtcNow,
                Feature = feature ?? "General",
                Message = message ?? string.Empty,
                Exception = exception
            };
            lock (SyncRoot)
            {
                Items.Enqueue(item);
                while (Items.Count > Capacity) Items.Dequeue();
            }
            Trace.WriteLine($"MSSQL Tool [{item.Feature}] {item.Message}{(exception == null ? string.Empty : ": " + exception)}");
        }

        public static FeatureDiagnostic[] Snapshot()
        {
            lock (SyncRoot) return Items.ToArray();
        }
    }
}
