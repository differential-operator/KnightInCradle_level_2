# AliceInCradle 玩家系统映射文档

> 基于 dnSpy 反编译结果整理，游戏版本 ver 0.29（Unity 2022.3.62f2）。
> 反编译源码位置：`reference/AIC`（Assembly-CSharp.dll）、`reference/AIC-extras`（unsafeAssem 等）。

## 1. 程序集与命名空间

| 程序集 | 内容 | 反编译目录 |
|---|---|---|
| Assembly-CSharp.dll | 游戏逻辑（`nel` 命名空间）：玩家、敌人、道具、UI、事件 | `reference/AIC/Assembly-CSharp/nel` |
| unsafeAssem.dll | 自研 2D 引擎（`m2d`）+ 工具库（`XX`）+ 事件（`evt`） | `reference/AIC-extras/unsafeAssem/unsafeAssem` |
| pixelliner.dll | PixelLiner 像素动画库 | `reference/AIC-extras/pixelliner` |

玩家相关的核心代码几乎都在 `nel` 命名空间；物理、碰撞、输入、伤害基类在 `m2d` / `XX`。

## 2. 玩家类层级与组件

```
M2Attackable（可受伤基类，m2d）
  └─ M2MoverPr（玩家移动/物理/碰撞，m2d）   ← walkSpeed / jump / size
       └─ PR（抽象，nel）                    ← HP/MP、状态机、组件容器
            └─ PRMain（抽象，nel）           ← 凳子/游戏结束恢复等
                 └─ PRNoel（sealed，nel）    ← 诺艾尔本体
```

`PRNoel` 关键成员（`nel/PRNoel.cs`）：

| 成员 | 说明 |
|---|---|
| `hp / maxhp` | 生命值，新游戏 150 / 150（`newGame()`） |
| `mp / maxmp` | 魔力值，新游戏 200 / 200 |
| `newGame()` | 初始化数值、语音、服装、状态 |
| `setOutfitType(OUTFIT)` | 换装（NORMAL / BABYDOLL / DOJO） |
| `createAnimator()` | 用 `M2D.createBasicPxlAnimatorForRenderTicket(this, "noel", "stand", ...)` 创建动画 |

`PR` 关键组件（`nel/PR.cs`、`nel/PRMain.cs`）：

| 字段 | 类型 | 职责 |
|---|---|---|
| `Phy` | M2PhysPr | 物理（速度、重力、力） |
| `Anm` | PrNoelAnimator | 动画（`setPose("姿势名")` 驱动） |
| `Skill` | M2PrSkill | 手杖/技能/攻击状态机（平砍、冲刺砍、空中砍、盾） |
| `DMG` | M2PrADmg | 玩家受伤处理（击退、硬直、状态切换） |
| `DMGE` | M2PrADmgEffect | 受伤特效 |
| `GSaver` | PrGaugeSaver | 血量/魔力扣减前缓冲（慢扣血条） |
| `Ser` | M2Ser | 状态效果（中毒/燃烧/冰冻/麻痹等） |
| `SpMp` | SpecialMpGauge | 特殊魔力槽 |
| `NoDamage` | M2NoDamageManager | 无敌时间 |
| `SttInjector` | PrStateInjector | 状态注入（剧情强制状态） |
| `VO` | PrVoiceController | 语音 |
| `UP` | — | 玩家 UI 面板 |

## 3. 数值

- 新游戏：HP 150/150，MP 200/200（`PRNoel.newGame()`）
- 存档字段（`SVD.sFile`）：`maxhp_noel` / `maxmp_noel` / `hp_noel` / `mp_noel`
- 数值上限/下限统一用 `X.MMX(0, val, max)` 钳制
- 扣血基类（`m2d/M2Attackable.cs`）：
  - `cureHp(val)`：治疗，`hp = clamp(0, hp+val, maxhp)`
  - `applyHpDamage(val, force, Atk)`：扣血，`hp = max(hp - val, 0)`，血量归零走 `initDeath()`
  - `cureMp(val)` / `applyMpDamage(val, force, Atk)`：MP 同理

## 4. 移动与碰撞（M2MoverPr）

移动参数（`m2d/M2MoverPr.cs`，地图单位/帧，60fps 基准）：

| 参数 | 默认值 | 含义 |
|---|---:|---|
| `walkSpeed` | 0.085 | 走路速度 |
| `runSpeed` | 0.17 | 跑步速度 |
| `ySpeedStart` | -0.298 | 起跳初速 |
| `ySpeedMax0` | 0.19 | 最大下落速度 |
| `ySpeedKeyReleased` | -0.06695 | 松键后截断速度 |
| `accel_run_break` | 0.0063 | 地面急停加速度 |
| `double_tap_running` | true | 双击方向跑步 |

碰撞体（`nel/PR.cs`）：
- `size_x_normal = 12`（宽 12 像素）
- `size_y_default_pixel = 68`（高 68 像素，`appear()` 中设置）
- 脚底切片碰撞：`collider_foot_slice_px_x = 8`、`collider_foot_slice_px_y = 46`

物理类：`M2PhysPr : M2Phys`（`nel/M2PhysPr.cs`），速度操作常用
`Phy.walk_xspeed`、`Phy.addFoc(FOCTYPE.xxx, ...)`、`Phy.killSpeedForce(...)`。

## 5. 玩家状态机（PR.STATE）

定义在 `nel/PR.cs`。分组：

- 普通：`NORMAL`
- 攻击：`PUNCH`(20)、`SLIDING`、`DASHPUNCH`、`AIRPUNCH`、`EVADE`(10)、`EVADE_JUMP`、`BURST`(22)、`SHIELD_BUSH`(40)、`EVADECOUNTER`、`SMASH`
- 受伤：`DAMAGE`(4000)、`DAMAGE_L`(4010)、`DAMAGE_L_HITWALL`、`DAMAGE_L_LAND`、`DAMAGE_LT`(4020)、`DAMAGE_PRESS_LR`(4030)、`DOWN_STUN`(4100)、`DAMAGE_BURNED`(4150)、`DAMAGE_WEB_TRAPPED`(4200)
- 特殊/剧情：`ABSORB`(4600)、`WORM_TRAPPED`(4980)、`WATER_CHOKED`、`SLEEP`、`FROZEN`、`BENCH`(10000)、`ONNIE`(500)、`EV_GACHA`、`GAMEOVER_RECOVERY`
- `_OFFLINE = -1`（离线/未使用）

状态切换统一走 `changeState(PR.STATE, PR.STATE)`（virtual，可 Harmony patch）。

## 6. 输入系统

链路：**Unity Input System 的 InputActionAsset（30 个 action）→ `XX.KEY` 类 → 静态 `XX.IN` 辅助方法 → 玩家控制器**。

- `KEY.IPT` 枚举（`XX/KEY.cs`）：SUBMIT、CANCEL、LA/TA/RA/BA（方向）、JUMP、RUN、CHECK、Z、X、C、A、S、D、LSH、MENU、LTAB/RTAB、SORT、ADD、REM、SHIFT、M_NEUTRAL、MLA/MTA/MRA/MBA（魔法瞄准）等，共 30 个。
- 默认键盘绑定（`KEY.cs` `setDefaultInput`）：
  - Z = SUBMIT/魔法Z，X = CANCEL/魔法X，C = CHECK，A = 盾/排序，S、D = 技能键
  - 方向键 = 移动，空格 = RUN/CANCEL2，Escape = 菜单
  - **F1~F5 = 魔法瞄准方向（M_NEUTRAL=1、左=2、下=3、上=4、右=5）→ F5 已被游戏占用，切换键不可用 F5**
  - F7 = UnityExplorer 开关（BepInEx 插件配置）
- 移动/跳跃输入入口（`m2d/M2MoverPr.cs`）：`IN.isLU()` / `IN.isRU()` / `IN.isJumpU()` / `IN.isRunO()` 等，并受 `EV.lockPrInputManipulate(SIMKEY.xxx, ...)` 剧情锁控制。
- 攻击入口：`M2PrSkill` 内根据输入/状态触发 `this.Pr.changeState(PR.STATE.PUNCH)`，落地帧执行 `executeSmallAttack(...)` 并播放 `attack_dash` / `attack_jumpslash1` 等姿势。

## 7. 伤害管线（敌人打诺艾尔）

```
敌人攻击 → AttackInfo（m2d）→ NelAttackInfo（nel，含击退/盾反/吸收等扩展字段）
  → PR 受击入口（PR.cs / M2PrADmg）
  → M2PrADmg.applyHpDamageSimple(...)（无敌判定、伤害修正）
  → PrGaugeSaver.applyHpDamage(...)（慢扣血缓冲条）
  → M2Attackable.applyHpDamage(...)  →  hp = max(hp - val, 0)
  → hp <= 0 → initDeath()（游戏结束/战败演出）
```

- `NelAttackInfo` 关键字段：`hpdmg0`（继承）、`huttobi_ratio`（击飞）、`parryable`（可盾反）、`absorb_replace_prob`（吸收）、`shield_break_ratio`、`Beto`（粘液）等。
- 玩家的无敌时间：`NoDamage`（M2NoDamageManager），受击硬直由 `M2PrADmg` 控制状态与 `t_state`。

## 8. 动画系统

- 角色动画 = PixelLiner 的 `M2PxlAnimatorRT`（m2d），诺艾尔动画源 key 为 `"noel"`（`PRNoel.createAnimator`）。
- 动画由“姿势名”驱动：`Anm.setPose("姿势名", ...)` / `SpSetPose(...)`。
- 已知姿势名：`stand`、`run`、`attack_dash`、`attack_jumpslash1`、`dmg_down`、`dmg_down2`、`dmg_hktb`、`stunned`、`stand2bench`、`spike_trapped`、`spike_trapped_down` 等。
- `PrNoelAnimator : PrBakeAnimator`（`nel/PrNoelAnimator.cs`）管理诺艾尔的换装、手杖持有、表情。
- 朝向：`Anm.setAim(...)` / `base.aim`（AIM 枚举，8 方向）。

## 9. 场景初始化与存档

- 玩家不是代码动态创建的，而是**场景 `SceneGame` 里的预制体 `GobNoel` 挂载 `PRNoel` 组件**：
  `SceneGame.cs:131  this.PrNoel = this.GobNoel.GetComponent<PRNoel>();`
- 之后 `SceneGame.M2D.PlayerNoel = this.PrNoel`（`NelM2DBase.PlayerNoel`），运行时取玩家用 `SceneGame.M2D.getPrNoel()`。
- 地图加载后：`M2D.AddToCoreMover(prNoel)` + `prNoel.newGame()`（读档或新游戏）。
- 存档：`SVD`（`nel/SVD.cs`）+ `PRNoel.readBinaryFrom/writeBinaryTo` 自定义二进制格式，含版本号（当前写 19）。

## 10. HUD

- `UIStatus`（`nel/UIStatus.cs`）：HP/MP 主 HUD（`initHpCrack` 血条裂纹、`fineHpRatio` 刷新）。
- `UISpecialGage`：特殊槽 UI。
- `MpGaugeBreaker` / `SpecialMpGauge`：MP 相关特殊机制。

## 11. 第二阶段（战斗系统替换）候选 Hook 点

> 这些是“小骑士战斗系统”将来要接管/观察的入口，第一阶段先记录：

| Hook 目标 | 用途 |
|---|---|
| `PR.changeState(PR.STATE, PR.STATE)` | 拦截/改写玩家状态（攻击、受伤） |
| `M2PrADmg.applyHpDamageSimple` / `M2Attackable.applyHpDamage` | 换血条规则（面具血量、无敌帧） |
| `PRNoel.newGame` / `readBinaryFrom` | 双角色独立存档数据 |
| `PrNoelAnimator.setPose` | 把小骑士动画接到姿势系统上 |
| `SceneGame` 初始化 / `GobNoel` | 替换/隐藏诺艾尔渲染 |
| `KEY` / `IN` 输入 | 重映射小骑士操作（攻击、跳跃、冲刺） |
| `UIStatus` | 替换成小骑士 HUD（灵魂、护符） |
| `M2MoverPr` 移动参数 | 小骑士移动手感（行走/冲刺/二段跳） |
