using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace KnightInCradle
{
    /// <summary>
    /// 小骑士特效同步：把本地特效网格按“贴图四边形”快照出来，随 PlayerInfo.shieldData 一起发送，
    /// 远端据此复现挥砍弧光、骨钉剑气、法术特效等。
    ///
    /// 负载布局（小端）：
    ///   [0..43]  44 字节全 0 —— 占位“空护盾 DTO”，联机模组解析后 alpha=0 会直接跳过护盾绘制；
    ///   [44]     版本号 (3/4/5)
    ///   [45]     标志位（bit0=本体贴图需要水平镜像）
    ///   [46]     四边形数量
    ///   之后每个四边形 30 字节：
    ///     int16 贴图下标
    ///     int16 × 8   四个角相对“本体中心”的位置（以本体高度为单位 × 1024）
    ///     byte × 8    四个角各自的 UV（×255，保留镜像朝向）
    ///     byte × 4    颜色 RGBA
    ///   [v4+]    下砸骨剑/尖刺条目：byte 数量 + 每个 13 字节（见 DiveRect）
    ///   [v5]     骨钉攻击判定箱（拼刀用）：byte 数量 + 每个 9 字节
    ///     int16 × 4  相对“骑士中心”的 (dx, dy) 与尺寸 (w, h)，单位：格 × 1024
    ///     byte       招式类别（1=普攻 2=强力劈砍 3=冲刺劈砍 4=旋风劈砍）
    /// 说明：坐标不使用世界绝对坐标，而是“相对本体中心、以本体高度为单位”的偏移；
    ///       远端用“画面中本体的绘制高度”还原（与本体大小严格成比例）。
    ///       注意：本体贴图帧与负载必须来自同一帧（见 KnightEntity._fxBodySpriteName），
    ///       否则换帧时高度不一致会造成特效上下抖动。
    /// </summary>
    internal static class KnightFxSync
    {
        internal struct Quad
        {
            public int Sprite;
            public float N0, N1, N2, N3, N4, N5, N6, N7; // 相对本体中心、以本体高度为单位的偏移
            public byte U0, V0, U1, V1, U2, V2, U3, V3;    // 每个角的 UV（逐角保存，保镜像）
            public byte R, G, B, A;
        }

        /// <summary>下砸骨剑/尖刺：按实时状态描述的矩形（世界格坐标，避免渲染延迟）。</summary>
        internal struct DiveRect
        {
            public int Sprite;
            public float Cx, Cy;   // 世界格坐标（y 向下为正）
            public float W, H;     // 格
            public float UvTop, UvHeight;
            public bool Flip;
        }

        internal const int StubLen = 44;
        internal const int MaxQuads = 64;
        internal const int MaxDiveEntries = 28;   // 5 把骨剑 + 22 根尖刺
        internal const int MaxAtkEntries = 6;     // 同时生效的骨钉判定箱上限（普攻 1 + 技艺 1，留冗余）
        internal const int MaxElegyEntries = 8;   // 同时存在的蜕变挽歌剑气上限
        internal const int MaxSporeClouds = 8;    // 同时存在的蘑菇孢子云上限
        private const int DiveBytes = 13;
        private const int AtkBytes = 9;
        private const int ElegyBytes = 8;
        private const int QuadBytes = 30;
        private const float PosScale = 1024f;

        /// <summary>
        /// 骨钉（剑气）攻击判定箱：相对“骑士中心 (X,Y)”的偏移 + 尺寸，单位：地图格（y 向下为正）。
        /// 用于远端拼刀判定：两端各自把本地判定箱与本列表比对，相交即“拼刀”。
        /// </summary>
        internal struct AtkRect
        {
            public float Dx, Dy;   // 相对骑士中心的偏移（格）
            public float W, H;     // 判定箱宽高（格）
            public byte Kind;      // 1=普攻 2=强力劈砍 3=冲刺劈砍 4=旋风劈砍
        }

        /// <summary>
        /// 蜕变挽歌剑气判定箱：世界格坐标（y 向下为正），供远端下劈弹起判定。
        /// 视觉仍走 Quad 通道，这里只同步“能被踩/能被下劈弹起”的语义判定。
        /// </summary>
        internal struct ElegyRect
        {
            public float Cx, Cy;
            public float W, H;
        }

        /// <summary>防御者纹章（护符24）法阵状态；程序化贴图无法走四边形快照，单独同步。</summary>
        internal struct ShelterState
        {
            public bool Active;
            public float CircleRadius;
            public float RingRadius;
            public float PatternTimer;
            public byte PatternType;
            public float FlashTimer;
            public float SphereAngle;
        }

        /// <summary>蘑菇孢子云：世界格坐标 + 已存活时间；远端据此重建整团粒子。</summary>
        internal struct SporeCloudState
        {
            public float Cx, Cy, Age, Radius;
        }

        private static string[] _names;
        private static Dictionary<string, int> _ids;

        /// <summary>
        /// 精灵名表按 knight_manifest.json 的精灵键排序生成（两端资源包一致即可得到相同下标）。
        /// 若清单缺失则退化为 sprites 目录里 *.png 文件名排序。
        /// </summary>
        private static void EnsureTable()
        {
            if (_names != null)
            {
                return;
            }
            var list = new List<string>();
            try
            {
                string hk = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
                string manifest = Path.Combine(hk, "knight_manifest.json");
                if (File.Exists(manifest))
                {
                    string text = File.ReadAllText(manifest);
                    var rx = new Regex("\"([^\"]+)\"\\s*:\\s*\\{");
                    foreach (Match m in rx.Matches(text))
                    {
                        string key = m.Groups[1].Value;
                        if (!list.Contains(key))
                        {
                            list.Add(key);
                        }
                    }
                }
                // 吸虫之巢：fluke_manifest.json 不在主清单里，显式并入，
                // 保证 black_fluke_air*/flop* 帧在两端使用相同下标。
                string flukeManifest = Path.Combine(hk, "sheets", "nest", "fluke_manifest.json");
                if (File.Exists(flukeManifest))
                {
                    string text = File.ReadAllText(flukeManifest);
                    var rx = new Regex("\"([^\"]+)\"\\s*:\\s*\\{");
                    foreach (Match m in rx.Matches(text))
                    {
                        string key = m.Groups[1].Value;
                        if (!list.Contains(key))
                        {
                            list.Add(key);
                        }
                    }
                }
                string dir = Path.Combine(hk, "sprites");
                if (list.Count == 0 && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "*.png"))
                    {
                        list.Add(Path.GetFileNameWithoutExtension(f));
                    }
                }
                // 运行期从 sheets 加载的帧（shadow_dash / scream_blast 等）不在清单里，必须一并纳入
                if (Directory.Exists(hk))
                {
                    foreach (string p in Directory.GetFiles(hk, "*.png", SearchOption.AllDirectories))
                    {
                        string key = Path.GetFileNameWithoutExtension(p);
                        if (!list.Contains(key))
                        {
                            list.Add(key);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            // 蘑菇孢子粒子用的程序化圆点贴图：给一个两端一致的虚拟键。
            if (!list.Contains("knight_spore_dot"))
            {
                list.Add("knight_spore_dot");
            }
            list.Sort(StringComparer.Ordinal);
            _names = list.ToArray();
            _ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _names.Length; i++)
            {
                _ids[_names[i]] = i;
            }
        }

        internal static int IdOf(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            EnsureTable();
            return _ids.TryGetValue(name, out int id) ? id : -1;
        }

        internal static string NameOf(int id)
        {
            EnsureTable();
            return id >= 0 && id < _names.Length ? _names[id] : null;
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored)
        {
            return Encode(quads, bodyMirrored, null);
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive)
        {
            return Encode(quads, bodyMirrored, dive, null);
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive,
            List<AtkRect> atk)
        {
            return Encode(quads, bodyMirrored, dive, atk, null);
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive,
            List<AtkRect> atk, List<ElegyRect> elegy)
        {
            return Encode(quads, bodyMirrored, dive, atk, elegy, 0);
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive,
            List<AtkRect> atk, List<ElegyRect> elegy, byte furyAlpha)
        {
            return Encode(quads, bodyMirrored, dive, atk, elegy, furyAlpha, default(ShelterState));
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive,
            List<AtkRect> atk, List<ElegyRect> elegy, byte furyAlpha, ShelterState shelter)
        {
            return Encode(quads, bodyMirrored, dive, atk, elegy, furyAlpha, shelter, null);
        }

        internal static byte[] Encode(List<Quad> quads, bool bodyMirrored, List<DiveRect> dive,
            List<AtkRect> atk, List<ElegyRect> elegy, byte furyAlpha, ShelterState shelter,
            List<SporeCloudState> spores)
        {
            int nc = quads != null ? (quads.Count < MaxQuads ? quads.Count : MaxQuads) : 0;
            int nd = dive != null ? (dive.Count < MaxDiveEntries ? dive.Count : MaxDiveEntries) : 0;
            int na = atk != null ? (atk.Count < MaxAtkEntries ? atk.Count : MaxAtkEntries) : 0;
            int ne = elegy != null ? (elegy.Count < MaxElegyEntries ? elegy.Count : MaxElegyEntries) : 0;
            int nsp = spores != null ? Math.Min(spores.Count, MaxSporeClouds) : 0;
            if (nc == 0 && nd == 0 && na == 0 && ne == 0 && furyAlpha == 0 && !shelter.Active && nsp == 0)
            {
                return Array.Empty<byte>();
            }
            int n = nc;
            byte[] data = new byte[StubLen + 4 + n * QuadBytes + nd * DiveBytes + 1 + na * AtkBytes +
                1 + ne * ElegyBytes + 1 + 12 + 1 + nsp * 8];
            int o = StubLen;
            // 版本固定 5：Encode 始终会写 atk/elegy/fury 语义段（即使计数为 0）。
            // 旧版本客户端仍能解析前面的四边形/下砸/骨钉判定段；新版解析尾部段时不会错位。
            data[o++] = 5;
            data[o++] = (byte)(bodyMirrored ? 1 : 0);
            data[o++] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                Quad q = quads[i];
                // 贴图下标为负表示该特效画在小骑士身后（PR0 层）
                WriteI16(data, ref o, (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, q.Sprite)));
                WritePos(data, ref o, q.N0);
                WritePos(data, ref o, q.N1);
                WritePos(data, ref o, q.N2);
                WritePos(data, ref o, q.N3);
                WritePos(data, ref o, q.N4);
                WritePos(data, ref o, q.N5);
                WritePos(data, ref o, q.N6);
                WritePos(data, ref o, q.N7);
                data[o++] = q.U0;
                data[o++] = q.V0;
                data[o++] = q.U1;
                data[o++] = q.V1;
                data[o++] = q.U2;
                data[o++] = q.V2;
                data[o++] = q.U3;
                data[o++] = q.V3;
                data[o++] = q.R;
                data[o++] = q.G;
                data[o++] = q.B;
                data[o++] = q.A;
            }
            data[o++] = (byte)nd;
            for (int i = 0; i < nd; i++)
            {
                DiveRect r = dive[i];
                WriteI16(data, ref o, (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, r.Sprite)));
                WritePos(data, ref o, r.Cx);
                WritePos(data, ref o, r.Cy);
                WritePos(data, ref o, r.W);
                WritePos(data, ref o, r.H);
                data[o++] = (byte)Math.Round(Clamp01(r.UvTop) * 255f);
                data[o++] = (byte)Math.Round(Clamp01(r.UvHeight) * 255f);
                data[o++] = (byte)(r.Flip ? 1 : 0);
            }
            data[o++] = (byte)na;
            for (int i = 0; i < na; i++)
            {
                AtkRect a = atk[i];
                WritePos(data, ref o, a.Dx);
                WritePos(data, ref o, a.Dy);
                WritePos(data, ref o, a.W);
                WritePos(data, ref o, a.H);
                data[o++] = a.Kind;
            }
            data[o++] = (byte)ne;
            for (int i = 0; i < ne; i++)
            {
                ElegyRect r = elegy[i];
                WritePos(data, ref o, r.Cx);
                WritePos(data, ref o, r.Cy);
                WritePos(data, ref o, r.W);
                WritePos(data, ref o, r.H);
            }
            data[o++] = furyAlpha;
            data[o++] = (byte)(shelter.Active ? 1 : 0);
            WritePos(data, ref o, shelter.CircleRadius);
            WritePos(data, ref o, shelter.RingRadius);
            WritePos(data, ref o, shelter.PatternTimer);
            data[o++] = shelter.PatternType;
            WritePos(data, ref o, shelter.FlashTimer);
            WritePos(data, ref o, shelter.SphereAngle);
            data[o++] = (byte)nsp;
            for (int i = 0; i < nsp; i++)
            {
                SporeCloudState sp = spores[i];
                WritePos(data, ref o, sp.Cx);
                WritePos(data, ref o, sp.Cy);
                WritePos(data, ref o, sp.Age);
                WritePos(data, ref o, sp.Radius);
            }
            return data;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static void WriteI16(byte[] d, ref int o, short v)
        {
            d[o++] = (byte)(v & 0xFF);
            d[o++] = (byte)((v >> 8) & 0xFF);
        }

        private static void WritePos(byte[] d, ref int o, float v)
        {
            float q = v * PosScale;
            if (q > short.MaxValue)
            {
                q = short.MaxValue;
            }
            else if (q < short.MinValue)
            {
                q = short.MinValue;
            }
            WriteI16(d, ref o, (short)Math.Round(q));
        }

        /// <summary>远端解析：返回负载中的四边形数量，并按顺序回调每个四边形。数据非法时返回 0。</summary>
        /// <summary>只读头部：返回本体镜像标志；负载不可用时返回 null。</summary>
        internal static bool? BodyMirrored(byte[] data)
        {
            if (data == null || data.Length < StubLen + 3)
            {
                return null;
            }
            if (data[StubLen] != 3 && data[StubLen] != 4 && data[StubLen] != 5)
            {
                return null;
            }
            return (data[StubLen + 1] & 1) != 0;
        }

        internal static int Read(byte[] data, out bool bodyMirrored, Action<int, float[], byte[], byte[]> onQuad)
        {
            bodyMirrored = false;
            if (data == null || data.Length < StubLen + 4)
            {
                return 0;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 3 && ver != 4 && ver != 5)
            {
                return 0;
            }
            bodyMirrored = (data[o++] & 1) != 0;
            int n = data[o++];
            if (n <= 0 || n > MaxQuads || data.Length < o + n * QuadBytes)
            {
                return 0;
            }
            var pos = new float[8];
            var uv = new byte[8];
            var col = new byte[4];
            for (int i = 0; i < n; i++)
            {
                int sprite = ReadI16(data, ref o);
                for (int k = 0; k < 8; k++)
                {
                    pos[k] = ReadI16(data, ref o) / PosScale;
                }
                for (int k = 0; k < 8; k++)
                {
                    uv[k] = data[o++];
                }
                col[0] = data[o++];
                col[1] = data[o++];
                col[2] = data[o++];
                col[3] = data[o++];
                onQuad(sprite, pos, uv, col);
            }
            return n;
        }

        private static short ReadI16(byte[] d, ref int o)
        {
            short v = (short)(d[o] | (d[o + 1] << 8));
            o += 2;
            return v;
        }

        /// <summary>读取“下砸骨剑/尖刺”语义条目（v4 才有）；返回条目数量。</summary>
        internal static int ReadDive(byte[] data,
            Action<int, float, float, float, float, float, float, bool> onEntry)
        {
            if (data == null || data.Length < StubLen + 4)
            {
                return 0;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 4 && ver != 5)
            {
                return 0;
            }
            o++;                       // flags
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length)
            {
                return 0;
            }
            int nd = data[o++];
            if (nd <= 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes)
            {
                return 0;
            }
            for (int i = 0; i < nd; i++)
            {
                int sprite = ReadI16(data, ref o);
                float cx = ReadI16(data, ref o) / PosScale;
                float cy = ReadI16(data, ref o) / PosScale;
                float w = ReadI16(data, ref o) / PosScale;
                float h = ReadI16(data, ref o) / PosScale;
                float uvTop = data[o++] / 255f;
                float uvH = data[o++] / 255f;
                bool flip = data[o++] != 0;
                onEntry(sprite, cx, cy, w, h, uvTop, uvH, flip);
            }
            return nd;
        }

        /// <summary>
        /// 读取“骨钉攻击判定箱”语义条目（v5 才有）；条目本身是相对骑士中心的偏移与尺寸（格）。
        /// 返回条目数量；负载不含该段（v3/v4）时返回 0。
        /// </summary>
        internal static int ReadAttack(byte[] data, Action<float, float, float, float, byte> onEntry)
        {
            if (data == null || data.Length < StubLen + 5)
            {
                return 0;
            }
            int o = StubLen;
            if (data[o++] != 5)
            {
                return 0;
            }
            o++;                       // flags
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length)
            {
                return 0;
            }
            int nd = data[o++];
            if (nd < 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes)
            {
                return 0;
            }
            o += nd * DiveBytes;
            if (o >= data.Length)
            {
                return 0;
            }
            int na = data[o++];
            if (na <= 0 || na > MaxAtkEntries || data.Length < o + na * AtkBytes)
            {
                return 0;
            }
            for (int i = 0; i < na; i++)
            {
                float dx = ReadI16(data, ref o) / PosScale;
                float dy = ReadI16(data, ref o) / PosScale;
                float w = ReadI16(data, ref o) / PosScale;
                float h = ReadI16(data, ref o) / PosScale;
                byte kind = data[o++];
                onEntry(dx, dy, w, h, kind);
            }
            return na;
        }

        /// <summary>
        /// 读取“蜕变挽歌剑气判定箱”（v5 尾部附加段）。返回条目数量；
        /// 旧负载没有该段时返回 0（不影响旧客户端解析前面的段）。
        /// </summary>
        internal static int ReadElegy(byte[] data, Action<float, float, float, float> onEntry)
        {
            if (data == null || data.Length < StubLen + 4)
            {
                return 0;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 4 && ver != 5)
            {
                return 0;
            }
            o++;                       // flags
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length)
            {
                return 0;
            }
            int nd = data[o++];
            if (nd < 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes)
            {
                return 0;
            }
            o += nd * DiveBytes;
            if (ver >= 5)
            {
                if (o >= data.Length)
                {
                    return 0;
                }
                int na = data[o++];
                if (na < 0 || na > MaxAtkEntries || data.Length < o + na * AtkBytes)
                {
                    return 0;
                }
                o += na * AtkBytes;
            }
            if (o >= data.Length)
            {
                return 0;
            }
            int ne = data[o++];
            if (ne <= 0 || ne > MaxElegyEntries || data.Length < o + ne * ElegyBytes)
            {
                return 0;
            }
            for (int i = 0; i < ne; i++)
            {
                float cx = ReadI16(data, ref o) / PosScale;
                float cy = ReadI16(data, ref o) / PosScale;
                float w = ReadI16(data, ref o) / PosScale;
                float h = ReadI16(data, ref o) / PosScale;
                onEntry(cx, cy, w, h);
            }
            return ne;
        }

        /// <summary>
        /// 读取“亡者之怒红色光晕”透明度字节（v5 尾部，Elegy 段之后）。
        /// 返回 0~255；旧负载没有该字段时返回 0。
        /// </summary>
        internal static byte ReadFury(byte[] data)
        {
            if (data == null || data.Length < StubLen + 4)
            {
                return 0;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 4 && ver != 5)
            {
                return 0;
            }
            o++;                       // flags
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length)
            {
                return 0;
            }
            int nd = data[o++];
            if (nd < 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes)
            {
                return 0;
            }
            o += nd * DiveBytes;
            if (ver >= 5)
            {
                if (o >= data.Length)
                {
                    return 0;
                }
                int na = data[o++];
                if (na < 0 || na > MaxAtkEntries || data.Length < o + na * AtkBytes)
                {
                    return 0;
                }
                o += na * AtkBytes;
            }
            if (o >= data.Length)
            {
                return 0;
            }
            int ne = data[o++];
            if (ne < 0 || ne > MaxElegyEntries || data.Length < o + ne * ElegyBytes)
            {
                return 0;
            }
            o += ne * ElegyBytes;
            return o < data.Length ? data[o] : (byte)0;
        }

        /// <summary>读取防御者纹章法阵状态（v5 尾部，Fury 字节之后）；旧负载返回 Active=false。</summary>
        internal static ShelterState ReadShelter(byte[] data)
        {
            ShelterState s = default(ShelterState);
            if (data == null || data.Length < StubLen + 4)
            {
                return s;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 4 && ver != 5)
            {
                return s;
            }
            o++;
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length) return s;
            int nd = data[o++];
            if (nd < 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes) return s;
            o += nd * DiveBytes;
            if (ver >= 5)
            {
                if (o >= data.Length) return s;
                int na = data[o++];
                if (na < 0 || na > MaxAtkEntries || data.Length < o + na * AtkBytes) return s;
                o += na * AtkBytes;
            }
            if (o >= data.Length) return s;
            int ne = data[o++];
            if (ne < 0 || ne > MaxElegyEntries || data.Length < o + ne * ElegyBytes) return s;
            o += ne * ElegyBytes;
            if (o >= data.Length) return s;
            o++; // furyAlpha
            if (o + 12 > data.Length) return s;
            s.Active = data[o++] != 0;
            s.CircleRadius = ReadI16(data, ref o) / PosScale;
            s.RingRadius = ReadI16(data, ref o) / PosScale;
            s.PatternTimer = ReadI16(data, ref o) / PosScale;
            s.PatternType = data[o++];
            s.FlashTimer = ReadI16(data, ref o) / PosScale;
            s.SphereAngle = ReadI16(data, ref o) / PosScale;
            return s;
        }

        /// <summary>读取蘑菇孢子云列表（Shelter 段之后）；返回数量并回调每个云。</summary>
        internal static int ReadSpores(byte[] data, Action<float, float, float, float> onEntry)
        {
            if (data == null || data.Length < StubLen + 4)
            {
                return 0;
            }
            int o = StubLen;
            int ver = data[o++];
            if (ver != 4 && ver != 5) return 0;
            o++;
            int nc = data[o++];
            o += nc * QuadBytes;
            if (o >= data.Length) return 0;
            int nd = data[o++];
            if (nd < 0 || nd > MaxDiveEntries || data.Length < o + nd * DiveBytes) return 0;
            o += nd * DiveBytes;
            if (ver >= 5)
            {
                if (o >= data.Length) return 0;
                int na = data[o++];
                if (na < 0 || na > MaxAtkEntries || data.Length < o + na * AtkBytes) return 0;
                o += na * AtkBytes;
            }
            if (o >= data.Length) return 0;
            int ne = data[o++];
            if (ne < 0 || ne > MaxElegyEntries || data.Length < o + ne * ElegyBytes) return 0;
            o += ne * ElegyBytes;
            if (o >= data.Length) return 0;
            o++; // furyAlpha
            if (o + 12 > data.Length) return 0;
            o += 12; // shelter block
            if (o >= data.Length) return 0;
            int nsp = data[o++];
            if (nsp <= 0 || nsp > MaxSporeClouds || data.Length < o + nsp * 8) return 0;
            for (int i = 0; i < nsp; i++)
            {
                float cx = ReadI16(data, ref o) / PosScale;
                float cy = ReadI16(data, ref o) / PosScale;
                float age = ReadI16(data, ref o) / PosScale;
                float radius = ReadI16(data, ref o) / PosScale;
                onEntry(cx, cy, age, radius);
            }
            return nsp;
        }
    }
}
