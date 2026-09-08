using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

// The desktop is a Shell folder view. Do not substitute registry caches or
// ListView cell spacing for IFolderView2's live icon size.
public sealed class ShellLayoutState
{
    public int version = 1;
    public int viewMode;
    public int iconSize;
    public uint folderFlags;
    public int spacingX;
    public int spacingY;
    public int metricX;
    public int metricY;
    public List<ShellLayoutItem> items = new List<ShellLayoutItem>();
}

public sealed class ShellLayoutItem
{
    public string identity;
    public string name;
    public int x;
    public int y;
}

public sealed class ShellRestoreResult
{
    public int matched;
    public int missing;
    public int misplaced;
    public int added;
    public bool sizeMatches;
    public bool flagsMatch;
    public bool spacingMatches;
    public bool Complete { get { return missing == 0 && misplaced == 0 && sizeMatches && flagsMatch && spacingMatches; } }
}

public sealed class ShellDesktop : IDisposable
{
    private IntPtr view;
    private IntPtr viewWindow;
    private readonly int threadId;
    private const uint ArrangeMask = 0x5; // FWF_AUTOARRANGE | FWF_SNAPTOGRID
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    // Slots follow the Windows SDK IShellWindows/IShellBrowser/IFolderView2 ABI.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl_core.h
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindWindow(IntPtr self, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int kind, out int hwnd, int options, out IntPtr dispatch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryService(IntPtr self, ref Guid service, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ViewAction(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(IntPtr self, out int mode, out int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSize(IntPtr self, int mode, int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFlags(IntPtr self, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFlags(IntPtr self, uint mask, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CountItems(IntPtr self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItem(IntPtr self, int index, out IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPosition(IntPtr self, IntPtr pidl, out Point point);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PositionItems(IntPtr self, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] Point[] points, uint flags);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);

    private static T Method<T>(IntPtr instance, int slot) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T));
    }
    private static void Check(int hr, string action)
    {
        if (hr < 0) throw new InvalidOperationException(action + " (0x" + hr.ToString("X8") + ")", Marshal.GetExceptionForHR(hr));
    }
    private static IntPtr Query(IntPtr source, string id)
    {
        var iid = new Guid(id); IntPtr result;
        Check(Marshal.QueryInterface(source, ref iid, out result), "Shell QueryInterface");
        return result;
    }
    public ShellDesktop()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Shell desktop requires an STA thread.");
        threadId = Thread.CurrentThread.ManagedThreadId;
        IntPtr windows = IntPtr.Zero, dispatch = IntPtr.Zero, service = IntPtr.Zero, browser = IntPtr.Zero, shellView = IntPtr.Zero;
        try
        {
            var clsid = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
            var iid = new Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85");
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out windows), "ShellWindows");
            object location = 0, root = 0; int hwnd;
            Check(Method<FindWindow>(windows, 15)(windows, ref location, ref root, 8, out hwnd, 1, out dispatch), "Find desktop view");
            if (dispatch == IntPtr.Zero) throw new InvalidOperationException("Desktop view is unavailable.");
            service = Query(dispatch, "6D5140C1-7436-11CE-8034-00AA006009FA");
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            iid = new Guid("000214E2-0000-0000-C000-000000000046");
            Check(Method<QueryService>(service, 3)(service, ref sid, ref iid, out browser), "Desktop browser");
            Check(Method<GetInterface>(browser, 15)(browser, out shellView), "Active desktop view");
            Check(Method<GetInterface>(shellView, 3)(shellView, out viewWindow), "Desktop window");
            view = Query(shellView, "1AF3A467-214F-4298-908E-06B03E0B39F9");
        }
        finally
        {
            foreach (var ptr in new[] { shellView, browser, service, dispatch, windows }) if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
    }
    private void CheckThread()
    {
        if (view == IntPtr.Zero || Thread.CurrentThread.ManagedThreadId != threadId) throw new InvalidOperationException("Invalid Shell desktop lifetime/thread.");
    }
    public ShellLayoutState ReadSettings()
    {
        CheckThread(); var result = new ShellLayoutState();
        Check(Method<GetSize>(view, 36)(view, out result.viewMode, out result.iconSize), "Read desktop icon size");
        Check(Method<GetFlags>(view, 25)(view, out result.folderFlags), "Read folder flags");
        int[] metrics = DesktopGridMetrics.Read(); result.metricX = metrics[0]; result.metricY = metrics[1];
        IntPtr spacing;
        var list = FindWindowEx(viewWindow, IntPtr.Zero, "SysListView32", null);
        if (list != IntPtr.Zero && SendMessageTimeout(list, 0x1033, IntPtr.Zero, IntPtr.Zero, 2, 1000, out spacing) != IntPtr.Zero)
        {
            var packed = unchecked((uint)spacing.ToInt64()); result.spacingX = (int)(packed & 65535); result.spacingY = (int)(packed >> 16);
        }
        return result;
    }
    public void Refresh()
    {
        CheckThread();
        IntPtr shellView = Query(view,"000214E3-0000-0000-C000-000000000046");
        try { Check(Method<ViewAction>(shellView,8)(shellView),"Refresh desktop"); }
        finally { Marshal.Release(shellView); }
    }
    public void SaveViewState()
    {
        CheckThread();
        IntPtr shellView = Query(view,"000214E3-0000-0000-C000-000000000046");
        try { Check(Method<ViewAction>(shellView,13)(shellView),"Save desktop view state"); }
        finally { Marshal.Release(shellView); }
    }
    private static string ItemName(IntPtr pidl, uint format)
    {
        IntPtr text;
        Check(SHGetNameFromIDList(pidl, format, out text), "Read Shell item identity");
        try { return Marshal.PtrToStringUni(text); } finally { Marshal.FreeCoTaskMem(text); }
    }
    private List<IntPtr> Enumerate()
    {
        CheckThread(); int count;
        Check(Method<CountItems>(view, 7)(view, 2, out count), "Count desktop items");
        var result = new List<IntPtr>();
        try
        {
            for (int i = 0; i < count; i++) { IntPtr pidl; Check(Method<GetItem>(view, 6)(view, i, out pidl), "Read desktop item"); result.Add(pidl); }
            return result;
        }
        catch { foreach (var ptr in result) Marshal.FreeCoTaskMem(ptr); throw; }
    }
    public ShellLayoutState Capture()
    {
        var result = ReadSettings(); var pidls = Enumerate();
        try
        {
            foreach (var pidl in pidls)
            {
                Point pos; Check(Method<GetPosition>(view, 11)(view, pidl, out pos), "Read desktop position");
                result.items.Add(new ShellLayoutItem { identity = ItemName(pidl, 0x80028000), name = ItemName(pidl, 0), x = pos.X, y = pos.Y });
            }
        }
        finally { foreach (var ptr in pidls) Marshal.FreeCoTaskMem(ptr); }
        var after = ReadSettings();
        if (after.iconSize != result.iconSize || after.viewMode != result.viewMode) throw new InvalidOperationException("捕获期间图标大小发生变化，请重试。");
        return result;
    }
    public void ApplySize(int mode, int size)
    {
        CheckThread();
        if (mode < 1 || mode > 8 || size < 16 || size > 256) throw new InvalidOperationException("无效的图标大小记录。");
        Check(Method<SetSize>(view, 35)(view, mode, size), "Set desktop icon size");
        int stable = 0;
        for (int i = 0; i < 20; i++)
        {
            var actual = ReadSettings();
            stable = actual.viewMode == mode && actual.iconSize == size ? stable + 1 : 0;
            if (stable >= 3) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("图标大小未能恢复，已取消排列恢复。");
    }
    private void Arrangement(uint flags)
    {
        Check(Method<SetFlags>(view, 24)(view, ArrangeMask, flags & ArrangeMask), "Set desktop arrangement flags");
    }
    private void RestoreSpacing(ShellLayoutState state)
    {
        if (state.spacingX < 4 || state.spacingY < 4) return;
        var list = FindWindowEx(viewWindow, IntPtr.Zero, "SysListView32", null);
        IntPtr previous;
        var packed = unchecked((uint)(state.spacingX | (state.spacingY << 16)));
        if (SendMessageTimeout(list, 0x1035, IntPtr.Zero, new IntPtr((long)packed), 2, 1000, out previous) == IntPtr.Zero) throw new InvalidOperationException("Desktop grid unavailable.");
    }
    private void ApplyPersistentGrid(ShellLayoutState saved)
    {
        // SPI stores base metrics, whereas Shell spacing includes additional
        // layout metrics. They must not be assigned the same value blindly.
        // Older snapshots recorded only the rendered spacing. Calibrate their
        // base metrics from measured Shell results, without shifting coordinates.
        var metrics = DesktopGridMetrics.Read();
        int x = saved.metricX > 0 ? saved.metricX : metrics[0];
        int y = saved.metricY > 0 ? saved.metricY : metrics[1];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var currentMetrics = DesktopGridMetrics.Read();
            if (currentMetrics[0] != x || currentMetrics[1] != y) DesktopGridMetrics.Apply(x,y);
            Arrangement(4);
            Arrangement(0);
            Thread.Sleep(100);
            var actual = ReadSettings();
            if (actual.spacingX == saved.spacingX && actual.spacingY == saved.spacingY) return;
            x = checked(x + saved.spacingX - actual.spacingX);
            y = checked(y + saved.spacingY - actual.spacingY);
            if (x < 4 || y < 4 || x > 16384 || y > 16384) break;
        }
        throw new InvalidOperationException("无法恢复保存时的图标间距。");
    }
    public void Position(ShellLayoutState saved)
    {
        var pidls = Enumerate();
        try
        {
            var targets = saved.items.ToDictionary(x => x.identity, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<IntPtr>(); var positions = new List<Point>();
            foreach (var pidl in pidls)
            {
                ShellLayoutItem target;
                if (!targets.TryGetValue(ItemName(pidl, 0x80028000), out target)) continue;
                chosen.Add(pidl); positions.Add(new Point { X = target.x, Y = target.y });
            }
            if (chosen.Count > 0) Check(Method<PositionItems>(view, 16)(view, (uint)chosen.Count, chosen.ToArray(), positions.ToArray(), 0x80), "Position desktop items");
        }
        finally { foreach (var ptr in pidls) Marshal.FreeCoTaskMem(ptr); }
    }
    public void RecoverCapturedCoordinates(ShellLayoutState saved)
    {
        // Preserve Shell flags while restoring the control's pre-test state.
        var list = FindWindowEx(viewWindow, IntPtr.Zero, "SysListView32", null);
        IntPtr style;
        if (SendMessageTimeout(list, 0x1037, IntPtr.Zero, IntPtr.Zero, 2, 1000, out style) == IntPtr.Zero) throw new InvalidOperationException("Desktop styles unavailable.");
        IntPtr ignored;
        try
        {
            if (SendMessageTimeout(list, 0x1036, new IntPtr(0x80000), IntPtr.Zero, 2, 1000, out ignored) == IntPtr.Zero)
                throw new InvalidOperationException("Cannot suspend desktop snapping.");
            RestoreSpacing(saved);
            Position(saved);
        }
        finally
        {
            if (SendMessageTimeout(list, 0x1036, new IntPtr(0x80000), new IntPtr(style.ToInt64() & 0x80000), 2, 1000, out ignored) == IntPtr.Zero)
                throw new InvalidOperationException("Cannot restore desktop snapping.");
        }
    }
    public static ShellRestoreResult Compare(ShellLayoutState saved, ShellLayoutState actual)
    {
        var lookup = actual.items.ToDictionary(x => x.identity, StringComparer.OrdinalIgnoreCase);
        var result = new ShellRestoreResult { sizeMatches = saved.iconSize == actual.iconSize && saved.viewMode == actual.viewMode, flagsMatch = (saved.folderFlags & ArrangeMask) == (actual.folderFlags & ArrangeMask) };
        result.spacingMatches = saved.spacingX == actual.spacingX && saved.spacingY == actual.spacingY;
        foreach (var item in saved.items)
        {
            ShellLayoutItem current;
            if (!lookup.TryGetValue(item.identity, out current)) result.missing++;
            else { result.matched++; if (current.x != item.x || current.y != item.y) result.misplaced++; }
        }
        result.added = actual.items.Count - result.matched;
        return result;
    }
    public ShellRestoreResult Restore(ShellLayoutState saved)
    {
        return Restore(saved, false);
    }
    public ShellRestoreResult Restore(ShellLayoutState saved, bool bestEffort)
    {
        if (saved == null || saved.version != 1 || saved.items == null || saved.items.Count == 0) throw new InvalidOperationException("布局缺少实时图标大小记录，请重新保存后再恢复。");
        var before = Capture();
        bool completed = false;
        try
        {
            if (before.iconSize != saved.iconSize || before.viewMode != saved.viewMode) ApplySize(saved.viewMode, saved.iconSize);
            ShellRestoreResult result = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Arrangement(0);
                try { ApplyPersistentGrid(saved); Position(saved); }
                finally { Arrangement(saved.folderFlags); }
                Position(saved);
                SaveViewState();
                bool stable = true;
                for (int refresh = 0; refresh < 3; refresh++)
                {
                    Refresh();
                    Thread.Sleep(250);
                    result = Compare(saved, Capture());
                    if (result.missing != 0 || result.misplaced != 0 || !result.sizeMatches || !result.flagsMatch || !result.spacingMatches) { stable = false; break; }
                }
                if (stable) { completed = true; return result; }
            }
            if (bestEffort) { completed = true; return result; }
            return result;
        }
        finally
        {
            if (!completed)
            {
                ApplySize(before.viewMode,before.iconSize);
                DesktopGridMetrics.Apply(before.metricX,before.metricY);
                Arrangement(before.folderFlags);
                RecoverCapturedCoordinates(before); // Emergency rollback to pre-operation state only.
            }
        }
    }
    public void Dispose()
    {
        if (view != IntPtr.Zero) { CheckThread(); Marshal.Release(view); view = IntPtr.Zero; }
    }
}

internal static class DesktopGridMetrics
{
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")] private static extern bool Get(uint action,uint param,out int value,uint flags);
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")] private static extern bool Set(uint action,uint param,IntPtr value,uint flags);
    public static int[] Read()
    {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (old == IntPtr.Zero) throw new InvalidOperationException("无法读取图标间距的缩放环境。");
        try
        {
            int x,y;
            if (!Get(13,0,out x,0) || !Get(24,0,out y,0)) throw new InvalidOperationException("无法读取系统图标间距。");
            return new[] {x,y};
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }
    public static void Apply(int x,int y)
    {
        if (x < 4 || y < 4 || x > 16384 || y > 16384) throw new InvalidOperationException("图标间距记录无效。");
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (old == IntPtr.Zero) throw new InvalidOperationException("无法设置图标间距的缩放环境。");
        try
        {
            if (!Set(13,(uint)x,IntPtr.Zero,3) || !Set(24,(uint)y,IntPtr.Zero,3)) throw new InvalidOperationException("系统图标间距设置失败。");
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }
}
