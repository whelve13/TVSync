using BepInEx;
using BepInEx.Configuration;

namespace TVSync
{
    public class TVSyncConfig
    {
        public ConfigEntry<string> Language { get; private set; }
        public ConfigEntry<float> DriftThresholdMs { get; private set; }
        public ConfigEntry<float> SyncIntervalSec { get; private set; }
        public ConfigEntry<float> ReadyTimeoutSec { get; private set; }
        public ConfigEntry<float> MaxSeekThresholdMs { get; private set; }

        public Lang LangData { get; private set; }

        public void Init()
        {
            string configPath = System.IO.Path.Combine(Paths.ConfigPath, "TVSync.cfg");
            var cfg = new ConfigFile(configPath, true);

            Language = cfg.Bind("General", "Language", "en", "en = English, ru = Русский");
            DriftThresholdMs = cfg.Bind("Sync", "DriftThresholdMs", 150f,
                "Maximum drift in ms before correction is applied");
            SyncIntervalSec = cfg.Bind("Sync", "SyncIntervalSec", 3f,
                "How often the host broadcasts sync data (seconds)");
            ReadyTimeoutSec = cfg.Bind("Sync", "ReadyTimeoutSec", 30f,
                "Maximum time to wait for all clients to be ready (seconds)");
            MaxSeekThresholdMs = cfg.Bind("Sync", "MaxSeekThresholdMs", 500f,
                "Drift above this threshold causes a hard seek instead of speed adjustment");

            LangData = new Lang();
            string lang = Language.Value.ToLower();
            if (lang == "ru") LangData.InitRU();
            else LangData.InitEN();
        }
    }

    public class Lang
    {
        public ConfigEntry<string> LibLoading;
        public ConfigEntry<string> TVTooltip;
        public ConfigEntry<string> LibReady;
        public ConfigEntry<string> VideoLoading;
        public ConfigEntry<string> HelpText;
        public ConfigEntry<string> InvalidUrl;
        public ConfigEntry<string> PleaseWait;
        public ConfigEntry<string> VideoReady;
        public ConfigEntry<string> VolumeChanged;
        public ConfigEntry<string> InvalidVolume;
        public ConfigEntry<string> LinkInvalid;
        public ConfigEntry<string> TimeChanged;
        public ConfigEntry<string> MemoryToggle;
        public ConfigEntry<string> WaitingForPlayers;
        public ConfigEntry<string> AllPlayersReady;
        public ConfigEntry<string> SyncStarting;
        public ConfigEntry<string> PlayerTimedOut;
        public ConfigEntry<string> HostOnly;
        public ConfigEntry<string> Paused;
        public ConfigEntry<string> Resumed;
        public ConfigEntry<string> Stopped;

        public void InitEN()
        {
            var val = new ConfigFile("TVSync\\lang\\television_en.cfg", true);
            LibLoading = val.Bind("General", "Main_1", "Please wait, downloading libraries...");
            TVTooltip = val.Bind("General", "Main_2", "Toggle TV : [E]\n@2 - @3\n@1 volume\nVol Up [PG UP]\nVol Down [PG DN]");
            LibReady = val.Bind("General", "Main_3", "Libraries loaded.");
            VideoLoading = val.Bind("General", "Main_4", "Video is loading!");
            HelpText = val.Bind("General", "Main_5",
                "/tplay [LINK] - Play video\n/ttime [TIME] - Seek to time\n/treset - Restart\n/tpause - Pause\n/tresume - Resume\n/tstop - Stop\n/tvolume [0-100] - Volume\n/tposition [BOOL] - Remember position");
            InvalidUrl = val.Bind("General", "Main_6", "Invalid URL!");
            PleaseWait = val.Bind("General", "Main_7", "Please wait...");
            VideoReady = val.Bind("General", "Main_8", "Video loaded.");
            VolumeChanged = val.Bind("General", "Main_9", "@1 set volume to @2");
            InvalidVolume = val.Bind("General", "Main_10", "Invalid Volume!");
            LinkInvalid = val.Bind("General", "Main_11", "Link is invalid!");
            TimeChanged = val.Bind("General", "Main_12", "Time: @1");
            MemoryToggle = val.Bind("General", "Main_13", "Memory: @1");
            WaitingForPlayers = val.Bind("General", "Main_14", "Waiting for all players to be ready...");
            AllPlayersReady = val.Bind("General", "Main_15", "All players ready! Starting playback.");
            SyncStarting = val.Bind("General", "Main_16", "Synchronized playback starting...");
            PlayerTimedOut = val.Bind("General", "Main_17", "Timed out waiting for players. Starting anyway.");
            HostOnly = val.Bind("General", "Main_18", "Only the host can use this command.");
            Paused = val.Bind("General", "Main_19", "Playback paused.");
            Resumed = val.Bind("General", "Main_20", "Playback resumed.");
            Stopped = val.Bind("General", "Main_21", "Playback stopped.");
        }

        public void InitRU()
        {
            var val = new ConfigFile("TVSync\\lang\\television_ru.cfg", true);
            LibLoading = val.Bind("General", "Main_1", "Пожалуйста, подождите, загружаются библиотеки...");
            TVTooltip = val.Bind("General", "Main_2", "Вкл/Выкл ТВ : [E]\n@2 - @3\n@1 громкость\nГромче [PG UP]\nТише [PG DN]");
            LibReady = val.Bind("General", "Main_3", "Библиотеки загружены.");
            VideoLoading = val.Bind("General", "Main_4", "Видео загружается!");
            HelpText = val.Bind("General", "Main_5",
                "/tplay [ССЫЛКА] - Проиграть\n/ttime [ВРЕМЯ] - Перемотать\n/treset - Сброс\n/tpause - Пауза\n/tresume - Продолжить\n/tstop - Остановить\n/tvolume [0-100] - Громкость\n/tposition [BOOL] - Запомнить позицию");
            InvalidUrl = val.Bind("General", "Main_6", "Неверный URL!");
            PleaseWait = val.Bind("General", "Main_7", "Пожалуйста подождите...");
            VideoReady = val.Bind("General", "Main_8", "Видео готово.");
            VolumeChanged = val.Bind("General", "Main_9", "@1 изменил громкость @2");
            InvalidVolume = val.Bind("General", "Main_10", "Неверная громкость!");
            LinkInvalid = val.Bind("General", "Main_11", "Ссылка недействительная!");
            TimeChanged = val.Bind("General", "Main_12", "Позиция: @1");
            MemoryToggle = val.Bind("General", "Main_13", "Запоминание: @1");
            WaitingForPlayers = val.Bind("General", "Main_14", "Ожидание готовности всех игроков...");
            AllPlayersReady = val.Bind("General", "Main_15", "Все игроки готовы! Начинаем воспроизведение.");
            SyncStarting = val.Bind("General", "Main_16", "Синхронное воспроизведение начинается...");
            PlayerTimedOut = val.Bind("General", "Main_17", "Время ожидания игроков истекло. Начинаем.");
            HostOnly = val.Bind("General", "Main_18", "Только хост может использовать эту команду.");
            Paused = val.Bind("General", "Main_19", "Воспроизведение приостановлено.");
            Resumed = val.Bind("General", "Main_20", "Воспроизведение продолжено.");
            Stopped = val.Bind("General", "Main_21", "Воспроизведение остановлено.");
        }
    }
}
