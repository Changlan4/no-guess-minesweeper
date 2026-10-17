using System.IO;
using System.Text.Json;
using Minesweeper.Models;
using Minesweeper.Services;
using Xunit;

namespace Minesweeper.Tests;

/// <summary>
/// 配置读写相关的回归测试。
///
/// 背景：窗口在最大化状态下关闭时，Window.RestoreBounds 可能是 Rect.Empty，
/// 而 Rect.Empty.Width 就是 double.NegativeInfinity —— 原实现会把它格式化成 "-∞"
/// 写进 settings.json，下次启动读取后赋给 Window.Width 直接抛异常（游戏打不开）。
/// 这两道校验就是为这个真实事故加的。
///
/// 注意：所有涉及 settings.json 的用例都必须传自己的临时目录。
/// 这些方法原来没有目录参数，测试直接写玩家的 %AppData% —— 跑一次单元测试
/// 就会把玩家保存的窗口尺寸/最大化状态覆盖掉。
/// </summary>
public class ConfigTests : IDisposable
{
    private readonly string _dir;

    public ConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "minesweeper-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败无所谓 */ }
    }

    [Theory]
    [InlineData(double.NegativeInfinity, false)]   // ← Rect.Empty.Width 的真实取值
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NaN, false)]
    [InlineData(0, false)]
    [InlineData(-100, false)]
    [InlineData(100, false)]
    [InlineData(900, true)]
    [InlineData(1920, true)]
    [InlineData(3840, true)]
    public void 窗口尺寸校验能挡掉无效值(double value, bool expected)
    {
        Assert.Equal(expected, ScoreService.IsUsableWindowSize(value));
    }

    [Fact]
    public void 存盘的窗口尺寸永远不会是无穷或NaN()
    {
        // 走真实读写路径，但写进本测试自己的临时目录
        ScoreService.SaveWindowState(double.NegativeInfinity, double.NaN, 7, _dir);
        var (w, h, state) = ScoreService.LoadWindowState(_dir);

        Assert.True(ScoreService.IsUsableWindowSize(w), $"读回的宽度不合法：{w}");
        Assert.True(ScoreService.IsUsableWindowSize(h), $"读回的高度不合法：{h}");
        Assert.True(state is 0 or 2, $"读回的窗口状态不合法：{state}");
    }

    [Fact]
    public void 窗口尺寸与主题写在同一个文件里互不覆盖()
    {
        ScoreService.SaveWindowState(1234, 777, 2, _dir);
        ScoreService.SaveTheme("VioletNoir", _dir);

        // 读回窗口状态：不能被随后的主题写入挤掉
        var (w, h, state) = ScoreService.LoadWindowState(_dir);
        Assert.Equal(1234, w);
        Assert.Equal(777, h);
        Assert.Equal(2, state);

        // 再写一次窗口状态，主题也不能丢
        ScoreService.SaveWindowState(1000, 700, 0, _dir);
        Assert.Equal("VioletNoir", ScoreService.LoadTheme(_dir));
    }

    [Fact]
    public void 设置文件损坏时回退到默认值而不是抛异常()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ 这不是合法 JSON");

        var (w, h, state) = ScoreService.LoadWindowState(_dir);
        Assert.Equal(900, w);
        Assert.Equal(580, h);
        Assert.Equal(0, state);
    }

    [Fact]
    public void 设置文件里的坏值会被挡掉()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"winW":"-∞","winH":"NaN","winState":"7","theme":"  "}""");

        var (w, h, state) = ScoreService.LoadWindowState(_dir);
        Assert.True(ScoreService.IsUsableWindowSize(w));
        Assert.True(ScoreService.IsUsableWindowSize(h));
        Assert.Equal(0, state);
        // 空字符串主题名要回退到默认
        Assert.Equal("MacaronMint", ScoreService.LoadTheme(_dir));
    }

    [Theory]
    [InlineData("MintNoir", "MintNoir")]        // 当前主题名直接命中
    [InlineData("mintnoir", "MintNoir")]        // 大小写不敏感
    [InlineData("SeaSaltBlue", "OceanNoir")]    // 上一轮的浅色主题 → 语义最接近的暗色主题
    [InlineData("MacaronMint", "MintNoir")]
    [InlineData("Lavender", "VioletNoir")]
    [InlineData("ClassicGray", "MintNoir")]     // 更早的主题名
    [InlineData("OceanBlue", "OceanNoir")]
    [InlineData("早就删掉的名字", "MintNoir")]   // 认不出来就回退默认
    public void 历史主题名会映射到最接近的当前主题(string saved, string expected)
    {
        Assert.Equal(expected, ThemeService.Resolve(saved).Name);
    }

    [Fact]
    public void 空主题名回退到默认主题()
    {
        Assert.Equal(ThemeService.Themes[0].Name, ThemeService.Resolve(null).Name);
        Assert.Equal(ThemeService.Themes[0].Name, ThemeService.Resolve("").Name);
    }

    [Fact]
    public void 成绩记录不会把显示文本写进文件()
    {
        var record = new BestRecord { BestTimeSeconds = 12.3, Date = new DateTime(2026, 1, 1) };
        var json = JsonSerializer.Serialize(record);

        Assert.Contains("BestTimeSeconds", json);
        Assert.DoesNotContain("Display", json);

        // 旧版本写过的文件里带 Display 字段，读取时必须兼容（多余字段直接忽略）
        const string legacy = """
            {"BestTimeSeconds":12.3,"Date":"2026-01-01T00:00:00","Display":"12.3 秒"}
            """;
        var restored = JsonSerializer.Deserialize<BestRecord>(legacy);

        Assert.NotNull(restored);
        Assert.Equal(12.3, restored!.BestTimeSeconds);
        Assert.Equal("12.3 秒", restored.Display);
    }
}
