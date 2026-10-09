using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace MSSQLTool
{
    [Guid("6df34681-5e35-4f4b-80fd-bcbdfdca7f8a")]
    public sealed class QueryTemplateWindow : ToolWindowPane
    {
        public QueryTemplateWindow() : base(null)
        {
            Caption = LocalizationManager.T("Query Template Picker");
            Content = new QueryTemplateWindowControl();
        }
    }
}
