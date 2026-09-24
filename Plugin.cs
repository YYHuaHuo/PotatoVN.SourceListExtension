using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Models;

namespace PotatoVN.App.SourceListExtension;

/// <summary>
/// 插件主类。
///
/// <para>
/// 这个插件**不提供搜刮器**，只干一件事：让宿主的「确认游戏信息」弹窗
/// （拖进新游戏后第一次搜刮完弹出的那个）能列出**所有已安装的信息源**，
/// 而不只是内置的 5 个。装了几个源插件就多出几个 —— 例如只装了 DLsite 搜刮器，
/// 那个下拉框里就是 6 个。
/// </para>
/// <para>
/// 实现方式是 Harmony 给那个弹窗打补丁（见 <see cref="ConfirmDialogPatch"/>）。
/// 打不上（宿主改版、结构对不上）就什么都不做，弹窗保持原样，日志里说明原因；
/// 补丁只存在于本次进程，卸载/重启即恢复。
/// </para>
/// </summary>
public class Plugin : IPlugin
{
    /// <summary>
    /// 插件 GUID。必须保持不变，并且与 csproj 的 AssemblyName（<c>A{GUID}</c>）一致。
    /// </summary>
    private static readonly Guid PluginId = new("ffc39013-6c81-4a36-a3fe-7bca0811f571");

    /// <summary>宿主 API。在 <see cref="InitializeAsync"/> 中被赋值。</summary>
    public static IPotatoVnApi HostApi { get; private set; } = null!;

    public PluginInfo Info { get; } = new()
    {
        Id = PluginId,
        Name = "新增源插件",
        Description = "让「添加新游戏后确认信息」的弹窗里列出**所有已安装的信息源**，不只是内置的那几个。\n"
                      + "装了几个信息源插件（如 DLsite 搜刮器）就多出几个选项 —— 只装一个就是 6 个。\n"
                      + "方式是在运行时给宿主的那个弹窗打补丁；对不上宿主版本时不会做任何改动。",
    };

    public Task InitializeAsync(IPotatoVnApi hostApi)
    {
        HostApi = hostApi;

        // 补丁要在弹窗第一次被创建之前装好 ⇒ 插件加载时立刻装。
        // 失败不抛：一个可选的美化插件不该拖垮宿主启动。
        try
        {
            ConfirmDialogPatch.Install(hostApi);
        }
        catch (Exception e)
        {
            try
            {
                hostApi.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    $"[新增源插件] 初始化失败，未做任何改动：{e.GetType().Name}: {e.Message}");
            }
            catch
            {
                // 日志都不可用就算了
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 卸载/停用时撤销补丁。补丁本来也只活在本进程里，这里只是让"停用"立刻生效。
    /// </summary>
    public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
    {
        if (cts.IsCancellationRequested) return Task.FromCanceled(cts);

        ConfirmDialogPatch.Uninstall();
        return Task.CompletedTask;
    }
}
