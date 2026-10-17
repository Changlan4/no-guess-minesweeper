using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Minesweeper.Controls;
using Minesweeper.Models;
using Minesweeper.Services;
using Minesweeper.ViewModels;

namespace Minesweeper;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private bool _updatingLayout;

    public MainWindow()
    {
        InitializeComponent();

        var (w, h, state) = ScoreService.LoadWindowState();
        Width = w; Height = h;
        if (state == 2) WindowState = WindowState.Maximized;

        // 必须在 NewGame 之前订阅，否则首次棋盘绘制收不到通知
        Board.CellLeftUp += cell => cell.LeftClickCommand.Execute(null);
        Board.CellRightUp += cell => cell.RightClickCommand.Execute(null);
        Board.CellChord += cell => cell.ChordCommand.Execute(null);

        // 键盘操作：方向键移动光标，空格掀开，F 插旗
        Board.CellActivate += cell => cell.LeftClickCommand.Execute(null);
        Board.CellFlagToggle += cell => cell.RightClickCommand.Execute(null);

        // 重绘前把本帧要播的动画和提示位置交给画布
        VM.BoardChanged += () =>
        {
            foreach (var (row, col, kind, delay) in VM.TakePendingAnimations())
            {
                switch (kind)
                {
                    case BoardCanvas.CellAnimKind.Reveal:
                        Board.AnimateReveal(row, col, delay);
                        break;
                    case BoardCanvas.CellAnimKind.Flag:
                        Board.AnimateFlag(row, col);
                        break;
                    default:
                        Board.AnimatePulse(row, col);
                        break;
                }
            }
            // 提示是一次性请求：每次按 H 都会取到一次，不会因为「坐标没变」而被跳过。
            if (VM.TryTakeHint(out int hintRow, out int hintCol))
            {
                if (hintRow < 0) Board.ClearHint();
                else Board.FlashHint(hintRow, hintCol);
            }

            Board.TriggerRender();
        };        SourceInitialized += (_, _) => ApplyDarkTitleBar();

        VM.NewGame();
        UpdateCellSize();

        KeyDown += OnWindowKeyDown;

        Loaded += (_, _) =>
        {
            foreach (var child in LogicalTreeHelper.GetChildren(this))
                SetMenuCheckRecursive(child, VM.SelectedTheme.Name);
            SetMenuCheckRecursive(this, VM.CurrentDifficulty.Name);
        };

        Closing += (_, _) =>
        {
            VM.Shutdown();

            // 最大化/最小化关闭时要记「还原后」的尺寸。注意 RestoreBounds 为空时（Rect.Empty）
            // 它的 Width 是 double.NegativeInfinity —— 原实现会把它存成 "-∞"，
            // 下次启动赋给 Window.Width 直接抛异常，游戏就再也打不开了。
            double w = Width, h = Height;
            if (WindowState != WindowState.Normal && !RestoreBounds.IsEmpty)
            {
                w = RestoreBounds.Width;
                h = RestoreBounds.Height;
            }
            ScoreService.SaveWindowState(w, h, WindowState == WindowState.Maximized ? 2 : 0);
        };
    }

    private void OnBoardSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCellSize();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 把系统标题栏切成深色，否则会出现「浅色标题栏 + 深色主体」的割裂感。
    /// 老系统不支持这个属性时静默忽略，标题栏保持系统默认。
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int enabled = 1;
            const int UseImmersiveDarkMode = 20;      // Win10 2004+
            const int UseImmersiveDarkModeLegacy = 19; // 更早的版本

            if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch
        {
            // 忽略：拿不到系统支持就沿用系统配色
        }
    }

    private void UpdateCellSize()
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var difficulty = VM.CurrentDifficulty;

            // 用撑满可用空间的外层 Grid 测量：内层 Border 的尺寸由棋盘决定，拿它测量会循环依赖
            const double chrome = 12;   // 棋盘外框内边距与圆角余量
            double size = Math.Min(
                (BoardHost.ActualWidth - chrome) / difficulty.Cols,
                (BoardHost.ActualHeight - chrome) / difficulty.Rows);

            // 下限保证格子可点；上限避免小难度棋盘在大屏上被拉得过分巨大
            VM.CellSize = Math.Clamp(size, 20, 80);
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                VM.NewGame();
                e.Handled = true;
                break;
            case Key.H:
                VM.ShowHint();
                e.Handled = true;
                break;
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                VM.NewGame();
                e.Handled = true;
                break;
        }
    }

    private void OnHintClick(object sender, RoutedEventArgs e) => VM.ShowHint();

    private void OnStatsClick(object sender, RoutedEventArgs e) => VM.ShowStatistics();

    private void OnDifficultyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string name) return;

        var difficulty = Difficulty.All.FirstOrDefault(d => d.Name == name);
        if (difficulty == null) return;

        VM.CurrentDifficulty = difficulty;

        if (item.Parent is MenuItem parent)
            foreach (MenuItem child in parent.Items) child.IsChecked = child == item;

        UpdateCellSize();
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string themeName)
        {
            var theme = ThemeService.Themes.FirstOrDefault(t => t.Name == themeName);
            // 认不出主题名就什么都不做：不能只改勾选而不换配色
            //（难度菜单在同样情况下是提前 return 的，两边保持一致）
            if (theme == null) return;
            VM.SelectedTheme = theme;
            if (item.Parent is MenuItem parent)
                foreach (MenuItem child in parent.Items) child.IsChecked = child == item;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var d = VM.CurrentDifficulty;
        MessageBox.Show(
            "无猜扫雷 v1.6\n\n" +
            $"当前难度：{d.Name}  {d.Rows} × {d.Cols} · {d.MineCount} 雷\n" +
            "每一局都经求解器验证，保证可以纯逻辑推理完成，不需要盲猜。\n\n" +
            "操作说明：\n" +
            "• 左键：掀开\n" +
            "• 右键：插旗 / 取消旗\n" +
            "• 左右键同时按：对已翻开的数字格快速排雷\n" +
            "• 方向键 + 空格：纯键盘操作（F 插旗）\n\n" +
            "F2：新游戏    H：提示",
            "关于无猜扫雷", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void SetMenuCheckRecursive(object element, string tag)
    {
        if (element is MenuItem item)
        {
            if (item.Tag is string t && t == tag) item.IsChecked = true;
            foreach (var sub in item.Items) SetMenuCheckRecursive(sub, tag);
        }
        else if (element is DependencyObject dep)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(dep)) SetMenuCheckRecursive(child, tag);
        }
    }
}
