using System;
using System.Collections.Generic;
using System.Text;
using Celeste.Mod.BingoElo.Net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.BingoElo.UI {

    // Items are rebuilt from live state every frame, so the menu only offers what applies now.
    public class BingoMenuUI : Entity {

        private const float PanelW = 760f;
        private const float RowH = 58f;

        public static BingoMenuUI Instance { get; private set; }

        public bool Open { get; private set; }

        private float fade;
        private int cursor;
        private const int LeaderboardPageSize = 10;

        private bool showLeaderboard;
        private Profile[] leaderboard;
        private int leaderboardPage;
        private bool leaderboardHasMore;
        private string busy;
        private bool holdsControl;

        // refreshed when the menu opens, not in BuildItems() which runs every frame
        private Challenge[] incomingChallenges = Array.Empty<Challenge>();

        // name entry for "Private Match", typed through Everest's TextInput like the chat
        private bool enteringChallengeName;
        private readonly StringBuilder challengeName = new StringBuilder();
        private bool challengeListening;
        private const int MaxNameLength = 32;

        // The title-screen entry opens us with a Confirm press that is still live this
        // frame, so it has to be swallowed or it picks the first item.
        private bool justOpened;

        private class Item {
            public string Label;
            public Action Action;
            public bool Danger;
            public bool NeedsConfirm;
        }

        private readonly List<Item> items = new List<Item>();
        private int confirmingIndex = -1;

        public BingoMenuUI() {
            Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate | Tags.FrozenUpdate;
            Depth = -110000;   // above the board
            Instance = this;
        }

        private static bool InputBelongsElsewhere() {
            if (Engine.Commands != null && Engine.Commands.Open)
                return true;

            // Chat sits above the menu; while typing, keys belong to the composer.
            if (BingoChatUI.Instance != null && BingoChatUI.Instance.Open)
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

            if (BingoEloModule.Settings.OpenMenu.Pressed) {
                BingoEloModule.Settings.OpenMenu.ConsumePress();
                SetOpen(!Open);
            }

            fade = Calc.Approach(fade, Open ? 1f : 0f, Engine.RawDeltaTime * 6f);

            if (!Open)
                return;

            BuildItems();

            if (justOpened) {
                justOpened = false;
                Input.MenuConfirm.ConsumePress();
                return;
            }

            if (enteringChallengeName) {
                // raw key, since MenuCancel could be bound to a letter
                if (MInput.Keyboard.Pressed(Keys.Escape)) {
                    StopChallengeEntry();
                    Input.ESC.ConsumePress();
                    Input.Pause.ConsumePress();
                    Input.MenuCancel.ConsumePress();
                }
                return;
            }

            if (showLeaderboard) {
                if (Input.MenuCancel.Pressed || Input.MenuConfirm.Pressed) {
                    Input.MenuCancel.ConsumePress();
                    Input.MenuConfirm.ConsumePress();
                    showLeaderboard = false;
                    return;
                }
                if (Input.MenuLeft.Pressed && leaderboardPage > 0) {
                    Input.MenuLeft.ConsumePress();
                    FetchLeaderboardPage(leaderboardPage - 1);
                }
                if (Input.MenuRight.Pressed && leaderboardHasMore) {
                    Input.MenuRight.ConsumePress();
                    FetchLeaderboardPage(leaderboardPage + 1);
                }
                return;
            }

            if (Input.MenuUp.Pressed) { cursor = Math.Max(0, cursor - 1); confirmingIndex = -1; }
            if (Input.MenuDown.Pressed) { cursor = Math.Min(items.Count - 1, cursor + 1); confirmingIndex = -1; }

            if (Input.MenuCancel.Pressed) {
                Input.MenuCancel.ConsumePress();
                if (confirmingIndex >= 0)
                    confirmingIndex = -1;
                else
                    SetOpen(false);
                return;
            }

            if (Input.MenuConfirm.Pressed && items.Count > 0) {
                Input.MenuConfirm.ConsumePress();
                cursor = Calc.Clamp(cursor, 0, items.Count - 1);
                Item item = items[cursor];

                // destructive options need a second press
                if (item.NeedsConfirm && confirmingIndex != cursor) {
                    confirmingIndex = cursor;
                    return;
                }

                confirmingIndex = -1;
                Logger.Log(LogLevel.Info, nameof(BingoEloModule), $"menu: {item.Label}");
                item.Action?.Invoke();
            }
        }

        public void OpenFromMainMenu() => SetOpen(true);

        private void SetOpen(bool open) {
            if (open == Open)
                return;

            Open = open;
            confirmingIndex = -1;
            showLeaderboard = false;
            StopChallengeEntry();

            if (open) {
                cursor = 0;
                busy = null;
                justOpened = true;
                Input.MenuConfirm.ConsumePress();
                ParkPlayer();
                SupabaseClient.FireAndForget(async () => {
                    await LadderService.ConnectAsync();
                    await MatchState.RefreshAsync();
                    incomingChallenges = await LadderService.GetIncomingChallengesAsync();
                }, "menu refresh");
            } else {
                UnparkPlayer();
            }
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

        public override void Removed(Scene scene) {
            if (holdsControl) {
                holdsControl = false;
                GameControlLock.Release(scene);
            }
            StopChallengeEntry();
            base.Removed(scene);
        }

        private void StartChallengeEntry() {
            enteringChallengeName = true;
            challengeName.Clear();
            if (!challengeListening) {
                TextInput.OnInput += OnChallengeNameInput;
                challengeListening = true;
            }
        }

        private void StopChallengeEntry() {
            enteringChallengeName = false;
            if (challengeListening) {
                TextInput.OnInput -= OnChallengeNameInput;
                challengeListening = false;
            }
        }

        private void OnChallengeNameInput(char c) {
            if (!enteringChallengeName)
                return;

            if (c == '\b') {
                if (challengeName.Length > 0)
                    challengeName.Remove(challengeName.Length - 1, 1);
                return;
            }

            if (c == '\r' || c == '\n') {
                SubmitChallenge();
                return;
            }

            if (c < ' ')
                return;

            if (challengeName.Length < MaxNameLength)
                challengeName.Append(c);
        }

        private void SubmitChallenge() {
            string name = challengeName.ToString().Trim();
            StopChallengeEntry();
            if (name.Length == 0)
                return;

            Run("challenging...", async () => {
                await LadderService.ConnectAsync();
                await LadderService.ChallengePlayerAsync(name);
                busy = $"challenged {name}, waiting for them to accept";
            });
        }

        private void BuildItems() {
            items.Clear();

            bool inMatch = MatchState.HasMatch && !MatchState.Finished;
            bool inTraining = inMatch && MatchState.IsTraining;
            bool inRanked = inMatch && !MatchState.IsTraining;

            // readiness is settled once the countdown starts
            if (inMatch && !MatchState.HasStarted && MatchState.CountdownStartedAt == null) {
                items.Add(new Item {
                    Label = MatchState.IAmReady ? "Cancel Ready" : "Ready Up",
                    Action = () => {
                        bool want = !MatchState.IAmReady;
                        Run(want ? "readying up..." : "no longer ready", async () => {
                            await LadderService.SetReadyAsync(MatchState.MatchId, want);
                            await MatchState.RefreshLiveAsync();
                            busy = null;
                        });
                    }
                });

                // always shown; the server explains if the opponent isn't ready
                if (MatchState.CountdownStartedAt == null) {
                    items.Add(new Item {
                        Label = "Start Countdown",
                        Action = () => Run("starting countdown...", async () => {
                            await LadderService.StartCountdownAsync(MatchState.MatchId);
                            await MatchState.RefreshLiveAsync();
                            busy = null;
                            BingoEloModule.OnMainThread(() => SetOpen(false));
                        })
                    });
                }
            }

            // the only way back in after Save & Quit, since file select doesn't list the bingo save
            if (inMatch && MatchState.HasStarted && !(Scene is Level)) {
                items.Add(new Item {
                    Label = "Return to Run",
                    Action = () => {
                        SetOpen(false);
                        BingoEloModule.OnMainThread(BingoSave.ReturnToRun);
                    }
                });
            }

            if (inMatch) {
                items.Add(new Item {
                    Label = "Open Board",
                    Action = () => {
                        SetOpen(false);
                        BingoBoardUI.Instance?.OpenFor(BoardMode.Interacting);
                    }
                });
            }

            if (!inMatch) {
                if (LadderService.IsQueued) {
                    items.Add(new Item {
                        Label = "Leave Queue",
                        Action = () => Run("leaving queue...", async () => {
                            await LadderService.LeaveQueueAsync();
                            busy = null;
                        })
                    });
                } else {
                    items.Add(new Item {
                        Label = "Find Match",
                        Action = () => Run("searching for an opponent...", async () => {
                            await LadderService.ConnectAsync();
                            await LadderService.JoinQueueAsync();
                            string id = await LadderService.TryMatchmakeAsync();
                            if (!string.IsNullOrEmpty(id))
                                await MatchState.RefreshAsync();
                            busy = string.IsNullOrEmpty(id) ? "in queue, waiting for an opponent" : null;
                        })
                    });
                }

                if (BingoEloModule.TrainingDummyEnabled) {
                    items.Add(new Item {
                        Label = "Practice vs Training Dummy",
                        Action = () => Run("starting practice...", async () => {
                            await LadderService.ConnectAsync();
                            string id = await LadderService.SpawnTrainingMatchAsync();
                            if (!string.IsNullOrEmpty(id))
                                await MatchState.RefreshAsync();
                            busy = string.IsNullOrEmpty(id) ? "couldn't start practice" : null;
                        })
                    });
                }

                items.Add(new Item {
                    Label = "Private Match",
                    Action = StartChallengeEntry
                });

                foreach (Challenge challenge in incomingChallenges) {
                    string fromName = challenge.FromProfile?.RankedName ?? "someone";
                    string fromId = challenge.FromPlayer;
                    items.Add(new Item {
                        Label = $"Accept Challenge from {fromName}",
                        Action = () => Run("starting match...", async () => {
                            string id = await LadderService.AcceptChallengeAsync(fromId);
                            if (!string.IsNullOrEmpty(id))
                                await MatchState.RefreshAsync();
                            busy = string.IsNullOrEmpty(id) ? "couldn't start the match" : null;
                        })
                    });
                    items.Add(new Item {
                        Label = $"Decline Challenge from {fromName}",
                        Action = () => Run("declining...", async () => {
                            await LadderService.DeclineChallengeAsync(fromId);
                            incomingChallenges = await LadderService.GetIncomingChallengesAsync();
                            busy = null;
                        })
                    });
                }
            }

            items.Add(new Item {
                Label = "Leaderboard",
                Action = () => {
                    showLeaderboard = true;
                    FetchLeaderboardPage(0);
                }
            });

            if (inTraining) {
                items.Add(new Item {
                    Label = "End Practice",
                    Action = () => Run("ending practice...", async () => {
                        await LadderService.EndTrainingAsync();
                        MatchState.Clear();
                        await MatchState.RefreshAsync();
                        busy = null;
                    })
                });
            }

            if (inRanked) {
                items.Add(new Item {
                    Label = "Concede Match",
                    Danger = true,
                    NeedsConfirm = true,
                    Action = () => Run("conceding...", async () => {
                        await LadderService.ConcedeAsync(MatchState.MatchId);
                        await MatchState.RefreshLiveAsync();
                        busy = null;
                    })
                });
            }

            if (MatchState.HasMatch) {
                // leaving a live ranked match forfeits it
                bool rankedLive = !MatchState.IsTraining && !MatchState.Finished;
                items.Add(new Item {
                    Label = rankedLive ? "Leave Room  (you forfeit)" : "Leave Room",
                    Danger = rankedLive,
                    NeedsConfirm = rankedLive,
                    Action = () => Run("leaving room...", async () => {
                        if (MatchState.IsTraining && !MatchState.Finished)
                            await LadderService.EndTrainingAsync();
                        BingoEloModule.LeaveRoom();
                        busy = null;
                    })
                });
            }

            items.Add(new Item { Label = "Close", Action = () => SetOpen(false) });

            cursor = Calc.Clamp(cursor, 0, Math.Max(0, items.Count - 1));
        }

        private void FetchLeaderboardPage(int page) {
            leaderboardPage = page;
            leaderboard = null;
            SupabaseClient.FireAndForget(async () => {
                Profile[] rows = await LadderService.GetLeaderboardAsync(
                    LeaderboardPageSize, page * LeaderboardPageSize);
                BingoEloModule.OnMainThread(() => {
                    // drop stale responses
                    if (leaderboardPage != page)
                        return;
                    leaderboard = rows;
                    leaderboardHasMore = rows != null && rows.Length == LeaderboardPageSize;
                });
            }, "leaderboard");
        }

        private void Run(string message, Func<System.Threading.Tasks.Task> work) {
            busy = message;
            SupabaseClient.FireAndForget(async () => {
                try {
                    await work();
                } catch (SupabaseException e) {
                    busy = Describe(e);
                }
            }, message);
        }

        private static string Describe(SupabaseException e) {
            string body = e.Body ?? "";
            if (body.Contains("not everyone is ready")) return "your opponent hasn't readied up yet";
            if (body.Contains("finish your ranked match")) return "finish your ranked match first";
            if (body.Contains("match is not active")) return "that match has ended";
            if (body.Contains("you are not in this match")) return "you're not in that match";
            if (body.Contains("no player named")) return "no player by that name";
            if (body.Contains("finish your current match first")) return "finish your current match first";
            if (body.Contains("one of you is already in a match")) return "one of you is already in a match";
            if (body.Contains("no pending challenge")) return "that challenge is gone";
            return "something went wrong";
        }

        public override void Render() {
            if (fade <= 0.01f)
                return;

            float alpha = Ease.CubeOut(fade);
            Draw.Rect(0f, 0f, 1920f, 1080f, Color.Black * (0.86f * alpha));

            if (enteringChallengeName) {
                RenderChallengeEntry(alpha);
                return;
            }

            if (showLeaderboard) {
                RenderLeaderboard(alpha);
                return;
            }

            float panelH = 190f + items.Count * RowH;
            float x = (1920f - PanelW) / 2f;
            float y = (1080f - panelH) / 2f;

            Draw.Rect(x, y, PanelW, panelH, new Color(20, 20, 26) * (0.96f * alpha));
            Draw.HollowRect(x, y, PanelW, panelH, Color.White * (0.35f * alpha));

            float cx = x + PanelW / 2f;
            Centered("Bingo Ranked", new Vector2(cx, y + 46f), 0.85f, Color.White * alpha);
            Centered(HeaderLine(), new Vector2(cx, y + 96f), 0.46f, new Color(190, 190, 205) * alpha);

            float rowY = y + 150f;
            for (int i = 0; i < items.Count; i++) {
                Item item = items[i];
                bool selected = i == cursor;
                bool confirming = confirmingIndex == i;

                if (selected)
                    Draw.Rect(x + 20f, rowY - RowH / 2f + 6f, PanelW - 40f, RowH - 12f,
                        (item.Danger ? new Color(90, 30, 34) : new Color(52, 52, 66)) * alpha);

                string label = confirming ? $"{item.Label}  -  press again to confirm" : item.Label;
                Color color = item.Danger
                    ? (selected ? new Color(255, 190, 190) : new Color(215, 130, 135))
                    : (selected ? Color.White : new Color(175, 175, 190));

                Centered(label, new Vector2(cx, rowY), 0.52f, color * alpha);
                rowY += RowH;
            }

            string footer = busy ?? "Move to choose  -  Confirm to select  -  Cancel to close";
            Centered(footer, new Vector2(cx, y + panelH - 34f), 0.4f,
                (busy != null ? new Color(235, 220, 150) : new Color(140, 140, 155)) * alpha);
        }

        private string HeaderLine() {
            if (!SupabaseAuth.IsSignedIn)
                return "connecting...";

            Profile me = LadderService.Me;
            string who = me == null
                ? "connected"
                : $"{me.RankedName}   {me.Elo} ELO   {me.Wins}W / {me.Losses}L";

            if (MatchState.Finished && MatchState.ResultText != null)
                return $"{who}   -   {MatchState.ResultText}";

            if (MatchState.HasMatch) {
                string kind = MatchState.IsTraining ? "practice" : MatchState.IsPrivate ? "private" : "ranked";

                if (!MatchState.HasStarted) {
                    string state =
                        MatchState.CountdownStartedAt != null ? "counting down" :
                        MatchState.IsTraining ? (MatchState.IAmReady ? "ready" : "not ready") :
                        MatchState.IAmReady && MatchState.OpponentReady ? "both ready" :
                        MatchState.IAmReady ? "you're ready, opponent isn't" :
                        MatchState.OpponentReady ? "opponent is ready, you aren't" :
                        "nobody ready yet";
                    return $"{who}   -   {kind}, {state}";
                }

                return $"{who}   -   {kind} {MatchState.MyClaims}/{MatchState.WinTarget}";
            }
            if (LadderService.IsQueued)
                return $"{who}   -   in queue";
            return who;
        }

        private void RenderChallengeEntry(float alpha) {
            float panelW = 700f;
            float panelH = 220f;
            float x = (1920f - panelW) / 2f;
            float y = (1080f - panelH) / 2f;

            Draw.Rect(x, y, panelW, panelH, new Color(20, 20, 26) * (0.96f * alpha));
            Draw.HollowRect(x, y, panelW, panelH, Color.White * (0.35f * alpha));

            float cx = x + panelW / 2f;
            Centered("Challenge a Player", new Vector2(cx, y + 46f), 0.6f, Color.White * alpha);
            Centered("no ELO at stake", new Vector2(cx, y + 82f), 0.38f, new Color(140, 140, 155) * alpha);

            float boxW = panelW - 80f;
            float boxX = x + 40f;
            float boxY = y + 118f;
            Draw.Rect(boxX, boxY, boxW, 56f, Color.Black * (0.6f * alpha));
            Draw.HollowRect(boxX, boxY, boxW, 56f, Color.White * (0.3f * alpha));

            bool caret = ((int) (Engine.Scene.TimeActive * 2f) % 2) == 0;
            string typed = challengeName.ToString();
            string shown = typed.Length == 0 ? (caret ? "_" : "") : typed + (caret ? "_" : "");
            ActiveFont.Draw(shown, new Vector2(boxX + 14f, boxY + 28f), new Vector2(0f, 0.5f),
                Vector2.One * 0.5f, Color.White * alpha);
            if (typed.Length == 0) {
                ActiveFont.Draw("player's display name", new Vector2(boxX + boxW - 14f, boxY + 28f),
                    new Vector2(1f, 0.5f), Vector2.One * 0.42f, Color.Gray * (0.8f * alpha));
            }

            Centered("Enter to send  -  Escape to cancel", new Vector2(cx, y + panelH - 30f), 0.4f,
                new Color(140, 140, 155) * alpha);
        }

        private void RenderLeaderboard(float alpha) {
            float panelW = 900f;
            float panelH = 700f;
            float x = (1920f - panelW) / 2f;
            float y = (1080f - panelH) / 2f;

            Draw.Rect(x, y, panelW, panelH, new Color(20, 20, 26) * (0.96f * alpha));
            Draw.HollowRect(x, y, panelW, panelH, Color.White * (0.35f * alpha));

            float cx = x + panelW / 2f;
            Centered("Leaderboard", new Vector2(cx, y + 50f), 0.8f, Color.White * alpha);
            if (leaderboardPage > 0 || leaderboardHasMore) {
                Centered($"page {leaderboardPage + 1}", new Vector2(cx, y + 84f), 0.4f,
                    new Color(140, 140, 155) * alpha);
            }

            if (leaderboard == null) {
                Centered("loading...", new Vector2(cx, y + panelH / 2f), 0.5f, Color.Gray * alpha);
            } else if (leaderboard.Length == 0) {
                Centered(leaderboardPage == 0 ? "no players yet" : "no more players",
                    new Vector2(cx, y + panelH / 2f), 0.5f, Color.Gray * alpha);
            } else {
                float rowY = y + 120f;
                string me = SupabaseAuth.UserId;
                for (int i = 0; i < leaderboard.Length; i++) {
                    Profile p = leaderboard[i];
                    bool isMe = p.Id == me;
                    Color color = isMe ? new Color(255, 225, 140) : Color.White;

                    ActiveFont.Draw($"{leaderboardPage * LeaderboardPageSize + i + 1}.", new Vector2(x + 50f, rowY),
                        new Vector2(0f, 0.5f), Vector2.One * 0.45f, new Color(140, 140, 155) * alpha);
                    ActiveFont.Draw(p.RankedName, new Vector2(x + 120f, rowY), new Vector2(0f, 0.5f),
                        Vector2.One * 0.48f, color * alpha);
                    ActiveFont.Draw($"{p.Elo}", new Vector2(x + panelW - 210f, rowY), new Vector2(1f, 0.5f),
                        Vector2.One * 0.48f, color * alpha);
                    ActiveFont.Draw($"{p.Wins}W / {p.Losses}L", new Vector2(x + panelW - 50f, rowY),
                        new Vector2(1f, 0.5f), Vector2.One * 0.4f, new Color(150, 150, 165) * alpha);

                    rowY += 52f;
                }
            }

            string navHint = leaderboardPage > 0 && leaderboardHasMore ? "Left / Right for more pages"
                : leaderboardHasMore ? "Right for more pages"
                : leaderboardPage > 0 ? "Left for previous page"
                : null;
            if (navHint != null) {
                Centered(navHint, new Vector2(cx, y + panelH - 66f), 0.38f, new Color(140, 140, 155) * alpha);
            }
            Centered("Confirm or Cancel to go back", new Vector2(cx, y + panelH - 40f),
                0.4f, new Color(140, 140, 155) * alpha);
        }

        private static void Centered(string text, Vector2 position, float scale, Color color)
            => ActiveFont.Draw(text, position, new Vector2(0.5f, 0.5f), Vector2.One * scale, color);
    }
}
