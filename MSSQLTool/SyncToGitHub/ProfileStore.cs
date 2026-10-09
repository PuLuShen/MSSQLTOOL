using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

public static class ProfileStore
{
    /// <summary>Follows the configured data folder; resolved on every access.</summary>
    private static string Path => MSSQLTool.AppPaths.GitHubProfilesFile;

    public static List<GitHubSyncProfile> Load()
    {
        string path = Path;
        try
        {
            if (!File.Exists(path)) return new List<GitHubSyncProfile>();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<GitHubSyncProfile>>(json);
        }
        catch { return new List<GitHubSyncProfile>(); }
    }

    public static void Save(IEnumerable<GitHubSyncProfile> profiles)
    {
        string path = Path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
    }
}
