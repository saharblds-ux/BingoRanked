using Celeste.Mod;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.BingoElo {
    public class BingoEloSettings : EverestModuleSettings {

        [SettingMaxLength(20)]
        public string DisplayName { get; set; } = "";

        public bool ConnectOnStartup { get; set; } = true;

        [SettingSubHeader("BINGOELO_SETTINGS_GAMEPLAY")]
        [SettingSubText("BINGOELO_SETTINGS_AUTOREVEAL_SUB")]
        public bool AutoRevealOnCountdown { get; set; } = false;

        [SettingSubText("BINGOELO_SETTINGS_AUTOSTART_SUB")]
        public bool AutoStartOnGo { get; set; } = false;

        [SettingSubHeader("BINGOELO_SETTINGS_VISUALS")]
        [SettingRange(60, 200)]
        public int ChatTextSize { get; set; } = 100;

        [SettingRange(30, 100)]
        public int ViewBoardSize { get; set; } = 60;

        [SettingRange(20, 100)]
        public int BoardBackgroundOpacity { get; set; } = 75;

        [SettingRange(20, 100)]
        public int TileOpacity { get; set; } = 100;

        public bool ShowStatusIndicator { get; set; } = true;

        [SettingSubText("BINGOELO_SETTINGS_SCANASSIST_SUB")]
        public bool ScanAssist { get; set; } = false;

        [SettingSubHeader("BINGOELO_SETTINGS_CONTROLS")]
        [DefaultButtonBinding(0, Keys.M)]
        public ButtonBinding OpenMenu { get; set; }

        [DefaultButtonBinding(0, Keys.B)]
        public ButtonBinding ViewBoard { get; set; }

        [DefaultButtonBinding(0, Keys.N)]
        public ButtonBinding InteractWithBoard { get; set; }

        [DefaultButtonBinding(0, Keys.T)]
        public ButtonBinding OpenChat { get; set; }
    }
}
