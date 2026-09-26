using System;
using System.Collections.Generic;
using System.Reflection;
using evt;
using HarmonyLib;
using m2d;
using nel;
using nel.gm;
using PixelLiner;
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
                CharmData cdNoEquip = CharmDatabase.Get(id);
                if (cdNoEquip != null && cdNoEquip.NoEquip)
                {
                    return false; // 37/38/39/41：不可佩戴，效果自然也不生效
                }
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
        /// <summary>
        /// 坚硬外壳 + 乔尼的祝福**同时携带**时的单次受伤上限（2026-09-24 用户指定的组合规则）。
        /// </summary>
        public const int SturdyJoniDamageCap = 50;
        /// <summary>坚硬外壳 + 乔尼的祝福：受伤后"锁魔力池"的秒数（这段时间内不再受伤）。</summary>
        public const float SturdyJoniMpLockSeconds = 2f;
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
                // 护符3 + 护符30（2026-09-24 组合规则）：佩戴乔尼的祝福时，次数血**整体让位**——
                // 乔尼已经把血条并进魔力池（HP 条不再参与结算），再对 HP 上限 ÷50 只会平白把
                // 魔力池也缩掉（魔力上限 = 基础上限 + 生命上限），与组合效果"单次伤 ≤ 50"自相矛盾。
                // 因此乔尼生效期间这里把坚硬外壳当成"未佩戴"（走 Deactivate 还原真实 hp/maxhp），
                // 组合效果由 SturdyHpDamagePrefix 里的乔尼分支实现：单次 ≤ 50 + 受击 2 秒无敌。
                // 乔尼卸下后下一帧 want 恢复 true，外壳会自动重新生效（寄存值此时已是真实值）。
                bool want = IsEquipped(CharmOwner.Noel, SturdyId) && !JoniBlessingActive(pr);
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
                if (!IsPlayerMagicKind(Mg.kind) && !IsNoelShotgunFlavored(Mg))
                {
                    return; // 不消耗魔力的攻击（普攻/未被蓄力强化的技艺）不算魔法
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

        /// <summary>
        /// "魔法霰弹 / 霰弹变种"判据（萨满之石 / 灵魂捕手 / 噬魂者共用）：
        /// 诺艾尔**蓄力后释放的战斗挥击**——魔法霰弹本身，以及
        /// 旋风斩击 / 彗星俯冲 / 突进冲击 / 凌空横斩 / 会心重击 / 轮舞斩击
        /// 在魔法蓄力状态下释放的"霰弹变种"（`PR.STATE.*_SHOTGUN`）。
        ///
        /// 这些变种**招牌仍是自己的技艺 kind**（`M2PrSkill.cs:2610-2674`：WHEEL_SHOTGUN → PR_WHEEL、
        /// COMET_SHOTGUN → PR_COMET…），所以只看 kind 识别不出它们；但它们的伤害一定经过
        /// `MDAT.initShotGun` 结算，而后者会把 `MgShot.reduce_mp` 从 0 改成这次蓄力的耗魔
        /// （`MDAT.cs:1162`）——普通挥击的 `reduce_mp` 恒为 0（`MagicItem.init`，`:46`）,
        /// 而法术类的 `reduce_mp` 由 kind 表给出、不是 `NORMAL_ATTACK`，
        /// 因此 `is_normal_attack && reduce_mp > 0` 正好圈定"霰弹（含变种）"。
        /// 蓄力刚开始就释放时 `reduce_mp` 会被取整成 0，此时用"施法者仍在 `*_SHOTGUN` 状态"
        /// （`PR.isShotgunState()`，`PR.cs:6006/6019-6029`）兜底。
        /// </summary>
        private static bool IsNoelShotgunFlavored(MagicItem Mg)
        {
            try
            {
                if (Mg == null || !Mg.is_normal_attack)
                {
                    return false; // 法术不是 NORMAL_ATTACK（它已由 IsPlayerMagicKind 覆盖）
                }
                if (Mg.reduce_mp > 0f)
                {
                    return true;
                }
                PRNoel pr = Mg.Caster as PRNoel;
                return pr != null && pr.isShotgunState();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ================= 护符30 乔尼的祝福（诺艾尔侧） =================
        /// <summary>
        /// 护符30 乔尼的祝福（用户逐条给效果；当前已实现 2 / 4 / 5）：
        /// ・效果2：**魔力上限 = 基础上限 + 其它护符修正 + 当前生命上限**（把血条的量并进魔力池）；
        /// ・效果4：HP 的**扣血 / 回血**都改由**魔力池**结算——
        ///   扣血 → `applyMpDamage`（不扣 HP），回血（`PR.cureHp`）→ 改成回魔（`cureMp`）；
        ///   魔力本身的收支本来就记在这个池子上，于是"回血/扣血/回魔/扣魔"都算在魔力条里；
        /// ・效果5：HP 字段不再被伤害/治疗改动 → **HP 条不随之变化**。
        ///
        /// ・效果1（HP 条渲染颜色改成 MP 条的颜色）与效果3（HP 条下方数字显示 `???/???`）属 HUD 层：
        ///   AIC 的 HP/MP 条由屏幕 shader `Nel/UiBg`（按 `_HpRatio/_MpRatio` 画）渲染、颜色烘在 shader 里；
        ///   数字则由 `UIStatus.redrawBarNumber` 用位图字体拼成定长字符串。两者都没有现成钩子，
        ///   需要额外手段（见 docs 第 44 节，等用户选方案）。
        /// </summary>
        public static bool JoniBlessingActive(PRNoel pr)
        {
            return !IsKnightMode && pr != null && IsEquipped(CharmOwner.Noel, JohnnyId);
        }

        /// <summary>护符30 效果4：诺艾尔**回血 → 改成回魔**（HP 条不动）。</summary>
        private static bool JoniCureHpPrefix(PR __instance, int val)
        {
            try
            {
                if (val <= 0 || !(__instance is PRNoel))
                {
                    return true;
                }
                if (!JoniBlessingActive((PRNoel)__instance))
                {
                    return true;
                }
                __instance.cureMp(val); // 回血改记在魔力池上
                RefreshNoelHudMp();
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>护符30 效果4：诺艾尔**扣血 → 改成扣魔**（HP 条不动）。由受伤前缀调用。</summary>
        private static void JoniRedirectDamageToMp(PRNoel noel, int val)
        {
            try
            {
                noel.applyMpDamage(val, true, null, false, false);
            }
            catch (Exception)
            {
            }
            RefreshNoelHudMp();
            // 魔力池打空 = 死亡（AIC 的死亡读 hp，所以这里绕过"扣血改扣魔"直接走原版强制死亡）
            try
            {
                if (noel.get_mp() <= 0f)
                {
                    _joniDying = true;
                    try
                    {
                        noel.applyHpDamage(9999, true, null);
                    }
                    finally
                    {
                        _joniDying = false;
                    }
                }
            }
            catch (Exception)
            {
                _joniDying = false;
            }
        }

        /// <summary>true = 这次 HP 伤害是"魔力池打空后的强制死亡"，不要改写成扣魔。</summary>
        private static bool _joniDying;

        /// <summary>
        /// 护符3 + 护符30 组合：锁蓝（锁魔力池）截止时刻（`Time.time`，秒）。
        /// 组合生效期间，受伤后 2 秒内伤害一律作废。
        /// </summary>
        private static float _joniSturdyMpLockUntil;

        /// <summary>诺艾尔是否正坐在长椅上（AIC 原生 BENCH 系列状态）。</summary>
        private static bool IsNoelOnBench(PRNoel pr)
        {
            try
            {
                return pr != null && pr.isBenchState();
            }
            catch (Exception)
            {
                return false;
            }
        }
        /// <summary>UIStatus.MdGageT（HUD 上的 HP/MP 数字网格），用于精确识别 HP 数字的绘制调用。</summary>
        private static FieldInfo _uiMdGageTField;

        /// <summary>UIStatus.MdH（HP 条填充网格）——用来把填充段染成乔尼的蓝色。</summary>
        private static FieldInfo _joniMdHField;

        /// <summary>UIStatus.MdM（MP 条填充网格）——乔尼的祝福下两条同色（#46B2FF）。</summary>
        private static FieldInfo _joniMdMField;

        /// <summary>
        /// 护符30 效果1（正式做法）：诺艾尔佩戴乔尼的祝福时，把 HUD **HP 条的填充段染成 `#46B2FF`**。
        ///
        /// 和小骑士的血条染色是**同一套机制**：挂 `UIStatus.redrawAll` 的**后缀**
        /// ——它是血条/魔力条/数字的统一重绘入口（`CombatGuard.RedrawAllPostfix` 就是用它把
        /// 骑士血条染黑的）；这里直接改 `MdH` 网格前 4 个顶点（= 填充段）的颜色，
        /// 位置/长度仍是游戏自己算好的，所以不需要我们算矩形，也不受分辨率影响。
        /// 只染填充段、不动其余顶点（背景/虚血段保持原样），因此空条外观与原来一致。
        /// </summary>
        private static void JoniRedrawAllPostfix(UIStatus __instance)
        {
            try
            {
                if (__instance == null || IsKnightMode)
                {
                    return;
                }
                bool joni = IsEquipped(CharmOwner.Noel, JohnnyId);
                bool hive = IsEquipped(CharmOwner.Noel, HiveId);
                if (!joni && !hive)
                {
                    return;
                }
                if (_joniMdHField == null)
                {
                    _joniMdHField = AccessTools.Field(typeof(UIStatus), "MdH");
                }
                if (_joniMdMField == null)
                {
                    _joniMdMField = AccessTools.Field(typeof(UIStatus), "MdM");
                }
                if (joni)
                {
                    // 乔尼的祝福优先级最高：HP / MP 两条都染 #46B2FF
                    //（此时 MP 条本身就是血条，两条同色才是"一条池子"的观感）。
                    // 与原版一致：MP 为 0 时原版不画填充段（前 4 顶点是空条背景），
                    // 此时不能染色，否则会出现一条假的满格条，改为只把填充段置透明。
                    bool mpEmpty = true;
                    try
                    {
                        PRNoel prNow = KnightInCradleBehaviour.GetPrPublic();
                        mpEmpty = prNow == null || PrMpField == null ||
                                  (int)PrMpField.GetValue(prNow) <= 0;
                    }
                    catch (Exception)
                    {
                    }
                    // HP 条：染 #46B2FF。
                    TintGauge(__instance, _joniMdHField, JoniHpRgb, false);
                    // MP 条（追加需求 2026-09-24）：同样染 #46B2FF。
                    TintGauge(__instance, _joniMdMField, JoniHpRgb, mpEmpty);
                }
                else
                {
                    // 护符31 蜂巢之血（且没戴乔尼）：只把 HP 条染成橙色 #FF7F27。
                    // 与小骑士那侧"蓝色（生命血/乔尼）> 橙色（蜂巢之血）> 黑色（默认）"
                    // 的优先级一致。
                    TintGauge(__instance, _joniMdHField, HiveHpRgb, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>护符30 乔尼的祝福：HUD 血条颜色 `#46B2FF`。</summary>
        private const uint JoniHpRgb = 0x46B2FFU;
        /// <summary>护符31 蜂巢之血：HUD 血条颜色 `#FF7F27`。</summary>
        private const uint HiveHpRgb = 0xFF7F27U;

        /// <summary>
        /// 把 HUD 网格的**填充段（前 4 个顶点）**染成指定 RGB（uint 0xRRGGBB）；
        /// `empty = true` 时改为置透明。
        /// 其余顶点（背景 / 虚血 / cushion / hold 段）一概不动，保持原版观感。
        /// </summary>
        private static void TintGauge(UIStatus ui, FieldInfo field, uint rgb, bool empty)
        {
            if (ui == null || field == null)
            {
                return;
            }
            MeshDrawer md = field.GetValue(ui) as MeshDrawer;
            if (md == null)
            {
                return;
            }
            Color32[] cols = md.getColorArray();
            if (cols == null || cols.Length == 0)
            {
                return;
            }
            byte r = (byte)(rgb >> 16);
            byte g = (byte)(rgb >> 8);
            byte b = (byte)rgb;
            int keep = Mathf.Min(4, cols.Length);
            for (int i = 0; i < keep; i++)
            {
                if (empty)
                {
                    cols[i].a = 0;
                    continue;
                }
                cols[i].r = r;
                cols[i].g = g;
                cols[i].b = b;
            }
        }

        /// <summary>
        /// 护符30 效果3：**不显示 HP 条下方的数字**。
        ///
        /// HP 数字是 `UIStatus.redrawBarNumber` 用位图字体画的（`UIStatus.cs:2168`），与 MP 数字共用
        /// 同一个网格 `MdGageT`；两者唯一的区别是 **HP 那次传了 `bounds_w = bounds_hp_w`（>0）**，
        /// MP 那次传 0（`:2194`）。所以这里拦 `BMListChars.DrawScaleStringTo`：
        /// 当"网格是 MdGageT 且 bounds_w > 0"（= 正在画 HP 数字）且佩戴乔尼时，直接不画。
        /// </summary>
        private static bool JoniHideHpNumberPrefix(MeshDrawer Md, float bounds_w)
        {
            try
            {
                if (bounds_w <= 0f || Md == null || IsKnightMode ||
                    !IsEquipped(CharmOwner.Noel, JohnnyId))
                {
                    return true;
                }
                if (_uiMdGageTField == null)
                {
                    _uiMdGageTField = AccessTools.Field(typeof(UIStatus), "MdGageT");
                }
                UIStatus ui = UIStatus.Instance;
                if (_uiMdGageTField == null || ui == null ||
                    !ReferenceEquals(Md, _uiMdGageTField.GetValue(ui)))
                {
                    return true;
                }
                return false; // 不画 HP 数字
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ================= 生命上限修正：护符11 坚固心脏 / 28 生命血之心 / 29 生命血核心 =================
        /// <summary>护符11 坚固心脏：佩戴后提升的生命上限。</summary>
        public const int HeartMaxHpBonus = 120;
        /// <summary>护符28 生命血之心：生命上限 -30、魔力上限 +50。</summary>
        public const int BlueHeart1HpDelta = -30;
        public const int BlueHeart1MpDelta = 50;
        /// <summary>护符29 生命血核心：生命上限 -60、魔力上限 +100。</summary>
        public const int BlueHeart2HpDelta = -60;
        public const int BlueHeart2MpDelta = 100;
        /// <summary>
        /// 28 生命血之心 + 29 生命血核心 + 30 乔尼的祝福 **三件同时佩戴**时的额外魔力上限。
        /// 数值改由配置 `[Charm30] BlueHeartTripleMpBonus` 给出（默认 70，
        /// 对齐 `docs/护符加成描述.md` 末尾的羁绊列表）。
        /// </summary>
        /// <summary>基础上限寄存键（COOK SF，随存档序列化）：用来区分"存档里已经带上加成了"。</summary>
        private const string HeartBaseMaxHpKey = "kic_noel_heart_base";
        private const string HeartBaseMaxMpKey = "kic_noel_heart_base_mp";

        /// <summary>魔力上限字段（`M2Attackable.maxmp`，protected）。</summary>
        private static readonly FieldInfo PrMaxMpField = AccessTools.Field(typeof(M2Attackable), "maxmp");
        /// <summary>当前魔力字段（`M2Attackable.mp`，protected）。</summary>
        private static readonly FieldInfo PrMpField = AccessTools.Field(typeof(M2Attackable), "mp");

        private static bool _noelHeartActive;
        private static int _noelHeartBaseMaxHp = -1;
        private static int _noelHeartBaseMaxMp = -1;

        /// <summary>读档/换存档后重置会话状态（SF 里的基础上限保留，下一次 tick 会据此重新激活）。</summary>
        public static void ResetNoelHeartOnLoad()
        {
            _noelHeartActive = false;
            _noelHeartBaseMaxHp = -1;
            _noelHeartBaseMaxMp = -1;
        }

        /// <summary>
        /// 每帧维护（诺艾尔模式）：把**生命/魔力上限**统一算成
        /// "基础上限 + 各护符修正"，避免三个护符各自写字段互相覆盖：
        ///
        /// ```
        /// maxhp = max(1, base_hp + 坚固心脏(+120) + 生命血之心(-50) + 生命血核心(-100))
        /// maxmp = base_mp + 生命血之心(+50) + 生命血核心(+100)
        /// ```
        ///
        /// 佩戴时把基础上限寄存在 SF（读档回来据此重新激活，不会重复叠加），
        /// 首次佩戴时把坚固心脏新增的 +120 直接补成当前血量（同 HK 的观感）；
        /// 上限下降时把当前 HP/MP 钳回上限，生命上限最低为 **1**。
        /// </summary>
        public static void TickNoelHeartCharm(PRNoel pr)
        {
            try
            {
                if (pr == null || PrMaxHpField == null || PrHpField == null || PrMaxMpField == null)
                {
                    return;
                }
                bool heart = !IsKnightMode && IsEquipped(CharmOwner.Noel, HeartId);
                bool blue1 = !IsKnightMode && IsEquipped(CharmOwner.Noel, BlueHeart1Id);
                bool blue2 = !IsKnightMode && IsEquipped(CharmOwner.Noel, BlueHeart2Id);
                bool joni = !IsKnightMode && IsEquipped(CharmOwner.Noel, JohnnyId); // 护符30 乔尼的祝福
                bool want = heart || blue1 || blue2 || joni;
                if (want && !_noelHeartActive)
                {
                    ActivateNoelHeart(pr, heart);
                }
                else if (!want && _noelHeartActive)
                {
                    DeactivateNoelHeart(pr);
                    return;
                }
                if (!want || !_noelHeartActive)
                {
                    return;
                }
                int hpDelta = (heart ? HeartMaxHpBonus : 0) + (blue1 ? BlueHeart1HpDelta : 0) +
                              (blue2 ? BlueHeart2HpDelta : 0);
                int mpDelta = (blue1 ? BlueHeart1MpDelta : 0) + (blue2 ? BlueHeart2MpDelta : 0);
                int targetHp = Mathf.Max(1, _noelHeartBaseMaxHp + hpDelta);
                // 护符30：魔力上限 += 当前生命上限（效果2）
                if (joni)
                {
                    mpDelta += targetHp;
                    // 追加（2026-09-24）：28 生命血之心 + 29 生命血核心 + 30 乔尼的祝福
                    // 三件同时佩戴，再额外 +90 魔力上限。
                    if (blue1 && blue2)
                    {
                        mpDelta += KnightInCradlePlugin.JoniBlueHeartMpBonus;
                    }
                }
                int targetMp = Mathf.Max(1, _noelHeartBaseMaxMp + mpDelta);
                int nowHpMax = (int)PrMaxHpField.GetValue(pr);
                int nowMpMax = (int)PrMaxMpField.GetValue(pr);
                if (nowHpMax != targetHp || nowMpMax != targetMp)
                {
                    PrMaxHpField.SetValue(pr, targetHp);
                    PrMaxMpField.SetValue(pr, targetMp);
                    RefreshNoelHudHp();
                    RefreshNoelHudMp();
                    // 追加需求（2026-09-24）：**坐在长椅上**装卸坚固心脏 / 生命血之心 / 生命血核心 /
                    // 乔尼的祝福导致上限变化时，HP、MP 一并**回满**（等于坐在椅子上重新装满容量）。
                    if (IsNoelOnBench(pr))
                    {
                        PrHpField.SetValue(pr, targetHp);
                        if (PrMpField != null)
                        {
                            PrMpField.SetValue(pr, targetMp);
                        }
                        RefreshNoelHudHp();
                        RefreshNoelHudMp();
                    }
                }
                if ((int)PrHpField.GetValue(pr) > targetHp)
                {
                    PrHpField.SetValue(pr, targetHp);
                    RefreshNoelHudHp();
                }
                if (PrMpField != null && (int)PrMpField.GetValue(pr) > targetMp)
                {
                    PrMpField.SetValue(pr, targetMp);
                    RefreshNoelHudMp();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>首次佩戴（坚固心脏 / 生命血之心 / 生命血核心 任一）：寄存基础上限并立刻生效。</summary>
        private static void ActivateNoelHeart(PRNoel pr, bool heart)
        {
            int savedHp = COOK.getSF(HeartBaseMaxHpKey);
            int savedMp = COOK.getSF(HeartBaseMaxMpKey);
            if (savedHp > 0 && savedMp > 0)
            {
                _noelHeartBaseMaxHp = savedHp; // 读档回到"已佩戴"：基础上限记在 SF 里
                _noelHeartBaseMaxMp = savedMp;
            }
            else
            {
                int baseHp = (int)PrMaxHpField.GetValue(pr);
                int baseMp = (int)PrMaxMpField.GetValue(pr);
                if (baseHp <= 0)
                {
                    return;
                }
                _noelHeartBaseMaxHp = baseHp;
                _noelHeartBaseMaxMp = Mathf.Max(1, baseMp);
                COOK.setSF(HeartBaseMaxHpKey, Mathf.Clamp(baseHp, 0, 255));
                COOK.setSF(HeartBaseMaxMpKey, Mathf.Clamp(baseMp, 0, 255));
                if (heart)
                {
                    int hp = (int)PrHpField.GetValue(pr);
                    PrHpField.SetValue(pr, hp + HeartMaxHpBonus); // 坚固心脏新增的上限直接补满
                }
            }
            _noelHeartActive = true;
            RefreshNoelHudHp();
        }

        /// <summary>三个护符全卸下：上限还原（并把当前 HP/MP 钳回去）。</summary>
        private static void DeactivateNoelHeart(PRNoel pr)
        {
            int baseHp = _noelHeartBaseMaxHp > 0
                ? _noelHeartBaseMaxHp
                : Mathf.Max(1, (int)PrMaxHpField.GetValue(pr));
            int baseMp = _noelHeartBaseMaxMp > 0
                ? _noelHeartBaseMaxMp
                : Mathf.Max(1, (int)PrMaxMpField.GetValue(pr));
            PrMaxHpField.SetValue(pr, baseHp);
            PrMaxMpField.SetValue(pr, baseMp);
            if ((int)PrHpField.GetValue(pr) > baseHp)
            {
                PrHpField.SetValue(pr, baseHp);
            }
            if (PrMpField != null && (int)PrMpField.GetValue(pr) > baseMp)
            {
                PrMpField.SetValue(pr, baseMp);
            }
            COOK.setSF(HeartBaseMaxHpKey, 0);
            COOK.setSF(HeartBaseMaxMpKey, 0);
            _noelHeartActive = false;
            _noelHeartBaseMaxHp = -1;
            _noelHeartBaseMaxMp = -1;
            RefreshNoelHudHp();
            RefreshNoelHudMp();
        }

        // ================= 护符10 蜕变挽歌（诺艾尔侧） =================
        /// <summary>剑气飞行速度（格/秒）——与小骑士的挽歌剑气一致。</summary>
        public const float ElegySpeed = 30f;
        /// <summary>剑气射程（格）。（2026-09-22 二稿：4 → 8）</summary>
        public const float ElegyRange = 8f;
        /// <summary>剑气判定箱（世界单位）——与小骑士一致。</summary>
        public const float ElegyHitboxW = 2.0f;
        public const float ElegyHitboxH = 1.4f;
        /// <summary>
        /// 剑气伤害的**兜底值**（只在拿不到这一刀的攻击包数据时用）。
        /// 正式伤害按需求改成"复用那一刀的攻击包"：
        /// 未蓄力 = 轻攻击的伤害，已蓄力 = 魔法霰弹（含变种）的伤害。
        /// </summary>
        public const int ElegyDamage = 18;
        /// <summary>剑气贴图（与小骑士同款）。</summary>
        public const string ElegySprite = "slashes_effect0001";
        /// <summary>
        /// 蓄力释放（魔法霰弹及其变种）时替换的剑气贴图：
        /// `assets/hk/sheets/slash_effect/slash_effect_magic.png`（HK 魔法斩样式，单帧大图）。
        /// </summary>
        public const string MagicSlashSprite = "slash_effect_magic";
        /// <summary>剑气渲染尺寸系数（取小骑士 `SlashFxScale` 的同一数值，保证"大小一致"）。</summary>
        public const float ElegyFxScale = 1.5f;

        private sealed class NoelElegyBlade
        {
            public float X;
            public float Y;
            public float Dir;
            public float Traveled;
            /// <summary>这一道剑气是不是"蓄力释放"（魔法霰弹及其变种）发出来的——贴图用 magic 版。</summary>
            public bool Magic;
            /// <summary>
            /// 发射这道剑气的那一刀的攻击包（`NelAttackInfo` 复制构造，独立于原包，不怕原包回收）。
            /// 剑气命中时按这份数据结算伤害：未蓄力 = 轻攻击的伤害，已蓄力 = 魔法霰弹的伤害。
            /// </summary>
            public NelAttackInfo CarriedAtk;
            /// <summary>那一刀的 kind——用来算"萨满之石/坚固力量"的乘区。</summary>
            public MGKIND CarriedKind;
            /// <summary>
            /// 那一刀的"伤害发布率"（`PR.getHpDamagePublishRatio`，含力量等级等加成）。
            /// 原版在 `CircleCast` 里用它 `shuffleHpMpDmg`；这里在**开火时**先记下来，
            /// 命中时按同一口径结算，伤害才与真正打出那一刀一致。
            /// </summary>
            public float CarriedRatio;
            public readonly HashSet<NelEnemy> Hits = new HashSet<NelEnemy>();
        }

        private static readonly List<NoelElegyBlade> _noelElegyBlades = new List<NoelElegyBlade>();
        private static MeshDrawer _noelElegyMesh;
        private static Material _noelElegyMat;
        private static M2RenderTicket _noelElegyTicket;
        private static Map2d _noelElegyMap;
        private static Texture2D _noelElegyTex;
        /// <summary>"找敌人"的物理层掩码缓存（蜕变挽歌剑气 / 苦痛荆棘共用）。</summary>
        private static int _noelEnemyMask = -1;

        /// <summary>
        /// 护符10 蜕变挽歌（诺艾尔侧）：诺艾尔**轻攻击**（含蓄力后的魔法霰弹，见 2026-09-23 的需求）
        /// 时向前发射一道剑气。挂点 `M2PrSkill.executeSmallAttack`，
        /// 未蓄力时 kind = `MGKIND.PR_PUNCH`，蓄力释放时 kind 变成 `PR_SHOTGUN` / 各技艺 kind
        /// （`nel/M2PrSkill.cs:2705`：`mgkind = (CurMg == null) ? PR_PUNCH : PR_SHOTGUN`），
        /// 后者由 `IsNoelShotgunFlavored` 认出来；剑气会带上那一刀的攻击包结算伤害（见 `ApplyNoelElegyDamage`）。
        /// **不需要满血**（与小骑士的挽歌不同，这里没有血量条件）。
        /// </summary>
        private static void ElegyExecuteSmallAttackPostfix(MagicItem __result)
        {
            try
            {
                // 护符33 冲刺段：无论是否佩戴蜕变挽歌，都记录"最近一次挥击"的攻击包
                // （冲刺伤害要用"当前轻攻击 / 当前魔法霰弹"的数值）
                if (!IsKnightMode)
                {
                    CaptureNoelDashAttack(__result);
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, ElegyId))
                {
                    return;
                }
                if (__result == null)
                {
                    return;
                }
                // 轻攻击（PR_PUNCH）照旧；蓄力释放（魔法霰弹及其变种）时诺艾尔的"轻攻击/技艺"
                // 招牌 kind 变成了 PR_SHOTGUN / PR_WHEEL…，这里用"霰弹判据"把它们也认成同一招。
                bool charged = IsNoelShotgunFlavored(__result);
                if (__result.kind != MGKIND.PR_PUNCH && !charged)
                {
                    return;
                }
                if (charged && !KnightInCradlePlugin.ElegyOnChargedAttack)
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !pr.is_alive)
                {
                    return;
                }
                float dir = pr.mpf_is_right;
                // 把"这一刀的攻击判定"抄一份下来：剑气命中时当作那一刀打中该敌人来结算
                // （未蓄力 = 轻攻击，已蓄力 = 魔法霰弹；这一份是独立的 NelAttackInfo，
                //  不会随原攻击包回收而失效）。同时记下这一刀的"伤害发布率"。
                NelAttackInfo carriedAtk = null;
                float carriedRatio = 1f;
                if (__result.Atk0 != null)
                {
                    try
                    {
                        carriedAtk = new NelAttackInfo(__result.Atk0);
                        carriedRatio = pr.getHpDamagePublishRatio(__result);
                    }
                    catch (Exception)
                    {
                        carriedAtk = null;
                        carriedRatio = 1f;
                    }
                }
                _noelElegyBlades.Add(new NoelElegyBlade
                {
                    X = pr.x + dir * 0.5f,   // 从中心略前方发射
                    Y = pr.y - 0.5f,         // 判定/渲染整体上移 0.5 格（与小骑士一致）
                    Dir = dir,
                    Traveled = 0f,
                    Magic = charged,
                    CarriedAtk = carriedAtk,
                    CarriedKind = __result.kind,
                    CarriedRatio = carriedRatio,
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

        /// <summary>诺艾尔侧"找敌人"用的物理层掩码（蜕变挽歌剑气 / 苦痛荆棘共用）。</summary>
        private static int NoelEnemyOverlapMask()
        {
            if (_noelEnemyMask >= 0)
            {
                return _noelEnemyMask;
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
                _noelEnemyMask = mask;
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
            int mask = NoelEnemyOverlapMask();
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
                ApplyNoelElegyDamage(pr, enemy, b);
            }
        }

        /// <summary>
        /// 剑气伤害（需求 2026-09-23 调整）：
        /// **未蓄力 → 诺艾尔这一记轻攻击的伤害**；**已蓄力 → 这一发魔法霰弹（含变种）的伤害**。
        /// 做法是直接复用发射这道剑气的那一刀的攻击包（开火时复制下来的 `CarriedAtk`）：
        /// `hpdmg0` 乘上萨满之石/坚固力量/会心（`NoelFinalDamageMult`），再用这一刀的
        /// "伤害发布率" `shuffleHpMpDmg`（与原版 `CircleCast` 同一套），最后 `applyDamage`。
        /// **不再走 `fix_damage` 的真实伤害**（`fix_damage` 保持原攻击包的值 = false），
        /// 因此会照常吃敌人的减伤/浮动。
        /// 只有拿不到攻击包数据时才退回旧的固定 18 点真实伤害兜底。
        /// </summary>
        private static void ApplyNoelElegyDamage(PRNoel pr, NelEnemy enemy, NoelElegyBlade b)
        {
            try
            {
                if (IsEnemySummoning(enemy))
                {
                    return; // 生成中的魔物不能打（否则它渲染会永久消失）
                }
                NelAttackInfo src = b.CarriedAtk;
                if (src != null)
                {
                    // 护符5/13/16 的最终伤害乘区（与 CircleCast 那条路同一个函数）
                    float mult = NoelFinalDamageMult(b.CarriedKind, b.Magic);
                    if (b.Magic)
                    {
                        // 需求（2026-09-23）：蓄力释放的剑气削弱到"魔法霰弹伤害"的 30%
                        mult *= KnightInCradlePlugin.ElegyChargedDamageRatio;
                    }
                    int baseDmg = src.hpdmg0;
                    int dmg = baseDmg > 0 ? Mathf.FloorToInt(baseDmg * mult + 0.5f) : baseDmg;
                    var atk = new NelAttackInfo(src);
                    atk.Caster = pr;
                    atk.hpdmg0 = dmg;
                    atk.hpdmg_current = -1000; // 置回未结算态 → 按下面的发布率重新算
                    atk._apply_knockback_current = true;
                    atk.shuffleHpMpDmg(enemy, b.CarriedRatio, 1f, dmg, atk.mpdmg0);
                    atk.CenterXy(enemy.x, enemy.y, 0f);
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
                    // 蓄力释放的剑气命中 → 补一次"魔法霰弹击中"的动画/音效，并清掉蓄力
                    TriggerNoelElegyShotgun(pr, enemy, b);
                    // 表格：**附魔剑气**也要算"魔法命中"→ 灵魂捕手(+6) / 噬魂者(+15) 回魔
                    //（未附魔的剑气不算魔法，不回魔）
                    if (b.Magic)
                    {
                        float mpGain = 0f;
                        if (IsEquipped(CharmOwner.Noel, SoulCatcherId))
                        {
                            mpGain += SoulCatcherMp;
                        }
                        if (IsEquipped(CharmOwner.Noel, SoulEaterId))
                        {
                            mpGain += SoulEaterMp;
                        }
                        if (mpGain > 0f && KnightInCradleBehaviour.GrantNoelMana(mpGain))
                        {
                            RefreshNoelHudMp();
                        }
                    }
                    return;
                }
                // ---- 兜底：拿不到攻击包数据时沿用旧的固定真实伤害 ----
                int fallback = ElegyDamage;
                if (IsHeavyFocusActive)
                {
                    fallback = Mathf.FloorToInt(fallback * HeavyBlowFocusMult + 0.5f);
                }
                var atkFallback = new NelAttackInfo();
                atkFallback.hpdmg_current = fallback;
                atkFallback.hpdmg0 = fallback;
                atkFallback.fix_damage = true;
                atkFallback.Caster = pr;
                atkFallback.CenterXy(enemy.x, enemy.y, 0f);
                ResolveHeavyFocusHit();
                enemy.applyDamage(atkFallback, false);
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

        /// <summary>
        /// 护符10 蜕变挽歌 × 魔法蓄力（需求）：魔法蓄力之后发射的剑气命中敌人时，
        /// **视为对该敌人触发了一次魔法霰弹**——播放原版霰弹的击中动画/音效
        /// （`MDAT.setFullChargeShotgunEffect`），并清掉自己的蓄力。
        /// （伤害部分不在这里：剑气自身的伤害已经在 `ApplyNoelElegyDamage` 里按那一刀的攻击包
        ///   结算过了——未蓄力是轻攻击的伤害，已蓄力本来就是魔法霰弹的伤害。）
        ///
        /// 只在"这一发剑气是蓄力释放的（`Magic`）**且**此刻蓄力还在"时触发；
        /// 蓄力已经被别的方式消耗掉（例如这一刀的近身霰弹真的打中了）就不重复触发，
        /// 这与原版 `M2PrSkill.publishShotgunHit` 的早退条件（`CurMg == null || mp_hold < 1`）一致。
        /// </summary>
        private static void TriggerNoelElegyShotgun(PRNoel pr, NelEnemy enemy, NoelElegyBlade b)
        {
            try
            {
                if (!b.Magic || b.CarriedAtk == null || IsKnightMode || !KnightInCradlePlugin.ElegyShotgunOnHit)
                {
                    return;
                }
                M2PrSkill skill = pr != null ? pr.Skill : null;
                if (skill == null)
                {
                    return;
                }
                MagicItem curMg = skill.getCurMagic();
                if (curMg == null || !curMg.isPreparingCircle)
                {
                    return; // 蓄力已经不在了
                }
                int holdingMp = skill.getHoldingMp(true);
                if (holdingMp < 1)
                {
                    return;
                }
                Map2d mp = pr.Mp;
                if (mp == null || enemy == null)
                {
                    return;
                }
                // ① 击中动画/音效：原版霰弹那套（满蓄力时带瞬间减速的后仰效果）
                float charge01 = Mathf.Clamp01(holdingMp / Mathf.Max(1f, curMg.reduce_mp));
                var hitItem = new M2Ray.M2RayHittedItem();
                hitItem.type = HITTYPE.EN;
                hitItem.Hit = enemy;
                hitItem.Mv = enemy;
                hitItem.hit_ux = mp.map2globalux(enemy.x);
                hitItem.hit_uy = mp.map2globaluy(enemy.y);
                MDAT.setFullChargeShotgunEffect(pr, charge01, hitItem, false, true, 0.47123894f);
                // ② 清除自己的蓄力：与原版"霰弹把蓄力耗尽"时的收尾同一个调用（不复位、不返还魔力）
                skill.killHoldMagic(false, false, false);
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
            if (KnightInCradlePlugin.MagicSlashOnCharged)
            {
                GetMagicSlashTexture(); // 预热（缺素材时只找一次，不每帧读盘）
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
            // 剑气的框按**原贴图**尺寸算：蓄力时只换贴图，大小不变（magic 那张是 1280×832 的大图，
            // 直接按它的尺寸画会放大 8 倍），再叠 MagicSlash* 两个配置给用户自己调。
            float w = _noelElegyTex.width * scale * ElegyFxScale;
            float h = _noelElegyTex.height * scale * ElegyFxScale;
            Texture2D magicTex = KnightInCradlePlugin.MagicSlashOnCharged ? GetMagicSlashTexture() : null;
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            for (int i = 0; i < _noelElegyBlades.Count; i++)
            {
                NoelElegyBlade b = _noelElegyBlades[i];
                bool useMagic = b.Magic && magicTex != null;
                Texture2D tex = useMagic ? magicTex : _noelElegyTex;
                float bw = w;
                float bh = h;
                if (useMagic)
                {
                    bw *= KnightInCradlePlugin.MagicSlashScale;
                    bh *= KnightInCradlePlugin.MagicSlashScale * KnightInCradlePlugin.MagicSlashHeightRatio;
                }
                float dx = (b.X + 1f - pr.x) * mp.CLEN;
                float dy = -(b.Y - 0.5f - pr.y) * mp.CLEN;
                _noelElegyMesh.Col = MTRX.ColWhite;
                _noelElegyMesh.initForImgAndTexture(tex);
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
                _noelElegyMesh.Rect(dx - bw * 0.5f, dy - bh * 0.5f, bw, bh, false);
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

        // ---- 蓄力剑气贴图（魔法霰弹及其变种）：蜕变挽歌 / 修长之钉 / 骄傲印记共用同一张 ----
        private static Texture2D _noelMagicSlashTex;
        private static bool _noelMagicSlashTried;

        /// <summary>
        /// 蓄力释放（魔法霰弹及其变种）时替换的剑气贴图 `slash_effect_magic.png`。
        /// 素材在仓库里的位置是 `assets/hk/sheets/slash_effect/`（sheets 是原始素材目录），
        /// 所以两个路径都找一遍；找不到就返回 null（调用方回退到原贴图，只是观感不变）。
        /// </summary>
        private static Texture2D GetMagicSlashTexture()
        {
            if (_noelMagicSlashTex != null)
            {
                return _noelMagicSlashTex;
            }
            if (_noelMagicSlashTried)
            {
                return null; // 缺素材时只找一次，别每帧读磁盘
            }
            _noelMagicSlashTried = true;
            try
            {
                string root = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets", "hk");
                string[] candidates =
                {
                    System.IO.Path.Combine(root, "sheets", "slash_effect", MagicSlashSprite + ".png"),
                    System.IO.Path.Combine(root, "sprites", MagicSlashSprite + ".png"),
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (!System.IO.File.Exists(candidates[i]))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(candidates[i])))
                    {
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        _noelMagicSlashTex = tex;
                        KnightInCradlePlugin.PluginLog?.LogInfo("[KIC][magic剑气] 已加载 " + candidates[i]);
                        return tex;
                    }
                    UnityEngine.Object.Destroy(tex);
                }
                KnightInCradlePlugin.PluginLog?.LogWarning(
                    "[KIC][magic剑气] 没找到 " + MagicSlashSprite + ".png，蓄力时继续用原贴图");
            }
            catch (Exception)
            {
            }
            return null;
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

        // ================= 护符21 苦痛荆棘（诺艾尔侧） =================
        /// <summary>反击伤害 = 这次受到的伤害 × 该倍率（需求：2 倍）。</summary>
        public const float ThornsDamageMult = 2f;
        /// <summary>反击半径（格，需求：3）。</summary>
        public const float ThornsRadius = 3f;

        private static int _thornsLastFrame = -100;

        /// <summary>
        /// 护符21 苦痛荆棘（诺艾尔侧）效果②：诺艾尔受到伤害时，对**半径 3 格内**的敌人
        /// 造成"这次受到的伤害 × 2"的伤害（固定伤害 `fix_damage`，与场上难度曲线无关，
        /// 与诺艾尔侧的其它护符乘区也无关——需求就是"受到伤害的 2 倍"）。
        ///
        /// 由伤害前缀 `SturdyHpDamagePrefix`（`M2Attackable.applyHpDamage`）在**原始伤害 &gt; 0** 时调用，
        /// 与幼虫之歌同一个时机：即便同一次伤害随后被坚硬外壳改写成 0/1，反击照样按"真的挨了一下"结算；
        /// 同一帧的多次伤害入口只结算一次。
        /// </summary>
        private static void TryThornsOfAgony(PRNoel noel, int damage)
        {
            try
            {
                if (noel == null || damage <= 0 || IsKnightMode || !IsEquipped(CharmOwner.Noel, ThornsId))
                {
                    return;
                }
                if (_thornsLastFrame == Time.frameCount)
                {
                    return; // 同一帧只反击一次
                }
                _thornsLastFrame = Time.frameCount;
                Map2d mp = noel.Mp;
                if (mp == null)
                {
                    return;
                }
                int mask = NoelEnemyOverlapMask();
                if (mask == 0)
                {
                    return;
                }
                // 表格：苦痛荆棘吃 13 坚固力量 / 16 沉重之击(会心) / 20 亡者之怒 的伤害加成（不吃萨满）
                int dmg = Mathf.Max(1, Mathf.FloorToInt(
                    damage * KnightInCradlePlugin.ThornsDamageMult * NoelSideDamageMult(true) + 0.5f));
                float radius = KnightInCradlePlugin.ThornsRadius;
                float mx = mp.pixel2ux(noel.x * mp.CLEN);
                float my = mp.pixel2uy(noel.y * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 世界坐标的圆只用来粗筛，真正的"3 格"用地图坐标复核（grid = 1.0）
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, radius, mask);
                var done = new HashSet<NelEnemy>();
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null || !enemy.is_alive || IsEnemySummoning(enemy) || !done.Add(enemy))
                    {
                        continue; // 生成中的魔物不吃苦痛荆棘反击
                    }
                    float dx = enemy.x - noel.x;
                    float dy = enemy.y - noel.y;
                    if (dx * dx + dy * dy > radius * radius)
                    {
                        continue;
                    }
                    var atk = new NelAttackInfo();
                    atk.hpdmg0 = dmg;
                    atk.hpdmg_current = dmg;
                    atk.fix_damage = true;
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                    atk.CenterXy(enemy.x, enemy.y, 0f);
                    enemy.applyDamage(atk, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符22 巴尔德之壳（诺艾尔侧）：
        /// 诺艾尔**正在魔法咏唱**（= 正握着蓄力魔力）时，在她身体中心渲染 `blocker_shell`
        /// （与小骑士的巴尔德之壳同一套素材/尺寸），并且**咏唱期间处于无敌状态**。
        ///
        /// ① 无敌：走 AIC 原生 `M2NoDamageManager`（`M2Attackable.NoDamage`），
        ///    每帧续 2 帧（`BaldurShellInvincibleFrames`）——咏唱一停就立刻失效，不留尾巴；
        ///    额外在受伤前缀 `SturdyHpDamagePrefix` 里把伤害改写成 0 作为兜底
        ///    （覆盖原生无敌也挡不住的穿透类伤害，并让"壳挡下的攻击不算受伤"：
        ///     幼虫之歌/苦痛荆棘都不触发，与小骑士侧的口径一致）。
        /// ② 渲染：与小骑士一样播 出现(20fps,4 帧) → 持有帧 → 收起(3 帧)，
        ///    锚点 = 诺艾尔身体中心（`mbottom - sizey/2`），图层 PR1（身前层）。
        /// </summary>
        private const float BaldurShellInvincibleFrames = 2f;
        /// <summary>壳贴图基准缩放（取小骑士 `BaldurShellScale` 的同一数值，保证"同小骑士"的大小）。</summary>
        private const float BaldurShellBaseScale = 0.26f;
        /// <summary>壳动画帧率（与小骑士的 BaldurAppear/Disappear 一致）。</summary>
        private const float BaldurShellFps = 20f;

        private static readonly string[] NoelShellAppearSprites =
        {
            "blocker_shell_appear0000",
            "blocker_shell_appear0001",
            "blocker_shell_appear0002",
            "blocker_shell_appear0004",
        };
        private static readonly string[] NoelShellDisappearSprites =
        {
            "blocker_shell_appear0002",
            "blocker_shell_appear0001",
            "blocker_shell_appear0000",
        };
        /// <summary>壳挡下伤害时的受击动画（同小骑士的 BaldurImpact 剪辑）。</summary>
        private static readonly string[] NoelShellImpactSprites =
        {
            "blocker_shell_impact0001",
            "blocker_shell_appear0002",
            "blocker_shell_appear0000",
        };
        /// <summary>壳破碎时的起始帧（冲击帧），后面接收起帧。</summary>
        private const string NoelShellBreakLeadSprite = "blocker_shell_impact0001";

        private static Texture2D _noelShellHoldTex;      // 完全展开后的持有帧
        private static Texture2D[] _noelShellAppearTex;
        private static Texture2D[] _noelShellDisappearTex;
        private static Texture2D[] _noelShellImpactTex;
        private static Texture2D[] _noelShellBreakTex;   // 破碎 = 冲击帧 + 收起帧
        private static Texture2D[] _noelShellPlayingTex; // 正在播的一次性剪辑（出现/收起）
        private static Texture2D _noelShellCurTex;       // 本帧要画的帧
        private static float _noelShellTimer;
        private static int _noelShellIndex;
        private static bool _noelShellActive;            // 壳展开中（= 正在咏唱）
        private static bool _noelShellLoadTried;
        private static int _noelShellBlocksLeft = 3;     // 还能挡几次（每次展开重置为上限）
        private static float _noelShellRecoverAt;        // > 0 = 已破碎，到这个时间点恢复
        private static MeshDrawer _noelShellMesh;
        private static Material _noelShellMat;
        private static M2RenderTicket _noelShellTicket;
        private static Map2d _noelShellMap;
        private static bool _noelShellTicketBehind = true; // 当前票据建在哪个层（配置改了要重建）

        /// <summary>诺艾尔是不是"正在魔法咏唱"：握着蓄力魔力（`CurMg`）且蓄力量 ≥ 1。</summary>
        private static bool IsNoelMagicChanting(PRNoel pr)
        {
            try
            {
                if (pr == null || pr.Skill == null)
                {
                    return false;
                }
                MagicItem curMg = pr.Skill.getCurMagic();
                return curMg != null && curMg.isPreparingCircle && pr.Skill.getHoldingMp(true) >= 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>巴尔德之壳此刻是否该生效（佩戴 + 诺艾尔模式 + 正在咏唱）。</summary>
        private static bool IsNoelShellActive(PRNoel pr)
        {
            // 破碎后的恢复期内不生效（10 秒内壳不会展开、也不保护）
            return _noelShellRecoverAt <= 0f && !IsKnightMode && IsEquipped(CharmOwner.Noel, BaldurId) &&
                   IsNoelMagicChanting(pr);
        }

        /// <summary>每帧推进（诺艾尔模式调用）：壳的展开/收起、无敌续期、动画与票据维护。</summary>
        public static void TickNoelBaldurShellCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                // 破碎后的恢复时间到点 → 次数回满（若还在咏唱，壳会在这一帧重新展开）
                if (_noelShellRecoverAt > 0f && Time.unscaledTime >= _noelShellRecoverAt)
                {
                    _noelShellRecoverAt = 0f;
                    _noelShellBlocksLeft = KnightInCradlePlugin.BaldurShellMaxBlocks;
                }
                bool want = IsNoelShellActive(pr);
                if (want && !_noelShellActive)
                {
                    _noelShellActive = true;
                    if (_noelShellBlocksLeft <= 0)
                    {
                        _noelShellBlocksLeft = KnightInCradlePlugin.BaldurShellMaxBlocks;
                    }
                    PlayNoelShellClip(true);
                }
                else if (!want && _noelShellActive)
                {
                    _noelShellActive = false;
                    PlayNoelShellClip(false);
                }
                if (want)
                {
                    // 咏唱期间持续无敌（每帧续 2 帧，停下就立刻失效）
                    try
                    {
                        if (PrNoDamageField != null &&
                            PrNoDamageField.GetValue(pr) is M2NoDamageManager nd)
                        {
                            nd.Add(BaldurShellInvincibleFrames);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                AdvanceNoelShell(Time.deltaTime);
                EnsureNoelShellTicket(pr, _noelShellActive || _noelShellPlayingTex != null);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>播一次"出现/收起"剪辑（贴图未加载时什么都不做）。</summary>
        private static void PlayNoelShellClip(bool appear)
        {
            try
            {
                PlayNoelShellClip(appear ? _noelShellAppearTex : _noelShellDisappearTex);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>播一次指定剪辑（贴图未加载时什么都不做）。</summary>
        private static void PlayNoelShellClip(Texture2D[] frames)
        {
            try
            {
                if (!EnsureNoelShellTextures())
                {
                    return;
                }
                _noelShellPlayingTex = frames;
                _noelShellTimer = 0f;
                _noelShellIndex = 0;
                _noelShellCurTex = _noelShellPlayingTex != null && _noelShellPlayingTex.Length > 0
                    ? _noelShellPlayingTex[0]
                    : _noelShellHoldTex;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 壳挡下了一次伤害：扣一次抵挡次数并播受击动画；
        /// 次数用尽 → **壳破碎**（收起 + 进入 `ShellRecoverSeconds` 秒的恢复期，期间不再展开/不保护）。
        /// </summary>
        private static void ConsumeNoelShellBlock()
        {
            int maxBlocks = KnightInCradlePlugin.BaldurShellMaxBlocks;
            if (_noelShellBlocksLeft > maxBlocks)
            {
                _noelShellBlocksLeft = maxBlocks;
            }
            if (_noelShellBlocksLeft > 0)
            {
                _noelShellBlocksLeft--;
            }
            if (_noelShellBlocksLeft > 0)
            {
                PlayNoelShellClip(_noelShellImpactTex); // 还有次数：受击动画后回到持有帧
                return;
            }
            // 次数用尽：破碎
            _noelShellActive = false;
            _noelShellRecoverAt = Time.unscaledTime + KnightInCradlePlugin.BaldurShellRecoverSeconds;
            PlayNoelShellClip(_noelShellBreakTex);
            KnightInCradlePlugin.PluginLog?.LogInfo(
                "[KIC][巴尔德之壳] 挡满 " + maxBlocks + " 次 → 破碎，" +
                KnightInCradlePlugin.BaldurShellRecoverSeconds + " 秒后恢复");
        }

        /// <summary>
        /// 护符22 巴尔德之壳（诺艾尔侧）：壳展开期间把**整次伤害结算**拦下来
        /// （不扣血、不打断咏唱、不被击退），并消耗一次抵挡次数。
        ///
        /// 挂点 `M2PrADmg.applyDamage` 的 6 参核心重载（`nel/M2PrADmg.cs:1109`）：
        /// 玩家受到的每一发伤害都要从这里进去（敌人的 5 参重载只是转调到这里，
        /// 地图危险区 `PR.applyDamageFromMap` 也走同一条），
        /// 而且它在 `NoDamage` 判定**之前**，所以"壳挡住了几次"能准确计数。
        /// </summary>
        private static bool NoelShellDamagePrefix(M2PrADmg __instance, NelAttackInfo Atk, ref int __result)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                if (noel == null || !ReferenceEquals(__instance.Pr, noel))
                {
                    return true; // 只管本地诺艾尔
                }
                // 护符25 发光子宫：剑山及其污染体的攻击一律不造成伤害
                if (IsUniAttackBlocked(Atk))
                {
                    __result = 0;
                    return false;
                }
                if (!IsNoelShellActive(noel))
                {
                    return true; // 壳没展开 / 已破碎恢复中
                }
                ConsumeNoelShellBlock();
                __result = 0; // 这次伤害整个不结算
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 护符3 坚硬外壳 + 护符30 乔尼的祝福（组合）：**锁魔力池窗口内整次受击都不结算**。
        ///
        /// 为什么不能只在 `M2Attackable.applyHpDamage` 上锁：那条只管 **HP 伤害**，而 AIC 的攻击
        /// 还能附带**魔力伤害**（`Atk._mpdmg` / `split_mpdmg`），它走的是
        /// `M2PrADmg.splitMpByDamage`（`M2PrADmg.cs:1459 / 1505`），**根本不经过**
        /// `applyHpDamage`。乔尼把魔力池当血条用之后，这些伤害同样会掉"血"，于是绕过锁
        /// —— 这正是"短时间内还会受多次伤害"的来源。
        ///
        /// 所以锁蓝期间直接在**伤害管线最外层**整次作废：挂点与护符22 巴尔德之壳相同
        /// （`M2PrADmg.applyDamage` 六参核心重载，在 NoDamage 判定之前）。
        /// </summary>
        private static bool JoniSturdyLockDamagePrefix(M2PrADmg __instance, NelAttackInfo Atk, ref int __result)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                if (noel == null || !ReferenceEquals(__instance.Pr, noel))
                {
                    return true; // 只管本地诺艾尔
                }
                // 护符20 亡者之怒 效果3：亡者之怒期间，**魔物来源**的伤害整次作废
                // （地图危险格走 `PR.applyDamageFromMap`，那里另有分支；我们自己的致死调用
                // 直接走 `PR.applyHpDamage`，不经过这里，不受影响）
                if (IsNoelFuryImmune && IsEnemySourceAttack(Atk))
                {
                    __result = 0;
                    return false;
                }
                if (!IsJoniSturdyMpLocked(noel))
                {
                    // 护符33 冲刺段（隐藏本体期间）/ 护符35 旋风斩无敌帧：整次受击也直接作废
                    if (NoelShadowDashActive || NoelSpinInvincible)
                    {
                        __result = 0;
                        return false;
                    }
                    return true;
                }
                __result = 0; // 锁蓝中：这一次受击整个不结算
                if (_joniLockDiag < 10)
                {
                    _joniLockDiag++;
                    KnightInCradlePlugin.PluginLog?.LogInfo(
                        "[KIC][锁蓝] 拦截受击 t=" + Time.time.ToString("F2") +
                        "（锁定到 " + _joniSturdyMpLockUntil.ToString("F2") + "）");
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>临时诊断计数（锁蓝拦截日志，10 条封顶；验证后删）。</summary>
        private static int _joniLockDiag;

        // ================= 护符31 蜂巢之血（诺艾尔侧） =================
        /// <summary>蜂巢之血：回复间隔（秒）。</summary>
        public const float HiveBloodIntervalSeconds = 10f;
        /// <summary>蜂巢之血：每次回复量（HP；佩戴乔尼时同一数值改成回 MP）。</summary>
        public const int HiveBloodHealAmount = 10;
        private static float _noelHiveBloodTimer;

        /// <summary>
        /// 护符31 蜂巢之血（诺艾尔侧）效果2：**每 10 秒回复 10**。
        ///
        /// 回血走 AIC 原生的 `PR.cureHp(int)`：它自己会更新 HUD、并走 GSaver。
        /// 佩戴乔尼的祝福时不用另写分支——`JoniCureHpPrefix`（护符30 效果4）本来就把
        /// `cureHp` 改写成 `cureMp`，于是"此时蜂巢之血回复 MP"自动成立，HP 条也不动。
        ///
        /// 效果1（HP 条染橙 `#FF7F27`）在 `JoniRedrawAllPostfix` 里；
        /// 效果3（蜂巢魔物不攻击）与护符2 蜂群集结共用 `HiveNeutralActive()`。
        /// </summary>
        public static void TickNoelHiveBloodCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, HiveId))
                {
                    _noelHiveBloodTimer = 0f;
                    return;
                }
                if (!pr.is_alive)
                {
                    return;
                }
                _noelHiveBloodTimer += Time.deltaTime;
                if (_noelHiveBloodTimer < HiveBloodIntervalSeconds)
                {
                    return;
                }
                _noelHiveBloodTimer -= HiveBloodIntervalSeconds;
                if (_noelHiveBloodTimer > HiveBloodIntervalSeconds)
                {
                    _noelHiveBloodTimer = 0f; // 长时间没推进（过图/暂停）时不补算
                }
                pr.cureHp(HiveBloodHealAmount);
                RefreshNoelHudHp();
                RefreshNoelHudMp();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 组合（乔尼 + 硬壳）是否正处于"锁魔力池"窗口内：两种护符都戴着，且还在 2 秒窗口里。
        /// </summary>
        private static bool IsJoniSturdyMpLocked(PRNoel noel)
        {
            return Time.time < _joniSturdyMpLockUntil &&
                   IsEquipped(CharmOwner.Noel, SturdyId) &&
                   JoniBlessingActive(noel);
        }

        /// <summary>壳动画推进：一次性剪辑播完 → 展开时停在持有帧，收起时消失。</summary>
        private static void AdvanceNoelShell(float dt)
        {
            try
            {
                if (_noelShellPlayingTex == null)
                {
                    _noelShellCurTex = _noelShellActive ? _noelShellHoldTex : null;
                    return;
                }
                _noelShellTimer += dt;
                float frameTime = 1f / Mathf.Max(BaldurShellFps, 0.001f);
                int guard = 30;
                while (_noelShellTimer >= frameTime && guard-- > 0)
                {
                    _noelShellTimer -= frameTime;
                    _noelShellIndex++;
                }
                if (_noelShellIndex >= _noelShellPlayingTex.Length)
                {
                    _noelShellPlayingTex = null;
                    _noelShellCurTex = _noelShellActive ? _noelShellHoldTex : null;
                }
                else
                {
                    _noelShellCurTex = _noelShellPlayingTex[_noelShellIndex];
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>壳贴图：从 `assets/hk/sheets/baldur/sprites/` 读出现/收起帧与持有帧（只读一次）。</summary>
        private static bool EnsureNoelShellTextures()
        {
            if (_noelShellHoldTex != null)
            {
                return true;
            }
            if (_noelShellLoadTried)
            {
                return false;
            }
            _noelShellLoadTried = true;
            try
            {
                _noelShellAppearTex = LoadNoelShellFrames(NoelShellAppearSprites);
                _noelShellDisappearTex = LoadNoelShellFrames(NoelShellDisappearSprites);
                _noelShellImpactTex = LoadNoelShellFrames(NoelShellImpactSprites);
                // 破碎 = 起始冲击帧 + 收起帧
                var breakFrames = new List<Texture2D>();
                Texture2D lead = LoadNoelShellTex(NoelShellBreakLeadSprite);
                if (lead != null)
                {
                    breakFrames.Add(lead);
                }
                if (_noelShellDisappearTex != null)
                {
                    breakFrames.AddRange(_noelShellDisappearTex);
                }
                _noelShellBreakTex = breakFrames.Count > 0 ? breakFrames.ToArray() : null;
                _noelShellHoldTex = LoadNoelShellTex(NoelShellAppearSprites[NoelShellAppearSprites.Length - 1]);
                if (_noelShellHoldTex == null && _noelShellAppearTex != null && _noelShellAppearTex.Length > 0)
                {
                    _noelShellHoldTex = _noelShellAppearTex[_noelShellAppearTex.Length - 1];
                }
                if (_noelShellHoldTex == null)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][巴尔德之壳] 没找到 blocker_shell 素材（assets/hk/sheets/baldur/sprites），壳不显示");
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Texture2D[] LoadNoelShellFrames(string[] names)
        {
            if (names == null || names.Length == 0)
            {
                return null;
            }
            var list = new List<Texture2D>();
            for (int i = 0; i < names.Length; i++)
            {
                Texture2D tex = LoadNoelShellTex(names[i]);
                if (tex != null)
                {
                    list.Add(tex);
                }
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static Texture2D LoadNoelShellTex(string sprite)
        {
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "baldur", "sprites", sprite + ".png");
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

        /// <summary>壳票据：绑当前地图的 MovRenderer（身前层 PR1，同小骑士的壳）。</summary>
        private static void EnsureNoelShellTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelShellTicket();
                return;
            }
            if (!EnsureNoelShellTextures())
            {
                return; // 素材缺失：只是不显示，无敌照常
            }
            bool behind = KnightInCradlePlugin.BaldurShellBehindNoel;
            if (_noelShellMesh != null && _noelShellMap == mp && _noelShellTicket != null &&
                _noelShellTicketBehind == behind)
            {
                return;
            }
            ReleaseNoelShellTicket();
            _noelShellMap = mp;
            _noelShellTicketBehind = behind;
            _noelShellMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _noelShellMesh.draw_gl_only = true;
            _noelShellMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelShellMat.EnableKeyword("NO_PIXELSNAP");
            _noelShellMesh.activate("noel_baldur_shell", _noelShellMat, false, MTRX.ColWhite, null);
            _noelShellTicket = mp.MovRenderer.assignDrawable(
                // PR0 = 诺艾尔图层**之后**（身后层，同模组的会心光圈）；PR1 = 身前层（同小骑士的壳）
                behind ? M2Mover.DRAW_ORDER.PR0 : M2Mover.DRAW_ORDER.PR1,
                null, PrepareNoelShellMesh, _noelShellMesh, null, null);
        }

        private static void ReleaseNoelShellTicket()
        {
            try
            {
                if (_noelShellTicket != null && _noelShellMap != null && _noelShellMap.MovRenderer != null)
                {
                    _noelShellMap.MovRenderer.deassignDrawable(_noelShellTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelShellMat != null)
                {
                    IN.DestroyOne(_noelShellMat);
                }
            }
            catch (Exception)
            {
            }
            _noelShellTicket = null;
            _noelShellMesh = null;
            _noelShellMat = null;
            _noelShellMap = null;
        }

        /// <summary>
        /// 壳绘制：锚定诺艾尔身体中心，按当前帧画 `blocker_shell`。
        /// 尺寸 = 贴图尺寸 × `BaldurShellBaseScale`（同小骑士）× `ShellScale` × `ShellWidth/HeightRatio`；
        /// 位置 = 身体中心 + `ShellOffsetY` 格（y 向下为正）。为了和小骑士一样"更实"，叠 3 层绘制。
        /// </summary>
        private static bool PrepareNoelShellMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelShellMap;
            if (mp == null || _noelShellMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelShellMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            Texture2D tex = _noelShellCurTex;
            if (pr == null || tex == null)
            {
                MdOut = _noelShellMesh;
                return true;
            }
            float cy = pr.mbottom - pr.sizey * 0.5f + KnightInCradlePlugin.BaldurShellOffsetY;
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(cy * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = BaldurShellBaseScale * KnightInCradlePlugin.BaldurShellScale;
            float w = tex.width * scale * KnightInCradlePlugin.BaldurShellWidthRatio;
            float h = tex.height * scale * KnightInCradlePlugin.BaldurShellHeightRatio;
            if (w <= 0f || h <= 0f)
            {
                MdOut = _noelShellMesh;
                return true;
            }
            _noelShellMesh.Col = MTRX.ColWhite;
            _noelShellMesh.initForImgAndTexture(tex);
            _noelShellMesh.uv_top = 0f;
            _noelShellMesh.uv_height = 1f;
            _noelShellMesh.uv_left = 0f;
            _noelShellMesh.uv_width = 1f;
            // `MeshDrawer.Rect(x, y, w, h)` 的 (x,y) 就是矩形中心 → (0,0) = 以锚点为中心
            _noelShellMesh.Rect(0f, 0f, w, h, false);
            _noelShellMesh.Rect(0f, 0f, w, h, false);
            _noelShellMesh.Rect(0f, 0f, w, h, false);
            MdOut = _noelShellMesh;
            return true;
        }

        /// <summary>
        /// 护符27 深度聚集（诺艾尔侧，2026-09-24 新逻辑）：
        /// ① **咏唱时间 +50%**：`PR.getCastingTimeScale` 后缀除以 `ChantTimeMult`（默认 1.5），
        ///    只改读条时长，威力/耗魔照旧；
        /// ② **咏唱消耗的 MP 等量回血**：法师扣魔的三个入口里，正常施放走
        ///    `M2PrSkill.explodeMagic`（`M2PrSkill.cs:3611`）→ `Pr.applyMpDamage(num2,…)`；
        ///    这里在 `explodeMagic` 前缀里打一个"这一发是诺艾尔施法开销"的标记，
        ///    在 `PR.applyMpDamage` 后缀里用**实际扣掉的数值**回等量 HP（1 MP = 1 HP）。
        /// ③ **蓄力完成后，下一次伤害 +25%**：每帧 tick 里发现 `CurMg.chant_finished` 就置位，
        ///    由 `NoelFinalDamageMult`（与萨满之石/坚固力量/会心同一个乘区）在**法术、
        ///    魔法霰弹及其变种**命中时乘上并消耗掉。
        /// </summary>
        private static bool _noelDeepGatherReady;      // 蓄力完成 → 下一次伤害 +25%

        /// <summary>上一帧"已蓄魔力量"（= 咏唱到目前为止消耗掉的 MP），用于按增量动态回血。</summary>
        private static int _noelDeepGatherChargedMp;

        /// <summary>动态回血：把这一帧新增的"已消耗 MP"等量转成 HP（1 MP = 1 HP）。</summary>
        private static void HealNoelByDeepGather(PRNoel pr, int mp)
        {
            try
            {
                if (mp <= 0 || PrHpField == null || PrMaxHpField == null)
                {
                    return;
                }
                int hp = (int)PrHpField.GetValue(pr);
                int maxHp = (int)PrMaxHpField.GetValue(pr);
                int heal = Mathf.Min(mp, maxHp - hp);
                if (heal <= 0)
                {
                    return; // HP 已满：不转化
                }
                PrHpField.SetValue(pr, hp + heal);
                RefreshNoelHudHp();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进（诺艾尔模式调用）：蓄力完成 → 置"下一次伤害 +25%"。</summary>
        public static void TickNoelDeepGatherCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, DeepGatherId))
                {
                    _noelDeepGatherReady = false;
                    _noelDeepGatherChargedMp = 0;
                    return;
                }
                M2PrSkill skill = pr.Skill;
                MagicItem curMg = skill != null ? skill.getCurMagic() : null;
                // ② 动态回血：把"这一帧新增的已消耗 MP"等量转成 HP
                // （AIC 咏唱时魔力是**渐进累积**的：`getHoldingMp` 随读条增长、魔力条上表现为暗色蓄力段，
                //   所以用它的增量做 1:1 回血；松开/取消时它会归零，我们只重置基准、不倒扣）
                int charged = skill != null ? skill.getHoldingMp(true) : 0;
                if (charged > _noelDeepGatherChargedMp)
                {
                    HealNoelByDeepGather(pr, charged - _noelDeepGatherChargedMp);
                }
                _noelDeepGatherChargedMp = charged;
                if (curMg != null && curMg.isPreparingCircle && curMg.chant_finished)
                {
                    _noelDeepGatherReady = true; // 蓄力完成
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>深度聚集的"下一次伤害 +25%"：只对法术/魔法霰弹/霰弹变种生效，命中一次即消耗。</summary>
        private static float ApplyDeepGatherNextDamage(MGKIND kind, bool shotgunFlavored, float mult)
        {
            try
            {
                if (!_noelDeepGatherReady || IsKnightMode || !IsEquipped(CharmOwner.Noel, DeepGatherId))
                {
                    return mult;
                }
                if (!IsPlayerMagicKind(kind) && !shotgunFlavored)
                {
                    return mult; // 近战等其它伤害不消耗这个加成
                }
                _noelDeepGatherReady = false;
                return mult * KnightInCradlePlugin.DeepGatherNextDamageMult;
            }
            catch (Exception)
            {
                return mult;
            }
        }

        /// <summary>
        /// 护符26 快速聚集（诺艾尔侧）：诺艾尔**魔法咏唱速度 +25%**。
        ///
        /// 挂点：`PR.getCastingTimeScale(MagicItem Mg)`（`nel/PR.cs:5126`）的**后缀**——
        /// 它是"这一发魔法的推进速度"：咏唱推进用 `M2PrSkill.cs:525`
        /// （`base.TS × max(1, getCastingTimeScale) × magic_prepare_speed`），
        /// 咏唱中物品的 TS 用 `MagicItem.cs:2200`，所以加它同时加快**读条**与**咏唱完成后的出手延迟**，
        /// 而**不改**魔法威力（威力由 `mp_hold` 缩放）与耗魔总量（扣魔发生在释放时）。
        ///
        /// 只对"**当前正在咏唱的这发**"生效（`Skill.getCurMagic() == Mg`）：
        /// 松手释放后的子弹再调 `getCastingTimeScale`（例如 `MgWhiteArrow.cs:71` 用来自缩放箭体）
        /// 时 `CurMg` 已经为 null，因此飞行/命中的表现完全不受影响。
        /// </summary>
        private static void FastGatherCastScalePostfix(PR __instance, MagicItem Mg, ref float __result)
        {
            try
            {
                if (Mg == null || __instance == null || IsKnightMode)
                {
                    return;
                }
                bool fastGather = IsEquipped(CharmOwner.Noel, FastGatherId);
                bool deepGather = IsEquipped(CharmOwner.Noel, DeepGatherId);
                // 护符20 效果8：亡者之怒期间咏唱速度 +25%（走与快速聚集同一条 `getCastingTimeScale`）
                bool fury = _noelFuryActive;
                if (!fastGather && !deepGather && !fury)
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !ReferenceEquals(__instance, pr))
                {
                    return; // 只管本地诺艾尔
                }
                M2PrSkill skill = __instance.Skill;
                if (skill == null || !ReferenceEquals(skill.getCurMagic(), Mg))
                {
                    return; // 只加快"正在咏唱的那一发"
                }
                if (fastGather)
                {
                    __result *= KnightInCradlePlugin.FastGatherChantSpeedMult;
                }
                if (fury)
                {
                    __result *= KnightInCradlePlugin.FuryChantSpeedMult;
                }
                if (deepGather)
                {
                    // 深度聚集：咏唱时间 ×1.5 → 推进速度 ÷1.5
                    __result /= Mathf.Max(0.01f, KnightInCradlePlugin.DeepGatherChantTimeMult);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 魔物是否处于**召唤/生成阶段**（`NelEnemy.STATE.SUMMONED`）。
        /// 这个阶段**不能打**：AIC 的生成流程（`NelEnemy.runSummoned`，`NelEnemy.cs:1199-1236`）靠
        /// 60 帧后自己调 `quitSummonAndAppear` 把 `disappearing` 复位；中途挨打会被踢出 SUMMONED 状态，
        /// `disappearing` 无人复位 → **生成结束后魔物渲染永久消失**。
        /// 小骑士侧在 `ApplyKnightAreaDamage`（`KnightEntity.cs:5022-5028`）里同样跳过。
        /// 诺艾尔侧所有"模组自己直接调 `enemy.applyDamage`"的护符伤害都要先过这一关——
        /// 原版攻击走的是命中检测，生成中的魔物本来就不会被选中，所以我们绕过了那一层。
        /// </summary>
        private static bool IsEnemySummoning(NelEnemy enemy)
        {
            try
            {
                return enemy != null && enemy.getState() == NelEnemy.STATE.SUMMONED;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护符24 防御者纹章（诺艾尔侧）：与**小骑士那套完全相同**——
        /// ① 以诺艾尔为中心、半径 3 格的**深蓝法阵实心圆**（身后层 PR0）；
        /// ② 圆内敌人**进入立刻受 10 点伤害**（可击晕），之后**每 1 秒**仍在圆内再受 10 点；
        /// ③ 圆内**摧毁蜘蛛陷阱 / 蛛丝球**（`MGKIND.BASIC_SHOT` + `MgBsSpiderTrap.MEM`）；
        /// ④ 动态层（PR2）：每 3 秒从中心发射一道 12 格/秒扩张的圆环，到边缘发光并依次出现
        ///    星→方→叉图案（保持 0.3s 后淡出 0.3s）；
        /// ⑤ 身前层（PR1）：5 颗深蓝球体以 1.75 格为半径、2.5 秒公转一周。
        /// 贴图（法阵圆 / 球体）直接复用骑士侧的程序化生成方法（`KnightEntity.MakeShelter*Texture`）。
        /// </summary>
        private static readonly HashSet<NelEnemy> _noelShelterInside = new HashSet<NelEnemy>();
        private static float _noelShelterDamageTimer = NoelShelterDamageTick;
        private static float _noelShelterRingTimer = NoelShelterRingInterval;
        private static float _noelShelterRingRadius = -1f;
        private static float _noelShelterPatternTimer = -1f;
        private static int _noelShelterPatternType;
        private static float _noelShelterFlashTimer;
        private static float _noelShelterSphereAngle;

        private const float NoelShelterDamageTick = 1f;        // 圆内伤害间隔（秒，同小骑士）
        private const float NoelShelterRingInterval = 3f;      // 圆环发射间隔（同小骑士）
        private const float NoelShelterRingSpeed = 12f;        // 圆环扩张速度（格/秒）
        private const float NoelShelterPatternHoldTime = 0.3f; // 图案保持满亮度时长
        private const float NoelShelterPatternFadeTime = 0.3f; // 图案/圆环淡出时长
        private const float NoelShelterFlashTime = 0.25f;      // 边缘发光时长
        private const float NoelShelterSphereRadius = 0.5f;    // 球体半径（格）
        private const float NoelShelterSphereOrbit = 1.75f;    // 球心距（格）
        private const float NoelShelterSpherePeriod = 2.5f;    // 公转周期（秒）
        private const int NoelShelterSphereCount = 5;          // 球体数量

        private static Texture2D _noelShelterCircleTex;
        private static Texture2D _noelShelterSphereTex;
        private static MeshDrawer _noelShelterCircleMesh;
        private static MeshDrawer _noelShelterFxMesh;
        private static MeshDrawer _noelShelterSphereMesh;
        private static Material _noelShelterCircleMat;
        private static Material _noelShelterFxMat;
        private static Material _noelShelterSphereMat;
        private static M2RenderTicket _noelShelterCircleTicket;
        private static M2RenderTicket _noelShelterFxTicket;
        private static M2RenderTicket _noelShelterSphereTicket;
        private static Map2d _noelShelterMap;
        private static readonly HashSet<object> _noelShelterTrapDone = new HashSet<object>();
        private static FieldInfo _noelShelterMgItemsField;
        private static FieldInfo _noelShelterMgLenField;

        /// <summary>每帧推进（诺艾尔模式调用）：法阵计时、伤害结算、陷阱清除与票据维护。</summary>
        public static void TickNoelShelterCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, ShelterId))
                {
                    _noelShelterInside.Clear();
                    _noelShelterTrapDone.Clear();
                    _noelShelterDamageTimer = NoelShelterDamageTick;
                    _noelShelterRingTimer = NoelShelterRingInterval;
                    _noelShelterRingRadius = -1f;
                    _noelShelterPatternTimer = -1f;
                    _noelShelterFlashTimer = 0f;
                    ReleaseNoelShelterTickets();
                    return;
                }
                float dt = Time.deltaTime;
                _noelShelterSphereAngle += (6.2831853f / NoelShelterSpherePeriod) * dt;
                // 圆内持续伤害：每 1 秒对仍在圆内的敌人再打一次
                _noelShelterDamageTimer -= dt;
                if (_noelShelterDamageTimer <= 0f)
                {
                    _noelShelterDamageTimer = NoelShelterDamageTick;
                    int dmg = KnightInCradlePlugin.ShelterCircleDamage;
                    foreach (NelEnemy enemy in _noelShelterInside)
                    {
                        if (enemy != null && enemy.is_alive)
                        {
                            ApplyNoelShelterDamage(pr, enemy, dmg);
                        }
                    }
                }
                // 每帧检测新进入圆内的敌人：立刻打一次（离开再进会再次触发）
                NoelShelterEntryCheck(pr);
                // 圆内清除蜘蛛陷阱 / 蛛丝球
                NoelShelterDestroySpiderThings(pr);
                // 圆环发射与扩张
                _noelShelterRingTimer -= dt;
                if (_noelShelterRingTimer <= 0f)
                {
                    _noelShelterRingTimer = NoelShelterRingInterval;
                    _noelShelterRingRadius = 0f;
                }
                float radius = KnightInCradlePlugin.ShelterCircleRadius;
                if (_noelShelterRingRadius >= 0f)
                {
                    if (_noelShelterRingRadius < radius)
                    {
                        _noelShelterRingRadius += NoelShelterRingSpeed * dt;
                        if (_noelShelterRingRadius >= radius)
                        {
                            _noelShelterRingRadius = radius;
                            _noelShelterFlashTimer = NoelShelterFlashTime;
                            _noelShelterPatternType = (_noelShelterPatternType + 1) % 3; // 星→方→叉
                            _noelShelterPatternTimer = 0f;
                        }
                    }
                    else
                    {
                        _noelShelterPatternTimer += dt;
                        if (_noelShelterPatternTimer >= NoelShelterPatternHoldTime + NoelShelterPatternFadeTime)
                        {
                            _noelShelterRingRadius = -1f;
                            _noelShelterPatternTimer = -1f;
                        }
                    }
                }
                if (_noelShelterFlashTimer > 0f)
                {
                    _noelShelterFlashTimer -= dt;
                }
                EnsureNoelShelterTickets(pr);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧：圆内敌人集合刷新；新进入的立刻受一次伤害。</summary>
        private static void NoelShelterEntryCheck(PRNoel pr)
        {
            try
            {
                Map2d mp = pr.Mp;
                if (mp == null || mp.gameObject == null)
                {
                    return;
                }
                int mask = NoelEnemyOverlapMask();
                if (mask == 0)
                {
                    return;
                }
                float radius = KnightInCradlePlugin.ShelterCircleRadius;
                float mx = mp.pixel2ux(pr.x * mp.CLEN);
                float my = mp.pixel2uy(pr.y * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, radius, mask);
                var insideNow = new HashSet<NelEnemy>();
                if (hits != null)
                {
                    int dmg = KnightInCradlePlugin.ShelterCircleDamage;
                    for (int i = 0; i < hits.Length; i++)
                    {
                        Collider2D c = hits[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        if (enemy == null || !enemy.is_alive || enemy.Mp != mp || IsEnemySummoning(enemy) ||
                            !insideNow.Add(enemy))
                        {
                            continue; // 生成中的魔物不进法阵判定
                        }
                        if (!_noelShelterInside.Contains(enemy))
                        {
                            ApplyNoelShelterDamage(pr, enemy, dmg); // 刚进入：立刻一次
                        }
                    }
                }
                _noelShelterInside.Clear();
                _noelShelterInside.UnionWith(insideNow);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>法阵伤害：完整受击管线（可击晕），`huttobi_ratio = -100` 压制击飞（同小骑士）。</summary>
        private static void ApplyNoelShelterDamage(PRNoel pr, NelEnemy enemy, int dmg)
        {
            try
            {
                if (IsEnemySummoning(enemy))
                {
                    return; // 生成中的魔物不能打（否则它渲染会永久消失）
                }
                // 表格：防御者纹章吃 5 萨满之石 / 16 沉重之击(会心) / 20 亡者之怒；不吃 13 坚固力量。
                float mult = NoelSideDamageMult(false);
                if (IsEquipped(CharmOwner.Noel, ShamanId))
                {
                    mult *= ShamanDamageMult;
                }
                dmg = Mathf.Max(1, Mathf.FloorToInt(dmg * mult + 0.5f));
                var atk = new NelAttackInfo();
                atk.hpdmg0 = dmg;
                atk.hpdmg_current = dmg;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f;
                atk.Caster = pr;
                atk.AttackFrom = pr;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>圆内摧毁蜘蛛陷阱与蛛丝球（同小骑士：扫 `MGContainer` 里的 BASIC_SHOT + `MgBsSpiderTrap.MEM`）。</summary>
        private static void NoelShelterDestroySpiderThings(PRNoel pr)
        {
            try
            {
                Map2d mp = pr.Mp;
                if (mp == null)
                {
                    return;
                }
                if (_noelShelterMgItemsField == null)
                {
                    _noelShelterMgItemsField = typeof(RBase<MagicItem>).GetField("AItems",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    _noelShelterMgLenField = typeof(RBase<MagicItem>).GetField("LEN",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                if (_noelShelterMgItemsField == null || !(mp.M2D is NelM2DBase nm2d))
                {
                    return;
                }
                MagicItem[] items = _noelShelterMgItemsField.GetValue(nm2d.MGC) as MagicItem[];
                if (items == null)
                {
                    return;
                }
                int len = _noelShelterMgLenField != null
                    ? (int)_noelShelterMgLenField.GetValue(nm2d.MGC)
                    : items.Length;
                float radius = KnightInCradlePlugin.ShelterCircleRadius;
                for (int i = 0; i < len && i < items.Length; i++)
                {
                    MagicItem mg = items[i];
                    if (mg == null || mg.kind != MGKIND.BASIC_SHOT || !(mg.Other is MgBsSpiderTrap.MEM))
                    {
                        continue;
                    }
                    float dx = mg.sx - pr.x;
                    float dy = mg.sy - pr.y;
                    if (dx * dx + dy * dy > radius * radius)
                    {
                        continue;
                    }
                    if (_noelShelterTrapDone.Add(mg))
                    {
                        try
                        {
                            mg.kill(0f);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                if (_noelShelterTrapDone.Count > 64)
                {
                    _noelShelterTrapDone.Clear(); // 魔法实例会被池复用，容量过大时重置去重
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>法阵的三层票据：实心圆（身后 PR0）、动态层（PR2）、公转球体（身前 PR1）。</summary>
        private static void EnsureNoelShelterTickets(PRNoel pr)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null || mp.MovRenderer == null)
            {
                return;
            }
            if (_noelShelterCircleTex == null)
            {
                _noelShelterCircleTex = KnightEntity.MakeShelterCircleTexture(128);
            }
            if (_noelShelterSphereTex == null)
            {
                _noelShelterSphereTex = KnightEntity.MakeShelterSphereTexture(64);
            }
            if (_noelShelterCircleTicket != null && _noelShelterFxTicket != null &&
                _noelShelterSphereTicket != null && ReferenceEquals(_noelShelterMap, mp))
            {
                return;
            }
            ReleaseNoelShelterTickets();
            _noelShelterMap = mp;
            _noelShelterCircleMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _noelShelterCircleMesh.draw_gl_only = true;
            _noelShelterCircleMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelShelterCircleMat.EnableKeyword("NO_PIXELSNAP");
            _noelShelterCircleMesh.activate("noel_shelter_circle", _noelShelterCircleMat, false, MTRX.ColWhite, null);
            _noelShelterCircleTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelShelterCircleMesh, _noelShelterCircleMesh, null, null);
            _noelShelterFxMesh = new MeshDrawer(null, 4 * 512, 6 * 512);
            _noelShelterFxMesh.draw_gl_only = true;
            _noelShelterFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelShelterFxMat.EnableKeyword("NO_PIXELSNAP");
            _noelShelterFxMesh.activate("noel_shelter_fx", _noelShelterFxMat, false, MTRX.ColWhite, null);
            _noelShelterFxTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR2, null, PrepareNoelShelterFxMesh, _noelShelterFxMesh, null, null);
            _noelShelterSphereMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _noelShelterSphereMesh.draw_gl_only = true;
            _noelShelterSphereMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelShelterSphereMat.EnableKeyword("NO_PIXELSNAP");
            _noelShelterSphereMesh.activate("noel_shelter_sphere", _noelShelterSphereMat, false, MTRX.ColWhite, null);
            _noelShelterSphereTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, PrepareNoelShelterSphereMesh, _noelShelterSphereMesh, null, null);
        }

        private static void ReleaseNoelShelterTickets()
        {
            ReleaseNoelTicket(ref _noelShelterCircleTicket, ref _noelShelterCircleMesh, ref _noelShelterCircleMat);
            ReleaseNoelTicket(ref _noelShelterFxTicket, ref _noelShelterFxMesh, ref _noelShelterFxMat);
            ReleaseNoelTicket(ref _noelShelterSphereTicket, ref _noelShelterSphereMesh, ref _noelShelterSphereMat);
            _noelShelterMap = null;
        }

        private static void ReleaseNoelTicket(ref M2RenderTicket ticket, ref MeshDrawer mesh, ref Material mat)
        {
            try
            {
                if (ticket != null && _noelShelterMap != null && _noelShelterMap.MovRenderer != null)
                {
                    _noelShelterMap.MovRenderer.deassignDrawable(ticket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (mat != null)
                {
                    IN.DestroyOne(mat);
                }
            }
            catch (Exception)
            {
            }
            ticket = null;
            mesh = null;
            mat = null;
        }

        /// <summary>法阵实心圆（身后层）：半径 3 格的深蓝圆（浓度由贴图 alpha 控制）。</summary>
        private static bool PrepareNoelShelterCircleMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelShelterMap;
            if (mp == null || _noelShelterCircleMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelShelterCircleMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelShelterCircleTex == null)
            {
                MdOut = _noelShelterCircleMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float size = KnightInCradlePlugin.ShelterCircleRadius * 2f * mp.CLEN;
            _noelShelterCircleMesh.Col = new Color(0.1f, 0.18f, 0.75f, 1f);
            _noelShelterCircleMesh.initForImgAndTexture(_noelShelterCircleTex);
            _noelShelterCircleMesh.uv_top = 0f;
            _noelShelterCircleMesh.uv_height = 1f;
            _noelShelterCircleMesh.uv_left = 0f;
            _noelShelterCircleMesh.uv_width = 1f;
            _noelShelterCircleMesh.Rect(0f, 0f, size, size, false);
            MdOut = _noelShelterCircleMesh;
            return true;
        }

        /// <summary>法阵动态层（PR2）：扩张圆环 + 边缘发光 + 星/方/叉图案。</summary>
        private static bool PrepareNoelShelterFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelShelterMap;
            if (mp == null || _noelShelterFxMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelShelterFxMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null)
            {
                MdOut = _noelShelterFxMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float c = mp.CLEN;
            float radius = KnightInCradlePlugin.ShelterCircleRadius;
            if (_noelShelterRingRadius >= 0f)
            {
                float t = Mathf.Clamp01(_noelShelterRingRadius / radius);
                float alpha = 1f;
                if (_noelShelterRingRadius >= radius && _noelShelterPatternTimer >= 0f)
                {
                    alpha = _noelShelterPatternTimer < NoelShelterPatternHoldTime
                        ? 1f
                        : 1f - (_noelShelterPatternTimer - NoelShelterPatternHoldTime) / NoelShelterPatternFadeTime;
                }
                Color ringCol = Color.Lerp(Color.white, new Color(0.35f, 0.62f, 1f, 1f), t);
                ringCol.a = Mathf.Clamp01(alpha);
                _noelShelterFxMesh.Col = ringCol;
                _noelShelterFxMesh.Circle(0f, 0f, _noelShelterRingRadius * c, 3.5f, false);
            }
            if (_noelShelterFlashTimer > 0f)
            {
                float fa = Mathf.Clamp01(_noelShelterFlashTimer / NoelShelterFlashTime);
                _noelShelterFxMesh.Col = new Color(1f, 1f, 1f, fa * 0.95f);
                _noelShelterFxMesh.Circle(0f, 0f, radius * c, 5f, false);
            }
            if (_noelShelterPatternTimer >= 0f)
            {
                float pa = _noelShelterPatternTimer < NoelShelterPatternHoldTime
                    ? 1f
                    : 1f - (_noelShelterPatternTimer - NoelShelterPatternHoldTime) / NoelShelterPatternFadeTime;
                _noelShelterFxMesh.Col = new Color(1f, 1f, 1f, Mathf.Clamp01(pa));
                DrawNoelShelterPattern(_noelShelterFxMesh, _noelShelterPatternType, radius * c);
            }
            MdOut = _noelShelterFxMesh;
            return true;
        }

        /// <summary>法阵公转球体（PR1）：5 颗深蓝球，球心距 1.75 格、72° 均布、2.5 秒一周。</summary>
        private static bool PrepareNoelShelterSphereMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelShelterMap;
            if (mp == null || _noelShelterSphereMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelShelterSphereMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelShelterSphereTex == null)
            {
                MdOut = _noelShelterSphereMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float c = mp.CLEN;
            float size = NoelShelterSphereRadius * 2f * c;
            _noelShelterSphereMesh.Col = MTRX.ColWhite; // 渐变已做进贴图
            _noelShelterSphereMesh.initForImgAndTexture(_noelShelterSphereTex);
            _noelShelterSphereMesh.uv_top = 0f;
            _noelShelterSphereMesh.uv_height = 1f;
            _noelShelterSphereMesh.uv_left = 0f;
            _noelShelterSphereMesh.uv_width = 1f;
            for (int i = 0; i < NoelShelterSphereCount; i++)
            {
                float ang = _noelShelterSphereAngle + i * (6.2831853f / NoelShelterSphereCount);
                float sx = Mathf.Cos(ang) * NoelShelterSphereOrbit * c;
                float sy = -Mathf.Sin(ang) * NoelShelterSphereOrbit * c; // 网格 y 向上为正
                _noelShelterSphereMesh.Rect(sx, sy, size, size, false);
            }
            MdOut = _noelShelterSphereMesh;
            return true;
        }

        /// <summary>绘制法阵图案（星/方/叉），同小骑士的 `DrawShelterPattern`。</summary>
        private static void DrawNoelShelterPattern(MeshDrawer md, int type, float rPx)
        {
            const float thick = 3.5f;
            if (type == 0) // 五角星
            {
                float inner = rPx * 0.382f;
                for (int k = 0; k < 10; k++)
                {
                    float a0 = 1.5707964f + k * 0.62831854f;
                    float a1 = a0 + 0.62831854f;
                    float r0 = (k % 2 == 0) ? rPx : inner;
                    float r1 = ((k + 1) % 2 == 0) ? rPx : inner;
                    md.Line(Mathf.Cos(a0) * r0, Mathf.Sin(a0) * r0,
                            Mathf.Cos(a1) * r1, Mathf.Sin(a1) * r1, thick);
                }
            }
            else if (type == 1) // 正方形（四角贴圆）
            {
                for (int k = 0; k < 4; k++)
                {
                    float a0 = k * 1.5707964f;
                    float a1 = a0 + 1.5707964f;
                    md.Line(Mathf.Cos(a0) * rPx, Mathf.Sin(a0) * rPx,
                            Mathf.Cos(a1) * rPx, Mathf.Sin(a1) * rPx, thick);
                }
            }
            else // 叉号
            {
                float d = rPx * 0.70710678f;
                md.Line(-d, -d, d, d, thick);
                md.Line(-d, d, d, -d, thick);
            }
        }

        /// <summary>
        /// 护符25 发光子宫（诺艾尔侧）：
        /// ① 每 **2 秒**消耗 **10 MP** 生成一只**小剑山**（最多同时 4 只；坐长椅休息时不生成），
        ///    贴图 = `assets/hk/sheets/spike/spike_1~spike_8` 循环播放；
        /// ② 小剑山自动索敌飞行（索敌 12 格），碰到敌人爆炸，对以命中点为中心 6×6 格内的敌人
        ///    造成 **30** 伤害（每只敌人只结算一次）；
        /// ③ 跟随/坐长椅/过图逻辑与小骑士的幼虫一致——**过图时全部清除并按数量返还魔力**
        ///    （小骑士那边是每只返还 8 灵魂，这里每只返还 10 MP）。
        /// 渲染走自绘票据（同小骑士：锚定诺艾尔原点，每只按相对偏移画）。
        /// </summary>
        private sealed class NoelSpikeFollower
        {
            public float X;
            public float Y;
            public float Vx;
            public float Vy;
            public int Phase;          // 0 出生 / 1 飞行 / 3 坐长椅休息
            public float AnimTime;
            public float HomeOx;
            public float HomeOy;
            public float BuzzTimer;
            public NelEnemy Target;
            public int SleepStage;
            public float SleepX;
            public float SleepGroundY;
            public int Dir = 1;
        }

        private sealed class NoelSpikeBoom
        {
            public float X;
            public float Y;
            public float AnimTime;
        }

        private static readonly string[] NoelSpikeSprites =
        {
            "spike_1", "spike_2", "spike_3", "spike_4", "spike_5", "spike_6", "spike_7", "spike_8",
        };

        private static readonly List<NoelSpikeFollower> _noelSpikes = new List<NoelSpikeFollower>();
        private static readonly List<NoelSpikeBoom> _noelSpikeBooms = new List<NoelSpikeBoom>();
        private static float _noelSpikeSpawnTimer;
        private static Map2d _noelSpikeMap;
        private static Texture2D[] _noelSpikeFrames;
        private static Texture2D[] _noelSpikeBoomFrames;
        private static bool _noelSpikeLoadTried;
        private static MeshDrawer _noelSpikeMesh;
        private static Material _noelSpikeMat;
        private static M2RenderTicket _noelSpikeTicket;

        /// <summary>每帧推进（诺艾尔模式调用）：生成、跟随、索敌、碰撞爆炸、过图返还与票据维护。</summary>
        public static void TickNoelUterusCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, UterusId))
                {
                    _noelSpikes.Clear();
                    _noelSpikeBooms.Clear();
                    _noelSpikeSpawnTimer = 0f;
                    _noelSpikeMap = pr.Mp;
                    ReleaseNoelSpikeTicket();
                    return;
                }
                Map2d mp = pr.Mp;
                if (mp == null)
                {
                    return;
                }
                // 过图（同小骑士的幼虫）：清除全部小剑山并按数量返还魔力
                if (!ReferenceEquals(mp, _noelSpikeMap))
                {
                    _noelSpikeMap = mp;
                    if (_noelSpikes.Count > 0)
                    {
                        int refund = _noelSpikes.Count * KnightInCradlePlugin.UterusSpawnMp;
                        _noelSpikes.Clear();
                        if (refund > 0 && KnightInCradleBehaviour.GrantNoelMana(refund))
                        {
                            RefreshNoelHudMp();
                        }
                    }
                    _noelSpikeBooms.Clear();
                    ReleaseNoelSpikeTicket();
                }
                float dt = Time.deltaTime;
                bool sitting = false;
                try
                {
                    sitting = pr.isBenchState();
                }
                catch (Exception)
                {
                }
                // ① 生成：每 SpawnInterval 秒消耗 SpawnMpCost MP（坐长椅休息时不消耗、不生成）
                // ③ 剑山及其污染体不会攻击诺艾尔：每帧清掉它们对她的锁定目标
                ClearUniEnemyAim(mp);
                if (!sitting)
                {
                    _noelSpikeSpawnTimer -= dt;
                    if (_noelSpikeSpawnTimer <= 0f)
                    {
                        _noelSpikeSpawnTimer = KnightInCradlePlugin.UterusSpawnInterval;
                        int cost = KnightInCradlePlugin.UterusSpawnMp;
                        if (_noelSpikes.Count < KnightInCradlePlugin.UterusMaxCount && pr.get_mp() >= cost)
                        {
                            try
                            {
                                pr.applyMpDamage(cost, true, null, false, false);
                            }
                            catch (Exception)
                            {
                            }
                            SpawnNoelSpike(pr);
                        }
                    }
                }
                bool noelMoving = Mathf.Abs(pr.getPhysic() != null ? pr.getPhysic().walk_xspeed : 0f) > 0.05f ||
                                  !pr.hasFoot();
                // ② 跟随 / 索敌 / 坐椅子落地
                for (int i = _noelSpikes.Count - 1; i >= 0; i--)
                {
                    NoelSpikeFollower s = _noelSpikes[i];
                    s.AnimTime += dt;
                    if (sitting && s.Phase == 1)
                    {
                        s.Phase = 3;
                        s.AnimTime = 0f;
                        s.Vx = 0f;
                        s.Vy = 0f;
                        s.SleepStage = 0;
                        s.SleepX = s.X;
                        s.SleepGroundY = NoelSpikeSleepGroundY(mp, s.X);
                    }
                    if (!sitting && s.Phase == 3)
                    {
                        s.Phase = 1;
                        s.AnimTime = 0f;
                        s.Vx = 0f;
                        s.Vy = 0f;
                    }
                    if (s.Phase == 3)
                    {
                        if (s.SleepStage == 0)
                        {
                            float dx = s.SleepX - s.X;
                            float dy = s.SleepGroundY - s.Y;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            if (dist > 0.02f)
                            {
                                float spd = Mathf.Min(4f, dist * 4f);
                                s.X += dx / dist * spd * dt;
                                s.Y += dy / dist * spd * dt;
                            }
                            else
                            {
                                s.X = s.SleepX;
                                s.Y = s.SleepGroundY;
                                s.SleepStage = 1;
                                s.AnimTime = 0f;
                            }
                        }
                        continue;
                    }
                    if (s.Phase == 0) // 出生：初速衰减
                    {
                        s.X += s.Vx * dt;
                        s.Y += s.Vy * dt;
                        s.Vx *= 0.85f;
                        s.Vy *= 0.85f;
                        if (s.AnimTime >= 0.5f)
                        {
                            s.Phase = 1;
                            s.AnimTime = 0f;
                        }
                        continue;
                    }
                    // Phase 1：索敌冲刺 > 追诺艾尔 > 原地悬浮微动
                    if (s.Target == null || !s.Target.is_alive ||
                        s.Target.gameObject == null || s.Target.Mp != mp)
                    {
                        s.Target = FindNearestNoelSpikeTarget(mp, s.X, s.Y);
                    }
                    if (s.Target != null)
                    {
                        float dx = s.Target.x - s.X;
                        float dy = s.Target.y - s.Y;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > 0.01f)
                        {
                            dx /= dist;
                            dy /= dist;
                            s.Vx += dx * NoelSpikeAccel * dt;
                            s.Vy += dy * NoelSpikeAccel * dt;
                            float spd = Mathf.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);
                            if (spd > NoelSpikeSpeedMax)
                            {
                                s.Vx *= NoelSpikeSpeedMax / spd;
                                s.Vy *= NoelSpikeSpeedMax / spd;
                            }
                        }
                    }
                    else if (noelMoving)
                    {
                        float dx = pr.x + s.HomeOx - s.X;
                        float dy = pr.y + s.HomeOy - s.Y;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > 0.05f)
                        {
                            float spd = Mathf.Min(7f, dist * 4f);
                            s.Vx = dx / dist * spd;
                            s.Vy = dy / dist * spd;
                        }
                        else
                        {
                            s.Vx = 0f;
                            s.Vy = 0f;
                        }
                    }
                    else
                    {
                        s.BuzzTimer -= dt;
                        if (s.BuzzTimer <= 0f)
                        {
                            s.BuzzTimer = UnityEngine.Random.Range(0.3f, 0.8f);
                            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                            float rad = UnityEngine.Random.Range(0.8f, 2f);
                            s.HomeOx = Mathf.Cos(ang) * rad;
                            s.HomeOy = Mathf.Sin(ang) * rad;
                        }
                        float dx = pr.x + s.HomeOx - s.X;
                        float dy = pr.y + s.HomeOy - s.Y;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > 0.05f)
                        {
                            float spd = Mathf.Min(1.6f, dist * 2.5f);
                            s.Vx = dx / dist * spd;
                            s.Vy = dy / dist * spd;
                        }
                        else
                        {
                            s.Vx = 0f;
                            s.Vy = 0f;
                        }
                    }
                    s.X += s.Vx * dt;
                    s.Y += s.Vy * dt;
                    if (Mathf.Abs(s.Vx) > 0.05f)
                    {
                        s.Dir = s.Vx > 0f ? 1 : -1;
                    }
                    if (NoelSpikeHitEnemy(pr, s))
                    {
                        _noelSpikes.RemoveAt(i); // 碰撞后立即消失（爆炸特效另存）
                    }
                }
                // ③ 爆炸特效推进
                for (int i = _noelSpikeBooms.Count - 1; i >= 0; i--)
                {
                    NoelSpikeBoom b = _noelSpikeBooms[i];
                    b.AnimTime += dt;
                    if (b.AnimTime >= NoelSpikeBoomDuration())
                    {
                        _noelSpikeBooms.RemoveAt(i);
                    }
                }
                EnsureNoelSpikeTicket(pr, _noelSpikes.Count > 0 || _noelSpikeBooms.Count > 0);
            }
            catch (Exception)
            {
            }
        }

        private const float NoelSpikeSpeedMax = 10f;  // 飞行最大速度（格/秒，同小骑士）
        private const float NoelSpikeAccel = 32f;     // 朝向目标加速度（同小骑士）
        private const float NoelSpikeHitRadius = 0.55f;
        private const float NoelSpikeSeekRange = 12f; // 索敌范围（格，同小骑士）
        private const float NoelSpikeBoomFps = 20f;   // 爆炸动画帧率（同小骑士）

        /// <summary>生成一只小剑山：向上前方随机初速（同小骑士的出生手感），并分配停靠偏移。</summary>
        private static void SpawnNoelSpike(PRNoel pr)
        {
            float ang = UnityEngine.Random.Range(0.7f, 2.44f); // 40°~140°
            var s = new NoelSpikeFollower
            {
                X = pr.x,
                Y = pr.y,
                Vx = Mathf.Cos(ang) * 3.6f,
                Vy = -Mathf.Sin(ang) * 3.6f, // y 向下为正，向上为负
                Phase = 0,
                AnimTime = 0f,
                BuzzTimer = UnityEngine.Random.Range(0.3f, 0.8f),
            };
            float homeAng = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float homeRad = UnityEngine.Random.Range(0.8f, 2f);
            s.HomeOx = Mathf.Cos(homeAng) * homeRad;
            s.HomeOy = Mathf.Sin(homeAng) * homeRad;
            _noelSpikes.Add(s);
        }

        private static float NoelSpikeBoomDuration()
        {
            int n = _noelSpikeBoomFrames != null ? _noelSpikeBoomFrames.Length : 13;
            return Mathf.Max(0.01f, n / NoelSpikeBoomFps);
        }

        /// <summary>
        /// 是不是"**剑山**"（`NelNUni` = `Enemy_UNI`）。
        /// 剑山的污染体（雷雨 OverDrive）是**同一个实例**变强（`NelEnemy.initOverDrive`，
        /// `OverDriveManager.cs:204-215`），所以一个 `is NelNUni` 判断就同时覆盖两种形态。
        /// </summary>
        private static bool IsUniEnemy(NelEnemy en)
        {
            try
            {
                return en is NelNUni;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>发光子宫是否正在生效（诺艾尔模式 + 佩戴）。</summary>
        private static bool NoelUterusActive()
        {
            return !IsKnightMode && IsEquipped(CharmOwner.Noel, UterusId);
        }

        /// <summary>
        /// 发光子宫效果③：**剑山及其污染体不会攻击诺艾尔**——
        /// 每帧把地图上所有剑山的锁定目标（`NAI.AimPr`）清空，配合 `NaiAimPrSetPrefix` 里的拦截，
        /// 它们就不会再把诺艾尔作为目标。
        /// </summary>
        private static void ClearUniEnemyAim(Map2d mp)
        {
            try
            {
                if (mp == null || !NoelUterusActive())
                {
                    return;
                }
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (mp.getMv(i) is NelNUni uni)
                    {
                        NAI ai = uni.getAI();
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

        /// <summary>
        /// 发光子宫效果③的后半句："**即便攻击也不会造成伤害**"——
        /// 剑山（含污染体）打过来的伤害一律作废（不扣血、不打断、不击退）。
        /// 由 `NoelShellDamagePrefix` 在玩家受伤入口调用。
        /// </summary>
        private static bool IsUniAttackBlocked(NelAttackInfo Atk)
        {
            try
            {
                if (Atk == null || !NoelUterusActive())
                {
                    return false;
                }
                return Atk.Caster is NelNUni;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>坐长椅时小剑山的落点：脚下地面减去贴图半高（同小骑士的做法）。</summary>
        private static float NoelSpikeSleepGroundY(Map2d mp, float x)
        {
            float groundY = float.NaN;
            float probeY = 0f;
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr != null)
            {
                probeY = pr.mbottom;
                groundY = pr.mbottom;
            }
            try
            {
                if (mp != null && mp.BCC != null)
                {
                    BCCLine line;
                    float g = mp.BCC.isFallable(x, probeY + 0.1f, 0.15f, 4f, out line, true, true, -1f, null);
                    if (g >= 0f)
                    {
                        groundY = g;
                    }
                }
            }
            catch (Exception)
            {
            }
            if (_noelSpikeFrames != null && _noelSpikeFrames.Length > 0 && _noelSpikeFrames[0] != null &&
                mp != null && mp.CLEN > 0f)
            {
                groundY -= _noelSpikeFrames[0].height * KnightInCradlePlugin.UterusSpikeScale / (2f * mp.CLEN);
            }
            return groundY;
        }

        /// <summary>索敌：以自身为中心 NoelSpikeSeekRange 格内最近的敌人。</summary>
        private static NelEnemy FindNearestNoelSpikeTarget(Map2d mp, float sx, float sy)
        {
            try
            {
                int mask = NoelEnemyOverlapMask();
                if (mp == null || mask == 0)
                {
                    return null;
                }
                float mx = mp.pixel2ux(sx * mp.CLEN);
                float my = mp.pixel2uy(sy * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, NoelSpikeSeekRange, mask);
                NelEnemy best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null || !enemy.is_alive || enemy.Mp != mp || IsUniEnemy(enemy))
                    {
                        continue; // 不索敌"剑山及其污染体"（发光子宫追加效果）
                    }
                    float d = (enemy.x - sx) * (enemy.x - sx) + (enemy.y - sy) * (enemy.y - sy);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = enemy;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null; // 过图瞬间物理查询可能异常，静默跳过本帧
            }
        }

        /// <summary>
        /// 小剑山碰到敌人：生成爆炸特效与音效，并对以命中点为中心 6×6 格内的敌人造成 30 伤害
        /// （每只敌人只结算一次）。
        /// </summary>
        private static bool NoelSpikeHitEnemy(PRNoel pr, NoelSpikeFollower s)
        {
            try
            {
                Map2d mp = pr.Mp;
                int mask = NoelEnemyOverlapMask();
                if (mp == null || mask == 0)
                {
                    return false;
                }
                float mx = mp.pixel2ux(s.X * mp.CLEN);
                float my = mp.pixel2uy(s.Y * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 先小半径检测"真的碰到了敌人"（不能只看任意碰撞体，否则出生在身上会误炸）
                Collider2D[] touch = Physics2D.OverlapCircleAll(center, NoelSpikeHitRadius, mask);
                bool touched = false;
                if (touch != null)
                {
                    for (int i = 0; i < touch.Length; i++)
                    {
                        Collider2D c = touch[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        if (enemy != null && enemy.is_alive && enemy.Mp == mp && !IsEnemySummoning(enemy) &&
                            !IsUniEnemy(enemy))
                        {
                            touched = true; // 剑山不触发爆炸，小剑山继续飞
                            break;
                        }
                    }
                }
                if (!touched)
                {
                    return false;
                }
                _noelSpikeBooms.Add(new NoelSpikeBoom { X = s.X, Y = s.Y, AnimTime = 0f });
                try
                {
                    DashAudio.PlayUterusExplosion();
                }
                catch (Exception)
                {
                }
                float size = KnightInCradlePlugin.UterusExplosionSize;
                Collider2D[] aoe = Physics2D.OverlapBoxAll(center, new Vector2(size, size), 0f, mask);
                if (aoe != null)
                {
                    var applied = new HashSet<NelEnemy>();
                    int dmg = KnightInCradlePlugin.UterusExplosionDamage;
                    for (int i = 0; i < aoe.Length; i++)
                    {
                        Collider2D c = aoe[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        if (enemy == null || !enemy.is_alive || enemy.Mp != mp || IsEnemySummoning(enemy) ||
                            IsUniEnemy(enemy) || !applied.Add(enemy))
                        {
                            continue; // 生成中的魔物与剑山都不吃爆炸伤害
                        }
                        var atk = new NelAttackInfo();
                        atk.hpdmg0 = dmg;
                        atk.hpdmg_current = dmg;
                        atk.fix_damage = true; // 真伤，与小骑士的幼体爆炸同为固定值
                        atk.Caster = pr;
                        atk.AttackFrom = pr;
                        atk.CenterXy(enemy.x, enemy.y, 0f);
                        enemy.applyDamage(atk, false);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>小剑山 / 爆炸特效票据（身前层 PR1）。</summary>
        private static void EnsureNoelSpikeTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelSpikeTicket();
                return;
            }
            if (!EnsureNoelSpikeTextures())
            {
                return; // 素材缺失：只是不显示，爆炸伤害照常
            }
            if (_noelSpikeMesh != null && _noelSpikeMat != null && _noelSpikeTicket != null &&
                ReferenceEquals(_noelSpikeMap, mp))
            {
                return;
            }
            ReleaseNoelSpikeTicket();
            _noelSpikeMap = mp;
            _noelSpikeMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _noelSpikeMesh.draw_gl_only = true;
            _noelSpikeMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelSpikeMat.EnableKeyword("NO_PIXELSNAP");
            _noelSpikeMesh.activate("noel_spike_follower", _noelSpikeMat, false, MTRX.ColWhite, null);
            _noelSpikeTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, PrepareNoelSpikeMesh, _noelSpikeMesh, null, null);
        }

        private static void ReleaseNoelSpikeTicket()
        {
            try
            {
                if (_noelSpikeTicket != null && _noelSpikeMap != null && _noelSpikeMap.MovRenderer != null)
                {
                    _noelSpikeMap.MovRenderer.deassignDrawable(_noelSpikeTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelSpikeMat != null)
                {
                    IN.DestroyOne(_noelSpikeMat);
                }
            }
            catch (Exception)
            {
            }
            _noelSpikeTicket = null;
            _noelSpikeMesh = null;
            _noelSpikeMat = null;
        }

        /// <summary>
        /// 小剑山绘制：矩阵锚定诺艾尔原点一次，每只按相对偏移画；
        /// 贴图循环播 `spike_1~spike_8`，爆炸特效播 `explode_particle0000~0012`。
        /// </summary>
        private static bool PrepareNoelSpikeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelSpikeMap;
            if (mp == null || _noelSpikeMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelSpikeMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null)
            {
                MdOut = _noelSpikeMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.UterusSpikeScale;
            float fps = KnightInCradlePlugin.UterusSpikeFps;
            for (int i = 0; i < _noelSpikes.Count; i++)
            {
                NoelSpikeFollower s = _noelSpikes[i];
                if (_noelSpikeFrames == null || _noelSpikeFrames.Length == 0)
                {
                    break;
                }
                int idx = Mathf.Abs((int)(s.AnimTime * fps)) % _noelSpikeFrames.Length;
                Texture2D tex = _noelSpikeFrames[idx];
                if (tex == null)
                {
                    continue;
                }
                float dxm = (s.X - pr.x) * mp.CLEN;
                float dym = -(s.Y - pr.y) * mp.CLEN;
                float w = tex.width * scale;
                float h = tex.height * scale;
                _noelSpikeMesh.Col = MTRX.ColWhite;
                _noelSpikeMesh.initForImgAndTexture(tex);
                _noelSpikeMesh.uv_top = 0f;
                _noelSpikeMesh.uv_height = 1f;
                if (s.Dir < 0)
                {
                    _noelSpikeMesh.uv_left = 1f;
                    _noelSpikeMesh.uv_width = -1f;
                }
                else
                {
                    _noelSpikeMesh.uv_left = 0f;
                    _noelSpikeMesh.uv_width = 1f;
                }
                _noelSpikeMesh.Rect(dxm, dym, w, h, false);
            }
            // 爆炸特效：6 格宽（同小骑士的爆炸尺寸），锚定命中点
            if (_noelSpikeBooms.Count > 0 && _noelSpikeBoomFrames != null && _noelSpikeBoomFrames.Length > 0)
            {
                float ew = KnightInCradlePlugin.UterusExplosionSize * mp.CLEN;
                float eh = ew * 1.25f; // 帧比例 80:100
                for (int i = 0; i < _noelSpikeBooms.Count; i++)
                {
                    NoelSpikeBoom b = _noelSpikeBooms[i];
                    int idx = Mathf.Clamp((int)(b.AnimTime * NoelSpikeBoomFps), 0, _noelSpikeBoomFrames.Length - 1);
                    Texture2D tex = _noelSpikeBoomFrames[idx];
                    if (tex == null)
                    {
                        continue;
                    }
                    float dxm = (b.X - pr.x) * mp.CLEN;
                    float dym = -(b.Y - pr.y) * mp.CLEN;
                    _noelSpikeMesh.Col = MTRX.ColWhite;
                    _noelSpikeMesh.initForImgAndTexture(tex);
                    _noelSpikeMesh.uv_top = 0f;
                    _noelSpikeMesh.uv_height = 1f;
                    _noelSpikeMesh.uv_left = 0f;
                    _noelSpikeMesh.uv_width = 1f;
                    _noelSpikeMesh.Rect(dxm, dym, ew, eh, false);
                }
            }
            MdOut = _noelSpikeMesh;
            return true;
        }

        private static bool EnsureNoelSpikeTextures()
        {
            if (_noelSpikeFrames != null && _noelSpikeBoomFrames != null)
            {
                return true;
            }
            if (_noelSpikeLoadTried)
            {
                return _noelSpikeFrames != null && _noelSpikeBoomFrames != null;
            }
            _noelSpikeLoadTried = true;
            try
            {
                string root = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets");
                _noelSpikeFrames = LoadNoelPngFrames(System.IO.Path.Combine(root, "spike"), NoelSpikeSprites);
                var boom = new List<string>();
                for (int i = 0; i < 13; i++)
                {
                    boom.Add("explode_particle" + i.ToString("D4"));
                }
                _noelSpikeBoomFrames = LoadNoelPngFrames(
                    System.IO.Path.Combine(root, "explosion", "sprites"), boom.ToArray());
                if (_noelSpikeFrames == null)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][发光子宫] 没找到小剑山素材（assets/hk/sheets/spike/spike_1~8.png），小剑山不显示");
                }
                return _noelSpikeFrames != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>从指定目录按名字读一组 PNG 帧（缺帧就跳过）。</summary>
        private static Texture2D[] LoadNoelPngFrames(string dir, string[] names)
        {
            try
            {
                var list = new List<Texture2D>();
                for (int i = 0; i < names.Length; i++)
                {
                    string path = System.IO.Path.Combine(dir, names[i] + ".png");
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
                    {
                        UnityEngine.Object.Destroy(tex);
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    list.Add(tex);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 护符23 吸虫之巢（诺艾尔侧）：
        /// 诺艾尔施放**纯白之箭**（`MGKIND.WHITEARROW`）/ **聚能火球**（`MGKIND.FIREBALL`）时，
        /// 不再产生原版子弹，而是沿发射方向喷出一群**黑色吸虫**（每只 7 点真实伤害；
        /// 同时佩戴萨满之石则每只 9 点）。吸虫的初速度、位置偏移、重力、落地弹跳、
        /// 命中判定与贴图全部沿用小骑士那一套（HK SpellFluke 风格）。
        ///
        /// **挂点**：`M2PrSkill.explodeMagic(MagicItem, out MagicItem)` 的后缀
        /// （`nel/M2PrSkill.cs:3611`，法术"起飞"的那一步）。用后缀而不是前缀，是为了让原版结算完整走完
        /// （耗魔、清蓄力 `mp_hold = 0`、`CurMg = null`、状态机切 `MAG_EXPLODED` 都不受影响），
        /// 拿到产出的子弹后再把它收掉，并在同一帧生成吸虫。
        /// </summary>
        private const float NestFlukeGravity = 28f;     // 重力（格/秒²），同小骑士
        private const float NestFlukeLifeMin = 4f;      // 寿命 4~5 秒，同小骑士
        private const float NestFlukeLifeMax = 5f;
        private const float NestFlukeHitRadius = 0.675f;
        private const float NestFlukeScale = 0.24f;     // 贴图渲染缩放，同小骑士
        private const float NestFlukeFps = 12f;         // 空中/扑腾动画帧率，同小骑士

        private sealed class NoelFluke
        {
            public float X;
            public float Y;
            public float Vx;
            public float Vy;
            public float Life;
            public float AnimTime;
            public float Dir;
            public bool Flopping;
        }

        private static readonly List<NoelFluke> _noelFlukes = new List<NoelFluke>();

        private static readonly string[] NoelFlukeAirSprites =
        {
            "black_fluke_air0000", "black_fluke_air0001", "black_fluke_air0002",
            "black_fluke_air0003", "black_fluke_air0004", "black_fluke_air0005",
        };

        private static readonly string[] NoelFlukeFlopSprites =
        {
            "fluke_charm_black_flukes0000", "fluke_charm_black_flukes0001", "fluke_charm_black_flukes0002",
            "fluke_charm_black_flukes0003", "fluke_charm_black_flukes0004", "fluke_charm_black_flukes0005",
            "fluke_charm_black_flukes0006", "fluke_charm_black_flukes0007", "fluke_charm_black_flukes0008",
            "fluke_charm_black_flukes0009", "fluke_charm_black_flukes0010", "fluke_charm_black_flukes0011",
        };

        private static Texture2D[] _noelFlukeAirTex;
        private static Texture2D[] _noelFlukeFlopTex;
        private static bool _noelFlukeLoadTried;
        private static MeshDrawer _noelFlukeMesh;
        private static Material _noelFlukeMat;
        private static M2RenderTicket _noelFlukeTicket;
        private static Map2d _noelFlukeMap;

        /// <summary>当前每只吸虫的伤害：带萨满之石 9，否则 7。</summary>
        private static int NoelFlukeDamageNow()
        {
            // 表格：吸虫吃 5 萨满（7→9 的独立规则）、16 沉重之击(会心)、20 亡者之怒；不吃 13 坚固力量。
            int baseDmg = IsEquipped(CharmOwner.Noel, ShamanId)
                ? KnightInCradlePlugin.NestFlukeDamageWithShaman
                : KnightInCradlePlugin.NestFlukeDamage;
            return Mathf.Max(1, Mathf.FloorToInt(baseDmg * NoelSideDamageMult(false) + 0.5f));
        }

        /// <summary>
        /// 放出吸虫（同小骑士）：初速度 12~16 格/秒向前、Y 方向 -8~1 的随机上抛，
        /// 位置在身前 0.3~1.3 格、上下 -0.3~0.7 格范围内随机散开。
        /// </summary>
        private static void SpawnNoelFlukes(PRNoel pr, int count)
        {
            try
            {
                if (pr == null || count <= 0)
                {
                    return;
                }
                float dir;
                try
                {
                    int aimX = CAim._XD(pr.getAimForCaster(), 1);
                    dir = aimX != 0 ? aimX : (pr.mpf_is_right >= 0f ? 1f : -1f);
                }
                catch (Exception)
                {
                    dir = pr.mpf_is_right >= 0f ? 1f : -1f;
                }
                for (int i = 0; i < count; i++)
                {
                    float speedMult = KnightInCradlePlugin.NestFlukeSpeedMult;
                    var fl = new NoelFluke
                    {
                        Dir = dir,
                        X = pr.x + dir * UnityEngine.Random.Range(0.3f, 1.3f),
                        Y = pr.y + UnityEngine.Random.Range(-0.3f, 0.7f),
                        // 发射初速度（需求：整体 ×1.25，落地弹跳速度不受影响）
                        Vx = dir * UnityEngine.Random.Range(12f, 16f) * speedMult,
                        Vy = UnityEngine.Random.Range(-8f, 1f) * speedMult,
                        Life = UnityEngine.Random.Range(NestFlukeLifeMin, NestFlukeLifeMax),
                        AnimTime = UnityEngine.Random.Range(0f, 1f), // 错开动画相位
                    };
                    _noelFlukes.Add(fl);
                }
                try
                {
                    DashAudio.PlayFlukeCast();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进吸虫：重力、落地弹跳、命中判定、寿命与票据维护。</summary>
        public static void TickNoelNestCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                if (IsKnightMode)
                {
                    if (_noelFlukes.Count > 0)
                    {
                        _noelFlukes.Clear();
                    }
                    ReleaseNoelFlukeTicket();
                    return;
                }
                float dt = Time.deltaTime;
                for (int i = _noelFlukes.Count - 1; i >= 0; i--)
                {
                    NoelFluke fl = _noelFlukes[i];
                    fl.Life -= dt;
                    if (fl.Life <= 0f)
                    {
                        _noelFlukes.RemoveAt(i);
                        continue;
                    }
                    fl.AnimTime += dt;
                    fl.Vy += NestFlukeGravity * dt;
                    fl.X += fl.Vx * dt;
                    fl.Y += fl.Vy * dt;
                    // 落地弹跳：脚部贴地时随机横向速度 + 向上弹起（同小骑士 / HK SpellFluke）
                    float gy;
                    if (fl.Vy > 0f && NoelFlukeGroundY(pr.Mp, fl.Y + 0.35f, fl.X, out gy))
                    {
                        float feet = fl.Y + 0.35f;
                        if (feet >= gy - 0.35f && feet <= gy + 0.55f)
                        {
                            fl.Y = gy - 0.35f;
                            fl.Vx = UnityEngine.Random.Range(-4f, 4f);
                            fl.Vy = -UnityEngine.Random.Range(6f, 12f);
                            fl.Flopping = true;
                            fl.AnimTime = 0f;
                            try
                            {
                                DashAudio.PlayFlukeBounce();
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                    else if (fl.Flopping && fl.AnimTime >= NoelFlukeFlopDuration())
                    {
                        fl.Flopping = false;
                    }
                    if (HitEnemyByNoelFluke(pr, fl))
                    {
                        _noelFlukes.RemoveAt(i); // 碰到敌人即消失
                    }
                }
                EnsureNoelFlukeTicket(pr, _noelFlukes.Count > 0);
            }
            catch (Exception)
            {
            }
        }

        private static float NoelFlukeFlopDuration()
        {
            return Mathf.Max(0.01f, NoelFlukeFlopSprites.Length / NestFlukeFps);
        }

        /// <summary>吸虫脚部是否踩到地面（复用地图 BCC 地板检测，同小骑士）。</summary>
        private static bool NoelFlukeGroundY(Map2d mp, float feetY, float x, out float groundTop)
        {
            groundTop = float.NaN;
            if (mp == null || mp.BCC == null)
            {
                return false;
            }
            try
            {
                BCCLine line;
                float g = mp.BCC.isFallable(x, feetY - 0.30f, 0.15f, 0.35f, out line, true, true, -1f, null);
                if (g >= 0f)
                {
                    groundTop = g;
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>吸虫碰到敌人：固定真实伤害（7 / 带萨满之石 9）后消失。</summary>
        private static bool HitEnemyByNoelFluke(PRNoel pr, NoelFluke fl)
        {
            try
            {
                Map2d mp = pr.Mp;
                if (mp == null)
                {
                    return false;
                }
                int mask = NoelEnemyOverlapMask();
                if (mask == 0)
                {
                    return false;
                }
                float mx = mp.pixel2ux(fl.X * mp.CLEN);
                float my = mp.pixel2uy(fl.Y * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, NestFlukeHitRadius, mask);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null || !enemy.is_alive || IsEnemySummoning(enemy))
                    {
                        continue; // 生成中的魔物不挡吸虫（吸虫继续飞）
                    }
                    int dmg = NoelFlukeDamageNow();
                    var atk = new NelAttackInfo();
                    atk.hpdmg0 = dmg;
                    atk.hpdmg_current = dmg;
                    atk.fix_damage = true; // 真伤：不吃敌人减伤/浮动
                    atk.Caster = pr;
                    atk.AttackFrom = pr;
                    atk.CenterXy(enemy.x, enemy.y, 0f);
                    enemy.applyDamage(atk, false);
                    // 需求：命中后有一定概率再造成一次同样的伤害（小骑士版本是 25%）
                    if (UnityEngine.Random.value < KnightInCradlePlugin.NestFlukeDoubleHitChance)
                    {
                        atk.hpdmg0 = dmg;
                        atk.hpdmg_current = dmg;
                        atk.CenterXy(enemy.x, enemy.y, 0f);
                        enemy.applyDamage(atk, false);
                    }
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>吸虫票据（身前层 PR1，同小骑士的吸虫）。</summary>
        private static void EnsureNoelFlukeTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelFlukeTicket();
                return;
            }
            if (!EnsureNoelFlukeTextures())
            {
                return; // 素材缺失：只是不显示，伤害照常
            }
            if (_noelFlukeMesh != null && _noelFlukeMap == mp && _noelFlukeTicket != null)
            {
                return;
            }
            ReleaseNoelFlukeTicket();
            _noelFlukeMap = mp;
            _noelFlukeMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _noelFlukeMesh.draw_gl_only = true;
            _noelFlukeMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelFlukeMat.EnableKeyword("NO_PIXELSNAP");
            _noelFlukeMesh.activate("noel_fluke", _noelFlukeMat, false, MTRX.ColWhite, null);
            _noelFlukeTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, PrepareNoelFlukeMesh, _noelFlukeMesh, null, null);
        }

        private static void ReleaseNoelFlukeTicket()
        {
            try
            {
                if (_noelFlukeTicket != null && _noelFlukeMap != null && _noelFlukeMap.MovRenderer != null)
                {
                    _noelFlukeMap.MovRenderer.deassignDrawable(_noelFlukeTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelFlukeMat != null)
                {
                    IN.DestroyOne(_noelFlukeMat);
                }
            }
            catch (Exception)
            {
            }
            _noelFlukeTicket = null;
            _noelFlukeMesh = null;
            _noelFlukeMat = null;
            _noelFlukeMap = null;
        }

        /// <summary>
        /// 吸虫绘制（同小骑士）：矩阵锚定诺艾尔原点一次，每只吸虫按相对偏移绘制；
        /// 空中播 6 帧循环、落地瞬间播 12 帧扑腾，按发射方向镜像。
        /// </summary>
        private static bool PrepareNoelFlukeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelFlukeMap;
            if (mp == null || _noelFlukeMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelFlukeMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelFlukes.Count == 0)
            {
                MdOut = _noelFlukeMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            for (int i = 0; i < _noelFlukes.Count; i++)
            {
                NoelFluke fl = _noelFlukes[i];
                Texture2D tex;
                if (fl.Flopping && _noelFlukeFlopTex != null && _noelFlukeFlopTex.Length > 0)
                {
                    int idx = Mathf.Clamp((int)(fl.AnimTime * NestFlukeFps), 0, _noelFlukeFlopTex.Length - 1);
                    tex = _noelFlukeFlopTex[idx];
                }
                else if (_noelFlukeAirTex != null && _noelFlukeAirTex.Length > 0)
                {
                    int idx = Mathf.Abs((int)(fl.AnimTime * NestFlukeFps)) % _noelFlukeAirTex.Length;
                    tex = _noelFlukeAirTex[idx];
                }
                else
                {
                    continue;
                }
                if (tex == null)
                {
                    continue;
                }
                float dxm = (fl.X - pr.x) * mp.CLEN;
                float dym = -(fl.Y - pr.y) * mp.CLEN;
                float w = tex.width * NestFlukeScale;
                float h = tex.height * NestFlukeScale;
                _noelFlukeMesh.Col = MTRX.ColWhite;
                _noelFlukeMesh.initForImgAndTexture(tex);
                _noelFlukeMesh.uv_top = 0f;
                _noelFlukeMesh.uv_height = 1f;
                if (fl.Dir > 0f)
                {
                    _noelFlukeMesh.uv_left = 1f;
                    _noelFlukeMesh.uv_width = -1f;
                }
                else
                {
                    _noelFlukeMesh.uv_left = 0f;
                    _noelFlukeMesh.uv_width = 1f;
                }
                _noelFlukeMesh.Rect(dxm, dym, w, h, false);
            }
            MdOut = _noelFlukeMesh;
            return true;
        }

        /// <summary>吸虫贴图：`assets/hk/sheets/nest/sprites/`（与小骑士同一批帧）。</summary>
        private static bool EnsureNoelFlukeTextures()
        {
            if (_noelFlukeAirTex != null && _noelFlukeFlopTex != null)
            {
                return true;
            }
            if (_noelFlukeLoadTried)
            {
                return _noelFlukeAirTex != null && _noelFlukeFlopTex != null;
            }
            _noelFlukeLoadTried = true;
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "nest", "sprites");
                _noelFlukeAirTex = LoadNoelFlukeFrames(dir, NoelFlukeAirSprites);
                _noelFlukeFlopTex = LoadNoelFlukeFrames(dir, NoelFlukeFlopSprites);
                if (_noelFlukeAirTex == null && _noelFlukeFlopTex == null)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][吸虫之巢] 没找到吸虫素材（assets/hk/sheets/nest/sprites），吸虫不显示");
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Texture2D[] LoadNoelFlukeFrames(string dir, string[] names)
        {
            try
            {
                var list = new List<Texture2D>();
                for (int i = 0; i < names.Length; i++)
                {
                    string path = System.IO.Path.Combine(dir, names[i] + ".png");
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
                    {
                        UnityEngine.Object.Destroy(tex);
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    list.Add(tex);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 护符23：诺艾尔放出纯白之箭 / 聚能火球时，收掉原版子弹、改为喷出吸虫。
        ///
        /// 挂点：`MagicItem.explode(bool do_not_run1)` 的**后缀**（`MagicItem.cs:805`）。
        /// 为什么用这个而不是 `M2PrSkill.explodeMagic`：后者把产物放在 `out` 参数里，
        /// 而这里的返回值 `__result` 就是"这一发原版子弹"，语义一样但更好挂。
        /// 为什么必须判 `do_not_run1`：全游戏只有 `M2PrSkill.explodeMagic`（放法术）会传 `true`
        /// （`M2PrSkill.cs:3661`），子弹自己被打掉时走的是 `explode(false)`（`MagicItem.kill`）——
        /// 不判这个，子弹每次消失都会再喷一群吸虫。
        ///
        /// 处理顺序（保证原版结算不炸）：
        /// ① 先 `killHoldMagic(false,false,false)` 清掉蓄力 —— 这样上级拿到的 `_Expl == null`
        ///    分支里 `fineHoldMagicTime` 走到的是"mp_hold ≤ 0 → 清 CurMg"的正常收尾
        ///    （否则 `reduce_mp` 已被 explode 清零，会算 `casttime * mp_hold / 0` 出 NaN）；
        /// ② 收掉原版子弹并把返回值置 null（上级会走它自己"这一发没成型"的既有分支：
        ///    `M2PrSkill.cs:3662-3666`，与"CurMg 已失效"同一条路）；
        /// ③ 同一帧喷出吸虫。
        /// 魔力已在 `explodeMagic` 里扣过（`:3657-3660`），这里不再重复扣。
        /// </summary>
        private static void NoelNestMagicExplodePostfix(MagicItem __instance, bool do_not_run1,
            ref MagicItem __result)
        {
            try
            {
                if (!do_not_run1 || IsKnightMode || !IsEquipped(CharmOwner.Noel, NestId))
                {
                    return;
                }
                if (__instance == null || !(__instance.Caster is PRNoel pr))
                {
                    return;
                }
                int count;
                if (__instance.kind == MGKIND.WHITEARROW)
                {
                    count = KnightInCradlePlugin.NestArrowFlukeCount;
                }
                else if (__instance.kind == MGKIND.FIREBALL)
                {
                    count = KnightInCradlePlugin.NestFireballFlukeCount;
                }
                else
                {
                    return;
                }
                try
                {
                    if (pr.Skill != null)
                    {
                        pr.Skill.killHoldMagic(false, false, false); // 清蓄力（不返还）
                    }
                }
                catch (Exception)
                {
                }
                MagicItem proj = __result;
                __result = null; // 让上级知道"这一发没有子弹"
                if (proj != null)
                {
                    try
                    {
                        proj.kill(-1f); // 收掉原版子弹（不飞、不判定）
                    }
                    catch (Exception)
                    {
                    }
                }
                SpawnNoelFlukes(pr, count);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符21 苦痛荆棘（诺艾尔侧）效果①：诺艾尔不会受到**场景中荆棘/尖刺**的伤害。
        ///
        /// AIC 的棘刺伤害是"地图危险区"：地图 chip 带 `mapdmg` meta 时由 `BCCLine` 建成
        /// `M2MapDamageContainer` 的条目（`MAPDMG.SPIKE` = トゲ/棘刺；`MDAT.cs:206` 那段），
        /// 物理体触碰时走 `M2Attackable.applyDamageFromMap` → `PR.applyDamageFromMap`
        /// （`nel/PR.cs:3100`，是玩家侧的 override）。这里给它的前缀直接返回 null（=没造成伤害），
        /// 于是尖刺/荆棘的掉血、击退、僵直全部不发生；雷霆/火/岩浆等其它地图伤害不受影响。
        /// 只对本地诺艾尔 + 诺艾尔模式 + 佩戴苦痛荆棘时生效。
        /// </summary>
        private static bool ThornsMapDamagePrefix(PR __instance, M2MapDamageContainer.M2MapDamageItem MDI,
            ref AttackInfo __result)
        {
            try
            {
                if (IsKnightMode || MDI == null || !(__instance is PRNoel))
                {
                    return true;
                }
                // 护符20 亡者之怒 效果3：亡者之怒期间免疫地图上**所有**危险格
                // （尖刺 / 荆棘 / 虫墙拉扯之外的岩浆、雷电、火焰等一并作废）。
                if (IsNoelFuryImmune)
                {
                    __result = null;
                    return false;
                }
                // 护符21 苦痛荆棘 效果1：只免疫棘刺类（SPIKE）
                if (MDI.kind != MAPDMG.SPIKE || !IsEquipped(CharmOwner.Noel, ThornsId))
                {
                    return true;
                }
                __result = null; // 这一次地图伤害作废
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 护符9 幼虫之歌 / 护符10 蜕变挽歌 / 护符34 乌恩之形（诺艾尔侧）：
        /// 诺艾尔不会被虫墙/虫巢抓取（与小骑士侧一致）。
        /// `M2WormTrap` 决定是否拉扯玩家时读 `PR.canPullByWorm()`（`nel/M2WormTrap.cs:111,148`），
        /// 这里对本地诺艾尔直接返回 false。
        /// 骑士模式下另有 CombatGuard 的同名补丁（它会先返回 false 拦掉），两者按各自模式生效、互不冲突。
        /// </summary>
        private static bool GrubsongCanPullByWormPrefix(PR __instance, ref bool __result)
        {
            try
            {
                if (IsKnightMode || !(__instance is PRNoel) ||
                    !(IsEquipped(CharmOwner.Noel, GrubsongId) || IsEquipped(CharmOwner.Noel, ElegyId) ||
                      IsEquipped(CharmOwner.Noel, UnnId) || IsEquipped(CharmOwner.Noel, FuryId)))
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
        // ========== 护符34 乌恩之形（诺艾尔侧） ==========
        /// <summary>乌恩之形：该 PR 是否就是"佩戴了乌恩之形的本地诺艾尔"。</summary>
        private static bool IsUnnApplied(PR pr)
        {
            if (pr == null || IsKnightMode || !IsEquipped(CharmOwner.Noel, UnnId))
            {
                return false;
            }
            PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
            return noel != null && ReferenceEquals(pr, noel);
        }

        /// <summary>
        /// 受伤反应状态（摔倒 / 后仰 / 撞墙 / 被撞倒）。亡者之怒期间一律不进入：
        /// ① 亡者之怒本来就免疫全部魔物伤害，不该再摔倒；
        /// ② 这些状态是**伤害管线在 HP 结算之后**才切的（`M2PrADmg.applyDamage` :1315 / :1380），
        ///    而圣光爆发是在 HP 结算**当中**切进 `STATE.BURST` 的 —— 不挡掉就会被它们顶掉，
        ///    表现就是"触发了亡者之怒但看不到圣光爆发"。
        /// </summary>
        private static bool IsDamageReactionState(PR.STATE s)
        {
            switch (s)
            {
                case PR.STATE.DAMAGE:
                case PR.STATE.DAMAGE_L:
                case PR.STATE.DAMAGE_LT:
                case PR.STATE.DAMAGE_LT_KIRIMOMI:
                case PR.STATE.DAMAGE_L_LAND:
                case PR.STATE.DAMAGE_L_HITWALL:
                case PR.STATE.DAMAGE_L_DOWN_ABSORBAFTER:
                case PR.STATE.ENEMY_SINK:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>诺艾尔是否处于"蹲下/爬行"（蹲着左右移动就是爬行）。</summary>
        public static bool IsNoelCrouchOrCrawl(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return false;
                }
                return pr.view_crouching || pr.forceCrouch(false, false) || pr.isPoseCrouch(false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>效果2 是否生效：佩戴乌恩之形 + 诺艾尔模式 + 正蹲着/爬行。</summary>
        public static bool UnnFriendlyActive()
        {
            try
            {
                if (IsKnightMode || !IsEquipped(CharmOwner.Noel, UnnId))
                {
                    return false;
                }
                return IsNoelCrouchOrCrawl(KnightInCradleBehaviour.GetPrPublic());
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 效果3（追加 2026-09-25）：蹲下 / 爬行时**持续回复生命值**，默认 5 HP/秒。
        ///
        /// 回血同样走 AIC 原生的 `PR.cureHp` —— 佩戴乔尼的祝福时它会被改写成回魔
        /// （与护符31 蜂巢之血一个口径），所以这条不需要额外分支。
        /// 小数部分累积不丢；HP 已满时不积累（不浪费）；起身立即清零累积量。
        /// </summary>
        public static void TickUnnCrouchHeal(PRNoel pr)
        {
            try
            {
                float perSecond = KnightInCradlePlugin.UnnCrouchHealPerSecond;
                if (perSecond <= 0f || pr == null || !pr.is_alive || !UnnFriendlyActive())
                {
                    _unnCrouchHealAccum = 0f;
                    _unnCrouchMpAccum = 0f;
                    return;
                }
                if (PrMaxHpField == null || PrHpField == null)
                {
                    return;
                }
                // 羁绊 26+34（快速聚集 + 乌恩之形）：蹲下/爬行时**也按同样速率回魔**。
                // 与 HP 分开累加：HP 满的时候也要继续回魔。
                if (IsEquipped(CharmOwner.Noel, FastGatherId) && PrMpField != null && PrMaxMpField != null)
                {
                    int maxMpBond = (int)PrMaxMpField.GetValue(pr);
                    int mpNow = (int)PrMpField.GetValue(pr);
                    if (maxMpBond > 0 && mpNow < maxMpBond)
                    {
                        _unnCrouchMpAccum += perSecond * Time.deltaTime;
                        int gainMp = Mathf.FloorToInt(_unnCrouchMpAccum);
                        if (gainMp > 0)
                        {
                            _unnCrouchMpAccum -= gainMp;
                            if (KnightInCradleBehaviour.GrantNoelMana(gainMp))
                            {
                                RefreshNoelHudMp();
                            }
                        }
                    }
                    else
                    {
                        _unnCrouchMpAccum = 0f;
                    }
                }
                else
                {
                    _unnCrouchMpAccum = 0f;
                }
                int maxHp = (int)PrMaxHpField.GetValue(pr);
                int hp = (int)PrHpField.GetValue(pr);
                if (maxHp <= 0 || hp >= maxHp)
                {
                    _unnCrouchHealAccum = 0f;
                    return;
                }
                _unnCrouchHealAccum += perSecond * Time.deltaTime;
                int heal = Mathf.FloorToInt(_unnCrouchHealAccum);
                if (heal <= 0)
                {
                    return;
                }
                _unnCrouchHealAccum -= heal;
                if (hp + heal > maxHp)
                {
                    heal = maxHp - hp;
                }
                if (heal <= 0)
                {
                    return;
                }
                pr.cureHp(heal);
                RefreshNoelHudHp();
                RefreshNoelHudMp();
            }
            catch (Exception)
            {
            }
        }

        private static float _unnCrouchHealAccum;
        /// <summary>羁绊 26+34：蹲下/爬行回复魔力的累加器（与 HP 那份分开算）。</summary>
        private static float _unnCrouchMpAccum;

        // ==================== 护符35 骨钉大师的荣耀（诺艾尔侧） ====================
        private enum NailMasterPhase
        {
            None,
            Charging,  // 长按攻击键蓄力中（黄色粒子）
            Charged,   // 蓄力完成（含 nail_charge_effect 光圈）
            Spin,      // 旋风斩：attack_air1 → attack_air2 循环，方向键平移
            SpinOutro, // 收尾：attack_air3
        }
        private static NailMasterPhase _nmPhase = NailMasterPhase.None;
        private static float _nmTimer;
        /// <summary>旋风斩期间锁重力的 key。</summary>
        private static readonly object NailMasterGravityKey = new object();
        // ---- "点按攻击键"识别（护符35 压制了突进冲击/凌空横斩，点按要由模组补发）----
        private static bool _nmAtkHeldLast;
        private static bool _nmTapTracking;
        private static float _nmTapTimer;
        private static bool _nmTapWasAir;
        private static bool _nmTapWasRun;
        // ---- 旋风斩：无敌窗口 + 自绘圆形判定箱 ----
        /// <summary>圆内敌人的"下次可再吃一次伤害"的时刻（`Time.time`）。</summary>
        private static readonly Dictionary<NelEnemy, float> _nmCircleNextHit =
            new Dictionary<NelEnemy, float>();
        private static Texture2D _nmCircleTex;
        private static MeshDrawer _nmCircleMesh;
        private static Material _nmCircleMat;
        private static M2RenderTicket _nmCircleTicket;
        private static Map2d _nmCircleMap;

        /// <summary>
        /// 是否处于"旋风斩无敌帧"：只有 `attack_air2`（循环段）无敌，
        /// 起手 `attack_air1` 与收尾 `attack_air3` 照常会被打/被抓（需求 2026-09-26）。
        /// </summary>
        public static bool NoelSpinInvincible =>
            _nmPhase == NailMasterPhase.Spin &&
            _nmTimer >= KnightInCradlePlugin.NailMasterSpinIntroSeconds;

        /// <summary>旋风斩（含收尾动作）是否进行中。</summary>
        public static bool NailMasterSpinActive =>
            _nmPhase == NailMasterPhase.Spin || _nmPhase == NailMasterPhase.SpinOutro;

        /// <summary>护符35 是否装备在本地诺艾尔身上（诺艾尔模式）。</summary>
        public static bool NailMasterEquipped =>
            !IsKnightMode && IsEquipped(CharmOwner.Noel, NailMasterId);

        /// <summary>护符35 骨钉大师的荣耀：该伤害包是不是"诺艾尔的骨钉系攻击"。</summary>
        private static bool IsNoelNailAttack(AttackInfo Atk)
        {
            try
            {
                NelAttackInfo nAtk = Atk as NelAttackInfo;
                if (nAtk == null)
                {
                    return false;
                }
                MagicItem mg = nAtk.PublishMagic;
                return mg != null && IsNailMasterFixedKind(mg.kind) &&
                       (mg.Caster is PRNoel || nAtk.Caster is PRNoel || nAtk.AttackFrom is PRNoel);
            }
            catch (Exception)
            {
                return false;
            }
        }
        /// <summary>护符35 的"固定伤害"覆盖哪些招式（自己的骨钉系攻击，不含法术）。</summary>
        private static bool IsNailMasterFixedKind(MGKIND kind)
        {
            switch (kind)
            {
                case MGKIND.PR_PUNCH:
                case MGKIND.PR_SHOTGUN:
                case MGKIND.PR_WHEEL:
                case MGKIND.PR_COMET:
                case MGKIND.PR_DASHPUNCH:
                case MGKIND.PR_SMASH:
                case MGKIND.PR_EVADECOUNTER:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>蓄力完成状态（决定是否画 nail_charge_effect 光圈）。</summary>
        public static bool NailMasterCharged => _nmPhase == NailMasterPhase.Charged;

        /// <summary>
        /// 护符35 骨钉大师的荣耀（诺艾尔侧）：
        /// ① 锁魔法键 X（见 `NoelShadowChantInputLockPrefix`）；
        /// ② 长按**攻击键**蓄力：黄色粒子（照搬护符33 那套），**蓄力期间可自由行动**，
        ///    `NailMasterChargeSeconds`（默认 1 秒）后完成，完成时出现 `nail_charge_effect` 光圈；
        /// ③ 松开攻击键进入**旋风斩**：起手 `attack_air1` → 循环 `attack_air2`（沿用原版旋风斩的动作名），
        ///    期间上下左右键不再控制跳跃/蹲下/移动，而是**直接平移**诺艾尔（锁重力、可 8 方向飞），
        ///    持续 `SpinSeconds`（默认 2 秒）→ `attack_air3` 收尾 → 恢复。
        /// </summary>
        public static void TickNoelNailMasterCharm(PRNoel pr)
        {
            try
            {
                if (pr == null || !NailMasterEquipped)
                {
                    EndNailMaster(pr);
                    return;
                }
                bool attackHeld = false;
                bool airborne = false;
                bool running = false;
                try
                {
                    attackHeld = pr.isAtkO(0);
                    airborne = !pr.hasFoot();
                    running = pr.run_continue_time >= 22f;
                }
                catch (Exception)
                {
                    attackHeld = false;
                }
                TickNailMasterTap(pr, attackHeld, airborne, running);
                switch (_nmPhase)
                {
                    case NailMasterPhase.None:
                    case NailMasterPhase.Charging:
                        if (attackHeld)
                        {
                            _nmTimer += Time.deltaTime;
                            // 蓄力粒子：与护符33 同一套（向内收敛）
                            SpawnNoelShadowParticles(KnightInCradlePlugin.ShadowChantParticlesPerFrame, false);
                            if (_nmTimer >= KnightInCradlePlugin.NailMasterChargeSeconds)
                            {
                                _nmPhase = NailMasterPhase.Charged;
                            }
                            else
                            {
                                _nmPhase = NailMasterPhase.Charging;
                            }
                        }
                        else
                        {
                            _nmTimer = 0f;
                            _nmPhase = NailMasterPhase.None;
                        }
                        break;
                    case NailMasterPhase.Charged:
                        if (attackHeld)
                        {
                            EnsureNoelChargeAuraTicket(pr, true); // 蓄满：nail_charge_effect 光圈
                        }
                        else
                        {
                            StartNailMasterSpin(pr);
                        }
                        break;
                    case NailMasterPhase.Spin:
                    case NailMasterPhase.SpinOutro:
                        TickNailMasterSpin(pr);
                        break;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符35：识别"**点按**攻击键"并在松手那帧**补发**被压制掉的那一招
        /// （空中 → 凌空横斩 AIRPUNCH；奔跑中 → 突进冲击 DASHPUNCH）。
        ///
        /// 为什么需要补发：按下那一帧无法预知会不会长按，所以模组一律先用
        /// `M2PrSkill.isEnable` 把这两招压掉（让长按走蓄力）；若在 `TapSeconds`（默认 0.18 秒）
        /// 之内就松手，就判定为点按，由这里按原版规则把状态切过去（原版的状态机自己会结算伤害/动画，
        /// 与 `getPunchVariation` 里 `CurMg != null ? *_SHOTGUN : *` 的写法一致）。
        /// </summary>
        private static void TickNailMasterTap(PRNoel pr, bool attackHeld, bool airborne, bool running)
        {
            try
            {
                bool pressed = attackHeld && !_nmAtkHeldLast;
                bool released = !attackHeld && _nmAtkHeldLast;
                _nmAtkHeldLast = attackHeld;
                if (pressed)
                {
                    _nmTapTracking = airborne || running;
                    _nmTapTimer = 0f;
                    _nmTapWasAir = airborne;
                    _nmTapWasRun = running;
                }
                if (_nmTapTracking && attackHeld)
                {
                    _nmTapTimer += Time.deltaTime;
                }
                if (!released || !_nmTapTracking)
                {
                    if (!attackHeld)
                    {
                        _nmTapTracking = false;
                    }
                    return;
                }
                _nmTapTracking = false;
                if (_nmTapTimer > KnightInCradlePlugin.NailMasterTapSeconds)
                {
                    return; // 长按：交给蓄力，不补发
                }
                bool shotgun = false;
                try
                {
                    shotgun = pr.Skill != null && pr.Skill.getCurMagic() != null;
                }
                catch (Exception)
                {
                    shotgun = false;
                }
                if (_nmTapWasAir && airborne)
                {
                    // 空中点按：若同时按着左右方向键（且脚下有落脚点）→ 原版这条会走"旋风斩击"，
                    // 所以补发也要按原版规则优先给 WHEEL（`getPunchVariation` 空中分支的判定顺序）
                    bool dirHeld = false;
                    bool downHeld = false;
                    bool canStand = false;
                    try
                    {
                        dirHeld = pr.isLO(0, false) || pr.isRO(0, false);
                        downHeld = pr.isBO(0);
                        canStand = pr.canStand((int)pr.x, (int)(pr.mbottom + 0.12f));
                    }
                    catch (Exception)
                    {
                        dirHeld = false;
                        downHeld = false;
                    }
                    if (dirHeld && canStand)
                    {
                        pr.changeState(shotgun ? PR.STATE.WHEEL_SHOTGUN : PR.STATE.WHEEL);
                    }
                    else if (downHeld && canStand)
                    {
                        pr.changeState(shotgun ? PR.STATE.COMET_SHOTGUN : PR.STATE.COMET);
                    }
                    else
                    {
                        pr.changeState(shotgun ? PR.STATE.AIRPUNCH_SHOTGUN : PR.STATE.AIRPUNCH);
                    }
                }
                else if (_nmTapWasRun)
                {
                    pr.changeState(shotgun ? PR.STATE.DASHPUNCH_SHOTGUN : PR.STATE.DASHPUNCH);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void StartNailMasterSpin(PRNoel pr)
        {
            _nmPhase = NailMasterPhase.Spin;
            _nmTimer = 0f;
            EnsureNoelChargeAuraTicket(pr, false); // 收掉光圈
            try
            {
                // 与锁定重力配套：先把残余速度清干净（原版旋风斩也是这么起手的）
                pr.getPhysic()?.killSpeedForce(true, true, true, false, false);
                pr.getPhysic()?.addLockGravity(NailMasterGravityKey, 0f, -1f);
            }
            catch (Exception)
            {
            }
        }

        private static void TickNailMasterSpin(PRNoel pr)
        {
            _nmTimer += Time.deltaTime;
            if (_nmPhase == NailMasterPhase.Spin)
            {
                TickNailMasterSpinMove(pr);
                // attack_air2（循环段）阶段：无敌 + 圆形判定箱
                if (_nmTimer >= KnightInCradlePlugin.NailMasterSpinIntroSeconds)
                {
                    ApplyNoelDashImmunity(pr); // 复用同一条"滚动无敌帧"做法
                    CheckNailMasterSpinCircle(pr);
                }
                EnsureNailMasterCircleTicket(pr, NoelSpinInvincible);
                if (_nmTimer >= KnightInCradlePlugin.NailMasterSpinSeconds)
                {
                    _nmPhase = NailMasterPhase.SpinOutro;
                    _nmTimer = 0f;
                }
                return;
            }
            // SpinOutro：只播 attack_air3，不再平移
            if (_nmTimer >= KnightInCradlePlugin.NailMasterSpinOutroSeconds)
            {
                EndNailMaster(pr);
            }
        }

        /// <summary>旋风斩期间：方向键直接平移（8 方向、锁重力、带墙检测）。</summary>
        private static void TickNailMasterSpinMove(PRNoel pr)
        {
            try
            {
                float speed = KnightInCradlePlugin.NailMasterSpinMoveSpeed;
                if (speed <= 0f)
                {
                    return;
                }
                float dx = 0f;
                float dy = 0f;
                if (IN.isRO(0))
                {
                    dx += 1f;
                }
                if (IN.isLO(0))
                {
                    dx -= 1f;
                }
                if (IN.isBO(0))
                {
                    dy += 1f; // y 向下为正
                }
                if (IN.isTO(0))
                {
                    dy -= 1f;
                }
                if (dx == 0f && dy == 0f)
                {
                    return;
                }
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                float step = speed / 60f; // 格/帧@60（与冲刺同一个口径）
                pr.walkBy(FOCTYPE.WALK, dx / len * step, dy / len * step, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>结束旋风斩/蓄力：解锁重力、收掉光圈。</summary>
        private static void EndNailMaster(PRNoel pr)
        {
            if (_nmPhase != NailMasterPhase.None)
            {
                try
                {
                    pr?.getPhysic()?.remLockGravity(NailMasterGravityKey);
                }
                catch (Exception)
                {
                }
                try
                {
                    EnsureNoelChargeAuraTicket(pr, false);
                }
                catch (Exception)
                {
                }
            }
            _nmCircleNextHit.Clear();
            try
            {
                ReleaseNailMasterCircleTicket();
            }
            catch (Exception)
            {
            }
            _nmPhase = NailMasterPhase.None;
            _nmTimer = 0f;
        }

        /// <summary>旋风斩圆形判定箱的圆心（世界格坐标；X 偏移随朝向）。</summary>
        private static void GetNailMasterCircleCenter(PRNoel pr, out float cx, out float cy)
        {
            float dir = pr != null && pr.mpf_is_right < 0f ? -1f : 1f;
            cx = (pr != null ? pr.x : 0f) + dir * KnightInCradlePlugin.NailMasterCircleOffsetX;
            cy = (pr != null ? NoelBodyCenterY(pr) : 0f) + KnightInCradlePlugin.NailMasterCircleOffsetY;
        }

        /// <summary>
        /// 旋风斩（`attack_air2` 段）的圆形判定箱：
        /// 碰到圈的敌人**立刻**吃一次"轻攻击"伤害；之后在圈内**每停留 `SpinCircleHitSeconds`（默认 0.2 秒）**
        /// 再吃一次；离开圈子后登记清除（再进圈会重新按"立刻一次"结算）。
        /// </summary>
        private static void CheckNailMasterSpinCircle(PRNoel pr)
        {
            try
            {
                Map2d mp = pr != null ? pr.Mp : null;
                if (mp == null)
                {
                    return;
                }
                int mask = NoelEnemyOverlapMask();
                if (mask == 0)
                {
                    return;
                }
                float cx;
                float cy;
                GetNailMasterCircleCenter(pr, out cx, out cy);
                float radius = KnightInCradlePlugin.NailMasterCircleRadius;
                Vector2 center = mp.gameObject.transform.TransformPoint(
                    new Vector2(mp.pixel2ux(cx * mp.CLEN), mp.pixel2uy(cy * mp.CLEN)));
                // ⚠ 这里的物理世界单位与"格"是 1:1（护符21 苦痛荆棘 / 护符24 防御者纹章都是直接用格数当半径），
                // 早先误乘了 CLEN → 半径变成 100 多格 = 全屏伤害。
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, radius, mask);
                float now = Time.time;
                float interval = KnightInCradlePlugin.NailMasterCircleHitSeconds;
                // 先把"这帧不在圈里"的登记清掉（离开后重新进圈 → 立刻再吃一次）
                if (_nmCircleNextHit.Count > 0)
                {
                    var stale = new List<NelEnemy>();
                    foreach (KeyValuePair<NelEnemy, float> kv in _nmCircleNextHit)
                    {
                        bool inside = false;
                        for (int i = 0; i < hits.Length; i++)
                        {
                            if (hits[i] != null && ReferenceEquals(hits[i].GetComponentInParent<NelEnemy>(), kv.Key))
                            {
                                inside = true;
                                break;
                            }
                        }
                        if (!inside || kv.Key == null || kv.Key.destructed)
                        {
                            stale.Add(kv.Key);
                        }
                    }
                    for (int i = 0; i < stale.Count; i++)
                    {
                        _nmCircleNextHit.Remove(stale[i]);
                    }
                }
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null)
                    {
                        continue;
                    }
                    float nextHit;
                    if (_nmCircleNextHit.TryGetValue(enemy, out nextHit))
                    {
                        if (now < nextHit)
                        {
                            continue; // 还在间隔里，等下一次
                        }
                    }
                    _nmCircleNextHit[enemy] = now + interval;
                    ApplyNailMasterSpinHit(pr, enemy);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 旋风斩命中一次。需求 2026-09-27 改版：**不再读取轻攻击**，每击固定
        /// `[Charm35] SpinHitDamage`（默认 10）伤害；佩戴护符13 坚固力量时改用
        /// `SpinHitDamageWithPower`（默认 13）。走真伤（`fix_damage`），不吃其它乘区二次放大。
        /// </summary>
        private static void ApplyNailMasterSpinHit(PRNoel pr, NelEnemy enemy)
        {
            try
            {
                if (IsEnemySummoning(enemy))
                {
                    return; // 生成中的魔物不能打
                }
                int dmg = IsEquipped(CharmOwner.Noel, PowerId)
                    ? KnightInCradlePlugin.NailMasterSpinHitDamageWithPower
                    : KnightInCradlePlugin.NailMasterSpinHitDamage;
                dmg = Mathf.Max(1, dmg);
                var atk = new NelAttackInfo();
                atk.fix_damage = true; // 固定伤害，不吃敌人减伤/其它乘区
                atk.Caster = pr;
                atk.hpdmg0 = dmg;
                atk.hpdmg_current = dmg;
                atk._apply_knockback_current = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>绿色圆圈（调试用）：绑当前地图的 MovRenderer，画在诺艾尔身后层 PR0。</summary>
        private static void EnsureNailMasterCircleTicket(PRNoel pr, bool want)
        {
            if (!want || !KnightInCradlePlugin.NailMasterCircleDebug)
            {
                ReleaseNailMasterCircleTicket();
                return;
            }
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (_nmCircleTex == null)
            {
                _nmCircleTex = MakeRingTexture(128, 0.06f);
            }
            if (_nmCircleTex == null)
            {
                return;
            }
            if (_nmCircleMesh != null && _nmCircleMap == mp && _nmCircleTicket != null)
            {
                return;
            }
            ReleaseNailMasterCircleTicket();
            _nmCircleMap = mp;
            _nmCircleMesh = new MeshDrawer(null, 4, 6);
            _nmCircleMesh.draw_gl_only = true;
            _nmCircleMat = MTRX.newMtr(MTRX.ShaderGDT);
            _nmCircleMat.EnableKeyword("NO_PIXELSNAP");
            _nmCircleMesh.activate("noel_spin_circle", _nmCircleMat, false, MTRX.ColWhite, null);
            _nmCircleTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNailMasterCircleMesh, _nmCircleMesh, null, null);
        }

        private static void ReleaseNailMasterCircleTicket()
        {
            try
            {
                if (_nmCircleTicket != null && _nmCircleMap != null && _nmCircleMap.MovRenderer != null)
                {
                    _nmCircleMap.MovRenderer.deassignDrawable(_nmCircleTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_nmCircleMat != null)
                {
                    IN.DestroyOne(_nmCircleMat);
                }
            }
            catch (Exception)
            {
            }
            _nmCircleTicket = null;
            _nmCircleMesh = null;
            _nmCircleMat = null;
            _nmCircleMap = null;
        }

        private static bool PrepareNailMasterCircleMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw,
            int draw_id, out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _nmCircleMap;
            if (mp == null || _nmCircleMesh == null || draw_id != 0)
            {
                return false;
            }
            _nmCircleMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _nmCircleTex == null || !NoelSpinInvincible)
            {
                MdOut = _nmCircleMesh;
                return true;
            }
            float cx;
            float cy;
            GetNailMasterCircleCenter(pr, out cx, out cy);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(cx * mp.CLEN), mp.pixel2uy(cy * mp.CLEN), 0f));
            float size = KnightInCradlePlugin.NailMasterCircleRadius * 2f * mp.CLEN;
            _nmCircleMesh.Col = new Color(0f, 1f, 0f, 0.9f); // 绿框
            _nmCircleMesh.initForImgAndTexture(_nmCircleTex);
            _nmCircleMesh.uv_top = 0f;
            _nmCircleMesh.uv_height = 1f;
            _nmCircleMesh.uv_left = 0f;
            _nmCircleMesh.uv_width = 1f;
            _nmCircleMesh.Rect(0f, 0f, size, size, false);
            MdOut = _nmCircleMesh;
            return true;
        }

        /// <summary>程序化生成"空心圆环"贴图（绿色由绘制时的 Col 乘上去）。</summary>
        private static Texture2D MakeRingTexture(int size, float thicknessRatio)
        {
            try
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float half = size * 0.5f;
                float outer = half * 0.98f;
                float inner = outer - half * thicknessRatio;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x + 0.5f - half;
                        float dy = y + 0.5f - half;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = d <= outer && d >= inner ? 1f : 0f;
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply();
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ==================== 护符36 编织者之歌（诺艾尔侧） ====================
        // 做法与小骑士那套一致：3 只小编织者贴地跟随、随机乱跑、偶尔跳，
        // 周期性朝附近敌人发射蛛丝（3 真伤）；近身则每 5 秒一次近战（15 真伤）。
        // 羁绊：**同时携带幼虫之歌**时，小蜘蛛每次命中 → 回复 WeaverGrubsongMp（默认 3）MP。

        /// <summary>
        /// 羁绊 8+36（飞毛腿 + 编织者之歌）：小编织者的攻击间隔缩放。
        /// 同时佩戴飞毛腿时返回 `[Charm36] RunnerBondCooldownScale`（默认 0.75 = 攻速约 +33%），
        /// 否则 1（不变）。
        /// </summary>
        private static float NoelWeaverBondCooldownScale()
        {
            try
            {
                return !IsKnightMode && IsEquipped(CharmOwner.Noel, RunnerId)
                    ? KnightInCradlePlugin.WeaverRunnerBondCooldownScale
                    : 1f;
            }
            catch (Exception)
            {
                return 1f;
            }
        }
        private const int WeaverCount = 3;
        private const float WeaverRespawnDelay = 3f;     // 过图后等待（秒）再生成
        private const int WeaverThreadDamage = 3;        // 蛛丝命中伤害
        private const int WeaverMeleeDamage = 15;        // 近战攻击伤害
        private const float WeaverMeleeRange = 1.6f;     // 近战攻击距离（格）
        private const float WeaverMeleeInterval = 5f;    // 近战冷却（秒）
        private const float WeaverMeleeAnimTime = 0.3f;  // 近战动画时长（秒）
        private const float WeaverFollowSpeed = 12f;     // 跟随爬行速度（格/秒）
        private const float WeaverHalfH = 0.25f;         // 渲染半高（格，用于贴地）
        private const float WeaverMaxRange = 3.2f;       // 离诺艾尔的最大横向距离（格）
        private const float WeaverTooFarRange = 8f;      // 超过该距离直接删除重生
        private const float WeaverWanderMin = 1.2f;
        private const float WeaverWanderMax = 2.6f;
        private const float WeaverHopVy = -8f;           // 跳跃初速（格/秒）
        private const float WeaverHopGravity = 28f;      // 跳跃重力（格/秒²）
        private const float WeaverHopTime = 0.7f;
        private const float WeaverSeekRange = 7f;        // 索敌范围（格）
        private const float WeaverAttackInterval = 2f;   // 蛛丝攻击间隔（秒）
        private const float WeaverAttackAnimTime = 0.33f;
        private const float WeaverThreadSpeed = 18f;     // 蛛丝速度（格/秒）
        private const float WeaverThreadLife = 2.2f;     // 蛛丝最长存活（秒）
        private const float WeaverThreadHitRadius = 0.4f;
        private const float WeaverScale = 0.28f;         // 渲染缩放（贴图约 90px）

        private sealed class NoelWeaver
        {
            public float X;
            public float Y;
            public float Vx;
            public int State;        // 0=出生 1=跟随 2=蛛丝攻击 4=近战
            public float AnimTime;
            public float AttackCd;
            public float MeleeCd;
            public float MeleeAnimT;
            public NelEnemy Target;
            public int Face;         // 1=左 -1=右
            public bool ThreadFired;
            public float WanderT;
            public float WanderX;
            public float HomeOx;     // 跟随停靠偏移（格）：三只固定错开 -1.3 / 0 / +1.3
            public float HopT;
            public float HopVy;
            public float NextHopT;
        }

        private sealed class NoelWeaverThread
        {
            public float X;
            public float Y;
            public float DirX;
            public float DirY;
            public float Life;
            public float Dist;
            public readonly HashSet<NelEnemy> Hits = new HashSet<NelEnemy>();
        }

        private sealed class NoelWeaverClip
        {
            public string[] Frames;
            public float Fps;
        }

        private static readonly List<NoelWeaver> _noelWeavers = new List<NoelWeaver>();
        private static readonly List<NoelWeaverThread> _noelWeaverThreads = new List<NoelWeaverThread>();
        private static float _noelWeaverRespawn;
        private static Map2d _noelWeaverMap;
        private static Dictionary<string, Texture2D> _noelWeaverTex;
        private static Dictionary<string, NoelWeaverClip> _noelWeaverClips;
        private static MeshDrawer _noelWeaverMesh;
        private static Material _noelWeaverMat;
        private static M2RenderTicket _noelWeaverTicket;
        private static Map2d _noelWeaverTicketMap;

        /// <summary>编织者之歌是否生效（本地诺艾尔 + 佩戴36 + 非小骑士模式）。</summary>
        private static bool NoelWeaverActive =>
            !IsKnightMode && IsEquipped(CharmOwner.Noel, SpiderId);

        /// <summary>羁绊：同时携带幼虫之歌时，小蜘蛛命中回 MP。</summary>
        private static bool NoelWeaverGrubsongBond =>
            IsEquipped(CharmOwner.Noel, GrubsongId);

        /// <summary>
        /// 护符36 编织者之歌：每帧推进（挂进 `TickNoelCharmEffects`）。
        /// 与小骑士那套做法一致：维持 3 只、贴地跟随、乱跑 + 跳跃、蛛丝远程 + 近战，
        /// 离太远（掉坑/被墙隔开）直接删除重生；过图后等 3 秒再生成。
        /// </summary>
        public static void TickNoelWeaversongCharm(PRNoel pr)
        {
            try
            {
                if (pr == null || !NoelWeaverActive)
                {
                    _noelWeavers.Clear();
                    _noelWeaverThreads.Clear();
                    _noelWeaverRespawn = 0f;
                    _noelWeaverMap = null;
                    ReleaseNoelWeaverTicket();
                    return;
                }
                Map2d mp = pr.Mp;
                if (mp == null)
                {
                    return;
                }
                if (!ReferenceEquals(mp, _noelWeaverMap))
                {
                    _noelWeaverMap = mp;
                    _noelWeavers.Clear();
                    _noelWeaverThreads.Clear();
                    _noelWeaverRespawn = WeaverRespawnDelay; // 过图后等 3 秒再出现
                }
                float dt = Time.deltaTime;
                if (_noelWeaverRespawn > 0f)
                {
                    _noelWeaverRespawn -= dt;
                    if (_noelWeaverRespawn > 0f)
                    {
                        UpdateNoelWeaverThreads(pr, mp, dt);
                        EnsureNoelWeaverTicket(pr, true);
                        return;
                    }
                }
                while (_noelWeavers.Count < WeaverCount)
                {
                    int idx = _noelWeavers.Count;
                    var w = new NoelWeaver
                    {
                        X = pr.x + (idx - 1) * 1.3f,
                        // 出生位置以**诺艾尔的脚底**为准（她脚下的地面就在这里），
                        // 绝不放在她的身体中心之下 —— 早先直接用身体中心会陷进地面。
                        Y = pr.mbottom - WeaverHalfH,
                        State = 0,
                        Face = UnityEngine.Random.value < 0.5f ? 1 : -1,
                        AttackCd = UnityEngine.Random.Range(0.4f, 1.4f),
                        MeleeCd = UnityEngine.Random.Range(0.6f, 1.8f),
                        WanderT = UnityEngine.Random.Range(0.3f, 1f),
                        WanderX = pr.x + (idx - 1) * 1.3f,
                        // 三只固定错开站位：-1.3 / 0 / +1.3（跟随诺艾尔时各自停在自己的偏移上，
                        // 否则三只会全部朝诺艾尔坐标挤、叠在一起）
                        HomeOx = (idx - 1) * 1.3f,
                        NextHopT = UnityEngine.Random.Range(1f, 3f)
                    };
                    float gy = NoelWeaverGroundY(mp, w.X, w.Y);
                    if (!float.IsNaN(gy) && gy >= 0f)
                    {
                        w.Y = gy - WeaverHalfH;
                    }
                    // 兜底：任何情况下都不低于诺艾尔脚下的地面
                    if (w.Y > pr.mbottom - WeaverHalfH)
                    {
                        w.Y = pr.mbottom - WeaverHalfH;
                    }
                    _noelWeavers.Add(w);
                }
                UpdateNoelWeaverThreads(pr, mp, dt);
                for (int i = _noelWeavers.Count - 1; i >= 0; i--)
                {
                    NoelWeaver w = _noelWeavers[i];
                    float ddx = w.X - pr.x;
                    float ddy = w.Y - NoelBodyCenterY(pr);
                    if (ddx * ddx + ddy * ddy > WeaverTooFarRange * WeaverTooFarRange)
                    {
                        _noelWeavers.RemoveAt(i);
                        continue;
                    }
                    w.AnimTime += dt;
                    w.AttackCd -= dt;
                    w.MeleeCd -= dt;
                    if (w.MeleeAnimT > 0f)
                    {
                        w.MeleeAnimT -= dt;
                    }
                    if (w.State == 0)
                    {
                        if (w.AnimTime >= 0.5f)
                        {
                            w.State = 1;
                            w.AnimTime = 0f;
                        }
                        continue;
                    }
                    if (w.State == 2)
                    {
                        if (!w.ThreadFired && w.AnimTime >= 0.08f && w.Target != null && w.Target.is_alive)
                        {
                            w.ThreadFired = true;
                            FireNoelWeaverThread(w);
                        }
                        if (w.AnimTime >= WeaverAttackAnimTime)
                        {
                            w.State = 1;
                            w.AnimTime = 0f;
                            w.Target = null;
                            w.AttackCd = WeaverAttackInterval * NoelWeaverBondCooldownScale();
                        }
                        continue;
                    }
                    if (w.State == 4)
                    {
                        if (w.AnimTime <= 0.05f && w.Target != null && w.Target.is_alive)
                        {
                            ApplyNoelWeaverDamage(pr, w.Target, WeaverMeleeDamage);
                        }
                        if (w.AnimTime >= WeaverMeleeAnimTime)
                        {
                            w.State = 1;
                            w.AnimTime = 0f;
                            w.Target = null;
                            w.MeleeCd = WeaverMeleeInterval * NoelWeaverBondCooldownScale();
                        }
                        continue;
                    }
                    // State == 1：跟随 + 索敌
                    if (w.Target == null || !w.Target.is_alive || w.Target.Mp != mp)
                    {
                        w.Target = FindNoelWeaverTarget(mp, w.X, w.Y);
                    }
                    if (w.Target != null)
                    {
                        float tdx = w.Target.x - w.X;
                        float tdy = w.Target.y - w.Y;
                        float tDistSq = tdx * tdx + tdy * tdy;
                        if (w.AttackCd <= 0f && tDistSq <= WeaverSeekRange * WeaverSeekRange)
                        {
                            w.State = 2;
                            w.AnimTime = 0f;
                            w.ThreadFired = false;
                            continue;
                        }
                        if (w.MeleeCd <= 0f && tDistSq <= WeaverMeleeRange * WeaverMeleeRange)
                        {
                            w.State = 4;
                            w.AnimTime = 0f;
                            w.MeleeAnimT = WeaverMeleeAnimTime;
                            continue;
                        }
                    }
                    TickNoelWeaverMove(pr, mp, w, dt);
                }
                EnsureNoelWeaverTicket(pr, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>跟随：随机乱跑 + 偶尔跳，离诺艾尔太远就贴回来。</summary>
        private static void TickNoelWeaverMove(PRNoel pr, Map2d mp, NoelWeaver w, float dt)
        {
            w.WanderT -= dt;
            float dxToPr = pr.x - w.X;
            if (Mathf.Abs(pr.vx) > 0.05f)
            {
                w.WanderX = pr.x + w.HomeOx; // 各自停在自己的偏移上（否则三只叠在一起）
                w.WanderT = 0f;
            }
            else if (Mathf.Abs(dxToPr) > WeaverMaxRange)
            {
                w.WanderX = pr.x + w.HomeOx;
                w.WanderT = 0f;
            }
            else if (w.WanderT <= 0f)
            {
                w.WanderT = UnityEngine.Random.Range(0.35f, 1f);
                float rad = UnityEngine.Random.Range(WeaverWanderMin, WeaverWanderMax);
                w.WanderX = pr.x + (UnityEngine.Random.value < 0.5f ? -rad : rad);
            }
            float dx = w.WanderX - w.X;
            float dist = Mathf.Abs(dx);
            bool prMoving = Mathf.Abs(pr.vx) > 0.05f;
            if (dist > 0.12f || prMoving)
            {
                float spd = prMoving ? WeaverFollowSpeed : Mathf.Min(WeaverFollowSpeed, dist * 5f);
                float dir = Mathf.Sign(dx);
                float step = dir * spd * dt;
                if (dist > 0f && Mathf.Abs(step) > dist)
                {
                    step = dir * dist;
                }
                float newX = w.X + step;
                // 撞墙不穿（与小骑士那套一致：查单元格是否"实心且站不住"）
                int newCx = Mathf.FloorToInt(newX);
                bool blocked = NoelWeaverBlockCell(mp, newCx, Mathf.FloorToInt(w.Y + WeaverHalfH));
                if (pr.hasFoot())
                {
                    blocked = blocked && NoelWeaverBlockCell(mp, newCx, Mathf.FloorToInt(pr.mbottom));
                }
                if (!blocked)
                {
                    w.X = newX;
                }
                w.Vx = dir * spd;
                if (dir != 0f)
                {
                    w.Face = dir < 0f ? 1 : -1;
                }
            }
            else
            {
                w.Vx = 0f;
                w.WanderT = 0f;
            }
            // 跳跃
            w.NextHopT -= dt;
            if (w.HopT <= 0f && w.NextHopT <= 0f && Mathf.Abs(w.HopVy) <= 0.01f)
            {
                w.HopT = WeaverHopTime;
                w.HopVy = WeaverHopVy;
                w.NextHopT = UnityEngine.Random.Range(0.7f, 2f);
            }
            if (w.HopT > 0f || Mathf.Abs(w.HopVy) > 0.01f)
            {
                if (w.HopT > 0f)
                {
                    w.HopT -= dt;
                }
                w.HopVy += WeaverHopGravity * dt;
                w.Y += w.HopVy * dt;
                // 跳跃中只有真正落到地面才停下（早先的"双向吸附"会在起跳那一帧就把跳跃取消）
                float hopGy = NoelWeaverGroundY(mp, w.X, w.Y);
                if (!float.IsNaN(hopGy) && hopGy >= 0f && w.Y + WeaverHalfH >= hopGy)
                {
                    w.Y = hopGy - WeaverHalfH;
                    w.HopT = 0f;
                    w.HopVy = 0f;
                }
            }
            else
            {
                float localGy = NoelWeaverGroundY(mp, w.X, w.Y);
                bool hasLocal = !float.IsNaN(localGy) && localGy >= 0f;
                if (hasLocal)
                {
                    w.Y = localGy - WeaverHalfH;
                }
                else
                {
                    // 脚下没地面：自然下坠
                    w.HopVy = (w.HopVy > 0f ? w.HopVy : 0f) + WeaverHopGravity * dt;
                    w.Y += w.HopVy * dt;
                }
                // 诺艾尔站在高台上、而小蜘蛛还在下面板时：把它拉上台（同小骑士那套）
                if (pr.hasFoot() && hasLocal && pr.mbottom < localGy - 0.35f)
                {
                    float highGy = NoelWeaverGroundY(mp, w.X, pr.mbottom - WeaverHalfH);
                    if (!float.IsNaN(highGy) && highGy >= 0f && highGy < localGy - 0.05f)
                    {
                        w.Y = highGy - WeaverHalfH;
                        w.WanderX = pr.x + w.HomeOx;
                        w.WanderT = 0f;
                    }
                }
            }
        }

        /// <summary>
        /// 小编织者撞墙判据（照抄小骑士的 `IsBlockCell`）：
        /// 越界算实心；单元格"非空、不能站、不是地板、不是水"才算被挡住。
        /// </summary>
        private static bool NoelWeaverBlockCell(Map2d mp, int cx, int cy)
        {
            try
            {
                if (mp == null)
                {
                    return false;
                }
                if (cx < 0 || cy < 0 || cx >= mp.width || cy >= mp.rows)
                {
                    return true;
                }
                int cfg = mp.getConfig(cx, cy);
                return !CCON.isEmpty(cfg) && !CCON.canStand(cfg) && !CCON.isFloor(cfg) && !CCON.isWater(cfg);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>蛛丝推进与命中（命中即消失；每只敌人只吃一次）。</summary>
        private static void UpdateNoelWeaverThreads(PRNoel pr, Map2d mp, float dt)
        {
            if (_noelWeaverThreads.Count == 0)
            {
                return;
            }
            int mask = NoelEnemyOverlapMask();
            for (int i = _noelWeaverThreads.Count - 1; i >= 0; i--)
            {
                NoelWeaverThread t = _noelWeaverThreads[i];
                t.Life -= dt;
                if (t.Life <= 0f)
                {
                    _noelWeaverThreads.RemoveAt(i);
                    continue;
                }
                float step = WeaverThreadSpeed * dt;
                t.X += t.DirX * step;
                t.Y += t.DirY * step;
                t.Dist += step;
                if (t.Dist > 9f)
                {
                    _noelWeaverThreads.RemoveAt(i);
                    continue;
                }
                if (mask == 0)
                {
                    continue;
                }
                Vector2 center = mp.gameObject.transform.TransformPoint(
                    new Vector2(mp.pixel2ux(t.X * mp.CLEN), mp.pixel2uy(t.Y * mp.CLEN)));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, WeaverThreadHitRadius, mask);
                if (hits == null)
                {
                    continue;
                }
                bool hitAny = false;
                for (int j = 0; j < hits.Length; j++)
                {
                    Collider2D c = hits[j];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null || !t.Hits.Add(enemy))
                    {
                        continue;
                    }
                    ApplyNoelWeaverDamage(pr, enemy, WeaverThreadDamage);
                    hitAny = true;
                }
                if (hitAny)
                {
                    _noelWeaverThreads.RemoveAt(i);
                }
            }
        }

        /// <summary>发射蛛丝。</summary>
        private static void FireNoelWeaverThread(NoelWeaver w)
        {
            if (w.Target == null)
            {
                return;
            }
            float dx = w.Target.x - w.X;
            float dy = w.Target.y - w.Y;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d < 0.01f)
            {
                return;
            }
            _noelWeaverThreads.Add(new NoelWeaverThread
            {
                X = w.X,
                Y = w.Y - 0.3f,
                DirX = dx / d,
                DirY = dy / d,
                Life = WeaverThreadLife,
                Dist = 0f
            });
        }

        /// <summary>
        /// 小蜘蛛命中敌人：固定伤害（真伤，与小骑士那套一致）。
        /// 羁绊「幼虫之歌 + 编织者之歌」→ 每次命中回复 `WeaverGrubsongMp`（默认 3）MP。
        /// </summary>
        private static void ApplyNoelWeaverDamage(PRNoel pr, NelEnemy enemy, int damage)
        {
            try
            {
                if (enemy == null || pr == null || IsEnemySummoning(enemy))
                {
                    return; // 生成中的魔物不能打
                }
                var atk = new NelAttackInfo();
                atk.hpdmg0 = damage;
                atk.hpdmg_current = damage;
                atk.fix_damage = true; // 真伤，同小骑士的幼体/编织者
                atk.Caster = pr;
                atk.AttackFrom = pr;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                enemy.applyDamage(atk, false);
                if (NoelWeaverGrubsongBond)
                {
                    KnightInCradleBehaviour.GrantNoelMana(KnightInCradlePlugin.WeaverGrubsongMp);
                    RefreshNoelHudMp();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>索敌：找最近的、可控的敌人（7 格内）。</summary>
        private static NelEnemy FindNoelWeaverTarget(Map2d mp, float x, float y)
        {
            try
            {
                NelEnemy best = null;
                float bestSq = WeaverSeekRange * WeaverSeekRange;
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (!(mp.getMv(i) is NelEnemy en) || !en.is_alive || en.destructed ||
                        IsEnemySummoning(en) || IsUniEnemy(en))
                    {
                        continue;
                    }
                    float dx = en.x - x;
                    float dy = en.y - y;
                    float dSq = dx * dx + dy * dy;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        best = en;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>地面高度（与原版/小骑士同一条路：`Map2d.BCC.isFallable`）。</summary>
        private static float NoelWeaverGroundY(Map2d mp, float wx, float feetY)
        {
            try
            {
                if (mp == null || mp.BCC == null)
                {
                    return float.NaN;
                }
                BCCLine line;
                return mp.BCC.isFallable(wx, feetY - 0.3f, 0.2f, 0.55f, out line, true, true, -1f, null);
            }
            catch (Exception)
            {
                return float.NaN;
            }
        }

        /// <summary>小蜘蛛 + 蛛丝的渲染票据（诺艾尔身后层 PR0）。</summary>
        private static void EnsureNoelWeaverTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null || !want)
            {
                if (!want)
                {
                    ReleaseNoelWeaverTicket();
                }
                return;
            }
            if (_noelWeaverTex == null)
            {
                LoadNoelWeaverAssets();
            }
            if (_noelWeaverTex == null || _noelWeaverTex.Count == 0)
            {
                return;
            }
            if (_noelWeaverMesh != null && ReferenceEquals(_noelWeaverTicketMap, mp) && _noelWeaverTicket != null)
            {
                return;
            }
            ReleaseNoelWeaverTicket();
            _noelWeaverTicketMap = mp;
            _noelWeaverMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _noelWeaverMesh.draw_gl_only = true;
            _noelWeaverMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelWeaverMat.EnableKeyword("NO_PIXELSNAP");
            _noelWeaverMesh.activate("noel_weaver", _noelWeaverMat, false, MTRX.ColWhite, null);
            _noelWeaverTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelWeaverMesh, _noelWeaverMesh, null, null);
        }

        private static void ReleaseNoelWeaverTicket()
        {
            try
            {
                if (_noelWeaverTicket != null && _noelWeaverTicketMap != null &&
                    _noelWeaverTicketMap.MovRenderer != null)
                {
                    _noelWeaverTicketMap.MovRenderer.deassignDrawable(_noelWeaverTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelWeaverMat != null)
                {
                    IN.DestroyOne(_noelWeaverMat);
                }
            }
            catch (Exception)
            {
            }
            _noelWeaverTicket = null;
            _noelWeaverMesh = null;
            _noelWeaverMat = null;
            _noelWeaverTicketMap = null;
        }

        private static bool PrepareNoelWeaverMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelWeaverTicketMap;
            if (mp == null || _noelWeaverMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelWeaverMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelWeaverTex == null)
            {
                MdOut = _noelWeaverMesh;
                return true;
            }
            float ax = pr.x;
            float ay = NoelBodyCenterY(pr);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(ax * mp.CLEN), mp.pixel2uy(ay * mp.CLEN), 0f));
            // 蛛丝：画成细线段（与骑士侧一致）
            _noelWeaverMesh.Col = new Color(1f, 1f, 1f, 0.85f);
            for (int i = 0; i < _noelWeaverThreads.Count; i++)
            {
                NoelWeaverThread t = _noelWeaverThreads[i];
                float x0 = (t.X - ax) * mp.CLEN;
                float y0 = -(t.Y - ay) * mp.CLEN;
                float x1 = (t.X + t.DirX * 0.55f - ax) * mp.CLEN;
                float y1 = -(t.Y + t.DirY * 0.55f - ay) * mp.CLEN;
                _noelWeaverMesh.Line(x0, y0, x1, y1, 2f);
            }
            // 小蜘蛛
            for (int i = 0; i < _noelWeavers.Count; i++)
            {
                NoelWeaver w = _noelWeavers[i];
                string sprite = NoelWeaverSprite(w);
                Texture2D tex;
                if (sprite == null || _noelWeaverTex == null ||
                    !_noelWeaverTex.TryGetValue(sprite, out tex) || tex == null)
                {
                    continue;
                }
                float dxm = (w.X - ax) * mp.CLEN;
                float dym = -(w.Y - ay) * mp.CLEN + KnightInCradlePlugin.WeaverRenderOffsetY * mp.CLEN;
                float ww = tex.width * KnightInCradlePlugin.WeaverRenderScale;
                float hh = tex.height * KnightInCradlePlugin.WeaverRenderScale;
                _noelWeaverMesh.Col = MTRX.ColWhite;
                _noelWeaverMesh.initForImgAndTexture(tex);
                _noelWeaverMesh.uv_top = 0f;
                _noelWeaverMesh.uv_height = 1f;
                if (w.Face < 0)
                {
                    _noelWeaverMesh.uv_left = 1f;
                    _noelWeaverMesh.uv_width = -1f;
                }
                else
                {
                    _noelWeaverMesh.uv_left = 0f;
                    _noelWeaverMesh.uv_width = 1f;
                }
                _noelWeaverMesh.Rect(dxm - ww * 0.5f, dym - hh * 0.5f, ww, hh, false);
            }
            MdOut = _noelWeaverMesh;
            return true;
        }

        /// <summary>当前该画哪一帧（出生/攻击/近战/跑/待机，与小骑士同一套剪辑名）。</summary>
        private static string NoelWeaverSprite(NoelWeaver w)
        {
            string clipName;
            if (w.State == 0)
            {
                clipName = "WeaverLaunch";
            }
            else if (w.State == 2)
            {
                clipName = "WeaverAttack";
            }
            else if (w.State == 4 || w.MeleeAnimT > 0f)
            {
                clipName = "WeaverMelee";
            }
            else
            {
                clipName = Mathf.Abs(w.Vx) > 0.05f ? "WeaverRun" : "WeaverIdle";
            }
            NoelWeaverClip clip;
            if (_noelWeaverClips == null || !_noelWeaverClips.TryGetValue(clipName, out clip) ||
                clip == null || clip.Frames == null || clip.Frames.Length == 0)
            {
                return null;
            }
            int idx = (int)(w.AnimTime * Mathf.Max(1f, clip.Fps)) % clip.Frames.Length;
            return clip.Frames[idx];
        }

        /// <summary>读取 `assets/hk/sheets/spider`（与小骑士那份素材/清单完全相同）。</summary>
        private static void LoadNoelWeaverAssets()
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets",
                    "hk", "sheets", "spider");
                string spriteDir = System.IO.Path.Combine(dir, "sprites");
                string manifestPath = System.IO.Path.Combine(dir, "spider_manifest.json");
                if (!System.IO.Directory.Exists(spriteDir) || !System.IO.File.Exists(manifestPath))
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][编织者之歌] 找不到素材目录：" + spriteDir);
                    _noelWeaverTex = new Dictionary<string, Texture2D>();
                    return;
                }
                var texes = new Dictionary<string, Texture2D>();
                foreach (string png in System.IO.Directory.GetFiles(spriteDir, "*.png"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(png);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    texes[name] = tex;
                }
                var clips = new Dictionary<string, NoelWeaverClip>();
                var manifest = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(manifestPath));
                var clipMap = new Dictionary<string, string>
                {
                    ["Launch"] = "WeaverLaunch",
                    ["Run"] = "WeaverRun",
                    ["Idle"] = "WeaverIdle",
                    ["Attack"] = "WeaverAttack",
                    ["Sleep"] = "WeaverSleep"
                };
                if (manifest["clips"] is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (Newtonsoft.Json.Linq.JToken c in arr)
                    {
                        string clipName = (string)c["name"];
                        if (clipName == null || !clipMap.TryGetValue(clipName, out string key))
                        {
                            continue;
                        }
                        var frames = new List<string>();
                        if (c["frames"] is Newtonsoft.Json.Linq.JArray fr)
                        {
                            foreach (Newtonsoft.Json.Linq.JToken f in fr)
                            {
                                string nm = (string)f;
                                if (nm != null && texes.ContainsKey(nm))
                                {
                                    frames.Add(nm);
                                }
                            }
                        }
                        if (frames.Count == 0)
                        {
                            continue;
                        }
                        clips[key] = new NoelWeaverClip
                        {
                            Fps = (float)c["fps"],
                            Frames = frames.ToArray()
                        };
                    }
                }
                // 近战剪辑：0014~0016（3 帧，0.3s）
                var melee = new List<string>();
                for (int i = 14; i <= 16; i++)
                {
                    string nm = "Weaver_Charm_spawn_spider00" + i;
                    if (texes.ContainsKey(nm))
                    {
                        melee.Add(nm);
                    }
                }
                if (melee.Count > 0)
                {
                    clips["WeaverMelee"] = new NoelWeaverClip { Fps = 10f, Frames = melee.ToArray() };
                }
                _noelWeaverTex = texes;
                _noelWeaverClips = clips;
            }
            catch (Exception ex)
            {
                KnightInCradlePlugin.PluginLog?.LogWarning(
                    "[KIC][编织者之歌] 素材读取失败：" + ex.Message);
                _noelWeaverTex = new Dictionary<string, Texture2D>();
            }
        }

        /// <summary>旋风斩当前该摆的姿势名（起手 / 循环 / 收尾）。</summary>
        private static string NailMasterPoseName()
        {
            if (_nmPhase == NailMasterPhase.Spin)
            {
                return _nmTimer < KnightInCradlePlugin.NailMasterSpinIntroSeconds
                    ? KnightInCradlePlugin.NailMasterSpinPoseIntro
                    : KnightInCradlePlugin.NailMasterSpinPoseLoop;
            }
            if (_nmPhase == NailMasterPhase.SpinOutro)
            {
                return KnightInCradlePlugin.NailMasterSpinPoseOutro;
            }
            return null;
        }

        /// <summary>
        /// 效果1：诺艾尔不会被魔物抓取/吞下。
        /// - `PR.initAbsorb`（被魔物吞下/吸收）直接拦掉；
        /// - 抓取类状态直接拒绝：`PARASITISED`（被寄生/蚂蟥附着）、`WORM_TRAPPED`（虫墙）、
        ///   `EATEN`（被吃住）、`STRONG_HOLD`（强力抓取）、`WEB_TRAPPED`（蜘蛛网）；
        /// - 虫墙拉扯另外由 `PR.canPullByWorm`（见 `GrubsongCanPullByWormPrefix`，已含乌恩之形）负责。
        /// </summary>
        private static bool UnnInitAbsorbPrefix(PR __instance, ref bool __result)
        {
            try
            {
                if (!IsUnnApplied(__instance) && !NoelSpinInvincible && !(IsNoelFuryImmune && __instance is PRNoel))
                {
                    return true;
                }
                __result = false; // 免疫吸收/吞下
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 效果2：蹲下/爬行期间，让**战斗区域内**的魔物变成友好状态
        /// （每帧清掉它们的锁定目标 `Nai.AimPr`；配合 `NaiAimPrSetPrefix` 里禁止重新锁定，
        /// 它们就不会追着诺艾尔打；起身后自动恢复）。
        /// 战斗区域取 `EnemySummoner.ActiveScript.getSummonedArea()` 的矩形；没有战斗时才作用于本房间所有魔物。
        /// </summary>
        public static void ClearUnnFriendlyAims()
        {
            if (!UnnFriendlyActive())
            {
                return;
            }
            try
            {
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                Map2d mp = noel != null ? noel.Mp : null;
                if (mp == null)
                {
                    return;
                }
                M2LpSummon area = null;
                try
                {
                    EnemySummoner active = EnemySummoner.ActiveScript;
                    area = active != null ? active.getSummonedArea() : null;
                }
                catch (Exception)
                {
                    area = null;
                }
                for (int i = mp.count_movers - 1; i >= 0; i--)
                {
                    if (!(mp.getMv(i) is NelEnemy en))
                    {
                        continue;
                    }
                    if (area != null &&
                        (en.x < area.mapx || en.x >= area.mapx + area.mapw ||
                         en.y < area.mapy || en.y >= area.mapy + area.maph))
                    {
                        continue; // 只影响当前战斗区域内的魔物
                    }
                    NAI ai = en.getAI();
                    if (ai != null && ai.AimPr != null)
                    {
                        ai.AimPr = null; // set_AimPr 前缀放行 null → 清空目标
                    }
                }
            }
            catch (Exception)
            {
            }
        }

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
                // 蹲伏/爬行时**不要**强制跑步：AIC 的姿势选择里 `isRunning()` 为真会无条件摆跑步姿势
                // （AnimationShufflerNoel.cs:669-671），而蹲下左右移动应当走 `crawl`
                // （同文件 :677 的 `isRunning() ? "run" : (flag2 ? "crawl" : "walk")`）。
                if (pr.view_crouching || pr.forceCrouch(false, false))
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

        // ================= 护符18 修长之钉（诺艾尔侧：近战距离 ×倍率 + 自绘白色弧带） =========
        /// <summary>
        /// 护符18 修长之钉（诺艾尔侧）：近战"总触及距离"按配置倍率放大，并**自绘一道白色弧带**
        /// 把加长的距离显示出来。
        ///
        /// 为什么自绘：AIC 诺艾尔挥击时的弧光是**烘焙在本体姿势图层里**的（实测她的挥击姿势只有
        /// `Layer`/`Layer_2`（本体，弧光在其中）、`rod_4`（法杖）、`rodeff`（法杖旁粒子）、`hand`（手）
        /// 这几个图层，没有独立的"弧光层"），所以既不能只拉弧光，也不能靠游戏自己的特效参数
        /// （`pr_cane_swing` 的 `lax` 实测不影响画面）。这里用纯程序化的白色三角形画一段弧带，
        /// 长度 = 这一招放大后的判定触及距离（`|(sx,sy)| + |sz|`），做到"看得到多远 = 打得到多远"。
        /// </summary>
        private const float LongNailArcLife = 0.14f;
        private const float LongNailArcSpanDeg = 42f;   // 弧带张角（相对正前方，左右各一半）
        private const int LongNailArcSegments = 14;

        private sealed class NoelLongNailArc
        {
            public float X;
            public float Y;
            public float Dir;
            public float ReachFrom;   // 原版触及距离
            public float ReachTo;     // 加成后的触及距离（只画这一段）
            public float T;
            public int Frame;         // 生成帧（同一次挥击的多个攻击包合并成一条弧带）
            public MGKIND Kind;
            /// <summary>这一刀是不是"蓄力释放"（魔法霰弹及其变种）——贴图用 magic 版。</summary>
            public bool Magic;
        }

        private static readonly List<NoelLongNailArc> _noelLongNailArcs = new List<NoelLongNailArc>();
        /// <summary>剑气贴图（HK 长钉样式，与小骑士戴修长之钉时同款）。</summary>
        private static readonly string[] LongNailSlashSprites = { "mantis_slash_left0001", "mantis_slash_left0002" };
        private static Texture2D[] _noelLongNailSlashTex;
        private static MeshDrawer _noelLongNailArcMesh;
        private static Material _noelLongNailArcMat;
        private static M2RenderTicket _noelLongNailArcTicket;
        private static Map2d _noelLongNailArcMap;

        /// <summary>
        /// 修长之钉/骄傲印记覆盖的招式（近战判定包 kind）：轻攻击、凌空横斩（同一 kind = PR_PUNCH）
        /// 与魔法霰弹（PR_SHOTGUN / 附魔凌空横斩）。
        /// 按 `docs/护符加成描述.md` 的表格：18/19 只勾了"轻攻击 / 魔法霰弹 / 凌空横斩 / 附魔凌空横斩"，
        /// **会心重击（PR_SMASH）没有勾**，所以这里不再包含 SMASH。
        /// </summary>
        private static bool IsLongNailKind(MGKIND kind)
        {
            return kind == MGKIND.PR_PUNCH || kind == MGKIND.PR_SHOTGUN;
        }

        /// <summary>是否佩戴了"加长近战"类护符（18 修长之钉 / 19 骄傲印记）。</summary>
        private static bool HasReachCharm()
        {
            return IsEquipped(CharmOwner.Noel, LongNailId) || IsEquipped(CharmOwner.Noel, PrideId);
        }

        /// <summary>
        /// 近战距离总倍率：修长之钉（默认 +25%）与骄傲印记（默认 +35%）**百分比相加**，
        /// 只算实际佩戴的那几个（同时佩戴 = +60%）。
        /// </summary>
        private static float NoelMeleeReachMult()
        {
            float bonus = 0f;
            if (IsEquipped(CharmOwner.Noel, LongNailId))
            {
                bonus += KnightInCradlePlugin.LongNailReachMult - 1f;
            }
            if (IsEquipped(CharmOwner.Noel, PrideId))
            {
                bonus += KnightInCradlePlugin.PrideReachPercent / 100f;
            }
            return 1f + bonus;
        }

        // ---- 判定侧：`PrCaneEquip.initChantMagicAwaken` 前缀记基准、后缀把总触及距离精确改倍率 ----
        /// <summary>`PrCaneEquip.reach_ratio` 后缀：连游戏自己的 reach 倍率（判定线段 + 挥击特效）一起放大。</summary>
        private static void LongNailReachRatioPostfix(ref float __result)
        {
            try
            {
                if (IsKnightMode || !HasReachCharm())
                {
                    return;
                }
                __result *= NoelMeleeReachMult();
            }
            catch (Exception)
            {
            }
        }

        private static float _longNailBaseReach;
        private static int _longNailBaseMgId = -1;
        private static readonly HashSet<int> _longNailDoneIds = new HashSet<int>();
        private static Map2d _longNailDoneMap;
        /// <summary>每个攻击包的"原版触及距离"（用于只画加成区那一段弧带）。</summary>
        private static readonly Dictionary<int, float> _longNailBaseReachById = new Dictionary<int, float>();
        private static int _longNailArcLogCount;

        private static void LongNailCaneAwakenPrefix(MagicItem Mg)
        {
            _longNailBaseReach = 0f;
            _longNailBaseMgId = -1;
            try
            {
                if (Mg == null || IsKnightMode || !HasReachCharm() || NoelMeleeReachMult() <= 1f)
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
                    _longNailBaseReachById.Clear();
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
        /// 把这一招最终的"触及距离"改成 `基准 × 倍率`。
        /// 注意游戏自己的 reach 只乘在线段 `sx` 上、粗细 `sz` 不参与，所以只靠倍率会让总距离增幅打折
        /// （实测 +20% 落到总距离只剩 +9% ≈ 3.5 像素），这里按"总触及距离 = 线段 + 粗细"精确补足。
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
                float target = _longNailBaseReach * NoelMeleeReachMult();
                float need = target - rad;
                float len = Mathf.Sqrt(Mg.sx * Mg.sx + Mg.sy * Mg.sy);
                if (need > 0f && len > 0.0001f && need > len)
                {
                    float k = need / len;
                    Mg.sx *= k;
                    Mg.sy *= k;
                }
                _longNailDoneIds.Add(Mg.id);
                _longNailBaseReachById[Mg.id] = _longNailBaseReach;
                if (_longNailBaseReachById.Count > 256)
                {
                    _longNailBaseReachById.Clear();
                }
                _longNailBaseReach = 0f;
            }
            catch (Exception)
            {
                _longNailBaseReach = 0f;
            }
        }

        // ---- 渲染侧：自绘白色弧带 ----
        /// <summary>攻击包生成时登记一道弧带（长度 = 判定触及距离）。</summary>
        private static void LongNailSmallAttackPostfix(MagicItem __result)
        {
            try
            {
                if (__result == null || IsKnightMode || !HasReachCharm() ||
                    !(__result.Caster is PRNoel) || !IsLongNailKind(__result.kind))
                {
                    return;
                }
                float reach = Mathf.Sqrt(__result.sx * __result.sx + __result.sy * __result.sy) +
                              Mathf.Abs(__result.sz);
                if (reach <= 0.2f)
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null)
                {
                    return;
                }
                float dir = Mathf.Abs(__result.sx) > 0.0001f
                    ? Mathf.Sign(__result.sx)
                    : (pr.mpf_is_right >= 0f ? 1f : -1f);
                // 只画"护符多出来的那一段"：内半径 = 原版触及距离，外半径 = 加成后的触及距离
                float baseReach;
                if (!_longNailBaseReachById.TryGetValue(__result.id, out baseReach) || baseReach <= 0f)
                {
                    baseReach = reach / Mathf.Max(1.0001f, NoelMeleeReachMult());
                }
                if (baseReach >= reach - 0.02f)
                {
                    return; // 没有加成就不画
                }
                // 一次挥击会连续生成多个攻击包（id=0 主判定 + id=1 长距离补判定），
                // 它们属于同一刀 —— 合并成一条弧带（取最小内半径 / 最大外半径），否则会画出两道弧。
                bool magic = IsNoelShotgunFlavored(__result);
                int frame = Time.frameCount;
                for (int i = 0; i < _noelLongNailArcs.Count; i++)
                {
                    NoelLongNailArc ex = _noelLongNailArcs[i];
                    if (ex.Frame == frame && ex.Kind == __result.kind && Mathf.Sign(ex.Dir) == Mathf.Sign(dir))
                    {
                        ex.ReachFrom = Mathf.Min(ex.ReachFrom, baseReach);
                        ex.ReachTo = Mathf.Max(ex.ReachTo, reach);
                        ex.T = 0f;
                        ex.Magic = ex.Magic || magic;
                        return;
                    }
                }
                _noelLongNailArcs.Add(new NoelLongNailArc
                {
                    X = pr.x,
                    Y = pr.y,
                    Dir = dir,
                    ReachFrom = baseReach,
                    ReachTo = reach,
                    T = 0f,
                    Frame = frame,
                    Kind = __result.kind,
                    Magic = magic,
                });
                if (_longNailArcLogCount < 12)
                {
                    _longNailArcLogCount++;
                    KnightInCradlePlugin.PluginLog?.LogInfo(
                        "[KIC][长钉弧带] kind=" + __result.kind + " 原版触及=" + baseReach +
                        " 加成后=" + reach + "（倍率 " + NoelMeleeReachMult() + "）");
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进：弧带计时 / 过期清理 / 票据维护。</summary>
        public static void TickNoelLongNailArc(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                float dt = Time.deltaTime;
                for (int i = _noelLongNailArcs.Count - 1; i >= 0; i--)
                {
                    NoelLongNailArc a = _noelLongNailArcs[i];
                    a.T += dt;
                    if (a.T >= LongNailArcLife)
                    {
                        _noelLongNailArcs.RemoveAt(i);
                    }
                }
                EnsureLongNailArcTicket(pr, _noelLongNailArcs.Count > 0);
            }
            catch (Exception)
            {
            }
        }

        private static void EnsureLongNailArcTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseLongNailArcTicket();
                return;
            }
            if (_noelLongNailSlashTex == null)
            {
                _noelLongNailSlashTex = LoadLongNailSlashTextures();
            }
            if (KnightInCradlePlugin.MagicSlashOnCharged)
            {
                GetMagicSlashTexture(); // 预热（缺素材时只找一次，不每帧读盘）
            }
            if (_noelLongNailSlashTex == null)
            {
                return; // 素材缺失：判定照常，只是不显示
            }
            if (_noelLongNailArcMesh != null && _noelLongNailArcMap == mp && _noelLongNailArcTicket != null)
            {
                return;
            }
            ReleaseLongNailArcTicket();
            _noelLongNailArcMap = mp;
            _noelLongNailArcMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _noelLongNailArcMesh.draw_gl_only = true;
            _noelLongNailArcMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelLongNailArcMat.EnableKeyword("NO_PIXELSNAP");
            _noelLongNailArcMesh.activate("noel_longnail_arc", _noelLongNailArcMat, false, MTRX.ColWhite, null);
            _noelLongNailArcTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, PrepareLongNailArcMesh, _noelLongNailArcMesh, null, null);
        }

        /// <summary>
        /// 剑气绘制（复用 HK"长钉样式"斩击帧，与小骑士戴修长之钉时同款）：
        /// 以诺艾尔中心为锚点，朝攻击方向画一道**长度 = 该招加成后触及距离 × 长度倍率**的剑气，
        /// 两帧交替、随时间淡出。
        /// </summary>
        private static bool PrepareLongNailArcMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelLongNailArcMap;
            if (mp == null || _noelLongNailArcMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelLongNailArcMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelLongNailSlashTex == null || _noelLongNailArcs.Count == 0)
            {
                MdOut = _noelLongNailArcMesh;
                return true;
            }
            float mx = mp.pixel2ux(pr.x * mp.CLEN);
            float my = mp.pixel2uy(pr.y * mp.CLEN);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float alphaK = KnightInCradlePlugin.LongNailArcAlpha;
            float lenRatio = KnightInCradlePlugin.LongNailSlashLengthRatio;
            float sizeK = KnightInCradlePlugin.LongNailSlashScale;
            // 两个护符同时佩戴时，剑气的观感参数以"骄傲印记"那套为准（长度两者都算进触及距离了）
            if (IsEquipped(CharmOwner.Noel, PrideId))
            {
                alphaK = KnightInCradlePlugin.PrideAlpha;
                lenRatio = KnightInCradlePlugin.PrideSlashLengthRatio;
                sizeK = KnightInCradlePlugin.PrideSlashScale;
            }
            Texture2D magicTex = KnightInCradlePlugin.MagicSlashOnCharged ? GetMagicSlashTexture() : null;
            for (int i = 0; i < _noelLongNailArcs.Count; i++)
            {
                NoelLongNailArc arc = _noelLongNailArcs[i];
                float progress = Mathf.Clamp01(arc.T / LongNailArcLife);
                // 蓄力释放（魔法霰弹及其变种）→ 换成 slash_effect_magic 单帧图；否则用长钉样式两帧交替
                bool useMagic = arc.Magic && magicTex != null;
                Texture2D tex = useMagic
                    ? magicTex
                    : _noelLongNailSlashTex[progress < 0.5f ? 0 : _noelLongNailSlashTex.Length - 1];
                if (tex == null)
                {
                    continue;
                }
                // 剑气长度：跟判定一致（加成后的触及距离 × 可调倍率）
                float w = arc.ReachTo * lenRatio * mp.CLEN;
                float h = w * ((float)tex.height / tex.width) * sizeK *
                          (IsEquipped(CharmOwner.Noel, PrideId)
                              ? KnightInCradlePlugin.PrideSlashHeightRatio
                              : KnightInCradlePlugin.LongNailSlashHeightRatio);
                w *= sizeK;
                if (useMagic)
                {
                    w *= KnightInCradlePlugin.MagicSlashScale;
                    h *= KnightInCradlePlugin.MagicSlashScale * KnightInCradlePlugin.MagicSlashHeightRatio;
                }
                if (w <= 0f || h <= 0f)
                {
                    continue;
                }
                // 剑气中心放在"诺艾尔中心 → 判定末端"的中点，向攻击方向镜像
                float dx = arc.Dir * w * 0.5f;
                _noelLongNailArcMesh.Col = new Color(1f, 1f, 1f, alphaK * (1f - progress));
                _noelLongNailArcMesh.initForImgAndTexture(tex);
                _noelLongNailArcMesh.uv_top = 0f;
                _noelLongNailArcMesh.uv_height = 1f;
                if (arc.Dir > 0f)
                {
                    _noelLongNailArcMesh.uv_left = 1f;
                    _noelLongNailArcMesh.uv_width = -1f;
                }
                else
                {
                    _noelLongNailArcMesh.uv_left = 0f;
                    _noelLongNailArcMesh.uv_width = 1f;
                }
                _noelLongNailArcMesh.Rect(dx, 0f, w, h, false);
            }
            MdOut = _noelLongNailArcMesh;
            return true;
        }

        private static Texture2D[] LoadLongNailSlashTextures()
        {
            try
            {
                var list = new Texture2D[LongNailSlashSprites.Length];
                for (int i = 0; i < list.Length; i++)
                {
                    string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets",
                        "hk", "sprites", LongNailSlashSprites[i] + ".png");
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

        private static void ReleaseLongNailArcTicket()
        {
            try
            {
                if (_noelLongNailArcTicket != null && _noelLongNailArcMap != null &&
                    _noelLongNailArcMap.MovRenderer != null)
                {
                    _noelLongNailArcMap.MovRenderer.deassignDrawable(_noelLongNailArcTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelLongNailArcMat != null)
                {
                    IN.DestroyOne(_noelLongNailArcMat);
                }
            }
            catch (Exception)
            {
            }
            _noelLongNailArcTicket = null;
            _noelLongNailArcMesh = null;
            _noelLongNailArcMat = null;
            _noelLongNailArcMap = null;
        }

        /// <summary>弧带绘制：以诺艾尔中心为圆心，朝攻击方向画一条白色弯月带（张角 ±42°，半径 = 触及距离）。</summary>

        // ================= 护符17 快速劈砍（诺艾尔侧：挥杖速度 +50%） =================
        /// <summary>快速劈砍：诺艾尔挥杖速度倍率（需求：+50%）。</summary>
        public const float FastSlashSpeedMult = 1.5f;

        /// <summary>`PR.state`（protected 字段）的快速读取器，用于判断"当前是不是挥击状态"。</summary>
        private static AccessTools.FieldRef<PR, PR.STATE> _prStateRef;

        /// <summary>
        /// 诺艾尔的"挥击/技艺状态"白名单（快速劈砍 17 / 亡者之怒 20 的攻速只在这些状态里加速）。
        /// 按 `docs/护符加成描述.md` 的表格：17 勾了 轻攻击 / 魔法霰弹 / 凌空横斩(+附魔) /
        /// 会心重击(+附魔) / 轮舞斩击(+附魔)，**没有**勾 旋风斩击 / 彗星俯冲（也列了 突进冲击、滑铲则完全不提），
        /// 所以这里把 WHEEL / COMET / DASHPUNCH / SLIDING 去掉。
        /// </summary>
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
                    case PR.STATE.SMASH:
                    case PR.STATE.SMASH_SHOTGUN:
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
                // 护符20 效果8：亡者之怒期间攻击速度 +25%（走与快速劈砍同一条 `PR.baseTS`）
                bool fury = _noelFuryActive;
                bool charm = IsEquipped(CharmOwner.Noel, FastSlashId);
                if (IsKnightMode || (!charm && !fury))
                {
                    return;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || !ReferenceEquals(__instance, pr) || !IsNoelAttackState(pr))
                {
                    return;
                }
                if (charm)
                {
                    __result *= FastSlashSpeedMult;
                }
                if (fury)
                {
                    __result *= KnightInCradlePlugin.FuryAttackSpeedMult;
                }
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

        /// <summary>
        /// 需求（2026-09-26 追加）：亡者之怒期间诺艾尔"碰到魔物不摔倒"（同稳定之体）。
        /// = 佩戴稳定之体 **或** 正在亡者之怒（只对本地诺艾尔）。
        /// 只用于**摔倒**相关的四个挂点（接触伤害标记 / DAMAGE_LT / RCenemy_sink / ENEMY_SINK）；
        /// 稳定之体的风力与冰面免疫不走这里（亡者之怒没有那条需求）。
        /// </summary>
        private static bool IsKnockdownImmune(PR pr)
        {
            if (IsStableBodyApplied(pr))
            {
                return true;
            }
            if (!IsNoelFuryImmune || !(pr is PRNoel))
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
                if (__instance == null || Atk == null || !IsKnockdownImmune(__instance.Pr))
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
                // 护符20 亡者之怒（2026-09-26 追加）：期间不进入任何"受伤反应"状态。
                // 触发那一帧我们是先切 `STATE.BURST`（圣光爆发）、伤害管线随后才切摔倒/后仰，
                // 不挡掉的话爆发会被瞬间顶掉（这就是"血量卡在 30 但看不到圣光爆发"的原因）。
                if (IsNoelFuryImmune && __instance is PRNoel && IsDamageReactionState(_state))
                {
                    return false;
                }
                if (_state != PR.STATE.DAMAGE_LT && _state != PR.STATE.DAMAGE_LT_KIRIMOMI)
                {
                    return true;
                }
                if (_stableBodyContactFrame != Time.frameCount ||
                    !ReferenceEquals(__instance, _stableBodyContactPr) ||
                    !IsKnockdownImmune(__instance))
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
                _stableBodyHitSinkScope = IsKnockdownImmune(__instance);
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
                if (_stableBodyHitSinkScope && IsKnockdownImmune(__instance))
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
        /// PR_SHOTGUN（魔法霰弹）、PR_WHEEL（旋风斩击）、PR_COMET（彗星俯冲）、
        /// PR_DASHPUNCH（突进冲击）、PR_SMASH（会心重击）、
        /// PR_EVADECOUNTER（轮舞斩击：`SkillManager` 里叫 `evade_dancing`，
        /// 弹开敌人攻击后 ←/→ + z 触发，`M2PrSkill.cs:2017-2021` → `PR.STATE.EVADECOUNTER`，
        /// 蓄力时为 `EVADECOUNTER_SHOTGUN` 但 kind 仍是 PR_EVADECOUNTER）。
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
                case MGKIND.PR_EVADECOUNTER:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>CircleCast 期间临时抬高的伤害值，调用结束原样还原（避免污染可复用的 Atk）。</summary>
        private sealed class ShamanBoostState
        {
            public int Hp0;
            /// <summary>原 `hpdmg_current`（固定伤害会一起改掉，后缀要还原）。</summary>
            public int Cur;
            /// <summary>原 `fix_damage`（护符35 的固定伤害会把它设成 true，后缀要还原）。</summary>
            public bool Fix;
        }

        /// <summary>
        /// 诺艾尔侧的"最终伤害乘区"（萨满之石 × 坚固力量 × 会心），`CircleCast` 前缀与
        /// 蜕变挽歌剑气的"霰弹命中"结算共用，保证两条路给同一个倍率。
        /// </summary>
        /// <summary>
        /// 诺艾尔**辅助伤害源**（苦痛荆棘反击 / 吸虫 / 防御者纹章法阵）的伤害乘区，
        /// 按 `docs/护符加成描述.md` 的表格逐项对齐：
        /// ・13 坚固力量：苦痛荆棘勾了、吸虫与法阵没勾 → 由 `powerKind` 控制；
        /// ・16 沉重之击（会心 +40%）：三者都勾 → 一律生效；
        /// ・20 亡者之怒（+75%）：三者都勾 → 一律生效；
        /// ・5 萨满之石：只有法阵勾了（吸虫走"7→9"的独立规则）→ 由调用方自己乘。
        /// 注意这里**不含**护符35（骨钉大师 ×5）与护符27（深聚下一击），
        /// 避免辅助伤害把深聚的"下一击"额度吃掉。
        /// </summary>
        private static float NoelSideDamageMult(bool powerKind)
        {
            float mult = 1f;
            if (powerKind && IsEquipped(CharmOwner.Noel, PowerId))
            {
                mult *= PowerDamageMult;
            }
            if (IsHeavyFocusActive)
            {
                mult *= HeavyBlowFocusMult;
            }
            if (_noelFuryActive)
            {
                mult *= KnightInCradlePlugin.FuryDamageMult;
            }
            return mult;
        }

        private static float NoelFinalDamageMult(MGKIND kind, bool shotgunFlavored)
        {
            float mult = 1f;
            // 护符35 骨钉大师的荣耀（需求 2026-09-27 改）：不再用倍率，
            // 而是把每一击的伤害**固定**成 20（携带坚固力量时 25），
            // 具体在命中处（`ShamanCircleCastPrefix` / 旋风斩判定）直接写 `hpdmg0`，
            // 所以这里不再乘任何系数。
            if (IsEquipped(CharmOwner.Noel, ShamanId) &&
                (IsPlayerMagicKind(kind) || shotgunFlavored))
            {
                mult *= ShamanDamageMult;
            }
            if (IsEquipped(CharmOwner.Noel, PowerId) && IsPowerBoostKind(kind))
            {
                mult *= PowerDamageMult;
            }
            if (IsHeavyFocusActive)
            {
                mult *= HeavyBlowFocusMult;
            }
            // 护符20 效果8：亡者之怒期间诺艾尔造成的伤害 +75%（与上面几项同一乘区连乘）
            if (_noelFuryActive)
            {
                mult *= KnightInCradlePlugin.FuryDamageMult;
            }
            // 护符27 深度聚集：蓄力完成后的"下一次伤害 +25%"（法术 / 魔法霰弹 / 霰弹变种）
            mult = ApplyDeepGatherNextDamage(kind, shotgunFlavored, mult);
            return mult;
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
                      IsEquipped(CharmOwner.Noel, HeavyBlowId) || IsEquipped(CharmOwner.Noel, DeepGatherId) ||
                      IsEquipped(CharmOwner.Noel, NailMasterId) || _noelFuryActive)) // 护符35 的 ×5 也走这个乘区
                {
                    // 注意：护符27 深度聚集的"下一击 +25%"也走这个乘区，
                    // 所以它的佩戴状态必须一起放行，否则只戴深聚时这里会直接早退（= 加成不生效）。
                    return;
                }
                if (Mg == null || Atk == null || !(Mg.Caster is PRNoel))
                {
                    return;
                }
                // 萨满之石（法术 + 魔法霰弹及其变种）与坚固力量（骨钉系技能）都抬高这一发的基准伤害，
                // 两者同时满足就连乘 —— 于是"装了萨满之石的魔法霰弹及其变种" = ×1.25 ×1.25
                // 另含护符16 沉重之击：进入"会心"后诺艾尔造成的伤害 +40%（不限定招式），同样连乘。
                float mult = NoelFinalDamageMult(Mg.kind, IsNoelShotgunFlavored(Mg));
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
                Atk.hpdmg_current = __state.Cur;
                Atk.fix_damage = __state.Fix;
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
        private static bool SturdyHpDamagePrefix(M2Attackable __instance, AttackInfo Atk, ref int val)
        {
            // 护符35 骨钉大师的荣耀（2026-09-27 重做）：诺艾尔的**骨钉系**攻击改成**固定伤害**，
            // 每击 20（携带护符13 坚固力量时 25）。
            // 挂在这里是因为 `M2Attackable.applyHpDamage` 是魔物掉血的最后一站，
            // `val` 已经是"所有伤害发布率 / 敌人减伤算完之后真正要扣的血"，
            // 直接覆盖它就能保证显示与扣除都是 20/25（不会出现 20 被再打七折变成 13 的情况）。
            if (__instance is NelEnemy && !IsKnightMode &&
                IsEquipped(CharmOwner.Noel, NailMasterId) &&
                IsNoelNailAttack(Atk))
            {
                val = KnightInCradlePlugin.NailMasterFixedDamage;
                return true;
            }
            if (!(__instance is PRNoel noel) || val <= 0)
            {
                return true;
            }
            // 护符过载（需求 2026-09-27）：诺艾尔每过载 1 个槽孔，受到的伤害 +25%
            val = ApplyNoelOverchargeDamage(val);
            // 护符20 效果4：亡者之怒的 HP 流失把自己耗死时的收尾调用 —— 直接走原版结算，
            // 不再触发受击被动（幼虫之歌回魔 / 苦痛荆棘反击 / 亡者之怒自身的阈值改写）。
            if (_noelFuryDying)
            {
                return true;
            }
            // 护符20 亡者之怒：魔物攻击若会把 HP 打到低于阈值 → 回到阈值 + 触发（效果1）
            if (TryTriggerNoelFury(noel, Atk, ref val))
            {
                return true;
            }
            // 护符22 巴尔德之壳：咏唱中被壳保护 —— 不扣血，且不算"受伤"
            // （幼虫之歌/苦痛荆棘都不触发，与小骑士侧"壳挡下的攻击不计为受伤"一致）
            if (IsNoelShellActive(noel))
            {
                val = 0;
                return true;
            }
            // 护符9 幼虫之歌：受到伤害 → 立刻回 20MP（用"原始伤害"判断，先于坚硬外壳的改写）
            TryGrantGrubsongMp();
            // 护符21 苦痛荆棘：受到伤害 → 对半径 3 格内的敌人反击（同样用"原始伤害"）
            TryThornsOfAgony(noel, val);
            // 护符30 乔尼的祝福：扣血改由魔力池承担（HP 条不动）——放在被动之后，受击被动照常触发
            if (JoniBlessingActive(noel))
            {
                if (_joniDying)
                {
                    return true; // 魔力池打空后的强制死亡：走原版 HP 结算
                }
                // 护符3 坚硬外壳 + 护符30 乔尼的祝福（2026-09-24 组合规则）：
                // 只保留这两条，次数血不再参与（见 TickNoelSturdyCharm 的说明）：
                //   ① 单次受到的伤害上限 50；② 受伤后 2 秒锁魔力池（这段时间内不再受伤）。
                //
                // ⚠ 为什么"2 秒无敌"不能用 AIC 原生 NoDamage：乔尼这条路是**拦下原本的
                // HP 结算**（返回 false 跳过 `applyHpDamage` 本体），而原版的无敌判定正是
                // `applyHpDamage` 里的第一句 `applyHpDamageRatio(Atk) == 0 → return 0`
                // （`M2Attackable.cs:263-303`）。跳过本体 = 连原版无敌判定一起跳过，
                // 所以 `NoDamage.Add(120)` 对魔力池不起作用（上一版"没给 2 秒无敌"的原因）。
                // 这里改成模组自己计时：锁蓝期间一律不受伤害。
                if (IsEquipped(CharmOwner.Noel, SturdyId))
                {
                    if (Time.time < _joniSturdyMpLockUntil)
                    {
                        return false; // 锁蓝中：伤害作废（不扣魔力）
                    }
                    if (val > SturdyJoniDamageCap)
                    {
                        val = SturdyJoniDamageCap;
                    }
                    JoniRedirectDamageToMp(noel, val);
                    _joniSturdyMpLockUntil = Time.time + SturdyJoniMpLockSeconds;
                    return false; // 不走原本的 HP 结算
                }
                // 其余情况尊重 AIC 自己的无敌帧（原版受击后的 1.3 秒等）：
                // 同样因为跳过了本体，这一步必须自己补上，否则怪物贴身时会连续扣魔。
                if (noel.isNoDamageActive())
                {
                    return false;
                }
                _joniSturdyMpLockUntil = 0f; // 只挂了乔尼：组合计时清空，避免残留
                JoniRedirectDamageToMp(noel, val);
                return false; // 不再走原本的 HP 结算
            }
            if (!_noelSturdyActive)
            {
                return true;
            }
            // ≤ 20 → 0；> 20 → 1
            val = val <= SturdyDamageThreshold ? 0 : 1;
            // 掉血后 HUD 的数字也要立刻更新（原版受伤流程走 cushion 分支，不会置 redraw_bar_num）
            RefreshNoelHudHp();
            // 无论记成 0 还是 1，都立刻给 2 秒无敌
            GrantNoelInvincible(noel, SturdyInvincibleFrames);
            return true;
        }

        /// <summary>
        /// 给诺艾尔上无敌帧：走 AIC 原生 `M2NoDamageManager`（挂在 `M2Attackable.NoDamage` 上），
        /// 后续伤害由游戏自己挡掉，而不是模组另做一套计时。
        /// （护符3 坚硬外壳、护符3+护符30 组合都用它，帧数 60fps 基准。）
        /// </summary>
        private static void GrantNoelInvincible(PRNoel noel, float frames)
        {
            try
            {
                if (noel != null && PrNoDamageField != null &&
                    PrNoDamageField.GetValue(noel) is M2NoDamageManager nd)
                {
                    nd.Add(frames);
                }
            }
            catch (Exception)
            {
            }
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
            NusiWeakByCyclone.Apply(harmony); // 需求：护符35 时旋风斩也能把森之领主打进虚弱
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
                // 护符32 蘑菇孢子（诺艾尔侧）：诺艾尔免疫蘑菇雾气
                // （CombatGuard 那两个只在骑士模式拦，诺艾尔模式归这里管）
                try
                {
                    MethodInfo gasLevel = AccessTools.Method(typeof(PR), "applyGasDamage",
                        new[] { typeof(MistManager.MistKind), typeof(float) });
                    if (gasLevel != null)
                    {
                        harmony.Patch(gasLevel, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelMushroomMistLevelPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    MethodInfo gasAtk = AccessTools.Method(typeof(PR), "applyGasDamage",
                        new[] { typeof(MistManager.MistKind), typeof(MistAttackInfo) });
                    if (gasAtk != null)
                    {
                        harmony.Patch(gasAtk, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelMushroomMistAtkPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符32 效果2：诺艾尔打到蘑菇 → 给 1 个满级黑棉孢子（挂蘑菇自己的 override）
                    MethodInfo mushDmg = AccessTools.Method(typeof(NelNMush), "applyDamage",
                        new[]
                        {
                            typeof(NelAttackInfo), typeof(HITTYPE).MakeByRefType(), typeof(bool),
                        });
                    if (mushDmg != null)
                    {
                        harmony.Patch(mushDmg, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(MushroomApplyDamagePostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符33 锋利之影（诺艾尔侧）效果1：闪避/幻影闪避/护盾冲击/环轨护盾失效
                    MethodInfo skillEnable = AccessTools.Method(typeof(M2PrSkill), "isEnable",
                        new[] { typeof(SkillManager.SKILL_TYPE) });
                    if (skillEnable != null)
                    {
                        harmony.Patch(skillEnable, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelShadowSkillDisablePrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符33：受身术（PR.STATE.UKEMI）失效
                    MethodInfo prChangeStateUkemi = AccessTools.Method(typeof(PR), "changeState",
                        new[] { typeof(PR.STATE) });
                    if (prChangeStateUkemi != null)
                    {
                        harmony.Patch(prChangeStateUkemi, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelShadowUkemiBlockPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符33 效果2：伪咏唱期间把游戏请求的待机姿势改写为咏唱姿势
                    // （只留一个写姿势的人，避免两路抢导致动画每帧重置）
                    MethodInfo prAnmSetPose = AccessTools.Method(typeof(PrAnimator), "setPose",
                        new[] { typeof(string), typeof(int), typeof(bool) });
                    if (prAnmSetPose != null)
                    {
                        harmony.Patch(prAnmSetPose, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelShadowChantPosePrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符33 冲刺段：隐藏本体期间拒绝一切状态注入（中毒/麻痹/束缚…）
                    MethodInfo serAdd = AccessTools.Method(typeof(M2Ser), "Add",
                        new[] { typeof(SER), typeof(int), typeof(int), typeof(bool) });
                    if (serAdd != null)
                    {
                        harmony.Patch(serAdd, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelDashSerAddPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符33：蓄力（长按护盾键）期间锁定移动/攻击/法术键
                    MethodInfo inputLock = AccessTools.Method(typeof(EV), "lockPrInputManipulate",
                        new[] { typeof(KEY.SIMKEY), typeof(bool), typeof(bool) });
                    if (inputLock != null)
                    {
                        harmony.Patch(inputLock, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(NoelShadowChantInputLockPrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    // 护符34 乌恩之形：不会被魔物抓取/吞下 + 蹲下时禁止魔物锁定
                    MethodInfo prInitAbsorb = AccessTools.Method(typeof(PR), "initAbsorb");
                    // 护符20 亡者之怒：自动"圣光爆发"期间跳过魔力消耗
                    MethodInfo burstMp = AccessTools.Method(typeof(PR), "applyBurstMpDamage",
                        new[] { typeof(int) });
                    if (burstMp != null)
                    {
                        try
                        {
                            harmony.Patch(burstMp, prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(FuryBurstMpDamagePrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        }
                        catch (Exception ex)
                        {
                            KnightInCradlePlugin.PluginLog?.LogWarning(
                                "[KIC][亡者之怒] applyBurstMpDamage 补丁挂载失败：" + ex.Message);
                        }
                    }
                    if (prInitAbsorb != null)
                    {
                        try
                        {
                            harmony.Patch(prInitAbsorb, prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(UnnInitAbsorbPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        }
                        catch (Exception ex)
                        {
                            KnightInCradlePlugin.PluginLog?.LogWarning(
                                "[KIC][乌恩之形] PR.initAbsorb 补丁挂载失败：" + ex.Message);
                        }
                    }
                    MethodInfo naiAimUnn = AccessTools.PropertySetter(typeof(NAI), "AimPr");
                    if (naiAimUnn != null)
                    {
                        try
                        {
                            harmony.Patch(naiAimUnn, prefix: new HarmonyMethod(
                                typeof(CharmEffects).GetMethod(nameof(NaiAimPrSetUnnPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        }
                        catch (Exception ex)
                        {
                            KnightInCradlePlugin.PluginLog?.LogWarning(
                                "[KIC][乌恩之形] NAI.AimPr 补丁挂载失败：" + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][护符32/33] 补丁挂载失败：" + ex.Message);
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
                    // 护符20 效果8：诺艾尔造成伤害时附加 10 点真伤（同一个挂点的**后缀**）
                    try
                    {
                        harmony.Patch(sturdyDmg, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(FuryTrueDamagePostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    catch (Exception ex)
                    {
                        KnightInCradlePlugin.PluginLog?.LogWarning(
                            "[KIC][亡者之怒] 附加真伤补丁挂载失败：" + ex.Message);
                    }
                }
                // 护符30 乔尼的祝福（诺艾尔侧）：回血改成回魔（HP 条不动）
                // 注意挂的是 PR 的 override（PR.cureHp 覆盖了 M2Attackable 的同名方法）
                MethodInfo cureHp = AccessTools.Method(typeof(PR), "cureHp", new[] { typeof(int) });
                if (cureHp != null)
                {
                    harmony.Patch(cureHp, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(JoniCureHpPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符30 乔尼的祝福（诺艾尔侧）：不显示 HP 条下方的数字
                MethodInfo drawString = AccessTools.Method(typeof(BMListChars), "DrawScaleStringTo");
                if (drawString != null)
                {
                    harmony.Patch(drawString, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(JoniHideHpNumberPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符30 乔尼的祝福（诺艾尔侧）：HP 条填充段染成 #0045FF
                // （与骑士血条染色同一挂点：UIStatus.redrawAll 后缀，重绘瞬间改网格顶点颜色）
                MethodInfo redrawAll = AccessTools.Method(typeof(UIStatus), "redrawAll");
                if (redrawAll != null)
                {
                    harmony.Patch(redrawAll, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(JoniRedrawAllPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符21 苦痛荆棘（诺艾尔侧）：场景中的荆棘/尖刺（MAPDMG.SPIKE）对诺艾尔无效。
                // 玩家侧的地图伤害入口是 PR.applyDamageFromMap 这个 override（`nel/PR.cs:3100`），
                // 前缀直接返回 null = 这一次地图伤害不生效。
                MethodInfo mapDmg = AccessTools.Method(typeof(PR), "applyDamageFromMap",
                    new[]
                    {
                        typeof(M2MapDamageContainer.M2MapDamageItem), typeof(AttackInfo),
                        typeof(float), typeof(float), typeof(bool),
                    });
                if (mapDmg != null)
                {
                    harmony.Patch(mapDmg, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(ThornsMapDamagePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符20 亡者之怒（2026-09-26 追加）：期间删去 HP 缓冲条。
                // 挂 UIStatus.fineHpRatio（受伤/治疗时唯一把 cushion_hp 加上去的入口）后缀。
                MethodInfo uiFineHp = AccessTools.Method(typeof(UIStatus), "fineHpRatio",
                    new[] { typeof(bool), typeof(bool) });
                if (uiFineHp != null)
                {
                    try
                    {
                        harmony.Patch(uiFineHp, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(FuryHideHpCushionPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    catch (Exception ex)
                    {
                        KnightInCradlePlugin.PluginLog?.LogWarning(
                            "[KIC][亡者之怒] UIStatus.fineHpRatio 补丁挂载失败：" + ex.Message);
                    }
                }
                // 护符20 亡者之怒（2026-09-26 追加）：期间关闭 GaugeSaver 的"缓冲回血"。
                MethodInfo gsaverCure = AccessTools.Method(typeof(DIFF), "cureHpFromGSaver",
                    new[] { typeof(PR), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
                if (gsaverCure != null)
                {
                    try
                    {
                        harmony.Patch(gsaverCure, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(FuryNoGsaverCurePrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    catch (Exception ex)
                    {
                        KnightInCradlePlugin.PluginLog?.LogWarning(
                            "[KIC][亡者之怒] DIFF.cureHpFromGSaver 补丁挂载失败：" + ex.Message);
                    }
                }
                // 护符22 巴尔德之壳（诺艾尔侧）：壳展开期间拦截整次伤害结算，并计抵挡次数。
                // 挂 M2PrADmg.applyDamage 的 6 参核心重载（玩家受伤总入口，在 NoDamage 判定之前）。
                MethodInfo prDmgShell = AccessTools.Method(typeof(M2PrADmg), "applyDamage",
                    new[]
                    {
                        typeof(NelAttackInfo), typeof(HITTYPE).MakeByRefType(), typeof(bool),
                        typeof(string), typeof(bool), typeof(bool),
                    });
                if (prDmgShell != null)
                {
                    harmony.Patch(prDmgShell, prefix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(NoelShellDamagePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符3+护符30 组合：锁魔力池窗口内，整次受击在伤害管线最外层作废
                // （同一个挂点；HP 伤害之外的"魔力伤害"走 splitMpByDamage，必须在这里拦）
                if (prDmgShell != null)
                {
                    // 单独 try/catch：CharmEffects.Apply 的失败是"静默中断后续全部补丁"，
                    // 不能因为这一条挂不上就把后面的护符效果一起带走。
                    try
                    {
                        harmony.Patch(prDmgShell, prefix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(JoniSturdyLockDamagePrefix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    catch (Exception ex)
                    {
                        KnightInCradlePlugin.PluginLog?.LogWarning(
                            "[KIC][锁蓝] 拦截补丁挂载失败：" + ex.Message);
                    }
                }
                // 护符26 快速聚集（诺艾尔侧）：诺艾尔咏唱速度 +25%
                // 挂 PR.getCastingTimeScale（咏唱推进速度），只对"正在咏唱的那一发"生效
                MethodInfo castScale = AccessTools.Method(typeof(PR), "getCastingTimeScale",
                    new[] { typeof(MagicItem) });
                if (castScale != null)
                {
                    harmony.Patch(castScale, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(FastGatherCastScalePostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 护符23 吸虫之巢（诺艾尔侧）：纯白之箭 / 聚能火球改成喷吸虫。
                // 挂 MagicItem.explode(bool) 的后缀：返回值就是"这一发原版子弹"，
                // 且只有法术释放会传 do_not_run1 = true（子弹自己被打掉时是 false）。
                MethodInfo magicExplode = AccessTools.Method(typeof(MagicItem), "explode",
                    new[] { typeof(bool) });
                if (magicExplode != null)
                {
                    harmony.Patch(magicExplode, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(NoelNestMagicExplodePostfix),
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
                        // 护符18 修长之钉（诺艾尔侧）：登记一道白色弧带（长度=判定触及距离）
                        harmony.Patch(smallAttack, postfix: new HarmonyMethod(
                            typeof(CharmEffects).GetMethod(nameof(LongNailSmallAttackPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                // 护符18 修长之钉（诺艾尔侧）：判定侧——手杖 reach 倍率 + 总触及距离精确补足
                MethodInfo reachRatio = AccessTools.Method(typeof(PrCaneEquip), "reach_ratio");
                if (reachRatio != null)
                {
                    harmony.Patch(reachRatio, postfix: new HarmonyMethod(
                        typeof(CharmEffects).GetMethod(nameof(LongNailReachRatioPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
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
            // 小骑士侧一直存在；第二部分起诺艾尔侧同样生效（按"当前操控角色"的护符集合判断）
            return IsEquippedForCurrentPlayer(MushroomId) && IsMushroomFamily(en);
        }

        // ================= 护符32 蘑菇孢子（诺艾尔侧：免疫蘑菇雾气 + 攻击蘑菇获得道具） =====
        // ================= 护符33 效果2：长按护盾键 → 咏唱姿势 + 金色粒子 =================
        private const int ShadowChantParticleCap = 256;      // 同时存在的粒子上限（网格容量同值）
        private const float ShadowChantParticleLife = 1.2f;  // 粒子最长存活（秒，同小骑士蓄力）
        private const float ShadowChantSpeedMin = 6f;        // 向中心收敛速度下限（格/秒；2026-09-24 翻倍：3→6）
        private const float ShadowChantSpeedMax = 8f;        // 上限（4→8）
        private const float ShadowChantSpawnRadMin = 1f;     // 生成半径下限（格）
        private const float ShadowChantSpawnRadMax = 1.8f;   // 上限
        private const float ShadowChantSizeMin = 0.1f;       // 粒子直径下限（格）
        private const float ShadowChantSizeMax = 0.18f;      // 上限

        /// <summary>金色收敛粒子（字段含义同小骑士 `LightDotParticle` 的那几个坐标/速度字段）。</summary>
        private sealed class ShadowChantParticle
        {
            public float X;
            public float Y;
            public float Age;
            public float Life;
            public float Size;
            public float Speed;
            /// <summary>true = 精华：从中心向四周扩散；false = 黄色圆点：从四周向中心聚集。</summary>
            public bool Outward;
            /// <summary>精华向外扩散的**最远距离**（格）：与黄色粒子的生成半径同一套随机值。</summary>
            public float TargetDist;
        }

        private static readonly List<ShadowChantParticle> _noelShadowParticles =
            new List<ShadowChantParticle>();
        private static float _noelShadowHoldTimer;
        private static bool _noelShadowChanting;
        private static MeshDrawer _noelShadowMesh;
        private static Material _noelShadowMat;
        private static M2RenderTicket _noelShadowTicket;
        private static Map2d _noelShadowMap;
        private static Texture2D _noelShadowDotTex;
        // ---- 蓄力完成段（长按冲刺键 1 秒后）：粒子改为向外扩散 + 中心光圈 ----
        private static MeshDrawer _noelChargeAuraMesh;
        private static Material _noelChargeAuraMat;
        private static M2RenderTicket _noelChargeAuraTicket;
        private static Map2d _noelChargeAuraMap;
        private static float _noelChargeAuraTime;
        private static float _noelShadowDashTimer;
        private static bool _noelShadowEssence;

        // ---- 护符33 冲刺段（蓄力完成后松开护盾键触发）----
        private enum ShadowDashPhase
        {
            None,
            Shrink, // 光圈向中心缩小
            Burst,  // 隐藏本体 + 发射 dash_burst 图片
        }
        private static ShadowDashPhase _shadowDashPhase = ShadowDashPhase.None;
        private static float _shadowDashPhaseTimer;
        private static float _shadowDashAuraScale = 1f;
        private static bool _shadowDashBodyHidden;
        private static Texture2D _shadowDashBurstTex;
        private static MeshDrawer _shadowDashBurstMesh;
        private static Material _shadowDashBurstMat;
        private static M2RenderTicket _shadowDashBurstTicket;
        private static Map2d _shadowDashBurstMap;
        private static float _shadowDashBurstX;
        private static float _shadowDashBurstY;
        private static float _shadowDashBurstDir;
        private static float _shadowDashPrevX;
        private static int _shadowDashStuckFrames;
        /// <summary>冲刺期间锁重力的 key（`Phy.addLockGravity` 用）。</summary>
        private static readonly object ShadowDashGravityKey = new object();
        /// <summary>冲刺期间被钉住的身体中心 Y（水平冲刺：不加重力、不下坠）。</summary>
        private static float _shadowDashLockY;
        // ---- 冲刺伤害：缓存"最近一次挥击"的攻击包（同蜕变挽歌的做法）----
        /// <summary>最近一次**轻攻击**（PR_PUNCH）的攻击包副本。</summary>
        private static NelAttackInfo _dashPunchAtk;
        private static float _dashPunchRatio = 1f;
        /// <summary>最近一次**魔法霰弹**（含变种）的攻击包副本。</summary>
        private static NelAttackInfo _dashShotgunAtk;
        private static float _dashShotgunRatio = 1f;
        /// <summary>本次冲刺是否按"魔法霰弹"结算（冲刺开始那一刻法杖是否带霰弹附魔）。</summary>
        private static bool _dashUseShotgun;
        /// <summary>本次冲刺已经打过的敌人（同一只只结算一次）。</summary>
        private static readonly HashSet<NelEnemy> _dashHitEnemies = new HashSet<NelEnemy>();
        /// <summary>本次冲刺的霰弹命中特效是否已经播过（只播一次）。</summary>
        private static bool _dashShotgunFxDone;

        /// <summary>冲刺段是否正在进行（光圈缩小 / 发射中）。</summary>
        public static bool NoelShadowDashActive => _shadowDashPhase != ShadowDashPhase.None;

        /// <summary>冲刺段是否处于"本体应被隐藏"的阶段（发射中）。</summary>
        public static bool NoelShadowDashHiding => _shadowDashPhase == ShadowDashPhase.Burst;

        /// <summary>蓄力（长按护盾键）期间：锁定移动键 / 攻击键 / 法术键。</summary>
        private static bool NoelShadowChantInputLockPrefix(KEY.SIMKEY key, ref bool __result)
        {
            try
            {
                if (IsKnightMode)
                {
                    return true;
                }
                // 移动（L/R/T/B，含 LA/RA/TA/BA 同值）、攻击（Z）、法术（X）、跳跃（JUMP）
                bool isDirection = key == KEY.SIMKEY.L || key == KEY.SIMKEY.R ||
                                   key == KEY.SIMKEY.T || key == KEY.SIMKEY.B;
                bool isJump = key == KEY.SIMKEY.JUMP;
                // 护符33：伪咏唱 / 冲刺期间锁移动/攻击/法术/跳跃
                if (_noelShadowChanting || NoelShadowDashActive)
                {
                    if (isDirection || isJump || key == KEY.SIMKEY.Z || key == KEY.SIMKEY.X)
                    {
                        __result = false; // = 这几个键在蓄力期间视为没按
                        return false;
                    }
                }
                // 护符35 旋风斩：方向键不再移动/蹲下/跳跃（改由模组直接平移），
                // 跳跃键与**攻击键**一并锁掉（需求 2026-09-25）
                if (NailMasterSpinActive && (isDirection || isJump || key == KEY.SIMKEY.Z))
                {
                    __result = false;
                    return false;
                }
                // 护符35：**魔法键锁掉**（需求1）
                if (NailMasterEquipped && key == KEY.SIMKEY.X)
                {
                    __result = false;
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>此刻是不是"精华"阶段（长按冲刺键够久；供 HUD 白闪与绘制判断）。</summary>
        public static bool NoelShadowEssence => _noelShadowEssence;

        // ==================== 护符33 冲刺段 ====================
        /// <summary>
        /// 冲刺段（需求 2026-09-25）：**蓄力完成后松开护盾键** →
        /// ① 0.1 秒内让 `nail_charge_effect` 光圈向诺艾尔中心缩小；
        /// ② 缩到最小的瞬间：删掉光圈 + 白屏 0.07 秒；隐藏诺艾尔本体，
        ///    在她中心把 `dash_burst0000.png` 以 8 格/秒**向前**发射（持续 0.5 秒）；
        /// ③ 发射结束：再白屏 0.07 秒 + 诺艾尔还原。
        /// </summary>
        private static bool NoelShadowCanDash(PRNoel pr)
        {
            try
            {
                return pr != null && pr.is_alive && !IsKnightMode &&
                       NoelPrStateIs(pr, PR.STATE.NORMAL);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 开始冲刺段。`instant = true`（随身佩戴冲刺大师、按护盾键直接冲）时**跳过 0.1 秒的光圈缩小**，
        /// 直接进入发射段，手感更跟手；冲刺进行中一律不接受新的冲刺。
        /// </summary>
        private static void StartNoelShadowDash(PRNoel pr, bool instant)
        {
            if (_shadowDashPhase != ShadowDashPhase.None)
            {
                return; // 冲刺期间不能再次冲刺
            }
            // 冲刺消耗 MP（默认 70）：不足则不冲刺（音效/白闪/伤害都不发生）
            // 羁绊 14+33：佩戴法术扭曲者时改成 `TwistedBondMpCost`（默认 60）。
            // 需求 2026-09-27：蓄力完成时**法杖没有附魔魔法霰弹**的话，只消耗 30MP。
            // 判据与冲刺伤害同源：`IsNoelMagicChanting`（手里握着魔法蓄力 = 附了霰弹）。
            bool dashEnchanted = false;
            try
            {
                dashEnchanted = pr != null && IsNoelMagicChanting(pr);
            }
            catch (Exception)
            {
            }
            int cost;
            if (!dashEnchanted)
            {
                cost = KnightInCradlePlugin.ShadowDashMpCostNoEnchant;
            }
            else
            {
                cost = IsEquipped(CharmOwner.Noel, SpellTwisterId)
                    ? KnightInCradlePlugin.ShadowDashTwistedMpCost
                    : KnightInCradlePlugin.ShadowDashMpCost;
            }
            if (cost > 0 && pr != null)
            {
                try
                {
                    if (pr.get_mp() < cost)
                    {
                        return;
                    }
                    pr.applyMpDamage(cost, true, null, false, false);
                    RefreshNoelHudMp();
                }
                catch (Exception)
                {
                }
            }
            _shadowDashPhase = ShadowDashPhase.Shrink;
            _shadowDashPhaseTimer = 0f;
            _shadowDashAuraScale = 1f;
            // 本次冲刺按哪种伤害结算：冲刺开始那一刻法杖是否带"魔法霰弹附魔"
            // ⚠ 判据**不能**用 `pr.isShotgunState()` —— 那个只在"挥出去的霰弹/变种"那几个
            // `*_SHOTGUN` 状态里为 true（`PR.cs:6012-6035`），站着的时候恒为 false。
            // 真正表示"法杖附了魔法霰弹"的是**手里握着魔法蓄力**（CurMg 存在且蓄着 ≥1 MP），
            // 也就是下面这个既有判据 `IsNoelMagicChanting`（护符22 巴尔德之壳用的同一个）。
            _dashUseShotgun = false;
            try
            {
                _dashUseShotgun = pr != null && IsNoelMagicChanting(pr);
            }
            catch (Exception)
            {
            }
            _dashHitEnemies.Clear();
            _dashShotgunFxDone = false;
            // 松开护盾键 / 按下护盾键的瞬间：播放冲刺爆发音效（hero_super_dash_burst）
            try
            {
                DashAudio.PlaySuperBurst();
            }
            catch (Exception)
            {
            }
            if (instant)
            {
                EnterNoelShadowBurst(pr);
            }
        }

        private static void TickNoelShadowDash(PRNoel pr)
        {
            try
            {
                if (_shadowDashPhase == ShadowDashPhase.None)
                {
                    if (_shadowDashBodyHidden && pr != null)
                    {
                        KnightInCradleBehaviour.SetNoelHiddenForDash(pr, false);
                        _shadowDashBodyHidden = false;
                    }
                    return;
                }
                // 用**游戏时间**推进（`Map2d.TS`：命中停滞/慢动作时会 →0），
                // 否则打中敌人的那几帧停滞会把冲刺时间白吃掉（也会误判成"撞墙"）
                float dt = Time.deltaTime;
                float gameDt = dt * Mathf.Clamp(Map2d.TS, 0f, 1f);
                _shadowDashPhaseTimer += gameDt;
                if (_shadowDashPhase == ShadowDashPhase.Shrink)
                {
                    float t = KnightInCradlePlugin.ShadowDashShrinkSeconds > 0f
                        ? Mathf.Clamp01(_shadowDashPhaseTimer / KnightInCradlePlugin.ShadowDashShrinkSeconds)
                        : 1f;
                    _shadowDashAuraScale = 1f - t;
                    if (t >= 1f)
                    {
                        EnterNoelShadowBurst(pr);
                    }
                    return;
                }
                // Burst：隐藏本体 + 图片向前飞
                if (pr != null)
                {
                    // 隐藏本体：真正生效的那一次在 KnightInCradleBehaviour.LateUpdate（渲染前），
                    // 这里再压一遍保证同帧生效（tick 早于游戏 Update 时会被覆盖，LateUpdate 会补上）
                    KnightInCradleBehaviour.SetNoelHiddenForDash(pr, true);
                    // 让**游戏自己的物理**来推进（`walkBy` 带 `checkwall`），这样撞墙会被挡住，
                    // 不会像 `setTo` 那样整个人穿墙过去（2026-09-25 用户反馈）。
                    float dx = _shadowDashBurstDir * KnightInCradlePlugin.ShadowDashBurstSpeed / 60f;
                    try
                    {
                        pr.walkBy(FOCTYPE.WALK, dx, 0f, true);
                        // 水平冲刺：不下坠。重力已锁（EnterNoelShadowBurst 里 addLockGravity），
                        // 这里再把残余的垂直速度用反向位移抵消，把身体中心 Y 钉在出发高度。
                        float dy = _shadowDashLockY - NoelBodyCenterY(pr);
                        if (Mathf.Abs(dy) > 0.001f)
                        {
                            pr.walkBy(FOCTYPE.WALK, 0f, dy, false);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    // 图片仍然画在诺艾尔**前方** `DashBurstBackOffset` 格 → 观感依旧是"图片带着她冲"
                    _shadowDashBurstX = pr.x + _shadowDashBurstDir * KnightInCradlePlugin.ShadowDashBurstBackOffset;
                    _shadowDashBurstY = NoelBodyCenterY(pr);
                    // 隐藏期间免疫伤害与负面状态
                    ApplyNoelDashImmunity(pr);
                    // 路径伤害：沿路对每只敌人结算一次（3 倍轻攻击 / 3 倍魔法霰弹）
                    CheckNoelDashHits(pr);
                    // 撞墙判定：连续几帧几乎没前进 → 提前收尾，避免图片卡在墙上空转
                    // ⚠ 打中敌人时 AIC 会有"命中停滞"（`Map2d.TS → 0`），那几帧她本来就不会动，
                    // 必须跳过判定，否则一打到第一个目标就误判撞墙、冲刺提前结束。
                    if (Map2d.TS < 0.1f)
                    {
                        _shadowDashStuckFrames = 0;
                    }
                    else if (Mathf.Abs(pr.x - _shadowDashPrevX) < 0.01f)
                    {
                        _shadowDashStuckFrames++;
                        if (_shadowDashStuckFrames >= 5)
                        {
                            EndNoelShadowDash(pr);
                            return;
                        }
                    }
                    else
                    {
                        _shadowDashStuckFrames = 0;
                    }
                    _shadowDashPrevX = pr.x;
                }
                if (_shadowDashPhaseTimer >= KnightInCradlePlugin.ShadowDashBurstSeconds)
                {
                    EndNoelShadowDash(pr);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>① 结束（光圈缩到最小）：删光圈 + 白屏 + 隐藏本体 + 开始发射。</summary>
        private static void EnterNoelShadowBurst(PRNoel pr)
        {
            _shadowDashAuraScale = 0f;
            _shadowDashPhase = ShadowDashPhase.Burst;
            _shadowDashPhaseTimer = 0f;
            EnsureNoelChargeAuraTicket(pr, false); // 删去 nail_charge_effect
            _noelShadowParticles.Clear();          // 一并清掉残留粒子，保持"消失"的干净感
            if (pr != null)
            {
                _shadowDashBurstDir = pr.mpf_is_right >= 0f ? 1f : -1f;
                _shadowDashBurstX = pr.x;
                _shadowDashBurstY = NoelBodyCenterY(pr);
                _shadowDashPrevX = pr.x;
                _shadowDashStuckFrames = 0;
                _shadowDashLockY = _shadowDashBurstY;
                // 水平冲刺：锁掉重力（重力倍率 → 0），否则 `walkBy` 走物理后她会往下掉
                try
                {
                    pr.getPhysic()?.addLockGravity(ShadowDashGravityKey, 0f, -1f);
                }
                catch (Exception)
                {
                }
            }
            KnightInCradleBehaviour.SetNoelHiddenForDash(pr, true);
            _shadowDashBodyHidden = true;
            EnsureNoelShadowDashBurstTicket(pr, true);
            KnightHudDeco.TriggerScreenWhite(KnightInCradlePlugin.ShadowDashFlashSeconds);
        }

        /// <summary>② 发射结束：白屏 + 恢复本体，回到无状态。</summary>
        private static void EndNoelShadowDash(PRNoel pr)
        {
            _shadowDashPhase = ShadowDashPhase.None;
            _shadowDashPhaseTimer = 0f;
            _shadowDashStuckFrames = 0;
            try
            {
                if (pr != null)
                {
                    pr.getPhysic()?.remLockGravity(ShadowDashGravityKey);
                }
            }
            catch (Exception)
            {
            }
            _shadowDashAuraScale = 1f;
            EnsureNoelShadowDashBurstTicket(pr, false);
            KnightInCradleBehaviour.SetNoelHiddenForDash(pr, false);
            _shadowDashBodyHidden = false;
            KnightHudDeco.TriggerScreenWhite(KnightInCradlePlugin.ShadowDashFlashSeconds);
        }

        /// <summary>
        /// 蓄力期间开关"缓降"（空中蓄力时生效）：直接复用 AIC 自己的 `FlgSoftFall` 管线，
        /// 它的回调里就是 `Phy.initSoftFall(chanting_softfall_scale × num, 14 × num)`，
        /// 而 `num` 取决于 `ENHA.EH.falling_cat`（= 原版技能「猫之缓降」）→ 效果与原版一致。
        /// </summary>
        private static void SetNoelChantSoftFall(PRNoel pr, bool active)
        {
            try
            {
                if (pr == null || pr.Skill == null || pr.Skill.FlgSoftFall == null)
                {
                    return;
                }
                if (active)
                {
                    // 只在空中需要；Add 本身幂等（重复添加不重复触发回调）
                    pr.Skill.FlgSoftFall.Add(NoelChantSoftFallKey);
                }
                else if (pr.Skill.FlgSoftFall.hasKey(NoelChantSoftFallKey))
                {
                    pr.Skill.FlgSoftFall.Rem(NoelChantSoftFallKey);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>蓄力缓降用的 FlgSoftFall 键名（AIC 自己用 "MAGIC" 之类；这里用独立键避免互相干扰）。</summary>
        private const string NoelChantSoftFallKey = "KIC_SHADOW_CHANT";

        /// <summary>
        /// 冲刺隐藏期间：让诺艾尔**免疫伤害与负面状态**。
        /// - 免疫帧：`addNoDamage(NDMG._ALL, …)`（覆盖普攻/地图伤害/各类键值判定）；
        /// - 状态：`M2Ser.Add` 前缀直接拒绝（见 `NoelDashSerAddPrefix`）；
        /// - 伤害总闸门：`M2PrADmg.applyDamage` 前缀里再挡一次（见 `JoniSturdyLockDamagePrefix`）。
        /// </summary>
        private static void ApplyNoelDashImmunity(PRNoel pr)
        {
            try
            {
                pr.addNoDamage(NDMG._ALL, 0.2f);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// `M2Ser.Add` 的**唯一**前缀（同一方法上挂两个前缀会让 HarmonyX 报
        /// `IL Compile Error`，所以冲刺免疫与乌恩之形抓取免疫合并在这里）：
        /// ① 冲刺隐藏期间拒绝一切状态注入（中毒/麻痹/束缚/睡眠…）；
        /// ② 佩戴乌恩之形时拒绝抓取类状态（被寄生/虫墙/被吃住/强力抓取/蜘蛛网）。
        /// </summary>
        private static bool NoelDashSerAddPrefix(M2Ser __instance, SER ser, ref M2SerItem __result)
        {
            try
            {
                if (!(__instance.Mv is PRNoel noel) || IsKnightMode)
                {
                    return true;
                }
                if (NoelShadowDashActive)
                {
                    __result = null; // 冲刺隐藏期间：免疫一切状态
                    return false;
                }
                if (IsUnnApplied(noel) && IsUnnGrabSer(ser))
                {
                    __result = null; // 乌恩之形：免疫抓取类状态
                    return false;
                }
                if (NoelSpinInvincible && IsUnnGrabSer(ser))
                {
                    __result = null; // 旋风斩无敌帧：同样不会被抓（被寄生/虫墙/被吃住/强力抓取/蜘蛛网）
                    return false;
                }
                if (_noelFuryBurstFree > 0f && IsFuryBurstTiredSer(ser))
                {
                    __result = null; // 亡者之怒的自动圣光爆发：不导致自己眩晕
                    return false;
                }
                if (IsNoelFuryImmune && IsNoelNegativeSer(ser))
                {
                    __result = null; // 护符20 效果3：亡者之怒期间免疫所有负面状态
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>护符34 乌恩之形：这些状态属于"被魔物抓取/吞下"。</summary>
        private static bool IsUnnGrabSer(SER key)
        {
            return key == SER.PARASITISED || key == SER.WORM_TRAPPED || key == SER.EATEN ||
                   key == SER.STRONG_HOLD || key == SER.WEB_TRAPPED;
        }

        /// <summary>诺艾尔此刻是不是"长按护盾键的伪咏唱"状态（护符33 效果2）。</summary>
        public static bool NoelShadowChanting => _noelShadowChanting;

        /// <summary>
        /// 护符33 效果2 的核心：伪咏唱期间，把**游戏自己请求的待机姿势**改写成护符的咏唱姿势。
        ///
        /// 为什么不自己每帧 `SpSetPose`：AIC 的状态机每帧也会设姿势（NORMAL 下请求 `stand`），
        /// 两路各设一次 → 一帧内姿势来回切 → `PrAnimator.setPose` 每帧都当成"新姿势"重置动画，
        /// 表现就是"当前动作被冻住"。**只留一个写姿势的人**（就是这里）才有动画。
        ///
        /// 挂点：`PrAnimator.setPose(string, int, bool)`（`PR.SpSetPose` 最终就是调它；
        /// `PrBakeAnimator`/`PrNoelAnimator` 的 override 都会 `base.setPose`，所以能拦到）。
        /// 只改写"待机类"标题：`stand` 及 `stand_*`，并跳过 `stand2xxx` 这类**过渡**姿势
        /// （如 stand2sink / stand2confused），避免把起身、下蹲插值也换掉。
        /// </summary>
        private static void NoelShadowChantPosePrefix(PrAnimator __instance, ref string title)
        {
            try
            {
                // 姿势浏览器（调试工具）优先：浏览期间所有姿势请求都改成浏览中的那个
                if (__instance != null && __instance.Pr is PRNoel && NoelPoseBrowser.TryOverride(ref title))
                {
                    return;
                }
                // 护符35 旋风斩：起手 / 循环 / 收尾三个动作名接管姿势
                if (__instance != null && __instance.Pr is PRNoel)
                {
                    string nmPose = NailMasterPoseName();
                    if (!string.IsNullOrEmpty(nmPose))
                    {
                        title = nmPose;
                        return;
                    }
                }
                if (!_noelShadowChanting || IsKnightMode || string.IsNullOrEmpty(title))
                {
                    return;
                }
                if (__instance == null || !(__instance.Pr is PRNoel))
                {
                    return;
                }
                // 待机/移动（stand*）+ **空中**（jump* / fall*，见 `AnimationShufflerNoel.cs:658`：
                // 上升中是 "jump"、下落/滞空是 "fall"）。跳过带 '2' 的过渡姿势（stand2sink、fall2…）。
                bool idle = title == "stand" || title == "jump" || title == "fall" ||
                            ((title.StartsWith("stand", StringComparison.Ordinal) ||
                              title.StartsWith("jump", StringComparison.Ordinal) ||
                              title.StartsWith("fall", StringComparison.Ordinal)) &&
                             title.IndexOf('2') < 0);
                if (!idle)
                {
                    return;
                }
                title = KnightInCradlePlugin.ShadowChantPose;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符33 效果2（第一步）：**长按护盾键** → 播放诺艾尔自身的**魔法咏唱姿势**
        /// （`M2PrSkill` 咏唱时用的 `magic_hold`，见 `M2PrSkill.cs:3814`），
        /// 同时在她周围生成**金色 `#FFCB00` 圆形粒子**并向中心收敛
        /// （生成/收敛数值照搬小骑士骨钉技艺蓄力：每帧 2 个、半径 1~1.8 格、
        /// 速度 3~4 格/秒、寿命 1.2 秒、骑士身后层 PR0）。
        ///
        /// **不碰魔法系统**：这里只 `SpSetPose` 一个姿势，没有任何 `M2PrSkill` 状态改动，
        /// 所以不会弹出法术选择界面、也不会消耗 MP。
        ///
        /// 触发条件：诺艾尔模式 + 佩戴护符33 + 按住护盾键（`PR.isEvadeO()`，也就是 AIC 的
        /// 防御键 = 默认左 Shift）+ **处于 NORMAL 状态**（避免战斗/受击时强行改姿势）。
        /// 长按阈值见配置 `ShadowChantHoldSeconds`（默认 0.25 秒）。
        /// </summary>
        public static void TickNoelShadowChantCharm(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    ReleaseNoelShadowTicket();
                    _noelShadowParticles.Clear();
                    _noelShadowHoldTimer = 0f;
                    _noelShadowChanting = false;
                    return;
                }
                bool armed = !IsKnightMode && IsEquipped(CharmOwner.Noel, ShadowId);
                // 同时佩戴"冲刺大师" → 无需蓄力：按一下护盾键直接冲刺
                bool instant = armed && KnightInCradlePlugin.ShadowDashInstantWithDashmaster &&
                               IsEquipped(CharmOwner.Noel, DashmasterId);
                bool holding = false;
                bool chargedBefore = _noelShadowEssence; // 本帧之前是否处于"蓄力完成"
                // 直接冲刺模式下不走"长按蓄力"这条路（否则按下的同一帧又会开始蓄力）
                if (armed && !instant && pr.is_alive && NoelPrStateIs(pr, PR.STATE.NORMAL))
                {
                    try
                    {
                        holding = pr.isEvadeO();
                    }
                    catch (Exception)
                    {
                        holding = false;
                    }
                }
                if (holding)
                {
                    _noelShadowHoldTimer += Time.deltaTime;
                    if (_noelShadowHoldTimer >= KnightInCradlePlugin.ShadowChantHoldSeconds)
                    {
                        _noelShadowChanting = true;
                    }
                    // 长按**冲刺键**到 EssenceHoldSeconds（默认 1 秒）→ 进入"精华"阶段：
                    // 屏幕四周白闪一次，粒子改为从中心向四周扩散
                    bool dashHeld = false;
                    try
                    {
                        dashHeld = KeyConfig.GetHeld(KnightInCradlePlugin.ShadowEssenceHoldKey, KeyCode.LeftShift);
                    }
                    catch (Exception)
                    {
                        dashHeld = false;
                    }
                    if (dashHeld)
                    {
                        _noelShadowDashTimer += Time.deltaTime;
                    }
                    else
                    {
                        _noelShadowDashTimer = 0f;
                    }
                    _noelShadowEssence = _noelShadowChanting && dashHeld &&
                                         _noelShadowDashTimer >= KnightInCradlePlugin.ShadowEssenceHoldSeconds;
                }
                else
                {
                    _noelShadowHoldTimer = 0f;
                    _noelShadowChanting = false;
                    _noelShadowDashTimer = 0f;
                    _noelShadowEssence = false;
                }
                if (_noelShadowChanting)
                {
                    // 只摆姿势：咏唱动作而已，与真正的施法无关
                    pr.SpSetPose(KnightInCradlePlugin.ShadowChantPose, -1, null, false);
                    // 蓄力完成段：粒子仍是黄色圆点，但方向改成"由内到外"
                    SpawnNoelShadowParticles(KnightInCradlePlugin.ShadowChantParticlesPerFrame,
                        _noelShadowEssence);
                    // 空中蓄力 → 缓降（复用 AIC 自己的 FlgSoftFall："猫之缓降"同一条管线）
                    SetNoelChantSoftFall(pr, true);
                    // 蓄力期间方向键**只改朝向**：移动输入仍被输入锁挡掉（不会真的走动），
                    // 这里直接读原生输入把朝向翻过去
                    try
                    {
                        if (IN.isRO(0))
                        {
                            pr.setAim(AIM.R, false);
                        }
                        else if (IN.isLO(0))
                        {
                            pr.setAim(AIM.L, false);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                else
                {
                    SetNoelChantSoftFall(pr, false);
                }
                UpdateNoelShadowParticles(pr);
                // 粒子（两种方向共用同一张黄色圆点贴图，所以共用一个网格）
                EnsureNoelShadowTicket(pr, _noelShadowParticles.Count > 0);
                // 蓄力完成：诺艾尔中心渲染"沉重之击"那组光圈图片（nail_charge_effect0005~0009）
                if (_noelShadowEssence || NailMasterCharged)
                {
                    _noelChargeAuraTime += Time.deltaTime;
                }
                else
                {
                    _noelChargeAuraTime = 0f;
                }
                // 冲刺段的"缩小中"也要继续画光圈（此时已经松开护盾键，_noelShadowEssence 为 false）
                EnsureNoelChargeAuraTicket(pr, _noelShadowEssence ||
                                               _shadowDashPhase == ShadowDashPhase.Shrink ||
                                               NailMasterCharged);
                // 冲刺段：蓄力完成状态下**松开护盾键** → 进入缩小 → 发射
                if (chargedBefore && !holding && armed && _shadowDashPhase == ShadowDashPhase.None)
                {
                    StartNoelShadowDash(pr, false);
                }
                // 直接冲刺（冲刺大师 + 锋利之影）：按下护盾键的瞬间就冲，无需蓄力
                if (instant && _shadowDashPhase == ShadowDashPhase.None && NoelShadowCanDash(pr))
                {
                    bool pressed = false;
                    try
                    {
                        pressed = pr.isEvadePD(2);
                    }
                    catch (Exception)
                    {
                        pressed = false;
                    }
                    if (pressed)
                    {
                        StartNoelShadowDash(pr, true);
                    }
                }
                TickNoelShadowDash(pr);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 蓄力完成段的光圈票据（同"沉重之击"的会心光圈）：
        /// 用 `nail_charge_effect0005~0009` 那组图，画在诺艾尔中心，身后层 PR0。
        /// </summary>
        private static void EnsureNoelChargeAuraTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelChargeAuraTicket();
                return;
            }
            if (_heavyFocusAuraTex == null)
            {
                _heavyFocusAuraTex = LoadHeavyFocusAuraTextures();
            }
            if (_heavyFocusAuraTex == null)
            {
                return; // 素材缺失：只是不显示光圈
            }
            if (_noelChargeAuraMesh != null && _noelChargeAuraMap == mp && _noelChargeAuraTicket != null)
            {
                return;
            }
            ReleaseNoelChargeAuraTicket();
            _noelChargeAuraMap = mp;
            _noelChargeAuraMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _noelChargeAuraMesh.draw_gl_only = true;
            _noelChargeAuraMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelChargeAuraMat.EnableKeyword("NO_PIXELSNAP");
            _noelChargeAuraMesh.activate("noel_shadow_charge_aura", _noelChargeAuraMat, false, MTRX.ColWhite, null);
            _noelChargeAuraTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelChargeAuraMesh, _noelChargeAuraMesh, null, null);
        }

        private static void ReleaseNoelChargeAuraTicket()
        {
            try
            {
                if (_noelChargeAuraTicket != null && _noelChargeAuraMap != null &&
                    _noelChargeAuraMap.MovRenderer != null)
                {
                    _noelChargeAuraMap.MovRenderer.deassignDrawable(_noelChargeAuraTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelChargeAuraMat != null)
                {
                    IN.DestroyOne(_noelChargeAuraMat);
                }
            }
            catch (Exception)
            {
            }
            _noelChargeAuraTicket = null;
            _noelChargeAuraMesh = null;
            _noelChargeAuraMat = null;
            _noelChargeAuraMap = null;
        }

        /// <summary>蓄力完成光圈绘制：与"沉重之击"同一套（同贴图、同 20fps、同锚点与偏移）。</summary>
        private static bool PrepareNoelChargeAuraMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelChargeAuraMap;
            if (mp == null || _noelChargeAuraMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelChargeAuraMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            bool wanted = _noelShadowEssence || _shadowDashPhase == ShadowDashPhase.Shrink ||
                          NailMasterCharged;
            if (pr == null || _heavyFocusAuraTex == null || !wanted || _shadowDashAuraScale <= 0.001f)
            {
                MdOut = _noelChargeAuraMesh;
                return true;
            }
            int frame = Mathf.Abs((int)(_noelChargeAuraTime * HeavyBlowAuraFps)) % _heavyFocusAuraTex.Length;
            Texture2D tex = _heavyFocusAuraTex[frame];
            if (tex == null)
            {
                MdOut = _noelChargeAuraMesh;
                return true;
            }
            float cy = NoelBodyCenterY(pr) + HeavyBlowAuraOffY;
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(pr.x * mp.CLEN), mp.pixel2uy(cy * mp.CLEN), 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null ? KnightInCradlePlugin.ScaleConfig.Value : 0.325f;
            float mult = HeavyBlowAuraScale * KnightInCradlePlugin.ShadowChargeAuraScale * _shadowDashAuraScale;
            float w = tex.width * scale * mult;
            float h = tex.height * scale * mult;
            _noelChargeAuraMesh.Col = MTRX.ColWhite;
            _noelChargeAuraMesh.initForImgAndTexture(tex);
            _noelChargeAuraMesh.uv_top = 0f;
            _noelChargeAuraMesh.uv_height = 1f;
            _noelChargeAuraMesh.uv_left = 0f;
            _noelChargeAuraMesh.uv_width = 1f;
            _noelChargeAuraMesh.Rect(0f, 0f, w, h, false);
            MdOut = _noelChargeAuraMesh;
            return true;
        }

        /// <summary>冲刺段发射图片（dash_burst0000）的票据：绑当前地图，身后层 PR0。</summary>
        private static void EnsureNoelShadowDashBurstTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelShadowDashBurstTicket();
                return;
            }
            if (_shadowDashBurstTex == null)
            {
                _shadowDashBurstTex = LoadSpriteTexture(KnightInCradlePlugin.ShadowDashBurstSprite);
            }
            if (_shadowDashBurstTex == null)
            {
                return; // 素材缺失：只是不显示这张图
            }
            if (_shadowDashBurstMesh != null && _shadowDashBurstMap == mp && _shadowDashBurstTicket != null)
            {
                return;
            }
            ReleaseNoelShadowDashBurstTicket();
            _shadowDashBurstMap = mp;
            _shadowDashBurstMesh = new MeshDrawer(null, 4 * 4, 6 * 4);
            _shadowDashBurstMesh.draw_gl_only = true;
            _shadowDashBurstMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shadowDashBurstMat.EnableKeyword("NO_PIXELSNAP");
            _shadowDashBurstMesh.activate("noel_shadow_dash_burst", _shadowDashBurstMat, false, MTRX.ColWhite, null);
            _shadowDashBurstTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelShadowDashBurstMesh, _shadowDashBurstMesh, null, null);
        }

        private static void ReleaseNoelShadowDashBurstTicket()
        {
            try
            {
                if (_shadowDashBurstTicket != null && _shadowDashBurstMap != null &&
                    _shadowDashBurstMap.MovRenderer != null)
                {
                    _shadowDashBurstMap.MovRenderer.deassignDrawable(_shadowDashBurstTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_shadowDashBurstMat != null)
                {
                    IN.DestroyOne(_shadowDashBurstMat);
                }
            }
            catch (Exception)
            {
            }
            _shadowDashBurstTicket = null;
            _shadowDashBurstMesh = null;
            _shadowDashBurstMat = null;
            _shadowDashBurstMap = null;
        }

        /// <summary>画发射图：锚在"当前发射位置"，朝诺艾尔面朝方向（UV 镜像同修长之钉弧带）。</summary>
        private static bool PrepareNoelShadowDashBurstMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw,
            int draw_id, out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _shadowDashBurstMap;
            if (mp == null || _shadowDashBurstMesh == null || draw_id != 0)
            {
                return false;
            }
            _shadowDashBurstMesh.clearSimple();
            if (_shadowDashBurstTex == null || _shadowDashPhase != ShadowDashPhase.Burst)
            {
                MdOut = _shadowDashBurstMesh;
                return true;
            }
            float scale = KnightInCradlePlugin.ScaleConfig != null ? KnightInCradlePlugin.ScaleConfig.Value : 0.325f;
            // 渲染大小：ScaleConfig × DashBurstScale × 宽/高各自的额外倍率（可分别调）
            float w = _shadowDashBurstTex.width * scale * KnightInCradlePlugin.ShadowDashBurstScale *
                      KnightInCradlePlugin.ShadowDashBurstWidthRatio;
            float h = _shadowDashBurstTex.height * scale * KnightInCradlePlugin.ShadowDashBurstScale *
                      KnightInCradlePlugin.ShadowDashBurstHeightRatio;
            // 位置：锚点 = 发射路径当前位置；再按配置偏移（X 正值 = 前方，Y 正值 = 下移）
            float px = _shadowDashBurstX + _shadowDashBurstDir * KnightInCradlePlugin.ShadowDashBurstOffsetX;
            float py = _shadowDashBurstY + KnightInCradlePlugin.ShadowDashBurstOffsetY;
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(px * mp.CLEN),
                            mp.pixel2uy(py * mp.CLEN), 0f));
            _shadowDashBurstMesh.Col = MTRX.ColWhite;
            _shadowDashBurstMesh.initForImgAndTexture(_shadowDashBurstTex);
            _shadowDashBurstMesh.uv_top = 0f;
            _shadowDashBurstMesh.uv_height = 1f;
            if (_shadowDashBurstDir > 0f)
            {
                _shadowDashBurstMesh.uv_left = 1f;
                _shadowDashBurstMesh.uv_width = -1f;
            }
            else
            {
                _shadowDashBurstMesh.uv_left = 0f;
                _shadowDashBurstMesh.uv_width = 1f;
            }
            _shadowDashBurstMesh.Rect(0f, 0f, w, h, false);
            MdOut = _shadowDashBurstMesh;
            return true;
        }

        /// <summary>读一张 `assets/hk/sprites/<名>.png`（供冲刺段发射图用）。</summary>
        private static Texture2D LoadSpriteTexture(string spriteName)
        {
            try
            {
                if (string.IsNullOrEmpty(spriteName))
                {
                    return null;
                }
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "assets",
                    "hk", "sprites", spriteName + ".png");
                if (!System.IO.File.Exists(path))
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning("[KIC][锋利之影] 找不到贴图：" + path);
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


        /// <summary>
        /// 生成粒子：
        /// - `outward = false`：在诺艾尔周围 1~1.8 格的圆环上随机取点生成**黄色圆点**（同小骑士蓄力）；
        /// - `outward = true`：在中心附近生成**精华**，之后向四周扩散（2026-09-24 需求）。
        /// 两者尺寸一致（`ShadowChantSizeMin/Max`）。
        /// </summary>
        private static void SpawnNoelShadowParticles(int count, bool outward)
        {
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || count <= 0)
            {
                return;
            }
            float cx = pr.x;
            float cy = NoelBodyCenterY(pr) + (outward ? KnightInCradlePlugin.ShadowChantCenterOffsetY : 0f);
            for (int i = 0; i < count; i++)
            {
                if (_noelShadowParticles.Count >= ShadowChantParticleCap)
                {
                    _noelShadowParticles.RemoveAt(0);
                }
                float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                float rad = outward
                    ? UnityEngine.Random.Range(0f, 0.2f)
                    : UnityEngine.Random.Range(ShadowChantSpawnRadMin, ShadowChantSpawnRadMax);
                _noelShadowParticles.Add(new ShadowChantParticle
                {
                    X = cx + Mathf.Cos(ang) * rad,
                    Y = cy + Mathf.Sin(ang) * rad,
                    Age = 0f,
                    Life = outward ? ShadowChantParticleLife : ShadowChantParticleLife,
                    Size = UnityEngine.Random.Range(ShadowChantSizeMin, ShadowChantSizeMax),
                    Speed = UnityEngine.Random.Range(ShadowChantSpeedMin, ShadowChantSpeedMax) *
                            KnightInCradlePlugin.ShadowChantParticleSpeedScale,
                    Outward = outward,
                    // 精华向外扩散到 1~1.8 格就消散 —— 与黄色粒子的生成半径（也就是它的"范围"）一致
                    TargetDist = outward
                        ? UnityEngine.Random.Range(ShadowChantSpawnRadMin, ShadowChantSpawnRadMax)
                        : 0f,
                });
            }
        }

        /// <summary>
        /// 推进粒子：恒定速度向"诺艾尔身体中心 + 配置偏移"收敛，贴身/寿命到即消失。
        /// 偏移默认 -1（= 中心**上方** 1 格；2026-09-24 需求先"上移 1 格"再"再上移 0.5 格"，
        /// 相对最初的 +0.5 共上移 1.5 格）。y 向下为正，所以负值是上移。
        /// </summary>
        private static void UpdateNoelShadowParticles(PRNoel pr)
        {
            float dt = Time.deltaTime;
            float tx = pr.x;
            float ty = NoelBodyCenterY(pr) + KnightInCradlePlugin.ShadowChantCenterOffsetY;
            for (int i = _noelShadowParticles.Count - 1; i >= 0; i--)
            {
                ShadowChantParticle p = _noelShadowParticles[i];
                p.Age += dt;
                if (p.Outward)
                {
                    // 精华：从中心向四周**扩散**，扩散到 TargetDist（1~1.8 格，与黄色粒子同一范围）即消散
                    float ox = p.X - pr.x;
                    float oy = p.Y - ty;
                    float od = Mathf.Sqrt(ox * ox + oy * oy);
                    if (od < 0.001f)
                    {
                        float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                        ox = Mathf.Cos(ang);
                        oy = Mathf.Sin(ang);
                        od = 1f;
                    }
                    if (od >= p.TargetDist || p.Age >= p.Life)
                    {
                        _noelShadowParticles.RemoveAt(i);
                        continue;
                    }
                    float oinv = p.Speed / od;
                    p.X += ox * oinv * dt;
                    p.Y += oy * oinv * dt;
                    continue;
                }
                float dx = tx - p.X;
                float dy = ty - p.Y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < 0.08f || p.Age >= p.Life)
                {
                    _noelShadowParticles.RemoveAt(i);
                    continue;
                }
                float inv = p.Speed / dist;
                p.X += dx * inv * dt;
                p.Y += dy * inv * dt;
            }
        }

        /// <summary>诺艾尔身体中心（脚底 - 身高/2，同"会心"光圈的取法）。</summary>
        private static float NoelBodyCenterY(PRNoel pr)
        {
            return pr.mbottom - pr.sizey * 0.5f;
        }

        /// <summary>`PR.state` 是 protected 字段，这里复用 5100 行附近那个 FieldRefAccess 读取器。</summary>
        private static bool NoelPrStateIs(PR pr, PR.STATE state)
        {
            try
            {
                if (_prStateRef == null)
                {
                    _prStateRef = AccessTools.FieldRefAccess<PR, PR.STATE>("state");
                }
                return _prStateRef != null && _prStateRef(pr) == state;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>粒子票据：绑当前地图的 MovRenderer，画在诺艾尔身后层（PR0，同小骑士蓄力粒子）。</summary>
        private static void EnsureNoelShadowTicket(PRNoel pr, bool want)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (!want)
            {
                ReleaseNoelShadowTicket();
                return;
            }
            if (_noelShadowDotTex == null)
            {
                _noelShadowDotTex = MakeShadowChantDotTexture(32);
            }
            if (_noelShadowDotTex == null)
            {
                return;
            }
            if (_noelShadowMesh != null && _noelShadowMap == mp && _noelShadowTicket != null)
            {
                return;
            }
            ReleaseNoelShadowTicket();
            _noelShadowMap = mp;
            _noelShadowMesh = new MeshDrawer(null, 4 * ShadowChantParticleCap, 6 * ShadowChantParticleCap);
            _noelShadowMesh.draw_gl_only = true;
            _noelShadowMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelShadowMat.EnableKeyword("NO_PIXELSNAP");
            _noelShadowMesh.activate("noel_shadow_chant", _noelShadowMat, false, MTRX.ColWhite, null);
            _noelShadowTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelShadowMesh, _noelShadowMesh, null, null);
        }

        private static void ReleaseNoelShadowTicket()
        {
            try
            {
                if (_noelShadowTicket != null && _noelShadowMap != null &&
                    _noelShadowMap.MovRenderer != null)
                {
                    _noelShadowMap.MovRenderer.deassignDrawable(_noelShadowTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelShadowMat != null)
                {
                    IN.DestroyOne(_noelShadowMat);
                }
            }
            catch (Exception)
            {
            }
            _noelShadowTicket = null;
            _noelShadowMesh = null;
            _noelShadowMat = null;
            _noelShadowMap = null;
        }

        /// <summary>画粒子：锚在诺艾尔中心，逐颗换算成网格像素坐标（金色 `#FFCB00`）。</summary>
        private static bool PrepareNoelShadowMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelShadowMap;
            if (mp == null || _noelShadowMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelShadowMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || _noelShadowDotTex == null || _noelShadowParticles.Count == 0)
            {
                MdOut = _noelShadowMesh;
                return true;
            }
            float cx = pr.x;
            float cy = NoelBodyCenterY(pr);
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(cx * mp.CLEN), mp.pixel2uy(cy * mp.CLEN), 0f));
            _noelShadowMesh.initForImgAndTexture(_noelShadowDotTex);
            for (int i = 0; i < _noelShadowParticles.Count; i++)
            {
                ShadowChantParticle p = _noelShadowParticles[i];
                float alpha;
                if (p.Outward)
                {
                    // 向外扩散的那批：快速淡入、接近最远距离时淡出
                    float ox = p.X - cx;
                    float oy = p.Y - cy;
                    float od = Mathf.Sqrt(ox * ox + oy * oy);
                    alpha = Mathf.Clamp01(p.Age / 0.05f) *
                            Mathf.Clamp01((p.TargetDist - od) / 0.4f);
                }
                else
                {
                    alpha = Mathf.Clamp01((p.Life - p.Age) / 0.2f);
                }
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = p.Size * mp.CLEN;
                float dxm = (p.X - cx) * mp.CLEN;
                float dym = -(p.Y - cy) * mp.CLEN;
                Color32 col = KnightInCradlePlugin.ShadowChantParticleColor;
                _noelShadowMesh.Col = new Color(col.r / 255f, col.g / 255f, col.b / 255f, alpha);
                _noelShadowMesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = _noelShadowMesh;
            return true;
        }

        /// <summary>程序化生成白色圆形光点（同小骑士 `MakeDotDotTexture`，金色由绘制时的 Col 乘上去）。</summary>
        private static Texture2D MakeShadowChantDotTexture(int size)
        {
            try
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float r = size * 0.38f;
                Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                        float a = Mathf.Clamp01((r - d) / (r * 0.35f));
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * 0.9f));
                    }
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply();
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ================= 护符33 锋利之影（诺艾尔侧） =================
        /// <summary>
        /// 护符33 效果1：以下技能对诺艾尔**失效**——
        /// 闪避 `evade`、幻影闪避 `evade_jump_i_arrow` / `evade_jump_i_run`、
        /// 护盾冲击 `guard_bush`、环轨护盾 `guard_lariat`、
        /// 完美防御 `justguard`、轮舞斩击 `evade_dancing`、
        /// 以及**护盾 `guard` 本身**（2026-09-24 追加：实测戴护符后还能举盾 → 一并禁用）。
        /// 注：受身术在 AIC 里**不是** `SKILL_TYPE`（枚举里没有它，代码也从没按 key 查过），
        /// 它体现为 `PR.STATE.UKEMI` 这个状态，所以单独由下面 `NoelShadowUkemiBlockPrefix` 拦。
        /// </summary>
        private static bool IsNoelShadowDisabledSkill(SkillManager.SKILL_TYPE type)
        {
            switch (type)
            {
                case SkillManager.SKILL_TYPE.guard:
                case SkillManager.SKILL_TYPE.evade:
                case SkillManager.SKILL_TYPE.evade_jump_i_arrow:
                case SkillManager.SKILL_TYPE.evade_jump_i_run:
                case SkillManager.SKILL_TYPE.guard_bush:
                case SkillManager.SKILL_TYPE.guard_lariat:
                case SkillManager.SKILL_TYPE.justguard:
                case SkillManager.SKILL_TYPE.evade_dancing:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 护符33 效果1：**受身术（`PR.STATE.UKEMI`）失效**。
        ///
        /// 受身术在 AIC 里不是 `SkillManager.SKILL_TYPE`（枚举里根本没有它），而是"倒地中按攻击键
        /// 立刻起身"的那个状态：`M2PrSkill.runPunchCheck`（`M2PrSkill.cs:1954`）与
        /// `initEvade`（`:3080` / `M2PrSkillShieldEvade.cs:1148`）。这里挂在
        /// `PR.changeState(PR.STATE)` 上拦下"进入 UKEMI"，诺艾尔就会像没按一样继续躺着，
        /// 等倒地时间自然走完起身（原版无输入就是这条路径）。
        ///
        /// 只拦 `UKEMI`、不拦 `UKEMI_SHOTGUN`：后者是"被魔物吞下后带着霰弹脱身"的
        /// 吸收释放流程（`M2PrADmg.runAbsorbing`，`M2PrADmg.cs:808`），拦住会让那条流程
        /// 每帧提前 return 而卡死（实测风险，故不动）。
        /// </summary>
        private static bool NoelShadowUkemiBlockPrefix(PR __instance, PR.STATE _state)
        {
            try
            {
                if (_state != PR.STATE.UKEMI || IsKnightMode || !(__instance is PRNoel))
                {
                    return true;
                }
                return !IsEquipped(CharmOwner.Noel, ShadowId);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 护符33 效果1 的实现：挂在 `M2PrSkill.isEnable(SKILL_TYPE)` 上——它是 AIC 自己的
        /// "这个技能能不能用"总闸门（`M2PrSkill.cs:5089`，`enable_skill_bits` 位掩码）。
        /// 拦截点核实过：`guard_bush`/`guard_lariat` 在
        /// `M2PrSkillShieldEvade.getPunchVariation`（:200-201）用来决定"举盾时的一击"变不变招；
        /// `evade` 在 `skill_on_evade`（:1414）门控 `PR.STATE.EVADE` 的进入；
        /// `evade_jump_i_*` 在 :355/:377 门控闪避后的幻影派生。
        /// 都没动原版数据，只是让这一次查询返回 false（卸下护符立刻恢复）。
        /// </summary>
        private static bool NoelShadowSkillDisablePrefix(M2PrSkill __instance,
            SkillManager.SKILL_TYPE type, ref bool __result)
        {
            try
            {
                if (IsKnightMode)
                {
                    return true;
                }
                // 护符35 骨钉大师的荣耀：**按住攻击键**时禁用
                // "突进冲击 / 凌空横斩 / 旋风斩击 / 彗星俯冲"，让长按走蓄力而不是被这些变招吃掉。
                // 让长按走蓄力而不是被这些变招吃掉（需求 2026-09-25）。
                // 与 AIC 的判定一一对应：
                //   突进冲击 = 奔跑中按下攻击键（`Pr.run_continue_time >= 22f`）；
                //   凌空横斩 = 空中按下攻击键（`!hasFoot()`）；
                //   旋风斩击 = 空中按下攻击键 + 左右方向（`isLO/isRO(4) && canStand(...)`）；
                //   彗星俯冲 = 空中按下攻击键 + 下方向（`isBO(4) && canStand(...)`）。
                if (NailMasterEquipped &&
                    (type == SkillManager.SKILL_TYPE.dashpunch ||
                     type == SkillManager.SKILL_TYPE.airpunch ||
                     type == SkillManager.SKILL_TYPE.wheel ||
                     type == SkillManager.SKILL_TYPE.comet))
                {
                    PRNoel prNm = KnightInCradleBehaviour.GetPrPublic();
                    bool held = false;
                    bool airborne = false;
                    try
                    {
                        held = prNm != null && prNm.isAtkO(0);
                        airborne = prNm != null && !prNm.hasFoot();
                    }
                    catch (Exception)
                    {
                        held = false;
                    }
                    if (held && (type == SkillManager.SKILL_TYPE.dashpunch || airborne))
                    {
                        __result = false;
                        return false;
                    }
                }
                if (!IsEquipped(CharmOwner.Noel, ShadowId) || !IsNoelShadowDisabledSkill(type))
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

        /// <summary>「黑棉孢子」的物品键（蘑菇类魔族的孢子团块，见 `zh-cn_tx_item.txt`）。</summary>
        public const string MushroomSporeItemKey = "mtr_essence_mush";

        /// <summary>`NelNMush.AMistKind`（蘑菇会喷的全部孢子雾种类，protected static）。</summary>
        private static FieldInfo _mushMistKindField;

        private static MistManager.MistKind[] MushroomMistKinds()
        {
            try
            {
                if (_mushMistKindField == null)
                {
                    _mushMistKindField = AccessTools.Field(typeof(NelNMush), "AMistKind");
                }
                return _mushMistKindField?.GetValue(null) as MistManager.MistKind[];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 是否为"蘑菇释放出的雾气"：
        /// ① 攻击包属性是 `MGATTR.ACME`（蘑菇孢子的专属属性，`NelNMush.cs:1686/1712`，
        ///    蘑菇 boss 的 `NelNBoss_Nusi.MkBigRun` 也是它）；
        /// ② 或者雾种就是蘑菇会喷的那几种（`NelNMush.AMistKind`，含睡眠/混乱/麻痹/冰冻/孢子）。
        /// </summary>
        private static bool IsMushroomMist(MistManager.MistKind kind, MistAttackInfo atk)
        {
            try
            {
                if (atk != null && atk.attr == MGATTR.ACME)
                {
                    return true;
                }
                if (kind == null)
                {
                    return false;
                }
                if (ReferenceEquals(kind, NelNMush.MkAcme) || ReferenceEquals(kind, NelNMush.MkAcmeS))
                {
                    return true;
                }
                MistManager.MistKind[] arr = MushroomMistKinds();
                if (arr == null)
                {
                    return false;
                }
                for (int i = 0; i < arr.Length; i++)
                {
                    if (ReferenceEquals(arr[i], kind))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>诺艾尔 + 护符32：这次雾是不是可以直接免疫。</summary>
        private static bool NoelMushroomMistImmune(PR pr, MistManager.MistKind kind, MistAttackInfo atk)
        {
            return pr is PRNoel && !IsKnightMode &&
                   (IsEquipped(CharmOwner.Noel, MushroomId) || IsNoelFuryImmune) && IsMushroomMist(kind, atk);
        }

        /// <summary>护符32：诺艾尔免疫蘑菇雾气（`PR.applyGasDamage` 的两个重载各拦一次）。</summary>
        private static bool NoelMushroomMistLevelPrefix(PR __instance, MistManager.MistKind Mist)
        {
            return !NoelMushroomMistImmune(__instance, Mist, null);
        }

        /// <summary>护符32：同上（带 MistAttackInfo 的重载，属性 ACME 直接判孢子雾）。</summary>
        private static bool NoelMushroomMistAtkPrefix(PR __instance, MistManager.MistKind K,
            MistAttackInfo Atk)
        {
            return !NoelMushroomMistImmune(__instance, K, Atk);
        }

        /// <summary>
        /// 护符32 效果2：诺艾尔攻击（任何类型）蘑菇一族时，获得 1 个**满级**「黑棉孢子」。
        ///
        /// 挂 `NelNMush.applyDamage(NelAttackInfo, ref HITTYPE, bool)`：它是蘑菇的虚方法
        /// **override**（`NelNMush.cs:1469`），而 `NelEnemy.applyDamage(Atk, force)` 那个二参重载
        /// 本身只是转发到它，所以无论近战、法术还是别的伤害来源，**只要真的打到了蘑菇**都会经过这里。
        /// 用后缀而不是前缀：`__result > 0` 才算真打中（蘑菇防御/无敌帧挡下时不给道具）。
        /// </summary>
        private static void MushroomApplyDamagePostfix(NelNMush __instance, NelAttackInfo Atk,
            ref int __result)
        {
            try
            {
                if (__result <= 0 || Atk == null || IsKnightMode ||
                    !IsEquipped(CharmOwner.Noel, MushroomId))
                {
                    return;
                }
                if (!(Atk.Caster is PRNoel) && !(Atk.AttackFrom is PRNoel))
                {
                    return; // 只算诺艾尔自己的攻击
                }
                GrantMushroomSporeItem();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>发放 1 个满级「黑棉孢子」（grade = GRADE_MAX - 1，与小骑士侧"满级"口径一致）。</summary>
        private static void GrantMushroomSporeItem()
        {
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                if (nM2D == null || nM2D.IMNG == null)
                {
                    return;
                }
                NelItem itm = NelItem.GetById(MushroomSporeItemKey, true);
                if (itm == null)
                {
                    return;
                }
                nM2D.IMNG.getItem(itm, 1, NelItem.GRADE_MAX - 1, true);
            }
            catch (Exception)
            {
            }
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
                // 与蜂巢中立同一条教训：雷雨转化的候选必须放它苏醒，
                // 否则它永远不会变成汚染体（也就不会被玩家"打醒"）。
                if (WillThunderOverdrive(__instance.En))
                {
                    return true;
                }
                return false;
            }
            // 护符34 乌恩之形：蹲下/爬行期间战斗区域内的魔物保持友好（不苏醒）
            if (UnnFriendlyActive())
            {
                if (WillThunderOverdrive(__instance.En))
                {
                    return true;
                }
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
            // 护符25 发光子宫：剑山及其污染体不把诺艾尔（玩家本体）作为目标
            if (value is PR && NoelUterusActive() && IsUniEnemy(__instance.En))
            {
                return false;
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

        /// <summary>护符34 乌恩之形：蹲下/爬行期间不允许魔物锁定诺艾尔（清空目标仍放行）。</summary>
        private static bool NaiAimPrSetUnnPrefix(NAI __instance, M2Attackable value)
        {
            try
            {
                if (value != null && UnnFriendlyActive())
                {
                    return false;
                }
            }
            catch (Exception)
            {
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

        // ==================== 护符33 冲刺段：伤害结算 ====================
        /// <summary>
        /// 记录"最近一次挥击"的攻击包（轻攻击 / 魔法霰弹及其变种），供冲刺段取"当前伤害"。
        /// 与蜕变挽歌当初的做法一致：抄一份**独立的** `NelAttackInfo`（不随原攻击包回收失效），
        /// 并记下那一刀的"伤害发布率"（`PR.getHpDamagePublishRatio`）。
        /// </summary>
        private static void CaptureNoelDashAttack(MagicItem mg)
        {
            try
            {
                if (mg == null || mg.Atk0 == null)
                {
                    return;
                }
                bool shotgun = IsNoelShotgunFlavored(mg);
                bool punch = mg.kind == MGKIND.PR_PUNCH;
                if (!shotgun && !punch)
                {
                    return; // 只关心轻攻击与魔法霰弹（含霰弹变种）
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null)
                {
                    return;
                }
                var copy = new NelAttackInfo(mg.Atk0);
                float ratio = 1f;
                try
                {
                    ratio = pr.getHpDamagePublishRatio(mg);
                }
                catch (Exception)
                {
                    ratio = 1f;
                }
                if (shotgun)
                {
                    _dashShotgunAtk = copy;
                    _dashShotgunRatio = ratio;
                }
                else
                {
                    _dashPunchAtk = copy;
                    _dashPunchRatio = ratio;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 冲刺路径伤害（需求 2026-09-25）：沿路对**所有**目标结算一次，
        /// 数值 = 当前伤害 × `DashDamageMult`（默认 3 倍）：
        /// - 法杖带**魔法霰弹附魔**（冲刺开始那一刻的 `pr.isShotgunState()`）→ 用"当前魔法霰弹"的伤害包，
        ///   并在首个命中时触发原版霰弹的击中特效（同时按原版规则把蓄力耗掉）；
        /// - 否则 → 用"当前轻攻击"的伤害包。
        /// 伤害包优先取"最近一次同类型挥击"的缓存；霰弹没有缓存时按原版公式现算
        /// （`int(shotgun_ratio × ZPOW(mp_hold, reduce_mp) × CurMg.Atk0.hpdmg0)`）；
        /// 都拿不到时用配置的兜底基础伤害。
        /// 乘区与其它诺艾尔侧效果统一走 `NoelFinalDamageMult`（萨满/坚固力量/会心）。
        /// </summary>
        private static void CheckNoelDashHits(PRNoel pr)
        {
            try
            {
                Map2d mp = pr != null ? pr.Mp : null;
                if (mp == null)
                {
                    return;
                }
                int mask = NoelEnemyOverlapMask();
                if (mask == 0)
                {
                    return;
                }
                float cy = NoelBodyCenterY(pr);
                float ux = mp.pixel2ux(pr.x * mp.CLEN);
                float uy = mp.pixel2uy(cy * mp.CLEN);
                Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(ux, uy));
                Collider2D[] hits = Physics2D.OverlapBoxAll(center,
                    new Vector2(KnightInCradlePlugin.ShadowDashHitboxW,
                        KnightInCradlePlugin.ShadowDashHitboxH), 0f, mask);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    if (enemy == null || !_dashHitEnemies.Add(enemy))
                    {
                        continue; // 每只魔物只挨一次
                    }
                    ApplyNoelDashDamage(pr, enemy);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ApplyNoelDashDamage(PRNoel pr, NelEnemy enemy)
        {
            try
            {
                if (IsEnemySummoning(enemy))
                {
                    return; // 生成中的魔物不能打（否则它渲染会永久消失）
                }
                // 附魔中就是霰弹结算（缓存只是"数值来源"之一，不能因为没有缓存就退回轻攻击）
                bool shotgun = _dashUseShotgun;
                NelAttackInfo src = shotgun ? _dashShotgunAtk : _dashPunchAtk;
                float ratio = shotgun ? _dashShotgunRatio : _dashPunchRatio;
                MGKIND kind = shotgun ? MGKIND.PR_SHOTGUN : MGKIND.PR_PUNCH;
                int baseDmg;
                if (src != null && src.hpdmg0 > 0)
                {
                    baseDmg = src.hpdmg0;
                }
                else if (shotgun)
                {
                    int computed = ComputeNoelShotgunDamageNow(pr);
                    baseDmg = computed > 0 ? computed : KnightInCradlePlugin.ShadowDashFallbackDamage;
                }
                else
                {
                    baseDmg = KnightInCradlePlugin.ShadowDashFallbackDamage;
                }
                float mult = KnightInCradlePlugin.ShadowDashDamageMult * NoelFinalDamageMult(kind, shotgun);
                int dmg = Mathf.Max(1, Mathf.FloorToInt(baseDmg * mult + 0.5f));
                NelAttackInfo atk;
                if (src != null)
                {
                    atk = new NelAttackInfo(src);
                }
                else
                {
                    // 没有任何可抄的攻击包 → 用最小攻击包 + 真伤，保证伤害真的落地（同挽歌的兜底做法）
                    atk = new NelAttackInfo();
                    atk.fix_damage = true;
                    ratio = 1f;
                }
                atk.Caster = pr;
                atk.hpdmg0 = dmg;
                atk.hpdmg_current = -1000; // 置回未结算态 → 按下面的发布率重新算
                atk._apply_knockback_current = true;
                atk.shuffleHpMpDmg(enemy, ratio, 1f, dmg, atk.mpdmg0);
                atk.CenterXy(enemy.x, enemy.y, 0f);
                ResolveHeavyFocusHit(); // 算是"这一发攻击命中了"（沉重之击的连击）
                enemy.applyDamage(atk, false);
                try
                {
                    DashAudio.PlayEnemyHit();
                }
                catch (Exception)
                {
                }
                if (_dashUseShotgun)
                {
                    TriggerNoelDashShotgunEffect(pr, enemy);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>按原版公式现算"当前魔法霰弹"的基础伤害（没有可用的挥击缓存时用）。</summary>
        private static int ComputeNoelShotgunDamageNow(PRNoel pr)
        {
            try
            {
                M2PrSkill skill = pr != null ? pr.Skill : null;
                MagicItem hold = skill != null ? skill.getCurMagic() : null;
                if (hold == null || hold.Atk0 == null || hold.casttime <= 0f || hold.reduce_mp <= 0f)
                {
                    return -1;
                }
                float ratio = 1.5f;
                MKind mk = MKind.Get(hold.kind);
                if (mk != null)
                {
                    ratio = mk.shotgun_ratio;
                }
                float charge01 = X.ZPOW(skill.getHoldingMp(true), hold.reduce_mp);
                int dmg = X.IntC(ratio * charge01 * hold.Atk0.hpdmg0);
                if (_dashPunchAtk != null && _dashPunchAtk.hpdmg0 > dmg)
                {
                    dmg = _dashPunchAtk.hpdmg0; // 原版是 max(算出来的, 轻攻击基础伤害)
                }
                return dmg;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>冲刺霰弹命中特效：与原版霰弹同一套（满蓄力时带瞬间减速的后仰）；只触发一次。</summary>
        private static void TriggerNoelDashShotgunEffect(PRNoel pr, NelEnemy enemy)
        {
            try
            {
                if (_dashShotgunFxDone || pr == null || enemy == null)
                {
                    return;
                }
                M2PrSkill skill = pr.Skill;
                MagicItem curMg = skill != null ? skill.getCurMagic() : null;
                Map2d mp = pr.Mp;
                if (skill == null || curMg == null || !curMg.isPreparingCircle || mp == null)
                {
                    return; // 冲刺那一刻的蓄力已经不在了 → 只结算伤害
                }
                int holdingMp = skill.getHoldingMp(true);
                if (holdingMp < 1)
                {
                    return;
                }
                _dashShotgunFxDone = true;
                float charge01 = Mathf.Clamp01(holdingMp / Mathf.Max(1f, curMg.reduce_mp));
                var hitItem = new M2Ray.M2RayHittedItem();
                hitItem.type = HITTYPE.EN;
                hitItem.Hit = enemy;
                hitItem.Mv = enemy;
                hitItem.hit_ux = mp.map2globalux(enemy.x);
                hitItem.hit_uy = mp.map2globaluy(enemy.y);
                MDAT.setFullChargeShotgunEffect(pr, charge01, hitItem, false, true, 0.47123894f);
                // 与原版"霰弹把蓄力耗尽"同一个收尾（不复位、不返还魔力）
                skill.killHoldMagic(false, false, false);
            }
            catch (Exception)
            {
            }
        }
        // ==================== 护符20 亡者之怒（诺艾尔侧） ====================
        /// <summary>亡者之怒是否处于"已触发"状态（HP ≤ 阈值；供后续伤害加成用）。</summary>
        public static bool NoelFuryActive => _noelFuryActive;
        private static bool _noelFuryActive;
        /// <summary>本次自动"圣光爆发"的免魔/免眩晕剩余时间（秒）。</summary>
        private static float _noelFuryBurstFree;
        /// <summary>亡者之怒的 HP 流失计时。</summary>
        private static float _noelFuryDrainTimer;
        /// <summary>true = 这次 HP 归零是"亡者之怒的 HP 流失"造成的，受击被动一律跳过。</summary>
        private static bool _noelFuryDying;
        /// <summary>本次"进入亡者之怒"是否已经放过圣光爆发（保证每次进入只放一次）。</summary>
        private static bool _noelFuryBurstFired;
        /// <summary>
        /// true = 亡者之怒已**锁死**（诺艾尔已经死亡）。
        /// 需求（2026-09-26 追加）：死亡之后不再走阈值判定 —— 否则魔物补刀时
        /// `TryTriggerNoelFury` 会照旧把 HP 抬回 30，等于"死后原地复活"。
        /// 复活 / 回血到阈值之上（或卸下护符、切小骑士）时解锁。
        /// </summary>
        private static bool _noelFuryLocked;

        /// <summary>
        /// 护符20 效果3（2026-09-26）：亡者之怒期间，诺艾尔**免疫魔物的伤害/抓取/负面效果**，
        /// 以及**地图上所有危险格**（尖刺、荆棘、虫墙等）。实现由四处组成：
        /// ① 每帧 `addNoDamage(NDMG._ALL)`（滚动无敌帧，挡掉游戏自己那层的判定）；
        /// ② `M2PrADmg.applyDamage` 前缀：只对"来源是魔物"的伤害整次作废（不影响我们自己的致死调用）；
        /// ③ `PR.applyDamageFromMap` 前缀（与护符21 共用）：地图危险格直接跳过；
        /// ④ `M2Ser.Add` 前缀：负面状态一律拒绝；`PR.initAbsorb` / `canPullByWorm` 拦吞下与虫墙拉扯。
        /// </summary>
        private static bool IsNoelFuryImmune => _noelFuryActive;

        /// <summary>护符过载：诺艾尔当前过载了几个槽孔（已装护符总费用 - 槽孔上限）。</summary>
        /// <summary>长按魔法键触发圣光爆发的计时/已触发标记（护符35）。</summary>
        private static float _nmBurstHoldTimer;
        private static bool _nmBurstHoldFired;
        private static int _nmBurstKeyDiag;

        private static object _keyItObj;
        private static Array _keyInputs;
        private static object _magicAct;
        private static MethodInfo _magicActIsPressed;

        /// <summary>
        /// 读**游戏当前键位**下的"魔法键是否按住"（护符35 用）。
        /// AIC 用新 Input System，绑定可被玩家改；这里反射走
        /// `KEY.IT.AInputs[(int)KEY.IPT.X].Act.IsPressed()`，任何一步失败都返回 false（调用方会退回配置键名）。
        /// </summary>
        private static bool NoelMagicKeyHeldByGame()
        {
            try
            {
                // 解析一次成功后缓存；失败则下一帧继续重试（AIC 的输入表可能晚于本补丁初始化）。
                if (_magicActIsPressed == null)
                {
                    // 真实名字（用反射 dump 游戏程序集确认过，反编译源码里的标识符被混淆过，不可信）：
                    //   XX.IN.KA            → KEY 单例
                    //   KEY.AInputs         → InputHolder[]
                    //   InputHolder.Act     → UnityEngine.InputSystem.InputAction
                    if (_keyItObj == null)
                    {
                        FieldInfo kaF = AccessTools.Field(typeof(IN), "KA");
                        _keyItObj = kaF != null ? kaF.GetValue(null) : null;
                    }
                    if (_keyItObj != null && _keyInputs == null)
                    {
                        FieldInfo fi = AccessTools.Field(typeof(KEY), "AInputs");
                        _keyInputs = fi != null ? fi.GetValue(_keyItObj) as Array : null;
                    }
                    if (_keyInputs != null && _magicAct == null)
                    {
                        int idx = (int)KEY.IPT.X;
                        if (idx >= 0 && idx < _keyInputs.Length)
                        {
                            object holder = _keyInputs.GetValue(idx);
                            FieldInfo actF = holder != null ? AccessTools.Field(holder.GetType(), "Act") : null;
                            _magicAct = actF != null ? actF.GetValue(holder) : null;
                        }
                    }
                    if (_magicAct != null)
                    {
                        _magicActIsPressed = _magicAct.GetType().GetMethod("IsPressed", Type.EmptyTypes);
                    }
                }
                return _magicActIsPressed != null && (bool)_magicActIsPressed.Invoke(_magicAct, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护符35 补充（需求 2026-09-27）：戴荣耀时魔法键被锁住，这里改成
        /// **长按魔法键**（`[Charm35] BurstMagicKey`，默认 X）达到 `BurstHoldSeconds`（默认 0.3 秒）
        /// → 直接切进 `PR.STATE.BURST`（圣光爆发）。一次按住只触发一次，松手后重新计数。
        /// 魔力消耗/后续流程全部走原版爆发（不享受亡者之怒那个免魔窗口）。
        /// 附带诊断日志：35 在场时按下 Z/X/C/Space 会打印一次键名，方便确认实际魔法键是哪个。
        /// </summary>
        public static void TickNoelBurstCombo(PRNoel pr)
        {
            try
            {
                if (pr == null || !pr.is_alive || IsKnightMode || !NailMasterEquipped ||
                    !KnightInCradlePlugin.NailMasterBurstComboEnabled)
                {
                    _nmBurstHoldTimer = 0f;
                    _nmBurstHoldFired = false;
                    return;
                }
                // 诊断：戴荣耀时把按下的候选键名打出来（最多 20 条），便于确认魔法键到底是哪个
                if (_nmBurstKeyDiag < 20)
                {
                    KeyCode[] cand = { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.Space, KeyCode.LeftShift };
                    for (int i = 0; i < cand.Length; i++)
                    {
                        if (UnityEngine.Input.GetKeyDown(cand[i]))
                        {
                            _nmBurstKeyDiag++;
                            KnightInCradlePlugin.PluginLog?.LogInfo(
                                "[KIC][护符35] 按下按键：" + cand[i]);
                        }
                    }
                }
                // 优先读**游戏自己的按键绑定**（AIC 用新 Input System，玩家可以改键；
                // 反射链：KEY.IT → AInputs[(int)KEY.IPT.X] → Act(InputAction).IsPressed()），
                // 读不到时退回 mod 配置的键名。
                bool held = NoelMagicKeyHeldByGame() ||
                            KeyConfig.GetHeld(KnightInCradlePlugin.NailMasterBurstComboMagicKey, KeyCode.X);
                if (!held)
                {
                    _nmBurstHoldTimer = 0f;
                    _nmBurstHoldFired = false;
                    return;
                }
                _nmBurstHoldTimer += Time.deltaTime;
                if (_nmBurstHoldFired || _nmBurstHoldTimer < KnightInCradlePlugin.NailMasterBurstHoldSeconds)
                {
                    return;
                }
                _nmBurstHoldFired = true;
                if (NoelPrStateIs(pr, PR.STATE.BURST))
                {
                    return; // 已经在爆发中
                }
                pr.changeState(PR.STATE.BURST);
            }
            catch (Exception)
            {
            }
        }

        public static int NoelOverchargeCount
        {
            get
            {
                try
                {
                    IReadOnlyList<int> eq = CharmSave.EquippedSnapshotFor(CharmOwner.Noel);
                    if (eq == null)
                    {
                        return 0;
                    }
                    int total = 0;
                    for (int i = 0; i < eq.Count; i++)
                    {
                        CharmData cd = CharmDatabase.Get(eq[i]);
                        if (cd != null && cd.Cost > 0)
                        {
                            total += cd.Cost;
                        }
                    }
                    return Mathf.Max(0, total - CharmDatabase.NotchCapacity);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>把这次的受伤量按"过载格数 × 每格加成"放大（向上取整，至少 +1）。</summary>
        private static int ApplyNoelOverchargeDamage(int val)
        {
            try
            {
                int n = NoelOverchargeCount;
                if (n <= 0 || val <= 0)
                {
                    return val;
                }
                float mult = 1f + KnightInCradlePlugin.OverchargeDamagePerSlot * n;
                return Mathf.Max(val + 1, Mathf.CeilToInt(val * mult));
            }
            catch (Exception)
            {
                return val;
            }
        }

        /// <summary>该伤害包是不是"魔物的攻击"（不是地图伤害/自伤/模组调用）。</summary>
        private static bool IsEnemySourceAttack(AttackInfo Atk)
        {
            NelAttackInfo nAtk = Atk as NelAttackInfo;
            return nAtk != null && (nAtk.Caster is NelEnemy || nAtk.AttackFrom is NelEnemy);
        }

        /// <summary>这些状态属于"负面效果"（亡者之怒期间一律拒绝）。</summary>
        private static bool IsNoelNegativeSer(SER ser)
        {
            switch (ser)
            {
                case SER.HP_REDUCE:
                case SER.MP_REDUCE:
                case SER.SEXERCISE:
                case SER.CONFUSE:
                case SER.POISON:
                case SER.PARALYSIS:
                case SER.BURNED:
                case SER.PARASITISED:
                case SER.SHAMED:
                case SER.SHAMED_SPLIT:
                case SER.SHAMED_WET:
                case SER.SHAMED_EP:
                case SER.EGGED:
                case SER.LAYING_EGG:
                case SER.WORM_TRAPPED:
                case SER.SLEEP:
                case SER.TIRED:
                case SER.BURST_TIRED:
                case SER.EATEN:
                case SER.STRONG_HOLD:
                case SER.FRUSTRATED:
                case SER.ORGASM_INITIALIZE:
                case SER.ORGASM_AFTER:
                case SER.ORGASM_STACK:
                case SER.FORBIDDEN_ORGASM:
                case SER.JAMMING:
                case SER.FROZEN:
                case SER.NEAR_PEE:
                case SER.DRUNK:
                case SER.CLT_BROKEN:
                case SER.OVERRUN_TIRED:
                case SER.WEB_TRAPPED:
                case SER.STONE:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 护符20 效果1（2026-09-26）：**魔物攻击**若会把诺艾尔的 HP 打到低于
        /// `Charm20/HpThreshold`（默认 30），则这一次伤害不结算、HP 立即回到该阈值，并触发亡者之怒。
        /// 只认魔物攻击（`Atk.Caster` / `Atk.AttackFrom` 是 `NelEnemy`）；地图伤害/自伤不触发。
        /// 佩戴乔尼的祝福时 HP 不参与结算，本条不生效（与护符30 的机制冲突，用户要求暂不处理）。
        /// </summary>
        private static bool TryTriggerNoelFury(PRNoel noel, AttackInfo Atk, ref int val)
        {
            try
            {
                if (IsKnightMode || noel == null || !IsEquipped(CharmOwner.Noel, FuryId))
                {
                    return false;
                }
                if (JoniBlessingActive(noel))
                {
                    return false; // 乔尼：HP 不是池子
                }
                // `applyHpDamage` 的形参类型是基类 `AttackInfo`，出手者字段在 `NelAttackInfo` 上
                NelAttackInfo nAtk = Atk as NelAttackInfo;
                if (!(nAtk != null && (nAtk.Caster is NelEnemy || nAtk.AttackFrom is NelEnemy)))
                {
                    return false; // 只认"魔物攻击"
                }
                if (PrHpField == null || PrMaxHpField == null)
                {
                    return false;
                }
                int threshold = KnightInCradlePlugin.FuryHpThreshold;
                int hp = (int)PrHpField.GetValue(noel);
                // 需求（2026-09-26 追加）：死亡之后锁住亡者之怒 —— HP 已经是 0（或刚判定死亡）时
                // 直接返回，绝不再把 HP 写回阈值。
                if (_noelFuryLocked || hp <= 0 || !noel.is_alive)
                {
                    if (hp <= 0 || !noel.is_alive)
                    {
                        _noelFuryLocked = true;
                        _noelFuryActive = false;
                    }
                    return false;
                }
                if (hp - val >= threshold)
                {
                    return false; // 这一下打不到阈值以下
                }
                val = 0; // 伤害不结算
                // 需求：触发时 HP **直接设为阈值（默认 30）**（不是"回到不低于 30"）
                PrHpField.SetValue(noel, threshold);
                RefreshNoelHudHp();
                // 需求（2026-09-26 澄清）：**只有"HP 从别的值降到 30"才算触发事件**
                // （圣光爆发 + 全清负面）；已经正好是 30HP 时再挨打只保持 30，不重复爆发。
                if (hp != threshold)
                {
                    TriggerNoelFury(noel);
                }
                else
                {
                    _noelFuryActive = true;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护符20 效果2：触发瞬间 ① 清除身上所有负面状态（`Ser.CureAll()`）；② 自动释放一次
        /// **不消耗魔力、不导致眩晕**的"圣光爆发"（`PR.STATE.BURST`；免魔/免眩晕由
        /// `FuryBurstMpDamagePrefix` 与 `M2Ser.Add` 前缀在该窗口内放行）。
        /// 已经在爆发状态中时不重复触发（避免连续受击刷屏）。
        /// </summary>
        private static void TriggerNoelFury(PRNoel pr)
        {
            _noelFuryActive = true;
            _noelFuryBurstFired = true;
            try
            {
                pr.Ser?.CureAll(); // 清除所有状态（AIC 自己的"全解"）
            }
            catch (Exception)
            {
            }
            try
            {
                if (!NoelPrStateIs(pr, PR.STATE.BURST))
                {
                    _noelFuryBurstFree = KnightInCradlePlugin.FuryBurstSeconds;
                    pr.changeState(PR.STATE.BURST);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 收尾"退出亡者之怒"的状态：清掉激活标记、爆发已放标记与流失计时。
        /// 注意**不**上死亡锁 —— 需求（2026-09-26）效果4 明确：退出之后 HP 再掉到 30 还要能重新触发。
        /// 长椅退出（效果5）与"被回血抬到阈值之上"（效果4）都走这里 / 同一套清理。
        /// </summary>
        private static void ReleaseNoelFuryForVanish()
        {
            _noelFuryActive = false;
            _noelFuryBurstFired = false;
            _noelFuryDrainTimer = 0f;
            _noelFuryBurstFree = 0f;
            StopNoelFuryBgm();
        }

        /// <summary>每帧推进（挂进 `TickNoelCharmEffects`）：维护亡者之怒状态与自动爆发的免魔窗口。</summary>
        public static void TickNoelFuryCharm(PRNoel pr)
        {
            try
            {
                if (_noelFuryBurstFree > 0f)
                {
                    _noelFuryBurstFree = Mathf.Max(0f, _noelFuryBurstFree - Time.deltaTime);
                }
                if (pr == null || IsKnightMode || !IsEquipped(CharmOwner.Noel, FuryId))
                {
                    _noelFuryActive = false;
                    _noelFuryBurstFired = false;
                    _noelFuryLocked = false;
                    _noelFuryBurstFree = 0f;
                    _noelFuryDrainTimer = 0f;
                    StopNoelFuryBgm();
                    return;
                }
                int hp = PrHpField != null ? (int)PrHpField.GetValue(pr) : 0;
                // 需求（2026-09-26 追加）：**死亡之后锁住亡者之怒**。
                // 死（HP 归零 / 非存活）即上锁；复活或回血到阈值之上（坐在长椅上也算）才解锁。
                if (hp <= 0 || !pr.is_alive)
                {
                    _noelFuryLocked = true;
                }
                else if (_noelFuryLocked &&
                         (hp > KnightInCradlePlugin.FuryHpThreshold || IsNoelOnBench(pr)))
                {
                    _noelFuryLocked = false;
                }
                // 需求（2026-09-26 追加）效果5：亡者之怒期间**坐在长椅上** → 退出亡者之怒并回满 HP。
                // （AIC 的长椅本来就会把 HP/MP 补满，这里主动补一次是为了不依赖"长椅菜单被打开"那一刻；
                // 补满后 hp > 阈值，亡者之怒自然结束。）
                if (!_noelFuryLocked && _noelFuryActive && hp > 0 && IsNoelOnBench(pr))
                {
                    try
                    {
                        int maxhp = PrMaxHpField != null ? (int)PrMaxHpField.GetValue(pr) : hp;
                        if (hp < maxhp)
                        {
                            pr.cureHp(maxhp - hp); // 走原版回血：HUD、GaugeSaver 一起同步
                        }
                        RefreshNoelHudHp();
                    }
                    catch (Exception)
                    {
                    }
                    ReleaseNoelFuryForVanish();
                    return;
                }
                bool wasActive = _noelFuryActive;
                // 亡者之怒的"在状态中"判据：HP ≤ 阈值（回到阈值之上就结束）
                _noelFuryActive = !_noelFuryLocked && hp <= KnightInCradlePlugin.FuryHpThreshold;
                if (!_noelFuryActive)
                {
                    // 需求（2026-09-26 追加）效果4：亡者之怒期间用道具/其它手段把 HP 回到阈值之上就**退出**，
                    // 之后再掉回阈值（哪怕正好 30）还能重新触发 —— 所以这里只清"本次激活"的痕迹，不上死亡锁。
                    StopNoelFuryBgm();
                    if (wasActive)
                    {
                        ReleaseNoelFuryForVanish();
                    }
                    else
                    {
                        _noelFuryBurstFired = false;
                        _noelFuryDrainTimer = 0f;
                    }
                    return;
                }
                // 需求（2026-09-26 追加）：**进入**亡者之怒的那一瞬间就要释放一次圣光爆发。
                // 进入有两条路：① 魔物把 HP 打到阈值以下 —— `TryTriggerNoelFury` 里当场放；
                // ② HP 因别的原因落到阈值（最典型就是本护符自己的 HP 流失）—— 在这里补上。
                // `_noelFuryBurstFired` 让"每次进入"只放一发，两条路都走到也不会连放。
                if (!wasActive && !_noelFuryBurstFired)
                {
                    TriggerNoelFury(pr);
                }
                if (hp <= 0 || !pr.is_alive)
                {
                    StopNoelFuryBgm();
                    return; // 已经死亡：不再续无敌帧、不再流失
                }
                // 效果6（2026-09-26 追加）：亡者之怒期间播放"森之领主虚弱"那段 BGM，
                // 战斗结束 / 脱离战斗 / 亡者之怒结束时淡回原来的 BGM。
                TickNoelFuryBgm(true, Time.deltaTime);
                // 需求（2026-09-26 追加）：亡者之怒期间删去 HUD 上的 HP 缓冲条
                HideNoelHpCushion(pr);
                // 效果3：亡者之怒期间持续续无敌帧（滚动续期 0.2 秒）
                try
                {
                    pr.addNoDamage(NDMG._ALL, 0.2f);
                }
                catch (Exception)
                {
                }
                // 效果4：HP 随时间流失（每 DrainSeconds 秒 -DrainAmount），归零走原版死亡
                float interval = Mathf.Max(0.1f, KnightInCradlePlugin.FuryDrainSeconds);
                _noelFuryDrainTimer += Time.deltaTime;
                if (_noelFuryDrainTimer < interval)
                {
                    return;
                }
                _noelFuryDrainTimer -= interval;
                if (_noelFuryDrainTimer > interval)
                {
                    _noelFuryDrainTimer = 0f; // 长时间没推进（过图/暂停）时不补算
                }
                int left = hp - Mathf.Max(1, KnightInCradlePlugin.FuryDrainAmount);
                if (left > 0)
                {
                    PrHpField.SetValue(pr, left);
                    RefreshNoelHudHp();
                    return;
                }
                _noelFuryDrainTimer = 0f;
                _noelFuryActive = false; // 先落状态，避免被自己这一发"魔物来源"判定卷住
                _noelFuryLocked = true;  // 死亡即锁死亡者之怒：之后魔物补刀也不会再把 HP 抬回阈值
                try
                {
                    _noelFuryDying = true;
                    pr.applyHpDamage(9999, true, null); // 原版强制死亡（读 hp、走 GAMEOVER）
                }
                finally
                {
                    _noelFuryDying = false;
                }
                RefreshNoelHudHp();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>自动圣光爆发期间：跳过魔力消耗（`PR.applyBurstMpDamage`）。</summary>
        private static bool FuryBurstMpDamagePrefix()
        {
            return !(_noelFuryBurstFree > 0f && !IsKnightMode);
        }

        /// <summary>自动圣光爆发期间：拒绝"爆发眩晕"类状态（免眩晕）。</summary>
        private static bool IsFuryBurstTiredSer(SER ser)
        {
            return ser == SER.BURST_TIRED || ser == SER.TIRED || ser == SER.OVERRUN_TIRED;
        }

        // ==================== 护符20 效果7：亡者之怒的红色视觉（屏幕红边 + 中心红闪） ====================
        /// <summary>true = 正在结算"亡者之怒附加的真伤"，避免自己触发的追加伤害再次追加。</summary>
        private static bool _noelFuryTrueDmgApplying;

        /// <summary>
        /// 效果8（2026-09-26）：诺艾尔**造成伤害时**额外附加 `FuryTrueDamage`（默认 10）点真实伤害。
        ///
        /// 挂点：`M2Attackable.applyHpDamage(int, bool, AttackInfo)` 的**后缀** —— 所有魔物受伤
        /// 最后都会走到这里（`NelEnemy.applyHpDamage(4 参)` 只是转发到它，`NelEnemy.cs:2249-2252`），
        /// 而逐个挂敌人的 3 参 `applyDamage` 是没用的（26 个子类各自 override，虚分派不会走基类）。
        /// 判据：`__result > 0`（这一下真的掉了血）+ 攻击包的 `Caster`/`AttackFrom` 是本地诺艾尔
        /// （与蘑菇孢子那份同一个口径）+ 诺艾尔模式 + 亡者之怒激活。
        /// 追加伤害用 `force = true`、`Atk = null` 打出去：不吃敌人减伤/浮动（真伤），
        /// 也不会因为 `Caster` 为空而再触发一次本方法（另配 `_noelFuryTrueDmgApplying` 兜底防递归）。
        /// </summary>
        private static void FuryTrueDamagePostfix(M2Attackable __instance, AttackInfo Atk, int __result)
        {
            try
            {
                if (_noelFuryTrueDmgApplying || __result <= 0 || Atk == null)
                {
                    return;
                }
                if (!_noelFuryActive || IsKnightMode || !(__instance is NelEnemy enemy))
                {
                    return;
                }
                // 基类 `AttackInfo` 没有 Caster/AttackFrom，出手者在 `NelAttackInfo` 上
                NelAttackInfo nAtk = Atk as NelAttackInfo;
                if (nAtk == null || (!(nAtk.Caster is PRNoel) && !(nAtk.AttackFrom is PRNoel)))
                {
                    return; // 不是诺艾尔打出来的伤害（例如同行的小骑士/魔物互殴）
                }
                int extra = KnightInCradlePlugin.FuryTrueDamage;
                if (extra <= 0)
                {
                    return;
                }
                _noelFuryTrueDmgApplying = true;
                try
                {
                    enemy.applyHpDamage(extra, true, null);
                }
                finally
                {
                    _noelFuryTrueDmgApplying = false;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>`NelItemManager.StPrecious`（"重要物品"存储区）——字段非 public，取一次缓存住。</summary>
        private static FieldInfo _imngPreciousField;

        /// <summary>当前存档的"重要物品"存储区（拿不到就返回 null）。</summary>
        public static ItemStorage GetPreciousStorage()
        {
            try
            {
                NelM2DBase nM2D = M2DBase.Instance as NelM2DBase;
                NelItemManager imng = nM2D != null ? nM2D.IMNG : null;
                if (imng == null)
                {
                    return null;
                }
                if (_imngPreciousField == null)
                {
                    _imngPreciousField = AccessTools.Field(typeof(NelItemManager), "StPrecious");
                }
                return _imngPreciousField?.GetValue(imng) as ItemStorage;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>供 `KnightInCradleBehaviour.OnGUI` 画"屏幕四周红色滤镜"用。</summary>
        public static bool NoelFuryVignetteVisible
        {
            get
            {
                if (!_noelFuryActive || IsKnightMode || !KnightInCradlePlugin.FuryVignette)
                {
                    return false;
                }
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                return pr != null && pr.is_alive;
            }
        }

        private static float _noelFuryGlowTimer;
        private static Texture2D _noelFuryGlowTex;
        private static MeshDrawer _noelFuryGlowMesh;
        private static Material _noelFuryGlowMat;
        private static M2RenderTicket _noelFuryGlowTicket;
        private static Map2d _noelFuryGlowMap;

        /// <summary>
        /// 效果7（2026-09-26）：亡者之怒期间诺艾尔"自身中心红色闪烁"。
        /// 复刻小骑士那份 `KnightPrepareFuryGlowMesh`：程序化径向光晕（背后层 PR0），
        /// 脉冲节奏 0.25s 升到峰值 → 短暂保持 → 0.25s 降回（周期 0.51s），峰值透明度默认 0.75。
        /// 屏幕四周的红色滤镜在 `KnightInCradleBehaviour.OnGUI` 里（复用骑士那张红框贴图）。
        /// </summary>
        public static void TickNoelFuryVisual(PRNoel pr)
        {
            try
            {
                bool want = !IsKnightMode && pr != null && pr.is_alive && _noelFuryActive;
                if (!want)
                {
                    ReleaseNoelFuryGlowTicket();
                    _noelFuryGlowTimer = 0f;
                    return;
                }
                _noelFuryGlowTimer += Time.deltaTime;
                EnsureNoelFuryGlowTicket(pr);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>红色光晕：绑当前地图的 MovRenderer，画在诺艾尔身后层 PR0。</summary>
        private static void EnsureNoelFuryGlowTicket(PRNoel pr)
        {
            Map2d mp = pr != null ? pr.Mp : null;
            if (mp == null)
            {
                return;
            }
            if (_noelFuryGlowTex == null)
            {
                _noelFuryGlowTex = MakeRadialGlowTextureNoel(64);
            }
            if (_noelFuryGlowTex == null)
            {
                return;
            }
            if (_noelFuryGlowMesh != null && _noelFuryGlowMap == mp && _noelFuryGlowTicket != null)
            {
                return;
            }
            ReleaseNoelFuryGlowTicket();
            _noelFuryGlowMap = mp;
            _noelFuryGlowMesh = new MeshDrawer(null, 4, 6);
            _noelFuryGlowMesh.draw_gl_only = true;
            _noelFuryGlowMat = MTRX.newMtr(MTRX.ShaderGDT);
            _noelFuryGlowMat.EnableKeyword("NO_PIXELSNAP");
            _noelFuryGlowMesh.activate("noel_fury_glow", _noelFuryGlowMat, false, MTRX.ColWhite, null);
            _noelFuryGlowTicket = mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, PrepareNoelFuryGlowMesh, _noelFuryGlowMesh, null, null);
        }

        private static void ReleaseNoelFuryGlowTicket()
        {
            try
            {
                if (_noelFuryGlowTicket != null && _noelFuryGlowMap != null &&
                    _noelFuryGlowMap.MovRenderer != null)
                {
                    _noelFuryGlowMap.MovRenderer.deassignDrawable(_noelFuryGlowTicket, -1);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_noelFuryGlowMat != null)
                {
                    IN.DestroyOne(_noelFuryGlowMat);
                }
            }
            catch (Exception)
            {
            }
            _noelFuryGlowTicket = null;
            _noelFuryGlowMesh = null;
            _noelFuryGlowMat = null;
            _noelFuryGlowMap = null;
        }

        private static bool PrepareNoelFuryGlowMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw,
            int draw_id, out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mp = _noelFuryGlowMap;
            if (mp == null || _noelFuryGlowMesh == null || draw_id != 0)
            {
                return false;
            }
            _noelFuryGlowMesh.clearSimple();
            PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
            if (pr == null || !pr.is_alive || _noelFuryGlowTex == null || !_noelFuryActive || IsKnightMode)
            {
                MdOut = _noelFuryGlowMesh;
                return true;
            }
            // 与小骑士那份同一个脉冲节奏：0.25s 升到峰值、短暂保持、0.25s 降回。
            float t = _noelFuryGlowTimer % 0.51f;
            float alpha = t < 0.25f
                ? t / 0.25f
                : (t < 0.26f ? 1f : 1f - (t - 0.26f) / 0.25f);
            alpha = Mathf.Clamp01(alpha) * KnightInCradlePlugin.FuryGlowAlpha;
            Tk.Matrix = mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mp.pixel2ux(pr.x * mp.CLEN), mp.pixel2uy(pr.y * mp.CLEN), 0f));
            _noelFuryGlowMesh.initForImgAndTexture(_noelFuryGlowTex);
            Color col = KnightInCradlePlugin.FuryGlowColor;
            _noelFuryGlowMesh.Col = new Color(col.r, col.g, col.b, alpha);
            float size = KnightInCradlePlugin.FuryGlowScale * mp.CLEN;
            _noelFuryGlowMesh.Rect(0f, KnightInCradlePlugin.FuryGlowOffsetY * mp.CLEN, size, size, false);
            MdOut = _noelFuryGlowMesh;
            return true;
        }

        /// <summary>程序化生成径向红色光晕贴图（中心亮、向外平滑衰减到 0）。</summary>
        private static Texture2D MakeRadialGlowTextureNoel(int size)
        {
            try
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - half) / half;
                        float dy = (y + 0.5f - half) / half;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - d);
                        a = a * a;
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply();
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ==================== 护符20 效果6：亡者之怒期间播放"森之领主虚弱"BGM ====================
        /// <summary>0 = 没动过 BGM；1 = 已切曲、等 ACB 就绪后跳块；2 = 已跳到虚弱块。</summary>
        private static int _furyBgmPhase;
        /// <summary>跳块前的等待（秒）。</summary>
        private static float _furyBgmWait;
        /// <summary>cue sheet 异步装载的重试次数。</summary>
        private static int _furyBgmTries;
        /// <summary>进来之前的 isFront BGM（退出时还原用）。</summary>
        private static string _furyBgmPrevSheet;
        private static string _furyBgmPrevCue;

        /// <summary>
        /// 效果6（2026-09-26）：亡者之怒期间播放「森之领主」虚弱阶段那段 BGM，
        /// 一直持续到战斗结束 / 脱离战斗（`IsInBattle()` 变 false）或亡者之怒本身结束。
        ///
        /// 原版实证（0.30g）：森之领主（`NelNBoss_n`，中文本地化 `Enemy_BOSS_NUSI` = 森之领主）
        /// 被 burst 打进虚弱时（`NelNBoss_Nusi.cs` `initBurstStunPhase`）会调
        /// `BGM.GotoBlock(第一次 "D" / 第三次以后 "F")` + `BGM.setOverrideKey("mainbattle" / "challenge_1")`。
        /// 曲子定义在 `Resources/Basic/Data/_bgm` 的 `BGM_battle_nusi` 段：
        /// cue = `BGM_battle_nusi`（单 cue，块 A〜I），块转移表写在 `block_override` 里
        /// （`mainbattle B C, E C, F G` / `challenge_1 B C, E F`）。
        /// 换句话说"虚弱时的音乐"= **同一首 cue 跳到 D（或 F）块**，所以这里直接复用游戏的
        /// BGM 系统（`BGM.load` → `BGM.replace` → `GotoBlock` + `setOverrideKey`），
        /// 音量 / 总线 / 淡入淡出都跟原版一致，不额外拷音频文件。
        ///
        /// 只当成"临时替换前台 BGM"：进入前记下当前 sheet/cue，退出时淡回原来那首；
        /// 如果进来时前台本来就是这首 cue（例如正在打森之领主本人），就不替换、只跳块，
        /// 退出时也不动它（避免把原版战斗曲一起停掉）。
        /// </summary>
        private static void TickNoelFuryBgm(bool active, float dt)
        {
            try
            {
                bool want = active && KnightInCradlePlugin.FuryBgmEnabled && IsInBattle();
                if (!want)
                {
                    StopNoelFuryBgm();
                    return;
                }
                string sheet = KnightInCradlePlugin.FuryBgmSheet;
                string cue = KnightInCradlePlugin.FuryBgmCue;
                if (_furyBgmPhase == 0)
                {
                    // 第一次：先把当前前台 BGM 记下来（还原用），然后进入"装载 cue"阶段。
                    _furyBgmPrevSheet = null;
                    _furyBgmPrevCue = null;
                    try
                    {
                        BGM.getFrontBgm(out _furyBgmPrevSheet, out _furyBgmPrevCue);
                    }
                    catch (Exception)
                    {
                        _furyBgmPrevSheet = null;
                        _furyBgmPrevCue = null;
                    }
                    _furyBgmPhase = 1;
                    _furyBgmWait = 0f;
                    _furyBgmTries = 0;
                }
                if (_furyBgmPhase == 1)
                {
                    _furyBgmWait -= dt;
                    if (_furyBgmWait > 0f)
                    {
                        return;
                    }
                    _furyBgmWait = 0.25f;
                    bool front = false;
                    try
                    {
                        front = BGM.frontBGMIs(sheet, cue);
                    }
                    catch (Exception)
                    {
                        front = false;
                    }
                    if (!front)
                    {
                        // AIC 的 cue sheet 是**异步装载**的：`SND.loaded` 没就绪时
                        // `BgmPlayer.prepare` 会直接返回 false，所以这里要反复试几次。
                        if (_furyBgmTries++ > 24)
                        {
                            _furyBgmPhase = 2; // 试了 ~6 秒还没成（多半是键名写错）：放弃，不动当前 BGM
                            return;
                        }
                        try
                        {
                            BGM.load(sheet, cue, true);
                            BGM.replace(KnightInCradlePlugin.FuryBgmFadeInMs, 0f, true, true);
                        }
                        catch (Exception)
                        {
                        }
                        return;
                    }
                    // cue 就绪：跳到"虚弱"块 + 套上对应的块转移 override（只做一次，
                    // 之后交给原版转移表自己走；每帧都跳会把音乐钉死在 D 块上）。
                    try
                    {
                        string block = KnightInCradlePlugin.FuryBgmBlock;
                        if (!string.IsNullOrEmpty(block))
                        {
                            BGM.GotoBlock(block, true);
                        }
                        BGM.setOverrideKey(KnightInCradlePlugin.FuryBgmOverride ?? "", false);
                    }
                    catch (Exception)
                    {
                    }
                    _furyBgmPhase = 2;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>退出：淡回原来那首 BGM（原本就是森之领主那首时什么都不做）。</summary>
        private static void StopNoelFuryBgm()
        {
            if (_furyBgmPhase == 0)
            {
                return;
            }
            string prevSheet = _furyBgmPrevSheet;
            string prevCue = _furyBgmPrevCue;
            _furyBgmPhase = 0;
            _furyBgmWait = 0f;
            _furyBgmPrevSheet = null;
            _furyBgmPrevCue = null;
            string sheet = KnightInCradlePlugin.FuryBgmSheet;
            string cue = KnightInCradlePlugin.FuryBgmCue;
            try
            {
                // 前台已经不是我们要换掉的那首了（过图/剧情自己换了曲）：不要乱还原。
                if (!BGM.frontBGMIs(sheet, cue))
                {
                    return;
                }
                if (string.IsNullOrEmpty(prevSheet))
                {
                    // 进来之前本来就没有 BGM：直接淡出。
                    BGM.fadeout(0f, KnightInCradlePlugin.FuryBgmFadeOutMs, true);
                    return;
                }
                if (prevSheet == sheet && prevCue == cue)
                {
                    // 本来就在放这首（没替换过，例如正在打森之领主本人）：不动它。
                    return;
                }
                BGM.load(prevSheet, prevCue, true);
                BGM.replace(KnightInCradlePlugin.FuryBgmFadeOutMs, KnightInCradlePlugin.FuryBgmFadeOutMs,
                    true, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 需求（2026-09-26 追加）：亡者之怒期间**删去 HP 缓冲条**。
        /// AIC 的血条在受伤后会留一段颜色更浅的"残影"（`UIStatus.cushion_hp`），
        /// 由 `UIStatus.Update` 每帧按 0.003/frame 慢慢收干（`UIStatus.cs:708-715`），
        /// 玩家侧的观感就是"血条掉得比数字慢"。这里在亡者之怒期间把它一直清零。
        /// 每帧清一次（见 `TickNoelFuryCharm`）+ 挂 `fineHpRatio` 后缀堵住"受伤那一帧刚加进去"。
        /// </summary>
        private static void HideNoelHpCushion(PRNoel pr)
        {
            try
            {
                UIStatus st = UIStatus.Instance;
                if (st != null && st.cushion_hp != 0f)
                {
                    st.cushion_hp = 0f;
                    st.redraw_hp = true;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                // 真正"看起来在回血"的那条更长：AIC 的 **GaugeSaver**（`PR.GSaver.GsHp`）。
                // 它把"受损前的 HP"记成一份可以慢慢恢复的额度：`hp < sval` 时
                // `GsItem.run` 会周期性调 `Pr.cureHp(1)`（`PrGaugeSaver.cs:444-465`），
                // 同时 HUD 还会把 `sval - hp` 画成血条后面那段浅色残影。
                // 亡者之怒期间把 sval 钉在当前 HP 上：残影段消失，回血额度也归零。
                if (pr != null && pr.GSaver != null && pr.GSaver.GsHp != null)
                {
                    pr.GSaver.GsHp.debugSetValue(pr.get_hp());
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 同上：亡者之怒期间**关闭 GaugeSaver 的回血**。
        /// `PrGaugeSaver.GsItem.run` 每当锁时结束就调一次 `DIFF.cureHpFromGSaver`，
        /// 返回 true 就 `cureHp(1)` —— 只要缓冲额度还在，就会一直把 HP 顶回来，
        /// 本护符"每 2 秒 -1HP"的流失永远追不上。这里在亡者之怒期间直接返回 false：
        /// 既不加回血，也不让缓冲额度继续涨（连带把"卡在 30HP 回不上也掉不下去"一起修掉）。
        /// </summary>
        private static bool FuryNoGsaverCurePrefix(PR Pr)
        {
            try
            {
                if (Pr == null || !IsNoelFuryImmune)
                {
                    return true;
                }
                PRNoel noel = KnightInCradleBehaviour.GetPrPublic();
                return !(noel != null && ReferenceEquals(Pr, noel));
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>同上：`UIStatus.fineHpRatio` 后缀，受伤/治疗把缓冲条加回来的那一帧立刻清掉。</summary>
        private static void FuryHideHpCushionPostfix(UIStatus __instance)
        {
            try
            {
                if (__instance != null && IsNoelFuryImmune)
                {
                    // 缓冲段被抹掉的同时必须**自己置重绘标记**：
                    // `UIStatus.fineHpRatio(use_cushion:true)` 本来只靠"cushion 非 0"去驱动血条重画，
                    // 我们把 cushion 清零之后那条路就断了 —— 不补标记的话，
                    // 亡者之怒期间道具回血/自己扣血的**条**不会立刻变（数字也不会）。
                    if (__instance.cushion_hp != 0f)
                    {
                        __instance.cushion_hp = 0f;
                        __instance.redraw_hp = true;
                        __instance.redraw_bar_num = true;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
