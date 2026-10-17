using System.Collections.ObjectModel;
using System.Windows;

namespace Minesweeper.Services;

/// <summary>主题管理服务</summary>
public static class ThemeService
{
    public record ThemeInfo(string Name, string DisplayName, string Description);

    public static readonly ObservableCollection<ThemeInfo> Themes = new()
    {
        new("MintNoir", "午夜薄荷", "深墨绿底，护眼耐看"),
        new("RoseNoir", "午夜樱粉", "深紫棕底，柔和暗粉"),
        new("VioletNoir", "午夜紫罗兰", "深紫底，雾感紫"),
        new("OceanNoir", "午夜海蓝", "深蓝灰底，冷静克制")
    };

    private static ThemeInfo _current = Themes[0];
    public static ThemeInfo Current => _current;

    /// <summary>
    /// 历史主题名 → 当前主题名。
    /// 配色换过几轮，老配置里存的名字可能已经不存在了；直接掉回默认会让人觉得「我的设置丢了」，
    /// 所以按语义映射到最接近的新主题。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ClassicGray"] = "MintNoir",
        ["ForestGreen"] = "MintNoir",
        ["OceanBlue"] = "OceanNoir",
        ["SunsetOrange"] = "RoseNoir",
        ["MacaronMint"] = "MintNoir",
        ["SakuraPink"] = "RoseNoir",
        ["Lavender"] = "VioletNoir",
        ["SeaSaltBlue"] = "OceanNoir"
    };

    /// <summary>把（可能是历史的）主题名解析成当前可用的主题，无法识别时回退到默认</summary>
    public static ThemeInfo Resolve(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName)) return Themes[0];

        if (Themes.FirstOrDefault(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        if (LegacyNames.TryGetValue(themeName, out var mapped))
            return Themes.FirstOrDefault(t => t.Name == mapped) ?? Themes[0];

        return Themes[0];
    }

    public static void ApplyTheme(string themeName)
    {
        var theme = Resolve(themeName);

        var app = Application.Current;
        if (app == null) return;

        ResourceDictionary dict;
        try
        {
            // 用 pack URI 全限定到本程序集。
            // 相对 URI（"Themes/X.xaml"）按入口程序集解析，在测试宿主等场景下会找不到资源。
            dict = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/Minesweeper;component/Themes/{theme.Name}.xaml",
                    UriKind.Absolute)
            };
        }
        catch (Exception)
        {
            // 主题资源加载失败：保留当前配色不动。
            // 注意 _current 也不能改 —— 否则 ThemeService.Current 会与实际生效的配色长期不一致。
            return;
        }

        // 只移除已知的主题字典。
        // 原来用 src.Contains("Themes/") 判断，将来一旦加入别的资源字典就可能被误删。
        var toRemove = app.Resources.MergedDictionaries.Where(IsThemeDictionary).ToList();
        foreach (var rd in toRemove)
            app.Resources.MergedDictionaries.Remove(rd);

        app.Resources.MergedDictionaries.Add(dict);
        _current = theme;   // 确认换成功之后再更新，顺序不能颠倒
    }

    private static bool IsThemeDictionary(ResourceDictionary rd)
    {
        var src = rd.Source?.OriginalString;
        if (string.IsNullOrEmpty(src)) return false;
        return Themes.Any(t => src.EndsWith($"{t.Name}.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
