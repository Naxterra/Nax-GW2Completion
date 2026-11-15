using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;

namespace Nax.Completion {

    /// <summary>
    /// Reads the game's world map icons once the map has stood still and reports which open points the game shows as
    /// done. A settled view is refreshed at a bounded interval, including after a failed capture or an occluded
    /// icon, so moving a tooltip away does not require panning the map. The screen around the icons is copied
    /// in one go and classified on a background thread, and the result is only used if the map has not moved at all
    /// since the read started.
    /// </summary>
    internal sealed class MapRecognizer {

        public sealed class Job {
            public int       MapId;     // the map the point belongs to; the view can show several maps
            public string    Key;
            public PointKind Kind;
            public int       ScreenX;   // physical pixels, relative to the game window's client area
            public int       ScreenY;
            public bool      HeroTooltip; // the explicit completion title of one unambiguous hovered hero
        }

        private static readonly Logger Logger = Logger.GetLogger<MapRecognizer>();

        // A permanent failure is logged again now and then, so it stays visible in the log.
        private static readonly TimeSpan FAILURE_LOG_INTERVAL = TimeSpan.FromMinutes(5);

        // Mumble reports the map centre and zoom while you drag, so the map counts as still once they stop changing.
        private static readonly TimeSpan SETTLE_TIME = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan READ_INTERVAL = TimeSpan.FromSeconds(2);

        private string   _view;
        private int      _generation;    // counts view changes, so A, B, A during a read is still noticed
        private DateTime _viewSinceUtc;
        private DateTime _nextReadUtc = DateTime.MinValue;
        private string   _tooltipContext;
        private int      _tooltipGeneration;
        private DateTime _tooltipSinceUtc;

        private Task<List<Job>>    _running;
        private int                _runningGeneration;
        private DateTime           _lastFailureLogUtc = DateTime.MinValue;
        private int                _runningTick;
        private int                _runningTooltipGeneration;

        /// <summary>Forgets everything, for example when the map closes. A read still running is ignored.</summary>
        public void Reset() {
            _view        = null;
            _nextReadUtc = DateTime.MinValue;
            _running     = null;
            _generation++;
            _tooltipContext = null;
            _tooltipSinceUtc = DateTime.MinValue;
            _tooltipGeneration++;
        }

        /// <summary>
        /// Call once per frame while the overlay shows the current map. Returns the jobs read as filled when a read of
        /// the still-current view has finished, otherwise null. <paramref name="buildJobs"/> returns null when the
        /// view cannot be read yet (it is then tried again).
        /// </summary>
        public List<Job> Update(string view, int mumbleTick, bool mayStart, Func<List<Job>> buildJobs, double uiScale,
                                string tooltipContext = null) {
            var now = DateTime.UtcNow;
            // Moving the pointer changes which tooltip may be read, not the map's geometry. Keep its generation
            // separate so title evidence can be dropped without starving ordinary icon recognition.
            if (tooltipContext != _tooltipContext) {
                _tooltipContext = tooltipContext;
                _tooltipSinceUtc = now;
                _tooltipGeneration++;
            }
            if (view != _view) {
                _view         = view;
                _viewSinceUtc = now;
                _nextReadUtc  = DateTime.MinValue;
                _generation++;
            }

            if (_running != null) {
                if (!_running.IsCompleted) return null;

                // The game must have ticked at least twice since the read started, otherwise the picture may be a
                // loading screen (which freezes Mumble). A result from an unchanged view keeps waiting for that.
                if (mumbleTick - _runningTick < 2 && _runningGeneration == _generation) return null;

                var task = _running;
                int ran  = _runningGeneration;
                _running = null;

                if (task.IsFaulted || task.IsCanceled) {
                    _nextReadUtc = now + READ_INTERVAL;
                    if (DateTime.UtcNow - _lastFailureLogUtc > FAILURE_LOG_INTERVAL) {
                        _lastFailureLogUtc = DateTime.UtcNow;
                        Logger.Warn(task.Exception?.GetBaseException(), "Reading the world map icons failed.");
                    }
                    return null;
                }

                if (ran == _generation) {
                    return _runningTooltipGeneration == _tooltipGeneration
                        ? task.Result
                        : task.Result.Where(job => !job.HeroTooltip).ToList();
                }

                // The map moved during the read. Read this view again once it has settled, even if it is the view
                // the read started from (moved away and back).
                return null;
            }

            if (!mayStart || now < _nextReadUtc || now - _viewSinceUtc < SETTLE_TIME) return null;

            var jobs = buildJobs();
            if (jobs == null) return null;
            if (tooltipContext != null && now - _tooltipSinceUtc < SETTLE_TIME && jobs.Any(job => job.HeroTooltip)) {
                jobs = jobs.Where(job => !job.HeroTooltip).ToList();
                // A title has not appeared yet. Retry when the pointer settles without using the polling interval.
                if (jobs.Count == 0) return null;
            }

            _nextReadUtc = now + READ_INTERVAL;
            if (jobs.Count == 0) return null;

            _runningGeneration = _generation;
            _runningTooltipGeneration = _tooltipGeneration;
            _runningTick       = mumbleTick;
            _running           = Task.Run(() => Read(jobs, uiScale));
            return null;
        }

        private static List<Job> Read(List<Job> jobs, double uiScale) {
            int reach  = IconRecognizer.Reach(uiScale);
            int left   = jobs.Min(j => j.ScreenX) - reach;
            int top    = jobs.Min(j => j.ScreenY) - reach;
            int right  = jobs.Max(j => j.ScreenX) + reach;
            int bottom = jobs.Max(j => j.ScreenY) + reach;
            if (jobs.Any(j => j.HeroTooltip)) {
                if (!ScreenCapture.TryGetClientSize(out int clientWidth, out int clientHeight)) {
                    throw new InvalidOperationException("The game window is not available.");
                }
                foreach (var job in jobs.Where(j => j.HeroTooltip)) {
                    HeroTooltipRecognizer.Bounds(job.ScreenX, job.ScreenY, uiScale, clientWidth, clientHeight,
                                                 out int jl, out int jt, out int jr, out int jb);
                    left = Math.Min(left, jl); top = Math.Min(top, jt);
                    right = Math.Max(right, jr); bottom = Math.Max(bottom, jb);
                }
            }
            int width = right - left + 1, height = bottom - top + 1;

            int[] pixels = ScreenCapture.Capture(left, top, width, height);
            return ReadPixels(jobs, pixels, width, height, left, top, uiScale);
        }

        internal static List<Job> ReadPixels(List<Job> jobs, int[] pixels, int width, int height,
                                             int left, int top, double uiScale) {
            var filled = new List<Job>();
            foreach (var job in jobs) {
                bool done = job.HeroTooltip
                    ? job.Kind == PointKind.HeroChallenge && HeroTooltipRecognizer.IsEarned(pixels, width, height, job.ScreenX - left, job.ScreenY - top, uiScale)
                    : IconRecognizer.IsFilled(pixels, width, height, job.ScreenX - left, job.ScreenY - top, job.Kind, uiScale);
                if (done) {
                    filled.Add(job);
                }
            }

            return filled;
        }

    }

}
