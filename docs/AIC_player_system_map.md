# AliceInCradle 玩家系统映射文档（ver 0.30g）

> 适用范围：AliceInCradle **ver 0.30g**（Unity 2022.3.62f2）＋ KnightInCradle v0.2.0。
> 本文**替换**同名 0.29 版旧文档（那份已按"版本过时"删除，内容只留在 git 历史里）。
>
> **依据（可逐条核对）**：
> - 0.30g 反编译源码树 `_tmp_aic_src_new/`：`Assembly-CSharp/` 977 个 `.cs` ＋ `unsafeAssem/` 735 个 `.cs`（2026-09-19 反编译）
> - 对应程序集：`Assembly-CSharp.dll` 5,617,664 B（2026-09-15 20:34）、`unsafeAssem.dll` 2,680,320 B（2026-09-15 17:49）
> - 本文所有行号都是**上面那棵树内的行号**，写法 `nel/PR.cs:1982`，可直接跳转核对。
>
> **不要拿错树**：
> - `_tmp_aic_src_new/` = 0.30g（本文依据，含 Assembly-CSharp + unsafeAssem）
> - `_tmp_aic_src/`、`_tmp_unsafe_src/` = 更早的 0.30d（2026-09-09），只作"这次改了什么"的对照
> - `KnightInCradle/reference/` = 0.29 反编译，**已废弃**，不要再引用
> - `_tmp_ttr_src/` = `better` / `pixelliner` / `ttr` 三个第三方库的反编译

---

## 1. 程序集与命名空间

| 程序集 | 命名空间 | 内容 | 0.30g 反编译位置 |
|---|---|---|---|
| Assembly-CSharp.dll | `nel`（含 `nel.mgm.*`、`nel.libra`、`nel.gm`、`nel.fatal`） | 玩家、敌人、道具、UI、事件、战斗、存档 | `_tmp_aic_src_new/Assembly-CSharp/` |
| unsafeAssem.dll | `m2d`（引擎核心）、`XX`（工具/输入）、`evt`（事件）、`Kayac`、`GGEZ` | 2D 地图引擎、物理/碰撞、输入、渲染票据、事件系统 | `_tmp_aic_src_new/unsafeAssem/` |
| pixelliner.dll | `PixelLiner` | 像素动画（PXL/Pxls） | `_tmp_ttr_src/pixelliner/` |
| better.dll | `better` | 容器库（`BDic` 等），例如 `NelItemManager.ODrop` 用它 | `_tmp_ttr_src/better/` |

玩家代码：**玩家本体逻辑在 `nel`**（`PR` / `PRMain` / `PRNoel`），而**物理、碰撞、输入、渲染票据的基类在 `m2d` / `XX` / `evt`**。改小骑士时要同时在两棵树里找入口。

---

## 2. 玩家类层级与组件

### 2.1 继承链（0.30g 实测）

```
M2Mover               （unsafeAssem/m2d/M2Mover.cs:10，MonoBehaviour，位置/尺寸/朝向基类）
 └ M2Attackable        （unsafeAssem/m2d/M2Attackable.cs:9，abstract，血量/伤害基类）
    └ M2AttackableP     （unsafeAssem/m2d/M2AttackableP.cs:7，abstract，玩家特化）
       └ M2MoverPr       （unsafeAssem/m2d/M2MoverPr.cs:10，玩家移动/输入/脚部/体型）
          └ PR            （nel/PR.cs:14，abstract，状态机 + 组件容器 + 一大堆接口）
             └ PRMain     （nel/PRMain.cs:12，abstract，长椅/战败恢复等）
                └ PRNoel  （nel/PRNoel.cs:11，sealed，诺艾尔本体）
```

> 与 0.29 旧文档的差异：0.29 版写的是 `M2MoverPr : M2Attackable`，0.30g 中间多了一层 **`M2AttackableP`**。

### 2.2 PRNoel 关键成员（`nel/PRNoel.cs`）

| 成员 | 行号 | 说明 |
|---|---:|---|
| `class PRNoel : PRMain`（sealed） | 11 | 本体 |
| `override void newGame()` | 20 | 新游戏初始化：`hp = maxhp = 150`、`mp = maxmp = 200`（22–23），`setOutfitType(OUTFIT.NORMAL, false, false)`（34），`EpCon/EggCon.newGame()`、`UiBenchMenu.newGame()`（37–40） |
| `override void createAnimator(ref PrAnimator Anm)` | 46 | **签名与 0.29 不同**：现在是 `ref PrAnimator` 出参形式 |
| `void readBinaryFrom(ByteReader Ba, SVD.sFile Sf)` | 119 | 读档；存档版本号 = `Ba.readByte()`（126），随后按版本号分支（`num >= 10/11/12/13/18` 等）反序列化各组件 |
| `void writeBinaryTo(ByteArray Ba)` | 200 | 写档；**第一字节写 `20`**（202）→ 0.29 时代是 19 |
| `void setOutfitType(PRNoel.OUTFIT, bool force=false, bool fine_state=false)` | 235 | 换装 |
| `enum OUTFIT` | 592 | `NORMAL / TORNED / BABYDOLL / DOJO / BUNNY / BUNNYREV / …`（0.29 只有前四个里的一部分） |

### 2.3 PR / PRMain 的关键字段（`nel/PR.cs`）

| 字段 | 行号 | 类型 / 职责 |
|---|---:|---|
| `Anm` | 7601 | `PrAnimator`（protected）——姿势/动画驱动 |
| `SttInjector` | 7603 | `PrStateInjector`——剧情强制状态注入 |
| `SfPose` | 7654 | `AnimationShuffler`——姿势混合/受击演出 |
| `need_check_bounds` | 7624 | 置位后下一帧 `autoCheckBounds()` 重算体型 |
| `Ser` | 7636 | `M2Ser`——状态效果（中毒/冰冻/束缚/着火…） |
| `DMG` | 7637 | `M2PrADmg`——受击处理（internal） |
| `DMGE` | 7638 | `M2PrADmgEffect`——受击特效 |
| `GaugeBrk` | 7639 | `MpGaugeBreaker`——魔力条碎裂 |
| `EggCon` | 7640 | `PrEggManager`——产卵相关 |
| `EpCon` | 7641 | `EpManager`——EP（被抓/吸取）系统 |
| `BetoMng` | 7642 | `BetobetoManager`——粘液 |
| `Skill` | 7643 | `M2PrSkill`——手杖/技能状态机（平砍、冲刺砍、空中砍、盾） |
| `GSaver` | 7644 | `PrGaugeSaver`——慢扣血/魔力缓冲条 |
| `JuiceCon` | 7645 | `M2PrAJuiceCon`——诺艾尔汁 |
| `AbsorbCon` | 7646 | `AbsorbManagerContainer`——被吸附/被骑乘 |
| `SpMp` | 24 | `SpecialMpGauge`（属性）——特殊魔力槽（预消耗段） |
| `VO` | 19 | `PrVoiceController`（属性，protected set）——语音 |
| `Phy` | — | `M2PhysPr`（`nel/M2PhysPr.cs`）——速度/重力/力 |
| `UP` | — | 玩家 UI 面板（`prepareUP` 见 `SceneGame.cs:171`） |

---

## 3. 数值与存档字段

- 新游戏：**HP 150/150，MP 200/200**（`nel/PRNoel.cs:22-23`；默认值同样是 `SVD.sFile.hp_noel/maxhp_noel = 150`、`mp_noel/maxmp_noel = 200`，见 `nel/SVD.cs:839-848`）。
- 玩家存档字段：`SVD.sFile`（`nel/SVD.cs:621`）；写入时 `mp_noel = get_mp() + SpMp.total_in_save`（`SVD.cs:683`）——**MP 存档包含"预消耗"段**，改 MP 逻辑时要一起考虑。
- 存档版本：玩家数据段第一字节 `PRNoel.writeBinaryTo` 写 **20**（`PRNoel.cs:202`），读取端按读到的版本号做兼容分支（`PRNoel.cs:126` 起）。**改存档布局必须同时改这两个地方**。
- 数值钳制统一走 `X.MMX(0, val, max)` / `X.Mn` / `X.Mx`（`XX` 命名空间；例：`M2Attackable.cs:243`、`271`）。

---

## 4. 移动、体型与碰撞

### 4.1 移动参数（`unsafeAssem/m2d/M2MoverPr.cs`，地图单位/帧 @60fps）

| 参数 | 0.30g 值 | 行号 |
|---|---:|---:|
| `walkSpeed` | 0.085 | 3556 |
| `runSpeed` | 0.17 | 3559 |
| `ySpeedStart` | -0.298 | 3565 |
| `ySpeedMax0` | 0.19 | 3562 |
| `ySpeedKeyReleased` | -0.06695（const） | 3580 |
| `ySpeedStart_water` / `ySpeedKeyReleased_water` | -0.158 / -0.35 | 3568 / 3571 |
| `accel_run_break` / `accel_run_break_air` | 0.0063 / 0.0044 | 3583 / 3586 |
| `double_tap_running` | true（static） | 3538 |

速度接口常用：`Phy.setWalkXSpeed(...)`、`Phy.addFoc(FOCTYPE…)`、`Phy.killSpeedForce(...)`、`calcWalkSpeed()`（`M2MoverPr.cs:1464`）。

### 4.2 体型：**游戏内部有三套彼此独立的"体型数据"**（改小骑士最容易踩的坑）

| # | 数据 | 出处（0.30g） | 用途 |
|---|---|---|---|
| 1 | `sizex` / `sizey` → 碰撞体 | `M2Mover.Size(float wmap, float hmap=-1000, ALIGN, ALIGNY, bool resize_moveby)`（`unsafeAssem/m2d/M2Mover.cs:113`）+ `M2MvColliderCreatorAtk.recreateExecute()`（`unsafeAssem/m2d/M2MvColliderCreatorAtk.cs:16`） | 被攻击命中、物理碰撞 |
| 2 | `event_sizex` / `event_sizey` / `event_cy` | `nel/PR.cs:7421-7443`：`event_cy = mbottom - 68f*rCLEN`、`event_sizex = 12f*rCLEN`、`event_sizey = 68f*rCLEN` | **门/出口/长椅/传送/NPC/狭窄处的地图互动判定**（`M2EventContainer` 拼矩形、`checkCurrentPoint` 取当前事件格） |
| 3 | `size_y_default_pixel` | `nel/PR.cs:146` 赋 68 | `forceCrouch` 判断"这格能不能站起来 / 要不要强制蹲伏" |

体型常量（`nel/PR.cs:7625-7632`，单位=像素）：

| 状态 | 值 |
|---|---|
| `size_x_normal` / `size_y_normal` | 12 × 68 |
| 蹲伏 `size_y_crouch` | 12 × 40 |
| 压扁 `size_y_presscrouch` | 12 × 10 |
| 倒地 `size_x_down` / `size_y_down` | 70 × 20 |
| 直线击飞 `size_x_damage_l` / `size_y_damage_l` | 12 × 24 |
| 脚底切片 `collider_foot_slice_px_x/y` | 8 / 46（`PR.cs:2037-2038`） |

体型状态机：`M2MoverPr.BOUNDS_TYPE`（`M2MoverPr.cs:3857`）= `OFFLINE / NORMAL / CROUCH / CROUCH_WIDE / DOWN / DAMAGE_L / PRESSCROUCH / EVADE_JUMP / ABSORB / AUTO`；
写入入口两个重载都在 `PR`：

- `protected override bool setBounds(M2MoverPr.BOUNDS_TYPE, bool force = false)`（`PR.cs:1964`）
- `protected bool setBounds(M2MoverPr.BOUNDS_TYPE, bool force, bool use_move_by)`（`PR.cs:1982`）← **模组夹紧体型挂的是这个 3 参重载**

蹲伏判定：`forceCrouch(bool fine_flag=false, bool fine_size=false)`（`M2MoverPr.cs:1604`）→ `checkForceCrouch(int cx)`（`:1657`）→ `recheckForceCrouch()`（`:1682`）。
`BOUNDS_TYPE` 是 `M2MoverPr` 的**受保护嵌套枚举**，模组侧要用反射拿。

### 4.3 单位与坐标

| 概念 | 值 | 出处 |
|---|---|---|
| `CLEN` | 28（1 格 = 28 mesh px） | `Map2d.cs:6729`（另有 `M2DBase.cs:2598`） |
| `rCLEN` | 0.035714287（= 1/28，格 = 像素 × rCLEN） | `Map2d.cs:6732` |
| 地图 y 方向 | 网格 y 向下为正；`pixel2uy` 会翻转（ux 内 y 向上为正） | 见 `m2d` 换算工具 |
| 常用坐标 | `x/y` 是中心、`mbottom` 是脚底 | `M2Mover` / `M2Attackable` |

> `event_*` 那几个 getter 返回的是**格**（像素 × `rCLEN`），不是 ux，也不是 mesh px——改判定框时最容易在这里差一个 2.286 倍。

---

## 5. 玩家状态机 `PR.STATE`（`nel/PR.cs:7800-7864`，0.30g 全量）

```
_OFFLINE = -1, NORMAL, MAG_EXPLODE_PREPARE, MAG_EXPLODED
EVADE = 10, UKEMI, EVADE_SHOTGUN, UKEMI_SHOTGUN, EVADE_JUMP
PUNCH = 20, BURST = 22, SLIDING, WHEEL, WHEEL_SHOTGUN, COMET, COMET_SHOTGUN,
DASHPUNCH, DASHPUNCH_SHOTGUN, AIRPUNCH, AIRPUNCH_SHOTGUN
SHIELD_BUSH = 40, SHIELD_LARIAT, EVADECOUNTER, EVADECOUNTER_SHOTGUN, SMASH, SMASH_SHOTGUN
BURST_SCAPECAT = 200, SP_RUN = 250, USE_BOMB = 390
ENEMY_SINK = 430, SHIELD_BREAK_STUN, LAYING_EGG, ORGASM, GAMEOVER_RECOVERY,
WATER_CHOKED_RELEASE = 436, SLEEP, FROZEN, BINTA_SINK
ONNIE = 500, EV_GACHA
DAMAGE = 4000, DAMAGE_L = 4010, DAMAGE_L_HITWALL, DAMAGE_L_DOWN_ABSORBAFTER = 4016,
DAMAGE_L_LAND = 4015, DAMAGE_LT = 4020, DAMAGE_LT_KIRIMOMI = 4022, DAMAGE_LT_LAND = 4025,
DAMAGE_OTHER_STUN = 4050, DAMAGE_PRESS_LR = 4030, DAMAGE_PRESS_TB
DOWN_STUN = 4100, DAMAGE_BURNED = 4150
DAMAGE_WEB_TRAPPED = 4200, DAMAGE_WEB_TRAPPED_LAND
ABSORB = 4600, WORM_TRAPPED = 4980, WATER_CHOKED, WATER_CHOKED_DOWN
BENCH = 10000, BENCH_LOADAFTER, BENCH_ONNIE, BENCH_SITDOWN_WAIT = 100003
```

切换统一走 `PR.changeState(PR.STATE)`（模组挂过）、`M2PrADmg.changeState(PR.STATE, PR.STATE, bool, bool, bool)`（`nel/M2PrADmg.cs:53`）、以及 `PRMain.changeState`（长椅/战败等特例）。
同一文件里还有 `PR.OCCUR`（`:7866` 起，`HITSTOP_* / BURNED_FADER / …`）控制镜头停滞等演出。

---

## 6. 输入系统

链路：**Unity Input System（InputActionAsset）→ `XX.KEY` → `XX.IN` 静态辅助 → 玩家控制器**。

- `KEY.IPT` 枚举（`unsafeAssem/XX/KEY.cs:3375`）共 **30 个 action**：`SUBMIT, SUBMIT2, CANCEL, CANCEL2, MENU, LTAB, RTAB, SORT, SHIFT, ADD, REM, LA, TA, RA, BA, JUMP, RUN, CHECK, Z, X, C, A, S, D, LSH, M_NEUTRAL, MLA, MTA, MRA, MBA`（`_MAX` 结尾）。
- `KEY.SIMKEY`（`KEY.cs:3306`）是**位集**（脚本可模拟按键）：`L/R/T/B = 1/2/4/8`、`Z=16, X=32, C=64, LSH=128, ESC=256, SUBMIT=512, CANCEL=1024, LTAB=2048, RTAB=4096, MENU=8192, MAP=16384, ITEM=32768, RUN=65536, JUMP=131072, SORT=262144, ADD=524288, REM=1048576, SHIFT=2097152,` **`CHECK=4194304`**`, A=8388608, D=16777216, S=33554432…`
- 剧情锁：`EV.lockPrInputManipulate(KEY.SIMKEY key, bool default_flag = true, bool is_or = false)`（`unsafeAssem/evt/EV.cs:3048`）；事件进行中判断 `EV.isActive(...)`（`EV.cs:3004` / `:3010`）。
- 事件/出口重检：`M2MoverPr.need_check_event`（`M2MoverPr.cs:14`）——**用 `moveBy` 强行搬动玩家后必须置 true**，否则门口/事件不会触发（模组踩过这个坑）。
- 默认键位（`KEY.cs` 的 `setKeyboardAndPadInput` 段，例：CHECK 绑 `c`，见 `KEY.cs:252`）：方向键移动、`Z/X` 提交/取消、`C` CHECK、`A` 盾/排序、`S/D` 技能，**F1~F5 是魔法瞄准方向（F5 已被占用，不能当模组切换键）**。

---

## 7. 伤害管线（敌人 → 诺艾尔）

```
敌人攻击 → AttackInfo / NelAttackInfo（含击退、盾反、吸收、粘液等字段）
  → M2PrADmg.applyHpDamageSimple(NelAttackInfoBase, out bool, int val, bool show)   nel/M2PrADmg.cs:1031
       └ 内部：val = Atk._hpdmg × (fix_damage ? 1 : applyHpDamageRatio(Atk))        M2PrADmg.cs:1038
  → M2PrADmg.applyDamage(NelAttackInfo, bool, string, bool, bool)                   M2PrADmg.cs:1102
    M2PrADmg.applyDamage(NelAttackInfo, ref HITTYPE, bool, string, bool, bool)       M2PrADmg.cs:1109
  → M2Attackable.applyHpDamage(int val, bool force, AttackInfo Atk)                 M2Attackable.cs:263
       └ val = overkill ? val : X.Mn(val, hp); hp = X.Mx(hp - val, 0)               M2Attackable.cs:270-271
       └ hp <= 0 → initDeath()（失败时会兜底 hp = 1）                                M2Attackable.cs:272-274
```

- 其它入口：`cureHp`（`:241`）、`applyMpDamage`（`:280`）、`applyHpDamageRatio`（`:298`，"非满血减伤"就来自这里）、`isNoDamageActive()`（`:304`）、`M2PrADmg.applyHpDamageRatio(AttackInfo)`（`M2PrADmg.cs:1619`）、`applyDamageAddition`（`M2PrADmg.cs:1084`）。
- MP：`PR.applyMpDamage`（两个重载）、`M2Attackable.applyMpDamage`、`MpGaugeBreaker.*`（魔力条碎裂）。
- EP（被抓/吸取）：`EpManager.applyEpDamage`。状态效果：`M2Ser.Add(SER ser, int __maxt=-1, int max_level=99, bool add_to_pre_bits=false)`（`nel/M2Ser.cs:251`）。
- 敌人侧：`NelEnemy.applyDamage(NelAttackInfo Atk, bool force=false)`（`nel/NelEnemy.cs:1797`）与 `applyDamage(NelAttackInfo, ref HITTYPE, bool)`（`:1804`）——**模组打怪走的就是这两个**（伤害数字/硬直/掉落全部复用）；硬直判定 `checkDamageStun`（`:2352`），死亡 `changeStateToDie`（`:2951`）。
- `NelAttackInfo : NelAttackInfoBase`（`nel/NelAttackInfo.cs:8`）关键字段：`absorb_replace_prob`（`:184`）、`absorb_replace_prob_ondamage`（`:187`）、`ignore_nodamage_time`（`:190`）、`pr_myself_fire`（`:193`）、`huttobi_ratio`（`:196`，击飞）、`shield_break_ratio = 1f`（`:199`）、`torn_apply_min/max`（`:202`/`:205`）、`pee_apply100`（`:208`）、`hit_r`（`:211`）、`parryable = true`（`:214`，可盾反）、`setable_UP`（`:217`）。

---

## 8. 动画系统

- 角色动画 = PixelLiner 的 `M2PxlAnimatorRT`（`unsafeAssem/m2d/M2PxlAnimatorRT.cs`），诺艾尔动画源 key 为 `"noel"`（`PRNoel.createAnimator`，`PRNoel.cs:46`）。
- 姿势接口：`M2PxlAnimator.setPose(string title, int restart_anim = -1)`（`unsafeAssem/m2d/M2PxlAnimator.cs:158`）；朝向 `setAim(AIM)`（同文件 `:102` 处调用）。
- 姿势类型枚举 `POSE_TYPE`（例：`POSE_TYPE.DOWN` / `POSE_TYPE.CROUCH`，见 `PR.cs:712` 的 `setBounds` 判定）——**游戏靠姿势名决定"要不要蹲/趴"**，模组据此决定"哪些姿势要放行"。
- `PrNoelAnimator : PrBakeAnimator, ICaneDroppableAnimator`（`nel/PrNoelAnimator.cs:10`）管理换装、手杖持有、表情；换姿势监听 `fnChangePoseListener`（模组用它做姿势白名单）。

---

## 9. 渲染管线（为什么"普通 SpriteRenderer 画不出来"）

角色**不走普通相机 + SpriteRenderer**，而是走游戏自绘 GL 的"渲染票据"：

```
M2PxlAnimatorRT.initRenderTicket(M2Mover.DRAW_ORDER order)                M2PxlAnimatorRT.cs:12
   └ Mp.MovRenderer.assignDrawable(order, null,
        new M2RenderTicket.FnPrepareMd(RenderPrepareMesh), null, Mv, null) M2PxlAnimatorRT.cs:16
   撤销：M2DBase.Instance.Cam.MovRender.deassignDrawable(RTkt, -1)        M2PxlAnimatorRT.cs:28 / :65
```

- 票据容器：`M2Camera.MovRender`（`unsafeAssem/m2d/M2Camera.cs:48` 创建，类型 `M2MovRenderContainer`）。
- 排序枚举 `M2Mover.DRAW_ORDER`（`unsafeAssem/m2d/M2Mover.cs:2917`）：`_NO_USE, MASK_B, MASK_G, MASK_T, N_BACK0, N_BACK1, N_BACK_EF0, N_BACK_EF1, BUF_0, BUF_1, BUF_2,` **`PR0, PR1, PR2`**, …（`PR0/PR1/PR2` 在 `:2930-2932`，即玩家身后/身前分层）。
- 结论：模组必须把自绘网格注册进 `MovRenderer`（与玩家同通道），否则任何标准 Unity 渲染组件都不在可见相机里。换图/读档/快速旅行会重建渲染容器，**旧票据会失效，必须重绑**。

---

## 10. 场景初始化、存档与长椅

- 玩家不是代码创建的，是场景预制体：`SceneGame.cs:131` `this.PrNoel = this.GobNoel.GetComponent<PRNoel>();`，`:132` `SceneGame.M2D.PlayerNoel = this.PrNoel;`。
- 地图加载后：`M2D.AddToCoreMover(prNoel)`（`SceneGame.cs:320`）＋ `prNoel.newGame()`（`:321`）；UI 侧 `prepareUP(...)`（`:171`）、`UP.newGame()`（`:201`）。
- 运行时取玩家：`NelM2DBase.getPrNoel()` / `NelM2DBase.PlayerNoel`（`nel/NelM2DBase.cs:267` 等处）。
- 通用存档键值：`COOK.setSF/getSF`（`nel/COOK.cs`），随存档二进制序列化；场景初始化钩子 `COOK.initGameScene`（`COOK.cs:212`）。
- 长椅：`NelChipBench`（`nel/NelChipBench.cs`，点亮地图图标 `fineIcon` `:60`）；重生点 `SVD.assignRevertPosition(NelM2DBase)`（`nel/SVD.cs:728`）。
- 快速旅行：`M2LpMapTransferBase.executeTransferFastTravel`（`nel/M2LpMapTransferBase.cs:550`）、`UiBenchMenu.ExecuteFastTravel`。

---

## 11. HUD（`nel/UIStatus.cs`）

| 成员 | 行号 | 说明 |
|---|---:|---|
| `MdH` / `MdM` / `MdMpEgg` | 41–43 | HP / MP / MP-蛋 的 `MeshDrawer`（**改血条颜色就是改这里的顶点色**） |
| `O2Gauge` | 74 | 氧气槽（`UISpecialGage`），模组用 `showO2Gauge` 钩子处理 |
| `t`（`:3437`，public）/ `t_settop`（`:3166`）/ `base_y_level`（`:3328`）/ `ui_hold_time`（同文件） | — | HUD 显隐/淡入淡出状态机。注意：本文件里还有嵌套类带同名私有字段，反射取字段时要按**声明类型**定位 |
| `redrawAll` | 558 | 重建 HUD 网格入口（模组挂它做染色） |
| `UIBase.fineHpMpRatio` | `nel/UIBase.cs:1917` | 比例刷新（模组挂它喂小骑士数值） |
| 左侧立绘 | `nel/UIPicture.cs` / `nel/UIPictureBase.cs` | 表情 `changeEmotIn/changeEmotDefault/readFader`（`UIPictureBase.cs:380` 附近） |

---

## 12. 敌人 / 魔法对象 / 掉落物 / NPC 体系（模组交互面）

| 类型 | 0.30g 位置 | 说明 |
|---|---|---|
| `NelEnemy` | `nel/NelEnemy.cs` | 敌人基类；`applyDamage` 两个重载见第 7 节；`initAbsorb`（`:1352`）等被抓/吸取状态 |
| `MagicItem` | `nel/MagicItem.cs` | 法术/投射物的运行时对象 |
| `MGKIND` / `MGHIT` | `nel/MGKIND.cs` / `nel/MGHIT.cs` | 魔法种类 / 命中旗标枚举；联机模组只放行 `MGKIND.PR_*` 段的玩家攻击 |
| `M2LpSummon` / `EnemySummoner` | `nel/M2LpSummon.cs` / `nel/EnemySummoner.cs` | 战斗区域（魔力草）拉起与战幕状态 |
| `NelItemManager` | `nel/NelItemManager.cs` | 掉落物：`ODrop`（`:67`，`BDic<M2DropObject, NelItemDrop>`）、嵌套类 `NelItemDrop`（`:3186`）、`getStorageFor`（`:1404`，private）、拾取入口 `executePickUp`（护符 2 自动拾取就靠这几个） |
| NPC 魔物 | `nel/MvNelNNEAListener.cs`（嵌套类 `NelNNpcEventAssign`） | 商人/酒保/傀儡等"可对话魔物"都派生自它；区分敌对怪与 NPC 只能沿继承链判断 |
| 农场魔物 | `nel/mgm/farm/NelNMgmFarmAnimal.cs` | 鸡/牛；模组对它有单独的梦语与免伤处理 |
| 可破坏物 / 机关 | `nel/M2MatoateTarget.cs`、`nel/M2PuncherCannon.cs`、`nel/M2BreakableWallMover.cs`、`nel/M2WormTrap.cs` | 靶子、拳炮、可破坏墙、虫巢抓取 |
| 物理 / 脚部 | `unsafeAssem/m2d/M2FootManager.cs`（`rideInitTo` `:239`）、`nel/M2PhysPr.cs` | 骑乘/挂脚、玩家物理 |

> 联机模组相关类型（如 `Kaleidoscopic.Syncs.M2Gunmu`）**不在** AIC 树里，属于 Kaleidoscopic 自己的程序集。

---

## 13. 本模组实际挂的 Hook 点（0.30g 实测 `71 成功 / 0 失败`）

以下标签就是 `CombatGuard.Apply` 里 `TryPatch(...)` 的清单，**共 71 条**（与启动日志的 `71 成功 / 0 失败` 同源；**改动不生效时先看启动日志这一行**）。分组速览：

| 组 | 入口 |
|---|---|
| 伤害 | `applyHpDamageSimple`、`M2Attackable.applyHpDamage`、`M2PrADmg.applyDamage`、`M2PrADmg.applyDamage(ref HITTYPE)`、`M2PrADmg.applyDamageAddition`、`M2PrADmg.applyHpDamageRatio`、`PR.applyDamage(Atk,bool)`、`NelEnemy.checkDamageStun`、`MDAT.applyWormTrapDamage` |
| MP/魔力 | `PR.applyMpDamage`（两个重载）、`M2Attackable.applyMpDamage`、`MpGaugeBreaker.applyDamage/check/gageDamage` |
| 状态 | `PRMain.changeState`、`M2PrADmg.changeState`、`PR.changeState(PR.STATE)`、`M2Ser.Add`、`EpManager.applyEpDamage`、`M2PrMistApplier.activate/run`、`MistManager.addSinkMover`、`PR.applyGasDamage`（两个重载） |
| 控制类 | `PR.initAbsorb`、`NelEnemy.initAbsorb`、`M2FootManager.rideInitTo`、`PR.canPullByWorm`、`M2SinkEffect.addMover`、`FallenCutin.run`、`FallenCutin.setE`、`M2MovePatSneaker.checkSightPrCheck` |
| 体形 | `M2Mover.Size`、`M2MvColliderCreatorAtk.recreateExecute`、`PR.setBounds(BOUNDS_TYPE,bool,bool)`、`PR.get_event_cy`、`PR.get_event_sizex`、`PR.get_event_sizey` |
| 视觉/演出 | `M2PxlAnimatorRT.set_color`、`M2PxlAnimatorRT.set_alpha`、`UIPicture.run`、`UIPictureBase.changeEmotIn/changeEmotDefault/readFader/applyDamage`、`UIPicture.applyGasDamage`、`PostEffect.setPE` |
| 输入 | `EV.lockPrInputManipulate`、`M2PrSkillShieldEvade.runState/isShieldOpeningOnNormal`、`M2PrSkill.isShotgun` |
| 数值读口 | `M2Attackable.get_hp/get_maxhp/get_maxmp`、`PR.getCastableMp`、`PR.isNoDamageActive()`、`PR.runPre(noCarry)`、`PR.applyWindFoc` |
| HUD | `UIStatus.run`、`UIStatus.redrawAll`、`UIBase.fineHpMpRatio`、`UIStatus.showO2Gauge` |
| 世界接入 | `COOK.initGameScene`、`M2LpMapTransferBase.executeTransferFastTravel`、`UiBenchMenu.ExecuteFastTravel`、`M2LpSummon.deassignActiveWeed`、`WholeMapManager.fnMgRun_initS_Sacred` |
| 特化 | `M2PuncherCannon.applyHpDamage`、`NelNBossSpider.applyDamage`、`MgBsSpiderTrap.run` |

配套：这 71 条目标可用离线体检工具 `tools/PatchProbe` 预先核对（目标清单在 `tools/PatchProbe/*_targets.txt`，其中 `failing_targets.txt` 记录过 0.30g 早期版本上"方法在、但 IL 无法被 Harmony 重写"的那批）。游戏再更新时先跑它，再对照启动日志的 `[KIC][补丁]` 行与 `[KIC][补丁失败详情]`。

另外还有**独立计数**的补丁组，不在上面这 71 条里：护符效果（含指南针，`src/CharmUi/CharmEffects.cs`）、护符 UI 输入拦截（`CharmUiInputPatch`）、原生物理接管（`NativeBody`）、联机兼容（`MultiplayerCompat`）。

---

## 14. 0.29 → 0.30g 变化与踩坑提示

### 14.1 玩家相关文件的规模变化（去掉 `// Token:` 注释行后逐行比对，**全部有实质改动**）

| 文件 | 0.29（`reference/`） | 0.30g（`_tmp_aic_src_new/`） |
|---|---:|---:|
| `nel/PRNoel.cs` | 568 | 681 |
| `nel/PR.cs` | 7124 | 7264 |
| `nel/PRMain.cs` | 543 | 552 |
| `nel/M2PrADmg.cs` | 2098 | 2108 |
| `nel/M2PrSkill.cs` | 5074 | 5406 |
| `nel/NelEnemy.cs` | 5346 | 5451 |
| `nel/PrNoelAnimator.cs` | 740 | 776 |
| `nel/UIStatus.cs` | 3195 | 3209 |
| `nel/SVD.cs` | 607 | 814 |
| `nel/SceneGame.cs` | 359 | 369 |
| `nel/NelAttackInfo.cs` | 189 | 192 |
| `nel/PrStateInjector.cs` | 146 | 146（行数同，但内容有改动） |

（`unsafeAssem` 侧同样有改动：`_tmp_unsafe_src/`（0.30d）与 `_tmp_aic_src_new/unsafeAssem/`（0.30g）可对照。）

### 14.2 已知的具体变化 / 踩坑点

1. **继承链多了一层**：`M2Attackable → M2AttackableP → M2MoverPr`（0.29 是 `M2Attackable → M2MoverPr`）。
2. **`PRNoel.createAnimator` 签名变了**：现在是 `createAnimator(ref PrAnimator Anm)`。
3. **存档版本 19 → 20**：`PRNoel.writeBinaryTo` 首字节写 20（`PRNoel.cs:202`）。
4. **`OUTFIT` 枚举扩充**：0.30g 多了 `TORNED / BUNNY / BUNNYREV` 等。
5. **体型是三套数据**（第 4.2 节）：只改 `sizex/sizey` 不够，`event_*` 与 `size_y_default_pixel` 必须一起改，且夹紧要发生在**物理之前**（`runPre` 之后、`runPhysics` 之前），否则地形判定仍按原体型。
6. **`BOUNDS_TYPE` 是受保护嵌套枚举**，模组侧只能反射取；3 参 `setBounds` 在 `PR.cs:1982`（不是 `void`，返回 `bool`）。
7. **不要用 `_tmp_aic_src/` 或 `reference/` 的行号**去核对 0.30g 的行为——两棵树行号对不上；本文行号一律以 `_tmp_aic_src_new/` 为准。

---

## 附：相关文档

- `docs/KIC_核心机制与实现总结.md`：模组自身的机制与实现总结（含联机、护符、踩坑记录）
- `docs/KIC_联机适配实现说明（给联机模组作者）.md`：给 Kaleidoscopic 作者的接口说明
- `docs/M3_外观切换原型说明.md` / `docs/M2_素材提取记录.md` / `docs/M4_护符系统_素材清单.md`：素材与渲染探索过程
- `src/strategy.cs`：判定框调试方法论、坐标换算、拉伸碰撞箱套路、联机索引表方案（S）
