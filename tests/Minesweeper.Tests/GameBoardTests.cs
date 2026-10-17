using Minesweeper.Models;
using Xunit;

namespace Minesweeper.Tests;

/// <summary>棋盘基础逻辑的回归测试</summary>
public class GameBoardTests
{
    public static TheoryData<Difficulty> AllDifficulties => new()
    {
        Difficulty.Beginner, Difficulty.Intermediate, Difficulty.Expert
    };

    [Theory]
    [MemberData(nameof(AllDifficulties))]
    public void 首击九宫格内永远没有雷(Difficulty d)
    {
        for (int trial = 0; trial < 40; trial++)
        {
            int r = Random.Shared.Next(d.Rows);
            int c = Random.Shared.Next(d.Cols);
            var board = new GameBoard(d);
            board.Reveal(r, c);

            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nr = r + dr, nc = c + dc;
                    if (nr < 0 || nr >= d.Rows || nc < 0 || nc >= d.Cols) continue;
                    Assert.False(board.Cells[nr, nc].IsMine, $"首击({r},{c}) 的九宫格里 ({nr},{nc}) 是雷");
                }
        }
    }

    [Theory]
    [MemberData(nameof(AllDifficulties))]
    public void 雷总数等于难度设定(Difficulty d)
    {
        for (int trial = 0; trial < 20; trial++)
        {
            var board = new GameBoard(d);
            board.Reveal(Random.Shared.Next(d.Rows), Random.Shared.Next(d.Cols));

            int mines = 0;
            for (int r = 0; r < d.Rows; r++)
                for (int c = 0; c < d.Cols; c++)
                    if (board.Cells[r, c].IsMine) mines++;

            Assert.Equal(d.MineCount, mines);
        }
    }

    [Theory]
    [MemberData(nameof(AllDifficulties))]
    public void 每个格子的相邻雷数都与实际一致(Difficulty d)
    {
        for (int trial = 0; trial < 10; trial++)
        {
            var board = new GameBoard(d);
            board.Reveal(Random.Shared.Next(d.Rows), Random.Shared.Next(d.Cols));

            for (int r = 0; r < d.Rows; r++)
                for (int c = 0; c < d.Cols; c++)
                {
                    if (board.Cells[r, c].IsMine) continue;
                    int real = board.GetNeighbors(r, c).Count(n => n.IsMine);
                    Assert.Equal(real, board.Cells[r, c].AdjacentMines);
                }
        }
    }

    [Fact]
    public void 空白区域会完整展开()
    {
        for (int trial = 0; trial < 30; trial++)
        {
            var board = new GameBoard(Difficulty.Expert);
            board.Reveal(Random.Shared.Next(board.Rows), Random.Shared.Next(board.Cols));
            if (board.GameOver) continue;

            for (int r = 0; r < board.Rows; r++)
                for (int c = 0; c < board.Cols; c++)
                {
                    var cell = board.Cells[r, c];
                    if (cell.State != CellState.Revealed || cell.AdjacentMines != 0) continue;
                    foreach (var nb in board.GetNeighbors(r, c))
                        Assert.Equal(CellState.Revealed, nb.State);
                }
        }
    }

    [Fact]
    public void 翻开全部安全格即获胜()
    {
        for (int trial = 0; trial < 10; trial++)
        {
            var board = new GameBoard(Difficulty.Beginner);
            board.Reveal(4, 4);
            for (int r = 0; r < board.Rows && !board.GameOver; r++)
                for (int c = 0; c < board.Cols && !board.GameOver; c++)
                    if (!board.Cells[r, c].IsMine) board.Reveal(r, c);

            Assert.True(board.GameOver);
            Assert.True(board.Won);
        }
    }

    [Fact]
    public void 踩雷即失败()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);

        (int r, int c) mine = (-1, -1);
        for (int r = 0; r < board.Rows && mine.r < 0; r++)
            for (int c = 0; c < board.Cols && mine.r < 0; c++)
                if (board.Cells[r, c].IsMine) mine = (r, c);

        board.Reveal(mine.r, mine.c);
        Assert.True(board.GameOver);
        Assert.False(board.Won);
    }

    [Fact]
    public void 标旗与取消标旗会正确计数()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);

        int flagged = 0;
        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
                if (board.Cells[r, c].State == CellState.Covered && board.ToggleFlag(r, c)) flagged++;

        Assert.Equal(flagged, board.FlaggedCount);

        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
                if (board.Cells[r, c].State == CellState.Flagged) board.ToggleFlag(r, c);

        Assert.Equal(0, board.FlaggedCount);
    }

    [Fact]
    public void 已标旗的格子不会被掀开()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);

        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                if (board.Cells[r, c].State != CellState.Covered) continue;
                board.ToggleFlag(r, c);
                var revealed = board.Reveal(r, c);
                Assert.Empty(revealed);
                Assert.Equal(CellState.Flagged, board.Cells[r, c].State);
                return;
            }

        Assert.Fail("没有找到可标旗的格子");
    }

    [Fact]
    public void 游戏结束后不再接受任何操作()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);

        for (int r = 0; r < board.Rows && !board.GameOver; r++)
            for (int c = 0; c < board.Cols && !board.GameOver; c++)
                if (board.Cells[r, c].IsMine) board.Reveal(r, c);

        Assert.True(board.GameOver);
        int before = board.FlaggedCount;
        board.ToggleFlag(0, 0);
        Assert.Equal(before, board.FlaggedCount);
        Assert.Empty(board.Reveal(0, 0));
    }

    [Fact]
    public void Reset_会清空棋盘()
    {
        var board = new GameBoard(Difficulty.Beginner);
        board.Reveal(4, 4);
        for (int c = 0; c < board.Cols; c++)
            if (board.Cells[0, c].State == CellState.Covered) board.ToggleFlag(0, c);

        board.Reset();

        Assert.False(board.GameOver);
        Assert.False(board.Won);
        Assert.False(board.FirstClickDone);
        Assert.Equal(0, board.FlaggedCount);
        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                Assert.False(board.Cells[r, c].IsMine);
                Assert.Equal(0, board.Cells[r, c].AdjacentMines);
                Assert.Equal(CellState.Covered, board.Cells[r, c].State);
            }
    }

    [Fact]
    public void 智能操作_旗够时会掀开剩余格且不会踩雷()
    {
        var board = new GameBoard(Difficulty.Expert);
        board.Reveal(8, 15);

        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                var cell = board.Cells[r, c];
                if (cell.State != CellState.Revealed || cell.AdjacentMines == 0) continue;

                var covered = board.GetNeighbors(r, c).Where(n => n.State == CellState.Covered).ToList();
                var mineNb = covered.Where(n => n.IsMine).ToList();
                var safeNb = covered.Where(n => !n.IsMine).ToList();
                if (mineNb.Count != cell.AdjacentMines || safeNb.Count == 0) continue;

                foreach (var m in mineNb) board.ToggleFlag(m.Row, m.Col);

                var (revealed, _, hitMine) = board.Chord(r, c);
                Assert.False(hitMine, "旗都插对了，不该踩雷");
                Assert.NotEmpty(revealed);
                foreach (var s in safeNb)
                    Assert.Equal(CellState.Revealed, board.Cells[s.Row, s.Col].State);
                return;
            }

        Assert.Fail("没有找到适合测试智能操作的盘面");
    }

    [Fact]
    public void 智能操作_旗不够时会返回闪烁提示而不是掀开格子()
    {
        var board = new GameBoard(Difficulty.Expert);
        board.Reveal(8, 15);

        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                var cell = board.Cells[r, c];
                if (cell.State != CellState.Revealed || cell.AdjacentMines == 0) continue;
                var covered = board.GetNeighbors(r, c).Count(n => n.State == CellState.Covered);
                if (covered == 0) continue;

                var (revealed, flashing, hitMine) = board.Chord(r, c);
                Assert.Empty(revealed);
                Assert.False(hitMine);
                Assert.Equal(covered, flashing.Count);
                return;
            }

        Assert.Fail("没有找到适合测试的盘面");
    }
}
