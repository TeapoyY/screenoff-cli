using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ScreenOffHelper
{
    public sealed class DdcMonitorInfo
    {
        public string DeviceName { get; private set; }
        public string Description { get; private set; }
        public string Capabilities { get; private set; }
        public uint? CurrentPowerMode { get; private set; }
        public bool SupportsPowerOff { get; private set; }
        public string Error { get; private set; }
        internal DdcMonitorInfo(string device, string description, string caps, uint? current, bool supported, string error)
        {
            DeviceName = device; Description = description; Capabilities = caps;
            CurrentPowerMode = current; SupportsPowerOff = supported; Error = error;
        }
        public override string ToString()
        {
            return DeviceName + " " + Description + "；D6=" + (CurrentPowerMode.HasValue ? CurrentPowerMode.Value.ToString(CultureInfo.InvariantCulture) : "未知")
                + "；硬件息屏=" + (SupportsPowerOff ? "支持" : "不支持") + (String.IsNullOrEmpty(Error) ? "" : "；" + Error);
        }
    }

    public sealed class DdcProbeInfo
    {
        public ReadOnlyCollection<DdcMonitorInfo> Monitors { get; private set; }
        public ReadOnlyCollection<string> Errors { get; private set; }
        public bool AllSupported
        {
            get
            {
                if (Monitors.Count == 0 || Errors.Count != 0) return false;
                foreach (DdcMonitorInfo monitor in Monitors) if (!monitor.SupportsPowerOff) return false;
                return true;
            }
        }
        internal DdcProbeInfo(List<DdcMonitorInfo> monitors, List<string> errors)
        {
            Monitors = new ReadOnlyCollection<DdcMonitorInfo>(monitors);
            Errors = new ReadOnlyCollection<string>(errors);
        }
        public override string ToString()
        {
            List<string> lines = new List<string>();
            foreach (DdcMonitorInfo monitor in Monitors) lines.Add(monitor.ToString());
            lines.AddRange(Errors);
            return String.Join(Environment.NewLine, lines.ToArray());
        }
    }

    public sealed class DdcPowerReading
    {
        public string DeviceName { get; private set; }
        public string Description { get; private set; }
        public uint? PowerMode { get; private set; }
        public string Error { get; private set; }
        internal DdcPowerReading(string device, string description, uint? mode, string error)
        {
            DeviceName = device; Description = description; PowerMode = mode; Error = error;
        }
        public override string ToString()
        {
            return DeviceName + " " + Description + "；D6=" + (PowerMode.HasValue ? PowerMode.Value.ToString(CultureInfo.InvariantCulture) : "未知")
                + (String.IsNullOrEmpty(Error) ? "" : "；" + Error);
        }
    }

    // All operations are synchronous and serialized. Construct/use/dispose on a worker:
    // a capabilities request can take seconds. This class has no UI-thread dependency.
    // It changes only the explicitly advertised D6 power values 05 (off) and 01 (on).
    public sealed class DdcPower : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            internal uint Size;
            internal RECT Monitor;
            internal RECT Work;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            internal IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        }
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] physical);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, [Out] StringBuilder capabilities, uint length);

        private sealed class Entry
        {
            internal IntPtr Handle;
            // Dxva2 physical-monitor handles are opaque; NVIDIA can validly return zero.
            internal bool HandleOwned;
            internal int PhysicalIndex;
            internal DdcMonitorInfo Info;
            // Set before writing, because a failed reply can still mean the monitor powered off.
            internal bool PossiblyOff;
        }

        private sealed class FreshHandle
        {
            internal IntPtr Handle;
            internal Entry Target;
        }

        private readonly object sync = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<DdcMonitorInfo> monitorInfos = new List<DdcMonitorInfo>();
        private readonly List<string> scanErrors = new List<string>();
        private readonly HashSet<IntPtr> ownedHandles = new HashSet<IntPtr>();
        private readonly uint ownerThread = GetCurrentThreadId();
        private bool workerKeepAwake;
        private bool disposed;
        public DdcProbeInfo Information { get; private set; }
        public ReadOnlyCollection<string> LastOperationErrors { get; private set; }

        public DdcPower()
        {
            LastOperationErrors = new ReadOnlyCollection<string>(new List<string>());
            try
            {
                bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data)
                {
                    try { OpenMonitor(monitor); }
                    catch (Exception error) { scanErrors.Add("读取显示器信息失败：" + error.Message); }
                    return true;
                }, IntPtr.Zero);
                if (!enumerated) scanErrors.Add(NativeFailure("枚举显示器失败", Marshal.GetLastWin32Error()));
                if (monitorInfos.Count == 0) scanErrors.Add("未找到可读取的物理显示器。");
                Information = new DdcProbeInfo(monitorInfos, scanErrors);
            }
            catch
            {
                DestroyOwnedHandles();
                throw;
            }
        }

        public static DdcProbeInfo Probe()
        {
            using (DdcPower power = new DdcPower()) return power.Information;
        }

        private static string NativeFailure(string action, int error)
        {
            return action + "（错误 " + error.ToString(CultureInfo.InvariantCulture) + "：" + new Win32Exception(error).Message + "）";
        }

        private void OpenMonitor(IntPtr monitor)
        {
            MONITORINFOEX logical = new MONITORINFOEX();
            logical.Size = (uint)Marshal.SizeOf(typeof(MONITORINFOEX));
            string device = GetMonitorInfo(monitor, ref logical) ? logical.Device : "显示器 " + (monitorInfos.Count + 1);
            uint count;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count))
            {
                monitorInfos.Add(new DdcMonitorInfo(device, "物理显示器", "", null, false, NativeFailure("读取物理显示器失败", Marshal.GetLastWin32Error())));
                return;
            }
            if (count == 0 || count > 64)
            {
                monitorInfos.Add(new DdcMonitorInfo(device, "物理显示器", "", null, false, "该输出没有可用的物理显示器句柄。"));
                return;
            }
            PHYSICAL_MONITOR[] physical = new PHYSICAL_MONITOR[count];
            bool opened = GetPhysicalMonitorsFromHMONITOR(monitor, count, physical);
            int openError = opened ? 0 : Marshal.GetLastWin32Error();
            if (!opened)
            {
                monitorInfos.Add(new DdcMonitorInfo(device, "物理显示器", "", null, false, NativeFailure("打开物理显示器失败", openError)));
                return;
            }
            int found = 0;
            for (int physicalIndex = 0; physicalIndex < physical.Length; physicalIndex++)
            {
                PHYSICAL_MONITOR item = physical[physicalIndex];
                if (!ownedHandles.Add(item.Handle)) continue;
                found++;
                Entry entry = new Entry();
                entry.Handle = item.Handle;
                entry.HandleOwned = true;
                entry.PhysicalIndex = physicalIndex;
                entries.Add(entry); // Own the handle before any subsequent read can throw.
                string description = String.IsNullOrWhiteSpace(item.Description) ? "物理显示器" : item.Description;
                try { entry.Info = InspectMonitor(device, description, item.Handle); }
                catch (Exception error) { entry.Info = new DdcMonitorInfo(device, description, "", null, false, "检查显示器失败：" + error.Message); }
                monitorInfos.Add(entry.Info);
            }
            if (found == 0)
                monitorInfos.Add(new DdcMonitorInfo(device, "物理显示器", "", null, false, opened ? "未取得物理显示器句柄。" : NativeFailure("打开物理显示器失败", openError)));
        }

        private static DdcMonitorInfo InspectMonitor(string device, string description, IntPtr handle)
        {
            List<string> errors = new List<string>();
            uint type, current, maximum;
            uint? mode = null;
            if (GetVCPFeatureAndVCPFeatureReply(handle, 0xD6, out type, out current, out maximum)) mode = current;
            else errors.Add(NativeFailure("读取 D6 电源状态失败", Marshal.GetLastWin32Error()));
            string caps = "";
            uint length;
            if (!GetCapabilitiesStringLength(handle, out length)) errors.Add(NativeFailure("读取 DDC/CI 能力长度失败", Marshal.GetLastWin32Error()));
            else if (length == 0 || length > 65536) errors.Add("显示器返回了无效的 DDC/CI 能力长度。");
            else
            {
                StringBuilder buffer = new StringBuilder((int)length);
                if (CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length)) caps = buffer.ToString();
                else errors.Add(NativeFailure("读取 DDC/CI 能力失败", Marshal.GetLastWin32Error()));
            }
            bool advertised = AdvertisesOnAndOff(caps);
            if (!advertised) errors.Add("显示器未明确声明支持 D6(01 05)，不会尝试硬件息屏。");
            Match model = Regex.Match(caps, @"\bmodel\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (model.Success && !String.IsNullOrWhiteSpace(model.Groups[1].Value)) description = model.Groups[1].Value.Trim();
            return new DdcMonitorInfo(device, description, caps, mode, advertised && mode.HasValue && errors.Count == 0, String.Join("；", errors.ToArray()));
        }

        internal static bool AdvertisesOnAndOff(string capabilities)
        {
            if (String.IsNullOrEmpty(capabilities)) return false;
            Match power = Regex.Match(capabilities, @"(?<![0-9A-F])D6\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!power.Success) return false;
            bool on = false, off = false;
            foreach (string token in Regex.Split(power.Groups[1].Value.Trim(), @"\s+"))
            {
                uint value;
                if (!UInt32.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
                if (value == 1) on = true;
                if (value == 5) off = true;
            }
            return on && off;
        }

        public ReadOnlyCollection<DdcPowerReading> ReadPowerModes()
        {
            lock (sync)
            {
                CheckDisposed();
                List<DdcPowerReading> readings = new List<DdcPowerReading>();
                List<string> errors = new List<string>();
                List<FreshHandle> fresh = OpenFreshTargets(entries, errors);
                try
                {
                    foreach (Entry entry in entries)
                    {
                        FreshHandle handle = FindFresh(fresh, entry);
                        uint type, mode, maximum;
                        if (handle == null)
                            readings.Add(new DdcPowerReading(entry.Info.DeviceName, entry.Info.Description, null, "暂时没有该显示器的新物理句柄。" + String.Join("；", errors.ToArray())));
                        else if (GetVCPFeatureAndVCPFeatureReply(handle.Handle, 0xD6, out type, out mode, out maximum))
                            readings.Add(new DdcPowerReading(entry.Info.DeviceName, entry.Info.Description, mode, ""));
                        else readings.Add(new DdcPowerReading(entry.Info.DeviceName, entry.Info.Description, null, NativeFailure("读取 D6 电源状态失败", Marshal.GetLastWin32Error())));
                    }
                }
                finally { CloseFreshHandles(fresh); }
                return new ReadOnlyCollection<DdcPowerReading>(readings);
            }
        }

        public void Off()
        {
            lock (sync)
            {
                CheckDisposed();
                CheckOwnerThread();
                foreach (Entry entry in entries) if (entry.PossiblyOff) throw new InvalidOperationException("上一次硬件息屏尚未恢复，请先调用 On()。");
                if (!Information.AllSupported) throw new InvalidOperationException("无法对全部显示器执行硬件息屏：" + Environment.NewLine + Information);
                List<string> errors = new List<string>();
                if (!workerKeepAwake)
                {
                    if (SetThreadExecutionState(0x80000001u) == 0)
                        throw new InvalidOperationException("无法在硬件息屏工作线程上启用临时防休眠。");
                    workerKeepAwake = true;
                }
                foreach (Entry entry in entries)
                {
                    entry.PossiblyOff = true;
                    if (!SetVCPFeature(entry.Handle, 0xD6, 5))
                    {
                        errors.Add(entry.Info.DeviceName + " " + NativeFailure("硬件息屏失败", Marshal.GetLastWin32Error()));
                        errors.AddRange(RestorePending());
                        LastOperationErrors = new ReadOnlyCollection<string>(errors);
                        throw new InvalidOperationException("硬件息屏未完成，已尝试恢复所有受影响的显示器：" + String.Join("；", errors.ToArray()));
                    }
                }
                LastOperationErrors = new ReadOnlyCollection<string>(errors);
            }
        }

        public void On()
        {
            lock (sync)
            {
                CheckDisposed();
                CheckOwnerThread();
                List<string> errors = RestorePending();
                LastOperationErrors = new ReadOnlyCollection<string>(errors);
                if (errors.Count != 0) throw new InvalidOperationException("部分显示器未能恢复亮屏：" + String.Join("；", errors.ToArray()));
            }
        }

        private List<string> RestorePending()
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            const int budget = 8000;
            Dictionary<Entry, string> problems = new Dictionary<Entry, string>();
            foreach (Entry entry in entries)
            {
                if (!entry.PossiblyOff) continue;
                // Old handles may still accept the wake write, but only a NEW handle can
                // reliably confirm restoration after a monitor hotplug/PDO transition.
                if (!SetVCPFeature(entry.Handle, 0xD6, 1))
                    problems[entry] = NativeFailure("首次亮屏命令未获确认", Marshal.GetLastWin32Error());
            }
            while (true)
            {
                List<Entry> pending = new List<Entry>();
                foreach (Entry entry in entries) if (entry.PossiblyOff) pending.Add(entry);
                if (pending.Count == 0)
                {
                    List<string> complete = new List<string>();
                    ReleaseWorkerKeepAwake(complete);
                    return complete;
                }
                if (elapsed.ElapsedMilliseconds >= budget) break;
                List<string> refreshErrors = new List<string>();
                List<FreshHandle> fresh = OpenFreshTargets(pending, refreshErrors);
                try
                {
                    foreach (Entry entry in pending)
                    {
                        if (elapsed.ElapsedMilliseconds >= budget) break;
                        FreshHandle handle = FindFresh(fresh, entry);
                        if (handle == null)
                        {
                            problems[entry] = "显示器仍在重新连接，尚未取得新物理句柄。" + String.Join("；", refreshErrors.ToArray());
                            continue;
                        }
                        uint type, mode, maximum;
                        bool read = GetVCPFeatureAndVCPFeatureReply(handle.Handle, 0xD6, out type, out mode, out maximum);
                        if (read && mode == 1)
                        {
                            entry.PossiblyOff = false;
                            problems.Remove(entry);
                            continue;
                        }
                        problems[entry] = read ? "显示器电源状态尚未恢复为 1（当前 " + mode + "）。"
                            : NativeFailure("读取新物理句柄的亮屏状态失败", Marshal.GetLastWin32Error());
                        if (elapsed.ElapsedMilliseconds < budget && !SetVCPFeature(handle.Handle, 0xD6, 1))
                            problems[entry] += " " + NativeFailure("再次亮屏命令未获确认", Marshal.GetLastWin32Error());
                    }
                }
                finally { CloseFreshHandles(fresh); }
                int remaining = budget - (int)elapsed.ElapsedMilliseconds;
                if (remaining > 0) Thread.Sleep(Math.Min(250, remaining));
            }
            List<string> errors = new List<string>();
            foreach (Entry entry in entries)
                if (entry.PossiblyOff)
                {
                    string detail;
                    problems.TryGetValue(entry, out detail);
                    errors.Add(entry.Info.DeviceName + " 8 秒内未能通过新物理句柄确认亮屏；可再次调用 On() 重试。" + detail);
                }
            return errors;
        }

        private List<FreshHandle> OpenFreshTargets(IList<Entry> targets, List<string> errors)
        {
            List<FreshHandle> opened = new List<FreshHandle>();
            bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data)
            {
                try
                {
                    MONITORINFOEX logical = new MONITORINFOEX();
                    logical.Size = (uint)Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (!GetMonitorInfo(monitor, ref logical)) return true;
                    bool relevant = false;
                    foreach (Entry target in targets)
                        if (String.Equals(target.Info.DeviceName, logical.Device, StringComparison.OrdinalIgnoreCase)) { relevant = true; break; }
                    if (!relevant) return true;
                    uint count;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0 || count > 64)
                    {
                        errors.Add(logical.Device + " 暂时无法读取物理显示器数量。");
                        return true;
                    }
                    PHYSICAL_MONITOR[] physical = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                    {
                        errors.Add(logical.Device + " " + NativeFailure("重新打开物理显示器失败", Marshal.GetLastWin32Error()));
                        return true;
                    }
                    for (int index = 0; index < physical.Length; index++)
                    {
                        FreshHandle handle = new FreshHandle();
                        handle.Handle = physical[index].Handle; // Zero is a valid opaque handle.
                        foreach (Entry target in targets)
                            if (target.PhysicalIndex == index && String.Equals(target.Info.DeviceName, logical.Device, StringComparison.OrdinalIgnoreCase)) { handle.Target = target; break; }
                        opened.Add(handle);
                    }
                }
                catch (Exception error) { errors.Add("刷新显示器句柄失败：" + error.Message); }
                return true;
            }, IntPtr.Zero);
            if (!enumerated) errors.Add(NativeFailure("重新枚举显示器失败", Marshal.GetLastWin32Error()));
            return opened;
        }

        private static FreshHandle FindFresh(List<FreshHandle> handles, Entry target)
        {
            foreach (FreshHandle handle in handles) if (Object.ReferenceEquals(handle.Target, target)) return handle;
            return null;
        }

        private static void CloseFreshHandles(List<FreshHandle> handles)
        {
            foreach (FreshHandle handle in handles) DestroyPhysicalMonitor(handle.Handle);
        }

        private void CheckOwnerThread()
        {
            if (GetCurrentThreadId() != ownerThread)
                throw new InvalidOperationException("硬件息屏、亮屏和释放资源必须在创建此对象的同一个工作线程上执行。");
        }

        private void ReleaseWorkerKeepAwake(List<string> errors)
        {
            if (!workerKeepAwake) return;
            if (SetThreadExecutionState(0x80000000u) == 0) errors.Add("未能释放硬件息屏工作线程的临时防休眠请求。");
            else workerKeepAwake = false;
        }

        private void CheckDisposed() { if (disposed) throw new ObjectDisposedException("DdcPower"); }

        private List<string> DestroyOwnedHandles()
        {
            List<string> errors = new List<string>();
            foreach (Entry entry in entries)
            {
                if (!entry.HandleOwned) continue;
                if (!DestroyPhysicalMonitor(entry.Handle)) errors.Add(NativeFailure("关闭物理显示器句柄失败", Marshal.GetLastWin32Error()));
                entry.HandleOwned = false;
            }
            ownedHandles.Clear();
            return errors;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                CheckOwnerThread();
                List<string> errors = new List<string>();
                try { errors.AddRange(RestorePending()); }
                finally
                {
                    errors.AddRange(DestroyOwnedHandles());
                    ReleaseWorkerKeepAwake(errors);
                    disposed = true;
                    LastOperationErrors = new ReadOnlyCollection<string>(errors);
                }
                if (errors.Count != 0) throw new InvalidOperationException("清理硬件息屏资源时发生错误：" + String.Join("；", errors.ToArray()));
            }
        }
    }
}
