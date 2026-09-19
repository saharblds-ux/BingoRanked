using System;
using System.Text;
using Celeste.Mod.BingoElo.Net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.BingoElo.UI {

    // Minecraft-style chat: lines stack up from the bottom-right and fade out when closed.
    // Typing goes through Everest's TextInput, and the player is parked while composing.
    public class BingoChatUI : Entity {

        private const float RightEdge = 1890f;
        private const float InputY = 1016f;

        private static float InputW => Math.Min(1400f, 760f * SizeFactor);
        private static float InputH => LineH + 12f;
        private const float BaseTextScale = 0.64f;

        // REVEAL and GO are drawn larger
        private const float ShoutFactor = 1.5f;

        private static float SizeFactor => BingoEloModule.Settings.ChatTextSize / 100f;

        private static float TextScale => BaseTextScale * SizeFactor;

        private static float LineH => ActiveFont.LineHeight * TextScale * 1.12f;
        private const int MaxVisibleClosed = 6;
        private const int MaxVisibleOpen = 12;
        private const float HoldSeconds = 8f;
        private const float FadeSeconds = 2f;
        private const int MaxLength = 200;

        public static BingoChatUI Instance { get; private set; }

        public bool Open { get; private set; }

        private float fade;
        private float refreshTimer;
        private readonly StringBuilder composing = new StringBuilder();
        private bool listening;
        private int scroll;
        private bool holdsControl;

        public BingoChatUI() {
            Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate | Tags.FrozenUpdate;
            Depth = -120000;   // above menu and board
            Instance = this;
        }

        private static bool InputBelongsElsewhere() {
            if (Engine.Commands != null && Engine.Commands.Open)
                return true;

            Scene scene = Engine.Scene;
            if (scene == null)
                return true;
            if (scene.Entities.FindFirst<TextMenu>() != null)
                return true;
            if (scene is Level level && level.Paused)
                return true;

            return false;
        }

        public override void Update() {
            base.Update();

            if (InputBelongsElsewhere()) {
                fade = Calc.Approach(fade, Open ? 1f : 0f, Engine.RawDeltaTime * 6f);
                return;
            }

            // open only, no toggle: the bind is a letter and would close chat mid-word
            if (!Open && BingoEloModule.Settings.OpenChat.Pressed) {
                BingoEloModule.Settings.OpenChat.ConsumePress();
                SetOpen(true);
            }

            fade = Calc.Approach(fade, Open ? 1f : 0f, Engine.RawDeltaTime * 6f);

            if (!Open) {
                if (MatchState.HasMatch)
                    Poll(3f);
                return;
            }

            // raw keys while composing, since the menu actions can be bound to letters
            if (MInput.Keyboard.Pressed(Keys.Escape)) {
                SetOpen(false);

                // Escape is also pause
                Input.ESC.ConsumePress();
                Input.Pause.ConsumePress();
                Input.MenuCancel.ConsumePress();
                return;
            }

            if (MInput.Keyboard.Pressed(Keys.Up) || MInput.Keyboard.Pressed(Keys.PageUp))
                scroll = Math.Min(scroll + 1, 40);
            if (MInput.Keyboard.Pressed(Keys.Down) || MInput.Keyboard.Pressed(Keys.PageDown))
                scroll = Math.Max(scroll - 1, 0);

            Poll(3f);
        }

        private void Poll(float interval) {
            refreshTimer -= Engine.RawDeltaTime;
            if (refreshTimer > 0f)
                return;
            refreshTimer = interval;
            SupabaseClient.FireAndForget(ChatService.RefreshAsync, "chat refresh");
        }

        public void ForceClose() => SetOpen(false);

        private void SetOpen(bool open) {
            if (open == Open)
                return;

            Open = open;

            if (open) {
                composing.Clear();
                scroll = 0;
                ChatService.MarkRead();
                StartListening();
                ParkPlayer();
                refreshTimer = 0f;
                SupabaseClient.FireAndForget(async () => {
                    await LadderService.ConnectAsync();
                    await ChatService.RefreshAsync();
                }, "chat open");
            } else {
                StopListening();
                UnparkPlayer();
            }
        }

        private void StartListening() {
            if (listening)
                return;
            TextInput.OnInput += OnTextInput;
            listening = true;
        }

        private void StopListening() {
            if (!listening)
                return;
            TextInput.OnInput -= OnTextInput;
            listening = false;
        }

        private void OnTextInput(char c) {
            if (!Open)
                return;

            if (c == '\b') {
                if (composing.Length > 0)
                    composing.Remove(composing.Length - 1, 1);
                return;
            }

            if (c == '\r' || c == '\n') {
                Send();
                return;
            }

            if (c < ' ')
                return;

            if (composing.Length < MaxLength)
                composing.Append(c);
        }

        private void Send() {
            string body = composing.ToString().Trim();
            composing.Clear();
            if (body.Length == 0)
                return;

            if (body.StartsWith("/")) {
                RunCommand(body);
                return;
            }

            SupabaseClient.FireAndForget(() => ChatService.SendAsync(body), "chat send");
        }

        // slash commands are actions, never sent as chat
        private void RunCommand(string line) {
            string command = line.Split(' ')[0].ToLowerInvariant();
            string matchId = MatchState.MatchId;

            if (string.IsNullOrEmpty(matchId)) {
                ChatService.SetLocalNotice("you're not in a match");
                return;
            }

            switch (command) {
                case "/ready":
                    SupabaseClient.FireAndForget(async () => {
                        await LadderService.SetReadyAsync(matchId, true);
                        await MatchState.RefreshLiveAsync();
                        ChatService.SetLocalNotice("you are ready");
                    }, "ready");
                    break;

                case "/unready":
                    SupabaseClient.FireAndForget(async () => {
                        await LadderService.SetReadyAsync(matchId, false);
                        await MatchState.RefreshLiveAsync();
                        ChatService.SetLocalNotice("you are not ready");
                    }, "unready");
                    break;

                case "/countdown":
                    SupabaseClient.FireAndForget(async () => {
                        try {
                            await LadderService.StartCountdownAsync(matchId);
                            await MatchState.RefreshLiveAsync();
                        } catch (SupabaseException e) {
                            ChatService.SetLocalNotice(
                                (e.Body ?? "").Contains("not everyone is ready")
                                    ? "both players need to be ready first"
                                    : "couldn't start the countdown");
                        }
                    }, "countdown");
                    break;

                case "/return":
                    if (!MatchState.HasStarted) {
                        ChatService.SetLocalNotice("the race hasn't started yet");
                        break;
                    }
                    // with no save file, ReturnToRun starts a fresh run
                    ChatService.SetLocalNotice(BingoSave.RunExists()
                        ? "returning to your run..."
                        : "starting your run...");
                    SetOpen(false);
                    BingoEloModule.OnMainThread(BingoSave.ReturnToRun);
                    break;

                default:
                    ChatService.SetLocalNotice("commands: /ready  /unready  /countdown  /return");
                    break;
            }
        }

        public override void Removed(Scene scene) {
            StopListening();
            if (holdsControl) {
                holdsControl = false;
                GameControlLock.Release(scene);
            }
            base.Removed(scene);
        }

        public override void SceneEnd(Scene scene) {
            StopListening();
            if (holdsControl) {
                holdsControl = false;
                GameControlLock.Release(scene);
            }
            base.SceneEnd(scene);
        }

        private void ParkPlayer() {
            if (holdsControl)
                return;
            holdsControl = true;
            GameControlLock.Acquire(Scene);
        }

        private void UnparkPlayer() {
            if (!holdsControl)
                return;
            holdsControl = false;
            GameControlLock.Release(Scene);
        }

        public override void Render() {
            ChatMessage[] all = ChatService.DisplayLines();
            float openAlpha = Ease.CubeOut(fade);

            // closed shows only recent fading lines; also capped to what fits on screen
            int fits = Math.Max(1, (int) ((InputY - 90f) / LineH));
            int shown = Math.Min(Open ? MaxVisibleOpen : MaxVisibleClosed, fits);
            int end = Math.Max(0, all.Length - (Open ? scroll : 0));
            int start = Math.Max(0, end - shown);

            float rowY = InputY - InputH / 2f - LineH / 2f - 6f;
            string me = SupabaseAuth.UserId;

            for (int i = end - 1; i >= start; i--) {
                ChatMessage m = all[i];

                float lineAlpha = Open ? 1f : TransientAlpha(m);
                if (lineAlpha <= 0.01f)
                    continue;

                // null author = system line (countdown ticks, command feedback)
                bool system = m.PlayerId == null;
                string name = system ? "" : m.AuthorName + ":";
                string body = m.Body ?? "";

                bool shout = body == "REVEAL" || body == "GO";
                float bodyScale = shout ? TextScale * ShoutFactor : TextScale;
                float rowH = shout ? LineH * ShoutFactor : LineH;

                float nameW = system ? 0f : ActiveFont.Measure(name).X * TextScale;
                float bodyW = ActiveFont.Measure(body).X * bodyScale;
                float total = nameW + (system ? 0f : 10f) + bodyW;

                Draw.Rect(RightEdge - total - 12f, rowY - rowH / 2f + 3f,
                    total + 24f, rowH - 6f, Color.Black * (0.55f * lineAlpha));

                if (!system) {
                    Color nameColor = m.PlayerId == me ? new Color(255, 225, 140) : new Color(150, 195, 245);
                    ActiveFont.Draw(name, new Vector2(RightEdge - bodyW - 10f, rowY), new Vector2(1f, 0.5f),
                        Vector2.One * TextScale, nameColor * lineAlpha);
                }

                ActiveFont.Draw(body, new Vector2(RightEdge, rowY), new Vector2(1f, 0.5f),
                    Vector2.One * bodyScale,
                    (system
                        ? (shout ? new Color(255, 235, 150) : new Color(190, 200, 215))
                        : Color.White) * lineAlpha);

                rowY -= rowH;
            }

            if (fade <= 0.01f)
                return;

            float boxW = InputW;
            float boxX = RightEdge - boxW;
            Draw.Rect(boxX, InputY - InputH / 2f, boxW, InputH, Color.Black * (0.72f * openAlpha));
            Draw.HollowRect(boxX, InputY - InputH / 2f, boxW, InputH, Color.White * (0.22f * openAlpha));

            if (!MatchState.HasMatch) {
                ActiveFont.Draw("chat opens when you're in a match",
                    new Vector2(RightEdge - 12f, InputY), new Vector2(1f, 0.5f),
                    Vector2.One * TextScale, Color.Gray * openAlpha);
                return;
            }

            string typed = composing.ToString();
            bool caret = ((int) (Engine.Scene.TimeActive * 2f) % 2) == 0;
            string visible = Truncate(typed, InputW - 40f, TextScale);

            if (typed.Length == 0) {
                ActiveFont.Draw(caret ? "_" : "", new Vector2(boxX + 14f, InputY), new Vector2(0f, 0.5f),
                    Vector2.One * TextScale, Color.White * openAlpha);
                ActiveFont.Draw(MatchState.IsTraining ? "say something (practice)" : "say something",
                    new Vector2(RightEdge - 12f, InputY), new Vector2(1f, 0.5f),
                    Vector2.One * (TextScale * 0.9f), Color.Gray * (0.8f * openAlpha));
            } else {
                ActiveFont.Draw(visible + (caret ? "_" : ""),
                    new Vector2(boxX + 14f, InputY), new Vector2(0f, 0.5f),
                    Vector2.One * TextScale, Color.White * openAlpha);
            }

        }

        private static float TransientAlpha(ChatMessage m) {
            double age = (DateTime.UtcNow - m.FirstSeen).TotalSeconds;
            if (age <= HoldSeconds)
                return 1f;
            if (age >= HoldSeconds + FadeSeconds)
                return 0f;
            return 1f - (float) ((age - HoldSeconds) / FadeSeconds);
        }

        private static string Truncate(string text, float maxWidth, float scale) {
            if (string.IsNullOrEmpty(text) || ActiveFont.Measure(text).X * scale <= maxWidth)
                return text ?? "";

            // keep the tail so the caret stays visible
            for (int cut = 1; cut < text.Length; cut++) {
                string candidate = text.Substring(cut);
                if (ActiveFont.Measure(candidate).X * scale <= maxWidth)
                    return candidate;
            }
            return text;
        }
    }
}
