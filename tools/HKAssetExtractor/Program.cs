using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HKAssetExtractor
{
    internal static class Program
    {
        private const long KnightAtlasTexturePathId = 347;
        private const long KnightAnimAssetPathId = 19580;
        private const long KnightCollAssetPathId = 19578;

        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
            string outDir = args.Length > 1
                ? args[1]
                : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk";

            Directory.CreateDirectory(outDir);
            string mbDir = Path.Combine(outDir, "mb");
            string spriteDir = Path.Combine(outDir, "sprites");
            Directory.CreateDirectory(spriteDir);

            var am = new AssetsManager();
            Console.WriteLine("开始加载: " + dataPath);
            am.LoadFolder(dataPath);
            Console.WriteLine("加载完成，序列化文件数: " + am.assetsFileList.Count);

            Texture2D atlasTexture = null;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                if (sf.fileName != "resources.assets")
                {
                    continue;
                }

                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (asset is Texture2D tex && tex.m_PathID == KnightAtlasTexturePathId)
                    {
                        atlasTexture = tex;
                        break;
                    }
                }
                if (atlasTexture != null) break;
            }

            if (atlasTexture == null)
            {
                Console.WriteLine("错误：未找到小骑士图集纹理 (pathID " + KnightAtlasTexturePathId + ")");
                return 1;
            }

            string animPath = Path.Combine(mbDir,
                "tk2dSpriteAnimation__resources.assets__" + KnightAnimAssetPathId + ".json");
            string collPath = Path.Combine(mbDir,
                "tk2dSpriteCollectionData__resources.assets__" + KnightCollAssetPathId + ".json");

            JObject anim = JObject.Parse(File.ReadAllText(animPath));
            JObject coll = JObject.Parse(File.ReadAllText(collPath));
            JArray spriteDefs = (JArray)coll["spriteDefinitions"];

            Console.WriteLine("精灵定义数: " + spriteDefs.Count);

            string atlasPath = Path.Combine(outDir, "knight_atlas.png");
            using (var atlasImage = atlasTexture.ConvertToImage(true))
            {
                int W = atlasImage.Width;
                int H = atlasImage.Height;
                Console.WriteLine("图集尺寸: " + W + "x" + H);

                var spriteRects = new JObject();
                int cutCount = 0;
                foreach (JToken s in spriteDefs)
                {
                    string name = (string)s["name"];
                    JArray uvs = (JArray)s["uvs"];
                    bool isDash = name != null && name.IndexOf("dash_v020", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.IsNullOrEmpty(name) || uvs == null || uvs.Count < 4)
                    {
                        if (isDash)
                        {
                            Console.WriteLine("[dash] 跳过(名字/UV): " + name + " uvs=" + (uvs != null ? uvs.Count : 0));
                        }
                        continue;
                    }

                    // tk2d 精灵 UV 顶点顺序不统一，用 4 个点算包围盒最稳妥
                    float x0 = float.MaxValue;
                    float y0 = float.MaxValue;
                    float x1 = float.MinValue;
                    float y1 = float.MinValue;
                    foreach (JToken u in uvs)
                    {
                        float ux = (float)u["x"];
                        float uy = (float)u["y"];
                        x0 = Math.Min(x0, ux);
                        y0 = Math.Min(y0, uy);
                        x1 = Math.Max(x1, ux);
                        y1 = Math.Max(y1, uy);
                    }
                    int flipped = (int)(s["flipped"] ?? 0);

                    int px = (int)Math.Round(x0 * W);
                    int py = (int)Math.Round((1.0 - y1) * H);
                    int pw = (int)Math.Round((x1 - x0) * W);
                    int ph = (int)Math.Round((y1 - y0) * H);

                    if (pw <= 0 || ph <= 0)
                    {
                        if (isDash)
                        {
                            Console.WriteLine("[dash] 跳过(尺寸): " + name + " pw=" + pw + " ph=" + ph);
                        }
                        continue;
                    }

                    var rect = new Rectangle(px, py, pw, ph);
                    rect.Intersect(new Rectangle(0, 0, W, H));
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        continue;
                    }

                    int flippedFlag = flipped == 1 ? 1 : 0;
                    using (var crop = atlasImage.Clone(ctx =>
                    {
                        ctx.Crop(rect);
                        if (flippedFlag == 1)
                        {
                            // tk2d flipped = TexturePacker 顺时针旋转，解包要逆时针
                            ctx.Rotate(RotateMode.Rotate270);
                        }
                        // 全局统一水平翻转：以“脸朝左”为默认方向（与游戏一致）
                        ctx.Flip(FlipMode.Horizontal);
                    }))
                    {
                        crop.Save(Path.Combine(spriteDir, Sanitize(name) + ".png"));
                        float feetFrac = ComputeFeetFraction(crop);
                        spriteRects[name] = new JObject
                        {
                            ["x"] = rect.X,
                            ["y"] = rect.Y,
                            ["w"] = rect.Width,
                            ["h"] = rect.Height,
                            ["flipped"] = flippedFlag,
                            ["feet"] = feetFrac
                        };
                        cutCount++;
                    }
                    if (isDash)
                    {
                        Console.WriteLine("[dash] 已切图: " + name + " " + rect.Width + "x" + rect.Height);
                    }
                }

                atlasImage.Save(atlasPath);
                Console.WriteLine("图集已导出: " + atlasPath);
                Console.WriteLine("精灵已切图: " + cutCount);

                // ---- 镜像相似度分析（仅 4 个基础剪辑）----
                var analysisClips = new HashSet<string> { "Idle", "Run", "Walk", "Airborne", "Land", "Run To Idle", "Turn" };
                var analysisCache = new Dictionary<string, Image<Bgra32>>();
                foreach (JToken clip in (JArray)anim["clips"])
                {
                    string clipName = (string)clip["name"];
                    if (!analysisClips.Contains(clipName))
                    {
                        continue;
                    }
                    JArray frameObjs = (JArray)clip["frames"];
                    var names = new List<string>();
                    foreach (JToken fo in frameObjs)
                    {
                        int sid = (int)fo["spriteId"];
                        if (sid >= 0 && sid < spriteDefs.Count)
                        {
                            string nm = (string)spriteDefs[sid]["name"];
                            if (nm != null && spriteRects[nm] != null && !analysisCache.ContainsKey(nm))
                            {
                                string pngPath = Path.Combine(spriteDir, Sanitize(nm) + ".png");
                                if (File.Exists(pngPath))
                                {
                                    try
                                    {
                                        analysisCache[nm] = Image.Load<Bgra32>(pngPath);
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                            names.Add(nm);
                        }
                    }
                    int n = names.Count;
                    if (n < 2)
                    {
                        continue;
                    }
                    Console.WriteLine("=== 分析剪辑: " + clipName + " ===");
                    for (int i = 0; i < n; i++)
                    {
                        string nm = names[i];
                        string nx = names[(i + 1) % n];
                        if (!analysisCache.ContainsKey(nm) || !analysisCache.ContainsKey(nx))
                        {
                            continue;
                        }
                        Image<Bgra32> img = analysisCache[nm];
                        Image<Bgra32> next = analysisCache[nx];
                        float normal = DiffImages(img, next);
                        using (var mir = img.Clone(x => x.Flip(FlipMode.Horizontal)))
                        {
                            float mirrored = DiffImages(mir, next);
                            Console.WriteLine(nm + " normal=" + normal.ToString("F1") + " mirror=" + mirrored.ToString("F1") + " delta=" + (normal - mirrored).ToString("F1"));
                        }
                    }
                }
                // ---- 镜像求解：相邻帧朝向一致性 2-染色 ----
                var mirrorSet = new HashSet<string>();
                // 参考帧对比法：idle_still_020001 是确认朝向正确的待机帧（原生朝左）
                // 每帧独立判断“镜像版是否更接近参考帧”，把所有剪辑归一化到同一朝向
                if (analysisCache.TryGetValue("idle_still_020001", out Image<Bgra32> reference))
                {
                    foreach (JToken clip in (JArray)anim["clips"])
                    {
                        string clipName = (string)clip["name"];
                        if (!analysisClips.Contains(clipName))
                        {
                            continue;
                        }
                        foreach (JToken fo in (JArray)clip["frames"])
                        {
                            int sid = (int)fo["spriteId"];
                            if (sid < 0 || sid >= spriteDefs.Count)
                            {
                                continue;
                            }
                            string nm = (string)spriteDefs[sid]["name"];
                            if (nm == null || mirrorSet.Contains(nm) || !analysisCache.ContainsKey(nm))
                            {
                                continue;
                            }
                            Image<Bgra32> img = analysisCache[nm];
                            float normal = DiffImages(img, reference);
                            using (var mir = img.Clone(x => x.Flip(FlipMode.Horizontal)))
                            {
                                float mirrored = DiffImages(mir, reference);
                                if (mirrored < normal - 12f)
                                {
                                    mirrorSet.Add(nm);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("[MIRROR] missing reference idle_still_020001");
                }

                // 全局翻转已覆盖所有精灵，不再需要逐帧镜像
                mirrorSet = new HashSet<string>();


                foreach (KeyValuePair<string, Image<Bgra32>> kv in analysisCache)
                {
                    kv.Value.Dispose();
                }
                analysisCache.Clear();
                Console.WriteLine("MIRROR_COUNT: " + mirrorSet.Count);
                foreach (string name in mirrorSet)
                {
                    JObject r = (JObject)spriteRects[name];
                    int rx = (int)r["x"];
                    int ry = (int)r["y"];
                    int rw = (int)r["w"];
                    int rh = (int)r["h"];
                    int flip = (int)r["flipped"];
                    try
                    {
                        using (var crop = atlasImage.Clone(ctx =>
                        {
                            ctx.Crop(new Rectangle(rx, ry, rw, rh));
                            if (flip == 1)
                            {
                                ctx.Rotate(RotateMode.Rotate270);
                            }
                            ctx.Flip(FlipMode.Horizontal);
                        }))
                        {
                            crop.Save(Path.Combine(spriteDir, Sanitize(name) + ".png"));
                        }
                        Console.WriteLine("已镜像修正: " + name);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("镜像修正失败: " + name + " : " + ex.Message);
                    }
                }


                // ---- 连拍对比图（Contact Sheet）+ 帧映射表 ----
                var sheetClips = new HashSet<string> { "Idle", "Walk", "Run", "Airborne", "Land", "Run To Idle" };
                string sheetDir = Path.Combine(outDir, "sheets");
                Directory.CreateDirectory(sheetDir);
                using (var sw = new StreamWriter(Path.Combine(sheetDir, "clip_map.txt"), false, Encoding.UTF8))
                {
                    foreach (JToken clip in (JArray)anim["clips"])
                    {
                        string clipName = (string)clip["name"];
                        if (!sheetClips.Contains(clipName))
                        {
                            continue;
                        }
                        var frameNames = new List<string>();
                        foreach (JToken fo in (JArray)clip["frames"])
                        {
                            int sid = (int)fo["spriteId"];
                            if (sid < 0 || sid >= spriteDefs.Count)
                            {
                                continue;
                            }
                            string nm = (string)spriteDefs[sid]["name"];
                            if (nm != null && spriteRects[nm] != null)
                            {
                                frameNames.Add(nm);
                            }
                        }
                        if (frameNames.Count == 0)
                        {
                            continue;
                        }
                        sw.WriteLine("=== " + clipName + " ===");
                        sw.WriteLine(string.Join(", ", frameNames));
                        sw.WriteLine();
                        int pad = 4;
                        int totalW = 0;
                        int maxH = 0;
                        var imgs = new List<Image<Bgra32>>();
                        foreach (string nm in frameNames)
                        {
                            string p = Path.Combine(spriteDir, Sanitize(nm) + ".png");
                            if (!File.Exists(p))
                            {
                                continue;
                            }
                            try
                            {
                                imgs.Add(Image.Load<Bgra32>(p));
                            }
                            catch
                            {
                            }
                        }
                        if (imgs.Count == 0)
                        {
                            continue;
                        }
                        foreach (var im in imgs)
                        {
                            totalW += im.Width + pad;
                            maxH = Math.Max(maxH, im.Height);
                        }
                        totalW -= pad;
                        using (var sheet = new Image<Bgra32>(totalW, maxH))
                        {
                            int x = 0;
                            foreach (var im in imgs)
                            {
                                sheet.Mutate(ctx => ctx.DrawImage(im, new Point(x, 0), 1f));
                                x += im.Width + pad;
                            }
                            sheet.Save(Path.Combine(sheetDir, Sanitize(clipName) + ".png"));
                        }
                        foreach (var im in imgs)
                        {
                            im.Dispose();
                        }
                    }
                }
                Console.WriteLine("SHEETS_READY: " + sheetDir);

                var clips = new JArray();
                foreach (JToken clip in (JArray)anim["clips"])
                {
                    string clipName = (string)clip["name"];
                    if (string.IsNullOrEmpty(clipName))
                    {
                        continue;
                    }

                    var frames = new JArray();
                    foreach (JToken frame in (JArray)clip["frames"])
                    {
                        int spriteId = (int)frame["spriteId"];
                        if (spriteId < 0 || spriteId >= spriteDefs.Count)
                        {
                            continue;
                        }
                        string spriteName = (string)spriteDefs[spriteId]["name"];
                        if (!string.IsNullOrEmpty(spriteName) && spriteRects[spriteName] != null)
                        {
                            frames.Add(spriteName);
                        }
                    }

                    if (frames.Count == 0)
                    {
                        continue;
                    }

                    clips.Add(new JObject
                    {
                        ["name"] = clipName,
                        ["fps"] = (float)clip["fps"],
                        ["wrapMode"] = (int)clip["wrapMode"],
                        ["loopStart"] = (int)clip["loopStart"],
                        ["frames"] = frames
                    });
                }

                var manifest = new JObject
                {
                    ["atlasFile"] = "knight_atlas.png",
                    ["atlasWidth"] = W,
                    ["atlasHeight"] = H,
                    ["spriteDir"] = "sprites",
                    ["sprites"] = spriteRects,
                    ["clips"] = clips
                };

                string manifestPath = Path.Combine(outDir, "knight_manifest.json");
                File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented), Encoding.UTF8);
                Console.WriteLine("清单已生成: " + manifestPath + " (剪辑数 " + clips.Count + ")");
            }

            return 0;
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

        private static float ComputeFeetFraction(Image<Bgra32> img)
        {
            for (int y = img.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    if (img[x, y].A > 32)
                    {
                        return (float)(y + 1) / img.Height;
                    }
                }
            }
            return 1f;
        }

        private static float DiffImages(Image<Bgra32> a, Image<Bgra32> b)
        {
            using (var ra = a.Clone(x => x.Resize(64, 64)))
            using (var rb = b.Clone(x => x.Resize(64, 64)))
            {
                double sum = 0.0;
                int count = 0;
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        Bgra32 pa = ra[x, y];
                        Bgra32 pb = rb[x, y];
                        sum += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B) + Math.Abs(pa.A - pb.A);
                        count += 4;
                    }
                }
                return (float)(sum / count);
            }
        }
    }
}
