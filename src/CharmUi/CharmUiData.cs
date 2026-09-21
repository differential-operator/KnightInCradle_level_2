using System;
using UnityEngine;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符 UI 导出数据的运行时镜像（与 KnightCharmUI 工程 Assets/Scripts/UiExportData.cs 一致）。
    /// 由 Newtonsoft.Json 从 layout.json 反序列化。
    /// </summary>
    [Serializable]
    public sealed class UiExport
    {
        public int version = 1;
        public UiCanvasData canvas = new UiCanvasData();
        public UiElementData[] elements = new UiElementData[0];
        public UiGridData[] grids = new UiGridData[0];
    }

    [Serializable]
    public sealed class UiCanvasData
    {
        public Vector2 referenceResolution = new Vector2(1920f, 1080f);
        public float matchWidthOrHeight = 0.5f;
        public int sortOrder = 100;
    }

    [Serializable]
    public sealed class UiElementData
    {
        public string path = "";
        public string tag = "";
        public string kind = "empty";
        public string parent = "";
        public bool active = true;

        public Vector2 anchorMin = new Vector2(0.5f, 0.5f);
        public Vector2 anchorMax = new Vector2(0.5f, 0.5f);
        public Vector2 pivot = new Vector2(0.5f, 0.5f);
        public Vector2 anchoredPosition = Vector2.zero;
        public Vector2 sizeDelta = new Vector2(100f, 100f);
        public Vector2 localScale = Vector2.one;

        public Color color = Color.white;
        public bool raycast = false;

        public UiImageData image = null;
        public UiTextData text = null;
    }

    [Serializable]
    public sealed class UiImageData
    {
        public string file = "";
        public Vector4 border = Vector4.zero;
        public float pixelsPerUnit = 100f;
        public int type = 0;
        public float fillAmount = 1f;
    }

    [Serializable]
    public sealed class UiTextData
    {
        public string content = "";
        public int fontSize = 24;
        public int alignment = 4;
        public int fontStyle = 0;
        public float lineSpacing = 1f;
        public bool richText = true;
        public int hOverflow = 0;
        public int vOverflow = 0;
    }

    [Serializable]
    public sealed class UiGridData
    {
        public string path = "";
        public string tag = "";
        public int columns = 8;
        public Vector2 cell = new Vector2(150f, 150f);
        public Vector2 spacing = new Vector2(8f, 8f);
        public Vector2 start = Vector2.zero;
        public string templatePath = "";
    }
}
