using System;
using System.Linq;
using System.Reflection;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.BingoElo {

    // The mod's own save file, bingo.celeste. It never uses a numbered slot since
    // any of those could be a real save. Slot -53 is hooked to the filename
    // "bingo", picked to stay clear of -1 (debug) and -2 which other mods might use.
    public static class BingoSave {

        public const int Slot = -53;

        private const string FileName = "bingo";

        private static string startedForMatch;
        private static string seenMatch;

        public static void Load() {
            // _int picks the GetFilename(slot) overload
            On.Celeste.SaveData.GetFilename_int += OnGetFilename;
        }

        public static void Unload() {
            On.Celeste.SaveData.GetFilename_int -= OnGetFilename;
        }

        private static string OnGetFilename(On.Celeste.SaveData.orig_GetFilename_int orig, int slot)
            => slot == Slot ? FileName : orig(slot);

        public static void Reset() => startedForMatch = null;

        // Starts a fresh run at GO if autostart is on. Runs every frame, fires once per match.
        public static void Tick() {
            BingoEloSettings settings = BingoEloModule.Settings;
            if (settings == null || !settings.AutoStartOnGo)
                return;

            if (!MatchState.HasMatch || MatchState.Finished)
                return;

            // mid-load the race always looks like it hasn't started
            if (!MatchState.FullyLoaded)
                return;

            string matchId = MatchState.MatchId;

            // If the race was already running when we first saw it (reconnect,
            // or launching mid-match), that isn't our GO. Otherwise opening the
            // game during a match would wipe the save and start a run.
            if (seenMatch != matchId) {
                seenMatch = matchId;
                if (MatchState.HasStarted)
                    startedForMatch = matchId;
            }

            if (!MatchState.HasStarted)
                return;

            if (startedForMatch == matchId)
                return;

            Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                $"autostart firing at GO (elapsed={MatchState.CountdownElapsed:F1}s)");

            startedForMatch = matchId;
            StartFreshRun();
        }

        public static bool RunExists() {
            try {
                return UserIO.Exists(SaveData.GetFilename(Slot));
            } catch {
                return false;
            }
        }

        // Resumes the run after Save & Quit instead of restarting it.
        public static void ReturnToRun() {
            Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                $"return to run: scene={Engine.Scene?.GetType().Name} started={MatchState.HasStarted}");

            if (Engine.Scene is Level) {
                MatchState.SetStatus("you're already in your run");
                return;
            }

            // would be a way to play before GO
            if (!MatchState.HasStarted) {
                MatchState.SetStatus("the race hasn't started yet");
                return;
            }

            try {
                SaveData data = null;
                if (UserIO.Open(UserIO.Mode.Read)) {
                    try {
                        data = UserIO.Load<SaveData>(SaveData.GetFilename(Slot), false);
                    } finally {
                        UserIO.Close();
                    }
                }

                if (data == null) {
                    // probably quit during the opening cutscene, nothing to lose
                    Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                        "no bingo save to resume; starting a fresh run instead");
                    StartFreshRun();
                    return;
                }

                data.AfterInitialize();
                SaveData.Start(data, Slot);

                Session session = SaveData.Instance.CurrentSession_Safe;
                if (session != null && session.InArea) {
                    LevelEnter.Go(session, true);
                } else {
                    // saved from the overworld, nothing to resume into
                    LevelEnter.Go(new Session(new AreaKey(0)), false);
                }
            } catch (Exception e) {
                Logger.Log(LogLevel.Error, nameof(BingoEloModule), $"could not return to the run: {e}");
                MatchState.SetStatus("couldn't return to your run");
            }
        }

        // Wipes bingo.celeste and starts a new run on it with variant mode on.
        public static void StartFreshRun() {
            try {
                Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                    $"starting a fresh run on {FileName}.celeste");

                SaveData.TryDelete(Slot);

                SaveData data = new SaveData {
                    Name = string.IsNullOrWhiteSpace(BingoEloModule.Settings.DisplayName)
                        ? "BINGO"
                        : BingoEloModule.Settings.DisplayName,
                    AssistMode = false,
                    VariantMode = true,
                };

                SaveData.Start(data, Slot);
                ApplyBingoUiProgression();

                // false = new file, true would skip the prologue cutscene
                LevelEnter.Go(new Session(new AreaKey(0)), false);
            } catch (Exception e) {
                Logger.Log(LogLevel.Error, nameof(BingoEloModule),
                    $"could not start the bingo run: {e}");
                MatchState.SetStatus("couldn't auto-start, begin your run manually");
            }
        }

        // BingoUI applies its custom progression in the file-select "new game"
        // hook, which we skip, so do the same thing here through reflection.
        private static void ApplyBingoUiProgression() {
            try {
                EverestModule bingoUi = Everest.Modules
                    .FirstOrDefault(m => m.Metadata?.Name == "BingoUI");
                if (bingoUi == null) {
                    Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                        "BingoUI progression: not installed, skipping");
                    return;
                }

                Type moduleType = bingoUi.GetType();
                PropertyInfo settingsProp = moduleType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo saveDataProp = moduleType.GetProperty("SaveData", BindingFlags.Public | BindingFlags.Static);
                Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                    $"BingoUI progression: module type={moduleType.FullName}, Settings prop found={settingsProp != null}, SaveData prop found={saveDataProp != null}");

                object settings = settingsProp?.GetValue(null);
                object saveData = saveDataProp?.GetValue(null);
                if (settings == null || saveData == null) {
                    Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                        $"BingoUI progression: settings null={settings == null}, saveData null={saveData == null}, aborting");
                    return;
                }

                bool enabled = (bool)settings.GetType().GetProperty("Enabled").GetValue(settings);
                Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                    $"BingoUI progression: BingoUI.Settings.Enabled={enabled}");
                if (!enabled)
                    return;

                object progression = settings.GetType().GetProperty("CustomProgression").GetValue(settings);
                saveData.GetType().GetField("CustomProgression").SetValue(saveData, progression);

                SaveData.Instance.SetFlag("BINGO");
                if (progression.ToString() == "CheatMode")
                    SaveData.Instance.CheatMode = true;

                Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                    $"applied BingoUI custom progression '{progression}' to the autostarted run");
            } catch (Exception e) {
                // don't let a BingoUI version mismatch break autostart
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    $"could not apply BingoUI progression: {e}");
            }
        }
    }
}
