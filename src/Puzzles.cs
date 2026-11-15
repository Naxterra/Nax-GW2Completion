using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Blish_HUD;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Nax.Completion {

    /// <summary>One jumping puzzle from the bundled list: where its nearest waypoint is.</summary>
    public sealed class PuzzleInfo {
        [JsonProperty("key")]      public string Key      { get; set; }
        [JsonProperty("name")]     public string Name     { get; set; }
        [JsonProperty("map")]      public int    MapId    { get; set; }
        [JsonProperty("waypoint")] public string Waypoint { get; set; }
        [JsonProperty("x")]        public double X        { get; set; }   // continent units
        [JsonProperty("y")]        public double Y        { get; set; }
        // The account achievement that completes with the puzzle's chest, or 0 when none matches by name.
        [JsonProperty("achievement")] public int AchievementId { get; set; }
    }

    /// <summary>
    /// The jumping puzzles shipped with the module (data\puzzles.json, embedded). Only names, maps and the nearest
    /// waypoint are known, so the arrow leads to that waypoint until you record a route for the puzzle.
    /// </summary>
    public static class PuzzleCatalog {

        private const string RESOURCE = "Nax.Completion.puzzles.json";

        private sealed class FileModel {
            [JsonProperty("puzzles")] public List<PuzzleInfo> Puzzles { get; set; } = new List<PuzzleInfo>();
        }

        private static readonly Lazy<IReadOnlyList<PuzzleInfo>> All = new Lazy<IReadOnlyList<PuzzleInfo>>(Load);

        public static IReadOnlyList<PuzzleInfo> Puzzles => All.Value;

        public static IEnumerable<PuzzleInfo> ForMap(int mapId) => All.Value.Where(p => p.MapId == mapId);

        public static PuzzleInfo Find(string key) => All.Value.FirstOrDefault(p => p.Key == key);

        private static IReadOnlyList<PuzzleInfo> Load() {
            using (var stream = typeof(PuzzleCatalog).Assembly.GetManifestResourceStream(RESOURCE)) {
                if (stream == null) return new List<PuzzleInfo>();

                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    var model = JsonConvert.DeserializeObject<FileModel>(reader.ReadToEnd());
                    return model?.Puzzles?.Where(p => !string.IsNullOrEmpty(p.Key)).ToList() ?? new List<PuzzleInfo>();
                }
            }
        }

    }

    /// <summary>How you get to a step from the one before it.</summary>
    public enum StepType {
        Walk,   // just move there
        Run,    // a running jump, take momentum
        Jump,   // a jump, from standing
        Fly     // glide or mount
    }

    public sealed class RouteStep {
        // Mumble position in metres: X east, Y north, Z up.
        [JsonProperty("x")]    public float    X    { get; set; }
        [JsonProperty("y")]    public float    Y    { get; set; }
        [JsonProperty("z")]    public float    Z    { get; set; }
        [JsonProperty("type")] [JsonConverter(typeof(StringEnumConverter))] public StepType Type { get; set; } = StepType.Walk;
        [JsonProperty("note")] public string   Note { get; set; } = string.Empty;
        // The source trail ends here and resumes elsewhere, for example after a portal or branch transition.
        [JsonProperty("breakBefore", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool BreakBefore { get; set; }

        public string TypeLabel {
            get {
                switch (this.Type) {
                    case StepType.Run:  return "Run and jump";
                    case StepType.Jump: return "Jump";
                    case StepType.Fly:  return "Fly";
                    default:            return "Walk";
                }
            }
        }
    }

    /// <summary>A route through one jumping puzzle: one you recorded, or one shipped with the module.</summary>
    public sealed class PuzzleRoute {
        [JsonProperty("key")]    public string          Key    { get; set; }
        [JsonProperty("puzzle")] public string          Puzzle { get; set; }
        [JsonProperty("map")]    public int             MapId  { get; set; }
        // Continent position of the first step, so the arrow can lead to the real start instead of the waypoint.
        [JsonProperty("startX")] public double          StartX { get; set; }
        [JsonProperty("startY")] public double          StartY { get; set; }
        [JsonProperty("steps")]  public List<RouteStep> Steps  { get; set; } = new List<RouteStep>();

        /// <summary>True for the routes shipped with the module (data\puzzle_routes.json), false for your own.</summary>
        [JsonIgnore] public bool BuiltIn { get; set; }
    }

    /// <summary>
    /// Puzzle routes. Your recorded routes are one JSON file each in the module's data folder under "routes", shared
    /// by all characters. The module also ships routes converted from the jumping puzzle trails of Lady Elyssa's
    /// Guides (github.com/LadyElyssa/LadyElyssaTacoTrails), used with credit as the author permits. Where you recorded
    /// a route of your own for a puzzle, yours is used instead of the shipped one.
    /// </summary>
    public sealed class RouteStore {

        private static readonly Logger Logger = Logger.GetLogger<RouteStore>();

        // Jumping puzzle routes, and vista approaches cut from the same author's barefoot map completion route.
        private static readonly string[] BUILT_IN_RESOURCES = { "Nax.Completion.puzzle_routes.json", "Nax.Completion.vista_routes.json" };

        private sealed class BuiltInFile {
            [JsonProperty("routes")] public List<PuzzleRoute> Routes { get; set; } = new List<PuzzleRoute>();
        }

        private readonly string _directory;
        private readonly object _lock = new object();
        private readonly Dictionary<string, PuzzleRoute> _routes  = new Dictionary<string, PuzzleRoute>(StringComparer.Ordinal);
        private readonly Dictionary<string, PuzzleRoute> _builtIn = new Dictionary<string, PuzzleRoute>(StringComparer.Ordinal);

        public RouteStore(string directory) {
            _directory = directory;
        }

        /// <summary>Loads the routes shipped inside the module. A broken resource only means no shipped routes from it.</summary>
        public void LoadBuiltIn() {
            foreach (string resource in BUILT_IN_RESOURCES) {
                try {
                    using (var stream = typeof(RouteStore).Assembly.GetManifestResourceStream(resource)) {
                        if (stream == null) continue;
                        using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                            var file = JsonConvert.DeserializeObject<BuiltInFile>(reader.ReadToEnd());
                            lock (_lock) {
                                foreach (var route in file?.Routes ?? new List<PuzzleRoute>()) {
                                    if (route?.Key == null || route.Steps == null || route.Steps.Count == 0) continue;
                                    route.BuiltIn = true;
                                    _builtIn[route.Key] = route;
                                }
                            }
                        }
                    }
                } catch (Exception ex) {
                    Logger.Warn(ex, "Could not read the shipped routes in {resource}.", resource);
                }
            }
        }

        /// <summary>Vista approach routes have the vista's own point key.</summary>
        public static bool IsVistaRoute(PuzzleRoute route) => route?.Key != null && route.Key.StartsWith("vista:", StringComparison.Ordinal);

        /// <summary>Your own route for the puzzle, ignoring the shipped one.</summary>
        public PuzzleRoute GetOwn(string key) {
            lock (_lock) return _routes.TryGetValue(key, out var route) ? route : null;
        }

        public void LoadAll() {
            if (!Directory.Exists(_directory)) return;

            foreach (string path in Directory.GetFiles(_directory, "*.json")) {
                try {
                    var route = JsonConvert.DeserializeObject<PuzzleRoute>(File.ReadAllText(path, Encoding.UTF8));
                    if (route?.Key == null || route.Steps == null || route.Steps.Count == 0) continue;
                    lock (_lock) _routes[route.Key] = route;
                } catch (Exception ex) {
                    Logger.Warn(ex, "Could not read puzzle route {path}.", path);
                }
            }
        }

        /// <summary>Your own route if you recorded one, else the shipped one. Safe to call from the map loader's background thread.</summary>
        public PuzzleRoute Get(string key) {
            lock (_lock) {
                if (_routes.TryGetValue(key, out var own)) return own;
                return _builtIn.TryGetValue(key, out var shipped) ? shipped : null;
            }
        }

        public List<PuzzleRoute> ForMap(int mapId) {
            lock (_lock) {
                return _routes.Values.Where(r => r.MapId == mapId)
                              .Concat(_builtIn.Values.Where(r => r.MapId == mapId && !_routes.ContainsKey(r.Key)))
                              .ToList();
            }
        }

        /// <summary>Returns false when the file could not be written. The route then only lasts until Blish restarts.</summary>
        public bool Save(PuzzleRoute route) {
            lock (_lock) _routes[route.Key] = route;

            string path    = Path.Combine(_directory, FileName(route.Key));
            string tmpPath = path + ".tmp";
            try {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(route, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) {
                    File.Replace(tmpPath, path, null);
                } else {
                    File.Move(tmpPath, path);
                }
                return true;
            } catch (Exception ex) {
                Logger.Warn(ex, "Could not save puzzle route {path}.", path);
                return false;
            }
        }

        /// <summary>Keys look like "jp:loreclaw-expanse", so only the colon needs replacing.</summary>
        internal static string FileName(string key) {
            var safe = new StringBuilder(key.Length);
            foreach (char c in key) safe.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return safe.ToString().ToLower(CultureInfo.InvariantCulture) + ".json";
        }

    }

}
