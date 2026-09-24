using System;
using System.Collections.Generic;
using nel;
using UnityEngine;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    /// <summary>
    /// 诺艾尔**姿势浏览器**（开发用调试工具，护符33 效果2 挑素材时用）。
    ///
    /// 为什么需要它：诺艾尔的动画名（`PxlPose.title`）只存在于运行时容器 `MTR.PConNoelAnim` 里，
    /// pxl 资源本身是压缩的（`.pxls.dat` 读不出名字），所以"用外部软件看全部素材"成本很高；
    /// 直接在游戏里把它们**逐个摆出来**最省事：
    ///
    /// - `F8` / `F7`：下一个 / 上一个姿势（屏幕左上角显示 `序号/总数 名称`，同时写进 BepInEx 日志便于复制）；
    /// - `F9`：关闭（恢复游戏自己的姿势）；
    /// - 浏览期间，游戏每帧请求的姿势都会被**改写**成当前浏览的姿势（挂 `PrAnimator.setPose` 前缀），
    ///   所以不会被待机/移动动画覆盖——和护符33 的伪咏唱用的是同一套改写机制。
    ///
    /// 姿势名可在 `BepInEx/config/dev.KnightInCradle.cfg` 的 `Charm33` 段改键位。
    /// </summary>
    public static class NoelPoseBrowser
    {
        /// <summary>是否正在浏览（此时由本工具接管姿势）。</summary>
        public static bool Active { get; private set; }

        private static List<string> _titles;
        private static int _index = -1;
        /// <summary>`PrPoseContainer.getWholePoseInfoObject()`（返回 Better.BDic，用反射拿）。</summary>
        private static System.Reflection.MethodInfo _wholePoseField;

        /// <summary>当前浏览的姿势名（未浏览时为 null）。</summary>
        public static string CurrentTitle
        {
            get
            {
                if (!Active || _titles == null || _index < 0 || _index >= _titles.Count)
                {
                    return null;
                }
                return _titles[_index];
            }
        }

        /// <summary>屏幕左上角显示的文本（HUD 用；未浏览时为 null）。</summary>
        public static string HudText
        {
            get
            {
                string cur = CurrentTitle;
                if (cur == null)
                {
                    return null;
                }
                return "姿势浏览器 " + (_index + 1) + "/" + _titles.Count + "  " + cur +
                       "\nF7 上一个 / F8 下一个 / F9 关闭";
            }
        }

        /// <summary>每帧调用（诺艾尔模式）：处理键位，并保证浏览中的姿势确实被摆上。</summary>
        public static void Tick(PRNoel pr)
        {
            try
            {
                if (CharmEffects.IsKnightMode)
                {
                    Active = false;
                    return;
                }
                bool next = KeyConfig.GetPressed(KnightInCradlePlugin.PoseBrowserNextKeyConfig, KeyCode.F8);
                bool prev = KeyConfig.GetPressed(KnightInCradlePlugin.PoseBrowserPrevKeyConfig, KeyCode.F7);
                bool off = KeyConfig.GetPressed(KnightInCradlePlugin.PoseBrowserOffKeyConfig, KeyCode.F9);
                if (off && Active)
                {
                    Active = false;
                    KnightInCradlePlugin.PluginLog?.LogInfo("[KIC][姿势] 浏览结束");
                    return;
                }
                if (!next && !prev)
                {
                    return;
                }
                EnsureList();
                if (_titles == null || _titles.Count == 0)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][姿势] 取不到诺艾尔姿势表（MTR.PConNoelAnim 为空）");
                    return;
                }
                if (!Active)
                {
                    Active = true;
                    _index = 0;
                }
                else
                {
                    _index += next ? 1 : -1;
                }
                if (_index < 0)
                {
                    _index = _titles.Count - 1;
                }
                else if (_index >= _titles.Count)
                {
                    _index = 0;
                }
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[KIC][姿势] " + (_index + 1) + "/" + _titles.Count + " " + _titles[_index]);
            }
            catch (Exception)
            {
            }
            // 摆姿势：即使游戏这一帧没有再请求姿势，切到新姿势也能立刻看到
            try
            {
                string cur = CurrentTitle;
                if (cur != null && pr != null && pr.is_alive)
                {
                    pr.SpSetPose(cur, -1, null, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 供 `PrAnimator.setPose` 前缀调用：浏览中就把这次请求改写成浏览的姿势。
        /// 返回 true = 已接管（调用方直接 return）。
        /// </summary>
        public static bool TryOverride(ref string title)
        {
            string cur = CurrentTitle;
            if (cur == null)
            {
                return false;
            }
            title = cur;
            return true;
        }

        /// <summary>是否正在浏览（供 HUD / 前缀快速判断）。</summary>
        public static bool IsActive => Active && CurrentTitle != null;

        private static void EnsureList()
        {
            if (_titles != null)
            {
                return;
            }
            try
            {
                _titles = new List<string>();
                // `getWholePoseInfoObject()` 是 Better.BDic<string, PoseInfo>：用非泛型 IDictionary
                // 枚举，这样不必引用 better.dll（与护符2 枚举掉落物同一做法）。
                // 反射调用：`getWholePoseInfoObject()` 的返回类型是 Better.BDic（外部程序集），
                // 直接写出来就得引用 better.dll；走反射则不必。
                if (_wholePoseField == null)
                {
                    _wholePoseField = HarmonyLib.AccessTools.Method(typeof(PrPoseContainer),
                        "getWholePoseInfoObject", Type.EmptyTypes);
                }
                object raw = MTR.PConNoelAnim != null && _wholePoseField != null
                    ? _wholePoseField.Invoke(MTR.PConNoelAnim, null)
                    : null;
                if (!(raw is System.Collections.IDictionary dict))
                {
                    return;
                }
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    if (kv.Key is string name && !string.IsNullOrEmpty(name))
                    {
                        _titles.Add(name);
                    }
                }
                _titles.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
                _titles = new List<string>();
            }
        }
    }
}
