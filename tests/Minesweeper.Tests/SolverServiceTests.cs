using System.Diagnostics;
using System.Text;
using Minesweeper.Models;
using Minesweeper.Services;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>无猜求解器的可靠性、完备性与性能测试</summary>
public class SolverServiceTests
{
    private readonly ITestOutputHelper _out;

    public SolverServiceTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<Difficulty> AllDifficulties => new()
    {
        Difficulty.Beginner, Difficulty.Intermediate, Difficulty.Expert
    };

    // ---------- 可靠性：绝不能推出错误结论 ----------

    [Theory]
    [MemberData(nameof(AllDifficulties))]
    public void 求解器推出的结论永远正确(Difficulty d)
    {
        int analyzed = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            var board = new GameBoard(d);
            board.Reveal(Random.Shared.Next(d.Rows), Random.Shared.Next(d.Cols));
            if (board.GameOver) continue;
            analyzed++;

            var outcome = SolverService.Analyze(board, out var safe, out var mines);

            // 盘面上没有任何旗，不可能出现「旗与真实雷数矛盾」
            Assert.NotEqual(SolveOutcome.Contradiction, outcome);

            foreach (var (row, col) in safe)
                Assert.False(board.Cells[row, col].IsMine,
                    $"[{d.Name}] 求解器把雷 ({row},{col}) 判成了安全格");

            foreach (var (row, col) in mines)
                Assert.True(board.Cells[row, col].IsMine,
                    $"[{d.Name}] 求解器把安全格 ({row},{col}) 判成了雷");
        }

        Assert.True(analyzed > 0, "样本全部无效");
    }

    [Fact]
    public void 即使有旗插错_求解器也不会给出错误结论()
    {
        int checkedCases = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            var board = new GameBoard(Difficulty.Expert);
            board.Reveal(8, 15);

            for (int r = 0; r < board.Rows; r++)
                for (int c = 0; c < board.Cols; c++)
                {
                    var cell = board.Cells[r, c];
                    if (cell.State != CellState.Covered || cell.IsMine) continue;
                    bool nextToNumber = board.GetNeighbors(r, c)
                        .Any(n => n.State == CellState.Revealed && n.AdjacentMines > 0);
                    if (!nextToNumber) continue;

                    board.ToggleFlag(r, c);   // 故意把旗插在安全格上
                    var outcome = SolverService.Analyze(board, out var safe, out var mines);
                    checkedCases++;

                    if (outcome == SolveOutcome.Contradiction) break;

                    foreach (var (row, col) in safe)
                        Assert.False(board.Cells[row, col].IsMine,
                            "错旗存在时求解器给出了错误的安全格");
                    foreach (var (row, col) in mines)
                        Assert.True(board.Cells[row, col].IsMine,
                            "错旗存在时求解器给出了错误的雷格");
                    break;
                }
        }

        _out.WriteLine($"检查了 {checkedCases} 个错旗局面");
        Assert.True(checkedCases > 0);
    }

    // ---------- 隔离性 ----------

    [Fact]
    public void 求解器不会修改传入的棋盘()
    {
        var board = new GameBoard(Difficulty.Expert);
        board.Reveal(8, 15);
        string before = Snapshot(board);

        SolverService.Analyze(board, out _, out _);
        SolverService.IsSolvableFromCurrentState(board);

        Assert.Equal(before, Snapshot(board));
    }

    private static string Snapshot(GameBoard b)
    {
        var sb = new StringBuilder(b.Rows * b.Cols);
        for (int r = 0; r < b.Rows; r++)
            for (int c = 0; c < b.Cols; c++)
                sb.Append((int)b.Cells[r, c].State);
        return sb.ToString();
    }

    // ---------- 生成质量 ----------

    [Theory]
    [MemberData(nameof(AllDifficulties))]
    public void 生成的无猜盘一定可以纯逻辑通关(Difficulty d)
    {
        for (int i = 0; i < 10; i++)
        {
            var result = SolverService.Generate(d, d.Rows / 2, d.Cols / 2, maxAttempts: 200);

            Assert.True(result.GuaranteedNoGuess,
                $"[{d.Name}] 200 次尝试内没能生成无猜盘（第 {i + 1} 次生成）");
            Assert.True(result.Attempts >= 1 && result.Attempts <= 200);
            Assert.True(SolverService.IsSolvableFromCurrentState(result.Board));
        }
    }

    [Fact]
    public void 高级难度不再出现静默兜底()
    {
        const int samples = 30;
        int fallback = 0;
        long totalAttempts = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < samples; i++)
        {
            var result = SolverService.Generate(Difficulty.Expert, 8, 15, maxAttempts: 200);
            totalAttempts += result.Attempts;
            if (!result.GuaranteedNoGuess) fallback++;
        }

        sw.Stop();
        double perGame = sw.Elapsed.TotalMilliseconds / samples;
        _out.WriteLine($"高级难度 {samples} 局：兜底 {fallback} 次，平均尝试 {totalAttempts / (double)samples:F2} 次，" +
                       $"平均单局生成 {perGame:F1} ms");

        Assert.True(fallback == 0, $"{samples} 局里有 {fallback} 局没能生成无猜盘");
    }

    // ---------- 性能与终止性 ----------

    [Fact]
    public void 单次判定耗时可接受()
    {
        // 预热
        for (int i = 0; i < 5; i++)
        {
            var warm = new GameBoard(Difficulty.Expert);
            warm.Reveal(8, 15);
            SolverService.IsSolvableFromCurrentState(warm);
        }

        const int n = 100;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            var board = new GameBoard(Difficulty.Expert);
            board.Reveal(8, 15);
            SolverService.IsSolvableFromCurrentState(board);
        }
        sw.Stop();

        double perCall = sw.Elapsed.TotalMilliseconds / n;
        _out.WriteLine($"高级难度单次判定平均 {perCall:F3} ms");

        Assert.True(perCall < 50, $"单次判定 {perCall:F1} ms，过慢");
    }

    [Fact]
    public async Task 求解不会陷入死循环()
    {
        var task = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var board = new GameBoard(Difficulty.Expert);
                board.Reveal(Random.Shared.Next(board.Rows), Random.Shared.Next(board.Cols));
                SolverService.Analyze(board, out _, out _);
            }
        });

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(task, finished);
    }
}
