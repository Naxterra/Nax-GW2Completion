using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Blish_HUD;
using Gw2Sharp.WebApi;
using Gw2Sharp.WebApi.Exceptions;
using Gw2Map            = Gw2Sharp.WebApi.V2.Models.Map;
using Gw2RegionMap      = Gw2Sharp.WebApi.V2.Models.ContinentFloorRegionMap;
using Gw2PoiType        = Gw2Sharp.WebApi.V2.Models.PoiType;

namespace Nax.Completion {

    /// <summary>Pulls the completion objectives of a map from the public GW2 API (no API key needed).</summary>
    public sealed class MapDataLoader {

        private static readonly Logger Logger = Logger.GetLogger<MapDataLoader>();

        private const double INCH_IN_METERS         = 0.0254;
        private const double FALLBACK_INCH_PER_UNIT = 24.0;

        private readonly Func<IGw2WebApiClient> _clientProvider;

        private readonly Dictionary<int, MapCompletionData> _cache     = new Dictionary<int, MapCompletionData>();
        private readonly object                             _cacheLock = new object();

        public MapDataLoader(Func<IGw2WebApiClient> clientProvider) {
            _clientProvider = clientProvider;
        }

        /// <summary>
        /// Loads and caches the map. Any error other than "this floor does not have the map" (404) is thrown,
        /// so the caller can retry later instead of being stuck with partial data.
        /// </summary>
        public async Task<MapCompletionData> LoadAsync(int mapId) {
            lock (_cacheLock) {
                if (_cache.TryGetValue(mapId, out var cached)) return cached;
            }

            var client = _clientProvider();
            Gw2Map map = await client.V2.Maps.GetAsync(mapId).ConfigureAwait(false);

            // The same map is exposed once per floor. Points can live on different floors (caves, upper levels),
            // so merge every floor of the map, starting with the default one, and de-duplicate by key.
            var floors = new List<int> { map.DefaultFloor };
            foreach (int floor in map.Floors) {
                if (!floors.Contains(floor)) floors.Add(floor);
            }

            var requests = floors.Select(floor => LoadFloorAsync(client, map, floor)).ToList();
            Gw2RegionMap[] results = await Task.WhenAll(requests).ConfigureAwait(false);

            // Merge in floor order (default floor first), so a point present on several floors keeps the default
            // floor's entry and is marked as drawn on the default floor.
            var points = new Dictionary<string, CompletionPoint>(StringComparer.Ordinal);
            bool anyFloorLoaded = false;
            for (int i = 0; i < results.Length; i++) {
                var regionMap = results[i];
                if (regionMap == null) continue;
                anyFloorLoaded = true;
                AddPoints(regionMap, points, onDefaultFloor: i == 0);
            }

            if (!anyFloorLoaded) {
                throw new InvalidOperationException($"No floor data found for map {mapId}.");
            }

            double inchPerUnit = FALLBACK_INCH_PER_UNIT;
            if (map.ContinentRect.Width > 0 && map.MapRect.Width > 0) {
                inchPerUnit = map.MapRect.Width / map.ContinentRect.Width;
            }

            var data = new MapCompletionData(mapId,
                                             map.Name,
                                             inchPerUnit * INCH_IN_METERS,
                                             points.Values.ToList());

            lock (_cacheLock) {
                _cache[mapId] = data;
            }

            Logger.Info("Loaded {count} completion points for map {mapId} ({name}).", data.Points.Count, mapId, map.Name);
            return data;
        }

        /// <summary>Returns null when this floor simply does not carry the map. Other errors propagate.</summary>
        private static async Task<Gw2RegionMap> LoadFloorAsync(IGw2WebApiClient client, Gw2Map map, int floor) {
            try {
                return await client.V2.Continents[map.ContinentId]
                                   .Floors[floor]
                                   .Regions[map.RegionId]
                                   .Maps[map.Id]
                                   .GetAsync()
                                   .ConfigureAwait(false);
            } catch (NotFoundException) {
                return null;
            }
        }

        private static void AddPoints(Gw2RegionMap regionMap, Dictionary<string, CompletionPoint> points, bool onDefaultFloor) {
            foreach (var poi in regionMap.PointsOfInterest.Values) {
                PointKind kind;
                string    prefix;

                if (poi.Type.IsUnknown) continue;

                switch (poi.Type.Value) {
                    case Gw2PoiType.Waypoint:
                        kind   = PointKind.Waypoint;
                        prefix = "wp";
                        break;
                    case Gw2PoiType.Landmark:
                        kind   = PointKind.PointOfInterest;
                        prefix = "poi";
                        break;
                    case Gw2PoiType.Vista:
                        kind   = PointKind.Vista;
                        prefix = "vista";
                        break;
                    default:
                        // "unlock" entries (dungeon entrances and similar) do not count toward map completion.
                        continue;
                }

                string key = prefix + ":" + poi.Id.ToString(CultureInfo.InvariantCulture);
                if (points.ContainsKey(key)) continue;

                string name = string.IsNullOrWhiteSpace(poi.Name) ? null : poi.Name.Trim();
                if (!points.ContainsKey(key)) points[key] = new CompletionPoint(key, kind, name, poi.Coord.X, poi.Coord.Y, null, onDefaultFloor);
            }

            foreach (var task in regionMap.Tasks.Values) {
                string key = "heart:" + task.Id.ToString(CultureInfo.InvariantCulture);
                if (points.ContainsKey(key)) continue;

                string name = string.IsNullOrWhiteSpace(task.Objective) ? null : task.Objective.Trim();
                if (!points.ContainsKey(key)) points[key] = new CompletionPoint(key, PointKind.Heart, name, task.Coord.X, task.Coord.Y, null, onDefaultFloor);
            }

            foreach (var challenge in regionMap.SkillChallenges) {
                // Some hero challenges come without an id; fall back to their rounded position so the key stays stable.
                string id = string.IsNullOrWhiteSpace(challenge.Id)
                                ? string.Format(CultureInfo.InvariantCulture, "{0:0}_{1:0}", challenge.Coord.X, challenge.Coord.Y)
                                : challenge.Id;

                string key = "hc:" + id;
                if (points.ContainsKey(key)) continue;

                if (!points.ContainsKey(key)) points[key] = new CompletionPoint(key, PointKind.HeroChallenge, null, challenge.Coord.X, challenge.Coord.Y, null, onDefaultFloor);
            }
        }

    }

}
