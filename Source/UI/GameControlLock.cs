using Celeste.Mod.BingoElo;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.BingoElo.UI {

    // Refcounted input lock shared by the overlays. Tick() must run every frame,
    // because the game undoes a one-shot park.
    public static class GameControlLock {

        private static int holders;
        private static int? parkedPlayerState;
        private static Oui forcedOui;

        public static bool Held => holders > 0;

        public static void Acquire(Scene scene) {
            holders++;
            if (holders == 1)
                Tick(scene);
        }

        public static void Release(Scene scene) {
            holders--;
            if (holders > 0)
                return;

            holders = 0;
            Restore();
        }

        public static void Tick(Scene scene) {
            if (holders <= 0 || scene == null)
                return;

            if (scene is Level level) {
                // can only park from a normal state, so keep retrying
                if (parkedPlayerState == null) {
                    Player player = level.Tracker.GetEntity<Player>();
                    if (player != null && player.StateMachine.State == Player.StNormal) {
                        parkedPlayerState = player.StateMachine.State;
                        player.StateMachine.State = Player.StDummy;
                        player.Speed = Vector2.Zero;
                    }
                }
                return;
            }

            if (scene is Overworld overworld && overworld.Current != null) {
                forcedOui = overworld.Current;
                forcedOui.Focused = false;
            }
        }

        public static void SuppressGameplayInput() {
            Input.MoveX.Value = 0;
            Input.MoveY.Value = 0;
            Input.GliderMoveY.Value = 0;

            Input.Jump.ConsumePress();
            Input.Dash.ConsumePress();
            Input.CrouchDash.ConsumePress();
            Input.Grab.ConsumePress();
            Input.Talk.ConsumePress();
            Input.QuickRestart.ConsumePress();
            Input.Pause.ConsumePress();
            Input.ESC.ConsumePress();
        }

        // a leaked count would keep the next scene frozen
        public static void ForgetForNewScene() {
            holders = 0;
            parkedPlayerState = null;
            forcedOui = null;
        }

        private static void Restore() {
            if (parkedPlayerState != null) {
                if (Engine.Scene is Level level) {
                    Player player = level.Tracker.GetEntity<Player>();
                    if (player != null && player.StateMachine.State == Player.StDummy)
                        player.StateMachine.State = parkedPlayerState.Value;
                }
                parkedPlayerState = null;
            }

            if (forcedOui != null) {
                forcedOui.Focused = true;
                forcedOui = null;
            }
        }
    }
}
