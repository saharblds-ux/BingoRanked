using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Celeste.Mod;
using Newtonsoft.Json;

namespace Celeste.Mod.BingoElo.Net {

    public class SupabaseSession {
        [JsonProperty("access_token")] public string AccessToken;
        [JsonProperty("refresh_token")] public string RefreshToken;
        [JsonProperty("expires_at")] public long ExpiresAtUnix;
        [JsonProperty("user_id")] public string UserId;

        [JsonIgnore]
        public bool NeedsRefresh =>
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAtUnix - 60;
    }

    // Anonymous sign-in on first launch, session cached on disk. The user id
    // is stable, so it can be linked to a real account later without losing ELO.
    public static class SupabaseAuth {

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static SupabaseSession Session { get; private set; }
        public static string UserId => Session?.UserId;
        public static bool IsSignedIn => Session != null;

        private static string SessionPath
            => Path.Combine(Everest.PathSettings, "bingoelo-session.json");

        public static Task<string> GetAccessTokenAsync() => EnsureSessionAsync(forceRefresh: false);

        public static Task<string> ForceRefreshAccessTokenAsync() => EnsureSessionAsync(forceRefresh: true);

        private static async Task<string> EnsureSessionAsync(bool forceRefresh) {
            await Gate.WaitAsync().ConfigureAwait(false);
            try {
                Session ??= LoadSession();

                if (Session == null) {
                    Session = await SignInAnonymouslyAsync().ConfigureAwait(false);
                    SaveSession(Session);
                    Logger.Log(LogLevel.Info, nameof(BingoEloModule),
                        $"signed in anonymously as {Session.UserId}");
                } else if (forceRefresh || Session.NeedsRefresh) {
                    try {
                        Session = await RefreshAsync(Session.RefreshToken).ConfigureAwait(false);
                        SaveSession(Session);
                    } catch (SupabaseException e) when (
                        e.StatusCode == HttpStatusCode.BadRequest ||
                        e.StatusCode == HttpStatusCode.Unauthorized
                    ) {
                        Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                            "refresh token rejected, signing in fresh");
                        Session = await SignInAnonymouslyAsync().ConfigureAwait(false);
                        SaveSession(Session);
                    }
                }

                return Session.AccessToken;
            } finally {
                Gate.Release();
            }
        }

        // If the project's JWT key rotates, tokens issued before it are rejected
        // with PGRST301 until they expire. Refresh once and retry.
        public static async Task<T> CallAsync<T>(Func<string, Task<T>> call) {
            string token = await GetAccessTokenAsync().ConfigureAwait(false);
            try {
                return await call(token).ConfigureAwait(false);
            } catch (SupabaseException e) when (IsRejectedToken(e)) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    "access token rejected by server, forcing refresh and retrying");
                token = await ForceRefreshAccessTokenAsync().ConfigureAwait(false);
                return await call(token).ConfigureAwait(false);
            }
        }

        public static async Task CallAsync(Func<string, Task> call) {
            string token = await GetAccessTokenAsync().ConfigureAwait(false);
            try {
                await call(token).ConfigureAwait(false);
            } catch (SupabaseException e) when (IsRejectedToken(e)) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    "access token rejected by server, forcing refresh and retrying");
                token = await ForceRefreshAccessTokenAsync().ConfigureAwait(false);
                await call(token).ConfigureAwait(false);
            }
        }

        private static bool IsRejectedToken(SupabaseException e) =>
            e.StatusCode == HttpStatusCode.Unauthorized && e.Body != null && e.Body.Contains("PGRST301");

        private static async Task<SupabaseSession> SignInAnonymouslyAsync() {
            AuthResponse res;
            try {
                res = await SupabaseClient.RequestAsync<AuthResponse>(
                    HttpMethod.Post, "/auth/v1/signup", new { }).ConfigureAwait(false);
            } catch (SupabaseException e) when (e.Body != null && e.Body.Contains("anonymous_provider_disabled")) {
                throw new InvalidOperationException(
                    "Anonymous sign-ins are disabled for this Supabase project. " +
                    "Enable them under Authentication -> Sign In / Providers.", e);
            }
            return ToSession(res);
        }

        private static async Task<SupabaseSession> RefreshAsync(string refreshToken) {
            AuthResponse res = await SupabaseClient.RequestAsync<AuthResponse>(
                HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token",
                new { refresh_token = refreshToken }).ConfigureAwait(false);
            return ToSession(res);
        }

        private static SupabaseSession ToSession(AuthResponse res) => new SupabaseSession {
            AccessToken = res.AccessToken,
            RefreshToken = res.RefreshToken,
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(res.ExpiresIn, 60),
            UserId = res.User?.Id
        };

        private static SupabaseSession LoadSession() {
            try {
                if (!File.Exists(SessionPath))
                    return null;
                SupabaseSession session =
                    JsonConvert.DeserializeObject<SupabaseSession>(File.ReadAllText(SessionPath));
                return string.IsNullOrEmpty(session?.RefreshToken) ? null : session;
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    $"could not read cached session, signing in fresh: {e.Message}");
                return null;
            }
        }

        private static void SaveSession(SupabaseSession session) {
            try {
                File.WriteAllText(SessionPath, JsonConvert.SerializeObject(session));
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    $"could not cache session: {e.Message}");
            }
        }

        public static void SignOut() {
            Session = null;
            try {
                if (File.Exists(SessionPath))
                    File.Delete(SessionPath);
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule), $"could not clear session: {e.Message}");
            }
        }

        // filled in by Json.NET
#pragma warning disable CS0649

        private class AuthResponse {
            [JsonProperty("access_token")] public string AccessToken;
            [JsonProperty("refresh_token")] public string RefreshToken;
            [JsonProperty("expires_in")] public int ExpiresIn;
            [JsonProperty("user")] public AuthUser User;
        }

        private class AuthUser {
            [JsonProperty("id")] public string Id;
        }

#pragma warning restore CS0649
    }
}
