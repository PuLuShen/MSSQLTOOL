using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MSSQLTool
{
    /// <summary>
    /// Applies the profile options the ScriptDOM script generator has no notion of.
    /// <para>
    /// Everything here works on the token stream of the already generated script: the generator
    /// stays responsible for the overall layout, and only whitespace between tokens is rewritten,
    /// so comments and literals can never be lost.  Every pass re-parses the text it edits, which
    /// keeps the node and token indexes it works with valid without any bookkeeping.
    /// </para>
    /// </summary>
    internal static class FormatterLayoutRules
    {
        private enum LayoutPass
        {
            /// <summary>Starts a new line before a keyword or list element.</summary>
            Breaks,
            /// <summary>Anchors the indentation of a body (subquery, CASE, block, routine).</summary>
            Indents,
            /// <summary>Collapses short subqueries onto a single line.</summary>
            Compaction
        }

        internal static string Apply(string generated, FormatterOptions options, int compatibilityLevel)
        {
            if (string.IsNullOrEmpty(generated)) return generated;

            string text = generated;
            text = RunPass(text, options, compatibilityLevel, LayoutPass.Breaks);
            text = RunPass(text, options, compatibilityLevel, LayoutPass.Indents);
            text = RunPass(text, options, compatibilityLevel, LayoutPass.Compaction);
            return CleanUp(text);
        }

        // ---------------------------------------------------------------- pass plumbing

        private static string RunPass(string text, FormatterOptions options, int compatibilityLevel, LayoutPass pass)
        {
            TSqlParser parser = TSqlFormatter.CreateParser(compatibilityLevel);
            IList<ParseError> parseErrors;
            TSqlFragment fragment;
            using (var reader = new StringReader(text))
                fragment = parser.Parse(reader, out parseErrors);

            // Never rewrite a tree that did not parse: the mapping from nodes to tokens would be
            // unreliable, so the generated text is kept exactly as it is.
            if (fragment == null || parseErrors.Count > 0) return text;

            var visitor = new LayoutVisitor();
            fragment.Accept(visitor);

            List<TSqlParserToken> tokens = fragment.ScriptTokenStream?.ToList();
            if (tokens == null || tokens.Count == 0) return text;

            bool crlf = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            var layout = new Layout(tokens, crlf ? "\r\n" : "\n", options.indent.indentSize);

            bool changed;
            switch (pass)
            {
                case LayoutPass.Breaks: changed = ApplyBreaks(layout, visitor, options); break;
                case LayoutPass.Indents: changed = ApplyIndents(layout, visitor, options); break;
                default: changed = ApplyCompaction(layout, visitor, options); break;
            }

            if (!changed) return text;

            var builder = new StringBuilder(text.Length + 128);
            foreach (TSqlParserToken token in tokens) builder.Append(token.Text);
            return builder.ToString();
        }

        /// <summary>Token list plus the newline and indentation conventions of the document.</summary>
        private sealed class Layout
        {
            public readonly List<TSqlParserToken> Tokens;
            public readonly string NewLine;
            public readonly int IndentSize;

            /// <summary>Line break requests, applied from the end of the document backwards.</summary>
            private readonly List<KeyValuePair<int, string>> breaks = new List<KeyValuePair<int, string>>();

            /// <summary>Line indentation requests, applied after every break is in place.</summary>
            private readonly List<KeyValuePair<int, string>> indents = new List<KeyValuePair<int, string>>();

            /// <summary>True once a rule changed the token stream without queueing a request.</summary>
            private bool directChanges;

            /// <summary>Removes the text of a token while keeping the token itself in place.</summary>
            public bool RemoveToken(int tokenIndex)
            {
                if (tokenIndex < 0 || tokenIndex >= Tokens.Count) return false;
                if (string.IsNullOrEmpty(Tokens[tokenIndex].Text)) return false;
                Tokens[tokenIndex].Text = string.Empty;
                directChanges = true;
                return true;
            }

            public Layout(List<TSqlParserToken> tokens, string newLine, int indentSize)
            {
                Tokens = tokens;
                NewLine = newLine;
                IndentSize = Math.Max(1, Math.Min(16, indentSize));
            }

            public string Spaces(int levels) => new string(' ', Math.Max(0, levels) * IndentSize);

            public string LineIndent(int tokenIndex) => FormatterLayoutRules.LineIndent(Tokens, tokenIndex);

            /// <summary>Column of a token inside its own line.</summary>
            public int LineColumn(int tokenIndex)
            {
                int column = 0;
                for (int i = tokenIndex - 1; i >= 0; i--)
                {
                    string text = Tokens[i].Text ?? string.Empty;
                    int lastNewLine = text.LastIndexOf('\n');
                    if (lastNewLine >= 0) return column + text.Length - lastNewLine - 1;
                    column += text.Length;
                }

                return column;
            }

            /// <summary>True when nothing but whitespace separates the token from the previous line.</summary>
            public bool IsAtLineStart(int tokenIndex)
            {
                for (int i = tokenIndex - 1; i >= 0; i--)
                {
                    if (Tokens[i].TokenType != TSqlTokenType.WhiteSpace) return false;
                    if ((Tokens[i].Text ?? string.Empty).IndexOf('\n') >= 0) return true;
                }

                return true;
            }

            /// <summary>
            /// Requests a line break before a token, unless it already opens a line: keeping the
            /// generator's own line breaks means an option that is already satisfied never moves
            /// text the user did not ask to move.
            /// </summary>
            public void EnsureBreakBefore(int tokenIndex, string indent)
            {
                if (tokenIndex <= 0 || tokenIndex >= Tokens.Count) return;
                if (!IsAtLineStart(tokenIndex)) breaks.Add(new KeyValuePair<int, string>(tokenIndex, indent));
            }

            /// <summary>Puts a token on its own line and aligns that line.</summary>
            public void StackLine(int tokenIndex, string indent)
            {
                EnsureBreakBefore(tokenIndex, indent);
                QueueLineIndent(tokenIndex, indent);
            }

            /// <summary>Sets the indentation of the line a token opens, without moving the token.</summary>
            public void QueueLineIndent(int tokenIndex, string indent)
            {
                if (tokenIndex <= 0 || tokenIndex >= Tokens.Count) return;
                indents.Add(new KeyValuePair<int, string>(tokenIndex, indent));
            }

            /// <summary>
            /// Replaces every whitespace token that precedes a token with one separator.  The
            /// generator can split "\r\n     " into two whitespace tokens, so joining two pieces of
            /// text has to normalise all of them.
            /// </summary>
            public bool CollapseWhitespaceBefore(int tokenIndex, string separator)
            {
                if (tokenIndex <= 0 || tokenIndex >= Tokens.Count) return false;

                int first = tokenIndex;
                for (int i = tokenIndex - 1; i >= 0; i--)
                {
                    if (Tokens[i].TokenType != TSqlTokenType.WhiteSpace) break;
                    first = i;
                }

                if (first == tokenIndex) return false;

                bool changed = false;
                for (int i = first; i < tokenIndex; i++)
                {
                    string desired = i == first ? separator : string.Empty;
                    if (string.Equals(Tokens[i].Text, desired, StringComparison.Ordinal)) continue;
                    Tokens[i].Text = desired;
                    changed = true;
                }

                directChanges |= changed;
                return changed;
            }

            /// <summary>Moves a token back onto the line above it.</summary>
            public bool JoinLine(int tokenIndex)
            {
                if (tokenIndex <= 0 || tokenIndex >= Tokens.Count) return false;

                bool changed = false;
                for (int i = tokenIndex - 1; i >= 0; i--)
                {
                    if (Tokens[i].TokenType != TSqlTokenType.WhiteSpace) break;
                    string text = Tokens[i].Text ?? string.Empty;
                    int lastNewLine = text.LastIndexOf('\n');
                    if (lastNewLine >= 0)
                    {
                        Tokens[i].Text = text.Substring(0, lastNewLine).TrimEnd() + " ";
                        changed = true;
                        break;
                    }

                    if (text.Length > 0)
                    {
                        Tokens[i].Text = string.Empty;
                        changed = true;
                    }
                }

                directChanges |= changed;
                return changed;
            }

            /// <summary>Applies the queued line breaks, then the queued indentation changes.</summary>
            public bool Commit()
            {
                if (breaks.Count == 0 && indents.Count == 0) return directChanges;

                bool changed = directChanges;

                // Applying from the bottom up keeps every not-yet-applied index valid, even
                // though a request may insert a whitespace token.
                foreach (var request in breaks.OrderByDescending(r => r.Key))
                {
                    int index = request.Key;
                    if (index <= 0 || index >= Tokens.Count) continue;
                    if (Tokens[index - 1].TokenType == TSqlTokenType.WhiteSpace)
                        Tokens[index - 1].Text = NewLine + request.Value;
                    else
                        Tokens.Insert(index, new TSqlParserToken(TSqlTokenType.WhiteSpace, NewLine + request.Value));
                    changed = true;
                }

                breaks.Clear();

                foreach (var request in indents.OrderBy(r => r.Key))
                {
                    int index = request.Key;
                    if (index <= 0 || index >= Tokens.Count) continue;
                    if (!IsAtLineStart(index)) continue;
                    changed |= SetLineIndentNow(index, request.Value);
                }

                indents.Clear();
                return changed;
            }

            /// <summary>
            /// Sets the indentation of the line the token opens.  The whitespace that precedes the
            /// token is normalised into the token that carries the line break, so a line whose
            /// indentation arrived as several whitespace tokens is rewritten as one.
            /// </summary>
            private bool SetLineIndentNow(int tokenIndex, string indent)
            {
                int newLineToken = -1;
                for (int i = tokenIndex - 1; i >= 0; i--)
                {
                    if (Tokens[i].TokenType != TSqlTokenType.WhiteSpace) break;
                    if ((Tokens[i].Text ?? string.Empty).IndexOf('\n') >= 0) { newLineToken = i; break; }
                }

                if (newLineToken < 0) return false;

                bool changed = false;
                string text = Tokens[newLineToken].Text;
                int lastNewLine = text.LastIndexOf('\n');
                string replacement = text.Substring(0, lastNewLine + 1) + indent;
                if (!string.Equals(replacement, text, StringComparison.Ordinal))
                {
                    Tokens[newLineToken].Text = replacement;
                    changed = true;
                }

                for (int i = newLineToken + 1; i < tokenIndex; i++)
                {
                    if (Tokens[i].TokenType != TSqlTokenType.WhiteSpace) break;
                    if ((Tokens[i].Text ?? string.Empty).Length == 0) continue;
                    Tokens[i].Text = string.Empty;
                    changed = true;
                }

                return changed;
            }

            /// <summary>
            /// Re-anchors the lines of a body to <paramref name="indent"/>: the shallowest body line
            /// moves to the requested column and every other line moves by the same amount, so the
            /// alignment and nesting the generator produced keeps its shape.
            /// </summary>
            public bool SetRangeIndent(int firstToken, int lastToken, string indent)
            {
                if (firstToken < 0 || lastToken <= firstToken) return false;

                var bodyLines = new List<KeyValuePair<int, int>>();
                int shallowest = int.MaxValue;
                for (int i = firstToken; i <= lastToken && i < Tokens.Count; i++)
                {
                    if (i == firstToken) continue;
                    if (Tokens[i].TokenType == TSqlTokenType.WhiteSpace) continue;
                    if (!IsAtLineStart(i)) continue;

                    int length = LineIndent(i).Length;
                    bodyLines.Add(new KeyValuePair<int, int>(i, length));
                    if (length < shallowest) shallowest = length;
                }

                if (bodyLines.Count == 0 || shallowest == int.MaxValue) return false;

                int shift = indent.Length - shallowest;
                foreach (var line in bodyLines)
                    QueueLineIndent(line.Key, new string(' ', Math.Max(0, line.Value + shift)));
                return true;
            }
        }

        // ---------------------------------------------------------------- rules: new lines

        private static bool ApplyBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            ApplyInsertBreaks(layout, visitor, options);
            ApplyUpdateBreaks(layout, visitor, options);
            ApplyMergeBreaks(layout, visitor, options);
            ApplyJoinOnBreaks(layout, visitor, options);
            ApplyListBreaks(layout, visitor, options);
            ApplyCaseBreaks(layout, visitor, options);
            ApplyBlockBreaks(layout, visitor, options);
            ApplyRoutineBreaks(layout, visitor, options);
            ApplyCreateTableBreaks(layout, visitor, options);
            ApplyConditionBreakPosition(layout, visitor, options);
            ApplySemicolonOption(layout, visitor, options);

            return layout.Commit();
        }

        /// <summary>
        /// The generator always terminates a statement with a semicolon, so the option that asks
        /// for no semicolons has to take the terminator away again.  A semicolon that opens a line
        /// separates a statement from the one before it (";WITH ...") and is left alone.
        /// </summary>
        private static void ApplySemicolonOption(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            if (options.includeSemicolons) return;

            for (int i = 0; i < layout.Tokens.Count; i++)
            {
                if (layout.Tokens[i].TokenType != TSqlTokenType.Semicolon) continue;
                if (layout.IsAtLineStart(i)) continue;
                layout.RemoveToken(i);
            }
        }

        private static void ApplyInsertBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (InsertStatement insert in visitor.Inserts)
            {
                int values = FindKeyword(tokens, insert.FirstTokenIndex, insert.LastTokenIndex, TSqlTokenType.Values);
                if (values < 0) continue;

                string statementIndent = layout.LineIndent(insert.FirstTokenIndex);
                if (options.insert.newLineBeforeValues)
                    layout.EnsureBreakBefore(values, statementIndent);
                else
                    // VALUES shares the target line again; the generator always breaks it.
                    layout.JoinLine(values);

                var source = insert.InsertSpecification?.InsertSource as ValuesInsertSource;
                if (source == null || source.RowValues.Count == 0) continue;

                // The generator pads the VALUES list so it lines up under the insert target list.
                // Once the list starts on the VALUES line, that padding is only visual noise.
                int firstRow = FirstMeaningful(tokens, source.RowValues[0].FirstTokenIndex, source.RowValues[0].LastTokenIndex);
                layout.CollapseWhitespaceBefore(firstRow, " ");

                string rowIndent = layout.LineIndent(values) + new string(' ', layout.LineColumn(firstRow));
                for (int i = 1; i < source.RowValues.Count; i++)
                {
                    int row = FirstMeaningful(tokens, source.RowValues[i].FirstTokenIndex, source.RowValues[i].LastTokenIndex);
                    if (options.insert.stackMultipleValues)
                        layout.StackLine(row, rowIndent);
                    else
                        // One row per line is the generator's default; joining them back is the
                        // only way "one row per line" can be switched off.
                        layout.JoinLine(row);
                }
            }
        }

        private static void ApplyUpdateBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (UpdateStatement update in visitor.Updates)
            {
                var specification = update.UpdateSpecification;
                if (specification == null) continue;

                string statementIndent = layout.LineIndent(update.FirstTokenIndex);

                if (options.update.newLineBeforeSet && specification.SetClauses.Count > 0)
                {
                    int set = FindKeyword(tokens, specification.Target?.LastTokenIndex ?? update.FirstTokenIndex,
                        specification.SetClauses[0].FirstTokenIndex, TSqlTokenType.Set);
                    if (set >= 0) layout.EnsureBreakBefore(set, statementIndent);
                }

                if (options.update.fromFollowsSelect && specification.FromClause != null)
                {
                    int previousEnd = specification.SetClauses.Count > 0
                        ? specification.SetClauses[specification.SetClauses.Count - 1].LastTokenIndex
                        : update.FirstTokenIndex;
                    int from = FindKeyword(tokens, previousEnd, specification.FromClause.FirstTokenIndex, TSqlTokenType.From);
                    if (from >= 0) layout.EnsureBreakBefore(from, statementIndent);
                }
            }
        }

        private static void ApplyMergeBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (MergeStatement merge in visitor.Merges)
            {
                var specification = merge.MergeSpecification;
                if (specification == null) continue;

                string statementIndent = layout.LineIndent(merge.FirstTokenIndex);
                string clauseIndent = statementIndent + layout.Spaces(1);

                // "MERGE INTO target AS alias": the generator puts the alias on its own line with a
                // leading space, which reads as a stray fragment.  Keep it on the target line.
                {
                    int limit = specification.TableReference?.FirstTokenIndex ?? merge.LastTokenIndex;
                    int alias = FindKeyword(tokens, merge.FirstTokenIndex, limit, TSqlTokenType.As);
                    if (alias > 0 && layout.IsAtLineStart(alias))
                        layout.CollapseWhitespaceBefore(alias, " ");
                }

                if (options.merge.newLineBeforeInto)
                {
                    int into = FindKeyword(tokens, merge.FirstTokenIndex,
                        specification.Target?.FirstTokenIndex ?? merge.FirstTokenIndex, TSqlTokenType.Into);
                    if (into >= 0) layout.EnsureBreakBefore(into, statementIndent);
                }

                if (options.merge.newLineBeforeUsing && specification.TableReference != null)
                {
                    int usingKeyword = FindTokenByText(tokens, specification.Target?.LastTokenIndex ?? merge.FirstTokenIndex,
                        specification.TableReference.FirstTokenIndex, "USING");
                    if (usingKeyword >= 0) layout.EnsureBreakBefore(usingKeyword, statementIndent);
                }

                if (options.merge.newLineBeforeOn && specification.SearchCondition != null)
                {
                    int on = FindKeyword(tokens, specification.TableReference?.LastTokenIndex ?? merge.FirstTokenIndex,
                        specification.SearchCondition.FirstTokenIndex, TSqlTokenType.On);
                    if (on >= 0) layout.EnsureBreakBefore(on, clauseIndent);
                }

                // MergeActionClause spans start at the action, so WHEN and THEN are located by
                // scanning back from the clause into the text that precedes it.
                int searchStart = specification.SearchCondition?.LastTokenIndex ?? merge.FirstTokenIndex;
                foreach (MergeActionClause clause in specification.ActionClauses)
                {
                    int when = FindKeyword(tokens, searchStart, clause.FirstTokenIndex, TSqlTokenType.When);
                    if (options.merge.newLineBeforeWhen && when >= 0)
                        layout.EnsureBreakBefore(when, statementIndent);

                    if (options.merge.newLineBeforeThen && clause.Action != null)
                    {
                        int then = FindKeyword(tokens, when >= 0 ? when : searchStart,
                            clause.Action.FirstTokenIndex, TSqlTokenType.Then);
                        if (then >= 0) layout.EnsureBreakBefore(then, clauseIndent);
                    }

                    searchStart = Math.Max(searchStart, clause.LastTokenIndex);
                }
            }
        }

        private static void ApplyJoinOnBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            if (!options.lineBreaks.newLineBeforeOn) return;

            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (QualifiedJoin join in visitor.Joins)
            {
                if (join.SearchCondition == null) continue;
                int on = FindKeyword(tokens, join.FirstTokenIndex, join.SearchCondition.FirstTokenIndex, TSqlTokenType.On);
                if (on >= 0) layout.EnsureBreakBefore(on, layout.LineIndent(join.FirstTokenIndex) + layout.Spaces(1));
            }
        }

        /// <summary>One element per line for FROM, GROUP BY and ORDER BY lists.</summary>
        private static void ApplyListBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;

            if (options.select.stackFromList)
            {
                foreach (FromClause clause in visitor.FromClauses)
                {
                    if (clause.TableReferences == null || clause.TableReferences.Count < 2) continue;
                    StackRange(layout, clause.TableReferences.Select(r => new TokenRange(r.FirstTokenIndex, r.LastTokenIndex)).ToList());
                }
            }

            if (options.select.stackGroupBy)
            {
                foreach (GroupByClause clause in visitor.GroupBys)
                {
                    if (clause.GroupingSpecifications == null || clause.GroupingSpecifications.Count < 2) continue;
                    StackRange(layout, clause.GroupingSpecifications.Select(r => new TokenRange(r.FirstTokenIndex, r.LastTokenIndex)).ToList());
                }
            }

            if (options.select.stackOrderBy)
            {
                foreach (OrderByClause clause in visitor.OrderBys)
                {
                    if (clause.OrderByElements == null || clause.OrderByElements.Count < 2) continue;
                    StackRange(layout, clause.OrderByElements.Select(r => new TokenRange(r.FirstTokenIndex, r.LastTokenIndex)).ToList());
                }
            }

            if (options.declare.stackVariables)
            {
                foreach (DeclareVariableStatement statement in visitor.Declares)
                {
                    if (statement.Declarations == null || statement.Declarations.Count < 2) continue;
                    StackRange(layout, statement.Declarations.Select(r => new TokenRange(r.FirstTokenIndex, r.LastTokenIndex)).ToList());
                }
            }

            if (options.routine.stackParameters)
            {
                foreach (ProcedureStatementBody procedure in visitor.Procedures)
                    StackRange(layout, ParameterRanges(procedure.Parameters));
                foreach (FunctionStatementBody function in visitor.Functions)
                    StackRange(layout, ParameterRanges(function.Parameters));
            }
        }

        private static List<TokenRange> ParameterRanges(IList<ProcedureParameter> parameters)
            => parameters == null || parameters.Count < 2
                ? new List<TokenRange>()
                : parameters.Select(p => new TokenRange(p.FirstTokenIndex, p.LastTokenIndex)).ToList();

        private struct TokenRange
        {
            public TokenRange(int first, int last) { First = first; Last = last; }
            public int First;
            public int Last;
        }

        /// <summary>Puts every element after the first on its own line, aligned under the first.</summary>
        private static void StackRange(Layout layout, List<TokenRange> ranges)
        {
            if (ranges.Count < 2) return;

            List<TSqlParserToken> tokens = layout.Tokens;
            int first = FirstMeaningful(tokens, ranges[0].First, ranges[0].Last);
            string indent = layout.LineIndent(first) + new string(' ', layout.LineColumn(first));

            for (int i = 1; i < ranges.Count; i++)
            {
                int token = FirstMeaningful(tokens, ranges[i].First, ranges[i].Last);
                layout.StackLine(token, indent);
            }
        }

        private static void ApplyCaseBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            CaseFormatOptions settings = options.caseExpression;

            foreach (CaseExpression expression in visitor.Cases)
            {
                List<WhenClause> whenClauses = WhenClausesOf(expression);
                string bodyIndent = layout.LineIndent(expression.FirstTokenIndex) + layout.Spaces(1);

                if (settings.newLineBeforeWhen)
                {
                    foreach (WhenClause clause in whenClauses)
                        layout.EnsureBreakBefore(clause.FirstTokenIndex, bodyIndent);
                }

                if (settings.newLineBeforeThen)
                {
                    foreach (WhenClause clause in whenClauses)
                    {
                        if (clause.ThenExpression == null) continue;
                        int then = FindKeyword(tokens, clause.FirstTokenIndex, clause.ThenExpression.FirstTokenIndex, TSqlTokenType.Then);
                        if (then >= 0) layout.EnsureBreakBefore(then, bodyIndent);
                    }
                }

                if (settings.newLineBeforeElse && expression.ElseExpression != null)
                {
                    int elseKeyword = FindKeyword(tokens,
                        whenClauses.Count > 0 ? whenClauses[whenClauses.Count - 1].LastTokenIndex : expression.FirstTokenIndex,
                        expression.ElseExpression.FirstTokenIndex, TSqlTokenType.Else);
                    if (elseKeyword >= 0) layout.EnsureBreakBefore(elseKeyword, bodyIndent);
                }
            }
        }

        private static List<WhenClause> WhenClausesOf(CaseExpression expression)
        {
            if (expression is SearchedCaseExpression searched) return searched.WhenClauses.Cast<WhenClause>().ToList();
            if (expression is SimpleCaseExpression simple) return simple.WhenClauses.Cast<WhenClause>().ToList();
            return new List<WhenClause>();
        }

        private static void ApplyBlockBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            BlockFormatOptions settings = options.block;

            foreach (BeginEndBlockStatement block in visitor.Blocks)
                BreakBlock(layout, block.FirstTokenIndex, block.LastTokenIndex, settings.newLineAfterBegin, settings.newLineBeforeEnd);

            foreach (IfStatement statement in visitor.Ifs)
            {
                string statementIndent = layout.LineIndent(statement.FirstTokenIndex);
                if (settings.newLineAfterIfCondition && statement.ThenStatement != null && statement.ThenStatement.FirstTokenIndex >= 0)
                    layout.EnsureBreakBefore(FirstMeaningful(tokens, statement.ThenStatement.FirstTokenIndex, statement.ThenStatement.LastTokenIndex),
                        statementIndent + layout.Spaces(1));

                if (settings.newLineBeforeElse && statement.ElseStatement != null && statement.ElseStatement.FirstTokenIndex >= 0)
                {
                    int elseKeyword = FindKeyword(tokens, statement.ThenStatement?.LastTokenIndex ?? statement.FirstTokenIndex,
                        statement.ElseStatement.FirstTokenIndex, TSqlTokenType.Else);
                    if (elseKeyword >= 0) layout.EnsureBreakBefore(elseKeyword, statementIndent);
                }
            }

            foreach (WhileStatement statement in visitor.Whiles)
            {
                if (!settings.newLineAfterWhileCondition || statement.Statement == null || statement.Statement.FirstTokenIndex < 0) continue;
                layout.EnsureBreakBefore(FirstMeaningful(tokens, statement.Statement.FirstTokenIndex, statement.Statement.LastTokenIndex),
                    layout.LineIndent(statement.FirstTokenIndex) + layout.Spaces(1));
            }
        }

        /// <summary>Puts the statements of a BEGIN..END block on their own lines.</summary>
        private static void BreakBlock(Layout layout, int firstToken, int lastToken, bool newLineAfterBegin, bool newLineBeforeEnd)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            int begin = FindKeyword(tokens, firstToken, lastToken, TSqlTokenType.Begin);
            if (begin < 0) return;

            int end = FindMatchingEnd(tokens, begin, lastToken);
            string beginIndent = layout.LineIndent(begin);

            if (newLineAfterBegin)
            {
                int body = FirstMeaningful(tokens, begin + 1, end >= 0 ? end - 1 : lastToken);
                if (body > begin) layout.EnsureBreakBefore(body, beginIndent + layout.Spaces(1));
            }

            if (newLineBeforeEnd && end >= 0) layout.EnsureBreakBefore(end, beginIndent);
        }

        private static void ApplyRoutineBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            RoutineFormatOptions settings = options.routine;

            if (settings.newLineBeforeReturns)
            {
                foreach (FunctionStatementBody function in visitor.Functions)
                {
                    if (function.ReturnType == null) continue;
                    int previousEnd = function.Parameters != null && function.Parameters.Count > 0
                        ? function.Parameters[function.Parameters.Count - 1].LastTokenIndex
                        : function.FirstTokenIndex;
                    int returns = FindTokenByText(tokens, previousEnd, function.ReturnType.FirstTokenIndex, "RETURNS");
                    if (returns >= 0) layout.EnsureBreakBefore(returns, layout.LineIndent(function.FirstTokenIndex));
                }
            }

            // "Indent the BEGIN..END keywords" also has to put them on their own line: a BEGIN that
            // shares the AS line cannot be indented on its own.
            if (settings.indentBeginEnd)
            {
                foreach (ProcedureStatementBody procedure in visitor.Procedures)
                    BreakRoutineBeginEnd(layout, procedure.StatementList, procedure);
                foreach (FunctionStatementBody function in visitor.Functions)
                    BreakRoutineBeginEnd(layout, function.StatementList, function);
            }

            if (options.trigger.indentBeginEnd)
            {
                foreach (TriggerStatementBody trigger in visitor.Triggers)
                    BreakRoutineBeginEnd(layout, trigger.StatementList, trigger);
            }
        }

        private static void BreakRoutineBeginEnd(Layout layout, StatementList body, TSqlFragment owner)
        {
            if (body == null) return;

            List<TSqlParserToken> tokens = layout.Tokens;
            int begin = FindKeyword(tokens, owner.FirstTokenIndex, owner.LastTokenIndex, TSqlTokenType.Begin);
            if (begin < 0) return;

            string indent = layout.LineIndent(owner.FirstTokenIndex) + layout.Spaces(1);
            layout.StackLine(begin, indent);

            int end = FindMatchingEnd(tokens, begin, owner.LastTokenIndex);
            if (end >= 0) layout.StackLine(end, indent);
        }

        private static void ApplyCreateTableBreaks(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            if (!options.createTable.stackStorageOptions) return;

            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (CreateTableStatement statement in visitor.CreateTables)
            {
                // The table options of the WITH clause do not carry token indexes, so the list is
                // read straight from the tokens between its parentheses.
                int with = FindTokenByText(tokens, statement.FirstTokenIndex, statement.LastTokenIndex, "WITH");
                if (with < 0) continue;
                int open = FindKeyword(tokens, with, statement.LastTokenIndex, TSqlTokenType.LeftParenthesis);
                if (open < 0) continue;
                int close = FindMatchingCloseParenthesis(tokens, open, statement.LastTokenIndex);
                if (close < 0) continue;
                StackParenthesizedList(layout, open, close);
            }
        }

        /// <summary>Puts every top-level element of a parenthesised list on its own line.</summary>
        private static void StackParenthesizedList(Layout layout, int open, int close)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            int first = FirstMeaningful(tokens, open + 1, close - 1);
            if (first <= open) return;

            string indent = layout.LineIndent(first) + new string(' ', layout.LineColumn(first));

            int depth = 0;
            for (int i = open + 1; i < close; i++)
            {
                TSqlTokenType type = tokens[i].TokenType;
                if (type == TSqlTokenType.LeftParenthesis) depth++;
                else if (type == TSqlTokenType.RightParenthesis) depth--;
                else if (type == TSqlTokenType.Comma && depth == 0)
                {
                    int next = FirstMeaningful(tokens, i + 1, close - 1);
                    if (next > i) layout.StackLine(next, indent);
                }
            }
        }

        /// <summary>
        /// Moves the line break of a condition list from before the AND/OR to after it.  Lines that
        /// already start with the operator are the only ones that need moving; the operator then
        /// ends the previous line and the operand starts the new one.
        /// </summary>
        private static void ApplyConditionBreakPosition(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            if (options.select.conditionBreak != ConditionBreakPosition.AfterOperator) return;

            List<TSqlParserToken> tokens = layout.Tokens;
            foreach (BooleanBinaryExpression expression in visitor.BooleanBinaries)
            {
                if (expression.BinaryExpressionType != BooleanBinaryExpressionType.And
                    && expression.BinaryExpressionType != BooleanBinaryExpressionType.Or) continue;
                if (expression.SecondExpression == null) continue;

                int start = expression.FirstExpression?.LastTokenIndex ?? expression.FirstTokenIndex;
                int op = FindKeyword(tokens, start, expression.SecondExpression.FirstTokenIndex, TSqlTokenType.And);
                if (op < 0) op = FindKeyword(tokens, start, expression.SecondExpression.FirstTokenIndex, TSqlTokenType.Or);
                if (op <= 0) continue;
                if (!layout.IsAtLineStart(op)) continue;

                string indent = layout.LineIndent(op);
                layout.JoinLine(op);
                int next = NextMeaningful(tokens, op + 1);
                if (next >= 0) layout.EnsureBreakBefore(next, indent);
            }
        }

        // ---------------------------------------------------------------- rules: indentation

        private static bool ApplyIndents(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            bool changed = false;

            foreach (CaseExpression expression in visitor.Cases)
                changed |= IndentBody(layout, expression.FirstTokenIndex, expression.LastTokenIndex,
                    options.caseExpression.indentBody || options.indent.indentCase);

            foreach (BeginEndBlockStatement block in visitor.Blocks)
            {
                int begin = FindKeyword(layout.Tokens, block.FirstTokenIndex, block.LastTokenIndex, TSqlTokenType.Begin);
                int end = begin < 0 ? -1 : FindMatchingEnd(layout.Tokens, begin, block.LastTokenIndex);
                if (begin < 0 || end < 0) continue;
                changed |= IndentBody(layout, begin, end - 1, options.block.indentCode || options.indent.indentBlock);
                layout.QueueLineIndent(end, layout.LineIndent(begin));
            }

            foreach (ProcedureStatementBody procedure in visitor.Procedures)
                changed |= IndentRoutineBody(layout, procedure.StatementList, procedure, options.routine.indentBody, options.routine.indentBeginEnd);

            foreach (FunctionStatementBody function in visitor.Functions)
                changed |= IndentRoutineBody(layout, function.StatementList, function, options.routine.indentBody, options.routine.indentBeginEnd);

            foreach (TriggerStatementBody trigger in visitor.Triggers)
                changed |= IndentRoutineBody(layout, trigger.StatementList, trigger, options.trigger.indentBody, options.trigger.indentBeginEnd);

            foreach (QualifiedJoin join in visitor.Joins)
                changed |= IndentConditionList(layout, join.SearchCondition, join.FirstTokenIndex, options.select.indentOnCondition);

            foreach (WhereClause clause in visitor.Wheres)
                changed |= IndentConditionList(layout, clause.SearchCondition, clause.FirstTokenIndex, options.select.indentWhereCondition);

            foreach (DeclareCursorStatement cursor in visitor.Cursors)
            {
                if (cursor.CursorDefinition?.Select == null) continue;
                changed |= IndentBody(layout, cursor.CursorDefinition.Select.FirstTokenIndex,
                    cursor.CursorDefinition.Select.LastTokenIndex, options.declare.indentCursorSubquery);
            }

            foreach (TSqlFragment subquery in visitor.Subqueries)
                changed |= IndentBody(layout, subquery.FirstTokenIndex, subquery.LastTokenIndex,
                    (options.subquery.indentSubquery || options.indent.indentSubquery) && options.subquery.inheritMainQueryFormat);

            return layout.Commit();
        }

        /// <summary>
        /// Anchors the lines of a body one level inside the line that opens it, or levels with it
        /// when the option is switched off.
        /// </summary>
        private static bool IndentBody(Layout layout, int firstToken, int lastToken, bool indent)
        {
            string anchor = layout.LineIndent(firstToken);
            return layout.SetRangeIndent(firstToken, lastToken, indent ? anchor + layout.Spaces(1) : anchor);
        }

        private static bool IndentRoutineBody(Layout layout, StatementList body, TSqlFragment owner, bool indentBody, bool indentBeginEnd)
        {
            List<TSqlParserToken> tokens = layout.Tokens;
            int begin = FindKeyword(tokens, owner.FirstTokenIndex, owner.LastTokenIndex, TSqlTokenType.Begin);
            if (begin < 0) return false;

            int end = FindMatchingEnd(tokens, begin, owner.LastTokenIndex);
            if (end < 0) return false;

            // The routine body is indented from its BEGIN keyword, and END lines up with BEGIN, so
            // a BEGIN that shares the "AS" line still anchors the body it opens.
            string beginIndent = indentBeginEnd
                ? layout.LineIndent(owner.FirstTokenIndex) + layout.Spaces(1)
                : layout.LineIndent(begin);

            bool changed = layout.SetRangeIndent(begin, end - 1,
                indentBody ? beginIndent + layout.Spaces(1) : beginIndent);
            layout.QueueLineIndent(end, beginIndent);
            return changed || indentBeginEnd;
        }

        /// <summary>
        /// Indents the continuation lines of a WHERE or ON condition.  Only lines that start with a
        /// condition operator are touched, so nested expressions keep their own indentation.
        /// </summary>
        private static bool IndentConditionList(Layout layout, BooleanExpression condition, int ownerToken, bool indent)
        {
            if (condition == null) return false;

            string target = layout.LineIndent(ownerToken) + (indent ? layout.Spaces(1) : string.Empty);
            bool changed = false;

            for (int i = condition.FirstTokenIndex; i <= condition.LastTokenIndex && i < layout.Tokens.Count; i++)
            {
                TSqlTokenType type = layout.Tokens[i].TokenType;
                if (type != TSqlTokenType.And && type != TSqlTokenType.Or) continue;
                if (!layout.IsAtLineStart(i)) continue;
                layout.QueueLineIndent(i, target);
                changed = true;
            }

            return changed;
        }

        // ---------------------------------------------------------------- rules: compaction

        private static bool ApplyCompaction(Layout layout, LayoutVisitor visitor, FormatterOptions options)
        {
            bool changed = false;

            if (options.subquery.allowSingleLine || options.compact.keepShortSubquerySingleLine)
            {
                int threshold = options.subquery.allowSingleLine
                    ? options.subquery.singleLineThreshold
                    : options.compact.singleLineThreshold;
                foreach (TSqlFragment subquery in visitor.Subqueries)
                    changed |= CollapseNode(layout, subquery.FirstTokenIndex, subquery.LastTokenIndex, threshold);
            }

            if (options.declare.cursorQuerySingleLine)
            {
                foreach (DeclareCursorStatement cursor in visitor.Cursors)
                {
                    if (cursor.CursorDefinition?.Select == null) continue;
                    changed |= CollapseNode(layout, cursor.CursorDefinition.Select.FirstTokenIndex,
                        cursor.CursorDefinition.Select.LastTokenIndex, options.compact.singleLineThreshold);
                }
            }

            return changed;
        }

        /// <summary>
        /// Replaces a token range with its single-line form when it fits into
        /// <paramref name="threshold"/> characters.  Ranges containing comments are left alone so
        /// no comment can be swallowed by the collapse.
        /// </summary>
        private static bool CollapseNode(Layout layout, int firstToken, int lastToken, int threshold)
        {
            if (threshold <= 0 || firstToken < 0 || lastToken <= firstToken || lastToken >= layout.Tokens.Count) return false;

            var builder = new StringBuilder();
            for (int i = firstToken; i <= lastToken; i++)
            {
                TSqlParserToken token = layout.Tokens[i];
                if (token.TokenType == TSqlTokenType.SingleLineComment
                    || token.TokenType == TSqlTokenType.MultilineComment) return false;
                builder.Append(token.TokenType == TSqlTokenType.WhiteSpace ? " " : token.Text ?? string.Empty);
            }

            string collapsed = CollapseWhitespace(builder.ToString());
            if (collapsed.Length > threshold) return false;

            layout.Tokens[firstToken].Text = collapsed;
            for (int i = firstToken + 1; i <= lastToken; i++) layout.Tokens[i].Text = string.Empty;
            return true;
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char value in text.Trim())
            {
                if (char.IsWhiteSpace(value)) { pendingSpace = true; continue; }
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(value);
            }

            return builder.ToString();
        }

        // ---------------------------------------------------------------- token helpers

        /// <summary>
        /// Leading whitespace of the line that contains the token, wherever the token sits inside
        /// that line.  A line break and its indentation may arrive as two separate whitespace
        /// tokens, so every whitespace token after the break counts towards the indentation.
        /// </summary>
        private static string LineIndent(List<TSqlParserToken> tokens, int tokenIndex)
        {
            if (tokenIndex < 0 || tokenIndex >= tokens.Count) return string.Empty;

            int lineBreak = -1;
            for (int i = tokenIndex - 1; i >= 0; i--)
            {
                if ((tokens[i].Text ?? string.Empty).IndexOf('\n') >= 0) { lineBreak = i; break; }
            }

            var indent = new StringBuilder();
            if (lineBreak >= 0 && tokens[lineBreak].TokenType == TSqlTokenType.WhiteSpace)
            {
                string text = tokens[lineBreak].Text;
                indent.Append(text.Substring(text.LastIndexOf('\n') + 1));
            }

            for (int i = lineBreak + 1; i < tokenIndex; i++)
            {
                if (tokens[i].TokenType != TSqlTokenType.WhiteSpace) break;
                indent.Append(tokens[i].Text);
            }

            return indent.ToString();
        }

        private static int FirstMeaningful(List<TSqlParserToken> tokens, int start, int end)
        {
            for (int i = Math.Max(0, start); i <= end && i < tokens.Count; i++)
                if (tokens[i].TokenType != TSqlTokenType.WhiteSpace) return i;
            return Math.Max(0, start);
        }

        private static int NextMeaningful(List<TSqlParserToken> tokens, int start)
        {
            for (int i = Math.Max(0, start); i < tokens.Count; i++)
                if (tokens[i].TokenType != TSqlTokenType.WhiteSpace) return i;
            return -1;
        }

        private static int FindKeyword(List<TSqlParserToken> tokens, int start, int end, TSqlTokenType keyword)
        {
            int depth = 0;
            int limit = Math.Min(end, tokens.Count - 1);
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                TSqlTokenType type = tokens[i].TokenType;
                // The keyword is tested before the parentheses are counted, so a search for a
                // parenthesis itself still matches at depth zero.
                if (depth == 0 && type == keyword) return i;
                if (type == TSqlTokenType.LeftParenthesis) depth++;
                else if (type == TSqlTokenType.RightParenthesis) depth--;
            }

            return -1;
        }

        /// <summary>
        /// Finds a keyword by its text.  Several T-SQL keywords (USING, RETURNS) have no token
        /// type of their own, so they can only be recognised by what they spell.
        /// </summary>
        private static int FindTokenByText(List<TSqlParserToken> tokens, int start, int end, string keyword)
        {
            int depth = 0;
            int limit = Math.Min(end, tokens.Count - 1);
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                TSqlParserToken token = tokens[i];
                if (token.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                else if (token.TokenType == TSqlTokenType.RightParenthesis) depth--;
                else if (depth == 0 && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        /// <summary>The END that closes the BEGIN at <paramref name="beginIndex"/>.</summary>
        private static int FindMatchingEnd(List<TSqlParserToken> tokens, int beginIndex, int limit)
        {
            int depth = 0;
            int last = Math.Min(limit, tokens.Count - 1);
            for (int i = beginIndex; i <= last; i++)
            {
                TSqlTokenType type = tokens[i].TokenType;
                if (type == TSqlTokenType.Begin) depth++;
                else if (type == TSqlTokenType.End)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        /// <summary>The parenthesis that closes the one at <paramref name="openIndex"/>.</summary>
        private static int FindMatchingCloseParenthesis(List<TSqlParserToken> tokens, int openIndex, int limit)
        {
            int depth = 0;
            int last = Math.Min(limit, tokens.Count - 1);
            for (int i = openIndex; i <= last; i++)
            {
                TSqlTokenType type = tokens[i].TokenType;
                if (type == TSqlTokenType.LeftParenthesis) depth++;
                else if (type == TSqlTokenType.RightParenthesis)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static string CleanUp(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            bool crlf = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                builder.Append(lines[i].TrimEnd());
                if (i < lines.Length - 1) builder.Append(crlf ? "\r\n" : "\n");
            }

            return builder.ToString();
        }

        // ---------------------------------------------------------------- visitor

        private sealed class LayoutVisitor : TSqlFragmentVisitor
        {
            public readonly List<InsertStatement> Inserts = new List<InsertStatement>();
            public readonly List<UpdateStatement> Updates = new List<UpdateStatement>();
            public readonly List<MergeStatement> Merges = new List<MergeStatement>();
            public readonly List<CaseExpression> Cases = new List<CaseExpression>();
            public readonly List<BeginEndBlockStatement> Blocks = new List<BeginEndBlockStatement>();
            public readonly List<IfStatement> Ifs = new List<IfStatement>();
            public readonly List<WhileStatement> Whiles = new List<WhileStatement>();
            public readonly List<DeclareVariableStatement> Declares = new List<DeclareVariableStatement>();
            public readonly List<DeclareCursorStatement> Cursors = new List<DeclareCursorStatement>();
            public readonly List<ProcedureStatementBody> Procedures = new List<ProcedureStatementBody>();
            public readonly List<FunctionStatementBody> Functions = new List<FunctionStatementBody>();
            public readonly List<TriggerStatementBody> Triggers = new List<TriggerStatementBody>();
            public readonly List<CreateTableStatement> CreateTables = new List<CreateTableStatement>();
            public readonly List<TSqlFragment> Subqueries = new List<TSqlFragment>();
            public readonly List<QualifiedJoin> Joins = new List<QualifiedJoin>();
            public readonly List<FromClause> FromClauses = new List<FromClause>();
            public readonly List<GroupByClause> GroupBys = new List<GroupByClause>();
            public readonly List<OrderByClause> OrderBys = new List<OrderByClause>();
            public readonly List<WhereClause> Wheres = new List<WhereClause>();
            public readonly List<BooleanBinaryExpression> BooleanBinaries = new List<BooleanBinaryExpression>();

            // Nodes are recorded before their children are visited, so the lists are ordered from
            // the outermost construct inwards.  Re-anchoring indentation then runs inner-most last,
            // which lets a nested construct win over the body it sits in.
            public override void ExplicitVisit(InsertStatement node) { Inserts.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(UpdateStatement node) { Updates.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(MergeStatement node) { Merges.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(SearchedCaseExpression node) { Cases.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(SimpleCaseExpression node) { Cases.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(BeginEndBlockStatement node) { Blocks.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(IfStatement node) { Ifs.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(WhileStatement node) { Whiles.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(DeclareVariableStatement node) { Declares.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(DeclareCursorStatement node) { Cursors.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateProcedureStatement node) { Procedures.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(AlterProcedureStatement node) { Procedures.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { Procedures.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateFunctionStatement node) { Functions.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(AlterFunctionStatement node) { Functions.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateOrAlterFunctionStatement node) { Functions.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateTriggerStatement node) { Triggers.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(AlterTriggerStatement node) { Triggers.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateOrAlterTriggerStatement node) { Triggers.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(CreateTableStatement node) { CreateTables.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(QualifiedJoin node) { Joins.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(FromClause node) { FromClauses.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(GroupByClause node) { GroupBys.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(OrderByClause node) { OrderBys.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(WhereClause node) { Wheres.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(BooleanBinaryExpression node) { BooleanBinaries.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(ScalarSubquery node) { Subqueries.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(QueryDerivedTable node) { Subqueries.Add(node); base.ExplicitVisit(node); }
            public override void ExplicitVisit(QueryParenthesisExpression node) { Subqueries.Add(node); base.ExplicitVisit(node); }
        }
    }
}
