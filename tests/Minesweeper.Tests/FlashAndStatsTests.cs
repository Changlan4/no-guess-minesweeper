using System.IO;
using System.Reflection;
using Minesweeper.Models;
using Minesweeper.Services;
using Minesweeper.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>
/// 修复后的行为回归测试。
///
/// 这里的两条重点用例是为两个真实缺陷加的：
///   1. 和弦闪黄原本闪烁着就变成了「闪两下」—— OnFlashTick 里算出来的 on 从未参与判断，
///      实际执行的是无条件的 IsFlashing 取反。这里把「只允许一次亮起」钉死。
///   2. 胜利收尾直接改 Cell.State、绕过 ToggleFlag，导致 FlaggedCount 不同步、
///      状态栏的剩余雷数永远不归零。
///
/// 两条都是纯逻辑断言，不构造窗口、不弹窗。
/// </summary>
public class FlashAndStatsTests
{
    private readonly ITestOutputHelper _out;

    public FlashAndStatsTests(ITestOutputHelper output) => _out = output;

    // ==================== 和弦闪烁：只闪一下 ====================

    [Fact]
    public void 和弦闪烁只亮起一次()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var vm = new MainViewModel();
            var board = new GameBoard(Difficulty.Beginner);

            // 造三个未翻开的格子，并把它们登记进 VM 的 O(1) 索引
            var targets = new[] { (0, 0), (0, 1), (1, 0) };
            var cells = new List<Cell>();
            foreach (var (r, c) in targets)
            {
                cells.Add(board.Cells[r, c]);
                IndexCell(vm, board.Cells[r, c]);
            }

            // 直接驱动 StartFlashing（OnChord 的前置条件太重，这里只测闪烁状态机本身）
            Invoke(vm, "StartFlashing", cells);
            var afterStart = FlashState(vm, targets);
            _out.WriteLine($"StartFlashing 之后：亮着的格子 = {afterStart.Count(x => x)} / {afterStart.Length}，" +
                           $"VM 记录的待闪格子 = {FlashingCount(vm)}");
            Assert.Contains(true, afterStart);   // 起始应当是亮着的

            // 统计整段过程里「亮起」出现的次数：
            // 起始就是亮的，所以从 1 开始数；此后任何一帧重新亮起都会让它变大。
            // 修好之前，第 2 次 Tick 会把熄灭的格子重新点亮 → 计数变成 2。
            int onEpisodes = 1;
            bool[] prev = afterStart;

            // 连跑 6 次 Tick：修好之后第一次就该收尾，后面全是熄灭
            for (int i = 0; i < 6; i++)
            {
                Invoke(vm, "OnFlashTick", null, EventArgs.Empty);

                var now = FlashState(vm, targets);
                bool anyRising = false;
                for (int k = 0; k < now.Length; k++)
                    if (now[k] && !prev[k]) anyRising = true;
                if (anyRising) onEpisodes++;

                _out.WriteLine($"Tick {i + 1}：亮着的格子 = {now.Count(x => x)}，重新亮起 = {anyRising}，累计亮起次数 = {onEpisodes}");
                prev = now;
            }

            Assert.Equal(1, onEpisodes);           // 全程只允许亮起一次
            Assert.DoesNotContain(true, prev);     // 收尾之后必须全部熄灭
            _out.WriteLine($"闪烁全程只亮起 {onEpisodes} 次，且结束态为熄灭");
        });
    }

    // ==================== 胜利收尾：剩余雷数归零 ====================

    [Fact]
    public void 胜利收尾后剩余雷数归零()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);                       // 首击：布雷 + 展开一片

        // 复刻 OnGameWon 的收尾：所有雷补旗、所有安全格翻开（直接改 State，不走 ToggleFlag）
        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                var cell = board.Cells[r, c];
                if (cell.IsMine) cell.State = CellState.Flagged;
                else cell.State = CellState.Revealed;
            }

        board.SyncFlaggedCount();

        Assert.Equal(board.MineCount, board.FlaggedCount);
        Assert.Equal(0, board.MineCount - board.FlaggedCount);   // 状态栏显示的就是这个差值
        _out.WriteLine($"胜利收尾后：雷 {board.MineCount}，已标旗 {board.FlaggedCount}，剩余 {board.MineCount - board.FlaggedCount}");
    }

    [Fact]
    public void 手动标旗的计数不会被同步逻辑改坏()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);

        int flagged = 0;
        for (int r = 0; r < board.Rows && flagged < 3; r++)
            for (int c = 0; c < board.Cols && flagged < 3; c++)
                if (board.Cells[r, c].State == CellState.Covered && board.ToggleFlag(r, c)) flagged++;

        Assert.Equal(flagged, board.FlaggedCount);

        board.SyncFlaggedCount();                  // 重算应当得到同样的结果
        Assert.Equal(flagged, board.FlaggedCount);
    }

    // ==================== 弃局不再拉低胜率 ====================

    [Fact]
    public void 弃局不计入失败也不拉低胜率()
    {
        var dir = Path.Combine(Path.GetTempPath(), "minesweeper-abandon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new ScoreService(dir);
            service.RecordGameStarted("高级");
            service.RecordGameStarted("高级");
            service.RecordGameStarted("高级");
            service.RecordGameAbandoned("高级");   // 第二局中途重开
            service.RecordWin("高级", 100);
            service.RecordLoss("高级");             // 第三局踩雷

            var stats = service.GetStats("高级");
            Assert.Equal(3, stats.GamesPlayed);
            Assert.Equal(1, stats.GamesAbandoned);
            Assert.Equal(1, stats.GamesWon);
            Assert.Equal(1, stats.GamesLost);            // 弃局不算失败
            Assert.Equal(50.0, stats.WinRatePercent);    // 1 胜 / (3 局 - 1 弃局)
            _out.WriteLine($"弃局统计：{stats.GamesPlayed} 局，胜 {stats.GamesWon}，负 {stats.GamesLost}，弃 {stats.GamesAbandoned}，胜率 {stats.WinRatePercent:F1}%");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ==================== 新纪录判定与存储口径一致 ====================

    [Fact]
    public void 新纪录判定与存储都用同一位小数()
    {
        var dir = Path.Combine(Path.GetTempPath(), "minesweeper-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new ScoreService(dir);

            // 10.04 存进去是 10.0
            Assert.True(service.RecordWin("高级", 10.04));
            Assert.Equal(10.0, service.GetStats("高级").TopTimes[0].BestTimeSeconds);

            // 9.98 存进去也是 10.0（同样四舍五入到一位），所以它不该被判定成新纪录 ——
            // 否则会弹出「新纪录」，可榜单上新旧两条都显示 10.0 秒，玩家会以为没更新
            Assert.False(service.RecordWin("高级", 9.98));

            // 9.94 → 9.9，确实更快，才算新纪录
            Assert.True(service.RecordWin("高级", 9.94));
            Assert.Equal(9.9, service.GetStats("高级").TopTimes[0].BestTimeSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void 排行榜文件损坏时保留副本而不是静默清空()
    {
        var dir = Path.Combine(Path.GetTempPath(), "minesweeper-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var records = Path.Combine(dir, "records.json");
            File.WriteAllText(records, "{ 这不是合法 JSON");

            var service = new ScoreService(dir);          // 读取时不抛异常
            Assert.Equal(0, service.GetStats("高级").GamesPlayed);

            service.RecordGameStarted("高级");            // 触发一次覆盖写
            Assert.True(File.Exists(records + ".corrupt"), "坏文件应该被留档，方便事后找回成绩");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ==================== 提示高亮的清理方式（缺陷 2 的防回归） ====================

    [Fact]
    public void 提示动画的清理不能用Storyboard_Remove()
    {
        // 契约：FlashHint / ClearHint 必须用 Storyboard.Stop 清理旧动画，而不是 Remove ——
        // Remove 会让该 storyboard 的 Completed 在下一次 Begin 启动新动画的那一刻再触发一次，
        // 那次迟到的回调会把刚设好的 HintCell 清成 (-1,-1)，
        // 于是「重复按 H 只有第一次显示」。
        //
        // 这里刻意直接检查源码：真正要跑的验证需要真实渲染循环（会开窗口），
        // 而这条断言的目的是防止有人把 Remove 写回去。
        var file = FindSourceFile(@"src\Minesweeper\Controls\BoardCanvas.cs");
        var source = File.ReadAllText(file);

        var flash = ExtractMemberBody(source, "public void FlashHint(int row, int col)");
        var clear = ExtractMemberBody(source, "public void ClearHint()");

        Assert.DoesNotContain(".Remove(this)", flash);
        Assert.DoesNotContain(".Remove(this)", clear);

        // 还要有「身份守卫」：即使将来真出现迟到的回调，也不许它动共享状态
        Assert.Contains("ReferenceEquals(_hintStoryboard, storyboard)", flash);
        Assert.Contains("_hintStoryboard?.Stop(this)", flash);
        Assert.Contains("_hintStoryboard?.Stop(this)", clear);
    }

    /// <summary>从测试输出目录往上找到源码树里的文件</summary>
    private static string FindSourceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"从 {AppContext.BaseDirectory} 往上找不到 {relative}");
    }

    /// <summary>按大括号配对截出某个成员的完整函数体</summary>
    private static string ExtractMemberBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"源码里找不到 {signature}");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"{signature} 后面没有函数体");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException($"{signature} 的大括号不配对");
    }

    // ==================== 辅助 ====================

    private static void IndexCell(MainViewModel vm, Cell cell)
    {
        var field = typeof(MainViewModel).GetField("_cellIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var index = field.GetValue(vm)!;
        var vmCell = new CellViewModel(cell, _ => { }, _ => { }, _ => { });
        index.GetType().GetMethod("Add")!.Invoke(index, new object[] { (cell.Row, cell.Col), vmCell });
    }

    /// <summary>读 VM 内部记录的"待闪格子"数量，用来区分「没登记上」和「登记了但没点亮」</summary>
    private static int FlashingCount(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_flashingCells", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = (System.Collections.ICollection)field.GetValue(vm)!;
        return list.Count;
    }

    private static bool[] FlashState(MainViewModel vm, (int, int)[] targets)    {
        var field = typeof(MainViewModel).GetField("_cellIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var index = field.GetValue(vm)!;
        var tryGet = index.GetType().GetMethod("TryGetValue")!;

        var result = new bool[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            var args = new object?[] { targets[i], null };
            tryGet.Invoke(index, args);
            var cellVm = args[1]!;
            result[i] = (bool)cellVm.GetType().GetProperty("IsFlashing")!.GetValue(cellVm)!;
        }
        return result;
    }

    private static void Invoke(MainViewModel vm, string method, params object?[] args)
    {
        try
        {
            typeof(MainViewModel)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, args);
        }
        catch (TargetInvocationException ex)
        {
            throw new Xunit.Sdk.XunitException($"{method} 抛异常：{ex.InnerException}");
        }
    }
}
