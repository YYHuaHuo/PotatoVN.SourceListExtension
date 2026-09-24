using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml;
using PotatoVN.App.SourceListExtension;

namespace SourceListHarness;

/// <summary>
/// 「信息源扩展」插件的离线自查。
///
/// <para>
/// 宿主那个弹窗继承 WinUI 的 ContentDialog，控制台里没法实例化，所以这里造一个**同形状的假弹窗**
/// <see cref="MockDialog"/>：成员名/签名、以及那些 <c>Galgame.Ids[(int)type]</c> 直接下标
/// 全部照抄宿主 1.10.2 的 <c>ConfirmGalInfoDialog</c>。
/// 然后把真正的补丁打到它身上、真的 new 出来、真的跑 Update / FetchInfo ——
/// 如果 IL 改写有任何问题，这里就会像真宿主一样 <c>IndexOutOfRangeException</c>。
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>假装装了一个信息源插件（用 DLsite 搜刮器的真实 ParserId）。</summary>
    private static readonly RssType PluginSource = (RssType)769164;

    /// <summary>
    /// 宿主 1.10.2 里 <c>RssType.Hikarinagi = 8</c>，本插件工程引用的基库（更早的提交）还没这个成员，
    /// 所以按数值用。宿主弹窗的内置 5 项 = Bangumi/Vndb/Ymgal/Cngal/Hikarinagi。
    /// </summary>
    private static readonly RssType Hikarinagi = (RssType)8;

    private static async Task<int> Main(string[] args)
    {
        var fail = 0;

        void Check(string what, bool ok, string got)
        {
            Console.WriteLine($"  {(ok ? "[ok]" : "[!!]")} {what}: {got}");
            if (!ok) fail++;
        }

        var host = HostApiProxy.Create();

        // ---------------------------------------------------------------
        Console.WriteLine("=== 1. IdRouter：插件 id 走 IdForPlugins，内置 id 走 Ids ===");
        {
            var routerGame = new Galgame("测试游戏");
            IdRouter.Set(routerGame, 1 /*Bangumi*/, "12345");
            Check("内置源写进 Ids", routerGame.Ids[1] == "12345", routerGame.Ids[1] ?? "(null)");

            IdRouter.Set(routerGame, 769164, "RJ00000001");
            Check("插件源写进 IdForPlugins", routerGame.IdForPlugins.GetValueOrDefault(769164) == "RJ00000001",
                routerGame.IdForPlugins.GetValueOrDefault(769164) ?? "(null)");
            Check("Ids 数组没被撑大", routerGame.Ids.Length == Galgame.PhraserNumber, routerGame.Ids.Length.ToString());
            Check("读插件源 = 读字典", IdRouter.Get(routerGame, 769164) == "RJ00000001", IdRouter.Get(routerGame, 769164) ?? "(null)");
            Check("读内置源 = 读数组", IdRouter.Get(routerGame, 1) == "12345", IdRouter.Get(routerGame, 1) ?? "(null)");
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 2. 列表合并：内置项原样，只追加 (int)>=100 的插件源 ===");
        {
            List<RssType> original = [RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, Hikarinagi];
            List<RssType> available =
            [
                RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, Hikarinagi,
                RssType.Mixed, RssType.Steam, RssType.PotatoVn, // 宿主本来就没放进弹窗的，不该被加进来
                PluginSource,
            ];

            var merged = SourceListProvider.Merge(original, available);
            Check("只装了 1 个源插件 ⇒ 6 项", merged.Count == 6, string.Join(", ", merged.Select(t => $"{(int)t}")));
            Check("第 6 项就是插件源", merged.Last() == PluginSource, merged.Last().ToString());
            Check("内置 5 项顺序不变", merged.Take(5).SequenceEqual(original), string.Join(", ", merged.Take(5)));
            Check("Mixed/Steam/PotatoVn 没被塞进来",
                !merged.Contains(RssType.Mixed) && !merged.Contains(RssType.Steam) && !merged.Contains(RssType.PotatoVn),
                string.Join(", ", merged));

            var merged2 = SourceListProvider.Merge(original, [.. available, (RssType)628112]);
            Check("再多装一个源插件 ⇒ 7 项", merged2.Count == 7, string.Join(", ", merged2.Select(t => (int)t)));
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 3. 结构校验：像宿主的能过，缺成员的会被拒 ===");
        {
            var shape = DialogMembers.Discover(typeof(MockDialog), out var error);
            Check("假弹窗（同形状）通过校验", shape is not null, shape is null ? error : "通过");

            var bad = DialogMembers.Discover(typeof(BrokenDialog), out var badError);
            Check("缺 _originalIds 的类型被拒", bad is null && badError.Length > 0, badError);
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 2. 列表合并：内置项原样，只追加 (int)>=100 的插件源 ===");
        {
            List<RssType> original = [RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, Hikarinagi];
            List<RssType> available =
            [
                RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, Hikarinagi,
                RssType.Mixed, RssType.Steam, RssType.PotatoVn, // 宿主本来就没放进弹窗的，不该被加进来
                PluginSource,
            ];

            var merged = SourceListProvider.Merge(original, available);
            Check("只装了 1 个源插件 ⇒ 6 项", merged.Count == 6, string.Join(", ", merged.Select(t => $"{(int)t}")));
            Check("第 6 项就是插件源", merged.Last() == PluginSource, merged.Last().ToString());
            Check("内置 5 项顺序不变", merged.Take(5).SequenceEqual(original), string.Join(", ", merged.Take(5)));
            Check("Mixed/Steam/PotatoVn 没被塞进来",
                !merged.Contains(RssType.Mixed) && !merged.Contains(RssType.Steam) && !merged.Contains(RssType.PotatoVn),
                string.Join(", ", merged));

            var merged2 = SourceListProvider.Merge(original, [.. available, (RssType)628112]);
            Check("再多装一个源插件 ⇒ 7 项", merged2.Count == 7, string.Join(", ", merged2.Select(t => (int)t)));
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 3. 结构校验：像宿主的能过，缺成员的会被拒 ===");
        {
            var shape = DialogMembers.Discover(typeof(MockDialog), out var error);
            Check("假弹窗（同形状）通过校验", shape is not null, shape is null ? error : "通过");

            var bad = DialogMembers.Discover(typeof(BrokenDialog), out var badError);
            Check("缺 _originalIds 的类型被拒", bad is null && badError.Length > 0, badError);
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 2.5 显示名：插件源换成宿主名，内置项与别的枚举一律不动 ===");
        {
            SourceListProvider.NameSource = t => (int)t == 769164 ? "DLsite" : null;
            SourceListProvider.ResetCache();

            var plugin = ((RssType)769164).ToString();
            SourceNamePatch.Postfix((RssType)769164, ref plugin);
            Check("插件源的 ToString() 换成显示名", plugin == "DLsite", plugin);

            var builtin = RssType.Bangumi.ToString();
            SourceNamePatch.Postfix(RssType.Bangumi, ref builtin);
            Check("内置源的枚举名保持原样", builtin == "Bangumi", builtin);

            var other = DayOfWeek.Monday.ToString();
            SourceNamePatch.Postfix(DayOfWeek.Monday, ref other);
            Check("别的枚举完全不受影响", other == "Monday", other);

            Check("弹窗日志里的名字也走同一套", SourceListProvider.DisplayNameOf(PluginSource) == "DLsite",
                SourceListProvider.DisplayNameOf(PluginSource));
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 4. 候选筛选：只认「自己声明了 RssTypes 且构造函数带 Galgame」的弹窗 ===");
        {
            var candidates = ConfirmDialogPatch.FindSourceDialogs(typeof(MockDialog).Assembly);
            Check("MockDialog 是候选", candidates.Contains(typeof(MockDialog)),
                string.Join(", ", candidates.Select(x => x.Name)));
            Check("WeirdDialog 不是候选（构造函数不带 Galgame）", !candidates.Contains(typeof(WeirdDialog)),
                string.Join(", ", candidates.Select(x => x.Name)));
            Check("BrokenDialog 不是候选（构造函数不带 Galgame）", !candidates.Contains(typeof(BrokenDialog)),
                string.Join(", ", candidates.Select(x => x.Name)));
            Check("结构不符的类型不会被替换那 4 个方法（Discover 返回 null，只做通用补丁）",
                DialogMembers.Discover(typeof(BrokenDialog), out _) is null, "已确认");
        }

        Console.WriteLine();
        Console.WriteLine("=== 5. 打补丁 + 真跑一遍（IL 改写有问题就会像真宿主一样越界）===");
        SourceListProvider.QueryHost = () =>
            [RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, Hikarinagi, PluginSource];
        SourceListProvider.ResetCache();

        var installed = ConfirmDialogPatch.Install(host, typeof(MockDialog));
        Check("补丁安装成功", installed, installed ? "已安装" : "失败（原因见上方日志）");
        if (!installed) return 1;

        var service = new FakeService();
        var game = new Galgame("测试游戏");
        MockDialog dialog;
        try
        {
            dialog = new MockDialog(game, game, service);   // fetchedMeta 传进去 ⇒ 弹窗用的就是同一个 Galgame
            Check("构造函数（内部有 Ids[插件类型] 访问）不越界", true, "构造完成");
        }
        catch (Exception e)
        {
            Check("构造函数（内部有 Ids[插件类型] 访问）不越界", false, $"{e.GetType().Name}: {e.Message}");
            return 1;
        }

        Check("弹窗源列表 = 5 内置 + 1 插件 = 6", dialog.RssTypes.Count == 6,
            string.Join(", ", dialog.RssTypes.Select(t => $"{(int)t}")));
        Check("宿主原有构造逻辑照常跑完（按钮文案设上了）",
            dialog.PrimaryButtonText == "Yes" && dialog.SecondaryButtonText == "Cancel",
            $"{dialog.PrimaryButtonText}/{dialog.SecondaryButtonText}");

        // 选中插件源：真宿主里这一步就是数组越界崩溃点
        dialog.SelectedRssType = PluginSource;
        Check("选中插件源不崩、id 为空", dialog.Id is null, dialog.Id ?? "(null)");
        Check("Ids 数组仍是 PhraserNumber 格", game.Ids.Length == Galgame.PhraserNumber, game.Ids.Length.ToString());

        // 宿主刮完后会把插件 id 写成 IdForPlugins（Galgame.Id 的规则），这里手工模拟
        game.IdForPlugins[769164] = "RJ00000001";
        dialog.SelectedRssType = RssType.Bangumi;
        dialog.SelectedRssType = PluginSource;
        Check("切回插件源能读出 id（来自 IdForPlugins）", dialog.Id == "RJ00000001", dialog.Id ?? "(null)");

        dialog.Id = "RJ99999999";
        Check("在弹窗里改 id 落到 IdForPlugins", game.IdForPlugins.GetValueOrDefault(769164) == "RJ99999999",
            game.IdForPlugins.GetValueOrDefault(769164) ?? "(null)");
        Check("插件 id 没有落进 Ids 数组（内置槽位没被污染）",
            !game.Ids.Contains("RJ99999999"),
            $"Ids=[{string.Join("|", game.Ids.Select(x => x ?? "(null)"))}]（第 3 格是宿主 UpdateMixedId 写的 Mixed 组合 id，正常）");
        // 注：harness 里没有宿主的资源字符串，GetLocalized(key, args) 的占位符不会展开，
        // 所以这里只能验"我们的 Update 确实重算过提示"；真宿主上提示里会列出插件源 id。
        Check("Update 被我们的实现刷新过（宿主逻辑被替换生效）",
            dialog.Hint.Contains("ConfirmGalInfoDialog_Hint"), dialog.Hint.Replace("\n", " / "));

        await Invoker.CallAsync(dialog, "FetchInfo");
        Check("FetchInfo 把插件源当目标源交给宿主服务", service.LastTarget == PluginSource,
            $"target={(int)service.LastTarget}（调用 {service.Calls} 次）");
        Check("FetchInfo 结束后按钮恢复可用", dialog.IsPrimaryButtonEnabled && dialog.IsSecondaryButtonEnabled,
            $"{dialog.IsPrimaryButtonEnabled}/{dialog.IsSecondaryButtonEnabled}");
        Check("FetchInfo 没把 id 清掉（id 变了 ⇒ 不该走 clearIds 分支）",
            game.IdForPlugins.GetValueOrDefault(769164) == "RJ99999999",
            game.IdForPlugins.GetValueOrDefault(769164) ?? "(null)");

        // 停用语义：不做撤销（半撤更危险），补丁活到进程结束，重启即恢复
        ConfirmDialogPatch.Uninstall();
        var another = new MockDialog(new Galgame("停用后"), new Galgame("停用后"), service);
        Check("停用后弹窗仍然可用（设计如此：重启才完全恢复）", another.RssTypes.Count == 6,
            string.Join(", ", another.RssTypes.Select(t => $"{(int)t}")));
        var afterUninstall = ConfirmDialogPatch.Install(host, typeof(MockDialog));
        Check("重复安装不会重复打补丁", afterUninstall, afterUninstall ? "已安装（幂等）" : "失败");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "===== 全部通过 =====" : $"===== 有 {fail} 项不符 =====");
        return fail == 0 ? 0 : 1;
    }
}

// =====================================================================
// 同形状的假弹窗：成员名/签名 + 那些直接下标，全部照抄宿主 1.10.2
// =====================================================================

/// <summary>
/// 假弹窗。CommunityToolkit 的源生成器不在本工程里，所以「值变了才回调」的语义手写模拟
/// （否则 Update ↔ OnIdChanged 会互相递归）。
/// </summary>
internal class MockDialog
{
    public List<RssType> RssTypes { get; } =
        [RssType.Bangumi, RssType.Vndb, RssType.Ymgal, RssType.Cngal, (RssType)8];

    private Galgame _galgame = null!;
    private string? _id = string.Empty;
    private RssType _selectedRssType = RssType.Bangumi;
    private string _hint = string.Empty;
    private Visibility _isPhrasing = Visibility.Collapsed;

    private readonly FakeService _service;

    private string _originalName = string.Empty;
    private Dictionary<int, string?> _originalIds = new();
    private RssType _originalSelectedRssType = RssType.None;

    // 弹窗（ContentDialog）那一侧的成员
    public string? Title { get; set; }
    public string? PrimaryButtonText { get; set; }
    public string? SecondaryButtonText { get; set; }
    public bool IsPrimaryButtonEnabled { get; set; } = true;
    public bool IsSecondaryButtonEnabled { get; set; } = true;

    public MockDialog(Galgame targetGame, Galgame? fetchedMeta, FakeService service)
    {
        InitializeComponent();
        _galgame = fetchedMeta ?? new Galgame(targetGame.Name.Value ?? string.Empty);
        _service = service;
        _originalName = _galgame.Name.Value ?? string.Empty;
        foreach (var rssType in RssTypes) _originalIds[(int)rssType] = Galgame.Ids[(int)rssType];
        _originalSelectedRssType = SelectedRssType;
        Update();
        PrimaryButtonText = "Yes";
        SecondaryButtonText = "Cancel";
    }

    public Galgame Galgame
    {
        get => _galgame;
        set => _galgame = value;
    }

    public string? Id
    {
        get => _id;
        set
        {
            if (_id == value) return;
            _id = value;
            OnIdChanged(value);
        }
    }

    public RssType SelectedRssType
    {
        get => _selectedRssType;
        set
        {
            if (_selectedRssType == value) return;
            _selectedRssType = value;
            OnSelectedRssTypeChanged(value);
        }
    }

    public string Hint
    {
        get => _hint;
        set => _hint = value;
    }

    public Visibility IsPhrasing
    {
        get => _isPhrasing;
        set => _isPhrasing = value;
    }

    public void InitializeComponent()
    {
        // 真宿主这里会加载 XAML；假弹窗不需要
    }

    private void Update()
    {
        Id = Galgame.Ids[(int)SelectedRssType];
        Hint = "hint:" + string.Join(',', RssTypes.Where(t => !string.IsNullOrWhiteSpace(Galgame.Ids[(int)t])));
    }

    private async Task FetchInfo()
    {
        IsPhrasing = Visibility.Visible;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        try
        {
            var nameChanged = _originalName != (Galgame.Name.Value ?? string.Empty);
            var idChanged = false;
            var rssTypeChanged = _originalSelectedRssType != SelectedRssType;
            var targetRssType = SelectedRssType;

            foreach (var rssType in RssTypes)
            {
                var originalId = _originalIds.ContainsKey((int)rssType) ? _originalIds[(int)rssType] : null;
                if (originalId != Galgame.Ids[(int)rssType])
                {
                    idChanged = true;
                    break;
                }
            }

            var shouldClearIds = !idChanged && (nameChanged || rssTypeChanged);

            if (idChanged)
            {
                foreach (var rssType in RssTypes)
                {
                    if (!string.IsNullOrWhiteSpace(Galgame.Ids[(int)rssType]))
                    {
                        targetRssType = rssType;
                        break;
                    }
                }
            }

            if (RssTypes.All(rssType => string.IsNullOrWhiteSpace(Galgame.Ids[(int)rssType])))
                targetRssType = RssType.None;

            if (shouldClearIds)
                foreach (var rssType in RssTypes)
                    Galgame.Ids[(int)rssType] = null;

            await _service.ParseGalInfoOnlyAsync(Galgame, targetRssType);

            _originalName = Galgame.Name.Value ?? string.Empty;
            foreach (var rssType in RssTypes) _originalIds[(int)rssType] = Galgame.Ids[(int)rssType];
            _originalSelectedRssType = SelectedRssType;
        }
        finally
        {
            IsPhrasing = Visibility.Collapsed;
            IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = true;
        }

        Update();
    }

    private void OnIdChanged(string? value)
    {
        Galgame.Ids[(int)SelectedRssType] = string.IsNullOrWhiteSpace(value) ? null : value;
        Galgame.UpdateMixedId();
        Update();
    }

    private void OnSelectedRssTypeChanged(RssType value) => Id = Galgame.Ids[(int)value];
}

/// <summary>
/// 多一处**改写不了**的访问（<c>get_Ids</c> 前面取的是字段而不是 Galgame 属性）：
/// 补丁必须整体拒绝，而不是只改一半。
/// </summary>
internal class WeirdDialog : MockDialog
{
    private readonly Galgame _rawGame = new("x");

    public WeirdDialog() : base(new Galgame("x"), null, new FakeService()) { }

    // ReSharper disable once UnusedMember.Local
    private string? WeirdAccess() => _rawGame.Ids[0];
}

/// <summary>缺 <c>_originalIds</c> 的类型：补丁必须拒绝它。</summary>
internal class BrokenDialog
{
    public List<RssType> RssTypes { get; } = [RssType.Bangumi];
    public Galgame Galgame { get; set; } = new("x");
    public RssType SelectedRssType { get; set; }
    public string? Id { get; set; }
    public string Hint { get; set; } = string.Empty;
    public Visibility IsPhrasing { get; set; }
    private readonly FakeService _service = new();

    public void InitializeComponent() { }
    private void Update() { }
    private Task FetchInfo() => Task.CompletedTask;
    private void OnIdChanged(string? value) { }
    private void OnSelectedRssTypeChanged(RssType value) { }
}

/// <summary>假的信息源服务：只提供补丁/宿主要用的那一个方法，并记下被调用的目标源。</summary>
internal sealed class FakeService
{
    public RssType LastTarget { get; private set; } = RssType.None;
    public int Calls { get; private set; }

    public Task<Galgame> ParseGalInfoOnlyAsync(Galgame galgame, RssType rssType = RssType.None, bool requireConfirm = false)
    {
        LastTarget = rssType;
        Calls++;
        return Task.FromResult(galgame);
    }
}

/// <summary>按名字调假弹窗的私有方法（宿主里 FetchInfo 也是私有的，由 XAML 命令触发）。</summary>
internal static class Invoker
{
    internal static async Task CallAsync(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        if (method.Invoke(instance, null) is Task task) await task;
    }
}

/// <summary>最小宿主桩：只接住 Log（插件靠它说明"打没打上、为什么没打上"）。</summary>
public class HostApiProxy : DispatchProxy
{
    public static IPotatoVnApi Create() => DispatchProxy.Create<IPotatoVnApi, HostApiProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IPotatoVnApi.Log))
        {
            Console.WriteLine($"    [插件日志] {args?.ElementAtOrDefault(1)}");
            return null;
        }

        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType is null || returnType == typeof(void)) return null;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [null]);
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
