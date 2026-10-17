using System.Text.Json.Serialization;

namespace Minesweeper.Models;

/// <summary>某个难度的排行榜与统计</summary>
public class DifficultyStats
{
    public const int MaxRecords = 10;

    /// <summary>最快的前 N 次成绩（按用时升序）</summary>
    public List<BestRecord> TopTimes { get; set; } = new();

    public int GamesPlayed { get; set; }
    public int GamesWon { get; set; }

    /// <summary>没分胜负就重开的局数（单独计，不算失败）</summary>
    public int GamesAbandoned { get; set; }

    public int CurrentStreak { get; set; }
    public int BestStreak { get; set; }
    public double TotalWinSeconds { get; set; }

    // ---- 兼容 v1.1 及更早版本的存储格式 ----
    // 旧版本只认这两个字段。为了让新旧版本能互相读取对方写出的文件，
    // 读取时会把它们迁移进 TopTimes，写入时也始终与榜首保持一致。
    public double? BestTimeSeconds { get; set; }

    public DateTime? Date { get; set; }

    // ---- 计算属性（不序列化） ----
    [JsonIgnore] public BestRecord? Best => TopTimes.Count > 0 ? TopTimes[0] : null;

    /// <summary>分出胜负的局数（弃局不算在里面）</summary>
    [JsonIgnore] public int GamesDecided => Math.Max(0, GamesPlayed - GamesAbandoned);

    [JsonIgnore] public int GamesLost => Math.Max(0, GamesDecided - GamesWon);

    [JsonIgnore] public double WinRatePercent => GamesDecided == 0 ? 0 : GamesWon * 100.0 / GamesDecided;

    [JsonIgnore] public double AverageWinSeconds => GamesWon == 0 ? 0 : TotalWinSeconds / GamesWon;
}
