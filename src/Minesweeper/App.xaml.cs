using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Minesweeper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：任何未预料的异常都要让玩家看到提示，而不是无声闪退
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 先把异常落盘再提示。以前只弹一个「已跳过该错误」，什么线索都不留，
        // 事后完全没法追查（尤其配合「点了没反应」这类症状）。
        LogError(e.Exception);

        // 带上主窗口作为 Owner，避免提示框被别的窗口盖住之后玩家看不到
        MessageBox.Show(
            Application.Current?.MainWindow,
            $"游戏遇到了一个未预料的错误：\n\n{e.Exception.Message}\n\n" +
            "已跳过该错误，可以继续玩（如果画面异常，按 F2 重开一局即可）。",
            "无猜扫雷", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    /// <summary>把未处理异常追加写入 %AppData%\Minesweeper\error.log（写不进去就算了，不影响游戏）</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minesweeper");
            Directory.CreateDirectory(dir);

            var text = new StringBuilder()
                .AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(Path.Combine(dir, "error.log"), text, Encoding.UTF8);
        }
        catch
        {
            // 记录失败绝不能引发新的异常
        }
    }
}
