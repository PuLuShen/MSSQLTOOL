using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Per-column statistics shown by the grid analytics window.
    ///
    /// Everything is a property on purpose: WPF binds to properties only, and while these were
    /// fields the statistics table rendered blank apart from the one property it had.  Keep it that
    /// way even though the values are written once by the analyzer.
    /// </summary>
    internal sealed class GridColumnInsight
    {
        public int Ordinal { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public int NonNull { get; set; }
        public int NullCount { get; set; }
        public int Distinct { get; set; }
        public int MaxLength { get; set; }
        public string Minimum { get; set; } = string.Empty;
        public string Maximum { get; set; } = string.Empty;
        public string Average { get; set; } = string.Empty;
        public string Sum { get; set; } = string.Empty;
        public string MostCommon { get; set; } = string.Empty;
        public int MostCommonCount { get; set; }
        public List<KeyValuePair<string, int>> TopValues { get; set; } = new List<KeyValuePair<string, int>>();
        public bool IsNumeric { get; set; }
        public bool IsTemporal { get; set; }

        public double NullPercent => NonNull + NullCount == 0 ? 0 : Math.Round(NullCount * 100.0 / (NonNull + NullCount), 1);

        /// <summary>Null share with the percent sign, ready for the grid.</summary>
        public string NullPercentText => NullPercent.ToString("0.#", CultureInfo.CurrentCulture) + "%";

        /// <summary>"value (12)" so the frequency is visible without opening the top-value list.</summary>
        public string MostCommonText => MostCommonCount > 0
            ? MostCommon + " (" + MostCommonCount.ToString(CultureInfo.CurrentCulture) + ")"
            : string.Empty;
    }

    /// <summary>Whole-grid statistics plus the sampling note that qualifies them.</summary>
    internal sealed class GridAnalyticsResult
    {
        public List<GridColumnInsight> Columns { get; set; } = new List<GridColumnInsight>();
        public int RowCount { get; set; }
        public int SampledRows { get; set; }
        public int ColumnCount { get; set; }
        public int DuplicateRows { get; set; }
        public int NullCells { get; set; }
        public int TotalCells { get; set; }
        public bool Truncated { get; set; }

        public double NullCellPercent => TotalCells == 0 ? 0 : Math.Round(NullCells * 100.0 / TotalCells, 1);
    }

    /// <summary>
    /// Computes the statistics behind the results analytics window in one pass over the sampled
    /// rows.  The loop is type aware: numeric and date columns never pay for text parsing, text
    /// columns never pay for decimal conversion, and values are keyed by object so the very common
    /// string columns do not allocate a substring per cell.
    /// </summary>
    internal static class GridAnalytics
    {
        /// <summary>
        /// Rows sampled for statistics.  Wide result sets are dominated by the per-cell work
        /// (120 columns x N rows), so the cap is what keeps the panel opening instantly; the status
        /// line always says when the numbers are a sample.
        /// </summary>
        public const int DefaultRowCap = 20000;

        public static GridAnalyticsResult Analyze(DataTable table, int rowCap = DefaultRowCap)
            => Analyze(table, rowCap, CancellationToken.None);

        public static GridAnalyticsResult Analyze(DataTable table, int rowCap, CancellationToken cancellationToken)
        {
            var result = new GridAnalyticsResult();
            if (table == null) return result;

            int columnCount = table.Columns.Count;
            result.ColumnCount = columnCount;
            result.RowCount = table.Rows.Count;

            int cap = Math.Max(1, rowCap);
            int rows = Math.Min(table.Rows.Count, cap);
            result.SampledRows = rows;
            result.Truncated = table.Rows.Count > rows;

            var insights = new GridColumnInsight[columnCount];
            var uniques = new HashSet<object>[columnCount];
            var frequencies = new Dictionary<object, int>[columnCount];
            var numericTotals = new decimal[columnCount];
            var numericCounts = new int[columnCount];
            var numericMins = new decimal?[columnCount];
            var numericMaxs = new decimal?[columnCount];
            var temporalMins = new DateTime?[columnCount];
            var temporalMaxs = new DateTime?[columnCount];
            var kinds = new ColumnKind[columnCount];
            var rowSignatures = new HashSet<string>(StringComparer.Ordinal);

            for (int index = 0; index < columnCount; index++)
            {
                DataColumn column = table.Columns[index];
                insights[index] = new GridColumnInsight
                {
                    Ordinal = index + 1,
                    Name = DisplayName(column),
                    DataType = FriendlyTypeName(column)
                };
                kinds[index] = KindOf(column.DataType);
                insights[index].IsNumeric = kinds[index] == ColumnKind.Number;
                insights[index].IsTemporal = kinds[index] == ColumnKind.Temporal;
                uniques[index] = new HashSet<object>();
                frequencies[index] = new Dictionary<object, int>();
            }

            var signature = new StringBuilder(Math.Max(64, columnCount * 8));

            for (int rowIndex = 0; rowIndex < rows; rowIndex++)
            {
                if ((rowIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();

                DataRow row = table.Rows[rowIndex];
                signature.Length = 0;

                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    object value = row[columnIndex];
                    bool isNull = value == null || value == DBNull.Value;
                    GridColumnInsight insight = insights[columnIndex];

                    result.TotalCells++;
                    if (isNull)
                    {
                        insight.NullCount++;
                        result.NullCells++;
                        signature.Append('\u0001').Append('\u0002');
                        continue;
                    }

                    insight.NonNull++;
                    signature.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\u0002');

                    ColumnKind kind = kinds[columnIndex];

                    if (kind == ColumnKind.Text)
                    {
                        string text = value as string ?? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                        if (text.Length > insight.MaxLength) insight.MaxLength = text.Length;
                        if (insight.NonNull == 1 || string.CompareOrdinal(text, insight.Minimum) < 0) insight.Minimum = text;
                        if (insight.NonNull == 1 || string.CompareOrdinal(text, insight.Maximum) > 0) insight.Maximum = text;
                    }
                    else if (kind == ColumnKind.Number)
                    {
                        decimal number = 0;
                        bool parsed = false;
                        try
                        {
                            number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                            parsed = true;
                        }
                        catch (Exception)
                        {
                            // NaN/Infinity and oversized doubles simply do not contribute to sum/avg.
                        }

                        if (parsed)
                        {
                            numericTotals[columnIndex] += number;
                            numericCounts[columnIndex]++;
                            if (!numericMins[columnIndex].HasValue || number < numericMins[columnIndex].Value) numericMins[columnIndex] = number;
                            if (!numericMaxs[columnIndex].HasValue || number > numericMaxs[columnIndex].Value) numericMaxs[columnIndex] = number;
                        }
                    }
                    else if (kind == ColumnKind.Temporal)
                    {
                        DateTime moment;
                        if (value is DateTime time) moment = time;
                        else DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out moment);

                        if (moment != default(DateTime))
                        {
                            if (!temporalMins[columnIndex].HasValue || moment < temporalMins[columnIndex].Value) temporalMins[columnIndex] = moment;
                            if (!temporalMaxs[columnIndex].HasValue || moment > temporalMaxs[columnIndex].Value) temporalMaxs[columnIndex] = moment;
                        }
                    }

                    // Distinct and the most-common value share one set of keys: the boxed value for
                    // value types, the string itself for text, which keeps this allocation-light.
                    object key = kind == ColumnKind.Binary ? BinaryKey((byte[])value) : (object)value;
                    if (key != null)
                    {
                        if (uniques[columnIndex].Count < 50000) uniques[columnIndex].Add(key);

                        Dictionary<object, int> frequency = frequencies[columnIndex];
                        if (frequency.Count < 20000 || frequency.ContainsKey(key))
                        {
                            frequency.TryGetValue(key, out int count);
                            frequency[key] = count + 1;
                        }
                    }
                }

                rowSignatures.Add(signature.ToString());
            }

            result.DuplicateRows = Math.Max(0, rows - rowSignatures.Count);

            for (int index = 0; index < columnCount; index++)
            {
                GridColumnInsight insight = insights[index];
                insight.Distinct = uniques[index].Count;

                Dictionary<object, int> frequency = frequencies[index];
                if (frequency.Count > 0)
                {
                    List<KeyValuePair<object, int>> top = frequency
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => Convert.ToString(pair.Key, CultureInfo.CurrentCulture), StringComparer.Ordinal)
                        .Take(10)
                        .ToList();

                    insight.TopValues = top
                        .Select(pair => new KeyValuePair<string, int>(
                            Shorten(Convert.ToString(pair.Key, CultureInfo.CurrentCulture), 120), pair.Value))
                        .ToList();

                    insight.MostCommon = Shorten(Convert.ToString(top[0].Key, CultureInfo.CurrentCulture), 60);
                    insight.MostCommonCount = top[0].Value;
                }

                if (numericCounts[index] > 0)
                {
                    insight.Sum = numericTotals[index].ToString("0.####", CultureInfo.CurrentCulture);
                    insight.Average = (numericTotals[index] / numericCounts[index]).ToString("0.####", CultureInfo.CurrentCulture);
                    insight.Minimum = numericMins[index].Value.ToString("0.####", CultureInfo.CurrentCulture);
                    insight.Maximum = numericMaxs[index].Value.ToString("0.####", CultureInfo.CurrentCulture);
                }
                else if (temporalMins[index].HasValue)
                {
                    insight.Minimum = temporalMins[index].Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                    insight.Maximum = temporalMaxs[index].Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                }
                else
                {
                    insight.Minimum = Shorten(insight.Minimum, 80);
                    insight.Maximum = Shorten(insight.Maximum, 80);
                }

                // A text column that happens to hold digits is still text: do not pretend its sum is
                // meaningful, and do not show a "99" minimum for a varchar id such as DjGuid.
                if (kinds[index] != ColumnKind.Number)
                {
                    insight.Sum = string.Empty;
                    insight.Average = string.Empty;
                }
            }

            result.Columns = insights.ToList();
            return result;
        }

        private enum ColumnKind
        {
            Text,
            Number,
            Temporal,
            Binary,
            Other
        }

        private static ColumnKind KindOf(Type type)
        {
            if (type == null) return ColumnKind.Other;
            if (type == typeof(string) || type == typeof(char)) return ColumnKind.Text;
            if (IsNumericType(type)) return ColumnKind.Number;
            if (IsTemporalType(type)) return ColumnKind.Temporal;
            if (type == typeof(byte[])) return ColumnKind.Binary;
            return ColumnKind.Other;
        }

        private static object BinaryKey(byte[] value)
        {
            // Distinct/frequency for a binary column cannot use reference equality, and base64 of a
            // large image would be wasteful; length plus hash keeps the counts meaningful.
            if (value == null) return null;
            return "bin:" + value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value.GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Display name of a result column.  The grid stores the real field name in the column
        /// caption (and, for older code, in ExtendedProperties["columnName"]); only when neither is
        /// available does the synthetic Column_N name surface.
        /// </summary>
        internal static string DisplayName(DataColumn column)
        {
            if (column == null) return string.Empty;

            if (!string.IsNullOrWhiteSpace(column.Caption) &&
                !string.Equals(column.Caption, column.ColumnName, StringComparison.Ordinal))
            {
                return column.Caption;
            }

            if (column.ExtendedProperties.Contains("columnName"))
            {
                object name = column.ExtendedProperties["columnName"];
                string text = name == null ? null : Convert.ToString(name, CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return column.ColumnName;
        }

        /// <summary>
        /// Index of the next column whose caption, field name or SQL type contains the needle,
        /// starting at <paramref name="startIndex"/> and wrapping at the end of the grid.  Returns -1
        /// when nothing matches, so the caller can tell "no match" from "the first column".
        /// </summary>
        internal static int FindColumn(DataColumnCollection columns, string needle, int startIndex)
        {
            if (columns == null || columns.Count == 0) return -1;

            string text = (needle ?? string.Empty).Trim();
            if (text.Length == 0) return -1;

            int count = columns.Count;
            int start = ((startIndex % count) + count) % count;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (start + offset) % count;
                DataColumn column = columns[index];
                if (column == null) continue;

                if (DisplayName(column).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    column.ColumnName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    FriendlyTypeName(column).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return index;
                }
            }

            return -1;
        }

        internal static string FriendlyTypeName(DataColumn column)
        {
            if (column == null) return string.Empty;
            if (column.ExtendedProperties.Contains("sqlType"))
            {
                object sqlType = column.ExtendedProperties["sqlType"];
                string text = sqlType == null ? null : Convert.ToString(sqlType, CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return FriendlyType(column.DataType);
        }

        private static string FriendlyType(Type type)
        {
            if (type == null) return string.Empty;
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "number";
            if (type == typeof(DateTime)) return "datetime";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(byte[])) return "binary";
            if (type == typeof(Guid)) return "guid";
            return type.Name;
        }

        private static bool IsNumericType(Type type)
        {
            if (type == null) return false;
            return type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                   type == typeof(decimal) || type == typeof(double) || type == typeof(float) ||
                   type == typeof(byte) || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong);
        }

        private static bool IsTemporalType(Type type)
            => type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan);

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        /// <summary>Tab separated summary, ready for Excel or a review comment.</summary>
        public static string ToText(GridAnalyticsResult result, bool localizedHeaders = true)
        {
            if (result == null) return string.Empty;
            var builder = new StringBuilder();
            builder.AppendLine(string.Join("\t", new[]
            {
                localizedHeaders ? LocalizationManager.T("Column") : "Column",
                localizedHeaders ? LocalizationManager.T("Type") : "Type",
                localizedHeaders ? LocalizationManager.T("Non-null") : "Non-null",
                localizedHeaders ? LocalizationManager.T("Nulls") : "Nulls",
                localizedHeaders ? LocalizationManager.T("Distinct") : "Distinct",
                localizedHeaders ? LocalizationManager.T("Min") : "Min",
                localizedHeaders ? LocalizationManager.T("Max") : "Max",
                localizedHeaders ? LocalizationManager.T("Average") : "Average",
                localizedHeaders ? LocalizationManager.T("Sum") : "Sum",
                localizedHeaders ? LocalizationManager.T("Most common") : "Most common"
            }));

            foreach (GridColumnInsight column in result.Columns)
            {
                builder.AppendLine(string.Join("\t", new[]
                {
                    column.Name, column.DataType,
                    column.NonNull.ToString(CultureInfo.CurrentCulture),
                    column.NullCount.ToString(CultureInfo.CurrentCulture),
                    column.Distinct.ToString(CultureInfo.CurrentCulture),
                    column.Minimum, column.Maximum, column.Average, column.Sum,
                    column.MostCommonCount > 0 ? column.MostCommon + " (" + column.MostCommonCount + ")" : string.Empty
                }));
            }

            return builder.ToString();
        }
    }
}
