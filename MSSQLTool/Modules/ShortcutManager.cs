using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;

namespace MSSQLTool
{
    internal static class ShortcutManager
    {
        private static readonly Guid QueryHistoryCommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");
        private const int QueryHistoryCommandId = 4144;
        private const int ScriptObjectCommandId = 4134;

        public static bool ApplyQueryHistoryShortcut(string shortcut, out string error)
            => ApplyShortcut(QueryHistoryCommandId, "Query History", shortcut, out error);

        public static bool ApplyScriptObjectShortcut(string shortcut, out string error)
        {
            // F12 is declared in the VSCT for the SQL Query Editor scope. Trying
            // to assign Global::F12 through EnvDTE conflicts with SSMS's built-in
            // binding and returns E_INVALIDARG.
            if (IsF12Shortcut(shortcut))
            {
                error = null;
                return true;
            }

            return ApplyShortcut(ScriptObjectCommandId, "Script Object Definition", shortcut, out error);
        }

        internal static bool IsF12Shortcut(string shortcut)
            => string.Equals((shortcut ?? string.Empty).Trim(), "F12", StringComparison.OrdinalIgnoreCase);

        private static bool ApplyShortcut(int commandId, string commandName, string shortcut, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null) throw new InvalidOperationException("SSMS automation service is unavailable.");

                Command target = null;
                foreach (Command command in dte.Commands)
                {
                    if (command.ID == commandId && Guid.TryParse(command.Guid, out Guid commandGuid) && commandGuid == QueryHistoryCommandSet)
                    {
                        target = command;
                        break;
                    }
                }
                if (target == null) throw new InvalidOperationException("The " + commandName + " command is not registered yet.");

                target.Bindings = CreateAutomationBinding(shortcut);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static object CreateAutomationBinding(string shortcut)
        {
            // EnvDTE accepts a scalar string for one shortcut. Passing object[] here
            // is rejected by SSMS with E_INVALIDARG even though the getter exposes a
            // SAFEARRAY when a command has multiple bindings.
            return string.IsNullOrWhiteSpace(shortcut)
                ? (object)new object[0]
                : "Global::" + shortcut.Trim();
        }

    }
}
