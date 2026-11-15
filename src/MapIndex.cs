using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Nax.Completion {

    /// <summary>One public Tyria map and its outline in continent units.</summary>
    public sealed class MapOutline {
        [JsonProperty("id")]     public int    Id     { get; set; }
        [JsonProperty("name")]   public string Name   { get; set; }
        [JsonProperty("left")]   public double Left   { get; set; }
        [JsonProperty("top")]    public double Top    { get; set; }
        [JsonProperty("right")]  public double Right  { get; set; }
        [JsonProperty("bottom")] public double Bottom { get; set; }
    }

    /// <summary>
    /// The public Tyria maps that have completion points (data\map_index.json, embedded), so the world map can be read
    /// for every map in view and not only the one you stand on. A map released after the index was built is still
    /// read while you are on it.
    /// </summary>
    public static class MapIndex {

        private const string RESOURCE = "Nax.Completion.map_index.json";

        private sealed class FileModel {
            [JsonProperty("maps")] public List<MapOutline> Maps { get; set; } = new List<MapOutline>();
        }

        private static readonly Lazy<IReadOnlyList<MapOutline>> All = new Lazy<IReadOnlyList<MapOutline>>(Load);

        public static IReadOnlyList<MapOutline> Maps => All.Value;

        public static bool Contains(int mapId) => All.Value.Any(m => m.Id == mapId);

        /// <summary>The maps whose outline overlaps the given rectangle in continent units.</summary>
        public static List<int> InView(double left, double top, double right, double bottom) {
            return All.Value.Where(m => m.Left < right && m.Right > left && m.Top < bottom && m.Bottom > top)
                      .Select(m => m.Id)
                      .ToList();
        }

        private static IReadOnlyList<MapOutline> Load() {
            using (var stream = typeof(MapIndex).Assembly.GetManifestResourceStream(RESOURCE)) {
                if (stream == null) return new List<MapOutline>();

                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    var model = JsonConvert.DeserializeObject<FileModel>(reader.ReadToEnd());
                    return model?.Maps?.Where(m => m.Id > 0 && m.Right > m.Left && m.Bottom > m.Top).ToList() ?? new List<MapOutline>();
                }
            }
        }

    }

}
