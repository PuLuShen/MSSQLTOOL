// Copyright (C) 2006-2010 Jim Tilander. See COPYING for and README for more details.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Reflection;
using EnvDTE;
using EnvDTE80;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using MSSQLTool;
using System.Windows.Input;

namespace Aurora
{
    class CommandProcessor : CommandBase
    {
        public CommandProcessor(Plugin plugin, string canonicalName, string buttonText, string toolTip)
            : base(buttonText, canonicalName, plugin, toolTip)
        {
        }

        public AsyncPackage package;
        public string FullFileName;

        override public int ScriptIconIndex { get { return 0; } }

        public override bool OnCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();


            try
            {

                bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                QueryTemplateInsertionService.InsertFile(FullFileName, isShiftPressed);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(
                    LocalizationManager.Format("Could not insert the query template: {0}", ex.Message),
                    "Query Templates",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }

            return true;

        }


        public override bool IsEnabled()
        {
            return true;
        }
    }

}
