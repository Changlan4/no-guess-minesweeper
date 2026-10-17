using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Minesweeper.Controls;
using Minesweeper.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>
/// 渲染成本基准。
///
/// 目的：用数据判断「每次重绘整块棋盘」是否够用，还是必须改成按脏格分层的保留模式重绘。
/// 动画期间是 16ms 一帧连续重绘，只要单帧成本明显低于 16.7ms，全盘重绘就是可接受的。
///
/// 注意：这里用的是 RenderTargetBitmap（软件光栅化到位图），比真实上屏渲染更慢，
/// 所以测出来的是**成本上界**。全程不显示窗口。
/// </summary>
public class RenderBenchmarkTests
{
    private readonly ITestOutputHelper _out;

    public RenderBenchmarkTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void 全盘重绘的单帧成本在60fps预算内()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(900, 580));
            root.Arrange(new Rect(0, 0, 900, 580));
            root.UpdateLayout();

            var board = StaTestRunner.FindDescendant<BoardCanvas>(root);
            Assert.NotNull(board);

            // 让格子回到真实使用尺寸（窗口未显示时 ActualWidth 为 0，会退到最小尺寸）
            if (window.DataContext is MainViewModel vm) vm.CellSize = 30;
            root.UpdateLayout();

            int cellCount = board!.Cols * board.Rows;
            _out.WriteLine($"棋盘 {board.Cols}×{board.Rows} = {cellCount} 格，格子 {board.CellSize:F0}px");
            _out.WriteLine($"WPF 渲染层级 Tier={RenderCapability.Tier >> 16}（0=软件渲染，动画会自动降级）");

            var bitmap = new RenderTargetBitmap(900, 580, 96, 96, PixelFormats.Pbgra32);

            // 预热（首次渲染要构建渲染指令与字体缓存）
            for (int i = 0; i < 10; i++)
            {
                board.InvalidateVisual();
                bitmap.Render(root);
            }

            const int frames = 40;

            // A：视觉树不脏时的重绘
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) bitmap.Render(root);
            sw.Stop();
            double idleMs = sw.Elapsed.TotalMilliseconds / frames;

            // B：全盘重绘（动画期间每一帧都是这样）
            sw.Restart();
            for (int i = 0; i < frames; i++)
            {
                board.InvalidateVisual();
                bitmap.Render(root);
            }
            sw.Stop();
            double fullMs = sw.Elapsed.TotalMilliseconds / frames;

            // C：对照 —— 旧实现每帧要重建的对象成本（缓存优化到底省了多少）
            var typeface = new Typeface(new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var brush = Brushes.Black;

            sw.Restart();
            for (int i = 0; i < frames; i++)
                for (int k = 0; k < cellCount; k++)
                    _ = new FormattedText((k % 8 + 1).ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 15, brush, 1.0);
            sw.Stop();
            double legacyTextMs = sw.Elapsed.TotalMilliseconds / frames;

            sw.Restart();
            for (int i = 0; i < frames; i++)
                for (int k = 0; k < cellCount * 2; k++)
                    _ = new Pen(brush, 2);
            sw.Stop();
            double legacyPenMs = sw.Elapsed.TotalMilliseconds / frames;

            _out.WriteLine($"A 未变化重绘            {idleMs:F2} ms/帧");
            _out.WriteLine($"B 全盘重绘（动画帧）    {fullMs:F2} ms/帧   占 60fps 预算 {fullMs / 16.7 * 100:F0}%");
            _out.WriteLine($"C 旧实现多花的钱：每帧新建 {cellCount} 个 FormattedText ≈ {legacyTextMs:F2} ms，" +
                            $"{cellCount * 2} 个 Pen ≈ {legacyPenMs:F2} ms，合计 ≈ {legacyTextMs + legacyPenMs:F2} ms/帧");

            // D：纯绘制指令录制（不含光栅化）—— 用来判断瓶颈到底在「录制」还是在「光栅化」
            var probe = new RenderProbe
            {
                Cells = board.Cells,
                Cols = board.Cols,
                Rows = board.Rows,
                CellSize = board.CellSize
            };

            for (int i = 0; i < 20; i++) RecordOnce(probe);
            sw.Restart();
            const int records = 300;
            for (int i = 0; i < records; i++) RecordOnce(probe);
            sw.Stop();
            double recordMs = sw.Elapsed.TotalMilliseconds / records;
            _out.WriteLine($"D 纯指令录制            {recordMs:F3} ms/帧（不含光栅化）");
            _out.WriteLine($"   → 录制只占全盘重绘的 {recordMs / fullMs * 100:F1}%，瓶颈在光栅化而非绘制指令");

            window.Close();

            // 结论性断言：动画需要在 16.7ms 内完成一帧
            Assert.True(fullMs < 16.7,
                $"全盘重绘 {fullMs:F1} ms/帧，已超出 60fps 预算，需要改为按脏格分层重绘");
        });
    }

    /// <summary>把 OnRender 暴露出来，好在不接视觉树的情况下单独测量绘制指令录制成本</summary>
    private sealed class RenderProbe : BoardCanvas
    {
        public void Record(DrawingContext dc) => OnRender(dc);
    }

    private static void RecordOnce(RenderProbe probe)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        probe.Record(dc);
    }
}
