using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Minesweeper.Models;

namespace Minesweeper.ViewModels;

public class CellViewModel : ViewModelBase
{
    // 静态缓存：避免每次 UpdateDisplay 都查 Application.Current.Resources
    private static Brush? _cachedCellCoveredFace;
    private static Brush? _cachedCellRevealed;
    private static Brush? _cachedWrongFlag;
    private static Brush? _cachedExploded;
    private static readonly Brush?[] CachedNumBrushes = new Brush?[9];

    static CellViewModel() => CacheBrushes();

    /// <summary>主题切换后调用，刷新静态 Brush 缓存</summary>
    public static void RefreshCachedBrushes() => CacheBrushes();

    private static void CacheBrushes()
    {
        var app = Application.Current;
        if (app == null) return;
        var res = app.Resources;
        _cachedCellCoveredFace = res["CellCoveredFace"] as Brush ?? Brushes.LightGray;
        _cachedCellRevealed = res["CellRevealed"] as Brush ?? Brushes.WhiteSmoke;
        _cachedWrongFlag = res["WrongFlagColor"] as Brush ?? Brushes.OrangeRed;
        _cachedExploded = res["ExplodedCell"] as Brush ?? Brushes.OrangeRed;
        CachedNumBrushes[1] = (res["Num1"] as Brush) ?? Brushes.Blue;
        CachedNumBrushes[2] = (res["Num2"] as Brush) ?? Brushes.Green;
        CachedNumBrushes[3] = (res["Num3"] as Brush) ?? Brushes.Red;
        CachedNumBrushes[4] = (res["Num4"] as Brush) ?? Brushes.DarkBlue;
        CachedNumBrushes[5] = (res["Num5"] as Brush) ?? Brushes.DarkRed;
        CachedNumBrushes[6] = (res["Num6"] as Brush) ?? Brushes.Teal;
        CachedNumBrushes[7] = (res["Num7"] as Brush) ?? Brushes.Black;
        CachedNumBrushes[8] = (res["Num8"] as Brush) ?? Brushes.Gray;
    }

    private readonly Cell _cell;
    private readonly Action<Cell> _onLeftClick;
    private readonly Action<Cell> _onRightClick;
    private readonly Action<Cell> _onChord;

    public int Row => _cell.Row;
    public int Col => _cell.Col;

    private CellState _state;
    /// <summary>格子状态。只读对外：真正的状态源是 <see cref="Cell"/>，由 <see cref="Refresh"/> 同步</summary>
    public CellState State => _state;

    private string _displayText = "";
    public string DisplayText { get => _displayText; set => Set(ref _displayText, value); }

    private Brush _textBrush = Brushes.Black;
    public Brush TextBrush { get => _textBrush; set => Set(ref _textBrush, value); }

    private Brush _bgBrush = Brushes.LightGray;
    public Brush BgBrush { get => _bgBrush; set => Set(ref _bgBrush, value); }

    private bool _isFlashing;
    public bool IsFlashing { get => _isFlashing; set => Set(ref _isFlashing, value); }

    public ICommand LeftClickCommand { get; }
    public ICommand RightClickCommand { get; }
    public ICommand ChordCommand { get; }

    public CellViewModel(Cell cell, Action<Cell> onLeftClick, Action<Cell> onRightClick, Action<Cell> onChord)
    {
        _cell = cell;
        _onLeftClick = onLeftClick;
        _onRightClick = onRightClick;
        _onChord = onChord;
        _state = cell.State;
        LeftClickCommand = new RelayCommand(() => _onLeftClick(_cell));
        RightClickCommand = new RelayCommand(() => _onRightClick(_cell));
        ChordCommand = new RelayCommand(() => _onChord(_cell));
        UpdateDisplay();
    }

    /// <summary>从 <see cref="Cell"/> 拉取最新状态并刷新显示</summary>
    public void Refresh()
    {
        _state = _cell.State;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        switch (_cell.State)
        {
            case CellState.Covered:
                DisplayText = "";
                BgBrush = _cachedCellCoveredFace!;
                break;

            case CellState.Flagged:
                // 游戏失败后，插在安全格上的旗显示为 ❌
                if (_cell.IsWrongFlag)
                {
                    DisplayText = "❌";
                    TextBrush = _cachedWrongFlag ?? Brushes.OrangeRed;
                }
                else
                {
                    DisplayText = "🚩";
                    TextBrush = CachedNumBrushes[1] ?? Brushes.Red;
                }
                BgBrush = _cachedCellCoveredFace!;
                break;

            case CellState.Revealed:
                if (_cell.IsMine)
                {
                    // 踩中的那一颗用不同底色 + 💥 标出来，其余雷是 💣
                    DisplayText = _cell.IsExploded ? "💥" : "💣";
                    TextBrush = Brushes.Black;
                    BgBrush = _cell.IsExploded
                        ? (_cachedExploded ?? Brushes.OrangeRed)
                        : Brushes.IndianRed;
                }
                else if (_cell.AdjacentMines == 0)
                {
                    DisplayText = "";
                    BgBrush = _cachedCellRevealed!;
                }
                else
                {
                    DisplayText = _cell.AdjacentMines.ToString();
                    TextBrush = CachedNumBrushes[_cell.AdjacentMines] ?? Brushes.Black;
                    BgBrush = _cachedCellRevealed!;
                }
                break;
        }
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
