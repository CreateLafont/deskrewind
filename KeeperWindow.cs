using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FormsScreen = System.Windows.Forms.Screen;

namespace DeskRewind.Wpf
{
    // The window uses one fixed design canvas and applies display scale exactly
    // once through the outer Viewbox.
    internal sealed class KeeperWindow : Window
    {
        private readonly string _root = RuntimeAssets.AssetPath();
        private readonly List<LayoutProfile> _profiles = new List<LayoutProfile>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly TextBlock _mode = Ui.Text("", 16, false);
        private readonly TextBlock _resolution = Ui.Text("", 16, false);
        private readonly MonitorPreview _monitors = new MonitorPreview();
        private Border _dialogMask;
        private LayoutProfile _selected;
        private Button _newLayout;
        private HwndSource _source;
        private bool _hooked;
        private int _maskDepth;
        private bool _operationBusy;

        public KeeperWindow()
        {
            // Follow the Windows app theme (light/dark) live.
            Ui.ApplyTheme(Ui.DetectSystemLight());
            SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
            Title = "DeskRewind";
            // The design canvas is 960x1181 at the 75% application scale.
            // 20px design-space shadow allowance around the 960x1181 shell.
            Width = 750; Height = 916; MinWidth = 750; MinHeight = 916; MaxWidth = 750; MaxHeight = 916;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
            Icon = LoadWindowIcon(Path.Combine(_root, "TitleBar", "DeskRewind.ico"));
            // The 1px inside stroke must survive the 75% Viewbox scale, so it is
            // layered outside the Viewbox at the window's real pixel size: 720x886
            // (960x1181 design * 0.75) at a 15px margin (20px shadow * 0.75).
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas() };
            var stroke = new SmoothRoundedStroke { Margin = new Thickness(15), Stroke = Ui.Stroke, StrokeThickness = 1, Radius = 18, Smoothing = 0.6 };
            Content = new Grid { Children = { viewbox, stroke } };
            Loaded += delegate { Refresh(); HookWindow(); };
            Closing += delegate(object sender, CancelEventArgs e) { if (_operationBusy) e.Cancel = true; };
            Closed += delegate { if (_source != null) _source.RemoveHook(WindowProc); SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged; };
        }

        // Borderless windows still need the system title-bar menu (right-click on
        // the title area) and a live taskbar thumbnail (Aero Peek). Both require
        // hooking the Win32 message loop; WPF's default borderless path offers
        // neither.
        private void HookWindow()
        {
            if (_hooked) return;
            _source = (HwndSource)PresentationSource.FromVisual(this);
            if (_source == null) return;
            _source.AddHook(WindowProc);
            _hooked = true;
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == Native.SC_CLOSE)
            {
                Close(); handled = true;
            }
            else if (msg == Native.WM_PRINTCLIENT)
            {
                // AllowsTransparency windows render black in the taskbar
                // thumbnail by default; repaint the WPF tree into the DWM
                // requested HDC so Aero Peek shows the real surface.
                RenderInto(wParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void RenderInto(IntPtr hdc)
        {
            if (hdc == IntPtr.Zero || Content == null) return;
            try
            {
                var width = (int)Math.Ceiling(ActualWidth);
                var height = (int)Math.Ceiling(ActualHeight);
                if (width <= 0 || height <= 0) return;
                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render((Visual)Content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    ms.Position = 0;
                    using (var bitmap = new System.Drawing.Bitmap(ms))
                    using (var graphics = System.Drawing.Graphics.FromHdc(hdc))
                    {
                        graphics.DrawImage(bitmap, 0, 0, width, height);
                    }
                }
            }
            catch
            {
                // Thumbnail painting is best-effort; never break the app on it.
            }
        }

        // Right-click anywhere on the custom title bar opens the system menu
        // (move / minimise / close; resize is disabled for the fixed window).
        // System theme flips repaint every shared token in place on the UI thread.
        private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate { Ui.ApplyTheme(Ui.DetectSystemLight()); }));
        }

        private void ShowSystemMenu(double screenX, double screenY)
        {
            var hwnd = _source == null ? IntPtr.Zero : _source.Handle;
            if (hwnd == IntPtr.Zero) return;
            var menu = Native.GetSystemMenu(hwnd, false);
            if (menu == IntPtr.Zero) return;
            Native.EnableMenuItem(menu, Native.SC_SIZE, Native.MF_GRAYED);
            Native.EnableMenuItem(menu, Native.SC_MAXIMIZE, Native.MF_GRAYED);
            Native.EnableMenuItem(menu, Native.SC_RESTORE, Native.MF_GRAYED);
            var cmd = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, (int)screenX, (int)screenY, 0, hwnd, IntPtr.Zero);
            if (cmd == 0) return;
            Native.SendMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
        }

        // BitmapImage would select the first ICO frame (16px), which Windows enlarges
        // for the taskbar, so the 48px frame is chosen explicitly.
        private static ImageSource LoadWindowIcon(string iconPath)
        {
            var decoder = new IconBitmapDecoder(new Uri(iconPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.FirstOrDefault(f => f.PixelWidth == 48) ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }

        private UIElement BuildCanvas()
        {
            // The shadow must live on an unclipped host so the blur is not cut off
            // at the rounded corners.
            var shell = new UiSurface(24, Ui.Surface) { Width = 960, Height = 1181, ClipToBounds = false, Effect = Ui.Shadow };
            var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1109) });
            root.Children.Add(BuildTitle());
            var work = new Grid { Margin = new Thickness(36, 36, 36, 56) };
            work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(384) });
            work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(545) });
            work.Children.Add(BuildDisplayHeader());
            var display = BuildDisplay(); Grid.SetRow(display, 2); work.Children.Add(display);
            var arrange = BuildArrange(); Grid.SetRow(arrange, 4); work.Children.Add(arrange);
            Grid.SetRow(work, 1); root.Children.Add(work);
            // The mask begins beneath the title bar, so only the workspace's
            // bottom corners need rounding.  This keeps the application's 24px
            // corners visible instead of covering them with a rectangular layer.
            // The workspace overlay uses 50% black in the dark theme.
            _dialogMask = new Border { Background = Ui.Mask, CornerRadius = new CornerRadius(0, 0, 24, 24), ClipToBounds = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(_dialogMask, 1); root.Children.Add(_dialogMask);
            shell.Child = root;
            var outer = new Canvas { Width = 1000, Height = 1221, ClipToBounds = false };
            Canvas.SetLeft(shell, 20); Canvas.SetTop(shell, 20); outer.Children.Add(shell); return outer;
        }

        private UIElement BuildTitle()
        {
            var title = new SmoothTopSurface { Height = 72, Fill = Ui.Shell, Radius = 24, Smoothing = 0.6 };
            // A Grid with a null background has no hit-test surface in empty areas.
            // Transparent stays hit-testable while remaining visually identical.
            var grid = new Grid { Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            grid.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { var p = PointToScreen(e.GetPosition(this)); ShowSystemMenu(p.X, p.Y); };

            var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(36, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(TitleImage("IconTitle.svg", 32, 32));
            // The bundled B face is already OPPOSans-B; do not synthesize FontWeight.Bold.
            identity.Children.Add(new TextBlock { Text = "DeskRewind", FontFamily = UiTypography.Static(true), FontSize = 24, FontWeight = FontWeights.Normal, Foreground = Ui.TextPrimary, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(identity);

            var min = new TitleBarStateButton(IconAsset("min.svg"), false) { Width = 84, Height = 72 }; min.Click += delegate { WindowState = WindowState.Minimized; }; Grid.SetColumn(min, 1); grid.Children.Add(min);
            var close = new TitleBarStateButton(IconAsset("close.svg"), true) { Width = 84, Height = 72 }; close.Click += delegate { Close(); }; Grid.SetColumn(close, 2); grid.Children.Add(close);
            title.Child = grid;
            return title;
        }

        private string TitleAsset(string name) { return Path.Combine(_root, "TitleBar", name); }
        private FrameworkElement TitleImage(string name, double width, double height) { return Ui.SvgImage(TitleAsset(name), width, height); }

        private UIElement BuildDisplayHeader()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Ui.SvgImage(IconAsset("monitor.svg"), 24, 24));
            left.Children.Add(Ui.Text("当前屏显模式", 24, true, new Thickness(8, 0, 0, 0)));
            grid.Children.Add(left);
            var refresh = new TextIconButton("刷新", IconAsset("refresh.svg"), 76, 33, TextIconButtonKind.Refresh) { VerticalAlignment = VerticalAlignment.Center };
            refresh.Click += delegate { Refresh(); };
            Grid.SetColumn(refresh, 1);
            grid.Children.Add(refresh);
            return grid;
        }

        private string IconAsset(string name) { return Path.Combine(_root, "Icons", name); }

        private UIElement BuildDisplay()
        {
            var card = new UiSurface(24, Ui.Shell);
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(320) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
            // The monitor preview occupies the full 888 x 320 card area.
            _monitors.Margin = new Thickness(0);
            grid.Children.Add(_monitors);

            // The footer places an 840px divider above a padded status row.
            var footerFrame = new Grid();
            footerFrame.Children.Add(new Border { Height = 1, Background = Ui.Divider, Margin = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Top });
            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _mode.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(_mode);
            _resolution.VerticalAlignment = VerticalAlignment.Center;
            _resolution.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_resolution, 2);
            footer.Children.Add(_resolution);
            var textCombo = new Border { Height = 21, Margin = new Thickness(24, 20, 24, 0), Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, Child = footer };
            footerFrame.Children.Add(textCombo);
            Grid.SetRow(footerFrame, 1);
            grid.Children.Add(footerFrame);
            card.Child = grid;
            return card;
        }

        private UIElement BuildArrange()
        {
            var module = new Grid { Height = 545 };
            module.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            module.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            module.RowDefinitions.Add(new RowDefinition { Height = new GridLength(457) });
            module.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            module.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });

            var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(Ui.SvgImage(IconAsset("arrange.svg"), 24, 24));
            heading.Children.Add(Ui.Text("图标布局", 24, true, new Thickness(8, 0, 0, 0)));
            module.Children.Add(heading);

            var form = new UiSurface(24, Ui.Shell);
            var canvas = new Canvas { Width = 888, Height = 457 };
            var header = BuildLayoutHeader(); Canvas.SetLeft(header, 24); Canvas.SetTop(header, 24); canvas.Children.Add(header);
            var divider = new Border { Width = 840, Height = 1, Background = Ui.Stroke }; Canvas.SetLeft(divider, 24); Canvas.SetTop(divider, 92); canvas.Children.Add(divider);
            _rows.Width = 840; _rows.Height = 320;
            Canvas.SetLeft(_rows, 24); Canvas.SetTop(_rows, 113); canvas.Children.Add(_rows);
            form.Child = canvas; Grid.SetRow(form, 2); module.Children.Add(form);

            var tip = Ui.Text("提示：保存/恢复前请先关闭桌面“自动排列图标”（右键 - 查看）。", 14, false);
            tip.Foreground = Ui.TextPrimary; tip.HorizontalAlignment = HorizontalAlignment.Center; tip.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(tip, 4); module.Children.Add(tip);
            return module;
        }

        private Grid BuildLayoutHeader()
        {
            var header = LayoutRowGrid(); header.Height = 48;
            var name = new Border { Padding = new Thickness(16, 0, 16, 0), Child = Ui.Text("名称", 16, false), VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(name);
            AddCell(header, Ui.Text("修改时间", 16, false), 1);
            AddCell(header, Ui.Text("图标数量", 16, false), 2);
            _newLayout = new TextIconButton("新建布局", IconAsset("add.svg"), 124, 48, TextIconButtonKind.NewLayout);
            _newLayout.Click += delegate { CreateProfile(); };
            _newLayout.HorizontalAlignment = HorizontalAlignment.Right; _newLayout.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_newLayout, 3); header.Children.Add(_newLayout);
            return header;
        }

        private void Refresh()
        {
            try { _monitors.SetScreens(FormsScreen.AllScreens); _mode.Text = Describe(FormsScreen.AllScreens); _resolution.Text = Resolution(FormsScreen.AllScreens); _profiles.Clear(); _profiles.AddRange(new JavaScriptSerializer().Deserialize<List<LayoutProfile>>(Run("list --json")) ?? new List<LayoutProfile>()); if (_selected != null && !_profiles.Any(p => p.id == _selected.id)) _selected = null; RenderRows(); }
            catch { _mode.Text = "当前屏幕状态不可用"; _resolution.Text = "请点击刷新重试"; }
        }

        private void RenderRows()
        {
            _rows.Children.Clear(); var shown = _profiles.Take(5).ToArray(); for (var i = 0; i < shown.Length; i++) _rows.Children.Add(ProfileRow(shown[i], i < shown.Length - 1));
            if (_newLayout != null) { _newLayout.IsEnabled = _profiles.Count(p => p.hardwareMatch) < 5; _newLayout.Opacity = 1; }
        }

        private UIElement ProfileRow(LayoutProfile p, bool hasNext)
        {
            var selected = p == _selected;
            var surface = new Border { Width = 840, Height = 48, CornerRadius = new CornerRadius(16), Background = selected ? Ui.PrimaryBase : Brushes.Transparent };
            var row = LayoutRowGrid();
            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var profileName = Ui.Dynamic(p.name, 16); profileName.Foreground = selected ? Ui.Dark : Ui.TextPrimary; name.Children.Add(profileName);
            if (selected)
            {
                var rename = new TextIconButton("重命名", IconAsset("rename.svg"), 92, 33, TextIconButtonKind.RowAction, Ui.Dark) { Margin = new Thickness(8, 0, 0, 0) };
                rename.Click += delegate { RenameProfile(p); };
                name.Children.Add(rename);
            }
            row.Children.Add(name);
            var savedAt = Ui.Text(DateTimeOffset.Parse(p.savedAt).ToString("yyyy-MM-dd HH:mm:ss"), 16, false); savedAt.Foreground = selected ? Ui.Dark : Ui.TextPrimary;
            AddCell(row, savedAt, 1);
            var iconCount = Ui.Text(p.iconCount.ToString(), 16, false); iconCount.Foreground = selected ? Ui.Dark : Ui.TextPrimary;
            AddCell(row, iconCount, 2);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(RowAction("overwrite", delegate { Overwrite(p); }, new Thickness(0, 0, 16, 0), selected ? Ui.Dark : null));
            actions.Children.Add(RowAction("apply", delegate { Restore(p); }, new Thickness(0, 0, 16, 0), selected ? Ui.Dark : null));
            actions.Children.Add(RowAction("delete", delegate { Delete(p); }, new Thickness(0), selected ? Ui.Dark : null));
            Grid.SetColumn(actions, 3); row.Children.Add(actions);
            surface.Child = row;
            surface.MouseLeftButtonDown += delegate { _selected = p; RenderRows(); };
            return new Border { Height = 48, Margin = new Thickness(0, 0, 0, hasNext ? 20 : 0), Child = surface };
        }

        private TextIconButton RowAction(string actionName, Action action, Thickness margin, Brush foreground = null)
        {
            var label = actionName == "overwrite" ? "覆盖" : actionName == "apply" ? "应用" : "删除";
            var icon = actionName == "overwrite" ? "overwrite.svg" : actionName == "apply" ? "restore.svg" : "delete.svg";
            var button = new TextIconButton(label, IconAsset(icon), 76, 33, TextIconButtonKind.RowAction, foreground) { Margin = margin };
            button.Click += delegate { action(); }; return button;
        }

        private static Grid LayoutRowGrid() { var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); return g; }
        private static void AddCell(Grid grid, UIElement value, int column) { value.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); value.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); Grid.SetColumn(value, column); grid.Children.Add(value); }
        private void CreateProfile()
        {
            if (_operationBusy || _profiles.Count(p => p.hardwareMatch) >= 5) return;
            var defaultName = "布局 " + DateTime.Now.ToString("MMddHHmm", CultureInfo.InvariantCulture);
            BeginMask();
            string name;
            try { name = NameDialog.Ask(this, "新建布局", "请输入布局名称", defaultName); }
            catch { EndMask(); throw; }
            if (String.IsNullOrWhiteSpace(name)) { EndMask(); return; }
            _operationBusy = true;
            ExecuteThenRefresh(delegate { Run("save --name " + Quote(name)); }, "保存成功", "已保存布局“" + name + "”");
        }

        private void RenameProfile(LayoutProfile profile)
        {
            if (_operationBusy) return;
            BeginMask();
            string name;
            try { name = NameDialog.Ask(this, "重命名布局", "请输入新的布局名称", profile.name); }
            catch { EndMask(); throw; }
            if (String.IsNullOrWhiteSpace(name) || name == profile.name) { EndMask(); return; }
            _operationBusy = true;
            ExecuteThenRefresh(delegate { Run("rename --profile " + Quote(profile.id) + " --name " + Quote(name)); }, "重命名成功", "已重命名布局“" + name + "”");
        }

        private void Overwrite() { if (!_operationBusy && _selected != null) Overwrite(_selected); }
        private void Restore() { if (!_operationBusy && _selected != null) Restore(_selected); }
        private void Overwrite(LayoutProfile profile)
        {
            if (_operationBusy) return;
            var message = "确定用当前桌面排列覆盖“" + profile.name + "”吗？\n原布局将被替换，无法撤销。";
            BeginMask();
            bool accepted;
            try { accepted = ConfirmDialog.Ask(this, "覆盖布局", message); }
            catch { EndMask(); throw; }
            if (!accepted) { EndMask(); return; }
            _operationBusy = true;
            ExecuteThenRefresh(delegate { Run("overwrite --profile " + Quote(profile.id)); }, "覆盖成功", "已覆盖布局“" + profile.name + "”");
        }

        private void Restore(LayoutProfile profile)
        {
            if (_operationBusy) return;
            if (!RefreshEnvironment(profile)) return;
            var message = RestoreMessage(profile);
            BeginMask();
            bool accepted;
            try { accepted = ConfirmDialog.Ask(this, "应用布局", message); }
            catch { EndMask(); throw; }
            if (!accepted) { EndMask(); return; }
            _operationBusy = true;
            var forceOption = HasEnvironmentMismatch(profile) ? " --force" : String.Empty;
            var bestEffort = !profile.hardwareMatch;
            ExecuteThenRefresh(delegate { Run("restore --profile " + Quote(profile.id) + forceOption + " --expected " + Quote(profile.environmentToken)); }, bestEffort ? "已尝试恢复" : "应用成功", bestEffort ? "已尝试恢复，请检查图标排列" : "已将“" + profile.name + "”应用到桌面");
        }

        private bool RefreshEnvironment(LayoutProfile profile)
        {
            try
            {
                var profiles = new JavaScriptSerializer().Deserialize<List<LayoutProfile>>(Run("list --json"));
                var fresh = profiles == null ? null : profiles.FirstOrDefault(p => p.id == profile.id);
                if (fresh == null) throw new InvalidOperationException("未找到当前布局环境。");
                profile.hardwareMatch = fresh.hardwareMatch;
                profile.systemScaleMatch = fresh.systemScaleMatch;
                profile.iconGridMatch = fresh.iconGridMatch;
                profile.environmentToken = fresh.environmentToken;
                return true;
            }
            catch
            {
                WithWorkspaceMask(delegate { NoticeDialog.Show(this, "无法读取当前桌面状态，请稍候再试。"); return true; });
                return false;
            }
        }

        private void Delete(LayoutProfile profile)
        {
            if (_operationBusy) return;
            var message = "确定删除“" + profile.name + "”吗？\n该布局记录将被永久删除，无法撤销。";
            BeginMask();
            bool accepted;
            try { accepted = ConfirmDialog.Ask(this, "删除布局", message); }
            catch { EndMask(); throw; }
            if (!accepted) { EndMask(); return; }
            _operationBusy = true;
            ExecuteThenRefresh(delegate { Run("delete --profile " + Quote(profile.id)); }, "删除成功", "已删除布局“" + profile.name + "”");
        }

        private static string RestoreMessage(LayoutProfile profile)
        {
            if (!profile.hardwareMatch) return "当前显示器硬件配置与保存时不同，无法完全恢复保存时的图标排列，可能产生更严重的错位，是否继续？";
            bool scale = !profile.systemScaleMatch;
            bool grid = !profile.iconGridMatch;
            if (scale && grid) return "当前系统缩放、图标大小与保存时不同，是否恢复保存时的系统缩放、图标大小和图标排列？";
            if (scale) return "当前系统缩放与保存时不同，是否恢复保存时的系统缩放并还原图标排列？";
            if (grid) return "当前图标大小与保存时不同，是否恢复保存时的图标大小并还原图标排列？";
            return "确定将“" + profile.name + "”应用到桌面吗？\n当前桌面图标将按此布局重新排列。";
        }

        private static bool HasEnvironmentMismatch(LayoutProfile profile)
        {
            return !profile.hardwareMatch || !profile.systemScaleMatch || !profile.iconGridMatch;
        }

        // Runs the native command off the UI thread so the dialog mask hides
        // immediately instead of waiting for the restore/overwrite retry loop,
        // then refreshes and reports success back on the UI thread.
        private void ExecuteThenRefresh(Action command, string successTitle, string successMessage)
        {
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        command();
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try { Refresh(); SuccessDialog.Show(this, successTitle, successMessage); }
                            finally { _operationBusy = false; EndMask(); }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try { NoticeDialog.Show(this, ex.Message); }
                            finally { _operationBusy = false; EndMask(); }
                        }));
                    }
                });
            }
            catch
            {
                _operationBusy = false;
                EndMask();
                throw;
            }
        }

        private void BeginMask()
        {
            _maskDepth++;
            _dialogMask.Visibility = Visibility.Visible;
        }

        private void EndMask()
        {
            if (_maskDepth <= 0) return;
            _maskDepth--;
            if (_maskDepth == 0) _dialogMask.Visibility = Visibility.Collapsed;
        }

        private T WithWorkspaceMask<T>(Func<T> showDialog)
        {
            BeginMask();
            try { return showDialog(); }
            finally { EndMask(); }
        }

        private static string Quote(string value) { return "\"" + (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        private static readonly object NativeEngineLock = new object();
        private string Run(string arguments)
        {
            lock (NativeEngineLock)
            {
                var previousOut = Console.Out; var previousError = Console.Error;
                using (var output = new StringWriter())
                using (var error = new StringWriter())
                {
                    try
                    {
                        Console.SetOut(output); Console.SetError(error);
                        var args = SplitArguments(arguments).Concat(new[] { "--store", RuntimeAssets.LayoutPath }).ToArray();
                        var exitCode = global::Program.Execute(args);
                        if (exitCode != 0) throw new InvalidOperationException(error.ToString().Trim());
                        return output.ToString().Trim();
                    }
                    finally { Console.SetOut(previousOut); Console.SetError(previousError); }
                }
            }
        }

        private static string[] SplitArguments(string value)
        {
            var result = new List<string>(); var token = new System.Text.StringBuilder(); var quoted = false; var escaped = false;
            foreach (var character in value)
            {
                if (escaped) { token.Append(character); escaped = false; continue; }
                if (quoted && character == '\\') { escaped = true; continue; }
                if (character == '"') { quoted = !quoted; continue; }
                if (!quoted && Char.IsWhiteSpace(character)) { if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); } continue; }
                token.Append(character);
            }
            if (escaped) token.Append('\\');
            if (token.Length > 0) result.Add(token.ToString());
            return result.ToArray();
        }
        private static bool IsVertical(FormsScreen[] screens)
        {
            if (screens == null || screens.Length != 2) return false;
            var a = screens[0].Bounds; var b = screens[1].Bounds;
            var horizontalDistance = Math.Abs((a.Left + a.Width / 2.0) - (b.Left + b.Width / 2.0));
            var verticalDistance = Math.Abs((a.Top + a.Height / 2.0) - (b.Top + b.Height / 2.0));
            return verticalDistance > horizontalDistance;
        }

        private static FormsScreen[] DisplayOrder(FormsScreen[] screens)
        {
            if (screens == null) return new FormsScreen[0];
            return IsVertical(screens) ? screens.OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToArray() : screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        }

        private static string Describe(FormsScreen[] screens)
        {
            if (screens == null || screens.Length == 0) return "未识别屏幕";
            if (screens.Length == 1) return "单屏";
            if (screens.Length == 2) return IsVertical(screens) ? "上下双屏" : "左右双屏";
            return screens.Length + "屏布局";
        }

        private static string Resolution(FormsScreen[] screens)
        {
            var ordered = DisplayOrder(screens);
            if (ordered.Length == 0) return "分辨率：未识别";
            if (ordered.Length == 1) return "分辨率：" + ordered[0].Bounds.Width + "×" + ordered[0].Bounds.Height;
            if (ordered.Length == 2)
            {
                var first = IsVertical(ordered) ? "上" : "左";
                var second = IsVertical(ordered) ? "下" : "右";
                return "分辨率：" + first + ordered[0].Bounds.Width + "×" + ordered[0].Bounds.Height + "，" + second + ordered[1].Bounds.Width + "×" + ordered[1].Bounds.Height;
            }
            return "分辨率：" + string.Join("，", ordered.Select((s, i) => (i + 1) + "号" + s.Bounds.Width + "×" + s.Bounds.Height));
        }
    }

    internal sealed class UiButton : Button
    {
        public UiButton() { Background = Brushes.Transparent; BorderBrush = Brushes.Transparent; BorderThickness = new Thickness(0); Padding = new Thickness(0); FocusVisualStyle = Ui.FocusRing(24); HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; Template = Ui.ButtonTemplate(); }
    }

    // Uses the exported default SVG raster solely as the icon layer. State colours are
    // painted by one WPF surface so no second anti-aliased SVG edge can leak through.
    internal sealed class TitleBarStateButton : Button
    {
        private readonly Border _surface;
        private readonly bool _isClose;
        private bool _pressed;

        public TitleBarStateButton(string defaultIcon, bool isClose)
        {
            _isClose = isClose;
            UIElement icon = defaultIcon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? CenteredIcon(defaultIcon)
                : new Image { Source = new BitmapImage(new Uri(defaultIcon)), Stretch = Stretch.Fill };
            _surface = new Border
            {
                Child = icon,
                Background = Brushes.Transparent,
                CornerRadius = isClose ? new CornerRadius(0, 24, 0, 0) : new CornerRadius(0)
            };
            Content = _surface;
            Padding = new Thickness(0);
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            FocusVisualStyle = Ui.FocusRing(isClose ? 24 : 8);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Template = FlatTemplate();
            Ui.Label(this, isClose ? "关闭窗口" : "最小化");
            MouseEnter += delegate { UpdateSurface(); };
            MouseLeave += delegate { _pressed = false; UpdateSurface(); };
            PreviewMouseLeftButtonDown += delegate { _pressed = true; UpdateSurface(); };
            PreviewMouseLeftButtonUp += delegate { _pressed = false; UpdateSurface(); };
        }

        private static UIElement CenteredIcon(string svgPath)
        {
            var grid = new Grid();
            var icon = Ui.SvgImage(svgPath, 24, 24); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon); return grid;
        }

        private void UpdateSurface()
        {
            if (!IsMouseOver) { _surface.Background = Brushes.Transparent; return; }
            var colour = _isClose ? (_pressed ? "#CC2934" : "#FF3341") : (_pressed ? "#454647" : "#363738");
            _surface.Background = Ui.Brush(colour);
        }

        private static ControlTemplate FlatTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            border.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }

    // The top corners use 60% smoothing, which WPF's stock Border cannot express.
    internal sealed class SmoothTopSurface : Decorator
    {
        public Brush Fill { get; set; }
        public double Radius { get; set; }
        public double Smoothing { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight; var r = Radius;
            var k = r * (0.55228475 + 0.12 * Smoothing);
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), true, false);
                c.BezierTo(new Point(w - r + k, 0), new Point(w, r - k), new Point(w, r), true, false);
                c.LineTo(new Point(w, h), true, false);
                c.LineTo(new Point(0, h), true, false);
                c.LineTo(new Point(0, r), true, false);
                c.BezierTo(new Point(0, r - k), new Point(r - k, 0), new Point(r, 0), true, false);
            }
            dc.DrawGeometry(Fill, null, geometry);
        }
    }

    // Four-corner smooth rounded rectangle with a 1px INSIDE stroke, drawn on the
    // Viewbox-outer layer so the stroke survives the 75% content scale unblurred.
    // Mirrors the 60% corner smoothing used by SmoothTopSurface.
    internal sealed class SmoothRoundedStroke : Decorator
    {
        public Brush Stroke { get; set; }
        public double StrokeThickness { get; set; }
        public double Radius { get; set; }
        public double Smoothing { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight; var r = Radius;
            var t = StrokeThickness > 0 ? StrokeThickness : 1;
            var k = r * (0.55228475 + 0.12 * Smoothing);
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), true, false);
                c.BezierTo(new Point(w - r + k, 0), new Point(w, r - k), new Point(w, r), true, false);
                c.LineTo(new Point(w, h - r), true, false);
                c.BezierTo(new Point(w, h - r + k), new Point(w - k, h), new Point(w - r, h), true, false);
                c.LineTo(new Point(r, h), true, false);
                c.BezierTo(new Point(r - k, h), new Point(0, h - k), new Point(0, h - r), true, false);
                c.LineTo(new Point(0, r), true, false);
                c.BezierTo(new Point(0, r - k), new Point(r - k, 0), new Point(r, 0), true, false);
            }
            dc.DrawGeometry(null, new Pen(Stroke, t) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    internal sealed class UiSurface : Border { public UiSurface(double radius, Brush fill) { CornerRadius = new CornerRadius(radius); Background = fill; } }

    internal enum TextIconButtonKind { Refresh, RowAction, NewLayout }

    // Keeps fixed dimensions and state colours while rendering labels through WPF.
    internal sealed class TextIconButton : Button
    {
        private readonly Border _surface = new Border();
        private readonly TextBlock _label;
        private readonly FrameworkElement _icon;
        private readonly TextIconButtonKind _kind;
        private readonly Brush _foreground;
        private bool _pressed;

        public TextIconButton(string label, string iconAsset, double width, double height, TextIconButtonKind kind, Brush foreground = null)
        {
            _kind = kind; _foreground = foreground ?? (kind == TextIconButtonKind.NewLayout ? Ui.Dark : null); Width = width; Height = height;
            _icon = Ui.SvgImage(iconAsset, 16, 16, _foreground); _icon.VerticalAlignment = VerticalAlignment.Center;
            _label = new TextBlock { Text = label, FontFamily = UiTypography.Static(false), FontSize = 16, FontWeight = FontWeights.Normal, Foreground = _foreground ?? Ui.TextPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), SnapsToDevicePixels = true, UseLayoutRounding = true };
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
            content.Children.Add(_icon); content.Children.Add(_label);
            _surface.Child = content; _surface.CornerRadius = new CornerRadius(kind == TextIconButtonKind.NewLayout ? 12 : 8);
            Content = _surface; Padding = new Thickness(0); BorderThickness = new Thickness(0); Background = Brushes.Transparent; FocusVisualStyle = Ui.FocusRing(kind == TextIconButtonKind.NewLayout ? 12 : 8);
            HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; Template = FlatTemplate();
            Ui.Label(this, label);
            MouseEnter += delegate { UpdateState(); }; MouseLeave += delegate { _pressed = false; UpdateState(); };
            PreviewMouseLeftButtonDown += delegate { _pressed = true; UpdateState(); }; PreviewMouseLeftButtonUp += delegate { _pressed = false; UpdateState(); };
            IsEnabledChanged += delegate { if (!IsEnabled) _pressed = false; UpdateState(); };
            UpdateState();
        }

        private void UpdateState()
        {
            var hovering = IsMouseOver;
            if (_kind == TextIconButtonKind.NewLayout)
            {
                _surface.Background = !IsEnabled ? Ui.Surface : !hovering ? Ui.PrimaryBase : (_pressed ? Ui.PrimaryDeep : Ui.Primary);
                _label.FontFamily = UiTypography.Static(false); _label.Foreground = _foreground ?? Ui.TextPrimary; _icon.Opacity = 1;
                return;
            }
            if (_kind == TextIconButtonKind.Refresh)
            {
                _surface.Background = !hovering ? Brushes.Transparent : (_pressed ? Ui.Hover : Ui.Stroke);
                _label.FontFamily = UiTypography.Static(false); _label.Foreground = _foreground ?? Ui.TextPrimary; _icon.Opacity = 1;
                return;
            }
            _surface.Background = !hovering ? Brushes.Transparent : (_pressed ? Ui.OverlayStrong : Ui.OverlayLight);
            _label.FontFamily = UiTypography.Static(false); _label.Foreground = _foreground ?? Ui.TextPrimary; _icon.Opacity = 1;
        }

        private static ControlTemplate FlatTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            border.AppendChild(presenter); return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }

    // Confirmation dialogs use a 420x298 canvas, a 56px header and 164x64 actions.
    internal sealed class ConfirmDialog : Window
    {
        private bool _accepted;

        private ConfirmDialog(string title, string message)
        {
            // 20px design-space clearance around every edge preserves the same
            // 0/0/20px 50% black shadow used by the program window.
            Width = 345; Height = 253.5; MinWidth = 345; MinHeight = 253.5; MaxWidth = 345; MaxHeight = 253.5;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            // The 1px inside stroke lives outside the Viewbox (315x223.5 real px:
            // 420x298 design * 0.75 at a 15px margin) so it is never scaled.
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas(title, message) };
            var stroke = new SmoothRoundedStroke { Margin = new Thickness(15), Stroke = Ui.Stroke, StrokeThickness = 1, Radius = 18, Smoothing = 0.6 };
            Content = new Grid { Children = { viewbox, stroke } };
        }

        private UIElement BuildCanvas(string title, string message)
        {
            var shell = new Canvas { Width = 420, Height = 298 };
            var body = new Border { Width = 420, Height = 242, Background = Ui.Surface, CornerRadius = new CornerRadius(0, 0, 24, 24) };
            Canvas.SetTop(body, 56); shell.Children.Add(body);
            AddHeader(shell, title, delegate { Close(); });
            // Separate paragraphs preserve the 21px line height and 8px gap.
            var paragraphs = new StackPanel { Width = 348, Height = 50, VerticalAlignment = VerticalAlignment.Top };
            var parts = message.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
            {
                var paragraph = new TextBlock { Text = parts[i], Width = 348, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontFamily = UiTypography.Static(false), FontSize = 16, Foreground = Ui.TextPrimary, LineHeight = 21 };
                if (i > 0) paragraph.Margin = new Thickness(0, 8, 0, 0);
                paragraphs.Children.Add(paragraph);
            }
            Canvas.SetLeft(paragraphs, 36); Canvas.SetTop(paragraphs, 112); shell.Children.Add(paragraphs);
            var cancel = ActionButton("取消", false, delegate { Close(); }); Canvas.SetLeft(cancel, 36); Canvas.SetTop(cancel, 198); shell.Children.Add(cancel);
            var confirm = ActionButton("确定", true, delegate { _accepted = true; Close(); }); Canvas.SetLeft(confirm, 220); Canvas.SetTop(confirm, 198); shell.Children.Add(confirm);
            return ShadowedSurface(shell, 420, 298);
        }

        internal static UIElement ShadowedSurface(UIElement content, double width, double height)
        {
            var host = new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                ClipToBounds = false,
                Child = content,
                Effect = Ui.Shadow
            };
            var outer = new Canvas { Width = width + 40, Height = height + 40, ClipToBounds = false };
            Canvas.SetLeft(host, 20); Canvas.SetTop(host, 20); outer.Children.Add(host);
            return outer;
        }

        internal static bool Ask(Window owner, string title, string message)
        {
            var dialog = new ConfirmDialog(title, message) { Owner = owner };
            dialog.ShowDialog(); return dialog._accepted;
        }

        internal static void AddHeader(Canvas shell, string title, Action closeAction)
        {
            // A rounded vector surface keeps the exported top-left curve intact.
            // #171819 matches the program window's title bar for visual unity.
            var header = new SmoothTopSurface { Width = 420, Height = 56, Fill = Ui.Shell, Radius = 24, Smoothing = 0.6 };
            shell.Children.Add(header);
            var heading = new TextBlock { Text = title, FontFamily = UiTypography.Static(true), FontSize = 18, FontWeight = FontWeights.Normal, Foreground = Ui.TextPrimary };
            Canvas.SetLeft(heading, 24); Canvas.SetTop(heading, 16); shell.Children.Add(heading);
            var close = new DialogCloseButton(closeAction) { Width = 72, Height = 56 };
            Canvas.SetLeft(close, 348); shell.Children.Add(close);
        }

        internal static Button ActionButton(string label, bool blue, Action action)
        {
            var normal = blue ? Ui.PrimaryBase : Brushes.White;
            var hover = blue ? Ui.Primary : Ui.LightHover;
            var pressed = blue ? Ui.PrimaryDeep : Ui.LightPressed;
            var content = new TextBlock { Text = label, FontFamily = UiTypography.Static(true), FontSize = 18, FontWeight = FontWeights.Normal, Foreground = Ui.Dark, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var button = new UiButton { Width = 164, Height = 64, Background = normal, Content = content, Template = Ui.ButtonTemplate(24) };
            var down = false;
            button.MouseEnter += delegate { button.Background = down ? pressed : hover; };
            button.MouseLeave += delegate { down = false; button.Background = normal; };
            button.PreviewMouseLeftButtonDown += delegate { down = true; button.Background = pressed; };
            button.PreviewMouseLeftButtonUp += delegate { down = false; button.Background = button.IsMouseOver ? hover : normal; };
            button.Click += delegate { action(); }; return button;
        }
    }

    // Same hover/pressed semantics as the application title-bar close control,
    // with the modal's 72x56 geometry and rounded top-right corner.
    internal sealed class DialogCloseButton : Button
    {
        private readonly Border _surface;
        private bool _pressed;

        public DialogCloseButton(Action close)
        {
            // Render the centred 24x24 close glyph as WPF geometry.
            var glyph = Ui.SvgImage(RuntimeAssets.AssetPath("Icons", "close.svg"), 24, 24);
            _surface = new Border { Child = glyph, Background = Brushes.Transparent, CornerRadius = new CornerRadius(0, 24, 0, 0) };
            Content = _surface; Padding = new Thickness(0); BorderThickness = new Thickness(0); Background = Brushes.Transparent; FocusVisualStyle = Ui.FocusRing(24);
            HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; Template = StretchTemplate();
            Ui.Label(this, "关闭弹窗");
            MouseEnter += delegate { UpdateSurface(); }; MouseLeave += delegate { _pressed = false; UpdateSurface(); };
            PreviewMouseLeftButtonDown += delegate { _pressed = true; UpdateSurface(); }; PreviewMouseLeftButtonUp += delegate { _pressed = false; UpdateSurface(); };
            Click += delegate { close(); };
        }

        private void UpdateSurface()
        {
            _surface.Background = !IsMouseOver ? Brushes.Transparent : (_pressed ? Ui.ClosePressed : Ui.CloseHover);
        }

        private static ControlTemplate StretchTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            border.AppendChild(presenter); return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }

    internal sealed class NameDialog : Window
    {
        private readonly TextBox _input = new TextBox();
        private bool _accepted;

        private NameDialog(string title, string hint, string initial)
        {
            Width = 345; Height = 282.75; MinWidth = 345; MinHeight = 282.75; MaxWidth = 345; MaxHeight = 282.75;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            // The 1px inside stroke lives outside the Viewbox (315x252.75 real px:
            // 420x337 design * 0.75 at a 15px margin) so it is never scaled.
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas(title, hint, initial) };
            var stroke = new SmoothRoundedStroke { Margin = new Thickness(15), Stroke = Ui.Stroke, StrokeThickness = 1, Radius = 18, Smoothing = 0.6 };
            // The input stroke remains outside the Viewbox so its 1px edge is not scaled.
            var inputStroke = new SmoothRoundedStroke
            {
                Width = 261, Height = 36,
                Margin = new Thickness(42, 129.75, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Stroke = Ui.Stroke,
                StrokeThickness = 1,
                Radius = 9,
                Smoothing = 0.6
            };
            Content = new Grid { Children = { viewbox, stroke, inputStroke } };
            Loaded += delegate { _input.Focus(); _input.SelectAll(); };
        }

        private UIElement BuildCanvas(string title, string hint, string initial)
        {
            var shell = new Canvas { Width = 420, Height = 337 };
            var body = new Border { Width = 420, Height = 281, Background = Ui.Surface, CornerRadius = new CornerRadius(0, 0, 24, 24) };
            Canvas.SetTop(body, 56); shell.Children.Add(body);
            ConfirmDialog.AddHeader(shell, title, delegate { Close(); });
            var help = new TextBlock { Text = hint, Width = 348, TextAlignment = TextAlignment.Center, FontFamily = UiTypography.Static(false), FontSize = 16, Foreground = Ui.TextPrimary };
            Canvas.SetLeft(help, 36); Canvas.SetTop(help, 112); shell.Children.Add(help);
            _input.Text = initial ?? String.Empty; _input.Padding = new Thickness(16, 0, 16, 0); _input.VerticalContentAlignment = VerticalAlignment.Center;
            _input.FontFamily = UiTypography.Dynamic; _input.FontSize = 16; _input.Foreground = Ui.Dark; _input.Background = Brushes.Transparent; _input.BorderBrush = Brushes.Transparent; _input.BorderThickness = new Thickness(0);
            var inputSurface = new Border { Width = 348, Height = 48, Background = Brushes.White, CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = _input };
            Canvas.SetLeft(inputSurface, 36); Canvas.SetTop(inputSurface, 153); shell.Children.Add(inputSurface);
            var cancel = ConfirmDialog.ActionButton("取消", false, delegate { Close(); }); Canvas.SetLeft(cancel, 36); Canvas.SetTop(cancel, 237); shell.Children.Add(cancel);
            var confirm = ConfirmDialog.ActionButton("确定", true, delegate { _accepted = true; Close(); }); Canvas.SetLeft(confirm, 220); Canvas.SetTop(confirm, 237); shell.Children.Add(confirm);
            return ConfirmDialog.ShadowedSurface(shell, 420, 337);
        }

        internal static string Ask(Window owner, string title, string hint, string initial)
        {
            var dialog = new NameDialog(title, hint, initial) { Owner = owner };
            dialog.ShowDialog(); return dialog._accepted ? dialog._input.Text.Trim() : null;
        }
    }

    // Minimal Win32 surface for the borderless window: system title-bar menu
    // and live Aero Peek thumbnail. Everything else stays in WPF.
    internal static class Native
    {
        public const int WM_SYSCOMMAND = 0x0112;
        public const int WM_PRINTCLIENT = 0x0318;
        public const int SC_CLOSE = 0xF060;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_RESTORE = 0xF120;
        public const int SC_SIZE = 0xF000;
        public const uint MF_GRAYED = 0x1;
        public const uint TPM_RETURNCMD = 0x0100;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_NONOTIFY = 0x0080;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
        [DllImport("user32.dll")]
        public static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
        [DllImport("user32.dll")]
        public static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr lprc);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // Success feedback for every completed operation. It follows the confirmation
    // dialog's chrome (56px header, single centred 164x64 action button) but drops
    // the second text line, so the shell is 420x269 instead of 420x298.
    internal sealed class SuccessDialog : Window
    {
        private SuccessDialog(string title, string message)
        {
            // 420x269 design + 40px shadow clearance -> 345x231.75 at 0.75 scale.
            Width = 345; Height = 231.75; MinWidth = 345; MinHeight = 231.75; MaxWidth = 345; MaxHeight = 231.75;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            // The 1px inside stroke lives outside the Viewbox (315x201.75 real px:
            // 420x269 design * 0.75 at a 15px margin) so it is never scaled.
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas(title, message) };
            var stroke = new SmoothRoundedStroke { Margin = new Thickness(15), Stroke = Ui.Stroke, StrokeThickness = 1, Radius = 18, Smoothing = 0.6 };
            Content = new Grid { Children = { viewbox, stroke } };
        }

        private UIElement BuildCanvas(string title, string message)
        {
            var shell = new Canvas { Width = 420, Height = 269 };
            var body = new Border { Width = 420, Height = 213, Background = Ui.Surface, CornerRadius = new CornerRadius(0, 0, 24, 24) };
            Canvas.SetTop(body, 56); shell.Children.Add(body);
            ConfirmDialog.AddHeader(shell, title, delegate { Close(); });
            var text = new TextBlock { Text = message, Width = 348, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontFamily = UiTypography.Static(false), FontSize = 16, Foreground = Ui.TextPrimary, LineHeight = 21 };
            Canvas.SetLeft(text, 36); Canvas.SetTop(text, 112); shell.Children.Add(text);
            var confirm = ConfirmDialog.ActionButton("确定", true, delegate { Close(); });
            Canvas.SetLeft(confirm, 128); Canvas.SetTop(confirm, 169); shell.Children.Add(confirm);
            return ConfirmDialog.ShadowedSurface(shell, 420, 269);
        }

        internal static void Show(Window owner, string title, string message)
        {
            var dialog = new SuccessDialog(title, message) { Owner = owner };
            dialog.ShowDialog();
        }
    }

    // Operational failures are exceptional (for example a rejected name) and
    // must not close the application before the user can read the native error.
    // Self-drawn like the success dialog, with a red confirm button so the
    // warning reads as failure instead of the system MessageBox chrome.
    internal sealed class NoticeDialog : Window
    {
        private NoticeDialog(string title, string message)
        {
            // A 420x298 design plus shadow clearance becomes 345x253.5 at 75%.
            Width = 345; Height = 253.5; MinWidth = 345; MinHeight = 253.5; MaxWidth = 345; MaxHeight = 253.5;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            // The 1px inside stroke lives outside the Viewbox (315x223.5 real px:
            // 420x298 design * 0.75 at a 15px margin) so it is never scaled.
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas(title, message) };
            var stroke = new SmoothRoundedStroke { Margin = new Thickness(15), Stroke = Ui.Stroke, StrokeThickness = 1, Radius = 18, Smoothing = 0.6 };
            Content = new Grid { Children = { viewbox, stroke } };
        }

        private UIElement BuildCanvas(string title, string message)
        {
            var shell = new Canvas { Width = 420, Height = 298 };
            var body = new Border { Width = 420, Height = 242, Background = Ui.Surface, CornerRadius = new CornerRadius(0, 0, 24, 24) };
            Canvas.SetTop(body, 56); shell.Children.Add(body);
            ConfirmDialog.AddHeader(shell, title, delegate { Close(); });
            // Same two-paragraph structure as the confirmation dialog: each
            // paragraph is 21px high with an 8px gap, centred on 348px width.
            var paragraphs = new StackPanel { Width = 348, Height = 50, VerticalAlignment = VerticalAlignment.Top };
            var parts = message.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
            {
                var paragraph = new TextBlock { Text = parts[i], Width = 348, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontFamily = UiTypography.Static(false), FontSize = 16, Foreground = Ui.TextPrimary, LineHeight = 21 };
                if (i > 0) paragraph.Margin = new Thickness(0, 8, 0, 0);
                paragraphs.Children.Add(paragraph);
            }
            Canvas.SetLeft(paragraphs, 36); Canvas.SetTop(paragraphs, 112); shell.Children.Add(paragraphs);
            // The cyan confirmation action is centred below the message.
            var confirm = ConfirmDialog.ActionButton("确定", true, delegate { Close(); });
            Canvas.SetLeft(confirm, 128); Canvas.SetTop(confirm, 198); shell.Children.Add(confirm);
            return ConfirmDialog.ShadowedSurface(shell, 420, 298);
        }

        internal static void Show(Window owner, string message)
        {
            var dialog = new NoticeDialog("操作失败", message) { Owner = owner };
            dialog.ShowDialog();
        }
    }

    // Display frames are ordered horizontally or vertically with a 4px gap,
    // centred on the cross axis and capped at 1/12 scale.
    internal sealed class MonitorPreview : FrameworkElement
    {
        private FormsScreen[] _screens = new FormsScreen[0];

        public void SetScreens(FormsScreen[] value)
        {
            _screens = value ?? new FormsScreen[0];
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_screens.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
            var vertical = IsVerticalLayout(_screens);
            var screens = vertical ? _screens.OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToArray() : _screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
            const double gap = 4;
            const double designScaleCap = 1.0 / 12.0;
            var totalPrimary = vertical ? screens.Sum(s => (double)s.Bounds.Height) : screens.Sum(s => (double)s.Bounds.Width);
            var maxCross = vertical ? screens.Max(s => (double)s.Bounds.Width) : screens.Max(s => (double)s.Bounds.Height);
            var primarySpace = vertical ? ActualHeight : ActualWidth;
            var crossSpace = vertical ? ActualWidth : ActualHeight;
            var scale = Math.Min(designScaleCap, Math.Min((primarySpace - gap * (screens.Length - 1)) / Math.Max(1, totalPrimary), crossSpace / Math.Max(1, maxCross)));
            scale = Math.Max(0, scale);
            var usedPrimary = totalPrimary * scale + gap * (screens.Length - 1);
            var cursor = (primarySpace - usedPrimary) / 2;
            var typeface = new Typeface(UiTypography.Static(false), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            foreach (var screen in screens)
            {
                var width = screen.Bounds.Width * scale;
                var height = screen.Bounds.Height * scale;
                var rect = vertical ? new Rect((ActualWidth - width) / 2, cursor, width, height) : new Rect(cursor, (ActualHeight - height) / 2, width, height);
                dc.DrawRoundedRectangle(Ui.PrimaryBase, null, rect, 24, 24);
                var label = screen.Bounds.Width + "×" + screen.Bounds.Height;
                var text = new FormattedText(label, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, typeface, 16, Ui.Dark, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(rect.Left + (rect.Width - text.Width) / 2, rect.Top + (rect.Height - text.Height) / 2));
                cursor += (vertical ? height : width) + gap;
            }
        }

        private static bool IsVerticalLayout(FormsScreen[] screens)
        {
            if (screens.Length != 2) return false;
            var a = screens[0].Bounds; var b = screens[1].Bounds;
            var horizontalDistance = Math.Abs((a.Left + a.Width / 2.0) - (b.Left + b.Width / 2.0));
            var verticalDistance = Math.Abs((a.Top + a.Height / 2.0) - (b.Top + b.Height / 2.0));
            return verticalDistance > horizontalDistance;
        }
    }

    internal static class Ui
    {
        // Shared semantic brushes for the dark and light palettes.
        // Each token is a SHARED SolidColorBrush instance: ApplyTheme mutates
        // its Color in place so every control referencing it repaints instantly.
        private static readonly SolidColorBrush ShellBrush = New("#171819");          // 2:171 shell / title bar
        private static readonly SolidColorBrush SurfaceBrush = New("#272829");        // 2:172 card surface
        private static readonly SolidColorBrush HoverBrush = New("#454647");          // 2:165 hover grey
        private static readonly SolidColorBrush StrokeBrush = New("#363738");         // 2:166 inside stroke / pressed grey
        private static readonly SolidColorBrush DividerBrush = New("#363738");        // separator line (same as stroke)
        private static readonly SolidColorBrush TextPrimaryBrush = New("#FFFFFF");    // 2:170 primary text
        private static readonly SolidColorBrush DarkBrush = New("#171819");           // constant dark text on light surfaces
        private static readonly SolidColorBrush PrimaryBrush = New("#57F4FF");        // 7:218 hover cyan
        private static readonly SolidColorBrush PrimaryDeepBrush = New("#0ECCE5");    // 7:236 pressed cyan
        private static readonly SolidColorBrush CloseHoverBrush = New("#FF3341");     // 2:178 close hover red
        private static readonly SolidColorBrush ClosePressedBrush = New("#CC2934");   // 2:184 close pressed red
        private static readonly SolidColorBrush LightHoverBrush = New("#DEDFE0");     // 9:2205 light button hover
        private static readonly SolidColorBrush LightPressedBrush = New("#BEC0C2");   // 9:2206 light button pressed
        private static readonly SolidColorBrush MaskBrush = New("#000000");           // 35:720 black overlay (alpha per theme)
        private static readonly SolidColorBrush OverlayLightBrush = NewAlpha(25, 255, 255, 255);   // 10% white/black hover overlay
        private static readonly SolidColorBrush OverlayStrongBrush = NewAlpha(51, 255, 255, 255);  // 20% white/black pressed overlay
        private static readonly SolidColorBrush PrimaryBaseBrush = New("#23E5FF");
        private static readonly System.Windows.Media.Effects.DropShadowEffect ShadowEffect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.5, ShadowDepth = 0, BlurRadius = 20 };

        public static Brush Shell { get { return ShellBrush; } }
        public static Brush Surface { get { return SurfaceBrush; } }
        public static Brush Hover { get { return HoverBrush; } }
        public static Brush Stroke { get { return StrokeBrush; } }
        public static Brush Divider { get { return DividerBrush; } }
        public static Brush TextPrimary { get { return TextPrimaryBrush; } }
        public static Brush Dark { get { return DarkBrush; } }
        public static Brush PrimaryBase { get { return PrimaryBaseBrush; } }
        public static Brush Primary { get { return PrimaryBrush; } }
        public static Brush PrimaryDeep { get { return PrimaryDeepBrush; } }
        public static Brush CloseHover { get { return CloseHoverBrush; } }
        public static Brush ClosePressed { get { return ClosePressedBrush; } }
        public static Brush LightHover { get { return LightHoverBrush; } }
        public static Brush LightPressed { get { return LightPressedBrush; } }
        public static Brush Mask { get { return MaskBrush; } }
        public static Brush OverlayLight { get { return OverlayLightBrush; } }
        public static Brush OverlayStrong { get { return OverlayStrongBrush; } }
        public static System.Windows.Media.Effects.DropShadowEffect Shadow { get { return ShadowEffect; } }
        public static Brush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }

        private static SolidColorBrush New(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static SolidColorBrush NewAlpha(byte a, byte r, byte g, byte b) { return new SolidColorBrush(Color.FromArgb(a, r, g, b)); }
        private static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        // Switches every shared token between the dark and light palettes.
        public static void ApplyTheme(bool light)
        {
            ShellBrush.Color = C(light ? "#FFFFFF" : "#171819");       // 大白 <-> 大黑
            SurfaceBrush.Color = C(light ? "#DEDFE0" : "#272829");     // 二白 <-> 二黑
            HoverBrush.Color = C(light ? "#9EA1A4" : "#454647");       // 四白 <-> 四黑
            StrokeBrush.Color = C(light ? "#BEC0C2" : "#363738");      // 三白 <-> 三黑
            DividerBrush.Color = C(light ? "#BEC0C2" : "#363738");     // 三白 <-> 三黑
            TextPrimaryBrush.Color = C(light ? "#171819" : "#FFFFFF"); // 大黑 <-> 大白
            LightHoverBrush.Color = C(light ? "#BEC0C2" : "#DEDFE0");  // light buttons darken under light theme
            LightPressedBrush.Color = C(light ? "#9EA1A4" : "#BEC0C2");
            MaskBrush.Color = Color.FromArgb((byte)(light ? 51 : 128), 0, 0, 0);  // 20% <-> 50%
            OverlayLightBrush.Color = Color.FromArgb(25, light ? (byte)0 : (byte)255, light ? (byte)0 : (byte)255, light ? (byte)0 : (byte)255);
            OverlayStrongBrush.Color = Color.FromArgb(51, light ? (byte)0 : (byte)255, light ? (byte)0 : (byte)255, light ? (byte)0 : (byte)255);
            ShadowEffect.Opacity = light ? 0.2 : 0.5;
        }

        // Reads the system app theme (1 = light, 0 = dark) from the Personalize key.
        public static bool DetectSystemLight()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    var value = key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 1;
                }
            }
            catch { return false; }
        }
        // WPF has no native SVG control.  Parse the project paths into WPF geometry
        // so icons stay vector sharp at every DPI while preserving multi-color fills.
        public static FrameworkElement SvgImage(string svgPath, double width, double height, Brush fill = null)
        {
            try
            {
                var svg = File.ReadAllText(svgPath);
                var viewBox = Regex.Match(svg, "viewBox=\\\"\\s*0\\s+0\\s+([0-9.]+)\\s+([0-9.]+)\\s*\\\"");
                if (!viewBox.Success) throw new InvalidOperationException("SVG viewBox missing.");
                var pathTags = Regex.Matches(svg, "<path\\b[^>]*>", RegexOptions.IgnoreCase);
                var pathData = new List<string>();
                var pathFills = new List<string>();
                foreach (Match tag in pathTags)
                {
                    var data = Regex.Match(tag.Value, "\\bd=\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
                    if (!data.Success) continue;
                    pathData.Add(data.Groups[1].Value);
                    var colour = Regex.Match(tag.Value, "\\bfill=\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
                    pathFills.Add(colour.Success ? colour.Groups[1].Value : String.Empty);
                }
                var scaleX = width / Double.Parse(viewBox.Groups[1].Value, CultureInfo.InvariantCulture);
                var scaleY = height / Double.Parse(viewBox.Groups[2].Value, CultureInfo.InvariantCulture);
                var distinctFills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pathFill in pathFills)
                    if (!String.IsNullOrWhiteSpace(pathFill) && !pathFill.Equals("none", StringComparison.OrdinalIgnoreCase)) distinctFills.Add(pathFill);
                if (fill == null && distinctFills.Count > 1)
                {
                    var canvas = new Canvas { Width = width, Height = height, SnapsToDevicePixels = true, UseLayoutRounding = true };
                    for (var i = 0; i < pathData.Count; i++)
                    {
                        var colouredGeometry = Geometry.Parse(pathData[i]).Clone();
                        colouredGeometry.Transform = new ScaleTransform(scaleX, scaleY);
                        canvas.Children.Add(new System.Windows.Shapes.Path { Data = colouredGeometry, Fill = SvgBrush(pathFills[i]) ?? TextPrimary, Width = width, Height = height, Stretch = Stretch.None, SnapsToDevicePixels = true, UseLayoutRounding = true });
                    }
                    return canvas;
                }
                Geometry geometry;
                if (pathData.Count > 0)
                {
                    // An exported SVG can contain several independent paths.  They
                    // must be retained as one GeometryGroup; reading just the
                    // first path silently drops the rest of the icon.
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var data in pathData) group.Children.Add(Geometry.Parse(data).Clone());
                    geometry = group;
                }
                else
                {
                    var rect = Regex.Match(svg, "<rect[^>]*\\bx=\\\"([0-9.]+)\\\"[^>]*\\by=\\\"([0-9.]+)\\\"[^>]*\\bwidth=\\\"([0-9.]+)\\\"[^>]*\\bheight=\\\"([0-9.]+)\\\"(?:[^>]*\\brx=\\\"([0-9.]+)\\\")?");
                    if (!rect.Success) throw new InvalidOperationException("SVG geometry missing.");
                    var rx = rect.Groups[5].Success ? Double.Parse(rect.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
                    geometry = new RectangleGeometry(new Rect(Double.Parse(rect.Groups[1].Value, CultureInfo.InvariantCulture), Double.Parse(rect.Groups[2].Value, CultureInfo.InvariantCulture), Double.Parse(rect.Groups[3].Value, CultureInfo.InvariantCulture), Double.Parse(rect.Groups[4].Value, CultureInfo.InvariantCulture)), rx, rx);
                }
                geometry.Transform = new ScaleTransform(scaleX, scaleY);
                // A Path (not a frozen DrawingImage) so the fill brush can be
                // swapped live when the theme changes.  Fully qualified because
                // System.IO.Path would otherwise shadow the shape type.
                return new System.Windows.Shapes.Path { Data = geometry, Fill = fill ?? TextPrimary, Width = width, Height = height, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true, UseLayoutRounding = true };
            }
            catch
            {
                var fallback = new Image { Source = new BitmapImage(new Uri(Path.ChangeExtension(svgPath, ".png"))), Width = width, Height = height, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true, UseLayoutRounding = true };
                RenderOptions.SetBitmapScalingMode(fallback, BitmapScalingMode.HighQuality); return fallback;
            }
        }
        private static Brush SvgBrush(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            try { return Brush(value); } catch { return TextPrimary; }
        }
        // Static(true) already selects the bundled OPPOSans-B face. Requesting
        // FontWeight.Bold here may make WPF substitute or synthesize another weight.
        public static TextBlock Text(string value, double size, bool bold, Thickness? margin = null) { return new TextBlock { Text = value, FontFamily = UiTypography.Static(bold), FontSize = size, FontWeight = FontWeights.Normal, Foreground = Ui.TextPrimary, Margin = margin ?? new Thickness(0), VerticalAlignment = VerticalAlignment.Center }; }
        public static TextBlock Dynamic(string value, double size) { return new TextBlock { Text = value, FontFamily = UiTypography.Dynamic, FontSize = size, Foreground = Ui.TextPrimary, VerticalAlignment = VerticalAlignment.Center }; }
        // Keyboard reachability: a low-key 1px blue focus ring replaces the
        // platform dotted outline so keyboard users can track focus on the
        // dark theme without breaking the visual language.  Control.FocusVisualStyle
        // is a Style property (the FocusVisualStyle template goes inside it).
        public static Style FocusRing(double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderBrushProperty, Brush("#57F4FF"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.MarginProperty, new Thickness(1));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);
            var style = new Style();
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = border }));
            return style;
        }
        // Screen-reader name for icon-only and custom buttons.
        public static void Label(FrameworkElement element, string name)
        {
            AutomationProperties.SetName(element, name);
        }
        public static ControlTemplate ButtonTemplate(double radius = 24) { var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius)); var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter); return new ControlTemplate(typeof(Button)) { VisualTree = border }; }
    }
}
