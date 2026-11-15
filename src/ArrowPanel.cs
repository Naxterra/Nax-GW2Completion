using System;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Nax.Completion {

    /// <summary>
    /// Small on-screen panel: a direction arrow (TomTom style) plus target name, distance and what is left on the map.
    /// Click-through, except while Ctrl is held: then it can be dragged anywhere, without going to the settings. With
    /// "Lock panel position" unticked it can be dragged without Ctrl as well.
    /// </summary>
    public sealed class ArrowPanel : Control {

        public const int PANEL_WIDTH  = 380;
        public const int PANEL_HEIGHT = 108;

        private const int ARROW_BOX = 84;

        /// <summary>Clockwise screen rotation in radians; 0 means "straight ahead" (arrow points up).</summary>
        public float  ArrowRotation { get; set; }
        public bool   HasTarget     { get; set; }
        public bool   ArrowVisible  { get; set; } = true;
        public Color  ArrowColor    { get; set; } = Color.White;
        public string Title         { get; set; } = string.Empty;
        public string Detail        { get; set; } = string.Empty;
        public string Hint          { get; set; } = string.Empty;
        public string Summary       { get; set; } = string.Empty;

        /// <summary>When false the panel captures the mouse so it can be dragged.</summary>
        public bool Locked { get; set; } = true;

        public event EventHandler<EventArgs> DragFinished;

        private bool  _dragging;
        private Point _dragOffset;

        public ArrowPanel() {
            this.Size        = new Point(PANEL_WIDTH, PANEL_HEIGHT);
            this.ClipsBounds = true;
        }

        /// <summary>Holding exactly Ctrl makes the panel movable for the moment.</summary>
        private static bool MoveKeyHeld => GameService.Input.Keyboard.ActiveModifiers == ModifierKeys.Ctrl;

        private bool Movable => !this.Locked || MoveKeyHeld || _dragging;

        protected override CaptureType CapturesInput() {
            return this.Movable ? CaptureType.Mouse : CaptureType.None;
        }

        protected override void OnLeftMouseButtonPressed(MouseEventArgs e) {
            base.OnLeftMouseButtonPressed(e);
            if (!this.Movable) return;

            _dragging   = true;
            _dragOffset = GameService.Input.Mouse.Position - this.Location;
        }

        /// <summary>
        /// Called every frame by the module. Blish only picks the control that gets the next click when the cursor
        /// moves, so pressing Ctrl over a resting cursor arms the panel here (otherwise the click went to the game and
        /// nothing moved). Releasing Ctrl hands the mouse back. A drag keeps going until the mouse button is released.
        /// </summary>
        public void UpdateDrag() {
            if (!_dragging) {
                var  mouse = GameService.Input.Mouse;
                var  at    = mouse.Position;
                bool over  = at.X >= this.Location.X && at.X < this.Location.X + this.Size.X
                          && at.Y >= this.Location.Y && at.Y < this.Location.Y + this.Size.Y;
                if (this.Locked) {
                    bool wanted = this.Visible && over && MoveKeyHeld;
                    if (mouse.ActiveControl == this && !wanted) {
                        mouse.UnsetActiveControl();
                    } else if (mouse.ActiveControl == null && wanted && ControlArming.MayArm(mouse)) {
                        ControlArming.TryArm(mouse, this);
                    }
                }
                return;
            }

            // A drag started with Ctrl keeps going until the mouse button is released, even if Ctrl is let go first.
            if (GameService.Input.Mouse.State.LeftButton != ButtonState.Pressed) {
                _dragging = false;
                DragFinished?.Invoke(this, EventArgs.Empty);
                return;
            }

            var parentSize = this.Parent?.Size ?? new Point(int.MaxValue, int.MaxValue);
            var target     = GameService.Input.Mouse.Position - _dragOffset;

            this.Location = new Point(MathHelper.Clamp(target.X, 0, Math.Max(0, parentSize.X - this.Width)),
                                      MathHelper.Clamp(target.Y, 0, Math.Max(0, parentSize.Y - this.Height)));
        }

        protected override void Paint(SpriteBatch spriteBatch, Rectangle bounds) {
            var pixel = ContentService.Textures.Pixel;

            spriteBatch.DrawOnCtrl(this, pixel, bounds, Color.Black * 0.55f);

            if (this.Movable) {
                // Thin yellow frame so it is obvious the panel can be moved right now.
                spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(0, 0, bounds.Width, 2), Color.Gold);
                spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(0, bounds.Height - 2, bounds.Width, 2), Color.Gold);
                spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(0, 0, 2, bounds.Height), Color.Gold);
                spriteBatch.DrawOnCtrl(this, pixel, new Rectangle(bounds.Width - 2, 0, 2, bounds.Height), Color.Gold);
            }

            if (this.HasTarget && this.ArrowVisible) {
                DrawArrow(spriteBatch, new Vector2(ARROW_BOX / 2f + 4, bounds.Height / 2f), this.ArrowRotation, this.ArrowColor);
            }

            int textX     = this.HasTarget && this.ArrowVisible ? ARROW_BOX + 8 : 10;
            int textWidth = bounds.Width - textX - 6;

            // One short fact per line; the module keeps each string short enough for this width.
            spriteBatch.DrawStringOnCtrl(this, this.Title,   GameService.Content.DefaultFont16, new Rectangle(textX, 4,  textWidth, 24), Color.White,      false, true);
            spriteBatch.DrawStringOnCtrl(this, this.Detail,  GameService.Content.DefaultFont14, new Rectangle(textX, 30, textWidth, 22), this.ArrowColor, false, true);
            spriteBatch.DrawStringOnCtrl(this, this.Hint,    GameService.Content.DefaultFont14, new Rectangle(textX, 54, textWidth, 22), Color.LightGray,  false, true);
            spriteBatch.DrawStringOnCtrl(this, this.Summary, GameService.Content.DefaultFont14, new Rectangle(textX, 78, textWidth, 22), Color.LightGray,  false, true);
        }

        private void DrawArrow(SpriteBatch spriteBatch, Vector2 center, float rotation, Color color) {
            // Arrow shape pointing up, rotated clockwise by "rotation".
            Vector2 tip   = center + Rotate(new Vector2(0,   -32), rotation);
            Vector2 tail  = center + Rotate(new Vector2(0,    28), rotation);
            Vector2 left  = center + Rotate(new Vector2(-16, -12), rotation);
            Vector2 right = center + Rotate(new Vector2(16,  -12), rotation);

            DrawLine(spriteBatch, tail, tip,   7, Color.Black * 0.8f);
            DrawLine(spriteBatch, left, tip,   7, Color.Black * 0.8f);
            DrawLine(spriteBatch, right, tip,  7, Color.Black * 0.8f);

            DrawLine(spriteBatch, tail, tip,   4, color);
            DrawLine(spriteBatch, left, tip,   4, color);
            DrawLine(spriteBatch, right, tip,  4, color);
        }

        private static Vector2 Rotate(Vector2 v, float radians) {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            // Screen space has Y pointing down, so this turns the vector clockwise on screen.
            return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        private void DrawLine(SpriteBatch spriteBatch, Vector2 from, Vector2 to, int thickness, Color color) {
            Vector2 delta  = to - from;
            float   length = delta.Length();
            if (length < 0.5f) return;

            float angle = (float)Math.Atan2(delta.Y, delta.X);

            spriteBatch.DrawOnCtrl(this,
                                   ContentService.Textures.Pixel,
                                   new Rectangle((int)Math.Round(from.X), (int)Math.Round(from.Y), (int)Math.Round(length), thickness),
                                   null,
                                   color,
                                   angle,
                                   new Vector2(0f, 0.5f));
        }

    }

}
