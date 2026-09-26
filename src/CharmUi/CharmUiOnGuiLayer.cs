using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using XX;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符 UI 的 OnGUI（IMGUI）渲染层。
    /// AIC 构建裁掉了 uGUI 着色器，OnGUI 是唯一确定可用的渲染通道（与 HUD 相同）。
    /// 职责：
    /// 1. 按 layout.json 绘制静态布局（背景/边框/网格图标/槽位点/文字占位）；
    /// 2. 按 CharmUiController 状态绘制动态内容（已装备栏图标、cost_white/cost_overcharm、
    ///    描述面板、选中高亮），并隐藏已装备护符的下层图标。
    /// </summary>
    public sealed class CharmUiOnGuiLayer : MonoBehaviour
    {
        private UiExport _data;
        private readonly Dictionary<string, Texture2D> _textures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GUIStyle> _styles = new Dictionary<string, GUIStyle>();
        private readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        private readonly Dictionary<string, Rect> _rects =
            new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hidden =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _imageDir = "";
        private bool _loaded;
        private float _scale = 1f;
        private Rect _canvasRect;
        private bool _animating;      // 开合动画进行中（OpenScale < 1）
        private float _bgAlpha = 0.784f; // 背景遮罩透明度（与布局 Background 元素一致）
        // 装卸平移动画：缓存起止位置（网格槽位 ↔ 已装备槽位）
        private int _flyCachedId;
        private bool _flyCachedEquip;
        private bool _flyCacheValid;
        private Rect _flyFrom;
        private Rect _flyTo;
        private Texture2D _white;
        private Texture2D _cursorTex; // AIC 原版指针 curs_hand（charm_ui/images/curs_hand.png）
        private const float CursorHotX = 3f;   // 指尖热点（原版 53x74 画布 (21,28) → 图像局部 (3,2)）
        private const float CursorHotY = 2f;
        private const float CursorScale = 1f;  // 与原版 SetCursor 原生尺寸一致，可整体缩放微调

        /// <summary>行为控制器（由 Behaviour 在创建后挂上）。</summary>
        public CharmUiController Controller;

        public static CharmUiOnGuiLayer Create(GameObject host, string dir)
        {
            var layer = host.AddComponent<CharmUiOnGuiLayer>();
            if (!layer.Init(dir))
            {
                Destroy(layer);
                return null;
            }
            layer.enabled = false;
            return layer;
        }

        public void SetVisible(bool v)
        {
            enabled = v;
        }

        public void SetHidden(string path, bool v)
        {
            if (v)
            {
                _hidden.Add(path);
            }
            else
            {
                _hidden.Remove(path);
            }
        }

        public bool TryGetRect(string path, out Rect r)
        {
            return _rects.TryGetValue(path, out r);
        }

        public Rect CanvasRect => _canvasRect;
        public float Scale => _scale;

        /// <summary>按护符 id 取图标纹理。</summary>
        public Texture2D GetCharmIcon(int id)
        {
            CharmData cd = CharmDatabase.Get(id);
            if (cd == null)
            {
                return null;
            }
            return GetTextureByPrefix(cd.IconFile);
        }

        /// <summary>按导出顺序返回网格里出现的护符 id（去重，含 41）。</summary>
        public List<int> GetGridCharmIds()
        {
            var list = new List<int>();
            var seen = new HashSet<int>();
            if (_data == null || _data.elements == null)
            {
                return list;
            }
            foreach (UiElementData el in _data.elements)
            {
                if (el.kind != "image" || el.image == null || string.IsNullOrEmpty(el.image.file))
                {
                    continue;
                }
                if (MatchCharmIcon(el.image.file, out int id) && seen.Add(id))
                {
                    list.Add(id);
                }
            }
            return list;
        }

        private bool Init(string dir)
        {
            string jsonPath = Path.Combine(dir, "layout.json");
            if (!File.Exists(jsonPath))
            {
                return false;
            }
            try
            {
                _data = JsonConvert.DeserializeObject<UiExport>(File.ReadAllText(jsonPath));
            }
            catch (Exception)
            {
                return false;
            }
            if (_data == null || _data.elements == null)
            {
                return false;
            }
            _imageDir = Path.Combine(dir, "images");
            CharmAudio.Init();
            _loaded = true;
            return true;
        }

        private Texture2D GetTexture(string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }
            if (_textures.TryGetValue(file, out Texture2D tex))
            {
                return tex;
            }
            string path = Path.Combine(_imageDir, file);
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
                Destroy(tex);
                return null;
            }
            _textures[file] = tex;
            return tex;
        }

        private Texture2D GetTextureByPrefix(string prefix)
        {
            if (_data == null || _data.elements == null)
            {
                return null;
            }
            foreach (UiElementData el in _data.elements)
            {
                if (el.kind == "image" && el.image != null && !string.IsNullOrEmpty(el.image.file) &&
                    el.image.file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return GetTexture(el.image.file);
                }
            }
            return GetTexture(prefix + ".png");
        }

        private static bool MatchCharmIcon(string file, out int id)
        {
            id = -1;
            if (string.IsNullOrEmpty(file))
            {
                return false;
            }
            CharmData[] all = CharmDatabase.All;
            for (int i = 0; i < all.Length; i++)
            {
                string icon = all[i].IconFile;
                if (file.Length >= icon.Length &&
                    string.Compare(file, 0, icon, 0, icon.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (file.Length == icon.Length || file[icon.Length] == '.'))
                {
                    id = all[i].Id;
                    return true;
                }
            }
            return false;
        }

        private void RefreshMetrics()
        {
            float refW = _data.canvas != null && _data.canvas.referenceResolution.x > 1f
                ? _data.canvas.referenceResolution.x
                : 1920f;
            float refH = _data.canvas != null && _data.canvas.referenceResolution.y > 1f
                ? _data.canvas.referenceResolution.y
                : 1080f;
            float cfgScale = KnightInCradlePlugin.CharmUiScale != null
                ? KnightInCradlePlugin.CharmUiScale.Value
                : 1f;
            float cfgOx = KnightInCradlePlugin.CharmUiOffsetX != null
                ? KnightInCradlePlugin.CharmUiOffsetX.Value
                : 0f;
            float cfgOy = KnightInCradlePlugin.CharmUiOffsetY != null
                ? KnightInCradlePlugin.CharmUiOffsetY.Value
                : 0f;
            _scale = (IN.w / refW) * IN.pixel_scale * cfgScale;
            _canvasRect = new Rect(cfgOx, cfgOy, refW * _scale, refH * _scale);
            // sign 点击震动：给画布加随机像素偏移（UI 整体小幅晃动，随 SignShake 衰减）
            if (Controller != null && Controller.SignShake > 0.01f)
            {
                Vector2 sh = UnityEngine.Random.insideUnitCircle * Controller.SignShake;
                _canvasRect = new Rect(_canvasRect.x + sh.x, _canvasRect.y + sh.y,
                    _canvasRect.width, _canvasRect.height);
            }
            // 开合动画：从中间向两边逐渐显示 / 从两边向中间逐渐收回 ——
            // 用与背景同色的黑色幕布盖住未显示区域（UI 始终按最终位置绘制，不做几何压缩）。
            float s = Controller != null ? Controller.OpenScale : 1f;
            if (s >= 1f)
            {
                s = 1f;
            }
            _animating = s < 1f;
            // 从布局读取背景元素透明度（找不到时用 0.784 兜底）
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (el != null && el.path != null &&
                        el.path.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _bgAlpha = Mathf.Clamp01(el.color.a);
                        break;
                    }
                }
            }

            _rects.Clear();
            _rects[""] = _canvasRect;
            for (int ei = 0; ei < _data.elements.Length; ei++)
            {
                UiElementData el = _data.elements[ei];
                if (!_rects.TryGetValue(el.parent ?? "", out Rect pr))
                {
                    continue;
                }
                float w = el.sizeDelta.x * _scale;
                float h = el.sizeDelta.y * _scale;
                float ax = pr.x + pr.width * el.anchorMin.x;
                float ay = pr.y + pr.height * (1f - el.anchorMax.y);
                float lx = ax + el.anchoredPosition.x * _scale - w * el.pivot.x;
                float ly = ay - el.anchoredPosition.y * _scale - h * (1f - el.pivot.y);
                var rect = new Rect(lx, ly, w, h);
                if (ei == 0 && string.IsNullOrEmpty(el.parent))
                {
                    rect = _canvasRect;
                }
                _rects[el.path] = rect;
            }
        }

        private void OnGUI()
        {
            if (!_loaded || Event.current.type != EventType.Repaint)
            {
                return;
            }
            RefreshMetrics();
            RefreshFlightCache();
            try
            {
                DrawStaticElements();
                if (Controller != null)
                {
                    DrawDynamic();
                }
                // 开合动画幕布：最后绘制，盖住尚未显示的区域
                if (_animating)
                {
                    DrawRevealCurtains();
                }
                // 鼠标指针（AIC 原版 curs_hand 样式）：放在最上层，热点对准鼠标位置
                DrawMouseCursor();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>在护符界面内绘制 AIC 原版鼠标指针（curs_hand，18x24 原生尺寸）。</summary>
        private void DrawMouseCursor()
        {
            if (_cursorTex == null)
            {
                _cursorTex = GetTexture("curs_hand.png");
            }
            if (_cursorTex == null)
            {
                return;
            }
            float mx = UnityEngine.Input.mousePosition.x;
            float my = Screen.height - UnityEngine.Input.mousePosition.y;
            float w = 18f * CursorScale;
            float h = 24f * CursorScale;
            GUI.DrawTexture(
                new Rect(mx - CursorHotX * CursorScale, my - CursorHotY * CursorScale, w, h),
                _cursorTex, ScaleMode.StretchToFill, true);
        }

        /// <summary>
        /// 开合动画幕布：以画布中心为轴，用与背景同色的黑色矩形盖住左右未显示区域。
        /// 打开时幕布从中心向两边退开（UI 从中间向两边逐渐显示）；
        /// 关闭时幕布从两边向中心合拢（UI 从两边向中间逐渐收回）。
        /// </summary>
        private void DrawRevealCurtains()
        {
            float s = Controller != null ? Mathf.Clamp01(Controller.OpenScale) : 1f;
            if (s >= 1f)
            {
                return;
            }
            float cx = _canvasRect.x + _canvasRect.width * 0.5f;
            float half = _canvasRect.width * s * 0.5f;
            float left = Mathf.Max(_canvasRect.x, cx - half);
            float right = Mathf.Min(_canvasRect.x + _canvasRect.width, cx + half);
            float top = _canvasRect.y;
            float bottom = _canvasRect.y + _canvasRect.height;
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, _bgAlpha);
            if (left > _canvasRect.x + 0.5f)
            {
                GUI.DrawTexture(new Rect(_canvasRect.x, top, left - _canvasRect.x, bottom - top),
                    Texture2D.whiteTexture);
            }
            if (right < _canvasRect.x + _canvasRect.width - 0.5f)
            {
                GUI.DrawTexture(new Rect(right, top, _canvasRect.x + _canvasRect.width - right, bottom - top),
                    Texture2D.whiteTexture);
            }
            GUI.color = old;
        }

        private void DrawStaticElements()
        {
            foreach (UiElementData el in _data.elements)
            {
                if (!el.active || CharmUiRoot.IsTemplateTag(el.tag) || _hidden.Contains(el.path))
                {
                    continue;
                }
                if (Controller != null && el.kind == "image" && el.image != null &&
                    !string.IsNullOrEmpty(el.image.file) && Controller.IsIconHidden(el.image.file))
                {
                    continue;
                }
                // 诺艾尔没有固定虚空之心：该侧装备栏完全由 DrawEquippedBar 动态绘制，
                // 布局里那两个"槽位样例"（Equipment/charm_up (1)/(2)）不再静态绘制，
                // 否则空装备栏会多出一个槽位孔（它们的作用只是给动态槽位提供位置/间距基准，
                // GetBaseSlotRects 仍会读取它们的矩形，不受这里影响）。
                if (Controller != null && Controller.Owner == CharmOwner.Noel &&
                    el.kind == "image" &&
                    el.path.IndexOf("charm_up", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                // 护符槽孔：只画"当前上限"个（初始 3 个，随开箱数每 4 箱 +1，最多 14），
                // 布局里多余的槽孔图直接不画（需求 2026-09-26）。
                if (el.kind == "image" &&
                    el.path.IndexOf("cost_black", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !IsCostHoleVisible(el.path))
                {
                    continue;
                }
                // 寻神者模式选择器：sign 第 4 次点击前不显示（解锁后由 IsIconHidden 在装备时隐藏）
                if (Controller != null && !Controller.IsGgSelectorShown &&
                    el.kind == "image" && el.image != null && !string.IsNullOrEmpty(el.image.file) &&
                    MatchCharmIcon(el.image.file, out int ggFid) && ggFid == CharmDatabase.GgSelectorId)
                {
                    continue;
                }
                // GG 自限面板（四个文本 + 四个按钮）：由 DrawGgPanel 在选中“束缚”时动态绘制
                if (!string.IsNullOrEmpty(el.path) &&
                    el.path.StartsWith("CharmUi/GG_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // 平移动画期间：隐藏正在移动的护符的网格图标（由 DrawFlightCharm 绘制）
                if (Controller != null && Controller.IsFlying &&
                    el.kind == "image" && el.image != null && !string.IsNullOrEmpty(el.image.file) &&
                    MatchCharmIcon(el.image.file, out int flyFid) && flyFid == Controller.FlyId)
                {
                    continue;
                }
                if (!_rects.TryGetValue(el.path, out Rect rect))
                {
                    continue;
                }

                if ((el.kind == "image" || el.kind == "rawimage") &&
                    el.image != null && !string.IsNullOrEmpty(el.image.file))
                {
                    Texture2D tex = GetTexture(el.image.file);
                    if (tex != null)
                    {
                        Color old = GUI.color;
                        GUI.color = el.color;
                        GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
                        GUI.color = old;
                    }
                }
                else if (el.kind == "text" && el.text != null)
                {
                    // 描述面板文字由 DrawDetail 按真实内容绘制，跳过静态占位
                    if (!string.IsNullOrEmpty(el.tag) &&
                        el.tag.StartsWith("detail_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Color old = GUI.color;
                    GUI.color = el.color;
                    GUI.Label(rect, el.text.content, GetStyle(el.text));
                    GUI.color = old;
                }
            }
        }

        // ---------------- 动态绘制 ----------------

        private void DrawDynamic()
        {
            DrawEquippedBar();
            DrawCostRow();
            DrawDetail();
            DrawSelectionHighlight();
            DrawFlightCharm();
        }

        /// <summary>
        /// 装卸平移动画：护符图标从起点（选中槽位）平移到终点（已装备区），或反向。
        /// 起止位置在动画开始时缓存一次（源/目标槽位），进度由控制器驱动。
        /// </summary>
        private void RefreshFlightCache()
        {
            if (Controller == null || !Controller.IsFlying)
            {
                _flyCacheValid = false;
                return;
            }
            if (_flyCacheValid && _flyCachedId == Controller.FlyId &&
                _flyCachedEquip == Controller.FlyEquipping)
            {
                return;
            }
            _flyCachedId = Controller.FlyId;
            _flyCachedEquip = Controller.FlyEquipping;
            _flyCacheValid = true;
            if (Controller.FlyEquipping)
            {
                _flyFrom = GetGridCharmRect(Controller.FlyId);
                _flyTo = GetEquippedIconRect(Controller.FlyEquipIndex, Controller.FlyId);
            }
            else
            {
                _flyFrom = GetEquippedIconRect(Controller.FlyEquipIndex, Controller.FlyId);
                _flyTo = GetGridCharmRect(Controller.FlyId);
            }
            if (_flyFrom.width <= 0f)
            {
                _flyFrom = _flyTo;
            }
            if (_flyTo.width <= 0f)
            {
                _flyTo = _flyFrom;
            }
        }

        /// <summary>护符在网格中的图标矩形（布局元素，image 文件名匹配护符 id）。</summary>
        public Rect GetGridCharmRect(int id)
        {
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (el.kind == "image" && el.image != null && !string.IsNullOrEmpty(el.image.file) &&
                        MatchCharmIcon(el.image.file, out int fid) && fid == id &&
                        _rects.TryGetValue(el.path, out Rect r))
                    {
                        return r;
                    }
                }
            }
            return default;
        }

        /// <summary>已装备栏第 index 个槽位的图标矩形（与 DrawEquippedBar 同一套计算）。</summary>
        public Rect GetEquippedIconRect(int index, int id = 0)
        {
            List<Rect> slots = GetBaseSlotRects();
            if (slots.Count == 0)
            {
                return default;
            }
            float spacing = slots.Count >= 2 ? slots[1].x - slots[0].x : slots[0].width;
            float slotW = slots[0].width;
            float slotH = slots[0].height;
            float baseY = slots[0].y;
            Rect r = index < slots.Count
                ? slots[index]
                : new Rect(slots[slots.Count - 1].x + spacing * (index - slots.Count + 1),
                    baseY, slotW, slotH);
            float iconSize = EquippedIconSizeFor(id);
            return new Rect(r.center.x - iconSize * 0.5f, r.center.y - iconSize * 0.5f,
                iconSize, iconSize);
        }

        /// <summary>GG 自限按钮（0=骨钉 1=外壳 2=护符 3=灵魂）的矩形（状态 1/2 共用同一位置）。</summary>
        public bool TryGetGgButtonRect(int index, out Rect r)
        {
            r = default;
            if (index < 0 || index >= 4 || _data == null || _data.elements == null)
            {
                return false;
            }
            string[] p1 =
            {
                "CharmUi/GG_button/nail_1",
                "CharmUi/GG_button/mask_1",
                "CharmUi/GG_button/charm_1",
                "CharmUi/GG_button/soul_1"
            };
            string[] p2 =
            {
                "CharmUi/GG_button/nail_2",
                "CharmUi/GG_button/mask_2",
                "CharmUi/GG_button/charm_2",
                "CharmUi/GG_button/soul_2"
            };
            if (_rects.TryGetValue(p1[index], out r) || _rects.TryGetValue(p2[index], out r))
            {
                return r.width > 0f;
            }
            return false;
        }

        /// <summary>
        /// 已装备栏图标尺寸：寻神者模式选择器按其在布局中的大小（100×100）显示，
        /// 其余护符统一 120×_scale。
        /// </summary>
        private float EquippedIconSizeFor(int id)
        {
            if (id == CharmDatabase.GgSelectorId)
            {
                Rect gr = GetGridCharmRect(CharmDatabase.GgSelectorId);
                if (gr.width > 0f)
                {
                    return gr.width;
                }
            }
            return 120f * _scale;
        }

        /// <summary>绘制正在平移的护符图标（起点→终点插值）。</summary>
        private void DrawFlightCharm()
        {
            if (Controller == null || !Controller.IsFlying || !_flyCacheValid)
            {
                return;
            }
            Texture2D icon = GetCharmIcon(_flyCachedId);
            if (icon == null)
            {
                return;
            }
            float t = Controller.FlyProgress;
            var r = new Rect(
                Mathf.Lerp(_flyFrom.x, _flyTo.x, t),
                Mathf.Lerp(_flyFrom.y, _flyTo.y, t),
                Mathf.Lerp(_flyFrom.width, _flyTo.width, t),
                Mathf.Lerp(_flyFrom.height, _flyTo.height, t));
            GUI.DrawTexture(r, icon, ScaleMode.StretchToFill, true);
        }

        private List<Rect> GetBaseSlotRects()
        {
            var list = new List<Rect>();
            if (_data == null || _data.elements == null)
            {
                return list;
            }
            foreach (UiElementData el in _data.elements)
            {
                if (el.kind != "image" || el.image == null || string.IsNullOrEmpty(el.image.file) ||
                    CharmUiRoot.IsTemplateTag(el.tag) || _hidden.Contains(el.path))
                {
                    continue;
                }
                if (el.path.IndexOf("charm_up", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (_rects.TryGetValue(el.path, out Rect r))
                {
                    list.Add(r);
                }
            }
            list.Sort((a, b) => a.x.CompareTo(b.x));
            return list;
        }

        private void DrawEquippedBar()
        {
            List<Rect> slots = GetBaseSlotRects();
            if (slots.Count == 0)
            {
                return;
            }
            float spacing = slots.Count >= 2 ? slots[1].x - slots[0].x : slots[0].width;
            float slotW = slots[0].width;
            float slotH = slots[0].height;
            float baseY = slots[0].y;

            List<int> equipped = Controller.EquippedIds;
            // 满 11 槽或过载时不显示右侧空槽
            int slotCount = equipped.Count +
                (Controller.TotalCost >= CharmDatabase.NotchCapacity ? 0 : 1);
            Texture2D slotTex = GetTemplateTexture("charm_up_template");
            if (slotTex == null)
            {
                slotTex = GetTextureOfElementPathContaining("charm_up");
            }

            for (int i = 0; i < slotCount; i++)
            {
                Rect r = i < slots.Count
                    ? slots[i]
                    : new Rect(slots[slots.Count - 1].x + spacing * (i - slots.Count + 1), baseY, slotW, slotH);

                if (slotTex != null)
                {
                    GUI.DrawTexture(r, slotTex, ScaleMode.StretchToFill, true);
                }
                if (i < equipped.Count)
                {
                    // 平移动画期间：隐藏目标槽位的图标（由 DrawFlightCharm 绘制）
                    if (Controller != null && Controller.IsFlying && equipped[i] == Controller.FlyId)
                    {
                        continue;
                    }
                    Texture2D icon = GetCharmIcon(equipped[i]);
                    if (icon != null)
                    {
                        float iconSize = EquippedIconSizeFor(equipped[i]);
                        var iconRect = new Rect(r.center.x - iconSize * 0.5f, r.center.y - iconSize * 0.5f,
                            iconSize, iconSize);
                        GUI.DrawTexture(iconRect, icon, ScaleMode.StretchToFill, true);
                    }
                }
            }
        }

        private void DrawCostRow()
        {
            var blacks = new List<Rect>();
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (el.kind != "image" || el.image == null || string.IsNullOrEmpty(el.image.file) ||
                        CharmUiRoot.IsTemplateTag(el.tag))
                    {
                        continue;
                    }
                    if (el.path.IndexOf("cost_black", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (_rects.TryGetValue(el.path, out Rect r))
                    {
                        blacks.Add(r);
                    }
                }
            }
            blacks.Sort((a, b) => a.x.CompareTo(b.x));
            if (blacks.Count == 0)
            {
                return;
            }

            float spacing = blacks.Count >= 2 ? blacks[1].x - blacks[0].x : blacks[0].width;
            Texture2D white = GetTemplateTexture("cost_white_template");
            Texture2D over = GetTemplateTexture("cost_overcharm_template");

            int filled = Mathf.Min(Controller.TotalCost, blacks.Count);
            if (white != null)
            {
                for (int i = 0; i < filled; i++)
                {
                    GUI.DrawTexture(blacks[i], white, ScaleMode.StretchToFill, true);
                }
            }
            int excess = Controller.TotalCost - blacks.Count;
            if (excess > 0 && over != null)
            {
                Rect last = blacks[blacks.Count - 1];
                for (int k = 0; k < excess; k++)
                {
                    var r = new Rect(last.x + spacing * (k + 1), last.y, last.width, last.height);
                    GUI.DrawTexture(r, over, ScaleMode.StretchToFill, true);
                }
            }
        }

        /// <summary>
        /// 该"护符槽孔"（cost_black）是否应该画出来：只画前 `CharmDatabase.NotchCapacity` 个
        /// （初始 3，随开箱数每 4 箱 +1，最多 14）。序号 = 比它更靠左的槽孔数量。
        /// </summary>
        private bool IsCostHoleVisible(string path)
        {
            try
            {
                if (_data == null || _data.elements == null ||
                    !_rects.TryGetValue(path, out Rect mine))
                {
                    return true;
                }
                int cap = CharmDatabase.NotchCapacity;
                int index = 0;
                for (int i = 0; i < _data.elements.Count; i++)
                {
                    UiElementData el = _data.elements[i];
                    if (el.kind != "image" || el.image == null || string.IsNullOrEmpty(el.image.file) ||
                        CharmUiRoot.IsTemplateTag(el.tag))
                    {
                        continue;
                    }
                    if (el.path.IndexOf("cost_black", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (_rects.TryGetValue(el.path, out Rect r) && r.x < mine.x - 0.01f)
                    {
                        index++;
                    }
                }
                return index < cap;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void DrawDetail()
        {
            int id = Controller.SelectedId;
            if (id <= 0)
            {
                return;
            }
            CharmData cd = CharmDatabase.Get(id);
            if (cd == null)
            {
                return;
            }

            if (TryGetTaggedRect("detail_icon", out Rect iconRect))
            {
                Texture2D icon = GetCharmIcon(id);
                if (icon != null)
                {
                    Rect drawRect = iconRect;
                    if (id == CharmDatabase.GgSelectorId)
                    {
                        // 束缚：右侧图标向上平移 60px
                        drawRect = new Rect(iconRect.x, iconRect.y - 60f * _scale,
                            iconRect.width, iconRect.height);
                    }
                    GUI.DrawTexture(drawRect, icon, ScaleMode.StretchToFill, true);
                }
            }
            string desc = cd.Desc;
            if (id == CharmDatabase.FixedCharmId)
            {
                desc = "这个护符是持有者的一部分，不能卸下。";
            }
            else if (CharmDatabase.IsLocked(id))
            {
                // 未解锁：第一行是【未解锁】标记，换行之后是解锁条件
                // （文案见 docs/护符效果描述.md 的"未解锁"行）
                desc = "【未解锁】\n" + (cd.LockedText ?? "？？？");
            }
            Color old = GUI.color;
            GUI.color = Color.white;
            if (TryGetTaggedElement("detail_name", out UiElementData nameEl, out Rect nameRect) &&
                nameEl.text != null)
            {
                GUI.Label(nameRect, cd.Name,
                    GetStyle(nameEl.text.fontSize, (TextAnchor)nameEl.text.alignment, FontStyle.Bold));
            }
            bool isGg = id == CharmDatabase.GgSelectorId;
            if (!isGg)
            {
                if (TryGetTaggedElement("detail_desc", out UiElementData descEl, out Rect descRect) &&
                    descEl.text != null)
                {
                    GUI.Label(descRect, desc,
                        GetStyle(descEl.text.fontSize, (TextAnchor)descEl.text.alignment, FontStyle.Normal));
                }
                if (TryGetTaggedElement("detail_cost", out UiElementData costEl, out Rect costRect) &&
                    costEl.text != null)
                {
                    string costText = id == CharmDatabase.FixedCharmId
                        ? "不可卸下"
                        : "花费：" + cd.Cost;
                    GUI.Label(costRect, costText,
                        GetStyle(costEl.text.fontSize, (TextAnchor)costEl.text.alignment, FontStyle.Normal));
                }
            }
            else
            {
                // 束缚：不显示描述与花费，改为绘制 GG 自限面板（四个文本 + 四个按钮）
                DrawGgPanel();
            }
            GUI.color = old;
        }

        /// <summary>
        /// 束缚（GG 自限）面板：四个描述文本（骨钉/外壳/护符/灵魂）+
        /// 四个切换按钮（nail/mask/charm/soul 的 1/2 图，按存档状态显示）。
        /// </summary>
        private void DrawGgPanel()
        {
            if (_data == null || _data.elements == null || Controller == null)
            {
                return;
            }
            string[] textPaths =
            {
                "CharmUi/GG_description/nail",
                "CharmUi/GG_description/mask",
                "CharmUi/GG_description/charm",
                "CharmUi/GG_description/soul"
            };
            for (int i = 0; i < textPaths.Length; i++)
            {
                if (TryGetElement(textPaths[i], out UiElementData el) && el.text != null &&
                    _rects.TryGetValue(textPaths[i], out Rect tr))
                {
                    Color old = GUI.color;
                    GUI.color = el.color;
                    GUI.Label(tr, el.text.content,
                        GetStyle(el.text.fontSize, (TextAnchor)el.text.alignment, FontStyle.Normal));
                    GUI.color = old;
                }
            }
            string[] btn1Paths =
            {
                "CharmUi/GG_button/nail_1",
                "CharmUi/GG_button/mask_1",
                "CharmUi/GG_button/charm_1",
                "CharmUi/GG_button/soul_1"
            };
            string[] btn2Paths =
            {
                "CharmUi/GG_button/nail_2",
                "CharmUi/GG_button/mask_2",
                "CharmUi/GG_button/charm_2",
                "CharmUi/GG_button/soul_2"
            };
            for (int i = 0; i < 4; i++)
            {
                string path = Controller.GetGgButtonState(i) == 2 ? btn2Paths[i] : btn1Paths[i];
                if (TryGetElement(path, out UiElementData el) && el.image != null &&
                    !string.IsNullOrEmpty(el.image.file) &&
                    _rects.TryGetValue(path, out Rect br))
                {
                    Texture2D tex = GetTexture(el.image.file);
                    if (tex != null)
                    {
                        GUI.DrawTexture(br, tex, ScaleMode.StretchToFill, true);
                    }
                }
            }
        }

        private bool TryGetElement(string path, out UiElementData el)
        {
            if (_data != null && _data.elements != null)
            {
                for (int i = 0; i < _data.elements.Length; i++)
                {
                    UiElementData e = _data.elements[i];
                    if (e != null && string.Equals(e.path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        el = e;
                        return true;
                    }
                }
            }
            el = null;
            return false;
        }

        private void DrawSelectionHighlight()
        {
            Rect? target = FindCursorRect();
            if (target == null)
            {
                return;
            }
            Rect r = target.Value;
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0f, 1f);
            float t = 4f;
            Texture2D white = GetWhite();
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), white, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), white, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), white, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), white, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>按光标位置返回高亮矩形：上方栏用槽位（扩大到图标尺寸），网格用图标格。</summary>
        private Rect? FindCursorRect()
        {
            if (Controller.CursorRow == -2)
            {
                // GG 按钮模式：高亮对应按钮（nail/mask/charm/soul）
                string[] btnPaths =
                {
                    "CharmUi/GG_button/nail_1",
                    "CharmUi/GG_button/mask_1",
                    "CharmUi/GG_button/charm_1",
                    "CharmUi/GG_button/soul_1"
                };
                int idx = Mathf.Clamp(Controller.CursorCol, 0, 3);
                if (_rects.TryGetValue(btnPaths[idx], out Rect r))
                {
                    return r;
                }
                return null;
            }
            if (Controller.CursorRow == -1)
            {
                // 顶部“sign”图片：高亮其自身矩形
                return GetSignRect();
            }
            if (Controller.CursorRow == 0)
            {
                List<Rect> slots = GetBaseSlotRects();
                if (slots.Count == 0)
                {
                    return null;
                }
                int idx = Controller.CursorCol;
                Rect r = idx < slots.Count
                    ? slots[idx]
                    : new Rect(slots[slots.Count - 1].x +
                        (slots.Count >= 2 ? slots[1].x - slots[0].x : slots[0].width) *
                        (idx - slots.Count + 1), slots[0].y, slots[0].width, slots[0].height);
                float iconSize = 120f * _scale;
                return new Rect(r.center.x - iconSize * 0.5f, r.center.y - iconSize * 0.5f,
                    iconSize, iconSize);
            }
            int gid = Controller.CursorGridId;
            if (gid <= 0)
            {
                return null;
            }
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (el.kind != "image" || el.image == null || string.IsNullOrEmpty(el.image.file))
                    {
                        continue;
                    }
                    if (MatchCharmIcon(el.image.file, out int fid) && fid == gid &&
                        _rects.TryGetValue(el.path, out Rect r))
                    {
                        return r;
                    }
                }
            }
            return null;
        }

        /// <summary>顶部“sign”图片的矩形（路径含 /sign）。</summary>
        private Rect GetSignRect()
        {
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (!string.IsNullOrEmpty(el.path) &&
                        el.path.IndexOf("/sign", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        _rects.TryGetValue(el.path, out Rect r))
                    {
                        return r;
                    }
                }
            }
            return default;
        }

        private Texture2D GetTemplateTexture(string tag)
        {
            if (_data == null || _data.elements == null)
            {
                return null;
            }
            foreach (UiElementData el in _data.elements)
            {
                if (string.Equals(el.tag, tag, StringComparison.OrdinalIgnoreCase) &&
                    el.image != null && !string.IsNullOrEmpty(el.image.file))
                {
                    return GetTexture(el.image.file);
                }
            }
            return null;
        }

        private Texture2D GetTextureOfElementPathContaining(string keyword)
        {
            if (_data == null || _data.elements == null)
            {
                return null;
            }
            foreach (UiElementData el in _data.elements)
            {
                if (el.kind == "image" && el.image != null && !string.IsNullOrEmpty(el.image.file) &&
                    el.path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return GetTexture(el.image.file);
                }
            }
            return null;
        }

        private bool TryGetTaggedRect(string tag, out Rect r)
        {
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData el in _data.elements)
                {
                    if (string.Equals(el.tag, tag, StringComparison.OrdinalIgnoreCase) &&
                        _rects.TryGetValue(el.path, out r))
                    {
                        return true;
                    }
                }
            }
            r = default;
            return false;
        }

        private bool TryGetTaggedElement(string tag, out UiElementData el, out Rect r)
        {
            el = null;
            r = default;
            if (_data != null && _data.elements != null)
            {
                foreach (UiElementData e in _data.elements)
                {
                    if (string.Equals(e.tag, tag, StringComparison.OrdinalIgnoreCase) &&
                        _rects.TryGetValue(e.path, out r))
                    {
                        el = e;
                        return true;
                    }
                }
            }
            return false;
        }

        private Texture2D GetWhite()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            return _white;
        }

        private GUIStyle GetStyle(int size, TextAnchor align, FontStyle fontStyle)
        {
            string key = size + "_" + (int)align + "_" + (int)fontStyle;
            if (_styles.TryGetValue(key, out GUIStyle st))
            {
                return st;
            }
            st = new GUIStyle();
            int px = Mathf.Max(1, Mathf.RoundToInt(size * _scale));
            string fkey = px + "_" + (int)fontStyle;
            if (!_fonts.TryGetValue(fkey, out Font font))
            {
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, px);
                font.hideFlags = HideFlags.HideAndDontSave;
                _fonts[fkey] = font;
            }
            st.font = font;
            st.fontSize = px;
            st.alignment = align;
            st.fontStyle = fontStyle;
            st.wordWrap = true;
            st.normal.textColor = Color.white;
            _styles[key] = st;
            return st;
        }

        private GUIStyle GetStyle(UiTextData td)
        {
            return GetStyle(td.fontSize, (TextAnchor)td.alignment, (FontStyle)td.fontStyle);
        }

        private void OnDestroy()
        {
            if (CharmUiController.Instance == Controller)
            {
                CharmUiController.Instance = null;
            }
            foreach (Texture2D tex in _textures.Values)
            {
                if (tex != null)
                {
                    Destroy(tex);
                }
            }
            foreach (Font f in _fonts.Values)
            {
                if (f != null)
                {
                    Destroy(f);
                }
            }
            if (_white != null)
            {
                Destroy(_white);
            }
            _textures.Clear();
            _fonts.Clear();
            _styles.Clear();
        }
    }
}
