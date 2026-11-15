using System;
using System.Runtime.InteropServices;

namespace Nax.Completion {

    /// <summary>
    /// Copies pixels of the game window. It asks Windows to render the window itself (PrintWindow with
    /// PW_RENDERFULLCONTENT), which works even when the focused game is shown through a hardware overlay plane that a
    /// plain screen copy cannot see, and which never includes Blish's own drawing. The whole client area is rendered
    /// into one reused bitmap (about 30 ms at 3440x1440), and only the requested rectangle is copied out.
    /// </summary>
    internal static class ScreenCapture {

        private const uint PW_CLIENTONLY        = 0x00000001;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader {
            public uint   Size;
            public int    Width;
            public int    Height;
            public ushort Planes;
            public ushort BitCount;
            public uint   Compression;
            public uint   SizeImage;
            public int    XPelsPerMeter;
            public int    YPelsPerMeter;
            public uint   ClrUsed;
            public uint   ClrImportant;
        }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);

        private static readonly object Lock = new object();
        private static IntPtr _dc;
        private static IntPtr _bitmap;
        private static IntPtr _bits;
        private static int    _width;
        private static int    _height;

        /// <summary>Set by the module each frame: the GW2 main window.</summary>
        public static IntPtr GameWindow { get; set; }

        /// <summary>Client area size of the game window in physical pixels, or false when it is not available.</summary>
        public static bool TryGetClientSize(out int width, out int height) {
            width = height = 0;
            if (GameWindow == IntPtr.Zero || !GetClientRect(GameWindow, out var rect)) return false;

            width  = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }

        /// <summary>
        /// Renders the game window and copies the given client rectangle into 0xAARRGGBB pixels, row by row. Throws
        /// when the window cannot be rendered or the rectangle lies outside the client area.
        /// </summary>
        public static int[] Capture(int left, int top, int width, int height) {
            lock (Lock) {
                if (!TryGetClientSize(out int clientWidth, out int clientHeight)) throw new InvalidOperationException("The game window is not available.");
                if (left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > clientWidth || top + height > clientHeight) {
                    throw new ArgumentOutOfRangeException(nameof(left), "The capture rectangle lies outside the game window.");
                }

                EnsureBitmap(clientWidth, clientHeight);
                if (!PrintWindow(GameWindow, _dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) throw new InvalidOperationException("PrintWindow failed.");

                var pixels = new int[width * height];
                for (int y = 0; y < height; y++) {
                    IntPtr row = IntPtr.Add(_bits, ((top + y) * clientWidth + left) * 4);
                    Marshal.Copy(row, pixels, y * width, width);
                }

                return pixels;
            }
        }

        private static void EnsureBitmap(int width, int height) {
            if (_bitmap != IntPtr.Zero && _width == width && _height == height) return;

            Release();

            IntPtr screen = GetDC(IntPtr.Zero);
            try {
                _dc = CreateCompatibleDC(screen);
                var info = new BitmapInfoHeader {
                    Size     = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                    Width    = width,
                    Height   = -height,   // top-down rows
                    Planes   = 1,
                    BitCount = 32
                };
                _bitmap = CreateDIBSection(screen, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero || _dc == IntPtr.Zero) {
                    Release();
                    throw new InvalidOperationException("Could not create the capture bitmap.");
                }

                SelectObject(_dc, _bitmap);
                _width  = width;
                _height = height;
            } finally {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>Frees the bitmap, for example when the module unloads.</summary>
        public static void Release() {
            lock (Lock) {
                if (_dc != IntPtr.Zero)     DeleteDC(_dc);
                if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
                _dc = _bitmap = _bits = IntPtr.Zero;
                _width = _height = 0;
            }
        }

    }

}
