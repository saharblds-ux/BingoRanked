using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Celeste.Mod;
using Celeste.Mod.BingoElo.Net;
using Celeste.Pico8;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.BingoElo {
    public class BingoEloModule : EverestModule {

        public static BingoEloModule Instance { get; private set; }

        // practice-vs-bot, dev only. Flip and rebuild to bring back the menu entry and bingoelo_train.
        public const bool TrainingDummyEnabled = false;

        public override Type SettingsType => typeof(BingoEloSettings);
        public static BingoEloSettings Settings => (BingoEloSettings) Instance._Settings;

        // work from network tasks that has to run on the game thread
        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        public BingoEloModule() {
            Instance = this;
            Logger.SetLogLevel(nameof(BingoEloModule), LogLevel.Info);
        }

        public override void Load() {
            On.Monocle.Engine.Update += OnEngineUpdate;
            On.Monocle.Scene.Begin += OnSceneBegin;
            On.Celeste.Level.Update += OnLevelUpdate;
            On.Celeste.LevelLoader.Update += OnLevelLoaderUpdate;
            On.Celeste.IntroVignette.Update += OnIntroVignetteUpdate;
            UI.MainMenuEntry.Load();
            BingoSave.Load();
            Logger.Log(LogLevel.Info, nameof(BingoEloModule), "BingoElo loaded");
        }

        public override void Unload() {
            On.Monocle.Engine.Update -= OnEngineUpdate;
            On.Monocle.Scene.Begin -= OnSceneBegin;
            On.Celeste.Level.Update -= OnLevelUpdate;
            On.Celeste.LevelLoader.Update -= OnLevelLoaderUpdate;
            On.Celeste.IntroVignette.Update -= OnIntroVignetteUpdate;
            UI.MainMenuEntry.Unload();
            BingoSave.Unload();
        }

        // Has to run before Level.Update, which checks for pause before entities update.
        private static void OnLevelUpdate(On.Celeste.Level.orig_Update orig, Level self) {
            if (UI.GameControlLock.Held) {
                // parking can't engage mid-dash, so blank the inputs too
                UI.GameControlLock.Tick(self);
                UI.GameControlLock.SuppressGameplayInput();
            }
            orig(self);
        }

        // LevelLoader never updates its entity list, so drive our overlays by hand.
        // EntityList.Update is internal, hence the three explicit calls.
        private static void OnLevelLoaderUpdate(On.Celeste.LevelLoader.orig_Update orig, LevelLoader self) {
            orig(self);
            self.Entities.UpdateLists();
            UI.BingoBoardUI.Instance?.Update();
            UI.BingoMenuUI.Instance?.Update();
            UI.BingoChatUI.Instance?.Update();
        }

        // same as LevelLoader, for a chapter's scripted opening line
        private static void OnIntroVignetteUpdate(On.Celeste.IntroVignette.orig_Update orig, IntroVignette self) {
            orig(self);
            self.Entities.UpdateLists();
            UI.BingoBoardUI.Instance?.Update();
            UI.BingoMenuUI.Instance?.Update();
            UI.BingoChatUI.Instance?.Update();
        }

        // overlays are per-scene, attach them to every scene a player can be in mid-race
        private static void OnSceneBegin(On.Monocle.Scene.orig_Begin orig, Scene self) {
            orig(self);
            if (self is Level || self is Overworld || self is LevelLoader ||
                self is LevelExit || self is LevelEnter || self is Emulator ||
                self is IntroVignette) {
                UI.GameControlLock.ForgetForNewScene();
                self.Add(new UI.BingoBoardUI());
                self.Add(new UI.BingoMenuUI());
                self.Add(new UI.BingoChatUI());
            }
        }

        public override void Initialize() {
            MatchState.MatchFinished += result => Echo(result);
            if (Settings.ConnectOnStartup)
                Connect(quiet: true);
        }

        // the server drops queue entries after 60s of silence
        private const float HeartbeatInterval = 15f;
        private static float heartbeatTimer;

        // match-ended check while the board is closed
        private const float MatchWatchInterval = 6f;
        private static float matchWatchTimer;

        // A challenger isn't queued and has no match yet, so nothing else would
        // tell them the target accepted.
        private const float MatchDiscoveryInterval = 15f;
        private static float matchDiscoveryTimer;

        private static void OnEngineUpdate(On.Monocle.Engine.orig_Update orig, Engine self, GameTime gameTime) {
            // every scene, the Level.Update hook never runs on the overworld
            UI.GameControlLock.Tick(Engine.Scene);

            // PICO-8's Emulator never runs Level.Update either
            if (UI.GameControlLock.Held)
                UI.GameControlLock.SuppressGameplayInput();

            orig(self, gameTime);

            // every frame so both boards reveal at the same instant
            MatchState.TickCountdown();

            MatchState.TickGoalVerification();

            // These watch state Celeste doesn't keep, so they have to run every frame.
            MatchState.TickOneUps();
            MatchState.TickPico8();
            MatchState.TickSeekerKills();
            MatchState.TickSegments();

            BingoSave.Tick();

            while (MainThreadQueue.TryDequeue(out Action action)) {
                try {
                    action();
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, nameof(BingoEloModule), $"main-thread work failed: {e}");
                }
            }

            if (LadderService.IsQueued) {
                heartbeatTimer -= Engine.RawDeltaTime;
                if (heartbeatTimer <= 0f) {
                    heartbeatTimer = HeartbeatInterval;
                    SupabaseClient.FireAndForget(async () => {
                        if (!await LadderService.HeartbeatAsync()) {
                            // matched or reaped
                            string matchId = await LadderService.GetActiveMatchIdAsync();
                            Echo(string.IsNullOrEmpty(matchId)
                                ? "you were removed from the queue"
                                : "match found! press your board key");
                            if (!string.IsNullOrEmpty(matchId))
                                await MatchState.RefreshAsync();
                        }
                    }, "queue heartbeat");
                }
            }

            // neither queued nor in a match, e.g. waiting on a challenge
            if (!LadderService.IsQueued && !MatchState.HasMatch) {
                matchDiscoveryTimer -= Engine.RawDeltaTime;
                if (matchDiscoveryTimer <= 0f) {
                    matchDiscoveryTimer = MatchDiscoveryInterval;
                    SupabaseClient.FireAndForget(async () => {
                        string matchId = await LadderService.GetActiveMatchIdAsync();
                        if (!string.IsNullOrEmpty(matchId)) {
                            Echo("match found! press your board key");
                            await MatchState.RefreshAsync();
                        }
                    }, "match discovery");
                }
            }

            // notice the match ending even with the board closed
            if (!MatchState.HasMatch || MatchState.Finished)
                return;

            matchWatchTimer -= Engine.RawDeltaTime;
            if (matchWatchTimer > 0f)
                return;

            matchWatchTimer = MatchWatchInterval;
            string matchId = MatchState.MatchId;
            SupabaseClient.FireAndForget(async () => {
                // heartbeat first, it also collects forfeits
                bool forfeit = await LadderService.MatchHeartbeatAsync(matchId);
                if (forfeit)
                    Echo("opponent left, match awarded to you");
                await MatchState.RefreshLiveAsync();
            }, "match heartbeat");
        }

        public static void OnMainThread(Action action) => MainThreadQueue.Enqueue(action);

        public static void Echo(string message) => OnMainThread(() => Engine.Commands.Log(message));

        // local only, the match keeps running for the opponent
        public static void LeaveRoom() {
            MatchState.LeaveRoom();
            ChatService.Clear();
            BingoSave.Reset();
            OnMainThread(() => {
                UI.BingoChatUI.Instance?.ForceClose();
                UI.BingoBoardUI.Instance?.ForceClose();
            });
        }

        private static void Connect(bool quiet) {
            SupabaseClient.FireAndForget(async () => {
                Profile me = await LadderService.ConnectAsync();
                Logger.Log(LogLevel.Info, nameof(BingoEloModule), $"connected as {me}");
                if (!quiet)
                    Echo($"Connected as {me}");
            }, "connect");
        }

        [Command("bingoelo", "checks that BingoElo is loaded and responsive")]
        public static void CmdBingoElo() {
            Engine.Commands.Log("BingoElo is alive.");
            Engine.Commands.Log(SupabaseAuth.IsSignedIn
                ? $"signed in as {SupabaseAuth.UserId}"
                : "not signed in yet, try bingoelo_connect");
        }

        [Command("bingoelo_connect", "signs in to the ladder and creates a profile if needed")]
        public static void CmdConnect() {
            Engine.Commands.Log("connecting...");
            Connect(quiet: false);
        }

        [Command("bingoelo_whoami", "shows your ladder profile as the server sees it")]
        public static void CmdWhoAmI() {
            SupabaseClient.FireAndForget(async () => {
                Profile me = await LadderService.ConnectAsync();
                Echo(me == null ? "no profile" : $"{me}\nid: {me.Id}");
            }, "whoami");
        }

        [Command("bingoelo_name", "changes your display name on the ladder")]
        public static void CmdName(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                Engine.Commands.Log("usage: bingoelo_name <name>");
                return;
            }
            SupabaseClient.FireAndForget(async () => {
                Profile me = await LadderService.RenameAsync(name);
                Echo($"renamed to {me?.RankedName}");
            }, "rename");
        }

        [Command("bingoelo_queue", "joins the matchmaking queue")]
        public static void CmdQueue() {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                await LadderService.JoinQueueAsync();
                int size = await LadderService.GetQueueSizeAsync();
                Echo($"joined the queue ({size} waiting)");
            }, "join queue");
        }

        [Command("bingoelo_unqueue", "leaves the matchmaking queue")]
        public static void CmdUnqueue() {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.LeaveQueueAsync();
                Echo("left the queue");
            }, "leave queue");
        }

        [Command("bingoelo_top", "shows the top rated players")]
        public static void CmdTop() {
            SupabaseClient.FireAndForget(async () => {
                Profile[] top = await LadderService.GetLeaderboardAsync();
                if (top == null || top.Length == 0) {
                    Echo("no players yet");
                    return;
                }
                for (int i = 0; i < top.Length; i++)
                    Echo($"{i + 1}. {top[i]}");
            }, "leaderboard");
        }

        [Command("bingoelo_match", "asks the server to pair waiting players")]
        public static void CmdMatch() {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                string id = await LadderService.TryMatchmakeAsync();
                Echo(string.IsNullOrEmpty(id)
                    ? "nobody to pair with yet"
                    : $"match created: {id}");
            }, "matchmake");
        }

        [Command("bingoelo_board", "prints your current match board")]
        public static void CmdBoard() {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                string matchId = await LadderService.GetActiveMatchIdAsync();
                if (string.IsNullOrEmpty(matchId)) {
                    Echo("you're not in a match");
                    return;
                }

                Match match = await LadderService.GetMatchAsync(matchId);
                BoardTile[] tiles = await LadderService.GetBoardAsync(match.BoardId);
                TileClaim[] claims = await LadderService.GetClaimsAsync(matchId);

                string me = SupabaseAuth.UserId;
                int mine = 0;
                foreach (TileClaim c in claims)
                    if (c.PlayerId == me)
                        mine++;

                Echo($"-- {match.Mode}, first to {match.WinTarget} (you have {mine}) --");
                foreach (BoardTile tile in tiles) {
                    string mark = " ";
                    foreach (TileClaim c in claims) {
                        if (c.TilePosition == tile.Position) {
                            mark = c.PlayerId == me ? "*" : "x";
                            break;
                        }
                    }
                    Echo($"[{mark}] {tile.Position,2}  T{tile.Goal?.Tier,-2} {tile.Goal?.Text}");
                }
            }, "board");
        }

        [Command("bingoelo_claim", "claims a square of your current board by position (0-24)")]
        public static void CmdClaim(int position) {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                string matchId = await LadderService.GetActiveMatchIdAsync();
                if (string.IsNullOrEmpty(matchId)) {
                    Echo("you're not in a match");
                    return;
                }

                try {
                    ClaimResult result = await LadderService.ClaimTileAsync(matchId, position);
                    Echo(result.Won
                        ? $"claimed {result.Position} ({result.Claimed}/{result.Target}). YOU WIN!"
                        : $"claimed {result.Position} ({result.Claimed}/{result.Target})");
                } catch (SupabaseException e) {
                    // show the server's reason as is
                    Echo($"claim refused: {e.Body}");
                }
            }, "claim");
        }

        [Command("bingoelo_train", "starts a practice match against the training dummy (no ELO)")]
        public static void CmdTrain() {
            if (!TrainingDummyEnabled) {
                Engine.Commands.Log("training dummy is disabled in this build");
                return;
            }
            Engine.Commands.Log("spawning training dummy...");
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                string id = await LadderService.SpawnTrainingMatchAsync();
                if (string.IsNullOrEmpty(id)) {
                    Echo("couldn't start a training match");
                    return;
                }
                await MatchState.RefreshAsync();
                Echo("training match ready, open your board. No ELO at stake.");
            }, "training match");
        }

        [Command("bingoelo_bot", "makes the training dummy claim a square (0-24)")]
        public static void CmdBotClaim(int position) {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                string matchId = await LadderService.GetActiveMatchIdAsync();
                if (string.IsNullOrEmpty(matchId)) {
                    Echo("you're not in a match");
                    return;
                }
                try {
                    ClaimResult result = await LadderService.TrainingBotClaimAsync(matchId, position);
                    Echo($"dummy claimed {result.Position} ({result.Claimed}/{result.Target})");
                } catch (SupabaseException e) {
                    Echo($"dummy couldn't claim: {e.Body}");
                }
                await MatchState.RefreshLiveAsync();
            }, "bot claim");
        }

        [Command("bingoelo_challenge", "challenges a specific player by name to an unranked match")]
        public static void CmdChallenge(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                Engine.Commands.Log("usage: bingoelo_challenge <name>");
                return;
            }
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                try {
                    await LadderService.ChallengePlayerAsync(name);
                    Echo($"challenged {name}, waiting for them to accept (bingoelo_accept)");
                } catch (SupabaseException e) {
                    Echo($"couldn't challenge {name}: {e.Body}");
                }
            }, "challenge");
        }

        [Command("bingoelo_challenges", "lists players who have challenged you")]
        public static void CmdChallenges() {
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                Challenge[] incoming = await LadderService.GetIncomingChallengesAsync();
                if (incoming == null || incoming.Length == 0) {
                    Echo("no pending challenges");
                    return;
                }
                foreach (Challenge c in incoming)
                    Echo($"challenged by {c.FromProfile?.RankedName}");
            }, "list challenges");
        }

        [Command("bingoelo_accept", "accepts a pending challenge from a specific player (no ELO)")]
        public static void CmdAccept(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                Engine.Commands.Log("usage: bingoelo_accept <name>");
                return;
            }
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                Challenge target = await FindIncomingChallengeAsync(name);
                if (target == null) {
                    Echo($"no pending challenge from {name}");
                    return;
                }
                try {
                    string id = await LadderService.AcceptChallengeAsync(target.FromPlayer);
                    if (string.IsNullOrEmpty(id)) {
                        Echo("couldn't start the match");
                        return;
                    }
                    await MatchState.RefreshAsync();
                    Echo($"match with {target.FromProfile?.RankedName} ready, open your board. No ELO at stake.");
                } catch (SupabaseException e) {
                    Echo($"couldn't accept: {e.Body}");
                }
            }, "accept challenge");
        }

        [Command("bingoelo_decline", "declines a pending challenge from a specific player")]
        public static void CmdDecline(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                Engine.Commands.Log("usage: bingoelo_decline <name>");
                return;
            }
            SupabaseClient.FireAndForget(async () => {
                await LadderService.ConnectAsync();
                Challenge target = await FindIncomingChallengeAsync(name);
                if (target == null) {
                    Echo($"no pending challenge from {name}");
                    return;
                }
                await LadderService.DeclineChallengeAsync(target.FromPlayer);
                Echo($"declined {target.FromProfile?.RankedName}");
            }, "decline challenge");
        }

        private static async Task<Challenge> FindIncomingChallengeAsync(string name) {
            Challenge[] incoming = await LadderService.GetIncomingChallengesAsync();
            if (incoming == null) return null;
            foreach (Challenge c in incoming)
                if (string.Equals(c.FromProfile?.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        [Command("bingoelo_logout", "forgets the cached identity (development only)")]
        public static void CmdLogout() {
            SupabaseAuth.SignOut();
            Engine.Commands.Log("signed out; next command will sign in fresh");
        }
    }
}
