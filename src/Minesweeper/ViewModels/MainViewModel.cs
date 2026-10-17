using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Minesweeper.Controls;
using Minesweeper.Models;
using Minesweeper.Services;

namespace Minesweeper.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ScoreService _scoreService = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _stopwatch = new();
    private bool _isGameActive;

    // ---- 难度（可随时切换） ----
    private Difficulty _currentDifficulty = Difficulty.Expert;
    public Difficulty CurrentDifficulty
    {
        get => _currentDifficulty;
        set
        {
            if (value == null || !Set(ref _currentDifficulty, value)) return;
            OnPropertyChanged(nameof(WindowTitle));
            UpdateBestRecord();
            NewGame();
        }
    }

    // ---- 棋盘 ----
    private GameBoard? _board;
    public ObservableCollection<CellViewModel> Cells { get; } = new();

    // O(1) 索引：每次查找 CellViewModel 不用 O(n) 扫描
    private Dictionary<(int, int), CellViewModel> _cellIndex = new();

    // ---- 状态 ----
    private int _remainingMines;
    public int RemainingMines
    {
        get => _remainingMines;
        set
        {
            if (!Set(ref _remainingMines, value)) return;
            OnPropertyChanged(nameof(RemainingMinesText));
        }
    }

    /// <summary>
    /// 状态栏「雷」的数字文本。旗插多了会显示负数（经典扫雷的做法），
    /// 这样玩家能立刻看出自己多插了几面，而不是被钳成 0 之后只能靠数格子。
    /// </summary>
    public string RemainingMinesText => _remainingMines < 0 ? $"-{-_remainingMines}" : _remainingMines.ToString();

    private bool _hasExcessFlags;
    /// <summary>旗插多了（剩余雷数为负）：状态栏换个颜色提醒一下</summary>
    public bool HasExcessFlags { get => _hasExcessFlags; set => Set(ref _hasExcessFlags, value); }

    private string _elapsedTime = "0.0";
    public string ElapsedTime { get => _elapsedTime; set => Set(ref _elapsedTime, value); }

    private string _faceIcon = "😊";
    public string FaceIcon { get => _faceIcon; set => Set(ref _faceIcon, value); }

    private string _bestRecordText = "";
    public string BestRecordText { get => _bestRecordText; set => Set(ref _bestRecordText, value); }

    private string _statusMessage = "";
    /// <summary>状态栏提示（生成中 / 未能生成无猜盘 / 踩雷等）</summary>
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _isBusy;
    /// <summary>正在后台生成雷盘，此期间忽略棋盘输入</summary>
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    // ---- 主题 ----
    public ObservableCollection<ThemeService.ThemeInfo> Themes => ThemeService.Themes;
    private ThemeService.ThemeInfo _selectedTheme = ThemeService.Themes[0];
    public ThemeService.ThemeInfo SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (Set(ref _selectedTheme, value))
            {
                ThemeService.ApplyTheme(value.Name);
                CellViewModel.RefreshCachedBrushes();
                ScoreService.SaveTheme(value.Name);
                RefreshAllCells();
                BoardChanged?.Invoke();
            }
        }
    }

    public ICommand NewGameCommand { get; }
    public ICommand FaceClickCommand { get; }

    private double _cellSize = 30;
    public double CellSize { get => _cellSize; set => Set(ref _cellSize, value); }

    /// <summary>窗口标题（随难度变化）</summary>
    public string WindowTitle =>
        $"无猜扫雷 · {CurrentDifficulty.Rows}×{CurrentDifficulty.Cols} · {CurrentDifficulty.MineCount} 雷";

    /// <summary>待播放的格子动画：(行, 列, 类型, 延迟帧数)</summary>
    private readonly List<(int Row, int Col, BoardCanvas.CellAnimKind Kind, int Delay)> _pendingAnimations = new();

    /// <summary>取出本帧要播放的动画（由 BoardCanvas 在重绘前消费）</summary>
    public IReadOnlyList<(int Row, int Col, BoardCanvas.CellAnimKind Kind, int Delay)> TakePendingAnimations()
    {
        if (_pendingAnimations.Count == 0)
            return Array.Empty<(int, int, BoardCanvas.CellAnimKind, int)>();

        var copy = _pendingAnimations.ToArray();
        _pendingAnimations.Clear();
        return copy;
    }

    /// <summary>按到落点的距离错峰，连片展开时形成从点击处扩散出去的观感</summary>
    private void QueueRevealAnimations(List<Cell> revealed, int originRow, int originCol)
    {
        // 本次操作独占这一批动画。若上一次操作入队后没能触发 BoardChanged
        //（例如 Reveal 返回空列表），残留项会在之后某次重绘时突然播出来，
        // 玩家会看到一批与当前操作无关的格子在动。
        _pendingAnimations.Clear();

        // 大面积展开（和弦、整片掀开）时错峰只会显得拖沓，直接同步播放
        bool stagger = revealed.Count <= 20;

        foreach (var c in revealed)
        {
            int delay = 0;
            if (stagger)
            {
                // 每格延迟一帧（16ms）：快速排雷时能看出从点击处一圈圈荡开
                int distance = Math.Max(Math.Abs(c.Row - originRow), Math.Abs(c.Col - originCol));
                delay = Math.Min(distance, 8);
            }
            _pendingAnimations.Add((c.Row, c.Col, BoardCanvas.CellAnimKind.Reveal, delay));
        }
    }

    /// <summary>当前提示高亮的格子（(-1,-1) 表示没有提示）</summary>
    public (int Row, int Col) HintCell { get; private set; } = (-1, -1);

    /// <summary>是否有一次待播放的提示请求（一次性，取走即清零）</summary>
    private bool _hintPending;

    /// <summary>
    /// 取走待播放的提示请求。
    /// 用「一次性请求」而不是「比较属性值有没有变化」—— 后者在连续按 H 命中同一格时
    /// 会被判定成「没变化」而完全不显示，这正是上一版重复按 H 没反应的根因。
    /// </summary>
    public bool TryTakeHint(out int row, out int col)
    {
        if (!_hintPending)
        {
            row = col = -1;
            return false;
        }

        _hintPending = false;
        row = HintCell.Row;
        col = HintCell.Col;
        return true;
    }

    /// <summary>撤掉当前的提示（玩家继续操作时调用）</summary>
    private void ClearHint()
    {
        HintCell = (-1, -1);
        // 用「一次性请求」把撤销也走一遍通知通道：
        // 置 true 之后，下一次 BoardChanged 会取到 (-1,-1) 并调用 BoardCanvas.ClearHint()，
        // 让画布上的高亮立刻撤掉。原来这里直接置 false，那条通道永远进不去，
        // 提示只能等 430ms 的动画自己跑完才消失。
        _hintPending = true;
    }

    /// <summary>
    /// 提示：用求解器算出当前局面里逻辑上必然安全的格子（没有安全格时给出必然的雷）。
    /// 求解器只返回「在所有合法解中都成立」的结论，所以提示不会误导玩家。
    /// </summary>
    public void ShowHint()
    {
        if (_board == null || _board.GameOver) return;

        if (IsBusy)
        {
            StatusMessage = "正在生成雷盘，稍等一下";
            return;
        }

        // 还没首击时盘面根本没布雷（所有格子都是未翻开的空白），求解器收集不到任何约束，
        // 会返回 NeedsGuess —— 于是玩家看到「当前局面无法只靠逻辑确定」，误以为这个盘需要猜。
        if (!_board.FirstClickDone)
        {
            ClearHint();
            StatusMessage = "先随便点开一格，再按 H 看提示";
            BoardChanged?.Invoke();
            return;
        }

        var outcome = SolverService.Analyze(_board, out var certainSafe, out var certainMines);

        if (outcome == SolveOutcome.Contradiction)
        {
            ClearHint();
            StatusMessage = "⚠ 有旗插错了，右键修正后再提示";
            BoardChanged?.Invoke();
            return;
        }

        if (outcome == SolveOutcome.NeedsGuess)
        {
            ClearHint();
            StatusMessage = "当前局面无法只靠逻辑确定，需要你自己判断";
            BoardChanged?.Invoke();
            return;
        }

        // 只在「紧贴已翻开区域」的格子里挑。
        // 求解器会一路推演到通关，结果里包含好几步之后才确定的远处格子；
        // 直接从全部结论里随机取一个，玩家会看到一个跟当前盘面毫无关联的位置，无从理解。
        var frontierSafe = certainSafe.Where(p => IsOnFrontier(p.Row, p.Col)).ToList();
        var frontierMines = certainMines.Where(p => IsOnFrontier(p.Row, p.Col)).ToList();

        if (frontierSafe.Count > 0)
        {
            HintCell = frontierSafe[Random.Shared.Next(frontierSafe.Count)];
            StatusMessage = $"💡 第 {HintCell.Row + 1} 行第 {HintCell.Col + 1} 列一定是安全的";
            _hintPending = true;
        }
        else if (frontierMines.Count > 0)
        {
            HintCell = frontierMines[Random.Shared.Next(frontierMines.Count)];
            StatusMessage = $"💡 第 {HintCell.Row + 1} 行第 {HintCell.Col + 1} 列一定是雷";
            _hintPending = true;
        }
        else
        {
            ClearHint();
            StatusMessage = "现在没有可以确定的信息";
        }

        // 这里只表达「提示的是哪一格」；闪两下并淡出消失由 BoardCanvas.FlashHint 负责。
        // 位置信息同时留在状态栏里，闪没了也能照着找。
        BoardChanged?.Invoke();
    }

    /// <summary>该格是否紧贴已翻开的区域（提示只从推理边界上给）</summary>
    private bool IsOnFrontier(int row, int col)
    {
        if (_board == null) return false;
        foreach (var neighbor in _board.GetNeighbors(row, col))
            if (neighbor.State == CellState.Revealed) return true;
        return false;
    }

    /// <summary>打开排行榜与统计窗口</summary>
    public void ShowStatistics()
    {
        var window = new StatsWindow(CurrentDifficulty, _scoreService.GetStats(CurrentDifficulty.Name))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>通知 BoardCanvas 重绘</summary>
    public event Action? BoardChanged;

    public MainViewModel()
    {
        // 加载上次保存的主题（老配置里的历史主题名会被映射到最接近的当前主题）
        var savedTheme = ScoreService.LoadTheme();
        var theme = ThemeService.Resolve(savedTheme);
        _selectedTheme = theme;
        ThemeService.ApplyTheme(theme.Name);

        NewGameCommand = new RelayCommand(() => NewGame());
        FaceClickCommand = new RelayCommand(() => NewGame());
        _timer.Tick += OnTimerTick;
        UpdateBestRecord();
    }

    // ---- 批量构建 Cells ----
    private void RebuildCells(GameBoard board)
    {
        var newCells = new List<CellViewModel>(board.Rows * board.Cols);
        var newIndex = new Dictionary<(int, int), CellViewModel>(board.Rows * board.Cols);
        for (int r = 0; r < board.Rows; r++)
            for (int c = 0; c < board.Cols; c++)
            {
                var vm = new CellViewModel(board.Cells[r, c], OnLeftClick, OnRightClick, OnChord);
                newCells.Add(vm);
                newIndex[(r, c)] = vm;
            }
        Cells.Clear();
        foreach (var vm in newCells) Cells.Add(vm);
        _cellIndex = newIndex;
    }

    /// <summary>窗口关闭时调用：停掉定时器并取消在途的生成任务</summary>
    public void Shutdown()
    {
        CancelPendingGeneration();
        _timer.Stop();
        StopFlashing();
    }

    public void NewGame()
    {
        // 上一局已经开局、但既没赢也没输就被换掉了 —— 记一次「弃局」。
        // 不记的话这一局会一直挂在 GamesPlayed 里，被统计窗口当成失败，白白拉低胜率。
        if (_board is { GameOver: false, FirstClickDone: true })
            _scoreService.RecordGameAbandoned(CurrentDifficulty.Name);

        CancelPendingGeneration();
        StopFlashing();
        _pendingAnimations.Clear();
        ClearHint();
        _timer.Stop();
        _stopwatch.Reset();
        _isGameActive = false;
        IsBusy = false;
        _board = new GameBoard(CurrentDifficulty);
        RebuildCells(_board);
        RemainingMines = _board.MineCount;
        ElapsedTime = "0.0";
        FaceIcon = "😊";
        StatusMessage = "";
        UpdateBestRecord();
        BoardChanged?.Invoke();
    }

    // ==================== 首次点击：后台生成无猜雷盘 ====================

    private CancellationTokenSource? _generationCts;

    private void CancelPendingGeneration()
    {
        var cts = _generationCts;
        _generationCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>
    /// 生成期间靠 <see cref="IsBusy"/> 互斥，重复点击不会再起第二个后台任务
    /// （原实现会，导致两次生成的棋盘互相覆盖、玩家点击"没反应"）。
    /// </summary>
    private async void BeginGenerateBoard(int row, int col)
    {
        CancelPendingGeneration();
        var cts = new CancellationTokenSource();
        _generationCts = cts;

        // 先取一次 token：CancellationTokenSource.Token 这个属性在 Dispose 之后访问会抛
        // ObjectDisposedException，而 CancelPendingGeneration 会立刻 Dispose 掉它。
        // 把 token 提前拿出来，后面就只碰这个值类型，不会再摸到已释放的源。
        var token = cts.Token;

        // 下面这些状态改动全部放进 try：一旦 BoardChanged 的订阅者抛异常，
        // finally 仍然会跑，IsBusy 不会被永久留在 true（那会让整个棋盘失去响应）。
        try
        {
            IsBusy = true;
            FaceIcon = "⏳";
            StatusMessage = "正在生成无猜雷盘…";
            BoardChanged?.Invoke();

            var difficulty = CurrentDifficulty;

            var result = await Task.Run(
                () => SolverService.Generate(difficulty, row, col, SolverService.DefaultMaxAttempts, token),
                token);

            if (token.IsCancellationRequested) return;

            _board = result.Board;
            RebuildCells(_board);
            _stopwatch.Restart();
            _timer.Start();
            FaceIcon = "😊";
            _scoreService.RecordGameStarted(difficulty.Name);
            // 生成器会如实告知是否真的验证过无猜，不再静默交付需要猜的盘
            StatusMessage = result.GuaranteedNoGuess
                ? ""
                : "⚠ 本局未能生成无猜盘面，需要自行判断";

            // 极低概率下首击会直接掀开全部安全格（初级尤其可能），此时这一局其实已经赢了。
            // 不处理的话会卡在「已结束但没有收尾」的状态：没有胜利提示、计时器照跑、
            // 点任何格子都被 GameOver 挡掉，只能按 F2。
            if (_board.GameOver)
            {
                _isGameActive = true;
                if (_board.Won) OnGameWon(); else OnGameLost();
                return;
            }

            _isGameActive = true;
        }
        catch (OperationCanceledException)
        {
            // 玩家重开或被取消，静默收场
        }
        catch (ObjectDisposedException)
        {
            // 生成期间玩家重开，CancellationTokenSource 已被释放；静默收场
        }
        catch (Exception ex)
        {
            FaceIcon = "😵";
            StatusMessage = $"生成失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_generationCts, cts))
            {
                _generationCts = null;
                IsBusy = false;
            }
            cts.Dispose();
            UpdateStatus();
            BoardChanged?.Invoke();
        }
    }

    // ==================== 玩家操作 ====================

    private void OnLeftClick(Cell cell)
    {
        if (_board == null || _board.GameOver) return;
        if (IsBusy) return;
        if (cell.State == CellState.Revealed || cell.State == CellState.Flagged) return;

        ClearHint();

        // 首击：先到后台生成一个保证无猜的雷盘
        if (!_board.FirstClickDone && !_isGameActive)
        {
            BeginGenerateBoard(cell.Row, cell.Col);
            return;
        }

        if (!_isGameActive)
        {
            _isGameActive = true;
            _stopwatch.Restart();
            _timer.Start();
        }

        var revealed = _board.Reveal(cell.Row, cell.Col);
        QueueRevealAnimations(revealed, cell.Row, cell.Col);
        RefreshCells(revealed);
        if (_board.GameOver && !_board.Won) OnGameLost();
        else if (_board.Won) OnGameWon();

        UpdateStatus();
        BoardChanged?.Invoke();
    }

    private void OnRightClick(Cell cell)
    {
        if (_board == null || _board.GameOver || IsBusy) return;
        ClearHint();
        bool changed = _board.ToggleFlag(cell.Row, cell.Col);
        // 真的插上/取消了旗 → 旗帜回弹；点在已翻开的格子上（没有变化）→ 轻触反馈
        _pendingAnimations.Clear();
        _pendingAnimations.Add((cell.Row, cell.Col,
            changed ? BoardCanvas.CellAnimKind.Flag : BoardCanvas.CellAnimKind.Pulse, 0));
        RefreshCell(cell);
        UpdateStatus();
        BoardChanged?.Invoke();
    }

    // ---- Chord + 闪烁（只闪 1 下） ----
    private DispatcherTimer? _flashTimer;
    private List<CellViewModel> _flashingCells = new();
    private int _flashCount;

    private void OnChord(Cell cell)
    {
        if (_board == null || _board.GameOver || IsBusy) return;
        if (cell.State != CellState.Revealed || cell.AdjacentMines == 0) return;

        ClearHint();
        var (revealed, flashing, hitMine) = _board.Chord(cell.Row, cell.Col);

        // 被点的那一格自己也弹一下：这是「收到这次快速排雷」的即时反馈。
        // 否则玩家点完只看到周围在变，被点的格子毫无动静（而单独右键点它时却有脉冲）。
        _pendingAnimations.Add((cell.Row, cell.Col, BoardCanvas.CellAnimKind.Pulse, 0));

        if (revealed.Count > 0)
        {
            QueueRevealAnimations(revealed, cell.Row, cell.Col);
            RefreshCells(revealed);
        }
        else if (!hitMine)
        {
            // 没有格子被掀开：说清楚是旗不够还是旗多了
            int flagsAround = _board.GetNeighbors(cell.Row, cell.Col).Count(n => n.State == CellState.Flagged);
            StatusMessage = flagsAround < cell.AdjacentMines
                ? $"周围只有 {flagsAround} 面旗，这个数字是 {cell.AdjacentMines}，先补齐旗子"
                : $"周围有 {flagsAround} 面旗，比数字 {cell.AdjacentMines} 多，先取消多余的旗";
        }

        if (flashing.Count > 0) StartFlashing(flashing);
        if (hitMine) OnGameLost();
        else if (_board.Won) OnGameWon();

        UpdateStatus();
        BoardChanged?.Invoke();
    }

    private void StartFlashing(List<Cell> cells)
    {
        StopFlashing();
        _flashingCells = cells
            .Select(c => _cellIndex.TryGetValue((c.Row, c.Col), out var vm) ? vm : null)
            .Where(vm => vm != null).Cast<CellViewModel>().ToList();
        _flashCount = 0;
        // 只闪一下，所以这一次亮起的时长就是全部观感。
        // 160ms ≈ 10 帧：足够看清是哪几格，又不至于让人等它消失。
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _flashTimer.Tick += OnFlashTick;
        _flashTimer.Start();
        foreach (var vm in _flashingCells) vm.IsFlashing = true;
        BoardChanged?.Invoke();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        // 只闪一下：StartFlashing 已经把格子点亮，这里第一次 Tick 就收尾。
        // 原实现把「交替亮灭」写成了无条件的 IsFlashing 取反（算出来的 on 从未参与判断），
        // 于是每 Tick 翻转一次、一共闪了两次。
        _flashCount++;
        if (_flashCount == 1) StopFlashing();
        BoardChanged?.Invoke();
    }

    private void StopFlashing()
    {
        _flashTimer?.Stop();
        _flashTimer = null;
        foreach (var vm in _flashingCells) vm.IsFlashing = false;
        _flashingCells.Clear();
    }

    private void OnGameWon()
    {
        _timer.Stop();
        _stopwatch.Stop();
        StopFlashing();          // 同上：胜利收尾时把高亮一起收掉
        _isGameActive = false;
        FaceIcon = "😎";

        double seconds = _stopwatch.Elapsed.TotalSeconds;
        bool isNewRecord = _scoreService.RecordWin(CurrentDifficulty.Name, seconds);
        UpdateBestRecord();

        // 收尾：未标旗的雷补上旗，剩余安全格全部翻开
        for (int r = 0; r < _board!.Rows; r++)
            for (int c = 0; c < _board.Cols; c++)
            {
                var cell = _board.Cells[r, c];
                if (cell.IsMine)
                {
                    if (cell.State != CellState.Flagged) cell.State = CellState.Flagged;
                }
                else if (cell.State != CellState.Revealed)
                {
                    cell.State = CellState.Revealed;
                }
            }

        // 上面是直接改 Cell.State，绕过了 GameBoard.ToggleFlag —— 而 FlaggedCount 只在
        // ToggleFlag 里维护。不同步的话，状态栏会把「刚补上的旗」漏算，
        // 于是每次胜利后都显示着「剩余 N 雷」不归零。
        _board.SyncFlaggedCount();

        RefreshAllCells();
        UpdateStatus();          // 立刻把「剩余雷数」刷新为 0，而不是停在上一次的值
        BoardChanged?.Invoke();

        string msg = $"你赢了！用时 {seconds:F1} 秒。";
        if (isNewRecord) msg += "\n🎉 新纪录！";
        StatusMessage = isNewRecord ? $"🎉 新纪录：{seconds:F1} 秒" : $"用时 {seconds:F1} 秒";
        MessageBox.Show(Application.Current?.MainWindow, msg, "胜利",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnGameLost()
    {
        _timer.Stop();
        _stopwatch.Stop();
        StopFlashing();          // 盘面已经结束了，别让黄色高亮盖在揭晓的雷区上继续闪
        _isGameActive = false;
        FaceIcon = "😵";
        _scoreService.RecordLoss(CurrentDifficulty.Name);
        _board!.RevealAllMines();
        RefreshAllCells();
        StatusMessage = "踩到雷了，按 F2 重开";
        BoardChanged?.Invoke();
    }

    private void UpdateStatus()
    {
        if (_board == null) return;
        // 不钳到 0：旗插多了要能看出来（经典扫雷就是显示负数）
        RemainingMines = _board.MineCount - _board.FlaggedCount;
        HasExcessFlags = RemainingMines < 0;
        if (_board.GameOver) FaceIcon = _board.Won ? "😎" : "😵";
    }

    private void UpdateBestRecord()
    {
        var record = _scoreService.GetRecord(CurrentDifficulty.Name);
        BestRecordText = $"🏆 {CurrentDifficulty.Name}最佳：{record.Display}";
    }

    // ---- O(1) 刷新 ----
    private void RefreshCell(Cell cell)
    {
        if (_cellIndex.TryGetValue((cell.Row, cell.Col), out var vm)) vm.Refresh();
    }

    private void RefreshCells(List<Cell> cells)
    {
        foreach (var cell in cells) RefreshCell(cell);
    }

    private void RefreshAllCells()
    {
        foreach (var vm in _cellIndex.Values) vm.Refresh();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_isGameActive) return;
        ElapsedTime = _stopwatch.Elapsed.TotalSeconds.ToString("F1");
    }
}
