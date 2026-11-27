using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenOffHelper
{
    // Wake on release after a new press, so the wake gesture cannot click or type through.
    internal sealed class WakeInput
    {
        private bool spacePressed;
        private bool spaceHeldAtStart;
        private bool leftPressed;
        private bool rightPressed;

        internal WakeInput(bool spaceHeldAtStart)
        {
            this.spaceHeldAtStart = spaceHeldAtStart;
        }

        internal bool Keyboard(int message, int key)
        {
            if (key != 0x20) return false;
            if (spaceHeldAtStart)
            {
                if (message == 0x0101 || message == 0x0105) spaceHeldAtStart = false;
                return false;
            }
            if (message == 0x0100 || message == 0x0104) spacePressed = true;
            if (message == 0x0101 || message == 0x0105) return Release(ref spacePressed);
            return false;
        }

        internal bool Mouse(int message)
        {
            if (message == 0x0201) leftPressed = true;
            if (message == 0x0204) rightPressed = true;
            if (message == 0x0202) return Release(ref leftPressed);
            if (message == 0x0205) return Release(ref rightPressed);
            return false;
        }

        private static bool Release(ref bool pressed)
        {
            bool wasPressed = pressed;
            pressed = false;
            return wasPressed;
        }
    }

    internal sealed class Blackout : IDisposable
    {
        private readonly Dictionary<string, BlackWindow> windows = new Dictionary<string, BlackWindow>();
        private bool cursorHidden;

        internal void Refresh()
        {
            Screen[] screens = Screen.AllScreens;
            HashSet<string> current = new HashSet<string>();
            foreach (Screen screen in screens)
            {
                current.Add(screen.DeviceName);
                BlackWindow window;
                if (!windows.TryGetValue(screen.DeviceName, out window))
                {
                    window = new BlackWindow();
                    windows.Add(screen.DeviceName, window);
                }
                window.Bounds = screen.Bounds;
                if (!window.Visible) window.Show();
                window.Update();
            }
            foreach (string name in new List<string>(windows.Keys))
            {
                if (current.Contains(name)) continue;
                windows[name].Dispose();
                windows.Remove(name);
            }
            if (!cursorHidden && windows.Count > 0)
            {
                Cursor.Hide();
                cursorHidden = true;
            }
        }

        public void Dispose()
        {
            try { foreach (BlackWindow window in windows.Values) window.Dispose(); }
            finally
            {
                windows.Clear();
                if (cursorHidden) Cursor.Show();
                cursorHidden = false;
            }
        }

        private sealed class BlackWindow : Form
        {
            [DllImport("user32.dll")]
            private static extern IntPtr SetCursor(IntPtr cursor);

            internal BlackWindow()
            {
                Text = "息屏助手 · 黑屏";
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    return parameters;
                }
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0020) // WM_SETCURSOR
                {
                    SetCursor(IntPtr.Zero);
                    message.Result = new IntPtr(1);
                    return;
                }
                if (message.Msg == 0x0010) return; // Only the owning session disposes this window.
                base.WndProc(ref message);
            }
        }
    }
}
