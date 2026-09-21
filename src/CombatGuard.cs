using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using evt;
using HarmonyLib;
using m2d;
using nel;
using UnityEngine;
using XX;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    /// <summary>
    /// 战斗守卫：小骑士模式下，隐藏的诺艾尔不造成伤害、也不受到伤害。
    /// 用 Harmony 补丁拦截：伤害入口、MP 扣减、攻击状态切换。
    /// </summary>
    public static class CombatGuard
    {
        public static bool IsKnightMode =>
            KnightInCradlePlugin.KnightModeActive;

        // UI 覆盖状态：仅在数值变化时强制刷新（避免每帧重绘导致闪烁），
        // 并记录“上一帧是否处于骑士覆盖”，用于切回诺艾尔时一次性恢复。
        private static bool _uiWasKnight;
        private static int _uiLastHp = -1;
        private static int _uiLastMaxHp = -1;
        private static int _uiLastSoul = -1;
        private static int _uiLastMaxSoul = -1;
        // HUD 常显（“/”键）用：UIStatus 的显隐状态字段
        private static readonly FieldInfo UiStatusTField = AccessTools.Field(typeof(UIStatus), "t");
        private static readonly FieldInfo UiStatusHoldField = AccessTools.Field(typeof(UIStatus), "ui_hold_time");
        private static readonly FieldInfo UiStatusSettopField = AccessTools.Field(typeof(UIStatus), "t_settop");
        private static readonly FieldInfo UiBaseYLevelField = AccessTools.Field(typeof(UIStatus), "base_y_level");
        // HUD 网格（淡入透明度用）
        private static readonly FieldInfo UiMdHField = AccessTools.Field(typeof(UIStatus), "MdH");
        private static readonly FieldInfo UiMdMField = AccessTools.Field(typeof(UIStatus), "MdM");
        private static readonly FieldInfo CannonSttField = AccessTools.Field(typeof(M2PuncherCannon), "stt");
        // 蜘蛛 BOSS：织网阶段标记 / 受击状态切换
        private static readonly FieldInfo SpiderSpAtkField = AccessTools.Field(typeof(NelNBossSpider), "special_atk_flags");
        private static readonly System.Reflection.MethodInfo SpiderChangeStateMethod =
            AccessTools.Method(typeof(NelEnemy), "changeState");
        private static int _spiderWebHits; // 织网阶段小骑士命中计数（三次触发落地虚弱）

        private static readonly KEY.SIMKEY AllowedMenuKeys =
            KEY.SIMKEY.SUBMIT | KEY.SIMKEY.CANCEL | KEY.SIMKEY.CHECK | KEY.SIMKEY.MENU |
            KEY.SIMKEY.ESC | KEY.SIMKEY.LTAB | KEY.SIMKEY.RTAB;

        private static readonly FieldInfo MvField = AccessTools.Field(typeof(M2PxlAnimator), "Mv");
        // PR.isNoDamageActive()（无参重载）：谷仓教学（house_barn）里 Primula 冲击波用这个判断
        // 是否跳过命中（见 PrimulaPVV11.MgRunShockwave）。找不到时回退到基类 M2MoverPr 上找。
        private static readonly System.Reflection.MethodInfo PrNoDamageActiveMethod =
            AccessTools.Method(typeof(PR), "isNoDamageActive", System.Type.EmptyTypes)
            ?? AccessTools.Method(typeof(m2d.M2MoverPr), "isNoDamageActive", System.Type.EmptyTypes);

        public static void Apply(Harmony harmony)
        {
            TryPatch(harmony, "applyHpDamageSimple",
                AccessTools.Method(typeof(M2PrADmg), "applyHpDamageSimple"),
                nameof(HpDamageSimplePrefix));
            TryPatch(harmony, "M2Attackable.applyHpDamage",
                AccessTools.Method(typeof(M2Attackable), "applyHpDamage", new[] { typeof(int), typeof(bool), typeof(AttackInfo) }),
                nameof(HpDamagePrefix));
            TryPatch(harmony, "PR.applyMpDamage",
                AccessTools.Method(typeof(PR), "applyMpDamage", new[] { typeof(int), typeof(bool), typeof(AttackInfo) }),
                nameof(MpDamagePrefix));
            TryPatch(harmony, "PR.applyMpDamage(5参数)",
                AccessTools.Method(typeof(PR), "applyMpDamage",
                    new[] { typeof(int), typeof(bool), typeof(AttackInfo), typeof(bool), typeof(bool) }),
                nameof(MpDamagePrefix));
            TryPatch(harmony, "M2PrADmg.applyDamage",
                AccessTools.Method(typeof(M2PrADmg), "applyDamage",
                    new[] { typeof(NelAttackInfo), typeof(bool), typeof(string), typeof(bool), typeof(bool) }),
                nameof(PrDmgApplyPrefix));
            TryPatch(harmony, "M2PrADmg.applyDamage(ref HITTYPE)",
                AccessTools.Method(typeof(M2PrADmg), "applyDamage",
                    new[] { typeof(NelAttackInfo), typeof(HITTYPE).MakeByRefType(), typeof(bool), typeof(string), typeof(bool), typeof(bool) }),
                new HarmonyMethod(typeof(CombatGuard), nameof(PrDmgApplyRefPrefix)),
                new HarmonyMethod(typeof(CombatGuard), nameof(PrDmgApplyRefPostfix)),
                null);
            // 只拦“小骑士点燃的 SER.BURNED”造成的周期灼烧伤害（保留着火动作/特效）。
            TryPatch(harmony, "PR.applyDamage(Atk,bool)",
                AccessTools.Method(typeof(PR), "applyDamage",
                    new[] { typeof(NelAttackInfo), typeof(bool) }),
                nameof(BurnSlipDamagePrefix));
            // 轻受击 1 秒锁：锁内不切 DAMAGE 状态，但伤害仍正常结算。
            TryPatch(harmony, "PR.changeState(PR.STATE)",
                AccessTools.Method(typeof(PR), "changeState",
                    new[] { typeof(PR.STATE) }),
                nameof(PrChangeStatePrefix));
            TryPatch(harmony, "M2PrADmg.changeState",
                AccessTools.Method(typeof(M2PrADmg), "changeState",
                    new[] { typeof(PR.STATE), typeof(PR.STATE), typeof(bool), typeof(bool), typeof(bool) }),
                nameof(PrDmgChangeStatePrefix));
            TryPatch(harmony, "M2PrADmg.applyDamageAddition",
                AccessTools.Method(typeof(M2PrADmg), "applyDamageAddition",
                    new[] { typeof(NelAttackInfoBase) }),
                nameof(PrDmgAdditionPrefix));
            // 【只补这一条】“非满血减半”的唯一来源：PR.applyHpDamageRatio → M2PrADmg.applyHpDamageRatio。
            // 只对“远端小骑士的包”（收包端已打 fix_damage 标记且 kind ∈ MGKIND.PR_*）强制 1.0，
            // 满血/非满血都按攻击端发出的数值结算；诺艾尔自己的攻击不带该标记，行为完全不变。
            // 注意：与 PvPDamageMultiplier=2 配套使用（2 抵消转发层固定 ×0.5）。
            TryPatch(harmony, "M2PrADmg.applyHpDamageRatio",
                AccessTools.Method(typeof(M2PrADmg), "applyHpDamageRatio", new[] { typeof(AttackInfo) }),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(PrHpDamageRatioPostfix)),
                null);
            TryPatch(harmony, "PR.applyWindFoc",
                AccessTools.Method(typeof(PR), "applyWindFoc"),
                nameof(PrWindFocPrefix));
            TryPatch(harmony, "PR.runPre(noCarry)",
                AccessTools.Method(typeof(PR), "runPre", Type.EmptyTypes),
                nameof(PrRunPreNoCarryPrefix));
            TryPatch(harmony, "PR.isNoDamageActive()",
                PrNoDamageActiveMethod,
                nameof(PrNoDamageActivePrefix));
            TryPatch(harmony, "M2MovePatSneaker.checkSightPrCheck",
                AccessTools.Method(typeof(nel.mgm.sneaking.M2MovePatSneaker), "checkSightPrCheck"),
                nameof(SneakerCheckSightPrefix));
            TryPatch(harmony, "COOK.initGameScene",
                AccessTools.Method(typeof(COOK), "initGameScene"),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(InitGameScenePostfix)),
                null);
            TryPatch(harmony, "M2Attackable.applyMpDamage",
                AccessTools.Method(typeof(M2Attackable), "applyMpDamage", new[] { typeof(int), typeof(bool), typeof(AttackInfo) }),
                nameof(MpDamagePrefixBase));
            TryPatch(harmony, "NelEnemy.checkDamageStun",
                AccessTools.Method(typeof(NelEnemy), "checkDamageStun",
                    new[] { typeof(NelAttackInfo), typeof(float) }),
                nameof(CheckDamageStunPrefix));
            TryPatch(harmony, "PRMain.changeState",
                AccessTools.Method(typeof(PRMain), "changeState", new[] { typeof(PR.STATE), typeof(PR.STATE) }),
                nameof(ChangeStatePrefix));
            TryPatch(harmony, "EpManager.applyEpDamage",
                AccessTools.Method(typeof(EpManager), "applyEpDamage"),
                nameof(EpDamagePrefix));
            TryPatch(harmony, "M2PxlAnimatorRT.set_color",
                AccessTools.PropertySetter(typeof(M2PxlAnimatorRT), "color"),
                nameof(AnimatorColorPostfix));
            TryPatch(harmony, "M2PxlAnimatorRT.set_alpha",
                AccessTools.PropertySetter(typeof(M2PxlAnimatorRT), "alpha"),
                nameof(AnimatorAlphaPostfix));
            TryPatch(harmony, "EV.lockPrInputManipulate",
                AccessTools.Method(typeof(EV), "lockPrInputManipulate"),
                nameof(LockInputPrefix));
            TryPatch(harmony, "M2PrSkillShieldEvade.isShieldOpeningOnNormal",
                AccessTools.Method(typeof(M2PrSkillShieldEvade), "isShieldOpeningOnNormal"),
                nameof(ShieldOpeningPrefix));
            TryPatch(harmony, "M2PrSkillShieldEvade.runState",
                AccessTools.Method(typeof(M2PrSkillShieldEvade), "runState"),
                nameof(ShieldEvadeRunStatePrefix));
            TryPatch(harmony, "PR.canPullByWorm",
                AccessTools.Method(typeof(PR), "canPullByWorm"),
                nameof(CanPullByWormPrefix));
            TryPatch(harmony, "M2Ser.Add",
                AccessTools.Method(typeof(M2Ser), "Add",
                    new[] { typeof(SER), typeof(int), typeof(int), typeof(bool) }),
                nameof(SerAddPrefix));
            TryPatch(harmony, "MDAT.applyWormTrapDamage",
                AccessTools.Method(typeof(MDAT), "applyWormTrapDamage"),
                nameof(WormTrapDamagePrefix));
            TryPatch(harmony, "FallenCutin.setE",
                AccessTools.Method(typeof(FallenCutin), "setE"),
                nameof(FallenCutinSetEPrefix));
            // 0.29j 上 setE 的 IL 会让 Harmony 编译失败，改挂 FallenCutin.run（每帧驱动入口）：
            // 骑士模式下直接终止过场运行，效果等同“不触发坠落聚焦”。
            TryPatch(harmony, "FallenCutin.run",
                AccessTools.Method(typeof(FallenCutin), "run", new[] { typeof(float) }),
                nameof(FallenCutinRunPrefix));
            TryPatch(harmony, "M2FootManager.rideInitTo",
                AccessTools.Method(typeof(M2FootManager), "rideInitTo"),
                nameof(RideInitToPrefix));
            TryPatch(harmony, "NelEnemy.initAbsorb",
                AccessTools.Method(typeof(NelEnemy), "initAbsorb"),
                nameof(EnemyInitAbsorbPrefix));
            TryPatch(harmony, "PR.initAbsorb",
                AccessTools.Method(typeof(PR), "initAbsorb"),
                nameof(PrInitAbsorbPrefix));
            TryPatch(harmony, "M2SinkEffect.addMover",
                AccessTools.Method(typeof(M2SinkEffect), "addMover"),
                nameof(SinkAddMoverPrefix));
            TryPatch(harmony, "M2LpMapTransferBase.executeTransferFastTravel",
                AccessTools.Method(typeof(M2LpMapTransferBase), "executeTransferFastTravel",
                    new[] { typeof(Map2d), typeof(int), typeof(int), typeof(int) }),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(FastTravelPostfix)),
                null);
            TryPatch(harmony, "UiBenchMenu.ExecuteFastTravel",
                AccessTools.Method(typeof(nel.gm.UiBenchMenu), "ExecuteFastTravel",
                    new[] { typeof(WMIconPosition), typeof(WholeMapItem), typeof(Map2d),
                        typeof(WholeMapItem.WMTransferPoint.WMRectItem) }),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(BenchFastTravelPostfix)),
                null);
            TryPatch(harmony, "WholeMapManager.fnMgRun_initS_Sacred",
                AccessTools.Method(typeof(nel.WholeMapManager), "fnMgRun_initS_Sacred",
                    new[] { typeof(nel.MagicItem), typeof(float) }),
                nameof(SacredRunPrefix));
            // 小骑士模式下诺艾尔魔力条永不破碎：拦截 MpGaugeBreaker 所有增伤入口，
            // 即使圣域魔法钩子因环境差异未生效，魔力条破碎也不会出现。
            TryPatch(harmony, "MpGaugeBreaker.gageDamage",
                AccessTools.Method(typeof(MpGaugeBreaker), "gageDamage", new[] { typeof(bool) }),
                nameof(GaugeBrkGageDamagePrefix));
            TryPatch(harmony, "MpGaugeBreaker.check",
                AccessTools.Method(typeof(MpGaugeBreaker), "check", new[] { typeof(float) }),
                nameof(GaugeBrkCheckPrefix));
            TryPatch(harmony, "MpGaugeBreaker.applyDamage",
                AccessTools.Method(typeof(MpGaugeBreaker), "applyDamage",
                    new[] { typeof(float), typeof(int) }),
                nameof(GaugeBrkApplyDamagePrefix));
            // 小骑士模式下创建“被吞噬”镜头拉近特效（圣域 motherstone 的 ZOOM2_EATEN）时立即停用
            TryPatch(harmony, "PostEffect.setPE",
                AccessTools.Method(typeof(PostEffect), "setPE",
                    new[] { typeof(POSTM), typeof(float), typeof(float), typeof(int) }),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(SetPePostfix)),
                null);
            TryPatch(harmony, "M2LpSummon.deassignActiveWeed",
                AccessTools.Method(typeof(M2LpSummon), "deassignActiveWeed",
                    new[] { typeof(MANA_HIT), typeof(IM2ManaWeedHitable), typeof(AttackInfo) }),
                nameof(DeassignActiveWeedPrefix));
            TryPatch(harmony, "M2PrSkill.isShotgun",
                AccessTools.Method(typeof(M2PrSkill), "isShotgun",
                    new[] { typeof(MagicItem), typeof(float).MakeByRefType(),
                        typeof(float).MakeByRefType(), typeof(bool).MakeByRefType() }),
                nameof(IsShotgunPrefix));
            TryPatch(harmony, "M2PuncherCannon.applyHpDamage",
                AccessTools.Method(typeof(M2PuncherCannon), "applyHpDamage",
                    new[] { typeof(int), typeof(bool), typeof(AttackInfo) }),
                nameof(CannonApplyHpDamagePrefix));
            TryPatch(harmony, "MgBsSpiderTrap.run",
                AccessTools.Method(typeof(MgBsSpiderTrap), "run",
                    new[] { typeof(MagicItem), typeof(float) }),
                nameof(SpiderTrapRunPrefix));
            TryPatch(harmony, "NelNBossSpider.applyDamage",
                AccessTools.Method(typeof(NelNBossSpider), "applyDamage",
                    new[] { typeof(NelAttackInfo), typeof(HITTYPE).MakeByRefType(), typeof(bool) }),
                nameof(SpiderApplyDamagePrefix));
            TryPatch(harmony, "MistManager.addSinkMover",
                AccessTools.Method(typeof(MistManager), "addSinkMover"),
                nameof(SinkAddMoverPrefix));
            TryPatch(harmony, "UIPictureBase.applyDamage",
                AccessTools.Method(typeof(UIPictureBase), "applyDamage"),
                nameof(UiPictureApplyDamagePrefix));
            TryPatch(harmony, "UIBase.fineHpMpRatio",
                AccessTools.Method(typeof(UIBase), "fineHpMpRatio", new[] { typeof(float), typeof(float) }),
                nameof(FineHpMpRatioPrefix));
            TryPatch(harmony, "UIStatus.run",
                AccessTools.Method(typeof(UIStatus), "run", new[] { typeof(float) }),
                new HarmonyMethod(typeof(CombatGuard), nameof(UiStatusRunPrefix)),
                new HarmonyMethod(typeof(CombatGuard), nameof(UiStatusRunPostfix)),
                null);
            TryPatch(harmony, "UIStatus.showO2Gauge",
                AccessTools.Method(typeof(UIStatus), "showO2Gauge"),
                nameof(O2GaugeShowPrefix));
            TryPatch(harmony, "UIStatus.redrawAll",
                AccessTools.Method(typeof(UIStatus), "redrawAll"),
                new HarmonyMethod(typeof(CombatGuard), nameof(RedrawAllPrefix)),
                new HarmonyMethod(typeof(CombatGuard), nameof(RedrawAllPostfix)),
                null);
            // 0.29j 上 drawMHBar/redrawBarNumber 的 IL 会让 Harmony 编译失败，transpiler 无法替换
            // 数字读取源（get_hp 等）。这里直接给 getter 挂前缀：骑士模式下返回骑士数值，
            // 保证 HUD 数字（9/9、180/180）与血条一致。仅限诺艾尔（玩家本体）。
            TryPatch(harmony, "M2Attackable.get_hp",
                AccessTools.Method(typeof(M2Attackable), "get_hp"),
                nameof(GetHpPrefix));
            TryPatch(harmony, "M2Attackable.get_maxhp",
                AccessTools.Method(typeof(M2Attackable), "get_maxhp"),
                nameof(GetMaxHpPrefix));
            TryPatch(harmony, "M2Attackable.get_maxmp",
                AccessTools.Method(typeof(M2Attackable), "get_maxmp"),
                nameof(GetMaxMpPrefix));
            TryPatch(harmony, "PR.getCastableMp",
                AccessTools.Method(typeof(PR), "getCastableMp"),
                nameof(GetCastableMpPrefix));
            TryPatch(harmony, "M2PrMistApplier.run",
                AccessTools.Method(typeof(M2PrMistApplier), "run"),
                nameof(MistApplierRunPrefix));
            TryPatch(harmony, "M2PrMistApplier.activate",
                AccessTools.Method(typeof(M2PrMistApplier), "activate"),
                nameof(MistApplierActivatePrefix));
            // 雾气源头：PR.applyGasDamage 两个重载在骑士模式下直接拦截
            // （一个创建 MistApplier，一个直接上 SER/伤害；防止版本新增雾类型绕过 applier 补丁）
            TryPatch(harmony, "PR.applyGasDamage(MistKind,float)",
                AccessTools.Method(typeof(PR), "applyGasDamage",
                    new[] { typeof(MistManager.MistKind), typeof(float) }),
                nameof(PrGasDamagePrefix));
            TryPatch(harmony, "PR.applyGasDamage(MistKind,Atk)",
                AccessTools.Method(typeof(PR), "applyGasDamage",
                    new[] { typeof(MistManager.MistKind), typeof(MistAttackInfo) }),
                nameof(PrGasDamageAtkPrefix));
            TryPatch(harmony, "UIPictureBase.changeEmotDefault",
                AccessTools.Method(typeof(UIPictureBase), "changeEmotDefault"),
                nameof(UiEmotDefaultPrefix));
            TryPatch(harmony, "UIPictureBase.changeEmotIn",
                AccessTools.Method(typeof(UIPictureBase), "changeEmotIn"),
                nameof(UiEmotInPrefix));
            TryPatch(harmony, "UIPicture.applyGasDamage",
                AccessTools.Method(typeof(UIPicture), "applyGasDamage"),
                nameof(UiGasDamagePrefix));
            // 0.29j 上 changeEmotIn / applyGasDamage 的 IL 会让 Harmony 编译失败，
            // 改挂 UIPicture.run（诺艾尔立绘每帧更新总入口，表情/贴图/毒气特效都在其内部驱动）：
            // 骑士模式下直接跳过整帧立绘更新。
            TryPatch(harmony, "UIPicture.run",
                AccessTools.Method(typeof(UIPicture), "run",
                    new[] { typeof(float), typeof(float), typeof(bool) }),
                nameof(UiPictureRunPrefix));
            // 0.29j 上 changeEmotIn 挂不上补丁，改在它的主要触发入口 readFader 拦截：
            // 骑士模式下不让立绘表情通过 fader 切换（保持切出时设置的战斗姿态），
            // 但 UIPicture.run 本身放行，Spine 动画仍正常播放。
            TryPatch(harmony, "UIPictureBase.readFader",
                AccessTools.Method(typeof(UIPictureBase), "readFader",
                    new[] { typeof(UIPictureFader.UIPFader), typeof(UIPictureFader.UIP_RES),
                        typeof(UIPictureBase.EMSTATE), typeof(bool) }),
                nameof(UiFaderReadPrefix));
            // 宿主碰撞箱尺寸：诺艾尔的尺寸由 PR.setBounds() 决定（12×68 像素，按状态切换重设），
            // 切人时改一次会被它覆盖，所以每次 setBounds 之后立刻把尺寸拉回小骑士尺寸。
            TryPatch(harmony, "PR.setBounds(BOUNDS_TYPE,bool,bool)",
                FindPrSetBounds(),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(PrSetBoundsPostfix)),
                null);
            // 更可靠的源头截断：M2Mover.Size() 是游戏写入体型的**唯一入口**
            // （PR.setBounds 里的 12×68、蹲伏 12×40、压身 12×10 全都会走这里）。
            // 在源头把“游戏想要的值”限制到小骑士尺寸以内，字段 / 碰撞体 / mbottom / 状态机口径就完全一致，
            // 不会出现“字段改了但碰撞体还是诺艾尔大小”。只收紧不放大，所以压身挤过依然可用。
            TryPatch(harmony, "M2Mover.Size",
                AccessTools.Method(typeof(m2d.M2Mover), "Size",
                    new[] { typeof(float), typeof(float), typeof(XX.ALIGN), typeof(XX.ALIGNY), typeof(bool) }),
                nameof(MoverSizePrefix));
            // 兜底：碰撞体重建前再夹一次（防止别的代码绕过 M2Mover.Size 直接写 sizex/sizey 后重建）
            TryPatch(harmony, "M2MvColliderCreatorAtk.recreateExecute",
                AccessTools.Method(typeof(m2d.M2MvColliderCreatorAtk), "recreateExecute"),
                nameof(ColliderRecreatePrefix));
            // 地图互动占位框：PR 把 event_sizex/event_sizey/event_cy 硬编码成诺艾尔体型
            // （12 像素半宽 / 68 像素半高 / 中心在脚底上方 68 像素），
            // M2EventContainer 用它拼事件矩形、M2MoverPr.checkCurrentPoint 用它取事件格 ——
            // 门/出口/长椅/升降台/传送/NPC 等“与地形互动”的判定因此仍按诺艾尔大小。
            // 这几个属性的 getter 是 PR 的 override，直接挂后缀改成小骑士占位框。
            TryPatch(harmony, "PR.get_event_sizex",
                AccessTools.PropertyGetter(typeof(PR), "event_sizex"),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(EventSizexPostfix)),
                null);
            TryPatch(harmony, "PR.get_event_sizey",
                AccessTools.PropertyGetter(typeof(PR), "event_sizey"),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(EventSizeyPostfix)),
                null);
            TryPatch(harmony, "PR.get_event_cy",
                AccessTools.PropertyGetter(typeof(PR), "event_cy"),
                null,
                new HarmonyMethod(typeof(CombatGuard), nameof(EventCyPostfix)),
                null);
        }

        /// <summary>
        /// 取 <c>PR.setBounds(M2MoverPr.BOUNDS_TYPE, bool, bool)</c>：
        /// BOUNDS_TYPE 是 M2MoverPr 里的受保护嵌套枚举，模组代码不能直接引用，
        /// 因此用反射拿类型后按签名取方法；取不到再按“名字 + 3 参数 + 首参类型名”兜底。
        /// </summary>
        private static MethodInfo FindPrSetBounds()
        {
            try
            {
                Type boundsType = AccessTools.TypeByName("m2d.M2MoverPr+BOUNDS_TYPE") ??
                                  AccessTools.Inner(typeof(m2d.M2MoverPr), "BOUNDS_TYPE");
                if (boundsType != null)
                {
                    MethodInfo m = AccessTools.Method(typeof(PR), "setBounds",
                        new[] { boundsType, typeof(bool), typeof(bool) });
                    if (m != null)
                    {
                        return m;
                    }
                }
                MethodInfo[] methods = typeof(PR).GetMethods(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo mi = methods[i];
                    if (mi.Name != "setBounds")
                    {
                        continue;
                    }
                    ParameterInfo[] ps = mi.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType.Name == "BOUNDS_TYPE" &&
                        ps[1].ParameterType == typeof(bool) && ps[2].ParameterType == typeof(bool))
                    {
                        return mi;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static void TryPatch(Harmony harmony, string label, MethodBase method, string prefixName)
        {
            TryPatch(harmony, label, method,
                prefixName != null ? new HarmonyMethod(typeof(CombatGuard), prefixName) : null,
                null, null);
        }

        private static int _patchOk;
        private static int _patchFail;
        private static readonly List<string> _patchFailDetail = new List<string>();

        private static void TryPatch(Harmony harmony, string label, MethodBase method,
            HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod transpiler)
        {
            try
            {
                if (method == null)
                {
                    _patchFail++;
                    _patchFailDetail.Add(label + ": 找不到目标方法");
                    return;
                }
                harmony.Patch(method, prefix: prefix, postfix: postfix, transpiler: transpiler);
                _patchOk++;
            }
            catch (Exception ex)
            {
                _patchFail++;
                string msg = ex.Message;
                if (msg != null && msg.Length > 200)
                {
                    msg = msg.Substring(0, 200);
                }
                _patchFailDetail.Add(label + ": " + ex.GetType().Name + " " + msg);
                // 新版游戏上部分方法会因 IL 无法编译而挂不上（Harmony 的 "IL Compile Error"），
                // 只记 Message 看不出是哪一步失败，这里额外打一条完整堆栈，便于定位要换哪个锚点。
                try
                {
                    string full = ex.ToString();
                    if (full != null && full.Length > 1500)
                    {
                        full = full.Substring(0, 1500) + " …（截断）";
                    }
                    KnightInCradlePlugin.PluginLog?.LogWarning("[KIC][补丁失败详情] " + label + "\n" + full);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>返回本次挂载成功/失败的补丁数量（供 ApplyHarmony 汇总日志）。</summary>
        public static string GetPatchResult()
        {
            string s = _patchOk + " 成功 / " + _patchFail + " 失败";
            if (_patchFailDetail.Count > 0)
            {
                s += "（失败：" + string.Join(" | ", _patchFailDetail) + "）";
            }
            return s;
        }

        // 同一攻击会先走 applyHpDamageSimple 再调 PR.applyHpDamage（=M2Attackable.applyHpDamage），
        // 用 (帧, Atk引用) 去重，保证一次命中只对小骑士扣 1 血
        private static int _lastKnightDmgFrame = -1;
        private static AttackInfo _lastKnightDmgAtk;

        // 受击驱动去重 + 轻受击 1 秒锁
        private static object _reactDedupAtk;
        private static int _reactDedupFrame = -100;
        private static float _lightHitLockUntil;
        // 着火状态本身每 50 帧会通过 PR.applyDamage(AtkSlipDmgBurnedPr) 扣血；
        // 记录一个“由小骑士点燃、期间不结算灼烧伤害”的窗口，只保留视觉效果。
        private static PR _burnNoDamagePr;
        private static int _burnNoDamageUntilFrame = -1;
        // 轻受击锁生效时，屏蔽本帧 AIC 原生的 DAMAGE 状态切换（伤害仍然正常结算）。
        private static PR _suppressLightHitPr;
        private static int _suppressLightHitFrame = -1;
        // 着火时原生管线仍可能选择 DAMAGE/DAMAGE_L 等状态；本标记用于屏蔽整段普通受击状态，
        // 让显式设置的 DAMAGE_BURNED 不被覆盖。
        private static bool _suppressAnyDamageState;
        private static PR _noCarryPr;
        private static int _noCarryUntilFrame = -1;
        private static PR _forceLightHitPr;
        private static int _forceLightHitFrame = -1;
        private static PR _postLightHitPosePr;
        private static int _postLightHitPoseFrame = -1;
        private static PR _forceTripPosePr;
        private static string _forceTripPoseName;
        private static int _forceTripPoseUntilFrame = -1;

        /// <summary>
        /// 【2026-09-17】诺艾尔受击反馈（由小队士的远程攻击触发，收包端本地驱动）。
        ///
        /// 识别方式：攻击端在包里写了统一标签 <c>knockback_ratio_t = 0.777</c>
        /// （诺艾尔自己的攻击恒为 1.0），配合 kind ∈ MGKIND.PR_*（9000~9499）。
        /// 反馈类型由包里的 attr / burst 决定：
        ///   attr == MGATTR.FIRE            → 着火（SER.BURNED，时长 120，不附加伤害）
        ///   |burst_vx| / |burst_vy| > 0.05 → 直线击飞（远离攻击者）+ changeState(DAMAGE_L)
        ///   其它                            → 轻受击 changeState(DAMAGE)（1 秒内不重复触发）
        /// 因为远端小骑士的包带 fix_damage，AIC 会跳过自身的受击状态处理，所以这里显式驱动。
        /// </summary>
        private static void DriveKnightHitReaction(M2PrADmg dmg, AttackInfo Atk)
        {
            try
            {
                if (!(Atk is NelAttackInfo na) || na.PublishMagic == null)
                {
                    return;
                }
                int kind = (int)na.PublishMagic.kind;
                if (kind < 9000 || kind > 9499 || Mathf.Abs(na.knockback_ratio_t - 0.777f) >= 0.01f)
                {
                    return; // 不是小骑士的攻击
                }
                if (ReferenceEquals(_reactDedupAtk, na) && _reactDedupFrame == Time.frameCount)
                {
                    return; // 同一刀重复进入
                }
                _reactDedupAtk = na;
                _reactDedupFrame = Time.frameCount;
                PR pr = dmg != null ? dmg.Pr : null;
                if (pr == null || !pr.is_alive)
                {
                    return;
                }
                if (na.attr == MGATTR.FIRE)
                {
                    // 着火：给 SER.BURNED 以驱动 AIC 原生的着火动作/特效，
                    // 但它每 50 帧会附带 6 点灼烧伤害；这里用窗口屏蔽掉这段伤害。
                    // SER 等级 99 = 满强度；时长 120 帧。
                    _burnNoDamagePr = pr;
                    _burnNoDamageUntilFrame = Time.frameCount + 140;
                    if (pr.Ser != null)
                    {
                        M2SerItem burn = pr.Ser.Add(SER.BURNED, 120, 99, false);
                        if (burn != null)
                        {
                            // M2SerLis_BURNED.init() 会把 maxt 设回 180，
                            // 这里在初始化之后显式钳回需求值 120 帧。
                            burn.maxt = 120;
                        }
                    }
                    // 火球/尖啸的伤害包本身会被 AIC 判成普通受击；这里屏蔽本帧的
                    // DAMAGE 切换，并显式进入真正的着火状态（方向键短时间失效）。
                    _suppressLightHitPr = pr;
                    _suppressLightHitFrame = Time.frameCount;
                    _suppressAnyDamageState = true;
                    pr.changeState(PR.STATE.DAMAGE_BURNED);
                    return;
                }
                if (Mathf.Abs(na.burst_vx) > 0.05f || Mathf.Abs(na.burst_vy) > 0.05f)
                {
                    // 直线击飞：burst_vx / burst_vy 已随包发到本机，
                    // 交给 AIC 原生伤害管线选择 DAMAGE_L 并施加击飞。
                    // 这里不再额外 addFoc，避免双重击飞。
                    return;
                }
                if (_forceLightHitPr == pr && _forceLightHitFrame == Time.frameCount)
                {
                    // 暗影冲刺等指定招式：每次造成伤害都必定触发轻受击，不受 1 秒锁影响。
                    _forceLightHitPr = null;
                    _lightHitLockUntil = Time.unscaledTime + 1f;
                    _suppressAnyDamageState = false;
                    pr.changeState(PR.STATE.DAMAGE_LT);
                    _forceTripPosePr = pr;
                    _forceTripPoseName = PlayNoelTripPose(pr, na);
                    _forceTripPoseUntilFrame = Time.frameCount + 30;
                    return;
                }
                if (Time.unscaledTime >= _lightHitLockUntil)
                {
                    _lightHitLockUntil = Time.unscaledTime + 1f; // 1 秒内不再触发轻受击
                    _suppressAnyDamageState = false;
                    pr.changeState(PR.STATE.DAMAGE); // 锁到期后主动重切，确保动画/状态重新触发
                    PlayNoelLightHitPose(pr, na);    // 只切状态不会驱动本体骨骼，这里补上原生受击姿势
                }
                else
                {
                    // 已处于 1 秒锁内：屏蔽本帧 AIC 原生的 DAMAGE 状态切换，
                    // 但伤害照常结算。
                    _suppressLightHitPr = pr;
                    _suppressLightHitFrame = Time.frameCount;
                    _suppressAnyDamageState = false;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 轻受击时显式调用 AIC 原生的 <c>AnimationShuffler.dmg_normal</c> 生成姿势并套到本体上。
        /// 只调用 <c>changeState(DAMAGE)</c> 时，左侧立绘会反应，但诺艾尔真实模型不会进入硬直；
        /// 这里补上 <c>SpSetPose</c> 后，本体和立绘会同步进入轻受击。
        /// </summary>
        private static void PlayNoelLightHitPose(PR pr, NelAttackInfo na)
        {
            try
            {
                if (pr == null || pr.SfPose == null || na == null)
                {
                    return;
                }
                string fade_key = null;
                string fade_key0 = null;
                string pose = pr.SfPose.dmg_normal(na, Mathf.Max(1, na._hpdmg),
                    M2PrADmg.DMGRESULT.S, 0f, ref fade_key, ref fade_key0);
                if (!string.IsNullOrEmpty(pose))
                {
                    pr.SpSetPose(pose, 1, null, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>暗影冲刺的普通摔倒：切 DAMAGE_LT 并套用原生 dmg_t 姿势。</summary>
        private static string PlayNoelTripPose(PR pr, NelAttackInfo na)
        {
            try
            {
                if (pr == null || pr.SfPose == null || na == null)
                {
                    return null;
                }
                string fade_key = null;
                string fade_key0 = null;
                string pose = pr.SfPose.dmg_normal(na, Mathf.Max(1, na._hpdmg),
                    M2PrADmg.DMGRESULT.L | M2PrADmg.DMGRESULT._DMGT, 0f,
                    ref fade_key, ref fade_key0);
                if (!string.IsNullOrEmpty(pose))
                {
                    pr.SpSetPose(pose, 1, null, false);
                }
                return pose;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// <c>PR.applyDamage(NelAttackInfo, bool)</c> 前缀：
        /// 只拦截“由小骑士点燃的 SER.BURNED”产生的周期性灼烧伤害
        /// （MDAT.AtkSlipDmgBurnedPr），让着火保留动作和特效但不掉血。
        /// </summary>
        private static bool BurnSlipDamagePrefix(PR __instance, NelAttackInfo Atk, ref int __result)
        {
            try
            {
                if (__instance != null && ReferenceEquals(__instance, _burnNoDamagePr) &&
                    Time.frameCount <= _burnNoDamageUntilFrame &&
                    Atk != null && ReferenceEquals(Atk, MDAT.AtkSlipDmgBurnedPr))
                {
                    __result = 0;
                    return false;
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>
        /// <c>PR.changeState(PR.STATE)</c> 前缀：轻受击 1 秒锁生效时，
        /// 跳过本帧的 DAMAGE 状态切换（伤害仍由伤害管线正常结算）。
        /// </summary>
        private static bool PrChangeStatePrefix(PR __instance, PR.STATE _state)
        {
            if (_state == PR.STATE.DAMAGE && _suppressLightHitFrame == Time.frameCount &&
                ReferenceEquals(__instance, _suppressLightHitPr))
            {
                return false;
            }
            if (_suppressAnyDamageState && _suppressLightHitFrame == Time.frameCount &&
                ReferenceEquals(__instance, _suppressLightHitPr) &&
                (int)_state >= (int)PR.STATE.DAMAGE &&
                (int)_state < (int)PR.STATE.DAMAGE_BURNED)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// <c>M2PrADmg.applyHpDamageRatio</c> 后缀：玩家侧会按自身 HP 状态压伤害
        /// （非满血时约 0.5，这就是“满血 50、残血 25”的来源）。
        /// 对“远端小骑士的包”（收包端已打 fix_damage 标记，且 kind 在 MGKIND.PR_* 段）强制 1.0，
        /// 于是满血/非满血都按攻击端发出的数值结算；诺艾尔自己的攻击不带该标记，行为不变。
        /// </summary>
        private static void PrHpDamageRatioPostfix(AttackInfo Atk, ref float __result)
        {
            try
            {
                if (__result >= 0.999f)
                {
                    return;
                }
                if (!(Atk is NelAttackInfo na) || !na.fix_damage || na.PublishMagic == null)
                {
                    return;
                }
                int kind = (int)na.PublishMagic.kind;
                if (kind < 9000 || kind > 9499)
                {
                    return;
                }
                __result = 1f;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 【2026-09-17】把“来自远端小骑士”的伤害标成固定伤害（`fix_damage = true`）。
        ///
        /// 原因：联机收包端（Kaleidoscopic 的 DefaultInboundPacketListener）收到伤害包后新建的
        /// NelAttackInfo **没有 `fix_damage`**，于是 AIC 自己的公式生效
        /// （M2PrADmg.cs:1028）：
        ///   val = _hpdmg × (fix_damage ? 1 : applyHpDamageRatio())
        /// 而玩家（诺艾尔）的 applyHpDamageRatio 会按受伤者 HP 状态压伤害 ——
        /// 表现就是“满血时是真实伤害、非满血时直接砍半”。
        ///
        /// 识别方式：联机包里小骑士的攻击被伪装成 MGKIND.PR_*（9000~9499 段，见 MultiplayerCompat），
        /// 诺艾尔自己的法术不会落在这个区间里，所以这里只对“小骑士的远程攻击”改标记。
        /// </summary>
        private static void MarkRemoteKnightAttackFixed(AttackInfo Atk)
        {
            try
            {
                if (!(Atk is NelAttackInfo na) || na.PublishMagic == null)
                {
                    return;
                }
                int kind = (int)na.PublishMagic.kind;
                if (kind < 9000 || kind > 9499 || na.fix_damage)
                {
                    return;
                }
                na.fix_damage = true;
                // 缓冲条（GSaver）：AIC 会先让缓冲吃掉一部分伤害（满血不吃、掉过血就开始吃，
                // 表现就是“非满血时伤害减半”）。对远端小骑士的包要求穿透缓冲，
                // 让伤害全额直接进 HP（与全力全开模式用的是同一个官方开关）。
                try
                {
                    PRNoel victim = KnightInCradleBehaviour.GetPrPublic();
                    if (victim != null && victim.GSaver != null)
                    {
                        victim.GSaver.penetrateHpDamageReduce();
                    }
                }
                catch (Exception)
                {
                }
                // 【受击效果实验已回退】原来这里对“小骑士包指纹”做 ×2（抵消转发层砍半），
                // 撤掉，改回“攻击端预乘 PvPDamageMultiplier”的旧方案。
                // 【受击效果实验已回退】原来这里按 attr/burst 还原“着火/击飞/轻受击”，
                // 撤掉以免影响伤害结算。
                // 【2026-09-17 撤销收包端 ×2】诺艾尔自己的攻击同样是 MGKIND.PR_* 段，
                // 收包端无法区分“小骑士的包”和“诺艾尔原生攻击”，在这里乘 2 会把诺艾尔
                // 的最终伤害也翻倍。所以收包端只保留 fix_damage（挡掉 AIC 的非满血减伤），
                // 转发层的 0.5 仍由攻击端 PvPDamageMultiplier 预乘抵消。
            }
            catch (Exception)
            {
            }
        }

        private static void RouteDamageToKnight(AttackInfo Atk)
        {
            KnightEntity k = KnightEntity.Instance;
            if (k == null || !k.IsActive)
            {
                return;
            }
            MarkRemoteKnightAttackFixed(Atk);
            // 尖刺/荆棘等“地图伤害”（MAPDMG / ndmg 标记）：小骑士免疫，不转伤
            if (DIFF.isMapDmg(Atk))
            {
                return;
            }
            // 大炮炮弹（MgPuncherCannon）：小骑士免疫，不转伤
            if (Atk is NelAttackInfo naCannon && naCannon.PublishMagic != null &&
                naCannon.PublishMagic.kind == MGKIND.PUNCHER_CANNON)
            {
                return;
            }
            // 蜘蛛陷阱（MgBsSpiderTrap）：小骑士碰到不受伤（陷阱是蛛丝球释放源，可被攻击破坏）
            if (Atk is NelAttackInfo naTrap && naTrap.PublishMagic != null &&
                naTrap.PublishMagic.kind == MGKIND.BASIC_SHOT &&
                naTrap.PublishMagic.Other is MgBsSpiderTrap.MEM)
            {
                return;
            }
            // 谷仓教学（house_barn）Primula 的攻击：小骑士模式下不判定打到诺艾尔，也不转伤给小骑士
            // （安全网；正常路径下 isNoDamageActive 已让教学攻击跳过诺艾尔）。
            if (Atk is NelAttackInfo naPrimula && naPrimula.Caster is PrimulaPVV11 && IsBarnTutorialKnightMode)
            {
                return;
            }
            if (Time.frameCount == _lastKnightDmgFrame && ReferenceEquals(_lastKnightDmgAtk, Atk))
            {
                return;
            }
            _lastKnightDmgFrame = Time.frameCount;
            _lastKnightDmgAtk = Atk;
            k.KnightTakeDamage(Atk);
        }

        private static bool HpDamageSimplePrefix(M2PrADmg __instance, NelAttackInfoBase Atk, out bool force, ref int __result)
        {
            force = false;
            // 与是否处于骑士模式无关：先给“远端小骑士的攻击”打上固定伤害标记，
            // 否则受害者是“玩诺艾尔的玩家”时（那侧没有小骑士实体）会被 AIC 按玩家规则砍半。
            MarkRemoteKnightAttackFixed(Atk as AttackInfo);
            if (!IsKnightMode)
            {
                return true;
            }
            // 该攻击可以对诺艾尔造成伤害 → 转给小骑士扣血；诺艾尔本体仍免疫
            RouteDamageToKnight(Atk);
            __result = 0;
            return false;
        }

        private static bool HpDamagePrefix(M2Attackable __instance, int val, bool force, AttackInfo Atk, ref int __result)
        {
            if (!IsKnightMode || !(__instance is PR))
            {
                return true;
            }
            RouteDamageToKnight(Atk);
            __result = 0;
            return false;
        }

        /// <summary>
        /// 小骑士普攻不施加眩晕：AIC 原版敌人受击时会按概率施加 SER.EATEN（倒地眩晕），
        /// 普攻 attr 默认为 MGATTR.NORMAL（概率乘 0.7），小骑士攻速快时会把敌人一直眩晕。
        /// 普攻的特征：骑士模式下、以诺艾尔为攻击者、且不挂 PublishMagic（法术/技艺都会挂）。
        /// </summary>
        private static bool CheckDamageStunPrefix(NelEnemy __instance, NelAttackInfo Atk, float level,
            ref bool __result)
        {
            if (!IsKnightMode || Atk == null || Atk.PublishMagic != null)
            {
                return true;
            }
            if (Atk.Caster is PRNoel || Atk.AttackFrom is PRNoel)
            {
                __result = false; // 小骑士普攻：跳过眩晕判定
                return false;
            }
            return true;
        }

        /// <summary>
        /// M2PrADmg.applyDamage 是诺艾尔伤害管线的最外层（激光/光束/抓取/下压等所有伤害最终都汇聚到这里）。
        /// 小骑士模式下整条管线跳过：非地图伤害转给小骑士扣血，地图伤害（尖刺/激光等）诺艾尔完全免疫且无任何受击反应。
        /// </summary>
        private static bool PrDmgApplyPrefix(M2PrADmg __instance, NelAttackInfo Atk, ref int __result)
        {
            MarkRemoteKnightAttackFixed(Atk);
            DetectShadowDash(__instance, Atk);
            DriveKnightHitReaction(__instance, Atk);
            if (!IsKnightMode)
            {
                return true;
            }
            RouteDamageToKnight(Atk);
            __result = 0;
            return false;
        }

        private static bool PrDmgApplyRefPrefix(M2PrADmg __instance, NelAttackInfo Atk, ref HITTYPE add_hittype,
            ref int __result)
        {
            MarkRemoteKnightAttackFixed(Atk);
            DetectShadowDash(__instance, Atk);
            DriveKnightHitReaction(__instance, Atk);
            if (!IsKnightMode)
            {
                return true;
            }
            RouteDamageToKnight(Atk);
            add_hittype = HITTYPE.NONE;
            __result = 0;
            return false;
        }

        /// <summary>
        /// 暗影冲刺包：记录冲刺方向并清除实际击退速度；本轮原生伤害状态会被屏蔽，
        /// 等 postfix 根据诺艾尔最终血量选择“摔倒（DAMAGE_LT）”或“直线击飞（DAMAGE_L）”。
        /// </summary>
        private static void DetectShadowDash(M2PrADmg dmg, NelAttackInfo Atk)
        {
            try
            {
                if (dmg == null || Atk == null || dmg.Pr == null)
                {
                    return;
                }
                if (Mathf.Abs(Atk.burst_center - KnightEntity.ShadowDashPacketMarker) >= 0.001f)
                {
                    return;
                }
                // 防止远端骑士代理的移动把诺艾尔当成可携带速度拖走。
                dmg.Pr.addD(M2MoverPr.DECL.DO_NOT_CARRY_BY_OTHER);
                _noCarryPr = dmg.Pr;
                _noCarryUntilFrame = Time.frameCount + 30;
                _forceLightHitPr = dmg.Pr;
                _forceLightHitFrame = Time.frameCount;
                _postLightHitPosePr = dmg.Pr;
                _postLightHitPoseFrame = Time.frameCount;
                // 清掉原版击退/拖拽参数，避免诺艾尔被暗影冲刺带着走。
                Atk.burst_center = 0f;
                Atk.burst_vx = 0f;
                Atk.burst_vy = 0f;
                Atk.knockback_len = 0f;
                Atk.knockback_ratio_p = 0f;
                Atk._apply_knockback_current = false;
                Atk.huttobi_ratio = -1000f;
            }
            catch (Exception)
            {
            }
        }

        private static void PrDmgApplyRefPostfix(M2PrADmg __instance, NelAttackInfo Atk)
        {
            try
            {
                if (__instance == null || Atk == null)
                {
                    return;
                }
                if (Mathf.Abs(Atk.burst_center - KnightEntity.FlukePacketMarker) < 0.001f)
                {
                    if (__instance.Pr != null && __instance.Pr.UP != null)
                    {
                        // 只要被吸虫命中，就把左侧立绘切到“虫墙”动画。
                        __instance.Pr.UP.applyDamage(MGATTR.WORM, 0f, 0f,
                            UIPictureBase.EMSTATE.DEAD, false, "insected", true);
                    }
                    TryGrantFlukeRewards(__instance.Pr);
                    Atk.burst_center = 0f; // 清除吸虫标记，避免影响后续伤害
                    return;
                }
                if (Mathf.Abs(Atk.burst_center - KnightEntity.ShelterPacketMarker) < 0.001f)
                {
                    // 防御者纹章：造成 HP 伤害的同时扣 10 MP。
                    if (__instance.Pr != null)
                    {
                        __instance.Pr.applyMpDamage(10, false, Atk);
                    }
                    Atk.burst_center = 0f;
                }
                if (_postLightHitPosePr == __instance.Pr && _postLightHitPoseFrame == Time.frameCount)
                {
                    _postLightHitPosePr = null;
                    _suppressAnyDamageState = false;
                    _suppressLightHitFrame = -1;
                    if (__instance.Pr != null)
                    {
                        __instance.Pr.changeState(PR.STATE.DAMAGE_LT);
                        PlayNoelTripPose(__instance.Pr, Atk);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 诺艾尔（本地与远端都走这里）每次受到吸虫伤害，三个独立 roll：
        ///   50% 满级（grade 4 = 5 星）“新鲜的诺艾尔汁”（需要空瓶 + 容量）
        ///   50% 满级“诺艾尔的卵”（只需要容量）
        ///   50% 满级“诺艾儿乳”mtr_noel_milk（需要空瓶 + 容量）
        /// 诺艾儿乳与诺艾儿汁同属 WATER（WLink 水瓶物品），必须保留空瓶让 Add 自己完成
        /// “空瓶 → 乳”的链接，不能先手动扣瓶。
        /// </summary>
        private static void TryGrantFlukeRewards(PR pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null || nm2d.IMNG == null)
                {
                    return;
                }
                ItemStorage st = nm2d.IMNG.getInventory();
                if (st == null)
                {
                    return;
                }
                // 50%：新鲜的诺艾尔汁，满级（grade 4）
                if (UnityEngine.Random.value < 0.50f)
                {
                    NelItem juice = NelItem.GetById("mtr_noel_juice0", true);
                    NelItem bottle = NelItem.GetById("mtr_bottle0", true);
                    if (juice != null && bottle != null && st.getEmptyBottleCount() > 0 &&
                        st.getItemCapacity(juice, false, false) > 0)
                    {
                        // 果汁是 WLink 水瓶物品：必须保留空瓶让 Add 完成“空瓶→果汁”链接，
                        // 不能先手动扣瓶，否则 Add 找不到可连接的空瓶会直接失败。
                        st.Add(juice, 1, 4, true, true);
                    }
                }
                // 50%：诺艾尔的卵，满级（grade 4）
                if (UnityEngine.Random.value < 0.50f)
                {
                    NelItem egg = NelItem.GetById("mtr_noel_egg", true);
                    if (egg != null && st.getItemCapacity(egg, false, false) > 0)
                    {
                        st.Add(egg, 1, 4, true, true);
                    }
                }
                // 50%：诺艾儿乳，满级（grade 4）
                if (UnityEngine.Random.value < 0.50f)
                {
                    NelItem milk = NelItem.GetById(NelItem.noel_milk_key, true);
                    NelItem bottle = NelItem.GetById(NelItem.empty_bottle_key, true);
                    if (milk != null && bottle != null && st.getEmptyBottleCount() > 0 &&
                        st.getItemCapacity(milk, false, false) > 0)
                    {
                        st.Add(milk, 1, 4, true, true);
                    }
                }
            }
            catch (Exception)
            {
            }
        }


        /// <summary>M2PrADmg.changeState：伤害/抓取/控制状态的直接注入入口，小骑士模式下只允许 NORMAL/_OFFLINE。</summary>
        private static bool PrDmgChangeStatePrefix(M2PrADmg __instance, PR.STATE state, PR.STATE prestate,
            bool n_dmg, bool pre_dmg, bool n_trapped_state)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            return state == PR.STATE.NORMAL || state == PR.STATE._OFFLINE;
        }

        /// <summary>附加伤害效果（灼烧/毒等 tick 追加）：小骑士模式下全部拦截。</summary>
        private static bool PrDmgAdditionPrefix(M2PrADmg __instance, NelAttackInfoBase Atk, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false;
            return false;
        }

        /// <summary>风压/吹飞：小骑士模式下诺艾尔不受任何风力影响。</summary>
        private static bool PrWindFocPrefix(PR __instance)
        {
            return !IsKnightMode;
        }

        /// <summary>暗影冲刺命中后临时禁止诺艾尔被其他移动体携带，避免被远端骑士代理拖走。</summary>
        private static bool PrRunPreNoCarryPrefix(PR __instance)
        {
            if (_noCarryPr == __instance && Time.frameCount > _noCarryUntilFrame)
            {
                __instance.remD(M2MoverPr.DECL.DO_NOT_CARRY_BY_OTHER);
                _noCarryPr = null;
            }
            return true;
        }

        /// <summary>
        /// 谷仓教学（house_barn）+ 小骑士模式：教学的攻击不应判定命中诺艾尔。
        /// 用于让 Primula 的冲击波跳过“被弹”失败判定、并让教学弹幕不对诺艾尔生效。
        /// </summary>
        private static bool IsBarnTutorialKnightMode
        {
            get
            {
                if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive)
                {
                    return false;
                }
                try
                {
                    Map2d mp = (M2DBase.Instance as NelM2DBase)?.curMap;
                    return mp != null && mp.key == "house_barn";
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// PR.isNoDamageActive()（无参）：谷仓教学里 Primula 冲击波命中判定的前置门槛。
        /// 小骑士模式下返回 true → MgRunShockwave 整体跳过（不扣血、不设置 HITTED 失败标志）；
        /// 教学弹幕射线也会因目标免伤而跳过/穿过诺艾尔。
        /// </summary>
        private static bool PrNoDamageActivePrefix(PR __instance, ref bool __result)
        {
            if (!IsBarnTutorialKnightMode)
            {
                return true;
            }
            __result = true;
            return false;
        }

        /// <summary>
        /// 潜行小游戏（士兵手电筒搜索）：小骑士模式下跳过士兵对“诺艾尔”的视野判定——
        /// 诺艾尔隐藏且与小骑士位置同步，被光照到也不算被发现（因为不是诺艾尔本人）。
        /// </summary>
        private static bool SneakerCheckSightPrefix(nel.mgm.sneaking.M2MovePatSneaker __instance, M2MoverPr Pr)
        {
            return !IsKnightMode;
        }

        /// <summary>读取存档/新游戏后：小骑士血量回满、灵魂设为 90（实体未创建时置标记，首次生成补设置）。</summary>
        private static void InitGameScenePostfix(NelM2DBase M2D)
        {
            // 读取存档/新游戏：认真模式复位为关闭
            KnightInCradleBehaviour.DisableSeriousModeOnLoad();
            // 本次会话第一次切小骑士时重新播放 HUD 淡入，之后切出直接显示
            KnightHudDeco.ResetRevealForSaveLoad();
            // 读档/新游戏：换图后直接把小骑士对齐到诺艾尔，避免残留在旧地图坐标
            KnightEntity.JustLoadedSave = true;
            // 读档/新游戏：AIC 重建地图渲染器，旧渲染票据失效——标记待重绑
            KnightEntity.PendingLoadRebind = true;
            KnightEntity.ResetOnLoad = true;
            // 读档/新游戏：恢复护符装备（SF 字典此时已由存档二进制读出）
            CharmSave.RestoreAfterLoad();
            if (KnightEntity.Instance != null)
            {
                KnightEntity.ResetOnLoad = false;
                KnightEntity.Instance.FillHealthSoul();
            }
            // 读档/新游戏：复活点更新为当前存档的出生位置（防残留上一存档坐过的长椅）
            try
            {
                PRNoel prNoel = KnightInCradleBehaviour.GetPrPublic();
                if (KnightEntity.Instance != null && prNoel != null)
                {
                    KnightEntity.Instance.SetRespawnToNoel(prNoel);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 复用诺艾尔原版 HUD：骑士模式下把 HP/MP 条的数据源替换为小骑士的独立数值。
            /// HP 以 9 格为单位显示（每 1 点 = 一格），MP 条显示灵魂（0~180）。
        /// </summary>
        private static void FineHpMpRatioPrefix(UIBase __instance, ref float hp_ratio, ref float mp_ratio)
        {
            // 严格双重条件：只有小骑士真正激活时才覆盖；切回诺艾尔（IsActive=false）立即退出，绝不干预原版 UI
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive)
            {
                return;
            }
            KnightEntity k = KnightEntity.Instance;
            hp_ratio = k.MaxHealth > 0
                ? Mathf.CeilToInt(k.Health * 9f / k.MaxHealth) / 9f
                : 0f;
            mp_ratio = k.MaxSoul > 0
                ? (k.SoulInfinite ? 1f : Mathf.Clamp01((float)k.Soul / k.MaxSoul))
                : 0f;
        }

        /// <summary>骑士模式下让诺艾尔的 get_hp 返回小骑士血量（HUD 数字用）。</summary>
        private static bool GetHpPrefix(M2Attackable __instance, ref float __result)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive ||
                !(__instance is PRNoel))
            {
                return true;
            }
            __result = KnightEntity.Instance.Health;
            return false;
        }

        private static bool GetMaxHpPrefix(M2Attackable __instance, ref float __result)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive ||
                !(__instance is PRNoel))
            {
                return true;
            }
            __result = KnightEntity.Instance.MaxHealth;
            return false;
        }

        private static bool GetMaxMpPrefix(M2Attackable __instance, ref float __result)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive ||
                !(__instance is PRNoel))
            {
                return true;
            }
            __result = KnightEntity.Instance.SoulInfinite
                ? 999999f
                : KnightEntity.Instance.MaxSoul;
            return false;
        }

        private static bool GetCastableMpPrefix(PR __instance, ref float __result)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive ||
                !(__instance is PRNoel))
            {
                return true;
            }
            __result = KnightEntity.Instance.SoulInfinite
                ? 999999f
                : KnightEntity.Instance.Soul;
            return false;
        }

        /// <summary>
        /// UIStatus.run 执行前：HUD 固定常显（模组不再提供开关），
        /// 把 base_y_level 顶到 1，避免 UIBase 的 visib_t 滑入动画把 HUD 从屏幕下方升起。
        /// </summary>
        private static void UiStatusRunPrefix(UIStatus __instance)
        {
            // 常显：把 base_y_level 顶到 1（HUD 永远停在终点），并确保 Gob 激活
            try
            {
                UiBaseYLevelField.SetValue(__instance, 1f);
                if (__instance.GetGob() != null && !__instance.GetGob().activeSelf)
                {
                    __instance.GetGob().SetActive(true);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
            /// UIStatus 每帧刷新后：强制 HP/MP 条比例使用小骑士独立数值（_health/9、_soul/180），
        /// 并强制红绘，确保原版血条显示的是小骑士的数据。
        /// </summary>
        private static void UiStatusRunPostfix(UIStatus __instance)
        {
            bool knightActive = IsKnightMode &&
                KnightEntity.Instance != null && KnightEntity.Instance.IsActive;

            // HUD 固定常显（模组不再提供开关）：诺艾尔/小骑士都强制常显
            // （t_settop=40 → base_y_level>0，顶住游戏的自动隐藏倒计时）。
            try
            {
                UiStatusTField.SetValue(__instance, 9999f);
                UiStatusHoldField.SetValue(__instance, 9999f);
                UiStatusSettopField.SetValue(__instance, 40f);
                __instance.need_reposit = true;
            }
            catch (Exception)
            {
            }

            // 骑士模式每帧强制重绘：drawMHBar 会用原版颜色重建网格，
            // RedrawAllPostfix 再按“原始颜色”区分填充段/背景段（详见 RecolorMesh）。
            // 只干预数值/染色，不干预显隐计时。
            if (knightActive)
            {
                try
                {
                    __instance.redraw_hp = true;
                    __instance.redraw_mp = true;
                    __instance.redraw_bar_num = true;
                    __instance.redraw_gage = true;
                    __instance.redraw_gage_back = true;
                }
                catch (Exception)
                {
                }
            }

            // 骑士模式下诺艾尔不允许有“蓄力预消耗”魔力残留：
            // UIStatus.run 每帧会从诺艾尔 Skill 重新读出 mp_hold（预消耗段），
            // 这里一旦发现就清掉，避免小骑士灵魂条多出一段魔法蓄力条。
            if (knightActive)
            {
                try
                {
                    if ((int)AccessTools.Field(typeof(UIStatus), "mp_hold").GetValue(__instance) != 0)
                    {
                        PR prHold = (M2DBase.Instance as NelM2DBase)?.curMap?.Pr as PR;
                        if (prHold != null && prHold.Skill != null)
                        {
                            prHold.Skill.killHoldMagic(false, false, false);
                        }
                    }
                    // 清空特殊魔力条（SpMp）：地雷/黑洞/花环/水碎等魔法预消耗段
                    // 会画在魔力条后方并扣减上限，骑士模式下不允许残留
                    PR prSp = (M2DBase.Instance as NelM2DBase)?.curMap?.Pr as PR;
                    if (prSp != null && prSp.SpMp != null)
                    {
                        prSp.SpMp.Clear();
                    }
                }
                catch (Exception)
                {
                }
            }

            if (!knightActive)
            {
                // 切回诺艾尔：用诺艾尔真实数据一次性重算比例并强制重绘，
                // 洗掉小骑士残留的白色/空条状态；之后完全交给原版逻辑，不再干预。
                if (_uiWasKnight)
                {
                    _uiWasKnight = false;
                    _uiLastHp = _uiLastMaxHp = _uiLastSoul = _uiLastMaxSoul = -1;
                    try
                    {
                        __instance.fineHpRatio();
                        __instance.fineMpRatio();
                    }
                    catch (Exception)
                    {
                    }
                    __instance.redraw_hp = true;
                    __instance.redraw_mp = true;
                    __instance.redraw_bar_num = true;
                }
                return;
            }
            _uiWasKnight = true;
            KnightEntity k = KnightEntity.Instance;
            int hp = Mathf.Clamp(k.Health, 0, k.MaxHealth);
            int maxHp = Mathf.Max(1, k.MaxHealth);
            int soul = Mathf.Clamp(k.Soul, 0, k.MaxSoul);
            int maxSoul = Mathf.Max(1, k.MaxSoul);
            if (k.SoulInfinite)
            {
                soul = maxSoul;
            }
            if (hp == _uiLastHp && maxHp == _uiLastMaxHp &&
                soul == _uiLastSoul && maxSoul == _uiLastMaxSoul)
            {
                return; // 数值没变：不打扰 UI，避免数字闪烁/白条残留
            }
            _uiLastHp = hp;
            _uiLastMaxHp = maxHp;
            _uiLastSoul = soul;
            _uiLastMaxSoul = maxSoul;
            // 比例 + cushion 一起覆盖，保证进度条与数字一致、无白色闪烁残影
            AccessTools.Field(typeof(UIStatus), "hp_ratio")
                .SetValue(__instance, (float)hp / maxHp);
            AccessTools.Field(typeof(UIStatus), "mp_ratio")
                .SetValue(__instance, (float)soul / maxSoul);
            AccessTools.Field(typeof(UIStatus), "cushion_hp").SetValue(__instance, 0f);
            AccessTools.Field(typeof(UIStatus), "cushion_mp").SetValue(__instance, 0f);
            __instance.redraw_hp = true;
            __instance.redraw_mp = true;
            __instance.redraw_bar_num = true;
        }

        /// <summary>骑士模式下不显示 O2 呼吸条（毒雾/水呛的氧气 UI）。</summary>
        private static bool O2GaugeShowPrefix(UIStatus __instance)
        {
            return !IsKnightMode;
        }

        /// <summary>
        /// 绘制前强制骑士数值：redrawAll 是血条/魔力条/数字的统一绘制入口。
        /// 骑士模式下在绘制瞬间锁定比例、清零 cushion，并清除“咏唱预备 hold”段——
        /// hold 段会叠加在灵魂条上把条撑满（数字正确但条显示满的问题就来自它）。
        /// </summary>
        private static void RedrawAllPrefix(UIStatus __instance)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive)
            {
                return;
            }
            // 诺艾尔魔力槽碎裂（GaugeBrk）会画在灵魂条上：骑士模式下每次重绘前复位，
            // 保证灵魂条始终显示完整（切出小骑士后碎裂即被消除）。
            RestoreNoelMpGauge();
            KnightEntity k = KnightEntity.Instance;
            int hp = Mathf.Clamp(k.Health, 0, k.MaxHealth);
            int maxHp = Mathf.Max(1, k.MaxHealth);
            int soul = Mathf.Clamp(k.Soul, 0, k.MaxSoul);
            int maxSoul = Mathf.Max(1, k.MaxSoul);
            if (k.SoulInfinite)
            {
                soul = maxSoul;
            }
            AccessTools.Field(typeof(UIStatus), "hp_ratio").SetValue(__instance, (float)hp / maxHp);
            AccessTools.Field(typeof(UIStatus), "mp_ratio").SetValue(__instance, (float)soul / maxSoul);
            AccessTools.Field(typeof(UIStatus), "cushion_hp").SetValue(__instance, 0f);
            AccessTools.Field(typeof(UIStatus), "cushion_mp").SetValue(__instance, 0f);
            AccessTools.Field(typeof(UIStatus), "mp_hold").SetValue(__instance, 0);
        }

        /// <summary>
        /// HUD 淡入：骑士模式下，把 HUD 网格顶点透明度乘上 RevealProgress（0→1）。
        /// </summary>
        private static void RedrawAllPostfix(UIStatus __instance)
        {
            if (!IsKnightMode || KnightEntity.Instance == null || !KnightEntity.Instance.IsActive)
            {
                return;
            }
            float a = Mathf.Clamp01(KnightHudDeco.RevealProgress);
            // 染黑/染白填充条；数字（MdGageT）、边框（MdBase）保持原版。
            // 血条颜色优先级：蓝色（生命血/乔尼的祝福）> 橙色（蜂巢之血）> 黑色（默认）
            uint hpColor = 0x000000U;
            bool lifeblood = KnightEntity.Instance.Health > KnightEntity.Instance.MaxHealth ||
                             CharmEffects.IsEquipped(CharmEffects.JohnnyId);
            if (lifeblood)
            {
                hpColor = 0x3399FFU;
            }
            else if (CharmEffects.IsEquipped(CharmEffects.HiveId))
            {
                hpColor = 0xFFA500U;
            }
            RecolorMesh(__instance, UiMdHField, hpColor, a);
            // MP（灵魂）填充：白色。灵魂为 0 时 AIC 不画填充段，网格前 4 顶点是“空条背景”
            // （带 +1px 越界补边），不能染白，否则整条变白且出现超出边框的白条；此时全透明。
            int soulNow = KnightEntity.Instance != null ? KnightEntity.Instance.Soul : 0;
            if (soulNow > 0)
            {
                RecolorMesh(__instance, UiMdMField, 0xFFFFFFU, a);
            }
            else
            {
                RecolorMesh(__instance, UiMdMField, 0, 0f);
            }
        }

        /// <summary>把 HUD 网格所有顶点染成指定 RGB，保留原 alpha（淡入乘 RevealProgress）。</summary>
        private static void RecolorMesh(UIStatus ui, FieldInfo field, uint rgb, float a)
        {
            if (field == null)
            {
                return;
            }
            try
            {
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
                byte aa = (byte)(255f * Mathf.Clamp01(a));
                byte r = (byte)(rgb >> 16);
                byte g = (byte)(rgb >> 8);
                byte b = (byte)rgb;
                // drawMHBar 绘制顺序：填充段（前 4 顶点）→ gsave → cushion → hold → 空条背景。
                // 骑士模式只保留填充段（染黑/白），其余附加段（cushion/gsave/背景）全部透明，
                // 避免它们被染成黑/白后形成“穿出框的长条”。
                int keepVerts = Mathf.Min(4, cols.Length);
                for (int i = 0; i < cols.Length; i++)
                {
                    // 前 4 个顶点 = 填充段本体，染成黑/白；其余顶点 = 附加段，完全透明
                    if (i >= keepVerts)
                    {
                        cols[i].a = 0;
                        continue;
                    }
                    cols[i].r = r;
                    cols[i].g = g;
                    cols[i].b = b;
                    if (a < 1f)
                    {
                        cols[i].a = (byte)(cols[i].a * aa / 255);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool KnightUiActive()
        {
            return IsKnightMode && KnightEntity.Instance != null && KnightEntity.Instance.IsActive;
        }

        private static bool MpDamagePrefix(PR __instance, int val, bool force, AttackInfo Atk, ref int __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = 0;
            return false;
        }

        private static bool MpDamagePrefixBase(M2Attackable __instance, int val, bool force, AttackInfo Atk, ref int __result)
        {
            if (!IsKnightMode || !(__instance is PR))
            {
                return true;
            }
            __result = 0;
            return false;
        }

        private static bool ChangeStatePrefix(PR __instance, PR.STATE state, PR.STATE prestate)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            // 小骑士模式下诺艾尔只允许停留在 NORMAL（_OFFLINE 允许）：
            // 版本更新新增的任何抓取/控制/伤害/负面状态都会自动被拦下，天然免疫
            return state == PR.STATE.NORMAL || state == PR.STATE._OFFLINE;
        }

        private static bool CanPullByWormPrefix(PR __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 小骑士模式下诺艾尔免疫虫巢/抓取蠕虫的拉扯
            return false;
        }

        private static bool SerAddPrefix(M2Ser __instance, ref M2SerItem __result)
        {
            if (!IsKnightMode || !(__instance.Mv is PR))
            {
                return true;
            }
            __result = null; // 小骑士模式下诺艾尔免疫一切状态效果（中毒/麻痹/束缚等）
            return false;
        }

        private static bool WormTrapDamagePrefix(PR Pr, int count, bool decline_additional_effect)
        {
            return !IsKnightMode; // 小骑士模式下虫巢抓取伤害不作用于诺艾尔
        }

        private static bool FallenCutinSetEPrefix(FallenCutin __instance, PR _Pr, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 小骑士模式下不触发对隐藏诺艾尔的镜头聚焦/过场
            return false;
        }

        /// <summary>
        /// 0.29j 上 FallenCutin.setE 的 IL 让 Harmony 编译失败，这里改挂 run：
        /// 骑士模式下直接终止坠落聚焦过场的运行（run 返回 false 会被调用方清除）。
        /// </summary>
        private static bool FallenCutinRunPrefix(FallenCutin __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 终止过场，由调用方 Clear
            return false;
        }

        private static bool RideInitToPrefix(M2FootManager __instance, ref IFootable __result)
        {
            if (!IsKnightMode || !(__instance.Mv is PR))
            {
                return true;
            }
            __result = null; // 小骑士模式下诺艾尔不会被任何怪物吸附/骑乘（位置由 setTo 跟随骑士）
            return false;
        }

        private static bool EnemyInitAbsorbPrefix(NelEnemy __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 小骑士模式下怪物无法对隐藏的诺艾尔发起吸收/吸附抓取
            return false;
        }

        private static bool PrInitAbsorbPrefix(PR __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 小骑士模式下诺艾尔免疫一切吸收/吸附抓取
            return false;
        }

        private static bool SinkAddMoverPrefix(M2Mover Mv)
        {
            // 小骑士模式下诺艾尔不进入毒雾/水下的下沉独立渲染，避免模型显形
            return !IsKnightMode || !(Mv is PR);
        }

        private static bool UiPictureApplyDamagePrefix(UIPictureBase __instance)
        {
            // 小骑士模式下不显示诺艾尔的受击/捂嘴等过场 UI 大图（也是“视线受阻”的来源）
            return !IsKnightMode;
        }

        private static bool MistApplierRunPrefix(M2PrMistApplier __instance,
            ref M2MoverPr.PR_MNP manip, float fcnt, ref M2PrMistApplier __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = __instance; // 小骑士模式下诺艾尔不进入毒雾/呛咳流程（UI/音效/伤害全停）
            return false;
        }

        private static bool MistApplierActivatePrefix(M2PrMistApplier __instance)
        {
            // 小骑士模式下毒气不启动：不设置气体后处理，也不触发 recheck_emot 咳嗽表情/姿态
            return !IsKnightMode;
        }

        /// <summary>雾气源头拦截：PR.applyGasDamage（创建 MistApplier 的入口）。</summary>
        private static bool PrGasDamagePrefix(PR __instance)
        {
            return !IsKnightMode;
        }

        /// <summary>雾气源头拦截：PR.applyGasDamage（直接上 SER/伤害的入口）。</summary>
        private static bool PrGasDamageAtkPrefix(PR __instance)
        {
            return !IsKnightMode;
        }

        private static bool UiEmotDefaultPrefix(UIPictureBase __instance)
        {
            // 小骑士模式下诺艾尔的表情贴图（咳嗽/捂嘴等）不更新不显示
            return !IsKnightMode;
        }

        private static bool UiEmotInPrefix(UIPictureBase __instance)
        {
            return !IsKnightMode;
        }

        /// <summary>
        /// 0.29j 上 changeEmotIn / applyGasDamage 的 IL 让 Harmony 编译失败，
        /// 这里改挂 UIPicture.run：骑士模式下跳过诺艾尔立绘整帧更新
        /// （表情切换、贴图特效、毒气捂嘴贴图全部不再刷新）。
        /// </summary>
        private static bool UiPictureRunPrefix(UIPicture __instance)
        {
            // 骑士模式：确保立绘处于“战斗”姿态。首次切出时立绘资源可能未就绪
            // （changeEmotIn 返回 NOW_LOADING 静默失败），这里每帧重试直到成功。
            if (IsKnightMode)
            {
                try
                {
                    var fi = AccessTools.Field(typeof(UIPictureBase), "pre_emstate");
                    if (fi != null)
                    {
                        var st = (UIPictureBase.EMSTATE)(uint)fi.GetValue(__instance);
                        if ((st & UIPictureBase.EMSTATE.BATTLE) == 0)
                        {
                            __instance.changeEmotIn(UIEMOT.STAND, UIPictureBase.EMSTATE.BATTLE);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            // 放行 run：立绘 Spine 动画正常播放（切出小骑士后战斗立绘会动）。
            // 表情锁定交给 UiFaderReadPrefix / UiEmotDefaultPrefix。
            return true;
        }

        /// <summary>
        /// 骑士模式下拦截 readFader：立绘表情不通过 fader 切换，
        /// 保持切出时设置的战斗姿态（但 run 已放行，动画照常播放）。
        /// </summary>
        private static bool UiFaderReadPrefix(UIPictureBase __instance,
            ref UIPictureFader.UIP_RES __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = (UIPictureFader.UIP_RES)0;
            return false;
        }

        private static bool UiGasDamagePrefix(UIPicture __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false; // 小骑士模式下诺艾尔不显示毒气贴图特效（捂嘴/咳嗽贴图跟随骑士的来源）
            return false;
        }

        private static bool EpDamagePrefix(EpManager __instance)
        {
            return !IsKnightMode;
        }

        private static void AnimatorColorPostfix(M2PxlAnimatorRT __instance)
        {
            if (!IsKnightMode || !IsNoelAnimator(__instance))
            {
                return;
            }
            __instance.alpha = 0f;
        }

        private static void AnimatorAlphaPostfix(M2PxlAnimatorRT __instance, float value)
        {
            if (!IsKnightMode || value <= 0.01f || !IsNoelAnimator(__instance))
            {
                return;
            }
            __instance.alpha = 0f;
        }

        private static bool IsNoelAnimator(M2PxlAnimatorRT anm)
        {
            try
            {
                return MvField.GetValue(anm) is PR;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// PR.setBounds 之后：骑士模式下把宿主碰撞箱尺寸重新钉回小骑士尺寸。
        /// 游戏在正常/蹲伏/倒地/受击等状态切换时都会用 12×68 像素重建诺艾尔的身体碰撞箱，
        /// 只靠切人时改一次尺寸会被它覆盖（表现为“切成小骑士后碰撞箱还是诺艾尔大小”）。
        /// </summary>
        private static void PrSetBoundsPostfix(PR __instance)
        {
            if (!IsKnightMode || !(__instance is PRNoel noel))
            {
                return;
            }
            KnightInCradleBehaviour.EnforceKnightBodySize(noel);
        }

        /// <summary>
        /// 骑士模式下宿主（诺艾尔）的体型上限，单位：像素（与 M2Mover.Size 的入参同口径）。
        /// 上限 = 小骑士受击箱尺寸 × CLENM（Size 内部就是除以 CLENM 得到 mover 单位）。
        /// </summary>
        private static bool TryGetKnightBodySizePx(PRNoel noel, out float maxW, out float maxH)
        {
            maxW = 0f;
            maxH = 0f;
            try
            {
                Map2d mp = noel != null ? noel.Mp : null;
                if (mp == null)
                {
                    return false;
                }
                float clenm = mp.CLEN * mp.mover_scale;
                if (clenm <= 0.001f)
                {
                    return false;
                }
                KnightInCradleBehaviour.HostSizeCapMoverUnits(mp, out float capW, out float capH);
                maxW = capW * clenm;
                maxH = capH * clenm;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// <c>M2Mover.Size(w, h, ...)</c> 前缀：骑士模式下把宿主的体型限制在“小骑士尺寸”以内。
        ///
        /// 只做**上限**（min 语义），所以：
        /// ① 站着时被夹到约 12×30 像素 —— 受击箱不再有诺艾尔那么高（这正是“碰撞箱=小骑士大小”的目标）；
        /// ② 游戏为了钻矮洞/窄缝把体型压得更小时（CROUCH 40 像素、PRESSCROUCH 10 像素）依然能更小 ——
        ///    所以“诺艾尔能挤过去的窄处，小骑士也能挤过去”。
        ///
        /// 之所以要在源头截住而不是事后改字段：Size() 内部还要按新旧尺寸差**移动本体**
        /// （ALIGNY.BOTTOM 时保持底边不动），事后改字段会让“字段 / 碰撞体 / mbottom”三者错位。
        /// </summary>
        private static void MoverSizePrefix(M2Mover __instance, ref float wmap, ref float hmap)
        {
            if (!IsKnightMode || !KnightInCradlePlugin.ResizeHostToKnight || !(__instance is PRNoel noel))
            {
                return;
            }
            if (!TryGetKnightBodySizePx(noel, out float maxW, out float maxH))
            {
                return;
            }
            if (wmap > maxW)
            {
                wmap = maxW;
            }
            if (hmap > maxH)
            {
                // hmap == -1000 表示“高与宽相同”，比上限小，不会被误夹
                hmap = maxH;
            }
        }

        /// <summary>
        /// <c>M2MvColliderCreatorAtk.recreateExecute</c> 前缀（兜底）：
        /// 重建碰撞体之前把宿主的 sizex/sizey 夹到小骑士尺寸以内，保证**真正参与物理/受击的碰撞体**
        /// 绝不可能是诺艾尔站立体型（即使有别的代码绕过 M2Mover.Size 直接写尺寸）。
        /// 同时写回字段，避免“碰撞体已夹小、但 sizey 还是大值”导致脚底对齐错位。
        /// </summary>
        private static void ColliderRecreatePrefix(M2MvColliderCreatorAtk __instance)
        {
            if (!IsKnightMode || !KnightInCradlePlugin.ResizeHostToKnight ||
                __instance == null || !(__instance.Mv is PRNoel noel))
            {
                return;
            }
            try
            {
                Map2d mp = noel.Mp;
                if (mp == null)
                {
                    return;
                }
                float clenm = mp.CLEN * mp.mover_scale;
                KnightInCradleBehaviour.HostSizeCapMoverUnits(mp, out float capW, out float capH);
                float tx = Mathf.Min(noel.sizex, capW);
                float ty = Mathf.Min(noel.sizey, capH);
                if (Mathf.Abs(noel.sizex - tx) > 0.0001f)
                {
                    noel.sizex = tx;
                }
                if (Mathf.Abs(noel.sizey - ty) > 0.0001f)
                {
                    noel.sizey = ty;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>骑士模式下“小骑士占位框”的半宽（格；sizex 的单位数值与格一致）。</summary>
        private static bool TryGetEventFootprint(PRNoel noel, out float halfW, out float halfH)
        {
            halfW = 0f;
            halfH = 0f;
            if (noel == null)
            {
                return false;
            }
            halfW = Mathf.Min(noel.sizex, KnightEntity.HurtSizeX);
            if (noel.Mp != null)
            {
                KnightInCradleBehaviour.HostSizeCapMoverUnits(noel.Mp, out float capW2, out float capH2);
                halfW = Mathf.Min(halfW, capW2);
                halfH = Mathf.Min(noel.sizey, capH2);
            }
            return halfW > 0.0001f && halfH > 0.0001f;
        }

        /// <summary>
        /// <c>PR.event_sizex</c> 后缀：骑士模式下返回小骑士占位框半宽。
        ///
        /// PR 原版把这三个属性**硬编码**成诺艾尔体型（见 PR.cs:7277~7303）：
        ///   event_sizex = 12 像素 → 0.4286 格；event_sizey = 68 像素 → 2.43 格；
        ///   event_cy   = 脚底 − 68 像素（即占位框中心，底边贴脚底）。
        /// 它们与碰撞箱（CC.Cld，已被限制成小骑士）是**两套数据**：
        /// M2EventContainer 用它们拼“事件矩形”，M2MoverPr.checkCurrentPoint 用 event_cy
        /// 取“当前事件格” —— 门/出口/长椅/升降台/传送/NPC 等与地形/地图的互动判定因此
        /// 仍然按诺艾尔大小算，这正是“受击判定箱已经是小骑士、但和地形互动还是诺艾尔大小”的原因。
        ///
        /// 口径与原版一致：占位框是碰撞箱的 2 倍（原版 12px 半宽对诺艾尔 12px 半宽碰撞箱…… 见下方换算），
        /// 这里同样用 2 × 半宽/半高，保证“谁在哪一格/能不能互动”的判断与小骑士身体匹配。
        /// </summary>
        private static void EventSizexPostfix(PR __instance, ref float __result)
        {
            if (!IsKnightMode || !(__instance is PRNoel noel) || !KnightInCradlePlugin.ResizeHostToKnight)
            {
                return;
            }
            if (TryGetEventFootprint(noel, out float halfW, out _))
            {
                __result = 2f * halfW;
            }
        }

        /// <summary><c>PR.event_sizey</c> 后缀：骑士模式下返回小骑士占位框半高（格）。</summary>
        private static void EventSizeyPostfix(PR __instance, ref float __result)
        {
            if (!IsKnightMode || !(__instance is PRNoel noel) || !KnightInCradlePlugin.ResizeHostToKnight)
            {
                return;
            }
            if (TryGetEventFootprint(noel, out _, out float halfH))
            {
                __result = 2f * halfH;
            }
        }

        /// <summary>
        /// <c>PR.event_cy</c> 后缀：骑士模式下把事件矩形中心压到小骑士身体中心
        /// （= 脚底 − 半高，底边仍贴脚底），而不是诺艾尔的“脚底 − 68 像素”。
        /// </summary>
        private static void EventCyPostfix(PR __instance, ref float __result)
        {
            if (!IsKnightMode || !(__instance is PRNoel noel) || !KnightInCradlePlugin.ResizeHostToKnight)
            {
                return;
            }
            if (TryGetEventFootprint(noel, out _, out float halfH))
            {
                // 与原版同一几何：事件矩形 = event_cy ± event_sizey，而 event_sizey = 2×半高，
                // 所以 event_cy = 脚底 − event_sizey，保证矩形底边贴在脚底。
                __result = noel.mbottom - 2f * halfH;
            }
        }

        private static bool LockInputPrefix(KEY.SIMKEY key, bool default_flag, bool is_or, ref bool __result)
        {
            // 骑士模式只锁“移动/跳跃/攻击”等玩法输入，菜单键与 CHECK（交互）都保持原生：
            // 门 / NPC / 宝箱 / 存档点等交互直接用游戏自带的交互键完成，
            // 模组不再有自己的“交互”键位（坐长椅走骑士自研逻辑，用上/下键）。
            if (!IsKnightMode || (key & AllowedMenuKeys) != 0)
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool ShieldOpeningPrefix(M2PrSkillShieldEvade __instance, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool ShieldEvadeRunStatePrefix(M2PrSkillShieldEvade __instance, bool first, ref float t,
            ref M2MoverPr.PR_MNP manip, ref bool no_progress_main_t, ref bool stop_sink, ref bool __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false;
            return false;
        }

        /// <summary>
        /// 地图快速旅行（选中其他长椅传送）执行后：让骑士退出坐姿并跟随诺艾尔到新长椅，
        /// 否则骑士会钉在旧长椅坐标上（跨图后该坐标无效，表现为消失且无法移动）。
        /// </summary>
        private static void FastTravelPostfix()
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k == null || !k.IsActive)
                {
                    return;
                }
                k.HandleFastTravel();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 地图菜单选择“快速旅行”执行时（EV 脚本开始、黑屏淡入之前）：
        /// 立即隐藏 deco，直到到达目的地再重播淡入。
        /// </summary>
        private static void BenchFastTravelPostfix()
        {
            try
            {
                KnightHudDeco.OnFastTravelStart();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 圣域（sacred 区域，如 sacred_0）：小骑士模式下跳过圣域的
        /// MP 消耗倍率、魔力条破碎（GaugeBrk）、hp_crack 与镜头/画面特效，
        /// 并自动恢复诺艾尔已破碎的魔力条与镜头特效（切成小骑士后下一帧生效）。
        /// </summary>
        private static bool SacredRunPrefix(nel.WholeMapManager __instance, nel.MagicItem Mg, float fcnt)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            try
            {
                // 复位圣域设置的 MP 消耗倍率
                if (__instance.M2D != null && __instance.M2D.pr_mp_consume_ratio != 1f)
                {
                    __instance.M2D.pr_mp_consume_ratio = 1f;
                }
            }
            catch (Exception)
            {
            }
            SuppressSacredPostEffects(Mg);
            RestoreNoelMpGauge();
            return false; // 跳过圣域的伤害/魔力条破碎/hp_crack 逻辑
        }

        /// <summary>小骑士模式下诺艾尔魔力条永不破碎（圣域/MP伤害等任何来源都不再增加破碎）。</summary>
        private static bool GaugeBrkGageDamagePrefix(MpGaugeBreaker __instance, ref int __result)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = 0;
            return false;
        }

        private static bool GaugeBrkCheckPrefix(MpGaugeBreaker __instance, ref bool __result, float mp_dmg)
        {
            if (!IsKnightMode)
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool GaugeBrkApplyDamagePrefix(MpGaugeBreaker __instance)
        {
            return !IsKnightMode;
        }

        /// <summary>
        /// 小骑士模式下创建“被吞噬”镜头拉近特效（圣域 motherstone 的 ZOOM2_EATEN）时立即停用。
        /// 即使圣域魔法钩子因环境差异未生效，镜头也不会被拉近。
        /// </summary>
        private static void SetPePostfix(PostEffect __instance, ref PostEffectItem __result, POSTM postm)
        {
            if (!IsKnightMode || postm != POSTM.ZOOM2_EATEN || __result == null)
            {
                return;
            }
            try
            {
                __result.deactivate(false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把圣域的镜头/画面特效（zoom/burst/jamming/bloom/alpha）强度归零。</summary>
        private static void SuppressSacredPostEffects(nel.MagicItem Mg)
        {
            try
            {
                object other = Mg.Other;
                if (other == null)
                {
                    return;
                }
                Type t = other.GetType();
                string[] names = { "PeBloom", "PeAlpha", "PeBurst", "PeNoise", "PeZm2" };
                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo fi = AccessTools.Field(t, names[i]);
                    if (fi == null)
                    {
                        continue;
                    }
                    if (fi.GetValue(other) is XX.EffectItemBase eib)
                    {
                        eib.x = 0f;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>恢复诺艾尔已破碎的魔力条（MpGaugeBreaker）。</summary>
        private static void RestoreNoelMpGauge()
        {
            try
            {
                PR pr = (M2DBase.Instance as NelM2DBase)?.curMap?.Pr as PR;
                if (pr != null && pr.GaugeBrk != null && pr.GaugeBrk.isActive())
                {
                    pr.GaugeBrk.reset();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 小骑士模式：破坏魔力草只有在该召唤点的“战斗召唤点”界面显示（t_ui&gt;0）时才允许开战。
        /// 小骑士攻击范围较远，远处破坏魔力草不再触发战斗，避免被卡在战斗区域外。
        /// （魔力草仍正常破坏、获得灵魂；下砸/尖啸开战与召唤点长按交互不受影响。）
        /// </summary>
        private static bool DeassignActiveWeedPrefix(M2LpSummon __instance, ref MANA_HIT __result,
            MANA_HIT def_mana_hit, IM2ManaWeedHitable _Weed, AttackInfo AtkHitExecute)
        {
            if (!IsKnightMode || AtkHitExecute == null ||
                !(AtkHitExecute.AttackFrom is PRMain) || AtkHitExecute.AttackFrom != __instance.Mp.Pr)
            {
                return true;
            }
            if (__instance.t_ui <= 0f)
            {
                // 战斗召唤点未显示：不开放战斗，只移除该魔力草
                try
                {
                    if (__instance.AActiveWeed != null)
                    {
                        __instance.AActiveWeed.Remove(_Weed);
                    }
                }
                catch (Exception)
                {
                }
                __result = def_mana_hit;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 小骑士法术命中大炮（M2PuncherCannon）时，把大炮判定为“满充能魔法霰弹”，
        /// 从而为大炮充能并发射（大炮的充能判定依赖 PR.Skill.isShotgun 的输出）。
        /// </summary>
        private static bool IsShotgunPrefix(M2PrSkill __instance, MagicItem Mg,
            out float exploded_shotgun_mp, out float target_magic_full_mp, out bool max_charged,
            ref bool __result)
        {
            if (IsKnightMode && Mg != null &&
                KnightEntity.Instance != null && KnightEntity.Instance.IsKnightSpellMagic(Mg))
            {
                exploded_shotgun_mp = 60f;
                target_magic_full_mp = 60f;
                max_charged = true;
                __result = true;
                return false;
            }
            exploded_shotgun_mp = 0f;
            target_magic_full_mp = 0f;
            max_charged = false;
            return true;
        }

        /// <summary>
        /// 大炮发射后的冷却阶段（CHARGE_AFTER）较长，连续释放小骑士法术时命中会被原版忽略，
        /// 导致大炮长时间不发射。这里在小骑士法术命中且大炮处于冷却时，把状态重置回 WAIT，
        /// 让原版立即重新充能（旋转/充能中则不打断，避免充能永远不完成）。
        /// </summary>
        private static bool CannonApplyHpDamagePrefix(M2PuncherCannon __instance, int val, bool force,
            AttackInfo Atk_, ref int __result)
        {
            if (!IsKnightMode || !(Atk_ is NelAttackInfo na) || na.PublishMagic == null ||
                KnightEntity.Instance == null || !KnightEntity.Instance.IsKnightSpellMagic(na.PublishMagic))
            {
                return true;
            }
            try
            {
                if (CannonSttField != null && (int)CannonSttField.GetValue(__instance) == 3) // CHARGE_AFTER
                {
                    CannonSttField.SetValue(__instance, 0); // WAIT：让原版立即重新充能
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>
        /// 蜘蛛陷阱（MgBsSpiderTrap）射线：小骑士处于下劈状态时屏蔽玩家检测，
        /// 避免下劈期间靠“接触即爆炸”把陷阱提前打掉；其余状态保持原版（接触即破坏）。
        /// </summary>
        private static void SpiderTrapRunPrefix(MgBsSpiderTrap __instance, MagicItem Mg, float fcnt)
        {
            if (!IsKnightMode || Mg == null || Mg.Ray == null ||
                KnightEntity.Instance == null || !KnightEntity.Instance.IsDownSlashing)
            {
                return;
            }
            try
            {
                Mg.Ray.hit_pr = false;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 蜘蛛 BOSS 织网阶段（DANMAKU / DANMAKU_FINAL）：小骑士命中三次
        /// （含多段伤害的每次命中）即触发落地虚弱。计数只在织网阶段累积，
        /// 离开织网阶段自动清零；触发后清掉织网标记并直接切入受击（坠落）状态。
        /// </summary>
        private static void SpiderApplyDamagePrefix(NelNBossSpider __instance, NelAttackInfo Atk,
            ref HITTYPE add_hittype, bool force)
        {
            if (!IsKnightMode || SpiderSpAtkField == null || SpiderChangeStateMethod == null)
            {
                return;
            }
            try
            {
                int flags = (int)SpiderSpAtkField.GetValue(__instance);
                // 织网阶段：DANMAKU=2 / DANMAKU_FINAL=34
                if (flags != 2 && flags != 34)
                {
                    _spiderWebHits = 0;
                    return;
                }
                _spiderWebHits++;
                if (_spiderWebHits >= 3)
                {
                    _spiderWebHits = 0;
                    SpiderSpAtkField.SetValue(__instance, 0); // 清除织网标记
                    SpiderChangeStateMethod.Invoke(__instance,
                        new object[] { NelEnemy.STATE.DAMAGE });
                }
            }
            catch (Exception)
            {
            }
        }

    }
}
