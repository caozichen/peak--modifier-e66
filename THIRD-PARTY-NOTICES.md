# 第三方依赖与许可说明

此仓库为 PEAK Workshop 自有代码与开发材料的源码仓库，未为项目代码额外选择开源许可证。第三方代码与组件仍各自遵循原作者许可证；私有仓库不改变相关义务。

当前仓库不提交编译后的第三方库、游戏程序集、原修改器二进制或应用程序。依赖准备/发布脚本可在本机下载并随便携版分发运行所需的第三方组件，分发时应保留适用许可证和 notices。

- **Microsoft .NET / Windows Desktop**：[源码与许可](https://github.com/dotnet/runtime)、[Windows Desktop](https://github.com/dotnet/wpf)。`licenses/` 保留当前 .NET Runtime / Windows Desktop 10.0.14 的许可证和运行时第三方说明；发布脚本一并复制。升级运行时后应同步更新对应文本。
- **BepInEx 5.4.23.6**：该固定版本为 MIT（按 v5.4.23.6 标签的 LICENSE 核对）；本仓库保留 [许可证文本](licenses/BepInEx-LICENSE.txt)。[对应发布](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6)、[对应源码](https://github.com/BepInEx/BepInEx/tree/v5.4.23.6)。随包依赖不修改上游二进制。
- **HarmonyX / Harmony interop**：[BepInEx/HarmonyX](https://github.com/BepInEx/HarmonyX)、[pardeike/Harmony](https://github.com/pardeike/Harmony)。以各仓库与所用发行版 LICENSE 为准。
- **Mono.Cecil**：[jbevain/cecil](https://github.com/jbevain/cecil)，MIT。
- **MonoMod RuntimeDetour / Utils**：[MonoMod/MonoMod](https://github.com/MonoMod/MonoMod)，以发行版许可为准。
- **UnityDoorstop**：[NeighTools/UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)，以所用发行版许可为准。
- **Newtonsoft.Json**：插件引用游戏自带版本，不重新分发或替换游戏中的该库。[上游源码与 MIT 许可](https://github.com/JamesNK/Newtonsoft.Json)。

游戏和 Unity 的程序、数据与程序集属于相应权利人，须从自己的游戏安装获取。本项目不是游戏官方产品。
