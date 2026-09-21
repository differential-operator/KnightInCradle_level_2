using System;
using System.Collections.Generic;
using KnightInCradle;

/// <summary>
/// KnightFxSync 负载编解码回归测试：
///   ① 只有拼刀段（无特效/无下砸）时负载必须非空且版本号为 5；
///   ② 无拼刀段时保持 v4（旧客户端/旧解析路径不受影响）；
///   ③ 四边形 + 下砸 + 拼刀混装时三段都能各自解析；
///   ④ 全空输入返回空负载；
///   ⑤ 超范围数值被钳位而不是回绕。
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok)
        {
            _fail++;
        }
    }

    private static int Main()
    {
        // ① 只有骨钉判定箱（拼刀）：负载非空、版本 5、数值往返
        var atkOnly = new List<KnightFxSync.AtkRect>
        {
            new KnightFxSync.AtkRect { Dx = 1.95f, Dy = 0.4f, W = 4.4f, H = 2.9f, Kind = 2 }
        };
        byte[] p1 = KnightFxSync.Encode(null, false, null, atkOnly);
        Check(p1.Length > KnightFxSync.StubLen, "仅拼刀段时负载非空 len=" + p1.Length);
        Check(p1[KnightFxSync.StubLen] == 5, "版本号 = 5（带拼刀段）");
        int n1 = KnightFxSync.ReadAttack(p1, (dx, dy, w, h, kind) =>
        {
            Check(Math.Abs(dx - 1.95f) < 0.002f, $"dx 往返 dx={dx}");
            Check(Math.Abs(dy - 0.4f) < 0.002f, $"dy 往返 dy={dy}");
            Check(Math.Abs(w - 4.4f) < 0.002f, $"w 往返 w={w}");
            Check(Math.Abs(h - 2.9f) < 0.002f, $"h 往返 h={h}");
            Check(kind == 2, $"招式类别往返 kind={kind}");
        });
        Check(n1 == 1, "ReadAttack 条目数 = 1");
        Check(KnightFxSync.Read(p1, out bool m1, (s, p, u, c) => { }) == 0, "无四边形时 Read 返回 0");
        Check(m1 == false, "无四边形时镜像标志为 false");
        Check(KnightFxSync.ReadDive(p1, (s, cx, cy, w, h, t, hh, f) => { }) == 0, "无下砸条目时 ReadDive 返回 0");

        // ② 无拼刀段（v4）：保持旧格式
        var dive = new List<KnightFxSync.DiveRect>
        {
            new KnightFxSync.DiveRect
            {
                Sprite = 3, Cx = 1.5f, Cy = 2.25f, W = 1f, H = 2f,
                UvTop = 0.5f, UvHeight = 0.5f, Flip = true
            }
        };
        byte[] p2 = KnightFxSync.Encode(null, true, dive, null);
        Check(p2[KnightFxSync.StubLen] == 4, "无拼刀段时版本号保持 4");
        Check(KnightFxSync.ReadAttack(p2, (dx, dy, w, h, kind) => { }) == 0, "v4 负载 ReadAttack 返回 0");
        Check(KnightFxSync.BodyMirrored(p2) == true, "v4 负载 BodyMirrored 正常");
        Check(KnightFxSync.ReadDive(p2, (s, cx, cy, w, h, t, hh, f) =>
            Check(s == 3 && Math.Abs(cx - 1.5f) < 0.01f && f, "v4 下砸条目解析正常")) == 1,
            "ReadDive 条目数 = 1");

        // ③ 混装（v5）：三段都能读
        var quads = new List<KnightFxSync.Quad>
        {
            new KnightFxSync.Quad { Sprite = -4, N0 = -0.5f, N1 = 0.25f, U0 = 10, A = 255 }
        };
        var atk2 = new List<KnightFxSync.AtkRect>
        {
            new KnightFxSync.AtkRect { Dx = -2.5f, Dy = 0.1f, W = 5f, H = 2.2f, Kind = 3 }
        };
        byte[] p3 = KnightFxSync.Encode(quads, true, dive, atk2);
        Check(p3[KnightFxSync.StubLen] == 5, "混装负载版本号 = 5");
        Check(KnightFxSync.Read(p3, out bool m3, (s, p, u, c) => Check(s == -4, "混装：四边形贴图下标（负=身后层）")) == 1 &&
              m3, "混装：四边形数量与镜像标志");
        Check(KnightFxSync.ReadDive(p3, (s, cx, cy, w, h, t, hh, f) => { }) == 1, "v5 负载仍能读下砸条目");
        Check(KnightFxSync.ReadAttack(p3, (dx, dy, w, h, kind) =>
            Check(Math.Abs(dx + 2.5f) < 0.002f && Math.Abs(w - 5f) < 0.002f && kind == 3,
                $"混装负载拼刀条目 dx={dx} w={w} kind={kind}")) == 1, "v5 负载 ReadAttack 条目数 = 1");

        // ④ 空输入
        Check(KnightFxSync.Encode(null, false, null, null).Length == 0, "全空时返回空负载");
        Check(KnightFxSync.Encode(null, false, null, new List<KnightFxSync.AtkRect>()).Length == 0,
            "空拼刀列表时返回空负载");

        // ⑤ 越界量化：钳位而非回绕
        byte[] p4 = KnightFxSync.Encode(null, false, null, new List<KnightFxSync.AtkRect>
        {
            new KnightFxSync.AtkRect { Dx = 100f, Dy = -100f, W = 3f, H = 3f, Kind = 1 }
        });
        KnightFxSync.ReadAttack(p4, (dx, dy, w, h, kind) =>
            Check(dx > 30f && dx < 33f && dy < -30f && dy > -33f, $"超范围值被钳位 dx={dx} dy={dy}"));

        Console.WriteLine(_fail == 0 ? "ALL OK" : _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
