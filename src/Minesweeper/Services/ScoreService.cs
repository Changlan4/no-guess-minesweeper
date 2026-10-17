using System.IO;
using System.Text.Json;
using Minesweeper.Models;

namespace Minesweeper.Services;

/// <summary>
/// 成绩服务 —— JSON 持久化排行榜与统计
/// </summary>
public class ScoreService
{
    /// <summary>默认存储目录（%AppData%\Minesweeper）</summary>
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minesweeper");

    private readonly string _recordsPath;

    private Dictionary<string, DifficultyStats> _stats = new();

    public ScoreService() : this(DirPath) { }

    /// <summary>指定存储目录 —— 单元测试传临时目录，避免污染真实成绩</summary>
    public ScoreService(string directory)
    {
        _recordsPath = Path.Combine(directory, "records.json");
        Load();
    }

    // ==================== 查询 ====================

    public DifficultyStats GetStats(string difficultyName) =>
        _stats.TryGetValue(difficultyName, out var s) ? s : new DifficultyStats();

    public BestRecord GetRecord(string difficultyName) => GetStats(difficultyName).Best ?? new BestRecord();

    // ==================== 记录 ====================

    /// <summary>一局开始（用于统计总局数）</summary>
    public void RecordGameStarted(string difficultyName)
    {
        EnsureStats(difficultyName).GamesPlayed++;
        Save();
    }

    /// <summary>一局胜利：计入排行榜、更新连胜与平均用时；返回是否刷新了最佳成绩</summary>
    public bool RecordWin(string difficultyName, double seconds)
    {
        var stats = EnsureStats(difficultyName);
        stats.GamesWon++;
        stats.TotalWinSeconds += seconds;
        stats.CurrentStreak++;
        if (stats.CurrentStreak > stats.BestStreak) stats.BestStreak = stats.CurrentStreak;

        // 判定与存储都用取整后的值，否则 10.04 存成 10.0、下次 9.98 会弹「新纪录」，
        // 但榜单上新旧两条都显示 10.0 秒，玩家会以为纪录没更新。
        double recorded = Math.Round(seconds, 1);
        bool isNewBest = stats.Best?.BestTimeSeconds is not { } best || recorded < best;

        stats.TopTimes.Add(new BestRecord
        {
            BestTimeSeconds = recorded,
            Date = DateTime.Now
        });
        stats.TopTimes = stats.TopTimes
            .OrderBy(r => r.BestTimeSeconds ?? double.MaxValue)
            .Take(DifficultyStats.MaxRecords)
            .ToList();

        // 同步兼容字段：旧版本读同一份文件时依然能看到最佳成绩（方便回退对比）
        stats.BestTimeSeconds = stats.TopTimes[0].BestTimeSeconds;
        stats.Date = stats.TopTimes[0].Date;

        Save();
        return isNewBest;
    }

    /// <summary>一局失败：连胜中断</summary>
    public void RecordLoss(string difficultyName)
    {
        EnsureStats(difficultyName).CurrentStreak = 0;
        Save();
    }

    /// <summary>
    /// 一局中途放弃（还没分胜负就重开/换难度）。
    /// 单独计数，而不是混进「失败」里 —— 否则每次弃局都会拉低胜率。
    /// </summary>
    public void RecordGameAbandoned(string difficultyName)
    {
        var stats = EnsureStats(difficultyName);
        stats.GamesAbandoned++;
        stats.CurrentStreak = 0;
        Save();
    }

    private DifficultyStats EnsureStats(string difficultyName)
    {
        if (!_stats.TryGetValue(difficultyName, out var stats))
            _stats[difficultyName] = stats = new DifficultyStats();
        return stats;
    }

    // ==================== 主题持久化 ====================

    /// <summary>
    /// 设置文件的默认路径。
    /// 注意：测试必须传自己的目录（各方法都有可选的 directory 参数）——
    /// 否则跑一次单元测试就会把玩家真实的窗口尺寸/主题覆盖掉。
    /// </summary>
    private static string DefaultSettingsPath => Path.Combine(DirPath, "settings.json");

    private static string SettingsPathOf(string? directory) =>
        string.IsNullOrEmpty(directory) ? DefaultSettingsPath : Path.Combine(directory, "settings.json");

    public static string LoadTheme(string? directory = null)
    {
        try
        {
            var settings = LoadSettings(directory);
            if (settings.TryGetValue("theme", out var theme) && !string.IsNullOrWhiteSpace(theme))
                return theme;
        }
        catch { }
        return "MacaronMint";
    }

    public static void SaveTheme(string themeName, string? directory = null)
    {
        try
        {
            var path = SettingsPathOf(directory);
            EnsureDirectory(path);
            var settings = LoadSettings(directory);
            settings["theme"] = themeName;
            WriteAtomic(path, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch { }
    }

    // ==================== 窗口状态持久化 ====================

    private const double DefaultWindowWidth = 900;
    private const double DefaultWindowHeight = 580;
    private const double MinUsableSize = 320;
    private const double MaxUsableSize = 20000;

    /// <summary>
    /// 判断一个窗口尺寸是否可用。
    /// 必要性：窗口在最大化状态下关闭时 <c>Window.RestoreBounds</c> 可能是 <c>Rect.Empty</c>，
    /// 而 <c>Rect.Empty.Width</c> 恰好是 double.NegativeInfinity，会被原样存成 "-∞"。
    /// 下次启动把它赋给 <c>Window.Width</c> 会直接抛异常，游戏打不开。
    /// </summary>
    public static bool IsUsableWindowSize(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) && v >= MinUsableSize && v <= MaxUsableSize;

    public static (double w, double h, int state) LoadWindowState(string? directory = null)
    {
        try
        {
            var settings = LoadSettings(directory);
            double w = settings.TryGetValue("winW", out var sw) && double.TryParse(sw, out var pw) ? pw : DefaultWindowWidth;
            double h = settings.TryGetValue("winH", out var sh) && double.TryParse(sh, out var ph) ? ph : DefaultWindowHeight;
            int state = settings.TryGetValue("winState", out var ss) && int.TryParse(ss, out var ps) ? ps : 0;

            // 挡住历史遗留的坏值，否则窗口构造阶段就会崩
            if (!IsUsableWindowSize(w)) w = DefaultWindowWidth;
            if (!IsUsableWindowSize(h)) h = DefaultWindowHeight;
            if (state != 0 && state != 2) state = 0;

            return (w, h, state);
        }
        catch { return (DefaultWindowWidth, DefaultWindowHeight, 0); }
    }

    public static void SaveWindowState(double width, double height, int windowState, string? directory = null)
    {
        try
        {
            // 无效尺寸一律不落盘，避免把 -∞ / NaN 传染给下一次启动
            if (!IsUsableWindowSize(width)) width = DefaultWindowWidth;
            if (!IsUsableWindowSize(height)) height = DefaultWindowHeight;
            if (windowState != 0 && windowState != 2) windowState = 0;

            var path = SettingsPathOf(directory);
            EnsureDirectory(path);
            var settings = LoadSettings(directory);
            settings["winW"] = width.ToString("F0");
            settings["winH"] = height.ToString("F0");
            settings["winState"] = windowState.ToString();
            WriteAtomic(path, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch { }
    }

    private static Dictionary<string, string> LoadSettings(string? directory = null)
    {
        try
        {
            var path = SettingsPathOf(directory);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
        }
        catch { }
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// 原子写入：先写同目录的临时文件，再整体替换目标。
    /// 直接覆盖写的话，进程在写一半时被杀 / 断电会留下截断的 JSON，
    /// 下次启动读取抛异常，整份数据就没了。
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    // ==================== 排行榜读写 ====================

    /// <summary>落盘用的统一序列化选项（紧凑输出，避免整份文件被缩进撑大）</summary>
    private static readonly JsonSerializerOptions WriteOptions = new();

    private void Load()
    {
        string json;
        try
        {
            if (!File.Exists(_recordsPath)) return;
            json = File.ReadAllText(_recordsPath);
        }
        catch { return; }

        Dictionary<string, DifficultyStats>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Dictionary<string, DifficultyStats>>(json);
        }
        catch
        {
            // 整个文件都坏了：保留空统计，但把坏文件改名留档，
            // 免得下一次 Save 直接把它覆盖成空数据、再也追不回来。
            TryKeepCorruptCopy();
            return;
        }

        if (loaded == null) return;

        foreach (var (difficulty, stats) in loaded)
        {
            // 逐个难度容错：某个条目坏掉不该让其他难度的成绩一起消失
            if (string.IsNullOrEmpty(difficulty) || stats == null) continue;

            try
            {
                Normalize(stats);
                _stats[difficulty] = stats;
            }
            catch { /* 单条坏数据，跳过 */ }
        }
    }

    /// <summary>把单条统计里的可空/异常字段归一，避免读取阶段就抛异常</summary>
    private static void Normalize(DifficultyStats stats)
    {
        stats.TopTimes ??= new List<BestRecord>();
        stats.TopTimes.RemoveAll(r => r == null);
        if (stats.TopTimes.Count > DifficultyStats.MaxRecords)
            stats.TopTimes = stats.TopTimes.OrderBy(r => r.BestTimeSeconds ?? double.MaxValue)
                                           .Take(DifficultyStats.MaxRecords).ToList();

        // 旧格式迁移：老文件里每个难度直接是一条 { BestTimeSeconds, Date }
        // 兼容字段本身保留不动，旧版本还要靠它显示成绩
        if (stats.TopTimes.Count == 0 && stats.BestTimeSeconds.HasValue)
        {
            stats.TopTimes.Add(new BestRecord
            {
                BestTimeSeconds = stats.BestTimeSeconds,
                Date = stats.Date
            });
        }

        if (stats.GamesPlayed < 0) stats.GamesPlayed = 0;
        if (stats.GamesWon < 0) stats.GamesWon = 0;
        if (stats.GamesAbandoned < 0) stats.GamesAbandoned = 0;
        if (stats.TotalWinSeconds < 0) stats.TotalWinSeconds = 0;
    }

    /// <summary>文件损坏时留一份 .corrupt 副本，方便事后找回成绩</summary>
    private void TryKeepCorruptCopy()
    {
        try
        {
            var backup = _recordsPath + ".corrupt";
            if (!File.Exists(backup)) File.Copy(_recordsPath, backup);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            EnsureDirectory(_recordsPath);
            WriteAtomic(_recordsPath, JsonSerializer.Serialize(_stats, WriteOptions));
        }
        catch { /* 静默失败，不影响游戏 */ }
    }
}
