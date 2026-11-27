using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("ScreenOff CLI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace ScreenOffHelper
{
    internal static class Program
    {
        internal const int Busy = 3;
        internal const int Failed = 4;
        private const string LegacyMutex = "Local\\ScreenOffHelper_1_1_";
        private const string CliMutex = "Local\\ScreenOffCli_";

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Options options;
            try { options = Options.Parse(args); }
            catch (ArgumentException error)
            {
                Write(Console.Error, error.Message);
                Write(Console.Error, "Run screenoff help for usage.");
                return 2;
            }

            if (options.Command == "help")
            {
                Write(Console.Out, "ScreenOff CLI 1.0.0\n" +
                    "  screenoff off [--delay 1..60] [--for 1..86400] [--dry-run]\n" +
                    "  screenoff wake\n  screenoff status\n  screenoff probe\n" +
                    "Delay and duration are in seconds. The default countdown is 3 seconds.");
                return 0;
            }

            try
            {
                if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
                    throw new InvalidOperationException("Run screenoff in an interactive Windows user session.");
                if (options.Command == "status") return Status();
                if (options.Command == "wake") return Wake();

                NativeMethods.RequireInputDesktop();
                string desktop = NativeMethods.DesktopIdentity();
                if (options.Command == "probe")
                {
                    return Exclusive(LegacyMutex + desktop, delegate
                    {
                        DdcProbeInfo information = DdcPower.Probe();
                        Write(Console.Out, information.ToString());
                        return information.AllSupported ? 0 : Failed;
                    });
                }
                return Exclusive(CliMutex + desktop, delegate
                {
                    if (options.DryRun) return RunController(options);
                    return Exclusive(LegacyMutex + desktop, delegate { return RunController(options); });
                });
            }
            catch (Exception error)
            {
                Write(Console.Error, error.GetBaseException().Message);
                return Failed;
            }
        }

        private static int Exclusive(string name, Func<int> action)
        {
            using (Mutex guard = new Mutex(false, name))
            {
                bool acquired;
                try { acquired = guard.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    Write(Console.Error, "Another ScreenOff session owns this desktop. Exit the legacy tray app if it is open.");
                    return Busy;
                }
                try { return action(); }
                finally { guard.ReleaseMutex(); }
            }
        }

        private static int RunController(Options options)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using (Controller controller = new Controller(options))
                return controller.Run();
        }

        private static int Status()
        {
            IntPtr window = NativeMethods.FindWindow(null, Controller.WindowTitle);
            if (window == IntPtr.Zero)
            {
                Write(Console.Out, ControllerLockHeld() ? "busy" : "idle");
                return 0;
            }
            IntPtr state;
            if (!Send(window, false, out state)) return Unresponsive();
            Write(Console.Out, ((Stage)state.ToInt32()).ToString().ToLowerInvariant());
            return 0;
        }

        private static int Wake()
        {
            IntPtr window = NativeMethods.FindWindow(null, Controller.WindowTitle);
            Stopwatch wait = Stopwatch.StartNew();
            while (window == IntPtr.Zero && ControllerLockHeld())
            {
                if (wait.ElapsedMilliseconds >= 1500) return Unresponsive();
                Thread.Sleep(25);
                window = NativeMethods.FindWindow(null, Controller.WindowTitle);
            }
            if (window == IntPtr.Zero)
            {
                Write(Console.Out, "idle");
                return 0;
            }
            uint pid;
            if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0) return Unresponsive();
            using (Process process = Process.GetProcessById((int)pid))
            {
                // Hold the process handle before sending. Window disappearance alone
                // cannot distinguish a confirmed restore from a process failure.
                if (process.Handle == IntPtr.Zero) return Unresponsive();
                uint currentPid;
                if (NativeMethods.GetWindowThreadProcessId(window, out currentPid) == 0 || pid != currentPid)
                    return Unresponsive();
                IntPtr state;
                if (!Send(window, true, out state)) return Unresponsive();
                if (!process.WaitForExit(45000))
                {
                    Write(Console.Error, "Wake was queued. Restoration did not finish within 45 seconds.");
                    return Busy;
                }
                if (process.ExitCode != 0)
                {
                    Write(Console.Error, "ScreenOff ended with error " + process.ExitCode + ". Check the off command's output.");
                    return Failed;
                }
                Write(Console.Out, "idle");
                return 0;
            }
        }

        private static bool ControllerLockHeld()
        {
            Mutex guard;
            if (!Mutex.TryOpenExisting(CliMutex + NativeMethods.DesktopIdentity(), out guard)) return false;
            using (guard)
            {
                try { if (!guard.WaitOne(0)) return true; }
                catch (AbandonedMutexException) { /* This thread now owns the abandoned mutex. */ }
                guard.ReleaseMutex();
                return false;
            }
        }

        private static bool Send(IntPtr window, bool wake, out IntPtr state)
        {
            const uint abortIfHung = 0x0002;
            const uint errorOnExit = 0x0020;
            return NativeMethods.SendMessageTimeout(window, Controller.ControlMessage,
                wake ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero, abortIfHung | errorOnExit, 1200, out state) != IntPtr.Zero;
        }

        private static int Unresponsive()
        {
            Write(Console.Error, "The ScreenOff session did not respond. Check status and retry.");
            return Busy;
        }

        internal static void ReportState(string state)
        {
            Write(Console.Out, state);
            string path = Environment.GetEnvironmentVariable("SCREENOFF_STATUS_PATH");
            if (String.IsNullOrWhiteSpace(path)) return;
            try { File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + state + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (System.Security.SecurityException) { }
        }

        internal static void Write(TextWriter output, string text)
        {
            try { output.WriteLine(text); }
            catch (IOException) { /* A closed output pipe must not interrupt a restore. */ }
        }
    }

    internal sealed class Options
    {
        internal string Command { get; private set; }
        internal int Delay { get; private set; }
        internal int Duration { get; private set; }
        internal bool DryRun { get; private set; }

        internal static Options Parse(string[] args)
        {
            string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
            if (command == "--off") command = "off";
            if (command == "--wake") command = "wake";
            if (command == "--help" || command == "-h") command = "help";
            Options result = new Options { Command = command, Delay = 3 };
            if (command == "help" || command == "wake" || command == "status" || command == "probe")
            {
                if (args.Length > 1) throw new ArgumentException(command + " takes no options.");
                return result;
            }
            if (command != "off") throw new ArgumentException("Unknown command: " + command);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index++)
            {
                string option = args[index];
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                switch (option)
                {
                    case "--dry-run": result.DryRun = true; break;
                    case "--delay": result.Delay = Seconds(args, ref index, 60); break;
                    case "--for": result.Duration = Seconds(args, ref index, 86400); break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
            }
            if (result.DryRun && result.Duration != 0) throw new ArgumentException("--for requires real screen-off.");
            return result;
        }

        private static int Seconds(string[] args, ref int index, int maximum)
        {
            string option = args[index++];
            int value;
            if (index == args.Length || !Int32.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out value)
                || value < 1 || value > maximum)
                throw new ArgumentException(option + " expects whole seconds from 1 to " + maximum + ".");
            return value;
        }
    }
}
