using System;
using System.Diagnostics;
using System.Threading;

namespace QqmBetterDownload {
    internal static class WorkerSession {
        internal static string Name(Process parent, string signal) {
            return "Local\\QQM-BetterDownload.Worker." + parent.Id + "." + parent.StartTime.ToUniversalTime().Ticks + "." + signal;
        }
        internal static bool Alive(Process parent) {
            try { using (var handle = EventWaitHandle.OpenExisting(Name(parent, "Alive"))) return handle.WaitOne(0); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
    }
    // The UI message loop may already have lost its foreign parent HWND or be
    // blocked in a runtime error dialog. Lifetime detection must not depend on it.
    internal sealed class WorkerLifetime : IDisposable {
        readonly ManualResetEvent finished = new ManualResetEvent(false);
        readonly Thread thread;
        internal WorkerLifetime(Func<bool> parentAlive, Action requestExit, Action forceExit, int graceMilliseconds = 10000) {
            thread = new Thread(delegate() {
                while (!finished.WaitOne(200)) {
                    bool alive; try { alive = parentAlive(); } catch (InvalidOperationException) { alive = false; }
                    if (alive) continue;
                    try { requestExit(); } catch (InvalidOperationException) { }
                    if (!finished.WaitOne(graceMilliseconds)) forceExit();
                    return;
                }
            }) { IsBackground = true, Name = "BetterDownload parent lifetime" };
            thread.Start();
        }
        public void Dispose() { finished.Set(); thread.Join(); finished.Dispose(); }
    }
}
