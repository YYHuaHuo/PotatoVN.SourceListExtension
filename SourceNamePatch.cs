using System.Reflection;
using GalgameManager.Enums;
using HarmonyLib;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 让「信息源」下拉显示名字，而不是一串数字。
///
/// <para>
/// 宿主 1.10.2 的 XAML 是空的模板：
/// <c>&lt;ComboBox ItemsSource="{x:Bind RssTypes}" SelectedItem="{x:Bind SelectedRssType, Mode=TwoWay}"/&gt;</c>
/// —— 没有 ItemTemplate，所以每一项显示的就是枚举的 <c>ToString()</c>：
/// 内置 5 项恰好是枚举名（Bangumi / Vndb / …），而插件源是 <c>(RssType)769164</c> 这种值，
/// <c>ToString()</c> 出来就是「769164」。
/// </para>
/// <para>
/// 所以这里给 <see cref="Enum.ToString()"/> 加一个后置补丁，**只改 (int) &gt;= 100 的插件源**，
/// 换成宿主自己的显示名（<c>EnumExtension.GetLocalized</c> 认得插件源，返回
/// DLsite / Kungal / 批评空间(ErogameScape)）；内置项、别的枚举、别的枚举值一律原样返回，
/// 所以打补丁前后的界面观感完全一致。
/// </para>
/// </summary>
internal static class SourceNamePatch
{
    /// <summary>防递归：显示名本身是问宿主要的，宿主内部可能又会 ToString()。</summary>
    [ThreadStatic] private static bool _rewriting;

    internal static void Install(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(Enum), nameof(Enum.ToString), Type.EmptyTypes)
                     ?? throw new MissingMethodException("System.Enum.ToString()");
        var postfix = AccessTools.Method(typeof(SourceNamePatch), nameof(Postfix))
                      ?? throw new MissingMethodException(nameof(Postfix));
        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    /// <summary>后置补丁体。只动插件源，且不改内置项。</summary>
    internal static void Postfix(object __instance, ref string __result)
    {
        if (_rewriting) return;
        if (__instance is not RssType type) return;
        if ((int)type < IdRouter.PluginIdThreshold) return;   // 内置的枚举名保持原样

        _rewriting = true;
        try
        {
            __result = SourceListProvider.DisplayName(type);
        }
        finally
        {
            _rewriting = false;
        }
    }
}
