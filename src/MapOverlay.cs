using System;
using System.Reflection;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Nax.Completion {

    /// <summary>The key that must be held for a click to reach a frame instead of the game.</summary>
    public enum FrameClickKey {
        Alt,
        Ctrl,
        Shift
    }

    /// <summary>
    /// Full-screen layer shown while the world map is open. Draws a square frame around every tracked completion point
    /// (orange = module thinks it is still open, green = ticked) so it can be compared with the game's own icons.
    ///
    /// Frames are click-through unless exactly the <see cref="ClickKey"/> modifier is held. Without it, a click on a
    /// frame reaches the game, so the game's own dialogs and waypoint icons under a frame keep working.
    ///
    /// How a click is routed (Blish HUD v1.0.0, GameServices/Input/Mouse/MouseHandler.cs):
    ///  1. Input update N: if the cursor moved, Blish asks every control "do you capture here?" and stores the
    ///     answer as Mouse.ActiveControl. Our CapturesInput answers from the live cursor position and modifier keys.
    ///  2. Module update N (after input): the module calls <see cref="CaptureFrameState"/>. It hands the mouse back
    ///     when the key is released or no frame is under the cursor. Blish only picks a new ActiveControl when the
    ///     cursor moves, so when the key is pressed over a resting cursor it arms the frame itself (see
    ///     <see cref="TryArm"/>). Then it records whether we are the ActiveControl and which frame is under the cursor.
    ///     The white hover highlight shows when a frame is armed.
    ///  3. The click arrives on the mouse hook thread. GW2 gets it unless ActiveControl is set. ActiveControl is only
    ///     changed on the main thread, so between updates it is the state recorded in step 2.
    ///  4. Input update N+1 raises Mouse.LeftMouseButtonPressed. The module toggles <see cref="OwnedHover"/>, which
    ///     is only set when the click was kept from the game, so apart from the gaps below a click never acts on both.
    /// Accepted gaps, each at most one frame long:
    ///  - A click in the few milliseconds between releasing the key and the next module update is still kept from
    ///    the game, and it either toggles the frame or is lost.
    ///  - When the key goes down over a resting cursor and the click lands before the module update of that same
    ///    frame, the game gets the Alt+click (personal marker) and TryArm then also toggles the frame.
    /// </summary>
    public sealed class MapOverlay : Control {


        public sealed class Marker {
            public int             MapId;      // the map the point belongs to; the view can show several maps
            public CompletionPoint Point;
            public Vector2         Position;   // UI units, relative to this control (which sits at 0,0)
            public bool            Done;
            public int             Size;       // frame edge length; smaller when points sit close together
        }

        public const  int FRAME_SIZE   = 30;   // sits around the game's icon instead of covering it
        private const int MIN_SIZE     = 10;
        private const int CROWDED_SIZE = 18;   // below this the zoom hint is shown

        private static readonly Color OpenColor = new Color(255, 140, 0);
        private static readonly Color DoneColor = new Color(90, 220, 90);

        // Swapped by reference only; never modified after it is published. The mouse hook thread may read it.
        private Marker[] _markers = new Marker[0];

        /// <summary>False right after the map opens (the map is still animating), so nothing is captured then.</summary>
        public bool Interactive { get; set; }

        public bool AnyCrowded { get; private set; }

        /// <summary>Set by the module from its settings.</summary>
        public FrameClickKey ClickKey { get; set; } = FrameClickKey.Alt;

        /// <summary>
        /// True only when the chosen key is the sole modifier held, so combinations such as Alt+Shift (game markers)
        /// or AltGr (reported as Ctrl+Alt) still go to the game.
        /// </summary>
        private bool ClickKeyHeld {
            get {
                ModifierKeys wanted;
                switch (this.ClickKey) {
                    case FrameClickKey.Ctrl:  wanted = ModifierKeys.Ctrl;  break;
                    case FrameClickKey.Shift: wanted = ModifierKeys.Shift; break;
                    default:                  wanted = ModifierKeys.Alt;   break;
                }

                return GameService.Input.Keyboard.ActiveModifiers == wanted;
            }
        }

        /// <summary>The frame a click would toggle right now, or null. Set by <see cref="CaptureFrameState"/>.</summary>
        public Marker OwnedHover { get; private set; }

        public MapOverlay() {
            this.ClipsBounds = true;
        }

        public void SetMarkers(Marker[] markers) {
            bool crowded = false;

            // Shrink frames that sit close to another one so neighbouring hit boxes never overlap
            // and close pairs stay clickable (zoom in to make them bigger).
            for (int i = 0; i < markers.Length; i++) {
                float nearest = float.MaxValue;
                for (int j = 0; j < markers.Length; j++) {
                    if (i == j) continue;
                    float d = Math.Max(Math.Abs(markers[i].Position.X - markers[j].Position.X),
                                       Math.Abs(markers[i].Position.Y - markers[j].Position.Y));
                    if (d < nearest) nearest = d;
                }

                int size = nearest >= FRAME_SIZE + 2 ? FRAME_SIZE : (int)Math.Floor(nearest) - 2;
                markers[i].Size = Math.Max(MIN_SIZE, Math.Min(FRAME_SIZE, size));
                crowded |= markers[i].Size < CROWDED_SIZE;
            }

            this.AnyCrowded = crowded;
            _markers        = markers;
        }

        /// <summary>Frame under the given UI position (nearest centre wins), or null. Safe to call from any thread.</summary>
        public Marker HitTest(Point position) {
            if (!this.Visible || !this.Interactive) return null;

            Marker best       = null;
            float  bestDistSq = float.MaxValue;

            var markers = _markers;
            foreach (var marker in markers) {
                float half = marker.Size / 2f + 1;
                float dx   = position.X - marker.Position.X;
                float dy   = position.Y - marker.Position.Y;

                if (Math.Abs(dx) > half || Math.Abs(dy) > half) continue;

                float distSq = dx * dx + dy * dy;

                // Two points on the same spot (e.g. a heart and a hero challenge): the open one wins,
                // so each click ticks the next open one instead of always hitting the same entry.
                bool better = distSq < bestDistSq
                           || (distSq == bestDistSq && best != null && best.Done && !marker.Done);

                if (better) {
                    bestDistSq = distSq;
                    best       = marker;
                }
            }

            return best;
        }

        protected override CaptureType CapturesInput() {
            return this.ClickKeyHeld && HitTest(GameService.Input.Mouse.Position) != null ? CaptureType.Mouse : CaptureType.None;
        }

        /// <summary>
        /// Called once per frame by the module, after the markers are updated.
        /// Hands the mouse back if frames slid away from a resting cursor (zoom, pan) or the click key was released,
        /// then records the state the mouse hook will use for the next click.
        /// </summary>
        public void CaptureFrameState() {
            var  mouse   = GameService.Input.Mouse;
            var  hit     = HitTest(mouse.Position);
            bool keyHeld = this.ClickKeyHeld;

            // While GW2 is not focused Blish stops updating the mouse, so a recorded hover would go stale and could
            // toggle a frame on the click that brings the game back into focus.
            bool gameFocused = GameService.GameIntegration.Gw2Instance.Gw2HasFocus;

            if (mouse.ActiveControl == this && (hit == null || !keyHeld)) {
                mouse.UnsetActiveControl();
            } else if (mouse.ActiveControl == null && hit != null && keyHeld && gameFocused
                    && mouse.CursorIsVisible && !mouse.CameraDragging && !GameService.Overlay.InterfaceHidden) {
                // Without this, "point at it, then hold Alt and click" would hand the game an Alt+click.
                // Never while the camera is being dragged: Blish only notices the right button release while it
                // owns no control, and would stay stuck in camera-drag mode. Never while Blish's interface is hidden,
                // because Blish itself picks no control then.
                TryArm(mouse);
            }

            this.OwnedHover = gameFocused && mouse.ActiveControl == this ? hit : null;
        }

        /// <summary>Makes this overlay Blish's ActiveControl, as a cursor move onto a frame would.</summary>
        private void TryArm(MouseHandler mouse) {
            ControlArming.TryArm(mouse, this);
        }

        protected override void Paint(SpriteBatch spriteBatch, Rectangle bounds) {
            var markers = _markers;
            var hovered = this.OwnedHover;   // only highlight what a click would really hit

            foreach (var marker in markers) {
                if (marker == hovered) continue;   // drawn last, on top
                DrawMarker(spriteBatch, marker, false);
            }

            if (hovered != null) {
                DrawMarker(spriteBatch, hovered, true);
                DrawLabel(spriteBatch, bounds, hovered);
            }

            if (this.AnyCrowded) {
                var hintRect = new Rectangle(bounds.Width / 2 - 170, 60, 340, 26);
                spriteBatch.DrawOnCtrl(this, ContentService.Textures.Pixel, hintRect, Color.Black * 0.6f);
                spriteBatch.DrawStringOnCtrl(this, "Zoom in for bigger frames",
                                             GameService.Content.DefaultFont14, hintRect, Color.White,
                                             false, true, 1, HorizontalAlignment.Center, VerticalAlignment.Middle);
            }
        }

        private void DrawMarker(SpriteBatch spriteBatch, Marker marker, bool hovered) {
            var   pixel     = ContentService.Textures.Pixel;
            Color color     = marker.Done ? DoneColor : OpenColor;
            int   thickness = hovered ? 4 : (marker.Size < CROWDED_SIZE ? 1 : 2);
            int   size      = hovered ? marker.Size + 6 : marker.Size;

            int x = (int)Math.Round(marker.Position.X - size / 2f);
            int y = (int)Math.Round(marker.Position.Y - size / 2f);

            // Dark outline first so the frame stays readable on bright map art.
            DrawBox(spriteBatch, pixel, x - 1, y - 1, size + 2, thickness + 2, Color.Black * 0.7f);
            DrawBox(spriteBatch, pixel, x, y, size, thickness, hovered ? Color.White : color);
        }

        private void DrawBox(SpriteBatch spriteBatch, Texture2D pixel, int x, int y, int size, int t, Color color) {
            spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(x,            y,            size, t),    color);
            spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(x,            y + size - t, size, t),    color);
            spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(x,            y,            t,    size), color);
            spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(x + size - t, y,            t,    size), color);
        }

        private void DrawLabel(SpriteBatch spriteBatch, Rectangle bounds, Marker marker) {
            const int width  = 300;
            const int height = 46;

            string action = marker.Done ? "Ticked. Click to untick" : "Open. Click to tick";

            int x = (int)marker.Position.X + marker.Size / 2 + 8;
            int y = (int)marker.Position.Y - height / 2;

            // Keep the label on screen.
            if (x + width > bounds.Width) x = (int)marker.Position.X - marker.Size / 2 - 8 - width;
            y = MathHelper.Clamp(y, 0, Math.Max(0, bounds.Height - height));

            spriteBatch.DrawOnCtrl(this, ContentService.Textures.Pixel, new Rectangle(x, y, width, height), Color.Black * 0.75f);
            spriteBatch.DrawStringOnCtrl(this, marker.Point.ShortLabel, GameService.Content.DefaultFont14, new Rectangle(x + 8, y + 2,  width - 16, 20), Color.White,                          false, true);
            spriteBatch.DrawStringOnCtrl(this, action,                  GameService.Content.DefaultFont14, new Rectangle(x + 8, y + 23, width - 16, 20), marker.Done ? DoneColor : OpenColor, false, true);
        }

    }

}
