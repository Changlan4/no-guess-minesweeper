using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Minesweeper.Models;
using Minesweeper.Services;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>
/// 把真实窗口渲染成 PNG，供人工核对视觉效果（配色、菜单可读性、状态栏对比度等）。
/// 不显示任何窗口，只用 RenderTargetBitmap 离屏渲染。
/// 产物：测试输出目录下的 ui-preview.png
/// </summary>
public class UiSnapshotTests
{
    private readonly ITestOutputHelper _out;

    public UiSnapshotTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void 深色主题下菜单文字必须和背景有足够对比度()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(900, 580));
            root.Arrange(new Rect(0, 0, 900, 580));
            root.UpdateLayout();

            var menu = StaTestRunner.FindDescendant<System.Windows.Controls.Menu>(root);
            Assert.NotNull(menu);

            var item = menu!.Items.OfType<System.Windows.Controls.MenuItem>().First();
            var foreground = ((SolidColorBrush)item.Foreground).Color;
            var background = ((SolidColorBrush)item.Background).Color;

            // 用感知亮度差做粗略对比度判断：深色界面上文字必须明显比背景亮
            double fgLuma = 0.2126 * foreground.R + 0.7152 * foreground.G + 0.0722 * foreground.B;
            double bgLuma = 0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B;

            Assert.True(Math.Abs(fgLuma - bgLuma) > 100,
                $"菜单文字亮度 {fgLuma:F0} 与背景亮度 {bgLuma:F0} 太接近，会看不清");

            _out.WriteLine($"菜单前景 {foreground}（亮度 {fgLuma:F0}）  背景 {background}（亮度 {bgLuma:F0}）");

            window.Close();
        });
    }

    [Fact]
    public void 生成主界面截图供人工核对()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;

            const int width = 900;
            const int height = 580;

            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            // 先摆出一个「玩到一半」的局面再截图：空棋盘说明不了任何问题
            PlaySomeMoves(window);

            // 状态改了必须让画布重绘，否则 RenderTargetBitmap 用的还是上一帧的缓存。
            // 先 InvalidateVisual 让画布失效，再让 Dispatcher 把 Render 优先级的队列跑完。
            var boardCanvas = StaTestRunner.FindDescendant<Controls.BoardCanvas>(root);
            boardCanvas?.TriggerRender();
            root.InvalidateVisual();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);
            root.UpdateLayout();

            // 诊断：确认画布拿到的确实是「玩到一半」的盘面
            if (boardCanvas != null)
            {
                int nonEmpty = 0, flags = 0;
                foreach (var c in boardCanvas.Cells ?? Array.Empty<ViewModels.CellViewModel>())
                {
                    if (!string.IsNullOrEmpty(c.DisplayText)) nonEmpty++;
                    if (c.State == CellState.Flagged) flags++;
                }
                _out.WriteLine($"[截图] 画布看到：格子 {boardCanvas.Cells?.Count} 个，带文本 {nonEmpty} 个，旗 {flags} 个，CellSize={boardCanvas.CellSize:F1}");
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var path = Path.Combine(AppContext.BaseDirectory, "ui-preview.png");
            using (var stream = File.Create(path))
                encoder.Save(stream);

            _out.WriteLine($"界面截图已保存：{path}");

            window.Close();
        });
    }

    /// <summary>
    /// 在窗口上推进几步真实的游戏操作，让截图里有数字、有旗、有空白区。
    /// 全程走生产代码（RebuildCells / OnLeftClick / OnRightClick），
    /// 所以这一步同时也在验证「生成好的盘面能被正常翻开」。
    /// </summary>
    private static void PlaySomeMoves(MainWindow window)
    {
        var vm = window.DataContext!;
        var vmType = vm.GetType();

        // 用一个确定性的首击点生成盘面：截图需要可复现
        var board = SolverService.Generate(Difficulty.Expert, 8, 15).Board;

        vmType.GetField("_board", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, board);
        vmType.GetMethod("RebuildCells", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new[] { (object)board });

        // 首击格一定是空的（首击九宫格无雷），翻开它会展开一片空白区
        ClickCell(vm, board, 8, 15);

        // 再用求解器推几步，盘面上就会出现数字和边界
        for (int i = 0; i < 12; i++)
            if (!RevealOneCertainSafe(vm, board)) break;

        // 在几个确定是雷的格子旁边插旗，让截图里能看到旗子
        FlagSomeMines(vm, board, maxFlags: 6);

        // 把 VM 侧的状态同步到显示层（BoardChanged 的订阅者是窗口私有的，这里手动走一遍）
        var cells = (System.Collections.IEnumerable)vmType.GetProperty("Cells")!.GetValue(vm)!;
        int revealed = 0, flagged = 0;
        foreach (var cellVm in cells)
        {
            cellVm.GetType().GetMethod("Refresh")!.Invoke(cellVm, null);
            var state = cellVm.GetType().GetProperty("State")!.GetValue(cellVm)!.ToString();
            if (state == "Revealed") revealed++;
            else if (state == "Flagged") flagged++;
        }
        Console.WriteLine($"[截图] 盘面：已翻开 {revealed} 格，插旗 {flagged} 格");
    }

    private static void ClickCell(object vm, GameBoard board, int row, int col)
    {
        vm.GetType().GetMethod("OnLeftClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new[] { (object)board.Cells[row, col] });
    }

    /// <summary>用求解器找一个确定安全的格子翻开；返回 false 表示推不动了</summary>
    private static bool RevealOneCertainSafe(object vm, GameBoard board)
    {
        SolverService.Analyze(board, out var safe, out _);
        foreach (var (r, c) in safe)
        {
            if (board.Cells[r, c].State != CellState.Covered) continue;
            ClickCell(vm, board, r, c);
            return true;
        }
        return false;
    }

    private static void FlagSomeMines(object vm, GameBoard board, int maxFlags)
    {
        var onRight = vm.GetType().GetMethod("OnRightClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int flagged = 0;
        SolverService.Analyze(board, out _, out var mines);
        foreach (var (r, c) in mines)
        {
            if (flagged >= maxFlags) break;
            if (board.Cells[r, c].State != CellState.Covered) continue;
            onRight.Invoke(vm, new[] { (object)board.Cells[r, c] });
            flagged++;
        }
    }
}
