using System;
using System.Collections.Generic;

namespace Nax.Completion {

    /// <summary>
    /// The ticks the undo hotkey can take back. Undo only corrects what just happened on the map you are on: entries
    /// older than the window and entries of other maps are never unwound, and presses closer together than the
    /// repeat guard count once, so a held or repeating key undoes one step. On 2026-10-25 a burst of undo presses
    /// without these limits unticked points ticked two hours earlier on another map.
    /// </summary>
    internal sealed class UndoHistory {

        public static readonly TimeSpan WINDOW       = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan REPEAT_GUARD = TimeSpan.FromSeconds(1.5);
        public const int                MAX_ENTRIES  = 50;

        internal sealed class Entry {
            public int          MapId;
            public List<string> Keys;
            public bool         Recognised;   // ticked from the map icons, so undoing it means "the read was wrong"
            public DateTime     CreatedUtc;
        }

        private readonly List<Entry> _entries  = new List<Entry>();
        private DateTime             _lastTake = DateTime.MinValue;

        public int Count => _entries.Count;

        public void Push(int mapId, List<string> keys, bool recognised, DateTime nowUtc) {
            _entries.Add(new Entry { MapId = mapId, Keys = keys, Recognised = recognised, CreatedUtc = nowUtc });
            if (_entries.Count > MAX_ENTRIES) _entries.RemoveAt(0);
        }

        /// <summary>
        /// Removes and returns the newest entry of this map from the last two minutes. Returns null when a press comes
        /// within the repeat guard of the previous press, accepted or not (<paramref name="repeat"/> is then true), or
        /// when nothing qualifies.
        /// </summary>
        public Entry Take(int mapId, DateTime nowUtc, out bool repeat) {
            // Every press restarts the guard, including rejected ones, so a steady stream of presses undoes one step.
            repeat = nowUtc - _lastTake < REPEAT_GUARD && nowUtc >= _lastTake;
            _lastTake = nowUtc;
            if (repeat) return null;

            // An entry stamped after "now" means the clock went back. Dropping it keeps the window a hard limit.
            _entries.RemoveAll(e => nowUtc - e.CreatedUtc > WINDOW || e.CreatedUtc > nowUtc);
            int index = _entries.FindLastIndex(e => e.MapId == mapId);
            if (index < 0) return null;

            var entry = _entries[index];
            _entries.RemoveAt(index);
            return entry;
        }

        /// <summary>Forgets one point, for example after it was unticked by hand, so undo does not touch it again.</summary>
        public void Forget(int mapId, string key) {
            foreach (var entry in _entries) {
                if (entry.MapId == mapId) entry.Keys.Remove(key);
            }
            _entries.RemoveAll(e => e.Keys.Count == 0);
        }

        public void ClearMap(int mapId) {
            _entries.RemoveAll(e => e.MapId == mapId);
        }

        public void Clear() {
            _entries.Clear();
            _lastTake = DateTime.MinValue;
        }

    }

}
