using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace MSSQLTool.Editor.Vs2022
{
    /// <summary>
    /// A buffer-scoped listener for code-analysis setting changes.
    /// </summary>
    internal interface IAnalysisSettingsListener
    {
        void OnAnalysisSettingsChanged();
    }

    /// <summary>
    /// One timer for the whole shell instead of one timer per open buffer.
    ///
    /// Every <see cref="SqlDiagnosticsTagger"/> used to start its own 1 second
    /// <see cref="DispatcherTimer"/> in the constructor and stop it only from Dispose.  Buffers are
    /// kept alive by the editor for undo and by their properties, so the timers outlived the tabs
    /// that created them and the count kept growing with every query window opened during a
    /// session.  The watcher polls the settings version once per second, keeps only weak references
    /// to its listeners and stops entirely once the last one is gone, so a tagger that is never
    /// disposed costs nothing but the reference in its own buffer.
    /// </summary>
    internal static class AnalysisSettingsWatcher
    {
        private static readonly object Sync = new object();

        private static readonly List<WeakReference<IAnalysisSettingsListener>> Listeners =
            new List<WeakReference<IAnalysisSettingsListener>>();

        private static DispatcherTimer timer;
        private static int version = SettingsManager.AnalysisSettingsVersion;

        public static void Register(IAnalysisSettingsListener listener)
        {
            if (listener == null) return;

            lock (Sync)
            {
                Prune();
                for (int i = 0; i < Listeners.Count; i++)
                {
                    IAnalysisSettingsListener existing;
                    if (Listeners[i].TryGetTarget(out existing) && ReferenceEquals(existing, listener)) return;
                }

                Listeners.Add(new WeakReference<IAnalysisSettingsListener>(listener));
                version = SettingsManager.AnalysisSettingsVersion;
                EnsureTimer();
            }
        }

        public static void Unregister(IAnalysisSettingsListener listener)
        {
            if (listener == null) return;

            lock (Sync)
            {
                for (int i = Listeners.Count - 1; i >= 0; i--)
                {
                    IAnalysisSettingsListener existing;
                    if (!Listeners[i].TryGetTarget(out existing) || ReferenceEquals(existing, listener))
                        Listeners.RemoveAt(i);
                }

                if (Listeners.Count == 0) StopTimer();
            }
        }

        private static void EnsureTimer()
        {
            if (timer != null) return;

            // Register runs on the UI thread from the tagger constructor, which is where the
            // per-buffer timer used to be created.
            timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += OnTick;
            timer.Start();
        }

        private static void StopTimer()
        {
            if (timer == null) return;
            timer.Stop();
            timer.Tick -= OnTick;
            timer = null;
        }

        private static void OnTick(object sender, EventArgs e)
        {
            List<IAnalysisSettingsListener> snapshot;
            bool changed;

            lock (Sync)
            {
                Prune();
                if (Listeners.Count == 0)
                {
                    StopTimer();
                    return;
                }

                int current = SettingsManager.AnalysisSettingsVersion;
                changed = current != version;
                if (changed) version = current;
                snapshot = changed ? Snapshot() : null;
            }

            if (!changed || snapshot == null) return;

            foreach (IAnalysisSettingsListener listener in snapshot)
            {
                try
                {
                    listener.OnAnalysisSettingsChanged();
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("SQL Diagnostics", "The analysis settings listener failed", ex);
                }
            }
        }

        /// <summary>Caller holds <see cref="Sync"/>.</summary>
        private static void Prune()
        {
            for (int i = Listeners.Count - 1; i >= 0; i--)
            {
                IAnalysisSettingsListener target;
                if (!Listeners[i].TryGetTarget(out target)) Listeners.RemoveAt(i);
            }
        }

        /// <summary>Caller holds <see cref="Sync"/>.</summary>
        private static List<IAnalysisSettingsListener> Snapshot()
        {
            var result = new List<IAnalysisSettingsListener>(Listeners.Count);
            foreach (WeakReference<IAnalysisSettingsListener> reference in Listeners)
            {
                IAnalysisSettingsListener target;
                if (reference.TryGetTarget(out target)) result.Add(target);
            }
            return result;
        }
    }
}
