using GalgameManager.Enums;
using GalgameManager.Models;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 安全的「按信息源取/存作品 id」。
///
/// <para>
/// 宿主自己的规则（<c>Galgame.Id</c> 的 getter/setter，1.10.2 实测）：
/// <c>(int)RssType &gt;= 100</c> 的插件 id 存在 <c>Galgame.IdForPlugins</c> 字典里，
/// 内置源存在长度 <c>PhraserNumber</c>（1.10.2 = 9）的 <c>Galgame.Ids</c> 数组里。
/// </para>
/// <para>
/// 而「确认游戏信息」弹窗（<c>ConfirmGalInfoDialog</c>）里是**直接下标**写死的：
/// <c>Galgame.Ids[(int)rssType]</c>。一旦列表里出现插件源（如 DLsite 的 769164），
/// 就会 <see cref="IndexOutOfRangeException"/>。
/// 本类就是那些访问点的替身 —— <see cref="ConfirmDialogPatch"/> 会把弹窗里
/// <c>get_Ids() + ldelem.ref/stelem.ref</c> 的字节码改写成对本类这两个方法的调用。
/// </para>
/// <para>
/// ⚠️ 必须 <c>public</c>：改写后的宿主字节码会在**宿主的加载上下文**里解析这个类型，
/// 非公开类型会因可见性检查而失败。
/// </para>
/// </summary>
public static class IdRouter
{
    /// <summary>宿主的约定：&gt;= 100 走插件字典。</summary>
    public const int PluginIdThreshold = 100;

    public static string? Get(Galgame game, int type)
    {
        if (type >= PluginIdThreshold) return game.IdForPlugins.GetValueOrDefault(type);

        var ids = game.Ids;
        return type >= 0 && type < ids.Length ? ids[type] : null;
    }

    public static void Set(Galgame game, int type, string? value)
    {
        if (type >= PluginIdThreshold)
        {
            game.IdForPlugins[type] = value;
            return;
        }

        if (type < 0) return;
        EnsureCapacity(game, type + 1);
        game.Ids[type] = value;
    }

    /// <summary>与宿主一样按需扩容（宿主用的是 <c>Ids.ResizeArray(PhraserNumber)</c>）。</summary>
    private static void EnsureCapacity(Galgame game, int minLength)
    {
        if (game.Ids.Length >= minLength) return;

        var needed = Math.Max(minLength, Galgame.PhraserNumber);
        var grown = new string?[needed];
        Array.Copy(game.Ids, grown, game.Ids.Length);
        game.Ids = grown;
    }

    internal static string? Get(Galgame game, RssType type) => Get(game, (int)type);

    internal static void Set(Galgame game, RssType type, string? value) => Set(game, (int)type, value);
}
