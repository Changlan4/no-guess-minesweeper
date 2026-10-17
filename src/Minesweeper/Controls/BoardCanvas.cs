using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Minesweeper.Models;
using Minesweeper.ViewModels;

namespace Minesweeper.Controls;

/// <summary>
/// 棋盘自绘控件。
///
/// 性能要点（原实现每帧都要对 480 个格子重建全部绘制对象）：
///   1. 所有 Pen 静态缓存并 Freeze()。未冻结的 Freezable 会让 WPF 无法复用渲染指令，
///      原来每帧要 new 出上千个 Pen，是全盘重绘的主要开销来源。
///   2. FormattedText 按「文本 + 量化字号 + 颜色 + DPI」缓存。它构造时要走字体回退和
///      字形布局，很贵；原来每帧重建上百个。
///   3. 动画定时器只在真的有动画时运行，空闲时完全不产生重绘。
///   4. 集合变化通知做了合并，批量重建 480 个格子只会重绘一次。
/// </summary>
public class BoardCanvas : Canvas
{
    // ==================== 依赖属性 ====================

    public static readonly DependencyProperty CellsProperty =
        DependencyProperty.Register(nameof(Cells), typeof(IList<CellViewModel>), typeof(BoardCanvas),
            new PropertyMetadata(null, OnCellsChanged));

    /// <summary>
    /// 格子集合。
    /// 注意：集合变化订阅放在依赖属性回调里，而不是 CLR setter —— WPF 绑定直接写 SetValue，
    /// 根本不经过 CLR 包装器，写在 setter 里的订阅会变成哑的（原实现就是这个问题）。
    /// </summary>
    public IList<CellViewModel>? Cells
    {
        get => (IList<CellViewModel>?)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    private static void OnCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (BoardCanvas)d;
        if (e.OldValue is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= canvas.OnCellsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += canvas.OnCellsCollectionChanged;

        canvas.ResetInteractionState();
        canvas.InvalidateVisual();
    }

    public static readonly DependencyProperty CellSizeProperty =
        DependencyProperty.Register(nameof(CellSize), typeof(double), typeof(BoardCanvas),
            new PropertyMetadata(30.0, (d, _) =>
            {
                var c = (BoardCanvas)d;
                c.InvalidateMeasure();
                c.InvalidateVisual();
            }));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public static readonly DependencyProperty ColsProperty =
        DependencyProperty.Register(nameof(Cols), typeof(int), typeof(BoardCanvas),
            new PropertyMetadata(30, (d, _) => ((BoardCanvas)d).InvalidateMeasure()));

    public int Cols
    {
        get => (int)GetValue(ColsProperty);
        set => SetValue(ColsProperty, value);
    }

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(nameof(Rows), typeof(int), typeof(BoardCanvas),
            new PropertyMetadata(16, (d, _) => ((BoardCanvas)d).InvalidateMeasure()));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    // ==================== 事件 ====================

    public event Action<CellViewModel>? CellLeftUp;
    public event Action<CellViewModel>? CellRightUp;
    public event Action<CellViewModel>? CellChord;

    // ==================== 交互状态 ====================

    private CellViewModel? _leftDownCell;
    private CellViewModel? _pressedCell;
    private (int Row, int Col) _hoverCell = (-1, -1);
    private bool _redrawQueued;
    private bool _chordActive;

    /// <summary>提示高亮的格子（由 ViewModel 设置，(-1,-1) 表示当前没有提示）</summary>
    public (int Row, int Col) HintCell { get; set; } = (-1, -1);

    /// <summary>提示高亮的当前不透明度（由淡出动画驱动，0 = 不可见）</summary>
    public static readonly DependencyProperty HintOpacityProperty =
        DependencyProperty.Register(nameof(HintOpacity), typeof(double), typeof(BoardCanvas),
            new PropertyMetadata(0.0, (d, _) => ((BoardCanvas)d).InvalidateVisual()));

    public double HintOpacity
    {
        get => (double)GetValue(HintOpacityProperty);
        set => SetValue(HintOpacityProperty, value);
    }

    private Storyboard? _hintStoryboard;

    /// <summary>
    /// 提示闪烁：闪两下然后淡出消失。
    /// 提示需要闪两下——玩家要在一瞬间看清是哪一格，只一下容易错过；
    /// 而快速排雷的格子反馈是「操作已收到」，一下就够（见 <see cref="CellAnimKind.Pulse"/>）。
    /// 用透明度关键帧而不是硬亮硬灭，避免格子颜色陡跳造成「又闪了一下」的错觉。
    ///
    /// 清理旧动画必须用 Stop 而不是 Remove：<c>Storyboard.Remove</c> 会让该 storyboard 的
    /// Completed 在下一次 Begin 启动新动画的那一刻**再触发一次**，那次迟到的回调会把刚设好的
    /// HintCell 清成 (-1,-1)，于是动画照跑、画面全无 ——「重复按 H 只有第一次显示」就是这么来的。
    /// </summary>
    public void FlashHint(int row, int col)
    {
        _hintStoryboard?.Stop(this);
        _hintStoryboard = null;
        HintCell = (row, col);

        static KeyTime Ms(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));

        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(430) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, Ms(0)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, Ms(140)));   // 第一下：亮着
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.10, Ms(210)));  // 暗下去
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, Ms(290)));   // 第二下：再亮
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, Ms(430)));   // 淡出消失

        var storyboard = new Storyboard();
        storyboard.Children.Add(anim);
        Storyboard.SetTarget(anim, this);
        Storyboard.SetTargetProperty(anim, new PropertyPath(HintOpacityProperty));

        storyboard.Completed += (_, _) =>
        {
            // 身份守卫：只有「当前这一轮」才有资格清理共享状态。
            // 万一将来再出现迟到的回调，它会在这里被丢掉，而不是清掉别人的提示。
            if (!ReferenceEquals(_hintStoryboard, storyboard)) return;
            HintCell = (-1, -1);
            HintOpacity = 0;
            InvalidateVisual();
        };

        _hintStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    /// <summary>立刻撤掉提示高亮</summary>
    public void ClearHint()
    {
        _hintStoryboard?.Stop(this);
        _hintStoryboard = null;
        HintCell = (-1, -1);
        HintOpacity = 0;
        InvalidateVisual();
    }

    // ==================== 动画 ====================

    /// <summary>格子动画类型</summary>
    public enum CellAnimKind
    {
        /// <summary>掀开：从 0.84 弹到 1.0</summary>
        Reveal,
        /// <summary>插旗：放大回弹一下</summary>
        Flag,
        /// <summary>轻触反馈：点了但盘面没有变化时也给一点动静</summary>
        Pulse
    }

    private struct AnimState
    {
        public double Progress;
        public CellAnimKind Kind;
        public int DelayFrames;
    }

    private readonly Dictionary<(int Row, int Col), AnimState> _anims = new();
    private readonly List<(int Row, int Col)> _animKeyBuffer = new();
    private readonly DispatcherTimer _animTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    /// <summary>
    /// 是否启用动画。
    /// 软件渲染（<see cref="RenderCapability.Tier"/> 为 0，例如远程桌面、虚拟机、显卡驱动异常）
    /// 下整块棋盘的光栅化成本会高出一个数量级，此时宁可不播动画，也要保证点击跟手。
    /// </summary>
    private static readonly bool AnimationsEnabled = RenderCapability.Tier > 0;

    public BoardCanvas()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Focusable = true;          // 接收键盘焦点，支持方向键导航
        PreviewMouseDown += OnMouseDown;
        PreviewMouseLeftButtonUp += OnLeftUp;
        PreviewMouseRightButtonUp += OnRightUp;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        _animTimer.Tick += AdvanceAnimations;
    }

    public void TriggerRender() => InvalidateVisual();

    /// <summary>揭格动画；<paramref name="delayFrames"/> 用于连片展开时按距离错峰，形成扩散感</summary>
    public void AnimateReveal(int row, int col, int delayFrames = 0)
    {
        if (!AnimationsEnabled) return;
        _anims[(row, col)] = new AnimState { Progress = 0, Kind = CellAnimKind.Reveal, DelayFrames = delayFrames };
        if (!_animTimer.IsEnabled) _animTimer.Start();
    }

    /// <summary>插旗动画</summary>
    public void AnimateFlag(int row, int col)
    {
        if (!AnimationsEnabled) return;
        _anims[(row, col)] = new AnimState { Progress = 0, Kind = CellAnimKind.Flag, DelayFrames = 0 };
        if (!_animTimer.IsEnabled) _animTimer.Start();
    }

    /// <summary>
    /// 轻触反馈。用于「点了但盘面没变化」的情况——比如快速排雷时周围旗数不对，
    /// 此时既没有格子被掀开、也没有插旗，如果不给反馈玩家会以为按了没反应。
    /// </summary>
    public void AnimatePulse(int row, int col)
    {
        if (!AnimationsEnabled) return;
        _anims[(row, col)] = new AnimState { Progress = 0, Kind = CellAnimKind.Pulse, DelayFrames = 0 };
        if (!_animTimer.IsEnabled) _animTimer.Start();
    }

    private void AdvanceAnimations(object? sender, EventArgs e)
    {
        if (_anims.Count == 0)
        {
            _animTimer.Stop();
            return;
        }

        _animKeyBuffer.Clear();
        _animKeyBuffer.AddRange(_anims.Keys);

        foreach (var key in _animKeyBuffer)
        {
            if (!_anims.TryGetValue(key, out var anim)) continue;

            if (anim.DelayFrames > 0)
            {
                anim.DelayFrames--;
                _anims[key] = anim;
                continue;
            }

            anim.Progress += 0.17;   // 约 6 帧（≈100ms）：看得清动作，又不拖沓
            if (anim.Progress >= 1) _anims.Remove(key);
            else _anims[key] = anim;
        }

        if (_anims.Count == 0) _animTimer.Stop();
        InvalidateVisual();
    }

    private void ResetInteractionState()
    {
        _leftDownCell = null;
        _pressedCell = null;
        _hoverCell = (-1, -1);
        _cursorVisible = false;
        _chordActive = false;
        _anims.Clear();
        _animTimer.Stop();
    }

    private void OnCellsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 重建棋盘时会连续触发几百次通知，这里合并成一次重绘
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _redrawQueued = false;
            InvalidateVisual();
        }), DispatcherPriority.Render);
    }

    // ==================== 键盘导航 ====================

    public event Action<CellViewModel>? CellActivate;      // 空格 / 回车：掀开
    public event Action<CellViewModel>? CellFlagToggle;    // F：插旗 / 取消旗

    private int _cursorRow = -1;
    private int _cursorCol = -1;
    private bool _cursorVisible;

    private void EnsureCursor()
    {
        _cursorVisible = true;

        // 每次都钳位，而不是「首次可见时才修正」。
        // 切难度后棋盘尺寸会变小（高级 16×30 → 初级 9×9），旧坐标可能整个越界；
        // 原来这里一进来就 if (_cursorVisible) return，越界坐标再也得不到修正，
        // 表现为键盘光标消失、空格/F 彻底没反应，只能拿鼠标点一下棋盘才恢复。
        if (_cursorRow < 0 || _cursorRow >= Rows) _cursorRow = Rows / 2;
        if (_cursorCol < 0 || _cursorCol >= Cols) _cursorCol = Cols / 2;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down:
                EnsureCursor();
                switch (e.Key)
                {
                    case Key.Left: _cursorCol = Math.Max(0, _cursorCol - 1); break;
                    case Key.Right: _cursorCol = Math.Min(Cols - 1, _cursorCol + 1); break;
                    case Key.Up: _cursorRow = Math.Max(0, _cursorRow - 1); break;
                    case Key.Down: _cursorRow = Math.Min(Rows - 1, _cursorRow + 1); break;
                }
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Space or Key.Enter:
                if (_cursorVisible)
                {
                    var cell = CellAt(_cursorRow, _cursorCol);
                    if (cell != null) CellActivate?.Invoke(cell);
                    e.Handled = true;
                }
                break;

            case Key.F:
                if (_cursorVisible)
                {
                    var cell = CellAt(_cursorRow, _cursorCol);
                    if (cell != null) CellFlagToggle?.Invoke(cell);
                    e.Handled = true;
                }
                break;
        }
    }

    private CellViewModel? CellAt(int row, int col)
    {
        var cells = Cells;
        if (cells == null || row < 0 || col < 0 || row >= Rows || col >= Cols) return null;
        int idx = row * Cols + col;
        return idx < cells.Count ? cells[idx] : null;
    }

    /// <summary>轻触反馈的叠加层：一次性的白色高亮，随 alpha 淡出</summary>
    private static void DrawPulse(DrawingContext dc, double alpha, Rect rect)
    {
        if (alpha <= 0.01) return;

        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), 255, 255, 255));
        brush.Freeze();
        dc.DrawRectangle(brush, null, rect);
    }

    /// <summary>格子上的叠加层：提示高亮 + 键盘光标</summary>
    private void DrawOverlays(DrawingContext dc, int row, int col, double sz)
    {
        var (hr, hc) = HintCell;
        if (hr == row && hc == col && HintOpacity > 0.01)
        {
            // 提示格：暖黄半透明填充 + 描边，整体按 HintOpacity 淡出（渐隐而不是瞬间熄灭）
            byte fillAlpha = (byte)Math.Clamp(84 * HintOpacity, 0, 255);
            byte edgeAlpha = (byte)Math.Clamp(255 * HintOpacity, 0, 255);

            var fill = new SolidColorBrush(Color.FromArgb(fillAlpha, 255, 205, 100));
            fill.Freeze();

            var baseColor = (TryGetBrush("FlashColor") as SolidColorBrush)?.Color ?? Colors.Gold;
            var edge = new SolidColorBrush(Color.FromArgb(edgeAlpha, baseColor.R, baseColor.G, baseColor.B));
            edge.Freeze();

            var hintPen = new Pen(edge, 3);
            hintPen.Freeze();

            dc.DrawRectangle(fill, hintPen, new Rect(col * sz + 2, row * sz + 2, sz - 4, sz - 4));
        }

        if (!_cursorVisible || _cursorRow != row || _cursorCol != col) return;
        var cursorPen = CachedPen(TryGetBrush("AccentColor") ?? Brushes.DeepSkyBlue, 2.5);
        dc.DrawRectangle(null, cursorPen, new Rect(col * sz + 1.5, row * sz + 1.5, sz - 3, sz - 3));
    }

    // ==================== 鼠标 ====================

    private CellViewModel? HitTestCell(Point pos)
    {
        var cells = Cells;
        if (cells == null) return null;

        // 先挡掉负坐标。(int) 对负数是向零取整，-5/30 会变成 0，
        // 于是棋盘左上外侧会被当成 (0,0) —— 玩家把棋子拖出左上角再松手就会误掀那一格。
        if (pos.X < 0 || pos.Y < 0) return null;

        int col = (int)(pos.X / CellSize), row = (int)(pos.Y / CellSize);
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return null;
        int idx = row * Cols + col;
        return idx >= 0 && idx < cells.Count ? cells[idx] : null;
    }

    private void SetPressed(CellViewModel? cell)
    {
        if (ReferenceEquals(_pressedCell, cell)) return;
        _pressedCell = cell;
        InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var cell = HitTestCell(e.GetPosition(this));
        var pos = cell == null ? (-1, -1) : (cell.Row, cell.Col);
        if (pos == _hoverCell) return;
        _hoverCell = pos;
        InvalidateVisual();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverCell == (-1, -1)) return;
        _hoverCell = (-1, -1);
        InvalidateVisual();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.RightButton == MouseButtonState.Pressed)
        {
            var c = HitTestCell(e.GetPosition(this));
            if (c != null)
            {
                CellChord?.Invoke(c);

                // 和弦之后玩家的手指往往还按着两个键。在它们全部松开之前，
                // 随后的右键 Up 绝不能再被当成「插旗」——否则一松手就会多出一面旗
                //（而且常常正好插在刚排完雷的格子上，变成错旗）。
                _chordActive = true;
                // 和弦也要捕获鼠标：否则两键按住拖出棋盘再松手就收不到 Up，
                // _chordActive 会留在 true，把之后的第一次点击吃掉。
                CaptureMouse();
            }
            _leftDownCell = null;
            SetPressed(null);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Focus();                        // 拿到键盘焦点，方向键才能接着用
            _cursorVisible = false;         // 一旦用鼠标，就收起键盘光标
            _leftDownCell = HitTestCell(e.GetPosition(this));
            SetPressed(_leftDownCell);
            // 捕获鼠标：按下后拖出窗口再松开也能收到 Up，避免按下态残留
            if (_leftDownCell != null) CaptureMouse();
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        SetPressed(null);
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (_chordActive)
        {
            // 和弦那一轮操作还没收尾：这次抬起不掀格子
            _leftDownCell = null;
            e.Handled = true;
            EndChordIfReleased();
            return;
        }

        var cell = HitTestCell(e.GetPosition(this));
        if (_leftDownCell != null && cell == _leftDownCell && Mouse.RightButton != MouseButtonState.Pressed)
            CellLeftUp?.Invoke(cell);

        _leftDownCell = null;
        EndChordIfReleased();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        SetPressed(null);

        if (_chordActive)
        {
            // 和弦剩下的那次右键抬起：吃掉，不插旗
            e.Handled = true;
            EndChordIfReleased();
            return;
        }

        if (_leftDownCell == null)
        {
            var c = HitTestCell(e.GetPosition(this));
            if (c != null)
            {
                CellRightUp?.Invoke(c);
                e.Handled = true;   // 只有真的点在棋盘上才吃掉事件
            }
        }
        else
        {
            e.Handled = true;
        }
    }

    /// <summary>两个键都松开之后，和弦状态才算结束</summary>
    private void EndChordIfReleased()
    {
        if (!_chordActive) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (Mouse.RightButton == MouseButtonState.Pressed) return;
        _chordActive = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ==================== 绘制 ====================

    protected override Size MeasureOverride(Size constraint)
    {
        double want = CellSize * Cols;
        double wantH = CellSize * Rows;
        double w = double.IsInfinity(constraint.Width) ? want : Math.Min(want, constraint.Width);
        double h = double.IsInfinity(constraint.Height) ? wantH : Math.Min(wantH, constraint.Height);
        return new Size(w > 0 ? w : want, h > 0 ? h : wantH);
    }

    private static readonly Typeface EmojiFace = new("Segoe UI");
    private static readonly Typeface NumberFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush HoverOverlay = Frozen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
    private static readonly Brush PressOverlay = Frozen(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)));

    /// <summary>Pen 缓存：避免每次重绘都 new 出上千个画笔</summary>
    private static readonly Dictionary<(uint Argb, double Thickness), Pen> PenCache = new();

    /// <summary>文本缓存：FormattedText 很重，按「文本+字号+颜色+DPI」复用</summary>
    private readonly Dictionary<(string Text, int FontKey, uint Argb, bool Emoji, int DpiKey), FormattedText> _textCache = new();

    private static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
        return brush;
    }

    private static uint ArgbOf(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Pen CachedPen(Brush brush, double thickness)
    {
        uint argb = brush is SolidColorBrush s ? ArgbOf(s.Color) : 0xFF808080u;
        var key = (argb, thickness);
        if (PenCache.TryGetValue(key, out var pen)) return pen;

        pen = new Pen(brush, thickness);
        pen.Freeze();   // 冻结后才能被 WPF 复用渲染指令
        PenCache[key] = pen;
        return pen;
    }

    private FormattedText CachedText(string text, double fontSize, Brush brush, bool emoji, double dpi)
    {
        uint argb = brush is SolidColorBrush s ? ArgbOf(s.Color) : 0xFF000000u;
        var key = (text, (int)Math.Round(fontSize * 2), argb, emoji, (int)Math.Round(dpi * 100));
        if (_textCache.TryGetValue(key, out var cached)) return cached;

        if (_textCache.Count > 3000) _textCache.Clear();   // 拖拽缩放会不断产生新字号，加个上限

        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            emoji ? EmojiFace : NumberFace, fontSize, brush, dpi);
        _textCache[key] = ft;
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var cells = Cells;
        if (cells == null || CellSize < 2) return;

        double sz = CellSize;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var brushHighlight = TryGetBrush("CellHighlight") ?? Brushes.White;
        var brushShadow = TryGetBrush("CellShadow") ?? Brushes.Gray;
        var brushFlag = TryGetBrush("FlagColor") ?? Brushes.Red;
        var brushFlash = TryGetBrush("FlashColor") ?? Brushes.Orange;

        var penRevealedEdge = CachedPen(brushShadow, 1);
        var penHighlight = CachedPen(brushHighlight, 2);
        var penShadow = CachedPen(brushShadow, 2);

        for (int i = 0; i < cells.Count; i++)
        {
            var vm = cells[i];
            int r = i / Cols, c = i % Cols;
            double cx = c * sz + sz / 2, cy = r * sz + sz / 2;

            // 动画状态。
            // Reveal / Flag 用缩放表现；Pulse 不用缩放 —— 缩放与揭格动画肉眼难以区分，
            // 改成一次性高亮淡出，和 H 提示的两下黄闪也彻底区分开。
            double scale = 1;
            double pulseAlpha = 0;
            if (_anims.TryGetValue((r, c), out var anim))
            {
                switch (anim.Kind)
                {
                    case CellAnimKind.Reveal:
                        scale = 0.84 + 0.16 * EaseOutCubic(anim.Progress);
                        break;
                    case CellAnimKind.Flag:
                        scale = 1.0 + 0.22 * Math.Sin(anim.Progress * Math.PI) * (1 - anim.Progress);
                        break;
                    default:
                        // 只亮一次然后淡出，进度 0→1 单调衰减，不可能出现第二下
                        pulseAlpha = 0.45 * (1 - anim.Progress);
                        break;
                }
            }

            double half = sz * scale / 2;
            var rect = new Rect(cx - half, cy - half, sz * scale, sz * scale);

            if (vm.IsFlashing)
            {
                dc.DrawRectangle(brushFlash, null, new Rect(c * sz, r * sz, sz, sz));
                DrawOverlays(dc, r, c, sz);
                continue;
            }

            if (vm.State == CellState.Revealed)
            {
                dc.DrawRectangle(vm.BgBrush, penRevealedEdge, rect);
                DrawCellText(dc, vm.DisplayText, vm.TextBrush, cx, cy, scale, sz, dpi);
                DrawPulse(dc, pulseAlpha, rect);
                DrawOverlays(dc, r, c, sz);
                continue;
            }

            // ---- 未翻开的格子：画出浮雕边框 ----
            bool isPressed = _pressedCell != null && _pressedCell.Row == r && _pressedCell.Col == c;
            bool isHovered = _hoverCell == (r, c);

            dc.DrawRectangle(vm.BgBrush, null, new Rect(rect.X + 2, rect.Y + 2,
                Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 4)));

            var topLeftA = new Point(rect.X, rect.Y + 1);
            var topLeftB = new Point(rect.X + rect.Width - 1, rect.Y + 1);
            var topLeftC = new Point(rect.X + 1, rect.Y);
            var topLeftD = new Point(rect.X + 1, rect.Y + rect.Height - 1);
            var bottomRightA = new Point(rect.X, rect.Y + rect.Height - 1);
            var bottomRightB = new Point(rect.X + rect.Width, rect.Y + rect.Height - 1);
            var bottomRightC = new Point(rect.X + rect.Width - 1, rect.Y);
            var bottomRightD = new Point(rect.X + rect.Width - 1, rect.Y + rect.Height);

            if (isPressed)
            {
                // 按下：把高光与阴影对调，做出「凹下去」的手感（经典扫雷的按压反馈）
                dc.DrawLine(penShadow, topLeftA, topLeftB);
                dc.DrawLine(penShadow, topLeftC, topLeftD);
                dc.DrawLine(penHighlight, bottomRightA, bottomRightB);
                dc.DrawLine(penHighlight, bottomRightC, bottomRightD);
                dc.DrawRectangle(PressOverlay, null, new Rect(rect.X + 2, rect.Y + 2,
                    Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 4)));
            }
            else
            {
                dc.DrawLine(penHighlight, topLeftA, topLeftB);
                dc.DrawLine(penHighlight, topLeftC, topLeftD);
                dc.DrawLine(penShadow, bottomRightA, bottomRightB);
                dc.DrawLine(penShadow, bottomRightC, bottomRightD);
                if (isHovered)
                    dc.DrawRectangle(HoverOverlay, null, new Rect(rect.X + 2, rect.Y + 2,
                        Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 4)));
            }

            DrawCellText(dc, vm.DisplayText, BrushForText(vm, brushFlag), cx, cy, scale, sz, dpi);
            DrawOverlays(dc, r, c, sz);
        }
    }

    /// <summary>
    /// 文本颜色：插着的旗用主题里的 FlagColor（其余沿用 CellViewModel 选好的数字色）。
    /// 这样主题里的「旗色」才真正作用在棋盘上；此前 OnRender 取了 FlagColor 却没用，
    /// 改主题的旗色只影响状态栏文字。
    /// </summary>
    private static Brush BrushForText(CellViewModel vm, Brush flagBrush) =>
        vm.State == CellState.Flagged && vm.DisplayText == "🚩" ? flagBrush : vm.TextBrush;

    private void DrawCellText(DrawingContext dc, string text, Brush brush,
        double cx, double cy, double scale, double sz, double dpi)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = CachedText(text, sz * 0.5 * scale, brush, IsEmoji(text), dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    private static bool IsEmoji(string text) => text.Length > 0 && text[0] > 0x2000;

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static Brush? TryGetBrush(string key) => Application.Current?.Resources[key] as Brush;
}
