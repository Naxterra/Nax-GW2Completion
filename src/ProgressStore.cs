using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Blish_HUD;
using Newtonsoft.Json;

namespace Nax.Completion {

    /// <summary>
    /// Per-character record of which completion points are done. The game API does not expose this,
    /// so the module keeps its own record: one JSON file per character in the module's data folder.
    /// </summary>
    public sealed class ProgressStore {

        private static readonly Logger Logger = Logger.GetLogger<ProgressStore>();

        private sealed class FileModel {
            [JsonProperty("maps")]
            public Dictionary<string, List<string>> Maps { get; set; } = new Dictionary<string, List<string>>();

            // Points you unticked yourself; map icon recognition never ticks them again.
            [JsonProperty("keepOpen")]
            public List<string> KeepOpen { get; set; } = new List<string>();
        }

        private readonly string _directory;

        private readonly Dictionary<int, HashSet<string>> _done = new Dictionary<int, HashSet<string>>();
        private readonly HashSet<string>                  _keepOpen = new HashSet<string>(StringComparer.Ordinal);

        public string CharacterName { get; private set; }

        public ProgressStore(string directory) {
            _directory = directory;
        }

        /// <summary>
        /// True when the character's file exists but could not be read (locked by antivirus or cloud sync, or
        /// corrupt and impossible to move aside). Saving is then disabled so the original file is never overwritten.
        /// </summary>
        public bool SavingDisabled { get; private set; }

        public void LoadCharacter(string characterName) {
            _done.Clear();
            _keepOpen.Clear();
            this.CharacterName  = characterName;
            this.SavingDisabled = false;

            if (string.IsNullOrEmpty(characterName)) return;

            string path = GetPath(characterName);
            if (!File.Exists(path)) return;

            string json;
            try {
                json = File.ReadAllText(path, Encoding.UTF8);
            } catch (Exception ex) {
                // Most likely a temporary lock. Do not touch the file; just stop saving until the next load.
                Logger.Warn(ex, "Could not read progress file {path}; saving is disabled for this character until it can be read.", path);
                this.SavingDisabled = true;
                return;
            }

            try {
                var model = JsonConvert.DeserializeObject<FileModel>(json) ?? new FileModel();

                foreach (var entry in model.Maps ?? new Dictionary<string, List<string>>()) {
                    if (!int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId)) continue;
                    _done[mapId] = new HashSet<string>(entry.Value ?? new List<string>(), StringComparer.Ordinal);
                }

                foreach (string key in model.KeepOpen ?? new List<string>()) {
                    if (!string.IsNullOrEmpty(key)) _keepOpen.Add(key);
                }
            } catch (JsonException ex) {
                // Corrupt content. Move it aside so it can be inspected; only start fresh if that worked.
                _done.Clear();
                _keepOpen.Clear();
                if (TryBackup(path)) {
                    Logger.Warn(ex, "Progress file {path} was corrupt; it was renamed and a fresh one will be started.", path);
                } else {
                    Logger.Warn(ex, "Progress file {path} is corrupt and could not be renamed; saving is disabled.", path);
                    this.SavingDisabled = true;
                }
            }
        }

        public bool IsDone(int mapId, string key) {
            return _done.TryGetValue(mapId, out var keys) && keys.Contains(key);
        }

        /// <summary>Returns true when the state actually changed.</summary>
        public bool SetDone(int mapId, string key, bool done) {
            if (string.IsNullOrEmpty(this.CharacterName)) return false;

            if (!_done.TryGetValue(mapId, out var keys)) {
                keys = new HashSet<string>(StringComparer.Ordinal);
                _done[mapId] = keys;
            }

            bool changed = done ? keys.Add(key) : keys.Remove(key);
            if (changed) Save();
            return changed;
        }

        /// <summary>Marks many points at once with a single save.</summary>
        public int SetManyDone(int mapId, IEnumerable<string> keysToSet) {
            if (string.IsNullOrEmpty(this.CharacterName)) return 0;

            if (!_done.TryGetValue(mapId, out var keys)) {
                keys = new HashSet<string>(StringComparer.Ordinal);
                _done[mapId] = keys;
            }

            int added = keysToSet.Count(keys.Add);
            if (added > 0) Save();
            return added;
        }

        /// <summary>True for a point you unticked yourself, which recognition must leave alone. Keys are unique across maps.</summary>
        public bool IsKeptOpen(string key) {
            return _keepOpen.Contains(key);
        }

        public void KeepOpen(IEnumerable<string> keys) {
            if (string.IsNullOrEmpty(this.CharacterName)) return;
            if (keys.Count(_keepOpen.Add) > 0) Save();
        }

        /// <summary>Lets recognition tick these points again, for example after you ticked them yourself.</summary>
        public void ReleaseKeepOpen(IEnumerable<string> keys) {
            if (string.IsNullOrEmpty(this.CharacterName)) return;
            if (keys.Count(_keepOpen.Remove) > 0) Save();
        }

        public int ClearMap(int mapId) {
            if (!_done.TryGetValue(mapId, out var keys) || keys.Count == 0) return 0;

            int count = keys.Count;
            keys.Clear();
            Save();
            return count;
        }

        private void Save() {
            if (string.IsNullOrEmpty(this.CharacterName) || this.SavingDisabled) return;

            var model = new FileModel {
                Maps = _done.Where(kv => kv.Value.Count > 0)
                            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                                          kv => kv.Value.OrderBy(k => k, StringComparer.Ordinal).ToList()),
                KeepOpen = _keepOpen.OrderBy(k => k, StringComparer.Ordinal).ToList()
            };

            string path    = GetPath(this.CharacterName);
            string tmpPath = path + ".tmp";

            try {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(model, Formatting.Indented), new UTF8Encoding(false));

                // Write to a temp file first so a crash mid-write cannot leave a half-written progress file.
                if (File.Exists(path)) {
                    File.Replace(tmpPath, path, null);
                } else {
                    File.Move(tmpPath, path);
                }
            } catch (Exception ex) {
                Logger.Warn(ex, "Could not save progress file {path}.", path);
            }
        }

        // Windows rules, stated explicitly so the result does not depend on the OS the code runs on.
        private const string INVALID_FILE_CHARS = "\\/:*?\"<>|";

        private string GetPath(string characterName) {
            var safe = new StringBuilder(characterName.Length);
            foreach (char c in characterName) {
                safe.Append(c < 32 || INVALID_FILE_CHARS.IndexOf(c) >= 0 ? '_' : c);
            }

            // CON, PRN, AUX, NUL, COM1..9, LPT1..9 are device names on Windows and cannot be file names.
            string name = safe.ToString();
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL"
             || (upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT")) && char.IsDigit(upper[3]))) {
                name += "_";
            }

            return Path.Combine(_directory, name + ".json");
        }

        private static bool TryBackup(string path) {
            try {
                string backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(path, backup);
                return true;
            } catch (Exception ex) {
                Logger.Warn(ex, "Could not rename broken progress file {path}.", path);
                return false;
            }
        }

    }

}
