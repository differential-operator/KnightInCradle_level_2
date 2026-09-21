# KnightInCradle（小骑士模组）联机适配实现说明

面向：Kaleidoscopic（联机模组）作者
版本：KIC build = 2026-09-20.1
说明：KIC **没有改动 Kaleidoscopic 的源码/二进制**，全部通过 Harmony 按“类型名 + 方法名”反射挂补丁，
      联机模组不存在时所有补丁自动跳过（启动时扫描 AppDomain 里有没有名字含 `Kaleidoscopic` 的程序集）。

---

## 一、远端渲染

### 1.1 接入点

| 挂在哪 | 类型/方法 | 补丁类型 | 作用 |
| --- | --- | --- | --- |
| 发送端 | `Kaleidoscopic.Syncs.SyncPatcherPlayers.createPlayerInfo(PR)` | postfix | 本地是小骑士时，往生成的 `PlayerInfo` 里写骑士状态与自定义负载 |
| 接收端 | `Kaleidoscopic.Syncs.SyncPatcherPlayers.render1(ProjectionContainer, PlayerInfo, float)` | prefix | 远端是骑士时自己绘制，并**返回 false 跳过原版诺艾尔绘制** |

### 1.2 发送端写了哪些字段（复用 `PlayerInfo` 现有字段）

| 字段 | 内容 | 说明 |
| --- | --- | --- |
| `characterTitle` | `"__KNIGHT__"` | **骑士标记**，接收端只认这个字符串 |
| `caneName` | 当前帧精灵名，如 `idle_still_020000` | 本体贴图**按名字**取，接收端从自己的 `assets/hk/` 里加载同名 PNG |
| `poseTitle` | 剪辑名（`Idle`/`Run`/`Slash`…） | 动画状态（接收端目前主要用精灵名绘制，此字段留作扩展） |
| `frameIndex` | 剪辑内帧号 | 同上 |
| `aimInt` | 朝向（1=右、-1=左） | 镜像 |
| `nameColor` | 骑士标记色 | KIC 内部用 |
| `ay` | **骑士脚底 Y**（游戏 y 向下为正） | 远端把贴图脚底钉在地面；老版本无该值时回退用 `y` |
| `hp` / `hpmax` / `mp` / `mpmax` | 骑士血量 / 灵魂 | 远端头顶条与血条 |
| `shieldData` | **自定义特效负载**（见 1.3） | 前 44 字节是“空护盾 DTO”占位，之后的字节是 KIC 自己的数据 |
| `curMgKindInt` / `skillMpHold` / `curMgReduceMp` | 0 | 避免远端画出诺艾尔的魔法图标 |

### 1.3 `shieldData` 负载布局（小端）

```
[0..43]   44 字节全 0            —— 空护盾 DTO（alpha=0，联机模组会跳过护盾绘制）
[44]      版本号（3/4/5）
[45]      标志位（bit0 = 本体贴图需要水平镜像）
[46]      四边形数量 N
之后 N × 30 字节：
  int16  ×1   贴图下标（见 1.4 的索引表）
  int16  ×8   四个角相对“本体中心”的偏移，单位 = 本体高度 × 1024
  byte   ×8   四个角各自的 UV（×255，保留镜像朝向）
  byte   ×4   颜色 RGBA
[v4+] 下砸骨剑/尖刺：byte 数量 + 每个 13 字节
[v5]  骨钉攻击判定箱（拼刀用）：byte 数量 + 每个 9 字节
        int16 ×4 相对骑士中心的 (dx, dy) 与尺寸 (w, h)，单位 = 格 × 1024
        byte  ×1 招式类别（1=普攻 2=强力劈砍 3=冲刺劈砍 4=旋风劈砍）
[v5+] 蜕变挽歌剑气矩形 / 蘑菇孢子云 / 亡者之怒透明度 / 防御者法阵 等语义段
```

设计要点：

- 坐标全部用**相对本体中心、以本体高度为单位**的归一化值，远端按“画面上本体的绘制高度”还原，
  因此不依赖房间尺度/坐标系，也不需要绝对世界坐标。
- 本体网格本身**不放进四边形列表**（否则远端会出现第二个小骑士），它由 `caneName` 单独驱动。
- 负载按帧缓存（同一帧内多次取用只构建一次）。

### 1.4 贴图索引表（跨机器一致性的关键）

`KnightFxSync` 在两端各自构建同一张“精灵名 → 下标”表：

1. 先读 `assets/hk/knight_manifest.json` 的 sprites 列表；
2. 再把 `assets/hk/**/*.png` 里所有文件名（去扩展名）补进列表；
3. 追加一个虚拟键 `knight_spore_dot`（孢子粒子的程序化圆点）；
4. **按 `StringComparer.Ordinal` 排序**后固定下标。

因此：**两台机器的 `assets/hk` 文件集必须完全一致**，否则下标会整体错位
（表现：特效画错图或干脆没有、本体可能因缺帧完全不显示）。
本体贴图用“名字”取，只要同名 PNG 在，本体就能画出来——所以“完全看不到人”通常是**缺素材**，
日志会打 `[KIC][联机同步] 远端小骑士本体贴图缺失: <帧名>`。

### 1.5 接收端绘制

`render1` 前置里：

1. 读 `characterTitle`，不是 `__KNIGHT__` → `return true`（照原样让联机模组画诺艾尔）；
2. 是 → `DrawRemoteKnight(op)`：
   - `LoadKnightFrame(caneName)` 从本机 `BepEx/plugins/KnightInCradle/assets/hk/` 加载该帧 PNG（带缓存）；
   - 用 `CharacterInfo.x` + `ay`（脚底 Y）投影到屏幕，按“本体高度”等比缩放；
   - 用 mesh 绘制本体 + 负载里的四边形特效；
   - **返回 false** 抑制原版诺艾尔绘制。

---

## 二、伤害与受击

### 2.1 攻击端（本地小骑士打远端玩家/魔物）

接入点：`Kaleidoscopic.Syncs.M2Gunmu.applyHpDamage(int val, bool force, AttackInfo Atk)` 的 **prefix**
（所有“打中远端代理 → 生成 OutboundDamagePacket”都汇聚在这里）。

做的事：

1. **把攻击伪装成原版玩家拳击**，让 AIC/转发层按“玩家攻击”处理：
   `mg.kind = MGKIND.PR_PUNCH`，`mg.hittype = MGHIT.PR | IMMEDIATE | NORMAL_ATTACK`；
2. **补偿转发层的玩家伤害压缩**：目标若是远端玩家（诺艾尔/另一名小骑士），
   `val = round(val × PvPDamageMultiplier)`（cfg `Multiplayer/PvPDamageMultiplier`，当前默认 2，
   实测等价于抵消转发层的固定 ×0.5）；
3. **打标记**（不改数值，只给接收端识别用）：
   - `knockback_ratio_t = 0.777` → “这一包来自小骑士”（诺艾尔自己的攻击是 1.0）；
   - 受击反馈类别（由 KIC 的攻击状态决定）：
     - 轻受击：只带 0.777 标记；
     - 直线击飞（冲刺劈砍 / 下砸）：`burst_vx / burst_vy`（方向＝远离攻击者，冲刺劈砍 ×1.5）；
     - 着火（复仇之魂 / 深渊尖啸）：`attr = FIRE`；
     - 暗影冲刺：`burst_center = 0.6243`（魔法数字标记，见 2.4）；
   - 同一次命中只补偿一次（用 `(AttackInfo 引用, 帧号)` 去重，避免多段/嵌套调用重复乘倍率）；
4. 补上诺艾尔普攻同款击退参数，保证远方有击退/受击表现。

### 2.2 收包端（别人打过来）

接入点（都在 KIC 本地补丁里，作用于“远端小骑士打本地诺艾尔”的路径）：

| 类型/方法 | 补丁 | 作用 |
| --- | --- | --- |
| `m2d.M2PrADmg.applyDamage(...)` | prefix | 识别 `kind ∈ MGKIND.PR_*` 且 `knockback_ratio_t ≈ 0.777` 的包 → 打上 `na.fix_damage = true`；同时读 `burst_center / attr / burst_vx,vy` 分派受击反馈 |
| `m2d.M2PrADmg.applyHpDamageRatio(AttackInfo)` | postfix | 若 `na.fix_damage` → 强制返回 1.0，**跳过 AIC 自带的“非满血减伤”**；诺艾尔自己的攻击不带该标记，行为不变 |

受击反馈的实现：因为 `fix_damage` 会让 AIC 跳过自身的受击状态处理，KIC 在收包端显式驱动：
`DAMAGE`（轻受击，1 秒内不重复）/ `DAMAGE_L`（直线击飞）/ `SER.BURNED`（着火，120 帧，灼烧伤害屏蔽为 0），
并通过 `SfPose` + `SpSetPose` 让右侧模型与左侧立绘同步播放。

### 2.3 数值联动关系（重要）

```
落地伤害 ≈ 本地伤害 × PvPDamageMultiplier × 转发层玩家伤害系数（当前实测 0.5）
```

- 只有“攻击方”的倍率影响数值，受击方不缩放（只挡 AIC 减伤）；
- 单机没有远端玩家，配置值无影响；
- 若将来转发层改变压缩比（例如变成 1/3），KIC 侧把这个 cfg 值改成对应数字即可，无需改代码。

### 2.4 目前用到的“魔法数字”标记（供联机模组作者知悉风险）

这些值写在 `AttackInfo` 的浮点字段里当“信令”，彼此间隔 ≥0.01 以避免浮点误差误判：

| 标记值 | 字段 | 含义 |
| --- | --- | --- |
| `0.777` | `knockback_ratio_t` | 这一包来自小骑士（同时用于受击反馈分派） |
| `0.4242` | `burst_center` | 吸虫之巢（触发远端虫墙立绘/奖励等） |
| `0.5243` | `burst_center` | 防御者纹章法阵（命中扣 10 MP） |
| `0.6243` | `burst_center` | 锋利之影暗影冲刺（轻受击） |

---

## 三、与联机模组目前的耦合点（如果你要改这些地方，麻烦知会一声）

1. **`shieldData` 被当作自定义通道使用**：KIC 用 44 字节全 0 的“空护盾 DTO”占位，
   后面跟自己的负载。只要保持“`shieldData` 会原样透传、且长度不限得太死（小于几千字节）”，这套就能继续工作。
   如果将来护盾 DTO 长度变化，请把新长度告诉 KIC（代码里是 `KnightFxSync.StubLen = 44`）。
2. **复用了若干 `PlayerInfo` 字段**：`characterTitle / caneName / poseTitle / frameIndex / aimInt /
   nameColor / ay / curMgKindInt / skillMpHold / curMgReduceMp`。语义或发送与否若有变化，KIC 需要同步改。
   （`ay` 是 KIC 新加语义的字段，用来广播“骑士脚底 Y”。）
3. **玩家伤害压缩系数**：KIC 目前靠 cfg 手动预乘抵消（默认 2 假设 ×0.5）。
4. **精灵下标表**：两端资产集必须一致（KIC 侧按 `Ordinal` 排序建表；缺文件就会错位）。

## 四、可选的“官方接口”建议（能省掉不少 hack）

如果联机模组愿意开一点口子，KIC 可以更稳、更省性能：

1. **自定义玩家负载通道**：提供一个“随 `PlayerInfo` 一起透传的字节数组（长度上限 ≥2KB）”，
   KIC 就不必借用 `shieldData`；
2. **玩家状态字段/枚举**：例如 `playerState`（0=诺艾尔 1=小骑士 …），比 KIC 现在借 `characterTitle` 判断更稳；
3. **伤害 API**：例如“以玩家 P 为来源、对目标 T 施加 (伤害, 击退方向/力度, 受击类型)”的直接接口，
   就能免掉“改写 `MGKIND.PR_PUNCH` + 魔法数字标记”的做法；
4. **转发系数告知**：如果转发层必须压缩玩家伤害，建议把系数写进包或配置（KIC 现在只能靠 cfg 手调）；
5. **精灵索引用名字而非下标**（或提供“名字↔下标”查询），可以彻底摆脱两端资产集必须逐字节一致的限制。

---

## 五、附：KIC 侧用到的补丁点清单（本版本）

```
发送：Kaleidoscopic.Syncs.SyncPatcherPlayers.createPlayerInfo(PR)              postfix
接收：Kaleidoscopic.Syncs.SyncPatcherPlayers.render1(ProjectionContainer,
                                                      PlayerInfo, float)       prefix
伤害：Kaleidoscopic.Syncs.M2Gunmu.applyHpDamage(int, bool, AttackInfo)         prefix / postfix
头顶条：Kaleidoscopic 远端玩家 HUD 绘制入口（RenderHpMpBar 等）                prefix
自身（非联机模组部分，仅本地）：
  m2d.M2Attackable.applyHpDamage / applyMpDamage
  m2d.M2PrADmg.applyDamage(ref HITTYPE) / applyHpDamageRatio / applyDamageAddition
  nel.PR.applyDamage / applyMpDamage / applyGasDamage / runPre / changeState …
```

若上面任何一个类型/方法在你的新版本里换了名字或签名，KIC 会在启动日志里报
`[KIC][补丁] … N 失败（… | [KIC][补丁失败详情] 完整堆栈）`，按日志把名字对上即可。
