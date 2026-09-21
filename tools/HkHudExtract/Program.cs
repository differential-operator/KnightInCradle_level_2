using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "scan")
        {
            ScanHud(args);
            return;
        }
        if (args.Length > 0 && args[0] == "shader")
        {
            DumpShaders(args);
            return;
        }
        if (args.Length > 0 && args[0] == "pxls")
        {
            PeekPxls(args);
            return;
        }

        string gameData = args.Length > 0
            ? args[0]
            : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
        string outDir = args.Length > 1
            ? args[1]
            : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\sprites\hud";
        string collectionJson = args.Length > 2
            ? args[2]
            : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\mb\tk2dSpriteCollectionData__resources.assets__20151.json";

        Directory.CreateDirectory(outDir);

        var am = new AssetsManager();
        am.LoadFiles(new[] { Path.Combine(gameData, "resources.assets") });

        Texture2D atlasTex = null;
        foreach (var file in am.assetsFileList)
        {
            foreach (var obj in file.Objects)
            {
                if (obj is Texture2D t && obj.m_PathID == 389)
                {
                    atlasTex = t;
                    break;
                }
            }
            if (atlasTex != null)
            {
                break;
            }
        }

        if (atlasTex == null)
        {
            Console.WriteLine("[error] HUD atlas texture (pathID 389) not found");
            return;
        }

        using (Image<Bgra32> atlas = Texture2DExtensions.ConvertToImage(atlasTex, true))
        {
            Console.WriteLine($"atlas: {atlas.Width}x{atlas.Height}");

            var wanted = new HashSet<string>(GetWantedNames(), StringComparer.OrdinalIgnoreCase);
            int saved = 0;

            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(collectionJson)))
            {
                JsonElement defs = doc.RootElement.GetProperty("spriteDefinitions");
                foreach (JsonElement def in defs.EnumerateArray())
                {
                    string name = def.GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(name) || !wanted.Contains(name))
                    {
                        continue;
                    }
                    JsonElement uvs = def.GetProperty("uvs");
                    if (uvs.GetArrayLength() < 4)
                    {
                        continue;
                    }
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    foreach (JsonElement uv in uvs.EnumerateArray())
                    {
                        float ux = uv.GetProperty("x").GetSingle();
                        float uy = uv.GetProperty("y").GetSingle();
                        minX = Math.Min(minX, ux);
                        maxX = Math.Max(maxX, ux);
                        minY = Math.Min(minY, uy);
                        maxY = Math.Max(maxY, uy);
                    }

                    // tk2d UV 原点在左下；ImageSharp 图像原点在左上，需要翻转 Y
                    int px = (int)Math.Round(minX * atlas.Width);
                    int py = (int)Math.Round((1f - maxY) * atlas.Height);
                    int pw = (int)Math.Round((maxX - minX) * atlas.Width);
                    int ph = (int)Math.Round((maxY - minY) * atlas.Height);
                    if (pw <= 0 || ph <= 0)
                    {
                        continue;
                    }

                    using (var crop = atlas.Clone(ctx => ctx.Crop(new Rectangle(px, py, pw, ph))))
                    {
                        string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                        string path = Path.Combine(outDir, safe + ".png");
                        crop.SaveAsPng(path);
                        saved++;
                        Console.WriteLine($"saved {safe}.png ({pw}x{ph})");
                    }
                }
            }
            Console.WriteLine($"done, {saved} sprites");
        }
    }

    private static void PeekPxls(string[] args)
    {
        string bundle = args.Length > 1
            ? args[1]
            : @"D:\Documents\GAME\AliceInCradle Win ver029\AliceInCradle_ver029\AliceInCradle_Data\StreamingAssets\Pxl\_icons_l.pxls.dat";
        var am = new AssetsManager();
        am.LoadFiles(new[] { bundle });
        foreach (var file in am.assetsFileList)
        {
            Console.WriteLine("== file: " + file.fileName);
            foreach (var obj in file.Objects)
            {
                string typeName = obj.type.ToString();
                string name = obj is AssetStudio.NamedObject n ? n.m_Name : "";
                if (obj is AssetStudio.TextAsset ta)
                {
                    Console.WriteLine("  TextAsset '" + name + "' len=" +
                        (ta.m_Script != null ? ta.m_Script.Length.ToString() : "null"));
                    if (ta.m_Script != null)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(ta.m_Script);
                        if (text.IndexOf("gage", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine("    contains 'gage'! head: " +
                                (text.Length > 300 ? text.Substring(0, 300) : text));
                        }
                    }
                }
                else if (obj is AssetStudio.Texture2D t2)
                {
                    Console.WriteLine("  Texture2D '" + name + "' " + t2.m_Width + "x" + t2.m_Height);
                }
                else if (typeName != "GameObject" && typeName != "Transform" && typeName != "MonoBehaviour")
                {
                    Console.WriteLine("  " + typeName + " '" + name + "'");
                }
            }
        }
        Console.WriteLine("[pxls peek done]");
    }

    private static void DumpShaders(string[] args)
    {
        string gameData = args.Length > 1
            ? args[1]
            : @"D:\Documents\GAME\AliceInCradle Win ver029\AliceInCradle_ver029\AliceInCradle_Data";
        string pattern = args.Length > 2 ? args[2] : "UiBg";
        string outDir = args.Length > 3
            ? args[3]
            : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\tools\HkHudExtract\shaders";
        Directory.CreateDirectory(outDir);

        var files = new List<string>();
        foreach (var f in Directory.GetFiles(gameData, "*.assets"))
        {
            files.Add(f);
        }
        foreach (var f in Directory.GetFiles(gameData, "mti_*.dat"))
        {
            files.Add(f);
        }
        var am = new AssetsManager();
        am.LoadFiles(files.ToArray());

        foreach (var file in am.assetsFileList)
        {
            foreach (var obj in file.Objects)
            {
                if (obj is AssetStudio.Shader sh)
                {
                    string name = obj is AssetStudio.NamedObject n ? n.m_Name : "";
                    if (!string.IsNullOrEmpty(pattern) &&
                        name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    Console.WriteLine("shader: '" + name + "' script=" +
                        (sh.m_Script != null ? sh.m_Script.Length.ToString() : "null"));
                    if (sh.m_Script == null || sh.m_Script.Length == 0)
                    {
                        continue;
                    }
                    string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                    File.WriteAllBytes(Path.Combine(outDir, safe + ".shader"), sh.m_Script);
                    Console.WriteLine("saved shader: " + safe + " (" + sh.m_Script.Length + " bytes)");
                }
            }
        }
        Console.WriteLine("[shader dump done]");
    }

    private static void ScanHud(string[] args)
    {
        string gameData = args.Length > 1
            ? args[1]
            : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
        string[] files = args.Length > 2
            ? args[2].Split(';')
            : new[] { Path.Combine(gameData, "resources.assets") };

        var am = new AssetsManager();
        am.LoadFiles(files.Select(f => Path.IsPathRooted(f) ? f : Path.Combine(gameData, f)).ToArray());

        var nameIndex = new Dictionary<long, AssetStudio.GameObject>();
        foreach (var file in am.assetsFileList)
        {
            foreach (var obj in file.Objects)
            {
                if (obj is AssetStudio.GameObject go && !string.IsNullOrEmpty(go.m_Name))
                {
                    nameIndex[obj.m_PathID] = go;
                }
            }
        }

        foreach (var go in nameIndex.Values)
        {
            if (go.m_Name != "Hud Canvas" &&
                !System.Text.RegularExpressions.Regex.IsMatch(go.m_Name, "HUD|Health|Soul|Mask", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                continue;
            }
            DumpGo(go, nameIndex, 0, new HashSet<long>());
        }
        Console.WriteLine("[scan done]");
    }

    private static void DumpGo(AssetStudio.GameObject go, Dictionary<long, AssetStudio.GameObject> nameIndex,
        int depth, HashSet<long> visited)
    {
        if (depth > 12 || !visited.Add(go.m_PathID))
        {
            return;
        }
        var tr = go.m_Transform;
        string pos = tr != null
            ? $"pos=({tr.m_LocalPosition.X:F2},{tr.m_LocalPosition.Y:F2},{tr.m_LocalPosition.Z:F2}) rot=({tr.m_LocalRotation.X:F2},{tr.m_LocalRotation.Y:F2},{tr.m_LocalRotation.Z:F2},{tr.m_LocalRotation.W:F2}) scale=({tr.m_LocalScale.X:F2},{tr.m_LocalScale.Y:F2})"
            : "no-transform";
        string ind = new string(' ', depth * 2);
        Console.WriteLine($"{ind}{go.m_Name} [{go.m_PathID}] {pos}");
        if (tr != null)
        {
            foreach (var childPtr in tr.m_Children)
            {
                if (childPtr.TryGet(out AssetStudio.Transform child) &&
                    nameIndex.TryGetValue(child.m_PathID, out var childGo))
                {
                    DumpGo(childGo, nameIndex, depth + 1, visited);
                }
            }
        }
    }

    private static IEnumerable<string> GetWantedNames()
    {
        var list = new List<string>
        {
            "health_backboard",
            "idle_v020000", "idle_v020001", "idle_v020002", "idle_v020003", "idle_v020004", "idle_v020005",
            "break_backboard0000", "break_backboard0001", "break_backboard0002", "break_backboard0003",
            "break_backboard0004", "break_backboard0005",
            "refill0000", "refill0001", "refill0002", "refill0003", "refill0004", "refill0005",
            "appear_v020000", "appear_v020001", "appear_v020002", "appear_v020003", "appear_v020004",
            "break0000", "break0001", "break0002", "break0003", "break0004",
            "appear0000", "appear0001", "appear0002", "appear0003", "appear0004", "appear0005", "appear0006",
            "appear0007", "appear0008"
        };
        for (int i = 0; i < 10; i++)
        {
            list.Add("full000" + i);
        }
        for (int level = 1; level <= 3; level++)
        {
            for (int i = 0; i < 6; i++)
            {
                list.Add($"level_0{level}000{i}");
            }
        }
        list.AddRange(new[]
        {
            "soul_orb_shape", "soul_orb_eyes", "soul_orb_darken", "soul_orb_glow0000",
            "soulorb_flash0000", "soulorb_flash0001", "soulorb_flash0002", "soulorb_flash0003",
            "appear0000 1", "appear0001 1", "appear0002 1", "appear0003 1",
            "appear0004 1", "appear0005 1", "appear0006 1",
            "HUD_frame_v020000", "HUD_frame_v020001", "HUD_frame_v020002", "HUD_frame_v020003",
            "HUD_frame_v020004", "HUD_frame_v020005",
            "gg_UI_hud0000", "gg_UI_hud0001", "gg_UI_hud0002", "gg_UI_hud0003",
            "gg_UI_hud0004", "gg_UI_hud0005",
            "HUD_yellow_health0000", "HUD_yellow_health0001", "HUD_yellow_health0002",
            "HUD_yellow_health0003", "HUD_yellow_health0004", "HUD_yellow_health0005",
            "HUD_yellow_health_backboard",
            "idle0000", "idle0001", "idle0002", "idle0003", "idle0004", "idle0005",
            "idle0006", "idle0007", "idle0008", "idle0009", "idle0010", "idle0011",
            "idle0012", "idle0013", "idle0014", "idle0015", "idle0016", "idle0017",
            "idle0018", "idle0019", "idle0020", "idle0021", "idle0022", "idle0023",
            "idle0024", "idle0025", "idle0026"
        });
        return list;
    }
}
