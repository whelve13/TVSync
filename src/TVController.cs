using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using TVSync.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Video;

namespace TVSync
{
    /// <summary>
    /// Core TV controller that manages host-authoritative synchronized playback.
    /// Attached to the TVScript's GameObject at runtime via Harmony patch.
    /// </summary>
    public class TVController : MonoBehaviour
    {
        public static TVController Instance { get; private set; }

        // Reference to the game's TVScript
        public TVScript TV { get; private set; }

        // State
        private bool _isDownloading;
        private bool _isWaitingForReady;
        private bool _isPlaying;
        private bool _isPaused;
        private double _scheduledStartNetworkTime;
        private string _currentUrl = "";
        private double _hostPlaybackTime;

        // Drift correction
        private float _lastSyncTime;
        private float _syncInterval;
        private float _driftThresholdSec;
        private float _maxSeekThresholdSec;
        private float _readyTimeoutSec;

        // Position memory
        public bool PositionMemory { get; set; }
        public double SavedPosition { get; set; }

        // Volume (local, client-side)
        public float Volume
        {
            get => TV != null ? TV.tvSFX.volume : 0.5f;
            set
            {
                if (TV != null) TV.tvSFX.volume = Mathf.Clamp01(value);
                SaveVolume(value);
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Init(TVScript tvScript)
        {
            TV = tvScript;
            var cfg = Plugin.SyncConfig;
            _syncInterval = cfg.SyncIntervalSec.Value;
            _driftThresholdSec = cfg.DriftThresholdMs.Value / 1000f;
            _maxSeekThresholdSec = cfg.MaxSeekThresholdMs.Value / 1000f;
            _readyTimeoutSec = cfg.ReadyTimeoutSec.Value;

            LoadVolume();
        }

        private void Update()
        {
            if (TV == null || TV.video == null) return;

            // Host: periodically broadcast sync state
            if (NetworkSync.IsHost && _isPlaying && !_isPaused)
            {
                if (Time.time - _lastSyncTime >= _syncInterval)
                {
                    _lastSyncTime = Time.time;
                    BroadcastSyncState();
                }
            }

            // Wait for scheduled start time
            if (_isWaitingForReady && _scheduledStartNetworkTime > 0)
            {
                double currentNetTime = GetNetworkTime();
                if (currentNetTime >= _scheduledStartNetworkTime)
                {
                    _isWaitingForReady = false;
                    BeginPlaybackNow();
                }
            }
        }

        #region Host Commands

        /// <summary>
        /// Host initiates video playback. Downloads video and coordinates all clients.
        /// Called only on the host (command processing ensures this).
        /// </summary>
        public void HostPlayVideo(string url, HUDManager hud, string playerName)
        {
            if (_isDownloading)
            {
                ShowChat(Plugin.SyncConfig.LangData.VideoLoading.Value);
                return;
            }

            _currentUrl = url;

            // Tell all clients to download
            NetworkSync.SendPlayVideo(url);

            // Host also starts downloading (handled by SendPlayVideo calling OnPlayVideoReceived)
            ShowChat(Plugin.SyncConfig.LangData.PleaseWait.Value);
        }

        /// <summary>
        /// Host pauses playback for everyone.
        /// </summary>
        public void HostPause()
        {
            if (!NetworkSync.IsHost) return;
            if (!_isPlaying || _isPaused) return;

            double pauseTime = TV.video.time;
            NetworkSync.SendPause(pauseTime);
        }

        /// <summary>
        /// Host resumes playback for everyone.
        /// </summary>
        public void HostResume()
        {
            if (!NetworkSync.IsHost) return;
            if (!_isPaused) return;

            double resumeTime = TV.video.time;
            double networkTime = GetNetworkTime() + 0.5; // small buffer
            NetworkSync.SendResume(resumeTime, networkTime);
        }

        /// <summary>
        /// Host seeks to a specific time.
        /// </summary>
        public void HostSeek(double seekTime)
        {
            if (!NetworkSync.IsHost) return;
            NetworkSync.SendSeek(seekTime);
        }

        /// <summary>
        /// Host stops playback for everyone.
        /// </summary>
        public void HostStop()
        {
            if (!NetworkSync.IsHost) return;
            NetworkSync.SendStop();
        }

        /// <summary>
        /// Host restarts playback from beginning.
        /// </summary>
        public void HostRestart()
        {
            if (!NetworkSync.IsHost) return;
            NetworkSync.SendSeek(0);
        }

        #endregion

        #region Network Event Handlers

        /// <summary>
        /// Called when PlayVideo message is received (on both host and clients).
        /// </summary>
        public void OnPlayVideoReceived(string url)
        {
            _currentUrl = url;
            _isPlaying = false;
            _isPaused = false;

            StartCoroutine(DownloadAndPrepareCoroutine(url));
        }

        /// <summary>
        /// Called when SyncState is received (clients only for drift correction).
        /// </summary>
        public void OnSyncStateReceived(PlaybackState state)
        {
            if (NetworkSync.IsHost) return;
            if (!_isPlaying) return;
            if (_isPaused != state.IsPaused) return; // State mismatch, ignore

            ApplyDriftCorrection(state);
        }

        /// <summary>
        /// Called when pause is received.
        /// </summary>
        public void OnPauseReceived(double pauseTime)
        {
            if (TV == null || TV.video == null) return;

            _isPaused = true;
            TV.video.Pause();
            TV.tvSFX.Pause();
            TV.video.time = pauseTime;
        }

        /// <summary>
        /// Called when resume is received.
        /// </summary>
        public void OnResumeReceived(double resumeTime, double scheduledNetworkTime)
        {
            if (TV == null || TV.video == null) return;

            TV.video.time = resumeTime;
            _scheduledStartNetworkTime = scheduledNetworkTime;
            _isPaused = false;

            // Wait for the scheduled network time
            _isWaitingForReady = true;
        }

        /// <summary>
        /// Called when seek is received.
        /// </summary>
        public void OnSeekReceived(double seekTime)
        {
            if (TV == null || TV.video == null) return;

            TV.video.time = seekTime;
            _hostPlaybackTime = seekTime;
        }

        /// <summary>
        /// Called when stop is received.
        /// </summary>
        public void OnStopReceived()
        {
            if (TV == null) return;

            _isPlaying = false;
            _isPaused = false;
            _isWaitingForReady = false;

            if (TV.tvOn)
            {
                TV.tvOn = false;
                SetTVScreenMaterial(false);
                TV.tvSFX.Stop();
                TV.video.Stop();
            }
        }

        /// <summary>
        /// Called when a chat message is received from the host.
        /// </summary>
        public void OnChatReceived(string message)
        {
            ShowChat(message);
        }

        /// <summary>
        /// Host sends current state to a specific client (late joiner).
        /// </summary>
        public void SendCurrentStateToClient(ulong clientId)
        {
            if (!NetworkSync.IsHost) return;

            var state = new PlaybackState
            {
                PlaybackTime = TV != null ? TV.video.time : 0,
                NetworkTime = GetNetworkTime(),
                PlaybackSpeed = 1f,
                IsPaused = _isPaused,
                IsPlaying = _isPlaying,
                VideoUrl = _currentUrl
            };

            NetworkSync.SendSyncStateToClient(clientId, state);
        }

        #endregion

        #region Download & Prepare

        private IEnumerator DownloadAndPrepareCoroutine(string url)
        {
            _isDownloading = true;

            // Ensure directories exist (critical for clients)
            try
            {
                if (!Directory.Exists(Plugin.OtherDir)) Directory.CreateDirectory(Plugin.OtherDir);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"TVSync: Could not create directory: {ex.Message}");
            }

            // Stop current playback
            if (TV.tvOn)
            {
                TV.video.Stop();
                TV.tvSFX.Stop();
            }

            // Delete old file
            string videoPath = Path.GetFullPath(Plugin.VideoPath);
            try
            {
                if (File.Exists(videoPath)) File.Delete(videoPath);
                string partPath = videoPath + ".part";
                if (File.Exists(partPath)) File.Delete(partPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"TVSync: Could not clean old video: {ex.Message}");
            }

            // Wait for yt-dlp to be ready
            ShowChat(Plugin.SyncConfig.LangData.PleaseWait.Value);
            float waitStart = Time.time;
            while (!Plugin.YtDlpReady && Time.time - waitStart < 60f)
            {
                yield return new WaitForSeconds(1f);
            }

            if (!Plugin.YtDlpReady)
            {
                Plugin.Log.LogError("TVSync: yt-dlp not ready after 60s");
                _isDownloading = false;
                ShowChat(Plugin.SyncConfig.LangData.LinkInvalid.Value);
                yield break;
            }

            // Build yt-dlp arguments
            string ytDlpFullPath = Path.GetFullPath(Plugin.YtDlpPath);
            string workDir = Path.GetFullPath(Plugin.OtherDir);
            string cookiesPath = Path.Combine(workDir, "cookies.txt");
            string cookiesArg = File.Exists(cookiesPath) ? $"--cookies \"{cookiesPath}\" " : "";

            string arguments =
                $"{cookiesArg}" +
                $"-f \"b[height<=360][ext=mp4]/b[ext=mp4]/b\" " +
                $"--force-ipv4 -N 4 \"{url}\" -o test.mp4";

            Plugin.Log.LogInfo($"TVSync: Starting download: {ytDlpFullPath} {arguments}");
            Plugin.Log.LogInfo($"TVSync: Working directory: {workDir}");

            // Start yt-dlp download in a thread
            bool downloadComplete = false;
            bool downloadFailed = false;
            string errorMsg = "";

            Thread downloadThread = new Thread(() =>
            {
                try
                {
                    var process = new Process();
                    process.StartInfo.FileName = ytDlpFullPath;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.WorkingDirectory = workDir;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();

                    // Capture output for debugging
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();

                    process.WaitForExit(120000); // 2 min timeout

                    if (!process.HasExited)
                    {
                        process.Kill();
                        downloadFailed = true;
                        errorMsg = "yt-dlp timed out after 2 minutes";
                        return;
                    }

                    if (!string.IsNullOrEmpty(stderr))
                        Plugin.Log.LogWarning($"TVSync: yt-dlp stderr: {stderr}");
                    if (!string.IsNullOrEmpty(stdout))
                        Plugin.Log.LogInfo($"TVSync: yt-dlp stdout: {stdout}");

                    Plugin.Log.LogInfo($"TVSync: yt-dlp exit code: {process.ExitCode}");

                    // Wait for file to fully appear
                    for (int i = 0; i < 30; i++)
                    {
                        if (File.Exists(videoPath))
                        {
                            downloadComplete = true;
                            return;
                        }
                        Thread.Sleep(500);
                    }

                    downloadFailed = true;
                    errorMsg = $"Video file not found at: {videoPath}";
                }
                catch (Exception ex)
                {
                    downloadFailed = true;
                    errorMsg = ex.ToString();
                }
            })
            { IsBackground = true };

            downloadThread.Start();

            // Wait for download to finish
            while (downloadThread.IsAlive)
            {
                yield return new WaitForSeconds(0.5f);
            }

            if (downloadFailed || !downloadComplete)
            {
                Plugin.Log.LogError($"TVSync: Download failed: {errorMsg}");
                _isDownloading = false;
                ShowChat(Plugin.SyncConfig.LangData.LinkInvalid.Value);
                yield break;
            }

            // Prepare VideoPlayer
            string fileUrl = "file:///" + videoPath.Replace('\\', '/');
            Plugin.Log.LogInfo($"TVSync: Loading video from: {fileUrl}");
            TV.video.source = VideoSource.Url;
            TV.video.url = fileUrl;
            TV.video.controlledAudioTrackCount = 1;
            TV.video.audioOutputMode = VideoAudioOutputMode.AudioSource;
            TV.video.SetTargetAudioSource(0, TV.tvSFX);
            TV.video.playbackSpeed = 1f;

            TV.video.Prepare();

            // Wait for preparation
            float prepStart = Time.time;
            while (!TV.video.isPrepared && Time.time - prepStart < 30f)
            {
                yield return new WaitForSeconds(0.1f);
            }

            _isDownloading = false;

            if (!TV.video.isPrepared)
            {
                Plugin.Log.LogError("TVSync: VideoPlayer failed to prepare");
                ShowChat(Plugin.SyncConfig.LangData.LinkInvalid.Value);
                yield break;
            }

            Plugin.Log.LogInfo("TVSync: Video prepared. Reporting ready.");
            ShowChat(Plugin.SyncConfig.LangData.VideoReady.Value);

            // Report ready to host
            NetworkSync.SendClientReady();

            if (NetworkSync.IsHost)
            {
                // Host: wait for all clients to be ready, then start synchronized playback
                StartCoroutine(WaitForReadyAndStart());
            }
        }

        private IEnumerator WaitForReadyAndStart()
        {
            int clientCount = NetworkManager.Singleton.ConnectedClientsIds.Count;
            _isWaitingForReady = true;

            ShowChat(Plugin.SyncConfig.LangData.WaitingForPlayers.Value);

            NetworkSync.BeginWaitForReady(clientCount, () =>
            {
                Plugin.Log.LogInfo("TVSync: All clients ready!");
            });

            float waitStart = Time.time;
            while (NetworkSync.ReadyCount < NetworkSync.ExpectedCount)
            {
                if (Time.time - waitStart > _readyTimeoutSec)
                {
                    Plugin.Log.LogWarning(
                        $"TVSync: Timeout waiting for clients ({NetworkSync.ReadyCount}/{NetworkSync.ExpectedCount})");
                    ShowChat(Plugin.SyncConfig.LangData.PlayerTimedOut.Value);
                    NetworkSync.ForceAllReady();
                    break;
                }
                yield return new WaitForSeconds(0.25f);
            }

            ShowChat(Plugin.SyncConfig.LangData.AllPlayersReady.Value);

            // Schedule start time: current network time + 1 second buffer
            double startTime = GetNetworkTime() + 1.0;
            _scheduledStartNetworkTime = startTime;

            // Tell all clients to start at this time
            NetworkSync.SendResume(0, startTime);
        }

        private void BeginPlaybackNow()
        {
            if (TV == null || TV.video == null) return;

            Plugin.Log.LogInfo("TVSync: Beginning synchronized playback!");

            // Turn TV on
            TV.tvOn = true;
            SetTVScreenMaterial(true);

            TV.video.Play();
            TV.tvSFX.Play();
            TV.tvSFX.PlayOneShot(TV.switchTVOn);
            WalkieTalkie.TransmitOneShotAudio(TV.tvSFX, TV.switchTVOn, 1f);

            _isPlaying = true;
            _isPaused = false;
            _lastSyncTime = Time.time;
        }

        #endregion

        #region Drift Correction

        private void BroadcastSyncState()
        {
            if (TV == null || TV.video == null) return;

            var state = new PlaybackState
            {
                PlaybackTime = TV.video.time,
                NetworkTime = GetNetworkTime(),
                PlaybackSpeed = TV.video.playbackSpeed,
                IsPaused = _isPaused,
                IsPlaying = _isPlaying,
                VideoUrl = _currentUrl
            };

            NetworkSync.SendSyncState(state);
        }

        private void ApplyDriftCorrection(PlaybackState hostState)
        {
            if (TV == null || TV.video == null) return;
            if (!TV.video.isPlaying) return;

            // Calculate what the host's playback time should be NOW
            double networkNow = GetNetworkTime();
            double elapsed = networkNow - hostState.NetworkTime;
            double expectedHostTime = hostState.PlaybackTime + (elapsed * hostState.PlaybackSpeed);

            double localTime = TV.video.time;
            double drift = expectedHostTime - localTime;
            double absDrift = Math.Abs(drift);

            if (absDrift < _driftThresholdSec)
            {
                // Within threshold — reset speed to normal
                TV.video.playbackSpeed = 1f;
                return;
            }

            if (absDrift > _maxSeekThresholdSec)
            {
                // Large drift — hard seek
                Plugin.Log.LogInfo($"TVSync: Large drift {drift:F3}s — seeking to {expectedHostTime:F2}");
                TV.video.time = expectedHostTime;
                TV.video.playbackSpeed = 1f;
            }
            else
            {
                // Small drift — adjust speed
                float speedAdjust = drift > 0 ? 1.03f : 0.97f;
                TV.video.playbackSpeed = speedAdjust;
            }
        }

        #endregion

        #region Late Join

        public void HandleLateJoin()
        {
            if (NetworkSync.IsHost) return;

            Plugin.Log.LogInfo("TVSync: Requesting current state (late join)...");
            NetworkSync.SendRequestState();
        }

        /// <summary>
        /// Client handles full state sync from host (late join scenario).
        /// </summary>
        public void HandleFullStateSync(PlaybackState state)
        {
            if (string.IsNullOrEmpty(state.VideoUrl)) return;
            if (!state.IsPlaying) return;

            _currentUrl = state.VideoUrl;
            StartCoroutine(LateJoinCoroutine(state));
        }

        private IEnumerator LateJoinCoroutine(PlaybackState state)
        {
            // Download and prepare
            yield return DownloadAndPrepareCoroutine(state.VideoUrl);

            if (TV.video.isPrepared)
            {
                // Calculate where the host should be now
                double networkNow = GetNetworkTime();
                double elapsed = networkNow - state.NetworkTime;
                double expectedTime = state.PlaybackTime + (elapsed * state.PlaybackSpeed);

                TV.video.time = Math.Max(0, expectedTime);

                if (state.IsPaused)
                {
                    TV.video.time = state.PlaybackTime;
                    _isPaused = true;
                    // Don't auto-play
                }
                else
                {
                    BeginPlaybackNow();
                }
            }
        }

        #endregion

        #region TV Control Helpers

        public void SetTVScreenMaterial(bool on)
        {
            if (TV == null || TV.tvMesh == null) return;

            Material[] mats = TV.tvMesh.sharedMaterials;
            if (mats.Length > 1)
            {
                mats[1] = on ? TV.tvOnMaterial : TV.tvOffMaterial;
                TV.tvMesh.sharedMaterials = mats;
            }
            if (TV.tvLight != null)
                TV.tvLight.enabled = on;
        }

        /// <summary>
        /// Handle TV toggle (E key interaction).
        /// </summary>
        public void HandleTVToggle(bool on)
        {
            if (on)
            {
                // Turn on
                if (PositionMemory)
                {
                    TV.video.time = SavedPosition;
                }

                SetTVScreenMaterial(true);
                TV.video.Play();
                TV.tvSFX.Play();
                TV.tvSFX.PlayOneShot(TV.switchTVOn);
                WalkieTalkie.TransmitOneShotAudio(TV.tvSFX, TV.switchTVOn, 1f);
            }
            else
            {
                // Turn off
                if (PositionMemory)
                {
                    SavedPosition = TV.video.time;
                }

                SetTVScreenMaterial(false);
                TV.tvSFX.Stop();
                TV.video.Stop();
                TV.tvSFX.PlayOneShot(TV.switchTVOff);
                WalkieTalkie.TransmitOneShotAudio(TV.tvSFX, TV.switchTVOff, 1f);
            }
        }

        public double CurrentTime => TV != null && TV.video != null ? TV.video.time : 0;
        public double TotalTime => TV != null && TV.video != null ? TV.video.length : 0;
        public bool IsPlaying => _isPlaying;
        public bool IsPaused => _isPaused;

        #endregion

        #region Volume Persistence

        private void LoadVolume()
        {
            try
            {
                if (File.Exists(Plugin.CachePath))
                {
                    string content = File.ReadAllText(Plugin.CachePath).Trim();
                    if (!string.IsNullOrEmpty(content) && float.TryParse(content, out float vol))
                    {
                        if (TV != null) TV.tvSFX.volume = Mathf.Clamp01(vol);
                    }
                    else
                    {
                        if (TV != null) TV.tvSFX.volume = 0.5f;
                    }
                }
                else
                {
                    if (TV != null) TV.tvSFX.volume = 0.5f;
                }
            }
            catch
            {
                if (TV != null) TV.tvSFX.volume = 0.5f;
            }
        }

        private void SaveVolume(float vol)
        {
            try
            {
                File.WriteAllText(Plugin.CachePath, vol.ToString("F2"));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"TVSync: Could not save volume: {ex.Message}");
            }
        }

        #endregion

        #region Chat

        public void ShowChat(string message)
        {
            var hud = HUDManager.Instance;
            if (hud == null) return;

            ChatHelper.ShowMessage(hud, message, "TVSync");
        }

        #endregion

        #region Utility

        private static double GetNetworkTime()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
            {
                return NetworkManager.Singleton.ServerTime.Time;
            }
            return Time.timeAsDouble;
        }

        #endregion
    }
}
