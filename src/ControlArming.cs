using System;
using System.Reflection;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;

namespace Nax.Completion {

    /// <summary>
    /// Blish only picks a new ActiveControl (the control that gets the next click) when the cursor moves. A control
    /// that starts to capture the mouse because a key went down over a resting cursor would therefore let the next
    /// click through to the game. This makes the control the ActiveControl right away, as a cursor move onto it would.
    /// Used by the map frames (frame click key) and the arrow panel (Ctrl to move it).
    /// </summary>
    internal sealed class ControlArming {

        private static readonly Logger Logger = Logger.GetLogger<ControlArming>();

        private ControlArming() { }

        // Blish 1.0.0 keeps this setter private. If a later version renames it, arming quietly does nothing and the
        // control is armed by the next cursor move instead.
        private static readonly MethodInfo ActiveControlSetter =
            typeof(MouseHandler).GetProperty("ActiveControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?.GetSetMethod(true);

        private static bool _failed;

        /// <summary>
        /// Makes <paramref name="control"/> the ActiveControl if Blish's own routing would pick it under the cursor
        /// right now, so another window over it keeps its priority. Returns true when it is armed.
        /// </summary>
        public static bool TryArm(MouseHandler mouse, Control control) {
            if (ActiveControlSetter == null || _failed) return false;

            try {
                var picked = GameService.Graphics.SpriteScreen.TriggerMouseInput(MouseEventType.MouseMoved, mouse.State);
                if (picked != control) return false;

                ActiveControlSetter.Invoke(mouse, new object[] { control });
                return true;
            } catch (Exception ex) {
                _failed = true;
                Logger.Warn(ex, "Could not arm a control directly. Controls now arm on the next cursor move.");
                return false;
            }
        }

        /// <summary>True when Blish could hand a click to one of our controls at all right now.</summary>
        public static bool MayArm(MouseHandler mouse) {
            return GameService.GameIntegration.Gw2Instance.Gw2HasFocus && mouse.CursorIsVisible && !mouse.CameraDragging
                && !GameService.Overlay.InterfaceHidden;
        }

    }

}
