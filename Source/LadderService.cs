using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Celeste.Mod.BingoElo.Net;

namespace Celeste.Mod.BingoElo {

    // Runs off the game thread; use BingoEloModule.OnMainThread before touching Celeste state.
    public static class LadderService {

        public static Profile Me { get; private set; }

        public static async Task<Profile> ConnectAsync() {
            Profile[] existing = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<Profile[]>(
                    "profiles", $"id=eq.{SupabaseAuth.UserId}&select=*", token)
            ).ConfigureAwait(false);

            if (existing != null && existing.Length > 0) {
                Me = existing[0];

                string wanted = BingoEloModule.Settings.DisplayName;
                if (!string.IsNullOrWhiteSpace(wanted) && wanted != Me.DisplayName) {
                    try {
                        Me = await RenameAsync(wanted).ConfigureAwait(false);
                    } catch (SupabaseException e) when (e.StatusCode == HttpStatusCode.Conflict) {
                        // name taken, keep what the server has
                    }
                }

                return Me;
            }

            // display names are unique, so the default can collide
            string name = BingoEloModule.Settings.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = "Madeline" + SupabaseAuth.UserId.Substring(0, 4);

            for (int attempt = 0; ; attempt++) {
                try {
                    Profile[] created = await SupabaseAuth.CallAsync(token =>
                        SupabaseClient.InsertAsync<Profile[]>(
                            "profiles", new { id = SupabaseAuth.UserId, display_name = name }, token)
                    ).ConfigureAwait(false);

                    BingoEloModule.Settings.DisplayName = name;
                    Me = created != null && created.Length > 0 ? created[0] : null;
                    return Me;
                } catch (SupabaseException e) when (e.StatusCode == HttpStatusCode.Conflict && attempt < 5) {
                    name = "Madeline" + Guid.NewGuid().ToString("N").Substring(0, 4);
                }
            }
        }

        public static async Task<Profile> RenameAsync(string newName) {
            Profile[] updated = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.RequestAsync<Profile[]>(
                    new HttpMethod("PATCH"), $"/rest/v1/profiles?id=eq.{SupabaseAuth.UserId}",
                    new { display_name = newName }, token, "return=representation")
            ).ConfigureAwait(false);

            BingoEloModule.Settings.DisplayName = newName;
            Me = updated != null && updated.Length > 0 ? updated[0] : Me;
            return Me;
        }

        // the heartbeat runs while this is true; the server drops queue entries after 60s of silence
        public static bool IsQueued { get; private set; }

        public static DateTime QueuedSince { get; private set; } = DateTime.UtcNow;

        public static async Task JoinQueueAsync() {
            await SupabaseAuth.CallAsync(token =>
                SupabaseClient.InsertAsync("queue", new { player_id = SupabaseAuth.UserId }, token)
            ).ConfigureAwait(false);
            IsQueued = true;
            QueuedSince = DateTime.UtcNow;

            MatchState.AllowRejoin();
        }

        public static async Task LeaveQueueAsync() {
            await SupabaseAuth.CallAsync(token =>
                SupabaseClient.DeleteAsync("queue", $"player_id=eq.{SupabaseAuth.UserId}", token)
            ).ConfigureAwait(false);
            IsQueued = false;

            BingoEloModule.LeaveRoom();
        }

        // false once the row is gone, either matched or reaped
        public static async Task<bool> HeartbeatAsync() {
            bool stillQueued = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.RpcAsync<bool>("queue_heartbeat", null, token)
            ).ConfigureAwait(false);
            IsQueued = stillQueued;
            return stillQueued;
        }

        public static async Task<int> GetQueueSizeAsync() {
            QueueEntry[] rows = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<QueueEntry[]>("queue", "select=player_id", token)
            ).ConfigureAwait(false);
            return rows?.Length ?? 0;
        }

        public static Task<Profile[]> GetLeaderboardAsync(int limit = 10, int offset = 0) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.SelectAsync<Profile[]>(
                "profiles", $"select=*&is_bot=eq.false&order=elo.desc&limit={limit}&offset={offset}", token));

        // returns the new match id, or null if nobody was waiting
        public static Task<string> TryMatchmakeAsync() =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<string>("try_matchmake", null, token));

        public static Task<string> GetActiveMatchIdAsync() =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<string>("my_active_match", null, token));

        public static async Task<Match> GetMatchAsync(string matchId) {
            Match[] rows = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<Match[]>("matches", $"id=eq.{matchId}&select=*", token)
            ).ConfigureAwait(false);
            return rows != null && rows.Length > 0 ? rows[0] : null;
        }

        public static Task<BoardTile[]> GetBoardAsync(string boardId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.SelectAsync<BoardTile[]>(
                "board_tiles",
                $"board_id=eq.{boardId}&select=position,goals(id,text,tier,tags,check_kind,check_params)&order=position.asc",
                token));

        public static Task<TileClaim[]> GetClaimsAsync(string matchId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.SelectAsync<TileClaim[]>(
                "match_tile_progress",
                $"match_id=eq.{matchId}&select=player_id,tile_position", token));

        // status and claims in one request, used by the poll loop
        public static async Task<MatchLive> GetMatchLiveAsync(string matchId) {
            MatchLive[] rows = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<MatchLive[]>(
                    "matches",
                    $"id=eq.{matchId}&select=status,winner_id,win_target,is_training,is_private," +
                    "countdown_started_at,started_at," +
                    "match_tile_progress(player_id,tile_position),match_players(player_id,ready)",
                    token)
            ).ConfigureAwait(false);
            return rows != null && rows.Length > 0 ? rows[0] : null;
        }

        public static Task SetReadyAsync(string matchId, bool ready) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<Newtonsoft.Json.Linq.JObject>(
                "set_ready", new { p_match_id = matchId, p_ready = ready }, token));

        public static Task StartCountdownAsync(string matchId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<Newtonsoft.Json.Linq.JObject>(
                "start_countdown", new { p_match_id = matchId }, token));

        public static async Task<MatchPlayerRow> GetMyMatchPlayerAsync(string matchId) {
            MatchPlayerRow[] rows = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<MatchPlayerRow[]>(
                    "match_players",
                    $"match_id=eq.{matchId}&player_id=eq.{SupabaseAuth.UserId}&select=elo_before,elo_after",
                    token)
            ).ConfigureAwait(false);
            return rows != null && rows.Length > 0 ? rows[0] : null;
        }

        // also how forfeits are collected: true means the opponent timed out and we won
        public static async Task<bool> MatchHeartbeatAsync(string matchId) {
            Newtonsoft.Json.Linq.JObject result = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.RpcAsync<Newtonsoft.Json.Linq.JObject>(
                    "match_heartbeat", new { p_match_id = matchId }, token)
            ).ConfigureAwait(false);
            return result != null && result.Value<bool>("forfeit");
        }

        public static Task ConcedeAsync(string matchId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<Newtonsoft.Json.Linq.JObject>(
                "concede_match", new { p_match_id = matchId }, token));

        // training (dev only, remove before release)

        public static Task<string> SpawnTrainingMatchAsync() =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<string>(
                "spawn_training_match", null, token));

        // unlike conceding, this isn't recorded as a loss
        public static Task<bool> EndTrainingAsync() =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<bool>(
                "end_training_match", null, token));

        public static Task<ClaimResult> TrainingBotClaimAsync(string matchId, int position) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<ClaimResult>(
                "training_bot_claim", new { p_match_id = matchId, p_position = position }, token));

        // private rooms

        // the match is only created once the target accepts
        public static Task<string> ChallengePlayerAsync(string displayName) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<string>(
                "challenge_player", new { p_name = displayName }, token));

        public static Task<string> AcceptChallengeAsync(string fromPlayerId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<string>(
                "accept_challenge", new { p_from = fromPlayerId }, token));

        public static Task DeclineChallengeAsync(string fromPlayerId) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<Newtonsoft.Json.Linq.JObject>(
                "decline_challenge", new { p_from = fromPlayerId }, token));

        // sent and received
        public static Task<Challenge[]> GetChallengesAsync() =>
            SupabaseAuth.CallAsync(token => SupabaseClient.SelectAsync<Challenge[]>(
                "challenges",
                "select=from_player,to_player,created_at," +
                "from_profile:profiles!from_player(display_name,elo)," +
                "to_profile:profiles!to_player(display_name,elo)",
                token));

        public static async Task<Challenge[]> GetIncomingChallengesAsync() {
            Challenge[] all = await GetChallengesAsync().ConfigureAwait(false);
            if (all == null) return Array.Empty<Challenge>();
            return Array.FindAll(all, c => c.ToPlayer == SupabaseAuth.UserId);
        }

        // the server enforces lockout, membership and the win condition
        public static Task<ClaimResult> ClaimTileAsync(string matchId, int position) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<ClaimResult>(
                "claim_tile", new { p_match_id = matchId, p_position = position }, token));

        // no-op if the tile is unclaimed or the opponent's
        public static Task<ClaimResult> UnclaimTileAsync(string matchId, int position) =>
            SupabaseAuth.CallAsync(token => SupabaseClient.RpcAsync<ClaimResult>(
                "unclaim_tile", new { p_match_id = matchId, p_position = position }, token));
    }
}
