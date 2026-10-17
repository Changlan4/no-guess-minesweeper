using System.IO;
using System.Windows;
using System.Windows.Threading;
using Xunit.Sdk;

namespace Minesweeper.Tests;

/// <summary>
/// WPF 测试辅助。
///
/// 关键点：所有 UI 测试共用一个 STA 线程（自带 Dispatcher），而不是每个测试各起一个。
/// 原因有两个：
///   1. 一个进程只能有一个 <see cref="Application"/> 实例，各起各的线程会直接崩；
///   2. 主题 XAML 里的 SolidColorBrush 是 Freezable，默认未冻结、带线程亲和性，
///      跨线程访问会抛「调用线程无法访问此对象」。
/// 共用一个线程后，这些坑一次性绕开。
/// </summary>
internal static class StaTestRunner
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>把动作投递到共享 STA 线程执行；异常带回调用方再抛，避免宿主进程直接崩溃</summary>
    public static void Run(Action action, int timeoutSeconds = 120)
    {
        var dispatcher = EnsureDispatcher();

        Exception? failure = null;
        var operation = dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (!operation.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            throw new TimeoutException("STA 测试超时（可能死循环或死锁）");

        if (failure != null)
            throw new XunitException($"STA 测试失败：{failure}");
    }

    /// <summary>进程内唯一的 Application 实例（必须在共享 STA 线程上调用）</summary>
    public static Application EnsureApplication()
    {
        lock (Gate)
        {
            if (Application.Current is { } existing) return existing;

            // pack://application:,,,/ 是按「入口程序集」解析的，而测试宿主的入口程序集是 testhost。
            // 必须先把它指到被测程序集，否则 App.xaml / ThemeService 里的主题资源都会找不到，
            // 测试环境也就无法反映真实的界面效果。只能在创建 Application 之前设置一次。
            if (Application.ResourceAssembly == null)
                Application.ResourceAssembly = typeof(Minesweeper.App).Assembly;

            var app = new Minesweeper.App
            {
                // 默认是 OnLastWindowClose：测试里关掉窗口会让 Application 自动关闭，
                // 之后再创建就会抛「不能在同一 AppDomain 中创建多个 Application 实例」。
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            // 初始化才会加载 App.xaml 里的全局资源（菜单深色样式、默认主题）。
            // 用裸 new Application() 会缺掉这些样式，导致「菜单文字看不清」这类问题测不出来。
            app.InitializeComponent();
            return app;
        }
    }

    private static Dispatcher EnsureDispatcher()
    {
        if (_dispatcher != null) return _dispatcher;

        lock (Gate)
        {
            if (_dispatcher != null) return _dispatcher;

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();      // 消息循环，供后续 Invoke 使用
            })
            {
                IsBackground = true,
                Name = "WPF 测试线程"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("无法启动 WPF 测试线程");
        }

        return _dispatcher!;
    }

    /// <summary>从输出目录读取主题文件（测试工程已把主题文件复制过去）</summary>
    public static ResourceDictionary LoadTheme(string name)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Themes", $"{name}.xaml");
        if (!File.Exists(file)) throw new FileNotFoundException($"找不到主题文件 {file}");
        return new ResourceDictionary { Source = new Uri(file) };
    }

    /// <summary>在视觉树里按类型查找后代元素</summary>
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;

            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
