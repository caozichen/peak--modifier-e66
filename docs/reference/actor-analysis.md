> 历史资料：记录当时的分析与计划；当前环境、状态保留策略和使用步骤以根目录 README 与 docs/architecture.md 为准。

# 0.2.0 角色功能静态依据与边界

适配基线：`Assembly-CSharp.dll` SHA256 `E526A5234EA2289EA55297FBD6FB7626C75E86E6A0A26A6B87D82ED8F44CD168`。本轮以静态 IL 复核；尚未启动游戏，不能把补丁编译成功当作游戏实测通过。

## 枚举

实际 CLR 类型名为 `CharacterAfflictions+STATUSTYPE`（嵌套枚举），Int32：Injury=0、Hunger=1、Cold=2、Poison=3、Crab=4、Curse=5、Drowsy=6、Weight=7、Hot=8、Thorns=9、Spores=10、Web=11、Arrow=12、Petrify=13、FlyTrap=14。初始化核对枚举与共享目录一一匹配。

## 真实补丁位置

| 功能 ID | 已确认的成员与实现 |
|---|---|
| speed | `CharacterMovement.GetMovementForce(): float` 后置倍率。原方法计算运动驱动力；不会逐帧累乘字段。 |
| stamina | `Character.infiniteStam` bool getter 后置保留原结果或对本地角色设 true。`UseStamina`、`OutOfStamina`、`OutOfRegularStamina`、currentStamina setter 使用该判定。 |
| stamina.consume | `Character.UseStamina(float,bool,bool): bool` 前置调整正消耗；倍率为零时让免费动作返回 true，避免游戏原 `UseStamina(0)` 的 false 阻止动作。 |
| climb.consume | 与通用消耗相乘；按墙面、绳索、藤蔓角色状态及原消耗调用方法作用域识别，包含抓取／攀爬跳跃的瞬时调用。作用域用 finalizer 释放。 |
| stamina.regen | `Character.AddStamina(float): void` 前置调整正增量；包含自然回复和物品给予体力，不仅自然回复。 |
| stamina.delay | `Character.CanRegenStamina(): bool` 中唯一 `stloc.0` 写入等待时间前注入可配置秒数。保留地面、蛛网、挂点等原判断；不改计时器字段。 |
| climb.speed | 替换墙面 `GetRequestedPostition()`、绳索 `Update()`、藤蔓 `FixedUpdate()` 中对各自 `climbSpeed` 的实际读取，读取值乘倍率，原字段不变。 |
| jump | 普通 `JumpRpc(bool)` 启动了延迟协程；因此直接补丁该协程 `MoveNext` 内对 `CharacterMovement.jumpImpulse` 的实际读取，避免只在 RPC 调用时暂改字段却错过延迟施力。 |
| fall.threshold | `CharacterMovement.FallTime(): float` 返回测得时间除以阈值倍率，使原地面摔伤判断及 FallFactor 同时使用调整后的时间。不是额外无敌。 |
| fall.disabled | `Character.Fall(float,float): void` 前置跳过本地角色的跌倒流程。**不等于摔伤免疫或不死**。 |
| slippery.disabled | 墙面请求位移中读取 `CharacterData.slippy` 时返回 0；并在本地墙面请求作用域内忽略 `ClimbModifierSurface.onlySlideDown`。不改共享表面对象，不取消体力耗尽导致的自然下滑。 |
| status.* | `CharacterAfflictions.AddStatus(STATUSTYPE,float,bool,bool,bool,bool,bool):bool` 对正增加先应用阻止，再应用全部×单项倍率；负值保留。 |
| status.*（直接赋值） | `CharacterAfflictions.SetStatus(STATUSTYPE,float,bool):void` 检查正增量。反复重算的 Weight／Thorns／Arrow 用绝对派生值计算倍率，防止逐帧漂移／振荡。 |
| status.Petrify.* | 额外覆盖 `CharacterAfflictions.AddPetrify(int):void`。AddStatus 嵌套调用时用作用域防止重复乘倍率。直接设置石化时读真实 `CharacterData.petrifyAmount` 作比较。整数石化增量受游戏整数粒度影响。 |
| weight.disabled | SetStatus 的 Weight 输入暂时设为 0；开关变化和 Reset 时调用游戏原 `CharacterAfflictions.UpdateWeight()` 依据当前物品重算，保留真实物品与重量。 |

所有通用 Actor 补丁都通过当前本地 Character、IsLocal、IsInitialized、死亡／昏迷检查。世界／物品另由 World partial 负责。Patch 抛错或者所需 IL 位置缺失时不宣称相应能力可用；复合功能要求各必需补丁全部建立。

## 语义与恢复

- 跳跃功能实际改变跳跃冲量，最终高度仍受重力、物品、跳跃方向等影响；不是保证高度线性乘倍。
- 地面跌落时间阈值不覆盖 `CharacterClimbing.CheckFallDamage(RaycastHit)` 的独立滑落碰撞算法。禁止跌倒也不阻止其伤害增加；伤害增量可用 Injury 状态开关处理。
- 状态功能阻止后续增加，不清除已经产生的状态，不撤销已经生成的物件、动画或网络事件。
- 大多数功能仅改变参数／方法计算结果，关闭后下一次计算恢复原行为。体力回复与状态增加等已经发生的游戏变化不回滚成旧快照。
- noWeight 是暂时性的派生状态覆盖，关闭时按当下物品重新计算。旧角色引用在切换前完成重算后释放；其余不保存无界对象集合。
- 默认全部关闭，生命周期及失联由 TrainerPlugin 主线程 Reset。Core 的 Tick 转发世界效果；Dispose 释放世界状态并清空当前角色。

分析输出：同目录 `*.full.txt`，由 `inspect_actor.py` 读取游戏 DLL 元数据及 IL 产生，不执行游戏程序集。
# 2026-10-29 补充复审

- 当前游戏程序集 SHA256 再次核对未变化。
- 修复真实构建失败：HarmonyX 旧 5 参数 Patch 重载是 Obsolete(error)，改为显式 6 参数重载；netstandard2.1 编译不能直接链接 Harmony API 中的 ILGenerator/Label，原始指令读取改用公共重载反射调用，标签和异常区段通过 Harmony MoveLabelsTo/MoveBlocksTo 转移。
- 保留分支跳入被替换读取时的标签和异常边界，避免跳过 helper。
- GameAdapter.Reset 使用 finally 保证世界恢复异常后仍尝试负重重算；Dispose 使用嵌套 finally 保证世界清理异常也会清空静态适配器、角色引用与线程作用域。metadata-only 字典同时清空。
- 全插件标准 csproj netstandard2.1 Release 构建通过，0 warnings/0 errors（当时 World 文件现有版本也参与编译）。
- 离线静态结构检查 18/18：恢复延迟单写点、三种攀爬速度读点、所有攀爬消耗作用域、延迟跳跃冲量读点、主要 hook 方法。结果 actor_static_checks.json。
- 44 项角色控制均有能力报告（14 个基础控制 + 15×2 个单项状态）。没有新增长期持有角色或其他场景对象的集合；反射元数据字典为有限类型/字段集合。

保留的实际边界：跳跃改变冲量而非保证线性高度；体力恢复包含 AddStamina 物品增量；状态阻止作用于后续增加，不清除已有状态或回滚事件；FallTime 阈值倍率只覆盖地面摔伤算法，墙面滑落碰撞是独立算法；禁止跌倒不等于禁止伤害或死亡。RPC_PetrifyInstantly 是带死亡动作的事件，不属于普通 AddStatus 增量，状态阻止不承诺拦截该事件。

仍未运行游戏，Harmony 实际打补丁成功率、Unity/Mono 内存与游戏效果需要实机验证。

最终整合补充：InitializeActor 用 GetNestedType 查找状态枚举；真实游戏成员/生产 IL transformer 31/31 检查通过。插件 csproj 现已显式使用游戏 Mono 引用，Release 构建 0 警告/0 错误；无需在用户电脑安装开发环境。
