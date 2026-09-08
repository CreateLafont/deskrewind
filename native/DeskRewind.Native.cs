using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    // Entry point invoked by the single-file WPF host.
    internal static int Execute(string[] args)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA) return Main(args);
        int result = 1;
        var thread = new System.Threading.Thread(delegate() { result = Main(args); });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start(); thread.Join();
        return result;
    }

    private static int Main(string[] args)
    {
        if (IntPtr.Size != 8) return Fail("必须使用 64 位程序。");
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h") return Help();
        string command = args[0].ToLowerInvariant();
        string store = GetOption(args, "--store") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts.profiles.json");
        string trace = GetOption(args, "--trace");
        string name = GetOption(args, "--name");
        string profileId = GetOption(args, "--profile");
        bool force = HasFlag(args, "--force");
        string legacyStore = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts.json");
        try
        {
            if (command == "diagnose") { Diagnose(); return 0; }
            if (command == "list") { ListProfiles(store, legacyStore, args.Any(a => String.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))); return 0; }
            if (command == "inspect") { Inspect(store, profileId); return 0; }
            if (command == "save") { Save(store, legacyStore, trace, name); return 0; }
            if (command == "overwrite") { OverwriteProfile(store, legacyStore, trace, profileId); return 0; }
            if (command == "restore") { Restore(store, legacyStore, trace, profileId, force, GetOption(args, "--expected")); return 0; }
            if (command == "rename") { RenameProfile(store, legacyStore, profileId, name); return 0; }
            if (command == "delete") { DeleteProfile(store, legacyStore, profileId); return 0; }
            return Fail("未知命令：" + args[0]);
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static int Help()
    {
        Console.WriteLine("DeskRewind Native CLI");
        Console.WriteLine("  DeskRewind.Native.exe diagnose");
        Console.WriteLine("  DeskRewind.Native.exe list [--json]");
        Console.WriteLine("  DeskRewind.Native.exe save --name <name> [--trace <path>]");
        Console.WriteLine("  DeskRewind.Native.exe overwrite --profile <id> [--trace <path>]");
        Console.WriteLine("  DeskRewind.Native.exe restore --profile <id> [--force] [--trace <path>]");
        Console.WriteLine("  DeskRewind.Native.exe inspect --profile <id> --json");
        Console.WriteLine("  DeskRewind.Native.exe rename --profile <id> --name <name>");
        Console.WriteLine("  DeskRewind.Native.exe delete --profile <id>");
        return 0;
    }
    private static int Fail(string text) { Console.Error.WriteLine("错误：" + text); return 1; }
    private static bool HasFlag(string[] args, string name) { return args.Any(a => String.Equals(a, name, StringComparison.OrdinalIgnoreCase)); }
    private static string GetOption(string[] args, string name)
    {
        for (int i = 1; i + 1 < args.Length; i++) if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void Save(string store, string legacyStore, string trace, string name)
    {
        IntPtr list = Desktop.FindListView();
        if (list == IntPtr.Zero) throw new InvalidOperationException("无法访问桌面图标列表，\n请在交互式桌面会话中运行。");
        if (Desktop.AutoArrange(list)) throw new InvalidOperationException("桌面已开启“自动排列图标”，\n请先关闭后再保存。");
        Dictionary<string, Icon> icons = Desktop.ReadIcons(list);
        if (icons.Count == 0) throw new InvalidOperationException("未读取到桌面图标，\n已取消保存。");
        string signature = Displays.Signature();
        string hardware = Displays.HardwareSignature();
        int spacingX, spacingY;
        if (!Desktop.IconSpacing(list, out spacingX, out spacingY)) throw new InvalidOperationException("无法读取桌面图标大小，\n已取消保存。");
        List<MonitorScaleState> monitors = DisplayScale.Capture();
        ShellLayoutState shellState;
        using (var shell = new ShellDesktop()) shellState = shell.Capture();
        ValidateCapturedEnvironment(monitors,shellState);
        ProfileDocument data = ProfileStore.Load(store, legacyStore);
        List<LayoutProfile> sameTopology = data.profiles.Where(p => p.topology == signature).ToList();
        string profileName = String.IsNullOrWhiteSpace(name) ? "布局 " + DateTime.Now.ToString("MMddHHmm") : NormalizeProfileName(name);
        if (sameTopology.Any(p => String.Equals(p.name, profileName, StringComparison.CurrentCultureIgnoreCase))) throw new InvalidOperationException("当前屏幕布局中已有同名版本，\n请先重命名或换一个名称。");
        if (sameTopology.Count >= 5) throw new InvalidOperationException("当前屏幕布局最多保存 5 个版本，\n请删除一个旧版本后再保存。");
        data.profiles.Add(new LayoutProfile { id = Guid.NewGuid().ToString("N"), name = profileName, savedAt = DateTime.Now.ToString("o"), topology = signature, hardware = hardware, monitors = monitors, shellState = shellState, iconSpacingX = spacingX, iconSpacingY = spacingY, icons = icons.ToDictionary(x => x.Key, x => new[] { x.Value.X, x.Value.Y }) });
        ProfileStore.Save(store, data);
        TraceLog.Snapshot(trace, "save", "saved", Displays.Signature(), icons, null, null);
        Console.WriteLine("已保存布局 [{0}]，共 {1} 个图标。", Displays.Signature(), icons.Count);
    }

    private static void OverwriteProfile(string store, string legacyStore, string trace, string profileId)
    {
        if (String.IsNullOrWhiteSpace(profileId)) throw new InvalidOperationException("覆盖需要指定 --profile。");
        IntPtr list = Desktop.FindListView();
        if (list == IntPtr.Zero) throw new InvalidOperationException("无法访问桌面图标列表，\n请在交互式桌面会话中运行。");
        if (Desktop.AutoArrange(list)) throw new InvalidOperationException("桌面已开启“自动排列图标”，\n请先关闭后再保存。");
        string signature = Displays.Signature();
        string hardware = Displays.HardwareSignature();
        ProfileDocument data = ProfileStore.Load(store, legacyStore);
        LayoutProfile profile = data.profiles.FirstOrDefault(p => p.id == profileId);
        if (profile == null) throw new InvalidOperationException("未找到当前屏幕布局中的指定版本。");
        Dictionary<string, Icon> icons = Desktop.ReadIcons(list);
        if (icons.Count == 0) throw new InvalidOperationException("未读取到桌面图标，\n已取消覆盖。");
        profile.icons = icons.ToDictionary(x => x.Key, x => new[] { x.Value.X, x.Value.Y });
        profile.savedAt = DateTime.Now.ToString("o");
        int spacingX, spacingY;
        if (!Desktop.IconSpacing(list, out spacingX, out spacingY)) throw new InvalidOperationException("无法读取桌面图标大小，\n已取消覆盖。");
        profile.topology = signature;
        profile.hardware = hardware;
        profile.monitors = DisplayScale.Capture();
        using (var shell = new ShellDesktop()) profile.shellState = shell.Capture();
        ValidateCapturedEnvironment(profile.monitors,profile.shellState);
        profile.iconSpacingX = spacingX;
        profile.iconSpacingY = spacingY;
        ProfileStore.Save(store, data);
        TraceLog.Snapshot(trace, "overwrite", "saved", signature, icons, null, profile.name);
        Console.WriteLine("已用当前桌面排列覆盖布局“{0}”，共 {1} 个图标。", profile.name, icons.Count);
    }

    private static void Restore(string store, string legacyStore, string trace, string profileId, bool force, string expected)
    {
        var data = ProfileStore.Load(store, legacyStore);
        var selected = data.profiles.FirstOrDefault(p => p.id == profileId);
        if (selected == null) throw new InvalidOperationException("未找到要恢复的布局。");
        if (selected.shellState == null || selected.shellState.version != 1)
            throw new InvalidOperationException("旧布局未记录实时图标大小，请重新保存后再恢复。");
        if (!LayoutEnvironment.HasSystemScale(selected))
            throw new InvalidOperationException("旧布局未记录系统缩放，请重新保存后再恢复。");
        IntPtr list;
        if (!Desktop.WaitUntilReady(out list)) throw new InvalidOperationException("桌面尚未就绪，请稍候再试。");
        var current = LayoutEnvironment.Capture(list);
        if (!String.IsNullOrEmpty(expected) && expected != LayoutEnvironment.Fingerprint(current))
            throw new InvalidOperationException("确认期间桌面环境已变化，请重新应用布局。");
        var mismatch = LayoutEnvironment.Compare(selected, current);
        if (mismatch.Any && !force) throw new InvalidOperationException("布局环境已变化，请重新确认后恢复。");
        ShellLayoutState beforeDesktop;
        using (var shell = new ShellDesktop()) beforeDesktop = shell.Capture();
        bool scaleAttempted = mismatch.SystemScale;
        ShellRestoreResult shellResult = null;
        try
        {
        if (scaleAttempted) DisplayScale.Apply(selected.monitors, mismatch.Hardware);
        using (var shell = new ShellDesktop())
        {
            shellResult = shell.Restore(selected.shellState, mismatch.Hardware);
            if (!shellResult.Complete && !mismatch.Hardware)
                throw new InvalidOperationException(String.Format("未完全恢复：{0} 个图标缺失，{1} 个图标位置不符。", shellResult.missing, shellResult.misplaced));
            var verified = shell.Capture();
            shellResult = ShellDesktop.Compare(selected.shellState, verified);
            if (!shellResult.Complete && !mismatch.Hardware) throw new InvalidOperationException("桌面状态再次变化，请重试。");
            if (LayoutEnvironment.HasSystemScale(selected) && !DisplayScale.SameScale(selected.monitors,DisplayScale.Capture()))
                throw new InvalidOperationException("系统缩放再次变化，恢复未完成。");
        }
        }
        catch
        {
            if (scaleAttempted)
            {
                try
                {
                    DisplayScale.Apply(current.monitors, true);
                    using (var recovery = new ShellDesktop())
                    {
                        if (!recovery.Restore(beforeDesktop).Complete) throw new InvalidOperationException("桌面未完全还原。");
                    }
                }
                catch (Exception rollback) { throw new InvalidOperationException("恢复失败，操作前状态未能完全还原：" + rollback.Message); }
            }
            throw;
        }
        if (mismatch.Hardware)
        {
            Console.WriteLine(shellResult.Complete ? "已尝试恢复，请检查图标排列。" : "已尝试恢复，部分图标位置可能不同，请检查图标排列。");
            return;
        }
        Console.WriteLine("已恢复布局“{0}”。", selected.name);
    }

    private static void ValidateCapturedEnvironment(List<MonitorScaleState> monitors, ShellLayoutState shellState)
    {
        var after = DisplayScale.Capture();
        if (DisplayScale.HardwareSignature(monitors) != DisplayScale.HardwareSignature(after) || !DisplayScale.SameScale(monitors,after))
            throw new InvalidOperationException("保存期间显示环境已变化，请重试。");
        using (var shell = new ShellDesktop())
            if (!LayoutEnvironment.SameIconState(shellState,shell.ReadSettings())) throw new InvalidOperationException("保存期间图标大小已变化，请重试。");
    }

    private static void ListProfiles(string store, string legacyStore, bool json)
    {
        EnvironmentSnapshot current = LayoutEnvironment.Capture(Desktop.FindListView());
        List<LayoutProfile> profiles = ProfileStore.Load(store, legacyStore).profiles.OrderByDescending(p => LayoutEnvironment.HardwareMatches(p, current)).ThenByDescending(p => p.savedAt).ToList();
        if (json)
        {
            Console.WriteLine(new JavaScriptSerializer().Serialize(profiles.Select(p => new { p.id, p.name, p.savedAt, iconCount = p.icons == null ? 0 : p.icons.Count, environmentToken = LayoutEnvironment.Fingerprint(current), hardwareMatch = LayoutEnvironment.HardwareMatches(p, current), systemScaleKnown = LayoutEnvironment.HasSystemScale(p), systemScaleMatch = !LayoutEnvironment.HasSystemScale(p) || DisplayScale.SameScale(p.monitors, current.monitors), iconGridKnown = LayoutEnvironment.HasIconGrid(p), iconGridMatch = !LayoutEnvironment.HasIconGrid(p) || LayoutEnvironment.SameIconState(p.shellState, current.shellState) }).ToList()));
            return;
        }
        Console.WriteLine("当前屏幕布局共有 {0}/5 个已保存版本。", profiles.Count);
        foreach (LayoutProfile profile in profiles) Console.WriteLine("{0}\t{1}\t{2}\t{3} 个图标", profile.id, profile.name, profile.savedAt, profile.icons == null ? 0 : profile.icons.Count);
    }

    private static void Inspect(string store, string profileId)
    {
        if (String.IsNullOrWhiteSpace(profileId)) throw new InvalidOperationException("检查环境需要指定 --profile。");
        LayoutProfile profile = ProfileStore.Load(store, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts.json")).profiles.FirstOrDefault(p => p.id == profileId);
        if (profile == null) throw new InvalidOperationException("未找到要检查的布局版本。");
        IntPtr list = Desktop.FindListView();
        if (list == IntPtr.Zero) throw new InvalidOperationException("无法访问桌面图标列表。");
        Console.WriteLine(new JavaScriptSerializer().Serialize(LayoutEnvironment.Report(profile, LayoutEnvironment.Capture(list))));
    }

    private static string NormalizeProfileName(string name)
    {
        string trimmed = name == null ? String.Empty : name.Trim();
        if (String.IsNullOrWhiteSpace(trimmed)) throw new InvalidOperationException("布局名称不能为空。");
        if (ProfileNameWidth(trimmed) > 9.0) throw new InvalidOperationException("布局名称过长：最多 9 个全角显示单位\n（中文/全角算 1，英文、数字和空格算 0.5）。");
        return trimmed;
    }

    private static double ProfileNameWidth(string name)
    {
        double width = 0;
        foreach (char c in name)
        {
            bool wide = (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF01 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);
            width += wide ? 1.0 : 0.5;
        }
        return width;
    }

    private static void RenameProfile(string store, string legacyStore, string profileId, string name)
    {
        if (String.IsNullOrWhiteSpace(profileId) || String.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("重命名需要指定 --profile 和 --name。");
        ProfileDocument data = ProfileStore.Load(store, legacyStore);
        LayoutProfile profile = data.profiles.FirstOrDefault(p => p.id == profileId);
        if (profile == null) throw new InvalidOperationException("未找到要重命名的布局版本。");
        string trimmed = NormalizeProfileName(name);
        if (data.profiles.Any(p => p.id != profile.id && p.topology == profile.topology && String.Equals(p.name, trimmed, StringComparison.CurrentCultureIgnoreCase))) throw new InvalidOperationException("同一屏幕布局中已有同名版本。");
        profile.name = trimmed;
        ProfileStore.Save(store, data);
        Console.WriteLine("已重命名布局“{0}”。", trimmed);
    }

    private static void DeleteProfile(string store, string legacyStore, string profileId)
    {
        if (String.IsNullOrWhiteSpace(profileId)) throw new InvalidOperationException("删除需要指定 --profile。");
        ProfileDocument data = ProfileStore.Load(store, legacyStore);
        LayoutProfile profile = data.profiles.FirstOrDefault(p => p.id == profileId);
        if (profile == null) throw new InvalidOperationException("未找到要删除的布局版本。");
        data.profiles.Remove(profile);
        ProfileStore.Save(store, data);
        Console.WriteLine("已删除布局“{0}”。", profile.name);
    }

    private static void Diagnose()
    {
        IntPtr list = Desktop.FindListView();
        if (list == IntPtr.Zero) { Console.WriteLine("未识别到桌面图标列表，\n请在交互式桌面会话中运行。"); return; }
        Dictionary<string, Icon> icons = Desktop.ReadIcons(list);
        Screen[] screens = Screen.AllScreens;
        string topology;
        string[] labels;
        if (screens.Length == 1)
        {
            topology = "单屏";
            labels = new[] { "主屏" };
        }
        else if (screens.Length == 2)
        {
            Screen first = screens[0], second = screens[1];
            bool horizontalOverlap = first.Bounds.Left < second.Bounds.Right && second.Bounds.Left < first.Bounds.Right;
            bool verticalOverlap = first.Bounds.Top < second.Bounds.Bottom && second.Bounds.Top < first.Bounds.Bottom;
            if (horizontalOverlap && !verticalOverlap)
            {
                screens = screens.OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToArray();
                topology = "上下双屏";
                labels = new[] { "上屏", "下屏" };
            }
            else if (verticalOverlap && !horizontalOverlap)
            {
                screens = screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
                topology = "左右双屏";
                labels = new[] { "左屏", "右屏" };
            }
            else
            {
                screens = screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
                topology = "双屏（斜向排列）";
                labels = new[] { "屏幕 1", "屏幕 2" };
            }
        }
        else
        {
            screens = screens.OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToArray();
            topology = screens.Length + "屏";
            labels = screens.Select((s, i) => "屏幕 " + (i + 1)).ToArray();
        }
        var counts = screens.ToDictionary(s => s.DeviceName, s => 0);
        foreach (Icon icon in icons.Values)
        {
            string device = Desktop.ScreenForItemPosition(list, icon.X, icon.Y);
            if (device != null && counts.ContainsKey(device)) counts[device]++;
        }
        Console.WriteLine("已识别{0}，桌面共有 {1} 个图标。", topology, icons.Count);
        for (int i = 0; i < screens.Length; i++)
        {
            Screen screen = screens[i];
            Console.WriteLine("{0}分辨率 {1}×{2}，有 {3} 个图标。", labels[i], screen.Bounds.Width, screen.Bounds.Height, counts[screen.DeviceName]);
        }
    }
}

internal sealed class Icon { public string Key; public int Index; public int X; public int Y; }
internal sealed class LayoutProfile
{
    public ShellLayoutState shellState;
    public string id;
    public string name;
    public string savedAt;
    public string topology;
    public string hardware;
    public Dictionary<string, int> systemScale = null; // Preserve legacy cached records; never apply them.
    public List<MonitorScaleState> monitors;
    public int iconSpacingX;
    public int iconSpacingY;
    public Dictionary<string, int[]> icons;
}
internal sealed class ProfileDocument { public string format = "DeskRewind.Profiles/v1"; public List<LayoutProfile> profiles = new List<LayoutProfile>(); }

internal static class Store
{
    public static Dictionary<string, Dictionary<string, int[]>> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, Dictionary<string, int[]>>();
        try { return new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, int[]>>>(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex) { throw new InvalidOperationException("布局存档无法读取：" + ex.Message); }
    }
    public static void Save(string path, Dictionary<string, Dictionary<string, int[]>> data)
    {
        string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full), temp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Process.GetCurrentProcess().Id + ".tmp");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("存档目录不存在：" + dir);
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
        try { if (File.Exists(full)) File.Replace(temp, full, full + ".bak", true); else File.Move(temp, full); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class ProfileStore
{
    public static ProfileDocument Load(string path, string legacyPath)
    {
        if (File.Exists(path))
        {
            try
            {
                ProfileDocument document = new JavaScriptSerializer().Deserialize<ProfileDocument>(File.ReadAllText(path, Encoding.UTF8));
                if (document == null) throw new InvalidOperationException("多版本存档为空。");
                if (document.profiles == null) document.profiles = new List<LayoutProfile>();
                return document;
            }
            catch (Exception ex) { throw new InvalidOperationException("多版本存档无法读取：" + ex.Message); }
        }
        var imported = new ProfileDocument();
        if (File.Exists(legacyPath))
        {
            Dictionary<string, Dictionary<string, int[]>> old = Store.Load(legacyPath);
            int number = 0;
            foreach (var item in old)
            {
                number++;
                imported.profiles.Add(new LayoutProfile { id = Guid.NewGuid().ToString("N"), name = "导入的布局 " + number, savedAt = File.GetLastWriteTime(legacyPath).ToString("o"), topology = item.Key, icons = item.Value });
            }
        }
        Save(path, imported);
        return imported;
    }

    public static void Save(string path, ProfileDocument data)
    {
        string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full), temp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Process.GetCurrentProcess().Id + ".tmp");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("存档目录不存在：" + dir);
        data.format = "DeskRewind.Profiles/v3";
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
        try { if (File.Exists(full)) File.Replace(temp, full, full + ".bak", true); else File.Move(temp, full); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class EnvironmentSnapshot
{
    public ShellLayoutState shellState;
    public string topology;
    public string hardware;
    public List<MonitorScaleState> monitors;
    public int iconSpacingX;
    public int iconSpacingY;
}

internal sealed class EnvironmentMismatch
{
    public bool Hardware;
    public bool SystemScale;
    public bool IconGrid;
    public bool Any { get { return Hardware || SystemScale || IconGrid; } }
}

internal sealed class EnvironmentReport
{
    public bool hardwareMatch;
    public bool systemScaleKnown;
    public bool systemScaleMatch;
    public bool iconGridKnown;
    public bool iconGridMatch;
}

internal static class LayoutEnvironment
{
    public static EnvironmentSnapshot Capture(IntPtr list)
    {
        int x = 0, y = 0;
        if (list != IntPtr.Zero) Desktop.IconSpacing(list, out x, out y);
        ShellLayoutState state;
        using (var shell = new ShellDesktop()) state = shell.ReadSettings();
        var monitors = DisplayScale.Capture();
        string hardware = DisplayScale.HardwareSignature(monitors);
        return new EnvironmentSnapshot { topology = hardware, hardware = hardware, monitors = monitors, shellState = state, iconSpacingX = x, iconSpacingY = y };
    }

    public static EnvironmentMismatch Compare(LayoutProfile profile, EnvironmentSnapshot current)
    {
        EnvironmentReport report = Report(profile, current);
        return new EnvironmentMismatch { Hardware = !report.hardwareMatch, SystemScale = report.systemScaleKnown && !report.systemScaleMatch, IconGrid = report.iconGridKnown && !report.iconGridMatch };
    }

    public static EnvironmentReport Report(LayoutProfile profile, EnvironmentSnapshot current)
    {
        bool hardware = String.IsNullOrEmpty(profile.hardware) ? profile.topology == current.topology : profile.hardware == current.hardware;
        bool scaleKnown = HasSystemScale(profile);
        bool gridKnown = HasIconGrid(profile);
        bool spacingMatch = !gridKnown || SameIconState(profile.shellState, current.shellState);
        return new EnvironmentReport { hardwareMatch = hardware, systemScaleKnown = scaleKnown, systemScaleMatch = !scaleKnown || DisplayScale.SameScale(profile.monitors, current.monitors), iconGridKnown = gridKnown, iconGridMatch = spacingMatch };
    }

    public static bool HardwareMatches(LayoutProfile profile, EnvironmentSnapshot current)
    {
        return String.IsNullOrEmpty(profile.hardware) ? profile.topology == current.topology : profile.hardware == current.hardware;
    }

    public static bool HasSystemScale(LayoutProfile profile) { return profile.monitors != null && profile.monitors.Count > 0; }
    public static bool HasIconGrid(LayoutProfile profile) { return profile.shellState != null && profile.shellState.version == 1 && profile.shellState.iconSize >= 16; }
    public static bool SameIconState(ShellLayoutState a, ShellLayoutState b)
    {
        return a != null && b != null && a.iconSize == b.iconSize && a.viewMode == b.viewMode;
    }
    public static string Fingerprint(EnvironmentSnapshot current)
    {
        string value = current.hardware + "|" + String.Join("|",current.monitors.OrderBy(x=>x.identity,StringComparer.OrdinalIgnoreCase).Select(x=>x.identity+":"+x.percent)) + "|" + current.shellState.viewMode + ":" + current.shellState.iconSize + ":" + current.shellState.spacingX + ":" + current.shellState.spacingY;
        using (var hash = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-",String.Empty);
    }
}

internal static class TraceLog
{
    public static void Snapshot(string path, string action, string phase, string signature, Dictionary<string, Icon> actual, Dictionary<string, int[]> expected, string outcome)
    {
        if (String.IsNullOrWhiteSpace(path)) return;
        try
        {
            var entry = new Dictionary<string, object>();
            entry["timestamp"] = DateTime.Now.ToString("o");
            entry["action"] = action;
            entry["phase"] = phase;
            entry["topology"] = signature;
            entry["outcome"] = outcome;
            entry["actual"] = actual == null ? null : actual.ToDictionary(pair => pair.Key, pair => new[] { pair.Value.X, pair.Value.Y });
            entry["expected"] = expected;
            Write(path, entry);
        }
        catch { }
    }

    private static void Write(string path, Dictionary<string, object> entry)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full);
        if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        File.AppendAllText(full, new JavaScriptSerializer().Serialize(entry) + Environment.NewLine, new UTF8Encoding(false));
    }
}

internal static class Displays
{
    public static string Signature() { return DisplayScale.HardwareSignature(DisplayScale.Capture()); }
    public static string HardwareSignature() { return Signature(); }
    public static string LegacySignature()
    {
        Screen[] all = Screen.AllScreens; Screen primary = all.FirstOrDefault(s => s.Primary) ?? all[0];
        string value = "P:" + primary.Bounds.Width + "x" + primary.Bounds.Height;
        foreach (Screen s in all.Where(s => !s.Primary).OrderBy(s => s.DeviceName, StringComparer.Ordinal)) value += "|S:" + s.Bounds.Width + "x" + s.Bounds.Height;
        return value + "|N:" + all.Length;
    }
    public static bool WorkArea(out Rectangle result)
    {
        Screen[] all = Screen.AllScreens; if (all.Length == 0) { result = Rectangle.Empty; return false; }
        result = Rectangle.FromLTRB(all.Min(s => s.WorkingArea.Left), all.Min(s => s.WorkingArea.Top), all.Max(s => s.WorkingArea.Right), all.Max(s => s.WorkingArea.Bottom)); return true;
    }
}

internal static class Desktop
{
    const int LVM_FIRST = 0x1000, LVM_GETITEMCOUNT = LVM_FIRST + 4, LVM_GETITEMPOSITION = LVM_FIRST + 16, LVM_SETITEMPOSITION = LVM_FIRST + 15, LVM_GETITEMTEXTW = LVM_FIRST + 115, LVM_GETITEMSPACING = LVM_FIRST + 51, LVM_SETICONSPACING = LVM_FIRST + 53, LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54, LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55, GWL_STYLE = -16;
    const uint LVIF_TEXT = 1, LVS_AUTOARRANGE = 0x0100, LVS_EX_SNAPTOGRID = 0x00080000, PROCESS_VM_OPERATION = 8, PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 4, SMTO_ABORTIFHUNG = 2, SPI_ICONHORIZONTALSPACING = 0x000D, SPI_ICONVERTICALSPACING = 0x0018;
    const uint SPIF_UPDATEINIFILE = 0x0001, SPIF_SENDCHANGE = 0x0002;
    [StructLayout(LayoutKind.Sequential)] struct LVITEM { public uint mask; public int iItem, iSubItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; public int iIndent, iGroupId; public uint cColumns; public IntPtr puColumns; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(IntPtr hWnd, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, out int c, uint d);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, uint p);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, uint s, uint t, uint x);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr a, uint s, uint t);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, uint s, out UIntPtr r);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, uint s, out UIntPtr r);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static IntPtr FindListView()
    {
        IntPtr found = IntPtr.Zero; EnumWindows(delegate(IntPtr w, IntPtr _) { IntPtr d = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null); if (d == IntPtr.Zero) return true; IntPtr l = FindWindowEx(d, IntPtr.Zero, "SysListView32", null); if (l == IntPtr.Zero) return true; found = l; return false; }, IntPtr.Zero);
        if (found != IntPtr.Zero) return found; IntPtr p = FindWindow("Progman", null); IntPtr d2 = p == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(p, IntPtr.Zero, "SHELLDLL_DefView", null); return d2 == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(d2, IntPtr.Zero, "SysListView32", null);
    }
    public static bool WaitUntilReady(out IntPtr list)
    {
        list = IntPtr.Zero;
        string previous = null;
        int stable = 0;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            IntPtr candidate = FindListView();
            IntPtr result;
            int count = candidate == IntPtr.Zero || !Send(candidate, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out result) ? -1 : result.ToInt32();
            string state = candidate.ToInt64().ToString("X") + ":" + count + ":" + Displays.Signature();
            if (count > 0 && state == previous) stable++; else stable = 0;
            if (stable >= 3) { list = candidate; return true; }
            previous = state;
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }
    public static bool AutoArrange(IntPtr list) { return (GetWindowLongPtr(list, GWL_STYLE).ToInt64() & LVS_AUTOARRANGE) != 0; }
    public static string ScreenForItemPosition(IntPtr list, int x, int y)
    {
        uint dpi = GetDpiForWindow(list);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        POINT point = new POINT { X = (int)Math.Round(x / scale), Y = (int)Math.Round(y / scale) };
        return ClientToScreen(list, ref point) ? Screen.FromPoint(new Point(point.X, point.Y)).DeviceName : null;
    }
    public static bool IconSpacing(IntPtr list, out int x, out int y)
    {
        x = y = 0;
        if (list != IntPtr.Zero)
        {
            IntPtr packed;
            if (Send(list, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero, out packed))
            {
                uint value = unchecked((uint)packed.ToInt64());
                x = (int)(value & 0xffff); y = (int)((value >> 16) & 0xffff);
                if (x > 0 && y > 0) return true;
            }
        }
        return SystemParametersInfo(SPI_ICONHORIZONTALSPACING, 0, out x, 0) && SystemParametersInfo(SPI_ICONVERTICALSPACING, 0, out y, 0) && x > 0 && y > 0;
    }
    public static void Update(IntPtr list) { UpdateWindow(list); }
    static bool Send(IntPtr h, int m, IntPtr w, IntPtr l, out IntPtr r) { return SendMessageTimeout(h, m, w, l, SMTO_ABORTIFHUNG, 5000, out r) != IntPtr.Zero; }
    static IntPtr Process(IntPtr list) { uint id; GetWindowThreadProcessId(list, out id); return id == 0 ? IntPtr.Zero : OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, id); }
    public static Dictionary<string, Icon> ReadIcons(IntPtr list)
    {
        IntPtr result; if (!Send(list, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out result) || result.ToInt32() <= 0) throw new InvalidOperationException("无法读取桌面图标数量。");
        var map = new Dictionary<string, Icon>(); var counts = new Dictionary<string, int>();
        for (int i = 0; i < result.ToInt32(); i++) { string name = Text(list, i); int x, y; if (!Position(list, i, out x, out y)) throw new InvalidOperationException("无法读取图标坐标：" + name); int n = counts.ContainsKey(name) ? counts[name] + 1 : 1; counts[name] = n; string key = n == 1 ? name : name + " [" + n + "]"; map.Add(key, new Icon { Key = key, Index = i, X = x, Y = y }); }
        return map;
    }
    static string Text(IntPtr list, int index)
    {
        IntPtr p = Process(list); if (p == IntPtr.Zero) throw new InvalidOperationException("无法打开 Explorer 进程。"); IntPtr item = IntPtr.Zero, text = IntPtr.Zero;
        try { item = VirtualAllocEx(p, IntPtr.Zero, (uint)Marshal.SizeOf(typeof(LVITEM)), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); text = VirtualAllocEx(p, IntPtr.Zero, 2048, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); if (item == IntPtr.Zero || text == IntPtr.Zero) throw new InvalidOperationException("无法分配 Explorer 进程内存。"); LVITEM v = new LVITEM { mask = LVIF_TEXT, iItem = index, pszText = text, cchTextMax = 1023 }; int size = Marshal.SizeOf(v); IntPtr local = Marshal.AllocHGlobal(size); try { Marshal.StructureToPtr(v, local, false); byte[] raw = new byte[size]; Marshal.Copy(local, raw, 0, size); UIntPtr wrote; if (!WriteProcessMemory(p, item, raw, (uint)raw.Length, out wrote) || wrote.ToUInt64() != (ulong)raw.Length) throw new InvalidOperationException("无法写入 Explorer 项目缓冲区。"); } finally { Marshal.FreeHGlobal(local); } IntPtr r; if (!Send(list, LVM_GETITEMTEXTW, (IntPtr)index, item, out r)) throw new InvalidOperationException("Explorer 未响应图标名称请求。"); byte[] bytes = new byte[2048]; UIntPtr read; if (!ReadProcessMemory(p, text, bytes, (uint)bytes.Length, out read)) throw new InvalidOperationException("无法读取 Explorer 图标名称。"); string name = Encoding.Unicode.GetString(bytes); int zero = name.IndexOf('\0'); name = (zero >= 0 ? name.Substring(0, zero) : name).Trim(); if (name.Length == 0) throw new InvalidOperationException("读取到空图标名称。"); return name; }
        finally { if (item != IntPtr.Zero) VirtualFreeEx(p, item, 0, MEM_RELEASE); if (text != IntPtr.Zero) VirtualFreeEx(p, text, 0, MEM_RELEASE); CloseHandle(p); }
    }
    static bool Position(IntPtr list, int index, out int x, out int y)
    {
        x = y = 0; IntPtr p = Process(list); if (p == IntPtr.Zero) return false; IntPtr remote = IntPtr.Zero;
        try { remote = VirtualAllocEx(p, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); IntPtr r; if (remote == IntPtr.Zero || !Send(list, LVM_GETITEMPOSITION, (IntPtr)index, remote, out r) || r == IntPtr.Zero) return false; byte[] b = new byte[8]; UIntPtr read; if (!ReadProcessMemory(p, remote, b, 8, out read) || read.ToUInt64() != 8) return false; x = BitConverter.ToInt32(b, 0); y = BitConverter.ToInt32(b, 4); return true; } finally { if (remote != IntPtr.Zero) VirtualFreeEx(p, remote, 0, MEM_RELEASE); CloseHandle(p); }
    }
}
