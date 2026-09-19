using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Celeste.Mod.BingoElo.Net;
using Celeste.Pico8;
using Microsoft.Xna.Framework;
using Monocle;
using Newtonsoft.Json.Linq;

namespace Celeste.Mod.BingoElo {

    // The current match, cached so the UI doesn't hit the network every frame.
    public static class MatchState {

        public static string MatchId { get; private set; }
        public static string BoardId { get; private set; }
        public static string Mode { get; private set; } = "lockout";
        public static int WinTarget { get; private set; } = 13;
        public static bool IsTraining { get; private set; }

        // Unranked like training, but with a real opponent, so it must not skip ready-up.
        public static bool IsPrivate { get; private set; }

        public static BoardTile[] Tiles { get; private set; }
        public static bool HasMatch => MatchId != null && Tiles != null;

        // HasMatch turns true before the live fetch finishes, when CountdownStartedAt
        // is still null. Anything asking "has the race started?" has to wait for this.
        public static bool FullyLoaded { get; private set; }

        // client-side only, the server doesn't care whether you've looked
        public static bool Revealed { get; set; }

        public static readonly HashSet<int> StarredTiles = new HashSet<int>();

        // tile position -> player who claimed it
        private static readonly Dictionary<int, string> ClaimedBy = new Dictionary<int, string>();

        // so each ready notice fires once
        private static readonly HashSet<int> NotifiedReady = new HashSet<int>();

        // 1-Up chains (StrawberryCollectIndex >= 5) reset a couple of seconds after
        // the last berry and never reach the save, so TickOneUps catches the rising edge.
        private static readonly HashSet<int> OneUpAreas = new HashSet<int>();
        private static int OneUpCount;
        private static bool oneUpWasChaining;

        // PICO-8 state is a fresh emulator each time the console is entered and never
        // saved. Sticky so leaving and re-entering doesn't erase what was reached.
        private static int picoMaxFruit;
        private static bool picoAtOldSite;
        private static bool picoHasOrb;
        private static bool picoCompleted;

        // Squished seekers leave no trace, unlike stunned ones (BingoUI counts those).
        // Keyed by reference so a seeker isn't counted twice.
        private static readonly HashSet<Seeker> KilledSeekers = new HashSet<Seeker>();

        // One entry per (goal, segment) for the dashless/grabless goals. A segment arms
        // at its start room (or immediately with no start) and freezes at its end room
        // (or when the chapter is completed with no end). Rock Bottom has two segments,
        // either of which counts.
        private class SegmentProgress {
            public bool Armed;
            public bool Completed;
            public bool DashUsed;
            public bool GrabViolated;
        }
        private static readonly Dictionary<string, SegmentProgress> Segments = new Dictionary<string, SegmentProgress>();

        public static int MyClaims { get; private set; }
        public static int TheirClaims { get; private set; }

        public static bool Finished { get; private set; }
        public static bool IWon { get; private set; }
        public static string ResultText { get; private set; }

        public static string Status { get; private set; } = "not connected";
        public static void SetStatus(string status) => Status = status;

        // Leaving is local only and the match carries on for the opponent, so remember
        // it or the next refresh would pull us back in.
        public static string AbandonedMatchId { get; private set; }

        // lets a leftover practice match be cleaned up once per launch
        private static bool firstRefreshOfSession = true;

        // without this the first poll would announce every existing claim as new
        private static bool hasClaimSnapshot;

        public static void LeaveRoom() {
            AbandonedMatchId = MatchId;
            Clear();
            Status = "left the room";
        }

        public static void AllowRejoin() => AbandonedMatchId = null;

        // raised on the game thread
        public static event Action<string> MatchFinished;

        public static bool IAmReady { get; private set; }
        public static bool OpponentReady { get; private set; }
        public static bool EveryoneReady => IAmReady && (OpponentReady || IsTraining);

        // server time, null if not started
        public static DateTime? CountdownStartedAt { get; private set; }

        // Both clients render this from the same server timestamp so they stay in step.
        public static readonly (double At, string Text)[] CountdownScript = {
            (0,  "reveal in 5"),
            (1,  "4"),
            (2,  "3"),
            (3,  "2"),
            (4,  "1"),
            (5,  "REVEAL"),
            (8,  "go in 5"),
            (9,  "4"),
            (10, "3"),
            (11, "2"),
            (12, "1"),
            (13, "GO"),
        };

        public const double RevealAt = 5;
        public const double GoAt = 13;
        private const double CountdownVisibleFor = 18;

        public static double? CountdownElapsed {
            get {
                if (CountdownStartedAt == null)
                    return null;
                return (SupabaseClient.ServerNow - CountdownStartedAt.Value).TotalSeconds;
            }
        }

        public static bool CountdownRunning {
            get {
                double? e = CountdownElapsed;
                return e != null && e.Value >= -1 && e.Value < CountdownVisibleFor;
            }
        }

        public static bool HasStarted {
            get {
                double? e = CountdownElapsed;
                return e != null && e.Value >= GoAt;
            }
        }

        public static void TickCountdown() {
            if (!BingoEloModule.Settings.AutoRevealOnCountdown)
                return;

            double? elapsed = CountdownElapsed;
            if (elapsed != null && elapsed.Value >= RevealAt)
                Revealed = true;
        }

        // Local-only chat notice when an unclaimed tile's goal flips to Ready.
        // Waits for Revealed so the notice can't spoil the board.
        public static void TickGoalVerification() {
            if (!HasMatch || Finished || !Revealed || Tiles == null)
                return;

            foreach (BoardTile tile in Tiles) {
                if (tile.Goal == null)
                    continue;

                if (OwnerOf(tile.Position) != null) {
                    // reset so it can notify again after an unclaim
                    NotifiedReady.Remove(tile.Position);
                    continue;
                }

                if (NotifiedReady.Contains(tile.Position))
                    continue;

                if (GoalVerification.Check(tile.Goal) == GoalVerification.Status.Ready) {
                    NotifiedReady.Add(tile.Position);
                    ChatService.SetLocalNotice($"ready to claim: {tile.Goal.Text}");
                }
            }
        }

        // Not gated on Revealed: the moment is lost if it isn't caught live.
        public static void TickOneUps() {
            if (!HasMatch || Finished || !(Engine.Scene is Level level)) {
                oneUpWasChaining = false;
                return;
            }

            Player player = level.Tracker.GetEntity<Player>();
            bool chaining = player != null && player.StrawberryCollectIndex >= 5;
            if (chaining && !oneUpWasChaining) {
                OneUpAreas.Add(level.Session.Area.ID);
                OneUpCount++;
            }
            oneUpWasChaining = chaining;
        }

        public static bool HasOneUpIn(int area) => OneUpAreas.Contains(area);
        public static int OneUpAreaCount => OneUpAreas.Count;
        public static int TotalOneUps => OneUpCount;

        // Old Site is room (3,1); the flag object only
        // spawns once the run is won.
        public static void TickPico8() {
            if (!HasMatch || Finished || !(Engine.Scene is Emulator emulator))
                return;

            try {
                Classic game = PicoFields.Game(emulator);
                if (game == null) return;

                int fruit = PicoFields.FruitCount(game);
                if (fruit > picoMaxFruit) picoMaxFruit = fruit;

                Point room = PicoFields.Room(game);
                if (room.X == 3 && room.Y == 1) picoAtOldSite = true;

                if (PicoFields.MaxDjump(game) >= 2) picoHasOrb = true;

                if (!picoCompleted && PicoFields.HasFlag(game)) picoCompleted = true;
            } catch {
                // private fields, may differ between game versions
            }
        }

        public static int PicoFruitCount => picoMaxFruit;
        public static bool PicoAtOldSite => picoAtOldSite;
        public static bool PicoHasOrb => picoHasOrb;
        public static bool PicoCompleted => picoCompleted;

        // Classic's fields are private; they're Celeste's own so they're assumed to exist.
        private static class PicoFields {
            private static readonly FieldInfo GameField =
                typeof(Emulator).GetField("game", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly FieldInfo GotFruitField =
                typeof(Classic).GetField("got_fruit", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly FieldInfo RoomField =
                typeof(Classic).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly FieldInfo MaxDjumpField =
                typeof(Classic).GetField("max_djump", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly FieldInfo ObjectsField =
                typeof(Classic).GetField("objects", BindingFlags.NonPublic | BindingFlags.Instance);

            public static Classic Game(Emulator e) => (Classic) GameField.GetValue(e);
            public static int FruitCount(Classic g) => ((HashSet<int>) GotFruitField.GetValue(g)).Count;
            public static Point Room(Classic g) => (Point) RoomField.GetValue(g);
            public static int MaxDjump(Classic g) => (int) MaxDjumpField.GetValue(g);

            public static bool HasFlag(Classic g) {
                var objects = (System.Collections.IEnumerable) ObjectsField.GetValue(g);
                foreach (object o in objects)
                    if (o is Classic.flag) return true;
                return false;
            }
        }

        private static readonly FieldInfo SeekerDeadField =
            typeof(Seeker).GetField("dead", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void TickSeekerKills() {
            if (!HasMatch || Finished || !(Engine.Scene is Level level) || SeekerDeadField == null)
                return;

            try {
                foreach (Entity entity in level.Tracker.GetEntities<Seeker>()) {
                    var seeker = (Seeker) entity;
                    if (SeekerDeadField.GetValue(seeker) is bool dead && dead)
                        KilledSeekers.Add(seeker);
                }
            } catch {
                // private fields, may differ between game versions
            }
        }

        public static int SeekerKillCount => KilledSeekers.Count;

        // Runs every frame whether or not the board is open.
        public static void TickSegments() {
            if (!HasMatch || Finished || Tiles == null || SaveData.Instance == null || !(Engine.Scene is Level level))
                return;

            int area = level.Session.Area.ID;
            int side = (int) level.Session.Area.Mode;
            string room = level.Session.Level;
            Player player = level.Tracker.GetEntity<Player>();
            bool dashing = player != null && player.StateMachine.State == Player.StDash;
            bool grabVariantOff = !SaveData.Instance.Assists.NoGrabbing;

            foreach (BoardTile tile in Tiles) {
                Goal goal = tile.Goal;
                if (goal == null || goal.CheckParams == null) continue;
                if (goal.CheckKind != "dashless_segment" && goal.CheckKind != "grabless_segment") continue;
                if (!(goal.CheckParams["segments"] is JArray segs)) continue;

                for (int i = 0; i < segs.Count; i++) {
                    JToken seg = segs[i];
                    int gArea = (int) (seg["area"] ?? -1);
                    int gSide = (int) (seg["side"] ?? 0);
                    if (area != gArea || side != gSide) continue;

                    string key = goal.Id + ":" + i;
                    if (!Segments.TryGetValue(key, out SegmentProgress p)) {
                        p = new SegmentProgress();
                        Segments[key] = p;
                    }
                    if (p.Completed) continue;

                    string start = (string) seg["start"];
                    if (!p.Armed && (start == null || room == start))
                        p.Armed = true;
                    if (!p.Armed) continue;

                    if (dashing) p.DashUsed = true;
                    if (grabVariantOff) p.GrabViolated = true;

                    string end = (string) seg["end"];
                    bool reachedEnd = end != null ? room == end : AreaCompleted(gArea, gSide);
                    if (reachedEnd) p.Completed = true;
                }
            }
        }

        private static bool AreaCompleted(int area, int side) {
            var areas = SaveData.Instance?.Areas;
            if (areas == null || area < 0 || area >= areas.Count) return false;
            var modes = areas[area].Modes;
            if (modes == null || side < 0 || side >= modes.Length) return false;
            return modes[side].Completed;
        }

        // true if any of the goal's segments was cleared
        public static bool SegmentClear(Goal goal, bool dashless) {
            if (!(goal.CheckParams?["segments"] is JArray segs)) return false;
            for (int i = 0; i < segs.Count; i++) {
                if (Segments.TryGetValue(goal.Id + ":" + i, out SegmentProgress p) && p.Completed
                    && (dashless ? !p.DashUsed : !p.GrabViolated))
                    return true;
            }
            return false;
        }

        public static async Task RefreshAsync() {
            await LadderService.ConnectAsync().ConfigureAwait(false);

            // consumed here so a first refresh that finds nothing doesn't stay armed
            bool isFirstRefresh = firstRefreshOfSession;
            firstRefreshOfSession = false;

            string matchId = await LadderService.GetActiveMatchIdAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(matchId)) {
                // a finished match isn't "active", but the player still wants to see the result
                if (Finished && MatchId != null) {
                    Status = ResultText;
                    return;
                }
                Clear();
                Status = "no active match";
                return;
            }

            if (matchId == AbandonedMatchId) {
                Clear();
                Status = "left the room";
                return;
            }

            // A practice match from a previous session is abandoned by definition.
            // Ranked is left alone so relaunching within the forfeit window rejoins.
            if (isFirstRefresh) {
                Match check = await LadderService.GetMatchAsync(matchId).ConfigureAwait(false);
                if (check != null && check.IsTraining) {
                    Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                        "ending a practice match left over from a previous session");
                    await LadderService.EndTrainingAsync().ConfigureAwait(false);
                    Clear();
                    Status = "no active match";
                    return;
                }
            }

            bool isNewMatch = matchId != MatchId;

            Match match = await LadderService.GetMatchAsync(matchId).ConfigureAwait(false);
            if (match == null) {
                Clear();
                return;
            }

            BoardTile[] tiles = await LadderService.GetBoardAsync(match.BoardId).ConfigureAwait(false);

            MatchId = matchId;
            BoardId = match.BoardId;
            Mode = match.Mode;
            WinTarget = match.WinTarget;
            Tiles = tiles;

            if (isNewMatch) {
                Revealed = false;
                StarredTiles.Clear();
                Finished = false;
                ResultText = null;
                CountdownStartedAt = null;
                IAmReady = false;
                OpponentReady = false;
                FullyLoaded = false;
                hasClaimSnapshot = false;
                NotifiedReady.Clear();
                OneUpAreas.Clear();
                OneUpCount = 0;
                oneUpWasChaining = false;
                picoMaxFruit = 0;
                picoAtOldSite = false;
                picoHasOrb = false;
                picoCompleted = false;
                KilledSeekers.Clear();
                Segments.Clear();
            }

            await RefreshLiveAsync().ConfigureAwait(false);
            FullyLoaded = true;

            if (!Finished)
                Status = IsTraining ? "training, no ELO at stake"
                    : IsPrivate ? $"private match, first to {WinTarget}, no ELO at stake"
                    : $"{Mode}, first to {WinTarget}";
        }

        // Claims and match status in one request, so a match ending gets noticed.
        public static async Task RefreshLiveAsync() {
            if (MatchId == null || Finished)
                return;

            MatchLive live = await LadderService.GetMatchLiveAsync(MatchId).ConfigureAwait(false);
            if (live == null)
                return;

            string me = SupabaseAuth.UserId;
            Dictionary<int, string> previous = null;

            lock (ClaimedBy) {
                if (hasClaimSnapshot)
                    previous = new Dictionary<int, string>(ClaimedBy);

                ClaimedBy.Clear();
                int mine = 0, theirs = 0;
                if (live.Claims != null) {
                    foreach (TileClaim claim in live.Claims) {
                        ClaimedBy[claim.TilePosition] = claim.PlayerId;
                        if (claim.PlayerId == me)
                            mine++;
                        else
                            theirs++;
                    }
                }
                MyClaims = mine;
                TheirClaims = theirs;
                hasClaimSnapshot = true;
            }

            // your own claims already got feedback from the call itself
            if (previous != null)
                AnnounceOpponentChanges(previous, me);

            WinTarget = live.WinTarget;
            IsTraining = live.IsTraining;
            IsPrivate = live.IsPrivate;
            CountdownStartedAt = live.CountdownStartedAt?.UtcDateTime;

            if (live.Players != null) {
                foreach (MatchPlayerState p in live.Players) {
                    if (p.PlayerId == me)
                        IAmReady = p.Ready;
                    else
                        OpponentReady = p.Ready;
                }
            }

            if (live.Status == "active")
                return;

            await FinishAsync(live).ConfigureAwait(false);
        }

        private static void AnnounceOpponentChanges(Dictionary<int, string> previous, string me) {
            lock (ClaimedBy) {
                foreach (KeyValuePair<int, string> kv in ClaimedBy) {
                    if (kv.Value == me)
                        continue;
                    if (!previous.TryGetValue(kv.Key, out string was) || was != kv.Value)
                        ChatService.SetLocalNotice($"opponent ticked {DescribeTile(kv.Key)}");
                }

                foreach (KeyValuePair<int, string> kv in previous) {
                    if (kv.Value == me)
                        continue;
                    if (!ClaimedBy.ContainsKey(kv.Key))
                        ChatService.SetLocalNotice($"opponent unticked {DescribeTile(kv.Key)}");
                }
            }
        }

        // goal text is withheld until the board has been revealed
        private static string DescribeTile(int position) {
            if (!Revealed)
                return "a square";

            if (Tiles != null)
                foreach (BoardTile tile in Tiles)
                    if (tile.Position == position)
                        return tile.Goal?.Text ?? $"square {position + 1}";

            return $"square {position + 1}";
        }

        private static async Task FinishAsync(MatchLive live) {
            Finished = true;
            IWon = live.WinnerId == SupabaseAuth.UserId;

            if (live.Status == "cancelled") {
                ResultText = "match cancelled";
            } else if (IsTraining) {
                ResultText = IWon ? "Training complete: you won" : "Training complete: dummy won";
            } else if (IsPrivate) {
                ResultText = IWon ? "You win!   (private match, no ELO)" : "You lost.   (private match, no ELO)";
            } else {
                int delta = await GetEloDeltaAsync().ConfigureAwait(false);
                string sign = delta >= 0 ? "+" : "";
                ResultText = IWon ? $"You win!   {sign}{delta} ELO" : $"You lost.   {sign}{delta} ELO";
            }

            Status = ResultText;

            // also in chat, for anyone with the board closed
            ChatService.SetLocalNotice(ResultText);
            MatchFinished?.Invoke(ResultText);
        }

        private static async Task<int> GetEloDeltaAsync() {
            try {
                MatchPlayerRow row = await LadderService.GetMyMatchPlayerAsync(MatchId).ConfigureAwait(false);
                if (row?.EloAfter == null)
                    return 0;
                return row.EloAfter.Value - row.EloBefore;
            } catch {
                return 0;
            }
        }

        public static void Clear() {
            MatchId = null;
            BoardId = null;
            Tiles = null;
            Revealed = false;
            StarredTiles.Clear();
            Finished = false;
            IWon = false;
            ResultText = null;
            IsTraining = false;
            IsPrivate = false;
            CountdownStartedAt = null;
            IAmReady = false;
            OpponentReady = false;
            FullyLoaded = false;
            lock (ClaimedBy)
                ClaimedBy.Clear();
            NotifiedReady.Clear();
            MyClaims = 0;
            TheirClaims = 0;
            OneUpAreas.Clear();
            OneUpCount = 0;
            oneUpWasChaining = false;
            picoMaxFruit = 0;
            picoAtOldSite = false;
            picoHasOrb = false;
            picoCompleted = false;
            KilledSeekers.Clear();
            Segments.Clear();
        }

        // null, "me" or "them"
        public static string OwnerOf(int position) {
            lock (ClaimedBy) {
                if (!ClaimedBy.TryGetValue(position, out string who))
                    return null;
                return who == SupabaseAuth.UserId ? "me" : "them";
            }
        }
    }
}
