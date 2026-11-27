using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace ScreenOffHelper
{
    // DDC calls can take seconds. Keep them off the UI/hook thread and preserve
    // command order so an immediate wake or exit cannot overtake an Off call.
    internal sealed class HardwarePowerWorker : IDisposable
    {
        private readonly object gate = new object();
        private readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
        private readonly Thread thread;
        private DdcPower backend;
        private bool initialized;
        private bool stopping;

        internal HardwarePowerWorker()
        {
            thread = new Thread(Run);
            thread.Name = "ScreenOff hardware power";
            // Keep the process alive while pending On/Dispose restores displays.
            thread.IsBackground = false;
            thread.Start();
        }

        internal void Initialize(Action<DdcProbeInfo, Exception> done)
        {
            Enqueue(delegate
            {
                initialized = true;
                DdcProbeInfo information = null;
                Exception result = null;
                try { information = GetOrOpenBackend().Information; }
                catch (Exception error) { result = error; }
                Invoke(done, information, result);
            }, delegate(Exception error) { Invoke(done, default(DdcProbeInfo), error); });
        }

        internal void SetPower(bool on, Action<Exception> done)
        {
            Enqueue(delegate
            {
                Exception result = null;
                try
                {
                    if (!initialized)
                        throw new InvalidOperationException("显示器控制尚未初始化。");
                    if (!on)
                    {
                        DdcPower active;
                        try { active = OpenSupportedBackend(); }
                        catch (Exception error) { throw new InvalidOperationException("显示器控制初始化失败。", error); }
                        active.Off();
                    }
                    else if (backend != null)
                    {
                        // Keep the original handles when wake fails, so the next
                        // On can retry. Once verified, reopen on the next Off:
                        // power cycling can invalidate the original handles.
                        backend.On();
                        DdcPower restored = backend;
                        backend = null;
                        try { restored.Dispose(); }
                        catch (Exception cleanupError) { Report(cleanupError); }
                    }
                }
                catch (Exception error) { result = error; }
                Invoke(done, result);
            }, delegate(Exception error) { Invoke(done, error); });
        }

        private DdcPower GetOrOpenBackend()
        {
            if (backend == null) backend = new DdcPower();
            return backend;
        }

        private DdcPower OpenSupportedBackend()
        {
            for (int attempt = 0; ; attempt++)
            {
                DdcPower current = GetOrOpenBackend();
                if (current.Information.AllSupported || attempt == 2) return current;
                // A monitor may already report power on while its DDC capability
                // channel is still reconnecting. Reopen before attempting any write.
                backend = null;
                try { current.Dispose(); }
                catch (Exception cleanupError) { Report(cleanupError); }
                Thread.Sleep(1200);
            }
        }

        private void Enqueue(Action job, Action<Exception> rejected)
        {
            bool accepted;
            lock (gate)
            {
                accepted = !stopping;
                if (accepted) jobs.Add(job);
            }
            if (!accepted)
            {
                // No more queue entries are possible after shutdown. Deliver the
                // rejection asynchronously; callbacks never have to block the UI.
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { rejected(new ObjectDisposedException("HardwarePowerWorker")); }
                    catch (Exception error) { Report(error); }
                });
            }
        }

        private void Run()
        {
            try
            {
                foreach (Action job in jobs.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception error) { Report(error); }
                }
            }
            catch (Exception error) { Report(error); }
            finally
            {
                RestoreAndDispose();
            }
        }

        private void RestoreAndDispose()
        {
            DdcPower current = backend;
            backend = null;
            if (current == null) return;
            try { current.Dispose(); }
            catch (Exception error) { Report(error); }
        }

        internal void Shutdown()
        {
            lock (gate)
            {
                if (!stopping)
                {
                    stopping = true;
                    // The existing Off/On operations drain before final restore.
                    jobs.Add(RestoreAndDispose);
                    jobs.CompleteAdding();
                }
            }
            if (Thread.CurrentThread != thread)
            {
                // This bounds caller waiting. The foreground worker continues a
                // slow hardware restore even if the UI has already closed.
                if (!thread.Join(8000))
                    Report(new TimeoutException("显示器恢复仍在后台完成。"));
            }
        }

        public void Dispose()
        {
            Shutdown();
        }

        private static void Invoke(Action<Exception> callback, Exception error)
        {
            if (callback == null) return;
            try { callback(error); }
            catch (Exception callbackError) { Report(callbackError); }
        }

        private static void Invoke(Action<DdcProbeInfo, Exception> callback, DdcProbeInfo information, Exception error)
        {
            if (callback == null) return;
            try { callback(information, error); }
            catch (Exception callbackError) { Report(callbackError); }
        }

        private static void Report(Exception error)
        {
            try { Trace.WriteLine("HardwarePowerWorker: " + error); }
            catch { }
        }
    }
}
