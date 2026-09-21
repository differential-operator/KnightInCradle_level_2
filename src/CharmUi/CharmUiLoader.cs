using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace KnightInCradle.CharmUi
{
    /// <summary>护符网格运行时对象：容器 + 图标模板 + 排布参数。</summary>
    public sealed class CharmUiGrid
    {
        public RectTransform Container;
        public RectTransform Template;
        public int Columns;
        public Vector2 Cell;
        public Vector2 Spacing;
        public Vector2 Start;

        /// <summary>按模板实例化一个图标，返回新图标的 RectTransform。</summary>
        public RectTransform InstantiateIcon(int index)
        {
            if (Template == null)
            {
                return null;
            }
            GameObject go = UnityEngine.Object.Instantiate(Template.gameObject, Container, false);
            go.name = "icon_" + index;
            go.SetActive(true);
            RectTransform rt = go.transform as RectTransform;
            if (rt != null)
            {
                int col = index % Mathf.Max(1, Columns);
                int row = index / Mathf.Max(1, Columns);
                // 模板在编辑器里摆放的位置 = 第一个图标的位置；Start 是额外偏移
                rt.anchoredPosition = Template.anchoredPosition + Start + new Vector2(
                    col * (Cell.x + Spacing.x),
                    -row * (Cell.y + Spacing.y));
            }
            return rt;
        }

        public int CalcRows(int count)
        {
            int c = Mathf.Max(1, Columns);
            return (count + c - 1) / c;
        }
    }

    /// <summary>护符 UI 根对象：Canvas 根 + 标签索引 + 网格列表。</summary>
    public sealed class CharmUiRoot
    {
        public GameObject Root;
        public Canvas Canvas;
        public CanvasScaler Scaler;
        public RectTransform RootRect;
        public readonly Dictionary<string, List<RectTransform>> Tags =
            new Dictionary<string, List<RectTransform>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<CharmUiGrid> Grids = new List<CharmUiGrid>();

        /// <summary>按标签取第一个元素；没有返回 null。</summary>
        public RectTransform Find(string tag)
        {
            return Tags.TryGetValue(tag, out var list) && list.Count > 0 ? list[0] : null;
        }

        /// <summary>按标签取全部元素。</summary>
        public List<RectTransform> FindAll(string tag)
        {
            return Tags.TryGetValue(tag, out var list) ? list : new List<RectTransform>();
        }

        public void Destroy()
        {
            if (Root != null)
            {
                UnityEngine.Object.Destroy(Root);
                Root = null;
            }
        }

        /// <summary>
        /// 按模板标签克隆一个元素并激活（模板本身运行时保持隐藏）。
        /// 用于动态生成：cost_white 覆盖点、cost_overcharm 超额点、charm_up 槽位等。
        /// </summary>
        public RectTransform Spawn(string templateTag, string name, Vector2 anchoredPosition)
        {
            RectTransform tpl = Find(templateTag);
            if (tpl == null)
            {
                return null;
            }
            GameObject go = UnityEngine.Object.Instantiate(tpl.gameObject, tpl.parent, false);
            go.name = name;
            go.SetActive(true);
            RectTransform rt = go.transform as RectTransform;
            if (rt != null)
            {
                rt.anchoredPosition = anchoredPosition;
            }
            return rt;
        }

        /// <summary>模板标签（运行时自动隐藏，只作生成源）。</summary>
        public static bool IsTemplateTag(string tag)
        {
            return !string.IsNullOrEmpty(tag) && tag.EndsWith("_template", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 运行时重建护符 UI：读取插件目录下 charm_ui/layout.json + images/*.png，
    /// 按 Unity 工程导出的布局创建 ScreenSpaceOverlay Canvas 与全部元素。
    /// 文本统一使用系统动态字体（中文优先），编辑器里选的字体只用于预览。
    /// </summary>
    public static class CharmUiLoader
    {
        private static readonly Dictionary<string, Sprite> SpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> TextureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, Font> FontCache = new Dictionary<int, Font>();
        private static Material _uiFallbackMat;

        private static string _imageDir = "";

        /// <summary>从目录加载并重建 UI；失败返回 null（不会抛异常）。</summary>
        public static CharmUiRoot Build(string dir)
        {
            string jsonPath = Path.Combine(dir, "layout.json");
            if (!File.Exists(jsonPath))
            {
                return null;
            }

            UiExport data;
            try
            {
                data = JsonConvert.DeserializeObject<UiExport>(File.ReadAllText(jsonPath));
            }
            catch (Exception)
            {
                return null;
            }
            if (data == null || data.elements == null)
            {
                return null;
            }

            _imageDir = Path.Combine(dir, "images");
            ClearCaches();

            var root = new CharmUiRoot();
            root.Root = new GameObject("CharmUi");
            root.RootRect = root.Root.AddComponent<RectTransform>();
            root.Canvas = root.Root.AddComponent<Canvas>();
            root.Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            root.Canvas.sortingOrder = data.canvas != null ? data.canvas.sortOrder : 100;
            root.Scaler = root.Root.AddComponent<CanvasScaler>();
            root.Scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            root.Scaler.referenceResolution = data.canvas != null
                ? data.canvas.referenceResolution
                : new Vector2(1920f, 1080f);
            root.Scaler.matchWidthOrHeight = data.canvas != null ? data.canvas.matchWidthOrHeight : 0.5f;
            root.Root.AddComponent<GraphicRaycaster>();

            // 游戏构建裁掉了 UI/Default 着色器，uGUI 默认材质无法渲染。
            // 检测后给所有 UI 图形换上游戏自带的 Sprites/Default，否则画面全空。
            ApplyMaterialFix(root);

            var byPath = new Dictionary<string, RectTransform>(StringComparer.OrdinalIgnoreCase);
            byPath[""] = root.RootRect;

            for (int ei = 0; ei < data.elements.Length; ei++)
            {
                UiElementData el = data.elements[ei];
                RectTransform parent = null;
                if (!string.IsNullOrEmpty(el.parent) && !byPath.TryGetValue(el.parent, out parent))
                {
                    continue;
                }

                var go = new GameObject(Path.GetFileName(el.path), typeof(RectTransform));
                RectTransform rt = go.transform as RectTransform;
                rt.SetParent(parent, false);
                go.SetActive(el.active);

                rt.anchorMin = el.anchorMin;
                rt.anchorMax = el.anchorMax;
                rt.pivot = el.pivot;
                rt.anchoredPosition = el.anchoredPosition;
                rt.sizeDelta = el.sizeDelta;
                rt.localScale = el.localScale;

                // 画布根元素：运行时画布由本加载器创建，忽略导出值里的编辑器画布布局
                if (ei == 0 && string.IsNullOrEmpty(el.parent))
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.zero;
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(Screen.width, Screen.height);
                }

                if (el.kind == "image")
                {
                    Image img = go.AddComponent<Image>();
                    img.color = el.color;
                    img.raycastTarget = el.raycast;
                    if (el.image != null && !string.IsNullOrEmpty(el.image.file))
                    {
                        img.sprite = LoadSprite(el.image.file, el.image.border, el.image.pixelsPerUnit);
                        img.type = (Image.Type)Mathf.Clamp(el.image.type, 0, 3);
                        if (img.type == Image.Type.Filled)
                        {
                            img.fillAmount = el.image.fillAmount;
                        }
                    }
                }
                else if (el.kind == "rawimage")
                {
                    RawImage raw = go.AddComponent<RawImage>();
                    raw.color = el.color;
                    raw.raycastTarget = el.raycast;
                    if (el.image != null && !string.IsNullOrEmpty(el.image.file))
                    {
                        raw.texture = LoadTexture(el.image.file);
                    }
                }
                else if (el.kind == "text")
                {
                    Text tx = go.AddComponent<Text>();
                    tx.color = el.color;
                    tx.raycastTarget = el.raycast;
                    if (el.text != null)
                    {
                        tx.text = el.text.content;
                        tx.fontSize = Mathf.Max(1, el.text.fontSize);
                        tx.font = GetFont(tx.fontSize);
                        tx.alignment = (TextAnchor)el.text.alignment;
                        tx.fontStyle = (FontStyle)el.text.fontStyle;
                        tx.lineSpacing = el.text.lineSpacing;
                        tx.supportRichText = el.text.richText;
                        tx.horizontalOverflow = (HorizontalWrapMode)el.text.hOverflow;
                        tx.verticalOverflow = (VerticalWrapMode)el.text.vOverflow;
                    }
                }

                byPath[el.path] = rt;
                if (!string.IsNullOrEmpty(el.tag))
                {
                    if (!root.Tags.TryGetValue(el.tag, out var list))
                    {
                        list = new List<RectTransform>();
                        root.Tags[el.tag] = list;
                    }
                    list.Add(rt);
                }
            }

            // 模板元素（标签以 _template 结尾）构建后隐藏，供运行时 Spawn 克隆
            foreach (var kv in root.Tags)
            {
                if (!CharmUiRoot.IsTemplateTag(kv.Key))
                {
                    continue;
                }
                foreach (RectTransform rt in kv.Value)
                {
                    if (rt != null)
                    {
                        rt.gameObject.SetActive(false);
                    }
                }
            }

            if (data.grids != null)
            {
                foreach (UiGridData g in data.grids)
                {
                    if (!byPath.TryGetValue(g.path, out RectTransform container))
                    {
                        continue;
                    }
                    RectTransform template = null;
                    if (!string.IsNullOrEmpty(g.templatePath))
                    {
                        byPath.TryGetValue(g.templatePath, out template);
                    }
                    if (template == null)
                    {
                        for (int i = 0; i < container.childCount; i++)
                        {
                            RectTransform c = container.GetChild(i) as RectTransform;
                            if (c != null && (c.GetComponent<Image>() != null || c.GetComponent<RawImage>() != null))
                            {
                                template = c;
                                break;
                            }
                        }
                    }
                    if (template != null)
                    {
                        template.gameObject.SetActive(false);
                    }
                    root.Grids.Add(new CharmUiGrid
                    {
                        Container = container,
                        Template = template,
                        Columns = g.columns,
                        Cell = g.cell,
                        Spacing = g.spacing,
                        Start = g.start
                    });
                }
            }

            return root;
        }

        private static void ApplyMaterialFix(CharmUiRoot root)
        {
            if (Shader.Find("UI/Default") != null)
            {
                return; // 引擎带 UI 着色器，无需处理
            }
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback == null)
            {
                return;
            }

            if (_uiFallbackMat == null)
            {
                _uiFallbackMat = new Material(fallback)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            Image[] images = root.Root.GetComponentsInChildren<Image>(true);
            foreach (Image img in images)
            {
                img.material = _uiFallbackMat;
            }
            RawImage[] raws = root.Root.GetComponentsInChildren<RawImage>(true);
            foreach (RawImage raw in raws)
            {
                raw.material = _uiFallbackMat;
            }

            // 文字：直接把字体自带的材质换成可用着色器（保留字体图集绑定）
            Text[] texts = root.Root.GetComponentsInChildren<Text>(true);
            foreach (Text tx in texts)
            {
                if (tx.font != null && tx.font.material != null && tx.font.material.shader == null)
                {
                    tx.font.material.shader = fallback;
                }
            }
        }

        private static Sprite LoadSprite(string file, Vector4 border, float ppu)
        {
            if (SpriteCache.TryGetValue(file, out var sp))
            {
                return sp;
            }
            Texture2D tex = LoadTexture(file);
            if (tex == null)
            {
                return null;
            }
            sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), ppu > 0f ? ppu : 100f, 0, SpriteMeshType.FullRect, border);
            SpriteCache[file] = sp;
            return sp;
        }

        private static Texture2D LoadTexture(string file)
        {
            if (TextureCache.TryGetValue(file, out var tex))
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
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            TextureCache[file] = tex;
            return tex;
        }

        private static Font GetFont(int size)
        {
            if (!FontCache.TryGetValue(size, out var font))
            {
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, size);
                font.hideFlags = HideFlags.HideAndDontSave;
                FontCache[size] = font;
            }
            return font;
        }

        private static void ClearCaches()
        {
            foreach (Texture2D tex in TextureCache.Values)
            {
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }
            foreach (Sprite sp in SpriteCache.Values)
            {
                if (sp != null)
                {
                    UnityEngine.Object.Destroy(sp);
                }
            }
            foreach (Font f in FontCache.Values)
            {
                if (f != null)
                {
                    UnityEngine.Object.Destroy(f);
                }
            }
            TextureCache.Clear();
            SpriteCache.Clear();
            FontCache.Clear();
        }
    }
}
