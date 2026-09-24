# 新增源插件

**「添加新游戏 → 确认游戏信息」弹窗里的信息源选项允许列出所有已安装的信息源**，而不只是原内置的几个。

| 你装了几个信息源插件 | 弹窗里的源 |
| --- | --- |
| 0 个 | 5 个（原样不变） |
| 1 个（如 DLsite 搜刮器） | **6 个** |
| N 个 | 5 + N 个 |

---

## 关键标识

| 项 | 值 | 说明 |
| --- | --- | --- |
| 插件 GUID | `ffc39013-6c81-4a36-a3fe-7bca0811f571` | 必须永久不变；与 `AssemblyName` 绑定 |
| `AssemblyName` | `Affc39013-6c81-4a36-a3fe-7bca0811f571` | 命名规则 `A{GUID}`，避免与其他插件程序集冲突 |
| 插件市场 `types` | `32` | `View`；若按「功能优化」报则填 `96` |
| 版本 | `1.8.0` | 与 `csproj` 的 `<Version>` 一致 |

---

## 目录结构

```
PotatoVN.App.SourceListExtension/
├─ PotatoVN.App.SourceListExtension.csproj   # TFM / AssemblyName / PackPlugin 打包目标
├─ Plugin.cs                                 # 插件主类：IPlugin
├─ DialogPatch.cs                            # 给「确认游戏信息」弹窗打补丁
├─ PatchBodies.cs                            # 各补丁体
├─ SourceListProvider.cs                     # 询问 PotatoVN「有哪些可用源」
├─ SourceNamePatch.cs                        # 让插件源显示成名字而不是数字
├─ IdRouter.cs                               # 插件源 id 的读写路由
├─ harness/                                  # 离线自查（自带 Main，不编进插件）
└─ README.md
```

---

## 使用

装上即生效，没有需要配置的项。

1. 装好本插件，以及你想让它出现在下拉里的信息源插件（例如 DLsite 搜刮器）；
2. 拖入新游戏 → 「确认游戏信息」弹窗里的信息源下拉，就能看到已安装的源插件；
3. 是否接管成功、提供了哪些源、失败原因，都写在插件页日志里；停用或卸载后需重启 PotatoVN 才完全恢复。
