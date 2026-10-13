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
            private string message;

            public Operation(string message) { this.message = message; }
            public string Message => Volatile.Read(ref message);
            public bool IsDisposed => Volatile.Read(ref disposed) != 0;

            /// <summary>Replaces the text of a running operation, so long loads can report progress.</summary>
            public void Update(string text)
            {
                if (string.IsNullOrWhiteSpace(text) || IsDisposed) return;
                if (string.Equals(Message, text, StringComparison.Ordinal)) return;
                Volatile.Write(ref message, text);
                Schedule(() => SetText(this, text));
            }

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

        /// <summary>Shows a new text for the operation that is currently on screen.</summary>
        private static void SetText(Operation operation, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (operation.IsDisposed || Active.Count == 0) return;
            // Only the newest operation owns the status bar; an older one must not overwrite it.
            if (!ReferenceEquals(Active[Active.Count - 1], operation)) return;
            statusBar?.SetText(text);
        }

        /// <summary>
        /// Reports progress of the newest running operation.  Long loads call this as they move through
        /// their steps so the status bar shows what is happening instead of one static sentence.
        /// </summary>
        public static void Update(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Operation operation;
            lock (Active)
            {
                if (Active.Count == 0) return;
                operation = Active[Active.Count - 1];
            }

            operation.Update(message);
        }
    }
}
