using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    public class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
    {
        public override string Name => Constants.PluginName;
        public override string Description => "Anime metadata for TV series and movies from AniList, AniDB, MAL, TVDB, Fanart.tv and Fribb mappings. Works with Jellyfin 12.2. Jellyfin 12 fork of webbster64's Anime Multi Source.";
        public override Guid Id => Guid.Parse(Constants.PluginGuid);

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            PluginLog.Directory = applicationPaths.LogDirectoryPath;
            logger = PluginLog.Wrap(logger);
            CleanupStaleVersionFolders(applicationPaths, logger);
        }

        // Jellyfin's plugin updater installs a new version alongside the old one instead of
        // replacing it (https://github.com/jellyfin/jellyfin/issues/12959 - affects other plugins
        // too, not specific to this one). If both stick around, a later restart can load both
        // assemblies at once, which breaks config load/save (see GetConfigurationSafe) because
        // .NET treats PluginConfiguration from the two assemblies as different types despite the
        // identical name. We're already running by the time this constructor executes, so any
        // *other* folder here that carries our own name and our own dll is provably a stale
        // leftover - remove it now so the next restart never hits the dual-load bug at all,
        // instead of requiring a manual SSH cleanup after every update.
        private void CleanupStaleVersionFolders(IApplicationPaths applicationPaths, ILogger logger)
        {
            try
            {
                var pluginsPath = applicationPaths.PluginsPath;
                var ownDirectory = Path.GetDirectoryName(AssemblyFilePath);
                if (string.IsNullOrWhiteSpace(pluginsPath) || !Directory.Exists(pluginsPath) || string.IsNullOrWhiteSpace(ownDirectory))
                {
                    return;
                }

                var ownDllName = Path.GetFileName(AssemblyFilePath);

                foreach (var directory in Directory.GetDirectories(pluginsPath))
                {
                    if (string.Equals(directory, ownDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(Path.Combine(directory, ownDllName)))
                    {
                        // Doesn't look like a version of this same plugin; leave it alone.
                        continue;
                    }

                    var metaPath = Path.Combine(directory, "meta.json");
                    if (!IsOlderVersionFolder(Path.GetFileName(directory), File.Exists(metaPath) ? File.ReadAllText(metaPath) : null, Version))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(directory, recursive: true);
                        logger.LogInformation("Removed stale plugin version folder {Directory} left behind by a previous update.", directory);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Found a stale plugin version folder at {Directory} but could not remove it automatically; delete it manually to avoid config/duplicate-provider issues.", directory);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check for stale plugin version folders.");
            }
        }

        // Catalog installs name the folder after the manifest ("Anime Multi Source_1.0.8.0"), manual ones often
        // "AnimeMultiSource_1.0.8.0". Only a strictly older version is stale: when two versions load at once,
        // the old one must not delete the update that was just installed.
        public static bool IsOlderVersionFolder(string folderName, string? metaJson, Version ownVersion)
        {
            var underscore = folderName.LastIndexOf('_');
            if (underscore < 0 || !string.Equals(folderName[..underscore].Replace(" ", string.Empty, StringComparison.Ordinal), Constants.PluginName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? versionText = folderName[(underscore + 1)..];
            if (metaJson != null)
            {
                try
                {
                    using var meta = System.Text.Json.JsonDocument.Parse(metaJson);
                    if (meta.RootElement.TryGetProperty("version", out var v))
                    {
                        versionText = v.GetString();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Fall back to the version in the folder name.
                }
            }

            return Version.TryParse(versionText, out var version) && version < ownVersion;
        }

        public static Plugin? Instance { get; private set; }

        public static Configuration.PluginConfiguration GetConfigurationSafe(ILogger logger)
        {
            if (Instance == null)
            {
                return new Configuration.PluginConfiguration();
            }

            try
            {
                return Instance.Configuration;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load plugin configuration via the cached instance; attempting to recover it directly from disk.");
                return RecoverConfigurationFromDisk(logger) ?? new Configuration.PluginConfiguration();
            }
        }

        // BasePlugin<T>.Configuration throws InvalidCastException when a stale duplicate plugin
        // folder from a previous update is still sitting in the plugins directory alongside the
        // current one (a known upstream Jellyfin bug - old version folders aren't cleaned up on
        // update: https://github.com/jellyfin/jellyfin/issues/12959). When that happens, .NET
        // treats PluginConfiguration loaded from that other assembly as a different type even
        // though the name matches, so the cached Configuration instance silently ends up holding
        // the wrong type. The fix above (catch and default) was losing every setting on every
        // such conflict. Re-deserializing the XML file directly, against our own local type
        // reference instead of whatever type the cache is holding, recovers the user's actual
        // saved settings instead.
        private static Configuration.PluginConfiguration? RecoverConfigurationFromDisk(ILogger logger)
        {
            try
            {
                var path = Instance!.ConfigurationFilePath;
                if (!System.IO.File.Exists(path))
                {
                    return null;
                }

                var recovered = Instance.XmlSerializer.DeserializeFromFile(typeof(Configuration.PluginConfiguration), path) as Configuration.PluginConfiguration;
                if (recovered != null)
                {
                    logger.LogWarning(
                        "Recovered plugin configuration directly from {Path} after a duplicate-plugin-version conflict. " +
                        "To stop this happening on every update, delete any old \"Anime Multi Source_<version>\" folders " +
                        "under your Jellyfin plugins directory, leaving only the current version.",
                        path);
                }

                return recovered;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Direct configuration file recovery also failed; falling back to defaults.");
                return null;
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Constants.PluginName,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                    // Listed under "Plugins" in the dashboard's left sidebar.
                    EnableInMainMenu = true,
                    DisplayName = "Anime Multi Source",
                    MenuIcon = "animation"
                }
            };
        }
    }
}
