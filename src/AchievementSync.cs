using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Modules.Managers;
using Gw2Sharp.WebApi.V2.Models;

namespace Nax.Completion {

    /// <summary>
    /// Asks the GW2 API which achievements the account has completed (/v2/account/achievements). Every jumping puzzle
    /// has an achievement that completes when you open its chest, so this ticks puzzles you finished, also ones done
    /// before the module or on another character. Needs the "account" and "progression" permissions.
    /// All fields are read and written on the update loop only. The background request just returns its answer, which
    /// the next <see cref="Update"/> applies, so a character or account change can discard an answer still in flight.
    /// </summary>
    internal sealed class AchievementSync {

        private static readonly Logger Logger = Logger.GetLogger<AchievementSync>();

        // The API caches account data for a few minutes, so asking more often gains nothing.
        private static readonly TimeSpan REFRESH_INTERVAL = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan RETRY_INTERVAL   = TimeSpan.FromMinutes(1);

        private static readonly TokenPermission[] NEEDED = { TokenPermission.Account, TokenPermission.Progression };

        private readonly Gw2ApiManager _api;

        private HashSet<int>       _done = new HashSet<int>();
        private DateTime           _nextRequestUtc = DateTime.MinValue;
        private Task<HashSet<int>> _running;
        private int                _generation;
        private int                _runningGeneration;
        private bool               _permissionWarned;
        private bool               _synced;

        /// <summary>Set to true when the list changed. The module clears it after applying.</summary>
        public bool Changed;

        public AchievementSync(Gw2ApiManager api) {
            _api = api;
        }

        public bool Available => _api != null && _api.HasPermissions(NEEDED);

        public bool IsDone(int achievementId) {
            return achievementId > 0 && _done.Contains(achievementId);
        }

        /// <summary>Asks again soon, for example after a map change, where a puzzle may have been finished.</summary>
        public void RequestSoon() {
            _nextRequestUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Forgets everything known, for a character change: the new character may be on another account, and its
        /// puzzles must never be ticked from the old account's achievements. An answer still in flight is discarded.
        /// </summary>
        public void Reset() {
            _done           = new HashSet<int>();
            _nextRequestUtc = DateTime.MinValue;
            _generation++;
            _synced         = false;
        }

        /// <summary>The ids of the done achievements in an API answer.</summary>
        internal static HashSet<int> DoneIds(IEnumerable<AccountAchievement> achievements) {
            return new HashSet<int>((achievements ?? Enumerable.Empty<AccountAchievement>()).Where(a => a != null && a.Done).Select(a => a.Id));
        }

        /// <summary>Call from the update loop. Applies a finished request and starts a new one when one is due.</summary>
        public void Update() {
            if (_running != null) {
                if (!_running.IsCompleted) return;
                Apply(_running, _runningGeneration);
                _running = null;
            }

            if (DateTime.UtcNow < _nextRequestUtc) return;

            if (!this.Available) {
                if (!_permissionWarned) {
                    _permissionWarned = true;
                    Logger.Info("Jumping puzzle achievements are not synced from the API: the module has no key with the account and progression permissions.");
                }
                _nextRequestUtc = DateTime.UtcNow + RETRY_INTERVAL;
                return;
            }

            _permissionWarned  = false;
            _nextRequestUtc    = DateTime.UtcNow + REFRESH_INTERVAL;
            _runningGeneration = _generation;
            var api = _api;
            _running = Task.Run(async () => DoneIds(await api.Gw2ApiClient.V2.Account.Achievements.GetAsync().ConfigureAwait(false)));
        }

        private void Apply(Task<HashSet<int>> task, int generation) {
            if (generation != _generation) return;   // the character changed while the request ran

            if (task.IsFaulted || task.IsCanceled) {
                Logger.Warn(task.Exception?.GetBaseException(), "Could not read completed achievements from the API.");
                _nextRequestUtc = DateTime.UtcNow + RETRY_INTERVAL;
                return;
            }

            var done = task.Result;
            if (!_synced) {
                _synced = true;
                Logger.Info("Synced {count} completed achievements from the API.", done.Count);
            }

            if (!done.SetEquals(_done)) {
                _done   = done;
                Changed = true;
            }
        }

    }

}
