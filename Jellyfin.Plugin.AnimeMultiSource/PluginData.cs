using System.IO;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    // Files this plugin keeps between restarts (API caches, the Fribb list, sync state) live in its own
    // data folder. Writes go to a temp file first, so a crash mid-write never leaves a cut-off file behind.
    public static class PluginData
    {
        public static string GetPath(string fileName)
        {
            // Plugin.Instance is only null outside a running server (e.g. unit tests).
            var folder = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "AnimeMultiSource");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, fileName);
        }

        public static void WriteAtomic(string path, string contents)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
    }
}
