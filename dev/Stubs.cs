// Minimal stand-ins mirroring the real public signatures used by the module.
// Sources: Blish HUD v1.0.0 tag, Gw2Sharp v1.7.4 tag, MonoGame 3.8 public API.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Xna.Framework {
    public struct Vector2 {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public float Length() => (float)Math.Sqrt(X * X + Y * Y);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.X + b.X, a.Y + b.Y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.X - b.X, a.Y - b.Y);
    }
    public struct Vector3 {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float Length() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public static Vector3 UnitX => new Vector3(1, 0, 0);
        public static Vector3 UnitZ => new Vector3(0, 0, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.X * f, a.Y * f, a.Z * f);
        public static Vector3 operator /(Vector3 a, float f) => a * (1 / f);
        public static Vector3 Normalize(Vector3 a) => a / a.Length();
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
    public struct Matrix { public static Matrix Identity => default; }
    public struct Point {
        public int X, Y;
        public Point(int x, int y) { X = x; Y = y; } public static Point Zero => default;
        public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
        public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
    }
    public struct Rectangle {
        public int X, Y, Width, Height;
        public Rectangle(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }
    }
    public struct Color {
        public static Color White => default; public static Color Black => default; public static Color Gold => default;
        public static Color LightGray => default; public static Color LightGreen => default; public static Color Orange => default;
        public static Color operator *(Color c, float f) => c; public Color(int r, int g, int b) { }
    }
    public static class MathHelper { public static int Clamp(int v, int min, int max) => v; public static float Clamp(float v, float min, float max) => v; }
    public class GameTime { public TimeSpan ElapsedGameTime { get; } }
}
namespace Microsoft.Xna.Framework.Graphics {
    using Microsoft.Xna.Framework;
    public class Texture2D { }
    public class SpriteBatch { }
    public enum SpriteEffects { None }
    public struct VertexPositionColor { public Vector3 Position; public Color Color; public VertexPositionColor(Vector3 position, Color color) { Position = position; Color = color; } }
    public class BlendState { public static BlendState AlphaBlend { get; } = new BlendState(); }
    public class DepthStencilState { public static DepthStencilState None { get; } = new DepthStencilState(); }
    public class RasterizerState { public static RasterizerState CullNone { get; } = new RasterizerState(); }
    public enum PrimitiveType { TriangleList }
    public class GraphicsDevice {
        public BlendState BlendState { get; set; } public DepthStencilState DepthStencilState { get; set; } public RasterizerState RasterizerState { get; set; }
        public void DrawUserPrimitives<T>(PrimitiveType type, T[] vertices, int offset, int count) where T : struct { }
    }
    public class EffectPass { public void Apply() { } }
    public class EffectTechnique { public IEnumerable<EffectPass> Passes { get; } = new[] { new EffectPass() }; }
    public class BasicEffect : IDisposable {
        public BasicEffect(GraphicsDevice device) { }
        public bool VertexColorEnabled { get; set; } public bool LightingEnabled { get; set; } public bool TextureEnabled { get; set; }
        public Matrix World { get; set; } public Matrix View { get; set; } public Matrix Projection { get; set; }
        public EffectTechnique CurrentTechnique { get; } = new EffectTechnique();
        public void Dispose() { }
    }
}
namespace Blish_HUD.Entities {
    public interface IEntity : IDisposable { float DrawOrder { get; } void Update(Microsoft.Xna.Framework.GameTime time); void Render(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, IWorld world, ICamera camera); }
    public interface IWorld { }
    public interface ICamera { }
    public class World { public void AddEntity(IEntity entity) { } public void RemoveEntity(IEntity entity) { } }
}
namespace Microsoft.Xna.Framework.Input {
    public enum Keys { None, J, K, L, N, O, P, U }
    public enum ButtonState { Released, Pressed }
    public struct MouseState { public ButtonState LeftButton { get; } public Point Position { get; } }
    [Flags] public enum ModifierKeys { None = 0, Ctrl = 1, Alt = 2, Shift = 4 }
}
namespace MonoGame.Extended.BitmapFonts { public class BitmapFont { } }

namespace Gw2Sharp.Models {
    public readonly struct Coordinates2 { public double X { get; } public double Y { get; } }
    public readonly struct Size { public int Width { get; } public int Height { get; } }
    public enum MountType { None, Jackal, Griffon, Springer, Skimmer, Raptor, RollerBeetle, Warclaw, Skyscale, Skiff, SiegeTurtle }
}
namespace Gw2Sharp.WebApi.V2.Models {
    using Gw2Sharp.Models;
    public enum PoiType { Unknown, Landmark, Waypoint, Vista, Unlock }
    public enum TokenPermission { Account, Builds, Characters, Guilds, Inventories, Progression, Pvp, Tradingpost, Unlocks, Wallet }
    public class ApiEnum<T> where T : struct { public bool IsUnknown { get; } public T Value { get; } }
    public struct Rectangle { public double Width => 0; public double Height => 0; }
    public class Map {
        public int Id { get; set; } public string Name { get; set; } public int DefaultFloor { get; set; }
        public IReadOnlyList<int> Floors { get; set; } public int RegionId { get; set; } public int ContinentId { get; set; }
        public Rectangle MapRect { get; set; } public Rectangle ContinentRect { get; set; }
    }
    public class ContinentFloorRegionMapPoi { public int Id { get; set; } public string Name { get; set; } public int Floor { get; set; } public Coordinates2 Coord { get; set; } public ApiEnum<PoiType> Type { get; set; } }
    public class ContinentFloorRegionMapTask { public int Id { get; set; } public string Objective { get; set; } public Coordinates2 Coord { get; set; } }
    public class ContinentFloorRegionMapSkillChallenge { public string Id { get; set; } public Coordinates2 Coord { get; set; } }
    public class AccountAchievement { public int Id { get; set; } public bool Done { get; set; } }
    public class ContinentFloorRegionMap {
        public IReadOnlyDictionary<int, ContinentFloorRegionMapPoi> PointsOfInterest { get; set; }
        public IReadOnlyDictionary<int, ContinentFloorRegionMapTask> Tasks { get; set; }
        public IReadOnlyList<ContinentFloorRegionMapSkillChallenge> SkillChallenges { get; set; }
    }
}
namespace Gw2Sharp.WebApi.V2.Clients {
    using Gw2Sharp.WebApi.V2.Models;
    public interface IBlobClient<TObject> { Task<TObject> GetAsync(CancellationToken cancellationToken = default); }
    public interface IBulkExpandableClient<TObject, TId> { Task<TObject> GetAsync(TId id, CancellationToken cancellationToken = default); }
    public interface IMapsClient : IBulkExpandableClient<Map, int> { }
    public interface IContinentsFloorsRegionsMapsIdClient : IBlobClient<ContinentFloorRegionMap> { }
    public interface IContinentsFloorsRegionsMapsClient { IContinentsFloorsRegionsMapsIdClient this[int mapId] { get; } }
    public interface IContinentsFloorsRegionsIdClient { IContinentsFloorsRegionsMapsClient Maps { get; } }
    public interface IContinentsFloorsRegionsClient { IContinentsFloorsRegionsIdClient this[int regionId] { get; } }
    public interface IContinentsFloorsIdClient { IContinentsFloorsRegionsClient Regions { get; } }
    public interface IContinentsFloorsClient { IContinentsFloorsIdClient this[int floorId] { get; } }
    public interface IContinentsIdClient { IContinentsFloorsClient Floors { get; } }
    public interface IContinentsClient { IContinentsIdClient this[int continentId] { get; } }
    public interface ICharactersIdHeroPointsClient { Task<List<string>> GetAsync(); }
    public interface ICharactersIdClient { ICharactersIdHeroPointsClient HeroPoints { get; } }
    public interface ICharactersClient { ICharactersIdClient this[string name] { get; } }
    public interface IAccountAchievementsClient { Task<List<AccountAchievement>> GetAsync(); }
    public interface IAccountClient { IAccountAchievementsClient Achievements { get; } }
}
namespace Gw2Sharp.WebApi.V2 {
    using Gw2Sharp.WebApi.V2.Clients;
    public interface IGw2WebApiV2Client { IContinentsClient Continents { get; } IMapsClient Maps { get; } Gw2Sharp.WebApi.V2.Clients.ICharactersClient Characters { get; } Gw2Sharp.WebApi.V2.Clients.IAccountClient Account { get; } }
}
namespace Gw2Sharp.WebApi.Exceptions { public class NotFoundException : Exception { } }
namespace Gw2Sharp.WebApi {
    public interface IGw2WebApiClient { Gw2Sharp.WebApi.V2.IGw2WebApiV2Client V2 { get; } }
}

namespace Blish_HUD {
    using Microsoft.Xna.Framework;
    using Microsoft.Xna.Framework.Graphics;
    using MonoGame.Extended.BitmapFonts;
    using Blish_HUD.Controls;
    public class Logger {
        public static Logger GetLogger<T>() => new Logger();
        public void Info(string message, params object[] args) { }
        public void Warn(Exception exception, string message, params object[] args) { }
        public void Debug(Exception exception, string message, params object[] args) { }
    }
    public class ValueEventArgs<T> : EventArgs { public T Value { get; } public ValueEventArgs(T value) { Value = value; } }
    public class ValueChangedEventArgs<T> : EventArgs { public T PreviousValue { get; } public T NewValue { get; } }
    public class ContentService {
        public static class Textures { public static Texture2D Pixel { get; } }
        public BitmapFont DefaultFont14 { get; } public BitmapFont DefaultFont16 { get; }
    }
    public class GraphicsService { public Screen SpriteScreen { get; } = new Screen(); public float UIScaleMultiplier { get; set; } = 0.897f; public Blish_HUD.Entities.World World { get; } = new Blish_HUD.Entities.World(); }
    public class OverlayService { public bool InterfaceHidden; }
    public class InputService { public Blish_HUD.Input.MouseHandler Mouse { get; } = new Blish_HUD.Input.MouseHandler(); public Blish_HUD.Input.KeyboardHandler Keyboard { get; } = new Blish_HUD.Input.KeyboardHandler(); }
    public class Gw2MumbleService {
        public bool IsAvailable { get; set; } = true;
        public Blish_HUD.Gw2Mumble.PlayerCharacter PlayerCharacter { get; } = new Blish_HUD.Gw2Mumble.PlayerCharacter();
        public Blish_HUD.Gw2Mumble.PlayerCamera PlayerCamera { get; } = new Blish_HUD.Gw2Mumble.PlayerCamera();
        public Blish_HUD.Gw2Mumble.UI UI { get; } = new Blish_HUD.Gw2Mumble.UI();
        public Blish_HUD.Gw2Mumble.CurrentMap CurrentMap { get; } = new Blish_HUD.Gw2Mumble.CurrentMap();
        public int Tick { get; set; }
    }
    public class Gw2InstanceIntegration { public bool Gw2HasFocus { get; set; } = true; public bool IsInGame { get; set; } = true; public IntPtr Gw2WindowHandle { get; set; } }
    public class GameIntegrationService { public Gw2InstanceIntegration Gw2Instance { get; } = new Gw2InstanceIntegration(); }
    public abstract class GameService {
        public static GameIntegrationService GameIntegration = new GameIntegrationService();
        public static readonly ContentService Content = new ContentService(); public static readonly Gw2MumbleService Gw2Mumble = new Gw2MumbleService();
        public static readonly GraphicsService Graphics = new GraphicsService(); public static InputService Input = new InputService();
        public static OverlayService Overlay = new OverlayService();
    }
    public static class SpriteBatchExtensions {
        public static void DrawOnCtrl(this SpriteBatch spriteBatch, Control ctrl, Texture2D texture, Rectangle destinationRectangle, Color color) { }
        public static void DrawOnCtrl(this SpriteBatch spriteBatch, Control ctrl, Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color, float rotation, Vector2 origin, SpriteEffects effects = SpriteEffects.None) { }
        public static void DrawStringOnCtrl(this SpriteBatch spriteBatch, Control ctrl, string text, BitmapFont font, Rectangle destinationRectangle, Color color, bool wrap = false, HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left, VerticalAlignment verticalAlignment = VerticalAlignment.Middle) { }
        public static void DrawStringOnCtrl(this SpriteBatch spriteBatch, Control ctrl, string text, BitmapFont font, Rectangle destinationRectangle, Color color, bool wrap, bool stroke, int strokeDistance = 1, HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left, VerticalAlignment verticalAlignment = VerticalAlignment.Middle) { }
    }
}
namespace Blish_HUD.Gw2Mumble {
    using Microsoft.Xna.Framework;
    public class PlayerCharacter { public event EventHandler<ValueEventArgs<string>> NameChanged; public Vector3 Forward { get; } public Vector3 Position { get; set; } public string Name { get; } public Gw2Sharp.Models.MountType CurrentMount { get; } }
    public class PlayerCamera { public Vector3 Forward { get; } public Matrix View { get; } public Matrix Projection { get; } }
    public class UI { public bool IsMapOpen { get; set; } public Gw2Sharp.Models.Coordinates2 MapPosition { get; } public Gw2Sharp.Models.Coordinates2 MapCenter { get; } public double MapScale { get; set; } public Gw2Sharp.Models.Size CompassSize { get; } public double CompassRotation { get; } public bool IsCompassRotationEnabled { get; } public bool IsCompassTopRight { get; } }
    public class CurrentMap { public event EventHandler<ValueEventArgs<int>> MapChanged; public int Id { get; set; } public bool IsCompetitiveMode { get; } }
}
namespace Blish_HUD.Input {
    using Microsoft.Xna.Framework;
    using Microsoft.Xna.Framework.Input;
    public class MouseEventArgs : EventArgs { }
    public enum MouseEventType { MouseMoved }
    public class MouseHandler { public Point Position { get; set; } public MouseState State { get; } public bool CursorIsVisible { get; set; } = true; public bool CameraDragging { get; set; } public Blish_HUD.Controls.Control ActiveControl { get; set; } public void UnsetActiveControl() { ActiveControl = null; } public event EventHandler<MouseEventArgs> LeftMouseButtonPressed; }
    public class KeyboardHandler { public ModifierKeys ActiveModifiers { get; set; } public Blish_HUD.Controls.Control FocusedControl { get; set; } public System.Collections.Generic.IReadOnlyList<Keys> KeysDown { get; } = new Keys[0]; }
    public class KeyBinding {
        public event EventHandler<EventArgs> Activated;
        public bool Enabled { get; set; } public bool BlockSequenceFromGw2 { get; set; }
        public KeyBinding() { } public KeyBinding(Keys primaryKey) { } public KeyBinding(ModifierKeys modifierKeys, Keys primaryKey) { }
        public string GetBindingDisplayText() => "";
    }
}
namespace Blish_HUD.Controls {
    using Microsoft.Xna.Framework;
    using Microsoft.Xna.Framework.Graphics;
    using Blish_HUD.Input;
    public enum HorizontalAlignment { Left, Center, Right }
    public enum VerticalAlignment { Top, Middle, Bottom }
    [Flags] public enum CaptureType { None = 0, Filter = 1, Mouse = 4, MouseWheel = 8 }
    public class ResizedEventArgs : EventArgs { }
    public enum NotificationType { Info }
    public abstract class Control : IDisposable {
        public event EventHandler<ResizedEventArgs> Resized;
        public Point Location { get; set; } public Point Size { get; set; } public int Width { get; set; } public int Height { get; set; }
        public bool ClipsBounds { get; set; } public bool Visible { get; set; } public Container Parent { get; set; }
        protected virtual CaptureType CapturesInput() => CaptureType.Mouse;
        public virtual Control TriggerMouseInput(Blish_HUD.Input.MouseEventType mouseEventType, Microsoft.Xna.Framework.Input.MouseState ms) => this;
        protected virtual void OnLeftMouseButtonPressed(MouseEventArgs e) { }
        protected abstract void Paint(SpriteBatch spriteBatch, Rectangle bounds);
        public void Dispose() { }
    }
    public abstract class Container : Control { }
    public class Screen : Container {
        // Tests set the control Blish's own routing would pick under the cursor.
        public Control TestPick { get; set; }
        public override Control TriggerMouseInput(Blish_HUD.Input.MouseEventType mouseEventType, Microsoft.Xna.Framework.Input.MouseState ms) => TestPick;
        protected override void Paint(SpriteBatch s, Rectangle b) { }
    }
    public class TextBox : Control {
        public event EventHandler<EventArgs> EnterPressed;
        public event EventHandler<ValueEventArgs<bool>> InputFocusChanged;
        public string Text { get; set; } public string PlaceholderText { get; set; } public bool Focused { get; set; } public int MaxLength { get; set; }
        public void UnsetFocus() { Focused = false; }
        protected override void Paint(SpriteBatch s, Rectangle b) { }
    }
    public class ScreenNotification : Control {
        public static void ShowNotification(string message, NotificationType type = NotificationType.Info, Texture2D icon = null, int duration = 4) { }
        protected override void Paint(SpriteBatch s, Rectangle b) { }
    }
}
namespace Blish_HUD.Settings {
    public class SettingEntry<T> { public event EventHandler<ValueChangedEventArgs<T>> SettingChanged; public T Value { get; set; } }
    public sealed class SettingCollection {
        public SettingEntry<TEntry> DefineSetting<TEntry>(string entryKey, TEntry defaultValue, Func<string> displayNameFunc = null, Func<string> descriptionFunc = null) => new SettingEntry<TEntry> { Value = defaultValue };
    }
    public static class SettingComplianceExtensions { public static void SetRange(this SettingEntry<int> setting, int minValue = 0, int maxValue = 1) { } }
}
namespace Blish_HUD.Modules.Managers {
    public class DirectoriesManager { public string GetFullDirectoryPath(string directoryName) => null; }
    public class Gw2ApiManager { public Gw2Sharp.WebApi.IGw2WebApiClient Gw2ApiClient { get; } public bool HasPermissions(IEnumerable<Gw2Sharp.WebApi.V2.Models.TokenPermission> p) => false; }
}
namespace Blish_HUD.Modules {
    using Microsoft.Xna.Framework;
    using Blish_HUD.Modules.Managers;
    using Blish_HUD.Settings;
    public class ModuleParameters { public DirectoriesManager DirectoriesManager { get; } public Gw2ApiManager Gw2ApiManager { get; } }
    public abstract class Module : IDisposable {
        protected readonly ModuleParameters ModuleParameters;
        protected Module(ModuleParameters moduleParameters) { ModuleParameters = moduleParameters; }
        protected virtual void DefineSettings(SettingCollection settings) { }
        protected virtual void Initialize() { }
        protected virtual Task LoadAsync() => Task.CompletedTask;
        protected virtual void OnModuleLoaded(EventArgs e) { }
        protected virtual void Update(GameTime gameTime) { }
        protected virtual void Unload() { }
        public void Dispose() { }
    }
}
