using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.Shell;

namespace MSSQLTool
{
    /// <summary>
    /// SSMS names auto-recovered tabs SQLQuery1.sql, SQLQuery2.sql, ... inside %TEMP% and then
    /// New Query tries to create the same names again, failing with STG_E_FILEREADYEXISTS.
    ///
    /// This moves those temporary documents/files to a plugin-owned folder with unique names, so
    /// the native New Query can allocate SQLQuery1.sql again.  Only %TEMP%\SQLQuery*.sql* files
    /// are touched; real files are never moved.
    /// </summary>
    internal static class RecoveredTempFileService
    {
        private static readonly object SyncRoot = new object();
        private static DispatcherTimer timer;
        private static int passes;
        private static int rearranging;

        public static void Schedule()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // First pass: move leftovers that no document owns yet (the shell has not opened tabs
            // at package startup).  Then two delayed passes handle the documents AutoRecover opens.
            TryMoveStaleTempFiles();

            if (timer != null) return;
            passes = 0;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, __) =>
            {
                try
                {
                    TryMoveStaleTempFiles();
                    RearrangeOpenDocuments();
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not rearrange the recovered query files.");
                }

                passes++;
                if (passes >= 3)
                {
                    timer.Stop();
                    timer = null;
                }
            };
            timer.Start();
        }

        /// <summary>Moves %TEMP%\SQLQuery*.sql* files that no open document owns.</summary>
        public static int TryMoveStaleTempFiles()
        {
            int moved = 0;
            try
            {
                string temp = TrimSeparator(Path.GetTempPath());
                if (!Directory.Exists(temp)) return 0;

                HashSet<string> open = OpenDocumentPaths();
                string target = TargetFolder;
                Directory.CreateDirectory(target);

                foreach (string file in Directory.EnumerateFiles(temp, "SQLQuery*.sql*"))
                {
                    try
                    {
                        if (open.Contains(file)) continue;
                        string destination = UniquePath(target, Path.GetFileName(file));
                        File.Move(file, destination);
                        moved++;
                        MSSQLToolPackage._logger?.Info("Moved a recovery-temp query file: " + file + " -> " + destination);
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Could not move recovery-temp query file: " + file);
                    }
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not scan the recovery-temp query files.");
            }

            return moved;
        }

        /// <summary>SaveAs each open document whose path is a %TEMP%\SQLQuery*.sql* path.</summary>
        public static int RearrangeOpenDocuments()
        {
            if (Interlocked.Exchange(ref rearranging, 1) == 1) return 0;

            int moved = 0;
            try
            {
                var dte = ServiceCache.ExtensibilityModel as DTE2 ?? Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte?.Documents == null) return 0;

                string temp = TrimSeparator(Path.GetTempPath());
                string target = TargetFolder;
                Directory.CreateDirectory(target);

                foreach (Document document in dte.Documents)
                {
                    try
                    {
                        string oldPath = document.FullName;
                        if (!IsTempQueryPath(oldPath, temp)) continue;
                        if (!File.Exists(oldPath)) continue;

                        string newPath = UniquePath(target, Path.GetFileName(oldPath));
                        document.Save(newPath);
                        if (!File.Exists(newPath)) continue;

                        moved++;
                        MSSQLToolPackage._logger?.Info("Moved an open recovered query to: " + newPath);

                        // The document now points at newPath.  Remove the old temp name so SSMS can
                        // allocate SQLQuery1.sql / SQLQuery2.sql again for New Query.
                        TryDelete(oldPath);
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Debug(ex, "Could not move an open recovered query.");
                    }
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not rearrange the open recovered queries.");
            }
            finally
            {
                Interlocked.Exchange(ref rearranging, 0);
            }

            return moved;
        }

        private static HashSet<string> OpenDocumentPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dte = ServiceCache.ExtensibilityModel as DTE2 ?? Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte?.Documents == null) return paths;
                foreach (Document document in dte.Documents)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(document.FullName)) paths.Add(document.FullName);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }

            return paths;
        }

        private static bool IsTempQueryPath(string path, string temp)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string directory = TrimSeparator(Path.GetDirectoryName(path) ?? string.Empty);
                if (!string.Equals(directory, temp, StringComparison.OrdinalIgnoreCase)) return false;

                string name = Path.GetFileName(path) ?? string.Empty;
                return name.StartsWith("SQLQuery", StringComparison.OrdinalIgnoreCase)
                    && name.IndexOf(".sql", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TargetFolder => Path.Combine(AppPaths.Root, "RecoveredQueries");

        private static string UniquePath(string folder, string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            string candidate = Path.Combine(folder, stem + "_" + stamp + extension);
            int suffix = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, stem + "_" + stamp + "_" + suffix++ + extension);
            }

            return candidate;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not delete the old recovery-temp query file: " + path);
            }
        }

        private static string TrimSeparator(string path)
            => (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
