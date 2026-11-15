using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Modules.Managers;
using Gw2Sharp.WebApi.V2.Models;

namespace Nax.Completion {

    /// <summary>
    /// Asks the GW2 API which hero challenges a character has completed (/v2/characters/{name}/heropoints). Needs the
    /// "characters" and "progression" permissions of the API key Blish HUD holds. Since 2023 ArenaNet's endpoint returns
    /// an empty list for every character (gw2-api/issues#56), so hero challenges are read from the map icons instead,
    /// and this only takes effect if ArenaNet fixes the endpoint.
    /// All fields are read and written on the update loop only. The background request just returns its answer, which
    /// the next <see cref="Update"/> applies, so an answer for a previous character is discarded.
    /// </summary>
    internal sealed class HeroPointSync {

        private static readonly Logger Logger = Logger.GetLogger<HeroPointSync>();

        // The API itself caches character data for a few minutes, so asking more often gains nothing.
        private static readonly TimeSpan REFRESH_INTERVAL = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan RETRY_INTERVAL   = TimeSpan.FromMinutes(1);

        private static readonly TokenPermission[] NEEDED = { TokenPermission.Characters, TokenPermission.Progression };

        private readonly Gw2ApiManager _api;

        private string                _character;
        private HashSet<string>       _done = new HashSet<string>(StringComparer.Ordinal);
        private DateTime              _nextRequestUtc = DateTime.MinValue;
        private Task<HashSet<string>> _running;
        private string                _runningCharacter;
        private bool                  _permissionWarned;
        private bool                  _synced;

        /// <summary>Set to true when the list changed. The module clears it after applying.</summary>
        public bool Changed;

        public HeroPointSync(Gw2ApiManager api) {
            _api = api;
        }

        public bool Available => _api != null && _api.HasPermissions(NEEDED);

        /// <summary>Hero challenge ids (the API's "0-14" form) the current character has completed.</summary>
        public bool IsDone(string heroPointId) {
            return _done.Contains(heroPointId);
        }

        public void SetCharacter(string name) {
            if (name == _character) return;

            _character      = name;
            _done           = new HashSet<string>(StringComparer.Ordinal);
            _nextRequestUtc = DateTime.MinValue;
            _synced         = false;
        }

        /// <summary>Asks again soon, for example after a map change, where new challenges may have been done.</summary>
        public void RequestSoon() {
            _nextRequestUtc = DateTime.MinValue;
        }

        /// <summary>Call from the update loop. Applies a finished request and starts a new one when one is due.</summary>
        public void Update() {
            if (_running != null) {
                if (!_running.IsCompleted) return;
                Apply(_running, _runningCharacter);
                _running = null;
            }

            if (string.IsNullOrEmpty(_character) || DateTime.UtcNow < _nextRequestUtc) return;

            if (!this.Available) {
                if (!_permissionWarned) {
                    _permissionWarned = true;
                    Logger.Info("Hero challenges are not synced from the API: the module has no key with the characters and progression permissions.");
                }
                _nextRequestUtc = DateTime.UtcNow + RETRY_INTERVAL;
                return;
            }

            _permissionWarned = false;
            _nextRequestUtc   = DateTime.UtcNow + REFRESH_INTERVAL;
            _runningCharacter = _character;
            string character  = _character;
            var    api        = _api;
            _running = Task.Run(async () => new HashSet<string>(await api.Gw2ApiClient.V2.Characters[character].HeroPoints.GetAsync().ConfigureAwait(false),
                                                                StringComparer.Ordinal));
        }

        private void Apply(Task<HashSet<string>> task, string character) {
            if (character != _character) return;   // the character changed while the request ran

            if (task.IsFaulted || task.IsCanceled) {
                Logger.Warn(task.Exception?.GetBaseException(), "Could not read completed hero challenges for {character} from the API.", character);
                _nextRequestUtc = DateTime.UtcNow + RETRY_INTERVAL;
                return;
            }

            var set = task.Result;
            if (!_synced) {
                _synced = true;
                Logger.Info("Synced {count} completed hero challenges for {character} from the API: {ids}", set.Count, character,
                            string.Join(" ", set.Take(12)));
            }

            if (!set.SetEquals(_done)) {
                _done   = set;
                Changed = true;
            }
        }

    }

}
