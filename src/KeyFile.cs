using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace KnightInCradle
{
    /// <summary>
    /// 简单键位文件（BepInEx/plugins/KnightInCradle/键位.txt）：
    /// 用记事本即可编辑，每行“能力：键位”，冒号支持中文“：”或英文“:”。
    /// 键位支持中文名（空格 / 鼠标左键 / 左Shift ...）或 Unity KeyCode 名（Space / Mouse0 / LeftShift ...）。
    /// 启动时读取并覆盖对应配置项，保存后重启游戏生效；文件缺失时自动生成默认模板。
    /// </summary>
    public static class KeyFile
    {
        private static readonly Dictionary<string, string> ChineseKey = new Dictionary<string, string>
        {
            { "空格", "Space" },
            { "空格键", "Space" },
            { "回车", "Return" },
            { "回车键", "Return" },
            { "退格", "Backspace" },
            { "退格键", "Backspace" },
            { "tab", "Tab" },
            { "tab键", "Tab" },
            { "esc", "Escape" },
            { "esc键", "Escape" },
            { "鼠标左键", "Mouse0" },
            { "鼠标右键", "Mouse1" },
            { "鼠标中键", "Mouse2" },
            { "左shift", "LeftShift" },
            { "右shift", "RightShift" },
            { "左ctrl", "LeftControl" },
            { "右ctrl", "RightControl" },
            { "左alt", "LeftAlt" },
            { "右alt", "RightAlt" },
            { "上方向键", "UpArrow" },
            { "下方向键", "DownArrow" },
            { "左方向键", "LeftArrow" },
            { "右方向键", "RightArrow" },
            { "上", "UpArrow" },
            { "下", "DownArrow" },
            { "左", "LeftArrow" },
            { "右", "RightArrow" },
            { "句号", "Period" },
            { "句号键", "Period" },
            { "逗号", "Comma" },
            { "逗号键", "Comma" },
            { "斜杠", "Slash" },
            { "斜杠键", "Slash" },
            { "等号", "Equals" },
            { "等号键", "Equals" },
            { "减号", "Minus" },
            { "减号键", "Minus" },
            { "分号", "Semicolon" },
            { "分号键", "Semicolon" },
            { "引号", "Quote" },
            { "引号键", "Quote" },
            { "左括号", "LeftBracket" },
            { "右括号", "RightBracket" },
            { "反斜杠", "Backslash" },
            { "反引号", "BackQuote" },
            { "大写锁定", "CapsLock" }
        };

        /// <summary>默认模板：文件缺失时自动生成（也用于打包发布）。</summary>
        public static readonly string DefaultTemplate =
            "# ============================================\r\n" +
            "#  KnightInCradle小骑士键位设置，请往下翻，调键位在最后\r\n" +
            "# ============================================\r\n" +
            "#  【技能键位】\r\n" +
            "#   回血/聚集：长按 [聚集/施法] \r\n" +
            "#   暗影之魂：点按 [聚集/施法] 或 [快速施法]\r\n" +
            "#   黑暗降临：按住 [下] 的同时点按 [聚集/施法] 或 [快速施法]\r\n" +
            "#   深渊尖啸：按住 [上] 的同时点按 [聚集/施法] 或 [快速施法]\r\n" +
            "#   蓄力劈砍：长按 [攻击]，蓄力完成后松开 [攻击]\r\n" +
            "#   冲刺劈砍：长按 [攻击]，蓄力完成后点按 [冲刺]，在冲刺期间松开 [攻击]\r\n" +
            "#   旋风劈砍：长按 [攻击]，蓄力完成后按住 [上] 或 [下] ，松开 [攻击]，之后连续点按 [攻击] 可延长攻击段数\r\n" +
            "#   水晶升腾：长按 [超级冲刺]，蓄力完成后按住 [上]，松开 [超级冲刺]\r\n" +
            "#   虚空解放：按住 [上]，点按 [梦之钉]\r\n" +
            "#\r\n" +
            "# ============================================\r\n" +
            "# 【功能键位】\r\n" +
            "#   切换角色：点按 [切换角色]，可以在两角色间进行切换\r\n" +
            "#   护符：点按 [护符] 可以查看护符界面，坐在椅子上可以选择装配或卸下护符\r\n" +
            "#   挑衅：当小骑士位于战斗详情界面下时，点按 [挑衅] 可触发战斗\r\n" +
            "#   认真模式：点按 [认真模式]，隐藏左侧诺艾尔的立绘\r\n" +
            "#\r\n" +
            "# ============================================\r\n" +
            "#  【可用键位】\r\n" +
            "#   键盘空格键 = 空格/空格键\r\n" +
            "#   鼠标左键    = 鼠标左键\r\n" +
            "#   鼠标右键    = 鼠标右键\r\n" +
            "#   鼠标中键    = 鼠标中键\r\n" +
            "#   字母键      = A  B  C  ...  Z\r\n" +
            "#   数字键      = 0  1  2  ...  9\r\n" +
            "#   功能键      = F1  F2  ...  F12\r\n" +
            "#   方向键      = 上方向键  下方向键  左方向键  右方向键\r\n" +
            "#   Shift       = 左Shift  右Shift\r\n" +
            "#   Ctrl        = 左Ctrl   右Ctrl\r\n" +
            "#   Alt         = 左Alt    右Alt\r\n" +
            "#   其它        = 回车  Tab  Esc  退格  句号  逗号  斜杠\r\n" +
            "#                等号  减号  分号  引号  左括号  右括号  反斜杠  反引号  大写锁定\r\n" +
            "#   （也可以直接写英文键名：Space  Mouse0  Mouse1  LeftShift  Period  Comma  Slash ...）\r\n" +
            "#\r\n" +
            "# ============================================\r\n" +
            "# 【修改键位】\r\n" +
            "# ============================================\r\n" +
            "上：Q\r\n" +
            "下：鼠标右键\r\n" +
            "左：A\r\n" +
            "右：D\r\n" +
            "跳跃：W\r\n" +
            "\r\n" +
            "攻击：鼠标左键\r\n" +
            "聚集/施法：C\r\n" +
            "快速施法：S\r\n" +
            "梦之钉：空格\r\n" +
            "\r\n" +
            "冲刺：LeftShift\r\n" +
            "超级冲刺：LeftControl\r\n" +
            "\r\n" +
            "切换角色：T\r\n" +
            "护符：O\r\n" +
            "挑衅：V\r\n" +
            "认真模式：Period";

        /// <summary>启动时读取键位文件；文件缺失则先生成默认模板。返回成功应用的行数。</summary>
        public static int Load()
        {
            try
            {
                string path = Path.Combine(Paths.PluginPath, "KnightInCradle", "键位.txt");
                if (!File.Exists(path))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, DefaultTemplate, new UTF8Encoding(true));
                    }
                    catch (Exception)
                    {
                    }
                }
                if (!File.Exists(path))
                {
                    return 0;
                }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int applied = 0;
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    {
                        continue;
                    }
                    int idx = line.IndexOfAny(new[] { ':', '：' });
                    if (idx <= 0)
                    {
                        continue;
                    }
                    string ability = line.Substring(0, idx).Trim().ToLowerInvariant();
                    string keyName = ResolveKey(line.Substring(idx + 1).Trim());
                    if (keyName == null)
                    {
                        continue;
                    }
                    ConfigEntry<string> cfg = FindAbility(ability);
                    if (cfg == null)
                    {
                        continue;
                    }
                    cfg.Value = keyName;
                    applied++;
                }
                return applied;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string ResolveKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            string lower = key.ToLowerInvariant();
            if (ChineseKey.TryGetValue(lower, out string mapped))
            {
                return mapped;
            }
            // 兼容 Unity KeyCode 名（A / Space / Mouse0 / LeftShift / Period ...）
            if (Enum.TryParse(key, true, out KeyCode kc) && kc != KeyCode.None)
            {
                return kc.ToString();
            }
            return null;
        }

        private static ConfigEntry<string> FindAbility(string ability)
        {
            switch (ability)
            {
                case "左": return KnightInCradlePlugin.MoveLeftKey;
                case "右": return KnightInCradlePlugin.MoveRightKey;
                case "跳跃": return KnightInCradlePlugin.JumpKey;
                case "聚集/施法": return KnightInCradlePlugin.FocusKey;
                case "聚集":
                case "凝聚": return KnightInCradlePlugin.FocusKey;
                case "快速施法": return KnightInCradlePlugin.FireballKey;
                case "法术": return KnightInCradlePlugin.FireballKey;
                case "抬头": return KnightInCradlePlugin.LookUpKey;
                case "上": return KnightInCradlePlugin.LookUpKey;
                case "低头": return KnightInCradlePlugin.LookDownKey;
                case "下": return KnightInCradlePlugin.LookDownKey;
                case "冲刺": return KnightInCradlePlugin.DashKey;
                case "攻击": return KnightInCradlePlugin.AttackKey;
                case "梦钉": return KnightInCradlePlugin.DreamNailKey;
                case "梦之钉": return KnightInCradlePlugin.DreamNailKey;
                case "超级冲刺": return KnightInCradlePlugin.SuperDashKey;
                case "切换角色":
                case "切换": return KnightInCradlePlugin.ToggleKey;
                case "认真模式": return KnightInCradlePlugin.SeriousModeKey;
                case "挑衅": return KnightInCradlePlugin.TauntKey;
                case "护符ui":
                case "护符ui键":
                case "护符":
                case "护符界面": return KnightInCradlePlugin.CharmUiKey;
                default: return null;
            }
        }
    }
}
