using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Nax.Completion {

    /// <summary>
    /// Follows you through a recorded puzzle route. A route is active while you are within <see cref="ZONE_RADIUS"/>
    /// of any of its steps, and the guide disappears as soon as you leave that zone. The next step is the one after
    /// the last step you reached. After a fall it points at the nearest earlier step you can get to from where you
    /// landed.
    /// </summary>
    public sealed class PuzzleGuide {

        public const float ZONE_RADIUS = 40f;
        public const float REACHED     = 5f;

        // Being this far below the step you last reached counts as a fall.
        public const float FALL_HEIGHT = 8f;

        // After a fall, only steps at most this far above you count as reachable.
        public const float CLIMB_HEIGHT = 3f;

        // Another route nearby takes over only when its closest step is this much closer than the current route's.
        public const float ROUTE_SWITCH_MARGIN = 5f;

        public PuzzleRoute Route     { get; private set; }

        /// <summary>Index of the step to go to next. Equal to the step count once the last step is reached.</summary>
        public int         NextStep  { get; private set; }

        public bool Finished => this.Route != null && this.NextStep >= this.Route.Steps.Count;

        // Share of a route's steps you must have reached in one visit before reaching its last step counts as doing the
        // puzzle. Standing at the end after a waypoint, a portal or a walk round the back does not.
        public const double COMPLETION_SHARE = 0.4;

        private readonly HashSet<int> _reached = new HashSet<int>();

        /// <summary>
        /// True once the last step is reached after following the route in this visit: at least 40% of its steps were
        /// reached since you entered its zone, and at least three, or all of them for a route of two steps. A route of
        /// a single step never completes, because arriving at that one step proves nothing.
        /// </summary>
        public bool CompletedThisVisit => this.Route != null
                                       && this.Route.Steps.Count >= 2
                                       && _reached.Contains(this.Route.Steps.Count - 1)
                                       && _reached.Count >= Math.Min(this.Route.Steps.Count,
                                                                     Math.Max(3, (int)Math.Ceiling(this.Route.Steps.Count * COMPLETION_SHARE)));

        /// <summary>
        /// Set once the module has acted on <see cref="CompletedThisVisit"/>, so a visit ticks the puzzle at most once.
        /// Without it, undoing the automatic tick while still at the end would tick the puzzle again on the next update.
        /// Cleared when you leave the zone or switch to another route.
        /// </summary>
        public bool CompletionClaimed { get; private set; }

        public void ClaimCompletion() {
            this.CompletionClaimed = true;
        }

        /// <summary>Returns true while a route is active.</summary>
        public bool Update(IList<PuzzleRoute> routesOnMap, Vector3 position) {
            PuzzleRoute closestRoute = null;
            int         closestStep  = -1;
            float       closestDist  = float.MaxValue;

            foreach (var route in routesOnMap) {
                float d = ClosestStep(route, position, out int step);
                if (d < closestDist) {
                    closestDist  = d;
                    closestRoute = route;
                    closestStep  = step;
                }
            }

            // Stay on the route you are following unless another one is clearly closer, so two puzzles that share
            // an area do not take turns.
            if (this.Route != null && closestRoute != this.Route && routesOnMap.Contains(this.Route)) {
                float current = ClosestStep(this.Route, position, out int currentStep);
                if (current <= ZONE_RADIUS && closestDist > current - ROUTE_SWITCH_MARGIN) {
                    closestRoute = this.Route;
                    closestStep  = currentStep;
                    closestDist  = current;
                }
            }

            if (closestRoute == null || closestDist > ZONE_RADIUS) {
                Reset();
                return false;
            }

            if (closestRoute != this.Route) {
                // Entering a zone: head for the nearest step first (usually the start).
                this.Route    = closestRoute;
                this.NextStep = closestDist <= REACHED ? closestStep + 1 : closestStep;
                _reached.Clear();
                this.CompletionClaimed = false;
                if (closestDist <= REACHED) _reached.Add(closestStep);
                return true;
            }

            if (closestDist <= REACHED) {
                // Reaching any step, forwards or backwards, makes the one after it the next target.
                this.NextStep = closestStep + 1;
                _reached.Add(closestStep);
            } else if (IsFall(this.Route, this.NextStep, position)) {
                int restart = NearestReachableBefore(this.Route, this.NextStep, position);
                if (restart >= 0) this.NextStep = restart;
            }

            return true;
        }

        public void Reset() {
            this.Route    = null;
            this.NextStep = 0;
            _reached.Clear();
            this.CompletionClaimed = false;
        }

        private static float ClosestStep(PuzzleRoute route, Vector3 position, out int step) {
            step = -1;
            float best = float.MaxValue;
            for (int i = 0; i < route.Steps.Count; i++) {
                float d = Distance(route.Steps[i], position);
                if (d < best) {
                    best = d;
                    step = i;
                }
            }

            return best;
        }

        /// <summary>
        /// A fall leaves you far below the step you last reached and below the step you were heading for. A planned
        /// drop to a lower next step is not a fall.
        /// </summary>
        private static bool IsFall(PuzzleRoute route, int nextStep, Vector3 position) {
            if (nextStep <= 0) return false;
            if (route.Steps[nextStep - 1].Z - position.Z <= FALL_HEIGHT) return false;
            return nextStep >= route.Steps.Count || route.Steps[nextStep].Z - position.Z > CLIMB_HEIGHT;
        }

        /// <summary>Nearest step before <paramref name="before"/> that is not far above you, or -1.</summary>
        private static int NearestReachableBefore(PuzzleRoute route, int before, Vector3 position) {
            int   best     = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < before; i++) {
                var step = route.Steps[i];
                if (step.Z - position.Z > CLIMB_HEIGHT) continue;

                float d = Distance(step, position);
                if (d < bestDist) {
                    bestDist = d;
                    best     = i;
                }
            }

            return best;
        }

        public static float Distance(RouteStep step, Vector3 position) {
            float dx = step.X - position.X;
            float dy = step.Y - position.Y;
            float dz = step.Z - position.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>"4 m up", "6 m down" or "same height", from the height you are at to the step's.</summary>
        public static string HeightText(float fromZ, float toZ) {
            float dz = toZ - fromZ;
            if (dz > 1.5f)  return $"{dz:0} m up";
            if (dz < -1.5f) return $"{-dz:0} m down";
            return "same height";
        }

    }

}
