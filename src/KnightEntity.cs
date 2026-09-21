using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using evt;
using HarmonyLib;
using m2d;
using nel;
using Newtonsoft.Json.Linq;
using UnityEngine;
using XX;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    /// <summary>
    /// 小骑士独立实体：自己的位置/速度/碰撞/动画/渲染。
    /// 与诺艾尔完全无关，T 键切换后独立存在。
    /// </summary>
    public class KnightEntity : MonoBehaviour
    {
        public static KnightEntity Instance { get; private set; }
        /// <summary>小骑士是否处于激活状态（切回诺艾尔时为 false）。</summary>
        public bool IsActive => _active;

        /// <summary>
        /// 方案A（原生物理模式）资格判定：普通“贴地/走/停”状态交给诺艾尔原生
        /// M2Mover 执行（解决斜坡进门与墙角穿模）；一切有专属手写位移的状态
        /// （冲刺/超冲/下砸/施法/凝聚/坐椅/攀墙/空中动作等）继续走旧路径。
        /// </summary>
        public bool IsNativeLocomotionEligible(bool hasNativeFoot)
        {
            if (!_active || !_assetsLoaded || _isDead || _respawnFadeOut || _hurt ||
                _isSitting || _sitStandingUp || _taunting || _attacking || _dashing ||
                _isShadowDash || _superCharging || _superDashing || _superInertiaTimer > 0f ||
                _superBrakeTimer > 0f || _superUnchargeTimer > 0f || _focusing ||
                _fireballCasting || _screaming || _voidPhase >= 2 || _diving ||
                _swimState != 0 || _nailArtCharging || _nailArtSlashing || _dashSlashing ||
                _cycloneSlashing || _dreamNailing || _onWall || _wallJumping ||
                _wallJumpDrift || _doubleJumping || _pogoGravityLock > 0f ||
                _recoilTimer > 0f || _pendingReposition > 0 || _transitionPause ||
                _pendingRepositionActive)
            {
                return false;
            }
            if (IsGameMenuOpen())
            {
                return false;
            }
            if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
            {
                return false;
            }
            if (_mp == null)
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
            // 只接管“贴地 / 刚落地”的普通行走；跳跃/空中仍走旧路径
            // （原生跳跃接管实验已回退：引擎落地后 FootD 不自动挂地，会导致
            //   hasFoot/canJump 恒 false 及地面抽搐，待专项解决后另行引入）
            return Grounded || hasNativeFoot;
        }

        /// <summary>
        /// 方案A：原生帧结束后把小骑士对齐到诺艾尔（诺艾尔=原生物理真身）。
        /// 用于替换“骑士→诺艾尔”方向的拖拽同步。
        /// </summary>
        public void SyncFromHost(PRNoel pr)
        {
            if (pr == null || _mp == null)
            {
                return;
            }
            X = pr.x - HurtCenterX;
            Y = pr.mbottom - SizeY;
            Grounded = true;
            _lastSafeX = X;
            _lastSafeY = Y;
        }

        /// <summary>供联机远端渲染读取的骑士动画快照。</summary>
        public sealed class KnightRemoteSnapshot
        {
            public string Clip;   // 当前剪辑名（Idle/Run/Slash…）
            public string Sprite; // 当前帧精灵名（knight_manifest 的 sprites 键 / sprites 目录 PNG 名）
            public int Frame;     // 剪辑内帧序号
            public int Face;      // 1=朝右（贴图需镜像），-1=朝左
        }

        /// <summary>当前骑士动画快照；未激活/无帧时返回 null。</summary>
        public KnightRemoteSnapshot GetRemoteSnapshot()
        {
            // 优先使用“与特效负载同一帧”的本体帧名/镜像状态：
            // 特效网格是上一帧渲染产物，若这里用当前帧的贴图，换帧时高度不一致会让远端特效上下抖动。
            bool unnClip = !string.IsNullOrEmpty(_currentClip) &&
                (_currentClip.StartsWith("Unn", StringComparison.Ordinal) ||
                 _currentClip.StartsWith("MushUnn", StringComparison.Ordinal));
            string sprite = unnClip && !string.IsNullOrEmpty(_currentSpriteName)
                ? _currentSpriteName
                : (!string.IsNullOrEmpty(_fxBodySpriteName) ? _fxBodySpriteName : _currentSpriteName);
            if (!_active || string.IsNullOrEmpty(sprite))
            {
                return null;
            }
            int face = unnClip
                ? (_faceDir < 0f ? 1 : -1)
                : (!string.IsNullOrEmpty(_fxBodySpriteName)
                ? ((sprite.StartsWith("shadow_dash", StringComparison.Ordinal)
                    ? !_fxBodyMirrored : _fxBodyMirrored) ? 1 : -1)
                : (_faceDir < 0f ? 1 : -1));
            return new KnightRemoteSnapshot
            {
                Clip = _currentClip ?? "Idle",
                Sprite = sprite,
                Frame = _frameIndex,
                Face = face
            };
        }

        // ================= 联机特效同步 =================

        private static FieldInfo[] _fxTicketFields;
        private static readonly Dictionary<Texture2D, string> _texNameRev = new Dictionary<Texture2D, string>();
        private static int _texRevCount = -1;
        private int _fxPayloadFrame = -1;
        private byte[] _fxPayloadCache = Array.Empty<byte>();
        private string _fxBodySpriteName;   // 与特效负载同一帧的本体帧名
        private bool _fxBodyMirrored;       // 与特效负载同一帧的本体镜像状态

        /// <summary>同一帧内多处取特效负载时复用结果（每帧只快照一次网格）。</summary>
        public byte[] BuildRemoteFxPayload()
        {
            if (_fxPayloadFrame == Time.frameCount && _fxPayloadCache != null)
            {
                return _fxPayloadCache;
            }
            byte[] result = BuildRemoteFxPayloadUncached();
            _fxPayloadFrame = Time.frameCount;
            _fxPayloadCache = result;
            return result;
        }

        /// <summary>
        /// 把当前所有特效票据的网格快照成“贴图四边形”（世界格坐标 + UV + 颜色 + 贴图下标），
        /// 供联机远端复现挥砍弧光 / 骨钉剑气 / 法术等特效。
        /// 空负载表示当前没有特效；格式见 KnightFxSync。
        /// </summary>
        private byte[] BuildRemoteFxPayloadUncached()
        {
            try
            {
                if (!_active || _mp == null)
                {
                    return Array.Empty<byte>();
                }
                if (_fxTicketFields == null)
                {
                    var fields = new List<FieldInfo>();
                    foreach (FieldInfo fi in typeof(KnightEntity).GetFields(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (fi.FieldType == typeof(M2RenderTicket) &&
                            fi.Name.IndexOf("Dbg", StringComparison.Ordinal) < 0 &&
                            fi.Name.IndexOf("Debug", StringComparison.Ordinal) < 0)
                        {
                            fields.Add(fi);
                        }
                    }
                    _fxTicketFields = fields.ToArray();
                }
                // 贴图 → 精灵名反查（数量变化时重建，避免每帧全量重建）
                if (_texRevCount != _textures.Count)
                {
                    _texRevCount = _textures.Count;
                    _texNameRev.Clear();
                    foreach (KeyValuePair<string, Texture2D> kv in _textures)
                    {
                        if (kv.Value != null)
                        {
                            _texNameRev[kv.Value] = kv.Key;
                        }
                    }
                }
                // 蘑菇孢子程序化圆点贴图：映射到虚拟键，让粒子网格走四边形同步。
                if (_dotTexes != null && _dotTexes.Length > 1 && _dotTexes[1] != null)
                {
                    _texNameRev[_dotTexes[1]] = "knight_spore_dot";
                }
                // 本体参照：同一网格空间里取身体网格的中心与高度，作为特效归一化基准。
                // 这样两边只需共享“本体”这一参照，不受坐标系/房间尺度差异影响。
                MeshDrawer bodyMd = null;
                Matrix4x4 bodyM = Matrix4x4.identity;
                foreach (FieldInfo fi in _fxTicketFields)
                {
                    M2RenderTicket tkB = fi.GetValue(this) as M2RenderTicket;
                    if (tkB != null && tkB.MdMain != null && tkB.MdMain == _mesh &&
                        _mesh != null && _mesh.getVertexMax() >= 4)
                    {
                        bodyMd = _mesh;
                        bodyM = tkB.Matrix;
                        break;
                    }
                }
                if (bodyMd == null)
                {
                    return Array.Empty<byte>(); // 没有本体参照就无法做相对坐标
                }
                Vector3[] bvs = bodyMd.getVertexArray();
                Vector3 b0 = bodyM.MultiplyPoint3x4(bvs[0]);
                Vector3 b1 = bodyM.MultiplyPoint3x4(bvs[1]);
                Vector3 b2 = bodyM.MultiplyPoint3x4(bvs[2]);
                Vector3 b3 = bodyM.MultiplyPoint3x4(bvs[3]);
                float bMinX = Mathf.Min(Mathf.Min(b0.x, b1.x), Mathf.Min(b2.x, b3.x));
                float bMaxX = Mathf.Max(Mathf.Max(b0.x, b1.x), Mathf.Max(b2.x, b3.x));
                float bMinY = Mathf.Min(Mathf.Min(b0.y, b1.y), Mathf.Min(b2.y, b3.y));
                float bMaxY = Mathf.Max(Mathf.Max(b0.y, b1.y), Mathf.Max(b2.y, b3.y));
                float bodyH = Mathf.Max(1f, bMaxY - bMinY);
                float bodyCx = (bMinX + bMaxX) * 0.5f;
                float bodyCy = (bMinY + bMaxY) * 0.5f;
                if (bodyH < 1f)
                {
                    return Array.Empty<byte>();
                }
                // 记录与本次快照同一帧的本体帧名与镜像状态，供 GetRemoteSnapshot 使用
                _fxBodyMirrored = bodyMd.uv_width < 0f;
                _fxBodySpriteName = null;
                Material bodyMat = bodyMd.getMaterial();
                if (bodyMat != null && bodyMat.mainTexture is Texture2D bodyTex &&
                    _texNameRev.TryGetValue(bodyTex, out string bodyName))
                {
                    _fxBodySpriteName = bodyName;
                }

                var quads = new List<KnightFxSync.Quad>();
                foreach (FieldInfo fi in _fxTicketFields)
                {
                    M2RenderTicket tk = fi.GetValue(this) as M2RenderTicket;
                    MeshDrawer md = tk != null ? tk.MdMain : null;
                    if (md == null || md == bodyMd || md == _diveSwordMesh || md == _diveSpikeMesh)
                    {
                        continue; // 本体不重复同步（否则远端会出现第二个小骑士）
                    }
                    int vn = md.getVertexMax();
                    if (vn < 4 || vn % 4 != 0)
                    {
                        continue; // 只处理四顶点矩形（非矩形/程序化形状暂不同步）
                    }
                    Material mt = md.getMaterial();
                    if (!(mt != null && mt.mainTexture is Texture2D tex))
                    {
                        continue;
                    }
                    if (!_texNameRev.TryGetValue(tex, out string sprite))
                    {
                        // 贴图反查失败时，攻击剑气网格仍可用当前帧名同步
                        // （亡者之怒红色剑气 rage_slash_* 等，避免因共享贴图而漏发）。
                        if (md == _fxMesh && !string.IsNullOrEmpty(_fxSpriteName))
                        {
                            sprite = _fxSpriteName;
                        }
                        else
                        {
                            continue; // 程序化贴图（白点/羽翼等）暂不同步
                        }
                    }
                    int sid = KnightFxSync.IdOf(sprite);
                    if (sid < 0)
                    {
                        continue;
                    }
                    // 身后层（PR0）用负下标标记，远端先画它、再画本体
                    int encSprite = tk.order == M2Mover.DRAW_ORDER.PR0 ? -(sid + 1) : sid;
                    Vector3[] vs = md.getVertexArray();
                    if (vs == null)
                    {
                        continue;
                    }
                    Color32[] cs = md.getColorArray();
                    Vector2[] uvs = md.getUvArray();
                    Matrix4x4 m = tk.Matrix;
                    Color32 meshCol = md.Col;
                    int groups = Mathf.Min(vn / 4, KnightFxSync.MaxQuads);
                    for (int g = 0; g < groups; g++)
                    {
                        int b = g * 4;
                        // 相对本体中心、以“本体高度”为单位（同一网格空间，与房间/坐标系无关）
                        Vector3 p0 = m.MultiplyPoint3x4(vs[b]);
                        Vector3 p1 = m.MultiplyPoint3x4(vs[b + 1]);
                        Vector3 p2 = m.MultiplyPoint3x4(vs[b + 2]);
                        Vector3 p3 = m.MultiplyPoint3x4(vs[b + 3]);
                        Color32 c = cs != null && b < cs.Length ? cs[b] : (Color32)Color.white;
                        c = new Color32(
                            (byte)(c.r * meshCol.r / 255),
                            (byte)(c.g * meshCol.g / 255),
                            (byte)(c.b * meshCol.b / 255),
                            (byte)(c.a * meshCol.a / 255));
                        if (c.a == 0)
                        {
                            continue;
                        }
                        // 过滤超大全屏类网格（红/黑边饰、滤镜等），避免远端糊上一块大方块
                        float bw = Mathf.Max(Mathf.Abs(p1.x - p0.x), Mathf.Abs(p3.x - p2.x));
                        float bh = Mathf.Max(Mathf.Abs(p3.y - p0.y), Mathf.Abs(p2.y - p1.y));
                        if (bw > 30f * _mp.CLEN || bh > 30f * _mp.CLEN)
                        {
                            continue;
                        }
                        quads.Add(new KnightFxSync.Quad
                        {
                            Sprite = encSprite,
                            N0 = (p0.x - bodyCx) / bodyH, N1 = (p0.y - bodyCy) / bodyH,
                            N2 = (p1.x - bodyCx) / bodyH, N3 = (p1.y - bodyCy) / bodyH,
                            N4 = (p2.x - bodyCx) / bodyH, N5 = (p2.y - bodyCy) / bodyH,
                            N6 = (p3.x - bodyCx) / bodyH, N7 = (p3.y - bodyCy) / bodyH,
                            // 逐角 UV：多四边形网格（下砸骨剑/尖刺等）每个四边形的 UV 不同，
                            // 只发网格级 UV 会导致远端贴图内容逐帧变化（看起来抖动）
                            U0 = ToU8(uvs, b + 0, 0), V0 = ToU8(uvs, b + 0, 1),
                            U1 = ToU8(uvs, b + 1, 0), V1 = ToU8(uvs, b + 1, 1),
                            U2 = ToU8(uvs, b + 2, 0), V2 = ToU8(uvs, b + 2, 1),
                            U3 = ToU8(uvs, b + 3, 0), V3 = ToU8(uvs, b + 3, 1),
                            R = c.r, G = c.g, B = c.b, A = c.a
                        });
                        if (quads.Count >= KnightFxSync.MaxQuads)
                        {
                            break;
                        }
                    }
                    if (quads.Count >= KnightFxSync.MaxQuads)
                    {
                        break;
                    }
                }
                // 下砸骨剑/尖刺：直接用实时状态（与判定箱同源），避免“渲染滞后/跟随上一帧网格”导致的抖动
                var dive = new List<KnightFxSync.DiveRect>();
                int swordId = KnightFxSync.IdOf("nail_upgrade_0000_pure-nail");
                if (swordId >= 0)
                {
                    for (int i = 0; i < _diveSwords.Count; i++)
                    {
                        DiveSword s = _diveSwords[i];
                        if (s.Height <= 0.03f)
                        {
                            continue;
                        }
                        float f = Mathf.Clamp01(s.Height / s.MaxHeight);
                        dive.Add(new KnightFxSync.DiveRect
                        {
                            Sprite = swordId,
                            Cx = s.X + DiveSwordRenderOffX - s.Width * 0.5f,
                            Cy = s.BaseY + s.RenderOffY,
                            W = s.Width,
                            H = s.Height,
                            UvTop = 1f - f,
                            UvHeight = f,
                            Flip = false
                        });
                    }
                }
                int spikeId = KnightFxSync.IdOf("white_spikes0000");
                if (spikeId >= 0)
                {
                    float spikeFullH = DiveSpikeBaseWidth * DiveSpikeAspect;
                    for (int i = 0; i < _diveSpikes.Count; i++)
                    {
                        DiveSpike p = _diveSpikes[i];
                        if (p.Height <= 0.02f)
                        {
                            continue;
                        }
                        float f = Mathf.Clamp01(p.Height / spikeFullH);
                        dive.Add(new KnightFxSync.DiveRect
                        {
                            Sprite = spikeId,
                            Cx = p.X,
                            Cy = p.BaseY + DiveSpikeRenderOffY,
                            W = DiveSpikeBaseWidth,
                            H = p.Height,
                            UvTop = 1f - f,
                            UvHeight = f,
                            Flip = p.X > _diveLandX
                        });
                    }
                }
                // 深渊尖啸冲击波：按实时状态直发（6.5×6.5 格的大图走网格快照容易失真/算丢）
                // 拼刀：把当前生效的骨钉（剑气）判定箱一并同步（相对骑士中心，单位：格）。
                // 远端据此还原绝对坐标，与本地方形判定箱做相交检测。
                var atk = new List<KnightFxSync.AtkRect>(4);
                _nailParrySelfRects.Clear();
                _nailParrySelfKinds.Clear();
                CollectNailAttackRects(_nailParrySelfRects, _nailParrySelfKinds);
                for (int i = 0; i < _nailParrySelfRects.Count; i++)
                {
                    Vector4 r = _nailParrySelfRects[i];
                    atk.Add(new KnightFxSync.AtkRect
                    {
                        Dx = r.x - X,
                        Dy = r.y - Y,
                        W = r.z,
                        H = r.w,
                        Kind = i < _nailParrySelfKinds.Count ? _nailParrySelfKinds[i] : NailParryKindSlash
                    });
                }
                // 蜕变挽歌剑气语义判定：世界格坐标，远端据此做“下劈弹起”。
                var elegy = new List<KnightFxSync.ElegyRect>(_elegyBlades.Count);
                for (int i = 0; i < _elegyBlades.Count && i < KnightFxSync.MaxElegyEntries; i++)
                {
                    ElegyBladeProj p = _elegyBlades[i];
                    float ehx = p.X + (p.Dir < 0f ? -1.2f : 0.2f);
                    float ehy = p.Y + 0.5f;
                    elegy.Add(new KnightFxSync.ElegyRect
                    {
                        Cx = ehx,
                        Cy = ehy,
                        W = ElegyBladeHitboxW,
                        H = ElegyBladeHitboxH
                    });
                }
                // 亡者之怒：本地红色光晕是程序化贴图，通用四边形快照会跳过；
                // 这里把当前脉冲透明度单独同步给远端重建。
                byte furyAlpha = 0;
                if (FuryActive)
                {
                    float ft = _furyGlowTimer % 0.51f;
                    float fa = ft < 0.25f
                        ? ft / 0.25f
                        : (ft < 0.26f ? 1f : 1f - (ft - 0.26f) / 0.25f);
                    furyAlpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(fa) * 0.75f * 255f);
                }
                // 防御者纹章：程序化贴图不走四边形通道，单独同步法阵状态。
                KnightFxSync.ShelterState shelter = default(KnightFxSync.ShelterState);
                if (CharmEffects.IsEquipped(CharmEffects.ShelterId))
                {
                    shelter.Active = true;
                    shelter.CircleRadius = ShelterCircleRadius;
                    shelter.RingRadius = _shelterRingRadius;
                    shelter.PatternTimer = _shelterPatternTimer;
                    shelter.PatternType = (byte)_shelterPatternType;
                    shelter.FlashTimer = _shelterFlashTimer;
                    shelter.SphereAngle = _shelterSphereAngle;
                }
                var spores = new List<KnightFxSync.SporeCloudState>(_sporeClouds.Count);
                for (int i = 0; i < _sporeClouds.Count && i < KnightFxSync.MaxSporeClouds; i++)
                {
                    SporeCloud sc = _sporeClouds[i];
                    spores.Add(new KnightFxSync.SporeCloudState
                    {
                        Cx = sc.X,
                        Cy = sc.Y,
                        Age = sc.Age,
                        Radius = SporeRadiusNow()
                    });
                }
                return KnightFxSync.Encode(quads, _fxBodyMirrored, dive, atk, elegy, furyAlpha, shelter, spores);
            }
            catch (Exception)
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>取第 index 个顶点 UV 的某个分量，量化成 0~255 字节。</summary>
        private static byte ToU8(Vector2[] uvs, int index, int comp)
        {
            if (uvs == null || index < 0 || index >= uvs.Length)
            {
                return 0;
            }
            float v = comp == 0 ? uvs[index].x : uvs[index].y;
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
        }

        /// <summary>小骑士当前是否处于下劈状态（该状态下陷阱接触爆炸被屏蔽，避免下劈提前打掉陷阱）。</summary>
        public bool IsDownSlashing => _downSlash;
        /// <summary>小骑士是否正坐在长椅上（含起身过渡，用于禁用换人/冲刺等）。</summary>
        public bool IsSitting => _isSitting || _sitStandingUp;
        /// <summary>小骑士脚底 Y（游戏内 y 轴向下为正）。</summary>
        public float FootY => Y + SizeY;
        /// <summary>判定箱半宽（供切出小骑士时改诺艾尔碰撞体用）。</summary>
        public static float HurtSizeX => (CollideX1 - CollideX0) * 0.5f;
        /// <summary>判定箱半高。</summary>
        public static float HurtSizeY => (CollideY1 - CollideY0) * 0.5f;
        /// <summary>判定箱中心相对实体原点 X 的偏移。</summary>
        public static float HurtCenterX => (CollideX0 + CollideX1) * 0.5f;
        /// <summary>判定箱中心相对实体原点 Y 的偏移。</summary>
        public static float HurtCenterY => (CollideY0 + CollideY1) * 0.5f;
        /// <summary>骑士中心 Y 到脚底的距离（格）：联机侧用它把广播的脚底坐标反推回骑士中心。</summary>
        public static float CenterToFootY => SizeY;
        /// <summary>亡者之怒：装备亡者之怒且血量恰为 1 时进入狂怒状态。</summary>
        public bool FuryActive => _active && !_isDead && !_respawnFadeOut && _health == 1 &&
            CharmEffects.IsEquipped(CharmEffects.FuryId);
        /// <summary>低血量（1 格）屏幕黑边滤镜是否显示（亡者之怒时改用红框）。</summary>
        public bool LowHpVisible => _active && !_isDead && !_respawnFadeOut && _health == 1 && !FuryActive;
        /// <summary>亡者之怒：屏幕四周红色边框是否显示。</summary>
        public bool FuryVignetteVisible => FuryActive;
        /// <summary>小骑士是否处于死亡流程（含黑屏/复活淡出）。</summary>
        public bool IsDead => _isDead || _respawnFadeOut;
        /// <summary>死亡黑屏透明度（0~1），供 OnGUI 绘制全屏黑幕。</summary>
        public float DeathBlackAlpha => (_isDead || _respawnFadeOut) ? _deathBlackAlpha : 0f;
        /// <summary>受击瞬间屏幕四周黑边闪过的透明度（0~1，随时间渐出）。</summary>
        public float HitVignetteAlpha => _active ? _hitVignetteAlpha : 0f;
        /// <summary>凝聚回血成功时屏幕四周短暂亮一下的透明度（0~1，渐出）。</summary>
        public float FocusFlashAlpha => _active ? _focusFlashAlpha : 0f;
        /// <summary>无忧旋律免伤成功时屏幕四周红色闪光的透明度（0~1，渐出）。</summary>
        public float MelodyFlashAlpha => _active ? _melodyFlashAlpha : 0f;

        /// <summary>屏幕四周短时金色闪烁（同回血闪光）。</summary>
        public float GgGoldFlashAlpha => _active ? _ggGoldFlashAlpha : 0f;

        /// <summary>寻神者护符出现时：屏幕四周短时金色闪烁（同回血闪光）。</summary>
        public void TriggerGoldScreenFlash()
        {
            _ggGoldFlashAlpha = 1f;
        }
        /// <summary>生命血羁绊回血时的蓝色屏幕闪光（供 UI 层绘制）。</summary>
        public float LifebloodFlashAlpha => _active ? _lifebloodFlashAlpha : 0f;

        /// <summary>虚空解放：黑/白屏闪透明度（0~1，供 OnGUI 全屏绘制）。</summary>
        public float VoidFlashAlpha => _active ? _voidFlashAlpha : 0f;

        /// <summary>虚空解放：当前闪屏颜色，true=黑 false=白。</summary>
        public bool VoidFlashBlack => _voidFlashBlack;

        /// <summary>虚空解放：屏幕边缘虚空触手是否显示（黑屏结束 → 白屏开始）。</summary>
        public float VoidTentacleAlpha => _active && _voidTentacleActive ? 1f : 0f;

        /// <summary>虚空解放是否进行中（前摇/出伤/后摇，禁止换人/技能）。</summary>
        public bool IsVoidLiberating => _voidPhase >= 2;

        /// <summary>取第 slot 根触手的随机起始帧偏移（每次出现时重新随机，期间保持）。</summary>
        public int VoidTentacleOffsetFor(int slot)
        {
            if (_voidTentacleOffsets == null || _voidTentacleOffsets.Length == 0)
            {
                return 0;
            }
            return _voidTentacleOffsets[Mathf.Abs(slot) % _voidTentacleOffsets.Length];
        }

        /// <summary>取某根触手当前应显示的帧（当前帧 + 该触手随机偏移，循环；edge 0=底 1=顶 2=左 3=右）。</summary>
        public VoidTentacleFrameInfo GetVoidTentacleFrame(int offset, int edge)
        {
            var info = new VoidTentacleFrameInfo();
            if (!_active || !_voidTentacleActive || _voidTentacleData == null ||
                _voidTentacleData.Length == 0)
            {
                return info;
            }
            int n = _voidTentacleData.Length;
            int baseIdx = (int)(_voidTentacleTimer * VoidTentacleFps) % n;
            int idx = ((baseIdx + offset) % n + n) % n;
            TentacleVariantData v = _voidTentacleData[idx].Variants[Mathf.Clamp(edge, 0, 3)];
            info.Tex = v.Tex;
            info.W = v.W;
            info.H = v.H;
            info.ContentAlong = v.ContentAlong;
            info.EdgeGapFrac = v.EdgeGapFrac;
            return info;
        }

        // ---- 小骑士独立数值（与诺艾尔 UI 完全断开，由骑士模式自己的战斗系统维护）----
        private int _maxHealth = 9;
        private int _health = 9;
        private int _maxSoul = 180;
        private int _soul = 90; // 初始灵魂（读档/新游戏由读档机制设为 90）
        private bool _soulInfinite; // 诺艾尔魔力条为无穷时，小骑士灵魂条同步为无穷

        /// <summary>血量上限：基础 9 格，护符11 坚固心脏 +3、护符30 乔尼的祝福 +4。
        /// 羁绊“生命血”：同时佩戴 28/29/30 时乔尼的 +4 不再生效（仅坚固心脏 +3）。
        /// 束缚·外壳：无加成血量上限降低为 4（护符加成仍按原逻辑叠加）。</summary>
        public int MaxHealth => (CharmEffects.GgMaskBound ? 4 : _maxHealth) +
            (CharmEffects.IsEquipped(CharmEffects.HeartId) ? 3 : 0) +
            (CharmEffects.IsEquipped(CharmEffects.JohnnyId) && !LifebloodBondActive() ? 4 : 0);
        public int Health => _health;
        /// <summary>灵魂上限：束缚·灵魂时降低为 30。</summary>
        public int MaxSoul => CharmEffects.GgSoulBound ? 30 : _maxSoul;
        public int Soul => _soul;
        public bool SoulInfinite => _soulInfinite;

        /// <summary>读取存档/新游戏后，血量回满、灵魂设为 90。</summary>
        public void FillHealthSoul()
        {
            // 28 生命血之心(+2)/29 生命血核心(+4)：佩戴时读档/复活同样带生命血（超上限显示为蓝色）
            _health = MaxHealth + LifebloodBonus();
            _soul = Mathf.Min(MaxSoul, 90); // 束缚·灵魂时上限 30
        }

        /// <summary>读档标记：小骑士实体尚未创建时，首次生成时补一次回满。</summary>
        public static bool ResetOnLoad;

        /// <summary>读档/新游戏标记：换图后直接把小骑士对齐到诺艾尔，跳过渐进跟随。</summary>
        public static bool JustLoadedSave;

        /// <summary>读档后待重绑渲染票据：AIC 读档会重建地图渲染器，旧票据失效。</summary>
        public static bool PendingLoadRebind;

        /// <summary>快速旅行（含原地传送）后待重绑渲染票据：地图材质/渲染器可能被重建。</summary>
        public static bool PendingFastTravelRebind;

        /// <summary>
        /// 受击：血量 -1，进入硬直（0.3s 画面停滞 → 0.2s 击飞），获得 1.3s 无敌。
        /// 由 CombatGuard 在“诺艾尔可被该攻击造成伤害”的拦截点调用。
        /// </summary>
        public void KnightTakeDamage(AttackInfo Atk)
        {
            // 泡泡（NelNSyabon）：地图中的泡泡均不对小骑士造成触碰伤害
            try
            {
                if (Atk is NelAttackInfo naBubble &&
                    (naBubble.Caster is NelNSyabon || naBubble.AttackFrom is NelNSyabon))
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
            // 友好动物（鸡/牛等 NelNMgmFarmAnimal）：小骑士不受其伤害
            try
            {
                if (Atk is NelAttackInfo naFriendly &&
                    (naFriendly.Caster is nel.mgm.farm.NelNMgmFarmAnimal ||
                     naFriendly.AttackFrom is nel.mgm.farm.NelNMgmFarmAnimal))
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
            // 魔法霰弹：即使处于受伤无敌帧，也保证本段霰弹触发一次荆棘反击。
            if (_active && !_isDead && IsShotgunAttack(Atk) &&
                CharmEffects.IsEquipped(CharmEffects.ThornsId) &&
                _shotgunThornsFrame != Time.frameCount)
            {
                _shotgunThornsFrame = Time.frameCount;
                TriggerThornsAgony();
            }
            // 虚空解放：出伤/后摇阶段完全免疫；前摇受击但不打断、不致死（即便 1 血）
            if (IsVoidLiberating)
            {
                if (_voidPhase >= 3)
                {
                    return; // 出伤/后摇：霸体，免疫一切伤害
                }
                if (_voidPhase == 2)
                {
                    // 前摇：整段只受一次伤害，且那次伤害 -1（可减到 0）；后续命中完全忽略
                    if (_voidChargeHurtDone)
                    {
                        return;
                    }
                    _voidChargeHurtDone = true;
                    int baseHurt = (CharmUiController.Instance != null &&
                                    CharmUiController.Instance.Overcharmed) ? 2 : 1;
                    int voidHurt = Mathf.Max(0, baseHurt - 1);
                    if (voidHurt > 0 && _health > 1)
                    {
                        _health = Mathf.Max(1, _health - voidHurt);
                    }
                    if (voidHurt > 0)
                    {
                        // 受伤被动仍触发（幼虫之歌回魂 / 苦痛荆棘反击）
                        if (CharmEffects.IsEquipped(CharmEffects.GrubsongId))
                        {
                            KnightAddSoul(CharmEffects.IsEquipped(CharmEffects.ElegyId) ? 25 : 15);
                        }
                        if (CharmEffects.IsEquipped(CharmEffects.ThornsId))
                        {
                            TriggerThornsAgony();
                        }
                    }
                    _hitVignetteAlpha = 1f;
                    DashAudio.PlayHurt();
                    return; // 不进入硬直、不被打断
                }
            }
            if (!_active || _isDead || _hurt || _invincibleTimer > 0f ||
                _diving || _isSitting || _sitStandingUp)
            {
                // 诊断：下砸无敌期间被命中的攻击（确认蛛丝球伤害来源）
                if (_diving && Time.frameCount - _diveHitLogFrame > 30)
                {
                    _diveHitLogFrame = Time.frameCount;
                    string kind = "?";
                    try
                    {
                        if (Atk is NelAttackInfo na && na.PublishMagic != null)
                        {
                            kind = na.PublishMagic.kind.ToString();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return;
            }
            // 蜂群集结：蜂巢房间魔物中立期间免疫伤害；挑衅后（全房攻击）正常受击
            if (CharmEffects.HiveNeutralActive())
            {
                return;
            }
            // 护符22 巴尔德之壳：凝聚回血期间硬壳抵挡攻击（不扣血、不打断凝聚）
            if (CharmEffects.IsEquipped(CharmEffects.BaldurId) && _baldurActive && _baldurBlocksLeft > 0)
            {
                _baldurBlocksLeft--;
                PlayBaldurClip("BaldurImpact");
                _hitVignetteAlpha = 1f; // 受击反馈（黑边闪一下）
                DashAudio.PlayHurt();
                // 羁绊：坚硬外壳 + 巴尔德之壳 —— 壳挡下攻击同样触发坚硬外壳的无敌延长
                if (CharmEffects.IsEquipped(CharmEffects.SturdyId))
                {
                    _invincibleTimer = HurtInvincibleTime + 2f;
                }
                // 羁绊：苦痛荆棘 + 巴尔德之壳 —— 壳挡下攻击仍触发苦痛荆棘范围伤害
                if (CharmEffects.IsEquipped(CharmEffects.ThornsId))
                {
                    TriggerThornsAgony();
                }
                // 羁绊：巴尔德之壳 + 幼虫之歌 —— 壳挡下攻击不计为受伤，不获得灵魂
                // （此处直接 return，不会走到下方幼虫之歌回魂逻辑）
                if (_baldurBlocksLeft <= 0)
                {
                    // 本场战斗次数耗尽：壳破碎，本次凝聚不再抵挡
                    _baldurActive = false;
                    PlayBaldurClip("BaldurBreak");
                }
                return;
            }
            // 护符43 无忧旋律：受伤时 25% 概率免伤（走完整受击流程，只不掉血）。
            // 未抵挡成功则概率 +25%；抵挡成功后概率回到 25%。
            _melodyBlockedThisHit = false;
            if (CharmEffects.IsEquipped(CharmEffects.MelodyId))
            {
                if (UnityEngine.Random.value < _melodyChance)
                {
                    _melodyChance = 0.25f;
                    _melodyBlockedThisHit = true;
                    DashAudio.PlayTune(); // 守护之歌音效
                    _melodyFlashAlpha = 1f; // 屏幕四周短暂红闪（同回血白闪渲染逻辑）
                }
                else
                {
                    _melodyChance = Mathf.Min(1f, _melodyChance + 0.25f);
                }
            }
            else if (_melodyChance != 0.25f)
            {
                _melodyChance = 0.25f; // 未佩戴时复位，避免再次佩戴带着旧概率
            }
            // 受击会停下超级冲刺（蓄力/飞行/惯性全部清掉）
            StopSuperDashAll();
            // 凝聚被打断：已吸取的灵魂化为乌有
            InterruptFocusByHit();
            // 受击会打断回血音效（只有受击/死亡允许截断回血音）
            DashAudio.StopFocusHeal();
            // 击退/面向方向取自伤害来源，优先级：命中点 > 攻击来源实体 > 施法者本体。
            // 近身、接触类伤害常常没有命中点与 AttackFrom，之前会落到“未知来源”分支，
            // 表现为无论从哪边被打都向左后退。
            // 受击后退方向：只看小骑士当前朝向——面朝左则向右后退，面朝右则向左后退；
            // 朝向本身不改变。face 约定：左=1；击退速度 Vx 正方向为右，故 Vx = _hurtDir * 速度。
            // 受击后退方向：按攻击者相对位置决定——攻击者在左则向右退，在右则向左退；
            // 来源未知时退化为“与当前朝向相反”。face 约定：左=1；Vx 正方向为右，Vx = _hurtDir * 速度。
            float srcX = X;
            bool hasSrc = false;
            if (Atk != null)
            {
                float cand = float.NaN;
                if (Atk.AttackFrom != null)
                {
                    cand = Atk.AttackFrom.x;
                }
                else if (Atk is NelAttackInfo naSrc && naSrc.Caster is M2Mover mvSrc)
                {
                    cand = mvSrc.x;
                }
                else if (Mathf.Abs(Atk.hit_x) > 0.0001f)
                {
                    cand = Atk.hit_x;
                }
                // 距离过近的候选值其实是“命中点/自身”，不能当作攻击者位置（否则方向由噪声决定）
                if (!float.IsNaN(cand) && Mathf.Abs(cand - X) > 1.5f)
                {
                    srcX = cand;
                    hasSrc = true;
                }
            }
            if (!hasSrc && _mp != null)
            {
                // 兜底：攻击信息里没有位置时，扫描附近最近的敌人，按它的位置决定后退方向
                try
                {
                    Vector2 c = _mp.gameObject.transform.TransformPoint(
                        new Vector2(_mp.pixel2ux(X * _mp.CLEN), _mp.pixel2uy(Y * _mp.CLEN)));
                    Collider2D[] near = Physics2D.OverlapCircleAll(c, 5f, GetEnemyOverlapMask());
                    float best = float.MaxValue;
                    for (int i = 0; i < near.Length; i++)
                    {
                        Collider2D nc = near[i];
                        if (nc == null)
                        {
                            continue;
                        }
                        NelEnemy ne = ResolveDamageTarget(nc.GetComponentInParent<NelEnemy>());
                        if (ne == null)
                        {
                            continue;
                        }
                        float d = Mathf.Abs(ne.x - X);
                        if (d < best)
                        {
                            best = d;
                            srcX = ne.x;
                            hasSrc = true;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            if (!hasSrc && MultiplayerCompat.TryGetNearestRemotePlayerX(X, 30f, out float pkx))
            {
                // PvP：伤害包没有攻击者位置时，用最近的远端玩家（诺艾尔/小骑士）
                // 位置决定后退方向，确保背离攻击者而不是按自身朝向后退。
                srcX = pkx;
                hasSrc = true;
            }
            if (hasSrc && Mathf.Abs(srcX - X) > 0.0001f)
            {
                _hurtDir = srcX < X ? 1f : -1f;   // 背离攻击者后退
            }
            else
            {
                _hurtDir = _faceDir != 0f ? _faceDir : 1f;
            }
            _faceDir = _hurtDir;                   // 面向攻击者

            // 被其他玩家攻击：把攻击者代理加入召唤物仇恨表。
            try
            {
                if (Atk is NelAttackInfo naPvp && naPvp.AttackFrom == null &&
                    naPvp.Caster is PR)
                {
                    RegisterNearestPvpAggroNear(X, 20f);
                }
            }
            catch (Exception)
            {
            }

            // 护符过载（总费用 > 11 槽）：每次受伤掉 2 血
            if (!_melodyBlockedThisHit)
            {
                int hurtAmount = (CharmUiController.Instance != null && CharmUiController.Instance.Overcharmed) ? 2 : 1;
                _health = Mathf.Max(0, _health - hurtAmount);
            }
            // 护符3 坚硬外壳：受伤后的无敌时间延长 2 秒（1.3s → 3.3s）
            _invincibleTimer = HurtInvincibleTime +
                (CharmEffects.IsEquipped(CharmEffects.SturdyId) ? 2f : 0f);
            // 护符9 幼虫之歌：受到伤害时获得 15 灵魂；
            // 羁绊：幼虫之歌 + 蜕变挽歌 —— 受伤获得的灵魂改为 25
            // 无忧旋律抵挡成功时不算“受到伤害”，不触发受伤被动（与巴尔德之壳一致）
            if (!_melodyBlockedThisHit && CharmEffects.IsEquipped(CharmEffects.GrubsongId))
            {
                KnightAddSoul(CharmEffects.IsEquipped(CharmEffects.ElegyId) ? 25 : 15);
            }
            // 护符21 苦痛荆棘：受到伤害时，对以骑士为中心、半径3格圆形范围内的
            // 敌人造成“当前两倍普攻伤害”（含护符加成后的当前值）
            if (!_melodyBlockedThisHit && CharmEffects.IsEquipped(CharmEffects.ThornsId) &&
                !(IsShotgunAttack(Atk) && _shotgunThornsFrame == Time.frameCount))
            {
                TriggerThornsAgony();
            }
            _melodyBlockedThisHit = false;
            _hurt = true;
            _hitVignetteAlpha = 1f; // 受击瞬间屏幕四周黑边立即亮起，随后在 Update 中渐出
            _hurtFreezeTimer = HurtFreezeTime;
            _hurtFlyTimer = 0f;
            _dashing = false;
            _dashTimer = 0f;
            // 冲刺时受伤：终止骨钉技艺蓄力与冲刺劈砍（含等待中的）
            _nailArtCharging = false;
            _nailArtCharged = false;
            _nailArtHoldPending = false;
            _nailArtHoldTimer = 0f;
            _nailArtChargeParticles.Clear();
            _nailArtGlowFrames = null;
            _nailArtGlowSprite = null;
            DashAudio.StopNailArtChargeLoop(); // 受伤打断蓄满循环音
            _dashSlashPending = false;
            _dashSlashing = false;
            _dashSlashFxFrames = null;
            _dashSlashFxSprite = null;
            _dashSlashWeedHits.Clear();
            _cycloneSlashing = false; // 受伤立即停止旋风劈砍
            _cycloneTimer = 0f;
            _cycloneFxFrames = null;
            _cycloneFxSprite = null;
            _cycloneWeedHits.Clear();
            // 梦钉期间受伤：终止动画与音效
            if (_dreamNailing)
            {
                CancelDreamNail();
                DashAudio.StopDreamNailSlash();
            }
            _attacking = false;
            _attackTimer = 0f;
            _onWall = false;
            _wallJumpDrift = false;
            _wallKickTimer = 0f;
            _wallKickVx = 0f;
            DestroyHitbox();
            // 受击粒子爆发在判定箱中心（伤害实际注册的位置）。
            // Atk.hit_x/hit_y 是 AIC 的攻击视觉落点（怪物-目标插值），常偏出判定箱，不作粒子位置。
            float hitX = X + HurtCenterX;
            float hitY = Y + HurtCenterY;
            SpawnHitFx(hitX, hitY);
            DashAudio.PlayHurt(); // 空洞骑士原版受击音效

            if (_health <= 0)
            {
                StartDeath();
            }
        }

        /// <summary>
        /// 护符21 苦痛荆棘：以骑士为中心、半径 3 格圆形范围内的所有敌人
        /// 受到“无加成基础普攻伤害 × 2”（不吃护符加成，当前 = 42 × 2 = 84）。
        /// </summary>
        private void TriggerThornsAgony()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            int dmg = (int)(SlashDamage * 1.5f); // 苦痛荆棘：1.5 倍无加成骨钉（42×1.5=63）
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, 3f, mask);
            var hitEnemies = new HashSet<NelEnemy>();
            var hitPlayers = new HashSet<M2Attackable>();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 联机远端玩家代理（M2Gunmu）不是 NelEnemy：
                    // 苦痛荆棘在 PvP 下也要能反击到攻击者。
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                        IsRemoteProxy(ga) && hitPlayers.Add(ga))
                    {
                        ApplyThornsToRemoteProxy(ga, dmg);
                    }
                    continue;
                }
                if (!hitEnemies.Add(enemy))
                {
                    continue;
                }
                try
                {
                    var atk = new NelAttackInfo();
                    atk.hpdmg_current = dmg;
                    atk.hpdmg0 = dmg;
                    atk.fix_damage = true;
                    atk.CenterXy(enemy.x, enemy.y, 0f);
                    PRNoel noel = GetPr();
                    if (noel != null)
                    {
                        atk.Caster = noel;
                        atk.AttackFrom = noel;
                    }
                    atk.PublishMagic = GetKnightAttackMagic();
                    enemy.applyDamage(atk, false);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>是否为诺艾尔的魔法霰弹攻击（PR_SHOTGUN）。</summary>
        private static bool IsShotgunAttack(AttackInfo Atk)
        {
            return Atk is NelAttackInfo na && na.PublishMagic != null &&
                na.PublishMagic.kind == MGKIND.PR_SHOTGUN;
        }

        /// <summary>
        /// 苦痛荆棘对联机远端玩家代理造成伤害：走与骨钉相同的伤害包通道，
        /// 让其他诺艾尔/小骑士玩家正常受伤、扣血并同步结算。
        /// </summary>
        private void ApplyThornsToRemoteProxy(M2Attackable a, int dmg)
        {
            if (a == null)
            {
                return;
            }
            try
            {
                // 苦痛荆棘对诺艾尔玩家的伤害减半；对远端小骑士保持原值。
                if (MultiplayerCompat.IsRemoteNoelProxy(a))
                {
                    dmg = Mathf.Max(1, dmg / 2);
                }
                SetHitFeedback(0); // 荆棘反击 → 轻受击
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                if (IsRemoteProxy(a))
                {
                    RegisterPvpAggro(a);
                }
                DashAudio.PlayEnemyHit();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 受击粒子爆发：在指定命中点（hitX/hitY）生成白色为主 + 黑色虚空粒子。
        /// </summary>
        private void SpawnHitFx(float hx, float hy)
        {
            int count = 36;
            for (int i = 0; i < count; i++)
            {
                bool white = UnityEngine.Random.value < 0.65f;
                _hitFxParticles.Add(new LightDotParticle
                {
                    X = hx + (UnityEngine.Random.value - 0.5f) * 1.6f,
                    Y = hy + (UnityEngine.Random.value - 0.5f) * 1.6f,
                    Vx = (UnityEngine.Random.value - 0.5f) * 0.9f,
                    Vy = (UnityEngine.Random.value - 0.5f) * 0.8f - 0.08f,
                    TexIndex = 1,
                    Age = 0f,
                    Life = UnityEngine.Random.Range(0.3f, 0.55f),
                    Size = white
                        ? UnityEngine.Random.Range(0.20f, 0.42f)
                        : UnityEngine.Random.Range(0.14f, 0.30f),
                    Color = white
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(20, 20, 25, 255),
                    Index = 0
                });
            }
        }

        /// <summary>开始死亡：进入死亡动画 + 黑屏流程。</summary>
        private void StartDeath()
        {
            if (_isDead)
            {
                return;
            }
            _isDead = true;
            StopSuperDashAll(); // 死亡接管：超级冲刺立刻结束
            _focusing = false; // 死亡接管：凝聚立刻结束
            _fireballCasting = false; // 死亡接管：暗影之魂立刻结束
            _fireballs.Clear();
            _flukes.Clear(); // 死亡接管：吸虫之巢的吸虫立刻消失
            _uterusHatchlings.Clear(); // 死亡接管：发光子宫的幼体立刻消失
            _uterusExplosions.Clear(); // 死亡接管：发光子宫的爆炸特效立刻消失
            _sporeClouds.Clear(); // 死亡接管：蘑菇孢子的孢子云立刻消失
            _weaverlings.Clear(); // 死亡接管：编织者之歌的小编织者立刻消失
            _weaverThreads.Clear();
            _fireballBlastFrames = null;
            _fireballBlastSprite = null;
            _screaming = false; // 死亡接管：深渊尖啸立刻结束
            DestroyScreamHitbox();
            _screamBlastFrames = null;
            _screamBlastSprite = null;
            _screamParticles.Clear();
            _diving = false;
            _swimState = 0; // 死亡接管：游泳状态复位
            ResetNailArt(); // 死亡接管：骨钉技艺复位
            ResetDreamNail(); // 死亡接管：梦钉复位
            DashAudio.StopDiveLoop(); // 死亡打断下砸循环音
            _diveSwords.Clear();
            _diveSpikes.Clear();
            _diveLandParticles.Clear();
            if (_focusChargePlaying)
            {
                DashAudio.StopFocusCharge();
                _focusChargePlaying = false;
            }
            DashAudio.StopFocusHeal(); // 死亡打断回血音
            _focusParticles.Clear();
            _focusFlashAlpha = 0f;
            _melodyFlashAlpha = 0f;
            _lifebloodFlashAlpha = 0f;
            _hurt = false;
            _hurtFreezeTimer = 0f;
            _hurtFlyTimer = 0f;
            _invincibleTimer = 0f;
            _hitVignetteAlpha = 0f; // 死亡黑屏接管画面，受击黑边不再叠加
            CancelHitStop(); // 解除可能残留的击飞 hit-stop
            EndActiveBattle(); // 立刻结束当前战斗区域的战斗（清敌/开宝箱/清危险度）
            DashAudio.PlayDeath(); // 空洞骑士原版死亡音效
            _deathTimer = 0f;
            _deathBlackAlpha = 0f;
            _deathBlackPhase = 0f;
            _respawnFadeOut = false;
            Vx = 0f;
            Vy = 0f;
            Grounded = true;
            _dashing = false;
            _attacking = false;
            _onWall = false;
            _taunting = false;
            _tauntTimer = 0f;
            DestroyHitbox();
            _currentClip = null;
        }

        /// <summary>死亡时立刻结束当前战斗区域的战斗（AIC 原生 close：清敌但不判胜利、不发奖励）。</summary>
        private void EndActiveBattle()
        {
            try
            {
                if (EnemySummoner.isActiveBorder())
                {
                    EnemySummoner.ActiveScript.close(true, false);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// “战斗详情界面”（UILpSummon 长按确认框）显示期间，小骑士下砸落地/深渊尖啸释放可直接开战：
        /// 与界面长按确认走同一条 openSummoner 管线（MvFrom=null，跳过边界包含判定）。
        /// 仅在 NearLpSmn 仍指向本图且界面显示（t_ui&gt;0）且战斗尚未开始时生效。
        /// 成功开战额外奖励 30 灵魂。
        /// </summary>
        private void TryOpenNearBattleByDiveOrScream()
        {
            try
            {
                if (_mp == null)
                {
                    return;
                }
                M2LpSummon smn = M2LpSummon.NearLpSmn;
                if (smn == null || smn.Mp != _mp || smn.t_ui <= 0f || smn.isActiveBorder())
                {
                    return;
                }
                smn.openSummoner(null, null, false);
                // 利用下砸/尖啸成功开战：奖励 30 灵魂
                if (smn.isActiveBorder())
                {
                    KnightAddSoul(30);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 挑衅时若“战斗详情界面”（UILpSummon 长按确认框）正在显示，则直接进入战斗。
        /// 与 TryOpenNearBattleByDiveOrScream 同一条 openSummoner 管线，但不奖励灵魂。
        /// </summary>
        private void TryOpenNearBattleByTaunt()
        {
            try
            {
                if (_mp == null)
                {
                    return;
                }
                M2LpSummon smn = M2LpSummon.NearLpSmn;
                if (smn == null || smn.Mp != _mp || smn.t_ui <= 0f || smn.isActiveBorder())
                {
                    return;
                }
                smn.openSummoner(null, null, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>解除受伤画面停滞（AIC 时间轴 + Unity 时间轴）。</summary>
        private void CancelHitStop()
        {
            if (_hurtFrozen)
            {
                _hurtFrozen = false;
                try
                {
                    Map2d.setTimeScale(_hurtPrevTsBase > 0f ? _hurtPrevTsBase : 1f, true);
                }
                catch (Exception)
                {
                }
            }
            if (Mathf.Abs(Time.timeScale) < 0.01f)
            {
                Time.timeScale = 1f;
            }
        }

        /// <summary>在长椅/重生点复活：回满血、灵魂补到 90，短暂无敌。</summary>
        private void RespawnAtBench()
        {
            _health = MaxHealth;
            _soul = Mathf.Min(MaxSoul, Mathf.Max(_soul, 90));
            _invincibleTimer = HurtInvincibleTime;
            bool sitting = false;
            if (_hasRespawn && _respawnMp != null)
            {
                if (_mp == _respawnMp)
                {
                    // 同图：直接传送到重生点
                    X = _respawnX;
                    Y = _respawnY;
                }
                else
                {
                    // 跨图：用 AIC 快速旅行传送（会把诺艾尔传到目标地图的重生点，
                    // 小骑士随后由地图切换的 _pendingReposition 流程跟随过去）
                    try
                    {
                        M2LpMapTransferBase.executeTransferFastTravel(
                            _respawnMp,
                            Mathf.FloorToInt(_respawnX),
                            Mathf.FloorToInt(_respawnY),
                            10);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            else if (CheckGround(out float gt, Y + SizeY))
            {
                Y = gt - SizeY;
            }
            Vx = 0f;
            Vy = 0f;
            Grounded = true;
            // 复活在长椅上坐着，并播放苏醒动画（HK 原版 Wake To Sit）。
            // 直接按“复活点所属地图”找长椅：跨图时目标地图尚未切换过来也能正确找到并坐下
            try
            {
                NelChipBench bench = FindNearBenchIn(_respawnMp, _respawnX, _respawnY);
                if (bench != null)
                {
                    // 复活坐椅：清空地图重定位，确保诺艾尔同步立即恢复、两者碰撞箱对齐
                    _pendingReposition = 0;
                    _transitionPause = false;
                    _isSitting = true;
                    _sitBench = bench;
                    _sitTimer = 9999f; // 直接进入坐姿（跳过坐下过渡）
                    _sitSliding = false;
                    _sitStandingUp = false;
                    _sitStandTimer = 0f;
                    _sitX = bench.mapcx;
                    // 以长椅实际底部（地面）为基准，避免复活点残留的坐标偏差把骑士带歪
                    _sitGroundY = bench.mbottom - SizeY;
                    _sitY = _sitGroundY - 0.3f;
                    X = _sitX;
                    Y = _sitY;
                    float animSpeed = KnightInCradlePlugin.AnimSpeedConfig != null
                        ? KnightInCradlePlugin.AnimSpeedConfig.Value
                        : 1f;
                    _respawnWaking = true;
                    _respawnWakeTimer = GetClipDuration("Wake To Sit") / Mathf.Max(animSpeed, 0.01f);
                    sitting = true;
                }
            }
            catch (Exception)
            {
            }
            if (!sitting)
            {
                _respawnWaking = false;
                _respawnWakeTimer = 0f;
            }
            _currentClip = null;
            RefreshHudAfterRespawn();
        }

        /// <summary>
        /// 复活后强制刷新 HUD / 左侧诺艾尔人像：清除低血量红色背景等残留状态。
        /// </summary>
        private void RefreshHudAfterRespawn()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr != null)
                {
                    pr.Ser.Cure(SER.HP_REDUCE);
                    pr.recheck_emot = true;
                }
                if (UIStatus.Instance != null)
                {
                    UIStatus.Instance.fineHpRatio(true, true);
                    UIStatus.Instance.fineMpRatio(true, true);
                    UIStatus.Instance.redraw_hp = true;
                    UIStatus.Instance.redraw_mp = true;
                    UIStatus.Instance.redraw_bar_num = true;
                    UIStatus.Instance.redraw_gage = true;
                    UIStatus.Instance.redraw_gage_back = true;
                }
                UIPicture.Recheck(60);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 死亡状态机：0.5s 黑屏淡入（死亡动画）→ 1s 全黑保持 → 长椅复活 + 0.8s 黑屏淡出。
        /// </summary>
        private void HandleDeathState()
        {
            _deathTimer += Time.deltaTime;
            if (_deathBlackPhase == 0f)
            {
                _deathBlackAlpha = Mathf.Clamp01(_deathTimer / DeathBlackInTime);
                if (_deathTimer >= DeathBlackInTime)
                {
                    _deathBlackPhase = 1f;
                    _deathTimer = 0f;
                }
            }
            else if (_deathBlackPhase == 1f)
            {
                _deathBlackAlpha = 1f;
                if (_deathTimer >= DeathBlackHoldTime)
                {
                    _deathBlackPhase = 2f;
                    _deathTimer = 0f;
                    RespawnAtBench(); // 全黑期间回到长椅并回满
                    _isDead = false;
                    _respawnFadeOut = true;
                    _respawnFadeTimer = DeathBlackOutTime;
                }
            }
        }

        /// <summary>击飞物理：水平撞墙停、垂直重力 + 落地。</summary>
        private void ApplyHurtPhysics(float dt)
        {
            X += Vx * dt;
            if (HitWall())
            {
                X -= Vx * dt;
                Vx = 0f;
            }
            Vy += Gravity * dt;
            if (Vy > FallMax)
            {
                Vy = FallMax;
            }
            float prevFeet = Y + SizeY;
            Y += Vy * dt;
            if (Vy < 0f)
            {
                if (HitCeil())
                {
                    Y -= Vy * dt;
                    Vy = 0f;
                }
            }
            else if (CheckGround(out float groundTop, prevFeet))
            {
                Y = groundTop - SizeY;
                Vy = 0f;
                Grounded = true;
            }
            else
            {
                Grounded = false;
            }
        }

        /// <summary>回血：_health += 1（上限 MaxHealth）。</summary>
        public void KnightHeal()
        {
            _health = Mathf.Min(MaxHealth, _health + 1);
        }

        /// <summary>坐在椅子上：直接回满。</summary>
        public void KnightRestoreAll()
        {
            _health = MaxHealth;
        }

        /// <summary>
        /// 28 生命血之心(+2)/29 生命血核心(+4)：将当前血量设为上限+生命血
        /// （不改变上限，超上限血条变蓝）。
        /// </summary>
        public void KnightApplyLifeblood()
        {
            _health = MaxHealth + LifebloodBonus();
        }

        /// <summary>生命血加成：佩戴 28 时 +2、佩戴 29 时 +4（可叠加）。
        /// 生命血羁绊（28+29+30）激活时不再提供额外血量，血量固定为基础/坚固心脏上限。</summary>
        private static int LifebloodBonus()
        {
            if (LifebloodBondActive())
            {
                return 0;
            }
            int bonus = 0;
            if (CharmEffects.IsEquipped(CharmEffects.BlueHeart1Id))
            {
                bonus += 2;
            }
            if (CharmEffects.IsEquipped(CharmEffects.BlueHeart2Id))
            {
                bonus += 4;
            }
            return bonus;
        }

        /// <summary>
        /// 生命血羁绊：同时佩戴 28 生命血之心 + 29 生命血核心 + 30 乔尼的祝福。
        /// 生命上限不再受乔尼 +4 影响；小骑士身后出现蓝色闪烁，每 7 秒回 1 血。
        /// </summary>
        public static bool LifebloodBondActive()
        {
            return CharmEffects.IsEquipped(CharmEffects.BlueHeart1Id) &&
                CharmEffects.IsEquipped(CharmEffects.BlueHeart2Id) &&
                CharmEffects.IsEquipped(CharmEffects.JohnnyId);
        }

        /// <summary>将当前血量钳制到有效上限（护符11 坚固心脏卸下时使用）。</summary>
        public void ClampHealthToMax()
        {
            if (_health > MaxHealth)
            {
                _health = MaxHealth;
            }
        }

        /// <summary>将当前灵魂钳制到有效上限（束缚·灵魂时使用）。</summary>
        public void ClampSoulToMax()
        {
            if (_soul > MaxSoul)
            {
                _soul = MaxSoul;
            }
        }

        /// <summary>获得灵魂（砍中敌人/植物 +10，击杀 +10），上限 MaxSoul（束缚·灵魂时 30）。</summary>
        public void KnightAddSoul(int amount)
        {
            _soul = Mathf.Min(MaxSoul, _soul + amount);
        }

        // ---------- 凝聚回血（Focus） ----------

        /// <summary>当前发动阶段时长（秒）：34 乌恩之形为 0.2s。</summary>
        private float FocusStartTimeNow()
        {
            return CharmEffects.IsEquipped(CharmEffects.UnnId) ? FocusStartTimeUnn : FocusStartTime;
        }

        /// <summary>当前爆发段时长（秒）：34 乌恩之形为 0.2s。</summary>
        private float FocusBurstTimeNow()
        {
            return CharmEffects.IsEquipped(CharmEffects.UnnId) ? FocusBurstTimeUnn : FocusBurstTime;
        }

        /// <summary>
        /// 当前回血阶段时长（秒）：34 乌恩之形 0.82s；26 快速聚集 -0.3s、27 深度聚集 +0.55s，可叠加。
        /// </summary>
        private float FocusHealTimeNow()
        {
            float t = CharmEffects.IsEquipped(CharmEffects.UnnId) ? FocusHealTimeUnn : FocusHealTime;
            if (CharmEffects.IsEquipped(CharmEffects.FastGatherId))
            {
                t += FocusFastMod;
            }
            if (CharmEffects.IsEquipped(CharmEffects.DeepGatherId))
            {
                t += FocusDeepMod;
            }
            return t;
        }

        /// <summary>当前后续循环回血段时长（秒）：34 乌恩之形 0.82s；26/27 修正可叠加。</summary>
        private float FocusHealDupTimeNow()
        {
            float t = CharmEffects.IsEquipped(CharmEffects.UnnId) ? FocusHealDupTimeUnn : FocusHealDupTime;
            if (CharmEffects.IsEquipped(CharmEffects.FastGatherId))
            {
                t += FocusFastMod;
            }
            if (CharmEffects.IsEquipped(CharmEffects.DeepGatherId))
            {
                t += FocusDeepMod;
            }
            return t;
        }

        /// <summary>
        /// 是否允许开始凝聚：站立地面、无其它动作、灵魂足够（满血也可用，会白扣灵魂）。
        /// 30 乔尼的祝福：佩戴时无法使用凝聚回复生命。
        /// </summary>
        private bool CanStartFocus()
        {
            return Grounded && !_hurt && !_attacking && !_dashing && !_onWall &&
                   !_wallJumping && !_doubleJumping && !_isSitting && !_sitStandingUp &&
                   !_isDead && !_respawnFadeOut && !_fireballCasting && !_screaming &&
                   !_nailArtSlashing && !_dreamNailing && _soul >= FocusSoulCost &&
                   !CharmEffects.IsEquipped(CharmEffects.JohnnyId);
        }

        /// <summary>
        /// 每帧调用一次（坐在长椅/受伤/死亡等会提前 goto 跳过，因此不会与其它状态并发）。
        /// 按下凝聚键立即开始俯身（含 0.26s 长按判定期），之后 0.3s 俯身 → 0.83s 消耗灵魂 → 回血。
        /// </summary>
        private void UpdateFocus(bool focusHeld, float dt)
        {
            if (!_focusing)
            {
                if (focusHeld && CanStartFocus())
                {
                    StartFocus();
                }
                return;
            }

            // 凝聚中必须站在地上；受击/死亡/坐椅等异常直接取消
            if (!Grounded || _hurt || _isDead || _isSitting || _sitStandingUp)
            {
                CancelFocus();
                return;
            }

            // 凝聚期间持续生成自下往上的白色条状线
            SpawnFocusFx();

            if (_focusPhase == 0) // 发动阶段：focus_v020000~0002，0.27s，不耗魂
            {
                _focusTimer -= Time.deltaTime;
                if (_focusTimer <= 0f)
                {
                    // 发动结束：进入回血阶段
                    _focusPhase = 1;
                    _focusTimer = FocusHealTimeNow() + FocusBurstTimeNow();
                    _focusSoulDrained = 0f;
                    _currentClip = null; // 切到回血阶段动画
                }
                else if (!focusHeld)
                {
                    // 发动期间松开：尚未消耗灵魂，直接起身，无硬直无损耗
                    CancelFocus();
                }
            }
            else if (_focusPhase == 1) // 回血阶段：消耗灵魂
            {
                // 回血段（首次 0003~0006 一次 / 后续 0003~0006×2）→ 爆发段（0007~0010，0.25s）
                float healPortion = _focusFirstCycle ? FocusHealTimeNow() : FocusHealDupTimeNow();
                float phaseTotal = healPortion + FocusBurstTimeNow();
                float prevDrain = _focusSoulDrained;
                _focusTimer -= Time.deltaTime;
                _focusSoulDrained = Mathf.Min(
                    FocusSoulCost,
                    _focusSoulDrained + FocusSoulCost * (Time.deltaTime / healPortion));
                // 实时按整数边界扣灵魂：HUD 灵魂条随消耗阶段逐渐下降（原版手感）
                int drainedNow = Mathf.FloorToInt(_focusSoulDrained);
                int drainedPrev = Mathf.FloorToInt(prevDrain);
                if (drainedNow > drainedPrev)
                {
                    if (!_soulInfinite)
                    {
                        _soul = Mathf.Max(0, _soul - (drainedNow - drainedPrev));
                    }
                }
                // 进入爆发段（0007）瞬间回血 + 播放回血音效 + 白闪（首次与后续循环都一样）
                if (!_focusBurstFired && _focusTimer <= FocusBurstTimeNow())
                {
                    _focusBurstFired = true;
                    if (_health < MaxHealth)
                    {
                        // 27 深度聚集：一次回 2 血（上限 MaxHealth）
                        int focusHeal = CharmEffects.IsEquipped(CharmEffects.DeepGatherId) ? 2 : 1;
                        _health = Mathf.Min(MaxHealth, _health + focusHeal);
                    }
                    // 32 蘑菇孢子：凝聚完成（无论是否满血）都释放孢子云
                    SpawnSporeCloud();
                    DashAudio.PlayFocusHeal();
                    _focusFlashAlpha = 1f;
                    _currentClip = null; // 切到爆发段动画（0007~0010）
                }
                if (_focusTimer <= 0f)
                {
                    DecideFocusContinue(focusHeld);
                }
                else if (!focusHeld)
                {
                    // 未完成就松开：已吸取灵魂 ≤10 返还（已过爆发段则已回血，不返还）
                    bool healApplied = _focusBurstFired;
                    int drained = Mathf.FloorToInt(_focusSoulDrained);
                    if (!healApplied && drained <= FocusSoulRefundMax)
                    {
                        _soul = Mathf.Min(MaxSoul, _soul + drained); // 返还
                    }
                    EndFocusFree();
                }
            }
        }

        /// <summary>开始凝聚：锁定输入、播放发动动画；满血也能开始（完成时会白扣 30 灵魂）。</summary>
        private void StartFocus()
        {
            _focusing = true;
            _focusPhase = 0;
            _focusTimer = FocusStartTimeNow();
            _focusSoulDrained = 0f;
            _focusFirstCycle = true;
            _focusBurstFired = false;
            _focusFxSpawnTimer = 0f;
            _attacking = false;
            _attackTimer = 0f;
            _dashing = false;
            _dashTimer = 0f;
            _onWall = false;
            Vx = 0f;
            Vy = 0f;
            Grounded = true;
            _currentClip = null; // 凝聚动画从头播放
            DestroyHitbox();
            // 凝聚持续音循环播放 + 白色条状线开始生成
            _focusChargePlaying = true;
            DashAudio.PlayFocusCharge();
            // 护符22 巴尔德之壳：凝聚开始展开硬壳
            StartBaldurShell();
        }

        /// <summary>消耗灵魂阶段完成：扣 30 灵魂并回 1 格血，然后决定继续/停止。</summary>
        /// <summary>回血/爆发段结束：回血已在爆发段完成，这里只决定继续或结束。</summary>
        private void DecideFocusContinue(bool focusHeld)
        {
            _focusSoulDrained = 0f;
            _focusBurstFired = false;
            bool cont = focusHeld && _soul >= FocusSoulCost;
            if (cont)
            {
                _focusFirstCycle = false;
                _focusPhase = 1;
                _focusTimer = FocusHealDupTimeNow() + FocusBurstTimeNow();
                _currentClip = null; // 后续循环（回血段+爆发段）从头播放
            }
            else
            {
                EndFocusFree();
            }
        }

        /// <summary>
        /// 回血结束：解除凝聚锁（无硬直），紧接着补播 focus_v020011（0007~0010 已作为爆发段播完）。
        /// </summary>
        private void EndFocusFree()
        {
            _focusing = false; // 取消硬直：立即恢复自由行动
            // 停止凝聚持续音循环（否则回血结束后循环音会一直残留）
            if (_focusChargePlaying)
            {
                DashAudio.StopFocusCharge();
                _focusChargePlaying = false;
            }
            _focusSoulDrained = 0f;
            _focusPhase = 0;
            _focusTimer = 0f;
            _focusBurstFired = false;
            // 护符34 乌恩之形：回血结束播放变身回来动画，否则播普通收尾帧；
            // 羁绊：乌恩之形 + 蘑菇孢子 —— 用蘑菇蛞蝓变身回来动画
            string clipName = "FocusEndLast";
            if (CharmEffects.IsEquipped(CharmEffects.UnnId))
            {
                bool mushUnn = CharmEffects.IsEquipped(CharmEffects.MushroomId) &&
                    _clips.ContainsKey("MushUnnTransformBack");
                clipName = (mushUnn || _clips.ContainsKey("UnnTransformBack"))
                    ? (mushUnn ? "MushUnnTransformBack" : "UnnTransformBack")
                    : "FocusEndLast";
            }
            if (_clips.TryGetValue(clipName, out ClipData clip) && clip.frames.Length > 0)
            {
                _focusEnding = true;
                _focusEndClip = clipName;
                _focusEndFrames = clip.frames;
                _focusEndFps = clip.fps;
                _focusEndIndex = 0;
                _focusEndTimer = 0f;
                _currentClip = null; // 从结束帧第一张开始播
            }
            else
            {
                _focusEnding = false;
                _focusEndClip = null;
                _focusEndFrames = null;
            }
        }

        /// <summary>回血结束动画推进：播完自动回到普通状态（期间可自由行动）。</summary>
        private void UpdateFocusEndAnim(float dt)
        {
            if (!_focusEnding || _focusEndFrames == null || _focusEndFrames.Length == 0)
            {
                return;
            }
            _focusEndTimer += dt;
            float frameTime = 1f / Mathf.Max(_focusEndFps, 0.001f);
            int guard = 30;
            while (_focusEndTimer >= frameTime && guard-- > 0)
            {
                _focusEndTimer -= frameTime;
                _focusEndIndex++;
            }
            if (_focusEndIndex >= _focusEndFrames.Length)
            {
                _focusEnding = false;
                _focusEndClip = null;
                _focusEndFrames = null;
                _currentClip = null; // 回到普通状态动画
            }
        }

        /// <summary>立即结束凝聚（干净取消：不扣灵魂、无硬直），恢复自由控制。</summary>
        private void CancelFocus()
        {
            if (!_focusing)
            {
                return;
            }
            _focusing = false;
            _focusPhase = 0;
            _focusTimer = 0f;
            _focusSoulDrained = 0f;
            _focusBurstFired = false;
            if (_focusChargePlaying)
            {
                DashAudio.StopFocusCharge();
                _focusChargePlaying = false;
            }
            _focusParticles.Clear();
            _focusFxSpawnTimer = 0f;
            _currentClip = null; // 强制回到普通状态动画
            // 护符22 巴尔德之壳：凝聚结束收起硬壳
            EndBaldurShell();
        }

        /// <summary>受击打断凝聚：消耗阶段已吸取的灵魂全部损失（化为乌有，不返还）。</summary>
        private void InterruptFocusByHit()
        {
            if (!_focusing)
            {
                return;
            }
            // 消耗阶段已实时扣掉的灵魂即“化为乌有”，无需再扣；未消耗的部分不动
            CancelFocus();
        }

        // ---------- 护符22 巴尔德之壳 ----------

        /// <summary>凝聚开始：展开硬壳（需装备且本场战斗仍有抵挡次数）。</summary>
        private void StartBaldurShell()
        {
            if (!CharmEffects.IsEquipped(CharmEffects.BaldurId) || _baldurBlocksLeft <= 0)
            {
                return;
            }
            _baldurActive = true;
            if (_clips.TryGetValue("BaldurAppear", out ClipData appear) && appear.frames.Length > 0)
            {
                _baldurHeldFrame = appear.frames[appear.frames.Length - 1];
            }
            PlayBaldurClip("BaldurAppear");
        }

        /// <summary>凝聚结束：播放收起动画并关闭壳（壳已破碎时无动作）。</summary>
        private void EndBaldurShell()
        {
            if (!_baldurActive)
            {
                return;
            }
            _baldurActive = false;
            PlayBaldurClip("BaldurDisappear");
        }

        /// <summary>播放壳的一次性动画（Appear/Impact/Break/Disappear）。</summary>
        private void PlayBaldurClip(string clipName)
        {
            if (!_clips.TryGetValue(clipName, out ClipData clip) || clip.frames.Length == 0)
            {
                return;
            }
            _baldurFxFrames = clip.frames;
            _baldurFxFps = clip.fps;
            _baldurFxIndex = 0;
            _baldurFxTimer = 0f;
            _baldurFxPlaying = true;
            _baldurFxSprite = clip.frames[0];
        }

        /// <summary>壳动画推进：一次性剪辑播完回到持有帧（壳破碎/收起后消失）。</summary>
        private void UpdateBaldurShellFx(float dt)
        {
            // 兜底：凝聚被直接终止（死亡/坐椅等未走 CancelFocus）时收起壳
            if (_baldurActive && !_focusing)
            {
                EndBaldurShell();
            }
            if (!_baldurFxPlaying || _baldurFxFrames == null)
            {
                return;
            }
            _baldurFxTimer += dt;
            float frameTime = 1f / Mathf.Max(_baldurFxFps, 0.001f);
            int guard = 30;
            while (_baldurFxTimer >= frameTime && guard-- > 0)
            {
                _baldurFxTimer -= frameTime;
                _baldurFxIndex++;
            }
            if (_baldurFxIndex >= _baldurFxFrames.Length)
            {
                _baldurFxPlaying = false;
                _baldurFxSprite = _baldurActive ? _baldurHeldFrame : null;
            }
            else
            {
                _baldurFxSprite = _baldurFxFrames[_baldurFxIndex];
            }
        }

        // ---------- 水晶之心（超级冲刺）----------

        private static void InitSuperDashKey()
        {
            if (_superDashKeyInit)
            {
                return;
            }
            _superDashKeyInit = true;
            if (KnightInCradlePlugin.SuperDashKey != null &&
                Enum.TryParse(KnightInCradlePlugin.SuperDashKey.Value, out KeyCode k))
            {
                _superDashKey = k;
            }
            // 配置成 Ctrl / LeftControl / RightControl 时，左右 Ctrl 都生效
            if (_superDashKey == KeyCode.LeftControl || _superDashKey == KeyCode.RightControl)
            {
                _superDashKey = KeyCode.LeftControl;
                _superDashAltKey = KeyCode.RightControl;
            }
        }

        private static bool SuperDashHeld()
        {
            InitSuperDashKey();
            return UnityEngine.Input.GetKey(_superDashKey) ||
                   (_superDashAltKey != KeyCode.None && UnityEngine.Input.GetKey(_superDashAltKey));
        }

        private static bool SuperDashPressed()
        {
            InitSuperDashKey();
            return UnityEngine.Input.GetKeyDown(_superDashKey) ||
                   (_superDashAltKey != KeyCode.None && UnityEngine.Input.GetKeyDown(_superDashAltKey));
        }

        /// <summary>能否开始蓄力：地面或攀附墙壁，且不在攻击/冲刺/凝聚等状态。</summary>
        private bool CanStartSuperCharge()
        {
            return !_hurt && !_isDead && !_respawnFadeOut && !_isSitting && !_sitStandingUp &&
                   !_focusing && !_fireballCasting && !_screaming && !_attacking && !_dashing &&
                   !_doubleJumping && !_wallJumping && !_nailArtSlashing && !_dreamNailing &&
                   (Grounded || _onWall);
        }

        /// <summary>每帧调用一次：蓄力 / 飞行 / 惯性后的开始判定。</summary>
        private void UpdateSuperDash(bool superHeld, bool superPressed, bool jump, bool upHeld, float dt)
        {
            // 过图传送/重定位阶段：蓄力与冲刺整体冻结，
            // 状态保留，重定位结束后按原方向继续（跨房间延续）
            if (_pendingReposition > 0 || _transitionPause)
            {
                return;
            }
            if (_superCharging)
            {
                _superChargeTime += Time.deltaTime;
                if (!_superCharged && _superChargeTime >= SuperDashChargeTime)
                {
                    _superCharged = true;
                    // 蓄满瞬间：立即从外围生成第一圈紫色收缩光圈
                    _sdRingSpawnTimer = SdRingSpawnInterval;
                    SpawnSuperRing();
                    DashAudio.PlaySuperReady();
                }
                UpdateSuperCrystalFx();
                Vx = 0f;
                if (_onWall)
                {
                    // 攀附时蓄力停止下滑；频繁按超级冲刺键让下滑更慢
                    if (superPressed)
                    {
                        _superWallPump = SuperWallPumpTime;
                    }
                    if (_superWallPump > 0f)
                    {
                        _superWallPump -= Time.deltaTime;
                        Vy = SuperSlidePumpSpeed;
                    }
                    else
                    {
                        Vy = SuperSlideSlowSpeed;
                    }
                }
                else
                {
                    Vy = 0f;
                    Grounded = true;
                }
                if (!superHeld)
                {
                    if (_superCharged)
                    {
                        LaunchSuperDash(upHeld);
                    }
                    else
                    {
                        CancelSuperCharge();
                    }
                }
                return;
            }

            if (_superDashing)
            {
                UpdateSuperDashFlight(superPressed, jump, dt);
                return;
            }

            // 开始蓄力判定（按下键即开始，松开取消/发射）
            if (superPressed && CanStartSuperCharge())
            {
                StartSuperCharge();
            }
        }

        private void StartSuperCharge()
        {
            _superCharging = true;
            _superChargeTime = 0f;
            _superCharged = false;
            _sdRingSpawnTimer = 0f;
            _sdRings.Clear();
            _superWallPump = 0f;
            _superUnchargeTimer = 0f;
            _superDashTime = 0f;
            _sdCrystalShown = 1; // 按下立即出现第一个
            Vx = 0f;
            Vy = 0f;
            _currentClip = null;
            DashAudio.PlaySuperCharge();
        }

        private void CancelSuperCharge()
        {
            _superCharging = false;
            _superCharged = false;
            _sdRings.Clear();
            _sdCrystalShown = 0;
            _superUnchargeTimer = GetClipDuration("SD Charge Ground End");
            _currentClip = null;
        }

        /// <summary>蓄满能量松开：向前发射。</summary>
        private void LaunchSuperDash(bool upDash)
        {
            _superCharging = false;
            _superCharged = false;
            _superDashing = true;
            // 水晶升腾（向上超级冲刺）：仅地面 + 松开瞬间按住上键（Q）触发
            _superUpDash = upDash && Grounded && !_onWall;
            if (!_superUpDash)
            {
                // 发射方向：墙上蓄力向远离墙的方向，地面蓄力按面朝方向
                _superDashDir = _onWall ? -_wallDir : -_faceDir;
                _faceDir = -_superDashDir;
            }
            _superDashTime = 0f;
            _superUpDashWallGrace = 0;
            _superStartLock = SuperDashStartLock;
            _superInertiaTimer = 0f;
            _superBrakeTimer = 0f;
            _superWallHitTimer = 0f;
            _superDashHit.Clear();
            _sdCrystalShown = 0;
            if (!_superUpDash)
            {
                _sdTrailSprite = "superdash_effect0000_appear";
                _sdTrailTimer = 0f;
            }
            _onWall = false;
            Grounded = false; // 飞行中不落地，撞墙后可直接进入爬墙
            if (!_superUpDash)
            {
                // 发射点抬升：避免刚发射就贴地撞上地面小凸起
                Y -= SuperDashFlyLift;
            }
            Vx = 0f;
            Vy = 0f;
            _currentClip = null;
            DashAudio.PlaySuperBurst();
            DashAudio.PlaySuperLoop();
            // 起始爆发：后方水晶碎屑 + 白色特效 + 较大范围 21 伤害
            PlaySuperFxOnce("SD Fx Burst");
            SuperDashStartBurst();
        }

        private void UpdateSuperDashFlight(bool superPressed, bool jump, float dt)
        {
            // 过图传送/重定位阶段：冻结冲刺（不移动、不判定、不停止），
            // 状态保留到重定位结束，随后按原方向继续（水晶升腾跨房间延续）
            if (_pendingReposition > 0 || _transitionPause)
            {
                return;
            }
            _superDashTime += Time.deltaTime;
            if (_superStartLock > 0f)
            {
                _superStartLock -= Time.deltaTime;
            }
            // 主动停止：跳跃键或超级冲刺键（起步锁定期内无效）
            if (_superStartLock <= 0f && (jump || superPressed))
            {
                // 按跳跃停止：本帧不再触发跳跃/蹬墙跳/二段跳
                _superJumpStop = jump && !superPressed;
                if (_superUpDash)
                {
                    StopSuperDashUp("主动停止");
                }
                else
                {
                    StopSuperDash(true);
                }
                return;
            }
            // 水晶升腾：向正上方以 20 格/秒直线飞行
            if (_superUpDash)
            {
                Y -= SuperUpDashSpeed * dt;
                if (SuperUpFrontBlocked())
                {
                    Y += SuperUpDashSpeed * dt;
                    if (_superUpDashWallGrace == 0)
                    {
                        // 第一次撞墙：开始宽限（门口上方的墙可能是转房触发区，先等转房而不是立刻停）
                        _superUpDashWallGrace = 15;
                    }
                    _superUpDashWallGrace--;
                    if (_superUpDashWallGrace <= 0)
                    {
                        _superUpDashWallGrace = 0;
                        StopSuperDashUp("撞墙");
                        return;
                    }
                    return;
                }
                CheckSuperUpDashEnemyHit();
                if (_hurt)
                {
                    // 撞到未死的敌人：KnightTakeDamage 已停止冲刺并进入硬直
                    _superDashing = false;
                    _superUpDash = false;
                    _sdTrailSprite = null;
                    DashAudio.StopSuperLoop();
                }
                return;
            }
            // 直线飞行（无重力）；只检测冲刺方向前方的墙（避免被侧面的墙误停）
            X += _superDashDir * SuperDashSpeed * dt;
            if (SuperDashFrontBlocked())
            {
                X -= _superDashDir * SuperDashSpeed * dt;
                StopSuperDash(false); // 地形阻挡 → 立刻停 + 撞击特效
                // 撞到可攀爬的墙：立刻进入爬墙状态（螳螂爪）
                TryEnterWallClimbAfterSuperDash();
                return;
            }
            UpdateSuperTrailFx();
            CheckSuperDashEnemyHit();
            if (_hurt)
            {
                // 撞到未死的敌人：KnightTakeDamage 已停止冲刺并进入硬直
                _superDashing = false;
                _sdTrailSprite = null;
                DashAudio.StopSuperLoop();
            }
        }

        /// <summary>
        /// 水晶升腾停止：撞墙或主动停止都播放刹车音效（hero_super_dash_air_brake），
        /// 随后恢复重力正常下落；不进入横向冲刺的惯性滑行。
        /// </summary>
        private void StopSuperDashUp(string reason = "停止")
        {
            _superDashing = false;
            _superUpDash = false;
            _superStartLock = 0f;
            _superBrakeTimer = 0f;
            _superInertiaTimer = 0f;
            DashAudio.StopSuperLoop();
            DashAudio.PlaySuperBrake();
            _currentClip = null;
        }

        /// <summary>水晶升腾：正上方是否有墙（整条身体宽度范围）。</summary>
        private bool SuperUpFrontBlocked()
        {
            if (_mp == null)
            {
                return false;
            }
            int cx0 = Mathf.FloorToInt(X + CollideX0);
            int cx1 = Mathf.FloorToInt(X + CollideX1);
            int cy = Mathf.FloorToInt(Y + CollideTopY) - 1;
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (IsBlockCell(cx, cy))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>骑士受击箱是否嵌进了墙格（用于上冲入口微调校验）。</summary>
        private bool KnightBodyBlocked()
        {
            if (_mp == null)
            {
                return false;
            }
            int cx0 = Mathf.FloorToInt(X + CollideX0);
            int cx1 = Mathf.FloorToInt(X + CollideX1);
            int cy0 = Mathf.FloorToInt(Y + CollideTopY);
            int cy1 = Mathf.FloorToInt(Y + CollideY1);
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    if (IsBlockCell(cx, cy))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>水晶升腾：与水晶之心相同的撞敌判定（判定箱置于骑士上方 0.65 格）。</summary>
        private void CheckSuperUpDashEnemyHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my + 0.65f));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(1.4f, 1.6f), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                // 友好动物（鸡/牛）：超级冲刺直接无视，不碰撞、不掉血、不伤害它们
                if (enemy == null || enemy is nel.mgm.farm.NelNMgmFarmAnimal || !_superDashHit.Add(enemy))
                {
                    continue;
                }
                ApplySuperDashDamage(enemy, true);
                if (_hurt)
                {
                    break;
                }
            }
        }

        /// <summary>超级冲刺撞墙停下后，若墙面就在冲刺方向且可攀爬，立刻进入爬墙状态。</summary>
        private void TryEnterWallClimbAfterSuperDash()
        {
            if (_mp == null)
            {
                return;
            }
            int cx = _superDashDir > 0f
                ? Mathf.FloorToInt(X + CollideX1) + 1
                : Mathf.FloorToInt(X + CollideX0) - 1;
            // 与 HitWall 一致：从头顶扫到脚底
            int cyTop = Mathf.FloorToInt(Y + CollideTopY);
            int cyBot = Mathf.FloorToInt(Y + SizeY);
            bool wallFound = false;
            for (int cy = cyTop; cy <= cyBot; cy++)
            {
                if (IsBlockCell(cx, cy))
                {
                    wallFound = true;
                    break;
                }
            }
            if (!wallFound)
            {
                return;
            }
            _onWall = true;
            _wallDir = _superDashDir; // 墙在冲刺方向
            _faceDir = -1f; // 与普通爬墙一致的朝向（左右墙动画由 clip 区分）
            Grounded = false; // 保持非落地，避免下一帧爬墙维持逻辑判落地退出
            _superWallHitTimer = SuperWallHitTime; // 撞击停顿 0.5s
            // 吸附到墙面边缘
            if (_superDashDir > 0f)
            {
                X = cx - CollideX1;
            }
            else
            {
                X = cx + 1 - CollideX0;
            }
            Vx = 0f;
            _canDoubleJump = true;
            _canDash = true;
            _dashCooldown = 0f;
            _currentClip = null;
        }

        /// <summary>超级冲刺方向前方是否撞墙（只查冲刺方向一侧的整条身体）。</summary>
        private bool SuperDashFrontBlocked()
        {
            if (_mp == null)
            {
                return false;
            }
            int cx = _superDashDir > 0f
                ? Mathf.FloorToInt(X + CollideX1)
                : Mathf.FloorToInt(X + CollideX0);
            int cyTop = Mathf.FloorToInt(Y + CollideTopY);
            int cyBot = Mathf.FloorToInt(Y + SizeY);
            // 与 HitWall 一致：排除脚底行（飞行已抬升，脚不贴地）
            for (int cy = cyTop; cy < cyBot; cy++)
            {
                if (IsBlockCell(cx, cy))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 停止超级冲刺。inertia=true：主动停止，保留惯性漂移片刻（期间不自动攀附）；
        /// false：撞墙/受阻，立刻停下并播撞击特效。
        /// </summary>
        private void StopSuperDash(bool inertia)
        {
            _superDashing = false;
            _sdTrailSprite = null;
            _superStartLock = 0f;
            DashAudio.StopSuperLoop();
            if (inertia)
            {
                _superInertiaTimer = SuperInertiaTime;
                _superInertiaDir = _superDashDir;
                _superBrakeTimer = GetClipDuration("SD Air Brake");
                DashAudio.PlaySuperBrake();
            }
            else
            {
                _superInertiaTimer = 0f;
                _superBrakeTimer = 0f;
                PlaySuperFxOnce("SD Break");
                DashAudio.PlaySuperImpact();
            }
            _currentClip = null;
        }

        /// <summary>播放超级冲刺一次性特效（发射爆发 / 撞击碎裂），走独立特效网格。</summary>
        private void PlaySuperFxOnce(string clipName)
        {
            if (_clips.TryGetValue(clipName, out ClipData clip) && clip.frames.Length > 0)
            {
                _sdFxFrames = clip.frames;
                _sdFxFps = clip.fps;
                _sdFxTimer = 0f;
                _sdFxSprite = clip.frames[0];
            }
        }

        /// <summary>每帧推进一次性特效帧；播完自动清空。</summary>
        private void UpdateSuperFx()
        {
            if (_sdFxFrames == null || _sdFxFrames.Length == 0)
            {
                _sdFxSprite = null;
                return;
            }
            _sdFxTimer += Time.deltaTime;
            int idx = (int)(_sdFxTimer * _sdFxFps);
            if (idx >= _sdFxFrames.Length)
            {
                _sdFxFrames = null;
                _sdFxSprite = null;
                return;
            }
            _sdFxSprite = _sdFxFrames[idx];
        }

        /// <summary>受击/死亡/切回等彻底清掉超级冲刺状态。</summary>
        private void StopSuperDashAll()
        {
            _superCharging = false;
            _superDashing = false;
            _superUpDash = false;
            _superCharged = false;
            _sdRings.Clear();
            _superWallPump = 0f;
            _superUnchargeTimer = 0f;
            _superInertiaTimer = 0f;
            _superBrakeTimer = 0f;
            _superWallHitTimer = 0f;
            _sdCrystalShown = 0;
            _sdTrailSprite = null;
            DashAudio.StopSuperLoop();
        }

        /// <summary>蓄力水晶帧推进：成长 → 待机 → 蓄满后闪光循环。</summary>
        private void UpdateSuperCrystalFx()
        {
            // 以小骑士中心为基准：水晶向两侧一帧一帧对称出现并保留，
            // 蓄力完成时 8 帧水晶两侧全部出现
            _sdCrystalShown = Mathf.Clamp(
                Mathf.FloorToInt(_superChargeTime / SuperDashChargeTime * 8f) + 1, 0, 8);
            // 蓄满后：持续一圈圈生成从外围收到中心的紫色光圈
            if (_superCharged)
            {
                _sdRingSpawnTimer -= Time.deltaTime;
                if (_sdRingSpawnTimer <= 0f)
                {
                    SpawnSuperRing();
                    _sdRingSpawnTimer = SdRingSpawnInterval;
                }
            }
        }

        /// <summary>生成一圈紫色收缩光圈（从自身外围收到中心）。</summary>
        private void SpawnSuperRing()
        {
            _sdRings.Add(new SdRing
            {
                Age = 0f,
                Life = SdRingLife,
                StartR = SdRingStartR,
                EndR = SdRingEndR
            });
        }

        // ================= 暗影之魂（法术·火球）=================

        /// <summary>能否发射暗影之魂：有足够灵魂、不在攻击/冲刺/凝聚/爬墙等状态。</summary>
        private bool CanCastFireball()
        {
            return _soul >= CharmEffects.SpellSoulCost() && _fireballCooldown <= 0f &&
                   !_isSitting && !_sitStandingUp && !_isDead && !_respawnFadeOut &&
                   !_hurt && !_focusing && !_fireballCasting && !_diving && !_screaming &&
                   !_dashing && !_nailArtSlashing && !_dreamNailing &&
                   !_superCharging && !_superDashing && _pendingReposition <= 0;
        }

        /// <summary>施法时终止下劈回弹：清零上升速度与重力锁，让法术动画/滞空正常接管。</summary>
        private void CancelPogoRebound()
        {
            _pogoGravityLock = 0f;
            if (Vy < 0f)
            {
                Vy = 0f; // y 负 = 向上，正在回弹上升则清零
            }
        }

        /// <summary>
        /// 攻击（普攻/法术/骨钉技艺）触发时终止蹬墙跳：清掉蹬墙跳状态与水平推力，
        /// 让攻击优先于蹬墙跳动画。
        /// </summary>
        private void CancelWallJumpForAttack()
        {
            if (!_wallJumping && _wallKickTimer <= 0f)
            {
                return;
            }
            _wallJumping = false;
            _wallJumpTimer = 0f;
            _wallKickTimer = 0f;
            _wallKickVx = 0f;
            _wallJumpDrift = false;
        }

        /// <summary>点按凝聚键：立即消耗 30 灵魂，向前发射冲击波，并滞空 0.5s + 后坐 0.2 格。</summary>
        private void CastFireball()
        {
            if (!_soulInfinite)
            {
            _soul = Mathf.Max(0, _soul - CharmEffects.SpellSoulCost());
            }
            _fireballCasting = true;
            _fireballCooldown = FireballCooldown;
            CancelPogoRebound(); // 下劈回弹期间施法：终止回弹
            // 二段跳/爬墙期间施法：立即终止对应动画状态，避免被动画锁住
            _doubleJumping = false;
            _doubleJumpTimer = 0f;
            _onWall = false;
            CancelWallJumpForAttack(); // 蹬墙跳期间施法：终止蹬墙跳，优先施法
            // 普攻中施法：立即终止普攻动画并清掉判定框/剑气
            _attacking = false;
            _attackTimer = 0f;
            _swingHits.Clear();
            DestroyHitbox();
            _fxTex = null;
            _fireballCastTimer = GetClipDuration("Fireball Cast");
            _fireballHoverTimer = FireballHoverTime;
            // 面朝约定：_faceDir<0=面朝右、>0=面朝左（与精灵镜像相反），发射方向取反
            // 新增一个冲击波（支持连续施法：旧冲击波继续飞行，不被覆盖）
            var proj = new FireballProj();
            proj.Dir = -_faceDir;
            // 统一起点（世界坐标，Y 向下为正、X 向右为正，格为单位）：
            // 骑士面朝前方 0.9 格，Y 偏移 +0.1 格（胸口高度）
            proj.X = X + proj.Dir * 0.9f;
            proj.Y = Y + 0.1f;
            proj.AnimTimer = 0f;
            proj.Sprite = null;
            // 护符23 吸虫之巢：不放冲击波，改为放出 16 只黑色吸虫
            bool nest = CharmEffects.IsEquipped(CharmEffects.NestId);
            if (nest)
            {
                SpawnFlukes(proj.Dir);
            }
            else
            {
                _fireballs.Add(proj);
            }
            _fireballDir = proj.Dir; // blast 特效镜像用
            // 后坐力：0.15s 内向后移动 10 像素
            _fireballRecoilTimer = FireballRecoilTime;
            _fireballRecoilVx = -proj.Dir * ((FireballRecoilDistPx / _mp.CLEN) / FireballRecoilTime);
            _currentClip = null; // 立即切到施法动画
            // 施法瞬间：面前 0.5 格播放一次爆炸特效（fireball_lvl_02_blast_effect）
            // 吸虫之巢不播火球爆发特效（吸虫本身就是视觉）
            if (!nest && _clips.TryGetValue("Fireball Blast", out ClipData blastClip) &&
                blastClip.frames.Length > 0)
            {
                _fireballBlastFrames = blastClip.frames;
                _fireballBlastFps = blastClip.fps;
                _fireballBlastTimer = 0f;
                _fireballBlastSprite = blastClip.frames[0];
                // 位置：按发射方向对称（左冲 X-0.1、右冲 X+0.1），Y 上移 0.6（Y 向下为正）
                _fireballBlastX = X + proj.Dir * 0.1f;
                if (proj.Dir > 0f)
                {
                    _fireballBlastX += 1.2f; // 向右释放：整体再右移 1.2 格
                }
                _fireballBlastY = Y + 0.1f - 0.6f;
            }
            if (nest)
            {
                DashAudio.PlayFlukeCast();
            }
            else
            {
                DashAudio.PlayFireballCast();
            }
            // 释放冲击波：镜头轻微震动一下（强度 2，约 8 帧 ≈ 0.13s）
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.Cam != null)
                {
                    m2d.Cam.setQuake(2f, 8, 1f, 0);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>每帧推进暗影之魂：投射物飞行/动画/撞敌/出图移除，以及骑士滞空与后坐。</summary>
        private void UpdateFireball(float dt)
        {
            // 注意：外部传入的 dt = Time.deltaTime*60（帧数），此处一律用真实秒
            float sdt = Time.deltaTime;
            // 施法冷却计时
            if (_fireballCooldown > 0f)
            {
                _fireballCooldown -= sdt;
                if (_fireballCooldown < 0f)
                {
                    _fireballCooldown = 0f;
                }
            }

            // 骑士施法状态：滞空 + 后坐（动画播完即结束，冲击波独立继续飞）
            if (_fireballCasting)
            {
                _fireballHoverTimer -= sdt;
                _fireballCastTimer -= sdt;
                Vy = 0f; // 滞空：施法期间垂直速度清零（下方 goto 跳过重力/落地物理段，即悬浮）
                if (_fireballRecoilTimer > 0f)
                {
                    _fireballRecoilTimer -= sdt;
                    float rx = _fireballRecoilVx * sdt;
                    X += rx;
                    if (HitWall())
                    {
                        X -= rx;
                    }
                }
                if (_fireballCastTimer <= 0f && _fireballHoverTimer <= 0f)
                {
                    _fireballCasting = false;
                    _currentClip = null;
                }
            }

            // 冲击波投射物（可多个同时存在）：独立于施法状态，穿墙直线飞行，
            // 直到整个判定框都在房间外才移除。
            for (int fi = _fireballs.Count - 1; fi >= 0; fi--)
            {
                FireballProj proj = _fireballs[fi];
                proj.X += proj.Dir * FireballSpeed * sdt;
                UpdateFireballAnim(proj, sdt);
                CheckFireballHit(proj);
                // 整个冲击波（含渲染框）都在房间外：才移除
                float halfW = FireballHitboxW * 0.5f;
                float halfH = FireballHitboxH * 0.5f;
                if (proj.Sprite != null &&
                    _textures.TryGetValue(proj.Sprite, out Texture2D rtex))
                {
                    float scale = KnightInCradlePlugin.ScaleConfig != null
                        ? KnightInCradlePlugin.ScaleConfig.Value
                        : 0.42f;
                    halfW = Mathf.Max(halfW,
                        (rtex.width * scale + FireballRenderPadX * _mp.CLEN) * 0.5f / _mp.CLEN);
                    halfH = Mathf.Max(halfH,
                        (rtex.height * scale + FireballRenderPadY * _mp.CLEN) * 0.5f / _mp.CLEN);
                }
                if (_mp != null &&
                    (proj.X + halfW < 0f || proj.X - halfW > _mp.width ||
                     proj.Y + halfH < 0f || proj.Y - halfH > _mp.rows))
                {
                    _fireballs.RemoveAt(fi);
                }
            }

            // 施法瞬间爆发特效帧推进（一次性，播完自动清空）
            UpdateFireballBlast(sdt);
        }

        // ---------- 护符23 吸虫之巢 ----------

        /// <summary>吸虫之巢：放出 16 只黑色吸虫（向前 + 随机上下初速，乱蹦乱跳）。</summary>
        private void SpawnFlukes(float dir)
        {
            // 不清理旧吸虫：连续施法时新旧吸虫同时存在（各自寿命 2~3 秒后消失）
            for (int i = 0; i < FlukeCount; i++)
            {
                var fl = new FlukeProj();
                fl.Dir = dir;
                fl.X = X + dir * UnityEngine.Random.Range(0.3f, 1.3f);
                fl.Y = Y + UnityEngine.Random.Range(-0.3f, 0.7f);
                fl.Vx = dir * UnityEngine.Random.Range(12f, 16f);
                fl.Vy = UnityEngine.Random.Range(-8f, 1f); // Y 向下为正：-8~0 向上，最多 1 向下
                fl.Life = UnityEngine.Random.Range(FlukeLifeMin, FlukeLifeMax);
                fl.AnimTime = UnityEngine.Random.Range(0f, 1f); // 错开动画相位
                _flukes.Add(fl);
            }
        }

        /// <summary>每帧推进吸虫：重力/落地弹跳/碰敌/寿命（HK SpellFluke 风格）。</summary>
        private void UpdateFlukes(float sdt)
        {
            for (int i = _flukes.Count - 1; i >= 0; i--)
            {
                FlukeProj fl = _flukes[i];
                fl.Life -= sdt;
                if (fl.Life <= 0f)
                {
                    _flukes.RemoveAt(i);
                    continue;
                }
                fl.AnimTime += sdt;
                // 重力
                fl.Vy += FlukeGravity * sdt;
                fl.X += fl.Vx * sdt;
                fl.Y += fl.Vy * sdt;
                // 落地弹跳：脚部接触地面时随机横向速度 + 向上弹起（HK SpellFluke 逻辑）
                if (fl.Vy > 0f && FlukeGroundY(fl.Y + 0.35f, fl.X, out float gy))
                {
                    float feet = fl.Y + 0.35f;
                    if (feet >= gy - 0.35f && feet <= gy + 0.55f)
                    {
                        fl.Y = gy - 0.35f;
                        fl.Vx = UnityEngine.Random.Range(-4f, 4f);
                        fl.Vy = -UnityEngine.Random.Range(6f, 12f);
                        fl.Flopping = true;
                        fl.AnimTime = 0f;
                        DashAudio.PlayFlukeBounce();
                    }
                }
                else if (fl.Flopping && fl.AnimTime >= GetClipDuration("FlukeFlop"))
                {
                    fl.Flopping = false;
                }
                // 碰到敌人：造成 7 伤害后消失
                CheckFlukeHit(fl);
            }
        }

        /// <summary>吸虫脚部是否踩到地面（复用地图 BCC 地板检测）。</summary>
        private bool FlukeGroundY(float feetY, float x, out float groundTop)
        {
            groundTop = float.NaN;
            Map2d mp = FlukeMp; // 切回诺艾尔后仍要继续检测地面，所以用保留的地图引用
            if (mp == null || mp.BCC == null)
            {
                return false;
            }
            try
            {
                BCCLine line;
                float qy = feetY - 0.30f;
                float g = mp.BCC.isFallable(x, qy, 0.15f, 0.35f, out line, true, true, -1f, null);
                if (g >= 0f)
                {
                    groundTop = g;
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>吸虫碰到敌人：造成 7 伤害后消失。</summary>
        private void CheckFlukeHit(FlukeProj fl)
        {
            Map2d mp = FlukeMp; // 切回诺艾尔后仍要继续判定命中
            if (mp == null)
            {
                return;
            }
            float mx = mp.pixel2ux(fl.X * mp.CLEN);
            float my = mp.pixel2uy(fl.Y * mp.CLEN);
            Vector2 center = mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            // 本地诺艾尔：切回诺艾尔后，骑士放出的吸虫仍在飞 —— 同样要能命中诺艾尔本人。
            // 玩家碰撞体不在 GetEnemyOverlapMask() 的层里，所以单独用诺艾尔自己的碰撞体判定。
            if (TryHitLocalNoelByFluke(center))
            {
                _flukes.Remove(fl); // 碰到诺艾尔即消失
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, FlukeHitRadius, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 联机远端玩家代理（其他诺艾尔/小骑士）也会被小吸虫命中并受伤。
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                        ApplyFlukeToGeneric(ga, FlukeDamageNow()))
                    {
                        _flukes.Remove(fl); // 碰到玩家即消失
                    }
                    continue;
                }
                try
                {
                    ApplyFlukeDamage(enemy);
                    // 25% 概率再造成一次伤害
                    if (UnityEngine.Random.value < 0.25f)
                    {
                        ApplyFlukeDamage(enemy);
                    }
                    _flukes.Remove(fl); // 碰到敌人即消失
                }
                catch (Exception)
                {
                }
                break;
            }
        }

        /// <summary>对敌人施加一次吸虫伤害（7）。</summary>
        private void ApplyFlukeDamage(NelEnemy enemy)
        {
            // 吸虫命中森之领主：五触手抓取窗口内直接使其虚弱（与普攻/法术同一打断管线）
            TryNusiBurstInterrupt(enemy);
            // 羁绊：萨满之石 + 吸虫之巢 —— 小吸虫伤害由 7 提升为 9
            int dmg = FlukeDamageNow();
            var atk = new NelAttackInfo();
            atk.hpdmg_current = dmg;
            atk.hpdmg0 = dmg;
            atk.fix_damage = true;
            atk.CenterXy(enemy.x, enemy.y, 0f);
            PRNoel noel = GetPr();
            if (noel != null)
            {
                atk.Caster = noel;
                atk.AttackFrom = noel;
            }
            atk.PublishMagic = GetKnightAttackMagic();
            enemy.applyDamage(atk, false);
        }

        /// <summary>当前小吸虫伤害（萨满之石+吸虫之巢羁绊 9，否则 7）。</summary>
        private int FlukeDamageNow()
        {
            return (CharmEffects.IsEquipped(CharmEffects.ShamanId) &&
                    CharmEffects.IsEquipped(CharmEffects.NestId)) ? 9 : FlukeDamage;
        }

        /// <summary>
        /// 吸虫命中联机远端玩家/通用可攻击实体：走伤害包通道，并在包里写吸虫专用标记。
        /// 标记用于“致命吸虫最后一击 → 左侧立绘虫墙战败动画”。
        /// </summary>
        private bool ApplyFlukeToGeneric(M2Attackable a, int dmg)
        {
            if (a == null)
            {
                return false;
            }
            try
            {
                SetHitFeedback(0);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.burst_center = FlukePacketMarker;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                if (IsRemoteProxy(a)) RegisterPvpAggro(a);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 吸虫是否命中“本地诺艾尔”（小骑士放完吸虫后切回诺艾尔，吸虫仍在飞）。
        /// 骑士模式下诺艾尔是隐藏的宿主，此时不判定，避免刚放出的吸虫立刻打到自己。
        /// 诺艾尔受击碰撞体的世界包围盒向外扩一个吸虫半径后做圆-盒粗判。
        /// </summary>
        private bool TryHitLocalNoelByFluke(Vector2 flukeWorldCenter)
        {
            if (KnightInCradlePlugin.KnightModeActive)
            {
                return false;
            }
            PRNoel pr = GetPr();
            if (pr == null)
            {
                return false;
            }
            try
            {
                M2MvColliderCreator cc = pr.getColliderCreator();
                PolygonCollider2D col = cc != null ? cc.Cld : null;
                if (col == null || !col.enabled)
                {
                    return false;
                }
                Bounds bb = col.bounds;
                if (bb.size.x <= 1e-4f || bb.size.y <= 1e-4f)
                {
                    return false; // 碰撞体尚未重建/尺寸为 0，避免退化成原点小方块
                }
                Vector3 ext = new Vector3(FlukeHitRadius, FlukeHitRadius, 0f);
                Vector3 lo = bb.min - ext;
                Vector3 hi = bb.max + ext;
                if (flukeWorldCenter.x < lo.x || flukeWorldCenter.x > hi.x ||
                    flukeWorldCenter.y < lo.y || flukeWorldCenter.y > hi.y)
                {
                    return false;
                }
                return ApplyFlukeToLocalNoel(pr);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 吸虫命中本地诺艾尔：走诺艾尔自己的伤害管线（<c>PR.applyDamage</c>），
        /// 并在攻击包里写吸虫标记 <see cref="FlukePacketMarker"/>。
        /// CombatGuard 的 <c>M2PrADmg.applyDamage</c> 后缀据此执行与远端诺艾尔完全相同的收包逻辑：
        /// 左侧立绘切“虫墙” + 结算吸虫奖励（新鲜诺艾尔汁 / 诺艾尔的卵）。
        /// <c>fix_damage = true</c> 保证伤害不按 HP 状态砍半（与打怪/远端诺艾尔一致）。
        /// </summary>
        private bool ApplyFlukeToLocalNoel(PRNoel pr)
        {
            if (pr == null)
            {
                return false;
            }
            try
            {
                int dmg = FlukeDamageNow();
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.burst_center = FlukePacketMarker;
                atk.CenterXy(pr.x, pr.y, 0f);
                atk.Caster = pr;
                atk.AttackFrom = pr;
                atk.PublishMagic = GetKnightAttackMagic();
                pr.applyDamage(atk, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>暗影冲刺每次冲刺对同一远端玩家只命中一次；按实例+对象名双去重。</summary>
        private bool RegisterShadowDashPlayerHit(M2Attackable ga)
        {
            if (ga == null)
            {
                return false;
            }
            string key = ga.gameObject != null && !string.IsNullOrEmpty(ga.gameObject.name)
                ? ga.gameObject.name
                : ga.GetInstanceID().ToString();
            return _shadowDashPlayerHits.Add(ga) && _shadowDashPlayerKeys.Add(key);
        }

        // ---------- 护符24 防御者纹章：蓝色魔力云 ----------

        /// <summary>
        /// 每帧推进法阵：圆环计时/扩张、到达边缘触发发光 + 随机图案、图案/闪光计时。
        /// </summary>
        private void UpdateShelterMagic(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.ShelterId))
            {
                _shelterCircleEntered.Clear();
                _shelterCircleEnteredPlayers.Clear();
                return;
            }
            // 五个深蓝球体：2.5 秒公转一周，72° 均布
            _shelterSphereAngle += (6.2831853f / ShelterSpherePeriod) * sdt;
            // 实心圆伤害：进入领域立即 1 点；之后每 1.5s 仍在领域内再 1 点（均可击晕）
            _shelterDamageTimer -= sdt;
            if (_shelterDamageTimer <= 0f)
            {
                _shelterDamageTimer = ShelterCircleTick;
                ShelterCircleDamageTick();
            }
            // 每帧检测新进入领域的敌人：立即造成 1 点伤害（可击晕）
            ShelterCircleEntryCheck();
            // 实心圆范围：破坏山蜘蛛 BOSS 的蜘蛛陷阱与蛛丝球（碰掉即销毁）
            ShelterDestroySpiderThings();
            // 圆环发射计时：每 3s 从中心发射一道
            _shelterRingTimer -= sdt;
            if (_shelterRingTimer <= 0f)
            {
                _shelterRingTimer = ShelterRingInterval;
                _shelterRingRadius = 0f;
            }
            // 圆环以 9 格/秒向外扩张；到达实心圆外围 → 变纯白消失 + 边缘发光 + 随机图案
            if (_shelterRingRadius >= 0f)
            {
                if (_shelterRingRadius < ShelterCircleRadius)
                {
                    _shelterRingRadius += ShelterRingSpeed * sdt;
                    if (_shelterRingRadius >= ShelterCircleRadius)
                    {
                        // 到达边缘：圆环停在边缘、立即生成图案（无淡入）、边缘发光
                        _shelterRingRadius = ShelterCircleRadius;
                        _shelterFlashTimer = ShelterFlashTime;
                        _shelterPatternType = (_shelterPatternType + 1) % 3; // 按顺序：星→方→叉
                        _shelterPatternTimer = 0f; // 开始 0.2s 保持 + 0.3s 淡出
                    }
                }
                else
                {
                    // 圆环与图案一起：保持 0.2s 满亮度 → 0.3s 淡出
                    _shelterPatternTimer += sdt;
                    if (_shelterPatternTimer >= ShelterPatternHoldTime + ShelterPatternFadeTime)
                    {
                        _shelterRingRadius = -1f;
                        _shelterPatternTimer = -1f;
                    }
                }
            }
            // 边缘发光计时
            if (_shelterFlashTimer > 0f)
            {
                _shelterFlashTimer -= sdt;
            }
        }

        /// <summary>每 1.5s：对仍在领域内的敌人造成 1 点伤害（完整受击，可击晕）。</summary>
        private void ShelterCircleDamageTick()
        {
            foreach (NelEnemy enemy in _shelterCircleEntered)
            {
                if (enemy == null || !enemy.is_alive)
                {
                    continue;
                }
                ApplyShelterHitDamage(enemy, (int)ShelterCircleDamage);
            }
            foreach (M2Attackable player in _shelterCircleEnteredPlayers)
            {
                if (player == null)
                {
                    continue;
                }
                ApplyShelterToGeneric(player, (int)ShelterCircleDamage);
            }
        }

        /// <summary>
        /// 每帧检测：新进入领域的敌人立即受到 1 点伤害（完整受击，可击晕），
        /// 并刷新“已在领域内”集合（离开后再进入会再次触发）。
        /// </summary>
        private void ShelterCircleEntryCheck()
        {
            // 换图/地图销毁瞬间 _mp 可能仍非空但 gameObject 已被销毁，必须一并保护
            if (_mp == null || _mp.gameObject == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, ShelterCircleRadius, mask);
            var insideNow = new HashSet<NelEnemy>();
            var insidePlayersNow = new HashSet<M2Attackable>();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                        IsRemoteProxy(ga) && insidePlayersNow.Add(ga))
                    {
                        if (!_shelterCircleEnteredPlayers.Contains(ga))
                        {
                            ApplyShelterToGeneric(ga, (int)ShelterCircleDamage);
                        }
                    }
                    continue;
                }
                if (!insideNow.Add(enemy))
                {
                    continue;
                }
                if (!_shelterCircleEntered.Contains(enemy))
                {
                    // 刚进入领域：立即造成 1 点伤害（可击晕）
                    ApplyShelterHitDamage(enemy, (int)ShelterCircleDamage);
                }
            }
            // 只保留本帧仍在领域内的敌人
            _shelterCircleEntered.Clear();
            _shelterCircleEntered.UnionWith(insideNow);
            _shelterCircleEnteredPlayers.Clear();
            _shelterCircleEnteredPlayers.UnionWith(insidePlayersNow);
        }

        /// <summary>法阵伤害：完整受击管线（受击硬直/击晕，压制击飞）。</summary>
        private void ApplyShelterHitDamage(NelEnemy enemy, int dmg)
        {
            try
            {
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f; // 大负值压制击飞，仅保留受击硬直
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>防御者纹章法阵命中联机远端玩家代理。</summary>
        private void ApplyShelterToGeneric(M2Attackable a, int dmg)
        {
            if (a == null)
            {
                return;
            }
            try
            {
                SetHitFeedback(0);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f;
                atk.burst_center = ShelterPacketMarker;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                if (IsRemoteProxy(a)) RegisterPvpAggro(a);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 实心圆范围：破坏山蜘蛛 BOSS 的蜘蛛陷阱（MgBsSpiderTrap）与蛛丝球（MgNWebShot），
        /// 进入圆内即销毁。
        /// </summary>
        private void ShelterDestroySpiderThings()
        {
            if (_mp == null)
            {
                return;
            }
            // 蜘蛛陷阱：以圆形范围判定（半径 = 实心圆半径）
            DestroySpiderTrapsInBox(new Vector2(X, Y),
                ShelterCircleRadius, ShelterCircleRadius, ShelterCircleRadius, false);
        }

        // ---------- 护符25 发光子宫：幼体 ----------

        /// <summary>护符31 蜂巢之血：每 10 秒回复 1 血量（上限 MaxHealth，不回复生命血）。</summary>
        private void UpdateHiveHeal(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.HiveId))
            {
                _hiveHealTimer = HiveHealInterval;
                return;
            }
            _hiveHealTimer -= sdt;
            if (_hiveHealTimer <= 0f)
            {
                _hiveHealTimer = HiveHealInterval;
                if (_health < MaxHealth)
                {
                    _health = Mathf.Min(MaxHealth, _health + 1);
                }
            }
        }

        /// <summary>
        /// 生命血羁绊：每 7 秒回 1 血（上限 MaxHealth，不回复生命血），
        /// 回血瞬间屏幕四周蓝色短闪（复用回血闪光管线）。
        /// </summary>
        private void UpdateLifebloodHeal(float sdt)
        {
            if (!LifebloodBondActive())
            {
                _lifebloodHealTimer = LifebloodHealInterval;
                return;
            }
            _lifebloodHealTimer -= sdt;
            if (_lifebloodHealTimer <= 0f)
            {
                _lifebloodHealTimer = LifebloodHealInterval;
                if (_health < MaxHealth)
                {
                    _health = Mathf.Min(MaxHealth, _health + 1);
                    _lifebloodFlashAlpha = 1f; // 屏幕周围蓝色短闪
                }
            }
        }

        /// <summary>护符41 国王之魂：每 2 秒恢复 4 点灵魂（上限 180）。</summary>
        private void UpdateKingsoulSoul(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.KingsoulId))
            {
                _kingsoulTimer = KingsoulInterval;
                return;
            }
            _kingsoulTimer -= sdt;
            if (_kingsoulTimer <= 0f)
            {
                _kingsoulTimer = KingsoulInterval;
                KnightAddSoul(KingsoulAmount);
            }
        }

        /// <summary>护符32 蘑菇孢子：凝聚回血成功时，在小骑士中心爆发浅绿+黄色孢子粒子（持续 4.1s）。</summary>
        private void SpawnSporeCloud()
        {
            if (!CharmEffects.IsEquipped(CharmEffects.MushroomId))
            {
                return;
            }
            var cloud = new SporeCloud
            {
                X = X + HurtCenterX,
                Y = Y + HurtCenterY
            };
            // 粒子：浅绿 120 + 黄 120，从中心爆发，初速 1~12 格/秒、半径 0.06~0.09 格，方向随机；
            // 羁绊：深度聚集 + 蘑菇孢子 —— 两种颜色各增加 60 个粒子
            int perColor = SporeParticlePerColor +
                ((CharmEffects.IsEquipped(CharmEffects.DeepGatherId) &&
                  CharmEffects.IsEquipped(CharmEffects.MushroomId)) ? 60 : 0);
            for (int i = 0; i < perColor * 2; i++)
            {
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                cloud.Particles.Add(new SporeParticle
                {
                    DirX = Mathf.Cos(ang),
                    DirY = Mathf.Sin(ang),
                    Speed = UnityEngine.Random.Range(1f, 12f),
                    Radius = UnityEngine.Random.Range(0.06f, 0.09f),
                    Color = i < perColor ? 0 : 1,
                    Layer = UnityEngine.Random.value < 0.8f ? 0 : 1, // 80% 身后 / 20% 身前
                    DriftT = UnityEngine.Random.Range(0.3f, 0.6f)
                });
            }
            _sporeClouds.Add(cloud);
        }

        /// <summary>
        /// 护符32 蘑菇孢子：推进孢子云与粒子——
        /// 0.7s 扩张（云初速 10→0、尺寸 0→1.75；粒子初速 1~8→0），之后随机飘动/转动；
        /// 前 4.1s 对领域内敌人造成伤害（进入 1 次、每 0.5s 1 次、每次 5 伤害）；
        /// 4.1s 后 0.3s 淡出。云与粒子不随骑士移动。
        /// </summary>
        private void UpdateSporeClouds(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.MushroomId))
            {
                _sporeClouds.Clear(); // 卸下护符：孢子云消失
                return;
            }
            if (_sporeClouds.Count == 0)
            {
                return;
            }
            if (_mp == null || _mp.gameObject == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            try
            {
                for (int i = _sporeClouds.Count - 1; i >= 0; i--)
                {
                    SporeCloud c = _sporeClouds[i];
                    c.Age += sdt;
                    if (c.Age >= SporeTotalLife)
                    {
                        _sporeClouds.RemoveAt(i);
                        continue;
                    }
                    // 伤害窗口：前 4.1 秒
                    if (c.Age <= SporeLife)
                    {
                        float mx = _mp.pixel2ux(c.X * _mp.CLEN);
                        float my = _mp.pixel2uy(c.Y * _mp.CLEN);
                        Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                        Collider2D[] hits = Physics2D.OverlapCircleAll(center, SporeRadiusNow(), mask);
                        if (hits != null)
                        {
                            for (int j = 0; j < hits.Length; j++)
                            {
                                Collider2D col = hits[j];
                                if (col == null)
                                {
                                    continue;
                                }
                                NelEnemy enemy = col.GetComponentInParent<NelEnemy>();
                                enemy = ResolveDamageTarget(enemy);
                                if (enemy == null)
                                {
                                    M2Attackable ga = col.GetComponentInParent<M2Attackable>();
                                    if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                                        IsRemoteProxy(ga))
                                    {
                                        if (!c.EnteredPlayers.Contains(ga))
                                        {
                                            c.EnteredPlayers.Add(ga);
                                            c.NextHitPlayers[ga] = c.Age + SporeTick;
                                            ApplySporeToGeneric(ga, SporeDamageNow());
                                        }
                                        else if (c.NextHitPlayers.TryGetValue(ga, out float np) &&
                                                 c.Age >= np)
                                        {
                                            c.NextHitPlayers[ga] = c.Age + SporeTick;
                                            ApplySporeToGeneric(ga, SporeDamageNow());
                                        }
                                    }
                                    continue;
                                }
                if (!c.Entered.Contains(enemy))
                {
                    // 首次进入：立即 1 次伤害，并记录下一次伤害时间
                    c.Entered.Add(enemy);
                    c.NextHit[enemy] = c.Age + SporeTick;
                    ApplyKnightAreaDamage(enemy, SporeDamageNow());
                }
                else if (c.NextHit.TryGetValue(enemy, out float next) && c.Age >= next)
                {
                    c.NextHit[enemy] = c.Age + SporeTick;
                    ApplyKnightAreaDamage(enemy, SporeDamageNow());
                }
                            }
                        }
                    }
                    // 扩张结束后：粒子开始极小的随机飘动
                    if (c.Age >= SporeExpandTime)
                    {
                        for (int pi = 0; pi < c.Particles.Count; pi++)
                        {
                            SporeParticle p = c.Particles[pi];
                            p.DriftT -= sdt;
                            if (p.DriftT <= 0f)
                            {
                                p.DriftT = UnityEngine.Random.Range(0.3f, 0.6f);
                                float dang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                                float dspd = UnityEngine.Random.Range(0.05f, 0.18f);
                                p.DriftVx = Mathf.Cos(dang) * dspd;
                                p.DriftVy = Mathf.Sin(dang) * dspd;
                            }
                            p.DriftX += p.DriftVx * sdt;
                            p.DriftY += p.DriftVy * sdt;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符36 编织者之歌：三只小编织者跟随小骑士贴地爬行，
        /// 周期性朝附近敌人发射蛛丝（7 伤害）。
        /// </summary>
        private void UpdateWeaverlings(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.SpiderId))
            {
                _weaverlings.Clear();
                _weaverThreads.Clear();
                _weaverRespawnDelay = 0f;
                return;
            }
            // 过图后：等小骑士在新地图稳定 3 秒，再生成小编织者
            // （传送不设倒计时，仍按原来立即生成）
            if (_weaverRespawnDelay > 0f)
            {
                _weaverRespawnDelay -= sdt;
                if (_weaverRespawnDelay > 0f)
                {
                    return;
                }
            }
            // 维持 3 只
            while (_weaverlings.Count < WeaverCount)
            {
                int widx = _weaverlings.Count;
                _weaverlings.Add(new Weaverling
                {
                    X = X + (widx - 1) * 1.3f,
                    Y = Y + SizeY - WeaverHalfH,
                    State = 0,
                    AnimTime = 0f,
                    // 三只固定错开站位：-1.3 / 0 / +1.3，避免叠在一起
                    HomeOx = (widx - 1) * 1.3f,
                    HomeOy = 0f,
                    Face = UnityEngine.Random.value < 0.5f ? 1 : -1,
                    AttackCd = UnityEngine.Random.Range(0.4f, 1.4f),
                    MeleeCd = UnityEngine.Random.Range(0.6f, 1.8f),
                    MeleeAnimT = 0f,
                    ThreadFired = false,
                    WanderT = UnityEngine.Random.Range(0.3f, 1f),
                    WanderX = X + (UnityEngine.Random.value - 0.5f) * 2f,
                    HopT = 0f,
                    HopVy = 0f,
                    NextHopT = UnityEngine.Random.Range(1f, 3f)
                });
                // 出生直接贴地
                Weaverling newborn = _weaverlings[_weaverlings.Count - 1];
                float ngy = GetWeaverGroundY(newborn.X, newborn.Y);
                if (!float.IsNaN(ngy) && ngy >= 0f)
                {
                    newborn.Y = ngy - WeaverHalfH;
                }
            }
            // 推进蛛丝
            if (_weaverThreads.Count > 0 && _mp != null && _mp.gameObject != null)
            {
                int mask = GetEnemyOverlapMask();
                if (mask != 0)
                {
                    try
                    {
                        for (int ti = _weaverThreads.Count - 1; ti >= 0; ti--)
                        {
                            WeaverThread t = _weaverThreads[ti];
                            t.Life -= sdt;
                            if (t.Life <= 0f)
                            {
                                _weaverThreads.RemoveAt(ti);
                                continue;
                            }
                            float step = t.Speed * sdt;
                            t.X += t.DirX * step;
                            t.Y += t.DirY * step;
                            t.Dist += step;
                            if (t.Dist > 9f)
                            {
                                _weaverThreads.RemoveAt(ti);
                                continue;
                            }
                            float mx = _mp.pixel2ux(t.X * _mp.CLEN);
                            float my = _mp.pixel2uy(t.Y * _mp.CLEN);
                            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                            Collider2D[] hits = Physics2D.OverlapCircleAll(center, WeaverThreadHitRadius, mask);
                            if (hits == null)
                            {
                                continue;
                            }
                            bool hitAny = false;
                            for (int j = 0; j < hits.Length; j++)
                            {
                                Collider2D c = hits[j];
                                if (c == null)
                                {
                                    continue;
                                }
                                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                                enemy = ResolveDamageTarget(enemy);
                                if (enemy == null)
                                {
                                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                                    if (ga != null && IsRemoteProxy(ga) && IsPvpAggroed(ga) &&
                                        t.Hits.Add(ga))
                                    {
                                        ApplyKnightAreaDamage(ga, WeaverDamage);
                                        if (CharmEffects.IsEquipped(CharmEffects.GrubsongId))
                                        {
                                            KnightAddSoul(1);
                                        }
                                        hitAny = true;
                                    }
                                    continue;
                                }
                                if (!t.Hits.Add(enemy))
                                {
                                    continue;
                                }
                                ApplyKnightAreaDamage(enemy, WeaverDamage);
                                // 幼虫之歌+编织者之歌：蛛丝命中敌人时回 1 灵魂
                                if (CharmEffects.IsEquipped(CharmEffects.GrubsongId))
                                {
                                    KnightAddSoul(1);
                                }
                                hitAny = true;
                            }
                            if (hitAny)
                            {
                                _weaverThreads.RemoveAt(ti); // 命中即消失
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            // 推进小编织者
            bool sittingNow = IsSitting;
            if (sittingNow)
            {
                _weaverSitTimer += sdt;
                if (_weaverSitTimer >= WeaverSitSleepTime)
                {
                    // 坐椅超 2 秒：所有还在跟随的小蜘蛛进入休息（先降落）
                    for (int si = 0; si < _weaverlings.Count; si++)
                    {
                        Weaverling sw = _weaverlings[si];
                        if (sw.State != 3)
                        {
                            sw.State = 3;
                            sw.AnimTime = 0f;
                            sw.SleepStage = 1; // 原地贴地休息（不做落点移动）
                            sw.Target = null;
                            // 原地贴地：X 保持当前，Y 对齐脚下地面
                            float gy = GetWeaverSleepGroundY(sw.X);
                            if (!float.IsNaN(gy))
                            {
                                sw.Y = gy;
                            }
                        }
                    }
                }
            }
            else
            {
                _weaverSitTimer = 0f;
                // 起身：休息中的小蜘蛛反向播睡眠（0003→0001），播完恢复跟随
                for (int si = 0; si < _weaverlings.Count; si++)
                {
                    Weaverling sw = _weaverlings[si];
                    if (sw.State == 3 && sw.SleepStage != 3)
                    {
                        sw.SleepStage = 3; // 苏醒阶段
                        sw.AnimTime = 0f;
                    }
                }
            }
            for (int i = 0; i < _weaverlings.Count; i++)
            {
                Weaverling w = _weaverlings[i];
                // 离小骑士太远（掉进深坑/被墙隔开等）：直接删除，
                // 下一帧由“维持 3 只”逻辑在小骑士旁边重新生成
                float wdx = w.X - X;
                float wdy = w.Y - Y;
                if (wdx * wdx + wdy * wdy > WeaverTooFarRange * WeaverTooFarRange)
                {
                    _weaverlings.RemoveAt(i);
                    i--;
                    continue;
                }
                w.AnimTime += sdt;
                w.AttackCd -= sdt;
                w.MeleeCd -= sdt;
                if (w.State == 0) // 出生：播 Launch 后进入跟随
                {
                    if (w.AnimTime >= GetClipDuration("WeaverLaunch"))
                    {
                        w.State = 1;
                        w.AnimTime = 0f;
                        w.SleepT = 0f;
                    }
                }
                else if (w.State == 2) // 攻击：动画中途发射蛛丝
                {
                    if (!w.ThreadFired && w.AnimTime >= 0.08f && w.Target != null &&
                        w.Target.is_alive)
                    {
                        w.ThreadFired = true;
                        FireWeaverThread(w);
                    }
                    if (w.AnimTime >= WeaverAttackAnimTime)
                    {
                        w.State = 1;
                        w.AnimTime = 0f;
                        w.Target = null;
                        w.AttackCd = WeaverAttackIntervalNow();
                    }
                }
                else if (w.State == 3) // 休息（坐椅）：降落 → 播睡眠 → 保持末帧；起身反向
                {
                    if (w.SleepStage == 1)
                    {
                        // 睡眠动画 0000~0003 播完 → 保持 0003
                        if (w.AnimTime >= GetClipDuration("WeaverSleep"))
                        {
                            w.SleepStage = 2;
                        }
                    }
                    else if (w.SleepStage == 3)
                    {
                        // 起身：反向睡眠（0003→0001），播完恢复跟随
                        if (w.AnimTime >= GetClipDuration("WeaverWake"))
                        {
                            w.State = 1;
                            w.AnimTime = 0f;
                            w.SleepStage = 0;
                            w.AttackCd = UnityEngine.Random.Range(0.4f, 1.4f);
                            w.MeleeCd = UnityEngine.Random.Range(0.6f, 1.8f);
                        }
                    }
                    continue; // 休息期间不做跟随/索敌/跳跃
                }
                else if (w.State == 4) // 近战攻击：动画 0.3s，起始瞬间对锁定敌人造成 35 伤害
                {
                    if (w.AnimTime <= 0.05f && w.Target != null && w.Target.is_alive)
                    {
                        ApplyKnightAreaDamage(w.Target, WeaverMeleeDamage);
                    }
                    if (w.AnimTime >= WeaverMeleeAnimTime)
                    {
                        w.State = 1;
                        w.AnimTime = 0f;
                        w.Target = null;
                        w.MeleeCd = WeaverMeleeIntervalNow();
                    }
                }
                else if (w.State == 1) // 跟随：贴地爬向停靠点，遇敌攻击
                {
                    // 近战动画计时
                    if (w.MeleeAnimT > 0f)
                    {
                        w.MeleeAnimT -= sdt;
                    }
                    if (w.Target == null || !w.Target.is_alive || w.Target.gameObject == null)
                    {
                        w.Target = FindNearestWeaverTarget(w.X, w.Y);
                    }
                    // 近战与远程独立读秒，可同时进行
                    if (w.Target != null)
                    {
                        float tdx = w.Target.x - w.X;
                        float tdy = w.Target.y - w.Y;
                        float tDistSq = tdx * tdx + tdy * tdy;
                        if (w.AttackCd <= 0f)
                        {
                            FireWeaverThread(w);
                            w.AttackCd = WeaverAttackIntervalNow();
                        }
                        if (w.MeleeCd <= 0f && tDistSq <= WeaverMeleeRange * WeaverMeleeRange)
                        {
                            ApplyKnightAreaDamage(w.Target, WeaverMeleeDamage);
                            // 幼虫之歌+编织者之歌：近战命中敌人时回 1 灵魂
                            if (CharmEffects.IsEquipped(CharmEffects.GrubsongId))
                            {
                                KnightAddSoul(1);
                            }
                            w.MeleeCd = WeaverMeleeIntervalNow();
                            w.MeleeAnimT = WeaverMeleeAnimTime;
                        }
                    }
                    // 跟随：在小骑士周围随机乱跑，偶尔跳两下，不离太远
                    float dxToKnight = X - w.X;
                    w.WanderT -= sdt;
                    if (Mathf.Abs(Vx) > 0.05f)
                    {
                        w.WanderX = X + w.HomeOx;
                        w.WanderT = 0f;
                    }
                    else if (Mathf.Abs(dxToKnight) > WeaverMaxRange)
                    {
                        w.WanderX = X;
                        w.WanderT = 0f;
                    }
                    else if (w.WanderT <= 0f)
                    {
                        w.WanderT = UnityEngine.Random.Range(0.35f, 1f);
                        float rad = UnityEngine.Random.Range(WeaverWanderMin, WeaverWanderMax);
                        w.WanderX = X + (UnityEngine.Random.value < 0.5f ? -rad : rad);
                    }
                    float dx = w.WanderX - w.X;
                    float dist = Mathf.Abs(dx);
                    bool knightMoving = Mathf.Abs(Vx) > 0.05f;
                    if (dist > 0.12f || knightMoving)
                    {
                        float spd = knightMoving ? WeaverFollowSpeed : Mathf.Min(WeaverFollowSpeed, dist * 5f);
                        float dir = Mathf.Sign(dx);
                        float step = dir * spd * sdt;
                        if (dist > 0f && Mathf.Abs(step) > dist)
                        {
                            step = dir * dist;
                        }
                        float newX = w.X + step;
                        int newCx = Mathf.FloorToInt(newX);
                        int footCy = Mathf.FloorToInt(w.Y + WeaverHalfH);
                        bool blocked = IsBlockCell(newCx, footCy);
                        if (Grounded)
                        {
                            blocked = blocked && IsBlockCell(newCx, Mathf.FloorToInt(Y + SizeY));
                        }
                        if (!blocked)
                        {
                            w.X = newX;
                        }
                        w.Vx = dir * spd;
                        if (dir != 0f)
                        {
                            w.Face = dir < 0f ? 1 : -1;
                        }
                    }
                    else
                    {
                        w.Vx = 0f;
                        w.WanderT = 0f;
                    }
                    // 跳跃计时
                    w.NextHopT -= sdt;
                    if (w.HopT <= 0f && w.NextHopT <= 0f && Mathf.Abs(w.HopVy) <= 0.01f)
                    {
                        w.HopT = WeaverHopTime;
                        w.HopVy = WeaverHopVy;
                        w.NextHopT = UnityEngine.Random.Range(0.7f, 2f);
                    }
                    if (w.HopT > 0f || Mathf.Abs(w.HopVy) > 0.01f)
                    {
                        if (w.HopT > 0f)
                        {
                            w.HopT -= sdt;
                        }
                        w.HopVy += WeaverHopGravity * sdt;
                        w.Y += w.HopVy * sdt;
                        float gy = GetWeaverGroundY(w.X, w.Y);
                        if (!float.IsNaN(gy) && gy >= 0f && w.Y + WeaverHalfH >= gy)
                        {
                            w.Y = gy - WeaverHalfH;
                            w.HopT = 0f;
                            w.HopVy = 0f;
                        }
                    }
                    else
                    {
                        float localGy = GetWeaverGroundY(w.X, w.Y);
                        bool hasLocal = !float.IsNaN(localGy) && localGy >= 0f;
                        if (hasLocal)
                        {
                            w.Y = localGy - WeaverHalfH;
                        }
                        else
                        {
                            w.HopVy = (w.HopVy > 0f ? w.HopVy : 0f) + WeaverHopGravity * sdt;
                            w.Y += w.HopVy * sdt;
                        }
                        if (Grounded && hasLocal && Y + SizeY < localGy - 0.35f)
                        {
                            float highGy = GetWeaverGroundY(w.X, Y + SizeY);
                            if (!float.IsNaN(highGy) && highGy >= 0f && highGy < localGy - 0.05f)
                            {
                                w.Y = highGy - WeaverHalfH;
                                w.WanderX = X;
                                w.WanderT = 0f;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>小蜘蛛休息落地的目标 Y（脚底贴地）：优先取该落点地面高度，回退长椅地面。</summary>
        private float GetWeaverSleepGroundY(float sleepX)
        {
            float gy = GetWeaverGroundY(sleepX, Y + SizeY);
            if (float.IsNaN(gy) || gy < 0f)
            {
                gy = _sitGroundY + SizeY;
            }
            // 再向下 0.1 格（y 向下为正），避免视觉悬空
            return gy - WeaverHalfH + 0.1f;
        }

        /// <summary>小编织者索敌：找最近的有效敌人（范围 7 格）。</summary>
        private M2Attackable FindNearestWeaverTarget(float sx, float sy)
        {
            if (_mp == null || _mp.gameObject == null)
            {
                return null;
            }
            try
            {
                M2Attackable aggro = FindNearestPvpAggroTarget(sx, sy, WeaverSeekRange);
                if (aggro != null)
                {
                    return aggro;
                }
                int mask = GetEnemyOverlapMask();
                if (mask == 0)
                {
                    return null;
                }
                float mx = _mp.pixel2ux(sx * _mp.CLEN);
                float my = _mp.pixel2uy(sy * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, WeaverSeekRange, mask);
                if (hits == null)
                {
                    return null;
                }
                M2Attackable best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        continue;
                    }
                    float d = (enemy.x - sx) * (enemy.x - sx) + (enemy.y - sy) * (enemy.y - sy);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = enemy;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>小编织者脚下地面高度（世界格 Y，地面顶面）；找不到返回 NaN。</summary>
        private float GetWeaverGroundY(float wx, float feetY)
        {
            if (_mp == null || _mp.BCC == null)
            {
                return float.NaN;
            }
            try
            {
                BCCLine line;
                float qy = feetY - 0.3f;
                return _mp.BCC.isFallable(wx, qy, 0.2f, 0.55f, out line, true, true, -1f, null);
            }
            catch (Exception)
            {
                return float.NaN;
            }
        }

        /// <summary>小编织者向目标发射蛛丝。</summary>
        private void FireWeaverThread(Weaverling w)
        {
            if (w.Target == null)
            {
                return;
            }
            float dx = w.Target.x - w.X;
            float dy = w.Target.y - w.Y;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d < 0.01f)
            {
                return;
            }
            _weaverThreads.Add(new WeaverThread
            {
                X = w.X,
                Y = w.Y - 0.3f,
                DirX = dx / d,
                DirY = dy / d,
                Speed = WeaverThreadSpeed,
                Life = WeaverThreadLife,
                Dist = 0f
            });
        }

        // ---------- 护符38 梦之盾 ----------

        /// <summary>
        /// 每帧推进梦之盾：围绕骑士公转（基础周期 4s，半径 1.5 格）；
        /// 回血时 0.5s 内加速到周期 1s，停止回血时 0.5s 内减速回 4s。
        /// 羁绊“舞梦者+梦之盾”：基础周期 3.2s；回血时盾牌以 10 格/秒直线远离骑士
        /// （仍绕转），1s 内周期降至 0.5s；停止回血以 10 格/秒直线返回，周期回 3.2s。
        /// 盾牌接触敌人造成 42 伤害（每只敌人进入接触只判定一次），
        /// 并销毁盾牌接触范围内的敌方弹幕。
        /// </summary>
        private void UpdateDreamShield(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.DreamShieldId))
            {
                _shieldContactEnemies.Clear();
                return;
            }
            bool bond = CharmEffects.IsEquipped(CharmEffects.DreamId);
            // 变速：目标角速度取决于是否回血；周期随羁绊不同（普通 4s/1s，羁绊 3.2s/0.5s）
            float targetOmega;
            float speedChangeTime;
            if (bond)
            {
                targetOmega = _focusing
                    ? 6.2831853f / ShieldPeriodFastBond
                    : 6.2831853f / ShieldPeriodBaseBond;
                speedChangeTime = ShieldSpeedChangeTimeBond;
            }
            else
            {
                targetOmega = _focusing ? ShieldOmegaFast : ShieldOmegaBase;
                speedChangeTime = ShieldSpeedChangeTime;
            }
            if (Mathf.Abs(_shieldOmegaTo - targetOmega) > 0.0001f)
            {
                _shieldOmegaFrom = _shieldOmega;
                _shieldOmegaTo = targetOmega;
                _shieldSpeedT = 0f;
            }
            if (_shieldSpeedT < 1f)
            {
                _shieldSpeedT = Mathf.Min(1f, _shieldSpeedT + sdt / speedChangeTime);
                _shieldOmega = Mathf.Lerp(_shieldOmegaFrom, _shieldOmegaTo, _shieldSpeedT);
            }
            else
            {
                _shieldOmega = _shieldOmegaTo;
            }
            _shieldAngle += _shieldOmega * sdt;

            // 羁绊：回血时盾牌以 10 格/秒沿径向远离骑士，停止回血同速返回基础半径；
            // 非羁绊固定基础半径
            if (bond)
            {
                if (_focusing)
                {
                    _shieldOrbitRadius = Mathf.Min(8f, _shieldOrbitRadius + ShieldRadialSpeed * sdt);
                }
                else
                {
                    _shieldOrbitRadius = Mathf.Max(ShieldOrbitRadius,
                        _shieldOrbitRadius - ShieldRadialSpeed * sdt);
                }
            }
            else
            {
                _shieldOrbitRadius = ShieldOrbitRadius;
            }

            // 碰撞箱圆心 = 渲染位置 + 碰撞偏移（左 1 格、下 0.5 格）
            float sx = X + ShieldRenderOffX + Mathf.Cos(_shieldAngle) * _shieldOrbitRadius + ShieldCollisionOffX;
            float sy = Y + Mathf.Sin(_shieldAngle) * _shieldOrbitRadius + ShieldCollisionOffY;

            // 接触伤害：进入接触范围瞬间判定一次，停留期间不重复
            if (_mp != null && _mp.gameObject != null)
            {
                int mask = GetEnemyOverlapMask();
                if (mask != 0)
                {
                    float mx = _mp.pixel2ux(sx * _mp.CLEN);
                    float my = _mp.pixel2uy(sy * _mp.CLEN);
                    Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                    var inside = new HashSet<NelEnemy>();
                    Collider2D[] hits = Physics2D.OverlapCircleAll(center, ShieldContactRadius, mask);
                    var insidePlayers = new HashSet<M2Attackable>();
                    for (int i = 0; i < hits.Length; i++)
                    {
                        Collider2D c = hits[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        enemy = ResolveDamageTarget(enemy);
                        if (enemy == null)
                        {
                            M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                            if (ga != null && IsRemoteProxy(ga) && IsPvpAggroed(ga) &&
                                insidePlayers.Add(ga))
                            {
                                if (!_shieldContactPlayers.Contains(ga))
                                {
                                    ApplyShieldToGeneric(ga);
                                }
                            }
                            continue;
                        }
                        if (!inside.Add(enemy))
                        {
                            continue;
                        }
                        if (!_shieldContactEnemies.Contains(enemy))
                        {
                            ApplyShieldContactDamage(enemy);
                        }
                    }
                    _shieldContactEnemies.Clear();
                    _shieldContactEnemies.UnionWith(inside);
                    _shieldContactPlayers.Clear();
                    _shieldContactPlayers.UnionWith(insidePlayers);
                }
            }
            // 阻挡敌弹：销毁盾牌接触范围内的敌方魔法弹幕
            BlockEnemyProjectilesAt(sx, sy);
        }

        /// <summary>梦之盾接触伤害：与当前无加成骨钉相等的 42 点（完整受击，压制击飞）。</summary>
        private void ApplyShieldContactDamage(NelEnemy enemy)
        {
            try
            {
                var atk = new NelAttackInfo();
                atk.hpdmg_current = 20;
                atk.hpdmg0 = 20;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f; // 压制击飞，盾牌只造成受击硬直
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        private void ApplyShieldToGeneric(M2Attackable a)
        {
            ApplySummonToGeneric(a, 20);
        }

        /// <summary>阻挡敌弹：销毁位于盾牌接触范围内的敌方弹幕（Caster 为魔物）。</summary>
        private void BlockEnemyProjectilesAt(float sx, float sy)
        {
            try
            {
                if (_mp == null || MagicAItemsField == null)
                {
                    return;
                }
                NelM2DBase nm2d = _mp.M2D as NelM2DBase;
                if (nm2d == null)
                {
                    return;
                }
                MagicItem[] items = MagicAItemsField.GetValue(nm2d.MGC) as MagicItem[];
                int len = MagicLenField != null
                    ? (int)MagicLenField.GetValue(nm2d.MGC)
                    : (items != null ? items.Length : 0);
                if (items == null)
                {
                    return;
                }
                for (int i = 0; i < len && i < items.Length; i++)
                {
                    MagicItem mg = items[i];
                    if (mg == null || mg.Caster == null || !(mg.Caster is NelEnemy))
                    {
                        continue;
                    }
                    float dx = mg.sx - sx;
                    float dy = mg.sy - sy;
                    if (dx * dx + dy * dy <= ShieldContactRadius * ShieldContactRadius)
                    {
                        mg.kill(0f); // 命中即销毁（阻挡）
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ---------- 护符39 格林之子 ----------

        /// <summary>
        /// 每帧推进小格林：
        /// 出现（turn）→ 待机（idle，站立/坐椅）/ 跟随（fly_full，走路，速度略慢）；
        /// 坐椅超 3 秒入睡（sleep，落地保持末帧），起身反向播睡眠再缓缓升空；
        /// 离骑士超 3.5 格原地播 teleport 后删除，在骑士身边重生（turn）。
        /// </summary>
        private void UpdateGrimm(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.GrimmId))
            {
                _grimm = null;
                _grimmFireballs.Clear();
                _grimmRespawnDelay = 0f;
                _grimmSitTimer = 0f;
                if (_grimmSoundState == 1)
                {
                    DashAudio.StopGrimmIdleLoop();
                }
                _grimmSoundState = 0;
                return;
            }
            // 火球推进（不受小格林状态/过图倒计时影响，只要护符佩戴中）
            UpdateGrimmFireballs(sdt);
            // 过图后重生倒计时（同编织者之歌：等骑士在新地图稳定再生成）
            if (_grimmRespawnDelay > 0f)
            {
                _grimmRespawnDelay -= sdt;
                if (_grimmRespawnDelay > 0f)
                {
                    if (_grimmSoundState == 1)
                    {
                        DashAudio.StopGrimmIdleLoop();
                    }
                    _grimmSoundState = 0;
                    return;
                }
            }
            if (_grimm == null)
            {
                _grimm = new GrimmChild
                {
                    X = GrimmHoverTargetX(),
                    Y = Y + GrimmHoverOffY,
                    Phase = 0,
                    AnimTime = 0f,
                    AttackCd = 0f,
                    Target = null
                };
            }
            GrimmChild g = _grimm;
            g.AnimTime += sdt;
            // 非攻击状态：循环播放待机音
            if (g.Phase != 5 && _grimmSoundState != 1)
            {
                DashAudio.PlayGrimmIdleLoop();
                _grimmSoundState = 1;
            }

            bool sitting = IsSitting;
            if (sitting)
            {
                _grimmSitTimer += sdt;
                if (_grimmSitTimer >= GrimmSitSleepTime && g.Phase == 1)
                {
                    // 坐椅超时：入睡（原地垂直落下，不横向移动）
                    g.Phase = 3;
                    g.AnimTime = 0f;
                    g.SleepStage = 0;
                    g.SleepX = g.X;
                    g.SleepGroundY = GetGrimmSleepGroundY();
                }
            }
            else
            {
                _grimmSitTimer = 0f;
                if (g.Phase == 3)
                {
                    // 起身：反向睡眠动画（播到 sleep0000 即回活跃）
                    g.Phase = 4;
                    g.AnimTime = 0f;
                }
            }

            if (g.Phase == 3) // 睡眠
            {
                if (g.SleepStage == 0)
                {
                    Vector2 toSleep = new Vector2(g.SleepX - g.X, g.SleepGroundY - g.Y);
                    float dist = toSleep.magnitude;
                    if (dist > 0.02f)
                    {
                        float spd = Mathf.Min(4f, dist * 4f);
                        toSleep /= dist;
                        g.X += toSleep.x * spd * sdt;
                        g.Y += toSleep.y * spd * sdt;
                    }
                    else
                    {
                        g.X = g.SleepX;
                        g.Y = g.SleepGroundY;
                        g.SleepStage = 1;
                        g.AnimTime = 0f;
                    }
                }
                else if (g.SleepStage == 1 && g.AnimTime >= GetClipDuration("GrimmSleep"))
                {
                    g.SleepStage = 2; // 保持睡眠末帧
                }
                return;
            }
            if (g.Phase == 4) // 苏醒：反向睡眠（sleep0002→0000）播完后直接回活跃，由 Phase1 自动升回追随点
            {
                if (g.AnimTime >= GetClipDuration("GrimmWake"))
                {
                    g.Phase = 1;
                    g.AnimTime = 0f;
                }
                return;
            }
            if (g.Phase == 2) // 传送：原地播完 teleport，删除并在骑士身边重生
            {
                if (g.AnimTime >= GetClipDuration("GrimmTeleport"))
                {
                    _grimm = null; // 下一帧以 turn 出生
                }
                return;
            }
            if (g.Phase == 0) // 出现：播完 turn 进入活跃
            {
                if (g.AnimTime >= GetClipDuration("GrimmAppear"))
                {
                    g.Phase = 1;
                    g.AnimTime = 0f;
                }
                return;
            }
            if (g.Phase == 5) // 攻击：播 shoot，0004 帧瞬间发射三枚火球
            {
                if (!g.Fired && g.AnimTime >= GrimmShootFireTime)
                {
                    g.Fired = true;
                    GrimmSpawnFireballs(g);
                }
                if (g.AnimTime >= GetClipDuration("GrimmShoot"))
                {
                    g.Phase = 1;
                    g.AnimTime = 0f;
                    g.AttackCd = GrimmAttackInterval;
                    g.Target = null;
                    g.AliceTarget = null;
                    DashAudio.PlayGrimmIdleLoop(); // 攻击结束恢复待机循环
                    _grimmSoundState = 1;
                }
                return;
            }

            // Phase 1 活跃：待机悬浮 / 跟随
            g.AttackCd -= sdt;
            float hoverX = GrimmHoverTargetX();
            float hoverY = Y + GrimmHoverOffY;
            bool knightMoving = Mathf.Abs(Vx) > 0.05f || !Grounded;
            Vector2 toTarget = new Vector2(hoverX - g.X, hoverY - g.Y);
            float tDist = toTarget.magnitude;
            if (tDist > 0.05f)
            {
                toTarget /= tDist;
                float spd;
                if (knightMoving)
                {
                    // 跟随：正常略低于骑士；远时加速追赶，但最大不超过骑士速度的 2 倍
                    // Vx 为“格/帧@60”单位，×60 换算为“格/秒”再参与真实秒积分
                    float knightSpd = Mathf.Abs(Vx) * 60f;
                    float followSpd = knightSpd * GrimmFollowLag;
                    float maxSpd = knightSpd * GrimmMaxSpeedRatio;
                    spd = Mathf.Clamp(tDist * 2.5f, followSpd, maxSpd);
                }
                else
                {
                    spd = Mathf.Min(2.5f, tDist * 2.5f);
                }
                g.X += toTarget.x * spd * sdt;
                g.Y += toTarget.y * spd * sdt;
            }
            // 攻击触发：冷却结束且范围内有目标（敌人优先，其次魔力草）
            if (g.AttackCd <= 0f && FindGrimmTarget(g))
            {
                g.Phase = 5;
                g.AnimTime = 0f;
                g.Fired = false;
                if (_grimmSoundState == 1)
                {
                    DashAudio.StopGrimmIdleLoop();
                }
                DashAudio.PlayGrimmAttackYelp(); // 攻击瞬间播放 yelp
                _grimmSoundState = 2;
                return;
            }
            // 距离检测：离骑士超 3.5 格 → 原地传送动画后重生
            float gdx = g.X - X;
            float gdy = g.Y - Y;
            if (gdx * gdx + gdy * gdy > GrimmTeleportRange * GrimmTeleportRange)
            {
                g.Phase = 2;
                g.AnimTime = 0f;
            }
        }

        /// <summary>
        /// 小格林追随点 X：位于小骑士后上方（面朝方向的反方向）。
        /// 面朝约定：_faceDir&gt;0=面朝左（后方在右），_faceDir&lt;0=面朝右（后方在左）。
        /// </summary>
        private float GrimmHoverTargetX()
        {
            return X + (_faceDir > 0f ? GrimmHoverOffX : -GrimmHoverOffX);
        }

        /// <summary>坐椅时小格林落地的目标 Y（睡眠贴图底边贴地）。</summary>
        private float GetGrimmSleepGroundY()
        {
            float groundY = _sitGroundY + SizeY;
            if (_mp != null && _clips.TryGetValue("GrimmSleep", out ClipData sc) &&
                sc.frames.Length > 0 &&
                _textures.TryGetValue(sc.frames[sc.frames.Length - 1], out Texture2D tex))
            {
                groundY -= tex.height * GrimmScale / (2f * _mp.CLEN);
            }
            return groundY;
        }

        /// <summary>按剪辑取当前帧（loop=循环，否则播完停末帧）。</summary>
        private string GrimmFrame(string clipName, float t, bool loop)
        {
            if (!_clips.TryGetValue(clipName, out ClipData clip) || clip.frames.Length == 0)
            {
                return null;
            }
            int idx = loop
                ? (int)(t * clip.fps) % clip.frames.Length
                : Mathf.Clamp((int)(t * clip.fps), 0, clip.frames.Length - 1);
            return clip.frames[idx];
        }

        /// <summary>
        /// 小格林索敌：6 格半径内优先级为 敌人 &gt; 爱丽丝 &gt; 魔力草。
        /// 攻击爱丽丝不造成伤害。返回是否找到目标。
        /// </summary>
        private bool FindGrimmTarget(GrimmChild g)
        {
            M2Attackable aggro = FindNearestPvpAggroTarget(g.X, g.Y, GrimmSeekRange);
            if (aggro != null)
            {
                g.Target = aggro;
                g.TargetIsWeed = false;
                return true;
            }
            NelEnemy enemy = FindNearestGrimmEnemy(g.X, g.Y);
            if (enemy != null)
            {
                g.Target = enemy;
                g.TargetIsWeed = false;
                g.AliceTarget = null;
                return true;
            }
            // 爱丽丝（任意房间）：佩戴格林之子时小格林攻击爱丽丝，但不造成伤害
            if (TryFindGrimmAlice(g.X, g.Y, out AlicePVV200 alice))
            {
                g.Target = null;
                g.TargetIsWeed = false;
                g.AliceTarget = alice;
                return true;
            }
            if (FindNearestGrimmWeed(g.X, g.Y, out float wx, out float wy))
            {
                g.Target = null;
                g.TargetIsWeed = true;
                g.AliceTarget = null;
                g.WeedTargetX = wx;
                g.WeedTargetY = wy;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 查找当前房间里的爱丽丝（AlicePVV200）作为小格林攻击目标：
        /// 6 格半径内最近者；攻击爱丽丝不造成伤害（火球命中即消失）。
        /// </summary>
        private bool TryFindGrimmAlice(float sx, float sy, out AlicePVV200 alice)
        {
            alice = null;
            if (_mp == null)
            {
                return false;
            }
            try
            {
                int count = _mp.count_movers;
                for (int i = 0; i < count; i++)
                {
                    M2Mover mv = _mp.getMv(i);
                    if (mv is AlicePVV200 a && !a.destructed)
                    {
                        float dx = a.x - sx;
                        float dy = a.y - sy;
                        if (dx * dx + dy * dy <= GrimmSeekRange * GrimmSeekRange)
                        {
                            alice = a;
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>6 格半径内最近敌人（复刻小编织者索敌，范围改为 6）。</summary>
        private NelEnemy FindNearestGrimmEnemy(float sx, float sy)
        {
            if (_mp == null || _mp.gameObject == null)
            {
                return null;
            }
            try
            {
                int mask = GetEnemyOverlapMask();
                if (mask == 0)
                {
                    return null;
                }
                float mx = _mp.pixel2ux(sx * _mp.CLEN);
                float my = _mp.pixel2uy(sy * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, GrimmSeekRange, mask);
                if (hits == null)
                {
                    return null;
                }
                NelEnemy best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        continue;
                    }
                    float dx = enemy.x - sx;
                    float dy = enemy.y - sy;
                    float d = dx * dx + dy * dy;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = enemy;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>6 格半径内最近的魔力草，返回其格坐标。</summary>
        private bool FindNearestGrimmWeed(float sx, float sy, out float wx, out float wy)
        {
            wx = 0f;
            wy = 0f;
            if (_mp == null || _mp.gameObject == null)
            {
                return false;
            }
            try
            {
                int mask = GetAreaObjectMask();
                if (mask == 0)
                {
                    return false;
                }
                float mx = _mp.pixel2ux(sx * _mp.CLEN);
                float my = _mp.pixel2uy(sy * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, GrimmSeekRange, mask);
                if (hits == null)
                {
                    return false;
                }
                bool found = false;
                float bestD = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    M2ManaWeed weed = c.GetComponentInParent<M2ManaWeed>();
                    if (weed == null || !IsManaWeedReady(weed))
                    {
                        // 跳过已被破坏/正在重生（未完全长成）的魔力草
                        continue;
                    }
                    float wx2 = weed.mapcx;
                    float wy2 = weed.mapcy;
                    float dx = wx2 - sx;
                    float dy = wy2 - sy;
                    float d = dx * dx + dy * dy;
                    if (d < bestD)
                    {
                        bestD = d;
                        wx = wx2;
                        wy = wy2;
                        found = true;
                    }
                }
                return found;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>攻击 0004 帧：向目标发射三枚直线火球（正中 + 顺/逆时针各 30°）。</summary>
        private void GrimmSpawnFireballs(GrimmChild g)
        {
            float tx, ty;
            if (g.AliceTarget != null && !g.AliceTarget.destructed)
            {
                // 爱丽丝：瞄准当前位置（攻击不造成伤害）
                tx = g.AliceTarget.x;
                ty = g.AliceTarget.y;
            }
            else if (!g.TargetIsWeed)
            {
                if (g.Target != null && g.Target.is_alive)
                {
                    tx = g.Target.x;
                    ty = g.Target.y;
                }
                else
                {
                    // 目标已失效：朝自身面前方向射出（保底）
                    tx = g.X + (_faceDir > 0f ? -1f : 1f);
                    ty = g.Y - 1f;
                }
            }
            else
            {
                tx = g.WeedTargetX;
                ty = g.WeedTargetY;
                if (tx == 0f && ty == 0f)
                {
                    tx = g.X;
                    ty = g.Y - 1.5f;
                }
            }
            float baseAng = Mathf.Atan2(ty - g.Y, tx - g.X); // 游戏 Y 向下，+角=顺时针
            float[] offs = { 0f, GrimmFireballSpread, -GrimmFireballSpread };
            for (int i = 0; i < offs.Length; i++)
            {
                float a = baseAng + offs[i];
                _grimmFireballs.Add(new GrimmFireball
                {
                    X = g.X,
                    Y = g.Y,
                    DirX = Mathf.Cos(a),
                    DirY = Mathf.Sin(a),
                    Life = GrimmFireballLife,
                    Alice = g.AliceTarget
                });
            }
        }

        /// <summary>火球推进：直线飞行、穿敌、破魔力草、5 秒后销毁。</summary>
        private void UpdateGrimmFireballs(float sdt)
        {
            if (_grimmFireballs.Count == 0)
            {
                return;
            }
            if (_mp == null || _mp.gameObject == null)
            {
                return;
            }
            int enemyMask = GetEnemyOverlapMask();
            int areaMask = GetAreaObjectMask();
            for (int i = _grimmFireballs.Count - 1; i >= 0; i--)
            {
                GrimmFireball f = _grimmFireballs[i];
                f.Life -= sdt;
                if (f.Life <= 0f)
                {
                    _grimmFireballs.RemoveAt(i);
                    continue;
                }
                float step = GrimmFireballSpeed * sdt;
                f.X += f.DirX * step;
                f.Y += f.DirY * step;
                // 爱丽丝：火球到达她身边即算“命中”（不造成伤害，火球消失）
                if (f.Alice != null && !f.Alice.destructed)
                {
                    float adx = f.X - f.Alice.x;
                    float ady = f.Y - f.Alice.y;
                    if (adx * adx + ady * ady <= GrimmFireballRadius * GrimmFireballRadius)
                    {
                        _grimmFireballs.RemoveAt(i);
                        continue;
                    }
                }
                float mx = _mp.pixel2ux(f.X * _mp.CLEN);
                float my = _mp.pixel2uy(f.Y * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 敌人：穿透，每只敌人只判定一次
                if (enemyMask != 0)
                {
                    Collider2D[] hits = Physics2D.OverlapCircleAll(center, GrimmFireballRadius, enemyMask);
                    if (hits != null)
                    {
                        for (int j = 0; j < hits.Length; j++)
                        {
                            Collider2D c = hits[j];
                            if (c == null)
                            {
                                continue;
                            }
                            NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                            enemy = ResolveDamageTarget(enemy);
                            if (enemy == null)
                            {
                                M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                                if (ga != null && IsRemoteProxy(ga) && IsPvpAggroed(ga) &&
                                    f.Hits.Add(ga))
                                {
                                    ApplyKnightAreaDamage(ga, GrimmFireballDamage);
                                }
                                continue;
                            }
                            if (!f.Hits.Add(enemy))
                            {
                                continue;
                            }
                            ApplyKnightAreaDamage(enemy, GrimmFireballDamage);
                        }
                    }
                }
                // 魔力草：破坏并给小骑士回魂（复用普攻破草逻辑）
                if (areaMask != 0)
                {
                    BreakManaWeeds(f.Hits, f.X, f.Y, GrimmFireballRadius, GrimmFireballRadius);
                }
            }
        }

        /// <summary>
        /// 每帧推进幼体：每 3s 消耗 8 灵魂生成（最多 4 只）；
        /// 出生后自动索敌飞行，碰到敌人造成 20 伤害并爆炸消失。
        /// </summary>
        private void UpdateUterus(float sdt)
        {
            if (!CharmEffects.IsEquipped(CharmEffects.UterusId))
            {
                _uterusHatchlings.Clear(); // 卸下护符：幼体消失
                _uterusExplosions.Clear(); // 爆炸特效一并清除
                return;
            }
            _uterusSpawnTimer -= sdt;
            if (_uterusSpawnTimer <= 0f)
            {
                // 羁绊：亡者之怒 + 发光子宫 —— 生成速度提高至 2 秒
                _uterusSpawnTimer = (CharmEffects.IsEquipped(CharmEffects.FuryId) &&
                                     CharmEffects.IsEquipped(CharmEffects.UterusId))
                    ? 2f : UterusSpawnInterval;
                if (_uterusHatchlings.Count < UterusMaxCount && _soul >= UterusSpawnSoul)
                {
                    if (!_soulInfinite)
                    {
                        _soul -= UterusSpawnSoul;
                    }
                    // 出生：随机向上前方初速（HK 原版：40°~140°，速度约 45 单位 → 按格换算 3~4 格/秒）
                    float ang = UnityEngine.Random.Range(0.7f, 2.44f); // 40°~140°
                    _uterusHatchlings.Add(new UterusHatchling
                    {
                        X = X,
                        Y = Y,
                        Vx = Mathf.Cos(ang) * 3.6f,
                        Vy = -Mathf.Sin(ang) * 3.6f, // Y 向下为正，向上为负
                        Phase = 0,
                        AnimTime = 0f,
                        Target = null,
                        HomeOx = 0f,
                        HomeOy = 0f,
                        BuzzTimer = UnityEngine.Random.Range(0.3f, 0.8f)
                    });
                    // 站立停靠点：骑士中心半径 0.8~2 格圆内（初始随机分布，避免挤在一起）
                    UterusHatchling newborn = _uterusHatchlings[_uterusHatchlings.Count - 1];
                    float homeAng = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    float homeRad = UnityEngine.Random.Range(0.8f, 2f);
                    newborn.HomeOx = Mathf.Cos(homeAng) * homeRad;
                    newborn.HomeOy = Mathf.Sin(homeAng) * homeRad;
                }
            }
            // 小骑士是否在行动（移动/空中）：行动时幼体追上骑士，站立时悬浮微动
            bool knightMoving = Mathf.Abs(Vx) > 0.05f || !Grounded;
            bool knightSitting = IsSitting;
            for (int i = _uterusHatchlings.Count - 1; i >= 0; i--)
            {
                UterusHatchling h = _uterusHatchlings[i];
                h.AnimTime += sdt;
                // 坐椅子：飞行/悬浮的幼体降落到地面睡觉；起身后升空恢复跟随
                if (knightSitting && h.Phase == 1)
                {
                    h.Phase = 3;
                    h.AnimTime = 0f;
                    h.Vx = 0f;
                    h.Vy = 0f;
                    // 原地垂直落下：不横向移动，Y 对齐地面
                    h.SleepStage = 0;
                    h.SleepX = h.X;
                    // 落点整体提高 0.1 格（Y 向下为正）
                    h.SleepGroundY = GetUterusSleepGroundY() - 0.1f;
                }
                if (!knightSitting && h.Phase == 3)
                {
                    h.Phase = 1;
                    h.AnimTime = 0f;
                    h.Vx = 0f;
                    h.Vy = 0f;
                }
                if (h.Phase == 3)
                {
                    if (h.SleepStage == 0)
                    {
                        // 下落：斜向滑落到长椅周边的落点；到达后切到已落地并重置动画时间
                        Vector2 toSleep = new Vector2(h.SleepX - h.X, h.SleepGroundY - h.Y);
                        float dist = toSleep.magnitude;
                        if (dist > 0.02f)
                        {
                            float spd = Mathf.Min(4f, dist * 4f);
                            toSleep /= dist;
                            h.X += toSleep.x * spd * sdt;
                            h.Y += toSleep.y * spd * sdt;
                        }
                        else
                        {
                            h.X = h.SleepX;
                            h.Y = h.SleepGroundY;
                            h.SleepStage = 1;
                            h.AnimTime = 0f; // 落地后才开始播睡眠动画
                        }
                    }
                    continue;
                }
                if (h.Phase == 0) // 出生：速度衰减，播完 Hatch 进入飞行
                {
                    h.X += h.Vx * sdt;
                    h.Y += h.Vy * sdt;
                    h.Vx *= 0.85f;
                    h.Vy *= 0.85f;
                    if (h.AnimTime >= GetClipDuration("UterusHatch"))
                    {
                        h.Phase = 1;
                        h.AnimTime = 0f;
                    }
                }
                else if (h.Phase == 1) // 飞行：索敌冲刺 / 追骑士 / 站立悬浮微动
                {
                    if (h.Target == null || !h.Target.is_alive || h.Target.gameObject == null)
                    {
                        h.Target = FindNearestUterusTarget(h.X, h.Y);
                    }
                    if (h.Target != null)
                    {
                        // 有敌人：加速冲向目标
                        Vector2 dir = new Vector2(h.Target.x - h.X, h.Target.y - h.Y);
                        float dist = dir.magnitude;
                        if (dist > 0.01f)
                        {
                            dir /= dist;
                        }
                        h.Vx += dir.x * UterusAccel * sdt;
                        h.Vy += dir.y * UterusAccel * sdt;
                        float spd = Mathf.Sqrt(h.Vx * h.Vx + h.Vy * h.Vy);
                        if (spd > UterusSpeedMax)
                        {
                            h.Vx *= UterusSpeedMax / spd;
                            h.Vy *= UterusSpeedMax / spd;
                        }
                    }
                    else if (knightMoving)
                    {
                        // 行动中：追到各自固定的停靠偏移处（避免所有幼体叠在骑士中心）
                        Vector2 toKnight = new Vector2(X + h.HomeOx - h.X, Y + h.HomeOy - h.Y);
                        float dist = toKnight.magnitude;
                        if (dist > 0.05f)
                        {
                            float spd = Mathf.Min(7f, dist * 4f);
                            toKnight /= dist;
                            h.Vx = toKnight.x * spd;
                            h.Vy = toKnight.y * spd;
                        }
                        else
                        {
                            h.Vx = 0f;
                            h.Vy = 0f;
                        }
                    }
                    else
                    {
                        // 站立：骑士中心附近悬浮微动（停靠点随机小幅变化）
                        h.BuzzTimer -= sdt;
                        if (h.BuzzTimer <= 0f)
                        {
                            h.BuzzTimer = UnityEngine.Random.Range(0.3f, 0.8f);
                            // 活动范围：骑士中心半径 0.8~2 格圆内
                            float homeAng = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                            float homeRad = UnityEngine.Random.Range(0.8f, 2f);
                            h.HomeOx = Mathf.Cos(homeAng) * homeRad;
                            h.HomeOy = Mathf.Sin(homeAng) * homeRad;
                        }
                        Vector2 toHome = new Vector2(X + h.HomeOx - h.X, Y + h.HomeOy - h.Y);
                        float dist = toHome.magnitude;
                        if (dist > 0.05f)
                        {
                            float spd = Mathf.Min(1.6f, dist * 2.5f);
                            toHome /= dist;
                            h.Vx = toHome.x * spd;
                            h.Vy = toHome.y * spd;
                        }
                        else
                        {
                            h.Vx = 0f;
                            h.Vy = 0f;
                        }
                    }
                    h.X += h.Vx * sdt;
                    h.Y += h.Vy * sdt;
                    if (UterusHitEnemy(h))
                    {
                        _uterusHatchlings.RemoveAt(i); // 碰撞后幼体立即消失（爆炸特效由 UterusHitEnemy 生成）
                    }
                }
                else // 爆炸：播完 Burst 消失
                {
                    if (h.AnimTime >= GetClipDuration("UterusBurst"))
                    {
                        _uterusHatchlings.RemoveAt(i);
                    }
                }
            }
            // 碰撞爆炸特效推进：播完移除
            for (int i = _uterusExplosions.Count - 1; i >= 0; i--)
            {
                _uterusExplosions[i].AnimTime += sdt;
                if (_uterusExplosions[i].AnimTime >= GetClipDuration("UterusExplosion"))
                {
                    _uterusExplosions.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 坐椅子时幼体落地的目标 Y：以坐下时记录的地面基准（_sitGroundY）为脚底，
        /// 减去睡眠贴图一半高度使贴图底边贴地。
        /// 注意不能用坐下后的 Y（椅面比地面高 0.3），否则会停在长椅底座高度、被前板挡住。
        /// </summary>
        private float GetUterusSleepGroundY()
        {
            float groundY = _sitGroundY + SizeY; // 长椅所在的地面
            if (_mp != null && _clips.TryGetValue("UterusRest", out ClipData restClip) &&
                restClip.frames.Length > 0 && _textures.TryGetValue(restClip.frames[0], out Texture2D tex))
            {
                groundY -= tex.height * UterusScale / (2f * _mp.CLEN);
            }
            return groundY;
        }

        /// <summary>寻找幼体附近的最近敌人（索敌范围 12 格）。</summary>
        private M2Attackable FindNearestUterusTarget(float sx, float sy)
        {
            if (_mp == null || _mp.gameObject == null)
            {
                return null;
            }
            try
            {
                M2Attackable aggro = FindNearestPvpAggroTarget(sx, sy, UterusSeekRange);
                if (aggro != null)
                {
                    return aggro;
                }
                int mask = GetEnemyOverlapMask();
                if (mask == 0)
                {
                    return null;
                }
                float mx = _mp.pixel2ux(sx * _mp.CLEN);
                float my = _mp.pixel2uy(sy * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapCircleAll(center, UterusSeekRange, mask);
                if (hits == null)
                {
                    return null;
                }
                M2Attackable best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        continue;
                    }
                    float d = (enemy.x - sx) * (enemy.x - sx) + (enemy.y - sy) * (enemy.y - sy);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = enemy;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null; // 过图/地图销毁瞬间物理查询可能异常，静默跳过本帧索敌
            }
        }

        /// <summary>
        /// 幼体碰到敌人：在碰撞点生成爆炸特效 + 播放爆炸音效，
        /// 并对以碰撞点为中心 3 格半径内的所有敌人造成 48 伤害（每个敌人只结算一次）。
        /// </summary>
        private bool UterusHitEnemy(UterusHatchling h)
        {
            if (_mp == null || _mp.gameObject == null)
            {
                return false;
            }
            try
            {
                int mask = GetEnemyOverlapMask();
                if (mask == 0)
                {
                    return false;
                }
                float mx = _mp.pixel2ux(h.X * _mp.CLEN);
                float my = _mp.pixel2uy(h.Y * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 先小半径检测：半径内必须存在真实敌人（解析为有效目标）才爆炸。
                // 不能用“任意碰撞体”判定，否则出生点在小骑士身上会立刻误炸。
                Collider2D[] touch = Physics2D.OverlapCircleAll(center, UterusHitRadius, mask);
                bool touchedEnemy = false;
                if (touch != null)
                {
                    for (int i = 0; i < touch.Length; i++)
                    {
                        Collider2D c = touch[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        enemy = ResolveDamageTarget(enemy);
                        if (enemy != null)
                        {
                            touchedEnemy = true;
                            break;
                        }
                        M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                        if (ga != null && IsRemoteProxy(ga) && IsPvpAggroed(ga))
                        {
                            touchedEnemy = true;
                            break;
                        }
                    }
                }
                if (!touchedEnemy)
                {
                    return false;
                }
                // 碰撞：生成爆炸特效（碰撞点）并播放爆炸音效
                _uterusExplosions.Add(new UterusExplosion { X = h.X, Y = h.Y, AnimTime = 0f });
                try
                {
                    DashAudio.PlayUterusExplosion();
                }
                catch (Exception)
                {
                }
                // 6x6 格 AoE：判定中心相对碰撞点右移 2 格，所有敌人 48 伤害，每个敌人只结算一次
                float amx = _mp.pixel2ux((h.X + UterusExplosionOffX) * _mp.CLEN);
                float amy = _mp.pixel2uy(h.Y * _mp.CLEN);
                Vector2 aoeCenter = _mp.gameObject.transform.TransformPoint(new Vector2(amx, amy));
                Collider2D[] aoe = Physics2D.OverlapBoxAll(
                    aoeCenter, new Vector2(UterusExplosionSize, UterusExplosionSize), 0f, mask);
                if (aoe != null)
                {
                    var applied = new HashSet<NelEnemy>();
                    var appliedPlayers = new HashSet<M2Attackable>();
                    for (int i = 0; i < aoe.Length; i++)
                    {
                        Collider2D c = aoe[i];
                        if (c == null)
                        {
                            continue;
                        }
                        NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                        enemy = ResolveDamageTarget(enemy);
                        if (enemy == null)
                        {
                            M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                            if (ga != null && IsRemoteProxy(ga) && IsPvpAggroed(ga) &&
                                appliedPlayers.Add(ga))
                            {
                                ApplyKnightAreaDamage(ga, UterusExplosionDamage);
                            }
                            continue;
                        }
                        if (!applied.Add(enemy))
                        {
                            continue;
                        }
                        // 羁绊：亡者之怒 + 发光子宫 —— 爆炸伤害提升至 40
                        ApplyKnightAreaDamage(enemy,
                            (CharmEffects.IsEquipped(CharmEffects.FuryId) &&
                             CharmEffects.IsEquipped(CharmEffects.UterusId))
                                ? 40 : UterusExplosionDamage);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false; // 过图/地图销毁瞬间物理查询可能异常，静默跳过本帧碰撞
            }
        }

        /// <summary>小骑士区域伤害（完整受击管线，幼体爆炸/孢子云共用）。</summary>
        private void ApplyKnightAreaDamage(NelEnemy enemy, int dmg)
        {
            try
            {
                // 召唤/生成阶段的魔物不可被攻击：直接跳过，
                // 否则伤害会把它们踢出 SUMMONED 状态，而 disappearing 标志无人复位，
                // 导致生成结束后渲染永久消失。
                if (IsEnemySummoning(enemy))
                {
                    return;
                }
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>召唤物伤害通用入口：敌人走原管线，PvP 远端玩家走伤害包。</summary>
        private void ApplyKnightAreaDamage(M2Attackable target, int dmg)
        {
            if (target == null)
            {
                return;
            }
            if (target is NelEnemy enemy)
            {
                ApplyKnightAreaDamage(enemy, dmg);
                return;
            }
            if (IsRemoteProxy(target) && IsPvpAggroed(target))
            {
                ApplySummonToGeneric(target, dmg);
            }
        }

        private void ApplySummonToGeneric(M2Attackable a, int dmg)
        {
            if (a == null) return;
            try
            {
                SetHitFeedback(0);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                if (IsRemoteProxy(a)) RegisterPvpAggro(a);
            }
            catch (Exception)
            {
            }
        }

        internal void RegisterPvpAggro(M2Attackable target)
        {
            // 按需求：小骑士主动攻击其他玩家不再挂仇恨，仅保留“自己受到伤害”触发。
        }

        private void AddPvpAggro(M2Attackable target)
        {
            if (target == null || !IsRemoteProxy(target)) return;
            _pvpAggroTargets.Add(target);
        }

        private void RegisterNearestPvpAggroNear(float x, float range)
        {
            try
            {
                M2Attackable[] movers = Resources.FindObjectsOfTypeAll<M2Attackable>();
                M2Attackable best = null;
                float bestD = range;
                for (int i = 0; i < movers.Length; i++)
                {
                    M2Attackable ga = movers[i];
                    if (ga == null || ga is PR || ga is M2MoverPr || !IsRemoteProxy(ga) ||
                        !ga.is_alive)
                    {
                        continue;
                    }
                    float d = Mathf.Abs(ga.x - x);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = ga;
                    }
                }
                if (best != null)
                {
                    AddPvpAggro(best);
                }
            }
            catch (Exception)
            {
            }
        }

        internal bool IsPvpAggroed(M2Attackable target)
        {
            return target != null && _pvpAggroTargets.Contains(target);
        }

        private void PrunePvpAggroTargets()
        {
            if (_pvpAggroTargets.Count == 0) return;
            _pvpAggroTargets.RemoveWhere(t =>
                t == null || t.gameObject == null || !t.is_alive || t.get_hp() <= 0f);
        }

        private M2Attackable FindNearestPvpAggroTarget(float sx, float sy, float range)
        {
            PrunePvpAggroTargets();
            M2Attackable best = null;
            float bestD = range * range;
            foreach (M2Attackable t in _pvpAggroTargets)
            {
                if (t == null || !t.is_alive) continue;
                float dx = t.x - sx;
                float dy = t.y - sy;
                float d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>孢子云命中联机远端玩家代理：持续伤害走正常 PvP 伤害包。</summary>
        private void ApplySporeToGeneric(M2Attackable a, int dmg)
        {
            if (a == null)
            {
                return;
            }
            try
            {
                SetHitFeedback(0);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.huttobi_ratio = -100f;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>施法瞬间的爆炸特效帧推进（Fireball Blast，一次性）。</summary>
        private void UpdateFireballBlast(float sdt)
        {
            if (_fireballBlastFrames == null || _fireballBlastFrames.Length == 0)
            {
                _fireballBlastSprite = null;
                return;
            }
            _fireballBlastTimer += sdt;
            int idx = (int)(_fireballBlastTimer * _fireballBlastFps);
            if (idx >= _fireballBlastFrames.Length)
            {
                _fireballBlastFrames = null;
                _fireballBlastSprite = null;
                return;
            }
            _fireballBlastSprite = _fireballBlastFrames[idx];
        }

        /// <summary>蜕变挽歌：满血普攻播放到 slashes_effect0001 帧时向前发射剑气。</summary>
        private void SpawnElegyBlade()
        {
            _elegySpawnedThisSwing = true;
            float dir = -_faceDir; // _faceDir=1 脸朝左（-X），-1 脸朝右（+X）
            _elegyBlades.Add(new ElegyBladeProj
            {
                X = X + HurtCenterX + dir * 0.5f, // 从骑士中心略前方发射
                Y = Y + HurtCenterY - 0.5f, // 渲染与判定箱整体上移 0.5 格
                Dir = dir,
                Traveled = 0f
            });
            DashAudio.PlayElegyBlade(); // 发射音效（soul_totem_slash，已裁掉倒放后半段）
        }

        /// <summary>每帧推进剑气：直线飞行（12 格/秒），飞行 6 格后消失。</summary>
        private void UpdateElegyBlades(float sdt)
        {
            if (_elegyBlades.Count == 0)
            {
                return;
            }
            // 注意：外部传入的 dt 是帧归一化（Time.deltaTime×60），这里必须用真实秒
            sdt = Time.deltaTime;
            for (int bi = _elegyBlades.Count - 1; bi >= 0; bi--)
            {
                ElegyBladeProj p = _elegyBlades[bi];
                float step = ElegyBladeSpeed * sdt;
                p.X += p.Dir * step;
                p.Traveled += step;
                CheckElegyBladeHit(p);
                if (p.Traveled >= ElegyBladeRange)
                {
                    _elegyBlades.RemoveAt(bi);
                }
            }
        }

        /// <summary>剑气判定：每只魔物进入判定箱只受一次伤害（p.Hits 去重）。</summary>
        private void CheckElegyBladeHit(ElegyBladeProj p)
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            // 判定框：向下 0.5 格；向左发射 x-1.2 格、向右发射 x+0.2 格
            float hx = p.X + (p.Dir < 0f ? -1.2f : 0.2f);
            float hy = p.Y + 0.5f;
            // 蜕变挽歌剑气：沿途破坏魔力草（+4 灵魂，与普攻一致，每株草只结算一次）
            BreakManaWeeds(p.Hits, hx, hy, ElegyBladeHitboxW * 0.5f, ElegyBladeHitboxH * 0.5f);
            // 蜕变挽歌剑气：可破坏蜘蛛陷阱（蛛丝球释放源，与普攻/技艺一致）
            DestroySpiderTrapsInBox(new Vector2(hx, hy),
                ElegyBladeHitboxW * 0.5f, ElegyBladeHitboxH * 0.5f);
            float mx = _mp.pixel2ux(hx * _mp.CLEN);
            float my = _mp.pixel2uy(hy * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center,
                new Vector2(ElegyBladeHitboxW, ElegyBladeHitboxH), 0f, mask);
            int elegyDmg = ElegyBladeDamageNow();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 与复仇之魂一致：剑气也能命中联机远端玩家代理（M2Gunmu）。
                    // 剑气属于骨钉体系，但不通过此入口结算命中灵魂。
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                        TryHitGenericAttackable(c, elegyDmg, p.Hits, 0, false))
                    {
                        continue;
                    }
                    continue;
                }
                if (!p.Hits.Add(enemy))
                {
                    continue;
                }
                // 蜕变挽歌：若魔物同时进入普攻判定箱，则只吃普攻伤害，剑气不重复结算
                if (_swingHits.Contains(enemy))
                {
                    continue;
                }
                ApplyElegyBladeDamage(enemy);
            }
        }

        /// <summary>剑气伤害：15 点固定伤害（完整受击管线）。</summary>
        private void ApplyElegyBladeDamage(NelEnemy enemy)
        {
            try
            {
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                // 剑气由普攻发射，属于骨钉伤害体系，受坚固贪婪“每 4 物品降低 1.5%”削减；
                // 羁绊“蜕变挽歌+亡者之怒”：狂怒状态剑气伤害提升至 25，且不受坚固贪婪削减
                int dmg = ElegyBladeDamageNow();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
                DashAudio.PlayEnemyHit();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>当前蜕变挽歌剑气伤害（狂怒羁绊 25，否则 15 × 坚固贪婪削减）。</summary>
        private int ElegyBladeDamageNow()
        {
            return FuryActive
                ? 25
                : Mathf.Max(1, Mathf.FloorToInt(
                    ElegyBladeDamage * CharmEffects.GreedDamageMultiplier() + 0.5f));
        }

        // ================= 深渊尖啸（法术·尖啸）=================

        /// <summary>能否尖啸：有足够灵魂、不在攻击/冲刺/凝聚/爬墙/下砸等状态。</summary>
        private bool CanCastScream()
        {
            return _soul >= CharmEffects.SpellSoulCost() && !_screaming &&
                   !_isSitting && !_sitStandingUp && !_isDead && !_respawnFadeOut &&
                   !_hurt && !_focusing && !_fireballCasting && !_diving &&
                   !_dashing &&
                   !_superCharging && !_superDashing && !_nailArtSlashing && !_dreamNailing &&
                   _pendingReposition <= 0;
        }

        /// <summary>同时按住 S 与上键：消耗 30 灵魂开始尖啸，滞空 0.62s，期间锁输入。</summary>
        private void StartScream()
        {
            if (!_soulInfinite)
            {
            _soul = Mathf.Max(0, _soul - CharmEffects.SpellSoulCost());
            }
            _screaming = true;
            _screamTimer = ScreamAnimTime;
            _screamTickTimer = ScreamDamageStart; // 短暂延迟后开始伤害
            _screamTickIndex = 0;
            _screamHitCounts.Clear();
            _screamAreaHits.Clear();
            _screamTickTargets.Clear();
            _screamTickDone = 0;
            _screamDamageApplied = 0;
            CancelWallJumpForAttack(); // 蹬墙跳期间尖啸：终止蹬墙跳，优先施法
            DashAudio.PlayScreamCast(); // 释放瞬间播放深渊尖啸音效
            SpawnScreamParticles();      // 黑/白粒子爆发（白 50 + 黑 100）
            CancelPogoRebound(); // 下劈回弹期间施法：终止回弹
            // 打断普攻/二段跳/爬墙/火球/下砸状态
            _attacking = false;
            _attackTimer = 0f;
            _swingHits.Clear();
            DestroyHitbox();
            _fxTex = null;
            _doubleJumping = false;
            _doubleJumpTimer = 0f;
            _onWall = false;
            _fireballCasting = false;
            _fireballs.Clear();
            _diving = false; // 取消下砸本体状态，但保留已落地的骨剑/尖刺动画继续走完
            _currentClip = null; // 立即切到尖啸施法动画
            // 特效帧
            if (_clips.TryGetValue("Scream Blast", out ClipData blast) && blast.frames.Length > 0)
            {
                _screamBlastFrames = blast.frames;
                _screamBlastFps = blast.fps;
                _screamBlastTimer = 0f;
                _screamBlastSprite = blast.frames[0];
            }
            EnsureScreamHitbox();
            // 战斗详情界面显示期间，尖啸释放瞬间直接进入战斗
            TryOpenNearBattleByDiveOrScream();
        }

        /// <summary>尖啸瞬间从骑士身上爆发黑/白圆形粒子（约 150 个：白 50 + 黑 100），更快、逸散范围更大。</summary>
        private void SpawnScreamParticles()
        {
            const int total = 150;
            for (int i = 0; i < total; i++)
            {
                bool white = i < 50;
                _screamParticles.Add(new LightDotParticle
                {
                    X = X + (UnityEngine.Random.value - 0.5f) * 1.2f,
                    Y = Y + (UnityEngine.Random.value - 0.5f) * 1.0f,
                    Vx = (UnityEngine.Random.value - 0.5f) * 10f,   // 更快的水平逸散
                    Vy = -UnityEngine.Random.Range(2f, 8.5f),       // 更快的向上爆发
                    TexIndex = 1,
                    Age = 0f,
                    Life = UnityEngine.Random.Range(1f, 1.2f),
                    Size = white
                        ? UnityEngine.Random.Range(0.16f, 0.24f)
                        : UnityEngine.Random.Range(0.2f, 0.3f),
                    Color = white
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(15, 15, 20, 255),
                    Index = 0,
                    Layer = UnityEngine.Random.value < 0.8f ? 0 : 1, // 80% 在身后层
                    TravelDist = 0f,
                    Braked = false,
                    BrakeDist = UnityEngine.Random.Range(2.5f, 5f) // 移动 2.5~5 格后大幅减速
                });
            }
        }

        /// <summary>每帧推进尖啸：滞空、0.1s 一次的伤害判定（最多 5 次）、特效帧推进。</summary>
        private void UpdateScream()
        {
            if (!_screaming)
            {
                return;
            }
            float sdt = Time.deltaTime;
            _screamTimer -= sdt;
            Vy = 0f; // 滞空：尖啸期间不会下落（下方 goto 跳过重力）
            // 尖啸判定区域内的魔力草一并破坏（覆盖三角+圆判定箱范围）
            BreakManaWeeds(_screamAreaHits, X, Y - 2f, 2.8f, 3.2f);
            BreakBugWallsInArea(_screamAreaHits, X, Y - 2f, 2.8f, 3.2f);
            // 伤害判定
            _screamTickTimer -= sdt;
            if (_screamTickTimer <= 0f && _screamTickIndex < ScreamTickCount)
            {
                CheckScreamHit();
                _screamTickIndex++;
                _screamTickTimer = ScreamTickInterval;
            }
            // 特效帧推进（一次性播完）
            if (_screamBlastFrames != null)
            {
                _screamBlastTimer += sdt;
                int bi = (int)(_screamBlastTimer * _screamBlastFps);
                _screamBlastSprite = bi < _screamBlastFrames.Length ? _screamBlastFrames[bi] : null;
            }
            if (_screamTimer <= 0f)
            {
                _screaming = false;
                _currentClip = null;
                _screamBlastFrames = null;
                _screamBlastSprite = null;
                DestroyScreamHitbox();
            }
        }

        // ================= 虚空解放（梦之门技能）=================

        /// <summary>能否开始虚空解放：非战斗状态、不忙、地面。</summary>
        private bool CanStartVoidLiberation()
        {
            // 已入战：灵魂不足 30 不能释放（入战消耗至多 100、至少 30）
            bool soulOk = true;
            try
            {
                if (CharmEffects.IsInBattle())
                {
                    soulOk = _soul >= 30;
                }
            }
            catch (Exception)
            {
            }
            return _voidPhase == 0 && soulOk && !_isDead && !_respawnFadeOut &&
                   !_hurt && !_isSitting && !_sitStandingUp && !_focusing &&
                   !_fireballCasting && !_screaming && !_diving && !_dashing &&
                   !_superCharging && !_superDashing && !_nailArtCharging &&
                   !_nailArtSlashing && !_dashSlashing && !_cycloneSlashing &&
                   !_dreamNailing && !_attacking && _pendingReposition <= 0 &&
                   _swimState == 0;
        }

        /// <summary>按住 1s 达成：进入前摇（阶段 2）。</summary>
        private void StartVoidLiberation()
        {
            _voidPhase = 2;
            _voidTimer = 0f;
            _voidFlashAlpha = 0f;
            _voidFlashBlack = false;
            _voidBlackFlashTimer = 0f;
            _voidWhiteFlashTimer = 0f;
            _voidExitTimer = 0f;
            _voidZoomRestoreTimer = 0f; // 重新开始前摇时，终止上一次的镜头收回
            _voidChargeHurtDone = false; // 前摇受伤次数重置
            _currentClip = null; // 立即切到前摇动画
            _attacking = false;
            _attackTimer = 0f;
            _swingHits.Clear();
            DestroyHitbox();
            _fxTex = null;
            _dreamNailing = false;
            _dashing = false;
            // 记录相机原缩放，前摇结束/技能结束后恢复
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.Cam != null)
                {
                    var fi = AccessTools.Field(typeof(M2Camera), "scale");
                    if (fi != null)
                    {
                        _voidZoomBase = (float)fi.GetValue(m2d.Cam);
                    }
                }
            }
            catch (Exception)
            {
                _voidZoomBase = 1f;
            }
            // 前摇音效：蓄力音 + 挑战音同时播放
            DashAudio.PlayVoidCharge();
        }

        /// <summary>前摇 frame0011 播放瞬间：黑屏 0.1s → 击倒音 + 尖叫（阶段 3）。</summary>
        private void StartVoidStrike()
        {
            _voidPhase = 3;
            _voidTimer = 0f;
            _voidFlashAlpha = 1f;
            _voidFlashBlack = true;
            _voidBlackFlashTimer = VoidFlashTime;
            _currentClip = null; // 切到尖叫动画
            // 伤害段：入战才消耗灵魂（至多 100、至少 30，每 10 魂一段）；未入战不消耗
            _voidSegments = 0;
            _voidSegFired = 0;
            _voidSegTimer = 0f;
            _voidSlashTimer = 0f;
            _voidSlashTargets.Clear();
            try
            {
                if (CharmEffects.IsInBattle())
                {
                    int consume = Mathf.Min(_soul, 100);
                    _soul = Mathf.Max(0, _soul - consume);
                    _voidSegments = Mathf.Max(1, consume / 10);
                }
            }
            catch (Exception)
            {
            }
            _voidSegInterval = _voidSegments > 0 ? VoidStrikeTime / _voidSegments : 0f;
        }

        /// <summary>出伤 3s 结束：白屏 0.1s → 后摇动画（阶段 4）。</summary>
        private void StartVoidRecovery()
        {
            _voidPhase = 4;
            _voidExitTimer = 0f;
            _voidFlashAlpha = 1f;
            _voidFlashBlack = false;
            _voidWhiteFlashTimer = VoidFlashTime;
            _voidTentacleActive = false; // 白屏开始，触手消失
            _voidSlashTargets.Clear();   // 出伤结束，目标划痕消失
            _currentClip = null;
        }

        /// <summary>后摇播完：恢复相机缩放与自由行动。</summary>
        private void EndVoidLiberation()
        {
            _voidPhase = 0;
            _voidTimer = 0f;
            _voidExitTimer = 0f;
            _voidFlashAlpha = 0f;
            _voidTentacleActive = false;
            _voidSlashTargets.Clear();
            _voidChargeHurtDone = false;
            _currentClip = null;
            // 镜头收回由 _voidZoomRestoreTimer 继续推进（技能结束后仍收完最后一段）
        }

        /// <summary>每帧推进虚空解放（蓄力判定 / 前摇 / 出伤 / 后摇 / 闪屏衰减 / 相机缩放）。</summary>
        private void UpdateVoidLiberation(float dt)
        {
            // 注意：外部传入的 dt = Time.deltaTime*60（帧数），这里一律用真实秒
            float sdt = Time.deltaTime;
            // 镜头收回：第二段播完后开始，1s 内 1.6 → 1（技能结束后继续收完）
            if (_voidZoomRestoreTimer > 0f)
            {
                _voidZoomRestoreTimer -= sdt;
                float t = Mathf.Clamp01(1f - _voidZoomRestoreTimer / VoidZoomRestoreTime);
                ApplyVoidCameraZoom(Mathf.Lerp(VoidZoomIn, _voidZoomBase, t));
                if (_voidZoomRestoreTimer <= 0f)
                {
                    ApplyVoidCameraZoom(_voidZoomBase);
                }
            }
            // 屏幕边缘虚空触手：黑屏结束出现，白屏开始消失
            if (_voidTentacleActive)
            {
                _voidTentacleTimer += sdt;
            }
            if (_voidPhase == 0)
            {
                return;
            }

            // 闪屏衰减
            if (_voidFlashAlpha > 0f)
            {
                if (_voidFlashBlack && _voidBlackFlashTimer > 0f)
                {
                    _voidBlackFlashTimer -= sdt;
                    if (_voidBlackFlashTimer <= 0f)
                    {
                        _voidFlashAlpha = 0f;
                        DashAudio.PlayVoidStrike(); // 黑屏结束 → 击倒音
                        _voidTentacleActive = true; // 同时屏幕边缘出现虚空触手
                        _voidTentacleTimer = 0f;
                        _voidZoomRestoreTimer = VoidZoomRestoreTime; // 黑屏结束，开始收回镜头
                        // 每根触手随机起始帧（数量按屏幕宽度可能变化，给足 48 个槽位）
                        int frameCount = _voidTentacleData != null && _voidTentacleData.Length > 0
                            ? _voidTentacleData.Length
                            : 1;
                        _voidTentacleOffsets = new int[48];
                        for (int oi = 0; oi < _voidTentacleOffsets.Length; oi++)
                        {
                            _voidTentacleOffsets[oi] = UnityEngine.Random.Range(0, frameCount);
                        }
                    }
                }
                else if (!_voidFlashBlack && _voidWhiteFlashTimer > 0f)
                {
                    _voidWhiteFlashTimer -= sdt;
                    if (_voidWhiteFlashTimer <= 0f)
                    {
                        _voidFlashAlpha = 0f;
                    }
                }
            }

            if (_voidPhase == 2)
            {
                _voidTimer += sdt;
                // 镜头缓慢拉近：2s 内 1 → 1.3
                ApplyVoidCameraZoom(Mathf.Lerp(1f, VoidZoomIn, Mathf.Clamp01(_voidTimer / VoidChargeTime)));
                // 前摇 12 帧（6fps）播满 2s 后才进入出伤
                if (_voidTimer >= VoidChargeTime)
                {
                    StartVoidStrike();
                }
            }
            else if (_voidPhase == 3)
            {
                _voidTimer += sdt;
                // 第二段期间镜头保持拉近，播完才收回
                // 攻击段：按段间隔触发，全部在出伤段内打完
                if (_voidSegments > 0)
                {
                    _voidSegTimer += sdt;
                    while (_voidSegFired < _voidSegments &&
                           _voidSegInterval > 0f && _voidSegTimer >= _voidSegInterval)
                    {
                        _voidSegTimer -= _voidSegInterval;
                        _voidSegFired++;
                        FireVoidSegment();
                    }
                }
                // 目标划痕动画计时
                if (_voidSlashTargets.Count > 0)
                {
                    _voidSlashTimer += sdt;
                }
                if (_voidTimer >= VoidStrikeTime)
                {
                    StartVoidRecovery();
                }
            }
            else if (_voidPhase == 4)
            {
                // 白屏 0.1s 后才开始播后摇动画；后摇总时长 0.35s
                if (_voidWhiteFlashTimer <= 0f)
                {
                    _voidExitTimer += sdt;
                    float exitDur = Mathf.Max(0.01f, VoidRecoveryTime - VoidFlashTime);
                    if (_voidExitTimer >= exitDur)
                    {
                        EndVoidLiberation();
                    }
                }
            }
        }

        /// <summary>按技能计时器精确选帧并覆盖当前渲染帧（不走 PlayClip 的全局动画倍率）。</summary>
        private void ApplyVoidLiberationFrame()
        {
            if (_voidPhase < 2 || _voidPhase > 4)
            {
                return;
            }
            string clipName = _voidPhase == 2
                ? "VoidEnter"
                : _voidPhase == 3 ? "VoidScream" : "VoidExit";
            if (!_clips.TryGetValue(clipName, out ClipData clip) || clip.frames.Length == 0)
            {
                return;
            }
            int idx;
            if (_voidPhase == 3)
            {
                // 尖叫帧 020002~020007 循环：整段出伤内循环 VoidScreamCycles 遍，
                // 帧率随出伤时长自动加快（3s → 6帧×4遍 = 8fps）
                float effFps = VoidScreamCycles * clip.frames.Length / VoidStrikeTime;
                int raw = (int)(_voidTimer * effFps);
                idx = Mathf.Min(raw, (int)(clip.frames.Length * VoidScreamCycles) - 1) % clip.frames.Length;
            }
            else if (_voidPhase == 4)
            {
                // 后摇：白屏 0.1s 后，剩余 0.25s 内播完 7 帧
                float animT = Mathf.Max(0f, _voidExitTimer);
                float animDur = Mathf.Max(0.01f, VoidRecoveryTime - VoidFlashTime);
                idx = Mathf.Clamp((int)(animT / animDur * clip.frames.Length), 0, clip.frames.Length - 1);
            }
            else
            {
                int raw = (int)(_voidTimer * clip.fps);
                idx = Mathf.Clamp(raw, 0, clip.frames.Length - 1);
            }
            string spriteName = clip.frames[idx];
            if (_textures.TryGetValue(spriteName, out Texture2D tex))
            {
                _currentSpriteName = spriteName;
                _currentTex = tex;
            }
        }

        /// <summary>应用镜头缩放（animateScaleTo 立即生效）。</summary>
        private void ApplyVoidCameraZoom(float scale)
        {
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.Cam != null)
                {
                    m2d.Cam.animateScaleTo(scale, 0);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 触发一段虚空解放伤害：对当前战斗区域边界内所有敌人造成其生命上限 10% 的伤害，
        /// 并记录目标（出伤结束前在其中心循环渲染 Radiance_GG_slashes）。
        /// </summary>
        private void FireVoidSegment()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            try
            {
                EnemySummoner active = EnemySummoner.ActiveScript;
                M2LpSummon area = active != null ? active.getSummonedArea() : null;
                if (area == null || area.Mp != _mp)
                {
                    return;
                }
                Transform mapT = _mp.gameObject.transform;
                Vector3 p0 = mapT.TransformPoint(new Vector3(
                    _mp.pixel2ux(area.mapx * _mp.CLEN),
                    _mp.pixel2uy(area.mapy * _mp.CLEN), 0f));
                Vector3 p1 = mapT.TransformPoint(new Vector3(
                    _mp.pixel2ux((area.mapx + area.mapw) * _mp.CLEN),
                    _mp.pixel2uy((area.mapy + area.maph) * _mp.CLEN), 0f));
                Vector2 center = new Vector2((p0.x + p1.x) * 0.5f, (p0.y + p1.y) * 0.5f);
                Vector2 size = new Vector2(Mathf.Abs(p1.x - p0.x), Mathf.Abs(p1.y - p0.y));
                if (size.x <= 0.01f || size.y <= 0.01f)
                {
                    return;
                }
                Collider2D[] hits = Physics2D.OverlapBoxAll(center, size, 0f, mask);
                var hitEnemies = new HashSet<NelEnemy>();
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null || !hitEnemies.Add(enemy))
                    {
                        continue;
                    }
                    // 最大生命 10% + 30 伤害（至少 1 点）
                    int maxHp = EnemyMaxHpField != null
                        ? (int)EnemyMaxHpField.GetValue(enemy)
                        : 0;
                    int dmg = Mathf.Max(1, Mathf.CeilToInt(maxHp * 0.1f + 30f));
                    var atk = new NelAttackInfo();
                    atk.hpdmg_current = dmg;
                    atk.hpdmg0 = dmg;
                    atk.fix_damage = true;
                    atk.CenterXy(enemy.x, enemy.y, 0f);
                    PRNoel noel = GetPr();
                    if (noel != null)
                    {
                        atk.Caster = noel;
                        atk.AttackFrom = noel;
                    }
                    atk.PublishMagic = GetKnightAttackMagic();
                    enemy.applyDamage(atk, false);
                    // 记录目标：出伤结束前在其中心循环划痕动画
                    AddVoidSlashTarget(enemy, c);
                }
            }
            catch (Exception)
            {
            }
        }

        private void AddVoidSlashTarget(NelEnemy enemy, Collider2D c)
        {
            try
            {
                for (int i = 0; i < _voidSlashTargets.Count; i++)
                {
                    if (_voidSlashTargets[i].Enemy == enemy)
                    {
                        return;
                    }
                }
                Vector3 offset = Vector3.zero;
                if (c != null && enemy != null)
                {
                    offset = c.bounds.center - enemy.transform.position;
                }
                _voidSlashTargets.Add(new VoidSlashTarget
                {
                    Enemy = enemy,
                    Offset = offset
                });
            }
            catch (Exception)
            {
            }
        }

        // 说明（2026-09-17）：尖啸原来用“三角 PolygonCollider2D + 圆 CircleCollider2D +
        // Physics2D.OverlapCollider”做判定，这既受 ContactFilter2D 默认不含 Trigger 的影响，
        // 又依赖每段才移动一次的查询物体（SimulationMode2D.Script 下位置同步滞后），
        // 实测表现为“一次尖啸只结算一段伤害”。现在判定改成静态查询（见 CheckScreamHit），
        // 不再需要判定物体；这里的 Ensure/Destroy 保留为空实现以兼容既有的清理调用点。
        private void EnsureScreamHitbox()
        {
        }

        private void DestroyScreamHitbox()
        {
        }

        /// <summary>
        /// 尖啸伤害判定（每 0.1 秒一次，共 5 段）：
        /// 三角区（顶点在骑士中心、底边在上方 3 格）用 6 层水平切片精确覆盖；
        /// 圆形区（中心在骑士上方 3.5 格、半径 2.95）用 OverlapCircleAll。
        ///
        /// 两者都用本模组其它招式验证过的**静态物理查询**（Physics2D.OverlapBoxAll / OverlapCircleAll），
        /// 不再用 Physics2D.OverlapCollider + ContactFilter2D：
        /// ① ContactFilter2D 的 useTriggers 默认 false，敌方可命中碰撞体若是 Trigger 会被整片漏掉；
        /// ② 该查询依赖“每段才移动一次的查询物体”，其形状/位置同步时机不受本模组控制。
        /// 这两点都可能造成漏判（都表现为“一次尖啸只结算一段伤害”）。
        /// 同一个目标每段只结算一次；每只怪/每个可攻击目标一次尖啸最多 5 段。
        /// </summary>
        private void CheckScreamHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            _screamTickDone++;
            _screamTickTargets.Clear();
            // 尖啸：破坏范围内的蛛丝球释放源（蜘蛛陷阱）
            // 尖啸判定主要向上（三角 + 圆：圆中心在骑士上方 3.5 格、半径 2.95），
            // 陷阱破坏框覆盖整个范围，避免高处的陷阱漏掉
            DestroySpiderTrapsInBox(
                new Vector2(X, Y - ScreamCircleOffsetY + ScreamCircleRadius * 0.5f),
                ScreamCircleRadius, ScreamCircleOffsetY - ScreamCircleRadius * 0.5f + ScreamCircleRadius);
            // 判定基准：骑士中心的世界坐标。原来的三角/圆碰撞体是挂在“未缩放的世界根物体”上，
            // 局部点/半径就是世界单位，所以这里所有偏移与尺寸都按世界单位、沿世界 +y（上方）叠加，
            // 与旧碰撞体形状严格等价。
            Vector2 w0 = CellPosToWorld(X, Y);
            // ---- 三角区：水平切片（第 i 层中心距骑士中心 h，半宽按高度线性插值）----
            const int slices = 6;
            float sliceH = ScreamTriHeight / slices;
            for (int i = 0; i < slices; i++)
            {
                float h = (i + 0.5f) * sliceH;                       // 距骑士中心的高度（世界单位，向上）
                float halfW = ScreamTriHalfWidth * (h / ScreamTriHeight);
                Vector2 center = w0 + new Vector2(0f, h);
                // 切片高度略微重叠（×1.05），避免层与层之间出现判定缝隙
                Collider2D[] hits = Physics2D.OverlapBoxAll(center,
                    new Vector2(halfW * 2f, sliceH * 1.05f), 0f, mask);
                for (int k = 0; k < hits.Length; k++)
                {
                    TryScreamDamage(hits[k]);
                }
            }
            // ---- 圆形区 ----
            Vector2 circleCenter = w0 + new Vector2(0f, ScreamCircleOffsetY);
            Collider2D[] circleHits = Physics2D.OverlapCircleAll(circleCenter, ScreamCircleRadius, mask);
            for (int k = 0; k < circleHits.Length; k++)
            {
                TryScreamDamage(circleHits[k]);
            }
        }

        private void TryScreamDamage(Collider2D c)
        {
            if (c == null)
            {
                return;
            }
            NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
            enemy = ResolveDamageTarget(enemy);
            if (enemy == null)
            {
                // 通用可攻击目标（的当て靶 / 拳炮 / 联机远端小骑士代理等）也要按“段”结算：
                // 若沿用普攻那样的整段去重集合，尖啸对这些目标只会出一段伤害。
                M2Attackable generic = c.GetComponentInParent<M2Attackable>();
                if (generic == null || generic is PR || generic is M2MoverPr)
                {
                    return;
                }
                if (!_screamTickTargets.Add(generic))
                {
                    return;
                }
                int ng;
                _screamHitCounts.TryGetValue(generic, out ng);
                if (ng >= ScreamTickCount)
                {
                    return; // 每个目标一次尖啸最多 5 段
                }
                if (TryHitGenericAttackable(c, CharmEffects.ScaleSpellDamage(ScreamDamage), null, 2))
                {
                    _screamHitCounts[generic] = ng + 1;
                    _screamDamageApplied++;
                }
                return;
            }
            if (!_screamTickTargets.Add(enemy))
            {
                return; // 同一段内该目标已结算（多碰撞体不重复吃同一段）
            }
            int n;
            _screamHitCounts.TryGetValue(enemy, out n);
            if (n >= ScreamTickCount)
            {
                return; // 每只怪最多吃 5 段
            }
            _screamHitCounts[enemy] = n + 1;
            _screamDamageApplied++;
            // suppress_pop：尖啸是多段驻留判定，抑制引擎默认上弹，避免目标被弹出判定范围
            ApplyDiveDamage(enemy, CharmEffects.ScaleSpellDamage(ScreamDamage), true);
        }

        // ================= 游泳（水面漂浮 / 水下）=================

        /// <summary>某个地图格是否为液体（水/岩浆/酸水，AIC 均用 water 配置）。</summary>
        private bool IsLiquidCell(int cx, int cy)
        {
            if (_mp == null || cx < 0 || cy < 0 || cx >= _mp.width || cy >= _mp.rows)
            {
                return false;
            }
            return CCON.isWater(_mp.getConfig(cx, cy));
        }

        /// <summary>找到 (cx, cy) 所在列的液面 Y（格，向下为正 = 最顶液体格的上边缘）。</summary>
        private float FindLiquidSurfaceY(int cx, int cy)
        {
            int topRow = cy;
            while (topRow - 1 >= 0 && IsLiquidCell(cx, topRow - 1))
            {
                topRow--;
            }
            return topRow;
        }

        /// <summary>进入液面漂浮：锁定高度、刷新冲刺/二段跳。</summary>
        private void EnterSwimSurface(int cx, int cy)
        {
            _swimRising = false;
            _swimSurfaceY = FindLiquidSurfaceY(cx, cy);
            if (_swimSurfaceY < SwimMinSurfaceRow)
            {
                // 液面在地图顶端（海洋地下城等全水房间）：没有可漂浮的空间，转入水下状态
                _swimState = 2;
                _swimEnterTimer = 0f;
                Grounded = false;
                if (Y < SwimMaxRiseY)
                {
                    Y = SwimMaxRiseY;
                }
                Vy = 0f;
                return;
            }
            // 入水点明显低于液面（超过漂浮位置 0.3 格）：不瞬移到表面，
            // 直接进入水下状态，由玩家按住上键上浮（避免“掉进深水被瞬间拉到水面”）
            float feetNow = Y + SizeY;
            if (feetNow > _swimSurfaceY + SwimSurfaceSink + 0.3f)
            {
                _swimState = 2;
                _swimEnterTimer = 0f;
                Grounded = false;
                Vy = 0f;
                return;
            }
            _swimState = 1;
            _swimEnterTimer = SwimEnterTime;
            Y = _swimSurfaceY + SwimSurfaceSink - SizeY;
            Vy = 0f;
            Grounded = true;
            _canDoubleJump = true;
            _canDash = true;
            _dashCooldown = 0f;
        }

        /// <summary>
        /// 每帧推进游泳状态：
        /// 0=陆地 → 落入液体时进入液面漂浮；1=液面（可慢速移动/跳跃/按下键下潜）；
        /// 2=液内（行动与陆地一致，按住上键上浮直到液面）。
        /// </summary>
        private void UpdateSwimState(ref bool left, ref bool right, ref bool jump, ref bool jumpHeldNow,
            ref bool downPressed, bool lookUpHeldNow, float dt)
        {
            if (_mp == null || _nailArtSlashing)
            {
                return; // 强力劈砍期间不切换游泳状态
            }
            int feetCx = Mathf.FloorToInt(X);
            int feetCy = Mathf.FloorToInt(Y + SizeY);
            int bodyCy = Mathf.FloorToInt(Y + SizeY * 0.5f);
            bool feetLiquid = IsLiquidCell(feetCx, feetCy);
            bool bodyLiquid = IsLiquidCell(feetCx, bodyCy);
            bool inLiquidNow = feetLiquid || bodyLiquid;

            if (_swimState == 0)
            {
                _swimRising = false;
                // 落入液体（下落）→ 进入游泳；或已泡在液体中（含脚底贴住池底的站立姿态）
                // 按住上键 → 进入水下上浮
                if (inLiquidNow && (lookUpHeldNow || (!Grounded && Vy >= 0f)))
                {
                    // 脚底格可能是池底实心格（脚底与液底平齐），此时用身体格向上找液面
                    EnterSwimSurface(feetCx, feetLiquid ? feetCy : bodyCy);
                }
                ProtectNoelFromDrowning(inLiquidNow);
                return;
            }

            if (_swimState == 1)
            {
                _swimRising = false;
                _swimEnterTimer -= Time.deltaTime;
                if (jump)
                {
                    // 液面跳跃：交还正常物理（跳离液面），仍可二段跳/冲刺
                    jump = false;
                    Grounded = false;
                    _swimState = 0;
                    _swimEnterTimer = 0f;
                    Vy = JumpVy;
                    _jumpHeld = true;
                    _jumpCutApplied = false;
                    return;
                }
                if (downPressed)
                {
                    // 按下键：下潜进入液体
                    downPressed = false;
                    _swimState = 2;
                    Grounded = false;
                    Vy = 0.1f;
                    _swimEnterTimer = 0f;
                    return;
                }
                if (!feetLiquid)
                {
                    _swimState = 0; // 游到岸边/液面消失：交还正常物理
                    Grounded = false;
                    return;
                }
                // 保持漂浮在液面
                _swimSurfaceY = FindLiquidSurfaceY(feetCx, feetCy);
                if (_swimSurfaceY < SwimMinSurfaceRow)
                {
                    _swimState = 2; // 漂浮中液面消失到地图顶端：转入水下
                    Grounded = false;
                    _swimEnterTimer = 0f;
                    return;
                }
                // 新液面明显高于当前脚底（浅水 → 深水，两块液体有高度差）：
                // 转入液内，不瞬移到新液面，由玩家按住上键上浮
                float feetNow = Y + SizeY;
                if (feetNow > _swimSurfaceY + SwimSurfaceSink + 0.3f)
                {
                    _swimState = 2;
                    Grounded = false;
                    _swimEnterTimer = 0f;
                    Vy = 0f;
                    return;
                }
                Grounded = true;
                Y = _swimSurfaceY + SwimSurfaceSink - SizeY;
                Vy = 0f;
                ProtectNoelFromDrowning(true);
                return;
            }

            // _swimState == 2：液内（行动与陆地一致）
            if (!inLiquidNow)
            {
                _swimState = 0; // 身体完全离开液体
                _swimRising = false;
                return;
            }
            _swimRising = lookUpHeldNow;
            if (_swimRising)
            {
                // 按住上键：上浮（速度与走路速度一致），直到到达液面；
                // 上浮期间跳过重力与地面吸附（否则在池底按 Q 会被地面每帧吸回，看起来“浮不起来”）
                _swimSurfaceY = FindLiquidSurfaceY(feetCx, feetCy);
                Vy = 0f;
                Y -= SwimRiseSpeed * dt;
                Grounded = false;
                if (_swimSurfaceY >= SwimMinSurfaceRow && Y + SizeY <= _swimSurfaceY + SwimSurfaceSink)
                {
                    EnterSwimSurface(feetCx, feetCy);
                }
                else if (Y < SwimMaxRiseY)
                {
                    Y = SwimMaxRiseY; // 全水房间：上浮到地图顶端即停，不浮出地图
                }
            }
            ProtectNoelFromDrowning(true);
        }

        /// <summary>
        /// 小骑士在水中时，诺艾尔（隐藏跟随）也会泡在水里触发 AIC 溺水机制。
        /// 节流调用她的 M2PrMistApplier.cureAll()，保持氧气满、中止呛水状态，避免她扣血/掉魔法杖。
        /// </summary>
        private void ProtectNoelFromDrowning(bool inWater)
        {
            if (!inWater)
            {
                return;
            }
            _noelO2ProtectTimer -= Time.deltaTime;
            if (_noelO2ProtectTimer > 0f)
            {
                return;
            }
            _noelO2ProtectTimer = 0.25f;
            try
            {
                PRNoel noel = GetPr();
                if (noel == null)
                {
                    return;
                }
                M2PrMistApplier ma = noel.getMistApplier();
                if (ma != null)
                {
                    ma.cureAll();
                }
            }
            catch (Exception)
            {
            }
        }

        // ================= 骨钉技艺·强力劈砍（Great Slash）=================

        /// <summary>
        /// 每帧处理蓄力/松开判定：按下攻击键先等待 0.3s 长按判定（期间不播蓄力动画）；
        /// 长按 0.3s 后开始蓄力；蓄满后松开（且不按上/下/冲刺）释放强力劈砍；
        /// 未蓄满松开 → 转为普通攻击；蓄满但松开时按了上/下/冲刺 → 取消。
        /// </summary>
        private void UpdateNailArt(bool attackPressed, bool attackHeld, bool attackReleased,
            bool upHeld, bool downHeld, bool dashPressed, float dt)
        {
            if (_nailArtSlashing)
            {
                return; // 劈砍推进在 UpdateNailArtSlash
            }
            if (!_nailArtCharging && !_nailArtCharged)
            {
                // 只要按住攻击键就能开始蓄力判定：
                // 入场硬直/攻击后摇期间按下的“瞬间”可能被锁定态吞掉，导致之后一直按住也蓄不上；
                // 因此按下（attackPressed）或当前仍按住（attackHeld）都补记长按，
                // 仅排除真正冲突的状态（坐椅/凝聚/施法/尖啸/下砸/游泳/旋风/梦钉/过图重定位）。
                bool canHoldStart = !_isSitting && !_focusing && !_fireballCasting && !_screaming &&
                                    !_diving && _pendingReposition <= 0 &&
                                    !_cycloneSlashing && !_dreamNailing;
                if (!_nailArtHoldPending && canHoldStart && (attackPressed || attackHeld))
                {
                    _nailArtHoldPending = true;
                    _nailArtHoldTimer = 0f;
                    _nailArtQuickTap = false;
                }
                // 长按 0.3s 后才真正开始蓄力（避免普攻瞬间闪过蓄力粒子帧）
                if (_nailArtHoldPending)
                {
                    if (attackHeld)
                    {
                        _nailArtHoldTimer += Time.deltaTime;
                        if (_nailArtHoldTimer >= NailArtHoldStartTime)
                        {
                            _nailArtHoldPending = false;
                            CancelWallJumpForAttack(); // 蹬墙跳期间蓄力：终止蹬墙跳，优先蓄力
                            _nailArtCharging = true;
                            _nailArtCharged = false;
                            _nailArtChargeTimer = 0f;
                            _nailArtChargeParticles.Clear();
                            DashAudio.PlayNailArtChargeInitiate(); // 蓄力开始音效
                        }
                    }
                    else
                    {
                        // 未到 0.3s 就松开：普通攻击
                        _nailArtHoldPending = false;
                        _nailArtQuickTap = true;
                    }
                }
                return;
            }
            // 蓄力中：继续按住 → 累计时间
            if (attackHeld)
            {
                _nailArtChargeTimer += Time.deltaTime; // 真实秒（之前误用帧单位导致瞬间蓄满）
                // 护符35 骨钉大师的荣耀：蓄力时间 1.35s → 0.75s
                float chargeTime = CharmEffects.IsEquipped(CharmEffects.NailMasterId)
                    ? NailArtChargeTimeFast
                    : NailArtChargeTime;
                if (!_nailArtCharged && _nailArtChargeTimer >= chargeTime)
                {
                    _nailArtCharged = true;
                    DashAudio.PlayNailArtChargeComplete(); // 蓄满完成音效
                    DashAudio.PlayNailArtChargeLoop();     // 蓄满后循环音，直到释放
                    // 蓄满：白色粒子迅速淡出，切换为光圈（nail_charge_effect）
                    for (int pi = 0; pi < _nailArtChargeParticles.Count; pi++)
                    {
                        LightDotParticle pd = _nailArtChargeParticles[pi];
                        pd.Life = Mathf.Min(pd.Life, pd.Age + NailArtChargeParticleFade);
                    }
                    if (_clips.TryGetValue("Nail Art Glow", out ClipData glow) && glow.frames.Length > 0)
                    {
                        _nailArtGlowFrames = glow.frames;
                        _nailArtGlowFps = glow.fps;
                        _nailArtGlowTimer = 0f;
                        _nailArtGlowSprite = glow.frames[0];
                    }
                }
                // 蓄力开始到蓄满：每帧生成 2 个白色粒子向中心收敛
                if (!_nailArtCharged)
                {
                    SpawnNailArtChargeParticles(NailArtChargeParticlePerFrame);
                }
                // 光圈帧循环推进
                if (_nailArtCharged && _nailArtGlowFrames != null)
                {
                    _nailArtGlowTimer += Time.deltaTime;
                    int gi = (int)(_nailArtGlowTimer * _nailArtGlowFps);
                    _nailArtGlowSprite = _nailArtGlowFrames[gi % _nailArtGlowFrames.Length];
                }
                return;
            }
            // 松开攻击键
            if (!attackReleased)
            {
                return;
            }
            if (_nailArtCharged && _dashing)
            {
                // 冲刺期间松开蓄满的攻击键：冲刺劈砍（等冲刺到达最大位移后触发）
                _dashSlashPending = true;
                _nailArtCharging = false;
                _nailArtCharged = false;
                _nailArtChargeParticles.Clear();
                _nailArtGlowFrames = null;
                _nailArtGlowSprite = null;
                DashAudio.StopNailArtChargeLoop();
            }
            else if (_nailArtCharged && (upHeld || downHeld))
            {
                // 按住上或下松开：旋风劈砍
                StartCycloneSlash();
            }
            else if (_nailArtCharged && !upHeld && !downHeld && !dashPressed)
            {
                StartNailArtSlash();
            }
            else if (_nailArtCharged)
            {
                // 蓄满但松开时按了上/下/冲刺：取消，不劈砍
                _nailArtCharging = false;
                _nailArtCharged = false;
                _nailArtChargeParticles.Clear();
                DashAudio.StopNailArtChargeLoop(); // 取消蓄力：停止循环音
                _nailArtGlowFrames = null;
                _nailArtGlowSprite = null;
            }
            else
            {
                // 快速点击（未蓄满）：转为普通攻击
                _nailArtCharging = false;
                _nailArtQuickTap = true;
            }
        }

        /// <summary>蓄力期间每帧生成 count 个白色粒子，以 2.5~3 格/秒匀速向中心偏下 0.5 格处收敛。</summary>
        private void SpawnNailArtChargeParticles(int count)
        {
            for (int i = 0; i < count; i++)
            {
                float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                float rad = UnityEngine.Random.Range(1f, 1.8f);
                _nailArtChargeParticles.Add(new LightDotParticle
                {
                    X = X + Mathf.Cos(ang) * rad,
                    Y = Y + Mathf.Sin(ang) * rad,
                    Vx = 0f,
                    Vy = 0f,
                    TargetX = X,
                    TargetY = Y + 0.5f,
                    Speed = UnityEngine.Random.Range(NailArtChargeParticleSpeedMin, NailArtChargeParticleSpeedMax),
                    TexIndex = 1,
                    Age = 0f,
                    Life = NailArtChargeParticleMaxLife,
                    Size = UnityEngine.Random.Range(0.1f, 0.18f),
                    Color = new Color32(255, 255, 255, 255),
                    Index = 0,
                    Layer = 0
                });
            }
        }

        /// <summary>蓄满松开：开始强力劈砍（0.5s，105 伤害，空中缓慢下落）。</summary>
        private void StartNailArtSlash()
        {
            DashAudio.StopNailArtChargeLoop(); // 释放强力劈砍：停止蓄满循环音
            _nailArtCharging = false;
            _nailArtCharged = false;
            _nailArtSlashing = true;
            _nailArtSlashTimer = NailArtSlashTime;
            _nailArtSlashDir = -_faceDir; // 面朝方向（与普攻一致）
           _nailArtHits.Clear();
            _nailArtSoulGranted = false; // 新一次蓄力劈砍：重置灵魂结算
            _nailArtDwell.Clear();
            _nailArtWeedHits.Clear();
            _nailArtChargeParticles.Clear();
            _nailArtGlowFrames = null;
            _nailArtGlowSprite = null;
            _currentClip = null; // 切到劈砍动画
            if (_clips.TryGetValue("Nail Art Slash Effect", out ClipData fx) && fx.frames.Length > 0)
            {
                _nailArtSlashFxFrames = fx.frames;
                _nailArtSlashFxFps = fx.fps;
                _nailArtSlashFxTimer = 0f;
                _nailArtSlashFxSprite = fx.frames[0];
            }
            DashAudio.PlayNailArtGreatSlash();
        }

        /// <summary>每帧推进强力劈砍：伤害结算、剑气帧推进、到时结束。</summary>
        private void UpdateNailArtSlash()
        {
            if (!_nailArtSlashing)
            {
                return;
            }
            _nailArtSlashTimer -= Time.deltaTime;
            // 蓄力劈砍破坏判定范围内的魔力草（+4 灵魂，去重）
            if (_mp != null)
            {
                float hitX = X + _nailArtSlashDir * NailArtHitboxOffX;
                float hitY = Y + NailArtHitboxOffY;
                BreakManaWeeds(_nailArtWeedHits, hitX, hitY,
                    NailArtHitboxW * 0.5f, NailArtHitboxH * 0.5f);
                BreakBugWallsInArea(_nailArtWeedHits, hitX, hitY,
                    NailArtHitboxW * 0.5f, NailArtHitboxH * 0.5f); // 一次命中即破坏虫墙
            }
            // 伤害：目标进入判定箱的瞬间立即受到一次伤害；
        // 之后检测目标在箱内的驻留时间，每 0.15s 追加一次，每只怪最多 3 次（42×3=126）
            CheckNailArtHit();
            if (_nailArtSlashFxFrames != null)
            {
                _nailArtSlashFxTimer += Time.deltaTime;
                int idx = (int)(_nailArtSlashFxTimer * _nailArtSlashFxFps);
                _nailArtSlashFxSprite = idx < _nailArtSlashFxFrames.Length ? _nailArtSlashFxFrames[idx] : null;
            }
            if (_nailArtSlashTimer <= 0f)
            {
                _nailArtSlashing = false;
                _currentClip = null;
                _nailArtSlashFxFrames = null;
                _nailArtSlashFxSprite = null;
                _nailArtSlashTimer = 0f;
            }
        }

        /// <summary>
        /// 强力劈砍判定箱：面朝方向前的长方形。
        /// 目标进入判定箱的瞬间立即受到一次伤害；之后每 0.15s 驻留追加一次伤害，
        /// 每只怪最多 3 次（进箱 1 + 驻留 2，总 126）。离开后驻留计时清零。
        /// </summary>
        private void CheckNailArtHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float hitX = X + _nailArtSlashDir * NailArtHitboxOffX;
            float hitY = Y + NailArtHitboxOffY;
            float mx = _mp.pixel2ux(hitX * _mp.CLEN);
            float my = _mp.pixel2uy(hitY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            DestroySpiderTrapsInBox(new Vector2(hitX, hitY), NailArtHitboxW * 0.5f, NailArtHitboxH * 0.5f);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(NailArtHitboxW, NailArtHitboxH), 0f, mask);
            _nailArtInsideNow.Clear();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 非 NelEnemy 的可攻击目标（的当て靶 / 拳炮 / TD路障 / 联机远端小骑士代理等）：
                    // 与敌人同一套“进箱 1 次 + 驻留 0.15s 追加”、每目标最多 NailArtTickCount 段。
                    // 注意：这些目标以前走 _nailArtGenericHits 这种“整段招式只命中一次”的去重集合，
                    // 所以强力劈砍对靶子只掉一段血；这里改为按段计数（去重集合传 null）。
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga == null || ga is PR || ga is M2MoverPr)
                    {
                        continue;
                    }
                    _nailArtInsideNow.Add(ga);
                    int n2;
                    _nailArtHits.TryGetValue(ga, out n2);
                    if (n2 == 0)
                    {
                        // 进入判定箱瞬间：立即造成一次伤害
                        _nailArtHits[ga] = 1;
                        _nailArtDwell[ga] = 0f;
                        ApplyNailArtGenericHit(c, ga, CharmEffects.ScaleNailDamage(NailArtDamage));
                    }
                    else if (n2 < NailArtTickCount)
                    {
                        float t;
                        _nailArtDwell.TryGetValue(ga, out t);
                        t += Time.deltaTime;
                        if (t >= NailArtDwellInterval)
                        {
                            t -= NailArtDwellInterval;
                            _nailArtHits[ga] = n2 + 1;
                            ApplyNailArtGenericHit(c, ga, CharmEffects.ScaleNailDamage(NailArtDamage));
                        }
                        _nailArtDwell[ga] = t;
                    }
                    continue;
                }
                _nailArtInsideNow.Add(enemy);
                int n;
                _nailArtHits.TryGetValue(enemy, out n);
                if (n == 0)
                {
                    // 进入瞬间：立即造成一次伤害
                    _nailArtHits[enemy] = 1;
                    _nailArtDwell[enemy] = 0f;
                    ApplyNailArtDamage(enemy);
                }
                else if (n < NailArtTickCount)
                {
                    // 驻留计时：每 0.15s 追加一次伤害
                    float t;
                    _nailArtDwell.TryGetValue(enemy, out t);
                    t += Time.deltaTime;
                    if (t >= NailArtDwellInterval)
                    {
                        t -= NailArtDwellInterval;
                        _nailArtHits[enemy] = n + 1;
                        ApplyNailArtDamage(enemy);
                    }
                    _nailArtDwell[enemy] = t;
                }
            }
            // 本帧不在判定箱内的目标：驻留计时清零（重新进入时按新一次进入计）
            if (_nailArtDwell.Count > 0)
            {
                List<object> gone = null;
                foreach (var kv in _nailArtDwell)
                {
                    if (!_nailArtInsideNow.Contains(kv.Key))
                    {
                        if (gone == null)
                        {
                            gone = new List<object>();
                        }
                        gone.Add(kv.Key);
                    }
                }
                if (gone != null)
                {
                    for (int gi = 0; gi < gone.Count; gi++)
                    {
                        _nailArtDwell[gone[gi]] = 0f;
                    }
                }
            }
        }

        /// <summary>
        /// 骨钉技艺命中“非 NelEnemy 目标”的一段伤害：
        /// 联机远端小骑士代理（M2Gunmu）走伤害包伪装通道；其余通用可攻击目标
        /// （的当て靶 / 拳炮 / TD路障等）走通用通道，dedup 传 null
        /// —— 段数由调用方的“进箱 1 次 + 驻留 0.15s 追加”计数控制，不是由去重集合控制。
        /// </summary>
        private void ApplyNailArtGenericHit(Collider2D c, M2Attackable ga, int dmg)
        {
            if (IsRemoteProxy(ga))
            {
                ApplyProxyNailHit(ga, dmg);
                return;
            }
            TryHitGenericAttackable(c, dmg, null, 0);
        }

        private void ApplyNailArtDamage(NelEnemy enemy)
        {
            try
            {
                if (enemy == null)
                {
                    return;
                }
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = CharmEffects.ScaleNailDamage(NailArtDamage);
                atk.hpdmg0 = CharmEffects.ScaleNailDamage(NailArtDamage);
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic(); // 蜘蛛 BOSS 等需要 PublishMagic 判定（织网阶段受击坠落）
                MarkNailArtHit(enemy); // 骨钉技艺击杀记录
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        // ---------- PvP 受击反馈类型（发送给联机远端用于还原诺艾尔的受击动作）----------
        // 0=轻受击（DAMAGE）1=直线击飞（DAMAGE_L）2=着火（DAMAGE_BURNED）3=直线击飞×1.5
        // 默认 0：取不到标记时按轻受击处理（普攻/蓄力劈砍/旋风劈砍都走这一档）。
        internal static byte PendingHitFeedback;
        private static int _feedbackFrame = -100;

        internal static void SetHitFeedback(byte kind)
        {
            PendingHitFeedback = kind;
            _feedbackFrame = Time.frameCount;
        }

        /// <summary>取走本次伤害的反馈类型（同一帧/相邻两帧内有效，其它情况按轻受击）。</summary>
        internal static byte TakeHitFeedback()
        {
            byte k = (Time.frameCount - _feedbackFrame) <= 2 ? PendingHitFeedback : (byte)0;
            PendingHitFeedback = 0;
            return k;
        }

        // ================= 骨钉技艺·冲刺劈砍（Dash Slash）=================

        /// <summary>冲刺到达最大位移时触发冲刺劈砍：横向冲刺段（每 0.02s 衰减四分之一）+ 105 伤害。</summary>
        private void StartDashSlash()
        {
            _dashSlashPending = false;
            _dashSlashing = true;
            _dashSlashTimer = DashSlashTotalTime;
            _dashSlashLungeTimer = DashSlashLungeTime;
            _dashSlashDir = _dashDir; // 冲刺方向
            _dashSlashSpeed = DashSlashStartSpeed;
            _dashSlashDamaged = false;
            _dashSlashHits.Clear();
            _dashSlashWeedHits.Clear();
            _currentClip = null; // 切到冲刺劈砍动画
            if (_clips.TryGetValue("Dash Slash Effect", out ClipData fx) && fx.frames.Length > 0)
            {
                _dashSlashFxFrames = fx.frames;
                _dashSlashFxFps = fx.fps;
                _dashSlashFxTimer = 0f;
                _dashSlashFxSprite = fx.frames[0];
            }
            DashAudio.PlayNailArtGreatSlash(); // 释放音效（同蓄力劈砍）
        }

        /// <summary>每帧推进冲刺劈砍：冲刺段水平位移、105 伤害判定、剑气帧推进。</summary>
        private void UpdateDashSlash()
        {
            if (!_dashSlashing)
            {
                return;
            }
            _dashSlashTimer -= Time.deltaTime;
            // 冲刺劈砍破坏判定范围内的魔力草（+4 灵魂，去重）
            if (_mp != null)
            {
                float hitX = X + _dashSlashDir * DashSlashHitboxOffX;
                float hitY = Y + DashSlashHitboxOffY;
                BreakManaWeeds(_dashSlashWeedHits, hitX, hitY,
                    DashSlashHitboxW * 0.5f, DashSlashHitboxH * 0.5f);
                BreakBugWallsInArea(_dashSlashWeedHits, hitX, hitY,
                    DashSlashHitboxW * 0.5f, DashSlashHitboxH * 0.5f); // 一次命中即破坏虫墙
            }
            // 冲刺段：每 0.02s 速度衰减四分之一
            if (_dashSlashLungeTimer > 0f)
            {
                _dashSlashLungeTimer -= Time.deltaTime;
                _dashSlashSpeed *= Mathf.Pow(DashSlashDecayFactor, Time.deltaTime / 0.02f);
                float dx = _dashSlashDir * _dashSlashSpeed * Time.deltaTime * 60f;
                X += dx;
                if (HitWall())
                {
                    X -= dx;
                    _dashSlashLungeTimer = 0f;
                }
            }
            // 105 伤害：斩击瞬间一次
            if (!_dashSlashDamaged)
            {
                _dashSlashDamaged = true;
                CheckDashSlashHit();
            }
            // 剑气帧推进
            if (_dashSlashFxFrames != null)
            {
                _dashSlashFxTimer += Time.deltaTime;
                int idx = (int)(_dashSlashFxTimer * _dashSlashFxFps);
                _dashSlashFxSprite = idx < _dashSlashFxFrames.Length ? _dashSlashFxFrames[idx] : null;
            }
            if (_dashSlashTimer <= 0f)
            {
                _dashSlashing = false;
                _currentClip = null;
                _dashSlashFxFrames = null;
                _dashSlashFxSprite = null;
                _dashSlashTimer = 0f;
            }
        }

        /// <summary>冲刺劈砍判定箱：冲刺方向前的长方形（5×2 格），命中敌人造成 105 伤害（每只一次）。</summary>
        private void CheckDashSlashHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float hitX = X + _dashSlashDir * DashSlashHitboxOffX;
            float hitY = Y + DashSlashHitboxOffY;
            float mx = _mp.pixel2ux(hitX * _mp.CLEN);
            float my = _mp.pixel2uy(hitY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            DestroySpiderTrapsInBox(new Vector2(hitX, hitY), DashSlashHitboxW * 0.5f, DashSlashHitboxH * 0.5f);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(DashSlashHitboxW, DashSlashHitboxH), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    TryHitGenericAttackable(c, CharmEffects.ScaleNailDamage(DashSlashDamage), _dashSlashHits, 3);
                    continue;
                }
                if (!_dashSlashHits.Add(enemy))
                {
                    continue;
                }
                ApplyDashSlashDamage(enemy);
            }
        }

        private void ApplyDashSlashDamage(NelEnemy enemy)
        {
            try
            {
                if (enemy == null)
                {
                    return;
                }
                SetHitFeedback(3); // 冲刺劈砍 → 直线击飞（1.5 倍距离）
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = CharmEffects.ScaleNailDamage(DashSlashDamage);
                atk.hpdmg0 = CharmEffects.ScaleNailDamage(DashSlashDamage);
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                MarkNailArtHit(enemy); // 骨钉技艺击杀记录
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>护符33 锋利之影：暗影冲刺期间穿过敌人，对每个敌人造成一次当前骨钉伤害。</summary>
        private void CheckShadowDashEnemyHit()
        {
            if (!_isShadowDash || !CharmEffects.IsEquipped(CharmEffects.ShadowId) ||
                _mp == null || _mp.gameObject == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            try
            {
                // 范围放宽：判定中心下移到脚部附近，覆盖骑士全身并探到地面，照顾趴地的小体型怪
                float mx = _mp.pixel2ux((X + HurtCenterX) * _mp.CLEN);
                float my = _mp.pixel2uy((Y + 0.35f) * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(1.6f, 2.2f), 0f, mask);
                if (hits == null)
                {
                    return;
                }
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D c = hits[i];
                    if (c == null)
                    {
                        continue;
                    }
                    NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                        if (ga != null && !(ga is PR) && !(ga is M2MoverPr) &&
                            IsRemoteProxy(ga) && RegisterShadowDashPlayerHit(ga))
                        {
                            ApplyShadowDashToGeneric(ga);
                        }
                        continue;
                    }
                    if (!_shadowDashHits.Add(enemy))
                    {
                        continue;
                    }
                    ApplyShadowDashDamage(enemy);
                }
                // 兜底：直接按“骑士中心 ↔ 远端玩家中心”的距离检测触碰。
                // 远端代理碰撞体偶尔因层/重建顺序漏查，这里保证暗影冲刺擦到就触发直线击退。
                try
                {
                    M2Attackable[] movers = Resources.FindObjectsOfTypeAll<M2Attackable>();
                    float curCx = X + HurtCenterX;
                    float curCy = Y + SizeY * 0.5f;
                    Collider2D[] selfCols = GetComponentsInChildren<Collider2D>(true);
                    if (!_shadowDashPrevValid)
                    {
                        _shadowDashPrevCx = curCx;
                        _shadowDashPrevCy = curCy;
                        _shadowDashPrevValid = true;
                    }
                    int remoteCount = 0;
                    float nearest = 9999f;
                    for (int i = 0; i < movers.Length; i++)
                    {
                        M2Attackable ga = movers[i];
                        if (ga == null || ga is PR || ga is M2MoverPr || !IsRemoteProxy(ga))
                        {
                            continue;
                        }
                        remoteCount++;
                        float dx = Mathf.Abs(ga.x - curCx);
                        float dy = Mathf.Abs(ga.y - curCy);
                        nearest = Mathf.Min(nearest, Mathf.Max(dx, dy));
                        float swept = DistancePointToSegment(ga.x, ga.y,
                            _shadowDashPrevCx, _shadowDashPrevCy, curCx, curCy);
                        bool boundsHit = false;
                        if (selfCols != null && selfCols.Length > 0)
                        {
                            Collider2D[] proxyCols = ga.GetComponentsInChildren<Collider2D>(true);
                            for (int a = 0; a < selfCols.Length && !boundsHit; a++)
                            {
                                Collider2D sc = selfCols[a];
                                if (sc == null) continue;
                                for (int b = 0; b < proxyCols.Length; b++)
                                {
                                    Collider2D pc = proxyCols[b];
                                    if (pc != null && sc.bounds.Intersects(pc.bounds))
                                    {
                                        boundsHit = true;
                                        break;
                                    }
                                }
                            }
                        }
                        // 优先用真实碰撞体 bounds；距离/扫掠只作为极端情况兜底。
                        if (boundsHit || (dx <= 1.0f && dy <= 1.0f) || swept <= 0.8f)
                        {
                            if (RegisterShadowDashPlayerHit(ga))
                            {
                                ApplyShadowDashToGeneric(ga);
                            }
                        }
                    }
                    _shadowDashPrevCx = curCx;
                    _shadowDashPrevCy = curCy;
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>锋利之影：对敌人造成当前骨钉伤害（普攻伤害 + 护符加成）。</summary>
        private void ApplyShadowDashDamage(NelEnemy enemy)
        {
            try
            {
                // 羁绊：冲刺大师 + 锋利之影 —— 伤害变为两倍当前无加成骨钉伤害
                // （骨钉伤害体系，同样受坚固贪婪“每 4 物品降低 1.5%”削减）
                int dmg = (CharmEffects.IsEquipped(CharmEffects.DashmasterId) &&
                           CharmEffects.IsEquipped(CharmEffects.ShadowId))
                    ? Mathf.Max(1, Mathf.FloorToInt(SlashDamage * 2 * CharmEffects.GreedDamageMultiplier() + 0.5f))
                    : CharmEffects.ScaleNailDamage(SlashDamage);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>锋利之影：暗影冲刺穿过联机远端玩家，造成与敌人相同的骨钉伤害。</summary>
        private void ApplyShadowDashToGeneric(M2Attackable a)
        {
            if (a == null)
            {
                return;
            }
            try
            {
                if (_shadowDashPacketSent)
                {
                    return; // 每次暗影冲刺全局只发一次伤害包，避免重复音效/判定
                }
                _shadowDashPacketSent = true;
                int dmg = (CharmEffects.IsEquipped(CharmEffects.DashmasterId) &&
                           CharmEffects.IsEquipped(CharmEffects.ShadowId))
                    ? Mathf.Max(1, Mathf.FloorToInt(SlashDamage * 2 * CharmEffects.GreedDamageMultiplier() + 0.5f))
                    : CharmEffects.ScaleNailDamage(SlashDamage);
                SetHitFeedback(ShadowDashFeedback);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.burst_center = ShadowDashPacketMarker;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                if (IsRemoteProxy(a)) RegisterPvpAggro(a);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>点到线段的最短距离（格），用于高速冲刺的扫掠触碰检测。</summary>
        private static float DistancePointToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax;
            float vy = by - ay;
            float len2 = vx * vx + vy * vy;
            if (len2 <= 1e-6f)
            {
                return Mathf.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            }
            float t = ((px - ax) * vx + (py - ay) * vy) / len2;
            t = Mathf.Clamp01(t);
            float qx = ax + vx * t;
            float qy = ay + vy * t;
            return Mathf.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        // ================= 骨钉技艺·旋风劈砍（Cyclone Slash）=================

        /// <summary>松开蓄满的攻击键且按住上/下：开始旋风劈砍（旋转多段伤害，可延长）。</summary>
        private void StartCycloneSlash()
        {
            _nailArtCharging = false;
            _nailArtCharged = false;
            _nailArtChargeParticles.Clear();
            _nailArtGlowFrames = null;
            _nailArtGlowSprite = null;
            DashAudio.StopNailArtChargeLoop();
            _cycloneSlashing = true;
            _cycloneTimer = CycloneBaseTime;
            _cycloneHitCount = 3;
            _cycloneHitIndex = 0;
            _cycloneHitTimer = 0f; // 立即第一击
            _cycloneExtCount = 0;
            _cycloneSlowFallTimer = 0f;
            _cycloneSoulGranted = false;
            _cycloneWeedHits.Clear();
            _currentClip = null; // 切到旋风劈砍动画
            if (_clips.TryGetValue("Cyclone Effect", out ClipData fx) && fx.frames.Length > 0)
            {
                _cycloneFxFrames = fx.frames;
                _cycloneFxFps = fx.fps;
                _cycloneFxTimer = 0f;
                _cycloneFxSprite = fx.frames[0];
            }
            DashAudio.PlayNailArtCyclone(); // 旋转音效（施放至停止）
        }

        /// <summary>每帧推进旋风劈砍：延长判定、多段伤害、旋转动画。</summary>
        private void UpdateCycloneSlash(bool attackPressed)
        {
            if (!_cycloneSlashing)
            {
                return;
            }
            float sdt = Time.deltaTime;
            _cycloneTimer -= sdt;
            // 旋风劈砍破坏判定范围内的魔力草（+4 灵魂，去重）
            if (_mp != null)
            {
                BreakManaWeeds(_cycloneWeedHits, X, Y + CycloneSlashHitboxOffY,
                    CycloneSlashHitboxW * 0.5f, CycloneSlashHitboxH * 0.5f);
                BreakBugWallsInArea(_cycloneWeedHits, X, Y + CycloneSlashHitboxOffY,
                    CycloneSlashHitboxW * 0.5f, CycloneSlashHitboxH * 0.5f); // 一次命中即破坏虫墙
            }
            // 延长：旋转期间按下攻击键（最多 3 次），每次 +0.25s 并 +1 次伤害
            if (attackPressed && _cycloneExtCount < CycloneMaxExtend)
            {
                _cycloneExtCount++;
                _cycloneTimer += CycloneExtendTime;
                _cycloneHitCount++;
                _cycloneSlowFallTimer = CycloneSlowFallTime; // 按下后短暂限速窗口
            }
            // 伤害判定：总时长/总次数 均匀分布
            if (_cycloneHitIndex < _cycloneHitCount)
            {
                float total = CycloneBaseTime + _cycloneExtCount * CycloneExtendTime;
                float interval = total / _cycloneHitCount;
                _cycloneHitTimer -= sdt;
                if (_cycloneHitTimer <= 0f)
                {
                    CheckCycloneHit();
                    _cycloneHitIndex++;
                    _cycloneHitTimer = interval;
                }
            }
            // 剑气帧循环推进
            if (_cycloneFxFrames != null)
            {
                _cycloneFxTimer += sdt;
                int idx = (int)(_cycloneFxTimer * _cycloneFxFps);
                _cycloneFxSprite = _cycloneFxFrames[idx % _cycloneFxFrames.Length];
            }
            if (_cycloneTimer <= 0f)
            {
                _cycloneSlashing = false;
                _currentClip = null;
                _cycloneFxFrames = null;
                _cycloneFxSprite = null;
                _cycloneTimer = 0f;
            }
        }

        /// <summary>旋风劈砍判定箱：骑士中心下 0.5 格、8×2 格，命中敌人造成 30 伤害（每只每转一次）。</summary>
        private void CheckCycloneHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float hitY = Y + CycloneSlashHitboxOffY;
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(hitY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            DestroySpiderTrapsInBox(new Vector2(X, hitY), CycloneSlashHitboxW * 0.5f, CycloneSlashHitboxH * 0.5f);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(CycloneSlashHitboxW, CycloneSlashHitboxH), 0f, mask);
            _cycloneTickTargets.Clear();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 非 NelEnemy 的可攻击目标（的当て靶 / 拳炮 / TD路障 / 联机远端小骑士代理等）：
                    // 旋风劈砍每一转都结算一段，与敌人一致，不做跨转去重。
                    // 注意：这些目标以前走 _cycloneGenericHits 整段去重集合，所以只掉一段血。
                    M2Attackable ga = c.GetComponentInParent<M2Attackable>();
                    if (ga == null || ga is PR || ga is M2MoverPr ||
                        !_cycloneTickTargets.Add(ga))
                    {
                        continue; // 同一转内该目标已结算（多碰撞体不重复吃同一转）
                    }
                    if (IsRemoteProxy(ga))
                    {
                        ApplyProxyNailHit(ga, CharmEffects.ScaleNailDamage(CycloneSlashDamage));
                        continue;
                    }
                    TryHitGenericAttackable(c, CharmEffects.ScaleNailDamage(CycloneSlashDamage), null, 0);
                    continue;
                }
                ApplyCycloneDamage(enemy);
            }
        }

        private void ApplyCycloneDamage(NelEnemy enemy)
        {
            try
            {
                if (enemy == null)
                {
                    return;
                }
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = CharmEffects.ScaleNailDamage(CycloneSlashDamage);
                atk.hpdmg0 = CharmEffects.ScaleNailDamage(CycloneSlashDamage);
                atk.fix_damage = true;
                atk.huttobi_ratio = 0f; // 旋风劈砍不击退目标
                atk.CenterXy(X, Y, 0f); // 以骑士为中心，击退方向朝碰撞箱外侧
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                MarkNailArtHit(enemy); // 骨钉技艺击杀记录
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        // ================= 梦钉（Dream Nail）=================

        /// <summary>计算 nail_slash 每帧内容相对帧中心的水平偏移（用于消除挥梦钉时的左右晃动）。</summary>
        private void BuildNailSlashCenterOffsets()
        {
            try
            {
                for (int i = 0; i < 26; i++)
                {
                    string n = "nail_slash" + i.ToString("D4");
                    if (!_textures.TryGetValue(n, out Texture2D t))
                    {
                        continue;
                    }
                    Color32[] px = t.GetPixels32();
                    int minX = t.width;
                    int maxX = -1;
                    for (int y = 0; y < t.height; y++)
                    {
                        for (int x = 0; x < t.width; x++)
                        {
                            if (px[y * t.width + x].a > 30)
                            {
                                if (x < minX)
                                {
                                    minX = x;
                                }
                                if (x > maxX)
                                {
                                    maxX = x;
                                }
                            }
                        }
                    }
                    if (maxX < 0)
                    {
                        continue;
                    }
                    float contentCenter = (minX + maxX) * 0.5f;
                    _spriteCenterOffX[n] = contentCenter - t.width * 0.5f;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>舞梦者（37）：梦钉命中灵魂 60（原 30）。</summary>
        private int DreamNailSoulNow()
        {
            return CharmEffects.IsEquipped(CharmEffects.DreamId) ? 60 : DreamNailSoul;
        }

        /// <summary>舞梦者（37）：梦钉前摇时长（总时长降到 0.9s，抽出时长不变）。</summary>
        private float DreamNailWindupTimeNow()
        {
            return CharmEffects.IsEquipped(CharmEffects.DreamId)
                ? DreamNailWindupTimeDream
                : DreamNailWindupTime;
        }

        /// <summary>长按空格：开始梦钉前摇（仅地面可用，前摇期间松开即取消）。</summary>
        private void StartDreamNail()
        {
            _dreamNailing = true;
            _dreamPhase = 0;
            _dreamNailTimer = DreamNailWindupTimeNow();
            _dreamNailCandidates.Clear();
            _dreamClosestEnemy = null;
            _dreamClosestDist = float.MaxValue;
            _dreamNailApplied = false;
            _dreamHitBugWall = false;
            _currentClip = null; // 切到梦钉前摇动画
            DashAudio.PlayDreamNailCharge(); // 前摇音效
        }

        /// <summary>每帧推进梦钉：前摇（松开取消）→ 抽出（碰撞箱收集命中，结束对最近怪生效）。</summary>
        private void UpdateDreamNail()
        {
            if (!_dreamNailing)
            {
                return;
            }
            _dreamNailTimer -= Time.deltaTime;
            if (_dreamPhase == 0)
            {
                // 前摇：松开梦钉键 → 终止动画与音效
                if (!DreamNailHeld())
                {
                    CancelDreamNail();
                    return;
                }
                if (_dreamNailTimer <= 0f)
                {
                    // 前摇完成：进入抽出阶段（动画+抽出音效，松开无法终止）
                    _dreamPhase = 1;
                    _dreamNailTimer = DreamNailSwingTime;
                    DashAudio.StopDreamNailChargeFade();
                    DashAudio.PlayDreamNailSlash(); // 抽出音效（无论是否命中）
                    SpawnDreamSwingParticles();     // 前摇 1.2s 未被中断：生成挥击光尘
                }
                return;
            }

            // 抽出阶段：每帧检测碰撞箱，收集命中的怪并记录最近者；
            // 判定箱碰到怪的瞬间立刻生效（不再等抽出动画播完）
            bool hitNew = CheckDreamNailHit();
            if (hitNew && !_dreamNailApplied)
            {
                ApplyDreamNailHit();
                _dreamNailApplied = true;
            }
            if (_dreamNailTimer <= 0f)
            {
                // 抽出结束：若整段抽出都没碰到怪则无事发生；已生效的不会重复结算
                if (!_dreamNailApplied)
                {
                    ApplyDreamNailHit();
                }
                _dreamNailing = false;
                _currentClip = null;
                _dreamNailTimer = 0f;
                _dreamNailCandidates.Clear();
                _dreamClosestEnemy = null;
                _dreamNailApplied = false;
                _dreamHitBugWall = false;
            }
        }

        /// <summary>前摇阶段松开梦钉键：终止动画与音效。</summary>
        private void CancelDreamNail()
        {
            _dreamNailing = false;
            _currentClip = null;
            _dreamNailTimer = 0f;
            _dreamNailCandidates.Clear();
            _dreamClosestEnemy = null;
            _dreamNailApplied = false;
            _dreamHitBugWall = false;
            DashAudio.StopDreamNailChargeFade();
        }

        /// <summary>
        /// 梦钉前摇三段式动画：按当前前摇总时长三等分，
        /// 第一段播 nail_slash0000~0005，第二段播 0006~0010，第三段播 0011~0015。
        /// 不走 PlayClip 的均匀帧率，直接按经过时间精确选帧。
        /// </summary>
        private void PlayDreamNailChargeAnim()
        {
            if (_currentClip != "Dream Nail Charge")
            {
                _currentClip = "Dream Nail Charge";
            }
            // 三段按当前前摇总时长等分（原版每段 0.4s；舞梦者佩戴时同步加快）
            float windup = DreamNailWindupTimeNow();
            float elapsed = windup - Mathf.Max(_dreamNailTimer, 0f);
            float seg = windup / 3f;
            int idx;
            if (elapsed < seg)
            {
                idx = Mathf.Clamp((int)(elapsed / seg * 6f), 0, 5);
            }
            else if (elapsed < seg * 2f)
            {
                idx = 6 + Mathf.Clamp((int)((elapsed - seg) / seg * 5f), 0, 4);
            }
            else
            {
                idx = 11 + Mathf.Clamp((int)((elapsed - seg * 2f) / seg * 5f), 0, 4);
            }
            string spriteName = "nail_slash" + idx.ToString("D4");
            if (_textures.TryGetValue(spriteName, out Texture2D tex))
            {
                _currentSpriteName = spriteName;
                _currentTex = tex;
            }
        }

        /// <summary>抽出结束后，对离骑士最近命中的怪生效（灵魂/MP/梦语/闪光/粒子）。</summary>
        private void ApplyDreamNailHit()
        {
            // 梦钉击中虫墙（且未同时命中怪）：显示固定梦语“好饿……”并恢复 30 灵魂
            if (_dreamHitBugWall && _dreamClosestEnemy == null)
            {
                KnightHudDeco.ShowDreamText("好饿......");
                KnightAddSoul(DreamNailSoulNow()); // 恢复灵魂（舞梦者 60）
                _dreamHitBugWall = false;
                return;
            }
            if (_dreamClosestEnemy == null)
            {
                return;
            }
            NelEnemy enemy = _dreamClosestEnemy;
            TryNusiBurstInterrupt(enemy);
            KnightAddSoul(DreamNailSoulNow()); // 恢复灵魂（舞梦者 60）
            try
            {
                enemy.applyMpDamage(99999, true, null); // 清空怪物 MP
            }
            catch (Exception)
            {
            }
            ShowDreamTextForEnemy(enemy); // 梦语
            _focusFlashAlpha = 1f;        // 屏幕周围亮一下（同回血闪光）
            SpawnDreamParticles(enemy);   // 白色粒子爆发
        }

        /// <summary>梦钉判定箱（抽出阶段）：骑士中心下 0.5 格、面朝方向前 2 格、高 1.5 格；收集命中并记录最近者。
        /// 返回本帧是否新命中了怪（用于命中瞬间立即结算）。</summary>
        private bool CheckDreamNailHit()
        {
            if (_mp == null)
            {
                return false;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return false;
            }
            bool anyNew = false;
            float dir = -_faceDir; // 面朝方向
            float hitX = X + dir * DreamNailHitboxLen * 0.5f;
            float hitY = Y + DreamNailHitboxOffY;
            float mx = _mp.pixel2ux(hitX * _mp.CLEN);
            float my = _mp.pixel2uy(hitY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(DreamNailHitboxLen, DreamNailHitboxH), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    continue;
                }
                if (_dreamNailCandidates.Add(enemy))
                {
                    anyNew = true;
                    float dx = enemy.x - X;
                    float dy = enemy.y - Y;
                    float dist = dx * dx + dy * dy;
                    if (dist < _dreamClosestDist)
                    {
                        _dreamClosestDist = dist;
                        _dreamClosestEnemy = enemy;
                    }
                }
            }
            // 虫墙检测：梦钉击中虫墙 → 梦语“好饿……”+30 灵魂（有怪时以怪为准，本次抽出仅一次）
            if (!_dreamHitBugWall)
            {
                try
                {
                    int bwMask = LayerMask.GetMask("EnemySelf", "Enemy", "AttackHitable", "Default");
                    int bwIgnoreRayLayer = LayerMask.NameToLayer("Ignore Raycast");
                    if (bwIgnoreRayLayer >= 0)
                    {
                        bwMask |= 1 << bwIgnoreRayLayer;
                    }
                    int bwWaterLayer = LayerMask.NameToLayer("Water");
                    if (bwWaterLayer >= 0)
                    {
                        bwMask |= 1 << bwWaterLayer;
                    }
                    int bwChipsLayer = LayerMask.NameToLayer("Chips");
                    if (bwChipsLayer >= 0)
                    {
                        bwMask |= 1 << bwChipsLayer;
                    }
                    int bwChipsUColLayer = LayerMask.NameToLayer("ChipsUCol");
                    if (bwChipsUColLayer >= 0)
                    {
                        bwMask |= 1 << bwChipsUColLayer;
                    }
                    int bwTransparentLayer = LayerMask.NameToLayer("TransparentFX");
                    if (bwTransparentLayer >= 0)
                    {
                        bwMask |= 1 << bwTransparentLayer;
                    }
                    Collider2D[] bwHits = Physics2D.OverlapBoxAll(
                        center, new Vector2(DreamNailHitboxLen, DreamNailHitboxH), 0f, bwMask);
                    for (int i = 0; i < bwHits.Length; i++)
                    {
                        Collider2D c = bwHits[i];
                        if (c != null && IsBugWallCollider(c))
                        {
                            _dreamHitBugWall = true;
                            anyNew = true;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            // 虫巢格（芯片）虫墙：扫描梦钉判定箱覆盖的地图格
            if (!_dreamHitBugWall)
            {
                try
                {
                    int hbCx0 = Mathf.FloorToInt(hitX - DreamNailHitboxLen * 0.5f);
                    int hbCx1 = Mathf.FloorToInt(hitX + DreamNailHitboxLen * 0.5f);
                    int hbCy0 = Mathf.FloorToInt(hitY - DreamNailHitboxH * 0.5f);
                    int hbCy1 = Mathf.FloorToInt(hitY + DreamNailHitboxH * 0.5f);
                    for (int cy = hbCy0; cy <= hbCy1; cy++)
                    {
                        for (int cx = hbCx0; cx <= hbCx1; cx++)
                        {
                            if (cx < 0 || cy < 0 || cx >= _mp.clms || cy >= _mp.rows)
                            {
                                continue;
                            }
                            if (IsWormNestCell(cx, cy))
                            {
                                _dreamHitBugWall = true;
                                anyNew = true;
                                break;
                            }
                        }
                        if (_dreamHitBugWall)
                        {
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return anyNew;
        }

        /// <summary>
        /// 是否为「可对话的魔物 NPC」：魔物商人 / 魔物酒保 / 会钓鱼的魔物 / 农场动物 / 魔像 NPC 等，
        /// 全部派生自游戏内的嵌套类型 <c>MvNelNNEAListener.NelNNpcEventAssign</c>。
        /// 这里沿基类链按类型名判断（反射比对，避免编译期硬依赖嵌套类型），
        /// 命中即视为 NPC → 不给梦语（只有敌对魔物才有梦语）。
        /// </summary>
        private static bool IsNpcMonster(NelEnemy enemy)
        {
            if (enemy == null)
            {
                return false;
            }
            try
            {
                for (Type t = enemy.GetType(); t != null; t = t.BaseType)
                {
                    if (t.Name == "NelNNpcEventAssign")
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 反射异常时按“普通敌对怪”处理，不影响梦语主流程
            }
            return false;
        }

        /// <summary>按怪物最大血量随机展示一句梦语（日文设置则显示日文）。</summary>
        private void ShowDreamTextForEnemy(NelEnemy enemy)
        {
            // 农场魔物（NelNMgmFarmAnimal：NelNMgmFarmChicken 鸡 / NelNMgmFarmCow 牛）：
            // 虽然和可对话 NPC 同属 NelNNpcEventAssign 体系，但按需求要给梦语，
            // 所以这一支必须放在 NPC 判断之前。
            if (enemy is nel.mgm.farm.NelNMgmFarmAnimal)
            {
                bool chicken = enemy.GetType().Name == "NelNMgmFarmChicken";
                string[] farm = chicken
                    ? new[] { "......这个能吃吗......", "......蛋......蛋......", "......魔力......魔力......" }
                    : new[] { "......这个能吃吗......", "......奶......奶......", "......母亲......" };
                string farmText = farm[UnityEngine.Random.Range(0, farm.Length)];
                try
                {
                    if (TX.familyIs("_") || TX.familyIs("ja"))
                    {
                        string[] farmJa = chicken
                            ? new[] { "……これ、食べられるのかな……", "……卵……卵……", "……魔力……魔力……" }
                            : new[] { "……これ、食べられるのかな……", "……乳……乳……", "……母……" };
                        farmText = farmJa[UnityEngine.Random.Range(0, farmJa.Length)];
                    }
                }
                catch (Exception)
                {
                }
                KnightHudDeco.ShowDreamText(farmText);
                return;
            }
            // 可对话的魔物 NPC（魔物商人 / 魔物酒保 / 会钓鱼的魔物 / 魔像 NPC 等）：
            // 它们都派生自游戏内的 MvNelNNEAListener.NelNNpcEventAssign，这类 NPC 不给梦语，
            // 只有敌对魔物才有梦语（见 IsNpcMonster 说明；农场动物已在上面单独处理）。
            if (IsNpcMonster(enemy))
            {
                return;
            }
            int maxhp = 0;
            try
            {
                // maxhp 声明在基类 M2Attackable 上（NelEnemy 里没有），
                // 之前只查 NelEnemy 会拿到 null → 梦语永远按“<100 血”那一档。
                // 这里沿继承链向上找，找不到再退回 0。
                FieldInfo fi = null;
                for (Type tt = enemy.GetType(); tt != null && fi == null; tt = tt.BaseType)
                {
                    fi = AccessTools.Field(tt, "maxhp");
                }
                if (fi != null)
                {
                    maxhp = (int)fi.GetValue(enemy);
                }
            }
            catch (Exception)
            {
            }
            // 护符32 蘑菇孢子：佩戴时所有蘑菇统一使用同一组梦语
            if (CharmEffects.IsEquipped(CharmEffects.MushroomId) && enemy is NelNMush)
            {
                string[] mushLines = new[]
                {
                    "......朋友......",
                    "......菌丝之王......",
                    "......魔力......魔力......"
                };
                KnightHudDeco.ShowDreamText(mushLines[UnityEngine.Random.Range(0, mushLines.Length)]);
                return;
            }
            // 攻防战房间（city_scl_center 校门 / city_scl_ground 操场）：所有怪物统一使用爱丽丝主题梦语
            bool tdRoom = _mp != null &&
                (_mp.key == "city_scl_center" || _mp.key == "city_scl_ground");
            string text;
            if (tdRoom)
            {
                text = new[] { "爱丽丝......", "魔力......魔力.....", "不要害怕......." }[UnityEngine.Random.Range(0, 3)];
            }
            else
            {
                string[] zh;
                if (maxhp < 100)
                {
                    zh = new[] { "入侵者......", "魔力......魔力......", "好饿......" };
                }
                else if (maxhp <= 300)
                {
                    zh = new[] { "强力的身躯......", "入侵者......", "魔力......魔力......" };
                }
                else
                {
                    zh = new[] { "它是什么？.......", "母亲......", "入侵者......" };
                }
                text = zh[UnityEngine.Random.Range(0, zh.Length)];
            }
            // 日文设置：显示日文梦语
            try
            {
                bool isJapanese = TX.familyIs("_") || TX.familyIs("ja");
                if (isJapanese)
                {
                    if (tdRoom)
                    {
                        text = new[] { "アリス……", "魔力……魔力……", "怖がらないで……" }[UnityEngine.Random.Range(0, 3)];
                    }
                    else if (maxhp < 100)
                    {
                        text = new[] { "侵略者……", "魔力……魔力……", "お腹すいた……" }[UnityEngine.Random.Range(0, 3)];
                    }
                    else if (maxhp <= 300)
                    {
                        text = new[] { "強靭な身体……", "侵略者……", "魔力……魔力……" }[UnityEngine.Random.Range(0, 3)];
                    }
                    else
                    {
                        text = new[] { "それは何？……", "母……", "侵略者……" }[UnityEngine.Random.Range(0, 3)];
                    }
                }
            }
            catch (Exception)
            {
            }
            KnightHudDeco.ShowDreamText(text);
        }

        /// <summary>梦钉命中：在被击中的怪物中心爆发 50~60 个白色粒子（复用法术白色圆点贴图）。</summary>
        private void SpawnDreamParticles(NelEnemy enemy)
        {
            float cx = enemy.x;
            float cy = enemy.y;
            int count = UnityEngine.Random.Range((int)DreamHitParticleCountMin, (int)DreamHitParticleCountMax + 1);
            for (int i = 0; i < count; i++)
            {
                float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                float speed = UnityEngine.Random.Range(DreamHitParticleSpeedMin, DreamHitParticleSpeedMax);
                float alpha = UnityEngine.Random.Range(0.8f, 1f);
                _dreamHitParticles.Add(new LightDotParticle
                {
                    X = cx,
                    Y = cy,
                    Vx = Mathf.Cos(ang) * speed,
                    Vy = Mathf.Sin(ang) * speed,
                    TexIndex = 1, // 法术白色圆点贴图
                    Age = 0f,
                    Life = DreamHitParticleDecelStart + DreamHitParticleDecelTime + DreamHitParticleFadeOut,
                    Size = UnityEngine.Random.Range(DreamHitParticleSizeMin, DreamHitParticleSizeMax),
                    Color = new Color32(255, 255, 255, (byte)(alpha * 255f)),
                    Index = 0,
                    Layer = UnityEngine.Random.value < 0.8f ? 0 : 1, // 80% 在骑士身后层
                    Speed = speed
                });
            }
        }

        /// <summary>
        /// 梦钉挥击光尘：前摇 1.2s 未被中断、进入抽出阶段时生成。
        /// 20 个白色粒子，半径 0.1 格，在“骑士中心下方 0.5 格、前方 2.5 格、高 0.5 格”的
        /// 限定范围内缓慢飘荡；范围在生成时固定（世界坐标），不随小骑士转身翻转；
        /// 0.1s 淡入、持续 1s、0.1s 淡出。
        /// </summary>
        private void SpawnDreamSwingParticles()
        {
            float dir = -_faceDir; // 面朝方向
            _dreamSwingCx = X + dir * DreamSwingFxW * 0.5f;
            _dreamSwingCy = Y + DreamNailHitboxOffY;
            float hw = DreamSwingFxW * 0.5f;
            float hh = DreamSwingFxH * 0.5f;
            for (int i = 0; i < 20; i++)
            {
                _dreamSwingParticles.Add(new LightDotParticle
                {
                    X = _dreamSwingCx + UnityEngine.Random.Range(-hw, hw),
                    Y = _dreamSwingCy + UnityEngine.Random.Range(-hh, hh),
                    Vx = (UnityEngine.Random.value - 0.5f) * 0.5f,  // 缓慢飘荡
                    Vy = (UnityEngine.Random.value - 0.5f) * 0.5f,
                    TexIndex = 1,
                    Age = 0f,
                    Life = DreamSwingFxLife,
                    Size = DreamSwingFxSize,
                    Color = new Color32(255, 255, 255, 255),
                    Index = 0,
                    Layer = 1,
                    Speed = 0f
                });
            }
        }

        /// <summary>梦钉挥击光尘推进：缓慢飘荡，并始终钳制在生成时固定的限定范围内（不随骑士转身翻转）。</summary>
        private void UpdateDreamSwingParticles()
        {
            float sdt = Time.deltaTime;
            float hw = DreamSwingFxW * 0.5f;
            float hh = DreamSwingFxH * 0.5f;
            for (int i = _dreamSwingParticles.Count - 1; i >= 0; i--)
            {
                LightDotParticle p = _dreamSwingParticles[i];
                p.Age += sdt;
                p.X += p.Vx * sdt;
                p.Y += p.Vy * sdt;
                // 不超出生成时固定的限定范围
                p.X = Mathf.Clamp(p.X, _dreamSwingCx - hw, _dreamSwingCx + hw);
                p.Y = Mathf.Clamp(p.Y, _dreamSwingCy - hh, _dreamSwingCy + hh);
                if (p.Age >= p.Life)
                {
                    _dreamSwingParticles.RemoveAt(i);
                }
            }
        }

        /// <summary>梦钉白色粒子推进：以 1~6 格/秒远离爆发中心，0.3s 后平滑减速，速度到 0 后淡出 0.25s。</summary>
        private void UpdateDreamParticles()
        {
            float sdt = Time.deltaTime;
            for (int i = _dreamHitParticles.Count - 1; i >= 0; i--)
            {
                LightDotParticle p = _dreamHitParticles[i];
                p.Age += sdt;
                float cur;
                if (p.Age < DreamHitParticleDecelStart)
                {
                    cur = p.Speed;
                }
                else
                {
                    float t = Mathf.Clamp01((p.Age - DreamHitParticleDecelStart) / DreamHitParticleDecelTime);
                    cur = Mathf.Lerp(p.Speed, 0f, t);
                }
                if (cur > 0f && p.Speed > 0f)
                {
                    float scale = cur / p.Speed;
                    p.X += p.Vx * scale * sdt;
                    p.Y += p.Vy * scale * sdt;
                }
                if (p.Age >= p.Life)
                {
                    _dreamHitParticles.RemoveAt(i);
                }
            }
        }

        /// <summary>梦钉白色粒子渲染：按 Layer 分层（PR0 身后 80% / PR1 身前 20%），法术白色圆点贴图。</summary>
        private bool PrepareDreamParticleLayer(MeshDrawer mesh, int layer, Camera Cam, M2RenderTicket Tk,
            int draw_id, out MeshDrawer MdOut)
        {
            MdOut = null;
            if (_mp == null || mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            mesh.clearSimple();
            if (_dotTexes[1] == null || (_dreamHitParticles.Count == 0 && _dreamSwingParticles.Count == 0))
            {
                MdOut = mesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            mesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _dreamHitParticles.Count; i++)
            {
                LightDotParticle p = _dreamHitParticles[i];
                if (p.Layer != layer)
                {
                    continue;
                }
                // 快速淡入 + 速度减到 0 后最后 0.25s 淡出（透明度 80%~100%）
                float alpha = Mathf.Clamp01(p.Age / 0.05f) *
                              Mathf.Clamp01((p.Life - p.Age) / DreamHitParticleFadeOut) *
                              (p.Color.a / 255f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = p.Size * 2f * _mp.CLEN; // 直径
                float dxm = (p.X - X) * _mp.CLEN;
                float dym = -(p.Y - Y) * _mp.CLEN;
                mesh.Col = new Color(1f, 1f, 1f, alpha);
                mesh.Rect(dxm, dym, size, size, false); // Rect(x,y) 为矩形中心
            }
            // 梦钉挥击光尘：白色粒子，0.1s 淡入、1s 持续、0.1s 淡出
            for (int i = 0; i < _dreamSwingParticles.Count; i++)
            {
                LightDotParticle p = _dreamSwingParticles[i];
                if (p.Layer != layer)
                {
                    continue;
                }
                float alpha = Mathf.Clamp01(p.Age / DreamSwingFxFadeIn) *
                              Mathf.Clamp01((p.Life - p.Age) / DreamSwingFxFadeOut);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = p.Size * 2f * _mp.CLEN; // 直径
                float dxm = (p.X - X) * _mp.CLEN;
                float dym = -(p.Y - Y) * _mp.CLEN;
                mesh.Col = new Color(p.Color.r / 255f, p.Color.g / 255f, p.Color.b / 255f, alpha);
                mesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = mesh;
            return true;
        }

        /// <summary>死亡/切回诺艾尔/过图/释放票据时复位梦钉状态。</summary>
        private void ResetDreamNail()
        {
            _dreamNailing = false;
            _dreamNailTimer = 0f;
            _dreamNailCandidates.Clear();
            _dreamClosestEnemy = null;
            _dreamNailApplied = false;
            DashAudio.StopDreamNailChargeFade();
            DashAudio.StopDreamNailSlash();
            _dreamHitParticles.Clear();
            _dreamSwingParticles.Clear();
            KnightHudDeco.HideDreamText();
        }

        /// <summary>死亡/切回诺艾尔/过图/释放票据时复位骨钉技艺状态。</summary>
        private void ResetNailArt()
        {
            DashAudio.StopNailArtChargeLoop(); // 复位：停止蓄满循环音
            _nailArtCharging = false;
            _nailArtCharged = false;
            _nailArtHoldPending = false;
            _nailArtHoldTimer = 0f;
            _nailArtSlashing = false;
            _nailArtQuickTap = false;
            _nailArtChargeTimer = 0f;
            _nailArtSlashTimer = 0f;
            _nailArtChargeParticles.Clear();
            _nailArtWeedHits.Clear();
            _nailArtGlowFrames = null;
            _nailArtGlowSprite = null;
            _nailArtSlashFxFrames = null;
            _nailArtSlashFxSprite = null;
            _dashSlashPending = false;
            _dashSlashing = false;
            _dashSlashTimer = 0f;
            _dashSlashFxFrames = null;
            _dashSlashFxSprite = null;
            _dashSlashWeedHits.Clear();
            _cycloneSlashing = false;
            _cycloneTimer = 0f;
            _cycloneFxFrames = null;
            _cycloneFxSprite = null;
            _cycloneWeedHits.Clear();
        }

        // ================= 黑暗降临（下砸）=================

        private bool CanStartDive()
        {
            return _soul >= CharmEffects.SpellSoulCost() && _diveCooldown <= 0f && !_diving &&
                   !_isSitting && !_sitStandingUp && !_isDead && !_respawnFadeOut &&
                   !_hurt && !_focusing && !_fireballCasting && !_screaming && !_attacking &&
                   !_dashing && !_superCharging && !_superDashing && !_nailArtSlashing &&
                   !_dreamNailing &&
                   _pendingReposition <= 0;
        }

        /// <summary>同时按住 S 与下键：消耗 30 灵魂开始下砸。</summary>
        private void StartDive()
        {
            if (!_soulInfinite)
            {
            _soul = Mathf.Max(0, _soul - CharmEffects.SpellSoulCost());
            }
            DashAudio.PlayDiveLoop(); // 下砸开始：循环播放下落音效直到落地
            _diving = true;
            _divePhase = 0;
            CancelWallJumpForAttack(); // 蹬墙跳期间下砸：终止蹬墙跳，优先下砸
            _diveNoLand = 0f; // 新的下砸：清除上次过图残留的免落地窗口
            _diveTimer = DiveAnticTime;
            _divePathHits.Clear();
            _diveAreaHits.Clear();
            _diveSwords.Clear();
            _diveSpikes.Clear();
            CancelPogoRebound(); // 下劈回弹期间施法：终止回弹
            // 打断普攻/二段跳/爬墙/火球状态
            _attacking = false;
            _attackTimer = 0f;
            _swingHits.Clear();
            DestroyHitbox();
            _fxTex = null;
            _doubleJumping = false;
            _doubleJumpTimer = 0f;
            _onWall = false;
            _fireballCasting = false;
            _fireballs.Clear();
            _currentClip = null;
            // 无敌：从前摇开始覆盖整个下砸（下落→落地硬直→落地后 0.6s）。
            // 用 30s 大计时器兜底，避免超高落差（下落超过 5s）时出现无敌空窗；
            // 落地硬直结束处会把无敌上限钳到 0.6s，保证总时长正好“落地后 0.7s”。
            _invincibleTimer = Mathf.Max(_invincibleTimer, 30f);
        }

        /// <summary>每帧推进下砸：前摇/下落/落地硬直，以及冲击波扩散与冷却。</summary>
        private void UpdateDive()
        {
            // 过图传送/重定位阶段：冻结下砸（不推进、不移动），
            // 状态保留，随后在下一房间继续下落（跨房间延续下砸）
            if (_pendingReposition > 0 || _transitionPause)
            {
                return;
            }
            float sdt = Time.deltaTime;
            float fdt = sdt * 60f; // 与其它物理一致的“帧数”单位
            if (_diveCooldown > 0f)
            {
                _diveCooldown -= sdt;
                if (_diveCooldown < 0f)
                {
                    _diveCooldown = 0f;
                }
            }
            if (_diveInvulnTimer > 0f)
            {
                _diveInvulnTimer -= sdt;
                if (_diveInvulnTimer <= 0f)
                {
                    _diveInvulnTimer = 0f;
                    _invincibleTimer = 0f; // 可行动无敌结束
                }
            }
            if (!_diving)
            {
                return;
            }

            if (_divePhase == 0)
            {
                // 前摇 0.25s：地面固定速度上移，空中悬停
                _diveTimer -= sdt;
                if (Grounded)
                {
                    float dy = DiveAnticUpSpeed * sdt;
                    Y -= dy;
                    if (HitCeil())
                    {
                        Y += dy;
                    }
                }
                else
                {
                    Vy = 0f;
                }
                if (_diveTimer <= 0f)
                {
                    _divePhase = 1;
                    _diveTimer = 0f;
                    Grounded = false;
                    Vy = 0f;
                }
            }
            else if (_divePhase == 1)
            {
                // 下落：前摇结束后直接以 30 格/秒匀速下坠（无加速过程）
                Vy = DiveFallAccelTarget / 60f;
                Y += Vy * fdt;
                // 掉出地图兜底：下砸期间跳过正常物理，这里必须自行处理越界，
                // 否则从房间底部出口洞下砸会一路掉出地图、永远无法落地
                // 转房事件/传送/重定位期间跳过该兜底：下砸跨房时骑士会短暂低于地图，
                // 由重定位负责把它带到新房间入口，下砸延续不应被回弹
                if (_mp != null && Y + SizeY > _mp.rows + 1f &&
                    !EV.isActive(false) && _pendingReposition <= 0 && !_transitionPause &&
                    !(M2DBase.Instance != null && M2DBase.Instance.transferring_game_stopping))
                {
                    _diving = false;
                    _divePhase = 0;
                    _diveTimer = 0f;
                    _diveHardlockTimer = 0f;
                    _invincibleTimer = 0f;      // 下砸被越界中断：清除下砸无敌
                    _diveInvulnTimer = 0f;
                    DashAudio.StopDiveLoop();
                    X = _lastSafeX;
                    Y = _lastSafeY;
                    Vx = 0f;
                    Vy = 0f;
                    Grounded = true;
                    _currentClip = null;
                    return;
                }
                CheckDivePathHit();
                BreakManaWeeds(_diveAreaHits, X, Y + SizeY * 0.5f, 0.8f, 1.2f); // 下落路径经过的魔力草一并破坏
                BreakBugWallsInArea(_diveAreaHits, X, Y + SizeY * 0.5f, 0.8f, 1.2f); // 下落路径经过的虫墙一并破坏
                // 过图强制位移期间（测量中 _diveNoLand<0）：跳过单向地板（lift），实心地板仍正常落地；
                // 位移结束即恢复：没按下键时，单向地板照常能砸上去。
                bool savedSkipLift = _skipLiftNow;
                if (_diveNoLand < 0f)
                {
                    _skipLiftNow = true;
                }
                bool diveLanded = CheckGround(out float gt, Y + SizeY);
                _skipLiftNow = savedSkipLift;
                if (diveLanded)
                {
                    Y = gt - SizeY;
                    Vy = 0f;
                    Grounded = true;
                    OnDiveLand();
                }
            }
            else if (_divePhase == 2)
            {
                // 落地硬直 0.1s（锁输入），之后保持落地动画播完整（输入已恢复）
                if (_diveHardlockTimer > 0f)
                {
                    _diveHardlockTimer -= sdt;
                    if (_diveHardlockTimer <= 0f)
                    {
                        _diveHardlockTimer = 0f;
                        _diveInvulnTimer = DiveInvulnTime; // 硬直结束：开始 0.6s 可行动无敌（含硬直共 0.7s）
                        _invincibleTimer = Mathf.Min(_invincibleTimer, DiveInvulnTime); // 兜底：无敌上限钳到落地后 0.7s
                    }
                }
                _diveTimer -= sdt;
                if (_diveTimer <= 0f)
                {
                    _diving = false;
                    _currentClip = null;
                }
            }
        }

        /// <summary>落地：生成 5 把骨剑 + 两侧 22 个尖刺，进入硬直。</summary>
        private void OnDiveLand()
        {
            _divePhase = 2;
            _diveNoLand = 0f; // 落地：结束免落地窗口
            // 落地阶段持续到 Dive Land 动画播完（输入硬直仅前 0.1s）
            _diveTimer = GetClipDuration("Dive Land");
            _diveHardlockTimer = DiveLandHardlock;
            _diveLandX = X;
            _diveLandY = Y + SizeY * 0.5f;
            SpawnDiveSwords();
            SpawnDiveSpikes();
            // 落地冲击范围（骨剑 ±2 格、尖刺 ±5.5 格、骨剑高 5 格）内的魔力草一并破坏
            BreakManaWeeds(_diveAreaHits, _diveLandX, _diveLandY - 2.5f, 6f, 4f);
            BreakBugWallsInArea(_diveAreaHits, _diveLandX, _diveLandY - 2.5f, 6f, 4f);
            _diveCooldown = DiveCooldown;
            _currentClip = null;
            DashAudio.StopDiveLoop(); // 落地：停止下落循环音
            DashAudio.PlayDiveLand();
            SpawnDiveLandParticles();
            // 战斗详情界面显示期间，下砸落地瞬间直接进入战斗
            TryOpenNearBattleByDiveOrScream();
            // 落地镜头震动
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.Cam != null)
                {
                    m2d.Cam.setQuake(4f, 12, 2f, 0);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>落地时从落点向上爆发大量黑/白圆形粒子（约 105 个：白 30 + 黑 75）。</summary>
        private void SpawnDiveLandParticles()
        {
            float bx = X;
            float by = Y + SizeY * 0.5f + 0.6f; // 爆发点在原基础上向下 0.6 格
            const int total = 105;
            for (int i = 0; i < total; i++)
            {
                bool white = i < 30;
                _diveLandParticles.Add(new LightDotParticle
                {
                    X = bx + (UnityEngine.Random.value - 0.5f) * 0.8f,
                    Y = by + (UnityEngine.Random.value - 0.5f) * 0.6f,
                    Vx = (UnityEngine.Random.value - 0.5f) * 4.8f, // 向两侧散开（翻倍）
                    Vy = -UnityEngine.Random.Range(0.7f, 4.4f),   // 向上爆发（翻倍）
                    TexIndex = 1,
                    Age = 0f,
                    Life = UnityEngine.Random.Range(1f, 3f),
                    Size = white
                        ? UnityEngine.Random.Range(0.16f, 0.24f)
                        : UnityEngine.Random.Range(0.2f, 0.3f),
                    Color = white
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(15, 15, 20, 255),
                    Index = 0,
                    Layer = UnityEngine.Random.value < 0.8f ? 0 : 1 // 80% 在身后层
                });
            }
        }

        /// <summary>
        /// 落地生成 5 把骨剑（时序按全局时间，非各自延迟）：
        /// 中央 0s 起 0.1s 升 5 格；±1 在中央到顶后起 0.1s 升 4 格；±2 在 ±1 到顶后起 0.1s 升 3 格。
        /// 全部到顶后等待 0.4s，中央立即 0.1s 降回；±1 延时 0.05s、±2 延时 0.1s 后各自 0.1s 降回。
        /// </summary>
        private void SpawnDiveSwords()
        {
            _diveSwords.Clear();
            float centerRiseEnd = DiveSwordRiseTime;                        // 0.1s：中央到顶
            float side1RiseEnd = centerRiseEnd + DiveSwordRiseTime;         // 0.2s：±1 到顶
            float allTopTime = side1RiseEnd + DiveSwordRiseTime;            // 0.3s：±2 到顶
            float centerFallStart = allTopTime + DiveSwordHoldTime;         // 0.7s：中央开始降
            float side1FallStart = centerFallStart + DiveSwordFallStep;     // 0.75s
            float side2FallStart = centerFallStart + DiveSwordFallStep * 2f;// 0.8s

            _diveSwords.Add(MakeDiveSword(0f, 5f, 0f, centerFallStart, DiveSwordRenderOffY));
            _diveSwords.Add(MakeDiveSword(-1f, 4f, centerRiseEnd, side1FallStart, DiveSwordSide1RenderOffY));
            _diveSwords.Add(MakeDiveSword(1f, 4f, centerRiseEnd, side1FallStart, DiveSwordSide1RenderOffY));
            _diveSwords.Add(MakeDiveSword(-2f, 3f, side1RiseEnd, side2FallStart, DiveSwordSide2RenderOffY));
            _diveSwords.Add(MakeDiveSword(2f, 3f, side1RiseEnd, side2FallStart, DiveSwordSide2RenderOffY));
        }

        private DiveSword MakeDiveSword(float xOff, float maxH, float riseStart, float fallStart, float renderOffY)
        {
            return new DiveSword
            {
                X = _diveLandX + xOff,
                BaseY = _diveLandY,
                MaxHeight = maxH,
                // 保持贴图高宽比（89×329）：落点剑宽 = DiveSwordWidth，其它按高度比例缩放
                Width = DiveSwordWidth * (maxH / 5f),
                RiseStart = riseStart,
                FallStart = fallStart,
                RiseTime = DiveSwordRiseTime,
                FallTime = DiveSwordFallTime,
                RenderOffY = renderOffY,
                Timer = 0f,
                Height = 0f
            };
        }

        /// <summary>落地生成 22 个尖刺：两侧各 11 个，底宽 0.5 格、间隔 0.5 格，逐个延迟 0.05s（左右对称同时生成）。</summary>
        private void SpawnDiveSpikes()
        {
            _diveSpikes.Clear();
            for (int i = 0; i < DiveSpikePerSide; i++)
            {
                float off = DiveSpikeSpacing + i * DiveSpikeSpacing;
                float delay = i * DiveSpikeStepDelay;
                _diveSpikes.Add(new DiveSpike
                {
                    X = _diveLandX - off,
                    BaseY = _diveLandY,
                    Delay = delay,
                    Timer = 0f,
                    Height = 0f
                });
                _diveSpikes.Add(new DiveSpike
                {
                    X = _diveLandX + off,
                    BaseY = _diveLandY,
                    Delay = delay,
                    Timer = 0f,
                    Height = 0f
                });
            }
        }

        /// <summary>每帧推进骨剑/尖刺的升起与降下，并结算各自判定箱伤害。</summary>
        private void UpdateDiveSwordsAndSpikes(float sdt)
        {
            float spikeFullH = DiveSpikeBaseWidth * DiveSpikeAspect;
            // 尖刺下降开始时刻：最后一个（延迟 0.50s）升到最高点（+0.05s）
            float spikeDescentStart = DiveSpikeStepDelay * (DiveSpikePerSide - 1) + DiveSpikeRiseTime;

            for (int i = _diveSwords.Count - 1; i >= 0; i--)
            {
                DiveSword s = _diveSwords[i];
                s.Timer += sdt;
                if (s.Timer < s.RiseStart)
                {
                    s.Height = 0f;
                    continue;
                }
                if (s.Timer < s.RiseStart + s.RiseTime)
                {
                    s.Height = s.MaxHeight * ((s.Timer - s.RiseStart) / s.RiseTime);
                }
                else if (s.Timer < s.FallStart)
                {
                    s.Height = s.MaxHeight; // 到顶保持
                }
                else if (s.Timer < s.FallStart + s.FallTime)
                {
                    s.Height = s.MaxHeight * (1f - (s.Timer - s.FallStart) / s.FallTime);
                }
                else
                {
                    _diveSwords.RemoveAt(i);
                }
            }
            for (int i = _diveSpikes.Count - 1; i >= 0; i--)
            {
                DiveSpike p = _diveSpikes[i];
                p.Timer += sdt;
                float local = p.Timer - p.Delay;
                if (local < 0f)
                {
                    p.Height = 0f;
                    continue;
                }
                float dLocal = local - (spikeDescentStart + p.Delay);
                if (local < DiveSpikeRiseTime)
                {
                    p.Height = spikeFullH * (local / DiveSpikeRiseTime);
                }
                else if (dLocal < 0f)
                {
                    p.Height = spikeFullH;
                }
                else if (dLocal < DiveSpikeRiseTime)
                {
                    p.Height = spikeFullH * (1f - dLocal / DiveSpikeRiseTime);
                }
                else
                {
                    _diveSpikes.RemoveAt(i);
                }
            }
            CheckDiveSwordDamage();
            CheckDiveSpikeDamage();
        }

        /// <summary>下落路径伤害：骑士下落经过的敌人造成 30 伤害（每只一次）。</summary>
        private void CheckDivePathHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy((Y + SizeY * 0.5f) * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(0.9f, 1.4f), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                NelEnemy enemy = hits[i] != null ? hits[i].GetComponentInParent<NelEnemy>() : null;
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    TryHitGenericAttackable(hits[i], CharmEffects.ScaleSpellDamage(DivePathDamage), _divePathHits, 1);
                    continue;
                }
                if (!_divePathHits.Add(enemy))
                {
                    continue;
                }
                ApplyDiveDamage(enemy, CharmEffects.ScaleSpellDamage(DivePathDamage));
            }
        }

        /// <summary>骨剑伤害：每把剑独立判定箱（当前高度），命中造成 30 伤害。</summary>
        private void CheckDiveSwordDamage()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            for (int i = 0; i < _diveSwords.Count; i++)
            {
                DiveSword s = _diveSwords[i];
                if (s.Height < 0.1f)
                {
                    continue;
                }
                float centerY = s.BaseY - s.Height * 0.5f + DiveSwordHitboxOffY;
                float centerX = s.X + DiveSwordHitboxOffX;
                float mx = _mp.pixel2ux(centerX * _mp.CLEN);
                float my = _mp.pixel2uy(centerY * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 骨剑：破坏范围内的蛛丝球释放源（蜘蛛陷阱）
                DestroySpiderTrapsInBox(new Vector2(centerX, centerY), s.Width * 0.5f, s.Height * 0.5f);
                // 骨剑同时破坏范围内的魔力草（与下落路径/尖刺共享去重，每株草只结算一次灵魂）
                BreakManaWeeds(_diveAreaHits, s.X, centerY, s.Width * 0.5f, s.Height * 0.5f);
                Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(s.Width, s.Height), 0f, mask);
                for (int h = 0; h < hits.Length; h++)
                {
                    NelEnemy enemy = hits[h] != null ? hits[h].GetComponentInParent<NelEnemy>() : null;
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        TryHitGenericAttackable(hits[h], CharmEffects.ScaleSpellDamage(DiveSwordDamage), s.Hits, 1);
                        continue;
                    }
                    if (!s.Hits.Add(enemy))
                    {
                        continue;
                    }
                    ApplyDiveDamage(enemy, CharmEffects.ScaleSpellDamage(DiveSwordDamage));
                }
            }
        }

        /// <summary>尖刺伤害：每个尖刺独立判定箱（当前高度），命中造成 20 伤害。</summary>
        private void CheckDiveSpikeDamage()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            for (int i = 0; i < _diveSpikes.Count; i++)
            {
                DiveSpike p = _diveSpikes[i];
                if (p.Height < 0.05f)
                {
                    continue;
                }
                float centerY = p.BaseY - p.Height * 0.5f + DiveSpikeHitboxOffY;
                float mx = _mp.pixel2ux((p.X + DiveSpikeHitboxOffX) * _mp.CLEN);
                float my = _mp.pixel2uy(centerY * _mp.CLEN);
                Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
                // 尖刺：破坏范围内的蛛丝球释放源（蜘蛛陷阱）
                DestroySpiderTrapsInBox(new Vector2(p.X + DiveSpikeHitboxOffX, centerY),
                    DiveSpikeBaseWidth * 0.5f, p.Height * 0.5f);
                // 尖刺同时破坏范围内的魔力草（与下落路径/骨剑共享去重，每株草只结算一次灵魂）
                BreakManaWeeds(_diveAreaHits, p.X + DiveSpikeHitboxOffX, centerY,
                    DiveSpikeBaseWidth * 0.5f, p.Height * 0.5f);
                Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(DiveSpikeBaseWidth, p.Height), 0f, mask);
                for (int h = 0; h < hits.Length; h++)
                {
                    NelEnemy enemy = hits[h] != null ? hits[h].GetComponentInParent<NelEnemy>() : null;
                    enemy = ResolveDamageTarget(enemy);
                    if (enemy == null)
                    {
                        TryHitGenericAttackable(hits[h], CharmEffects.ScaleSpellDamage(DiveSpikeDamage), p.Hits, 1);
                        continue;
                    }
                    if (!p.Hits.Add(enemy))
                    {
                        continue;
                    }
                    ApplyDiveDamage(enemy, CharmEffects.ScaleSpellDamage(DiveSpikeDamage));
                }
            }
        }

        /// <summary>
        /// 下砸骨剑/尖刺/尖啸的通用伤害结算。
        /// suppress_pop=true 时抑制 AIC 引擎默认的“受击上弹”：Atk.burst_vy 为 0 时引擎会补
        /// -0.1~-0.26 的上弹速度（见 NelEnemy.applyDamage），多段驻留判定（尖啸 5 段）会因此
        /// 把目标弹出判定范围、只吃到 1 段；这里填一个极小的非 0 值跳过该默认分支。
        /// </summary>
        private void ApplyDiveDamage(NelEnemy enemy, int dmg, bool suppress_pop = false)
        {
            try
            {
                if (enemy == null)
                {
                    return;
                }
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                // 深渊尖啸（调用处传 suppress_pop=true）→ 着火；下砸骨剑/尖刺 → 直线击飞
                SetHitFeedback(suppress_pop ? (byte)2 : (byte)1);
                if (suppress_pop)
                {
                    atk.burst_vy = 0.0001f; // 非 0 → 跳过引擎默认上弹；数值极小，位移可忽略
                }
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 冲击波帧动画推进（Fireball Projectile）：
        /// 播完 single_fireball0000~0005 一轮后停在最后一帧（不再循环）。
        /// </summary>
        private void UpdateFireballAnim(FireballProj proj, float dt)
        {
            if (!_clips.TryGetValue("Fireball Projectile", out ClipData clip) || clip.frames.Length == 0)
            {
                return;
            }
            proj.AnimTimer += dt;
            int raw = (int)(proj.AnimTimer * clip.fps);
            if (raw >= clip.frames.Length)
            {
                raw = clip.frames.Length - 1; // 播完一轮保持最后一帧
            }
            if (raw < 0)
            {
                raw = 0;
            }
            proj.Sprite = clip.frames[raw];
        }

        /// <summary>冲击波沿途检测敌人：命中过的敌人只结算一次（穿过多目标造成伤害）。</summary>
        private void CheckFireballHit(FireballProj proj)
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            // 伤害检测中心 = 冲击波逻辑位置 + 判定框偏移
            float hitX = proj.X + FireballHitboxOffX;
            float hitY = proj.Y + FireballHitboxOffY;
            float mx = _mp.pixel2ux(hitX * _mp.CLEN);
            float my = _mp.pixel2uy(hitY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            DestroySpiderTrapsInBox(new Vector2(hitX, hitY), FireballHitboxW * 0.5f, FireballHitboxH * 0.5f);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center,
                new Vector2(FireballHitboxW, FireballHitboxH), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy == null)
                {
                    // 普通可破坏墙（M2BreakableWallMover）：暗影之魂也能按 AIC 原版伤害管线破坏
                    M2BreakableWallMover breakableWall = c.GetComponentInParent<M2BreakableWallMover>();
                    if (breakableWall != null)
                    {
                        if (proj.Hits.Add(breakableWall))
                        {
                            DamageBreakableWall(breakableWall, false);
                        }
                        continue;
                    }
                    TryHitGenericAttackable(c, CharmEffects.ScaleSpellDamage(FireballDamage), proj.Hits, 2);
                    continue;
                }
                if (!proj.Hits.Add(enemy))
                {
                    continue;
                }
                ApplyFireballDamage(enemy);
            }
        }

        /// <summary>暗影之魂伤害：60 伤害（不再削减敌人 MP）。</summary>
        private void ApplyFireballDamage(NelEnemy enemy)
        {
            try
            {
                if (enemy == null)
                {
                    return;
                }
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                int fireDmg = CharmEffects.ScaleSpellDamage(FireballDamage);
                atk.hpdmg_current = fireDmg;
                atk.hpdmg0 = fireDmg;
                atk.fix_damage = true;
                SetHitFeedback(2); // 复仇之魂（暗影之魂火球）→ 着火
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
                DashAudio.PlayFireballHit();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>冲刺拖尾特效帧推进（SD Trail，含出现帧后循环）。</summary>
        private void UpdateSuperTrailFx()
        {
            if (!_clips.TryGetValue("SD Trail", out ClipData clip))
            {
                return;
            }
            _sdTrailTimer += Time.deltaTime;
            int raw = (int)(_sdTrailTimer * clip.fps);
            _sdTrailSprite = clip.frames[EffectiveIndex(clip, raw)];
        }

        private int GetEnemyOverlapMask()
        {
            int mask = LayerMask.GetMask("EnemySelf", "Enemy", "AttackHitable");
            // 标靶（M2MatoateTarget）/拳炮/TD路障等可攻击实体的碰撞体在 Ignore Raycast 层
            int ignoreRayLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRayLayer >= 0)
            {
                mask |= 1 << ignoreRayLayer;
            }
            // 森之领主等大型 Boss 锁定墙体碰撞时，本体碰撞体会被游戏放到 Water 层
            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer >= 0)
            {
                mask |= 1 << waterLayer;
            }
            // 森之领主等 Boss 锁定墙+移动体碰撞时，本体会被放到 TransparentFX 层
            int transparentLayer = LayerMask.NameToLayer("TransparentFX");
            if (transparentLayer >= 0)
            {
                mask |= 1 << transparentLayer;
            }
            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer >= 0)
            {
                mask |= 1 << defaultLayer;
            }
            return mask;
        }

        /// <summary>冲刺飞行中检测前方敌人：20 伤害 + 击退；敌人未死则小骑士受伤并停下。</summary>
        private void CheckSuperDashEnemyHit()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            float hx = _superDashDir * 0.65f;
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx + hx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(1.4f, 1.6f), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                // 友好动物（鸡/牛）：超级冲刺直接无视
                if (enemy == null || enemy is nel.mgm.farm.NelNMgmFarmAnimal || !_superDashHit.Add(enemy))
                {
                    continue;
                }
                ApplySuperDashDamage(enemy, true);
                if (_hurt)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 对敌人造成 20 点伤害 + 击退。hurtKnightOnSurvive=true 时，敌人未死则小骑士受伤并停下；
        /// 起始爆发（false）只伤敌不伤己。
        /// </summary>
        private void ApplySuperDashDamage(NelEnemy enemy, bool hurtKnightOnSurvive)
        {
            try
            {
                TryNusiBurstInterrupt(enemy);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = SuperDashDamage;
                atk.hpdmg0 = SuperDashDamage;
                atk.fix_damage = true;
                atk.huttobi_ratio = 25f; // 击退
                atk.CenterXy(enemy.x, enemy.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                enemy.applyDamage(atk, false);
                DashAudio.PlayEnemyHit();
                // 泡泡（NelNSyabon）：超级冲刺撞到也不回伤小骑士
                if (hurtKnightOnSurvive && !(enemy is NelNSyabon) &&
                    enemy.is_alive && enemy.hp_ratio > 0f)
                {
                    // 敌人没死：小骑士受伤并停下（方向朝怪物）
                    var kAtk = new NelAttackInfo();
                    kAtk.CenterXy(enemy.x, enemy.y, 0f);
                    KnightTakeDamage(kAtk);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>发射瞬间的起始爆发：骑士周围较大范围造成 20 点伤害（不伤己）。</summary>
        private void SuperDashStartBurst()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetEnemyOverlapMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(3.6f, 2.4f), 0f, mask);
            var done = new HashSet<object>();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy != null && !(enemy is nel.mgm.farm.NelNMgmFarmAnimal) && done.Add(enemy))
                {
                    ApplySuperDashDamage(enemy, false);
                }
            }
        }

        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, ClipData> _clips = new Dictionary<string, ClipData>();
        private readonly Dictionary<string, float> _feetFraction = new Dictionary<string, float>();

        private Material _mat;
        private MeshDrawer _mesh;
        private M2RenderTicket _ticket;
        private MeshDrawer _rcMesh;
        private M2RenderTicket _rcTicket;
        private Material _rcMat;
        private Texture2D _rcTex;
        private string _rcSpriteName;
        private string _rcClipName;
        private int _rcFrameIndex;
        private float _rcFrameTimer;
        private const float ShadowDashInvulnTime = 0.4f; // 暗影冲刺无敌时长（秒），从按下冲刺键起生效
        // 护符33 锋利之影：暗影冲刺速度 +40%、距离 +25%（时间 = 1.25/1.4），穿过敌人造成当前骨钉伤害
        private const float SharpShadowSpeedMult = 1.4f;
        private const float SharpShadowTimeMult = 1.25f / 1.4f;
        private bool _isShadowDash;
        private readonly HashSet<NelEnemy> _shadowDashHits = new HashSet<NelEnemy>();
        private readonly HashSet<M2Attackable> _shadowDashPlayerHits = new HashSet<M2Attackable>();
        private readonly HashSet<string> _shadowDashPlayerKeys = new HashSet<string>();
        private bool _shadowDashHitSoundPlayed;
        private bool _shadowDashPacketSent;
        private float _shadowDashPrevCx;
        private float _shadowDashPrevCy;
        private bool _shadowDashPrevValid;
        private float _shadowRechargeTimer;
        private readonly List<TrailGhost> _ghosts = new List<TrailGhost>();
        private float _dashStartX;
        private float _dashStartY;
        private float _ghostFrameTimer;
        // 拖尾整体淡出：最后帧生成后开始，按生成顺序从第1帧起依次消失，0.3s 内全部消失
        private bool _ghostFading;
        private float _ghostFadeTimer;
        private int _ghostFadeTotal;
        private readonly List<GhostNode> _ghostNodes = new List<GhostNode>();
        private Map2d _mp;
        private bool _assetsLoaded;

        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public bool Grounded;

        public bool IsRepositioning => _pendingReposition > 0 || _transitionPause;
        /// <summary>过图跟随阶段标志：告知 KnightInCradleBehaviour 此阶段必须保持诺艾尔物理运行。</summary>
        public bool PendingRepositionActive => _pendingRepositionActive;
        /// <summary>
        /// 清除诺艾尔残留的模拟按键（转房事件的 PR_KEY_SIMULATE 可能卡住，
        /// 导致诺艾尔自动行走、事件 WAIT_MOVE 永不结束、出口判定被占用）。
        /// </summary>
        public static void ClearNoelSimKeys()
        {
            try
            {
                PRNoel pr = KnightInCradleBehaviour.GetPrPublic();
                if (pr == null || SimKeyField == null)
                {
                    return;
                }
                object v = SimKeyField.GetValue(pr);
                uint sk = 0;
                if (v is uint u)
                {
                    sk = u;
                }
                else if (v is int i)
                {
                    sk = (uint)i;
                }
                if (sk != 0u)
                {
                    SimKeyField.SetValue(pr, 0u);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>小骑士是否接近地图边缘（margin 格内，用于过图触发时恢复诺艾尔物理）。</summary>
        public bool NearMapEdge(float margin)
        {
            if (_mp == null)
            {
                return false;
            }
            return X < margin || X > _mp.width - margin ||
                   Y + SizeY < margin || Y + SizeY > _mp.rows - margin;
        }

        /// <summary>调试：F8 打印当前房间信息（地图 key / 子地图 / 尺寸 / 骑士位置 / 脚底是否液体）。</summary>
        private void DebugDumpRoomInfo()
        {
            try
            {
                if (_mp == null)
                {
                    KnightInCradlePlugin.PluginLog.LogInfo("[房间] 当前无地图");
                    return;
                }
                int feetCx = Mathf.FloorToInt(X);
                int feetCy = Mathf.FloorToInt(Y + SizeY);
                int cfg = (feetCx >= 0 && feetCy >= 0 && feetCx < _mp.width && feetCy < _mp.rows)
                    ? _mp.getConfig(feetCx, feetCy)
                    : -1;
                string sub = _mp.SubMapData != null ? _mp.SubMapData.key : "无";
                KnightInCradlePlugin.PluginLog.LogInfo(
                    "[房间] key=" + _mp.key + " submap=" + sub +
                    " clms=" + _mp.clms + " rows=" + _mp.rows +
                    " 骑士=(" + X.ToString("F2") + "," + Y.ToString("F2") + ")" +
                    " 游泳状态=" + _swimState +
                    " 脚底格液体=" + (cfg >= 0 && CCON.isWater(cfg)) +
                    " 脚底cfg=" + cfg);
            }
            catch (Exception e)
            {
                KnightInCradlePlugin.PluginLog.LogWarning("[房间] 调试输出异常: " + e.Message);
            }
        }

        private float _faceDir = 1f;
        private float _lastMoveDir = 1f;

        private Texture2D _currentTex;
        private string _currentSpriteName;
        private readonly Dictionary<string, float> _spriteCenterOffX = new Dictionary<string, float>(); // 帧内容相对帧中心的水平偏移（像素）
        private string _currentClip;
        private int _frameIndex;
        private float _frameTimer;
        private long _prepareCount;
        private bool _active;
        private string _groundSrc = "";
        private bool _groundIsLift;
        private NelChipJumperBoard _groundIsJumper; // 当前地面是否为弹簧地板（JumperBoard）
        private float _jumperBounceLock;            // 弹起后的短暂锁定（防止原地连续弹跳）
        private static bool _jumperLogged;          // 诊断：已打印过弹簧地板识别结果
        private int _groundDiagFrame;               // 地面诊断节流帧
        private readonly HashSet<object> _trapSoulGranted = new HashSet<object>(); // 陷阱破坏给魂去重
        private int _diveHitLogFrame;               // 下砸受击诊断节流帧
        private const float JumperBoardBounceVy = -0.35f; // 弹簧地板弹起初速（与原版 pump_velocity 默认一致）
        private int _dropThroughTimer;
        private bool _skipLiftNow;
        private bool _dropLogged;
        private bool _jumpHeld;
        private bool _jumpCutApplied;
        private bool _canDoubleJump = true;
        private bool _doubleJumping;
        private float _doubleJumpTimer;
        private float _doubleJumpDipTimer;
        private bool _onWall;
        private float _wallDir;
        private bool _wallJumping;
        private float _wallJumpTimer;
        // 墙跳/沿墙小跳的水平推力：线性衰减，保证弹离墙面的位移持续生效（不会只弹一帧）
        private float _wallKickVx;
        private float _wallKickTimer;
        private float _wallKickDuration;
        // 行为 B（松开方向键墙跳）：普通跳跃后以走路速度向远离墙方向持续漂移
        private bool _wallJumpDrift;
        // 二段跳特效（光翼/白羽各自独立网格票据，避免贴图互相覆盖）
        private MeshDrawer _djWingMesh;
        private M2RenderTicket _djWingTicket;
        private Material _djWingMat;
        private Texture2D _whiteTex;
        private readonly MeshDrawer[] _djDotMesh = new MeshDrawer[DotShapeCount];
        private readonly M2RenderTicket[] _djDotTicket = new M2RenderTicket[DotShapeCount];
        private readonly Material[] _djDotMat = new Material[DotShapeCount];
        private readonly Texture2D[] _dotTexes = new Texture2D[DotShapeCount];
        private float _wingTimer;
        private readonly List<LightDotParticle> _dots = new List<LightDotParticle>();
        private float _dotSpawnTimer;
        // 暗影冲刺黑色粒子：复用 LightDotParticle 框架，独立列表与渲染网格
        private readonly List<LightDotParticle> _shadowParticles = new List<LightDotParticle>();
        private MeshDrawer _shadowDotMesh;
        private M2RenderTicket _shadowDotTicket;
        private Material _shadowDotMat;
        private float _shadowDotSpawnTimer;
        // 冲刺结束后黑色粒子整体按序淡出
        private bool _shadowDotFading;
        private float _shadowDotFadeTimer;
        private int _shadowDotFadeTotal;
        private bool _dashing;
        private float _dashTimer;
        private float _dashEndTime;
        private float _dashDir = 1f;
        private float _dashCooldown;
        private bool _canDash = true; // 空中冲刺次数限制：落地重置
        // 冲刺结束过渡：按玩家输入决定最终状态（未按方向键立即停止 / 按住方向键保留冲刺速度衔接行走）
        private bool _dashJustEnded;
        private float _dashExitVx;
        private bool _prevGrounded;
        private bool _wasMoving;
        private string _transitionClip;
        private float _transitionTimer;
        private bool _transitionPause;
        private int _pendingReposition;
        // 攻击
        private bool _attacking;
        private float _attackTimer;
        // 本刀总时长（秒）：连击时用来算“后摇取消点”（_attackTimer 剩多少时可以接下一刀）
        private float _attackTotalTime;
        // 上一刀的起手时刻（Time.time）：仅用于 AttackHitboxDebug 下打印“两刀间隔”诊断
        private float _lastSwingStartTime = -1f;
        // 多目标剑气：每次挥砍逐目标去重（同一目标一刀只处理一次）
        private readonly HashSet<object> _swingHits = new HashSet<object>();
        private MagicItem _knightAttackMagic; // 通用可攻击目标（标靶/拳炮等）用的“玩家普攻” MagicItem 缓存
        private static readonly object TrapSwingKey = new object();
        private bool _isRightSwing;
        private string _attackClipName = "Attack";
        private bool _downSlash;
        private bool _upSlash;
        // 下劈（Pogo）回弹初速：原 -0.20 弹起约 2.8 格；高度减半 → 初速 ÷ √2 ≈ -0.1415
        private const float PogoBounceVy = -0.1415f;
        // 场景物（尖刺/荆棘/虫墙等）下劈回弹：弹起高度 = 怪物回弹的 1.5 倍 → 初速 × √1.5 ≈ -0.1733
        private const float PogoBounceVyScene = -0.1733f;
        private float _pogoGravityLock;
        // 普攻命中“已处理但不应反馈”的目标（如保卫战防御工事/友军）时置 true，
        // 挥砍分支据此跳过横劈后坐力 / 下劈弹起。
        private bool _attackHitFeedbackBlocked;
        private float _recoilVx;
        private float _recoilTimer;
        private float _hitboxActiveTimer;
        // 挑衅（V 键）：播放 knight_challenge 开场→循环→收尾动画，期间锁定移动/攻击；
        // 动画按帧推进，_tauntTimer 倒计时结束后恢复站立。
        private bool _taunting;
        private float _tauntTimer;
        // 挑衅开战是否已触发（等 0010 帧播完后只触发一次）
        private bool _tauntBattleOpened;
        // 坐长椅：坐下过渡(HK 原版 Sit)、坐姿循环(Sit Idle)、起身(HK 原版 Get Off)。
        // 需求中的 “Bench” = HK “Sit”，“BenchEnd” = HK “Get Off”；
        // 剪辑缺失时 PlayClip 会自动回退到 Idle（原版小骑士没有名为 Bench/BenchEnd 的剪辑）。
        private bool _isSitting = false;
        private float _sitTimer = 0f;
        private NelChipBench _sitBench;
        // 坐姿锁定位置（椅面高度）与起身后的地面位置；游戏内 y 轴向下为正
        private float _sitX;
        private float _sitY;
        private float _sitGroundY;
        // 坐下的平滑滑动过渡：从起点滑到长椅中心（同时播放 Sit 动画）
        private bool _sitSliding = false;
        private float _sitSlideTimer = 0f;
        private float _sitSlideDuration = 0.4f;
        private float _sitFromX;
        private float _sitFromY;
        private float _sitToX;
        private float _sitToY;
        // 坐上/离开椅面的过渡时长（秒）
        private const float SitTransitionTime = 0.2f;
        // 起身过渡期间继续锁输入，播完 Get Off 再恢复普通控制
        private bool _sitStandingUp = false;
        private float _sitStandTimer = 0f;
        // 受伤硬直（数值按空洞骑士原版 15/7.5 换算到 AIC：1 格 ≈ 64px，约 0.25 / 0.12 格每帧）
        private const float HurtFreezeTime = 0.3f;      // 画面停滞
        private const float HurtFlyTime = 0.2f;         // 击飞时长
        private const float HurtInvincibleTime = 1.3f;  // 受伤后无敌
        private const float HurtKnockVx = 0.15f;        // 水平击退速度（原版15换算后略收短，避免飞太远）
        private const float HurtKnockVy = -0.12f;       // 向上击退速度（y 负=上）
        private bool _hurt;
        private float _hurtFreezeTimer;
        private float _hurtFlyTimer;
        private float _invincibleTimer;
        private float _hurtDir = 1f;
        private int _hurtDirLogFrame = -999;
        private bool _hurtFrozen;
        private float _hurtPrevTsBase = 1f;
        // 受击屏幕黑边渐隐：受击瞬间四周黑一下（与低血量黑边同一径向纹理），随后短暂渐出
        private const float HitVignetteTime = 0.4f; // 渐出总时长（秒）
        private float _hitVignetteAlpha;
        // 凝聚回血（Focus）：30 灵魂/格，三阶段按帧驱动：
        // 发动 focus_v020000~0002（0.27s，不耗魂）→ 回血 focus_v020003~0006（0.82s，耗魂）
        // → 结束 focus_v020007~0011（0.23s，硬直）
        private const int FocusSoulCost = 30;                // 每格血量消耗灵魂
        private const int FocusSoulRefundMax = 10;           // 未完成凝聚时，已吸取 ≤10 返还
        private const float FocusStartTime = 0.27f;          // 发动阶段时长（3帧）
        private const float FocusHealTime = 0.82f;           // 回血阶段时长（4帧）
        private const float FocusHealDupTime = 0.8f;         // 后续循环回血段时长（0003~0006×2）
        private const float FocusFastMod = -0.3f;            // 快速聚集：回血阶段 -0.3s
        private const float FocusDeepMod = 0.55f;            // 深度聚集：回血阶段 +0.55s
        private const float FocusBurstTime = 0.25f;          // 爆发段时长（0007~0010，4帧）
        // 护符34 乌恩之形：凝聚总时长 1.22 秒（发动 0.2 + 回血 0.82 + 爆发 0.2）
        private const float FocusStartTimeUnn = 0.20f;
        private const float FocusHealTimeUnn = 0.82f;
        private const float FocusHealDupTimeUnn = 0.82f;
        private const float FocusBurstTimeUnn = 0.20f;
        private const float UnnCollideShrink = 0.8f;        // 乌恩形态碰撞箱顶部下移量（底部不动）
        private float _unnCollideShrink;                     // 当前乌恩形态碰撞箱下移量（0 或 0.8）
        private bool _focusing;          // 凝聚流程进行中（含俯身/消耗/硬直）
        private int _focusPhase;         // 0=发动 1=回血 2=结束（硬直）
        private float _focusTimer;       // 当前阶段剩余时间
        private float _focusSoulDrained; // 消耗阶段已吸取的灵魂（未完成时按此扣减/返还）
        private bool _focusFirstCycle;   // 是否第一次回血（首次：发动+回血+爆发；后续：回血段+爆发段）
        private bool _focusBurstFired;   // 是否已进入爆发段（0007，此时回血+音效）
        // 回血结束动画（无硬直）：_focusing 已解除，仅播放结束帧，播完回普通状态
        private bool _focusEnding;
        private string _focusEndClip;
        private string[] _focusEndFrames;
        private float _focusEndFps;
        private int _focusEndIndex;
        private float _focusEndTimer;
        // 凝聚持续音循环播放状态：凝聚开始时启动，回血完成/凝聚结束时停止
        private bool _focusChargePlaying;
        // 凝聚特效：身体周围自下往上的白色条状线（复用 LightDotParticle，独立列表与网格）
        private readonly List<LightDotParticle> _focusParticles = new List<LightDotParticle>();
        private MeshDrawer _focusFxBackMesh;   // 骑士身后层（PR0）
        private M2RenderTicket _focusFxBackTicket;
        private Material _focusFxBackMat;
        private MeshDrawer _focusFxFrontMesh;  // 骑士身前层（PR1）
        private M2RenderTicket _focusFxFrontTicket;
        private Material _focusFxFrontMat;
        private float _focusFxSpawnTimer;
        private const float FocusFxSpawnInterval = 0.05f; // 每 0.05s 生成一批
        // 回血完成时屏幕四周短暂亮一下（白色径向闪光，渐出）
        private const float FocusFlashTime = 0.35f;
        private float _focusFlashAlpha;
        private float _ggGoldFlashAlpha; // 寻神者护符出现时的金色屏幕闪光（UI 打开时也衰减）
        private const float LifebloodFlashTime = 0.35f;
        private float _lifebloodFlashAlpha;
        // ---- 虚空解放（梦之门技能）状态 ----
        private const float VoidChargeFrames = 18f; // 前摇 18 帧（0000~0008 + 0003~0011）@6fps
        private const float VoidChargeFps = 6f;
        private const float VoidChargeTime = VoidChargeFrames / VoidChargeFps; // 3.0s
        private const float VoidStrikeTime = 3.0f;  // 出伤 3s
        private const float VoidScreamCycles = 4f;  // 出伤段尖叫循环遍数（随段时长自动加速填满）
        private const float VoidRecoveryTime = 0.7f; // 后摇总时长 0.7s
        private const float VoidFlashTime = 0.1f;   // 黑/白屏 0.1s
        private const float VoidZoomIn = 1.6f;      // 镜头拉近倍率（原 1.3 的两倍拉近距离）
        // 黑屏结束开始收回，后摇结束彻底还原：跨整个出伤+后摇
        private const float VoidZoomRestoreTime = (VoidStrikeTime - VoidFlashTime) + VoidRecoveryTime;
        private const float VoidTentacleFps = 12f;  // 虚空触手动画帧率
        private int _voidPhase;                     // 0=无 2=前摇 3=出伤 4=后摇
        private float _voidTimer;                   // 前摇/出伤计时
        private float _voidExitTimer;               // 后摇动画计时
        private float _voidZoomRestoreTimer;        // 镜头收回剩余时间（>0 时每帧推进）
        private TentacleFrameData[] _voidTentacleData; // Abyss_tendrils 帧数据（按顺序）
        private int[] _voidTentacleOffsets;            // 每根触手的随机起始帧偏移（每次出现重新随机）
        private float _voidTentacleTimer;
        private bool _voidTentacleActive;
        // ---- 虚空解放伤害 ----
        private int _voidSegments;       // 本次攻击段数（入战按灵魂决定）
        private int _voidSegFired;       // 已触发段数
        private float _voidSegTimer;     // 段间隔计时
        private float _voidSegInterval;  // 段间隔（= 出伤时长 / 段数）
        private bool _voidChargeHurtDone; // 前摇期间是否已受过一次伤害
        private string[] _voidSlashFrames;  // Radiance_GG_slashes0000~0010
        private float _voidSlashTimer;
        private readonly List<VoidSlashTarget> _voidSlashTargets = new List<VoidSlashTarget>();
        private MeshDrawer _voidSlashMesh;
        private M2RenderTicket _voidSlashTicket;
        private Material _voidSlashMat;
        private const float VoidSlashFps = 12f;   // 目标划痕动画帧率
        private const float VoidSlashSize = 2f;   // 划痕渲染直径（格）
        private static readonly System.Reflection.FieldInfo EnemyMaxHpField =
            AccessTools.Field(typeof(M2Attackable), "maxhp");
        /// <summary>被骨钉技艺命中的敌人：死亡时 +10 灵魂（跨帧记录，直到其死亡）。</summary>
        private readonly HashSet<NelEnemy> _nailArtHitEnemies = new HashSet<NelEnemy>();

        /// <summary>记录一次骨钉技艺命中（用于击杀判定）。</summary>
        public void MarkNailArtHit(NelEnemy e)
        {
            if (e != null)
            {
                _nailArtHitEnemies.Add(e);
            }
        }

        /// <summary>敌人死亡时消费命中标记：是骨钉技艺击杀则返回 true。</summary>
        public bool ConsumeNailArtKill(NelEnemy e)
        {
            return e != null && _nailArtHitEnemies.Remove(e);
        }
        private float _voidFlashAlpha;              // 当前闪屏透明度
        private bool _voidFlashBlack;
        private float _voidBlackFlashTimer;         // 出伤黑屏剩余
        private float _voidWhiteFlashTimer;         // 后摇白屏剩余
        private float _voidZoomBase = 1f;           // 技能开始时相机缩放（结束后恢复）
        // ================= 单位约定（统一标准）=================
        // 1. 世界坐标 X/Y：单位为“格”（tile）。1 格 = 1 个地图格子。
        //    X 向右为正、Y 向下为正。所有逻辑/物理数值（速度、距离、
        //    碰撞箱尺寸、出生偏移、滞空时长等）一律以格/秒 等逻辑单位书写。
        // 2. 网格绘制坐标（MeshDrawer 的 Rect/Box/Line 参数）：单位为“网格像素”。
        //    换算关系：1 格 = _mp.CLEN 网格像素（运行时实测 28）。
        //    只在绘制边界换算：px = 格 × _mp.CLEN；格 = px / _mp.CLEN。
        // 3. 屏幕像素：网格像素 × 相机缩放（base_scale），不固定，不作为代码单位。
        // 4. 因此“格 → 屏幕像素”= × CLEN × base_scale；玩家实测 1 格 ≈ 64 屏幕像素。
        // =====================================================
        // ---- 暗影之魂（法术·火球）----
        private const int FireballSoulCost = 30;         // 点按凝聚键立即消耗灵魂
        private const float FireballSpeed = 25f;         // 冲击波速度（格/秒）
        private const int FireballDamage = 70;           // 对沿途所有敌人造成伤害（不再削减敌人 MP）
        private const float FireballHoverTime = 0.3f;    // 使用后滞空时长（秒）
        private const float FireballCooldown = 0.3f;     // 施法冷却（秒），防连发
        private const float FireballRecoilDistPx = 10f;  // 向后反冲距离（像素）
        private const float FireballRecoilTime = 0.15f;  // 反冲时长（秒）
        private const float FireballHitboxW = 4.8f;      // 冲击波碰撞箱宽（格）
        private const float FireballHitboxH = 1.8f;      // 冲击波碰撞箱高（格）
        private const float FireballHitboxOffX = -1.4f;  // 判定框相对冲击波逻辑位置：X 偏移（格）
        private const float FireballHitboxOffY = 0.4f;   // 判定框相对冲击波逻辑位置：Y 偏移（Y 向下为正）
        private const float FireballRenderOffX = 1.5f;   // 渲染相对碰撞箱中心：X 右移（格）
        private const float FireballRenderOffY = -0.8f;  // 渲染相对碰撞箱中心：Y 上移（Y 向下为正，故为负）
        private const float FireballRenderPadX = 0.5f;   // 渲染宽额外增加（格）
        private const float FireballRenderPadY = 0.5f;   // 渲染高额外增加（格）
        private const bool FireballHitboxDebug = false;   // 暗影之魂冲击波绿框（调试完已关）
        private const bool BossHitboxDebug = false;       // Boss 本体碰撞箱绿框（诊断用）
        private bool _fireballCasting;    // 施法进行中（含滞空与后坐）
        private float _fireballCooldown;  // 剩余冷却时间
        private float _fireballCastTimer; // 施法动画剩余时间
        private float _fireballHoverTimer; // 剩余滞空时间
        private float _fireballRecoilTimer;
        private float _fireballRecoilVx;
        // 冲击波可同时存在多个（连续施法时旧冲击波继续飞行，不被新施法覆盖）
        private class FireballProj
        {
            public float X;
            public float Y;
            public float Dir;          // 发射方向（=面朝方向）
            public float AnimTimer;
            public string Sprite;
            public readonly HashSet<object> Hits = new HashSet<object>(); // 每只敌人只结算一次
        }
        private readonly List<FireballProj> _fireballs = new List<FireballProj>();
        private float _fireballDir = 1f; // 最近一次施法方向（供 blast 特效渲染镜像用）
        private MeshDrawer _fireballMesh;
        private M2RenderTicket _fireballTicket;
        private Material _fireballMat;
        // ---- 护符23 吸虫之巢：暗影之魂变成一群黑色吸虫 ----
        private const int FlukeCount = 16;          // 每次施法放出 16 只
        private const int FlukeDamage = 7;          // 每只碰到敌人造成 7 伤害
        private const float FlukeGravity = 28f;     // 重力（格/秒²）
        private const float FlukeLifeMin = 4f;      // 寿命 4~5 秒
        private const float FlukeLifeMax = 5f;
        private const float FlukeScale = 0.24f;     // 渲染缩放（贴图约 90~130px，为翻倍后的 75%）
        private const float FlukeHitRadius = 0.675f; // 碰撞半径（格，同样 75%）
        internal const float FlukePacketMarker = 0.4242f; // 联机包：标记本次伤害来自吸虫
        internal const float ShelterPacketMarker = 0.5243f; // 联机包：标记本次伤害来自防御者纹章
        internal const float ShadowDashPacketMarker = 0.6243f; // 联机包：标记本次伤害来自暗影冲刺
        internal const byte ShadowDashFeedback = 5; // PvP 反馈码：暗影冲刺（轻受击，无击退）
        private sealed class FlukeProj
        {
            public float X;
            public float Y;
            public float Vx;
            public float Vy;          // Y 向下为正
            public float Dir;         // 发射方向（面朝）
            public float Life;
            public float AnimTime;    // 动画计时
            public bool Flopping;     // 是否正在播放地面扑腾动画（落地瞬间）
        }
        private readonly List<FlukeProj> _flukes = new List<FlukeProj>();
        // 切回诺艾尔后仍在飞的吸虫需要继续更新/绘制，但此时 _mp 会被置空，
        // 所以另外留一份地图引用（有值就优先用它）。
        private Map2d _flukeMp;
        private Map2d FlukeMp => _flukeMp != null ? _flukeMp : _mp;

        /// <summary>骑士实体已停用、但还有吸虫在飞（“孤儿吸虫”）：需要每帧继续驱动。</summary>
        public bool HasOrphanFlukes => !_active && _flukes.Count > 0;

        /// <summary>
        /// 每帧由 KnightInCradleBehaviour 调用：切回诺艾尔（骑士实体停用）后，
        /// 继续推进仍在飞的吸虫，并保持它们的渲染票据不被释放。
        /// </summary>
        public static void TickOrphanFlukes()
        {
            KnightEntity k = Instance;
            if (k == null || k._active)
            {
                return; // 骑士模式下由 Update() 正常驱动
            }
            if (k._flukes.Count == 0)
            {
                k.ReleaseOrphanFlukeTicket();
                return;
            }
            try
            {
                k.UpdateFlukes(Time.deltaTime);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>孤儿吸虫全部消失后，把吸虫票据/网格释放掉（避免空票据常驻）。</summary>
        private void ReleaseOrphanFlukeTicket()
        {
            if (_flukeTicket == null && _flukeMesh == null && _flukeMat == null)
            {
                _flukeMp = null;
                return;
            }
            ReleaseTicket(keepFlukeList: true, keepFlukeTicket: false);
            _flukeMp = null;
        }
        private MeshDrawer _flukeMesh;
        private M2RenderTicket _flukeTicket;
        private Material _flukeMat;
        // ---- 护符24 防御者纹章：法阵特效 ----
        private const float ShelterCircleRadius = 3f;    // 实心圆半径（格，也是碰撞半径）
        private const float ShelterRingSpeed = 12f;      // 圆环扩展速度（格/秒）
        private const float ShelterRingInterval = 3f;    // 每 3s 发射一次圆环
        private const float ShelterPatternHoldTime = 0.3f; // 圆环/图案保持满亮度时长
        private const float ShelterPatternFadeTime = 0.3f; // 圆环/图案一起淡出时长
        private const float ShelterFlashTime = 0.25f;    // 边缘发光时长
        private const float ShelterSphereRadius = 0.5f;  // 围绕球体半径（格）
        private const float ShelterSphereOrbit = 1.75f;  // 球心距骑士中心（格）
        private const float ShelterSpherePeriod = 2.5f;  // 公转周期（秒）
        private const int ShelterSphereCount = 5;        // 球体数量（等弧长均布）
        private const float ShelterCircleDamage = 10f;   // 实心圆单次伤害（进入立即 10 点，之后每 1s 10 点）
        private const float ShelterCircleTick = 1f;      // 实心圆伤害间隔（秒）
        private float _shelterRingTimer = ShelterRingInterval;
        private float _shelterRingRadius = -1f;          // -1 = 当前无圆环
        private float _shelterPatternTimer = -1f;        // -1 = 无图案；≥0 = 保持/淡出计时
        private int _shelterPatternType;                 // 0=五角星 1=正方形 2=叉号
        private float _shelterFlashTimer;
        private float _shelterSphereAngle;               // 公转角度（三球 120° 均布）
        private float _shelterDamageTimer;               // 实心圆伤害计时
        private readonly HashSet<NelEnemy> _shelterCircleEntered = new HashSet<NelEnemy>(); // 本跳仍在领域内的敌人（进入第一下判定）
        private readonly HashSet<M2Attackable> _shelterCircleEnteredPlayers = new HashSet<M2Attackable>(); // 领域内的联机玩家
        private Texture2D _shelterCircleTex;             // 深蓝实心圆填充（中心100%→边缘50%）
        private MeshDrawer _shelterCircleMesh;
        private M2RenderTicket _shelterCircleTicket;
        private Material _shelterCircleMat;
        private MeshDrawer _shelterFxMesh;               // 圆环 / 图案 / 边缘发光
        private M2RenderTicket _shelterFxTicket;
        private Material _shelterFxMat;
        private Texture2D _shelterSphereTex;             // 球体贴图（中心100%→边缘20%）
        private MeshDrawer _shelterSphereMesh;
        private M2RenderTicket _shelterSphereTicket;
        private Material _shelterSphereMat;
        // ---- 护符25 发光子宫：自动索敌飞行的幼体 ----
        private const int UterusSpawnSoul = 8;          // 每只消耗灵魂
        private const float UterusSpawnInterval = 3f;   // 每 3s 生成
        private const int UterusMaxCount = 4;           // 最多同时 4 只
        private const int UterusExplosionDamage = 30;   // 碰撞点 6x6 格判定 AoE 伤害
        private const float UterusExplosionSize = 6f;   // 爆炸判定 6x6 格（正方形边长）
        private const float UterusExplosionOffX = 2f;         // 爆炸判定/渲染基础右移（格）
        private const float UterusExplosionRenderOffX = 2f;   // 渲染额外右移（总右移 4 格）
        private const float UterusExplosionRenderOffY = -3.5f;// 渲染总上移（Y 向下为正，负值=上移；1.5+2）
        private const float UterusSpeedMax = 10f;       // 飞行最大速度（格/秒）
        private const float UterusAccel = 32f;          // 朝向目标加速度（格/秒²）
        private const float UterusHitRadius = 0.55f;    // 碰撞半径（格）
        private const float UterusSeekRange = 12f;      // 索敌范围（格）
        private const float UterusScale = 0.22f;        // 渲染缩放
        private const float UterusExplosionFps = 20f;   // 爆炸动画帧率（13 帧约 0.65s）
        private const bool UterusExplosionHitboxDebug = false; // 爆炸判定框绿框调试（已微调完，关闭）
        // ---- 护符31 蜂巢之血：每 10 秒回复 1 血量 ----
        private const float HiveHealInterval = 10f;     // 回复间隔（秒）
        private float _hiveHealTimer = HiveHealInterval;
        private const float KingsoulInterval = 2f;      // 国王之魂：回复间隔（秒）
        private const int KingsoulAmount = 4;           // 国王之魂：每次恢复灵魂
        private float _kingsoulTimer = KingsoulInterval;
        // ---- 护符32 蘑菇孢子：凝聚回血成功释放孢子粒子 ----
        private const int SporeDamage = 5;              // 每次伤害
        private const float SporeRadius = 3.5f;         // 伤害领域半径（格）
        private const float SporeLife = 4.1f;           // 伤害/粒子活跃时长（秒）
        private const float SporeFadeTime = 0.3f;       // 结束淡出时长（秒）
        private const float SporeTotalLife = SporeLife + SporeFadeTime; // 总寿命 4.4s
        private const float SporeTick = 0.5f;           // 领域内重复伤害间隔（秒）
        private const float SporeExpandTime = 0.7f;     // 扩张阶段时长（秒）
        private const int SporeParticlePerColor = 120;  // 每色粒子数（浅绿+黄，共 240）
        private const bool SporeCloudHitboxDebug = false; // 孢子云碰撞箱绿框调试（已关闭）

        /// <summary>
        /// 蘑菇孢子当前每次伤害：羁绊“蘑菇孢子 + 防御者纹章”提升为 7，否则 5。
        /// </summary>
        private int SporeDamageNow()
        {
            return (CharmEffects.IsEquipped(CharmEffects.MushroomId) &&
                    CharmEffects.IsEquipped(CharmEffects.ShelterId)) ? 7 : SporeDamage;
        }

        /// <summary>孢子云领域半径（格）：羁绊“深度聚集 + 蘑菇孢子”提升至 4.5。</summary>
        private float SporeRadiusNow()
        {
            return (CharmEffects.IsEquipped(CharmEffects.DeepGatherId) &&
                    CharmEffects.IsEquipped(CharmEffects.MushroomId)) ? 4.5f : SporeRadius;
        }

        private sealed class SporeCloud
        {
            public float X;                             // 固定中心（生成瞬间读取小骑士中心，格）
            public float Y;
            public float Age;                           // 已存活时间（秒，0→SporeTotalLife）
            public readonly HashSet<NelEnemy> Entered = new HashSet<NelEnemy>();
            public readonly Dictionary<NelEnemy, float> NextHit = new Dictionary<NelEnemy, float>();
            public readonly HashSet<M2Attackable> EnteredPlayers = new HashSet<M2Attackable>();
            public readonly Dictionary<M2Attackable, float> NextHitPlayers = new Dictionary<M2Attackable, float>();
            public readonly List<SporeParticle> Particles = new List<SporeParticle>();
        }
        private sealed class SporeParticle
        {
            public float DirX;                          // 远离中心方向（随机）
            public float DirY;
            public float Speed;                         // 初速 1~10（格/秒）
            public float Radius;                        // 渲染半径（格，0.04~0.06）
            public float DriftX;                        // 累积随机飘动偏移（格）
            public float DriftY;
            public float DriftVx;
            public float DriftVy;
            public float DriftT;
            public int Color;                           // 0=浅绿 1=黄
            public int Layer;                           // 0=骑士身后(PR0) 1=骑士身前(PR1)
        }
        private readonly List<SporeCloud> _sporeClouds = new List<SporeCloud>();
        // 召唤物 PvP 仇恨：仅记录“双方发生过攻击”的远端玩家代理，默认不主动打诺艾尔。
        private readonly HashSet<M2Attackable> _pvpAggroTargets = new HashSet<M2Attackable>();
        private MeshDrawer _sporeCloudMesh;
        private M2RenderTicket _sporeCloudTicket;
        private Material _sporeCloudMat;
        private MeshDrawer _sporeCloudBackMesh;
        private M2RenderTicket _sporeCloudBackTicket;
        private Material _sporeCloudBackMat;
        private MeshDrawer _sporeCloudDbgMesh;
        private M2RenderTicket _sporeCloudDbgTicket;
        private Material _sporeCloudDbgMat;
        // ---- 护符36 编织者之歌：三只小编织者跟随并发射蛛丝 ----
        private const int WeaverCount = 3;              // 小编织者数量
        private const float WeaverRespawnDelay = 3f;    // 过图后等待（秒）再生成
        private const int WeaverDamage = 3;             // 蛛丝命中伤害
        private const int WeaverMeleeDamage = 15;       // 近战攻击伤害
        private const float WeaverMeleeRange = 1.6f;    // 近战攻击距离（格）
        private const float WeaverMeleeInterval = 5f;   // 近战冷却（秒）
        private const float WeaverMeleeAnimTime = 0.3f; // 近战动画时长（秒）
        private const float WeaverFollowSpeed = 12f;    // 跟随爬行速度（格/秒，远快于小骑士走路）
        private const float WeaverHalfH = 0.25f;        // 小编织者渲染半高（格，用于贴地）
        private const float WeaverMaxRange = 3.2f;      // 离小骑士的最大横向距离（格）
        private const float WeaverTooFarRange = 8f;     // 超过该距离（格）则删除重生
        private const float WeaverWanderMin = 1.2f;     // 乱跑目标最小半径（格）
        private const float WeaverWanderMax = 2.6f;     // 乱跑目标最大半径（格）
        private const float WeaverHopVy = -8f;          // 跳跃初速（格/秒，Y 向下为正，负=上）
        private const float WeaverHopGravity = 28f;     // 跳跃重力（格/秒²，落地干脆）
        private const float WeaverHopTime = 0.7f;       // 跳跃时长（秒，足够上升后自然落地）
        private const float WeaverSeekRange = 7f;       // 索敌范围（格）
        private const float WeaverAttackInterval = 2f;  // 攻击间隔（秒）
        private const float WeaverAttackAnimTime = 0.33f; // Attack 动画时长（4 帧 @12fps）

        /// <summary>远程蛛丝冷却（秒）：羁绊“编织者之歌 + 飞毛腿”减为 50%。</summary>
        private float WeaverAttackIntervalNow()
        {
            return (CharmEffects.IsEquipped(CharmEffects.SpiderId) &&
                    CharmEffects.IsEquipped(CharmEffects.RunnerId))
                ? WeaverAttackInterval * 0.5f : WeaverAttackInterval;
        }

        /// <summary>近战攻击冷却（秒）：羁绊“编织者之歌 + 飞毛腿”减为 50%。</summary>
        private float WeaverMeleeIntervalNow()
        {
            return (CharmEffects.IsEquipped(CharmEffects.SpiderId) &&
                    CharmEffects.IsEquipped(CharmEffects.RunnerId))
                ? WeaverMeleeInterval * 0.5f : WeaverMeleeInterval;
        }
        private const float WeaverThreadSpeed = 18f;    // 蛛丝速度（格/秒）
        private const float WeaverThreadLife = 2.2f;    // 蛛丝最长存活（秒）
        private const float WeaverThreadHitRadius = 0.4f; // 蛛丝碰撞半径（格）
        private const float WeaverScale = 0.28f;        // 渲染缩放（贴图约 90px）
        private sealed class Weaverling
        {
            public float X;          // 世界格
            public float Y;
            public float Vx;
            public int State;       // 0=出生 1=跟随 2=攻击 3=睡眠
            public float AnimTime;
            public float AttackCd;
            public float MeleeCd;
            public float MeleeAnimT;    // 近战动画剩余时间（>0 时播放 0014~0016）
            public float SleepT;
            public float HomeOx;    // 跟随停靠偏移（格）
            public float HomeOy;
            public M2Attackable Target;
            public int Face;        // 1=左 -1=右（镜像）
            public bool ThreadFired;
            public float WanderT;   // 换乱跑目标计时
            public float WanderX;   // 当前乱跑目标 X
            public float HopT;      // 当前跳跃剩余时间（>0 = 跳跃中）
            public float HopVy;     // 跳跃垂直速度（格/秒）
            public float NextHopT;  // 距下次跳跃的计时
            public int SleepStage;  // 休息阶段：0=降落中 1=休息动画播放中 2=保持末帧
        }
        private sealed class WeaverThread
        {
            public float X;
            public float Y;
            public float DirX;
            public float DirY;
            public float Speed;
            public float Life;
            public float Dist;      // 已飞行距离（格）
            public readonly HashSet<object> Hits = new HashSet<object>();
        }
        private readonly List<Weaverling> _weaverlings = new List<Weaverling>();
        private readonly List<WeaverThread> _weaverThreads = new List<WeaverThread>();
        private float _weaverRespawnDelay;              // 过图后重生倒计时
        private float _weaverSitTimer;                  // 坐椅累计时长（超 2 秒小蜘蛛入睡）
        private const float WeaverSitSleepTime = 2f;    // 坐椅超过该秒数后入睡
        private MeshDrawer _weaverMesh;
        private M2RenderTicket _weaverTicket;
        private Material _weaverMat;
        // ---- 护符38 梦之盾：围绕骑士公转的盾牌 ----
        private const float ShieldOrbitRadius = 1.5f;     // 轨道半径（格）
        private const float ShieldPeriodBase = 4f;        // 基础公转周期（秒）
        private const float ShieldPeriodFast = 1f;        // 回血时公转周期（秒）
        private const float ShieldSpeedChangeTime = 0.5f; // 变速过渡时长（秒）
        // 羁绊：舞梦者 + 梦之盾 —— 基础周期 3.2s，回血时 1s 内降至 0.5s；
        // 回血时盾牌以 10 格/秒直线远离骑士，停止回血以同速直线返回
        private const float ShieldPeriodBaseBond = 3.2f;
        private const float ShieldPeriodFastBond = 0.5f;
        private const float ShieldSpeedChangeTimeBond = 1f;
        private const float ShieldRadialSpeed = 0.7f;     // 径向远离/返回速度（格/秒）
        private const float ShieldContactRadius = 0.85f;  // 接触判定半径（格）
        private const float ShieldRenderOffX = 1f;        // 渲染/碰撞整体右移（格）
        private const float ShieldCollisionOffX = -1f;    // 碰撞箱相对渲染位置左移（格）
        private const float ShieldCollisionOffY = 0.5f;   // 碰撞箱相对渲染位置下移（格，游戏 Y 向下为正）
        private const float ShieldRenderShiftX = -1f;     // 渲染净偏移 X（与 ShieldRenderOffX 合计为 0，即骑士正上方/下方轨道中心）
        private const float ShieldRenderShiftY = 0.5f;    // 渲染净偏移 Y（游戏 Y 向下为正，合计下移 0.5 格）
        private const float ShieldOmegaBase = 6.2831853f / ShieldPeriodBase; // 基础角速度（弧度/秒）
        private const float ShieldOmegaFast = 6.2831853f / ShieldPeriodFast; // 回血角速度（弧度/秒）
        private const float ShieldRenderScale = 0.24f;    // 渲染缩放（相对贴图原始像素，当前为原尺寸的 40%）
        private float _shieldAngle;      // 当前公转角度（弧度，0=骑士右侧，Y 向下顺时针）
        private float _shieldOmega;      // 当前角速度（弧度/秒）
        private float _shieldOmegaFrom;  // 变速起点角速度
        private float _shieldOmegaTo;    // 变速目标角速度
        private float _shieldSpeedT;     // 变速进度 0~1
        private float _shieldOrbitRadius = ShieldOrbitRadius; // 当前轨道半径（格，羁绊回血时动态增减）
        private readonly HashSet<NelEnemy> _shieldContactEnemies = new HashSet<NelEnemy>();
        private readonly HashSet<M2Attackable> _shieldContactPlayers = new HashSet<M2Attackable>();
        private MeshDrawer _shieldMesh;
        private M2RenderTicket _shieldTicket;
        private Material _shieldMat;
        // ---- 护符39 格林之子：跟随小骑士的小格林 ----
        private const float GrimmSitSleepTime = 2f;    // 坐椅超过该秒数后入睡
        private const float GrimmTeleportRange = 6f;   // 离骑士超过该距离触发传送
        private const float GrimmScale = 0.22f;        // 渲染缩放（相对贴图原始像素）
        private const float GrimmHoverOffX = 0.8f;     // 待机悬浮相对骑士 X 偏移（格）
        private const float GrimmHoverOffY = -1.1f;    // 待机悬浮相对骑士 Y 偏移（格，向上为负）
        private const float GrimmFollowLag = 0.9f;     // 正常跟随速度比例（略低于骑士）
        private const float GrimmMaxSpeedRatio = 2f;   // 最大速度相对骑士速度的倍数（追赶用）
        private const float GrimmRespawnDelay = 3f;    // 过图后重生等待（秒，同编织者之歌）
        private const float GrimmSeekRange = 6f;       // 攻击范围半径（格，以自身为中心）
        private const float GrimmAttackInterval = 2f;  // 攻击间隔（秒）
        private const float GrimmFireballSpeed = 16f;  // 火球速度（格/秒）
        private const float GrimmFireballRadius = 0.25f; // 火球半径（格）
        private const float GrimmFireballLife = 5f;    // 火球寿命（秒）
        private const int GrimmFireballDamage = 30;    // 火球伤害
        private const float GrimmFireballSpread = 30f * Mathf.Deg2Rad; // 副火球偏转角（±30°）
        private const float GrimmFireballScale = 0.3f; // 火球渲染缩放
        private const float GrimmShootFireTime = 4f / 12f; // 第 0004 帧时刻（12fps）
        private sealed class GrimmChild
        {
            public float X, Y;         // 格坐标
            public int Phase;          // 0=出现 1=活跃 2=传送 3=睡眠 4=苏醒
            public float AnimTime;     // 动画时间
            public float AttackCd;     // 攻击冷却
            public int SleepStage;     // 0=降落中 1=播睡眠动画 2=保持末帧
            public float SleepX;       // 落点 X
            public float SleepGroundY; // 落点 Y（脚底贴地）
            public M2Attackable Target; // 攻击目标（敌人或已仇恨的远端玩家）
            public bool TargetIsWeed;  // 目标是否为魔力草
            public float WeedTargetX, WeedTargetY; // 魔力草目标位置
            public AlicePVV200 AliceTarget; // 攻击目标（爱丽丝，不造成伤害）
            public bool Fired;         // 本次攻击是否已发射火球
        }
        private sealed class GrimmFireball
        {
            public float X, Y;
            public float DirX, DirY;
            public float Life;
            public AlicePVV200 Alice; // 爱丽丝目标（火球命中即消失，不造成伤害）
            public readonly HashSet<object> Hits = new HashSet<object>(); // 敌人/魔力草去重
        }
        private GrimmChild _grimm;
        private readonly List<GrimmFireball> _grimmFireballs = new List<GrimmFireball>();
        private float _grimmRespawnDelay;
        private float _grimmSitTimer;
        private int _grimmSoundState;  // 0=静音 1=待机循环 2=攻击音
        private MeshDrawer _grimmMesh;
        private M2RenderTicket _grimmTicket;
        private Material _grimmMat;
        private MeshDrawer _grimmFireballMesh;
        private M2RenderTicket _grimmFireballTicket;
        private Material _grimmFireballMat;
        private sealed class UterusHatchling
        {
            public float X;
            public float Y;
            public float Vx;
            public float Vy;          // Y 向下为正
            public int Phase;         // 0=出生 1=飞行 2=爆炸 3=睡眠（坐椅子）
            public float AnimTime;
            public M2Attackable Target;
            public float HomeOx;      // 站立时相对骑士中心的停靠偏移（会随机变化）
            public float HomeOy;
            public float BuzzTimer;   // 随机微动计时
            public int SleepStage;    // 睡眠阶段 0=下落中 1=已落地（落地后才播睡眠动画）
            public float SleepX;      // 睡眠落点 X（长椅侧面，避免被长椅前板遮挡）
            public float SleepGroundY; // 睡眠时落地的目标 Y（Y 向下为正）
        }
        private readonly List<UterusHatchling> _uterusHatchlings = new List<UterusHatchling>();
        private float _uterusSpawnTimer = UterusSpawnInterval;
        // 碰撞爆炸特效（幼体碰撞后消失，特效在碰撞点播放）
        private sealed class UterusExplosion
        {
            public float X;
            public float Y;
            public float AnimTime;
        }
        private readonly List<UterusExplosion> _uterusExplosions = new List<UterusExplosion>();
        private MeshDrawer _uterusMesh;
        private M2RenderTicket _uterusTicket;
        private Material _uterusMat;
        private MeshDrawer _uterusExplosionDbgMesh;
        private M2RenderTicket _uterusExplosionDbgTicket;
        private Material _uterusExplosionDbgMat;
        // ---- 蜕变挽歌（满血普攻剑气）----
        private const float ElegyBladeSpeed = 30f;   // 剑气速度（格/秒）
        private const float ElegyBladeRange = 4f;    // 剑气射程（格）
        private const int ElegyBladeDamage = 15;     // 剑气伤害
        private const float ElegyBladeHitboxW = 2.0f; // 剑气判定箱长度（格，沿飞行方向）
        private const float ElegyBladeHitboxH = 1.4f; // 剑气判定箱高（格）
        private const bool ElegyBladeHitboxDebug = false; // 绿色判定框调试（诊断用，已关闭）
        private const bool AttackHitboxDebug = false; // 普攻判定框绿框（横劈/上劈/下劈）（调试完已关）
        private const bool DiveHitboxDebug = false;   // 下砸骨剑/尖刺判定框绿框（调试完已关）

        /// <summary>调试：按世界格坐标画一个绿色矩形线框（相对骑士锚点）。</summary>
        private void DrawDbgRect(MeshDrawer mesh, float cx, float cy, float w, float h)
        {
            if (mesh == null)
            {
                return;
            }
            if (_mp == null)
            {
                return;
            }
            // 世界格 → 骑士锚点网格像素：x 向右、y 向上（故 y 取负）
            float x0 = (cx - w * 0.5f - X) * _mp.CLEN;
            float x1 = (cx + w * 0.5f - X) * _mp.CLEN;
            float y0 = -(cy - h * 0.5f - Y) * _mp.CLEN;
            float y1 = -(cy + h * 0.5f - Y) * _mp.CLEN;
            mesh.Line(x0, y0, x1, y0, 2f);
            mesh.Line(x1, y0, x1, y1, 2f);
            mesh.Line(x1, y1, x0, y1, 2f);
            mesh.Line(x0, y1, x0, y0, 2f);
        }
        private const bool HurtBoxDebug = false; // 受击箱绿框（期望）+ 橙框（宿主实际碰撞箱）（调试完已关）
        // pixel2ux 内部乘 0.015625（1/64）：1 ux（骑士本地单位）= 64 mesh px
        private const float UxToMeshPx = 64f;
        /// <summary>
        /// 把“格”单位的数值换算成 ux（偏移公式所用单位）。
        /// 1 格 = _mp.CLEN mesh px，1 ux = 64 mesh px，所以 1 格 = CLEN/64 ux。
        /// 判定框/渲染框的偏移常量（HitboxOffsetX 等）直接以 ux 加在 mx/my 上，
        /// 若把“格”数值直接填入会放大 64/CLEN ≈ 2.286 倍，必须在此换算。
        /// </summary>
        private float CellToUx(float cells)
        {
            float clen = _mp != null ? _mp.CLEN : 28f;
            return cells * (clen / UxToMeshPx);
        }
        private class ElegyBladeProj
        {
            public float X;           // 格坐标
            public float Y;
            public float Dir;         // 1=右, -1=左
            public float Traveled;    // 已飞行距离（格）
            public readonly HashSet<object> Hits = new HashSet<object>(); // 每只敌人只结算一次
        }
        private readonly List<ElegyBladeProj> _elegyBlades = new List<ElegyBladeProj>();
        private bool _elegySpawnedThisSwing; // 本次挥砍是否已发射剑气
        private MeshDrawer _elegyBladeMesh;
        private M2RenderTicket _elegyBladeTicket;
        private Material _elegyBladeMat;
        private MeshDrawer _elegyBladeDbgMesh;
        private M2RenderTicket _elegyBladeDbgTicket;
        private Material _elegyBladeDbgMat;
        // 施法瞬间的爆发特效（fireball_lvl_02_blast_effect）：面前 0.5 格一次性播放
        private string _fireballBlastSprite;
        private float _fireballBlastTimer;
        private string[] _fireballBlastFrames;
        private float _fireballBlastFps;
        private float _fireballBlastX;
        private float _fireballBlastY;
        private MeshDrawer _fireballBlastMesh;
        private M2RenderTicket _fireballBlastTicket;
        private Material _fireballBlastMat;
        private MeshDrawer _fireballDbgMesh;
        private M2RenderTicket _fireballDbgTicket;
        private Material _fireballDbgMat;
        private MeshDrawer _bossDbgMesh;
        private M2RenderTicket _bossDbgTicket;
        private Material _bossDbgMat;
        // ---- 黑暗降临（下砸）----
        private const int DiveSoulCost = 30;
        private const float DiveAnticTime = 0.25f;    // 前摇时长（秒）
        private const float DiveAnticUpSpeed = 4f;    // 地面时前摇内上移速度（格/秒）
        private const float DiveFallMax = 1.2f;       // 下砸最大下落速度（格/帧@60），仅下砸生效
        private const float DiveFallAccelTime = 0.1f; // 下落后 0.1 秒内加速到目标速度
        private const float DiveFallAccelTarget = 40f; // 0.1 秒后达到的下落速度（格/秒，真实秒）
        private const int DivePathDamage = 10;        // 下落路径伤害
        private const int DiveSwordDamage = 15;       // 骨剑伤害
        private const int DiveSpikeDamage = 8;        // 尖刺伤害
        private const float DiveSwordRiseTime = 0.1f;    // 骨剑升起时长（秒，所有剑统一）
        private const float DiveSwordFallTime = 0.1f;    // 骨剑降下时长（秒，所有剑统一）
        private const float DiveSwordHoldTime = 0.4f;    // 全部到顶后整体等待时长（秒）
        private const float DiveSwordFallStep = 0.05f;   // 两侧剑相对中央剑的下降延时步进（秒）
        private const float DiveSwordWidth = 1.35f;      // 落点骨剑宽度（格），其它剑按高度比例缩放（进游戏调试）
        private const float DiveSwordAspect = 329f / 89f; // 骨剑贴图 89×329（高:宽）
        private const float DiveSwordHitboxOffY = 1f;     // 骨剑判定箱整体下移 1 格（y 向下为正）
        private const float DiveSwordHitboxOffX = -0.5f;  // 骨剑判定箱整体左移 0.5 格
        private const float DiveSwordRenderOffX = 0.5f;   // 骨剑渲染相对落点：X 偏移（向右为正）
        private const float DiveSwordRenderOffY = -2f;    // 骨剑渲染相对落点：Y 偏移（Y 向下为正，向上为负）
        private const float DiveSwordSide1RenderOffY = -1.2f; // 左右 1 格：中央(-2) + 1 - 0.2（在之前基础上再 y-0.2）
        private const float DiveSwordSide2RenderOffY = -0.8f; // 左右 2 格：中央(-2) + 2 - 0.8（在之前基础上再 y-0.8）
        private const float DiveSpikeBaseWidth = 0.5f;    // 尖刺底宽（格）
        private const float DiveSpikeAspect = 122f / 58f; // 尖刺贴图 58×122（高:宽）
        private const float DiveSpikeRiseTime = 0.05f;    // 尖刺升起/降下时长（秒）
        private const float DiveSpikeStepDelay = 0.05f;   // 尖刺逐个生成/下降延迟（秒）
        private const int DiveSpikePerSide = 11;          // 每侧尖刺数量
        private const float DiveSpikeSpacing = 0.5f;      // 尖刺间距（格）
        private const float DiveSpikeHitboxOffX = -0.25f; // 尖刺判定箱右移 0.25 格（原 -0.5）
        private const float DiveSpikeHitboxOffY = 1f;     // 尖刺判定箱整体下移 1 格（y 向下为正）
        private const float DiveSpikeRenderOffY = 0.15f;  // 尖刺渲染相对尖刺底部：Y 偏移（Y 向下为正）
        private const float DiveLandHardlock = 0.1f;  // 落地硬直（秒）
        private const float DiveInvulnTime = 0.6f;    // 硬直结束后的可行动无敌（秒）；硬直 0.1s 也算无敌，落地后总计 0.7s
        private const float DiveCooldown = 0.3f;      // 落地后冷却（秒）
        private const float DiveAnimScale = 0.9f;     // 下砸动画绘制缩放（相对普通帧）
        private bool _diving;
        private int _divePhase;            // 0=前摇 1=下落 2=落地硬直
        private float _diveNoLand;         // 过图强制位移免落地窗口（>0 秒内下砸不判定落地）
        private float _diveNoLandMeasure;  // 强制位移测量计时
        private bool _diveSimWasActive;    // 测量中模拟键是否已出现过
        private float _diveTimer;
        private float _diveHardlockTimer;  // 落地硬直剩余（仅前 0.1s 锁输入）
        private float _diveCooldown;
        private float _diveInvulnTimer;
        private float _diveLandX;
        private float _diveLandY;
        private readonly HashSet<object> _divePathHits = new HashSet<object>();
        private readonly HashSet<object> _diveAreaHits = new HashSet<object>(); // 下砸范围破坏（魔力草/虫墙）去重
        // 骨剑：落点 5 把（0、±1、±2 格），每把独立判定箱，伤害 15
        private sealed class DiveSword
        {
            public float X;         // 落点 X（格）
            public float BaseY;     // 底部 Y（地面，格）
            public float MaxHeight; // 最大升起高度（格）
            public float Width;     // 宽（格）
            public float RiseStart; // 升起开始时刻（秒，落地后全局时间）
            public float FallStart; // 降下开始时刻（秒，落地后全局时间）
            public float RiseTime;  // 升起时长（秒）
            public float FallTime;  // 降下时长（秒）
            public float RenderOffY;// 渲染 Y 偏移（格，Y 向下为正）
            public float Timer;     // 累计时间（秒）
            public float Height;    // 当前高度（格）
            public readonly HashSet<object> Hits = new HashSet<object>();
        }
        private readonly List<DiveSword> _diveSwords = new List<DiveSword>();
        // 尖刺：落点两侧各 11 个，底宽 0.5 格，伤害 5
        private sealed class DiveSpike
        {
            public float X;         // 中心 X（格）
            public float BaseY;     // 底部 Y（地面，格）
            public float Delay;     // 生成延迟（秒）
            public float Timer;     // 累计时间（秒）
            public float Height;    // 当前高度（格）
            public readonly HashSet<object> Hits = new HashSet<object>();
        }
        private readonly List<DiveSpike> _diveSpikes = new List<DiveSpike>();
        private MeshDrawer _diveSwordMesh;
        private M2RenderTicket _diveSwordTicket;
        private Material _diveSwordMat;
        private MeshDrawer _diveSpikeMesh;
        private M2RenderTicket _diveSpikeTicket;
        private Material _diveSpikeMat;
        // 落地黑/白粒子：80% 在小骑士身后层（PR0），20% 身前层（PR1）
        private readonly List<LightDotParticle> _diveLandParticles = new List<LightDotParticle>();
        private MeshDrawer _diveFxBackMesh;
        private M2RenderTicket _diveFxBackTicket;
        private Material _diveFxBackMat;
        private MeshDrawer _diveFxFrontMesh;
        private M2RenderTicket _diveFxFrontTicket;
        private Material _diveFxFrontMat;
        // ---- 深渊尖啸（法术·尖啸）----
        private const int ScreamSoulCost = 30;            // 灵魂消耗
        private const float ScreamAnimTime = 0.62f;       // 尖啸持续（滞空）时长（秒）
        private const float ScreamDamageStart = 0.15f;    // 按键后短暂延迟开始伤害（秒）
        private const float ScreamTickInterval = 0.1f;    // 伤害判定间隔（秒）
        private const int ScreamTickCount = 5;            // 最多伤害判定次数
        private const int ScreamDamage = 40;              // 单次伤害
        private const float ScreamTriHeight = 3.0f;       // 三角判定箱高（格）
        private const float ScreamTriHalfWidth = 2.75f;   // 三角判定箱底半宽（格，等腰直角）
        private const float ScreamCircleOffsetY = 3.5f;   // 圆判定箱中心（骑士中心上方，格）
        private const float ScreamCircleRadius = 2.95f;   // 圆判定箱半径（格）
        private const float ScreamBlastRenderW = 6.5f;    // 尖啸冲击波渲染宽（格）
        private const float ScreamBlastRenderH = 6.5f;    // 尖啸冲击波渲染高（格）
        private const float ScreamBlastRenderOffX = 3f;   // 尖啸冲击波渲染相对骑士：X 偏移（右为正；3.2 - 0.2）
        private const float ScreamBlastRenderOffY = -6.3f;// 尖啸冲击波渲染相对骑士：Y 偏移（向下为正，向上为负；-6 - 0.3）
        private const float ScreamParticleBrakeFactor = 0.12f; // 粒子中程大幅减速倍率
        private bool _screaming;          // 尖啸进行中（滞空、锁输入）
        private float _screamTimer;
        private float _screamTickTimer;
        private int _screamTickIndex;
        private readonly Dictionary<object, int> _screamHitCounts = new Dictionary<object, int>(); // 每只怪最多吃 5 次判定
        // 尖啸单段内去重（同一目标有多个碰撞体时，一段只结算一次）
        private readonly HashSet<object> _screamTickTargets = new HashSet<object>();
        private int _screamTickDone;        // 本次尖啸已执行的判定次数（诊断：应为 ScreamTickCount）
        private int _screamDamageApplied;   // 本次尖啸实际结算的伤害段数（诊断）
        private readonly HashSet<object> _screamAreaHits = new HashSet<object>(); // 尖啸范围破坏（魔力草/虫墙）去重
        // 尖啸特效：上升冲击波 + 地面底座（骑士身前层）
        private string _screamBlastSprite;
        private float _screamBlastTimer;
        private string[] _screamBlastFrames;
        private float _screamBlastFps;
        private MeshDrawer _screamBlastMesh;
        private M2RenderTicket _screamBlastTicket;
        private Material _screamBlastMat;
        // 尖啸黑/白粒子：80% 在小骑士身后层（PR0），20% 身前层（PR1）
        private readonly List<LightDotParticle> _screamParticles = new List<LightDotParticle>();
        private MeshDrawer _screamFxBackMesh;
        private M2RenderTicket _screamFxBackTicket;
        private Material _screamFxBackMat;
        private MeshDrawer _screamFxFrontMesh;
        private M2RenderTicket _screamFxFrontTicket;
        private Material _screamFxFrontMat;
        // ---- 水晶之心（超级冲刺）----
        private const float SuperDashChargeTime = 0.8f;   // 蓄满能量所需时间（秒）
        private const float SuperDashSpeed = 0.45f;       // 飞行速度（格/帧@60，≈HK 30 换算）
        private const float SuperUpDashSpeed = 27f / 60f; // 水晶升腾速度：27 格/秒（格/帧@60）
        private const float SuperDashStartLock = 0.18f;   // 起步短时间内不可主动停止
        private const float SuperInertiaTime = 0.35f;     // 停止后惯性飞行时长
        private const float SuperInertiaSpeed = 0.28f;    // 惯性初始速度（格/帧@60）
        private const float SuperDashFlyLift = 0.25f;     // 发射时抬升高度（格），避免刚发射就贴地撞上
        private const float SuperWallHitTime = 0.5f;      // 撞可攀爬墙后的停顿时长（秒）
        private const float SuperWallPumpTime = 0.55f;    // 频繁按键减慢下滑的保持时长
        private const float SuperSlideSlowSpeed = 0.03f;  // 墙上蓄力时的下滑速度（很慢）
        private const float SuperSlidePumpSpeed = 0.008f; // 频繁按键时的下滑速度（更慢）
        private const int SuperDashDamage = 20;           // 撞敌 / 起始爆发伤害
        private const float SuperDashWatchdogTime = 10f;  // 冲刺/蓄力持续超过 10s 强制解除（防卡死）
        private bool _superCharging;        // 蓄力中
        private float _superChargeTime;
        private bool _superCharged;         // 已蓄满（≥0.8s）
        private float _superWallPump;       // 墙上频繁按键的减慢计时
        private float _superUnchargeTimer;  // 取消蓄力后的起身动画时长
        private float _superDashAliveTimer; // 冲刺/蓄力持续计时（看门狗）
        private float _superDashStuckTimer; // 冲刺无位移计时（看门狗）
        private float _superDashLastX;      // 冲刺位置采样（看门狗）
        private float _superDashLastY;
        private float _pendingRepositionStuckTimer; // 重定位卡住计时（看门狗）
        private float _lingeringEvTimer;    // 转房事件残留计时（强制结束兜底）
        private bool _superDashing;         // 超级冲刺飞行中
        private bool _superUpDash;          // 水晶升腾（向上超级冲刺）飞行中
        private int _superUpDashWallGrace;  // 上冲撞墙宽限帧：门口上方墙先等转房，不触发才停
        /// <summary>超级冲刺飞行中（供镜头读取，切换超冲专用快速镜头）。</summary>
        public bool IsSuperDashing => _superDashing;
        private float _superDashDir = 1f;
        private float _superDashTime;
        private float _superStartLock;      // 起步锁定（不可主动停止）
        private readonly HashSet<object> _superDashHit = new HashSet<object>();
        private float _superInertiaTimer;   // 停止后惯性飞行计时（期间不自动攀附）
        private float _superInertiaDir = 1f;
        private float _superBrakeTimer;     // SD Air Brake 停止动画时长
        private float _superWallHitTimer;   // 撞墙停顿计时（期间播 superdash_wall_hit 并停止下滑）
        private bool _horizontalTransfer;   // 本次过图是否为左右方向（横向才启用进门强化处理）
        private bool _pendingRepositionActive; // 过图跟随阶段（传送/重定位期间）标志
        private bool _prevTransferring; // 上一帧是否处于地图传送暂停（用于快速旅行到达检测）
        private bool _fastTravelInProgress; // 快速旅行跟随中：延长到诺艾尔到达目的地长椅
        private int _fastTravelWaitFrames;  // 快速旅行等待帧数（超时兜底）
        private float _fastTravelArrivedTimer = -1f; // 到达目的地后的 1 秒缓冲（期间骑士继续跟随诺艾尔）
        private float _fastTravelNoBenchTimer = -1f; // 无长椅目的地（战斗区域）的落地缓冲
        private bool _fastTravelPrevTransferring;    // 快速旅行期间上一帧是否处于地图传送暂停
        private bool _fastTravelMapChanged;          // 快速旅行期间是否已切换地图（跨图信号）
        private float _lastSafeX;           // 最后安全站立点（掉出地图兜底回传）
        private float _lastSafeY;
        // 进门强制位移检测：walk_in 脚本用 simulate_key 模拟按键，清零即位移结束
        private static readonly System.Reflection.FieldInfo SimKeyField =
            typeof(m2d.M2MoverPr).GetField("simulate_key",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        // M2ManaWeed 私有 time 字段：>0=枯萎中、<0=重生中、==0=完全长成。
        // isActiveMana() 在重生阶段（time<0）也返回 true，会导致“已破坏的草仍被再次破坏并加魂”，
        // 因此这里直读 time，只允许在完全长成（==0）时采集。
        private static readonly System.Reflection.FieldInfo ManaWeedTimeField =
            AccessTools.Field(typeof(M2ManaWeed), "time");
        // 蜘蛛 BOSS：蛛丝球（MgNWebShot）所在魔法容器（MGContainer : XX.RBase<MagicItem>）
        private static readonly System.Reflection.FieldInfo MagicAItemsField =
            typeof(XX.RBase<MagicItem>).GetField("AItems",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo MagicLenField =
            typeof(XX.RBase<MagicItem>).GetField("LEN",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // 地图的“额外 BCC 容器”列表（弹簧地板等芯片 BCC 所在）
        private static readonly System.Reflection.FieldInfo MpAbcConField =
            typeof(m2d.Map2d).GetField("ABcCon",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private float _transferSimDur;
        // 蓄力水晶 / 冲刺拖尾 特效帧
        private int _sdCrystalShown;       // 已出现的水晶碎片数（每侧；蓄力时两侧对称逐帧出现并保留）
        private Texture2D _sdCrystalAtlasTex; // 8 个水晶碎片 × 3 朝向（正常/顺时针/逆时针）合成一张图集
        private float[][] _sdCrystalUvLeft; // [朝向][碎片]：0=正常 1=顺时针90°(左墙) 2=逆时针90°(右墙)
        private float[][] _sdCrystalUvTop;
        private float[][] _sdCrystalUvW;
        private float[][] _sdCrystalUvH;
        private float[][] _sdCrystalDrawW; // 贴图像素宽（绘制时乘缩放）
        private float[][] _sdCrystalDrawH;
        // 蓄力完成时的紫色收缩光圈（从外围一圈圈收向自身中心）
        private sealed class SdRing
        {
            public float Age;
            public float Life;
            public float StartR;
            public float EndR;
        }
        private readonly List<SdRing> _sdRings = new List<SdRing>();
        private float _sdRingSpawnTimer;
        private const float SdRingSpawnInterval = 0.22f;
        private const float SdRingLife = 0.55f;
        private const float SdRingStartR = 1.7f;  // 起始半径（格）
        private const float SdRingEndR = 0.18f;   // 收缩终点半径（格）
        private MeshDrawer _sdRingMesh;
        private M2RenderTicket _sdRingTicket;
        private Material _sdRingMat;
        private string _sdTrailSprite;
        private float _sdTrailTimer;
        private string _sdFxSprite;      // 一次性特效（发射爆发 / 撞击碎裂）
        private float _sdFxTimer;
        private string[] _sdFxFrames;
        private float _sdFxFps;
        private MeshDrawer _sdCrystalMesh;
        private M2RenderTicket _sdCrystalTicket;
        private Material _sdCrystalMat;
        private MeshDrawer _sdTrailMesh;
        private M2RenderTicket _sdTrailTicket;
        private Material _sdTrailMat;
        private MeshDrawer _sdFxMesh;
        private M2RenderTicket _sdFxTicket;
        private Material _sdFxMat;
        private bool _superJumpStop;     // 按跳跃停止冲刺后，本帧不再触发跳跃
        private static KeyCode _superDashKey = KeyCode.C;
        private static KeyCode _superDashAltKey = KeyCode.None;
        private static bool _superDashKeyInit;
        // 可配置键位缓存（每帧从配置读取，玩家改 cfg 后重启生效）
        private static KeyCode _focusKey = KeyCode.C;
        private static KeyCode _fireballKey = KeyCode.S;
        private static KeyCode _dreamNailKey = KeyCode.Space;
        private static KeyCode _tauntKey = KeyCode.V;
        private static bool _keyInit;
        // 聚集/施法合并键（C）：快速点按=施法（+上=深渊尖啸、+下=黑暗降临），长按=凝聚回血。
        // 点按/长按判别：按下后 CastTapLimitFrames 帧内松开 → 点按施法；超过 → 视为长按凝聚。
        private const int CastTapLimitFrames = 10; // 约 0.17s
        private int _castTapTimer;
        private bool _castTapWait;   // 正在等待“松开=施法 or 超时=凝聚”
        private bool _castTapFocus;  // 已超时：当前按住合并键视为凝聚

        private static void InitConfigKeys()
        {
            if (_keyInit)
            {
                return;
            }
            _keyInit = true;
            _focusKey = KeyConfig.Parse(
                KnightInCradlePlugin.FocusKey != null ? KnightInCradlePlugin.FocusKey.Value : null, KeyCode.C);
            _fireballKey = KeyConfig.Parse(
                KnightInCradlePlugin.FireballKey != null ? KnightInCradlePlugin.FireballKey.Value : null, KeyCode.S);
            _dreamNailKey = KeyConfig.Parse(
                KnightInCradlePlugin.DreamNailKey != null ? KnightInCradlePlugin.DreamNailKey.Value : null, KeyCode.Space);
            _tauntKey = KeyConfig.Parse(
                KnightInCradlePlugin.TauntKey != null ? KnightInCradlePlugin.TauntKey.Value : null, KeyCode.V);
        }

        private static bool TauntPressed()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKeyDown(_tauntKey);
        }

        private static bool FocusHeld()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKey(_focusKey);
        }

        private static bool FocusPressed()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKeyDown(_focusKey);
        }

        private static bool FocusReleased()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKeyUp(_focusKey);
        }

        /// <summary>
        /// 聚集/施法合并键（C）点按判定：按住超过 CastTapLimitFrames 帧 → 进入凝聚模式；
        /// 在此之前松开 → 返回 true 表示本帧应触发一次“点按施法”。
        /// </summary>
        private bool UpdateCastTapState(bool pressed, bool held, bool released)
        {
            bool tapCast = false;
            if (pressed)
            {
                _castTapWait = true;
                _castTapTimer = CastTapLimitFrames;
            }
            if (_castTapWait && held)
            {
                _castTapTimer--;
                if (_castTapTimer <= 0)
                {
                    _castTapWait = false;
                    _castTapFocus = true; // 长按：转入凝聚
                }
            }
            if (_castTapWait && released)
            {
                _castTapWait = false;
                tapCast = true; // 快速松开：点按施法
            }
            if (released)
            {
                _castTapFocus = false;
            }
            return tapCast;
        }

        /// <summary>清空合并键的点按/凝聚判定状态（输入被锁定/重置时调用）。</summary>
        private void ResetCastTapState()
        {
            _castTapWait = false;
            _castTapFocus = false;
            _castTapTimer = 0;
        }

        private static bool FireballHeld()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKey(_fireballKey);
        }

        private static bool FireballPressed()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKeyDown(_fireballKey);
        }

        private static bool DreamNailHeld()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKey(_dreamNailKey);
        }

        private static bool DreamNailPressed()
        {
            InitConfigKeys();
            return UnityEngine.Input.GetKeyDown(_dreamNailKey);
        }
        // 受击粒子爆发（白色为主 + 黑色虚空，加大版受击特效）
        private readonly List<LightDotParticle> _hitFxParticles = new List<LightDotParticle>();
        private MeshDrawer _hitFxMesh;
        private M2RenderTicket _hitFxTicket;
        private Material _hitFxMat;
        // 低血量（1 格）：黑色虚空粒子 + 俯身姿态 + 屏幕黑边
        private const float LowHpParticleInterval = 0.12f;
        private readonly List<LightDotParticle> _lowHpParticles = new List<LightDotParticle>();
        private float _lowHpSpawnTimer;
        private MeshDrawer _lowHpMesh;
        private M2RenderTicket _lowHpTicket;
        private Material _lowHpMat;
        // 亡者之怒：身后红色光晕（背后层 PR0，脉冲闪烁）
        private MeshDrawer _furyGlowMesh;
        private M2RenderTicket _furyGlowTicket;
        private Material _furyGlowMat;
        private Texture2D _furyGlowTex;
        // 虚空解放第二段：身后双图迅速交替 + 随机旋转（背后层 PR0）
        private MeshDrawer _voidBackMesh;
        private M2RenderTicket _voidBackTicket;
        private Material _voidBackMat;
        private Texture2D _voidBackTex1;
        private Texture2D _voidBackTex2;
        private int _voidBackIndex;
        private float _voidBackSwitchTimer;
        private float _voidBackRot;
        private const float VoidBackSwitchInterval = 0.1f;  // 切换间隔（秒）
        private const float VoidBackSize = 6f;              // 身后图直径（格）
        private float _furyGlowTimer;
        // 生命血羁绊：身后蓝色光晕（复刻亡者之怒，背后层 PR0，脉冲闪烁）
        private MeshDrawer _lifebloodGlowMesh;
        private M2RenderTicket _lifebloodGlowTicket;
        private Material _lifebloodGlowMat;
        private Texture2D _lifebloodGlowTex;
        private float _lifebloodGlowTimer;
        private const float LifebloodHealInterval = 7f;  // 每 7 秒回 1 血
        private float _lifebloodHealTimer = LifebloodHealInterval;
        // 护符22 巴尔德之壳：凝聚回血时硬壳护体，每次战斗最多挡 4 次
        private const int BaldurMaxBlocks = 4;
        private const float BaldurShellScale = 0.26f; // 壳贴图渲染缩放（相对骑士公共 scale）
        private int _baldurBlocksLeft = BaldurMaxBlocks;
        private bool _baldurActive;    // 壳当前是否展开（凝聚中且未破碎）
        private bool _baldurInBattle;  // 当前是否处于战斗区域
        private string _baldurFxSprite; // 当前壳动画帧
        private string[] _baldurFxFrames;
        private float _baldurFxTimer;
        private float _baldurFxFps;
        private int _baldurFxIndex;
        private bool _baldurFxPlaying;
        private string _baldurHeldFrame; // 壳完全展开后的持有帧
        private MeshDrawer _baldurShellMesh;
        private M2RenderTicket _baldurShellTicket;
        private Material _baldurShellMat;
        // 护符43 无忧旋律：受伤时按概率免伤（25% 起，未抵挡成功 +25%，抵挡成功回到 25%）
        private float _melodyChance = 0.25f;
        private bool _melodyBlockedThisHit; // 本次受击是否被无忧旋律抵挡（照常受击流程，只跳过扣血）
        // 魔法霰弹是短时间多段判定：若骑士处于受伤无敌帧，后续段会绕过常规荆棘触发。
        // 用 (攻击实例, 帧) 去重，让霰弹的反伤在第一段就必定触发一次。
        private int _shotgunThornsFrame = -1;
        private const float MelodyFlashTime = 0.35f; // 免伤成功红色屏幕闪光时长（同回血白闪）
        private float _melodyFlashAlpha;
        // 死亡 / 重生（回到上次休息的长椅）
        private const float DeathBlackInTime = 0.5f;    // 黑屏淡入
        private const float DeathBlackHoldTime = 0.4f;  // 全黑保持（缩短，尽早露出苏醒动画）
        private const float DeathBlackOutTime = 0.45f;  // 黑屏淡出（加快，苏醒动画更早可见）
        private bool _isDead;
        private float _deathTimer;
        private float _deathBlackAlpha;
        private float _deathBlackPhase; // 0=淡入 1=保持 2=淡出
        private bool _respawnFadeOut;
        private float _respawnFadeTimer;
        // 复活后在长椅上坐着 + 苏醒动画
        private bool _respawnWaking;
        private float _respawnWakeTimer;
        private bool _hasRespawn;
        private float _respawnX;
        private float _respawnY;
        private Map2d _respawnMp;
        private bool _initialRespawnRecorded;
        // 虫墙（BugWall）：记录每面墙已受攻击次数，2 次后摧毁（进出房间由引擎重新生成，无需复位）
        private readonly Dictionary<Collider2D, int> _bugWallHits = new Dictionary<Collider2D, int>();
        // 虫巢格（worm/ 芯片）的受击计数：按格坐标记录，2 次后隐藏虫巢芯片
        private readonly Dictionary<long, int> _bugWallCellHits = new Dictionary<long, int>();
        // 虫巢墙（M2LpBreakable）实例的受击计数：整面墙共用，2 次后调用 AIC 原版破坏
        private readonly Dictionary<M2LpBreakable, int> _bugWallLpHits = new Dictionary<M2LpBreakable, int>();
        private Collider2D[] _hitboxColliders;
        private GameObject _hitboxGo;
        private MeshDrawer _attackDbgMesh;
        private M2RenderTicket _attackDbgTicket;
        private Material _attackDbgMat;
        private MeshDrawer _hurtDbgMesh;
        private M2RenderTicket _hurtDbgTicket;
        private Material _hurtDbgMat;
        // 骨钉技艺判定框绿框调试（强力/冲刺/旋风三种）：框线用“真实判定几何”换算，
        // 与 CheckNailArtHit / CheckDashSlashHit / CheckCycloneHit 的 Physics2D 查询严格一致
        private const bool NailArtHitboxDebug = false;   // 骨钉技艺三大招式判定框绿框（调试完已关）
        private const bool DashSlashHitboxDebug = false;
        private const bool CycloneHitboxDebug = false;
        private MeshDrawer _nailArtDbgMesh;
        private M2RenderTicket _nailArtDbgTicket;
        private Material _nailArtDbgMat;
        private MeshDrawer _dashSlashDbgMesh;
        private M2RenderTicket _dashSlashDbgTicket;
        private Material _dashSlashDbgMat;
        private MeshDrawer _cycloneDbgMesh;
        private M2RenderTicket _cycloneDbgTicket;
        private Material _cycloneDbgMat;
        // 拼刀判定框调试：绿框=本地骨钉判定箱，红框=同步到的远端骨钉判定箱（相交即拼刀）
        private const bool NailParryDebug = false;
        private MeshDrawer _nailParryDbgMesh;
        private M2RenderTicket _nailParryDbgTicket;
        private Material _nailParryDbgMat;
        private MeshDrawer _fxMesh;
        private M2RenderTicket _fxTicket;
        private Material _fxMat;
        private Texture2D _fxTex;
        private string _fxSpriteName;
        private string _fxClipName;
        private int _fxFrameIndex;
        private float _fxFrameTimer;

        private const float WalkSpeed = 0.1f;
        private const float Gravity = 0.009f;
        private const float JumpVy = -0.285f;
        // ---- 游泳（水面漂浮 / 水下）----
        private const float SwimSurfaceSpeed = 0.075f;   // 液面移动速度（格/帧@60，≈走路 × 0.75）
        private const float SwimSurfaceSink = 0.65f;     // 浮在液面时脚底比液面低（格；0.45 + 0.2）
        private const float SwimRiseSpeed = WalkSpeed;   // 液体内按住上键的上浮速度（与走路速度一致，格/帧@60）
        private const float SwimEnterTime = 0.3f;        // 入水过渡动画时长（秒）
        private const float SwimMinSurfaceRow = 2f;      // 液面至少在第 2 行以下才能漂浮（全水房间液面在顶端则转水下）
        private const float SwimMaxRiseY = 0.55f;        // 上浮时骑士中心 Y 的下限（头不越出地图顶端）
        private int _swimState;        // 0=陆地 1=液面漂浮 2=液内
        private float _swimSurfaceY;   // 当前液面 Y（格，向下为正）
        private float _swimEnterTimer; // 入水过渡动画剩余
        private bool _swimRising;      // 液内按住上键上浮中（跳过重力与地面吸附，避免被池底吸回）
        private float _noelO2ProtectTimer; // 保护诺艾尔不溺水的节流计时
        // ---- 骨钉技艺·强力劈砍（Great Slash）----
        private const float NailArtHoldStartTime = 0.2f;  // 长按攻击键 0.2s 后才开始蓄力
        private const float NailArtChargeTime = 1.35f;   // 骨钉技艺蓄满所需时间（秒，HK 原版；35 号护符降至 0.75s）
        private const float NailArtChargeTimeFast = 0.5f; // 护符35 骨钉大师的荣耀：蓄力缩短至 0.5s
        /// <summary>强力劈砍单次伤害（3 段共 126）；束缚·骨钉时降为 30。</summary>
        private int NailArtDamage => CharmEffects.GgNailBound ? 36 : 42;
        private const float NailArtDwellInterval = 0.15f; // 目标驻留判定箱内每 0.15s 追加一次伤害
        private const int NailArtTickCount = 3;           // 每只怪最多伤害次数（进箱 1 + 驻留 2，总 126）
        private const float NailArtSlashTime = 0.48f;    // 劈砍持续（滞空）时长（秒）
        private const float NailArtFallSpeed = 0.0135f;  // 空中劈砍缓慢下落速度（格/帧@60，≈0.9 换算）
        private const float NailArtHitboxW = 4.4f;       // 判定箱宽（格，向右拉伸 0.5 → 3.9+0.5）
        private const float NailArtHitboxH = 2.9f;       // 判定箱高（格，上下各拉伸 0.2 → 2.5+0.4）
        private const float NailArtHitboxOffX = 1.95f;   // 判定箱中心相对骑士 X（格，向右拉伸中心 +0.25）
        private const float NailArtHitboxOffY = 0.4f;    // 判定箱中心相对骑士 Y（格，向下为正）
        private const float NailArtSlashFxW = 4.5f;      // 剑气渲染宽（格，向右拉伸 0.5 → 4+0.5）
        private const float NailArtSlashFxH = 2.9f;      // 剑气渲染高（格，上下各拉伸 0.2 → 2.5+0.4）
        private const float NailArtSlashFxOffY = -0.8f;  // 剑气渲染相对骑士 Y（格，向上为负）
        private const float NailArtSlashFxOffX = 4.0f;   // 右劈时剑气渲染相对骑士 X（格，向右拉伸中心 +0.25）
        private const float NailArtSlashFxOffXLeft = 0.0f; // 左劈时剑气渲染相对骑士 X（格，向左拉伸中心 -0.25）
        private const float NailArtGlowOffX = 1.2f;      // 蓄满光圈渲染相对骑士 X（格）
        private const float NailArtGlowOffY = -0.5f;     // 蓄满光圈渲染相对骑士 Y（格；0 - 0.5）
        // 白色蓄力粒子（替代 first_charge_effect）
        private const int NailArtChargeParticlePerFrame = 2;   // 蓄力期间每帧生成粒子数
        private const float NailArtChargeParticleSpeedMin = 3f; // 粒子向中心速度下限（格/秒）
        private const float NailArtChargeParticleSpeedMax = 4f; // 粒子向中心速度上限（格/秒）
        private const float NailArtChargeParticleMaxLife = 1.2f; // 粒子最长存活（秒）
        private const float NailArtChargeParticleFade = 0.2f;   // 蓄满后淡出时长（秒）
        private bool _nailArtCharging;
        private bool _nailArtCharged;
        private bool _nailArtHoldPending;   // 已按下攻击键，等待 0.3s 长按判定
        private float _nailArtHoldTimer;
        private float _nailArtChargeTimer;
        private bool _nailArtQuickTap;     // 未蓄满松开 → 转为普通攻击
        private bool _nailArtSlashing;
        private float _nailArtSlashTimer;
        private float _nailArtSlashDir;
        private readonly Dictionary<object, int> _nailArtHits = new Dictionary<object, int>(); // 每只怪已受伤害次数
        private readonly Dictionary<object, float> _nailArtDwell = new Dictionary<object, float>(); // 目标驻留判定箱内累计时间
        private readonly HashSet<object> _nailArtInsideNow = new HashSet<object>(); // 本帧在判定箱内的目标
        private readonly HashSet<object> _nailArtWeedHits = new HashSet<object>(); // 蓄力劈砍破坏魔力草去重
        // 蓄力白色粒子（骑士身后层 PR0）
        private readonly List<LightDotParticle> _nailArtChargeParticles = new List<LightDotParticle>();
        private MeshDrawer _nailArtChargeParticleMesh;
        private M2RenderTicket _nailArtChargeParticleTicket;
        private Material _nailArtChargeParticleMat;
        // 蓄满光圈（nail_charge_effect，骑士身后层）
        private string _nailArtGlowSprite;
        private float _nailArtGlowTimer;
        private string[] _nailArtGlowFrames;
        private float _nailArtGlowFps;
        private MeshDrawer _nailArtGlowMesh;
        private M2RenderTicket _nailArtGlowTicket;
        private Material _nailArtGlowMat;
        // 强力劈砍剑气（charge_slash_effect）
        private string _nailArtSlashFxSprite;
        private float _nailArtSlashFxTimer;
        private string[] _nailArtSlashFxFrames;
        private float _nailArtSlashFxFps;
        private MeshDrawer _nailArtSlashFxMesh;
        private M2RenderTicket _nailArtSlashFxTicket;
        private Material _nailArtSlashFxMat;
        // ---- 骨钉技艺·冲刺劈砍（Dash Slash）----
        private const int DashSlashDamage = 125;          // 冲刺劈砍伤害
        private const float DashSlashHitboxW = 5f;        // 判定箱宽（格）
        private const float DashSlashHitboxH = 2.2f;      // 判定箱高（格，基础 2 + 向下拉伸 0.2）
        private const float DashSlashHitboxOffX = 2.5f;   // 判定箱中心相对骑士 X（格，冲刺方向为正）
        private const float DashSlashHitboxOffY = 0.1f;   // 判定箱中心相对骑士 Y（格；向下移动 0.5 → -0.4+0.5）
        private const float DashSlashFxW = 5f;            // 剑气渲染宽（格，与判定箱一致）
        private const float DashSlashFxH = 2f;            // 剑气渲染高（格，与判定箱一致）
        private const float DashSlashFxOffY = -0.6f;      // 剑气渲染相对骑士 Y（格，向上为负；-0.3 - 0.3）
        private const float DashSlashFxOffXRight = 5f;    // 朝右冲刺劈砍时剑气渲染 X 偏移（格）
        private const float DashSlashStartSpeed = 0.225f; // 斩击开始时横向速度（格/帧@60，HK 15 换算）
        private const float DashSlashDecayFactor = 0.75f; // 每 0.02s 衰减四分之一
        private const float DashSlashLungeTime = 0.11f;   // 冲刺段时长（秒，总位移约 0.77 格）
        private const float DashSlashTotalTime = 0.45f;   // 冲刺劈砍总时长（秒）
        private const float DashSlashFallSpeed = 0.0135f; // 空中缓慢下落（格/帧@60，0.9 换算）
        private bool _dashSlashPending;   // 冲刺中松开蓄满攻击键，等冲刺结束触发
        private bool _dashSlashing;
        private float _dashSlashTimer;
        private float _dashSlashLungeTimer;
        private float _dashSlashDir;
        private float _dashSlashSpeed;
        private bool _dashSlashDamaged;
        private readonly HashSet<object> _dashSlashHits = new HashSet<object>();
        private readonly HashSet<object> _dashSlashWeedHits = new HashSet<object>(); // 冲刺劈砍破坏魔力草去重
        private string _dashSlashFxSprite;
        private float _dashSlashFxTimer;
        private string[] _dashSlashFxFrames;
        private float _dashSlashFxFps;
        private MeshDrawer _dashSlashFxMesh;
        private M2RenderTicket _dashSlashFxTicket;
        private Material _dashSlashFxMat;
        // ---- 骨钉技艺·旋风劈砍（Cyclone Slash）----
        private const int CycloneSlashDamage = 40;           // 单次伤害
        private const float CycloneSlashHitboxW = 8f;        // 判定箱宽（格，左右各 4）
        private const float CycloneSlashHitboxH = 2f;        // 判定箱高（格）
        private const float CycloneSlashHitboxOffY = 0.5f;   // 判定箱中心相对骑士 Y（格，向下）
        private const float CycloneFxOffX = 4f;              // 剑气渲染相对骑士 X（格）
        private const float CycloneFxOffY = -0.8f;           // 剑气渲染在判定箱基准上再向上（格；-0.5 - 0.3）
        private const float CycloneBaseTime = 0.45f;         // 基础时长（秒）
        private const float CycloneExtendTime = 0.25f;       // 每次延长（秒）
        private const int CycloneMaxExtend = 3;              // 最多延长次数
        private const float CycloneMoveSpeed = 0.09f;        // 旋转中慢速移动（格/帧@60，HK 6 换算）
        private const float CycloneSlowFallMax = 0.0735f;    // 按攻击键后短暂下落限速（格/帧@60，HK 4.9 换算）
        private const float CycloneSlowFallTime = 0.2f;      // 限速窗口（秒）
        private bool _cycloneSlashing;
        private float _cycloneTimer;
        private float _cycloneHitTimer;
        private int _cycloneHitCount;
        private int _cycloneHitIndex;
        private int _cycloneExtCount;
        private float _cycloneSlowFallTimer;
       private bool _cycloneSoulGranted;
        private bool _nailArtSoulGranted;   // 蓄力劈砍整段只结算一次命中灵魂
        private readonly HashSet<object> _cycloneTickTargets = new HashSet<object>(); // 旋风劈砍“同一转内”去重（多碰撞体不重复吃同一转）
        private readonly HashSet<object> _cycloneWeedHits = new HashSet<object>(); // 旋风劈砍破坏魔力草去重
        private string _cycloneFxSprite;
        private float _cycloneFxTimer;
        private string[] _cycloneFxFrames;
        private float _cycloneFxFps;
        private MeshDrawer _cycloneFxMesh;
        private M2RenderTicket _cycloneFxTicket;
        private Material _cycloneFxMat;
        // ---- 梦钉（Dream Nail）----
        private const float DreamNailWindupTime = 1.8f;      // 前摇总时长：三等分各 0.6s，播 0000~0005 / 0006~0010 / 0011~0015
        private const float DreamNailWindupTimeDream = 0.683f; // 舞梦者：前摇 0.683s（三段同步加快）
        private const float DreamNailSwingTime = 10f / 24f;  // 抽出时长（nail_slash0016~0025 @24fps）
        private const int DreamNailSoul = 30;                // 命中恢复灵魂
        private const float DreamNailHitboxLen = 2.5f;       // 判定箱长（前方 2.5 格）
        private const float DreamNailHitboxH = 1.5f;         // 判定箱高（格）
        private const float DreamNailHitboxOffY = 0.5f;      // 判定箱中心相对骑士 Y（格，向下）
        // 梦钉命中白色粒子（复用法术白色圆点贴图 _dotTexes[1]）
        private const float DreamHitParticleCountMin = 50f;  // 粒子数量下限
        private const float DreamHitParticleCountMax = 60f;  // 粒子数量上限
        private const float DreamHitParticleSpeedMin = 1f;   // 初始速度下限（格/秒）
        private const float DreamHitParticleSpeedMax = 6f;   // 初始速度上限（格/秒）
        private const float DreamHitParticleSizeMin = 0.1f;  // 半径下限（格）
        private const float DreamHitParticleSizeMax = 0.2f;  // 半径上限（格）
        private const float DreamHitParticleDecelStart = 0.3f; // 0.3s 后开始减速
        private const float DreamHitParticleDecelTime = 0.4f;  // 平滑减速到 0 的时长
        private const float DreamHitParticleFadeOut = 0.25f;   // 速度到 0 后淡出时长
        // 梦钉挥击光尘（前摇 1.2s 未被中断、进入抽出阶段时生成）
        private const float DreamSwingFxW = 2.5f;       // 特效范围长（格，骑士前方，与判定箱一致）
        private const float DreamSwingFxH = 0.5f;       // 特效范围高（格）
        private const float DreamSwingFxLife = 1f;      // 持续时长（秒）
        private const float DreamSwingFxFadeIn = 0.1f;  // 淡入时长（秒）
        private const float DreamSwingFxFadeOut = 0.1f; // 淡出时长（秒）
        private const float DreamSwingFxSize = 0.1f;    // 粒子半径（格）
        private bool _dreamNailing;
        private int _dreamPhase;         // 0=前摇 1=抽出
        private float _dreamNailTimer;
        private readonly HashSet<object> _dreamNailCandidates = new HashSet<object>(); // 抽中候选中怪
        private NelEnemy _dreamClosestEnemy;   // 离骑士最近命中的怪
        private float _dreamClosestDist;
        private bool _dreamNailApplied;        // 本次抽出是否已生效（碰到怪瞬间结算，仅生效一次）
        private bool _dreamHitBugWall;         // 本次抽出是否击中了虫墙（梦语“好饿……”+30 灵魂）
        // 梦钉命中白色粒子（LightDotParticle 复用，分层渲染）
        private readonly List<LightDotParticle> _dreamHitParticles = new List<LightDotParticle>();
        // 梦钉挥击光尘（前摇完成时生成，白色粒子在固定范围内缓慢飘荡）
        private readonly List<LightDotParticle> _dreamSwingParticles = new List<LightDotParticle>();
        private float _dreamSwingCx;  // 光尘限定范围中心 X（生成时固定，世界格）
        private float _dreamSwingCy;  // 光尘限定范围中心 Y（生成时固定，世界格）
        private MeshDrawer _dreamParticleBackMesh;
        private M2RenderTicket _dreamParticleBackTicket;
        private Material _dreamParticleBackMat;
        private MeshDrawer _dreamParticleFrontMesh;
        private M2RenderTicket _dreamParticleFrontTicket;
        private Material _dreamParticleFrontMat;
        // 梦语 UI 状态（由 KnightHudDeco 绘制）
        // 松开跳跃键时的上升速度削减倍率（0.258 → 点按小跳约 0.30 格高）
        private const float JumpCut = 0.258f;
        private const float FallMax = 0.22f;
        private const float SizeX = 0.28f;
        private const float SizeY = 1.2f;
        // 碰撞箱 X：整体左移 0.1 格（负值=左侧）
        private const float CollideX0 = -0.271875f;
        private const float CollideX1 = 0.2325f;
        // 碰撞箱高度（脚底到头顶），约等于贴图高度(1.45)再略收一点，
        // 防止“离天花板还很远就顶头”。旧值是 2*SizeY=2.4，贴图只有约1.45。
        private const float CollideTop = 1.35f;
        // 小骑士碰撞箱（相对实体原点 X,Y；y 向下为正，负值=上方/左侧）。
        // 与脚底物理分开：地面/渲染仍以脚底 Y+SizeY 为准，碰撞箱只负责墙/顶/受击范围。
        // 受击箱整体上移 1 格（y 向下为正；在上一版基础上再上调）
        // 【修复 2026-09-17】原先这对常量相对实体原点 Y 偏高了约 1.15 格
        // （脚底 = Y + SizeY = Y + 1.2，而盒子却是 Y-1.02…Y+0.05，即悬在身体上方），
        // 于是模组自己的撞墙/撞天花板判定（手写物理，NativeBodyMode=false 时使用）
        // 在矮缝里会撞到天花板 → 位移恒为 0（当年就是靠这条排查出“卡窄缝”的）。
        // 这里整体下移 1.15 格，使盒子 = [脚底-1.07 格, 脚底]，跨度仍是 1.07 格，
        // 因此受击箱大小（HurtSizeY = 跨度/2）与宿主对齐逻辑完全不变。
        private const float CollideY0 = 0.13f;
        private const float CollideY1 = 1.2f;
        /// <summary>碰撞箱动态顶部（Y）：乌恩形态时顶部下移 0.8 格，底部保持不动。</summary>
        private float CollideTopY => CollideY0 + _unnCollideShrink;
        // 攻击剑气位置（相对骑士中心，格为单位；正 X 为脸朝向）
        // 1. 贴脸的距离
        private const float SlashFxOffsetX = 0.45f; 
        private const float HitboxOffsetX = 0.45f; // 和上面保持一致！
        // 上劈剑气位置（头顶正中间往上，无水平偏移；Y 下移到 0.25）
        private const float UpSlashFxOffsetX = 0f;
        private const float UpSlashFxOffsetY = 0.25f;
        // 上劈剑气额外缩小倍率（相对普通剑气，视觉更紧凑）
        private const float UpSlashFxScale = 0.7f;
        // 上劈剑气 0001 帧向右微调量（面朝左时向右，面朝右时镜像向左，让打击线与身体居中）
        private const float UpSlashFxShiftX = 0.15f;
        // 攻击判定有效时间（秒）：极短窗口，一闪而过，防止判定框拖着走
        private const float HitboxActiveTime = 0.08f;
        // 普攻（骨钉伤害）：单次伤害、总时长=冷却、动画帧率（基础 0.28s，护符17 快速劈砍 0.21s）
        /// <summary>普攻单次伤害（平砍/上劈/下劈相同）；束缚·骨钉时降为 30。</summary>
        private int SlashDamage => CharmEffects.GgNailBound ? 36 : 50;
        // 挥砍剪辑帧率：8 帧正好播满一整刀的时长（0.4s → 20fps，= HK 原版骨钉挥砍的帧率）。
        // 攻击动画不再乘全局 AnimSpeed，改由 SlashAnimSpeedMultiplier（快速劈砍）单独控制，
        // 这样“两次攻击的间隔”恒等于 SlashAttackTime()，不受动画速度配置影响。
        private const float SlashClipFps = 8f / CharmEffects.SlashTimeBase;
        // 横劈命中后坐力：总后退距离（格）与持续时间（秒），线性衰减平滑推出
        private const float RecoilDistance = 0.8f;
        private const float RecoilTime = 0.15f;
        // 二段跳：先极轻下沉再向上（下沉时间秒）
        private const float DoubleJumpDipTime = 0.05f;
        // 爬墙（螳螂爪）：挂墙恒定下滑速度、蹬墙跳水平初速（对照官方描述：
        // 蹬墙跳≈45°斜上，水平方向以 WallJumpVx 起步并随时间线性衰减；
        // 0.1 秒后玩家输入可取消剩余的向外速度，准备再次贴墙）
        private const float WallSlideSpeed = 0.075f;
        private const float WallJumpVx = 0.22f;         // 蹬墙跳初始水平速度（格/帧@60）
        private const float WallJumpKickTime = 0.22f;   // 水平推力衰减时长（秒）
        private const float WallJumpCancelTime = 0.1f;  // 之后输入可取消剩余向外速度（秒）
        // 抓墙距离阈值（格）：骑士边缘距墙面 < 0.25 才吸附，防远距离磁吸
        private const float WallGripDistance = 0.25f;
        // 二段跳白色光翼持续时长（秒）：急速展开并消散
        private const float WingFxTime = 0.2f;
        // 羽翼整体缩放系数（大幅缩小，避免与骑士本体一样大）
        private const float WingScale = 0.6f;
        // 二段跳光点程序生成形状数量（弯月/圆点/长条）
        private const int DotShapeCount = 3;
        // 二段跳散射光点：翅膀特效窗口（0.2 秒）内的生成间隔与生命周期（秒）
        private const float LightDotSpawnInterval = 0.016f; // 0.2s / 0.016 ≈ 12.5 个
        private const float LightDotLifeMin = 2.0f;
        private const float LightDotLifeMax = 3.0f;
        // 暗影冲刺黑色粒子：生成间隔（0.25s 冲刺 ≈ 20 个）与整体淡出时长
        private const float ShadowDotSpawnInterval = 0.0125f;
        // 冲刺结束后先保持 0.5 秒，再开始按序销毁粒子
        private const float ShadowDotFadeDelay = 0.5f;
        private const float ShadowDotFadeTime = 0.3f;
        private const float ShadowDotLifeMin = 0.2f;
        private const float ShadowDotLifeMax = 0.4f;
        // 判定形状：底部长方形 + 前方三角形尖端（尖端长度）
        private const float AttackTipLen = 0.5f;
        // 下劈剑气位置（骑士正下方，与下劈判定框对齐）
        private const float DownSlashFxOffsetX = 0f;
        private const float DownSlashFxOffsetY = -0.9f;
        // 下劈剑气额外缩小倍率（相对普通剑气，视觉更紧凑）
        private const float DownSlashFxScale = 0.6f;

        // 2. 巨大的尺寸
        private const float HitboxSizeX = 2.1f; // 拉长
        private const float HitboxSizeY = 1.4f; // 拔高

        // 3. 贴地的高度（视觉和判定对齐）
        private const float SlashFxOffsetY = -0.25f;
        private const float HitboxOffsetY = -0.24f; // 判定略低于视觉，能砍到脚边的怪
        // 剑气单独放大倍数（只放大剑气，骑士本体不受影响）
        private const float SlashFxScale = 1.5f;
        // ---- 修长之钉/骄傲印记（螳螂爪样式剑气，M 版剪辑）的渲染微调（单位：格，均为累计值）----
        // 横劈：向身体侧收缩 0.2 格（骑士侧边缘不动 → 中心回移 0.1 格），
        // 上下各拉伸 0.1+0.1=0.2 格（左右普攻对称，与朝向无关）
        private const float MantisSlashPullBack = 0.2f;
        private const float MantisSlashGrowY = 0.2f;
        // 默认横劈剑气（slashes_effect0001~0002 帧）：朝攻击方向再拉伸 0.2 格
        // （该侧边缘外扩、另一侧不动 → 中心朝攻击方向移 0.1 格；面朝右自动镜像）
        private const float SlashEffectFrontGrow = 0.2f;
        // 上劈（累计：向下 0.5 → +向下 0.1/向上 0.2/左右 0.2 → +向下 0.3/左右 0.2）
        // 向上 0.2 格、向下 0.9 格、左右各 0.5 格；中心位移 = (上-下)/2 = 下移 0.35 格
        private const float MantisUpSlashStretchUp = 0.2f;
        private const float MantisUpSlashStretchDown = 0.9f;
        private const float MantisUpSlashGrowX = 0.5f;
        // 下劈（累计值：第一轮 向上 0.5 / 左右 0.1，第二轮再叠加 向下 0.2、左右 0.2）
        // 向上 0.5 格、向下 0.2 格、左右各 0.3 格；中心位移 = (上-下)/2 = 上移 0.15 格
        private const float MantisDownSlashStretchUp = 0.5f;
        private const float MantisDownSlashStretchDown = 0.2f;
        private const float MantisDownSlashGrowX = 0.3f;
        // 单帧额外微调（螳螂爪样式；均以“面朝左”定义，面朝右自动镜像）
        // 下劈 mantis_down_slash0002：整体向右平移 0.5 格
        private const float MantisDownSlash0002ShiftX = 0.5f;
        // 上劈 mantis_up_slash0001：整体竖直平移（正值=上移）
        // 累计 上0.3 + 上0.3 − 下1.0 + 上0.2 = 净下移 0.2 格
        private const float MantisUpSlash0001ShiftY = -0.2f;
        // 横向边缘外扩量（格，正=该侧向外长；两侧分开记，避免以后叠加时算反）
        // 面朝左时在“右”侧：+0.5 向右拉伸 − 0.2 向左收缩 = 净 +0.3
        private const float MantisUpSlash0001GrowRight = 0.3f;
        // 面朝左时在“左”侧：+0.1 向左拉伸
        private const float MantisUpSlash0001GrowLeft = 0.1f;
        // 横向纯平移（不改变宽度；正值 = 面朝左时向右，面朝右自动镜像）：累计 向右 0.2 格
        private const float MantisUpSlash0001ShiftX = 0.2f;
        // ---- 修长之钉/骄傲印记：上/下劈【判定框】额外拉伸（格，叠加在护符原有拉伸之上）----
        // 注意判定框是“矩形 + 三角形”，三角形底边恒等于矩形宽度，所以改矩形宽度即可带动底边。
        // 上劈：矩形向下 0.5 格（上边缘/三角形底边不动 → 中心下移 0.25），左右各 0.5 格
        private const float MantisUpSlashHitboxDown = 0.5f;
        private const float MantisUpSlashHitboxWiden = 0.5f;
        // 下劈：矩形向上 0.2 格（下边缘/三角形底边不动 → 中心上移 0.1），左右各 0.2 格
        private const float MantisDownSlashHitboxUp = 0.2f;
        private const float MantisDownSlashHitboxWiden = 0.2f;
        // ---- 不佩戴修长之钉/骄傲印记时的上/下劈额外拉伸（格，叠加）----
        // 上劈：判定框矩形向下 0.5 格（上边缘/三角形底边不动 → 中心下移 0.25）；渲染向下 0.8 格（中心下移 0.4）
        private const float PlainUpSlashHitboxDown = 0.5f;
        private const float PlainUpSlashFxDown = 0.8f;
        // 下劈：判定框矩形向上 0.5 格（下边缘/三角形底边不动 → 中心上移 0.25）；渲染向上 0.8 格（中心上移 0.4）
        private const float PlainDownSlashHitboxUp = 0.5f;
        private const float PlainDownSlashFxUp = 0.8f;
        // 上劈：矩形顶端再向下收缩 0.5 格（底端与三角形不动 → 高度 -0.5、中心下移 0.25）；矩形左右各 0.2 格、渲染左右各 0.2 格
        private const float PlainUpSlashTopShrink = 0.5f;
        private const float PlainUpSlashHitboxWidenX = 0.2f;
        private const float PlainUpSlashFxWidenX = 0.2f;
        // 下劈：矩形底端再向上收缩 0.5 格（顶端与三角形不动 → 高度 -0.5、中心上移 0.25）；矩形左右各 0.2 格、渲染左右各 0.2 格
        private const float PlainDownSlashBottomShrink = 0.5f;
        private const float PlainDownSlashHitboxWidenX = 0.2f;
        private const float PlainDownSlashFxWidenX = 0.2f;
        // 空中攻击（下劈）：判定框在骑士正下方
        private const float DownSlashOffsetX = 0f;
        private const float DownSlashOffsetY = -0.8f;
        private const float DownSlashSizeX = 1.4f;
        private const float DownSlashSizeY = 2.1f;
        // 下劈碰撞/渲染“向上拉伸”量（格）：只拉上边缘，下边缘不动，中心上移拉伸量的一半
        private const float DownSlashStretchUp = 0.4f;
        // 上劈：判定框在骑士正上方（水平居中，不受面朝方向影响）
        private const float UpSlashOffsetX = 0f;
        private const float UpSlashOffsetY = 0.1f;
        private const float UpSlashSizeX = 1.4f;
        private const float UpSlashSizeY = 2.1f;

        private sealed class ClipData
        {
            public float fps;
            public int wrapMode;
            public int loopStart;
            public string[] frames;
        }

        /// <summary>虚空触手帧数据：四个朝向的旋转变体（0=底 1=顶 2=左 3=右）。</summary>
        private sealed class TentacleFrameData
        {
            public string Name;
            public TentacleVariantData[] Variants = new TentacleVariantData[4];
        }

        /// <summary>虚空解放划痕目标：跟随目标移动，出伤结束前持续渲染。</summary>
        private sealed class VoidSlashTarget
        {
            public NelEnemy Enemy;
            public Vector3 Offset; // 命中时目标中心相对 transform.position 的偏移
        }

        /// <summary>某一朝向变体的绘制数据：内容沿边缘方向铺满槽位，边缘侧贴屏幕边。</summary>
        private sealed class TentacleVariantData
        {
            public Texture2D Tex;
            public int W;
            public int H;
            public int ContentAlong;  // 沿屏幕边缘方向的内容尺寸（像素）
            public float EdgeGapFrac; // 内容边缘侧到图片边缘侧的距离比例
        }

        /// <summary>虚空触手某一帧的绘制信息（OnGUI 用，按帧缩放并贴边）。</summary>
        public struct VoidTentacleFrameInfo
        {
            public Texture2D Tex;
            public float W;
            public float H;
            public float ContentAlong;
            public float EdgeGapFrac;
        }

        private sealed class TrailGhost
        {
            public float X;
            public float Y;
            public int Frame;
            public int Index; // 生成顺序（0=第一帧），用于整条拖尾按顺序淡出
            public float Age; // 生成后经过时间（用于淡入，避免残影生硬弹出）
        }

        private sealed class GhostNode
        {
            public MeshDrawer Mesh;
            public M2RenderTicket Ticket;
            public TrailGhost Ghost;
            public Material Mat;
        }

        private sealed class LightDotParticle
        {
            public float X;
            public float Y;
            public float Vx;
            public float Vy;
            public int TexIndex;
            public float Age;
            public float Life;
            public float Size;
            public Color32 Color;
            public int Index; // 生成顺序（暗影冲刺粒子按顺序淡出用）
            public int Layer;  // 特效分层：0=骑士身后，1=骑士身前
            public float TravelDist; // 已移动距离（格，用于中程大幅减速）
            public bool Braked;      // 是否已触发大幅减速
            public float BrakeDist;  // 触发减速的距离阈值（格）
            public float TargetX;    // 蓄力粒子收敛目标 X（格）
            public float TargetY;    // 蓄力粒子收敛目标 Y（格）
            public float Speed;      // 蓄力粒子向目标的速度（格/秒）
        }

        private const float GhostLife = 0.05f;
        // 残影淡入时间：生成时快速淡入，避免硬弹出/卡顿感
        private const float GhostFadeInTime = 0.03f;
        private const int GhostNodeCount = 14;
        // 拖尾帧放大系数：从第3帧(0002)起每帧比上一帧放大 12%
        private const float GhostFrameGrowth = 0.12f;
        // 拖尾叠层数：特效本身微弱，叠 3 层提升可见度
        private const int GhostLayers = 3;
        // 方案A（单残影，固定帧率播放）：整条拖尾最多只存在 1 帧残影。
        // 帧的切换不按骑士前进距离，而是固定时间间隔推进；
        // 每帧相对冲刺起点固定向后移 GhostFrameSpacing 格（0000/0001 同在起点）。
        private const float GhostFrameInterval = 0.05f;
        private const float GhostFrameSpacing = 1.0f;
        // 0004/0005 两帧整体上移 0.5 格（原位置偏下；游戏坐标中 Y 减小 = 向上）
        private const float GhostRaiseY = 0.5f;

        private void Awake()
        {
            Instance = this;
            LoadAssets();
        }

        private void OnDestroy()
        {
            ReleaseTicket();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public static void SpawnAt(PRNoel noel)
        {
            if (Instance == null)
            {
                var go = new GameObject("KnightEntity");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<KnightEntity>();
            }

            if (noel != null)
            {
                Instance.X = noel.x;
                // 先按诺艾尔脚底放，随后贴地修正
                Instance.Y = noel.mbottom - SizeY;
                Instance.Vx = 0f;
                Instance.Vy = 0f;
                Instance.Grounded = false;
                Instance._mp = noel.Mp;
                Instance._active = true;
                Instance._taunting = false; // 切人时清除挑衅状态
                Instance._tauntTimer = 0f;
                // 生成时直接吸附到游戏地面检测的准确位置
                if (Instance.CheckGround(out float gt, Instance.Y + SizeY))
                {
                    Instance.Y = gt - SizeY;
                    Instance.Grounded = true;
                }
                Instance._lastSafeX = Instance.X;
                Instance._lastSafeY = Instance.Y;
                // 记录初始复活点（仅第一次）：进入游戏时诺艾尔所在的长椅/位置，
                // 之后只有“坐上长椅”才会更新复活点
                if (!Instance._initialRespawnRecorded)
                {
                    Instance._initialRespawnRecorded = true;
                    Instance._hasRespawn = true;
                    Instance._respawnX = Instance.X;
                    Instance._respawnY = Instance.Y;
                    Instance._respawnMp = Instance._mp;
                }
                // 读档/新游戏：刷新复活点为当前存档出生位置，
                // 防止残留上一存档坐过的长椅（死亡回到错误位置）
                if (JustLoadedSave)
                {
                    Instance._hasRespawn = true;
                    Instance._respawnX = Instance.X;
                    Instance._respawnY = Instance.Y;
                    Instance._respawnMp = Instance._mp;
                }
                // 切回小骑士（同房间换人）时保留仍在飞的吸虫；读档/新游戏则照旧清掉
                Instance.RebindTicket(keepFlukes: !JustLoadedSave);
                PendingLoadRebind = false; // 重绑成功：清除读档待重绑标记
                PendingFastTravelRebind = false; // 重绑成功：清除快速旅行待重绑标记
                // 读档后首次生成：血量/灵魂回满
                if (ResetOnLoad)
                {
                    ResetOnLoad = false;
                    Instance.FillHealthSoul();
                }
                // 重新生成（切回小骑士）时清除读档对齐标记，避免残留到下次换图
                JustLoadedSave = false;

                // 骑士模式复用诺艾尔原版 HUD：确保 UIStatus 已重新激活（切出骑士时被 SetActive(false) 藏过）
                try
                {
                    GameObject.Find("UIStatus")?.SetActive(true);
                    if (UIStatus.Instance != null && UIStatus.Instance.GetGob() != null)
                    {
                        UIStatus.Instance.GetGob().SetActive(true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public static void Deactivate()
        {
            if (Instance != null)
            {
                Instance._attacking = false;
                Instance._swingHits.Clear();
                Instance._fxTex = null;
                Instance._canDoubleJump = true;
                Instance._canDash = true;
                Instance._doubleJumping = false;
                Instance._onWall = false;
                Instance._wallJumping = false;
                Instance._wallKickTimer = 0f;
                Instance._wallKickVx = 0f;
                Instance._wallJumpDrift = false;
                Instance._dashJustEnded = false;
                Instance._dashExitVx = 0f;
                Instance._bugWallHits.Clear();
                Instance._bugWallCellHits.Clear();
                Instance._bugWallLpHits.Clear();
                Instance._wingTimer = 0f;
                Instance._dots.Clear();
                Instance._shadowParticles.Clear();
                Instance._shadowDotFading = false;
                Instance.DestroyHitbox();
                Instance._nailParryFreezeTimer = 0f; // 切回诺艾尔：清拼刀冻结与闩锁，避免残留
                Instance._nailParryLatched = false;
                Instance._nailParrySelfRects.Clear();
                Instance._nailParrySelfKinds.Clear();
                // 切回诺艾尔：吸虫之巢放出的吸虫**不消失**——保留它们的地图引用、
                // 渲染票据与列表，由 KnightInCradleBehaviour → TickOrphanFlukes 继续推进/绘制，
                // 直到各自寿命结束（或死亡/换图时随 ReleaseTicket 一起清掉）。
                Instance._flukeMp = Instance._mp;
                Instance.ReleaseTicket(keepFlukeList: true, keepFlukeTicket: true);
                Instance._mp = null;
                Instance._fastTravelInProgress = false;
                Instance._fastTravelWaitFrames = 0;
                Instance._fastTravelArrivedTimer = -1f;
                Instance._fastTravelNoBenchTimer = -1f;
                Instance._fastTravelPrevTransferring = false;
                Instance._fastTravelMapChanged = false;
                CharmEffects.BattleAreaFastTravel = false;
                Instance._currentTex = null;
                Instance._rcTex = null;
                Instance._shadowRechargeTimer = 0f;
                Instance._ghosts.Clear();
                Instance._ghostFading = false;
                Instance._sdRings.Clear();
                Instance._fireballCasting = false;
                Instance._fireballs.Clear();
                Instance._fireballBlastFrames = null;
                Instance._fireballBlastSprite = null;
                Instance._screaming = false;
                Instance.DestroyScreamHitbox();
                Instance._screamBlastFrames = null;
                Instance._screamBlastSprite = null;
                Instance._screamParticles.Clear();
                Instance._diving = false;
                Instance._swimState = 0; // 切回诺艾尔：游泳状态复位
                Instance.ResetNailArt(); // 切回诺艾尔：骨钉技艺复位
                Instance.ResetDreamNail(); // 切回诺艾尔：梦钉复位
                DashAudio.StopDiveLoop(); // 切回诺艾尔：停止下砸循环音
                Instance._diveSwords.Clear();
                Instance._diveSpikes.Clear();
                Instance._diveLandParticles.Clear();
                // 受伤/死亡状态复位，避免切回诺艾尔后残留硬直/黑屏/时间冻结
                Instance._hurt = false;
                Instance._hurtFreezeTimer = 0f;
                Instance._hurtFlyTimer = 0f;
                Instance._invincibleTimer = 0f;
                Instance._focusing = false;
                Instance._focusPhase = 0;
                Instance._focusSoulDrained = 0f;
                Instance.CancelHitStop(); // 解除可能残留的受伤画面停滞
                Instance._isDead = false;
                Instance._respawnFadeOut = false;
                Instance._deathBlackAlpha = 0f;
                Instance._deathBlackPhase = 0f;
                Instance._hitVignetteAlpha = 0f;
                Instance._focusFlashAlpha = 0f;
                Instance._melodyFlashAlpha = 0f;
                Instance._focusParticles.Clear();
                Instance._focusFxSpawnTimer = 0f;
                if (Instance._focusChargePlaying)
                {
                    DashAudio.StopFocusCharge();
                    Instance._focusChargePlaying = false;
                }
                DashAudio.StopFocusHeal();
                Instance.StopSuperDashAll();
                Instance._lowHpParticles.Clear();
                Instance._hitFxParticles.Clear();
                // 切回诺艾尔：虚空解放状态复位并立即恢复镜头缩放（防止残留拉近）
                Instance._voidPhase = 0;
                Instance._voidTimer = 0f;
                Instance._voidExitTimer = 0f;
                Instance._voidFlashAlpha = 0f;
                Instance._voidZoomRestoreTimer = 0f;
                Instance._voidTentacleActive = false;
                Instance._voidSlashTargets.Clear();
                Instance._voidChargeHurtDone = false;
                Instance._nailArtHitEnemies.Clear();
                try
                {
                    M2DBase m2d = M2DBase.Instance;
                    if (m2d != null && m2d.Cam != null)
                    {
                        m2d.Cam.animateScaleTo(Instance._voidZoomBase, 0);
                    }
                }
                catch (Exception)
                {
                }
                Instance._active = false;
            }
        }

        private void Update()
        {
            if (!_assetsLoaded || !_active)
            {
                return;
            }
            // 寻神者金色闪光渐出：放在护符界面/菜单提前 return 之前，
            // 保证 UI 打开期间也会短时闪烁后消失（同回血闪光）。
            if (_ggGoldFlashAlpha > 0f)
            {
                _ggGoldFlashAlpha = Mathf.Max(0f, _ggGoldFlashAlpha - Time.unscaledDeltaTime / FocusFlashTime);
            }
            // 护符34 乌恩之形：凝聚中碰撞箱顶部下移 0.8 格（蛞蝓形态）
            _unnCollideShrink = (_focusing && CharmEffects.IsEquipped(CharmEffects.UnnId))
                ? UnnCollideShrink
                : 0f;
            // 护符2 蜂群集结：保护落地魔力不被超时改成“谁都能吸”；蜂巢房间魔物中立（每帧清除目标）
            // 护符9 幼虫之歌：蚂蟥/女王蚂蟥中立（每帧清除目标）
            CharmEffects.ProtectCollectorMana();
            // 护符2 蜂群集结：自动拾取 3 格内已落地、且背包放得下的掉落物
            CharmEffects.TickCollectorAutoPickup();
            CharmEffects.ClearHiveEnemyAim();
            CharmEffects.ClearGrubsongLeechAim();
            // 护符22 巴尔德之壳：入战/战斗结束刷新抵挡次数
            if (CharmEffects.IsEquipped(CharmEffects.BaldurId))
            {
                bool inBattle = false;
                try
                {
                    inBattle = EnemySummoner.isActiveBorder();
                }
                catch (Exception)
                {
                }
                if (inBattle != _baldurInBattle)
                {
                    _baldurInBattle = inBattle;
                    _baldurBlocksLeft = BaldurMaxBlocks;
                    if (!inBattle)
                    {
                        EndBaldurShell();
                    }
                }
            }
            // 巴尔德之壳动画推进
            UpdateBaldurShellFx(Time.deltaTime);
            // 回血结束动画推进（无硬直，播完回普通状态）
            UpdateFocusEndAnim(Time.deltaTime);
            // 护符24 防御者纹章：法阵推进（放在坐椅/回血 goto 之前，保证坐椅/回血时球体仍公转）
            UpdateShelterMagic(Time.deltaTime);
            // 护符25 发光子宫：幼体生成/索敌飞行/碰撞
            try
            {
                UpdateUterus(Time.deltaTime);
            }
            catch (Exception)
            {
                // 过图/地图销毁瞬间的异常在这里兜底，不让它从 Update 抛出导致游戏崩溃
            }
            // 护符31 蜂巢之血：每 10 秒回复 1 血量
            UpdateHiveHeal(Time.deltaTime);
            // 生命血羁绊：每 7 秒回复 1 血量（可与蜂巢之血叠加）
            UpdateLifebloodHeal(Time.deltaTime);
            // 护符41 国王之魂：每 2 秒恢复 4 点灵魂
            UpdateKingsoulSoul(Time.deltaTime);
            // 护符32 蘑菇孢子：孢子云推进与伤害
            UpdateSporeClouds(Time.deltaTime);
            PrunePvpAggroTargets();
            // 护符36 编织者之歌：小编织者跟随/攻击
            UpdateWeaverlings(Time.deltaTime);
            // 护符38 梦之盾：公转/接触伤害/挡弹/回血变速
            UpdateDreamShield(Time.deltaTime);
            // 护符39 格林之子：小格林跟随
            UpdateGrimm(Time.deltaTime);
            // 游戏菜单（ESC 暂停）打开期间：禁用小骑士全部输入与物理
            if (IsGameMenuOpen())
            {
                return;
            }
            // 护符界面打开期间：同样禁用小骑士全部输入与物理（移动/跳跃/攻击/技能/冲刺）
            if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
            {
                return;
            }
            // ---- 冲刺/蓄力安全看门狗：任何情况下都不允许冲刺状态永久卡死 ----
            if (_superCharging)
            {
                _superDashAliveTimer += Time.deltaTime;
                if (_superDashAliveTimer > SuperDashWatchdogTime)
                {
                    StopSuperDashAll();
                }
            }
            else
            {
                _superDashAliveTimer = 0f;
            }
            // 状态卡死硬恢复：冲刺/蓄力/下砸等锁定状态位置长时间不动（且不在重定位/传送中）
            // 时，强制解除状态并回传最后安全点。不排除事件——事件冻结有 2.5s 上限，
            // 超过后状态仍未恢复推进就说明真的卡死了（如转房事件残留导致游戏卡住）。
            if ((_superDashing || _superCharging || _diving) &&
                _pendingReposition <= 0 && !_transitionPause)
            {
                float stuckDist = Mathf.Abs(X - _superDashLastX) + Mathf.Abs(Y - _superDashLastY);
                if (stuckDist < 0.05f)
                {
                    _superDashStuckTimer += Time.deltaTime;
                    if (_superDashStuckTimer > 3f)
                    {
                        _diving = false;
                        _divePhase = 0;
                        _diveTimer = 0f;
                        _diveHardlockTimer = 0f;
                        _diveInvulnTimer = 0f;
                        _invincibleTimer = 0f;
                        DashAudio.StopDiveLoop();
                        StopSuperDashAll();
                        X = _lastSafeX;
                        Y = _lastSafeY;
                        Vx = 0f;
                        Vy = 0f;
                        Grounded = true;
                        _currentClip = null;
                    }
                }
                else
                {
                    _superDashStuckTimer = 0f;
                }
                _superDashLastX = X;
                _superDashLastY = Y;
            }
            else
            {
                _superDashStuckTimer = 0f;
            }
            // 重定位看门狗：重定位阶段卡住超过 5s 强制走完成分支，防止骑士被永久冻结
            if (_pendingReposition > 0)
            {
                _pendingRepositionStuckTimer += Time.deltaTime;
                if (_pendingRepositionStuckTimer > 5f)
                {
                    _pendingReposition = 1;
                }
            }
            else
            {
                _pendingRepositionStuckTimer = 0f;
            }
            // 转房事件残留兜底：游戏 runUi 的出口检测在 EV.isActive 时会被整体跳过
            // （`if (Map2d.can_handle && !EV.isActive(false))`），
            // 所以转房事件若残留不结束，小骑士就永远无法触发新出口。
            // 重定位结束后，若 Exit 的 STAND 转房事件残留超过 1 秒，强制结束它。
            if (_pendingReposition <= 0 && !_transitionPause && EV.isActive(false))
            {
                string lingerEvName = "";
                try
                {
                    evt.EvReader lingerEv = EV.getCurrentEvent();
                    if (lingerEv != null)
                    {
                        lingerEvName = lingerEv.name;
                    }
                }
                catch (Exception)
                {
                }
                if (lingerEvName.Contains("Exit") && lingerEvName.Contains("STAND"))
                {
                    _lingeringEvTimer += Time.deltaTime;
                    if (_lingeringEvTimer > 1f)
                    {
                        try
                        {
                            EV.evEnd(true);
                        }
                        catch (Exception)
                        {
                        }
                        ClearNoelSimKeys();
                        _lingeringEvTimer = 0f;
                    }
                }
                else
                {
                    _lingeringEvTimer = 0f;
                }
            }
            else
            {
                _lingeringEvTimer = 0f;
            }
            // 掉出地图兜底（无论任何状态）：脚底低于地图底部 1 格以上时，回到最后安全点。
            // 放在 Update 最前面，保证冲刺/下砸等跳过物理的状态也能被拉回来。
            // 转房事件/传送期间除外（那些阶段由重定位负责把骑士带回新房间入口）。
            if (_mp != null && Y + SizeY > _mp.rows + 1f &&
                _pendingReposition <= 0 && !_transitionPause &&
                !(M2DBase.Instance != null && M2DBase.Instance.transferring_game_stopping) &&
                !EV.isActive(false))
            {
                _diving = false;
                _divePhase = 0;
                _diveTimer = 0f;
                _diveHardlockTimer = 0f;
                _diveInvulnTimer = 0f;
                _invincibleTimer = 0f;
                DashAudio.StopDiveLoop();
                StopSuperDashAll();
                X = _lastSafeX;
                Y = _lastSafeY;
                Vx = 0f;
                Vy = 0f;
                Grounded = true;
                _currentClip = null;
            }
            // 诺艾尔魔力条变成“无穷”（数值极大）时，小骑士灵魂条同步为无穷：
            // 灵魂保持满、施法不消耗、HUD 显示无穷
            try
            {
                PRNoel pr = GetPr();
                // AIC 的“魔力无穷”：谜题魔力管理中 puzz_magic_count_max == -1
                // （此时原版 MP 条显示 ∞，且魔法不耗蓝）；另保留数值极大兜底
                bool puzzleInf = false;
                if (pr != null)
                {
                    try
                    {
                        puzzleInf = pr.isPuzzleManagingMp() &&
                            PUZ.IT != null && PUZ.IT.puzz_magic_count_max == -1;
                    }
                    catch (Exception)
                    {
                    }
                }
                // 注意：骑士模式下 CombatGuard 的 GetMaxMpPrefix 会把 pr.get_maxmp() 拦截成
                // 小骑士灵魂值，导致“无穷”标志一旦为 true 就自我维持、退出谜题后无法恢复。
                // 这里直接反射读取诺艾尔真实 maxmp/mp 字段来判断。
                float realMaxMp = 0f;
                float realMp = 0f;
                if (pr != null)
                {
                    try
                    {
                        var fMaxMp = AccessTools.Field(typeof(M2Attackable), "maxmp");
                        var fMp = AccessTools.Field(typeof(M2Attackable), "mp");
                        if (fMaxMp != null)
                        {
                            realMaxMp = (float)(int)fMaxMp.GetValue(pr);
                        }
                        if (fMp != null)
                        {
                            realMp = (float)(int)fMp.GetValue(pr);
                        }
                    }
                    catch (Exception)
                    {
                        realMaxMp = pr.get_maxmp();
                        realMp = pr.get_mp();
                    }
                }
                _soulInfinite = puzzleInf ||
                    (pr != null && (realMaxMp >= 100000f || realMp >= 100000f));
            }
            catch (Exception)
            {
                _soulInfinite = false;
            }
            if (_soulInfinite)
            {
                _soul = _maxSoul;
            }
            ApplyDarkAreaBrightness(); // 黑暗区域：整个房间调亮（提灯效果）
            ApplyFogWeatherVisibility(); // 雾天：全屏雾层不透明度归零（小骑士不遮蔽视线）
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                DebugDumpRoomInfo();
            }

            // 下砸过图强制位移测量：从换图起计时，模拟键（方向键位）结束后 +10% 作为免落地窗口
            if (_diveNoLand < 0f)
            {
                _diveNoLandMeasure += Time.deltaTime;
                bool simActive = false;
                try
                {
                    PRNoel prSim = GetPr();
                    if (prSim != null && SimKeyField != null)
                    {
                        object v = SimKeyField.GetValue(prSim);
                        uint sk = 0;
                        if (v is uint u)
                        {
                            sk = u;
                        }
                        else if (v is int i)
                        {
                            sk = (uint)i;
                        }
                        simActive = (sk & 15U) != 0U; // 方向键位 L/R/T/B
                    }
                }
                catch (Exception)
                {
                }
                if (simActive)
                {
                    _diveSimWasActive = true;
                }
                else if (_diveSimWasActive)
                {
                    // 位移结束：免落地（跳过单向板）到此为止，之后恢复正常判定
                    _diveNoLand = 0f;
                    _diveSimWasActive = false;
                }
                else if (_diveNoLandMeasure > 1.5f)
                {
                    // 兜底：模拟键一直没出现（非 walk-in 传送），位移很短，直接恢复
                    _diveNoLand = 0f;
                }
            }

            // 地图切换时重新绑定
            M2DBase m2d = M2DBase.Instance;
            Map2d cur = m2d != null ? m2d.curMap : null;
            bool transferring = m2d != null && m2d.transferring_game_stopping;
            // 快速旅行到达：地图加载完成（transferring 由 true→false，画面开始恢复）时
            // 重播 deco 淡入；同图传送没有 transferring 翻转，由重定位结束兜底触发。
            // 普通换房也会经过这里，但 KnightHudDeco 内部以 _fastTraveling 为门槛，不会误触发。
            if (!transferring && _prevTransferring)
            {
                KnightHudDeco.OnFastTravelArrive();
            }
            _prevTransferring = transferring;
            if (cur != null && _mp != cur)
            {
                Map2d oldMp = _mp;
                _mp = cur;
                if (_fastTravelInProgress)
                {
                    _fastTravelMapChanged = true;
                }
                // 调试：检测小骑士进入的房间（每进入一个房间打印一次；
                // F8 可在任意时刻打印当前房间）
                try
                {
                    CharmEffects.UpdateHiveRoom(cur);
                    string sub = cur.SubMapData != null ? cur.SubMapData.key : "无";
                    PRNoel prDbg = GetPr();
                    KnightInCradlePlugin.PluginLog.LogInfo(
                        "[房间] 进入 key=" + cur.key + " submap=" + sub +
                        " 尺寸=" + cur.clms + "x" + cur.rows +
                        " 骑士=(" + X.ToString("F2") + "," + Y.ToString("F2") + ")" +
                        " 诺艾尔=(" + (prDbg != null ? prDbg.x.ToString("F2") : "?") + "," +
                        (prDbg != null ? prDbg.y.ToString("F2") : "?") + ")" +
                        " 蜂巢=" + (CharmEffects.CurrentRoomIsHive ? "是" : "否"));
                }
                catch (Exception)
                {
                }
                _pendingRepositionActive = true;
                // 换图后视为未落地：第一帧重定位必须镜像诺艾尔位置（旧地图的 Grounded 已失效）
                Grounded = false;
                // 判断横向/纵向过图：骑士更靠近左右边界视为横向（用旧地图坐标判断）
                _horizontalTransfer = false;
                if (oldMp != null)
                {
                    float dL = X;
                    float dR = oldMp.width - X;
                    float dT = Y + SizeY - CollideTop;
                    float dB = oldMp.rows - (Y + SizeY);
                    _horizontalTransfer = Mathf.Min(dL, dR) <= Mathf.Min(dT, dB);
                }
                // 过图时下砸延续判断：必须先于 RebindTicket（它内部 ReleaseTicket 会清 _diving）
                bool diveTransferContinue = false;
                if (_diving && _divePhase == 1 && oldMp != null)
                {
                    float dTD = Y + SizeY - CollideTop;
                    float dBD = oldMp.rows - (Y + SizeY);
                    diveTransferContinue = dBD < dTD;
                }
                // 过图/传送：清理发光子宫幼体，并按清理数量返还灵魂（每只 8 点）。
                // 必须放在 RebindTicket 之前——它内部 ReleaseTicket 会先清掉幼体，
                // 若在此之后清点数量就永远为 0，无法返还。
                if (_uterusHatchlings.Count > 0)
                {
                    KnightAddSoul(_uterusHatchlings.Count * UterusSpawnSoul);
                }
                _uterusHatchlings.Clear();
                _uterusExplosions.Clear(); // 旧地图坐标的爆炸特效一并清除
                _shelterCircleEntered.Clear(); // 过图：旧地图敌人引用失效，清空领域进入判定
                _shelterCircleEnteredPlayers.Clear();
                // 读档/换图重绑渲染票据：失败不中断本帧（后续由 PendingLoadRebind 重试）
                try
                {
                    RebindTicket();
                }
                catch (Exception)
                {
                }
                if (diveTransferContinue)
                {
                    // RebindTicket 清了 _diving/_divePhase；延续下砸时恢复，
                    // 重定位结束后在下一房间继续下落（跨房间延续下砸）
                    _diving = true;
                    _divePhase = 1;
                    _diveTimer = 0f;
                    _diveHardlockTimer = 0f;
                    _diveInvulnTimer = 0f;
                    _invincibleTimer = 0f;
                    // 开始测量强制位移：从换图起计时，模拟键结束 +10% 作为免落地窗口
                    _diveNoLand = -1f;
                    _diveNoLandMeasure = 0f;
                    _diveSimWasActive = false;
                }
                _transitionPause = true;
                // 过图时中止攻击（判定框/剑气一并清掉）
                _attacking = false;
                _swingHits.Clear();
                DestroyHitbox();
                _fxTex = null;
                // 过图时停止冲刺，避免进入下张图后继续冲刺
                _dashing = false;
                _dashTimer = 0f;
                _dashJustEnded = false;
                _dashExitVx = 0f;
                _canDash = true; // 过图后重置冲刺资格（重定位不走正常落地流程）
                _bugWallHits.Clear(); // 虫墙由引擎按房间配置重新生成，清掉旧墙计数
                _bugWallCellHits.Clear();
                _bugWallLpHits.Clear();
                _rcTex = null;
                _shadowRechargeTimer = 0f;
                _ghosts.Clear();
                _ghostFading = false;
                _shadowParticles.Clear();
                _shadowDotFading = false;
                _focusParticles.Clear();
                // 过图时移除暗影之魂冲击波与施法状态
                _fireballCasting = false;
                _fireballs.Clear();
                _fireballBlastFrames = null;
                _fireballBlastSprite = null;
                _screaming = false;
                DestroyScreamHitbox();
                _screamBlastFrames = null;
                _screamBlastSprite = null;
                _screamParticles.Clear();
                // 过图时清零“穿过单向平台”的残留状态：
                // 若过图前按过下键，_dropThroughTimer 会在传送期间被冻结而不递减，
                // 进房后会残留导致短暂无视单向平台
                _dropThroughTimer = 0;
                _skipLiftNow = false;
                _groundIsLift = false;
                // 过图时下砸延续：判断已在上方 RebindTicket 之前完成（它会清 _diving）
                if (!diveTransferContinue)
                {
                    _diving = false;
                    _divePhase = 0;
                    _diveTimer = 0f;
                    _diveHardlockTimer = 0f;
                    _diveInvulnTimer = 0f;
                    _invincibleTimer = 0f;
                }
                _swimState = 0; // 过图：游泳状态复位
                ResetNailArt(); // 过图：骨钉技艺复位
                ResetDreamNail(); // 过图：梦钉复位
                if (!diveTransferContinue)
                {
                    DashAudio.StopDiveLoop(); // 过图：停止下砸循环音（延续时保留）
                }
                _diveSwords.Clear();
                _diveSpikes.Clear();
                _diveLandParticles.Clear();
                _sporeClouds.Clear(); // 过图：旧地图坐标的孢子云一并清除
                _weaverlings.Clear(); // 过图：小编织者一并清除（随后重生）
                _weaverThreads.Clear();
                _grimm = null; // 过图：小格林一并清除（随后重生）
                _grimmFireballs.Clear(); // 过图：旧地图坐标的火球一并清除
                // 走路过图：等骑士在新地图稳定 3 秒后再生成小编织者，
                // 避免在强制位移/坐标不稳定期间重生导致丢失（传送保持立即生成）。
                if (!_fastTravelInProgress)
                {
                    _weaverRespawnDelay = WeaverRespawnDelay;
                    _grimmRespawnDelay = GrimmRespawnDelay;
                }
                // 读档/新游戏：直接把小骑士对齐到诺艾尔（跳过渐进跟随）。
                // 否则骑士会残留在旧地图坐标上（新地图里不可见/卡住），需要按两下 T 才能恢复。
                if (JustLoadedSave)
                {
                    JustLoadedSave = false;
                    PRNoel prLoad = GetPr();
                    if (prLoad != null)
                    {
                        _isSitting = false;
                        _sitStandingUp = false;
                        _sitBench = null;
                        _sitSliding = false;
                        _sitTimer = 0f;
                        _sitStandTimer = 0f;
                        X = prLoad.x - HurtCenterX;
                        Y = prLoad.mbottom - SizeY;
                        Vx = 0f;
                        Vy = 0f;
                        Grounded = true;
                        _lastSafeX = X;
                        _lastSafeY = Y;
                        _pendingReposition = 0;
                        _pendingRepositionActive = false;
                        _transitionPause = false;
                        _horizontalTransfer = false;
                        _transferSimDur = 0f;
                        _currentClip = null;
                        if (CheckGround(out float gtLoad, Y + SizeY))
                        {
                            Y = gtLoad - SizeY;
                        }
                    }
                }
                // 仅横向过图：恢复诺艾尔物理，让 AIC 的进门强制位移能推动她（防止被带回原房间）
                if (_horizontalTransfer)
                {
                    KnightInCradleBehaviour.ResumeNoelForTransfer();
                }
                return;
            }

            // 传送期间：暂停小骑士物理，避免在旧地图上乱跑
            if (transferring)
            {
                _transitionPause = true;
                _pendingRepositionActive = true;
                return;
            }

            // 读档后：AIC 重建了地图渲染器，旧渲染票据失效。地图切换分支已尝试重绑，
            // 但若当时渲染器尚未就绪/异常，这里持续重试直到成功，否则小骑士读档后渲染消失。
            if (PendingLoadRebind && cur != null && _mp == cur)
            {
                try
                {
                    RebindTicket();
                    PendingLoadRebind = false;
                }
                catch (Exception)
                {
                }
            }
            // 快速旅行（含原地传送）：结束后地图材质/渲染器可能被重建，重绑一次，
            // 否则小骑士传送到“当前位置就是椅子/战斗地点”后渲染消失。
            if (PendingFastTravelRebind && !_fastTravelInProgress &&
                cur != null && _mp == cur)
            {
                try
                {
                    RebindTicket();
                    PendingFastTravelRebind = false;
                }
                catch (Exception)
                {
                }
            }

            // 传送结束：开始持续重定位到诺艾尔的新位置（入口点）
            if (_transitionPause)
            {
                _transitionPause = false;
                // 横向：90 帧兜底（约 1.5s）；
                // 纵向：普通 45 帧简单跟随；冲刺/下砸跨房时缩短到 20 帧，
                // 让延续的上冲更快恢复（减少“反向下移到入口再上冲”的等待感）
                if (_horizontalTransfer)
                {
                    _pendingReposition = 90;
                }
                else
                {
                    _pendingReposition = (_superDashing || _diving) ? 20 : 45;
                }
            }

            // 坐姿/死亡/复活淡出期间不跟随诺艾尔重定位，
            // 否则会把刚复活坐在长椅上的小骑士带跑（甚至带出房间）
            if (_pendingReposition > 0 &&
                !_isSitting && !_sitStandingUp && !_isDead && !_respawnFadeOut)
            {
                // 记录本帧开始时是否已落地：已落地则保持当前站立位置，
                // 不再每帧被镜像拉回诺艾尔位置（避免站上单向板后被拉穿/被吸回）
                bool wasGrounded = Grounded;
                PRNoel pr = GetPr();
                if (pr != null)
                {
                    if (_horizontalTransfer)
                    {
                        // 横向过图：每帧硬跟随诺艾尔的确切位置。
                        // 不再用手写物理跟随（手写物理无法正确处理单向平台，
                        // 会导致进单向地板房间时穿板）；硬跟随则完全复制诺艾尔
                        // 的位置，她站在单向平台上骑士就站在上面。
                        X = pr.x - HurtCenterX; // 骑士受击箱中心对齐诺艾尔中心（消除右漂）
                        if (!wasGrounded)
                        {
                            Y = pr.mbottom - SizeY;
                        }
                        Vx = 0f;
                        Vy = 0f;
                    }
                    else
                    {
                        // 纵向过图：保持简单跟随
                        X = pr.x - HurtCenterX; // 骑士受击箱中心对齐诺艾尔中心（消除右漂）
                        if (!wasGrounded)
                        {
                            Y = pr.mbottom - SizeY;
                        }
                        Vx = 0f;
                        Vy = 0f;
                    }
                    Grounded = true;
                    _lastSafeX = X;
                    _lastSafeY = Y;
                    // 重定位期间每帧地面吸附：防止横向物理跟随阶段穿过单向平台。
                    // 只在脚底贴近地面（≤1.5 格）时吸附，避免被远处地板拽过去。
                    if (!_superDashing && !_diving && !wasGrounded)
                    {
                        try
                        {
                            float footSnap = FindSnapFootY(Y + SizeY, false);
                            if (footSnap >= 0f && footSnap <= Y + SizeY + 1.5f)
                            {
                                Y = footSnap - SizeY;
                                Grounded = true;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                    // 仅横向过图：进门强制位移结束检测（模拟按键从有到无即结束，提前让骑士接管）
                    if (_horizontalTransfer)
                    {
                        bool simActive = false;
                        try
                        {
                            object v = SimKeyField != null ? SimKeyField.GetValue(pr) : null;
                            uint sk = 0;
                            if (v is uint u)
                            {
                                sk = u;
                            }
                            else if (v is int i)
                            {
                                sk = (uint)i;
                            }
                            simActive = (sk & 3U) != 0U; // 位0/1 = 左右移动模拟键
                        }
                        catch (Exception)
                        {
                        }
                        if (simActive)
                        {
                            _transferSimDur += Time.deltaTime;
                        }
                        else
                        {
                            // 必须已落地才提前释放：防止在强制位移中段/坑洞上方被放开而掉下去
                            if (_transferSimDur >= 0.2f && Grounded)
                            {
                                // 强制位移刚结束：下一帧结束跟随
                                _pendingReposition = 1;
                            }
                            _transferSimDur = 0f;
                        }
                    }
                }
                if (_pendingReposition == 90)
                {
                    // 已进入新房间（脚底实心/落地）：提前 20 帧结束，防止等待过久状态残留
                    if (CheckGround(out float gtD, Y + SizeY))
                    {
                        _pendingReposition = 70;
                    }
                }
            if (_pendingReposition == 1)
            {
                _horizontalTransfer = false;
                _transferSimDur = 0f;
                _pendingRepositionActive = false;
                _fastTravelInProgress = false;
                _fastTravelWaitFrames = 0;
                // 快速旅行同图传送没有 transferring 翻转，由重定位结束兜底重播 deco 淡入
                KnightHudDeco.OnFastTravelArrive();
                // 最后一帧：手动强制对齐一次 + 强制同步诺艾尔（KnightInCradleBehaviour 恢复暂停）
                    if (pr != null)
                    {
                        X = pr.x - HurtCenterX; // 骑士受击箱中心对齐诺艾尔中心（消除右漂）
                        if (!wasGrounded)
                        {
                            Y = pr.mbottom - SizeY;
                        }
                        Vx = 0f;
                        Vy = 0f;
                    }
                    // 重定位结束：强制吸附一次地面（防止入口上方是单向平台时短暂穿过去）。
                    // 冲刺/下砸延续时不吸附（它们要按原方向继续飞行/下落）
                    _dropThroughTimer = 0;
                    _skipLiftNow = false;
                    if (!_superDashing && !_diving)
                    {
                        try
                        {
                            if (wasGrounded)
                            {
                                // 已落地：保持站立位置，不吸附不镜像（消除“被吸回板子上”的顿感）
                            }
                            else
                            {
                                // 空中交接：吸附到入口单向板（防止掉回原房间）
                                float footY = FindSnapFootY(Y + SizeY, true);
                                if (footY >= 0f)
                                {
                                    Y = footY - SizeY;
                                    Grounded = true;
                                }
                                else if (CheckGround(out float gtEnd, Y + SizeY))
                                {
                                    Y = gtEnd - SizeY;
                                    Grounded = true;
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                    // 上冲跨图续接：入口门框/天花板可能正好在头顶，
                    // 一恢复上冲就撞墙断掉。向上微调（≤3 格）到头顶无墙且身体不嵌墙。
                    if (_superUpDash)
                    {
                        // 进入新房间：重置撞墙宽限（新房间的入口墙重新计）
                        _superUpDashWallGrace = 0;
                        int nudge = 0;
                        while ((SuperUpFrontBlocked() || KnightBodyBlocked()) && nudge < 6)
                        {
                            Y -= 0.5f;
                            nudge++;
                        }
                    }
                    KnightInCradleBehaviour.SyncNoelToKnightPublic();
                    // 兜底：重定位结束时若骑士与诺艾尔仍明显脱节，强制对齐
                    PRNoel prEnd = GetPr();
                    if (prEnd != null)
                    {
                        float wantX = prEnd.x - HurtCenterX;
                        float wantY = prEnd.mbottom - SizeY;
                        if (Mathf.Abs(X - wantX) + Mathf.Abs(Y - wantY) > 2f)
                        {
                            X = wantX;
                            Y = wantY;
                            Vx = 0f;
                            Vy = 0f;
                        }
                    }
                    // 过图后刷新诺艾尔碰撞体/清蹲伏/镜头对焦（AIC 传送可能重建了她的碰撞体）
                    KnightInCradleBehaviour.RefreshNoelForKnight();
                }
                // 快速旅行：到达目的地长椅前不结束跟随（诺艾尔还在走向长椅），
                // 到达后把两人精确放到椅位，确保 AIC 的 AUTO_SAVE_BENCH（3×3 查长椅）成功；
                // 超时（约 5 秒）兜底按普通流程结束。
                if (_fastTravelInProgress)
                {
                    _fastTravelWaitFrames++;
                    PRNoel prFT = GetPr();
                    NelChipBench benchFT = prFT != null
                        ? FindNearBenchIn(_mp, prFT.x, prFT.mbottom)
                        : null;
                    if (benchFT != null && prFT != null)
                    {
                        // 到达目的地：先让骑士继续跟随诺艾尔 1 秒（让 AIC 传送收尾走完），
                        // 之后才把两人精确放到椅位并恢复“诺艾尔跟随骑士”的常规同步。
                        if (_fastTravelArrivedTimer < 0f)
                        {
                            _fastTravelArrivedTimer = 1f;
                        }
                        _fastTravelArrivedTimer -= Time.deltaTime;
                        if (_fastTravelArrivedTimer <= 0f)
                        {
                            _fastTravelInProgress = false;
                            CharmEffects.BattleAreaFastTravel = false; // 长椅目的地：清战斗区域标记
                            prFT.setTo(benchFT.mapcx, benchFT.mbottom - prFT.sizey);
                            X = prFT.x - HurtCenterX;
                            Y = prFT.mbottom - SizeY;
                            Grounded = true;
                            Vx = 0f;
                            Vy = 0f;
                            _lastSafeX = X;
                            _lastSafeY = Y;
                            _pendingReposition = 1; // 下一帧执行完成分支
                        }
                    }
                    else if (_fastTravelWaitFrames > 300)
                    {
                        _fastTravelInProgress = false;
                        CharmEffects.BattleAreaFastTravel = false; // 超时兜底：清标记防残留
                        _fastTravelArrivedTimer = -1f;
                        _fastTravelNoBenchTimer = -1f;
                        _pendingReposition = 1;
                    }
                }
                else
                {
                    _pendingReposition--;
                }

                // 战斗区域传送：跨图以“地图已切换”为到达信号，0.5 秒缓冲后完成跟随；
                // 长椅传送不走这里（等诺艾尔走到长椅，AUTO_SAVE_BENCH 才能成功）。
                if (_fastTravelInProgress && CharmEffects.BattleAreaFastTravel)
                {
                    M2DBase m2dNow = M2DBase.Instance;
                    bool transferringNow = m2dNow != null && m2dNow.transferring_game_stopping;
                    bool transferJustDone = !transferringNow && _fastTravelPrevTransferring;
                    _fastTravelPrevTransferring = transferringNow;
                    if (transferJustDone || _fastTravelMapChanged || _fastTravelWaitFrames > 30)
                    {
                        if (_fastTravelNoBenchTimer < 0f)
                        {
                            _fastTravelNoBenchTimer = 0.5f;
                        }
                        _fastTravelNoBenchTimer -= Time.deltaTime;
                        if (_fastTravelNoBenchTimer <= 0f)
                        {
                            _fastTravelInProgress = false;
                            _fastTravelNoBenchTimer = -1f;
                            CharmEffects.BattleAreaFastTravel = false;
                            _pendingReposition = 1;
                        }
                    }
                }
                // 不 return：继续执行下方物理（Vx/Vy 已同步诺艾尔速度，由碰撞系统自行走进入口）
            }

            try
            {
            float dt = Time.deltaTime * 60f;

            // 移动（左右可配置，默认 A/D）；上下左右交给抬头/低头
            bool left = KeyConfig.GetHeld(KnightInCradlePlugin.MoveLeftKey, KeyCode.A);
            bool right = KeyConfig.GetHeld(KnightInCradlePlugin.MoveRightKey, KeyCode.D);
            // 跳跃（默认 W）
            bool jump = KeyConfig.GetPressed(KnightInCradlePlugin.JumpKey, KeyCode.W);
            bool jumpHeldNow = KeyConfig.GetHeld(KnightInCradlePlugin.JumpKey, KeyCode.W);
            // C 键 = 聚集/施法合并键：快速点按=施法（+上=深渊尖啸、+下=黑暗降临），长按=凝聚回血
            bool mergedPressed = FocusPressed();
            bool mergedHeld = FocusHeld();
            bool mergedReleased = FocusReleased();
            bool mergedTapCast = UpdateCastTapState(mergedPressed, mergedHeld, mergedReleased);
            bool focusHeld = _castTapFocus && mergedHeld; // 超过点按窗口仍按住 → 才开始凝聚
            // S 键 = 快速施法：点按直接施法（+上/下对应法术）
            bool fireballHeld = FireballHeld();
            bool fireballPressed = FireballPressed() || mergedTapCast;
            // 梦钉（默认空格）
            bool dreamPressed = DreamNailPressed();
            // 抬头/低头（上/下）：按住时按施法键不发射火球
            bool lookUpHeldNow = KeyConfig.GetHeld(KnightInCradlePlugin.LookUpKey, KeyCode.Q);
            bool lookDownHeldNow = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            // 水晶之心（超级冲刺）：按住蓄能，松开发射（默认 C，可在配置改）
            bool superHeld = SuperDashHeld();
            bool superPressed = SuperDashPressed();
            // 转房事件播放期间：同样锁定全部输入，防止骑士沿斜梯/出口继续走远
            bool exitEventActive = false;
            if (EV.isActive(false))
            {
                try
                {
                    evt.EvReader exitEv = EV.getCurrentEvent();
                    if (exitEv != null && exitEv.name != null &&
                        exitEv.name.Contains("Exit") && exitEv.name.Contains("STAND"))
                    {
                        exitEventActive = true;
                    }
                }
                catch (Exception)
                {
                }
            }
            // 重定位跟随阶段：锁定全部玩家输入，让骑士只按诺艾尔物理速度走进入口
            if (_pendingReposition > 0 || exitEventActive)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                focusHeld = false;
                fireballPressed = false;
                fireballHeld = false;
                dreamPressed = false;
                lookUpHeldNow = false;
                lookDownHeldNow = false;
                superHeld = false;
                superPressed = false;
                ResetCastTapState();
            }

            // 按住“下”（默认鼠标右键）：临时跳过单向平台，让小骑士穿过去
            bool down = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            bool downPressed = KeyConfig.GetPressed(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            if (_pendingReposition > 0 || exitEventActive)
            {
                down = false;
                downPressed = false;
            }
            if (downPressed && Grounded && _groundIsLift)
            {
                _dropThroughTimer = 14;
            }
            if (!down)
            {
                _dropThroughTimer = 0;
            }
            else if (_dropThroughTimer > 0)
            {
                _dropThroughTimer--;
            }
            _skipLiftNow = down || _dropThroughTimer > 0;
            if (_skipLiftNow && !Grounded && _groundIsLift && !_dropLogged)
            {
                _dropLogged = true;
            }
            if (!_skipLiftNow)
            {
                _dropLogged = false;
            }

            // 受伤无敌计时（unscaled：画面停滞期间也继续走）
            if (_invincibleTimer > 0f)
            {
                _invincibleTimer -= Time.unscaledDeltaTime;
                if (_invincibleTimer <= 0f)
                {
                    _invincibleTimer = 0f;
                }
            }

            // 受击黑边渐出（unscaled：画面停滞期间也继续走，受击瞬间全亮后线性消失）
            if (_hitVignetteAlpha > 0f)
            {
                _hitVignetteAlpha = Mathf.Max(0f, _hitVignetteAlpha - Time.unscaledDeltaTime / HitVignetteTime);
            }

            // 回血成功屏幕闪光渐出
            if (_focusFlashAlpha > 0f)
            {
                _focusFlashAlpha = Mathf.Max(0f, _focusFlashAlpha - Time.unscaledDeltaTime / FocusFlashTime);
            }
            // 无忧旋律免伤成功红色屏幕闪光渐出
            if (_melodyFlashAlpha > 0f)
            {
                _melodyFlashAlpha = Mathf.Max(0f,
                    _melodyFlashAlpha - Time.unscaledDeltaTime / MelodyFlashTime);
            }
            // 生命血羁绊回血：蓝色屏幕闪光渐出
            if (_lifebloodFlashAlpha > 0f)
            {
                _lifebloodFlashAlpha = Mathf.Max(0f,
                    _lifebloodFlashAlpha - Time.unscaledDeltaTime / LifebloodFlashTime);
            }

            // 拼刀冻结（0.3s）：动画、特效、键位、位移全部暂停 —— 直接本帧 return，
            // 不推进输入/物理/剪辑/特效计时（渲染票据仍按当前状态绘制，所以是“定格”）。
            // 上面的无敌计时与闪光渐出已按真实时间推进，所以冻结不会把无敌时间“冻长”。
            if (_nailParryFreezeTimer > 0f)
            {
                _nailParryFreezeTimer -= Time.unscaledDeltaTime;
                if (_nailParryFreezeTimer <= 0f)
                {
                    _nailParryFreezeTimer = 0f;
                }
                if (!_isDead)
                {
                    return;
                }
            }

            // 死亡状态：死亡动画 + 黑屏淡入/保持/复活淡出
            if (_isDead)
            {
                HandleDeathState();
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                goto SitPhysicsSkipped;
            }
            if (_respawnFadeOut)
            {
                // 复活后黑屏淡出：短暂锁输入，恢复站立
                _respawnFadeTimer -= Time.deltaTime;
                _deathBlackAlpha = Mathf.Clamp01(_respawnFadeTimer / DeathBlackOutTime);
                if (_respawnFadeTimer <= 0f)
                {
                    _respawnFadeOut = false;
                    _deathBlackAlpha = 0f;
                }
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                goto SitPhysicsSkipped;
            }

            // 受伤硬直：0.3s 画面停滞（timeScale=0）→ 0.2s 不受控制击飞
            if (_hurt)
            {
                _taunting = false; // 受伤打断挑衅
                _tauntTimer = 0f;
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                if (_hurtFreezeTimer > 0f)
                {
                    if (!_hurtFrozen)
                    {
                        // 画面停滞：同时停 AIC 时间轴（世界/敌人/镜头）与 Unity 时间轴（小骑士动画/HUD）
                        _hurtFrozen = true;
                        _hurtPrevTsBase = Map2d.TSbase;
                        try
                        {
                            Map2d.setTimeScale(0f, true);
                        }
                        catch (Exception)
                        {
                        }
                        Time.timeScale = 0f;
                    }
                    _hurtFreezeTimer -= Time.unscaledDeltaTime;
                    if (_hurtFreezeTimer <= 0f)
                    {
                        _hurtFreezeTimer = 0f;
                        _hurtFrozen = false;
                        try
                        {
                            Map2d.setTimeScale(_hurtPrevTsBase > 0f ? _hurtPrevTsBase : 1f, true);
                        }
                        catch (Exception)
                        {
                        }
                        Time.timeScale = 1f;
                        _hurtFlyTimer = HurtFlyTime;
                        Vx = _hurtDir * HurtKnockVx;
                        Vy = HurtKnockVy;
                    }
                    goto SitPhysicsSkipped;
                }
                if (_hurtFlyTimer > 0f)
                {
                    _hurtFlyTimer -= Time.deltaTime;
                    if (_hurtFlyTimer <= 0f)
                    {
                        _hurtFlyTimer = 0f;
                        _hurt = false;
                        Vx = 0f;
                    }
                    ApplyHurtPhysics(Time.deltaTime * 60f);
                    goto SitPhysicsSkipped;
                }
                _hurt = false;
            }

            // ---- 坐长椅：交互检测 ----
            // 未坐下时，靠近长椅（<1 格）按 Z 坐下；坐姿下按 Z / 移动 / 跳跃 / 冲刺 / 攻击 起身。
            TryBenchInteraction();

            // 坐姿 / 起身过渡：彻底锁定输入与物理（移动、跳跃、冲刺、攻击全部无效）
            if (_isSitting || _sitStandingUp)
            {
                _sitTimer += Time.deltaTime;
                Vx = 0f;
                Vy = 0f;
                Grounded = true;
                _dashing = false;
                _dashTimer = 0f;
                _attacking = false;
                _attackTimer = 0f;
                _onWall = false;
                DestroyHitbox();
                if (_isSitting && _sitBench != null)
                {
                    if (_sitSliding)
                    {
                        // 平滑滑向长椅中心（同时播放 Sit 动画）
                        _sitSlideTimer += Time.deltaTime;
                        float t = Mathf.Clamp01(_sitSlideTimer / _sitSlideDuration);
                        float e = Mathf.SmoothStep(0f, 1f, t);
                        X = Mathf.Lerp(_sitFromX, _sitToX, e);
                        Y = Mathf.Lerp(_sitFromY, _sitToY, e);
                        if (t >= 1f)
                        {
                            _sitSliding = false;
                            // 到达椅面：播放与诺艾尔相同的坐椅粒子 + 空洞骑士原版坐椅音效
                            PlaySitLandingFx();
                        }
                    }
                    else
                    {
                        // 坐姿期间锁定在椅面高度（y 已 -0.5 抬升），防止重力把骑士慢慢拖下去
                        X = _sitX;
                        Y = _sitY;
                    }
                }
                else if (_sitStandingUp && _sitBench != null)
                {
                    // 起身过渡：横向保持椅子中心，纵向从椅面平滑落回地面（与 Get Off 动画同速）
                    X = _sitX;
                    float t = Mathf.Clamp01(1f - _sitStandTimer / SitTransitionTime);
                    if (SitTransitionTime > 0f)
                    {
                        Y = Mathf.Lerp(_sitY, _sitGroundY, t);
                    }
                }
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                // 坐姿/起身期间跳过移动、跳跃、冲刺、重力、落地检测等全部物理
                goto SitPhysicsSkipped;
            }

            // ---- 挑衅（V 键）：仅地面可用；播放挑衅动画期间锁定移动/攻击 ----
            bool tauntPressed = TauntPressed();
            if (tauntPressed && !_taunting && Grounded && !_isSitting && !_sitStandingUp &&
                !_attacking && !_dashing && !_focusing && !_fireballCasting && !_screaming &&
                !_diving && !_nailArtCharging && !_nailArtSlashing && !_dashSlashing &&
                !_cycloneSlashing && !_dreamNailing && !_superCharging && !_superDashing &&
                !_hurt && !_isDead && _pendingReposition <= 0 && _swimState == 0 &&
                _clips.ContainsKey("Challenge"))
            {
                _taunting = true;
                _tauntTimer = GetClipDuration("Challenge");
                _currentClip = null; // 从头播放挑衅动画
                _tauntBattleOpened = false; // 等 0010 帧播完后才触发开战
            }
            if (_taunting)
            {
                _tauntTimer -= Time.deltaTime;
                // 0007 帧播完后才进入战斗，仅触发一次
                if (!_tauntBattleOpened && _currentClip == "Challenge" && _frameIndex >= 8)
                {
                    _tauntBattleOpened = true;
                    TryOpenNearBattleByTaunt();
                }
                if (_tauntTimer <= 0f)
                {
                    _taunting = false;
                    _tauntTimer = 0f;
                    _currentClip = null; // 动画播完恢复站立
                }
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                fireballPressed = false;
                fireballHeld = false;
                dreamPressed = false;
                superHeld = false;
                superPressed = false;
                lookUpHeldNow = false;
                lookDownHeldNow = false;
                goto SitPhysicsSkipped;
            }

            // 虚空解放期间：禁用一切技能输入（法术/超级冲刺/梦钉/凝聚），放在法术判定之前，
            // 否则 法术/超级冲刺 的起手判定会先于下方的锁输入块执行，导致技能中仍可施法。
            if (_voidPhase >= 2)
            {
                focusHeld = false;
                fireballPressed = false;
                fireballHeld = false;
                dreamPressed = false;
                superHeld = false;
                superPressed = false;
                lookUpHeldNow = false;
                lookDownHeldNow = false;
                ResetCastTapState();
            }
            // 下劈剑气/回弹期间按法术键：立即终止下劈与剑气动画，释放法术
            // （三个法术的施放函数内部会再调用 CancelPogoRebound 清掉回弹速度）
            bool upPressedNow = KeyConfig.GetPressed(KnightInCradlePlugin.LookUpKey, KeyCode.Q);
            if (_attacking && _downSlash &&
                (fireballPressed ||
                 (upPressedNow && fireballHeld) ||
                 (downPressed && fireballHeld)))
            {
                _attacking = false;
                _attackTimer = 0f;
                _swingHits.Clear();
                DestroyHitbox();
                _fxTex = null;
            }

            // ---- 黑暗降临（下砸）：同时按住 S 与下键 ----
            if (((fireballPressed && down) || (downPressed && fireballHeld)) && CanStartDive())
            {
                StartDive();
            }
            // ---- 深渊尖啸（尖啸）：同时按住施法键与上键 ----
            else if (((fireballPressed && lookUpHeldNow) ||
                      (upPressedNow && fireballHeld)) && CanCastScream())
            {
                StartScream();
            }
            // ---- 暗影之魂（火球）：点按 S（不按上/下）直接施法 ----
            else if (fireballPressed && !lookUpHeldNow && !lookDownHeldNow && CanCastFireball())
            {
                CastFireball(); // 内部含音效与冷却
            }
            // ---- 虚空解放：梦钉键 + 上键 按下直接释放（按下瞬间即开始前摇）----
            bool dreamHeld = DreamNailHeld();
            bool voidPressed = (dreamPressed && lookUpHeldNow) || (dreamHeld && upPressedNow);
            if (voidPressed && _voidPhase == 0 && CanStartVoidLiberation())
            {
                StartVoidLiberation();
            }
            if (voidPressed && dreamPressed)
            {
                dreamPressed = false; // 虚空解放按下时屏蔽普通梦钉起手
            }
            // ---- 梦钉：长按空格（仅地面）前摇后抽出梦钉（挥击期间禁用其他按键）----
            if (dreamPressed && _voidPhase == 0 && !_dreamNailing && !_attacking && !_dashing &&
                !_isSitting && !_focusing && !_fireballCasting && !_screaming && !_diving &&
                !_nailArtCharging && !_nailArtSlashing && !_dashSlashing && !_cycloneSlashing &&
                _pendingReposition <= 0 && _swimState == 0 && Grounded)
            {
                StartDreamNail();
            }

            // ---- 凝聚回血（Focus）：状态机；凝聚中锁定一切输入与物理 ----
            UpdateFocus(focusHeld, dt);
            if (_focusing)
            {
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                if (!CharmEffects.IsEquipped(CharmEffects.UnnId))
                {
                    left = false;
                    right = false;
                    goto SitPhysicsSkipped;
                }
                // 护符34 乌恩之形：回血时可以左右移动（蛞蝓贴地走）；
                // 禁止跳跃/冲刺/技能，但保留左右输入交给下方物理
                fireballPressed = false;
                fireballHeld = false;
                dreamPressed = false;
                superHeld = false;
                superPressed = false;
                lookUpHeldNow = false;
                lookDownHeldNow = false;
            }

            // ---- 水晶之心（超级冲刺）：蓄力 / 飞行；蓄力或飞行中锁定输入与物理 ----
            UpdateSuperDash(superHeld, superPressed, jump, lookUpHeldNow, dt);
            if (_superJumpStop)
            {
                _superJumpStop = false;
                jump = false;
                jumpHeldNow = false;
            }
            if ((_superCharging || _superDashing) && _pendingReposition <= 0)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                goto SitPhysicsSkipped;
            }

            // ---- 暗影之魂（火球）：施法/滞空/后坐期间锁定输入与物理 ----
            UpdateFireball(dt);
            // ---- 护符23 吸虫之巢：黑色吸虫推进（重力/弹跳/碰敌/寿命）----
            UpdateFlukes(Time.deltaTime);
            // ---- 蜕变挽歌：满血普攻剑气投射物 ----
            UpdateElegyBlades(dt);
            if (_fireballCasting)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                goto SitPhysicsSkipped;
            }

            // ---- 深渊尖啸：尖啸期间锁定输入与物理（滞空）----
            UpdateScream();
            if (_screaming)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                goto SitPhysicsSkipped;
            }

            // ---- 虚空解放：前摇/出伤/后摇期间锁定输入与物理 ----
            UpdateVoidLiberation(dt);
            if (_voidPhase >= 2)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                fireballPressed = false;
                fireballHeld = false;
                dreamPressed = false;
                superHeld = false;
                superPressed = false;
                goto SitPhysicsSkipped;
            }

            // ---- 黑暗降临（下砸）：前摇/下落/落地硬直期间锁定输入与物理 ----
            UpdateDive();
            // 重定位阶段不跳过物理（横向过图物理跟随需要），下砸移动已在 UpdateDive 内冻结
            if (_diving && (_divePhase != 2 || _diveHardlockTimer > 0f) && _pendingReposition <= 0)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
                goto SitPhysicsSkipped;
            }

            // ---- 游泳：液面漂浮 / 水下上浮（占用跳跃/下潜输入）----
            UpdateSwimState(ref left, ref right, ref jump, ref jumpHeldNow, ref downPressed, lookUpHeldNow, dt);

            // 强力劈砍 / 冲刺劈砍：推进判定与剑气，锁输入（空中由重力段改为缓慢下落）。
            // 必须在 move/朝向计算之前清零，否则玩家仍可用方向键改变朝向。
            UpdateNailArtSlash();
            UpdateDashSlash();
            if (_nailArtSlashing || _dashSlashing)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
            }
            // 旋风劈砍：推进多段伤害与延长；只允许慢速左右移动
            bool cycloneAttackPressed = KeyConfig.GetPressed(KnightInCradlePlugin.AttackKey, KeyCode.Mouse0);
            UpdateCycloneSlash(cycloneAttackPressed);
            if (_cycloneSlashing)
            {
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
            }
            // 梦钉挥击：推进判定，期间禁用其他按键
            UpdateDreamNail();
            if (_dreamNailing)
            {
                left = false;
                right = false;
                jump = false;
                jumpHeldNow = false;
                down = false;
                downPressed = false;
                _dropThroughTimer = 0;
            }
            // 冲刺期间禁用方向键：既不走动，也不转身（在 move/朝向计算之前清零）
            if (_dashing)
            {
                left = false;
                right = false;
            }

            float move = 0f;
            if (left)
            {
                move -= 1f;
            }
            if (right)
            {
                move += 1f;
            }
                if (move != 0f)
                {
                    _lastMoveDir = move;
                    // 原版精灵默认朝左：左移不镜像，右移镜像
                    bool invert = KnightInCradlePlugin.FacingInvertConfig != null &&
                                  KnightInCradlePlugin.FacingInvertConfig.Value;
                    int face = move < 0f ? 1 : -1;
                    if (invert)
                    {
                        face = -face;
                    }
                    _faceDir = face;
                }

        // 冲刺（默认 Shift：LeftShift / RightShift 均可）
        string dashKeyCfg = KnightInCradlePlugin.DashKey != null ? KnightInCradlePlugin.DashKey.Value : "LeftShift";
        KeyCode dashKey = KeyConfig.Parse(dashKeyCfg, KeyCode.LeftShift);
        bool dashPressed = UnityEngine.Input.GetKeyDown(dashKey) ||
                           (dashKey == KeyCode.LeftShift && UnityEngine.Input.GetKeyDown(KeyCode.RightShift)) ||
                           (dashKey == KeyCode.RightShift && UnityEngine.Input.GetKeyDown(KeyCode.LeftShift));
        // 攀附时也能冲刺：爬左墙向右冲、爬右墙向左冲（向远离墙方向）；
        // 墙上直接冲刺会脱离墙壁，且不消耗空中冲刺（之后在空中还能冲一次）
        bool dashFromWall = _onWall;
        // 护符7 冲刺大师：向下冲刺（下 + 冲刺键，空中），位移与平地冲刺相同，落地停止
        bool downDash = dashPressed && !_dashing && _dashCooldown <= 0f && _canDash &&
            !Grounded && down && CharmEffects.IsEquipped(CharmEffects.DashmasterId) &&
            !_isSitting && !_focusing && !_fireballCasting && !_nailArtSlashing && !_cycloneSlashing &&
            !_dreamNailing && _pendingReposition <= 0;
        if (downDash)
        {
            _dashing = true;
            _dashDir = 0f; // _dashDir==0 标记向下冲刺
            _canDash = false; // 消耗空中冲刺
            _dashCooldown = 0.4f; // 冲刺大师：冷却 0.4s
            _dashJustEnded = false;
            _dashExitVx = 0f;
            _dashTimer = KnightInCradlePlugin.DashTimeConfig != null
                ? KnightInCradlePlugin.DashTimeConfig.Value
                : 0.28f;
            Vy = 0f;
            _jumpHeld = false;
            // 暗影冲刺充能完毕时，向下冲刺同样触发暗影冲刺（无敌/充能消耗/残影）
            _isShadowDash = _shadowRechargeTimer <= 0f;
            if (_isShadowDash)
            {
                // 护符33 锋利之影：暗影冲刺距离 +25%（时间缩短，速度提升见位移段）
                if (CharmEffects.IsEquipped(CharmEffects.ShadowId))
                {
                    _dashTimer *= SharpShadowTimeMult;
                }
                _invincibleTimer = Mathf.Max(_invincibleTimer, ShadowDashInvulnTime);
                _shadowRechargeTimer = KnightInCradlePlugin.ShadowRechargeConfig != null
                    ? KnightInCradlePlugin.ShadowRechargeConfig.Value
                    : 1.5f;
                _shadowDashHits.Clear(); // 锋利之影：本次暗影冲刺每个敌人只结算一次
                _shadowDashPlayerHits.Clear();
                _shadowDashPlayerKeys.Clear();
                _shadowDashHitSoundPlayed = false;
                _shadowDashPacketSent = false;
                _shadowDashPrevValid = false;
                _rcClipName = null;
                _rcFrameIndex = 0;
                _rcFrameTimer = 0f;
                _ghosts.Clear();
                _dashStartX = X;
                _dashStartY = Y;
                _ghostFading = false;
                _ghostFadeTimer = 0f;
                _ghostFadeTotal = 0;
                _ghostFrameTimer = 0f;
                _shadowDotSpawnTimer = 0f;
                _shadowDotFading = false;
                _shadowDotFadeTimer = 0f;
                _shadowDotFadeTotal = 0;
                _ghosts.Add(new TrailGhost { X = X, Y = Y, Frame = 0, Index = 0, Age = 0f });
            }
            _dashEndTime = _dashTimer * 3f / 12f;
            DashAudio.PlayDash(_isShadowDash);
        }
        else if (dashPressed && !_dashing && _dashCooldown <= 0f && (_canDash || dashFromWall) &&
            !_isSitting && !_focusing && !_fireballCasting && !_nailArtSlashing && !_cycloneSlashing &&
            !_dreamNailing &&
            _pendingReposition <= 0)
        {
            _dashing = true;
            if (dashFromWall)
            {
                _dashDir = -_wallDir; // 远离墙壁
                _faceDir = _wallDir;  // 面朝冲刺方向
                _onWall = false;      // 脱离墙壁
                _canDash = true;      // 保留空中冲刺次数
                _dashCooldown = 0f;   // 不设冷却，之后空中还能冲一次
            }
            else
            {
                _canDash = false; // 普通冲刺消耗一次
                _dashDir = -_faceDir;
                // 护符7 冲刺大师：冷却减至 0.4s
                _dashCooldown = CharmEffects.IsEquipped(CharmEffects.DashmasterId) ? 0.4f : 0.5f;
            }
            _dashJustEnded = false; // 新冲刺覆盖上一段冲刺的结束过渡
            _dashExitVx = 0f;
            _dashTimer = KnightInCradlePlugin.DashTimeConfig != null
                ? KnightInCradlePlugin.DashTimeConfig.Value
                : 0.28f;
            // 终点静止段 = 总时长的 3/12（播放 0009~0011）
            Vy = 0f;
            _jumpHeld = false;
            // 暗影冲刺：充能完毕时触发（ShadowDash 身体 + ShadowDashTrail 拖尾）
            _isShadowDash = _shadowRechargeTimer <= 0f;
            if (_isShadowDash)
            {
                // 护符33 锋利之影：暗影冲刺距离 +25%（时间缩短，速度提升见位移段）
                if (CharmEffects.IsEquipped(CharmEffects.ShadowId))
                {
                    _dashTimer *= SharpShadowTimeMult;
                }
                _invincibleTimer = Mathf.Max(_invincibleTimer, ShadowDashInvulnTime); // 暗影冲刺：0.4s 无敌
                _shadowRechargeTimer = KnightInCradlePlugin.ShadowRechargeConfig != null
                    ? KnightInCradlePlugin.ShadowRechargeConfig.Value
                    : 1.5f;
                _shadowDashHits.Clear(); // 锋利之影：本次暗影冲刺每个敌人只结算一次
                _shadowDashPlayerHits.Clear();
                _shadowDashPlayerKeys.Clear();
                _shadowDashHitSoundPlayed = false;
                _shadowDashPacketSent = false;
                _shadowDashPrevValid = false;
                    // 每次暗影冲刺都让充能动画从第 1 帧重新播放
                    _rcClipName = null;
                    _rcFrameIndex = 0;
                    _rcFrameTimer = 0f;
                    _ghosts.Clear();
                    _dashStartX = X;
                    _dashStartY = Y;
                    _ghostFading = false; // 新冲刺必须重置淡出状态，避免上一次残留计时器影响
                    _ghostFadeTimer = 0f;
                    _ghostFadeTotal = 0;
                    _ghostFrameTimer = 0f;
                    _shadowDotSpawnTimer = 0f;
                    _shadowDotFading = false;
                    _shadowDotFadeTimer = 0f;
                    _shadowDotFadeTotal = 0;
                    _ghosts.Add(new TrailGhost { X = X, Y = Y, Frame = 0, Index = 0, Age = 0f });
            }
            _dashEndTime = _dashTimer * 3f / 12f;
            // 播放冲刺音效（空洞骑士原版）：普通冲刺 hero_dash，暗影冲刺 hero_shade_dash_1
            DashAudio.PlayDash(_isShadowDash);
        }

            // 攻击：鼠标左键。按一下砍一刀；攻击期间不锁定移动/跳跃，可走砍、跳砍。
            // 独立 try/catch：攻击逻辑抛异常也不影响本帧物理与动画。
            try
            {
                bool attackPressed = KeyConfig.GetPressed(KnightInCradlePlugin.AttackKey, KeyCode.Mouse0);
                bool attackHeldNow = KeyConfig.GetHeld(KnightInCradlePlugin.AttackKey, KeyCode.Mouse0);
                bool attackReleasedNow = KeyConfig.GetReleased(KnightInCradlePlugin.AttackKey, KeyCode.Mouse0);
                bool upHeldNowArt = KeyConfig.GetHeld(KnightInCradlePlugin.LookUpKey, KeyCode.Q);
                bool downHeldNowArt = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);

                // 骨钉技艺：按住攻击键蓄力，蓄满后松开（且不按上/下/冲刺）释放强力劈砍；
                // 未蓄满松开 → 转普通攻击（_nailArtQuickTap）
                UpdateNailArt(attackPressed, attackHeldNow, attackReleasedNow,
                    upHeldNowArt, downHeldNowArt, dashPressed, dt);

                // 起手/接刀共用的一套“当前状态允许出刀吗”判断（不含是否正在挥砍本身：连击取消也要用它）
                bool attackEnvOk = !_dashing && !_isSitting && !_focusing &&
                    !_fireballCasting && !_nailArtSlashing && !_dreamNailing && _pendingReposition <= 0;
                // 本刀计时先推进：这样“上一刀刚好到点”的同一帧就能起手下一刀，
                // 两次攻击的间隔严格等于 SlashAttackTime()（否则会多等一帧，约 +16ms）。
                if (_attacking)
                {
                    _attackTimer -= Time.deltaTime;
                    if (_attackTimer <= 0f)
                    {
                        _attacking = false;
                        DestroyHitbox();
                    }
                }
                // 连击取消：上一刀已经播过“后摇取消点”（可视挥砍帧播完，只剩定格保持帧），
                // 而这时手里还握着本次按下的攻击 → 立刻结束当前刀，让下面直接接上下一刀。
                if (_attacking && _nailArtQuickTap && attackEnvOk && _attackTotalTime > 0f &&
                    _attackTimer <= _attackTotalTime * (1f - KnightInCradlePlugin.AttackChainCancelFrac))
                {
                    _attacking = false;
                    DestroyHitbox();
                }

                if (_nailArtQuickTap && !_attacking && attackEnvOk)
                {
                    _nailArtQuickTap = false;
                    CancelWallJumpForAttack(); // 蹬墙跳期间普攻/技艺：终止蹬墙跳，优先攻击
                    _attacking = true;
                    _swingHits.Clear(); // 新的一刀：清空已处理目标
                    // 一整刀的时长（真实秒）：0.4s；佩戴快速劈砍 0.3s。
                    // 不乘全局 AnimSpeed —— 普攻间隔按固定数值走，动画速度由 SlashClipFps 控制。
                    float swingTime = CharmEffects.SlashAttackTime();
                    _attackTotalTime = swingTime;
                    if (upHeldNowArt)
                    {
                        // 上劈：按住 Q 时优先，不受左右手交替影响，统一用上劈动画；
                        // 总时长与普攻一致（0.4s / 快速劈砍 0.3s）
                        _upSlash = true;
                        _downSlash = false;
                        _attackClipName = "UpSlash";
                        _attackTimer = swingTime;
                    }
                    else if (!Grounded && down)
                    {
                        // 空中下劈（Pogo）：只有“空中 + 按住鼠标右键”才进入；
                        // 动画用原版 DownSlash，判定框在正下方，命中后弹跳
                        _upSlash = false;
                        _downSlash = true;
                        _attackClipName = "DownSlash";
                        _attackTimer = swingTime;
                    }
                    else
                    {
                        // 普通攻击：左右手交替（第一次攻击初始 false → 右手挥砍并置 true；
                        // 下一次 → 左手挥砍并置 false，实现 右→左→右 循环）
                        _upSlash = false;
                        _downSlash = false;
                        _isRightSwing = !_isRightSwing;
                        _attackClipName = _isRightSwing ? "Attack" : "AttackAlt";
                        _attackTimer = swingTime;
                    }
                    // 新的一刀：强制动画从第 1 帧重播。
                    // 上劈/下劈连续出招时剪辑名不变，帧索引不会重置 → 第二刀只剩“定格保持帧”，
                    // 看起来像卡住不动。置空剪辑名让 PlayClip 重新播整套挥砍帧。
                    _currentClip = null;
                    _fxClipName = null;
                    _fxFrameIndex = 0;
                    _fxFrameTimer = 0f;
                    _elegySpawnedThisSwing = false; // 新的一刀：重置蜕变挽歌剑气发射标记
                    SpawnHitbox();
                    // 挥出骨钉的音效（空洞骑士原版 sword_2/3/4 交替）
                    DashAudio.PlaySlashSwing();
                    // 诊断（随普攻绿框一起开关）：打印实测的“两次攻击间隔”，用于核对
                    // 0.4s（无快速劈砍）/ 0.3s（佩戴快速劈砍）这组数值。
                    if (AttackHitboxDebug)
                    {
                        float now = Time.time;
                        if (_lastSwingStartTime > 0f && now - _lastSwingStartTime < 3f)
                        {
                            KnightInCradlePlugin.PluginLog?.LogInfo(
                                $"[KIC][普攻间隔] 实测 {now - _lastSwingStartTime:F3}s" +
                                $"（快速劈砍={CharmEffects.IsEquipped(CharmEffects.FastSlashId)}，" +
                                $"本刀时长={swingTime:F3}s）");
                        }
                        _lastSwingStartTime = now;
                    }
                }
                else if (_nailArtQuickTap)
                {
                    // 连击手感：一刀还没播完时按下的攻击**不再丢弃**，先留着；
                    // 到后摇取消点（见上方取消分支）立刻接上下一刀。
                    // 只有冲刺/凝聚/坐椅等真正冲突的状态才丢弃这次输入。
                    if (!(_attacking && attackEnvOk))
                    {
                        _nailArtQuickTap = false;
                    }
                }
                if (_attacking)
                {
                    // 位置始终跟随骑士（视觉参考）；只有极短有效时间内做碰撞检测
                    UpdateHitboxPosition();
                    if (_hitboxActiveTimer > 0f)
                    {
                        _hitboxActiveTimer -= Time.deltaTime;
                        CheckAttackOverlap();
                        if (_hitboxActiveTimer <= 0f)
                        {
                            // 有效时间结束：立即禁用碰撞体，绝不拖尾
                            DisableHitboxColliders();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 拼刀：普攻/骨钉技艺的判定箱与本帧远端小骑士的骨钉判定箱相交 → 双方各 1 秒无敌 + 弹刀音效
            try
            {
                UpdateNailParry();
            }
            catch (Exception)
            {
            }

            if (_dashCooldown > 0f)
            {
                _dashCooldown -= Time.deltaTime;
            }

            if (_dashing)
            {
                float dashSpeed = KnightInCradlePlugin.DashSpeedConfig != null
                    ? KnightInCradlePlugin.DashSpeedConfig.Value
                    : 0.45f;
                // 护符33 锋利之影：暗影冲刺速度 +40%
                if (_isShadowDash && CharmEffects.IsEquipped(CharmEffects.ShadowId))
                {
                    dashSpeed *= SharpShadowSpeedMult;
                }
                if (_dashDir == 0f)
                {
                    // 向下冲刺（冲刺大师）：垂直位移与平地冲刺相同，落地停止；
                    // 收尾段（shadow_dash0009~0011，_dashTimer<=_dashEndTime）开始
                    // 直接以最大下落速度下坠
                    bool endSegment = _dashTimer <= _dashEndTime;
                    float dy = (endSegment ? FallMax : dashSpeed) * dt;
                    if (endSegment)
                    {
                        Vy = FallMax;
                    }
                    Y += dy;
                    // 暗影向下冲刺：残影沿垂直方向（骑士路径上方）依次排布 + 黑色粒子
                    if (_isShadowDash)
                    {
                        if (_ghosts.Count == 0)
                        {
                            _ghosts.Add(new TrailGhost
                            {
                                X = _dashStartX,
                                Y = _dashStartY,
                                Frame = 0,
                                Index = 0,
                                Age = 0f
                            });
                        }
                        TrailGhost trail = _ghosts[0];
                        _ghostFrameTimer += Time.deltaTime;
                        if (_ghostFrameTimer >= GhostFrameInterval)
                        {
                            _ghostFrameTimer = 0f;
                            trail.Frame++;
                            trail.Age = 0f;
                        }
                        int posIndex = Mathf.Max(0, trail.Frame - 1);
                        trail.X = _dashStartX;
                        // 与平地冲刺相同的“逐帧向后”逻辑：向下冲刺的“向后”= 上方（Y 减小）
                        trail.Y = _dashStartY - posIndex * GhostFrameSpacing;
                        _shadowDotSpawnTimer -= Time.deltaTime;
                        if (_shadowDotSpawnTimer <= 0f)
                        {
                            _shadowDotSpawnTimer = ShadowDotSpawnInterval;
                            SpawnShadowDot();
                        }
                    }
                    // 护符33 锋利之影：暗影冲刺穿过敌人造成当前骨钉伤害
                    CheckShadowDashEnemyHit();
                    if (CheckGround(out float gtD, Y + SizeY))
                    {
                        // 落地：贴地停止，防止穿地
                        Y = Mathf.Min(gtD, Y + SizeY) - SizeY;
                        Grounded = true;
                        _dashing = false;
                        _dashJustEnded = true;
                        _dashExitVx = 0f;
                        _dashTimer = 0f;
                        // 暗影残影/粒子开始淡出
                        _ghostFading = _ghosts.Count > 0;
                        _ghostFadeTimer = 0f;
                        _ghostFadeTotal = _ghosts.Count;
                        _shadowDotFading = _shadowParticles.Count > 0;
                        _shadowDotFadeTimer = 0f;
                        _shadowDotFadeTotal = _shadowParticles.Count;
                    }
                    else
                    {
                        _dashTimer -= Time.deltaTime;
                        if (_dashTimer <= 0f)
                        {
                            _dashing = false;
                            _dashJustEnded = true;
                            _dashExitVx = 0f;
                            _dashTimer = 0f;
                            Vy = FallMax; // 下冲结束未落地：直接以最大下落速度下坠
                            _ghostFading = _ghosts.Count > 0;
                            _ghostFadeTimer = 0f;
                            _ghostFadeTotal = _ghosts.Count;
                            _shadowDotFading = _shadowParticles.Count > 0;
                            _shadowDotFadeTimer = 0f;
                            _shadowDotFadeTotal = _shadowParticles.Count;
                        }
                    }
                }
                else
                {
                // 全程统一位移：冲刺整个周期都执行水平移动（不再有“终点静止段”）
                float dx = _dashDir * dashSpeed * dt;
                X += dx;
                if (_isShadowDash)
                {
                    // 方案A（单残影，固定帧率）：帧切换按固定时间间隔推进，
                    // 不依赖骑士前进距离；每帧相对冲刺起点固定向后移 1.0 格，
                    // 0000/0001 同在起点；新帧出现时重新淡入。
                    if (_ghosts.Count == 0)
                    {
                        _ghosts.Add(new TrailGhost
                        {
                            X = _dashStartX,
                            Y = _dashStartY,
                            Frame = 0,
                            Index = 0,
                            Age = 0f
                        });
                    }
                    TrailGhost trail = _ghosts[0];
                    _ghostFrameTimer += Time.deltaTime;
                    if (_ghostFrameTimer >= GhostFrameInterval)
                    {
                        _ghostFrameTimer = 0f;
                        trail.Frame++; // 渲染时按剪辑长度取模循环
                        trail.Age = 0f; // 新帧重新淡入（一帧一帧放出）
                    }
                    // 固定向后移动：0000/0001 同在起点，之后每帧再向后移 1.0 格
                    int posIndex = Mathf.Max(0, trail.Frame - 1);
                    trail.X = _dashStartX - posIndex * GhostFrameSpacing * _dashDir;
                    // 0004/0005 两帧整体上移 0.5 格，其余帧保持起点高度
                    int shownFrame = GhostTrailFrame(trail);
                    trail.Y = _dashStartY - ((shownFrame == 4 || shownFrame == 5) ? GhostRaiseY : 0f);

                    // 暗影冲刺黑色粒子：沿骑士路径留下黑色拖尾（每 0.03s 生成一个）
                    _shadowDotSpawnTimer -= Time.deltaTime;
                    if (_shadowDotSpawnTimer <= 0f)
                    {
                        _shadowDotSpawnTimer = ShadowDotSpawnInterval;
                        SpawnShadowDot();
                    }
                }
                if (HitWall())
                {
                    // 顶墙停下，但冲刺动画继续播完
                    X -= dx;
                }
                else
                {
                    // 冲刺可上坡：前方坡面高于脚底时，跟着坡面抬升
                    float gy = DashFollowGround();
                    if (!float.IsNaN(gy) && gy < Y + SizeY - 0.05f)
                    {
                        Y = gy - SizeY;
                    }
                }
                // 护符33 锋利之影：暗影冲刺穿过敌人造成当前骨钉伤害
                CheckShadowDashEnemyHit();
                _dashTimer -= Time.deltaTime;
                if (_dashTimer <= 0f)
                {
                    _dashing = false;
                    // 冲刺到达最大位移：触发冲刺劈砍
                    if (_dashSlashPending)
                    {
                        StartDashSlash();
                    }
                    // 冲刺结束：记录冲刺速度，由 else 分支首帧按玩家输入决定最终状态
                    _dashJustEnded = true;
                    _dashExitVx = _dashDir * dashSpeed;
                    // 最后一帧已生成：开始整条拖尾按序淡出（从第1帧起，0.3s 内全部消失）
                    _ghostFading = _ghosts.Count > 0;
                    _ghostFadeTimer = 0f;
                    _ghostFadeTotal = _ghosts.Count;
                    // 冲刺结束：黑色粒子开始按生成顺序整体淡出
                    _shadowDotFading = _shadowParticles.Count > 0;
                    _shadowDotFadeTimer = 0f;
                    _shadowDotFadeTotal = _shadowParticles.Count;
                }
                }
            }
            else
            {
                if (_pendingReposition > 0)
                {
                    // 重定位跟随：Vx/Vy 保持诺艾尔物理速度，不被玩家输入覆盖
                }
                else if (_dashJustEnded)
                {
                    // 冲刺结束首帧，按玩家输入决定最终状态：
                    // 方式 A（未按方向键）→ Vx 立即归零，动画由 movingNow 自然回到待机/空中；
                    // 方式 B（按住方向键）→ 保留冲刺速度，随后由 move * WalkSpeed 自然接管
                    _dashJustEnded = false;
                    Vx = move == 0f ? 0f : _dashExitVx;
                }
                else
                {
                    // 旋风劈砍：仅慢速左右移动；液面漂浮：移动速度略低于走路
                    // 护符8 飞毛腿：仅普通走路速度 +20%（游泳/旋风/冲刺/蹬墙漂移等不受影响）；
                    // 羁绊：冲刺大师 + 飞毛腿 —— 移动速度加成提升为 40%
                    bool runner = CharmEffects.IsEquipped(CharmEffects.RunnerId);
                    float walkSpeed = WalkSpeed *
                        (runner
                            ? (CharmEffects.IsEquipped(CharmEffects.DashmasterId) ? 1.4f : 1.2f)
                            : 1f);
                    // 护符34 乌恩之形：回血（蛞蝓形态）时移动速度降低 25%；
                    // 羁绊：快速聚集 + 乌恩之形 —— 乌恩形态移动速度提升 50%
                    if (_focusing && CharmEffects.IsEquipped(CharmEffects.UnnId))
                    {
                        walkSpeed *= 0.75f;
                        if (CharmEffects.IsEquipped(CharmEffects.FastGatherId))
                        {
                            walkSpeed *= 1.5f;
                        }
                    }
                    Vx = move * (_cycloneSlashing ? CycloneMoveSpeed : (_swimState == 1 ? SwimSurfaceSpeed : walkSpeed));
                }

                if (jump && Grounded)
                {
                    Vy = JumpVy;
                    Grounded = false;
                    _jumpHeld = true;
                    _jumpCutApplied = false;
                }
                else if (jump && !Grounded)
                {
                    float away = _wallDir;
                    if (_onWall || TouchingWallNow(out away))
                    {
                        // 蹬墙跳（官方）：约45°斜上；水平以 WallJumpVx 起步、随时间线性衰减；
                        // 0.1 秒后输入可取消剩余向外速度（拉回贴墙）；纵向与普通跳跃相同（含松开削减）。
                        // 触碰墙壁即使未攀附，按下跳跃键也会进行蹬墙跳。
                        _onWall = false;
                        _wallJumpDrift = false;
                        // 先向外挪一小段，脱离墙格边缘，避免 HitWall() 把第一帧的弹墙位移误判吞掉
                        X -= away * 0.05f;
                        Vx = -away * WallJumpVx;
                        _wallKickVx = -away * WallJumpVx;
                        _wallKickDuration = WallJumpKickTime;
                        _wallKickTimer = WallJumpKickTime;
                        Vy = JumpVy;
                        _jumpHeld = true;
                        _jumpCutApplied = false;
                        _wallJumping = true;
                        _wallJumpTimer = GetClipDuration("Walljump");
                    }
                    else if (_canDoubleJump)
                    {
                        // 二段跳：先极轻下沉（反冲），下沉结束后再转向上初速
                        _doubleJumpDipTimer = DoubleJumpDipTime;
                        Vy = 0.01f; // 极短时间的轻微向下速度
                        _jumpHeld = false; // 下沉阶段不参与跳跃高度削减
                        _jumpCutApplied = false;
                        _canDoubleJump = false; // 防止无限连跳
                        _doubleJumping = true;
                        _doubleJumpTimer = GetClipDuration("Double Jump");
                        // 二段跳专属：白色光翼 + 翅膀窗口内按间隔散射光点 + 翅膀音效
                        _wingTimer = WingFxTime;
                        _dotSpawnTimer = 0f; // 立即生成第一个
                        DashAudio.PlayWingFlap();
                    }
                }
                else if (_jumpHeld)
                {
                    // 松开跳跃键且还在上升：削减上升速度，点按=矮跳、按住=满跳
                    if (!jumpHeldNow && Vy < 0f && !_jumpCutApplied)
                    {
                        Vy *= JumpCut;
                        _jumpCutApplied = true;
                    }
                    // 开始下落或已经落地：本次跳跃结束
                    if (Vy >= 0f || Grounded)
                    {
                        _jumpHeld = false;
                    }
                }

                if (_pogoGravityLock > 0f)
                {
                    // Pogo 弹射瞬间屏蔽重力：速度保持弹跳初速，弹出去干脆有力
                    _pogoGravityLock -= Time.deltaTime;
                    if (_pogoGravityLock < 0f)
                    {
                        _pogoGravityLock = 0f;
                    }
                }
                else if (_superInertiaTimer > 0f)
                {
                    // 超级冲刺停止后的惯性飞行（官方设定）：短暂保持飞行、不受重力，
                    // 让 air_break 刹车动画完整播放，随后才恢复正常下坠
                    Vy = 0f;
                }
                else if (_swimState == 1 || _swimRising)
                {
                    Vy = 0f; // 液面漂浮 / 水下上浮：不受重力
                }
                else if (_nailArtSlashing && !Grounded)
                {
                    Vy = NailArtFallSpeed; // 空中强力劈砍：缓慢下落
                }
                else if (_dashSlashing && !Grounded)
                {
                    Vy = DashSlashFallSpeed; // 空中冲刺劈砍：缓慢下落
                }
                else
                {
                    Vy += Gravity * dt;
                }
                // 下落速度上限：旋风劈砍默认取消上限（快速下坠）；按下攻击键后的短暂窗口内限速 4.9
                if (_cycloneSlashing)
                {
                    if (_cycloneSlowFallTimer > 0f)
                    {
                        _cycloneSlowFallTimer -= Time.deltaTime;
                        if (_cycloneSlowFallTimer < 0f)
                        {
                            _cycloneSlowFallTimer = 0f;
                        }
                        if (Vy > CycloneSlowFallMax)
                        {
                            Vy = CycloneSlowFallMax;
                        }
                    }
                }
                else if (Vy > FallMax)
                {
                    Vy = FallMax;
                }
                // 行为 B：漂移持续到跳跃最高点（Vy 由升转降）即停止
                if (_wallJumpDrift && Vy >= 0f)
                {
                    _wallJumpDrift = false;
                }
                // 挂墙：下落时恒定速度下滑；沿墙小跳（Vy<0 上升）时不钳制，可沿墙弹起；
                // 超级冲刺撞墙停顿期间不滑动
                if (_onWall && _superWallHitTimer <= 0f && Vy >= 0f)
                {
                    Vy = WallSlideSpeed;
                }

                // 二段跳下沉阶段结束：转为向上初速，并重新启用按住/松开控制高度
                if (_doubleJumpDipTimer > 0f)
                {
                    _doubleJumpDipTimer -= Time.deltaTime;
                    if (_doubleJumpDipTimer <= 0f)
                    {
                        _doubleJumpDipTimer = 0f;
                        Vy = JumpVy;
                        _jumpHeld = true;
                        _jumpCutApplied = false;
                    }
                }

                // 墙壁跳水平推力：
                // 行为 A：小跳推力线性衰减期间覆盖行走速度；
                // 行为 B：以走路速度向远离墙方向持续漂移，直到跳跃最高点/落地/再抓墙/玩家输入接管
                if (_wallKickTimer > 0f)
                {
                    _wallKickTimer -= Time.deltaTime;
                    // 0.1 秒后玩家输入可取消剩余的向外速度（拉回贴墙准备再次蹬墙跳）
                    float kickElapsed = _wallKickDuration - _wallKickTimer;
                    if (move != 0f && kickElapsed >= WallJumpCancelTime)
                    {
                        _wallKickTimer = 0f;
                        _wallKickVx = 0f;
                    }
                    else
                    {
                        float kickLeft = Mathf.Max(_wallKickTimer, 0f);
                        Vx = _wallKickVx * (kickLeft / _wallKickDuration);
                        if (_wallKickTimer <= 0f)
                        {
                            _wallKickVx = 0f;
                        }
                    }
                }
                else if (_wallJumpDrift)
                {
                    if (move != 0f)
                    {
                        _wallJumpDrift = false; // 玩家按下方向键后由正常输入接管
                    }
                    else
                    {
                        Vx = -_wallDir * WalkSpeed; // 保持走路速度远离墙
                    }
                }

                // 超级冲刺停止后的惯性：随玩家输入自然接管；期间不自动攀附
                if (_superInertiaTimer > 0f)
                {
                    _superInertiaTimer -= Time.deltaTime;
                    if (_superInertiaTimer <= 0f)
                    {
                        _superInertiaTimer = 0f;
                    }
                    float t = Mathf.Clamp01(_superInertiaTimer / SuperInertiaTime);
                    if (move != 0f)
                    {
                        Vx = move * WalkSpeed;
                    }
                    else
                    {
                        Vx = _superInertiaDir * SuperInertiaSpeed * t;
                    }
                }

                // 水平移动（细分步进，防止高速/冲刺瞬间穿过薄墙）
                float hMove = Vx * dt;
                int hSteps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(hMove) / 0.2f));
                float hStepDx = hMove / hSteps;
                for (int hs = 0; hs < hSteps; hs++)
                {
                    X += hStepDx;
                    if (HitWall())
                    {
                        X -= hStepDx;
                        Vx = 0f;
                        _wallKickTimer = 0f; // 撞到另一侧墙时立即停止水平推力
                        _wallKickVx = 0f;
                        _wallJumpDrift = false;
                        break;
                    }
                }

                // 横劈后坐力：线性衰减的短促后退，撞墙则停
                if (_recoilTimer > 0f)
                {
                    _recoilTimer -= Time.deltaTime;
                    float tLeft = Mathf.Max(_recoilTimer, 0f);
                    float rv = _recoilVx * (tLeft / RecoilTime); // 细胞/秒，衰减到 0
                    X += rv * Time.deltaTime;
                    if (HitWall())
                    {
                        X -= rv * Time.deltaTime;
                    }
                    if (_recoilTimer <= 0f)
                    {
                        _recoilVx = 0f;
                    }
                }

                // 爬墙（螳螂爪）：网格预检（IsBlockCell），不依赖 HitWall()。
                // 只在空中下落（Vy > 0，本模组约定：负=上升、正=下落）时允许进入，避免平地误触发。
                if (_swimState == 0 && !Grounded && (left || right) && Vy >= 0f && _superInertiaTimer <= 0f)
                {
                    // 墙所在列：骑士外侧相邻的一格（左边缘外一列 / 右边缘外一列）
                    int checkCx = move < 0f
                        ? Mathf.FloorToInt(X + CollideX0) - 1
                        : Mathf.FloorToInt(X + CollideX1) + 1;
                    // 距离阈值：骑士碰撞箱边缘到墙面距离 < 0.25 格才允许吸附，防远距离磁吸
                    float edge = move < 0f ? X + CollideX0 : X + CollideX1;
                    float wallFace = move < 0f ? checkCx + 1f : checkCx;
                    float distToWall = move < 0f ? edge - wallFace : wallFace - edge;
                    // 脚底 / 身体 / 头顶 三个高度
                    int cyBot = Mathf.FloorToInt(Y + CollideY1);
                    int cyMid = Mathf.FloorToInt(Y + (CollideTopY + CollideY1) * 0.5f);
                    int cyTop = Mathf.FloorToInt(Y + CollideTopY);
                    if (distToWall < WallGripDistance &&
                        (IsBlockCell(checkCx, cyBot) || IsBlockCell(checkCx, cyMid) || IsBlockCell(checkCx, cyTop)))
                    {
                        _onWall = true;
                        // 爬墙吸附成功：立即终止二段跳动画，让爬墙动画接管
                        _doubleJumping = false;
                        _doubleJumpTimer = 0f;
                        // 刷新：爬墙吸附成功恢复空中冲刺和二段跳资格（Reset 机制）
                        _canDoubleJump = true;
                        _canDash = true;
                        _dashCooldown = 0f;
                        _wallJumpDrift = false; // 重新抓墙时结束行为 B 的漂移
                        _wallDir = move; // 撞墙方向（1=墙在右，-1=墙在左）
                        // 爬墙原图统一左右镜像：左墙保持现状（镜像），右墙按要求改为镜像
                        _faceDir = -1f;
                        // 强制吸附到实心格边缘（无视美术皮缝隙）
                        if (move < 0f)
                        {
                            X = checkCx + 1 - CollideX0; // 左墙：左边缘对齐 checkCx+1
                        }
                        else
                        {
                            X = checkCx - CollideX1;     // 右墙：右边缘对齐 checkCx
                        }
                        Vx = 0f;
                    }
                }
                if (_onWall)
                {
                    Vx = 0f;
                    if (_superWallHitTimer > 0f)
                    {
                        // 超级冲刺撞墙停顿：0.5s 内钉在撞击点不滑动（动画由选择器播撞墙帧）
                        _superWallHitTimer -= Time.deltaTime;
                        Vy = 0f;
                    }
                    else
                    {
                        // 维持：落地、按下反方向键（离开墙）、或轻按一次下键（脱离爬墙）都退出；
                        // 松开方向键依然挂墙下滑（不需要一直按着）
                        bool goingAway = (left && _wallDir > 0f) || (right && _wallDir < 0f);
                        if (Grounded || goingAway || downPressed)
                        {
                            _onWall = false;
                        }
                        else
                        {
                            // 下半身脱离墙壁后取消攀附（官方）：脚底/身体不再贴墙即退出
                            int checkCx = _wallDir < 0f
                                ? Mathf.FloorToInt(X + CollideX0) - 1
                                : Mathf.FloorToInt(X + CollideX1) + 1;
                            int cyBot = Mathf.FloorToInt(Y + CollideY1);
                            int cyMid = Mathf.FloorToInt(Y + (CollideTopY + CollideY1) * 0.5f);
                            if (!(IsBlockCell(checkCx, cyBot) || IsBlockCell(checkCx, cyMid)))
                            {
                                _onWall = false;
                            }
                        }
                    }
                }

                // 垂直移动
                float prevFeet = Y + SizeY;
                if (_jumperBounceLock > 0f)
                {
                    _jumperBounceLock -= Time.deltaTime;
                    if (_jumperBounceLock < 0f)
                    {
                        _jumperBounceLock = 0f;
                    }
                }
                Y += Vy * dt;
                if (Vy < 0f)
                {
                    if (HitCeil())
                    {
                        Y -= Vy * dt;
                        Vy = 0f;
                    }
                }
                // Pogo 发射期间（重力锁定窗口）跳过落地：下劈陷阱/怪的回弹初速
                // 不被同帧的“贴近地面就落地”覆盖（否则贴地陷阱/怪劈下去弹不起来）
                else if (!_swimRising && _superInertiaTimer <= 0f && _pogoGravityLock <= 0f &&
                         CheckGround(out float groundTop, prevFeet))
                {
                    Y = groundTop - SizeY;
                    if (_groundIsJumper != null && _jumperBounceLock <= 0f)
                    {
                        // 弹簧地板：落地即弹起（与原版 JumperBoard 行为一致）
                        Vy = JumperBoardBounceVy;
                        _jumperBounceLock = 0.3f;
                    }
                    else
                    {
                        Vy = 0f;
                    }
                    Grounded = true;
                    // 记录最后安全站立点（掉出地图时兜底回传）
                    _lastSafeX = X;
                    _lastSafeY = Y;
                    // 落地重置二段跳资格，并结束二段跳动画状态
                    _canDoubleJump = true;
                    _canDash = true; // 落地重置冲刺次数
                    _doubleJumping = false;
                    _onWall = false;
                    _wallJumpDrift = false; // 落地结束行为 B 的漂移
                }
                else
                {
                    Grounded = false;
                }

                // 掉出地图兜底：脚底低于地图底部 1 格以上时，回到最后安全点
                if (_mp != null && Y + SizeY > _mp.rows + 1f)
                {
                    X = _lastSafeX;
                    Y = _lastSafeY;
                    Vx = 0f;
                    Vy = 0f;
                    Grounded = true;
                }

            }

            SitPhysicsSkipped:

            // 低血量（1 格）：黑色虚空粒子从身上冒出
            if (!_isDead && !_respawnFadeOut && _health == 1)
            {
                _lowHpSpawnTimer -= Time.deltaTime;
                if (_lowHpSpawnTimer <= 0f)
                {
                    _lowHpSpawnTimer = LowHpParticleInterval;
                    _lowHpParticles.Add(new LightDotParticle
                    {
                        X = X + (UnityEngine.Random.value - 0.5f) * 0.6f,
                        Y = Y + SizeY * 0.55f + (UnityEngine.Random.value - 0.5f) * 0.4f,
                        Vx = (UnityEngine.Random.value - 0.5f) * 0.04f,
                        Vy = -UnityEngine.Random.Range(0.02f, 0.08f), // 向上冒出（y 负=上）
                        TexIndex = 1,
                        Age = 0f,
                        Life = UnityEngine.Random.Range(0.5f, 0.8f),
                        Size = UnityEngine.Random.Range(0.12f, 0.22f),
                        Index = 0
                    });
                }
            }
            else
            {
                _lowHpParticles.Clear();
            }
            // 亡者之怒：身后红闪脉冲计时
            if (FuryActive)
            {
                _furyGlowTimer += Time.deltaTime;
            }
            // 生命血羁绊：身后蓝闪脉冲计时（亡者之怒激活时暂停，恢复后继续）
            if (LifebloodBondActive() && !FuryActive)
            {
                _lifebloodGlowTimer += Time.deltaTime;
            }
            for (int li = _lowHpParticles.Count - 1; li >= 0; li--)
            {
                LightDotParticle d = _lowHpParticles[li];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
                if (d.Age >= d.Life)
                {
                    _lowHpParticles.RemoveAt(li);
                }
            }

            // 受击粒子爆发：独立物理飘散后自动消失
            for (int hi = _hitFxParticles.Count - 1; hi >= 0; hi--)
            {
                LightDotParticle d = _hitFxParticles[hi];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
                if (d.Age >= d.Life)
                {
                    _hitFxParticles.RemoveAt(hi);
                }
            }

            // 落地黑/白粒子：独立物理飘散后自动消失
            for (int di = _diveLandParticles.Count - 1; di >= 0; di--)
            {
                LightDotParticle d = _diveLandParticles[di];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
                if (d.Age >= d.Life)
                {
                    _diveLandParticles.RemoveAt(di);
                }
            }

            // 下砸骨剑/尖刺：独立于下砸状态，始终推进动画（尖啸/法术期间也走完）
            UpdateDiveSwordsAndSpikes(Time.deltaTime);

            // 尖啸黑/白粒子：独立于尖啸状态，始终飘散直到消失
            for (int si = _screamParticles.Count - 1; si >= 0; si--)
            {
                LightDotParticle d = _screamParticles[si];
                float pdt = Time.deltaTime;
                d.Age += pdt;
                // 运动一段距离后大幅减速（一次触发，之后保持慢速漂移）
                d.TravelDist += Mathf.Sqrt(d.Vx * d.Vx + d.Vy * d.Vy) * pdt;
                if (!d.Braked && d.TravelDist >= d.BrakeDist)
                {
                    d.Braked = true;
                    d.Vx *= ScreamParticleBrakeFactor;
                    d.Vy *= ScreamParticleBrakeFactor;
                }
                d.X += d.Vx * pdt;
                d.Y += d.Vy * pdt;
                if (d.Age >= d.Life)
                {
                    _screamParticles.RemoveAt(si);
                }
            }

            // 蓄力白色粒子：继承小骑士当前速度（相对速度不变），同时以恒定速度向中心偏下 0.5 格收敛
            for (int ni = _nailArtChargeParticles.Count - 1; ni >= 0; ni--)
            {
                LightDotParticle d = _nailArtChargeParticles[ni];
                float pdt = Time.deltaTime;
                d.Age += pdt;
                d.TargetX = X;
                d.TargetY = Y + 0.5f;
                float dx = d.TargetX - d.X;
                float dy = d.TargetY - d.Y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < 0.08f || d.Age >= d.Life)
                {
                    _nailArtChargeParticles.RemoveAt(ni);
                    continue;
                }
                // 骑士速度（格/秒）：Vx/Vy 为格/帧@60，×60 换算
                float kx = Vx * 60f;
                float ky = Vy * 60f;
                float inv = d.Speed / dist;
                d.Vx = dx * inv + kx;
                d.Vy = dy * inv + ky;
                d.X += d.Vx * pdt;
                d.Y += d.Vy * pdt;
            }

            // 梦钉命中白色粒子：独立推进（远离中心、减速、速度到 0 淡出）
            UpdateDreamParticles();
            // 梦钉挥击光尘：限定范围内缓慢飘荡
            UpdateDreamSwingParticles();

            // 暗影充能：充能期间播放充能特效，充能完毕特效消失
            if (_shadowRechargeTimer > 0f)
            {
                PlayRechargeClip("ShadowRecharge");
                _shadowRechargeTimer -= Time.deltaTime;
                if (_shadowRechargeTimer <= 0f)
                {
                    _shadowRechargeTimer = 0f;
                    _rcTex = null;
                }
            }
            else
            {
                _rcTex = null;
            }

            // 拖尾整体淡出：最后帧生成后开始计时，按生成顺序从第1帧起依次消失
            if (_ghostFading)
            {
                _ghostFadeTimer += Time.deltaTime;
                float step = _ghostFadeTotal > 0 ? GhostLife / _ghostFadeTotal : GhostLife;
                for (int i = _ghosts.Count - 1; i >= 0; i--)
                {
                    if (_ghostFadeTimer > (_ghosts[i].Index + 1) * step)
                    {
                        _ghosts.RemoveAt(i);
                    }
                }
                if (_ghosts.Count == 0)
                {
                    _ghostFading = false;
                }
            }
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                _ghosts[i].Age += Time.deltaTime; // 用于淡入，避免残影硬弹出
            }

            // 残影分配给出渲染节点
            for (int i = 0; i < _ghostNodes.Count; i++)
            {
                _ghostNodes[i].Ghost = i < _ghosts.Count ? _ghosts[i] : null;
            }

            // 二段跳白色光翼：0.2 秒内淡出，期间按间隔生成散射光点
            if (_wingTimer > 0f)
            {
                _wingTimer -= Time.deltaTime;
                if (_wingTimer < 0f)
                {
                    _wingTimer = 0f;
                }
                // 翅膀特效存在的 0.2 秒窗口内，按间隔生成飘落白粒
                _dotSpawnTimer -= Time.deltaTime;
                if (_dotSpawnTimer <= 0f)
                {
                    _dotSpawnTimer = LightDotSpawnInterval;
                    SpawnLightDot();
                }
            }

            // 二段跳散射光点：独立物理下坠，生命周期 0.4~0.6 秒，到期自动销毁
            for (int i = _dots.Count - 1; i >= 0; i--)
            {
                LightDotParticle d = _dots[i];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
                if (d.Age >= d.Life)
                {
                    _dots.RemoveAt(i);
                }
            }

            // 暗影冲刺黑色粒子：冲刺期间一直保留，冲刺结束后按生成顺序整体淡出
            for (int i = _shadowParticles.Count - 1; i >= 0; i--)
            {
                LightDotParticle d = _shadowParticles[i];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
            }

            // 凝聚白色条状线：自下往上升起，寿命结束移除
            for (int i = _focusParticles.Count - 1; i >= 0; i--)
            {
                LightDotParticle d = _focusParticles[i];
                d.Age += Time.deltaTime;
                d.X += d.Vx * Time.deltaTime;
                d.Y += d.Vy * Time.deltaTime;
                if (d.Age >= d.Life)
                {
                    _focusParticles.RemoveAt(i);
                }
            }

            // 水晶之心：一次性特效（发射爆发 / 撞击碎裂）帧推进
            UpdateSuperFx();
            // 紫色收缩光圈：随时间老化，寿命结束移除
            for (int i = _sdRings.Count - 1; i >= 0; i--)
            {
                SdRing ring = _sdRings[i];
                ring.Age += Time.deltaTime;
                if (ring.Age >= ring.Life)
                {
                    _sdRings.RemoveAt(i);
                }
            }
            if (_shadowDotFading)
            {
                _shadowDotFadeTimer += Time.deltaTime;
                // 先等 ShadowDotFadeDelay 秒保持全量可见，再开始按序销毁
                if (_shadowDotFadeTimer >= ShadowDotFadeDelay)
                {
                    float t = _shadowDotFadeTimer - ShadowDotFadeDelay;
                    float step = _shadowDotFadeTotal > 0 ? ShadowDotFadeTime / _shadowDotFadeTotal : ShadowDotFadeTime;
                    for (int i = _shadowParticles.Count - 1; i >= 0; i--)
                    {
                        if (t > (_shadowParticles[i].Index + 1) * step)
                        {
                            _shadowParticles.RemoveAt(i);
                        }
                    }
                    if (_shadowParticles.Count == 0)
                    {
                        _shadowDotFading = false;
                    }
                }
            }
            }
            catch (Exception)
            {
            }

            // ---- 过渡动画（参考空洞骑士 HeroAnimationController）----
            bool movingNow = Mathf.Abs(Vx) > 0.01f;
            if (Grounded && !_prevGrounded)
            {
                _transitionClip = "Land";
                _transitionTimer = GetClipDuration("Land");
            }
            else if (Grounded && _wasMoving && !movingNow && _currentClip == "Run")
            {
                _transitionClip = "Run To Idle";
                _transitionTimer = GetClipDuration("Run To Idle");
            }
            _prevGrounded = Grounded;
            _wasMoving = movingNow;

            string clip;
            bool lookUpHeld = KeyConfig.GetHeld(KnightInCradlePlugin.LookUpKey, KeyCode.Q);
            bool lookDownHeld = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);

            if (_isDead)
            {
                // 死亡动画（黑屏淡入阶段播放，之后黑屏复活）
                clip = "Death";
            }
            else if (_hurt)
            {
                // 受伤硬直：原版 Recoil（stun 帧）
                clip = "Recoil";
            }
            else if (_respawnWaking)
            {
                // 复活后长椅上苏醒动画，播完回到坐姿
                _respawnWakeTimer -= Time.deltaTime;
                clip = "Wake To Sit";
                if (_respawnWakeTimer <= 0f)
                {
                    _respawnWakeTimer = 0f;
                    _respawnWaking = false;
                }
            }
            else if (_respawnFadeOut)
            {
                // 复活后黑屏淡出（无长椅时站立待机）
                clip = "Idle";
            }
            else if (_isSitting || _sitStandingUp)
            {
                // 坐长椅：先播坐下的过渡动画（HK 原版 Sit，约 0.3s），之后循环 Sit Idle；
                // 起身时播 HK 原版 Get Off（约 0.42s），播完自动恢复普通控制。
                if (_sitStandingUp)
                {
                    _sitStandTimer -= Time.deltaTime;
                    if (_sitStandTimer <= 0f)
                    {
                        _sitStandingUp = false;
                    }
                }
                clip = _sitStandingUp ? "Get Off" : (_sitTimer < GetClipDuration("Sit") ? "Sit" : "Sit Idle");
            }
            else if (_taunting)
            {
                // 挑衅：开场→循环 3 次→收尾，播完由 _tauntTimer 结束并恢复站立
                clip = _clips.ContainsKey("Challenge") ? "Challenge" : "Idle";
            }
            else if (_focusing)
            {
                // 凝聚三阶段按帧驱动：
                // 发动 focus_v020000~0002（0.27s）/ 回血 focus_v020003~0006（0.82s）/
                // 结束 focus_v020007~0011（0.23s，硬直）
                if (CharmEffects.IsEquipped(CharmEffects.UnnId))
                {
                    // 羁绊：乌恩之形 + 蘑菇孢子 —— 替换成蘑菇蛞蝓动画
                    bool mushUnn = CharmEffects.IsEquipped(CharmEffects.MushroomId) &&
                        _clips.ContainsKey("MushUnnTransform");
                    // 护符34 乌恩之形：变身（发动）→ 待机/行走（回血，可移动）→ 爆发
                    if (_focusPhase == 0)
                    {
                        clip = mushUnn ? "MushUnnTransform" : "UnnTransform";
                    }
                    else if (_focusPhase == 1)
                    {
                        if (_focusTimer > FocusBurstTimeNow())
                        {
                            bool moving = Mathf.Abs(Vx) > 0.01f;
                            if (mushUnn)
                            {
                                clip = moving && _clips.ContainsKey("MushUnnWalk")
                                    ? "MushUnnWalk"
                                    : (_clips.ContainsKey("MushUnnIdle") ? "MushUnnIdle" : "Focus");
                            }
                            else
                            {
                                clip = moving && _clips.ContainsKey("UnnWalk")
                                    ? "UnnWalk"
                                    : (_clips.ContainsKey("UnnIdle") ? "UnnIdle" : "Focus");
                            }
                        }
                        else
                        {
                            clip = mushUnn
                                ? (_clips.ContainsKey("MushUnnBurst") ? "MushUnnBurst" : "Focus")
                                : (_clips.ContainsKey("UnnBurst") ? "UnnBurst" : "Focus");
                        }
                    }
                    else
                    {
                        clip = mushUnn
                            ? (_clips.ContainsKey("MushUnnIdle") ? "MushUnnIdle" : "Focus")
                            : (_clips.ContainsKey("UnnIdle") ? "UnnIdle" : "Focus");
                    }
                }
                else if (_focusPhase == 0 && _clips.ContainsKey("FocusStart"))
                {
                    clip = "FocusStart";
                }
                else if (_focusPhase == 1)
                {
                    // 回血段：首次播 0003~0006（0.82s）/ 后续播 0003~0006×2（0.8s）；
                    // 爆发段：0007~0010（0.25s，更快）
                    if (_focusTimer > FocusBurstTimeNow())
                    {
                        // 26 快速聚集（快）/ 27 深度聚集（慢）：按佩戴组合选对应帧率剪辑
                        bool fastGather = CharmEffects.IsEquipped(CharmEffects.FastGatherId);
                        bool deepGather = CharmEffects.IsEquipped(CharmEffects.DeepGatherId);
                        string suffix = _focusFirstCycle ? "" : "Next";
                        string variant = fastGather && deepGather ? "Both"
                            : fastGather ? "Fast"
                            : deepGather ? "Deep" : "";
                        string healClip = "FocusHeal" + suffix + variant;
                        clip = _clips.ContainsKey(healClip) ? healClip : "Focus";
                    }
                    else
                    {
                        clip = _clips.ContainsKey("FocusHealBurst") ? "FocusHealBurst" : "Focus";
                    }
                }
                else
                {
                    clip = "Focus";
                }
            }
            else if (_diving)
            {
                // 黑暗降临：前摇 / 下落 / 落地
                if (_divePhase == 0)
                {
                    clip = _clips.ContainsKey("Dive Antic") ? "Dive Antic" : "Idle";
                }
                else if (_divePhase == 1)
                {
                    clip = _clips.ContainsKey("Dive Fall") ? "Dive Fall" : "Airborne";
                }
                else
                {
                    clip = _clips.ContainsKey("Dive Land") ? "Dive Land" : "Land";
                }
            }
            else if (_screaming)
            {
                // 深渊尖啸施法动画
                clip = _clips.ContainsKey("Scream Cast") ? "Scream Cast" : "Idle";
            }
            else if (_voidPhase >= 2)
            {
                // 虚空解放：前摇/出伤/后摇动画（帧由 UpdateVoidLiberation 精确驱动）
                string vc = _voidPhase == 2
                    ? "VoidEnter"
                    : _voidPhase == 3 ? "VoidScream" : "VoidExit";
                clip = _clips.ContainsKey(vc) ? vc : "Idle";
            }
            else if (_fireballCasting)
            {
                // 暗影之魂施法动画
                clip = _clips.ContainsKey("Fireball Cast") ? "Fireball Cast" : "Idle";
            }
            else if (_swimState == 1)
            {
                // 液面：入水过渡 → 游动 / 待机
                if (_swimEnterTimer > 0f)
                {
                    clip = _clips.ContainsKey("Water Enter") ? "Water Enter" : "Water Surface Idle";
                }
                else if (movingNow)
                {
                    clip = _clips.ContainsKey("Water Surface Swim") ? "Water Surface Swim" : "Water Surface Idle";
                }
                else
                {
                    clip = _clips.ContainsKey("Water Surface Idle") ? "Water Surface Idle" : "Idle";
                }
            }
            else if (_swimState == 2 && lookUpHeld)
            {
                // 液内按住上键：上浮，播放液面游动动画
                clip = _clips.ContainsKey("Water Surface Swim") ? "Water Surface Swim" : "Water Surface Idle";
            }
            else if (_nailArtSlashing)
            {
                // 强力劈砍动作
                clip = _clips.ContainsKey("Nail Art Slash") ? "Nail Art Slash" : "Idle";
            }
            else if (_dashSlashing)
            {
                // 冲刺劈砍动作：改用与蓄力劈砍相同的自身挥剑动画
                clip = _clips.ContainsKey("Nail Art Slash") ? "Nail Art Slash" : "Idle";
            }
            else if (_cycloneSlashing)
            {
                // 旋风劈砍动作（旋转循环）
                clip = _clips.ContainsKey("Cyclone Slash") ? "Cyclone Slash" : "Idle";
            }
            else if (_dreamNailing)
            {
                // 梦钉：前摇 0000~0015 / 抽出 0016~0025
                clip = _dreamPhase == 0
                    ? (_clips.ContainsKey("Dream Nail Charge") ? "Dream Nail Charge" : "Idle")
                    : (_clips.ContainsKey("Dream Nail Swing") ? "Dream Nail Swing" : "Idle");
            }
            else if (_superCharging)
            {
                // 蓄力：地面用 SD Charge Ground；墙上按墙侧选 SD Wall Charge / SD Wall Charge Right
                if (_onWall)
                {
                    string wc = _wallDir > 0f ? "SD Wall Charge Right" : "SD Wall Charge";
                    clip = _clips.ContainsKey(wc) ? wc : "Idle";
                }
                else
                {
                    clip = _clips.ContainsKey("SD Charge Ground") ? "SD Charge Ground" : "Idle";
                }
            }
            else if (_superDashing)
            {
                // 飞行：水晶升腾只播 superdash0013（顺时针旋转90°）；横向冲刺播 0013/0014 两帧循环
                clip = _superUpDash
                    ? (_clips.ContainsKey("SuperDashUp") ? "SuperDashUp" : "SuperDashBody")
                    : (_clips.ContainsKey("SuperDashBody") ? "SuperDashBody" : "SD Dash");
            }
            else if (_superBrakeTimer > 0f)
            {
                // 主动停止：空中刹车动画（播完回正常状态）
                if (Grounded)
                {
                    // 落地立即结束刹车与惯性
                    _superBrakeTimer = 0f;
                    _superInertiaTimer = 0f;
                    clip = "Land";
                }
                else
                {
                    _superBrakeTimer -= Time.deltaTime;
                    clip = _clips.ContainsKey("SD Air Brake") ? "SD Air Brake" : "Airborne";
                }
            }
            else if (_superUnchargeTimer > 0f)
            {
                // 取消蓄力：起身动画
                _superUnchargeTimer -= Time.deltaTime;
                clip = _clips.ContainsKey("SD Charge Ground End") ? "SD Charge Ground End" : "Idle";
            }
            else if (_dashing)
            {
                // 暗影冲刺：位移段播 0000~0008，终点静止段播 0009~0011
                clip = _isShadowDash
                    ? (_dashTimer > _dashEndTime
                        ? (_clips.ContainsKey("ShadowDashMove") ? "ShadowDashMove" : "Dash")
                        : (_clips.ContainsKey("ShadowDashEnd") ? "ShadowDashEnd" : "Dash"))
                    : "Dash";
            }
            else if (_attacking)
            {
                // 攻击动画：左右手交替（Slash / SlashAlt 两套帧）
                clip = _attackClipName;
            }
            else if (_wallJumping && _wallJumpTimer > 0f)
            {
                // 墙壁跳动画（空洞骑士原版 Walljump 剪辑），播完自动回 Airborne
                _wallJumpTimer -= Time.deltaTime;
                clip = "Walljump";
                if (_wallJumpTimer <= 0f)
                {
                    _wallJumping = false;
                }
            }
            else if (_doubleJumping && _doubleJumpTimer > 0f)
            {
                // 二段跳动画（空洞骑士原版 Double Jump 剪辑），播完自动回 Airborne
                _doubleJumpTimer -= Time.deltaTime;
                clip = "Double Jump";
                if (_doubleJumpTimer <= 0f)
                {
                    _doubleJumping = false;
                }
            }
            else if (_onWall && _superWallHitTimer > 0f)
            {
                // 超级冲刺撞墙停顿：按墙侧播 superdash_wall_hit 三帧
                string hitClip = _wallDir > 0f ? "SuperWallHit Right" : "SuperWallHit";
                clip = _clips.ContainsKey(hitClip) ? hitClip : "Wall Slide";
            }
            else if (_onWall)
            {
                // 爬墙滑行动画：左墙用原版 Wall Slide，右墙用左右镜像的 Wall Slide Right
                clip = _wallDir > 0f && _clips.ContainsKey("Wall Slide Right") ? "Wall Slide Right" : "Wall Slide";
            }
            else if (_focusEnding && _focusEndClip != null)
            {
                // 回血结束动画（无硬直）：播完自动回普通状态；被攻击/跳跃/移动等动作打断时让位
                clip = _focusEndClip;
            }
            else if (!Grounded)
            {
                clip = "Airborne";
            }
            else if (movingNow)
            {
                // 按用户要求：删除 Walk，所有移动都用 Run
                // 黑暗/雾天等视线遮蔽环境：替换为提灯行走 lantern_run
                clip = InLanternMode() && _clips.ContainsKey("Lantern Run") ? "Lantern Run" : "Run";
            }
            else if (lookUpHeld || lookDownHeld)
            {
                // 抬头/低头优先：立即响应，不被“跑→停”等过渡挡住
                clip = lookUpHeld ? "LookUp" : "LookDown";
            }
            else if (_transitionTimer > 0f)
            {
                _transitionTimer -= Time.deltaTime;
                clip = _transitionClip;
            }
            else if (_currentClip == "LookUp")
            {
                // 松开 Q：播放空洞骑士原版的“收尾”过渡（LookUpEnd），再回待机
                _transitionClip = "LookUpEnd";
                _transitionTimer = GetClipDuration("LookUpEnd");
                clip = _transitionClip;
            }
            else if (_currentClip == "LookDown")
            {
                _transitionClip = "LookDownEnd";
                _transitionTimer = GetClipDuration("LookDownEnd");
                clip = _transitionClip;
            }
            else
            {
                // 仅剩 1 格血：微微俯身喘气的待机（HK 原版 idle_low_health）
                // 黑暗/雾天等视线遮蔽环境：替换为提灯待机 lantern_idle
                // 亡者之怒：保持正常 idle_still，不触发低血量待机
                clip = InLanternMode() && _clips.ContainsKey("Lantern Idle")
                    ? "Lantern Idle"
                    : ((_health == 1 && !FuryActive && _clips.ContainsKey("LowHpIdle")) ? "LowHpIdle" : "Idle");
            }
            if (_dreamNailing && _dreamPhase == 0)
            {
                // 梦钉前摇：三段式精确帧动画（0~0.4/0.4~0.8/0.8~1.2s）
                PlayDreamNailChargeAnim();
            }
            else
            {
                PlayClip(clip);
            }
            // 虚空解放：以技能计时器精确覆盖当前帧（不受全局动画倍率影响）
            ApplyVoidLiberationFrame();
            if (_attacking)
            {
                // 剑气特效：
                //   平砍与本体动画（Attack / AttackAlt 左右手交替）同步交替剑气：
                //     Attack    → SlashEffect    （slashes_effect0000~0002）
                //     AttackAlt → SlashEffectAlt （slashes_effect0004~0006）
                //   修长之钉/骄傲印记（18/19）：换成螳螂爪样式（M 版剪辑）——
                //     横劈 mantis_slash_left0001~0002 / 0005~0006 交替，
                //     上劈 mantis_up_slash0000~0001，下劈 mantis_down_slash0001~0002
                //   亡者之怒（1 血）：换成 rage 版（F 版剪辑，优先级高于螳螂爪样式）
                bool mantisFx = CharmEffects.LongNailVisual();
                string fxClip;
                if (_upSlash)
                {
                    fxClip = FuryActive
                        ? "UpSlashEffect F"
                        : (mantisFx ? "UpSlashEffect M" : "UpSlashEffect");
                }
                else if (_downSlash)
                {
                    fxClip = FuryActive
                        ? "DownSlashEffect F"
                        : (mantisFx ? "DownSlashEffect M" : "DownSlashEffect");
                }
                else if (FuryActive)
                {
                    fxClip = _isRightSwing ? "SlashEffect F" : "SlashEffectAlt F";
                }
                else if (mantisFx)
                {
                    fxClip = _isRightSwing ? "SlashEffect M" : "SlashEffectAlt M";
                }
                else
                {
                    fxClip = _isRightSwing ? "SlashEffect" : "SlashEffectAlt";
                }
                PlayFxClip(fxClip);
            }
            else
            {
                _fxTex = null;
            }

        }

        /// <summary>
        /// 空中是否贴墙（未攀附也算）：返回墙体所在侧（-1=墙在左，+1=墙在右）。
        /// 用于“触碰墙壁即使并未攀附，按下跳跃键也会进行蹬墙跳”。
        /// </summary>
        private bool TouchingWallNow(out float wallDir)
        {
            wallDir = 0f;
            if (_mp == null)
            {
                return false;
            }
            int cxL = Mathf.FloorToInt(X + CollideX0) - 1;
            int cxR = Mathf.FloorToInt(X + CollideX1) + 1;
            int cyBot = Mathf.FloorToInt(Y + CollideY1);
            int cyMid = Mathf.FloorToInt(Y + (CollideTopY + CollideY1) * 0.5f);
            int cyTop = Mathf.FloorToInt(Y + CollideTopY);
            float distL = (X + CollideX0) - (cxL + 1f);
            float distR = cxR - (X + CollideX1);
            bool wallL = distL < WallGripDistance &&
                (IsBlockCell(cxL, cyBot) || IsBlockCell(cxL, cyMid) || IsBlockCell(cxL, cyTop));
            bool wallR = distR < WallGripDistance &&
                (IsBlockCell(cxR, cyBot) || IsBlockCell(cxR, cyMid) || IsBlockCell(cxR, cyTop));
            if (wallL && wallR)
            {
                wallDir = distL <= distR ? -1f : 1f;
            }
            else if (wallL)
            {
                wallDir = -1f;
            }
            else if (wallR)
            {
                wallDir = 1f;
            }
            return wallDir != 0f;
        }

        private bool HitWall()
        {
            if (_mp == null)
            {
                return false;
            }
            int cxL = Mathf.FloorToInt(X + CollideX0);
            int cxR = Mathf.FloorToInt(X + CollideX1);
            int cyTop = Mathf.FloorToInt(Y + CollideTopY);
            // 从头顶扫到脚底（Y+SizeY），覆盖整个身体（含腿部）；
            // IsBlockCell 本身会排除地板/单向平台，因此脚底那格不会误挡。
            // 旧版只扫到判定箱底部（Y+0.55），腿部（Y+0.55~Y+1.2）没有墙碰撞，
            // 遇到矮墙/墙角时骑士的腿会直接插进墙里然后掉下去。
            int cyBot = Mathf.FloorToInt(Y + SizeY);
            // 始终排除脚底行（站立表面，避免站墙顶/地板时把自己挡住）；
            // 腿部（判定箱底部 Y+0.55 到脚底 Y+1.2）已包含在扫描范围内
            int cyEnd = cyBot - 1;
            for (int cy = cyTop; cy <= cyEnd; cy++)
            {
                if (IsBlockCell(cxL, cy) || IsBlockCell(cxR, cy))
                {
                    return true;
                }
            }
            return false;
        }

        private bool HitCeil()
        {
            if (_mp == null)
            {
                return false;
            }
            // 头顶检测用“脚底 − CollideTop”（视觉头顶高度），而不是受击箱顶部 CollideY0：
            // 判定箱顶部（Y−0.52）比视觉头顶（约 Y−0.15）高 0.37 格，
            // 旧版会让小骑士“离天花板还有一段距离就顶头”。
            // 乌恩形态顶部更低：视觉头顶下移 0.8 格，蛞蝓可钻更矮的空间
            int cy = Mathf.FloorToInt(Y + SizeY - (CollideTop - _unnCollideShrink));
            int cxL = Mathf.FloorToInt(X + CollideX0);
            int cxR = Mathf.FloorToInt(X + CollideX1);
            return IsBlockCell(cxL, cy) || IsBlockCell(cxR, cy);
        }

        /// <summary>
        /// 冲刺时查找脚底附近的坡面：返回坡面 Y（世界坐标），找不到返回 NaN。
        /// 冲刺遇到上坡时用它贴坡爬升。
        /// </summary>
        private float DashFollowGround()
        {
            if (_mp == null)
            {
                return float.NaN;
            }
            float feet = Y + SizeY;
            // 从脚底上方 0.6 处向下搜索：只抓“贴脚”的坡面（一帧内升起的量）
            float qy = feet - 0.6f;
            const float marginx = 0.2f;
            const float marginy = 0.8f;
            float best = float.NaN;
            float[] xs = { X, X - 0.20f, X + 0.20f };

            try
            {
                if (_mp.BCC != null)
                {
                    for (int i = 0; i < xs.Length; i++)
                    {
                        BCCLine line;
                        float gy = _mp.BCC.isFallable(xs[i], qy, marginx, marginy, out line, true, !_skipLiftNow, -1f, null);
                        if (gy >= 0f && (float.IsNaN(best) || gy < best))
                        {
                            best = gy;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                int n = _mp.count_carryable_bcc;
                for (int i = 0; i < n; i++)
                {
                    M2BlockColliderContainer bcc = _mp.getCarryableBCCByIndex(i);
                    if (bcc == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < xs.Length; j++)
                    {
                        BCCLine line;
                        float gy = bcc.isFallable(xs[j], qy, marginx, marginy, out line, true, true, -1f, null);
                        if (_skipLiftNow && line != null && line.is_lift)
                        {
                            continue;
                        }
                        if (gy >= 0f && (float.IsNaN(best) || gy < best))
                        {
                            best = gy;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return best;
        }

        private bool CheckGround(out float groundTop, float prevFeet)
        {
            groundTop = Y + SizeY;
            if (_mp == null)
            {
                return false;
            }

            // 用游戏自己的 BCC 地面检测（能区分地板表面与墙壁，支持坡度/单向平台/移动平台）
            float feet = Y + SizeY;
            float gy = GetGroundY(prevFeet);
            if (!float.IsNaN(gy))
            {
                // 下落捕捉窗口：脚在表面上方 0.35 内或下方 0.55 内即接住
                if (feet >= gy - 0.35f && feet <= gy + 0.55f)
                {
                    groundTop = gy;
                    return true;
                }
            }
            // 诊断：矿井地图一直找不到地面时，打印脚底附近芯片，定位“弹簧地板”的真实类型
            else if (_mp.key != null && _mp.key.IndexOf("mine", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     Time.frameCount - _groundDiagFrame > 60)
            {
                _groundDiagFrame = Time.frameCount;
                try
                {
                    var diagList = new List<M2Puts>();
                    _mp.getAllPointMetaPutsTo(Mathf.FloorToInt(X) - 2, Mathf.FloorToInt(Y + SizeY) - 2,
                        5, 5, diagList, (M2Puts V, List<M2Puts> _L) => true);
                    var names = new List<string>();
                    for (int i = 0; i < diagList.Count && i < 24; i++)
                    {
                        string tn = diagList[i].GetType().Name;
                        names.Add(tn);
                    }
                }
                catch (Exception)
                {
                }
            }
            return false;
        }

        private static PRNoel GetPr()
        {
            NelM2DBase m2d = M2DBase.Instance as NelM2DBase;
            return m2d != null ? m2d.getPrNoel() : null;
        }

        private float GetGroundY()
        {
            return GetGroundY(float.NaN);
        }

        private float GetGroundY(float prevFeet)
        {
            if (_mp == null)
            {
                return float.NaN;
            }

            // 从“帧初脚底位置”再往上 0.30 处向下搜索：
            // 既能在下落时接住地板，也能接住一帧内向上移动的平台
            float feetRef = float.IsNaN(prevFeet) ? Y + SizeY : prevFeet;
            float qy = feetRef - 0.30f;
            const float marginx = 0.20f;
            const float marginy = 0.55f;

            float best = float.NaN;
            _groundSrc = "无";
            _groundIsLift = false;
            _groundIsJumper = null;
            NelChipJumperBoard bestJumper = null;

            float[] xs = { X - 0.20f, X, X + 0.20f };

            // 1) 地图静态地板 + 单向/升降平台（isFallable 会同时查 main 与 lift）
            try
            {
                if (_mp.BCC != null)
                {
                    for (int i = 0; i < xs.Length; i++)
                    {
                        BCCLine line;
                        // 按住“下”键时跳过单向平台线，只留实心地板
                        float gy = _mp.BCC.isFallable(xs[i], qy, marginx, marginy, out line, true, !_skipLiftNow, -1f, null);
                        if (gy >= 0f && (float.IsNaN(best) || gy < best))
                        {
                            best = gy;
                            _groundIsLift = line != null && line.is_lift;
                            _groundSrc = _groundIsLift ? "单向平台" : "静态地板";
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 1b) 其余 BCC 容器（ABcCon：弹簧地板等芯片 BCC 可能在这里）
            try
            {
                if (MpAbcConField != null)
                {
                    object abcConObj = MpAbcConField.GetValue(_mp);
                    if (abcConObj is System.Collections.IList abcCon)
                    {
                        for (int ai = 0; ai < abcCon.Count; ai++)
                        {
                            M2BlockColliderContainer bccX = abcCon[ai] as M2BlockColliderContainer;
                            if (bccX == null)
                            {
                                continue;
                            }
                            for (int xi = 0; xi < xs.Length; xi++)
                            {
                                BCCLine line;
                                float gy = bccX.isFallable(xs[xi], qy, marginx, marginy, out line, true, !_skipLiftNow, -1f, null);
                                if (gy >= 0f && (float.IsNaN(best) || gy < best))
                                {
                                    best = gy;
                                    _groundIsLift = line != null && line.is_lift;
                                    _groundSrc = "额外BCC";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 2) 移动平台等“可携带 BCC”
            try
            {
                int n = _mp.count_carryable_bcc;
                for (int i = 0; i < n; i++)
                {
                    M2BlockColliderContainer bcc = _mp.getCarryableBCCByIndex(i);
                    if (bcc == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < xs.Length; j++)
                    {
                        BCCLine line;
                        float gy = bcc.isFallable(xs[j], qy, marginx, marginy, out line, true, true, -1f, null);
                        if (_skipLiftNow && line != null && line.is_lift)
                        {
                            continue;
                        }
                        if (gy >= 0f && (float.IsNaN(best) || gy < best))
                        {
                            best = gy;
                            _groundIsLift = line != null && line.is_lift;
                            _groundSrc = "移动平台";
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 3) 兜底：getSideBcc（普通地板/坡）
            try
            {
                BCCLine bcc = _mp.getSideBcc(Mathf.FloorToInt(X), Mathf.FloorToInt(Y + SizeY), AIM.B);
                // 与第 1/2 步一致：跳过单向平台时这里也要跳过 lift 线
                // （否则下砸免落地窗口/按下键下穿时，单向板会从第 3 步漏进来直接落地）
                if (bcc != null && (!_skipLiftNow || !bcc.is_lift))
                {
                    float gy = bcc.slopeBottomY(X);
                    if (!float.IsNaN(gy) && (float.IsNaN(best) || gy < best))
                    {
                        best = gy;
                        _groundIsLift = bcc.is_lift;
                        _groundSrc = bcc.is_lift ? "单向平台" : "侧向BCC";
                    }
                }
            }
            catch (Exception)
            {
            }

            // 4) 弹簧地板（NelChipJumperBoard）：显式扫描，确保小骑士能站上并弹起
            // （原版由脚部管理器触发，小骑士是自定义实体，需要自行把板面当作地面）
            try
            {
                int cxj = Mathf.FloorToInt(X);
                int cyj = Mathf.FloorToInt(Y + SizeY);
                var jlist = new List<M2Puts>();
                _mp.getAllPointMetaPutsTo(cxj - 2, cyj - 2, 5, 5, jlist,
                    (M2Puts V, List<M2Puts> _L) => V is NelChipJumperBoard);
                float feetJ = Y + SizeY;
                for (int ji = 0; ji < jlist.Count; ji++)
                {
                    if (jlist[ji] is NelChipJumperBoard board && !board.active_removed && !board.Lay.unloaded)
                    {
                        float top = board.mtop;
                        if (feetJ >= top - 0.45f && feetJ <= top + 0.55f &&
                            X + 0.2f >= board.mleft && X - 0.2f <= board.mright)
                        {
                            if (float.IsNaN(best) || top < best)
                            {
                                best = top;
                                bestJumper = board;
                                _groundSrc = "弹簧地板";
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            _groundIsJumper = bestJumper;
            if (bestJumper != null && !_jumperLogged)
            {
                _jumperLogged = true;
            }
            return best;
        }

        /// <summary>
        /// getFootableY 返回 ceil(板顶)（X.IntC = CeilToInt），对非整数板顶会把人放到
        /// 板顶下方最多 1 格处，而自检接住窗口只有 ±0.55 格 → 站上后下一帧掉板。
        /// 这里用 BCC.isFallable 以 footY 为中心直查精确板顶并返回。
        /// </summary>
        /// <summary>
        /// 交接/重定位吸附：在骑士受击箱宽度内多点采样 BCC.isFallable，取最高可用板面。
        /// isFallable 只返回查询点及其下方的板面——交接镜像后骑士脚底常比入口单向板
        /// 低 1 格左右，因此查询点必须抬到脚底上方 1.6 格，才能发现“脚底上方最多
        /// 1.5 格”的入口板（旧 getFootableY 查询点太低，永远查不到 → 掉竖井）。
        /// 只接受脚底上方 1.5 格内、下方 6 格内的板面（与 v1.0 吸附窗口一致）。
        /// allowUp=false（重定位期间）：只吸脚底及以下的板面，避免与镜像诺艾尔的位置
        /// 每帧来回弹跳；allowUp=true（交接）：允许吸到脚底上方 1.5 格内的入口单向板。
        /// </summary>
        private float FindSnapFootY(float feetNow, bool allowUp)
        {
            if (_mp == null || _mp.BCC == null)
            {
                return -1000f;
            }
            float best = -1000f;
            try
            {
                float cy = feetNow - 1.6f;
                float[] xs = { X - 0.30f, X - 0.15f, X, X + 0.15f, X + 0.30f };
                for (int i = 0; i < xs.Length; i++)
                {
                    BCCLine line;
                    float gy = _mp.BCC.isFallable(xs[i], cy, 0.20f, 8f, out line, true, true, -1f, null);
                    float minTop = allowUp ? feetNow - 1.5f : feetNow - 0.01f;
                    if (gy >= 0f && gy <= feetNow + 6f && gy >= minTop)
                    {
                        if (best < 0f || gy < best)
                        {
                            best = gy;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return best;
        }

        private bool IsBlockCell(int cx, int cy)
        {
            if (_mp == null)
            {
                return false;
            }
            if (cx < 0 || cy < 0 || cx >= _mp.width || cy >= _mp.rows)
            {
                return true;
            }
            int cfg = _mp.getConfig(cx, cy);
            return !CCON.isEmpty(cfg) && !CCON.canStand(cfg) && !CCON.isFloor(cfg) && !CCON.isWater(cfg);
        }

        // ---------- 渲染 ----------

        /// <param name="keepFlukes">true = 重建票据时保留仍在飞的吸虫（切回小骑士时用；换图/读档传 false）</param>
        private void RebindTicket(bool keepFlukes = false)
        {
            ReleaseTicket(keepFlukeList: keepFlukes);
            if (_mp == null)
            {
                return;
            }
            _flukeMp = null; // _mp 已恢复有效，不再需要孤儿地图引用
            EnsureMaterial();
            if (_mat == null)
            {
                return;
            }
            // 水晶图集可能被 Deactivate 销毁：重绑时若缺失则重建
            if (_sdCrystalAtlasTex == null && _textures.ContainsKey("superdash_crystal0000"))
            {
                BuildSuperCrystalAtlas();
            }

            _mesh = new MeshDrawer(null, 4, 6);
            _mesh.draw_gl_only = true;
            _mesh.activate("knight_entity", _mat, false, MTRX.ColWhite, null);
            _ticket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareMesh, _mesh, null, null);

            // 攻击剑气特效：与骑士同层、后注册（画在骑士上面）
            _fxMesh = new MeshDrawer(null, 4, 6);
            _fxMesh.draw_gl_only = true;
            _fxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _fxMat.EnableKeyword("NO_PIXELSNAP");
            _fxMesh.activate("knight_fx", _fxMat, false, MTRX.ColWhite, null);
            _fxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFxMesh, _fxMesh, null, null);

            // 二段跳特效：程序化羽翼轮廓网格（背后一层）+ 3 种形状光点（独立物理下坠）
            _whiteTex = MakeWhiteTexture();

            _djWingMesh = new MeshDrawer(null, 4, 6);
            _djWingMesh.draw_gl_only = true;
            _djWingMat = MTRX.newMtr(MTRX.ShaderGDT);
            _djWingMat.EnableKeyword("NO_PIXELSNAP");
            _djWingMesh.activate("knight_dj_wing", _djWingMat, false, MTRX.ColWhite, null);
            _djWingTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareDjWingMesh, _djWingMesh, null, null);

            // 3 种光点形状（弯月/圆点/长条）各自独立网格（一个网格只能绑一张贴图）
            _dotTexes[0] = MakeDotCrescentTexture(32);
            _dotTexes[1] = MakeDotDotTexture(32);
            _dotTexes[2] = MakeDotStripTexture(32, 12);
            for (int i = 0; i < DotShapeCount; i++)
            {
                int idx = i;
                _djDotMesh[i] = new MeshDrawer(null, 4, 6);
                _djDotMesh[i].draw_gl_only = true;
                _djDotMat[i] = MTRX.newMtr(MTRX.ShaderGDT);
                _djDotMat[i].EnableKeyword("NO_PIXELSNAP");
                _djDotMesh[i].activate("knight_dj_dot" + i, _djDotMat[i], false, MTRX.ColWhite, null);
                _djDotTicket[i] = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR0, null,
                    (Camera Cam, M2RenderTicket Tk, bool redraw, int draw_id, out MeshDrawer MdOut, ref bool overwrite) =>
                        KnightPrepareDjDotMesh(idx, Cam, Tk, redraw, draw_id, out MdOut, ref overwrite),
                    _djDotMesh[i], null, null);
            }

            // 暗影冲刺黑色粒子：单一网格 + 圆形贴图，骑士同层渲染
            _shadowDotMesh = new MeshDrawer(null, 4 * 96, 6 * 96);
            _shadowDotMesh.draw_gl_only = true;
            _shadowDotMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shadowDotMat.EnableKeyword("NO_PIXELSNAP");
            _shadowDotMesh.activate("knight_shadow_dot", _shadowDotMat, false, MTRX.ColWhite, null);
            _shadowDotTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareShadowDashDotMesh, _shadowDotMesh, null, null);

            // 凝聚白色条状线：独立网格 + 长条贴图（_dotTexes[2]），
            // 前后各一张网格：一半粒子画在骑士身后（PR0），一半画在骑士身前（PR1）
            _focusFxBackMesh = new MeshDrawer(null, 4 * 96, 6 * 96);
            _focusFxBackMesh.draw_gl_only = true;
            _focusFxBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _focusFxBackMat.EnableKeyword("NO_PIXELSNAP");
            _focusFxBackMesh.activate("knight_focus_fx_back", _focusFxBackMat, false, MTRX.ColWhite, null);
            _focusFxBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareFocusFxBackMesh, _focusFxBackMesh, null, null);
            _focusFxFrontMesh = new MeshDrawer(null, 4 * 96, 6 * 96);
            _focusFxFrontMesh.draw_gl_only = true;
            _focusFxFrontMat = MTRX.newMtr(MTRX.ShaderGDT);
            _focusFxFrontMat.EnableKeyword("NO_PIXELSNAP");
            _focusFxFrontMesh.activate("knight_focus_fx_front", _focusFxFrontMat, false, MTRX.ColWhite, null);
            _focusFxFrontTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFocusFxFrontMesh, _focusFxFrontMesh, null, null);

            // 水晶之心：蓄力水晶（骑士身前）+ 冲刺拖尾（骑士身后）
            _sdCrystalMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _sdCrystalMesh.draw_gl_only = true;
            _sdCrystalMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sdCrystalMat.EnableKeyword("NO_PIXELSNAP");
            _sdCrystalMesh.activate("knight_sd_crystal", _sdCrystalMat, false, MTRX.ColWhite, null);
            _sdCrystalTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareSuperCrystalMesh, _sdCrystalMesh, null, null);
            _sdTrailMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _sdTrailMesh.draw_gl_only = true;
            _sdTrailMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sdTrailMat.EnableKeyword("NO_PIXELSNAP");
            _sdTrailMesh.activate("knight_sd_trail", _sdTrailMat, false, MTRX.ColWhite, null);
            _sdTrailTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareSuperTrailMesh, _sdTrailMesh, null, null);
            _sdFxMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _sdFxMesh.draw_gl_only = true;
            _sdFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sdFxMat.EnableKeyword("NO_PIXELSNAP");
            _sdFxMesh.activate("knight_sd_fx", _sdFxMat, false, MTRX.ColWhite, null);
            _sdFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareSuperFxMesh, _sdFxMesh, null, null);

            // 紫色收缩光圈：蓄力完成时一圈圈从外围收到中心，骑士身前层
            _sdRingMesh = new MeshDrawer(null, 4 * 512, 6 * 512);
            _sdRingMesh.draw_gl_only = true;
            _sdRingMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sdRingMat.EnableKeyword("NO_PIXELSNAP");
            _sdRingMesh.activate("knight_sd_ring", _sdRingMat, false, MTRX.ColWhite, null);
            _sdRingTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareSuperRingMesh, _sdRingMesh, null, null);

            // 暗影之魂冲击波：独立网格，骑士身前层
            _fireballMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _fireballMesh.draw_gl_only = true;
            _fireballMat = MTRX.newMtr(MTRX.ShaderGDT);
            _fireballMat.EnableKeyword("NO_PIXELSNAP");
            _fireballMesh.activate("knight_fireball", _fireballMat, false, MTRX.ColWhite, null);
            _fireballTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFireballMesh, _fireballMesh, null, null);
            // 吸虫之巢：黑色吸虫（骑士身前层，16 只上限）
            _flukeMesh = new MeshDrawer(null, 4 * 512, 6 * 512);
            _flukeMesh.draw_gl_only = true;
            _flukeMat = MTRX.newMtr(MTRX.ShaderGDT);
            _flukeMat.EnableKeyword("NO_PIXELSNAP");
            _flukeMesh.activate("knight_fluke", _flukeMat, false, MTRX.ColWhite, null);
            _flukeTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFlukeMesh, _flukeMesh, null, null);
            // 防御者纹章法阵：实心圆（骑士身后层 PR0）+ 圆环/图案/发光（骑士身前层 PR1）
            _shelterCircleTex = MakeShelterCircleTexture(128);
            _shelterCircleMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _shelterCircleMesh.draw_gl_only = true;
            _shelterCircleMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shelterCircleMat.EnableKeyword("NO_PIXELSNAP");
            _shelterCircleMesh.activate("knight_shelter_circle", _shelterCircleMat, false, MTRX.ColWhite, null);
            _shelterCircleTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareShelterCircleMesh, _shelterCircleMesh, null, null);
            _shelterFxMesh = new MeshDrawer(null, 4 * 512, 6 * 512);
            _shelterFxMesh.draw_gl_only = true;
            _shelterFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shelterFxMat.EnableKeyword("NO_PIXELSNAP");
            _shelterFxMesh.activate("knight_shelter_fx", _shelterFxMat, false, MTRX.ColWhite, null);
            _shelterFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR2, null, KnightPrepareShelterFxMesh, _shelterFxMesh, null, null);
            // 围绕球体：三个深蓝球公转（骑士身前层 PR1）
            _shelterSphereTex = MakeShelterSphereTexture(64);
            _shelterSphereMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _shelterSphereMesh.draw_gl_only = true;
            _shelterSphereMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shelterSphereMat.EnableKeyword("NO_PIXELSNAP");
            _shelterSphereMesh.activate("knight_shelter_sphere", _shelterSphereMat, false, MTRX.ColWhite, null);
            _shelterSphereTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareShelterSphereMesh, _shelterSphereMesh, null, null);
            // 护符38 梦之盾：围绕骑士公转的盾牌（骑士身前层 PR1）
            _shieldMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _shieldMesh.draw_gl_only = true;
            _shieldMat = MTRX.newMtr(MTRX.ShaderGDT);
            _shieldMat.EnableKeyword("NO_PIXELSNAP");
            _shieldMesh.activate("knight_dreamshield", _shieldMat, false, MTRX.ColWhite, null);
            _shieldTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareDreamShieldMesh, _shieldMesh, null, null);
            // 护符39 格林之子：小格林（骑士身前层 PR1）
            _grimmMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _grimmMesh.draw_gl_only = true;
            _grimmMat = MTRX.newMtr(MTRX.ShaderGDT);
            _grimmMat.EnableKeyword("NO_PIXELSNAP");
            _grimmMesh.activate("knight_grimm", _grimmMat, false, MTRX.ColWhite, null);
            _grimmTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareGrimmMesh, _grimmMesh, null, null);
            // 小格林火球：独立网格（MeshDrawer 一个网格只能绑一张贴图，不能与小格林同网格）
            _grimmFireballMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _grimmFireballMesh.draw_gl_only = true;
            _grimmFireballMat = MTRX.newMtr(MTRX.ShaderGDT);
            _grimmFireballMat.EnableKeyword("NO_PIXELSNAP");
            _grimmFireballMesh.activate("knight_grimm_fireball", _grimmFireballMat, false, MTRX.ColWhite, null);
            _grimmFireballTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareGrimmFireballMesh, _grimmFireballMesh, null, null);
            // 发光子宫：幼体（骑士身前层 PR1）
            _uterusMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _uterusMesh.draw_gl_only = true;
            _uterusMat = MTRX.newMtr(MTRX.ShaderGDT);
            _uterusMat.EnableKeyword("NO_PIXELSNAP");
            _uterusMesh.activate("knight_uterus", _uterusMat, false, MTRX.ColWhite, null);
            _uterusTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareUterusMesh, _uterusMesh, null, null);
            // 蘑菇孢子：孢子云（骑士身前层 PR1，烟雾团多片拼成）
            // 240 个粒子 × 4 顶点 = 960 顶点（旧容量 4×64=256 会溢出导致不渲染）
            _sporeCloudMesh = new MeshDrawer(null, 4 * 256, 6 * 256);
            _sporeCloudMesh.draw_gl_only = true;
            _sporeCloudMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sporeCloudMat.EnableKeyword("NO_PIXELSNAP");
            _sporeCloudMesh.activate("knight_spore_cloud", _sporeCloudMat, false, MTRX.ColWhite, null);
            _sporeCloudTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareSporeCloudMesh, _sporeCloudMesh, null, null);
            // 孢子粒子身后层（PR0，80% 粒子）
            _sporeCloudBackMesh = new MeshDrawer(null, 4 * 256, 6 * 256);
            _sporeCloudBackMesh.draw_gl_only = true;
            _sporeCloudBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _sporeCloudBackMat.EnableKeyword("NO_PIXELSNAP");
            _sporeCloudBackMesh.activate("knight_spore_cloud_back", _sporeCloudBackMat, false,
                MTRX.ColWhite, null);
            _sporeCloudBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareSporeCloudBackMesh, _sporeCloudBackMesh, null, null);
            // 编织者之歌：小编织者（骑士身前层 PR1）
            _weaverMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _weaverMesh.draw_gl_only = true;
            _weaverMat = MTRX.newMtr(MTRX.ShaderGDT);
            _weaverMat.EnableKeyword("NO_PIXELSNAP");
            _weaverMesh.activate("knight_weaver", _weaverMat, false, MTRX.ColWhite, null);
            _weaverTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareWeaverlingMesh, _weaverMesh, null, null);
            if (SporeCloudHitboxDebug)
            {
                _sporeCloudDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _sporeCloudDbgMesh.draw_gl_only = true;
                _sporeCloudDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _sporeCloudDbgMat.EnableKeyword("NO_PIXELSNAP");
                _sporeCloudDbgMesh.activate("knight_spore_cloud_dbg", _sporeCloudDbgMat, false,
                    MTRX.ColWhite, null);
                _sporeCloudDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareSporeCloudDebugMesh,
                    _sporeCloudDbgMesh, null, null);
            }

            // 蜕变挽歌剑气：独立网格，骑士身前层
            _elegyBladeMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _elegyBladeMesh.draw_gl_only = true;
            _elegyBladeMat = MTRX.newMtr(MTRX.ShaderGDT);
            _elegyBladeMat.EnableKeyword("NO_PIXELSNAP");
            _elegyBladeMesh.activate("knight_elegy_blade", _elegyBladeMat, false, MTRX.ColWhite, null);
            _elegyBladeTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareElegyBladeMesh, _elegyBladeMesh, null, null);
            if (ElegyBladeHitboxDebug)
            {
                _elegyBladeDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _elegyBladeDbgMesh.draw_gl_only = true;
                _elegyBladeDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _elegyBladeDbgMat.EnableKeyword("NO_PIXELSNAP");
                _elegyBladeDbgMesh.activate("knight_elegy_blade_dbg", _elegyBladeDbgMat, false, MTRX.ColWhite, null);
                _elegyBladeDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareElegyBladeDebugMesh, _elegyBladeDbgMesh, null, null);
            }
            if (AttackHitboxDebug)
            {
                _attackDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _attackDbgMesh.draw_gl_only = true;
                _attackDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _attackDbgMat.EnableKeyword("NO_PIXELSNAP");
                _attackDbgMesh.activate("knight_attack_dbg", _attackDbgMat, false, MTRX.ColWhite, null);
                _attackDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareAttackDebugMesh, _attackDbgMesh, null, null);
            }
            if (HurtBoxDebug)
            {
                _hurtDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _hurtDbgMesh.draw_gl_only = true;
                _hurtDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _hurtDbgMat.EnableKeyword("NO_PIXELSNAP");
                _hurtDbgMesh.activate("knight_hurt_dbg", _hurtDbgMat, false, MTRX.ColWhite, null);
                _hurtDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareHurtDebugMesh, _hurtDbgMesh, null, null);
            }
            if (NailArtHitboxDebug)
            {
                _nailArtDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _nailArtDbgMesh.draw_gl_only = true;
                _nailArtDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _nailArtDbgMat.EnableKeyword("NO_PIXELSNAP");
                _nailArtDbgMesh.activate("knight_nailart_dbg", _nailArtDbgMat, false, MTRX.ColWhite, null);
                _nailArtDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareNailArtDebugMesh, _nailArtDbgMesh, null, null);
            }
            if (DashSlashHitboxDebug)
            {
                _dashSlashDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _dashSlashDbgMesh.draw_gl_only = true;
                _dashSlashDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _dashSlashDbgMat.EnableKeyword("NO_PIXELSNAP");
                _dashSlashDbgMesh.activate("knight_dashslash_dbg", _dashSlashDbgMat, false, MTRX.ColWhite, null);
                _dashSlashDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareDashSlashDebugMesh, _dashSlashDbgMesh, null, null);
            }
            if (CycloneHitboxDebug)
            {
                _cycloneDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _cycloneDbgMesh.draw_gl_only = true;
                _cycloneDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _cycloneDbgMat.EnableKeyword("NO_PIXELSNAP");
                _cycloneDbgMesh.activate("knight_cyclone_dbg", _cycloneDbgMat, false, MTRX.ColWhite, null);
                _cycloneDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareCycloneDebugMesh, _cycloneDbgMesh, null, null);
            }
            if (NailParryDebug)
            {
                _nailParryDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _nailParryDbgMesh.draw_gl_only = true;
                _nailParryDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _nailParryDbgMat.EnableKeyword("NO_PIXELSNAP");
                _nailParryDbgMesh.activate("knight_nailparry_dbg", _nailParryDbgMat, false, MTRX.ColWhite, null);
                _nailParryDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareNailParryDebugMesh, _nailParryDbgMesh, null, null);
            }
            if (UterusExplosionHitboxDebug)
            {
                _uterusExplosionDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _uterusExplosionDbgMesh.draw_gl_only = true;
                _uterusExplosionDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _uterusExplosionDbgMat.EnableKeyword("NO_PIXELSNAP");
                _uterusExplosionDbgMesh.activate("knight_uterus_expl_dbg", _uterusExplosionDbgMat, false,
                    MTRX.ColWhite, null);
                _uterusExplosionDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareUterusExplosionDebugMesh,
                    _uterusExplosionDbgMesh, null, null);
            }

            // 施法瞬间爆发特效：独立网格，骑士身前层
            _fireballBlastMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _fireballBlastMesh.draw_gl_only = true;
            _fireballBlastMat = MTRX.newMtr(MTRX.ShaderGDT);
            _fireballBlastMat.EnableKeyword("NO_PIXELSNAP");
            _fireballBlastMesh.activate("knight_fireball_blast", _fireballBlastMat, false, MTRX.ColWhite, null);
            _fireballBlastTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFireballBlastMesh, _fireballBlastMesh, null, null);

            // 深渊尖啸：上升冲击波特效，骑士身前层
            _screamBlastMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _screamBlastMesh.draw_gl_only = true;
            _screamBlastMat = MTRX.newMtr(MTRX.ShaderGDT);
            _screamBlastMat.EnableKeyword("NO_PIXELSNAP");
            _screamBlastMesh.activate("knight_scream_blast", _screamBlastMat, false, MTRX.ColWhite, null);
            _screamBlastTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareScreamBlastMesh, _screamBlastMesh, null, null);

            // 深渊尖啸黑/白粒子：身后层（PR0，80%）+ 身前层（PR1，20%）
            _screamFxBackMesh = new MeshDrawer(null, 4 * 128, 6 * 128);
            _screamFxBackMesh.draw_gl_only = true;
            _screamFxBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _screamFxBackMat.EnableKeyword("NO_PIXELSNAP");
            _screamFxBackMesh.activate("knight_scream_fx_back", _screamFxBackMat, false, MTRX.ColWhite, null);
            _screamFxBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareScreamFxBackMesh, _screamFxBackMesh, null, null);
            _screamFxFrontMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _screamFxFrontMesh.draw_gl_only = true;
            _screamFxFrontMat = MTRX.newMtr(MTRX.ShaderGDT);
            _screamFxFrontMat.EnableKeyword("NO_PIXELSNAP");
            _screamFxFrontMesh.activate("knight_scream_fx_front", _screamFxFrontMat, false, MTRX.ColWhite, null);
            _screamFxFrontTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareScreamFxFrontMesh, _screamFxFrontMesh, null, null);

            // 骨钉技艺·强力劈砍：蓄力白色粒子（每帧 2 个，容量加大），骑士身后层
            _nailArtChargeParticleMesh = new MeshDrawer(null, 4 * 256, 6 * 256);
            _nailArtChargeParticleMesh.draw_gl_only = true;
            _nailArtChargeParticleMat = MTRX.newMtr(MTRX.ShaderGDT);
            _nailArtChargeParticleMat.EnableKeyword("NO_PIXELSNAP");
            _nailArtChargeParticleMesh.activate("knight_nail_art_charge_pt", _nailArtChargeParticleMat, false, MTRX.ColWhite, null);
            _nailArtChargeParticleTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareNailArtChargeParticleMesh, _nailArtChargeParticleMesh, null, null);

            // 骨钉技艺·强力劈砍：蓄满光圈（nail_charge_effect），骑士身后层
            _nailArtGlowMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _nailArtGlowMesh.draw_gl_only = true;
            _nailArtGlowMat = MTRX.newMtr(MTRX.ShaderGDT);
            _nailArtGlowMat.EnableKeyword("NO_PIXELSNAP");
            _nailArtGlowMesh.activate("knight_nail_art_glow", _nailArtGlowMat, false, MTRX.ColWhite, null);
            _nailArtGlowTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareNailArtGlowMesh, _nailArtGlowMesh, null, null);

            // 骨钉技艺·强力劈砍：剑气（charge_slash_effect），骑士身前层
            _nailArtSlashFxMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _nailArtSlashFxMesh.draw_gl_only = true;
            _nailArtSlashFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _nailArtSlashFxMat.EnableKeyword("NO_PIXELSNAP");
            _nailArtSlashFxMesh.activate("knight_nail_art_slash_fx", _nailArtSlashFxMat, false, MTRX.ColWhite, null);
            _nailArtSlashFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareNailArtSlashFxMesh, _nailArtSlashFxMesh, null, null);

            // 骨钉技艺·冲刺劈砍：剑气（dash_slash_effect），骑士身前层
            _dashSlashFxMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _dashSlashFxMesh.draw_gl_only = true;
            _dashSlashFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _dashSlashFxMat.EnableKeyword("NO_PIXELSNAP");
            _dashSlashFxMesh.activate("knight_dash_slash_fx", _dashSlashFxMat, false, MTRX.ColWhite, null);
            _dashSlashFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareDashSlashFxMesh, _dashSlashFxMesh, null, null);

            // 骨钉技艺·旋风劈砍：旋转剑气（cyclone_slash_effect），骑士身后层
            _cycloneFxMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _cycloneFxMesh.draw_gl_only = true;
            _cycloneFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _cycloneFxMat.EnableKeyword("NO_PIXELSNAP");
            _cycloneFxMesh.activate("knight_cyclone_fx", _cycloneFxMat, false, MTRX.ColWhite, null);
            _cycloneFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareCycloneFxMesh, _cycloneFxMesh, null, null);

            // 梦钉命中白色粒子：身后层（PR0，80%）+ 身前层（PR1，20%）
            _dreamParticleBackMesh = new MeshDrawer(null, 4 * 128, 6 * 128);
            _dreamParticleBackMesh.draw_gl_only = true;
            _dreamParticleBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _dreamParticleBackMat.EnableKeyword("NO_PIXELSNAP");
            _dreamParticleBackMesh.activate("knight_dream_pt_back", _dreamParticleBackMat, false, MTRX.ColWhite, null);
            _dreamParticleBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareDreamParticleBackMesh, _dreamParticleBackMesh, null, null);
            _dreamParticleFrontMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _dreamParticleFrontMesh.draw_gl_only = true;
            _dreamParticleFrontMat = MTRX.newMtr(MTRX.ShaderGDT);
            _dreamParticleFrontMat.EnableKeyword("NO_PIXELSNAP");
            _dreamParticleFrontMesh.activate("knight_dream_pt_front", _dreamParticleFrontMat, false, MTRX.ColWhite, null);
            _dreamParticleFrontTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareDreamParticleFrontMesh, _dreamParticleFrontMesh, null, null);

            // 骨剑（up_nail）：下砸落地 5 把剑，贴图渲染，骑士身后层
            _diveSwordMesh = new MeshDrawer(null, 4 * 8, 6 * 8);
            _diveSwordMesh.draw_gl_only = true;
            _diveSwordMat = MTRX.newMtr(MTRX.ShaderGDT);
            _diveSwordMat.EnableKeyword("NO_PIXELSNAP");
            _diveSwordMesh.activate("knight_dive_sword", _diveSwordMat, false, MTRX.ColWhite, null);
            _diveSwordTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareDiveSwordMesh, _diveSwordMesh, null, null);

            // 尖刺（white_spikes）：下砸落地两侧 22 个尖刺，贴图渲染，骑士身后层
            _diveSpikeMesh = new MeshDrawer(null, 4 * 32, 6 * 32);
            _diveSpikeMesh.draw_gl_only = true;
            _diveSpikeMat = MTRX.newMtr(MTRX.ShaderGDT);
            _diveSpikeMat.EnableKeyword("NO_PIXELSNAP");
            _diveSpikeMesh.activate("knight_dive_spike", _diveSpikeMat, false, MTRX.ColWhite, null);
            _diveSpikeTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareDiveSpikeMesh, _diveSpikeMesh, null, null);

            // 落地黑/白粒子：身后层（PR0，80%）+ 身前层（PR1，20%）
            _diveFxBackMesh = new MeshDrawer(null, 4 * 128, 6 * 128);
            _diveFxBackMesh.draw_gl_only = true;
            _diveFxBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _diveFxBackMat.EnableKeyword("NO_PIXELSNAP");
            _diveFxBackMesh.activate("knight_dive_fx_back", _diveFxBackMat, false, MTRX.ColWhite, null);
            _diveFxBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareDiveFxBackMesh, _diveFxBackMesh, null, null);
            _diveFxFrontMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _diveFxFrontMesh.draw_gl_only = true;
            _diveFxFrontMat = MTRX.newMtr(MTRX.ShaderGDT);
            _diveFxFrontMat.EnableKeyword("NO_PIXELSNAP");
            _diveFxFrontMesh.activate("knight_dive_fx_front", _diveFxFrontMat, false, MTRX.ColWhite, null);
            _diveFxFrontTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareDiveFxFrontMesh, _diveFxFrontMesh, null, null);

            // 暗影之魂碰撞箱调试：绿色框（碰撞箱）+ 白色框（渲染范围），骑士身前层
            if (FireballHitboxDebug)
            {
                _fireballDbgMesh = new MeshDrawer(null, 4 * 256, 6 * 256);
                _fireballDbgMesh.draw_gl_only = true;
                _fireballDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _fireballDbgMat.EnableKeyword("NO_PIXELSNAP");
                _fireballDbgMesh.activate("knight_fireball_dbg", _fireballDbgMat, false, MTRX.ColWhite, null);
                _fireballDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareFireballDebugMesh, _fireballDbgMesh, null, null);
            }

            // Boss 本体碰撞箱调试：绿色框（场内存在 Boss 时显示），骑士身前层
            if (BossHitboxDebug)
            {
                _bossDbgMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
                _bossDbgMesh.draw_gl_only = true;
                _bossDbgMat = MTRX.newMtr(MTRX.ShaderGDT);
                _bossDbgMat.EnableKeyword("NO_PIXELSNAP");
                _bossDbgMesh.activate("knight_boss_dbg", _bossDbgMat, false, MTRX.ColWhite, null);
                _bossDbgTicket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR1, null, KnightPrepareBossDebugMesh, _bossDbgMesh, null, null);
            }

            // 低血量（1 格）黑色虚空粒子：独立网格 + 圆形贴图
            _lowHpMesh = new MeshDrawer(null, 4 * 64, 6 * 64);
            _lowHpMesh.draw_gl_only = true;
            _lowHpMat = MTRX.newMtr(MTRX.ShaderGDT);
            _lowHpMat.EnableKeyword("NO_PIXELSNAP");
            _lowHpMesh.activate("knight_lowhp_dot", _lowHpMat, false, MTRX.ColWhite, null);
            _lowHpTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareLowHpMesh, _lowHpMesh, null, null);
            // 亡者之怒：身后红色光晕（背后层 PR0，脉冲闪烁，与原版 FlashingFury 节奏一致）
            _furyGlowTex = MakeRadialGlowTexture(64);
            _furyGlowMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _furyGlowMesh.draw_gl_only = true;
            _furyGlowMat = MTRX.newMtr(MTRX.ShaderGDT);
            _furyGlowMat.EnableKeyword("NO_PIXELSNAP");
            _furyGlowMesh.activate("knight_fury_glow", _furyGlowMat, false, MTRX.ColWhite, null);
            _furyGlowTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareFuryGlowMesh, _furyGlowMesh, null, null);
            // 虚空解放：身后双图（背后层 PR0，第二段期间迅速交替 + 随机旋转）
            _voidBackMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _voidBackMesh.draw_gl_only = true;
            _voidBackMat = MTRX.newMtr(MTRX.ShaderGDT);
            _voidBackMat.EnableKeyword("NO_PIXELSNAP");
            _voidBackMesh.activate("knight_void_back", _voidBackMat, false, MTRX.ColWhite, null);
            _voidBackTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareVoidBackMesh, _voidBackMesh, null, null);
            _voidBackIndex = 0;
            _voidBackSwitchTimer = 0f;
            _voidBackRot = 0f;
            // 虚空解放：目标身上划痕（身前层 PR1，出伤结束前循环）
            _voidSlashMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _voidSlashMesh.draw_gl_only = true;
            _voidSlashMat = MTRX.newMtr(MTRX.ShaderGDT);
            _voidSlashMat.EnableKeyword("NO_PIXELSNAP");
            _voidSlashMesh.activate("knight_void_slash", _voidSlashMat, false, MTRX.ColWhite, null);
            // 划痕用 PR2（高于小骑士本体 PR1），确保盖在怪身上可见
            _voidSlashTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR2, null, KnightPrepareVoidSlashMesh, _voidSlashMesh, null, null);
            // 生命血羁绊：身后蓝色光晕（背后层 PR0，脉冲闪烁，复刻亡者之怒）
            _lifebloodGlowTex = MakeRadialGlowTexture(64);
            _lifebloodGlowMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _lifebloodGlowMesh.draw_gl_only = true;
            _lifebloodGlowMat = MTRX.newMtr(MTRX.ShaderGDT);
            _lifebloodGlowMat.EnableKeyword("NO_PIXELSNAP");
            _lifebloodGlowMesh.activate("knight_lifeblood_glow", _lifebloodGlowMat, false, MTRX.ColWhite, null);
            _lifebloodGlowTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareLifebloodGlowMesh, _lifebloodGlowMesh, null, null);
            // 巴尔德之壳：凝聚回血时的硬壳（骑士身前层 PR1）
            _baldurShellMesh = new MeshDrawer(null, 4 * 16, 6 * 16);
            _baldurShellMesh.draw_gl_only = true;
            _baldurShellMat = MTRX.newMtr(MTRX.ShaderGDT);
            _baldurShellMat.EnableKeyword("NO_PIXELSNAP");
            _baldurShellMesh.activate("knight_baldur_shell", _baldurShellMat, false, MTRX.ColWhite, null);
            _baldurShellTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareBaldurShellMesh, _baldurShellMesh, null, null);

            // 受击粒子爆发：独立网格，骑士上一层（PR1）
            _hitFxMesh = new MeshDrawer(null, 4 * 256, 6 * 256);
            _hitFxMesh.draw_gl_only = true;
            _hitFxMat = MTRX.newMtr(MTRX.ShaderGDT);
            _hitFxMat.EnableKeyword("NO_PIXELSNAP");
            _hitFxMesh.activate("knight_hit_fx", _hitFxMat, false, MTRX.ColWhite, null);
            _hitFxTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR1, null, KnightPrepareHitFxMesh, _hitFxMesh, null, null);

            // 暗影充能特效（绕骑士的影雾），在骑士后面一层
            _rcMesh = new MeshDrawer(null, 4, 6);
            _rcMesh.draw_gl_only = true;
            // 每个特效必须用独立材质实例，否则同层多票据共享材质时贴图会被第一张覆盖
            _rcMat = MTRX.newMtr(MTRX.ShaderGDT);
            _rcMat.EnableKeyword("NO_PIXELSNAP");
            _rcMesh.activate("knight_recharge", _rcMat, false, MTRX.ColWhite, null);
            _rcTicket = _mp.MovRenderer.assignDrawable(
                M2Mover.DRAW_ORDER.PR0, null, KnightPrepareRechargeMesh, _rcMesh, null, null);

            // 拖尾残影：每个残影独立一张网格+票据，矩阵写法与骑士完全一致
            _ghostNodes.Clear();
            for (int i = 0; i < GhostNodeCount; i++)
            {
                GhostNode node = new GhostNode();
                node.Mesh = new MeshDrawer(null, 4 * GhostLayers, 6 * GhostLayers);
                node.Mesh.draw_gl_only = true;
                node.Mat = MTRX.newMtr(MTRX.ShaderGDT);
                node.Mat.EnableKeyword("NO_PIXELSNAP");
                node.Mesh.activate("knight_ghost_" + i, node.Mat, false, MTRX.ColWhite, null);
                GhostNode captured = node;
                node.Ticket = _mp.MovRenderer.assignDrawable(
                    M2Mover.DRAW_ORDER.PR0, null,
                    (Camera Cam, M2RenderTicket Tk, bool redraw, int draw_id, out MeshDrawer MdOut, ref bool overwrite) =>
                        KnightPrepareGhostNode(captured, Cam, Tk, redraw, draw_id, out MdOut, ref overwrite),
                    node.Mesh, null, null);
                _ghostNodes.Add(node);
            }
        }

        /// <param name="keepFlukeList">true = 保留仍在飞的吸虫列表（切回诺艾尔 / 切回小骑士时用）</param>
        /// <param name="keepFlukeTicket">true = 连吸虫的渲染票据/网格也保留（切回诺艾尔后仍要画它们）</param>
        private void ReleaseTicket(bool keepFlukeList = false, bool keepFlukeTicket = false)
        {
            if (_ticket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_ticket, -1);
                }
                catch
                    {
                    }
            }
            if (_fxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_fxTicket, -1);
                }
                catch
                {
                }
            }
            if (_fxMesh != null)
            {
                try
                {
                    _fxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_fxMat != null)
            {
                try
                {
                    IN.DestroyOne(_fxMat);
                }
                catch
                    {
                    }
            }
            if (_djWingTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_djWingTicket, -1);
                }
                catch
                {
                }
            }
            if (_djWingMesh != null)
            {
                try
                {
                    _djWingMesh.destruct();
                }
                catch
                {
                }
            }
            if (_djWingMat != null)
            {
                try
                {
                    IN.DestroyOne(_djWingMat);
                }
                catch
                {
                }
            }
            if (_whiteTex != null)
            {
                try
                {
                    Destroy(_whiteTex);
                }
                catch
                {
                }
            }
            for (int i = 0; i < DotShapeCount; i++)
            {
                if (_djDotTicket[i] != null && _mp != null)
                {
                    try
                    {
                        _mp.MovRenderer.deassignDrawable(_djDotTicket[i], -1);
                    }
                    catch
                    {
                    }
                }
                if (_djDotMesh[i] != null)
                {
                    try
                    {
                        _djDotMesh[i].destruct();
                    }
                    catch
                    {
                    }
                }
                if (_djDotMat[i] != null)
                {
                    try
                    {
                        IN.DestroyOne(_djDotMat[i]);
                    }
                    catch
                    {
                    }
                }
                if (_dotTexes[i] != null)
                {
                    try
                    {
                        Destroy(_dotTexes[i]);
                    }
                    catch
                    {
                    }
                }
                _djDotTicket[i] = null;
                _djDotMesh[i] = null;
                _djDotMat[i] = null;
                _dotTexes[i] = null;
            }
            if (_shadowDotTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_shadowDotTicket, -1);
                }
                catch
                {
                }
            }
            if (_shadowDotMesh != null)
            {
                try
                {
                    _shadowDotMesh.destruct();
                }
                catch
                {
                }
            }
            if (_shadowDotMat != null)
            {
                try
                {
                    IN.DestroyOne(_shadowDotMat);
                }
                catch
                {
                }
            }
            _shadowDotTicket = null;
            _shadowDotMesh = null;
            _shadowDotMat = null;
            _shadowParticles.Clear();
            if (_focusFxBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_focusFxBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_focusFxBackMesh != null)
            {
                try
                {
                    _focusFxBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_focusFxBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_focusFxBackMat);
                }
                catch
                {
                }
            }
            _focusFxBackTicket = null;
            _focusFxBackMesh = null;
            _focusFxBackMat = null;
            if (_focusFxFrontTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_focusFxFrontTicket, -1);
                }
                catch
                {
                }
            }
            if (_focusFxFrontMesh != null)
            {
                try
                {
                    _focusFxFrontMesh.destruct();
                }
                catch
                {
                }
            }
            if (_focusFxFrontMat != null)
            {
                try
                {
                    IN.DestroyOne(_focusFxFrontMat);
                }
                catch
                {
                }
            }
            _focusFxFrontTicket = null;
            _focusFxFrontMesh = null;
            _focusFxFrontMat = null;
            _focusParticles.Clear();
            if (_sdCrystalTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sdCrystalTicket, -1);
                }
                catch
                {
                }
            }
            if (_sdCrystalMesh != null)
            {
                try
                {
                    _sdCrystalMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sdCrystalMat != null)
            {
                try
                {
                    IN.DestroyOne(_sdCrystalMat);
                }
                catch
                {
                }
            }
            _sdCrystalTicket = null;
            _sdCrystalMesh = null;
            _sdCrystalMat = null;
            if (_sdTrailTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sdTrailTicket, -1);
                }
                catch
                {
                }
            }
            if (_sdTrailMesh != null)
            {
                try
                {
                    _sdTrailMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sdTrailMat != null)
            {
                try
                {
                    IN.DestroyOne(_sdTrailMat);
                }
                catch
                {
                }
            }
            _sdTrailTicket = null;
            _sdTrailMesh = null;
            _sdTrailMat = null;
            if (_sdFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sdFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_sdFxMesh != null)
            {
                try
                {
                    _sdFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sdFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_sdFxMat);
                }
                catch
                {
                }
            }
            _sdFxTicket = null;
            _sdFxMesh = null;
            _sdFxMat = null;
            if (_sdRingTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sdRingTicket, -1);
                }
                catch
                {
                }
            }
            if (_sdRingMesh != null)
            {
                try
                {
                    _sdRingMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sdRingMat != null)
            {
                try
                {
                    IN.DestroyOne(_sdRingMat);
                }
                catch
                {
                }
            }
            _sdRingTicket = null;
            _sdRingMesh = null;
            _sdRingMat = null;
            _sdRings.Clear();
            if (_fireballTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_fireballTicket, -1);
                }
                catch
                {
                }
            }
            if (_fireballMesh != null)
            {
                try
                {
                    _fireballMesh.destruct();
                }
                catch
                {
                }
            }
            if (_fireballMat != null)
            {
                try
                {
                    IN.DestroyOne(_fireballMat);
                }
                catch
                {
                }
            }
            _fireballTicket = null;
            _fireballMesh = null;
            _fireballMat = null;
            _fireballs.Clear();
            Map2d mpFlukeRelease = FlukeMp; // 孤儿吸虫时 _mp 已为 null，用保留的地图引用才能正确 deassign
            if (!keepFlukeTicket && _flukeTicket != null && mpFlukeRelease != null)
            {
                try
                {
                    mpFlukeRelease.MovRenderer.deassignDrawable(_flukeTicket, -1);
                }
                catch
                {
                }
            }
            if (!keepFlukeTicket && _flukeMesh != null)
            {
                try
                {
                    _flukeMesh.destruct();
                }
                catch
                {
                }
            }
            if (!keepFlukeTicket && _flukeMat != null)
            {
                try
                {
                    IN.DestroyOne(_flukeMat);
                }
                catch
                {
                }
            }
            if (!keepFlukeTicket)
            {
                _flukeTicket = null;
                _flukeMesh = null;
                _flukeMat = null;
            }
            if (!keepFlukeList)
            {
                _flukes.Clear();
                _flukeMp = null;
            }
            if (_shelterCircleTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_shelterCircleTicket, -1);
                }
                catch
                {
                }
            }
            if (_shelterCircleMesh != null)
            {
                try
                {
                    _shelterCircleMesh.destruct();
                }
                catch
                {
                }
            }
            if (_shelterCircleMat != null)
            {
                try
                {
                    IN.DestroyOne(_shelterCircleMat);
                }
                catch
                {
                }
            }
            if (_shelterCircleTex != null)
            {
                try
                {
                    Destroy(_shelterCircleTex);
                }
                catch
                {
                }
            }
            _shelterCircleTicket = null;
            _shelterCircleMesh = null;
            _shelterCircleMat = null;
            _shelterCircleTex = null;
            if (_shelterFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_shelterFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_shelterFxMesh != null)
            {
                try
                {
                    _shelterFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_shelterFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_shelterFxMat);
                }
                catch
                {
                }
            }
            _shelterFxTicket = null;
            _shelterFxMesh = null;
            _shelterFxMat = null;
            if (_shelterSphereTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_shelterSphereTicket, -1);
                }
                catch
                {
                }
            }
            if (_shelterSphereMesh != null)
            {
                try
                {
                    _shelterSphereMesh.destruct();
                }
                catch
                {
                }
            }
            if (_shelterSphereMat != null)
            {
                try
                {
                    IN.DestroyOne(_shelterSphereMat);
                }
                catch
                {
                }
            }
            if (_shelterSphereTex != null)
            {
                try
                {
                    Destroy(_shelterSphereTex);
                }
                catch
                {
                }
            }
            _shelterSphereTicket = null;
            _shelterSphereMesh = null;
            _shelterSphereMat = null;
            _shelterSphereTex = null;
            if (_shieldTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_shieldTicket, -1);
                }
                catch
                {
                }
            }
            if (_shieldMesh != null)
            {
                try
                {
                    _shieldMesh.destruct();
                }
                catch
                {
                }
            }
            if (_shieldMat != null)
            {
                try
                {
                    IN.DestroyOne(_shieldMat);
                }
                catch
                {
                }
            }
            _shieldTicket = null;
            _shieldMesh = null;
            _shieldMat = null;
            if (_grimmTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_grimmTicket, -1);
                }
                catch
                {
                }
            }
            if (_grimmMesh != null)
            {
                try
                {
                    _grimmMesh.destruct();
                }
                catch
                {
                }
            }
            if (_grimmMat != null)
            {
                try
                {
                    IN.DestroyOne(_grimmMat);
                }
                catch
                {
                }
            }
            _grimmTicket = null;
            _grimmMesh = null;
            _grimmMat = null;
            if (_grimmFireballTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_grimmFireballTicket, -1);
                }
                catch
                {
                }
            }
            if (_grimmFireballMesh != null)
            {
                try
                {
                    _grimmFireballMesh.destruct();
                }
                catch
                {
                }
            }
            if (_grimmFireballMat != null)
            {
                try
                {
                    IN.DestroyOne(_grimmFireballMat);
                }
                catch
                {
                }
            }
            _grimmFireballTicket = null;
            _grimmFireballMesh = null;
            _grimmFireballMat = null;
            if (_uterusTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_uterusTicket, -1);
                }
                catch
                {
                }
            }
            if (_uterusMesh != null)
            {
                try
                {
                    _uterusMesh.destruct();
                }
                catch
                {
                }
            }
            if (_uterusMat != null)
            {
                try
                {
                    IN.DestroyOne(_uterusMat);
                }
                catch
                {
                }
            }
            _uterusTicket = null;
            _uterusMesh = null;
            _uterusMat = null;
            _uterusHatchlings.Clear();
            if (_sporeCloudTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sporeCloudTicket, -1);
                }
                catch
                {
                }
            }
            if (_sporeCloudMesh != null)
            {
                try
                {
                    _sporeCloudMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sporeCloudMat != null)
            {
                try
                {
                    IN.DestroyOne(_sporeCloudMat);
                }
                catch
                {
                }
            }
            _sporeCloudTicket = null;
            _sporeCloudMesh = null;
            _sporeCloudMat = null;
            _sporeClouds.Clear();
            if (_sporeCloudBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_sporeCloudBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_sporeCloudBackMesh != null)
            {
                try
                {
                    _sporeCloudBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sporeCloudBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_sporeCloudBackMat);
                }
                catch
                {
                }
            }
            _sporeCloudBackTicket = null;
            _sporeCloudBackMesh = null;
            _sporeCloudBackMat = null;
            if (_weaverTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_weaverTicket, -1);
                }
                catch
                {
                }
            }
            if (_weaverMesh != null)
            {
                try
                {
                    _weaverMesh.destruct();
                }
                catch
                {
                }
            }
            if (_weaverMat != null)
            {
                try
                {
                    IN.DestroyOne(_weaverMat);
                }
                catch
                {
                }
            }
            _weaverTicket = null;
            _weaverMesh = null;
            _weaverMat = null;
            _weaverlings.Clear();
            _weaverThreads.Clear();
            if (_sporeCloudDbgMesh != null)
            {
                try
                {
                    _sporeCloudDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_sporeCloudDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_sporeCloudDbgMat);
                }
                catch
                {
                }
            }
            _sporeCloudDbgTicket = null;
            _sporeCloudDbgMesh = null;
            _sporeCloudDbgMat = null;
            if (_elegyBladeMesh != null)
            {
                try
                {
                    _elegyBladeMesh.destruct();
                }
                catch
                {
                }
            }
            if (_elegyBladeMat != null)
            {
                try
                {
                    IN.DestroyOne(_elegyBladeMat);
                }
                catch
                {
                }
            }
            _elegyBladeTicket = null;
            _elegyBladeMesh = null;
            _elegyBladeMat = null;
            _elegyBlades.Clear();
            if (_elegyBladeDbgMesh != null)
            {
                try
                {
                    _elegyBladeDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_elegyBladeDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_elegyBladeDbgMat);
                }
                catch
                {
                }
            }
            _elegyBladeDbgTicket = null;
            _elegyBladeDbgMesh = null;
            _elegyBladeDbgMat = null;
            if (_attackDbgMesh != null)
            {
                try
                {
                    _attackDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_attackDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_attackDbgMat);
                }
                catch
                {
                }
            }
            _attackDbgTicket = null;
            _attackDbgMesh = null;
            _attackDbgMat = null;
            if (_hurtDbgMesh != null)
            {
                try
                {
                    _hurtDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_hurtDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_hurtDbgMat);
                }
                catch
                {
                }
            }
            _hurtDbgTicket = null;
            _hurtDbgMesh = null;
            _hurtDbgMat = null;
            if (_nailArtDbgMesh != null)
            {
                try
                {
                    _nailArtDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_nailArtDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_nailArtDbgMat);
                }
                catch
                {
                }
            }
            _nailArtDbgTicket = null;
            _nailArtDbgMesh = null;
            _nailArtDbgMat = null;
            if (_dashSlashDbgMesh != null)
            {
                try
                {
                    _dashSlashDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_dashSlashDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_dashSlashDbgMat);
                }
                catch
                {
                }
            }
            _dashSlashDbgTicket = null;
            _dashSlashDbgMesh = null;
            _dashSlashDbgMat = null;
            if (_cycloneDbgMesh != null)
            {
                try
                {
                    _cycloneDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_cycloneDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_cycloneDbgMat);
                }
                catch
                {
                }
            }
            _cycloneDbgTicket = null;
            _cycloneDbgMesh = null;
            _cycloneDbgMat = null;
            if (_nailParryDbgMesh != null)
            {
                try
                {
                    _nailParryDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_nailParryDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_nailParryDbgMat);
                }
                catch
                {
                }
            }
            _nailParryDbgTicket = null;
            _nailParryDbgMesh = null;
            _nailParryDbgMat = null;
            if (_uterusExplosionDbgMesh != null)
            {
                try
                {
                    _uterusExplosionDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_uterusExplosionDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_uterusExplosionDbgMat);
                }
                catch
                {
                }
            }
            _uterusExplosionDbgTicket = null;
            _uterusExplosionDbgMesh = null;
            _uterusExplosionDbgMat = null;
            _fireballCasting = false;
            _screaming = false;
            DestroyScreamHitbox();
            if (_fireballBlastTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_fireballBlastTicket, -1);
                }
                catch
                {
                }
            }
            if (_fireballBlastMesh != null)
            {
                try
                {
                    _fireballBlastMesh.destruct();
                }
                catch
                {
                }
            }
            if (_fireballBlastMat != null)
            {
                try
                {
                    IN.DestroyOne(_fireballBlastMat);
                }
                catch
                {
                }
            }
            _fireballBlastTicket = null;
            _fireballBlastMesh = null;
            _fireballBlastMat = null;
            _fireballBlastSprite = null;
            _fireballBlastFrames = null;
            if (_screamBlastTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_screamBlastTicket, -1);
                }
                catch
                {
                }
            }
            if (_screamBlastMesh != null)
            {
                try
                {
                    _screamBlastMesh.destruct();
                }
                catch
                {
                }
            }
            if (_screamBlastMat != null)
            {
                try
                {
                    IN.DestroyOne(_screamBlastMat);
                }
                catch
                {
                }
            }
            _screamBlastTicket = null;
            _screamBlastMesh = null;
            _screamBlastMat = null;
            _screamBlastSprite = null;
            _screamBlastFrames = null;
            if (_screamFxBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_screamFxBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_screamFxBackMesh != null)
            {
                try
                {
                    _screamFxBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_screamFxBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_screamFxBackMat);
                }
                catch
                {
                }
            }
            _screamFxBackTicket = null;
            _screamFxBackMesh = null;
            _screamFxBackMat = null;
            if (_screamFxFrontTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_screamFxFrontTicket, -1);
                }
                catch
                {
                }
            }
            if (_screamFxFrontMesh != null)
            {
                try
                {
                    _screamFxFrontMesh.destruct();
                }
                catch
                {
                }
            }
            if (_screamFxFrontMat != null)
            {
                try
                {
                    IN.DestroyOne(_screamFxFrontMat);
                }
                catch
                {
                }
            }
            _screamFxFrontTicket = null;
            _screamFxFrontMesh = null;
            _screamFxFrontMat = null;
            _screamParticles.Clear();
            if (_nailArtChargeParticleTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_nailArtChargeParticleTicket, -1);
                }
                catch
                {
                }
            }
            if (_nailArtChargeParticleMesh != null)
            {
                try
                {
                    _nailArtChargeParticleMesh.destruct();
                }
                catch
                {
                }
            }
            if (_nailArtChargeParticleMat != null)
            {
                try
                {
                    IN.DestroyOne(_nailArtChargeParticleMat);
                }
                catch
                {
                }
            }
            _nailArtChargeParticleTicket = null;
            _nailArtChargeParticleMesh = null;
            _nailArtChargeParticleMat = null;
            _nailArtChargeParticles.Clear();
            if (_nailArtGlowTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_nailArtGlowTicket, -1);
                }
                catch
                {
                }
            }
            if (_nailArtGlowMesh != null)
            {
                try
                {
                    _nailArtGlowMesh.destruct();
                }
                catch
                {
                }
            }
            if (_nailArtGlowMat != null)
            {
                try
                {
                    IN.DestroyOne(_nailArtGlowMat);
                }
                catch
                {
                }
            }
            _nailArtGlowTicket = null;
            _nailArtGlowMesh = null;
            _nailArtGlowMat = null;
            _nailArtGlowSprite = null;
            _nailArtGlowFrames = null;
            if (_nailArtSlashFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_nailArtSlashFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_nailArtSlashFxMesh != null)
            {
                try
                {
                    _nailArtSlashFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_nailArtSlashFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_nailArtSlashFxMat);
                }
                catch
                {
                }
            }
            _nailArtSlashFxTicket = null;
            _nailArtSlashFxMesh = null;
            _nailArtSlashFxMat = null;
            _nailArtSlashFxSprite = null;
            _nailArtSlashFxFrames = null;
            if (_dashSlashFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_dashSlashFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_dashSlashFxMesh != null)
            {
                try
                {
                    _dashSlashFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_dashSlashFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_dashSlashFxMat);
                }
                catch
                {
                }
            }
            _dashSlashFxTicket = null;
            _dashSlashFxMesh = null;
            _dashSlashFxMat = null;
            _dashSlashFxSprite = null;
            _dashSlashFxFrames = null;
            if (_cycloneFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_cycloneFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_cycloneFxMesh != null)
            {
                try
                {
                    _cycloneFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_cycloneFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_cycloneFxMat);
                }
                catch
                {
                }
            }
            _cycloneFxTicket = null;
            _cycloneFxMesh = null;
            _cycloneFxMat = null;
            _cycloneFxSprite = null;
            _cycloneFxFrames = null;
            if (_dreamParticleBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_dreamParticleBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_dreamParticleBackMesh != null)
            {
                try
                {
                    _dreamParticleBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_dreamParticleBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_dreamParticleBackMat);
                }
                catch
                {
                }
            }
            _dreamParticleBackTicket = null;
            _dreamParticleBackMesh = null;
            _dreamParticleBackMat = null;
            if (_dreamParticleFrontTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_dreamParticleFrontTicket, -1);
                }
                catch
                {
                }
            }
            if (_dreamParticleFrontMesh != null)
            {
                try
                {
                    _dreamParticleFrontMesh.destruct();
                }
                catch
                {
                }
            }
            if (_dreamParticleFrontMat != null)
            {
                try
                {
                    IN.DestroyOne(_dreamParticleFrontMat);
                }
                catch
                {
                }
            }
            _dreamParticleFrontTicket = null;
            _dreamParticleFrontMesh = null;
            _dreamParticleFrontMat = null;
            _dreamHitParticles.Clear();
            _dreamSwingParticles.Clear();
            if (_diveSwordTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_diveSwordTicket, -1);
                }
                catch
                {
                }
            }
            if (_diveSwordMesh != null)
            {
                try
                {
                    _diveSwordMesh.destruct();
                }
                catch
                {
                }
            }
            if (_diveSwordMat != null)
            {
                try
                {
                    IN.DestroyOne(_diveSwordMat);
                }
                catch
                {
                }
            }
            _diveSwordTicket = null;
            _diveSwordMesh = null;
            _diveSwordMat = null;
            if (_diveSpikeTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_diveSpikeTicket, -1);
                }
                catch
                {
                }
            }
            if (_diveSpikeMesh != null)
            {
                try
                {
                    _diveSpikeMesh.destruct();
                }
                catch
                {
                }
            }
            if (_diveSpikeMat != null)
            {
                try
                {
                    IN.DestroyOne(_diveSpikeMat);
                }
                catch
                {
                }
            }
            _diveSpikeTicket = null;
            _diveSpikeMesh = null;
            _diveSpikeMat = null;
            DashAudio.StopDiveLoop(); // 释放渲染票据：停止下砸循环音
            _diveSwords.Clear();
            _diveSpikes.Clear();
            _diving = false;
            _swimState = 0; // 释放渲染票据：游泳状态复位
            ResetNailArt(); // 释放渲染票据：骨钉技艺复位
            ResetDreamNail(); // 释放渲染票据：梦钉复位
            if (_diveFxBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_diveFxBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_diveFxBackMesh != null)
            {
                try
                {
                    _diveFxBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_diveFxBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_diveFxBackMat);
                }
                catch
                {
                }
            }
            _diveFxBackTicket = null;
            _diveFxBackMesh = null;
            _diveFxBackMat = null;
            if (_diveFxFrontTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_diveFxFrontTicket, -1);
                }
                catch
                {
                }
            }
            if (_diveFxFrontMesh != null)
            {
                try
                {
                    _diveFxFrontMesh.destruct();
                }
                catch
                {
                }
            }
            if (_diveFxFrontMat != null)
            {
                try
                {
                    IN.DestroyOne(_diveFxFrontMat);
                }
                catch
                {
                }
            }
            _diveFxFrontTicket = null;
            _diveFxFrontMesh = null;
            _diveFxFrontMat = null;
            _diveLandParticles.Clear();
            if (_fireballDbgTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_fireballDbgTicket, -1);
                }
                catch
                {
                }
            }
            if (_fireballDbgMesh != null)
            {
                try
                {
                    _fireballDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_fireballDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_fireballDbgMat);
                }
                catch
                {
                }
            }
            _fireballDbgTicket = null;
            _fireballDbgMesh = null;
            _fireballDbgMat = null;
            if (_bossDbgTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_bossDbgTicket, -1);
                }
                catch
                {
                }
            }
            if (_bossDbgMesh != null)
            {
                try
                {
                    _bossDbgMesh.destruct();
                }
                catch
                {
                }
            }
            if (_bossDbgMat != null)
            {
                try
                {
                    IN.DestroyOne(_bossDbgMat);
                }
                catch
                {
                }
            }
            _bossDbgTicket = null;
            _bossDbgMesh = null;
            _bossDbgMat = null;
            if (_lowHpTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_lowHpTicket, -1);
                }
                catch
                {
                }
            }
            if (_lowHpMesh != null)
            {
                try
                {
                    _lowHpMesh.destruct();
                }
                catch
                {
                }
            }
            if (_lowHpMat != null)
            {
                try
                {
                    IN.DestroyOne(_lowHpMat);
                }
                catch
                {
                }
            }
            _lowHpTicket = null;
            _lowHpMesh = null;
            _lowHpMat = null;
            _lowHpParticles.Clear();
            if (_furyGlowTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_furyGlowTicket, -1);
                }
                catch
                {
                }
            }
            if (_furyGlowMesh != null)
            {
                try
                {
                    _furyGlowMesh.destruct();
                }
                catch
                {
                }
            }
            if (_furyGlowMat != null)
            {
                try
                {
                    IN.DestroyOne(_furyGlowMat);
                }
                catch
                {
                }
            }
            if (_furyGlowTex != null)
            {
                try
                {
                    Destroy(_furyGlowTex);
                }
                catch
                {
                }
            }
            _furyGlowTicket = null;
            _furyGlowMesh = null;
            _furyGlowMat = null;
            _furyGlowTex = null;
            if (_voidBackTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_voidBackTicket, -1);
                }
                catch
                {
                }
            }
            if (_voidBackMesh != null)
            {
                try
                {
                    _voidBackMesh.destruct();
                }
                catch
                {
                }
            }
            if (_voidBackMat != null)
            {
                try
                {
                    IN.DestroyOne(_voidBackMat);
                }
                catch
                {
                }
            }
            _voidBackTicket = null;
            _voidBackMesh = null;
            _voidBackMat = null;
            if (_voidSlashTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_voidSlashTicket, -1);
                }
                catch
                {
                }
            }
            if (_voidSlashMesh != null)
            {
                try
                {
                    _voidSlashMesh.destruct();
                }
                catch
                {
                }
            }
            if (_voidSlashMat != null)
            {
                try
                {
                    IN.DestroyOne(_voidSlashMat);
                }
                catch
                {
                }
            }
            _voidSlashTicket = null;
            _voidSlashMesh = null;
            _voidSlashMat = null;
            _voidSlashTargets.Clear();
            _nailArtHitEnemies.Clear();
            if (_lifebloodGlowTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_lifebloodGlowTicket, -1);
                }
                catch
                {
                }
            }
            if (_lifebloodGlowMesh != null)
            {
                try
                {
                    _lifebloodGlowMesh.destruct();
                }
                catch
                {
                }
            }
            if (_lifebloodGlowMat != null)
            {
                try
                {
                    IN.DestroyOne(_lifebloodGlowMat);
                }
                catch
                {
                }
            }
            if (_lifebloodGlowTex != null)
            {
                try
                {
                    Destroy(_lifebloodGlowTex);
                }
                catch
                {
                }
            }
            _lifebloodGlowTicket = null;
            _lifebloodGlowMesh = null;
            _lifebloodGlowMat = null;
            _lifebloodGlowTex = null;
            if (_baldurShellTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_baldurShellTicket, -1);
                }
                catch
                {
                }
            }
            if (_baldurShellMesh != null)
            {
                try
                {
                    _baldurShellMesh.destruct();
                }
                catch
                {
                }
            }
            if (_baldurShellMat != null)
            {
                try
                {
                    IN.DestroyOne(_baldurShellMat);
                }
                catch
                {
                }
            }
            _baldurShellTicket = null;
            _baldurShellMesh = null;
            _baldurShellMat = null;
            if (_hitFxTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_hitFxTicket, -1);
                }
                catch
                {
                }
            }
            if (_hitFxMesh != null)
            {
                try
                {
                    _hitFxMesh.destruct();
                }
                catch
                {
                }
            }
            if (_hitFxMat != null)
            {
                try
                {
                    IN.DestroyOne(_hitFxMat);
                }
                catch
                {
                }
            }
            _hitFxTicket = null;
            _hitFxMesh = null;
            _hitFxMat = null;
            _hitFxParticles.Clear();
            if (_rcTicket != null && _mp != null)
            {
                try
                {
                    _mp.MovRenderer.deassignDrawable(_rcTicket, -1);
                }
                catch
                {
                }
            }
            if (_rcMesh != null)
            {
                try
                {
                    _rcMesh.destruct();
                }
                catch
                {
                }
            }
            if (_rcMat != null)
            {
                try
                {
                    IN.DestroyOne(_rcMat);
                }
                catch
                {
                }
            }
            for (int i = 0; i < _ghostNodes.Count; i++)
            {
                GhostNode node = _ghostNodes[i];
                if (node.Ticket != null && _mp != null)
                {
                    try
                    {
                        _mp.MovRenderer.deassignDrawable(node.Ticket, -1);
                    }
                    catch
                    {
                    }
                }
                if (node.Mesh != null)
                {
                    try
                    {
                        node.Mesh.destruct();
                    }
                    catch
                    {
                    }
                }
                if (node.Mat != null)
                {
                    try
                    {
                        IN.DestroyOne(node.Mat);
                    }
                    catch
                    {
                    }
                }
                node.Ticket = null;
                node.Mesh = null;
                node.Ghost = null;
                node.Mat = null;
            }
            _ghostNodes.Clear();
            if (_mesh != null)
            {
                try
                {
                    _mesh.destruct();
                }
                catch
                {
                }
            }
            _ticket = null;
            _mesh = null;
            _fxTicket = null;
            _fxMesh = null;
            _fxMat = null;
            _djWingTicket = null;
            _djWingMesh = null;
            _djWingMat = null;
            _whiteTex = null;
            _rcTicket = null;
            _rcMesh = null;
            _rcMat = null;
        }

        private bool KnightPrepareMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mesh == null || _mp == null || _currentTex == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }

            _prepareCount++;

            // 必须用 pixel2ux/pixel2uy（和角色渲染同款：居中坐标 ÷ 64），
            // map2mesh 少了 ÷64，会导致实体被画到 64 倍远的位置
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));

            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = _currentTex.width * scale;
            float h = _currentTex.height * scale;
            // 下砸动画缩小（前摇/下落/落地统一缩放）
            if (_currentClip == "Dive Antic" || _currentClip == "Dive Fall" ||
                _currentClip == "Dive Land")
            {
                w *= DiveAnimScale;
                h *= DiveAnimScale;
            }

            float feetFrac = 1f;
            if (_currentSpriteName != null && _feetFraction.TryGetValue(_currentSpriteName, out float ff))
            {
                feetFrac = ff;
            }
            // 脚对齐实体脚底：实体脚在中心下方 SizeY*CLEN 像素处（网格 y 向上，取负号）
            float offsetY = -SizeY * _mp.CLEN - (0.5f - feetFrac) * h;
            // 帧内容水平居中修正：内容相对帧中心的偏移（像素）在渲染时反向平移，消除左右晃动。
            // 面朝右（_faceDir<0）时贴图水平镜像，内容偏移方向相反，符号取反。
            float offsetX = 0f;
            if (_currentSpriteName != null && _spriteCenterOffX.TryGetValue(_currentSpriteName, out float co))
            {
                offsetX = (_faceDir < 0f ? co : -co) * scale;
            }

            _mesh.clearSimple();
            _mesh.Col = MTRX.ColWhite;
            _mesh.initForImgAndTexture(_currentTex);
            _mesh.uv_top = 0f;
            _mesh.uv_height = 1f;
            if (_faceDir < 0f)
            {
                _mesh.uv_left = 1f;
                _mesh.uv_width = -1f;
            }
            // 向下冲刺：本体旋转 90°；暗影冲刺的收尾静止段（shadow_dash0009~0011，
            // 即 _dashTimer <= _dashEndTime）保持原样不旋转
            if (_dashing && _dashDir == 0f &&
                !(_isShadowDash && _dashTimer <= _dashEndTime))
            {
                // 向下冲刺（冲刺大师）：精灵绕自身中心旋转 90°——
                // 面朝左（_faceDir>0）逆时针，面朝右（_faceDir<0）顺时针
                // 注意：Rect 内部按 ppu(64) 把像素换算成网格单位，矩阵平移也必须用网格单位
                float rotR = _faceDir > 0f ? 1.5707964f : -1.5707964f; // ±π/2
                float cx = offsetX / 64f;
                float cy = offsetY / 64f;
                Matrix4x4 savedM = _mesh.getCurrentMatrix();
                _mesh.Translate(cx, cy, true);
                _mesh.Rotate(rotR, true);
                _mesh.Translate(-cx, -cy, true);
                _mesh.Rect(offsetX, offsetY, w, h, false);
                _mesh.setCurrentMatrix(savedM, false);
            }
            else
            {
                _mesh.Rect(offsetX, offsetY, w, h, false);
            }

            MdOut = _mesh;
            return true;
        }

        private bool KnightPrepareFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _fxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }

            // 先清空；没有剑气时返回 true（空网格）保持票据存活
            _fxMesh.clearSimple();
            if (_fxTex == null)
            {
                MdOut = _fxMesh;
                return true;
            }

            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            // 上劈：正上方；下劈：正下方；平砍：脸朝向前方
            // 前方 = -_faceDir（_faceDir=1 表示脸朝左，世界坐标前方是 -X）
            float fx;
            float fy;
            // 修长之钉/骄傲印记的“螳螂爪”样式剑气（M 版剪辑）单独做尺寸/位置微调，
            // 直接看当前特效剪辑名，保证只有真的在播 M 版时生效（亡者之怒 F 版不套用）。
            bool mantisFx = !string.IsNullOrEmpty(_fxClipName) &&
                _fxClipName.EndsWith(" M", StringComparison.Ordinal);
            // 单帧微调：只对螳螂爪样式里那两个特定帧生效
            bool mantisUp001 = mantisFx && _fxSpriteName == "mantis_up_slash0001";
            bool mantisDown002 = mantisFx && _fxSpriteName == "mantis_down_slash0002";
            // 默认横劈剑气的收招两帧（0001~0002）：朝攻击方向再拉伸 0.2 格
            bool slashFrontGrow = _fxSpriteName == "slashes_effect0001" || _fxSpriteName == "slashes_effect0002";
            if (_upSlash)
            {
                fx = -_faceDir * UpSlashFxOffsetX;
                // 护符18/19：上劈渲染向上拉伸，中心上移拉伸量的一半
                fy = UpSlashFxOffsetY + CellToUx(CharmEffects.UpSlashStretchUp() * 0.5f);
                // 螳螂爪样式（累计）：向上拉伸 0.2 格、向下拉伸 0.9 格
                // → 中心净下移 (0.9-0.2)/2 = 0.35 格（两端都长，下边多长一点）
                if (mantisFx)
                {
                    fy += CellToUx((MantisUpSlashStretchUp - MantisUpSlashStretchDown) * 0.5f);
                }
                else if (!CharmEffects.LongNailVisual())
                {
                    // 不佩戴修长之钉/骄傲印记：渲染再向下拉伸 0.8 格（上边缘不动 → 中心下移 0.4 格）
                    fy -= CellToUx(PlainUpSlashFxDown * 0.5f);
                }
                // 上劈剑气 0001 帧起向右微调（面朝左时向右、面朝右时镜像向左）
                // 修长之钉/骄傲印记的螳螂爪样式（mantis_up_slash0001）尺寸几乎一致，套用同一微调
                if (_fxSpriteName == "up_slash_effect0001" || _fxSpriteName == "mantis_up_slash0001")
                {
                    fx += _faceDir * UpSlashFxShiftX;
                }
                // 螳螂爪 0001 帧：竖直平移（累计 = 净下移 0.2 格）；
                // 横向 = 纯平移 0.2 格 + 两侧不对称外扩带来的中心位移 (右-左)/2 = 0.1 格
                if (mantisUp001)
                {
                    fy += CellToUx(MantisUpSlash0001ShiftY);
                    float edgeCenter = (MantisUpSlash0001GrowRight - MantisUpSlash0001GrowLeft) * 0.5f;
                    fx += _faceDir * (CellToUx(MantisUpSlash0001ShiftX)
                        + CellToUx(edgeCenter));
                }
            }
            else if (_downSlash)
            {
                fx = DownSlashFxOffsetX;
                // 基础向上拉伸（中心上移 0.2）+ 护符向下拉伸（中心下移一半）
                fy = DownSlashFxOffsetY + CellToUx(DownSlashStretchUp * 0.5f
                    - CharmEffects.DownSlashStretchDown() * 0.5f);
                // 螳螂爪样式（累计）：向上拉伸 0.5 格、向下拉伸 0.2 格
                // → 中心净上移 (0.5-0.2)/2 = 0.15 格
                if (mantisFx)
                {
                    fy += CellToUx((MantisDownSlashStretchUp - MantisDownSlashStretchDown) * 0.5f);
                }
                else if (!CharmEffects.LongNailVisual())
                {
                    // 不佩戴修长之钉/骄傲印记：渲染再向上拉伸 0.8 格（下边缘不动 → 中心上移 0.4 格）
                    fy += CellToUx(PlainDownSlashFxUp * 0.5f);
                }
                // 螳螂爪 0002 帧：整体向右（面朝左时）平移 0.5 格
                if (mantisDown002)
                {
                    fx += _faceDir * CellToUx(MantisDownSlash0002ShiftX);
                }
            }
            else
            {
                // 护符18/19 长钉/骄傲印记：平砍剑气渲染向面朝方向平移（可叠加）
                // LongRangeShift 以“格”为单位，先换算成 ux 再应用（与 HitboxOffsetX 同单位）
                float lshift = CellToUx(CharmEffects.LongRangeShift());
                // 拉伸只拉骑士侧：渲染中心再向身体方向移“拉伸量的一半”
                float stretchShift = CellToUx(CharmEffects.LongRangeStretch() * 0.5f);
                fx = -_faceDir * (SlashFxOffsetX + lshift) + _faceDir * stretchShift;
                fy = SlashFxOffsetY;
                // 螳螂爪样式：整体向身体侧收缩 0.2 格（骑士侧边缘不动 → 中心回移 0.1 格）
                if (mantisFx)
                {
                    fx += _faceDir * CellToUx(MantisSlashPullBack * 0.5f);
                }
                // 默认横劈剑气 0001~0002 帧：朝攻击方向拉伸 0.2 格 → 中心朝攻击方向移 0.1 格
                else if (slashFrontGrow)
                {
                    fx -= _faceDir * CellToUx(SlashEffectFrontGrow * 0.5f);
                }
            }
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx + fx, my + fy, 0f));

            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            // 剑气单独放大（SlashFxScale），骑士本体仍用公共 scale，不受影响；
            // 上劈/下劈剑气再乘各自的缩小系数，视觉更紧凑
            float fxScale = _upSlash
                ? SlashFxScale * UpSlashFxScale
                : _downSlash
                    ? SlashFxScale * DownSlashFxScale
                    : SlashFxScale;
            float w = _fxTex.width * scale * fxScale;
            // 护符18/19 长钉/骄傲印记：平砍剑气渲染长度增加（可叠加）；
            // 亡者之怒的上劈/下劈渲染同样套用长度倍率（保持与平砍一致）
            bool horizontalCharm = !_upSlash && !_downSlash;
            bool furyUpDownCharm = FuryActive && (_upSlash || _downSlash);
            if (horizontalCharm || furyUpDownCharm)
            {
                w *= CharmEffects.LongRangeMultiplier();
            }
            if (horizontalCharm)
            {
                // 骄傲印记“只向右拉伸”：渲染宽额外增加拉伸量（格 → mesh px）
                w += CharmEffects.LongRangeStretch() * _mp.CLEN;
            }
            float h = _fxTex.height * scale * fxScale;
            // 护符18/19 长钉/骄傲印记：平砍剑气渲染高度增加（可叠加）；
            // 亡者之怒的上劈/下劈渲染同样套用高度倍率
            if (horizontalCharm || furyUpDownCharm)
            {
                h *= CharmEffects.LongRangeHeightMultiplier();
            }
            // 护符18/19：上劈渲染高向上拉伸（格 → mesh px），下边缘不动
            if (_upSlash)
            {
                h += CharmEffects.UpSlashStretchUp() * _mp.CLEN;
            }
            // 下劈：基础向上拉伸 + 护符向下拉伸（渲染高同步增加）
            if (_downSlash)
            {
                h += (DownSlashStretchUp + CharmEffects.DownSlashStretchDown()) * _mp.CLEN;
            }
            // 螳螂爪样式剑气的尺寸微调（单位：格 → mesh px，与上面的拉伸同一套换算）
            if (mantisFx)
            {
                if (_upSlash)
                {
                    // 上劈：向上 0.2 + 向下 0.9 格，左右各 0.5 格
                    h += (MantisUpSlashStretchUp + MantisUpSlashStretchDown) * _mp.CLEN;
                    w += MantisUpSlashGrowX * 2f * _mp.CLEN;
                }
                else if (_downSlash)
                {
                    // 下劈：向上 0.5 + 向下 0.2 格，左右各 0.3 格
                    h += (MantisDownSlashStretchUp + MantisDownSlashStretchDown) * _mp.CLEN;
                    w += MantisDownSlashGrowX * 2f * _mp.CLEN;
                }
                else
                {
                    // 横劈：向身体侧收缩 0.2 格（中心已回移一半）+ 上下各拉伸 0.2 格（左右普攻对称）
                    w -= MantisSlashPullBack * _mp.CLEN;
                    h += MantisSlashGrowY * 2f * _mp.CLEN;
                }
                // 上劈 0001 帧：横向两侧外扩合计（右 0.3 + 左 0.1 = +0.4 格）
                if (mantisUp001)
                {
                    w += (MantisUpSlash0001GrowRight + MantisUpSlash0001GrowLeft) * _mp.CLEN;
                }
            }
            else if (!CharmEffects.LongNailVisual())
            {
                // 不佩戴修长之钉/骄傲印记：
                //   上劈渲染向下拉伸 0.8 格、下劈渲染向上拉伸 0.8 格；
                //   上/下劈渲染再左右各拉伸 0.2 格（与判定框同步）
                if (_upSlash)
                {
                    h += PlainUpSlashFxDown * _mp.CLEN;
                    w += PlainUpSlashFxWidenX * 2f * _mp.CLEN;
                }
                else if (_downSlash)
                {
                    h += PlainDownSlashFxUp * _mp.CLEN;
                    w += PlainDownSlashFxWidenX * 2f * _mp.CLEN;
                }
            }
            // 默认横劈剑气 0001~0002 帧：朝攻击方向再拉伸 0.2 格（宽度 +0.2，中心位移已在位置处补偿）
            if (slashFrontGrow && !_upSlash && !_downSlash)
            {
                w += SlashEffectFrontGrow * _mp.CLEN;
            }

            _fxMesh.Col = MTRX.ColWhite;
            _fxMesh.initForImgAndTexture(_fxTex);
            _fxMesh.uv_top = 0f;
            _fxMesh.uv_height = 1f;
            if (_faceDir < 0f)
            {
                _fxMesh.uv_left = 1f;
                _fxMesh.uv_width = -1f;
            }
            _fxMesh.Rect(0f, 0f, w, h, false);

            MdOut = _fxMesh;
            return true;
        }

        /// <summary>
        /// 二段跳白色光翼：骑士背后向两侧展开的半透明发光羽翼，0.3 秒内淡出。
        /// </summary>
        /// <summary>
        /// 二段跳白色光翼：播放 sheets/wings/ 序列帧，左右张开，0.3 秒内淡出。
        /// </summary>
        private bool KnightPrepareDjWingMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _djWingMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _djWingMesh.clearSimple();
            if (_wingTimer <= 0f || _whiteTex == null)
            {
                MdOut = _djWingMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my - 0.2f, 0f)); // 翅膀渲染在小骑士中心下方 0.2
            float clen = _mp.CLEN;
            float progress = 1f - _wingTimer / WingFxTime; // 0→1（0.2 秒）
            float wingAlpha = Mathf.Clamp01(1f - progress); // 从完全不透明快速变透明
            // 张开角：以竖直向上为基准线，两侧翅膀之间夹角从 320° 匀速收拢到 60°。
            // 两翼用同一角度（右翼=90°−θ/2），靠 dir 的 X 镜像分边，
            // 避免“角度差异 + 镜像”双重翻转导致两翼跑到同一侧。
            float openAng = Mathf.Lerp(320f, 60f, progress) * Mathf.Deg2Rad;
            float wingAng = 90f * Mathf.Deg2Rad - openAng * 0.5f;
            _djWingMesh.initForImgAndTexture(_whiteTex);
            DrawWingRotated(_djWingMesh, 1f, clen, wingAlpha, wingAng);
            DrawWingRotated(_djWingMesh, -1f, clen, wingAlpha, wingAng);
            // 整体再叠加一层：同位置再画一遍，亮度翻倍
            DrawWingRotated(_djWingMesh, 1f, clen, wingAlpha, wingAng);
            DrawWingRotated(_djWingMesh, -1f, clen, wingAlpha, wingAng);
            MdOut = _djWingMesh;
            return true;
        }
        /// <summary>
        /// 画一只以根部为轴旋转张开的白色羽翼：
        /// 局部坐标 (u,v) 沿翼展方向定义轮廓（约 17 顶点、后缘三道羽毛缺口），
        /// 整体绕根部旋转 ang 角；亮度按距根部距离渐变（尖端白炽发光）。
        /// dir=+1 右翼，dir=-1 左翼（X 镜像）。
        /// </summary>
        private static void DrawWingRotated(MeshDrawer md, float dir, float clen, float wingAlpha, float ang)
        {
            // 羽翼局部轮廓（u=沿翼展根→尖，v=羽片展开方向）
            // 后缘改为平缓波浪线（6~8 个过渡顶点），替代尖锐锯齿；前缘与尖端保持不变
            float[] us = { 0.12f, 0.45f, 0.95f, 1.55f, 2.15f, 2.70f, 3.15f, 2.88f, 2.58f, 2.28f, 1.98f, 1.68f, 1.38f, 1.08f, 0.72f, 0.32f, 0.12f };
            float[] vs = { 0.20f, 0.45f, 0.70f, 0.85f, 0.82f, 0.62f, 0.40f, 0.34f, 0.38f, 0.26f, 0.30f, 0.16f, 0.20f, 0.06f, 0.00f, -0.10f, -0.10f };
            float rootX = 0.28f; // 羽翼根部：骑士背部（固定不动）
            float rootY = 0.10f;
            float cosA = Mathf.Cos(ang);
            float sinA = Mathf.Sin(ang);
            float maxU = 3.2f;
            int n = us.Length;
            // 填充：从根部中心扇形，亮度随 u 增大（尖端更亮、白炽边缘）
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float uFrac = Mathf.Clamp01((us[i] + us[j]) * 0.5f / maxU);
                float a = Mathf.Lerp(0.55f, 1.0f, uFrac) * wingAlpha;
                float ax = dir * (rootX + us[i] * cosA - vs[i] * sinA) * WingScale * clen;
                float ay = -(rootY + us[i] * sinA + vs[i] * cosA) * WingScale * clen;
                float bx = dir * (rootX + us[j] * cosA - vs[j] * sinA) * WingScale * clen;
                float by = -(rootY + us[j] * sinA + vs[j] * cosA) * WingScale * clen;
                float px = dir * (rootX + 0.30f * cosA - 0.05f * sinA) * WingScale * clen;
                float py = -(rootY + 0.30f * sinA + 0.05f * cosA) * WingScale * clen;
                md.Col = new Color(1f, 1f, 1f, a);
                // 两个绕序都画，避免背面剔除
                md.Triangle(px, py, ax, ay, bx, by, false);
                md.Triangle(px, py, bx, by, ax, ay, false);
            }
            // 三片放射状羽毛骨架：从同一根部原点向 上30°/水平/下30° 三个方向炸开
            float bladeAlpha = Mathf.Clamp01(wingAlpha * 2.2f);
            DrawWingBlade(md, dir, clen, bladeAlpha, ang, 25f, 1.8f, 0.16f);   // 上分叉：斜上方 25°
            DrawWingBlade(md, dir, clen, bladeAlpha, ang, 0f, 2.4f, 0.18f);    // 中分叉：水平
            DrawWingBlade(md, dir, clen, bladeAlpha, ang, -25f, 3.0f, 0.16f);  // 下分叉：斜下方 25°

            // 补全分叉之间的扇形白色填充（下分叉伸出轮廓之外，轮廓填充盖不到，需单独补）
            FillBladeSector(md, dir, clen, wingAlpha, ang, 0f, 2.4f, -25f, 3.0f); // 中-下 扇形
            FillBladeSector(md, dir, clen, wingAlpha, ang, 0f, 2.4f, 25f, 1.8f);  // 中-上 扇形
        }

        /// <summary>
        /// 从骨架根部原点，在两个分叉方向之间填满白色放射状扇形（延伸到分叉尖端），
        /// 让三根骨架和三个区域连成完整飞蛾翅膀外观。
        /// </summary>
        private static void FillBladeSector(MeshDrawer md, float dir, float clen, float wingAlpha, float ang,
            float a1, float len1, float a2, float len2)
        {
            float cosA = Mathf.Cos(ang);
            float sinA = Mathf.Sin(ang);
            float rootX = 0.28f;
            float rootY = 0.10f;
            float rx = dir * rootX * WingScale * clen;
            float ry = -(rootY) * WingScale * clen;
            int steps = 5;
            for (int i = 0; i < steps; i++)
            {
                float t0 = (float)i / steps;
                float t1 = (float)(i + 1) / steps;
                float deg0 = Mathf.Lerp(a1, a2, t0);
                float deg1 = Mathf.Lerp(a1, a2, t1);
                float l0 = Mathf.Lerp(len1, len2, t0);
                float l1 = Mathf.Lerp(len1, len2, t1);
                float r0 = deg0 * Mathf.Deg2Rad;
                float r1 = deg1 * Mathf.Deg2Rad;
                float u0 = Mathf.Cos(r0) * l0;
                float v0 = Mathf.Sin(r0) * l0;
                float u1 = Mathf.Cos(r1) * l1;
                float v1 = Mathf.Sin(r1) * l1;
                float p0x = dir * (rootX + u0 * cosA - v0 * sinA) * WingScale * clen;
                float p0y = -(rootY + u0 * sinA + v0 * cosA) * WingScale * clen;
                float p1x = dir * (rootX + u1 * cosA - v1 * sinA) * WingScale * clen;
                float p1y = -(rootY + u1 * sinA + v1 * cosA) * WingScale * clen;
                float uFrac = Mathf.Clamp01(((l0 + l1) * 0.5f) / 3.2f);
                float a = Mathf.Lerp(0.55f, 1.0f, uFrac) * wingAlpha;
                md.Col = new Color(1f, 1f, 1f, a);
                md.Triangle(rx, ry, p0x, p0y, p1x, p1y, false);
                md.Triangle(rx, ry, p1x, p1y, p0x, p0y, false);
            }
        }

        /// <summary>
        /// 生成一个散射光点：骑士周围随机位置，独立下落速度 + 随机水平飘移。
        /// </summary>
        private void SpawnLightDot()
        {
            _dots.Add(new LightDotParticle
            {
                X = X + UnityEngine.Random.Range(-0.35f, 0.35f),
                Y = Y + UnityEngine.Random.Range(-0.2f, 0.25f),
                Vx = UnityEngine.Random.Range(-0.08f, 0.08f),
                Vy = UnityEngine.Random.Range(0.6f, 1.8f), // 快速下坠
                TexIndex = UnityEngine.Random.Range(0, DotShapeCount),
                Age = 0f,
                Life = UnityEngine.Random.Range(LightDotLifeMin, LightDotLifeMax),
                Size = UnityEngine.Random.Range(0.12f, 0.24f)
            });
        }

        /// <summary>
        /// 生成一个暗影冲刺黑色粒子：骑士当前位置，Y 轴 ±0.2 随机偏差，
        /// 微弱随机 Vy 上下飘散，生命周期 0.2~0.4 秒。
        /// </summary>
        private void SpawnShadowDot()
        {
            _shadowParticles.Add(new LightDotParticle
            {
                X = X,
                Y = Y + UnityEngine.Random.Range(-0.1f, 1.2f),
                Vx = UnityEngine.Random.Range(-0.1f, 0.1f),
                Vy = UnityEngine.Random.Range(-0.2f, 0.2f), // 随机上下飘散
                TexIndex = 1, // 圆形贴图（MakeDotDotTexture）
                Age = 0f,
                Life = UnityEngine.Random.Range(ShadowDotLifeMin, ShadowDotLifeMax),
                Size = UnityEngine.Random.Range(0.2f, 0.25f),
                Index = _shadowParticles.Count // 生成顺序，用于冲刺结束后的按序淡出
            });
        }

        /// <summary>
        /// 凝聚期间持续生成白色条状线：围绕身体（水平 ±0.55 格），自下往上升起（y 负=上），速度较快。
        /// </summary>
        private void SpawnFocusFx()
        {
            _focusFxSpawnTimer -= Time.deltaTime;
            if (_focusFxSpawnTimer > 0f)
            {
                return;
            }
            _focusFxSpawnTimer = FocusFxSpawnInterval;
            for (int i = 0; i < 3; i++)
            {
                _focusParticles.Add(new LightDotParticle
                {
                    X = X + UnityEngine.Random.Range(-0.55f, 0.55f),
                    Y = Y + UnityEngine.Random.Range(-0.25f, 1.1f),
                    Vx = UnityEngine.Random.Range(-0.06f, 0.06f),
                    Vy = -UnityEngine.Random.Range(0.95f, 1.45f), // 更快地向上
                    TexIndex = 2, // 白色长条贴图
                    Age = 0f,
                    Life = UnityEngine.Random.Range(0.3f, 0.5f),
                    Size = UnityEngine.Random.Range(0.14f, 0.26f),
                    Color = Color.white,
                    Index = 0,
                    Layer = UnityEngine.Random.value < 0.5f ? 0 : 1 // 前后层各一半
                });
            }
        }
        /// <summary>
        /// 画一片放射状细长羽毛：从根部原点沿 bladeDeg（相对翼展方向）伸展 length 的亮色三角形。
        /// </summary>
        private static void DrawWingBlade(MeshDrawer md, float dir, float clen, float alpha, float ang,
            float bladeDeg, float length, float halfW)
        {
            float cosA = Mathf.Cos(ang);
            float sinA = Mathf.Sin(ang);
            float rootX = 0.28f;
            float rootY = 0.10f;
            float bRad = bladeDeg * Mathf.Deg2Rad;
            float ub = Mathf.Cos(bRad) * length;
            float vb = Mathf.Sin(bRad) * length;
            // 尖端世界坐标（相对骑士中心，整体再旋转 ang）
            float tipX = rootX + ub * cosA - vb * sinA;
            float tipY = rootY + ub * sinA + vb * cosA;
            float pxx = -sinA * halfW;
            float pyy = cosA * halfW;
            float ax = dir * (rootX + pxx) * WingScale * clen;
            float ay = -(rootY + pyy) * WingScale * clen;
            float bx = dir * (rootX - pxx) * WingScale * clen;
            float by = -(rootY - pyy) * WingScale * clen;
            float tx = dir * tipX * WingScale * clen;
            float ty = -tipY * WingScale * clen;
            md.Col = new Color(1f, 1f, 1f, alpha);
            md.Triangle(ax, ay, bx, by, tx, ty, false);
            md.Triangle(ax, ay, tx, ty, bx, by, false);
        }
        /// <summary>
        /// 二段跳 moth 光点：绝对世界坐标绘制（不跟随骑士），独立物理下坠 + 序列帧播放。
        /// 每个网格只画对应帧索引的光点。
        /// </summary>
        private bool KnightPrepareDjDotMesh(int texIndex, Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _djDotMesh[texIndex] == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _djDotMesh[texIndex].clearSimple();
            if (_dotTexes[texIndex] == null)
            {
                MdOut = _djDotMesh[texIndex];
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            bool any = false;
            _djDotMesh[texIndex].initForImgAndTexture(_dotTexes[texIndex]);
            for (int i = 0; i < _dots.Count; i++)
            {
                LightDotParticle d = _dots[i];
                if (d.TexIndex != texIndex)
                {
                    continue;
                }
                any = true;
                float alpha = (1f - d.Age / d.Life) * 1.0f;
                float size = d.Size * _mp.CLEN;
                // 与翅膀同一单位：Rect 偏移 = 格差 × CLEN（绝对世界坐标，不随骑士移动）
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                _djDotMesh[texIndex].Col = new Color(1f, 1f, 1f, alpha);
                _djDotMesh[texIndex].Rect(dxm, dym, size, size, false);
            }
            if (!any)
            {
                _djDotMesh[texIndex].clearSimple();
            }
            MdOut = _djDotMesh[texIndex];
            return true;
        }

        /// <summary>
        /// 暗影冲刺黑色粒子：绝对世界坐标绘制（不跟随骑士），黑色圆点独立飘散。
        /// </summary>
        private bool KnightPrepareShadowDashDotMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _shadowDotMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _shadowDotMesh.clearSimple();
            if (_dotTexes[1] == null)
            {
                MdOut = _shadowDotMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            bool any = false;
            _shadowDotMesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _shadowParticles.Count; i++)
            {
                LightDotParticle d = _shadowParticles[i];
                // 淡入：生成后 0.03s 内快速淡入
                float alpha = Mathf.Clamp01(d.Age / 0.03f);
                if (_shadowDotFading)
                {
                    // 先保持全量可见 0.5s，再按生成顺序逐个淡出
                    float t = Mathf.Max(0f, _shadowDotFadeTimer - ShadowDotFadeDelay);
                    float step = _shadowDotFadeTotal > 0 ? ShadowDotFadeTime / _shadowDotFadeTotal : ShadowDotFadeTime;
                    float fadeOut = 1f - (t - d.Index * step) / step;
                    alpha = Mathf.Min(alpha, Mathf.Clamp01(fadeOut));
                }
                if (alpha < 0.04f)
                {
                    continue; // 几乎不可见，跳过不画
                }
                any = true;
                float size = d.Size * _mp.CLEN;
                // 绝对世界坐标：格差 × CLEN（不随骑士移动）
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                // 关键：黑色粒子（不是白色）
                _shadowDotMesh.Col = new Color(0f, 0f, 0f, alpha);
                // 叠 3 层增强可见度（特效本身偏淡）
                for (int li = 0; li < 3; li++)
                {
                    _shadowDotMesh.Rect(dxm, dym, size, size, false);
                }
            }
            if (!any)
            {
                _shadowDotMesh.clearSimple();
            }
            MdOut = _shadowDotMesh;
            return true;
        }

        /// <summary>凝聚白色条状线（骑士身后层）：只画 Layer==0 的粒子。</summary>
        private bool KnightPrepareFocusFxBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareFocusFxLayer(_focusFxBackMesh, 0, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>凝聚白色条状线（骑士身前层）：只画 Layer==1 的粒子。</summary>
        private bool KnightPrepareFocusFxFrontMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareFocusFxLayer(_focusFxFrontMesh, 1, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>
        /// 凝聚白色条状线：围绕身体自下往上升起，绝对世界坐标绘制，淡入淡出。
        /// 叠 3 层全白提升亮度；按 layer 只画对应分层的粒子。
        /// </summary>
        private bool PrepareFocusFxLayer(MeshDrawer mesh, int layer, Camera Cam, M2RenderTicket Tk,
            int draw_id, out MeshDrawer MdOut)
        {
            MdOut = null;
            if (_mp == null || mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            mesh.clearSimple();
            if (_dotTexes[2] == null || _focusParticles.Count == 0)
            {
                MdOut = mesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            bool any = false;
            mesh.initForImgAndTexture(_dotTexes[2]);
            for (int i = 0; i < _focusParticles.Count; i++)
            {
                LightDotParticle d = _focusParticles[i];
                if (d.Layer != layer)
                {
                    continue;
                }
                // 快速淡入（0.04s），寿命末尾 0.1s 淡出
                float alpha = Mathf.Clamp01(d.Age / 0.04f) *
                              Mathf.Clamp01((d.Life - d.Age) / 0.1f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                any = true;
                float w = d.Size * 0.28f * _mp.CLEN;  // 更细的条
                float h = d.Size * 2.6f * _mp.CLEN;   // 竖向长条
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                mesh.Col = new Color(1f, 1f, 1f, alpha);
                // 叠 3 层：亮度明显提高
                for (int li = 0; li < 3; li++)
                {
                    mesh.Rect(dxm, dym, w, h, false);
                }
            }
            if (!any)
            {
                mesh.clearSimple();
            }
            MdOut = mesh;
            return true;
        }

        /// <summary>
        /// 水晶之心：蓄力水晶（骑士身前层）。
        /// 站在地上：底部对齐脚底（原点下移 1.2 格），左右对称逐帧出现，碎片间距 20px；
        /// 爬墙：底部对齐原点高度，碎片在靠墙一侧从墙上生长出来，
        /// 左墙顺时针旋转 90°、右墙逆时针旋转 90°。
        /// </summary>
        private bool KnightPrepareSuperCrystalMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _sdCrystalMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _sdCrystalMesh.clearSimple();
            if (_sdCrystalShown <= 0 || _sdCrystalAtlasTex == null)
            {
                MdOut = _sdCrystalMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            _sdCrystalMesh.Col = MTRX.ColWhite;
            _sdCrystalMesh.initForImgAndTexture(_sdCrystalAtlasTex);
            bool onWallNow = _onWall;
            int ori = 0;
            if (onWallNow)
            {
                // 左墙=逆时针(2)，右墙=顺时针(1)——尖端朝外（远离墙）
                ori = _wallDir < 0f ? 2 : 1;
            }
            for (int i = 0; i < _sdCrystalShown && i < 8; i++)
            {
                float w = _sdCrystalDrawW[ori][i] * scale * 0.7f;
                float h = _sdCrystalDrawH[ori][i] * scale * 0.7f;
                if (onWallNow)
                {
                    // 贴墙：两簇水晶以骑士高度为中心，沿墙向上/向下生长，
                    // x 固定在靠墙一侧（约骑士半身宽，贴近墙表面）；整体 y 降低 0.3 格
                    float wallX = 11f;
                    float x = _wallDir < 0f ? -wallX : wallX;
                    float wallY = -0.3f * _mp.CLEN;
                    float off = (i + 1) * 4f;
                    DrawSuperCrystalQuad(i, ori, x, wallY + off, w, h, false);          // 向上簇
                    DrawSuperCrystalQuad(i, ori, x, wallY - off, w, h, false, true);    // 向下簇（垂直镜像）
                }
                else
                {
                    // 地面：左右对称逐帧出现，底部对齐脚底（原点下移 1.2 格后再低 3px），间距 4px
                    float off = (i + 1) * 4f;
                    float cy = -1.2f * _mp.CLEN + h * 0.5f - 3f;
                    DrawSuperCrystalQuad(i, ori, off, cy, w, h, false);  // 右侧
                    DrawSuperCrystalQuad(i, ori, -off, cy, w, h, true);  // 左侧（镜像）
                }
            }
            MdOut = _sdCrystalMesh;
            return true;
        }

        /// <summary>从水晶图集按子 UV 画一个碎片；mirror=true 左右镜像，vflip=true 上下镜像。</summary>
        private void DrawSuperCrystalQuad(int i, int ori, float x, float cy, float w, float h, bool mirror, bool vflip = false)
        {
            _sdCrystalMesh.uv_left = mirror
                ? _sdCrystalUvLeft[ori][i] + _sdCrystalUvW[ori][i]
                : _sdCrystalUvLeft[ori][i];
            _sdCrystalMesh.uv_width = mirror ? -_sdCrystalUvW[ori][i] : _sdCrystalUvW[ori][i];
            _sdCrystalMesh.uv_top = vflip
                ? _sdCrystalUvTop[ori][i] + _sdCrystalUvH[ori][i]
                : _sdCrystalUvTop[ori][i];
            _sdCrystalMesh.uv_height = vflip ? -_sdCrystalUvH[ori][i] : _sdCrystalUvH[ori][i];
            _sdCrystalMesh.Rect(x, cy, w, h, false);
        }

        /// <summary>把 8 个水晶碎片合成一张纵向图集，记录每帧的子 UV 与绘制尺寸。</summary>
        private void BuildSuperCrystalAtlas()
        {
            try
            {
                // 旋转 90° 后碎片宽度 = 原图高度（最大 102px），槽位必须足够宽，
                // 否则 SetPixels32 越界抛异常导致整张图集构建失败（水晶完全不显示）
                const int slotW = 128;
                const int cols = 3; // 0=正常 1=顺时针90° 2=逆时针90°
                int totalH = 0;
                var sizes = new int[8][];
                for (int i = 0; i < 8; i++)
                {
                    string n = "superdash_crystal" + i.ToString("D4");
                    if (_textures.TryGetValue(n, out Texture2D t))
                    {
                        sizes[i] = new[] { t.width, t.height };
                        totalH += t.height + 2;
                    }
                }
                if (totalH <= 0)
                {
                    return;
                }
                var atlas = new Texture2D(slotW * cols, totalH, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _sdCrystalUvLeft = new float[cols][];
                _sdCrystalUvTop = new float[cols][];
                _sdCrystalUvW = new float[cols][];
                _sdCrystalUvH = new float[cols][];
                _sdCrystalDrawW = new float[cols][];
                _sdCrystalDrawH = new float[cols][];
                for (int c = 0; c < cols; c++)
                {
                    _sdCrystalUvLeft[c] = new float[8];
                    _sdCrystalUvTop[c] = new float[8];
                    _sdCrystalUvW[c] = new float[8];
                    _sdCrystalUvH[c] = new float[8];
                    _sdCrystalDrawW[c] = new float[8];
                    _sdCrystalDrawH[c] = new float[8];
                }
                int y = 0;
                for (int i = 0; i < 8; i++)
                {
                    string n = "superdash_crystal" + i.ToString("D4");
                    if (sizes[i] == null || !_textures.TryGetValue(n, out Texture2D t))
                    {
                        continue;
                    }
                    int w = t.width;
                    int h = t.height;
                    Color32[] src = t.GetPixels32();
                    // 列0：正常朝向
                    int px = (slotW - w) / 2;
                    atlas.SetPixels32(px, y, w, h, src);
                    _sdCrystalUvLeft[0][i] = (float)px / (slotW * cols);
                    _sdCrystalUvTop[0][i] = (float)y / totalH;
                    _sdCrystalUvW[0][i] = (float)w / (slotW * cols);
                    _sdCrystalUvH[0][i] = (float)h / totalH;
                    _sdCrystalDrawW[0][i] = w;
                    _sdCrystalDrawH[0][i] = h;
                    // 列1：顺时针旋转90°（左墙用），尺寸变为 h×w
                    var cw = new Color32[h * w];
                    for (int yy = 0; yy < w; yy++)
                    {
                        for (int xx = 0; xx < h; xx++)
                        {
                            // 顺时针 90°：目标(xx,yy) ← 源(sx=yy, sy=h-1-xx)
                            cw[yy * h + xx] = src[(h - 1 - xx) * w + yy];
                        }
                    }
                    int pxCw = (slotW - h) / 2;
                    atlas.SetPixels32(slotW + pxCw, y, h, w, cw);
                    _sdCrystalUvLeft[1][i] = (float)(slotW + pxCw) / (slotW * cols);
                    _sdCrystalUvTop[1][i] = (float)y / totalH;
                    _sdCrystalUvW[1][i] = (float)h / (slotW * cols);
                    _sdCrystalUvH[1][i] = (float)w / totalH;
                    _sdCrystalDrawW[1][i] = h;
                    _sdCrystalDrawH[1][i] = w;
                    // 列2：逆时针旋转90°（右墙用），尺寸变为 h×w
                    var ccw = new Color32[h * w];
                    for (int yy = 0; yy < w; yy++)
                    {
                        for (int xx = 0; xx < h; xx++)
                        {
                            // 逆时针 90°：目标(xx,yy) ← 源(sx=w-1-yy, sy=xx)
                            ccw[yy * h + xx] = src[xx * w + (w - 1 - yy)];
                        }
                    }
                    int pxCcw = (slotW - h) / 2;
                    atlas.SetPixels32(slotW * 2 + pxCcw, y, h, w, ccw);
                    _sdCrystalUvLeft[2][i] = (float)(slotW * 2 + pxCcw) / (slotW * cols);
                    _sdCrystalUvTop[2][i] = (float)y / totalH;
                    _sdCrystalUvW[2][i] = (float)h / (slotW * cols);
                    _sdCrystalUvH[2][i] = (float)w / totalH;
                    _sdCrystalDrawW[2][i] = h;
                    _sdCrystalDrawH[2][i] = w;
                    y += h + 2;
                }
                atlas.Apply();
                _sdCrystalAtlasTex = atlas;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>水晶之心：冲刺拖尾特效帧，绘制在骑士身后（骑士身后层）。</summary>
        private bool KnightPrepareSuperTrailMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _sdTrailMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _sdTrailMesh.clearSimple();
            if (_sdTrailSprite == null ||
                !_textures.TryGetValue(_sdTrailSprite, out Texture2D tex))
            {
                MdOut = _sdTrailMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = tex.width * scale * 0.6f;
            float h = tex.height * scale * 0.6f;
            // 拖尾：中心在身后 1 格基础上整体向右移 1 格，Y 向上 0.5 格
            // 贴图主体在贴图左侧 1/3，配合镜像后始终落在骑士身后一侧
            float offX = (1f - _superDashDir) * 1.0f * _mp.CLEN;
            // 向左冲刺时拖尾整体再向右移 1 格（累计：现基础上 +0.5 格）
            if (_superDashDir < 0f)
            {
                offX += 1.0f * _mp.CLEN;
            }
            float offY = 0.5f * _mp.CLEN;
            _sdTrailMesh.Col = MTRX.ColWhite;
            _sdTrailMesh.initForImgAndTexture(tex);
            _sdTrailMesh.uv_top = 0f;
            _sdTrailMesh.uv_height = 1f;
            // 右冲镜像（主体靠左=身后），左冲不镜像（主体靠右=身后）
            if (_superDashDir > 0f)
            {
                _sdTrailMesh.uv_left = 1f;
                _sdTrailMesh.uv_width = -1f;
            }
            else
            {
                _sdTrailMesh.uv_left = 0f;
                _sdTrailMesh.uv_width = 1f;
            }
            _sdTrailMesh.Rect(offX - w * 0.5f, offY - h * 0.5f, w, h, false);
            MdOut = _sdTrailMesh;
            return true;
        }

        /// <summary>水晶之心：一次性特效（发射爆发 / 撞击碎裂），绘制在骑士位置（身前层）。</summary>
        private bool KnightPrepareSuperFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _sdFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _sdFxMesh.clearSimple();
            if (_sdFxSprite == null ||
                !_textures.TryGetValue(_sdFxSprite, out Texture2D tex))
            {
                MdOut = _sdFxMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = tex.width * scale * 0.75f;
            float h = tex.height * scale * 0.75f;
            // 发射爆发：右冲保持原位（居中）；左冲时把坐标对称到右侧
            float offY = 0f;
            float offX = _superDashDir < 0f ? w * 0.5f : 0f;
            _sdFxMesh.Col = MTRX.ColWhite;
            _sdFxMesh.initForImgAndTexture(tex);
            _sdFxMesh.uv_top = 0f;
            _sdFxMesh.uv_height = 1f;
            if (_superDashDir > 0f)
            {
                _sdFxMesh.uv_left = 1f;
                _sdFxMesh.uv_width = -1f;
            }
            else
            {
                _sdFxMesh.uv_left = 0f;
                _sdFxMesh.uv_width = 1f;
            }
            _sdFxMesh.Rect(offX - w * 0.5f, offY - h * 0.5f, w, h, false);
            MdOut = _sdFxMesh;
            return true;
        }

        /// <summary>
        /// 紫色收缩光圈：蓄力完成时一圈圈从外围收到自身中心，绘制在骑士身前层。
        /// 半径随时间从 StartR 收缩到 EndR（格），透明度线性淡出。
        /// </summary>
        private bool KnightPrepareSuperRingMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _sdRingMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _sdRingMesh.clearSimple();
            if (_sdRings.Count == 0)
            {
                MdOut = _sdRingMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float clen = _mp.CLEN;
            const float thick = 4.5f;
            for (int i = 0; i < _sdRings.Count; i++)
            {
                SdRing ring = _sdRings[i];
                float t = Mathf.Clamp01(ring.Age / ring.Life);
                float rad = Mathf.Lerp(ring.StartR, ring.EndR, t) * clen;
                float alpha = Mathf.Clamp01(1f - t) * 0.85f;
                if (alpha < 0.04f)
                {
                    continue;
                }
                _sdRingMesh.Col = new Color(0.68f, 0.25f, 1f, alpha);
                // 整体向下平移 20px（mesh y 向上为正，屏幕下方为负）
                _sdRingMesh.Circle(0f, -20f, rad, thick, false);
            }
            MdOut = _sdRingMesh;
            return true;
        }

        /// <summary>
        /// 暗影之魂冲击波：绘制在投射物当前位置，按发射方向左右镜像，循环播放火球帧。
        /// </summary>
        private bool KnightPrepareFireballMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _fireballMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _fireballMesh.clearSimple();
            if (_fireballs.Count == 0)
            {
                MdOut = _fireballMesh;
                return true;
            }
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            // 矩阵只锚定骑士原点一次，每发冲击波用相对骑士的偏移绘制
            // （否则共用票证矩阵会被最后一发覆盖，全部堆叠在同一位置）
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            // 渲染全部存活的冲击波（每个都锚定自身碰撞箱中心）
            for (int fi = 0; fi < _fireballs.Count; fi++)
            {
                FireballProj proj = _fireballs[fi];
                if (proj.Sprite == null ||
                    !_textures.TryGetValue(proj.Sprite, out Texture2D tex))
                {
                    continue;
                }
                // 渲染位置 = 碰撞箱中心 + 渲染偏移（世界格），与白框一致
                float renderX = proj.X + FireballRenderOffX;
                float renderY = proj.Y + FireballRenderOffY;
                float dxm = (renderX - X) * _mp.CLEN;
                float dym = -(renderY - Y) * _mp.CLEN;
                float w = tex.width * scale + FireballRenderPadX * _mp.CLEN;
                float h = tex.height * scale + FireballRenderPadY * _mp.CLEN;
                _fireballMesh.Col = MTRX.ColWhite;
                _fireballMesh.initForImgAndTexture(tex);
                _fireballMesh.uv_top = 0f;
                _fireballMesh.uv_height = 1f;
                // 向右发射镜像（脸朝右），向左发射不镜像（脸朝左）
                if (proj.Dir > 0f)
                {
                    _fireballMesh.uv_left = 1f;
                    _fireballMesh.uv_width = -1f;
                }
                else
                {
                    _fireballMesh.uv_left = 0f;
                    _fireballMesh.uv_width = 1f;
                }
                // 渲染完全锚定碰撞箱中心：贴图以 (proj.X, proj.Y) 为绝对中心绘制
                _fireballMesh.Rect(dxm - w * 0.5f, dym - h * 0.5f, w, h, false);
            }
            MdOut = _fireballMesh;
            return true;
        }

        /// <summary>
        /// 吸虫之巢：黑色吸虫渲染（骑士身前层 PR1）。
        /// 空中播 FlukeAir（6 帧循环），落地瞬间播 FlukeFlop（12 帧一次），按发射方向镜像。
        /// </summary>
        private bool KnightPrepareFlukeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            Map2d mpFl = FlukeMp; // 切回诺艾尔后仍要画完正在飞的吸虫
            if (mpFl == null || _flukeMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _flukeMesh.clearSimple();
            if (_flukes.Count == 0)
            {
                MdOut = _flukeMesh;
                return true;
            }
            // 矩阵只锚定骑士原点一次，每只吸虫用相对骑士的偏移绘制
            // （否则共用票证矩阵会被最后一只覆盖，全部堆叠在同一位置）
            float mx = mpFl.pixel2ux(X * mpFl.CLEN);
            float my = mpFl.pixel2uy(Y * mpFl.CLEN);
            Tk.Matrix = mpFl.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _clips.TryGetValue("FlukeAir", out ClipData airClip);
            _clips.TryGetValue("FlukeFlop", out ClipData flopClip);
            for (int i = 0; i < _flukes.Count; i++)
            {
                FlukeProj fl = _flukes[i];
                string sprite = null;
                if (fl.Flopping && flopClip != null && flopClip.frames.Length > 0)
                {
                    int idx = Mathf.Clamp((int)(fl.AnimTime * flopClip.fps), 0, flopClip.frames.Length - 1);
                    sprite = flopClip.frames[idx];
                }
                else if (airClip != null && airClip.frames.Length > 0)
                {
                    int idx = (int)(fl.AnimTime * airClip.fps) % airClip.frames.Length;
                    sprite = airClip.frames[idx];
                }
                if (sprite == null || !_textures.TryGetValue(sprite, out Texture2D tex))
                {
                    continue;
                }
                float dxm = (fl.X - X) * mpFl.CLEN;
                float dym = -(fl.Y - Y) * mpFl.CLEN;
                float w = tex.width * FlukeScale;
                float h = tex.height * FlukeScale;
                _flukeMesh.Col = MTRX.ColWhite;
                _flukeMesh.initForImgAndTexture(tex);
                _flukeMesh.uv_top = 0f;
                _flukeMesh.uv_height = 1f;
                // 按发射方向镜像（与火球一致：向右发射镜像）
                if (fl.Dir > 0f)
                {
                    _flukeMesh.uv_left = 1f;
                    _flukeMesh.uv_width = -1f;
                }
                else
                {
                    _flukeMesh.uv_left = 0f;
                    _flukeMesh.uv_width = 1f;
                }
                _flukeMesh.Rect(dxm - w * 0.5f, dym - h * 0.5f, w, h, false);
            }
            MdOut = _flukeMesh;
            return true;
        }

        /// <summary>
        /// 发光子宫：幼体渲染（骑士身前层 PR1）。
        /// 出生播 Hatch、飞行播 Fly（有目标时播 Attack 冲锋）、碰撞后播 Burst 爆炸。
        /// </summary>
        private bool KnightPrepareUterusMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _uterusMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _uterusMesh.clearSimple();
            // 幼体可能全部爆炸消失，但爆炸特效仍在播放，必须继续绘制
            if (!CharmEffects.IsEquipped(CharmEffects.UterusId) ||
                (_uterusHatchlings.Count == 0 && _uterusExplosions.Count == 0))
            {
                MdOut = _uterusMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _clips.TryGetValue("UterusHatch", out ClipData hatchClip);
            _clips.TryGetValue("UterusFly", out ClipData flyClip);
            _clips.TryGetValue("UterusFlyL", out ClipData flyLClip);
            _clips.TryGetValue("UterusAttack", out ClipData attackClip);
            _clips.TryGetValue("UterusBurst", out ClipData burstClip);
            _clips.TryGetValue("UterusRest", out ClipData restClip);
            for (int i = 0; i < _uterusHatchlings.Count; i++)
            {
                UterusHatchling h = _uterusHatchlings[i];
                string sprite = null;
                if (h.Phase == 0)
                {
                    if (hatchClip != null && hatchClip.frames.Length > 0)
                    {
                        int idx = Mathf.Clamp((int)(h.AnimTime * hatchClip.fps), 0, hatchClip.frames.Length - 1);
                        sprite = hatchClip.frames[idx];
                    }
                }
                else if (h.Phase == 1)
                {
                    if (h.Target != null && attackClip != null && attackClip.frames.Length > 0)
                    {
                        // 索敌冲锋
                        int idx = (int)(h.AnimTime * attackClip.fps) % attackClip.frames.Length;
                        sprite = attackClip.frames[idx];
                    }
                    else
                    {
                        bool knightMoving = Mathf.Abs(Vx) > 0.05f || !Grounded;
                        if (knightMoving)
                        {
                            // 行动中按骑士朝向：面朝左（_faceDir>0）播普通帧，面朝右（_faceDir<0）播镜像帧
                            ClipData clip = (_faceDir < 0f && flyLClip != null) ? flyLClip : flyClip;
                            if (clip != null && clip.frames.Length > 0)
                            {
                                int idx = (int)(h.AnimTime * clip.fps) % clip.frames.Length;
                                sprite = clip.frames[idx];
                            }
                        }
                        else if (flyClip != null && flyLClip != null &&
                                 flyClip.frames.Length > 0 && flyLClip.frames.Length > 0)
                        {
                            // 站立且无敌人：普通一套（0.5s）播完再播镜像一套，交替循环
                            float setDur = (float)flyClip.frames.Length / flyClip.fps;
                            float t = h.AnimTime % (setDur * 2f);
                            bool alt = t >= setDur;
                            ClipData active = alt ? flyLClip : flyClip;
                            float tt = alt ? t - setDur : t;
                            int idx = Mathf.Clamp((int)(tt * active.fps), 0, active.frames.Length - 1);
                            sprite = active.frames[idx];
                        }
                        else if (flyClip != null && flyClip.frames.Length > 0)
                        {
                            int idx = (int)(h.AnimTime * flyClip.fps) % flyClip.frames.Length;
                            sprite = flyClip.frames[idx];
                        }
                    }
                }
                else if (h.Phase == 3)
                {
                    if (h.SleepStage == 0)
                    {
                        // 下落中：仍播飞行动画
                        if (flyClip != null && flyClip.frames.Length > 0)
                        {
                            int idx = (int)(h.AnimTime * flyClip.fps) % flyClip.frames.Length;
                            sprite = flyClip.frames[idx];
                        }
                    }
                    else if (restClip != null && restClip.frames.Length > 0)
                    {
                        // 落地后：hatchling_sleep0000~0005 播一遍后停最后一帧（静止）
                        int idx = Mathf.Clamp((int)(h.AnimTime * restClip.fps), 0, restClip.frames.Length - 1);
                        sprite = restClip.frames[idx];
                    }
                }
                else
                {
                    if (burstClip != null && burstClip.frames.Length > 0)
                    {
                        int idx = Mathf.Clamp((int)(h.AnimTime * burstClip.fps), 0, burstClip.frames.Length - 1);
                        sprite = burstClip.frames[idx];
                    }
                }
                if (sprite == null || !_textures.TryGetValue(sprite, out Texture2D tex))
                {
                    continue;
                }
                float dxm = (h.X - X) * _mp.CLEN;
                float dym = -(h.Y - Y) * _mp.CLEN;
                float w = tex.width * UterusScale;
                float ht = tex.height * UterusScale;
                _uterusMesh.Col = MTRX.ColWhite;
                _uterusMesh.initForImgAndTexture(tex);
                _uterusMesh.uv_top = 0f;
                _uterusMesh.uv_height = 1f;
                _uterusMesh.uv_left = 0f;
                _uterusMesh.uv_width = 1f;
                _uterusMesh.Rect(dxm - w * 0.5f, dym - ht * 0.5f, w, ht, false);
            }
            // 碰撞爆炸特效（explode_particle，渲染 6x6 格；判定/渲染中心右移 2 格，渲染再上移 1.5 格）
            if (_uterusExplosions.Count > 0 &&
                _clips.TryGetValue("UterusExplosion", out ClipData explClip) &&
                explClip.frames.Length > 0)
            {
                float exW = UterusExplosionSize * _mp.CLEN;  // 6 格宽（可见内容约 6x6 格）
                float exH = exW * (100f / 80f);     // 保持 80:100 帧比例
                for (int i = 0; i < _uterusExplosions.Count; i++)
                {
                    UterusExplosion ex = _uterusExplosions[i];
                    int idx = Mathf.Clamp((int)(ex.AnimTime * explClip.fps), 0, explClip.frames.Length - 1);
                    string spriteName = explClip.frames[idx];
                    if (spriteName == null || !_textures.TryGetValue(spriteName, out Texture2D tex))
                    {
                        continue;
                    }
                    // 渲染位置：相对碰撞点右移 4 格（2+2）、上移 3.5 格（Y 向下为正，上移即 Y-3.5）
                    float exDxm = (ex.X + UterusExplosionOffX + UterusExplosionRenderOffX - X) * _mp.CLEN;
                    float exDym = -((ex.Y + UterusExplosionRenderOffY) - Y) * _mp.CLEN;
                    _uterusMesh.Col = MTRX.ColWhite;
                    _uterusMesh.initForImgAndTexture(tex);
                    _uterusMesh.uv_top = 0f;
                    _uterusMesh.uv_height = 1f;
                    _uterusMesh.uv_left = 0f;
                    _uterusMesh.uv_width = 1f;
                    _uterusMesh.Rect(exDxm - exW * 0.5f, exDym - exH * 0.5f, exW, exH, false);
                }
            }
            MdOut = _uterusMesh;
            return true;
        }

        /// <summary>
        /// 发光子宫爆炸判定框调试：绿色线框标出幼体碰撞爆炸的 6x6 格 AoE 判定区
        /// （中心相对碰撞点右移 2 格，与 UterusHitEnemy 里的 OverlapBoxAll 完全一致）。
        /// 换算链同 strategy.cs：世界点 → InverseTransformPoint → 减锚点 → ×64。
        /// </summary>
        private bool KnightPrepareUterusExplosionDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw,
            int draw_id, out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _uterusExplosionDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _uterusExplosionDbgMesh.clearSimple();
            if (!UterusExplosionHitboxDebug || _uterusExplosions.Count == 0)
            {
                MdOut = _uterusExplosionDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _uterusExplosionDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);

            Transform mapT = _mp.gameObject.transform;
            Vector3 ToAnchor(float wx, float wy)
            {
                Vector3 lp = mapT.InverseTransformPoint(new Vector3(wx, wy, 0f));
                return new Vector3((lp.x - mx) * UxToMeshPx, (lp.y - my) * UxToMeshPx, 0f);
            }

            float half = UterusExplosionSize * 0.5f;
            for (int i = 0; i < _uterusExplosions.Count; i++)
            {
                UterusExplosion ex = _uterusExplosions[i];
                Vector2 wc = mapT.TransformPoint(new Vector2(
                    _mp.pixel2ux((ex.X + UterusExplosionOffX) * _mp.CLEN),
                    _mp.pixel2uy(ex.Y * _mp.CLEN)));
                float x0 = wc.x - half;
                float x1 = wc.x + half;
                float y0 = wc.y - half;
                float y1 = wc.y + half;
                Vector3 c0 = ToAnchor(x0, y0);
                Vector3 c1 = ToAnchor(x1, y0);
                Vector3 c2 = ToAnchor(x1, y1);
                Vector3 c3 = ToAnchor(x0, y1);
                _uterusExplosionDbgMesh.Line(c0.x, c0.y, c1.x, c1.y, 2f);
                _uterusExplosionDbgMesh.Line(c1.x, c1.y, c2.x, c2.y, 2f);
                _uterusExplosionDbgMesh.Line(c2.x, c2.y, c3.x, c3.y, 2f);
                _uterusExplosionDbgMesh.Line(c3.x, c3.y, c0.x, c0.y, 2f);
            }
            MdOut = _uterusExplosionDbgMesh;
            return true;
        }

        /// <summary>护符32 蘑菇孢子：孢子粒子身前层（PR1，20% 粒子）。</summary>
        private bool KnightPrepareSporeCloudMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareSporeParticleLayer(_sporeCloudMesh, 1, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>护符32 蘑菇孢子：孢子粒子身后层（PR0，80% 粒子）。</summary>
        private bool KnightPrepareSporeCloudBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareSporeParticleLayer(_sporeCloudBackMesh, 0, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>
        /// 护符32 蘑菇孢子：孢子粒子渲染公共实现。
        /// 浅绿+黄色圆形粒子（各 120 个），0.7s 内从中心扩散（初速 1~12 格/秒减速到 0），
        /// 之后极小的随机飘动；4.1s 后 0.3s 淡出。按 Layer 分到骑士身后(PR0)/身前(PR1)。
        /// </summary>
        private bool PrepareSporeParticleLayer(MeshDrawer mesh, int layer, Camera Cam, M2RenderTicket Tk,
            int draw_id, out MeshDrawer MdOut)
        {
            MdOut = null;
            if (_mp == null || mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            mesh.clearSimple();
            if (_sporeClouds.Count == 0 || !CharmEffects.IsEquipped(CharmEffects.MushroomId) ||
                _dotTexes[1] == null)
            {
                MdOut = mesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            mesh.initForImgAndTexture(_dotTexes[1]);
            mesh.uv_top = 0f;
            mesh.uv_height = 1f;
            mesh.uv_left = 0f;
            mesh.uv_width = 1f;
            for (int i = 0; i < _sporeClouds.Count; i++)
            {
                SporeCloud c = _sporeClouds[i];
                // 淡出：4.1s 后 0.3s 内 alpha 1→0
                float alpha = c.Age <= SporeLife
                    ? 1f
                    : Mathf.Clamp01((SporeTotalLife - c.Age) / SporeFadeTime);
                if (alpha <= 0f)
                {
                    continue;
                }
                for (int j = 0; j < c.Particles.Count; j++)
                {
                    SporeParticle p = c.Particles[j];
                    if (p.Layer != layer)
                    {
                        continue;
                    }
                    // 扩张阶段：初速 1~10 减速到 0（0.7s），位移 = v0t - 0.5(v0/0.7) t²
                    float t2 = Mathf.Min(c.Age, SporeExpandTime);
                    float accel = p.Speed / SporeExpandTime;
                    float dist = p.Speed * t2 - 0.5f * accel * t2 * t2;
                    if (c.Age >= SporeExpandTime)
                    {
                        dist = 0.5f * p.Speed * SporeExpandTime;
                    }
                    float px = c.X + p.DirX * dist + p.DriftX;
                    float py = c.Y + p.DirY * dist + p.DriftY;
                    float dxm = (px - X) * _mp.CLEN;
                    float dym = -(py - Y) * _mp.CLEN;
                    mesh.Col = p.Color == 0
                        ? new Color(0.55f, 1f, 0.55f, alpha)   // 浅绿
                        : new Color(1f, 0.92f, 0.3f, alpha);    // 黄
                    float dotSize = p.Radius * 2f * _mp.CLEN;
                    mesh.Rect(dxm - dotSize * 0.5f, dym - dotSize * 0.5f, dotSize, dotSize, false);
                }
            }
            MdOut = mesh;
            return true;
        }

        /// <summary>
        /// 护符32 蘑菇孢子：孢子云碰撞箱调试——绿色圆圈标出 3.5 格半径领域。
        /// 换算链同 strategy.cs：世界点 → InverseTransformPoint → 减锚点 → ×64。
        /// </summary>
        private bool KnightPrepareSporeCloudDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _sporeCloudDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _sporeCloudDbgMesh.clearSimple();
            if (!SporeCloudHitboxDebug || _sporeClouds.Count == 0)
            {
                MdOut = _sporeCloudDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _sporeCloudDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);

            Transform mapT = _mp.gameObject.transform;
            Vector3 ToAnchor(float wx, float wy)
            {
                Vector3 lp = mapT.InverseTransformPoint(new Vector3(wx, wy, 0f));
                return new Vector3((lp.x - mx) * UxToMeshPx, (lp.y - my) * UxToMeshPx, 0f);
            }

            const int seg = 24;
            float dbgRadius = SporeRadiusNow();
            for (int i = 0; i < _sporeClouds.Count; i++)
            {
                SporeCloud c = _sporeClouds[i];
                Vector2 wc = mapT.TransformPoint(new Vector2(
                    _mp.pixel2ux(c.X * _mp.CLEN),
                    _mp.pixel2uy(c.Y * _mp.CLEN)));
                Vector3 prev = ToAnchor(
                    wc.x + dbgRadius * Mathf.Cos(0f),
                    wc.y + dbgRadius * Mathf.Sin(0f));
                for (int s = 1; s <= seg; s++)
                {
                    float a = (float)s / seg * Mathf.PI * 2f;
                    Vector3 cur = ToAnchor(
                        wc.x + dbgRadius * Mathf.Cos(a),
                        wc.y + dbgRadius * Mathf.Sin(a));
                    _sporeCloudDbgMesh.Line(prev.x, prev.y, cur.x, cur.y, 2f);
                    prev = cur;
                }
            }
            MdOut = _sporeCloudDbgMesh;
            return true;
        }

        /// <summary>
        /// 护符36 编织者之歌：小编织者渲染（骑士身前层 PR1）。
        /// 出生播 Launch、攻击播 Attack、睡眠播 Sleep、移动播 Run、静止播 Idle；
        /// 蛛丝画为白色短线段。
        /// </summary>
        private bool KnightPrepareWeaverlingMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _weaverMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _weaverMesh.clearSimple();
            if (_weaverlings.Count == 0 && _weaverThreads.Count == 0)
            {
                MdOut = _weaverMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            // 蛛丝：白色短线段（沿飞行方向）
            if (_weaverThreads.Count > 0)
            {
                _weaverMesh.Col = MTRX.ColWhite;
                for (int i = 0; i < _weaverThreads.Count; i++)
                {
                    WeaverThread t = _weaverThreads[i];
                    float x0 = (t.X - X) * _mp.CLEN;
                    float y0 = -(t.Y - Y) * _mp.CLEN;
                    float x1 = (t.X + t.DirX * 0.55f - X) * _mp.CLEN;
                    float y1 = -(t.Y + t.DirY * 0.55f - Y) * _mp.CLEN;
                    _weaverMesh.Line(x0, y0, x1, y1, 2f);
                }
            }
            // 小编织者
            _clips.TryGetValue("WeaverLaunch", out ClipData launchClip);
            _clips.TryGetValue("WeaverRun", out ClipData runClip);
            _clips.TryGetValue("WeaverIdle", out ClipData idleClip);
            _clips.TryGetValue("WeaverAttack", out ClipData attackClip);
            _clips.TryGetValue("WeaverSleep", out ClipData sleepClip);
            _clips.TryGetValue("WeaverWake", out ClipData wakeClip);
            _clips.TryGetValue("WeaverMelee", out ClipData meleeClip);
            for (int i = 0; i < _weaverlings.Count; i++)
            {
                Weaverling w = _weaverlings[i];
                ClipData clip = null;
                if (w.State == 0)
                {
                    clip = launchClip;
                }
                else if (w.State == 2)
                {
                    clip = attackClip;
                }
                else if (w.State == 4)
                {
                    clip = meleeClip;
                }
                else if (w.State == 3)
                {
                    if (w.SleepStage == 1)
                    {
                        clip = sleepClip; // 睡眠 0000~0003
                    }
                    else if (w.SleepStage == 2)
                    {
                        // 保持睡眠末帧（0003）：取剪辑最后一帧
                        if (sleepClip != null && sleepClip.frames.Length > 0)
                        {
                            string last = sleepClip.frames[sleepClip.frames.Length - 1];
                            if (_textures.TryGetValue(last, out Texture2D ltex))
                            {
                                float sdx = (w.X - X) * _mp.CLEN;
                                float sdy = -(w.Y - Y) * _mp.CLEN + 0.4f * _mp.CLEN;
                                float sww = ltex.width * WeaverScale;
                                float shh = ltex.height * WeaverScale;
                                _weaverMesh.Col = MTRX.ColWhite;
                                _weaverMesh.initForImgAndTexture(ltex);
                                _weaverMesh.uv_top = 0f;
                                _weaverMesh.uv_height = 1f;
                                _weaverMesh.uv_left = 0f;
                                _weaverMesh.uv_width = 1f;
                                if (w.Face < 0f)
                                {
                                    _weaverMesh.uv_left = 1f;
                                    _weaverMesh.uv_width = -1f;
                                }
                                _weaverMesh.Rect(sdx - sww * 0.5f, sdy - shh * 0.5f, sww, shh, false);
                            }
                        }
                        continue;
                    }
                    else
                    {
                        clip = wakeClip; // 苏醒反向 0003→0001
                    }
                }
                else
                {
                    // 近战动画优先于跑/待机
                    clip = w.MeleeAnimT > 0f
                        ? meleeClip
                        : (Mathf.Abs(w.Vx) > 0.05f ? runClip : idleClip);
                }
                if (clip == null || clip.frames.Length == 0)
                {
                    continue;
                }
                int idx = (int)(w.AnimTime * clip.fps) % clip.frames.Length;
                string sprite = clip.frames[idx];
                if (sprite == null || !_textures.TryGetValue(sprite, out Texture2D tex))
                {
                    continue;
                }
                float dxm = (w.X - X) * _mp.CLEN;
                // 渲染位置抬高 0.4 格：游戏 Y 向下为正、mesh y 向上为正，
                // 抬高 = dym 加正偏移（在之前错误方向的基础上修正 -0.8 格）
                float dym = -(w.Y - Y) * _mp.CLEN + 0.4f * _mp.CLEN;
                float ww = tex.width * WeaverScale;
                float hh = tex.height * WeaverScale;
                _weaverMesh.Col = MTRX.ColWhite;
                _weaverMesh.initForImgAndTexture(tex);
                _weaverMesh.uv_top = 0f;
                _weaverMesh.uv_height = 1f;
                _weaverMesh.uv_left = 0f;
                _weaverMesh.uv_width = 1f;
                if (w.Face < 0f)
                {
                    _weaverMesh.uv_left = 1f;
                    _weaverMesh.uv_width = -1f;
                }
                _weaverMesh.Rect(dxm - ww * 0.5f, dym - hh * 0.5f, ww, hh, false);
            }
            MdOut = _weaverMesh;
            return true;
        }

        /// <summary>
        /// 防御者纹章法阵·实心圆（骑士身后层 PR0）：
        /// 半径 3 格，深蓝填充，中心透明度 100% → 边缘 50%。
        /// </summary>
        private bool KnightPrepareShelterCircleMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _shelterCircleMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _shelterCircleMesh.clearSimple();
            if (!CharmEffects.IsEquipped(CharmEffects.ShelterId) || _shelterCircleTex == null)
            {
                MdOut = _shelterCircleMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float size = ShelterCircleRadius * 2f * _mp.CLEN;
            _shelterCircleMesh.Col = new Color(0.1f, 0.18f, 0.75f, 1f); // 深蓝（浓度由贴图 alpha 控制）
            _shelterCircleMesh.initForImgAndTexture(_shelterCircleTex);
            _shelterCircleMesh.uv_top = 0f;
            _shelterCircleMesh.uv_height = 1f;
            _shelterCircleMesh.uv_left = 0f;
            _shelterCircleMesh.uv_width = 1f;
            _shelterCircleMesh.Rect(0f, 0f, size, size, false);
            MdOut = _shelterCircleMesh;
            return true;
        }

        /// <summary>
        /// 防御者纹章法阵·动态层（骑士身前层 PR1）：
        /// 扩张圆环（浅蓝→白）、到达边缘时的发光、随机星/方/叉图案。
        /// </summary>
        private bool KnightPrepareShelterFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _shelterFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _shelterFxMesh.clearSimple();
            if (!CharmEffects.IsEquipped(CharmEffects.ShelterId))
            {
                MdOut = _shelterFxMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float c = _mp.CLEN;
            // 扩张圆环：纯白 → 随半径增大逐渐变浅蓝，到达边缘时浅蓝
            if (_shelterRingRadius >= 0f)
            {
                float t = Mathf.Clamp01(_shelterRingRadius / ShelterCircleRadius);
                float alpha = 1f;
                // 到达边缘后：圆环与图案一起保持 0.2s → 0.3s 淡出
                if (_shelterRingRadius >= ShelterCircleRadius && _shelterPatternTimer >= 0f)
                {
                    alpha = _shelterPatternTimer < ShelterPatternHoldTime
                        ? 1f
                        : 1f - (_shelterPatternTimer - ShelterPatternHoldTime) / ShelterPatternFadeTime;
                }
                Color ringCol = Color.Lerp(Color.white, new Color(0.35f, 0.62f, 1f, 1f), t);
                ringCol.a = Mathf.Clamp01(alpha);
                _shelterFxMesh.Col = ringCol;
                _shelterFxMesh.Circle(0f, 0f, _shelterRingRadius * c, 3.5f, false);
            }
            // 到达边缘的发光：实心圆外围亮一下（白色，快速淡出）
            if (_shelterFlashTimer > 0f)
            {
                float fa = Mathf.Clamp01(_shelterFlashTimer / ShelterFlashTime);
                _shelterFxMesh.Col = new Color(1f, 1f, 1f, fa * 0.95f);
                _shelterFxMesh.Circle(0f, 0f, ShelterCircleRadius * c, 5f, false);
            }
            // 随机图案：五角星 / 正方形 / 叉号（内接于圆），浅蓝，淡入0.3-显示0.1-淡出0.3
            if (_shelterPatternTimer >= 0f)
            {
                // 图案到达边缘瞬间生成（满亮度、无淡入，纯白），保持 0.2s 后随圆环一起淡出
                float pa = _shelterPatternTimer < ShelterPatternHoldTime
                    ? 1f
                    : 1f - (_shelterPatternTimer - ShelterPatternHoldTime) / ShelterPatternFadeTime;
                _shelterFxMesh.Col = new Color(1f, 1f, 1f, Mathf.Clamp01(pa));
                DrawShelterPattern(_shelterFxMesh, _shelterPatternType, ShelterCircleRadius * c);
            }
            MdOut = _shelterFxMesh;
            return true;
        }

        /// <summary>
        /// 防御者纹章法阵·围绕球体（骑士身前层 PR1）：
        /// 五个深蓝球体围绕骑士公转，半径 0.5 格、球心距 1.75 格、72° 均布、周期 2.5s；
        /// 球体透明度中心 100% → 边缘 0%。
        /// </summary>
        private bool KnightPrepareShelterSphereMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _shelterSphereMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _shelterSphereMesh.clearSimple();
            if (!CharmEffects.IsEquipped(CharmEffects.ShelterId) || _shelterSphereTex == null)
            {
                MdOut = _shelterSphereMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float c = _mp.CLEN;
            float size = ShelterSphereRadius * 2f * c;
            _shelterSphereMesh.Col = MTRX.ColWhite; // 颜色渐变已做进贴图（中心白→边缘深蓝）
            _shelterSphereMesh.initForImgAndTexture(_shelterSphereTex);
            _shelterSphereMesh.uv_top = 0f;
            _shelterSphereMesh.uv_height = 1f;
            _shelterSphereMesh.uv_left = 0f;
            _shelterSphereMesh.uv_width = 1f;
            for (int i = 0; i < ShelterSphereCount; i++)
            {
                float ang = _shelterSphereAngle + i * (6.2831853f / ShelterSphereCount); // 72°
                float sx = Mathf.Cos(ang) * ShelterSphereOrbit * c;
                float sy = -Mathf.Sin(ang) * ShelterSphereOrbit * c; // 网格 y 向上为正
                _shelterSphereMesh.Rect(sx, sy, size, size, false);
            }
            MdOut = _shelterSphereMesh;
            return true;
        }

        /// <summary>绘制法阵图案（星/方/叉），坐标以骑士锚点为中心、半径 rPx。</summary>
        private void DrawShelterPattern(MeshDrawer md, int type, float rPx)
        {
            const float thick = 3.5f;
            if (type == 0) // 五角星（外顶点在圆上，内顶点 0.382 比例）
            {
                float inner = rPx * 0.382f;
                for (int k = 0; k < 10; k++)
                {
                    // 起始角 +90°：五角星一个尖端朝上
                    float a0 = 1.5707964f + k * 0.62831854f;
                    float a1 = a0 + 0.62831854f;
                    float r0 = (k % 2 == 0) ? rPx : inner;
                    float r1 = ((k + 1) % 2 == 0) ? rPx : inner;
                    md.Line(Mathf.Cos(a0) * r0, Mathf.Sin(a0) * r0,
                            Mathf.Cos(a1) * r1, Mathf.Sin(a1) * r1, thick);
                }
            }
            else if (type == 1) // 正方形：内接圆（旋转 45°，四角贴圆）
            {
                for (int k = 0; k < 4; k++)
                {
                    // 四个角在上下左右（0°=右、90°=上、180°=左、270°=下）
                    float a0 = k * 1.5707964f;
                    float a1 = a0 + 1.5707964f;
                    md.Line(Mathf.Cos(a0) * rPx, Mathf.Sin(a0) * rPx,
                            Mathf.Cos(a1) * rPx, Mathf.Sin(a1) * rPx, thick);
                }
            }
            else // 叉号：两条对角线（45° 端点贴圆）
            {
                float d = rPx * 0.70710678f;
                md.Line(-d, -d, d, d, thick);
                md.Line(-d, d, d, -d, thick);
            }
        }

        /// <summary>读档/新游戏：把复活点更新为诺艾尔当前所在位置（当前存档的出生点/长椅）。</summary>
        public void SetRespawnToNoel(PRNoel noel)
        {
            if (noel == null || noel.Mp == null)
            {
                return;
            }
            _hasRespawn = true;
            _respawnX = noel.x;
            _respawnY = noel.mbottom - SizeY;
            _respawnMp = noel.Mp;
        }

        /// <summary>
        /// 蜕变挽歌剑气：渲染 slashes_effect0001 帧（羁绊“蜕变挽歌+亡者之怒”1血时用
        /// rage_slash_left0001），大小与普通剑气一致，锚定剑气自身位置，按飞行方向左右镜像。
        /// </summary>
        private bool KnightPrepareElegyBladeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _elegyBladeMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _elegyBladeMesh.clearSimple();
            // 羁绊：蜕变挽歌 + 亡者之怒 —— 狂怒状态（1 血）剑气用 rage_slash_left0001
            string elegySprite = FuryActive ? "rage_slash_left0001" : "slashes_effect0001";
            if (_elegyBlades.Count == 0 ||
                !_textures.TryGetValue(elegySprite, out Texture2D tex))
            {
                MdOut = _elegyBladeMesh;
                return true;
            }
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = tex.width * scale * SlashFxScale;
            float h = tex.height * scale * SlashFxScale;
            // 矩阵锚定小骑士原点，每个剑气以相对骑士的偏移绘制（否则多道剑气会共用
            // 同一个票证矩阵，新剑气生成后旧剑气全部被画到新位置，看起来像被删除）
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            for (int bi = 0; bi < _elegyBlades.Count; bi++)
            {
                ElegyBladeProj p = _elegyBlades[bi];
                // 渲染框：向上 0.5 格、向右 1 格
                float dx = (p.X + 1f - X) * _mp.CLEN;
                float dy = -(p.Y - 0.5f - Y) * _mp.CLEN; // 网格 y 向下为正，mesh y 向上为负
                _elegyBladeMesh.Col = MTRX.ColWhite;
                _elegyBladeMesh.initForImgAndTexture(tex);
                _elegyBladeMesh.uv_top = 0f;
                _elegyBladeMesh.uv_height = 1f;
                // 向右飞（Dir>0）镜像，向左飞不镜像（与原版剑气朝向一致）
                if (p.Dir > 0f)
                {
                    _elegyBladeMesh.uv_left = 1f;
                    _elegyBladeMesh.uv_width = -1f;
                }
                else
                {
                    _elegyBladeMesh.uv_left = 0f;
                    _elegyBladeMesh.uv_width = 1f;
                }
                _elegyBladeMesh.Rect(dx - w * 0.5f, dy - h * 0.5f, w, h, false);
            }
            MdOut = _elegyBladeMesh;
            return true;
        }

        /// <summary>
        /// 梦之盾渲染：锚定小骑士中心，按公转角度以像素偏移画盾牌贴图。
        /// 自转：贴图长边始终垂直于“碰撞箱圆心—小骑士中心”连线（加速/减速公转时自转同步正确）。
        /// 骑士身前层 PR1。
        /// </summary>
        private bool KnightPrepareDreamShieldMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _shieldMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _shieldMesh.clearSimple();
            // clearSimple 不会重置 MatrixTransform，先 Identity 保证每帧干净
            _shieldMesh.Identity();
            if (!CharmEffects.IsEquipped(CharmEffects.DreamShieldId) ||
                !_textures.TryGetValue("dreamshield", out Texture2D tex))
            {
                MdOut = _shieldMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float c = _mp.CLEN;
            float w = tex.width * ShieldRenderScale;
            float h = tex.height * ShieldRenderScale;
            _shieldMesh.Col = MTRX.ColWhite;
            _shieldMesh.initForImgAndTexture(tex);
            _shieldMesh.uv_top = 0f;
            _shieldMesh.uv_height = 1f;
            _shieldMesh.uv_left = 0f;
            _shieldMesh.uv_width = 1f;
            // 渲染中心（相对骑士的像素偏移，网格 Y 向上为正，取负）；渲染再左 1 格、下 0.5 格，碰撞箱不动
            float px = (ShieldRenderOffX + ShieldRenderShiftX + Mathf.Cos(_shieldAngle) * _shieldOrbitRadius) * c;
            float py = -(Mathf.Sin(_shieldAngle) * _shieldOrbitRadius + ShieldRenderShiftY) * c;
            // 碰撞箱圆心（相对骑士，格；游戏 Y 向下为正）
            float ccx = ShieldRenderOffX + Mathf.Cos(_shieldAngle) * _shieldOrbitRadius + ShieldCollisionOffX;
            float ccy = Mathf.Sin(_shieldAngle) * _shieldOrbitRadius + ShieldCollisionOffY;
            // 自转角度 A：“y 负半轴”（上）顺时针到“碰撞箱圆心→小骑士中心下方 0.5 格”连线的夹角（0~360°）
            // 连线终点 = 骑士中心 + (0, +0.5)（游戏 Y 向下），方向 = 终点 - 碰撞圆心 = (-ccx, 0.5 - ccy)
            // 游戏坐标 Y 向下，atan2+90° 即顺时针夹角
            float A = Mathf.Atan2(0.5f - ccy, -ccx) + Mathf.PI * 0.5f;
            // 贴图顺时针自转 A；网格空间 Y 向上，顺时针为负角
            float rotR = -A;
            // 像素 → 网格单位（ppu=64），先平移到盾牌中心再旋转，矩形绕中心绘制
            Matrix4x4 savedM = _shieldMesh.getCurrentMatrix();
            _shieldMesh.Translate(px * 0.015625f, py * 0.015625f, true);
            _shieldMesh.Rotate(rotR, true);
            // Rect 的 (x,y) 是中心：传 (0,0) 使矩形中心与旋转原点重合，自转才绕贴图自身中心
            _shieldMesh.Rect(0f, 0f, w, h, false);
            _shieldMesh.setCurrentMatrix(savedM, false);
            MdOut = _shieldMesh;
            return true;
        }

        /// <summary>
        /// 小格林渲染：锚定骑士中心，按状态选择动画帧（出现/待机/飞行/睡眠/苏醒/传送），
        /// 以自身坐标相对骑士的像素偏移绘制，骑士身前层 PR1。
        /// </summary>
        private bool KnightPrepareGrimmMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _grimmMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _grimmMesh.clearSimple();
            if (!CharmEffects.IsEquipped(CharmEffects.GrimmId) || _grimm == null)
            {
                MdOut = _grimmMesh;
                return true;
            }
            GrimmChild g = _grimm;
            string sprite = null;
            if (g.Phase == 0)
            {
                sprite = GrimmFrame("GrimmAppear", g.AnimTime, false);
            }
            else if (g.Phase == 1)
            {
                bool moving = Mathf.Abs(Vx) > 0.05f || !Grounded;
                sprite = GrimmFrame(moving ? "GrimmFly" : "GrimmIdle", g.AnimTime, true);
            }
            else if (g.Phase == 2)
            {
                sprite = GrimmFrame("GrimmTeleport", g.AnimTime, false);
            }
            else if (g.Phase == 3)
            {
                if (g.SleepStage == 0)
                {
                    sprite = GrimmFrame("GrimmIdle", g.AnimTime, true); // 降落中仍待机
                }
                else if (g.SleepStage == 1)
                {
                    sprite = GrimmFrame("GrimmSleep", g.AnimTime, false);
                }
                else
                {
                    // 保持睡眠末帧（sleep0002）：直接取剪辑最后一帧，
                    // 不能用超大时间走 GrimmFrame（float→int 溢出会取到第 0 帧）
                    if (_clips.TryGetValue("GrimmSleep", out ClipData sc) && sc.frames.Length > 0)
                    {
                        sprite = sc.frames[sc.frames.Length - 1];
                    }
                }
            }
            else if (g.Phase == 4)
            {
                sprite = GrimmFrame("GrimmWake", g.AnimTime, false);
            }
            else if (g.Phase == 5)
            {
                sprite = GrimmFrame("GrimmShoot", g.AnimTime, false);
            }
            if (sprite == null || !_textures.TryGetValue(sprite, out Texture2D tex))
            {
                MdOut = _grimmMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float dxm = (g.X - X) * _mp.CLEN;
            float dym = -(g.Y - Y) * _mp.CLEN;
            float w = tex.width * GrimmScale;
            float h = tex.height * GrimmScale;
            _grimmMesh.Col = MTRX.ColWhite;
            _grimmMesh.initForImgAndTexture(tex);
            _grimmMesh.uv_top = 0f;
            _grimmMesh.uv_height = 1f;
            // 面朝约定：_faceDir<0=面朝右（翻转），_faceDir>0=面朝左（保持原图，素材默认朝左）
            if (_faceDir < 0f)
            {
                _grimmMesh.uv_left = 1f;
                _grimmMesh.uv_width = -1f;
            }
            else
            {
                _grimmMesh.uv_left = 0f;
                _grimmMesh.uv_width = 1f;
            }
            _grimmMesh.Rect(dxm, dym, w, h, false); // Rect 的 (x,y) 是中心
            MdOut = _grimmMesh;
            return true;
        }

        /// <summary>小格林火球渲染：独立网格（循环播 grimm_fireball0000~0007，16fps）。</summary>
        private bool KnightPrepareGrimmFireballMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _grimmFireballMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _grimmFireballMesh.clearSimple();
            if (!CharmEffects.IsEquipped(CharmEffects.GrimmId) || _grimmFireballs.Count == 0 ||
                !_clips.TryGetValue("GrimmFireball", out ClipData fbClip) || fbClip.frames.Length == 0)
            {
                MdOut = _grimmFireballMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _grimmFireballMesh.Col = MTRX.ColWhite;
            for (int fi = 0; fi < _grimmFireballs.Count; fi++)
            {
                GrimmFireball f = _grimmFireballs[fi];
                // 用已存活时间正向取帧（Life 是倒计时，直接用会倒放）
                int fidx = ((int)((GrimmFireballLife - f.Life) * fbClip.fps)) % fbClip.frames.Length;
                if (fidx < 0) fidx = 0;
                if (!_textures.TryGetValue(fbClip.frames[fidx], out Texture2D ftex))
                {
                    continue;
                }
                _grimmFireballMesh.initForImgAndTexture(ftex);
                _grimmFireballMesh.uv_top = 0f;
                _grimmFireballMesh.uv_height = 1f;
                _grimmFireballMesh.uv_left = 0f;
                _grimmFireballMesh.uv_width = 1f;
                float fdx = (f.X - X) * _mp.CLEN;
                float fdy = -(f.Y - Y) * _mp.CLEN;
                float fbw = ftex.width * GrimmFireballScale;
                float fbh = ftex.height * GrimmFireballScale;
                _grimmFireballMesh.Rect(fdx, fdy, fbw, fbh, false);
            }
            MdOut = _grimmFireballMesh;
            return true;
        }

        /// <summary>
        /// 蜕变挽歌调试：绿色线框标出每道剑气的判定箱（矩阵锚定小骑士原点）。
        /// </summary>
        private bool KnightPrepareElegyBladeDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _elegyBladeDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _elegyBladeDbgMesh.clearSimple();
            if (!ElegyBladeHitboxDebug || _elegyBlades.Count == 0)
            {
                MdOut = _elegyBladeDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float hw = ElegyBladeHitboxW * 0.5f * _mp.CLEN;
            float hh = ElegyBladeHitboxH * 0.5f * _mp.CLEN;
            _elegyBladeDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);
            for (int bi = 0; bi < _elegyBlades.Count; bi++)
            {
                ElegyBladeProj p = _elegyBlades[bi];
                // 与判定框一致：向下 0.5 格；向左 x-1.2、向右 x+0.2
                float hx = p.X + (p.Dir < 0f ? -1.2f : 0.2f);
                float hy = p.Y + 0.5f;
                float dx = (hx - X) * _mp.CLEN;
                float dy = -(hy - Y) * _mp.CLEN;
                _elegyBladeDbgMesh.Line(dx - hw, dy - hh, dx + hw, dy - hh, 2f);
                _elegyBladeDbgMesh.Line(dx + hw, dy - hh, dx + hw, dy + hh, 2f);
                _elegyBladeDbgMesh.Line(dx + hw, dy + hh, dx - hw, dy + hh, 2f);
                _elegyBladeDbgMesh.Line(dx - hw, dy + hh, dx - hw, dy - hh, 2f);
            }
            MdOut = _elegyBladeDbgMesh;
            return true;
        }

        /// <summary>
        /// 普攻判定框调试：绿色线框标出平砍/上劈/下劈的“矩形+尖端”判定箱。
        /// 直接读取实际碰撞体（BoxCollider2D + PolygonCollider2D）的几何，
        /// 转换到骑士本地空间绘制，保证绿框与真实碰撞箱严格重合。
        /// </summary>
        private bool KnightPrepareAttackDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _attackDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _attackDbgMesh.clearSimple();
            if (!AttackHitboxDebug || _hitboxGo == null || !_attacking)
            {
                if (DiveHitboxDebug)
                {
                    // 重新设置票据矩阵（否则沿用上次挥砍时的旧锚点，绿框会跟着骑士漂移）
                    float dmx = _mp.pixel2ux(X * _mp.CLEN);
                    float dmy = _mp.pixel2uy(Y * _mp.CLEN);
                    Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                                Matrix4x4.Translate(new Vector3(dmx, dmy, 0f));
                    for (int i = 0; i < _diveSwords.Count; i++)
                    {
                        DiveSword s = _diveSwords[i];
                        if (s.Height > 0.03f)
                        {
                            DrawDbgRect(_attackDbgMesh, s.X + DiveSwordRenderOffX + DiveSwordHitboxOffX,
                                s.BaseY - s.Height * 0.5f + DiveSwordHitboxOffY, s.Width, s.Height);
                        }
                    }
                    for (int i = 0; i < _diveSpikes.Count; i++)
                    {
                        DiveSpike p = _diveSpikes[i];
                        if (p.Height > 0.02f)
                        {
                            DrawDbgRect(_attackDbgMesh, p.X + DiveSpikeHitboxOffX,
                                p.BaseY - p.Height * 0.5f + DiveSpikeHitboxOffY, DiveSpikeBaseWidth, p.Height);
                        }
                    }
                }
                MdOut = _attackDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _attackDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);

            // 世界点 → 地图根本地坐标(ux) → 相对骑士锚点(mx,my) → ×64 得 mesh px
            Transform mapT = _mp.gameObject.transform;
            Vector3 ToAnchor(float wx, float wy)
            {
                Vector3 lp = mapT.InverseTransformPoint(new Vector3(wx, wy, 0f));
                return new Vector3((lp.x - mx) * UxToMeshPx, (lp.y - my) * UxToMeshPx, 0f);
            }

            // 矩形部分：BoxCollider2D 世界包围盒四角
            BoxCollider2D box = _hitboxGo.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                Vector3 min = box.bounds.min;
                Vector3 max = box.bounds.max;
                Vector3 c0 = ToAnchor(min.x, min.y);
                Vector3 c1 = ToAnchor(max.x, min.y);
                Vector3 c2 = ToAnchor(max.x, max.y);
                Vector3 c3 = ToAnchor(min.x, max.y);
                _attackDbgMesh.Line(c0.x, c0.y, c1.x, c1.y, 2f);
                _attackDbgMesh.Line(c1.x, c1.y, c2.x, c2.y, 2f);
                _attackDbgMesh.Line(c2.x, c2.y, c3.x, c3.y, 2f);
                _attackDbgMesh.Line(c3.x, c3.y, c0.x, c0.y, 2f);
            }

            // 尖端三角形：PolygonCollider2D 三个顶点（tipGo 本地 → 世界 → 地图本地）
            Transform tipT = _hitboxGo.transform.Find("KnightAttackTip");
            if (tipT != null)
            {
                PolygonCollider2D poly = tipT.GetComponent<PolygonCollider2D>();
                if (poly != null)
                {
                    Vector2[] pts = poly.points;
                    if (pts != null && pts.Length >= 3)
                    {
                        Vector3[] mp = new Vector3[pts.Length];
                        for (int i = 0; i < pts.Length; i++)
                        {
                            Vector3 wp = tipT.TransformPoint(pts[i]);
                            mp[i] = ToAnchor(wp.x, wp.y);
                        }
                        Vector3 p0 = mp[mp.Length - 1];
                        for (int i = 0; i < pts.Length; i++)
                        {
                            Vector3 p1 = mp[i];
                            _attackDbgMesh.Line(p0.x, p0.y, p1.x, p1.y, 2f);
                            p0 = p1;
                        }
                    }
                }
            }
            if (DiveHitboxDebug)
            {
                // 下砸骨剑/尖刺判定箱（与本机实际伤害判定同一组数值）
                for (int i = 0; i < _diveSwords.Count; i++)
                {
                    DiveSword s = _diveSwords[i];
                    if (s.Height > 0.03f)
                    {
                        DrawDbgRect(_attackDbgMesh, s.X + DiveSwordRenderOffX + DiveSwordHitboxOffX,
                            s.BaseY - s.Height * 0.5f + DiveSwordHitboxOffY, s.Width, s.Height);
                    }
                }
                for (int i = 0; i < _diveSpikes.Count; i++)
                {
                    DiveSpike p = _diveSpikes[i];
                    if (p.Height > 0.02f)
                    {
                        DrawDbgRect(_attackDbgMesh, p.X + DiveSpikeHitboxOffX,
                            p.BaseY - p.Height * 0.5f + DiveSpikeHitboxOffY, DiveSpikeBaseWidth, p.Height);
                    }
                }
            }
            MdOut = _attackDbgMesh;
            return true;
        }

        /// <summary>
        /// 受击碰撞箱调试：
        /// 绿框 = 小骑士期望的受击箱（CollideX0/1、CollideY0/1）；
        /// 橙框 = **宿主诺艾尔身上真正参与物理/受击的那个碰撞体**（挂在 M2MvColliderCreator.Cld 上），
        /// 用来确认骑士模式的尺寸限制是否生效（两者应基本重合；诺艾尔被游戏压身时会临时更小）。
        /// 绘制链与普攻绿框一致：相对骑士锚点(mx,my) 的 mesh px；1 格 = CLEN mesh px。
        /// </summary>
        private bool KnightPrepareHurtDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _hurtDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _hurtDbgMesh.clearSimple();
            if (!HurtBoxDebug || !_active)
            {
                MdOut = _hurtDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _hurtDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);
            // 受击箱网格像素：相对骑士中心 (0,0)，x/y 各偏移 = 格数 × CLEN
            float x0 = CollideX0 * _mp.CLEN;
            float x1 = CollideX1 * _mp.CLEN;
            float y0 = CollideY0 * _mp.CLEN;
            float y1 = CollideY1 * _mp.CLEN;
            _hurtDbgMesh.Line(x0, y0, x1, y0, 2f);
            _hurtDbgMesh.Line(x1, y0, x1, y1, 2f);
            _hurtDbgMesh.Line(x1, y1, x0, y1, 2f);
            _hurtDbgMesh.Line(x0, y1, x0, y0, 2f);
            // 橙色框：宿主诺艾尔身上真正参与物理/受击的碰撞体（骑士模式下应被限制到小骑士尺寸，
            // 游戏压身时会更小 —— 用来排查“卡在窄缝/矮洞里出不去”）
            try
            {
                PRNoel pr = GetPr();
                M2MvColliderCreator cc = pr != null ? pr.getColliderCreator() : null;
                PolygonCollider2D hostCol = cc != null ? cc.Cld : null;
                if (hostCol != null && hostCol.enabled)
                {
                    Bounds b = hostCol.bounds;
                    if (TryWorldRectToCellRect(b.min, b.max, out Vector4 hr))
                    {
                        _hurtDbgMesh.Col = new Color(1f, 0.55f, 0.1f, 0.95f);
                        DrawDbgRect(_hurtDbgMesh, hr.x, hr.y, hr.z, hr.w);
                    }
                }
            }
            catch (Exception)
            {
            }
            MdOut = _hurtDbgMesh;
            return true;
        }

        /// <summary>
        /// 蓄力劈砍（强力劈砍）判定框调试：绿色线框标出矩形判定箱。
        /// 与 CheckNailArtHit 同源：中心按格、尺寸按 Physics2D 的世界单位，
        /// 这里把“实际查询用的世界矩形”换算回格再画（见 DrawDebugWorldRect），
        /// 因此绿框与真实判定箱严格重合，不受 ux/格 单位口径影响。
        /// </summary>
        private bool KnightPrepareNailArtDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _nailArtDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _nailArtDbgMesh.clearSimple();
            if (!NailArtHitboxDebug || !_nailArtSlashing)
            {
                MdOut = _nailArtDbgMesh;
                return true;
            }
            SetDebugAnchorMatrix(Tk);
            DrawDebugWorldRect(_nailArtDbgMesh,
                CellPosToWorld(X + _nailArtSlashDir * NailArtHitboxOffX, Y + NailArtHitboxOffY),
                new Vector2(NailArtHitboxW, NailArtHitboxH));
            MdOut = _nailArtDbgMesh;
            return true;
        }

        /// <summary>
        /// 冲刺劈砍判定框调试：绿色线框标出矩形判定箱。
        /// 与 CheckDashSlashHit 同源（中心按格、尺寸按世界单位 → 换算回格再画）。
        /// </summary>
        private bool KnightPrepareDashSlashDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _dashSlashDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _dashSlashDbgMesh.clearSimple();
            if (!DashSlashHitboxDebug || !_dashSlashing)
            {
                MdOut = _dashSlashDbgMesh;
                return true;
            }
            SetDebugAnchorMatrix(Tk);
            DrawDebugWorldRect(_dashSlashDbgMesh,
                CellPosToWorld(X + _dashSlashDir * DashSlashHitboxOffX, Y + DashSlashHitboxOffY),
                new Vector2(DashSlashHitboxW, DashSlashHitboxH));
            MdOut = _dashSlashDbgMesh;
            return true;
        }

        /// <summary>
        /// 旋风劈砍判定框调试：绿色线框标出矩形判定箱（左右各 4 格、高 2 格）。
        /// 与 CheckCycloneHit 同源（中心按格、尺寸按世界单位 → 换算回格再画）。
        /// </summary>
        private bool KnightPrepareCycloneDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _cycloneDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _cycloneDbgMesh.clearSimple();
            if (!CycloneHitboxDebug || !_cycloneSlashing)
            {
                MdOut = _cycloneDbgMesh;
                return true;
            }
            SetDebugAnchorMatrix(Tk);
            DrawDebugWorldRect(_cycloneDbgMesh,
                CellPosToWorld(X, Y + CycloneSlashHitboxOffY),
                new Vector2(CycloneSlashHitboxW, CycloneSlashHitboxH));
            MdOut = _cycloneDbgMesh;
            return true;
        }

        /// <summary>
        /// 调试线框票据的矩阵锚点：与小骑士中心对齐（与正文渲染同一套坐标换算），
        /// 之后 DrawDbgRect 传入的“格”坐标即可直接画在正确位置。
        /// </summary>
        private void SetDebugAnchorMatrix(M2RenderTicket Tk)
        {
            if (_mp == null || Tk == null)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
        }

        /// <summary>格坐标 → 世界坐标（与 Physics2D 查询用的 TransformPoint 完全一致）。</summary>
        private Vector2 CellPosToWorld(float cellX, float cellY)
        {
            if (_mp == null)
            {
                return Vector2.zero;
            }
            return _mp.gameObject.transform.TransformPoint(new Vector2(
                _mp.pixel2ux(cellX * _mp.CLEN), _mp.pixel2uy(cellY * _mp.CLEN)));
        }

        /// <summary>
        /// 把“Physics2D 实际查询用的世界矩形”画成绿框：
        /// 世界 → 地图本地(ux) → Map2d 格坐标 → DrawDbgRect（相对骑士锚点的 mesh px）。
        /// 这条换算链与 strategy.cs 记录的方法论一致，不需要假设“1 世界单位 = 几格”。
        /// </summary>
        private void DrawDebugWorldRect(MeshDrawer mesh, Vector2 worldCenter, Vector2 worldSize)
        {
            if (mesh == null || _mp == null || worldSize.x <= 0.0001f || worldSize.y <= 0.0001f)
            {
                return;
            }
            if (TryWorldRectToCellRect(
                new Vector3(worldCenter.x - worldSize.x * 0.5f, worldCenter.y - worldSize.y * 0.5f, 0f),
                new Vector3(worldCenter.x + worldSize.x * 0.5f, worldCenter.y + worldSize.y * 0.5f, 0f),
                out Vector4 r))
            {
                mesh.Col = new Color(0f, 1f, 0f, 0.9f);
                DrawDbgRect(mesh, r.x, r.y, r.z, r.w);
            }
        }

        /// <summary>
        /// 拼刀判定框调试（NailParryDebug=true 时启用）：
        /// 绿框 = 本地骨钉（普攻 / 强力·冲刺·旋风劈砍）判定箱；
        /// 红框 = 本帧同步到的远端小骑士骨钉判定箱（需联机 Kaleidoscopic）。
        /// 两框相交即拼刀，用来核对同步过来的判定箱与本地是否对得上。
        /// </summary>
        private bool KnightPrepareNailParryDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _nailParryDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _nailParryDbgMesh.clearSimple();
            if (!NailParryDebug || !_active)
            {
                MdOut = _nailParryDbgMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            // 本地：绿框（与拼刀检测同源，每帧重算）
            _nailParrySelfRects.Clear();
            CollectNailAttackRects(_nailParrySelfRects);
            _nailParryDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);
            for (int i = 0; i < _nailParrySelfRects.Count; i++)
            {
                Vector4 r = _nailParrySelfRects[i];
                DrawDbgRect(_nailParryDbgMesh, r.x, r.y, r.z, r.w);
            }
            // 远端：红框
            _nailParryDbgMesh.Col = new Color(1f, 0.25f, 0.25f, 0.9f);
            MultiplayerCompat.ForEachRemoteNailRect((cx, cy, w, h) =>
                DrawDbgRect(_nailParryDbgMesh, cx, cy, w, h));
            MdOut = _nailParryDbgMesh;
            return true;
        }

        /// <summary>
        /// 施法瞬间爆发特效：绘制在骑士面前 0.5 格，按发射方向左右镜像，一次性播放。
        /// </summary>
        private bool KnightPrepareFireballBlastMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _fireballBlastMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _fireballBlastMesh.clearSimple();
            if (_fireballBlastSprite == null ||
                !_textures.TryGetValue(_fireballBlastSprite, out Texture2D tex))
            {
                MdOut = _fireballBlastMesh;
                return true;
            }
            float mx = _mp.pixel2ux(_fireballBlastX * _mp.CLEN);
            float my = _mp.pixel2uy(_fireballBlastY * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = tex.width * scale;
            float h = tex.height * scale;
            _fireballBlastMesh.Col = MTRX.ColWhite;
            _fireballBlastMesh.initForImgAndTexture(tex);
            _fireballBlastMesh.uv_top = 0f;
            _fireballBlastMesh.uv_height = 1f;
            if (_fireballDir > 0f)
            {
                _fireballBlastMesh.uv_left = 1f;
                _fireballBlastMesh.uv_width = -1f;
            }
            else
            {
                _fireballBlastMesh.uv_left = 0f;
                _fireballBlastMesh.uv_width = 1f;
            }
            _fireballBlastMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _fireballBlastMesh;
            return true;
        }

        /// <summary>深渊尖啸：上升冲击波特效渲染，矩阵锚定骑士中心，一次性播完。</summary>
        private bool KnightPrepareScreamBlastMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _screamBlastMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _screamBlastMesh.clearSimple();
            if (!_screaming || _screamBlastSprite == null ||
                !_textures.TryGetValue(_screamBlastSprite, out Texture2D tex))
            {
                MdOut = _screamBlastMesh;
                return true;
            }
            float mx = _mp.pixel2ux((X + ScreamBlastRenderOffX) * _mp.CLEN);
            float my = _mp.pixel2uy((Y + ScreamBlastRenderOffY) * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float w = ScreamBlastRenderW * _mp.CLEN; // 固定 6.5×6.5 格
            float h = ScreamBlastRenderH * _mp.CLEN;
            _screamBlastMesh.Col = MTRX.ColWhite;
            _screamBlastMesh.initForImgAndTexture(tex);
            _screamBlastMesh.uv_top = 0f;
            _screamBlastMesh.uv_height = 1f;
            _screamBlastMesh.uv_left = 0f;
            _screamBlastMesh.uv_width = 1f;
            _screamBlastMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _screamBlastMesh;
            return true;
        }

        /// <summary>深渊尖啸黑/白粒子渲染：按 Layer 分层（0=身后 PR0、1=身前 PR1），圆形贴图黑/白。</summary>
        private bool KnightPrepareScreamFxBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareScreamFxLayer(_screamFxBackMesh, 0, Cam, Tk, draw_id, out MdOut);
        }

        private bool KnightPrepareScreamFxFrontMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareScreamFxLayer(_screamFxFrontMesh, 1, Cam, Tk, draw_id, out MdOut);
        }

        private bool PrepareScreamFxLayer(MeshDrawer mesh, int layer, Camera Cam, M2RenderTicket Tk,
            int draw_id, out MeshDrawer MdOut)
        {
            MdOut = null;
            if (_mp == null || mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            mesh.clearSimple();
            if (_dotTexes[1] == null || _screamParticles.Count == 0)
            {
                MdOut = mesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            mesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _screamParticles.Count; i++)
            {
                LightDotParticle d = _screamParticles[i];
                if (d.Layer != layer)
                {
                    continue;
                }
                float alpha = Mathf.Clamp01(d.Age / 0.05f) *
                              Mathf.Clamp01((d.Life - d.Age) / 0.3f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = d.Size * _mp.CLEN;
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                mesh.Col = new Color(d.Color.r / 255f, d.Color.g / 255f, d.Color.b / 255f, alpha);
                mesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = mesh;
            return true;
        }

        /// <summary>蓄力白色粒子渲染：锚定骑士中心，骑士身后层（PR0），到达目标前不透明、蓄满后 0.2s 淡出。</summary>
        private bool KnightPrepareNailArtChargeParticleMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _nailArtChargeParticleMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _nailArtChargeParticleMesh.clearSimple();
            if (_dotTexes[1] == null || _nailArtChargeParticles.Count == 0)
            {
                MdOut = _nailArtChargeParticleMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _nailArtChargeParticleMesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _nailArtChargeParticles.Count; i++)
            {
                LightDotParticle d = _nailArtChargeParticles[i];
                float alpha = Mathf.Clamp01((d.Life - d.Age) / 0.2f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = d.Size * _mp.CLEN;
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                _nailArtChargeParticleMesh.Col = new Color(1f, 1f, 1f, alpha);
                _nailArtChargeParticleMesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = _nailArtChargeParticleMesh;
            return true;
        }

        /// <summary>强力劈砍蓄满光圈渲染：锚定骑士中心，骑士身后层（PR0），循环播放 nail_charge_effect。</summary>
        private bool KnightPrepareNailArtGlowMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _nailArtGlowMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _nailArtGlowMesh.clearSimple();
            if (!_nailArtCharged || _nailArtGlowSprite == null ||
                !_textures.TryGetValue(_nailArtGlowSprite, out Texture2D tex))
            {
                MdOut = _nailArtGlowMesh;
                return true;
            }
            float mx = _mp.pixel2ux((X + NailArtGlowOffX) * _mp.CLEN);
            float my = _mp.pixel2uy((Y + NailArtGlowOffY) * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            float w = tex.width * scale;
            float h = tex.height * scale;
            _nailArtGlowMesh.Col = MTRX.ColWhite;
            _nailArtGlowMesh.initForImgAndTexture(tex);
            _nailArtGlowMesh.uv_top = 0f;
            _nailArtGlowMesh.uv_height = 1f;
            _nailArtGlowMesh.uv_left = 0f;
            _nailArtGlowMesh.uv_width = 1f;
            _nailArtGlowMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _nailArtGlowMesh;
            return true;
        }

        /// <summary>强力劈砍剑气渲染：锚定骑士中心（Y 上移 0.5 格），一次性播完 charge_slash_effect，朝面向方向镜像。</summary>
        private bool KnightPrepareNailArtSlashFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _nailArtSlashFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _nailArtSlashFxMesh.clearSimple();
            if (!_nailArtSlashing || _nailArtSlashFxSprite == null ||
                !_textures.TryGetValue(_nailArtSlashFxSprite, out Texture2D tex))
            {
                MdOut = _nailArtSlashFxMesh;
                return true;
            }
            // 剑气渲染中心：右劈（_faceDir<0）右移 1.75、左劈右移 0.25（右侧对称修正）
            float mx = _mp.pixel2ux(
                (X + (_faceDir < 0f ? NailArtSlashFxOffX : NailArtSlashFxOffXLeft)) * _mp.CLEN);
            float my = _mp.pixel2uy((Y + NailArtSlashFxOffY) * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float w = NailArtSlashFxW * _mp.CLEN; // 固定 3.5×2 格
            float h = NailArtSlashFxH * _mp.CLEN;
            _nailArtSlashFxMesh.Col = MTRX.ColWhite;
            _nailArtSlashFxMesh.initForImgAndTexture(tex);
            _nailArtSlashFxMesh.uv_top = 0f;
            _nailArtSlashFxMesh.uv_height = 1f;
            // 面朝约定：_faceDir<0=面朝右、>0=面朝左；镜像方向与普攻剑气一致
            if (_faceDir < 0f)
            {
                _nailArtSlashFxMesh.uv_left = 1f;
                _nailArtSlashFxMesh.uv_width = -1f;
            }
            else
            {
                _nailArtSlashFxMesh.uv_left = 0f;
                _nailArtSlashFxMesh.uv_width = 1f;
            }
            _nailArtSlashFxMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _nailArtSlashFxMesh;
            return true;
        }

        /// <summary>冲刺劈砍剑气渲染：锚定骑士中心，一次性播完 dash_slash_effect，按冲刺方向镜像。</summary>
        private bool KnightPrepareDashSlashFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _dashSlashFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _dashSlashFxMesh.clearSimple();
            if (!_dashSlashing || _dashSlashFxSprite == null ||
                !_textures.TryGetValue(_dashSlashFxSprite, out Texture2D tex))
            {
                MdOut = _dashSlashFxMesh;
                return true;
            }
            // 渲染偏移：整体 y-0.3；朝右（_dashSlashDir>0）额外 x+5
            float fx = _dashSlashDir > 0f ? DashSlashFxOffXRight : 0f;
            float mx = _mp.pixel2ux((X + fx) * _mp.CLEN);
            float my = _mp.pixel2uy((Y + DashSlashFxOffY) * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float w = DashSlashFxW * _mp.CLEN; // 固定 5×2 格
            float h = DashSlashFxH * _mp.CLEN;
            _dashSlashFxMesh.Col = MTRX.ColWhite;
            _dashSlashFxMesh.initForImgAndTexture(tex);
            _dashSlashFxMesh.uv_top = 0f;
            _dashSlashFxMesh.uv_height = 1f;
            // 冲刺方向镜像：_dashSlashDir>0=向右冲 → 镜像
            if (_dashSlashDir > 0f)
            {
                _dashSlashFxMesh.uv_left = 1f;
                _dashSlashFxMesh.uv_width = -1f;
            }
            else
            {
                _dashSlashFxMesh.uv_left = 0f;
                _dashSlashFxMesh.uv_width = 1f;
            }
            _dashSlashFxMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _dashSlashFxMesh;
            return true;
        }

        /// <summary>旋风劈砍旋转剑气渲染：锚定骑士中心下 0.5 格，8×2 格，骑士身后层（PR0），循环播放。</summary>
        private bool KnightPrepareCycloneFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _cycloneFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _cycloneFxMesh.clearSimple();
            if (!_cycloneSlashing || _cycloneFxSprite == null ||
                !_textures.TryGetValue(_cycloneFxSprite, out Texture2D tex))
            {
                MdOut = _cycloneFxMesh;
                return true;
            }
            float my = _mp.pixel2uy((Y + CycloneSlashHitboxOffY + CycloneFxOffY) * _mp.CLEN);
            float mx = _mp.pixel2ux((X + CycloneFxOffX) * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float w = CycloneSlashHitboxW * _mp.CLEN; // 8×2 格
            float h = CycloneSlashHitboxH * _mp.CLEN;
            _cycloneFxMesh.Col = MTRX.ColWhite;
            _cycloneFxMesh.initForImgAndTexture(tex);
            _cycloneFxMesh.uv_top = 0f;
            _cycloneFxMesh.uv_height = 1f;
            _cycloneFxMesh.uv_left = 0f;
            _cycloneFxMesh.uv_width = 1f;
            _cycloneFxMesh.Rect(-w * 0.5f, -h * 0.5f, w, h, false);
            MdOut = _cycloneFxMesh;
            return true;
        }

        private bool KnightPrepareDreamParticleBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareDreamParticleLayer(_dreamParticleBackMesh, 0, Cam, Tk, draw_id, out MdOut);
        }

        private bool KnightPrepareDreamParticleFrontMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareDreamParticleLayer(_dreamParticleFrontMesh, 1, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>
        /// 骨剑渲染：矩阵锚定小骑士原点。剑身从落点（地面）向上生长，
        /// 用 UV 裁剪只显示贴图底部当前可见部分（升起/降下各 0.05s）。
        /// </summary>
        private bool KnightPrepareDiveSwordMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _diveSwordMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _diveSwordMesh.clearSimple();
            if (!_textures.TryGetValue("nail_upgrade_0000_pure-nail", out Texture2D tex) || _diveSwords.Count == 0)
            {
                MdOut = _diveSwordMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _diveSwordMesh.initForImgAndTexture(tex);
            for (int i = 0; i < _diveSwords.Count; i++)
            {
                DiveSword s = _diveSwords[i];
                if (s.Height <= 0.03f)
                {
                    continue;
                }
                float f = Mathf.Clamp01(s.Height / s.MaxHeight); // 只显示贴图底部 f 段
                float dx = (s.X - X + DiveSwordRenderOffX) * _mp.CLEN;
                float bottom = -((s.BaseY + s.RenderOffY) - Y) * _mp.CLEN;
                float w = s.Width * _mp.CLEN;
                float h = s.Height * _mp.CLEN;
                _diveSwordMesh.Col = MTRX.ColWhite;
                _diveSwordMesh.uv_top = 1f - f;
                _diveSwordMesh.uv_height = f;
                _diveSwordMesh.uv_left = 0f;
                _diveSwordMesh.uv_width = 1f;
                _diveSwordMesh.Rect(dx - w * 0.5f, bottom, w, h, false);
            }
            MdOut = _diveSwordMesh;
            return true;
        }

        /// <summary>
        /// 尖刺渲染：矩阵锚定小骑士原点。尖刺从落点（地面）向上生长，
        /// 右侧镜像贴图（左右对称），升起/降下各 0.01s。
        /// </summary>
        private bool KnightPrepareDiveSpikeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _diveSpikeMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _diveSpikeMesh.clearSimple();
            if (!_textures.TryGetValue("white_spikes0000", out Texture2D tex) || _diveSpikes.Count == 0)
            {
                MdOut = _diveSpikeMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float spikeFullH = DiveSpikeBaseWidth * DiveSpikeAspect;
            _diveSpikeMesh.initForImgAndTexture(tex);
            for (int i = 0; i < _diveSpikes.Count; i++)
            {
                DiveSpike p = _diveSpikes[i];
                if (p.Height <= 0.02f)
                {
                    continue;
                }
                float f = Mathf.Clamp01(p.Height / spikeFullH);
                float dx = (p.X - X) * _mp.CLEN;
                float bottom = -((p.BaseY + DiveSpikeRenderOffY) - Y) * _mp.CLEN;
                float w = DiveSpikeBaseWidth * _mp.CLEN;
                float h = p.Height * _mp.CLEN;
                _diveSpikeMesh.Col = MTRX.ColWhite;
                _diveSpikeMesh.uv_top = 1f - f;
                _diveSpikeMesh.uv_height = f;
                // 右侧尖刺镜像贴图，与左侧对称
                if (p.X > _diveLandX)
                {
                    _diveSpikeMesh.uv_left = 1f;
                    _diveSpikeMesh.uv_width = -1f;
                }
                else
                {
                    _diveSpikeMesh.uv_left = 0f;
                    _diveSpikeMesh.uv_width = 1f;
                }
                _diveSpikeMesh.Rect(dx - w * 0.5f, bottom, w, h, false);
            }
            MdOut = _diveSpikeMesh;
            return true;
        }

        private bool KnightPrepareDiveFxBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareDiveFxLayer(_diveFxBackMesh, 0, Cam, Tk, draw_id, out MdOut);
        }

        private bool KnightPrepareDiveFxFrontMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            return PrepareDiveFxLayer(_diveFxFrontMesh, 1, Cam, Tk, draw_id, out MdOut);
        }

        /// <summary>落地黑/白粒子渲染：按 Layer 分层（0=身后、1=身前），圆形贴图黑/白。</summary>
        private bool PrepareDiveFxLayer(MeshDrawer mesh, int layer, Camera Cam, M2RenderTicket Tk,
            int draw_id, out MeshDrawer MdOut)
        {
            MdOut = null;
            if (_mp == null || mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            mesh.clearSimple();
            if (_dotTexes[1] == null || _diveLandParticles.Count == 0)
            {
                MdOut = mesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            mesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _diveLandParticles.Count; i++)
            {
                LightDotParticle d = _diveLandParticles[i];
                if (d.Layer != layer)
                {
                    continue;
                }
                // 淡入 0.05s，末尾 0.3s 淡出
                float alpha = Mathf.Clamp01(d.Age / 0.05f) *
                              Mathf.Clamp01((d.Life - d.Age) / 0.3f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = d.Size * _mp.CLEN;
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                mesh.Col = new Color(d.Color.r / 255f, d.Color.g / 255f, d.Color.b / 255f, alpha);
                mesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = mesh;
            return true;
        }

        /// <summary>暗影之魂调试：绿框标出冲击波碰撞箱。</summary>
        private bool KnightPrepareFireballDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _fireballDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _fireballDbgMesh.clearSimple();
            // 暗影之魂冲击波的碰撞箱绿框：每发冲击波各画一个（与 CheckFireballHit 的
            // Physics2D.OverlapBoxAll 中心/尺寸同源，世界矩形换算回格再画）
            if (FireballHitboxDebug)
            {
                SetDebugAnchorMatrix(Tk);
                for (int fi = 0; fi < _fireballs.Count; fi++)
                {
                    FireballProj proj = _fireballs[fi];
                    DrawDebugWorldRect(_fireballDbgMesh,
                        CellPosToWorld(proj.X + FireballHitboxOffX, proj.Y + FireballHitboxOffY),
                        new Vector2(FireballHitboxW, FireballHitboxH));
                }
            }
            MdOut = _fireballDbgMesh;
            return true;
        }

        /// <summary>
        /// Boss 本体碰撞箱调试：找到场内最大的存活 Boss（NelEnemyBoss / BOSS_ 前缀），
        /// 取其面积最大的碰撞体（本体，非触手）用绿框标出。
        /// </summary>
        private bool KnightPrepareBossDebugMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _bossDbgMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _bossDbgMesh.clearSimple();
            NelEnemy boss = FindBossEnemy();
            if (boss == null || boss.gameObject == null)
            {
                MdOut = _bossDbgMesh;
                return true;
            }
            // 取面积最大的碰撞体（本体）
            Collider2D best = null;
            float bestArea = -1f;
            Collider2D[] cols = boss.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider2D c = cols[i];
                if (c == null || !c.enabled)
                {
                    continue;
                }
                float area = c.bounds.size.x * c.bounds.size.y;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = c;
                }
            }
            if (best == null)
            {
                MdOut = _bossDbgMesh;
                return true;
            }
            Bounds b = best.bounds;
            Vector3 localPos = _mp.gameObject.transform.InverseTransformPoint(b.center);
            float uxPerCell = 64f / _mp.CLEN;
            float cx = localPos.x * uxPerCell + _mp.clms * 0.5f;   // 世界格 X
            float cy = _mp.rows * 0.5f - localPos.y * uxPerCell;   // 世界格 Y（向下为正）
            float sw = b.size.x * uxPerCell;
            float sh = b.size.y * uxPerCell;
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float px = (cx - X) * _mp.CLEN;
            float py = -(cy - Y) * _mp.CLEN; // Y 向下为正 → 网格 y 取负
            float hw = sw * 0.5f * _mp.CLEN;
            float hh = sh * 0.5f * _mp.CLEN;
            _bossDbgMesh.Col = new Color(0f, 1f, 0f, 0.9f);
            _bossDbgMesh.Line(px - hw, py - hh, px + hw, py - hh, 2f);
            _bossDbgMesh.Line(px + hw, py - hh, px + hw, py + hh, 2f);
            _bossDbgMesh.Line(px + hw, py + hh, px - hw, py + hh, 2f);
            _bossDbgMesh.Line(px - hw, py + hh, px - hw, py - hh, 2f);
            MdOut = _bossDbgMesh;
            return true;
        }

        /// <summary>查找当前地图中存活的 Boss（NelEnemyBoss 或名字以 BOSS_ 开头）。</summary>
        private NelEnemy FindBossEnemy()
        {
            if (_mp == null)
            {
                return null;
            }
            try
            {
                for (int i = _mp.count_movers - 1; i >= 0; i--)
                {
                    M2Mover mv = _mp.getMv(i);
                    NelEnemy en = mv as NelEnemy;
                    if (en != null && en.is_alive &&
                        (en is NelEnemyBoss ||
                         (en.name != null && en.name.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase))))
                    {
                        return en;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>游戏菜单（ESC 暂停）是否打开：以 UiGameMenu 激活状态为准，打开命令发出的那一帧也计入。</summary>
        private bool IsGameMenuOpen()
        {
            try
            {
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null)
                {
                    return false;
                }
                if (nm2d.GM != null && nm2d.GM.isActive())
                {
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前地图是否为黑暗区域（AIC dark_area 元数据，如 mount_darkcave）。</summary>
        private bool InDarkArea()
        {
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.map_dark_area)
                {
                    return true;
                }
                if (_mp != null && _mp.Meta != null)
                {
                    return _mp.Meta.GetB("dark", false) || _mp.Meta.GetI("dark_area", 0, 0) != 0;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>NightController.AWeather（私有字段）的反射缓存。</summary>
        private static readonly System.Reflection.FieldInfo NightConAWeatherField =
            AccessTools.Field(typeof(NightController), "AWeather");

        /// <summary>当前是否有雾天/浓雾（WeatherItem.MIST / MIST_DENSE），会遮蔽视线。</summary>
        private bool InFogWeather()
        {
            try
            {
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null || nm2d.NightCon == null || NightConAWeatherField == null)
                {
                    return false;
                }
                object arr = NightConAWeatherField.GetValue(nm2d.NightCon);
                if (arr is WeatherItem[] list)
                {
                    for (int i = 0; i < list.Length; i++)
                    {
                        WeatherItem wi = list[i];
                        if (wi != null &&
                            (wi.weather == WeatherItem.WEATHER.MIST ||
                             wi.weather == WeatherItem.WEATHER.MIST_DENSE))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>是否需要提灯动画：黑暗区域或雾天等视线遮蔽环境（同黑暗中的 lantern_idle/lantern_run）。</summary>
        private bool InLanternMode()
        {
            return InDarkArea() || InFogWeather();
        }

        /// <summary>雾天全屏雾层（M2FillingMistDrawer）的原始不透明度缓存：切回诺艾尔时恢复。</summary>
        private static readonly Dictionary<M2FillingMistDrawer, byte> _fogOriginalAlpha =
            new Dictionary<M2FillingMistDrawer, byte>();

        /// <summary>小骑士模式下把雾天全屏雾层的不透明度归零（小骑士不受视线遮蔽）。</summary>
        private void ApplyFogWeatherVisibility()
        {
            try
            {
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null || nm2d.NightCon == null || NightConAWeatherField == null)
                {
                    return;
                }
                object arr = NightConAWeatherField.GetValue(nm2d.NightCon);
                if (!(arr is WeatherItem[] list))
                {
                    return;
                }
                for (int i = 0; i < list.Length; i++)
                {
                    WeatherItem wi = list[i];
                    if (wi == null || wi.DrM == null)
                    {
                        continue;
                    }
                    M2FillingMistDrawer dr = wi.DrM;
                    if (!_fogOriginalAlpha.ContainsKey(dr))
                    {
                        _fogOriginalAlpha[dr] = dr.C.a;
                    }
                    if (dr.C.a != 0)
                    {
                        dr.C.a = 0;
                        dr.need_reset_alpha = true; // 触发下一帧用新不透明度重绘顶点色
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>切回诺艾尔时恢复雾天雾层的原版不透明度。</summary>
        public static void RestoreFogWeatherVisibility()
        {
            try
            {
                foreach (KeyValuePair<M2FillingMistDrawer, byte> kv in _fogOriginalAlpha)
                {
                    M2FillingMistDrawer dr = kv.Key;
                    if (dr == null)
                    {
                        continue;
                    }
                    if (dr.C.a != kv.Value)
                    {
                        dr.C.a = kv.Value;
                        dr.need_reset_alpha = true;
                    }
                }
                _fogOriginalAlpha.Clear();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>小骑士在黑暗区域时，黑暗覆盖（M2DarkRenderer）的不透明度；1=原版全黑，越小越亮。</summary>
        private const float LanternDarkAlpha = 0.5f;

        /// <summary>小骑士在黑暗区域时调亮整个房间：降低 M2DarkRenderer 不透明度；非黑暗区域恢复原值。</summary>
        private void ApplyDarkAreaBrightness()
        {
            try
            {
                Map2d mp = _mp;
                if (mp == null || mp.Unstb == null)
                {
                    return;
                }
                // 0.29j 起 GetBinder 返回 M2UnstabilizeMapItem.ICameraRenderBinderUnstb（旧版返回 ICameraRenderBinder）
                M2UnstabilizeMapItem.ICameraRenderBinderUnstb binder =
                    mp.Unstb.GetBinder(M2UnstabilizeMapItem.key_dark);
                if (binder is M2DarkRenderer dark)
                {
                    dark.base_alpha = InDarkArea() ? LanternDarkAlpha : 1f;
                    dark.need_fine_mesh = true; // 强制重建网格，让新不透明度立即生效
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>切回诺艾尔时恢复黑暗覆盖的原版不透明度。</summary>
        public static void RestoreDarkAreaBrightness()
        {
            try
            {
                M2DBase m2d = M2DBase.Instance;
                Map2d mp = m2d != null ? m2d.curMap : null;
                if (mp == null || mp.Unstb == null)
                {
                    return;
                }
                M2UnstabilizeMapItem.ICameraRenderBinderUnstb binder =
                    mp.Unstb.GetBinder(M2UnstabilizeMapItem.key_dark);
                if (binder is M2DarkRenderer dark)
                {
                    dark.base_alpha = 1f;
                    dark.need_fine_mesh = true; // 强制重建网格，恢复原版黑暗
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 低血量（1 格）黑色虚空粒子：绝对世界坐标绘制，黑色圆点向上冒出。
        /// </summary>
        private bool KnightPrepareLowHpMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _lowHpMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _lowHpMesh.clearSimple();
            if (_dotTexes[1] == null || _lowHpParticles.Count == 0)
            {
                MdOut = _lowHpMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _lowHpMesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _lowHpParticles.Count; i++)
            {
                LightDotParticle d = _lowHpParticles[i];
                float alpha = Mathf.Clamp01(d.Age / 0.05f) * Mathf.Clamp01((d.Life - d.Age) / 0.25f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = d.Size * _mp.CLEN;
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                _lowHpMesh.Col = new Color(0f, 0f, 0f, alpha);
                _lowHpMesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = _lowHpMesh;
            return true;
        }

        /// <summary>
        /// 受击粒子爆发：绝对世界坐标绘制，白色/黑色粒子向四周散射。
        /// </summary>
        private bool KnightPrepareHitFxMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _hitFxMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _hitFxMesh.clearSimple();
            if (_dotTexes[1] == null || _hitFxParticles.Count == 0)
            {
                MdOut = _hitFxMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _hitFxMesh.initForImgAndTexture(_dotTexes[1]);
            for (int i = 0; i < _hitFxParticles.Count; i++)
            {
                LightDotParticle d = _hitFxParticles[i];
                float alpha = Mathf.Clamp01(d.Age / 0.04f) * Mathf.Clamp01((d.Life - d.Age) / 0.18f);
                if (alpha < 0.04f)
                {
                    continue;
                }
                float size = d.Size * _mp.CLEN;
                float dxm = (d.X - X) * _mp.CLEN;
                float dym = -(d.Y - Y) * _mp.CLEN;
                _hitFxMesh.Col = new Color(d.Color.r / 255f, d.Color.g / 255f, d.Color.b / 255f, alpha);
                _hitFxMesh.Rect(dxm, dym, size, size, false);
            }
            MdOut = _hitFxMesh;
            return true;
        }

        /// <summary>
        /// 亡者之怒：身后红色光晕（背后层 PR0），脉冲闪烁。
        /// 节奏与原版 FlashingFury 一致：0.25s 升到峰值、短暂保持、0.25s 降回，峰值透明度 0.75。
        /// </summary>
        private bool KnightPrepareFuryGlowMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _furyGlowMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _furyGlowMesh.clearSimple();
            if (!FuryActive || _furyGlowTex == null)
            {
                MdOut = _furyGlowMesh;
                return true;
            }
            float t = _furyGlowTimer % 0.51f;
            float alpha = t < 0.25f
                ? t / 0.25f
                : (t < 0.26f ? 1f : 1f - (t - 0.26f) / 0.25f);
            alpha = Mathf.Clamp01(alpha) * 0.75f;
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _furyGlowMesh.initForImgAndTexture(_furyGlowTex);
            _furyGlowMesh.Col = new Color(1f, 0.25f, 0.15f, alpha);
            float size = 3.2f * _mp.CLEN; // 直径约 3.2 格
            // 渲染位置向下移动 0.5 格（网格 y 向上为正，向下为负）
            _furyGlowMesh.Rect(0f, -0.5f * _mp.CLEN, size, size, false);
            MdOut = _furyGlowMesh;
            return true;
        }

        /// <summary>
        /// 虚空解放第二段：小骑士身后渲染两张图，迅速来回切换，每次切换随机旋转角度。
        /// 背后层 PR0，位置跟随小骑士中心。
        /// </summary>
        private bool KnightPrepareVoidBackMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _voidBackMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _voidBackMesh.clearSimple();
            _voidBackMesh.Identity(); // clearSimple 不会重置 MatrixTransform，先 Identity 保证每帧干净
            if (_voidPhase != 3 || _voidBackTex1 == null || _voidBackTex2 == null)
            {
                MdOut = _voidBackMesh;
                return true;
            }
            // 双图迅速来回切换，每次切换随机旋转角度
            _voidBackSwitchTimer += Time.deltaTime;
            if (_voidBackSwitchTimer >= VoidBackSwitchInterval)
            {
                _voidBackSwitchTimer = 0f;
                _voidBackIndex = 1 - _voidBackIndex;
                _voidBackRot = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            }
            Texture2D tex = _voidBackIndex == 0 ? _voidBackTex1 : _voidBackTex2;
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _voidBackMesh.initForImgAndTexture(tex);
            _voidBackMesh.Col = MTRX.ColWhite;
            float size = VoidBackSize * _mp.CLEN;
            Matrix4x4 saved = _voidBackMesh.getCurrentMatrix();
            // 渲染中心下移 0.5 格（网格 y 向上为正，向下为负），旋转围绕该中心
            _voidBackMesh.Translate(0f, -0.5f * _mp.CLEN * 0.015625f, true);
            _voidBackMesh.Rotate(_voidBackRot, true);
            // Rect 的 (x,y) 是中心：传 (0,0) 使矩形中心与旋转原点重合
            _voidBackMesh.Rect(0f, 0f, size, size, false);
            _voidBackMesh.setCurrentMatrix(saved, false);
            MdOut = _voidBackMesh;
            return true;
        }

        /// <summary>
        /// 虚空解放出伤：在命中目标中心循环渲染 Radiance_GG_slashes，直到出伤结束。
        /// 身前层 PR1，目标死亡/消失则移除。
        /// </summary>
        private bool KnightPrepareVoidSlashMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _voidSlashMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _voidSlashMesh.clearSimple();
            if (_voidPhase != 3 || _voidSlashFrames == null || _voidSlashFrames.Length == 0 ||
                _voidSlashTargets.Count == 0)
            {
                MdOut = _voidSlashMesh;
                return true;
            }
            if (!_textures.TryGetValue(
                _voidSlashFrames[(int)(_voidSlashTimer * VoidSlashFps) % _voidSlashFrames.Length],
                out Texture2D tex))
            {
                MdOut = _voidSlashMesh;
                return true;
            }
            Transform mapT = _mp.gameObject.transform;
            Tk.Matrix = mapT.localToWorldMatrix;
            _voidSlashMesh.initForImgAndTexture(tex);
            _voidSlashMesh.Col = MTRX.ColWhite;
            float size = VoidSlashSize * _mp.CLEN;
            Vector3 ls = mapT.lossyScale;
            for (int i = _voidSlashTargets.Count - 1; i >= 0; i--)
            {
                VoidSlashTarget t = _voidSlashTargets[i];
                NelEnemy e = t.Enemy;
                if (e == null || e.gameObject == null || !e.is_alive)
                {
                    _voidSlashTargets.RemoveAt(i);
                    continue;
                }
                // Rect 的 x/y 是像素单位（内部 ÷64 成网格单位）：直接传 格×CLEN 像素 + 世界偏移换算成像素
                float px = e.x * _mp.CLEN + t.Offset.x * 64f / Mathf.Max(0.001f, ls.x);
                float py = e.y * _mp.CLEN + t.Offset.y * 64f / Mathf.Max(0.001f, ls.y);
                _voidSlashMesh.Rect(px, py, size, size, false);
            }
            MdOut = _voidSlashMesh;
            return true;
        }

        /// <summary>
        /// 生命血羁绊：身后蓝色光晕（背后层 PR0），脉冲闪烁节奏与亡者之怒一致。
        /// </summary>
        private bool KnightPrepareLifebloodGlowMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _lifebloodGlowMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _lifebloodGlowMesh.clearSimple();
            // 亡者之怒激活（1 血）时优先显示红色光晕，蓝色光晕让位
            if (!LifebloodBondActive() || FuryActive || _lifebloodGlowTex == null)
            {
                MdOut = _lifebloodGlowMesh;
                return true;
            }
            float t = _lifebloodGlowTimer % 0.51f;
            float alpha = t < 0.25f
                ? t / 0.25f
                : (t < 0.26f ? 1f : 1f - (t - 0.26f) / 0.25f);
            alpha = Mathf.Clamp01(alpha) * 0.75f;
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            _lifebloodGlowMesh.initForImgAndTexture(_lifebloodGlowTex);
            _lifebloodGlowMesh.Col = new Color(0.3f, 0.6f, 1f, alpha);
            float size = 3.2f * _mp.CLEN;
            _lifebloodGlowMesh.Rect(0f, -0.5f * _mp.CLEN, size, size, false);
            MdOut = _lifebloodGlowMesh;
            return true;
        }

        /// <summary>
        /// 巴尔德之壳：凝聚回血时的硬壳渲染（骑士身前层 PR1），
        /// 锚定骑士中心，按当前壳动画帧绘制（出现/受击/破碎/收起/持有帧）。
        /// </summary>
        private bool KnightPrepareBaldurShellMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _baldurShellMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }
            _baldurShellMesh.clearSimple();
            if (string.IsNullOrEmpty(_baldurFxSprite) ||
                !_textures.TryGetValue(_baldurFxSprite, out Texture2D tex))
            {
                MdOut = _baldurShellMesh;
                return true;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));
            float w = tex.width * BaldurShellScale;
            float h = tex.height * BaldurShellScale;
            _baldurShellMesh.Col = MTRX.ColWhite;
            _baldurShellMesh.initForImgAndTexture(tex);
            _baldurShellMesh.uv_top = 0f;
            _baldurShellMesh.uv_height = 1f;
            _baldurShellMesh.uv_left = 0f;
            _baldurShellMesh.uv_width = 1f;
            // 渲染位置降低 0.2 格；叠 3 层绘制让壳更实（透明度叠加）
            float sy = -0.2f * _mp.CLEN;
            _baldurShellMesh.Rect(0f, sy, w, h, false);
            _baldurShellMesh.Rect(0f, sy, w, h, false);
            _baldurShellMesh.Rect(0f, sy, w, h, false);
            MdOut = _baldurShellMesh;
            return true;
        }

        /// <summary>
        /// 程序化生成径向渐变光晕贴图（中心白、边缘透明），供亡者之怒身后红闪使用。
        /// </summary>
        internal static Texture2D MakeRadialGlowTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var cols = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // 中心红色范围加大：d≤0.4 全亮，之后平滑衰减到边缘
                    float a = d <= 0.4f ? 1f : Mathf.Clamp01((1f - d) / 0.6f);
                    a = a * a;
                    cols[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成法阵实心圆贴图：半径内中心透明度 100% → 边缘 50%，半径外全透明。
        /// </summary>
        internal static Texture2D MakeShelterCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var cols = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // 平滑曲线：中心 90% → 边缘 30%；半径外全透明
                    float a = d <= 1f
                        ? 0.3f + 0.6f * Mathf.Pow(Mathf.Clamp01(1f - d), 1.5f)
                        : 0f;
                    cols[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成球体贴图：中心纯白 → 边缘深蓝，透明度中心 100% → 边缘 0%（平滑曲线）。
        /// </summary>
        internal static Texture2D MakeShelterSphereTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var cols = new Color[size * size];
            Color deep = new Color(0.1f, 0.18f, 0.75f);
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = d <= 1f
                        ? Mathf.Pow(Mathf.Clamp01(1f - d), 1.5f)
                        : 0f;
                    // 中心纯白，向边缘渐变成深蓝（蓝色在边缘更明显）
                    float bt = Mathf.Clamp01(d);
                    bt = bt * bt * bt;
                    Color cc = Color.Lerp(Color.white, deep, bt);
                    cols[y * size + x] = new Color(cc.r, cc.g, cc.b, a);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成纯白 2x2 贴图（多边形光翼的底色绑定用）。
        /// </summary>
        private static Texture2D MakeWhiteTexture()
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Color c = Color.white;
            tex.SetPixels(new[] { c, c, c, c });
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成白色小弯月牙（光点形状 0）。
        /// </summary>
        private static Texture2D MakeDotCrescentTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.42f;
            float rr = r * 0.78f;
            Vector2 c1 = new Vector2(size * 0.5f, size * 0.5f);
            Vector2 c2 = new Vector2(size * 0.63f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float a1 = Mathf.Clamp01((r - Vector2.Distance(p, c1)) / (r * 0.3f));
                    float a2 = Mathf.Clamp01((rr - Vector2.Distance(p, c2)) / (rr * 0.3f));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a1 - a2) * 0.9f));
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成白色小圆点（光点形状 1）。
        /// </summary>
        private static Texture2D MakeDotDotTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.38f;
            Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                    float a = Mathf.Clamp01((r - d) / (r * 0.35f));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * 0.9f));
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 程序化生成白色小长条（光点形状 2）：两端柔边。
        /// </summary>
        private static Texture2D MakeDotStripTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            float halfH = h * 0.5f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float cx = x + 0.5f;
                    float cy = y + 0.5f;
                    float edge = Mathf.Clamp01(Mathf.Min(w - cx, cx) / (w * 0.18f));
                    float mid = Mathf.Clamp01(1f - Mathf.Abs(cy - halfH) / (halfH * 0.7f));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, edge * mid * 0.9f));
                }
            }
            tex.Apply();
            return tex;
        }

        private bool KnightPrepareRechargeMesh(Camera Cam, M2RenderTicket Tk, bool need_redraw, int draw_id,
            out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || _rcMesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }

            // 先清空；不活跃时返回 true（空网格）保持票据存活
            _rcMesh.clearSimple();
            if (_rcTex == null || _shadowRechargeTimer <= 0f)
            {
                MdOut = _rcMesh;
                return true;
            }

            // 充能特效跟随骑士当前位置
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(mx, my, 0f));

            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            // 充能特效比骑士略大一圈（1.2 倍），并上移 12 像素
            float rcScale = scale * 1.2f;
            float w = _rcTex.width * rcScale;
            float h = _rcTex.height * rcScale;
            float offsetY = -h / 2f + 19f;
            if (_rcSpriteName == "shadow_recharge0017")
            {
                offsetY -= 30f;
            }
            else if (_rcSpriteName == "shadow_recharge0018")
            {
                offsetY -= 12f;
            }

            _rcMesh.Col = new Color(0f, 0f, 0f, 1f);
            _rcMesh.initForImgAndTexture(_rcTex);
            _rcMesh.uv_top = 0f;
            _rcMesh.uv_height = 1f;
            if (_faceDir < 0f)
            {
                _rcMesh.uv_left = 1f;
                _rcMesh.uv_width = -1f;
            }
            _rcMesh.Rect(0f, offsetY, w, h, false);

            MdOut = _rcMesh;
            return true;
        }

        private bool KnightPrepareGhostNode(GhostNode node, Camera Cam, M2RenderTicket Tk, bool need_redraw,
            int draw_id, out MeshDrawer MdOut, ref bool color_one_overwrite)
        {
            MdOut = null;
            if (_mp == null || node == null || node.Mesh == null)
            {
                return false;
            }
            if (draw_id != 0)
            {
                return false;
            }

            // 先清空；没有残影时返回 true（空网格）保持票据存活
            node.Mesh.clearSimple();
            if (node.Ghost == null)
            {
                MdOut = node.Mesh;
                return true;
            }

            TrailGhost g = node.Ghost;
            int frame = GhostTrailFrame(g);
            string sprite = GhostTrailSprite(frame);
            if (!_textures.TryGetValue(sprite, out Texture2D tex))
            {
                MdOut = node.Mesh;
                return true;
            }
            // 淡入：残影生成后 0.03s 内从 0 淡入到 1，避免硬弹出
            float alpha = Mathf.Clamp01(g.Age / GhostFadeInTime);
            if (_ghostFading)
            {
                // 淡出阶段：第1帧先消失，逐帧跟上，GhostLife 内全部消失
                float step = _ghostFadeTotal > 0 ? GhostLife / _ghostFadeTotal : GhostLife;
                float fadeOut = 1f - (_ghostFadeTimer - g.Index * step) / step;
                alpha = Mathf.Min(alpha, Mathf.Clamp01(fadeOut));
            }
            if (alpha < 0.04f)
            {
                MdOut = node.Mesh;
                return true;
            }

            // 方案A：残影渲染在各自的固定锚点（冲刺起点反方向偏移处），
            // 帧号只决定该残影的形态；锚点固定不跟随骑士，无跳变无闪烁
            float renderX = g.X;
            float gx = _mp.pixel2ux(renderX * _mp.CLEN);
            float gy = _mp.pixel2uy(g.Y * _mp.CLEN);
            Tk.Matrix = _mp.gameObject.transform.localToWorldMatrix *
                        Matrix4x4.Translate(new Vector3(gx, gy, 0f));

            float scale = KnightInCradlePlugin.ScaleConfig != null
                ? KnightInCradlePlugin.ScaleConfig.Value
                : 0.42f;
            // 从第3帧(0002)起每帧比上一帧放大一点：0000/0001 基准大小，0002 起逐帧 +6%
            float frameGrow = 1f + Mathf.Max(0, frame - 1) * GhostFrameGrowth;
            float w = tex.width * scale * frameGrow;
            float h = tex.height * scale * frameGrow;
            float offsetY = -h / 2f + 7f;

            node.Mesh.Col = new Color(0f, 0f, 0f, alpha);
            node.Mesh.initForImgAndTexture(tex);
            node.Mesh.uv_top = 0f;
            node.Mesh.uv_height = 1f;
            if (_faceDir < 0f)
            {
                node.Mesh.uv_left = 1f;
                node.Mesh.uv_width = -1f;
            }
            // 用 _dashDir==0 判断向下冲刺：冲刺结束后残影淡出阶段 _dashing 已为 false，
            // 但 _dashDir 会保持 0 直到下一次冲刺，保证淡出的最后几帧残影也保持旋转
            if (_dashDir == 0f)
            {
                // 向下冲刺：暗影残影与本体一样绕中心旋转 90°
                //（面朝左逆时针、面朝右顺时针；矩阵平移用网格单位，ppu=64）
                float rotR = _faceDir > 0f ? 1.5707964f : -1.5707964f; // ±π/2
                float cy = offsetY / 64f;
                Matrix4x4 savedM = node.Mesh.getCurrentMatrix();
                node.Mesh.Translate(0f, cy, true);
                node.Mesh.Rotate(rotR, true);
                node.Mesh.Translate(0f, -cy, true);
                // 叠 GhostLayers 层，让残影更明显（避免发白看不清）
                for (int li = 0; li < GhostLayers; li++)
                {
                    node.Mesh.Rect(0f, offsetY, w, h, false);
                }
                node.Mesh.setCurrentMatrix(savedM, false);
            }
            else
            {
                // 叠 GhostLayers 层，让残影更明显（避免发白看不清）
                for (int li = 0; li < GhostLayers; li++)
                {
                    node.Mesh.Rect(0f, offsetY, w, h, false);
                }
            }

            MdOut = node.Mesh;
            return true;
        }

        /// <summary>
        /// 拖尾残影帧：按生成序号循环（0000, 0001, 0002, ...），
        /// 每道残影只负责展示对应序号的形态；位置由生成时的世界坐标决定。
        /// </summary>
        private int GhostTrailFrame(TrailGhost g)
        {
            if (!_clips.TryGetValue("ShadowDashTrail", out ClipData clip) || clip.frames.Length == 0)
            {
                return 0;
            }
            // 固定帧号：生成时即分配，最新两道为 0000/0001（第1、2帧）
            return g.Frame % clip.frames.Length;
        }

        private string GhostTrailSprite(int index)
        {
            if (_clips.TryGetValue("ShadowDashTrail", out ClipData clip) && clip.frames.Length > 0)
            {
                return clip.frames[index % clip.frames.Length];
            }
            return "shadow_dash_trail0000";
        }

        private void EnsureMaterial()
        {
            if (_mat != null)
            {
                return;
            }
            if (MTRX.ShaderGDT != null)
            {
                _mat = MTRX.newMtr(MTRX.ShaderGDT);
                _mat.EnableKeyword("NO_PIXELSNAP");
            }
        }

        // ---------- 素材 ----------

        private void LoadAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
                string manifestPath = Path.Combine(dir, "knight_manifest.json");
                string spriteDir = Path.Combine(dir, "sprites");

                if (!File.Exists(manifestPath) || !Directory.Exists(spriteDir))
                {
                    return;
                }

                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                var wantedClips = new HashSet<string>
                {
                    "Idle", "Run", "Airborne", "Double Jump", "Wall Slide", "Walljump", "Dash", "Slash", "SlashAlt", "UpSlash", "DownSlash",
                    // 平砍剑气两组（Attack/SlashEffect=0000~0002，AttackAlt/SlashEffectAlt=0004~0006）
                    "SlashEffect", "SlashEffectAlt", "UpSlashEffect", "DownSlashEffect",
                    // 修长之钉/骄傲印记：螳螂爪样式剑气（mantis_* 帧）
                    "SlashEffect M", "SlashEffectAlt M", "UpSlashEffect M", "DownSlashEffect M",
                    // 亡者之怒：rage 版剑气特效（1 血时切换）
                    "SlashEffect F", "SlashEffectAlt F", "UpSlashEffect F", "DownSlashEffect F",
                    "Land", "Run To Idle", "Turn", "LookUp", "LookDown",
                    "LookUpEnd", "LookDownEnd",
                    // 坐长椅动画：原版小骑士没有 “Bench/BenchEnd”，对应 HK 的 “Sit / Get Off”，
                    // 坐姿循环用 “Sit Idle”；剪辑缺失时 PlayClip 回退 Idle。
                    "Sit", "Sit Idle", "Get Off", "Sit Lean", "Wake", "Wake To Sit",
                    "Sitting Asleep", "Sit Fall Asleep",
                    // 挑衅（V 键）：开场 / 收尾剪辑（manifest 内 0002~0004 用 0001 重复补帧）
                    "Challenge Start", "Challenge End",
                    // 受伤硬直（Recoil / stun 帧）、死亡动画（Death / death_anim 帧）、
                    // 低血量待机（Idle Hurt / idle_low_health 帧）
                    "Recoil", "Death", "Idle Hurt",
                    // 复活后在长椅上苏醒（Wake To Sit）
                    "Wake To Sit",
                    // 凝聚回血：俯身/消耗（Focus）、回血爆发（Focus Get Once）、起身硬直（Focus End）
                    "Focus", "Focus Get Once", "Focus End",
                    // 水晶之心（超级冲刺）：地面/墙上蓄力、飞行、刹车、起身，水晶成长/闪光/收缩，爆发/拖尾/碎裂特效
                    "SD Charge Ground", "SD Wall Charge", "SD Dash", "SD Air Brake", "SD Charge Ground End",
                    "SD Crys Grow", "SD Crys Idle", "SD Crys Flash", "SD Crys Shrink",
                    "SD Fx Burst", "SD Trail", "SD Trail End", "SD Break",
                    // 暗影之魂（法术）：施法、火球投射物、爆炸特效
                    "Fireball Cast", "Fireball Projectile", "Fireball Blast",
                    // 黑暗降临（下砸）：前摇、下落、落地
                    "Dive Antic", "Dive Fall", "Dive Land",
                    // 提灯小骑士（黑暗区域）：待机 / 行走
                    "Lantern Idle", "Lantern Run"
                };
                var wantedSprites = new HashSet<string>();
                foreach (JToken c in (JArray)manifest["clips"])
                {
                    if (!wantedClips.Contains((string)c["name"]))
                    {
                        continue;
                    }
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        wantedSprites.Add((string)f);
                    }
                }

                foreach (KeyValuePair<string, JToken> kv in (JObject)manifest["sprites"])
                {
                    if (!wantedSprites.Contains(kv.Key))
                    {
                        continue;
                    }
                    string png = Path.Combine(spriteDir, Sanitize(kv.Key) + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[kv.Key] = tex;
                    if (kv.Value["feet"] != null)
                    {
                        _feetFraction[kv.Key] = (float)kv.Value["feet"];
                    }
                }

                foreach (JToken c in (JArray)manifest["clips"])
                {
                    var frames = new List<string>();
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        string name = (string)f;
                        if (_textures.ContainsKey(name))
                        {
                            frames.Add(name);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[(string)c["name"]] = new ClipData
                    {
                        fps = (float)c["fps"],
                        wrapMode = (int)c["wrapMode"],
                        loopStart = (int)c["loopStart"],
                        frames = frames.ToArray()
                    };
                }

                // 攻击剪辑：取原版 Slash / SlashAlt 前 8 帧，按 SlashClipFps（≈28.57fps）播放，
                // 8 帧动画正好在 0.35s 普攻时长/冷却内播完；两套帧交替播放实现“右挥→左挥”的交替感。
                // 上劈/下劈同样 8 帧、同一帧率，三向普攻节奏一致。
                BuildAttackClip("Slash", "Attack", SlashClipFps);
                BuildAttackClip("SlashAlt", "AttackAlt", SlashClipFps);
                BuildAttackClip("UpSlash", "UpSlash", SlashClipFps, 8);
                BuildAttackClip("DownSlash", "DownSlash", SlashClipFps, 8);
                // 右墙爬墙剪辑：wall_slide 的左右镜像帧（wall_slide_right0000~0003）
                BuildWallSlideRightClip();

                // 三个暗影冲刺特效从 sheets 的排序文件夹加载（帧顺序=数字从小到大）
                LoadShadowClipSet("shadow_dash", "ShadowDash");
                LoadShadowClipSet("shadow_dash_trail", "ShadowDashTrail");
                LoadShadowClipSet("shadow_recharge", "ShadowRecharge");
                // 虚空解放（梦之门技能）：前摇/出伤/后摇动画帧
                LoadVoidLiberationAssets();
                // 低血量待机：只取 idle_low_health 帧做干净循环（manifest 的 Idle Hurt 前两帧是 collect 过渡）
                BuildLowHpIdleClip();
                // 凝聚剪辑修复：manifest 的 wrapMode=1（loopStart=2/6）会被 EffectiveIndex 直接从
                // loopStart 开始循环，跳过“站立→低头”的发动帧，导致一按凝聚就跳到跪姿。
                // 改为 wrapMode=4：先播发动帧一次，再循环 loopStart 之后的保持帧。
                if (_clips.TryGetValue("Focus", out ClipData focusClip) && focusClip.wrapMode == 1)
                {
                    focusClip.wrapMode = 4;
                }
                if (_clips.TryGetValue("Focus Get", out ClipData focusGetClip) && focusGetClip.wrapMode == 1)
                {
                    focusGetClip.wrapMode = 4;
                }
                // 超级冲刺剪辑同样需要“先播发动帧一次，再循环保持帧”的语义，
                // 否则蓄力/拖尾会直接从循环段开始，跳过蓄力下蹲与拖尾出现帧
                if (_clips.TryGetValue("SD Charge Ground", out ClipData sdCg) && sdCg.wrapMode == 1)
                {
                    sdCg.wrapMode = 4;
                }
                if (_clips.TryGetValue("SD Wall Charge", out ClipData sdWc) && sdWc.wrapMode == 1)
                {
                    sdWc.wrapMode = 4;
                }
                if (_clips.TryGetValue("SD Trail", out ClipData sdTr) && sdTr.wrapMode == 1)
                {
                    sdTr.wrapMode = 4;
                }
                // 超级冲刺飞行本体：只播 superdash0013 / superdash0014 两帧循环
                if (_textures.ContainsKey("superdash0013") && _textures.ContainsKey("superdash0014"))
                {
                    _clips["SuperDashBody"] = new ClipData
                    {
                        fps = 20f,
                        wrapMode = 0,
                        loopStart = 0,
                        frames = new[] { "superdash0013", "superdash0014" }
                    };
                }
                // 水晶升腾（向上超级冲刺）本体：只播 superdash0013 一帧，逆时针旋转 90°
                if (_textures.TryGetValue("superdash0013", out Texture2D sd13Tex))
                {
                    int sd13W = sd13Tex.width;
                    int sd13H = sd13Tex.height;
                    var rotTex = new Texture2D(sd13H, sd13W, TextureFormat.RGBA32, false)
                    {
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    Color32[] srcPx = sd13Tex.GetPixels32();
                    var rotPx = new Color32[sd13H * sd13W];
                    for (int yy = 0; yy < sd13W; yy++)
                    {
                        for (int xx = 0; xx < sd13H; xx++)
                        {
                            // 逆时针 90°：目标(xx,yy) ← 源(sx=w-1-yy, sy=xx)
                            rotPx[yy * sd13H + xx] = srcPx[xx * sd13W + (sd13W - 1 - yy)];
                        }
                    }
                    rotTex.SetPixels32(rotPx);
                    rotTex.Apply();
                    _textures["superdash0013_rot"] = rotTex;
                    _clips["SuperDashUp"] = new ClipData
                    {
                        fps = 20f,
                        wrapMode = 0,
                        loopStart = 0,
                        frames = new[] { "superdash0013_rot" }
                    };
                }
                // 暗影之魂施法动画：9 帧 @30fps = 0.3s，与滞空时长一致
                if (_clips.TryGetValue("Fireball Cast", out ClipData fireballCastClip))
                {
                    fireballCastClip.fps = 30f;
                }
                // 空中刹车（官方设定）：超级冲刺停下后的惯性滑行动画只取 air_break0000~0004，
                // 去掉 manifest 里混入的 jump_04~06 帧，保证停下瞬间就是刹车姿态
                if (_textures.ContainsKey("air_break0000"))
                {
                    var abFrames = new List<string>();
                    for (int ab = 0; ab < 5; ab++)
                    {
                        string abn = "air_break" + ab.ToString("D4");
                        if (_textures.ContainsKey(abn))
                        {
                            abFrames.Add(abn);
                        }
                    }
                    if (abFrames.Count > 0)
                    {
                        _clips["SD Air Brake"] = new ClipData
                        {
                            fps = 15f,
                            wrapMode = 2,
                            loopStart = 0,
                            frames = abFrames.ToArray()
                        };
                    }
                }
                // 后续凝聚循环：直接从跪姿 0002 开始循环（不重播站立→低头的发动帧），
                // 连续回血时小骑士始终保持低头凝聚，只在回血瞬间播爆发帧
                if (_textures.ContainsKey("focus_v020002") && _textures.ContainsKey("focus_v020006"))
                {
                    _clips["Focus Loop"] = new ClipData
                    {
                        fps = 12f,
                        wrapMode = 4,
                        loopStart = 0,
                        frames = new[]
                        {
                            "focus_v020002", "focus_v020003", "focus_v020004", "focus_v020005", "focus_v020006"
                        }
                    };
                }
                // 超级冲刺蓄力水晶：8 个碎片合成图集（逐帧向两侧对称出现）
                BuildSuperCrystalAtlas();
                // 超级冲刺撞墙停顿动画（superdash_wall_hit0000~0002，manifest 无对应剪辑，直接按名加载）
                for (int i = 0; i < 3; i++)
                {
                    string n = "superdash_wall_hit" + i.ToString("D4");
                    if (_textures.ContainsKey(n))
                    {
                        continue;
                    }
                    string png = Path.Combine(spriteDir, n + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        _textures[n] = tex;
                    }
                }
                if (_textures.ContainsKey("superdash_wall_hit0000"))
                {
                    _clips["SuperWallHit"] = new ClipData
                    {
                        fps = 30f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = new[]
                        {
                            "superdash_wall_hit0000", "superdash_wall_hit0001", "superdash_wall_hit0002"
                        }
                    };
                }
                // 骨剑 / 尖刺：下砸落地冲击波素材（不在任何剪辑中，直接按名加载）
                LoadStandaloneSprite("nail_upgrade_0000_pure-nail");
                LoadStandaloneSprite("white_spikes0000");
                // 深渊尖啸：身体施法 / 上升冲击波 / 地面底座（不在任何剪辑中，直接按名加载）
                LoadScreamClips();
                // 游泳动画：入水过渡 / 液面待机 / 液面游动
                LoadWaterClips();
                // 骨钉技艺·强力劈砍：蓄力/发光/劈砍动作/剑气
                LoadNailArtClips();
                // 护符22 巴尔德之壳：出现/受击/破碎/收起动画
                LoadBaldurShellAssets();
                // 护符23 吸虫之巢：黑色吸虫飞行/扑腾动画
                LoadFlukeAssets();
                // 护符25 发光子宫：幼体出生/飞行/爆炸动画
                LoadUterusAssets();
                // 护符25 发光子宫：幼体碰撞爆炸特效（explode_particle 帧）
                LoadUterusExplosionAssets();
                // 护符34 乌恩之形：乌恩蛞蝓形态素材
                LoadUnnAssets();
                // 羁绊：乌恩之形 + 蘑菇孢子 —— 蘑菇蛞蝓形态素材
                LoadMushUnnAssets();
                // 护符36 编织者之歌：小编织者素材
                LoadWeaverAssets();
                // 护符38 梦之盾：盾牌贴图
                LoadDreamShieldAssets();
                // 护符39 格林之子：小格林出现/待机/飞行/睡眠/传送动画
                LoadGrimmAssets();
                // 凝聚三阶段剪辑：发动（0000~0002，0.27s）/ 回血（0003~0006，0.82s）
                BuildFocusPhaseClips();
                // 挑衅（V 键）：开场 0000~0008 → 循环 3 次 0009~0010 → 收尾 0010~0017
                BuildChallengeClip();
                // 右墙镜像变体：撞墙停顿（SuperWallHit Right）与墙上蓄力（SD Wall Charge Right）
                BuildRightVariantClip("SuperWallHit Right", "SuperWallHit", "_right");
                BuildRightVariantClip("SD Wall Charge Right", "SD Wall Charge", "_right");
                _assetsLoaded = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>加载不在任何剪辑中的独立贴图（按 sprites 目录下的 png 名加载）。</summary>
        private void LoadStandaloneSprite(string name)
        {
            try
            {
                if (_textures.ContainsKey(name))
                {
                    return;
                }
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
                string png = Path.Combine(dir, "sprites", name + ".png");
                if (!File.Exists(png))
                {
                    return;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                {
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符22 巴尔德之壳素材：读取 sheets/baldur/baldur_shell_manifest.json，
        /// 加载 Appear/Impact/Break/Crack/Disappear 剪辑（命名为 BaldurXxx）与贴图。
        /// </summary>
        private void LoadBaldurShellAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "baldur");
                string manifestPath = Path.Combine(dir, "baldur_shell_manifest.json");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!File.Exists(manifestPath) || !Directory.Exists(spriteDir))
                {
                    return;
                }
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JArray clips = (JArray)manifest["clips"];
                var clipNames = new Dictionary<string, string>
                {
                    ["Appear"] = "BaldurAppear",
                    ["Impact"] = "BaldurImpact",
                    ["Break"] = "BaldurBreak",
                    ["Crack"] = "BaldurCrack",
                    ["Disappear"] = "BaldurDisappear"
                };
                var wantedSprites = new HashSet<string>();
                foreach (JToken c in clips)
                {
                    if (!clipNames.ContainsKey((string)c["name"]))
                    {
                        continue;
                    }
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        wantedSprites.Add((string)f);
                    }
                }
                foreach (string name in wantedSprites)
                {
                    string png = Path.Combine(spriteDir, name + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                foreach (JToken c in clips)
                {
                    string clipName = (string)c["name"];
                    if (!clipNames.TryGetValue(clipName, out string key))
                    {
                        continue;
                    }
                    var frames = new List<string>();
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        string nm = (string)f;
                        if (_textures.ContainsKey(nm))
                        {
                            frames.Add(nm);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[key] = new ClipData
                    {
                        fps = (float)c["fps"],
                        wrapMode = (int)c["wrapMode"],
                        loopStart = (int)c["loopStart"],
                        frames = frames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符23 吸虫之巢素材：读取 sheets/nest/fluke_manifest.json，
        /// 注册黑色吸虫飞行（FlukeAir）与地面扑腾（FlukeFlop）剪辑。
        /// </summary>
        private void LoadFlukeAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "nest");
                string manifestPath = Path.Combine(dir, "fluke_manifest.json");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!File.Exists(manifestPath) || !Directory.Exists(spriteDir))
                {
                    return;
                }
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JArray clips = (JArray)manifest["clips"];
                var clipNames = new Dictionary<string, string>
                {
                    ["Air Black"] = "FlukeAir",
                    ["Flop Black"] = "FlukeFlop"
                };
                var wantedSprites = new HashSet<string>();
                foreach (JToken c in clips)
                {
                    if (!clipNames.ContainsKey((string)c["name"]))
                    {
                        continue;
                    }
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        wantedSprites.Add((string)f);
                    }
                }
                foreach (string name in wantedSprites)
                {
                    string png = Path.Combine(spriteDir, name + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                foreach (JToken c in clips)
                {
                    string clipName = (string)c["name"];
                    if (!clipNames.TryGetValue(clipName, out string key))
                    {
                        continue;
                    }
                    var frames = new List<string>();
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        string nm = (string)f;
                        if (_textures.ContainsKey(nm))
                        {
                            frames.Add(nm);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[key] = new ClipData
                    {
                        fps = (float)c["fps"],
                        wrapMode = (int)c["wrapMode"],
                        loopStart = (int)c["loopStart"],
                        frames = frames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符25 发光子宫素材：读取 sheets/uterus/hatchling_manifest.json，
        /// 注册幼体出生（UterusHatch）、飞行（UterusFly）、冲锋（UterusAttack）、爆炸（UterusBurst）。
        /// </summary>
        private void LoadUterusAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "uterus");
                string manifestPath = Path.Combine(dir, "hatchling_manifest.json");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!File.Exists(manifestPath) || !Directory.Exists(spriteDir))
                {
                    return;
                }
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JArray clips = (JArray)manifest["clips"];
                var clipNames = new Dictionary<string, string>
                {
                    ["Hatch"] = "UterusHatch",
                    ["Fly"] = "UterusFly",
                    ["Attack"] = "UterusAttack",
                    ["Burst"] = "UterusBurst"
                };
                var wantedSprites = new HashSet<string>();
                foreach (JToken c in clips)
                {
                    if (!clipNames.ContainsKey((string)c["name"]))
                    {
                        continue;
                    }
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        wantedSprites.Add((string)f);
                    }
                }
                // 坐椅子睡眠帧：hatchling_sleep0000~0005（manifest 的 Rest Start 过长，自定义剪辑）
                for (int i = 0; i < 6; i++)
                {
                    wantedSprites.Add("hatchling_sleep000" + i);
                }
                foreach (string name in wantedSprites)
                {
                    string png = Path.Combine(spriteDir, name + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                foreach (JToken c in clips)
                {
                    string clipName = (string)c["name"];
                    if (!clipNames.TryGetValue(clipName, out string key))
                    {
                        continue;
                    }
                    var frames = new List<string>();
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        string nm = (string)f;
                        if (_textures.ContainsKey(nm))
                        {
                            frames.Add(nm);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[key] = new ClipData
                    {
                        fps = (float)c["fps"],
                        wrapMode = (int)c["wrapMode"],
                        loopStart = (int)c["loopStart"],
                        frames = frames.ToArray()
                    };
                }
                // 幼体镜像帧（面朝右）：hatch_fly0000 - l ~ 0005 - l（用户提供，不在 manifest 中）
                var flyL = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    string nm = "hatch_fly000" + i + " - l";
                    string png = Path.Combine(spriteDir, nm + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[nm] = tex;
                    flyL.Add(nm);
                }
                if (flyL.Count > 0)
                {
                    _clips["UterusFlyL"] = new ClipData
                    {
                        fps = 12f,
                        wrapMode = 0,
                        loopStart = 0,
                        frames = flyL.ToArray()
                    };
                }
                // 坐椅子睡眠剪辑：hatchling_sleep0000~0005，12fps 播一遍后停最后一帧（静止）
                var restFrames = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    string nm = "hatchling_sleep000" + i;
                    if (_textures.ContainsKey(nm))
                    {
                        restFrames.Add(nm);
                    }
                }
                if (restFrames.Count == 6)
                {
                    _clips["UterusRest"] = new ClipData
                    {
                        fps = 12f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = restFrames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符25 发光子宫：碰撞爆炸特效素材。
        /// 读取 sheets/explosion/sprites 下的 explode_particle0000~0012.png（13 帧，20fps，一次性）。
        /// </summary>
        private void LoadUterusExplosionAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "explosion");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!Directory.Exists(spriteDir))
                {
                    return;
                }
                var frames = new List<string>();
                for (int i = 0; i < 13; i++)
                {
                    string nm = "explode_particle" + i.ToString("D4");
                    string png = Path.Combine(spriteDir, nm + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[nm] = tex;
                    frames.Add(nm);
                }
                if (frames.Count > 0)
                {
                    _clips["UterusExplosion"] = new ClipData
                    {
                        fps = UterusExplosionFps,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = frames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 护符34 乌恩之形：读取 sheets/unn/sprites 的 knight_slug 帧并注册剪辑。
        /// 素材已由作者左右翻转（默认面朝左，与其它小骑士帧一致）。
        /// </summary>
        private void LoadUnnAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "unn_2");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!Directory.Exists(spriteDir))
                {
                    return;
                }
                foreach (string png in Directory.GetFiles(spriteDir, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(png);
                    if (name.IndexOf("knight_slug", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                RegisterUnnClip("UnnTransform",
                    new[] { "transform0000", "transform0001", "transform0002", "transform0003" }, 20f);
                RegisterUnnClip("UnnIdle",
                    new[] { "idle0000", "idle0001", "idle0002", "idle0003", "idle0004" }, 12f);
                RegisterUnnClip("UnnWalk",
                    new[] { "walk0000", "walk0001", "walk0002", "walk0003", "walk0004", "walk0005" }, 12f);
                RegisterUnnClip("UnnBurst",
                    new[] { "focus_burst0000", "focus_burst0001", "focus_burst0002" }, 15f);
                RegisterUnnClip("UnnTransformBack",
                    new[] { "transform_back0000", "transform_back0001", "transform_back0002" }, 15f);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>注册乌恩剪辑：帧名补上 knight_slug_ 前缀。</summary>
        private void RegisterUnnClip(string key, string[] names, float fps)
        {
            var frames = new List<string>();
            foreach (string n in names)
            {
                string full = "knight_slug_" + n;
                if (_textures.ContainsKey(full))
                {
                    frames.Add(full);
                }
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[key] = new ClipData
            {
                fps = fps,
                wrapMode = 0,
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>
        /// 羁绊：乌恩之形 + 蘑菇孢子 —— 读取 sheets/mush_unn/sprites 的 shroom_slug 帧，
        /// 注册蘑菇蛞蝓动画剪辑（MushUnnTransform / MushUnnIdle / MushUnnWalk /
        /// MushUnnBurst / MushUnnTransformBack）。帧序列沿用原版乌恩的节奏。
        /// </summary>
        private void LoadMushUnnAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "mush_unn");
                string spriteDir = Path.Combine(dir, "sprites");
                if (!Directory.Exists(spriteDir))
                {
                    return;
                }
                foreach (string png in Directory.GetFiles(spriteDir, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(png);
                    if (name.IndexOf("shroom_slug", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                RegisterMushUnnClip("MushUnnTransform",
                    new[] { "0000", "0001", "0002", "0003" }, 20f);
                RegisterMushUnnClip("MushUnnIdle",
                    new[] { "0000", "0001", "0002", "0003", "0004" }, 12f);
                RegisterMushUnnClip("MushUnnWalk",
                    new[] { "0005", "0006", "0007", "0008", "0009", "0010" }, 12f);
                RegisterMushUnnClip("MushUnnBurst",
                    new[] { "0014", "0015", "0016", "0000", "0001", "0002", "0003", "0004" }, 15f);
                RegisterMushUnnClip("MushUnnTransformBack",
                    new[] { "0000", "0001", "0002", "0003" }, 15f);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>注册蘑菇乌恩剪辑：帧名补上 shroom_slug_ 前缀。</summary>
        private void RegisterMushUnnClip(string key, string[] names, float fps)
        {
            var frames = new List<string>();
            foreach (string n in names)
            {
                string full = "shroom_slug" + n;
                if (_textures.ContainsKey(full))
                {
                    frames.Add(full);
                }
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[key] = new ClipData
            {
                fps = fps,
                wrapMode = 0,
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>
        /// 护符36 编织者之歌：读取 sheets/spider/sprites 的小编织者帧并注册剪辑。
        /// </summary>
        private void LoadWeaverAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "spider");
                string spriteDir = Path.Combine(dir, "sprites");
                string manifestPath = Path.Combine(dir, "spider_manifest.json");
                if (!Directory.Exists(spriteDir) || !File.Exists(manifestPath))
                {
                    return;
                }
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                foreach (string png in Directory.GetFiles(spriteDir, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(png);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                }
                var clipMap = new Dictionary<string, string>
                {
                    ["Launch"] = "WeaverLaunch",
                    ["Run"] = "WeaverRun",
                    ["Idle"] = "WeaverIdle",
                    ["Attack"] = "WeaverAttack",
                    ["Sleep"] = "WeaverSleep"
                };
                foreach (JToken c in (JArray)manifest["clips"])
                {
                    string clipName = (string)c["name"];
                    if (!clipMap.TryGetValue(clipName, out string key))
                    {
                        continue;
                    }
                    var frames = new List<string>();
                    foreach (JToken f in (JArray)c["frames"])
                    {
                        string nm = (string)f;
                        if (_textures.ContainsKey(nm))
                        {
                            frames.Add(nm);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[key] = new ClipData
                    {
                        fps = (float)c["fps"],
                        wrapMode = (int)c["wrapMode"],
                        loopStart = (int)c["loopStart"],
                        frames = frames.ToArray()
                    };
                }
                // 近战攻击剪辑：Weaver_Charm_spawn_spider0014~0016，3 帧在 0.3s 内播完
                if (_textures.ContainsKey("Weaver_Charm_spawn_spider0014") &&
                    _textures.ContainsKey("Weaver_Charm_spawn_spider0015") &&
                    _textures.ContainsKey("Weaver_Charm_spawn_spider0016"))
                {
                    _clips["WeaverMelee"] = new ClipData
                    {
                        fps = 10f,
                        wrapMode = 0,
                        loopStart = 0,
                        frames = new[]
                        {
                            "Weaver_Charm_spawn_spider0014",
                            "Weaver_Charm_spawn_spider0015",
                            "Weaver_Charm_spawn_spider0016"
                        }
                    };
                }
                // 休息苏醒剪辑：spawn_spider_sleep0003 → 0001（反向，不含 0000，播完恢复跟随）
                var wakeFrames = new List<string>();
                for (int i = 3; i >= 1; i--)
                {
                    string nm = "spawn_spider_sleep000" + i;
                    if (_textures.ContainsKey(nm))
                    {
                        wakeFrames.Add(nm);
                    }
                }
                if (wakeFrames.Count > 0)
                {
                    _clips["WeaverWake"] = new ClipData
                    {
                        fps = 12f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = wakeFrames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>加载深渊尖啸 3 组帧（身体施法 / 上升冲击波 / 地面底座）并注册剪辑，帧率按 0.62s 播完。</summary>
        private void LoadScreamClips()
        {
            try
            {
                LoadScreamGroup("Scream Cast", "scream_cast_lvl_02", 9);
                LoadScreamGroup("Scream Blast", "scream_blast_level_02", 13);
            }
            catch (Exception)
            {
            }
        }

        private void LoadScreamGroup(string clipName, string prefix, int count)
        {
            string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
            string spriteDir = Path.Combine(dir, "sprites");
            var frames = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string n = prefix + i.ToString("D4");
                if (!_textures.ContainsKey(n))
                {
                    string png = Path.Combine(spriteDir, n + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[n] = tex;
                }
                frames.Add(n);
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[clipName] = new ClipData
            {
                fps = frames.Count / ScreamAnimTime,
                wrapMode = 2, // 一次性播完
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>加载游泳 3 组帧（入水过渡 / 液面待机 / 液面游动），统一 20fps。</summary>
        private void LoadWaterClips()
        {
            try
            {
                LoadWaterGroup("Water Enter", "water_enter_to_idle", 7);
                LoadWaterGroup("Water Surface Idle", "water_surface_idle", 6);
                LoadWaterGroup("Water Surface Swim", "water_surface_swim", 5);
            }
            catch (Exception)
            {
            }
        }

        private void LoadWaterGroup(string clipName, string prefix, int count)
        {
            string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
            string spriteDir = Path.Combine(dir, "sprites");
            var frames = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string n = prefix + i.ToString("D4");
                if (!_textures.ContainsKey(n))
                {
                    string png = Path.Combine(spriteDir, n + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[n] = tex;
                }
                frames.Add(n);
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[clipName] = new ClipData
            {
                fps = 20f,
                wrapMode = 1,
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>加载强力劈砍 4 组帧（蓄力动作 / 劈砍动作 / 剑气 / 蓄满发光）。</summary>
        private void LoadNailArtClips()
        {
            try
            {
                // 劈砍动作：6 帧按 20fps 加速播放（0.3s 播完，剩余时间保持收势）
                LoadArtGroup("Nail Art Slash", "charge_slash", 6, 20f, 2);
                // 剑气：一次性播完
                LoadArtGroup("Nail Art Slash Effect", "charge_slash_effect", 9, 20f, 2);
                // 蓄满发光：循环
                LoadArtGroup("Nail Art Glow", "nail_charge_effect", 10, 20f, 1);
                // 冲刺劈砍：动作（dash_slash0002~0006）+ 剑气（dash_slash_effect0002~0008，跳过空白 0000/0001）
                LoadArtGroup("Dash Slash", "dash_slash", 7, 20f, 2);
                LoadArtGroup("Dash Slash Effect", "dash_slash_effect", 9, 20f, 2);
                if (_clips.TryGetValue("Dash Slash Effect", out ClipData dse) && dse.frames.Length > 0)
                {
                    var dseFrames = new List<string>();
                    for (int i = 0; i < dse.frames.Length; i++)
                    {
                        string fn = dse.frames[i];
                        if (!fn.EndsWith("0000") && !fn.EndsWith("0001"))
                        {
                            dseFrames.Add(fn);
                        }
                    }
                    if (dseFrames.Count > 0)
                    {
                        _clips["Dash Slash Effect"] = new ClipData
                        {
                            fps = 20f,
                            wrapMode = 2,
                            loopStart = 0,
                            frames = dseFrames.ToArray()
                        };
                    }
                }
                // 旋风劈砍：动作（cyclone_slash0000~0010，循环）+ 剑气（cyclone_slash_effect0003~0008，跳过空白 0000）
                LoadArtGroup("Cyclone Slash", "cyclone_slash", 11, 20f, 0);
                LoadArtGroup("Cyclone Effect", "cyclone_slash_effect", 9, 20f, 0);
                if (_clips.TryGetValue("Cyclone Effect", out ClipData ce) && ce.frames.Length > 0)
                {
                    var ceFrames = new List<string>();
                    for (int i = 0; i < ce.frames.Length; i++)
                    {
                        if (!ce.frames[i].EndsWith("0000"))
                        {
                            ceFrames.Add(ce.frames[i]);
                        }
                    }
                    if (ceFrames.Count > 0)
                    {
                        _clips["Cyclone Effect"] = new ClipData
                        {
                            fps = 20f,
                            wrapMode = 0,
                            loopStart = 0,
                            frames = ceFrames.ToArray()
                        };
                    }
                }
                // 梦钉：前摇（nail_slash0000~0015）+ 抽出（nail_slash0016~0025）
                // 前摇三段式（0~0.4s 播 0000~0005，0.4~0.8s 播 0006~0010，0.8~1.2s 播 0011~0015），
                // 帧推进由 PlayDreamNailChargeAnim 按时间精确驱动（此 clip 仅作兜底）。
                LoadArtGroup("Dream Nail Charge", "nail_slash", 16, 16f / DreamNailWindupTime, 2);
                var dncFrames = new List<string>();
                for (int i = 0; i <= 15; i++)
                {
                    dncFrames.Add("nail_slash" + i.ToString("D4"));
                }
                if (dncFrames.Count > 0)
                {
                    _clips["Dream Nail Charge"] = new ClipData
                    {
                        fps = 16f / DreamNailWindupTime,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = dncFrames.ToArray()
                    };
                }
                LoadArtGroup("Dream Nail Swing", "nail_slash", 26, 24f, 2);
                if (_clips.TryGetValue("Dream Nail Swing", out ClipData dnsw) && dnsw.frames.Length > 16)
                {
                    _clips["Dream Nail Swing"] = new ClipData
                    {
                        fps = 24f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = dnsw.frames.Skip(16).ToArray()
                    };
                }
                BuildNailSlashCenterOffsets();
            }
            catch (Exception)
            {
            }
        }

        private void LoadArtGroup(string clipName, string prefix, int count, float fps, int wrapMode)
        {
            string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
            string spriteDir = Path.Combine(dir, "sprites");
            var frames = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string n = prefix + i.ToString("D4");
                if (!_textures.ContainsKey(n))
                {
                    string png = Path.Combine(spriteDir, n + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[n] = tex;
                }
                frames.Add(n);
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[clipName] = new ClipData
            {
                fps = fps,
                wrapMode = wrapMode,
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>护符38 梦之盾素材：读取 sheets/shield/dreamshield.png 单张贴图。</summary>
        private void LoadDreamShieldAssets()
        {
            try
            {
                string png = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "shield", "dreamshield.png");
                if (!File.Exists(png))
                {
                    return;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                {
                    return;
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                _textures["dreamshield"] = tex;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>护符39 格林之子素材：读取 sheets/grimm 下的 Grimmbat_* 帧并注册剪辑。</summary>
        private void LoadGrimmAssets()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "grimm");
                if (!Directory.Exists(dir))
                {
                    return;
                }
                var wanted = new Dictionary<string, string[]>
                {
                    ["GrimmAppear"] = new[] { "Grimmbat_turn0000", "Grimmbat_turn0001" },
                    ["GrimmIdle"] = new[]
                    {
                        "Grimmbat_idle0000", "Grimmbat_idle0001", "Grimmbat_idle0002",
                        "Grimmbat_idle0003", "Grimmbat_idle0004", "Grimmbat_idle0005"
                    },
                    ["GrimmFly"] = new[]
                    {
                        "Grimmbat_fly_full0000", "Grimmbat_fly_full0001", "Grimmbat_fly_full0002",
                        "Grimmbat_fly_full0003", "Grimmbat_fly_full0004", "Grimmbat_fly_full0005"
                    },
                    ["GrimmSleep"] = new[]
                    {
                        "Grimmbat_sleep0000", "Grimmbat_sleep0001", "Grimmbat_sleep0002"
                    },
                    ["GrimmWake"] = new[]
                    {
                        "Grimmbat_sleep0002", "Grimmbat_sleep0001", "Grimmbat_sleep0000"
                    },
                    ["GrimmTeleport"] = new[]
                    {
                        "Grimmbat_teleport0000", "Grimmbat_teleport0001", "Grimmbat_teleport0002",
                        "Grimmbat_teleport0003", "Grimmbat_teleport0004", "Grimmbat_teleport0005",
                        "Grimmbat_teleport0006", "Grimmbat_teleport0007"
                    },
                    ["GrimmShoot"] = new[]
                    {
                        "Grimmbat_shoot0000", "Grimmbat_shoot0001", "Grimmbat_shoot0002",
                        "Grimmbat_shoot0003", "Grimmbat_shoot0004", "Grimmbat_shoot0005"
                    },
                    ["GrimmFireball"] = new[]
                    {
                        "grimm_fireball0000", "grimm_fireball0001", "grimm_fireball0002",
                        "grimm_fireball0003", "grimm_fireball0004", "grimm_fireball0005",
                        "grimm_fireball0006", "grimm_fireball0007"
                    }
                };
                var fps = new Dictionary<string, float>
                {
                    ["GrimmAppear"] = 20f,
                    ["GrimmIdle"] = 12f,
                    ["GrimmFly"] = 12f,
                    ["GrimmSleep"] = 12f,
                    ["GrimmWake"] = 12f,
                    ["GrimmTeleport"] = 20f,
                    ["GrimmShoot"] = 12f,
                    ["GrimmFireball"] = 16f
                };
                foreach (var kv in wanted)
                {
                    foreach (string n in kv.Value)
                    {
                        if (_textures.ContainsKey(n))
                        {
                            continue;
                        }
                        string png = Path.Combine(dir, n + ".png");
                        if (!File.Exists(png))
                        {
                            continue;
                        }
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                        {
                            continue;
                        }
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        _textures[n] = tex;
                    }
                }
                foreach (var kv in wanted)
                {
                    var frames = new List<string>();
                    foreach (string n in kv.Value)
                    {
                        if (_textures.ContainsKey(n))
                        {
                            frames.Add(n);
                        }
                    }
                    if (frames.Count == 0)
                    {
                        continue;
                    }
                    _clips[kv.Key] = new ClipData
                    {
                        fps = fps[kv.Key],
                        wrapMode = 0,
                        loopStart = 0,
                        frames = frames.ToArray()
                    };
                }
                // 小格林音效（外部 wav）
                DashAudio.LoadGrimmSounds();
                // 无忧旋律音效（外部 wav）
                DashAudio.LoadTuneSound();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 凝聚三阶段剪辑：发动 focus_v020000~0002（0.27s）、
        /// 回血 focus_v020003~0006（0.82s）、后续循环 focus_v020003~0010（1s）。
        /// 结束帧：首次复用 Focus Get Once（0007~0011），后续只补 0011（FocusEndLast）。
        /// </summary>
        private void BuildFocusPhaseClips()
        {
            string[] heal4 = { "focus_v020003", "focus_v020004", "focus_v020005", "focus_v020006" };
            string[] heal8 = { "focus_v020003", "focus_v020004", "focus_v020005", "focus_v020006",
                               "focus_v020003", "focus_v020004", "focus_v020005", "focus_v020006" };
            BuildFocusSubClip("FocusStart",
                new[] { "focus_v020000", "focus_v020001", "focus_v020002" },
                3f / FocusStartTime);
            BuildFocusSubClip("FocusHeal",
                heal4,
                4f / FocusHealTime);
            // 26 快速聚集（-0.3s）/ 27 深度聚集（+0.55s）：回血段帧率按调整后时长变化
            BuildFocusSubClip("FocusHealFast",
                heal4,
                4f / (FocusHealTime + FocusFastMod));
            BuildFocusSubClip("FocusHealDeep",
                heal4,
                4f / (FocusHealTime + FocusDeepMod));
            BuildFocusSubClip("FocusHealBoth",
                heal4,
                4f / (FocusHealTime + FocusFastMod + FocusDeepMod));
            // 后续回血循环·回血段：0003~0006 复制两遍，共 8 帧在 0.8s 内播完
            BuildFocusSubClip("FocusHealNext",
                heal8,
                8f / FocusHealDupTime);
            BuildFocusSubClip("FocusHealNextFast",
                heal8,
                8f / (FocusHealDupTime + FocusFastMod));
            BuildFocusSubClip("FocusHealNextDeep",
                heal8,
                8f / (FocusHealDupTime + FocusDeepMod));
            BuildFocusSubClip("FocusHealNextBoth",
                heal8,
                8f / (FocusHealDupTime + FocusFastMod + FocusDeepMod));
            // 爆发段（首次与后续共用）：0007~0010 共 4 帧在 0.25s 内播完（0007 与回血音效同时）
            BuildFocusSubClip("FocusHealBurst",
                new[] { "focus_v020007", "focus_v020008", "focus_v020009", "focus_v020010" },
                4f / FocusBurstTime);
            // 后续循环松开后的补帧：只播 focus_v020011（帧率与 Focus Get Once 一致）
            BuildFocusSubClip("FocusEndLast", new[] { "focus_v020011" }, 21.73913f);
        }

        /// <summary>按帧序列与 fps 注册一个一次性剪辑（只保留已加载的帧）。</summary>
        private void BuildFocusSubClip(string key, string[] frameNames, float fps)
        {
            var frames = new List<string>();
            foreach (string name in frameNames)
            {
                if (_textures.ContainsKey(name))
                {
                    frames.Add(name);
                }
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips[key] = new ClipData
            {
                fps = fps,
                wrapMode = 2, // 一次性播完
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        private void BuildLowHpIdleClip()
        {
            var frames = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                string name = "idle_low_health" + i.ToString("D3");
                if (_textures.ContainsKey(name))
                {
                    frames.Add(name);
                }
            }
            if (frames.Count == 0)
            {
                return;
            }
            _clips["LowHpIdle"] = new ClipData
            {
                fps = 12f,
                wrapMode = 0,
                loopStart = 0,
                frames = frames.ToArray()
            };
        }

        /// <summary>
        /// 挑衅动画（V 键）：按需求拼接
        /// 开场 knight_challenge0000~0008 → 0009~0010 各一次 → 收尾 0010~0017。
        /// 原版帧 0002~0004 / 0012~0015 不存在，缺失帧沿用前一帧补齐（与 manifest Challenge Start 的补帧方式一致）。
        /// wrapMode=2：一次性播完停在最后一帧，由 _tauntTimer 结束后恢复站立。
        /// </summary>
        private void BuildChallengeClip()
        {
            try
            {
                var frames = new List<string>();
                // 开场 0000~0008（缺失帧用 0001 补齐，与原版 Challenge Start 一致）
                string[] openSeq = new[] { "0000", "0001", "0001", "0001", "0001", "0005", "0006", "0007", "0008" };
                for (int i = 0; i < openSeq.Length; i++)
                {
                    string n = "knight_challenge" + openSeq[i];
                    if (_textures.ContainsKey(n))
                    {
                        frames.Add(n);
                    }
                }
                // 0009~0010 各播一次（收尾段已用 0011 补齐时长，这里不再重复）
                string c0009 = "knight_challenge0009";
                string c0010 = "knight_challenge0010";
                if (_textures.ContainsKey(c0009))
                {
                    frames.Add(c0009);
                }
                if (_textures.ContainsKey(c0010))
                {
                    frames.Add(c0010);
                }
                // 收尾 0010~0017（缺失帧 0012~0015 用 0011 补齐）
                string[] endSeq = new[] { "0010", "0011", "0011", "0011", "0011", "0011", "0016", "0017" };
                for (int i = 0; i < endSeq.Length; i++)
                {
                    string n = "knight_challenge" + endSeq[i];
                    if (_textures.ContainsKey(n))
                    {
                        frames.Add(n);
                    }
                }
                if (frames.Count == 0)
                {
                    return;
                }
                _clips["Challenge"] = new ClipData
                {
                    // 总时长固定 1.2s：帧数 ÷ 1.2（当前 19 帧 → ≈15.83fps）
                    fps = frames.Count / 1.2f,
                    wrapMode = 2,
                    loopStart = 0,
                    frames = frames.ToArray()
                };
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 从原版挥砍剪辑取前 8 帧，做成 24fps 的一次性攻击剪辑。
        /// </summary>
        private void BuildAttackClip(string sourceClip, string targetClip, float fps = 24f, int maxFrames = 8)
        {
            try
            {
                if (_clips.TryGetValue(sourceClip, out ClipData slashClip) && slashClip.frames.Length >= maxFrames)
                {
                    var attackFrames = new List<string>();
                    for (int i = 0; i < maxFrames; i++)
                    {
                        attackFrames.Add(slashClip.frames[i]);
                    }
                    _clips[targetClip] = new ClipData
                    {
                        fps = fps,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = attackFrames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 右墙爬墙专用剪辑：把原版 Wall Slide 的每一帧（wall_slideXXXX）映射到
        /// 左右镜像帧（wall_slide_rightXXXX），参数与原剪辑一致（fps/wrapMode/loopStart）。
        /// </summary>
        private void BuildWallSlideRightClip()
        {
            try
            {
                if (!_clips.TryGetValue("Wall Slide", out ClipData ws) || ws.frames.Length == 0)
                {
                    return;
                }
                string spriteDir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk", "sprites");
                var frames = new List<string>();
                foreach (string f in ws.frames)
                {
                    string rightName = f.StartsWith("wall_slide")
                        ? "wall_slide_right" + f.Substring("wall_slide".Length)
                        : f + "_right";
                    string png = Path.Combine(spriteDir, rightName + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    // 跳过空白帧（全透明占位图会让该残影完全看不见）
                    if (IsBlankTexture(tex))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[rightName] = tex;
                    // 脚底位置沿用原帧的 feet 值
                    if (_feetFraction.TryGetValue(f, out float ff))
                    {
                        _feetFraction[rightName] = ff;
                    }
                    frames.Add(rightName);
                }

                if (frames.Count > 0)
                {
                    _clips["Wall Slide Right"] = new ClipData
                    {
                        fps = ws.fps,
                        wrapMode = ws.wrapMode,
                        loopStart = ws.loopStart,
                        frames = frames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 从源剪辑构建“右墙”镜像变体：帧名末尾数字前插入 rightMarker，
        /// 从 sprites 目录加载对应的预镜像 PNG（例如 wall_charge0000 → wall_charge_right0000）。
        /// </summary>
        private void BuildRightVariantClip(string dstClip, string srcClip, string rightMarker)
        {
            try
            {
                if (!_clips.TryGetValue(srcClip, out ClipData src) || src.frames.Length == 0)
                {
                    return;
                }
                string spriteDir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk", "sprites");
                var frames = new List<string>();
                foreach (string f in src.frames)
                {
                    int di = f.Length;
                    while (di > 0 && char.IsDigit(f[di - 1]))
                    {
                        di--;
                    }
                    string rightName = f.Substring(0, di) + rightMarker + f.Substring(di);
                    if (_textures.ContainsKey(rightName))
                    {
                        frames.Add(rightName);
                        continue;
                    }
                    string png = Path.Combine(spriteDir, rightName + ".png");
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[rightName] = tex;
                    frames.Add(rightName);
                }
                if (frames.Count > 0)
                {
                    _clips[dstClip] = new ClipData
                    {
                        fps = src.fps,
                        wrapMode = src.wrapMode,
                        loopStart = src.loopStart,
                        frames = frames.ToArray()
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 从 sheets 下的小写排序文件夹加载特效帧（shadow_dash / shadow_dash_trail / shadow_recharge）。
        /// 帧顺序严格按数字从小到大。
        /// </summary>
        /// <summary>
        /// 虚空解放素材：读取 sheets/void_liberation 下的梦之门传送帧与尖叫帧，
        /// 注册三套剪辑：前摇 VoidEnter（0000~0008 + 0003~0011，6fps）、
        /// 出伤 VoidScream（scream_cast_lvl_020002~020007，6fps 循环）、
        /// 后摇 VoidExit（enter0011~0008 + enter0002~0000，6fps）。
        /// </summary>
        private void LoadVoidLiberationAssets()
        {
            try
            {
                string folder = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "void_liberation");
                if (!Directory.Exists(folder))
                {
                    return;
                }
                // 素材按子目录组织：dark_descent（梦之门传送帧）/ knight_cast（尖叫帧）
                string enterDir = Path.Combine(folder, "dark_descent");
                string screamDir = Path.Combine(folder, "knight_cast");
                string tentacleDir = Path.Combine(folder, "tentacle");
                // 梦之门传送帧 Knight_dream_gate_enter0000~0011
                var enterFrames = new List<string>();
                for (int i = 0; i < 12; i++)
                {
                    string name = "Knight_dream_gate_enter" + i.ToString("D4");
                    string png = Path.Combine(enterDir, name + ".png");
                    if (!File.Exists(png))
                    {
                        png = Path.Combine(folder, name + ".png"); // 兼容旧平铺布局
                    }
                    if (!File.Exists(png))
                    {
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                    enterFrames.Add(name);
                }
                // 尖叫帧 scream_cast_lvl_020002~020007（用户删去了 020001，共 6 帧）
                var screamFrames = new List<string>();
                var screamFiles = new List<string>();
                if (Directory.Exists(screamDir))
                {
                    screamFiles.AddRange(Directory.GetFiles(screamDir, "scream_cast_lvl_020*.png"));
                }
                screamFiles.AddRange(Directory.GetFiles(folder, "scream_cast_lvl_020*.png")); // 兼容平铺布局
                foreach (string png in screamFiles.Distinct().OrderBy(p => p, StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(png);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[name] = tex;
                    screamFrames.Add(name);
                }
                if (enterFrames.Count >= 12)
                {
                    // 前摇顺序：0000~0008，然后 0003~0011（18 帧，6fps = 3s）
                    var chargeOrder = new List<string>(18);
                    for (int i = 0; i <= 8; i++)
                    {
                        chargeOrder.Add(enterFrames[i]);
                    }
                    for (int i = 3; i <= 11; i++)
                    {
                        chargeOrder.Add(enterFrames[i]);
                    }
                    _clips["VoidEnter"] = new ClipData
                    {
                        fps = VoidChargeFps,
                        wrapMode = 2, // 播完保持最后一帧
                        loopStart = 0,
                        frames = chargeOrder.ToArray()
                    };
                    // 后摇：enter0011~0008，enter0002~0000
                    var exitFrames = new List<string>();
                    for (int i = 11; i >= 8; i--)
                    {
                        exitFrames.Add(enterFrames[i]);
                    }
                    for (int i = 2; i >= 0; i--)
                    {
                        exitFrames.Add(enterFrames[i]);
                    }
                    _clips["VoidExit"] = new ClipData
                    {
                        fps = 6f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = exitFrames.ToArray()
                    };
                }
                if (screamFrames.Count >= 6)
                {
                    _clips["VoidScream"] = new ClipData
                    {
                        fps = 6f,
                        wrapMode = 2,
                        loopStart = 0,
                        frames = screamFrames.ToArray()
                    };
                }
                // 屏幕边缘虚空触手：Abyss_tendrils0000~0020（黑屏结束 → 白屏开始 期间显示）
                // 四个朝向：0=底（原图朝上） 1=顶（180°） 2=左（顺时针90°） 3=右（逆时针90°）
                var tentacleData = new List<TentacleFrameData>();
                if (Directory.Exists(tentacleDir))
                {
                    foreach (string png in Directory.GetFiles(tentacleDir, "Abyss_tendrils*.png")
                        .OrderBy(p => p, StringComparer.Ordinal))
                    {
                        string name = Path.GetFileNameWithoutExtension(png);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                        {
                            continue;
                        }
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        var td = new TentacleFrameData { Name = name };
                        td.Variants[0] = BuildTentacleVariant(tex, 0); // 底：原图
                        td.Variants[1] = BuildTentacleVariant(tex, 1); // 顶：180°
                        td.Variants[2] = BuildTentacleVariant(tex, 2); // 左：顺时针90°
                        td.Variants[3] = BuildTentacleVariant(tex, 3); // 右：逆时针90°
                        tentacleData.Add(td);
                        UnityEngine.Object.Destroy(tex); // 原图已复制到各变体，释放临时对象
                    }
                }
                if (tentacleData.Count > 0)
                {
                    _voidTentacleData = tentacleData.ToArray();
                }
                // 虚空解放第二段：身后双图（迅速交替 + 随机旋转）
                string backDir = Path.Combine(folder, "void_back");
                _voidBackTex1 = LoadSingleTexture(Path.Combine(backDir, "void_back_1.png"));
                _voidBackTex2 = LoadSingleTexture(Path.Combine(backDir, "void_back_2.png"));
                // 虚空解放出伤：目标身上的划痕动画 Radiance_GG_slashes0000~0010
                string slashDir = Path.Combine(folder, "slashes");
                var slashFrames = new List<string>();
                if (Directory.Exists(slashDir))
                {
                    foreach (string png in Directory.GetFiles(slashDir, "Radiance_GG_slashes*.png")
                        .OrderBy(p => p, StringComparer.Ordinal))
                    {
                        string name = Path.GetFileNameWithoutExtension(png);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                        {
                            continue;
                        }
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        _textures[name] = tex;
                        slashFrames.Add(name);
                    }
                }
                if (slashFrames.Count > 0)
                {
                    _voidSlashFrames = slashFrames.ToArray();
                }
                // 虚空解放音效（外部 wav）
                DashAudio.LoadVoidLiberationSounds();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 生成一个朝向变体：0=原图（底） 1=180°（顶） 2=顺时针90°（左） 3=逆时针90°（右），
        /// 并扫描旋转后的不透明内容包围盒，用于“贴边锚定”避免换帧晃动。
        /// </summary>
        private static TentacleVariantData BuildTentacleVariant(Texture2D src, int rot)
        {
            var v = new TentacleVariantData();
            try
            {
                int w = src.width;
                int h = src.height;
                Color32[] px = src.GetPixels32();
                if (rot == 0)
                {
                    v.Tex = CloneTexture(src, px, w, h);
                    v.W = w;
                    v.H = h;
                }
                else
                {
                    int nw = rot == 1 ? w : h;
                    int nh = rot == 1 ? h : w;
                    var np = new Color32[nw * nh];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color32 c = px[y * w + x];
                            if (rot == 1) // 180°
                            {
                                np[(nh - 1 - y) * nw + (nw - 1 - x)] = c;
                            }
                            else if (rot == 2) // 顺时针90°
                            {
                                np[x * nw + (nh - 1 - y)] = c;
                            }
                            else // 逆时针90°
                            {
                                np[(w - 1 - x) * nw + y] = c;
                            }
                        }
                    }
                    v.Tex = new Texture2D(nw, nh, TextureFormat.RGBA32, false)
                    {
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    v.Tex.SetPixels32(np);
                    v.Tex.Apply();
                    v.W = nw;
                    v.H = nh;
                }
                // 扫描旋转后内容包围盒
                Color32[] vp = v.Tex.GetPixels32();
                int x0 = v.W, y0 = v.H, x1 = -1, y1 = -1;
                for (int y = 0; y < v.H; y++)
                {
                    for (int x = 0; x < v.W; x++)
                    {
                        if (vp[y * v.W + x].a > 8)
                        {
                            if (x < x0)
                            {
                                x0 = x;
                            }
                            if (x > x1)
                            {
                                x1 = x;
                            }
                            if (y < y0)
                            {
                                y0 = y;
                            }
                            if (y > y1)
                            {
                                y1 = y;
                            }
                        }
                    }
                }
                if (x1 >= x0 && y1 >= y0)
                {
                    if (rot == 0 || rot == 1) // 底/顶：沿水平方向排布
                    {
                        v.ContentAlong = x1 - x0 + 1;
                        v.EdgeGapFrac = rot == 0
                            ? (v.H - (y1 + 1f)) / v.H
                            : y0 / (float)v.H;
                    }
                    else // 左/右：沿竖直方向排布
                    {
                        v.ContentAlong = y1 - y0 + 1;
                        v.EdgeGapFrac = rot == 2
                            ? x0 / (float)v.W
                            : (v.W - (x1 + 1f)) / v.W;
                    }
                }
                else
                {
                    v.ContentAlong = rot == 0 || rot == 1 ? v.W : v.H;
                    v.EdgeGapFrac = 0f;
                }
            }
            catch (Exception)
            {
                v.ContentAlong = Mathf.Max(1, v.W);
                v.EdgeGapFrac = 0f;
            }
            return v;
        }

        private static Texture2D CloneTexture(Texture2D src, Color32[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>从插件素材目录加载一张 PNG 贴图（失败返回 null）。</summary>
        private static Texture2D LoadSingleTexture(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
                {
                    return null;
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void LoadShadowClipSet(string folderName, string clipName)
        {
            try
            {
                string folder = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk", "sheets", folderName);
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var frames = new List<string>();
                // 直接枚举文件夹里的帧（按数字顺序），不再按固定数量查找
                string[] pngs = Directory.GetFiles(folder, "*.png")
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();
                foreach (string png in pngs)
                {
                    string baseName = Path.GetFileNameWithoutExtension(png);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                    {
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    _textures[baseName] = tex;
                    frames.Add(baseName);
                }

                if (frames.Count > 0)
                {
                    if (folderName == "shadow_dash" && frames.Count >= 12)
                    {
                        // 冲刺身体拆两段：位移段 0000~0008，终点静止段 0009~0011
                        // 帧率随冲刺时长自适应：12 帧在冲刺时长内播完
                        float dashTime = KnightInCradlePlugin.DashTimeConfig != null
                            ? KnightInCradlePlugin.DashTimeConfig.Value
                            : 0.4f;
                        float bodyFps = 12f / (dashTime * 0.75f);
                        _clips["ShadowDashMove"] = new ClipData
                        {
                            fps = bodyFps,
                            wrapMode = 2,
                            loopStart = 0,
                            frames = frames.Take(9).ToArray()
                        };
                        _clips["ShadowDashEnd"] = new ClipData
                        {
                            fps = bodyFps,
                            wrapMode = 2,
                            loopStart = 0,
                            frames = frames.Skip(9).ToArray()
                        };
                    }
                    else
                    {
                        _clips[clipName] = new ClipData
                        {
                            fps = 20f,
                            // 充能只播一遍（Once），避免 0020 后绕回重播
                            wrapMode = clipName == "ShadowRecharge" ? 2 : 1,
                            loopStart = 0,
                            frames = frames.ToArray()
                        };
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 判断贴图是否为全透明空白帧（加载时过滤掉，避免残影/特效出现“看不见的帧”）。
        /// </summary>
        private static bool IsBlankTexture(Texture2D tex)
        {
            try
            {
                Color32[] px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a > 8)
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }


        private void PlayClip(string clipName)
        {
            // 诊断：乌恩形态凝聚期间打印朝向与剪辑，定位“忽左忽右”问题
            if (_focusing && CharmEffects.IsEquipped(CharmEffects.UnnId) && _currentClip != clipName)
            {
            }
            if (!_clips.TryGetValue(clipName, out ClipData clip))
            {
                clipName = "Idle";
                if (!_clips.TryGetValue(clipName, out clip))
                {
                    return;
                }
            }

            if (_currentClip != clipName)
            {
                _currentClip = clipName;
                _frameIndex = 0;
                _frameTimer = 0f;
            }

            int n = clip.frames.Length;
            if (n == 0)
            {
                return;
            }

            float animSpeed = KnightInCradlePlugin.AnimSpeedConfig != null
                ? KnightInCradlePlugin.AnimSpeedConfig.Value
                : 1f;
            float speed = animSpeed;
            // 挥砍剪辑（8 帧 @ SlashClipFps = 一整刀时长）：
            // 不乘全局 AnimSpeed，只按快速劈砍加速（0.4/0.3 ≈ 1.333 倍），
            // 保证“动画长度 = 本刀时长 = 两刀之间的间隔”。
            if (_currentClip == "Attack" || _currentClip == "AttackAlt" ||
                _currentClip == "UpSlash" || _currentClip == "DownSlash")
            {
                speed = CharmEffects.SlashAnimSpeedMultiplier();
            }
            _frameTimer += Time.deltaTime * speed;
            float frameTime = 1f / Mathf.Max(clip.fps, 0.001f);
            int guard = 20;
            while (_frameTimer >= frameTime && guard-- > 0)
            {
                _frameTimer -= frameTime;
                _frameIndex++;
            }

            string spriteName = clip.frames[EffectiveIndex(clip, _frameIndex)];
            _currentSpriteName = spriteName;
            _currentTex = _textures[spriteName];
        }

        private void PlayRechargeClip(string clipName)
        {
            if (!_clips.TryGetValue(clipName, out ClipData clip))
            {
                return;
            }

            if (_rcClipName != clipName)
            {
                _rcClipName = clipName;
                _rcFrameIndex = 0;
                _rcFrameTimer = 0f;
            }

            int n = clip.frames.Length;
            if (n == 0)
            {
                return;
            }

            float animSpeed = KnightInCradlePlugin.AnimSpeedConfig != null
                ? KnightInCradlePlugin.AnimSpeedConfig.Value
                : 1f;
            _rcFrameTimer += Time.deltaTime * animSpeed;
            float frameTime = 1f / Mathf.Max(clip.fps, 0.001f);
            int guard = 20;
            while (_rcFrameTimer >= frameTime && guard-- > 0)
            {
                _rcFrameTimer -= frameTime;
                _rcFrameIndex++;
            }

            string spriteName = clip.frames[EffectiveIndex(clip, _rcFrameIndex)];
            _rcSpriteName = spriteName;
            if (_textures.TryGetValue(spriteName, out Texture2D tex))
            {
                _rcTex = tex;
            }
        }

        /// <summary>
        /// 播放攻击剑气特效（SlashEffect），按原版帧率播放，播完即消失。
        /// </summary>
        private void PlayFxClip(string clipName)
        {
            if (!_clips.TryGetValue(clipName, out ClipData clip) || clip.frames.Length == 0)
            {
                _fxTex = null;
                return;
            }
            if (_fxClipName != clipName)
            {
                _fxClipName = clipName;
                _fxFrameIndex = 0;
                _fxFrameTimer = 0f;
            }

            _fxFrameTimer += Time.deltaTime;
            float frameTime = 1f / Mathf.Max(clip.fps, 0.001f);
            int guard = 20;
            while (_fxFrameTimer >= frameTime && guard-- > 0)
            {
                _fxFrameTimer -= frameTime;
                _fxFrameIndex++;
            }

            // 播完最后一张后隐藏（不循环）
            if (_fxFrameIndex >= clip.frames.Length)
            {
                _fxTex = null;
                return;
            }
            string spriteName = clip.frames[_fxFrameIndex];
            _fxSpriteName = spriteName;
            // 护符10 蜕变挽歌：满血普攻（平砍）播放到 slashes_effect0001 帧时发射剑气；
            // 平砍剑气左右手交替（SlashEffect=0001 / SlashEffectAlt=0005），两组都要能发射；
            // 修长之钉/骄傲印记换成螳螂爪样式时对应帧是 mantis_slash_left0001 / 0005。
            // 羁绊：蜕变挽歌 + 亡者之怒 —— 1 血（狂怒状态）时同样能发射剑气
            if (!_elegySpawnedThisSwing && _attacking && !_upSlash && !_downSlash &&
                CharmEffects.IsEquipped(CharmEffects.ElegyId) &&
                ((_health >= MaxHealth &&
                  (spriteName == "slashes_effect0001" || spriteName == "slashes_effect0005" ||
                   spriteName == "mantis_slash_left0001" || spriteName == "mantis_slash_left0005")) ||
                 (FuryActive && spriteName == "rage_slash_left0001")))
            {
                SpawnElegyBlade();
            }
            if (_textures.TryGetValue(spriteName, out Texture2D tex))
            {
                _fxTex = tex;
            }
        }

        // ---------- 攻击判定框 ----------

        private void SpawnHitbox()
        {
            DestroyHitbox();
            var go = new GameObject("KnightAttackHitbox");
            go.transform.SetParent(transform, false);
            // 怪物可被打中的内层碰撞器在 "EnemySelf" 物理层，
            // 判定框也放同一层才能与怪物产生 2D 触发器事件
            int enemySelfLayer = LayerMask.NameToLayer("EnemySelf");
            if (enemySelfLayer >= 0)
            {
                go.layer = enemySelfLayer;
            }

            // 判定形状 = 底部长方形(Box) + 前方三角形尖端(Polygon)，还原原版“矩形+三角形”
            Vector2 boxSize;
            Vector2 tipDir;
            Vector2 totalSize;
            if (_upSlash)
            {
                // 护符18/19：上劈高度向上拉伸（尖端在上边缘，随拉伸一起上移）
                float sup = CharmEffects.UpSlashStretchUp();
                bool upLongNail = CharmEffects.LongNailVisual();
                // 附加向下拉伸：佩戴修长之钉/骄傲印记 0.5 格，未佩戴也 0.5 格（数值不同则分开配）
                float supDown = upLongNail ? MantisUpSlashHitboxDown : PlainUpSlashHitboxDown;
                // 未佩戴护符：矩形顶端再向下收缩 0.5 格（三角形底边贴在矩形顶端，一起下移）
                float supTopShrink = upLongNail ? 0f : PlainUpSlashTopShrink;
                // 左右拉伸：修长之钉/骄傲印记各 0.5 格，未佩戴各 0.2 格（三角形底边随矩形同宽）
                float supWide = upLongNail ? MantisUpSlashHitboxWiden : PlainUpSlashHitboxWidenX;
                totalSize = new Vector2(UpSlashSizeX + supWide * 2f,
                    UpSlashSizeY + sup + supDown - supTopShrink);
                boxSize = new Vector2(UpSlashSizeX + supWide * 2f,
                    UpSlashSizeY - AttackTipLen + sup + supDown - supTopShrink);
                tipDir = new Vector2(0f, 1f); // 尖端向上
            }
            else if (_downSlash)
            {
                // 下劈基础向上拉伸 +0.4 格；护符18/19 再向下拉伸（尖端在下边缘，随拉伸下移）
                float sdown = CharmEffects.DownSlashStretchDown();
                bool downLongNail = CharmEffects.LongNailVisual();
                // 附加向上拉伸：佩戴修长之钉/骄傲印记 0.2 格，未佩戴 0.5 格
                float sdownUp = downLongNail ? MantisDownSlashHitboxUp : PlainDownSlashHitboxUp;
                // 未佩戴护符：矩形底端再向上收缩 0.5 格（三角形底边贴在矩形底端，一起上移）
                float sdownBottomShrink = downLongNail ? 0f : PlainDownSlashBottomShrink;
                // 左右拉伸：修长之钉/骄傲印记各 0.2 格，未佩戴也各 0.2 格（三角形底边随矩形同宽）
                float sdownWide = downLongNail ? MantisDownSlashHitboxWiden : PlainDownSlashHitboxWidenX;
                totalSize = new Vector2(DownSlashSizeX + sdownWide * 2f,
                    DownSlashSizeY + DownSlashStretchUp + sdown + sdownUp - sdownBottomShrink);
                boxSize = new Vector2(DownSlashSizeX + sdownWide * 2f,
                    DownSlashSizeY - AttackTipLen + DownSlashStretchUp + sdown + sdownUp - sdownBottomShrink);
                tipDir = new Vector2(0f, -1f); // 尖端向下
            }
            else
            {
                // 护符18/19 长钉/骄傲印记：平砍判定框长度/高度增加（可叠加）
                float lw = CharmEffects.LongRangeMultiplier();
                float lh = CharmEffects.LongRangeHeightMultiplier();
                // 骄傲印记“只向右拉伸”：骑士侧边缘额外外扩（远侧不动，尖端不受影响）
                float stretch = CharmEffects.LongRangeStretch();
                totalSize = new Vector2(HitboxSizeX * lw + stretch, HitboxSizeY * lh);
                boxSize = new Vector2((HitboxSizeX - AttackTipLen) * lw + stretch, HitboxSizeY * lh);
                // 尖端朝面朝方向（_faceDir=1 朝左 → 世界 -X）
                tipDir = new Vector2(-_faceDir, 0f);
            }

            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = boxSize;

            var tipGo = new GameObject("KnightAttackTip");
            tipGo.transform.SetParent(go.transform, false);
            if (enemySelfLayer >= 0)
            {
                tipGo.layer = enemySelfLayer; // 子物体也要同物理层，否则触发器不生效
            }
            var poly = tipGo.AddComponent<PolygonCollider2D>();
            poly.isTrigger = true;
            float hw = boxSize.x * 0.5f;
            float hh = boxSize.y * 0.5f;
            if (tipDir.x != 0f)
            {
                float dir = tipDir.x;
                poly.points = new Vector2[]
                {
                    new Vector2(dir * hw, -hh),
                    new Vector2(dir * hw, hh),
                    new Vector2(dir * (hw + AttackTipLen), 0f)
                };
            }
            else
            {
                float dir = tipDir.y;
                poly.points = new Vector2[]
                {
                    new Vector2(-hw, dir * hh),
                    new Vector2(hw, dir * hh),
                    new Vector2(0f, dir * (hh + AttackTipLen))
                };
            }

            var rootHb = go.AddComponent<KnightAttackHitbox>();
            rootHb.InitRoot(go);
            var tipHb = tipGo.AddComponent<KnightAttackHitbox>();
            tipHb.InitRoot(go);
            _hitboxColliders = new Collider2D[] { box, poly };
            _hitboxActiveTimer = HitboxActiveTime;
            _hitboxGo = go;
            UpdateHitboxPosition();
        }

        private void UpdateHitboxPosition()
        {
            if (_hitboxGo == null || _mp == null)
            {
                return;
            }
            // 与渲染骑士同一套坐标换算，保证判定框紧跟渲染位置
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            // 上劈：正上方水平居中；下劈：正下方；平砍：脸朝向前方
            float hx;
            float hy;
            if (_upSlash)
            {
                hx = UpSlashOffsetX; // 0：与骑士 X 轴完全对齐
                // 护符18/19：上劈向上拉伸，中心上移拉伸量的一半
                // 判定框还向下拉伸（长钉/骄傲印记 0.5 / 未佩戴 0.5），中心再下移拉伸量的一半
                bool upLongNailPos = CharmEffects.LongNailVisual();
                float supDownPos = upLongNailPos ? MantisUpSlashHitboxDown : PlainUpSlashHitboxDown;
                // 未佩戴护符：矩形顶端下移 0.5 格 → 中心再下移 0.25 格
                float supTopShrinkPos = upLongNailPos ? 0f : PlainUpSlashTopShrink;
                hy = UpSlashOffsetY + CellToUx(CharmEffects.UpSlashStretchUp() * 0.5f
                    - supDownPos * 0.5f
                    - supTopShrinkPos * 0.5f);
            }
            else if (_downSlash)
            {
                hx = DownSlashOffsetX;
                // 基础向上拉伸（中心上移 0.2）+ 护符向下拉伸（中心下移一半）
                // 判定框还向上拉伸（长钉/骄傲印记 0.2 / 未佩戴 0.5），中心再上移拉伸量的一半
                bool downLongNailPos = CharmEffects.LongNailVisual();
                float sdownUpPos = downLongNailPos ? MantisDownSlashHitboxUp : PlainDownSlashHitboxUp;
                // 未佩戴护符：矩形底端上移 0.5 格 → 中心再上移 0.25 格
                float sdownBottomShrinkPos = downLongNailPos ? 0f : PlainDownSlashBottomShrink;
                hy = DownSlashOffsetY + CellToUx(DownSlashStretchUp * 0.5f
                    - CharmEffects.DownSlashStretchDown() * 0.5f
                    + sdownUpPos * 0.5f
                    + sdownBottomShrinkPos * 0.5f);
            }
            else
            {
                // 护符18/19 长钉/骄傲印记：平砍判定框向面朝方向平移（可叠加）
                // LongRangeShift 以“格”为单位，先换算成 ux 再应用（与 HitboxOffsetX 同单位）
                float lshift = CellToUx(CharmEffects.LongRangeShift());
                // 拉伸只拉骑士侧边缘：中心再向身体方向移“拉伸量的一半”
                float stretchShift = CellToUx(CharmEffects.LongRangeStretch() * 0.5f);
                hx = -_faceDir * (HitboxOffsetX + lshift) + _faceDir * stretchShift;
                hy = HitboxOffsetY;
            }
            _hitboxGo.transform.position = _mp.gameObject.transform.TransformPoint(
                new Vector3(mx + hx, my + hy, 0f));
        }

        /// <summary>
        /// 兜底命中检测：直接对判定框区域做物理查询（不受层碰撞矩阵限制），
        /// 即使 OnTriggerEnter2D 因层矩阵没触发，也能保证打中怪物时输出日志。
        /// </summary>
        private void CheckAttackOverlap()
        {
            if (_mp == null)
            {
                return;
            }
            int mask = LayerMask.GetMask("EnemySelf", "Enemy", "AttackHitable");
            // 追加尖刺/荆棘等陷阱可能所在的层，确保 OverlapBoxAll 能扫到它们
            int trapLayer = LayerMask.NameToLayer("Trap");
            if (trapLayer >= 0)
            {
                mask |= 1 << trapLayer;
            }
            int kusariLayer = LayerMask.NameToLayer("Kusari");
            if (kusariLayer >= 0)
            {
                mask |= 1 << kusariLayer;
            }
            int spikeLayer = LayerMask.NameToLayer("Spike");
            if (spikeLayer >= 0)
            {
                mask |= 1 << spikeLayer;
            }
            // AIC 地图芯片（含尖刺/荆棘的贴图物体）所在的层
            int chipsLayer = LayerMask.NameToLayer("Chips");
            if (chipsLayer >= 0)
            {
                mask |= 1 << chipsLayer;
            }
            int chipsUColLayer = LayerMask.NameToLayer("ChipsUCol");
            if (chipsUColLayer >= 0)
            {
                mask |= 1 << chipsUColLayer;
            }
            // 可破坏物（Breakable_*）等 M2Mover 碰撞体可能在 Default / Water 层
            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer >= 0)
            {
                mask |= 1 << defaultLayer;
            }
            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer >= 0)
            {
                mask |= 1 << waterLayer;
            }
            int transparentLayer = LayerMask.NameToLayer("TransparentFX");
            if (transparentLayer >= 0)
            {
                mask |= 1 << transparentLayer;
            }
            // 虫巢抓取体（M2WormTrap）的碰撞体在 Ignore Raycast 层
            int ignoreRayLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRayLayer >= 0)
            {
                mask |= 1 << ignoreRayLayer;
            }
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(X * _mp.CLEN);
            float my = _mp.pixel2uy(Y * _mp.CLEN);
            // 与 UpdateHitboxPosition 同一套坐标：上劈正上方、下劈正下方、平砍正前方
            float hx;
            float hy;
            Vector2 size;
            if (_upSlash)
            {
                hx = UpSlashOffsetX; // 0：与骑士 X 轴完全对齐
                // 与碰撞体一致：护符18/19 上劈向上拉伸，中心上移一半
                // 判定框附加：向下拉伸（长钉/骄傲印记 0.5 / 未佩戴 0.5）+ 左右拉伸（0.5 / 0.2）
                bool upLongNailOv = CharmEffects.LongNailVisual();
                float supWideOv = upLongNailOv ? MantisUpSlashHitboxWiden : PlainUpSlashHitboxWidenX;
                float supDownOv = upLongNailOv ? MantisUpSlashHitboxDown : PlainUpSlashHitboxDown;
                // 未佩戴护符：矩形顶端再向下收缩 0.5 格（三角形一起下移）
                float supTopShrinkOv = upLongNailOv ? 0f : PlainUpSlashTopShrink;
                hy = UpSlashOffsetY + CellToUx(CharmEffects.UpSlashStretchUp() * 0.5f
                    - supDownOv * 0.5f
                    - supTopShrinkOv * 0.5f);
                size = new Vector2(UpSlashSizeX + supWideOv * 2f,
                    UpSlashSizeY + CharmEffects.UpSlashStretchUp() + supDownOv - supTopShrinkOv);
            }
            else if (_downSlash)
            {
                hx = DownSlashOffsetX;
                // 与碰撞体一致：基础向上拉伸 + 护符向下拉伸
                // 判定框附加：向上拉伸（长钉/骄傲印记 0.2 / 未佩戴 0.5）+ 左右拉伸（各 0.2）
                bool downLongNailOv = CharmEffects.LongNailVisual();
                float sdownWideOv = downLongNailOv ? MantisDownSlashHitboxWiden : PlainDownSlashHitboxWidenX;
                float sdownUpOv = downLongNailOv ? MantisDownSlashHitboxUp : PlainDownSlashHitboxUp;
                // 未佩戴护符：矩形底端再向上收缩 0.5 格（三角形一起上移）
                float sdownBottomShrinkOv = downLongNailOv ? 0f : PlainDownSlashBottomShrink;
                hy = DownSlashOffsetY + CellToUx(DownSlashStretchUp * 0.5f
                    - CharmEffects.DownSlashStretchDown() * 0.5f
                    + sdownUpOv * 0.5f
                    + sdownBottomShrinkOv * 0.5f);
                size = new Vector2(DownSlashSizeX + sdownWideOv * 2f,
                    DownSlashSizeY + DownSlashStretchUp
                    + CharmEffects.DownSlashStretchDown() + sdownUpOv - sdownBottomShrinkOv);
            }
            else
            {
                // 与 UpdateHitboxPosition/碰撞体一致：护符18/19 的长度/高度/平移/拉伸
                float lw = CharmEffects.LongRangeMultiplier();
                float lh = CharmEffects.LongRangeHeightMultiplier();
                float lshift = CellToUx(CharmEffects.LongRangeShift());
                float stretchShift = CellToUx(CharmEffects.LongRangeStretch() * 0.5f);
                hx = -_faceDir * (HitboxOffsetX + lshift) + _faceDir * stretchShift;
                hy = HitboxOffsetY;
                size = new Vector2(HitboxSizeX * lw + CharmEffects.LongRangeStretch(), HitboxSizeY * lh);
            }
            Vector2 center = _mp.gameObject.transform.TransformPoint(
                new Vector2(mx + hx, my + hy));
            // 陷阱（蛛丝球释放源）破坏框：比敌人命中框略宽松，覆盖剑气视觉范围。
            // 下劈不参与陷阱破坏（避免下劈把陷阱提前打掉）。
            if (!_downSlash)
            {
                DestroySpiderTrapsInBox(
                    new Vector2(X + hx, Y + hy), size.x * 0.5f + 0.7f, size.y * 0.5f + 0.4f);
            }
            Collider2D[] hits = Physics2D.OverlapBoxAll(
                center, size, 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null || c.GetComponentInParent<KnightAttackHitbox>() != null)
                {
                    // 跳过自己的判定框
                    continue;
                }
                // 蜘蛛蛛丝球：普攻可直接破坏
                if (c.gameObject != null && TryDestroyWebShot(c.gameObject))
                {
                    continue;
                }
                // 虫墙（Breakable_honey* / BugWall / Cocoon）：优先于陷阱判定，
                // 否则虫墙容易被危险格/标签误判成尖刺（只有“叮”声、不销毁、无粒子）
                if (IsBugWallCollider(c))
                {
                    if (_swingHits.Add(c))
                    {
                        HandleBugWallHit(c);
                    }
                    continue;
                }
                // 普通可破坏墙（M2BreakableWallMover，如 forest_secret_lake 的 Breakable_slake）：
                // 走 AIC 原版可破坏墙伤害管线（每次攻击扣 1 血，扣完自动 breakEffect），
                // 不触发虫墙的 Pogo/后坐力/音效逻辑。
                M2BreakableWallMover breakableWall = c.GetComponentInParent<M2BreakableWallMover>();
                if (breakableWall != null)
                {
                    if (_swingHits.Add(breakableWall))
                    {
                        DamageBreakableWall(breakableWall, false);
                    }
                    continue;
                }
                // 虫巢抓取体（M2WormTrap）：命中即按虫巢墙处理（转成格子坐标走虫巢逻辑）
                M2WormTrap wormTrap = c.GetComponentInParent<M2WormTrap>();
                if (wormTrap != null)
                {
                    if (_swingHits.Add(wormTrap))
                    {
                        HandleBugWallTrapHit(wormTrap);
                    }
                    continue;
                }
                // 魔法植物（魔力草 M2ManaWeed）：小骑士可破坏/采集（AIC 原生法力飞溅 + 重生计时）
                M2ManaWeed manaWeed = c.GetComponentInParent<M2ManaWeed>();
                if (manaWeed != null)
                {
                    if (_swingHits.Add(manaWeed))
                    {
                        HandleManaWeedHit(manaWeed);
                    }
                    continue;
                }
                // 尖刺/荆棘等陷阱：不扣血、不打怪，进入专属“陷阱反击”逻辑
                if (IsTrapCollider(c))
                {
                    if (_swingHits.Add(TrapSwingKey))
                    {
                        HandleTrapHit(); // 陷阱的“效果”是骑士的 Pogo/后坐力，一次挥砍只触发一次
                    }
                    continue;
                }
                NelEnemy enemy = c.GetComponentInParent<NelEnemy>();
                enemy = ResolveDamageTarget(enemy);
                if (enemy != null)
                {
                    NotifyAttackHit(enemy); // 内部按 enemy 实例去重
                    continue;
                }
                // 其他可攻击实体（的当て靶/拳炮/TD路障/事件可操作物等）：诺艾尔能打的小骑士也能打
                if (TryHitGenericAttackable(c, CharmEffects.ScaleNailDamage(SlashDamage), _swingHits, 0))
                {
                    // 友方拦截（如保卫战防御工事/友军）：不产生后坐力/弹起
                    if (!_attackHitFeedbackBlocked)
                    {
                        ApplyAttackHitFeedback(true);
                    }
                    continue;
                }
                bool isEnemy =
                    c.CompareTag("MoverEn") ||
                    c.gameObject.layer == LayerMask.NameToLayer("Enemy") ||
                    c.gameObject.layer == LayerMask.NameToLayer("EnemySelf") ||
                    c.gameObject.layer == LayerMask.NameToLayer("AttackHitable");
                if (isEnemy)
                {
                    NotifyAttackHit(null);
                    continue;
                }
            }

            // 格级危险检测（AIC 尖刺/荆棘通常由地图危险格标记，不一定有物理碰撞体）：
            // 把攻击判定框的世界坐标换算成地图格，扫描范围内是否存在危险格。
            float uxPerCell = 64f / _mp.CLEN; // 1 ux = 64 像素；1 格 = CLEN 像素
            float ccx = X + hx * uxPerCell;
            float ccy = Y - hy * uxPerCell;
            float scx = size.x * uxPerCell;
            float scy = size.y * uxPerCell;
            int cx0 = Mathf.FloorToInt(ccx - scx * 0.5f);
            int cx1 = Mathf.FloorToInt(ccx + scx * 0.5f);
            int cy0 = Mathf.FloorToInt(ccy - scy * 0.5f);
            int cy1 = Mathf.FloorToInt(ccy + scy * 0.5f);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (cx < 0 || cy < 0 || cx >= _mp.clms || cy >= _mp.rows)
                    {
                        continue;
                    }
                    // 只认真正的尖刺/荆棘（SPIKE/SPIKE_TOP config 或 spike/thorn 芯片），
                    // 不用 isDangerous：岩浆/雷电/水/激光危险位不应触发后坐力/弹起。
                    if (IsSpikeThornCell(cx, cy))
                    {
                        // 虫巢（worm/ 芯片）危险格 = 虫墙；其余危险格 = 尖刺/荆棘
                        if (IsWormNestCell(cx, cy))
                        {
                            HandleBugWallCellHit(cx, cy); // 内部按整面墙去重
                        }
                        else if (_swingHits.Add(TrapSwingKey))
                        {
                            HandleTrapHit(); // 尖刺 Pogo 一次挥砍只触发一次
                        }
                    }
                    else if (DownSlashTouchesDivePart() && _swingHits.Add(TrapSwingKey))
                    {
                        HandleTrapHit(); // 下劈骨剑/尖刺（含 PvP 远端）：回弹 + 音效，不消耗
                    }
                }
            }
        }

        // ================= 拼刀（PvP 骨钉对拼）=================
        // 规则：两名小骑士在使用普攻或骨钉技艺时，若两者的骨钉（剑气）判定箱相交 → 拼刀：
        //   ① 双方各自冻结 0.3 秒：动画、特效、键位（输入）、位移全部暂停；
        //   ② 双方各自获得 0.5 秒无敌（连续拼刀会重新计算，即刷新回满 0.5 秒）；
        //   ③ 播放一次“骨钉被弹开”的叮声（与劈中尖刺/荆棘同款 DashAudio.PlaySpikePogo）。
        // 音效去重：用“本次拼刀闩锁”保证同一段拼刀只结算一次 —— 判定到相交时置闩锁，
        // 直到出现“一帧不相交”才解除。0.3 秒冻结本身也是天然冷却（冻结期间不做判定），
        // 因此连续按住/长判定招式（旋风劈砍等）不会连续刷音效，而分开的两次拼刀各响一次。
        // 判定箱经联机特效负载（KnightFxSync v5 段）每帧同步，两端各自本地判定，
        // 因此“双方各自冻结 + 各得 0.5 秒无敌”不需要额外发包。
        private const float NailParryFreezeTime = 0.3f;     // 拼刀冻结时长（秒）
        private const float NailParryInvincibleTime = 0.5f; // 拼刀无敌时长（秒）
        private const byte NailParryKindSlash = 1;          // 普攻（平砍/上劈/下劈）
        private const byte NailParryKindGreatSlash = 2;     // 骨钉技艺·强力劈砍
        private const byte NailParryKindDashSlash = 3;      // 骨钉技艺·冲刺劈砍
        private const byte NailParryKindCyclone = 4;        // 骨钉技艺·旋风劈砍
        private float _nailParryFreezeTimer;   // >0：本帧处于拼刀冻结（暂停动画/特效/键位/位移）
        private bool _nailParryLatched;        // true：当前这段拼刀已结算过（避免重复播音）
        private readonly List<Vector4> _nailParrySelfRects = new List<Vector4>(4);
        private readonly List<byte> _nailParrySelfKinds = new List<byte>(4);

        /// <summary>
        /// 拼刀检测（每帧一次，放在普攻/技艺判定推进之后）。
        /// 本地正在使用普攻或骨钉技艺、且与远端小骑士的骨钉判定箱相交时触发。
        /// 同一段持续交叠只结算一次（闩锁），避免音效连播。
        /// </summary>
        private void UpdateNailParry()
        {
            if (_nailParryFreezeTimer > 0f)
            {
                return; // 冻结期间不判定（冻结结束前不会重复触发/重复播音）
            }
            if (_mp == null || !_active || _isDead || _hurt || _isSitting || _sitStandingUp ||
                _respawnFadeOut)
            {
                return;
            }
            try
            {
                CollectNailAttackRects(_nailParrySelfRects);
                bool overlapped = false;
                for (int i = 0; i < _nailParrySelfRects.Count; i++)
                {
                    Vector4 r = _nailParrySelfRects[i];
                    if (MultiplayerCompat.OverlapsRemoteNailRect(r.x, r.y, r.z, r.w))
                    {
                        overlapped = true;
                        if (!_nailParryLatched)
                        {
                            TriggerNailParry(i);
                        }
                        break;
                    }
                }
                if (!overlapped)
                {
                    // 判定箱分开了：解除闩锁，下一次相交才算新的一次拼刀（各自播一次音效）
                    _nailParryLatched = false;
                }
            }
            catch (Exception)
            {
                // 过图/地图销毁瞬间的异常兜底：拼刀失败不影响本体逻辑
            }
        }

        /// <summary>
        /// 触发拼刀：双方（两端各自本地执行）冻结 0.3 秒 + 无敌刷新为 0.5 秒 + 弹刀音效（一次）。
        /// </summary>
        private void TriggerNailParry(int rectIndex)
        {
            _nailParryLatched = true;
            _nailParryFreezeTimer = NailParryFreezeTime;
            // 连续拼刀：无敌时间重新计算（直接刷新为 0.5 秒，而不是叠加）
            _invincibleTimer = Mathf.Max(_invincibleTimer, NailParryInvincibleTime);
            // 与“下劈尖刺/荆棘”同款音效（HK sword_hit_reject：骨钉被弹开的叮声）
            DashAudio.PlaySpikePogo();
        }

        /// <summary>
        /// 采集当前生效的骨钉（剑气）判定箱，输出绝对地图格坐标 (cx, cy, w, h)：
        /// ① 普攻：命中窗口内直接读“矩形 + 尖端”真实碰撞体的世界包围盒；
        /// ② 骨钉技艺：与 CheckNailArtHit / CheckDashSlashHit / CheckCycloneHit 完全同源
        ///    （中心是格、尺寸是世界单位，二者分别换算后统一成格）。
        /// 单位换算一律走 Map2d 的 uxToMapx/uyToMapy，不对世界单位做任何假设。
        /// </summary>
        private void CollectNailAttackRects(List<Vector4> outRects, List<byte> outKinds = null)
        {
            outRects.Clear();
            outKinds?.Clear();
            if (_mp == null)
            {
                return;
            }
            if (_attacking && _hitboxGo != null && _hitboxActiveTimer > 0f && _hitboxColliders != null)
            {
                if (TryCollidersToCellRect(_hitboxColliders, out Vector4 slashRect))
                {
                    outRects.Add(slashRect);
                    outKinds?.Add(NailParryKindSlash);
                }
            }
            if (_nailArtSlashing)
            {
                AddNailArtCellRect(outRects, X + _nailArtSlashDir * NailArtHitboxOffX,
                    Y + NailArtHitboxOffY, NailArtHitboxW, NailArtHitboxH, NailParryKindGreatSlash, outKinds);
            }
            if (_dashSlashing)
            {
                AddNailArtCellRect(outRects, X + _dashSlashDir * DashSlashHitboxOffX,
                    Y + DashSlashHitboxOffY, DashSlashHitboxW, DashSlashHitboxH, NailParryKindDashSlash, outKinds);
            }
            if (_cycloneSlashing)
            {
                AddNailArtCellRect(outRects, X, Y + CycloneSlashHitboxOffY,
                    CycloneSlashHitboxW, CycloneSlashHitboxH, NailParryKindCyclone, outKinds);
            }
        }

        /// <summary>多个碰撞体的世界包围盒合并 → 地图格矩形。</summary>
        private bool TryCollidersToCellRect(Collider2D[] cols, out Vector4 rect)
        {
            rect = default;
            if (_mp == null || cols == null)
            {
                return false;
            }
            bool has = false;
            Bounds b = default;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider2D c = cols[i];
                if (c == null)
                {
                    continue;
                }
                if (!has)
                {
                    b = c.bounds;
                    has = true;
                }
                else
                {
                    b.Encapsulate(c.bounds);
                }
            }
            return has && TryWorldRectToCellRect(b.min, b.max, out rect);
        }

        /// <summary>
        /// 世界矩形（左下 + 右上）→ 地图格矩形：世界点先转地图本地(ux)，再用 Map2d 转格坐标。
        /// 这样即使地图根节点带缩放/偏移，也不需要在模组里假设“1 世界单位 = 多少格”。
        /// </summary>
        private bool TryWorldRectToCellRect(Vector3 wMin, Vector3 wMax, out Vector4 rect)
        {
            rect = default;
            if (_mp == null)
            {
                return false;
            }
            Transform mapT = _mp.gameObject.transform;
            Vector3 p0 = mapT.InverseTransformPoint(new Vector3(wMin.x, wMin.y, 0f));
            Vector3 p1 = mapT.InverseTransformPoint(new Vector3(wMax.x, wMax.y, 0f));
            float x0 = _mp.uxToMapx(p0.x);
            float x1 = _mp.uxToMapx(p1.x);
            float y0 = _mp.uyToMapy(p0.y);
            float y1 = _mp.uyToMapy(p1.y);
            float w = Mathf.Abs(x1 - x0);
            float h = Mathf.Abs(y1 - y0);
            if (w <= 0.0001f || h <= 0.0001f)
            {
                return false;
            }
            rect = new Vector4((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, w, h);
            return true;
        }

        /// <summary>骨钉技艺判定箱（中心：格；尺寸：世界单位，与 Physics2D.OverlapBoxAll 同源）→ 格矩形。</summary>
        private void AddNailArtCellRect(List<Vector4> outRects, float centerCellX, float centerCellY,
            float worldW, float worldH, byte kind, List<byte> outKinds)
        {
            if (_mp == null || worldW <= 0.0001f || worldH <= 0.0001f)
            {
                return;
            }
            Transform mapT = _mp.gameObject.transform;
            Vector3 c = mapT.TransformPoint(new Vector3(
                _mp.pixel2ux(centerCellX * _mp.CLEN), _mp.pixel2uy(centerCellY * _mp.CLEN), 0f));
            if (TryWorldRectToCellRect(
                new Vector3(c.x - worldW * 0.5f, c.y - worldH * 0.5f, 0f),
                new Vector3(c.x + worldW * 0.5f, c.y + worldH * 0.5f, 0f),
                out Vector4 r))
            {
                outRects.Add(r);
                outKinds?.Add(kind);
            }
        }

        /// <summary>
        /// 虫墙识别：AIC 的虫墙是可破坏的蜂巢墙（对象名 Breakable_honey*），
        /// 同时兼容 BugWall / Cocoon 标签占位（用字符串比较，避免未定义标签报错）。
        /// </summary>
        private bool IsBugWallCollider(Collider2D c)
        {
            if (c == null)
            {
                return false;
            }
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++)
            {
                string tag = t.tag;
                string name = t.name;
                if (tag == "BugWall" || tag == "Cocoon" ||
                    name.StartsWith("Breakable_honey", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("Breakable_honey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (name.IndexOf("honey", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     name.IndexOf("breakable", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// 判断碰撞体是否属于尖刺/荆棘等陷阱（按标签或图层匹配）。
        /// </summary>
        private bool IsTrapCollider(Collider2D c)
        {
            if (c == null)
            {
                return false;
            }
            // AIC 的尖刺载具（CM-Carrier_*）：带 M2PuzzCarrierMover 组件的物体视为尖刺陷阱
            if (c.GetComponentInParent<M2PuzzCarrierMover>() != null)
            {
                return true;
            }
            // 检查自身与父级链上的标签（尖刺本体可能在父对象上）
            // 注意：AIC 项目里并没有 Spike/Thorn 等标签，不能用 CompareTag（会报 Tag is not defined），
            // 这里用字符串比较，未定义时只是不匹配、不会报错。
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++)
            {
                string tag = t.tag;
                if (tag == "Spike" || tag == "Spikes" ||
                    tag == "Thorn" || tag == "Thorns" ||
                    tag == "Trap" || tag == "Kusari" ||
                    tag == "Needle")
                {
                    return true;
                }
                t = t.parent;
            }
            // 图层匹配
            int layer = c.gameObject.layer;
            int layerTrap = LayerMask.NameToLayer("Trap");
            int layerKusari = LayerMask.NameToLayer("Kusari");
            int layerSpike = LayerMask.NameToLayer("Spike");
            int layerSpikes = LayerMask.NameToLayer("Spikes");
            int layerThorn = LayerMask.NameToLayer("Thorn");
            return layer == layerTrap || layer == layerKusari ||
                   layer == layerSpike || layer == layerSpikes || layer == layerThorn;
        }

        /// <summary>
        /// 陷阱（尖刺/荆棘）命中：
        /// 不调用 enemy.applyDamage、不给尖刺扣血、不触发怪物受击特效；
        /// 下劈 → 复用怪物 Pogo 物理（弹跳）；横劈/上劈 → 轻微后坐力；
        /// 播放专属“叮”音效（spike_pogo）。
        /// </summary>
        /// <summary>下劈是否碰到骨剑/尖刺（本地或 PvP 远端）；命中只回弹，不消耗它们。</summary>
        private bool DownSlashTouchesDivePart()
        {
            if (!_downSlash)
            {
                return false;
            }
            float cx = X + HurtCenterX;
            float cy = Y + HurtCenterY;
            // 略微放宽（原 +1.2 过松，离尖刺一段距离也会误触发）；0.4 格手感够灵敏且不误判
            float hw = HurtSizeX + 0.4f;
            float hh = HurtSizeY + 0.4f;
            for (int i = 0; i < _diveSwords.Count; i++)
            {
                DiveSword s = _diveSwords[i];
                if (s.Height > 0.03f &&
                    Mathf.Abs(s.X + DiveSwordRenderOffX + DiveSwordHitboxOffX - cx) <= s.Width * 0.5f + hw &&
                    Mathf.Abs((s.BaseY - s.Height * 0.5f + DiveSwordHitboxOffY) - cy) <= s.Height * 0.5f + hh)
                {
                    return true;
                }
            }
            for (int i = 0; i < _diveSpikes.Count; i++)
            {
                DiveSpike p = _diveSpikes[i];
                if (p.Height > 0.02f &&
                    Mathf.Abs(p.X + DiveSpikeHitboxOffX - cx) <= DiveSpikeBaseWidth * 0.5f + hw &&
                    Mathf.Abs((p.BaseY - p.Height * 0.5f + DiveSpikeHitboxOffY) - cy) <= p.Height * 0.5f + hh)
                {
                    return true;
                }
            }
            bool remoteHit = MultiplayerCompat.OverlapsRemoteDiveRect(cx, cy, hw, hh) ||
                MultiplayerCompat.OverlapsRemoteElegyRect(cx, cy, hw, hh);
            return remoteHit;
        }

        private void HandleTrapHit()
        {
            DashAudio.PlaySpikePogo(); // 专属音效（资源未嵌入前静默跳过）
            if (_downSlash)
            {
                // 复用“怪物 Pogo”的物理逻辑，但弹起高度为怪物的 1.5 倍
                Vy = PogoBounceVyScene; // 下劈场景物反弹初速（弹起约 2.1 格）
                _pogoGravityLock = 0.05f; // 极短时间屏蔽重力
                Grounded = false;
                _jumpHeld = false;
                // 刷新：下劈尖刺/荆棘同样恢复空中冲刺和二段跳资格（Reset 机制）
                _canDoubleJump = true;
                _canDash = true;
                _dashCooldown = 0f;
            }
            else if (!_upSlash)
            {
                // 仅横劈命中陷阱：与打中怪物相同的平滑后坐力（上劈无后坐力）
                // 护符15 稳定之体：普攻命中不产生后坐力
                if (!CharmEffects.IsEquipped(CharmEffects.StableId))
                {
                    _recoilVx = _faceDir * (RecoilDistance * 2f / RecoilTime);
                    _recoilTimer = RecoilTime;
                }
            }
        }

        /// <summary>
        /// 虫墙命中：同一面墙累计 2 次攻击后销毁。
        /// 每次命中都播放受击音效与白色散落粒子；
        /// 下劈 → 与怪物/尖刺完全相同的 Pogo 弹跳并刷新冲刺/二段跳；横劈/上劈 → 平滑后坐力。
        /// </summary>
        private void HandleBugWallHit(Collider2D wallCollider)
        {
            DashAudio.PlayEnemyHit(); // 受击音效
            // 白色散落点粒子（复用 NotifyAttackHit 的 pr_cane_hit 逻辑）
            try
            {
                Map2d mp = _mp;
                if (mp != null && wallCollider != null)
                {
                    Vector3 localPos = mp.gameObject.transform.InverseTransformPoint(wallCollider.transform.position);
                    float uxPerCell = 64f / mp.CLEN; // 1 ux = 64 像素；1 格 = CLEN 像素
                    mp.PtcSTsetVar("hit_x", (double)(localPos.x * uxPerCell + mp.clms * 0.5));
                    mp.PtcSTsetVar("hit_y", (double)(mp.rows * 0.5 - localPos.y * uxPerCell));
                    mp.PtcSTsetVar("ax", 0.0); // pr_cane_hit 强依赖 ax，缺失会崩溃
                    mp.PtcST("pr_cane_hit", null, (XX.PTCThread.StFollow)0);
                }
            }
            catch (Exception)
            {
            }
            // 计数器：第 2 次攻击摧毁虫墙
            if (wallCollider != null)
            {
                int hits;
                _bugWallHits.TryGetValue(wallCollider, out hits);
                hits++;
                if (hits >= 2)
                {
                    _bugWallHits.Remove(wallCollider);
                    GameObject.Destroy(wallCollider.gameObject);
                    KnightAddSoul(30); // 破坏虫墙获得 30 灵魂
                }
                else
                {
                    _bugWallHits[wallCollider] = hits;
                }
            }
            // 物理反馈
            if (_downSlash)
            {
                // 与尖刺等场景物相同的 Pogo 弹跳 + 刷新（高度为打怪物的 1.5 倍）
                Vy = PogoBounceVyScene;
                _pogoGravityLock = 0.05f;
                Grounded = false;
                _jumpHeld = false;
                _canDoubleJump = true;
                _canDash = true;
                _dashCooldown = 0f;
            }
            else if (!_upSlash)
            {
                // 仅横劈：与打怪物相同的平滑后坐力（上劈虫墙无后坐力）
                // 护符15 稳定之体：普攻命中不产生后坐力
                if (!CharmEffects.IsEquipped(CharmEffects.StableId))
                {
                    _recoilVx = _faceDir * (RecoilDistance * 2f / RecoilTime);
                    _recoilTimer = RecoilTime;
                }
            }
        }

        /// <summary>
        /// 判断某个地图格是否是虫巢（虫墙）：格上存在 worm/ 前缀的芯片（wormnest / insect / front_worm_nest）。
        /// </summary>
        private bool IsWormNestCell(int cx, int cy)
        {
            try
            {
                var puts = new List<m2d.M2Puts>();
                _mp.getPointPutsTo(cx, cy, false, puts, -1, null);
                for (int i = 0; i < puts.Count; i++)
                {
                    string src = puts[i] != null ? puts[i].src : null;
                    if (src != null && src.IndexOf("worm/", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 判断某个地图格是否为尖刺/荆棘：config 为 SPIKE(125)/SPIKE_TOP(155)，
        /// 或格上存在名字含 spike/thorn 的芯片。
        /// 不用 isDangerous：那会把岩浆/雷电/水/激光危险位也算进去，导致“空刀”也触发后坐力/弹起。
        /// </summary>
        private bool IsSpikeThornCell(int cx, int cy)
        {
            try
            {
                if (_mp == null || cx < 0 || cy < 0 || cx >= _mp.clms || cy >= _mp.rows)
                {
                    return false;
                }
                int cfg = _mp.getConfig(cx, cy);
                if (cfg == CCON.SPIKE || cfg == CCON.SPIKE_TOP)
                {
                    return true;
                }
                var puts = new List<m2d.M2Puts>();
                _mp.getPointPutsTo(cx, cy, false, puts, -1, null);
                for (int i = 0; i < puts.Count; i++)
                {
                    string src = puts[i] != null ? puts[i].src : null;
                    if (src != null &&
                        (src.IndexOf("spike", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         src.IndexOf("thorn", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 在图层 Lp 容器里查找覆盖指定格子的 M2LpBreakable（AIC 可破坏墙的处理器）。
        /// </summary>
        private M2LpBreakable FindBreakableAt(int cx, int cy)
        {
            try
            {
                M2MapLayer[] layers = _mp.getLayerArray();
                if (layers != null)
                {
                    for (int li = 0; li < layers.Length; li++)
                    {
                        M2LabelPointContainer lp = layers[li] != null ? layers[li].LP : null;
                        if (lp == null)
                        {
                            continue;
                        }
                        for (int j = 0; j < lp.Length; j++)
                        {
                            M2LabelPoint pt = lp.Get(j);
                            if (pt is M2LpBreakable bl && bl.isContainingMapXy(cx, cy, cx, cy, 0f))
                            {
                                return bl;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>
        /// 虫巢格（虫墙）命中：与碰撞体虫墙相同的反馈（音效/粒子/Pogo或后坐力），
        /// 同一格累计 2 次后隐藏该区域内的全部虫巢芯片（AIC 芯片的 activeRemoveKey 机制，进出房间自动恢复）。
        /// forceBreak=true（法术破坏）：跳过计数，一次性摧毁并 +50 灵魂。
        /// </summary>
        private void HandleBugWallCellHit(int cx, int cy, bool forceBreak = false)
        {
            // 整面墙去重：同一面虫巢墙一次挥砍只计一次（按 Lp 或墙簇 key）
            M2LpBreakable lp = FindBreakableAt(cx, cy);
            int minX = cx, minY = cy, maxX = cx, maxY = cy;
            object dedupKey = lp != null
                ? (object)lp
                : (object)GetWormClusterKey(cx, cy, out minX, out minY, out maxX, out maxY);
            if (!_swingHits.Add(dedupKey))
            {
                return;
            }
            DashAudio.PlayEnemyHit();
            // 白色散落点粒子（复用 NotifyAttackHit 的 pr_cane_hit 逻辑；hit_x/hit_y 用地图格坐标）
            try
            {
                _mp.PtcSTsetVar("hit_x", (double)cx);
                _mp.PtcSTsetVar("hit_y", (double)cy);
                _mp.PtcSTsetVar("ax", 0.0);
                _mp.PtcST("pr_cane_hit", null, (XX.PTCThread.StFollow)0);
            }
            catch (Exception)
            {
            }
            // 计数：优先按整面虫巢墙（M2LpBreakable）计，2 次后调用 AIC 原版破坏（隐藏整墙芯片+重算配置）
            if (lp != null)
            {
                int hits;
                _bugWallLpHits.TryGetValue(lp, out hits);
                bool destroyNow = forceBreak || hits + 1 >= 2;
                if (destroyNow)
                {
                    _bugWallLpHits.Remove(lp);
                    try
                    {
                        lp.pastingChipManual(false); // 与 AIC 原版破坏可破坏墙同一机制
                        KnightAddSoul(30); // 破坏虫墙获得 30 灵魂
                    }
                    catch (Exception)
                    {
                    }
                }
                else
                {
                    _bugWallLpHits[lp] = hits + 1;
                }
            }
            else
            {
                // 兜底：找不到 Breakable Lp 时按“整面虫巢墙（连通簇）”计数，
                // 2 次后按整墙边界移除全部虫巢芯片并清危险标记
                long key = ((long)minY << 20) | (long)minX;
                int hits;
                _bugWallCellHits.TryGetValue(key, out hits);
                bool destroyNow = forceBreak || hits + 1 >= 2;
                if (destroyNow)
                {
                    _bugWallCellHits.Remove(key);
                    // 只打掉命中点所在的那条虫巢链的蠕虫（巢体与其余链保留）
                    KillWormChain(cx, cy, minX, minY, maxX, maxY);
                    KnightAddSoul(30); // 破坏虫墙获得 30 灵魂
                }
                else
                {
                    _bugWallCellHits[key] = hits + 1;
                }
            }
            // 物理反馈（与碰撞体虫墙一致）：仅普通骨钉挥砍触发。
            // 深渊尖啸/下砸/骨钉技艺等范围破坏不应有后坐力/Pogo 弹跳。
            if (_attacking)
            {
                if (_downSlash)
                {
                    Vy = PogoBounceVyScene;
                    _pogoGravityLock = 0.05f;
                    Grounded = false;
                    _jumpHeld = false;
                    _canDoubleJump = true;
                    _canDash = true;
                    _dashCooldown = 0f;
                }
                else if (!_upSlash)
                {
                    // 仅横劈：与打怪物相同的平滑后坐力（上劈虫墙无后坐力）
                    // 护符15 稳定之体：普攻命中不产生后坐力
                    if (!CharmEffects.IsEquipped(CharmEffects.StableId))
                    {
                        _recoilVx = _faceDir * (RecoilDistance * 2f / RecoilTime);
                        _recoilTimer = RecoilTime;
                    }
                }
            }
        }

        /// <summary>
        /// 命中虫巢抓取体（M2WormTrap）时，把它的世界坐标换算成地图格，复用虫巢格逻辑。
        /// </summary>
        private void HandleBugWallTrapHit(M2WormTrap wormTrap)
        {
            try
            {
                Vector3 localPos = _mp.gameObject.transform.InverseTransformPoint(wormTrap.transform.position);
                float uxPerCell = 64f / _mp.CLEN;
                int cx = Mathf.FloorToInt(localPos.x * uxPerCell + _mp.clms * 0.5f);
                int cy = Mathf.FloorToInt(_mp.rows * 0.5f - localPos.y * uxPerCell);
                HandleBugWallCellHit(cx, cy);
            }
            catch (Exception)
            {
                DashAudio.PlayEnemyHit();
                if (_downSlash)
                {
                    Vy = PogoBounceVyScene;
                    _pogoGravityLock = 0.05f;
                    Grounded = false;
                    _jumpHeld = false;
                    _canDoubleJump = true;
                    _canDash = true;
                    _dashCooldown = 0f;
                }
                else if (!_upSlash)
                {
                    // 仅横劈：与打怪物相同的平滑后坐力（上劈虫墙无后坐力）
                    // 护符15 稳定之体：普攻命中不产生后坐力
                    if (!CharmEffects.IsEquipped(CharmEffects.StableId))
                    {
                        _recoilVx = _faceDir * (RecoilDistance * 2f / RecoilTime);
                        _recoilTimer = RecoilTime;
                    }
                }
            }
        }

        /// <summary>
        /// 小骑士破坏/采集魔法植物（M2ManaWeed）：
        /// 用诺艾尔（PR）作为攻击者调用 AIC 原生 ApplyDamageFrom——
        /// 触发 break_weed 粒子、法力飞溅、枯萎计时，时间到后自动重生。
        /// </summary>
        /// <summary>小骑士破坏/采集魔法植物（M2ManaWeed）。返回是否真正破坏成功。</summary>
        private bool HandleManaWeedHit(M2ManaWeed manaWeed)
        {
            try
            {
                if (manaWeed == null || !IsManaWeedReady(manaWeed))
                {
                    float dt = -999f;
                    try
                    {
                        if (ManaWeedTimeField != null)
                        {
                            dt = (float)ManaWeedTimeField.GetValue(manaWeed);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    return false; // 已枯萎/正在重生（未完全长成），不再重复采集
                }
                PRNoel noel = GetPr();
                if (noel == null || _mp == null)
                {
                    return false;
                }
                Vector3 localPos = _mp.gameObject.transform.InverseTransformPoint(manaWeed.transform.position);
                float uxPerCell = 64f / _mp.CLEN;
                float wx = localPos.x * uxPerCell + _mp.clms * 0.5f;
                float wy = _mp.rows * 0.5f - localPos.y * uxPerCell;
                var atk = new NelAttackInfo();
                atk.Caster = noel;
                atk.AttackFrom = noel;
                // 下砸骨剑/尖刺等命中点可能远离骑士中心（骨剑 ±2 格、尖刺 ±11 格），
                // 而 M2ManaWeed.ApplyDamageFrom 有“攻击者位置 ±(sizex+2)”的距离判定。
                // 临时把诺艾尔放到草的位置确保判定通过（她隐藏且每帧会被同步回骑士）。
                noel.setTo(wx, wy);
                int r = manaWeed.ApplyDamageFrom(noel, true, wx, wy, wx, wy, atk);
                if (r > 0)
                {
                    // 只有真正破坏才获得灵魂
                    // 护符2 蜂群集结：破坏魔力草获得的灵魂变为 5（原 4）
                    KnightAddSoul(CharmEffects.IsEquipped(CharmEffects.CollectorId) ? 5 : 4);
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 魔力草是否“完全长成”可采集：直读 AIC 私有 time 字段，仅 time==0 时允许。
        /// time&gt;0 = 枯萎中（已破坏，等重生）；time&lt;0 = 重生动画中（看起来还没长出来）。
        /// 这两个阶段都不能再次破坏/加魂。
        /// </summary>
        private bool IsManaWeedReady(M2ManaWeed manaWeed)
        {
            try
            {
                if (manaWeed == null)
                {
                    return false;
                }
                if (ManaWeedTimeField != null)
                {
                    float t = (float)ManaWeedTimeField.GetValue(manaWeed);
                    return t == 0f;
                }
                return manaWeed.isActiveMana();
            }
            catch (Exception)
            {
                return manaWeed != null && manaWeed.isActiveMana();
            }
        }

        /// <summary>
        /// 破坏/采集魔力草：以 (centerX, centerY) 为中心、半宽 halfW/半高 halfH 的矩形内，
        /// 所有 M2ManaWeed 均按“破坏/采集”处理（+4 灵魂，触发法力飞溅与重生计时）。
        /// dedup 用于同一招式内去重（黑暗降临/深渊尖啸各用独立集合）。
        /// </summary>
        private void BreakManaWeeds(HashSet<object> dedup, float centerX, float centerY, float halfW, float halfH)
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetAreaObjectMask();
            if (mask == 0)
            {
                return;
            }
            float mx = _mp.pixel2ux(centerX * _mp.CLEN);
            float my = _mp.pixel2uy(centerY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(halfW * 2f, halfH * 2f), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                M2ManaWeed manaWeed = c.GetComponentInParent<M2ManaWeed>();
                if (manaWeed == null || dedup.Contains(manaWeed))
                {
                    continue;
                }
                // 只有真正破坏成功才占去重位，避免“占了位却没破坏，后续再也打不到”的问题
                if (HandleManaWeedHit(manaWeed))
                {
                    dedup.Add(manaWeed);
                }
            }
        }

        /// <summary>与普攻兜底检测同一套宽掩码：魔力草/虫墙等可破坏物的碰撞体可能分布在多个层。</summary>
        private int GetAreaObjectMask()
        {
            int mask = LayerMask.GetMask("EnemySelf", "Enemy", "AttackHitable");
            int trapLayer = LayerMask.NameToLayer("Trap");
            if (trapLayer >= 0)
            {
                mask |= 1 << trapLayer;
            }
            int kusariLayer = LayerMask.NameToLayer("Kusari");
            if (kusariLayer >= 0)
            {
                mask |= 1 << kusariLayer;
            }
            int spikeLayer = LayerMask.NameToLayer("Spike");
            if (spikeLayer >= 0)
            {
                mask |= 1 << spikeLayer;
            }
            int chipsLayer = LayerMask.NameToLayer("Chips");
            if (chipsLayer >= 0)
            {
                mask |= 1 << chipsLayer;
            }
            int chipsUColLayer = LayerMask.NameToLayer("ChipsUCol");
            if (chipsUColLayer >= 0)
            {
                mask |= 1 << chipsUColLayer;
            }
            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer >= 0)
            {
                mask |= 1 << defaultLayer;
            }
            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer >= 0)
            {
                mask |= 1 << waterLayer;
            }
            int transparentLayer = LayerMask.NameToLayer("TransparentFX");
            if (transparentLayer >= 0)
            {
                mask |= 1 << transparentLayer;
            }
            int ignoreRayLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRayLayer >= 0)
            {
                mask |= 1 << ignoreRayLayer;
            }
            return mask;
        }

        /// <summary>
        /// 对 AIC 普通可破坏墙（M2BreakableWallMover，如 forest_secret_lake 的 Breakable_slake）
        /// 造成一次伤害：走 AIC 原版可破坏墙伤害管线（扣血/受击白闪/扣完自动 breakEffect），
        /// 与诺艾尔攻击该墙同一入口。forceBreak=true 时一次扣满直接破坏。
        /// </summary>
        private bool DamageBreakableWall(M2BreakableWallMover mover, bool forceBreak = false)
        {
            try
            {
                if (mover == null || !mover.is_alive || !mover.damage_applyable)
                {
                    return false;
                }
                var atk = new NelAttackInfo();
                atk.hpdmg_current = 1;
                atk.hpdmg0 = 1;
                atk.fix_damage = true;
                atk.CenterXy(mover.x, mover.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                // 普攻标记（is_normal_attack=true）：check_damage 对普通墙放行，且单次只扣 1 血
                atk.PublishMagic = GetKnightAttackMagic();
                int res = mover.applyHpDamage(forceBreak ? mover.get_maxhp() : 1, forceBreak, atk);
                if (res > 0)
                {
                    DashAudio.PlayEnemyHit();
                }
                return res > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 法术范围破坏虫墙：范围内碰撞体虫墙一次摧毁（+50 灵魂），
        /// 虫巢格（worm/ 芯片）墙按格强制破坏（+50 灵魂）；普通可破坏墙
        /// （M2BreakableWallMover）则按 AIC 原版伤害管线扣血。dedup 与魔力草共用同一招式集合。
        /// </summary>
        private void BreakBugWallsInArea(HashSet<object> dedup, float centerX, float centerY, float halfW, float halfH)
        {
            if (_mp == null)
            {
                return;
            }
            int mask = GetAreaObjectMask();
            if (mask == 0)
            {
                return;
            }
            // 碰撞体虫墙（Breakable_honey* / BugWall / Cocoon）：一次摧毁
            float mx = _mp.pixel2ux(centerX * _mp.CLEN);
            float my = _mp.pixel2uy(centerY * _mp.CLEN);
            Vector2 center = _mp.gameObject.transform.TransformPoint(new Vector2(mx, my));
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, new Vector2(halfW * 2f, halfH * 2f), 0f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                // 虫墙优先：保持既有“一次摧毁 +50 灵魂”逻辑不变
                if (IsBugWallCollider(c))
                {
                    if (dedup.Add(c))
                    {
                        try
                        {
                            DashAudio.PlayEnemyHit();
                            GameObject.Destroy(c.gameObject);
                            KnightAddSoul(30); // 破坏虫墙获得 30 灵魂
                        }
                        catch (Exception)
                        {
                        }
                    }
                    continue;
                }
                // 普通可破坏墙（M2BreakableWallMover）：按 AIC 原版伤害管线扣血
                M2BreakableWallMover breakableWall = c.GetComponentInParent<M2BreakableWallMover>();
                if (breakableWall != null)
                {
                    if (dedup.Add(breakableWall))
                    {
                        DamageBreakableWall(breakableWall, false);
                    }
                    continue;
                }
            }
            // 虫巢格墙：遍历范围内格子，命中则强制破坏
            int cx0 = Mathf.FloorToInt(centerX - halfW);
            int cx1 = Mathf.FloorToInt(centerX + halfW);
            int cy0 = Mathf.FloorToInt(centerY - halfH);
            int cy1 = Mathf.FloorToInt(centerY + halfH);
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long key = ((long)cy << 20) | (long)cx;
                    if (!dedup.Add(key))
                    {
                        continue;
                    }
                    // 只有真正的虫墙才强制破坏：真虫墙 = 可破坏墙处理器（M2LpBreakable）
                    // 或实心格或危险格；虫巢地板/装饰三者都不是，跳过，避免误 +30 灵魂。
                    if (IsWormNestCell(cx, cy))
                    {
                        bool dangerCell = _mp.isDangerous(cx, cy);
                        bool blockCell = IsBlockCell(cx, cy);
                        bool hasLp = FindBreakableAt(cx, cy) != null;
                        bool isWall = dangerCell || blockCell || hasLp;
                        if (isWall)
                        {
                            HandleBugWallCellHit(cx, cy, true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 从命中格出发做连通 BFS，收集整面虫巢墙（worm/ 芯片）的边界，
        /// 返回以“墙左上角格”为键的稳定标识（同一面墙砍哪里都累计到同一个计数）。
        /// </summary>
        private long GetWormClusterKey(int cx, int cy, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = cx;
            minY = cy;
            maxX = cx;
            maxY = cy;
            try
            {
                var queue = new Queue<long>();
                var visited = new HashSet<long>();
                long startKey = ((long)cy << 20) | (long)cx;
                queue.Enqueue(startKey);
                visited.Add(startKey);
                // 虫巢墙本身是连通簇，BFS 会自然结束，不需要硬编码上限
                while (queue.Count > 0)
                {
                    long k = queue.Dequeue();
                    int x = (int)(k & 0xFFFFF);
                    int y = (int)(k >> 20);
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    int[] dxs = { 1, -1, 0, 0 };
                    int[] dys = { 0, 0, 1, -1 };
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = x + dxs[i];
                        int ny = y + dys[i];
                        if (nx < 0 || ny < 0 || nx >= _mp.clms || ny >= _mp.rows)
                        {
                            continue;
                        }
                        long nk = ((long)ny << 20) | (long)nx;
                        if (visited.Contains(nk))
                        {
                            continue;
                        }
                        if (IsWormNestCell(nx, ny))
                        {
                            visited.Add(nk);
                            queue.Enqueue(nk);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return ((long)minY << 20) | (long)minX;
        }

        /// <summary>
        /// 打掉命中点所在的那条虫巢链的蠕虫（保留巢体与其余链）：
        /// 蠕虫的抓取体/动画只挂在“链条起点”的虫巢头芯片上，所以必须沿虫巢头连通链找到整条链，
        /// 对链上每个头芯片调用 closeAction(false, true)——移除蠕虫动画与抓取体，保留巢体绘制。
        /// 进出房间时芯片重新初始化，蠕虫自动复原。
        /// </summary>
        private void KillWormChain(int cx, int cy, int l, int t, int r, int b)
        {
            try
            {
                // 1) 收集墙簇内所有虫巢头芯片，并挑离命中格最近的一个作为种子
                var heads = new List<NelChipWormHead>();
                NelChipWormHead seed = null;
                int bestDist = int.MaxValue;
                for (int y = t; y <= b; y++)
                {
                    for (int x = l; x <= r; x++)
                    {
                        if (x < 0 || y < 0 || x >= _mp.clms || y >= _mp.rows)
                        {
                            continue;
                        }
                        var puts = new List<m2d.M2Puts>();
                        _mp.getPointPutsTo(x, y, false, puts, -1, null);
                        for (int i = 0; i < puts.Count; i++)
                        {
                            if (puts[i] is NelChipWormHead wh)
                            {
                                heads.Add(wh);
                                int d = Mathf.Abs(x - cx) + Mathf.Abs(y - cy);
                                if (d < bestDist)
                                {
                                    bestDist = d;
                                    seed = wh;
                                }
                            }
                        }
                    }
                }
                if (seed == null)
                {
                    return;
                }
                // 2) 从种子沿“虫巢头芯片”4-连通做 BFS，收集同一条链的全部头芯片
                var visited = new HashSet<long>();
                var queue = new Queue<long>();
                long startKey = ((long)seed.mapy << 20) | (long)seed.mapx;
                queue.Enqueue(startKey);
                visited.Add(startKey);
                var chainHeads = new List<NelChipWormHead>();
                while (queue.Count > 0)
                {
                    long k = queue.Dequeue();
                    int x = (int)(k & 0xFFFFF);
                    int y = (int)(k >> 20);
                    for (int i = 0; i < heads.Count; i++)
                    {
                        if (heads[i].mapx == x && heads[i].mapy == y)
                        {
                            chainHeads.Add(heads[i]);
                            break;
                        }
                    }
                    int[] dxs = { 1, -1, 0, 0 };
                    int[] dys = { 0, 0, 1, -1 };
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = x + dxs[i];
                        int ny = y + dys[i];
                        if (nx < l || ny < t || nx > r || ny > b)
                        {
                            continue;
                        }
                        long nk = ((long)ny << 20) | (long)nx;
                        if (visited.Contains(nk))
                        {
                            continue;
                        }
                        bool isHead = false;
                        for (int j = 0; j < heads.Count; j++)
                        {
                            if (heads[j].mapx == nx && heads[j].mapy == ny)
                            {
                                isHead = true;
                                break;
                            }
                        }
                        if (isHead)
                        {
                            visited.Add(nk);
                            queue.Enqueue(nk);
                        }
                    }
                }
                // 3) 杀这一条链的蠕虫：移除动画 + 抓取体，保留巢体绘制
                for (int i = 0; i < chainHeads.Count; i++)
                {
                    chainHeads[i].closeAction(false, true);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 普攻命中反馈：横劈轻微后坐力 / 下劈 Pogo 弹跳并刷新冲刺、二段跳。
        /// sceneProp=true：命中场景物（靶/路障等），弹起高度为打怪物的 1.5 倍；false 打怪物保持原值。
        /// </summary>
        private void ApplyAttackHitFeedback(bool sceneProp = false)
        {
            // 普通横劈命中后坐力：极短、极轻微地向后推（面朝左往右、面朝右往左）
            if (!_downSlash && !_upSlash)
            {
                // 用短暂速度衰减实现平滑后退，而非一帧瞬移
                // 护符15 稳定之体：普攻命中不产生后坐力
                if (!CharmEffects.IsEquipped(CharmEffects.StableId))
                {
                    _recoilVx = _faceDir * (RecoilDistance * 2f / RecoilTime);
                    _recoilTimer = RecoilTime;
                }
            }
            // 下劈命中（Pogo）：立即重置垂直速度并向上弹跳。
            // 位于去重锁定之后，一次命中只弹一次，不会在空中连击叠速度
            if (_downSlash)
            {
                Vy = sceneProp ? PogoBounceVyScene : PogoBounceVy; // 打怪保持原值，场景物 1.5 倍高度
                _pogoGravityLock = 0.05f; // 极短时间不受重力，弹射瞬间无下坠感
                Grounded = false;
                _jumpHeld = false;
                // 刷新：Pogo 命中恢复空中冲刺和二段跳资格（Reset 机制）
                _canDoubleJump = true;
                _canDash = true;
                _dashCooldown = 0f;
            }
        }

        /// <summary>
        /// 伤害目标解析：森之领主的触手/笼子等嵌套体（NelEnemyNested，Parent 为 NelNBoss_Nusi）
        /// 被打中时，伤害转到本体。其他敌人原样返回。
        /// </summary>
        private NelEnemy ResolveDamageTarget(NelEnemy enemy)
        {
            // 召唤/生成阶段的魔物不可被攻击（与原版 APPEARING 锁一致）：
            // 跳过，避免破坏生成流程导致生成后渲染消失
            if (IsEnemySummoning(enemy))
            {
                return null;
            }
            if (enemy is NelEnemyNested nested && nested.Parent is NelNBoss_Nusi boss && boss.is_alive)
            {
                return boss;
            }
            return enemy;
        }

        /// <summary>魔物是否处于召唤/生成阶段（STATE.SUMMONED）。</summary>
        private bool IsEnemySummoning(NelEnemy enemy)
        {
            try
            {
                return enemy != null && enemy.getState() == NelEnemy.STATE.SUMMONED;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 森之领主五触手抓取期间：小骑士任意攻击命中 → 等效诺艾尔“圣光爆发”，直接进入眩晕。
        /// 触发窗口与游戏原版 Holy Burst 可打断的窗口一致（isFaintCounterExecuting(true)）。
        /// </summary>
        private void TryNusiBurstInterrupt(NelEnemy enemy)
        {
            if (enemy is NelNBoss_Nusi boss && boss.is_alive && boss.isFaintCounterExecuting(true))
            {
                try
                {
                    boss.initBurstStunPhase(null); // 小骑士无 PR Skill，传 null 跳过 clearManaDrainLock
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 获取一个“玩家普通攻击”标记的 MagicItem（缓存复用）。
        /// 的当て靶（M2MatoateTarget）/ 拳炮 / TD路障等目标要求 AttackInfo.PublishMagic 非空，
        /// 且部分目标会检查 PublishMagic.is_normal_attack / hit_pr / Caster 是否玩家，因此按玩家普攻构造。
        /// </summary>
        private MagicItem GetKnightAttackMagic()
        {
            if (_knightAttackMagic != null)
            {
                return _knightAttackMagic;
            }
            try
            {
                Map2d mpMg = FlukeMp; // 孤儿吸虫也要能拿到攻击 MagicItem
                if (mpMg != null && mpMg.M2D is NelM2DBase nm2d)
                {
                    MagicItem mg = new MagicItem(nm2d.MGC);
                    mg.is_normal_attack = true;
                    mg.hit_pr = true;
                    // school_garage_pre 的“环连水球”标靶只接受 MGKIND.WATERSHARD 攻击；
                    // 小骑士应能像打怪一样击破这些标靶（伤害 = 各攻击对怪的伤害值）。
                    // 对其他可攻击目标（普通标靶/拳炮/TD路障）kind 不影响受击判定。
                    mg.kind = MGKIND.WATERSHARD;
                    PRNoel noel = GetPr();
                    if (noel != null)
                    {
                        mg.Caster = noel;
                    }
                    _knightAttackMagic = mg;
                }
            }
            catch (Exception)
            {
            }
            return _knightAttackMagic;
        }

        /// <summary>该 MagicItem 是否是小骑士构造的攻击魔法（用于大炮充能等特殊判定）。</summary>
        public bool IsKnightSpellMagic(MagicItem Mg)
        {
            return Mg != null && ReferenceEquals(Mg, _knightAttackMagic);
        }

        /// <summary>
        /// <summary>小骑士的攻击可以破坏蜘蛛 BOSS 发射的蛛丝球（MgNWebShot）。</summary>
        private bool TryDestroyWebShot(GameObject gob)
        {
            try
            {
                if (gob == null || _mp == null || MagicAItemsField == null)
                {
                    return false;
                }
                NelM2DBase nm2d = _mp.M2D as NelM2DBase;
                if (nm2d == null)
                {
                    return false;
                }
                MagicItem[] items = MagicAItemsField.GetValue(nm2d.MGC) as MagicItem[];
                int len = MagicLenField != null
                    ? (int)MagicLenField.GetValue(nm2d.MGC)
                    : (items != null ? items.Length : 0);
                if (items == null)
                {
                    return false;
                }
                for (int i = 0; i < len && i < items.Length; i++)
                {
                    MagicItem mg = items[i];
                    if (mg != null && mg.kind == MGKIND.WEB_SHOT && mg.Dro != null &&
                        mg.Dro.MyObj is GameObject dg)
                    {
                        bool same = dg == gob;
                        if (!same && dg.transform != null && gob.transform != null)
                        {
                            same = gob.transform.IsChildOf(dg.transform) ||
                                   dg.transform.IsChildOf(gob.transform);
                        }
                        if (same)
                        {
                            mg.kill(0f);
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 小骑士的攻击可以破坏蜘蛛 BOSS 的“蛛丝球释放源”（MgBsSpiderTrap 陷阱）。
        /// 陷阱魔法：kind == BASIC_SHOT 且 Other 为 MgBsSpiderTrap.MEM；按攻击范围矩形判定。
        /// </summary>
        private void DestroySpiderTrapsInBox(Vector2 centerTile, float halfW, float halfH,
            float maxDist = 0f, bool grantSoul = true)
        {
            try
            {
                if (_mp == null || MagicAItemsField == null)
                {
                    return;
                }
                NelM2DBase nm2d = _mp.M2D as NelM2DBase;
                if (nm2d == null)
                {
                    return;
                }
                MagicItem[] items = MagicAItemsField.GetValue(nm2d.MGC) as MagicItem[];
                int len = MagicLenField != null
                    ? (int)MagicLenField.GetValue(nm2d.MGC)
                    : (items != null ? items.Length : 0);
                if (items == null)
                {
                    return;
                }
                for (int i = 0; i < len && i < items.Length; i++)
                {
                    MagicItem mg = items[i];
                    if (mg == null || mg.kind != MGKIND.BASIC_SHOT ||
                        !(mg.Other is MgBsSpiderTrap.MEM))
                    {
                        continue;
                    }
                    float dx = mg.sx - centerTile.x;
                    float dy = mg.sy - centerTile.y;
                    bool hit = maxDist > 0f
                        ? (dx * dx + dy * dy <= maxDist * maxDist)
                        : (mg.sx >= centerTile.x - halfW && mg.sx <= centerTile.x + halfW &&
                           mg.sy >= centerTile.y - halfH && mg.sy <= centerTile.y + halfH);
                    if (hit)
                    {
                        // 破坏释放源（陷阱）：攻击破坏获得 10 灵魂（每个陷阱只结算一次）
                        if (grantSoul && _trapSoulGranted.Add(mg))
                        {
                            KnightAddSoul(10);
                        }
                        mg.kill(0f);
                    }
                }
                if (_trapSoulGranted.Count > 64)
                {
                    _trapSoulGranted.Clear(); // 魔法实例会被池复用，容量过大时重置去重
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 伊夏（city_scl_ground 靶子小游戏 NPC）：M2CityCaster，mover 名为 citycaster_sub_i。
        /// 在 city_scl_ground 的友伤拦截中单独放行，让小骑士的攻击可以对她造成伤害。
        /// </summary>
        private static bool IsIxiaCaster(M2Attackable a)
        {
            return a is M2CityCaster cc && cc.gameObject != null &&
                cc.gameObject.name == "citycaster_sub_i";
        }

        /// <summary>
        /// 对非敌人的 M2Attackable（的当て靶、拳炮、TD路障、事件可操作物等）造成固定伤害。
        /// 诺艾尔能攻击/破坏的目标，小骑士同样可以。返回 true 表示该碰撞体已被本分支处理。
        /// </summary>
        private bool TryHitGenericAttackable(Collider2D c, int dmg, HashSet<object> dedup,
            byte feedback = 0, bool grantProxySoul = true)
        {
            if (c == null)
            {
                return false;
            }
            _attackHitFeedbackBlocked = false;
            // 联机远端玩家代理（M2Gunmu）属于非 NelEnemy 目标，会走这条通用命中入口；
            // 由各招式调用点显式传入反馈类型：
            //   0 = 轻受击（普攻 / 蓄力劈砍 / 旋风劈砍）
            //   1 = 直线击飞（下砸）
            //   2 = 着火（复仇之魂 / 深渊尖啸）
            //   3 = 直线击飞 ×1.5（冲刺劈砍）
            SetHitFeedback(feedback);
            // 蜘蛛蛛丝球：小骑士攻击可直接破坏（法术/技艺/下砸等通用命中分支）
            if (c.gameObject != null && TryDestroyWebShot(c.gameObject))
            {
                return true;
            }
            M2Attackable a = c.GetComponentInParent<M2Attackable>();
            if (a == null || a is PR || a is m2d.M2MoverPr)
            {
                return false;
            }
            // 格拉提亚保卫战（city_scl_ground）：禁止小骑士伤害防御工事、友军与自家基地；
            // 例外：伊夏（citycaster_sub_i，靶子小游戏 NPC）可以被小骑士攻击伤害。
            // 大炮（M2PuncherCannon 充能）与魔力塔（M2MoverBarricadeTDManaTower 吸魂/转魔力）是既有机制，不拦截。
            if (_mp != null && _mp.key == "city_scl_ground"
                && ((a is M2CityCaster && !IsIxiaCaster(a)) ||
                    (a is M2MoverBarricadeTD && !(a is M2MoverBarricadeTDManaTower))))
            {
                // 拦截友方目标：不掉血，也不产生横劈后坐力/下劈弹起
                _attackHitFeedbackBlocked = true;
                return true;
            }
            // 诊断：非普攻（下砸/法术/技艺）命中可攻击实体时，记录目标身份，用于排查“地板被当怪打”
            if (!_attacking)
            {
            }
            // 魔力塔：只有普攻（_attacking 挥砍）允许命中并触发魔力转移；
            // 法术/技艺命中时直接跳过，避免使魔力塔损失魔力
            if (a is M2MoverBarricadeTDManaTower && !_attacking)
            {
                return false;
            }
            if (dedup != null && !dedup.Add(a))
            {
                return true; // 本招式已命中过
            }
            // 普攻魔力塔新命中：恢复 15 灵魂（仅一次，不按帧重复；
            // 法术/技艺已被上面的魔力塔门槛提前跳过，这里只可能是普攻）
            if (a is M2MoverBarricadeTDManaTower && _attacking)
            {
                KnightAddSoul(15);
            }
            try
            {
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel; // 标靶等目标要求 AttackFrom 非空
                }
                atk.PublishMagic = GetKnightAttackMagic(); // 标靶/拳炮/TD路障要求 PublishMagic 非空
                if (a is M2PuncherCannon && atk.PublishMagic != null)
                {
                    // 大炮需要“攻击来源”角度来转向充能：把小骑士当前位置作为魔法中心
                    atk.PublishMagic.Cen = new Vector2(X, Y);
                }
                a.applyHpDamage(dmg, true, atk);
                DashAudio.PlayEnemyHit();
                // 骨钉类攻击命中联机远端代理（其他玩家/其魔物代理）：与打怪一致结算命中灵魂
                if (grantProxySoul && IsRemoteProxy(a) &&
                    (_attacking || _dashSlashing || _cycloneSlashing || _nailArtSlashing))
                {
                    GrantProxyNailSoul();
                }
            }
            catch (Exception)
            {
            }
            return true;
        }

        /// <summary>是否为联机远端实体代理（Kaleidoscopic.M2Gunmu）：按类型名判断，避免强依赖联机程序集。</summary>
        private static bool IsRemoteProxy(M2Attackable a)
        {
            return a != null && a.GetType().Name == "M2Gunmu";
        }

        /// <summary>
        /// 对联机远端实体代理（M2Gunmu）施加骨钉/技艺伤害：
        /// 与 TryHitGenericAttackable 同款攻击构造（Caster=本地诺艾尔 + 骑士攻击魔法），
        /// 联机端会据此转发伤害包；命中后结算灵魂。
        /// </summary>
        private void ApplyProxyNailHit(M2Attackable a, int dmg, byte feedback = 0)
        {
            if (a == null)
            {
                return;
            }
            try
            {
                SetHitFeedback(feedback);
                var atk = new NelAttackInfo();
                atk.hpdmg_current = dmg;
                atk.hpdmg0 = dmg;
                atk.fix_damage = true;
                atk.CenterXy(a.x, a.y, 0f);
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                    atk.AttackFrom = noel;
                }
                atk.PublishMagic = GetKnightAttackMagic();
                a.applyHpDamage(dmg, true, atk);
                DashAudio.PlayEnemyHit();
                GrantProxyNailSoul();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>骨钉命中结算灵魂：命中 +10（灵魂捕手 +3、噬魂者 +8），与打怪一致。</summary>
        private void GrantProxyNailSoul()
        {
            // 蓄力劈砍一次施放有三段伤害，但整段只结算一次命中灵魂（仅 +10）
            if (_nailArtSlashing)
            {
                if (_nailArtSoulGranted)
                {
                    return;
                }
                _nailArtSoulGranted = true;
            }
            // 旋风劈砍整段只结算一次命中灵魂（避免每斩都 +10）
            if (_cycloneSlashing)
            {
                if (_cycloneSoulGranted)
                {
                    return;
                }
                _cycloneSoulGranted = true;
            }
            int soul = 10 +
                (CharmEffects.IsEquipped(CharmEffects.SoulCatcherId) ? 3 : 0) +
                (CharmEffects.IsEquipped(CharmEffects.SoulEaterId) ? 8 : 0);
            KnightAddSoul(soul);
        }

        /// <summary>
        /// 打中怪物：每次挥砍按目标去重（_swingHits），
        /// 打印日志并给怪物造成 50 点真实伤害（完整受击管线，含硬直/伤害数字/死亡）。
        /// </summary>
        public void NotifyAttackHit(NelEnemy enemy)
        {
            enemy = ResolveDamageTarget(enemy);
            if (!_swingHits.Add((object)enemy))
            {
                return; // 同一只怪物一次挥砍只判定一次
            }
            if (enemy == null)
            {
                return;
            }
            TryNusiBurstInterrupt(enemy);
            // 骨钉击中怪物的音效
            DashAudio.PlaySlashHit();
            try
            {
                // 走 AIC 完整伤害管线：受击反馈、伤害数字、击退、死亡都由游戏自己处理
                var atk = new NelAttackInfo();
                atk.hpdmg_current = CharmEffects.ScaleNailDamage(SlashDamage);
                atk.hpdmg0 = CharmEffects.ScaleNailDamage(SlashDamage);
                // 固定伤害：跳过敌人防御倍率，保证 42 点打满
                atk.fix_damage = true;
                // 以诺艾尔为攻击者（她与小骑士位置同步），让怪物受击后能苏醒/仇恨玩家
                PRNoel noel = GetPr();
                if (noel != null)
                {
                    atk.Caster = noel;
                }
                enemy.applyDamage(atk, false);
                // 砍中敌人获得灵魂：命中 +10，击杀 +10；
                // 护符4 灵魂捕手 +3、护符6 噬魂者 +8（可叠加）
                int slashSoul = 10 +
                    (CharmEffects.IsEquipped(CharmEffects.SoulCatcherId) ? 3 : 0) +
                    (CharmEffects.IsEquipped(CharmEffects.SoulEaterId) ? 8 : 0);
                if (!enemy.is_alive || enemy.hp_ratio <= 0f)
                {
                    KnightAddSoul(slashSoul);
                }
                else
                {
                    KnightAddSoul(slashSoul);
                }
                ApplyAttackHitFeedback();
                // 造成伤害后播放敌人受击音效（白闪/粒子由 applyDamage 原版管线自带）
                try
                {
                    DashAudio.PlayEnemyHit();
                }
                catch (Exception)
                {
                }
                // 原版普攻受击粒子（pr_cane_hit = 诺艾尔法杖普攻命中时的白色软圆点特效）。
                // 与 AttackInfo.playEffect 的原版调用一致：hit_x/hit_y=怪物位置
                try
                {
                    Map2d mp = enemy.Mp;
                    if (mp != null)
                    {
                        mp.PtcSTsetVar("hit_x", (double)enemy.x);
                        mp.PtcSTsetVar("hit_y", (double)enemy.y);
                        // pr_cane_hit 粒子强依赖 ax 变量，缺失会触发 VariableP 崩溃，补 0 防止报错
                        mp.PtcSTsetVar("ax", 0.0);
                        mp.PtcST("pr_cane_hit", null, (XX.PTCThread.StFollow)0);
                    }
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
                // 完整管线异常时兜底：直接扣 42 点 HP
                try
                {
                    enemy.applyHpDamage(CharmEffects.ScaleNailDamage(SlashDamage), true, null);
                    // 兜底扣血同样获得灵魂：命中 +10，击杀 +10；
                    // 护符4 灵魂捕手 +4、护符6 噬魂者 +10（可叠加）
                    int slashSoulFallback = 10 +
                        (CharmEffects.IsEquipped(CharmEffects.SoulCatcherId) ? 3 : 0) +
                        (CharmEffects.IsEquipped(CharmEffects.SoulEaterId) ? 8 : 0);
                    if (!enemy.is_alive || enemy.hp_ratio <= 0f)
                    {
                        KnightAddSoul(slashSoulFallback);
                    }
                    else
                    {
                        KnightAddSoul(slashSoulFallback);
                    }
                    ApplyAttackHitFeedback();
                    try
                    {
                        DashAudio.PlayEnemyHit();
                    }
                    catch (Exception)
                    {
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 判定框在触碰到怪物时自毁（OnTriggerEnter2D 里调用），
        /// 清掉实体持有的引用，避免残留碰撞箱继续造成“空气打怪”。
        /// </summary>
        public void NotifyHitboxDestroyed()
        {
            _hitboxGo = null;
        }

        private void DestroyHitbox()
        {
            _hitboxColliders = null;
            if (_hitboxGo != null)
            {
                Destroy(_hitboxGo);
                _hitboxGo = null;
            }
        }

        /// <summary>
        /// 有效时间结束后立即禁用所有判定碰撞体（Box + Polygon），停止伤害检测。
        /// </summary>
        private void DisableHitboxColliders()
        {
            if (_hitboxColliders == null)
            {
                return;
            }
            for (int i = 0; i < _hitboxColliders.Length; i++)
            {
                if (_hitboxColliders[i] != null)
                {
                    _hitboxColliders[i].enabled = false;
                }
            }
        }

        private float GetClipDuration(string clipName)
        {
            if (_clips.TryGetValue(clipName, out ClipData clip) && clip.frames.Length > 0 && clip.fps > 0f)
            {
                return clip.frames.Length / clip.fps;
            }
            return 0.3f;
        }

        // ---------- 坐长椅 ----------

        /// <summary>
        /// 每帧调用一次：未坐下时检测“靠近长椅 + 按 上/下（抬头/低头键）”坐下，
        /// 坐下后检测“移动 / 跳跃 / 攻击”起身。
        /// （原先的“交互键 F”已移除：门/NPC/宝箱/存档点等原生交互改由游戏自带的交互键触发。）
        /// </summary>
        private void TryBenchInteraction()
        {
            if (_isSitting)
            {
                // 护符界面打开期间禁止起身（方向键/攻击等不再触发下椅）
                if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
                {
                    return;
                }
                bool leave = KeyConfig.GetHeld(KnightInCradlePlugin.MoveLeftKey, KeyCode.A) ||
                             KeyConfig.GetHeld(KnightInCradlePlugin.MoveRightKey, KeyCode.D) ||
                             KeyConfig.GetPressed(KnightInCradlePlugin.JumpKey, KeyCode.W) ||
                             KeyConfig.GetPressed(KnightInCradlePlugin.AttackKey, KeyCode.Mouse0);
                if (leave)
                {
                    ExitSitting();
                }
                return;
            }

            if (_sitStandingUp || _attacking || _dashing || _wallJumping || _doubleJumping || _onWall ||
                _focusing || _fireballCasting || _taunting)
            {
                return;
            }

            // 上（抬头）/ 下（低头）均可坐下
            bool interactPressed = KeyConfig.GetPressed(KnightInCradlePlugin.LookUpKey, KeyCode.Q) ||
                                   KeyConfig.GetPressed(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            if (!interactPressed)
            {
                return;
            }

            NelChipBench bench = FindNearBench();
            if (bench != null)
            {
                // 距离长椅小于 1 格（横向），纵向允许 2 格（骑士脚底对齐长椅中心附近）
                float dx = Mathf.Abs(X - bench.mapcx);
                float dy = Mathf.Abs((Y + SizeY) - bench.mapcy);
                if (dx < 1f && dy < 2f)
                {
                    StartSitting(bench);
                }
            }
        }

        /// <summary>
        /// 复用 AIC 原版搜索：以骑士脚底为中心搜 7×9 格的 bench 芯片，返回最近一个未移除的长椅。
        /// </summary>
        private NelChipBench FindNearBench()
        {
            return FindNearBenchIn(_mp, X, Y + SizeY);
        }

        /// <summary>在指定地图、指定坐标附近搜索最近的长椅。</summary>
        private NelChipBench FindNearBenchIn(Map2d mp, float x, float y)
        {
            if (mp == null)
            {
                return null;
            }
            int cx = Mathf.FloorToInt(x);
            int cy = Mathf.FloorToInt(y);
            var list = new List<M2Puts>();
            mp.getAllPointMetaPutsTo(cx - 3, cy - 6, 7, 9, list, "bench");

            NelChipBench best = null;
            float bestDist = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is NelChipBench b && !b.active_removed && !b.Lay.unloaded)
                {
                    float d = Mathf.Abs(x - b.mapcx) + Mathf.Abs(y - b.mapcy);
                    if (best == null || d < bestDist)
                    {
                        best = b;
                        bestDist = d;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 坐下：锁定输入、播放 Sit 过渡动画、回满 9 格血、灵魂补到 100，
        /// 并把长椅注册为地图快速旅行点 + 记录重生点。
        /// </summary>
        public void StartSitting(NelChipBench bench)
        {
            _isSitting = true;
            // 护符22 巴尔德之壳：坐椅子恢复本场战斗抵挡次数
            _baldurBlocksLeft = BaldurMaxBlocks;
            EndBaldurShell();
            _sitTimer = 0f;
            _sitBench = bench;
            _sitStandingUp = false;
            _sitStandTimer = 0f;
            // 坐下时清空地图重定位状态：否则残留的 _pendingReposition 会暂停诺艾尔同步
            // （两者碰撞箱分开），起身后还会把骑士拽到诺艾尔的位置
            _pendingReposition = 0;
            _transitionPause = false;
            _dashing = false;
            _dashTimer = 0f;
            _attacking = false;
            _attackTimer = 0f;
            _onWall = false;
            Vx = 0f;
            Vy = 0f;
            Grounded = true;
            DestroyHitbox();
            // 按下交互键后不瞬移：平滑滑到长椅中心（同时播放 Sit 动画）。
            // 目标椅面高度 = 当前地面 y - 0.5（游戏内 y 轴向下为正，椅子较高需抬升）。
            if (bench != null)
            {
                _sitToX = bench.mapcx;
                _sitFromX = X;
                _sitFromY = Y;
                _sitGroundY = Y;
                _sitToY = Y - 0.3f;
                _sitX = _sitToX;
                _sitY = _sitToY;
                // 记录重生点：上次休息的长椅（长椅中心、地面高度）
                _hasRespawn = true;
                _respawnX = _sitToX;
                _respawnY = _sitGroundY;
                _respawnMp = _mp;
                _sitSlideTimer = 0f;
                // 滑到椅面的时间固定 0.2 秒（与起身一致）
                _sitSlideDuration = SitTransitionTime;
                _sitSliding = true;
            }
            // 强制坐下动画从第一帧开始播
            _currentClip = null;

            // 回血：补满（基础 9 格，坚固心脏时 12 格）；
            // 28 生命血之心(+2)/29 生命血核心(+4)：长椅休息额外获得生命血（不改变上限，超出部分血条蓝色）
            _health = MaxHealth + LifebloodBonus();
            if (_soul < 90)
            {
                _soul = Mathf.Min(MaxSoul, 90); // 束缚·灵魂时最多补到 30
            }

            // 传送注册：点亮地图上的长椅图标（成为快速旅行点），并记录重生点
            try
            {
                if (bench != null)
                {
                    bench.fineIcon();
                    SVD.sFile cur = COOK.getCurrentFile();
                    if (cur != null)
                    {
                        cur.assignRevertPosition(M2DBase.Instance as NelM2DBase);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 起身：退出坐姿，播放 HK 原版 Get Off 过渡（期间继续锁输入），播完恢复普通控制。
        /// </summary>
        public void ExitSitting()
        {
            if (!_isSitting)
            {
                return;
            }
            _isSitting = false;
            // 保留 _sitBench 引用用于起身过渡的位置插值；过渡结束后自然失效
            _sitStandingUp = true;
            // 离开椅面 0.2 秒（与坐上滑行一致）
            _sitStandTimer = SitTransitionTime;
            _currentClip = null;
        }

        /// <summary>
        /// 地图快速旅行（选中其他长椅传送）执行后调用：
        /// 清除坐姿并进入重定位跟随，让骑士逐帧对齐诺艾尔的新位置。
        /// 否则骑士会钉在旧长椅的坐标上（跨图后该坐标无效，表现为消失且无法移动）。
        /// 跨图传送时地图切换分支会接管重定位参数；同图传送则直接开始跟随。
        /// </summary>
        public void HandleFastTravel()
        {
            if (!_active || _isDead || _respawnFadeOut)
            {
                return;
            }
            // 黑屏传送开始：立即隐藏 deco（到达后由 OnFastTravelArrive 重播淡入）
            KnightHudDeco.OnFastTravelStart();
            // 快速旅行跟随：延长到诺艾尔到达目的地长椅（否则固定帧数结束后
            // SyncNoelToKnight 会暂停她物理/拉回骑士位置，导致 AUTO_SAVE_BENCH 找不到长椅）
            _fastTravelInProgress = true;
            PendingFastTravelRebind = true; // 传送结束后重绑渲染票据（原地传送也生效）
            _fastTravelWaitFrames = 0;
            _fastTravelArrivedTimer = -1f;
            _fastTravelNoBenchTimer = -1f;
            _fastTravelPrevTransferring = false;
            _fastTravelMapChanged = false;
            // 注意：这里不清除 CharmEffects.BattleAreaFastTravel —— 战斗区域传送
            // 需要该标记在到达后走 0.5s 快速完成跟随；长椅传送的确认前缀已将其置为 false。
            // 清除坐姿：坐姿期间重定位跟随被跳过，且旧椅坐标在新地图无效
            _isSitting = false;
            _sitStandingUp = false;
            _sitBench = null;
            _sitSliding = false;
            _sitTimer = 0f;
            _sitStandTimer = 0f;
            // 同图传送：直接进入跟随；跨图传送由地图切换分支接管（会重新赋值）
            _pendingReposition = 45;
            _pendingRepositionActive = true;
            _transitionPause = false;
            _horizontalTransfer = false;
            _transferSimDur = 0f;
            _attacking = false;
            DestroyHitbox();
        }

        /// <summary>
        /// 坐到椅面上时播放与诺艾尔相同的 bench_sitdown 粒子特效（含诺艾尔原版坐椅音效）。
        /// </summary>
        private void PlaySitLandingFx()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr != null)
                {
                    pr.setTo(X, Y);
                    pr.PtcST("bench_sitdown", PtcHolder.PTC_HOLD.NORMAL, PTCThread.StFollow.NO_FOLLOW);
                }
            }
            catch (Exception)
            {
            }
        }

        private static int EffectiveIndex(ClipData clip, int raw)
        {
            int n = clip.frames.Length;
            switch (clip.wrapMode)
            {
                case 2:
                    return Math.Min(raw, n - 1);
                case 3:
                {
                    int period = Math.Max(1, n * 2 - 2);
                    int m = raw % period;
                    return m < n ? m : period - m;
                }
                case 4:
                {
                    // 先播 frames[0..loopStart-1] 一次（过渡进入），
                    // 然后循环 frames[loopStart..n-1]（保持）
                    int start = Math.Min(clip.loopStart, n - 1);
                    if (raw < start)
                    {
                        return raw;
                    }
                    int len = n - start;
                    return start + (raw - start) % len;
                }
                default:
                {
                    int start = clip.wrapMode == 1 ? Math.Min(clip.loopStart, n - 1) : 0;
                    int len = n - start;
                    return start + raw % len;
                }
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        }
    }
}
