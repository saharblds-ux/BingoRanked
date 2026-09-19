using System;
using System.Collections.Generic;
using Celeste.Mod.BingoElo.Net;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.Utils;

namespace Celeste.Mod.BingoElo.UI {

    // Adds a "Bingo Ranked" button to the title screen, just above Quit.
    public static class MainMenuEntry {

        public static void Load() {
            On.Celeste.OuiMainMenu.CreateButtons += OnCreateButtons;
        }

        public static void Unload() {
            On.Celeste.OuiMainMenu.CreateButtons -= OnCreateButtons;
        }

        private static void OnCreateButtons(On.Celeste.OuiMainMenu.orig_CreateButtons orig, OuiMainMenu self) {
            orig(self);

            try {
                List<MenuButton> buttons = new DynamicData(self).Get<List<MenuButton>>("buttons");
                if (buttons == null || buttons.Count < 3)
                    return;

                int insertAt = buttons.Count - 1;
                MenuButton displaced = buttons[insertAt];
                Vector2 spacing = displaced.TargetPosition - buttons[insertAt - 1].TargetPosition;

                MainMenuSmallButton button = new MainMenuSmallButton(
                    "BINGOELO_MAINMENU_QUEUE", "menu/options", self,
                    displaced.TargetPosition, displaced.TweenFrom,
                    OnPressed);

                buttons.Insert(insertAt, button);
                self.Scene.Add(button);

                for (int i = insertAt + 1; i < buttons.Count; i++) {
                    buttons[i].TargetPosition += spacing;
                    buttons[i].TweenFrom += spacing;
                }

                // only relink around the new button, the climb button is fragile
                button.UpButton = buttons[insertAt - 1];
                button.DownButton = buttons[insertAt + 1];
                buttons[insertAt - 1].DownButton = button;
                buttons[insertAt + 1].UpButton = button;
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, nameof(BingoEloModule),
                    $"could not add the main menu entry: {e}");
            }
        }

        private static void OnPressed() {
            Audio.Play(SFX.ui_main_button_select);
            BingoMenuUI.Instance?.OpenFromMainMenu();
        }
    }
}
