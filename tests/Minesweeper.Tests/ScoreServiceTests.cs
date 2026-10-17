using System.IO;
using Minesweeper.Models;
using Minesweeper.Services;
using Xunit;

namespace Minesweeper.Tests;

/// <summary>
/// 排行榜与统计的测试。
/// 每个测试用自己的临时目录，绝不碰 %AppData% 里的真实成绩。
/// </summary>
public class ScoreServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ScoreService _service;

    public ScoreServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "minesweeper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _service = new ScoreService(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败无所谓 */ }
    }

    [Fact]
    public void 胜利成绩会按用时升序进入排行榜()
    {
        Record("高级", 120.5);
        Record("高级", 90.2);
        Record("高级", 150.0);

        var stats = _service.GetStats("高级");
        Assert.Equal(3, stats.GamesPlayed);
        Assert.Equal(3, stats.GamesWon);
        Assert.Equal(3, stats.TopTimes.Count);
        Assert.Equal(90.2, stats.TopTimes[0].BestTimeSeconds);
        Assert.Equal(120.5, stats.TopTimes[1].BestTimeSeconds);
        Assert.Equal(150.0, stats.TopTimes[2].BestTimeSeconds);
    }

    [Fact]
    public void 排行榜最多保留十名且只留最快的()
    {
        for (int i = 0; i < 15; i++)
            Record("初级", 100 - i);   // 100, 99, ... 86

        var stats = _service.GetStats("初级");
        Assert.Equal(DifficultyStats.MaxRecords, stats.TopTimes.Count);
        Assert.Equal(86.0, stats.TopTimes[0].BestTimeSeconds);
        Assert.Equal(95.0, stats.TopTimes[^1].BestTimeSeconds);
    }

    [Fact]
    public void 失败会中断当前连胜但不影响最长连胜()
    {
        Record("中级", 60);
        Record("中级", 55);
        Assert.Equal(2, _service.GetStats("中级").CurrentStreak);

        _service.RecordGameStarted("中级");
        _service.RecordLoss("中级");

        var stats = _service.GetStats("中级");
        Assert.Equal(0, stats.CurrentStreak);
        Assert.Equal(2, stats.BestStreak);
        Assert.Equal(3, stats.GamesPlayed);
        Assert.Equal(2, stats.GamesWon);
        Assert.Equal(1, stats.GamesLost);
    }

    [Fact]
    public void 胜率与获胜平均用时计算正确()
    {
        Record("高级", 100);
        Record("高级", 200);
        _service.RecordGameStarted("高级");
        _service.RecordLoss("高级");

        var stats = _service.GetStats("高级");
        Assert.Equal(3, stats.GamesPlayed);      // 两胜一负
        Assert.Equal(2, stats.GamesWon);
        Assert.Equal(66.667, stats.WinRatePercent, 3);
        Assert.Equal(150.0, stats.AverageWinSeconds, 3);
    }

    [Fact]
    public void 新纪录判定正确()
    {
        Assert.True(_service.RecordWin("高级", 100), "首次成绩应算新纪录");
        Assert.False(_service.RecordWin("高级", 120), "更慢的成绩不该算新纪录");
        Assert.True(_service.RecordWin("高级", 99), "更快的成绩应算新纪录");
    }

    [Fact]
    public void 尚未玩过的难度返回空统计而不是null()
    {
        var stats = _service.GetStats("没玩过");
        Assert.Equal(0, stats.GamesPlayed);
        Assert.Empty(stats.TopTimes);
        Assert.Equal("暂无记录", _service.GetRecord("没玩过").Display);
    }

    [Fact]
    public void 能读取并迁移旧版本的记录格式()
    {
        // v1.1 及更早：每个难度直接是一条 { BestTimeSeconds, Date }
        const string legacy = """
            {"高级":{"BestTimeSeconds":146.7,"Date":"2026-08-24T13:48:41.8650221+08:00","Display":"146.7 秒"}}
            """;
        File.WriteAllText(Path.Combine(_dir, "records.json"), legacy);

        var reloaded = new ScoreService(_dir);
        var stats = reloaded.GetStats("高级");

        Assert.Single(stats.TopTimes);
        Assert.Equal(146.7, stats.TopTimes[0].BestTimeSeconds);
        Assert.Equal(146.7, stats.BestTimeSeconds);   // 兼容字段保留，旧版本仍能读到
        Assert.Equal("146.7 秒", reloaded.GetRecord("高级").Display);
    }

    [Fact]
    public void 新版本写出的文件仍能被旧版本读出最佳成绩()
    {
        Record("高级", 88.8);

        // 旧版本读的是 BestTimeSeconds / Date 这两个顶层字段
        var json = File.ReadAllText(Path.Combine(_dir, "records.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var node = document.RootElement.GetProperty("高级");

        Assert.True(node.TryGetProperty("BestTimeSeconds", out var best));
        Assert.Equal(88.8, best.GetDouble());
        Assert.True(node.TryGetProperty("TopTimes", out var top));
        Assert.Equal(1, top.GetArrayLength());
    }

    [Fact]
    public void 保存后再读取内容一致()
    {
        Record("中级", 42.5);
        _service.RecordGameStarted("中级");
        _service.RecordLoss("中级");

        var reloaded = new ScoreService(_dir);
        var stats = reloaded.GetStats("中级");

        Assert.Equal(2, stats.GamesPlayed);
        Assert.Equal(1, stats.GamesWon);
        Assert.Equal(42.5, stats.TopTimes[0].BestTimeSeconds);
        Assert.Equal(0, stats.CurrentStreak);
        Assert.Equal(1, stats.BestStreak);
    }

    private void Record(string difficulty, double seconds)
    {
        _service.RecordGameStarted(difficulty);
        _service.RecordWin(difficulty, seconds);
    }
}
