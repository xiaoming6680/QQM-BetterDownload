using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace QqmBetterDownload {
    internal static class ClientUi {
        internal const int Child = 0x40000000, Visible = 0x10000000, Layered = 0x80000, NoActivate = 0x08000000;
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct Placement { public int Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect NormalPosition; }
        [DllImport("user32")] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr window, string name);
        delegate bool EnumWindow(IntPtr window, IntPtr data);
        [DllImport("user32")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32")] static extern IntPtr GetWindow(IntPtr window, uint command);
        internal static bool IsClientWindow(IntPtr window, int processId) {
            uint pid; return IsWindow(window) && NativeBridge.GetWindowThreadProcessId(window, out pid) != 0 && pid == processId;
        }
        // Process.MainWindowHandle is cached and can refer to a startup window.
        // Select the application's full-size unowned window each time instead.
        internal static IntPtr MainWindow(Process process, IntPtr previous) {
            // A minimized window reports an empty client rectangle. Preserve a
            // validated HWND instead of mistaking it for a destroyed window.
            if (IsClientWindow(previous, process.Id) && IsIconic(previous)) return previous;
            IntPtr best = IntPtr.Zero; long area = 0;
            EnumWindows(delegate(IntPtr candidate, IntPtr data) {
                uint pid; NativeBridge.GetWindowThreadProcessId(candidate, out pid);
                if (pid != process.Id || GetWindow(candidate, 4) != IntPtr.Zero || (GetWindowLong(candidate, -20) & 0x80) != 0) return true;
                Rect rect; if (!GetClientRect(candidate, out rect)) return true;
                if (IsIconic(candidate)) {
                    // A freshly started agent has no previous HWND. Use the
                    // restored bounds so repair/startup also works while QQ is
                    // minimized, without forcing its window to the foreground.
                    var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                    if (!GetWindowPlacement(candidate, ref placement)) return true;
                    rect = placement.NormalPosition;
                }
                double scale = Scale(candidate);
                if (rect.Width < 640 * scale || rect.Height < 360 * scale) return true;
                if (!IsWindowVisible(candidate) && candidate != previous) return true;
                long size = (long)rect.Width * rect.Height;
                if (IsWindowVisible(candidate)) size += 1L << 40;
                if (size > area) { area = size; best = candidate; }
                return true;
            }, IntPtr.Zero);
            return best;
        }
        [DllImport("user32")] internal static extern bool ShowWindow(IntPtr window, int how);
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32")] internal static extern bool AllowSetForegroundWindow(int processId);
        // An explicit "open settings" request from the Start menu or installer
        // must be visible: restore a minimized or tray-hidden client first.
        internal static void Activate(IntPtr window) {
            if (!IsWindow(window)) return;
            if (IsIconic(window)) ShowWindow(window, 9); else if (!IsWindowVisible(window)) ShowWindow(window, 5);
            SetForegroundWindow(window);
        }
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
