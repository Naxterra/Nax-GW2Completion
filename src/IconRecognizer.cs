using System;

namespace Nax.Completion {

    /// <summary>
    /// Decides from screen pixels whether the game's world map icon for a point is filled (discovered or done).
    ///
    /// Calibrated on the owner's 3440x1440 screenshots with the game's Normal UI size, where Blish's UIScaleMultiplier
    /// is 0.897. Every distance below is in pixels at that scale and is scaled for other UI sizes. The projected
    /// position matches the icon within a pixel or two, so each check only searches a few pixels around it.
    ///  - Waypoint: cyan centre inside the tan diamond frame, and no other cyan around it. Hollow waypoints have a dark
    ///    grey centre. Other cyan icons (Black Citadel's chevrons, helmets) often cover waypoints. A covered waypoint
    ///    cannot be read, so cyan around the diamond makes it count as not filled.
    ///  - Heart: almost entirely gold centre with no gold around it. Hollow hearts are a gold outline with a dark
    ///    centre. Yellow foliage reaches about 0.6 and fails.
    ///  - Point of interest: orange centre in a white frame with no orange around it. Hollow ones have an orange-brown
    ///    frame and a dark centre. Orange event circles have no white frame and leave orange around.
    ///  - Vista: solid red triangles with no red around them. Hollow vistas have a dark centre.
    ///  - Hero challenge: a gold upward arrow with a thin dark border. A done one has a dark figure inside the arrow, an
    ///    open one a bright yellow centre (measured live in Diessa Plateau and Plains of Ashford on 2026-10-25).
    /// Points under map fog show no icon and read as not filled. A misread may only leave a point open, so every rule errs towards
    /// "not filled".
    /// </summary>
    /// <summary>Where the icon is drawn. The compass draws some icons smaller than the world map.</summary>
    internal enum IconSet {
        WorldMap,
        Compass
    }

    internal static class IconRecognizer {

        public const double CALIBRATION_UI_SCALE = 0.897;

        // Measured on the owner's screenshots (Black Citadel at 3440x1440, Wayfarer Foothills and Plains of Ashford
        // scaled up from 2000 px). Waypoints, filled: centre 0.32 to 0.66, tan frame 0.41 to 0.77, cyan around 0.00;
        // covered by a chevron: cyan around 0.05 to 0.10. Hearts, filled: centre 1.00, around 0.00 to 0.03; hollow 0.00;
        // foliage 0.63. POIs, filled: centre 0.56 to 1.0, white frame 0.28 to 0.46, around 0.00; event rings: around 0.18
        // to 0.67, white 0.00.
        private const int    WP_SEARCH       = 2;
        private const int    WP_CENTRE       = 5;     // Manhattan radius of the cyan centre
        private const double WP_MIN_CENTRE   = 0.25;
        private const int    WP_FRAME_INNER  = 10;    // Manhattan band of the tan diamond frame
        private const int    WP_FRAME_OUTER  = 12;
        private const double WP_MIN_FRAME    = 0.25;
        private const int    WP_AROUND_INNER = 8;     // Manhattan band that must hold no cyan at all
        private const int    WP_AROUND_OUTER = 16;
        private const double WP_MAX_AROUND   = 0.02;
        // World-map waypoint size changes with zoom. Check the measured size range with complete shape checks
        // at each candidate centre; the compass continues to use its original template.
        private const double WORLD_MAP_WAYPOINT_SCALE = 1.5;
        private const int WP_RIM_INNER = 6;
        private const int WP_RIM_OUTER = 7;
        private const double WP_MIN_DARK_RIM = 0.4;

        private const int    HEART_SEARCH       = 4;
        private const int    HEART_HALF         = 5;
        private const double HEART_MIN_CENTRE   = 0.85;
        private const int    HEART_AROUND_INNER = 12;
        private const int    HEART_AROUND_OUTER = 14;
        private const double HEART_MAX_AROUND   = 0.10;

        // The game fills a heart's icon from the bottom up as you make progress, so a heart at 85% looks solid gold in
        // the middle and only its upper part is still dark (the owner saw "Protect Redreave Mill" ticked at 85% on
        // 2026-10-25). A done heart's gold reaches the top of its lobes: measured on a real done heart, the gold is
        // 0.86 as tall as it is wide (18 by 21 px). The same heart filled only up to 4, 6 or 7 px above its centre
        // measures 0.62, 0.71 and 0.76, also on yellow ground, because only strict heart gold is counted. The measure
        // comes from the gold pixels themselves, so it does not depend on where the centre was found.
        private const int    HEART_SHAPE_RADIUS = 12;
        private const double HEART_MIN_HEIGHT   = 0.80;   // gold height / gold width

        // The compass draws hearts smaller (about 13x11 px of gold against 15 or more on the world map). Measured live:
        // a filled compass heart gives 0.96 in a 5x5 patch and 0.80 in 7x7. A hollow one has a dark centre.
        private const int    COMPASS_HEART_HALF         = 2;
        private const double COMPASS_HEART_MIN_CENTRE   = 0.9;
        private const int    COMPASS_HEART_AROUND_INNER = 8;
        private const int    COMPASS_HEART_AROUND_OUTER = 10;

        // Vista: two overlapping red triangles (about 13 px on the compass, larger on the world map) with a dark
        // outline. Filled ones are solid red, about (160, 55, 30); hollow ones are an outline with a dark centre. A
        // solid shape has red pixels with red on all four sides (measured: 17 on the compass, 76 on the world map),
        // an outline has none (measured: 0). The icon is drawn on top of the map, so foliage of the same red cannot
        // show through a hollow icon; the band and total checks only reject a large red area at a misprojected spot.
        private const int    VISTA_SEARCH           = 3;
        private const int    VISTA_WINDOW           = 7;    // half-size of the window the counts are taken in
        private const int    VISTA_MIN_INTERIOR     = 8;
        private const int    VISTA_MAP_MIN_INTERIOR = 30;
        private const int    VISTA_MAX_TOTAL        = 110;
        private const int    VISTA_MAP_MAX_TOTAL    = 200;
        private const int    VISTA_AROUND_INNER     = 9;
        private const int    VISTA_AROUND_OUTER     = 11;
        private const int    VISTA_MAP_AROUND_INNER = 12;
        private const int    VISTA_MAP_AROUND_OUTER = 14;
        private const double VISTA_MAX_AROUND       = 0.10;

        private const int    POI_SEARCH       = 3;
        private const int    POI_HALF         = 2;
        private const double POI_MIN_CENTRE   = 0.5;
        private const int    POI_WHITE_INNER  = 3;
        private const int    POI_WHITE_OUTER  = 5;
        private const double POI_MIN_WHITE    = 0.15;
        private const int    POI_AROUND_INNER = 8;
        private const int    POI_AROUND_OUTER = 10;
        private const double POI_MAX_AROUND   = 0.10;

        // Hero challenge templates, as x,y pixel offsets from the icon centre at the calibration UI scale. Taken from clean
        // done icons: the gold arrow outline, the dark figure enclosed by it, and the thin dark border just outside it.
        // The icon's brightness changes from frame to frame (the figure brightens with the outline), so the figure and
        // the border are tested relative to the outline's own brightness. Measured on 2026-10-25: done icons give an
        // outline share of 0.69 to 1.00, a dark figure of 0.88 to 1.00 and a dark border of 0.73 to 0.95; open icons
        // give an outline share of at most 0.24, because their gold fills the centre and shifts the best offset; fog and
        // terrain that pass the gold and figure tests give a dark border of at most 0.22.
        private const int    HC_SEARCH          = 3;
        private const double HC_MIN_OUTLINE     = 0.6;
        private const double HC_MIN_FIGURE_DARK = 0.6;
        private const double HC_MAX_FIGURE_GOLD = 0.1;
        private const double HC_MIN_BORDER_DARK = 0.5;
        private const double HC_DARK_RATIO      = 0.75;   // darker than this share of the outline's mean brightness
        private const double HC_BRIGHT_DARK_RATIO = 0.92; // measured across the screenshot and PrintWindow frames
        private const int    HC_MAP_EXTENT      = 16;
        private const int    HC_COMPASS_EXTENT  = 9;

        private static readonly int[] HC_MAP_OUTLINE = {
            -2,-12, 1,-12, -3,-11, -4,-10, -1,-10, 0,-10, 3,-10, -5,-9, -2,-9, -1,-9, 0,-9, 1,-9, 4,-9, -6,-8,
            -3,-8, -2,-8, -1,-8, 0,-8, 1,-8, 2,-8, 5,-8, -7,-7, -4,-7, -3,-7, -2,-7, -1,-7, 0,-7, 1,-7, 2,-7,
            3,-7, 6,-7, -8,-6, -6,-6, -5,-6, -4,-6, -3,-6, 2,-6, 3,-6, 4,-6, 5,-6, -9,-5, -7,-5, -6,-5, -5,-5,
            -4,-5, -3,-5, 2,-5, 3,-5, 4,-5, 5,-5, 6,-5, 8,-5, -10,-4, -8,-4, -7,-4, -6,-4, -5,-4, -4,-4, -3,-4,
            2,-4, 3,-4, 4,-4, 5,-4, 6,-4, 9,-4, -9,-3, -8,-3, -7,-3, -6,-3, -5,-3, -4,-3, -3,-3, 2,-3, 3,-3,
            4,-3, 5,-3, 6,-3, 7,-3, 10,-3, -12,-2, -10,-2, -9,-2, -8,-2, -7,-2, -6,-2, -5,-2, -4,-2, -3,-2,
            -2,-2, 1,-2, 2,-2, 3,-2, 4,-2, 5,-2, 6,-2, 7,-2, 8,-2, 9,-2, 11,-2, -13,-1, -12,-1, -11,-1, -10,-1,
            -9,-1, -8,-1, -7,-1, -6,-1, -5,-1, 4,-1, 5,-1, 6,-1, 7,-1, 8,-1, 9,-1, 10,-1, 11,-1, 12,-1, -14,0,
            -13,0, -12,0, -11,0, -10,0, -9,0, -8,0, -7,0, -6,0, 5,0, 6,0, 7,0, 8,0, 9,0, 10,0, 11,0, 12,0, -8,1,
            -7,1, -6,1, 5,1, 6,1, 7,1, -7,2, -6,2, 5,2, 6,2, -7,3, -6,3, -5,3, 4,3, 5,3, 6,3, -7,4, -6,4, -4,4,
            3,4, 5,4, 6,4, -7,5, -6,5, -5,5, -3,5, 2,5, 4,5, 5,5, 6,5, -7,6, -6,6, -5,6, -4,6, -1,6, 0,6, 1,6,
            3,6, 4,6, 5,6, 6,6, -7,7, -6,7, -5,7, -4,7, -3,7, 0,7, 2,7, 3,7, 4,7, 5,7, -5,8, -4,8, -3,8, -2,8,
            1,8, 2,8, 3,8, 4,8, -4,9, -3,9, -2,9, -1,9, 0,9, 1,9, 2,9, 3,9, -3,10, -2,10, -1,10, 0,10, 1,10,
            2,10, -2,11, -1,11, 0,11
        };

        private static readonly int[] HC_MAP_FIGURE = {
            -1,-6, 0,-6, -1,-5, 0,-5, -1,-4, 0,-4, -1,-3, 0,-3, -1,-2, 0,-2, -4,-1, -3,-1, -2,-1, -1,-1, 0,-1,
            1,-1, 2,-1, 3,-1, -5,0, -4,0, -3,0, -2,0, -1,0, 0,0, 1,0, 2,0, 3,0, 4,0, -5,1, -4,1, -3,1, -2,1,
            -1,1, 0,1, 1,1, 2,1, 3,1, 4,1, -5,2, -4,2, -3,2, -2,2, -1,2, 0,2, 1,2, 2,2, 3,2, 4,2, -4,3, -3,3,
            -2,3, -1,3, 0,3, 1,3, 2,3, 3,3, -2,4, -1,4, 0,4, 1,4, -4,5, -1,5, 0,5, -3,6, 2,6
        };

        private static readonly int[] HC_MAP_BORDER = {
            -4,-14, -4,-13, -4,-12, -4,-11, -3,-14, -3,-13, -3,-12, -2,-14, -2,-13, -2,-11, -1,-14, -1,-13,
            -1,-11, 0,-14, 0,-13, 0,-11, 1,-14, 1,-13, 1,-11, 2,-14, 2,-13, 2,-12, 2,-11, 3,-14, 3,-13, 3,-12,
            3,-11, -5,-13, -5,-12, -5,-11, -5,-10, -6,-12, -6,-11, -6,-10, -6,-9, 4,-12, 4,-11, 4,-10, 5,-12,
            5,-11, 5,-10, 5,-9, -7,-11, -7,-10, -7,-9, -7,-8, 6,-11, 6,-10, 6,-9, 6,-8, -8,-10, -8,-9, -8,-8,
            -8,-7, 6,-6, 7,-10, 7,-9, 7,-8, 7,-7, 7,-6, -9,-9, -9,-8, -9,-7, -9,-6, 8,-9, 8,-8, 8,-7, 8,-6,
            -10,-8, -10,-7, -10,-6, -10,-5, -11,-7, -11,-6, -11,-5, -11,-4, -11,-3, -10,-3, 9,-7, 9,-6, 9,-5,
            10,-7, 10,-6, 10,-5, 10,-4, -12,-6, -12,-5, -12,-4, -12,-3, 11,-6, 11,-5, 11,-4, 11,-3, 12,-5, 12,-4,
            12,-3, 12,-2, -14,-4, -14,-3, -14,-2, -14,-1, -13,-4, -13,-3, -13,-2, 13,-4, 13,-3, 13,-2, 13,-1,
            13,0, -15,-3, -15,-2, -15,-1, -15,0, -15,1, -14,1, -13,1, -12,1, -11,1, -10,1, -9,1, 8,1, 9,1, 10,1,
            11,1, 12,1, 13,1, 14,-3, 14,-2, 14,-1, 14,0, 14,1, -16,-2, -16,-1, -16,0, -16,1, -16,2, -15,2, -14,2,
            -13,2, -12,2, -11,2, -10,2, -9,2, -8,2, 7,2, 8,2, 9,2, 10,2, 11,2, 12,2, 13,2, 14,2, -10,3, -9,3,
            -8,3, 7,3, 8,3, 9,3, -9,4, -8,4, 7,4, 8,4, -9,5, -8,5, 7,5, 8,5, -9,6, -8,6, 7,6, 8,6, -9,7, -8,7,
            6,7, 7,7, 8,7, -9,8, -8,8, -7,8, -6,8, 5,8, 6,8, 7,8, 8,8, -9,9, -8,9, -7,9, -6,9, -5,9, 4,9, 5,9,
            6,9, 7,9, -7,10, -6,10, -5,10, -4,10, 3,10, 4,10, 5,10, 6,10, -6,11, -5,11, -4,11, -3,11, 1,11, 2,11,
            3,11, 4,11, 5,11, -5,12, -4,12, -3,12, -2,12, -1,12, 0,12, 1,12, 2,12, 3,12, 4,12, -4,13, -3,13,
            -2,13, -1,13, 0,13, 1,13, 2,13
        };

        private static readonly int[] HC_COMPASS_OUTLINE = {
            -1,-4, 0,-4, 1,-4, -3,-3, -2,-3, -1,-3, 1,-3, 2,-3, 3,-3, -4,-2, -3,-2, -2,-2, 1,-2, 2,-2, 3,-2,
            -5,-1, -4,-1, -3,-1, -2,-1, 1,-1, 2,-1, 3,-1, 4,-1, -6,0, -5,0, -4,0, -3,0, 3,0, 4,0, 5,0, -5,1,
            -4,1, 3,1, 4,1, -4,2, 3,2, -4,3, -3,3, 3,3, -4,4, -3,4, 2,4, 3,4, -3,5, -2,5, 2,5, -2,6, -1,6, 1,6,
            -1,7, 0,7
        };

        private static readonly int[] HC_COMPASS_FIGURE = {
            0,-2, 0,-1, -1,0, 0,0, -2,1, -1,1, 0,1, 1,1, 2,1, -2,2, -1,2, 0,2, 1,2, 2,2, -1,3, 0,3, 1,3
        };

        private static readonly int[] HC_COMPASS_BORDER = {
            -3,-6, -3,-5, -3,-4, -2,-6, -2,-5, -2,-4, -1,-6, -1,-5, 0,-6, 0,-5, 1,-6, 1,-5, 2,-6, 2,-5, 2,-4,
            3,-6, 3,-5, 3,-4, -5,-5, -5,-4, -5,-3, -5,-2, -4,-5, -4,-4, -4,-3, 4,-5, 4,-4, 4,-3, 4,-2, 5,-5,
            5,-4, 5,-3, 5,-2, 5,-1, -6,-4, -6,-3, -6,-2, -6,-1, -7,-3, -7,-2, -7,-1, -7,0, -7,1, -6,1, 5,1, 6,-3,
            6,-2, 6,-1, 6,0, 6,1, -8,-2, -8,-1, -8,0, -8,1, -8,2, -7,2, -6,2, -5,2, 4,2, 5,2, 6,2, 7,-2, 7,-1,
            7,0, 7,1, 7,2, -7,3, -6,3, -5,3, 4,3, 5,3, 6,3, -6,4, -5,4, 4,4, 5,4, -6,5, -5,5, -4,5, 3,5, 4,5,
            5,5, -6,6, -5,6, -4,6, -3,6, 2,6, 3,6, 4,6, 5,6, -5,7, -4,7, -3,7, -2,7, 1,7, 2,7, 3,7, 4,7, -4,8,
            -3,8, -2,8, -1,8, 0,8, 1,8, 2,8, 3,8, -3,9, -2,9, -1,9, 0,9, 1,9, 2,9
        };

        public static bool CanRecognise(PointKind kind) {
            return kind == PointKind.Waypoint || kind == PointKind.Heart || kind == PointKind.PointOfInterest || kind == PointKind.Vista
                || kind == PointKind.HeroChallenge;
        }

        internal static int CentreSearchRadius(double uiScale, PointKind kind, IconSet set) {
            double s = uiScale / CALIBRATION_UI_SCALE;
            switch (kind) {
                case PointKind.Waypoint:
                    return Scaled(WP_SEARCH, set == IconSet.WorldMap ? s * WORLD_MAP_WAYPOINT_SCALE : s);
                case PointKind.Heart: return Scaled(HEART_SEARCH, s);
                case PointKind.Vista: return Scaled(VISTA_SEARCH, s);
                case PointKind.HeroChallenge: return Scaled(HC_SEARCH, s);
                default: return Scaled(POI_SEARCH, s);
            }
        }

        /// <summary>Furthest distance from the projected position that <see cref="IsFilled"/> reads, in pixels.</summary>
        public static int Reach(double uiScale, IconSet set = IconSet.WorldMap) {
            if (set == IconSet.Compass) return CalibratedReach(uiScale);
            double waypointScale = uiScale / CALIBRATION_UI_SCALE * WORLD_MAP_WAYPOINT_SCALE;
            return Math.Max(CalibratedReach(uiScale), Scaled(WP_SEARCH, waypointScale) + Scaled(WP_AROUND_OUTER, waypointScale));
        }

        private static int CalibratedReach(double uiScale) {
            double s = uiScale / CALIBRATION_UI_SCALE;
            int reach = Math.Max(Math.Max(Scaled(WP_SEARCH, s) + Scaled(WP_AROUND_OUTER, s),
                                          Scaled(VISTA_SEARCH, s) + Math.Max(Scaled(VISTA_MAP_AROUND_OUTER, s), Scaled(VISTA_WINDOW, s) + 1)),
                                 Math.Max(Scaled(HEART_SEARCH, s) + Scaled(HEART_AROUND_OUTER, s),
                                          Scaled(POI_SEARCH, s) + Scaled(POI_AROUND_OUTER, s)));
            return Math.Max(reach, Scaled(HC_SEARCH, s) + Scaled(Math.Max(HC_MAP_EXTENT, HC_COMPASS_EXTENT), s));
        }

        /// <summary>
        /// Pixels are 0xAARRGGBB, row by row (System.Drawing's Format32bppArgb). Returns false when anything it needs
        /// lies outside the buffer.
        /// </summary>
        public static bool IsFilled(int[] pixels, int width, int height, int x, int y, PointKind kind, double uiScale) {
            return IsFilled(pixels, width, height, x, y, kind, uiScale, IconSet.WorldMap);
        }

        public static bool IsFilled(int[] pixels, int width, int height, int x, int y, PointKind kind, double uiScale, IconSet set) {
            if (!CanRecognise(kind) || uiScale <= 0) return false;

            double s     = uiScale / CALIBRATION_UI_SCALE;
            int    reach = CalibratedReach(uiScale);
            if (x - reach < 0 || y - reach < 0 || x + reach >= width || y + reach >= height) return false;

            var img = new Image(pixels, width);
            switch (kind) {
                case PointKind.Waypoint:
                    if (IsWaypointFilled(img, x, y, s)) return true;
                    if (set != IconSet.WorldMap) return false;
                    return IsWorldMapWaypointFilled(img,x,y,s,width,height)
                        || IsWorldMapWaypointFilled(img,x,y,s * 1.25,width,height)
                        || IsWorldMapWaypointFilled(img,x,y,s * WORLD_MAP_WAYPOINT_SCALE,width,height);
                case PointKind.Heart:           return set == IconSet.Compass ? IsCompassHeartFilled(img, x, y, s) : IsHeartFilled(img, x, y, s);
                case PointKind.Vista:           return IsVistaFilled(img, x, y, s, set);
                case PointKind.HeroChallenge:   return IsHeroChallengeDone(img, x, y, s, set);
                default:                        return IsPoiFilled(img, x, y, s);
            }
        }

        private static bool IsWaypointFilled(Image img, int x, int y, double s) {
            int search = Scaled(WP_SEARCH, s);
            int centre = Scaled(WP_CENTRE, s);

            // Best centre within the search window, then the frame and the surroundings around that centre.
            double best = -1;
            int    bx = x, by = y, bestDist = int.MaxValue;
            for (int dy = -search; dy <= search; dy++) {
                for (int dx = -search; dx <= search; dx++) {
                    double v    = img.Diamond(x + dx, y + dy, 0, centre, IsWaypointCyan);
                    int    dist = Math.Abs(dx) + Math.Abs(dy);
                    if (v > best || (v == best && dist < bestDist)) {
                        best     = v;
                        bestDist = dist;
                        bx       = x + dx;
                        by       = y + dy;
                    }
                }
            }

            return best >= WP_MIN_CENTRE
                && img.Diamond(bx, by, Scaled(WP_FRAME_INNER, s), Scaled(WP_FRAME_OUTER, s), IsWaypointTan) >= WP_MIN_FRAME
                && img.Diamond(bx, by, Scaled(WP_AROUND_INNER, s), Scaled(WP_AROUND_OUTER, s), IsWaypointCyan) <= WP_MAX_AROUND;
        }

        private static bool IsWorldMapWaypointFilled(Image img,int x,int y,double s,int width,int height) {
            int search=Scaled(WP_SEARCH,s), outer=Scaled(WP_AROUND_OUTER,s), reach=search+outer;
            if(x-reach<0 || y-reach<0 || x+reach>=width || y+reach>=height) return false;
            for(int dy=-search;dy<=search;dy++) {
                for(int dx=-search;dx<=search;dx++) {
                    int cx=x+dx,cy=y+dy;
                    // A bright cyan patch can pull the old best-centre search off the actual diamond. Check the
                    // complete icon at each nearby centre instead of discarding one whose centre is less cyan.
                    if(img.Diamond(cx,cy,0,Scaled(WP_CENTRE,s),IsWaypointCyan) < WP_MIN_CENTRE) continue;
                    if(img.Diamond(cx,cy,Scaled(WP_FRAME_INNER,s),Scaled(WP_FRAME_OUTER,s),IsWaypointTan) < WP_MIN_FRAME) continue;
                    if(img.Diamond(cx,cy,Scaled(WP_AROUND_INNER,s),outer,IsWaypointCyan) > WP_MAX_AROUND) continue;
                    if(img.Diamond(cx,cy,Scaled(WP_RIM_INNER,s),Scaled(WP_RIM_OUTER,s),p=>Luminance(p)<110) >= WP_MIN_DARK_RIM) return true;
                }
            }
            return false;
        }

        private static bool IsHeartFilled(Image img, int x, int y, double s) {
            img.BestSquare(x, y, Scaled(HEART_SEARCH, s), Scaled(HEART_HALF, s), IsHeartGold, out double best, out int bx, out int by);

            return best >= HEART_MIN_CENTRE
                && img.Square(bx, by, Scaled(HEART_AROUND_INNER, s), Scaled(HEART_AROUND_OUTER, s), IsHeartGold) <= HEART_MAX_AROUND
                && GoldHeightRatio(img, bx, by, Scaled(HEART_SHAPE_RADIUS, s)) >= HEART_MIN_HEIGHT;
        }

        /// <summary>Height of the heart gold divided by its widest row, within the square of the given radius.</summary>
        private static double GoldHeightRatio(Image img, int cx, int cy, int radius) {
            int top = int.MaxValue, bottom = int.MinValue, width = 0;
            for (int y = cy - radius; y <= cy + radius; y++) {
                int count = 0, minX = int.MaxValue, maxX = int.MinValue;
                for (int x = cx - radius; x <= cx + radius; x++) {
                    if (!IsHeartGold(img.At(x, y))) continue;
                    count++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
                if (count < 2) continue;   // a single stray pixel is not part of the heart
                if (y < top) top = y;
                bottom = y;
                width  = Math.Max(width, maxX - minX + 1);
            }

            return width == 0 ? 0 : (double)(bottom - top + 1) / width;
        }

        private static bool IsCompassHeartFilled(Image img, int x, int y, double s) {
            img.BestSquare(x, y, Scaled(HEART_SEARCH, s), Scaled(COMPASS_HEART_HALF, s), IsHeartGold, out double best, out int bx, out int by);

            return best >= COMPASS_HEART_MIN_CENTRE
                && img.Square(bx, by, Scaled(COMPASS_HEART_AROUND_INNER, s), Scaled(COMPASS_HEART_AROUND_OUTER, s), IsHeartGold) <= HEART_MAX_AROUND;
        }

        private static bool IsVistaFilled(Image img, int x, int y, double s, IconSet set) {
            bool   map      = set == IconSet.WorldMap;
            int    search   = Scaled(VISTA_SEARCH, s);
            int    window   = Scaled(VISTA_WINDOW, s);
            double area     = s * s;
            int    minInner = (int)Math.Round((map ? VISTA_MAP_MIN_INTERIOR : VISTA_MIN_INTERIOR) * area);
            int    maxTotal = (int)Math.Round((map ? VISTA_MAP_MAX_TOTAL : VISTA_MAX_TOTAL) * area);
            int    inner    = Scaled(map ? VISTA_MAP_AROUND_INNER : VISTA_AROUND_INNER, s);
            int    outer    = Scaled(map ? VISTA_MAP_AROUND_OUTER : VISTA_AROUND_OUTER, s);

            int bestInterior = -1, bestTotal = 0, bx = x, by = y, bestDist = int.MaxValue;
            for (int dy = -search; dy <= search; dy++) {
                for (int dx = -search; dx <= search; dx++) {
                    img.SolidCount(x + dx, y + dy, window, IsVistaRed, out int interior, out int total);
                    int dist = Math.Abs(dx) + Math.Abs(dy);
                    if (interior > bestInterior || (interior == bestInterior && dist < bestDist)) {
                        bestInterior = interior;
                        bestTotal    = total;
                        bestDist     = dist;
                        bx           = x + dx;
                        by           = y + dy;
                    }
                }
            }

            return bestInterior >= minInner
                && bestTotal <= maxTotal
                && img.Square(bx, by, inner, outer, IsVistaRed) <= VISTA_MAX_AROUND;
        }

        private static bool IsHeroChallengeDone(Image img, int x, int y, double s, IconSet set) {
            return IsHeroChallengeDoneCore(img,x,y,s,set,false)
                || (set == IconSet.WorldMap && IsHeroChallengeDoneCore(img,x,y,s,set,true))
                || (set == IconSet.Compass && IsBrightCompassHeroDone(img,x,y,s));
        }

        private static bool IsBrightCompassHeroDone(Image img, int x, int y, double s) {
            int search = Scaled(HC_SEARCH,s);
            for(int dy=-search;dy<=search;dy++) for(int dx=-search;dx<=search;dx++) {
                int cx=x+dx,cy=y+dy,gold=0; double red=0,green=0,blue=0,light=0;
                for(int i=0;i<HC_COMPASS_OUTLINE.Length;i+=2) {
                    int p=img.At(cx+Offset(HC_COMPASS_OUTLINE[i],s),cy+Offset(HC_COMPASS_OUTLINE[i+1],s));
                    if(!IsHcGold(p)) continue;
                    gold++; red+=R(p); green+=G(p); blue+=B(p); light+=Luminance(p);
                }
                if(2.0*gold/HC_COMPASS_OUTLINE.Length < 0.9) continue;
                red/=gold; green/=gold; blue/=gold; light/=gold;
                // The washed-out palette still has a saturated gold outline and a darker enclosed figure.
                if(red<245 || green<220 || blue>60) continue;
                int figureDark=0,figureBright=0;
                for(int i=0;i<HC_COMPASS_FIGURE.Length;i+=2) {
                    int p=img.At(cx+Offset(HC_COMPASS_FIGURE[i],s),cy+Offset(HC_COMPASS_FIGURE[i+1],s));
                    if(Luminance(p)<light*HC_BRIGHT_DARK_RATIO) figureDark++;
                    else if(IsHcGold(p)) figureBright++;
                }
                if(2.0*figureDark/HC_COMPASS_FIGURE.Length < 0.9 || 2.0*figureBright/HC_COMPASS_FIGURE.Length > HC_MAX_FIGURE_GOLD) continue;
                int borderGold=0,borderDifferent=0;
                for(int i=0;i<HC_COMPASS_BORDER.Length;i+=2) {
                    int p=img.At(cx+Offset(HC_COMPASS_BORDER[i],s),cy+Offset(HC_COMPASS_BORDER[i+1],s));
                    if(IsHcGold(p)) {borderGold++;continue;}
                    if(Math.Abs(B(p)-blue)>30 || Math.Abs(R(p)-red)+Math.Abs(G(p)-green)+Math.Abs(B(p)-blue)>60) borderDifferent++;
                }
                // Bright ground need not be dark: it must instead differ in colour from the closed gold outline.
                if(2.0*borderGold/HC_COMPASS_BORDER.Length <= 0.3 && 2.0*borderDifferent/HC_COMPASS_BORDER.Length >= 0.65) return true;
            }
            return false;
        }

        private static bool IsHeroChallengeDoneCore(Image img, int x, int y, double s, IconSet set, bool brightMap) {
            bool  map     = set == IconSet.WorldMap;
            int[] outline = map ? HC_MAP_OUTLINE : HC_COMPASS_OUTLINE;
            int[] figure  = map ? HC_MAP_FIGURE  : HC_COMPASS_FIGURE;
            int[] border  = map ? HC_MAP_BORDER  : HC_COMPASS_BORDER;
            int   search  = Scaled(HC_SEARCH, s);

            double bestKey = -1, bestOutline = 0, bestDark = 0, bestGold = 1, bestBorder = 0;
            int    bestDist = int.MaxValue;
            for (int dy = -search; dy <= search; dy++) {
                for (int dx = -search; dx <= search; dx++) {
                    int cx = x + dx, cy = y + dy;

                    int    gold = 0;
                    double lit  = 0;
                    for (int i = 0; i < outline.Length; i += 2) {
                        int p = img.At(cx + Offset(outline[i], s), cy + Offset(outline[i + 1], s));
                        if (!IsHcGold(p)) continue;
                        gold++;
                        lit += Luminance(p);
                    }
                    double outlineShare = 2.0 * gold / outline.Length;
                    double meanOutline  = gold > 0 ? lit / gold : 255;
                    // In bright world-map rendering the completed figure is still darker than its outline, but
                    // its gold pixels are brighter than the original absolute colour cutoff. Require a stronger
                    // outline, figure and border match for this additional, measured contrast mode.
                    if (brightMap && meanOutline < 200) continue;
                    double dark         = meanOutline * (brightMap ? HC_BRIGHT_DARK_RATIO : HC_DARK_RATIO);

                    int figureDark = 0, figureGold = 0;
                    for (int i = 0; i < figure.Length; i += 2) {
                        int p = img.At(cx + Offset(figure[i], s), cy + Offset(figure[i + 1], s));
                        if (brightMap && Luminance(p) < dark) figureDark++;
                        else if (IsHcGold(p)) figureGold++;
                        else if (Luminance(p) < dark) figureDark++;
                    }

                    int borderDark = 0;
                    for (int i = 0; i < border.Length; i += 2) {
                        if (Luminance(img.At(cx + Offset(border[i], s), cy + Offset(border[i + 1], s))) < dark) borderDark++;
                    }

                    double darkShare = 2.0 * figureDark / figure.Length;
                    double key       = outlineShare + darkShare;
                    int    dist      = Math.Abs(dx) + Math.Abs(dy);
                    if (key > bestKey || (key == bestKey && dist < bestDist)) {
                        bestKey     = key;
                        bestDist    = dist;
                        bestOutline = outlineShare;
                        bestDark    = darkShare;
                        bestGold    = 2.0 * figureGold / figure.Length;
                        bestBorder  = 2.0 * borderDark / border.Length;
                    }
                }
            }

            return bestOutline >= (brightMap ? 0.8 : HC_MIN_OUTLINE)
                && bestDark    >= (brightMap ? 0.8 : HC_MIN_FIGURE_DARK)
                && bestGold    <= HC_MAX_FIGURE_GOLD
                && bestBorder  >= (brightMap ? 0.6 : HC_MIN_BORDER_DARK);
        }

        private static int Offset(int value, double s) {
            return (int)Math.Round(value * s);
        }

        private static bool IsPoiFilled(Image img, int x, int y, double s) {
            img.BestSquare(x, y, Scaled(POI_SEARCH, s), Scaled(POI_HALF, s), IsPoiOrange, out double best, out int bx, out int by);

            return (best >= POI_MIN_CENTRE
                && img.Square(bx, by, Scaled(POI_WHITE_INNER, s), Scaled(POI_WHITE_OUTER, s), IsWhite) >= POI_MIN_WHITE
                && img.Square(bx, by, Scaled(POI_AROUND_INNER, s), Scaled(POI_AROUND_OUTER, s), IsPoiOrange) <= POI_MAX_AROUND)
                || IsBrightPoiFilled(img,x,y,s);
        }

        private static bool IsBrightPoiFilled(Image img,int x,int y,double s) {
            int search=Scaled(POI_SEARCH,s),core=Scaled(1,s);
            for(int dy=-search;dy<=search;dy++) for(int dx=-search;dx<=search;dx++) {
                int cx=x+dx,cy=y+dy;
                if(img.Square(cx,cy,0,core,IsBrightPoiGold)<0.8) continue;
                img.MeanSquare(cx,cy,0,core,out double cr,out double cg,out double cb);
                double centreLight=0.3*cr+0.6*cg+0.1*cb;
                for(int size=3;size<=4;size++) {
                    int frame=Scaled(size,s),length=2*frame-1;
                    double totalR=0,totalG=0,totalB=0;bool closed=true;
                    for(int side=0;side<4;side++) {
                        int sx=side==0?cx-frame:side==1?cx+frame:cx-frame+1;
                        int sy=side==2?cy-frame:side==3?cy+frame:cy-frame+1;
                        img.MeanLine(sx,sy,side<2?0:1,side<2?1:0,length,out double r,out double g,out double b);
                        if(r<230 || g<205 || b<110 || g<cg+8 || b<cb+20 || 0.3*r+0.6*g+0.1*b<centreLight+8) {closed=false;break;}
                        totalR+=r;totalG+=g;totalB+=b;
                    }
                    if(!closed) continue;
                    int corners=0;
                    for(int corner=0;corner<4;corner++) {
                        int p=img.At(cx+(corner%2==0?-frame:frame),cy+(corner<2?-frame:frame));
                        if(IsBrightPoiFrame(p,cg,cb,centreLight)) corners++;
                    }
                    if(corners<3) continue;
                    // Label letters also enclose gold ground. A point has square corners and an isolated frame.
                    if(img.Square(cx,cy,frame+Scaled(2,s),frame+Scaled(5,s),p=>IsBrightPoiFrame(p,cg,cb,centreLight))>0.1) continue;
                    img.MeanSquare(cx,cy,frame+Scaled(2,s),frame+Scaled(3,s),out double ar,out double ag,out double ab);
                    double frameLight=(0.3*totalR+0.6*totalG+0.1*totalB)/4;
                    // A compact pale square must surround the gold centre and be distinct from the map behind it.
                    if(frameLight-(0.3*ar+0.6*ag+0.1*ab)>=18 && totalB/4-ab>=20) return true;
                }
            }
            return false;
        }

        private static int Scaled(int value, double s) {
            return value == 0 ? 0 : Math.Max(1, (int)Math.Round(value * s));
        }

        private struct Image {
            private readonly int[] _pixels;
            private readonly int   _width;

            public Image(int[] pixels, int width) {
                _pixels = pixels;
                _width  = width;
            }

            public int At(int x, int y) => _pixels[y * _width + x];

            public void MeanSquare(int cx,int cy,int inner,int outer,out double r,out double g,out double b) {
                r=g=b=0;int count=0;
                for(int y=-outer;y<=outer;y++) for(int x=-outer;x<=outer;x++) {
                    if(Math.Max(Math.Abs(x),Math.Abs(y))<inner) continue;
                    int p=At(cx+x,cy+y);r+=R(p);g+=G(p);b+=B(p);count++;
                }
                r/=count;g/=count;b/=count;
            }

            public void MeanLine(int x,int y,int dx,int dy,int count,out double r,out double g,out double b) {
                r=g=b=0;
                for(int i=0;i<count;i++){int p=At(x+i*dx,y+i*dy);r+=R(p);g+=G(p);b+=B(p);}
                r/=count;g/=count;b/=count;
            }

            /// <summary>Share of pixels passing the test whose Chebyshev distance from (cx, cy) is inner..outer.</summary>
            public double Square(int cx, int cy, int inner, int outer, Func<int, bool> test) {
                int total = 0, hits = 0;
                for (int dy = -outer; dy <= outer; dy++) {
                    for (int dx = -outer; dx <= outer; dx++) {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < inner) continue;
                        total++;
                        if (test(_pixels[(cy + dy) * _width + cx + dx])) hits++;
                    }
                }

                return total == 0 ? 0 : (double)hits / total;
            }

            /// <summary>
            /// In the square window of the given half-size: how many pixels pass the test together with all four
            /// neighbours (solid area), and how many pass at all.
            /// </summary>
            public void SolidCount(int cx, int cy, int half, Func<int, bool> test, out int interior, out int total) {
                interior = total = 0;
                for (int dy = -half; dy <= half; dy++) {
                    for (int dx = -half; dx <= half; dx++) {
                        int i = (cy + dy) * _width + cx + dx;
                        if (!test(_pixels[i])) continue;
                        total++;
                        if (test(_pixels[i - 1]) && test(_pixels[i + 1]) && test(_pixels[i - _width]) && test(_pixels[i + _width])) interior++;
                    }
                }
            }

            /// <summary>Same with the Manhattan distance, which follows the waypoint's diamond shape.</summary>
            public double Diamond(int cx, int cy, int inner, int outer, Func<int, bool> test) {
                int total = 0, hits = 0;
                for (int dy = -outer; dy <= outer; dy++) {
                    for (int dx = -outer; dx <= outer; dx++) {
                        int d = Math.Abs(dx) + Math.Abs(dy);
                        if (d < inner || d > outer) continue;
                        total++;
                        if (test(_pixels[(cy + dy) * _width + cx + dx])) hits++;
                    }
                }

                return total == 0 ? 0 : (double)hits / total;
            }

            /// <summary>
            /// The square patch with the highest share of fill within the search window. On equal shares the offset
            /// nearest the projected position wins, so a solid icon larger than the patch is measured at its middle
            /// and the band around it stays outside the icon.
            /// </summary>
            public void BestSquare(int x, int y, int search, int half, Func<int, bool> test, out double best, out int bx, out int by) {
                best = -1;
                bx   = x;
                by   = y;
                int bestDist = int.MaxValue;
                for (int dy = -search; dy <= search; dy++) {
                    for (int dx = -search; dx <= search; dx++) {
                        double v    = Square(x + dx, y + dy, 0, half, test);
                        int    dist = Math.Abs(dx) + Math.Abs(dy);
                        if (v > best || (v == best && dist < bestDist)) {
                            best     = v;
                            bestDist = dist;
                            bx       = x + dx;
                            by       = y + dy;
                        }
                    }
                }
            }
        }

        private static int R(int p) => (p >> 16) & 0xFF;
        private static int G(int p) => (p >> 8) & 0xFF;
        private static int B(int p) => p & 0xFF;

        // The module's own open-frame orange (255, 140, 0) passes the POI and heart tests. Blending or colour
        // management may shift it a little, so a small range around it never counts as fill.
        private static bool IsOwnFrameOrange(int p) => R(p) >= 235 && Math.Abs(G(p) - 140) <= 25 && B(p) <= 30;

        private static bool IsWaypointCyan(int p) => B(p) > 200 && G(p) > 170 && R(p) < 200 && B(p) - R(p) > 40;
        private static bool IsWaypointTan(int p)  => R(p) > 150 && G(p) > 130 && B(p) > 80 && G(p) < R(p) && R(p) - B(p) > 30 && R(p) - B(p) < 120;
        private static bool IsHeartGold(int p)    => R(p) > 200 && G(p) > 130 && G(p) < 215 && B(p) < 90 && R(p) - B(p) > 140 && !IsOwnFrameOrange(p);
        private static bool IsPoiOrange(int p)    => R(p) > 180 && G(p) > 80 && G(p) < 170 && B(p) < 90 && R(p) - G(p) > 50 && !IsOwnFrameOrange(p);
        private static bool IsBrightPoiGold(int p) => R(p)>220 && G(p)>150 && G(p)<240 && B(p)<150 && R(p)-G(p)>20 && R(p)-B(p)>100 && !IsOwnFrameOrange(p);
        private static bool IsBrightPoiFrame(int p,double centreG,double centreB,double centreLight) => R(p)>=230 && G(p)>=205 && B(p)>=110 && G(p)>=centreG+8 && B(p)>=centreB+20 && Luminance(p)>=centreLight+8;
        private static bool IsVistaRed(int p)     => R(p) > 120 && G(p) < 0.42 * R(p) && B(p) < 0.30 * R(p) && R(p) - G(p) > 70 && !IsOwnFrameOrange(p);
        private static bool IsWhite(int p)        => R(p) > 200 && G(p) > 200 && B(p) > 190;
        private static bool IsHcGold(int p)       => R(p) > 170 && G(p) > 120 && B(p) < 90 && R(p) - B(p) > 100 && !IsOwnFrameOrange(p);
        private static double Luminance(int p)    => 0.3 * R(p) + 0.6 * G(p) + 0.1 * B(p);

    }

}
