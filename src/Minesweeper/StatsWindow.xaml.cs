using System.Windows;
using Minesweeper.Models;

namespace Minesweeper;

public partial class StatsWindow : Window
{
    public StatsWindow(Difficulty difficulty, DifficultyStats stats)
    {
        InitializeComponent();

        HeaderText.Text = $"{difficulty.Name}  {difficulty.Rows} × {difficulty.Cols} · {difficulty.MineCount} 雷";

        StatsText.Text =
            $"已玩局数      {stats.GamesPlayed}\n" +
            $"胜利 / 失败   {stats.GamesWon} / {stats.GamesLost}\n" +
            $"中途重开      {stats.GamesAbandoned}\n" +
            $"胜率          {stats.WinRatePercent:F1}%\n" +
            $"获胜平均用时  {(stats.GamesWon == 0 ? "—" : $"{stats.AverageWinSeconds:F1} 秒")}\n" +
            $"当前连胜      {stats.CurrentStreak}\n" +
            $"最长连胜      {stats.BestStreak}";

        var rows = stats.TopTimes
            .Select((record, index) => new
            {
                Rank = $"{index + 1}.",
                Time = record.Display,
                Date = record.Date?.ToString("yyyy-MM-dd") ?? ""
            })
            .ToList();

        RecordsList.ItemsSource = rows;
        RecordsList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
