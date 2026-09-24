using System.Reflection;
using GalgameManager.Enums;
using GalgameManager.Models;
using HarmonyLib;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 全部补丁体。**只用前置/后置补丁**（Harmony 通过委托调用，实测跨「可回收插件 ALC → 宿主」可行），
/// 绝不往宿主 IL 里注入对本插件类型的调用 —— 那条路会被 CLR 拒绝
/// （<c>FileLoadException 0x80131515</c>：可回收 ALC 的程序集不能进默认上下文）。
///
/// <para>补丁清单（打补丁的顺序也很关键，见 <see cref="ConfirmDialogPatch.Install"/>）：</para>
/// <list type="number">
/// <item>弹窗构造函数：前置记「正在构造弹窗」、后置修正 <c>_originalIds</c> 并刷新提示、终结器清标志。</item>
/// <item><c>Galgame.get_Ids</c>：仅在「正在构造弹窗」期间返回一个**加长的临时数组**，
///   让宿主构造函数里那句 <c>Galgame.Ids[(int)类型]</c>（插件 id 会越界）平安返回 null。
///   它只改返回值、不动字段，所以既不会污染数据也不会把 <c>Ids</c> 撑大（存档不受影响）。</item>
/// <item>弹窗的 <c>Update</c> / <c>FetchInfo</c> / <c>OnIdChanged</c> / <c>OnSelectedRssTypeChanged</c>：
///   逻辑照抄宿主 1.10.2，只把 id 读写换成 <see cref="IdRouter"/>（插件 id 走 <c>IdForPlugins</c>）。</item>
/// <item>最后才给 <c>get_RssTypes</c> 加后置补丁把插件源加进列表 —— 放在最后，
///   这样前面任何一步失败时列表都还是原样 5 项，绝不会出现「列表里有插件源、但代码还没改好」的崩溃组合。</item>
/// </list>
/// </summary>
internal static class PatchBodies
{
    // =====================================================================
    // ④ 源列表（最后才打）
    // =====================================================================

    internal static void RssTypesPostfix(ref List<RssType> __result)
    {
        __result = SourceListProvider.Build(__result ?? []);
    }

    // =====================================================================
    // ① 弹窗构造函数：只在前后加标志，**绝不跳过**原构造函数
    //    （跳过会连带跳过字段初始化器与 ContentDialog 的基类构造，弹窗会被构造坏）
    // =====================================================================

    internal static void DialogCtorPrefix()
    {
        Guard.EnterDialogCtor();
    }

    internal static void DialogCtorPostfix(object __instance)
    {
        try
        {
            var type = __instance.GetType();
            var merged = EffectiveTypesOf(__instance, type);

            // 若这个弹窗有 _originalIds（宿主那个"确认游戏信息"弹窗就有）：把插件源也补进去，
            // 否则 FetchInfo 会把"本来就有 id"误判成"用户刚改了 id"。
            var originalIdsField = type.GetField("_originalIds", BindingFlags.Instance | BindingFlags.NonPublic);
            if (originalIdsField?.GetValue(__instance) is Dictionary<int, string?> originalIds)
            {
                var galgame = type.GetProperty("Galgame")?.GetValue(__instance) as Galgame;
                if (galgame is not null)
                    foreach (var source in merged) originalIds[(int)source] = IdRouter.Get(galgame, source);
            }

            // 若这个弹窗有 Update()：刷新一次提示（构造函数里那次 Update 时列表还没含插件源）
            type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(__instance, null);

            // ★ 关键一步：那个下拉的 ItemsSource 是 {x:Bind RssTypes}，x:Bind 默认 OneTime，
            //   在 InitializeComponent() 里就把当时那个 List 实例抓走绑上了，改 getter 不一定来得及。
            //   所以这里直接把 ItemsSource 换成合并后的列表。
            var replaced = ReplaceSourceControls(__instance, merged);

            // 宿主也可能在构造**之后**才把 5 项塞进那个下拉（1.2 的日志显示"已换成 8 项"，
            // 用户看到的却还是 5 项），所以再挂一轮晚到替换：下拉展开、Opened/Loaded、定时兜底。
            ScheduleLateReplace(__instance);
            CtorLogs++;
            if (CtorLogs <= 8) Log($"构造完成：{type.Name} 合并后 {merged.Count} 项；{replaced}");
            if (CtorLogs == 2) LogTree(__instance);   // 第二次构造时把控件树写出来，便于定位
        }
        catch (Exception e)
        {
            Log($"构造后修正失败（不影响弹窗本身）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>弹窗要用的列表：原始项 + 已安装的源插件（按类型反射取，任何弹窗都适用）。</summary>
    private static List<RssType> EffectiveTypesOf(object instance, Type type)
    {
        var raw = type.GetProperty("RssTypes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance) as List<RssType>;
        return SourceListProvider.Build(raw ?? []);
    }
    /// <summary>终结器：无论构造函数正常结束还是抛异常，都要把标志清掉。</summary>
    internal static Exception? DialogCtorFinalizer(Exception? __exception)
    {
        Guard.ExitDialogCtor();
        return __exception;
    }

    // =====================================================================
    // ② Galgame.get_Ids：只在构造弹窗期间返回加长数组
    // =====================================================================

    internal static void IdsPostfix(ref string?[] __result)
    {
        if (Guard.DialogCtorDepth == 0 || __result is null) return;

        var padded = Guard.PaddedIds();
        if (padded is null)
        {
            // 还没算出要加多长（源列表还没查）：按原始长度返回会造成越界，所以这里给一个够大的兜底
            padded = Guard.PaddedIds(1_000_000);
        }

        if (padded is null) return;
        Array.Copy(__result, padded, Math.Min(__result.Length, padded.Length));
        __result = padded;
    }

    // =====================================================================
    // ③ 四个方法：照抄宿主逻辑，id 读写换成 IdRouter
    // =====================================================================

    internal static bool UpdatePrefix(object __instance)
    {
        var shape = ConfirmDialogPatch.MembersFor(__instance)!;
        var galgame = GalgameOf(__instance);

        shape.Id.SetValue(__instance, IdRouter.Get(galgame, SelectedRssTypeOf(__instance)));

        DialogUi.Set(__instance, "Title", (galgame.Description.Value?.Length ?? 0) > 0
            ? Localized("ConfirmGalInfoDialog_Title_Correct")
            : Localized("ConfirmGalInfoDialog_Title_NotFound"));

        var withId = EffectiveTypes(__instance)
            .Where(type => !string.IsNullOrWhiteSpace(IdRouter.Get(galgame, type)))
            .ToList();

        shape.Hint.SetValue(__instance, Localized("ConfirmGalInfoDialog_Hint") + "\n" +
                                          (withId.Count == 0
                                              ? Localized("ConfirmGalInfoDialog_NoID")
                                              : Localized("ConfirmGalInfoDialog_ID", string.Join(',', withId))));
        return false;
    }

    internal static bool FetchInfoPrefix(object __instance, ref Task __result)
    {
        __result = RunFetchInfoAsync(__instance);
        return false;
    }

    private static async Task RunFetchInfoAsync(object instance)
    {
        var shape = ConfirmDialogPatch.MembersFor(instance)!;
        var galgame = GalgameOf(instance);

        shape.IsPhrasing.SetValue(instance, Visibility.Visible);
        DialogUi.Set(instance, "IsPrimaryButtonEnabled", false);
        DialogUi.Set(instance, "IsSecondaryButtonEnabled", false);
        try
        {
            var selected = SelectedRssTypeOf(instance);
            var nameChanged = !string.Equals((string?)shape.OriginalName.GetValue(instance),
                galgame.Name.Value ?? string.Empty, StringComparison.Ordinal);
            var rssTypeChanged = (RssType)shape.OriginalSelectedRssType.GetValue(instance)! != selected;
            var targetRssType = selected;

            var originalIds = (Dictionary<int, string?>)shape.OriginalIds.GetValue(instance)!;
            var idChanged = false;
            foreach (var type in EffectiveTypes(instance))
            {
                if (originalIds.GetValueOrDefault((int)type) == IdRouter.Get(galgame, type)) continue;
                idChanged = true;
                break;
            }

            var shouldClearIds = !idChanged && (nameChanged || rssTypeChanged);

            if (idChanged)
            {
                foreach (var type in EffectiveTypes(instance))
                {
                    if (string.IsNullOrWhiteSpace(IdRouter.Get(galgame, type))) continue;
                    targetRssType = type;
                    break;
                }
            }

            if (EffectiveTypes(instance).All(type => string.IsNullOrWhiteSpace(IdRouter.Get(galgame, type))))
                targetRssType = RssType.None;

            if (shouldClearIds)
                foreach (var type in EffectiveTypes(instance))
                    IdRouter.Set(galgame, type, null);

            if (shape.Service.GetValue(instance) is { } service)
                await ParseGalInfoOnlyAsync(service, galgame, targetRssType);

            shape.OriginalName.SetValue(instance, galgame.Name.Value ?? string.Empty);
            foreach (var type in EffectiveTypes(instance)) originalIds[(int)type] = IdRouter.Get(galgame, type);
            shape.OriginalSelectedRssType.SetValue(instance, selected);
        }
        finally
        {
            shape.IsPhrasing.SetValue(instance, Visibility.Collapsed);
            DialogUi.Set(instance, "IsPrimaryButtonEnabled", true);
            DialogUi.Set(instance, "IsSecondaryButtonEnabled", true);
        }

        shape.Update.Invoke(instance, null);
    }

    internal static bool OnIdChangedPrefix(object __instance, string? value)
    {
        var shape = ConfirmDialogPatch.MembersFor(__instance)!;
        var galgame = GalgameOf(__instance);

        IdRouter.Set(galgame, SelectedRssTypeOf(__instance), string.IsNullOrWhiteSpace(value) ? null : value);
        galgame.UpdateMixedId();
        shape.Update.Invoke(__instance, null);
        return false;
    }

    internal static bool OnSelectedRssTypeChangedPrefix(object __instance, RssType value)
    {
        ConfirmDialogPatch.MembersFor(__instance)!.Id.SetValue(__instance, IdRouter.Get(GalgameOf(__instance), value));
        return false;
    }

    // =====================================================================
    // 合并后的列表 / 替换弹窗里的源下拉
    // =====================================================================

    private static int CtorLogs;

    /// <summary>弹窗原始列表（宿主字段里那份，5 项）。</summary>
    private static List<RssType> RawTypesOf(object instance) =>
        (List<RssType>)ConfirmDialogPatch.MembersFor(instance)!.RssTypes.GetValue(instance)!;

    /// <summary>
    /// 实际要用的列表：原始项 + 已安装的源插件。
    /// **不依赖 getter 补丁是否生效**（x:Bind 是 OneTime，getter 可能已经被内联），所以这里显式再合并一次。
    /// </summary>
    private static List<RssType> EffectiveTypes(object instance) =>
        SourceListProvider.Build(RawTypesOf(instance));

    /// <summary>
    /// 把弹窗里那个「信息源」下拉的 ItemsSource 换成合并后的列表。
    /// 返回一段说明文字，写进日志（0 个 ComboBox 就说明得换别的找法）。
    /// </summary>
    /// <summary>
    /// 把弹窗里那个「信息源」下拉的 ItemsSource 换成合并后的列表。
    /// 返回一段说明文字写进日志（含替换后控件里实际的项数/项名），便于确认到底改到了没有。
    /// </summary>
    /// <summary>
    /// 把弹窗里所有「装着信息源」的控件（ComboBox / ListView / 任何 ItemsControl）的 ItemsSource
    /// 换成合并后的列表，并把实际结果写进日志。
    /// <para>
    /// 不写死 ComboBox：1.2 的日志显示"已换成 8 项"但用户看到的仍是 5 项，
    /// 说明宿主里那个选源头可能不是 ComboBox（或者不止一个下拉），所以这里认 ItemsControl 全体。
    /// </para>
    /// </summary>
    private static string ReplaceSourceControls(object instance, List<RssType> merged)
    {
        var content = instance.GetType()
            .GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance) as DependencyObject;
        if (content is null) return "（拿不到 Content，无法遍历控件树）";

        var controls = new List<ItemsControl>();
        Collect(content, controls);
        if (controls.Count == 0)
        {
            var logical = FindFirstLogical<ItemsControl>(content);
            if (logical is not null) controls.Add(logical);
        }

        if (controls.Count == 0) return "找不到任何 ItemsControl（视觉树与逻辑树都没有）";

        var selected = SelectedSourceOf(instance);
        var notes = new List<string>();

        foreach (var control in controls)
        {
            // 不能只认「里面已经有信息源」的控件：构造时宿主还没把 5 项塞进去（ItemsSource 为空），
            // 真机上唯一那个 ItemsControl 就是这么被漏掉的。所以：只有一个 ItemsControl 就认它，
            // 或者是下拉（ComboBox）也认它，只有在多个候选里才靠内容挑。
            if (!HoldsSources(control) && control is not ComboBox && controls.Count > 1) continue;

            var before = DescribeItems(control);
            if (SameTypes(control.ItemsSource, merged))
            {
                notes.Add($"{control.GetType().Name}：已经是 {merged.Count} 项，跳过");
                continue;
            }

            try
            {
                var selectedItem = control.GetType()
                    .GetProperty("SelectedItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                control.ItemsSource = new ObservableCollection<RssType>(merged);
                // 只在真的不一致时才回写，避免反复触发宿主的"切换信息源"回调
                if (selected is { } want && merged.Contains(want) && !Equals(selectedItem?.GetValue(control), want))
                    selectedItem?.SetValue(control, want);
                notes.Add($"{control.GetType().Name}：{before} → {DescribeItems(control)}");
            }
            catch (Exception e)
            {
                notes.Add($"{control.GetType().Name}：替换失败（{e.GetType().Name}: {e.Message}）");
            }
        }

        return notes.Count == 0
            ? $"遍历了 {controls.Count} 个 ItemsControl，没找到装着信息源的那个"
            : string.Join("；", notes);
    }

    /// <summary>收集视觉树里所有 T。</summary>
    private static void Collect<T>(DependencyObject root, List<T> found) where T : DependencyObject
    {
        try
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) found.Add(hit);
                Collect(child, found);
            }
        }
        catch
        {
            // 视觉树不可用就靠逻辑树兜底
        }
    }
    private static readonly List<object> Timers = [];   // 防被 GC 回收
    private static int LateLogs;
    private static int ScheduleLogs;
    private static string? LastNote;

    /// <summary>
    /// 安排"迟到替换"：宿主常常在构造函数**之后**才把 5 项塞进那个下拉
    /// （1.3 的日志显示构造时控件还是空的，1.2 的日志显示构造时换成了 8 项、用户看到的还是 5 项）。
    /// 这里挂三处，任一处生效即可：用户点开下拉的那一刻（最稳）、Opened/Loaded、以及 300ms × 8 的定时兜底。
    /// </summary>
    private static void ScheduleLateReplace(object instance)
    {
        try
        {
            var combo = ComboOf(instance);
            if (instance is ContentDialog dialog)
                dialog.Opened += (_, _) => LateReplace(instance, "Opened");
            if (instance is FrameworkElement element)
                element.Loaded += (_, _) => LateReplace(instance, "Loaded");
            if (combo is not null)
                combo.DropDownOpened += (_, _) => LateReplace(instance, "下拉展开");

            if (ScheduleLogs++ < 6)
                Log($"已挂晚到替换（{instance.GetType().Name}）：{(combo is null ? "没找到下拉" : $"下拉 {combo.Name ?? "(无名)"}")} / Opened / Loaded / 定时器");

            var queue = (instance as DependencyObject)?.DispatcherQueue;
            if (queue is null)
            {
                if (ScheduleLogs <= 6) Log("（本线程没有 DispatcherQueue，跳过定时兜底）");
                return;
            }

            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(300);
            timer.IsRepeating = true;
            var ticks = 0;
            timer.Tick += (sender, _) =>
            {
                LateReplace(instance, $"定时器 #{++ticks}");
                if (ticks < 8) return;
                sender.Stop();
                Timers.Remove(sender);
            };

            Timers.Add(timer);
            timer.Start();
        }
        catch (Exception e)
        {
            Log($"安排延迟替换失败（不影响弹窗）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>弹窗内容里的那个下拉（先逻辑树后视觉树，构造时视觉树可能还没长出来）。</summary>
    private static ComboBox? ComboOf(object instance)
    {
        if (instance.GetType()
                .GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance) is not DependencyObject content) return null;

        return FindFirstLogical<ComboBox>(content) ?? FindFirst<ComboBox>(content);
    }

    private static void LateReplace(object instance, string how)
    {
        try
        {
            var merged = EffectiveTypesOf(instance, instance.GetType());
            var note = ReplaceSourceControls(instance, merged);
            if (note == LastNote) return;   // 定时器每 300ms 刷一次，重复的不记
            LastNote = note;
            if (LateLogs++ < 24) Log($"延迟替换（{how}）：{note}");
        }
        catch (Exception e)
        {
            if (LateLogs++ < 24) Log($"延迟替换（{how}）失败：{e.GetType().Name}: {e.Message}");
        }
    }
    /// <summary>这个控件是不是在显示信息源（ItemsSource 或现有项里是 RssType）。</summary>
    private static bool HoldsSources(ItemsControl control)
    {
        try
        {
            if (control.ItemsSource is List<RssType>) return true;
            return control.Items.Count > 0 && control.Items[0] is RssType;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>控件当前的 ItemsSource 是不是已经是这份列表（按值比较）。
    /// 对了就不再重塞：既少一次无谓改动，也避免在下拉正在展开时把 WinUI 弄出 NRE。</summary>
    private static bool SameTypes(object? itemsSource, List<RssType> merged) =>
        itemsSource is IEnumerable<RssType> current && current.SequenceEqual(merged);

    private static string DescribeItems(ItemsControl control)
    {
        try
        {
            var names = control.Items.Cast<object?>().Take(12).Select(SourceListProvider.DisplayNameOf).ToList();
            return names.Count == 0 ? "空" : $"{control.Items.Count} 项[{string.Join("/", names)}]";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>弹窗当前选中的源（不同弹窗的属性名可能不一样，取不到就返回 null）。</summary>
    private static RssType? SelectedSourceOf(object instance)
    {
        var type = instance.GetType();
        foreach (var name in new[] { "SelectedRssType", "SelectedSource", "SelectedType" })
        {
            try
            {
                if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(instance) is RssType value) return value;
            }
            catch
            {
                // 取不到就算了
            }
        }

        return null;
    }

    /// <summary>把控件树（类型 + 项名）打进日志，方便定位"那个 5 项的下拉到底是什么"。</summary>
    private static void DumpTree(DependencyObject root, int depth, List<string> lines)
    {
        if (lines.Count >= 60) return;

        try
        {
            var type = root.GetType().Name;
            var extra = root is ItemsControl items ? $"({DescribeItems(items)})" : string.Empty;
            lines.Add($"{new string(' ', depth * 2)}{type}{extra}");

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) DumpTree(VisualTreeHelper.GetChild(root, i), depth + 1, lines);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把弹窗的控件树写进日志（只在头几次构造时写，避免刷屏）。</summary>
    private static void LogTree(object instance)
    {
        try
        {
            var content = instance.GetType()
                .GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance) as DependencyObject;
            if (content is null) return;

            var lines = new List<string>();
            DumpTree(content, 0, lines);
            Log($"控件树（{instance.GetType().Name}）：\n    " + string.Join("\n    ", lines));
        }
        catch
        {
            // 忽略
        }
    }
    /// <summary>按视觉树找第一个 T。</summary>
    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        try
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;
                if (FindFirst<T>(child) is { } deeper) return deeper;
            }
        }
        catch
        {
            // 视觉树不可用时交给逻辑树
        }

        return null;
    }

    /// <summary>按逻辑树找第一个 T（ContentDialog 的内容在显示前不一定进了视觉树）。</summary>
    private static T? FindFirstLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalChildren(root))
        {
            if (child is T hit) return hit;
            if (FindFirstLogical<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject node)
    {
        // 常见容器：Panel.Children / Border.Child / ContentControl.Content / ItemsControl.Items
        if (node is Panel panel)
            foreach (var child in panel.Children) yield return child;
        if (node is Border { Child: { } borderChild }) yield return borderChild;
        if (node is ContentControl { Content: DependencyObject contentChild }) yield return contentChild;
    }

    // =====================================================================
    // 反射小工具
    // =====================================================================

    private static Galgame GalgameOf(object instance) =>
        (Galgame)ConfirmDialogPatch.MembersFor(instance)!.Galgame.GetValue(instance)!;

    private static RssType SelectedRssTypeOf(object instance) =>
        (RssType)ConfirmDialogPatch.MembersFor(instance)!.SelectedRssType.GetValue(instance)!;

    private static List<RssType> RssTypesOf(object instance) =>
        (List<RssType>)ConfirmDialogPatch.MembersFor(instance)!.RssTypes.GetValue(instance)!;

    private static string Localized(string key) =>
        ConfirmDialogPatch.Members!.GetLocalized?.Invoke(null, [key]) as string ?? key;

    private static string Localized(string key, params object[] args) =>
        ConfirmDialogPatch.Members!.GetLocalizedWithArgs?.Invoke(null, [key, args]) as string ?? key;

    private static void Log(string message) => ConfirmDialogPatch.WriteLog(message);

    private static readonly Dictionary<Type, MethodInfo> ParseMethodCache = [];

    /// <summary>调宿主的 <c>ParseGalInfoOnlyAsync</c>（走反射：离线验证可以换成同形状的假服务）。</summary>
    private static Task ParseGalInfoOnlyAsync(object service, Galgame galgame, RssType rssType)
    {
        var type = service.GetType();
        if (!ParseMethodCache.TryGetValue(type, out var method))
        {
            method = type.GetMethods()
                .FirstOrDefault(m => m.Name == "ParseGalInfoOnlyAsync" && m.GetParameters().Length == 3)
                ?? throw new MissingMethodException($"{type.FullName} 上没有 ParseGalInfoOnlyAsync(Galgame, RssType, bool)");
            ParseMethodCache[type] = method;
        }

        return method.Invoke(service, [galgame, rssType, false]) as Task ?? Task.CompletedTask;
    }
}

/// <summary>
/// 「正在构造弹窗」这段短暂窗口的状态。
/// <para>
/// 弹窗构造函数里有一句 <c>Galgame.Ids[(int)rssType]</c>，列表里一旦有插件源（id &gt;= 100）就会越界；
/// 而构造函数不能跳过（会连带跳过基类构造），所以就在这段时间里让 <c>get_Ids</c> 返回一个足够长的临时数组。
/// 它只改返回值、不动 <c>Galgame.Ids</c> 字段，所以**不会**把存档撑大。
/// </para>
/// </summary>
internal static class Guard
{
    internal static int DialogCtorDepth { get; private set; }

    private static string?[]? _padded;
    private static int _paddedLength;

    internal static void EnterDialogCtor()
    {
        DialogCtorDepth++;
        _paddedLength = NeededLength();
        if (_padded?.Length != _paddedLength) _padded = null; // 源集合变了就重新分配
    }

    internal static void ExitDialogCtor()
    {
        if (DialogCtorDepth > 0) DialogCtorDepth--;
    }

    /// <summary>构造期间用的加长数组（**每次弹窗新建一个**，避免跨次弹窗读到上一次的内容）。</summary>
    internal static string?[]? PaddedIds(int? force = null)
    {
        var length = force ?? _paddedLength;
        if (length <= 0) return null;
        if (force is not null) return new string?[length];
        return _padded ??= new string?[length];
    }

    /// <summary>最长需要多少格：内置格数，或者「最大的插件源 id + 1」。</summary>
    private static int NeededLength()
    {
        var needed = Galgame.PhraserNumber;

        try
        {
            foreach (var type in SourceListProvider.Merge([], SourceListProvider.QueryHost()))
                needed = Math.Max(needed, (int)type + 1);
        }
        catch
        {
            // 查不到就退回内置格数（那说明也没有插件源要显示）
        }

        return needed;
    }
}
