using System.Reflection;
using GalgameManager.Enums;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 决定「确认游戏信息」弹窗的源列表内容：**原来的内置项一个不动，后面追加所有已安装的源插件**。
///
/// <para>
/// 可用源不自己猜，而是问宿主 —— 用的是宿主设置页那句一模一样的话（1.10.2 的
/// <c>SettingsViewModel</c> 第 699 行）：
/// <c>RssHelperX.GetAvailableTypes(App.GetService&lt;IGalgameCollectionService&gt;())</c>。
/// 它返回的就是 <c>PhraserList</c> 的键，也就是"内置源 + 已注册的插件源"。
/// </para>
/// <para>
/// 我们只把其中 <b>(int)RssType &gt;= 100 的插件源</b>追加进去：
/// 内置的 5 项（Bangumi/Vndb/Ymgal/Cngal/Hikarinagi）保持原样，
/// <c>Mixed</c>/<c>Steam</c>/<c>PotatoVn</c> 这些宿主本来就没放进这个弹窗的也不去动。
/// </para>
/// </summary>
internal static class SourceListProvider
{
    /// <summary>查「有哪些可用源」。默认走宿主反射；离线验证时可以替换成假的。</summary>
    internal static Func<IEnumerable<RssType>> QueryHost { get; set; } = QueryHostByReflection;

    /// <summary>出错时往这里说话（由补丁层接到宿主的日志上）。</summary>
    internal static Action<string>? Log { get; set; }

    /// <summary>合并结果缓存 10 秒：getter 每次弹窗会被调用十几次，没必要每次都反射一遍。</summary>
    private static List<RssType>? _cached;
    private static DateTime _cachedAtUtc;

    /// <summary>把「已安装的源插件」追加到弹窗原有列表后面。</summary>
    internal static List<RssType> Build(IEnumerable<RssType> original)
    {
        try
        {
            return Merge(original, Available());
        }
        catch (Exception e)
        {
            Log?.Invoke($"查可用信息源失败，本次不改动列表：{e.GetType().Name}: {e.Message}");
            return original.ToList();
        }
    }

    /// <summary>纯函数：原有项原样保留，只追加 (int)&gt;=100 的插件源，并按显示名去重。</summary>
    internal static List<RssType> Merge(IEnumerable<RssType> original, IEnumerable<RssType> available)
    {
        var result = new List<RssType>();
        var seenValues = new HashSet<int>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in original)
        {
            if (!seenValues.Add((int)type)) continue;
            result.Add(type);
            seenNames.Add(DisplayName(type));
        }

        foreach (var type in available)
        {
            if ((int)type < IdRouter.PluginIdThreshold) continue;
            if (!seenValues.Add((int)type)) continue;

            // 显示名重复就不加了：避免同一个源在列表里出现两次
            // （例如某插件给某个内置源又注册了一遍、却用了 >=100 的 id）
            var name = DisplayName(type);
            if (name.Length > 0 && !seenNames.Add(name)) continue;

            result.Add(type);
        }

        return result;
    }

    private static List<RssType> Available()
    {
        if (_cached is not null && (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < 10) return _cached;

        _cached = QueryHost().ToList();
        _cachedAtUtc = DateTime.UtcNow;
        return _cached;
    }

    /// <summary>问宿主：<c>RssHelperX.GetAvailableTypes(App.GetService&lt;IGalgameCollectionService&gt;())</c>。</summary>
    private static IEnumerable<RssType> QueryHostByReflection()
    {
        var appType = HostReflection.FindType("GalgameManager.App")
                      ?? throw new InvalidOperationException("找不到宿主类型 GalgameManager.App");
        var helperType = HostReflection.FindType("GalgameManager.Helpers.EnumHelpers.RssHelperX")
                         ?? throw new InvalidOperationException("找不到宿主类型 RssHelperX");
        // 这个接口是宿主程序集里的类型（基库不暴露），所以只能按名字找
        var serviceType = HostReflection.FindType("GalgameManager.Contracts.Services.IGalgameCollectionService")
                          ?? throw new InvalidOperationException("找不到 IGalgameCollectionService");

        var getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static)
                         ?? throw new InvalidOperationException("找不到 App.GetService<T>()");
        var service = getService.MakeGenericMethod(serviceType).Invoke(null, null);
        if (service is null) throw new InvalidOperationException("App.GetService 返回了 null");

        var getAvailable = helperType.GetMethod("GetAvailableTypes", BindingFlags.Public | BindingFlags.Static)
                           ?? throw new InvalidOperationException("找不到 RssHelperX.GetAvailableTypes()");

        if (getAvailable.Invoke(null, [service]) is IEnumerable<RssType> types) return types;
        throw new InvalidOperationException("GetAvailableTypes 返回了意外的类型");
    }

    /// <summary>给日志用：把控件里的项（可能是装箱的 RssType）转成显示名。</summary>
    internal static string DisplayNameOf(object? item) =>
        item is RssType type ? DisplayName(type) : item?.ToString() ?? "?";

    /// <summary>源的显示名，走宿主自己的本地化（<c>EnumExtension.GetLocalized</c>）。
    /// 结果按枚举值缓存：<c>ToString()</c> 补丁会频繁调到这里。</summary>
    internal static string DisplayName(RssType type)
    {
        if (Names.TryGetValue((int)type, out var cached)) return cached;

        string name;
        try
        {
            name = NameSource(type) is { Length: > 0 } hit ? hit : type.ToString();
        }
        catch
        {
            name = type.ToString();
        }

        Names[(int)type] = name;
        return name;
    }

    /// <summary>显示名的来源。默认问宿主；离线验证可以换成假的。</summary>
    internal static Func<RssType, string?> NameSource { get; set; } = NameByHost;

    private static readonly Dictionary<int, string> Names = [];

    private static string? NameByHost(RssType type)
    {
        var enumExtension = HostReflection.FindType("GalgameManager.Helpers.EnumExtension");
        var method = enumExtension?.GetMethod("GetLocalized", BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Enum)], null);
        return method?.Invoke(null, [(Enum)type]) as string;
    }

    /// <summary>清掉缓存（离线验证用）。</summary>
    internal static void ResetCache()
    {
        _cached = null;
        _cachedAtUtc = default;
        Names.Clear();
    }
}

/// <summary>在已加载的程序集里按全名找宿主类型（插件 ALC 对宿主程序集返回 null，会落到默认上下文，所以能拿到同一份）。</summary>
internal static class HostReflection
{
    internal static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type is not null) return type;
            }
            catch
            {
                // 个别程序集读不了类型，跳过
            }
        }

        return null;
    }
}
