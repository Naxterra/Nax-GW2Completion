using System;
using System.Collections.Generic;

namespace Nax.Completion {

    /// <summary>
    /// Draws part of a puzzle route into the 3D world: the next few steps as markers on the spot, joined by a thin
    /// line, each coloured by how you get there. The real implementation lives in src\World and talks to Blish's
    /// renderer. The test harness gets a stub.
    /// </summary>
    public interface IRoutePath : IDisposable {

        /// <summary>Shows the given steps (in route order). An empty list hides everything.</summary>
        void Show(IList<RouteStep> steps, RouteStep from);

        void Hide();

    }

    /// <summary>
    /// Decides how you got from one recorded step to the next, from the movement in between. Samples must be one per
    /// game frame (Mumble tick), with the real time between them.
    ///  - A jump is ballistic: the vertical speed is high at launch and then falls by gravity within a fraction of a
    ///    second. Running up a ramp keeps a constant vertical speed, so it never counts.
    ///  - A glide is a long, slow descent while moving forward, after being airborne (a jump or a free fall). Running
    ///    down a slope never passes through free fall, so it never counts.
    ///  - Any mounted movement counts as fly.
    /// </summary>
    public sealed class StepTypeDetector {

        public const float JUMP_UP_SPEED      = 2.5f;   // launch speed that starts a possible jump
        public const float JUMP_DROP          = 2.0f;   // the vertical speed must fall by this much ...
        public const float JUMP_CONFIRM_SECS  = 0.35f;  // ... within this time, as gravity does
        public const float RUN_SPEED          = 4.0f;   // horizontal speed at launch that makes it a running jump
        public const float FREE_FALL_SPEED    = 5.0f;   // sinking faster than this means airborne
        public const float GLIDE_MIN_SECONDS  = 1.2f;
        public const float GLIDE_MAX_SINK     = 4.0f;
        public const float GLIDE_MIN_SINK     = 0.8f;
        public const float GLIDE_MIN_FORWARD  = 2.0f;

        private bool  _mounted;
        private bool  _jumped;
        private bool  _ran;
        private bool  _airborne;
        private bool  _glided;
        private float _glideSeconds;
        private bool  _launchPending;
        private float _launchPeak;
        private float _launchSeconds;
        private bool  _launchRan;
        private float _lastX, _lastY, _lastZ;
        private float _horizontalSpeed;
        private bool  _hasLast;

        public void Reset(float x, float y, float z) {
            _mounted = _jumped = _ran = _airborne = _glided = _launchPending = _launchRan = false;
            _glideSeconds = _launchPeak = _launchSeconds = 0;
            _horizontalSpeed = 0;
            _lastX = x; _lastY = y; _lastZ = z;
            _hasLast = true;
        }

        /// <summary>After a step was removed, the way to it still counts for the step recorded next.</summary>
        public void Assume(StepType type) {
            switch (type) {
                case StepType.Run:  _jumped = true; _ran = true; break;
                case StepType.Jump: _jumped = true; break;
                case StepType.Fly:  _mounted = true; break;
            }
        }

        /// <summary>Feed one position sample. <paramref name="seconds"/> is the time since the previous sample.</summary>
        public void Sample(float x, float y, float z, float seconds, bool mounted) {
            if (!_hasLast || seconds <= 0) {
                Reset(x, y, z);
                return;
            }

            if (mounted) _mounted = true;

            float dx = x - _lastX, dy = y - _lastY, dz = z - _lastZ;
            float horizontal = (float)Math.Sqrt(dx * dx + dy * dy) / seconds;
            float vertical   = dz / seconds;

            if (_launchPending) {
                _launchSeconds += seconds;
                if (vertical > _launchPeak) _launchPeak = vertical;
                if (_launchPeak - vertical >= JUMP_DROP) {
                    // Gravity took the speed away: a real jump.
                    _jumped        = true;
                    _airborne      = true;
                    _ran           = _ran || _launchRan;
                    _launchPending = false;
                } else if (_launchSeconds > JUMP_CONFIRM_SECS) {
                    // A constant climb: a ramp or stairs.
                    _launchPending = false;
                }
            } else if (vertical > JUMP_UP_SPEED) {
                _launchPending = true;
                _launchPeak    = vertical;
                _launchSeconds = 0;
                _launchRan     = _horizontalSpeed >= RUN_SPEED;   // the speed just before the launch
            }

            if (vertical < -FREE_FALL_SPEED) _airborne = true;

            if (_airborne && vertical < -GLIDE_MIN_SINK && vertical > -GLIDE_MAX_SINK && horizontal > GLIDE_MIN_FORWARD) {
                _glideSeconds += seconds;
                if (_glideSeconds >= GLIDE_MIN_SECONDS) _glided = true;
            } else {
                _glideSeconds = 0;
            }

            _horizontalSpeed = horizontal;
            _lastX = x; _lastY = y; _lastZ = z;
        }

        public StepType Classify() {
            if (_mounted || _glided) return StepType.Fly;
            if (_jumped) return _ran ? StepType.Run : StepType.Jump;
            return StepType.Walk;
        }

        public static StepType Next(StepType type) {
            switch (type) {
                case StepType.Walk: return StepType.Run;
                case StepType.Run:  return StepType.Jump;
                case StepType.Jump: return StepType.Fly;
                default:            return StepType.Walk;
            }
        }

    }

}
