using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Celeste.Mod.BingoElo.Net;

namespace Celeste.Mod.BingoElo {

    // Per-match chat. RLS limits rows to the two players, and the server does the rate limiting.
    public static class ChatService {

        private const int Window = 60;

        private static readonly List<ChatMessage> messages = new List<ChatMessage>();
        private static string loadedMatchId;
        private static long newestSeen;

        public static bool HasUnread { get; private set; }

        // command feedback, never sent to the server
        private static readonly List<ChatMessage> local = new List<ChatMessage>();

        public static void SetLocalNotice(string text) {
            lock (local) {
                local.Add(new ChatMessage {
                    Id = long.MinValue + local.Count,
                    PlayerId = null,
                    Body = text,
                    FirstSeen = DateTime.UtcNow
                });
                if (local.Count > 20)
                    local.RemoveAt(0);
            }
        }

        public static ChatMessage[] LocalNotices() {
            lock (local) {
                local.RemoveAll(m => (DateTime.UtcNow - m.FirstSeen).TotalSeconds > 12);
                return local.ToArray();
            }
        }

        public static ChatMessage[] Snapshot() {
            lock (messages)
                return messages.ToArray();
        }

        public static void MarkRead() => HasUnread = false;

        public static void Clear() {
            lock (messages)
                messages.Clear();
            lock (local)
                local.Clear();
            loadedMatchId = null;
            newestSeen = 0;
            HasUnread = false;
        }

        public static ChatMessage[] DisplayLines() {
            List<ChatMessage> all = new List<ChatMessage>();
            all.AddRange(Snapshot());
            all.AddRange(LocalNotices());
            all.AddRange(CountdownLines());
            all.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));
            return all.ToArray();
        }

        // Derived from the match timestamp on each client, so nothing is sent.
        private static ChatMessage[] CountdownLines() {
            double? elapsed = MatchState.CountdownElapsed;
            if (elapsed == null)
                return Array.Empty<ChatMessage>();

            List<ChatMessage> lines = new List<ChatMessage>();
            DateTime now = DateTime.UtcNow;

            foreach ((double at, string text) in MatchState.CountdownScript) {
                if (elapsed.Value < at)
                    continue;

                // age it from when it was due so the fade-out is right
                lines.Add(new ChatMessage {
                    Id = long.MinValue / 2 + (long) (at * 100),
                    PlayerId = null,
                    Body = text,
                    FirstSeen = now.AddSeconds(-(elapsed.Value - at))
                });
            }

            return lines.ToArray();
        }

        public static async Task RefreshAsync() {
            string matchId = MatchState.MatchId;

            if (string.IsNullOrEmpty(matchId)) {
                Clear();
                return;
            }

            if (matchId != loadedMatchId) {
                lock (messages)
                    messages.Clear();
                newestSeen = 0;
                loadedMatchId = matchId;
            }

            ChatMessage[] fetched = await SupabaseAuth.CallAsync(token =>
                SupabaseClient.SelectAsync<ChatMessage[]>(
                    "chat_messages",
                    $"match_id=eq.{matchId}&select=id,player_id,body,created_at,profiles(display_name,elo)" +
                    $"&order=id.desc&limit={Window}",
                    token)
            ).ConfigureAwait(false);

            if (fetched == null)
                return;

            // fetched newest-first so the limit keeps the latest
            Array.Reverse(fetched);

            string me = SupabaseAuth.UserId;
            long newest = newestSeen;
            bool unread = false;

            lock (messages) {
                // keep first-seen times so a refetch doesn't restart the fade-out
                Dictionary<long, DateTime> seenAt = new Dictionary<long, DateTime>();
                foreach (ChatMessage existing in messages)
                    seenAt[existing.Id] = existing.FirstSeen;

                DateTime now = DateTime.UtcNow;
                foreach (ChatMessage m in fetched) {
                    m.FirstSeen = seenAt.TryGetValue(m.Id, out DateTime when) ? when : now;

                    if (m.Id > newestSeen && m.PlayerId != me)
                        unread = true;
                    if (m.Id > newest)
                        newest = m.Id;
                }

                messages.Clear();
                messages.AddRange(fetched);
            }

            // first load of a room isn't "unread"
            if (newestSeen != 0 && unread)
                HasUnread = true;

            newestSeen = newest;
        }

        public static async Task SendAsync(string body) {
            string matchId = MatchState.MatchId;
            if (string.IsNullOrEmpty(matchId) || string.IsNullOrWhiteSpace(body))
                return;

            body = body.Trim();
            if (body.Length > 200)
                body = body.Substring(0, 200);

            try {
                await SupabaseAuth.CallAsync(token => SupabaseClient.InsertAsync("chat_messages",
                    new { match_id = matchId, player_id = SupabaseAuth.UserId, body },
                    token)).ConfigureAwait(false);
            } catch (SupabaseException e) {
                // shown as a chat line, a fixed label falls off screen at large text sizes
                string detail = e.Body ?? "";
                SetLocalNotice(
                    detail.Contains("chat rate limit") ? "slow down a moment" :
                    detail.Contains("duplicate chat message") ? "you just said that" :
                    detail.Contains("row-level security") ? "you're not in this match" :
                    "message not sent");
                return;
            }

            await RefreshAsync().ConfigureAwait(false);
        }
    }
}
