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

        /// <summary>距离倍率 = 1 + 百分比/100（自绘白色弧带的长度也用这个值）。</summary>
        internal static float LongNailReachMult =>
            LongNailReachPercentConfig != null
                ? 1f + Mathf.Clamp(LongNailReachPercentConfig.Value, 0, 200) / 100f
                : 1.25f;

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
