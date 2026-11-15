using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Nax.Completion {

    [Export(typeof(Blish_HUD.Modules.Module))]
    public class NaxCompletionModule : Blish_HUD.Modules.Module {

        private static readonly Logger Logger = Logger.GetLogger<NaxCompletionModule>();

        private const string DATA_DIRECTORY = "nax-completion";

        // Logic runs ten times a second; the panel itself still redraws every frame.
        private const double LOGIC_INTERVAL_MS = 100;

        // A failed API load is retried after this long while you stay on the map.
        private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(30);

        // An undone auto-tick is not re-ticked until you have walked this many radii away from it.
        private const double SUPPRESS_RELEASE_FACTOR = 1.5;

        // The arrow only moves to another point that is at most 70% as far away and at least 25 m closer than the target.
        private const double REPICK_RATIO      = 0.7;
        private const double REPICK_MIN_METERS = 25;

        // A hotkey-ticked target is never swapped while you are this close to it.
        private const double HOLD_METERS       = 50;
        private const double HEART_HOLD_METERS = 150;

        private const int MAX_TITLE_LEN = 32;

        internal DirectoriesManager DirectoriesManager => this.ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager      Gw2ApiManager      => this.ModuleParameters.Gw2ApiManager;

        #region Settings

        private SettingEntry<bool>       _showPanel;
        private SettingEntry<bool>       _showMapFrames;
        private SettingEntry<FrameClickKey> _frameClickKey;
        private SettingEntry<bool>       _recognizeDone;
        private SettingEntry<bool>       _lockPanel;
        private SettingEntry<int>        _autoTickRadius;
        private SettingEntry<bool>       _trackWaypoints;
        private SettingEntry<bool>       _trackPois;
        private SettingEntry<bool>       _trackVistas;
        private SettingEntry<bool>       _trackHearts;
        private SettingEntry<bool>       _trackHeroChallenges;
        private SettingEntry<bool>       _trackPuzzles;
        private SettingEntry<KeyBinding> _markDoneKey;
        private SettingEntry<KeyBinding> _nextTargetKey;
        private SettingEntry<KeyBinding> _undoKey;
        private SettingEntry<KeyBinding> _markMapDoneKey;
        private SettingEntry<KeyBinding> _resetMapKey;
        private SettingEntry<KeyBinding> _addStepKey;
        private SettingEntry<KeyBinding> _finishRouteKey;
        private SettingEntry<KeyBinding> _noteKey;
        private SettingEntry<KeyBinding> _cycleKindKey;
        private SettingEntry<int>        _panelX;
        private SettingEntry<int>        _panelY;

        #endregion

        // Pathing uses the same factor: Mumble's map scale is calibrated for the game's "Normal" UI size.
        private const double MAP_SCALE_FACTOR = 0.897;

        private static readonly TimeSpan MAP_SETTLE_TIME = TimeSpan.FromMilliseconds(700);

        // Blish's IsInGame only turns false after 0.5 s without a Mumble update, so frames flashed for half a second
        // at the start of every loading screen. The focused game updates Mumble every frame, so once no update has
        // come for this long, frames and panel are no longer drawn. This only hides them: everything that has state
        // (the map settle delay, recognition, the note box) still waits for Blish's own flag, so a short stutter costs
        // nothing but a flicker. Time is measured with a real clock, because after a stall of its own Blish runs many
        // updates back to back and its game time would make the game look frozen.
        private const long MUMBLE_STALE_MS = 200;

        private readonly Stopwatch _sinceMumbleTick = Stopwatch.StartNew();
        private int                _lastMumbleTick  = -1;

        /// <summary>
        /// True while the focused game has not updated Mumble for a moment, typically at the start of a loading
        /// screen. An unfocused game may run at a few frames per second, so it never counts as frozen. Set once per
        /// module update, so hotkey handlers (which Blish runs earlier in the frame) see the previous frame's value.
        /// </summary>
        private bool _mumbleFrozen;
        private bool _mumbleTicked;   // the game delivered a new frame this update

        // A double click (the habit from teleporting) would switch a frame off and straight back on, so a click on
        // the same frame within this time of the previous one is ignored.
        private static readonly TimeSpan REPEAT_CLICK_WINDOW = TimeSpan.FromMilliseconds(500);

        private string   _lastFrameClickKey;
        private DateTime _lastFrameClickUtc = DateTime.MinValue;

        private bool     _mapWasOpen;
        private DateTime _mapOpenedUtc = DateTime.MinValue;
        private bool     _wasInGame;

        private ArrowPanel    _panel;
        private MapOverlay    _mapOverlay;
        private ProgressStore _store;
        private RouteStore    _routes;
        private HeroPointSync _heroPoints;
        private AchievementSync _achievements;

        // Compass (minimap) recognition, and the in-world drawing of puzzle routes.
        private readonly MinimapRecognizer _minimap      = new MinimapRecognizer();
        private IRoutePath                 _routePath;
        private readonly StepTypeDetector  _stepDetector = new StepTypeDetector();
        private DateTime                   _lastSampleUtc = DateTime.MinValue;
        private MapDataLoader _loader;

        // Written by the background loader, read by the update loop.
        private volatile MapCompletionData _mapData;
        private volatile string            _loadError;
        private volatile int               _loadToken;
        private          int               _loadingMapId = -1;
        private          DateTime          _lastLoadStartUtc = DateTime.MinValue;

        // The other maps inside the world map view, so their icons are read too. Written by the background loader,
        // read by the update loop. A failed load is tried again after NEARBY_RETRY_DELAY.
        private const    int                                               NEARBY_MAX_LOADS    = 2;
        private static readonly TimeSpan                                   NEARBY_RETRY_DELAY  = TimeSpan.FromMinutes(1);
        private readonly ConcurrentDictionary<int, MapCompletionData>      _nearbyData         = new ConcurrentDictionary<int, MapCompletionData>();
        private readonly ConcurrentDictionary<int, DateTime>               _nearbyFailedUtc    = new ConcurrentDictionary<int, DateTime>();
        private readonly HashSet<int>                                      _nearbyLoading      = new HashSet<int>();
        private readonly object                                            _nearbyLock         = new object();

        // What the world map read in flight was built from, per map, and the last line logged about it.
        private MapReadLog _mapReadLog;
        private string     _loggedMapRead;

        private CompletionPoint _target;
        private string          _lastSkippedKey;

        private readonly HashSet<string>             _skipped       = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string>             _suppressAuto  = new HashSet<string>(StringComparer.Ordinal);
        private readonly MapRecognizer               _recognizer    = new MapRecognizer();

        // Jumping puzzle routes: the one being followed, and the one being recorded (with the step whose note is typed).
        private readonly PuzzleGuide _guide = new PuzzleGuide();
        private PuzzleRoute          _recording;
        private RouteStep            _noteStep;
        private TextBox              _noteBox;
        private bool                 _releaseNoteFocus;

        // Only this kind is offered as a target while set (the hotkey cycles through the kinds); null means all kinds.
        private PointKind?           _kindFilter;
        private readonly UndoHistory _undo = new UndoHistory();
        private DateTime             _lastStepUndoUtc = DateTime.MinValue;

        private bool   _savingWarningShown;
        private double _sinceLogic = LOGIC_INTERVAL_MS;

        [ImportingConstructor]
        public NaxCompletionModule([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) { }

        private IEnumerable<SettingEntry<KeyBinding>> AllKeys =>
            new[] { _markDoneKey, _nextTargetKey, _undoKey, _markMapDoneKey, _resetMapKey, _addStepKey, _finishRouteKey, _noteKey, _cycleKindKey }.Where(k => k != null);

        protected override void DefineSettings(SettingCollection settings) {
            _showPanel = settings.DefineSetting("ShowPanel", true,
                                                () => "Show arrow panel",
                                                () => "Show the direction arrow and target info. Automatic completion tracking continues while it is hidden.");

            _showMapFrames = settings.DefineSetting("ShowMapFrames", false,
                                                     () => "Show frames on the world map",
                                                     () => "While the world map is open, frames every tracked point: orange = open, green = ticked. Hold the frame click key and click a frame to switch it. Use this to match the module to what the game shows.");

            _frameClickKey = settings.DefineSetting("FrameClickKey", FrameClickKey.Alt,
                                                    () => "Frame click key",
                                                    () => "Hold this key alone to click frames on the world map. A frame turns white when a click will switch it. Without the key, clicks go to the game, so waypoints and dialogs under a frame still work. Alt is the default because Alt+click on the map only places the game's personal marker, which you then cannot place on a frame.");

            _lockPanel = settings.DefineSetting("LockPanel", true,
                                                () => "Lock panel position",
                                                () => "The panel can always be dragged while you hold Ctrl. Untick this to drag it without Ctrl; tick it again to make it click-through.");

            _autoTickRadius = settings.DefineSetting("AutoTickRadius", 15,
                                                     () => "Auto-tick radius (m)",
                                                     () => "Waypoints and points of interest within this distance are ticked off automatically. Height is not known, so keep it small.");
            _autoTickRadius.SetRange(5, 60);

            _recognizeDone = settings.DefineSetting("RecognizeDone", true,
                                                    () => "Recognise done points on the world map",
                                                    () => "Reads the game's own icons on the compass while you play, and on the world map while it is open and still, and ticks waypoints, points of interest, hearts, vistas and hero challenges the game shows as done. On the world map this covers every Tyria map in view, so panning over other maps (zoomed in far enough to see their icons) ticks their points too. It never unticks anything. Undo works for the map you are on.");

            _trackWaypoints      = settings.DefineSetting("TrackWaypoints",      true, () => "Track waypoints",          () => "Include waypoints.");
            _trackPois           = settings.DefineSetting("TrackPois",           true, () => "Track points of interest", () => "Include points of interest.");
            _trackVistas         = settings.DefineSetting("TrackVistas",         true, () => "Track vistas",             () => "Include vistas (tick with the hotkey after viewing).");
            _trackHearts         = settings.DefineSetting("TrackHearts",         true, () => "Track hearts",             () => "Include renown hearts (tick with the hotkey when done).");
            _trackHeroChallenges = settings.DefineSetting("TrackHeroChallenges", true, () => "Track hero challenges",    () => "Include hero challenges. They tick themselves once the compass or the world map shows them done.");
            _trackPuzzles        = settings.DefineSetting("TrackPuzzles",        true, () => "Track jumping puzzles",    () => "Include jumping puzzle guidance and automatic completion from followed routes and account achievements.");

            _markDoneKey    = settings.DefineSetting("MarkDoneKey",    new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.K),
                                                     () => "Mark target done", () => "Ticks off the point the arrow is pointing at.");
            _nextTargetKey  = settings.DefineSetting("NextTargetKey",  new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.L),
                                                     () => "Skip to next target", () => "Points the arrow at the next nearest point instead.");
            _undoKey        = settings.DefineSetting("UndoKey",        new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.J),
                                                     () => "Undo last tick", () => "Unticks whatever was ticked off last (by hotkey or automatically).");
            // Renamed from "MarkMapDoneKey", whose saved value still held Ctrl+Shift+M from an early default, so one stray
            // keypress could tick a whole map. The old saved binding is ignored.
            _markMapDoneKey = settings.DefineSetting("MarkWholeMapDoneKey", new KeyBinding(Keys.None),
                                                     () => "Mark whole map done", () => "For maps this character already finished. Unbound by default.");
            _resetMapKey    = settings.DefineSetting("ResetMapKey",    new KeyBinding(Keys.None),
                                                     () => "Reset this map", () => "Unticks every point on the current map. Unbound by default.");
            _addStepKey     = settings.DefineSetting("AddRouteStepKey", new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.P),
                                                     () => "Puzzle route: add step", () => "Records where you stand as the next step of a jumping puzzle route (the first press starts a route for the nearest puzzle). You can type a short note for the step.");
            _finishRouteKey = settings.DefineSetting("FinishRouteKey", new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.O),
                                                     () => "Puzzle route: save", () => "Saves the route you are recording. Next time, the panel guides you through the puzzle step by step.");
            _noteKey        = settings.DefineSetting("RouteNoteKey", new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.N),
                                                     () => "Puzzle route: note", () => "While recording, opens a box to type a short note for the last step (for example \"jump left onto the beam\"). Enter or Escape closes it.");
            _cycleKindKey   = settings.DefineSetting("CycleKindKey", new KeyBinding(ModifierKeys.Ctrl | ModifierKeys.Shift, Keys.U),
                                                     () => "Show only one kind", () => "Cycles the arrow through all kinds, then only waypoints, points of interest, vistas, hearts, hero challenges or jumping puzzles, and back to all.");

            _panelX = settings.DefineSetting("PanelX", -1,  () => "Panel X", () => "Set automatically when you drag the panel. -1 means centered.");
            _panelY = settings.DefineSetting("PanelY", 140, () => "Panel Y", () => "Set automatically when you drag the panel.");
        }

        protected override void Initialize() {
            _store  = new ProgressStore(this.DirectoriesManager.GetFullDirectoryPath(DATA_DIRECTORY));
            _loader = new MapDataLoader(() => this.Gw2ApiManager.Gw2ApiClient);
            _routes = new RouteStore(Path.Combine(this.DirectoriesManager.GetFullDirectoryPath(DATA_DIRECTORY), "routes"));
            _routes.LoadBuiltIn();
            _routes.LoadAll();
            _heroPoints = new HeroPointSync(this.Gw2ApiManager);
            _achievements = new AchievementSync(this.Gw2ApiManager);
        }

        protected override Task LoadAsync() {
            return Task.CompletedTask;
        }

        protected override void OnModuleLoaded(EventArgs e) {
            _panel = new ArrowPanel {
                Parent  = GameService.Graphics.SpriteScreen,
                Visible = false,
                Locked  = _lockPanel.Value
            };
            PlacePanel();

            _mapOverlay = new MapOverlay {
                Parent   = GameService.Graphics.SpriteScreen,
                Location = Point.Zero,
                Size     = GameService.Graphics.SpriteScreen.Size,
                Visible  = false
            };
            GameService.Input.Mouse.LeftMouseButtonPressed += OnMouseLeftPressed;

            _panel.DragFinished       += OnPanelDragFinished;
            _lockPanel.SettingChanged += OnLockChanged;
            _panelX.SettingChanged    += OnPanelPositionSettingChanged;
            _panelY.SettingChanged    += OnPanelPositionSettingChanged;

            GameService.Graphics.SpriteScreen.Resized += OnScreenResized;

            foreach (var key in this.AllKeys) {
                // Keep GW2 from also acting on the chord (e.g. Ctrl+Shift+K reaching a game keybind).
                key.Value.BlockSequenceFromGw2 = true;
                key.Value.Enabled              = true;
            }

            _markDoneKey.Value.Activated    += OnMarkDone;
            _nextTargetKey.Value.Activated  += OnNextTarget;
            _undoKey.Value.Activated        += OnUndo;
            _markMapDoneKey.Value.Activated += OnMarkMapDone;
            _resetMapKey.Value.Activated    += OnResetMap;
            _addStepKey.Value.Activated     += OnAddRouteStep;
            _finishRouteKey.Value.Activated += OnFinishRoute;
            _noteKey.Value.Activated        += OnRouteNote;
            _cycleKindKey.Value.Activated   += OnCycleKind;

            _routePath = World.RoutePathFactory.Create();

            _noteBox = new TextBox {
                Parent  = GameService.Graphics.SpriteScreen,
                Visible = false,
                Width     = ArrowPanel.PANEL_WIDTH,
                // About what fits on one line of the panel.
                MaxLength = 40
            };
            _noteBox.EnterPressed       += OnNoteEntered;
            _noteBox.InputFocusChanged  += OnNoteFocusChanged;

            GameService.Gw2Mumble.PlayerCharacter.NameChanged += OnCharacterChanged;
            GameService.Gw2Mumble.CurrentMap.MapChanged       += OnMapChanged;

            if (GameService.Gw2Mumble.IsAvailable) {
                LoadCharacter(GameService.Gw2Mumble.PlayerCharacter.Name);
                BeginLoadMap(GameService.Gw2Mumble.CurrentMap.Id);
            }

            base.OnModuleLoaded(e);
        }

        #region Events

        private void OnCharacterChanged(object sender, ValueEventArgs<string> e) {
            AbandonRecording("You switched character");
            _guide.Reset();
            LoadCharacter(e.Value);
        }

        private void OnMapChanged(object sender, ValueEventArgs<int> e) {
            AbandonRecording("You left the map");
            _guide.Reset();
            _minimap.Reset();
            _recognizer.Reset();
            _skipped.Clear();
            _suppressAuto.Clear();
            _lastSkippedKey = null;
            _target         = null;
            _heroPoints.RequestSoon();
            _achievements.RequestSoon();
            BeginLoadMap(e.Value);
        }

        private void OnLockChanged(object sender, ValueChangedEventArgs<bool> e) {
            if (_panel != null) _panel.Locked = e.NewValue;
        }

        private void OnPanelDragFinished(object sender, EventArgs e) {
            _panelX.Value = _panel.Location.X;
            _panelY.Value = _panel.Location.Y;
        }

        private void OnPanelPositionSettingChanged(object sender, ValueChangedEventArgs<int> e) {
            PlacePanel();
        }

        private void OnScreenResized(object sender, ResizedEventArgs e) {
            PlacePanel();
            if (_mapOverlay != null) _mapOverlay.Size = GameService.Graphics.SpriteScreen.Size;
        }

        /// <summary>
        /// Raised by Blish for every left press it saw. OwnedHover is only set when the press was kept from the game
        /// (see MapOverlay for the full reasoning), so this never toggles a frame for a click the game also handled.
        /// </summary>
        private void OnMouseLeftPressed(object sender, MouseEventArgs e) {
            var hit = _mapOverlay?.OwnedHover;
            if (hit == null) return;

            if (CurrentData() == null) return;

            // The frame may belong to another map in the view, so its own map is switched.
            int      mapId = hit.MapId;
            string   key   = hit.Point.Key;
            DateTime now = DateTime.UtcNow;

            bool repeat = IsRepeatClick(_lastFrameClickKey, _lastFrameClickUtc, key, now);
            _lastFrameClickKey = key;
            _lastFrameClickUtc = now;   // measured from the latest click, so a fast triple click also switches once
            if (repeat) return;

            if (_store.IsDone(mapId, key)) {
                _store.SetDone(mapId, key, false);
                // Stay unticked even if you are standing right next to it or the map icon looks filled.
                _suppressAuto.Add(key);
                _store.KeepOpen(new[] { key });
                // Drop it from the undo history so a later undo does not "untick" something already open.
                _undo.Forget(mapId, key);
            } else {
                _store.SetDone(mapId, key, true);
                _store.ReleaseKeepOpen(new[] { key });
                PushUndo(mapId, new List<string> { key });
            }

            _target = null;
        }

        internal static bool IsRepeatClick(string lastKey, DateTime lastUtc, string key, DateTime nowUtc) {
            return key == lastKey && nowUtc - lastUtc < REPEAT_CLICK_WINDOW;
        }

        private void OnAddRouteStep(object sender, EventArgs e) {
            var mumble = GameService.Gw2Mumble;
            if (!mumble.IsAvailable || !GameService.GameIntegration.Gw2Instance.IsInGame || _noteBox.Focused) return;

            int mapId = mumble.CurrentMap.Id;
            var pos   = mumble.PlayerCharacter.Position;
            var cont  = mumble.UI.MapPosition;

            if (_recording == null) {
                var puzzle = PuzzlesByDistance(mapId, cont.X, cont.Y).FirstOrDefault();
                if (puzzle == null) {
                    Notify("No jumping puzzle is known on this map.");
                    return;
                }

                _recording = new PuzzleRoute { Key = puzzle.Key, Puzzle = puzzle.Name, MapId = mapId, StartX = cont.X, StartY = cont.Y };
                _guide.Reset();
                Notify($"Recording a route for {puzzle.Name}. {_nextTargetKey.Value.GetBindingDisplayText()} picks another puzzle.");
            }

            if (_recording.Steps.Count == 0) {
                // The first step is where the arrow leads to, also after undoing every step and starting again.
                _recording.StartX = cont.X;
                _recording.StartY = cont.Y;
            }

            var step = new RouteStep { X = pos.X, Y = pos.Y, Z = pos.Z };
            if (_recording.Steps.Count > 0) step.Type = _stepDetector.Classify();
            _recording.Steps.Add(step);
            _stepDetector.Reset(pos.X, pos.Y, pos.Z);
            RunLogic();
        }

        /// <summary>
        /// While recording, feeds the movement to the step type detector (jump, running jump, glide, mount). One
        /// sample per game frame: the position only changes when Mumble ticks, so a Blish frame without a tick would
        /// read as standing still and the next one as twice the speed.
        /// </summary>
        private void SampleMovement() {
            if (_recording == null || _recording.Steps.Count == 0 || !_mumbleTicked) return;

            var mumble = GameService.Gw2Mumble;
            if (!mumble.IsAvailable) return;

            var now = DateTime.UtcNow;
            if (_lastSampleUtc != DateTime.MinValue) {
                float seconds = (float)(now - _lastSampleUtc).TotalSeconds;
                var   pos     = mumble.PlayerCharacter.Position;
                bool  mounted = mumble.PlayerCharacter.CurrentMount != Gw2Sharp.Models.MountType.None;
                if (seconds > 0 && seconds < 1) _stepDetector.Sample(pos.X, pos.Y, pos.Z, seconds, mounted);
            }
            _lastSampleUtc = now;
        }

        /// <summary>
        /// Reads the compass icons every couple of seconds while you play, so hearts, waypoints and points of interest
        /// the game shows as done tick themselves without the world map.
        /// </summary>
        private void UpdateMinimap() {
            var data = CurrentData();
            var mumble = GameService.Gw2Mumble;
            if (data == null || !_recognizeDone.Value || !mumble.IsAvailable || mumble.UI.IsMapOpen) {
                _minimap.Reset();
                return;
            }

            bool readable = GameService.GameIntegration.Gw2Instance.IsInGame && !_mumbleFrozen;
            // A read that started just before a loading screen would have copied the loading screen.
            if (!readable) _minimap.Invalidate();

            bool mayStart = readable
                         && GameService.GameIntegration.Gw2Instance.Gw2HasFocus
                         && !GameService.Overlay.InterfaceHidden;

            float uiScale = GameService.Graphics.UIScaleMultiplier;
            var found = _minimap.Update(data.MapId, GameService.Gw2Mumble.Tick, mayStart, () => BuildMinimapJobs(data, uiScale), uiScale);
            if (found != null) ApplyRecognised(found.ToLookup(k => data.MapId), "compass");
        }

        private List<MinimapRecognizer.Job> BuildMinimapJobs(MapCompletionData data, float uiScale) {
            var mumble = GameService.Gw2Mumble;
            var size   = mumble.UI.CompassSize;
            // The rotation direction is not verified yet, so a rotating compass is not read.
            if (uiScale <= 0 || size.Width <= 0 || size.Height <= 0 || mumble.UI.IsCompassRotationEnabled
             || !ScreenCapture.TryGetClientSize(out int clientW, out int clientH)) {
                return null;
            }

            int bar      = (int)Math.Round(MinimapRecognizer.BOTTOM_BAR_PX * uiScale / IconRecognizer.CALIBRATION_UI_SCALE);
            int left     = clientW - size.Width;
            int top      = mumble.UI.IsCompassTopRight ? 0 : clientH - size.Height - bar;
            var centre   = mumble.UI.MapCenter;
            var player   = mumble.UI.MapPosition;
            double scale = mumble.UI.MapScale;

            var jobs = new List<MinimapRecognizer.Job>();
            foreach (var point in data.Points) {
                if (!IsTracked(point) || !IconRecognizer.CanRecognise(point.Kind) || !point.OnDefaultFloor
                 || _store.IsDone(data.MapId, point.Key) || _store.IsKeptOpen(point.Key)) {
                    continue;
                }

                if (MinimapRecognizer.TryProject(point.X, point.Y, centre.X, centre.Y, player.X, player.Y, scale, 0,
                                                 left, top, size.Width, size.Height, uiScale, out int x, out int y)) {
                    jobs.Add(new MinimapRecognizer.Job { Key = point.Key, Kind = point.Kind, ScreenX = x, ScreenY = y });
                }
            }

            return jobs;
        }

        /// <summary>
        /// Puzzles of the map, nearest first, measured to their waypoint. Several puzzles share a waypoint, so while
        /// recording the skip hotkey walks through this list.
        /// </summary>
        private static List<PuzzleInfo> PuzzlesByDistance(int mapId, double x, double y) {
            return PuzzleCatalog.ForMap(mapId).OrderBy(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)).ToList();
        }

        private void OnRouteNote(object sender, EventArgs e) {
            if (_recording == null || _recording.Steps.Count == 0 || _noteBox.Focused) return;
            AskForNote(_recording.Steps[_recording.Steps.Count - 1], _recording.Steps.Count);
        }

        private void OnCycleKind(object sender, EventArgs e) {
            if (_recording != null) {
                if (_recording.Steps.Count > 1) {
                    var last  = _recording.Steps[_recording.Steps.Count - 1];
                    last.Type = StepTypeDetector.Next(last.Type);
                    Notify($"Step {_recording.Steps.Count}: {last.TypeLabel}.");
                    RunLogic();
                } else {
                    Notify("The first step has no way to it. Record a second step first.");
                }
                return;
            }

            _kindFilter = NextKindFilter(_kindFilter, IsTrackedKind);
            _target     = null;
            Notify(_kindFilter == null ? "Arrow: all kinds." : $"Arrow: only {KindPlural(_kindFilter.Value)}.");
            RunLogic();
        }

        /// <summary>The next kind in the cycle all, waypoints, POIs, vistas, hearts, hero challenges, puzzles, all.</summary>
        internal static PointKind? NextKindFilter(PointKind? current, Func<PointKind, bool> tracked) {
            var cycle = new[] { PointKind.Waypoint, PointKind.PointOfInterest, PointKind.Vista, PointKind.Heart, PointKind.HeroChallenge, PointKind.Puzzle };
            int start = current == null ? 0 : Array.IndexOf(cycle, current.Value) + 1;
            for (int i = start; i < cycle.Length; i++) {
                if (tracked(cycle[i])) return cycle[i];
            }

            return null;
        }

        private static string KindPlural(PointKind kind) {
            switch (kind) {
                case PointKind.Waypoint:        return "waypoints";
                case PointKind.PointOfInterest: return "points of interest";
                case PointKind.Vista:           return "vistas";
                case PointKind.Heart:           return "hearts";
                case PointKind.HeroChallenge:   return "hero challenges";
                case PointKind.Puzzle:          return "jumping puzzles";
                default:                        return "points";
            }
        }

        private void OnFinishRoute(object sender, EventArgs e) {
            if (_recording == null) return;

            SaveNote();
            if (_recording.Steps.Count < 2) {
                Notify("A route needs at least two steps. Nothing was saved.");
            } else if (_routes.Save(_recording)) {
                Notify($"Saved the route for {_recording.Puzzle} with {_recording.Steps.Count} steps.");
            } else {
                Notify($"The route for {_recording.Puzzle} could not be written to disk. It works until Blish HUD restarts.");
            }

            // The map data holds each puzzle's start, so rebuild it to point the arrow at the new start right away.
            var data = _mapData;
            if (data != null && data.MapId == _recording.MapId) _mapData = WithPuzzles(data);
            _target    = null;
            _recording     = null;
            _lastSampleUtc = DateTime.MinValue;
            RunLogic();
        }

        private void AbandonRecording(string reason) {
            if (_recording == null) return;

            SaveNote();
            Notify($"{reason}, so the route for {_recording.Puzzle} was not saved.");
            _recording     = null;
            _lastSampleUtc = DateTime.MinValue;
        }

        private void AskForNote(RouteStep step, int number) {
            SaveNote();
            _noteStep                = step;
            _noteBox.Text            = string.Empty;
            _noteBox.PlaceholderText = $"Note for step {number}, Enter keeps it";
            // Below the panel, or above it when the panel sits at the bottom of the screen.
            int below = _panel.Location.Y + _panel.Height + 4;
            _noteBox.Location = new Point(_panel.Location.X,
                                          below + _noteBox.Height <= GameService.Graphics.SpriteScreen.Size.Y
                                              ? below
                                              : Math.Max(0, _panel.Location.Y - _noteBox.Height - 4));
            _noteBox.Visible         = true;
            _noteBox.Focused         = true;
        }

        /// <summary>Stores what was typed for the current step and hides the note box.</summary>
        private void SaveNote() {
            if (_noteStep != null) _noteStep.Note = (_noteBox.Text ?? string.Empty).Trim();
            _noteStep        = null;
            _noteBox.Visible = false;
            // UnsetFocus also clears Blish's focused control; setting Focused to false alone leaves the hidden box
            // focused, and Blish would then swallow the next Escape. Another control's focus is left alone.
            if (_noteBox.Focused || GameService.Input.Keyboard.FocusedControl == _noteBox) _noteBox.UnsetFocus();
        }

        private void OnNoteEntered(object sender, EventArgs e) {
            SaveNote();
        }

        private void OnNoteFocusChanged(object sender, ValueEventArgs<bool> e) {
            // Escape or clicking elsewhere also ends typing, and keeps whatever was typed.
            if (!e.Value && _noteStep != null) SaveNote();
            // After a click elsewhere Blish marks the box as focused again once this event returns, so clear that
            // on the next update.
            if (!e.Value) _releaseNoteFocus = true;
        }

        private void OnMarkDone(object sender, EventArgs e) {
            var data = CurrentData();
            if (data == null || !_panel.Visible || _recording != null) return;

            // Inside a route zone the panel shows the puzzle, so the key ticks the puzzle. Once it is ticked, the key
            // ticks the nearest open point within reach instead, for example a vista on top of the puzzle.
            var route = _guide.Route;
            if (route != null) {
                CompletionPoint zoneTarget = null;
                if (route.MapId == data.MapId && !_store.IsDone(data.MapId, route.Key)) {
                    zoneTarget = data.Points.FirstOrDefault(p => p.Key == route.Key);
                }

                if (zoneTarget == null) {
                    var    me      = GameService.Gw2Mumble.UI.MapPosition;
                    double reachSq = Math.Pow(HOLD_METERS / data.MetersPerUnit, 2);
                    zoneTarget = data.Points
                                     .Where(p => IsTracked(p) && p.Kind != PointKind.Puzzle && !_store.IsDone(data.MapId, p.Key)
                                              && DistanceSq(p, me.X, me.Y) <= reachSq)
                                     .OrderBy(p => DistanceSq(p, me.X, me.Y))
                                     .FirstOrDefault();
                }

                if (zoneTarget != null && _store.SetDone(data.MapId, zoneTarget.Key, true)) {
                    _store.ReleaseKeepOpen(new[] { zoneTarget.Key });
                    PushUndo(data.MapId, new List<string> { zoneTarget.Key });
                    Notify($"Done: {Describe(zoneTarget)}");
                }

                RunLogic();
                return;
            }

            if (_target == null) return;

            var target = _target;
            if (_store.SetDone(data.MapId, target.Key, true)) {
                _store.ReleaseKeepOpen(new[] { target.Key });
                PushUndo(data.MapId, new List<string> { target.Key });
                Notify($"Done: {Describe(target)}");
            }

            _target = null;
            RunLogic();
        }

        private void OnNextTarget(object sender, EventArgs e) {
            if (_recording != null) {
                // While recording, the key picks the next nearest puzzle for the route instead.
                var cont   = GameService.Gw2Mumble.UI.MapPosition;
                var list   = PuzzlesByDistance(_recording.MapId, cont.X, cont.Y);
                int index  = list.FindIndex(p => p.Key == _recording.Key);
                var puzzle = list[(index + 1) % list.Count];
                _recording.Key    = puzzle.Key;
                _recording.Puzzle = puzzle.Name;
                RunLogic();
                return;
            }

            // Inside a route zone the guide is shown, not a target.
            if (_guide.Route != null) return;

            if (CurrentData() == null || _target == null || !_panel.Visible) return;

            _skipped.Add(_target.Key);
            _lastSkippedKey = _target.Key;
            _target         = null;
            RunLogic();
        }

        private void OnUndo(object sender, EventArgs e) {
            if (_recording != null) {
                // While recording, undo removes the last step, and with no steps left it cancels the recording.
                // A burst of presses counts once, so a held key cannot wipe the whole route.
                var  nowUtc     = DateTime.UtcNow;
                bool stepRepeat = nowUtc - _lastStepUndoUtc < UndoHistory.REPEAT_GUARD && nowUtc >= _lastStepUndoUtc;
                _lastStepUndoUtc = nowUtc;
                if (stepRepeat) return;
                SaveNote();
                if (_recording.Steps.Count > 0) {
                    var removed = _recording.Steps[_recording.Steps.Count - 1];
                    _recording.Steps.RemoveAt(_recording.Steps.Count - 1);
                    // The way to the removed step still counts for the step you record in its place.
                    if (_recording.Steps.Count > 0) _stepDetector.Assume(removed.Type);
                    Notify(_recording.Steps.Count == 0 ? "Removed the step. Undo once more cancels the recording." : "Removed the last step.");
                } else {
                    Notify($"Cancelled the route for {_recording.Puzzle}.");
                    _recording     = null;
                    _lastSampleUtc = DateTime.MinValue;
                }
                RunLogic();
                return;
            }

            var data = CurrentData();
            if (data == null || !GameService.GameIntegration.Gw2Instance.IsInGame || _mumbleFrozen) return;

            var last = _undo.Take(data.MapId, DateTime.UtcNow, out bool repeat);
            if (repeat) return;
            if (last == null) {
                Notify("Nothing to undo. Undo only covers the last two minutes on this map.");
                return;
            }

            // The held keys show what fired the undo, for example a modifier whose key-up Blish missed.
            var keyboard = GameService.Input.Keyboard;
            Logger.Info("Undo on map {mapId} unticked {keys} (recognised {recognised}). Modifiers {mods}, keys down {down}.",
                        last.MapId, string.Join(" ", last.Keys), last.Recognised, keyboard.ActiveModifiers, string.Join("+", keyboard.KeysDown));

            // Undoing a recognised batch means the map icons were misread, so recognition leaves those points open.
            if (last.Recognised) _store.KeepOpen(last.Keys);

            foreach (string key in last.Keys) {
                _store.SetDone(last.MapId, key, false);
                // Without this, standing next to a wrongly auto-ticked point, or a misread map icon, would tick it
                // again right away.
                _suppressAuto.Add(key);
            }

            // Name what was unticked, so an accidental undo is noticed. For a recognised batch, also say that
            // recognition now leaves those points open until they are ticked by hand.
            string names = string.Join(", ", last.Keys.Take(3).Select(k => {
                var point = data.Points.FirstOrDefault(p => p.Key == k);
                return point != null ? Describe(point) : k;
            })) + (last.Keys.Count > 3 ? $" and {last.Keys.Count - 3} more" : "");
            string which = last.Keys.Count == 1 ? "this" : "these";
            Notify(last.Recognised ? $"Unticked {names}. Recognition leaves {which} open now." : $"Unticked {names}.");
            _target = null;
            RunLogic();
        }

        private void OnMarkMapDone(object sender, EventArgs e) {
            var data = CurrentData();
            // During a loading screen Mumble still reports the old map, so this would act on the map you are leaving.
            if (data == null || !GameService.GameIntegration.Gw2Instance.IsInGame || _mumbleFrozen) return;

            var keys = data.Points
                           .Where(p => IsTracked(p) && p.Kind != PointKind.Puzzle && !_store.IsDone(data.MapId, p.Key))
                           .Select(p => p.Key)
                           .ToList();

            if (keys.Count == 0) return;

            _store.SetManyDone(data.MapId, keys);
            _store.ReleaseKeepOpen(keys);
            PushUndo(data.MapId, keys);
            Notify($"Marked {keys.Count} points on {data.MapName} as done.");
            _target = null;
            RunLogic();
        }

        private void OnResetMap(object sender, EventArgs e) {
            var data = CurrentData();
            if (data == null || !GameService.GameIntegration.Gw2Instance.IsInGame || _mumbleFrozen) return;

            int cleared = _store.ClearMap(data.MapId);
            // A fresh start for this map: recognition may tick everything the game shows as done again.
            _store.ReleaseKeepOpen(data.Points.Select(p => p.Key));
            _undo.ClearMap(data.MapId);
            _skipped.Clear();
            _suppressAuto.Clear();
            _lastSkippedKey = null;
            Notify($"Reset {data.MapName}: {cleared} points unticked.");
            _target = null;
            RunLogic();
        }

        #endregion

        protected override void Update(GameTime gameTime) {
            if (_panel == null) return;

            _panel.UpdateDrag();

            if (_releaseNoteFocus) {
                _releaseNoteFocus = false;
                if (_noteBox != null && !_noteBox.Focused && GameService.Input.Keyboard.FocusedControl == _noteBox) _noteBox.UnsetFocus();
            }
            int tick = GameService.Gw2Mumble.Tick;
            _mumbleTicked = tick != _lastMumbleTick;
            if (_mumbleTicked) {
                _lastMumbleTick = tick;
                _sinceMumbleTick.Restart();
            }

            _mumbleFrozen = GameService.GameIntegration.Gw2Instance.Gw2HasFocus && _sinceMumbleTick.ElapsedMilliseconds > MUMBLE_STALE_MS;

            UpdateMapOverlay();   // every frame, so the frames follow map dragging and zooming smoothly
            ScreenCapture.GameWindow = GameService.GameIntegration.Gw2Instance.Gw2WindowHandle;
            _heroPoints.Update();
            _achievements.Update();
            SampleMovement();
            UpdateMinimap();
            if (_heroPoints.Changed || _achievements.Changed) {
                _heroPoints.Changed   = false;
                _achievements.Changed = false;
                _target = null;
                _sinceLogic = LOGIC_INTERVAL_MS;
            }

            // Run the logic right away when a loading screen starts or ends, so the panel does not linger on it.
            bool inGame = GameService.GameIntegration.Gw2Instance.IsInGame && !_mumbleFrozen;
            if (inGame != _wasInGame) {
                _wasInGame  = inGame;
                _sinceLogic = LOGIC_INTERVAL_MS;
            }

            _sinceLogic += gameTime.ElapsedGameTime.TotalMilliseconds;
            if (_sinceLogic < LOGIC_INTERVAL_MS) return;
            _sinceLogic = 0;

            RunLogic();
        }

        private void RunLogic() {
            if (_panel == null) return;

            var mumble = GameService.Gw2Mumble;

            if (!mumble.IsAvailable) {
                _panel.Visible = false;
                _routePath.Hide();
                return;
            }

            int currentMapId = mumble.CurrentMap.Id;
            var data         = _mapData;   // one snapshot per pass; the loader may swap it any time
            bool haveData    = data != null && data.MapId == currentMapId;
            bool haveChar    = !string.IsNullOrEmpty(_store.CharacterName);

            if (!haveData && _loadError != null && _loadingMapId == currentMapId
             && DateTime.UtcNow - _lastLoadStartUtc > RETRY_DELAY) {
                BeginLoadMap(currentMapId);
            }

            // Ticking off happens regardless of whether the panel is shown.
            List<CompletionPoint> remaining = null;
            double playerX = 0, playerY = 0;
            if (haveData && haveChar) {
                var pos = mumble.UI.MapPosition;
                playerX   = pos.X;
                playerY   = pos.Y;
                remaining = TickAndCollect(data, playerX, playerY);
            }

            // Following a route is completion state, even when its panel and drawing are hidden.
            bool guideReadable = haveData && haveChar
                              && GameService.GameIntegration.Gw2Instance.IsInGame
                              && !mumble.UI.IsMapOpen && !_mumbleFrozen;
            if (guideReadable) UpdatePuzzleGuide(currentMapId);

            bool noPointsOnMap = haveData && data.Points.Count == 0;   // PvP maps, cities without completion, etc.
            // Mumble freezes on loading screens, cinematics and character select, so it can still report the map open.
            bool show = _showPanel.Value
                     && GameService.GameIntegration.Gw2Instance.IsInGame
                     && !mumble.UI.IsMapOpen
                     && (!noPointsOnMap || !_lockPanel.Value);   // unlocked: stay visible so it can be positioned

            _panel.Visible = show && !_mumbleFrozen;
            if (!_panel.Visible) {
                _routePath.Hide();
                // A short freeze only hides the panel. The note box closes only for a real reason.
                if (!show && _noteStep != null) SaveNote();
                return;
            }

            if (guideReadable && ShowPuzzleGuide(currentMapId)) return;
            _routePath.Hide();

            if (!haveData) {
                ShowStatus(_loadError ?? "Loading map data...");
                return;
            }

            if (!haveChar) {
                ShowStatus("Waiting for character...");
                return;
            }

            _panel.Summary = BuildSummary(remaining);

            if (remaining.Count == 0) {
                _target           = null;
                _panel.HasTarget  = false;
                _panel.ArrowColor = Color.LightGreen;
                _panel.Title      = Truncate(data.MapName);
                _panel.Detail     = noPointsOnMap ? "No completion points here." : "Everything tracked is done.";
                _panel.Hint       = _store.SavingDisabled ? "Progress file locked, not saving." : string.Empty;
                return;
            }

            var targetable = _kindFilter == null ? remaining : remaining.Where(p => p.Kind == _kindFilter.Value).ToList();
            if (_kindFilter != null) {
                _panel.Summary = $"Only {KindPlural(_kindFilter.Value)}, {_cycleKindKey.Value.GetBindingDisplayText()} for more";
            }

            if (targetable.Count == 0) {
                _target           = null;
                _panel.HasTarget  = false;
                _panel.ArrowColor = Color.LightGray;
                _panel.Title      = Truncate(data.MapName);
                _panel.Detail     = $"No open {KindPlural(_kindFilter.Value)} here.";
                _panel.Hint       = string.Empty;
                return;
            }

            // Keep the current target until it is done, skipped or another point is clearly closer, so the arrow
            // does not jump around between two points at similar distance.
            if (_target != null && targetable.Any(p => p.Key == _target.Key)) {
                SwitchIfClearlyCloser(targetable, playerX, playerY, data.MetersPerUnit);
            } else {
                var candidates = targetable.Where(p => !_skipped.Contains(p.Key)).ToList();
                if (candidates.Count == 0) {
                    // Everything was skipped once; start over, but keep the point that was just skipped out, so the
                    // re-pick below cannot switch straight back to it on the next tick.
                    _skipped.Clear();
                    if (targetable.Count > 1 && _lastSkippedKey != null) _skipped.Add(_lastSkippedKey);
                    candidates = targetable.Where(p => !_skipped.Contains(p.Key)).ToList();
                }

                _target = candidates.OrderBy(p => DistanceSq(p, playerX, playerY)).First();
            }

            double meters = Math.Sqrt(DistanceSq(_target, playerX, playerY)) * data.MetersPerUnit;

            _panel.HasTarget     = true;
            _panel.ArrowRotation = ComputeArrowRotation(_target, playerX, playerY);
            _panel.ArrowColor    = DistanceColor(meters);
            _panel.Title         = Truncate(string.IsNullOrEmpty(_target.Name) ? _target.KindLabel : _target.Name);
            _panel.Detail        = $"{_target.KindLabel}   {FormatDistance(meters)}";
            _panel.Hint          = _store.SavingDisabled ? "Progress file locked, not saving." : BuildHint(_target, meters);
        }

        private void UpdateMapOverlay() {
            if (_mapOverlay == null) return;

            var    mumble  = GameService.Gw2Mumble;
            var    data    = CurrentData();
            // Not in game (loading screen etc.) counts as closed, so the settle delay also restarts after a load.
            bool   mapOpen = mumble.IsAvailable && GameService.GameIntegration.Gw2Instance.IsInGame && mumble.UI.IsMapOpen;
            double scale   = mumble.IsAvailable ? mumble.UI.MapScale * MAP_SCALE_FACTOR : 0;

            if (mapOpen && !_mapWasOpen) _mapOpenedUtc = DateTime.UtcNow;
            _mapWasOpen = mapOpen;

            if (data == null || !mapOpen || scale <= 0) {
                if (_mapOverlay.Visible) {
                    _mapOverlay.Visible     = false;
                    _mapOverlay.Interactive = false;
                    _mapOverlay.SetMarkers(new MapOverlay.Marker[0]);
                    _mapOverlay.CaptureFrameState();
                }
                _recognizer.Reset();
                _loggedMapRead = null;   // the next opening of the map logs its first read again
                return;
            }

            if (_mumbleFrozen) {
                // Most likely a loading screen has just started. Stop drawing and clicking, but keep the settle delay,
                // the markers and any recognition read as they are, in case it was only a stutter.
                _mapOverlay.Visible     = false;
                _mapOverlay.Interactive = false;
                _mapOverlay.CaptureFrameState();
                return;
            }

            var   center  = mumble.UI.MapCenter;
            var   size    = _mapOverlay.Size;
            float halfW   = size.X / 2f;
            float halfH   = size.Y / 2f;
            // The map you are on first, then the other maps in view.
            var nearby  = NearbyData(data.MapId, center.X - halfW * scale, center.Y - halfH * scale,
                                     center.X + halfW * scale, center.Y + halfH * scale);
            var markers = BuildMarkers(new[] { data }.Concat(nearby), IsTracked, _store.IsDone, center.X, center.Y, scale, size);

            // The map zooms in for a moment after opening; nothing is read or clickable until it settles.
            bool settled = DateTime.UtcNow - _mapOpenedUtc > MAP_SETTLE_TIME;

            // The frames are optional (off by default, to keep the screen clean). Recognition works without them.
            var shown = markers.ToArray();
            _mapOverlay.SetMarkers(shown);
            _mapOverlay.ClickKey    = _frameClickKey.Value;
            _mapOverlay.Visible     = _showMapFrames.Value;
            _mapOverlay.Interactive = _showMapFrames.Value && settled;
            _mapOverlay.CaptureFrameState();

            if (_recognizeDone.Value) {
                float uiScale = GameService.Graphics.UIScaleMultiplier;
                // A neighbouring map that finishes loading changes the view, so its icons are read as well.
                var mouse = GameService.Input.Mouse.Position;
                string view   = $"{data.MapId}|{nearby.Count}|{center.X:F2}|{center.Y:F2}|{scale:F5}|{size.X}x{size.Y}|{uiScale:F3}";
                string tooltipContext = $"{mouse.X},{mouse.Y}";
                // Do not start another read while the overlay owns a manual frame interaction.
                bool mayStart = settled
                             && _mapOverlay.OwnedHover == null
                             && GameService.Input.Keyboard.ActiveModifiers == ModifierKeys.None
                             && GameService.GameIntegration.Gw2Instance.Gw2HasFocus;

                var found = _recognizer.Update(view, GameService.Gw2Mumble.Tick, mayStart, () => BuildRecognitionJobs(shown, uiScale), uiScale, tooltipContext);
                if (found != null) {
                    if (_mapReadLog != null) LogMapRead(_mapReadLog.Describe(found.Select(j => j.MapId)));
                    ApplyRecognised(found.ToLookup(j => j.MapId, j => j.Key), "world map");
                }
            } else {
                _recognizer.Reset();
            }
        }

        /// <summary>
        /// Frames for the tracked points of the given maps that lie on or near the screen (UI units). A few maps share
        /// points with another map (a city and its arena, for example); such a point gets one frame, owned by the first
        /// map that has it, so the two frames cannot crowd each other.
        /// </summary>
        internal static List<MapOverlay.Marker> BuildMarkers(IEnumerable<MapCompletionData> maps, Func<CompletionPoint, bool> isTracked,
                                                             Func<int, string, bool> isDone, double centerX, double centerY,
                                                             double scale, Point size) {
            float halfW   = size.X / 2f;
            float halfH   = size.Y / 2f;
            var   markers = new List<MapOverlay.Marker>();
            var   keys    = new HashSet<string>(StringComparer.Ordinal);

            foreach (var mapData in maps) {
                foreach (var point in mapData.Points) {
                    // A puzzle's position is only its nearest waypoint, which has a frame of its own.
                    if (!isTracked(point) || point.Kind == PointKind.Puzzle || keys.Contains(point.Key)) continue;

                    // Continent units to UI units; continent Y grows south like screen Y, so no flip here.
                    float x = (float)((point.X - centerX) / scale + halfW);
                    float y = (float)((point.Y - centerY) / scale + halfH);

                    if (x < -40 || y < -40 || x > size.X + 40 || y > size.Y + 40) continue;

                    keys.Add(point.Key);
                    markers.Add(new MapOverlay.Marker {
                        MapId    = mapData.MapId,
                        Point    = point,
                        Position = new Vector2(x, y),
                        Done     = isDone(mapData.MapId, point.Key)
                    });
                }
            }

            return markers;
        }

        /// <summary>
        /// Open points whose game icon can be read right now, with their position on the desktop. Null when the game
        /// window's position is unknown, so the same view is tried again.
        /// </summary>
        private List<MapRecognizer.Job> BuildRecognitionJobs(MapOverlay.Marker[] shown, float uiScale) {
            if (uiScale <= 0 || !ScreenCapture.TryGetClientSize(out int clientW, out int clientH)) return null;
            var log = new MapReadLog();
            var jobs = RecognitionJobsForView(shown, uiScale, _mapOverlay.Size, GameService.Input.Mouse.Position,
                                             clientW, clientH, _store.IsKeptOpen, log);
            _mapReadLog = log;
            if (jobs.Count == 0) LogMapRead(log.Describe());
            return jobs;
        }

        internal static List<MapRecognizer.Job> RecognitionJobsForView(MapOverlay.Marker[] shown, float uiScale,
                Point screen, Point mouse, int clientW, int clientH, Func<string, bool> isKeptOpen, MapReadLog log = null) {
            var jobs = new List<MapRecognizer.Job>();
            var hoveredHero = HeroTooltipTarget(shown, mouse);
            // Keep the whole copied square inside the game window, so nothing from another screen or window is read.
            float margin = IconRecognizer.Reach(uiScale) / uiScale + 1;

            foreach (var marker in shown) {
                var point = marker.Point;
                if (marker.Done) {
                    log?.AlreadyDone(marker.MapId);
                    continue;
                }

                string skip = !point.OnDefaultFloor                             ? "on another floor"
                            : !IconRecognizer.CanRecognise(point.Kind)          ? "of a kind not read from icons"
                            : isKeptOpen(point.Key)                             ? "kept open"
                            : marker.Position.X < margin || marker.Position.Y < margin
                           || marker.Position.X > screen.X - margin || marker.Position.Y > screen.Y - margin
                                                                                ? "at the screen edge"
                            // PrintWindow renders the game alone, without Blish frames or its zoom hint.
                            : IsCoveredByMapChrome(marker.Position, screen, false)
                                                                                ? "under the map's own panels"
                            : marker != hoveredHero && IsCoveredOnMap(marker.Position, screen, mouse, false)
                                                                                ? "near the cursor"
                            : null;

                int px = (int)Math.Round(marker.Position.X * uiScale);
                int py = (int)Math.Round(marker.Position.Y * uiScale);
                if (skip == null && (px < 0 || py < 0 || px >= clientW || py >= clientH)) skip = "outside the game window";

                if (skip == null && marker != hoveredHero && AmbiguousMapIcon(marker, shown, px, py, uiScale)) {
                    skip = "where icon searches overlap";
                }
                if (skip != null) {
                    log?.Skipped(marker.MapId, skip);
                    continue;
                }

                log?.Read(marker.MapId);
                jobs.Add(new MapRecognizer.Job { MapId = marker.MapId, Key = point.Key, Kind = point.Kind,
                                                ScreenX = px, ScreenY = py, HeroTooltip = marker == hoveredHero });
            }

            return jobs;
        }

        private static bool AmbiguousMapIcon(MapOverlay.Marker marker, MapOverlay.Marker[] shown, int x, int y, double uiScale) {
            int reach = IconRecognizer.CentreSearchRadius(uiScale, marker.Point.Kind, IconSet.WorldMap);
            foreach (var other in shown) {
                if (other == marker || !other.Point.OnDefaultFloor || !IconRecognizer.CanRecognise(other.Point.Kind)) continue;
                int ox = (int)Math.Round(other.Position.X * uiScale), oy = (int)Math.Round(other.Position.Y * uiScale);
                // Already-recorded and kept-open points still have game icons. A pixel of rounding at each centre
                // is included so overlapping search areas cannot attribute one icon to two different objectives.
                int separation = reach + IconRecognizer.CentreSearchRadius(uiScale, other.Point.Kind, IconSet.WorldMap) + 2;
                if (Math.Abs(x - ox) <= separation && Math.Abs(y - oy) <= separation) return true;
            }
            return false;
        }

        /// <summary>
        /// Logs what one still world map view read, per map. The same text is not logged twice in a row, so a map left
        /// open does not fill the log, but every new view (panning, zooming, a neighbouring map loading) gets a line.
        /// </summary>
        private void LogMapRead(string summary) {
            if (summary == _loggedMapRead) return;
            _loggedMapRead = summary;
            // The zoom shows whether the game draws icons at all: zoomed far out it hides them.
            Logger.Info("World map read at map scale {scale:F2}: {summary}", GameService.Gw2Mumble.UI.MapScale, summary);
        }

        /// <summary>
        /// True where something else is drawn over the map, in UI units: the game's completion legend (top left), its
        /// side buttons and loot list (right edge), the compass (bottom right), the tooltip near the cursor, and our
        /// own zoom hint. Sizes are measured from the owner's screenshots and scale with the game's UI size.
        /// </summary>
        internal static bool IsCoveredOnMap(Vector2 p, Point screen, Point mouse, bool zoomHintShown) {
            if (IsCoveredByMapChrome(p, screen, zoomHintShown)) return true;
            // The tooltip sits right of and above the cursor, up to about 360 units wide for events and hearts, and GW2
            // flips it to the left near the right edge.
            bool flipped = mouse.X > screen.X - 380;
            int  minX    = flipped ? mouse.X - 380 : mouse.X - 40;
            int  maxX    = flipped ? mouse.X + 40  : mouse.X + 380;
            return p.X > minX && p.X < maxX && p.Y > mouse.Y - 200 && p.Y < mouse.Y + 60;
        }

        private static bool IsCoveredByMapChrome(Vector2 p, Point screen, bool zoomHintShown) {
            if (p.X < 300 && p.Y < 500) return true;
            if (p.X > screen.X - 150) return true;
            if (p.X > screen.X - 220 && p.Y > screen.Y - 240) return true;
            if (zoomHintShown && Math.Abs(p.X - screen.X / 2f) < 200 && p.Y < 116) return true;
            return false;
        }

        internal static MapOverlay.Marker HeroTooltipTarget(MapOverlay.Marker[] shown, Point mouse) {
            MapOverlay.Marker hovered = null;
            foreach (var marker in shown) {
                if (!marker.Point.OnDefaultFloor) continue;
                double dx = marker.Position.X - mouse.X, dy = marker.Position.Y - mouse.Y;
                if (dx * dx + dy * dy > 22 * 22) continue;
                // Include already completed points and other kinds when checking ambiguity: their game tooltips
                // are still visible, and must never be attributed to a different, nearby open hero challenge.
                if (hovered != null) return null;
                hovered = marker;
            }
            return hovered?.Point.Kind == PointKind.HeroChallenge ? hovered : null;
        }

        /// <summary>Ticks the points read as done, grouped by the map they belong to.</summary>
        private void ApplyRecognised(ILookup<int, string> keysByMap, string source) {
            int total = 0, elsewhere = 0;
            int currentMapId = GameService.Gw2Mumble.CurrentMap.Id;
            foreach (var group in keysByMap) {
                int mapId = group.Key;
                var fresh = group.Distinct().Where(k => !_store.IsDone(mapId, k) && !_store.IsKeptOpen(k)).ToList();
                if (fresh.Count == 0) continue;

                // Every automatic tick is logged with where it was read, so a wrong tick can be traced to its rule.
                Logger.Info("Recognised as done from the {source} on map {mapId}: {keys}", source, mapId, string.Join(" ", fresh));

                _store.SetManyDone(mapId, fresh);
                PushUndo(mapId, fresh, recognised: true);
                total += fresh.Count;
                if (mapId != currentMapId) elsewhere += fresh.Count;
            }

            if (total == 0) return;

            string undo = _undoKey.Value.GetBindingDisplayText();
            // Undo only reaches the map you are on, so the message does not promise more.
            if (elsewhere == 0) {
                Notify(total == 1
                           ? $"Recognised 1 done point on the map. {undo} undoes it."
                           : $"Recognised {total} done points on the map. {undo} undoes them.");
            } else if (elsewhere == total) {
                Notify(total == 1
                           ? "Recognised 1 done point on a neighbouring map."
                           : $"Recognised {total} done points on neighbouring maps.");
            } else {
                Notify($"Recognised {total} done points, {elsewhere} of them on neighbouring maps. {undo} undoes the ones on this map.");
            }
            _target = null;
        }

        /// <summary>
        /// The loaded data of the other indexed maps that overlap the view (continent units), in index order. Maps not
        /// loaded yet are started in the background, a few at a time, and show up on a later frame. Only used while
        /// you stand on an indexed Tyria map, because the index's outlines are in Tyria's continent units.
        /// </summary>
        private List<MapCompletionData> NearbyData(int currentMapId, double left, double top, double right, double bottom) {
            var result = new List<MapCompletionData>();
            if (!MapIndex.Contains(currentMapId)) return result;

            var now = DateTime.UtcNow;
            foreach (int mapId in MapIndex.InView(left, top, right, bottom)) {
                if (mapId == currentMapId) continue;

                if (_nearbyData.TryGetValue(mapId, out var loaded)) {
                    result.Add(loaded);
                    continue;
                }

                if (_nearbyFailedUtc.TryGetValue(mapId, out var failedUtc) && now - failedUtc < NEARBY_RETRY_DELAY) continue;

                lock (_nearbyLock) {
                    if (_nearbyLoading.Contains(mapId) || _nearbyLoading.Count >= NEARBY_MAX_LOADS) continue;
                    _nearbyLoading.Add(mapId);
                }

                Task.Run(async () => {
                    try {
                        _nearbyData[mapId] = await _loader.LoadAsync(mapId).ConfigureAwait(false);
                        _nearbyFailedUtc.TryRemove(mapId, out _);
                    } catch (Exception ex) {
                        Logger.Warn(ex, "Could not load completion data for the nearby map {mapId}.", mapId);
                        _nearbyFailedUtc[mapId] = DateTime.UtcNow;
                    } finally {
                        lock (_nearbyLock) _nearbyLoading.Remove(mapId);
                    }
                });
            }

            return result;
        }

        /// <summary>
        /// Moves the arrow to another open point once you have walked closer to it than to the current target.
        /// Both margins must hold: the relative one matters far away, the absolute one up close.
        /// After a switch the old target would need to become just as clearly closer again, so the arrow cannot flicker.
        /// </summary>
        private void SwitchIfClearlyCloser(List<CompletionPoint> remaining, double playerX, double playerY, double metersPerUnit) {
            double current = Math.Sqrt(DistanceSq(_target, playerX, playerY)) * metersPerUnit;

            // Vistas, hearts and hero challenges tick from their map icon after you finish, or by the hotkey, which
            // ticks whatever the arrow points at. While you are working on one, it must not be swapped for a point you
            // happen to stand next to.
            if (!_target.AutoCompletes && current <= HoldRange(_target)) return;

            var closest = remaining.Where(p => p.Key != _target.Key && !_skipped.Contains(p.Key))
                                   .OrderBy(p => DistanceSq(p, playerX, playerY))
                                   .FirstOrDefault();
            if (closest == null) return;

            double other = Math.Sqrt(DistanceSq(closest, playerX, playerY)) * metersPerUnit;

            if (other <= current * REPICK_RATIO && current - other >= REPICK_MIN_METERS) {
                _target = closest;
            }
        }

        /// <summary>A heart's position is only the centre of its task area, so you can be working on it much further out.</summary>
        private static double HoldRange(CompletionPoint target) {
            return target.Kind == PointKind.Heart ? HEART_HOLD_METERS : HOLD_METERS;
        }

        /// <summary>Auto-ticks waypoints and points of interest in range and returns what is left to do.</summary>
        private List<CompletionPoint> TickAndCollect(MapCompletionData data, double playerX, double playerY) {
            double radiusUnits    = _autoTickRadius.Value / data.MetersPerUnit;
            double radiusSq       = radiusUnits * radiusUnits;
            double releaseSq      = radiusSq * SUPPRESS_RELEASE_FACTOR * SUPPRESS_RELEASE_FACTOR;

            var remaining  = new List<CompletionPoint>();
            var autoTicked = new List<string>();
            var apiTicked  = new List<string>();
            var puzzleApi  = new List<string>();

            foreach (var point in data.Points) {
                if (!IsTracked(point) || _store.IsDone(data.MapId, point.Key)) continue;

                // The API knows which hero challenges this character has done, so those tick themselves. The API is
                // authoritative, so this ignores "keep open" and is not undoable.
                if (point.Kind == PointKind.HeroChallenge && _heroPoints.IsDone(point.Key.Substring(3))) {
                    apiTicked.Add(point.Key);
                    continue;
                }

                // A jumping puzzle's achievement completes when you open its chest. It is account-wide and, like the
                // hero challenges above, authoritative: it ignores "keep open" and is not undoable.
                if (point.Kind == PointKind.Puzzle && _achievements.IsDone(PuzzleCatalog.Find(point.Key)?.AchievementId ?? 0)) {
                    puzzleApi.Add(point.Key);
                    continue;
                }

                if (point.AutoCompletes) {
                    double distSq = DistanceSq(point, playerX, playerY);

                    if (_suppressAuto.Contains(point.Key)) {
                        if (distSq > releaseSq) _suppressAuto.Remove(point.Key);
                    } else if (distSq <= radiusSq) {
                        autoTicked.Add(point.Key);
                        continue;
                    }
                }

                remaining.Add(point);
            }

            if (autoTicked.Count > 0) {
                _store.SetManyDone(data.MapId, autoTicked);
                PushUndo(data.MapId, autoTicked);
            }

            if (apiTicked.Count > 0) {
                _store.SetManyDone(data.MapId, apiTicked);
                _store.ReleaseKeepOpen(apiTicked);
                Notify(apiTicked.Count == 1 ? "Hero challenge done (from the API)." : $"{apiTicked.Count} hero challenges done (from the API).");
            }

            if (puzzleApi.Count > 0) {
                _store.SetManyDone(data.MapId, puzzleApi);
                _store.ReleaseKeepOpen(puzzleApi);
                Notify(puzzleApi.Count == 1 ? "Jumping puzzle done (from your achievements)." : $"{puzzleApi.Count} jumping puzzles done (from your achievements).");
            }

            if (_store.SavingDisabled && !_savingWarningShown) {
                _savingWarningShown = true;
                Notify("Nax Completion: progress file could not be read, so nothing is being saved. Check the Blish HUD log.");
            }

            return remaining;
        }

        private bool IsTracked(CompletionPoint point) {
            return IsTrackedKind(point.Kind);
        }

        private bool IsTrackedKind(PointKind kind) {
            switch (kind) {
                case PointKind.Waypoint:        return _trackWaypoints.Value;
                case PointKind.PointOfInterest: return _trackPois.Value;
                case PointKind.Vista:           return _trackVistas.Value;
                case PointKind.Heart:           return _trackHearts.Value;
                case PointKind.HeroChallenge:   return _trackHeroChallenges.Value;
                case PointKind.Puzzle:          return _trackPuzzles.Value;
                default:                        return false;
            }
        }

        /// <summary>
        /// Updates route progress independently of panel visibility. Only tracked, open points can be guided or ticked.
        /// </summary>
        private void UpdatePuzzleGuide(int mapId) {
            if (_recording != null) return;
            var pos = GameService.Gw2Mumble.PlayerCharacter.Position;
            // Only puzzles still open are guided: a ticked puzzle should stop steering you back to its route. The
            // owner reported being led to the end of Crimson Plateau again after ticking it. A vista's approach is
            // only guided near the vista itself (and while vistas are tracked), so passing one on the way elsewhere
            // does not take over the panel.
            var openRoutes = _routes.ForMap(mapId)
                                    .Where(r => !_store.IsDone(mapId, r.Key)
                                             && (RouteStore.IsVistaRoute(r)
                                                 ? _trackVistas.Value && NearVista(r, pos)
                                                 : _trackPuzzles.Value))
                                    .ToList();
            if (!_guide.Update(openRoutes, pos)) return;

            var route = _guide.Route;
            if (_guide.Finished) {
                // Reaching the end after following the route ticks the puzzle by itself, once per visit (the claim).
                // Undo puts the puzzle on the suppress list, so it stays open until you change maps or characters,
                // even though the guide restarts its visit once the ticked route drops out of the open routes. A
                // kept-open puzzle is left alone (puzzles get no map frames and are never in recognised batches, so
                // only a hand-edited progress file can keep one open). The claim is only used once a tick was
                // possible, so a visit that ends before the character is known still counts later.
                // Vistas are not ticked here: you still have to view the vista, and its map icon then ticks it.
                if (_trackPuzzles.Value && !RouteStore.IsVistaRoute(route) && _guide.CompletedThisVisit && !_guide.CompletionClaimed && route.MapId == mapId
                 && !string.IsNullOrEmpty(_store.CharacterName)) {
                    _guide.ClaimCompletion();
                    if (!_suppressAuto.Contains(route.Key) && !_store.IsKeptOpen(route.Key)
                     && !_store.IsDone(mapId, route.Key) && _store.SetDone(mapId, route.Key, true)) {
                        PushUndo(mapId, new List<string> { route.Key });
                        Notify($"Jumping puzzle done: {route.Puzzle}");
                        _target = null;
                    }
                }
            }
        }

        /// <summary>Displays the current recording or guide without changing completion state.</summary>
        private bool ShowPuzzleGuide(int mapId) {
            var pos = GameService.Gw2Mumble.PlayerCharacter.Position;
            if (_recording != null) {
                int count = _recording.Steps.Count;
                string addKey = _addStepKey.Value.GetBindingDisplayText();
                string saveKey = _finishRouteKey.Value.GetBindingDisplayText();
                _panel.HasTarget  = false;
                _panel.ArrowColor = Color.Gold;
                _panel.Title      = Truncate("Recording: " + _recording.Puzzle);
                _panel.Detail     = (count == 1 ? "1 step" : $"{count} steps") + $", {addKey} adds one";
                _panel.Hint       = $"{_noteKey.Value.GetBindingDisplayText()} note, {_undoKey.Value.GetBindingDisplayText()} remove step";
                _panel.Summary    = _routes.GetOwn(_recording.Key) != null
                                        ? $"{saveKey} saves, replacing your old route"
                                        : $"{saveKey} saves the route";
                ShowRecordedPath();
                return true;
            }

            var route = _guide.Route;
            if (route == null) return false;
            if (_guide.Finished || AtVista(route, mapId, pos)) {
                _panel.HasTarget  = false;
                _panel.ArrowColor = Color.LightGreen;
                _panel.Title      = Truncate(route.Puzzle);
                _panel.Detail     = route.BuiltIn ? "End of the route" : "End of your route";
                // Ticked points are no longer guided, so the end of a route is only shown while the point is open.
                _panel.Hint       = RouteStore.IsVistaRoute(route)
                                        ? "View the vista here"
                                        : $"Follow the route to tick it, or {_markDoneKey.Value.GetBindingDisplayText()}";
                _panel.Summary    = string.Empty;
                _routePath.Hide();
                return true;
            }

            var   step = route.Steps[_guide.NextStep];
            ShowGuidePath(route, _guide.NextStep);
            float toEast  = step.X - pos.X;
            float toNorth = step.Y - pos.Y;
            float flat    = (float)Math.Sqrt(toEast * toEast + toNorth * toNorth);
            float full    = PuzzleGuide.Distance(step, pos);

            _panel.HasTarget     = true;
            _panel.ArrowRotation = ArrowRotationToward(toEast, toNorth);
            _panel.ArrowColor    = full <= PuzzleGuide.REACHED ? Color.LightGreen : full <= 15 ? Color.Gold : Color.Orange;
            _panel.Title         = Truncate($"{route.Puzzle} {_guide.NextStep + 1}/{route.Steps.Count}");
            _panel.Detail        = $"{step.TypeLabel}   {flat:0} m   {PuzzleGuide.HeightText(pos.Z, step.Z)}";
            _panel.Hint          = step.Note ?? string.Empty;

            int after = _guide.NextStep + 1;
            _panel.Summary = after < route.Steps.Count && !string.IsNullOrEmpty(route.Steps[after].Note)
                                 ? TruncateTo("Then: " + route.Steps[after].Note, 44)
                                 : string.Empty;
            return true;
        }

        // How many steps ahead are drawn in the world. Few, so the screen stays readable.
        private const int PATH_STEPS_AHEAD = 3;

        // A vista's approach is guided once you are this close (flat distance) to the vista at its end.
        private const float VISTA_GUIDE_RADIUS = 60f;

        // Standing this close (flat) to a vista's real position, and within VISTA_AT_HEIGHT of the route's end, counts as
        // being at the vista, wherever the trail itself goes. At Ritual Isle the trail reaches the vista over a rock,
        // while the owner came into the gap below from another side and was still sent up the rock.
        private const double VISTA_AT_METERS = 4;
        private const float  VISTA_AT_HEIGHT = 6f;

        private bool AtVista(PuzzleRoute route, int mapId, Vector3 pos) {
            if (!RouteStore.IsVistaRoute(route)) return false;

            var data  = _mapData;
            var point = data != null && data.MapId == mapId ? data.Points.FirstOrDefault(p => p.Key == route.Key) : null;
            if (point == null) return false;

            var    me   = GameService.Gw2Mumble.UI.MapPosition;
            double flat = Math.Sqrt(DistanceSq(point, me.X, me.Y)) * data.MetersPerUnit;
            return flat <= VISTA_AT_METERS && Math.Abs(route.Steps[route.Steps.Count - 1].Z - pos.Z) <= VISTA_AT_HEIGHT;
        }

        private static bool NearVista(PuzzleRoute route, Vector3 pos) {
            var   end = route.Steps[route.Steps.Count - 1];
            float dx  = end.X - pos.X, dy = end.Y - pos.Y;
            return dx * dx + dy * dy <= VISTA_GUIDE_RADIUS * VISTA_GUIDE_RADIUS;
        }

        private void ShowGuidePath(PuzzleRoute route, int nextStep) {
            var steps = new List<RouteStep>();
            for (int i = nextStep; i < route.Steps.Count && steps.Count < PATH_STEPS_AHEAD; i++) steps.Add(route.Steps[i]);
            _routePath.Show(steps, nextStep > 0 ? route.Steps[nextStep - 1] : null);
        }

        /// <summary>While recording, the last few recorded steps stay visible, so you see what you have.</summary>
        private void ShowRecordedPath() {
            var all   = _recording.Steps;
            int first = Math.Max(0, all.Count - PATH_STEPS_AHEAD);
            var steps = new List<RouteStep>();
            for (int i = first; i < all.Count; i++) steps.Add(all[i]);
            _routePath.Show(steps, first > 0 ? all[first - 1] : null);
        }

        /// <summary>Arrow rotation towards an offset given in metres east and north.</summary>
        private static float ArrowRotationToward(double toEast, double toNorth) {
            Vector3 facing = GameService.Gw2Mumble.PlayerCamera.Forward;
            if (Math.Abs(facing.X) < 0.001f && Math.Abs(facing.Y) < 0.001f) {
                facing = GameService.Gw2Mumble.PlayerCharacter.Forward;
            }

            return (float)NavMath.ArrowRotation(toEast, toNorth, facing.X, facing.Y);
        }

        /// <summary>
        /// Rotation of the arrow on screen, clockwise, 0 = the direction the camera looks.
        /// Blish HUD exposes the camera vector with X = east and Y = north; continent Y grows south and is flipped in NavMath.
        /// </summary>
        private static float ComputeArrowRotation(CompletionPoint target, double playerX, double playerY) {
            NavMath.ContinentOffsetToEastNorth(playerX, playerY, target.X, target.Y, out double toEast, out double toNorth);

            Vector3 facing = GameService.Gw2Mumble.PlayerCamera.Forward;
            if (Math.Abs(facing.X) < 0.001f && Math.Abs(facing.Y) < 0.001f) {
                // Camera looks straight down or up; fall back to where the character faces.
                facing = GameService.Gw2Mumble.PlayerCharacter.Forward;
            }

            return (float)NavMath.ArrowRotation(toEast, toNorth, facing.X, facing.Y);
        }

        private static double DistanceSq(CompletionPoint point, double x, double y) {
            double dx = point.X - x;
            double dy = point.Y - y;
            return dx * dx + dy * dy;
        }

        private static Color DistanceColor(double meters) {
            if (meters <= 50)  return Color.LightGreen;
            if (meters <= 200) return Color.Gold;
            return Color.Orange;
        }

        private static string FormatDistance(double meters) {
            return meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m";
        }

        private string BuildHint(CompletionPoint target, double meters) {
            if (target.AutoCompletes) {
                return "Ticks itself when you arrive";
            }

            if (meters > 50 && !string.IsNullOrEmpty(target.Hint)) return target.Hint;

            string key = _markDoneKey.Value.GetBindingDisplayText();

            // The panel only promises an automatic tick where one can actually happen; otherwise it names the hotkey.
            // Hearts, vistas and hero challenges tick from their map icon. The compass skips icons within 18 px of your
            // own marker (MinimapRecognizer.PLAYER_MARKER_RADIUS_PX), so the tick follows a few steps after you leave.
            // A rotating compass is not read, so then only the world map ticks them. Puzzles tick at the end of a
            // route, or from the account achievement when the API key allows it.
            switch (target.Kind) {
                case PointKind.Heart:
                case PointKind.Vista:
                case PointKind.HeroChallenge:
                    if (!_recognizeDone.Value || !target.OnDefaultFloor) return $"{key} when done";
                    if (GameService.Gw2Mumble.UI.IsCompassRotationEnabled) return $"Open the map to tick it, or {key}";
                    return meters <= 50 ? "Ticks itself a few steps after you finish" : "Ticks itself when done";
                case PointKind.Puzzle:
                    if (_routes.Get(target.Key) != null) return "Ticks itself at the end of the route";
                    if (_achievements.Available && (PuzzleCatalog.Find(target.Key)?.AchievementId ?? 0) > 0) return "Ticks itself from your achievements";
                    return $"{key} when done";
                default:
                    return $"{key} when done";
            }
        }

        private static string BuildSummary(List<CompletionPoint> remaining) {
            if (remaining == null || remaining.Count == 0) return string.Empty;

            var parts = new List<string>();
            AddCount(parts, remaining, PointKind.Waypoint,        "WP");
            AddCount(parts, remaining, PointKind.PointOfInterest, "POI");
            AddCount(parts, remaining, PointKind.Vista,           "Vista");
            AddCount(parts, remaining, PointKind.Heart,           "Heart");
            AddCount(parts, remaining, PointKind.HeroChallenge,   "HC");
            AddCount(parts, remaining, PointKind.Puzzle,          "JP");

            return "Left: " + string.Join("  ", parts);
        }

        private static void AddCount(List<string> parts, List<CompletionPoint> remaining, PointKind kind, string label) {
            int count = remaining.Count(p => p.Kind == kind);
            if (count > 0) parts.Add($"{count} {label}");
        }

        private static string Describe(CompletionPoint point) {
            return string.IsNullOrEmpty(point.Name) ? point.KindLabel : $"{point.KindLabel}: {point.Name}";
        }

        private static string TruncateTo(string text, int max) {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
            return text.Substring(0, max - 3).TrimEnd() + "...";
        }

        private static string Truncate(string text) {
            if (string.IsNullOrEmpty(text) || text.Length <= MAX_TITLE_LEN) return text ?? string.Empty;
            return text.Substring(0, MAX_TITLE_LEN - 3).TrimEnd() + "...";
        }

        private void ShowStatus(string text) {
            _target           = null;
            _panel.HasTarget  = false;
            _panel.ArrowColor = Color.LightGray;
            _panel.Title      = "Nax Completion";
            _panel.Detail     = text;
            _panel.Hint       = string.Empty;
            _panel.Summary    = string.Empty;
        }

        /// <summary>Map data for the map you are on right now, or null.</summary>
        private MapCompletionData CurrentData() {
            var data = _mapData;
            if (data == null
             || !GameService.Gw2Mumble.IsAvailable
             || data.MapId != GameService.Gw2Mumble.CurrentMap.Id
             || string.IsNullOrEmpty(_store.CharacterName)) {
                return null;
            }

            return data;
        }

        private void LoadCharacter(string name) {
            _store.LoadCharacter(name);
            _heroPoints.SetCharacter(name);
            _achievements.Reset();   // the new character may be on another account
            _undo.Clear();
            _skipped.Clear();
            _suppressAuto.Clear();
            _recognizer.Reset();
            _minimap.Reset();
            _guide.Reset();
            _lastSkippedKey     = null;
            _target             = null;
            _savingWarningShown = false;
        }

        private void PushUndo(int mapId, List<string> keys, bool recognised = false) {
            _undo.Push(mapId, keys, recognised, DateTime.UtcNow);
        }

        private void BeginLoadMap(int mapId) {
            if (mapId <= 0) return;

            int token = ++_loadToken;
            _loadingMapId     = mapId;
            _loadError        = null;
            _lastLoadStartUtc = DateTime.UtcNow;

            Task.Run(async () => {
                try {
                    var data = await _loader.LoadAsync(mapId).ConfigureAwait(false);
                    if (token == _loadToken) _mapData = WithPuzzles(data);
                } catch (Exception ex) {
                    Logger.Warn(ex, "Could not load completion data for map {mapId}.", mapId);
                    if (token == _loadToken) _loadError = "Map data failed, retrying in 30 s.";
                }
            });
        }

        /// <summary>
        /// Adds the map's jumping puzzles. A puzzle with a recorded route points at the route's start, otherwise at the
        /// waypoint nearest to it. Runs on the loader's thread, and the route store locks itself.
        /// </summary>
        private MapCompletionData WithPuzzles(MapCompletionData data) {
            var points = data.Points.Where(p => p.Kind != PointKind.Puzzle).ToList();
            foreach (var puzzle in PuzzleCatalog.ForMap(data.MapId)) {
                var route = _routes.Get(puzzle.Key);
                points.Add(route != null
                               ? new CompletionPoint(puzzle.Key, PointKind.Puzzle, puzzle.Name, route.StartX, route.StartY,
                                                     route.BuiltIn ? "Start of the puzzle" : "Start of your recorded route")
                               : new CompletionPoint(puzzle.Key, PointKind.Puzzle, puzzle.Name, puzzle.X, puzzle.Y, $"Near {puzzle.Waypoint}"));
            }

            return new MapCompletionData(data.MapId, data.MapName, data.MetersPerUnit, points);
        }

        private void PlacePanel() {
            if (_panel == null) return;

            var screen = GameService.Graphics.SpriteScreen.Size;
            int x      = _panelX.Value < 0 ? (screen.X - _panel.Width) / 2 : _panelX.Value;
            int y      = _panelY.Value;

            _panel.Location = new Point(MathHelper.Clamp(x, 0, Math.Max(0, screen.X - _panel.Width)),
                                        MathHelper.Clamp(y, 0, Math.Max(0, screen.Y - _panel.Height)));
        }

        private static void Notify(string message) {
            ScreenNotification.ShowNotification(message);
        }

        protected override void Unload() {
            foreach (var key in this.AllKeys) {
                if (key.Value != null) key.Value.Enabled = false;
            }

            if (_markDoneKey?.Value    != null) _markDoneKey.Value.Activated    -= OnMarkDone;
            if (_nextTargetKey?.Value  != null) _nextTargetKey.Value.Activated  -= OnNextTarget;
            if (_undoKey?.Value        != null) _undoKey.Value.Activated        -= OnUndo;
            if (_markMapDoneKey?.Value != null) _markMapDoneKey.Value.Activated -= OnMarkMapDone;
            if (_resetMapKey?.Value    != null) _resetMapKey.Value.Activated    -= OnResetMap;
            if (_addStepKey?.Value     != null) _addStepKey.Value.Activated     -= OnAddRouteStep;
            if (_finishRouteKey?.Value != null) _finishRouteKey.Value.Activated -= OnFinishRoute;
            if (_noteKey?.Value        != null) _noteKey.Value.Activated        -= OnRouteNote;
            if (_cycleKindKey?.Value   != null) _cycleKindKey.Value.Activated   -= OnCycleKind;

            _routePath?.Dispose();
            _routePath = null;
            // A read still running must fail instead of recreating the capture bitmap after unload.
            ScreenCapture.GameWindow = IntPtr.Zero;
            ScreenCapture.Release();

            if (_noteBox != null) {
                _noteBox.EnterPressed      -= OnNoteEntered;
                _noteBox.InputFocusChanged -= OnNoteFocusChanged;
                if (_noteBox.Focused) _noteBox.UnsetFocus();
                _noteBox.Dispose();
                _noteBox = null;
            }

            GameService.Gw2Mumble.PlayerCharacter.NameChanged -= OnCharacterChanged;
            GameService.Gw2Mumble.CurrentMap.MapChanged       -= OnMapChanged;
            GameService.Graphics.SpriteScreen.Resized         -= OnScreenResized;

            if (_lockPanel != null) _lockPanel.SettingChanged -= OnLockChanged;
            if (_panelX    != null) _panelX.SettingChanged    -= OnPanelPositionSettingChanged;
            if (_panelY    != null) _panelY.SettingChanged    -= OnPanelPositionSettingChanged;

            if (_panel != null) {
                _panel.DragFinished -= OnPanelDragFinished;
                _panel.Dispose();
                _panel = null;
            }

            if (_mapOverlay != null) {
                GameService.Input.Mouse.LeftMouseButtonPressed -= OnMouseLeftPressed;
                _mapOverlay.Dispose();
                _mapOverlay = null;
            }

            // Invalidate any load still in flight.
            _loadToken++;
            _mapData = null;
            _target  = null;
        }

    }

}
