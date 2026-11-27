using System;

namespace ScreenOffHelper
{
    public enum SessionState { Ready, Countdown, ScreenOff }

    public interface IPowerPlatform
    {
        void AcquireKeepAwake();
        void ReleaseKeepAwake();
        void InstallWakeInput(Action wake);
        void RemoveWakeInput();
        void SetDisplayPower(bool on);
    }

    // All calls, including keyboard notifications, run on the owning UI thread.
    public sealed class ScreenSession : IDisposable
    {
        private readonly IPowerPlatform platform;
        private bool keepAwake;
        private bool keyboardWatch;
        private bool displayOffRequested;
        private bool disposed;
        private int generation;
        public SessionState State { get; private set; }
        public int SecondsRemaining { get; private set; }
        public event Action Changed;

        public ScreenSession(IPowerPlatform platform)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            this.platform = platform;
        }

        public void BeginCountdown(int seconds = 3)
        {
            if (disposed) throw new ObjectDisposedException("ScreenSession");
            if (seconds < 1) throw new ArgumentOutOfRangeException("seconds");
            if (State != SessionState.Ready) return;
            SecondsRemaining = seconds;
            State = SessionState.Countdown;
            NotifyChanged();
        }

        public void TickCountdown()
        {
            if (disposed || State != SessionState.Countdown) return;
            SecondsRemaining--;
            if (SecondsRemaining > 0) { NotifyChanged(); return; }
            State = SessionState.ScreenOff;
            try
            {
                platform.AcquireKeepAwake();
                keepAwake = true;
                int currentGeneration = ++generation;
                platform.InstallWakeInput(delegate
                {
                    if (generation == currentGeneration && State == SessionState.ScreenOff) Wake();
                });
                keyboardWatch = true;
                NotifyChanged();
                if (disposed || State != SessionState.ScreenOff) return;
                displayOffRequested = true;
                platform.SetDisplayPower(false);
            }
            catch
            {
                try { Wake(); } catch { /* Preserve the original failure. */ }
                throw;
            }
        }

        public void Wake()
        {
            if (State == SessionState.Ready) return;
            generation++;
            State = SessionState.Ready;
            SecondsRemaining = 0;
            try
            {
                if (displayOffRequested)
                {
                    displayOffRequested = false;
                    platform.SetDisplayPower(true);
                }
            }
            finally
            {
                try
                {
                    if (keyboardWatch)
                    {
                        keyboardWatch = false;
                        platform.RemoveWakeInput();
                    }
                }
                finally
                {
                    try
                    {
                        if (keepAwake)
                        {
                            keepAwake = false;
                            platform.ReleaseKeepAwake();
                        }
                    }
                    finally { NotifyChanged(); }
                }
            }
        }

        private void NotifyChanged()
        {
            Action changed = Changed;
            if (changed != null) changed();
        }

        public void Dispose()
        {
            if (disposed) return;
            try { Wake(); }
            finally { disposed = true; }
        }
    }
}
