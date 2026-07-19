using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace TVSync
{
    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "TVSync.SynchronizedTV";
        public const string PLUGIN_NAME = "TVSync";
        public const string PLUGIN_VERSION = "2.0.0";
    }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        internal static TVSyncConfig SyncConfig { get; private set; }
        private Harmony _harmony;

        public static bool YtDlpReady { get; private set; }
        public static readonly string DataDir = Path.Combine("TVSync");
        public static readonly string LangDir = Path.Combine(DataDir, "lang");
        public static readonly string OtherDir = Path.Combine(DataDir, "other");
        public static readonly string YtDlpPath = Path.Combine(OtherDir, "yt-dlp.exe");
        public static readonly string CachePath = Path.Combine(DataDir, "cache");
        public static readonly string VersionPath = Path.Combine(OtherDir, "yt-dlp.version");
        public static readonly string VideoPath = Path.Combine(OtherDir, "test.mp4");

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            SyncConfig = new TVSyncConfig();
            SyncConfig.Init();

            EnsureDirectories();
            CleanTempFiles();

            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded!");

            // Download/update yt-dlp in background
            new Thread(UpdateYtDlp) { IsBackground = true }.Start();

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(Patches.TVPatches));
            _harmony.PatchAll(typeof(Patches.HUDPatches));
            _harmony.PatchAll(typeof(Patches.NetworkPatches));
        }

        private void EnsureDirectories()
        {
            if (!Directory.Exists(LangDir)) Directory.CreateDirectory(LangDir);
            if (!Directory.Exists(OtherDir)) Directory.CreateDirectory(OtherDir);
        }

        private void CleanTempFiles()
        {
            TryDeleteFile(VideoPath);
            TryDeleteFile(VideoPath + ".part");
            if (!File.Exists(CachePath))
            {
                File.WriteAllText(CachePath, "");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.LogWarning($"Could not delete {path}: {ex.Message}"); }
        }

        private void UpdateYtDlp()
        {
            try
            {
                string latestVersion = GetLatestYtDlpTag();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    Logger.LogWarning("TVSync: Could not determine latest yt-dlp version. Skipping update.");
                    YtDlpReady = File.Exists(YtDlpPath);
                    return;
                }

                bool needsUpdate = true;
                if (File.Exists(YtDlpPath) && File.Exists(VersionPath))
                {
                    string localVersion = File.ReadAllText(VersionPath).Trim();
                    if (localVersion == latestVersion) needsUpdate = false;
                }

                if (needsUpdate)
                {
                    Logger.LogInfo($"TVSync: Downloading yt-dlp version {latestVersion}...");
                    DownloadFileSync(
                        new Uri($"https://github.com/yt-dlp/yt-dlp/releases/download/{latestVersion}/yt-dlp.exe"),
                        YtDlpPath);
                    if (File.Exists(YtDlpPath))
                        File.WriteAllText(VersionPath, latestVersion);
                }

                YtDlpReady = File.Exists(YtDlpPath);
                Logger.LogInfo("TVSync: yt-dlp ready.");
            }
            catch (Exception e)
            {
                Logger.LogError($"TVSync: yt-dlp update failed: {e.Message}");
                YtDlpReady = File.Exists(YtDlpPath);
            }
        }

        private string GetLatestYtDlpTag()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://github.com/yt-dlp/yt-dlp/releases/latest");
                request.Method = "HEAD";
                request.AllowAutoRedirect = false;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                request.Timeout = 10000;

                using (var response = request.GetResponse())
                {
                    string location = response.Headers["Location"];
                    if (!string.IsNullOrEmpty(location))
                        return location.Substring(location.LastIndexOf('/') + 1);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"TVSync: Failed to fetch latest yt-dlp tag: {ex.Message}");
            }
            return null;
        }

        public static void DownloadFileSync(Uri uri, string filename)
        {
            try
            {
                TryDeleteFile(filename);
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    client.DownloadFile(uri, filename);
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"TVSync: Download failed for {uri}: {ex.Message}");
                throw;
            }
        }
    }
}
