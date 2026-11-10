using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MSSQLTool
{
    /// <summary>Result of one SQL Prompt import, including the cancellation case.</summary>
    internal sealed class QueryTextImportOutcome
    {
        public bool Success;
        public bool Cancelled;
        public int Imported;
        public int Skipped;
        public string Error = string.Empty;
    }

    /// <summary>
    /// Modal progress window for the SQL Prompt import.  Importing a large history used to run on
    /// the UI thread with no feedback, which looked like a hang; this window runs it on a worker,
    /// reports the table/row counters and offers cancel.
    /// </summary>
    internal sealed class ImportProgressWindow : Window
    {
        private readonly ProgressBar bar = new ProgressBar
        {
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Margin = new Thickness(0, 0, 0, 10)
        };
        private readonly TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        private readonly Button cancelButton = new Button
        {
            Content = LocalizationManager.T("Cancel"),
            Width = 92,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Func<IProgress<QueryTextHistoryStore.QueryTextImportProgress>, CancellationToken, QueryTextImportOutcome> work;
        private Action<QueryTextImportOutcome> completed;
        private bool finished;

        public ImportProgressWindow()
        {
            Title = LocalizationManager.T("Import SQL Prompt history...");
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = (Brush)Application.Current?.TryFindResource("ToolThemeBackgroundBrush") ?? SystemColors.WindowBrush;

            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Reading the SQL Prompt history database..."),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(bar);
            panel.Children.Add(status);
            panel.Children.Add(cancelButton);
            Content = panel;

            cancelButton.Click += (_, __) =>
            {
                cancelButton.IsEnabled = false;
                status.Text = LocalizationManager.T("Cancelling...");
                try { cancellation.Cancel(); } catch (Exception) { }
            };

            Loaded += (_, __) => StartImport();
            Closed += (_, __) =>
            {
                try { cancellation.Cancel(); } catch (Exception) { }
            };
        }

        public void Start(
            Func<IProgress<QueryTextHistoryStore.QueryTextImportProgress>, CancellationToken, QueryTextImportOutcome> importWork,
            Action<QueryTextImportOutcome> onCompleted)
        {
            work = importWork;
            completed = onCompleted;
        }

        private void StartImport()
        {
            if (work == null)
            {
                Close();
                return;
            }

            IProgress<QueryTextHistoryStore.QueryTextImportProgress> progress = new Progress<QueryTextHistoryStore.QueryTextImportProgress>(UpdateProgress);

            Task.Run(() =>
            {
                QueryTextImportOutcome result;
                try
                {
                    result = work(progress, cancellation.Token) ?? new QueryTextImportOutcome();
                }
                catch (OperationCanceledException)
                {
                    result = new QueryTextImportOutcome { Cancelled = true };
                }
                catch (Exception ex)
                {
                    result = new QueryTextImportOutcome { Error = ex.Message };
                }

                Dispatcher.BeginInvoke(new Action(() => Finish(result)));
            });
        }

        private void UpdateProgress(QueryTextHistoryStore.QueryTextImportProgress state)
        {
            if (finished || state == null) return;

            bar.IsIndeterminate = false;
            bar.Value = state.Percent;
            status.Text = LocalizationManager.Format(
                "{0}/{1} table(s) · {2} row(s) read · {3} imported · {4} already existed",
                state.TablesDone, state.TablesTotal, state.RowsScanned, state.Imported, state.Skipped);
        }

        private void Finish(QueryTextImportOutcome result)
        {
            finished = true;
            try { Close(); } catch (Exception) { }
            try { completed?.Invoke(result); } catch (Exception ex) { MSSQLToolPackage._logger?.Error(ex, "Import completion handler failed."); }
        }
    }
}
