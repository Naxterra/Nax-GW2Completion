using System;

namespace Nax.Completion {

    /// <summary>Pure math, kept separate so it can be tested without the game running.</summary>
    public static class NavMath {

        /// <summary>
        /// Rotation of the arrow on screen in radians, clockwise, 0 = straight ahead (arrow points up),
        /// range -pi..pi. All inputs are in a north-up frame: east and north components.
        /// </summary>
        public static double ArrowRotation(double toEast, double toNorth, double facingEast, double facingNorth) {
            double targetAngle = Math.Atan2(toNorth, toEast);         // counter-clockwise from east
            double facingAngle = Math.Atan2(facingNorth, facingEast);

            double clockwise = -(targetAngle - facingAngle);

            while (clockwise >  Math.PI) clockwise -= 2 * Math.PI;
            while (clockwise < -Math.PI) clockwise += 2 * Math.PI;

            return clockwise;
        }

        /// <summary>
        /// Converts the offset between two continent coordinates into east/north components.
        /// Continent X grows east, continent Y grows south, so Y is flipped.
        /// </summary>
        public static void ContinentOffsetToEastNorth(double fromX, double fromY, double toX, double toY, out double east, out double north) {
            east  = toX - fromX;
            north = -(toY - fromY);
        }

    }

}
