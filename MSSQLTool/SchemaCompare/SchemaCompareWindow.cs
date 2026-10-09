using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    /// <summary>
    /// Tool window that hosts the Schema Compare user control.
    /// </summary>
    [Guid("a3e5c7d9-4b21-4f68-9c0e-7d5a2b8e6f31")]
    public class SchemaCompareWindow : ToolWindowPane
    {
        public SchemaCompareWindow() : base(null)
        {
            this.Caption = LocalizationManager.T("Schema Compare");
            this.Content = new SchemaCompareWindowControl();
        }
    }
}
