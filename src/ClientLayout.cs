using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace QqmBetterDownload {
    // QQ exposes its header and player through accessibility. Their actual
    // rectangles follow the expanded/collapsed sidebar and client DPI.
    internal sealed class ClientLayout {
        readonly IntPtr window;
        readonly int pid;
        Task pending;
        DateTime next;
        internal sealed class Area { internal System.Windows.Rect Header, Player; }
        volatile Area area;
        internal ClientLayout(IntPtr parent) { window = parent; uint id; NativeBridge.GetWindowThreadProcessId(parent, out id); pid = (int)id; Refresh(); }
        internal void Refresh() {
            if (DateTime.UtcNow < next || (pending != null && !pending.IsCompleted)) return;
            next = DateTime.UtcNow.AddSeconds(1);
            pending = Task.Run((Action)delegate {
                try {
                    var root = AutomationElement.FromHandle(window);
                    var header = FindBand(root, "后退", 25, 135);
                    var player = FindBand(root, "播放队列", 45, 170);
                    if (!header.IsEmpty && !player.IsEmpty && player.Top > header.Bottom) area = new Area { Header = header, Player = player };
                } catch (ElementNotAvailableException) { } catch (InvalidOperationException) { } catch (ArgumentException) { } catch (COMException) { }
            });
        }
        System.Windows.Rect FindBand(AutomationElement root, string name, double min, double max) {
            var element = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, pid), new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            var result = System.Windows.Rect.Empty;
            double dpi = root.Current.BoundingRectangle.Width;
            // The physical window/96-dpi ratio also works for DPI-unaware QQ builds.
            double ratio;
            using (new ClientUi.DpiScope(window)) { ClientUi.Rect native; ClientUi.GetWindowRect(window, out native); ratio = native.Width > 0 ? dpi / native.Width * ClientUi.Scale(window) : 1; }
            for (int i = 0; element != null && i < 9; i++, element = TreeWalker.ControlViewWalker.GetParent(element)) {
                var r = element.Current.BoundingRectangle;
                if (r.Width > 400 * ratio && r.Height >= min * ratio && r.Height <= max * ratio && (result.IsEmpty || r.Width > result.Width)) result = r;
                if (element == root) break;
            }
            return result;
        }
        internal System.Drawing.Rectangle Bounds(ClientUi.Rect client) {
            double scale = ClientUi.Scale(window); var snapshot = area;
            int left = (int)(232 * scale), top = (int)(76 * scale), bottom = client.Height - (int)(104 * scale);
            if (snapshot != null) {
                var origin = Logical(snapshot.Header.Left, snapshot.Header.Bottom);
                var lower = Logical(snapshot.Player.Left, snapshot.Player.Top);
                if (origin.X >= 40 * scale && origin.X <= 350 * scale && origin.Y > 25 * scale && origin.Y < 150 * scale && lower.Y > client.Height / 2) {
                    left = Math.Max(origin.X, lower.X); top = origin.Y; bottom = lower.Y - (int)(12 * scale);
                }
            }
            return new System.Drawing.Rectangle(left, top, Math.Max(0, client.Width - left - (int)(16 * scale)), Math.Max(0, bottom - top));
        }
        ClientUi.Point Logical(double x, double y) {
            var point = new ClientUi.Point((int)Math.Round(x), (int)Math.Round(y));
            PhysicalToLogicalPointForPerMonitorDPI(window, ref point); ScreenToClient(window, ref point); return point;
        }
        [DllImport("user32")] static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr window, ref ClientUi.Point point);
        [DllImport("user32")] static extern bool ScreenToClient(IntPtr window, ref ClientUi.Point point);
    }
}
