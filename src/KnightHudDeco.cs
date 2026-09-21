using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using nel;
using UnityEngine;
using XX;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    /// <summary>
    /// 小骑士 HUD 辅助层（OnGUI）：
    /// - 骑士模式下始终显示（HUD 常显，不依赖“/”键）；
    /// - 负责 HUD 本体淡入进度（RevealProgress）与梦语文本框；
    /// - hud_deco 系列装饰图改为“相对血条框”定位：
    ///   直接读 MdH/MdM 填充网格的实际渲染矩形（drawMHBar 前 4 顶点），
    ///   由固定边推算“充满时”的 HP/MP 条中心；deco1 在两中心中点上方 50px、
    ///   deco2/3 分别在 HP/MP 条中心下方 20px。
    /// </summary>
    public class KnightHudDeco : MonoBehaviour
    {
        private Texture2D _deco1; // hud_deco_1：横条（中点上方）
        private Texture2D _deco2; // hud_deco_2：角饰（左端下方）
        private Texture2D _deco3; // hud_deco_3：角饰（右端下方）
        private bool _loaded;
        private static float _decoFadeTimer;   // HUD 本体（血条网格）淡入计时
        private static float _decoRevealTimer; // deco 图片展开淡入计时（可被传送重播）
        private static bool _revealPlayed;     // 本次存档会话是否已播放过 HUD 淡入
        private static bool _fastTraveling;    // 快速旅行黑屏传送期间：deco 立即隐藏
        private static float _fastTravelStartTime;
        private const float FadeDelay = 0.5f;    // 等待 0.5 秒
        private const float FadeDuration = 2.0f; // 展开总时长 2 秒

        // ---- 相对血条框的定位常量（单位：游戏网格像素 mesh px；渲染时按 UI 缩放）----
        private const float BarFullWidthPx = 140f;     // 充满时填充段宽度（drawMHBar 固定值）
        private const float Deco1AbovePx = -25f;       // deco1 中心：HP/MP 条中心中点下方 25px
        private const float Deco2BelowPx = 70f;        // deco2 中心：HP 条中心下方 70px
        private const float Deco3BelowPx = 70f;        // deco3 中心：MP 条中心下方 70px
        private const float Deco1ShiftX = 7f;          // deco1 横向偏移：+右
        private const float Deco2ShiftX = -80f;        // deco2 横向偏移：-左
        private const float Deco3ShiftX = 87f;         // deco3 横向偏移：+右
        private const float HudMeshPxScale = 1f;       // 网格像素→屏幕虚拟像素系数（实测 1:1）

        private static readonly FieldInfo FMdH = AccessTools.Field(typeof(UIStatus), "MdH");
        private static readonly FieldInfo FMdM = AccessTools.Field(typeof(UIStatus), "MdM");

        // 梦语文本框（梦钉命中后显示，自动淡出）
        private static string _dreamText;
        private static float _dreamTextTimer;
        private const float DreamTextFadeIn = 0.3f;
        private const float DreamTextHold = 2.0f;
        private const float DreamTextFadeOut = 0.8f;
        private static GUIStyle _dreamStyle;
        private static Font _dreamFont;

        /// <summary>梦钉命中：显示一句梦语并自动计时淡出。</summary>
        public static void ShowDreamText(string text)
        {
            _dreamText = text;
            _dreamTextTimer = DreamTextFadeIn + DreamTextHold + DreamTextFadeOut;
        }

        public static void HideDreamText()
        {
            _dreamText = null;
            _dreamTextTimer = 0f;
        }

        private void Update()
        {
            if (_dreamTextTimer > 0f)
            {
                _dreamTextTimer = Mathf.Max(0f, _dreamTextTimer - Time.unscaledDeltaTime);
                if (_dreamTextTimer <= 0f)
                {
                    _dreamText = null;
                }
            }
        }

        /// <summary>当前淡入进度 0~1（含 0.5 秒等待），供 HUD 本体跟随淡入使用。</summary>
        public static float RevealProgress =>
            Mathf.Clamp01((_decoFadeTimer - FadeDelay) / FadeDuration);

        /// <summary>
        /// 切出小骑士时调用：本次存档会话第一次切换播放渐显动画，
        /// 之后直接满进度显示、无进入动画。
        /// </summary>
        public static void ResetReveal()
        {
            _decoFadeTimer = _revealPlayed ? (FadeDelay + FadeDuration) : 0f;
            _decoRevealTimer = _decoFadeTimer;
        }

        /// <summary>读档/新游戏后调用：本次会话第一次切小骑士重新播放渐显动画。</summary>
        public static void ResetRevealForSaveLoad()
        {
            _revealPlayed = false;
            _decoFadeTimer = 0f;
            _decoRevealTimer = 0f;
            _fastTraveling = false;
        }

        /// <summary>快速旅行开始（黑屏传送）：立即隐藏 deco，HUD 本体不受影响。</summary>
        public static void OnFastTravelStart()
        {
            _fastTraveling = true;
            _fastTravelStartTime = Time.unscaledTime;
            _decoRevealTimer = 0f;
        }

        /// <summary>
        /// 快速旅行到达目的地：deco 重播展开淡入（同首次切出小骑士）。
        /// </summary>
        public static void OnFastTravelArrive()
        {
            if (!_fastTraveling)
            {
                return;
            }
            _fastTraveling = false;
            _decoRevealTimer = 0f;
        }

        /// <summary>
        /// “/”常显开关打开时（小骑士模式）调用：重播 deco 两侧展开淡入。
        /// </summary>
        public static void OnKnightHudShow()
        {
            _decoRevealTimer = 0f;
        }

        private void Awake()
        {
            try
            {
                LoadTextures();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[KnightHudDeco] 素材加载失败: " + e);
            }
        }

        private void LoadTextures()
        {
            string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk", "sprites", "hud_deco");
            _deco1 = LoadTex(Path.Combine(dir, "hud_deco_1.png"));
            _deco2 = LoadTex(Path.Combine(dir, "hud_deco_2.png"));
            _deco3 = LoadTex(Path.Combine(dir, "hud_deco_3.png"));
            _loaded = _deco1 != null && _deco2 != null && _deco3 != null;
        }

        private static Texture2D LoadTex(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (tex.LoadImage(File.ReadAllBytes(path)))
            {
                return tex;
            }
            Destroy(tex);
            return null;
        }

        /// <summary>
        /// 读取 MdH/MdM 填充网格的实际渲染矩形（drawMHBar 填充段，前 4 顶点），
        /// 返回“充满时”HP/MP 条中心（单位：mesh px，相对 HUD 锚点）。
        /// HP 从右边缘向左充满、MP 从左边缘向右充满，固定边不随数值变化，
        /// 因此由固定边向条内推 70px 即得充满时的条中心，数值不满也不漂移。
        /// </summary>
        private static bool TryGetBarCenters(UIStatus ui, out float hpCx, out float mpCx, out float cy)
        {
            hpCx = -76f; // 分析兜底（face_shift=0、Qu=0 时原版布局的近似值）
            mpCx = 70f;
            cy = 0f;
            if (FMdH == null || FMdM == null)
            {
                return false;
            }
            MeshDrawer mh = FMdH.GetValue(ui) as MeshDrawer;
            MeshDrawer mm = FMdM.GetValue(ui) as MeshDrawer;
            if (mh == null || mm == null)
            {
                return false;
            }
            Vector3[] vaH = mh.getVertexArray();
            Vector3[] vaM = mm.getVertexArray();
            if (vaH == null || vaH.Length < 4 || vaM == null || vaM.Length < 4)
            {
                return false;
            }
            // 填充段矩形：v0..v3，其中 v0/v3 在固定边（HP=右、MP=左）
            float hpMaxX = Mathf.Max(vaH[0].x, vaH[3].x);
            float mpMinX = Mathf.Min(vaM[0].x, vaM[3].x);
            float minY = Mathf.Min(vaH[0].y, vaH[1].y, vaH[2].y, vaH[3].y);
            float maxY = Mathf.Max(vaH[0].y, vaH[1].y, vaH[2].y, vaH[3].y);
            hpCx = hpMaxX * IN.ppu - BarFullWidthPx * 0.5f;
            mpCx = mpMinX * IN.ppu + BarFullWidthPx * 0.5f;
            cy = (minY + maxY) * 0.5f * IN.ppu;
            return true;
        }

        /// <summary>
        /// 计算 HUD 锚点（UIStatus Gob 原点）在当前屏幕上的像素位置，
        /// 以及“充满时”HP/MP 条中心（mesh px，相对该锚点）。
        /// Gob 本地坐标已含 base_y_level / uipic_lr / 镜头位移。
        /// </summary>
        private static bool TryGetHudAnchor(out float sx, out float sy, out float hpCx, out float mpCx, out float cy)
        {
            sx = sy = hpCx = mpCx = cy = 0f;
            UIStatus ui = UIStatus.Instance;
            if (ui == null)
            {
                return false;
            }
            Transform gobT = ui.GetGob() != null ? ui.GetGob().transform : null;
            if (gobT == null)
            {
                return false;
            }
            if (!TryGetBarCenters(ui, out hpCx, out mpCx, out cy))
            {
                return false;
            }
            Vector3 gl = gobT.localPosition;
            float gobOriginX = IN.w * 0.5f + gl.x * IN.ppu;   // 虚拟像素，左上原点
            float gobOriginY = IN.hh - gl.y * IN.ppu;         // 虚拟像素，y 向下
            sx = gobOriginX * IN.pixel_scale;
            sy = gobOriginY * IN.pixel_scale;
            return true;
        }

        /// <summary>网格像素偏移 → 屏幕像素（含 UI 缩放）。</summary>
        private static float Px(float meshPx, float anchorScreen)
        {
            return anchorScreen + meshPx * HudMeshPxScale * IN.pixel_scale;
        }

        /// <summary>
        /// 按裁剪矩形绘制贴图：贴图始终画在自身位置（整数像素），
        /// 由裁剪窗口决定可见区域，避免渐显期间的亚像素抖动。
        /// </summary>
        private static void DrawClippedTexture(Rect clip, float fullX, float fullY, float fullW, float fullH, Texture2D tex)
        {
            int cx = Mathf.RoundToInt(clip.x);
            int cy = Mathf.RoundToInt(clip.y);
            int cw = Mathf.Max(1, Mathf.RoundToInt(clip.width));
            int ch = Mathf.Max(1, Mathf.RoundToInt(clip.height));
            int fx = Mathf.RoundToInt(fullX);
            int fy = Mathf.RoundToInt(fullY);
            int fw = Mathf.RoundToInt(fullW);
            int fh = Mathf.RoundToInt(fullH);
            GUI.BeginClip(new Rect(cx, cy, cw, ch));
            GUI.DrawTexture(new Rect(fx - cx, fy - cy, fw, fh), tex);
            GUI.EndClip();
        }

        private void OnGUI()
        {
            // 护符界面打开时隐藏 HUD 装饰，避免叠在护符 UI 之上
            if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
            {
                return;
            }
            // 骑士模式始终显示；切回诺艾尔时隐藏并重置淡入
            if (!_loaded || !KnightInCradlePlugin.KnightModeActive)
            {
                _decoFadeTimer = 0f; // 关闭时立即隐藏并重置淡入
                return;
            }
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            // 梦语文本框：独立于 HUD 装饰，先绘制
            DrawDreamText();
            // 快速旅行黑屏传送期间：deco 立即隐藏（到目的地后由 OnFastTravelArrive 重播淡入）
            if (_fastTraveling)
            {
                // 兜底：传送流程异常未触发到达时，超时后恢复 deco
                if (Time.unscaledTime - _fastTravelStartTime > 8f)
                {
                    _fastTraveling = false;
                }
                return;
            }
            try
            {
                // HUD 隐藏检测：base_y_level <= 0 表示 UI 已滑出屏幕，装饰跟随隐藏
                UIStatus ui = UIStatus.Instance;
                if (ui != null)
                {
                    float baseY = (float)AccessTools.Field(typeof(UIStatus), "base_y_level").GetValue(ui);
                    if (baseY <= 0.01f)
                    {
                        return;
                    }
                }
                if (!TryGetHudAnchor(out float ax, out float ay, out float hpCx, out float mpCx, out float cy))
                {
                    return;
                }
                // 淡入进度：先等 0.5 秒，再在 1.5 秒内匀速推进
                _decoFadeTimer = Mathf.Min(FadeDelay + FadeDuration, _decoFadeTimer + Time.deltaTime);
                if (_decoFadeTimer >= FadeDelay + FadeDuration)
                {
                    _revealPlayed = true; // 淡入播放完毕，本次会话不再重复
                }
                // deco 用独立计时：快速旅行到达后可单独重播展开淡入（不影响 HUD 本体）
                _decoRevealTimer = Mathf.Min(FadeDelay + FadeDuration, _decoRevealTimer + Time.deltaTime);
                float t = Mathf.Clamp01((_decoRevealTimer - FadeDelay) / FadeDuration);
                if (t <= 0.01f)
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
        }


        /// <summary>绘制梦语文本框：全屏宽、3 格高黑色半透明矩形 + 白色居中文字，自动淡入淡出。</summary>
        private void DrawDreamText()
        {
            if (string.IsNullOrEmpty(_dreamText) || _dreamTextTimer <= 0f)
            {
                return;
            }
            float total = DreamTextFadeIn + DreamTextHold + DreamTextFadeOut;
            float remain = _dreamTextTimer;
            float textAlpha;
            if (remain > DreamTextHold + DreamTextFadeOut)
            {
                textAlpha = Mathf.Clamp01((total - remain) / DreamTextFadeIn);
            }
            else if (remain > DreamTextFadeOut)
            {
                textAlpha = 1f;
            }
            else
            {
                textAlpha = Mathf.Clamp01(remain / DreamTextFadeOut);
            }
            if (textAlpha <= 0.01f)
            {
                return;
            }
            float s = Mathf.Max(0.5f, Screen.height / 1080f);
            float boxH = 160f * s; // 2.5 格 ≈ 160px @1080p
            float boxY = Screen.height * 0.25f - boxH * 0.5f;
            Rect box = new Rect(0f, boxY, Screen.width, boxH);
            // 黑色 50% 透明度矩形
            GUI.color = new Color(0f, 0f, 0f, 0.5f * textAlpha);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            // 白色梦语居中
            EnsureDreamStyle();
            GUI.color = new Color(1f, 1f, 1f, textAlpha);
            GUI.Label(box, _dreamText, _dreamStyle);
        }

        private static void EnsureDreamStyle()
        {
            if (_dreamStyle != null)
            {
                return;
            }
            if (_dreamFont == null)
            {
                _dreamFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "SimSun", "NSimSun", "宋体", "Microsoft YaHei", "Yu Gothic UI" }, 56);
            }
            _dreamStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                font = _dreamFont,
                fontSize = 56
            };
            _dreamStyle.normal.textColor = Color.white;
        }
    }
}
