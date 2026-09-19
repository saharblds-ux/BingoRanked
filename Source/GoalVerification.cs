using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Celeste.Mod;
using Celeste.Mod.BingoElo.Net;
using Microsoft.Xna.Framework;
using Monocle;
using Newtonsoft.Json.Linq;

namespace Celeste.Mod.BingoElo {

    // Hints whether a goal already reads as done in the local save. Client-side only:
    // claim_tile() doesn't re-check it. Goals without a check_kind are always Unknown.
    public static class GoalVerification {

        public enum Status {
            // no check_kind, or nothing to check against yet
            Unknown,
            NotYet,
            Ready
        }

        // what "N Hearts" style totals count over
        private const int FirstMainArea = 1;
        private const int LastMainArea = 7;

        // City through Farewell, plus Core. Farewell may not exist on every install;
        // AreaValid() treats an out-of-range area as "not done".
        private static readonly int[] ChapterAreas = { 1, 2, 3, 4, 5, 6, 7, 9, 10 };

        public static Status Check(Goal goal) {
            if (goal == null || string.IsNullOrEmpty(goal.CheckKind) || goal.CheckParams == null)
                return Status.Unknown;
            if (SaveData.Instance == null)
                return Status.Unknown;

            try {
                bool? ready = goal.CheckKind switch {
                    "cassette" => Cassette(Area(goal)),
                    "cassettes_count" => CassetteCount() >= Count(goal),
                    "hearts_and_cassettes" => HeartCount(0) >= Count(goal, "hearts")
                        && CassetteCount() >= Count(goal, "cassettes"),
                    "heart" => Heart(Area(goal), 0),
                    "heart_both" => Heart(Area(goal), 0) && Heart(Area(goal), 1),
                    "hearts_count" => HeartCount(0) >= Count(goal),
                    "hearts_both_count" => HeartBothCount() >= Count(goal),
                    "side_complete" => Completed(Area(goal), Side(goal)),
                    "sides_complete_count" => SidesCompleteCount(goal),
                    "berries_area" => Berries(Area(goal), Side(goal)) >= Count(goal),
                    "berries_total" => SaveData.Instance.TotalStrawberries >= Count(goal),
                    // regular berries only, not golden or moon
                    "all_collectibles" => Berries(Area(goal), Side(goal)) >= Count(goal, "berries")
                        && Cassette(Area(goal))
                        && Heart(Area(goal), Side(goal)),
                    // N berries in each of M chapters, A-side only
                    "berries_in_chapters" => ChaptersWithBerries(Count(goal, "berries")) >= Count(goal, "chapters"),
                    // Binoculars and keys come from BingoUI's save via reflection. They
                    // return null when it isn't there, so the goal stays Unknown.
                    "binoculars_total" => BingoUiBinoculars.Total() is int bt ? bt >= Count(goal) : (bool?) null,
                    "binoculars_area" => BingoUiBinoculars.InArea(Area(goal), Side(goal)) is int ba ? ba >= Count(goal) : (bool?) null,
                    "binoculars_bsides" => BingoUiBinoculars.BSidesTotal() is int bb ? bb >= Count(goal) : (bool?) null,
                    "binoculars_in_chapters" => BingoUiBinoculars.ChaptersAtLeast(ChapterAreas, Count(goal, "binoculars")) is int bc ? bc >= Count(goal, "chapters") : (bool?) null,
                    // no room field to count against, so match by position
                    "binoculars_checkpoint" => BinocularsCheckpointReady(goal),
                    // ordinary strawberries whose ids are in a fixed list
                    "winged_berries_total" => SpecialBerryCount(WingedBerryIds) >= Count(goal),
                    "seed_berries_total" => SpecialBerryCount(SeedBerryIds) >= Count(goal),
                    "winged_berries_in_chapters" => ChaptersWithSpecialBerries(WingedBerryIds, Count(goal, "berries")) >= Count(goal, "chapters"),
                    // one specific strawberry by EntityID
                    "berry_id" => BerryCollected(Area(goal), Side(goal), (string) goal.CheckParams["id"]),
                    // every berry in one checkpoint, from an exact id list
                    "berries_checkpoint" => BerriesCheckpointReady(goal),
                    "keys_total" => BingoUiKeys.Total() is int kt ? kt >= Count(goal) : (bool?) null,
                    "keys_area" => BingoUiKeys.InArea(Area(goal), Side(goal)) is int ka ? ka >= Count(goal) : (bool?) null,
                    "keys_checkpoint" => BingoUiKeys.CountMatching(Area(goal), Side(goal), CheckpointIds(goal)) is int kc ? kc >= Count(goal) : (bool?) null,
                    "keys_in_chapters" => BingoUiKeys.ChaptersAtLeast(ChapterAreas, Count(goal, "keys")) is int kh ? kh >= Count(goal, "chapters") : (bool?) null,
                    // Theo talks and memo reads only live in the current Session, so this
                    // can only say Ready while the player is inside that chapter.
                    "theo_talks" => LiveSession(Area(goal))?.GetCounter("theo") is int tc ? tc >= Count(goal) : (bool?) null,
                    "cutscene_flag" => LiveSession(Area(goal))?.GetFlag((string) goal.CheckParams["flag"]),
                    // SummitGems is a plain saved bool[8]; each pair is the two gems in one 500m stretch
                    "summit_gems_pair" => SummitGemsReady(goal),
                    "summit_gems_count" => SummitGemCount() >= Count(goal),
                    // The next three groups are watched live by MatchState, since the game
                    // doesn't keep them.
                    "one_up_area" => MatchState.HasOneUpIn(Area(goal)),
                    "one_up_chapters" => MatchState.OneUpAreaCount >= Count(goal, "chapters"),
                    "one_up_count" => MatchState.TotalOneUps >= Count(goal),
                    "pico8_fruit" => MatchState.PicoFruitCount >= Count(goal),
                    "pico8_old_site" => MatchState.PicoAtOldSite,
                    "pico8_orb" => MatchState.PicoHasOrb,
                    "pico8_complete" => MatchState.PicoCompleted,
                    // stuns come from BingoUI, kills are watched live
                    "seekers_hit_count" => BingoUiSeekers.Hit() is int sh ? sh >= Count(goal) : (bool?) null,
                    "seeker_kills" => MatchState.SeekerKillCount >= Count(goal),
                    "dashless_segment" => MatchState.SegmentClear(goal, dashless: true),
                    "grabless_segment" => MatchState.SegmentClear(goal, dashless: false),
                    _ => (bool?) null
                };
                return ready == null ? Status.Unknown : ready.Value ? Status.Ready : Status.NotYet;
            } catch {
                // save mid-load, bad area, unexpected shape
                return Status.Unknown;
            }
        }

        private static bool SidesCompleteCount(Goal goal) {
            bool ok = true;
            if (goal.CheckParams["a"] != null)
                ok &= SidesCompleteCount(0) >= (int) goal.CheckParams["a"];
            if (goal.CheckParams["b"] != null)
                ok &= SidesCompleteCount(1) >= (int) goal.CheckParams["b"];
            return ok;
        }

        private static int Area(Goal goal) => (int) (goal.CheckParams["area"] ?? -1);
        private static int Side(Goal goal) => (int) (goal.CheckParams["side"] ?? 0);
        private static int Count(Goal goal, string key = "count") => (int) (goal.CheckParams[key] ?? 0);

        private static bool AreaValid(int area)
            => SaveData.Instance.Areas != null && area >= 0 && area < SaveData.Instance.Areas.Count;

        private static bool ModeValid(int area, int side)
            => AreaValid(area) && SaveData.Instance.Areas[area].Modes != null
                && side >= 0 && side < SaveData.Instance.Areas[area].Modes.Length;

        // the current Session, only if the player is inside that area right now
        private static Session LiveSession(int area) {
            if (!(Engine.Scene is Level level)) return null;
            return level.Session.Area.ID == area ? level.Session : null;
        }

        private static int SummitGemCount()
            => SaveData.Instance.SummitGems?.Count(g => g) ?? 0;

        private static bool SummitGemsReady(Goal goal) {
            bool[] gems = SaveData.Instance.SummitGems;
            if (gems == null) return false;
            foreach (JToken t in goal.CheckParams["indices"]) {
                int i = (int) t;
                if (i < 0 || i >= gems.Length || !gems[i]) return false;
            }
            return true;
        }

        private static bool Cassette(int area) => AreaValid(area) && SaveData.Instance.Areas[area].Cassette;
        private static bool Heart(int area, int side) => ModeValid(area, side) && SaveData.Instance.Areas[area].Modes[side].HeartGem;
        private static bool Completed(int area, int side) => ModeValid(area, side) && SaveData.Instance.Areas[area].Modes[side].Completed;
        private static int Berries(int area, int side) => ModeValid(area, side) ? SaveData.Instance.Areas[area].Modes[side].Strawberries.Count : 0;

        private static int CassetteCount() {
            int n = 0;
            for (int area = FirstMainArea; area <= LastMainArea; area++)
                if (Cassette(area)) n++;
            return n;
        }

        private static int HeartCount(int side) {
            int n = 0;
            for (int area = FirstMainArea; area <= LastMainArea; area++)
                if (Heart(area, side)) n++;
            return n;
        }

        private static int HeartBothCount() {
            int n = 0;
            for (int area = FirstMainArea; area <= LastMainArea; area++)
                if (Heart(area, 0) && Heart(area, 1)) n++;
            return n;
        }

        private static int SidesCompleteCount(int side) {
            int n = 0;
            for (int area = FirstMainArea; area <= LastMainArea; area++)
                if (Completed(area, side)) n++;
            return n;
        }

        private static int ChaptersWithBerries(int threshold) {
            int n = 0;
            foreach (int area in ChapterAreas)
                if (Berries(area, 0) >= threshold) n++;
            return n;
        }

        // Ids are EntityID.ToString() ("room:id"), taken from BingoUI's curated lists.
        // "end:4" is Forsaken City's dashless golden, placed as a memorialTextController
        // entity, so a scan for strawberry entities won't find it.
        private static readonly HashSet<string> WingedBerryIds = new HashSet<string> {
            "9c:2", "3b:2", "end_3c:13", "06-a:7", "13-b:31", "c-01:26",
            "b-21:99", "b-04:67", "d-10b:682", "e-09:398", "end:4"
        };
        private static readonly HashSet<string> SeedBerryIds = new HashSet<string> {
            "d1:67", "a-10:13", "b-17:10", "e-12:504"
        };

        private static int SpecialBerryCount(HashSet<string> ids) {
            if (SaveData.Instance.Areas == null) return 0;
            int n = 0;
            foreach (AreaStats area in SaveData.Instance.Areas) {
                if (area.Modes == null || area.Modes.Length == 0) continue;
                foreach (EntityID id in area.Modes[0].Strawberries)
                    if (ids.Contains(id.ToString())) n++;
            }
            return n;
        }

        private static int SpecialBerryCountInArea(int area, HashSet<string> ids) {
            if (!ModeValid(area, 0)) return 0;
            int n = 0;
            foreach (EntityID id in SaveData.Instance.Areas[area].Modes[0].Strawberries)
                if (ids.Contains(id.ToString())) n++;
            return n;
        }

        private static bool BerryCollected(int area, int side, string id) {
            if (!ModeValid(area, side) || id == null) return false;
            foreach (EntityID eid in SaveData.Instance.Areas[area].Modes[side].Strawberries)
                if (eid.ToString() == id) return true;
            return false;
        }

        private static bool BerriesCheckpointReady(Goal goal) {
            int area = Area(goal), side = Side(goal);
            if (!ModeValid(area, side)) return false;
            foreach (JToken t in goal.CheckParams["ids"])
                if (!BerryCollected(area, side, (string) t)) return false;
            return true;
        }

        private static HashSet<string> CheckpointIds(Goal goal) {
            var ids = new HashSet<string>();
            foreach (JToken t in goal.CheckParams["ids"])
                ids.Add((string) t);
            return ids;
        }

        private static int ChaptersWithSpecialBerries(HashSet<string> ids, int threshold) {
            int n = 0;
            foreach (int area in ChapterAreas)
                if (SpecialBerryCountInArea(area, ids) >= threshold) n++;
            return n;
        }

        // lookouts in a room cluster are over 500px apart
        private const float CheckpointPosTolerance = 64f;

        private static bool? BinocularsCheckpointReady(Goal goal) {
            List<Vector2> used = BingoUiBinoculars.PositionsInArea(Area(goal), Side(goal));
            if (used == null) return null;

            foreach (JToken t in goal.CheckParams["positions"]) {
                var target = new Vector2((float) t[0], (float) t[1]);
                bool found = used.Any(p => Vector2.DistanceSquared(p, target) <= CheckpointPosTolerance * CheckpointPosTolerance);
                if (!found) return false;
            }
            return true;
        }

        // BingoUI's BinocularsList, read by reflection (vanilla doesn't track this).
        // Every entry point returns null if the field isn't there. BingoUI already
        // dedupes to one entry per lookout.
        private static class BingoUiBinoculars {
            private static bool resolved;
            private static PropertyInfo saveDataProp;
            private static FieldInfo listField;
            private static FieldInfo areaIdField;
            private static FieldInfo areaModeField;
            private static FieldInfo posField;

            private static void Resolve() {
                if (resolved) return;
                resolved = true;
                try {
                    EverestModule bingoUi = Everest.Modules.FirstOrDefault(m => m.Metadata?.Name == "BingoUI");
                    if (bingoUi == null) return;

                    Type moduleType = bingoUi.GetType();
                    saveDataProp = moduleType.GetProperty("SaveData", BindingFlags.Public | BindingFlags.Static);
                    object saveData = saveDataProp?.GetValue(null);
                    if (saveData == null) { saveDataProp = null; return; }

                    listField = saveData.GetType().GetField("BinocularsList", BindingFlags.Public | BindingFlags.Instance);
                    Type itemType = listField?.FieldType.IsGenericType == true
                        ? listField.FieldType.GetGenericArguments().FirstOrDefault()
                        : null;
                    if (itemType == null) { listField = null; return; }

                    areaIdField = itemType.GetField("areaID", BindingFlags.Public | BindingFlags.Instance);
                    areaModeField = itemType.GetField("areaMode", BindingFlags.Public | BindingFlags.Instance);
                    posField = itemType.GetField("pos", BindingFlags.Public | BindingFlags.Instance);
                    if (areaIdField == null || areaModeField == null || posField == null)
                        listField = null;
                } catch {
                    saveDataProp = null;
                    listField = null;
                }
            }

            private static List<(int area, int mode, Vector2 pos)> Entries() {
                Resolve();
                if (saveDataProp == null || listField == null)
                    return null;
                try {
                    object saveData = saveDataProp.GetValue(null);
                    var list = (System.Collections.IEnumerable) listField.GetValue(saveData);
                    var result = new List<(int, int, Vector2)>();
                    if (list != null)
                        foreach (object item in list)
                            result.Add((
                                (int) areaIdField.GetValue(item),
                                (int) areaModeField.GetValue(item),
                                (Vector2) posField.GetValue(item)));
                    return result;
                } catch {
                    return null;
                }
            }

            public static int? Total() => Entries()?.Count;

            public static int? InArea(int area, int mode) {
                var entries = Entries();
                return entries?.Count(e => e.area == area && e.mode == mode);
            }

            public static int? BSidesTotal() {
                var entries = Entries();
                return entries?.Count(e => e.mode == 1);
            }

            public static int? ChaptersAtLeast(int[] chapterAreas, int threshold) {
                var entries = Entries();
                if (entries == null) return null;
                int n = 0;
                foreach (int area in chapterAreas)
                    if (entries.Count(e => e.area == area && e.mode == 0) >= threshold) n++;
                return n;
            }

            public static List<Vector2> PositionsInArea(int area, int mode) {
                var entries = Entries();
                return entries?.Where(e => e.area == area && e.mode == mode).Select(e => e.pos).ToList();
            }
        }

        // BingoUI's KeysList, same approach as above
        private static class BingoUiKeys {
            private static bool resolved;
            private static PropertyInfo saveDataProp;
            private static FieldInfo listField;
            private static FieldInfo areaIdField;
            private static FieldInfo areaModeField;
            private static FieldInfo entityField;

            private static void Resolve() {
                if (resolved) return;
                resolved = true;
                try {
                    EverestModule bingoUi = Everest.Modules.FirstOrDefault(m => m.Metadata?.Name == "BingoUI");
                    if (bingoUi == null) return;

                    Type moduleType = bingoUi.GetType();
                    saveDataProp = moduleType.GetProperty("SaveData", BindingFlags.Public | BindingFlags.Static);
                    object saveData = saveDataProp?.GetValue(null);
                    if (saveData == null) { saveDataProp = null; return; }

                    listField = saveData.GetType().GetField("KeysList", BindingFlags.Public | BindingFlags.Instance);
                    Type itemType = listField?.FieldType.IsGenericType == true
                        ? listField.FieldType.GetGenericArguments().FirstOrDefault()
                        : null;
                    if (itemType == null) { listField = null; return; }

                    areaIdField = itemType.GetField("areaID", BindingFlags.Public | BindingFlags.Instance);
                    areaModeField = itemType.GetField("areaMode", BindingFlags.Public | BindingFlags.Instance);
                    entityField = itemType.GetField("entity", BindingFlags.Public | BindingFlags.Instance);
                    if (areaIdField == null || areaModeField == null || entityField == null)
                        listField = null;
                } catch {
                    saveDataProp = null;
                    listField = null;
                }
            }

            private static List<(int area, int mode, string id)> Entries() {
                Resolve();
                if (saveDataProp == null || listField == null)
                    return null;
                try {
                    object saveData = saveDataProp.GetValue(null);
                    var list = (System.Collections.IEnumerable) listField.GetValue(saveData);
                    var result = new List<(int, int, string)>();
                    if (list != null)
                        foreach (object item in list)
                            result.Add((
                                (int) areaIdField.GetValue(item),
                                (int) areaModeField.GetValue(item),
                                ((EntityID) entityField.GetValue(item)).ToString()));
                    return result;
                } catch {
                    return null;
                }
            }

            public static int? Total() => Entries()?.Count;

            public static int? InArea(int area, int mode) {
                var entries = Entries();
                return entries?.Count(e => e.area == area && e.mode == mode);
            }

            public static int? CountMatching(int area, int mode, HashSet<string> ids) {
                var entries = Entries();
                return entries?.Count(e => e.area == area && e.mode == mode && ids.Contains(e.id));
            }

            public static int? ChaptersAtLeast(int[] chapterAreas, int threshold) {
                var entries = Entries();
                if (entries == null) return null;
                int n = 0;
                foreach (int area in chapterAreas)
                    if (entries.Count(e => e.area == area && e.mode == 0) >= threshold) n++;
                return n;
            }
        }

        // BingoUI's SeekersHit, a plain int
        private static class BingoUiSeekers {
            private static bool resolved;
            private static PropertyInfo saveDataProp;
            private static FieldInfo hitField;

            private static void Resolve() {
                if (resolved) return;
                resolved = true;
                try {
                    EverestModule bingoUi = Everest.Modules.FirstOrDefault(m => m.Metadata?.Name == "BingoUI");
                    if (bingoUi == null) return;

                    Type moduleType = bingoUi.GetType();
                    saveDataProp = moduleType.GetProperty("SaveData", BindingFlags.Public | BindingFlags.Static);
                    object saveData = saveDataProp?.GetValue(null);
                    if (saveData == null) { saveDataProp = null; return; }

                    hitField = saveData.GetType().GetField("SeekersHit", BindingFlags.Public | BindingFlags.Instance);
                    if (hitField == null) saveDataProp = null;
                } catch {
                    saveDataProp = null;
                    hitField = null;
                }
            }

            public static int? Hit() {
                Resolve();
                if (saveDataProp == null || hitField == null) return null;
                try {
                    return (int) hitField.GetValue(saveDataProp.GetValue(null));
                } catch {
                    return null;
                }
            }
        }
    }
}
