using System.Collections.Generic;
using System.Linq;

namespace Nax.Completion {

    /// <summary>
    /// Counts, for each map in one still world map view, how many points were in view, already ticked, read and
    /// shown as done, and why the others were skipped. The module logs it once per distinct result, so the log
    /// shows whether the points of a neighbouring map were read at all, and if not, what stopped them.
    /// </summary>
    internal sealed class MapReadLog {

        private sealed class MapCounts {
            public int InView, AlreadyDone, Read;
            public readonly List<string>            Reasons = new List<string>();   // in the order first seen
            public readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();
        }

        private readonly List<int>                 _order = new List<int>();
        private readonly Dictionary<int, MapCounts> _maps = new Dictionary<int, MapCounts>();

        public void AlreadyDone(int mapId) {
            var counts = Get(mapId);
            counts.InView++;
            counts.AlreadyDone++;
        }

        public void Skipped(int mapId, string reason) {
            var counts = Get(mapId);
            counts.InView++;
            if (!counts.Skipped.ContainsKey(reason)) {
                counts.Skipped[reason] = 0;
                counts.Reasons.Add(reason);
            }
            counts.Skipped[reason]++;
        }

        public void Read(int mapId) {
            var counts = Get(mapId);
            counts.InView++;
            counts.Read++;
        }

        /// <summary>
        /// One line for the log, maps in the order they were first counted (the map you are on first). Pass the map of
        /// every point that was read as done, or nothing when no read ran.
        /// </summary>
        public string Describe(IEnumerable<int> filledMapIds = null) {
            var filled = (filledMapIds ?? Enumerable.Empty<int>()).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            if (_order.Count == 0) return "no tracked points in view";

            return string.Join(" | ", _order.Select(mapId => {
                var counts = _maps[mapId];
                filled.TryGetValue(mapId, out int done);
                string line = $"map {mapId}: {counts.InView} in view, {counts.AlreadyDone} already ticked, {counts.Read} read, {done} of them shown as done";
                if (counts.Reasons.Count > 0) {
                    line += ", skipped " + string.Join(", ", counts.Reasons.Select(r => $"{counts.Skipped[r]} {r}"));
                }
                return line;
            }));
        }

        private MapCounts Get(int mapId) {
            if (!_maps.TryGetValue(mapId, out var counts)) {
                counts = new MapCounts();
                _maps[mapId] = counts;
                _order.Add(mapId);
            }
            return counts;
        }

    }

}
