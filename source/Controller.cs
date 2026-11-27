using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace ScreenOffHelper
{
    internal enum Stage { Starting = 1, Countdown, PoweringOff, Off, Restoring, Stopping }

    internal sealed class Controller : Form
    {
        internal const string WindowTitle = "ScreenOff CLI controller {D305C850-680E-4904-83B7-C19FD5A1D201}";
        internal const int ControlMessage = 0x8000 + 3310;
        private readonly Options options;
        private readonly Timer timer = new Timer();
        private readonly Stopwatch offTime = new Stopwatch();
        private readonly WindowsPower power;
        private readonly ScreenSession session;
        private Stage stage = Stage.Starting;
        private bool wakeQueued;
        private bool failed;
        private bool restoreRetried;
        private int dryTicks;

        internal Controller(Options options)
        {
            this.options = options;
            Text = WindowTitle;
            ShowInTaskbar = false;
            if (!options.DryRun)
            {
                power = new WindowsPower(this);
                session = new ScreenSession(power);
                session.Changed += OnSessionChanged;
                power.PowerOffAccepted += OnPowerOffAccepted;
                power.PowerFinished += OnPowerFinished;
            }
            timer.Tick += Tick;
        }

        internal int Run()
        {
            // The window receives local control messages without being shown.
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the ScreenOff controller.");
            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                BeginInvoke(new Action(StartCountdown));
                Application.Run();
                return failed ? Program.Failed : 0;
            }
            finally { Console.CancelKeyPress -= OnCancelKeyPress; }
        }

        private void StartCountdown()
        {
            if (stage != Stage.Starting) return;
            stage = Stage.Countdown;
            Program.ReportState("countdown " + options.Delay);
            if (session != null) session.BeginCountdown(options.Delay);
            dryTicks = options.Delay;
            timer.Interval = 1000;
            timer.Start();
        }

        private void Tick(object sender, EventArgs args)
        {
            if (stage == Stage.Off)
            {
                if (offTime.Elapsed.TotalSeconds >= options.Duration) RequestWake();
                return;
            }
            if (stage != Stage.Countdown) return;
            if (options.DryRun)
            {
                if (--dryTicks == 0)
                {
                    Program.ReportState("dry-run-complete");
                    Finish();
                }
                return;
            }
            try
            {
                if (session.SecondsRemaining == 1) NativeMethods.RequireInputDesktop();
                session.TickCountdown();
            }
            catch (Exception error)
            {
                ReportFailure(error);
                RequestWake();
            }
        }

        private void OnSessionChanged()
        {
            if (session.State == SessionState.ScreenOff)
            {
                stage = Stage.PoweringOff;
                timer.Stop();
                power.Prepare();
            }
            else if (session.State == SessionState.Ready)
            {
                timer.Stop();
                if (power.IsRestoring) stage = Stage.Restoring;
                else BeginInvoke(new Action(Finish));
            }
        }

        private void OnPowerOffAccepted()
        {
            stage = Stage.Off;
            if (options.Duration == 0) return;
            offTime.Restart();
            timer.Interval = 250;
            timer.Start();
        }

        private void OnPowerFinished(Exception error)
        {
            if (session.State == SessionState.ScreenOff)
            {
                ReportFailure(error);
                RequestWake();
                return;
            }
            if (error != null && !restoreRetried)
            {
                restoreRetried = true;
                Program.Write(Console.Error, "Retrying monitor restoration: " + error.Message);
                power.RestoreIfNeeded();
                return;
            }
            if (error != null) ReportFailure(error);
            Finish();
        }

        private void ReportFailure(Exception error)
        {
            failed = true;
            Program.Write(Console.Error, error.Message);
            Program.ReportState("hardware-operation-failed");
        }

        private void RequestWake()
        {
            if (stage == Stage.Stopping || stage == Stage.Restoring) return;
            if (stage == Stage.Countdown || stage == Stage.Starting) Program.ReportState("cancelled");
            if (session == null || session.State == SessionState.Ready)
            {
                if (power == null || !power.IsRestoring) Finish();
                return;
            }
            try { session.Wake(); }
            catch (Exception error)
            {
                ReportFailure(error);
                if (!power.IsRestoring) Finish();
            }
        }

        private void Finish()
        {
            if (stage == Stage.Stopping) return;
            stage = Stage.Stopping;
            timer.Stop();
            Application.ExitThread();
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            try { BeginInvoke(new Action(RequestWake)); }
            catch (InvalidOperationException) { /* Shutdown has already started. */ }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ControlMessage)
            {
                message.Result = new IntPtr((int)stage);
                if (message.WParam == new IntPtr(1) && !wakeQueued)
                {
                    wakeQueued = true;
                    BeginInvoke(new Action(RequestWake));
                }
                return;
            }
            if (message.Msg == 0x007e && power != null && (stage == Stage.Off || stage == Stage.PoweringOff || stage == Stage.Restoring))
                BeginInvoke(new Action(power.RefreshBlackout));
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                try { if (session != null) session.Dispose(); }
                finally { if (power != null) power.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
