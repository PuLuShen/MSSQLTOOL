using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MSSQLTool
{
    internal static class DatabaseIdentifier
    {
        public static string SqlServer(string value) => QuoteQualified(value, '[', ']', "]]", 4);
        public static string SqlServerLocalObject(string value) => QuoteQualified(value, '[', ']', "]]", 2);
        public static string NormalizeSqlServer(string value)
        {
            try { return string.Join(".", Split(value, '[', ']')); }
            catch { return (value ?? string.Empty).Trim(); }
        }

        public static bool TrySplitSqlServer(string value, out List<string> parts)
        {
            try
            {
                parts = Split(value, '[', ']');
                return parts.Count > 0 && parts.Count <= 4;
            }
            catch
            {
                parts = new List<string>();
                return false;
            }
        }

        public static string UnquoteSqlServerPart(string value)
        {
            string part = (value ?? string.Empty).Trim();
            if (part.Length >= 2 && part[0] == '[' && part[part.Length - 1] == ']')
                return part.Substring(1, part.Length - 2).Replace("]]", "]");
            if (part.Length >= 2 && part[0] == '"' && part[part.Length - 1] == '"')
                return part.Substring(1, part.Length - 2).Replace("\"\"", "\"");
            return part;
        }

        public static string SqlServerPart(string value)
        {
            string part = UnquoteSqlServerPart(value);
            if (part.Length == 0) throw new InvalidOperationException("SQL Server identifier cannot be empty.");
            return "[" + part.Replace("]", "]]" ) + "]";
        }

        private static string QuoteQualified(string input, char open, char close, string escapedClose, int maximumParts)
        {
            List<string> parts = Split(input, open, close);
            if (parts.Count == 0 || parts.Count > maximumParts) throw new InvalidOperationException("Invalid database object name: " + input);
            return string.Join(".", parts.Select(part => part.Length == 0 ? string.Empty : open + part.Replace(close.ToString(), escapedClose) + close));
        }

        private static List<string> Split(string input, char open, char close)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, doubleQuoted = false;
            string value = (input ?? string.Empty).Trim();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (doubleQuoted)
                {
                    if (character == '"' && index + 1 < value.Length && value[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else if (character == '"') doubleQuoted = false;
                    else current.Append(character);
                    continue;
                }
                if (character == open && !quoted) { quoted = true; continue; }
                if (character == close && quoted)
                {
                    // SQL Server escapes a closing bracket as ]].
                    if (index + 1 < value.Length && value[index + 1] == close)
                    {
                        current.Append(close);
                        index++;
                        continue;
                    }
                    quoted = false;
                    continue;
                }
                if (character == '"' && !quoted) { doubleQuoted = true; continue; }
                if (character == '.' && !quoted) { Add(current, result); continue; }
                current.Append(character);
            }
            if (quoted || doubleQuoted) throw new InvalidOperationException("Unclosed quoted identifier: " + input);
            Add(current, result);
            if (result.Count == 0 || result[0].Length == 0 || result[result.Count - 1].Length == 0)
                throw new InvalidOperationException("Database object names cannot start or end with an empty part.");
            return result;
        }

        private static void Add(StringBuilder current, ICollection<string> result)
        {
            string value = current.ToString().Trim();
            current.Clear();
            result.Add(value);
        }
    }
}
