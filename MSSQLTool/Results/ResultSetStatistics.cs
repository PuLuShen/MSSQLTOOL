using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MSSQLTool.Results
{
    /// <summary>One column of a result set, summarised.</summary>
    internal sealed class ResultColumnStatistics
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public int NonNullCount { get; set; }
        public int NullCount { get; set; }
        public int DistinctCount { get; set; }
        public string Minimum { get; set; }
        public string Maximum { get; set; }
        public string Average { get; set; }
        public string Sum { get; set; }
        public int MaximumLength { get; set; }
        public int EmptyCount { get; set; }
    }

    /// <summary>
    /// Statistics of one result set.  Everything is computed from the <see cref="DataTable"/> the results
    /// grid already exposes, so it works for any query without talking to the server again.
    /// </summary>
    internal static class ResultSetStatistics
    {
        private const int DistinctLimit = 100000;

        /// <summary>Summarises the current page of a result set.</summary>
        public static List<ResultColumnStatistics> Summarize(DataTable table)
        {
            var result = new List<ResultColumnStatistics>();
            if (table == null) return result;

            foreach (DataColumn column in table.Columns)
            {
                var stats = new ResultColumnStatistics
                {
                    Name = column.ColumnName,
                    DataType = column.DataType == null ? string.Empty : column.DataType.Name
                };

                bool numeric = IsNumeric(column.DataType);
                bool ordered = numeric || column.DataType == typeof(DateTime) || column.DataType == typeof(DateTimeOffset)
                    || column.DataType == typeof(TimeSpan) || column.DataType == typeof(Guid);

                var distinct = new HashSet<string>(StringComparer.Ordinal);
                object minimum = null, maximum = null;
                decimal sum = 0m;
                int numericCount = 0;

                foreach (DataRow row in table.Rows)
                {
                    object value = row[column];
                    bool isNull = value == null || value == DBNull.Value;
                    if (isNull)
                    {
                        stats.NullCount++;
                        continue;
                    }

                    stats.NonNullCount++;
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (text.Length == 0) stats.EmptyCount++;
                    if (text.Length > stats.MaximumLength) stats.MaximumLength = text.Length;
                    if (distinct.Count < DistinctLimit) distinct.Add(text);

                    if (numeric && TryToDecimal(value, out decimal number))
                    {
                        sum += number;
                        numericCount++;
                    }

                    if (ordered)
                    {
                        int comparisonMin = minimum == null ? -1 : Compare(value, minimum);
                        if (comparisonMin < 0) minimum = value;
                        int comparisonMax = maximum == null ? 1 : Compare(value, maximum);
                        if (comparisonMax > 0) maximum = value;
                    }
                }

                stats.DistinctCount = distinct.Count;
                stats.Minimum = minimum == null ? string.Empty : Convert.ToString(minimum, CultureInfo.InvariantCulture);
                stats.Maximum = maximum == null ? string.Empty : Convert.ToString(maximum, CultureInfo.InvariantCulture);
                if (numeric && numericCount > 0)
                {
                    stats.Sum = sum.ToString(CultureInfo.InvariantCulture);
                    stats.Average = (sum / numericCount).ToString(CultureInfo.InvariantCulture);
                }

                result.Add(stats);
            }

            return result;
        }

        /// <summary>Plain-text summary, used by "copy" and by the log.</summary>
        public static string ToText(DataTable table, IEnumerable<ResultColumnStatistics> statistics)
        {
            if (table == null) return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine(LocalizationManager.Format("Rows: {0}   Columns: {1}", table.Rows.Count, table.Columns.Count));
            builder.AppendLine();
            builder.AppendLine(LocalizationManager.T("Column\tType\tNon-null\tNull\tDistinct\tMin\tMax\tAvg\tMax length"));

            foreach (ResultColumnStatistics stats in statistics ?? Enumerable.Empty<ResultColumnStatistics>())
            {
                builder.AppendLine(string.Join("\t", new[]
                {
                    stats.Name, stats.DataType,
                    stats.NonNullCount.ToString(CultureInfo.InvariantCulture),
                    stats.NullCount.ToString(CultureInfo.InvariantCulture),
                    stats.DistinctCount.ToString(CultureInfo.InvariantCulture),
                    stats.Minimum, stats.Maximum, stats.Average,
                    stats.MaximumLength.ToString(CultureInfo.InvariantCulture)
                }));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Finds the next cell whose text contains <paramref name="needle"/>, starting after
        /// (<paramref name="row"/>, <paramref name="column"/>).  Returns null when nothing matches.
        /// </summary>
        public static ResultCellMatch Find(DataTable table, string needle, int row, int column)
        {
            if (table == null || string.IsNullOrEmpty(needle)) return null;

            int rowCount = table.Rows.Count, columnCount = table.Columns.Count;
            if (rowCount == 0 || columnCount == 0) return null;

            int startRow = Math.Max(0, row), startColumn = Math.Max(0, column);
            int total = rowCount * columnCount;

            for (int step = 1; step <= total; step++)
            {
                int flattened = ((startRow * columnCount) + startColumn) + step;
                if (flattened >= total) return null;

                int currentRow = flattened / columnCount;
                int currentColumn = flattened % columnCount;
                string text = Convert.ToString(table.Rows[currentRow][currentColumn], CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return new ResultCellMatch { Row = currentRow, Column = currentColumn, ColumnName = table.Columns[currentColumn].ColumnName, Value = text };
            }

            return null;
        }

        /// <summary>Index of a column by name, or -1 when the result set has no such column.</summary>
        public static int FindColumn(DataTable table, string columnName)
        {
            if (table == null || string.IsNullOrWhiteSpace(columnName)) return -1;
            for (int i = 0; i < table.Columns.Count; i++)
                if (string.Equals(table.Columns[i].ColumnName, columnName.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static bool IsNumeric(Type type)
        {
            if (type == null) return false;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryToDecimal(object value, out decimal number)
        {
            try
            {
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                number = 0m;
                return false;
            }
        }

        private static int Compare(object left, object right)
        {
            if (left is IComparable comparable && left.GetType() == right.GetType()) return comparable.CompareTo(right);
            return string.Compare(Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A cell matched by <see cref="ResultSetStatistics.Find"/>.</summary>
    internal sealed class ResultCellMatch
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public string ColumnName { get; set; }
        public string Value { get; set; }
    }
}
