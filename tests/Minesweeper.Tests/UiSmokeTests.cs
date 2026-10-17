using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Minesweeper.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>
/// UI 冒烟测试：在 STA 线程上真正构造主窗口并渲染成位图。
///
/// 全程不调用 Window.Show()，所以不会弹出任何窗口打扰使用者；
/// 但会真实执行 XAML 解析、数据绑定和 BoardCanvas.OnRender 的全部绘制代码，
/// 能抓住「XAML 写错」「绘制代码抛异常」「资源键写错」这类只在运行时才暴露的问题。
/// </summary>
public class UiSmokeTests
{
    private readonly ITestOutputHelper _out;

    public UiSmokeTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] RequiredThemeKeys =
    {
        "WindowBackground", "BoardBackground", "StatusBarBg", "TextPrimary",
        "CellCoveredFace", "CellRevealed", "CellHighlight", "CellShadow",
        "FlagColor", "FlashColor", "AccentColor", "WrongFlagColor", "ExplodedCell",
        "Num1", "Num2", "Num3", "Num4", "Num5", "Num6", "Num7", "Num8"
    };

    [Fact]
    public void 主窗口能构造并完成多次完整渲染()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(900, 580));
            root.Arrange(new Rect(0, 0, 900, 580));
            root.UpdateLayout();

            // 连续渲染多帧，覆盖全盘重绘路径（动画期间就是这种节奏）
            for (int frame = 0; frame < 3; frame++)
            {
                var bitmap = new RenderTargetBitmap(900, 580, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                Assert.True(bitmap.PixelWidth > 0, "渲染结果尺寸异常");
                Assert.True(bitmap.PixelHeight > 0, "渲染结果尺寸异常");
            }

            window.Close();
        });
    }

    [Fact]
    public void 大窗口下棋盘会跟着放大而不是只占中间一小块()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;

            // 模拟最大化到一块大屏
            root.Measure(new Size(2000, 1200));
            root.Arrange(new Rect(0, 0, 2000, 1200));
            root.UpdateLayout();

            var board = StaTestRunner.FindDescendant<BoardCanvas>(root);
            Assert.NotNull(board);

            // 旧实现把格子卡在 48px 上限，大窗口下棋盘只占中间一小块
            Assert.True(board!.CellSize > 48,
                $"2000px 宽的窗口里格子只有 {board.CellSize:F1}px，棋盘没有跟着放大");

            double boardWidth = board.CellSize * board.Cols;
            Assert.True(boardWidth > 2000 * 0.8,
                $"棋盘宽 {boardWidth:F0}px，在 2000px 的窗口里仍然只占一小块");

            window.Close();
        });
    }

    [Theory]
    [InlineData("MintNoir")]
    [InlineData("RoseNoir")]
    [InlineData("VioletNoir")]
    [InlineData("OceanNoir")]
    public void 每套主题都含有绘制代码需要的全部资源键(string themeName)
    {
        // 主题是以 BAML 形式嵌在程序集里的，所以直接切过去验证运行时真实加载结果，
        // 而不是去读磁盘文件（主题文件根本不会被复制到输出目录）。
        StaTestRunner.Run(() =>
        {
            var app = StaTestRunner.EnsureApplication();
            Services.ThemeService.ApplyTheme(themeName);

            foreach (var key in RequiredThemeKeys)
                Assert.True(app.TryFindResource(key) != null, $"主题 {themeName} 缺少资源键 {key}");

            _out.WriteLine($"{themeName}：{RequiredThemeKeys.Length} 个必需资源键全部就位");
        });
    }
}
