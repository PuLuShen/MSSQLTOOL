using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace MSSQLTool
{
    internal sealed class EditorEditTransaction : IDisposable
    {
        private readonly IVsCompoundAction action;
        private bool completed;

        private EditorEditTransaction(IVsCompoundAction action) { this.action = action; }

        public static EditorEditTransaction Begin(IVsTextLines lines, string description)
        {
            var compound = lines as IVsCompoundAction;
            if (compound == null || compound.OpenCompoundAction(description ?? "MSSQL Tool edit") != 0)
                return new EditorEditTransaction(null);
            return new EditorEditTransaction(compound);
        }

        public void Complete()
        {
            if (completed) return;
            completed = true;
            action?.CloseCompoundAction();
        }

        public void Dispose()
        {
            if (!completed) action?.AbortCompoundAction();
        }
    }
}
