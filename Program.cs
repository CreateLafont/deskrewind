using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;

[assembly: AssemblyTitle("DeskRewind")]
[assembly: AssemblyProduct("DeskRewind")]
[assembly: AssemblyDescription("Save and restore Windows desktop icon layouts.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace DeskRewind.Wpf
{
    internal static class Program
    {
        private static Mutex SingleInstanceMutex;

        [STAThread]
        private static void Main()
        {
            bool created;
            SingleInstanceMutex = new Mutex(true, "DeskRewind.SingleInstance", out created);
            if (!created) return;
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
            {
                var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (!String.IsNullOrWhiteSpace(systemRoot))
                    Environment.SetEnvironmentVariable("WINDIR", systemRoot, EnvironmentVariableTarget.Process);
            }

            RuntimeAssets.Initialize();
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.Run(new KeeperWindow());
        }
    }

    internal sealed class LayoutProfile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string savedAt { get; set; }
        public int iconCount { get; set; }
        public bool hardwareMatch { get; set; }
        public bool systemScaleKnown { get; set; }
        public bool systemScaleMatch { get; set; }
        public bool iconGridKnown { get; set; }
        public bool iconGridMatch { get; set; }
        public string environmentToken { get; set; }
    }

    internal static class UiTypography
    {
        public static readonly FontFamily Dynamic = new FontFamily("Microsoft YaHei UI");

        public static FontFamily Static(bool bold)
        {
            var fontFile = RuntimeAssets.AssetPath("Fonts", bold ? "OPPOSans-B-UI.ttf" : "OPPOSans-R-UI.ttf");
            return new FontFamily(new Uri(fontFile), bold ? "./#DeskRewind OPPOSans B" : "./#DeskRewind OPPOSans R");
        }
    }

    // Runtime dependencies are embedded in the EXE.  They are materialised into
    // a versioned LocalAppData cache because WPF font and image decoders require
    // file-backed URIs; the distributed application remains one file.
    internal static class RuntimeAssets
    {
        private const string ResourcePrefix = "DeskRewind.";
        private const string RuntimeVersion = "shell-view-1";
        private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskRewind");
        private static readonly string RuntimeRoot = Path.Combine(DataRoot, "runtime", RuntimeVersion);
        private static bool _initialized;

        internal static string LayoutPath
        {
            get
            {
#if POWERSHELL_TEST
                return Environment.GetEnvironmentVariable("DESKREWIND_TEST_STORE");
#else
                Initialize(); return Path.Combine(DataRoot, "layouts.profiles.json");
#endif
            }
        }
        internal static string AssetPath(params string[] parts)
        {
#if POWERSHELL_TEST
            return Path.Combine(new[] { Environment.GetEnvironmentVariable("DESKREWIND_TEST_ROOT"), "Assets" }.Concat(parts).ToArray());
#else
            Initialize(); return Path.Combine(new[] { RuntimeRoot, "Assets" }.Concat(parts).ToArray());
#endif
        }

        internal static void Initialize()
        {
#if POWERSHELL_TEST
            return;
#else
            if (_initialized) return;
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(RuntimeRoot);
            foreach (var asset in EmbeddedAssets)
            {
                var relative = Path.Combine(asset);
                var target = Path.Combine(RuntimeRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!File.Exists(target))
                {
                    var resource = ResourcePrefix + relative.Replace('\\', '.').Replace('/', '.');
                    using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    {
                        if (input == null) throw new InvalidOperationException("缺少内嵌资源：" + resource);
                        using (var output = File.Create(target)) input.CopyTo(output);
                    }
                }
            }

            // Layouts stored next to the application are migrated once into
            // LocalAppData, which then owns all future layout data.
            var portableLayout = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts.profiles.json");
            var localLayout = Path.Combine(DataRoot, "layouts.profiles.json");
            if (File.Exists(portableLayout) && (!File.Exists(localLayout) || IsEmptyLayout(localLayout)))
                File.Copy(portableLayout, localLayout, true);
            _initialized = true;
#endif
        }

        private static bool IsEmptyLayout(string path)
        {
            var compact = File.ReadAllText(path).Replace(" ", String.Empty).Replace("\r", String.Empty).Replace("\n", String.Empty).Replace("\t", String.Empty);
            return compact.Contains("\"profiles\":[]");
        }

        private static readonly string[][] EmbeddedAssets = new[]
        {
            new[] { "Assets", "Fonts", "OPPOSans-B-UI.ttf" }, new[] { "Assets", "Fonts", "OPPOSans-R-UI.ttf" },
            new[] { "Assets", "Icons", "add.svg" }, new[] { "Assets", "Icons", "arrange.svg" }, new[] { "Assets", "Icons", "close.svg" },
            new[] { "Assets", "Icons", "delete.svg" }, new[] { "Assets", "Icons", "full.svg" }, new[] { "Assets", "Icons", "min.svg" },
            new[] { "Assets", "Icons", "monitor.svg" }, new[] { "Assets", "Icons", "overwrite.svg" }, new[] { "Assets", "Icons", "refresh.svg" },
            new[] { "Assets", "Icons", "rename.svg" }, new[] { "Assets", "Icons", "restore.svg" },
            new[] { "Assets", "TitleBar", "DeskRewind.ico" }, new[] { "Assets", "TitleBar", "IconTitle.png" }
        };
    }
}
