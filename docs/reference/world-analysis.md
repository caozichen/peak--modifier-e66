> 历史资料：记录当时的分析与计划；当前环境、状态保留策略和使用步骤以根目录 README 与 docs/architecture.md 为准。

# 世界与道具功能实现证据（0.2）

实现文件：`work/PeakTrainer/Plugin/GameAdapter.World.cs`。只修改此 partial 与 `world*` 分析材料；未启动游戏。

游戏静态基线：Unity 6000.3.15f1 / Mono x64；Assembly-CSharp SHA256 为 `E526A5234EA2289EA55297FBD6FB7626C75E86E6A0A26A6B87D82ED8F44CD168`。由主 GameAdapter 校验整程序集后才安装本文件补丁。

| 功能 | 已安装的实际入口 | 实现 |
| --- | --- | --- |
| fuel.unlimited / fuel.consume | `Lantern.UpdateFuel(): void` 唯一 `Time.deltaTime` 读取 | IL 后插入纯倍率函数；无限优先返回 0，否则乘消耗倍率。保留原来的空油判定、熄灭、数据写入与 UI 更新。 |
| items.unlimited | `Action_ReduceUses.RunAction(): void`、`Item.ConsumeDelayed(bool): IEnumerator` | 跳过本地发起的减次数操作；保留 OnConsumed 回调及一帧迭代时序，取消本次销毁 RPC。不写回旧次数、不复活已消耗物品。 |
| ping.size | `PointPing.Go(): void` postfix | 每次原方法重新计算大小后乘倍率，仅本地角色发出的标记。没有累计倍率。 |
| fog.stop / fog.speed | `OrbFogHandler.Move(): void` 唯一 `speed` 读取 | IL 后插入速度函数，无限/停止优先返回 0，否则乘倍率。每次调用重新检查房主及当前本地角色资格；不改写历史 currentSize。 |
| fog.reveal | `FogSphere.SetSharderVars(): void` 唯一 `REVEAL_AMOUNT` 读取 | 只给着色器 setter 提供值 1，不改变游戏字段。其余寒冷/状态逻辑原样执行。 |

## 所有权与联网边界

油灯和无限道具同时要求：真实持有角色 `Item.trueHolderCharacter` 是当前已初始化、未死亡/昏迷的本地角色，且道具 `photonView.IsMine == true`。无限道具还要求 holderCharacter 没有被临时替换为被喂食角色。不会依靠 overrideHolderCharacter 判定本地所有权。

没有修改 `ReduceUsesRPC` 或 `Item.Consume` 这些接收入口。只拦截本地发起的广播，避免本机跳过收到的消耗、而其他客户端正常消耗导致不同步。其他角色消耗本地持有的物品（如喂食）仍按游戏原规则处理；此版本的无限道具范围为本地持有、自身发起的使用。

浓雾推进要求 Session.InRoom && Session.IsHost；Session 不存在时只允许 Photon InRoom && IsMasterClient 兼容回退。TickWorld 动态公布房主可用性，客机显示不可用原因。游戏原来的浓雾网络同步仍保留（其同步周期约 5 秒），多人显示效果需要实际联机验证。

## 生命周期与恢复

油灯、道具、推进倍率不保存旧燃料、旧次数或旧世界进度，因此关闭功能不会回滚真实玩法进度。

标记跟踪最多 64 个 WeakReference；记录本帧原方法计算的基础 scale，仅当前 scale 仍等于本补丁写入值时恢复。角色切换/资格丢失/关闭/Reset/Dispose 都会清理；超过容量先恢复最旧条目。

浓雾显示跟踪最多 16 个 WeakReference；关闭时只恢复 renderer 的 _RevealAmount，使用此刻游戏 REVEAL_AMOUNT 字段，不恢复历史快照。仅当 shader 值仍为本补丁写入的 1 时改写。没有为了恢复重新执行 SetSharderVars（该方法含寒冷状态累计逻辑）。

本实现没有临时改写 FogSphere.REVEAL_AMOUNT，因此即使游戏方法抛异常，也没有字段状态需要在 finalizer 中回滚。

原 ConsumeDelayed 迭代器会把 consuming 永久设为 true，然后在一帧后广播销毁；只跳过 Consume 会留下不可恢复状态。自制迭代器完全不写此字段，也不触碰临时喂食接收者。扫描整个程序集后，get_consuming 仅由 Character.GetFedItemRPC 使用，见 world_consuming_refs.txt。迭代器第一次运行时重新检查功能与所有权；若已经失效，转回原迭代器。

## 补丁验证与编译

三处 transpiler 都要求精确匹配 1 个目标读取，否则抛出并将对应 capability 标记不可用。任何补丁组未完整安装时，其 helper/prefix 保持原游戏行为。

最终整合使用真实角色代码和实际游戏 Mono/Unity/BepInEx 引用，标准项目 Release 构建通过，无警告或错误。WorldPatch 显式使用 Harmony 六参数重载。验证替身仅用于隔离分支测试，正式插件不包含替身。

## 2026-10-29 新增独立验证

- `world_static_check.py` 直接只读解析当前 F: 游戏程序集，17/17 检查通过；核对整程序集 SHA256、3 个实际 IL 输入各仅出现一次、方法签名、道具所有权/回调字段、原 ConsumeDelayed 的 consuming 与销毁 RPC、标记每次重新计算 scale，以及 FogSphere 的 property block 和伤害流程。报告 `world_static_results.json`。没有执行游戏 DLL。
- `world_validation/` 将真实生产 `GameAdapter.World.cs` 与托管 Unity/Harmony 替身编译运行，13/13 检查通过；没有复制生产功能实现来代替测试。覆盖燃料优先级、他人/非本机拥有物品、角色失效、消耗协程首次运行前禁用回退与一次回调、已经运行后停用的一帧结束、房主判断、1000 次新计算无累乘、64/16 缓存上界与驱逐恢复、保留第三方后写视觉参数、三处转译 helper 插入与歧义拒绝。报告 `world_validation/world_results.json`。
- 这两组验证只能证明当前元数据/IL 位置及隔离逻辑分支符合预期。替身不会模拟 Unity 原生生命周期、实际联网、Harmony 运行时 JIT 或每种道具行为，因此仍不能称游戏效果实测通过。
- 使用真实游戏/Unity/BepInEx 引用的 world_syntaxcheck 已再次编译通过；编译 stub 仍在生产项目外，不应分发为正式代码。

整合关注：失去房主身份时本文件会立即恢复原推进计算并显示 capability 不可用，但不自行改变共享 settings 或 stateRevision；主控制器现已清除对应开启状态、递增 stateRevision 并拒绝旧指令；恢复房主身份需要用户重新开启，生产控制器测试已覆盖。关闭/停用都经 ResetWorld 清除视觉引用。

尚未实测游戏内生效。标记功能改变最终显示大小，和旧模板只缩放 minMaxScale 的实现细节不同；它提供同类大小调节但不保证每个观察距离与旧模板像素一致。油量消耗、道具食用/次数、浓雾实际推进和关闭恢复均需用户启动游戏后逐项验证。
