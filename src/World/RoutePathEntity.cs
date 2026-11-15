using System;
using System.Collections.Generic;
using Blish_HUD;
using Blish_HUD.Entities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Nax.Completion.World {

    /// <summary>
    /// Blish 3D entity that draws route steps as thin rings lying flat around the spot to reach, joined by a thin dashed
    /// line. Colours: walk = white, run = gold, jump = orange, fly = light blue. The GW2 world is not in Blish's depth
    /// buffer, so the drawing shows over everything, like every overlay. To keep the ground readable (the owner could
    /// not see where to jump with the first version's solid ribbon and 0.9 m filled diamonds), the ring leaves the
    /// landing spot itself clear, the line stops short of both ends, and only the next step is drawn at full strength.
    /// </summary>
    internal sealed class RoutePathEntity : IEntity, IRoutePath {

        private const float LINE_HALF_WIDTH = 0.03f;   // metres
        private const float LINE_ALPHA      = 0.5f;
        private const float LINE_END_GAP    = 1.0f;    // the line stops this far from both steps
        private const float DASH_LENGTH     = 0.5f;
        private const float DASH_GAP        = 0.4f;
        private const float RING_RADIUS     = 0.55f;
        private const float RING_WIDTH      = 0.06f;
        private const int   RING_SEGMENTS   = 24;
        private const float LIFT            = 0.05f;   // above the recorded foot position, so the ground does not hide it
        private const float LATER_ALPHA     = 0.45f;   // the steps after the next one are drawn fainter
        private const float MAX_LINE_LENGTH = 30f;     // longer gaps are unresolved transitions, not a walkable line

        private static readonly Color WalkColor = Color.White;
        private static readonly Color RunColor  = Color.Gold;
        private static readonly Color JumpColor = new Color(255, 140, 0);
        private static readonly Color FlyColor  = new Color(120, 200, 255);

        private readonly object _lock = new object();
        private VertexPositionColor[] _vertices = new VertexPositionColor[0];
        private BasicEffect           _effect;
        private bool                  _added;
        private bool                  _disposed;

        public float DrawOrder { get; private set; }

        public void Show(IList<RouteStep> steps, RouteStep from) {
            var list = new List<VertexPositionColor>();

            RouteStep previous = from;
            bool      first    = true;
            foreach (var step in steps) {
                var colour = ColorFor(step.Type) * (first ? 1f : LATER_ALPHA);
                if (previous != null && !step.BreakBefore) AddDashedLine(list, ToWorld(previous), ToWorld(step), colour * LINE_ALPHA);
                AddRing(list, ToWorld(step), colour);
                previous = step;
                first    = false;
            }

            lock (_lock) _vertices = list.ToArray();
            Attach();
        }

        public void Hide() {
            lock (_lock) _vertices = new VertexPositionColor[0];
        }

        private void Attach() {
            if (_added || _disposed) return;
            GameService.Graphics.World.AddEntity(this);
            _added = true;
        }

        public void Update(GameTime gameTime) {
            // Blish draws the largest DrawOrder first, so 0 draws this last and the translucent ribbon blends over
            // other entities.
            this.DrawOrder = 0;
        }

        public void Render(GraphicsDevice graphicsDevice, IWorld world, ICamera camera) {
            VertexPositionColor[] vertices;
            lock (_lock) vertices = _vertices;
            if (vertices.Length < 3) return;

            if (_effect == null) {
                _effect = new BasicEffect(graphicsDevice) { VertexColorEnabled = true, LightingEnabled = false, TextureEnabled = false };
            }

            var playerCamera = GameService.Gw2Mumble.PlayerCamera;
            _effect.World      = Matrix.Identity;
            _effect.View       = playerCamera.View;
            _effect.Projection = playerCamera.Projection;

            var previousBlend = graphicsDevice.BlendState;
            var previousDepth = graphicsDevice.DepthStencilState;
            var previousCull  = graphicsDevice.RasterizerState;
            graphicsDevice.BlendState        = BlendState.AlphaBlend;
            graphicsDevice.DepthStencilState = DepthStencilState.None;
            graphicsDevice.RasterizerState   = RasterizerState.CullNone;

            foreach (var pass in _effect.CurrentTechnique.Passes) {
                pass.Apply();
                graphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3);
            }

            graphicsDevice.BlendState        = previousBlend;
            graphicsDevice.DepthStencilState = previousDepth;
            graphicsDevice.RasterizerState   = previousCull;
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            Hide();
            if (_added) {
                GameService.Graphics.World.RemoveEntity(this);
                _added = false;
            }
            _effect?.Dispose();
            _effect = null;
        }

        // Recorded positions are Blish's world vectors already: X east, Y north, Z up, in metres.
        private static Vector3 ToWorld(RouteStep step) => new Vector3(step.X, step.Y, step.Z);

        private static Color ColorFor(StepType type) {
            switch (type) {
                case StepType.Run:  return RunColor;
                case StepType.Jump: return JumpColor;
                case StepType.Fly:  return FlyColor;
                default:            return WalkColor;
            }
        }

        /// <summary>
        /// A thin dashed line between two steps that stops short of both ends, so the edge you jump from and the spot
        /// you land on stay visible. Lying flat, widened sideways, so it reads from above and at an angle.
        /// </summary>
        private static void AddDashedLine(List<VertexPositionColor> list, Vector3 a, Vector3 b, Color colour) {
            var   dir    = b - a;
            float length = dir.Length();
            if (length <= 2 * LINE_END_GAP + 0.1f || length > MAX_LINE_LENGTH) return;

            var unit = dir / length;
            var flat = new Vector3(dir.X, dir.Y, 0);
            var side = flat.Length() > 0.01f ? Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitZ)) * LINE_HALF_WIDTH : Vector3.UnitX * LINE_HALF_WIDTH;
            var up   = Vector3.UnitZ * LIFT;

            for (float t = LINE_END_GAP; t < length - LINE_END_GAP; t += DASH_LENGTH + DASH_GAP) {
                float end = Math.Min(t + DASH_LENGTH, length - LINE_END_GAP);
                var   p   = a + unit * t + up;
                var   q   = a + unit * end + up;
                Quad(list, p - side, p + side, q + side, q - side, colour);
            }
        }

        /// <summary>A thin ring lying flat around the spot to reach. The spot itself stays clear.</summary>
        private static void AddRing(List<VertexPositionColor> list, Vector3 at, Color colour) {
            var   centre = at + Vector3.UnitZ * LIFT;
            float inner  = RING_RADIUS - RING_WIDTH, outer = RING_RADIUS;
            for (int i = 0; i < RING_SEGMENTS; i++) {
                double a0 = 2 * Math.PI * i / RING_SEGMENTS, a1 = 2 * Math.PI * (i + 1) / RING_SEGMENTS;
                var    d0 = new Vector3((float)Math.Cos(a0), (float)Math.Sin(a0), 0);
                var    d1 = new Vector3((float)Math.Cos(a1), (float)Math.Sin(a1), 0);
                Quad(list, centre + d0 * inner, centre + d0 * outer, centre + d1 * outer, centre + d1 * inner, colour);
            }
        }

        private static void Quad(List<VertexPositionColor> list, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour) {
            Tri(list, a, b, c, colour);
            Tri(list, a, c, d, colour);
        }

        private static void Tri(List<VertexPositionColor> list, Vector3 a, Vector3 b, Vector3 c, Color colour) {
            list.Add(new VertexPositionColor(a, colour));
            list.Add(new VertexPositionColor(b, colour));
            list.Add(new VertexPositionColor(c, colour));
        }

    }

    /// <summary>Creates the renderer. The test harness provides a stub with the same name instead.</summary>
    internal static class RoutePathFactory {
        public static IRoutePath Create() => new RoutePathEntity();
    }

}
