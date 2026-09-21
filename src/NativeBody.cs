using System;
using System.Reflection;
using evt;
using HarmonyLib;
using m2d;
using nel;
using UnityEngine;

namespace KnightInCradle
{
    /// <summary>
    /// 方案A：原生物理模式（NativeBody）——当前稳定基线。
    ///
    /// 只把“普通贴地走/停”交给诺艾尔的原生 M2MoverPr 执行：
    /// - 每帧在 PR.runPre 之前，把小骑士的方向输入翻译成诺艾尔 simulate_key 的 L/R 位
    ///   （骑士模式下 CombatGuard 已把 L/R/T 原生输入锁死，M2MoverPr 只会读 simulate_key）；
    /// - 诺艾尔物理不暂停，由游戏原生 runPre/runPhysics 完成移动与碰撞，
    ///   从而解决斜坡进门被卡回原房间、墙角穿模等几何问题；
    /// - 帧末由 SyncNoelToKnight 的读回分支把小骑士对齐到诺艾尔。
    ///
    /// 跳跃/滞空/冲刺/超冲/下砸/施法/凝聚/坐椅/攀墙等状态仍走骑士自研物理 +
    /// 旧“暂停 + moveBy”同步（对这些状态 NativeBody 判定 not eligible）。
    ///
    /// 曾试验“跳跃也交给原生”，但引擎在原生跳跃落地后 FootD 不再自动挂地
    /// （hasFoot/canJump 恒 false），且强制挂地会与骑士读回互相拉扯，表现为
    /// “时跳时不跳/地面抽搐”。结论：先保证地面行走原生稳定，跳跃的接管
    /// 需要另做“原生落地接脚”专项后再单独引入。
    /// </summary>
    public static class NativeBody
    {
        // M2MoverPr.simulate_key 位定义（与 m2d.M2MoverPr 反编译一致）
        private const uint SIM_L = 1U;      // 左
        private const uint SIM_R = 2U;      // 右
        private const uint SIM_T = 4U;      // 上/跳跃（保留给将来使用）
        private const uint SIM_B = 8U;      // 下（AIC：按住下 = 蹲伏 / 趴下 / 穿过单向平台）
        private const uint SIM_CHECK = 4194304U; // KEY.SIMKEY.CHECK
        private const uint SimMoveMask = SIM_L | SIM_R | SIM_T | SIM_B;

        private const float NativeWalkSpeed = 0.1f; // 与 KnightEntity.WalkSpeed 一致

        private static FieldInfo _simKeyField;
        private static FieldInfo _walkSpeedField;
        private static FieldInfo _tForceCrouchField;
        private static float _origWalkSpeed = float.NaN;
        private static bool _engaged;
        private static bool _engageLogged;

        public static bool Enabled =>
            KnightInCradlePlugin.NativeBodyConfig != null &&
            KnightInCradlePlugin.NativeBodyConfig.Value;

        /// <summary>配置值本体（供启动日志汇报，Enabled 会为 null 配置兜底）。</summary>
        public static bool ConfigValue =>
            KnightInCradlePlugin.NativeBodyConfig == null || KnightInCradlePlugin.NativeBodyConfig.Value;

        private static FieldInfo SimKeyField =>
            _simKeyField ?? (_simKeyField = AccessTools.Field(typeof(M2MoverPr), "simulate_key"));

        private static FieldInfo WalkSpeedField =>
            _walkSpeedField ?? (_walkSpeedField = AccessTools.Field(typeof(M2MoverPr), "walkSpeed"));

        private static FieldInfo TForceCrouchField =>
            _tForceCrouchField ?? (_tForceCrouchField = AccessTools.Field(typeof(M2MoverPr), "t_force_crouch"));

        /// <summary>
        /// 【关键修复 2026-09-17】让宿主（诺艾尔）持续保持蹲伏。
        ///
        /// 背景：AIC 只在少数事件（`setTo` / 受伤 / 换姿势 / 换格…）才会重算“这个格子能不能站起来”
        /// （`M2MoverPr.forceCrouch` 里的 `recheck_force_crouch`）。玩家**走进**矮洞/窄缝时
        /// 往往根本不会触发重算，于是宿主一直保持站立（碰撞箱 12×68 像素），
        /// 表现就是“诺艾尔趴下能过的地形，小骑士过不去”。
        ///
        /// 做法（每帧，runPre 内、早于游戏自己的 forceCrouch）：
        ///   ① `pr.recheckForceCrouch()` —— 让游戏用它的原版算法重算一次蹲伏需求，
        ///      包含 `PressCheck` 的“趴下/压扁”（PRESSCROUCH）判定；
        ///   ② 把 `t_force_crouch` 顶到 60（游戏自己的“强制蹲伏计时”字段），
        ///      保证在开阔处也不会马上站起来。小骑士比诺艾尔矮得多，保持蹲伏只会更容易通过，
        ///      代价是行走按 AIC 的蹲伏倍率略降（`PR` 的 crouch 速度倍率）。
        ///
        /// 注意：这**不会**阻止更矮的 DOWN / PRESSCROUCH 状态 —— 那两个由游戏自己设置，
        /// `forceCrouch` 只在 `bounds_ == NORMAL` 时才改尺寸。
        /// </summary>
        private static void KeepHostCrouched(PRNoel pr)
        {
            try
            {
                if (!KnightInCradlePlugin.ForceHostCrouch || pr == null)
                {
                    return;
                }
                pr.recheckForceCrouch();
                FieldInfo fc = TForceCrouchField;
                if (fc != null)
                {
                    fc.SetValue(pr, 60f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>注册 Harmony 补丁：PR.runPre 前缀（喂 simulate_key）。</summary>
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo runPre = AccessTools.Method(typeof(PR), "runPre", Type.EmptyTypes);
                if (runPre != null)
                {
                    harmony.Patch(runPre, prefix: new HarmonyMethod(
                        typeof(NativeBody).GetMethod(nameof(PrRunPrePrefix),
                            BindingFlags.Static | BindingFlags.Public)));
                    // 【关键】后缀：runPre 里已经由 autoCheckBounds / forceCrouch 写完尺寸，
                    // 而物理（runPhysics）在这一步之后才跑 —— 在这里把宿主尺寸夹到小骑士尺寸，
                    // 才能让**地形判定真正用到小骑士的碰撞箱**。
                    harmony.Patch(runPre, postfix: new HarmonyMethod(
                        typeof(NativeBody).GetMethod(nameof(PrRunPrePostfix),
                            BindingFlags.Static | BindingFlags.Public)));
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// PR.runPre 后缀：把宿主（诺艾尔）的碰撞箱夹到小骑士尺寸，**在物理之前**。
        ///
        /// 这是整个“地形判定仍按诺艾尔”问题的关键一环：
        /// 游戏一帧的顺序是 `PR.runPre`（autoCheckBounds / forceCrouch 写尺寸）
        /// → `runPhysics`（真正做地形碰撞、用当前 Collider 形状）。
        /// 之前所有夹紧（Behaviour 的 Update/LateUpdate、Size 前缀、setBounds 后缀）都在这一帧
        /// 之外，物理拿到的始终是游戏刚写回的诺艾尔站立尺寸（12×68 像素）——
        /// 所以“受击箱看起来是小骑士（我们自己画的框），但地图判定仍是诺艾尔”。
        /// </summary>
        public static void PrRunPrePostfix(PR __instance)
        {
            try
            {
                if (!KnightInCradlePlugin.KnightModeActive ||
                    !KnightInCradlePlugin.ResizeHostToKnight ||
                    !(__instance is PRNoel pr))
                {
                    return;
                }
                KnightInCradleBehaviour.EnforceKnightBodySize(pr);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 当前帧是否处于原生接管状态（供 Behaviour 决定“读回”还是“旧拖拽同步”）。
        /// 与 PR.runPre 前缀里的判定保持一致。
        /// </summary>
        public static bool IsNowEligible(PRNoel pr, KnightEntity k)
        {
            if (!Enabled || pr == null || k == null || !k.IsActive ||
                !KnightInCradlePlugin.KnightModeActive)
            {
                return false;
            }
            try
            {
                if (EV.isActive(false))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            bool downHeld = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            bool jumpPressed = KeyConfig.GetPressed(KnightInCradlePlugin.JumpKey, KeyCode.W);
            // 跳跃按下即让出给骑士旧路径（跳跃本体由骑士物理执行，原生只负责地面行走）
            if (jumpPressed)
            {
                return false;
            }
            // 【2026-09-17】按住“下”时不再让出给旧拖拽同步：
            // AIC 里“趴下/蹲着钻过矮洞窄缝”就是按住下（simulate_key 的 B 位）触发的，
            // 之前这里一见下键就把宿主体交给骑士手写物理（没有蹲伏/趴下），
            // 结果就是“诺艾尔趴下能过的地形，小骑士过不去”。现在下键也走原生：
            // 位置仍由诺艾尔原生物理执行，下键通过 SIM_B 传下去。
            return k.IsNativeLocomotionEligible(SafeHasFoot(pr)) || (downHeld && k.Grounded);
        }

        /// <summary>
        /// Harmony 前缀：在诺艾尔 runPre（输入/状态解析）前，把小骑士的地面方向
        /// 翻译成 simulate_key。原生物理随后在同一帧 runPhysics 中带碰撞执行。
        /// </summary>
        public static void PrRunPrePrefix(PR __instance)
        {
            try
            {
                if (!Enabled || !KnightInCradlePlugin.KnightModeActive ||
                    !(__instance is PRNoel pr))
                {
                    return;
                }
                KnightEntity k = KnightEntity.Instance;
                if (k == null || !k.IsActive || pr.Mp == null)
                {
                    return;
                }
                // 剧情/转房事件期间由 AIC 脚本自行写 simulate_key（PR_KEY_SIMULATE 等），
                // 原生物理模式必须让路，绝不覆盖。
                try
                {
                    if (EV.isActive(false))
                    {
                        EnsureDisengaged(pr);
                        return;
                    }
                }
                catch (Exception)
                {
                    EnsureDisengaged(pr);
                    return;
                }

                bool foot = SafeHasFoot(pr);
                bool downHeld = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
                bool jumpPressed = KeyConfig.GetPressed(KnightInCradlePlugin.JumpKey, KeyCode.W);
                // 【关键修复 2026-09-17】持续强制宿主蹲伏（AIC 只在少数事件才重算“能否站立”，
                // 走进矮洞时往往不重算 → 宿主一直站着 12×68，小骑士过不去“诺艾尔趴下能过”的地形）
                if (!k.IsSitting && !k.IsDead && !k.IsRepositioning && k.Grounded)
                {
                    KeepHostCrouched(pr);
                }
                // 与 IsNowEligible 保持一致：按住下且在地面时也让原生接管（用于蹲伏/趴下钻行）
                bool eligible = (k.IsNativeLocomotionEligible(foot) || (downHeld && k.Grounded)) &&
                                !jumpPressed;

                FieldInfo sim = SimKeyField;
                if (sim == null)
                {
                    EnsureDisengaged(pr);
                    return;
                }

                uint cur = ReadSimKey(sim, pr);
                if (!eligible)
                {
                    // 非地面行走状态（跳跃/冲刺/施法/事件等）：清移动位并让旧同步接管
                    EnsureDisengaged(pr);
                    if ((cur & SimMoveMask) != 0U)
                    {
                        sim.SetValue(pr, cur & ~SimMoveMask);
                    }
                    return;
                }

                // 原生接管：确保物理在跑（内部会做一次“贴地 + 主动挂脚”）
                Engage(pr);
                foot = SafeHasFoot(pr);
                M2Phys phy = SafeGetPhysic(pr);
                if (phy != null && phy.isPausing())
                {
                    phy.Resume();
                }

                uint flags = cur & SIM_CHECK; // 保留同帧的 CHECK 交互位
                bool left = KeyConfig.GetHeld(KnightInCradlePlugin.MoveLeftKey, KeyCode.A);
                bool right = KeyConfig.GetHeld(KnightInCradlePlugin.MoveRightKey, KeyCode.D);
                if (left && !right)
                {
                    flags |= SIM_L;
                }
                else if (right && !left)
                {
                    flags |= SIM_R;
                }
                // 下键：AIC 用它进入蹲伏（CROUCH）/趴下（PRESSCROUCH），
                // 这是钻过矮洞、窄缝的唯一手段，必须转发给宿主。
                if (downHeld && k.Grounded)
                {
                    flags |= SIM_B;
                }
                sim.SetValue(pr, flags);
                pr.need_check_event = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>退出骑士模式 / 原生模式关闭时强制还原（切回诺艾尔时调用）。</summary>
        public static void ForceDisengage(PRNoel pr)
        {
            try
            {
                EnsureDisengaged(pr);
                _engageLogged = false;
                FieldInfo sim = SimKeyField;
                if (sim != null && pr != null)
                {
                    uint cur = ReadSimKey(sim, pr);
                    if ((cur & SimMoveMask) != 0U)
                    {
                        sim.SetValue(pr, cur & ~SimMoveMask);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void Engage(PRNoel pr)
        {
            if (_engaged)
            {
                return;
            }
            _engaged = true;
            if (!_engageLogged)
            {
                _engageLogged = true;
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[NativeBody] 原生地面移动接管 ON（贴地行走由诺艾尔 M2MoverPr 物理执行）");
            }
            try
            {
                FieldInfo f = WalkSpeedField;
                if (f != null && float.IsNaN(_origWalkSpeed))
                {
                    _origWalkSpeed = (float)f.GetValue(pr);
                }
                if (f != null && !float.IsNaN(_origWalkSpeed))
                {
                    f.SetValue(pr, NativeWalkSpeed); // 骑士走速 0.1 格/帧@60
                }
            }
            catch (Exception)
            {
            }
            // 接管瞬间做一次“贴地 + 主动挂脚”：原生体之前在旧路径（暂停+moveBy）
            // 中可能没有挂地（hasFoot=false），直接跑会导致贴地行走异常。
            // 注意：不要调用 FootD.initJump（它会把 t_foot 置负=强制离地）。
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k != null && k.IsActive)
                {
                    float tx = k.X + KnightEntity.HurtCenterX;
                    float ty = k.FootY - pr.sizey;
                    if (Mathf.Abs(tx - pr.x) > 0.001f || Mathf.Abs(ty - pr.y) > 0.001f)
                    {
                        pr.setTo(tx, ty);
                    }
                    // 微抬 0.04 格，让脚下 BCC 能被 foot 查询命中
                    pr.moveBy(0f, -0.04f, false);
                }
                M2Phys phy = SafeGetPhysic(pr);
                if (phy != null && phy.isPausing())
                {
                    phy.Resume();
                }
                if (phy != null)
                {
                    phy.recheckFoot(0.2f);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void EnsureDisengaged(PRNoel pr)
        {
            if (!_engaged)
            {
                return;
            }
            _engaged = false;
            try
            {
                FieldInfo f = WalkSpeedField;
                if (f != null && pr != null && !float.IsNaN(_origWalkSpeed))
                {
                    f.SetValue(pr, _origWalkSpeed);
                }
            }
            catch (Exception)
            {
            }
            _origWalkSpeed = float.NaN;
        }

        private static bool SafeHasFoot(PRNoel pr)
        {
            try
            {
                return pr.hasFoot();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static M2Phys SafeGetPhysic(PRNoel pr)
        {
            try
            {
                return pr.getPhysic();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static uint ReadSimKey(FieldInfo f, PRNoel pr)
        {
            try
            {
                object v = f.GetValue(pr);
                if (v is uint u)
                {
                    return u;
                }
                if (v is int i)
                {
                    return (uint)i;
                }
            }
            catch (Exception)
            {
            }
            return 0U;
        }

    }
}
