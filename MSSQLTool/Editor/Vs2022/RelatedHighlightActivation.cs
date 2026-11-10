using System.Threading;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// Tells the completion overlay whether the editor-native highlight is drawing instead of the
    /// window fallback.
    /// </summary>
    internal static class RelatedHighlightActivation
    {
        private static int activeViews;

        public static bool IsAdornmentActive => Volatile.Read(ref activeViews) > 0;

        internal static void Register() => Interlocked.Increment(ref activeViews);

        internal static void Unregister() => Interlocked.Decrement(ref activeViews);
    }
}
