using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ScreenOffHelper
{
    internal static class NativeMethods
    {
        internal delegate IntPtr InputHook(int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);

        internal static string DesktopIdentity()
        {
            return DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        }

        internal static void RequireInputDesktop()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
            if (desktop == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the active input desktop.");
            try
            {
                if (!String.Equals(DesktopIdentity(), DesktopName(desktop), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Run this command on the active, unlocked desktop.");
            }
            finally { CloseDesktop(desktop); }
        }

        private static string DesktopName(IntPtr desktop)
        {
            StringBuilder name = new StringBuilder(256);
            uint needed;
            if (!GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * 2), out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确定当前桌面。");
            return name.ToString();
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int kind, InputHook callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }

    internal sealed class WindowsPower : IPowerPlatform, IDisposable
    {
        private readonly Control owner;
        private readonly uint ownerThread;
        private readonly NativeMethods.InputHook keyboardCallback;
        private readonly NativeMethods.InputHook mouseCallback;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private Action wakeAction;
        private WakeInput wakeInput;
        private bool wakeQueued;
        private Blackout blackout;
        private readonly HardwarePowerWorker hardware = new HardwarePowerWorker();
        private int powerGeneration;
        private bool needsRestore;

        internal bool IsRestoring { get; private set; }
        internal event Action<Exception> PowerFinished;
        internal event Action PowerOffAccepted;

        internal WindowsPower(Control owner)
        {
            this.owner = owner;
            ownerThread = NativeMethods.GetCurrentThreadId();
            keyboardCallback = OnKeyboard;
            mouseCallback = OnMouse;
        }

        private void CheckThread()
        {
            if (NativeMethods.GetCurrentThreadId() != ownerThread)
                throw new InvalidOperationException("Power requests must stay on the owning UI thread.");
        }

        public void AcquireKeepAwake()
        {
            CheckThread();
            if (NativeMethods.SetThreadExecutionState(0x80000001u) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用临时防休眠。");
        }

        public void ReleaseKeepAwake()
        {
            CheckThread();
            if (NativeMethods.SetThreadExecutionState(0x80000000u) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法释放临时防休眠请求。");
        }

        public void InstallWakeInput(Action wake)
        {
            CheckThread();
            if (keyboardHook != IntPtr.Zero || mouseHook != IntPtr.Zero)
                throw new InvalidOperationException("唤醒监听已经启用。");
            wakeAction = wake;
            wakeInput = new WakeInput(NativeMethods.GetAsyncKeyState(0x20) < 0);
            wakeQueued = false;
            try
            {
                IntPtr module = NativeMethods.GetModuleHandle(null);
                keyboardHook = NativeMethods.SetWindowsHookEx(13, keyboardCallback, module, 0);
                if (keyboardHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听空格键。");
                mouseHook = NativeMethods.SetWindowsHookEx(14, mouseCallback, module, 0);
                if (mouseHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听鼠标按键。");
            }
            catch
            {
                RemoveWakeInput();
                throw;
            }
        }

        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && wakeAction != null)
            {
                int kind = message.ToInt32();
                int key = Marshal.ReadInt32(data); // KBDLLHOOKSTRUCT.vkCode, never stored.
                if (wakeInput.Keyboard(kind, key)) QueueWake();
                if (key == 0x20 || kind == 0x0100 || kind == 0x0104) return new IntPtr(1);
            }
            return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
        }

        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && wakeAction != null)
            {
                int kind = message.ToInt32();
                if (wakeInput.Mouse(kind)) QueueWake();
                if (kind != 0x0200) return new IntPtr(1); // Movement may reach Windows, but never restores the image.
            }
            return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
        }

        private void QueueWake()
        {
            if (wakeQueued) return;
            wakeQueued = true;
            Action capturedWake = wakeAction;
            try
            {
                owner.BeginInvoke(new Action(delegate
                {
                    if (Object.ReferenceEquals(wakeAction, capturedWake)) capturedWake();
                }));
            }
            catch (InvalidOperationException) { /* Owner is closing. */ }
        }

        public void RemoveWakeInput()
        {
            CheckThread();
            wakeAction = null;
            wakeInput = null;
            if (keyboardHook != IntPtr.Zero && NativeMethods.UnhookWindowsHookEx(keyboardHook)) keyboardHook = IntPtr.Zero;
            if (mouseHook != IntPtr.Zero && NativeMethods.UnhookWindowsHookEx(mouseHook)) mouseHook = IntPtr.Zero;
        }

        public void SetDisplayPower(bool on)
        {
            CheckThread();
            if (on)
            {
                RestoreIfNeeded();
                return;
            }
            blackout = new Blackout();
            blackout.Refresh();
            needsRestore = true;
            int generation = ++powerGeneration;
            Program.ReportState("sending-hardware-off");
            hardware.SetPower(false, delegate(Exception error)
            {
                PostToOwner(delegate
                {
                    if (generation != powerGeneration) return;
                    if (error == null)
                    {
                        Program.ReportState("hardware-off-command-accepted");
                        Action accepted = PowerOffAccepted;
                        if (accepted != null) accepted();
                    }
                    else NotifyPowerFinished(error);
                });
            });
        }

        internal void Prepare()
        {
            hardware.Initialize(null);
        }

        internal void RestoreIfNeeded()
        {
            if (IsRestoring) return;
            if (!needsRestore)
            {
                CloseBlackout();
                return;
            }
            ++powerGeneration;
            IsRestoring = true;
            Program.ReportState("restoring");
            NativeMethods.DefWindowProc(owner.Handle, 0x0112, new UIntPtr(0xf170), new IntPtr(-1));
            hardware.SetPower(true, delegate(Exception error)
            {
                PostToOwner(delegate
                {
                    IsRestoring = false;
                    needsRestore = error != null;
                    CloseBlackout();
                    Program.ReportState(error == null ? "hardware-on-confirmed" : "restore-failed");
                    NotifyPowerFinished(error);
                });
            });
        }

        internal void RefreshBlackout()
        {
            if (blackout != null) blackout.Refresh();
        }

        private void CloseBlackout()
        {
            if (blackout != null) blackout.Dispose();
            blackout = null;
        }

        private void NotifyPowerFinished(Exception error)
        {
            Action<Exception> finished = PowerFinished;
            if (finished != null) finished(error);
        }

        private void PostToOwner(Action action)
        {
            try { if (!owner.IsDisposed && owner.IsHandleCreated) owner.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            try { hardware.Dispose(); }
            finally
            {
                CloseBlackout();
                RemoveWakeInput();
            }
        }
    }
}
