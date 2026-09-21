using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符 UI 音效：从插件素材目录加载 wav（16bit/8bit PCM），
    /// 由 AudioSource 播放。映射（用户确认）：
    /// - 护符间切换（光标移动）→ ui_change_selection
    /// - 选中/确认 → ui_button_confirm
    /// - 装配成功 / 卸下 → charm_click_in
    /// - 过载 → charm_overcharm
    /// </summary>
    public static class CharmAudio
    {
        private static string _dir = "";
        private static string _ggDir = "";
        private static readonly Dictionary<string, WavData> Cache =
            new Dictionary<string, WavData>(StringComparer.OrdinalIgnoreCase);

        public static void Init()
        {
            _dir = Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle",
                "assets", "hk", "sheets", "charms", "Audio");
            _ggDir = Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle",
                "assets", "hk", "sheets", "GG");
        }

        public static void SelectionChange() { Play("ui_change_selection"); }
        public static void Confirm() { Play("ui_button_confirm"); }
        public static void Equip() { Play("charm_click_in"); }
        public static void Save() { Play("ui_save"); }
        public static void Overcharm() { Play("charm_overcharm"); }
        /// <summary>自限 sign 点击音效（前 3 次）。</summary>
        public static void ChainCut() { PlayFrom(_ggDir, "chain_cut"); }
        /// <summary>自限 sign 第 4 次点击：解锁音效。</summary>
        public static void GgVictoryCoreBling() { PlayFrom(_ggDir, "gg_victory_core_bling"); }

        private static void Play(string name)
        {
            WavData clip = Get(name);
            if (clip != null)
            {
                DashAudio.PlayWavOnce(clip.Raw);
            }
        }

        private static void PlayFrom(string dir, string name)
        {
            WavData clip = GetFrom(dir, name);
            if (clip != null)
            {
                DashAudio.PlayWavOnce(clip.Raw);
            }
        }

        private static WavData Get(string name)
        {
            if (Cache.TryGetValue(name, out WavData clip))
            {
                return clip;
            }
            string path = Path.Combine(_dir, name + ".wav");
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                clip = new WavData { Raw = Scale16BitPcm(File.ReadAllBytes(path), 1.8f) };
                if (clip != null)
                {
                    Cache[name] = clip;
                }
            }
            catch (Exception)
            {
            }
            return clip;
        }

        private static WavData GetFrom(string dir, string name)
        {
            string key = dir + "/" + name;
            if (Cache.TryGetValue(key, out WavData clip))
            {
                return clip;
            }
            string path = Path.Combine(dir, name + ".wav");
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                clip = new WavData { Raw = Scale16BitPcm(File.ReadAllBytes(path), 1.8f) };
                if (clip != null)
                {
                    Cache[key] = clip;
                }
            }
            catch (Exception)
            {
            }
            return clip;
        }

        /// <summary>按 data chunk 对 16bit PCM 采样放大音量（带削波保护）。</summary>
        private static byte[] Scale16BitPcm(byte[] src, float volume)
        {
            if (src == null || src.Length < 44)
            {
                return src;
            }
            byte[] dst = (byte[])src.Clone();
            int p = 12;
            int dataOff = -1;
            int dataLen = 0;
            while (p + 8 <= src.Length)
            {
                string id = Encoding.ASCII.GetString(src, p, 4);
                int sz = BitConverter.ToInt32(src, p + 4);
                if (id == "data")
                {
                    dataOff = p + 8;
                    dataLen = Math.Min(sz, src.Length - dataOff);
                    break;
                }
                p += 8 + sz + (sz % 2);
            }
            if (dataOff < 0)
            {
                return dst;
            }
            int end = Math.Min(dataOff + dataLen, dst.Length);
            for (int i = dataOff; i + 1 < end; i += 2)
            {
                short sample = BitConverter.ToInt16(dst, i);
                int v = (int)(sample * volume);
                if (v > short.MaxValue) v = short.MaxValue;
                if (v < short.MinValue) v = short.MinValue;
                dst[i] = (byte)(v & 0xFF);
                dst[i + 1] = (byte)((v >> 8) & 0xFF);
            }
            return dst;
        }

        private sealed class WavData
        {
            public byte[] Raw;
        }
    }
}
