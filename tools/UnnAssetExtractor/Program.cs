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

namespace UnnAssetExtractor
{
    /// <summary>
    /// 乌恩之形素材提取：图集纹理 pathID 404 + 精灵集合 20859（Knight Slug Cln）
    /// + 骑士动画 19580（Slug 剪辑）。只切 knight_slug_* 系列并生成剪辑清单。
    /// </summary>
    internal static class Program
    {
        private const long SlugAtlasTexturePathId = 404;
        private const long SlugCollAssetPathId = 20859;
        private const long KnightAnimAssetPathId = 19580;

        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
            string outDir = args.Length > 1
                ? args[1]
                : @"D:\Documents\Knight In Cradle\KnightInCradle\模组源码\assets\hk\sheets\unn";
            string mbDir = args.Length > 2
                ? args[2]
                : @"D:\Documents\Knight In Cradle\KnightInCradle\模组源码\assets\hk\mb";

            Directory.CreateDirectory(outDir);
            string spriteDir = Path.Combine(outDir, "sprites");
            Directory.CreateDirectory(spriteDir);

            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);

            Texture2D atlasTexture = null;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                if (sf.fileName != "resources.assets")
                {
                    continue;
                }
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (asset is Texture2D tex && tex.m_PathID == SlugAtlasTexturePathId)
                    {
                        atlasTexture = tex;
                        break;
                    }
                }
                if (atlasTexture != null)
                {
                    break;
                }
            }
            if (atlasTexture == null)
            {
                Console.WriteLine("错误：未找到乌恩图集纹理 pathID " + SlugAtlasTexturePathId);
                return 1;
            }

            string animPath = Path.Combine(mbDir,
                "tk2dSpriteAnimation__resources.assets__" + KnightAnimAssetPathId + ".json");
            string collPath = Path.Combine(mbDir,
                "tk2dSpriteCollectionData__resources.assets__" + SlugCollAssetPathId + ".json");
            if (!File.Exists(animPath) || !File.Exists(collPath))
            {
                Console.WriteLine("缺少 mb JSON: " + animPath + " / " + collPath);
                return 1;
            }
            JObject anim = JObject.Parse(File.ReadAllText(animPath));
            JObject coll = JObject.Parse(File.ReadAllText(collPath));
            JArray spriteDefs = (JArray)coll["spriteDefinitions"];
            Console.WriteLine("精灵定义数: " + spriteDefs.Count);

            using (var atlasImage = atlasTexture.ConvertToImage(true))
            {
                int W = atlasImage.Width;
                int H = atlasImage.Height;
                Console.WriteLine("图集尺寸: " + W + "x" + H);

                var spriteRects = new JObject();
                int cutCount = 0;
                for (int si = 0; si < spriteDefs.Count; si++)
                {
                    JToken s = spriteDefs[si];
                    string name = (string)s["name"];
                    if (string.IsNullOrEmpty(name) || name.IndexOf("knight_slug", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    JArray uvs = (JArray)s["uvs"];
                    if (uvs == null || uvs.Count < 4)
                    {
                        continue;
                    }
                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
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
                        continue;
                    }
                    var rect = new Rectangle(px, py, pw, ph);
                    rect.Intersect(new Rectangle(0, 0, W, H));
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        continue;
                    }
                    using (var crop = atlasImage.Clone(ctx =>
                    {
                        ctx.Crop(rect);
                        if (flipped == 1)
                        {
                            ctx.Rotate(RotateMode.Rotate270);
                            ctx.Flip(FlipMode.Horizontal);
                        }
                    }))
                    {
                        crop.Save(Path.Combine(spriteDir, Sanitize(name) + ".png"));
                        spriteRects[name] = new JObject
                        {
                            ["x"] = rect.X,
                            ["y"] = rect.Y,
                            ["w"] = rect.Width,
                            ["h"] = rect.Height,
                            ["flipped"] = flipped == 1 ? 1 : 0
                        };
                        cutCount++;
                    }
                }

                // 剪辑：只保留引用 Knight Slug 集合（20859）且帧名属于 knight_slug 的剪辑
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
                        JToken sc = frame["spriteCollection"];
                        if (sc == null || (long)(sc["m_PathID"] ?? 0L) != SlugCollAssetPathId)
                        {
                            continue;
                        }
                        int sid = (int)frame["spriteId"];
                        if (sid < 0 || sid >= spriteDefs.Count)
                        {
                            continue;
                        }
                        string spriteName = (string)spriteDefs[sid]["name"];
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
                    Console.WriteLine("剪辑: " + clipName + "（" + frames.Count + " 帧, " +
                        (float)clip["fps"] + "fps）");
                }

                var manifest = new JObject
                {
                    ["spriteDir"] = "sprites",
                    ["sprites"] = spriteRects,
                    ["clips"] = clips
                };
                File.WriteAllText(
                    Path.Combine(outDir, "unn_manifest.json"),
                    manifest.ToString(Formatting.Indented), Encoding.UTF8);
                Console.WriteLine("乌恩精灵已切图: " + cutCount + "；剪辑数: " + clips.Count);
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
    }
}
