# 依赖与准备材料

| 依赖 | 固定版本 | 来源 / 位置 |
| --- | --- | --- |
| .NET SDK | 10.0.403 x64 | Microsoft 官方；仓库 global.json |
| Windows Desktop / 自包含运行包 | 由固定 SDK 解析 | SDK 和 NuGet；publish 随包携带 |
| BepInEx | 5.4.23.6 Windows x64 Mono | 官方 release ZIP；`.dependencies/BepInEx/` 是完整解压根目录 |
| HarmonyX、Mono.Cecil、MonoMod、Doorstop | BepInEx 上述发行版所附版本 | 不单独任意替换；内嵌策略记录各文件 SHA256 |
| Unity/Mono 基础程序集 | 随本机 PEAK | `PEAK_Data/Managed/`，只读引用 |
| Newtonsoft.Json | 随本机 PEAK，历史分析为 13.0.2 | 插件使用游戏已有库；不另行覆盖 |

BepInEx ZIP：

```text
https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip
SHA256: 6C57A714A892EFB4E9E1FEF63D608A3391F30EC984031C465404E4EE381A0755
```

准备脚本只解压校验通过的固定版本。插件项目的 `BepInExDir` 指向 `.dependencies/BepInEx/BepInEx/core`；Publish 使用其所属完整加载器根目录。游戏目录由 `-GameDir` 或环境变量 `PEAK_GAME_DIR` 提供，不依赖开发者固定盘符。

## 仓库包含哪些准备材料

- 依赖准备、构建、测试、便携目录生成和实际程序集只读分析脚本。
- 完整生产源文件、共用协议/功能目录、所有随仓库运行的回归工程。
- 当前开发计划、架构、生命周期、人工验证矩阵、名称映射。
- 历史角色与世界适配分析、原始开发计划与资源检查，标记为历史资料。
- 旧部署与 0.2.5→0.2.6 升级事务源码和测试，仅用于兼容已存在的旧包布局。
- BepInEx 许可证、第三方项目与相应源码链接。

没有提交 SDK、NuGet 缓存、游戏程序集、原第三方修改器二进制或其反编译全集、已发布 APP、个人机器运行数据。获取本项目功能所需的开发与构建信息保留在上述资料中。

## 离线构建

在有网络的环境使用相同 SDK 预先 restore/publish 所需项目和 win-x64 运行包，将需要的 nupkg 保存在自己的离线源中，创建相应 NuGet.Config，运行三个脚本时均传 `-NuGetConfig`。参考路径不存入仓库。BepInEx 则通过 `-BepInExArchive` 使用本地原版 ZIP。

自定义核心依赖会使内嵌载荷校验不匹配。当前脚本仅自动更新自己编译的插件哈希，不会把未知加载器文件默认为可信；需要升级第三方依赖时明确审查并更新策略与相应说明。
