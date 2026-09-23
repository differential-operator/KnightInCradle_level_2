# KnightInCradle 核心机制与实现总结

> 适用范围：AliceInCradle ver0.30（Unity 2022.3.62f2，BepInEx 5.4.23.5）＋ KnightInCradle v0.2.0。
> 本文基于当前工作区源码梳理：模组源码 `KnightInCradle/src`、游戏本体
> `../AliceInCradle Win ver030/AliceInCradle_ver030`、以及工作区内反编译参考代码
> `KnightInCradle/reference`（Assembly-CSharp / unsafeAssem / pixelliner / HollowKnight）。
> 注意：`reference` 内反编译工程标注为 ver0.29 产物，与 ver0.30 大体一致，个别方法 IL 存在差异
> （源码里留有“0.29j 上该 IL 导致 Harmony 编译失败”的替代补丁，可反证版本差异）。

---

## 1. 概述

### 1.1 一句话总结

KnightInCradle **没有替换 PRNoel，也没有给世界新增一个“第二玩家”**。它的做法是：

1. 小骑士是一个**独立的渲染实体**（KnightEntity，自绘网格 + 自研物理/状态机），
   负责 Hollow Knight 的所有动作、手感、技能、数值与演出；
2. 诺艾尔（PRNoel）作为**隐藏的“宿主”**继续存在于世界中：她的小碰撞体被改造成
   小骑士受击箱，被每帧 `moveBy` 拉到骑士坐标、物理暂停，让**敌人的攻击判定、接触伤害、
   出门/事件/菜单/镜头/存档**等所有 AIC 世界机制都以为玩家还在原地；
3. 在宿主与真实玩家之间架一层 **Harmony 守卫（CombatGuard）**，把“打到诺艾尔”的伤害
   转给小骑士、把诺艾尔的状态/输出/渲染副作用全部摁住。

因此本模组本质是：**“傀儡角色（小骑士）” ＋ “宿主角色（诺艾尔）” ＋ “世界机制复用层”**
三者每帧协同的复合角色系统。

### 1.2 总体架构

```
                    ┌────────────────────────────────────────────┐
                    │            AliceInCradle 世界              │
                    │   Map2d / 敌人 / 事件 / 相机 / 菜单 / 存档   │
                    └───────▲──────────────────────▲─────────────┘
          受击判定/出门/镜头/存档│                      │渲染票据/伤害/实体交互
                     ┌───────┴────────┐      ┌────────┴──────────┐
                     │   PRNoel(宿主)  │      │  KnightEntity     │
                     │ 每帧拉到骑士位置 │      │  自绘渲染+自研物理 │
                     │ 隐藏渲染/禁输出  │      │  全部 HK 机制/数值 │
                     │ 碰撞体=骑士尺寸  │      │  护符/技能/动画   │
                     └───────▲────────┘      └────────▲──────────┘
                             │同步                    │输入
                     ┌───────┴────────────────────────┴──────────┐
                     │   KnightInCradleBehaviour / CombatGuard    │
                     │   Harmony 补丁 + 模式切换 + 相机/UI 接管      │
                     └────────────────────────────────────────────┘
```

### 1.3 相关代码/资产位置

| 内容 | 路径 |
|---|---|
| 模组源码 | `KnightInCradle/src/*.cs`、`KnightInCradle/src/CharmUi/*.cs` |
| 模组工程/部署脚本 | `KnightInCradle/KnightInCradle.csproj`、`deploy.ps1` |
| 反编译参考（AIC 主程序集） | `KnightInCradle/reference/AIC/Assembly-CSharp/nel` |
| 反编译参考（m2d 引擎/XX/evt） | `KnightInCradle/reference/AIC-extras/unsafeAssem/unsafeAssem/{m2d,XX,evt}` |
| 反编译参考（空洞骑士） | `KnightInCradle/reference/HK` |
| 部署后的插件目录 | `…/AliceInCradle_ver030/BepInEx/plugins/KnightInCradle/` |
| 已有系统文档 | `docs/AIC_player_system_map.md`、`docs/M3_外观切换原型说明.md` 等 |

---

## 2. AliceInCradle 本体关键机制（模组视角）

### 2.1 运行时与 Mod 框架

- 游戏是 Unity IL2CPP 之外的 **Mono/.NET Framework 4.x** 程序，主程序集 `Assembly-CSharp.dll`。
- 使用 **BepInEx 5.4 + doorstop（winhttp.dll）** 注入，模组目标框架 `net472`。
- 修改方式是 **Harmony 前缀/后缀补丁**，极少用 Transpiler（部分方法 IL 在 0.29j 上会让
  Harmony 编译失败，因此源码大量采用“换一个入口打补丁”的容错策略）。
- 游戏在启动/换场景时会销毁挂在场景里的插件 GameObject，因此 `Plugin.Awake` 创建
  `DontDestroyOnLoad + HideAndDontSave` 的常驻对象承载所有运行时逻辑。

### 2.2 程序集分层

| 程序集 | 命名空间 | 作用 | 反编译目录 |
|---|---|---|---|
| Assembly-CSharp.dll | `nel` | 游戏内容：玩家、敌人、道具、UI、战斗、存档 | `reference/AIC/Assembly-CSharp/nel` |
| unsafeAssem.dll | `m2d` / `XX` / `evt` | 自研 2D 地图引擎、工具库、事件系统 | `reference/AIC-extras/unsafeAssem/unsafeAssem` |
| pixelliner.dll | `PixelLiner` | 像素动画数据（PXL/Pxls） | `_decomp_battle`、工具记录 |

模组源码引用的关键类型：`Map2d`、`M2MoverPr`、`M2Phys`、`M2PxlAnimatorRT`、
`M2RenderTicket`、`MeshDrawer`、`M2Attackable`、`M2PrADmg`、`M2Ser`、`PRNoel`、
`NelEnemy`、`NelAttackInfo`、`MagicItem`、`EnemySummoner`、`UIStatus`、`COOK`、`EV` 等。

### 2.3 单位与坐标系（最容易踩坑的部分）

| 概念 | 值/含义 |
|---|---|
| `CLEN` | 28：1 格地图 = 28 mesh px |
| `pixel2ux / pixel2mesh` | `1/64`：1 ux（骑士/地图本地单位）= 64 mesh px |
| 1 格 | `28 mesh px = 0.4375 ux` |
| 坐标轴方向 | 地图/网格 **y 向下为正**；`pixel2uy` 会翻转，ux 内 **y 向上为正** |
| 地面判定 | `Map2d.getConfig(cx,cy)` + `CCON`（isFloor / canStand / isWater / isEmpty…） |
| 常用实体坐标 | `pr.x/pr.y` 是中心；`pr.mbottom` 是脚底；骑士用 `X,Y` 中心 + `Y+SizeY` 脚底 |

模组内所有“小骑士格坐标 → 世界渲染坐标”都必须走
`mapT.TransformPoint(pixel2ux(X*CLEN), pixel2uy(Y*CLEN))` 这一条换算链，
不能把 ux 直接当世界坐标（`strategy.cs` 里记录了 4 次换算踩坑）。

### 2.4 玩家类层级与组件

```
M2Attackable（m2d，可受伤基类）
  └ M2MoverPr（m2d，移动/物理/碰撞/输入）
      └ PR（nel，抽象玩家：状态机、组件容器）
          └ PRMain（nel，凳子/结算/恢复）
              └ PRNoel（sealed，诺艾尔本体）
```

`PR` 上的关键组件（详见 `docs/AIC_player_system_map.md`）：
`Anm`（动画器）、`Skill`（手杖/技能状态机）、`DMG`（受击）、`Ser`（状态效果）、
`GSaver`（缓冲扣血条）、`SpMp`（预消耗魔力）、`NoDamage`（无敌）、`SttInjector`（剧情状态注入）、
`UP`（左侧立绘）等。玩家在 `SceneGame` 场景预制体 `GobNoel` 上，运行时取用
`(M2DBase.Instance as NelM2DBase).getPrNoel()`。

### 2.5 帧循环、事件与输入

- 游戏主循环是 `SceneGame.Update → M2DBase → Map2d.runUi → PR.runUi`（逐层推进）。
- 玩家输入统一由 `XX.KEY`（InputActionAsset）→ `XX.IN` → 玩家控制器读取；
  受 `EV.lockPrInputManipulate(KEY.SIMKEY.xxx)` 剧情锁控制。
- `M2MoverPr` 有 **`simulate_key`（uint 位集）**：`L/R/T/B`、`CHECK` 等都是位。
  **`KEY.SIMKEY.CHECK = 4194304`**。`isCheckPD` 在锁输入时会改走 simulate_key 分支。
- `PR.need_check_event = true` 用于让 runUi 重新检测脚下事件（出门传送点、区域触发等）。
- 菜单/地图：`NelM2DBase.menu_open` 是 `MENU_OPEN`（NONE/OPEN/OPEN_MAP/...）状态机。
- 相机默认跟随玩家（`M2Camera` 的 `MvCenter`/`FocusTo_`、`cam_walk_speed_x/y`），
  小骑士模式直接借用“镜头跟随诺艾尔”这一点，让镜头钉在骑士身上。

### 2.6 伤害与状态管线（敌人 → 诺艾尔）

```
敌人攻击 → AttackInfo / NelAttackInfo（含击退、盾反、吸收等字段）
  → M2PrADmg.applyHpDamageSimple / applyDamage        （无敌、修正、转伤）
  → M2Attackable.applyHpDamage(int, bool, AttackInfo) （扣血，hp=max(hp-val,0)）
  → hp<=0 → initDeath（战败演出）
```

- MP 伤害另有 `PR.applyMpDamage` / `MpGaugeBreaker`（魔力条碎裂）；
- EP（被抓取/吸取）走独立 `EpManager.applyEpDamage`；
- 状态效果（中毒/冰冻/束缚/眩晕等）统一走 `M2Ser.Add`，用 `ser_bits` 位集；
- 剧情/受击状态注入走 `PR.changeState` / `M2PrADmg.changeState` / `PrStateInjector`；
- 部分接触类控制：`M2FootManager.rideInitTo`、`PR.initAbsorb`（被骑乘/被吸附）、
  `M2WormTrap`（虫巢抓取）。

这些入口正是 CombatGuard 逐个拦截的位置（见 3.4）。

### 2.7 渲染管线（为什么“正常 Unity 精灵”画不出来）

- 角色与可动对象**不走普通相机 + SpriteRenderer**，而是 AIC 自绘 GL：
  `M2Camera.MovRender`（`M2MovRenderContainer`，按专用 drawer 层/相机）管理
  **`M2RenderTicket` 票据**；
- `M2PxlAnimatorRT.initRenderTicket(DRAW_ORDER order)` 内部调用
  `Map2d.MovRenderer.assignDrawable(order, null, FnPrepareMd, MeshDrawer, Mv, null)`；
- 票据渲染回调 `FnPrepareMd(Camera, M2RenderTicket, need_redraw, draw_id, out MeshDrawer, ref bool overwrite)`
  每帧由引擎回调，模组在这里向 `MeshDrawer` 填四边形/粒子；
- 排序用 `M2Mover.DRAW_ORDER`：`PR0 / PR1 / PR2` 就是玩家身前/身后附近的分层，
  模组给“本体/身前特效/身后特效”分别分配票据。
- 因此模组必须把骑士注册进 `MovRenderer`（同 PR 通道），否则任何标准 SpriteRenderer
  都不会出现在可见相机里（M3 文档记录了这条探索过程）。

### 2.8 UI

- 战斗 HUD 是 `UIStatus`：`hp_ratio / mp_ratio / cushion_hp / cushion_mp / mp_hold` 等字段，
  `redraw_hp / redraw_mp / redraw_bar_num` 置脏，`drawMHBar` 重建**网格顶点**；
  顶点顺序固定：前 4 顶点是填充段，其余是 gsave/cushion/hold/背景段。
- HUD 显隐由 `base_y_level`、`t`、`ui_hold_time`、`t_settop` 控制，`UIStatus.run` 每帧推进。
- 左侧立绘是 `UIPicture`（Spine），表情由 `changeEmotIn/changeEmotDefault/readFader` 驱动，
  姿态由 `UIPictureBase.EMSTATE`（如 BATTLE）决定。
- 菜单布局由 `UIBase` + `uipic_lr`（立绘在左/居中）与 `ui_shift_x` 控制。

### 2.9 存档、长椅与传送

- 通用存档键值：`COOK` 静态字典 `Osf`，`COOK.setSF/getSF`（0~255），
  随 `createBinary/readBinary` 序列化进存档文件；`SVD` 管理存档文件与出生点。
- 长椅：`NelChipBench` 是地图芯片（`getAllPointMetaPutsTo(..., "bench")` 可检索），
  `fineIcon()` 点亮地图图标，`SVD.assignRevertPosition` 记录“回到该椅子”的存档位。
- 快速旅行：地图菜单执行 `UiBenchMenu.ExecuteFastTravel` → 事件传送
  `M2LpMapTransferBase.executeTransferFastTravel`，期间 `M2DBase.transferring_game_stopping` 为 true。

### 2.10 敌人、战斗与魔法对象

- 敌人基类 `NelEnemy`，受击统一 `applyDamage(NelAttackInfo, bool)`：
  内含伤害数字、硬直（`checkDamageStun`，可能施加 SER.EATEN 倒地）、击退、死亡掉落。
- 战斗区域由“魔力草/召唤点” `M2LpSummon`/`M2ManaWeed` 拉起，战幕状态由
  `EnemySummoner`（`isActiveBorder()`）管理。
- AIC 的投射物/法术运行时对象是 `MagicItem`（`MGKIND` 枚举 + `MGHIT` 命中旗标 +
  `MagicItemHandler` 运行），伤害包 `NelAttackInfo.PublishMagic` 指向它。
  联机服务器只认 `MGKIND.PR_*（9000 段）` 的攻击，这是模组联机兼容补丁的切入点。
- 可被玩家打的“非敌人实体”（标靶 `M2MatoateTarget`、拳炮 `M2PuncherCannon`、
  魔力塔、可破坏墙 `M2BreakableWallMover`、虫墙、魔力草、蛛丝球等）也都是 `M2Attackable`
  或运行中的 `MagicItem`，骑士需要逐类兼容。

---

## 3. 模组实现方式

### 3.1 源码文件地图

| 文件 | 行数 | 职责 |
|---|---|---|
| `src/Plugin.cs` | 190 | BepInEx 入口：注册全部 ConfigEntry、`KeyFile.Load`、创建常驻对象；`KeyConfig` 统一按键判定 |
| `src/KeyFile.cs` | 225 | `键位.txt` 读取/生成（中文键名 ↔ KeyCode） |
| `src/KnightInCradleBehaviour.cs` | 1792 | 模式切换（T）、宿主同步/镜头/隐身、认真模式、护符 UI 开关、Harmony 汇总 |
| `src/CombatGuard.cs` | 1550 | 战斗守卫：约 60 个 Harmony 补丁，玩家管线 ↔ 骑士数值/状态的“翻译层” |
| `src/KnightEntity.cs` | ~23300 | 小骑士本体：主状态机、物理、渲染票据、技能/护符、HUD 数据源、长椅/重生 |
| `src/KnightAttackHitbox.cs` | 52 | 攻击判定框 Trigger 回调 |
| `src/DashAudio.cs` | 1088 | 嵌入式 WAV 资源 + 自研 waveOut 混音器（分组/循环/音量预缩放） |
| `src/KnightHudDeco.cs` | 391 | HUD 装饰图（相对血条框定位）、淡入、梦语文本框 |
| `src/MultiplayerCompat.cs` | 662 | 联机（Kaleidoscopic）兼容：伤害包伪装、远端骑士渲染、头顶条 |
| `src/KnightFxSync.cs` | ~400 | 联机特效/判定箱同步负载的编解码（护盾占位 + 特效四边形 + 下砸骨剑尖刺 + v5 骨钉判定箱） |
| `src/NativeBody.cs` | ~350 | 方案A 原生移动接管：把方向键翻译成诺艾尔 `simulate_key`（见第 8 节） |
| `src/strategy.cs` | 107 | 纯注释：判定框调试方法论、坐标系换算、拉伸碰撞箱套路 |
| `src/CharmUi/*` | ~4200 | 护符 UI（Controller/Loader/OnGuiLayer）、护符效果 CharmEffects、存档 CharmSave、音频 |

### 3.2 启动流程

`Plugin.Awake` 做的事：

1. 绑定约 30 个 ConfigEntry（键位、缩放、速度、音量、护符 UI 布局等）；
2. `DashAudio.Init`（从程序集内嵌资源解出全部 WAV，按音量预缩放 PCM，启动混音线程）；
3. `KeyFile.Load`（生成并读取 `键位.txt`，覆盖 ConfigEntry）；
4. 创建 `KnightInCradle_Runtime` 常驻对象，挂 `KnightInCradleBehaviour` + `KnightHudDeco`；
5. `Behaviour.Start → ApplyHarmony()`，把 `CombatGuard / CharmEffects / CharmUiInputPatch`
   的补丁以及 `SceneGame.Update` 的 TryToggle 兜底 postfix 一起挂上。

### 3.3 模式切换与“宿主同步”（核心机制）

#### 切换门控（`TryToggle`）

- 默认 **T** 切换（避开被游戏占用的 F1~F5）；
- 禁切场景：护符 UI 打开、骑士坐姿/死亡/虚空解放中、诺艾尔处于 BENCH 系列状态。

#### 进入骑士模式（`ActivateKnightMode`）

1. **快照诺艾尔真实 HP/MP 字段**（反射读 `M2Attackable.hp/maxhp/mp/maxmp`，不走 getter——
   getter 之后会被 CombatGuard 劫持成骑士数值，读 getter 会读到骑士数据）；
2. `ResetNoelToHealthy`：清 `Ser` 状态、关 `SttInjector`、`changeState(NORMAL)`、清风压计时；
3. 取消诺艾尔蓄力魔法（`killHoldMagic`）、清 `SpMp`（预消耗段）；
4. 隐藏诺艾尔：`alpha=0` + **停用渲染票据**（`deassignDrawable`）＋ 禁用所有 Renderer；
5. 给诺艾尔动画器挂 `fnChangePoseListener` 白名单：只能 stand/walk/jump；
6. 立绘强制 BATTLE 姿态；HUD 常显；
7. `ResizeNoelToKnight`：把诺艾尔碰撞体尺寸改成小骑士受击箱尺寸并重建碰撞体
   （**怪物打中的是“套了骑士尺寸的诺艾尔碰撞箱”**）；
8. `KnightEntity.SpawnAt(pr)`：在诺艾尔位置生成骑士实体、贴地、记复活点、`RebindTicket`。

#### 每帧宿主同步（`SyncNoelToKnight`，Update + LateUpdate 各一次）

- 目标：`诺艾尔中心X = 骑士X + HurtCenterX`，`诺艾尔脚底 = 骑士脚底`；
- 用 `pr.moveBy(dx, dy, false)` 直接搬（不触发脚部重检）；
- `pr.need_check_event = true`：**否则 moveBy 拖动不会触发游戏的事件/出口检测**，
  小骑士走到门口无法换房；
- `phy.killSpeedForce(...)` + `phy.Pause()`：钉死诺艾尔物理，防止 AIC 重力/脚本把她带走；
- 镜头：抬高 `cam_walk_speed` 跟随骑士；超级冲刺用更高速镜头；距离过大才 `setTo` 瞬移；
- 每帧恢复诺艾尔 HP/MP 快照：CombatGuard 把 getter 劫持成骑士数值后，
  AIC 内部逻辑（施法/受击的参数修正）会把骑士数值**写回**诺艾尔字段，
  这里用反射字段每帧拉回快照值防止污染；
- 转房/重定位阶段例外：恢复诺艾尔物理，让 AIC 进门强制位移推她走，骑士随后逐帧跟随；
- 隐藏诺艾尔有三层保险：Update、LateUpdate、`RenderPipelineManager.beginCameraRendering`，
  并针对所有 `M2PxlAnimatorRT`、MeshRenderer/SpriteRenderer/SkinnedMeshRenderer。

#### 切回诺艾尔（`DeactivateKnightMode`）

按相反顺序：恢复碰撞体原尺寸并重建、恢复状态注入、恢复快照、`pr.setTo(骑士位置)`、
强制退出蹲伏、恢复物理与脚部管理器、恢复立绘自动表情、恢复相机速度、清空骑士实体状态
（`KnightEntity.Deactivate`）、恢复黑暗/雾天原版可见度。

### 3.4 CombatGuard：玩家管线 ↔ 小骑士的翻译层

设计目标一句话：**骑士模式下，隐藏的诺艾尔“不造成伤害、不受到伤害、不进异常状态、
不出任何演出副作用”，但这些事件要“翻译”成小骑士的独立数值/状态。**

补丁分组（`CombatGuard.Apply`，逐个 `TryPatch` 容错，挂载结果 `_patchOk/_patchFail`）：

| 组 | 拦截对象 | 处理 |
|---|---|---|
| 伤害入口 | `M2PrADmg.applyHpDamageSimple`、`M2Attackable.applyHpDamage`、`M2PrADmg.applyDamage`（两个重载） | 命中即 `RouteDamageToKnight(Atk)` → 小骑士 `KnightTakeDamage`；诺艾尔本体重置为 0 伤害。同帧同 Atk 去重 |
| MP/魔力 | `PR.applyMpDamage`（两个重载）、`M2Attackable.applyMpDamage`、`MpGaugeBreaker.*` | 全部跳过；魔力条永不破碎 |
| 状态 | `PRMain.changeState`、`M2PrADmg.changeState`、`M2Ser.Add`、`EpManager.applyEpDamage`、`M2PrMistApplier.*`、`PR.applyGasDamage` | 只允许 NORMAL/_OFFLINE；免疫中毒/雾/EP/抓取 |
| 控制类 | `initAbsorb`（敌/我）、`rideInitTo`、`canPullByWorm`、`M2WormTrapDamage`、`M2SinkEffect.addMover`、`FallenCutin` | 免疫吸附/骑乘/虫巢拉扯/下沉/坠落聚焦 |
| 视觉/演出 | `M2PxlAnimatorRT.set_color/set_alpha`（仅诺艾尔）、`UIPicture.run`、`changeEmot*`、`readFader`、`applyDamage`、O2 条 | 诺艾尔强制透明、立绘锁 BATTLE 姿态 |
| 输入 | `EV.lockPrInputManipulate`、`ShieldOpening/runState` | 放行菜单键与 CHECK；禁止诺艾尔闪避/开盾 |
| 数值读口 | `M2Attackable.get_hp/get_maxhp/get_maxmp`、`PR.getCastableMp` | 骑士模式下对 PRNoel 返回骑士数值（HUD/内部逻辑都读到骑士数据） |
| HUD | `UIStatus.run`、`redrawAll`、`UIBase.fineHpMpRatio`、`showO2Gauge` | 比例=骑士数据；网格染色为“黑血条+白灵魂条”；清理 hold/SpMp 段 |
| 世界接入 | `COOK.initGameScene`、`M2LpMapTransferBase.executeTransferFastTravel`、`UiBenchMenu.ExecuteFastTravel` | 读档/传送后重建骑士状态、绑定票据、HUD 淡入 |
| 特化 | 圣域 `WholeMapManager.fnMgRun_initS_Sacred`、大炮 `M2PuncherCannon`/`isShotgun`、蜘蛛 BOSS 织网阶段、`M2LpSummon.deassignActiveWeed` | 让骑士攻击能正确驱动这些 AIC 机制 |

**伤害路由的过滤白名单**（`RouteDamageToKnight`）：AIC 地图伤害（`DIFF.isMapDmg`，
尖刺/激光等）、大炮炮弹、蜘蛛陷阱、谷仓教学 Primula 的攻击、泡泡、农场动物等，
不转给小骑士（部分由骑士自己的攻击逻辑另行处理）。

### 3.5 骑士主状态机与自研物理

#### Update 结构（`KnightEntity.Update`，约 9308 行起）

整体是一个“优先级链式状态机 + 每状态锁输入”的巨型方法：

1. 后台系统推进：护符（蜂群/防御者纹章/子宫/蜂巢之血/孢子/编织者/梦盾/格林…）、
   无敌/闪光计时、灵魂无限检测、黑暗/雾天提亮；
2. 看门狗：冲刺/蓄力/下砸卡死强制解除、重定位超时、残留转房事件强制 `EV.evEnd`、
   掉出地图回传安全点；
3. 地图切换分支：`_mp != curMap` → 重绑票据、跨图延续下砸、清理旧图投射物/召唤物、
   读档对齐、快速旅行跟随；
4. 输入收集（A/D/W/C/S/空格/Q/鼠标右/Shift/鼠标左/F + 合并键语义）；
5. 状态优先级链（后面的 goto 到 `SitPhysicsSkipped` 跳过物理）：
   死亡 → 复活淡出 → 受伤硬直 → 坐长椅 → 挑衅 → 虚空解放 → 凝聚 → 超级冲刺 →
   暗影之魂 → 深渊尖啸 → 黑暗降临 → 游泳 → 骨钉技艺/冲刺劈砍/旋风/梦钉 → 冲刺 → 攻击；
6. 普通物理：朝向、走路、跳跃/二段跳/蹬墙跳/爬墙、细分水平步进、重力/下落上限、
   弹簧地板/单向平台穿透/落地重置；
7. 动画剪辑选择（一个“当前状态 → 剪辑”的巨型 if 链）。

#### 手写物理而非复用 M2MoverPr

骑士保留 HK 手感（帧单位换算自原版数值），因此**不继承 AIC 的 M2Phys**，而是：

- 自己维护 `X/Y/Vx/Vy/Grounded/_onWall`，单位“格/帧 @60”（`dt = deltaTime*60`）；
- 地面/墙壁/天花板用 `Map2d.getConfig + CCON` 逐格查询；
- 高速位移（冲刺/超冲）做“细分步进”，避免穿墙；
- 落地时把诺艾尔对齐由 Behaviour 完成（骑士本身不依赖 AIC 碰撞体）。

### 3.6 渲染实现

#### 票据注册（`RebindTicket` / `ReleaseTicket`）

每次进入地图、读档、快速旅行结束都会重绑（旧票据在换图/读档后失效）：

- 每个渲染层一个 `MeshDrawer`（一个网格只能绑一张贴图）＋ `assignDrawable` 注册；
- 本体用 `DRAW_ORDER.PR1`，身后特效 PR0、身前特效 PR1/PR2；
- 注册回调里根据当前帧精灵画四边形（`initForImgAndTexture` + `Rect`），
  坐标全部经 `mapT.TransformPoint(pixel2ux(...))` 换算；
- 特效种类非常多（剑气、火球、残影、粒子、法阵、小格林、子宫幼体…），各自独立票据；
- 调试用绿框票据（`KnightPrepareAttackDebugMesh` 等）按 `const bool xxxDebug` 开关编译。

#### 素材加载（`LoadAssets`）

- 精灵/动画来自 HK 提取的 PNG（部署目录 `assets/hk/sprites/*.png`）；
- `assets/hk/knight_manifest.json` 描述 `sprites`（含 `feet` 锚点比例）与
  `clips`（帧列表/fps/wrapMode/loopStart）；
- 启动只加载白名单剪辑（Idle/Run/Slash/…），并按需程序化派生剪辑：
  攻击剪辑截取原版 Slash 前 8 帧、右墙爬墙镜像、上劈/下劈、低血量待机循环、
  凝聚三段相位、超级冲刺旋转 90° 帧等；
- 全部 `Texture2D` 用 `FilterMode.Point` + `Clamp`（像素风）。

### 3.7 输出伤害：骑士 → 敌人/世界

骑士打怪有三条路径：

1. **普攻/招式命中**：`SpawnHitbox` 生成“Box(矩形) + Polygon(尖端)”两组 Trigger 碰撞体
   （放 `EnemySelf` 层），挂 `KnightAttackHitbox`；`OnTriggerEnter2D` 命中敌人后
   `NotifyAttackHit(enemy)` → 构造 `NelAttackInfo`（`fix_damage=true`、Caster=诺艾尔）
   → `enemy.applyDamage(atk, false)` 走 AIC 完整受击管线（伤害数字/硬直/死亡/掉落都免费拿到）。
2. **每帧兜底 OverlapBox**（`CheckAttackOverlap`）：不受层碰撞矩阵限制，扫
   EnemySelf/Enemy/AttackHitable/Trap/Kusari/Spike/Chips/Water/Default/Ignore Raycast 等层，
   区分敌人、虫墙、可破坏墙、魔力草、蛛丝球、蜘蛛陷阱、尖刺（弹跳）、标靶/拳炮等并分别处理。
3. **法术/召唤物命中**：自建 `MagicItem` 缓存（`_knightAttackMagic`，kind=WATERSHARD 等）
   作为 `NelAttackInfo.PublishMagic` 再走 `applyHpDamage/applyDamage`，以欺骗 AIC 目标
   （标靶要求 PublishMagic 非空、大炮按 isShotgun 判定充能等）。

去重：每次挥砍 `_swingHits`（HashSet）；技能各自带去重字典（驻留伤害等）。

普攻伤害基值 `SlashDamage = 50`（束缚·骨钉时 36）；命中/击杀按护符补魂
（幼虫之歌、灵魂捕手/噬魂者、蜕变挽歌等）。

### 3.8 受击/防御：敌人 → 骑士

骑士自己不注册受击碰撞体；受击流程完全由 3.4 的伤害路由驱动：

```
AIC 伤害打到诺艾尔碰撞箱 → CombatGuard 拦截 → 去重过滤
  → KnightEntity.KnightTakeDamage(Atk)
      → 无敌/霸体/护符判定（巴尔德之壳、无忧旋律 25% 免伤等）
      → 扣面具血（Overcharm 时 2 格）
      → 受击演出：0.3s 画面停滞（Map2d.setTimeScale(0)+Time.timeScale=0）
                   → 0.2s 击飞（HurtKnockVx/Vy）
      → 1.3s 无敌（坚硬外壳 +2s）
      → hp<=0 → StartDeath：死亡动画/黑屏 → 长椅复活
```

死亡/复活是骑士自管状态：`HandleDeathState`、`RespawnAtBench`（复活点 = 首次出生点或
最近坐过的长椅），期间锁输入并淡入黑屏，不用 AIC 的 GAMEOVER 流程。

### 3.9 HUD 与屏幕演出

**复用诺艾尔原版 HUD（UIStatus），但把数据源/颜色替换成骑士的：**

- `get_hp/get_maxhp/get_maxmp/getCastableMp` 前缀：骑士模式下对 PRNoel 返回
  `Health/MaxHealth/Soul/MaxSoul`，HUD 数字、敌人 AI 读血、UI 内部逻辑都拿到骑士数据；
- `fineHpMpRatio`/`UIStatus.run`/`redrawAll` 前后缀：按骑士数值设置 `hp_ratio/mp_ratio`，
  清 `cushion/mp_hold/SpMp`，置脏强制重绘；只在数值变化时写，避免闪烁；
- `RedrawAllPostfix` 直接改 HUD 网格顶点颜色：血条前 4 顶点染黑（生命血蓝 / 蜂巢橙），
  灵魂条染白；其余附加段顶点置透明，防止“白条穿框”；
- 常显（`/` 键）与骑士模式隐藏逻辑通过反射写 `t / ui_hold_time / t_settop / base_y_level`。

**KnightHudDeco（OnGUI）**：读取 `UIStatus.MdH/MdM` 实际填充矩形推算 HP/MP 条中心，
把 HK 的 hud_deco 装饰图相对血条框定位；负责淡入展开动画与梦语文本。

**屏幕演出（OnGUI 全屏纹理）**：受击黑边、回血白闪、亡者之怒红框、低血量黑框、
死亡黑屏、虚空解放黑白闪/触手，均为运行时生成的渐变/纯色 `Texture2D` + `GUI.DrawTexture`。

### 3.10 护符系统

#### 数据与 UI

- `CharmDatabase`（`CharmData.cs`）：护符静态表（Id/图标/名字/描述/费用），
  槽位 `NotchCapacity = 11`，40 号虚空之心固定装备，42 号“束缚”是自限选择器，
  43 号无忧旋律是 39 号格林之子的变体（T 键互换）；
- `CharmUiLoader`：从 `charm_ui/layout.json + images/*.png` 运行时重建 **UGUI
  ScreenSpaceOverlay Canvas**（系统动态字体），`CharmUiOnGuiLayer` 负责分帧显示；
- `CharmUiController`：光标导航（上装备栏 + 下方网格）、坐长椅才能装卸、
  总费用/Overcharm、装卸飞行动画、束缚（GG）按钮状态；
- `CharmUiInputPatch`：界面打开期间给约 40 个 `IN.*` 输入方法挂前缀返回 false，
  防止长椅状态/游戏菜单抢输入。

#### 存档

- `CharmSave`：装备写入 `COOK.setSF("kic_charm_slot0..10", id)`，随 AIC 存档二进制持久化；
  束缚/变体等状态用 `kic_gg_*` / `kic_charm_variant`；
- `COOK.initGameScene` postfix → `CharmSave.RestoreAfterLoad()`：读档恢复装备、
  重置束缚状态、修正“坚固贪婪”背包容量。

#### 效果实现

- 纯骑士内逻辑：移动速度/冲刺/跳高/爬墙、普攻时长/范围、法术伤害/消耗、凝聚治疗、
  再生类（蜂巢之血、生命血、国王之魂）、召唤物（编织者/格林/子宫/吸虫/孢子）由
  `KnightEntity.Update` 内的 `UpdateXxx` + `CharmEffects.IsEquipped(id)` 查询驱动；
- 需要动 AIC 世界的效果走 Harmony（`CharmEffects.Apply`）：
  敌人死亡掉率（坚固贪婪复制宝箱）、重击斩杀、魔力草“蜂群集结”只给玩家、
  魔法塔/标靶交互、快速旅行与指南针、战斗区域 AUTO_SAVE_BENCH 抑制等。

### 3.11 长椅、重生与快速旅行（骑士模式下的“世界交互”）

- 交互键 F（或 Q/下键靠近长椅）→ `FindNearBench`（用 AIC 的 7×9 格 bench 检索）
  → `StartSitting`：平滑滑到椅面、播放 HK Sit 动画、回满 9 格血（生命血额外）、
  补魂到 90、`bench.fineIcon()` 点亮地图、`SVD.assignRevertPosition` 记重生点；
- 起身播 `Get Off` 过渡；护符界面打开时禁止起身；
- F 不在长椅旁时走**原生 CHECK**：`SetNoelCheckSim()` 置 `simulate_key` 的
  CHECK 位（4194304）+ `need_check_event`，或直接 `TryExecuteNativeCheckDirect`
  扫描 `IM2TalkableObject/M2EventItem`，让门/NPC/宝箱/存档点交互照常工作；
- 快速旅行：`CombatGuard` 在 AIC 传送执行后调 `KnightEntity.HandleFastTravel()`：
  清坐姿、标记 `_fastTravelInProgress`、跟住诺艾尔直到她走到目的地长椅
  （保证 AUTO_SAVE_BENCH 能 3×3 找到椅子），随后精确放两人到椅位；
  战斗区域传送则在地图切换完成后 0.5s 收尾；到达后 `PendingFastTravelRebind` 重绑票据。
- 读档/新游戏（`COOK.initGameScene` postfix）：血魂回满、灵魂 90、骑士对齐诺艾尔、
  票据重绑、复活点更新、HUD 淡入复位。

### 3.12 联机兼容（Kaleidoscopic）

- `Tick` 用程序集名探测联机模组，装上才打补丁（避免空转开销）；
- **伤害包伪装**：联机服务器只放行 `MGKIND.PR_*（9000~9499）` 的玩家攻击。
  骑士法术自建 MagicItem 是 WATERSHARD 段，在 Kaleidoscopic 的发包汇聚点
  `M2Gunmu.applyHpDamage` 前缀里临时把 kind 改成 `PR_PUNCH(9000)` +
  `MGHIT.PR|IMMEDIATE|NORMAL_ATTACK`，并按“打玩家(×2)/打他人魔物(×3)”预乘抵消
  服务器端的伤害压缩，postfix 还原，本地判定不受影响；
- **远端骑士渲染**：骑士模式把 PlayerInfo 字段当“协议字段”用
  （characterTitle=`__KNIGHT__`、poseTitle=剪辑、frameIndex=帧、aimInt=朝向、
  caneName=精灵名），远端 `render1` 前缀发现标记后改画骑士当前帧 PNG；
- **头顶条**：带标记的玩家由自定义“黑血条 + 白灵魂条”替代联机默认红/蓝条。
- **拼刀判定箱**：骨钉（普攻/骨钉技艺）判定箱随特效负载 v5 段同步，远端还原成绝对格坐标后
  与本地方形判定箱相交即“拼刀”（双方冻结 0.3s + 各 0.5s 无敌 + 弹刀音效一次）。见第 9 节。

### 3.13 调试工具

- **F8**：打印当前房间（key/尺寸/骑士与诺艾尔坐标/地面液体等）；
- `,` 键：骑士坐标轴叠加显示；`/` 键：HUD 常显；`.` 键：认真模式（藏立绘、画面居中）；
- 各种 `*HitboxDebug` 常量 + 绿色线框票据（方法论写在 `strategy.cs`）；
- UnityExplorer（F7）用于运行时查对象/字段。

---

## 4. 关键数值速查

### 4.1 单位换算

| 换算 | 值 |
|---|---|
| 1 地图格 | `CLEN = 28 mesh px` |
| 1 ux | `64 mesh px`（`pixel2ux = ×1/64`） |
| 1 格 | `0.4375 ux` |
| 骑士实体中心 | `X,Y`（格）；脚底 = `Y + SizeY` |
| 贴图缩放默认 | `ScaleConfig = 0.325` |

### 4.2 骑士基础参数（`KnightEntity` 常量）

| 参数 | 值 | 备注 |
|---|---:|---|
| 初始/默认血 | 9 格 | 坚固心脏 +3；束缚·外壳上限 4 |
| 灵魂 | 0~180，初始 90 | 束缚·灵魂上限 30 |
| WalkSpeed | 0.1 格/帧@60 | 飞毛腿 +20% |
| Gravity | 0.009 格/帧² | |
| JumpVy | -0.285 | 松键削减 0.258 |
| FallMax | 0.22 | |
| 二段跳 | 先下沉 0.05s 再 JumpVy | 落地/抓墙重置 |
| 爬墙 | WallSlideSpeed 0.075；蹬墙跳 WallJumpVx 0.22 | 网格预检 + 吸附阈值 0.25 格 |
| 冲刺 | DashSpeed 0.25、DashTime 0.25s（可配置） | 空中一次；暗影冲刺 0.4s 无敌 |
| 受击 | 停滞 0.3s + 击飞 0.2s + 无敌 1.3s | 坚硬外壳 +2s |
| 普攻 | 50 伤害 / 0.28s 冷却（快速劈砍 0.21s） | 左右手交替 |

### 4.3 招式伤害

| 招式 | 伤害 | 备注 |
|---|---:|---|
| 平砍/上劈/下劈 | 50/刀 | 下劈命中可 Pogo |
| 强力劈砍 | 42×3 | 驻留判定 |
| 冲刺劈砍 | 125 | |
| 旋风劈砍 | 40×N | 可延长 3 次 |
| 暗影之魂（火球） | 护符/萨满修正 | 消耗灵魂，暗影系 |
| 黑暗降临（下砸） | 下砸+骨剑 | |
| 深渊尖啸 | 多段 | |
| 梦钉 | 非伤害 | 吸魂/梦语 |
| 虚空解放 | 全屏多段 | 梦钉+上 触发 |

> 注：普攻伤害与技能伤害均受护符（坚固力量/萨满之石/亡者之怒/沉重之击等）与
> GG 束缚（束缚·骨钉）影响，实际数值以 `CharmEffects.ScaleNailDamage` 等函数为准。

---

## 5. 设计取舍与踩坑记录

1. **为什么让诺艾尔“假扮”骑士而不是替换她**
   - 好处：敌人 AI 仇恨、接触伤害、地图出口/事件、菜单、镜头、存档/快速旅行、
     AUTO_SAVE_BENCH、任务判定等世界逻辑零改造即复用；
   - 代价：必须把所有“AIC 对玩家的副作用”逐一拦截（CombatGuard 那 60 个补丁就是这么来的）；
     一旦某个副作用入口版本更新改名/换路径，需要补新补丁。
2. **骑士没有自己的受击碰撞体**：受击箱 = 诺艾尔碰撞体套骑士尺寸。
   因此同步（SyncNoelToKnight）与尺寸恢复（ResizeNoelToKnight）必须严格成对；
   过图脚本重建碰撞体后还要 `RefreshNoelForKnight` 重新套一次。
3. **地图伤害默认对骑士屏蔽**：`DIFF.isMapDmg` 的攻击不转给小骑士，
   尖刺/荆棘只走骑士自己的“下劈/挥砍弹跳”逻辑。若要做完整“踩刺掉血”还原，
   需要在 `RouteDamageToKnight` 放开并自己补充尖刺重叠检测。
4. **反射绕过 getter 劫持**：骑士模式下 CombatGuard 把 `get_hp/get_mp/...` 返回骑士数值，
   游戏内部“读数值→写回字段”会污染诺艾尔本体，所以代码里到处用
   `AccessTools.Field(typeof(M2Attackable),"hp/...")` 直读字段并每帧恢复快照。
5. **渲染票据生命周期**：票据绑定到 `Map2d/M2Camera.MovRender`，换图/读档/快速旅行会
   重建渲染容器，旧票据失效，必须重绑；模组用 `PendingLoadRebind/PendingFastTravelRebind`
   标志持续重试。
6. **版本兼容**：部分方法的 IL 在不同版本（0.29j）会导致 Harmony 补丁编译失败，
   源码采用 `TryPatch` 隔离 + “换一个总入口补丁”的策略，且 `ApplyHarmony` 会把
   `SceneGame.Update` 的补丁失败单独隔离，不让它阻断 CombatGuard。
7. **巨型单文件**：`KnightEntity.cs` 超过 2.3 万行，状态机用优先级 + goto 锁输入。
   优点是状态覆盖一目了然；缺点是新招式容易漏掉“打断/锁输入/复活/换图清理/联机同步”
   等分支。新加状态时可对照 `Deactivate()` 与换图清理段逐项清状态。
8. **音频不走游戏 CriWare**：WAV 内嵌 DLL，自研 waveOut 混音线程按分组播放/停止，
   好处是不依赖 AIC 音频资源与音量体系，坏处是无法自动跟随游戏“静音/音量设置”。

---

## 6. 给后续开发的快速指引

新增一个小骑士技能/护符的常规检查单：

1. `KnightEntity.cs` 增加状态字段与常量（伤害/时长/判定尺寸）；
2. 在 `Update` 的输入收集段加起手判定，在优先级链合适位置插入 `UpdateXxx` 并锁输入；
3. 剪辑：把 HK 帧 PNG 放入 `assets/hk/sprites` 并登记 manifest，或程序化 `BuildXxxClip`；
4. 渲染：`RebindTicket` 注册一个新 MeshDrawer 票据（注意一个 MeshDrawer 只能一张贴图），
   换图/Deactivate/死亡/读档时同步清理；
5. 判定：复刻“Box+尖端 Trigger / OverlapBox 兜底 / 去重集合”三件套；
6. 伤害：构造 `NelAttackInfo`（Caster=AttackFrom=诺艾尔；法术补 `PublishMagic`）
   → `applyDamage`；命中音效/粒子/灵魂结算走既有 helper；
7. 若会跨版本受击/状态，去 `CombatGuard` 登记对应入口；
8. 检查切回诺艾尔/换图/死亡复活/护符 UI 打开/坐姿/联机远端时的清理与同步分支。

---

## 7. 参考文档

- `docs/AIC_player_system_map.md`：AIC 玩家类层级、数值、组件与 Hook 点地图
- `docs/M3_外观切换原型说明.md`：渲染管线探索过程（为何必须用 MovRender 票据）
- `docs/M4_护符系统_素材清单.md`、`docs/M2_素材提取记录.md`：素材与护符清单
- `src/strategy.cs`：坐标系换算与“拉伸碰撞箱”通用套路

---

## 8. 方案A 实施记录（原生移动接管：走/停/跳跃）

> 目的：解决“斜坡进门被卡回原房间”与“墙角穿模掉出地图”两个已知问题。
> 思路：把位置真相从骑士手写格点物理迁移到诺艾尔的原生 M2MoverPr 上。

### 已实现（2026-09-09，编译通过、已部署 ver030 插件目录）

- `Plugin.cs`：新增配置 `General/NativeBodyMode`（默认 true；false=旧手写物理路径）。
- `src/NativeBody.cs`（新增）：
  - Harmony 前缀挂在 `PR.runPre`：骑士模式下（CombatGuard 已把 L/R/T 原生输入锁死），
    把小骑士的方向键翻译成诺艾尔 `simulate_key` 的 L/R 位；
  - 原生接管期间保证 `M2Phys` 不暂停，由 AIC 原生 runPre/runPhysics 完成
    移动、斜坡贴地、单向平台与墙角碰撞；
  - 接管时把 `M2MoverPr.walkSpeed` 临时覆盖为骑士走速 0.1（退出时还原）；
  - 事件/转房期间（`EV.isActive`）一律让路，不覆盖脚本的 simulate_key。
- `KnightEntity.cs`：
  - 新增 `IsNativeLocomotionEligible(bool hasNativeFoot)`：只有“贴地/走/停”
    这类普通状态可被原生接管；冲刺/超冲/下砸/施法/凝聚/坐椅/攀墙/空中动作等
    复杂状态继续走旧路径；
  - 新增 `SyncFromHost(PRNoel)`：读回诺艾尔位置（诺艾尔=物理真身）。
- `KnightInCradleBehaviour.cs`：
  - `SyncNoelToKnight` 增加原生分支：可接管时只做“读回”，不再 moveBy+暂停；
  - `ApplyHarmony` 注册 `NativeBody.Apply`；
  - `DeactivateKnightMode` 强制 `NativeBody.ForceDisengage`（还原走速与残留位）。

### 第二阶段（试验后已回退）：跳跃/滞空接管

曾尝试把跳跃/滞空也交给原生（`simulate_key` T 位触发原生跳跃），实测发现：

- 原生跳跃落地后 `FootD.hasFoot()/canJump` 不再自动恢复（引擎足部生命周期与
  “暂停+moveBy”接管方式冲突），导致地面按跳被误判成空中二段跳、时跳时不跳；
- 强制挂地/看门狗补丁会与骑士读回互相拉扯，出现地面上下抽搐。

结论：**当前稳定基线为“原生只管贴地行走，跳跃/滞空仍走骑士旧路径”**。
原生跳跃的落地接脚问题需要专项方案（例如让原生体自然下落一段距离再接管，
或同步骑士与诺艾尔的跳高/重力曲线）后另行引入。

### 第一阶段边界（尚未迁移到原生）

跳跃/二段跳/蹬墙跳、冲刺/暗影冲刺、水晶之心、下砸、法术、凝聚、坐长椅、
攀墙、游泳等仍走骑士手写物理 + 旧“拖拽同步”。若这些状态内仍出现穿模，
需要在下一阶段逐状态迁移到原生（每个状态的迁移点：`KnightEntity.Update`
对应状态段 → 改为写 `M2Phys` 速度/位移接口，再从诺艾尔读回）。

### 验证方法

1. 确认 `BepInEx/config/dev.KnightInCradle.cfg` 里 `NativeBodyMode = true`；
2. 骑士模式下在房间出入口斜坡上来回走、在墙角走位，观察：
   - 是否不再被卡回原房间；
   - 是否不再嵌入墙体/掉出地图；
3. 需要回退时把 `NativeBodyMode` 改为 false 重启游戏，即恢复旧行为；
4. 观察是否出现新的副作用（例如移动手感、进门触发时机变化），记录
   `BepInEx/LogOutput.log` 后反馈，再决定是否扩大原生接管状态集合。

---

## 9. 联机拼刀（PvP 骨钉对拼）实施记录（2026-09-16）

> 目的：两名小骑士互相战斗时，双方的普攻/骨钉技艺判定箱（剑气）相交要触发“拼刀”
> —— 双方冻结 0.3 秒（动画/特效/键位/位移） + 各获得 0.5 秒无敌 + 弹刀音效一次，
> 连续拼刀刷新无敌；同一段交叠只结算一次（不刷音效）。
>
> 修订记录（2026-09-16 二稿）：一稿是“1 秒无敌 + 0.1 秒冷却”，实测拼刀会连播好几遍音效
> —— 冷却只有 0.1 秒，而骨钉技艺的判定箱能持续 0.4~1.2 秒，交叠期间每 0.1 秒就重触发一次。
> 二稿改为“0.3 秒冻结 + 交叠闩锁”：冻结期间根本不判定，同一段交叠也只结算一次，
> 因此每次拼刀只响一次；两次分开的拼刀各响一次。

### 9.1 规则（与需求一一对应）

| 需求 | 实现 |
|---|---|
| 只在“普攻或骨钉技艺”时生效 | 采集普攻（平砍/上劈/下劈，命中窗口 0.08s）与三种骨钉技艺（强力/冲刺/旋风劈砍）的判定箱；法术、梦钉、下砸骨剑不参与 |
| 两者剑气碰撞箱有重叠即触发 | 远端判定箱随联机负载同步 → 本地做“轴对齐矩形相交”判定 |
| 暂停两个小骑士的动画、特效、键位 0.3 秒 | `_nailParryFreezeTimer = 0.3f`；`KnightEntity.Update` 在该计时器 >0 时于“输入/物理/剪辑/特效推进”之前直接 return（渲染票据按当前状态绘制 → 定格），两端各自冻结自己 |
| 双方各获得 0.5 秒无敌 | 两端各自本地判定、各自给自己 0.5 秒无敌（`_invincibleTimer`），无需额外发包 |
| 播放与劈中尖刺/荆棘相同的音效（且只播一次） | `DashAudio.PlaySpikePogo()`（HK `sword_hit_reject`，与陷阱弹刀同一条音频）；用 `_nailParryLatched` 闩锁保证“一段交叠只响一次” |
| 连续拼刀重新计算无敌 | `_invincibleTimer = Mathf.Max(_invincibleTimer, 0.5f)`，即刷新回 0.5 秒 |
| 不要连续播好几遍音效 | ① 冻结期间不判定（天然 0.3 秒最小间隔）；② 交叠闩锁：判定到相交时置位，直到“出现一帧不相交”才解除，持续交叠不再触发 |

### 9.2 改动的文件

| 文件 | 改动 |
|---|---|
| `src/KnightEntity.cs` | 新增拼刀区段：`UpdateNailParry / TriggerNailParry / CollectNailAttackRects / TryCollidersToCellRect / TryWorldRectToCellRect / AddNailArtCellRect`；`Update` 在普攻/技艺推进之后调用 `UpdateNailParry()`；特效负载追加骨钉判定箱；新增 `CenterToFootY` 供联机侧反推骑士中心；新增 `NailParryDebug` 线框调试票据（Rebind/Release 成对） |
| `src/MultiplayerCompat.cs` | 解析负载 v5 段的骨钉判定箱 → `RemoteNailRects`（绝对格坐标）；`OverlapsRemoteNailRect` 供本地判定；`RemoteNailRectCount / ForEachRemoteNailRect` 供诊断与调试绘制 |
| `src/KnightFxSync.cs` | 负载新增 v5 段“骨钉攻击判定箱”（每帧随 `PlayerInfo.shieldData` 发送）；`Read/ReadDive` 兼容 v3/v4/v5，`ReadAttack` 仅 v5 |
| `tools/FxSyncTest/`（新增） | 负载编解码回归测试（不参与插件编译）：`dotnet run -c Release -p tools/FxSyncTest/FxSyncTest.csproj` |

### 9.3 判定箱怎么算（口径统一是重点）

- **普攻**：直接读真实碰撞体（BoxCollider2D 矩形 + PolygonCollider2D 尖端）的**世界包围盒**
  —— 与 `AttackHitboxDebug` 绿框同源，含护符 18/19 的长度/高度/拉伸修正。
- **骨钉技艺**：与 `CheckNailArtHit / CheckDashSlashHit / CheckCycloneHit` 同源
  —— 中心用“格”常量（`NailArtHitboxOffX` 等），尺寸用“世界单位”常量（直接传
  `Physics2D.OverlapBoxAll` 的那一套）。
- **统一成地图格坐标**：世界点先 `mapT.InverseTransformPoint` → 地图本地 ux，
  再用 `Map2d.uxToMapx / uyToMapy` 转格。**不对“1 世界单位 = 几格”做任何假设**，
  地图根节点带缩放/偏移时同样成立（这点很关键：NailArt 的尺寸常量与
  HitboxOffsetX 这类 ux 偏移常量单位并不一致，硬算会差 64/CLEN ≈ 2.286 倍）。
- 换算后的矩形即 `(cx, cy, w, h)`（绝对格坐标，y 向下为正）。

### 9.4 联机同步（复用已有的特效通道，不新增包）

负载（`PlayerInfo.shieldData`，见 `KnightFxSync`）在 v4 的“下砸骨剑/尖刺”段之后追加：

```
[v5] byte 数量(≤6)
     每条 9 字节：int16 dx, dy, w, h（格 × 1024，相对“骑士中心”）+ byte 招式类别(1普攻/2强力/3冲刺/4旋风)
```

- 版本号策略：**只有真的带着骨钉判定箱时才写 5**，平时仍是 v4，旧版本客户端在非攻击帧
  不受影响；一旦本端进入攻击帧，旧版本客户端会读不懂该帧负载（表现为该帧远端特效/下砸
  条目不同步），因此**两端需要同版本**（与“两端素材必须一致”是同一要求）。
- 远端还原锚点：广播的 `x` 是诺艾尔中心（= 骑士中心 + `HurtCenterX`）、
  `ay` 是骑士脚底（`KnightEntity.FootY`，由 `CreatePlayerInfoPostfix` 覆盖写入），
  于是 `远端骑士中心 = (x - HurtCenterX, ay - CenterToFootY)` —— 与发送端的 `(X, Y)` 同口径。
- 收集时机：只在 `DrawRemoteKnight`（远端骑士被绘制）时解析，天然限定“同一张地图”的玩家；
  数据按帧戳保留 5 帧（与既有下砸条目同一策略），用于吸收网络抖动。
- 触发时机：`KnightEntity.Update` 中普攻/技艺判定推进之后，每帧最多判定一次；
  冻结期间直接跳过；已闩锁时不重算、不重播音。

### 9.4.1 冻结（0.3 秒）是怎么实现的

`KnightEntity.Update` 很长，输入/物理/状态机/剪辑/特效推进集中在后半段。拼刀冻结利用这个结构：
把冻结判定插在“按真实时间推进的计时器”（受伤无敌、受击黑边、回血闪光、生命血闪光）之后、
死亡处理与其余逻辑之前，命中冻结就 `return`：

- 因此**输入不再采集**（按了也没反应，`KeyConfig.GetPressed` 走 Unity 帧级按键，冻结帧的事件直接丢掉）；
- **物理不再推进**（Vx/Vy 不结算 → 空中拼刀会“停在空中”定格）；
- **剪辑与特效计时不再推进**（`PlayClip` 的帧计时、`PlayFxClip` 的剑气帧、各类粒子/残影全部停住，
  渲染票据仍按当前状态绘制 → 画面就是定格）；
- 普攻/骨钉技艺的 `_attackTimer / _hitboxActiveTimer / _nailArtSlashTimer` 等也一并暂停，
  所以冻结结束后招式从原处继续，不会被冻结吃掉持续时间；
- 换图/过图/快速旅行那套“地图切换 + 重定位”在冻结判定**之前**执行，因此冻结不会卡住转房与出行；
- 无敌计时在冻结之前按 `Time.unscaledDeltaTime` 推进，所以“0.5 秒无敌”不会被 0.3 秒冻结拖长。

注意：0.3 秒冻结与 0.5 秒无敌都由**各端各自本地执行**（判定箱同步是每帧的，两端通常在同一帧
前后触发），因此两边的定格与无敌在观感上同步。
护符召唤物（孢子云 / 编织者 / 格林之子 / 梦之盾）与蜂巢回血等**每帧维护逻辑**刻意放在冻结
判定之前，冻结期间照常推进 —— 它们不是拼刀本体的动画/特效，而且其中一部分是敌人索敌/AI 的
每帧清理，停掉反而会出怪问题。若确实要求“连召唤物一起定格”，需要把这些每帧维护拆成
“必须每帧”和“纯表现”两类再分别处理。

### 9.5 已知边界与后续可调项

1. 两端各自本地判定，理论上存在 1~3 帧的时间差：若“对方伤害包”先于“对方判定箱”到达，
   这一刀就按普通命中处理（会正常受击/打断），不会补触发拼刀。若实测这种抢跑太多，
   有两种加固方向：① 在 `KnightTakeDamage` 里补“被远端小骑士命中且自己正在骨钉招式内 →
   视作拼刀”的兜底；② 在本端负载里再带一个“本帧拼刀过”的标志位，让对端即使自己没判定到
   也能进无敌（代价是要处理重复音效，靠冷却去重）。
2. 远端判定箱在停止攻击后最多保留 5 帧，因此极短窗口内可能对“刚收招的远端”判定成功。
   想更严格可把 `OverlapsRemoteNailRect / RemoteNailRectCount` 的 `5` 调小（代价是抗抖动变差）。
3. 无敌是“刷新为 0.5 秒”而不是叠加；如果希望拼刀同时清硬直/打断对方招式，需要另加规则。
4. 时长都写死为常量（`NailParryFreezeTime = 0.3f`、`NailParryInvincibleTime = 0.5f`），
   若要进设置界面，按 `Plugin.cs` 的 ConfigEntry 模式再加。
5. 交叠闩锁的解除条件是“有一帧两层判定箱都不相交”。极端情况：两端贴脸且都把判定箱
   长时间压在对方身上（例如两人同时一直按住蓄力再连发旋风），第二次起只在真正分开后才响。
6. 冻结只作用于小骑士本体，**不会**冻结敌人的行动与出手（敌人仍然会打你，靠 0.5 秒无敌顶）。
   如果希望拼刀也给敌人“定身”，需要在 `Map2d`/`NelEnemy` 层另做时间暂停。

### 9.6 验证方法

0. 改过负载布局后先跑 `dotnet run -c Release -p tools/FxSyncTest/FxSyncTest.csproj`
   （13+ 项断言，覆盖 v4/v5 新旧格式、混装段、空负载、越界钳位）；
1. 两端都装同一版 `KnightInCradle.dll`（本仓库构建产物已部署到
   `…/AICmultiplayer/AliceInCradle Win ver030/…/BepInEx/plugins/KnightInCradle/`，旧版留作 `*.prev`）；
2. 两名玩家都按 T 切成小骑士，贴脸同时挥砍 → 应听到“叮”（与下劈尖刺同款）且双方 1 秒内不掉血；
3. 连续对拼（0.1 秒内重复触发）时无敌应刷新，而不是叠加/失效；
4. 想看判定箱：把 `KnightEntity.NailParryDebug` 改为 `true` 重新编译 → 绿框=本地骨钉判定箱，
   红框=同步过来的远端判定箱，两框相交时应正好触发拼刀（与
   `BepInEx/LogOutput.log` 里的 `[KIC][拼刀] 触发` 日志对照）；
5. 音效只响一次：两人贴脸用骨钉技艺（判定箱持续时间长）、或一人按下普攻一人用技艺，
   每次拼刀应只响一次“叮”，并且定格 0.3 秒；分开后再拼一次会再响一次。
   对数方法：`LogOutput.log` 里每次拼刀各出一行 `[KIC][拼刀] 触发 frame=...`，
   同一段交叠如果出现多行（间隔仅几帧），说明闩锁没能挡住重复触发，需要回看判定箱日志；
6. 记录日志反馈实际对拼手感（定格是否合适、是否抢跑）再决定是否微调时长与容差。

---

## 10. 判定框绿框调试 + 深渊尖啸五段伤害修复（2026-09-17）

### 10.1 三大骨钉技艺判定箱绿框

开关（`KnightEntity` 内，默认已开）：

| 开关 | 对应招式 | 判定箱常量 | 判定函数 |
|---|---|---|---|
| `NailArtHitboxDebug` | 强力劈砍（蓄力斩） | `NailArtHitboxW/H/OffX/OffY` | `CheckNailArtHit` |
| `DashSlashHitboxDebug` | 冲刺劈砍 | `DashSlashHitboxW/H/OffX/OffY` | `CheckDashSlashHit` |
| `CycloneHitboxDebug`（新增） | 旋风劈砍 | `CycloneSlashHitboxW/H/OffY` | `CheckCycloneHit` |

绘制方式改成了**与 strategy.cs 方法论一致的一条换算链**，而不是“常量 × CLEN”：

```
判定箱中心（格）──CellPosToWorld──▶ 世界点（与 Physics2D 查询的 TransformPoint 同源）
判定箱尺寸（世界单位，直接传 Physics2D）──▶ 世界矩形的两个角
世界角点 ──mapT.InverseTransformPoint→ux──Map2d.uxToMapx/uyToMapy→格──DrawDbgRect→绿框
```

为什么必须走这条链：骨钉技艺的**中心常量是“格”**（`X + dir × OffX` 直接加在格坐标上），
而**尺寸常量是“世界单位”**（直接传给 `Physics2D.OverlapBoxAll`），两者口径不同。
如果绿框把尺寸也按“格 × CLEN”画，屏幕上会与实际判定箱差一个 `64/CLEN ≈ 2.286` 倍。
新写法用运行时换算（`DrawDebugWorldRect` / `CellPosToWorld` / `SetDebugAnchorMatrix`），
不管世界单位到底是格还是 ux，绿框都与真实判定严格重合。

### 10.2 暗影之魂（冲击波）判定箱绿框

- 开关 `FireballHitboxDebug`（默认已开），票据 `_fireballDbgTicket` / `KnightPrepareFireballDebugMesh`。
- 每发冲击波各画一个绿框：中心 = `proj.X + FireballHitboxOffX, proj.Y + FireballHitboxOffY`（格），
  尺寸 = `FireballHitboxW/H`（世界单位）→ 同 10.1 的换算链，与 `CheckFireballHit`
  的 `Physics2D.OverlapBoxAll(center, new Vector2(FireballHitboxW, FireballHitboxH), ...)` 完全同源。
- 同一张票据还兼作“，”键的坐标轴/刻度叠加层（`ShowKnightAxes`），两者可同时显示。
- 绿框只在冲击波存活期间绘制（`_fireballs` 列表内）。

### 10.3 深渊尖啸只有一段伤害：原因与修复

**预期**：`ScreamTickCount = 5`，每 0.1 秒一段（第 0.15/0.25/0.35/0.45/0.55 秒），单段 40 伤害。

**查到的原因（两条都改掉了）**：

1. **判定查询方式与其它招式不一致**：尖啸原来是“三角 `PolygonCollider2D` + 圆 `CircleCollider2D`
   挂在临时物体上，再用 `Physics2D.OverlapCollider(collider, ContactFilter2D, results)` 查询”。
   而 `ContactFilter2D` 的 `useTriggers` 默认为 `false`（不返回 Trigger 碰撞体），
   并且该查询依赖“每段才移动一次的查询物体”，形状/位置同步时机不受模组控制
   （引擎此时是 `SimulationMode2D.Script`）。这两点都可能让某几段查不到目标。
2. **通用可攻击目标只结算一段**：`TryScreamDamage` 里对非 `NelEnemy` 目标走的是
   `TryHitGenericAttackable(c, dmg, _screamGenericHits)` —— 这个 `HashSet` 是“整段招式只命中一次”的
   去重集合，于是**靶子（的当て靶）/拳炮/TD 路障/联机远端小骑士代理**这类目标一次尖啸只掉一段血。

**修复**：

- 判定改为与本模组其它招式同源的静态查询：
  三角区用 6 层水平切片精确覆盖（`Physics2D.OverlapBoxAll`，层高 ×1.05 防缝隙），
  圆形区用 `Physics2D.OverlapCircleAll`；偏移/半径按世界单位沿 `+y`（上方）叠加，
  与旧碰撞体形状严格等价（形状定义见 `ScreamTriHeight/HalfWidth`、`ScreamCircleOffsetY/Radius`）。
  判定物体（`KnightScreamHitbox` / 两个 Collider）随之删除，`Ensure/DestroyScreamHitbox`
  保留为空实现以兼容既有清理调用点。
- 每段对**每个目标只结算一次**（`_screamTickTargets` 单段去重，多碰撞体不重复吃同一段）；
  每目标一次尖啸最多 5 段（`_screamHitCounts`），靶/拳炮/远端代理同样按段计数。
- 尖啸结算走 `ApplyDiveDamage(..., suppress_pop: true)`：把 `Atk.burst_vy` 设为极小非 0 值，
  跳过 `NelEnemy.applyDamage` 引擎默认的 `-0.1~-0.26` 受击上弹，避免多段驻留期间目标被弹出判定范围。
  （下砸骨剑/尖刺保持原行为，只有尖啸传 `true`。）
- 新增诊断日志（每次尖啸一行，结束时报）：
  `[KIC][尖啸] 结束：判定 5 段（配置 5 段），实际结算 N 段，命中目标 K 个`
  —— `实际结算` 就是本次真正打出的伤害段数（同一目标的多段累加），`命中目标` 是不同目标个数。

### 10.4 验证方法

1. 三个骨钉技艺各放一次：强力劈砍 → 绿框应出现在脸前方、与剑气弧光大致对齐；
   冲刺劈砍 → 绿框随冲刺方向；旋风劈砍 → 以骑士为中心左右各 4 格、高 2 格。
   用绿框核对“打不到的地方”是否真的在框外（这轮微调就该以绿框为准）。
2. 暗影之魂：按 S 放出冲击波，每发都应有一个绿框跟着飞行；命中/擦过敌人都应以绿框范围为准。
3. 深渊尖啸（下 + 上一起按）对着敌人/靶子各放一次，看日志：
   - 正常应打出 `判定 5 段，实际结算 5 段`（打靶子时目标是 1 个）；
   - 若目标中途被击退/死亡导致离开范围，实际结算会小于 5，日志会如实反映，
    这时把日志发回来，再决定是否需要进一步收紧击退或扩大判定。

---

## 11. 骨钉技艺对靶子多段伤害 + 宿主碰撞箱尺寸（2026-09-17 二稿）

### 11.1 强力劈砍 / 旋风劈砍对靶子只掉一段血

**原因**：`CheckNailArtHit` / `CheckCycloneHit` 把目标分成三类走不同分支：

- `NelEnemy`（怪）→ 按“段”结算（强力劈砍：进箱 1 段 + 每 0.15s 追加，最多 `NailArtTickCount`=3 段；
  旋风劈砍：`CheckCycloneHit` 每一转调用一次 → 每一转一段）；
- 联机远端代理（`M2Gunmu`）→ 也是按段结算（之前单独写过分支）；
- **其它通用可攻击目标**（的当て靶 / 拳炮 / TD路障 …）→ 走了
  `TryHitGenericAttackable(c, dmg, _nailArtGenericHits / _cycloneGenericHits)`，
  而这两个 `HashSet` 的语义是“**整段招式只命中一次**” —— 所以靶子只吃到第一段。

**修复**：通用目标改用与怪/远端代理**完全相同**的按段逻辑：

- 强力劈砍：`_nailArtHits` / `_nailArtDwell` / `_nailArtInsideNow` 计数（进箱 1 段 + 驻留追加，最多 3 段），
  伤害结算走新增的 `ApplyNailArtGenericHit(c, ga, dmg)`：远端代理仍走 `ApplyProxyNailHit`（伤害包伪装），
  其余目标走 `TryHitGenericAttackable(..., dedup: null)`（不再整段去重）。
- 旋风劈砍：每一转都结算一段；新增 `_cycloneTickTargets` 做“**同一转内**”去重
  （同一目标有多个碰撞体时不会在一转里重复结算），与远端代理分支共用同一套判断。
- `_nailArtGenericHits` / `_cycloneGenericHits` 两个“整段去重”字段已删除（不再有这种语义）。

期望结果（打靶子）：强力劈砍 42×3 段、旋风劈砍每转 40 一段（基础 3 转，按攻击键最多延长到 6 转）。

### 11.2 切成小骑士后宿主碰撞箱还是诺艾尔大小

**原因**：宿主（诺艾尔）的碰撞箱尺寸由游戏自己的 `PR.setBounds()` 决定 ——
正常 12×68 像素、蹲伏 12×40、倒地 70×20、受击 12×24 ……。而以下时机都会重新走一遍 setBounds：

- `PR.autoCheckBounds()`（`need_check_bounds` 由 `PrAnimator` 在**姿势变化**时置位、`M2PrADmg` 在受击时置位）；
- 蹲伏/倒地/闪避/被骑乘等状态切换；

于是“切人时改一次尺寸”会被游戏随后写回诺艾尔尺寸，表现为碰撞箱（受击箱/卡墙箱）仍是诺艾尔大小。

**修复（两道保险）**：

1. `CombatGuard` 新增 `PR.setBounds(BOUNDS_TYPE, bool, bool)` 后缀补丁 → 游戏刚写完尺寸立刻拉回骑士尺寸。
   注意 `BOUNDS_TYPE` 是 `M2MoverPr` 里的**受保护嵌套枚举**，模组代码不能直接引用类型，
   所以用 `FindPrSetBounds()` 反射取方法（先 `m2d.M2MoverPr+BOUNDS_TYPE`，
   再按“名字 + 3 参数 + 首参类型名 BOUNDS_TYPE”兜底）；该查找已在正式程序集上离线验证通过
   （能取到 `Boolean setBounds(BOUNDS_TYPE, Boolean, Boolean)`）。
2. `KnightInCradleBehaviour.EnforceKnightBodySize(pr)`：在 `SyncNoelToKnight`（Update + LateUpdate 各一次）
   与切人时各校验一次，只在尺寸不符时写回并 `getColliderCreator().fineRecreate()`
   —— 幂等、不会每帧重建碰撞体。切回诺艾尔时 `KnightModeActive` 已为 false，两处保险都不再干预。

**验证**（已按 11.3 的“只收紧不放大”修正）：切小骑士后，站着时 `pr.sizex/sizey`
应被限制在 `≤ KnightEntity.HurtSizeX/HurtSizeY`（≈ 0.2522 / 0.535，实际宽度取诺艾尔的 0.2143），
不会回到 (0.214, 1.214)（= 12×68 像素）；游戏压身时可以更小；切回诺艾尔后恢复原尺寸。

### 11.3 修正（同日）：碰撞箱只“收紧”，不“钉死”

**问题**：11.2 的第一版把尺寸**每帧强行写回固定值**（0.2522 / 0.535），结果小骑士过不去一些
以前能过的狭窄/低矮区域 —— 因为 AIC 让玩家钻进矮洞、窄缝靠的正是游戏自己临时把碰撞箱压小：
`CROUCH` 12×40 像素、`PRESSCROUCH` 12×10 像素（连 `DOWN` 都会换成 70×20），
而每帧钉死会把这种“临时压小”立刻撤销，等于把游戏的挤过机制一起封掉了。

另外旧版宽度取的是 `HurtSizeX`（半宽 0.2522 → 全宽约 14.1 像素），比诺艾尔自己的 12 像素**更宽**，
而 AIC 地图里确实存在半格（≈14 像素）级别的窄缝 —— 诺艾尔 12 像素能过、14.1 像素过不去。

**修正**：改成**只收紧、不放大**，取两者较小值：

```csharp
pr.sizex = Mathf.Min(pr.sizex, KnightEntity.HurtSizeX);
pr.sizey = Mathf.Min(pr.sizey, KnightEntity.HurtSizeY);
// 只有真的变了才 getColliderCreator().fineRecreate()（幂等，不会每帧重建）
```

- 站着（游戏给 12×68）→ 限制成 12×30 像素：受击箱不再有诺艾尔那么高 ✓（11.1 的目标仍满足），
  宽度也不超过诺艾尔的 12 像素 ✓（窄缝通过性不再退化）；
- 游戏要求压身时（40 / 20 / 10 像素）→ 因为我们取 `min`，**依然能继续缩小** ✓
  所以“诺艾尔能挤过去的地方小骑士也能挤过去”；
- `CCON.canStand()` 只看地图格配置、不看体型，所以是否触发蹲伏/压身完全由游戏状态机决定，
  我们只负责“不让它把箱子放回诺艾尔那么大”。

**新增调试**：`HurtBoxDebug = true`（默认开）。现在这张票据同时画两个框：

- **绿框** = 小骑士期望受击箱（`CollideX0/1`、`CollideY0/1`）；
- **橙框** = 宿主诺艾尔身上**真正参与物理/受击**的那个碰撞体（`pr.getColliderCreator().Cld`）。

排查“卡在某处过不去”时看这两个框：正常情况下两框应基本重合（橙框可能略小，说明正被游戏压身）；
若橙框明显比绿框大，说明尺寸限制没生效；若橙框位置不对（比如底边不在脚底），
那是宿主对齐（`SyncNoelToKnight` 的脚底对齐）出了问题。

### 11.4 修正二（同日，实测“仍然不行/碰撞箱仍是诺艾尔”后）：从源头截断尺寸

11.3 的“每帧事后改字段”在这台机器上没能生效（实测碰撞箱还是 12×68 像素），
原因是**尺寸写入有多个入口**（`PR.setBounds` 只是其一，另有 `AlicePVV200` 等直接调 `Size`），
而事后改字段还会造成“字段/碰撞体/mbottom”口径错位。这轮改成在**唯一源头**上做上限：

| 钩子 | 作用 |
|---|---|
| `M2Mover.Size(float,float,ALIGN,ALIGNY,bool)` **前缀** | 所有体型写入的唯一入口（12×68 / 蹲伏 12×40 / 压身 12×10 都走它）。骑士模式下把入参（像素）夹到 `≤ 小骑士尺寸 × CLENM`，并且写在**参数上**，所以 `Size()` 内部的位移（ALIGNY.BOTTOM 保持底边）也基于夹过的值计算，字段/碰撞体/mbottom 完全一致 |
| `M2MvColliderCreatorAtk.recreateExecute()` **前缀**（兜底） | 真正重建碰撞体之前，把宿主的 `sizex/sizey` 再夹一次（并写回字段）——即使有代码绕过 `M2Mover.Size` 直接写字段，**真正参与物理/受击的碰撞体也不可能变成诺艾尔体型** |
| `PR.setBounds(BOUNDS_TYPE,bool,bool)` **后缀**（保留） | 状态切换后立刻夹一次 |
| `EnforceKnightBodySize`（每帧，保留） | Update + LateUpdate 各一次兜底 |

以上三处都遵循同一语义：**只收紧、不放大**（`min(游戏值, 小骑士尺寸)`），所以游戏为钻矮洞
而临时压小（40 / 20 / 10 像素）依然有效；站着时被压到约 12×30 像素。

另外把宿主脚底对齐改用**实际生效半高**（`min(pr.sizey, HurtSizeY)`），
这样即使有代码在别处把 `sizey` 写回大值，宿主的碰撞箱底边也始终贴在小骑士脚底。

三个补丁目标已在正式程序集上离线核验存在：
`PR.setBounds(BOUNDS_TYPE,Boolean,Boolean)`、`M2Mover.Size(Single,Single,ALIGN,ALIGNY,Boolean)`、
`M2MvColliderCreatorAtk.recreateExecute()` ✓。

#### 11.4.1 新增开关与诊断

- **配置** `General/ResizeHostToKnight`（默认 `true`）：置 `false` 就完全不限制宿主碰撞箱
  （等于诺艾尔原版体型），用于 A/B 判断“卡窄处”到底是不是体型限制造成的。
- **诊断日志**：骑士模式下每 2 秒一行
  `[KIC][宿主体型] 骑士模式=True sizex=… sizey=… (上限 …) 实际碰撞体=…×… 像素（…×… 格）`
  —— 这行同时能确认“新 DLL 是否真的加载了”（没有这行说明游戏加载的不是这份 DLL）。
  判读方式：
  - `sizex/sizey` 已被限制、`实际碰撞体` 也 ≈ 12×30 像素 → 体型限制生效，卡住的原因不在碰撞箱；
  - `实际碰撞体` 仍是 12×68 像素 → 还有代码在重建碰撞体（把日志发回来继续查）；
  - `骑士模式=False` → 根本没进骑士模式（切人失败）。

### 11.5 修正三（同日，实测“受击箱已是小骑士、但地形互动仍按诺艾尔”）：游戏内部有 **三套** 体型数据

实测确认：碰撞箱（受击判定）已经成功限制成小骑士，但“与地形/地图互动”仍按诺艾尔算。
排查后确认游戏内部同时存在三套彼此独立的体型数据，之前只改了第 1 套：

| # | 数据 | 出处 | 用途 | 之前状态 |
|---|---|---|---|---|
| 1 | `sizex/sizey` → `CC.Cld`（PolygonCollider2D） | `M2Mover.Size` / `M2MvColliderCreatorAtk` | 被攻击命中、物理碰撞 | 已在 11.4 限制成小骑士 ✓ |
| 2 | **`event_sizex / event_sizey / event_cy`** | `PR.cs:7277~7303` **硬编码**：12 像素半宽 / 68 像素半高 / 中心在脚底上方 68 像素 | `M2EventContainer` 拼“事件矩形”、`M2MoverPr.checkCurrentPoint` 取“当前事件格” → **门/出口/长椅/升降台/传送/NPC/狭窄处的地图互动判定** | 仍是诺艾尔 ✗ |
| 3 | **`size_y_default_pixel`** | `PR.cs:146` **硬编码 68** | `M2MoverPr.checkForceCrouch` 判断“这一格能不能站起来 / 要不要强制蹲伏爬行”；另用于镜头与绘制偏移 | 仍是诺艾尔 ✗ |

**修正**（CombatGuard 补丁，均在骑士模式下生效、只会变小）：

- `PR.event_sizex / event_sizey / event_cy` **后缀**：改用小骑士实际尺寸重算占位框，
  几何与原版一致（事件矩形 = `event_cy ± event_sizey`，底边贴在脚底）：
  `event_sizex = 2 × min(sizex, HurtSizeX)`、`event_sizey = 2 × min(sizey, HurtSizeY)`、
  `event_cy = mbottom − event_sizey`。
- `M2MoverPr.checkForceCrouch(int)` **前缀**：用 `min(sizey, HurtSizeY) × 2 × CLEN`（小骑士实高，约 30 像素）
  替代硬编码的 68 像素，重算“能否站立/是否需要蹲伏”，并复刻原版的三段判定逻辑；
  出错时自动退回原版逻辑。
- 11.4 的 `M2Mover.Size` 源头夹紧、`M2MvColliderCreatorAtk.recreateExecute` 兜底夹紧、每帧夹紧继续保留
  （保证第 1 套数据；且因为取 `min`，游戏压身挤过窄缝依然可用）。

**新增诊断日志**：进入“强制蹲伏”判定时打一行（状态翻转时只打一次）
`[KIC][地形判定] 强制蹲伏：cx=… 小骑士全高≈30 像素（原版按诺艾尔 68 像素判定）`
—— 如果站在某个矮洞前刷出这行、而人还是过不去，说明卡的正是第 3 套数据的残留学问，
把房间 key（F8）+ 这行日志发回来继续查。

四个补丁目标已在正式程序集上离线核验：`PR.event_sizex/event_sizey/event_cy` getter（PR 的 override）、
`M2MoverPr.checkForceCrouch(Int32)` ✓。

### 11.6 修正四（同日，实测日志后定位）：**姿势白名单把游戏自己的蹲伏/爬行取消了**

实测日志（用户提供）：

```
[KIC][宿主体型] 骑士模式=True sizex=0.2143 sizey=1.2143 (上限 0.2522/0.5350) 实际碰撞体=12.0×68.0 像素（0.43×2.43 格）
```

即**碰撞体始终是诺艾尔站立尺寸**。顺着“谁会把它写回 12×68”排查，命中真正的原因：

`KnightInCradleBehaviour.NoelPoseWhitelistListener` 注册在宿主动画器上（`fnChangePoseListener`），
原实现**无条件**把诺艾尔的姿势顶回 `stand / walk / jump`：

```csharp
if (k.Grounded) return Mathf.Abs(k.Vx) > 0.05f ? "walk" : "stand";
return "jump";
```

而 AIC 让玩家钻过矮洞/窄缝，靠的正是 `CROUCH`（12×40 像素）/ `PRESSCROUCH`（12×10 像素）姿势
+ 配套的小碰撞箱，并且 `PR.setBounds` 判断“要不要蹲 / 用什么尺寸”时会读 **Anm 的姿势**
（`poseIs(POSE_TYPE.CROUCH / DOWN)`）。姿势被顶回站立后：

1. 游戏认为“不需要蹲伏”，`bounds_` 回到 `NORMAL`；
2. `setBounds(NORMAL)` → `Size(12, 68)` → 碰撞体被写回**诺艾尔站立尺寸**；
3. 小骑士因此在需要蹲伏/爬行的地形上过不去。

这同时解释了“碰撞体为什么总是 12×68”（与我们的尺寸夹紧无关，是姿势被顶回后游戏自己写回的）。

**修正**：`NoelPoseWhitelistListener` 增加放行分支 —— 当宿主处于游戏要求的
蹲伏/爬行/倒地状态时直接返回游戏原本的姿势：

```csharp
if (IsHostCrouchOrDownPose())   // pr.is_crouch（crouching>0 或 t_force_crouch>0）
{                               // 或 bounds_ != NORMAL（CROUCH / CROUCH_WIDE / DOWN / PRESSCROUCH …）
    return pose0;               // 放行，让游戏完成蹲伏/爬行
}
```

宿主是隐藏渲染的（`HideNoelRenderTicket` + `alpha=0`），姿势外观无影响；其余情况仍按
“小骑士在地面 → stand/walk，空中 → jump”覆盖，保持原有观感逻辑。
新增日志 `[KIC][宿主姿势] 放行游戏姿势（蹲伏/爬行/倒地）：pose=…` 便于确认是否真的进入放行分支。

另外本轮还做了两件事，便于后续定位：

- **补丁体检**：启动时打一行 `[KIC][补丁] KnightInCradle build=… N 成功 / M 失败（失败：…）`，
  失败项带原因（找不到目标方法 / Harmony 异常信息）——以后“改动不生效”先看这一行。
- **夹紧体检**：`M2Mover.Size` 前缀 / `PR.setBounds` 后缀 / 碰撞体重建前缀 / 每帧夹紧，
  每次真的发生夹紧时打一行 `[KIC][体型夹紧] <哪条路径>: sizex A→B sizey C→D`；
  `[KIC][宿主体型]` 也改成“夹紧前 → 夹紧后 + 实际碰撞体”。
  同时**撤掉了 11.5 里我自己复刻的 `checkForceCrouch` 覆盖**（少一处变量，蹲伏判定交回游戏本体）。

### 11.7 修正五（同日）：**持续强制宿主蹲伏**（采纳“让小骑士一直蹲着”的思路）

11.6 放行姿势之后仍有个漏洞：**AIC 只在少数事件时才重新评估“这个格子能不能站起来”** ——
`M2MoverPr.forceCrouch()` 里的 `recheck_force_crouch` 只由 `setTo` / 受伤 / 换姿势 / 换格等
少数路径置位。玩家**走进**矮洞、窄缝时常常根本不会触发重算，于是宿主一直保持站立
（碰撞箱 12×68 像素），小骑士就过不去“诺艾尔趴下能过”的地形 —— 这正是实测日志里
“实际碰撞体=12.0×68.0”一直不消失的原因。

**做法**（`NativeBody.PrRunPrePrefix` 内，每帧、早于游戏自己的 `forceCrouch`）：

```csharp
if (!k.IsSitting && !k.IsDead && !k.IsRepositioning && k.Grounded)
{
    pr.recheckForceCrouch();        // ① 让游戏用原版算法重算蹲伏需求（含 PressCheck 的趴下/压扁）
    t_force_crouch = 60f;           // ② 顶住游戏自己的“强制蹲伏计时”，开阔处也不站起来
}
```

- 小骑士比诺艾尔矮得多，**保持蹲伏只会更容易通过**；代价是行走按 AIC 的蹲伏速度倍率略降。
- 不会阻止更矮的 `DOWN` / `PRESSCROUCH`（趴下 12×10 像素）—— 那两个由游戏自己
  （`applyPressDamage` 等）设置，`forceCrouch` 只在 `bounds_ == NORMAL` 时才改尺寸。
- 按住“下”键（默认鼠标右键）仍会转发 `SIM_B`，可以主动蹲伏/趴下钻更矮的洞。
- 开关：`General/ForceHostCrouch`（默认 `true`）。设 `false` 即回到原版（只有按住下键才蹲）。
- 日志：每 2 秒一行 `[KIC][宿主蹲伏] 强制保持蹲伏 is_crouch=… sizex=… sizey=…（全高≈… 像素）`。

### 11.8 修正六（同日）：**真正让地形判定用上小骑士体型 —— 夹紧必须在“物理之前”**

前面几轮所有夹紧（Behaviour 的 Update / LateUpdate、`M2Mover.Size` 前缀、`PR.setBounds` 后缀、
碰撞体重建前缀）都没能改变地形判定，原因出在**游戏一帧内的顺序**：

```
PR.runPre()                      // 状态/输入/边界
   └─ base.runPre()              // 脚部/贴地处理（用上一帧结束时的碰撞体）
   └─ autoCheckBounds() / forceCrouch()   // ★ 在这里把尺寸写回诺艾尔站立 12×68
PR.runPhysics(fcnt)              // ★ 真正做地形/墙/台阶碰撞（用当前 Collider 形状）
```

模组原来的夹紧要么在 `runPre` 之前（Behaviour.Update）、要么在它之后（LateUpdate）——
**物理拿到的永远是游戏刚写回的诺艾尔站立尺寸**。这解释了为什么“我们自己画的绿框是小骑士的，
但地图判定仍是诺艾尔”，也解释了日志里碰撞体一直显示 12.0×68.0。

**修正**：把夹紧挂到 `PR.runPre` 的**后缀**（`NativeBody.PrRunPrePostfix`）——
时机正好是“`autoCheckBounds` 写完尺寸之后、`runPhysics` 之前”：

```csharp
float bw = pr.sizex, bh = pr.sizey;      // 记下游戏刚写入的诺艾尔尺寸
string before = MeasureBodyPixels(pr);   // 以及碰撞体的实际像素尺寸
KnightInCradleBehaviour.EnforceKnightBodySize(pr);   // 夹到小骑士尺寸 + fineRecreate()
// 1 秒一行：[KIC][物理前体型] 夹紧前 A/B 碰撞体=… → 夹紧后 … 碰撞体=…
```

从这一版起，**撞墙/钻洞/台阶判定用的都是小骑士的碰撞箱**（位置仍由 `SyncNoelToKnight`
把宿主脚底对齐到小骑士脚底，因此“大小和位置”都与小骑士一致），切换回诺艾尔时
`ResizeNoelToKnight(pr,false)` 照旧复原原始尺寸。

**日志速查**（骑士模式下每 1 秒一行，用来确认生效）：

```
[KIC][物理前体型] 夹紧前 0.2143/1.2143 碰撞体=12.0×68.0 像素 → 夹紧后 0.2143/0.5350 碰撞体=12.0×30.0 像素 is_crouch=True
```

若“夹紧后”已经不是 30 像素，或者这一行完全不出现，说明加载的不是本 DLL（`[KIC][补丁]` 那行可佐证）。

### 11.9 修正七（同日）：可调上限 + “卡在哪一层”的诊断

针对“某些狭窄地形仍然过不去”，补两个能直接定位/绕过的手柄：

1. **碰撞箱上限可调**（`General` 分组）
   - `HostColliderWidthPixels`（默认 12，整宽像素）= 默认与诺艾尔同宽；
   - `HostColliderHeightPixels`（默认 30，整高像素）≈ 小骑士身高（1.07 格）。

   所有夹紧（每帧、物理前、碰撞体重建）都改用这两个上限，`min(游戏值, 上限)`。
   遇到特别窄/特别矮的缝，可以把它调到例如 `10 / 20` 再试 —— 这是最直接的验证手段：
   如果调小后能过，就说明卡的是宿主碰撞箱；如果调小到很小仍然过不去，说明卡的是别的层。
2. **阻塞诊断**：骑士模式下若**不在原生接管路径**（即走旧“拖拽同步”= 骑士手写物理）
   且玩家按住方向 0.35 秒位移≈0，会打印一行
   `[KIC][阻塞诊断] 非原生路径（骑士手写物理）按住方向 0.35 秒位移=… 骑士位置=… 宿主 sizex/sizey=… 碰撞体=… is_crouch=… 房间=…`
   —— 出现本行说明卡的是**骑士自己的物理/状态**（原生未接管），不出现则卡的才是宿主碰撞箱。

> 另外确认：联机/技能同步那部分（`MultiplayerCompat` 只补 `M2Gunmu.applyHpDamage`、
> `createPlayerInfo`、`render1`/`renderHpMpBar`；`KnightFxSync` 只是把特效/判定箱打进
> `PlayerInfo.shieldData`）**不碰地形相关的任何东西**，与“卡地形”没有因果关系。
> 地形判定用的是宿主（诺艾尔）的 Collider 与游戏自己的碰撞/边界逻辑，
> 这块从来是“诺艾尔体型”，所以那些窄缝对原本的小骑士也一样过不去。

### 11.10 修正八（同日）：`forest_ahletic_home_thorn` —— 趴下区域，默认不再覆盖宿主姿势

实测指向的具体房间：`forest_ahletic_home_thorn`（有一则教程教玩家“诺艾尔可以趴下通过狭窄区域”）。
这类区域的动作链**完全由诺艾尔自己的姿势/状态机推进**：

```
按住下 → 游戏请求趴下姿势（down / down_u / press…）→ autoCheckBounds 看到 poseIs(DOWN)
      → setBounds(DOWN)（70×20 像素）或 PRESS（12×10 像素）→ 钻过去
```

而模组的姿势白名单会把这个请求**顶回 stand**（即使 11.6 已经放行了“蹲伏类姿势名”，
一旦请求的姿势名/时序不匹配就仍然会被覆盖），于是游戏永远进不了趴下状态
→ 碰撞箱也就一直是站立尺寸 → 过不去。

**修正**：

- `General/HostPoseOverride`（默认 **false**）：默认**完全不动宿主的姿势**。
  宿主是隐藏渲染的（`HideNoelRenderTicket` + `alpha=0`），姿势只影响游戏自己的状态机
  （蹲伏/爬行/趴下/压扁/爬梯/游泳），交给游戏最稳。
  `true` 可恢复旧行为（只在非蹲伏类姿势时把宿主姿势对齐小骑士）。
- 姿势监听里额外增加“玩家按住下键时一律放行”（等于玩家主动要求蹲/趴，教程教的正是这个）。

配合 11.8 的“物理前夹紧”与 11.3~11.7 的尺寸限制，趴下时宿主碰撞箱 = `min(70×20, 上限)`
= 12×20 像素（被压扁时 12×10），比原版趴下更窄更矮，因此该区域应该能直接钻过去。

### 11.11 联机适配（同日）：小骑士攻击诺艾尔的受击反馈

伤害链路稳定后，补上“小骑士打诺艾尔”的动作反馈。映射如下：

| 小骑士招式 | 诺艾尔反馈 |
| --- | --- |
| 普攻、蓄力劈砍、旋风劈砍 | 轻受击 `PR.STATE.DAMAGE` |
| 冲刺劈砍 | 直线击飞 `PR.STATE.DAMAGE_L`，方向背离攻击者，距离为下砸的 1.5 倍 |
| 下砸 | 直线击飞 `PR.STATE.DAMAGE_L`，方向背离攻击者 |
| 复仇之魂、深渊尖啸 | 着火 `SER.BURNED`，120 帧，**灼烧伤害为 0，只保留动作/特效** |

实现要点：

- 攻击端在 `MultiplayerCompat.ApplyHpDamagePrefix` 里把反馈标签写进现有伤害包的字段：
  `knockback_ratio_t = 0.777` 表示“来自小骑士”，`attr = MGATTR.FIRE` 表示着火，
  `burst_vx / burst_vy` 表示直线击飞。反馈码：`0=轻受击`、`1=下砸击飞`、
  `2=着火`、`3=冲刺劈砍击飞（速度/距离 ×1.5）`。**注意这些字段必须在通用击退参数（`= 1f`）
  之后写入**；早期版本就是标记先写、随后被覆盖，导致收包端完全识别不到。
- 联机远端玩家代理 `M2Gunmu` 不是 `NelEnemy`，走的是 `TryHitGenericAttackable` 通用命中入口；
  该入口现在也会按当前招式写反馈标签，否则冲刺/下砸/法术会被当成普攻轻受击。
- 收包端 `CombatGuard.DriveKnightHitReaction` 负责：
    - 着火：`Ser.Add(SER.BURNED, 120, 99, false)` 后把 `maxt` 钳到 120，
      并显式 `changeState(DAMAGE_BURNED)`；同时屏蔽本帧原生 `DAMAGE` 切换，
      确保进入真正的着火状态（方向键短时间失效）；
  - 直线击飞：不额外 `addFoc`，交给 AIC 原生伤害管线按 `burst` 选 `DAMAGE_L`，避免双重击飞；
    - 轻受击：用 `_lightHitLockUntil` 做 1 秒锁，锁内只结算伤害、不切 `DAMAGE` 状态；
      锁到期后显式重切 `DAMAGE`，并调用 `AnimationShuffler.dmg_normal` + `SpSetPose`
      驱动本体模型进入轻受击硬直（只切状态时只有左侧立绘会反应，模型不会动）。
- AIC 的 `SER.BURNED` 本身每 50 帧会通过 `PR.applyDamage(MDAT.AtkSlipDmgBurnedPr, true)`
  造成 6 点灼烧伤害；`CombatGuard.BurnSlipDamagePrefix` 只在小骑士点燃的 140 帧窗口内
  拦截这一项，因此画面/动作是满强度着火，但不掉血。

**日志速查**：

```
[KIC][PvP伤害] 本地 50 → 发包 100（倍率 2.00）
[KIC][受击反馈] 轻受击
[KIC][受击反馈] 轻受击（1 秒锁内，仅结算伤害）
[KIC][受击反馈] 直线击飞（burst=0.28,-0.12）
[KIC][受击反馈] 着火（120 帧，灼烧伤害已屏蔽=0）
```

若只看到 `[KIC][PvP伤害]` 而看不到 `[KIC][受击反馈]`，说明伤害包没有带 `knockback_ratio_t=0.777`
标记（先检查攻击端 DLL 是否为新版）；若反馈类型不对，检查 `TryHitGenericAttackable` 是否确实被调用。

### 11.12 护符 PvP 收官（同日）

- **4 灵魂捕手 / 6 噬魂者**：已存在。`GrantProxyNailSoul` 命中远端玩家代理时按 +3/+8 结算，无需改动。
- **9 幼虫之歌**：已存在。PvP 伤害走 `CombatGuard.RouteDamageToKnight → KnightTakeDamage`，
  受伤回魂逻辑正常触发。
- **21 苦痛荆棘**：对联机远端玩家代理补上了反击；且**对诺艾尔玩家伤害减半**，
  对远端小骑士保持原值。魔法霰弹（`MGKIND.PR_SHOTGUN`）额外在第一段就强制触发一次荆棘，
  避免多段判定在无敌帧里漏掉反伤。
- **22 巴尔德之壳**：已存在，PvP 伤害走同一受击入口。
- **42_tune 无忧旋律**：本模组内部 ID 为 43；已存在概率免伤逻辑，PvP 伤害走同一受击入口。

### 11.13 护符 10 蜕变挽歌（联机适配）

- 本地剑气原本只命中 `NelEnemy`；现在补上通用目标入口，和复仇之魂一样可以命中
  联机远端玩家代理 `M2Gunmu`，剑气也按骨钉体系结算，但不重复结算命中灵魂。
- 远端渲染继续走 `KnightFxSync` 的四边形特效通道（蜕变挽歌剑气独立网格已包含在快照里）。
- 额外在 `KnightFxSync` v5 负载尾部追加“蜕变挽歌剑气判定箱”语义段（世界格坐标），
  远端收到后登记为 `RemoteElegyRects`；本地小骑士下劈时除了检查远端骨剑/尖刺，
  也会检查远端剑气矩形，因此可以下劈弹起其他小骑士发出的蜕变挽歌剑气。

### 11.14 护符 20 亡者之怒（联机视觉）

- **红色光晕**：本地是程序化径向贴图，通用四边形快照会跳过；现在在特效负载尾部
  额外同步一个 Fury 透明度字节，远端用同样的径向贴图在本体身后重建脉冲红光。
- **红色剑气**：本地 Fury 使用 `rage_slash_*` 帧，这些帧已在共享精灵表中；另外在
  特效快照里加了兜底：贴图反查失败时，攻击剑气网格直接用当前帧名同步，避免红色
  斩击因共享贴图而漏发。

## 12. 普攻连击手感（迟滞感）修复（2026-09-18）

### 12.1 现象与成因

实测“连续普攻不流畅、有迟滞感”。查攻击状态机（`KnightEntity.Update` 的攻击段）后确认
不是掉帧，而是两个独立原因叠加：

1. **挥砍后段是“定格保持帧”**。攻击剪辑由 `BuildAttackClip` 取 HK 原版 `Slash/SlashAlt/UpSlash/DownSlash`
   的前 8 帧，而原版剪辑格式是“**只有前几帧是不同动作，后面全是同一张图**”（20fps，15 帧，尾部大量重复：
   `slash_left_longer0005` 重复十次）。取前 8 帧后：
   `[0000,0002,0003,0004,0004,0005,0005,0005]` —— 第 0.21s 起画面就完全不动了。
   也就是说一整刀 0.28s（全局 `Visual/AnimSpeed=0.75` 时被拉长到 **0.373s**）里，
   有约 **0.16s 是挥砍动作已经播完、只有定格帧的后摇**。
2. **后摇期间的攻击输入被直接丢弃**。原逻辑是
   `if (_nailArtQuickTap && !_attacking && …) { 出刀 } else if (_nailArtQuickTap) { _nailArtQuickTap = false; }`
   —— 一刀还没结束时按下的攻击会被整帧清掉。玩家连打时，落在后半段（定格帧）的那些点击全部作废，
   只能等这一刀结束再重新按，于是“按了没反应→突然砍一刀”交替出现，就是那种迟滞感。

（上劈/下劈另有一个独立小 bug：连续出同一套剪辑时剪辑名不变，`PlayClip` 不会重置 `_frameIndex`，
第二刀直接从最后一帧开始播，看起来像卡住不动。）

### 12.2 修改

- **攻击输入不再丢弃**：一刀未播完时按下的普攻/骨钉技艺会保留在 `_nailArtQuickTap` 里，
  只有冲刺/凝聚/坐椅/梦钉/劈砍等真正冲突的状态才丢弃。
- **后摇取消点接刀**（默认关闭）：本刀播到 `AttackChainCancelPoint`（占整刀时长比例）之后，
  手里握着的下一刀立刻接上（结束当前刀 → `DestroyHitbox()` → 同帧起手下一刀，动画/特效/判定箱全部重播）。
  设为 `0.75` 时正好落在“可视挥砍帧播完、只剩定格帧”的位置，连打间隔缩短为 0.3 / 0.225 秒。
- **每刀强制重播动画**：起手时置 `_currentClip = null`（与凝聚/尖啸/劈砍等其它技能同一套写法），
  上劈/下劈连续出招也能从第 1 帧重播。
- **配置** `Combat/AttackChainCancelPoint`（默认 `1`，范围 0.3~1）：
  `1` = 不取消，两次攻击的间隔严格等于一整刀。

### 12.3 普攻间隔定值：0.4s / 快速劈砍 0.3s（同日二稿）

攻击节奏按 HK 原版骨钉挥砍改成定值，并把“间隔”和动画速度解耦：

- `CharmEffects.SlashTimeBase` **0.28 → 0.4**，`SlashTimeQuick` **0.21 → 0.3**。
  挥砍计时器 `_attackTimer = SlashAttackTime()`，**不再除以全局 `Visual/AnimSpeed`**，
  因此实测的两次攻击间隔恒为 **0.4s**（佩戴护符 17 快速劈砍 **0.3s**），与动画速度配置无关。
- `SlashClipFps = 8f / SlashTimeBase`（= **20fps**，即 HK 原版挥砍帧率）：8 帧挥砍动画正好播满一整刀。
- `PlayClip` 里挥砍剪辑（`Attack/AttackAlt/UpSlash/DownSlash`）的速度改成
  **只乘快速劈砍倍率**（`SlashAnimSpeedMultiplier()`，0.4/0.3 ≈ 1.333），不再乘全局 `AnimSpeed`。
  这样“动画长度 = 本刀时长 = 两刀之间的间隔”，且 `AnimSpeed` 只影响走/跑/待机等其它动画。
- `Combat/AttackChainCancelPoint` 默认改成 `1`（不取消后摇），保证间隔就是 0.4s / 0.3s；
  连打时的输入缓冲仍然保留（不会丢输入，只是按 0.4s 的节奏出刀）。
- 诊断日志（跟随普攻判定框绿框开关 `AttackHitboxDebug`）：
  `[KIC][普攻间隔] 实测 0.xxx s（快速劈砍=…，本刀时长=0.xxx s）`。

单发伤害、判定框位置与判定时长（`HitboxActiveTime`）均未改动。

### 12.4 平砍剑气左右手交替（同日三稿）

本体动画本来就是左右手交替（`Attack` = `slash_left_longer0000~0005`，
`AttackAlt` = `slash_left_longer0006~0010`），但剑气一直固定播 `SlashEffect`
（`slashes_effect0000/0003/0001/0001/0002/0002`），两组动作配同一套剑气。

manifest 里其实早就有配套的另一组剪辑，只是没被使用/加载：

| 本体剪辑 | 剑气剪辑 | 帧 |
| --- | --- | --- |
| `Attack`（`_isRightSwing=true`） | `SlashEffect` | `slashes_effect0000/0003/0001/0001/0002/0002` |
| `AttackAlt`（`_isRightSwing=false`） | `SlashEffectAlt` | `slashes_effect0004/0007/0005/0005/0006/0006` |
| 亡者之怒同上 | `SlashEffect F` / `SlashEffectAlt F` | 同上（后段换成 `rage_slash_left*`） |

改动：

- `KnightEntity` 特效选择处按 `_isRightSwing` 交替 `SlashEffect` / `SlashEffectAlt`
  （亡者之怒对应 `SlashEffect F` / `SlashEffectAlt F`）。
- **必须把 `"SlashEffectAlt"` 加进加载白名单 `wantedClips`**：`PlayFxClip` 取不到剪辑时是
  `_fxTex = null`（直接不画），漏了这步会变成“隔一刀没有剑气”。
- 护符10 蜕变挽歌的剑气发射帧同步补上 `slashes_effect0005`
  （两组剑气的第 3 帧各自对应“出刀命中”那一帧），否则换成 `AttackAlt` 的那一刀不会发射剑气。
- 远端渲染无需额外改动：两组剑气的贴图都在 `knight_manifest.json` 的 sprites 表里，
  走同一套特效快照通道。

### 12.5 修长之钉 / 骄傲印记：螳螂爪样式剑气（同日四稿）

佩戴 **护符18 修长之钉** 或 **护符19 骄傲印记**（任意一个）时，骨钉剑气渲染换成螳螂爪样式。
manifest 里本来就有这套剪辑（`* M`），同样只是因为不在加载白名单里而没被用过：

| 出招 | 平时 | 长钉/骄傲印记 |
| --- | --- | --- |
| 横劈（右手，`Attack`） | `SlashEffect`：`slashes_effect0000/0003/0001/0001/0002/0002` | `SlashEffect M`：`slashes_effect0000/0003/mantis_slash_left0001×2/0002×2` |
| 横劈（左手，`AttackAlt`） | `SlashEffectAlt`：`slashes_effect0004/0007/0005/0005/0006/0006` | `SlashEffectAlt M`：`slashes_effect0004/0007/mantis_slash_left0005×2/0006×2` |
| 上劈 | `UpSlashEffect` | `UpSlashEffect M`：`…/mantis_up_slash0000×2/0001×2` |
| 下劈 | `DownSlashEffect` | `DownSlashEffect M`：`…/mantis_down_slash0001×2/0002×2` |
| 亡者之怒（1 血） | `SlashEffect(Alt) F` 等 | 仍然是 `F` 版（**F 优先级高于 M**，manifest 没有 `F M` 组合剪辑） |

改动：

- `CharmEffects.LongNailVisual()`：`IsEquipped(18) || IsEquipped(19)`。
- `KnightEntity` 特效选择处按“上劈/下劈/亡者之怒/长钉”四级选择剪辑名，横劈部分继续按
  `_isRightSwing` 左右手交替。
- 四个 `* M` 剪辑加入加载白名单 `wantedClips`（漏了会表现为“佩戴长钉后没有剑气”）。
- 上劈的横向微调 `UpSlashFxShiftX` 同步套用到 `mantis_up_slash0001`（两张图尺寸几乎一致：
  150×64 vs 146×58）。
- 护符10 蜕变挽歌的发射帧再补 `mantis_slash_left0001/0005`（长钉 + 挽歌同时佩戴时也要发射剑气）。

#### 12.5.1 螳螂爪剑气的尺寸微调（同日五稿）

螳螂爪那套图比普通剑气大一圈（157×108 → 214×110 等），需要在渲染时单独收/拉：

| 出招 | 尺寸（w/h） | 中心位移 |
| --- | --- | --- |
| 横劈 | `w -= 0.2 格`、`h += 0.4 格`（上下各 0.2） | 向身体侧回移 `0.1 格`（= 收缩量的一半，骑士侧边缘不动） |
| 上劈 | `h += 1.1 格`（向上 0.2 + 向下 0.9）、`w += 1.0 格`（左右各 0.5） | 中心净下移 `0.35 格` = (0.2−0.9)/2 |
| 下劈 | `h += 0.7 格`（向上 0.5 + 向下 0.2）、`w += 0.6 格`（左右各 0.3） | 中心净上移 `0.15 格` = (0.5−0.2)/2 |

（表里是**累计后**的最终值；横劈未再改动。）

#### 12.5.2 上/下劈拉伸数值再调整（累计，同日六稿）

- **这两轮是在前一轮基础上叠加，不是替换**（第一轮：上劈向下 0.5/左右 0.1，下劈向上 0.5/左右 0.1；
  第二轮：上劈向下 0.1 向上 0.2 左右 0.2，下劈向下 0.2 左右 0.2）。累计结果：
  - 上劈：`MantisUpSlashStretchUp=0.2`、`MantisUpSlashStretchDown=0.6`、`MantisUpSlashGrowX=0.3`
  - 下劈：`MantisDownSlashStretchUp=0.5`、`MantisDownSlashStretchDown=0.2`、`MantisDownSlashGrowX=0.3`
- 位置补偿统一按 `(向上拉伸 − 向下拉伸)/2` 计算中心位移：拉伸哪边就长哪边，另一侧边缘保持不动。
- 横劈保持 `w -= 0.2 格`、中心回移 0.1 格不变。

#### 12.5.3 继续叠加（同日七稿）

- 横劈（M）：上下再各加 0.1 格 → 累计上下各 0.2 格（`MantisSlashGrowY=0.2`），
  左右普攻对称（纵向拉伸与朝向无关），收缩量与中心回移不变。
- 上劈（M）：再叠加“向下 0.3 格、左右各 0.2 格” → 累计
  `MantisUpSlashStretchUp=0.2`、`MantisUpSlashStretchDown=0.9`、`MantisUpSlashGrowX=0.5`，
  中心净下移 0.35 格。
- 下劈本轮未涉及，仍为 `Up=0.5 / Down=0.2 / GrowX=0.3`。

#### 12.5.4 单帧额外微调（同日八稿）

同一套螳螂爪剪辑里，个别帧的重心和其他帧不一样，需要按帧单独补位置/尺寸。
实现方式是用 `_fxSpriteName` 命中具体帧名（不是按帧序号，避免以后改剪辑顺序就失效）：

| 帧 | 调整（以面朝左定义，面朝右自动镜像） | 实现 |
| --- | --- | --- |
| `mantis_down_slash0002` | 整体向右平移 0.5 格 | `fx += _faceDir * CellToUx(0.5)` |
| `mantis_up_slash0001` | 整体竖直平移（累计见 12.5.6） | `fy += CellToUx(MantisUpSlash0001ShiftY)`（上下不受朝向影响） |
| `mantis_up_slash0001` | 向右拉伸 0.5 格 | `w += 0.5 格`、`fx += _faceDir * CellToUx(0.25)`（右边缘外扩、左边缘不动） |

- 常量：`MantisDownSlash0002ShiftX=0.5`、`MantisUpSlash0001ShiftY`（累计）、`MantisUpSlash0001GrowSide=0.5`。
- 只对 M 版剪辑生效（先判断 `mantisFx`），亡者之怒 `F` 版与普通剑气不受影响。
- 上劈 `mantis_up_slash0001` 原本就有 `UpSlashFxShiftX`（0.15 格）的横向微调，这里是**再叠加**。

#### 12.5.5 上劈 0001 帧上移量再叠加（同日九稿）

- 上劈 `mantis_up_slash0001`：向上平移再叠加 0.3 格 → 累计 **0.6 格**
  （`MantisUpSlash0001ShiftUp=0.6`）；横向的“向右拉伸 0.5 格 + 中心右移 0.25 格”不变。
- 上下位移与朝向无关（面朝左/右都是上移 0.6 格），横向部分才按 `_faceDir` 镜像。

#### 12.5.6 上劈 0001 帧改为向下（同日十稿）

- 上劈 `mantis_up_slash0001`：再叠加“向下平移 1 格” → 累计
  `0.3(上) + 0.3(上) − 1.0(下) = 净下移 0.4 格`，即 `MantisUpSlash0001ShiftY = -0.4f`
  （正=上移、负=下移，代码里直接用 `CellToUx` 加到 `fy`）。
- 横向“向右拉伸 0.5 格 + 中心右移 0.25 格”不变；上下位移仍与朝向无关。

#### 12.5.7 上劈 0001 帧再叠加（同日十一稿）

- 上劈 `mantis_up_slash0001`：叠加“向上 0.2 格” → 竖直累计
  `0.3 + 0.3 − 1.0 + 0.2 = 净下移 0.2 格`（`MantisUpSlash0001ShiftY = -0.2f`）。
- 叠加“向左收缩 0.2 格”：按与横劈同一套口径（`收缩方向 = 图形整体朝该侧收缩，
  该侧边缘不动、另一侧边缘向该侧回缩`），横向上与之前的“向右拉伸 0.5 格”同轴叠加：
  净外扩 `0.5 − 0.2 = 0.3` 格，中心随之外移 `0.15` 格（`MantisUpSlash0001GrowSide = 0.3f`）。
- 朝向镜像规则不变：面朝右时“向右”变“向左”，竖直位移仍与朝向无关。

#### 12.5.8 上劈 0001 帧再加横向纯平移（同日十二稿）

- 上劈 `mantis_up_slash0001`：叠加“向右平移 0.2 格”。纯平移与“拉伸/收缩”分开记账，
  所以宽度不变，只是中心再右移 0.2 格（`MantisUpSlash0001ShiftX = 0.2f`）。
- 该帧横向合计中心位移（面朝左）= 纯平移 `0.2` + 净外扩 `0.3/2 = 0.15` → **右移 0.35 格**；
  宽度净外扩仍是 0.3 格。面朝右时整体镜像。

#### 12.5.9 上劈 0001 帧改用“左右两侧分别记账”（同日十三稿）

“向左拉伸”和之前的“向右拉伸/收缩”方向相反，用单一净外扩量表示容易算反，改成两侧分开记：

```csharp
private const float MantisUpSlash0001GrowRight = 0.3f; // +0.5 向右拉伸 − 0.2 向左收缩
private const float MantisUpSlash0001GrowLeft  = 0.1f; // 本次：向左拉伸 0.1
private const float MantisUpSlash0001ShiftX    = 0.2f; // 纯平移（不改宽度）
private const float MantisUpSlash0001ShiftY    = -0.2f;
```

- 宽度：`w += (GrowRight + GrowLeft) = +0.4 格`
- 中心横移：`(GrowRight − GrowLeft)/2 = +0.1 格`（面朝左时向右），再叠加纯平移 0.2 格
  → 该帧横向中心合计右移 **0.3 格**
- 面朝右时 `_faceDir` 取反，整体镜像；竖直位移与朝向无关。

#### 12.5.10 上/下劈【判定框】额外拉伸（同日十四稿）

之前各轮只改了剑气**渲染**，这轮改的是**判定框**（绿框画的就是实际碰撞体，会自动跟着变）。
判定框由「矩形 `BoxCollider2D` + 三角形 `PolygonCollider2D`」组成，三角形底边**恒等于矩形宽度**
（`hw = boxSize.x * 0.5f`），所以只改矩形宽度即可同时满足“矩形左右各拉伸 / 三角形底部左右各拉伸”。

| 出招 | 矩形高度 | 矩形宽度 | 中心位移 |
| --- | --- | --- | --- |
| 上劈 | `+0.5 格`（向下，上边缘与三角形底边不动） | `+1.0 格`（左右各 0.5） | 下移 `0.25 格` |
| 下劈 | `+0.2 格`（向上，下边缘与三角形底边不动） | `+0.4 格`（左右各 0.2） | 上移 `0.1 格` |

改动落在三处（必须同时改，否则绿框与实际命中会不一致）：

1. `SpawnHitbox()`：`boxSize`（矩形碰撞体；三角形底边由它推导）；
2. `UpdateHitboxPosition()`：判定框中心 `hy` 的补偿位移（`CellToUx(拉伸量/2)`）；
3. `CheckAttackOverlap()`：兜底 `Physics2D.OverlapBoxAll` 的 `size` 与 `hy`。

常量：`MantisUpSlashHitboxDown=0.5` / `MantisUpSlashHitboxWiden=0.5`、
`MantisDownSlashHitboxUp=0.2` / `MantisDownSlashHitboxWiden=0.2`，
均以 `CharmEffects.LongNailVisual()`（佩戴 18 或 19）为条件叠加在护符原有拉伸之上。
拼刀（`CollectNailAttackRects`）与远端判定都读实际碰撞体包围盒，无需改动即自动生效。

#### 12.5.11 不佩戴长钉/骄傲印记时的上/下劈判定框+渲染拉伸（同日十五稿）

同一套“拉伸量”现在按“是否佩戴 18/19”分成两档，用三元表达式一次算好，四处（碰撞体 / 中心偏移 /
兜底 Overlap / 渲染尺寸）共用同一组常量，避免两边数值不一致：

| 出招 | 判定框矩形（附加竖直拉伸） | 渲染（附加竖直拉伸） |
| --- | --- | --- |
| 上劈 | 向下 `0.5 格`（佩戴 18/19 时也是 0.5） | 向下 `0.8 格`（中心下移 0.4） |
| 下劈 | 向上 `0.5 格`（佩戴 18/19 时为 0.2） | 向上 `0.8 格`（中心上移 0.4） |

常量：`PlainUpSlashHitboxDown=0.5`、`PlainUpSlashFxDown=0.8`、
`PlainDownSlashHitboxUp=0.5`、`PlainDownSlashFxUp=0.8`。
渲染侧的条件是 `!CharmEffects.LongNailVisual()`（亡者之怒同时佩戴长钉时不套用普通档，保持原样）。

#### 12.5.12 不佩戴护符：矩形单边收缩 + 左右拉伸（同日十六稿）

“矩形顶端/底端收缩”是**单边收缩**：指定边向另一侧收 0.5 格，对侧边不动，
三角形底边贴在矩形对应边上（同一 GameObject，`hw = boxSize.x/2`、局部 `dir*hh` 即该边），
所以只要改 `boxSize.y` 和中心 `hy`，三角形自然跟着贴住不会脱离。

| 出招 | 矩形（叠加） | 渲染（叠加） |
| --- | --- | --- |
| 上劈 | 顶端向下收 0.5（高 −0.5、中心下移 0.25，底端不动）+ 左右各 0.2 | 左右各 0.2 |
| 下劈 | 底端向上收 0.5（高 −0.5、中心上移 0.25，顶端不动）+ 左右各 0.2 | 左右各 0.2 |

净效果（与 12.5.11 叠加后）：上劈判定框高度回到基础值、整体下移 0.5 格；
下劈整体上移 0.5 格；两者的矩形宽度都变成基础 + 0.4 格。

常量：`PlainUpSlashTopShrink=0.5`、`PlainDownSlashBottomShrink=0.5`、
`PlainUpSlashHitboxWidenX=PlainDownSlashHitboxWidenX=0.2`、
`PlainUpSlashFxWidenX=PlainDownSlashFxWidenX=0.2`。
四处（`SpawnHitbox` / `UpdateHitboxPosition` / `CheckAttackOverlap` / 渲染尺寸）同步计算，
其中“单边收缩”造成的中心位移方向为：顶端下移 → 中心下移；底端上移 → 中心上移。

#### 12.5.13 默认横劈剑气收招两帧朝攻击方向拉伸（同日十七稿）

- 帧：`slashes_effect0001` / `slashes_effect0002`（默认 `SlashEffect` 剪辑的后 4 帧，
  也即横劈“出刀→收招”段；`SlashEffectAlt` 的两个对应帧是 0005/0006，本次不涉及）。
- 调整：朝攻击方向再拉伸 `0.2 格` —— 朝向侧的边缘外扩 0.2、另一侧边缘不动，
  所以宽度 `+0.2 格`、中心朝攻击方向移 `0.1 格`（`fx -= _faceDir * CellToUx(0.1)`）。
- “向左普攻向左拉伸 / 向右普攻向右拉伸”由 `_faceDir` 自动镜像。
- 常量：`SlashEffectFrontGrow = 0.2f`；判定框（碰撞箱）本轮未改，只改渲染。

### 12.6 判定框绿框调试开关（2026-09-19）

普攻判定框微调全部完成后已关闭：`KnightEntity.AttackHitboxDebug = false`
（横劈/上劈/下劈的绿色矩形+三角形线框不再绘制，`[KIC][普攻间隔]` 诊断日志也随之停止输出）。
其余调试开关（`NailArtHitboxDebug` / `DashSlashHitboxDebug` / `CycloneHitboxDebug` /
`DiveHitboxDebug` / `FireballHitboxDebug` / `HurtBoxDebug` / `NailParryDebug` /
`SporeCloudHitboxDebug` / `UterusExplosionHitboxDebug` / `BossHitboxDebug`）此前均已关闭。
需要再看时把对应常量改成 `true` 重新编译即可。

### 12.7 梦语只给敌对魔物（2026-09-19）

**问题**：可对话的魔物 NPC（魔物商人、魔物酒保、会钓鱼的魔物等）也会被梦钉读出梦语
（而且是与普通怪共用的“入侵者……/魔力……魔力……”那套），不符合设定。

**根因**：AIC 里这些 NPC 都是 `NelEnemy` 的子类，梦钉命中判定只认 `NelEnemy`，
所以它们和敌对魔物一样进入 `ShowDreamTextForEnemy`。

**AIC 侧的类型事实**（反编译 `_tmp_aic_src/Assembly-CSharp` 核对）：
所有可对话 NPC 都派生自 `MvNelNNEAListener` 的**嵌套类** `MvNelNNEAListener.NelNNpcEventAssign`
（它本身是 `NelEnemy` + `ISummonActivateListener`），已知子类：

| 类 | 用途 |
| --- | --- |
| `NelNEvBarten` | 魔物酒保（`MvNelNNEAListener_Barten` 里 `prepareCreateNNEA<NelNEvBarten>`） |
| `NelNEvFirstman` | 事件 NPC（`prepareCreateNNEA<NelNEvFirstman>`） |
| `NelNNpcPuppet` | 流浪/魔像 NPC（`WanderingManager` 用 `sub_golem_npc` 创建） |
| `NelNMgmFarmAnimal` | 农场动物（鸡/牛等，`M2LpMgmFarm` 创建） |

它们共同特征：`cannotHitTo(M2Mover) => true`、`Nai.add_enemy_target_count = false`、
带 `AttachEvent`（挂在 `MvNelNNEAListener` 上），走路时不会主动攻击诺艾尔。
另有大量敌对类（`NelNMush/NelNFox/NelNSlime...`）都设 `kind = ENEMYKIND.DEVIL`，
所以**不能用 kind 区分**，只能用“是否派生自 NPC 基类”判断。

**改动**（`KnightEntity`）：

- 新增 `IsNpcMonster(NelEnemy)`：沿 `BaseType` 链按类型名比对 `"NelNNpcEventAssign"`
  （反射比对类型名，避免编译期硬依赖游戏内嵌套类型；异常时按普通敌对怪处理）。
- `ShowDreamTextForEnemy` 开头命中 NPC 直接 `return`（不给梦语）。
  灵魂恢复、怪物 MP 清空、白光闪光与粒子爆发仍照旧（只去掉文本）。
- 随之删除原来的农场动物专属台词（“……这个能吃吗……”），因为农场动物也属于该 NPC 体系；
  若要保留这一条，在 NPC 判断之前加回该分支即可。

#### 12.7.1 农场魔物恢复梦语（同日，按需求调整）

农场魔物属于 NPC 体系但**需要梦语**，所以单独开一支，放在 NPC 判断之前
（`enemy is nel.mgm.farm.NelNMgmFarmAnimal`，其子类是 `NelNMgmFarmChicken` 鸡 /
`NelNMgmFarmCow` 牛，用 `GetType().Name` 区分）：

| 类型 | 中文梦语（随机一句） | 日文（`TX.familyIs("_") || TX.familyIs("ja")`） |
| --- | --- | --- |
| 鸡 `NelNMgmFarmChicken` | ……这个能吃吗…… / ……蛋……蛋…… / ……魔力……魔力…… | ……これ、食べられるのかな…… / ……卵……卵…… / ……魔力……魔力…… |
| 牛 `NelNMgmFarmCow` | ……这个能吃吗…… / ……奶……奶…… / ……母亲…… | ……これ、食べられるのかな…… / ……乳……乳…… / ……母…… |

其余可对话 NPC（商人 / 酒保 / 钓鱼 / 魔像 NPC）仍然不出梦语。

## 13. 单机版部署整理 + “到底加载了哪份 DLL”诊断（2026-09-19）

### 13.1 现象与真正原因

用户反馈“同一份模组在联机版正常、单机版很多 bug”。查单机版
`…\AliceInCradle_ver030\BepInEx\plugins` 发现**同一个插件存在两份 DLL**：

| 位置 | 内容 |
| --- | --- |
| `plugins\KnightInCradle.dll` | 9/18 21:59 的**旧构建**（`AttackHitboxDebug=true`、没有后续 10 轮调整） |
| `plugins\KnightInCradle\KnightInCradle.dll` | 最新构建 |

`BepInEx` 是**递归扫描 `plugins\**\*.dll`** 的，于是日志里出现：

```
[Info   : BepInEx] 2 plugins to load
[Warning: BepInEx] Skipping [KnightInCradle 0.2.0] because a newer version exists (KnightInCradle 0.2.0)
```

两份 GUID/版本号完全相同（都是 `dev.KnightInCradle 0.2.0`），BepInEx 的去重规则对同版本
**保留先扫到的那份**——也就是 plugins 根目录里的旧构建，子目录里的新构建被跳过了。
所以“改了没生效 / 有一堆早就修过的 bug”，本质是**加载了旧 DLL**，不是单机环境本身的问题。
（联机版没有根目录那份 DLL，所以一直是好的。）

另外单机版还残留 `KnightInCradle.dll.prev`、`KnightInCradle.dll.bak-20260917-1242`
（旧版备份）和 `CharmUiExport\`（运行时不读取的开发导出目录）。

### 13.2 处理（2026-09-19）

1. 把根目录那份旧 DLL 与所有 `.prev`/`.bak` 备份、`CharmUiExport\` 移到
   `<游戏根>\_KIC_cleanup_20260919\`（移出 plugins 树，BepInEx 不再扫描；可随时取回）。
2. 单机版只保留 `plugins\KnightInCradle\KnightInCradle.dll`（与联机版一致），
   素材/`键位.txt` 本来就在同一个 `KnightInCradle\` 子目录里，
   模组是用 `Paths.PluginPath + "KnightInCradle\..."` 定位的，与 DLL 放哪无关。
3. 把联机版里**被手工平移过像素**的 4 张螳螂爪横劈图
   （`mantis_slash_left0001/0002/0005/0006.png`）同步到单机版和素材源目录
   —— 这 4 张是此前调视觉时在联机版里直接挪过像素的，单机版还是原始版本，
   会导致同一套代码在两个安装里渲染位置不同。原版已备份。
4. 两个安装的 `KnightInCradle\` 目录现在**逐字节一致**（2126 个文件，无差异）。
5. `deploy.ps1` 改成只部署到 `plugins\KnightInCradle\KnightInCradle.dll`，
   并在发现根目录还留着同名 DLL 时**报警告**，避免再次出现“两份插件”。

### 13.3 新增启动诊断：这局到底加载了哪份 DLL

`[KIC][补丁]` 那行现在会带上构建标记和**实际加载的 DLL 路径 + 文件时间**：

```
[KIC][补丁] KnightInCradle build=2026-09-19.1 71 成功 / 0 失败 dll=D:\...\plugins\KnightInCradle\KnightInCradle.dll (2026-09-19 04:52:53)
```

- `build=` 来自 `KnightInCradleBehaviour.SelfBuildTag`，每次部署时手动改；
- `dll=路径 (文件时间)` 用 `Assembly.GetExecutingAssembly().Location` 取，
  一眼就能确认“是不是 plugins 里那份旧 DLL 在跑”。

### 13.4 单机版“跳跃失灵 / 平台边缘浮空”= 配置里开了 `NativeBodyMode=true`（2026-09-19）

**现象**：单机版切成小骑士后跳跃无效、走到平台边缘会浮空，要在斜面上走一会才恢复正常；
同一份模组在联机版没有这个问题。

**定位**（两份安装的日志对照，构建号都是 `2026-09-19.1`、DLL 文件时间 04:52:53）：

| 安装 | cfg 里的值 | 日志 |
| --- | --- | --- |
| 联机版 | `NativeBodyMode = false` | 无 `[NativeBody]` 行 → 走模组自己的手写物理 |
| 单机版 | `NativeBodyMode = true` | `[NativeBody] 原生地面移动接管 ON（贴地行走由诺艾尔 M2MoverPr 物理执行）` |

**原因**：`NativeBodyMode=true` 会启用「方案A 原生物理接管」——贴地行走交给诺艾尔的
`M2MoverPr` 执行，小骑士每帧从宿主读回位置。这条路径在 `NativeBody.cs` 头部注释里就写明了
它的问题：跳跃不接管、由骑士自研物理执行，两边互相拉扯，表现为「时跳时不跳」；
而位置读回会让小骑士的落地/离地判定跟随宿主，走出平台边缘时因为宿主仍被判为挂地，
小骑士就被“钉”在空中（浮空），只有在斜坡上走一段触发宿主重新贴地/挂脚后才恢复。
换言之：**它本来就是已知不稳的实验开关，不是单机环境的 bug**。

**修复**：

1. 把单机版 `BepInEx/config/dev.KnightInCradle.cfg` 的 `NativeBodyMode` 改回 `false`
   （与联机版一致；这也是代码里的默认值）。
2. 源码里那句会误导人的说明（“窄缝/矮洞里会卡住，**建议保持 true**”）已改成
   如实描述：`false` 是行走/跳跃正常的默认值，`true` 会出现跳跃失灵/平台浮空。
   见 `Plugin.cs` 的 `NativeBodyMode` 描述与 `KnightInCradleBehaviour` 的 `[KIC][配置]` 行。
3. 启动时若检测到 `NativeBodyMode=true`，额外打一条 `[KIC][警告]`，避免再被误开启后
   当成模组 bug 排查。

> 需要过窄缝/过图稳定性时用 `ResizeHostToKnight` + `ForceHostCrouch`（默认都开）
> 配合 `false` 就够用；不要把 `NativeBodyMode` 打开。

### 13.5 键位.txt 换成新版模板 + 解析别名（2026-09-19）

用户给了新版 `键位.txt`（带【技能键位】【功能键位】【可用键位】【修改键位】四段说明），
已按该版本替换：两个安装的 `plugins\KnightInCradle\键位.txt` 用用户原件覆盖
（旧文件备份为同目录 `键位.txt.bak-20260919-0532`），
`KeyFile.DefaultTemplate`（键位文件缺失时自动生成的模板）也改成**与用户版本逐字节一致**。

**关键：新版里的两个名称原来解析不到，必须加别名**（`KeyFile.FindAbility`）：

| 新版写法 | 原解析名 | 处理 |
| --- | --- | --- |
| `梦之钉：空格` | 只认 `梦钉` | 新增 `梦之钉` 别名 |
| `护符：O` | 只认 `护符ui` / `护符ui键` | 新增 `护符` / `护符界面` 别名 |

不加别名时这两行会被静默跳过（`FindAbility` 返回 null → `continue`），
表现为“改了键位文件但梦钉/护符键没变”。

新版**没有列出** `交互` / `HUD常显` / `坐标轴` 三项；`KeyFile.Load()` 只覆盖文件里出现的行，
所以这三项保持 cfg 里的现值不变：`Interact=F`、`AlwaysShowHud=Slash`、`ShowAxes=Comma`。
需要继续用文件配置它们时，在“【修改键位】”段补上同名行即可。

#### 13.5.1 删除 `交互` / `HUD常显` 键位（含源码），`坐标轴` 加回（同日第二稿）

按需求把这两个键位**从模组里彻底删掉**（配置项、键位文件解析、源码逻辑全删），
并把误删的 `坐标轴` 加回。`KeyFile.DefaultTemplate` 仍与用户给的版本逐字节一致。

**删除清单**：

| 位置 | 处理 |
| --- | --- |
| `Plugin.cs` | 删除 `InteractKey`、`AlwaysShowHudKey` 字段与 `Config.Bind("Keybinds","Interact"/"AlwaysShowHud")` |
| `KeyFile.cs` | 删除 `交互`、`hud常显` 两个别名；模板删掉对应行（并用回用户新版，含 `坐标轴：Comma`） |
| `KnightEntity.cs` | `TryBenchInteraction` 去掉所有 `InteractKey` 分支；删除整套「修复4」原生 CHECK 模拟代码（`SetNoelCheckSim` / `ClearNoelCheckSim` / `TryExecuteNativeCheckDirect` / `_noelCheckSimTimer` 及其每帧倒计时） |
| `CombatGuard.cs` | `LockInputPrefix` 不再锁 CHECK；删除 `AlwaysShowHud` 全部判定，HUD 改为固定常显 |
| `KnightInCradleBehaviour.cs` | 删除 `AlwaysShowHud` 字段与“/”键切换逻辑 |
| 两份 cfg | 删掉 `Interact = F`、`AlwaysShowHud = Slash` 两行（含注释块） |

**行为变化（重要）**：

1. **交互（门 / NPC / 宝箱 / 存档点）**：原先骑士模式下 CHECK 被锁、只能靠模组的 F 键转发；
   现在改成**放行游戏原生 CHECK 键**（`AllowedMenuKeys` 里的 CHECK 不再排除），
   直接用 AIC 自带的交互键即可，模组不再有自己的交互键位。
2. **坐长椅**：本来就支持 上（抬头键 Q）/ 下（低头键 鼠标右键），去掉 F 后不受影响；
   起身仍然是 移动 / 跳跃 / 攻击。
3. **HUD（诺艾尔血条/魔力条 + 小骑士 HUD）**：`AlwaysShowHud` 开关删除后变为**固定常显**
   （原来是进游戏默认强制常显，所以观感不变，只是不能再按键关掉）。

## 14. AIC 新版（ver0.30g 构建）适配体检（2026-09-19）

### 14.1 版本与改动面

| 项目 | 旧版（我们一直在用的） | 新版 |
| --- | --- | --- |
| `Assembly-CSharp.dll` | 5,574,656 B（2026/8/31） | **5,617,664 B（2026/9/15）** |
| `unsafeAssem.dll` | 2,665,472 B（2026/8/27） | **2,680,320 B（2026/9/15）** |
| `pixelliner.dll` | 80,384 B（2026/8/21） | 80,384 B（2026/9/9） |
| `readme_v030.txt` | ver 0.30 (260825) | ver 0.30 (260825)（**未更新**，实际是新构建） |

反编译对比（忽略 `// Token:` 注释后逐字节比）：**977 个源文件里 117 个有实质改动**
（`nel\*.cs` 为主，含 `UIStatus/UIPictureBase/UiGMCMap/UiSVD/ItemStorage/MDAT/CFG` 等）。
两份新版目录（`New Version AIC\…` 与 `D:\Documents\示范\…`）程序集逐字节一致。

### 14.2 体检工具：`tools/PatchProbe`（离线补丁体检器）

不需要启动游戏，直接加载指定版本的 AIC 程序集，把一份目标清单逐条真跑 Harmony 补丁，
报告“方法是否存在（签名是否变了）/ 能否挂上（IL Compile Error 等）”，并打印完整异常堆栈。

```powershell
cd "D:\Documents\Knight In Cradle\KnightInCradle\tools\PatchProbe"
dotnet build -c Release
.\bin\Release\net472\PatchProbe.exe <Managed 目录> <目标清单.txt>
```

清单格式 `类型全名;方法名[;参数类型1,参数类型2]`，现成清单：`failing_targets.txt`（新版实测失败的 21 项）、
`fallback_targets.txt`（指南针修正后的目标 + 已有替代锚点）、`kaleido_targets.txt`（联机模组 57 个补丁目标）、
`kic_member_lookups.txt`（KIC 用 AccessTools 查的 126 个成员）。

### 14.3 体检结果

**① 编译层面：无冲突。** 同一份源码
对新版 `Assembly-CSharp/unsafeAssem/pixelliner` 与旧版分别编译都是 **0 错误**
→ 同一份 DLL 两版通用（csproj 新增 `AicRoot` 属性切换引用目录：
`dotnet build -c Release -p:AicRoot="<游戏根>"`，默认指向新版）。

**② 成员查找：无新增缺失。** 126 个 `AccessTools` 查找里，只有 3 个“找不到”，
且**新旧两版完全一样**（`M2MoverPr.isNoDamageActive`、`PR.isNoDamageActive` 本来就有基类兜底；
`NelEnemy.maxhp` 是查错了声明类型——真正的字段在基类 `M2Attackable` 上，
顺手修掉了：现在沿继承链向上找，梦语不再永远落进“<100 血”那一档）。

**③ 补丁目标：全部存在、签名未变。** 实测失败的 21 个目标在新版里都能找到，
参数名/参数类型与 KIC 里 prefix/postfix 声明的完全一致 → 不是“签名变了”导致的失败。

**④ 真正的冲突：21 个补丁挂不上（`HarmonyException: IL Compile Error`）。**
新版实机日志（构建 2026-09-19.1）：`56 成功 / 15 失败` + 指南针 `6 失败`：

```
KIC 15：M2PxlAnimatorRT.set_color / set_alpha、FallenCutin.setE、NelEnemy.initAbsorb、
        M2LpMapTransferBase.executeTransferFastTravel、UiBenchMenu.ExecuteFastTravel、
        WholeMapManager.fnMgRun_initS_Sacred、M2LpSummon.deassignActiveWeed、
        M2PuncherCannon.applyHpDamage、MgBsSpiderTrap.run、NelNBossSpider.applyDamage、
        UIBase.fineHpMpRatio、UIPictureBase.changeEmotIn、UIPicture.applyGasDamage、
        M2MvColliderCreatorAtk.recreateExecute
指南针 6：UiGameMenu.activate、UiGMCMap.initAppearMain / runEdit / executeFastTravelConfirm、
        ButtonSkinWholeMapArea.setWholeMapTarget、NelM2DEventListener.EvtRead
```

这不是新问题类型：源码里早有同类记录（“**0.29j 上 setE / changeEmotIn 的 IL 会让 Harmony 编译失败**”，
当时改为挂 `FallenCutin.run` / `UIPictureBase.readFader` 等替代入口）。
离身体检同样复现了其中 5 项（`FallenCutin.setE`、`UIPictureBase.changeEmotIn`、
`M2MvColliderCreatorAtk.recreateExecute`、`UiGameMenu.activate`、`UiGMCMap.executeFastTravelConfirm`、
`ButtonSkinWholeMapArea.setWholeMapTarget`），底层异常是
`System.Security.SecurityException: ECall 方法必须打包到系统模块中`
（MonoMod 在 `RuntimeHelpers._PrepareMethod` 阶段 Pin 失败）——也就是**这些方法的 IL 无法被 HarmonyX 重写**。
**已有的替代锚点在新版依然可用**（`FallenCutin.run`、`UIPictureBase.readFader`、`UIPicture.run`、
`UIPictureBase.changeEmotDefault/applyDamage`、`PR.applyGasDamage` 体检全部 OK）→ 修复思路与 0.29j 相同。

**⑤ 联机模组（Kaleidoscopic）**：新版安装里没有装它（`plugins` 只有 KIC）。
拿反编译源码抽出它的 57 个补丁目标做静态体检，**新/旧两版结果完全一致**
（41 OK、3 个目标名不存在：`Cursor.visible`/`RCP.RecipeDish`/`UiGMCMap.can_use_fasttravel`，13 个离体环境挂不上）
→ 静态层面**没有发现新版新增的破坏点**；但它必须在装了新版游戏的环境里实跑一次才能定论。

### 14.4 本轮交付

1. `tools/PatchProbe` 离线体检器 + 4 份清单（可复用于以后每次游戏更新）。
2. csproj 支持 `AicRoot` 切换引用目录（默认新版，一条命令即可切回旧版编译）。
3. **补丁失败日志升级**：失败时除了一行摘要，还会额外打印**完整异常堆栈**
   （`[KIC][补丁失败详情] …` / `[指南针][补丁失败详情] …`），
   这样才能区分“方法找不到 / 参数绑定失败 / IL 无法重写（要换锚点）”。
4. 顺带修复 `NelEnemy.maxhp` 查找（梦语血量分档之前一直失效）。
5. 新构建（build=2026-09-19.2）已部署到 4 个安装：
   新版（`New Version AIC`）、`D:\Documents\示范`、旧版单机、联机版。

### 14.5 下一步（待实机确认后逐项改锚点）

拿到新版实机日志里的完整堆栈后，按 0.29j 的老办法为每个失败目标挑一个“同类但 IL 可挂”的入口，例如：

| 失败目标 | 备选锚点（待验证） |
| --- | --- |
| `M2PxlAnimatorRT.set_color / set_alpha` | 渲染入口（每帧 run/update）或 `M2PxlAnimatorRT` 的其它公开方法 |
| `NelEnemy.initAbsorb` / `NelNBossSpider.applyDamage` / `M2PuncherCannon.applyHpDamage` | 走它上层的 `applyDamage` / `applyHpDamage` 管线（KIC 已挂同类） |
| `M2LpMapTransferBase.executeTransferFastTravel` / `UiBenchMenu.ExecuteFastTravel` | 传送前后可用的 `run` / 确认入口 |
| `UIPicture.applyGasDamage` / `UIPictureBase.changeEmotIn` | 已有 `UIPictureBase.readFader` 兜底可用 |
| 指南针 6 项 | `activateMap` / `runEdit`（已 OK）等入口 + 现有的每帧重试逻辑 |

### 14.6 新版 0.30g 联机安装"卡在启动画面"的排查结论（2026-09-19）

现象：`D:\Documents\联机模组`（后改名 `D:\Documents\AIC_MP`，0.30g）四个插件都在，
但游戏**停在启动画面十几分钟进不去**；而在 0.30d 的 `AICmultiplayer` 安装、以及只装 KIC 的
0.30g 安装（`New Version AIC`）都正常。

排查过程（逐项排除）：

1. **不是崩溃**：Windows 应用程序日志无 AliceInCradle 错误事件、无 CrashDump；
   BepInEx 日志里 4 个插件初始化全部成功（KIC `71 成功 / 0 失败`、Kaleidoscopic 48 模块 enabled、
   AicUtils `Backend initialized … cacheHit=True`），没有任何 Exception。日志末尾那一大串
   `disabled module` + `QUIC … 正在关闭: 插件进程退出` 是**玩家手动关掉游戏时的收尾**，不是中止原因。
2. **不是游戏文件损坏**：`AIC_MP` 与基准新版安装逐文件比对，`Managed` 159 个、
   `StreamingAssets` 3035 个（560.4 MB）名称+大小完全一致。
3. **不是中文路径**（但仍是隐患）：Kaleidoscopic 的 DLL 里就带着作者原文
   "Unity Mono/Harmony 可能因此启动失败，请将游戏和插件移动到纯英文路径"，
   且 cfg 里 `不再提示非 ASCII 路径` 默认 false、被手动设成了 true（把警告藏起来了）。
   文件夹已改名 `D:\Documents\AIC_MP`、警告设置改回 false —— **改完仍然卡**，说明这条不是本因。
4. **不是 Kaleidoscopic**：把它移出 plugins 后仍卡；且 `Kaleidoscopic.dll` 中不含任何对
   AicUtilsRefreshed 的引用（两者独立）。
5. **元凶 = AicUtilsRefreshed**：把它的总开关关掉后
   （`BepInEx\config\org.aliceincradle.aicutilsrefreshed.cfg` → `Enabled = false`），
   游戏立刻能正常进入，日志出现
   `[AicUtilsRefreshed] AicUtilsRefreshed 已由总开关停用；修改配置后需重启游戏才能生效。`。

结论：**AicUtilsRefreshed v0.1.0 与 AIC 0.30g 不兼容**——它在插件 Awake 阶段遍历 398 个
`.dat` 资源包建立"原始资源目录"，并 hook 游戏的资源加载；0.30g 换过资源加载代码后，
它把游戏的首次资源加载挂死在启动画面上（挂起但无异常）。

处置：

- 该安装保持 `AicUtilsRefreshed = false`（它只提供资源目录/F10 监控等辅助功能，
  KIC 与 Kaleidoscopic 都不依赖它，禁用后联机功能不受影响）；
- 想要这些辅助功能，需要找联机模组作者要**配套 0.30g 的 AicUtilsRefreshed 版本**，
  拿到后把 `Enabled` 改回 `true` 重启即可。

### 14.7 两套安装的最终状态与“按安装编译”规则（2026-09-19 收尾）

联机作者后来针对 0.30d 更新了 Kaleidoscopic（`protocol=7`，日志出现
`HandleAuthenticateComplete … 服务器认证完成`），小骑士与联机可同时正常使用。

当前两套安装（**注意工程与安装都已搬到 `D:\Documents\AliceInCradle\` 下**）：

| 用途 | 游戏根目录 | 游戏程序集 | KIC DLL |
| --- | --- | --- | --- |
| 联机（0.30d） | `D:\Documents\AliceInCradle\AICmultiplayer\AliceInCradle Win ver030\AliceInCradle_ver030` | 5,596,672 B（2026-09-02 01:10） | `FE59036E…`（按该安装编译） |
| 单机新版（0.30g） | `D:\Documents\AliceInCradle\Knight In Cradle\New Version AIC\AliceInCradle Win ver030\AliceInCradle_ver030` | 5,617,664 B（2026-09-15 20:34） | `C3666E35…`（按该安装编译） |

**规则：KIC 的 DLL 必须按目标安装的游戏程序集编译**，否则会出现
`[KIC][补丁] … N 失败（HarmonyException: IL Compile Error）`（新版 0.30g 上踩过：旧引用编译 → 21 个补丁挂不上）。

- 编译：`dotnet build -c Release -p:AicRoot="<目标游戏根>"`（csproj 默认指向 0.30g 那套）
- 一键部署：`build_and_deploy.ps1 -GameRoot "<目标游戏根>"`
- 自检：DLL 内写入 `AssemblyMetadata: AicCompileTarget`，启动日志会打印
  `编译目标=… 运行时游戏程序集=…`；**两者时间一致即配对正确**（0.30d 安装实测：
  `编译目标=2026-09-02 01:10 … 运行时游戏程序集=2026-09-02 01:10 / 5596672 bytes`，`71 成功 / 0 失败`）。

### 14.8 联机/单机统一到 0.30g（2026-09-19 最终形态）

作者随后放出了配套 0.30g 的联机包，用户把它和 0.30g 的小骑士模组放进同一份 0.30g 安装，
实测可正常进游戏并联机。核对结果（`D:\Documents\AliceInCradle\AIC_MP\AliceInCradle Win ver030\AliceInCradle_ver030`）：

| 检查项 | 结果 |
| --- | --- |
| 游戏本体 | 0.30g（`Assembly-CSharp.dll` 5,617,664 B / 2026-09-15 20:34） |
| 插件 | KIC + Kaleidoscopic(protocol 7) + AicUtilsRefreshed + ConfigurationManager |
| KIC 补丁 | `71 成功 / 0 失败`，且 `编译目标=2026-09-15 20:34 …` 与 `运行时游戏程序集=2026-09-15 20:34 / 5617664 bytes` **一致** |
| 联机 | `ConnectionController/Start … game=0.30g` → `HandleAuthenticateComplete … 服务器认证完成` ✓（**0.30g 能联机**） |
| 日志错误 | Error 0 行 |
| AicUtilsRefreshed | 此处 `Enabled = true` 且 `Backend initialized … cacheHit=True`，游戏正常 → 见下方更正 |

**更正 14.6 的结论**：当时"卡启动＝AicUtilsRefreshed 的锅"是在**旧版联机模组 + 0.30g 的混合环境**下得出的；
换成作者配套 0.30g 的联机包后，AicUtils 开着也能正常启动。以后再遇到卡启动，先按
“BepInEx 日志停在哪一行 + 逐个插件加减”定位，别直接照搬旧结论。

**损坏伤害缩水的处理（2026-09-19 晚，已修）**：作者包默认 `PvPDamageMultiplier = 1`，
实测小骑士打远端诺艾尔时伤害又缩水，日志给出直接证据：

```
[KIC][PvP伤害] 本地 72 → 发包 72（倍率 1.00，反馈=0）
```

即**攻击端把真实伤害原样发出**，打折发生在联机转发/收包侧（与 0.30d 时代同一现象：
当时也是靠预乘 2 抵消）。已把两份安装的该配置都改成 **`PvPDamageMultiplier = 2`**
（小骑士打玩家的伤害在发包前 ×2，落地后正好等于真实值；单机没有远端玩家，值是 1 或 2 都不影响）。

排查与验证要点：

- 攻击端日志：`[KIC][PvP伤害] 本地 X → 发包 Y（倍率 2.00，反馈=…）` —— 倍率应为 2；
- 收包端日志：`[KIC][PvP伤害] 收到远端小骑士攻击 kind=… 收包=… → 标记 fix_damage`
  —— 这条说明收包端已挡掉 AIC 的“非满血减伤”，**双方都必须用同一份 KIC DLL**（`C3666E35…`），
  否则收包端缺这条修复，伤害仍会不对；
- 若改成 2 后伤害变成**双倍**，说明新版联机模组已经不再压缩玩家伤害，把该项改回 1。

**DLL 统一**：两份安装（`AIC_MP` 与 `New Version AIC`）的 `KnightInCradle.dll` 现在逐字节相同
（`C3666E35FE460038…`，9,040,896 B，2026-09-19 21:32:10），且都与本工程 `bin\Release` 的最新 0.30g 构建一致。
两份 cfg 现在只差一项个人偏好（`SeriousMode` 认真模式开关）。

**`PvPDamageMultiplier` 的准确语义**（容易被当成"原伤害开关"，实际是发包前预乘倍率）：

```
落地伤害 ≈ 本地伤害 × PvPDamageMultiplier × 联机侧压缩系数（当前实测 0.5）
```

- 生效范围：**仅**“小骑士攻击远端玩家（远端诺艾尔 / 另一名小骑士）”这一条路径；
- 打怪/Boss 不走它：对“别人开战生成的远端代理魔物”是代码里固定 ×3（那边压缩到约 1/3）；
- 单机没有远端玩家，值多少都不影响；
- **倍率只需攻击方设置**，受击方设多少都不影响数值（受击方只做 `fix_damage` 挡非满血减伤）；
- 所以想“双向都保持原伤害”，**两台机器都要设 2**（只看自己的日志会漏掉对方那一侧）；
- 若将来联机模组改了压缩比（比如变成 1/3），这个值要跟着改成 3；日志里 `倍率 X.XX` 那行可直接核对。

### 14.9 定时诊断静音开关（2026-09-19，build=2026-09-19.3）

骑士模式下有几条"每 1~2 秒一行"的体检日志会持续刷日志（一次游玩几百 KB），
现在统一收到一个总开关后面：**`General/PeriodicDiagnostics`，默认 `false`（静音）**。

| 被静音的日志 | 原频率 |
| --- | --- |
| `[KIC][宿主体型]` | 2 秒 |
| `[KIC][物理前体型]`（NativeBody 路径） | 1 秒 |
| `[KIC][宿主蹲伏]` | 2 秒 |
| `[KIC][宿主姿势]` | 2 秒（节流） |
| `[KIC][体型夹紧]` | 1 秒（节流） |
| `[KIC][远端下砸]` | 每 20 帧（联机下砸判定探测） |

**保留（不受该开关影响）**：

- 启动体检：`[KIC][补丁]`（含失败完整堆栈）、`[KIC][配置]`、`[KIC][警告]`、编译目标/运行时程序集对照；
- 事件型诊断：`[KIC][PvP伤害]`、`[KIC][受击反馈]`、`[KIC][拼刀]`、`[KIC][暗影冲刺]`、`[KIC][尖啸]`、`[KIC][护符]`、`[KIC][吸虫奖励]`；
- `[KIC][阻塞诊断]`（只在"按住方向 0.35 秒没位移"即真卡住时才打，不刷屏）；
- `[KIC][普攻间隔]`（由 `AttackHitboxDebug` 控制，随绿框一起开关）。

排查体型/卡地形/远端下砸时：`BepInEx/config/dev.KnightInCradle.cfg` 里把
`PeriodicDiagnostics` 改成 `true`，重启游戏即可恢复这些体检行。

- 横劈的“向身体侧”用 `_faceDir` 表示，左右普攻自动镜像（向左普攻就向右收，向右普攻对称）。
- 判定条件直接用当前特效剪辑名（`_fxClipName` 以 `" M"` 结尾），保证只有真的在播 M 版时生效，
  亡者之怒的 `F` 版不受影响。
- 尺寸换算沿用旁边既有写法：尺寸用 `* _mp.CLEN`（mesh px/格），中心位移用 `CellToUx()`（ux/格）。
- 常量：`MantisSlashPullBack=0.2`、`MantisSlashGrowY=0.1`、`MantisUpDownStretch=0.5`、`MantisSlashGrowX=0.1`。

## 15. 吸虫之巢：切回诺艾尔后吸虫不再消失（2026-09-20，build=2026-09-20.2）

**现象**：佩戴护符23 吸虫之巢时，暗影之魂会变成 16 只黑色吸虫（寿命 4~5 秒、重力 28 格/s²、
落地弹跳、碰到敌人或远端玩家造成 7 点伤害（萨满之石+巢羁绊为 9）后消失）。
但**放完吸虫立刻切回诺艾尔，吸虫会瞬间全部消失**。

**原因**：切回诺艾尔走 `KnightEntity.Deactivate()` → `ReleaseTicket()`，后者会把 `_flukes` 清空、
并把吸虫的渲染票据/网格一起 deassign + destruct；同时 `_mp` 被置空，而 `Update()` 开头是
`if (!_assetsLoaded || !_active) return;` —— 骑士一停用就没人再推进这些吸虫了。

**改动**（全部在 KIC 侧，不需要改联机模组）：

| 位置 | 改动 |
| --- | --- |
| `ReleaseTicket(bool keepFlukeList=false, bool keepFlukeTicket=false)` | 新增两个开关：可保留吸虫列表 / 保留吸虫票据与网格；deassign 改用 `FlukeMp`（停用后 `_mp` 已为 null） |
| `Deactivate()`（切回诺艾尔） | `_flukeMp = _mp` → `ReleaseTicket(keepFlukeList: true, keepFlukeTicket: true)` → 再 `_mp = null` |
| `RebindTicket(bool keepFlukes=false)` | 切回小骑士（`SpawnAt` 且非读档）传 `keepFlukes: true`：列表保留、票据重建；换图/读档/快速旅行仍传 false 清掉 |
| `KnightEntity.TickOrphanFlukes()` | 停用期间由 `KnightInCradleBehaviour.SceneGameUpdatePostfix`（挂在 `SceneGame.Update` 后置）每帧调用，继续 `UpdateFlukes`；列表空了就释放票据 |
| `FlukeMp` 属性 | 地面检测 `FlukeGroundY`、命中判定 `CheckFlukeHit`、攻击 MagicItem `GetKnightAttackMagic`、渲染回调 `KnightPrepareFlukeMesh` 全部改用"保留的地图引用"，保证停用后仍能检测与绘制 |
| `MultiplayerCompat.ApplyHpDamagePrefix` | 伤害改写的准入从 `IsActive` 放宽到 `IsActive || HasOrphanFlukes`，让孤儿吸虫打远端玩家时仍走小骑士的伤害管线（伪装 `PR_PUNCH` + 倍率补偿 + 标记） |

**行为**：切回诺艾尔后，已放出的吸虫会继续飞、弹跳、命中敌人/远端玩家，直到各自寿命结束；
死亡、换图、读档（`RebindTicket` 默认参数）仍会立刻清掉。

验证：装备护符23 → 切小骑士 → 暗影之魂放出吸虫 → 立刻按 T 切回诺艾尔，应看到吸虫继续在地上扑腾几秒后消失。

## 16. 吸虫之巢：本地诺艾尔也能被（自己放出的）吸虫命中并结算奖励（2026-09-20，build=2026-09-20.3）

**需求**：小骑士放完吸虫后切回诺艾尔，诺艾尔应当能受到吸虫的伤害，并且同样获得吸虫奖励物品。

**改动前的准入条件**（`KnightEntity.CheckFlukeHit`）：

```csharp
M2Attackable ga = c.GetComponentInParent<M2Attackable>();
if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
    ApplyFlukeToGeneric(ga, FlukeDamageNow()))
```

`!(ga is PR) && !(ga is M2MoverPr)` 这一条本来是为了"吸虫不要打到自己"，
但它同时也把**本地诺艾尔**永久排除在吸虫判定之外；而且本地玩家的碰撞体并不在
`GetEnemyOverlapMask()`（`EnemySelf/Enemy/AttackHitable/Ignore Raycast/Water/TransparentFX/Default`）
覆盖的层里，所以即使去掉这行排除，Overlap 也未必能查到诺艾尔。

**改动**（`src/KnightEntity.cs`）：

| 位置 | 改动 |
| --- | --- |
| `CheckFlukeHit` | 先把吸虫位置换算成世界坐标 `center`，再调 `TryHitLocalNoelByFluke(center)`；命中则 `_flukes.Remove(fl)` 后返回。`GetEnemyOverlapMask()` 的空掩码检查移到这个新判定之后 |
| `TryHitLocalNoelByFluke(Vector2)` | 新增。`KnightInCradlePlugin.KnightModeActive == true`（骑士模式，诺艾尔是隐藏宿主）时直接返回 false —— 避免刚放出的吸虫立刻打到自己。否则取 `GetPr()`，用**诺艾尔自己的碰撞体** `pr.getColliderCreator().Cld` 的世界包围盒 `bounds` 向外扩一个 `FlukeHitRadius` 做圆-盒粗判（不依赖层掩码） |
| `ApplyFlukeToLocalNoel(PRNoel)` | 新增。构造 `NelAttackInfo`：`hpdmg_current/hpdmg0 = FlukeDamageNow()`（7，萨满之石+巢羁绊 9）、`fix_damage = true`（不按 HP 状态砍半）、`burst_center = FlukePacketMarker`、`PublishMagic = GetKnightAttackMagic()`，然后 **`pr.applyDamage(atk, true)`** |

**为什么走 `PR.applyDamage` 而不是 `applyHpDamage`**：
`PR.applyDamage(NelAttackInfo, bool)` → `DMG.applyDamage(Atk, ref hittype, …)`，
也就是 `CombatGuard.PrDmgApplyRefPostfix` 挂着的那个方法。于是吸虫标记会被同一段已有逻辑接住：

* 左侧立绘切"虫墙"（`UP.applyDamage(MGATTR.WORM, …, "insected")`）
* `TryGrantFlukeRewards(pr)`：各 50% 掉 5 星「新鲜的诺艾尔汁」（需空瓶+容量）/ 5 星「诺艾尔的卵」
* 清掉 `burst_center`，不影响后续伤害

即本地诺艾尔与远端诺艾尔走的是**同一条结算逻辑**，不需要为本地再写一份奖励代码。

**行为**：佩戴护符23 → 切小骑士 → 暗影之魂放出吸虫 → 按 T 切回诺艾尔 →
仍在飞的吸虫会正常命中诺艾尔本人（7 点真实伤害、吸虫消失），并按 50%/50% 结算奖励。
骑士模式下不判定，所以放吸虫的瞬间不会自伤。

验证：`build=2026-09-20.3`。

## 17. 吸虫奖励新增「诺艾儿乳」（2026-09-20，build=2026-09-20.4）

**需求**：诺艾尔（自己与远端）受到吸虫伤害时，有 20% 概率获得**满级**的「诺艾儿乳」。

**物品确认**（查 `AicDataPacks\monitor\resources\text\data\item` 与本地化文件，不是猜的）：

```text
mtr_noel_milk 0 3 4 {          // <key> <Rarely> <Price> <スタック最大>
    CATEG MTR CURE_HP CURE_MP WATER CURE_EP
    MAX_GRADE_VALUE 70
}
```

* 物品 ID = `mtr_noel_milk`（`NelItem.noel_milk_key` 常量就是它），中文名「诺艾儿乳」
* `CATEG` 含 `WATER` → **WLink 水瓶物品**，和诺艾尔汁一样：必须留一个空瓶（`mtr_bottle0`）
  让 `ItemStorage.Add` 自己完成"空瓶 → 乳"的链接，**不能先手动扣瓶**（先扣瓶会让 Add 找不到可连接容器而直接失败，
  这正是之前"诺艾尔汁拿不到"的坑）
* "满级" = `grade` 参数取 4：`NelItem.getGradeMultiply` 用 `X.ZLINE(grade, 4f)`，
  所以 grade 只有 0~4 五档，4 就是满级（HUD 显示 5 星）

**改动**（`src/CombatGuard.cs` → `TryGrantFlukeRewards`）：在原有两个独立 roll 之后追加第三个：

| roll | 物品 | 条件 |
| --- | --- | --- |
| 50% | 满级「新鲜的诺艾尔汁」`mtr_noel_juice0` | 空瓶 + 容量 |
| 50% | 满级「诺艾尔的卵」`mtr_noel_egg` | 容量 |
| 50% | 满级「诺艾儿乳」`mtr_noel_milk` | 空瓶 + 容量 |

**为什么不用改两处**：第 16 节已经把本地诺艾尔的吸虫命中并入了 `PR.applyDamage`，
与远端诺艾尔共用 `CombatGuard.PrDmgApplyRefPostfix` 的吸虫分支。所以奖励逻辑只在
`TryGrantFlukeRewards` 里加一段，**本地诺艾尔与远端诺艾尔同时生效**。

验证：`build=2026-09-20.4`，DLL SHA256 `D69E51D6439746A8…`（两份 0.30g 安装已同步部署）。
日志里出现 `[KIC][吸虫奖励] 获得 满级「诺艾儿乳」（消耗 1 空瓶）` 即命中。

> **概率调整（2026-09-20，build=2026-09-20.6）**：三个奖励统一为**各 50%**独立 roll
> （汁 50%、卵 50%、乳 50%）—— 只有诺艾儿乳从 0.20 改成了 0.50，汁和卵本来就是 50%。
> 附带效果：概率全部调成 50% 后，`[KIC][吸虫奖励]` 那三条日志也已随第 18 节的清理一起删掉，
> 所以现在只能靠背包/仓库实际到货确认命中。

## 18. 调试日志清理（2026-09-20，build=2026-09-20.5）

按需求删掉了事件型日志、定时型日志，以及逗号键 / O 键相关的调试内容。**保留**的都集中在启动阶段。

### 已删除

| 类别 | 删除内容 | 涉及文件 |
| --- | --- | --- |
| 事件型 | `[KIC][PvP伤害]`（攻击端发包 / 收包端标记）、`[KIC][受击反馈]`（轻受击·直线击飞·着火共 6 处）、`[KIC][拼刀]`、`[KIC][吸虫奖励]`（3 处）、`[KIC][尖啸]`、`[KIC][护符]`（苦痛荆棘反伤）、`[KIC][暗影冲刺]`（触碰 + 每 20 帧扫描汇总）、`[KIC][阻塞诊断]`、`[KIC][联机同步] 远端小骑士本体贴图缺失`、`[KnightInCradle][远端骑士] 帧=…`、`[指南针]` 补丁警告 | `CombatGuard.cs`、`KnightEntity.cs`、`KnightInCradleBehaviour.cs`、`MultiplayerCompat.cs`、`CharmEffects.cs` |
| 定时型 | `[KIC][宿主体型]`、`[物理前体型]`、`[宿主蹲伏]`、`[宿主姿势]`、`[体型夹紧]`、`[远端下砸]`、`[KIC][联机同步·发]`、`[KIC][联机同步·收]`（2 条）、`[NativeBody][st]` | 同上 + `NativeBody.cs` |
| 开关 | cfg `General/NativeBodyDebug`、`General/PeriodicDiagnostics`、`Keybinds/ShowAxes`；源码 `KnightInCradlePlugin.PeriodicDiag` / `NativeBodyDebugConfig` / `PeriodicDiagConfig` / `ShowAxesKey` | `Plugin.cs` + 两份 `dev.KnightInCradle.cfg` |
| 逗号键 | `KnightEntity.ShowKnightAxes` 字段、Update 里的切换、`KnightPrepareFireballDebugMesh` 里的红/蓝坐标轴绘制；`KeyFile` 的 `坐标轴` 别名与默认模板两行；两份部署的 `键位.txt` 同步删行 | `KnightEntity.cs`、`KeyFile.cs`、`键位.txt` |
| O 键相关 | 护符 UI 创建/加载时打印的 `[CharmUi]` 日志（`诊断: screen=…`、`构建完成: …`、`游戏缺少 UI/Default…` 以及素材缺失 / 布局解析失败的提示） | `CharmUiLoader.cs`、`CharmUiOnGuiLayer.cs` |

顺带删掉了因此变成死代码的辅助成员：`LogBodyClamp`、`LogKnightBodySize`、`LogPosePassthrough`、`LogStatus`、`DiagnoseStuck`/`ResetStuckWatch`、`MeasureBodyPixels`、`LogKnightSpriteOnce`、`DescribeFxPayload`、`NearestRemoteDiveDistance`，以及 `_fixDmgLogTimer` / `_bodyClampLogTimer` / `_bodySizeLogTimer` / `_posePassthroughLogTimer` / `_crouchLogTimer` / `_physicsBodyLogTimer` / `_statusTimer` / `_fx*LogTimer` / `_seenKnightSprites` / `_missingSpriteLogged` 等计时器与去重集合。

### 保留（全部在启动阶段，正常游玩不会刷屏）

| 日志 | 时机 |
| --- | --- |
| `[KIC][补丁] build=… 71 成功 / 0 失败 dll=… 编译目标=… 运行时游戏程序集=…` | 进游戏一次 |
| `[KIC][配置] NativeBodyMode=… ResizeHostToKnight=… ForceHostCrouch=… HostPoseOverride=… HostCollider=…` | 进游戏一次 |
| `[KIC][警告] NativeBodyMode=true…` | 仅开了原生接管时 |
| `[KIC][补丁失败详情]` | 仅当有补丁挂不上 |
| `[NativeBody] 原生地面移动接管 ON` | 原生接管首次生效时一次 |
| `[KnightHudDeco] 素材加载失败` | HUD 素材真缺失时 |
| `[KIC][普攻间隔]` | 受编译期常量 `AttackHitboxDebug` 控制，当前 `false` |
| `[房间] key=…`（F8） | 按 F8 时主动打印 |

> 注意：护符 UI / 指南针的"素材缺失 / 布局解析失败 / 补丁挂不上"提示也一并删掉了 —— 这两个功能若出问题，日志里不会再有任何线索。需要恢复"只保留出错提示"随时可以加回来。

验证：`build=2026-09-20.5`，DLL SHA256 `84415D325EF7CE91…`，0 错误 0 新增警告；两份 0.30g 安装的 DLL / cfg / 键位.txt 已同步。

## 19. 护符2 蜂群集结：自动拾取掉落物（2026-09-20，build=2026-09-20.7）

**需求**：护符 2_collector 追加机制 —— 背包有空位时，自动拾取半径 3 格内的掉落物
（史莱姆的假卵、剑山的刺等）；并把「蜂群也会帮助持有者捡起周围的物品。」加进护符描述第一段之后。

### AIC 掉落物结构（用反射读 0.30g 程序集确认，不是猜的）

| 成员 | 位置 | 说明 |
| --- | --- | --- |
| `NelItemManager.ODrop` | Assembly-CSharp（**nonpublic**） | `Better.BDic<m2d.M2DropObject, NelItemManager.NelItemDrop>`，`Better.BDic` 在 `better.dll` |
| `M2DropObject.x / .y` | unsafeAssem，public | 掉落物的地图格坐标 |
| `NelItemManager.NelItemDrop.Itm / .count / .grade / .Dro` | Assembly-CSharp，public | 物品、数量、品级、对应的 M2DropObject |
| `NelItemDrop.canTalkable(bool)` | Assembly-CSharp，public | 返回 1 = 游戏自己允许拾取（`af/af_ground` 落地判定就在这里） |
| `NelItemManager.executePickUp(NelItemDrop)` | Assembly-CSharp（**nonpublic**） | 游戏原生拾取入口：`getItem` → 减少 count → 全部拿完则 `removeItData` + `PtcST` 粒子 |
| `NelItemManager.getStorageFor(NelItem)` | Assembly-CSharp（**nonpublic**） | 决定该物品进背包 / 贵重品 / 仓库 |
| `ItemStorage.getItemCapacity(NelItem, bool, bool)` | Assembly-CSharp，public | 对应存储区还剩多少容量 |

### 实现（`src/CharmUi/CharmEffects.cs`）

新增 `TickCollectorAutoPickup()`，由 `KnightEntity.Update()` 紧挨着 `ProtectCollectorMana()` 每帧调用
（只在骑士模式 + 已装备护符2 时生效）。流程：

1. `ImngODropField`（反射缓存的 `ODrop`）**用非泛型 `IDictionary` 枚举** —— 这样不用引用 `better.dll`；
2. 取 `NelItemDrop.Dro` 的 `x/y` 与骑士脚底 `k.FootY` 比距离，超出 `CollectorPickupRadius = 3f` 跳过；
3. `drop.canTalkable(false) == 1` 才继续 —— **刚掉出来还在弹跳的物品不会被瞬间吸走**，
   与手动按键能拾取的时机完全一致；
4. `HasRoomForDrop()`：用 `getStorageFor` 找到该物品真正会进的存储区，
   `getItemCapacity(itm, false, false) > 0` 才拾取 —— 没空位就不动（也不会弹原生“装不下”提示）；
5. 调用 `ImngExecutePickUpMethod`（`NelItemManager.executePickUp`）走**游戏原生拾取流程**，
   于是拾取音效 / 粒子 / 背包路由（背包 / 贵重品 / 仓库 / 水壶）与手动拾取完全一致；
6. 拾取成功后**立刻 return**，因为 `removeItData` 会改写 `ODrop`，边遍历边改会破坏枚举器。

另外：剧情/转场事件进行中（`EV.isActive(false)`）不拾取；一帧最多拾取一件
（游戏自身的 `pickup_delay` 15 帧还会再限流一次）。

### 文案

`CharmData.cs` 的护符 2 描述变成：

```
小蜂群会为持有者收集魔力草中的所有灵魂。

蜂群也会帮助持有者捡起周围的物品。

适合那些无论多细小的东西都不愿意丢下的人。
```

（护符 UI 直接读 `CharmDatabase` 的 `Desc`，见 `CharmUiOnGuiLayer.cs` 的 `string desc = cd.Desc;`；
`assets/hk/Charm_Description.txt` 只是同源的参考文本，运行时不会加载，也一并同步了。）

验证：`build=2026-09-20.7`，DLL SHA256 `67EFACBDA9BBBAAF…`（两份 0.30g 安装已同步）。
实测：装备护符2 → 切小骑士 → 打死史莱姆/剑山后走到 3 格内，掉落物应自己飞到背包。

---

## 20. "诺艾尔的护符"第一部分：诺艾尔也能装/卸护符（2026-09-22，build=2026-09-22.1）

**主题**：让诺艾尔也拥有一套独立的护符（装配/卸下界面先做，**效果留到第二部分**）。
总目标（用户原话要点）：诺艾尔坐在椅子上能打开护符界面、选择装配或卸下，并获得对应效果；
**诺艾尔与小骑士的护符互相独立**。

### 20.1 本部分范围（已实现）

1. 诺艾尔模式下按 **O**（`Keybinds/CharmUi`）打开护符界面——**随时能开**；
2. 只有**坐在长椅上**才能装配/卸下（与小骑士规则一致）；
3. 诺艾尔的装备列表写入**独立的存档命名空间**，与小骑士互不干扰；
4. **护符效果暂未实现**（第二部分）：本部分只保证"装得上、存得下、两边不串"。

### 20.2 设计

| 决策 | 做法 | 原因 |
|---|---|---|
| 复用同一套界面 | 不新做 UI：`CharmUiOnGuiLayer`（`charm_ui/layout.json` 建的 UGUI Canvas）+ `CharmUiController` 整套复用 | 800+1059 行 UI/交互逻辑零复制，行为天然一致 |
| 归属（owner）概念 | 新增 `CharmOwner { Knight, Noel }`；`CharmUiController.Owner` + `SetOwner(owner)`——切换前先把当前列表写回原归属，再读入目标归属 | 同一个控制器轮流编辑两套装备 |
| 两套存档 | 小骑士沿用 `kic_charm_slot0..10`（**格式不变，向后兼容**）；诺艾尔用 `kic_noel_charm_slot0..10` | 天然互相独立；旧存档不受影响 |
| 双快照 | `CharmSave` 同时缓存两套列表（`_equipped` / `_equippedNoel`），读档时两套一起恢复 | UI 没打开时护符效果也能立即生效（与小骑士现有做法一致） |
| 诺艾尔"坐着" | `PRNoel.isBenchState()`（= `PR.STATE.BENCH` / `BENCH_LOADAFTER` / `BENCH_ONNIE`，见 `PR.cs:5653`） | 直接复用 AIC 自己的长椅状态，不另造判定 |
| 与原生长椅菜单共存 | **叠加**（用户选的方案 a）：护符界面盖在原生长椅菜单之上，`CharmUiInputPatch` 已屏蔽 40+ 个 `IN.*`，原生长椅菜单的输入被一并冻结；关闭后继续用原生长椅菜单 | 改动最小；做成原生长椅菜单项（方案 b）留作后续可选增强 |
| 归属失效自动关 | 每帧检查：小骑士护符只在骑士模式显示，诺艾尔护符只在诺艾尔模式显示；模式切换/对象消失即关闭 | 原来的"退出骑士模式就关"改成按归属判断 |

### 20.3 刻意不做的部分（避免两边串味）

以下都是**小骑士侧**的概念/状态，诺艾尔界面上不显示、也不会被改动：

- **束缚（寻神者自限）**：`IsGgSelectorShown` 只在 `Owner == Knight` 时为真（`kic_gg_*` 是全局键）；
- ~~**格林之子 ↔ 无忧旋律变体**（T 键）：仅在编辑小骑士护符时生效，`ApplySavedVariant()` 对诺艾尔直接返回~~ —— **二稿已改为两边各自独立支持，见 20.7**；
- **坚固贪婪的背包容量**（`SyncGreedCapacity` / `CanUnequipGreed`）：诺艾尔装卸不触发；
- **装配后的血量联动**（坚固心脏/生命血/乔尼的祝福回血）：`TryToggleEquip` 里诺艾尔分支提前返回，不碰小骑士血量。

另外修了一处会被"双归属"暴露的隐患：`CharmEffects.IsEquipped` 原来无条件读控制器的 `EquippedIds`——
一旦控制器正停在诺艾尔那一侧，小骑士的护符效果就会误读诺艾尔的列表。现在只在 `Owner == Knight` 时读控制器，
否则读小骑士快照（`CharmSave.HasEquipped(CharmOwner.Knight, id)`）。

### 20.4 改动文件

| 文件 | 改动 |
|---|---|
| `src/CharmUi/CharmSave.cs` | 新增 `CharmOwner` 枚举；全部接口加 owner 重载（`ReadEquipped/WriteEquipped/SyncFromController/HasEquipped/EquippedSnapshotFor`）；`RestoreAfterLoad` 恢复两套；`KeyPrefix` 拆成 `kic_charm_slot` / `kic_noel_charm_slot` |
| `src/CharmUi/CharmUiController.cs` | 新增 `Owner` + `SetOwner(owner)`；三处装配写盘点改 owner-aware；GG 选择器/变体槽/坚固贪婪/血量联动改为小骑士专属 |
| `src/KnightInCradleBehaviour.cs` | `ToggleCharmUi()` 按模式决定归属与坐姿来源（诺艾尔用 `isBenchState()`）；抽出 `EnsureCharmUiCreated()`；每帧维护块改为按归属判断 + 按归属取坐姿 |
| `src/CharmUi/CharmEffects.cs` | `IsEquipped` 只认小骑士侧列表（见 20.3 的隐患修复） |

### 20.5 验证方法

1. 进游戏（**诺艾尔模式**，不要切小骑士）→ 按 **O**：护符界面应打开，已装备栏只有虚空之心（首次）；
2. 站在长椅旁坐下（AIC 原生长椅菜单出现）→ 按 **O** → 界面盖在长椅菜单上 → 用方向键选中护符 → 确认装配：
   - 坐着才能装/卸；不坐时按确认不动（`_sitting` 门控）；
3. 装几个护符 → 起身 → 再按 O：刚才的装备仍在（内存快照）；
4. 关闭界面 → 存档 → 读档 → 再按 O：诺艾尔的装备应当恢复（`kic_noel_charm_slot*`）；
5. **独立性检查**：切到小骑士（T）按 O，小骑士的装备列表应与诺艾尔完全不同；反复切换互不影响；
6. 日志确认身份：`[KIC][补丁] KnightInCradle build=2026-09-22.1 提交=… 71 成功 / 0 失败 dll=…`。

> 注意：本部分诺艾尔装配**不产生任何效果**（不进小骑士的 `CharmEffects`），所以"装上坚固力量打怪伤害没变"是预期行为。

### 20.6 第二部分待办

1. 让诺艾尔读取 `kic_noel_charm_slot*` 产生效果（需要一个"诺艾尔视角"的效果查询入口，例如 `CharmEffects.IsEquipped(CharmOwner, id)`）；
2. "事件"式解锁：完成特定任务才能解锁某些护符；按开启宝箱数提升护符槽上限（现在槽位是常量 `CharmDatabase.NotchCapacity = 11`，
   需要改成可变量，并让 UI 的槽位点数/超载判定一起跟）；
3. 诺艾尔专属护符（若要做，`CharmDatabase` 需要给 `CharmData` 加"归属/解锁条件"字段）。

验证：`build=2026-09-22.1`，DLL SHA256 `DB5A9F8D9DEA22C2…`（9,034,240 B，两份 0.30g 安装已同步；
**本次只覆盖 DLL，未动 `assets/hk`**，两份安装的素材集仍是 2005 个文件，联机下标表不受影响）。

### 20.7 二稿：诺艾尔侧支持格林之子↔无忧旋律、去掉虚空之心（同日，build=2026-09-22.2）

按需求做了两处调整，都只影响**诺艾尔那一侧**：

1. **格林之子 ↔ 无忧旋律 的变体切换（T 键）在诺艾尔界面上同样可用**。
   - 原实现把变体状态存在全局键 `kic_charm_variant` 上，一稿里干脆对诺艾尔禁用了；
   - 现在改成**按归属各存一份**：小骑士仍是 `kic_charm_variant`（格式不变），诺艾尔新增 `kic_noel_charm_variant`
     （`CharmUiController.VariantKeyFor(owner)`）；T 键切换、`ApplySavedVariant()`（读档还原槽位）两边都生效、互不干扰。
   - 因此两边可以各自选择"这一侧当前用格林之子还是无忧旋律"，切换一侧不会影响另一侧。
2. **诺艾尔已装备栏里删掉了虚空之心**（护符 id 40，`CharmDatabase.FixedCharmId`）。
   - `ApplyEquippedFromSave` 现在按归属决定：小骑士首位恒插虚空之心、容量 = 11 + 虚空之心（原行为不变）；
     **诺艾尔不插**，容量 = 11（`maxCount` 按归属计算）。
   - 由于网格本来就排除虚空之心（`BuildGridIds`），诺艾尔的界面里这只护符完全不出现。
   - 小骑士侧完全不动：仍是"虚空之心恒在首位、不可卸下"。

顺带堵了一个会串味的口子：顶部 **sign**（束缚/寻神者自限的入口）点击流程 `TryClickSign()` 会写全局键
`kic_gg_clicks` 并可能装备 `gg_godseeker_mode_selector` —— 现在对诺艾尔直接 return，只有小骑士侧能触发。

改动仍集中在 `src/CharmUi/CharmUiController.cs`（变体键按归属、`ApplyEquippedFromSave` 的固定护符与容量、`TryClickSign` 门控）。

验证方法（在 20.5 的基础上加两条）：

- 诺艾尔界面里选中 **格林之子**所在格 → 按 **T**：应换成 **无忧旋律**（图标/描述随之变化）；再按 T 换回；
  切到小骑士界面按 T，两边的选择互不影响；存档→读档后各自保持；
- 诺艾尔已装备栏**看不到虚空之心**（空栏时应只有空槽），小骑士侧仍能看到且不可卸下。

验证：`build=2026-09-22.2`，DLL SHA256 `FEBBC62264A96229…`（9,034,752 B，两份 0.30g 安装已同步；同样只覆盖 DLL，未动素材）。

### 20.8 三稿：诺艾尔界面里"虚空之心残影 + 多余槽位孔"的修正（同日，build=2026-09-22.3）

**现象（用户实测）**：二稿把虚空之心从诺艾尔列表里去掉后，它**不会被选中了**，但界面上仍有：
虚空之心的**图片**、它所在位置的**槽位孔**、以及它**后面那个槽位孔**；装备护符时护符正好落在
虚空之心那个位置，把它的图片盖住。

**根因（不是列表驱动的，而是布局里的静态元素）**：`charm_ui/layout.json` 里有两处静态元素：

| 元素 | 内容 | 作用 |
|---|---|---|
| `CharmUi/Charms/Image (39)` | `40_VOID.png`，位置 `(-800, 252)` | **虚空之心在已装备栏里的那张图**（不是网格图标） |
| `CharmUi/Equipment/charm_up (1)` / `(2)` | `charm_up.png` / `charm_up_2.png`，位置 `(-800,245)` / `(-690,245)` | 装备栏的**两个槽位样例**（给动态槽位提供位置与间距基准） |

它们平时"看不见"只是因为：

- 虚空之心那张图靠 `Controller.IsIconHidden("40_VOID.png")` 隐藏 —— 而它的判据是"该图标在不在 `EquippedIds` 里"，
  小骑士列表首位恒为 40，所以一直是隐藏的；
- 两个槽位样例被 `DrawEquippedBar()` 按同样位置画出的动态槽位**盖住**（小骑士列表至少有 1 个护符，
  动态槽位数 ≥ 2，正好覆盖这两个）。

诺艾尔去掉 40 之后：图标没人隐藏了 → 露出来；动态槽位只有 1 个（空列表 = 1 个空槽）→ 第二个槽位样例没人盖 → 也多出来一个孔。
而护符仍然装在 `slots[0]`（= 虚空之心原来的位置），所以在视觉上"盖住了虚空之心"。

**修正**（都只对诺艾尔生效，小骑士渲染完全不变）：

1. `CharmUiController.IsIconHidden`：`Owner == Noel` 时，把固定虚空之心的图标文件也判为"隐藏"
   （于是那张静态图不再绘制）；
2. `CharmUiOnGuiLayer.DrawStaticElements`：`Owner == Noel` 时跳过路径含 `charm_up` 的静态元素
   —— 装备栏完全交给 `DrawEquippedBar()` 动态绘制。
   注意 `GetBaseSlotRects()` **仍然读这两个元素的矩形**（它读的是 `_rects`，不受绘制跳过影响），
   所以槽位的位置与间距基准不变。

**修正后的表现**：诺艾尔装备栏里虚空之心彻底消失；空栏时显示 **1 个**空槽（就是第一个护符会落进去的位置）；
装了 N 个护符时显示 N + 1 个槽（护符 + 1 个空槽），与小骑士的规则一致。

> 重新导出 `layout.json` 时要注意：上面这两个静态元素是**必须保留**的——前者是虚空之心图标（小骑士侧要用），
> 后者是槽位几何基准；误删会导致小骑士侧装备栏没有槽底或槽位错位。

验证：`build=2026-09-22.3`，DLL SHA256 `2EE369BAA652A565…`（9,034,752 B，两份安装已同步；只覆盖 DLL，未动素材）。

---

## 21. "诺艾尔的护符"第二部分：护符效果（2026-09-22，build=2026-09-22.4）

**目标**：让诺艾尔佩戴的护符产生与描述一致的效果。
**本轮完成**：护符 **1 任性的指南针**（需求：与小骑士佩戴时效果相同）。

### 21.1 机制：效果查询按"归属 / 当前操控角色"分流

一稿里所有护符效果都写成"骑士模式 + 已装备"两个条件：

```csharp
if (!IsKnightMode || !IsEquipped(CompassId)) { return true; }   // 旧写法
```

第二部分统一改成按"当前操控角色"判断。`CharmEffects` 新增两个入口：

| 入口 | 语义 |
|---|---|
| `IsEquipped(CharmOwner owner, int id)` | 指定归属是否装备了某护符。小骑士侧沿用原规则（虚空之心恒为装备、束缚让其它护符失效）；诺艾尔侧直接读她自己那份列表 |
| `IsEquippedForCurrentPlayer(int id)` | 当前操控角色是否装备：骑士模式 → 小骑士那套；诺艾尔模式 → 诺艾尔那套 |

于是"两个角色都能用"的效果只要把 `!IsKnightMode || !IsEquipped(id)` 换成 `!IsEquippedForCurrentPlayer(id)`，
**同一份效果代码同时服务两个角色**，不需要复制一套。

### 21.2 任性的指南针（id 1）改了什么

指南针的效果全部是"地图/菜单门控"，实现在 `CharmEffects.PatchCompass` 挂的 7 个补丁里。本轮把其中 5 个的判断改成 `IsEquippedForCurrentPlayer`：

| 补丁点 | 作用 |
|---|---|
| `UiGMCMap.runEdit`（`CompassRunEditPrefix`） | 光标附近有可传送图标时自动打开快速旅行模式（防误触开关/切图重置） |
| `ButtonSkinWholeMapArea.fast_travel_active.set` / `setWholeMapTarget`（`EnemyIconsPostfix`） | 把战斗区域（ENEMY 图标）也加入快速旅行磁吸列表 |
| `UiGMCMap.executeFastTravelConfirm`（`FastTravelConfirmPrefix`） | 确认传送时跳过"必须在长椅旁"直接执行（战斗区域目标会先终止当前战斗） |
| `UiGMCMap.initAppearMain`（`MapAppearPostfix`） | 地图出现即强制 `can_use_fasttravel = true`、`fast_travel_active = true` |
| `UiGameMenu.activate` / `activateMap`（`FastTravelPostfix`） | 游戏菜单/地图激活后把 `can_use_fasttravel`、`pr_on_bench` 置真 |

另外 `KnightInCradleBehaviour.TryOpenGameMenuWithEsc` 里的"战斗中也能打开菜单"绕过
（原来写死 `KnightModeActive && IsEquipped(1)`）也改成 `IsEquippedForCurrentPlayer(CompassId)`。

**诺艾尔佩戴指南针后的表现（与小骑士一致）**：

1. 地图上可直接选长椅图标传送，**不需要先坐到椅子上**；
2. 战斗区域（魔物图标）也能作为传送目标（会先终止当前战斗）；
3. 战斗中也能按 ESC 打开菜单去传送（绕过 `can_open_gamemenu` 禁用标记）；
4. 目的地没有长椅时不再报 `近場にベンチがありません`（沿用 `AutoSaveBenchPrefix`，与角色无关）。

> 传送本体仍走 AIC 原生 `UiBenchMenu.ExecuteFastTravel`，诺艾尔作为本体自己走过去/被传送；
> 小骑士模式那套"骑士跟随"逻辑不参与（诺艾尔模式下小骑士实体是停用状态）。

### 21.3 验证方法

1. 诺艾尔**不装**指南针：地图上点长椅图标应无法传送（原版行为，需要先坐长椅）；
2. 诺艾尔**装上**指南针（坐长椅时装配）→ 起身 → 打开地图 → 选另一个长椅图标 → 应能直接传送；
3. 战斗中按 ESC：装了指南针应能打开菜单（不装时不能）；
4. 切到小骑士：骑士那套指南针行为不变；两边互不影响（各读各的装备列表）。

### 21.4 第二部分后续待办

`CharmEffects` 里还有若干"骑士模式 + 已装备"写死的效果点，按 21.1 的套路逐个改成按归属/当前角色判断即可：

| 护符 | 相关效果点 |
|---|---|
| 2 蜂群集结 / 31 蜂巢之血 | `ShouldProtectManaWeed` / `ShouldAutoPickup`（原为骑士模式专用） |
| 12 坚固贪婪 | 掉率提升、宝箱复制、背包容量（多处） |
| 16 沉重之击 | 普攻 8% 斩杀 |
| 27 深度聚集 | 宝箱轮转减速 |
| 32 蘑菇孢子 | 对蘑菇家族增伤 |
| 9 幼虫之歌 / 10 蜕变挽歌 | 受伤回魂 / 满血剑气 |
| 1 任性的指南针 | ✅ 本轮完成 |

> **另一类效果要注意**：速度、冲刺、法术伤害、召唤物（编织者/格林之子/子宫/孢子）这类效果**实现写在 `KnightEntity` 里**，
> 依赖小骑士自己的物理与状态机；诺艾尔侧没有对应机制，需要另设计（例如改诺艾尔的 `walkSpeed`、或给诺艾尔加一套轻量技能层）。
> 这部分等排到具体护符时再定方案。

验证：`build=2026-09-22.4`，DLL SHA256 `0ACFD6CD68D41A87…`（9,034,752 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 21.5 蜂群集结（id 2）：自动拾取 / 蜂巢怪不打 / 魔力不会被怪捡走（build=2026-09-22.5）

三个效果与小骑士一致。它们分布在 `CharmEffects` 的三处判定 + 三个"每帧维护"入口上，
本轮**只改判定口径**（按当前操控角色）**+ 补上诺艾尔侧的每帧调用**，效果实现本身完全复用：

| 效果 | 实现 | 本轮改动 |
|---|---|---|
| 蜂巢怪不打（蜂巢房间魔物中立） | `HiveNeutralActive()` + 三个补丁（`NAI.AwakeInit` 阻止苏醒、`NAI.set_AimPr` 阻止锁定、`Enemy.applyDamage` 攻击后 `TriggerHiveAggro()` 解除中立）+ 每帧 `ClearHiveEnemyAim()` | 判定由"骑士模式 + 已装备"改成 `IsEquippedForCurrentPlayer(...)`；攻击触发敌对那一处同样按当前角色 |
| 魔力不会被怪捡走 | `CollectorManaGuardActive()` 门控两处：`M2ManaWeed.SplashMana` 前缀（剥掉 EN 位，让落地魔力只能由诺艾尔吸）+ 每帧 `ProtectCollectorMana()`（落地魔力超时后会变成"谁都能吸"，这里把它重新剥掉 EN） | 同一个判定函数改成按当前角色。**注：一稿这里顺手跳过了原生掉落（不生成落地魔力），三稿已改回正常掉落并补上"满魔力也吸"，见 21.6** |
| 3 格内自动拾取 | `TickCollectorAutoPickup()`：沿用游戏自己的判据（`canTalkable` 已落地、`getItemCapacity` 放得下），走原生 `NelItemManager.executePickUp` | 拆出 `TickCollectorAutoPickup(px, footY)`，诺艾尔模式传她的坐标；原无参重载继续给骑士用 |

**诺艾尔侧的每帧入口（本轮新增）**：骑士模式这三个"每帧维护"都在 `KnightEntity.Update` 里调用，
诺艾尔模式没有对应调用点，因此在 `KnightInCradleBehaviour.Update` 加了 `TickNoelCharmEffects()`（`!_knightMode` 时执行）：

```csharp
if (!ReferenceEquals(pr.Mp, _noelCharmMap)) { _noelCharmMap = pr.Mp; CharmEffects.UpdateHiveRoom(pr.Mp); }
CharmEffects.ProtectCollectorMana();       // ① 落地魔力保持"仅诺艾尔可吸"
CharmEffects.ClearHiveEnemyAim();          // ② 中立期清除魔物锁定
CharmEffects.TickCollectorAutoPickup(pr.x, pr.mbottom);   // ③ 自动拾取（按诺艾尔坐标）
```

关键点：**蜂巢房间标记**原来只在骑士换图分支里更新（`KnightEntity` 调 `CharmEffects.UpdateHiveRoom`），
诺艾尔模式永远算不出"这是蜂巢房间"（`CurrentRoomIsHive` 恒为 false，中立效果就永远不会生效）；
这里按"当前地图对象变化"检测换图并重算。**不能每帧重算**——`UpdateHiveRoom` 会顺带把"攻击后敌对"标记清掉，
每帧调用等于让蜂巢魔物永远中立、打了也不还手。
未装备护符时上述调用都立即返回，开销可忽略。

**诺艾尔佩戴后的表现**：

1. 蜂巢房间里魔物保持中立（不苏醒、不锁定她）；主动攻击其中一只 → 全房解除中立（与小骑士一致）；
2. 破坏魔力草：魔力**照常落地**（三稿修正，见 21.6），但魔物无论如何都吸不到；落地魔力超时后也不会变成"谁都能吸"；
3. 3 格内已落地、且背包放得下的掉落物自动收入（拾取音效/粒子/背包路由与手动拾取完全一致），一帧最多一件。

> 与小骑士的唯一差别：小骑士破坏魔力草时额外 +1 灵魂（`KnightAddSoul(5)` vs 4）是**小骑士资源**上的加成；
> 诺艾尔侧对应的是魔力本身（原生就给她），因此没有也不需要这一项。

验证：`build=2026-09-22.5`，DLL SHA256 `DC178558E5C5A21A…`（9,034,752 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 21.6 三稿：蜂群集结的魔力"照常掉落 + 满魔力也吸"（build=2026-09-22.6）

**现象（用户实测）**：带蜂群集结破坏魔力草后，**掉落的魔力整个消失了**（21.5 里 `SplashMana` 前缀直接跳过原生实现、
把魔力用 `GrantNoelMana` 记到诺艾尔账上，地上不生成魔力球），看起来像"魔力凭空没了"，诺艾尔也没吃到。
需求：**魔力仍然正常掉落**，但这些魔力**全部由诺艾尔吸收**（**哪怕魔力条已经满了**）。

**改法 1：恢复原生掉落**。`ManaWeedSplashPrefix` 不再 `return false` 跳过原生，只改参数位：

```csharp
mana_hit = (mana_hit & ~MANA_HIT.EN) | MANA_HIT.PR;   // 只允许玩家吸
return true;                                          // 仍然走原生：正常生成落地魔力
```

（滚动 21.5 的"直接给诺艾尔记账"路径不再使用；`GrantNoelMana` 作为工具方法保留。）

**改法 2：满魔力也吸 —— 新增 `VacuumCollectorMana`**。原生的吸取链在满魔力时会自己断掉：

```
M2Mana.run:
  if (TargetM.destructed || (Target.getMpDesireRatio(mana_hit, 0) >= 1f && !immediate_collect) || ...)
      { Target = null; ... }        // ← 魔力条满时直接放弃锁定，站在球上也不会被吸走
```

而真正的吸收动作 `Target.addMpFromMana(this, 4f)` 本身**并不检查魔力是否满**
（`PR.canGetMana` 只看 `MANA_HIT.PR` 位与接触范围）。所以新增的真空层绕开锁定流程：

```csharp
// 每帧（两种模式都跑，位置 = 当前操控角色所在处；骑士模式下宿主诺艾尔已被同步到小骑士位置）
for each 落地魔力 m in nM2D.Mana:
    条件：非空 / 非 only_effect / af >= 0 / 带 PR 位 / 不带 EN 位 / 与玩家距离 ≤ 3 格
    → pr.addMpFromMana(m, 4f);   // 与原生吸收同一条入口、同一数值（4）
      m.destruct();              // 吸掉就销毁，地上不留
```

- 4f 与 `M2Mana.run` 内部的 `Target.addMpFromMana(this, 4f)` 完全一致；
- 半径 3 格与护符2 的自动拾取半径（`CollectorPickupRadius`）保持一致；
- 满魔力时多出来的魔力照常"浪费掉"（`cureMp2` 自己会钳上限），不会堆在地上等魔物，也不会一直不消失；
- 仍然不会吸"还允许魔物吸"的魔力（`EN` 位存在就跳过）——那类球由 `ProtectCollectorMana()` 先剥掉 EN，再被这里吸走。

**两种模式的口径**：真空层每帧都跑（`KnightInCradleBehaviour.Update`）。骑士模式下被吸的是**宿主诺艾尔的真实魔力**
（和 21.5 之前的"直接记账"效果等价），切回诺艾尔后魔力保留；诺艾尔模式就是吸给她本人。

**修正后的表现**：破坏魔力草 → 魔力像原生一样正常弹出/落地 → 诺艾尔走近 3 格内就被吸走（**魔力条满也吸**，
多余的浪费），魔物始终吸不到。

验证：`build=2026-09-22.6`，DLL SHA256 `359E00CEDE515801…`（9,035,264 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 21.7 四稿：魔力草改为"不掉球、直接回诺艾尔 50 MP"（build=2026-09-22.7）

**实测反馈**：三稿的"照常掉落 + 剥掉 EN 位"方案下，**魔物仍然会抢到魔力球**。
虽然 `NelEnemy.canGetMana` 确实要求 `MANA_HIT.EN` 位（没 EN 位就吸不了），
但原生在超时会 `mana_hit |= MANA_HIT.ALL`（把 EN 加回来）等时机，与我们每帧剥位的时序存在窗口，
实战中仍会被抢。**用户决定：干脆不生成魔力球，破坏魔力草直接回复诺艾尔 50 MP。**

**改法**：

| 位置 | 改动 |
|---|---|
| `CharmEffects.CollectorManaWeedMp`（新增常量） | `50f` —— 破坏一棵魔力草给的魔力值 |
| `CharmEffects.ManaWeedSplashPrefix` | 装备蜂群集结时**跳过原生**（`return false`，不生成落地魔力球），先 `GrantNoelMana(50f)`；只有诺艾尔不可用（拿不到 PR）时才兜底走原生落地，并保持"仅诺艾尔可吸" |
| `KnightInCradleBehaviour.GrantNoelMana` | 返回值由 `void` 改为 `bool`（是否真的记上账）；**诺艾尔模式改为直接读写她的真实 `maxmp/mp` 字段**，不再依赖快照（快照是骑士模式的机制，诺艾尔模式下可能残留旧值）；骑士模式仍走"快照 + 真实字段一起写"的老路 |

满魔力时按原规则浪费（`min(maxmp, cur + 50)`），落地魔力球则完全不再产生，因此没有"被魔物抢走"的空间。

**保留的部分**：`ProtectCollectorMana()`（每帧把落地魔力剥成"仅诺艾尔可吸"）与
`VacuumCollectorMana()`（3 格内满魔力也吸）**都留着**——它们现在服务于**其它来源**的魔力球
（例如魔物死亡掉落的魔力）；魔力草这条线已经不再产球，两者互不冲突。

**修正后的表现**：装蜂群集结破坏魔力草 → 不出现魔力球，诺艾尔魔力 +50（满魔力时按上限钳制，多余浪费）。

验证：`build=2026-09-22.7`，DLL SHA256 `FC47D94FC8C844A3…`（9,035,776 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 21.8 五稿：数值定为每棵草 +30 MP（build=2026-09-22.8）

**反馈**：破坏魔力草时不是加固定值，而是**魔力条一次顶满**。

**排查结论（先确认不是重复结算）**：一棵草确实只结算一次——
`M2ManaWeed.ApplyDamageFrom`（`nel/M2ManaWeed.cs:157`）在正常破坏路径上只调一次
`SplashMana`（`:187`），而 `SplashManaInit(MANA_HIT.NOUSE, ...)`（`:182`）因为 flag 是 NOUSE 不会触发溅射（`:207` 的判断）；
破坏后 `SplashManaInit` 会把 `time` 置为 `charge_time_min..max`（`:211`），此后 `applyHpDamage` 在 `time > 0` 期间直接返回 0（`:147`），
所以同一棵草在枯萎/重生期间打不动、也不会重复给魔力。

**真正的原因是指令里的数值偏大**：四稿按需求写的常量是 **50 MP/棵**，而 AIC 上限是 200——
只要当前魔力 ≥150，一棵草就把条顶满，观感就是"直接回满了"。

**改动**：`CharmEffects.CollectorManaWeedMp` 由 `50f` 改为 **`30f`**，
对齐原生一棵草的量级（原生总量是 `(20 + xors(11)) × ManaWeedRatio()` ≈ 20~30，还会随夜间比例缩放）。

**口径确认**（写在常量注释里，避免以后再误会）：

- **每棵草结算一次**；一次攻击同时打到多棵草（区域技/多段技）就按棵数累加；
- 满魔力时按上限钳制，多出来的浪费掉；
- 仍然不生成落地魔力球，所以魔物没有可抢的东西。

验证：`build=2026-09-22.8`，DLL SHA256 `B895A30BA048C408…`（9,035,776 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 21.9 六稿（收官）：诺艾尔的蜂群集结只保留「自动拾取 + 蜂巢怪不打」（build=2026-09-22.10）

**用户决定**：魔力那一整套（掉落球 / 只给诺艾尔 / 满魔力也吸 / 固定回魔）反复出问题，**诺艾尔侧直接不要了**，
蜂群集结只保留两项效果。以下是最终形态，**21.6～21.8 关于魔力的三稿在诺艾尔侧全部作废**（小骑士侧回到模组原有实现）。

**诺艾尔侧（最终）**：

| 效果 | 实现 |
|---|---|
| 蜂巢怪不打 | `HiveNeutralActive()`（按当前操控角色判断）+ 每帧 `ClearHiveEnemyAim()`；攻击蜂巢魔物 → `TriggerHiveAggro()` 全房解除中立 |
| 3 格自动拾取 | `TickCollectorAutoPickup(px, footY)`，走原生 `NelItemManager.executePickUp`，判据仍是游戏自己的 `canTalkable` + `getItemCapacity` |
| ~~魔力草相关~~ | **不做**：诺艾尔模式下破坏魔力草完全走原版（正常掉落、谁都能吸） |

**代码改动**：

1. `CollectorManaGuardActive()` 恢复为 `IsKnightMode && IsEquipped(CollectorId)`——**只服务小骑士侧**（回到模组原有行为）；
2. `ManaWeedSplashPrefix` 恢复原来的金额算法 `(20 + rand(0..10)) × NightCon.ManaWeedRatio()`，
   仍是"小骑士破坏 → 不生成落地魔力、直接给后台诺艾尔记账；诺艾尔不可用时兜底走仅诺艾尔可吸的落地魔力"；
3. 删掉 `VacuumCollectorMana`（21.6 新增）与它的每帧调用；`TickNoelCharmEffects()` 只剩三件事：
   换图重算蜂巢标记、`ClearHiveEnemyAim()`、`TickCollectorAutoPickup(pr.x, pr.mbottom)`；
4. **顺手修掉一个连带 bug**：`TickCollectorAutoPickup` 原先用 `CollectorManaGuardActive()` 做门控，
   而它现在是"小骑士专有"——若不改，诺艾尔的自动拾取会静默失效。已改为 `IsEquippedForCurrentPlayer(CollectorId)`。

> 教训记一笔：这一族效果的"门控函数"混用了两种语义（**魔力保护的开关** vs **蜂群集结的开关**）。
> 两者在 21.5 之后被合并成一个 `IsEquippedForCurrentPlayer` 判断，等到 21.9 把魔力保护改回小骑士专有时，
> 自动拾取就被一起带走了。**以后新增/修改门控函数时，务必检查所有调用点是否仍符合语义**。

验证：`build=2026-09-22.10`，DLL SHA256 `DBEE6CA0B4303002…`（9,035,264 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

#### 21.10 七稿：放行"汚染体（雷雨 OverDrive）"的苏醒（build=2026-09-22.22）

**现象**：带蜂群集结时，某些魔物**不再变成汚染体**（AIC 里叫"汚染体"，游戏内文案 `Weather_desc_thunder`：
「一部のエネミーが汚染体に変化する」），结果是这些魔物停在未苏醒状态、**打不动**。

**根因**：汚染体化由 `OverDriveManager.runPre` 推进（`nel/OverDriveManager.cs:202-213`），条件是
`thunder_overdrive_t > 0 && !disappearing && **En.is_awaken**` —— **必须先苏醒**。
而蜂群集结的中立实现是"压住 `NAI.awakeInit`"（`NaiAwakeInitPrefix` 返回 false）＋清锁定目标，
于是这类魔物永远不苏醒 → 永远不转化 → 并且未苏醒状态无法被攻击。

**修正**：`NaiAwakeInitPrefix` 里加例外——若该魔物**即将因雷雨变成汚染体**就放行苏醒：

```csharp
if (HiveNeutralActive())
{
    if (WillThunderOverdrive(__instance.En)) return true;   // 先让它转化
    return false;
}
```

判据 `WillThunderOverdrive(en)` = `en.getOdManager() != null && od.thunder_overdrive`
（`OverDriveManager.thunder_overdrive` 由召唤器/天气在生成时置位，见 `SummonerPlayer.cs:869,1833`、`NightController.cs:695`）。

**转化后仍然保持中立**：我们照旧拦 `NAI.AimPr` 赋值并每帧 `ClearHiveEnemyAim()`，
所以汚染体不会打诺艾尔，但**可以被诺艾尔打了**（这正是需求）。

验证：`build=2026-09-22.22`，DLL SHA256 `FD8CA789BA9586DA…`（两份安装已同步；只覆盖 DLL）。

> **八稿修正（同日，build=2026-09-22.23）**：一稿只放行"苏醒"仍不转化，改为**把汚染候选整体排除在中立之外**
> （放行苏醒 + 放行锁定目标 + 每帧 `ClearHiveEnemyAim` 跳过它），实测转化恢复正常；
> 当时的诊断日志已在 2026-09-22.24 删除（本功能不再有任何日志）。

---

## 22. 诺艾尔的护符第二部分（3）：坚硬外壳 —— 次数血（2026-09-22，build=2026-09-22.11）

**需求（与小骑士完全不同）**：

1. 诺艾尔的血条变成"**次数血**"：血量上限 = `floor(当前最大生命 / 35)`；
2. 掉血后 **2 秒内**血量不会再次降低。

### 22.1 机制

| 环节 | 做法 |
|---|---|
| 上限换算 | `maxhp` 字段直接换成次数：`hitMax = max(1, floor(真实最大生命 / 35))`（150 → 4 次，200 → 5 次）。AIC 血条是比例驱动的，所以自动显示成"4 格血" |
| 真实上限寄存 | 次数血把 `maxhp` 字段改小了，读档时无法反推真实值 → 激活时把真实上限写进 COOK SF 的 `kic_noel_sturdy_maxhp`（随存档一起序列化），卸下时清零 |
| 切换时的血量 | 装备：`hp = clamp(ceil(真实hp × hitMax / 真实上限), 0, hitMax)`（还剩血就至少 1 次）；卸下：`真实hp = clamp(ceil(剩余次数 × 真实上限 / hitMax), 0, 真实上限)`——伤害按比例保留，**不会"卸下即回满"** |
| 每帧维护 | 装备期间把 `maxhp` 钳成 `hitMax`、`hp` 钳进 `[0, hitMax]`（被别处改写也拉回来） |
| 一次伤害 = 掉 1 次 | 前缀补丁挂在 `M2Attackable.applyHpDamage`（玩家受伤干线的最后一站：`M2PrADmg.applyHpDamageSimple` → `GSaver.applyHpDamage` → `Pr.applyHpDamage`），**只对本地诺艾尔生效**：`val = 1` |
| 2 秒免掉 | 同一个前缀里用 `Time.unscaledTime` 计时：距上次掉血不足 2 秒 → `val = 0`（血量不降，其它受击表现照旧） |
| 读档 | `CharmSave.RestoreAfterLoad()` → `CharmEffects.ResetNoelSturdyOnLoad()`；下一次每帧 tick 从 SF 里的真实上限重新激活（存档里的 hp 本来就是次数量，直接沿用） |
| 死亡 | 次数掉到 0 → 走 AIC 原生 `initDeath` / GAMEOVER 流程（不另写死亡逻辑）；长椅复活后回满次数 |

与 CombatGuard 的 `HpDamagePrefix` 挂在同一个方法上互不冲突：那个只在骑士模式拦截（返回 false），
诺艾尔模式下返回 true 让这边生效。

### 22.2 边界与已知取舍

1. **治疗按次数上限钳制**：次数血状态下回血最多补满到 `hitMax`（例如 +30 也只会到 3/3），符合"次数血"的语义；
2. **伤害飘字仍显示原值**：受击计数器/飘字用的是伤害计算阶段的原值（例如 -30），而血量只掉 1 次——刻意保留的观感差异；要改成显示 1 需另接 `setDamageCounter`；
3. **骑士模式下不维护**：切到小骑士时次数血状态原地保留（宿主字段本就是快照机制的一部分），切回诺艾尔继续生效；
4. **真实最大生命 > 255 会被 SF 截断**：COOK SF 是 0~255 的字节；AIC 正常流程下最大生命在 150~200，够用。若将来有超过 255 的成长，需要换寄存方案（例如走 SVD 自定义字段）；
5. **上游把 `val` 算成 0 的情况**（例如缓冲条 `GSaver` 完全吸收、或 `MDAT.getPrDamageVal` 判定为 0）不会掉血——与原生一致。

### 22.3 验证方法

1. 诺艾尔装上坚硬外壳 → 血条变成 `floor(最大生命 / 35)` 格（150 → 4）；坐着/起身不影响；
2. 挨一下：掉 1 格；**2 秒内**再挨：血量不动（受击演出照旧）；
3. 超过 2 秒再挨：再掉 1 格；掉到 0 → 正常战败与长椅复活，复活后回满；
4. 卸下护符：血条回到正常数值并按剩余次数折算（例：剩 2/4 → 75/150）；
5. 带着护符存档 → 读档：仍是次数血，真实上限不丢（`kic_noel_sturdy_maxhp` = 150）。

> **二稿（同日，build=2026-09-22.12）**：折算除数由 **45 → 35**（`CharmEffects.SturdyHpPerHit`），
> 即 150 上限 → 4 次、200 上限 → 5 次。其余机制与 22.1/22.2 完全不变。

验证：`build=2026-09-22.12`，DLL SHA256 `3FD33ECE33C6EF3E…`（两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 22.4 三稿：改为"**伪次数血**"（build=2026-09-22.13）

用户重新定义了逻辑，**22.1～22.3 的规则作废**，以本节为准：

| 项目 | 规则 |
|---|---|
| 佩戴瞬间 | `maxhp` 直接 `floor(maxhp / 35)`（至少 1）；当前 hp 按比例折算成次数（还剩血就至少 1 次） |
| 伤害判定 | 前缀补丁（`M2Attackable.applyHpDamage`）：`val ≤ 20 → 0`，`val > 20 → 1` |
| 受击之后 | 无论记成 0 还是 1，**立刻** `NoDamage.Add(120f)` —— 用 AIC 原生无敌系统给 2 秒全属性无敌。因此"2 秒内不会再掉血"由**游戏自己的无敌判定**实现，模组不再另做计时器 |
| 卸下瞬间 | 把佩戴时寄存的真实 hp/maxhp **原样写回**（不再按剩余次数折算）；佩戴期间掉的血在卸下时被丢弃——这是用户要求的语义 |
| 寄存 | 真实 hp / maxhp 写进 COOK SF 的 `kic_noel_sturdy_maxhp` / `kic_noel_sturdy_hp`（随存档序列化），卸下时清零 |
| 读档 | `RestoreAfterLoad()` → `ResetNoelSturdyOnLoad()`；下一次每帧 tick 从 SF 取回真实值并重新激活（存档里的 hp 已经是次数量，直接沿用） |

实现位置（全在 `src/CharmUi/CharmEffects.cs` 的"护符3 坚硬外壳"段）：

- 常量：`SturdyHpPerHit = 35`、`SturdyDamageThreshold = 20`、`SturdyInvincibleFrames = 120f`（帧，60fps）；
- `SturdyHpDamagePrefix`：伤害规则 + 给无敌；
- `TickNoelSturdyCharm`：佩戴/卸下的即时换算与佩戴期间的钳制；
- `PrNoDamageField`：`M2Attackable.NoDamage` 是 **protected** 字段（`unsafeAssem/m2d/M2Attackable.cs:1411`），用反射取；
  无敌时长走 `M2NoDamageManager.Add(NDMG.DEFAULT, time)`，它会把 `active_bits |= 16383`（点亮全部 14 个 key）= 全属性无敌。

注意事项：

1. 阈值比较的是**最终伤害**（已经过 `MDAT.getPrDamageVal`、非满血减伤、`GSaver` 缓冲之后、传进 `applyHpDamage` 的那个值）；
2. 记 0 的攻击**仍然会**打断凝聚/触发受击演出（我们只改血量，不改受击流程）；
3. 无敌是全属性的，2 秒内连"原本可穿无敌"的少数攻击也进不来；
4. 佩戴期间的回血最多补满到次数上限（`maxhp` 已经是次数），且卸下时会回到佩戴前的血量。

验证：`build=2026-09-22.13`，DLL SHA256 `9851E3781ED44D3C…`（9,036,800 B，两份 0.30g 安装已同步；只覆盖 DLL，未动素材）。

### 22.5 四稿：补 HUD 刷新（build=2026-09-22.14）

**现象**：佩戴/卸下坚硬外壳后，血条要**先切小骑士再切回诺艾尔**才变化。

**原因**：我们直接改的是 `hp/maxhp` 字段，而 AIC 只在受伤/治疗流程里调 `UIStatus.fineHpRatio(…)`
重算血条比例（`UIStatus.cs:1146`，内部把 `hp_ratio = Pr.hp_ratio` 并置 `redraw_hp/redraw_bar_num`）。
不调用它，血条就一直用旧比例画，直到某次全量刷新（切角色正好会触发）。

**修正**：新增 `CharmEffects.RefreshNoelHudHp()`（调 `UIStatus.Instance.fineHpRatio(false, false)`），
在**佩戴换算、卸下还原、以及佩戴期间的钳制**这三处改完字段后立刻调用，血条即时更新。

验证：`build=2026-09-22.14`，DLL SHA256 `B3FD8F4B0F3EA4E1…`（两份安装已同步；只覆盖 DLL）。

### 22.6 五稿：血条**数字**重绘（build=2026-09-22.15）

**现象**：22.5 的刷新之后，仍然"要切一次小骑士才变"。

**真正的原因**：AIC 的 HUD 血条分两部分——**比例条**（`UIStatus.hp_ratio`）和 **"hp/maxhp" 数字文本**
（`UIStatus.redrawBarNumber`，`UIStatus.cs:2122-2137` 里拼 `(int)get_hp() + "/" + (int)get_maxhp()`）。
伪次数血大多是"满血 → 满血"（`hp_ratio` 都是 1.0），所以 `UIStatus.fineHpRatio()` 里
`num2 = 新比例 - 旧比例` 为 0，**一个重绘标记都不会置**；而原版受伤流程走的是 `use_cushion=true` 分支，
把差值塞进缓冲条、同样不置 `redraw_bar_num`。于是数字文本一直显示旧值，直到某次全量刷新（切角色）。

**修正**：`RefreshNoelHudHp()` 除了 `fineHpRatio(false,false)` 之外，再**直接置脏两个 public 标记**：

```csharp
st.redraw_hp = true;        // 血条本体
st.redraw_bar_num = true;   // "hp/maxhp" 数字文本
```

并在**伤害前缀里也调用一次**（因为原版受伤流程不会置 `redraw_bar_num`，掉血后数字同样会滞后）。

验证：`build=2026-09-22.15`，DLL SHA256 `27C0B9F45BC7D1C9…`（两份安装已同步；只覆盖 DLL）。

---

## 23. 诺艾尔的护符第二部分（4）：灵魂捕手（2026-09-22，build=2026-09-22.17）

**需求**：诺艾尔用法术攻击敌人时，立即获得 **6 MP**。

**实现**：接在已有的敌人受伤前缀 `EnemyApplyDamagePrefix`（`NelEnemy.applyDamage(NelAttackInfo, bool)` 的 prefix，
`nel/NelEnemy.cs:1796` 的 2 参重载会转发到 3 参重载）里，判断 `Atk.Caster is PRNoel` 之后调用
`TryGrantSoulCatcherMp(Atk)`：

| 环节 | 判据 |
|---|---|
| "法术" | `Atk.PublishMagic != null` 且 `kind` 在**诺艾尔法术段** `MGKIND 0..FLOWERYCIRCLE`（WHITEARROW / FIREBALL / DROPBOMB / THUNDERBOLT / POWERBOMB / WATERSHARD / BLACKHOLE / FLOWERYCIRCLE）。她的物理技是 `PR_*`（9000+），**不算** |
| 模式 | **只在诺艾尔模式**结算：骑士模式里小骑士的攻击也把 `Caster` 设成诺艾尔，不隔离会连带改变小骑士那套（小骑士的灵魂捕手另有实现） |
| 去重 | 同一次命中同一帧只结算一次（`(Atk 引用, Time.frameCount)`） |
| 加魔 | 走现成的 `KnightInCradleBehaviour.GrantNoelMana(6f)`（会钳到上限），并调 `RefreshNoelHudMp()` 让魔力条与"mp/maxmp"数字立刻重绘（同 22.6 的道理） |

**多目标**：一次法术同时命中 3 个敌人 = 3 × 6 = 18 MP（按命中次数结算）。

验证：`build=2026-09-22.17`，DLL SHA256 `779441964E5E1989…`（两份安装已同步；只覆盖 DLL）。

> **二稿（同日，build=2026-09-22.18）——挂载点错了，已改**：
> 实测纯白之箭 / 魔法霰弹 / 地面炸弹命中敌人**都不回魔**。
> 原因：AIC 的法术伤害走的是 **3 参重载** `nelM2Attacker.applyDamage(Atk, ref hittype, false)`
> （`MGContainer.CircleCast`，`MGContainer.cs:636`），而**26 个敌人子类各自 override 了这个虚方法**
> （NelNSlime / NelNFox / NelNMush …），所以挂在 2 参入口 `NelEnemy.applyDamage(Atk, bool)` 上的补丁
> 在法术路径上根本不会被调用。
>
> 改法：把补丁挂到**非虚的汇聚点** `MGContainer.CircleCast`（postfix，`MGContainer.cs:473`）——
> `MgWhiteArrow` / `MgDropBomb` / `MgWaterShard` / `MgFireBall` / `MgBurst` … 全部走它。
> "法术"的判据同时改为**游戏自己的魔力消耗表** `MKind.getReduceMp(Mg.kind) > 0`
> （消耗魔力的一律算魔法，诺艾尔不耗魔的近战不算），命中判据用返回的 `HITTYPE.HITTED_EN`。
> 一次施法命中敌人结算一次（不按目标数）。

验证：`build=2026-09-22.18`，DLL SHA256 `A1794DC616EDA274…`（两份安装已同步；只覆盖 DLL）。

---

## 24. 诺艾尔的护符第二部分（5）：萨满之石（2026-09-22，build=2026-09-22.20）

**需求**：诺艾尔造成魔法伤害时，最终伤害 **+25%**（含魔法与**魔法霰弹**）。

**实现**：复用 23 节建立的"法术判据"`IsPlayerMagicKind(kind)`（`MKind.getReduceMp > 0` 或 `PR_SHOTGUN`），
在同一个汇聚点 `MGContainer.CircleCast` 上加一对**前缀/后缀**：

```csharp
// 前缀：把这一发的基准伤害临时抬高
var st = new ShamanBoostState { Hp0 = Atk.hpdmg0 };
if (st.Hp0 > 0) Atk.hpdmg0 = Mathf.FloorToInt(st.Hp0 * 1.25f + 0.5f);   // 与骑士侧 ScaleSpellDamage 同样的取整
__state = st;
// 后缀：原样还原，避免污染可复用的 Atk
Atk.hpdmg0 = __state.Hp0;
```

**为什么改 `hpdmg0`（int 字段）而不是 `_hpdmg`**：
`AttackInfo._hpdmg` 是**只读属性**（`unsafeAssem/m2d/AttackInfo.cs:48`），
取值规则是 `hpdmg_current != -1000 ? hpdmg_current : hpdmg0`；
而 `CircleCast` 对每个命中目标都会 `shuffleHpMpDmg(..., Atk.hpdmg0, ...)` 用 `hpdmg0` 重算 `hpdmg_current`
（`:103`）。所以抬 `hpdmg0` 就能让**每个目标**的最终伤害都 +25%；没走 shuffle 的路径则直接读 `hpdmg0`，同样生效。

只对**诺艾尔模式 + 诺艾尔放的法术**生效；小骑士侧的萨满之石仍是原来的 `ScaleSpellDamage`（+25%，走骑士自己的伤害计算），互不干扰。

验证：`build=2026-09-22.20`，DLL SHA256 `16AFE190BB80604A…`（两份安装已同步；只覆盖 DLL）。

---

## 25. 诺艾尔的护符第二部分（7）：冲刺大师（2026-09-22，build=2026-09-22.24）

**需求**：① 走路的动画换成跑步动画；② 佩戴期间诺艾尔的移动一律按跑步；③ 跑步速度降低 20%。

**实现**：AIC 里"跑 vs 走"的两处判据都读 `M2MoverPr.isRunning()`：

| 用途 | 出处 |
|---|---|
| 速度 | `calcWalkSpeed`：`isRunning() ? runSpeed : walkSpeed`（`unsafeAssem/m2d/M2MoverPr.cs:1477`） |
| 姿势 | `AnimationShufflerNoel`：`isRunning() ? "run" : "walk"`（`nel/AnimationShufflerNoel.cs:677`） |

所以做法只有两条：

1. **强制"始终跑步"**：`M2MoverPr.isRunning()` 上挂 postfix，佩戴期间对本地诺艾尔直接 `__result = true`
   （同时满足需求 ① 动画与 ② 移动）；
2. **跑速 -20%**：`runSpeed` 是 `M2MoverPr` 的 protected 字段，用反射在**每帧 tick**里钳到
   `原值 × 0.8`（原值在佩戴瞬间寄存，卸下/切模式还原）。

```csharp
public const float DashmasterRunSpeedMult = 0.8f;   // 0.17 → 0.136，仍快于走路 0.085
```

只对诺艾尔模式 + 本地诺艾尔生效（`__instance is PRNoel`），敌人/其它 mover 不受影响。

验证：`build=2026-09-22.24`，DLL SHA256 `14EF47AEB1EB4799…`（两份安装已同步；只覆盖 DLL）。

### 25.1 后续同批完成（build=2026-09-22.31）

| 护符 | 效果 | 实现要点 |
|---|---|---|
| 8 飞毛腿 | 走路与跑步速度 +20% | `M2MoverPr.walkSpeed/runSpeed` ×1.2；与冲刺大师**统一成一份基础值再连乘**（见下），倍率 `SprintmasterSpeedMult = 1.2`（先试过 1.1 手感不明显、1.5 过强） |
| 9 幼虫之歌 | ① 受到伤害时获得 20 MP；② 不会被虫墙/虫巢抓取 | ① 在伤害前缀 `SturdyHpDamagePrefix`（`M2Attackable.applyHpDamage`）里用**原始伤害 > 0** 触发 `TryGrantGrubsongMp()`（同帧去重），因此即便被坚硬外壳改写成 0/1 也照样回魔；② 对本地诺艾尔 patch `PR.canPullByWorm` → false（`M2WormTrap.cs:111,148` 用它决定是否拉扯玩家） |

**速度类护符的统一口径**（冲刺大师 0.9 × 飞毛腿 1.2，都改同一对字段）：

```
目标走速 = 基础走速 × 飞毛腿倍率
目标跑速 = 基础跑速 × 飞毛腿倍率 × 冲刺大师倍率
```

基础值在"首次佩戴任一速度护符"时寄存（反射读 `walkSpeed/runSpeed`），两个都卸下时写回；
若各改各的，第二个护符会把第一个的结果当成"原值"，乘出来是错的。

验证：`build=2026-09-22.31`，DLL SHA256 `347614A5DD6A3126…`（两份安装已同步；只覆盖 DLL）。

### 25.2 冲刺大师后续修正：蹲伏移动不再强制跑步（2026-09-23，build=2026-09-23.12）

**症状**：佩戴冲刺大师后按蹲伏键左右移动，仍播放跑步动画（应为 `crawl`）。

**原因**：`setDefaultPose` 的判断顺序是"先看跑步、后看蹲伏"——

```csharp
else if (this.Pr.isRunning())          // AnimationShufflerNoel.cs:669-671 —— 无条件 "run"
    dep_pose = "run";
bool flag2 = this.Pr.view_crouching || this.Pr.forceCrouch(false, false);
... (this.Pr.isRunning() ? "run" : (flag2 ? "crawl" : "walk"))   // :677
```

`:669` 在 `flag2` 之前，所以只要 `isRunning()` 为真，蹲伏也会被摆成跑步姿势。

**修正**：`DashmasterIsRunningPostfix` 在强制 `__result = true` 之前先判蹲伏，蹲伏时直接放行原值：

```csharp
if (pr.view_crouching || pr.forceCrouch(false, false)) return;   // 蹲伏/爬行：不强制跑步
```

放行后由游戏自己的逻辑给出 `crawl`：原版进入爬行时已经 `running = 0` 并清掉 `TO_*_RUN`
（`unsafeAssem/m2d/M2MoverPr.cs:424-426`）。速度也不受影响——`calcWalkSpeed` 的蹲伏分支
（`:1471-1473`，`walkSpeed × 0.75`）本来就优先于 `isRunning()` 的跑步分支（`:1477`）。

验证：`build=2026-09-23.12`，DLL SHA256 `4D7934BFDA7AB490…`（两份安装已同步；只覆盖 DLL）。

---

## 26. 诺艾尔的护符第二部分（8）：10～14 号护符（2026-09-22，build=2026-09-22.43）

| 护符 | 效果 | 实现要点 |
|---|---|---|
| 10 蜕变挽歌 | ① 轻攻击（`PR_PUNCH`）时向前发射剑气（贴图 `slashes_effect0001`，速度/大小与小骑士一致，**射程 8 格**，命中 **18 真实伤害**）；② 不会被虫墙/虫巢抓取 | 剑气在 `M2PrSkill.executeSmallAttack` 后缀里由模组自建 mesh 票据绘制；伤害走真实伤害通道，与诺艾尔血量/难度曲线无关；虫墙免疫与幼虫之歌共用 `PR.canPullByWorm` 前缀 |
| 11 坚固心脏 | 生命上限 +120 | 佩戴时把"基础上限"寄存 SF（防止读档/反复佩戴重复累加），新增的上限立刻补成当前血量 |
| 12 坚固贪婪 | 与小骑士**完全一致**，且两边同时佩戴时效果**可叠加** | `GreedStacks()` 统计小骑士 + 诺艾尔各自的层数：背包容量 +150×层数，掉率/星级/金币/宝箱按层放大；小骑士的伤害惩罚下限 0.5（最多 -50%）。容量用**幂等重算**（SF 记 `kic_greed_stack` 上次层数反推基础容量）＋每帧 `SyncGreedCapacity()`，避免"卸下不恢复" |
| 13 坚固力量 | 诺艾尔以下招式最终伤害 +25%：轻攻击 Punch、魔法霰弹 Shotgun、旋风斩击 Cyclone Slash、彗星俯冲 Comet Dive、突进冲击 Dashpunch、凌空横斩 Airpunch、会心重击 Fatal Smash、轮舞斩击 Dancing Slash | 与萨满之石（法术 ×1.25）**连乘**，互不覆盖 |
| 14 法术扭曲者 | 诺艾尔使用任意魔法时，**起手消耗 -10**；**蓄力消耗也 -10**（二者都不低于 1） | 见下方"三稿"。 |

验证：`build=2026-09-22.43`，DLL SHA256 `DB9996D011071A71…`（两份安装已同步；只覆盖 DLL）。

### 26.1 法术扭曲者三稿：扣魔数额**本身**减 10（build=2026-09-22.45）

**问题**：一稿之二只挂了 `PR.applyBurstMpDamage`（爆发型魔法起手）与
`M2PrSkill.digestShotgunHoldMp`（霰弹蓄力结算），测试时"咏唱魔法该扣多少还是扣多少"；
三稿一版改成"先扣满、扣完返还 10"，实测（纯白之箭 20 MP）表现为**先扣 20、放完再退 10**，
不是想要的"扣的时候就是 10"。

**查证**（`_tmp_aic_src_new/Assembly-CSharp/nel/M2PrSkill.cs`）：诺艾尔魔法真正扣魔的三个入口，
最终都走**同一个重载** `PR.applyMpDamage(int val, bool force, AttackInfo Atk, bool use_quake, bool calc_gsaver)`
（`nel/PR.cs:3297`）：

| 入口 | 触发时机 | 扣魔表达式 |
|---|---|---|
| `M2PrSkill.explodeMagic` (`:3659`) | 咏唱完毕 / 松手施放 | `applyMpDamage((int)mp_hold, …)` |
| `M2PrSkill.killHoldMagic(MANA_HIT,…)` (`:3875`) | 被打断 / 取消施法 | `applyMpDamage((int)(mp_hold - mp_overhold), …)` |
| `M2PrSkill.digestShotgunHoldMp` (`:2375`) | 霰弹每次命中结算蓄力 | `applyMpDamage(num2, …)` |

**做法**：给这个重载挂前缀，把 `val` 直接减 10（不低于 1）——**是扣魔时少扣，不是扣完返还**：

- 标记 `_noelSpellTwisterCostScope`：三个入口各自在前缀置位、后缀清除；
- `applyMpDamage` 前缀读一次即消费该标记，保证同一次结算只减一次；只对本地诺艾尔
  （`ReferenceEquals(__instance, pr)`）且**非小骑士模式**生效；
- 不动 `mp_hold`/`reduce_mag`，所以蓄力条与魔法威力（`MDAT.initShotGun` 用
  `X.ZPOW(mp_hold, reduce_mp)` 缩放伤害）都保持原样；
- `PR.applyBurstMpDamage`（爆发型魔法起手，走另一条 7 参重载）仍单独在前缀改参数。

### 26.2 法术扭曲者四稿：连"咏唱中的待扣预算"一起减 10（build=2026-09-22.46）

**问题**：三稿只改了**实际扣魔**（`applyMpDamage` 的 val）。实测仍然"释放之后返还"——
因为 AIC 在**咏唱时**就把"这一发要花多少"画在魔力条上：

- `UIStatus.cs:743`：`num2 = (int)X.Mn(Pr.get_maxmp(), Pr.Skill.getHoldingMp(false))`，
  赋给 UI 的 `mp_hold` 字段，绘制时（`:2271`/`:2352`）作为魔力条上的暗色"蓄力段"；
- `PR.getCastableMp()`（`PR.cs:5312`）= `X.Mx(mp - Skill.getHoldingMp(false), 0)`，
  即"还能用来施法的魔力"，魔力条的亮色段按它画。

也就是说：咏唱中预算显示 20、实际只扣 10 → 魔力条在释放瞬间"涨回 10"，看起来就是返还。

**做法**：给 `M2PrSkill.getHoldingMp(bool)` 挂后缀，对本地诺艾尔把返回值减 10（不低于 0）。
于是咏唱中的**预算**与释放时的**扣魔**都是 10，魔力条长度守恒、不再有跳变；
`getCastableMp()` 也随之认为多出 10 点可用魔力，与"少花 10"一致。

由于 `explodeMagic` 在"过充槽持有"分支里正是用 `getHoldingMp(false)` 当扣魔金额
（`:3651`），这一路已经被后缀减过，所以标记用三态
（`0` 不管 / `1` 在 `applyMpDamage` 里减 / `2` 已减过别再减），避免重复减免。

例：纯白之箭 20 MP → 咏唱时预算就是 10、释放时扣 10，中途没有任何返还。

验证：`build=2026-09-22.46`，DLL SHA256 `3CE694678A1F8F0C…`（两份安装已同步；只覆盖 DLL）。

---

## 27. 诺艾尔的护符第二部分（9）：稳定之体（id 15，2026-09-22，build=2026-09-22.47）

需求：① 诺艾尔不会因为触碰魔物而摔倒；② 免疫风力；③ 免疫黏滑地面对移动的干扰。

三条效果各自的原生机制（0.30g 反编译实证）与挂点：

| 效果 | 原生机制 | 挂点 |
|---|---|---|
| ① 触碰魔物不摔倒（**无伤害**的那条，主机制） | 诺艾尔的**身体**撞到魔物时走 `PR.moveByHitCheck`（`:5233`），里面按魔物种类调 `addEnemySink(EnemyAttr.getSinkRatio(enemy), …)`（`:5243`）给 `PR.RCenemy_sink` 累加"被撞倒计量"；`PR.checkEnemySink`（`:3692-3715`）在计量 ≥ **6** 时 `changeState(PR.STATE.ENEMY_SINK)` —— 这就是"撞到魔物摔倒"，**完全不经过伤害管线、不掉血**。跑动状态下累加量还会 ×2.3（`:5293-5296`），所以"跑着撞上去"特别容易摔；`runEnemySink` 也要求她在地面且非蹲伏（`:3679-3681`） | 只在 `PR.moveByHitCheck` 这一路屏蔽累加：该函数前缀置 `_stableBodyHitSinkScope`、后缀清除，`PR.addEnemySink` 前缀在该作用域内直接 `return false`。**物理推挤照旧**；其它 sink 来源（醉酒 `M2SerLisP_DRUNK`、被魔物驮着、硬撞 `Skill.moveByHitCheckHard` 之外的调用）不受影响 |
| ①-b 带伤害的"接触攻击"也不摔倒（附加，非需求主项） | 魔物的"身体接触攻击"是 `MGKIND.TACKLE` 的魔法（`NelEnemy.tackleInit`，`:3495` 建 Mg、`:3499` `magicItem.Atk0 = Atk`）；命中时 `MGContainer.CircleCast` 会 `Atk.PublishMagic = Mg`（`:498`）。这种小击退伤害在 `M2PrADmg.applyDamage` 里会走 `changeState(PR.STATE.DAMAGE_LT)`（`:1315`，就是"摔倒/dmg_t"姿势） | `M2PrADmg.applyDamage(NelAttackInfo, ref HITTYPE, bool, string, bool, bool)` 前缀记下"本帧这次伤害是 TACKLE 接触伤害"，再由 `PR.changeState(PR.STATE)` 前缀拦掉紧接着的 `DAMAGE_LT` / `DAMAGE_LT_KIRIMOMI` 切换。**HP 伤害照常结算**，只是不进入摔倒动作 |
| ② 免疫风力 | 风压等级 `PR.getWindApplyLevel`（`:2457`）决定受风强度，实际推力在 `PR.applyWindFoc`（`:2517`）的 `Phy.addFoc(FOCTYPE.KNOCKBACK,…)` | 两个入口都加前缀：等级直接返回 0、推力整体跳过（与小骑士模式的 `CombatGuard.PrWindFocPrefix` 同款） |
| ③ 免疫黏滑地面（冰面） | `M2FootManager` 踩到 `foottype == "ice"` 时调 `M2Phys.addOnIce(false, 3f)`（`M2FootManager.cs:740`），把 `t_ice` 拉高；`t_ice > 0` 会把横向摩擦力按 0.015 倍衰减（`M2Phys.cs:591/641/791`），表现为打滑、难以急停 | `M2Phys.addOnIce(bool, float)` 前缀：若这个 `M2Phys` 属于本地诺艾尔（`M2Phys.Mv` 反查）则跳过 → `t_ice` 始终为 0，摩擦力与普通地面一致 |

实现细节：

- 三条都只在"**本地诺艾尔 + 装备稳定之体 + 非小骑士模式**"时生效（`IsStableBodyApplied`）；
- `M2Mover.Phy` 是 `protected`，改用公开的 `M2Phys.Mv` 反查归属，避免反射；
- ① 与 ①-b 是两条独立机制：**①（撞上去摔倒）没有伤害**、走 `moveByHitCheck → addEnemySink → RCenemy_sink`；**①-b（接触攻击）有伤害**、走 `NelEnemy.tackleInit → MGKIND.TACKLE → M2PrADmg`。前者是需求本意，后者是同名现象的另一条路径，一起拦掉更贴合"不会因为触碰魔物而摔倒"；
- ①-b 的识别不看攻击者种类，只看 `Atk.PublishMagic.kind == MGKIND.TACKLE`：这是 AIC 里所有"撞上去才受伤"的接触攻击的统一形态，敌人自己的挥击/弹幕不落在这个 kind 上；
- 拦截只在受伤的**同一帧**有效（`_stableBodyContactFrame == Time.frameCount`），不会影响其它来源的倒地（如坠落、重击 `DAMAGE_L`）。

### 27.1 二稿：撞怪摔倒其实是 `RCenemy_sink`（build=2026-09-22.48）

一稿只拦了"接触**伤害**"（`MGKIND.TACKLE`），实测**诺艾尔撞到魔物仍然摔倒**。
原因是需求说的摔倒根本不是伤害管线：那是 **`PR.RCenemy_sink`（被撞倒计量）**——

```
PR.moveByHitCheck(AnotherPhy, …)      // 身体撞到别的 mover（魔物）时调用
  → addEnemySink(EnemyAttr.getSinkRatio(enemy), …)   // PR.cs:5243
    → RCenemy_sink.Add(num * ratio, …)                // PR.cs:5297（跑动时 num×2.3）
  → （每帧）runEnemySink → checkEnemySink              // PR.cs:3673 / 3692
    → RCenemy_sink >= 6f → changeState(PR.STATE.ENEMY_SINK)   // PR.cs:3710-3715
```

二稿挂点：

- `PR.moveByHitCheck` 前缀置 `_stableBodyHitSinkScope`、后缀清除（**只覆盖这一次身体碰撞**）；
- `PR.addEnemySink` 前缀在该作用域内且"本地诺艾尔 + 稳定之体"时 `return false`；
- 物理推挤（`base.moveByHitCheck` / `Phy.addFoc`）、`clipWalkXSpeed` 全部保留，所以撞上去的手感还在，只是不会再积累"被撞倒"；
- 醉酒（`M2SerLisP_DRUNK`）、被魔物驮着（`PRMain.initRideOnEnemyJumperBoard`）等其它 `addEnemySink` 调用不受影响。

验证：`build=2026-09-22.48`，DLL SHA256 `FF930DD24A9C64BE…`（两份安装已同步；只覆盖 DLL）。

---

## 28. 诺艾尔的护符第二部分（10）：沉重之击（id 16，2026-09-22，build=2026-09-22.50）

需求（**二稿取代一稿的"20% 概率翻倍"**）：

- 诺艾尔**连续击中目标 5 次** → 进入「**会心**」状态；
- 会心期间，攻击命中目标造成伤害时 **伤害 +40%**；
- 会心期间在诺艾尔**自身中心**渲染 `nail_charge_effect0005`～`0009`（与小骑士骨钉技艺蓄力同款）；
- **攻击未命中** → 重新计数；若已进入会心，则**退出会心**。

### 28.1 连击统计：以"一次攻击动作"为单位

**一稿的做法（已废弃）**：只看 `MGContainer.CircleCast` 的命中结果，用 `MagicItem.id` 区分"每一发"，
并靠 `MagicItem.killed` 判定未命中。实测两个问题：

1. **要打明显多于 5 次才进入会心**；
2. **进入会心后什么都不做也会自己消失**。

**原因**（`nel/M2PrSkill.cs:2573`）：`executeSmallAttack(num++, Mg)` 是**循环调用**的——
一次挥击可能创建**多个攻击判定物**（例如带长距离强化的拳，`id=0` 是主判定、`id=1` 是补判定的 0.45 倍伤害判定）。
于是每次挥击会被算成"两发攻击"：主判定命中计 1 次，副判定若没碰到敌人，就会被当成"一发未命中"→ 计数清零/会心退出。
两个症状都是这个原因。

**三稿（现用）**：改成按**攻击动作**计数，命中与未命中都在"动作"这一层判定：

| 环节 | 挂点 | 说明 |
|---|---|---|
| 出手（近战/骨钉技艺） | `M2PrSkill.executeSmallAttack` postfix（返回非 null 才算真出手） | `HeavyBlowMeleeWindow = 0.6s` 内打中算命中 |
| 出手（魔法） | `M2PrSkill.explodeMagic` postfix（`__result == true` 才算真放出去） | `HeavyBlowMagicWindow = 3s`，留给弹道飞行 |
| 命中 | `MGContainer.CircleCast` postfix：`__result` 含 `HITTYPE.HITTED_EN` 且 `Mg.Caster is PRNoel`、**该攻击包自己带伤害** | 一次动作**只结算一次**（打中即消费掉 pending，多目标/多判定物不重复计数） |
| 未命中 | `TickNoelHeavyBlowCharm` 里"出手后超过窗口仍没命中" | 计数清零 + 退出会心；**没有出手就不会有任何清零**（挂机不会掉会心） |
| 同一次挥击的多个判定物 | `HeavyBlowSameAttackGap = 0.12s` 内重复"出手"合并为同一发 | 解决上面"一次挥击被算成两发"的根因 |
| 额外命中源 | 蜕变挽歌剑气打中敌人也调用同一个"命中登记" | 剑气属于诺艾尔的攻击 |

计数与状态：命中 +1，满 5 → 会心成立（进入后保持）；未命中 → 计数清零 + 退出会心。

### 28.1.1 三稿：判定口径改成"看伤害值"，并修掉"命中早于出手登记"的时序（build=2026-09-22.52）

实测反馈："好像只算轻攻击，其它攻击不算"。两处原因：

1. **判定口径太窄**：原判据是 `MGKIND` 白名单（法术 kind ∪ 骨钉系 kind），
   于是滑铲（`PR_SLIDING`）、回避反击（`PR_EVADECOUNTER`）、盾击等**能造成伤害但不在白名单里**的攻击全被漏掉。
   改为**直接看攻击包自己的伤害**：`Mg.Atk0._hpdmg &gt; 0 || Mg.Atk0._mpdmg &gt; 0`
   （`AttackInfo._hpdmg` = `hpdmg_current ?? hpdmg0`）——只要这一发真能造成伤害就算，
   与"任何能造成伤害的攻击"一致。出手登记（`executeSmallAttack` 的返回值）用同一个判据。
2. **命中事件早于出手登记的时序**：`executeSmallAttack` 内部会立刻 `magicItem.run(0f)`（`:2730`）、
   `explodeMagic` 内部也会 `_Expl.run(0f)`（`:3682`），如果判定物在**创建瞬间**就碰到敌人，
   命中回调会跑在模组的"出手登记"之前 → 那次命中被丢弃，随后登记出的 pending 还会超时判成"未命中"、反而清零。
   现在命中事件会记下时间戳，出手登记时若发现"刚刚（`HeavyBlowSameAttackGap` 内）已经命中过"就直接按命中结算。

验证：`build=2026-09-22.52`，DLL SHA256 `AC691266C4F3BB97…`（两份安装已同步；只覆盖 DLL）。

### 28.4 会心光圈的位置与大小（build=2026-09-22.53）

实测：光圈显示在诺艾尔的左下角，且要求"放大到 2 倍、向右 1 格、向上 1 格"。根因有两条：

1. **`MeshDrawer.Rect(x, y, w, h)` 的 `(x,y)` 本身就是矩形中心**
   （`MeshDrawer.cs:1637`：内部 `RectBL(x - w/2, y - h/2, …)`）。
   之前写的是 `Rect(-w*0.5f, -h*0.5f, w, h)`，等于**又多减了一次半宽高**，
   光圈中心被推到锚点的左下各半张图（212×197 × scale=0.42 ÷ CLEN=28 ≈ 1.6 格）。
   而"向左下偏半张图"这个误差会随尺寸等比放大——所以不能只按用户给的"+1 格"平移了事，
   直接改成 `Rect(0, 0, w, h)` 才是"以锚点为中心"。
2. **锚点要用身体中心**：`pr.mbottom` 是脚底、`pr.sizey` 是身高（格），
   因此取 `cy = pr.mbottom - pr.sizey * 0.5f` 作为身体中心（比直接用 `pr.y` 稳）。

调整后：

| 项目 | 值 |
|---|---|
| 大小 | `tex.width/height × ScaleConfig × HeavyBlowAuraScale(2f)`（需求：2 倍） |
| 位置 | 水平/垂直均以诺艾尔身体中心为基准（净效果≈原来的"右移 1 格、上移 1 格"） |
| 其它 | 帧率 20fps、5 帧循环、PR0 层、退出会心即释放票据 —— 均不变 |

验证：`build=2026-09-22.53`，DLL SHA256 `02997CF3EDB9241E…`（两份安装已同步；只覆盖 DLL）。

### 28.5 光圈二稿：1.5 倍 + 上移 1 格（build=2026-09-22.54）

- 大小倍率 `HeavyBlowAuraScale`：`2f` → **`1.5f`**；
- 锚点新增固定纵向偏移 `HeavyBlowAuraOffY = -1f`（单位格；**AIC 的 y 向下为正，所以 -1 就是向上 1 格**），
  最终锚点 = `pr.mbottom - pr.sizey*0.5f - 1f`（脚底 − 身高/2 = 身体中心，再上移 1 格）。

验证：`build=2026-09-22.54`，DLL SHA256 `7FF75EF1725ADEB6…`（两份安装已同步；只覆盖 DLL）。

---

## 29. 诺艾尔的护符第二部分（11）：快速劈砍（id 17，2026-09-22，build=2026-09-22.55）

需求：诺艾尔**挥动法杖的速度提升 50%**。

### 29.1 一稿（作废）：挂 `PunchSpeed` 几乎没有效果

一稿给 `M2PrSkill.PunchSpeed(float level)` 挂后缀 ×1.5（实现是
`X.Mx(0.1f, CaneStat.Pow(CaneStat.near_punch_speed, level))`，即"手杖近接挥击速度"属性）。
实测**几乎看不出变化**，原因：`PunchSpeed` 只喂给

- `Anm.timescale = PunchSpeed(...)`（`:643 / :808 / :938 / :1083`），以及
- 各状态的**起手几帧**：`t += base.TS * this.PunchSpeed(...)`（`:653 / :854 / :971 / :1116 / :1202`，都在 `t < 9` 的起手段里）；

而挥击**主体**（`t` 从 9 推到 24 那一段）是**主状态计时**在推进（`PR.cs` 里 `this.t_state += base.TS`），
不受 `PunchSpeed` 影响 —— 所以整体只快约 1.14 倍，观感上"没变"。

### 29.2 二稿（现用）：挂 `PR.baseTS`，只加速挥击状态

| 项目 | 说明 |
|---|---|
| 挂点 | `PR.baseTS`（`nel/PR.cs:1417` 重写的属性，`= _baseTS * Skill.baseTimeScale()`）后缀 ×1.5 |
| 为什么它能同时管住两件事 | `M2Mover.TS => Map2d.TS * baseTS`（`m2d/M2Mover.cs:2514`）：① 主状态计时 `t_state += base.TS` → 挥击流程整体快 50%；② 动画播放 `M2PxlAnimator.runPre` 的 `ts = 帧时间 × Mv.TS × animator_TS × timescale`（`m2d/M2PxlAnimator.cs:202`）→ 动画同样快 50% |
| 生效范围 | 仅**挥击/技艺状态**（`IsNoelAttackState`：PUNCH / AIRPUNCH(+SG) / WHEEL(+SG) / COMET(+SG) / DASHPUNCH(+SG) / SMASH(+SG) / SLIDING / EVADECOUNTER(+SG)），走路跳跃等不受影响 |
| 状态读取 | `PR.state` 是 protected 字段，用 `AccessTools.FieldRefAccess&lt;PR, PR.STATE&gt;("state")` 快速读取 |
| 生效条件 | 本地诺艾尔 + 装备快速劈砍；小骑士模式不参与（骑士侧用模组自己的 `SlashAttackTime` 0.4→0.3） |

验证：`build=2026-09-22.56`，DLL SHA256 `C696790EB8A29EA9…`（两份安装已同步；只覆盖 DLL）。

---

## 30. 诺艾尔的护符第二部分（12）：修长之钉（id 18，2026-09-22，build=2026-09-22.57）

需求：诺艾尔**近战攻击距离 +20%**（轻攻击 Punch、魔法霰弹 Shotgun、凌空横斩 Airpunch、会心重击 Fatal Smash）。

### 30.1 参照 AIC 原生"长法杖"（`ENHA.EH.long_reach`）

长法杖**并不改伤害数值，而是改"攻击包自身的 reach 几何"**：

| 位置 | 做法 |
|---|---|
| `M2PrSkill.executeSmallAttack`（`nel/M2PrSkill.cs:2700`） | 轻攻击原本只建 `id=0` 一个判定物；带长法杖时**多建一个 `id=1` 的更远判定物**（`id==0` 时 `sx *= 2.4f`，伤害按 `1.25 - id*0.8` 折到 0.45 倍） |
| `:2872-2876`（回避反击） | `magicItem.sx *= 2.2f; magicItem.dx *= 2.2f;` |
| `:2881-2884`（会心重击） | `magicItem.sx *= 1.5f;` |
| `MDAT.cs:1596/1599` | 还留了 `EH_long_reach = 1.4f`、`EH_long_reach_smash = 0.5f` 两个常量（当前版本已无引用） |

判定几何长什么样（`MagicItem.runTackle`，`nel/MagicItem.cs:2593-2611`）：

```
sz >= 0 : 射线 = 从施法者中心 Cen 沿 (sx, sy) 方向、长度 |(sx,sy)|、粗细 sz
sz <  0 : 判定中心 = Cen + (sx, sy)，方向 (dx, dy)，粗细 |sz|
```

近战基础值来自 `MDAT`：`sx = ±0.75`、`sy = 0.55`、`sz = 0.45`（`nel/MDAT.cs:725-728`），
之后由 `executeSmallAttack` 里按状态再调（如凌空横斩 `sx += 0.6`）。

### 30.2 【2026-09-22 撤销】当前实现已整体移除

需求原意是"挥击看起来也变长"，但实测走不通，按用户要求**先把诺艾尔侧修长之钉的实现整体撤掉**
（`build=2026-09-22.74`，代码、配置项、补丁全部删除；判定与画面都回到原版，等新做法）。留下结论供下次参考：

**判定侧（可行，已随本次移除）**：

- `PrCaneEquip.reach_ratio(reach_level)`（`:246`，`Pow(near_reach, level)`）是 AIC 自己的"手杖攻击距离"入口：
  `PrCaneEquip.initChantMagicAwaken`（`:101-121`）会用它做 `Mg.sx *= reach_ratio(...)`，
  直接决定近战射线长度（`MagicItem.runTackle` → `M2Ray.CastRayAndColliderS(..., Dir_, len, ...)`）。
  注意游戏只会乘 `sx`，射线**粗细 `sz` 不参与**：只按比例乘的话，真实"触及距离"（`|(sx,sy)| + |sz|`）
  的增幅会打折（实测 20% → 只剩 9% ≈ 3.5 像素，肉眼几乎看不出）。
  据此做过"前缀量基准、后缀把总触及距离精确改成基准 ×N"的实现（N 试过 2 = +100%），**判定确实按比例变长了**。
- `M2PrSkill` 的挥击粒子 `lax`（`:2581`）等也乘同一个 `reach_ratio`，但实测**画面不变**（见下）。

**渲染侧（走不通）**：

- 诺艾尔的像素图是**分层**的（`PixelLiner.PxlLayer`：`name` / `x` / `y` / `zmx` / `zmy` / `rotR` / `alpha`），
  日志实测她的挥击姿势只有这几层：
  `attack1`——`rod_4` + `Layer`；`attack2`——`Layer`/`Layer_2` + `rod_4` + `rodeff` + `hand`。
- 逐层排查结论：`rod_4` = 法杖本体（拉大→法杖变巨大）、`rodeff` = 法杖旁的**粒子**（拉长→只有粒子变长）、
  `Layer`/`Layer_2` = **本体画**（身体和挥击弧光烘焙在一起）、`hand` = 手。
  → **弧光不是独立图层**，和身体画在同一层，没法只拉弧光。
- 改图层后还必须让渲染网格重建，否则画面完全不变：诺艾尔立绘由 `M2PxlAnimatorRT.MyMd` 绘制，
  而它只在 `need_fine == true && auto_replace_mesh` 时重建（`m2d/M2PxlAnimatorRT.cs:155-171`）；
  只调 `PxlFrame.Apply()`（重建的是 `PxlFrame.MeshGenerator`）**没有用**，必须置 `anm.need_fine = true`。

### 30.3 新做法（当前）：判定照旧放大 + **自绘白色弧带**（build=2026-09-23.1）

用户选定的方案：**不再试图改她的姿势图层**，改成模组自己画一段白色弧带：

| 部分 | 做法 |
|---|---|
| 判定 | 仍用 `PrCaneEquip.reach_ratio` × 倍率 + `PrCaneEquip.initChantMagicAwaken` 前缀量基准 / 后缀把**总触及距离**（`\|(sx,sy)\| + \|sz\|`）精确改成 `基准 × 倍率`；只作用于 `PR_PUNCH`/`PR_SHOTGUN`/`PR_SMASH`（轻攻击/凌空横斩/魔法霰弹/会心重击） |
| 表现 | 在 `M2PrSkill.executeSmallAttack` 后缀登记一道弧带（记录诺艾尔中心、朝向、**长度 = 该攻击包放大后的触及距离**），用 1×1 白纹理 + `MeshDrawer.Triangle` **程序化绘制**一段弯月带（张角 ±42°、14 段、中间粗两端细、随时间淡出，0.14 秒），画在 `PR1` 层（角色身前） |
| 弧带范围 | **只画"护符多出来的那一段"**：内半径 = 该招原版触及距离（在 `PrCaneEquip.initChantMagicAwaken` 前缀量到、按 `MagicItem.id` 存表），外半径 = 加成后的触及距离；中段向外探出，呈月牙而非等宽环 |
| 可调 | BepInEx 配置 `[Charm18] LongNailReachPercent`（默认 **20** = +20%，填 0 关闭）；判定与弧带长度始终同一口径 |

好处：不用任何外部素材、身体/法杖完全不动，且"看得到多远 = 打得到多远"；弧带只补在原来打不到的那一小段上，不会和游戏原本的挥击弧光重叠。

### 30.4 二稿：同一次挥击只画一条弧带（build=2026-09-23.3）

实测反馈"还是两段弧光"。原因：AIC 一次挥击会**连续生成多个攻击包**
（`executeBasicPunch` 里 `executeSmallAttack(num++, Mg)` 循环：`id=0` 主判定 + 长距离强化时 `id=1` 的补判定），
每个包都登记了一条弧带 → 画出两道。

修正：**同一帧、同一 kind、同朝向**的弧带合并成一条（内半径取最小、外半径取最大，长度计时重置）。
另外补了一条诊断日志便于核对不同法杖的数值（限 12 行）：

```
[KIC][长钉弧带] kind=PR_PUNCH 原版触及=… 加成后=…（倍率 …）
```

内半径来自 `PrCaneEquip.initChantMagicAwaken` 前缀量到的**这一招当前装备法杖**的原始触及距离
（按 `MagicItem.id` 存表），所以换法杖时加成范围会跟着变（不同法杖基础距离不同 → 加成量也不同）。

### 30.5 三稿：弧带厚度收窄到 1/5、加成改 25%（build=2026-09-23.4）

- 弧带**径向厚度**改成原来的 1/5：`rOut = ReachTo × (0.98 + 0.02·taper)`，
  并以 `加成区厚度 × (0.07 + 0.13·taper)` 作为最小厚度（原来是 `0.35 + 0.65·taper`）；
- 距离加成默认值改为 **25%**（`[Charm18] LongNailReachPercent = 25`）。

> 注意：配置里若已被旧版本写成 `LongNailReachPercent = 20`，请手动改成 25（或删掉那一行用默认值）。

### 30.6 四稿：弧带只画"加成后那一圈"（build=2026-09-23.7）

澄清需求后定为：**只要"修长之钉加成后那一圈"**，不要"普通法杖那圈 → 加成后那圈"之间的整片环形区域。

- 弧带半径 = 加成后的触及距离 `ReachTo`，自身很细：厚度 = `ReachTo × LongNailArcWidthRatio`（默认 **0.05** ≈ 2~3 像素），中段略粗、两端收细；
- 之前两稿的误区：先按"内半径=普通触及距离、外半径=加成后触及距离"画，厚度等于整个加成区（约 7 像素），所以无论怎么调系数都不变。
- 配置：`[Charm18] LongNailArcWidthRatio`（厚度比例）、`LongNailArcSpanDeg`（张角 ±度）、`LongNailArcAlpha`（亮度倍率）、`LongNailReachPercent`（距离加成 %，默认 25）。

验证：`build=2026-09-23.7`，DLL SHA256 `090B40433E147F3D…`（两份安装已同步；只覆盖 DLL）。

### 28.2 伤害 +40%

并入已有的"诺艾尔侧最终伤害乘区"（`CircleCast` 前缀/后缀，与护符 5 萨满之石、护符 13 坚固力量
共用同一个 `__state` 保存/还原），三者**连乘**且互不污染：

```
Atk.hpdmg0(临时) = hpdmg0 × [萨满 1.25（法术）] × [坚固力量 1.25（骨钉系招式）] × [会心 1.4]
```

蜕变挽歌剑气由模组自己结算（不走 `CircleCast`），已单独在这条路上也按会心 ×1.4（18 → 25）。

顺带修掉一个**既有 bug**：原先 `mult *= ShamanDamageMult` 只判断了"是不是法术 kind"，
没有判断萨满之石是否装备——于是"只装坚固力量（13）时，法术也会莫名 +25%"。现在两件都按
`IsEquipped` 各自判定，只有装了对应护符才进乘区。

### 28.3 会心光圈

与护符 10 挽歌剑气同一套自绘票据做法（AIC 的角色不走 SpriteRenderer）：

- 素材 `assets/hk/sprites/nail_charge_effect0005..0009.png`（已随模组部署，无需新增）；
- 票据挂 `M2Mover.DRAW_ORDER.PR0`（玩家身后层，同小骑士蓄力光圈），矩阵锚定诺艾尔中心 `pr.x/pr.y`；
- 20fps 循环播放（与小骑士 `LoadArtGroup("Nail Art Glow", "nail_charge_effect", 10, 20f, 1)` 同帧率），只画 5～9 这 5 帧；
- 退出会心即释放票据；素材缺失时只是不显示，连击与伤害照常。

验证：`build=2026-09-22.51`，DLL SHA256 `6BA544F2565A6E8B…`（两份安装已同步；只覆盖 DLL）。

---

## 31. 【口径确认】伤害乘区：萨满之石（5）与坚固力量（13）（2026-09-23，build=2026-09-23.13）

### 31.1 需求口径（用户确认）

| 招式（游戏内名） | 萨满之石 ×1.25 | 坚固力量 ×1.25 |
|---|---|---|
| 所有法术技能（纯白之箭/地面炸弹/火球/雷霆/水石/黑洞/花环…） | ✔ | — |
| 魔法霰弹（蓄力后轻攻击） | ✔ | ✔ |
| **魔法霰弹变种**（蓄力状态下释放的下列技艺） | ✔ | ✔ |
| 轻攻击 Punch、旋风斩击 Cyclone Slash、彗星俯冲 Comet Dive、突进冲击 Dashpunch、凌空横斩 Airpunch、会心重击 Fatal Smash、轮舞斩击 Dancing Slash | 仅"变种"时 ✔ | ✔ |

两件同时佩戴时，魔法霰弹及其变种 = **×1.25 × 1.25**（连乘，不是覆盖）。

### 31.2 招式 → kind 对照（0.30g 反编译实证）

`M2PrSkill.executeSmallAttack`（`M2PrSkill.cs:2587-2830`）里，"蓄力后释放的挥击"分两类：

| 招式 | 未蓄力 kind | 蓄力（变种）时 |
|---|---|---|
| 轻攻击 / 凌空横斩 | `PR_PUNCH` | **kind 直接变成 `PR_SHOTGUN`**（`:2705`） |
| 旋风斩击 | `PR_WHEEL` | kind 仍是 `PR_WHEEL`，只是 state 为 `WHEEL_SHOTGUN` |
| 彗星俯冲 | `PR_COMET` | 仍是 `PR_COMET`（`COMET_SHOTGUN`） |
| 突进冲击 | `PR_DASHPUNCH` | 仍是 `PR_DASHPUNCH`（`DASHPUNCH_SHOTGUN`） |
| 会心重击 | `PR_SMASH` | 仍是 `PR_SMASH`（`SMASH_SHOTGUN`） |
| 轮舞斩击 Dancing Slash | **`PR_EVADECOUNTER`** | 仍是 `PR_EVADECOUNTER`（`EVADECOUNTER_SHOTGUN`） |

轮舞斩击的对应关系是本轮新查实的：`SkillManager.SKILL_TYPE.evade_dancing`
（中文名"轮舞斩击"，操作=弹开敌人攻击后 ←/→ + z）在 `M2PrSkill.cs:2017-2021`
把 state 置为 `EVADECOUNTER`/`EVADECOUNTER_SHOTGUN`，而 `:2657-2664` 给它的 kind 是
`PR_EVADECOUNTER`。**旧的文档注释写"PR_WHEEL = 旋风斩击 / 轮舞斩击"是错的**，已修正。

### 31.3 本轮修掉的两个漏项

1. **坚固力量漏了轮舞斩击**：`IsPowerBoostKind` 原来只列 `PR_PUNCH/PR_SHOTGUN/PR_WHEEL/PR_COMET/PR_DASHPUNCH/PR_SMASH`，
   现已补 `PR_EVADECOUNTER`（未列出的 滑铲 `PR_SLIDING`、护盾冲击/环轨护盾 `PR_SHIELD_*` 按需求**不**加成）。
2. **萨满之石漏了"霰弹变种"**：变种的 kind 是自己的技艺 kind（见 31.2），
   `IsPlayerMagicKind` 判不出来，所以"同时佩戴两件时变种只有 ×1.25"。
   新增判据 `IsNoelShotgunFlavored(MagicItem)`：

```csharp
// ① 主判据：initShotGun 把"本次蓄力的耗魔"写进了 reduce_mp（MDAT.cs:1162）
//    普通挥击的 reduce_mp 恒为 0（MagicItem.init:46），法术类则不是 NORMAL_ATTACK
Mg.is_normal_attack && Mg.reduce_mp > 0f
// ② 兜底：蓄力刚开始就释放时 reduce_mp 会被取整成 0，此时看是否还在 *_SHOTGUN 状态
|| (Mg.Caster as PRNoel)?.isShotgunState()
```

萨满之石的条件因此从"`IsPlayerMagicKind(kind)`"变为
"`IsPlayerMagicKind(kind) || IsNoelShotgunFlavored(Mg)`"；坚固力量仍只看 kind。两者在同一个
`mult` 上连乘，所以霰弹及其变种 = ×1.5625。

同一条判据也补给了**护符 4 灵魂捕手 / 护符 6 噬魂者**（"用法术命中敌人回 MP"）：
蓄力释放的挥击同样算魔法，命中即回 MP（此前只有魔法霰弹本身算）。

验证：`build=2026-09-23.13`，DLL SHA256 `1C3E1D52C83E3A25…`（两份安装已同步；只覆盖 DLL）。

---

## 32. 蓄力剑气贴图：魔法霰弹及其变种改用 `slash_effect_magic`（2026-09-23，build=2026-09-23.14）

**需求**：诺艾尔为法杖蓄力后（魔法霰弹及其变种），蜕变挽歌、修长之钉、骄傲印记渲染出的剑气
贴图换成 `assets/hk/sheets/slash_effect/slash_effect_magic.png`。

**"蓄力释放"的判据**沿用第 31 节新增的 `IsNoelShotgunFlavored(Mg)`（`is_normal_attack && reduce_mp > 0`，
兜底 `isShotgunState()`），因此三处渲染共用同一条口径。

### 32.1 三处渲染的改法

| 护符 | 原贴图 | 蓄力时 | 说明 |
|---|---|---|---|
| 10 蜕变挽歌 | `slashes_effect0001` | `slash_effect_magic` | 剑气框仍按**原贴图**尺寸算（magic 那张是 1280×832，直接按它算会放大 8 倍） |
| 18 修长之钉 | `mantis_slash_left0001/0002`（两帧交替） | `slash_effect_magic`（单帧） | 框高仍按所选贴图宽高比算，所以 magic 图自带的比例会生效 |
| 19 骄傲印记 | 同上 | 同上 | 与 18 共用一条弧带绘制，`Magic` 标记记在弧带上 |

弧带结构 `NoelLongNailArc` 增加 `Magic` 字段；同一帧同一 kind/朝向的多段判定合并弧带时
`Magic = 旧 || 新`，避免"一蓄力一普通"混在一起画两套图。

### 32.2 蜕变挽歌在蓄力时也会发射剑气

原来挽歌只认 `MGKIND.PR_PUNCH`（= 不蓄力的轻攻击）。蓄力后轻攻击的 kind 变成 `PR_SHOTGUN`
（`M2PrSkill.cs:2705`）、技艺变种更是各自 kind，于是**蓄力时挽歌根本不发剑气**，
"换贴图"也就无从谈起。因此新增判据：`PR_PUNCH`（原样）**或** `IsNoelShotgunFlavored`
（= 蓄力释放的轻攻击/技艺）都发射剑气，后者用 magic 贴图。

这一条是**行为**变化（不只是观感），已做成开关：`[Charm10] ElegyOnChargedAttack`（默认开），
关掉就退回"只有不蓄力的轻攻击发射剑气"。

### 32.3 新增配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `MagicSlash` | `OnChargedAttack` | true | 蓄力释放时是否换 magic 贴图（关掉则三处一律用原贴图） |
| `MagicSlash` | `Scale` | 1 | magic 剑气整体渲染大小倍率（宽高等比，只影响蓄力时那张图） |
| `MagicSlash` | `HeightRatio` | 1 | magic 剑气渲染高度倍率（只改高度） |
| `Charm10` | `ElegyOnChargedAttack` | true | 蓄力释放时是否也发射挽歌剑气 |

### 32.4 素材部署

`assets/hk/` 在 `.gitignore` 里（HK 素材不入库），所以**部署 DLL 之外还要拷素材**：
`assets/hk/sheets/slash_effect/slash_effect_magic.png` → 两份安装的
`BepInEx/plugins/KnightInCradle/assets/hk/sheets/slash_effect/`（两份都已拷）。
加载器会先找 `sheets/slash_effect/`，再找 `sprites/`；缺素材时只记一条警告日志并回退原贴图
（判定与伤害不受影响）。

验证：`build=2026-09-23.14`，DLL SHA256 `D61CF42F7C34FCB0…`，
素材 SHA256 `98C2B059794A4AD6…`（两份安装已同步：DLL + 新素材）。

---

## 33. 蜕变挽歌 × 魔法蓄力：剑气命中 = 一次魔法霰弹击中（2026-09-23，build=2026-09-23.15）

**需求**：魔法蓄力之后发射的下一发剑气，如果打到了敌人，则**视为对该敌人触发了一次魔法霰弹**
（伤害 + 原版霰弹的击中音效/动画），并**清除自己的蓄力**。

### 33.1 挂点与数据

剑气的开火点是 `M2PrSkill.executeSmallAttack` 后缀（第 32 节已让它认"蓄力释放"），
命中点是模组自己的 `CheckNoelElegyHit` → `ApplyNoelElegyDamage`。

关键在于"剑气带着霰弹的判定飞出去"：

1. **开火时**：`NoelElegyBlade` 增加 `ShotAtk`——把这一刀攻击包的 `NelAttackInfo`
   用复制构造 `new NelAttackInfo(Atk0)` **整份抄下来**（伤害、属性、击退、burst、split_mpdmg…），
   这样即使原攻击包被回收，剑气手里的这份仍然完好；同时记下 `ShotKind`（算乘区用）。
2. **命中时** `TriggerNoelElegyShotgun(pr, enemy, b)`：
   - 前置：`b.Magic && b.ShotAtk != null`，且此刻**蓄力还在**
     （`skill.getCurMagic() != null && isPreparingCircle && getHoldingMp(true) >= 1`）
     ——与原版 `M2PrSkill.publishShotgunHit` 的早退条件一致；
   - ① 伤害 = `ShotAtk.hpdmg0 × NoelFinalDamageMult(ShotKind, 霰弹=true)`
     （萨满之石 ×1.25、坚固力量 ×1.25、会心 ×1.4，与 `CircleCast` 那条路**同一函数**）；
     用 `new NelAttackInfo(ShotAtk)` 再抄一份、把 `hpdmg0/hpdmg_current` 换成算好的值后
     `enemy.applyDamage(atk, false)`（`hpdmg_current` 一旦写入就跳过随机化，伤害确定）；
   - ② 击中动画/音效：`MDAT.setFullChargeShotgunEffect(pr, 蓄力比例, hitItem, not_use_full:false,
     fullcharge_hitstop:true, 0.47123894f)` —— 与"原版霰弹命中"同一入口
     （`shotgun_post`/`shotgun_post_small` 粒子 + 屏幕闪光 + 满蓄力时的瞬时减速），
     `hitItem` 是模组自建的 `M2Ray.M2RayHittedItem`（填 `hit_ux/hit_uy` = 敌人位置）；
   - ③ 清除蓄力：`skill.killHoldMagic(false, false, false)` —— 与原版"霰弹把蓄力耗尽"时
     （`M2PrSkill.cs:2408`）**同一个调用**：不复位、不返还已消耗的魔力。

剑气自带的 18 真实伤害**保留**（需求只说"额外触发霰弹"）；同一道剑气只对每只魔物结算一次，
第一只命中后蓄力即被清掉，后面的目标不会再触发第二次。

### 33.2 配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm10` | `ElegyOnChargedAttack` | true | 蓄力释放时是否发射剑气（第 32 节） |
| `Charm10` | `ElegyShotgunOnHit` | true | 蓄力释放的剑气命中时是否结算"魔法霰弹击中"并清蓄力 |

验证：`build=2026-09-23.15`，DLL SHA256 `FA37E35CED4F0A84…`（两份安装已同步；只覆盖 DLL）。

### 33.3 【2026-09-23 改口径】剑气伤害 = 那一刀的伤害（不再固定 18 真实伤害）

**需求**：未魔法蓄力 → 剑气造成**诺艾尔轻攻击的伤害**（不走真实伤害，直接走轻攻击）；
已魔法蓄力 → 剑气造成**当前魔法霰弹的伤害**。

做法：剑气不再自带固定伤害，而是**带上发射它的那一刀**——

1. 开火时（`executeSmallAttack` 后缀）无论有没有蓄力，都 `new NelAttackInfo(__result.Atk0)`
   复制一份攻击包存进剑气（`CarriedAtk`，带 kind、属性、击退、burst、split_mpdmg…），
   并记下 `pr.getHpDamagePublishRatio(__result)`（这一刀的**伤害发布率**，含力量等级等加成）。
2. 命中时（`ApplyNoelElegyDamage`）：

```
hpdmg0'  = round( CarriedAtk.hpdmg0 × [萨满 1.25] × [坚固力量 1.25] × [会心 1.4] )
hpdmg    = shuffleHpMpDmg(敌人, CarriedRatio, …)   // 与原版 CircleCast 同一套（含浮动）
applyDamage(atk)                                    // fix_damage 保持 false → 吃敌人减伤
```

   于是未蓄力时就是那一记轻攻击（`PR_PUNCH`）的伤害与手感，已蓄力时就是那一发魔法霰弹
   （`PR_SHOTGUN` / 各变种 kind）的伤害——与 33.1 的"霰弹命中触发"自然合并：
   伤害已由剑气本体按霰弹口径结算，`TriggerNoelElegyShotgun` 只负责**击中动画/音效 + 清蓄力**，
   不重复算伤害。`ElegyDamage = 18` 降级为"拿不到攻击包数据时"的兜底。

配置 `[Charm10] ElegyShotgunOnHit` 现在只控制"击中动画/音效 + 清蓄力"，不影响伤害。

验证：`build=2026-09-23.16`，DLL SHA256 `6FC1E003F3645704…`（两份安装已同步；只覆盖 DLL）。

### 33.4 【2026-09-23 削弱】蓄力剑气的伤害 = 魔法霰弹的 30%

**需求**：削弱蜕变挽歌在魔法蓄力后的剑气伤害，调整为原魔法霰弹伤害的 **30%**。

做法：在 33.3 的伤害公式上，给"蓄力释放"那一路乘一个倍率（新增配置，便于回调）：

```
hpdmg0' = round( 攻击包.hpdmg0 × [萨满 1.25] × [坚固力量 1.25] × [会心 1.4] × [蓄力剑气倍率 0.3] )
```

未蓄力的剑气**不受影响**（那一路仍是轻攻击的伤害）。乘法顺序对结果无影响，所以
"30% 魔法霰弹伤害"= 含萨满/坚固力量/会心之后的霰弹伤害再打三折。

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm10` | `ElegyChargedDamageRatio` | 0.3 | 蓄力释放的剑气伤害倍率（0.3 = 霰弹伤害的 30%）；只影响蓄力那一路 |

验证：`build=2026-09-23.17`，DLL SHA256 `7775E628890CEE8B…`（两份安装已同步；只覆盖 DLL）。

---

## 34. 护符 21 苦痛荆棘（诺艾尔侧，2026-09-23，build=2026-09-23.18）

**需求**：① 诺艾尔不会受到场景中**荆棘和尖刺**的伤害；
② 诺艾尔**受到伤害时，对半径 3 格内的目标造成"受到伤害 × 2"的伤害**。
（护符 20 亡者之怒机制复杂，按用户要求放到最后做。）

### 34.1 效果①：免疫场景棘刺（`MAPDMG.SPIKE`）

AIC 的棘刺是"地图危险区"：地图 chip 带 `mapdmg` meta 时，`BCCLine` 会把它建进
`M2MapDamageContainer`（`BCCLine.cs:282-351`），kind 由 `MDMGCon.Create(string)` 解析，
缺省就是 `MAPDMG.SPIKE`（`M2MapDamageContainer.cs:71-79`）；物理体压到该区域时走
`M2Attackable.applyDamageFromMap`（`M2Attackable.cs:186` 或 `M2FootManager.cs:1131`）。

玩家侧的实现是 **`PR.applyDamageFromMap`**（`nel/PR.cs:3100`，override）：
它 `shuffleHpMpDmg` 后交给 `DMG.applyDamage(...)`。所以挂它的**前缀**、在
`MDI.kind == MAPDMG.SPIKE` 时直接 `__result = null; return false;`（=这一次地图伤害没发生），
掉血/击退/僵直全部不产生；`THUNDER / FIRE / LAVA` 等其它地图伤害照旧。
只对**本地诺艾尔 + 诺艾尔模式 + 佩戴苦痛荆棘**生效。补丁用显式参数类型定位
（`M2MapDamageContainer.M2MapDamageItem, AttackInfo, float, float, bool`），避免误挂到基类上。

### 34.2 效果②：受伤反击（半径 3 格，伤害 = 受到伤害 × 2）

挂在已有的"诺艾尔受伤"钩子上——`SturdyHpDamagePrefix`（`M2Attackable.applyHpDamage`），
与幼虫之歌、坚硬外壳同一个入口：

```csharp
TryGrantGrubsongMp();          // 护符9
TryThornsOfAgony(noel, val);   // 护符21：val = 这次受到的**原始**伤害
if (!_noelSturdyActive) ...
```

- 用**原始伤害**（先于坚硬外壳把伤害改写成 0/1），所以同一次受击即便被外壳记成 0 点，
  荆棘反击依然按"真的挨了一下"结算；
- 同一帧只反击一次（`_thornsLastFrame`，与幼虫之歌同一套去重做法）；
- 半径用**地图坐标**判定（1 格 = 1.0）：先 `Physics2D.OverlapCircleAll` 粗筛
  （掩码复用剑气那套 `NoelEnemyOverlapMask`），再按 `dx²+dy² ≤ r²` 复核；
- 伤害是**固定伤害**（`fix_damage = true`），值 = `round(受到伤害 × 2)`，
  不受萨满之石/坚固力量/会心影响（需求就是"受到伤害的 2 倍"，与小骑士侧同为固定伤害）；
- 只打敌人（`NelEnemy`），不会误伤自己。

### 34.3 配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm21` | `ThornsDamageMult` | 2 | 反击伤害 = 受到伤害 × 该倍率 |
| `Charm21` | `ThornsRadius` | 3 | 反击半径（格） |

验证：`build=2026-09-23.18`，DLL SHA256 `8AEDE4A9B31D329B…`，两份安装已同步；
启动日志应是 `72 成功 / 0 失败`（上一版 71 + 本次新增的地图伤害前缀）。

---

## 35. 护符 22 巴尔德之壳（诺艾尔侧，2026-09-23，build=2026-09-23.19）

**需求**：诺艾尔**正在进行魔法咏唱**时，自身中心渲染 `blocker_shell`（同小骑士的巴尔德之壳），
并且**咏唱期间无敌**；另外要给"壳渲染大小 / 高度 / 宽度"三个可调配置。

### 35.1 判定"正在咏唱"

```csharp
MagicItem curMg = pr.Skill.getCurMagic();          // 手里握着的那发魔法（`M2PrSkill.CurMg`）
bool chanting = curMg != null && curMg.isPreparingCircle && pr.Skill.getHoldingMp(true) >= 1;
```

`CurMg` 只在"按住咏唱键、蓄力还留着"期间非空（落下时 `killHoldMagic` 会置 null），
`getHoldingMp(true) >= 1` 与霰弹/法术扭曲者用的是同一套"蓄力量"读数——所以"咏唱开始 → 壳展开，
蓄力被用光/被打断/松手施放 → 壳收起"这一条链是自动成立的。

### 35.2 无敌

1. **主**：每帧给 AIC 原生无敌续期——`M2Attackable.NoDamage.Add(2f)`
   （`M2NoDamageManager.Add(float)` = 把 `NDMG.DEFAULT` 一系全类型无敌续到 `floort + 2`，
   见 `M2NoDamageManager.cs:81-113`）。咏唱一停就不再续，**立刻失效**，不会残留无敌时间。
2. **兜底 + "不算受伤"**：在已有的受伤前缀 `SturdyHpDamagePrefix`（`M2Attackable.applyHpDamage`）最前面，
   壳生效时 `val = 0` 直接返回：
   - 覆盖原生无敌挡不住的**穿透类**伤害（岩浆/雷霆 A/抓取穿透，`isPenetrateDefault`）；
   - 同时跳过幼虫之歌（回魔）与苦痛荆棘（反击）——"壳挡下的攻击不算受伤"，
     与小骑士侧的羁绊口径一致。

### 35.3 渲染

素材沿用骑士侧已部署的那套：`assets/hk/sheets/baldur/sprites/blocker_shell_*.png`
（出现 4 帧 / 收起 3 帧，均为 20fps，与小骑士 `BaldurAppear`、`BaldurDisappear` 同一批帧）。

- 剪辑：**展开时播出现（20fps）→ 停在持有帧 `blocker_shell_appear0004`；收起时播收起剪辑 → 隐藏**；
- 锚点：诺艾尔**身体中心**（`mbottom - sizey/2`），可再按 `ShellOffsetY` 上下微调（y 向下为正）；
- 图层：`M2Mover.DRAW_ORDER.PR1`（身前层，同小骑士的壳），叠 3 层绘制让壳更实（同小骑士做法）；
- 尺寸基准取小骑士的 `BaldurShellScale = 0.26`：

```
w = 贴图宽 × 0.26 × ShellScale × ShellWidthRatio
h = 贴图高 × 0.26 × ShellScale × ShellHeightRatio
```

### 35.4 配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm22` | `ShellScale` | 1 | 壳整体渲染大小（宽高等比；1 = 小骑士的壳同尺寸） |
| `Charm22` | `ShellWidthRatio` | 1 | 只改宽度 |
| `Charm22` | `ShellHeightRatio` | 1 | 只改高度 |
| `Charm22` | `ShellOffsetY` | 0 | 上下微调（格；负 = 向上） |

验证：`build=2026-09-23.19`，DLL SHA256 `0C2F8A00A4A1CA20…`（两份安装已同步；只覆盖 DLL）。

### 35.5 【2026-09-23】抵挡次数 3 次 + 破碎后 10 秒恢复

**需求**：巴尔德之壳最多抵挡 **3 次**伤害，否则壳会**破碎**，**10 秒**后才能恢复。
（用户已撤销"仅咏唱中生效（排除 `chant_finished`）"那一版，保护窗口回到"握着蓄力魔力的全程"；
该提交用 `git revert` 撤销，见 `06fd5ea`。）

#### ① 计数挂点（关键）

计数必须挂在"**每一发打进诺艾尔的伤害都要经过、且在原本无敌判定之前**"的地方，
否则会被 AIC 原生无敌挡掉而漏计。选点是 **`M2PrADmg.applyDamage` 的 6 参核心重载**
（`nel/M2PrADmg.cs:1109`，前缀）：敌人的 5 参重载只是转调它，地图危险区
（`PR.applyDamageFromMap` → `DMG.applyDamage`）也走同一条，而 `NoDamage` 判定在它内部
（`:1190/1197`）——所以每一次来袭都只计一次。

```csharp
if (!IsNoelShellActive(noel)) return true;   // 壳没展开 / 破碎恢复期
ConsumeNoelShellBlock();                     // 扣 1 次
__result = 0; return false;                  // 整次伤害都不结算：不扣血、不打断咏唱、不被击退
```

顺带说明：这也让"壳挡下的攻击**不触发幼虫之歌/苦痛荆棘**"（伤害根本没进受伤管线），
与小骑士侧"壳挡下不算受伤"一致；`SturdyHpDamagePrefix` 里的 `val = 0` 保留作兜底
（覆盖绕过该入口的路径）。挂点与护符 15 稳定之体的接触伤害前缀是同一个方法，互不冲突。

#### ② 次数与破碎

- 上限 `ShellMaxBlocks = 3`：**每次壳展开时回满**；
- 每挡 1 次：播一次**受击动画**（`blocker_shell_impact0001` → `appear0002` → `appear0000`，
  同小骑士的 BaldurImpact 剪辑），然后回到持有帧；
- 第 3 次挡下后**次数用尽 → 破碎**：播"冲击帧 + 收起帧"的破碎动画、壳消失，
  并进入 `ShellRecoverSeconds = 10` 秒的恢复期；
- 恢复期内 `IsNoelShellActive` 直接返回 false：壳不展开、也不保护；
- 10 秒到点后次数回满；若此刻仍在咏唱，壳会在同一帧重新展开（继续保护）。

#### ③ 新增配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm22` | `ShellMaxBlocks` | 3 | 最多抵挡次数；挡满即破碎 |
| `Charm22` | `ShellRecoverSeconds` | 10 | 破碎后多久恢复（秒） |

验证：`build=2026-09-23.22`，DLL SHA256 `E581703C1ECD7D37…`（两份安装已同步；只覆盖 DLL）。
启动日志应为 `73 成功 / 0 失败`（上一版 72 + 本次新增的玩家受伤前缀）；
破碎时会打一条 `[KIC][巴尔德之壳] 挡满 3 次 → 破碎，10 秒后恢复`。

### 35.6 【2026-09-23】壳改画在"诺艾尔图层之后"（身后层）

需求：壳的渲染位置调整到诺艾尔图层**之后**。

`M2Mover.DRAW_ORDER` 里 `PR0` 在前、`PR1` 在后（枚举顺序即绘制顺序，`M2Mover.cs:2917`），
所以 `PR0` = 画在诺艾尔**身后**（模组的会心光圈用的就是 PR0），`PR1` = 身前（小骑士的壳用 PR1）。
现在默认改为 **PR0**；票据记录建在哪一层，配置改了会自动重建票据。

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm22` | `ShellBehindNoel` | true | true = 画在诺艾尔图层之后（身后）；false = 回到身前（小骑士那种） |

验证：`build=2026-09-23.23`，DLL SHA256 `BDAFEC626CBF3673…`（两份安装已同步；只覆盖 DLL）。

---

## 36. 护符 23 吸虫之巢（诺艾尔侧，2026-09-23，build=2026-09-23.24）

**需求**：佩戴后，诺艾尔的以下法术改成**向前喷出吸虫**——
**纯白之箭**（`MGKIND.WHITEARROW`）放 **8** 只、**聚能火球**（`MGKIND.FIREBALL`）放 **14** 只，
每只 **7 点真实伤害**；**同时佩戴萨满之石**时每只 **9 点**。
吸虫的手感（初速度、位置微小偏移、重力、落地弹跳、动画贴图）**同小骑士**。

### 36.1 挂点：替换"这一发法术"

法术释放链是 `M2PrSkill.explodeMagic`（`nel/M2PrSkill.cs:3611`）→ `this.CurMg.explode(true)`
（`MagicItem.cs:805`）——**全游戏只有这一处**用 `explode(true)` 放法术，子弹自己被打掉时走的是
`explode(false)`（`MagicItem.kill`）。所以把补丁挂在这里最精确：

```csharp
MagicItem.explode(bool do_not_run1)   // 后缀；__result 就是"这一发原版子弹"
条件：do_not_run1 == true && 诺艾尔模式 && 佩戴吸虫之巢
      && __instance.Caster is PRNoel && kind ∈ { WHITEARROW, FIREBALL }
```

判 `do_not_run1` 是必须的：不判的话，子弹每次消失（`explode(false)`）都会再喷一群吸虫。

### 36.2 处理顺序（为什么这么写）

1. **先 `pr.Skill.killHoldMagic(false,false,false)` 清蓄力**（不返还魔力；魔力已在
   `explodeMagic` 的 `applyMpDamage` 扣过 `:3657-3660`）。
   这样上级 `explodeMagic` 拿到的 `_Expl == null` 分支里，`fineHoldMagicTime` 走的是
   "`mp_hold ≤ 0` → 清 `CurMg`"的正常收尾——否则 `reduce_mp` 已被 `explode` 清零，
   会算出 `casttime * mp_hold / 0` 的 NaN。
2. **收掉原版子弹并把返回值置 `null`**：上级于是走它自己既有的"这一发没成型"分支
   （`M2PrSkill.cs:3662-3666`，与"CurMg 已失效"同一条路），状态机照常切到 `MAG_EXPLODED`。
3. **同一帧喷出吸虫**（`DashAudio.PlayFlukeCast()`）。

### 36.3 吸虫（同小骑士）

| 参数 | 值（与小骑士一致） |
|---|---|
| 每只伤害 | 7 真伤（`fix_damage`）；带萨满之石 9 |
| 初速度 | 水平 `dir × 12~16` 格/秒；垂直 `-8~1`（y 向下为正） |
| 初始位置 | 身前 `0.3~1.3` 格、上下 `-0.3~0.7` 格随机 |
| 寿命 | 4~5 秒 |
| 重力 / 落地弹跳 | 28 格/秒²；脚部触地时横向 `-4~4`、向上 `6~12` 弹起（HK SpellFluke 风格） |
| 命中判定 | 半径 0.675 格圆，命中敌人即消失 |
| 贴图 | `assets/hk/sheets/nest/sprites`：空中 6 帧循环、落地 12 帧扑腾，12fps，按方向镜像，缩放 0.24 |

方向取"这一发的发射方向"：`CAim._XD(pr.getAimForCaster(), 1)`，为 0 时退回朝向。
渲染票据挂 `PR1`（身前层，同小骑士的吸虫）；切到骑士模式时立即清空。

### 36.4 配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm23` | `ArrowFlukeCount` | 8 | 纯白之箭放出几只吸虫 |
| `Charm23` | `FireballFlukeCount` | 14 | 聚能火球放出几只吸虫 |
| `Charm23` | `FlukeDamage` | 7 | 每只吸虫伤害（真伤） |
| `Charm23` | `FlukeDamageWithShaman` | 9 | 同时佩戴萨满之石时每只的伤害 |

验证：`build=2026-09-23.24`，DLL SHA256 `9391DFB136BE2002…`（两份安装已同步；只覆盖 DLL）。
启动日志应为 `74 成功 / 0 失败`（上一版 73 + 本次新增的 `MagicItem.explode` 后缀）。

### 36.5 【2026-09-23】吸虫发射初速度 ×1.25

**需求**：把吸虫**发射出去**的速度提升到现在的 1.25 倍。

实现：生成吸虫时给发射初速度乘一个倍率（新增配置，便于回调）——

```
Vx = dir × rand(12, 16) × FlukeSpeedMult     // 默认 15~20
Vy = rand(-8, 1)      × FlukeSpeedMult
```

**落地弹跳的速度不改**（那已经不是"发射"了，弹跳仍是横向 -4~4 / 向上 6~12）。

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm23` | `FlukeSpeedMult` | 1.25 | 吸虫发射初速度倍率（只影响发射瞬间，弹跳不变） |

验证：`build=2026-09-23.25`，DLL SHA256 `C5C902D03693E98D…`（两份安装已同步；只覆盖 DLL）。

### 36.6 【2026-09-23】数量调整 + 命中后再造成一次伤害 50%

**需求**：纯白之箭 **10** 只 / 聚能火球 **16** 只；
并确认"吸虫命中后有 **50%** 概率再造成一次伤害"。

- 数量：`ArrowFlukeCount` 8 → **10**，`FireballFlukeCount` 14 → **16**；
- 二次伤害：命中敌人并在第一次伤害结算后，按概率**再造成一次同样的伤害**
  （同样的固定真伤值：7 / 带萨满之石 9）——小骑士侧本来就有这条逻辑，只是概率是 25%，
  诺艾尔这边按需求改成 **50%**，并做成配置。

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm23` | `ArrowFlukeCount` | 10 | 纯白之箭放出几只吸虫 |
| `Charm23` | `FireballFlukeCount` | 16 | 聚能火球放出几只吸虫 |
| `Charm23` | `FlukeDoubleHitChance` | 0.5 | 命中后再造成一次伤害的概率（50%） |

验证：`build=2026-09-23.26`，DLL SHA256 `49148332B6F15092…`（两份安装已同步；只覆盖 DLL）。

### 36.7 【2026-09-23】二次伤害概率调回 25%

需求：把 36.6 里设成 50% 的"命中后再造成一次伤害"概率调回 **25%**（= 小骑士的原始值）。

- 代码默认值：`FlukeDoubleHitChance` 0.5 → **0.25**；
- ⚠ **踩到的坑**：BepInEx 的 `.cfg` 一旦生成，就**不会**再被代码里的默认值覆盖。
  单机安装的 `BepInEx/config/dev.KnightInCradle.cfg` 里已经写下了
  `ArrowFlukeCount = 8` / `FireballFlukeCount = 14`（那是它们当时的默认值），
  所以 36.6 改默认值后必须**同时手改这份 cfg**，否则用户侧仍然是 8 / 14。
  本次已把该 cfg 改成 `ArrowFlukeCount = 10` / `FireballFlukeCount = 16`
  （`FlukeSpeedMult`、`FlukeDoubleHitChance` 这两个键当时还没写进 cfg，重启后会按新默认值生成）。
  **结论：以后凡是改"已有配置键"的默认数值，都要连带检查两份安装的 cfg。**

验证：`build=2026-09-23.27`，DLL SHA256 `A154B0AB18201ECE…`（两份安装已同步；只覆盖 DLL，cfg 已同步单机那份）。

---

## 37. 护符 25 发光子宫（诺艾尔侧，2026-09-23，build=2026-09-23.28）

**需求**：
① 每 **2 秒**消耗 **10 MP** 生成一只**小剑山**（贴图循环播 `spike_1~spike_8`）；
除了坐长椅休息，跟随逻辑与小骑士的幼虫一致；
② 同幼虫：自动索敌飞向敌人，命中敌人造成 **30 范围伤害**；
③ 过图逻辑也同幼虫。

> 注：用户口述是"24"，但护符表里 **24 = 防御者纹章**、**25 = 发光子宫**
> （`CharmData.cs:24` "24_shelter" / `:25` "25_uterus"），所以按 **id 25** 实现。

### 37.1 生成与"坐长椅休息"

```csharp
sitting = pr.isBenchState();          // AIC 原生长椅状态（BENCH / BENCH_LOADAFTER / BENCH_ONNIE）
if (!sitting) { 每 SpawnInterval 秒：若 count < MaxCount 且 MP ≥ SpawnMpCost
                    → pr.applyMpDamage(10, force:true, Atk:null, use_quake:false) 扣魔 + 生成 }
```

- 扣魔走原版 `PR.applyMpDamage`（法术扣魔的同一条路，`PR.cs:3297`），会正常更新魔力条；
- **坐长椅休息时不生成、不扣魔**；已经在场的小剑山按小骑士的做法**落地休息**（斜向滑到地面后停住），
  起身后自动恢复跟随。

### 37.2 跟随 / 索敌 / 爆炸（同小骑士）

| 行为 | 参数 |
|---|---|
| 出生 | 向上前方 40°~140°、初速 3.6 格/秒，0.5 秒内衰减后进入飞行 |
| 索敌 | 半径 12 格最近的敌人；加速度 32、最大速度 10 格/秒 |
| 无敌人·诺艾尔在移动 | 追各自随机停靠偏移（她中心半径 0.8~2 格内），最大 7 格/秒 |
| 无敌人·站立 | 停靠点每 0.3~0.8 秒随机刷新，最大 1.6 格/秒小幅悬浮 |
| 命中判定 | 半径 0.55 格触碰敌人 → 爆炸 |
| 爆炸伤害 | **30**（真伤 `fix_damage`），以命中点为中心 **6×6 格**判定框，每只敌人只结算一次 |
| 爆炸特效 | `explode_particle0000~0012`（13 帧，20fps），按 6 格宽渲染，音效 `DashAudio.PlayUterusExplosion()` |

### 37.3 过图（同幼虫）

小骑士那边是"过图清空幼体并每只返还 8 灵魂"（`KnightEntity.cs:11078-11086`），
诺艾尔这边同一条逻辑：**检测到 `pr.Mp` 换图 → 清空全部小剑山与爆炸特效，
并按 `数量 × 10` 返还 MP**（`KnightInCradleBehaviour.GrantNoelMana`），随后重建渲染票据。

### 37.4 渲染

自绘票据（同小骑士的幼体）：矩阵只锚定诺艾尔原点一次，每只按相对偏移绘制，
贴在身前层 `PR1`；小剑山按移动方向镜像；贴图从 `assets/hk/sheets/spike/spike_1~8.png` 循环读。

### 37.5 配置

| 分组 | 键 | 默认 | 含义 |
|---|---|---|---|
| `Charm25` | `SpawnInterval` | 2 | 生成间隔（秒） |
| `Charm25` | `SpawnMpCost` | 10 | 每只消耗 MP（不足则不生成） |
| `Charm25` | `MaxCount` | 4 | 同时存在上限 |
| `Charm25` | `ExplosionDamage` | 30 | 爆炸范围伤害（真伤） |
| `Charm25` | `ExplosionSize` | 6 | 爆炸判定框边长（格） |
| `Charm25` | `SpikeScale` | 0.22 | 小剑山渲染缩放（同小骑士幼体的 0.22） |
| `Charm25` | `SpikeFps` | 12 | 小剑山循环动画帧率 |

验证：`build=2026-09-23.28`，DLL SHA256 `A8208D25E8DDEEA2…`（两份安装已同步；只覆盖 DLL）。
本护符没有新增 Harmony 补丁（走每帧 tick + 已有的物理查询），补丁计数仍是 `74 成功 / 0 失败`。

### 37.6 【2026-09-23 部署修正】小剑山素材没进安装目录

**现象（用户实测）**：佩戴护符后**看不到小剑山，但伤害正常**。

**原因**：`assets/hk/` 在 `.gitignore` 里（HK 素材不入库），部署 DLL 时不会带素材。
`sheets/spike/`（spike_1~8.png）是**这次才第一次用到**的目录，两份安装里都没有，
于是贴图加载失败——代码里只记一条警告就"只是不显示"，所以伤害/逻辑照常。
日志证据：

```
[Warning:KnightInCradle] [KIC][发光子宫] 没找到小剑山素材（assets/hk/sheets/spike/spike_1~8.png），小剑山不显示
```

**修正**：把 `sheets/spike/`（8 张，166 KB）拷进两份安装的
`BepInEx/plugins/KnightInCradle/assets/hk/sheets/spike/`。
同时顺手把仓库里另外三个**尚未部署**的小素材目录 `slashes/`、`dark_descent/`、`tentacle/`
（共约 1 MB）也一并同步，避免以后再用到它们时重复踩坑
（`dream_to_dark/` 约 29 MB，等真做到对应护符再拷）。

> 教训（与 36.7 的 cfg 坑并列）：**只要用到新的素材目录，就要检查它在两份安装里是否存在**；
> "画面缺失但机制正常"基本就是这个原因。
