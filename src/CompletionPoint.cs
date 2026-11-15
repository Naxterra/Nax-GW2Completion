using System.Collections.Generic;

namespace Nax.Completion {

    public enum PointKind {
        Waypoint,
        PointOfInterest,
        Vista,
        Heart,
        HeroChallenge,
        Puzzle
    }

    /// <summary>One map completion objective, in continent coordinates.</summary>
    public sealed class CompletionPoint {

        public string    Key  { get; }
        public PointKind Kind { get; }
        public string    Name { get; }

        /// <summary>Continent X (grows east).</summary>
        public double X { get; }

        /// <summary>Continent Y (grows south).</summary>
        public double Y { get; }

        /// <summary>Extra line for the panel while the point is far away, for example the waypoint a puzzle starts near.</summary>
        public string Hint { get; }

        /// <summary>
        /// False for points that only exist on another floor of the map (caves, upper levels). The map and the compass
        /// show the default floor, so no icon is drawn for them there and their icons cannot be read.
        /// </summary>
        public bool OnDefaultFloor { get; }

        public CompletionPoint(string key, PointKind kind, string name, double x, double y, string hint = null, bool onDefaultFloor = true) {
            this.Key            = key;
            this.Kind           = kind;
            this.Name           = name;
            this.X              = x;
            this.Y              = y;
            this.Hint           = hint;
            this.OnDefaultFloor = onDefaultFloor;
        }

        /// <summary>
        /// Waypoints and points of interest are unlocked just by walking up to them, so they can be ticked off by proximity.
        /// Vistas, hearts and hero challenges need an action in game, so they are only ticked off by hotkey.
        /// </summary>
        public bool AutoCompletes => this.Kind == PointKind.Waypoint || this.Kind == PointKind.PointOfInterest;

        private string _shortLabel;

        /// <summary>"Kind: Name", cut to fit the map label. Built once.</summary>
        public string ShortLabel {
            get {
                if (_shortLabel != null) return _shortLabel;

                string full = string.IsNullOrEmpty(this.Name) ? this.KindLabel : this.KindLabel + ": " + this.Name;
                _shortLabel = full.Length <= 36 ? full : full.Substring(0, 33).TrimEnd() + "...";
                return _shortLabel;
            }
        }

        public string KindLabel {
            get {
                switch (this.Kind) {
                    case PointKind.Waypoint:        return "Waypoint";
                    case PointKind.PointOfInterest: return "Point of interest";
                    case PointKind.Vista:           return "Vista";
                    case PointKind.Heart:           return "Heart";
                    case PointKind.HeroChallenge:   return "Hero challenge";
                    case PointKind.Puzzle:          return "Jumping puzzle";
                    default:                        return "Point";
                }
            }
        }

    }

    /// <summary>All completion objectives of one map plus the scale needed to turn continent units into meters.</summary>
    public sealed class MapCompletionData {

        public int                            MapId          { get; }
        public string                         MapName        { get; }
        public double                         MetersPerUnit  { get; }
        public IReadOnlyList<CompletionPoint> Points         { get; }

        public MapCompletionData(int mapId, string mapName, double metersPerUnit, IReadOnlyList<CompletionPoint> points) {
            this.MapId         = mapId;
            this.MapName       = mapName;
            this.MetersPerUnit = metersPerUnit;
            this.Points        = points;
        }

    }

}
