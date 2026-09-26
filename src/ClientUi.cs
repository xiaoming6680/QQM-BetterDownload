using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QqmBetterDownload {
    internal static class ClientUi {
        internal const int Child = 0x40000000, Visible = 0x10000000, Layered = 0x80000, NoActivate = 0x08000000;
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [DllImport("user32")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32")] internal static extern bool ShowWindow(IntPtr window, int how);
        [DllImport("user32", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        internal static double Scale(IntPtr window) { try { uint dpi = GetDpiForWindow(window); return dpi == 0 ? 1 : dpi / 96.0; } catch (EntryPointNotFoundException) { return 1; } }
        // Create child HWNDs with the same DPI context as their foreign parent.
        internal sealed class DpiScope : IDisposable {
            readonly IntPtr previous;
            internal DpiScope(IntPtr parent) { try { previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(parent)); } catch (EntryPointNotFoundException) { } }
            public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
        internal static void RequireChild(IntPtr child, IntPtr parent) {
            if (GetParent(child) != parent || (GetWindowLong(child, -16) & Child) == 0 || (GetWindowLong(child, -20) & 8) != 0)
                throw new Win32Exception("无法在 QQ 音乐内部创建界面。");
        }
    }
}
