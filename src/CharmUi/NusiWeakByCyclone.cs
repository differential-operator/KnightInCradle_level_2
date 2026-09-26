using System;
using System.Reflection;
using HarmonyLib;
using nel;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 需求 2026-09-27：诺艾尔佩戴护符35「骨钉大师的荣耀」时，**旋风斩**也能像（小骑士的）法术那样
    /// 把森之领主打进虚弱。
    ///
    /// 原版判据在 `NelNBoss_Nusi.isBurstForFaint(NelAttackInfo Atk)`：
    /// `处于"反击窗口" && Atk.PublishMagic.kind == MGKIND.PR_BURST`（`NelNBoss_Nusiusi.cs:1960`）。
    /// 所以这里只给它的**后缀**加一条放行：攻击包是诺艾尔的旋风斩（`MGKIND.PR_WHEEL`）且佩戴护符35 时，
    /// 直接当成"可以打进虚弱" —— 后续流程（进入 STUN、BGM 跳段、burst_counter_success 计数、
    /// 花/触手/掉落结算等）全部沿用原版，不需要我们另写一套。
    /// </summary>
    internal static class NusiWeakByCyclone
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(NelNBoss_Nusi), "isBurstForFaint",
                    new[] { typeof(NelAttackInfo) });
                if (m == null)
                {
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(
                    typeof(NusiWeakByCyclone).GetMethod(nameof(IsBurstForFaintPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex)
            {
                KnightInCradlePlugin.PluginLog?.LogWarning(
                    "[KIC][森之领主] isBurstForFaint 补丁挂载失败：" + ex.Message);
            }
        }

        /// <summary>旋风斩（护符35 在场）也允许打进虚弱。</summary>
        private static void IsBurstForFaintPostfix(NelAttackInfo Atk, ref bool __result)
        {
            try
            {
                if (__result || Atk == null)
                {
                    return;
                }
                if (CharmEffects.IsKnightMode ||
                    !CharmEffects.IsEquipped(CharmOwner.Noel, CharmEffects.NailMasterId))
                {
                    return; // 只对"诺艾尔 + 骨钉大师的荣耀"放行
                }
                MagicItem mg = Atk.PublishMagic;
                if (mg == null || mg.kind != MGKIND.PR_WHEEL)
                {
                    return; // 只看旋风斩
                }
                if (!(mg.Caster is PRNoel) && !(Atk.Caster is PRNoel))
                {
                    return;
                }
                __result = true;
            }
            catch (Exception)
            {
            }
        }
    }
}
