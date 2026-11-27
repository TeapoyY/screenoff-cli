using System;
using System.Collections.Generic;
using ScreenOffHelper;

internal static class SessionTests
{
    private sealed class FakePower : IPowerPlatform
    {
        internal readonly List<string> Calls = new List<string>();
        internal readonly HashSet<string> Failures = new HashSet<string>();
        internal Action KeyboardWake;

        private void Call(string operation)
        {
            Calls.Add(operation);
            if (Failures.Contains(operation)) throw new InvalidOperationException(operation + " failed");
        }

        public void AcquireKeepAwake() { Call("acquire"); }
        public void ReleaseKeepAwake() { Call("release"); }
        public void InstallWakeInput(Action wake) { Call("hook"); KeyboardWake = wake; }
        public void RemoveWakeInput() { Call("unhook"); KeyboardWake = null; }
        public void SetDisplayPower(bool on) { Call(on ? "on" : "off"); }
    }

    private static int failures;
    private static int passes;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Calls(FakePower power, params string[] expected)
    {
        string actual = String.Join(",", power.Calls.ToArray());
        string desired = String.Join(",", expected);
        Assert(actual == desired, "Expected calls [" + desired + "], got [" + actual + "]");
    }

    private static void Throws(Action action, string expectedMessage)
    {
        try { action(); }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message == expectedMessage, "Unexpected error: " + exception.Message);
            return;
        }
        throw new Exception("Expected failure: " + expectedMessage);
    }

    private static void ReachOff(ScreenSession session)
    {
        session.BeginCountdown();
        session.TickCountdown();
        session.TickCountdown();
        session.TickCountdown();
    }

    private static void Run(string name, Action test)
    {
        try { test(); passes++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }

    private static void CancelCountdown()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            session.BeginCountdown();
            session.TickCountdown();
            session.Wake();
            session.TickCountdown();
            Assert(session.State == SessionState.Ready && session.SecondsRemaining == 0, "Cancel must return to Ready");
            Calls(power);
        }
    }

    private static void CountdownAndOrder()
    {
        FakePower power = new FakePower();
        ScreenSession session = new ScreenSession(power);
        session.BeginCountdown();
        Assert(session.SecondsRemaining == 3, "Countdown starts at three");
        session.TickCountdown();
        session.TickCountdown();
        Assert(session.State == SessionState.Countdown && session.SecondsRemaining == 1, "Must wait all three ticks");
        Calls(power);
        session.TickCountdown();
        Assert(session.State == SessionState.ScreenOff && session.SecondsRemaining == 0, "Third tick must enter off state");
        Calls(power, "acquire", "hook", "off");
        session.Dispose();
        Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
    }

    private static void KeyboardWakeExactlyOnce()
    {
        FakePower power = new FakePower();
        ScreenSession session = new ScreenSession(power);
        ReachOff(session);
        Action queuedKeyboardCallback = power.KeyboardWake;
        Assert(queuedKeyboardCallback != null, "Keyboard hook must be installed");
        queuedKeyboardCallback();
        queuedKeyboardCallback();
        session.Wake();
        session.Dispose();
        session.Dispose();
        Assert(session.State == SessionState.Ready, "Keyboard callback must return to Ready");
        Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
    }

    private static void RepeatedBeginAndTick()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            session.BeginCountdown();
            session.TickCountdown();
            session.BeginCountdown();
            Assert(session.SecondsRemaining == 2, "Repeat begin must not reset countdown");
            session.TickCountdown();
            session.TickCountdown();
            session.BeginCountdown();
            session.TickCountdown();
            session.TickCountdown();
            Calls(power, "acquire", "hook", "off");
        }
    }

    private static void AcquireFailure()
    {
        FakePower power = new FakePower();
        power.Failures.Add("acquire");
        using (ScreenSession session = new ScreenSession(power))
        {
            Throws(delegate { ReachOff(session); }, "acquire failed");
            Assert(session.State == SessionState.Ready, "Acquire error must return to Ready");
            Calls(power, "acquire");
        }
    }

    private static void HookFailure()
    {
        FakePower power = new FakePower();
        power.Failures.Add("hook");
        using (ScreenSession session = new ScreenSession(power))
        {
            Throws(delegate { ReachOff(session); }, "hook failed");
            Assert(session.State == SessionState.Ready, "Hook error must return to Ready");
            Calls(power, "acquire", "hook", "release");
        }
    }

    private static void OffFailure()
    {
        FakePower power = new FakePower();
        power.Failures.Add("off");
        using (ScreenSession session = new ScreenSession(power))
        {
            Throws(delegate { ReachOff(session); }, "off failed");
            Assert(session.State == SessionState.Ready, "Off error must return to Ready");
            Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
        }
    }

    private static void OffFailurePreservesOriginal()
    {
        FakePower power = new FakePower();
        power.Failures.Add("off");
        power.Failures.Add("on");
        using (ScreenSession session = new ScreenSession(power))
        {
            Throws(delegate { ReachOff(session); }, "off failed");
            Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
        }
    }

    private static void WakeFailureStillReleases()
    {
        FakePower power = new FakePower();
        ScreenSession session = new ScreenSession(power);
        ReachOff(session);
        power.Failures.Add("on");
        Throws(session.Wake, "on failed");
        session.Dispose();
        Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
    }

    private static void UnhookFailureStillReleases()
    {
        FakePower power = new FakePower();
        ScreenSession session = new ScreenSession(power);
        ReachOff(session);
        power.Failures.Add("unhook");
        Throws(session.Wake, "unhook failed");
        session.Dispose();
        Calls(power, "acquire", "hook", "off", "on", "unhook", "release");
    }

    private static void ExitDuringCountdown()
    {
        FakePower power = new FakePower();
        ScreenSession session = new ScreenSession(power);
        session.BeginCountdown();
        session.Dispose();
        session.TickCountdown();
        Calls(power);
        try { session.BeginCountdown(); }
        catch (ObjectDisposedException) { return; }
        throw new Exception("Disposed session accepted another start");
    }

    private static void MultipleIntervals()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            ReachOff(session);
            power.KeyboardWake();
            ReachOff(session);
            power.KeyboardWake();
            Calls(power, "acquire", "hook", "off", "on", "unhook", "release", "acquire", "hook", "off", "on", "unhook", "release");
        }
    }

    private static void CancelFromChangedBeforeOff()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            session.Changed += delegate { if (session.State == SessionState.ScreenOff) session.Wake(); };
            ReachOff(session);
            Assert(session.State == SessionState.Ready, "Reentrant cancellation must remain Ready");
            Calls(power, "acquire", "hook", "unhook", "release");
        }
    }

    private static void PreviousKeyboardCallbackCannotCancelNextCountdown()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            ReachOff(session);
            Action previousKeyboardCallback = power.KeyboardWake;
            session.Wake();
            session.BeginCountdown();
            previousKeyboardCallback();
            Assert(session.State == SessionState.Countdown && session.SecondsRemaining == 3,
                "An already queued callback from a previous interval canceled the next countdown");
        }
    }

    private static void PreviousKeyboardCallbackCannotWakeNextOffInterval()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            ReachOff(session);
            Action previousKeyboardCallback = power.KeyboardWake;
            session.Wake();
            ReachOff(session);
            previousKeyboardCallback();
            Assert(session.State == SessionState.ScreenOff,
                "An already queued callback from a previous interval woke the next off interval");
        }
    }

    private static void CustomCountdown()
    {
        FakePower power = new FakePower();
        using (ScreenSession session = new ScreenSession(power))
        {
            session.BeginCountdown(1);
            Assert(session.SecondsRemaining == 1, "Requested countdown must be used.");
            session.TickCountdown();
            Assert(session.State == SessionState.ScreenOff, "One tick must enter off state.");
            Calls(power, "acquire", "hook", "off");
        }
    }

    public static int Main()
    {
        Run("cancel countdown never changes display or sleep", CancelCountdown);
        Run("three second countdown acquires before off and exit cleans up", CountdownAndOrder);
        Run("custom countdown changes the number of ticks", CustomCountdown);
        Run("keyboard wake and duplicate callbacks clean up once", KeyboardWakeExactlyOnce);
        Run("repeated starts and ticks do not acquire twice", RepeatedBeginAndTick);
        Run("acquire failure does not hook or turn off", AcquireFailure);
        Run("hook failure releases keep-awake", HookFailure);
        Run("off failure restores and cleans up", OffFailure);
        Run("off error survives restore error and still cleans up", OffFailurePreservesOriginal);
        Run("wake error still removes hook and releases", WakeFailureStillReleases);
        Run("unhook error still releases", UnhookFailureStillReleases);
        Run("exit during countdown is safe and prevents restart", ExitDuringCountdown);
        Run("two intervals acquire and release independently", MultipleIntervals);
        Run("Changed handler can cancel before display off", CancelFromChangedBeforeOff);
        Run("old keyboard callback cannot cancel next countdown", PreviousKeyboardCallbackCannotCancelNextCountdown);
        Run("old keyboard callback cannot wake next interval", PreviousKeyboardCallbackCannotWakeNextOffInterval);
        Console.WriteLine("RESULT " + passes + " passed, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
