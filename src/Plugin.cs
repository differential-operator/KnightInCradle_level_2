using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using UnityEngine;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    [BepInPlugin("dev.KnightInCradle", "KnightInCradle", "0.2.0")]
    public class KnightInCradlePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource PluginLog;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<string> SuperDashKey;
        // ---- 键位自定义（玩家可在 cfg 配置文件中修改，默认值与原硬编码一致）----
        internal static ConfigEntry<string> MoveLeftKey;
        internal static ConfigEntry<string> MoveRightKey;
        internal static ConfigEntry<string> JumpKey;
        internal static ConfigEntry<string> FocusKey;
        internal static ConfigEntry<string> FireballKey;
        internal static ConfigEntry<string> DreamNailKey;
        internal static ConfigEntry<string> LookUpKey;
        internal static ConfigEntry<string> LookDownKey;
        internal static ConfigEntry<string> DashKey;
        internal static ConfigEntry<string> AttackKey;
        internal static ConfigEntry<string> SeriousModeKey;
        internal static ConfigEntry<string> CharmUiKey;
        internal static ConfigEntry<string> TauntKey;
        internal static ConfigEntry<bool> NativeBodyConfig;
        internal static ConfigEntry<float> CharmUiScale;
        internal static ConfigEntry<float> CharmUiOffsetX;
        internal static ConfigEntry<float> CharmUiOffsetY;
        internal static ConfigEntry<int> CharmUiColumns;
        internal static bool KnightModeActive;
        internal static ConfigEntry<float> ScaleConfig;
        internal static ConfigEntry<float> OffsetYConfig;
        internal static ConfigEntry<bool> FacingInvertConfig;
        internal static ConfigEntry<bool> SeriousModePersistConfig;
        internal static ConfigEntry<float> AnimSpeedConfig;
        internal static ConfigEntry<float> DashSpeedConfig;
        internal static ConfigEntry<float> DashTimeConfig;
        internal static ConfigEntry<float> FeetAdjustConfig;
        internal static ConfigEntry<float> ShadowRechargeConfig;
        internal static ConfigEntry<float> DashVolumeConfig;
        internal static ConfigEntry<float> ShadowDashVolumeConfig;
        internal static ConfigEntry<bool> ResizeHostConfig;
        internal static ConfigEntry<bool> ForceHostCrouchConfig;
        internal static ConfigEntry<float> HostWidthPixelConfig;
        internal static ConfigEntry<float> HostHeightPixelConfig;
        internal static ConfigEntry<bool> HostPoseOverrideConfig;
        internal static ConfigEntry<float> PvPDamageMulConfig;
        internal static ConfigEntry<float> AttackChainCancelConfig;
        /// <summary>护符18 修长之钉：近战距离加成百分比（默认 20 = +20%；0 = 关闭）。</summary>
        internal static ConfigEntry<int> LongNailReachPercentConfig;
        /// <summary>护符18 弧带：圆周张角（±度）与径向厚度倍率、亮度倍率。</summary>
        internal static ConfigEntry<float> LongNailArcSpanDegConfig;
        internal static ConfigEntry<float> LongNailArcWidthRatioConfig;
        internal static ConfigEntry<float> LongNailArcAlphaConfig;

        internal static float LongNailArcSpanDeg =>
            LongNailArcSpanDegConfig != null ? Mathf.Clamp(LongNailArcSpanDegConfig.Value, 2f, 90f) : 42f;
        /// <summary>剑气长度 = 加成后触及距离 × 该倍率（默认 1）。</summary>
        internal static float LongNailSlashLengthRatio =>
            LongNailArcWidthRatioConfig != null ? Mathf.Clamp(LongNailArcWidthRatioConfig.Value, 0.1f, 3f) : 1f;
        /// <summary>剑气整体渲染大小倍率（宽高等比，默认 1）。</summary>
        internal static float LongNailSlashScale =>
            LongNailSlashScaleConfig != null ? Mathf.Clamp(LongNailSlashScaleConfig.Value, 0.1f, 4f) : 1f;
        internal static ConfigEntry<float> LongNailSlashScaleConfig;
        /// <summary>剑气渲染高度倍率（只改高度，默认 1）。</summary>
        internal static float LongNailSlashHeightRatio =>
            LongNailSlashHeightConfig != null ? Mathf.Clamp(LongNailSlashHeightConfig.Value, 0.1f, 4f) : 1f;
        internal static ConfigEntry<float> LongNailSlashHeightConfig;
        internal static float LongNailArcAlpha =>
            LongNailArcAlphaConfig != null ? Mathf.Clamp(LongNailArcAlphaConfig.Value, 0.05f, 3f) : 1f;

        /// <summary>距离倍率 = 1 + 百分比/100（自绘白色弧带的长度也用这个值）。</summary>
        internal static float LongNailReachMult =>
            LongNailReachPercentConfig != null
                ? 1f + Mathf.Clamp(LongNailReachPercentConfig.Value, 0, 200) / 100f
                : 1.25f;
        /// <summary>护符19 骄傲印记：近战距离加成百分比（默认 35 = +35%），可与修长之钉叠加（百分比相加）。</summary>
        internal static ConfigEntry<int> PrideReachPercentConfig;
        internal static ConfigEntry<float> PrideSlashLengthRatioConfig;
        internal static ConfigEntry<float> PrideSlashScaleConfig;
        internal static ConfigEntry<float> PrideSlashHeightConfig;
        internal static ConfigEntry<float> PrideAlphaConfig;

        internal static float PrideReachPercent =>
            PrideReachPercentConfig != null ? Mathf.Clamp(PrideReachPercentConfig.Value, 0, 200) : 35f;
        internal static float PrideSlashLengthRatio =>
            PrideSlashLengthRatioConfig != null ? Mathf.Clamp(PrideSlashLengthRatioConfig.Value, 0.1f, 3f) : 1f;
        internal static float PrideSlashScale =>
            PrideSlashScaleConfig != null ? Mathf.Clamp(PrideSlashScaleConfig.Value, 0.1f, 4f) : 1f;
        internal static float PrideSlashHeightRatio =>
            PrideSlashHeightConfig != null ? Mathf.Clamp(PrideSlashHeightConfig.Value, 0.1f, 4f) : 1f;
        internal static float PrideAlpha =>
            PrideAlphaConfig != null ? Mathf.Clamp(PrideAlphaConfig.Value, 0.05f, 3f) : 1f;

        // ---- 蓄力剑气贴图：魔法霰弹及其变种（蜕变挽歌 / 修长之钉 / 骄傲印记共用）----
        /// <summary>蓄力释放时是否把剑气贴图换成 `slash_effect_magic`（默认开）。</summary>
        internal static ConfigEntry<bool> MagicSlashOnChargedConfig;
        /// <summary>magic 剑气的整体渲染大小倍率（宽高等比，只影响蓄力时那张图）。</summary>
        internal static ConfigEntry<float> MagicSlashScaleConfig;
        /// <summary>magic 剑气的渲染高度倍率（只改高度）。</summary>
        internal static ConfigEntry<float> MagicSlashHeightConfig;
        /// <summary>护符10 蜕变挽歌：蓄力释放时是否也发射剑气（默认开）。</summary>
        internal static ConfigEntry<bool> ElegyChargedAttackConfig;
        /// <summary>护符10 蜕变挽歌：蓄力释放的剑气命中时是否结算一次"魔法霰弹击中"（默认开）。</summary>
        internal static ConfigEntry<bool> ElegyShotgunOnHitConfig;
        /// <summary>护符10 蜕变挽歌：蓄力释放的剑气伤害 = 魔法霰弹伤害 × 这个比例（默认 0.3）。</summary>
        internal static ConfigEntry<float> ElegyChargedDamageRatioConfig;

        // ---- 护符21 苦痛荆棘（诺艾尔侧）----
        /// <summary>反击伤害倍率：受到伤害 × 这个值（需求：2 倍）。</summary>
        internal static ConfigEntry<float> ThornsDamageMultConfig;
        /// <summary>反击半径（格）。</summary>
        internal static ConfigEntry<float> ThornsRadiusConfig;

        // ---- 护符22 巴尔德之壳（诺艾尔侧：咏唱时展开硬壳）----
        /// <summary>壳贴图整体渲染大小倍率（宽高等比）。</summary>
        internal static ConfigEntry<float> BaldurShellScaleConfig;
        /// <summary>壳渲染宽度倍率（只改宽度）。</summary>
        internal static ConfigEntry<float> BaldurShellWidthConfig;
        /// <summary>壳渲染高度倍率（只改高度）。</summary>
        internal static ConfigEntry<float> BaldurShellHeightConfig;
        /// <summary>壳渲染位置上下微调（格；y 向下为正，负 = 向上）。</summary>
        internal static ConfigEntry<float> BaldurShellOffsetYConfig;
        /// <summary>壳能抵挡的伤害次数上限（需求：3）。</summary>
        internal static ConfigEntry<int> BaldurShellMaxBlocksConfig;
        /// <summary>壳破碎后的恢复时间（秒，需求：10）。</summary>
        internal static ConfigEntry<float> BaldurShellRecoverSecondsConfig;
        /// <summary>壳是否画在诺艾尔图层之后（true = 身后；false = 身前，同小骑士的壳）。</summary>
        internal static ConfigEntry<bool> BaldurShellBehindNoelConfig;

        // ---- 护符23 吸虫之巢（诺艾尔侧）----
        /// <summary>纯白之箭改放几只吸虫（需求：8）。</summary>
        internal static ConfigEntry<int> NestArrowFlukeCountConfig;
        /// <summary>聚能火球改放几只吸虫（需求：14）。</summary>
        internal static ConfigEntry<int> NestFireballFlukeCountConfig;
        /// <summary>每只吸虫的伤害（需求：7 真伤）。</summary>
        internal static ConfigEntry<int> NestFlukeDamageConfig;
        /// <summary>同时佩戴萨满之石时每只吸虫的伤害（需求：9）。</summary>
        internal static ConfigEntry<int> NestFlukeDamageShamanConfig;
        /// <summary>吸虫发射初速度倍率（需求：1.25）。</summary>
        internal static ConfigEntry<float> NestFlukeSpeedMultConfig;
        /// <summary>吸虫命中后再造成一次伤害的概率（需求：50%）。</summary>
        internal static ConfigEntry<float> NestFlukeDoubleHitChanceConfig;

        // ---- 护符25 发光子宫（诺艾尔侧）----
        /// <summary>生成间隔（秒，需求：2）。</summary>
        internal static ConfigEntry<float> UterusSpawnIntervalConfig;
        /// <summary>每只小剑山消耗的 MP（需求：10）。</summary>
        internal static ConfigEntry<int> UterusSpawnMpConfig;
        /// <summary>同时存在的上限。</summary>
        internal static ConfigEntry<int> UterusMaxCountConfig;
        /// <summary>爆炸范围伤害（需求：30）。</summary>
        internal static ConfigEntry<int> UterusExplosionDamageConfig;
        /// <summary>爆炸判定框边长（格）。</summary>
        internal static ConfigEntry<float> UterusExplosionSizeConfig;
        /// <summary>小剑山渲染缩放。</summary>
        internal static ConfigEntry<float> UterusSpikeScaleConfig;
        /// <summary>小剑山循环动画帧率。</summary>
        internal static ConfigEntry<float> UterusSpikeFpsConfig;

        // ---- 护符24 防御者纹章（诺艾尔侧）----
        /// <summary>法阵实心圆半径（格，同小骑士 3）。</summary>
        internal static ConfigEntry<float> ShelterCircleRadiusConfig;
        /// <summary>法阵单次伤害（同小骑士 10）。</summary>
        internal static ConfigEntry<int> ShelterCircleDamageConfig;

        // ---- 护符26 快速聚集（诺艾尔侧：魔法咏唱速度）----
        /// <summary>咏唱速度倍率（需求：+25%）。</summary>
        internal static ConfigEntry<float> FastGatherChantSpeedConfig;

        // ---- 护符27 深度聚集（诺艾尔侧：咏唱时间 +50%、MP 转 HP、下一次伤害 +25%）----
        /// <summary>咏唱时间倍率（需求：1.5 = 咏唱时间 +50%）。</summary>
        internal static ConfigEntry<float> DeepGatherChantTimeConfig;
        /// <summary>蓄力完成后"下一次伤害"的倍率（需求：1.25 = +25%）。</summary>
        internal static ConfigEntry<float> DeepGatherNextDamageConfig;

        // ---- 护符33 锋利之影（诺艾尔侧：效果2 长按护盾键的咏唱姿势 + 金色粒子）----
        /// <summary>长按多久算"长按"（秒，默认 0.25）。</summary>
        internal static ConfigEntry<float> ShadowChantHoldSecondsConfig;
        /// <summary>咏唱姿势名（默认 magic_hold = 诺艾尔咏唱时的持杖姿势）。</summary>
        internal static ConfigEntry<string> ShadowChantPoseConfig;
        /// <summary>每帧生成的粒子数（同小骑士蓄力：2）。</summary>
        internal static ConfigEntry<int> ShadowChantParticlesPerFrameConfig;
        /// <summary>粒子向中心收敛速度倍率（默认 1）。</summary>
        internal static ConfigEntry<float> ShadowChantParticleSpeedScaleConfig;
        /// <summary>粒子颜色（十六进制 RRGGBB，默认 FFF200）。</summary>
        internal static ConfigEntry<string> ShadowChantParticleColorConfig;
        /// <summary>粒子收敛目标相对身体中心的纵向偏移（格；y 向下为正，负值 = 上移）。</summary>
        internal static ConfigEntry<float> ShadowChantCenterOffsetYConfig;
        /// <summary>长按冲刺键多少秒触发"精华"阶段（白闪 + 精华向外扩散），默认 1.0。</summary>
        internal static ConfigEntry<float> ShadowEssenceHoldSecondsConfig;
        /// <summary>冲刺键（留空 = 用 Keybinds/Dash 那个键位），默认空。</summary>
        internal static ConfigEntry<string> ShadowEssenceHoldKeyConfig;
        /// <summary>蓄力完成光圈的额外缩放倍率（乘在"沉重之击"同一套基础上）。</summary>
        internal static ConfigEntry<float> ShadowChargeAuraScaleConfig;
        // ---- 护符33 冲刺段（蓄力完成后松开护盾键）----
        /// <summary>光圈向中心缩小耗时（秒，默认 0.1）。</summary>
        internal static ConfigEntry<float> ShadowDashShrinkSecondsConfig;
        /// <summary>白屏时长（秒，默认 0.07）。</summary>
        internal static ConfigEntry<float> ShadowDashFlashSecondsConfig;
        /// <summary>发射持续（秒，默认 0.5）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstSecondsConfig;
        /// <summary>发射速度（格/秒，默认 8）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstSpeedConfig;
        /// <summary>发射图片缩放倍率（默认 1）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstScaleConfig;
        /// <summary>发射图片名（assets/hk/sprites 下，默认 dash_burst0000）。</summary>
        internal static ConfigEntry<string> ShadowDashBurstSpriteConfig;
        /// <summary>诺艾尔跟随时相对发射图片的"身后"距离（格，默认 0.5）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstBackOffsetConfig;
        /// <summary>发射图片宽度额外倍率（默认 1）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstWidthRatioConfig;
        /// <summary>发射图片高度额外倍率（默认 1）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstHeightRatioConfig;
        /// <summary>发射图片位置偏移：正向 = 前方（格，默认 0）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstOffsetXConfig;
        /// <summary>发射图片位置偏移：y 向下为正（格，默认 0）。</summary>
        internal static ConfigEntry<float> ShadowDashBurstOffsetYConfig;
        /// <summary>冲刺消耗的 MP（默认 100）。</summary>
        internal static ConfigEntry<int> ShadowDashMpCostConfig;
        /// <summary>冲刺伤害倍率（相对"当前轻攻击/魔法霰弹"伤害，默认 3）。</summary>
        internal static ConfigEntry<float> ShadowDashDamageMultConfig;
        /// <summary>冲刺判定箱：宽（格）与高（格）。</summary>
        internal static ConfigEntry<float> ShadowDashHitboxWConfig;
        internal static ConfigEntry<float> ShadowDashHitboxHConfig;
        /// <summary>拿不到攻击包时的兜底基础伤害（默认 15）。</summary>
        internal static ConfigEntry<int> ShadowDashFallbackDamageConfig;
        /// <summary>同时佩戴冲刺大师时：按护盾键即可直接冲刺（无需蓄力），默认开。</summary>
        internal static ConfigEntry<bool> ShadowDashInstantConfig;

        // ---- 护符34 乌恩之形 ----
        /// <summary>蹲下/爬行时每秒回复的生命值（默认 5）。</summary>
        internal static ConfigEntry<float> UnnCrouchHealPerSecondConfig;

        // ---- 护符35 骨钉大师的荣耀 ----
        /// <summary>长按攻击键多少秒完成蓄力（默认 1）。</summary>
        internal static ConfigEntry<float> NailMasterChargeSecondsConfig;
        /// <summary>旋风斩持续时间（秒，默认 2）。</summary>
        internal static ConfigEntry<float> NailMasterSpinSecondsConfig;
        /// <summary>起手动作（attack_air1）播放多久后切到循环（秒，默认 0.25）。</summary>
        internal static ConfigEntry<float> NailMasterSpinIntroSecondsConfig;
        /// <summary>收尾动作（attack_air3）播放时长（秒，默认 0.3）。</summary>
        internal static ConfigEntry<float> NailMasterSpinOutroSecondsConfig;
        /// <summary>旋风斩期间方向键平移速度（格/秒，默认 6）。</summary>
        internal static ConfigEntry<float> NailMasterSpinMoveSpeedConfig;
        /// <summary>三个动作名（默认 attack_air1 / attack_air2 / attack_air3）。</summary>
        internal static ConfigEntry<string> NailMasterSpinPoseIntroConfig;
        internal static ConfigEntry<string> NailMasterSpinPoseLoopConfig;
        internal static ConfigEntry<string> NailMasterSpinPoseOutroConfig;
        /// <summary>攻击键按住不超过这个时长算"点按"（会补发凌空横斩/突进冲击），默认 0.18 秒。</summary>
        internal static ConfigEntry<float> NailMasterTapSecondsConfig;
        /// <summary>佩戴骨钉大师的荣耀时，诺艾尔造成伤害的倍率（默认 5）。</summary>
        internal static ConfigEntry<float> NailMasterDamageMultConfig;
        // ---- 护符35 旋风斩：自绘圆形判定箱 ----
        /// <summary>圆形判定箱半径（格，默认 2）。</summary>
        internal static ConfigEntry<float> NailMasterCircleRadiusConfig;
        /// <summary>圆心相对诺艾尔身体中心的偏移：X 正值 = 前方（格，默认 0）。</summary>
        internal static ConfigEntry<float> NailMasterCircleOffsetXConfig;
        /// <summary>圆心相对诺艾尔身体中心的偏移：Y 正值 = 向下（格，默认 0）。</summary>
        internal static ConfigEntry<float> NailMasterCircleOffsetYConfig;
        /// <summary>在圈内每停留多少秒再吃一次伤害（默认 0.2）。</summary>
        internal static ConfigEntry<float> NailMasterCircleHitSecondsConfig;
        /// <summary>是否画出绿色圆圈（调试用，默认开）。</summary>
        internal static ConfigEntry<bool> NailMasterCircleDebugConfig;

        // ---- 护符36 编织者之歌 ----
        /// <summary>幼虫之歌 + 编织者之歌：小蜘蛛每次命中回复的 MP（默认 3）。</summary>
        internal static ConfigEntry<float> WeaverGrubsongMpConfig;
        /// <summary>小蜘蛛渲染的纵向偏移（格，正值 = 上移；默认 0.4，同小骑士那份）。</summary>
        internal static ConfigEntry<float> WeaverRenderOffsetYConfig;
        /// <summary>小蜘蛛渲染缩放（默认 0.28，同小骑士那份）。</summary>
        internal static ConfigEntry<float> WeaverRenderScaleConfig;

        // ---- 护符20 亡者之怒 ----
        /// <summary>触发基准 HP（默认 30）：被魔物攻击若会把 HP 打到低于该值，则回到该值并触发亡者之怒。</summary>
        internal static ConfigEntry<int> FuryHpThresholdConfig;
        /// <summary>自动"圣光爆发"的免魔/免眩晕窗口（秒，默认 3）。</summary>
        internal static ConfigEntry<float> FuryBurstSecondsConfig;
        /// <summary>亡者之怒期间 HP 流失间隔（秒，默认 2）。</summary>
        internal static ConfigEntry<float> FuryDrainSecondsConfig;
        /// <summary>亡者之怒期间每次流失的 HP（默认 1）。</summary>
        internal static ConfigEntry<int> FuryDrainAmountConfig;
        // ---- 护符20 效果6：亡者之怒期间播放"森之领主虚弱"BGM ----
        /// <summary>是否启用效果6（默认开）。</summary>
        internal static ConfigEntry<bool> FuryBgmEnabledConfig;
        /// <summary>BGM sheet（默认 BGM_battle_nusi = 森之领主的战斗曲）。</summary>
        internal static ConfigEntry<string> FuryBgmSheetConfig;
        /// <summary>BGM cue（默认 BGM_battle_nusi）。</summary>
        internal static ConfigEntry<string> FuryBgmCueConfig;
        /// <summary>跳到哪个块（默认 D = 第一次"虚弱"时原版跳的块；F = 第三次以后）。</summary>
        internal static ConfigEntry<string> FuryBgmBlockConfig;
        /// <summary>块转移表 override 键（默认 mainbattle；F 块配 challenge_1）。</summary>
        internal static ConfigEntry<string> FuryBgmOverrideConfig;
        /// <summary>切进去时的淡出/淡入（毫秒，默认 240 / 0）。</summary>
        internal static ConfigEntry<int> FuryBgmFadeInMsConfig;
        /// <summary>退出时淡回原 BGM 的时间（毫秒，默认 800）。</summary>
        internal static ConfigEntry<int> FuryBgmFadeOutMsConfig;
        // ---- 护符20 效果7：亡者之怒的红色视觉（屏幕红边 + 中心红闪）----
        /// <summary>亡者之怒期间是否显示屏幕四周红色滤镜（默认开，同小骑士那份）。</summary>
        internal static ConfigEntry<bool> FuryVignetteConfig;
        /// <summary>中心红色闪烁的直径（格，默认 3.2，同小骑士那份）。</summary>
        internal static ConfigEntry<float> FuryGlowScaleConfig;
        /// <summary>中心红色闪烁的纵向偏移（格，默认 -0.5 = 向下半格，同小骑士那份）。</summary>
        internal static ConfigEntry<float> FuryGlowOffsetYConfig;
        /// <summary>中心红色闪烁的颜色（RRGGBB，默认 FF4026）。</summary>
        internal static ConfigEntry<string> FuryGlowColorConfig;
        /// <summary>中心红色闪烁的峰值透明度（默认 0.75，同小骑士那份）。</summary>
        internal static ConfigEntry<float> FuryGlowAlphaConfig;

        // ---- 诺艾尔姿势浏览器（开发用调试工具：逐个预览原版姿势/动画名）----
        /// <summary>下一个姿势（默认 F8）。</summary>
        internal static ConfigEntry<string> PoseBrowserNextKeyConfig;
        /// <summary>上一个姿势（默认 F7）。</summary>
        internal static ConfigEntry<string> PoseBrowserPrevKeyConfig;
        /// <summary>关闭浏览（默认 F9）。</summary>
        internal static ConfigEntry<string> PoseBrowserOffKeyConfig;


        internal static bool MagicSlashOnCharged =>
            MagicSlashOnChargedConfig == null || MagicSlashOnChargedConfig.Value;
        internal static float MagicSlashScale =>
            MagicSlashScaleConfig != null ? Mathf.Clamp(MagicSlashScaleConfig.Value, 0.1f, 4f) : 1f;
        internal static float MagicSlashHeightRatio =>
            MagicSlashHeightConfig != null ? Mathf.Clamp(MagicSlashHeightConfig.Value, 0.1f, 4f) : 1f;
        internal static bool ElegyOnChargedAttack =>
            ElegyChargedAttackConfig == null || ElegyChargedAttackConfig.Value;
        internal static bool ElegyShotgunOnHit =>
            ElegyShotgunOnHitConfig == null || ElegyShotgunOnHitConfig.Value;
        internal static float ElegyChargedDamageRatio =>
            ElegyChargedDamageRatioConfig != null
                ? Mathf.Clamp(ElegyChargedDamageRatioConfig.Value, 0.05f, 2f)
                : 0.3f;
        internal static float ThornsDamageMult =>
            ThornsDamageMultConfig != null ? Mathf.Clamp(ThornsDamageMultConfig.Value, 0.1f, 10f) : 2f;
        internal static float ThornsRadius =>
            ThornsRadiusConfig != null ? Mathf.Clamp(ThornsRadiusConfig.Value, 0.5f, 12f) : 3f;
        internal static float ShadowChantHoldSeconds =>
            ShadowChantHoldSecondsConfig != null
                ? Mathf.Clamp(ShadowChantHoldSecondsConfig.Value, 0f, 5f)
                : 0.25f;
        internal static string ShadowChantPose =>
            ShadowChantPoseConfig != null && !string.IsNullOrEmpty(ShadowChantPoseConfig.Value)
                ? ShadowChantPoseConfig.Value
                : "chant";
        internal static int ShadowChantParticlesPerFrame =>
            ShadowChantParticlesPerFrameConfig != null
                ? Mathf.Clamp(ShadowChantParticlesPerFrameConfig.Value, 0, 20)
                : 2;
        internal static float ShadowChantParticleSpeedScale =>
            ShadowChantParticleSpeedScaleConfig != null
                ? Mathf.Clamp(ShadowChantParticleSpeedScaleConfig.Value, 0.1f, 10f)
                : 1f;

        /// <summary>护符33 粒子收敛目标相对身体中心的纵向偏移（负值 = 上移；默认 -1）。</summary>
        internal static float ShadowChantCenterOffsetY =>
            ShadowChantCenterOffsetYConfig != null
                ? Mathf.Clamp(ShadowChantCenterOffsetYConfig.Value, -5f, 5f)
                : -1f;

        internal static float ShadowEssenceHoldSeconds =>
            ShadowEssenceHoldSecondsConfig != null
                ? Mathf.Clamp(ShadowEssenceHoldSecondsConfig.Value, 0f, 10f)
                : 1f;

        /// <summary>冲刺键的 ConfigEntry（留空时回落到 `Keybinds/Dash`）。</summary>
        internal static ConfigEntry<string> ShadowEssenceHoldKey =>
            ShadowEssenceHoldKeyConfig != null && !string.IsNullOrEmpty(ShadowEssenceHoldKeyConfig.Value)
                ? ShadowEssenceHoldKeyConfig
                : DashKey;

        internal static float ShadowChargeAuraScale =>
            ShadowChargeAuraScaleConfig != null
                ? Mathf.Clamp(ShadowChargeAuraScaleConfig.Value, 0.05f, 5f)
                : 1f;

        internal static float ShadowDashShrinkSeconds =>
            ShadowDashShrinkSecondsConfig != null
                ? Mathf.Clamp(ShadowDashShrinkSecondsConfig.Value, 0f, 3f)
                : 0.1f;
        internal static float ShadowDashFlashSeconds =>
            ShadowDashFlashSecondsConfig != null
                ? Mathf.Clamp(ShadowDashFlashSecondsConfig.Value, 0f, 2f)
                : 0.07f;
        internal static float ShadowDashBurstSeconds =>
            ShadowDashBurstSecondsConfig != null
                ? Mathf.Clamp(ShadowDashBurstSecondsConfig.Value, 0.05f, 5f)
                : 0.5f;
        internal static float ShadowDashBurstSpeed =>
            ShadowDashBurstSpeedConfig != null
                ? Mathf.Clamp(ShadowDashBurstSpeedConfig.Value, 0.1f, 60f)
                : 8f;
        internal static float ShadowDashBurstScale =>
            ShadowDashBurstScaleConfig != null
                ? Mathf.Clamp(ShadowDashBurstScaleConfig.Value, 0.05f, 5f)
                : 1f;
        internal static string ShadowDashBurstSprite =>
            ShadowDashBurstSpriteConfig != null && !string.IsNullOrEmpty(ShadowDashBurstSpriteConfig.Value)
                ? ShadowDashBurstSpriteConfig.Value
                : "dash_burst0000";
        internal static float ShadowDashBurstBackOffset =>
            ShadowDashBurstBackOffsetConfig != null
                ? Mathf.Clamp(ShadowDashBurstBackOffsetConfig.Value, 0f, 8f)
                : 0.5f;
        internal static float ShadowDashBurstWidthRatio =>
            ShadowDashBurstWidthRatioConfig != null
                ? Mathf.Clamp(ShadowDashBurstWidthRatioConfig.Value, 0.05f, 10f)
                : 1f;
        internal static float ShadowDashBurstHeightRatio =>
            ShadowDashBurstHeightRatioConfig != null
                ? Mathf.Clamp(ShadowDashBurstHeightRatioConfig.Value, 0.05f, 10f)
                : 1f;
        internal static float ShadowDashBurstOffsetX =>
            ShadowDashBurstOffsetXConfig != null
                ? Mathf.Clamp(ShadowDashBurstOffsetXConfig.Value, -20f, 20f)
                : 0f;
        internal static float ShadowDashBurstOffsetY =>
            ShadowDashBurstOffsetYConfig != null
                ? Mathf.Clamp(ShadowDashBurstOffsetYConfig.Value, -20f, 20f)
                : 0f;
        internal static int ShadowDashMpCost =>
            ShadowDashMpCostConfig != null ? Mathf.Clamp(ShadowDashMpCostConfig.Value, 0, 9999) : 100;
        internal static float ShadowDashDamageMult =>
            ShadowDashDamageMultConfig != null
                ? Mathf.Clamp(ShadowDashDamageMultConfig.Value, 0.1f, 20f)
                : 3f;
        internal static float ShadowDashHitboxW =>
            ShadowDashHitboxWConfig != null
                ? Mathf.Clamp(ShadowDashHitboxWConfig.Value, 0.2f, 20f)
                : 2f;
        internal static float ShadowDashHitboxH =>
            ShadowDashHitboxHConfig != null
                ? Mathf.Clamp(ShadowDashHitboxHConfig.Value, 0.2f, 20f)
                : 2f;
        internal static int ShadowDashFallbackDamage =>
            ShadowDashFallbackDamageConfig != null
                ? Mathf.Clamp(ShadowDashFallbackDamageConfig.Value, 1, 9999)
                : 15;
        internal static bool ShadowDashInstantWithDashmaster =>
            ShadowDashInstantConfig == null || ShadowDashInstantConfig.Value;
        internal static float UnnCrouchHealPerSecond =>
            UnnCrouchHealPerSecondConfig != null
                ? Mathf.Clamp(UnnCrouchHealPerSecondConfig.Value, 0f, 200f)
                : 5f;

        internal static float NailMasterChargeSeconds =>
            NailMasterChargeSecondsConfig != null
                ? Mathf.Clamp(NailMasterChargeSecondsConfig.Value, 0.05f, 10f)
                : 1f;
        internal static float NailMasterSpinSeconds =>
            NailMasterSpinSecondsConfig != null
                ? Mathf.Clamp(NailMasterSpinSecondsConfig.Value, 0.1f, 20f)
                : 2f;
        internal static float NailMasterSpinIntroSeconds =>
            NailMasterSpinIntroSecondsConfig != null
                ? Mathf.Clamp(NailMasterSpinIntroSecondsConfig.Value, 0f, 10f)
                : 0.25f;
        internal static float NailMasterSpinOutroSeconds =>
            NailMasterSpinOutroSecondsConfig != null
                ? Mathf.Clamp(NailMasterSpinOutroSecondsConfig.Value, 0f, 10f)
                : 0.3f;
        internal static float NailMasterSpinMoveSpeed =>
            NailMasterSpinMoveSpeedConfig != null
                ? Mathf.Clamp(NailMasterSpinMoveSpeedConfig.Value, 0f, 40f)
                : 6f;
        internal static string NailMasterSpinPoseIntro => PoseName(NailMasterSpinPoseIntroConfig, "attack_air1");
        internal static string NailMasterSpinPoseLoop => PoseName(NailMasterSpinPoseLoopConfig, "attack_air2");
        internal static string NailMasterSpinPoseOutro => PoseName(NailMasterSpinPoseOutroConfig, "attack_air3");
        internal static float NailMasterTapSeconds =>
            NailMasterTapSecondsConfig != null
                ? Mathf.Clamp(NailMasterTapSecondsConfig.Value, 0.02f, 1f)
                : 0.18f;
        internal static float NailMasterDamageMult =>
            NailMasterDamageMultConfig != null
                ? Mathf.Clamp(NailMasterDamageMultConfig.Value, 0.1f, 50f)
                : 5f;
        internal static float NailMasterCircleRadius =>
            NailMasterCircleRadiusConfig != null
                ? Mathf.Clamp(NailMasterCircleRadiusConfig.Value, 0.1f, 20f)
                : 2f;
        internal static float NailMasterCircleOffsetX =>
            NailMasterCircleOffsetXConfig != null
                ? Mathf.Clamp(NailMasterCircleOffsetXConfig.Value, -20f, 20f)
                : 0f;
        internal static float NailMasterCircleOffsetY =>
            NailMasterCircleOffsetYConfig != null
                ? Mathf.Clamp(NailMasterCircleOffsetYConfig.Value, -20f, 20f)
                : 0f;
        internal static float NailMasterCircleHitSeconds =>
            NailMasterCircleHitSecondsConfig != null
                ? Mathf.Clamp(NailMasterCircleHitSecondsConfig.Value, 0.05f, 5f)
                : 0.2f;
        internal static bool NailMasterCircleDebug =>
            NailMasterCircleDebugConfig == null || NailMasterCircleDebugConfig.Value;
        internal static float WeaverGrubsongMp =>
            WeaverGrubsongMpConfig != null ? Mathf.Clamp(WeaverGrubsongMpConfig.Value, 0f, 99f) : 3f;
        internal static float WeaverRenderOffsetY =>
            WeaverRenderOffsetYConfig != null
                ? Mathf.Clamp(WeaverRenderOffsetYConfig.Value, -3f, 3f)
                : 0.4f;
        internal static float WeaverRenderScale =>
            WeaverRenderScaleConfig != null
                ? Mathf.Clamp(WeaverRenderScaleConfig.Value, 0.05f, 3f)
                : 0.28f;
        internal static int FuryHpThreshold =>
            FuryHpThresholdConfig != null ? Mathf.Clamp(FuryHpThresholdConfig.Value, 1, 9999) : 30;
        internal static float FuryBurstSeconds =>
            FuryBurstSecondsConfig != null
                ? Mathf.Clamp(FuryBurstSecondsConfig.Value, 0.2f, 20f)
                : 3f;
        internal static float FuryDrainSeconds =>
            FuryDrainSecondsConfig != null
                ? Mathf.Clamp(FuryDrainSecondsConfig.Value, 0.1f, 60f)
                : 2f;
        internal static int FuryDrainAmount =>
            FuryDrainAmountConfig != null ? Mathf.Clamp(FuryDrainAmountConfig.Value, 1, 999) : 1;
        internal static bool FuryBgmEnabled => FuryBgmEnabledConfig == null || FuryBgmEnabledConfig.Value;
        internal static string FuryBgmSheet => BgmKey(FuryBgmSheetConfig, "BGM_battle_nusi");
        internal static string FuryBgmCue => BgmKey(FuryBgmCueConfig, "BGM_battle_nusi");
        internal static string FuryBgmBlock => BgmKey(FuryBgmBlockConfig, "D");
        internal static string FuryBgmOverride => FuryBgmOverrideConfig != null ? FuryBgmOverrideConfig.Value : "mainbattle";
        internal static int FuryBgmFadeInMs =>
            FuryBgmFadeInMsConfig != null ? Mathf.Clamp(FuryBgmFadeInMsConfig.Value, 0, 5000) : 240;
        internal static int FuryBgmFadeOutMs =>
            FuryBgmFadeOutMsConfig != null ? Mathf.Clamp(FuryBgmFadeOutMsConfig.Value, 0, 5000) : 800;
        internal static bool FuryVignette => FuryVignetteConfig == null || FuryVignetteConfig.Value;
        internal static float FuryGlowScale =>
            FuryGlowScaleConfig != null ? Mathf.Clamp(FuryGlowScaleConfig.Value, 0.2f, 30f) : 3.2f;
        internal static float FuryGlowOffsetY =>
            FuryGlowOffsetYConfig != null ? Mathf.Clamp(FuryGlowOffsetYConfig.Value, -20f, 20f) : -0.5f;
        internal static float FuryGlowAlpha =>
            FuryGlowAlphaConfig != null ? Mathf.Clamp01(FuryGlowAlphaConfig.Value) : 0.75f;
        /// <summary>护符20 效果7：中心红色闪烁的颜色（十六进制 RRGGBB，默认 FF4026 = 小骑士那份的 (1, 0.25, 0.15)）。</summary>
        internal static Color FuryGlowColor
        {
            get
            {
                string s = FuryGlowColorConfig != null ? FuryGlowColorConfig.Value : null;
                if (!string.IsNullOrEmpty(s))
                {
                    s = s.Trim().TrimStart('#');
                    if (s.Length == 6 &&
                        byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte r) &&
                        byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte g) &&
                        byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte b))
                    {
                        return new Color(r / 255f, g / 255f, b / 255f, 1f);
                    }
                }
                return new Color(1f, 0.25f, 0.15f, 1f);
            }
        }

        private static string BgmKey(ConfigEntry<string> cfg, string fallback)
        {
            return cfg != null && !string.IsNullOrEmpty(cfg.Value) ? cfg.Value : fallback;
        }

        private static string PoseName(ConfigEntry<string> cfg, string fallback)
        {
            return cfg != null && !string.IsNullOrEmpty(cfg.Value) ? cfg.Value : fallback;
        }

        /// <summary>护符33 粒子的 RGB（从十六进制字符串 `RRGGBB` 解析，默认 FFF200）。</summary>
        internal static Color32 ShadowChantParticleColor
        {
            get
            {
                string s = ShadowChantParticleColorConfig != null
                    ? ShadowChantParticleColorConfig.Value
                    : null;
                if (!string.IsNullOrEmpty(s))
                {
                    s = s.Trim().TrimStart('#');
                    if (s.Length == 6 &&
                        byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte r) &&
                        byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte g) &&
                        byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte b))
                    {
                        return new Color32(r, g, b, 255);
                    }
                }
                return new Color32(0xFF, 0xF2, 0x00, 255);
            }
        }
        internal static float BaldurShellScale =>
            BaldurShellScaleConfig != null ? Mathf.Clamp(BaldurShellScaleConfig.Value, 0.05f, 5f) : 1f;
        internal static float BaldurShellWidthRatio =>
            BaldurShellWidthConfig != null ? Mathf.Clamp(BaldurShellWidthConfig.Value, 0.05f, 5f) : 1f;
        internal static float BaldurShellHeightRatio =>
            BaldurShellHeightConfig != null ? Mathf.Clamp(BaldurShellHeightConfig.Value, 0.05f, 5f) : 1f;
        internal static float BaldurShellOffsetY =>
            BaldurShellOffsetYConfig != null ? Mathf.Clamp(BaldurShellOffsetYConfig.Value, -5f, 5f) : 0f;
        internal static int BaldurShellMaxBlocks =>
            BaldurShellMaxBlocksConfig != null ? Mathf.Clamp(BaldurShellMaxBlocksConfig.Value, 1, 20) : 3;
        internal static float BaldurShellRecoverSeconds =>
            BaldurShellRecoverSecondsConfig != null
                ? Mathf.Clamp(BaldurShellRecoverSecondsConfig.Value, 0.1f, 120f)
                : 10f;
        internal static bool BaldurShellBehindNoel =>
            BaldurShellBehindNoelConfig == null || BaldurShellBehindNoelConfig.Value;
        internal static int NestArrowFlukeCount =>
            NestArrowFlukeCountConfig != null ? Mathf.Clamp(NestArrowFlukeCountConfig.Value, 1, 48) : 10;
        internal static int NestFireballFlukeCount =>
            NestFireballFlukeCountConfig != null ? Mathf.Clamp(NestFireballFlukeCountConfig.Value, 1, 48) : 16;
        internal static int NestFlukeDamage =>
            NestFlukeDamageConfig != null ? Mathf.Clamp(NestFlukeDamageConfig.Value, 1, 999) : 7;
        internal static int NestFlukeDamageWithShaman =>
            NestFlukeDamageShamanConfig != null ? Mathf.Clamp(NestFlukeDamageShamanConfig.Value, 1, 999) : 9;
        internal static float NestFlukeSpeedMult =>
            NestFlukeSpeedMultConfig != null ? Mathf.Clamp(NestFlukeSpeedMultConfig.Value, 0.1f, 5f) : 1.25f;
        internal static float NestFlukeDoubleHitChance =>
            NestFlukeDoubleHitChanceConfig != null
                ? Mathf.Clamp(NestFlukeDoubleHitChanceConfig.Value, 0f, 1f)
                : 0.25f;
        internal static float UterusSpawnInterval =>
            UterusSpawnIntervalConfig != null ? Mathf.Clamp(UterusSpawnIntervalConfig.Value, 0.2f, 30f) : 2f;
        internal static int UterusSpawnMp =>
            UterusSpawnMpConfig != null ? Mathf.Clamp(UterusSpawnMpConfig.Value, 0, 999) : 10;
        internal static int UterusMaxCount =>
            UterusMaxCountConfig != null ? Mathf.Clamp(UterusMaxCountConfig.Value, 1, 24) : 4;
        internal static int UterusExplosionDamage =>
            UterusExplosionDamageConfig != null ? Mathf.Clamp(UterusExplosionDamageConfig.Value, 1, 999) : 30;
        internal static float UterusExplosionSize =>
            UterusExplosionSizeConfig != null ? Mathf.Clamp(UterusExplosionSizeConfig.Value, 0.5f, 20f) : 6f;
        internal static float UterusSpikeScale =>
            UterusSpikeScaleConfig != null ? Mathf.Clamp(UterusSpikeScaleConfig.Value, 0.02f, 2f) : 0.11f;
        internal static float UterusSpikeFps =>
            UterusSpikeFpsConfig != null ? Mathf.Clamp(UterusSpikeFpsConfig.Value, 1f, 60f) : 12f;
        internal static float ShelterCircleRadius =>
            ShelterCircleRadiusConfig != null ? Mathf.Clamp(ShelterCircleRadiusConfig.Value, 0.5f, 15f) : 3f;
        internal static int ShelterCircleDamage =>
            ShelterCircleDamageConfig != null ? Mathf.Clamp(ShelterCircleDamageConfig.Value, 1, 999) : 10;
        internal static float FastGatherChantSpeedMult =>
            FastGatherChantSpeedConfig != null ? Mathf.Clamp(FastGatherChantSpeedConfig.Value, 0.1f, 5f) : 1.25f;
        internal static float DeepGatherChantTimeMult =>
            DeepGatherChantTimeConfig != null ? Mathf.Clamp(DeepGatherChantTimeConfig.Value, 0.5f, 5f) : 1.5f;
        internal static float DeepGatherNextDamageMult =>
            DeepGatherNextDamageConfig != null ? Mathf.Clamp(DeepGatherNextDamageConfig.Value, 1f, 5f) : 1.25f;

        /// <summary>
        /// 小骑士攻击“远端玩家（诺艾尔/另一名小骑士）”时，发包前的伤害倍率。
        /// 联机服务器/收包端会对玩家伤害做压缩，这里预乘抵消；默认 2（即假设被打五折）。
        /// 如果实测打玩家掉血比本地伤害少一半以上，把它调大（例如 4）即可。
        /// </summary>
        internal static float PvPDamageMultiplier =>
            PvPDamageMulConfig != null ? PvPDamageMulConfig.Value : 2f;

        /// <summary>
        /// 普攻连击的“后摇取消点”：一刀播到这个比例之后，缓冲住的下一刀可以立刻接上。
        /// **默认 1 = 不取消**：两次攻击的间隔严格等于一整刀（0.4s / 快速劈砍 0.3s）。
        /// 想让连打更快可以调小（0.75 ≈ 省掉定格后摇，间隔变成 0.3s / 0.225s），
        /// 只会改变出刀间隔，不改变单发伤害。
        /// </summary>
        internal static float AttackChainCancelFrac =>
            AttackChainCancelConfig != null
                ? Mathf.Clamp(AttackChainCancelConfig.Value, 0.3f, 1f)
                : 1f;

        /// <summary>
        /// 是否用“小骑士状态”覆盖宿主（诺艾尔）的姿势。默认 **false**（不动她的姿势）。
        /// 宿主是隐藏渲染的，姿势只影响游戏自己的状态机（蹲伏/爬行/趴下/压扁/爬梯…）。
        /// 之前默认覆盖成 stand/walk/jump，会把游戏自己的“趴下钻过矮缝”取消掉
        /// （例如 forest_ahletic_home_thorn 的教程区域），所以默认改为不覆盖。
        /// </summary>
        internal static bool HostPoseOverride =>
            HostPoseOverrideConfig != null && HostPoseOverrideConfig.Value;

        /// <summary>骑士模式下宿主碰撞箱的最大宽度（像素，整宽）。默认 12 = 与诺艾尔原版同宽。</summary>
        internal static float HostWidthPixels =>
            HostWidthPixelConfig != null ? HostWidthPixelConfig.Value : 12f;

        /// <summary>
        /// 骑士模式下宿主碰撞箱的最大高度（像素，整高）。默认 30 ≈ 小骑士身高（1.07 格）。
        /// 某些特别矮的缝隙可以把它调小（例如 20）再试。
        /// </summary>
        internal static float HostHeightPixels =>
            HostHeightPixelConfig != null ? HostHeightPixelConfig.Value : 30f;

        /// <summary>
        /// 骑士模式下是否让宿主（诺艾尔）保持蹲伏（默认开）。
        /// AIC 只在少数事件时才重算“这个格子能不能站起来”，玩家走进矮洞/窄缝时常常不重算，
        /// 宿主会一直站着（碰撞箱 12×68），于是小骑士过不去“诺艾尔趴下能过”的地形。
        /// 开着=每帧重算并保持蹲伏（小骑士比诺艾尔矮，只会更容易通过；代价是按 AIC 的蹲伏倍率走）。
        /// </summary>
        internal static bool ForceHostCrouch =>
            ForceHostCrouchConfig == null || ForceHostCrouchConfig.Value;

        /// <summary>
        /// 骑士模式下是否把宿主（诺艾尔）的碰撞箱限制到小骑士尺寸（默认开）。
        /// 关掉即保持诺艾尔原版体型 —— 用于 A/B 排查“卡在窄处/受击距离不对”这类问题。
        /// </summary>
        internal static bool ResizeHostToKnight => ResizeHostConfig == null || ResizeHostConfig.Value;

        private void Awake()
        {
            PluginLog = Logger;
            ToggleKey = Config.Bind("General", "ToggleKey", "T",
                "切换角色模式的按键（F5 被游戏用作魔法瞄准方向）");
            SuperDashKey = Config.Bind("General", "SuperDashKey", "LeftControl",
                "水晶之心（超级冲刺）按键：按住蓄能，松开发射（LeftControl / RightControl 均可）");
            // 键位自定义（KeyCode 名，例如 A / W / Space / LeftShift；鼠标键用 Mouse0/Mouse1/Mouse2）
            MoveLeftKey = Config.Bind("Keybinds", "MoveLeft", "A", "向左移动");
            MoveRightKey = Config.Bind("Keybinds", "MoveRight", "D", "向右移动");
            JumpKey = Config.Bind("Keybinds", "Jump", "W", "跳跃");
            FocusKey = Config.Bind("Keybinds", "Focus", "C",
                "聚集/施法（合并键）：快速点按=施法（+上=深渊尖啸、+下=黑暗降临），长按=凝聚回血");
            FireballKey = Config.Bind("Keybinds", "Fireball", "S",
                "快速施法（暗影之魂）：点按施法，+上/下释放对应法术");
            DreamNailKey = Config.Bind("Keybinds", "DreamNail", "Space", "梦钉");
            LookUpKey = Config.Bind("Keybinds", "LookUp", "Q", "抬头 / 向上（法术上键）");
            LookDownKey = Config.Bind("Keybinds", "LookDown", "Mouse1", "低头 / 向下（法术下键，默认鼠标右键）");
            DashKey = Config.Bind("Keybinds", "Dash", "LeftShift", "冲刺（LeftShift / RightShift 均可）");
            AttackKey = Config.Bind("Keybinds", "Attack", "Mouse0", "攻击 / 蓄力劈砍（默认鼠标左键）");
            SeriousModeKey = Config.Bind("Keybinds", "SeriousMode", "Period", "认真模式开关（隐藏立绘居中，默认句号键）");
            CharmUiKey = Config.Bind("Keybinds", "CharmUi", "O", "护符 UI 开关（默认 O 键；U 是游戏自带菜单键，避免冲突）");
            TauntKey = Config.Bind("Keybinds", "Taunt", "V", "挑衅（默认 V 键，仅地面可用）");
            // 方案A：骑士普通地面移动（走/停）交给诺艾尔原生物理执行，骑士只做读回。
            // 斜坡/墙角/进门都由 AIC 原生 M2Mover 碰撞求解，解决“过图被卡回原房间”
            // 与“墙角穿模”两类问题；空中动作/冲刺等复杂状态暂保留旧路径。
            NativeBodyConfig = Config.Bind("General", "NativeBodyMode", false,
                "方案A 原生物理模式：普通地面移动由诺艾尔 M2MoverPr 执行。" +
                "false=模组自己的手写物理（行走/跳跃/斜坡手感正常，默认，联机版用的就是这个值）；" +
                "true=原生物理接管，已知会出现「跳跃失灵、走到平台边缘浮空（要在斜面上走一会才恢复）」，" +
                "除非专门排查窄缝/过图问题，否则不要开");
            CharmUiScale = Config.Bind("CharmUi", "Scale", 1f,
                "护符 UI 整体缩放倍率（画面太大调小，太小调大）");
            CharmUiOffsetX = Config.Bind("CharmUi", "OffsetX", 0f,
                "护符 UI 水平偏移（屏幕像素，正值向右）");
            CharmUiOffsetY = Config.Bind("CharmUi", "OffsetY", 0f,
                "护符 UI 垂直偏移（屏幕像素，正值向下）");
            CharmUiColumns = Config.Bind("CharmUi", "Columns", 10,
                "下方护符网格每行列数（用于方向键导航）");
            ScaleConfig = Config.Bind("Visual", "Scale", 0.325f,
                "小骑士显示缩放（1 = 原始大小）");
            SeriousModePersistConfig = Config.Bind("Visual", "SeriousMode", false,
                "认真模式全局开关（隐藏左侧立绘、画面居中）。默认 false=显示；按“。”切换后持久保存，进入游戏/读档保持");
            OffsetYConfig = Config.Bind("Visual", "OffsetY", 0f,
                "小骑士竖直偏移（像素，正值上移，用于脚底对齐）");
            FacingInvertConfig = Config.Bind("Visual", "FacingInvert", false,
                "朝向反向（如果左右脸反了改成 true）");
            AnimSpeedConfig = Config.Bind("Visual", "AnimSpeed", 0.75f,
                "动画播放速度倍率（越小越慢）");
            DashSpeedConfig = Config.Bind("Dash", "DashSpeed", 0.25f,
                "冲刺速度（地图单位/帧）");
            DashTimeConfig = Config.Bind("Dash", "DashTime", 0.25f,
                "冲刺持续时间（秒）");
            ShadowRechargeConfig = Config.Bind("Dash", "ShadowRecharge", 1.5f,
                "Shadow dash recharge seconds (cooldown before next shadow dash)");
            DashVolumeConfig = Config.Bind("Dash", "DashVolume", 0.5f,
                "普通冲刺音效音量（0~1）");
            ShadowDashVolumeConfig = Config.Bind("Dash", "ShadowDashVolume", 1f,
                "暗影冲刺音效音量（0~1）");
            FeetAdjustConfig = Config.Bind("Visual", "FeetAdjust", 0f,
                "脚底位置微调（像素，正值上移）");
            ResizeHostConfig = Config.Bind("General", "ResizeHostToKnight", true,
                "骑士模式下把宿主（诺艾尔）的碰撞箱限制到小骑士尺寸。" +
                "true=受击箱变成小骑士大小（默认）；false=保持诺艾尔原版体型（用于排查卡窄缝/受击距离问题）");
            ForceHostCrouchConfig = Config.Bind("General", "ForceHostCrouch", true,
                "骑士模式下让宿主保持蹲伏（默认 true）。AIC 只在少数事件时才重算“能否站立”，" +
                "走进矮洞时宿主会一直站着导致小骑士过不去；开着=每帧重算并保持蹲伏。" +
                "false=保持原版（只有按住下键时才蹲伏/趴下）");
            HostWidthPixelConfig = Config.Bind("General", "HostColliderWidthPixels", 12f,
                "骑士模式下宿主碰撞箱的最大整宽（像素）。默认 12（与诺艾尔原版同宽）；" +
                "过不去特别窄的缝时可以调小，例如 10 或 8");
            HostHeightPixelConfig = Config.Bind("General", "HostColliderHeightPixels", 30f,
                "骑士模式下宿主碰撞箱的最大整高（像素）。默认 30（≈小骑士身高 1.07 格）；" +
                "过不去特别矮的洞时可以调小，例如 20");
            HostPoseOverrideConfig = Config.Bind("General", "HostPoseOverride", false,
                "是否用“小骑士状态”覆盖宿主（诺艾尔）的姿势。默认 false=交给游戏自己，" +
                "这样蹲伏/爬行/趴下/压扁/爬梯等原版动作都能正常触发（宿主隐藏渲染，姿势无外观影响）。" +
                "true=只在非蹲伏类姿势时把宿主姿势对齐小骑士（旧行为）");
            // 联机：小骑士打“远端玩家”时的伤害补偿倍率（服务器/收包端会压缩玩家伤害）
            PvPDamageMulConfig = Config.Bind("Multiplayer", "PvPDamageMultiplier", 1f,
                "小骑士攻击远端玩家（诺艾尔/另一名小骑士）时发包前的伤害倍率，用于抵消服务器/收包端的压缩。" +
                "默认 2（假设被打五折）；实测掉血明显偏少就调大，例如 4");
            AttackChainCancelConfig = Config.Bind("Combat", "AttackChainCancelPoint", 1f,
                "普攻连击的后摇取消点（占一整刀时长的比例，0.3~1）。一刀播到这个比例后，" +
                "上一刀期间按下的攻击会立刻接上（输入不会丢，只是提前起手）。" +
                "1=不取消（默认）：两次攻击的间隔 = 一整刀 = 0.4 秒（佩戴快速劈砍 0.3 秒）；" +
                "0.75≈挥砍可视帧播完就接刀，间隔缩短为 0.3 / 0.225 秒");
            DashAudio.Init(DashVolumeConfig, ShadowDashVolumeConfig);
            // 护符18 修长之钉：近战距离加成（判定与自绘弧带同一口径）
            LongNailReachPercentConfig = Config.Bind("Charm18", "LongNailReachPercent", 25,
                "修长之钉：诺艾尔近战距离加成百分比（默认 25 = +25%；填 0 = 关闭）。" +
                "判定距离按这个百分比放大，自绘的白色弧带只画'多出来的那一段'。");
            LongNailArcSpanDegConfig = Config.Bind("Charm18", "LongNailArcSpanDeg", 42f,
                "修长之钉弧带的圆周张角（±度，默认 42 = 总张角 84°）。调小 → 弧变短，只留身前一小段。");
            LongNailArcWidthRatioConfig = Config.Bind("Charm18", "LongNailSlashLengthRatio", 1f,
                "修长之钉剑气长度 = 该招加成后触及距离 × 这个倍率（默认 1 = 与判定等长）。" +
                "调大剑气更长、调小更短；剑气用的是 HK 长钉样式贴图。");
            LongNailSlashScaleConfig = Config.Bind("Charm18", "LongNailSlashScale", 1f,
                "修长之钉剑气整体渲染大小倍率（宽高等比，默认 1）。调大整体变大，调小整体变小；" +
                "与 LongNailSlashLengthRatio 相乘。");
            LongNailSlashHeightConfig = Config.Bind("Charm18", "LongNailSlashHeightRatio", 1f,
                "修长之钉剑气渲染高度倍率（只改高度、不改长度，默认 1）。调大剑气更厚/更高，调小更扁。");
            // 护符19 骄傲印记：与修长之钉同一套做法，配置独立
            PrideReachPercentConfig = Config.Bind("Charm19", "PrideReachPercent", 35,
                "骄傲印记：诺艾尔近战距离加成百分比（默认 35 = +35%）。" +
                "与修长之钉同时佩戴时两个百分比相加（25+35=60%）。");
            PrideSlashLengthRatioConfig = Config.Bind("Charm19", "PrideSlashLengthRatio", 1f,
                "骄傲印记剑气长度 = 该招加成后触及距离 × 这个倍率（默认 1 = 与判定等长）。");
            PrideSlashScaleConfig = Config.Bind("Charm19", "PrideSlashScale", 1f,
                "骄傲印记剑气整体渲染大小倍率（宽高等比，默认 1）。");
            PrideSlashHeightConfig = Config.Bind("Charm19", "PrideSlashHeightRatio", 1f,
                "骄傲印记剑气渲染高度倍率（只改高度、不改长度，默认 1）。");
            PrideAlphaConfig = Config.Bind("Charm19", "PrideAlpha", 1f,
                "骄傲印记剑气亮度倍率（默认 1）。");
            LongNailArcAlphaConfig = Config.Bind("Charm18", "LongNailArcAlpha", 1f,
                "修长之钉弧带的亮度倍率（默认 1）。小于 1 更淡，大于 1 更亮。");
            // 蓄力剑气：魔法霰弹及其变种改用 slash_effect_magic 贴图（三个护符共用同一张）
            MagicSlashOnChargedConfig = Config.Bind("MagicSlash", "OnChargedAttack", true,
                "诺艾尔蓄力释放（魔法霰弹及其变种）时，蜕变挽歌/修长之钉/骄傲印记的剑气贴图" +
                "换成 slash_effect_magic（默认开）。关掉则一律用原来的贴图。");
            MagicSlashScaleConfig = Config.Bind("MagicSlash", "Scale", 1f,
                "magic 剑气的整体渲染大小倍率（宽高等比，默认 1；只影响蓄力释放的那张图）。");
            MagicSlashHeightConfig = Config.Bind("MagicSlash", "HeightRatio", 1f,
                "magic 剑气的渲染高度倍率（只改高度、不改长度，默认 1）。");
            ElegyChargedAttackConfig = Config.Bind("Charm10", "ElegyOnChargedAttack", true,
                "蜕变挽歌：蓄力释放（魔法霰弹及其变种）时是否也发射剑气（默认开）。" +
                "关掉则只有不蓄力的轻攻击会发射剑气。");
            ElegyShotgunOnHitConfig = Config.Bind("Charm10", "ElegyShotgunOnHit", true,
                "蜕变挽歌：蓄力释放的剑气命中敌人时，是否补上原版魔法霰弹的击中动画/音效" +
                "并清掉自己的蓄力（默认开）。剑气伤害不受这个开关影响：" +
                "未蓄力=诺艾尔轻攻击的伤害，已蓄力=魔法霰弹的伤害。");
            ElegyChargedDamageRatioConfig = Config.Bind("Charm10", "ElegyChargedDamageRatio", 0.3f,
                "蜕变挽歌：**蓄力释放**的剑气伤害倍率（默认 0.3 = 只造成魔法霰弹伤害的 30%）。" +
                "未蓄力的剑气不受影响（那一路就是轻攻击的伤害）。");
            // 护符21 苦痛荆棘（诺艾尔侧）
            ThornsDamageMultConfig = Config.Bind("Charm21", "ThornsDamageMult", 2f,
                "苦痛荆棘：受到伤害时，对周围敌人造成的伤害 = 这次受到的伤害 × 这个倍率（默认 2）。");
            ThornsRadiusConfig = Config.Bind("Charm21", "ThornsRadius", 3f,
                "苦痛荆棘：反击的圆形半径（格，默认 3）。");
            // 护符22 巴尔德之壳（诺艾尔侧：咏唱时展开硬壳）
            BaldurShellScaleConfig = Config.Bind("Charm22", "ShellScale", 1f,
                "巴尔德之壳：壳贴图的整体渲染大小倍率（宽高等比，默认 1 = 与小骑士的壳同尺寸）。");
            BaldurShellWidthConfig = Config.Bind("Charm22", "ShellWidthRatio", 1f,
                "巴尔德之壳：只改渲染宽度（默认 1）。");
            BaldurShellHeightConfig = Config.Bind("Charm22", "ShellHeightRatio", 1f,
                "巴尔德之壳：只改渲染高度（默认 1）。");
            BaldurShellOffsetYConfig = Config.Bind("Charm22", "ShellOffsetY", 0f,
                "巴尔德之壳：渲染位置上下微调（格；y 向下为正，负 = 向上，默认 0 = 诺艾尔身体中心）。");
            BaldurShellMaxBlocksConfig = Config.Bind("Charm22", "ShellMaxBlocks", 3,
                "巴尔德之壳：最多能抵挡几次伤害（默认 3）。挡满后壳破碎，进入恢复时间。");
            BaldurShellRecoverSecondsConfig = Config.Bind("Charm22", "ShellRecoverSeconds", 10f,
                "巴尔德之壳：破碎后多久才能再次展开（秒，默认 10）。");
            BaldurShellBehindNoelConfig = Config.Bind("Charm22", "ShellBehindNoel", true,
                "巴尔德之壳：壳是否画在**诺艾尔图层之后**（默认 true = 在诺艾尔身后）。" +
                "改成 false 就回到小骑士那种『画在人前』的效果。");
            // 护符23 吸虫之巢（诺艾尔侧）
            NestArrowFlukeCountConfig = Config.Bind("Charm23", "ArrowFlukeCount", 10,
                "吸虫之巢：纯白之箭改放几只吸虫（默认 10）。");
            NestFireballFlukeCountConfig = Config.Bind("Charm23", "FireballFlukeCount", 16,
                "吸虫之巢：聚能火球改放几只吸虫（默认 16）。");
            NestFlukeDamageConfig = Config.Bind("Charm23", "FlukeDamage", 7,
                "吸虫之巢：每只吸虫的伤害（真伤，默认 7）。");
            NestFlukeDamageShamanConfig = Config.Bind("Charm23", "FlukeDamageWithShaman", 9,
                "吸虫之巢：同时佩戴萨满之石时每只吸虫的伤害（默认 9）。");
            NestFlukeSpeedMultConfig = Config.Bind("Charm23", "FlukeSpeedMult", 1.25f,
                "吸虫之巢：吸虫**发射初速度**的倍率（默认 1.25 = 小骑士原速的 1.25 倍）。" +
                "只影响发射瞬间的水平/垂直初速，落地弹跳速度不变。");
            NestFlukeDoubleHitChanceConfig = Config.Bind("Charm23", "FlukeDoubleHitChance", 0.25f,
                "吸虫之巢：吸虫命中敌人后再造成一次同样伤害的概率（默认 0.25 = 25%）。");
            // 护符25 发光子宫（诺艾尔侧）
            UterusSpawnIntervalConfig = Config.Bind("Charm25", "SpawnInterval", 2f,
                "发光子宫：每隔几秒生成一只小剑山（默认 2）。");
            UterusSpawnMpConfig = Config.Bind("Charm25", "SpawnMpCost", 10,
                "发光子宫：每生成一只小剑山消耗的 MP（默认 10）；MP 不足时不生成。");
            UterusMaxCountConfig = Config.Bind("Charm25", "MaxCount", 4,
                "发光子宫：同时存在的小剑山上限（默认 4）。");
            UterusExplosionDamageConfig = Config.Bind("Charm25", "ExplosionDamage", 30,
                "发光子宫：小剑山命中敌人时造成的范围伤害（默认 30，真伤）。");
            UterusExplosionSizeConfig = Config.Bind("Charm25", "ExplosionSize", 6f,
                "发光子宫：范围伤害的判定框边长（格，默认 6 = 以命中点为中心 6×6 格）。");
            UterusSpikeScaleConfig = Config.Bind("Charm25", "SpikeScale", 0.11f,
                "发光子宫：小剑山贴图的渲染缩放（默认 0.11 = 原 0.22 的一半）。");
            UterusSpikeFpsConfig = Config.Bind("Charm25", "SpikeFps", 12f,
                "发光子宫：小剑山循环动画帧率（spike_1~spike_8 循环，默认 12）。");
            // 护符24 防御者纹章（诺艾尔侧）
            ShelterCircleRadiusConfig = Config.Bind("Charm24", "CircleRadius", 3f,
                "防御者纹章：法阵实心圆半径（格，默认 3，同小骑士）。");
            ShelterCircleDamageConfig = Config.Bind("Charm24", "CircleDamage", 10,
                "防御者纹章：法阵单次伤害（默认 10；进入立刻一次，之后每 1 秒一次）。");
            // 护符26 快速聚集（诺艾尔侧）
            FastGatherChantSpeedConfig = Config.Bind("Charm26", "ChantSpeedMult", 1.25f,
                "快速聚集：诺艾尔**魔法咏唱速度**倍率（默认 1.25 = +25%）。" +
                "只加快咏唱/蓄力的推进速度，不改变魔法威力与耗魔总量。");
            // 护符27 深度聚集（诺艾尔侧）
            DeepGatherChantTimeConfig = Config.Bind("Charm27", "ChantTimeMult", 1.5f,
                "深度聚集：诺艾尔**魔法咏唱时间**倍率（默认 1.5 = 咏唱时间 +50%）。" +
                "只改读条时长，不改魔法威力与耗魔（耗魔仍按蓄力量结算）。");
            DeepGatherNextDamageConfig = Config.Bind("Charm27", "NextDamageMult", 1.25f,
                "深度聚集：**蓄力完成后**，下一次造成伤害的倍率（默认 1.25 = +25%）。" +
                "作用于法术、魔法霰弹及其变种；命中一次后即消耗。");
            // 护符33 锋利之影（诺艾尔侧）效果2：长按护盾键 → 咏唱姿势 + 金色粒子
            ShadowChantHoldSecondsConfig = Config.Bind("Charm33", "ChantHoldSeconds", 0.25f,
                "锋利之影：长按**护盾键**多少秒后开始播放咏唱姿势与金色粒子（默认 0.25）。");
            ShadowChantPoseConfig = Config.Bind("Charm33", "ChantPose", "chant",
                "锋利之影：长按护盾键时播放的姿势名（默认 chant）。");
            ShadowChantParticlesPerFrameConfig = Config.Bind("Charm33", "ParticlesPerFrame", 1,
                "锋利之影：每帧生成的圆形粒子数（默认 1）。0 = 不生成粒子。");
            ShadowChantParticleSpeedScaleConfig = Config.Bind("Charm33", "ParticleSpeedScale", 1f,
                "锋利之影：粒子向诺艾尔中心收敛的速度倍率（默认 1）。");
            ShadowChantParticleColorConfig = Config.Bind("Charm33", "ParticleColor", "FFF200",
                "锋利之影：粒子颜色，十六进制 RRGGBB（默认 FFF200）。");
            ShadowChantCenterOffsetYConfig = Config.Bind("Charm33", "ParticleCenterOffsetY", -1f,
                "锋利之影：粒子收敛目标相对诺艾尔身体中心的纵向偏移（格；y 向下为正，" +
                "负值 = 上移。默认 -1 = 中心上方 1 格）。");
            ShadowEssenceHoldSecondsConfig = Config.Bind("Charm33", "EssenceHoldSeconds", 1f,
                "锋利之影：长按**冲刺键**多少秒后触发精华阶段（屏幕四周白闪 + 粒子从中心向外扩散，默认 1）。");
            ShadowEssenceHoldKeyConfig = Config.Bind("Charm33", "EssenceHoldKey", "",
                "锋利之影：触发精华阶段用的键（留空 = 用 Keybinds/Dash 那个键位）。");
            ShadowChargeAuraScaleConfig = Config.Bind("Charm33", "ChargeAuraScale", 1f,
                "锋利之影：蓄力完成后诺艾尔中心那组光圈（同沉重之击）的额外缩放倍率（默认 1）。");
            ShadowDashShrinkSecondsConfig = Config.Bind("Charm33", "DashShrinkSeconds", 0.1f,
                "锋利之影·冲刺：蓄力完成后松开护盾键，光圈向诺艾尔中心缩小的耗时（秒，默认 0.1）。");
            ShadowDashFlashSecondsConfig = Config.Bind("Charm33", "DashFlashSeconds", 0.07f,
                "锋利之影·冲刺：两次白屏各自持续的时间（秒，默认 0.07）。");
            ShadowDashBurstSecondsConfig = Config.Bind("Charm33", "DashBurstSeconds", 0.5f,
                "锋利之影·冲刺：发射图片的持续时间（秒，默认 0.5）。");
            ShadowDashBurstSpeedConfig = Config.Bind("Charm33", "DashBurstSpeed", 8f,
                "锋利之影·冲刺：发射速度（格/秒，默认 8）。");
            ShadowDashBurstScaleConfig = Config.Bind("Charm33", "DashBurstScale", 1f,
                "锋利之影·冲刺：发射图片的缩放倍率（默认 1）。");
            ShadowDashBurstSpriteConfig = Config.Bind("Charm33", "DashBurstSprite", "dash_burst0000",
                "锋利之影·冲刺：发射用的图片名（assets/hk/sprites 下的 png，不带扩展名）。");
            ShadowDashBurstBackOffsetConfig = Config.Bind("Charm33", "DashBurstBackOffset", 0.5f,
                "锋利之影·冲刺：诺艾尔隐藏期间「跟在图片后」的距离（格，默认 0.5）。");
            ShadowDashBurstWidthRatioConfig = Config.Bind("Charm33", "DashBurstWidthRatio", 1f,
                "锋利之影·冲刺：发射图片**宽度**额外倍率（默认 1；与 DashBurstScale 相乘）。");
            ShadowDashBurstHeightRatioConfig = Config.Bind("Charm33", "DashBurstHeightRatio", 1f,
                "锋利之影·冲刺：发射图片**高度**额外倍率（默认 1；与 DashBurstScale 相乘）。");
            ShadowDashBurstOffsetXConfig = Config.Bind("Charm33", "DashBurstOffsetX", 0f,
                "锋利之影·冲刺：发射图片的水平位置偏移（格，正值 = 朝**前方**，默认 0）。");
            ShadowDashBurstOffsetYConfig = Config.Bind("Charm33", "DashBurstOffsetY", 0f,
                "锋利之影·冲刺：发射图片的垂直位置偏移（格，y 向下为正，负值 = 上移，默认 0）。");
            ShadowDashMpCostConfig = Config.Bind("Charm33", "DashMpCost", 100,
                "锋利之影·冲刺：消耗的 MP（默认 100）；MP 不足则不冲刺。");
            ShadowDashDamageMultConfig = Config.Bind("Charm33", "DashDamageMult", 3f,
                "锋利之影·冲刺：伤害倍率 —— 相对**当前轻攻击**（法杖带霰弹附魔时相对**当前魔法霰弹**）的伤害，默认 3。");
            ShadowDashHitboxWConfig = Config.Bind("Charm33", "DashHitboxWidth", 2f,
                "锋利之影·冲刺：沿路径的伤害判定箱宽度（格，默认 2）。");
            ShadowDashHitboxHConfig = Config.Bind("Charm33", "DashHitboxHeight", 2f,
                "锋利之影·冲刺：沿路径的伤害判定箱高度（格，默认 2）。");
            ShadowDashFallbackDamageConfig = Config.Bind("Charm33", "DashFallbackDamage", 15,
                "锋利之影·冲刺：拿不到攻击包数据时的兜底基础伤害（默认 15，会再乘 DashDamageMult）。");
            ShadowDashInstantConfig = Config.Bind("Charm33", "DashInstantWithDashmaster", true,
                "锋利之影·冲刺：**同时佩戴冲刺大师**时，按一下护盾键即可直接冲刺（无需先蓄力）。" +
                "冲刺进行中不会再触发（默认开）。");
            // 护符34 乌恩之形
            UnnCrouchHealPerSecondConfig = Config.Bind("Charm34", "CrouchHealPerSecond", 5f,
                "乌恩之形：蹲下/爬行时每秒回复的生命值（默认 5；0 = 不回血）。");
            // 护符35 骨钉大师的荣耀
            NailMasterChargeSecondsConfig = Config.Bind("Charm35", "ChargeSeconds", 1f,
                "骨钉大师的荣耀：长按攻击键多少秒完成蓄力（默认 1）。");
            NailMasterSpinSecondsConfig = Config.Bind("Charm35", "SpinSeconds", 2f,
                "骨钉大师的荣耀：旋风斩（松开攻击键后）的持续时间（秒，默认 2）。");
            NailMasterSpinIntroSecondsConfig = Config.Bind("Charm35", "SpinIntroSeconds", 0.25f,
                "骨钉大师的荣耀：起手动作播放多久后切到循环动作（秒，默认 0.25）。");
            NailMasterSpinOutroSecondsConfig = Config.Bind("Charm35", "SpinOutroSeconds", 0.3f,
                "骨钉大师的荣耀：收尾动作播放时长（秒，默认 0.3）。");
            NailMasterSpinMoveSpeedConfig = Config.Bind("Charm35", "SpinMoveSpeed", 6f,
                "骨钉大师的荣耀：旋风斩期间用方向键平移的速度（格/秒，默认 6）。");
            NailMasterSpinPoseIntroConfig = Config.Bind("Charm35", "SpinPoseIntro", "attack_air1",
                "骨钉大师的荣耀：起手动作名（默认 attack_air1，原版旋风斩用的名字）。");
            NailMasterSpinPoseLoopConfig = Config.Bind("Charm35", "SpinPoseLoop", "attack_air2",
                "骨钉大师的荣耀：循环动作名（默认 attack_air2）。");
            NailMasterSpinPoseOutroConfig = Config.Bind("Charm35", "SpinPoseOutro", "attack_air3",
                "骨钉大师的荣耀：收尾动作名（默认 attack_air3）。");
            NailMasterTapSecondsConfig = Config.Bind("Charm35", "TapSeconds", 0.18f,
                "骨钉大师的荣耀：攻击键按住不超过这个秒数算「点按」——点按时会**补发**凌空横斩/突进冲击，" +
                "超过则视为长按（走蓄力）。默认 0.18。");
            NailMasterDamageMultConfig = Config.Bind("Charm35", "DamageMult", 5f,
                "骨钉大师的荣耀：佩戴时诺艾尔造成伤害的倍率（默认 5）。" +
                "因为该护符屏蔽了魔法键，此时她的伤害都是无附魔的。");
            NailMasterCircleRadiusConfig = Config.Bind("Charm35", "SpinCircleRadius", 2f,
                "骨钉大师的荣耀·旋风斩：自绘圆形判定箱的半径（格，默认 2）。");
            NailMasterCircleOffsetXConfig = Config.Bind("Charm35", "SpinCircleOffsetX", 0f,
                "骨钉大师的荣耀·旋风斩：圆心相对诺艾尔身体中心的水平偏移（格，正值 = 朝**前方**，默认 0）。");
            NailMasterCircleOffsetYConfig = Config.Bind("Charm35", "SpinCircleOffsetY", 0f,
                "骨钉大师的荣耀·旋风斩：圆心相对诺艾尔身体中心的纵向偏移（格，y 向下为正，负值 = 上移，默认 0）。");
            NailMasterCircleHitSecondsConfig = Config.Bind("Charm35", "SpinCircleHitSeconds", 0.2f,
                "骨钉大师的荣耀·旋风斩：敌人在圈内每停留多少秒再吃一次伤害（默认 0.2）。");
            NailMasterCircleDebugConfig = Config.Bind("Charm35", "SpinCircleDebug", true,
                "骨钉大师的荣耀·旋风斩：是否把圆形判定箱画成绿色圆圈（调试用，默认开）。");
            // 护符36 编织者之歌
            WeaverGrubsongMpConfig = Config.Bind("Charm36", "GrubsongBondMp", 3f,
                "编织者之歌：**同时携带幼虫之歌**时，小蜘蛛每次攻击命中回复的 MP（默认 3）。");
            WeaverRenderOffsetYConfig = Config.Bind("Charm36", "RenderOffsetY", 0.4f,
                "编织者之歌：小蜘蛛渲染的纵向偏移（格，正值 = 上移；默认 0.4，同小骑士那份）。" +
                "如果看到小蜘蛛陷在地面里，把它调大即可。");
            WeaverRenderScaleConfig = Config.Bind("Charm36", "RenderScale", 0.28f,
                "编织者之歌：小蜘蛛渲染缩放（默认 0.28，同小骑士那份）。");
            // 护符20 亡者之怒
            FuryHpThresholdConfig = Config.Bind("Charm20", "HpThreshold", 30,
                "亡者之怒：触发基准 HP（默认 30）。被魔物攻击若会把 HP 打到低于该值，" +
                "则立即回到该值并触发亡者之怒。");
            FuryBurstSecondsConfig = Config.Bind("Charm20", "AutoBurstSeconds", 3f,
                "亡者之怒：触发时自动释放的「圣光爆发」在这段时间内**不消耗魔力、不导致眩晕**（秒，默认 3）。");
            FuryDrainSecondsConfig = Config.Bind("Charm20", "DrainSeconds", 2f,
                "亡者之怒：处于亡者之怒期间 HP 流失的间隔（秒，默认 2 = 每 2 秒掉 1HP，掉到 0 死亡）。");
            FuryDrainAmountConfig = Config.Bind("Charm20", "DrainAmount", 1,
                "亡者之怒：每次流失的 HP 数量（默认 1）。");
            // 效果6：亡者之怒期间播放"森之领主虚弱"那段 BGM
            FuryBgmEnabledConfig = Config.Bind("Charm20", "BgmEnabled", true,
                "亡者之怒：触发后是否播放「森之领主」虚弱阶段的那段 BGM（默认开）。" +
                "只在战斗中播放，战斗结束 / 脱离战斗 / 亡者之怒结束就淡回原来的 BGM。");
            FuryBgmSheetConfig = Config.Bind("Charm20", "BgmSheet", "BGM_battle_nusi",
                "亡者之怒 BGM：sheet 键（StreamingAssets\\BGM_<key>.acb 里的数据，默认 BGM_battle_nusi）。");
            FuryBgmCueConfig = Config.Bind("Charm20", "BgmCue", "BGM_battle_nusi",
                "亡者之怒 BGM：cue 名（默认 BGM_battle_nusi）。");
            FuryBgmBlockConfig = Config.Bind("Charm20", "BgmBlock", "D",
                "亡者之怒 BGM：从哪个块开始（默认 D —— 原版森之领主**第一次**被 burst 打虚弱时跳的块；" +
                "想听第三次以后的那段改成 F）。");
            FuryBgmOverrideConfig = Config.Bind("Charm20", "BgmOverride", "mainbattle",
                "亡者之怒 BGM：块转移 override 键（默认 mainbattle，对应 D 块；用 F 块时改成 challenge_1）。" +
                "留空 = 用默认转移表。");
            FuryBgmFadeInMsConfig = Config.Bind("Charm20", "BgmFadeInMs", 240,
                "亡者之怒 BGM：切进来的淡出时长（毫秒，默认 240）。");
            FuryBgmFadeOutMsConfig = Config.Bind("Charm20", "BgmFadeOutMs", 800,
                "亡者之怒 BGM：退出时淡回原 BGM 的时长（毫秒，默认 800）。");
            // 效果7：亡者之怒的红色视觉
            FuryVignetteConfig = Config.Bind("Charm20", "Vignette", true,
                "亡者之怒：是否显示屏幕四周红色滤镜（默认开，同小骑士那份）。");
            FuryGlowScaleConfig = Config.Bind("Charm20", "GlowScale", 3.2f,
                "亡者之怒：诺艾尔中心红色闪烁的直径（格，默认 3.2，同小骑士那份）。");
            FuryGlowOffsetYConfig = Config.Bind("Charm20", "GlowOffsetY", -0.5f,
                "亡者之怒：中心红色闪烁的纵向偏移（格，正值 = 上移；默认 -0.5 = 向下半格，同小骑士那份）。");
            FuryGlowColorConfig = Config.Bind("Charm20", "GlowColor", "FF4026",
                "亡者之怒：中心红色闪烁的颜色，十六进制 RRGGBB（默认 FF4026）。");
            FuryGlowAlphaConfig = Config.Bind("Charm20", "GlowAlpha", 0.75f,
                "亡者之怒：中心红色闪烁的峰值透明度（0~1，默认 0.75，同小骑士那份）。");
            // 诺艾尔姿势浏览器（调试工具）
            PoseBrowserNextKeyConfig = Config.Bind("PoseBrowser", "NextKey", "F8",
                "姿势浏览器：切到下一个姿势（默认 F8）。浏览时屏幕左上角显示 序号/总数 + 姿势名，日志也会打印。");
            PoseBrowserPrevKeyConfig = Config.Bind("PoseBrowser", "PrevKey", "F7",
                "姿势浏览器：切到上一个姿势（默认 F7）。");
            PoseBrowserOffKeyConfig = Config.Bind("PoseBrowser", "OffKey", "F9",
                "姿势浏览器：关闭浏览、恢复游戏自己的姿势（默认 F9）。");
            // 简单键位文件（BepInEx/plugins/KnightInCradle/键位.txt）：
            // 覆盖上面 Keybinds 分组里的键位，用记事本改完重启游戏生效。
            KeyFile.Load();

            // 游戏会在启动时禁用/销毁 BepInEx 挂在场景里的插件对象，
            // 所以这里创建自己的常驻对象来承载运行逻辑。
            var go = new GameObject("KnightInCradle_Runtime");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<KnightInCradleBehaviour>();
            go.AddComponent<KnightHudDeco>();
        }
    }

    /// <summary>
    /// 键位读取辅助：配置项以字符串保存 KeyCode 名（鼠标键为 Mouse0/Mouse1/Mouse2），
    /// 提供统一的按住/按下/抬起判定。鼠标键走 GetMouseButton，键盘键走 GetKey。
    /// </summary>
    public static class KeyConfig
    {
        /// <summary>护符界面打开期间屏蔽小骑士的全部输入（移动/跳跃/攻击/技能/冲刺）。</summary>
        private static bool IsUiSuppressed()
        {
            return CharmUiController.Instance != null && CharmUiController.Instance.IsOpen;
        }

        public static KeyCode Parse(string name, KeyCode fallback)
        {
            if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, out KeyCode k))
            {
                return k;
            }
            return fallback;
        }

        private static bool IsMouse(KeyCode k)
        {
            return k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2;
        }

        private static int MouseIndex(KeyCode k)
        {
            if (k == KeyCode.Mouse0)
            {
                return 0;
            }
            if (k == KeyCode.Mouse1)
            {
                return 1;
            }
            return 2;
        }

        public static bool GetHeld(ConfigEntry<string> cfg, KeyCode fallback)
        {
            if (IsUiSuppressed())
            {
                return false;
            }
            KeyCode k = cfg != null ? Parse(cfg.Value, fallback) : fallback;
            return IsMouse(k) ? UnityEngine.Input.GetMouseButton(MouseIndex(k)) : UnityEngine.Input.GetKey(k);
        }

        public static bool GetPressed(ConfigEntry<string> cfg, KeyCode fallback)
        {
            if (IsUiSuppressed())
            {
                return false;
            }
            KeyCode k = cfg != null ? Parse(cfg.Value, fallback) : fallback;
            return IsMouse(k) ? UnityEngine.Input.GetMouseButtonDown(MouseIndex(k)) : UnityEngine.Input.GetKeyDown(k);
        }

        public static bool GetReleased(ConfigEntry<string> cfg, KeyCode fallback)
        {
            if (IsUiSuppressed())
            {
                return false;
            }
            KeyCode k = cfg != null ? Parse(cfg.Value, fallback) : fallback;
            return IsMouse(k) ? UnityEngine.Input.GetMouseButtonUp(MouseIndex(k)) : UnityEngine.Input.GetKeyUp(k);
        }
    }
}
