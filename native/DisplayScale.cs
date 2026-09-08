using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class MonitorScaleState
{
    public string identity;
    public string device;
    public int percent;
    public int x, y, width, height, rotation;
    public bool canSet;
    public int minimum, maximum;
}

public static class DisplayScale
{
    // DPI packet ABI is not public Windows API. Keep capability checks and
    // independent GetScaleFactorForMonitor verification; never use registry writes.
    // Packet research: https://github.com/lihas/windows-DPI-scaling-sample
    private static readonly int[] Levels = { 100,125,150,175,200,225,250,300,350,400,450,500 };
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public int Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct DpiGet { public Header Header; public int Min, Current, Max; }
    [StructLayout(LayoutKind.Sequential)] private struct DpiSet { public Header Header; public int Relative; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private sealed class Active { public MonitorScaleState State; public Luid Adapter; public uint SourceId; public DpiGet Dpi; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(IntPtr packet);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetDpi(ref DpiGet packet);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int SetDpi(ref DpiSet packet);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    private static void Check(int code, string action) { if (code != 0) throw new InvalidOperationException(action + " (" + code + ")"); }
    private static Dictionary<string, int> LiveScales()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Exception error = null;
        MonitorCallback callback = delegate(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data)
        {
            try
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("无法读取显示器信息。");
                int scale; Check(GetScaleFactorForMonitor(monitor, out scale), "读取系统缩放失败");
                result[info.Device] = scale;
                return true;
            }
            catch (Exception ex) { error = ex; return false; }
        };
        bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (error != null) throw error;
        if (!ok || result.Count == 0) throw new InvalidOperationException("无法读取当前系统缩放。");
        return result;
    }
    private static string DeviceName(int type, int bytes, Luid adapter, uint id, int textOffset, int chars)
    {
        IntPtr packet = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, packet, bytes);
            Marshal.StructureToPtr(new Header { Type = type, Size = bytes, Adapter = adapter, Id = id }, packet, false);
            Check(GetInfo(packet), "读取显示器身份失败");
            return Marshal.PtrToStringUni(IntPtr.Add(packet, textOffset), chars).TrimEnd('\0');
        }
        finally { Marshal.FreeHGlobal(packet); }
    }
    private static List<Active> ReadActive()
    {
        if (Marshal.SizeOf(typeof(DpiGet)) != 32 || Marshal.SizeOf(typeof(DpiSet)) != 24) throw new InvalidOperationException("DPI packet layout mismatch.");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint paths, modes; Check(GetDisplayConfigBufferSizes(2, out paths, out modes), "读取显示配置失败");
            if (paths == 0 || paths > 64 || modes > 1024) throw new InvalidOperationException("显示配置无效。");
            IntPtr pathBuffer = Marshal.AllocHGlobal(checked((int)paths * 72));
            IntPtr modeBuffer = Marshal.AllocHGlobal(checked((int)modes * 64));
            try
            {
                int code = QueryDisplayConfig(2, ref paths, pathBuffer, ref modes, modeBuffer, IntPtr.Zero);
                if (code == 122) continue;
                Check(code, "读取显示配置失败");
                var live = LiveScales(); var result = new List<Active>();
                for (int i = 0; i < paths; i++)
                {
                    // QDC_ONLY_ACTIVE_PATHS (without virtual-mode flags): SDK
                    // DISPLAYCONFIG_PATH_INFO=72; DISPLAYCONFIG_MODE_INFO=64.
                    var path = IntPtr.Add(pathBuffer, i * 72);
                    var adapter = (Luid)Marshal.PtrToStructure(path, typeof(Luid));
                    uint source = unchecked((uint)Marshal.ReadInt32(path, 8));
                    int index = Marshal.ReadInt32(path, 12);
                    var targetAdapter = (Luid)Marshal.PtrToStructure(IntPtr.Add(path,20), typeof(Luid));
                    uint targetId = unchecked((uint)Marshal.ReadInt32(path,28));
                    string device = DeviceName(1,84,adapter,source,20,32);
                    string identity = DeviceName(2,420,targetAdapter,targetId,164,128);
                    int scale;
                    if (String.IsNullOrEmpty(identity) || !live.TryGetValue(device, out scale) || index < 0 || index >= modes) throw new InvalidOperationException("显示器正在切换，请重试。");
                    var mode = IntPtr.Add(modeBuffer,index*64);
                    if (Marshal.ReadInt32(mode) != 1) throw new InvalidOperationException("显示模式无效。");
                    var dpi = new DpiGet { Header = new Header { Type = -3, Size = 32, Adapter = adapter, Id = source } };
                    int dpiCode = GetDpi(ref dpi);
                    int recommended = -dpi.Min, current = recommended + dpi.Current, max = recommended + dpi.Max;
                    bool supported = dpiCode == 0 && recommended >= 0 && recommended < Levels.Length && current >= 0 && current < Levels.Length && max >= 0 && max < Levels.Length && Levels[current] == scale;
                    result.Add(new Active { Adapter = adapter, SourceId = source, Dpi = dpi, State = new MonitorScaleState {
                        identity = identity, device = device, percent = scale,
                        x = Marshal.ReadInt32(mode,28), y = Marshal.ReadInt32(mode,32), width = Marshal.ReadInt32(mode,16), height = Marshal.ReadInt32(mode,20), rotation = Marshal.ReadInt32(path,40),
                        canSet = supported, minimum = supported ? 100 : scale, maximum = supported ? Levels[max] : scale
                    } });
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(pathBuffer); Marshal.FreeHGlobal(modeBuffer); }
        }
        throw new InvalidOperationException("显示配置持续变化，请稍候再试。");
    }
    public static List<MonitorScaleState> Capture() { return ReadActive().Select(x => x.State).ToList(); }
    public static string HardwareSignature(List<MonitorScaleState> monitors)
    {
        return "physical-v1|" + String.Join("|", monitors.OrderBy(x => x.identity,StringComparer.OrdinalIgnoreCase).Select(x => x.identity.ToUpperInvariant()+":"+x.x+","+x.y+","+x.width+","+x.height+","+x.rotation));
    }
    public static bool SameScale(List<MonitorScaleState> saved, List<MonitorScaleState> actual)
    {
        if (saved == null || actual == null) return false;
        var map = actual.ToDictionary(x=>x.identity,StringComparer.OrdinalIgnoreCase);
        return saved.All(x => !map.ContainsKey(x.identity) || map[x.identity].percent == x.percent);
    }
    private static void SetOne(Active current, int percent)
    {
        if (current.State.percent == percent) return;
        int index = Array.IndexOf(Levels,percent);
        if (!current.State.canSet || index < 0 || percent < current.State.minimum || percent > current.State.maximum)
            throw new InvalidOperationException("当前显示器不支持保存时的系统缩放。");
        var packet = new DpiSet { Header = new Header { Type = -4, Size = 24, Adapter = current.Adapter, Id = current.SourceId }, Relative = index + current.Dpi.Min };
        Check(SetDpi(ref packet), "设置系统缩放失败");
    }
    public static void Apply(List<MonitorScaleState> saved, bool allowMissing)
    {
        if (saved == null || saved.Count == 0) throw new InvalidOperationException("布局缺少系统缩放记录。");
        var original = Capture();
        var active = ReadActive();
        var selected = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in saved)
        {
            var current = active.FirstOrDefault(x=>String.Equals(x.State.identity,target.identity,StringComparison.OrdinalIgnoreCase));
            if (current == null) { if (!allowMissing) throw new InvalidOperationException("保存时的显示器未连接。"); continue; }
            string source = current.Adapter.High+":"+current.Adapter.Low+":"+current.SourceId;
            if (selected.ContainsKey(source) && selected[source] != target.percent) throw new InvalidOperationException("复制屏幕模式无法分别设置不同缩放。");
            selected[source] = target.percent;
            int index = Array.IndexOf(Levels,target.percent);
            if (target.percent != current.State.percent && (!current.State.canSet || index < 0 || target.percent < current.State.minimum || target.percent > current.State.maximum)) throw new InvalidOperationException("当前显示器不支持保存时的系统缩放。");
        }
        try
        {
            foreach (var target in saved)
            {
                var current = ReadActive().FirstOrDefault(x=>String.Equals(x.State.identity,target.identity,StringComparison.OrdinalIgnoreCase));
                if (current == null) { if (allowMissing) continue; throw new InvalidOperationException("恢复期间显示器已断开。"); }
                SetOne(current,target.percent);
            }
            int stable = 0;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var actual = Capture();
                bool present = allowMissing || saved.All(x=>actual.Any(a=>String.Equals(a.identity,x.identity,StringComparison.OrdinalIgnoreCase)));
                stable = present && SameScale(saved,actual) ? stable+1 : 0;
                if (stable >= 3) return;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("系统缩放未生效，已停止恢复。");
        }
        catch (Exception failure)
        {
            bool rolledBack = true;
            foreach (var old in original)
            {
                try { var now = ReadActive().FirstOrDefault(x=>String.Equals(x.State.identity,old.identity,StringComparison.OrdinalIgnoreCase)); if (now != null) SetOne(now,old.percent); }
                catch { rolledBack = false; }
            }
            throw new InvalidOperationException(failure.Message + (rolledBack ? " 已尝试还原操作前缩放。" : " 部分缩放未能还原。"),failure);
        }
    }
}
