using System.Reflection;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;
using HarmonyLib;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 给宿主的「确认游戏信息」弹窗（<c>GalgameManager.Views.Dialog.ConfirmGalInfoDialog</c>）打补丁，
/// 让它的源列表变成「内置 5 项 + 所有已安装的源插件」。
///
/// <para><b>背景</b>：宿主 1.10.2 里那个列表是写死的
/// <c>new() { Bangumi, Vndb, Ymgal, Cngal, Hikarinagi }</c>，插件没有任何接口能注册进去；
/// 而弹窗里有 10 处 <c>Galgame.Ids[(int)类型]</c> 的**直接下标**（<c>Ids</c> 只有 <c>PhraserNumber</c> 格），
/// 列表里一出现插件源（id &gt;= 100，例如 DLsite 的 769164）就会数组越界。</para>
///
/// <para><b>只用前置/后置补丁</b>：往宿主 IL 里注入对本插件类型的调用是**不行**的 ——
/// 插件程序集在可回收的 <c>PluginLoadContext</c> 里、宿主在默认上下文，CLR 会拒绝
/// （实测 <c>FileLoadException 0x80131515</c>：可回收 ALC 的程序集不能进默认上下文）。
/// Harmony 的前置/后置补丁走委托调用，实测跨这条边界没问题。所以：
/// 弹窗那 4 个方法用「前置补丁 + 等价实现」替换（逻辑照抄宿主，只把 id 读写换成 <see cref="IdRouter"/>）；
/// 构造函数**保持原样**（跳过它会连带跳过字段初始化器与 ContentDialog 的基类构造），只在前后加标志。</para>
///
/// <para><b>失败即不动作</b>：结构校验不过就什么都不打；补丁按「先无害、后激活」顺序打，
/// 最后一步才换源列表 —— 中途任何一步出错，弹窗都保持原样 5 项、不会崩。
/// 补丁只存在于本次进程，重启 PotatoVN 即完全恢复。</para>
/// </summary>
internal static class ConfirmDialogPatch
{
    internal const string HarmonyId = "dsh.potatovn.source-list-extension";

    /// <summary>宿主里那个弹窗的类型全名。</summary>
    internal const string DialogTypeName = "GalgameManager.Views.Dialog.ConfirmGalInfoDialog";

    /// <summary>本插件验证过的宿主版本（仅用于日志提示）。</summary>
    internal const string VerifiedHostVersion = "1.10.2.0";

    internal static DialogMembers? Members { get; private set; }

    /// <summary>按弹窗实例的类型取成员表（补丁体要按"这个实例自己的类型"反射）。</summary>
    internal static DialogMembers? MembersFor(object instance)
    {
        var type = instance.GetType();
        if (MembersCache.TryGetValue(type, out var cached)) return cached;

        var found = DialogMembers.Discover(type, out _);
        MembersCache[type] = found;
        return found;
    }
    internal static bool Installed { get; private set; }

    private static Harmony? _harmony;
    private static readonly HashSet<Type> PatchedDialogTypes = [];
    private static readonly Dictionary<Type, DialogMembers?> MembersCache = [];
    private static IPotatoVnApi? _host;

    internal static bool Install(IPotatoVnApi host, Type? dialogType = null)
    {
        _host = host;

        dialogType ??= HostReflection.FindType(DialogTypeName);
        if (dialogType is null)
        {
            Log(host, $"宿主里找不到 {DialogTypeName}，不做任何改动。");
            return false;
        }

        // ⚠️ 不假设"信息源下拉"只在 ConfirmGalInfoDialog 里：宿主里凡是带 List<RssType> 属性的弹窗
        // 都算候选（不同版本可能把选源的界面挪到别的弹窗），每个都打补丁。
        var candidates = FindSourceDialogs(dialogType.Assembly)
            .Where(type => !PatchedDialogTypes.Contains(type)).ToList();
        if (candidates.Count == 0)
        {
            Log(host, Installed ? "这些弹窗都已经打过补丁了。" : "宿主里没找到任何带 List<RssType> 的弹窗，不做任何改动。");
            return Installed;
        }

        var harmony = new Harmony(HarmonyId);
        var patched = new List<string>();

        try
        {
            // ① 先给 Galgame.get_Ids 加后置补丁（只在"构造弹窗"期间返回加长数组）。
            //    单独打上它无害：没有别处配合时构造深度永远是 0，行为与原来完全一致。
            harmony.Patch(typeof(Galgame).GetProperty(nameof(Galgame.Ids))!.GetGetMethod()!,
                postfix: new HarmonyMethod(typeof(PatchBodies), nameof(PatchBodies.IdsPostfix)));

            // ①.5 让那个下拉显示名字而不是数字：宿主 XAML 没有 ItemTemplate，显示的就是枚举的 ToString()，
            //      给 Enum.ToString() 加后置补丁，只把插件源（id >= 100）换成宿主的显示名。
            //      同样无害：没有插件源时它什么都不会改。
            SourceNamePatch.Install(harmony);

            // ② 每个候选弹窗：源列表 getter 后置补丁 + 构造函数前后补丁（标志 + 换下拉）
            foreach (var candidate in candidates)
            {
                // DeclaredOnly：继承来的属性不算自己实现，Harmony 会拒绝补继承的方法
                var rssTypes = candidate.GetProperty("RssTypes",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (rssTypes?.GetMethod is null) continue;

                harmony.Patch(rssTypes.GetMethod,
                    postfix: new HarmonyMethod(typeof(PatchBodies), nameof(PatchBodies.RssTypesPostfix)));

                var ctors = candidate.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var ctor in ctors)
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(Galgame)) continue; // 只认"构造时带游戏"的那种

                    harmony.Patch(ctor,
                        prefix: new HarmonyMethod(typeof(PatchBodies), nameof(PatchBodies.DialogCtorPrefix)),
                        postfix: new HarmonyMethod(typeof(PatchBodies), nameof(PatchBodies.DialogCtorPostfix)),
                        finalizer: new HarmonyMethod(typeof(PatchBodies), nameof(PatchBodies.DialogCtorFinalizer)));
                }

                patched.Add(candidate.FullName ?? candidate.Name);

                // 每个候选弹窗按自己的结构判定 —— 结构对得上才替换那 4 个方法
                // （不替换的话，选中插件源时宿主那句 Ids[(int)类型] 会越界）
                var members = DialogMembers.Discover(candidate, out var shapeError);
                MembersCache[candidate] = members;
                if (members is null)
                {
                    Log(host, $"（{candidate.Name} 结构与预期不符：{shapeError} —— 只做了通用补丁）");
                    continue;
                }

                Members ??= members;
                Patch(harmony, members.Update, nameof(PatchBodies.UpdatePrefix));
                Patch(harmony, members.FetchInfo, nameof(PatchBodies.FetchInfoPrefix));
                Patch(harmony, members.OnIdChanged, nameof(PatchBodies.OnIdChangedPrefix));
                Patch(harmony, members.OnSelectedRssTypeChanged, nameof(PatchBodies.OnSelectedRssTypeChangedPrefix));
            }

            if (patched.Count == 0)
            {
                Log(host, "候选弹窗上都没找到可补的成员，不做任何改动。");
                return false;
            }

            _harmony = harmony;
            Installed = true;
            foreach (var candidate in candidates) PatchedDialogTypes.Add(candidate);
            Log(host, $"已接管 {patched.Count} 个弹窗（宿主版本 {dialogType.Assembly.GetName().Version}，验证于 {VerifiedHostVersion}）：" +
                      string.Join("、", patched));
            LogSources(host);
            return true;
        }
        catch (Exception e)
        {
            Log(host, $"打补丁失败，弹窗保持原样：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>找出宿主里所有「列出信息源」的弹窗：带 <c>List&lt;RssType&gt;</c> 属性的类型。</summary>
    internal static List<Type> FindSourceDialogs(Assembly hostAssembly)
    {
        var result = new List<Type>();

        foreach (var type in hostAssembly.GetTypes())
        {
            try
            {
                if (!type.IsClass || type.IsAbstract) continue;
                if (!typeof(ContentDialog).IsAssignableFrom(type) && !type.Name.Contains("Dialog")) continue;
                if (type.GetProperty("RssTypes", BindingFlags.Instance | BindingFlags.Public |
                                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        ?.PropertyType != typeof(List<RssType>)) continue;

                // 必须有一个"带 Galgame"的构造函数 —— 否则它不可能是"拖进游戏时弹出来"的那个
                var isGameDialog = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(c => c.GetParameters() is { Length: > 0 } ps && ps[0].ParameterType == typeof(Galgame));
                if (!isGameDialog) continue;
                result.Add(type);
            }
            catch
            {
                // 个别类型读不了就跳过
            }
        }

        return result;
    }
    private static void Patch(Harmony harmony, MethodInfo method, string prefixName) =>
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(PatchBodies), prefixName));

    /// <summary>
    /// 不做撤销。实测 Harmony 的 <c>UnpatchAll</c> 对这类补丁撤不干净，而半撤状态
    /// （列表撤了、方法替换没撤，或反过来）比不撤更危险。补丁只活在本次进程，**重启即恢复**。
    /// </summary>
    internal static void Uninstall()
    {
        WriteLog("已停用：补丁只作用于本次运行，重启 PotatoVN 后弹窗即恢复原样。");
    }

    internal static void WriteLog(string message)
    {
        try
        {
            _host?.Log(InfoBarSeverity.Informational, "[新增源插件] " + message);
        }
        catch
        {
            // 日志不可用就算了
        }
    }

    private static void Log(IPotatoVnApi host, string message) =>
        host.Log(InfoBarSeverity.Informational, "[新增源插件] " + message);

    private static void LogSources(IPotatoVnApi host)
    {
        try
        {
            var plugins = SourceListProvider.Merge([], SourceListProvider.QueryHost()).ToList();
            Log(host, plugins.Count == 0
                ? "当前没有检测到任何源插件（只有宿主内置的源）。"
                : "由插件提供的源：" + string.Join("、", plugins.Select(SourceListProvider.DisplayName)));
        }
        catch (Exception e)
        {
            Log(host, $"查源插件列表失败：{e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>
/// 弹窗成员的反射句柄 + 结构校验（少一个就整体放弃，宁可不动也不能打歪）。
/// <para>
/// 弹窗是宿主类型、继承 WinUI 的 <c>ContentDialog</c>，插件没有（也不该有）对宿主程序集的编译期引用，
/// 所以一律按名字/签名反射。弹窗侧的 UI 成员（Title / 按钮文案 / 按钮可用性）由
/// <see cref="DialogUi"/> 按名字设值。
/// </para>
/// </summary>
internal sealed class DialogMembers
{
    internal ConstructorInfo Constructor { get; private init; } = null!;
    internal MethodInfo Update { get; private init; } = null!;
    internal MethodInfo FetchInfo { get; private init; } = null!;
    internal MethodInfo OnIdChanged { get; private init; } = null!;
    internal MethodInfo OnSelectedRssTypeChanged { get; private init; } = null!;
    internal PropertyInfo RssTypes { get; private init; } = null!;
    internal PropertyInfo Galgame { get; private init; } = null!;
    internal PropertyInfo SelectedRssType { get; private init; } = null!;
    internal PropertyInfo Id { get; private init; } = null!;
    internal PropertyInfo Hint { get; private init; } = null!;
    internal PropertyInfo IsPhrasing { get; private init; } = null!;
    internal FieldInfo Service { get; private init; } = null!;
    internal FieldInfo OriginalName { get; private init; } = null!;
    internal FieldInfo OriginalIds { get; private init; } = null!;
    internal FieldInfo OriginalSelectedRssType { get; private init; } = null!;
    internal MethodInfo? GetLocalized { get; private init; }
    internal MethodInfo? GetLocalizedWithArgs { get; private init; }

    internal static DialogMembers? Discover(Type type, out string error)
    {
        error = "";
        const BindingFlags Pub = BindingFlags.Instance | BindingFlags.Public;
        const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        try
        {
            // 第三个参数是宿主的 IGalgameCollectionService（宿主程序集里的类型，基库不暴露），
            // 所以只按「3 个参数且前两个是 Galgame」来认。
            var constructor = type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters() is { Length: 3 } ps &&
                                     ps[0].ParameterType == typeof(Galgame) &&
                                     ps[1].ParameterType == typeof(Galgame));
            if (constructor is null) { error = "找不到 (Galgame, Galgame, 服务) 构造函数"; return null; }

            var rssTypes = type.GetProperty("RssTypes", Pub);
            if (rssTypes?.PropertyType != typeof(List<RssType>)) { error = "RssTypes 不是 List<RssType>"; return null; }
            if (rssTypes.GetMethod is null) { error = "RssTypes 没有 getter"; return null; }

            var galgame = type.GetProperty("Galgame", Pub);
            if (galgame?.PropertyType != typeof(Galgame)) { error = "Galgame 属性不是 Galgame"; return null; }

            var selected = type.GetProperty("SelectedRssType", Pub);
            if (selected?.PropertyType != typeof(RssType)) { error = "SelectedRssType 不是 RssType"; return null; }

            var id = type.GetProperty("Id", Pub);
            if (id?.PropertyType != typeof(string)) { error = "Id 属性不是 string"; return null; }

            var hint = type.GetProperty("Hint", Pub);
            if (hint?.PropertyType != typeof(string)) { error = "Hint 属性不是 string"; return null; }

            var isPhrasing = type.GetProperty("IsPhrasing", Pub);
            if (isPhrasing is null) { error = "找不到 IsPhrasing 属性"; return null; }

            var service = type.GetField("_service", Priv);
            if (service is null) { error = "找不到 _service 字段"; return null; }

            var originalIds = type.GetField("_originalIds", Priv);
            if (originalIds?.FieldType != typeof(Dictionary<int, string>)) { error = "找不到 _originalIds 字段"; return null; }

            var originalSelected = type.GetField("_originalSelectedRssType", Priv);
            if (originalSelected?.FieldType != typeof(RssType)) { error = "找不到 _originalSelectedRssType 字段"; return null; }

            var originalName = type.GetField("_originalName", Priv);
            if (originalName?.FieldType != typeof(string)) { error = "找不到 _originalName 字段"; return null; }

            var update = type.GetMethod("Update", Priv, null, Type.EmptyTypes, null);
            if (update is null || update.ReturnType != typeof(void)) { error = "找不到 Update()"; return null; }

            var fetchInfo = type.GetMethod("FetchInfo", Priv, null, Type.EmptyTypes, null);
            if (fetchInfo is null || fetchInfo.ReturnType != typeof(Task)) { error = "找不到 FetchInfo()（应返回 Task）"; return null; }

            var onIdChanged = type.GetMethod("OnIdChanged", Priv, null, [typeof(string)], null);
            if (onIdChanged is null || onIdChanged.ReturnType != typeof(void)) { error = "找不到 OnIdChanged(string)"; return null; }

            var onSelected = type.GetMethod("OnSelectedRssTypeChanged", Priv, null, [typeof(RssType)], null);
            if (onSelected is null || onSelected.ReturnType != typeof(void)) { error = "找不到 OnSelectedRssTypeChanged(RssType)"; return null; }

            // 宿主自己的本地化扩展；找不到也不要紧 —— 退回原样的 key（宿主那个扩展失败时也是这个行为）
            var resourceExtensions = HostReflection.FindType("GalgameManager.Helpers.ResourceExtensions");
            var getLocalized = resourceExtensions?.GetMethod("GetLocalized", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
            var getLocalizedWithArgs = resourceExtensions?.GetMethod("GetLocalized", BindingFlags.Public | BindingFlags.Static, null,
                [typeof(string), typeof(object[])], null);

            return new DialogMembers
            {
                Constructor = constructor,
                Update = update,
                FetchInfo = fetchInfo,
                OnIdChanged = onIdChanged,
                OnSelectedRssTypeChanged = onSelected,
                RssTypes = rssTypes,
                Galgame = galgame,
                SelectedRssType = selected,
                Id = id,
                Hint = hint,
                IsPhrasing = isPhrasing,
                Service = service,
                OriginalName = originalName,
                OriginalIds = originalIds,
                OriginalSelectedRssType = originalSelected,
                GetLocalized = getLocalized,
                GetLocalizedWithArgs = getLocalizedWithArgs,
            };
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }
}

/// <summary>弹窗（ContentDialog）那一侧的成员访问：按名字反射，找不到就安静跳过（只是文案/按钮状态）。</summary>
internal static class DialogUi
{
    private static readonly Dictionary<(Type Type, string Name), PropertyInfo?> Cache = [];

    internal static void Set(object instance, string name, object? value)
    {
        var property = Property(instance.GetType(), name);
        if (property is null || !property.CanWrite) return;

        try
        {
            property.SetValue(instance, value);
        }
        catch
        {
            // UI 成员设不上就算了
        }
    }

    private static PropertyInfo? Property(Type type, string name)
    {
        if (Cache.TryGetValue((type, name), out var cached)) return cached;

        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Cache[(type, name)] = property;
        return property;
    }
}
