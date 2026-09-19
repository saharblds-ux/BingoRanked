using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Celeste.Mod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Celeste.Mod.BingoElo.Net {

    public class SupabaseException : Exception {
        public HttpStatusCode StatusCode { get; }
        public string Body { get; }

        public SupabaseException(HttpStatusCode status, string body)
            : base($"Supabase request failed ({(int) status} {status}): {body}") {
            StatusCode = status;
            Body = body;
        }
    }

    public static class SupabaseClient {

        public const string Url = "https://thbverdqtbuvxcfkcwbk.supabase.co";

        // Publishable key: safe to ship, RLS does the actual gatekeeping.
        // The service_role key must never go in this mod.
        public const string PublishableKey = "sb_publishable_uBKEQHBV3YqGdcVeuO5otA_3GuZbiEH";

        private static readonly HttpClient Http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public static async Task<string> RequestAsync(
            HttpMethod method,
            string path,
            object body = null,
            string accessToken = null,
            string prefer = null
        ) {
            using HttpRequestMessage req = new HttpRequestMessage(method, Url + path);

            req.Headers.TryAddWithoutValidation("apikey", PublishableKey);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (accessToken ?? PublishableKey));
            if (prefer != null)
                req.Headers.TryAddWithoutValidation("Prefer", prefer);

            if (body != null) {
                string json = body as string ?? JsonConvert.SerializeObject(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using HttpResponseMessage res = await Http.SendAsync(req).ConfigureAwait(false);
            NoteServerTime(res);
            string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
                throw new SupabaseException(res.StatusCode, text);

            return text;
        }

        public static async Task<T> RequestAsync<T>(
            HttpMethod method,
            string path,
            object body = null,
            string accessToken = null,
            string prefer = null
        ) {
            string text = await RequestAsync(method, path, body, accessToken, prefer).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? default : JsonConvert.DeserializeObject<T>(text);
        }

        public static Task<T> SelectAsync<T>(string table, string query, string accessToken = null)
            => RequestAsync<T>(HttpMethod.Get, $"/rest/v1/{table}?{query}", null, accessToken);

        public static Task<T> InsertAsync<T>(string table, object row, string accessToken = null)
            => RequestAsync<T>(HttpMethod.Post, $"/rest/v1/{table}", row, accessToken, "return=representation");

        public static Task InsertAsync(string table, object row, string accessToken = null)
            => RequestAsync(HttpMethod.Post, $"/rest/v1/{table}", row, accessToken, "return=minimal");

        public static Task DeleteAsync(string table, string filter, string accessToken = null)
            => RequestAsync(HttpMethod.Delete, $"/rest/v1/{table}?{filter}", null, accessToken, "return=minimal");

        public static Task<T> RpcAsync<T>(string function, object args = null, string accessToken = null)
            => RequestAsync<T>(HttpMethod.Post, $"/rest/v1/rpc/{function}", args ?? new JObject(), accessToken);

        // Server minus local clock, so both players' countdowns line up.
        private static TimeSpan clockOffset = TimeSpan.Zero;

        public static DateTime ServerNow => DateTime.UtcNow + clockOffset;

        private static void NoteServerTime(HttpResponseMessage res) {
            DateTimeOffset? date = res.Headers.Date;
            if (date == null)
                return;

            TimeSpan sample = date.Value.UtcDateTime - DateTime.UtcNow;

            // smooth it, a single sample includes network latency
            clockOffset = clockOffset == TimeSpan.Zero
                ? sample
                : TimeSpan.FromMilliseconds(clockOffset.TotalMilliseconds * 0.7 + sample.TotalMilliseconds * 0.3);
        }

        public static void FireAndForget(Func<Task> work, string what) {
            _ = Task.Run(async () => {
                try {
                    await work().ConfigureAwait(false);
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, nameof(BingoEloModule), $"{what} failed: {e.Message}");
                }
            });
        }
    }
}
