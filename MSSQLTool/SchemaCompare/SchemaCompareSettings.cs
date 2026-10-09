// Schema Compare - remembers the last used source and target.
// The values are stored under HKCU\MSSQLTool\Settings next to the other
// extension settings. SettingsManager is not modified on purpose; the keys are new
// and only this feature reads or writes them.

using System;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace MSSQLTool.SchemaCompare
{
    internal static class SchemaCompareSettings
    {
        private const string SubKeyPath = @"MSSQLTool\Settings";
        private const string LastSourceValueName = "SchemaCompareLastSource";
        private const string LastTargetValueName = "SchemaCompareLastTarget";

        public sealed class Endpoint
        {
            public string Server { get; set; }
            public string Database { get; set; }
        }

        public static Endpoint GetLastSource()
        {
            return Parse(ReadValue(LastSourceValueName));
        }

        public static Endpoint GetLastTarget()
        {
            return Parse(ReadValue(LastTargetValueName));
        }

        public static void SaveLastSource(string server, string database)
        {
            WriteValue(LastSourceValueName, Serialize(server, database));
        }

        public static void SaveLastTarget(string server, string database)
        {
            WriteValue(LastTargetValueName, Serialize(server, database));
        }

        private static string Serialize(string server, string database)
        {
            var endpoint = new Endpoint { Server = server ?? string.Empty, Database = database ?? string.Empty };
            return JsonConvert.SerializeObject(endpoint);
        }

        private static Endpoint Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<Endpoint>(value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadValue(string name)
        {
            try
            {
                return SettingsStore.Read(name);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The stored " + name + " value could not be read", ex);
                return null;
            }
        }

        private static void WriteValue(string name, string value)
        {
            try
            {
                SettingsStore.Write(name, value ?? string.Empty);
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Schema Compare", "The " + name + " value could not be saved", ex);
            }
        }
    }
}
