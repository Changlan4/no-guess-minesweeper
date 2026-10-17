using System.Text.Json.Serialization;

namespace Minesweeper.Models;

public class BestRecord
{
    public double? BestTimeSeconds { get; set; }
    public DateTime? Date { get; set; }

    /// <summary>
    /// 显示文本。计算属性，不参与序列化 —— 否则 records.json 里会多存一份
    /// 可能与 BestTimeSeconds 不一致的陈旧文本（旧版本就是这么写的）。
    /// </summary>
    [JsonIgnore]
    public string Display => BestTimeSeconds.HasValue
        ? $"{BestTimeSeconds.Value:F1} 秒"
        : "暂无记录";
}
