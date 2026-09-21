using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using m2d;
using nel;
using UnityEngine;
using XX;
using BepInEx;

namespace KnightInCradle
{
    /// <summary>
    /// 联机模组（Kaleidoscopic）兼容补丁。
    ///
    /// 背景：联机服务器只放行“玩家攻击（PR_* 系列，MGKIND 9000 段）”的伤害包；
    /// 小骑士自造的 PublishMagic 用的是 MGKIND.WATERSHARD(6)，被服务器当作
    /// “不认识的法术”把伤害清零（表现为：命中有效果、但对方不掉血/伤害为 0）。
    ///
    /// 做法：Kaleidoscopic 里所有“打中远端代理 → 生成 OutboundDamagePacket”都汇聚在
    /// M2Gunmu.applyHpDamage。这里给它挂前缀/后缀：当受害者是小骑士的攻击（命中缓存魔法
    /// _knightAttackMagic）时，在发包前把魔法种类临时伪装成 MGKIND.PR_PUNCH(9000) +
    /// MGHIT.PR|IMMEDIATE|NORMAL_ATTACK(5121)，方法返回后再还原，不影响本地伤害判定。
    /// </summary>
    public static class MultiplayerCompat
    {
        private const int PrKindMin = 9000;
        private const int PrKindMax = 9499; // PR_PUNCH(9000) ~ PR_SHINEBOOSTER 段；9500 起是 PR_BURST

        private static bool _patched;
        private static bool _hudPatched;
        private static bool _remoteKnightPatched;
        private static bool _damageTried;
        private static bool _hudTried;
        private static bool _remoteKnightTried;
        private static bool _mpKnown;
        private static bool _mpPresent;
        private static float _scanTimer;
        private static int _scanCount;
        private static bool _rewrote;
        private static int _origVal;
        // 同一次命中会被调用多次（联机中继 + 本地应用 + 伤害管线），只补偿一次：
        // 以 (AttackInfo 实例, 帧) 去重。骑士每段攻击都是新的 NelAttackInfo，且分段间隔 ≥0.15s，
        // 因此不会把同一招的多段误去重。
        private static object _valDedupAtk;
        private static int _valDedupFrame = -100;
        private static MGKIND _origKind;
        private static MGHIT _origHit;
        private static float _origKnockLen;
        private static float _origKnockP;
        private static float _origKnockT;
        private static bool _origApplyKnock;
        private static MGATTR _origAttr;
        private static float _origBurstVx;
        private static float _origBurstVy;

        /// <summary>每帧驱动：先用轻量程序集扫描确认 Kaleidoscopic 存在（最多约 3s），
        /// 不存在则永久空转，避免逐帧 TypeByName 造成卡顿/刷日志。</summary>
        public static void Tick()
        {
            if (_mpKnown)
            {
                if (_mpPresent)
                {
                    TryPatchDamageRelay();
                    TryPatchHeadBars();
                    TryPatchRemoteKnight();
                }
                return;
            }
            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer < 0.5f)
            {
                return;
            }
            _scanTimer = 0f;
            if (AssemblyScan())
            {
                _mpKnown = true;
                _mpPresent = true;
                TryPatchDamageRelay();
                TryPatchHeadBars();
                TryPatchRemoteKnight();
            }
            else if (++_scanCount >= 6)
            {
                _mpPresent = false;
                _scanCount = 0;
                _scanTimer = -1.5f;   // 暂时没找到就放慢到每 2 秒重试（联机模组可能稍后才加载）
            }
        }

        /// <summary>切换到小骑士模式时调用：立刻重试所有补丁，消除“刚进游戏打不动人”的窗口。</summary>
        internal static void OnKnightModeChanged()
        {
            _damageTried = false;
            _hudTried = false;
            _remoteKnightTried = false;
            _patched = false;
            _hudPatched = false;
            _remoteKnightPatched = false;
            if (!AssemblyScan())
            {
                return;
            }
            _mpKnown = true;
            _mpPresent = true;
            TryPatchDamageRelay();
            TryPatchHeadBars();
            TryPatchRemoteKnight();
        }

        /// <summary>程序集级探测（每 0.5s 一次，成本极低），不触发 HarmonyX 的类型查找日志。</summary>
        private static bool AssemblyScan()
        {
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    string n = asms[i].GetName().Name;
                    if (n != null && n.IndexOf("Kaleidoscopic", StringComparison.OrdinalIgnoreCase) >= 0)
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

        private static void TryPatchRemoteKnight()
        {
            if (_remoteKnightPatched || _remoteKnightTried)
            {
                return;
            }
            _remoteKnightTried = true;
            try
            {
                Type t = AccessTools.TypeByName("Kaleidoscopic.Syncs.SyncPatcherPlayers");
                if (t == null)
                {
                    _remoteKnightTried = false;
                    return;
                }
                // render1(ProjectionContainer, PlayerInfo, ElecTraceDrawer)：远端角色主体绘制
                MethodInfo render1 = null;
                foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "render1" && mi.GetParameters().Length == 3)
                    {
                        render1 = mi;
                        break;
                    }
                }
                if (render1 == null)
                {
                    _remoteKnightTried = false;
                    return;
                }
                var harmony = new Harmony("dev.KnightInCradle.multiplayer.remote");
                harmony.Patch(render1, prefix: new HarmonyMethod(typeof(MultiplayerCompat).GetMethod(
                    nameof(Render1Prefix), BindingFlags.Static | BindingFlags.Public)));
                _remoteKnightPatched = true;
            }
            catch (Exception)
            {
            }
        }

        private static void TryPatchDamageRelay()
        {
            if (_patched || _damageTried)
            {
                return;
            }
            _damageTried = true;
            try
            {
                Type t = AccessTools.TypeByName("Kaleidoscopic.Syncs.M2Gunmu");
                if (t == null)
                {
                    _damageTried = false;
                    return; // 未安装联机模组 / 尚未加载
                }
                MethodInfo m = AccessTools.Method(t, "applyHpDamage",
                    new[] { typeof(int), typeof(bool), typeof(AttackInfo) });
                if (m == null)
                {
                    _damageTried = false;
                    return;
                }
                var harmony = new Harmony("dev.KnightInCradle.multiplayer");
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(MultiplayerCompat).GetMethod(
                        nameof(ApplyHpDamagePrefix), BindingFlags.Static | BindingFlags.Public)),
                    postfix: new HarmonyMethod(typeof(MultiplayerCompat).GetMethod(
                        nameof(ApplyHpDamagePostfix), BindingFlags.Static | BindingFlags.Public)));
                // 远端玩家代理只负责同步/攻击判定，不再用实体碰撞推动本地诺艾尔。
                MethodInfo fineLayer = AccessTools.Method(t, "fineHittingLayer");
                if (fineLayer != null)
                {
                    harmony.Patch(fineLayer, postfix: new HarmonyMethod(
                        typeof(MultiplayerCompat).GetMethod(
                            nameof(RemoteProxyFineHittingLayerPostfix),
                            BindingFlags.Static | BindingFlags.Public)));
                }
                _patched = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 远端玩家代理（M2Gunmu）的碰撞体改为 Trigger，并与本地诺艾尔忽略碰撞：
        /// 仍能被攻击 Overlap 查询命中，但不会把诺艾尔推走/拖走。
        /// </summary>
        public static void RemoteProxyFineHittingLayerPostfix(object __instance)
        {
            try
            {
                M2Attackable proxy = __instance as M2Attackable;
                if (proxy == null || proxy.gameObject == null)
                {
                    return;
                }
                Collider2D[] proxyCols = proxy.GetComponentsInChildren<Collider2D>(true);
                PRNoel pr = (M2DBase.Instance as NelM2DBase)?.getPrNoel();
                Collider2D[] prCols = pr != null
                    ? pr.GetComponentsInChildren<Collider2D>(true)
                    : null;
                for (int i = 0; i < proxyCols.Length; i++)
                {
                    Collider2D pc = proxyCols[i];
                    if (pc == null)
                    {
                        continue;
                    }
                    pc.isTrigger = true;
                    if (prCols != null)
                    {
                        for (int j = 0; j < prCols.Length; j++)
                        {
                            if (prCols[j] != null)
                            {
                                Physics2D.IgnoreCollision(pc, prCols[j], true);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void TryPatchHeadBars()
        {
            if (_hudPatched || _hudTried)
            {
                return;
            }
            _hudTried = true;
            try
            {
                Type t = AccessTools.TypeByName("Kaleidoscopic.Syncs.SyncPatcherPlayers");
                if (t == null)
                {
                    _hudTried = false;
                    return;
                }
                MethodInfo create = AccessTools.Method(t, "createPlayerInfo", new[] { typeof(PR) });
                MethodInfo render = AccessTools.Method(t, "renderHpMpBar",
                    new[] { typeof(ProjectionContainer), ResolvePlayerInfoType(), typeof(float) });
                if (create == null || render == null)
                {
                    _hudTried = false;
                    return;
                }
                var harmony = new Harmony("dev.KnightInCradle.multiplayer.hud");
                harmony.Patch(create, postfix: new HarmonyMethod(typeof(MultiplayerCompat).GetMethod(
                    nameof(CreatePlayerInfoPostfix), BindingFlags.Static | BindingFlags.Public)));
                harmony.Patch(render, prefix: new HarmonyMethod(typeof(MultiplayerCompat).GetMethod(
                    nameof(RenderHpMpBarPrefix), BindingFlags.Static | BindingFlags.Public)));
                _hudPatched = true;
            }
            catch (Exception)
            {
            }
        }

        private static Type _playerInfoType;
        private static Type ResolvePlayerInfoType()
        {
            return _playerInfoType ?? (_playerInfoType =
                AccessTools.TypeByName("Kaleidoscopic.Syncs.Packets.PlayerInfo"));
        }

        /// <summary>PlayerInfo 里“骑士模式”标记（nameColor=0xFFFFFFFF；远程名字默认也是白色，无视觉副作用）。</summary>
        private const uint KnightMarker = 0xFFFFFFFF;

        private static readonly Dictionary<string, FieldInfo> PlayerFields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static MethodInfo _toScreen;

        private static void ToScreen(ref float x, ref float y)
        {
            try
            {
                if (_toScreen == null)
                {
                    Type t = AccessTools.TypeByName("Kaleidoscopic.StrictMath");
                    _toScreen = t != null
                        ? AccessTools.Method(t, "toScreen", new[] { typeof(float).MakeByRefType(), typeof(float).MakeByRefType() })
                        : null;
                }
                if (_toScreen == null)
                {
                    return;
                }
                object[] args = { x, y };
                _toScreen.Invoke(null, args);
                x = (float)args[0];
                y = (float)args[1];
            }
            catch (Exception)
            {
            }
        }

        private static FieldInfo PlayerField(string name)
        {
            if (!PlayerFields.TryGetValue(name, out FieldInfo fi))
            {
                Type t = ResolvePlayerInfoType();
                fi = t != null ? t.GetField(name) : null;
                PlayerFields[name] = fi;
            }
            return fi;
        }

        private static bool IsLocalKnight()
        {
            return KnightEntity.Instance != null && KnightEntity.Instance.IsActive;
        }

        /// <summary>本地玩家为小骑士时：让联机同步的 PlayerInfo 携带骑士血量/灵魂，并打上骑士标记。</summary>
        public static void CreatePlayerInfoPostfix(PR pr, ref object __result)
        {
            try
            {
                if (__result == null || !IsLocalKnight() || !(pr is PRNoel))
                {
                    return;
                }
                KnightEntity k = KnightEntity.Instance;
                KnightEntity.KnightRemoteSnapshot snap = k.GetRemoteSnapshot();
                SetPlayerField(__result, "hp", (float)k.Health);
                SetPlayerField(__result, "hpmax", (float)k.MaxHealth);
                SetPlayerField(__result, "mp", k.SoulInfinite ? 999999f : (float)k.Soul);
                SetPlayerField(__result, "mpmax", k.SoulInfinite ? 999999f : (float)k.MaxSoul);
                SetPlayerField(__result, "nameColor", KnightMarker);
                // ay 用于向远端广播“骑士脚底 Y”（y 向下为正），供远端把贴图脚底钉在地面
                SetPlayerField(__result, "ay", k.FootY);
                // shieldData 兼作特效同步通道：前 44 字节是空护盾占位（联机模组解析后 alpha=0 会跳过），
                // 之后是本插件自己的特效四边形负载，远端据此复现挥砍弧光 / 骨钉剑气 / 法术特效。
                byte[] fxPayload = k.BuildRemoteFxPayload();
                SetPlayerField(__result, "shieldData", fxPayload);
                if (snap != null)
                {
                    // 复用联机字段承载骑士动画状态：characterTitle=标记、poseTitle=剪辑、
                    // frameIndex=帧序号、aimInt=朝向(1=右)、caneName=精灵名
                    SetPlayerField(__result, "characterTitle", "__KNIGHT__");
                    SetPlayerField(__result, "poseTitle", snap.Clip);
                    SetPlayerField(__result, "frameIndex", snap.Frame);
                    SetPlayerField(__result, "aimInt", snap.Face);
                    SetPlayerField(__result, "caneName", snap.Sprite);
                    // 骑士模式不发诺艾尔魔法/蓄力信息，避免远端画出魔法图标
                    SetPlayerField(__result, "curMgKindInt", 0);
                    SetPlayerField(__result, "skillMpHold", 0f);
                    SetPlayerField(__result, "curMgReduceMp", 0f);
                }
            }
            catch (Exception)
            {
            }
        }

        private static readonly Dictionary<string, Texture2D> KnightFrameCache =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Material> KnightMatCache =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>骑士模式标记串（与广播一致）。</summary>
        private const string KnightRole = "__KNIGHT__";

        private static Dictionary<string, string> _spritePathIndex;

        /// <summary>
        /// 解析精灵 PNG 路径：优先 assets/hk/sprites，找不到时在整个 assets/hk 下按文件名检索。
        /// 暗影冲刺 shadow_dash、深渊尖啸施法 scream_cast 等帧存放在 sheets 子目录里，
        /// 只查 sprites 会导致远端这些动画加载失败（表现为骑士“直接消失”）。
        /// </summary>
        private static string ResolveKnightSpritePath(string sprite)
        {
            try
            {
                string root = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
                string direct = Path.Combine(root, "sprites", sprite + ".png");
                if (File.Exists(direct))
                {
                    return direct;
                }
                // 乌恩帧存在 unn / unn_2 两套同名文件；本地加载的是 unn_2，
                // 远端必须优先命中同一套，否则部分帧方向不一致。
                if (sprite.StartsWith("knight_slug", StringComparison.Ordinal))
                {
                    string unn2 = Path.Combine(root, "sheets", "unn_2", "sprites", sprite + ".png");
                    if (File.Exists(unn2)) return unn2;
                }
                else if (sprite.StartsWith("shroom_slug", StringComparison.Ordinal))
                {
                    string mush = Path.Combine(root, "sheets", "mush_unn", "sprites", sprite + ".png");
                    if (File.Exists(mush)) return mush;
                }
                if (_spritePathIndex == null)
                {
                    var idx = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        if (Directory.Exists(root))
                        {
                            foreach (string p in Directory.GetFiles(root, "*.png", SearchOption.AllDirectories))
                            {
                                string key = Path.GetFileNameWithoutExtension(p);
                                if (!idx.ContainsKey(key))
                                {
                                    idx[key] = p;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                    _spritePathIndex = idx;
                }
                return _spritePathIndex.TryGetValue(sprite, out string found) ? found : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Texture2D LoadKnightFrame(string sprite)
        {
            if (string.IsNullOrEmpty(sprite))
            {
                return null;
            }
            if (KnightFrameCache.TryGetValue(sprite, out Texture2D tex) && tex != null)
            {
                return tex;
            }
            try
            {
                string path = ResolveKnightSpritePath(sprite);
                if (!File.Exists(path))
                {
                    return null;
                }
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                KnightFrameCache[sprite] = tex;
                return tex;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Material GetKnightMat(Texture2D tex, string key)
        {
            if (KnightMatCache.TryGetValue(key, out Material m) && m != null)
            {
                return m;
            }
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null)
            {
                sh = Shader.Find("UI/Default");
            }
            if (sh == null)
            {
                return null;
            }
            m = new Material(sh)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            m.mainTexture = tex;
            KnightMatCache[key] = m;
            return m;
        }

        private static Dictionary<string, bool> _spriteExtraFlip;

        /// <summary>
        /// 乌恩蛞蝓原始清单里带 flipped:1 的帧，远端 PNG 直接加载时需要额外镜像一次。
        /// </summary>
        private static bool SpriteNeedsExtraFlip(string sprite)
        {
            if (string.IsNullOrEmpty(sprite))
            {
                return false;
            }
            if (_spriteExtraFlip == null)
            {
                _spriteExtraFlip = new Dictionary<string, bool>(StringComparer.Ordinal);
                try
                {
                    string manifest = Path.Combine(Paths.PluginPath, "KnightInCradle",
                        "assets", "hk", "sheets", "unn_2", "unn_manifest.json");
                    if (File.Exists(manifest))
                    {
                        string text = File.ReadAllText(manifest);
                        foreach (Match m in Regex.Matches(text,
                            "\"([^\"]+knight_slug[^\"]*)\"\\s*:\\s*\\{([^}]*)\\}",
                            RegexOptions.Singleline))
                        {
                            string key = m.Groups[1].Value;
                            bool flipped = Regex.IsMatch(m.Groups[2].Value,
                                "\"flipped\"\\s*:\\s*1");
                            _spriteExtraFlip[key] = flipped;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return _spriteExtraFlip.TryGetValue(sprite, out bool flip) && flip;
        }

        /// <summary>远端骑士：跳过诺艾尔本体绘制，改画骑士当前帧。</summary>
        public static bool Render1Prefix(object op)
        {
            try
            {
                if (op == null)
                {
                    return true;
                }
                NoteRemoteHp(op); // 记录远端玩家血量比例（供“非满血伤害补偿”用）
                NoteRemotePlayerX(GetF(op, "x")); // 记录所有远端玩家位置（诺艾尔/小骑士）
                FieldInfo role = PlayerField("characterTitle");
                object rv = role != null ? role.GetValue(op) : null;
                if (!(rv is string rs) || rs != KnightRole)
                {
                    return true;
                }
                DrawRemoteKnight(op);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void DrawRemoteKnight(object op)
        {
            try
            {
                FieldInfo fspr = PlayerField("caneName");
                object sv = fspr != null ? fspr.GetValue(op) : null;
                string sprite = sv as string;
                if (string.IsNullOrEmpty(sprite))
                {
                    return;
                }
                Texture2D tex = LoadKnightFrame(sprite);
                if (tex == null)
                {
                    return;
                }
                Material mat = GetKnightMat(tex, sprite);
                if (mat == null)
                {
                    return;
                }
                float mx = GetF(op, "x");
                // 脚底 Y：优先用广播的 ay（骑士脚底）；老版本客户端无该值时回退用 y
                float my = GetF(op, "ay");
                if (my <= 0.001f && my >= -0.001f)
                {
                    my = GetF(op, "y");
                }
                // 脚底世界点 → 屏幕（贴图脚底永远钉在地面/脚下）
                float fx = mx;
                float fy = my;
                ToScreen(ref fx, ref fy);
                // 每“格”对应的屏幕像素：用原始地图坐标 (mx,my) 与 (mx+1,my) 各自转屏后求差
                float bx = mx + 1f;
                float by = my;
                ToScreen(ref bx, ref by);
                float pxPerUnit = Mathf.Abs(bx - fx);
                if (pxPerUnit < 1f)
                {
                    pxPerUnit = 60f;
                }
                float scale = KnightInCradlePlugin.ScaleConfig != null
                    ? KnightInCradlePlugin.ScaleConfig.Value
                    : 0.325f;
                const float mapPxPerUnit = 64f;
                // 尺寸经验系数：用户按观感逐轮校准（2.0 → 2.1 → 2.5 → 2.3）；需要微调改这里
                const float sizeMul = 2.3f;
                float w = tex.width * scale * pxPerUnit / mapPxPerUnit * sizeMul;
                float h = tex.height * scale * pxPerUnit / mapPxPerUnit * sizeMul;
                if (w <= 0.5f || h <= 0.5f)
                {
                    return;
                }
                // 朝向：aimInt=1 表示朝右（贴图水平镜像）
                FieldInfo fa = PlayerField("aimInt");
                object av = fa != null ? fa.GetValue(op) : null;
                byte[] fxData = GetBytes(op, "shieldData");
                // 镜像优先取自特效通道里的“本体镜像”标志（与本地网格一致），无负载时回退 aimInt
                bool? fxMirror = KnightFxSync.BodyMirrored(fxData);
                bool faceRight;
                bool unnSprite = !string.IsNullOrEmpty(sprite) &&
                    (sprite.StartsWith("knight_slug", StringComparison.Ordinal) ||
                     sprite.StartsWith("shroom_slug", StringComparison.Ordinal));
                if (unnSprite)
                {
                    // 乌恩/蘑菇蛞蝓形态使用当前朝向（aimInt），避免沿用普通骑士的镜像状态。
                    faceRight = av is int aiUnn && aiUnn == 1;
                }
                else if (!string.IsNullOrEmpty(sprite) &&
                    sprite.StartsWith("shadow_dash", StringComparison.Ordinal))
                {
                    // shadow_dash 帧本身是预翻转的：远端需要取反镜像，否则朝向会左右颠倒
                    faceRight = fxMirror.HasValue
                        ? !fxMirror.Value
                        : (av is int ai0 && ai0 == 1);
                }
                else
                {
                    faceRight = fxMirror ?? (av is int ai && ai == 1);
                }

                var md = new MeshDrawer(null, 4, 6);
                md.activate("knight_remote_body", mat, false, C32.d2c(uint.MaxValue), null);
                md.Col = C32.MulA(0xFFFFFFFFU, 1f);
                md.initForImgAndTexture(tex);
                md.uv_top = 0f;
                md.uv_height = 1f;
                if (faceRight)
                {
                    md.uv_left = 1f;
                    md.uv_width = -1f;
                }
                // 帧的“脚底线”位于图片自上而下 feet 比例处（knight_manifest.json）。
                // 效果屏幕 y 向上：脚底线应落在屏幕脚底 fy，
                // 故矩形中心 cy = fy + (feet - 0.5f) * h，脚底以上是身体、以下只留底边余量。
                float ff = RemoteFeetFraction(sprite);
                float cy = fy + (ff - 0.5f) * h;
                // 特效基准：本体屏幕中心 + 本体绘制高度（负载以“本体高度”为单位）
                DrawRemoteFx(fxData, true, fx, cy, h); // 身后层特效（PR0）
                DrawRemoteFuryGlow(fxData, fx, cy, pxPerUnit); // 亡者之怒红色光晕（PR0）
                DrawRemoteShelter(fxData, fx, cy, pxPerUnit, true); // 防御者纹章实心圆（PR0）
                md.Rect(fx, cy, w, h, false);
                BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
                DrawRemoteFx(fxData, false, fx, cy, h); // 身前层特效（PR1）
                DrawRemoteShelter(fxData, fx, cy, pxPerUnit, false); // 防御者纹章球体/圆环（PR1/PR2）
                DrawRemoteSpores(fxData, pxPerUnit);    // 蘑菇孢子粒子云
                DrawRemoteDive(fxData, pxPerUnit);      // 下砸骨剑/尖刺（实时状态）
                // 蜕变挽歌剑气：登记语义判定箱，供本地小骑士下劈弹起。
                KnightFxSync.ReadElegy(fxData, (cx, cy2, ew, eh) =>
                    NoteRemoteElegyRect(cx, cy2, ew, eh));
                // 拼刀：远端“骨钉攻击判定箱”（负载里是相对骑士中心的偏移），
                // 这里加上远端骑士锚点，换算成绝对地图格坐标后登记。
                // 注意 mx 是广播的 x（= 诺艾尔中心 = 骑士中心 + HurtCenterX），
                // my 是广播的 ay（= 骑士脚底），两者反推出骑士中心 (X, Y)。
                float anchorX = mx - KnightEntity.HurtCenterX;
                float anchorY = my - KnightEntity.CenterToFootY;
                KnightFxSync.ReadAttack(fxData, (dx, dy, rw, rh, kind) =>
                    NoteRemoteNailRect(anchorX + dx, anchorY + dy, rw, rh));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 远端复现小骑士特效：负载里的四边形是“相对本体中心、以本体高度为单位”的偏移，
        /// 这里用本体绘制时的屏幕中心与绘制高度还原，因此与本体尺寸严格成比例。
        /// </summary>
        private static void DrawRemoteFx(byte[] data, bool behind, float anchorX, float anchorY, float scale)
        {
            try
            {
                string firstSprite = null;
                int count = KnightFxSync.Read(data, out bool mirrored, (spriteId, pos, uv, col) =>
                {
                    int sid = spriteId;
                    bool isBehind = sid < 0;
                    if (isBehind)
                    {
                        sid = -sid - 1;
                    }
                    if (isBehind != behind)
                    {
                        return;
                    }
                    string sprite = KnightFxSync.NameOf(sid);
                    if (string.IsNullOrEmpty(sprite))
                    {
                        return;
                    }
                    if (firstSprite == null)
                    {
                        firstSprite = sprite;
                    }
                    Texture2D tex = sprite == "knight_spore_dot"
                        ? RemoteSporeDotTexture()
                        : LoadKnightFrame(sprite);
                    if (tex == null)
                    {
                        return;
                    }
                    Material mat = GetKnightMat(tex, sprite);
                    if (mat == null)
                    {
                        return;
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        pos[i * 2] = anchorX + pos[i * 2] * scale;
                        pos[i * 2 + 1] = anchorY + pos[i * 2 + 1] * scale;
                    }
                    uint rgba = (uint)((col[0] << 24) | (col[1] << 16) | (col[2] << 8) | col[3]);
                    Color32 c32 = C32.d2c(rgba);
                    C32 c = new C32(rgba);
                    var md = new MeshDrawer(null, 4, 6);
                    md.activate("knight_remote_fx", mat, false, c32, null);
                    md.initForImgAndTexture(tex);
                    md.Tri(0, 1, 2, false, false);
                    md.Tri(0, 2, 3, false, false);
                    md.PosD(pos[0], pos[1], c);
                    md.PosD(pos[2], pos[3], c);
                    md.PosD(pos[4], pos[5], c);
                    md.PosD(pos[6], pos[7], c);
                    // 逐角 UV：保留每帧/每个四边形的贴图裁切与镜像朝向
                    Vector2[] uvs = md.getUvArray();
                    uvs[0] = new Vector2(uv[0] / 255f, uv[1] / 255f);
                    uvs[1] = new Vector2(uv[2] / 255f, uv[3] / 255f);
                    uvs[2] = new Vector2(uv[4] / 255f, uv[5] / 255f);
                    uvs[3] = new Vector2(uv[6] / 255f, uv[7] / 255f);
                    BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
                });
            }
            catch (Exception)
            {
            }
        }

        private static Texture2D _remoteFuryGlowTex;

        /// <summary>
        /// 远端复现亡者之怒红色光晕：本地是程序化径向贴图，不走四边形通道；
        /// 这里读取 Fury 透明度字节，用同样的径向贴图在远端本体身后绘制。
        /// </summary>
        private static void DrawRemoteFuryGlow(byte[] data, float anchorX, float anchorY, float pxPerUnit)
        {
            try
            {
                byte alpha = KnightFxSync.ReadFury(data);
                if (alpha == 0)
                {
                    return;
                }
                if (_remoteFuryGlowTex == null)
                {
                    _remoteFuryGlowTex = KnightEntity.MakeRadialGlowTexture(64);
                }
                if (_remoteFuryGlowTex == null)
                {
                    return;
                }
                Material mat = GetKnightMat(_remoteFuryGlowTex, "knight_remote_fury_glow");
                if (mat == null)
                {
                    return;
                }
                float size = 3.2f * pxPerUnit;
                // 本地光晕中心 ≈ 骑士身体中心；远端 anchorY 就是身体绘制中心，
                // 不要再额外下移，否则会被本体/地面遮住，只在出刀姿势变化时露出。
                float gy = anchorY;
                var md = new MeshDrawer(null, 4, 6);
                md.activate("knight_remote_fury_glow", mat, false, C32.d2c(uint.MaxValue), null);
                md.initForImgAndTexture(_remoteFuryGlowTex);
                md.Col = new Color(1f, 0.25f, 0.15f, alpha / 255f);
                md.Rect(anchorX, gy, size, size, false);
                BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
            }
            catch (Exception)
            {
            }
        }

        private static Texture2D _remoteSporeDotTex;

        private static Texture2D RemoteSporeDotTexture()
        {
            if (_remoteSporeDotTex == null)
            {
                _remoteSporeDotTex = KnightEntity.MakeRadialGlowTexture(16);
            }
            return _remoteSporeDotTex;
        }

        private static Texture2D _remoteShelterCircleTex;
        private static Texture2D _remoteShelterSphereTex;

        /// <summary>
        /// 远端复现防御者纹章法阵：实心圆在身后，球体与圆环在身前。
        /// 本地是程序化贴图，通用四边形快照会跳过，这里用同一套贴图重建。
        /// </summary>
        private static void DrawRemoteShelter(byte[] data, float ax, float ay, float pxPerUnit, bool behind)
        {
            try
            {
                KnightFxSync.ShelterState s = KnightFxSync.ReadShelter(data);
                if (!s.Active || pxPerUnit <= 0f)
                {
                    return;
                }
                if (behind)
                {
                    if (_remoteShelterCircleTex == null)
                    {
                        _remoteShelterCircleTex = KnightEntity.MakeShelterCircleTexture(128);
                    }
                    DrawQuad(_remoteShelterCircleTex, "knight_remote_shelter_circle",
                        ax, ay, s.CircleRadius * 2f * pxPerUnit, new Color(0.1f, 0.18f, 0.75f, 1f));
                    return;
                }
                if (_remoteShelterSphereTex == null)
                {
                    _remoteShelterSphereTex = KnightEntity.MakeShelterSphereTexture(64);
                }
                // 五个球体，72° 均布，球心距 1.75 格，直径 1 格
                for (int i = 0; i < 5; i++)
                {
                    float ang = s.SphereAngle + i * (6.2831853f / 5f);
                    float sx = ax + Mathf.Cos(ang) * 1.75f * pxPerUnit;
                    float sy = ay - Mathf.Sin(ang) * 1.75f * pxPerUnit;
                    DrawQuad(_remoteShelterSphereTex, "knight_remote_shelter_sphere",
                        sx, sy, 1.0f * pxPerUnit, Color.white);
                }
                // 圆环：白→浅蓝，随扩张半径增大
                if (s.RingRadius >= 0f)
                {
                    float t = Mathf.Clamp01(s.RingRadius / Mathf.Max(0.01f, s.CircleRadius));
                    Color ringCol = Color.Lerp(Color.white, new Color(0.35f, 0.62f, 1f, 1f), t);
                    if (s.RingRadius >= s.CircleRadius && s.PatternTimer >= 0f)
                    {
                        ringCol.a = s.PatternTimer < 0.3f
                            ? 1f
                            : Mathf.Clamp01(1f - (s.PatternTimer - 0.3f) / 0.3f);
                    }
                    DrawRemoteRing(ax, ay, s.RingRadius * pxPerUnit, ringCol);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void DrawQuad(Texture2D tex, string key, float x, float y, float size, Color col)
        {
            if (tex == null)
            {
                return;
            }
            Material mat = GetKnightMat(tex, key);
            if (mat == null)
            {
                return;
            }
            var md = new MeshDrawer(null, 4, 6);
            md.activate(key, mat, false, C32.d2c(uint.MaxValue), null);
            md.initForImgAndTexture(tex);
            md.Col = col;
            md.Rect(x, y, size, size, false);
            BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
        }

        private static void DrawRemoteRing(float x, float y, float radius, Color col)
        {
            Material mat = GetKnightMat(Texture2D.whiteTexture, "knight_remote_white");
            if (mat == null)
            {
                return;
            }
            var md = new MeshDrawer(null, 64, 96);
            md.activate("knight_remote_shelter_ring", mat, false, C32.d2c(uint.MaxValue), null);
            md.Col = col;
            md.Circle(x, y, radius, 3.5f, false);
            BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
        }

        private static void DrawRemoteSpores(byte[] data, float pxPerUnit)
        {
            try
            {
                KnightFxSync.ReadSpores(data, (cx, cy, age, radius) =>
                {
                    if (age > 4.4f) return;
                    float alpha = age <= 4.1f ? 0.9f : Mathf.Clamp01((4.4f - age) / 0.3f) * 0.9f;
                    float sx = cx, sy = cy;
                    ToScreen(ref sx, ref sy);
                    DrawRemoteRing(sx, sy, radius * pxPerUnit, new Color(0.2f, 1f, 0.35f, alpha));
                });
            }
            catch (Exception)
            {
            }
        }

        /// <summary>下砸骨剑/尖刺：世界格坐标 + 自然比例（每格屏幕像素）绘制，与判定同源、无渲染滞后。</summary>
        private static void DrawRemoteDive(byte[] data, float pxPerUnit)
        {
            try
            {
                KnightFxSync.ReadDive(data, (spriteId, cx, cy, wGrid, hGrid, uvTop, uvH, flip) =>
                {
                    // 记录给“下劈弹起”用的判定矩形：按贴图类型套用与本地一致的判定偏移
                    // （骨剑 X-0.5 / Y+1；尖刺 X-0.25 / Y+1），否则远端判定与本地错开约 1 格
                    string recSprite = KnightFxSync.NameOf(spriteId);
                    float rcx = cx;
                    float rcy = cy;
                    if (!string.IsNullOrEmpty(recSprite) &&
                        recSprite.StartsWith("nail_upgrade", StringComparison.Ordinal))
                    {
                        rcx -= 0.5f;
                        rcy = cy - hGrid * 0.5f + 1f;   // 与本地一致：判定箱中心 = 底边 - 高度/2 + 1
                    }
                    else
                    {
                        rcx -= 0.25f;
                        rcy = cy - hGrid * 0.5f + 1f;
                    }
                    NoteRemoteDiveRect(rcx, rcy, wGrid, hGrid);
                    string sprite = KnightFxSync.NameOf(spriteId);
                    if (string.IsNullOrEmpty(sprite))
                    {
                        return;
                    }
                    Texture2D tex = LoadKnightFrame(sprite);
                    if (tex == null)
                    {
                        return;
                    }
                    Material mat = GetKnightMat(tex, sprite);
                    if (mat == null)
                    {
                        return;
                    }
                    float sx = cx;
                    float sy = cy;
                    ToScreen(ref sx, ref sy);
                    var md = new MeshDrawer(null, 4, 6);
                    md.activate("knight_remote_dive", mat, false, C32.d2c(uint.MaxValue), null);
                    md.initForImgAndTexture(tex);
                    md.uv_left = flip ? 1f : 0f;
                    md.uv_width = flip ? -1f : 1f;
                    md.uv_top = uvTop;
                    md.uv_height = uvH;
                    md.Rect(sx, sy, wGrid * pxPerUnit, hGrid * pxPerUnit, false);
                    BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
                });
            }
            catch (Exception)
            {
            }
        }

        private static byte[] GetBytes(object op, string name)
        {
            FieldInfo fi = PlayerField(name);
            return fi != null ? fi.GetValue(op) as byte[] : null;
        }

        // ---- 远端玩家已知位置（诺艾尔/小骑士；供受击后退方向判定用；每帧随渲染刷新）----
        private static readonly List<float> RemotePlayerXs = new List<float>();
        private static int RemotePlayerXsFrame = -1;

        private static void NoteRemotePlayerX(float x)
        {
            if (RemotePlayerXsFrame != Time.frameCount)
            {
                RemotePlayerXsFrame = Time.frameCount;
                RemotePlayerXs.Clear();
            }
            RemotePlayerXs.Add(x);
        }

        /// <summary>取最近一帧见过的、距 selfX 最近的远端玩家 X（诺艾尔/小骑士，用于 PvP 受击后退方向）。</summary>
        internal static bool TryGetNearestRemotePlayerX(float selfX, float maxDist, out float x)
        {
            x = 0f;
            try
            {
                if (RemotePlayerXsFrame < 0 || Time.frameCount - RemotePlayerXsFrame > 15)
                {
                    return false;
                }
                float best = maxDist;
                bool found = false;
                for (int i = 0; i < RemotePlayerXs.Count; i++)
                {
                    float d = Mathf.Abs(RemotePlayerXs[i] - selfX);
                    if (d <= best)
                    {
                        best = d;
                        x = RemotePlayerXs[i];
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

        // ---- 远端小骑士的骨剑/尖刺矩形（供下劈弹起判定用）----
        private static readonly List<Vector4> RemoteDiveRects = new List<Vector4>();
        private static int RemoteDiveRectsFrame = -1;

        private static void NoteRemoteDiveRect(float cx, float cy, float w, float h)
        {
            if (RemoteDiveRectsFrame != Time.frameCount)
            {
                RemoteDiveRectsFrame = Time.frameCount;
                RemoteDiveRects.Clear();
            }
            RemoteDiveRects.Add(new Vector4(cx, cy, w, h));
        }

        /// <summary>下劈弹起：与远端小骑士的骨剑/尖刺矩形是否相交。</summary>
        internal static int RemoteDiveRectCount =>
            RemoteDiveRectsFrame >= 0 && Time.frameCount - RemoteDiveRectsFrame <= 15 ? RemoteDiveRects.Count : 0;

        internal static bool OverlapsRemoteDiveRect(float cx, float cy, float halfW, float halfH)
        {
            try
            {
                if (RemoteDiveRectsFrame < 0 || Time.frameCount - RemoteDiveRectsFrame > 5)
                {
                    return false;
                }
                for (int i = 0; i < RemoteDiveRects.Count; i++)
                {
                    Vector4 r = RemoteDiveRects[i];
                    // 远端骨剑/尖刺的纵向基准与本地存在固定的口径差（实测约 1~3 格），
                    // 这里给较大的纵向容差，保证“下劈剑气横向对上了”就能弹起
                    if (Mathf.Abs(r.x - cx) <= r.z * 0.5f + halfW &&
                        Mathf.Abs(r.y - cy) <= r.w * 0.5f + halfH + 4f)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 远端小骑士的蜕变挽歌剑气判定箱（供下劈弹起判定用）----
        private static readonly List<Vector4> RemoteElegyRects = new List<Vector4>();
        private static int RemoteElegyRectsFrame = -1;

        private static void NoteRemoteElegyRect(float cx, float cy, float w, float h)
        {
            if (RemoteElegyRectsFrame != Time.frameCount)
            {
                RemoteElegyRectsFrame = Time.frameCount;
                RemoteElegyRects.Clear();
            }
            RemoteElegyRects.Add(new Vector4(cx, cy, w, h));
        }

        internal static int RemoteElegyRectCount =>
            RemoteElegyRectsFrame >= 0 && Time.frameCount - RemoteElegyRectsFrame <= 15
                ? RemoteElegyRects.Count
                : 0;

        internal static bool OverlapsRemoteElegyRect(float cx, float cy, float halfW, float halfH)
        {
            try
            {
                if (RemoteElegyRectsFrame < 0 || Time.frameCount - RemoteElegyRectsFrame > 5)
                {
                    return false;
                }
                for (int i = 0; i < RemoteElegyRects.Count; i++)
                {
                    Vector4 r = RemoteElegyRects[i];
                    if (Mathf.Abs(r.x - cx) <= r.z * 0.5f + halfW &&
                        Mathf.Abs(r.y - cy) <= r.w * 0.5f + halfH)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 远端小骑士的骨钉（剑气）攻击判定箱：绝对地图格坐标，供本地拼刀判定 ----
        // 数据来自特效负载 v5 段的“骨钉攻击判定箱”，随 PlayerInfo.shieldData 每帧同步；
        // 只在远端骑士被绘制时收集（即两端在同一张地图），因此不需要额外的房间过滤。
        private static readonly List<Vector4> RemoteNailRects = new List<Vector4>();
        private static int RemoteNailRectsFrame = -1;

        private static void NoteRemoteNailRect(float cx, float cy, float w, float h)
        {
            if (RemoteNailRectsFrame != Time.frameCount)
            {
                RemoteNailRectsFrame = Time.frameCount;
                RemoteNailRects.Clear();
            }
            RemoteNailRects.Add(new Vector4(cx, cy, w, h));
        }

        /// <summary>最近一帧收到的远端骨钉判定箱数量（诊断用）。</summary>
        internal static int RemoteNailRectCount =>
            RemoteNailRectsFrame >= 0 && Time.frameCount - RemoteNailRectsFrame <= 5
                ? RemoteNailRects.Count
                : 0;

        /// <summary>遍历“本帧仍有效”的远端骨钉判定箱（调试线框绘制用）。</summary>
        internal static void ForEachRemoteNailRect(Action<float, float, float, float> fn)
        {
            if (fn == null || RemoteNailRectsFrame < 0 || Time.frameCount - RemoteNailRectsFrame > 5)
            {
                return;
            }
            for (int i = 0; i < RemoteNailRects.Count; i++)
            {
                Vector4 r = RemoteNailRects[i];
                fn(r.x, r.y, r.z, r.w);
            }
        }

        /// <summary>
        /// 拼刀判定：本地骨钉判定箱（绝对格坐标，中心 + 宽高）是否与任一远端小骑士的骨钉判定箱相交。
        /// 数据过期（远端停止攻击后最多保留数帧）时返回 false。
        /// </summary>
        internal static bool OverlapsRemoteNailRect(float cx, float cy, float w, float h)
        {
            try
            {
                if (RemoteNailRectsFrame < 0 || Time.frameCount - RemoteNailRectsFrame > 5)
                {
                    return false;
                }
                for (int i = 0; i < RemoteNailRects.Count; i++)
                {
                    Vector4 r = RemoteNailRects[i];
                    if (Mathf.Abs(r.x - cx) <= (r.z + w) * 0.5f &&
                        Mathf.Abs(r.y - cy) <= (r.w + h) * 0.5f)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool _feetCacheLoaded;
        private static readonly Dictionary<string, float> RemoteFeetCache =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>读取骑士清单里每帧的脚底线比例（feet，自上而下 0~1），供远端贴地绘制。</summary>
        private static float RemoteFeetFraction(string sprite)
        {
            if (!_feetCacheLoaded)
            {
                _feetCacheLoaded = true;
                try
                {
                    string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk");
                    string json = Path.Combine(dir, "knight_manifest.json");
                    if (File.Exists(json))
                    {
                        string text = File.ReadAllText(json);
                        var rx = new Regex("\"([^\"]+)\"\\s*:\\s*\\{[^}]*\"feet\"\\s*:\\s*([0-9.]+)");
                        foreach (Match m in rx.Matches(text))
                        {
                            if (float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out float ff))
                            {
                                RemoteFeetCache[m.Groups[1].Value] = ff;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return RemoteFeetCache.TryGetValue(sprite, out float f) && f > 0.001f && f <= 1.001f ? f : 1f;
        }

        private static float GetF(object p, string name)
        {
            FieldInfo fi = PlayerField(name);
            return fi != null ? (float)fi.GetValue(p) : 0f;
        }

        private static void SetPlayerField(object p, string name, object value)
        {
            FieldInfo fi = PlayerField(name);
            if (fi != null)
            {
                fi.SetValue(p, value);
            }
        }

        /// <summary>带“骑士标记”的玩家：头顶条改为小骑士配色（黑血条 + 白灵魂条），跳过联机模组默认红/蓝。</summary>
        public static bool RenderHpMpBarPrefix(object op, float alpha)
        {
            try
            {
                if (op == null)
                {
                    return true;
                }
                FieldInfo marker = PlayerField("nameColor");
                object mv = marker != null ? marker.GetValue(op) : 0U;
                if (!(mv is uint u) || u != KnightMarker)
                {
                    return true;
                }
                FieldInfo fx = PlayerField("x");
                FieldInfo fy = PlayerField("y");
                FieldInfo fhp = PlayerField("hp");
                FieldInfo fhpmax = PlayerField("hpmax");
                FieldInfo fmp = PlayerField("mp");
                FieldInfo fmpmax = PlayerField("mpmax");
                if (fx == null || fy == null || fhp == null || fhpmax == null || fmp == null || fmpmax == null)
                {
                    return true;
                }
                float x = (float)fx.GetValue(op);
                float y = (float)fy.GetValue(op);
                float hp = (float)fhp.GetValue(op);
                float hpmax = (float)fhpmax.GetValue(op);
                float mp = (float)fmp.GetValue(op);
                float mpmax = (float)fmpmax.GetValue(op);
                ToScreen(ref x, ref y);
                y += 91f;
                float num = 42f;
                float hpRatio = hpmax > 0f ? Mathf.Clamp01(hp / hpmax) : 0f;
                float mpRatio = mpmax > 0f ? Mathf.Clamp01(mp / mpmax) : 0f;
                int hpW = X.IntC(num * hpRatio);
                int mpW = X.IntC(num * mpRatio);

                var md = new MeshDrawer(null, 4, 6);
                md.activate("hpmpbar_graphics", MTRX.MtrMeshNormal, false, C32.d2c(uint.MaxValue), null);
                // 底条：半透明黑（与原版一致）
                md.Col = C32.MulA(0xCC000000U, alpha);
                md.BoxBL(x - num - 1f, y - 2f, 3f + num * 2f, 4f, 0f, false);
                // 血条（左半）：白色底 + 黑色当前值（小骑士 HUD 同款“黑血条”）
                md.Col = C32.MulA(0xCCFFFFFFU, alpha);
                md.BoxBL(x - num, y, num, 2f, 0f, false);
                md.Col = C32.MulA(0xFF000000U, alpha);
                md.BoxBL(x - hpW, y, hpW, 2f, 0f, false);
                // 灵魂条（右半）：白色当前值
                md.Col = C32.MulA(0xFFFFFFFFU, alpha);
                md.BoxBL(x + 1f, y, mpW, 2f, 0f, false);
                BLIT.RenderToGLImmediate001(md, -1, -1, true, true, null);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>是否是小骑士自造的攻击魔法（命中缓存 _knightAttackMagic）。</summary>
        private static bool IsKnightMagic(MagicItem mg)
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                return k != null && k.IsKnightSpellMagic(mg);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ApplyHpDamagePrefix(ref int val, bool force, AttackInfo _Atk, object __instance)
        {
            _rewrote = false;
            _origVal = val;
            try
            {
                if (!(_Atk is NelAttackInfo na) || na.PublishMagic == null)
                {
                    return;
                }
                MagicItem mg = na.PublishMagic;
                // 【2026-09-17】不再“已是 PR_* 就跳过”：骑士所有攻击共用同一个缓存 MagicItem，
                // 嵌套调用时 postfix 的还原可能被打断，导致该实例残留 PR_PUNCH；
                // 一旦跳过，后续命中（如蓄力劈砍第 2/3 段）就没有预乘补偿 →
                // 表现就是“第一段 42、后两段 21”“第一下 50、后面 25”。所以每次都无条件补偿。
                // 【修复 2026-09-17】不再用 IsKnightMagic(缓存 MagicItem) 判断：
                // 骑士的法术/技艺各自新建 MagicItem，用缓存实例比对上会漏判一部分攻击，
                // 表现为“打诺艾尔第一下 50、第二下 25”（漏判的那次没做补偿）。
                // 骑士模式下本机诺艾尔不会自己施法（施法/输出已被 CombatGuard 拦掉），
                // 所以“处于骑士模式”就足以说明这次攻击来自小骑士。
                if (KnightEntity.Instance == null ||
                    (!KnightEntity.Instance.IsActive && !KnightEntity.Instance.HasOrphanFlukes))
                {
                    // 切回诺艾尔后仍在飞的吸虫仍算“小骑士的攻击”，
                    // 所以这里也要继续走伪装成玩家攻击 + 伤害补偿 + 标记的流程。
                    return;
                }
                // 同一次命中只补偿一次（见 _valDedupAtk 注释）
                if (ReferenceEquals(_valDedupAtk, na) && _valDedupFrame == Time.frameCount)
                {
                    return;
                }
                _valDedupAtk = na;
                _valDedupFrame = Time.frameCount;
                _origKind = mg.kind;
                _origHit = mg.hittype;
                _origKnockLen = na.knockback_len;
                _origKnockP = na.knockback_ratio_p;
                _origKnockT = na.knockback_ratio_t;
                _origApplyKnock = na._apply_knockback_current;
                _origAttr = na.attr;
                _origBurstVx = na.burst_vx;
                _origBurstVy = na.burst_vy;
                mg.kind = MGKIND.PR_PUNCH;
                mg.hittype = (MGHIT)((int)MGHIT.PR | (int)MGHIT.IMMEDIATE | (int)MGHIT.NORMAL_ATTACK);
                bool isNoelTarget = IsNoelTarget(__instance);
                byte fb = 0;
                if (isNoelTarget)
                {
                    // 玩家对玩家：服务器/收包端会压缩玩家伤害，按配置预乘抵消
                    // （默认 2 = 假设被打五折；实测偏少可在 cfg 里调大，如 4）
                    val = Mathf.Max(1, (int)Math.Round(val * KnightInCradlePlugin.PvPDamageMultiplier));
                    // 【2026-09-17 已删除】这里原有一条“目标非满血再 ×2”，用于抵消 AIC 的玩家侧减伤；
                    // 该减伤现已由收包端 M2PrADmg.applyHpDamageRatio 后缀补丁挡掉（只作用于小骑士的攻击），
                    // 保留会造成非满血时伤害翻倍，故删除。
                    // 【受击效果实验已回退】原来这里用 attr/burst 承载“着火/击飞”标记，
                    // 撤掉以免影响伤害与诺艾尔自身表现。
                    // ① 受击反馈标签（与伤害无关）：
                    //    knockback_ratio_t = 0.777 → “来自小骑士”（诺艾尔自己的攻击是 1.0）
                    //    attr = FIRE               → 着火（复仇之魂 / 深渊尖啸）
                    //    burst_vx / burst_vy       → 直线击飞（冲刺劈砍 / 下砸，方向＝远离攻击者）
                    fb = KnightEntity.TakeHitFeedback();
                    if (fb == 1 || fb == 3)
                    {
                        // 冲刺劈砍 / 下砸 → 直线击飞（方向＝远离攻击者）；
                        // 冲刺劈砍 (fb=3) 的击飞速度/距离是常规的 1.5 倍。
                        float mul = (fb == 3) ? 1.5f : 1f;
                        float dir = 1f;
                        try
                        {
                            M2Attackable tgt = __instance as M2Attackable;
                            KnightEntity kk = KnightEntity.Instance;
                            if (tgt != null && kk != null)
                            {
                                dir = tgt.x >= kk.X ? 1f : -1f;
                            }
                        }
                        catch (Exception)
                        {
                        }
                        na.burst_vx = dir * 0.28f * mul;
                        na.burst_vy = -0.12f * mul;
                    }
                    else if (fb == 5)
                    {
                        // 暗影冲刺：只作为轻受击标记，不携带击退/击飞。
                        na.burst_center = KnightEntity.ShadowDashPacketMarker;
                        na.burst_vx = 0f;
                        na.burst_vy = 0f;
                    }
                    else
                    {
                        // 普攻 / 蓄力劈砍 / 旋风劈砍 → 不携带击飞；同时清掉同一
                        // NelAttackInfo 复用可能残留的 burst，避免被误判成击飞。
                        na.burst_vx = 0f;
                        na.burst_vy = 0f;
                    }
                    if (fb == 2)
                    {
                        // 复仇之魂 / 深渊尖啸 → 着火
                        na.attr = MGATTR.FIRE;
                    }
                    else
                    {
                        na.attr = _origAttr;
                    }
                }
                else
                {
                    // 别人开战生成的魔物（远端代理）：服务器/收包端压缩到约 1/3，
                    // 预乘 3 抵消，使骑士对这类魔物的伤害与本地一致
                    val *= 3;
                }
                // 补上诺艾尔普攻同款的击退参数，让受击方有击退/受击表现
                na.knockback_len = 0.725f;
                na.knockback_ratio_p = 1f;
                na.knockback_ratio_t = 1f;
                na._apply_knockback_current = true;
                if (fb == 5)
                {
                    // 暗影冲刺改为轻受击硬直：清掉全部击退参数。
                    na.knockback_len = 0f;
                    na.knockback_ratio_p = 0f;
                    na._apply_knockback_current = false;
                }
                if (isNoelTarget)
                {
                    // 必须在通用击退参数之后写入：这是收包端识别“小骑士攻击”
                    // 的标记（诺艾尔自己的攻击恒为 1.0）。
                    na.knockback_ratio_t = 0.777f;
                }
                _rewrote = true;
            }
            catch (Exception)
            {
                _rewrote = false;
            }
        }

        /// <summary>目标是否是远端玩家（M2Gunmu.remoteEnemyKey 含 "noel"）。</summary>
        internal static bool IsRemoteNoelProxy(M2Attackable a)
        {
            return a != null && IsNoelTarget(a);
        }

        // ---- 远端玩家血量缓存：token → hp/hpmax 比例（每帧从同步包刷新）----
        private static readonly Dictionary<long, float> RemoteHpRatio = new Dictionary<long, float>();

        /// <summary>从联机同步的 PlayerInfo 里记录该玩家当前血量比例。</summary>
        private static void NoteRemoteHp(object playerInfo)
        {
            try
            {
                FieldInfo ft = PlayerField("playerToken");
                FieldInfo fh = PlayerField("hp");
                FieldInfo fm = PlayerField("hpmax");
                if (ft == null || fh == null || fm == null)
                {
                    return;
                }
                long token = Convert.ToInt64(ft.GetValue(playerInfo) ?? 0L);
                float max = (float)fm.GetValue(playerInfo);
                if (max > 0.01f)
                {
                    RemoteHpRatio[token] = Mathf.Clamp01((float)fh.GetValue(playerInfo) / max);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>该远端代理对应的玩家是否处于“非满血”状态（拿不到数据时按满血处理，不做补偿）。</summary>
        private static bool IsRemoteTargetNotFull(object m2Gunmu)
        {
            try
            {
                if (m2Gunmu == null)
                {
                    return false;
                }
                FieldInfo f = m2Gunmu.GetType().GetField("remoteToken");
                if (f == null)
                {
                    return false;
                }
                long token = Convert.ToInt64(f.GetValue(m2Gunmu) ?? 0L);
                return RemoteHpRatio.TryGetValue(token, out float r) && r < 0.999f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsNoelTarget(object m2Gunmu)
        {
            try
            {
                if (m2Gunmu == null)
                {
                    return false;
                }
                FieldInfo f = m2Gunmu.GetType().GetField("remoteEnemyKey");
                string key = f != null ? f.GetValue(m2Gunmu) as string : null;
                return key != null && key.IndexOf("noel", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ApplyHpDamagePostfix(AttackInfo _Atk)
        {
            if (!_rewrote)
            {
                return;
            }
            try
            {
                if (_Atk is NelAttackInfo na && na.PublishMagic != null)
                {
                    na.PublishMagic.kind = _origKind;
                    na.PublishMagic.hittype = _origHit;
                    na.knockback_len = _origKnockLen;
                    na.knockback_ratio_p = _origKnockP;
                    na.knockback_ratio_t = _origKnockT;
                    na._apply_knockback_current = _origApplyKnock;
                    na.attr = _origAttr;
                    na.burst_vx = _origBurstVx;
                    na.burst_vy = _origBurstVy;
                }
            }
            catch (Exception)
            {
            }
            _rewrote = false;
        }
    }
}
