using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MSSQLTool
{
    public class SnippetVariableResult
    {
        public string ProcessedText { get; set; }
        public int CursorOffset { get; set; }
        public List<SnippetTabStop> TabStops { get; set; } = new List<SnippetTabStop>();

        public SnippetVariableResult(string processedText, int cursorOffset)
        {
            ProcessedText = processedText;
            CursorOffset = cursorOffset;
        }
    }

    public class SnippetTabStop
    {
        public int Index { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    public static class SnippetVariableProcessor
    {
        public static SnippetVariableResult ProcessVariables(string body, string cursorMarker)
        {
            if (string.IsNullOrEmpty(body))
                return new SnippetVariableResult(string.Empty, -1);

            string text = body;

            // Replace built-in variables
            text = text.Replace("$DATE$", DateTime.Now.ToString("yyyy-MM-dd"));
            text = text.Replace("$TIME$", DateTime.Now.ToString("HH:mm:ss"));
            text = text.Replace("$USER$", Environment.UserName);

            // SQL Prompt/VS Code-style numbered placeholders: ${1:name}, ${2}, ${0}.
            // Repeated indexes reuse the first default value.
            var defaults = new Dictionary<int, string>();
            foreach (Match match in Regex.Matches(text, @"\$\{(?<index>\d+)(?::(?<value>[^}]*))?\}"))
            {
                int index = int.Parse(match.Groups["index"].Value);
                if (!defaults.ContainsKey(index)) defaults[index] = match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty;
            }
            var output = new StringBuilder();
            var tabStops = new List<SnippetTabStop>();
            int source = 0;
            foreach (Match match in Regex.Matches(text, @"\$\{(?<index>\d+)(?::(?<value>[^}]*))?\}"))
            {
                output.Append(text, source, match.Index - source);
                int index = int.Parse(match.Groups["index"].Value);
                string value = defaults[index];
                int offset = output.Length;
                output.Append(value);
                if (!tabStops.Any(t => t.Index == index)) tabStops.Add(new SnippetTabStop { Index = index, Offset = offset, Length = value.Length });
                source = match.Index + match.Length;
            }
            if (source > 0)
            {
                output.Append(text, source, text.Length - source);
                text = output.ToString();
            }

            // Find and remove cursor marker
            int cursorOffset = -1;
            if (!string.IsNullOrEmpty(cursorMarker))
            {
                int markerIndex = text.IndexOf(cursorMarker);
                if (markerIndex >= 0)
                {
                    cursorOffset = markerIndex;
                    text = text.Remove(markerIndex, cursorMarker.Length);
                }
            }

            var result = new SnippetVariableResult(text, cursorOffset);
            result.TabStops = tabStops.OrderBy(t => t.Index == 0 ? int.MaxValue : t.Index).ToList();
            if (result.TabStops.Count > 0) result.CursorOffset = result.TabStops[0].Offset;
            return result;
        }
    }
}
