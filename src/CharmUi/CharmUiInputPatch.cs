using System;
using System.Reflection;
using HarmonyLib;
using XX;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符界面打开时屏蔽游戏输入：
    /// 小骑士坐长椅时，AIC 的长椅状态/长椅菜单仍会响应方向键、攻击、菜单键等输入，
    /// 导致小骑士下椅或打开游戏菜单。通过前缀补丁让这些 IN.* 输入在界面打开期间全部返回 false。
    /// </summary>
    public static class CharmUiInputPatch
    {
        private static readonly string[] MethodNames =
        {
            // 移动 / 跳跃 / 冲刺 / 攻击
            "isL", "isR", "isT", "isB", "isLP", "isRP", "isTP", "isBP",
            "isLO", "isRO", "isTO", "isBO", "isJumpPD", "isJumpO",
            "isRunPD", "isEvadePD", "isAtkPD", "isAtkO", "isAtkU",
            // 菜单 / 确认 / 取消 / 界面键
            "isMenuO", "isMenuU", "isMenuPD",
            "isCancel", "isCancelPD", "isCancelOn", "isCancelOrReturnPD",
            "isSubmit", "isSubmitPD", "isSubmitOn", "isSubmitOrMouseUp",
            "isUiRemPD", "isUiAddPD", "isUiRemO", "isUiAddO",
            "isMapPD", "isItmPD",
            // 鼠标
            "isMousePushDown", "isMouseUp", "isMouseOn",
            // 法术
            "isMagicPD", "isMagicNeutralPD", "isMagicLPD", "isMagicRPD",
            "isMagicTPD", "isMagicBPD", "isMagicO", "isMagicLO", "isMagicRO",
            "isMagicTO", "isMagicBO",
        };

        public static void Apply(Harmony harmony)
        {
            int ok = 0;
            int fail = 0;
            foreach (string name in MethodNames)
            {
                try
                {
                    MethodInfo mi = AccessTools.Method(typeof(IN), name);
                    if (mi == null)
                    {
                        fail++;
                        continue;
                    }
                    harmony.Patch(mi, prefix: new HarmonyMethod(
                        typeof(CharmUiInputPatch).GetMethod(nameof(Prefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    ok++;
                }
                catch (Exception)
                {
                    fail++;
                }
            }
        }

        private static bool Prefix(ref bool __result)
        {
            if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
