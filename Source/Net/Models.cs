using System;
using Newtonsoft.Json;

namespace Celeste.Mod.BingoElo.Net {

    // filled in by Json.NET
#pragma warning disable CS0649

    // Display only, the server doesn't know about tiers.
    public static class RankTier {
        public static string NameFor(int elo) {
            if (elo >= 700) return "Expert";
            if (elo >= 550) return "Advanced";
            if (elo >= 400) return "Intermediate";
            return "Beginner";
        }
    }

    public class Profile {
        [JsonProperty("id")] public string Id;
        [JsonProperty("display_name")] public string DisplayName;
        [JsonProperty("elo")] public int Elo;
        [JsonProperty("games_played")] public int GamesPlayed;
        [JsonProperty("wins")] public int Wins;
        [JsonProperty("losses")] public int Losses;

        // use this instead of DisplayName anywhere a name is drawn
        [JsonIgnore]
        public string RankedName => $"[{RankTier.NameFor(Elo)}] {DisplayName}";

        public override string ToString()
            => $"{RankedName} ({Elo} ELO, {Wins}W/{Losses}L in {GamesPlayed})";
    }

    public class QueueEntry {
        [JsonProperty("player_id")] public string PlayerId;
        [JsonProperty("joined_at")] public string JoinedAt;
    }

    public class Goal {
        [JsonProperty("id")] public string Id;
        [JsonProperty("text")] public string Text;
        [JsonProperty("tier")] public int Tier;
        [JsonProperty("tags")] public string[] Tags;

        // null for goals that can't be checked against the save
        [JsonProperty("check_kind")] public string CheckKind;
        [JsonProperty("check_params")] public Newtonsoft.Json.Linq.JObject CheckParams;
    }

    public class BoardTile {
        [JsonProperty("position")] public int Position;
        [JsonProperty("goals")] public Goal Goal;
    }

    public class Match {
        [JsonProperty("id")] public string Id;
        [JsonProperty("board_id")] public string BoardId;
        [JsonProperty("status")] public string Status;
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("win_target")] public int WinTarget;
        [JsonProperty("is_training")] public bool IsTraining;
        [JsonProperty("is_private")] public bool IsPrivate;
        [JsonProperty("winner_id")] public string WinnerId;
    }

    public class TileClaim {
        [JsonProperty("player_id")] public string PlayerId;
        [JsonProperty("tile_position")] public int TilePosition;
    }

    // Status and claims in one request (PostgREST embed).
    public class MatchLive {
        [JsonProperty("status")] public string Status;
        [JsonProperty("winner_id")] public string WinnerId;
        [JsonProperty("win_target")] public int WinTarget;
        [JsonProperty("is_training")] public bool IsTraining;
        [JsonProperty("is_private")] public bool IsPrivate;
        // Must be DateTimeOffset: Json.NET turns "+00:00" into local time for
        // DateTime, which shifts the countdown by the player's UTC offset.
        [JsonProperty("countdown_started_at")] public DateTimeOffset? CountdownStartedAt;
        [JsonProperty("started_at")] public DateTimeOffset? StartedAt;
        [JsonProperty("match_tile_progress")] public TileClaim[] Claims;
        [JsonProperty("match_players")] public MatchPlayerState[] Players;
    }

    public class MatchPlayerState {
        [JsonProperty("player_id")] public string PlayerId;
        [JsonProperty("ready")] public bool Ready;
    }

    public class MatchPlayerRow {
        [JsonProperty("elo_before")] public int EloBefore;
        [JsonProperty("elo_after")] public int? EloAfter;
    }

    public class ChatMessage {
        [JsonProperty("id")] public long Id;
        [JsonProperty("player_id")] public string PlayerId;
        [JsonProperty("body")] public string Body;
        [JsonProperty("created_at")] public string CreatedAt;
        [JsonProperty("profiles")] public Profile Author;

        [JsonIgnore]
        public string AuthorName => Author?.RankedName ?? "???";

        // local time, not created_at: a message fetched late still gets its full time on screen
        [JsonIgnore]
        public DateTime FirstSeen;
    }

    // Reads are scoped to rows where this client is either side, so check
    // FromPlayer/ToPlayer to tell sent from received.
    public class Challenge {
        [JsonProperty("from_player")] public string FromPlayer;
        [JsonProperty("to_player")] public string ToPlayer;
        [JsonProperty("created_at")] public string CreatedAt;
        [JsonProperty("from_profile")] public Profile FromProfile;
        [JsonProperty("to_profile")] public Profile ToProfile;
    }

    public class ClaimResult {
        [JsonProperty("position")] public int Position;
        [JsonProperty("claimed")] public int Claimed;
        [JsonProperty("target")] public int Target;
        [JsonProperty("won")] public bool Won;
    }

#pragma warning restore CS0649
}
