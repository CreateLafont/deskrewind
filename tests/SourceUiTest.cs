#if POWERSHELL_TEST
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskRewind.Wpf
{
    public static class SourceUiTest
    {
        public static void Run(string output)
        {
            var originalScales = DisplayScale.Capture();
            ShellLayoutState originalDesktop;
            using (var desktop = new ShellDesktop()) originalDesktop = desktop.Capture();
            File.WriteAllText(Path.Combine(output,"ui-scale-baseline.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { monitors=originalScales,desktop=originalDesktop }));
            var app = new Application(); var window = new KeeperWindow();
            var mask = (Border)typeof(KeeperWindow).GetField("_dialogMask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            int stage = 0, samples = 0; bool violation = false; Exception error = null;
            var started = DateTime.UtcNow;
            timer.Tick += delegate
            {
                try
                {
                    if ((DateTime.UtcNow - started).TotalSeconds > 45) throw new Exception("UI test timed out.");
                    if (stage == 0)
                    {
                        if (window.Title != "DeskRewind") throw new Exception("Source window title was not renamed.");
                        Render(window, Path.Combine(output, "deskrewind-main.png"));
                        stage = 1;
                        window.Dispatcher.BeginInvoke(new Action(delegate { typeof(KeeperWindow).GetMethod("CreateProfile", flags).Invoke(window, null); }));
                        return;
                    }
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w != window);
                    if (stage == 1 && dialog is NameDialog)
                    {
                        ((TextBox)typeof(NameDialog).GetField("_input", flags).GetValue(dialog)).Text = "回归测试";
                        typeof(NameDialog).GetField("_accepted", flags).SetValue(dialog, true);
                        stage = 2; dialog.Close(); return;
                    }
                    if (stage == 2)
                    {
                        samples++; if (mask.Visibility != Visibility.Visible) violation = true;
                        if (dialog is NoticeDialog) throw new Exception("Save returned an error dialog.");
                        if (dialog is SuccessDialog)
                        {
                            Render(dialog, Path.Combine(output, "save-success.png"));
                            stage = 3; dialog.Close(); return;
                        }
                    }
                    if (stage == 3)
                    {
                        if (mask.Visibility != Visibility.Collapsed) throw new Exception("Mask remained after final dialog closed.");
                        if (violation || samples == 0) throw new Exception("Mask flickered between dialogs.");
                        Console.WriteLine("PASS actual save UI: continuous mask, final release, samples=" + samples);
                        stage = 4;
                        var promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                        promptTimer.Tick += delegate
                        {
                            var prompt = app.Windows.Cast<Window>().FirstOrDefault(w => w is ConfirmDialog);
                            if (prompt == null) return;
                            promptTimer.Stop(); Render(prompt, Path.Combine(output, "icon-size-confirm.png")); prompt.Close();
                        };
                        promptTimer.Start();
                        ConfirmDialog.Ask(window, "", "应用布局", "当前图标大小与保存时不同，是否恢复保存时的图标大小并还原图标排列？");
                        stage = 5;
                        return;
                    }
                    if (stage == 5)
                    {
                        stage = 6;
                        var scales = DisplayScale.Capture();
                        scales[0].percent = scales[0].percent > 100 ? scales[0].percent - 25 : 125;
                        DisplayScale.Apply(scales,false);
                        using (var desktop = new ShellDesktop()) desktop.ApplySize(originalDesktop.viewMode,originalDesktop.iconSize+4);
                        var profiles = (System.Collections.Generic.List<LayoutProfile>)typeof(KeeperWindow).GetField("_profiles",flags).GetValue(window);
                        var profile = profiles.First();
                        window.Dispatcher.BeginInvoke(new Action(delegate { typeof(KeeperWindow).GetMethod("Restore",flags,null,new[] { typeof(LayoutProfile) },null).Invoke(window,new object[] {profile}); }));
                        return;
                    }
                    if (stage == 6 && dialog is ConfirmDialog)
                    {
                        Render(dialog,Path.Combine(output,"combined-confirm.png"));
                        typeof(ConfirmDialog).GetField("_accepted",flags).SetValue(dialog,true);
                        stage = 7; dialog.Close(); return;
                    }
                    if (stage == 7)
                    {
                        if (mask.Visibility != Visibility.Visible) throw new Exception("Restore mask flickered.");
                        if (dialog is NoticeDialog) { Render(dialog,Path.Combine(output,"restore-error.png")); throw new Exception("UI restore returned an error."); }
                        if (dialog is SuccessDialog)
                        {
                            Render(dialog,Path.Combine(output,"combined-success.png"));
                            stage = 8; dialog.Close(); return;
                        }
                    }
                    if (stage == 8)
                    {
                        if (mask.Visibility != Visibility.Collapsed) throw new Exception("Restore mask remained.");
                        using (var desktop = new ShellDesktop()) if (!ShellDesktop.Compare(originalDesktop,desktop.Capture()).Complete) throw new Exception("UI did not restore exact desktop.");
                        if (!DisplayScale.SameScale(originalScales,DisplayScale.Capture())) throw new Exception("UI did not restore scales.");
                        Console.WriteLine("PASS actual combined restore UI: scales, icon size, all " + originalDesktop.items.Count + " coordinates and mask lifetime.");
                        timer.Stop(); window.Close();
                    }
                }
                catch (Exception ex) { error = ex; timer.Stop(); foreach (var w in app.Windows.Cast<Window>().Reverse().ToArray()) w.Close(); }
            };
            window.Loaded += delegate { timer.Start(); };
            try { app.Run(window); }
            finally
            {
                DisplayScale.Apply(originalScales,true);
                using (var desktop = new ShellDesktop()) if (!desktop.Restore(originalDesktop).Complete) throw new Exception("UI test baseline recovery failed.");
            }
            if (error != null) throw error;
        }
        private static void Render(Window window, string file)
        {
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
    }
}
#endif
