using System.Collections.Concurrent;
using Minesweeper.Models;

namespace Minesweeper.Services;

/// <summary>求解器对当前局面的判定结果</summary>
public enum SolveOutcome
{
    /// <summary>可以纯逻辑推到通关</summary>
    Solvable,
    /// <summary>推到某一步无法继续确定，需要猜</summary>
    NeedsGuess,
    /// <summary>局面自相矛盾（通常是因为有旗插错了）</summary>
    Contradiction
}

/// <summary>雷盘生成结果</summary>
public sealed class GenerationResult
{
    public GameBoard Board { get; }

    /// <summary>实际尝试了几次才成功</summary>
    public int Attempts { get; }

    /// <summary>true = 已由求解器验证全盘无猜；false = 尝试次数用尽，未能验证</summary>
    public bool GuaranteedNoGuess { get; }

    public GenerationResult(GameBoard board, int attempts, bool guaranteedNoGuess)
    {
        Board = board;
        Attempts = attempts;
        GuaranteedNoGuess = guaranteedNoGuess;
    }
}

/// <summary>
/// 无猜求解器。
///
/// 推理分两层：
///   第一层 —— 基础计数：某数字格周围剩余未知格「全安全」或「全是雷」。
///   第二层 —— 约束图连通分量分解 + 分量内完整枚举：
///             按「是否共享同一个数字格约束」把未知格拆成互不相干的若干小块，
///             每块单独枚举全部合法赋值，只采用在该块所有解中都成立的结论。
///
/// 为什么按分量独立统计是安全的：真实雷分布必然是「每个分量各自一个合法赋值」的组合，
/// 所以某格若在某分量的所有合法赋值中都是雷，它在真实盘面里也必然是雷。
/// 该结论无须全局雷数约束即成立，因此绝不会推出错误结果（只会偏保守）。
///
/// 与旧实现的关键区别：
///   - 旧版把全盘未知格放在一起枚举，变量数一超过 28 个就整体放弃推理（高级难度下极常见）；
///     现在按分量拆开，规模上限只作用于单个分量，且单个分量超限不影响其他分量。
///   - 旧版用反射改私有字段来克隆棋盘；现在用数组化的内部模拟盘，不触碰 GameBoard 内部状态。
///   - 旧版在尝试次数耗尽后静默返回未经验证的盘面；现在通过 GenerationResult 显式告知。
/// </summary>
public static class SolverService
{
    /// <summary>生成时的默认最大尝试次数</summary>
    public const int DefaultMaxAttempts = 200;

    /// <summary>单个连通分量允许枚举的最大变量数，超过则该分量本轮不参与推理</summary>
    private const int ComponentVarCap = 40;

    /// <summary>单个分量的搜索节点预算，防止极端局面下枚举爆炸</summary>
    private const long ComponentNodeBudget = 500_000;

    /// <summary>单次求解的最大迭代轮数（防御性上限）</summary>
    private const int MaxRounds = 20_000;

    private const byte Covered = 0;
    private const byte Revealed = 1;
    private const byte Flagged = 2;

    private static readonly ConcurrentDictionary<(int Rows, int Cols), int[][]> NeighborCache = new();

    // ==================== 公开 API ====================

    /// <summary>
    /// 生成雷盘。<see cref="GenerationResult.GuaranteedNoGuess"/> 为 false 时表示尝试次数用尽、
    /// 返回的盘面未经无猜验证，调用方应当如实告知玩家，而不是假装它是无猜盘。
    /// </summary>
    public static GenerationResult Generate(
        Difficulty difficulty, int safeRow, int safeCol,
        int maxAttempts = DefaultMaxAttempts, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var board = new GameBoard(difficulty);
            board.Reveal(safeRow, safeCol);

            if (board.GameOver && !board.Won) continue;   // 首击踩雷（首击安全区下不会发生，保险）
            if (IsSolvableFromCurrentState(board))
                return new GenerationResult(board, attempt, true);
        }

        var fallback = new GameBoard(difficulty);
        fallback.Reveal(safeRow, safeCol);
        return new GenerationResult(fallback, maxAttempts, false);
    }

    /// <summary>从当前局面出发，判断能否通过纯逻辑推理通关</summary>
    public static bool IsSolvableFromCurrentState(GameBoard board)
        => SolveCore(new Sim(board), null, null) == SolveOutcome.Solvable;

    /// <summary>
    /// 分析当前局面，给出**逻辑上必然成立**的安全格与雷格（供「提示」功能使用）。
    /// 返回 Contradiction 表示盘面与现有标旗自相矛盾（多半是旗插错了），此时不应给出任何提示。
    /// </summary>
    public static SolveOutcome Analyze(
        GameBoard board,
        out List<(int Row, int Col)> certainSafe,
        out List<(int Row, int Col)> certainMines)
    {
        var sim = new Sim(board);
        var safe = new List<(int, int)>();
        var mines = new List<(int, int)>();
        var outcome = SolveCore(sim, safe, mines);
        certainSafe = safe;
        certainMines = mines;
        return outcome;
    }

    // ==================== 求解主循环 ====================

    private static SolveOutcome SolveCore(
        Sim sim,
        List<(int Row, int Col)>? certainSafe,
        List<(int Row, int Col)>? certainMines)
    {
        // 推理建立在「已插的旗都是雷」这一前提上。只要存在插错的旗，
        // 约束系统就会把真实雷分布排除在外，之后的任何「必然结论」都不再可靠。
        // 此时宁可报告无法判断，也不给出可能错误的结果。
        if (sim.HasWrongFlag) return SolveOutcome.Contradiction;

        for (int round = 0; round < MaxRounds; round++)
        {
            if (sim.RevealedSafe >= sim.TotalSafe) return SolveOutcome.Solvable;

            // ---------- 1. 收集约束 ----------
            var varIndexOf = new Dictionary<int, int>();
            var varCells = new List<int>();
            var constraints = new List<Constraint>();

            var hiddenBuf = new int[8];
            for (int i = 0; i < sim.N; i++)
            {
                if (sim.State[i] != Revealed || sim.Adj[i] == 0) continue;

                int flaggedAround = 0;
                int hiddenCount = 0;
                var nbrs = sim.Nbr[i];
                for (int k = 0; k < nbrs.Length; k++)
                {
                    byte st = sim.State[nbrs[k]];
                    if (st == Covered) hiddenBuf[hiddenCount++] = nbrs[k];
                    else if (st == Flagged) flaggedAround++;
                }
                if (hiddenCount == 0) continue;

                int need = sim.Adj[i] - flaggedAround;
                // 需要比候选还多、或为负 → 现有旗与真实雷数不符（多半是插错了旗）
                if (need < 0 || need > hiddenCount) return SolveOutcome.Contradiction;

                var vars = new int[hiddenCount];
                for (int k = 0; k < hiddenCount; k++)
                {
                    int cell = hiddenBuf[k];
                    if (!varIndexOf.TryGetValue(cell, out int vi))
                    {
                        vi = varCells.Count;
                        varIndexOf[cell] = vi;
                        varCells.Add(cell);
                    }
                    vars[k] = vi;
                }
                constraints.Add(new Constraint(vars, need));
            }

            if (varCells.Count == 0) return SolveOutcome.NeedsGuess;

            // ---------- 2. 第一层：基础计数（最快路径） ----------
            var mustFlag = new List<int>();
            var mustReveal = new List<int>();
            var deferred = new List<Constraint>();

            foreach (var c in constraints)
            {
                if (c.Need == 0)
                    foreach (int vi in c.Vars) mustReveal.Add(varCells[vi]);
                else if (c.Need == c.Vars.Length)
                    foreach (int vi in c.Vars) mustFlag.Add(varCells[vi]);
                else
                    deferred.Add(c);
            }

            // ---------- 3. 第二层：分量分解 + 分量内完整枚举 ----------
            if (mustFlag.Count == 0 && mustReveal.Count == 0)
            {
                var deduced = DeduceByComponents(varCells, deferred);
                if (deduced.Status == DeduceStatus.Contradiction) return SolveOutcome.Contradiction;
                if (deduced.Status == DeduceStatus.Progress)
                {
                    mustReveal.AddRange(deduced.Safe!);
                    mustFlag.AddRange(deduced.Mines!);
                }
            }

            if (mustFlag.Count == 0 && mustReveal.Count == 0)
                return SolveOutcome.NeedsGuess;

            // 同一个格子既被判为雷又被判为安全 → 推导本身矛盾，宁可不给结论
            if (mustFlag.Count > 0 && mustReveal.Count > 0)
            {
                var flagSet = new HashSet<int>(mustFlag);
                foreach (int cell in mustReveal)
                    if (flagSet.Contains(cell)) return SolveOutcome.Contradiction;
            }

            // ---------- 4. 应用结论 ----------
            foreach (int cell in mustFlag)
            {
                if (sim.State[cell] != Covered) continue;
                // 自检：推出的「雷格」若实际不是雷，说明推导有误
                if (!sim.Mine[cell]) return SolveOutcome.Contradiction;
                sim.State[cell] = Flagged;
                sim.FlaggedCount++;
                certainMines?.Add((cell / sim.Cols, cell % sim.Cols));
            }

            foreach (int cell in mustReveal)
            {
                if (sim.State[cell] != Covered) continue;
                // 自检：推出的「安全格」若实际是雷，说明推导有误 —— 宁可放弃也不给错结论
                if (sim.Mine[cell]) return SolveOutcome.Contradiction;
                sim.RevealFlood(cell);
                certainSafe?.Add((cell / sim.Cols, cell % sim.Cols));
            }
        }

        return SolveOutcome.NeedsGuess;
    }

    // ==================== 分量分解 + 分量内枚举 ====================

    private enum DeduceStatus { Progress, Stuck, Contradiction }

    private readonly struct DeduceResult
    {
        public readonly DeduceStatus Status;
        public readonly List<int>? Safe;
        public readonly List<int>? Mines;
        public DeduceResult(DeduceStatus status, List<int>? safe, List<int>? mines)
        {
            Status = status; Safe = safe; Mines = mines;
        }
    }

    /// <summary>把变量按连通分量拆开逐个完整枚举，只采用在该分量所有解中都成立的结论</summary>
    private static DeduceResult DeduceByComponents(List<int> varCells, List<Constraint> constraints)
    {
        int varCount = varCells.Count;

        // 并查集：出现在同一约束里的变量归为一组
        var dsu = new int[varCount];
        for (int i = 0; i < varCount; i++) dsu[i] = i;

        int Find(int x)
        {
            while (dsu[x] != x) { dsu[x] = dsu[dsu[x]]; x = dsu[x]; }
            return x;
        }

        foreach (var c in constraints)
        {
            int root = Find(c.Vars[0]);
            for (int k = 1; k < c.Vars.Length; k++)
            {
                int b = Find(c.Vars[k]);
                if (root != b) dsu[b] = root;
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < varCount; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
            list.Add(i);
        }

        var safe = new List<int>();
        var mines = new List<int>();

        var localOf = new Dictionary<int, int>();
        var localCons = new List<Constraint>();

        foreach (var group in groups.Values)
        {
            int n = group.Count;
            if (n > ComponentVarCap) continue;   // 该分量太复杂，本轮跳过（不影响其他分量）

            localOf.Clear();
            for (int i = 0; i < n; i++) localOf[group[i]] = i;

            localCons.Clear();
            foreach (var c in constraints)
            {
                if (!localOf.ContainsKey(c.Vars[0])) continue;
                var lv = new int[c.Vars.Length];
                for (int k = 0; k < lv.Length; k++) lv[k] = localOf[c.Vars[k]];
                localCons.Add(new Constraint(lv, c.Need));
            }

            var result = EnumerateComponent(n, localCons);
            if (result == null) continue;   // 超预算，跳过该分量

            var (solCount, mineCounts, safeCounts) = result.Value;
            if (solCount == 0) return new DeduceResult(DeduceStatus.Contradiction, null, null);

            for (int i = 0; i < n; i++)
            {
                if (mineCounts[i] == solCount) mines.Add(varCells[group[i]]);
                else if (safeCounts[i] == solCount) safe.Add(varCells[group[i]]);
            }
        }

        if (mines.Count > 0 || safe.Count > 0)
            return new DeduceResult(DeduceStatus.Progress, safe, mines);
        return new DeduceResult(DeduceStatus.Stuck, null, null);   // 含「全部超预算」的情况
    }

    /// <summary>
    /// 枚举单个分量的全部合法赋值；返回 null 表示超出节点预算。
    /// mineCounts[i] = 第 i 个变量为雷的合法解数量，safeCounts[i] 同理。
    /// </summary>
    private static (long solutions, int[] mineCounts, int[] safeCounts)? EnumerateComponent(
        int n, List<Constraint> constraints)
    {
        if (n == 0) return null;

        // 变量按「参与的约束数」降序排列，让剪枝尽早生效
        var conOfVar = new List<int>[n];
        for (int i = 0; i < n; i++) conOfVar[i] = new List<int>();
        var degree = new int[n];
        for (int ci = 0; ci < constraints.Count; ci++)
            foreach (int vi in constraints[ci].Vars)
            {
                degree[vi]++;
                conOfVar[vi].Add(ci);
            }

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => degree[b].CompareTo(degree[a]));

        int conCount = constraints.Count;
        var conVars = new int[conCount][];
        var conNeed = new int[conCount];
        var remaining = new int[conCount];   // 约束里还没赋值的变量数
        var placed = new int[conCount];      // 约束里已判定为雷的变量数
        for (int ci = 0; ci < conCount; ci++)
        {
            conVars[ci] = constraints[ci].Vars;
            conNeed[ci] = constraints[ci].Need;
            remaining[ci] = conVars[ci].Length;
        }

        var assigned = new bool[n];
        var mineCounts = new int[n];
        var safeCounts = new int[n];
        long solutions = 0, nodes = 0;
        bool overBudget = false;

        Search(0);

        if (overBudget) return null;
        return (solutions, mineCounts, safeCounts);

        void Search(int depth)
        {
            nodes++;
            if (nodes > ComponentNodeBudget) { overBudget = true; return; }

            if (depth == n)
            {
                solutions++;
                for (int i = 0; i < n; i++)
                {
                    if (assigned[i]) mineCounts[i]++;
                    else safeCounts[i]++;
                }
                return;
            }

            int v = order[depth];
            for (int value = 0; value <= 1; value++)
            {
                assigned[v] = value == 1;

                bool feasible = true;
                foreach (int ci in conOfVar[v])
                {
                    remaining[ci]--;
                    if (value == 1) placed[ci]++;
                    // 已判定的雷不能超过需求，也不能补不满需求
                    if (placed[ci] > conNeed[ci] || placed[ci] + remaining[ci] < conNeed[ci])
                        feasible = false;
                }

                if (feasible) Search(depth + 1);

                foreach (int ci in conOfVar[v])
                {
                    remaining[ci]++;
                    if (value == 1) placed[ci]--;
                }
                if (overBudget) return;
            }
        }
    }

    // ==================== 内部模拟盘 ====================

    private readonly struct Constraint
    {
        public readonly int[] Vars;
        public readonly int Need;
        public Constraint(int[] vars, int need) { Vars = vars; Need = need; }
    }

    /// <summary>
    /// 数组化的棋盘视图，求解全程只操作它，不触碰 GameBoard 的内部状态。
    /// Mine 仅用于「推出安全格却撞雷」的自检，推理过程本身不依赖它。
    /// </summary>
    private sealed class Sim
    {
        public readonly int Rows, Cols, N, MineCount, TotalSafe;
        public readonly byte[] State;
        public readonly byte[] Adj;
        public readonly bool[] Mine;
        public readonly int[][] Nbr;
        public int RevealedSafe;
        public int FlaggedCount;
        public bool HasWrongFlag;

        public Sim(GameBoard board)
        {
            Rows = board.Rows;
            Cols = board.Cols;
            N = Rows * Cols;
            MineCount = board.MineCount;
            TotalSafe = N - MineCount;
            State = new byte[N];
            Adj = new byte[N];
            Mine = new bool[N];
            Nbr = GetNeighbors(Rows, Cols);

            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    int i = r * Cols + c;
                    var cell = board.Cells[r, c];
                    byte st = cell.State switch
                    {
                        CellState.Revealed => Revealed,
                        CellState.Flagged => Flagged,
                        _ => Covered
                    };
                    State[i] = st;
                    Adj[i] = (byte)cell.AdjacentMines;
                    Mine[i] = cell.IsMine;
                    if (st == Revealed && !cell.IsMine) RevealedSafe++;
                    else if (st == Flagged)
                    {
                        FlaggedCount++;
                        if (!cell.IsMine) HasWrongFlag = true;
                    }
                }
        }

        /// <summary>从 start 展开空白区域（规则与 GameBoard 一致）</summary>
        public void RevealFlood(int start)
        {
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (State[cur] != Covered) continue;
                State[cur] = Revealed;
                RevealedSafe++;
                if (Adj[cur] != 0) continue;
                foreach (int nb in Nbr[cur])
                    if (State[nb] == Covered) stack.Push(nb);
            }
        }
    }

    private static int[][] GetNeighbors(int rows, int cols) =>
        NeighborCache.GetOrAdd((rows, cols), key =>
        {
            var (r0, c0) = key;
            var table = new int[r0 * c0][];
            for (int r = 0; r < r0; r++)
                for (int c = 0; c < c0; c++)
                {
                    var list = new List<int>(8);
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (dr == 0 && dc == 0) continue;
                            int nr = r + dr, nc = c + dc;
                            if (nr >= 0 && nr < r0 && nc >= 0 && nc < c0) list.Add(nr * c0 + nc);
                        }
                    table[r * c0 + c] = list.ToArray();
                }
            return table;
        });
}
