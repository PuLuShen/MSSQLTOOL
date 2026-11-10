using System;
using System.Collections.Generic;
using System.Globalization;

namespace MSSQLTool.Model
{
    /// <summary>One text replacement produced by a refactoring.</summary>
    internal sealed class SqlTextChange
    {
        public int Start;
        public int Length;
        public string Text;

        public SqlTextChange() { }

        public SqlTextChange(int start, int length, string text)
        {
            Start = start;
            Length = length;
            Text = text;
        }
    }

    /// <summary>The result of planning a rename, including the reason when it cannot run.</summary>
    internal sealed class SqlRenamePlan
    {
        public bool Success;
        public string Error = string.Empty;
        public string OldName = string.Empty;
        public string NewName = string.Empty;
        public bool WholeDocument;
        public List<SqlTextChange> Changes = new List<SqlTextChange>();

        public int Count => Changes == null ? 0 : Changes.Count;
    }

    /// <summary>
    /// Plans a script-scoped rename without touching the editor.  The plan is computed from the
    /// shared document model, so the command only has to apply the returned spans and the rules
    /// that decide *what* gets renamed stay unit-testable.
    ///
    /// Scope rules:
    ///   * current statement (default) — a UNION branch or a nearby CTE can reuse a name safely;
    ///   * whole document — explicit opt-in from the dialog.
    /// A name that already exists inside the scope is rejected instead of silently creating a
    /// second reference, which is what a half-renamed script looks like after a blind replace.
    /// </summary>
    internal static class SqlRenameService
    {
        public static SqlRenamePlan Build(string text, int caretOffset, string newName, bool wholeDocument)
        {
            var plan = new SqlRenamePlan { WholeDocument = wholeDocument };
            if (string.IsNullOrEmpty(text))
            {
                plan.Error = "There is no SQL text to rename.";
                return plan;
            }

            SqlDocumentModel model = SqlDocumentModel.Get(text);
            SqlToken token = SqlTokenizer.TokenAt(model.Tokens, caretOffset);
            if (token == null)
            {
                plan.Error = "Place the caret on a table, column, alias or variable name first.";
                return plan;
            }

            // A keyword can still be a legal object name when it is bracketed, so only reject the
            // token when it was written as a plain keyword and the user did not select anything.
            if (token.Kind != SqlTokenKind.Identifier)
            {
                plan.Error = "Place the caret on a table, column, alias or variable name first.";
                return plan;
            }

            string oldName = SqlTokenizer.Unquote(token);
            if (string.IsNullOrWhiteSpace(oldName) || IsStatementKeyword(oldName))
            {
                plan.Error = "Place the caret on a table, column, alias or variable name first.";
                return plan;
            }

            string cleaned = CleanName(newName);
            if (string.IsNullOrEmpty(cleaned))
            {
                plan.Error = "Enter a new name.";
                return plan;
            }

            if (cleaned.Length > 128)
            {
                plan.Error = "The new name is too long (128 characters maximum).";
                return plan;
            }

            if (string.Equals(cleaned, oldName, StringComparison.OrdinalIgnoreCase))
            {
                plan.Error = "The new name is the same as the current name.";
                return plan;
            }

            int scopeStart = 0;
            int scopeEnd = text.Length;
            if (!wholeDocument)
            {
                // Variables belong to the batch (DECLARE/SET and every use), not to one statement.
                SqlStatementRange statement = oldName.StartsWith("@", StringComparison.Ordinal)
                    ? model.Statements.InBatch(caretOffset)
                    : model.StatementAt(caretOffset);
                if (statement != null && statement.End > statement.Start)
                {
                    scopeStart = statement.Start;
                    scopeEnd = statement.End;
                }
            }

            // Collision: another identifier with the target name in the same scope would become
            // ambiguous after the rename.
            foreach (SqlToken candidate in model.Tokens)
            {
                if (candidate.Kind != SqlTokenKind.Identifier) continue;
                if (candidate.Start < scopeStart || candidate.Start >= scopeEnd) continue;

                string candidateName = SqlTokenizer.Unquote(candidate);
                if (string.Equals(candidateName, cleaned, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidateName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Error = "The name \"" + cleaned + "\" is already used in this scope.";
                    return plan;
                }
            }

            HashSet<int> execParameterNames = ExecParameterNamePositions(model, scopeStart, scopeEnd);
            var changedOffsets = new HashSet<int>();
            foreach (SqlToken candidate in model.Tokens)
            {
                if (candidate.Kind != SqlTokenKind.Identifier) continue;
                if (candidate.Start < scopeStart || candidate.Start >= scopeEnd) continue;

                string candidateName = SqlTokenizer.Unquote(candidate);
                if (!string.Equals(candidateName, oldName, StringComparison.OrdinalIgnoreCase)) continue;

                // EXEC dbo.Proc @CODE = @CODE: the first @CODE names a procedure parameter, not the
                // variable.  Renaming it would change which parameter is passed (or break the call).
                if (execParameterNames.Contains(candidate.Start)) continue;
                if (!changedOffsets.Add(candidate.Start)) continue;

                plan.Changes.Add(new SqlTextChange(candidate.Start, candidate.Length, QuoteLike(candidate.Text, cleaned)));
            }

            if (plan.Changes.Count == 0)
            {
                plan.Error = "No reference to \"" + oldName + "\" was found in this scope.";
                return plan;
            }

            plan.Success = true;
            plan.OldName = oldName;
            plan.NewName = cleaned;
            return plan;
        }

        /// <summary>
        /// Offsets of @name tokens that name an EXEC argument rather than reference a variable, for
        /// example the @CODE in <c>EXEC dbo.Proc @CODE = @value</c>.  The return-code form
        /// <c>EXEC @rc = dbo.Proc</c> is not excluded: there the assignment target is a variable.
        /// </summary>
        private static HashSet<int> ExecParameterNamePositions(SqlDocumentModel model, int start, int end)
        {
            var result = new HashSet<int>();
            if (model == null || model.Tokens == null || model.Statements == null) return result;

            List<SqlToken> tokens = model.Tokens;
            foreach (SqlStatementRange statement in model.Statements.Statements)
            {
                if (statement.End <= start || statement.Start >= end) continue;

                int first = Math.Max(0, statement.FirstToken);
                int last = Math.Min(statement.LastToken, tokens.Count - 1);
                int index = first;
                while (index <= last && tokens[index].Kind == SqlTokenKind.Comment) index++;
                if (index > last || !IsExecKeyword(tokens[index])) continue;

                bool procedureSeen = false;
                for (int i = index + 1; i <= last; i++)
                {
                    SqlToken token = tokens[i];
                    if (token.Kind != SqlTokenKind.Identifier) continue;

                    if (token.Text == null || !token.Text.StartsWith("@", StringComparison.Ordinal))
                    {
                        procedureSeen = true;
                        continue;
                    }

                    int next = i + 1;
                    while (next <= last && tokens[next].Kind == SqlTokenKind.Comment) next++;
                    bool namedArgument = next <= last && tokens[next].Text == "=";
                    if (namedArgument)
                    {
                        if (procedureSeen) result.Add(token.Start);
                    }
                    else
                    {
                        procedureSeen = true;
                    }
                }
            }

            return result;
        }

        private static bool IsExecKeyword(SqlToken token)
        {
            if (token == null || token.Kind != SqlTokenKind.Keyword) return false;
            return string.Equals(token.Text, "EXEC", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token.Text, "EXECUTE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the name is usable as a SQL identifier (spaces are allowed via brackets).</summary>
        public static bool IsValidName(string name)
        {
            string cleaned = CleanName(name);
            if (string.IsNullOrEmpty(cleaned)) return false;

            foreach (char value in cleaned)
            {
                if (value == '[' || value == ']' || value == '"' || value == '\'' ||
                    value == ';' || value == '\r' || value == '\n')
                    return false;
            }

            return true;
        }

        public static string CleanName(string name)
        {
            if (name == null) return string.Empty;
            string cleaned = name.Trim();
            if (cleaned.Length >= 2)
            {
                if (cleaned[0] == '[' && cleaned[cleaned.Length - 1] == ']')
                    cleaned = cleaned.Substring(1, cleaned.Length - 2).Replace("]]", "]");
                else if (cleaned[0] == '"' && cleaned[cleaned.Length - 1] == '"')
                    cleaned = cleaned.Substring(1, cleaned.Length - 2).Replace("\"\"", "\"");
            }

            return cleaned.Trim();
        }

        private static bool IsStatementKeyword(string value)
        {
            return SqlTokenizer.IsKeyword(value);
        }

        /// <summary>Keeps the quoting style of the original reference.</summary>
        internal static string QuoteLike(string originalToken, string name)
        {
            string original = originalToken ?? string.Empty;
            if (original.StartsWith("[", StringComparison.Ordinal))
                return "[" + name.Replace("]", "]]") + "]";

            if (original.StartsWith("\"", StringComparison.Ordinal))
                return "\"" + name.Replace("\"", "\"\"") + "\"";

            if (NeedsQuoting(name))
                return "[" + name.Replace("]", "]]") + "]";

            return name;
        }

        internal static bool NeedsQuoting(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!(char.IsLetter(name[0]) || name[0] == '_' || name[0] == '@' || name[0] == '#' || name[0] == '$'))
                return true;

            foreach (char value in name)
            {
                if (!(char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$'))
                    return true;
            }

            return false;
        }

        /// <summary>Applies the plan to a string; used by tests and by the copy-to-new-query path.</summary>
        internal static string Apply(string text, IList<SqlTextChange> changes)
        {
            if (string.IsNullOrEmpty(text) || changes == null || changes.Count == 0) return text;

            var ordered = new List<SqlTextChange>(changes);
            ordered.Sort((left, right) => right.Start.CompareTo(left.Start));

            var builder = new System.Text.StringBuilder(text);
            foreach (SqlTextChange change in ordered)
            {
                if (change.Start < 0 || change.Start + change.Length > builder.Length) continue;
                builder.Remove(change.Start, change.Length);
                builder.Insert(change.Start, change.Text);
            }

            return builder.ToString();
        }
    }
}
