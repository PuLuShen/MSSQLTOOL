using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MSSQLTool
{
    /// <summary>
    /// Shows non-modal background activity in the native SSMS status bar. Multiple
    /// overlapping operations share one animation and the newest message wins.
    /// </summary>
    internal static class StatusFeedback
    {
        private sealed class Operation : IDisposable
        {
            private int disposed;

            public Operation(string message) { Message = message; }
            public string Message { get; }
            public bool IsDisposed => Volatile.Read(ref disposed) != 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    Schedule(() => Remove(this));
            }
        }

        private static readonly List<Operation> Active = new List<Operation>();
        private static IVsStatusbar statusBar;
        private static string previousText;
        // 0 is the Visual Studio general-purpose status animation (SBAI_General).
        private static object animationIcon = (short)0;

        public static IDisposable Begin(string message)
        {
            var operation = new Operation(message ?? string.Empty);
            Schedule(() => Add(operation));
            return operation;
        }

        private static void Schedule(Action action)
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        action();
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Could not update SSMS status feedback.");
                    }
                });
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not schedule SSMS status feedback.");
            }
        }

        private static void Add(Operation operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (operation.IsDisposed) return;

            if (Active.Count == 0)
            {
                statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
                if (statusBar != null)
                {
                    try { statusBar.GetText(out previousText); } catch { previousText = null; }
                    statusBar.Animation(1, ref animationIcon);
                }
            }

            Active.Add(operation);
            statusBar?.SetText(operation.Message);
        }

        private static void Remove(Operation operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Active.Remove(operation)) return;

            if (Active.Count > 0)
            {
                statusBar?.SetText(Active[Active.Count - 1].Message);
                return;
            }

            if (statusBar != null)
            {
                statusBar.Animation(0, ref animationIcon);
                statusBar.SetText(previousText ?? string.Empty);
            }
            statusBar = null;
            previousText = null;
        }
    }
}
