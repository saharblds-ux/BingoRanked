using System;
using System.Collections.Generic;
using System.Text;
using Celeste.Mod.BingoElo.Net;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.BingoElo.UI {

    public enum BoardMode {
        Closed,
        // visible while you keep playing, read-only
        Viewing,
        // board takes your inputs, Madeline is parked
        Interacting
    }

    // Always opens hidden, so a glance can't spoil the goals.
    public class BingoBoardUI : Entity {

        private const int Size = 5;

        // square cells; height is the limit on 16:9 (5 * 163 + 4 * 8 = 847)
        private const float FullCell = 163f;
        private const float FullGap = 8f;
        private const float FullTop = 110f;

        public static BingoBoardUI Instance { get; private set; }

        // The entity is recreated per scene, so this carries "open" across
        // transitions. Only Viewing is restored, never Interacting.
        private static BoardMode lastOpenMode = BoardMode.Closed;

        public BoardMode Mode { get; private set; } = BoardMode.Closed;
        public bool Open => Mode != BoardMode.Closed;

        // what to draw as while fading out, after Mode is already Closed
        private BoardMode renderMode = BoardMode.Viewing;

        private float fade;
        private int cursor;
        private float refreshTimer;
        private bool holdsControl;
        private bool confirmingConcede;

        public BingoBoardUI() {
            Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate | Tags.FrozenUpdate;
            Depth = -100000;
            Instance = this;
        }

        // Also covers the key-rebinding screen, otherwise binding the board key opens it and the bind never sticks.
        private static bool InputBelongsElsewhere() {
            if (Engine.Commands != null && Engine.Commands.Open)
                return true;

            // precedence: chat > menu > board > game
            if (BingoChatUI.Instance != null && BingoChatUI.Instance.Open)
                return true;
            if (BingoMenuUI.Instance != null && BingoMenuUI.Instance.Open)
                return true;

            Scene scene = Engine.Scene;
            if (scene == null)
                return true;

            if (scene.Entities.FindFirst<TextMenu>() != null)
                return true;

            // transitions and cutscenes are deliberately not excluded
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

            BingoEloSettings settings = BingoEloModule.Settings;

            if (settings.ViewBoard.Pressed) {
                settings.ViewBoard.ConsumePress();
                SetMode(Mode == BoardMode.Viewing ? BoardMode.Closed : BoardMode.Viewing);
            } else if (settings.InteractWithBoard.Pressed) {
                settings.InteractWithBoard.ConsumePress();
                SetMode(Mode == BoardMode.Interacting ? BoardMode.Closed : BoardMode.Interacting);
            }

            fade = Calc.Approach(fade, Open ? 1f : 0f, Engine.RawDeltaTime * 6f);

            if (Mode == BoardMode.Interacting)
                UpdateInteraction();

            if (!Open || MatchState.Finished)
                return;

            refreshTimer -= Engine.RawDeltaTime;
            if (refreshTimer <= 0f) {
                refreshTimer = 3f;
                SupabaseClient.FireAndForget(MatchState.RefreshLiveAsync, "refresh claims");
            }
        }

        private void UpdateInteraction() {
            if (confirmingConcede) {
                if (Input.MenuConfirm.Pressed) {
                    Input.MenuConfirm.ConsumePress();
                    confirmingConcede = false;
                    Concede();
                } else if (Input.MenuCancel.Pressed) {
                    Input.MenuCancel.ConsumePress();
                    confirmingConcede = false;
                }
                return;
            }

            if (!MatchState.Revealed) {
                // ranked can't reveal before the countdown, that's an unfair head start
                bool canRevealNow = MatchState.IsTraining || MatchState.IsPrivate
                    || (MatchState.CountdownElapsed is double e && e >= MatchState.RevealAt);
                if (canRevealNow && Input.MenuConfirm.Pressed) {
                    Input.MenuConfirm.ConsumePress();
                    MatchState.Revealed = true;
                }
                return;
            }

            if (!MatchState.HasMatch)
                return;

            // the interact bind closes the board, so Cancel asks to concede
            if (!MatchState.Finished && Input.MenuCancel.Pressed) {
                Input.MenuCancel.ConsumePress();
                confirmingConcede = true;
                return;
            }

            if (MatchState.Finished)
                return;

            if (Input.MenuLeft.Pressed) cursor = Math.Max(0, cursor - 1);
            if (Input.MenuRight.Pressed) cursor = Math.Min(Size * Size - 1, cursor + 1);
            if (Input.MenuUp.Pressed) cursor = Math.Max(0, cursor - Size);
            if (Input.MenuDown.Pressed) cursor = Math.Min(Size * Size - 1, cursor + Size);

            int? hovered = HoveredTile();
            if (hovered != null) {
                cursor = hovered.Value;
                if (MInput.Mouse.PressedLeftButton) {
                    Claim(cursor);
                    return;
                }
                // right-click stars a tile, client-side only
                if (MInput.Mouse.PressedRightButton) {
                    if (!MatchState.StarredTiles.Remove(cursor))
                        MatchState.StarredTiles.Add(cursor);
                    return;
                }
            }

            if (Input.MenuConfirm.Pressed) {
                Input.MenuConfirm.ConsumePress();
                Claim(cursor);
            }
        }

        // MInput.Mouse.Position is already in HUD space, don't apply ScreenMatrix again
        private int? HoveredTile() {
            Vector2 mouse = MInput.Mouse.Position;
            Layout l = GetLayout();

            float relX = mouse.X - l.X;
            float relY = mouse.Y - l.Y;
            if (relX < 0f || relY < 0f || relX >= l.Grid || relY >= l.Grid)
                return null;

            int col = (int) (relX / (l.Cell + l.Gap));
            int row = (int) (relY / (l.Cell + l.Gap));
            if (col >= Size || row >= Size)
                return null;

            // in the gap after the cell
            if (relX - col * (l.Cell + l.Gap) > l.Cell || relY - row * (l.Cell + l.Gap) > l.Cell)
                return null;

            return row * Size + col;
        }

        public void OpenFor(BoardMode mode) => SetMode(mode);

        public void ForceClose() => SetMode(BoardMode.Closed);

        private void SetMode(BoardMode mode) {
            if (mode == Mode)
                return;

            bool wasInteracting = Mode == BoardMode.Interacting;
            Mode = mode;
            lastOpenMode = mode;
            confirmingConcede = false;

            if (mode != BoardMode.Closed)
                renderMode = mode;

            if (wasInteracting && mode != BoardMode.Interacting) {
                UnparkPlayer();
                Engine.Instance.IsMouseVisible = false;
            }
            if (mode == BoardMode.Interacting) {
                ParkPlayer();
                Engine.Instance.IsMouseVisible = true;
            }

            if (mode == BoardMode.Closed)
                return;

            cursor = 0;
            SupabaseClient.FireAndForget(MatchState.RefreshAsync, "refresh board");
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
                Engine.Instance.IsMouseVisible = false;
            }
            base.Removed(scene);
        }

        public override void Awake(Scene scene) {
            base.Awake(scene);
            if (lastOpenMode != BoardMode.Closed && MatchState.HasMatch)
                SetMode(BoardMode.Viewing);
        }

        // toggles: claims an empty square, unclaims one of yours
        private static void Claim(int position) {
            if (!MatchState.HasMatch || MatchState.Finished)
                return;

            string owner = MatchState.OwnerOf(position);
            if (owner == "them")
                return;

            bool unclaiming = owner == "me";

            // Blocks a claim only when verification reads "not done". Binoculars are
            // skipped (the BingoUI read is less reliable) and Unknown passes through.
            if (!unclaiming) {
                Goal goal = FindTile(position)?.Goal;
                bool isBinoculars = goal?.CheckKind != null && goal.CheckKind.StartsWith("binoculars");
                if (!isBinoculars && GoalVerification.Check(goal) == GoalVerification.Status.NotYet) {
                    MatchState.SetStatus("you haven't done that yet");
                    return;
                }
            }

            string matchId = MatchState.MatchId;
            SupabaseClient.FireAndForget(async () => {
                try {
                    ClaimResult result = unclaiming
                        ? await LadderService.UnclaimTileAsync(matchId, position)
                        : await LadderService.ClaimTileAsync(matchId, position);
                    MatchState.SetStatus(result.Won
                        ? $"YOU WIN!  {result.Claimed}/{result.Target}"
                        : unclaiming
                            ? $"unclaimed  {result.Claimed}/{result.Target}"
                            : $"claimed  {result.Claimed}/{result.Target}");
                } catch (SupabaseException e) {
                    MatchState.SetStatus(Describe(e));
                }
                await MatchState.RefreshLiveAsync();
            }, unclaiming ? "unclaim tile" : "claim tile");
        }

        private static void Concede() {
            if (!MatchState.HasMatch || MatchState.Finished)
                return;

            string matchId = MatchState.MatchId;
            SupabaseClient.FireAndForget(async () => {
                try {
                    await LadderService.ConcedeAsync(matchId);
                } catch (SupabaseException e) {
                    MatchState.SetStatus(Describe(e));
                }
                await MatchState.RefreshLiveAsync();
            }, "concede");
        }

        private static string Describe(SupabaseException e) {
            string body = e.Body ?? "";
            if (body.Contains("tile already claimed")) return "that square is already taken";
            if (body.Contains("match is not active")) return "this match has ended";
            if (body.Contains("you are not in this match")) return "you're not in this match";
            return "couldn't do that";
        }

        // Interacting takes the screen, viewing shrinks and tucks left
        private struct Layout {
            public float Scale, Cell, Gap, Grid, X, Y;
            public bool Compact;
        }

        private Layout GetLayout() {
            bool viewing = renderMode == BoardMode.Viewing;
            float scale = viewing
                ? Calc.Clamp(BingoEloModule.Settings.ViewBoardSize, 30, 100) / 100f
                : 1f;

            Layout l = new Layout {
                Scale = scale,
                Compact = viewing,
                Cell = FullCell * scale,
                Gap = FullGap * scale
            };
            l.Grid = Size * l.Cell + (Size - 1) * l.Gap;

            if (viewing) {
                l.X = 36f;
                l.Y = (1080f - l.Grid) / 2f;
            } else {
                l.X = (1920f - l.Grid) / 2f;
                l.Y = FullTop;
            }
            return l;
        }

        public override void Render() {
            RenderStatusIndicator();

            if (fade <= 0.01f)
                return;

            float alpha = Ease.CubeOut(fade);
            Layout l = GetLayout();

            if (l.Compact) {
                float bgAlpha = Calc.Clamp(BingoEloModule.Settings.BoardBackgroundOpacity, 20, 100) / 100f;
                Draw.Rect(l.X - 14f, l.Y - 48f, l.Grid + 28f, l.Grid + 96f,
                    Color.Black * (bgAlpha * alpha));
            } else {
                Draw.Rect(0f, 0f, 1920f, 1080f, Color.Black * (0.88f * alpha));
            }

            float cx = l.X + l.Grid / 2f;

            if (!MatchState.HasMatch) {
                Centered(MatchState.Status ?? "no active match",
                    new Vector2(cx, l.Y + l.Grid / 2f), l.Compact ? 0.6f : 1f, Color.White * alpha);
                return;
            }

            Centered($"{MatchState.MyClaims} / {MatchState.WinTarget}   -   opponent {MatchState.TheirClaims}",
                new Vector2(cx, l.Y - 26f), l.Compact ? 0.5f : 0.8f, Color.White * alpha);

            if (!MatchState.Revealed) {
                RenderHidden(l, alpha);
                return;
            }

            float tileAlpha = alpha * (Calc.Clamp(BingoEloModule.Settings.TileOpacity, 20, 100) / 100f);

            for (int i = 0; i < Size * Size; i++) {
                float x = l.X + (i % Size) * (l.Cell + l.Gap);
                float y = l.Y + (i / Size) * (l.Cell + l.Gap);

                string owner = MatchState.OwnerOf(i);
                Color fill =
                    owner == "me" ? new Color(40, 110, 60) :
                    owner == "them" ? new Color(120, 40, 45) :
                    new Color(28, 28, 34);

                Draw.Rect(x, y, l.Cell, l.Cell, fill * tileAlpha);

                if (renderMode == BoardMode.Interacting && i == cursor && !MatchState.Finished) {
                    Draw.HollowRect(x, y, l.Cell, l.Cell, Color.Gold * alpha);
                    Draw.HollowRect(x + 1f, y + 1f, l.Cell - 2f, l.Cell - 2f, Color.Gold * alpha);
                }

                // drop the star once anyone claims the tile
                if (MatchState.StarredTiles.Contains(i)) {
                    if (owner != null)
                        MatchState.StarredTiles.Remove(i);
                    else
                        DrawStar(new Vector2(x + 14f * l.Scale, y + 14f * l.Scale), 7f * l.Scale, Color.Gold * alpha);
                }

                BoardTile tile = FindTile(i);
                if (tile?.Goal == null)
                    continue;

                bool ready = owner == null && GoalVerification.Check(tile.Goal) == GoalVerification.Status.Ready;
                if (ready) {
                    Draw.HollowRect(x + 2f, y + 2f, l.Cell - 4f, l.Cell - 4f, Color.LimeGreen * alpha);
                    Draw.HollowRect(x + 3f, y + 3f, l.Cell - 6f, l.Cell - 6f, Color.LimeGreen * alpha);
                }

                float pad = l.Cell * 0.08f;
                DrawFitted(tile.Goal.Text,
                    x + l.Cell / 2f, y + l.Cell / 2f,
                    l.Cell - pad * 2f, l.Cell - pad * 2f,
                    (owner == null ? Color.White : Color.White * 0.85f) * alpha,
                    0.45f * l.Scale);

                if (ready) {
                    Centered("ready!", new Vector2(x + l.Cell / 2f, y + l.Cell - 16f * l.Scale),
                        0.32f * l.Scale, Color.LimeGreen * alpha);
                }
            }

            RenderFooter(l, alpha, cx);

            if (MatchState.Finished)
                RenderResult(l, alpha, cx);
            else if (confirmingConcede)
                RenderConcedePrompt(l, alpha, cx);
        }

        private void RenderHidden(Layout l, float alpha) {
            for (int i = 0; i < Size * Size; i++) {
                float x = l.X + (i % Size) * (l.Cell + l.Gap);
                float y = l.Y + (i / Size) * (l.Cell + l.Gap);
                Draw.Rect(x, y, l.Cell, l.Cell, new Color(24, 24, 30) * alpha);
                Centered("?", new Vector2(x + l.Cell / 2f, y + l.Cell / 2f),
                    0.9f * l.Scale, Color.DarkGray * alpha);
            }

            float cx = l.X + l.Grid / 2f;
            Centered("Board hidden", new Vector2(cx, l.Y + l.Grid + 22f),
                0.7f * (l.Compact ? 0.8f : 1f), Color.White * alpha);

            string manualHint = renderMode == BoardMode.Interacting
                ? "Press Confirm to reveal" : "Open with your interact bind to reveal";

            string hint =
                (MatchState.CountdownRunning && BingoEloModule.Settings.AutoRevealOnCountdown) ? "revealing on the countdown" :
                MatchState.CountdownRunning ? manualHint :
                MatchState.EveryoneReady ? "ready, type /countdown in chat" :
                MatchState.IAmReady ? "waiting for your opponent to ready up" :
                manualHint;

            Centered(hint, new Vector2(cx, l.Y + l.Grid + 60f),
                0.45f * (l.Compact ? 0.85f : 1f), Color.Gray * alpha);
        }

        private void RenderFooter(Layout l, float alpha, float cx) {
            Centered(MatchState.Status ?? "", new Vector2(cx, l.Y + l.Grid + 24f),
                0.55f * (l.Compact ? 0.8f : 1f), Color.LightGray * alpha);

            if (!l.Compact && !MatchState.Finished)
                Centered("Move or hover to choose  -  Confirm or click to tick  -  Cancel to concede",
                    new Vector2(cx, l.Y + l.Grid + 64f), 0.45f, Color.Gray * alpha);
        }

        // below the grid, so it doesn't cover the tile that ended the match
        private void RenderResult(Layout l, float alpha, float cx) {
            const float BannerH = 64f;
            const float Gap = 44f;
            float y = l.Y + l.Grid + Gap;

            Color tint = MatchState.IWon ? new Color(30, 90, 50) : new Color(95, 32, 38);
            Draw.Rect(l.X, y, l.Grid, BannerH, tint * (0.94f * alpha));
            Draw.HollowRect(l.X, y, l.Grid, BannerH, Color.White * (0.5f * alpha));

            Centered(MatchState.ResultText ?? (MatchState.IWon ? "You win!" : "You lost."),
                new Vector2(cx, y + BannerH / 2f), l.Compact ? 0.5f : 0.8f, Color.White * alpha);
        }

        private void RenderConcedePrompt(Layout l, float alpha, float cx) {
            float h = 150f;
            float y = l.Y + (l.Grid - h) / 2f;

            Draw.Rect(l.X, y, l.Grid, h, new Color(70, 26, 30) * (0.96f * alpha));
            Draw.HollowRect(l.X, y, l.Grid, h, Color.White * (0.55f * alpha));

            Centered("Concede this match?", new Vector2(cx, y + 46f), 0.8f, Color.White * alpha);
            Centered("You lose, and it counts. Confirm to concede  -  Cancel to go back",
                new Vector2(cx, y + 100f), 0.42f, new Color(240, 200, 200) * alpha);
        }

        // corner readout, hidden while the board itself is up
        private void RenderStatusIndicator() {
            if (!BingoEloModule.Settings.ShowStatusIndicator)
                return;

            float hide = 1f - Calc.Clamp(fade * 2f, 0f, 1f);
            if (hide <= 0.01f)
                return;

            string text = null;
            Color color = Color.White;

            if (MatchState.Finished) {
                text = MatchState.ResultText;
                color = MatchState.IWon ? new Color(120, 230, 150) : new Color(240, 140, 145);
            } else if (MatchState.HasMatch) {
                text = $"{MatchState.MyClaims} / {MatchState.WinTarget}   opp {MatchState.TheirClaims}";
            } else if (LadderService.IsQueued) {
                TimeSpan waited = DateTime.UtcNow - LadderService.QueuedSince;
                text = $"In queue   {(int) waited.TotalMinutes}:{waited.Seconds:00}";
                color = new Color(200, 200, 210);
            }

            if (text == null)
                return;

            Vector2 at = new Vector2(1890f, 40f);
            ActiveFont.Draw(text, at + new Vector2(2f, 2f), new Vector2(1f, 0f),
                Vector2.One * 0.42f, Color.Black * (0.6f * hide));
            ActiveFont.Draw(text, at, new Vector2(1f, 0f), Vector2.One * 0.42f, color * hide);
        }

        // eight lines out of a point, no sprite needed
        private static void DrawStar(Vector2 center, float radius, Color color) {
            float thick = Math.Max(1.5f, radius * 0.27f);
            for (int i = 0; i < 4; i++) {
                float angle = i * MathHelper.PiOver2;
                Draw.LineAngle(center, angle, radius, color, thick);
                Draw.LineAngle(center, angle + MathHelper.PiOver4, radius * 0.5f, color, thick * 0.7f);
            }
        }

        private static void Centered(string text, Vector2 position, float scale, Color color)
            => ActiveFont.Draw(text, position, new Vector2(0.5f, 0.5f), Vector2.One * scale, color);

        private static BoardTile FindTile(int position) {
            BoardTile[] tiles = MatchState.Tiles;
            if (tiles == null)
                return null;
            foreach (BoardTile tile in tiles)
                if (tile.Position == position)
                    return tile;
            return null;
        }

        // Scan Assist palette. Red/B-Side share a color and Blue is teal on purpose.
        private static readonly Dictionary<string, Color> HighlightWords = new Dictionary<string, Color> {
            { "Hearts", new Color(146, 144, 6) },
            { "Heart", new Color(146, 144, 6) },
            { "Berries", new Color(111, 0, 0) },
            { "Blue", new Color(22, 138, 132) },
            { "Cassette", new Color(141, 79, 18) },
            { "Cassettes", new Color(141, 79, 18) },
            { "B-Side", new Color(174, 38, 145) },
            { "B-Sides", new Color(174, 38, 145) },
            { "Red", new Color(174, 38, 145) },
            { "Collectibles", new Color(32, 126, 16) },
            { "Binoculars", new Color(103, 103, 103) },
            { "Binocular", new Color(103, 103, 103) },
            { "A-Sides", new Color(82, 31, 146) },
        };

        private static readonly List<string> WrapBuffer = new List<string>();

        private static void Wrap(string text, float maxWidth, float scale) {
            WrapBuffer.Clear();
            StringBuilder line = new StringBuilder();

            foreach (string word in text.Split(' ')) {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (ActiveFont.Measure(candidate).X * scale > maxWidth && line.Length > 0) {
                    WrapBuffer.Add(line.ToString());
                    line.Clear().Append(word);
                } else {
                    line.Clear().Append(candidate);
                }
            }

            if (line.Length > 0)
                WrapBuffer.Add(line.ToString());
        }

        // shrinks the text until it fits the box
        private static void DrawFitted(string text, float cx, float cy,
                                       float maxW, float maxH, Color color, float maxScale) {
            if (string.IsNullOrEmpty(text))
                return;

            const float MinScale = 0.10f;
            float scale = maxScale;

            while (true) {
                Wrap(text, maxW, scale);
                float lineHeight = ActiveFont.LineHeight * scale;
                if (WrapBuffer.Count * lineHeight <= maxH || scale <= MinScale)
                    break;
                scale -= 0.015f;
            }

            float h = ActiveFont.LineHeight * scale;
            float y = cy - WrapBuffer.Count * h / 2f + h / 2f;

            bool scanAssist = BingoEloModule.Settings.ScanAssist;

            foreach (string line in WrapBuffer) {
                if (scanAssist)
                    DrawHighlights(line, cx, y, h, scale);
                ActiveFont.Draw(line, new Vector2(cx, y), new Vector2(0.5f, 0.5f),
                    Vector2.One * scale, color);
                y += h;
            }
        }

        // chips go behind the text, which is drawn right after
        private static void DrawHighlights(string line, float cx, float y, float lineHeight, float scale) {
            float x = cx - ActiveFont.Measure(line).X * scale / 2f;

            foreach (string word in line.Split(' ')) {
                if (HighlightWords.TryGetValue(word, out Color chip))
                    Draw.Rect(x, y - lineHeight / 2f, ActiveFont.Measure(word).X * scale, lineHeight, chip);
                x += ActiveFont.Measure(word + " ").X * scale;
            }
        }
    }
}
