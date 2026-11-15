using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nax.Completion;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

static class Tests {
    static int fails = 0;
    static void Check(string name, bool ok, string detail) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + "  " + detail); if (!ok) fails++; }
    static double Deg(double r) => r * 180 / Math.PI;
    static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    // Real 41x41 crops of the game, centred on the projected point, from dev\testdata.
    static int[] Crop(string name) {
        using (var bmp = new System.Drawing.Bitmap(Path.Combine("dev", "testdata", "hc", name + ".png"))) {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) px[y * bmp.Width + x] = bmp.GetPixel(x, y).ToArgb();
            return px;
        }
    }
    static bool HcDone(string name, IconSet set) => IconRecognizer.IsFilled(Crop(name), 41, 41, 20, 20, PointKind.HeroChallenge, IconRecognizer.CALIBRATION_UI_SCALE, set);
    static void Diamond(int[] px, int cx, int cy, int r, int c) { int w = (int)Math.Sqrt(px.Length); for (int y = cy - r; y <= cy + r; y++) for (int x = cx - r; x <= cx + r; x++) if (Math.Abs(x - cx) + Math.Abs(y - cy) <= r) px[y * w + x] = c; }
    static void Fill(int[] px, int x0, int y0, int x1, int y1, int c) { int w = (int)Math.Sqrt(px.Length); for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) px[y * w + x] = c; }
    static void Rot(string name, double e, double n, double fe, double fn, double expectDeg) {
        double r = Deg(NavMath.ArrowRotation(e, n, fe, fn));
        bool ok = Math.Abs(Math.Abs(expectDeg) == 180 ? Math.Abs(r) - 180 : r - expectDeg) < 0.01;
        Check(name, ok, $"got {r:0.##} deg, want {expectDeg}");
    }

    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object target, string name) => target.GetType().GetField(name, PrivateInstance).GetValue(target);
    static void SetField(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance).Invoke(target, args);
    static void AllowMapRetry(MapRecognizer reader) {
        // Advance just the scheduler, without sleeping or capturing the desktop.
        typeof(MapRecognizer).GetField("_nextReadUtc", PrivateInstance)?.SetValue(reader, DateTime.MinValue);
    }
    static MapRecognizer SettledMapReader() {
        var reader = new MapRecognizer();
        reader.Update("view", 100, false, () => null, IconRecognizer.CALIBRATION_UI_SCALE);
        SetField(reader, "_viewSinceUtc", DateTime.UtcNow.AddMinutes(-1));
        return reader;
    }
    static void MapResult(MapRecognizer reader, System.Threading.Tasks.Task<List<MapRecognizer.Job>> task) {
        SetField(reader, "_running", task);
        SetField(reader, "_runningTick", 100);
        SetField(reader, "_runningGeneration", Field(reader, "_generation"));
        typeof(MapRecognizer).GetField("_runningTooltipGeneration", PrivateInstance)?.SetValue(reader, Field(reader, "_tooltipGeneration"));
    }
    static void RecognitionLifecycleTests() {
        const double ui = IconRecognizer.CALIBRATION_UI_SCALE;
        int attempts = 0;
        Func<List<MapRecognizer.Job>> jobs = () => { attempts++; return new List<MapRecognizer.Job>(); };
        var reader = SettledMapReader();
        reader.Update("view", 102, true, jobs, ui);
        reader.Update("view", 103, true, jobs, ui);
        Check("stationary_map_reads_are_rate_limited", attempts == 1, $"{attempts} job builds");
        AllowMapRetry(reader);
        reader.Update("view", 104, true, jobs, ui);
        Check("tooltip_cleared_in_same_map_view_is_retried", attempts == 2, $"{attempts} job builds");

        var found = new List<MapRecognizer.Job> { new MapRecognizer.Job { MapId = 32, Key = "heart:1" } };
        MapResult(reader, System.Threading.Tasks.Task.FromResult(found));
        Check("world_map_result_waits_for_two_mumble_ticks", reader.Update("view", 101, true, jobs, ui) == null, "");
        Check("world_map_current_view_result_is_returned", reader.Update("view", 102, true, jobs, ui) == found, "");
        AllowMapRetry(reader);
        reader.Update("view", 105, true, jobs, ui);
        Check("successful_world_map_view_is_refreshed", attempts == 3, $"{attempts} job builds");

        foreach (bool canceled in new[] { false, true }) {
            reader = SettledMapReader(); attempts = 0;
            reader.Update("view", 102, true, jobs, ui);
            var task = canceled
                ? System.Threading.Tasks.Task.FromCanceled<List<MapRecognizer.Job>>(new System.Threading.CancellationToken(true))
                : System.Threading.Tasks.Task.FromException<List<MapRecognizer.Job>>(new InvalidOperationException("capture failed"));
            MapResult(reader, task);
            reader.Update("view", 103, true, jobs, ui);
            reader.Update("view", 104, true, jobs, ui);
            Check(canceled ? "canceled_world_map_capture_backs_off" : "failed_world_map_capture_backs_off", attempts == 1, "");
            AllowMapRetry(reader);
            reader.Update("view", 105, true, jobs, ui);
            Check(canceled ? "canceled_world_map_capture_retries_same_view" : "failed_world_map_capture_retries_same_view", attempts == 2, $"{attempts} job builds");
        }
        reader = SettledMapReader();
        MapResult(reader, System.Threading.Tasks.Task.FromResult(found));
        reader.Update("other view", 101, false, jobs, ui);
        Check("world_map_result_from_moved_view_is_discarded", Field(reader, "_running") == null, "");
        reader.Reset(); attempts = 0;
        reader.Update("view", 110, true, jobs, ui);
        Check("world_map_reset_requires_view_to_settle", attempts == 0, "");

        var module = ModuleWithRoute();
        var mini = (MinimapRecognizer)Field(module, "_minimap");
        var pending = new System.Threading.Tasks.TaskCompletionSource<List<string>>();
        SetField(mini, "_running", pending.Task);
        SetField(mini, "_runningMap", 32);
        SetField(mini, "_runningTick", 100);
        SetField(mini, "_runningGeneration", Field(mini, "_generation"));
        Invoke(module, "LoadCharacter", "Second Character");
        pending.SetResult(new List<string> { "heart:previous-character" });
        var result = mini.Update(32, 103, false, () => null, ui);
        Check("character_switch_discards_pending_compass_result", result == null, "");
        SetField(mini, "_running", System.Threading.Tasks.Task.FromResult(new List<string> { "heart:previous-character" }));
        SetField(mini, "_runningGeneration", Field(mini, "_generation"));
        Invoke(module, "LoadCharacter", "Third Character");
        Invoke(module, "UpdateMinimap");
        Check("character_switch_cannot_persist_previous_characters_compass_progress", !((ProgressStore)Field(module, "_store")).IsDone(32, "heart:previous-character"), "");
        SetField(mini, "_running", System.Threading.Tasks.Task.FromResult(new List<string> { "heart:current-character" }));
        SetField(mini, "_runningGeneration", Field(mini, "_generation"));
        Check("current_character_compass_result_is_still_returned", mini.Update(32, 103, false, () => null, ui)?.Contains("heart:current-character") == true, "");
        SetField(mini, "_running", System.Threading.Tasks.Task.FromResult(new List<string> { "heart:old-map-visit" }));
        SetField(mini, "_runningGeneration", Field(mini, "_generation"));
        Invoke(module, "OnMapChanged", null, new Blish_HUD.ValueEventArgs<int>(0));
        Check("map_change_discards_compass_result_even_when_returning_to_same_map", mini.Update(32, 103, false, () => null, ui) == null, "");
    }
    static NaxCompletionModule ModuleWithRoute(bool showPanel = true, bool trackPuzzles = true, string key = "jp:regression") {
        var module = new NaxCompletionModule(null);
        Invoke(module, "DefineSettings", new Blish_HUD.Settings.SettingCollection());
        ((Blish_HUD.Settings.SettingEntry<bool>)Field(module, "_showPanel")).Value = showPanel;
        ((Blish_HUD.Settings.SettingEntry<bool>)Field(module, "_trackPuzzles")).Value = trackPuzzles;
        string directory = Path.Combine(Path.GetTempPath(), "nax-lifecycle-" + Guid.NewGuid().ToString("N"));
        var store = new ProgressStore(directory); store.LoadCharacter("First Character");
        var routes = new RouteStore(Path.Combine(directory, "routes"));
        routes.Save(new PuzzleRoute { Key = key, Puzzle = "Regression puzzle", MapId = 32,
            Steps = new List<RouteStep> { new RouteStep(), new RouteStep { X = 20 }, new RouteStep { X = 40 } } });
        SetField(module, "_store", store); SetField(module, "_routes", routes);
        SetField(module, "_heroPoints", new HeroPointSync(null)); SetField(module, "_achievements", new AchievementSync(null));
        SetField(module, "_panel", new ArrowPanel()); SetField(module, "_routePath", new Nax.Completion.World.RoutePathEntity());
        SetField(module, "_mapData", new MapCompletionData(32, "Test map", 1, new List<CompletionPoint> {
            new CompletionPoint(key, key.StartsWith("vista:") ? PointKind.Vista : PointKind.Puzzle, "Test", 1000, 1000) }));
        Blish_HUD.GameService.Gw2Mumble.IsAvailable = true;
        Blish_HUD.GameService.Gw2Mumble.CurrentMap.Id = 32;
        Blish_HUD.GameService.Gw2Mumble.Tick = 103;
        Blish_HUD.GameService.Gw2Mumble.UI.IsMapOpen = false;
        Blish_HUD.GameService.GameIntegration.Gw2Instance.IsInGame = true;
        return module;
    }
    static void FollowTestRoute(NaxCompletionModule module) {
        foreach (int x in new[] { 0, 20, 40 }) {
            Blish_HUD.GameService.Gw2Mumble.PlayerCharacter.Position = new Vector3(x, 0, 0);
            Invoke(module, "RunLogic");
        }
    }
    static void RouteLifecycleTests() {
        var module = ModuleWithRoute(trackPuzzles: false);
        FollowTestRoute(module);
        Check("disabled_puzzle_tracking_prevents_guidance", ((PuzzleGuide)Field(module, "_guide")).Route == null, "");
        Check("disabled_puzzle_tracking_prevents_route_tick", !((ProgressStore)Field(module, "_store")).IsDone(32, "jp:regression"), "");
        module = ModuleWithRoute(showPanel: false);
        FollowTestRoute(module);
        Check("hidden_panel_still_completes_followed_puzzle_route", ((ProgressStore)Field(module, "_store")).IsDone(32, "jp:regression"), "");
        Check("hidden_panel_keeps_panel_and_path_hidden", !((ArrowPanel)Field(module, "_panel")).Visible && RouteVertices((IRoutePath)Field(module, "_routePath")).Length == 0, "");
        module = ModuleWithRoute();
        FollowTestRoute(module);
        var store = (ProgressStore)Field(module, "_store");
        Check("visible_panel_completes_followed_puzzle_route", store.IsDone(32, "jp:regression"), "");
        var history = (UndoHistory)Field(module, "_undo");
        Check("route_completion_adds_one_undo_entry", history.Count == 1, "");
        store.SetDone(32, "jp:regression", false);
        ((HashSet<string>)Field(module, "_suppressAuto")).Add("jp:regression");
        FollowTestRoute(module);
        Check("undone_route_completion_is_not_reapplied", !store.IsDone(32, "jp:regression") && history.Count == 1, "");
        module = ModuleWithRoute(showPanel: false);
        store = (ProgressStore)Field(module, "_store"); store.KeepOpen(new[] { "jp:regression" });
        FollowTestRoute(module);
        Check("route_completion_respects_keep_open_with_panel_hidden", !store.IsDone(32, "jp:regression"), "");
        module = ModuleWithRoute(); SetField(module, "_mumbleFrozen", true);
        FollowTestRoute(module);
        Check("frozen_mumble_cannot_advance_or_complete_route", ((PuzzleGuide)Field(module, "_guide")).Route == null && !((ProgressStore)Field(module, "_store")).IsDone(32, "jp:regression"), "");
        module = ModuleWithRoute(); Blish_HUD.GameService.Gw2Mumble.UI.IsMapOpen = true;
        FollowTestRoute(module);
        Check("open_world_map_cannot_advance_or_complete_route", ((PuzzleGuide)Field(module, "_guide")).Route == null && !((ProgressStore)Field(module, "_store")).IsDone(32, "jp:regression"), "");
        module = ModuleWithRoute(key: "vista:regression"); FollowTestRoute(module);
        Check("following_vista_route_never_ticks_unviewed_vista", !((ProgressStore)Field(module, "_store")).IsDone(32, "vista:regression"), "");
        module = ModuleWithRoute();
        Blish_HUD.GameService.Gw2Mumble.PlayerCharacter.Position = new Vector3(); Invoke(module, "RunLogic");
        ((Blish_HUD.Settings.SettingEntry<bool>)Field(module, "_trackPuzzles")).Value = false;
        Invoke(module, "RunLogic");
        Check("disabling_puzzle_tracking_clears_active_guide_and_geometry", ((PuzzleGuide)Field(module, "_guide")).Route == null && RouteVertices((IRoutePath)Field(module, "_routePath")).Length == 0, "");
    }
    static Microsoft.Xna.Framework.Graphics.VertexPositionColor[] RouteVertices(IRoutePath path) =>
        (Microsoft.Xna.Framework.Graphics.VertexPositionColor[])Field(path, "_vertices");
    static void RouteGeometryTests() {
        using (var path = new Nax.Completion.World.RoutePathEntity()) {
            var from = new RouteStep();
            path.Show(new[] { new RouteStep { X = 10 } }, from);
            int ringVertices = 24 * 6;
            Check("connected_route_steps_draw_ring_and_dashes", RouteVertices(path).Length > ringVertices, "");
            path.Show(new[] { new RouteStep { X = 1162 } }, from);
            Check("kilometre_route_gap_draws_ring_without_bridge", RouteVertices(path).Length == ringVertices, "");
            var broken = Newtonsoft.Json.JsonConvert.DeserializeObject<RouteStep>("{\"x\":10,\"breakBefore\":true,\"note\":\"Use portal\"}");
            path.Show(new[] { broken }, from);
            Check("explicit_route_section_break_draws_no_bridge", RouteVertices(path).Length == ringVertices, "");
            var after = new RouteStep { X = 20 };
            path.Show(new[] { broken, after }, from);
            Check("route_dashes_resume_after_section_break", RouteVertices(path).Length > ringVertices * 2 && RouteVertices(path).Where(v => v.Position.X > 1 && v.Position.X < 9).Count() == 0, "");
            path.Hide();
            Check("hiding_route_clears_generated_geometry", RouteVertices(path).Length == 0, "");
            Check("route_section_break_survives_json_round_trip", Newtonsoft.Json.JsonConvert.SerializeObject(broken).Contains("\"breakBefore\":true"), "");
            string directory = Path.Combine(Path.GetTempPath(), "nax-section-" + Guid.NewGuid().ToString("N"));
            var routes = new RouteStore(directory);
            routes.Save(new PuzzleRoute { Key = "jp:section", MapId = 32, Steps = new List<RouteStep> { from, broken, after } });
            var reloaded = new RouteStore(directory); reloaded.LoadAll();
            Check("saved_route_preserves_section_break_and_portal_note", reloaded.Get("jp:section").Steps[1].BreakBefore && reloaded.Get("jp:section").Steps[1].Note == "Use portal", "");
            routes.LoadBuiltIn();
            var hiddenGarden = routes.Get("jp:hidden-garden");
            int largestGap = Enumerable.Range(1, hiddenGarden.Steps.Count - 1)
                .OrderByDescending(i => PuzzleGuide.Distance(hiddenGarden.Steps[i], new Vector3(hiddenGarden.Steps[i - 1].X, hiddenGarden.Steps[i - 1].Y, hiddenGarden.Steps[i - 1].Z)))
                .First();
            path.Show(new[] { hiddenGarden.Steps[largestGap] }, hiddenGarden.Steps[largestGap - 1]);
            Check("shipped_hidden_garden_transition_is_preserved_without_bridge", hiddenGarden.Steps[largestGap].BreakBefore && RouteVertices(path).Length == ringVertices, "");
        }
    }
    static void HeroTooltipTests() {
        const double ui = IconRecognizer.CALIBRATION_UI_SCALE;
        foreach (string name in new[] { "next-earned-tooltip", "rancher-earned-tooltip", "moors-open-tooltip" }) {
            using (var bmp = new System.Drawing.Bitmap(Path.Combine("dev", "testdata", "hc", name + ".png"))) {
                var pixels = new int[bmp.Width * bmp.Height];
                for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) pixels[y * bmp.Width + x] = bmp.GetPixel(x, y).ToArgb();
                bool earned = name != "moors-open-tooltip";
                Check(name.Replace('-', '_') + "_matches_explicit_hero_tooltip_state",
                      HeroTooltipRecognizer.IsEarned(pixels, bmp.Width, bmp.Height, 300, 180, ui) == earned,
                      earned ? "Captured Hero Point Earned title, including the player-covered next stop." : "Captured incomplete Hero Point title.");
                var job = new MapRecognizer.Job { MapId = 32, Key = "hc:test", Kind = PointKind.HeroChallenge,
                                                 ScreenX = 1720, ScreenY = 720, HeroTooltip = true };
                var jobs = new List<MapRecognizer.Job> { job };
                var found = MapRecognizer.ReadPixels(jobs, pixels, bmp.Width, bmp.Height, 1420, 540, ui);
                Check(name.Replace('-', '_') + "_world_map_dispatch_returns_only_earned_hero",
                      earned ? found.Count == 1 && found[0] == job : found.Count == 0,
                      "The production pixel-dispatch path uses physical capture offsets and the job identity.");
                if (name != "next-earned-tooltip") continue;
                var completedModule = ModuleWithRoute();
                SetField(completedModule, "_target", new CompletionPoint("hc:test", PointKind.HeroChallenge, null, 0, 0));
                Invoke(completedModule, "ApplyRecognised", found.ToLookup(j => j.MapId, j => j.Key), "world map");
                var completedStore = (ProgressStore)Field(completedModule, "_store");
                Check("earned_next_stop_tooltip_ticks_its_hero_and_releases_held_target",
                      completedStore.IsDone(32, "hc:test") && Field(completedModule, "_target") == null,
                      "Captured completion flows through the real module persistence and target-release path.");
                Invoke(completedModule, "OnUndo", null, EventArgs.Empty);
                Check("undo_of_earned_tooltip_tick_keeps_the_hero_open",
                      !completedStore.IsDone(32, "hc:test") && completedStore.IsKeptOpen("hc:test"),
                      "Undo reverses this recognised tick and prevents immediate recognition from overriding it.");
                Check("player_covered_world_map_hero_is_not_guessed_from_icon_pixels",
                      !IconRecognizer.IsFilled(pixels, bmp.Width, bmp.Height, 300, 180, PointKind.HeroChallenge, ui),
                      "The completed figure is hidden by the player marker; only explicit tooltip evidence is used.");
                var noEarned = (int[])pixels.Clone();
                for (int y = 100; y <= 119; y++) for (int x = 386; x <= 434; x++) noEarned[y * bmp.Width + x] = Argb(55, 50, 40);
                Check("hero_tooltip_requires_the_earned_suffix",
                      !HeroTooltipRecognizer.IsEarned(noEarned, bmp.Width, bmp.Height, 300, 180, ui),
                      "The completed suffix is removed while the Hero Point prefix remains.");
                var solid = Enumerable.Repeat(Argb(255, 255, 240), pixels.Length).ToArray();
                Check("bright_ground_cannot_substitute_for_hero_tooltip_letters",
                      !HeroTooltipRecognizer.IsEarned(solid, bmp.Width, bmp.Height, 300, 180, ui), "Uniform bright ground has no letter gaps.");
                job.Kind = PointKind.PointOfInterest;
                Check("earned_hero_tooltip_cannot_tick_another_point_kind",
                      MapRecognizer.ReadPixels(jobs, pixels, bmp.Width, bmp.Height, 1420, 540, ui).Count == 0,
                      "The tooltip flag on a POI job is rejected.");
                Check("hero_tooltip_outside_its_point_search_area_is_rejected",
                      !HeroTooltipRecognizer.IsEarned(pixels, bmp.Width, bmp.Height, 30, 180, ui),
                      "An earned title hundreds of pixels away must not belong to this point.");
                Check("hero_tooltip_at_image_edge_returns_safely",
                      !HeroTooltipRecognizer.IsEarned(pixels, bmp.Width, bmp.Height, 0, 0, ui), "No complete title fits above this point.");
                Check("hero_tooltip_invalid_buffer_is_rejected",
                      !HeroTooltipRecognizer.IsEarned(new int[1], bmp.Width, bmp.Height, 300, 180, ui), "Truncated capture data is unreadable.");
                var module = ModuleWithRoute();
                var store = (ProgressStore)Field(module, "_store");
                store.KeepOpen(new[] { "hc:test" });
                Invoke(module, "ApplyRecognised", new[] { "hc:test" }.ToLookup(k => 32), "world map");
                Check("earned_hero_tooltip_preserves_explicit_keep_open",
                      !store.IsDone(32, "hc:test"), "The normal persistence path preserves the owner's undo/keep-open choice.");
            }
        }
        var hero = new MapOverlay.Marker { Point = new CompletionPoint("hc:test", PointKind.HeroChallenge, null, 0, 0),
                                           Position = new Vector2(1000, 800) };
        var mouse = new Point(1004, 805);
        Check("hero_tooltip_is_associated_with_the_single_hovered_hero",
              NaxCompletionModule.HeroTooltipTarget(new[] { hero }, mouse) == hero, "The pointer is inside exactly one hero icon.");
        Check("hero_tooltip_is_not_associated_with_a_distant_pointer",
              NaxCompletionModule.HeroTooltipTarget(new[] { hero }, new Point(1050, 800)) == null, "The pointer is outside the hero hit area.");
        var other = new MapOverlay.Marker { Point = new CompletionPoint("hc:other", PointKind.HeroChallenge, null, 0, 0),
                                            Position = new Vector2(1010, 800), Done = true };
        Check("completed_nearby_hero_blocks_ambiguous_tooltip_attribution",
              NaxCompletionModule.HeroTooltipTarget(new[] { hero, other }, mouse) == null, "Already recorded heroes still draw game tooltips.");
        other.Point = new CompletionPoint("poi:other", PointKind.PointOfInterest, null, 0, 0);
        Check("other_point_kind_blocks_ambiguous_hero_tooltip_attribution",
              NaxCompletionModule.HeroTooltipTarget(new[] { hero, other }, mouse) == null, "A nearby POI might own the tooltip.");
        Check("non_hero_hover_has_no_hero_tooltip_job",
              NaxCompletionModule.HeroTooltipTarget(new[] { other }, mouse) == null, "Only hero challenges have the completed title reader.");
        var reader = SettledMapReader();
        var result = new List<MapRecognizer.Job> { new MapRecognizer.Job { HeroTooltip = true, Kind = PointKind.HeroChallenge } };
        MapResult(reader, System.Threading.Tasks.Task.FromResult(result));
        Check("pointer_change_discards_in_flight_hero_tooltip_evidence",
              reader.Update("view|new pointer", 102, false, () => null, ui) == null && Field(reader, "_running") == null,
              "A tooltip captured before the pointer moved cannot tick the newly hovered point.");
        var changedModule = ModuleWithRoute();
        var changedReader = (MapRecognizer)Field(changedModule, "_recognizer");
        MapResult(changedReader, System.Threading.Tasks.Task.FromResult(result));
        Invoke(changedModule, "OnCharacterChanged", null, new Blish_HUD.ValueEventArgs<string>("Second Character"));
        Check("character_change_discards_in_flight_hero_tooltip_evidence",
              Field(changedReader, "_running") == null, "A tooltip result cannot cross a character change.");
    }
    static void CompassDiscoveryTests() {
        const double ui = IconRecognizer.CALIBRATION_UI_SCALE;
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pixels = Crop("compass_done_moorwatch");
        int reads = 0;
        Func<List<MinimapRecognizer.Job>, double, List<string>> read = (jobs, scale) => {
            System.Threading.Interlocked.Increment(ref reads);
            return MinimapRecognizer.ReadPixels(jobs, pixels, 41, 41, 80, 80, scale);
        };
        var a = new MinimapRecognizer.Job { Key = "hc:a", Kind = PointKind.HeroChallenge, ScreenX = 100, ScreenY = 100 };
        var b = new MinimapRecognizer.Job { Key = "hc:b", Kind = PointKind.HeroChallenge, ScreenX = 100, ScreenY = 100 };
        var visible = new List<MinimapRecognizer.Job>();
        Func<List<MinimapRecognizer.Job>> build = () => visible;
        var reader = new MinimapRecognizer(() => now, read);
        reader.Update(32, 100, true, build, ui);
        now = now.AddMilliseconds(100); visible = new List<MinimapRecognizer.Job> { a };
        reader.Update(32, 101, true, build, ui);
        var pending = (System.Threading.Tasks.Task<List<string>>)Field(reader, "_running");
        pending?.GetAwaiter().GetResult();
        Check("empty_compass_does_not_delay_first_readable_completed_stop", pending != null && reads == 1,
              "A completed compass icon becomes visible 100 ms after an empty view; no map or tooltip is involved.");
        Check("new_compass_discovery_still_waits_for_two_mumble_ticks", reader.Update(32, 102, true, build, ui) == null,
              "The capture result remains pending after only one game tick.");
        var result = reader.Update(32, 103, true, build, ui);
        Check("compass_capture_dispatch_returns_its_completed_hero_key", result?.SequenceEqual(new[] { "hc:a" }) == true,
              "The real compass pixel classifier reads the saved completed icon and returns the associated job key.");
        now = now.AddMilliseconds(100); visible = new List<MinimapRecognizer.Job> { a, b };
        reader.Update(32, 104, true, build, ui);
        Check("compass_new_point_burst_keeps_capture_rate_bounded", Field(reader, "_running") == null && reads == 1,
              "Another point appears only 100 ms after capture; the discovery rate limit prevents an immediate second capture.");
        now = now.AddMilliseconds(200);
        reader.Update(32, 105, true, build, ui);
        pending = (System.Threading.Tasks.Task<List<string>>)Field(reader, "_running");
        pending?.GetAwaiter().GetResult();
        Check("newly_visible_completed_stop_bypasses_previous_two_second_poll", pending != null && reads == 2,
              "A second stop is readable for less than two seconds while travelling; it is captured after the short discovery limit.");
        result = reader.Update(32, 107, true, build, ui);
        Check("short_compass_view_is_recognised_before_player_marker_covers_it", result?.Contains("hc:b") == true,
              "The stop is read during its clear approach window, before the jobs become player-covered.");
        now = now.AddMilliseconds(300);
        reader.Update(32, 108, true, build, ui);
        Check("unchanged_compass_points_keep_the_normal_poll_interval", Field(reader, "_running") == null && reads == 2,
              "The same readable jobs on subsequent frames do not cause continuous capture.");
        visible = new List<MinimapRecognizer.Job> { a }; reader.Update(32, 109, true, build, ui);
        now = now.AddMilliseconds(300); visible = new List<MinimapRecognizer.Job> { a, b };
        reader.Update(32, 110, true, build, ui);
        pending = (System.Threading.Tasks.Task<List<string>>)Field(reader, "_running");
        pending?.GetAwaiter().GetResult();
        Check("uncovered_stop_is_discovered_while_other_compass_jobs_remain_visible", pending != null && reads == 3,
              "One hero becomes covered and clear again while another point stays visible; it still gets an early read.");
        reader.Update(32, 112, true, build, ui);
        visible = new List<MinimapRecognizer.Job>(); reader.Update(32, 113, true, build, ui);
        now = now.AddMilliseconds(300); visible = new List<MinimapRecognizer.Job> { b };
        reader.Update(32, 114, true, build, ui);
        pending = (System.Threading.Tasks.Task<List<string>>)Field(reader, "_running");
        pending?.GetAwaiter().GetResult();
        Check("naturally_uncovered_compass_stop_is_read_without_waiting_for_poll", pending != null && reads == 4,
              "An unreadable/player-covered view clears the captured-key set; ordinary movement exposing the icon triggers a read.");
        reader.Invalidate();
        Check("loading_guard_discards_early_compass_discovery", reader.Update(32, 116, false, build, ui) == null,
              "A loading/freeze invalidation rejects a completed capture rather than applying it.");
        now = now.AddMilliseconds(50);
        reader.Update(32, 117, false, build, ui);
        Check("early_compass_discovery_does_not_capture_without_game_read_permission", Field(reader, "_running") == null && reads == 4,
              "No capture is started when focus, loading or visibility prevents starting one.");
        reader.Update(32, 118, true, build, ui);
        pending = (System.Threading.Tasks.Task<List<string>>)Field(reader, "_running");
        pending?.GetAwaiter().GetResult();
        Check("readable_compass_after_loading_is_not_delayed_by_old_poll", pending != null && reads == 5,
              "After loading clears, the current point gets a fresh read without inheriting the old delay.");
    }
    static void CompassEdgeRegressionTests() {
        const double ui = IconRecognizer.CALIBRATION_UI_SCALE;
        var sample = Crop("compass_done_moorwatch");
        foreach (var edge in new[] { new Point(30, 137), new Point(332, 137), new Point(181, 30), new Point(181, 245) }) {
            const int width = 362, height = 275, left = 3078, top = 1135;
            bool projected = MinimapRecognizer.TryProject(1000 + (edge.X - width / 2.0) * 1.75,
                1000 + (edge.Y - height / 2.0) * 1.75, 1000, 1000, 1000, 1000, 1.75, 0,
                left, top, width, height, ui, out int sx, out int sy);
            var pixels = new int[width * height];
            for (int row = 0; row < 41; row++) Array.Copy(sample, row * 41, pixels, (edge.Y - 20 + row) * width + edge.X - 20, 41);
            var jobs = new List<MinimapRecognizer.Job>();
            if (projected) jobs.Add(new MinimapRecognizer.Job { Key = "hc:edge", Kind = PointKind.HeroChallenge, ScreenX = sx, ScreenY = sy });
            Check("completed_compass_hero_30px_from_edge_" + edge.X + "_" + edge.Y,
                MinimapRecognizer.ReadPixels(jobs, pixels, width, height, left, top, ui).Contains("hc:edge"),
                "A real completed icon fits the compass capture; larger world-map waypoint templates must not exclude it.");
        }
        Check("compass_border_still_excludes_clipped_icons", !MinimapRecognizer.TryProject(1000 + (20 - 181) * 1.75,
            1000, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, ui, out int ignoredX, out int ignoredY),
            "A projected icon whose capture would reach into the border is still skipped.");
    }
    static void MapPointerRegressionTests() {
        var ui = Blish_HUD.GameService.Gw2Mumble.UI;
        var instance = Blish_HUD.GameService.GameIntegration.Gw2Instance;
        double previousScale = ui.MapScale;
        bool previousMapOpen = ui.IsMapOpen, previousFocus = instance.Gw2HasFocus;
        var previousMouse = Blish_HUD.GameService.Input.Mouse.Position;
        try {
            foreach (bool returnPointer in new[] { false, true }) {
                var module = ModuleWithRoute();
                var store = (ProgressStore)Field(module, "_store");
                SetField(module, "_mapData", new MapCompletionData(32, "Test", 1, new List<CompletionPoint> {
                    new CompletionPoint("hc:visible", PointKind.HeroChallenge, null, 0, 0),
                    new CompletionPoint("hc:hovered", PointKind.HeroChallenge, null, 1000, 0) }));
                SetField(module, "_mapOverlay", new MapOverlay { Size = new Point(4000, 1600) });
                ui.MapScale = 1; ui.IsMapOpen = true; instance.Gw2HasFocus = false;
                Blish_HUD.GameService.Input.Mouse.Position = new Point(10, 10);
                Invoke(module, "UpdateMapOverlay");
                var reader = (MapRecognizer)Field(module, "_recognizer");
                var settledSince = DateTime.UtcNow.AddMinutes(-1);
                SetField(reader, "_viewSinceUtc", settledSince);
                SetField(module, "_mapOpenedUtc", settledSince);
                var found = new List<MapRecognizer.Job> {
                    new MapRecognizer.Job { MapId = 32, Key = "hc:visible", Kind = PointKind.HeroChallenge },
                    new MapRecognizer.Job { MapId = 32, Key = "hc:hovered", Kind = PointKind.HeroChallenge, HeroTooltip = true } };
                var pending = new System.Threading.Tasks.TaskCompletionSource<List<MapRecognizer.Job>>();
                MapResult(reader, pending.Task);
                Blish_HUD.GameService.Input.Mouse.Position = new Point(20, 10);
                Invoke(module, "UpdateMapOverlay");
                if (returnPointer) {
                    Blish_HUD.GameService.Input.Mouse.Position = new Point(10, 10);
                    Invoke(module, "UpdateMapOverlay");
                }
                pending.SetResult(found);
                Invoke(module, "UpdateMapOverlay");
                Check(returnPointer ? "pointer_return_does_not_discard_uncovered_world_map_icons" : "pointer_movement_does_not_discard_uncovered_world_map_icons",
                    store.IsDone(32, "hc:visible"), "Regular icon evidence is independent of the pointer's tooltip context.");
                Check(returnPointer ? "pointer_return_cannot_revive_stale_hero_tooltip" : "pointer_movement_discards_only_stale_hero_tooltip",
                    !store.IsDone(32, "hc:hovered"), "A moved-away pointer invalidates title evidence even when it returns to the same pixel.");
                Check(returnPointer ? "pointer_return_preserves_world_map_settle_time" : "pointer_movement_preserves_world_map_settle_time",
                    (DateTime)Field(reader, "_viewSinceUtc") == settledSince, "A settled map does not become unsettled while the cursor moves.");
            }
        } finally {
            ui.MapScale = previousScale; ui.IsMapOpen = previousMapOpen; instance.Gw2HasFocus = previousFocus;
            Blish_HUD.GameService.Input.Mouse.Position = previousMouse;
        }
    }
    static void TooltipContextRegressionTests() {
        const double ui = IconRecognizer.CALIBRATION_UI_SCALE;
        var title = new MapRecognizer.Job { MapId = 32, Key = "hc:tooltip", Kind = PointKind.HeroChallenge, HeroTooltip = true };
        var found = new List<MapRecognizer.Job> { title };
        var reader = SettledMapReader();
        reader.Update("view", 100, false, () => null, ui, "10,10");
        MapResult(reader, System.Threading.Tasks.Task.FromResult(found));
        Check("unchanged_pointer_preserves_completed_hero_tooltip_result",
            reader.Update("view", 102, false, () => null, ui, "10,10")?.SingleOrDefault() == title,
            "Separating tooltip generations preserves valid title evidence at the same pointer position.");
        reader = SettledMapReader();
        reader.Update("view", 101, true, () => found, ui, "10,10");
        Check("unsettled_pointer_cannot_start_hero_title_capture", Field(reader, "_running") == null,
            "A pointer that just moved has no settled title to capture.");
        Check("unsettled_title_does_not_consume_world_map_poll_interval", (DateTime)Field(reader, "_nextReadUtc") == DateTime.MinValue,
            "When only an unsettled title is available, the first readable title can be retried immediately.");
        int builds = 0;
        reader.Update("view", 102, true, () => { builds++; return new List<MapRecognizer.Job>(); }, ui, "20,10");
        Check("moving_pointer_does_not_block_regular_map_job_build", builds == 1,
            "Moving to a new pointer position does not restart the already-settled geometry delay.");
    }
    static bool MapFixtureReturnsKey(string name, List<MapRecognizer.Job> jobs, MapOverlay.Marker marker) {
        using (var image = new System.Drawing.Bitmap(Path.Combine("dev", "testdata", "world-map", name + ".png"))) {
            var pixels = new int[image.Width * image.Height];
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) pixels[y * image.Width + x] = image.GetPixel(x, y).ToArgb();
            int sx = (int)Math.Round(marker.Position.X * (float)IconRecognizer.CALIBRATION_UI_SCALE);
            int sy = (int)Math.Round(marker.Position.Y * (float)IconRecognizer.CALIBRATION_UI_SCALE);
            return MapRecognizer.ReadPixels(jobs, pixels, image.Width, image.Height, sx - image.Width / 2,
                sy - image.Height / 2, IconRecognizer.CALIBRATION_UI_SCALE).Any(job => job.Key == marker.Point.Key);
        }
    }
    static void MapFrameEligibilityTests() {
        const float ui = (float)IconRecognizer.CALIBRATION_UI_SCALE;
        var screen = new Point(4000, 1600);
        var mouse = new Point(20, 800);
        var marker = new MapOverlay.Marker { MapId = 32,
            Point = new CompletionPoint("poi:clear", PointKind.PointOfInterest, null, 0, 0), Position = new Vector2(600, 600) };
        var otherFloor = new MapOverlay.Marker { MapId = 32,
            Point = new CompletionPoint("vista:other-floor", PointKind.Vista, null, 0, 0, onDefaultFloor: false), Position = new Vector2(629, 600) };
        var overlay = new MapOverlay();
        var shown = new[] { marker, otherFloor };
        overlay.SetMarkers(shown);
        var jobs = NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false);
        Check("shrunken_overlay_frame_does_not_hide_clear_game_poi", marker.Size < MapOverlay.FRAME_SIZE && MapFixtureReturnsKey("ritual-isle-filled-poi", jobs, marker),
            "A real done POI is clear in the game-only capture; its optional overlay frame shrinks near another-floor marker.");
        otherFloor.Position = new Vector2(601, 600); overlay.SetMarkers(shown);
        jobs = NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false);
        Check("absent_other_floor_icon_cannot_block_done_poi", overlay.AnyCrowded && MapFixtureReturnsKey("ritual-isle-filled-poi", jobs, marker),
            "A point from another API floor has no game icon here and must not suppress the visible POI through frame crowding.");
        var topMarker = new MapOverlay.Marker { MapId = 32, Point = marker.Point, Position = new Vector2(2000, 80) };
        shown = new[] { topMarker, marker, otherFloor }; overlay.SetMarkers(shown);
        jobs = NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false);
        Check("uncaptured_overlay_zoom_hint_cannot_hide_done_poi", overlay.AnyCrowded && MapFixtureReturnsKey("ritual-isle-filled-poi", jobs, topMarker),
            "Crowding elsewhere sets the overlay hint flag, but PrintWindow does not contain that Blish hint over this clear icon.");

        shown = new[] { marker }; overlay.SetMarkers(shown);
        jobs = NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false);
        Check("clear_unframed_hollow_poi_stays_open", !MapFixtureReturnsKey("moorwatch-hollow-poi", jobs, marker),
            "Making a clear game icon eligible does not turn its genuine hollow state into completion.");
        marker.Done = true;
        Check("map_frame_repair_preserves_already_done_filter", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false).Count == 0,
            "Already recorded points are still excluded.");
        marker.Done = false;
        Check("map_frame_repair_preserves_keep_open", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => true).Count == 0,
            "Undo and explicit keep-open choices remain authoritative.");
        Check("map_frame_repair_preserves_game_tooltip_exclusion", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, new Point(600, 600), 4000, 1600, key => false).Count == 0,
            "The game's own cursor tooltip remains excluded from icon reads.");
        marker.Position = new Vector2(100, 100);
        Check("map_frame_repair_preserves_game_legend_exclusion", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false).Count == 0,
            "The game's real completion legend still covers these pixels.");
        marker.Position = new Vector2(15, 600);
        Check("map_frame_repair_preserves_capture_bounds", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false).Count == 0,
            "A capture that would reach outside the client remains ineligible.");
        marker.Position = new Vector2(600, 600);
        var adjacent = new MapOverlay.Marker { MapId = 32, Point = new CompletionPoint("poi:adjacent", PointKind.PointOfInterest, null, 0, 0), Position = new Vector2(604, 600) };
        shown = new[] { marker, adjacent }; overlay.SetMarkers(shown);
        Check("overlapping_game_icon_searches_remain_ambiguous", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false).Count == 0,
            "Two visible objectives inside the same classifier search must not receive credit from one icon.");
        adjacent.Done = true;
        Check("completed_neighbor_cannot_supply_another_points_icon_credit", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false).Count == 0,
            "The icon of an already recorded neighbor still makes the fresh point's search ambiguous.");
        adjacent.Done = false;
        Check("kept_open_neighbor_cannot_supply_another_points_icon_credit", NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => key == adjacent.Point.Key).Count == 0,
            "The explicit keep-open point's game icon still exists and must not be attributed to its neighbor.");
        marker.MapId = 33; shown = new[] { marker, otherFloor }; overlay.SetMarkers(shown);
        jobs = NaxCompletionModule.RecognitionJobsForView(shown, ui, screen, mouse, 4000, 1600, key => false);
        Check("clear_neighboring_map_icon_keeps_owning_map_after_frame_repair", jobs.Count == 1 && jobs[0].MapId == 33 && MapFixtureReturnsKey("ritual-isle-filled-poi", jobs, marker),
            "A clear icon on another map keeps its owning map even when a virtual frame shrinks around it.");
    }
    static int Main() {
        MapFrameEligibilityTests();
        CompassEdgeRegressionTests();
        MapPointerRegressionTests();
        TooltipContextRegressionTests();
        CompassDiscoveryTests();
        HeroTooltipTests();
        foreach(int counter in Enumerable.Range(1,9)) {
            string path=Path.Combine("dev","testdata","world-map","label-counter-"+counter+".png");
            using(var bmp=new System.Drawing.Bitmap(path)) {
                var px=new int[bmp.Width*bmp.Height];
                for(int y=0;y<bmp.Height;y++)for(int x=0;x<bmp.Width;x++)px[y*bmp.Width+x]=bmp.GetPixel(x,y).ToArgb();
                Check(Path.GetFileNameWithoutExtension(path).Replace('-','_')+"_is_not_a_filled_poi",!IconRecognizer.IsFilled(px,bmp.Width,bmp.Height,bmp.Width/2,bmp.Height/2,PointKind.PointOfInterest,IconRecognizer.CALIBRATION_UI_SCALE),"Map-label lettering surrounding warm ground.");
            }
        }
        foreach (string name in new[] { "ritual-isle-filled-poi", "stonefall-filled-poi", "holtimm-filled-poi", "moorwatch-hollow-poi", "raintimber-hollow-poi", "ebonshore-covered-poi" }) {
            using(var bmp=new System.Drawing.Bitmap(Path.Combine("dev","testdata","world-map",name+".png"))) {
                var px=new int[bmp.Width*bmp.Height];
                for(int y=0;y<bmp.Height;y++)for(int x=0;x<bmp.Width;x++)px[y*bmp.Width+x]=bmp.GetPixel(x,y).ToArgb();
                Check(name.Replace('-','_')+"_matches_visible_poi_state",IconRecognizer.IsFilled(px,bmp.Width,bmp.Height,bmp.Width/2,bmp.Height/2,PointKind.PointOfInterest,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap)==name.Contains("-filled-"),"");
                if(name=="ritual-isle-filled-poi") {
                    var uniform=Enumerable.Repeat(Argb(255,207,98),px.Length).ToArray();
                    Check("bright_gold_ground_without_closed_poi_frame_stays_open",!IconRecognizer.IsFilled(uniform,bmp.Width,bmp.Height,35,35,PointKind.PointOfInterest,IconRecognizer.CALIBRATION_UI_SCALE),"");
                    var noTop=(int[])px.Clone();Fill(noTop,20,23,50,33,Argb(255,207,98));
                    Check("bright_poi_missing_one_frame_side_stays_open",!IconRecognizer.IsFilled(noTop,bmp.Width,bmp.Height,35,35,PointKind.PointOfInterest,IconRecognizer.CALIBRATION_UI_SCALE),"");
                    var ownColour=(int[])px.Clone();Fill(ownColour,32,33,36,37,Argb(255,140,0));
                    Check("bright_poi_cannot_use_modules_own_orange_as_fill",!IconRecognizer.IsFilled(ownColour,bmp.Width,bmp.Height,35,35,PointKind.PointOfInterest,IconRecognizer.CALIBRATION_UI_SCALE),"");
                }
            }
        }
        using(var bmp=new System.Drawing.Bitmap(Path.Combine("dev","testdata","hc","bright_compass_earned.png"))) {
            var px=new int[bmp.Width*bmp.Height];
            for(int y=0;y<bmp.Height;y++)for(int x=0;x<bmp.Width;x++)px[y*bmp.Width+x]=bmp.GetPixel(x,y).ToArgb();
            Check("bright_compass_earned_hero_challenge_reads_done",IconRecognizer.IsFilled(px,bmp.Width,bmp.Height,bmp.Width/2,bmp.Height/2,PointKind.HeroChallenge,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.Compass),"Completion confirmed by game tooltip.");
            var open=(int[])px.Clone();Fill(open,22,21,26,26,Argb(255,252,8));
            Check("bright_compass_hero_with_unfinished_gold_centre_stays_open",!IconRecognizer.IsFilled(open,bmp.Width,bmp.Height,25,25,PointKind.HeroChallenge,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.Compass),"");
            var spread=(int[])px.Clone();
            var border=(int[])typeof(IconRecognizer).GetField("HC_COMPASS_BORDER",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            for(int i=0;i<border.Length;i+=2)spread[(23+border[i+1])*bmp.Width+24+border[i]]=Argb(255,252,8);
            Check("bright_compass_hero_requires_outline_separation_from_ground",!IconRecognizer.IsFilled(spread,bmp.Width,bmp.Height,25,25,PointKind.HeroChallenge,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.Compass),"");
        }
        foreach(string terrain in new[]{"map_fog_dark_spot","map_fog_dark_streak","map_terrain_dark_post"})
            Check(terrain+"_cannot_be_a_bright_compass_hero",!HcDone(terrain,IconSet.Compass),"");
        foreach (string sample in new[] { "bright_worldmap_earned", "bright_worldmap_live_earned", "bright_worldmap_open" }) {
            using (var bitmap = new System.Drawing.Bitmap(Path.Combine("dev","testdata","hc",sample+".png"))) {
                var pixels = new int[bitmap.Width*bitmap.Height];
                for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++) pixels[y*bitmap.Width+x]=bitmap.GetPixel(x,y).ToArgb();
                bool expected = sample != "bright_worldmap_open";
                Check(sample+"_hero_challenge_matches_game_tooltip",IconRecognizer.IsFilled(pixels,bitmap.Width,bitmap.Height,bitmap.Width/2,bitmap.Height/2,PointKind.HeroChallenge,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap)==expected, "Completion state confirmed by game tooltip.");
                if (!expected) {
                    bool rejected = true;
                    for(int dy=-3;dy<=3;dy++) for(int dx=-3;dx<=3;dx++)
                        rejected &= !IconRecognizer.IsFilled(pixels,bitmap.Width,bitmap.Height,bitmap.Width/2+dx,bitmap.Height/2+dy,PointKind.HeroChallenge,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap);
                    Check("bright_unearned_hero_stays_open_across_projection_offsets",rejected, "");
                }
            }
        }
        // Real world-map samples whose icons are larger than the original calibration.
        foreach (string sample in new[] { "blasted-moors-done", "dawnright-estate-done", "charradis-estate-done", "blasted-moors-close-done", "dawnright-estate-close-done", "unexplored-fog" }) {
            using (var bitmap = new System.Drawing.Bitmap(Path.Combine("dev", "testdata", "world-map", sample + ".png"))) {
                var pixels = new int[bitmap.Width * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = bitmap.GetPixel(x,y).ToArgb();
                bool done = IconRecognizer.IsFilled(pixels, bitmap.Width, bitmap.Height, 35, 35, PointKind.Waypoint, IconRecognizer.CALIBRATION_UI_SCALE, IconSet.WorldMap);
                Check("live_world_map_" + sample.Replace('-', '_') + "_is_classified_correctly", done == (sample != "unexplored-fog"), "");
                if (sample == "blasted-moors-done") {
                    var hollow = (int[])pixels.Clone();
                    for (int i = 0; i < hollow.Length; i++) {
                        int r = (hollow[i] >> 16) & 255, g = (hollow[i] >> 8) & 255, b = hollow[i] & 255;
                        if (b > 200 && g > 170 && r < 200 && b - r > 40) hollow[i] = Argb(35,35,35);
                    }
                    Check("large_world_map_waypoint_with_hollow_centre_stays_open", !IconRecognizer.IsFilled(hollow, bitmap.Width, bitmap.Height, 35,35,PointKind.Waypoint,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap), "Derived hollow centre, original frame and terrain.");
                    Check("larger_world_map_waypoint_template_does_not_apply_to_compass", !IconRecognizer.IsFilled(pixels,bitmap.Width,bitmap.Height,35,35,PointKind.Waypoint,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.Compass), "");
                    Check("large_waypoint_near_buffer_edge_is_rejected_without_overread", !IconRecognizer.IsFilled(pixels,bitmap.Width,bitmap.Height,20,35,PointKind.Waypoint,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap), "");
                    var noRim = (int[])pixels.Clone();
                    for (int py = 20; py <= 50; py++) for (int px = 20; px <= 50; px++) {
                        int p = noRim[py * bitmap.Width + px];
                        if (0.3 * ((p >> 16) & 255) + 0.6 * ((p >> 8) & 255) + 0.1 * (p & 255) < 110) noRim[py * bitmap.Width + px] = Argb(240,240,240);
                    }
                    Check("large_waypoint_without_dark_diamond_rim_is_rejected", !IconRecognizer.IsFilled(noRim,bitmap.Width,bitmap.Height,35,35,PointKind.Waypoint,IconRecognizer.CALIBRATION_UI_SCALE,IconSet.WorldMap), "");
                }
            }
        }
        // Arrow rotation: clockwise positive, 0 = ahead
        Rot("facing N, target N -> ahead",       0, 10,  0, 1,   0);
        Rot("facing N, target E -> right +90",  10,  0,  0, 1,  90);
        Rot("facing N, target W -> left -90",  -10,  0,  0, 1, -90);
        Rot("facing N, target S -> behind 180",  0,-10,  0, 1, 180);
        Rot("facing E, target N -> left -90",    0, 10,  1, 0, -90);
        Rot("facing E, target S -> right +90",   0,-10,  1, 0,  90);
        Rot("facing W, target N -> right +90",   0, 10, -1, 0,  90);
        Rot("facing NE, target E -> right +45", 10,  0, 0.7071, 0.7071, 45);

        // Continent Y grows south: a point with smaller Y is north of the player
        NavMath.ContinentOffsetToEastNorth(100, 100, 100, 50, out double e1, out double n1);
        Check("continent smaller Y = north", e1 == 0 && n1 == 50, $"east {e1} north {n1}");
        NavMath.ContinentOffsetToEastNorth(100, 100, 150, 100, out double e2, out double n2);
        Check("continent bigger X = east", e2 == 50 && n2 == 0, $"east {e2} north {n2}");

        // Screen rotation used to draw the arrow: rotating "up" (0,-1) clockwise by +90 deg must point right (+X)
        var rotate = typeof(ArrowPanel).GetMethod("Rotate", BindingFlags.NonPublic | BindingFlags.Static);
        var right = (Vector2)rotate.Invoke(null, new object[] { new Vector2(0, -1), (float)(Math.PI / 2) });
        Check("screen rotate +90 turns up into right", Math.Abs(right.X - 1) < 1e-4 && Math.Abs(right.Y) < 1e-4, $"({right.X:0.###},{right.Y:0.###})");
        var left = (Vector2)rotate.Invoke(null, new object[] { new Vector2(0, -1), (float)(-Math.PI / 2) });
        Check("screen rotate -90 turns up into left", Math.Abs(left.X + 1) < 1e-4 && Math.Abs(left.Y) < 1e-4, $"({left.X:0.###},{left.Y:0.###})");

        // Progress store round trip, per character, survives reload, file name sanitising
        string dir = Path.Combine(Path.GetTempPath(), "naxtest-" + Guid.NewGuid().ToString("N"));
        var store = new ProgressStore(dir);
        store.LoadCharacter("Test Character");
        Check("new char: nothing done", !store.IsDone(19, "vista:1"), "");
        Check("set done returns changed", store.SetDone(19, "vista:1", true), "");
        Check("set done twice returns unchanged", !store.SetDone(19, "vista:1", true), "");
        Check("SetManyDone counts only new", store.SetManyDone(19, new[] { "vista:1", "poi:2", "wp:3" }) == 2, "");
        var store2 = new ProgressStore(dir);
        store2.LoadCharacter("Test Character");
        Check("reload keeps vista:1", store2.IsDone(19, "vista:1"), "");
        Check("reload keeps wp:3", store2.IsDone(19, "wp:3"), "");
        Check("other map untouched", !store2.IsDone(20, "vista:1"), "");
        store2.LoadCharacter("Other Char");
        Check("other character separate", !store2.IsDone(19, "vista:1"), "");
        store2.LoadCharacter("Test Character");
        Check("undo (set false) works", store2.SetDone(19, "poi:2", false) && !store2.IsDone(19, "poi:2"), "");
        Check("ClearMap returns count", store2.ClearMap(19) == 2, "");
        var store3 = new ProgressStore(dir); store3.LoadCharacter("Test Character");
        Check("clear persisted", !store3.IsDone(19, "vista:1"), "");
        var badName = new ProgressStore(dir); badName.LoadCharacter("a/b:c"); badName.SetDone(1, "x", true);
        Check("unsafe name sanitised", File.Exists(Path.Combine(dir, "a_b_c.json")), string.Join(",", Directory.GetFiles(dir)));
        Check("no leftover tmp files", Directory.GetFiles(dir, "*.tmp").Length == 0, "");
        Console.WriteLine(File.ReadAllText(Path.Combine(dir, "a_b_c.json")));
        var con = new ProgressStore(dir); con.LoadCharacter("Con"); con.SetDone(1, "x", true);
        Check("Windows device name CON avoided", File.Exists(Path.Combine(dir, "Con_.json")), "");
        File.WriteAllText(Path.Combine(dir, "Broken.json"), "{ this is not json");
        var broken = new ProgressStore(dir); broken.LoadCharacter("Broken");
        Check("corrupt file: saving still enabled after backup", !broken.SavingDisabled, "");
        Check("corrupt file moved aside", Directory.GetFiles(dir, "Broken.json.broken-*").Length == 1, string.Join(",", Directory.GetFiles(dir)));
        broken.SetDone(5, "poi:9", true);
        var broken2 = new ProgressStore(dir); broken2.LoadCharacter("Broken");
        Check("fresh file written after corrupt one", broken2.IsDone(5, "poi:9"), "");
        File.WriteAllText(Path.Combine(dir, "Nullmaps.json"), "{\"maps\": null}");
        var nm = new ProgressStore(dir); nm.LoadCharacter("Nullmaps");
        Check("maps:null handled", !nm.SavingDisabled && !nm.IsDone(1, "x"), "");

        // Map overlay input rules
        var mouse = Blish_HUD.GameService.Input.Mouse;
        var cap = typeof(MapOverlay).GetMethod("CapturesInput", BindingFlags.NonPublic | BindingFlags.Instance);
        Func<string, CompletionPoint> P = k => new CompletionPoint(k, PointKind.Vista, "Name " + k, 0, 0);
        var ov = new MapOverlay { Visible = true, Interactive = true };
        ov.SetMarkers(new[] {
            new MapOverlay.Marker { Point = P("a"),  Position = new Vector2(100, 100) },
            new MapOverlay.Marker { Point = P("b"),  Position = new Vector2(200, 100) },
            new MapOverlay.Marker { Point = P("c1"), Position = new Vector2(400, 400) },
            new MapOverlay.Marker { Point = P("c2"), Position = new Vector2(415, 405) } });
        Check("isolated frames full size and hit", ov.HitTest(new Point(100,100))?.Point.Key == "a" && ov.HitTest(new Point(200,100))?.Point.Key == "b", "");
        Check("close pair still clickable, each on its own side", ov.HitTest(new Point(400,400))?.Point.Key == "c1" && ov.HitTest(new Point(415,405))?.Point.Key == "c2", "");
        Check("close pair frames shrunk so boxes do not overlap", ov.HitTest(new Point(407,402))?.Point.Key == "c1" && ov.HitTest(new Point(408,402))?.Point.Key == "c2", ov.HitTest(new Point(407,402))?.Point.Key + "/" + ov.HitTest(new Point(408,402))?.Point.Key);
        Check("crowding reported for zoom hint", ov.AnyCrowded, "");
        Check("16 px from centre of a full frame hits", ov.HitTest(new Point(116,116)) != null, "");
        Check("17 px from centre misses", ov.HitTest(new Point(100,117)) == null, "");
        var keyboard = Blish_HUD.GameService.Input.Keyboard;
        mouse.Position = new Point(102, 98);
        keyboard.ActiveModifiers = ModifierKeys.None;
        Check("click key not held: frame is click-through", cap.Invoke(ov, null).ToString() == "None", cap.Invoke(ov, null).ToString());
        keyboard.ActiveModifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        Check("Alt+Shift (game marker chord) is not the click key", cap.Invoke(ov, null).ToString() == "None", cap.Invoke(ov, null).ToString());
        keyboard.ActiveModifiers = ModifierKeys.Ctrl | ModifierKeys.Alt;
        Check("AltGr (Ctrl+Alt) is not the click key", cap.Invoke(ov, null).ToString() == "None", cap.Invoke(ov, null).ToString());
        keyboard.ActiveModifiers = ModifierKeys.Ctrl;
        Check("Ctrl alone does not count while the setting is Alt", cap.Invoke(ov, null).ToString() == "None", cap.Invoke(ov, null).ToString());
        ov.ClickKey = FrameClickKey.Ctrl;
        Check("Ctrl alone counts once the setting is Ctrl", cap.Invoke(ov, null).ToString() == "Mouse", cap.Invoke(ov, null).ToString());
        ov.ClickKey = FrameClickKey.Alt;
        keyboard.ActiveModifiers = ModifierKeys.Alt;
        Check("captures from live cursor position", cap.Invoke(ov, null).ToString() == "Mouse", cap.Invoke(ov, null).ToString());
        mouse.Position = new Point(150, 150);
        Check("off a frame the game gets the input", cap.Invoke(ov, null).ToString() == "None", cap.Invoke(ov, null).ToString());

        mouse.Position = new Point(100, 100); mouse.ActiveControl = null;   // game will get the next click
        keyboard.ActiveModifiers = ModifierKeys.None;
        ov.CaptureFrameState();
        Check("cursor on frame, key not held: nothing armed, game keeps the click", ov.OwnedHover == null && mouse.ActiveControl == null, "");

        // Key pressed while the cursor rests on a frame: Blish would not re-pick until the mouse moves
        keyboard.ActiveModifiers = ModifierKeys.Alt;
        mouse.CameraDragging = true;
        ov.CaptureFrameState();
        Check("resting cursor while the camera is dragged: not armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        mouse.CameraDragging = false; mouse.CursorIsVisible = false;
        ov.CaptureFrameState();
        Check("resting cursor while the cursor is hidden: not armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        mouse.CursorIsVisible = true;
        Blish_HUD.GameService.GameIntegration.Gw2Instance.Gw2HasFocus = false;
        ov.CaptureFrameState();
        Check("resting cursor while GW2 is unfocused: not armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        Blish_HUD.GameService.GameIntegration.Gw2Instance.Gw2HasFocus = true;
        var other = new MapOverlay();
        mouse.ActiveControl = other;
        ov.CaptureFrameState();
        Check("resting cursor on another Blish control: that control is not replaced", mouse.ActiveControl == other && ov.OwnedHover == null, "");
        mouse.ActiveControl = null;
        Blish_HUD.GameService.Overlay.InterfaceHidden = true;
        ov.CaptureFrameState();
        Check("resting cursor while Blish's interface is hidden: not armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        Blish_HUD.GameService.Overlay.InterfaceHidden = false;
        var screen = Blish_HUD.GameService.Graphics.SpriteScreen;
        screen.TestPick = other;                                            // a Blish window lies over the frame
        ov.CaptureFrameState();
        Check("resting cursor under another Blish window: frame not armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        screen.TestPick = ov;
        ov.CaptureFrameState();
        Check("resting cursor, key pressed: frame armed without a mouse move", mouse.ActiveControl == ov && ov.OwnedHover?.Point.Key == "a", "" + ov.OwnedHover?.Point.Key);
        mouse.ActiveControl = ov;                                           // Blish will block the next click
        ov.CaptureFrameState();
        Check("cursor on frame and Blish owns the click: that frame toggles", ov.OwnedHover?.Point.Key == "a", "" + ov.OwnedHover?.Point.Key);
        mouse.Position = new Point(150, 150);                               // frames slid away under a resting cursor
        ov.CaptureFrameState();
        Check("off frame: mouse handed back to the game", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        mouse.ActiveControl = ov; mouse.Position = new Point(100, 100);
        ov.Interactive = false;
        ov.CaptureFrameState();
        Check("map still settling: nothing clickable and mouse released", ov.OwnedHover == null && mouse.ActiveControl == null && cap.Invoke(ov, null).ToString() == "None", "");
        ov.Interactive = true; mouse.ActiveControl = ov; ov.Visible = false;
        ov.CaptureFrameState();
        Check("hidden overlay releases the mouse", mouse.ActiveControl == null && ov.OwnedHover == null, "");

        // Arrow panel: Ctrl over a resting cursor makes it movable without a mouse move, and releasing Ctrl gives the
        // click back to the game
        var savedMods = keyboard.ActiveModifiers; var savedPos = mouse.Position; var savedActive = mouse.ActiveControl;
        var panel = new ArrowPanel { Location = new Point(400, 50), Size = new Point(ArrowPanel.PANEL_WIDTH, ArrowPanel.PANEL_HEIGHT), Visible = true };
        mouse.ActiveControl = null; mouse.Position = new Point(450, 80);
        Blish_HUD.GameService.Graphics.SpriteScreen.TestPick = panel;
        keyboard.ActiveModifiers = ModifierKeys.None;
        panel.UpdateDrag();
        Check("panel_under_a_resting_cursor_without_ctrl_lets_the_click_through", mouse.ActiveControl == null, "");
        keyboard.ActiveModifiers = ModifierKeys.Ctrl;
        panel.UpdateDrag();
        Check("pressing_ctrl_over_the_panel_makes_it_movable_without_a_mouse_move", mouse.ActiveControl == panel, "");
        keyboard.ActiveModifiers = ModifierKeys.Ctrl | ModifierKeys.Shift;
        panel.UpdateDrag();
        Check("ctrl_shift_hotkeys_over_the_panel_hand_the_click_back", mouse.ActiveControl == null, "");
        keyboard.ActiveModifiers = ModifierKeys.Ctrl; mouse.Position = new Point(10, 10);
        panel.UpdateDrag();
        Check("ctrl_held_away_from_the_panel_does_not_arm_it", mouse.ActiveControl == null, "");
        keyboard.ActiveModifiers = savedMods; mouse.Position = savedPos; mouse.ActiveControl = savedActive;
        Blish_HUD.GameService.Graphics.SpriteScreen.TestPick = ov;

        // Focus change: recorded hover must not survive GW2 losing focus
        ov.Visible = true; ov.Interactive = true; mouse.Position = new Point(100, 100); mouse.ActiveControl = ov;
        Blish_HUD.GameService.GameIntegration.Gw2Instance.Gw2HasFocus = false;
        ov.CaptureFrameState();
        Check("GW2 not focused: no frame armed for the next click", ov.OwnedHover == null, "");
        Blish_HUD.GameService.GameIntegration.Gw2Instance.Gw2HasFocus = true;
        ov.CaptureFrameState();
        Check("GW2 focused again: frame armed", ov.OwnedHover?.Point.Key == "a", "");

        // Click key released while a frame owns the mouse: the next click must go to the game
        keyboard.ActiveModifiers = ModifierKeys.None;
        ov.CaptureFrameState();
        Check("click key released: mouse handed back, nothing armed", mouse.ActiveControl == null && ov.OwnedHover == null, "");
        keyboard.ActiveModifiers = ModifierKeys.Alt;

        // Double click on a frame switches it once
        var t0 = new DateTime(2026, 10, 21, 1, 0, 0, DateTimeKind.Utc);
        Check("first click ever is not a repeat", !NaxCompletionModule.IsRepeatClick(null, DateTime.MinValue, "wp:1", t0), "");
        Check("same frame 200 ms later is a repeat", NaxCompletionModule.IsRepeatClick("wp:1", t0, "wp:1", t0.AddMilliseconds(200)), "");
        Check("same frame 499 ms later is a repeat", NaxCompletionModule.IsRepeatClick("wp:1", t0, "wp:1", t0.AddMilliseconds(499)), "");
        Check("same frame 500 ms later counts again", !NaxCompletionModule.IsRepeatClick("wp:1", t0, "wp:1", t0.AddMilliseconds(500)), "");
        Check("other frame 100 ms later counts", !NaxCompletionModule.IsRepeatClick("wp:1", t0, "poi:2", t0.AddMilliseconds(100)), "");

        // Same spot: open point wins the tie
        var tie = new MapOverlay { Visible = true, Interactive = true };
        tie.SetMarkers(new[] {
            new MapOverlay.Marker { Point = P("done"), Position = new Vector2(50, 50), Done = true },
            new MapOverlay.Marker { Point = P("open"), Position = new Vector2(50, 50), Done = false } });
        Check("two points on one spot: the open one is hit", tie.HitTest(new Point(50, 50))?.Point.Key == "open", "" + tie.HitTest(new Point(50, 50))?.Point.Key);

        var longP = new CompletionPoint("h", PointKind.Heart, "Help Ghyrtratus Fiendmauler retake Ascalon City", 0, 0);
        Check("map label cut to 36 chars", longP.ShortLabel.Length <= 36 && longP.ShortLabel.EndsWith("..."), longP.ShortLabel);
        Check("short label kept whole", P("x").ShortLabel == "Vista: Name x", P("x").ShortLabel);

        // Icon recognition on synthetic pixels at calibration scale (0.897). Background is dark map green.
        Func<int[]> Canvas = () => { var c = new int[80 * 80]; for (int i = 0; i < c.Length; i++) c[i] = Argb(40, 70, 40); return c; };
        const double UI = IconRecognizer.CALIBRATION_UI_SCALE;
        var poiFilled = Canvas(); Fill(poiFilled, 33, 33, 47, 47, Argb(240, 240, 235)); Fill(poiFilled, 36, 36, 44, 44, Argb(230, 120, 40));
        Check("filled POI (white frame, orange centre) is recognised", IconRecognizer.IsFilled(poiFilled, 80, 80, 41, 39, PointKind.PointOfInterest, UI), "");
        var poiHollow = Canvas(); Fill(poiHollow, 33, 33, 47, 47, Argb(150, 90, 40)); Fill(poiHollow, 36, 36, 44, 44, Argb(30, 30, 30));
        Check("hollow POI (brown frame, dark centre) is not", !IconRecognizer.IsFilled(poiHollow, 80, 80, 40, 40, PointKind.PointOfInterest, UI), "");
        var poiNoFrame = Canvas(); Fill(poiNoFrame, 36, 36, 44, 44, Argb(230, 120, 40));
        Check("orange square without white frame is not a POI", !IconRecognizer.IsFilled(poiNoFrame, 80, 80, 40, 40, PointKind.PointOfInterest, UI), "");
        var band = Canvas(); Fill(band, 0, 30, 79, 50, Argb(190, 82, 20));
        Check("event ring band is not a POI", !IconRecognizer.IsFilled(band, 80, 80, 40, 40, PointKind.PointOfInterest, UI), "");
        Check("event ring band is not a heart", !IconRecognizer.IsFilled(band, 80, 80, 40, 40, PointKind.Heart, UI), "");

        // Waypoint: tan diamond frame (Manhattan radius 13) with a cyan diamond centre (radius 5)
        Func<int[]> Waypoint = () => { var c = Canvas(); Diamond(c, 40, 40, 13, Argb(200, 180, 140)); Diamond(c, 40, 40, 8, Argb(70, 60, 50)); return c; };
        var wp = Waypoint(); Diamond(wp, 40, 40, 5, Argb(120, 230, 250));
        Check("filled waypoint (cyan centre in tan diamond) is recognised", IconRecognizer.IsFilled(wp, 80, 80, 41, 39, PointKind.Waypoint, UI), "");
        var wpHollow = Waypoint(); Diamond(wpHollow, 40, 40, 5, Argb(60, 60, 60));
        Check("hollow waypoint (grey centre) is not", !IconRecognizer.IsFilled(wpHollow, 80, 80, 40, 40, PointKind.Waypoint, UI), "");
        var wpCovered = Waypoint(); Diamond(wpCovered, 40, 40, 5, Argb(120, 230, 250)); Fill(wpCovered, 44, 26, 56, 34, Argb(110, 225, 245));
        Check("waypoint with another cyan icon over its corner is not read", !IconRecognizer.IsFilled(wpCovered, 80, 80, 40, 40, PointKind.Waypoint, UI), "");
        var wpNoFrame = Canvas(); Diamond(wpNoFrame, 40, 40, 5, Argb(120, 230, 250));
        Check("cyan spot without the tan diamond frame is not a waypoint", !IconRecognizer.IsFilled(wpNoFrame, 80, 80, 40, 40, PointKind.Waypoint, UI), "");

        // A done heart: gold body plus the warm tips of its two lobes above it (the game fills the tips last).
        var heart = Canvas(); Fill(heart, 33, 33, 47, 47, Argb(240, 180, 40));
        Fill(heart, 34, 31, 37, 32, Argb(190, 140, 40)); Fill(heart, 43, 31, 46, 32, Argb(190, 140, 40));
        Check("filled heart is recognised", IconRecognizer.IsFilled(heart, 80, 80, 40, 40, PointKind.Heart, UI), "");
        var heartHollow = Canvas(); Fill(heartHollow, 33, 33, 47, 47, Argb(240, 180, 40)); Fill(heartHollow, 35, 35, 45, 45, Argb(30, 25, 20));
        Check("hollow heart (gold outline) is not", !IconRecognizer.IsFilled(heartHollow, 80, 80, 40, 40, PointKind.Heart, UI), "");
        var foliage = Canvas(); for (int y = 33; y <= 47; y++) for (int x = 33; x <= 47; x++) if ((x * 7 + y * 3) % 10 < 6) foliage[y * 80 + x] = Argb(240, 180, 40);
        Check("patchy yellow foliage (about 60% gold) is not a heart", !IconRecognizer.IsFilled(foliage, 80, 80, 40, 40, PointKind.Heart, UI), "");
        var yellowArea = Canvas(); Fill(yellowArea, 15, 15, 65, 65, Argb(240, 180, 40));
        Check("large yellow area is not a heart", !IconRecognizer.IsFilled(yellowArea, 80, 80, 40, 40, PointKind.Heart, UI), "");

        var ownFrame = Canvas(); Fill(ownFrame, 30, 30, 50, 50, Argb(255, 140, 0)); Fill(ownFrame, 32, 32, 48, 48, Argb(40, 70, 40));
        Fill(ownFrame, 37, 37, 43, 43, Argb(255, 150, 10));
        Check("the module's own orange frame colour never counts as POI fill", !IconRecognizer.IsFilled(ownFrame, 80, 80, 40, 40, PointKind.PointOfInterest, UI), "");
        Check("the module's own orange frame colour never counts as heart fill", !IconRecognizer.IsFilled(ownFrame, 80, 80, 40, 40, PointKind.Heart, UI), "");
        var vista = Canvas(); Fill(vista, 30, 32, 50, 48, Argb(200, 50, 40));
        Check("hero_challenges_are_read_from_icons", IconRecognizer.CanRecognise(PointKind.HeroChallenge), "");
        // Vista: a solid 13 px triangle (filled) against an outline (hollow), compass and world map
        var vFilled = Canvas(); for (int r = 0; r < 9; r++) for (int c = 0; c <= r; c++) vFilled[(36 + r) * 80 + 36 + c] = Argb(160, 55, 30);   // about as much solid red as the live compass icon
        Check("filled compass vista (solid red triangle) is recognised", IconRecognizer.IsFilled(vFilled, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.Compass), "");
        var vHollow = Canvas(); for (int r = 0; r < 12; r++) for (int c = 0; c <= r; c++) if (c <= 1 || c >= r - 1 || r >= 10) vHollow[(34 + r) * 80 + 34 + c] = Argb(160, 55, 30);   // 2 px outline
        Check("hollow compass vista (red outline) is not", !IconRecognizer.IsFilled(vHollow, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.Compass), "");
        var vMap = Canvas(); for (int r = 0; r < 16; r++) for (int c = 0; c <= r; c++) vMap[(32 + r) * 80 + 32 + c] = Argb(160, 55, 30);
        Check("filled world-map vista is recognised", IconRecognizer.IsFilled(vMap, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.WorldMap), "");
        Check("the small compass triangle is too small for the world-map rule", !IconRecognizer.IsFilled(vFilled, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.WorldMap), "");
        var vBig = Canvas(); Fill(vBig, 20, 20, 60, 60, Argb(160, 55, 30));
        Check("a large red area is not a vista", !IconRecognizer.IsFilled(vBig, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.Compass) && !IconRecognizer.IsFilled(vBig, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.WorldMap), "");
        Check("the orange event ring colour is not vista red", !IconRecognizer.IsFilled(band, 80, 80, 40, 40, PointKind.Vista, UI, IconSet.Compass), "");
        Check("icon too close to the edge is not read", !IconRecognizer.IsFilled(heart, 80, 80, 10, 40, PointKind.Heart, UI), "");
        var heartBig = new int[120 * 120]; for (int i = 0; i < heartBig.Length; i++) heartBig[i] = Argb(40, 70, 40); Fill(heartBig, 50, 50, 70, 70, Argb(240, 180, 40));
        Fill(heartBig, 52, 46, 58, 48, Argb(190, 140, 40)); Fill(heartBig, 62, 46, 68, 48, Argb(190, 140, 40));
        Check("heart at 1.5x UI scale is recognised", IconRecognizer.IsFilled(heartBig, 120, 120, 60, 60, PointKind.Heart, UI * 1.5), "");
        // The larger waypoint band reaches 24 px from its centre, plus a search of 3.
        Check("reach covers the widest band plus search", IconRecognizer.Reach(UI) == 27, "" + IconRecognizer.Reach(UI));

        // Points you untick yourself stay open for recognition, also after a restart
        var keepDir = Path.Combine(Path.GetTempPath(), "nax-keep-" + Guid.NewGuid().ToString("N"));
        var keepStore = new ProgressStore(keepDir); keepStore.LoadCharacter("Keeper"); keepStore.KeepOpen(new[] { "wp:1", "wp:3" }); keepStore.ReleaseKeepOpen(new[] { "wp:3" });
        var keepAgain = new ProgressStore(keepDir); keepAgain.LoadCharacter("Keeper");
        Check("kept-open points survive a reload", keepAgain.IsKeptOpen("wp:1") && !keepAgain.IsKeptOpen("wp:2"), "");
        Check("released points are no longer kept open", !keepAgain.IsKeptOpen("wp:3"), "");
        var keepOther = new ProgressStore(keepDir); keepOther.LoadCharacter("Someone");
        Check("kept-open points are per character", !keepOther.IsKeptOpen("wp:1"), "");
        Directory.Delete(keepDir, true);

        // Areas of the map the game draws over (UI units, screen 3835x1605 = 3440x1440 at 0.897)
        var scr = new Point(3835, 1605); var far = new Point(-5000, -5000);
        Check("completion legend top left is covered", NaxCompletionModule.IsCoveredOnMap(new Vector2(100, 300), scr, far, false), "");
        Check("map centre is free", !NaxCompletionModule.IsCoveredOnMap(new Vector2(1900, 800), scr, far, false), "");
        Check("right edge buttons are covered", NaxCompletionModule.IsCoveredOnMap(new Vector2(3750, 800), scr, far, false), "");
        Check("compass bottom right is covered", NaxCompletionModule.IsCoveredOnMap(new Vector2(3700, 1500), scr, far, false), "");
        Check("tooltip area right of and above the cursor is covered", NaxCompletionModule.IsCoveredOnMap(new Vector2(2100, 700), scr, new Point(1900, 800), false) && !NaxCompletionModule.IsCoveredOnMap(new Vector2(1500, 800), scr, new Point(1900, 800), false), "");
        Check("near the right edge the tooltip area flips to the left", NaxCompletionModule.IsCoveredOnMap(new Vector2(3500, 700), scr, new Point(3700, 800), false), "");
        Check("zoom hint is covered only while shown", NaxCompletionModule.IsCoveredOnMap(new Vector2(1917, 70), scr, far, true) && !NaxCompletionModule.IsCoveredOnMap(new Vector2(1917, 70), scr, far, false), "");

        // Jumping puzzles: bundled list
        var puzzles = PuzzleCatalog.Puzzles;
        Check("bundled puzzle list loads 68 puzzles", puzzles.Count == 68, "" + puzzles.Count);
        Check("puzzle keys are unique", puzzles.Select(z => z.Key).Distinct().Count() == puzzles.Count, "");
        Check("Plains of Ashford has Loreclaw Expanse near Loreclaw Waypoint",
              PuzzleCatalog.ForMap(19).Any(z => z.Name == "Loreclaw Expanse" && z.Waypoint == "Loreclaw Waypoint" && z.X > 0 && z.Y > 0), "");

        // Jumping puzzle achievements
        Check("crimson_plateau_is_linked_to_its_jumping_puzzle_achievement", PuzzleCatalog.Find("jp:crimson-plateau")?.AchievementId == 154, "" + PuzzleCatalog.Find("jp:crimson-plateau")?.AchievementId);
        Check("at_least_40_puzzles_are_linked_to_an_achievement", puzzles.Count(z => z.AchievementId > 0) >= 40, "" + puzzles.Count(z => z.AchievementId > 0));
        Check("achievement_ids_are_never_shared_by_two_puzzles", puzzles.Where(z => z.AchievementId > 0).Select(z => z.AchievementId).Distinct().Count() == puzzles.Count(z => z.AchievementId > 0), "");
        var answer = new List<Gw2Sharp.WebApi.V2.Models.AccountAchievement> {
            new Gw2Sharp.WebApi.V2.Models.AccountAchievement { Id = 154, Done = true },
            new Gw2Sharp.WebApi.V2.Models.AccountAchievement { Id = 199, Done = false }, null };
        var doneIds = AchievementSync.DoneIds(answer);
        Check("only_done_achievements_count_and_a_null_entry_is_ignored", doneIds.Contains(154) && !doneIds.Contains(199) && doneIds.Count == 1, "");
        Check("a_missing_api_answer_means_nothing_is_done", AchievementSync.DoneIds(null).Count == 0, "");
        Check("an_empty_api_answer_means_nothing_is_done", AchievementSync.DoneIds(new List<Gw2Sharp.WebApi.V2.Models.AccountAchievement>()).Count == 0, "");

        // Recorded routes: file round trip
        var routeDir = Path.Combine(Path.GetTempPath(), "nax-routes-" + Guid.NewGuid().ToString("N"));
        var routeStore = new RouteStore(routeDir);
        var saved = new PuzzleRoute { Key = "jp:loreclaw-expanse", Puzzle = "Loreclaw Expanse", MapId = 19, StartX = 1, StartY = 2,
                                      Steps = { new RouteStep { X = 1, Y = 2, Z = 3, Note = "jump left" }, new RouteStep { X = 4, Y = 5, Z = 6 } } };
        routeStore.Save(saved);
        var reloaded = new RouteStore(routeDir); reloaded.LoadAll();
        var back = reloaded.Get("jp:loreclaw-expanse");
        Check("route survives save and reload", back != null && back.Steps.Count == 2 && back.Steps[0].Note == "jump left" && back.Steps[1].Z == 6 && back.MapId == 19, "");
        Check("route file name has no colon", RouteStore.FileName("jp:loreclaw-expanse") == "jp_loreclaw-expanse.json", RouteStore.FileName("jp:loreclaw-expanse"));
        Check("routes are found by map", reloaded.ForMap(19).Count == 1 && reloaded.ForMap(20).Count == 0, "");

        // Shipped routes from the marker pack trails, and your own route taking precedence
        var shipped = new RouteStore(routeDir); shipped.LoadBuiltIn();
        var shaman  = shipped.Get("jp:shaman-s-rookery");
        Check("shipped_route_for_shamans_rookery_starts_at_the_puzzle",
              shaman != null && shaman.BuiltIn && shaman.MapId == 28 && shaman.Steps.Count > 20 && Math.Abs(shaman.Steps[0].X - (-321.4)) < 1 && Math.Abs(shaman.Steps[0].Z - 92.8) < 1,
              shaman == null ? "missing" : $"{shaman.Steps.Count} steps, first {shaman.Steps[0].X},{shaman.Steps[0].Y},{shaman.Steps[0].Z}");
        var shippedRoutes = PuzzleCatalog.Puzzles.Select(z => shipped.Get(z.Key)).Where(r => r != null).ToList();
        Check("most_catalog_puzzles_have_a_shipped_route", shippedRoutes.Count >= 55, "" + shippedRoutes.Count);
        Check("every_shipped_route_is_on_its_puzzles_map_and_starts_with_a_walk_step",
              shippedRoutes.All(r => r.MapId == PuzzleCatalog.Find(r.Key).MapId && r.Steps[0].Type == StepType.Walk), "");
        Check("shipped_route_has_no_gap_over_30_m_without_a_next_part_note",
              shippedRoutes.All(r => r.Steps.Zip(r.Steps.Skip(1), (a, b) => new { a, b })
                                           .All(p => p.b.Note == "Next part of the puzzle"
                                                  || Math.Sqrt((p.a.X - p.b.X) * (p.a.X - p.b.X) + (p.a.Y - p.b.Y) * (p.a.Y - p.b.Y) + (p.a.Z - p.b.Z) * (p.a.Z - p.b.Z)) <= 30.01)), "");
        var blitz = shipped.Get("jp:wall-breach-blitz");
        Check("jump_onto_the_block_in_wall_breach_blitz_is_marked_as_a_jump", blitz?.Steps.Count > 99 && blitz.Steps[34].Type == StepType.Jump, blitz == null ? "missing" : blitz.Steps[34].Type.ToString());
        Check("staircase_in_wall_breach_blitz_is_marked_as_walking", blitz?.Steps.Count > 99 && blitz.Steps[99].Type == StepType.Walk, blitz == null ? "missing" : blitz.Steps[99].Type.ToString());
        // Vista approaches from the barefoot map completion trails
        var vistaRoutes = Enumerable.Range(0, 3000).Select(i => shipped.Get("vista:" + i)).Where(r => r != null).ToList();
        Check("at_least_150_vistas_have_a_shipped_approach", vistaRoutes.Count >= 150, "" + vistaRoutes.Count);
        Check("every_vista_approach_climbs_to_the_vista", vistaRoutes.All(r => r.Steps.Count >= 2 && r.Steps[r.Steps.Count - 1].Z - r.Steps.Min(z => z.Z) >= 4), "");
        var burntfur = shipped.Get("vista:1498");
        Check("vista_near_burntfurs_pool_has_a_climbing_approach_in_diessa_plateau",
              burntfur != null && burntfur.MapId == 32 && burntfur.Puzzle == "Vista near Burntfur's Pool"
              && burntfur.Steps[burntfur.Steps.Count - 1].Z - burntfur.Steps.Min(z => z.Z) >= 20, burntfur?.Puzzle);
        // The vista is at (936.8, -339.3) in map metres (API coord 59648.7, 29356.6). The first version ended 8.3 m
        // past it, because it took the highest trail point near the vista, and the trail climbs on beyond it.
        var bEnd = burntfur?.Steps[burntfur.Steps.Count - 1];
        Check("vista_route_at_burntfurs_pool_ends_at_the_vista_not_past_it",
              bEnd != null && Math.Sqrt((bEnd.X - 936.8) * (bEnd.X - 936.8) + (bEnd.Y + 339.3) * (bEnd.Y + 339.3)) <= 3, bEnd == null ? "missing" : $"{bEnd.X},{bEnd.Y}");
        // The Ritual Isle vista is at (319.6, 573.1) in map metres. Its first route ended 9.4 m away on a rock above it.
        var ritual = shipped.Get("vista:1499");
        var rEnd   = ritual?.Steps[ritual.Steps.Count - 1];
        Check("vista_route_at_ritual_isle_ends_at_the_vista_not_on_the_rock_beside_it",
              rEnd != null && Math.Sqrt((rEnd.X - 319.6) * (rEnd.X - 319.6) + (rEnd.Y - 573.1) * (rEnd.Y - 573.1)) <= 3, rEnd == null ? "missing" : $"{rEnd.X},{rEnd.Y}");
        Check("vista_routes_are_told_apart_from_puzzle_routes", RouteStore.IsVistaRoute(burntfur) && !RouteStore.IsVistaRoute(shipped.Get("jp:wall-breach-blitz")), "");
        shipped.LoadAll();
        var own = shipped.Get("jp:loreclaw-expanse");
        Check("own_recorded_route_is_used_instead_of_the_shipped_one", own != null && !own.BuiltIn && own.Steps.Count == 2 && shipped.ForMap(19).Count(r => r.Key == "jp:loreclaw-expanse") == 1, "");
        Check("shipped_route_stays_for_puzzles_you_did_not_record", shipped.Get("jp:shaman-s-rookery")?.BuiltIn == true, "");
        Directory.Delete(routeDir, true);

        // Guide: four steps 10 m apart going east and up
        var line = new PuzzleRoute { Key = "jp:test", Puzzle = "Test", MapId = 1 };
        for (int i = 0; i < 4; i++) line.Steps.Add(new RouteStep { X = i * 10, Y = 0, Z = i * 2 });
        var routesHere = new List<PuzzleRoute> { line };
        var guide = new PuzzleGuide();
        Check("far from the route: no guide", !guide.Update(routesHere, new Vector3(-50, 0, 0)) && guide.Route == null, "");
        Check("entering the zone heads for the nearest step", guide.Update(routesHere, new Vector3(-20, 0, 0)) && guide.NextStep == 0, "" + guide.NextStep);
        guide.Update(routesHere, new Vector3(1, 0, 0));
        Check("reaching step 1 makes step 2 next", guide.NextStep == 1, "" + guide.NextStep);
        guide.Update(routesHere, new Vector3(5, 3, 1));
        Check("between steps the target stays", guide.NextStep == 1, "" + guide.NextStep);
        guide.Update(routesHere, new Vector3(20, 0, 4));
        Check("skipping ahead to step 3 makes step 4 next", guide.NextStep == 3, "" + guide.NextStep);
        guide.Update(routesHere, new Vector3(10, 1, 2));
        Check("falling back to step 2 makes step 3 next again", guide.NextStep == 2, "" + guide.NextStep);
        guide.Update(routesHere, new Vector3(30, 0, 6));
        Check("reaching the last step finishes the route", guide.Finished, "" + guide.NextStep);
        Check("following_a_route_to_its_end_completes_the_puzzle", guide.CompletedThisVisit, "");
        // Ten steps 10 m apart: arriving at the end without following the route does not complete the puzzle
        var ten = new PuzzleRoute { Key = "jp:ten", Puzzle = "Ten", MapId = 1 };
        for (int i = 0; i < 10; i++) ten.Steps.Add(new RouteStep { X = i * 10, Y = 0, Z = i });
        var tenRoutes = new List<PuzzleRoute> { ten };
        var jpA = new PuzzleGuide();
        jpA.Update(tenRoutes, new Vector3(90, 0, 9));
        Check("arriving_straight_at_the_end_of_a_route_does_not_complete_the_puzzle", jpA.Finished && !jpA.CompletedThisVisit, "");
        var jpB = new PuzzleGuide();
        foreach (int i in new[] { 0, 1, 9 }) jpB.Update(tenRoutes, new Vector3(i * 10, 0, i));
        Check("reaching_the_end_after_only_two_of_ten_steps_does_not_complete_the_puzzle", jpB.Finished && !jpB.CompletedThisVisit, "");
        var jpC = new PuzzleGuide();
        foreach (int i in new[] { 0, 2, 4, 6, 9 }) jpC.Update(tenRoutes, new Vector3(i * 10, 0, i));
        Check("reaching_the_end_after_half_the_steps_completes_the_puzzle", jpC.CompletedThisVisit, "");
        jpC.Update(tenRoutes, new Vector3(500, 0, 0));
        // Claim: the module ticks a completed visit once; leaving the zone clears the claim
        var jpD = new PuzzleGuide();
        foreach (int i in new[] { 0, 2, 4, 6, 9 }) jpD.Update(tenRoutes, new Vector3(i * 10, 0, i));
        jpD.ClaimCompletion();
        jpD.Update(tenRoutes, new Vector3(90, 0, 9));
        Check("a_claimed_completion_stays_claimed_while_you_stay_at_the_end", jpD.CompletedThisVisit && jpD.CompletionClaimed, "");
        jpD.Update(tenRoutes, new Vector3(500, 0, 0));
        foreach (int i in new[] { 0, 2, 4, 6, 9 }) jpD.Update(tenRoutes, new Vector3(i * 10, 0, i));
        Check("a_new_visit_can_complete_the_puzzle_again_after_leaving_the_zone", jpD.CompletedThisVisit && !jpD.CompletionClaimed, "");
        var two = new PuzzleRoute { Key = "jp:two", Puzzle = "Two", MapId = 1 };
        two.Steps.Add(new RouteStep { X = 0, Y = 0, Z = 0 }); two.Steps.Add(new RouteStep { X = 10, Y = 0, Z = 1 });
        var jpE = new PuzzleGuide();
        jpE.Update(new List<PuzzleRoute> { two }, new Vector3(0, 0, 0));
        jpE.Update(new List<PuzzleRoute> { two }, new Vector3(10, 0, 1));
        Check("a_two_step_route_followed_to_its_end_completes_the_puzzle", jpE.CompletedThisVisit, "");
        var one = new PuzzleRoute { Key = "jp:one", Puzzle = "One", MapId = 1 };
        one.Steps.Add(new RouteStep { X = 0, Y = 0, Z = 0 });
        var jpF = new PuzzleGuide();
        jpF.Update(new List<PuzzleRoute> { one }, new Vector3(0, 0, 0));
        Check("a_one_step_route_never_completes_the_puzzle", jpF.Finished && !jpF.CompletedThisVisit, "");
        Check("leaving_the_zone_forgets_the_steps_reached",!jpC.CompletedThisVisit && jpC.Route == null, "");
        Check("leaving the zone hides the guide", !guide.Update(routesHere, new Vector3(80, 0, 6)) && guide.Route == null && !guide.Finished, "");
        // Fall: heading for step 3 (index 2) after reaching step 2, then dropping 10 m below it near step 1
        var fall = new PuzzleRoute { Key = "jp:fall", Puzzle = "Fall", MapId = 1 };
        fall.Steps.Add(new RouteStep { X = 0,  Y = 0, Z = 0 });
        fall.Steps.Add(new RouteStep { X = 10, Y = 0, Z = 12 });
        fall.Steps.Add(new RouteStep { X = 20, Y = 0, Z = 24 });
        var fallRoutes = new List<PuzzleRoute> { fall };
        var g2 = new PuzzleGuide();
        g2.Update(fallRoutes, new Vector3(10, 0, 12));
        Check("climbing: reached step 2, step 3 next", g2.NextStep == 2, "" + g2.NextStep);
        g2.Update(fallRoutes, new Vector3(14, 0, 18));
        Check("climbing above the last step is not a fall", g2.NextStep == 2, "" + g2.NextStep);
        g2.Update(fallRoutes, new Vector3(8, 6, 1));
        Check("falling far below the last step restarts at the nearest reachable step", g2.NextStep == 0, "" + g2.NextStep);
        g2.Update(fallRoutes, new Vector3(1, 1, 0));
        Check("after the fall, reaching step 1 continues normally", g2.NextStep == 1, "" + g2.NextStep);

        // A planned drop: after the high step 2 the route goes down to step 3
        var drop = new PuzzleRoute { Key = "jp:drop", Puzzle = "Drop", MapId = 1 };
        drop.Steps.Add(new RouteStep { X = 0,  Y = 0, Z = 0 });
        drop.Steps.Add(new RouteStep { X = 10, Y = 0, Z = 20 });
        drop.Steps.Add(new RouteStep { X = 20, Y = 0, Z = 5 });
        drop.Steps.Add(new RouteStep { X = 30, Y = 0, Z = 5 });
        var g4 = new PuzzleGuide();
        var dropRoutes = new List<PuzzleRoute> { drop };
        g4.Update(dropRoutes, new Vector3(10, 0, 20));
        g4.Update(dropRoutes, new Vector3(15, 0, 11));
        Check("dropping down towards a lower next step is not a fall", g4.NextStep == 2, "" + g4.NextStep);

        // Two routes close together: the one being followed keeps the guide
        var second = new PuzzleRoute { Key = "jp:other", Puzzle = "Other", MapId = 1 };
        second.Steps.Add(new RouteStep { X = 0, Y = 12, Z = 0 });
        var both = new List<PuzzleRoute> { fall, second };
        var g3 = new PuzzleGuide();
        g3.Update(both, new Vector3(0, 0, 0));
        g3.Update(both, new Vector3(0, 5, 0));
        Check("a slightly closer second route does not take over", g3.Route == fall, g3.Route?.Key);
        g3.Update(both, new Vector3(0, 11, 0));
        Check("a clearly closer second route takes over", g3.Route == second, g3.Route?.Key);

        // Arrow kind filter cycle
        Func<PointKind, bool> allKinds = k => true;
        Check("kind cycle starts with waypoints", NaxCompletionModule.NextKindFilter(null, allKinds) == PointKind.Waypoint, "");
        Check("kind cycle reaches puzzles after hero challenges", NaxCompletionModule.NextKindFilter(PointKind.HeroChallenge, allKinds) == PointKind.Puzzle, "");
        Check("kind cycle returns to all after puzzles", NaxCompletionModule.NextKindFilter(PointKind.Puzzle, allKinds) == null, "");
        Check("kind cycle skips kinds that are not tracked", NaxCompletionModule.NextKindFilter(PointKind.Heart, k => k != PointKind.HeroChallenge) == PointKind.Puzzle, "");
        Check("height text", PuzzleGuide.HeightText(0, 4) == "4 m up" && PuzzleGuide.HeightText(10, 4) == "6 m down" && PuzzleGuide.HeightText(0, 1) == "same height", "");
        Check("puzzles are never read from map icons", !IconRecognizer.CanRecognise(PointKind.Puzzle), "");

        // Step type detection from movement samples (60 Hz)
        Func<Action<StepTypeDetector>, StepType> Run = feed => { var det = new StepTypeDetector(); det.Reset(0, 0, 0); feed(det); return det.Classify(); };
        Check("flat movement is a walk", Run(d => { for (int i = 1; i <= 120; i++) d.Sample(i * 0.08f, 0, 0, 1f / 60, false); }) == StepType.Walk, "");
        Check("walking up a slope is a walk", Run(d => { for (int i = 1; i <= 120; i++) d.Sample(i * 0.08f, 0, i * 0.01f, 1f / 60, false); }) == StepType.Walk, "");
        // Ballistic jump: launch at 5 m/s up, gravity 10 m/s², 60 Hz samples
        Action<StepTypeDetector, float, float> Jump = (d, forward, startX) => { float vz = 5f; float z = 0, x = startX; for (int i = 0; i < 60; i++) { z += vz / 60; x += forward / 60; vz -= 10f / 60; d.Sample(x, 0, Math.Max(0, z), 1f / 60, false); } };
        Check("standing jump is a jump", Run(d => Jump(d, 0.5f, 0)) == StepType.Jump, "");
        Check("running jump is a run", Run(d => { for (int i = 1; i <= 30; i++) d.Sample(i * 0.15f, 0, 0, 1f / 60, false); Jump(d, 9f, 4.5f); }) == StepType.Run, "");
        Check("running up a 15 degree ramp at 9.9 m/s is a walk", Run(d => { for (int i = 1; i <= 120; i++) d.Sample(i * 0.16f, 0, i * 0.043f, 1f / 60, false); }) == StepType.Walk, "");
        Check("running down an 8 degree slope is a walk, not a glide", Run(d => { for (int i = 1; i <= 180; i++) d.Sample(i * 0.123f, 0, -i * 0.0175f, 1f / 60, false); }) == StepType.Walk, "");
        Check("mounted movement is fly", Run(d => { for (int i = 1; i <= 60; i++) d.Sample(i * 0.2f, 0, 0, 1f / 60, true); }) == StepType.Fly, "");
        Check("a long slow descent after a fall is a glide", Run(d => { float z = 20; for (int i = 1; i <= 15; i++) { z -= 0.1f; d.Sample(i * 0.05f, 0, z, 1f / 60, false); } for (int i = 1; i <= 120; i++) { z -= 0.03f; d.Sample(1 + i * 0.08f, 0, z, 1f / 60, false); } }) == StepType.Fly, "");
        Check("a long slow descent without a fall first is not a glide", Run(d => { for (int i = 1; i <= 120; i++) d.Sample(i * 0.08f, 0, -i * 0.03f, 1f / 60, false); }) == StepType.Walk, "");
        Check("a short drop is not a glide", Run(d => { for (int i = 1; i <= 30; i++) d.Sample(i * 0.08f, 0, -i * 0.05f, 1f / 60, false); }) == StepType.Walk, "");
        Check("a removed jump step keeps counting for the next one", Run(d => { d.Assume(StepType.Jump); for (int i = 1; i <= 30; i++) d.Sample(i * 0.05f, 0, 0, 1f / 60, false); }) == StepType.Jump, "");
        Check("step types cycle walk, run, jump, fly, walk", StepTypeDetector.Next(StepType.Walk) == StepType.Run && StepTypeDetector.Next(StepType.Fly) == StepType.Walk, "");
        var typed = Newtonsoft.Json.JsonConvert.SerializeObject(new RouteStep { Type = StepType.Jump });
        Check("step type is saved by name", typed.Contains("\"type\": \"Jump\"") || typed.Contains("\"type\":\"Jump\""), typed);
        Check("a route file without a type reads as walk", Newtonsoft.Json.JsonConvert.DeserializeObject<RouteStep>("{\"x\":1,\"y\":2,\"z\":3}").Type == StepType.Walk, "");

        // Compass projection: compass 362x275 px at (3078, 1135), centre (3259, 1272.5), scale 1.75, no rotation
        int sx, sy;
        // Marker exclusion is a circle of 18 px: at scale 1.75 one pixel is 1.75 continent units.
        Check("icon_19_px_below_the_marker_is_read", MinimapRecognizer.TryProject(1000, 1000 + 19 * 1.75, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("icon_15_px_beside_and_25_px_below_the_marker_is_read", MinimapRecognizer.TryProject(1000 + 15.3 * 1.75, 1000 + 25 * 1.75, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("icon_17_px_below_the_marker_is_not_read", !MinimapRecognizer.TryProject(1000, 1000 + 17 * 1.75, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("icon_12_px_diagonally_off_the_marker_is_not_read", !MinimapRecognizer.TryProject(1000 + 12 * 1.75, 1000 + 12 * 1.75, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("icon_14_px_diagonally_off_the_marker_is_read", MinimapRecognizer.TryProject(1000 + 14 * 1.75, 1000 + 14 * 1.75, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("a point on the player marker is not read",!MinimapRecognizer.TryProject(1000, 1000, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("a point 175 continent units east lands 100 px right of the centre", MinimapRecognizer.TryProject(1175, 1000, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy) && sx == 3359 && Math.Abs(sy - 1273) <= 1, sx + "," + sy);
        Check("a point past the compass edge is not read", !MinimapRecognizer.TryProject(1400, 1000, 1000, 1000, 1000, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        Check("with a dragged compass the marker exclusion follows the player, not the centre",
              !MinimapRecognizer.TryProject(1175, 1000, 1000, 1000, 1175, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy)
              && MinimapRecognizer.TryProject(1000, 1000, 1000, 1000, 1175, 1000, 1.75, 0, 3078, 1135, 362, 275, 0.897, out sx, out sy), "");
        // Compass heart: 13x11 gold heart, filled and hollow
        var cHeart = Canvas(); Fill(cHeart, 36, 36, 44, 44, Argb(240, 180, 40));   // 9x9 solid core, as the live compass heart measures
        Check("filled compass heart is recognised with the compass rules", IconRecognizer.IsFilled(cHeart, 80, 80, 40, 40, PointKind.Heart, UI, IconSet.Compass), "");
        Check("the same small heart fails the world-map rules", !IconRecognizer.IsFilled(cHeart, 80, 80, 40, 40, PointKind.Heart, UI, IconSet.WorldMap), "");
        var cHollow = Canvas(); Fill(cHollow, 35, 35, 45, 45, Argb(240, 180, 40)); Fill(cHollow, 37, 37, 43, 43, Argb(30, 25, 20));
        Check("hollow compass heart is not", !IconRecognizer.IsFilled(cHollow, 80, 80, 40, 40, PointKind.Heart, UI, IconSet.Compass), "");
        Check("rotation by 90 degrees turns east into south", MinimapRecognizer.TryProject(1175, 1000, 1000, 1000, 1000, 1000, 1.75, Math.PI / 2, 3078, 1135, 362, 275, 0.897, out sx, out sy) && Math.Abs(sx - 3259) <= 1 && Math.Abs(sy - 1373) <= 1, sx + "," + sy);

        // Hearts on real game pixels: the game fills a heart from the bottom up, so a heart in progress has a dark top.
        // Partial hearts are made from a real done heart by darkening only the icon's own pixels above a fill line,
        // with the map behind the icon replaced by yellow ground, which the old tip-band rule would have let through.
        var doneHeart = Crop("map_done_heart");
        Func<int, bool> warmPx = c => ((c >> 16) & 255) > 150 && ((c >> 8) & 255) > 100 && (c & 255) < 90;
        Func<int, bool, int[]> heartVariant = (darkAbove, yellowGround) => {
            var v = (int[])doneHeart.Clone();
            for (int py = 0; py < 41; py++) for (int px = 0; px < 41; px++) {
                int i = py * 41 + px;
                bool icon = Math.Abs(px - 20) <= 12 && Math.Abs(py - 20) <= 12 && warmPx(doneHeart[i]);
                if (icon && py < darkAbove) v[i] = Argb(60, 45, 25);
                else if (!icon && yellowGround) v[i] = Argb(195, 165, 60);
            }
            return v;
        };
        Func<int[], int, int, bool> heartDone = (px, x, y) => IconRecognizer.IsFilled(px, 41, 41, x, y, PointKind.Heart, IconRecognizer.CALIBRATION_UI_SCALE, IconSet.WorldMap);
        Check("done_heart_on_the_world_map_reads_done", heartDone(doneHeart, 20, 20), "");
        Check("done_heart_on_yellow_ground_reads_done", heartDone(heartVariant(0, true), 20, 20), "");
        // The 41 px crop is too small to read 2 px off centre (the read reaches 19 px), so it is placed in a 61 px canvas.
        var padded = new int[61 * 61];
        for (int i = 0; i < padded.Length; i++) padded[i] = doneHeart[0];
        for (int py = 0; py < 41; py++) for (int px = 0; px < 41; px++) padded[(py + 10) * 61 + px + 10] = doneHeart[py * 41 + px];
        Func<int, int, bool> paddedDone = (x, y) => IconRecognizer.IsFilled(padded, 61, 61, x, y, PointKind.Heart, IconRecognizer.CALIBRATION_UI_SCALE, IconSet.WorldMap);
        Check("done_heart_read_2_px_off_its_centre_still_reads_done", paddedDone(30, 32) && paddedDone(30, 28) && paddedDone(32, 30) && paddedDone(28, 30), "");
        Check("heart_filled_to_85_percent_on_yellow_ground_reads_open", !heartDone(heartVariant(16, true), 20, 20), "");
        Check("heart_missing_only_its_lobe_tips_on_yellow_ground_reads_open", !heartDone(heartVariant(13, true), 20, 20) && !heartDone(heartVariant(12, true), 20, 20), "");

        // Hero challenges on real game pixels
        Check("hero_challenge_done_on_world_map_reads_done", HcDone("map_done_plains", IconSet.WorldMap), "");
        Check("hero_challenge_done_on_zoomed_out_world_map_reads_done", HcDone("map_done_plains_zoomed_out", IconSet.WorldMap), "");
        Check("hero_challenge_open_on_world_map_reads_open", !HcDone("map_open_old_grendich", IconSet.WorldMap) && !HcDone("map_open_old_grendich_2", IconSet.WorldMap), "");
        Check("hero_challenge_done_on_compass_reads_done", HcDone("compass_done_moorwatch", IconSet.Compass), "");
        Check("hero_challenge_done_on_compass_in_a_brighter_frame_reads_done", HcDone("compass_done_moorwatch_brighter", IconSet.Compass), "");
        Check("dark_spot_in_map_fog_is_not_a_done_hero_challenge", !HcDone("map_fog_dark_spot", IconSet.WorldMap) && !HcDone("map_fog_dark_streak", IconSet.WorldMap), "");
        Check("dark_post_on_yellow_terrain_is_not_a_done_hero_challenge", !HcDone("map_terrain_dark_post", IconSet.WorldMap), "");
        Check("open_heart_is_not_a_done_hero_challenge", !HcDone("compass_open_heart", IconSet.Compass) && !HcDone("map_open_heart", IconSet.WorldMap), "");
        Check("done_heart_is_not_a_done_hero_challenge", !HcDone("map_done_heart", IconSet.WorldMap) && !HcDone("map_done_heart", IconSet.Compass), "");
        Check("hero_challenge_at_the_image_edge_reads_open", !IconRecognizer.IsFilled(Crop("map_done_plains"), 41, 41, 5, 20, PointKind.HeroChallenge, IconRecognizer.CALIBRATION_UI_SCALE, IconSet.WorldMap), "");

        // Undo history: only the current map, only the last two minutes, one step per burst of presses
        var u0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        bool rep;
        var undo = new UndoHistory();
        undo.Push(19, new List<string> { "wp:1" }, true, u0);
        Check("undo_on_another_map_leaves_old_map_ticks_alone", undo.Take(32, u0.AddSeconds(10), out rep) == null && undo.Count == 1, "");
        Check("undo_two_hours_later_unticks_nothing", undo.Take(19, u0.AddHours(2), out rep) == null && !rep && undo.Count == 0, "");
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1" }, false, u0);
        undo.Push(32, new List<string> { "wp:2" }, false, u0.AddSeconds(5));
        undo.Push(19, new List<string> { "heart:3" }, true, u0.AddSeconds(10));
        var took = undo.Take(19, u0.AddSeconds(20), out rep);
        Check("undo_takes_newest_entry_of_current_map", took != null && took.Keys[0] == "heart:3" && took.Recognised, took?.Keys[0]);
        Check("undo_pressed_again_within_guard_counts_once", undo.Take(19, u0.AddSeconds(20.5), out rep) == null && rep && undo.Count == 2, "");
        took = undo.Take(19, u0.AddSeconds(23), out rep);
        Check("undo_after_guard_takes_next_entry_of_current_map", took != null && took.Keys[0] == "wp:1" && undo.Count == 1, took?.Keys[0]);
        Check("undo_skips_entries_of_other_maps", undo.Take(19, u0.AddSeconds(26), out rep) == null && undo.Count == 1, "");
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1", "wp:2" }, true, u0);
        undo.Forget(19, "wp:1");
        took = undo.Take(19, u0.AddSeconds(5), out rep);
        Check("forgotten_point_is_not_unticked_again", took != null && took.Keys.Count == 1 && took.Keys[0] == "wp:2", "");
        undo.Clear();
        for (int i = 0; i < UndoHistory.MAX_ENTRIES + 5; i++) undo.Push(19, new List<string> { "p:" + i }, false, u0);
        Check("undo_history_keeps_at_most_fifty_entries", undo.Count == UndoHistory.MAX_ENTRIES, undo.Count.ToString());
        undo.Clear();
        for (int i = 0; i < 10; i++) undo.Push(19, new List<string> { "p:" + i }, true, u0);
        int taken = 0;
        for (double s = 1; s <= 11; s += 0.3) { if (undo.Take(19, u0.AddSeconds(s), out rep) != null) taken++; }
        Check("undo_steady_burst_every_0_3_seconds_for_10_seconds_undoes_one_step", taken == 1, taken.ToString());
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1" }, false, u0);
        Check("undo_one_minute_59_later_still_works", undo.Take(19, u0.AddSeconds(119), out rep) != null, "");
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1" }, false, u0);
        Check("undo_two_minutes_1_second_later_unticks_nothing", undo.Take(19, u0.AddSeconds(121), out rep) == null && undo.Count == 0, "");
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1" }, false, u0);
        Check("undo_after_clock_goes_back_unticks_nothing", undo.Take(19, u0.AddMinutes(-30), out rep) == null && !rep && undo.Count == 0, "");
        undo.Clear();
        undo.Push(19, new List<string> { "wp:1" }, false, u0);
        undo.Push(32, new List<string> { "wp:2" }, false, u0);
        undo.ClearMap(19);
        took = undo.Count == 1 ? undo.Take(32, u0.AddSeconds(1), out rep) : null;
        Check("reset_map_drops_only_that_maps_undo_entries", took != null && took.Keys[0] == "wp:2", "");
        undo.Push(19, new List<string> { "wp:3" }, false, u0.AddSeconds(1));
        undo.Clear();
        undo.Push(19, new List<string> { "wp:4" }, false, u0.AddSeconds(1.5));
        Check("clearing_undo_history_resets_the_repeat_guard", undo.Take(19, u0.AddSeconds(1.6), out rep) != null, "");

        // World map views cover the neighbouring maps too
        Check("map_index_holds_the_public_tyria_maps", MapIndex.Maps.Count >= 70 && MapIndex.Contains(32) && MapIndex.Contains(19) && MapIndex.Contains(218), $"{MapIndex.Maps.Count} maps");
        Check("map_index_leaves_out_wvw_and_story_instances", !MapIndex.Contains(38) && !MapIndex.Contains(37), "");
        var diessa = MapIndex.Maps.First(m => m.Id == 32);
        var around = MapIndex.InView(diessa.Left - 100, diessa.Top - 100, diessa.Right + 100, diessa.Bottom + 100);
        Check("view_past_the_edge_of_diessa_plateau_includes_plains_of_ashford_and_black_citadel", around.Contains(32) && around.Contains(19) && around.Contains(218) && !around.Contains(15), string.Join(",", around));
        var inner = MapIndex.InView(diessa.Left + 500, diessa.Top + 500, diessa.Left + 600, diessa.Top + 600);
        Check("view_inside_diessa_plateau_includes_only_diessa_plateau", inner.Count == 1 && inner[0] == 32, string.Join(",", inner));

        var here  = new MapCompletionData(32, "Here", 0.03, new List<CompletionPoint> {
            new CompletionPoint("wp:1", PointKind.Waypoint, "a", 1000, 1000),
            new CompletionPoint("poi:9", PointKind.PointOfInterest, "shared", 1100, 1000) });
        var there = new MapCompletionData(19, "There", 0.03, new List<CompletionPoint> {
            new CompletionPoint("wp:2", PointKind.Waypoint, "b", 1200, 1000),
            new CompletionPoint("poi:9", PointKind.PointOfInterest, "shared", 1100, 1000),
            new CompletionPoint("wp:3", PointKind.Waypoint, "far", 90000, 1000),
            new CompletionPoint("jp:x", PointKind.Puzzle, "puzzle", 1150, 1000) });
        var built = NaxCompletionModule.BuildMarkers(new[] { here, there }, p => true, (m, k) => m == 19 && k == "wp:2",
                                                     1100, 1000, 1, new Point(1000, 800));
        var wp2 = built.FirstOrDefault(m => m.Point.Key == "wp:2");
        Check("neighbouring_map_point_on_screen_gets_a_frame_of_its_own_map", wp2 != null && wp2.MapId == 19 && wp2.Done && wp2.Position.X == 600 && wp2.Position.Y == 400, wp2 == null ? "missing" : $"map {wp2.MapId} at {wp2.Position.X},{wp2.Position.Y}");
        Check("point_shared_by_two_maps_gets_one_frame_owned_by_the_current_map", built.Count(m => m.Point.Key == "poi:9") == 1 && built.First(m => m.Point.Key == "poi:9").MapId == 32, "");
        Check("neighbouring_map_point_off_screen_and_puzzles_get_no_frame", built.All(m => m.Point.Key != "wp:3" && m.Point.Key != "jp:x") && built.Count == 3, $"{built.Count} frames");
        var untracked = NaxCompletionModule.BuildMarkers(new[] { here, there }, p => p.Kind != PointKind.Waypoint, (m, k) => false,
                                                         1100, 1000, 1, new Point(1000, 800));
        Check("untracked_kind_on_a_neighbouring_map_gets_no_frame", untracked.Count == 1 && untracked[0].Point.Key == "poi:9", $"{untracked.Count} frames");

        RecognitionLifecycleTests();
        RouteLifecycleTests();
        RouteGeometryTests();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }
}
