using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MSSQLTool;
using MSSQLTool.Completion;

namespace MSSQLTool.RegressionTests
{
    /// <summary>
    /// Behaviour of the completion list's highlight: it always starts on the first row, and the
    /// order of the list never depends on what was inserted before (the usage-based ranking and the
    /// "remembered highlight" were removed on request).
    /// </summary>
    internal static partial class Program
    {
        internal static void RunCompletionSelectionTests()
        {
            Run("Completion: highlight starts on the first row", TestCompletionFirstRow);
            Run("Completion: usage ranking is gone", TestCompletionLearningRemoved);
            Run("Completion: list order is deterministic", TestCompletionOrderIsStable);
        }

        private static void TestCompletionFirstRow()
        {
            // Empty list: nothing to select.
            Equal(-1, CompletionPresenter.InitialSelectionIndex(0));
            // Any non-empty list selects its first row, whatever the popup did before.
            Equal(0, CompletionPresenter.InitialSelectionIndex(1));
            Equal(0, CompletionPresenter.InitialSelectionIndex(25));
        }

        private static void TestCompletionLearningRemoved()
        {
            // The usage store and its setting must be gone, not merely unused.
            Type store = typeof(CompletionPresenter).Assembly.GetType("MSSQLTool.Completion.CompletionUsageStore", false);
            True(store == null);

            FieldInfo learning = typeof(SettingsManager.SqlCompletionSettings).GetField("learnFromUsage");
            True(learning == null);

            // The presenter no longer remembers a highlighted row between popups.
            True(typeof(CompletionPresenter).GetMethod("PromoteRememberedItem", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) == null);
            True(typeof(CompletionPresenter).GetMethod("IsSameCompletionSession", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) == null);
            True(typeof(CompletionPresenter).GetMethod("NotifyCommitted", BindingFlags.Instance | BindingFlags.Public) == null);
        }

        private static void TestCompletionOrderIsStable()
        {
            // The same statement always produces the same candidate order, so nothing is re-ranked
            // behind the user's back while typing.
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT Na", 9);
            List<string> first = SqlCompletionAnalyzer.BuildItems(context, Metadata()).Select(i => i.DisplayText).ToList();
            List<string> second = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT Na", 9), Metadata())
                .Select(i => i.DisplayText).ToList();

            True(first.Count > 0);
            Equal(string.Join("|", first), string.Join("|", second));
        }
    }
}
