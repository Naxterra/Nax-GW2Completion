using System;

namespace Nax.Completion {

    /// <summary>
    /// Reads the explicit English completed-hero tooltip when the pointer covers the icon. The full title must
    /// match, including Earned; the shorter incomplete title cannot pass. Call only for one unambiguous hovered
    /// hero challenge, in a settled world-map view with the same pointer position as the capture.
    /// </summary>
    internal static class HeroTooltipRecognizer {
        private const int LEFT = 260, RIGHT = 190, TOP = 140;
        private static readonly string[] TITLE = {
            "#.....#........................######.........###.................#######...............................#",
            "#.....#........................##...##........##.......###.......##.....................................#",
            "#.....#........................##....#.................##........##.....................................#",
            "#.....#..####..####..####......##....#..####..########.#####......#.......#####..####.#####..####...#####",
            "#######.##..##.##...##..##.....##...##.##..##..#.##.##..#.........#####.......#..##...##.##.##..##.##...#",
            "#.....#.#....#.#....#...##.....######..#....#.##.#..##..#.........#........####..#....#...#.#....#.#....#",
            "#.....#.######.#....#....#.....##.....##....#..#.#..##..#.........#.......##..#..#...##...#.######.#....#",
            "#.....#.#......#....#....#.....##......#....#..#.#..##..#........##......##...#..#....#...#.#......#....#",
            "#.....#.##.....#....##..##.....##......##..##..#.#..##..##........#.......#..##..#....#...#.##..##.##...#",
            "#.....#..####..#.....####......##.......####..##.#..##..####......#######.######.#....#...#..####...#####"
        };

        private static int Offset(int value, double scale) => (int)Math.Round(value * scale);

        internal static void Bounds(int x, int y, double uiScale, int width, int height,
                                    out int left, out int top, out int right, out int bottom) {
            double s = uiScale / IconRecognizer.CALIBRATION_UI_SCALE;
            left = Math.Max(0, x - Offset(LEFT, s));
            top = Math.Max(0, y - Offset(TOP, s));
            right = Math.Min(width - 1, x + Offset(RIGHT, s));
            bottom = Math.Min(height - 1, y);
        }

        internal static bool IsEarned(int[] pixels, int width, int height, int x, int y, double uiScale) {
            if (pixels == null || width <= 0 || height <= 0 || (long)width * height > pixels.Length || uiScale <= 0
             || double.IsNaN(uiScale) || double.IsInfinity(uiScale) || x < 0 || y < 0 || x >= width || y >= height) return false;

            double s = uiScale / IconRecognizer.CALIBRATION_UI_SCALE;
            int textWidth = Offset(TITLE[0].Length - 1, s) + 1, textHeight = Offset(TITLE.Length - 1, s) + 1;
            Bounds(x, y, uiScale, width, height, out int left, out int top, out int right, out int bottom);
            int lastX = Math.Min(right - textWidth + 1, x + Offset(80, s));
            int lastY = Math.Min(bottom - textHeight + 1, y - Offset(12, s));
            for (int ty = top; ty <= lastY; ty++) {
                for (int tx = left; tx <= lastX; tx++) {
                    // Reject most map pixels with one distinctive row before comparing the entire title.
                    if (!Matches(pixels, width, tx, ty, s, 4, 5, 0.7, 0.15)) continue;
                    if (Matches(pixels, width, tx, ty, s, 0, TITLE.Length, 0.84, 0.04)) return true;
                }
            }
            return false;
        }

        private static bool Matches(int[] pixels, int width, int x, int y, double scale,
                                    int firstRow, int lastRow, double minInk, double maxPaper) {
            int ink = 0, hit = 0, paper = 0, extra = 0;
            for (int row = firstRow; row < lastRow; row++) {
                int offset = (y + Offset(row, scale)) * width + x;
                for (int column = 0; column < TITLE[row].Length; column++) {
                    int p = pixels[offset + Offset(column, scale)];
                    int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                    bool white = r >= 190 && g >= 190 && b >= 170
                              && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 65;
                    if (TITLE[row][column] == '#') { ink++; if (white) hit++; }
                    else { paper++; if (white) extra++; }
                }
            }
            return hit >= ink * minInk && extra <= paper * maxPaper;
        }
    }
}
