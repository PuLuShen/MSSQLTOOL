using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace MSSQLTool
{
    /// <summary>
    /// A key/value store for the extension's settings.  Values are always strings; the typed
    /// wrappers live in <see cref="SettingsManager"/>.
    /// </summary>
    public interface ISettingsStore
    {
        string Read(string name);
        bool Write(string name, string value);
        bool Remove(string name);
        IDictionary<string, string> Snapshot();
    }

    /// <summary>Settings in <c>HKCU\Software\MSSQLTool\Settings</c>, the historical location.</summary>
    internal sealed class RegistrySettingsStore : ISettingsStore
    {
        public const string SubKeyPath = @"MSSQLTool\Settings";

        public string Read(string name)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, false))
            {
                return key?.GetValue(name)?.ToString() ?? string.Empty;
            }
        }

        public bool Write(string name, string value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SubKeyPath))
            {
                if (key == null) return false;
                if (value == null) key.DeleteValue(name, false);
                else key.SetValue(name, value, RegistryValueKind.String);
                return true;
            }
        }

        public bool Remove(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, true))
                {
                    key?.DeleteValue(name, false);
                }

                return true;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "A stored setting could not be removed", ex);
                return false;
            }
        }

        public IDictionary<string, string> Snapshot()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, false))
                {
                    if (key == null) return values;
                    foreach (string name in key.GetValueNames())
                    {
                        object value = key.GetValue(name);
                        if (value != null) values[name] = Convert.ToString(value);
                    }
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The stored settings could not be read", ex);
            }

            return values;
        }
    }

    /// <summary>
    /// Settings in a JSON file next to the other data, used once a custom data folder is
    /// configured.  Writes are atomic so a crash can never leave a truncated settings file behind.
    /// </summary>
    internal sealed class SettingsFileStore : ISettingsStore
    {
        private readonly string path;
        private readonly Func<IDictionary<string, string>> seedProvider;
        private readonly object gate = new object();
        private Dictionary<string, string> values;
        private DateTime loadedStampUtc;

        /// <param name="path">The settings file to use.</param>
        /// <param name="seedProvider">
        /// Supplies the values copied into the file the first time it is created, so switching to a
        /// file-backed configuration never loses the settings that were in the registry.
        /// </param>
        public SettingsFileStore(string path, Func<IDictionary<string, string>> seedProvider)
        {
            this.path = path;
            this.seedProvider = seedProvider;
        }

        public string Path => path;

        public string Read(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            lock (gate)
            {
                return Load().TryGetValue(name, out string value) ? value ?? string.Empty : string.Empty;
            }
        }

        public bool Write(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (gate)
            {
                Dictionary<string, string> current = ReloadIfChangedExternally();
                if (value == null) current.Remove(name);
                else current[name] = value;
                return Save(current);
            }
        }

        public bool Remove(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (gate)
            {
                Dictionary<string, string> current = ReloadIfChangedExternally();
                if (!current.Remove(name)) return true;
                return Save(current);
            }
        }

        public IDictionary<string, string> Snapshot()
        {
            lock (gate)
            {
                return new Dictionary<string, string>(Load(), StringComparer.Ordinal);
            }
        }

        /// <summary>Forgets the in-memory copy; the next read reloads the file.</summary>
        public void Invalidate()
        {
            lock (gate)
            {
                values = null;
            }
        }

        /// <summary>
        /// Picks up a file another SSMS instance (or the user) changed, so a write merges into the
        /// latest content instead of overwriting it with a stale copy.
        /// </summary>
        private Dictionary<string, string> ReloadIfChangedExternally()
        {
            if (values == null) return Load();

            try
            {
                if (!File.Exists(path))
                {
                    values = null;
                    return Load();
                }

                if (File.GetLastWriteTimeUtc(path) == loadedStampUtc) return values;
            }
            catch (Exception)
            {
                return values;
            }

            values = null;
            return Load();
        }

        private Dictionary<string, string> Load()
        {
            if (values != null) return values;

            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        loadedStampUtc = File.GetLastWriteTimeUtc(path);
                        return values = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                    }
                }
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The settings file could not be read; the registry copy is used instead", ex);
            }

            // First use, or an unreadable file: start from the registry so nothing is lost.
            var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
            IDictionary<string, string> source = null;
            try
            {
                source = seedProvider?.Invoke();
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The registry settings could not be read while seeding the settings file", ex);
            }

            if (source != null)
                foreach (var pair in source)
                    seeded[pair.Key] = pair.Value;

            values = seeded;
            Save(seeded);
            return values;
        }

        private bool Save(Dictionary<string, string> snapshot)
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                loadedStampUtc = File.GetLastWriteTimeUtc(path);
                return true;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The settings file could not be saved", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Picks the settings store: the registry by default, or a file inside the data folder once one
    /// is configured.  Every write is mirrored into the registry, so removing the custom folder
    /// again cannot lose the configuration.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly object Gate = new object();
        private static readonly RegistrySettingsStore Registry = new RegistrySettingsStore();
        private static SettingsFileStore fileStore;
        private static string fileStorePath;

        /// <summary>True when the settings live in the data folder instead of the registry.</summary>
        public static bool IsFileBacked => AppPaths.IsCustomRoot;

        /// <summary>The file holding the settings, or null while the registry is the store.</summary>
        public static string FilePath => IsFileBacked ? AppPaths.SettingsFile : null;

        public static string Read(string name)
        {
            if (!IsFileBacked) return SafeRegistryRead(name);

            string value = CurrentFileStore().Read(name);
            // A value that only exists in the registry still wins over a missing one, so a setting
            // written before the folder was configured keeps working.
            return value ?? SafeRegistryRead(name);
        }

        public static bool Write(string name, string value)
        {
            bool saved = true;
            if (IsFileBacked) saved = CurrentFileStore().Write(name, value);

            // The registry always keeps a copy: it is the fallback store and it is what the
            // extension reads when the configured folder is unavailable.
            try
            {
                Registry.Write(name, value);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The registry copy of a setting could not be written", ex);
                saved = false;
            }

            return saved;
        }

        public static IDictionary<string, string> Snapshot()
            => IsFileBacked ? CurrentFileStore().Snapshot() : Registry.Snapshot();

        /// <summary>Deletes a setting from every store that holds it.</summary>
        public static bool Remove(string name)
        {
            bool removed = true;
            if (IsFileBacked) removed = CurrentFileStore().Remove(name);

            try
            {
                Registry.Remove(name);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "The registry copy of a setting could not be removed", ex);
                removed = false;
            }

            return removed;
        }

        /// <summary>Re-resolves the store after the data folder changed.</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                fileStore = null;
                fileStorePath = null;
            }
        }

        /// <summary>Reads straight from the registry, used for the pointer that chooses the store.</summary>
        public static string ReadRegistryValue(string name) => SafeRegistryRead(name);

        private static SettingsFileStore CurrentFileStore()
        {
            string path = AppPaths.SettingsFile;
            lock (Gate)
            {
                if (fileStore != null && string.Equals(fileStorePath, path, StringComparison.OrdinalIgnoreCase))
                    return fileStore;

                fileStorePath = path;
                return fileStore = new SettingsFileStore(path, () => Registry.Snapshot());
            }
        }

        private static string SafeRegistryRead(string name)
        {
            try
            {
                return Registry.Read(name);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Storage", "A stored setting could not be read", ex);
                return string.Empty;
            }
        }
    }
}
