using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using TVSync.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Video;

namespace TVSync.Patches
{
    /// <summary>
    /// Harmony patches for the TVScript (TV behavior).
    /// </summary>
    public static class TVPatches
    {
        /// <summary>
        /// Patch OnEnable to attach our TVController and initialize the TV.
        /// </summary>
        [HarmonyPatch(typeof(TVScript), "OnEnable")]
        [HarmonyPrefix]
        public static bool OnEnable_Prefix(TVScript __instance)
        {
            // Attach TVController if not already present
            var controller = __instance.GetComponent<TVController>();
            if (controller == null)
            {
                controller = __instance.gameObject.AddComponent<TVController>();
            }
            controller.Init(__instance);

            // Clear default clips
            __instance.video.clip = null;
            __instance.tvSFX.clip = null;

            __instance.video.Stop();
            __instance.tvSFX.Stop();

            return false; // Skip original
        }

        /// <summary>
        /// Patch TurnTVOnOff to use our synchronized controller.
        /// </summary>
        [HarmonyPatch(typeof(TVScript), "TurnTVOnOff")]
        [HarmonyPrefix]
        public static bool TurnTVOnOff_Prefix(TVScript __instance, bool on)
        {
            __instance.tvOn = on;

            var controller = TVController.Instance;
            if (controller != null)
            {
                controller.HandleTVToggle(on);
            }

            return false; // Skip original
        }

        /// <summary>
        /// Patch TVFinishedClip to prevent default behavior.
        /// </summary>
        [HarmonyPatch(typeof(TVScript), "TVFinishedClip")]
        [HarmonyPrefix]
        public static bool TVFinishedClip_Prefix()
        {
            return false; // Skip original — we handle video lifecycle ourselves
        }

        /// <summary>
        /// Patch Update to track time display without running original logic.
        /// </summary>
        [HarmonyPatch(typeof(TVScript), "Update")]
        [HarmonyPrefix]
        public static bool Update_Prefix()
        {
            return false; // Skip original — our TVController.Update handles sync
        }
    }

    /// <summary>
    /// Harmony patches for the HUD (chat commands, tooltips, etc).
    /// </summary>
    public static class HUDPatches
    {
        /// <summary>
        /// Override tooltip display on the TV to show our info.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "SetHoverTipAndCurrentInteractTrigger")]
        [HarmonyPostfix]
        public static void SetHoverTipAndCurrentInteractTrigger_Postfix(PlayerControllerB __instance)
        {
            InteractTrigger currentTrigger = __instance.hoveringOverTrigger;
            if (currentTrigger == null || !currentTrigger.interactable) return;

            // Check if this is the TV
            bool isTV = currentTrigger.transform.parent != null &&
                       (currentTrigger.transform.parent.name.Contains("Television") ||
                        currentTrigger.hoverTip.Contains("Switch TV"));

            if (!isTV) return;

            var controller = TVController.Instance;
            if (controller == null || controller.TV == null) return;

            if (!Plugin.YtDlpReady)
            {
                currentTrigger.hoverTip = Plugin.SyncConfig.LangData.LibLoading.Value;
                return;
            }

            // Build tooltip
            double curTime = controller.CurrentTime;
            double totTime = controller.TotalTime;

            string curStr = FormatTime(curTime);
            string totStr = FormatTime(totTime);
            float volume = controller.Volume;

            currentTrigger.hoverTip = Plugin.SyncConfig.LangData.TVTooltip.Value
                .Replace("@1", $"{Mathf.RoundToInt(volume * 100f)}%")
                .Replace("@2", curStr)
                .Replace("@3", totStr);

            // Volume keys
            if (((ButtonControl)Keyboard.current.pageDownKey).wasPressedThisFrame && volume > 0f)
            {
                controller.Volume = Mathf.Max(0f, volume - 0.1f);
            }
            if (((ButtonControl)Keyboard.current.pageUpKey).wasPressedThisFrame && volume < 1f)
            {
                controller.Volume = Mathf.Min(1f, volume + 0.1f);
            }
        }

        /// <summary>
        /// Increase chat character limit.
        /// </summary>
        [HarmonyPatch(typeof(HUDManager), "Start")]
        [HarmonyPostfix]
        public static void HUDStart_Postfix(HUDManager __instance)
        {
            __instance.chatTextField.characterLimit = 200;
        }

        /// <summary>
        /// Allow longer messages to be sent via RPC.
        /// </summary>
        [HarmonyPatch(typeof(HUDManager), "AddPlayerChatMessageServerRpc")]
        [HarmonyPostfix]
        public static void AddPlayerChatMessageServerRpc_Postfix(HUDManager __instance, string chatMessage, int playerId)
        {
            if (chatMessage.Length > 50)
            {
                var method = typeof(HUDManager).GetMethod("AddPlayerChatMessageClientRpc",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                method?.Invoke(__instance, new object[] { chatMessage, playerId });
            }
        }

        /// <summary>
        /// Intercept chat messages to handle TV commands.
        /// </summary>
        [HarmonyPatch(typeof(HUDManager), "AddChatMessage")]
        [HarmonyPrefix]
        public static bool AddChatMessage_Prefix(HUDManager __instance, string chatMessage, string nameOfUserWhoTyped)
        {
            // Process the display
            if (__instance.lastChatMessage != chatMessage)
            {
                __instance.lastChatMessage = chatMessage;
                __instance.PingHUDElement(__instance.Chat, 4f, 1f, 0.2f);

                if (__instance.ChatMessageHistory.Count >= 4)
                {
                    __instance.ChatMessageHistory.RemoveAt(0);
                }

                var sb = new StringBuilder(chatMessage);
                for (int i = 0; i < Math.Min(4, StartOfRound.Instance.allPlayerScripts.Length); i++)
                {
                    sb.Replace($"[playerNum{i}]", StartOfRound.Instance.allPlayerScripts[i].playerUsername);
                }
                chatMessage = sb.ToString();

                string formatted;
                if (!string.IsNullOrEmpty(nameOfUserWhoTyped))
                    formatted = $"<color=#FF0000>{nameOfUserWhoTyped}</color>: <color=#FFFF00>'{chatMessage}'</color>";
                else
                    formatted = $"<color=#7069ff>{chatMessage}</color>";

                __instance.ChatMessageHistory.Add(formatted);

                var textSb = new StringBuilder();
                for (int i = 0; i < __instance.ChatMessageHistory.Count; i++)
                {
                    textSb.Append("\n");
                    textSb.Append(__instance.ChatMessageHistory[i]);
                }
                ((TMP_Text)__instance.chatText).text = textSb.ToString();

                // Process commands only if it's a new message
                ProcessCommand(__instance, chatMessage, nameOfUserWhoTyped);
            }

            return false; // Skip original
        }

        /// <summary>
        /// Handle chat submission to allow longer messages and TV commands.
        /// </summary>
        [HarmonyPatch(typeof(HUDManager), "SubmitChat_performed")]
        [HarmonyPrefix]
        public static bool SubmitChat_performed_Prefix(HUDManager __instance, ref InputAction.CallbackContext context)
        {
            if (!context.performed) return false;

            __instance.localPlayer = GameNetworkManager.Instance.localPlayerController;

            if (__instance.localPlayer == null) return false;
            if (!__instance.localPlayer.isTypingChat) return false;
            if (!((__instance.localPlayer.IsOwner &&
                  (!__instance.IsServer || __instance.localPlayer.isHostPlayerObject)) ||
                  __instance.localPlayer.isTestingPlayer))
                return false;
            if (__instance.localPlayer.isPlayerDead) return false;

            if (!string.IsNullOrEmpty(__instance.chatTextField.text) && __instance.chatTextField.text.Length < 200)
            {
                __instance.AddTextToChatOnServer(__instance.chatTextField.text,
                    (int)__instance.localPlayer.playerClientId);
            }

            for (int i = 0; i < StartOfRound.Instance.allPlayerScripts.Length; i++)
            {
                if (StartOfRound.Instance.allPlayerScripts[i].isPlayerControlled &&
                    Vector3.Distance(
                        GameNetworkManager.Instance.localPlayerController.transform.position,
                        StartOfRound.Instance.allPlayerScripts[i].transform.position) > 24.4f &&
                    (!GameNetworkManager.Instance.localPlayerController.holdingWalkieTalkie ||
                     !StartOfRound.Instance.allPlayerScripts[i].holdingWalkieTalkie))
                {
                    __instance.playerCouldRecieveTextChatAnimator.SetTrigger("ping");
                    break;
                }
            }

            __instance.localPlayer.isTypingChat = false;
            __instance.chatTextField.text = "";
            EventSystem.current.SetSelectedGameObject(null);
            __instance.PingHUDElement(__instance.Chat, 2f, 1f, 0.2f);
            __instance.typingIndicator.enabled = false;

            return false; // Skip original
        }

        /// <summary>
        /// Monitor for yt-dlp readiness.
        /// </summary>
        [HarmonyPatch(typeof(HUDManager), "Update")]
        [HarmonyPrefix]
        public static void HUDUpdate_Prefix(HUDManager __instance)
        {
            // Nothing to do here — yt-dlp readiness is handled in tooltip
        }

        #region Command Processing

        private static readonly Regex UrlRegex = new Regex(
            @"^https?:\/\/(?:www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b(?:[-a-zA-Z0-9()@:%_\+.~#?&\/=]*)$",
            RegexOptions.Compiled);

        private static void ProcessCommand(HUDManager hud, string chatMessage, string senderName)
        {
            if (string.IsNullOrEmpty(chatMessage)) return;

            string[] parts = chatMessage.Split(' ');
            string cmd = parts[0].Replace("/", "").ToLower();
            var lang = Plugin.SyncConfig.LangData;
            var controller = TVController.Instance;

            switch (cmd)
            {
                case "thelp":
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.HelpText.Value, "TVSync");
                    break;

                case "tplay":
                    // Only the host processes playback commands.
                    // Any player can TYPE the command — the game's chat system
                    // broadcasts it to all clients, so the host always receives it.
                    if (!NetworkSync.IsHost) break;

                    if (parts.Length < 2)
                    {
                        ChatHelper.ShowMessage(hud, lang.InvalidUrl.Value, "TVSync");
                        break;
                    }

                    if (!UrlRegex.IsMatch(parts[1]))
                    {
                        ChatHelper.ShowMessage(hud, lang.InvalidUrl.Value, "TVSync");
                        break;
                    }

                    // Check it's a YouTube URL
                    string urlHost = parts[1].Contains("://") ? parts[1].Split(new[] { "://" }, StringSplitOptions.None)[1] : parts[1];
                    if (!(urlHost.Contains("youtube.com") || urlHost.Contains("youtu.be")))
                    {
                        ChatHelper.ShowMessage(hud, lang.InvalidUrl.Value, "TVSync");
                        break;
                    }

                    if (parts[1].Contains("list"))
                    {
                        ChatHelper.ShowMessage(hud, lang.InvalidUrl.Value, "TVSync");
                        break;
                    }

                    if (controller != null)
                    {
                        controller.HostPlayVideo(parts[1], hud, senderName);
                    }
                    break;

                case "ttime":
                    if (!NetworkSync.IsHost) break;
                    if (parts.Length < 2 || controller == null) break;

                    double seekTime = ParseTimeString(parts[1]);
                    if (seekTime >= 0)
                    {
                        controller.HostSeek(seekTime);
                        ChatHelper.ResetDedup();
                        ChatHelper.ShowMessage(hud, lang.TimeChanged.Value.Replace("@1", FormatTime(seekTime)), "TVSync");
                    }
                    break;

                case "treset":
                    if (!NetworkSync.IsHost || controller == null) break;
                    controller.HostRestart();
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.TimeChanged.Value.Replace("@1", "00:00:00"), "TVSync");
                    break;

                case "tpause":
                    if (!NetworkSync.IsHost || controller == null) break;
                    controller.HostPause();
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.Paused.Value, "TVSync");
                    break;

                case "tresume":
                    if (!NetworkSync.IsHost || controller == null) break;
                    controller.HostResume();
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.Resumed.Value, "TVSync");
                    break;

                case "tstop":
                    if (!NetworkSync.IsHost || controller == null) break;
                    controller.HostStop();
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.Stopped.Value, "TVSync");
                    break;

                case "tvolume":
                    if (parts.Length < 2) break;
                    // Volume is always client-side
                    if (senderName == GameNetworkManager.Instance.localPlayerController.playerUsername)
                    {
                        if (int.TryParse(parts[1], out int volPercent))
                        {
                            float vol = Mathf.Clamp01(volPercent / 100f);
                            if (controller != null)
                            {
                                controller.Volume = vol;
                                ChatHelper.ResetDedup();
                                ChatHelper.ShowMessage(hud,
                                    lang.VolumeChanged.Value
                                        .Replace("@1", senderName ?? "")
                                        .Replace("@2", parts[1] + "%"),
                                    "TVSync");
                            }
                        }
                        else
                        {
                            ChatHelper.ShowMessage(hud, lang.InvalidVolume.Value, "TVSync");
                        }
                    }
                    break;

                case "tposition":
                    if (parts.Length < 2 || controller == null) break;
                    if (parts[1].ToLower() == "true") controller.PositionMemory = true;
                    else if (parts[1].ToLower() == "false") controller.PositionMemory = false;
                    string state = controller.PositionMemory ? "enabled" : "disabled";
                    ChatHelper.ResetDedup();
                    ChatHelper.ShowMessage(hud, lang.MemoryToggle.Value.Replace("@1", state), "TVSync");
                    break;
            }
        }

        private static double ParseTimeString(string timeStr)
        {
            string[] parts = timeStr.Split(':');
            try
            {
                switch (parts.Length)
                {
                    case 1:
                        return Convert.ToDouble(parts[0]);
                    case 2:
                        return Convert.ToInt32(parts[0]) * 60 + Convert.ToInt32(parts[1]);
                    case 3:
                        return Convert.ToInt32(parts[0]) * 3600 + Convert.ToInt32(parts[1]) * 60 + Convert.ToInt32(parts[2]);
                    default:
                        return -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        public static string FormatTime(double seconds)
        {
            int total = (int)seconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            return $"{h:00}:{m:00}:{s:00}";
        }
    }

    /// <summary>
    /// Network lifecycle patches for registering/unregistering message handlers.
    /// </summary>
    public static class NetworkPatches
    {
        [HarmonyPatch(typeof(GameNetworkManager), "StartDisconnect")]
        [HarmonyPostfix]
        public static void StartDisconnect_Postfix()
        {
            NetworkSync.Unregister();
        }

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        public static void StartOfRound_Start_Postfix()
        {
            // Register network handlers when a round starts
            NetworkSync.Register();

            // Late join: request current state from host
            if (!NetworkSync.IsHost && TVController.Instance != null)
            {
                TVController.Instance.HandleLateJoin();
            }
        }

        /// <summary>
        /// Handle client connected (for late join support).
        /// </summary>
        [HarmonyPatch(typeof(StartOfRound), "OnPlayerConnectedClientRpc")]
        [HarmonyPostfix]
        public static void OnPlayerConnected_Postfix(ulong clientId)
        {
            if (NetworkSync.IsHost)
            {
                Plugin.Log.LogInfo($"TVSync: Client {clientId} connected. Will send state on request.");
            }
        }
    }
}
