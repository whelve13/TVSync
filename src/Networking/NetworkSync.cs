using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace TVSync.Networking
{
    /// <summary>
    /// Host-authoritative network layer using Unity Netcode's CustomMessagingManager.
    /// No NetworkBehaviour registration needed — works with vanilla Lethal Company.
    /// </summary>
    public static class NetworkSync
    {
        // Message names
        private const string MSG_PLAY_VIDEO = "TVSync_PlayVideo";
        private const string MSG_SYNC_STATE = "TVSync_SyncState";
        private const string MSG_CLIENT_READY = "TVSync_ClientReady";
        private const string MSG_PAUSE = "TVSync_Pause";
        private const string MSG_RESUME = "TVSync_Resume";
        private const string MSG_SEEK = "TVSync_Seek";
        private const string MSG_STOP = "TVSync_Stop";
        private const string MSG_CHAT = "TVSync_Chat";
        private const string MSG_REQUEST_STATE = "TVSync_RequestState";

        public static bool IsRegistered { get; private set; }

        // Track which clients are ready
        private static HashSet<ulong> _readyClients = new HashSet<ulong>();
        private static int _expectedReadyCount = 0;
        private static Action _onAllReady;

        public static void Register()
        {
            if (IsRegistered) return;
            var mgr = NetworkManager.Singleton?.CustomMessagingManager;
            if (mgr == null) return;

            mgr.RegisterNamedMessageHandler(MSG_PLAY_VIDEO, OnReceivePlayVideo);
            mgr.RegisterNamedMessageHandler(MSG_SYNC_STATE, OnReceiveSyncState);
            mgr.RegisterNamedMessageHandler(MSG_CLIENT_READY, OnReceiveClientReady);
            mgr.RegisterNamedMessageHandler(MSG_PAUSE, OnReceivePause);
            mgr.RegisterNamedMessageHandler(MSG_RESUME, OnReceiveResume);
            mgr.RegisterNamedMessageHandler(MSG_SEEK, OnReceiveSeek);
            mgr.RegisterNamedMessageHandler(MSG_STOP, OnReceiveStop);
            mgr.RegisterNamedMessageHandler(MSG_CHAT, OnReceiveChat);
            mgr.RegisterNamedMessageHandler(MSG_REQUEST_STATE, OnReceiveRequestState);

            IsRegistered = true;
            Plugin.Log.LogInfo("TVSync: Network message handlers registered.");
        }

        public static void Unregister()
        {
            if (!IsRegistered) return;
            var mgr = NetworkManager.Singleton?.CustomMessagingManager;
            if (mgr == null) return;

            mgr.UnregisterNamedMessageHandler(MSG_PLAY_VIDEO);
            mgr.UnregisterNamedMessageHandler(MSG_SYNC_STATE);
            mgr.UnregisterNamedMessageHandler(MSG_CLIENT_READY);
            mgr.UnregisterNamedMessageHandler(MSG_PAUSE);
            mgr.UnregisterNamedMessageHandler(MSG_RESUME);
            mgr.UnregisterNamedMessageHandler(MSG_SEEK);
            mgr.UnregisterNamedMessageHandler(MSG_STOP);
            mgr.UnregisterNamedMessageHandler(MSG_CHAT);
            mgr.UnregisterNamedMessageHandler(MSG_REQUEST_STATE);

            IsRegistered = false;
            _readyClients.Clear();
        }

        public static bool IsHost =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

        public static bool IsConnected =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;

        #region Host -> All Clients

        /// <summary>
        /// Host tells all clients to download and prepare a video.
        /// </summary>
        public static void SendPlayVideo(string url)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            WriteString(writer, url);
            SendToAllClients(MSG_PLAY_VIDEO, writer);
            writer.Dispose();

            // Host also processes locally
            TVController.Instance?.OnPlayVideoReceived(url);
        }

        /// <summary>
        /// Host broadcasts current playback state for drift correction.
        /// </summary>
        public static void SendSyncState(PlaybackState state)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            state.Serialize(writer);
            SendToAllClients(MSG_SYNC_STATE, writer);
            writer.Dispose();
        }

        /// <summary>
        /// Host tells all clients to pause.
        /// </summary>
        public static void SendPause(double pauseTime)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            writer.WriteValueSafe(pauseTime);
            SendToAllClients(MSG_PAUSE, writer);
            writer.Dispose();

            TVController.Instance?.OnPauseReceived(pauseTime);
        }

        /// <summary>
        /// Host tells all clients to resume from a position.
        /// </summary>
        public static void SendResume(double resumeTime, double networkTime)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            writer.WriteValueSafe(resumeTime);
            writer.WriteValueSafe(networkTime);
            SendToAllClients(MSG_RESUME, writer);
            writer.Dispose();

            TVController.Instance?.OnResumeReceived(resumeTime, networkTime);
        }

        /// <summary>
        /// Host tells all clients to seek to a time.
        /// </summary>
        public static void SendSeek(double seekTime)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            writer.WriteValueSafe(seekTime);
            SendToAllClients(MSG_SEEK, writer);
            writer.Dispose();

            TVController.Instance?.OnSeekReceived(seekTime);
        }

        /// <summary>
        /// Host tells all clients to stop playback.
        /// </summary>
        public static void SendStop()
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            writer.WriteValueSafe((byte)0); // dummy
            SendToAllClients(MSG_STOP, writer);
            writer.Dispose();

            TVController.Instance?.OnStopReceived();
        }

        /// <summary>
        /// Host broadcasts a chat message to all clients.
        /// </summary>
        public static void SendChat(string message)
        {
            if (!IsHost) return;
            var writer = BeginMessage();
            WriteString(writer, message);
            SendToAllClients(MSG_CHAT, writer);
            writer.Dispose();
        }

        #endregion

        #region Client -> Host

        /// <summary>
        /// Client reports it's ready to play.
        /// </summary>
        public static void SendClientReady()
        {
            if (IsHost)
            {
                // Host reports itself ready locally
                OnClientReady(NetworkManager.Singleton.LocalClientId);
                return;
            }

            var writer = BeginMessage();
            writer.WriteValueSafe(NetworkManager.Singleton.LocalClientId);
            SendToServer(MSG_CLIENT_READY, writer);
            writer.Dispose();
        }

        /// <summary>
        /// Client requests current state (for late join).
        /// </summary>
        public static void SendRequestState()
        {
            if (IsHost) return; // Host already has state

            var writer = BeginMessage();
            writer.WriteValueSafe(NetworkManager.Singleton.LocalClientId);
            SendToServer(MSG_REQUEST_STATE, writer);
            writer.Dispose();
        }

        #endregion

        #region Ready System

        public static void BeginWaitForReady(int expectedCount, Action onAllReady)
        {
            _readyClients.Clear();
            _expectedReadyCount = expectedCount;
            _onAllReady = onAllReady;
        }

        private static void OnClientReady(ulong clientId)
        {
            _readyClients.Add(clientId);
            Plugin.Log.LogInfo($"TVSync: Client {clientId} ready ({_readyClients.Count}/{_expectedReadyCount})");

            if (_readyClients.Count >= _expectedReadyCount)
            {
                _onAllReady?.Invoke();
                _onAllReady = null;
            }
        }

        public static void ForceAllReady()
        {
            _onAllReady?.Invoke();
            _onAllReady = null;
        }

        public static int ReadyCount => _readyClients.Count;
        public static int ExpectedCount => _expectedReadyCount;

        #endregion

        #region Message Handlers

        private static void OnReceivePlayVideo(ulong senderClientId, FastBufferReader reader)
        {
            string url = ReadString(reader);
            Plugin.Log.LogInfo($"TVSync: Received PlayVideo: {url}");
            TVController.Instance?.OnPlayVideoReceived(url);
        }

        private static void OnReceiveSyncState(ulong senderClientId, FastBufferReader reader)
        {
            var state = PlaybackState.Deserialize(reader);
            TVController.Instance?.OnSyncStateReceived(state);
        }

        private static void OnReceiveClientReady(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong clientId);
            if (IsHost)
            {
                OnClientReady(clientId);
            }
        }

        private static void OnReceivePause(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out double pauseTime);
            TVController.Instance?.OnPauseReceived(pauseTime);
        }

        private static void OnReceiveResume(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out double resumeTime);
            reader.ReadValueSafe(out double networkTime);
            TVController.Instance?.OnResumeReceived(resumeTime, networkTime);
        }

        private static void OnReceiveSeek(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out double seekTime);
            TVController.Instance?.OnSeekReceived(seekTime);
        }

        private static void OnReceiveStop(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte _);
            TVController.Instance?.OnStopReceived();
        }

        private static void OnReceiveChat(ulong senderClientId, FastBufferReader reader)
        {
            string message = ReadString(reader);
            TVController.Instance?.OnChatReceived(message);
        }

        private static void OnReceiveRequestState(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong clientId);
            if (IsHost)
            {
                // Send current state back to requesting client
                TVController.Instance?.SendCurrentStateToClient(clientId);
            }
        }

        #endregion

        #region Helpers

        private static FastBufferWriter BeginMessage()
        {
            return new FastBufferWriter(4096, Allocator.Temp);
        }

        private static void SendToAllClients(string msgName, FastBufferWriter writer)
        {
            var mgr = NetworkManager.Singleton?.CustomMessagingManager;
            if (mgr == null) return;

            var clients = NetworkManager.Singleton.ConnectedClientsIds;
            foreach (ulong clientId in clients)
            {
                if (clientId == NetworkManager.Singleton.LocalClientId) continue;
                mgr.SendNamedMessage(msgName, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void SendToServer(string msgName, FastBufferWriter writer)
        {
            var mgr = NetworkManager.Singleton?.CustomMessagingManager;
            if (mgr == null) return;

            mgr.SendNamedMessage(msgName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
        }

        public static void SendSyncStateToClient(ulong clientId, PlaybackState state)
        {
            var mgr = NetworkManager.Singleton?.CustomMessagingManager;
            if (mgr == null) return;

            var writer = BeginMessage();
            state.Serialize(writer);
            mgr.SendNamedMessage(MSG_SYNC_STATE, clientId, writer, NetworkDelivery.ReliableSequenced);
            writer.Dispose();
        }

        private static void WriteString(FastBufferWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            writer.WriteValueSafe(bytes.Length);
            if (bytes.Length > 0)
                writer.WriteBytesSafe(bytes);
        }

        private static string ReadString(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int length);
            if (length <= 0) return "";
            byte[] bytes = new byte[length];
            reader.ReadBytesSafe(ref bytes, length);
            return Encoding.UTF8.GetString(bytes);
        }

        #endregion
    }

    /// <summary>
    /// Represents the host's current playback state, serialized for network transmission.
    /// </summary>
    public struct PlaybackState
    {
        public double PlaybackTime;
        public double NetworkTime;    // NetworkManager.ServerTime when this was sent
        public float PlaybackSpeed;
        public bool IsPaused;
        public bool IsPlaying;
        public string VideoUrl;

        public void Serialize(FastBufferWriter writer)
        {
            writer.WriteValueSafe(PlaybackTime);
            writer.WriteValueSafe(NetworkTime);
            writer.WriteValueSafe(PlaybackSpeed);
            writer.WriteValueSafe(IsPaused);
            writer.WriteValueSafe(IsPlaying);

            byte[] urlBytes = Encoding.UTF8.GetBytes(VideoUrl ?? "");
            writer.WriteValueSafe(urlBytes.Length);
            if (urlBytes.Length > 0)
                writer.WriteBytesSafe(urlBytes);
        }

        public static PlaybackState Deserialize(FastBufferReader reader)
        {
            var state = new PlaybackState();
            reader.ReadValueSafe(out state.PlaybackTime);
            reader.ReadValueSafe(out state.NetworkTime);
            reader.ReadValueSafe(out state.PlaybackSpeed);
            reader.ReadValueSafe(out state.IsPaused);
            reader.ReadValueSafe(out state.IsPlaying);

            reader.ReadValueSafe(out int urlLen);
            if (urlLen > 0)
            {
                byte[] urlBytes = new byte[urlLen];
                reader.ReadBytesSafe(ref urlBytes, urlLen);
                state.VideoUrl = Encoding.UTF8.GetString(urlBytes);
            }
            else
            {
                state.VideoUrl = "";
            }
            return state;
        }
    }
}
