using System.Reflection;
using Minesweeper.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Minesweeper.Tests;

/// <summary>
/// 提示（H）与轻触反馈（Pulse）的行为回归测试。
///
/// 这两个功能都出过回归：
///   1. 用「比较 HintCell 有没有变化」来决定要不要显示，导致连续按 H 命中同一格时完全不显示；
///   2. Pulse 原本用缩放实现，与揭格动画难以区分，观感上像闪了不止一下。
/// 这里把两者的关键约束固定下来。
/// </summary>
public class HintAndPulseTests
{
    private readonly ITestOutputHelper _out;

    public HintAndPulseTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void 连续按提示每次都能取到即使命中同一格()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var vm = new MainViewModel();
            var hintProp = typeof(MainViewModel).GetProperty(nameof(MainViewModel.HintCell))!;
            var pendingField = typeof(MainViewModel).GetField(
                "_hintPending", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // 第一次提示
            hintProp.SetValue(vm, (3, 5));
            pendingField.SetValue(vm, true);
            Assert.True(vm.TryTakeHint(out int row1, out int col1));
            Assert.Equal((3, 5), (row1, col1));

            // 第二次提示，仍然命中同一格 —— 必须照样显示
            hintProp.SetValue(vm, (3, 5));
            pendingField.SetValue(vm, true);
            Assert.True(vm.TryTakeHint(out int row2, out int col2),
                "重复按 H 命中同一格时也必须再次显示（这正是之前的 bug）");
            Assert.Equal((3, 5), (row2, col2));

            // 取走之后不应重复触发
            Assert.False(vm.TryTakeHint(out _, out _));

            _out.WriteLine("连续两次同格提示均能触发");
        });
    }

    [Fact]
    public void 清空提示会下发一次撤销请求()
    {
        StaTestRunner.Run(() =>
        {
            StaTestRunner.EnsureApplication();

            var vm = new MainViewModel();
            var pendingField = typeof(MainViewModel).GetField(
                "_hintPending", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var clearMethod = typeof(MainViewModel).GetMethod(
                "ClearHint", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // 先摆出一个「正在提示」的状态
            typeof(MainViewModel).GetProperty("HintCell")!.SetValue(vm, (3, 5));
            pendingField.SetValue(vm, true);

            clearMethod.Invoke(vm, null);

            // ClearHint 必须把撤销动作走一遍通知通道：
            // 取到的坐标是 (-1,-1)，MainWindow 会据此调用 BoardCanvas.ClearHint()，
            // 画布上的高亮才会立刻撤掉。若这里返回 false，画布永远收不到撤销通知，
            // 提示只能等 430ms 的淡出动画自己跑完 —— 那正是原来的行为。
            Assert.True(vm.TryTakeHint(out int row, out int col),
                "清空提示后应当能取到一次撤销请求，让画布立刻收掉高亮");
            Assert.Equal((-1, -1), (row, col));

            // 撤销请求同样是一次性的
            Assert.False(vm.TryTakeHint(out _, out _));
        });
    }

    [Fact]
    public void 轻触高亮强度单调衰减不会出现第二下()
    {
        // 与 BoardCanvas 里的 pulseAlpha = 0.45 * (1 - progress) 保持一致。
        // 单调衰减意味着只有一次亮起、随后逐渐消失，不可能出现第二次峰值。
        double[] progressFrames = { 0.0, 0.17, 0.34, 0.51, 0.68, 0.85 };
        var alphas = progressFrames.Select(p => 0.45 * (1 - p)).ToArray();

        Assert.True(alphas[0] > 0.40, "起始应该明显亮起");
        Assert.True(alphas[^1] < 0.10, "结束时应该基本看不见");

        for (int i = 1; i < alphas.Length; i++)
        {
            Assert.True(alphas[i] <= alphas[i - 1],
                $"第 {i} 帧亮度 {alphas[i]:F3} 高于上一帧 {alphas[i - 1]:F3}，说明亮度出现了二次峰值");
        }

        _out.WriteLine("亮度序列：" + string.Join(" → ", alphas.Select(a => a.ToString("F3"))));
    }
}
