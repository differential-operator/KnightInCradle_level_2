using System;
using System.Collections.Generic;
using System.Reflection;
using evt;
using HarmonyLib;
using m2d;
using nel;
using nel.gm;
using UnityEngine;
using XX;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符效果系统：按“已装备 / 未装备”状态驱动各护符效果。
    /// 当前实现：1 任性的指南针（地图快速旅行门控）。
    /// </summary>
    public static class CharmEffects
    {
        public const int CompassId = 1;
        public const int CollectorId = 2; // 蜂群集结
        public const int SturdyId = 3;    // 坚硬外壳
        public const int SoulCatcherId = 4; // 灵魂捕手
        public const int ShamanId = 5;    // 萨满之石
        public const int SoulEaterId = 6; // 噬魂者
        public const int DashmasterId = 7; // 冲刺大师
        public const int RunnerId = 8;    // 飞毛腿
        public const int GrubsongId = 9;  // 幼虫之歌
        public const int ElegyId = 10;    // 蜕变挽歌
        public const int HeartId = 11;    // 坚固心脏
        public const int GreedId = 12;    // 坚固贪婪
        public const int PowerId = 13;    // 坚固力量
        public const int SpellTwisterId = 14; // 法术扭曲者
        public const int StableId = 15;   // 稳定之体
        public const int HeavyBlowId = 16; // 沉重之击
        public const int FastSlashId = 17; // 快速劈砍
        public const int LongNailId = 18;  // 长钉
        public const int PrideId = 19;     // 骄傲印记
        public const int FuryId = 20;      // 亡者之怒
        public const int ThornsId = 21;    // 苦痛荆棘
        public const int BaldurId = 22;    // 巴尔德之壳
        public const int NestId = 23;      // 吸虫之巢
        public const int ShelterId = 24;   // 防御者纹章
        public const int UterusId = 25;    // 发光子宫
        public const int FastGatherId = 26; // 快速聚集
        public const int DeepGatherId = 27; // 深度聚集
        public const int BlueHeart1Id = 28; // 生命血之心
        public const int BlueHeart2Id = 29; // 生命血核心
        public const int JohnnyId = 30;     // 乔尼的祝福
        public const int HiveId = 31;       // 蜂巢之血
        public const int MushroomId = 32;   // 蘑菇孢子
        public const int ShadowId = 33;     // 锋利之影
        public const int UnnId = 34;        // 乌恩之形
        public const int NailMasterId = 35; // 骨钉大师的荣耀
        public const int SpiderId = 36;     // 编织者之歌
        public const int DreamId = 37;      // 舞梦者
        public const int DreamShieldId = 38; // 梦之盾
        public const int GrimmId = 39;      // 格林之子
        public const int KingsoulId = 41;   // 国王之魂
        public const int MelodyId = 43;     // 无忧旋律（格林之子变体，T 键互换）

        /// <summary>普攻基础总时长/间隔（秒，与 KnightEntity 的挥砍计时器一致）。
        /// 0.4s = 挥砍剪辑 8 帧 @20fps（HK 原版骨钉挥砍的标准时长，含后摇）。</summary>
        public const float SlashTimeBase = 0.4f;
        /// <summary>快速劈砍（护符17）：普攻总时长/间隔（秒）。</summary>
        public const float SlashTimeQuick = 0.3f;
        private const string GreedBaseKey = "kic_greed_base"; // 背包基础容量快照
        /// <summary>上次扩容时生效的"层数"（0/1/2）：用来从当前 row_max 正确反推基础容量。
        /// 存层数而不是存加成值，是因为 COOK SF 是 0~255 的字节，两层加成 300 存不下。</summary>
        private const string GreedStackKey = "kic_greed_stack";
        /// <summary>坚固贪婪：佩戴时背包上限加成（当前版本 +150）。</summary>
        private const int GreedSlotBonus = 150;
        /// <summary>旧版加成（+100 时期）——用于识别并迁移旧存档。</summary>
        private const int OldGreedSlotBonus = 100;
        // 束缚（自限）按钮状态键：值为 2 表示该束缚生效
        public const string GgNailKey = "kic_gg_nail";
        public const string GgMaskKey = "kic_gg_mask";
        public const string GgCharmKey = "kic_gg_charm";
        public const string GgSoulKey = "kic_gg_soul";

        /// <summary>本次快速旅行是否传送到战斗区域（区分长椅目的地，避免提前完成跟随）。</summary>
        public static bool BattleAreaFastTravel;

        /// <summary>本次快速旅行目的地是否为战斗区域：跳过收尾 AUTO_SAVE_BENCH 的“附近有长椅”检查。</summary>
        public static bool AutoSaveBenchSuppress;

        /// <summary>当前所在房间是否为蜂巢房间（房间 key 含 honey）。</summary>
        public static bool CurrentRoomIsHive { get; private set; }

        /// <summary>坚固贪婪：背包加成是否已生效（防重复叠加）。</summary>
        private static bool _greedCapacityApplied;
        // 坚固贪婪“击杀掉落宝箱复制”防递归标记
        private static bool _greedReelDupGuard;
        private static readonly HashSet<NelEnemy> _greedGoldGranted = new HashSet<NelEnemy>();

        /// <summary>
        /// 坚固贪婪的佩戴层数：小骑士与诺艾尔**各自算一层**（需求：两边同时佩戴时效果叠加）。
        /// 所有坚固贪婪的效果都按这个层数放大（背包上限 +150/层、金币 5%/层、掉率与品级加成叠层，
        /// 伤害惩罚 -0.75%/层每 4 格）。
        /// </summary>
        public static int GreedStacks()
        {
            int n = 0;
            if (IsEquipped(GreedId))
            {
                n++; // 小骑士那一套
            }
            if (IsEquipped(CharmOwner.Noel, GreedId))
            {
                n++; // 诺艾尔那一套
            }
            return n;
        }
        private static readonly FieldInfo EnemyMaxHpField =
            AccessTools.Field(typeof(M2Attackable), "maxhp");
        private static readonly FieldInfo EnemyDropItemField =
            AccessTools.Field(typeof(NelEnemy), "DropItem");
        private static readonly FieldInfo EnemyOdField =
            AccessTools.Field(typeof(NelEnemy), "Od");
        private static readonly FieldInfo OdDropItemOdField =
            AccessTools.Field(typeof(OverDriveManager), "DropItemOd");
        private static readonly MethodInfo ExecuteDropItemMethod =
            AccessTools.Method(typeof(NelEnemy), "ExecuteDropItem",
                new[] { typeof(NelItem).MakeByRefType() });
        // ---- 沉重之击（斩杀 / 上限百分比追加伤害）----
        private static readonly FieldInfo EnemyHpField =
            AccessTools.Field(typeof(M2Attackable), "hp");

        /// <summary>蜂巢房间内是否已被小骑士挑衅（全房魔物进入攻击状态）。</summary>
        private static bool _hiveAggroTriggered;

        /// <summary>护符是否已装备（含固定虚空之心）。</summary>
        public static bool IsEquipped(int id)
        {
            if (id == CharmDatabase.FixedCharmId)
            {
                return true; // 虚空之心恒为装备
            }
            // 束缚·护符：除束缚本身外，其他护符直接无效（仍佩戴，但效果不生效）；
            // 指南针、坚固贪婪不受束缚影响
            if (GgCharmBound && id != CharmDatabase.GgSelectorId &&
                id != CompassId && id != GreedId)
            {
                return false;
            }
            // 控制器未创建（从未打开护符 UI）时，读独立装备快照，
            // 保证读档后护符效果立即生效，无需先打开一次护符界面。
            // 注意："诺艾尔的护符"加入后控制器可能正停留在诺艾尔那一侧，
            // 它的 EquippedIds 就不再是小骑士的装备了 —— 这里一律按小骑士判断。
            CharmUiController ctr = CharmUiController.Instance;
            if (ctr != null && ctr.Owner == CharmOwner.Knight)
            {
                return ctr.EquippedIds.Contains(id);
            }
            return CharmSave.HasEquipped(CharmOwner.Knight, id);
        }

        /// <summary>指定归属是否装备了某护符（第二部分：诺艾尔侧的护符效果用）。
        /// 小骑士侧沿用上面的规则（虚空之心恒为装备、束缚会让其它护符失效）；
        /// 诺艾尔侧没有固定虚空之心、也没有束缚自限，直接读她自己那份装备列表。</summary>
        public static bool IsEquipped(CharmOwner owner, int id)
        {
            if (id <= 0)
            {
                return false;
            }
            if (owner == CharmOwner.Noel)
            {
                return CharmSave.HasEquipped(CharmOwner.Noel, id);
            }
            return IsEquipped(id);
        }

        /// <summary>当前操控角色是否装备了某护符：骑士模式看小骑士那一套，诺艾尔模式看诺艾尔那一套。
        /// 第二部分里"两个角色都能用"的护符效果统一走这个入口。</summary>
        public static bool IsEquippedForCurrentPlayer(int id)
        {
            return IsEquipped(IsKnightMode ? CharmOwner.Knight : CharmOwner.Noel, id);
        }

        /// <summary>束缚·骨钉：无加成骨钉伤害降低为 30。</summary>
        public static bool GgNailBound => COOK.getSF(GgNailKey) == 2;
        /// <summary>束缚·外壳：无加成血量上限降低为 4。</summary>
        public static bool GgMaskBound => COOK.getSF(GgMaskKey) == 2;
        /// <summary>束缚·护符：其他护符直接无效。</summary>
        public static bool GgCharmBound => COOK.getSF(GgCharmKey) == 2;
        /// <summary>束缚·灵魂：灵魂上限降低为 30。</summary>
        public static bool GgSoulBound => COOK.getSF(GgSoulKey) == 2;

        /// <summary>
        /// 每次读档后调用：四个束缚全部重置为未束缚（1），
        /// 避免其他存档/上次会话的束缚状态影响本次读档后的游戏。
        /// </summary>
        public static void ResetGgRestrictionsOnLoad()
        {
            COOK.setSF(GgNailKey, 1);
            COOK.setSF(GgMaskKey, 1);
            COOK.setSF(GgCharmKey, 1);
            COOK.setSF(GgSoulKey, 1);
        }

        /// <summary>当前是否小骑士模式。</summary>
        public static bool IsKnightMode => KnightInCradlePlugin.KnightModeActive;

        /// <summary>蜂群集结/蜂巢之血：蜂巢房间内魔物处于中立状态（不主动攻击）。</summary>
        public static bool HiveNeutralActive()
        {
            return (IsEquippedForCurrentPlayer(CollectorId) || IsEquippedForCurrentPlayer(HiveId)) &&
                CurrentRoomIsHive && !_hiveAggroTriggered;
        }

        /// <summary>蜂群集结：魔力草掉落的魔力只能由诺艾尔吸收，魔物无法吸收。</summary>
        public static bool CollectorManaGuardActive()
        {
            // 注意：**这一项只服务小骑士侧**（沿用模组原有行为）。
            // 诺艾尔侧的蜂群集结只保留"自动拾取 + 蜂巢怪不打"两项（2026-09-22 用户定），
            // 魔力草在诺艾尔手里完全走原版：正常掉落、谁都能吸。
            return IsKnightMode && IsEquipped(CollectorId);
        }

        // ================= 护符3 坚硬外壳（**诺艾尔专属**：伪次数血） =================
        // 与小骑士的"延长无敌时间"完全不同，诺艾尔侧的效果是：
        //   ① 佩戴后血量上限直接除以 35 向下取整（伪次数血）；
        //   ② 受到的伤害 ≤ 20 记 0、> 20 一律记 1；
        //   ③ 无论上面记成 0 还是 1，受击后都立刻获得 2 秒无敌（走 AIC 原生 NoDamage）；
        //   ④ 卸下立即回到"佩戴前的血量"（佩戴时的 hp/maxhp 原样寄存）。

        /// <summary>血量上限折算除数：floor(最大生命 / 50)。（2026-09-22 六稿：35 → 50）</summary>
        public const int SturdyHpPerHit = 50;
        /// <summary>伤害阈值：≤ 20 记 0，&gt; 20 记 1。</summary>
        public const int SturdyDamageThreshold = 20;
        /// <summary>受击后的无敌时长（帧，60fps 基准）：2 秒 = 120 帧。</summary>
        public const float SturdyInvincibleFrames = 120f;
        /// <summary>佩戴时寄存的"真实上限 / 真实血量"（COOK SF，随存档序列化）：
        /// 伪次数血把 hp/maxhp 字段改小了，读档与"卸下还原"都只能靠它们。</summary>
        private const string SturdyRealMaxHpKey = "kic_noel_sturdy_maxhp";
        private const string SturdyRealHpKey = "kic_noel_sturdy_hp";

        private static readonly FieldInfo PrHpField = AccessTools.Field(typeof(M2Attackable), "hp");
        private static readonly FieldInfo PrMaxHpField = AccessTools.Field(typeof(M2Attackable), "maxhp");
        /// <summary>M2Attackable.NoDamage 是 protected 字段，用反射取。</summary>
        private static readonly FieldInfo PrNoDamageField = AccessTools.Field(typeof(M2Attackable), "NoDamage");

        private static bool _noelSturdyActive;
        private static int _noelSturdyRealMaxHp = -1;
        private static int _noelSturdyRealHp = -1;

        /// <summary>伪次数血是否生效中。</summary>
        public static bool NoelSturdyActive => _noelSturdyActive;

        /// <summary>读档/换存档后重置会话状态（SF 里寄存的真实值保留，下一次每帧 tick 会据此重新激活）。</summary>
        public static void ResetNoelSturdyOnLoad()
        {
            _noelSturdyActive = false;
            _noelSturdyRealMaxHp = -1;
            _noelSturdyRealHp = -1;
        }

        /// <summary>伪次数血上限：floor(真实最大生命 / 35)，至少 1。</summary>
        public static int SturdyHitMax(int realMaxHp)
        {
            return Mathf.Max(1, realMaxHp / SturdyHpPerHit);
        }

        /// <summary>
        /// 每帧维护（诺艾尔模式调用）：
        /// - 佩戴状态变化时立即换算：佩戴 → maxhp 变 floor(maxhp/35)、hp 按比例折算成次数；
        ///   卸下 → 把佩戴时寄存的真实 hp/maxhp 原样写回；
        /// - 佩戴期间保证 maxhp == 次数上限、hp ∈ [0, maxhp]（被别处改写也拉回来）。
        /// </summary>
        public static void TickNoelSturdyCharm(PRNoel pr)
        {
            try
            {
                if (pr == null || PrHpField == null || PrMaxHpField == null)
                {
                    return;
                }
                bool want = IsEquipped(CharmOwner.Noel, SturdyId);
                if (want && !_noelSturdyActive)
                {
                    ActivateNoelSturdy(pr);
                }
                else if (!want && _noelSturdyActive)
                {
                    DeactivateNoelSturdy(pr);
                }
                else if (want)
                {
                    int hitMax = SturdyHitMax(_noelSturdyRealMaxHp);
                    int maxHp = (int)PrMaxHpField.GetValue(pr);
                    int hp = (int)PrHpField.GetValue(pr);
                    bool changed = false;
                    if (maxHp != hitMax)
                    {
                        PrMaxHpField.SetValue(pr, hitMax);
                        changed = true;
                    }
                    if (hp > hitMax)
                    {
                        PrHpField.SetValue(pr, hitMax);
                        changed = true;
                    }
                    else if (hp < 0)
                    {
                        PrHpField.SetValue(pr, 0);
                        changed = true;
                    }
                    if (changed)
                    {
                        RefreshNoelHudHp();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 改完 hp/maxhp 字段后必须主动让 HUD 重算比例：
        /// AIC 只在受伤/治疗流程里调 `UIStatus.fineHpRatio`，我们直接写字段它不会自己刷新，
        /// 表现就是"血条要等下一次全量刷新（例如切一次角色）才变"。
        /// </summary>
        private static void RefreshNoelHudHp()
        {
            try
            {
                UIStatus st = UIStatus.Instance;
                if (st == null)
                {
                    return;
                }
                st.fineHpRatio(false, false); // 同步 hp_ratio（比例真的变了时它自己会置脏）
                // 关键：伪次数血经常是"满血 → 满血"，比例没变，但 HUD 上的 **hp/maxhp 数字**变了。
                // 原版只在受伤/治疗流程里刷，且那条路走的是 cushion 分支、不会置 redraw_bar_num，
                // 所以必须手动把这两个重绘标记置脏（UIStatus.redraw_hp / redraw_bar_num 都是 public）。
                st.redraw_hp = true;
                st.redraw_bar_num = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>同理：改完 mp 字段后要让魔力条与"mp/maxmp"数字重绘。</summary>
        private static void RefreshNoelHudMp()
        {
            try
            {
                UIStatus st = UIStatus.Instance;
                if (st == null)
                {
                    return;
                }
                st.fineMpRatio(false, false);
                st.redraw_mp = true;
                st.redraw_bar_num = true;
            }
            catch (Exception)
            {
            }
        }

        // ========== 护符4 灵魂捕手 / 护符6 噬魂者（诺艾尔侧：法术命中敌人回 MP） ==========
        /// <summary>灵魂捕手：法术命中敌人时立即回复的 MP。</summary>
        public const float SoulCatcherMp = 6f;
        /// <summary>噬魂者：法术命中敌人时立即回复的 MP。</summary>
        public const float SoulEaterMp = 15f;

        /// <summary>
        /// 护符4 灵魂捕手 / 护符6 噬魂者（**诺艾尔侧**）：诺艾尔用法术命中敌人时立刻回 MP
        /// （灵魂捕手 +6、噬魂者 +15，**两者都装备时叠加**——与小骑士侧 `10 + 3 + 8` 的口径一致）。
        ///
        /// **挂载点**：`MGContainer.CircleCast` 的 postfix —— 它是 AIC 里法术命中的汇聚点
        /// （`MGContainer.cs:473`，内部对每个命中目标调 `nelM2Attacker.applyDamage(Atk, ref hittype, false)`）。
        /// 不能挂敌人受伤入口：法术走的是 **3 参重载**，而 26 个敌人子类各自 override 了它，
        /// 挂在基类上不会被虚分派调用（这正是"命中了却没有回魔"的原因）。
        ///
        /// **"法术"的判据**用游戏自己的魔力消耗表：`MKind.getReduceMp(kind) > 0`
        /// —— 消耗魔力的一律算魔法（纯白之箭/魔法霰弹/地面炸弹等），诺艾尔不耗魔的近战不算。
        ///
        /// 只在**诺艾尔模式**结算（骑士模式里小骑士的攻击 Caster 也是诺艾尔，需隔离）；
        /// 一次施法命中敌人结算一次（按"施法命中"计，不按目标数）。
        /// </summary>
        private static void SoulCharmCircleCastPostfix(MagicItem Mg, ref HITTYPE __result)
        {
            try
            {
                if (IsKnightMode)
                {
                    return;
                }
                float gain = 0f;
                if (IsEquipped(CharmOwner.Noel, SoulCatcherId))
                {
                    gain += SoulCatcherMp;
                }
                if (IsEquipped(CharmOwner.Noel, SoulEaterId))
                {
                    gain += SoulEaterMp;
                }
                if (gain <= 0f)
                {
                    return; // 两个护符都没装备
                }
                if (Mg == null || !(Mg.Caster is PRNoel))
                {
                    return; // 不是诺艾尔放的法术
                }
                if (!IsPlayerMagicKind(Mg.kind))
                {
                    return; // 不消耗魔力的攻击（普攻/技艺）不算魔法
                }
                if ((__result & HITTYPE.HITTED_EN) == HITTYPE.NONE)
                {
                    return; // 这一发没打中敌人
                }
                if (KnightInCradleBehaviour.GrantNoelMana(gain))
                {
                    RefreshNoelHudMp();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 诺艾尔的"魔法"判据（灵魂捕手 / 萨满之石共用）：
        /// 消耗魔力的一律算（`MKind.getReduceMp` &gt; 0）；
        /// 例外是**魔法霰弹（PR_SHOTGUN）**——它吃的是蓄力魔力 `mp_hold`、没有 per-kind 的 reduce_mp
        /// （命中处理见 `M2PrSkill.cs:2149-2155`），单独放行。
        /// </summary>
        private static bool IsPlayerMagicKind(MGKIND kind)
        {
            try
            {
                return MKind.getReduceMp(kind) > 0 || kind == MGKIND.PR_SHOTGUN;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ================= 护符11 坚固心脏（诺艾尔侧：生命上限 +120） =================
        /// <summary>佩戴后提升的生命上限。</summary>
        public const int HeartMaxHpBonus = 120;
        /// <summary>基础上限寄存键（COOK SF，随存档序列化）：用来区分"存档里已经带上 +120 了"。</summary>
        private const string HeartBaseMaxHpKey = "kic_noel_heart_base";

        private static bool _noelHeartActive;
        private static int _noelHeartBaseMaxHp = -1;

        /// <summary>读档/换存档后重置会话状态（SF 里的基础上限保留，下一次 tick 会据此重新激活）。</summary>
        public static void ResetNoelHeartOnLoad()
        {
            _noelHeartActive = false;
            _noelHeartBaseMaxHp = -1;
        }

        /// <summary>
        /// 每帧维护（诺艾尔模式）：佩戴坚固心脏时生命上限 = 基础上限 + 120。
        /// 佩戴瞬间把新增的 120 直接补成当前血量（同 HK 佩戴该护符的观感）；
        /// 卸下时把上限还原并把当前血量钳回去。
        /// 读档后靠 SF 里的"基础上限"判断存档是否已经带上加成，避免重复 +120。
        /// </summary>
        public static void TickNoelHeartCharm(PRNoel pr)
        {
            try
            {
                if (pr == null || PrMaxHpField == null || PrHpField == null)
                {
                    return;
                }
                bool want = !IsKnightMode && IsEquipped(CharmOwner.Noel, HeartId);
                if (want && !_noelHeartActive)
                {
                    ActivateNoelHeart(pr);
                }
                else if (!want && _noelHeartActive)
                {
                    DeactivateNoelHeart(pr);
                }
                else if (want)
                {
                    int target = _noelHeartBaseMaxHp + HeartMaxHpBonus;
                    if ((int)PrMaxHpField.GetValue(pr) != target)
                    {
                        PrMaxHpField.SetValue(pr, target);
                        RefreshNoelHudHp();
                    }
                    if ((int)PrHpField.GetValue(pr) > target)
                    {
                        PrHpField.SetValue(pr, target);
                        RefreshNoelHudHp();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ActivateNoelHeart(PRNoel pr)
        {
            int saved = COOK.getSF(HeartBaseMaxHpKey);
            int baseMax;
            if (saved > 0)
            {
                baseMax = saved; // 读档回到"已佩戴"：基础上限记在 SF 里
            }
            else
            {
                baseMax = (int)PrMaxHpField.GetValue(pr);
                if (baseMax <= 0)
                {
                    return;
                }
                COOK.setSF(HeartBaseMaxHpKey, Mathf.Clamp(baseMax, 0, 255));
                int hp = (int)PrHpField.GetValue(pr);
                PrHpField.SetValue(pr, hp + HeartMaxHpBonus); // 新增的上限直接补满
            }
            _noelHeartBaseMaxHp = baseMax;
            PrMaxHpField.SetValue(pr, baseMax + HeartMaxHpBonus);
            _noelHeartActive = true;
            RefreshNoelHudHp();
        }

        private static void DeactivateNoelHeart(PRNoel pr)
        {
            int baseMax = _noelHeartBaseMaxHp > 0
                ? _noelHeartBaseMaxHp
                : Mathf.Max(1, (int)PrMaxHpField.GetValue(pr) - HeartMaxHpBonus);
            PrMaxHpField.SetValue(pr, baseMax);
            int hp = (int)PrHpField.GetValue(pr);
            if (hp > baseMax)
            {
                PrHpField.SetValue(pr, baseMax);
            }
            COOK.setSF(HeartBaseMaxHpKey, 0);
            _noelHeartActive = false;
            _noelHeartBaseMaxHp = -1;
            RefreshNoelHudHp();
        }

        // ================= 护符10 蜕变挽歌（诺艾尔侧） =================
        /// <summary>剑气飞行速度（格/秒）——与小骑士的挽歌剑气一致。</summary>
        public const float ElegySpeed = 30f;
        /// <summary>剑气射程（格）。（2026-09-22 二稿：4 → 8）</summary>
        public const float ElegyRange = 8f;
        /// <summary>剑气判定箱（世界单位）——与小骑士一致。</summary>
        public const float ElegyHitboxW = 2.0f;
        public const float ElegyHitboxH = 1.4f;
        /// <summary>剑气伤害：**真实伤害**（`fix_damage`，不按非满血减伤打折）。（2026-09-22 四稿：30 → 18）</summary>
        public const int ElegyDamage = 18;
        /// <summary>剑气贴图（与小骑士同款）。</summary>
        public const string ElegySprite = "slashes_effect0001";
        /// <summary>剑气渲染尺寸系数（取小骑士 `SlashFxScale` 的同一数值，保证"大小一致"）。</summary>
        public const float ElegyFxScale = 1.5f;

        private sealed class NoelElegyBlade
        {
            public float X;
            public float Y;
            public float Dir;
            public float Traveled;
            public readonly HashSet<NelEnemy> Hits = new HashSet<NelEnemy>();
        }

        private static readonly List<NoelElegyBlade> _noelElegyBlades = new List<NoelElegyBlade>();
        private static MeshDrawer _noelElegyMesh;
        private static Material _noelElegyMat;
        private static M2RenderTicket _noelElegyTicket;
        private static Map2d _noelElegyMap;
        private static Texture2D _noelElegyTex;
        private static int _noelElegyMask = -1;

        /// <summary>
        /// 护符10 蜕变挽歌（诺艾尔侧）：诺艾尔**轻攻击**时向前发射一道剑气。
        /// 轻攻击 = `M2PrSkill.executeSmallAttack` 产出的 `MGKIND.PR_PUNCH`
        /// （`nel/M2PrSkill.cs:2705`：`mgkind = (CurMg == null) ? PR_PUNCH : PR_SHOTGUN`），
        /// 因此魔法霰弹（PR_SHOTGUN）与各种技艺不会误触发。
        /// **不需要满血**（与小骑士的挽歌不同，这里没有血量条件）。
        /// </summary>
        private static void ElegyExecuteSmallAttackPostfix(MagicItem __result)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, ElegyId))
                {
                    return;
                }
                if (__result == null || __result.kind != MGKIND.PR_PUNCH)
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !pr.is_alive)
                {
                    return;
                }
                float dir = pr.mpf_is_right;
                _noelElegyBlades.Add(new NoelElegyBlade
                {
                    X = pr.x + dir * 0.5f,   // 从中心略前方发射
                    Y = pr.y - 0.5f,         // 判定/渲染整体上移 0.5 格（与小骑士一致）
                    Dir = dir,
                    Traveled = 0f,
                });
                try
                {
                    DashAudio.PlayElegyBlade();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进（诺艾尔模式调用）：直线飞行、命中判定、超射程移除、票据维护。</summary>
        public static void TickNoelElegyCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                bool want = !IsKnightMode && IsEquipped(CharmOwner.Noel, ElegyId);
                if (_noelElegyBlades.Count > 0)
                {
                    float dt = Time.deltaTime;
                    for (int i = _noelElegyBlades.Count - 1; i >= 0; i--)
                    {
                        NoelElegyBlade b = _noelElegyBlades[i];
                        float step = ElegySpeed * dt;
                        b.X += b.Dir * step;
                        b.Traveled += step;
                        CheckNoelElegyHit(pr, b);
                        if (b.Traveled >= ElegyRange)
                        {
                            _noelElegyBlades.RemoveAt(i);
                        }
                    }
                }
                EnsureNoelElegyTicket(pr, want);
            }
            catch (Exception)
            {
            }
        }

        private static int NoelElegyOverlapMask()
        {
            if (_noelElegyMask >= 0)
            {
                return _noelElegyMask;
            }
            int mask = LayerMask.GetMask("EnemySelf", "Enemy", "AttackHitable");
            foreach (string name in new[] { "Ignore Raycast", "Water", "TransparentFX", "Default" })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                {
                    mask |= 1 << layer;
                }
            }
            if (mask != 0)
            {
                _noelElegyMask = mask;
            }
            return mask;
        }

        private static void CheckNoelElegyHit(PRNoel pr, NoelElegyBlade b)
        {
            Map2d mp = pr.Mp;
            if (mp == null)
            {
                return;
            }
            int mask = NoelElegyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float hx = b.X + (b.Dir < 0f ? -1.2f : 0.2f); // 与小骑士的剑气判定偏移一致
            float hy = b.Y + 0.5f;
            float ux = mp.pixel2ux(hx * mp.CLEN);
            float uy = mp.pixel2uy(hy * mp.CLEN);
            Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(ux, uy));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center,
                new Vector2(ElegyHitboxW, ElegyHitboxH), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                if (enemy == null || !b.Hits.Add(enemy))
                {
                    continue; // 每只魔物只会被这一道剑气打中一次
                }
                ApplyNoelElegyDamage(pr, enemy);
            }
        }

        /// <summary>剑气伤害：固定 20 点真实伤害（`fix_damage`），走 AIC 完整受击管线。</summary>
        private static void ApplyNoelElegyDamage(PRNoel pr, NelEnemy enemy)
        {
            try
            {
                // 护符16 沉重之击：剑气也是"诺艾尔造成的伤害"，"会心"期间同样 +40%。
                int dmg = ElegyDamage;
                if (IsHeavyFocusActive)
                {
                    dmg = Mathf.FloorToInt(dmg * HeavyBlowFocusMult + 0.5f);
                }
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                atk.Caster = pr;
                // 护符16 沉重之击：剑气打中敌人也算"这一发攻击命中了"
                ResolveHeavyFocusHit();
                enemy.applyDamage(atk, false);
                try
                {
                    DashAudio.PlayEnemyHit();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>剑气票据：绑当前地图的 MovRenderer；换图/失效时重建，剑气打完且未佩戴时释放。</summary>
        private static void EnsureNoelElegyTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            bool need = want || _noelElegyBlades.Count > 0;
            if (!need)
            {
                ReleaseNoelElegyTicket();
                return;
            }
            if (_noelElegyTex == null)
            {
                _noelElegyTex = LoadNoelElegyTexture();
            }
            if (_noelElegyTex == null)
            {
                return; // 素材缺失：判定照常，只是不显示
            }
            if (_noelElegyMesh != null && _noelElegyMap == mp && _noelElegyTicket != null)
            {
                return;
            }
            ReleaseNoelElegyTicket();
            _noelElegyMap = mp;
            _noelElegyMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _noelElegyMesh.draw_gl_only = true;
            _noelElegyMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelElegyMat.EnableKeyword("NO_PIXELSNAP");
            _noelElegyMesh.activate("noel_elegy_blade", _noelElegyMat, false, MTRX.ColWhite, null);
            _noelElegyTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, PrepareNoelElegyMesh, _noelElegyMesh, null, null);
        }

        private static void ReleaseNoelElegyTicket()
        {
            try
            {
                if (_noelElegyTicket != null && _noelElegyMap != null && _noelElegyMap.MovRenderer != null)
                {
                    _noelElegyMap.MovRenderer.deassignDrawable(_noelElegyTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelElegyMat != null)
                {
                    IN.DestroyOne(_noelElegyMat);
                }
            }
            catch (Exception)
            {
            }
            _noelElegyTicket = null;
            _noelElegyMesh = null;
            _noelElegyMat = null;
            _noelElegyMap = null;
        }

        /// <summary>剑气绘制：矩阵锚定诺艾尔中心，每道剑气按相对偏移画一个四边形（同小骑士的做法）。</summary>
        private static bool PrepareNoelElegyMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelElegyMap;
            if (mp == null || _noelElegyMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelElegyMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelElegyTex == null || _noelElegyBlades.Count == 0)
            {
                MdOut = _noelElegyMesh;
                return true;
            }
            float scale = KnightInCradlePlugin.ScaleConfig != null ? KnightInCradlePlugin.ScaleConfig.Value : 0.325f;
            float w = _noelElegyTex.width * scale * ElegyFxScale;
            float h = _noelElegyTex.height * scale * ElegyFxScale;
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            for (int i = 0; i < _noelElegyBlades.Count; i++)
            {
                NoelElegyBlade b = _noelElegyBlades[i];
                float dx = (b.X + 1f - pr.x) * mp.CLEN;
                float dy = -(b.Y - 0.5f - pr.y) * mp.CLEN;
                _noelElegyMesh.Col = MTRX.ColWhite;
                _noelElegyMesh.initForImgAndTexture(_noelElegyTex);
                _noelElegyMesh.uv_top = 0f;
                _noelElegyMesh.uv_height = 1f;
                if (b.Dir > 0f)
                {
                    _noelElegyMesh.uv_left = 1f;
                    _noelElegyMesh.uv_width = -1f;
                }
                else
                {
                    _noelElegyMesh.uv_left = 0f;
                    _noelElegyMesh.uv_width = 1f;
                }
                _noelElegyMesh.Rect(dx - w * 0.5f, dy - h * 0.5f, w, h, false);
            }
            MdOut = _noelElegyMesh;
            return true;
        }

        private static Texture2D LoadNoelElegyTexture()
        {
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sprites", ElegySprite + ".png");
                if (!System.IO.File.Exists(path))
                {
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ================= 护符9 幼虫之歌（诺艾尔侧） =================
        /// <summary>受到伤害时回复的 MP。</summary>
        public const float GrubsongDamageMp = 20f;

        private static int _grubsongLastFrame = -100;

        /// <summary>
        /// 护符9 幼虫之歌（诺艾尔侧）：诺艾尔受到伤害时立刻回 20 MP。
        /// 由伤害前缀 `SturdyHpDamagePrefix`（挂在 `M2Attackable.applyHpDamage`）在**原始伤害 &gt; 0** 时调用，
        /// 因此即使同一次伤害被坚硬外壳改写成 0/1，回魔依然按"确实挨了一下"结算；同一帧只结算一次。
        /// </summary>
        private static void TryGrantGrubsongMp()
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, GrubsongId))
                {
                    return;
                }
                if (_grubsongLastFrame == Time.frameCount)
                {
                    return; // 同一帧的多次伤害入口只结算一次
                }
                _grubsongLastFrame = Time.frameCount;
                if (KnightInCradleBehaviour.GrantNoelMana(GrubsongDamageMp))
                {
                    RefreshNoelHudMp();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符9 幼虫之歌 / 护符10 蜕变挽歌（诺艾尔侧）：诺艾尔不会被虫墙/虫巢抓取
        /// （与小骑士侧一致：这两个护符都会免虫墙抓取）。
        /// `M2WormTrap` 决定是否拉扯玩家时读 `PR.canPullByWorm()`（`nel/M2WormTrap.cs:111,148`），
        /// 这里对本地诺艾尔直接返回 false。
        /// 骑士模式下另有 CombatGuard 的同名补丁（它会先返回 false 拦掉），两者按各自模式生效、互不冲突。
        /// </summary>
        private static bool GrubsongCanPullByWormPrefix(PR __instance, ref bool __result)
        {
            try
            {
                if (IsKnightMode || !(__instance is PRNoel) ||
                    !(IsEquipped(CharmOwner.Noel, GrubsongId) || IsEquipped(CharmOwner.Noel, ElegyId)))
                {
                    return true;
                }
                __result = false;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ========== 护符7 冲刺大师 / 护符8 飞毛腿（诺艾尔侧：都改 walkSpeed/runSpeed） ==========
        /// <summary>冲刺大师：佩戴后跑步速度倍率（需求：降低 10%）。</summary>
        public const float DashmasterRunSpeedMult = 0.9f;
        /// <summary>飞毛腿：佩戴后走路与跑步速度倍率（+10% 手感不明显、+50% 过强，最终取 +20%）。</summary>
        public const float SprintmasterSpeedMult = 1.2f;

        /// <summary>
        /// 冲刺大师：松开方向键**立刻停住**（去掉跑动的"急停滑行"）。
        ///
        /// 滑行来自 `M2MoverPr.calcWalkSpeed` 的 `move_aim_ex == 0` 分支：
        /// `isRunning() && run_continue_time_ >= 0` → `X.VALWALK(num, 0f, accel_run_break)`
        /// （`unsafeAssem/m2d/M2MoverPr.cs:1467-1470`），结果随后写回 `Phy.walk_xspeed`（`:601-602`）。
        /// 因此只要在该函数返回后把结果清零，松手那一帧水平速度就归零、不再滑行。
        /// </summary>
        private static void DashmasterCalcWalkSpeedPostfix(M2MoverPr __instance, int move_aim_ex, ref float __result)
        {
            try
            {
                if (!_noelDashmasterActive || IsKnightMode || !(__instance is PRNoel))
                {
                    return;
                }
                if (move_aim_ex == 0)
                {
                    __result = 0f;
                }
            }
            catch (Exception)
            {
            }
        }

        private static readonly FieldInfo MoverWalkSpeedField = AccessTools.Field(typeof(M2MoverPr), "walkSpeed");
        private static readonly FieldInfo MoverRunSpeedField = AccessTools.Field(typeof(M2MoverPr), "runSpeed");

        /// <summary>冲刺大师是否生效（供 isRunning / calcWalkSpeed 两个补丁判断）。</summary>
        private static bool _noelDashmasterActive;
        private static bool _noelSpeedCharmsActive;
        private static float _noelBaseWalkSpeed = -1f;
        private static float _noelBaseRunSpeed = -1f;

        /// <summary>
        /// 每帧维护（诺艾尔模式调用）：冲刺大师（跑速 ×0.9）与飞毛腿（走/跑 ×1.1）**统一计算**。
        ///
        /// 两个护符改的是同一对字段（`M2MoverPr.walkSpeed` / `runSpeed`，protected，用反射），
        /// 各改各的会互相把对方的结果当成"原值"，所以这里只保留一份基础值：
        /// 首次佩戴任一护符时寄存 `walkSpeed/runSpeed` 原值，之后
        /// `目标走速 = 原值 × 飞毛腿倍率`、`目标跑速 = 原值 × 飞毛腿倍率 × 冲刺大师倍率`；
        /// 两个都卸下（或切到骑士模式）时把原值写回。
        /// </summary>
        public static void TickNoelMoveSpeedCharms(PRNoel pr)
        {
            try
            {
                if (pr == null || MoverWalkSpeedField == null || MoverRunSpeedField == null)
                {
                    return;
                }
                bool dash = !IsKnightMode && IsEquipped(CharmOwner.Noel, DashmasterId);
                bool sprint = !IsKnightMode && IsEquipped(CharmOwner.Noel, RunnerId);
                _noelDashmasterActive = dash;
                if (!dash && !sprint)
                {
                    if (_noelSpeedCharmsActive)
                    {
                        if (_noelBaseWalkSpeed > 0f)
                        {
                            MoverWalkSpeedField.SetValue(pr, _noelBaseWalkSpeed);
                        }
                        if (_noelBaseRunSpeed > 0f)
                        {
                            MoverRunSpeedField.SetValue(pr, _noelBaseRunSpeed);
                        }
                        _noelSpeedCharmsActive = false;
                        _noelBaseWalkSpeed = -1f;
                        _noelBaseRunSpeed = -1f;
                    }
                    return;
                }
                if (!_noelSpeedCharmsActive)
                {
                    _noelSpeedCharmsActive = true;
                    _noelBaseWalkSpeed = (float)MoverWalkSpeedField.GetValue(pr);
                    _noelBaseRunSpeed = (float)MoverRunSpeedField.GetValue(pr);
                }
                float walkTarget = _noelBaseWalkSpeed * (sprint ? SprintmasterSpeedMult : 1f);
                float runTarget = _noelBaseRunSpeed * (sprint ? SprintmasterSpeedMult : 1f) *
                    (dash ? DashmasterRunSpeedMult : 1f);
                if (Mathf.Abs((float)MoverWalkSpeedField.GetValue(pr) - walkTarget) > 0.0001f)
                {
                    MoverWalkSpeedField.SetValue(pr, walkTarget);
                }
                if (Mathf.Abs((float)MoverRunSpeedField.GetValue(pr) - runTarget) > 0.0001f)
                {
                    MoverRunSpeedField.SetValue(pr, runTarget);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 冲刺大师：佩戴期间诺艾尔"始终按跑步移动"，即把 `M2MoverPr.isRunning()` 强制为 true。
        /// AIC 里跑/走的两处判据都读它：
        /// ① 速度：`calcWalkSpeed` → `isRunning() ? runSpeed : walkSpeed`（`M2MoverPr.cs:1477`）；
        /// ② 姿势：`AnimationShufflerNoel` → `isRunning() ? "run" : "walk"`（`AnimationShufflerNoel.cs:677`）。
        /// 因此这一条同时满足"走路动画换成跑步动画"与"移动一律按跑步结算"。
        /// 只对本地诺艾尔生效，敌人不受影响。
        /// </summary>
        private static void DashmasterIsRunningPostfix(M2MoverPr __instance, ref bool __result)
        {
            try
            {
                if (!_noelDashmasterActive || IsKnightMode || __result || !(__instance is PRNoel pr))
                {
                    return;
                }
                // 只在**确实在移动**时强制跑步：AIC 的姿势选择里 `isRunning()` 为真会**无条件**摆跑步姿势
                // （`AnimationShufflerNoel.cs:669-671`：`else if (isRunning()) dep_pose = "run";`），
                // 静止时也跟着变跑步就错了。判据沿用游戏自己的"在移动"（`:677`）：
                // 按了左右键，或脚本移动且物理水平速度非 0。
                bool moving = (int)(pr.getMoveKey(true) & M2MoverPr.MOVEK._LR) > 0;
                if (!moving)
                {
                    M2Phys ph = pr.getPhysic();
                    if (ph != null && Mathf.Abs(ph.walk_xspeed) > 0.001f)
                    {
                        moving = true;
                    }
                }
                if (moving)
                {
                    __result = true;
                }
            }
            catch (Exception)
            {
            }
        }

        // ================= 护符5 萨满之石（诺艾尔侧：法术最终伤害 +25%） =================
        /// <summary>法术伤害倍率（+25%）。</summary>
        public const float ShamanDamageMult = 1.25f;

        // ================= 护符14 法术扭曲者（诺艾尔侧：起手耗魔 -10） =================
        /// <summary>起手消耗的减免量（需求：-10）。</summary>
        public const int SpellTwisterMpReduce = 10;

        /// <summary>
        /// 护符14 法术扭曲者（诺艾尔侧）：诺艾尔施法时的**起手消耗**减 10（不低于 1）。
        /// 起手扣除点是 `M2PrSkill.cs:2813` 的 `Pr.applyBurstMpDamage((int)magicItem.reduce_mp)`；
        /// 这里挂它的前缀改参数，蓄力/命中追加等其它扣魔点不动（按用户选择：只减起手）。
        /// </summary>
        private static bool SpellTwisterBurstMpPrefix(PR __instance, ref int val)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, SpellTwisterId) || !(__instance is PRNoel))
                {
                    return true;
                }
                if (val > 0)
                {
                    val = Mathf.Max(1, val - SpellTwisterMpReduce);
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        // ---------- 护符14 续：咏唱时"这一发要花多少魔力"就直接是 10 少 ----------
        /// <summary>
        /// 诺艾尔魔法消耗结算的标记（0=不管，1=`applyMpDamage` 里减 10，2=已经减过、别再减）。
        /// 施法的三个扣魔入口（`M2PrSkill.explodeMagic` 咏唱完毕/松手施放、
        /// `M2PrSkill.killHoldMagic(MANA_HIT,…)` 被打断取消、`M2PrSkill.digestShotgunHoldMp` 霰弹蓄力结算）
        /// 都在前缀置位、后缀清除；真正扣魔的 `PR.applyMpDamage` 前缀读一次就把它消费掉。
        /// </summary>
        private const int SpellTwisterScopeOff = 0;
        private const int SpellTwisterScopeReduce = 1;
        private const int SpellTwisterScopeAlreadyReduced = 2;
        private static int _noelSpellTwisterCostScope = SpellTwisterScopeOff;

        /// <summary>本次调用是否"本地诺艾尔在付魔法消耗"（小骑士模式/其它施法者不算）。</summary>
        private static bool IsNoelSpellTwisterCast(M2PrSkill skill)
        {
            if (IsKnightMode || !IsEquipped(CharmOwner.Noel, SpellTwisterId))
            {
                return false;
            }
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            return pr != null && ReferenceEquals(skill, pr.Skill);
        }

        /// <summary>
        /// `M2PrSkill.getHoldingMp(bool)` 后缀：**咏唱中的"待扣魔力"预算**也减 10。
        /// 这是关键的一步——AIC 在咏唱时就用它画魔力条的暗色蓄力段（`UIStatus.cs:743`
        /// `num2 = min(maxmp, Skill.getHoldingMp(false))`），`PR.getCastableMp()`
        /// （`PR.cs:5312` = `mp - getHoldingMp(false)`）也用它。
        /// 只改扣魔预算（不动 `mp_hold`），所以魔法威力（`X.ZPOW(mp_hold, reduce_mp)`）不受影响。
        /// </summary>
        private static void SpellTwisterHoldingMpPostfix(M2PrSkill __instance, ref int __result)
        {
            try
            {
                if (__result <= 0 || !IsNoelSpellTwisterCast(__instance))
                {
                    return;
                }
                __result = Mathf.Max(0, __result - SpellTwisterMpReduce);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// `PR.applyMpDamage(int val, bool force, AttackInfo Atk, bool use_quake, bool calc_gsaver)` 前缀：
        /// 把"这次施法要扣的魔力"直接减 10（不低于 1），**不是**先扣后还。
        /// 只改扣魔数额、不动 `mp_hold`，因此魔法威力（由 `mp_hold` 缩放）不受影响；
        /// 标记只消费一次，且只对本地诺艾尔生效。
        /// </summary>
        private static bool SpellTwisterMpDamagePrefix(PR __instance, ref int val)
        {
            try
            {
                int scope = _noelSpellTwisterCostScope;
                _noelSpellTwisterCostScope = SpellTwisterScopeOff;
                if (scope != SpellTwisterScopeReduce || IsKnightMode || !IsEquipped(CharmOwner.Noel, SpellTwisterId))
                {
                    return true;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !ReferenceEquals(__instance, pr))
                {
                    return true;
                }
                if (val > 0)
                {
                    val = Mathf.Max(1, val - SpellTwisterMpReduce);
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>
        /// `M2PrSkill.explodeMagic` 前缀/后缀：咏唱完毕（或松手）施放时的扣魔标记。
        /// 走"过充槽持有"分支时 `num2` 取自 `getHoldingMp(false)`（已经被上面的后缀减过 10），
        /// 因此标记为"已减过"，避免重复减免。
        /// </summary>
        private static void SpellTwisterCastScopePrefix(M2PrSkill __instance)
        {
            try
            {
                if (!IsNoelSpellTwisterCast(__instance))
                {
                    _noelSpellTwisterCostScope = SpellTwisterScopeOff;
                    return;
                }
                M2PrOverChargeSlot slots = __instance.getOverChargeSlots();
                _noelSpellTwisterCostScope = (slots != null && slots.isUseHolding())
                    ? SpellTwisterScopeAlreadyReduced
                    : SpellTwisterScopeReduce;
            }
            catch (Exception)
            {
            }
        }

        private static void SpellTwisterCastScopePostfix()
        {
            _noelSpellTwisterCostScope = SpellTwisterScopeOff;
        }

        /// <summary>
        /// `M2PrSkill.killHoldMagic(MANA_HIT,…)` 前缀/后缀：被打断/取消时的扣魔标记
        /// （只在 `split_mana != NOUSE` 这一路才真的扣魔）。
        /// </summary>
        private static void SpellTwisterHoldKillPrefix(M2PrSkill __instance, MANA_HIT split_mana)
        {
            try
            {
                _noelSpellTwisterCostScope =
                    (split_mana != MANA_HIT.NOUSE && IsNoelSpellTwisterCast(__instance))
                        ? SpellTwisterScopeReduce
                        : SpellTwisterScopeOff;
            }
            catch (Exception)
            {
            }
        }

        private static void SpellTwisterHoldKillPostfix()
        {
            _noelSpellTwisterCostScope = SpellTwisterScopeOff;
        }

        /// <summary>
        /// `M2PrSkill.digestShotgunHoldMp` 前缀/后缀：霰弹"蓄力消耗"同样走上面的
        /// `applyMpDamage` 直接减免（这里只置标记，不改 `reduce_mag`，
        /// 因此蓄力条与霰弹威力都保持原样）。
        /// </summary>
        private static void SpellTwisterHoldScopePrefix(M2PrSkill __instance)
        {
            try
            {
                _noelSpellTwisterCostScope = IsNoelSpellTwisterCast(__instance)
                    ? SpellTwisterScopeReduce
                    : SpellTwisterScopeOff;
            }
            catch (Exception)
            {
            }
        }

        private static void SpellTwisterHoldScopePostfix()
        {
            _noelSpellTwisterCostScope = SpellTwisterScopeOff;
        }

        // ================= 护符17 快速劈砍（诺艾尔侧：挥杖速度 +50%） =================
        /// <summary>快速劈砍：诺艾尔挥杖速度倍率（需求：+50%）。</summary>
        public const float FastSlashSpeedMult = 1.5f;

        // ================= 护符18 修长之钉（诺艾尔侧：近战攻击距离 +20%） =================
        /// <summary>修长之钉：近战攻击距离倍率（需求：+20%）。</summary>
        public const float LongNailReachMult = 1.2f;

        /// <summary>
        /// 护符18 修长之钉（**诺艾尔侧**）：近战攻击距离 +20%
        /// （轻攻击 Punch / 魔法霰弹 Shotgun / 凌空横斩 Airpunch / 会心重击 Fatal Smash）。
        ///
        /// 挂点 = AIC 自己的"手杖 reach（攻击距离）"入口 `PrCaneEquip.reach_ratio(reach_level)`
        /// （`nel/PrCaneEquip.cs:246`，实现 `Pow(near_reach, level) = (near_reach-1)*level + 1`）。
        /// 这正是原生"长法杖"那一系强化的用法：
        ///
        /// - **判定长度**：`PrCaneEquip.initChantMagicAwaken`（`:101-121`）对"基础近战攻击包"
        ///   （`Mg.is_normal_attack &amp;&amp; Mg.run_fn_is_basic_tackle`，即 PR_PUNCH/PR_SHOTGUN/
        ///   PR_COMET/PR_DASHPUNCH/PR_EVADECOUNTER/PR_SMASH 这些 `MDAT.FD_runBasicTackle` 的招式）
        ///   做 `Mg.sx *= reach_ratio(...)`（sx 为 0 时乘 `Mg.sy`），直接决定射线长度
        ///   （`MagicItem.runTackle`，`nel/MagicItem.cs:2593-2611` → `M2Ray.CastRayAndColliderS(..., Dir_, len, ...)`）；
        /// - **挥击特效长度**：`M2PrSkill` 里挥杖/突进/重击/回避反击的粒子变量
        ///   `lax`（`:2581`）、`msx`（`:2834`/`:2885`）、`len`（`:1478`）都乘同一个 `reach_ratio`。
        ///
        /// 所以在这里 ×1.2，判定和特效一起变长 20%，且完全走游戏自己的管线。
        /// 只对诺艾尔模式 + 装备修长之钉生效（小骑士模式不参与，骑士侧有自己的长钉实现）。
        /// </summary>
        private static void LongNailReachRatioPostfix(ref float __result)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, LongNailId))
                {
                    return;
                }
                float before = __result;
                __result *= LongNailReachMult;
                LongNailDiag("reach_ratio " + before + " -> " + __result);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>修长之钉覆盖的招式（近战判定包 kind）：轻攻击/凌空横斩、魔法霰弹、会心重击。</summary>
        private static bool IsLongNailKind(MGKIND kind)
        {
            return kind == MGKIND.PR_PUNCH || kind == MGKIND.PR_SHOTGUN || kind == MGKIND.PR_SMASH;
        }

        /// <summary>本次"游戏自己放大 reach 之前"的基准触及距离（<=0 表示这次不管）。</summary>
        private static float _longNailBaseReach;
        private static int _longNailBaseMgId = -1;
        /// <summary>已经处理过的 `MagicItem.id`（换图后 id 会重来，所以换图时清空）。</summary>
        private static readonly HashSet<int> _longNailDoneIds = new HashSet<int>();
        private static Map2d _longNailDoneMap;

        /// <summary>
        /// 护符18 修长之钉（诺艾尔侧）：**前缀**——在 `PrCaneEquip.initChantMagicAwaken`
        /// 给攻击包乘上游戏自己的 reach 倍率**之前**，量一下这一招原始的"触及距离"
        /// （= 线段长度 |(sx,sy)| + 粗细 |sz|，见 `MagicItem.runTackle`/`M2Ray.CastRayAndColliderS`）。
        /// </summary>
        private static void LongNailCaneAwakenPrefix(MagicItem Mg)
        {
            _longNailBaseReach = 0f;
            _longNailBaseMgId = -1;
            try
            {
                if (Mg == null || IsKnightMode || !IsEquipped(CharmOwner.Noel, LongNailId))
                {
                    return;
                }
                if (!(Mg.Caster is PRNoel) || !IsLongNailKind(Mg.kind))
                {
                    return;
                }
                if (!ReferenceEquals(Mg.Mp, _longNailDoneMap))
                {
                    _longNailDoneMap = Mg.Mp;
                    _longNailDoneIds.Clear(); // 换图后 MagicItem.id 会从 0 重新开始
                }
                if (_longNailDoneIds.Contains(Mg.id))
                {
                    return;
                }
                float len = Mathf.Sqrt(Mg.sx * Mg.sx + Mg.sy * Mg.sy);
                if (len <= 0.0001f)
                {
                    return;
                }
                _longNailBaseReach = len + Mathf.Abs(Mg.sz);
                _longNailBaseMgId = Mg.id;
            }
            catch (Exception)
            {
                _longNailBaseReach = 0f;
            }
        }

        /// <summary>
        /// 护符18 修长之钉（诺艾尔侧）：**后缀**——把这一招最终的"触及距离"精确改成
        /// `基准 × 1.2`。之所以要在这里再修一次：游戏自己的 reach 只乘在线段 `sx` 上，
        /// 粗细 `sz`（本招 0.45 格）不参与，所以只靠倍率的话，20% 落到总触及距离上只剩约 9%
        /// （实测 1.38 格 → 1.50 格，≈3.5 像素，肉眼几乎看不出）。
        /// </summary>
        private static void LongNailCaneAwakenPostfix(MagicItem Mg)
        {
            try
            {
                if (_longNailBaseReach <= 0f || Mg == null || Mg.id != _longNailBaseMgId)
                {
                    _longNailBaseReach = 0f;
                    return;
                }
                float rad = Mathf.Abs(Mg.sz);
                float target = _longNailBaseReach * LongNailReachMult; // 期望的总触及距离
                float need = target - rad;                             // 线段需要达到的长度
                float len = Mathf.Sqrt(Mg.sx * Mg.sx + Mg.sy * Mg.sy);
                if (need > 0f && len > 0.0001f && need > len)
                {
                    float k = need / len;
                    Mg.sx *= k;
                    Mg.sy *= k;
                }
                _longNailDoneIds.Add(Mg.id);
                _longNailBaseReach = 0f;
            }
            catch (Exception)
            {
                _longNailBaseReach = 0f;
            }
        }

        /// <summary>临时诊断：把修长之钉相关的关键数值写进 BepInEx 日志（上限 40 行，排除后删除）。</summary>
        private static int _longNailDiagCount;

        private static void LongNailDiag(string msg)
        {
            if (_longNailDiagCount >= 40)
            {
                return;
            }
            _longNailDiagCount++;
            KnightInCradlePlugin.PluginLog?.LogInfo("[KIC][长钉诊断] " + msg);
        }

        /// <summary>临时诊断：记录诺艾尔近战攻击包每帧的判定几何（sx/sy/sz + 射线长度/半径 + 命中）。</summary>
        private static void LongNailDiagCircleCastPostfix(MagicItem Mg, ref HITTYPE __result)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, LongNailId))
                {
                    return;
                }
                if (Mg == null || !(Mg.Caster is PRNoel))
                {
                    return;
                }
                switch (Mg.kind)
                {
                    case MGKIND.PR_PUNCH:
                    case MGKIND.PR_SHOTGUN:
                    case MGKIND.PR_SMASH:
                        break;
                    default:
                        return;
                }
                float len = Mg.Ray != null ? Mg.Ray.lenmp : -1f;
                float rad = Mg.Ray != null ? Mg.Ray.radius_map : -1f;
                LongNailDiag("kind=" + Mg.kind + " id=" + Mg.id + " sx=" + Mg.sx + " sy=" + Mg.sy +
                             " sz=" + Mg.sz + " rayLen=" + len + " rayRad=" + rad +
                             " 触及=" + (len + rad) + " hit=" + __result);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>`PR.state`（protected 字段）的快速读取器，用于判断"当前是不是挥击状态"。</summary>
        private static AccessTools.FieldRef<PR, PR.STATE> _prStateRef;

        /// <summary>诺艾尔的"挥击/技艺状态"白名单（快速劈砍只在这些状态里加速）。</summary>
        private static bool IsNoelAttackState(PR pr)
        {
            try
            {
                if (_prStateRef == null)
                {
                    _prStateRef = AccessTools.FieldRefAccess<PR, PR.STATE>("state");
                }
                if (_prStateRef == null)
                {
                    return false;
                }
                switch (_prStateRef(pr))
                {
                    case PR.STATE.PUNCH:
                    case PR.STATE.AIRPUNCH:
                    case PR.STATE.AIRPUNCH_SHOTGUN:
                    case PR.STATE.WHEEL:
                    case PR.STATE.WHEEL_SHOTGUN:
                    case PR.STATE.COMET:
                    case PR.STATE.COMET_SHOTGUN:
                    case PR.STATE.DASHPUNCH:
                    case PR.STATE.DASHPUNCH_SHOTGUN:
                    case PR.STATE.SMASH:
                    case PR.STATE.SMASH_SHOTGUN:
                    case PR.STATE.SLIDING:
                    case PR.STATE.EVADECOUNTER:
                    case PR.STATE.EVADECOUNTER_SHOTGUN:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护符17 快速劈砍（**诺艾尔侧**）：诺艾尔挥动法杖的速度提升 50%。
        ///
        /// 挂点：`PR.baseTS`（`nel/PR.cs:1417` 重写的属性，`= _baseTS * Skill.baseTimeScale()`）。
        /// `M2Mover.TS => Map2d.TS * baseTS`（`m2d/M2Mover.cs:2514`），而：
        /// ① **挥击状态推进**：主状态计时 `t_state += base.TS` → ×1.5 后整个挥击流程快 50%；
        /// ② **动画播放**：`M2PxlAnimator.runPre` 里 `ts = 帧时间 × Mv.TS × animator_TS × timescale`
        ///    （`m2d/M2PxlAnimator.cs:202`）→ 同样 ×1.5。
        ///
        /// 为什么不用 `M2PrSkill.PunchSpeed`（一稿的挂点）：它只喂给 ①`Anm.timescale`
        /// 与 ②各状态的**起手几帧**（`t += TS * PunchSpeed(...)`，`t &lt; 9` 那一段）；
        /// 挥击主体（`t` 9→24）是主状态计时在推，不受它影响 —— 所以一稿实测"几乎没变快"。
        ///
        /// 只在**挥击/技艺状态**里加速（`IsNoelAttackState`），走路/跳跃等不受影响；
        /// 也只对本地诺艾尔 + 装备快速劈砍生效（小骑士模式不参与）。
        /// </summary>
        private static void FastSlashBaseTsPostfix(PR __instance, ref float __result)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, FastSlashId))
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !ReferenceEquals(__instance, pr) || !IsNoelAttackState(pr))
                {
                    return;
                }
                __result *= FastSlashSpeedMult;
            }
            catch (Exception)
            {
            }
        }

        // ================= 护符15 稳定之体（诺艾尔侧：不摔倒 / 免疫风力 / 免疫黏滑地面） ===
        /// <summary>
        /// 护符15 稳定之体，三条效果各自的原生机制（0.30g 反编译实证）：
        /// ① **不会因为触碰魔物而摔倒**：魔物的身体接触伤害是 `MGKIND.TACKLE` 的魔法
        ///    （`NelEnemy.tackleInit`，`MGContainer.CircleCast` 里 `Atk.PublishMagic = Mg`），
        ///    它在 `M2PrADmg.applyDamage` 里按击退量把诺艾尔推进 `PR.STATE.DAMAGE_LT`（摔倒姿势）。
        ///    这里记下"本帧这次伤害是接触伤害"，再拦掉紧随其后的摔倒状态切换（HP 伤害照常结算）。
        /// ② **免疫风力**：风压等级由 `PR.getWindApplyLevel` 给出、实际推力在 `PR.applyWindFoc`，
        ///    两个入口都跳过（与小骑士模式的 `CombatGuard.PrWindFocPrefix` 同款做法）。
        /// ③ **免疫黏滑地面（冰面）**：`M2FootManager` 踩到 `foottype == "ice"` 时调
        ///    `M2Phys.addOnIce` 拉高 `t_ice`，而 `t_ice &gt; 0` 会把横向摩擦力压到 1.5%
        ///    （`M2Phys.cs:641`）。这里拦掉给诺艾尔上的 `t_ice`，她的移动不再打滑。
        /// </summary>
        private static PR _stableBodyContactPr;
        private static int _stableBodyContactFrame = -1;
        private static bool _stableBodyHitSinkScope;

        /// <summary>该 PR 是否就是"佩戴了稳定之体的本地诺艾尔"。</summary>
        private static bool IsStableBodyApplied(PR pr)
        {
            if (pr == null || IsKnightMode || !IsEquipped(CharmOwner.Noel, StableId))
            {
                return false;
            }
            PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
            return noel != null && ReferenceEquals(pr, noel);
        }

        /// <summary>风力②：风压等级直接为 0。</summary>
        private static bool StableBodyWindLevelPrefix(PR __instance, ref float __result)
        {
            try
            {
                if (IsStableBodyApplied(__instance))
                {
                    __result = 0f;
                    return false;
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>风力②：吹飞推力整体跳过。</summary>
        private static bool StableBodyWindFocPrefix(PR __instance)
        {
            try
            {
                return !IsStableBodyApplied(__instance);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>黏滑地面③：不给诺艾尔上冰面打滑计时（`t_ice`）。</summary>
        private static bool StableBodyAddOnIcePrefix(M2Phys __instance)
        {
            try
            {
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                // M2Mover.Phy 是 protected，但 M2Phys.Mv 是 public readonly，可直接反查归属。
                if (noel != null && __instance != null && ReferenceEquals(__instance.Mv, noel) &&
                    IsStableBodyApplied(noel))
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>接触伤害①：记下"本帧这次伤害是魔物身体接触"。</summary>
        private static void StableBodyContactDamagePrefix(M2PrADmg __instance, NelAttackInfo Atk)
        {
            try
            {
                if (__instance == null || Atk == null || !IsStableBodyApplied(__instance.Pr))
                {
                    return;
                }
                MagicItem mg = Atk.PublishMagic;
                if (mg == null || mg.kind != MGKIND.TACKLE)
                {
                    return;
                }
                _stableBodyContactPr = __instance.Pr;
                _stableBodyContactFrame = Time.frameCount;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>接触伤害①：紧接着的"摔倒"状态切换直接跳过（伤害已结算完）。</summary>
        private static bool StableBodyChangeStatePrefix(PR __instance, PR.STATE _state)
        {
            try
            {
                if (_state != PR.STATE.DAMAGE_LT && _state != PR.STATE.DAMAGE_LT_KIRIMOMI)
                {
                    return true;
                }
                if (_stableBodyContactFrame != Time.frameCount ||
                    !ReferenceEquals(__instance, _stableBodyContactPr) ||
                    !IsStableBodyApplied(__instance))
                {
                    return true;
                }
                _stableBodyContactPr = null;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// ①-b **走路/跑动撞到魔物而摔倒**（无伤害的那条，正是需求里说的"碰到怪摔倒"）：
        /// 玩家身体撞到魔物时走 `PR.moveByHitCheck`（`:5233`），里面按魔物种类调
        /// `addEnemySink(EnemyAttr.getSinkRatio(enemy), …)` 给 `PR.RCenemy_sink` 累加"被撞倒计量"；
        /// 计量 ≥ 6（`PR.checkEnemySink`，`:3692-3715`）就 `changeState(PR.STATE.ENEMY_SINK)` —— 摔倒。
        /// 跑动状态下累加量还会 ×2.3（`:5293-5296`），所以"跑着撞上去"特别容易摔。
        ///
        /// 做法：只在 `moveByHitCheck` 这一路里屏蔽累加（前缀置位、后缀清除），
        /// 物理推挤本身照旧；其它 sink 来源（醉酒、被魔物驮着、硬撞）不受影响。
        /// </summary>
        private static void StableBodyMoveHitPrefix(PR __instance)
        {
            try
            {
                _stableBodyHitSinkScope = IsStableBodyApplied(__instance);
            }
            catch (Exception)
            {
                _stableBodyHitSinkScope = false;
            }
        }

        private static void StableBodyMoveHitPostfix()
        {
            _stableBodyHitSinkScope = false;
        }

        /// <summary>①-b：撞怪这一路不计入"被撞倒计量"（其它来源照常）。</summary>
        private static bool StableBodyEnemySinkPrefix(PR __instance)
        {
            try
            {
                if (_stableBodyHitSinkScope && IsStableBodyApplied(__instance))
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        // ================= 护符13 坚固力量（诺艾尔侧：骨钉系技能最终伤害 +25%） =================
        /// <summary>坚固力量：下列招式的最终伤害倍率（需求：+25%）。</summary>
        public const float PowerDamageMult = 1.25f;

        /// <summary>
        /// 坚固力量覆盖的招式（按 `MGKIND` 判定）：
        /// PR_PUNCH（轻攻击 Punch，凌空横斩 Airpunch 走的也是这个 kind）、
        /// PR_SHOTGUN（魔法霰弹）、PR_WHEEL（旋风斩击 / 轮舞斩击）、PR_COMET（彗星俯冲）、
        /// PR_DASHPUNCH（突进冲击）、PR_SMASH（会心重击）。
        /// </summary>
        private static bool IsPowerBoostKind(MGKIND kind)
        {
            switch (kind)
            {
                case MGKIND.PR_PUNCH:
                case MGKIND.PR_SHOTGUN:
                case MGKIND.PR_WHEEL:
                case MGKIND.PR_COMET:
                case MGKIND.PR_DASHPUNCH:
                case MGKIND.PR_SMASH:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>CircleCast 期间临时抬高的伤害值，调用结束原样还原（避免污染可复用的 Atk）。</summary>
        private sealed class ShamanBoostState
        {
            public int Hp0;
        }

        // ================= 护符16 沉重之击（诺艾尔侧：连击 5 次进入"会心"） =================
        /// <summary>沉重之击：连续命中多少次进入"会心"（需求：5 次）。</summary>
        public const int HeavyBlowHitsToFocus = 5;
        /// <summary>沉重之击："会心"期间命中造成伤害的倍率（需求：+40%）。</summary>
        public const float HeavyBlowFocusMult = 1.4f;
        /// <summary>"会心"光圈素材（需求指定 nail_charge_effect0005～0009，与小骑士骨钉技艺蓄力同款）。</summary>
        private static readonly string[] HeavyBlowAuraSprites =
        {
            "nail_charge_effect0005",
            "nail_charge_effect0006",
            "nail_charge_effect0007",
            "nail_charge_effect0008",
            "nail_charge_effect0009",
        };
        /// <summary>光圈播放帧率（与小骑士骨钉技艺蓄力的 20fps 一致）。</summary>
        private const float HeavyBlowAuraFps = 20f;
        /// <summary>光圈渲染大小倍率（需求：当前的 1.5 倍）。</summary>
        private const float HeavyBlowAuraScale = 1.5f;
        /// <summary>光圈锚点的额外纵向偏移（格；AIC 的 y **向下为正**，所以 -1 = 向上 1 格）。</summary>
        private const float HeavyBlowAuraOffY = -1f;
        /// <summary>
        /// 同一次"攻击动作"里允许重复调用判定起点的间隔（秒）。
        /// AIC 的一次挥击可能创建多个 `MagicItem`（`M2PrSkill.cs:2573` 用 `executeSmallAttack(num++, Mg)`
        /// 循环创建，例如长距离拳的 id=0/1 两个判定物），这些都属于**同一次攻击**，不能各算一发。
        /// </summary>
        private const float HeavyBlowSameAttackGap = 0.12f;
        /// <summary>近战（挥击/骨钉技艺）的命中判定窗口（秒）：判定物创建后多久内该打中。</summary>
        private const float HeavyBlowMeleeWindow = 0.6f;
        /// <summary>魔法的命中判定窗口（秒）：留给弹道飞行/咏唱后延迟。</summary>
        private const float HeavyBlowMagicWindow = 3f;

        /// <summary>"会心"连击计数（0～5）。</summary>
        private static int _heavyFocusHits;
        /// <summary>是否已进入"会心"（进入后一直保持，直到有攻击未命中）。</summary>
        private static bool _heavyFocusActive;
        /// <summary>当前是否有一发"已出手、还没结算"的攻击。</summary>
        private static bool _heavyFocusPending;
        /// <summary>这一发是否已经打中过（打中就结算掉，不再等窗口过期）。</summary>
        private static bool _heavyFocusPendingHit;
        private static float _heavyFocusPendingAt;
        private static float _heavyFocusPendingUntil;
        /// <summary>最近一次"命中事件"的时间（用于识别"命中发生在出手登记之前"的时序）。</summary>
        private static bool _heavyFocusHasHit;
        private static float _heavyFocusLastHitAt;
        private static bool _heavyFocusHasLastBegin;
        private static float _heavyFocusLastBeginAt;
        private static float _heavyFocusAuraTime;
        private static Texture2D[] _heavyFocusAuraTex;
        private static MeshDrawer _heavyFocusAuraMesh;
        private static Material _heavyFocusAuraMat;
        private static M2RenderTicket _heavyFocusAuraTicket;
        private static Map2d _heavyFocusAuraMap;

        /// <summary>"会心"是否成立（伤害乘区查询用；换模式/卸下护符时一律按不成立处理）。</summary>
        private static bool IsHeavyFocusActive
        {
            get
            {
                return _heavyFocusActive && !IsKnightMode && IsEquipped(CharmOwner.Noel, HeavyBlowId);
            }
        }

        private static void ResetHeavyFocus()
        {
            _heavyFocusHits = 0;
            _heavyFocusActive = false;
            ClearHeavyFocusPending();
            _heavyFocusHasHit = false;
            _heavyFocusLastHitAt = 0f;
            _heavyFocusHasLastBegin = false;
            _heavyFocusLastBeginAt = 0f;
            _heavyFocusAuraTime = 0f;
        }

        private static void ClearHeavyFocusPending()
        {
            _heavyFocusPending = false;
            _heavyFocusPendingHit = false;
            _heavyFocusPendingAt = 0f;
            _heavyFocusPendingUntil = 0f;
        }

        /// <summary>命中一次：计数 +1；满 5 次进入"会心"（进入后保持，不再清零）。</summary>
        private static void OnHeavyFocusHit()
        {
            if (_heavyFocusHits < HeavyBlowHitsToFocus)
            {
                _heavyFocusHits++;
            }
            if (_heavyFocusHits >= HeavyBlowHitsToFocus)
            {
                _heavyFocusActive = true;
            }
        }

        /// <summary>未命中：计数清零；若已进入"会心"则退出（光圈随之消失）。</summary>
        private static void OnHeavyFocusMiss()
        {
            _heavyFocusHits = 0;
            if (_heavyFocusActive)
            {
                _heavyFocusActive = false;
                _heavyFocusAuraTime = 0f;
            }
        }

        /// <summary>
        /// 「一次攻击出手」：在挥击/施法真正发出的那一刻登记，`window` 秒内打中算命中、否则算未命中。
        /// 同一次挥击里重复调用（多个判定物）按 `HeavyBlowSameAttackGap` 合并成同一发。
        /// </summary>
        private static void BeginHeavyFocusAttack(float window)
        {
            if (IsKnightMode || !IsEquipped(CharmOwner.Noel, HeavyBlowId))
            {
                return;
            }
            float now = Time.unscaledTime;
            if (_heavyFocusHasLastBegin && now - _heavyFocusLastBeginAt <= HeavyBlowSameAttackGap)
            {
                // 同一次攻击动作的后续判定物（例如一次挥击循环创建多个 MagicItem）：
                // 只把判定窗口往后延，不新开一发、也不算未命中。
                if (_heavyFocusPending && !_heavyFocusPendingHit)
                {
                    _heavyFocusPendingUntil = Mathf.Max(_heavyFocusPendingUntil, now + window);
                }
                _heavyFocusLastBeginAt = now;
                return;
            }
            // 新的一发：上一发如果一次都没打中，先结算成"未命中"
            if (_heavyFocusPending && !_heavyFocusPendingHit)
            {
                OnHeavyFocusMiss();
            }
            ClearHeavyFocusPending();
            _heavyFocusHasLastBegin = true;
            _heavyFocusLastBeginAt = now;
            // 命中事件早于"出手登记"的时序（法术在 `explodeMagic` 内部同一帧就命中、
            // 或判定物创建瞬间(`magicItem.run(0f)`)就碰到敌人）→ 直接按命中结算。
            if (_heavyFocusHasHit && now - _heavyFocusLastHitAt <= HeavyBlowSameAttackGap)
            {
                _heavyFocusHasHit = false;
                OnHeavyFocusHit();
                return;
            }
            _heavyFocusPending = true;
            _heavyFocusPendingHit = false;
            _heavyFocusPendingAt = now;
            _heavyFocusPendingUntil = now + window;
        }

        /// <summary>当前这一发打中了：结算成一次"击中"（同一发只结算一次）。</summary>
        private static void ResolveHeavyFocusHit()
        {
            _heavyFocusHasHit = true;
            _heavyFocusLastHitAt = Time.unscaledTime;
            if (!_heavyFocusPending || _heavyFocusPendingHit)
            {
                return;
            }
            ClearHeavyFocusPending();
            OnHeavyFocusHit();
        }

        /// <summary>
        /// 诺艾尔"会造成伤害的攻击"判据（连击统计用）：**直接看攻击包自己的伤害值**，
        /// 而不是按 `MGKIND` 白名单——这样轻攻击、各类骨钉技艺、滑铲、回避反击、盾击、
        /// 所有法术（含魔法霰弹）只要真的能造成伤害都会算进去，符合"任何能造成伤害的攻击"。
        /// </summary>
        private static bool IsDamagingAttack(MagicItem Mg)
        {
            try
            {
                AttackInfo atk = Mg != null ? Mg.Atk0 : null;
                return atk != null && (atk._hpdmg > 0 || atk._mpdmg > 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护符16 沉重之击（诺艾尔侧）：命中登记。挂在诺艾尔攻击命中的汇聚点
        /// `MGContainer.CircleCast` 上：`__result` 含 `HITTYPE.HITTED_EN` 就是"这一发打中了"。
        /// </summary>
        private static void HeavyBlowCircleCastPostfix(MagicItem Mg, ref HITTYPE __result)
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, HeavyBlowId))
                {
                    ResetHeavyFocus();
                    return;
                }
                if (Mg == null || !(Mg.Caster is PRNoel) || !IsDamagingAttack(Mg))
                {
                    return;
                }
                if ((__result & HITTYPE.HITTED_EN) != HITTYPE.NONE)
                {
                    ResolveHeavyFocusHit();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符16 沉重之击（诺艾尔侧）：近战出手登记。
        /// `M2PrSkill.executeSmallAttack` 是所有挥击/骨钉技艺（`PR_PUNCH`/`PR_SHOTGUN`/`PR_WHEEL`/
        /// `PR_COMET`/`PR_DASHPUNCH`/`PR_SMASH`…）创建攻击判定物的地方，返回非 null 才算真的出手了。
        /// </summary>
        private static void HeavyBlowSmallAttackPostfix(MagicItem __result)
        {
            try
            {
                if (!IsDamagingAttack(__result))
                {
                    return;
                }
                BeginHeavyFocusAttack(HeavyBlowMeleeWindow);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符16 沉重之击（诺艾尔侧）：魔法出手登记。
        /// `M2PrSkill.explodeMagic` 返回 true = 这一发法术真的放出去了（返回 false 的早退分支不算）。
        /// </summary>
        private static void HeavyBlowExplodeMagicPostfix(bool __result)
        {
            try
            {
                if (!__result)
                {
                    return;
                }
                BeginHeavyFocusAttack(HeavyBlowMagicWindow);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进（诺艾尔模式调用）：判定窗口过期（=未命中）+ 光圈播放 + 票据维护。</summary>
        public static void TickNoelHeavyBlowCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, HeavyBlowId))
                {
                    ResetHeavyFocus();
                    EnsureHeavyFocusAuraTicket(pr, false);
                    return;
                }
                // 这一发过了判定窗口还没打中 → 未命中
                if (_heavyFocusPending && !_heavyFocusPendingHit &&
                    Time.unscaledTime > _heavyFocusPendingUntil)
                {
                    ClearHeavyFocusPending();
                    OnHeavyFocusMiss();
                }
                if (IsHeavyFocusActive)
                {
                    _heavyFocusAuraTime += Time.deltaTime;
                }
                EnsureHeavyFocusAuraTicket(pr, IsHeavyFocusActive);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>"会心"光圈票据：绑当前地图的 MovRenderer（玩家身后层 PR0，同小骑士蓄力光圈）。</summary>
        private static void EnsureHeavyFocusAuraTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseHeavyFocusAuraTicket();
                return;
            }
            if (_heavyFocusAuraTex == null)
            {
                _heavyFocusAuraTex = LoadHeavyFocusAuraTextures();
            }
            if (_heavyFocusAuraTex == null)
            {
                return; // 素材缺失：只是不显示，连击与伤害照常
            }
            if (_heavyFocusAuraMesh != null && _heavyFocusAuraMap == mp && _heavyFocusAuraTicket != null)
            {
                return;
            }
            ReleaseHeavyFocusAuraTicket();
            _heavyFocusAuraMap = mp;
            _heavyFocusAuraMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _heavyFocusAuraMesh.draw_gl_only = true;
            _heavyFocusAuraMat = MTRX.newMtr(MTRX.ShaderGDT);
            _heavyFocusAuraMat.EnableKeyword("NO_PIXELSNAP");
            _heavyFocusAuraMesh.activate("noel_heavy_focus", _heavyFocusAuraMat, false, MTRX.ColWhite, null);
            _heavyFocusAuraTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareHeavyFocusAuraMesh, _heavyFocusAuraMesh, null, null);
        }

        private static void ReleaseHeavyFocusAuraTicket()
        {
            try
            {
                if (_heavyFocusAuraTicket != null && _heavyFocusAuraMap != null &&
                    _heavyFocusAuraMap.MovRenderer != null)
                {
                    _heavyFocusAuraMap.MovRenderer.deassignDrawable(_heavyFocusAuraTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_heavyFocusAuraMat != null)
                {
                    IN.DestroyOne(_heavyFocusAuraMat);
                }
            }
            catch (Exception)
            {
            }
            _heavyFocusAuraTicket = null;
            _heavyFocusAuraMesh = null;
            _heavyFocusAuraMat = null;
            _heavyFocusAuraMap = null;
        }

        /// <summary>光圈绘制：锚定诺艾尔中心，按 20fps 循环播 nail_charge_effect0005～0009。</summary>
        private static bool PrepareHeavyFocusAuraMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _heavyFocusAuraMap;
            if (mp == null || _heavyFocusAuraMesh == null || draw_id != 0)
            {
                return false;
            }
            _heavyFocusAuraMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _heavyFocusAuraTex == null || !IsHeavyFocusActive)
            {
                MdOut = _heavyFocusAuraMesh;
                return true;
            }
            int frame = Mathf.Abs((int)(_heavyFocusAuraTime * HeavyBlowAuraFps)) % _heavyFocusAuraTex.Length;
            Texture2D tex = _heavyFocusAuraTex[frame];
            if (tex == null)
            {
                MdOut = _heavyFocusAuraMesh;
                return true;
            }
            // 锚点 = 诺艾尔**身体中心**：`pr.mbottom` 是脚底、`pr.sizey` 是身高（格），
            // 所以中心 = 脚底 − 身高/2（比直接用 `pr.y` 稳，AIC 里 `y` 并不总等于身体中心）。
            // 再按需求上移 `HeavyBlowAuraOffY` 格（y 向下为正，-1 = 向上 1 格）。
            float cy = pr.mbottom - pr.sizey * 0.5f + HeavyBlowAuraOffY;
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(cy * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.325f;
            float w = tex.width * scale * HeavyBlowAuraScale;
            float h = tex.height * scale * HeavyBlowAuraScale;
            _heavyFocusAuraMesh.Col = MTRX.ColWhite;
            _heavyFocusAuraMesh.initForImgAndTexture(tex);
            _heavyFocusAuraMesh.uv_top = 0f;
            _heavyFocusAuraMesh.uv_height = 1f;
            _heavyFocusAuraMesh.uv_left = 0f;
            _heavyFocusAuraMesh.uv_width = 1f;
            // 注意 `MeshDrawer.Rect(x, y, w, h)` 的 (x,y) **本身就是矩形中心**
            // （内部 `RectBL(x - w/2, y - h/2, …)`），所以传 (0,0) 才是"以锚点为中心"。
            // 之前多减了一次半宽高，整体被推到诺艾尔左下方，而且放大时偏移会等比变大。
            _heavyFocusAuraMesh.Rect(0f, 0f, w, h, false);
            MdOut = _heavyFocusAuraMesh;
            return true;
        }

        private static Texture2D[] LoadHeavyFocusAuraTextures()
        {
            try
            {
                var list = new Texture2D[HeavyBlowAuraSprites.Length];
                for (int i = 0; i < list.Length; i++)
                {
                    string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets",
                        "hk", "sprites", HeavyBlowAuraSprites[i] + ".png");
                    if (!System.IO.File.Exists(path))
                    {
                        return null;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
                    {
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    list[i] = tex;
                }
                return list;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 诺艾尔侧"最终伤害乘区"的统一挂点（护符5 萨满之石 +25%、护符13 坚固力量 +25%、
        /// 护符16 沉重之击 20% 概率 ×2）。
        ///
        /// 做法：在法术命中汇聚点 `MGContainer.CircleCast` 的**前缀**里把这次攻击的基准伤害
        /// `Atk.hpdmg0` 临时乘上倍率（`CircleCast` 内部会对每个命中目标用 `hpdmg0` 重算
        /// `hpdmg_current`（`AttackInfo._hpdmg` = `hpdmg_current ?? hpdmg0`），所以从基准值入手
        /// 能让**每个目标**都吃到加成；没走 shuffle 的路径直接用 `hpdmg0` 也同样被抬高），postfix 里原样还原，
        /// 不会污染这一发法术复用/后续的伤害数据。只对诺艾尔模式 + 诺艾尔自己放的法术生效。
        /// </summary>
        private static void ShamanCircleCastPrefix(MagicItem Mg, NelAttackInfo Atk, ref ShamanBoostState __state)
        {
            __state = null;
            try
            {
                if (IsKnightMode ||
                    !(IsEquipped(CharmOwner.Noel, ShamanId) || IsEquipped(CharmOwner.Noel, PowerId) ||
                      IsEquipped(CharmOwner.Noel, HeavyBlowId)))
                {
                    return;
                }
                if (Mg == null || Atk == null || !(Mg.Caster is PRNoel))
                {
                    return;
                }
                // 萨满之石（法术）与坚固力量（骨钉系技能）都抬高这一发的基准伤害，两者同时满足就连乘
                float mult = 1f;
                if (IsEquipped(CharmOwner.Noel, ShamanId) && IsPlayerMagicKind(Mg.kind))
                {
                    mult *= ShamanDamageMult;
                }
                if (IsEquipped(CharmOwner.Noel, PowerId) && IsPowerBoostKind(Mg.kind))
                {
                    mult *= PowerDamageMult;
                }
                // 护符16 沉重之击：进入"会心"后，诺艾尔造成的伤害 +40%（不限定招式），
                // 与萨满之石/坚固力量连乘。
                if (IsHeavyFocusActive)
                {
                    mult *= HeavyBlowFocusMult;
                }
                if (mult <= 1f)
                {
                    return;
                }
                var st = new ShamanBoostState { Hp0 = Atk.hpdmg0 };
                if (st.Hp0 > 0f)
                {
                    Atk.hpdmg0 = Mathf.FloorToInt(st.Hp0 * mult + 0.5f);
                }
                __state = st;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把 ↑ 临时抬高的伤害值还原。</summary>
        private static void ShamanCircleCastPostfix(NelAttackInfo Atk, ShamanBoostState __state)
        {
            try
            {
                if (__state == null || Atk == null)
                {
                    return;
                }
                Atk.hpdmg0 = __state.Hp0;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>进入伪次数血：真实 hp/maxhp 寄存在 SF，字段换成次数。</summary>
        private static void ActivateNoelSturdy(PRNoel pr)
        {
            int savedRealMax = COOK.getSF(SturdyRealMaxHpKey);
            int savedRealHp = COOK.getSF(SturdyRealHpKey);
            bool fromSave = savedRealMax > 0;
            int realMax;
            int realHp;
            if (fromSave)
            {
                // 读档回到"已佩戴"状态：字段里已经是次数，真实值从 SF 取回
                realMax = savedRealMax;
                realHp = savedRealHp;
            }
            else
            {
                realMax = (int)PrMaxHpField.GetValue(pr);
                realHp = (int)PrHpField.GetValue(pr);
                if (realMax <= 0)
                {
                    return;
                }
                COOK.setSF(SturdyRealMaxHpKey, Mathf.Clamp(realMax, 0, 255));
                COOK.setSF(SturdyRealHpKey, Mathf.Clamp(realHp, 0, 255));
            }
            int hitMax = SturdyHitMax(realMax);
            int hp;
            if (fromSave)
            {
                hp = Mathf.Clamp((int)PrHpField.GetValue(pr), 0, hitMax);
            }
            else
            {
                // 首次佩戴：当前血量按比例折算成次数（还剩血就至少 1 次）
                hp = Mathf.Clamp(Mathf.CeilToInt(realHp * (float)hitMax / Mathf.Max(1, realMax)), 0, hitMax);
            }
            _noelSturdyRealMaxHp = realMax;
            _noelSturdyRealHp = Mathf.Clamp(realHp, 0, realMax);
            PrMaxHpField.SetValue(pr, hitMax);
            PrHpField.SetValue(pr, hp);
            _noelSturdyActive = true;
            RefreshNoelHudHp();
        }

        /// <summary>退出伪次数血：把佩戴时寄存的真实 hp/maxhp 原样写回，清掉寄存键。</summary>
        private static void DeactivateNoelSturdy(PRNoel pr)
        {
            int realMax = _noelSturdyRealMaxHp > 0 ? _noelSturdyRealMaxHp : 150;
            int realHp = _noelSturdyRealHp >= 0 ? _noelSturdyRealHp : realMax;
            PrMaxHpField.SetValue(pr, realMax);
            PrHpField.SetValue(pr, Mathf.Clamp(realHp, 0, realMax));
            COOK.setSF(SturdyRealMaxHpKey, 0);
            COOK.setSF(SturdyRealHpKey, 0);
            _noelSturdyActive = false;
            _noelSturdyRealMaxHp = -1;
            _noelSturdyRealHp = -1;
            RefreshNoelHudHp();
        }

        /// <summary>
        /// 护符3 坚硬外壳（诺艾尔专属）：受到的伤害 ≤ 20 记 0、&gt; 20 一律记 1；
        /// 无论记成 0 还是 1，都立刻给诺艾尔 2 秒无敌（走 AIC 原生 `NoDamage`，
        /// 后续伤害由游戏自己挡掉，而不是模组另做一套计时）。
        /// 挂在 `M2Attackable.applyHpDamage` 上——它是玩家受伤干线的最后一站
        /// （`M2PrADmg.applyHpDamageSimple` → `GSaver.applyHpDamage` → `Pr.applyHpDamage`），
        /// 只对本地诺艾尔生效，敌人/其它可攻击物不受影响。
        /// </summary>
        private static bool SturdyHpDamagePrefix(M2Attackable __instance, ref int val)
        {
            if (!(__instance is PRNoel noel) || val <= 0)
            {
                return true;
            }
            // 护符9 幼虫之歌：受到伤害 → 立刻回 20MP（用"原始伤害"判断，先于坚硬外壳的改写）
            TryGrantGrubsongMp();
            if (!_noelSturdyActive)
            {
                return true;
            }
            // ≤ 20 → 0；> 20 → 1
            val = val <= SturdyDamageThreshold ? 0 : 1;
            // 掉血后 HUD 的数字也要立刻更新（原版受伤流程走 cushion 分支，不会置 redraw_bar_num）
            RefreshNoelHudHp();
            // 无论记成 0 还是 1，都立刻给 2 秒无敌
            try
            {
                if (PrNoDamageField != null &&
                    PrNoDamageField.GetValue(noel) is M2NoDamageManager nd)
                {
                    nd.Add(SturdyInvincibleFrames);
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>萨满之石：法术伤害每段提升 25%（四舍五入取整）。</summary>
        public static int ScaleSpellDamage(int baseDmg)
        {
            int dmg = baseDmg;
            if (!IsEquipped(ShamanId))
            {
                dmg = baseDmg;
            }
            else
            {
                dmg = Mathf.FloorToInt(baseDmg * 1.25f + 0.5f);
            }
            // 坚固贪婪：每持有 4 个物品，持有者造成的伤害降低 0.75%（法术属于持有者伤害）
            return Mathf.Max(1, Mathf.FloorToInt(dmg * GreedDamageMultiplier() + 0.5f));
        }

        /// <summary>亡者之怒：装备亡者之怒且小骑士血量恰为 1 时生效。</summary>
        public static bool IsFuryActive()
        {
            KnightEntity k = KnightEntity.Instance;
            return k != null && k.FuryActive;
        }

        /// <summary>骨钉伤害（普攻 + 三剑技）：坚固力量 +50%；亡者之怒（1 血）额外 +75%，可叠加。</summary>
        public static int ScaleNailDamage(int baseDmg)
        {
            float mul = 1f;
            if (IsEquipped(PowerId))
            {
                mul *= 1.5f;
            }
            if (IsFuryActive())
            {
                mul *= 1.75f;
            }
            int dmg;
            if (mul == 1f)
            {
                dmg = baseDmg;
            }
            else
            {
                dmg = Mathf.FloorToInt(baseDmg * mul + 0.5f);
            }
            // 坚固贪婪：每持有 4 个物品，持有者造成的伤害降低 0.75%（骨钉/技艺属于持有者伤害）
            return Mathf.Max(1, Mathf.FloorToInt(dmg * GreedDamageMultiplier() + 0.5f));
        }

        /// <summary>
        /// 坚固贪婪：每持有 4 个物品，小骑士造成的骨钉、法术、骨钉技艺伤害降低 0.75%。
        /// 召唤物（编织者之歌、格林之子等）不经过此削减。
        /// </summary>
        public static float GreedDamageMultiplier()
        {
            int stacks = GreedStacks();
            if (stacks <= 0)
            {
                return 1f;
            }
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                ItemStorage st = nM2D != null && nM2D.IMNG != null
                    ? nM2D.IMNG.getInventory()
                    : null;
                if (st == null)
                {
                    return 1f;
                }
                int groups = st.getVisibleRowCount(false) / 4; // 每 4 个被填充的格子一组
                // 小骑士的伤害惩罚最多 50%（下限 0.5）——需求：2026-09-22 用户确认
                return Mathf.Max(0.5f, 1f - groups * 0.0075f * stacks);
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        /// <summary>法术扭曲者：法术灵魂消耗从 30 降为 24。</summary>
        public static int SpellSoulCost()
        {
            return IsEquipped(SpellTwisterId) ? 24 : 30;
        }

        /// <summary>普攻总时长/间隔：0.4s（快速劈砍 0.3s）。</summary>
        public static float SlashAttackTime()
        {
            return IsEquipped(FastSlashId) ? SlashTimeQuick : SlashTimeBase;
        }

        /// <summary>快速劈砍：攻击动画播放速度倍率（0.4/0.3 ≈ 1.333）。</summary>
        public static float SlashAnimSpeedMultiplier()
        {
            return IsEquipped(FastSlashId) ? (SlashTimeBase / SlashTimeQuick) : 1f;
        }

        /// <summary>修长之钉 / 骄傲印记：佩戴任意一个时，骨钉剑气渲染换成螳螂爪样式
        /// （mantis_slash_left / mantis_up_slash / mantis_down_slash 帧）。</summary>
        public static bool LongNailVisual()
        {
            return IsEquipped(LongNailId) || IsEquipped(PrideId);
        }

        /// <summary>长钉/骄傲印记：平砍判定/渲染长度倍率
        /// （单戴 1.15 / 1.25，同戴 1.40）。</summary>
        public static float LongRangeMultiplier()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 1.40f;
            }
            if (a)
            {
                return 1.15f;
            }
            return b ? 1.25f : 1f;
        }

        /// <summary>长钉/骄傲印记：平砍判定/渲染高度倍率
        /// （单戴 1.10 / 1.15，同戴 1.20）。</summary>
        public static float LongRangeHeightMultiplier()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 1.20f;
            }
            if (a)
            {
                return 1.10f;
            }
            return b ? 1.15f : 1f;
        }

        /// <summary>长钉/骄傲印记：平砍判定/渲染向面朝方向的平移量
        /// 单位：格（= 坐标轴刻度 = 地图格）。调用处需经 KnightEntity.CellToUx 换算成 ux。
        /// 绿框三轮微调后（面朝左为例，最后一轮向右平移 0.1/0/0.1）：
        /// 单戴 0.0875 / 0.3125，同戴 0.5（正值 = 向攻击方向外伸）。</summary>
        public static float LongRangeShift()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 0.5f;
            }
            if (a)
            {
                return 0.0875f;
            }
            return b ? 0.3125f : 0f;
        }

        /// <summary>长钉/骄傲印记：平砍判定/渲染“只向身体方向拉伸”的量（格）。
        /// 仅拉骑士侧的边缘（右拉），远侧边缘不动；中心随之移动拉伸量的一半。
        /// 单戴：长钉 0 / 骄傲 0.2；同戴 0.1。</summary>
        public static float LongRangeStretch()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 0.1f;
            }
            if (a)
            {
                return 0f;
            }
            return b ? 0.2f : 0f;
        }

        /// <summary>长钉/骄傲印记：上劈判定/渲染高度向上拉伸量（格）。
        /// 单戴 0.3 / 0.5，同戴 0.8。</summary>
        public static float UpSlashStretchUp()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 0.8f;
            }
            if (a)
            {
                return 0.3f;
            }
            return b ? 0.5f : 0f;
        }

        /// <summary>长钉/骄傲印记：下劈判定/渲染高度向下拉伸量（格）。
        /// 单戴 0.2 / 0.3，同戴 0.5。</summary>
        public static float DownSlashStretchDown()
        {
            bool a = IsEquipped(LongNailId);
            bool b = IsEquipped(PrideId);
            if (a && b)
            {
                return 0.5f;
            }
            if (a)
            {
                return 0.2f;
            }
            return b ? 0.3f : 0f;
        }

        /// <summary>
        /// 沉重之击（普攻专属）：
        /// - 普通敌人：普攻 8% 概率直接击杀并播放斩杀音效；
        /// - BOSS（森之领主/山蜘蛛）：普攻 10% 概率额外造成其生命上限 1% 的伤害。
        /// 通过改写本次攻击数据（Atk）实现：斩杀时把伤害抬到剩余血量，追加时加上限百分比。
        /// </summary>
        public static void ProcHeavyBlow(NelEnemy enemy, NelAttackInfo Atk)
        {
            if (!IsKnightMode || !IsEquipped(HeavyBlowId) || enemy == null || Atk == null)
            {
                return;
            }
            try
            {
                bool isBoss = enemy is NelNBoss_Nusi || enemy is NelNBossSpider;
                if (isBoss)
                {
                    // BOSS：10% 概率额外 1% 生命上限伤害
                    int maxHp = EnemyMaxHpField != null
                        ? (int)EnemyMaxHpField.GetValue(enemy)
                        : 0;
                    if (maxHp <= 0 || X.XORSP() >= 0.10f)
                    {
                        return;
                    }
                    int extra = Mathf.FloorToInt(maxHp * 0.01f + 0.5f);
                    if (extra > 0)
                    {
                        Atk.hpdmg_current += extra;
                        Atk.hpdmg0 += extra;
                    }
                    return;
                }
                // 普通敌人：8% 概率直接击杀
                int curHp = EnemyHpField != null
                    ? (int)EnemyHpField.GetValue(enemy)
                    : 0;
                if (curHp > 0 && X.XORSP() < 0.08f)
                {
                    Atk.hpdmg_current = curHp;
                    Atk.hpdmg0 = curHp;
                    DashAudio.PlayHeavyKill(); // 斩杀音效
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>当前佩戴状态下实际生效的扩容值（兼容旧版 +100 存档）：按 row_max 反推。</summary>
        private static int AppliedGreedBonus(int rowMax)
        {
            int bonus = GreedSlotBonus * Mathf.Max(1, GreedStacks());
            int oldBonus = OldGreedSlotBonus * Mathf.Max(1, GreedStacks());
            return rowMax >= bonus ? bonus
                : rowMax >= oldBonus ? oldBonus : 0;
        }

        /// <summary>
        /// 坚固贪婪：背包上限随装配/卸下同步 ±GreedSlotBonus（当前 +150）。
        /// 基础容量始终从“当前 row_max”反推（已佩戴 → 含扩容加成；未佩戴 → 纯基础容量），
        /// 兼容游戏内工作台升级背包（increaseCapacity 直接改 row_max）——升级后无论是否佩戴，
        /// 下一次同步都会自动把新基础容量计算正确，不再使用首次装配时的旧快照。
        /// </summary>
        public static void SyncGreedCapacity()
        {
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                ItemStorage st = nM2D != null && nM2D.IMNG != null
                    ? nM2D.IMNG.getInventory()
                    : null;
                if (st == null)
                {
                    return;
                }
                int stacks = GreedStacks();
                // 幂等重算：按"上次写入时生效的层数"从当前 row_max 反推基础容量。
                // 这样无论哪一边的护符变化、层数怎么变，结果都一致；
                // 游戏内工作台升级背包（increaseCapacity 直接改 row_max）也会被算进基础容量。
                int lastStacks = COOK.getSF(GreedStackKey);
                if (lastStacks > 2)
                {
                    lastStacks = 2;
                }
                int baseCap = st.row_max - GreedSlotBonus * lastStacks;
                if (baseCap < 0)
                {
                    baseCap = st.row_max; // 异常兜底：当前容量就当基础
                }
                int want = baseCap + GreedSlotBonus * stacks;
                if (st.row_max != want)
                {
                    st.increaseCapacity(want - st.row_max);
                }
                COOK.setSF(GreedBaseKey, baseCap);
                COOK.setSF(GreedStackKey, stacks);
                _greedCapacityApplied = stacks > 0;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>坚固贪婪：能否卸下（背包占用数不得超过基础容量）。</summary>
        public static bool CanUnequipGreed()
        {
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                ItemStorage st = nM2D != null && nM2D.IMNG != null
                    ? nM2D.IMNG.getInventory()
                    : null;
                if (st == null)
                {
                    return true;
                }
                // 基础容量优先按当前状态反推（已佩戴 → 当前容量含扩容加成），
                // 保证升级背包后“占用了扩容格”的判定用的是最新基础容量。
                int baseCap = _greedCapacityApplied
                    ? st.row_max - AppliedGreedBonus(st.row_max) : st.row_max;
                if (baseCap < 0)
                {
                    baseCap = COOK.getSF(GreedBaseKey);
                }
                if (baseCap < 0)
                {
                    return true; // 推定失败（旧版 +8 存档）：允许卸下，避免卡死
                }
                return st.getVisibleRowCount(false) <= baseCap;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 读档后调用：把背包容量修正到与佩戴状态一致（佩戴=基础+GreedSlotBonus，未佩戴=基础）。
        /// 基础容量一律从当前容量反推：佩戴 → 减去已生效加成（兼容旧版 +100 → 自动迁移到 +150）；
        /// 未佩戴 → 就是当前容量。
        /// 这样即使背包容量在佩戴状态下被工作台升级过，读档后也不会被旧快照改回去。
        /// </summary>
        public static void FixGreedCapacityAfterLoad()
        {
            // 读档后与"佩戴状态变化"是同一件事：统一走幂等的 SyncGreedCapacity
            //（它会按 SF 里记的"上次层数"反推基础容量，再套用当前层数）。
            SyncGreedCapacity();
        }

        /// <summary>坚固贪婪：击杀魔物获得 5% 最大生命值的金币（四舍五入取整）。</summary>
        private static void EnemyDiePostfix(NelEnemy __instance)
        {
            if (GreedStacks() <= 0 || __instance == null)
            {
                return;
            }
            if (!_greedGoldGranted.Add(__instance))
            {
                return;
            }
            if (_greedGoldGranted.Count > 128)
            {
                _greedGoldGranted.Clear();
            }
            try
            {
                int maxHp = 0;
                if (EnemyMaxHpField != null)
                {
                    maxHp = (int)EnemyMaxHpField.GetValue(__instance);
                }
                if (maxHp <= 0)
                {
                    return;
                }
                int gold = Mathf.FloorToInt(maxHp * 0.05f + 0.5f); // 5% 四舍五入取整
                if (gold > 0)
                {
                    CoinStorage.addCount(gold, CoinStorage.CTYPE.GOLD, true);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>骨钉技艺击杀：小骑士获得 10 灵魂（不限于已入战，也不受束缚·骨钉影响）。</summary>
        private static void NailArtKillPostfix(NelEnemy __instance)
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k != null && k.ConsumeNailArtKill(__instance))
                {
                    k.KnightAddSoul(10);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 坚固贪婪：配置了特殊掉落物品的魔物被击杀时，绝对以 ~25% 概率掉落
        /// 特殊物品（不再依赖基础掉率/是否处于边界战斗）。星级提升走
        /// GetItemDropGradePostfix（每颗星 50%）。
        /// </summary>
        private static bool CheckDropChancePrefix(NelEnemy __instance)
        {
            if (GreedStacks() <= 0 || __instance == null)
            {
                return true;
            }
            try
            {
                NelItem dropItem = null;
                if (EnemyDropItemField != null)
                {
                    dropItem = (NelItem)EnemyDropItemField.GetValue(__instance);
                }
                if (dropItem == null && __instance.isOverDrive() &&
                    EnemyOdField != null && OdDropItemOdField != null)
                {
                    object od = EnemyOdField.GetValue(__instance);
                    if (od != null)
                    {
                        dropItem = (NelItem)OdDropItemOdField.GetValue(od);
                    }
                }
                if (dropItem == null)
                {
                    return true; // 未配置特殊掉落：交给原逻辑（原逻辑同样不会掉落）
                }
                // 绝对 ~25% 掉落：跳过原判定，直接执行特殊掉落（含星级提升）
                if (X.XORSP() < 0.25f && ExecuteDropItemMethod != null)
                {
                    object[] args = { dropItem };
                    ExecuteDropItemMethod.Invoke(__instance, args);
                }
                return false; // 已处理，不再走原掉率判定
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 坚固贪婪：掉落的特殊物品星级逐颗 50% 概率提升
        /// （1星 → 2星 50%，→ 3星 25%，依此类推；上限 GRADE_MAX=5）。
        /// </summary>
        private static void GetItemDropGradePostfix(NelEnemy __instance, ref int __result)
        {
            int stacks = GreedStacks();
            if (stacks <= 0 || __instance == null)
            {
                return;
            }
            int grade = __result;
            for (int s = 0; s < stacks; s++)   // 多层：每层都按同样规则升一次星
            {
                while (grade < NelItem.GRADE_MAX && X.XORSP() < 0.5f)
                {
                    grade++;
                }
            }
            __result = grade;
        }

        /// <summary>房间切换时更新蜂巢标记（含 honey 的 key 视为蜂巢房间）。</summary>
        public static void UpdateHiveRoom(Map2d mp)
        {
            CurrentRoomIsHive = mp != null && mp.key != null &&
                mp.key.IndexOf("honey", StringComparison.OrdinalIgnoreCase) >= 0;
            _hiveAggroTriggered = false; // 换房后重新中立
        }

        /// <summary>
        /// 小骑士攻击蜂巢房间内的魔物：全房魔物进入攻击状态（此后不再中立、正常攻击小骑士）。
        /// </summary>
        public static void TriggerHiveAggro()
        {
            if (!HiveNeutralActive())
            {
                return;
            }
            _hiveAggroTriggered = true;
            try
            {
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                if (noel == null || noel.Mp == null)
                {
                    return;
                }
                Map2d mp = noel.Mp;
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (mp.getMv(i) is NelEnemy en && en.getAI() != null)
                    {
                        en.getAI().awakeInit(noel);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>是否处于战斗（AIC 召唤区域激活）。</summary>
        public static bool IsInBattle()
        {
            return EnemySummoner.ActiveScript != null && EnemySummoner.isActiveBorder();
        }

        /// <summary>
        /// 深度聚集：开启宝箱时，掉落转轮（Reel）速度降低 75%。
        /// 真正的轮转速度是 ReelExecuter.reel_speed（由 fineSpeed 赋值），
        /// 这里在赋值后保留其 25%。
        /// </summary>
        private static void ReelExecuterFineSpeedPostfix(ReelExecuter __instance)
        {
            if (!IsKnightMode || !IsEquipped(DeepGatherId))
            {
                return;
            }
            try
            {
                FieldInfo f = AccessTools.Field(typeof(ReelExecuter), "reel_speed");
                if (f != null)
                {
                    float v = (float)f.GetValue(__instance);
                    f.SetValue(__instance, v * 0.25f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 坚固贪婪：击杀敌人掉落宝箱时，再掉落一个完全相同的宝箱。
        /// dropMBoxReel 是击杀掉落宝箱的唯一入口（整批掉落与特殊宝箱共用），
        /// 用防递归标记避免复制时再次触发本补丁。
        /// </summary>
        private static void DropMBoxReelPostfix(NelItemManager __instance, ReelManager.ItemReelDrop Reel,
            float mapx, float mapy, float vx, float vy)
        {
            if (_greedReelDupGuard || GreedStacks() <= 0)
            {
                return;
            }
            try
            {
                _greedReelDupGuard = true;
                __instance.dropMBoxReel(Reel, mapx, mapy, vx, vy);
            }
            catch (Exception)
            {
            }
            finally
            {
                _greedReelDupGuard = false;
            }
        }

        public static void Apply(Harmony harmony)
        {
            // 坚固贪婪：击杀敌人获得宝箱时，再掉落一个完全相同的宝箱
            try
            {
                MethodInfo dropMBox = AccessTools.Method(typeof(NelItemManager), "dropMBoxReel",
                    new[] { typeof(ReelManager.ItemReelDrop), typeof(float), typeof(float), typeof(float), typeof(float) });
                if (dropMBox != null)
                {
                    harmony.Patch(dropMBox, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(DropMBoxReelPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
            catch (Exception)
            {
            }
            // 深度聚集：开启宝箱时，掉落转轮（Reel）速度降低 75%
            try
            {
                MethodInfo fineSpeed = AccessTools.Method(typeof(ReelExecuter), "fineSpeed",
                    new[] { typeof(float) });
                if (fineSpeed != null)
                {
                    harmony.Patch(fineSpeed, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(ReelExecuterFineSpeedPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
            catch (Exception)
            {
            }
            // 指南针：小骑士模式 + 已装备时，地图快速旅行无需坐长椅。
            // 每个补丁独立挂载并记录失败日志：任一补丁挂不上只会影响自身，
            // 不会像以前那样一个异常就让整个指南针静默失效（小部分玩家环境差异的常见原因）。
            PatchCompass(harmony);
            try
            {
                // 护符2 蜂群集结：魔力草掉落的魔力魔物无法吸收（只能由诺艾尔吸收）
                MethodInfo splashMana = AccessTools.Method(typeof(M2ManaWeed), "SplashMana",
                    new[] { typeof(MANA_HIT), typeof(float), typeof(float) });
                if (splashMana != null)
                {
                    harmony.Patch(splashMana, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(ManaWeedSplashPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符2 蜂群集结：蜂巢房间内魔物不苏醒（含进入战斗后）
                MethodInfo naiAwake = AccessTools.Method(typeof(NAI), "awakeInit",
                    new[] { typeof(M2Attackable) });
                if (naiAwake != null)
                {
                    harmony.Patch(naiAwake, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(NaiAwakeInitPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符2 蜂群集结：中立期禁止魔物锁定目标
                MethodInfo naiAimSet = AccessTools.PropertySetter(typeof(NAI), "AimPr");
                if (naiAimSet != null)
                {
                    harmony.Patch(naiAimSet, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(NaiAimPrSetPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符2 蜂群集结：小骑士攻击蜂巢房间魔物 → 全房魔物进入攻击状态
                MethodInfo enemyDmg = AccessTools.Method(typeof(NelEnemy), "applyDamage",
                    new[] { typeof(NelAttackInfo), typeof(bool) });
                if (enemyDmg != null)
                {
                    harmony.Patch(enemyDmg, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(EnemyApplyDamagePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符3 坚硬外壳（诺艾尔专属）：次数血 + 掉血后 2 秒免掉。
                // 与 CombatGuard 的 HpDamagePrefix 挂在同一个方法上互不冲突：
                // 那个只在骑士模式拦截（返回 false），诺艾尔模式下返回 true 让这里生效。
                MethodInfo sturdyDmg = AccessTools.Method(typeof(M2Attackable), "applyHpDamage",
                    new[] { typeof(int), typeof(bool), typeof(AttackInfo) });
                if (sturdyDmg != null)
                {
                    harmony.Patch(sturdyDmg, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(SturdyHpDamagePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符4 灵魂捕手（诺艾尔侧）：法术命中敌人 → 回 6 MP。
                // 挂 MGContainer.CircleCast（非虚的"法术命中"汇聚点）的 postfix：
                // 法术伤害走的是 applyDamage 的 3 参重载，而敌人子类普遍 override 了它，
                // 挂基类虚方法不会被调用。
                MethodInfo circleCast = AccessTools.Method(typeof(MGContainer), "CircleCast");
                if (circleCast != null)
                {
                    harmony.Patch(circleCast, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(SoulCharmCircleCastPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    // 护符7 冲刺大师（诺艾尔侧）：强制"始终跑步"（速度与姿势共用这个判据）
                    MethodInfo isRunning = AccessTools.Method(typeof(M2MoverPr), "isRunning");
                    if (isRunning != null)
                    {
                        harmony.Patch(isRunning, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(DashmasterIsRunningPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符7 冲刺大师（诺艾尔侧）：松开方向键立刻停（去掉急停滑行）
                    MethodInfo calcWalk = AccessTools.Method(typeof(M2MoverPr), "calcWalkSpeed");
                    if (calcWalk != null)
                    {
                        harmony.Patch(calcWalk, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(DashmasterCalcWalkSpeedPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符9 幼虫之歌（诺艾尔侧）：诺艾尔不会被虫墙/虫巢抓取
                    MethodInfo canPullByWorm = AccessTools.Method(typeof(PR), "canPullByWorm");
                    if (canPullByWorm != null)
                    {
                        harmony.Patch(canPullByWorm, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(GrubsongCanPullByWormPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符10 蜕变挽歌（诺艾尔侧）：轻攻击（PR_PUNCH）时发射剑气
                    MethodInfo smallAttack = AccessTools.Method(typeof(M2PrSkill), "executeSmallAttack");
                    if (smallAttack != null)
                    {
                        harmony.Patch(smallAttack, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(ElegyExecuteSmallAttackPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        // 护符16 沉重之击（诺艾尔侧）：挥击/骨钉技艺的"出手"登记
                        harmony.Patch(smallAttack, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(HeavyBlowSmallAttackPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符18 修长之钉（诺艾尔侧）：手杖 reach（判定长度 + 挥击特效长度）统一 ×1.2
                    MethodInfo reachRatio = AccessTools.Method(typeof(PrCaneEquip), "reach_ratio");
                    if (reachRatio != null)
                    {
                        harmony.Patch(reachRatio, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(LongNailReachRatioPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符18 修长之钉（诺艾尔侧）：把这一招的"总触及距离"精确改成 基准 ×1.2
                    MethodInfo caneAwaken = AccessTools.Method(typeof(PrCaneEquip), "initChantMagicAwaken",
                        new[] { typeof(MagicItem), typeof(float) });
                    if (caneAwaken != null)
                    {
                        harmony.Patch(caneAwaken,
                            prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(LongNailCaneAwakenPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(LongNailCaneAwakenPostfix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符14 法术扭曲者（诺艾尔侧）：施法起手消耗 -10
                    MethodInfo burstMp = AccessTools.Method(typeof(PR), "applyBurstMpDamage");
                    if (burstMp != null)
                    {
                        harmony.Patch(burstMp, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(SpellTwisterBurstMpPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符14 法术扭曲者（续）：蓄力消耗同样 -10
                    MethodInfo holdMp = AccessTools.Method(typeof(M2PrSkill), "digestShotgunHoldMp");
                    if (holdMp != null)
                    {
                        harmony.Patch(holdMp,
                            prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterHoldScopePrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterHoldScopePostfix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符14 法术扭曲者（续2）：咏唱施放/被打断取消时的扣魔标记
                    MethodInfo explodeMg = AccessTools.Method(typeof(M2PrSkill), "explodeMagic");
                    if (explodeMg != null)
                    {
                        harmony.Patch(explodeMg,
                            prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterCastScopePrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterCastScopePostfix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        // 护符16 沉重之击（诺艾尔侧）：法术的"出手"登记
                        harmony.Patch(explodeMg, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(HeavyBlowExplodeMagicPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    MethodInfo killHold = AccessTools.Method(typeof(M2PrSkill), "killHoldMagic",
                        new[] { typeof(MANA_HIT), typeof(bool), typeof(bool) });
                    if (killHold != null)
                    {
                        harmony.Patch(killHold,
                            prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterHoldKillPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(SpellTwisterHoldKillPostfix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符14 法术扭曲者（续3）：施法扣魔的"直接减免"统一在这里做
                    // （咏唱施放/被打断/霰弹蓄力共用的重载）
                    MethodInfo prMpDamage = AccessTools.FirstMethod(typeof(PR),
                        m => m.Name == "applyMpDamage" && m.GetParameters().Length == 5);
                    if (prMpDamage != null)
                    {
                        harmony.Patch(prMpDamage, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(SpellTwisterMpDamagePrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符14 法术扭曲者（续4）：咏唱中的"待扣魔力预算"也减 10
                    // （魔力条暗色蓄力段 = getHoldingMp(false)，见 UIStatus.cs:743）
                    MethodInfo holdingMp = AccessTools.Method(typeof(M2PrSkill), "getHoldingMp");
                    if (holdingMp != null)
                    {
                        harmony.Patch(holdingMp, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(SpellTwisterHoldingMpPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符5 萨满之石（诺艾尔侧）：法术最终伤害 +25%（前缀抬高、后缀还原）
                    harmony.Patch(circleCast,
                        prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(ShamanCircleCastPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(ShamanCircleCastPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    // 护符16 沉重之击（诺艾尔侧）：在同一个命中汇聚点上统计连击（命中/未命中）
                    harmony.Patch(circleCast, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(HeavyBlowCircleCastPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    // 临时诊断（修长之钉）：记录近战攻击包的判定几何
                    harmony.Patch(circleCast, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(LongNailDiagCircleCastPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符15 稳定之体（诺艾尔侧）：风力（等级 + 推力两个入口）
                // 护符17 快速劈砍（诺艾尔侧）：挥杖速度 +50%
                // 挂 PR.baseTS（状态计时与动画播放共用它），仅挥击/技艺状态生效
                MethodInfo prBaseTs = AccessTools.PropertyGetter(typeof(PR), "baseTS");
                if (prBaseTs != null)
                {
                    harmony.Patch(prBaseTs, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(FastSlashBaseTsPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo windLevel = AccessTools.Method(typeof(PR), "getWindApplyLevel");
                if (windLevel != null)
                {
                    harmony.Patch(windLevel, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyWindLevelPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo windFoc = AccessTools.Method(typeof(PR), "applyWindFoc");
                if (windFoc != null)
                {
                    harmony.Patch(windFoc, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyWindFocPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符15 稳定之体（诺艾尔侧）：黏滑地面（冰面）不打滑
                MethodInfo addOnIce = AccessTools.Method(typeof(M2Phys), "addOnIce",
                    new[] { typeof(bool), typeof(float) });
                if (addOnIce != null)
                {
                    harmony.Patch(addOnIce, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyAddOnIcePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符15 稳定之体（诺艾尔侧）：触碰魔物（TACKLE 接触伤害）不摔倒
                MethodInfo prDmg = AccessTools.FirstMethod(typeof(M2PrADmg),
                    m => m.Name == "applyDamage" && m.GetParameters().Length == 6);
                if (prDmg != null)
                {
                    harmony.Patch(prDmg, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyContactDamagePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo prChangeState = AccessTools.Method(typeof(PR), "changeState",
                    new[] { typeof(PR.STATE) });
                if (prChangeState != null)
                {
                    harmony.Patch(prChangeState, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyChangeStatePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符15 稳定之体（诺艾尔侧）：走路/跑动撞到魔物不摔倒（屏蔽 RCenemy_sink 累加）
                MethodInfo moveByHitCheck = AccessTools.Method(typeof(PR), "moveByHitCheck");
                if (moveByHitCheck != null)
                {
                    harmony.Patch(moveByHitCheck,
                        prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(StableBodyMoveHitPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(StableBodyMoveHitPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo addEnemySink = AccessTools.Method(typeof(PR), "addEnemySink");
                if (addEnemySink != null)
                {
                    harmony.Patch(addEnemySink, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(StableBodyEnemySinkPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符12 坚固贪婪：击杀魔物掉落 5% 最大生命值的金币
                MethodInfo enemyDie = AccessTools.Method(typeof(NelEnemy), "changeStateToDie");
                if (enemyDie != null)
                {
                    harmony.Patch(enemyDie, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(EnemyDiePostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 骨钉技艺击杀：小骑士获得 10 灵魂（被骨钉技艺命中的敌人死亡时）
                if (enemyDie != null)
                {
                    harmony.Patch(enemyDie, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(NailArtKillPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符12 坚固贪婪：特殊物品掉率 +25%（掷点缩小）
                MethodInfo dropChance = AccessTools.Method(typeof(NelEnemy), "checkDropChance");
                if (dropChance != null)
                {
                    harmony.Patch(dropChance, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(CheckDropChancePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符12 坚固贪婪：特殊物品星级逐颗 50% 提升
                MethodInfo dropGrade = AccessTools.Method(typeof(NelEnemy), "getItemDropGrade");
                if (dropGrade != null)
                {
                    harmony.Patch(dropGrade, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(GetItemDropGradePostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>指南针补丁逐一挂载；任一补丁失败只影响自身并写警告日志。</summary>
        private static void PatchCompass(Harmony harmony)
        {
            // M 键走 OPEN_MAP → GM.activateMap()（不经 activate()），两个入口都挂
            TryPatchCompass(harmony, "UiGameMenu.activate",
                AccessTools.Method(typeof(UiGameMenu), "activate"),
                null,
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(FastTravelPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic)));
            TryPatchCompass(harmony, "UiGameMenu.activateMap",
                AccessTools.Method(typeof(UiGameMenu), "activateMap"),
                null,
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(FastTravelPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic)));

            // 地图界面出现时强制进入快速旅行模式（长椅图标可直接选中）
            Type mapType = AccessTools.TypeByName("nel.gm.UiGMCMap");
            TryPatchCompass(harmony, "UiGMCMap.initAppearMain",
                mapType != null ? AccessTools.Method(mapType, "initAppearMain") : null,
                null,
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(MapAppearPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic)));
            // 地图编辑中按下确认时：若光标附近有可传送图标而快速旅行模式被关闭
            // （切世界地图再回来、误触快速旅行开关等），立即重新打开，保证点图标必能传送。
            // 在空地提交仍走原标记选择，不影响放置标记。
            TryPatchCompass(harmony, "UiGMCMap.runEdit",
                mapType != null ? AccessTools.Method(mapType, "runEdit",
                    new[] { typeof(float), typeof(bool) }) : null,
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(CompassRunEditPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic)),
                null);
            // 确认传送时跳过“必须在椅子旁”的门控，直接执行
            TryPatchCompass(harmony, "UiGMCMap.executeFastTravelConfirm",
                mapType != null ? AccessTools.Method(mapType, "executeFastTravelConfirm") : null,
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(FastTravelConfirmPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic)),
                null);

            // 快速旅行磁吸列表也加入战斗区域（ENEMY 图标）
            Type wmSkinType = AccessTools.TypeByName("nel.ButtonSkinWholeMapArea");
            if (wmSkinType != null)
            {
                PropertyInfo fta = AccessTools.Property(wmSkinType, "fast_travel_active");
                TryPatchCompass(harmony, "ButtonSkinWholeMapArea.fast_travel_active.set",
                    fta != null ? fta.GetSetMethod() : null,
                    null,
                    new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(EnemyIconsPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                TryPatchCompass(harmony, "ButtonSkinWholeMapArea.setWholeMapTarget",
                    AccessTools.Method(wmSkinType, "setWholeMapTarget",
                        new[] { typeof(WholeMapItem), typeof(float), typeof(float) }),
                    null,
                    new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(EnemyIconsPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            }

            // 战斗区域传送收尾：AUTO_SAVE_BENCH 在无长椅处会报错
            // （近場にベンチがありません）。跳过“附近有长椅”检查，
            // 但仍执行自动存档与检查点更新，避免每次传送都生成错误报告。
            TryPatchCompass(harmony, "NelM2DEventListener.EvtRead",
                AccessTools.Method(typeof(NelM2DEventListener), "EvtRead"),
                new HarmonyMethod(typeof(CharmEffects).GetMethod(nameof(AutoSaveBenchPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic)),
                null);
        }

        private static void TryPatchCompass(Harmony harmony, string label, MethodBase method,
            HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                if (method == null)
                {
                    return;
                }
                harmony.Patch(method, prefix: prefix, postfix: postfix);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 指南针：地图编辑中按下确认（提交）前，若光标附近有可传送图标而快速旅行模式
        /// 处于关闭状态，则立即打开。这样即使误触了快速旅行开关 / 切图后模式被重置，
        /// 点长椅或战斗地点也一定走传送；空地处提交不改变原标记选择行为。
        /// </summary>
        private static bool CompassRunEditPrefix(object __instance)
        {
            if (!IsEquippedForCurrentPlayer(CompassId) || IN.isUiShiftO() || !IN.isSubmit())
            {
                return true;
            }
            try
            {
                object skin = AccessTools.Field(__instance.GetType(), "WmSkin")?.GetValue(__instance);
                if (skin == null)
                {
                    return true;
                }
                PropertyInfo pa = skin.GetType().GetProperty("fast_travel_active");
                if (pa == null || (bool)pa.GetValue(skin))
                {
                    return true;
                }
                if (TryGetTeleportTarget(skin, out _))
                {
                    pa.SetValue(skin, true);
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>
        /// 取当前可传送目标：优先用地图磁吸焦点（FastTravelFocused）；
        /// 磁吸未生效时（未按住等待 / 模式刚被关闭等），在当前区域光标附近
        /// 找最近的长椅 / 战斗区域图标兜底。仅限当前区域（WM == CurWM），
        /// 跨区域传送机制已移除，这里不放开。
        /// </summary>
        private static bool TryGetTeleportTarget(object skin, out WMIconPosition target)
        {
            target = default(WMIconPosition);
            try
            {
                if (skin == null)
                {
                    return false;
                }
                FieldInfo ff = AccessTools.Field(skin.GetType(), "FastTravelFocused");
                PropertyInfo fp = ff == null ? skin.GetType().GetProperty("FastTravelFocused") : null;
                object boxed = ff != null ? ff.GetValue(skin) : fp != null ? fp.GetValue(skin) : null;
                if (boxed is WMIconPosition focused && focused.valid)
                {
                    target = focused;
                    return true;
                }
                // 兜底：只在当前区域找图标（跨区域传送机制已移除）
                object wm = AccessTools.Field(skin.GetType(), "WM")?.GetValue(skin);
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (wm == null || nm2d == null || nm2d.WM == null || wm != nm2d.WM.CurWM)
                {
                    return false;
                }
                MethodInfo getCursor = skin.GetType().GetMethod("getCursorMapPos");
                MethodInfo getIcons = wm.GetType().GetMethod("getNoticedIconList",
                    new[] { typeof(WMIcon.TYPE) });
                if (getCursor == null || getIcons == null)
                {
                    return false;
                }
                object cur = getCursor.Invoke(skin, null);
                if (!(cur is Vector2 cursor))
                {
                    return false;
                }
                WMIconPosition best = default(WMIconPosition);
                float bestSq = 25f; // 兜底判定放宽到约 5 格（原版磁吸约 2 格），确认时按在图标附近即可传送
                foreach (WMIcon.TYPE type in new[] { WMIcon.TYPE.BENCH, WMIcon.TYPE.ENEMY })
                {
                    object list = getIcons.Invoke(wm, new object[] { type });
                    if (!(list is List<WMIconPosition> icons))
                    {
                        continue;
                    }
                    for (int i = 0; i < icons.Count; i++)
                    {
                        WMIconPosition pos = icons[i];
                        float dx = pos.wmx - cursor.x;
                        float dy = pos.wmy - cursor.y;
                        float sq = dx * dx + dy * dy;
                        if (sq <= bestSq)
                        {
                            bestSq = sq;
                            best = pos;
                        }
                    }
                }
                if (best.valid)
                {
                    target = best;
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static void EnemyIconsPostfix(object __instance)
        {
            if (!IsEquippedForCurrentPlayer(CompassId))
            {
                return;
            }
            try
            {
                Type t = __instance.GetType();
                object wm = AccessTools.Field(t, "WM")?.GetValue(__instance);
                object list = AccessTools.Field(t, "ABenchList")?.GetValue(__instance);
                if (wm == null || !(list is System.Collections.Generic.List<WMIconPosition> l))
                {
                    return;
                }
                MethodInfo getIcons = wm.GetType().GetMethod("getNoticedIconList",
                    new[] { typeof(WMIcon.TYPE) });
                if (getIcons == null)
                {
                    return;
                }
                object enemies = getIcons.Invoke(wm, new object[] { WMIcon.TYPE.ENEMY });
                if (enemies is System.Collections.Generic.List<WMIconPosition> el)
                {
                    bool added = false;
                    foreach (WMIconPosition p in el)
                    {
                        if (!l.Contains(p))
                        {
                            l.Add(p);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        AccessTools.Field(t, "ABenchList")?.SetValue(__instance, l);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool FastTravelConfirmPrefix(object __instance, ref bool __result)
        {
            if (!IsEquippedForCurrentPlayer(CompassId))
            {
                return true; // 走原逻辑
            }
            try
            {
                object skin = AccessTools.Field(__instance.GetType(), "WmSkin")?.GetValue(__instance);
                if (skin == null)
                {
                    return true;
                }
                if (TryGetTeleportTarget(skin, out WMIconPosition pos))
                {
                    WMIcon ico = pos.get_Icon();
                    if (ico != null && ico.type == WMIcon.TYPE.ENEMY)
                    {
                        // 战斗区域传送：目标=当前战斗区域时先终止战斗再传送；
                        // 异区域由 HandleBattleBeforeTeleport 终止战斗。
                        AutoSaveBenchSuppress = true;
                        if (IsInBattle() && IsSameBattleArea(pos))
                        {
                            CloseCurrentBattle();
                        }
                        else
                        {
                            HandleBattleBeforeTeleport(pos);
                        }
                        BattleAreaFastTravel = true;
                        ExecuteBattleAreaTransfer(pos);
                    }
                    else
                    {
                        AutoSaveBenchSuppress = false;
                        BattleAreaFastTravel = false;
                        HandleBattleBeforeTeleport(pos);
                        UiBenchMenu.ExecuteFastTravel(pos, null, null, null);
                    }
                    __result = true;
                    return false; // 跳过原方法（不再要求附近有椅子）
                }
                // 指南针已装备却找不到可传送目标（光标附近没有已发现图标等），
                // 与失败前一样静默回退到原逻辑，不产生任何日志。
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>终止当前战斗区域的战斗（与跨区传送时的处理一致：清敌但不判胜利）。</summary>
        private static void CloseCurrentBattle()
        {
            try
            {
                EnemySummoner active = EnemySummoner.ActiveScript;
                if (active != null)
                {
                    active.close(true, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// AUTO_SAVE_BENCH 替代处理：战斗区域传送收尾时目标地没有长椅，
        /// 原命令会报“近場にベンチがありません”。本前缀在目的地为战斗区域时
        /// 跳过长椅检查，但仍执行自动存档与检查点更新（与原命令一致）。
        /// </summary>
        private static bool AutoSaveBenchPrefix(NelM2DEventListener __instance, StringHolder rER, ref bool __result)
        {
            if (!AutoSaveBenchSuppress || rER == null || rER.cmd != "AUTO_SAVE_BENCH")
            {
                return true;
            }
            try
            {
                AutoSaveBenchSuppress = false; // 只消费一次
                NelM2DBase nM2D = __instance.nM2D;
                Map2d curMap = nM2D != null ? nM2D.curMap : null;
                if (curMap != null && curMap.Pr is PR pr)
                {
                    if (CFG.autosave_on_bench && SCN.canSave(true))
                    {
                        COOK.autoSave(nM2D, true, false);
                    }
                    BCCLine lastBCC = pr.getFootManager().get_LastBCC();
                    nM2D.CheckPoint.fineFoot(pr, lastBCC, true);
                }
                __result = true;
                return false;
            }
            catch (Exception)
            {
                return true; // 兜底走原逻辑（会报错但流程继续）
            }
        }

        /// <summary>
        /// 蜂群集结（**仅小骑士侧**）：破坏魔力草掉落的魔力直接给后台诺艾尔吸收（不生成落地魔力），
        /// 魔物始终拿不到。无论魔物还是小骑士破坏魔力草都生效；诺艾尔不可用时兜底走"仅诺艾尔可吸"的落地魔力。
        ///
        /// 金额与原生一致：`(20 + rand(0..10)) × 魔力草比例`（随夜间比例缩放）。
        /// **诺艾尔侧的蜂群集结不含这一项**（只保留"自动拾取 + 蜂巢怪不打"，2026-09-22 用户定）：
        /// 诺艾尔模式下破坏魔力草走原版——正常掉落、谁都能吸。
        /// </summary>
        private static bool ManaWeedSplashPrefix(M2ManaWeed __instance, ref MANA_HIT mana_hit,
            float cx, float cy)
        {
            if (!CollectorManaGuardActive())
            {
                return true;
            }
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                if (nM2D != null)
                {
                    float amount = (20f + (float)X.xors(11)) * nM2D.NightCon.ManaWeedRatio();
                    if (KnightInCradleBehaviour.GrantNoelMana(amount))
                    {
                        return false; // 跳过原生：直接给后台诺艾尔吸收，不生成落地魔力
                    }
                }
            }
            catch (Exception)
            {
            }
            // 兜底：诺艾尔不可用时仍走原生落地，但保持仅诺艾尔可吸
            mana_hit = (mana_hit & ~MANA_HIT.EN) | MANA_HIT.PR;
            return true;
        }

        /// <summary>是否为蚂蟥/女王蚂蟥一族（含连接体变体）。</summary>
        private static bool IsLeechFamily(NelEnemy en)
        {
            return en is NelNLeech || en is NelNLeechOdConnector ||
                   en is NelNLeechQueen || en is NelNLeechQueenConnector;
        }

        /// <summary>是否为蘑菇一族（蘑菇及其变体）。</summary>
        private static bool IsMushroomFamily(NelEnemy en)
        {
            return en is NelNMush;
        }

        /// <summary>
        /// 幼虫之歌 / 蜕变挽歌：蚂蟥/女王蚂蟥不攻击持有者（被攻击也不反击）。
        /// </summary>
        public static bool GrubsongLeechPassive(NelEnemy en)
        {
            return IsKnightMode &&
                (IsEquipped(GrubsongId) || IsEquipped(ElegyId)) && IsLeechFamily(en);
        }

        /// <summary>蘑菇孢子：蘑菇一族不攻击持有者（被攻击也不反击）。</summary>
        public static bool MushroomPassive(NelEnemy en)
        {
            return IsKnightMode && IsEquipped(MushroomId) && IsMushroomFamily(en);
        }

        /// <summary>
        /// 阻止魔物苏醒：蜂群集结中立期，或幼虫之歌对蚂蟥/女王蚂蟥生效。
        /// 小骑士攻击挑衅后（蜂巢房间）放行；蚂蟥一族则始终不苏醒。
        /// </summary>
        private static bool NaiAwakeInitPrefix(NAI __instance, M2Attackable _AimPr)
        {
            if (HiveNeutralActive())
            {
                // 例外：即将因雷雨变成"汚染体（OverDrive）"的魔物**必须先苏醒**才能转化，
                // 一直压着不苏醒会导致它永远不转化、也打不动（实测 bug）。
                if (WillThunderOverdrive(__instance.En))
                {
                    return true;
                }
                return false;
            }
            if (GrubsongLeechPassive(__instance.En))
            {
                return false;
            }
            if (MushroomPassive(__instance.En))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 该魔物是否"即将因雷雨天气变成汚染体（OverDrive）"。
        /// 转化在 `OverDriveManager.runPre` 里推进（`nel/OverDriveManager.cs:202-213`），
        /// 条件是 `thunder_overdrive_t &gt; 0 &amp;&amp; !disappearing &amp;&amp; En.is_awaken`
        /// —— 也就是说**必须先苏醒**。蜂巢中立把苏醒压住时，这类魔物既不转化也打不动，
        /// 因此对它们放行一次苏醒；转化后仍然保持中立（本模组照旧清掉它的锁定目标）。
        /// </summary>
        private static bool WillThunderOverdrive(NelEnemy en)
        {
            try
            {
                OverDriveManager od = en != null ? en.getOdManager() : null;
                return od != null && od.thunder_overdrive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 禁止魔物锁定目标（AimPr 赋值），允许清空（value==null）：
        /// 蜂群集结中立期 / 幼虫之歌对蚂蟥、女王蚂蟥生效。
        /// </summary>
        private static bool NaiAimPrSetPrefix(NAI __instance, M2Attackable value)
        {
            if (value == null)
            {
                return true; // 允许清空
            }
            if (HiveNeutralActive())
            {
                // 汚染体（雷雨 OverDrive）候选：**整体放行**，让它像普通魔物一样行动，
                // 否则它既不会转化、也无法被攻击（见 WillThunderOverdrive 的说明）。
                return WillThunderOverdrive(__instance.En);
            }
            if (GrubsongLeechPassive(__instance.En))
            {
                return false;
            }
            if (MushroomPassive(__instance.En))
            {
                return false;
            }
            return true;
        }

        /// <summary>中立期每帧清除蜂巢房间内魔物的锁定目标（AimPr=null）。</summary>
        public static void ClearHiveEnemyAim()
        {
            if (!HiveNeutralActive())
            {
                return;
            }
            try
            {
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                if (noel == null || noel.Mp == null)
                {
                    return;
                }
                Map2d mp = noel.Mp;
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (mp.getMv(i) is NelEnemy en)
                    {
                        if (WillThunderOverdrive(en))
                        {
                            continue; // 汚染体候选：不清它的锁定目标，让它正常行动并完成转化
                        }
                        NAI ai = en.getAI();
                        if (ai != null && ai.AimPr != null)
                        {
                            ai.AimPr = null; // set_AimPr 前缀放行 null → 清空目标
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>幼虫之歌：每帧清除蚂蟥/女王蚂蟥的锁定目标（确保始终中立）。</summary>
        public static void ClearGrubsongLeechAim()
        {
            if (!IsKnightMode || (!IsEquipped(GrubsongId) && !IsEquipped(ElegyId)))
            {
                return;
            }
            try
            {
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                if (noel == null || noel.Mp == null)
                {
                    return;
                }
                Map2d mp = noel.Mp;
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (mp.getMv(i) is NelEnemy en && IsLeechFamily(en))
                    {
                        NAI ai = en.getAI();
                        if (ai != null && ai.AimPr != null)
                        {
                            ai.AimPr = null; // set_AimPr 前缀放行 null → 清空目标
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static readonly FieldInfo RBaseAItemsField =
            AccessTools.Field(typeof(RBase<M2Mana>), "AItems");
        private static readonly FieldInfo RBaseLENField =
            AccessTools.Field(typeof(RBase<M2Mana>), "LEN");

        /// <summary>
        /// 蜂群集结：AIC 的落地魔力超时（约 5 秒）没人收会被自动改成“谁都能吸（ALL）”，
        /// 导致怪过一会儿仍能吸走。本方法在骑士模式下每帧检查，把含 PR 的魔力重新去掉 EN，
        /// 保持“只能由诺艾尔吸收”，直到切出诺艾尔收走。
        /// </summary>
        public static void ProtectCollectorMana()
        {
            if (!CollectorManaGuardActive())
            {
                return;
            }
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                if (nM2D == null || nM2D.Mana == null)
                {
                    return;
                }
                object items = RBaseAItemsField != null ? RBaseAItemsField.GetValue(nM2D.Mana) : null;
                if (!(items is M2Mana[] arr))
                {
                    return;
                }
                int len = RBaseLENField != null ? (int)RBaseLENField.GetValue(nM2D.Mana) : arr.Length;
                for (int i = 0; i < len; i++)
                {
                    M2Mana m = arr[i];
                    if (m == null || m.only_effect)
                    {
                        continue;
                    }
                    if ((m.mana_hit & MANA_HIT.PR) != MANA_HIT.NOUSE &&
                        (m.mana_hit & MANA_HIT.EN) != MANA_HIT.NOUSE)
                    {
                        // 超时被自动改成 ALL：重新去掉 EN，保持仅诺艾尔可吸
                        m.mana_hit = (m.mana_hit & ~MANA_HIT.EN) | MANA_HIT.PR;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ---- 护符2 蜂群集结：自动拾取掉落物 ----
        /// <summary>自动拾取半径（格）。</summary>
        public const float CollectorPickupRadius = 3f;

        // AIC 的掉落物表 / 存储区路由 / 拾取入口都不是 public，用反射取一次缓存住。
        private static readonly FieldInfo ImngODropField =
            AccessTools.Field(typeof(NelItemManager), "ODrop");
        private static readonly MethodInfo ImngGetStorageForMethod =
            AccessTools.Method(typeof(NelItemManager), "getStorageFor", new[] { typeof(NelItem) });
        private static readonly MethodInfo ImngExecutePickUpMethod =
            AccessTools.Method(typeof(NelItemManager), "executePickUp",
                new[] { typeof(NelItemManager.NelItemDrop) });

        /// <summary>
        /// 蜂群集结：骑士模式下每帧检查，把 3 格内**已经落地可拾取**、且对应存储区还放得下的
        /// 掉落物（史莱姆的假卵、剑山的刺…）直接交给游戏的拾取流程
        /// <c>NelItemManager.executePickUp</c>，因此拾取音效 / 粒子 / 背包路由
        /// （背包 / 贵重品 / 仓库 / 水壶）与手动按键拾取完全一致。
        ///
        /// 两个前置条件都沿用游戏自己的判据：
        /// ① <c>NelItemDrop.canTalkable(false) == 1</c> —— 刚掉出来还在弹跳的物品不会被瞬间吸走；
        /// ② <c>ItemStorage.getItemCapacity(...) &gt; 0</c> —— 没空位就不拾取（也不会弹原生的“装不下”提示）。
        /// 一帧最多拾取一件（游戏自带的 pickup_delay 还会再限流），拾取后立刻结束枚举，
        /// 避免边遍历边改写 ODrop。
        /// </summary>
        public static void TickCollectorAutoPickup()
        {
            KnightEntity k = KnightEntity.Instance;
            if (k == null || !k.IsActive || ImngODropField == null || ImngExecutePickUpMethod == null)
            {
                return;
            }
            TickCollectorAutoPickup(k.X, k.FootY);
        }

        /// <summary>
        /// 自动拾取的实现（按传入角色的坐标判定距离）。
        /// 小骑士模式由 <see cref="TickCollectorAutoPickup()"/> 传骑士坐标调用；
        /// 诺艾尔模式由 Behaviour 传诺艾尔坐标调用（第二部分新增）。
        /// </summary>
        public static void TickCollectorAutoPickup(float px, float footY)
        {
            // 注意：这里不能用 CollectorManaGuardActive()——那个只服务小骑士侧的"魔力保护"，
            // 自动拾取是**两个角色都有**的效果，因此按当前操控角色判断。
            if (!IsEquippedForCurrentPlayer(CollectorId))
            {
                return; // 当前操控角色没装备护符2
            }
            if (ImngODropField == null || ImngExecutePickUpMethod == null)
            {
                return;
            }
            // 剧情/转场事件期间不打扰（此时玩家输入本来也是被禁的）
            try
            {
                if (EV.isActive(false))
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                NelItemManager imng = nM2D != null ? nM2D.IMNG : null;
                if (imng == null)
                {
                    return;
                }
                // ODrop 是 Better.BDic<M2DropObject, NelItemDrop>：用非泛型 IDictionary 枚举，
                // 这样不必引用 better.dll。
                if (!(ImngODropField.GetValue(imng) is System.Collections.IDictionary drops))
                {
                    return;
                }
                float r2 = CollectorPickupRadius * CollectorPickupRadius;
                foreach (System.Collections.DictionaryEntry entry in drops)
                {
                    if (!(entry.Value is NelItemManager.NelItemDrop drop) || drop.destructed)
                    {
                        continue;
                    }
                    M2DropObject dro = drop.Dro;
                    if (dro == null)
                    {
                        continue;
                    }
                    float dx = dro.x - px;
                    float dy = dro.y - footY;
                    if (dx * dx + dy * dy > r2)
                    {
                        continue;
                    }
                    if (drop.canTalkable(false) != 1)
                    {
                        continue; // 尚未落地 / 游戏自己也不允许拾取
                    }
                    if (!HasRoomForDrop(imng, drop.Itm))
                    {
                        continue; // 放不下：不拾取，等玩家腾出空间后再说
                    }
                    ImngExecutePickUpMethod.Invoke(imng, new object[] { drop });
                    return; // 拾取会改动 ODrop，必须立刻结束枚举
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>掉落物对应的存储区（背包 / 贵重品 / 仓库…）里是否还放得下。</summary>
        private static bool HasRoomForDrop(NelItemManager imng, NelItem itm)
        {
            if (itm == null)
            {
                return false;
            }
            try
            {
                ItemStorage st = null;
                if (ImngGetStorageForMethod != null)
                {
                    st = ImngGetStorageForMethod.Invoke(imng, new object[] { itm }) as ItemStorage;
                }
                if (st == null)
                {
                    st = imng.getInventory(); // 兜底：反射失败时只看主背包
                }
                return st != null && st.getItemCapacity(itm, false, false) > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 蜂群集结：小骑士（以诺艾尔为 Caster）在蜂巢房间攻击魔物时，
        /// 触发全房魔物进入攻击状态。
        /// 蜂巢之血为“友好”：被小骑士攻击也不会敌对，因此不触发。
        /// </summary>
        private static void EnemyApplyDamagePrefix(NelEnemy __instance, NelAttackInfo Atk, bool force)
        {
            if (Atk != null && Atk.Caster is PRNoel)
            {
                // 只有蜂群集结会在攻击后全房敌对；蜂巢之血保持友好
                if (HiveNeutralActive() && IsEquippedForCurrentPlayer(CollectorId))
                {
                    TriggerHiveAggro();
                }
                // 护符16 沉重之击：只对普攻有效（普攻不挂 PublishMagic，
                // 法术/三剑技都会挂）：斩杀 / 上限百分比追加伤害
                if (Atk.PublishMagic == null)
                {
                    ProcHeavyBlow(__instance, Atk);
                }
                // 友好动物（鸡/牛）攻击掉落：仅小骑士模式
                if (IsKnightMode && KnightEntity.Instance != null && KnightEntity.Instance.IsActive)
                {
                    TryFarmAnimalDrop(__instance);
                }
            }
        }

        /// <summary>
        /// 友好动物攻击掉落：小骑士攻击“鸡”（NelNMgmFarmChicken）时 25% 获得“家禽蛋”
        /// （仅限 mount_caravan_entrance_left 房间）；攻击“牛”（NelNMgmFarmCow）时 25%
        /// 获得“魔族的肉”（任意房间）。星级随机 1~4。
        /// </summary>
        private static void TryFarmAnimalDrop(NelEnemy enemy)
        {
            try
            {
                if (enemy == null || enemy.destructed)
                {
                    return;
                }
                string itemKey = null;
                if (enemy is nel.mgm.farm.NelNMgmFarmChicken)
                {
                    Map2d mp = (M2DBase.Instance as NelM2DBase)?.curMap;
                    if (mp == null || mp.key != "mount_caravan_entrance_left")
                    {
                        return;
                    }
                    itemKey = "mtr_egg"; // 家禽蛋
                }
                else if (enemy is nel.mgm.farm.NelNMgmFarmCow)
                {
                    itemKey = "mtr_meat_demon0"; // 魔族的肉
                }
                else
                {
                    return;
                }
                if (UnityEngine.Random.value > 0.25f)
                {
                    return;
                }
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                if (nM2D == null || nM2D.IMNG == null)
                {
                    return;
                }
                NelItem itm = NelItem.GetById(itemKey, true);
                if (itm == null)
                {
                    return;
                }
                int grade = UnityEngine.Random.Range(1, 5); // 随机星级 1~4
                nM2D.IMNG.getItem(itm, 1, grade, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>解析战斗区域中心并传送（优先召唤者地块中心，避免落到图标浮空点）。</summary>
        private static void ExecuteBattleAreaTransfer(WMIconPosition pos)
        {
            BattleAreaFastTravel = true;
            // 图标位置即战斗区域 focus 中心（mapfocx/mapfocy，危险等级显示处）。
            // 统一走 ExecuteFastTravel 完整收尾（关菜单/黑屏/传送/骑士跟随）。
            UiBenchMenu.ExecuteFastTravel(pos, null, null, null);
        }

        /// <summary>
        /// 传送前处理战斗：若处于战斗且目标不在本战斗区域内，则先终止当前战斗。
        /// 目标在本战斗区域内（同地图 + 在召唤区域矩形内）则保留战斗。
        /// </summary>
        private static void HandleBattleBeforeTeleport(WMIconPosition pos)
        {
            try
            {
                EnemySummoner active = EnemySummoner.ActiveScript;
                M2LpSummon area = active != null ? active.getSummonedArea() : null;
                if (area != null && !IsSameBattleArea(pos))
                {
                    active.close(true, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>目标是否位于当前战斗区域（同地图 + 在召唤区域矩形内）。</summary>
        private static bool IsSameBattleArea(WMIconPosition pos)
        {
            try
            {
                if (!IsInBattle())
                {
                    return false;
                }
                EnemySummoner active = EnemySummoner.ActiveScript;
                M2LpSummon area = active != null ? active.getSummonedArea() : null;
                if (area == null)
                {
                    return false;
                }
                Map2d destMap = pos.getDepertureMap();
                Vector2 dest = pos.getDepertureMapPos();
                return destMap != null && active.Mp == destMap &&
                    dest.x >= area.mapx && dest.x < area.mapx + area.mapw &&
                    dest.y >= area.mapy && dest.y < area.mapy + area.maph;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void MapAppearPostfix(object __instance)
        {
            if (!IsEquippedForCurrentPlayer(CompassId))
            {
                return;
            }
            try
            {
                Type t = __instance.GetType();
                FieldInfo ctrF = AccessTools.Field(t, "WmCtr");
                FieldInfo skinF = AccessTools.Field(t, "WmSkin");
                object ctr = ctrF != null ? ctrF.GetValue(__instance) : null;
                object skin = skinF != null ? skinF.GetValue(__instance) : null;
                if (ctr != null)
                {
                    FieldInfo cc = AccessTools.Field(ctr.GetType(), "can_use_fasttravel");
                    if (cc != null)
                    {
                        cc.SetValue(ctr, true);
                    }
                }
                if (skin != null)
                {
                    PropertyInfo pa = skin.GetType().GetProperty("fast_travel_active");
                    if (pa != null)
                    {
                        pa.SetValue(skin, true);
                    }
                }
            }
            catch
            {
            }
        }

        private static void FastTravelPostfix(UiGameMenu __instance)
        {
            if (!IsEquippedForCurrentPlayer(CompassId))
            {
                return;
            }
            try
            {
                FieldInfo f = AccessTools.Field(typeof(UiGameMenu), "can_use_fasttravel");
                FieldInfo p = AccessTools.Field(typeof(UiGameMenu), "pr_on_bench");
                if (f != null)
                {
                    f.SetValue(__instance, true);
                }
                if (p != null)
                {
                    p.SetValue(__instance, true);
                }
            }
            catch
            {
            }
        }
    }
}
