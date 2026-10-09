using System;
using System.Collections.Generic;
using MSSQLTool.Completion;

namespace MSSQLTool.RegressionTests
{
    /// <summary>
    /// Regression tests for the completion popup's selection session.  Selecting a value without
    /// inserting it must not keep that row selected in the next popup: the value moves to the top of
    /// the list and the selection starts on the first row.
    /// </summary>
    internal static partial class Program
    {
        internal static void RunCompletionSelectionTests()
        {
            Run("Completion: closed popup starts a new session", TestCompletionNewSessionWhenHidden);
            Run("Completion: typing keeps the same session", TestCompletionTypingKeepsSession);
            Run("Completion: another line starts a new session", TestCompletionOtherLineNewSession);
            Run("Completion: re-trigger starts a new session", TestCompletionRetriggerNewSession);
            Run("Completion: another word starts a new session", TestCompletionOtherWordNewSession);
            Run("Completion: remembered item moves to the front", TestCompletionPromotesRememberedItem);
            Run("Completion: missing remembered item is ignored", TestCompletionMissingRememberedItem);
            Run("Completion: selection defaults to the first row", TestCompletionSelectionDefault);
            Run("Completion: session plan promotes and selects first", TestCompletionSessionPlan);
        }

        private static CompletionContext Context(int line, int column, int replacementStart)
            => new CompletionContext { CaretLine = line, CaretColumn = column, ReplacementStartColumn = replacementStart };

        private static List<CompletionItem> Items(params string[] names)
        {
            var items = new List<CompletionItem>();
            foreach (string name in names)
                items.Add(new CompletionItem { DisplayText = name, Kind = CompletionItemKind.Column });
            return items;
        }

        private static void TestCompletionNewSessionWhenHidden()
        {
            // The user dismissed the popup: nothing of the previous highlight may survive.
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(0, 9, 7), false));
        }

        private static void TestCompletionTypingKeepsSession()
        {
            True(CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(0, 9, 7), true));
            True(CompletionPresenter.IsSameCompletionSession(Context(4, 20, 15), Context(4, 16, 15), true));
        }

        private static void TestCompletionOtherLineNewSession()
        {
            // The popup can still be visible for a moment after the caret jumped elsewhere.
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(1, 8, 7), true));
        }

        private static void TestCompletionRetriggerNewSession()
        {
            // Another request at the very same caret is an explicit re-trigger.
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(0, 8, 7), true));
        }

        private static void TestCompletionOtherWordNewSession()
        {
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(0, 20, 19), true));
            // A caret that moved in front of the word it was completing is a new session too.
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(0, 5, 7), true));
        }

        private static void TestCompletionPromotesRememberedItem()
        {
            List<CompletionItem> items = Items("Alpha", "Beta", "Gamma");
            Equal(0, CompletionPresenter.PromoteRememberedItem(items, "Column|Gamma"));
            Equal("Gamma", items[0].DisplayText);
            Equal("Alpha", items[1].DisplayText);
            Equal("Beta", items[2].DisplayText);
            Equal(3, items.Count);

            // An item that is already first stays there.
            Equal(0, CompletionPresenter.PromoteRememberedItem(items, "Column|Gamma"));
            Equal("Gamma", items[0].DisplayText);
        }

        private static void TestCompletionMissingRememberedItem()
        {
            List<CompletionItem> items = Items("Alpha", "Beta");
            Equal(-1, CompletionPresenter.PromoteRememberedItem(items, "Column|Delta"));
            Equal("Alpha", items[0].DisplayText);
            Equal(-1, CompletionPresenter.PromoteRememberedItem(items, null));
            Equal(-1, CompletionPresenter.PromoteRememberedItem(new List<CompletionItem>(), "Column|Alpha"));
        }

        private static void TestCompletionSelectionDefault()
        {
            Equal(-1, CompletionPresenter.ResolveSelectedIndex(0, -1));
            Equal(0, CompletionPresenter.ResolveSelectedIndex(5, -1));
            Equal(2, CompletionPresenter.ResolveSelectedIndex(5, 2));
            // A restore match that no longer exists falls back to the first row.
            Equal(0, CompletionPresenter.ResolveSelectedIndex(5, 9));
            Equal(-1, CompletionPresenter.ResolveSelectedIndex(0, 3));
        }

        private static void TestCompletionSessionPlan()
        {
            // Session one: the user highlights "Gamma" without inserting it.
            string remembered = "Column|Gamma";
            List<CompletionItem> next = Items("Alpha", "Beta", "Gamma");

            // Session two somewhere else: the value is promoted and the first row is selected.
            True(!CompletionPresenter.IsSameCompletionSession(Context(0, 8, 7), Context(3, 12, 11), true));
            Equal(0, CompletionPresenter.PromoteRememberedItem(next, remembered));
            Equal("Gamma", next[0].DisplayText);
            Equal(0, CompletionPresenter.ResolveSelectedIndex(next.Count, -1));

            // A continuing session keeps the user's row instead.
            Equal(2, CompletionPresenter.ResolveSelectedIndex(next.Count, 2));
        }
    }
}
