using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;

namespace Nax.Completion {

    /// <summary>
    /// Reads the game's compass (minimap) icons while you play, so hearts, waypoints and points of interest tick
    /// themselves a moment after you finish them, without opening the world map. The compass is read every
    /// <see cref="INTERVAL"/> from one screen copy of its rectangle, classified with the same rules as the world map.
    /// Newly readable points trigger an earlier read, so a short view while travelling is not lost between polls.
    ///
    /// Geometry: while the world map is closed, Mumble's map centre and scale describe the compass, so a point sits
    /// at (point - centre) / scale pixels from the compass centre, rotated by the compass rotation when that is on.
    /// Mumble's compass size is in screen pixels. The compass sits at the right screen edge, at the top or above the
    /// bottom bar. The icon under your own marker (compass centre) cannot be read and is skipped.
    /// </summary>
    internal sealed class MinimapRecognizer {

        public sealed class Job {
            public string    Key;
            public PointKind Kind;
            public int       ScreenX;   // physical pixels, relative to the game window's client area
            public int       ScreenY;
        }

        private static readonly Logger Logger = Logger.GetLogger<MinimapRecognizer>();

        // A permanent failure is logged again now and then, so it stays visible in the log.
        private static readonly TimeSpan FAILURE_LOG_INTERVAL = TimeSpan.FromMinutes(5);

        public static readonly TimeSpan INTERVAL = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan DISCOVERY_INTERVAL = TimeSpan.FromMilliseconds(250);

        // Measured live on the owner's 3440x1440 screen with the Normal UI size (UIScaleMultiplier 0.897): the
        // compass bottom edge sits this many pixels above the window bottom. Scaled with the UI size.
        public const double BOTTOM_BAR_PX = 36;

        // Your own marker, the compass border and the top-right buttons cover icons near them.
        // Measured live on 2026-10-25: the player marker ring is about 17 px across, and a compass icon about 13 px. An
        // icon centred 18 px away keeps a gap of about 2 px to the ring. The pale view cone can only lighten an icon,
        // which never makes an open icon pass a "done" rule. 26 px skipped a viewed vista 22 px away for as long as
        // the owner stood near it.
        public const int PLAYER_MARKER_RADIUS_PX = 18;
        public const int EDGE_MARGIN_PX          = 6;

        private DateTime           _nextReadUtc = DateTime.MinValue;
        private Task<List<string>> _running;
        private int                _runningMap;
        private int                _runningGeneration;
        private int                _generation;
        private DateTime           _lastFailureLogUtc = DateTime.MinValue;
        private int                _runningTick;
        private readonly HashSet<string> _visibleKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _newlyReadableKeys = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _nextDiscoveryUtc = DateTime.MinValue;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<List<Job>, double, List<string>> _read;

        public MinimapRecognizer() : this(() => DateTime.UtcNow, Read) { }

        internal MinimapRecognizer(Func<DateTime> utcNow, Func<List<Job>, double, List<string>> read) {
            _utcNow = utcNow;
            _read = read;
        }

        public void Reset() {
            _running = null;
            _nextReadUtc = DateTime.MinValue;
            _nextDiscoveryUtc = DateTime.MinValue;
            _visibleKeys.Clear();
            _newlyReadableKeys.Clear();
            _generation++;
        }

        /// <summary>Call when the game stopped being readable (loading screen, freeze): a read in flight is then discarded.</summary>
        public void Invalidate() {
            _generation++;
            _visibleKeys.Clear();
            _newlyReadableKeys.Clear();
            _nextReadUtc = DateTime.MinValue;
            _nextDiscoveryUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Call once per frame while the map is closed and the game is in a readable state. Returns keys read as
        /// filled when a read for the same map has finished, otherwise null.
        /// </summary>
        public List<string> Update(int mapId, int mumbleTick, bool mayStart, Func<List<Job>> buildJobs, double uiScale) {
            if (_running != null) {
                if (!_running.IsCompleted) return null;

                // A loading screen freezes Mumble from the moment it appears, so the game must have ticked at least
                // twice since the read started before its picture can be trusted. A frozen game keeps the result waiting;
                // Invalidate drops it when the freeze is noticed.
                if (mumbleTick - _runningTick < 2 && _runningGeneration == _generation) return null;

                var task = _running;
                int ran  = _runningMap;
                int gen  = _runningGeneration;
                _running = null;

                if (task.IsFaulted || task.IsCanceled) {
                    if (DateTime.UtcNow - _lastFailureLogUtc > FAILURE_LOG_INTERVAL) {
                        _lastFailureLogUtc = DateTime.UtcNow;
                        Logger.Warn(task.Exception?.GetBaseException(), "Reading the compass icons failed.");
                    }
                    return null;
                }

                return ran == mapId && gen == _generation ? task.Result : null;
            }

            if (!mayStart) return null;

            var jobs = buildJobs();
            if (jobs == null || jobs.Count == 0) {
                // An empty or player-covered compass must not consume the next clear view's opportunity.
                _visibleKeys.Clear();
                _newlyReadableKeys.Clear();
                return null;
            }

            var visible = new HashSet<string>(jobs.Select(job => job.Key), StringComparer.Ordinal);
            _newlyReadableKeys.UnionWith(visible.Where(key => !_visibleKeys.Contains(key)));
            _newlyReadableKeys.IntersectWith(visible);
            _visibleKeys.Clear();
            _visibleKeys.UnionWith(visible);
            var now = _utcNow();
            bool newlyReadable = _newlyReadableKeys.Count > 0;
            if (now < _nextReadUtc && (!newlyReadable || now < _nextDiscoveryUtc)) return null;
            _nextReadUtc = now + INTERVAL;
            _nextDiscoveryUtc = now + DISCOVERY_INTERVAL;
            _newlyReadableKeys.Clear();

            _runningMap        = mapId;
            _runningGeneration = _generation;
            _runningTick       = mumbleTick;
            _running           = Task.Run(() => _read(jobs, uiScale));
            return null;
        }

        private static List<string> Read(List<Job> jobs, double uiScale) {
            int reach  = IconRecognizer.Reach(uiScale, IconSet.Compass);
            int left   = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (var job in jobs) {
                left   = Math.Min(left,   job.ScreenX - reach);
                top    = Math.Min(top,    job.ScreenY - reach);
                right  = Math.Max(right,  job.ScreenX + reach);
                bottom = Math.Max(bottom, job.ScreenY + reach);
            }

            int width  = right - left + 1;
            int height = bottom - top + 1;
            int[] pixels = ScreenCapture.Capture(left, top, width, height);
            return ReadPixels(jobs, pixels, width, height, left, top, uiScale);
        }

        internal static List<string> ReadPixels(List<Job> jobs, int[] pixels, int width, int height,
                                                int left, int top, double uiScale) {
            var filled = new List<string>();
            foreach (var job in jobs) {
                if (IconRecognizer.IsFilled(pixels, width, height, job.ScreenX - left, job.ScreenY - top, job.Kind, uiScale, IconSet.Compass)) {
                    filled.Add(job.Key);
                }
            }

            return filled;
        }

        /// <summary>
        /// Position of a point on the compass, or false when it lies outside the readable area. All inputs and the
        /// result are physical pixels relative to the game window. <paramref name="rotation"/> is the compass
        /// rotation in radians when rotation is enabled, otherwise 0. The player marker sits at the player's own
        /// position, which differs from the centre when the compass was dragged.
        /// </summary>
        public static bool TryProject(double pointX, double pointY, double centreX, double centreY, double playerX, double playerY,
                                      double scale, double rotation, int compassLeft, int compassTop, int compassWidth, int compassHeight,
                                      double uiScale, out int screenX, out int screenY) {
            screenX = screenY = 0;
            if (scale <= 0 || compassWidth <= 0 || compassHeight <= 0) return false;

            double dx = (pointX - centreX) / scale;
            double dy = (pointY - centreY) / scale;

            if (rotation != 0) {
                double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
                double rx = dx * cos - dy * sin;
                double ry = dx * sin + dy * cos;
                dx = rx;
                dy = ry;
            }

            double cx = compassLeft + compassWidth / 2.0;
            double cy = compassTop + compassHeight / 2.0;
            double x  = cx + dx;
            double y  = cy + dy;

            double s      = uiScale / IconRecognizer.CALIBRATION_UI_SCALE;
            double margin = EDGE_MARGIN_PX * s + IconRecognizer.Reach(uiScale, IconSet.Compass);
            if (x < compassLeft + margin || y < compassTop + margin
             || x > compassLeft + compassWidth - margin || y > compassTop + compassHeight - margin) {
                return false;
            }

            double px = (playerX - centreX) / scale, py = (playerY - centreY) / scale;
            if (rotation != 0) {
                double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
                double rx = px * cos - py * sin;
                double ry = px * sin + py * cos;
                px = rx;
                py = ry;
            }

            // A circle, not a square: you stand on a hero challenge or heart when it completes, and a square of this size
            // also skipped icons diagonally off the marker that it does not cover (measured 2026-10-25: an icon 29 px
            // away, 25 px below, stayed unread). The white marker is about 18 px across and cannot make an open icon look
            // done, so the circle adds no false-tick risk.
            double mx = dx - px, my = dy - py, radius = PLAYER_MARKER_RADIUS_PX * s;
            if (mx * mx + my * my < radius * radius) return false;

            screenX = (int)Math.Round(x);
            screenY = (int)Math.Round(y);
            return true;
        }

    }

}
