using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HKFireballExtract
{
    internal static class Program
    {
        private sealed class Job
        {
            public long TexPathId;
            public long CollPathId;
            public string OutFolder;
            public string[] Prefixes;
        }

        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
            string outRoot = args.Length > 1
                ? args[1]
                : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\sheets\fireball";
            // BaseDirectory = tools/HKFireballExtract/bin/Release/net10.0/
            // 上溯 5 级回到 KnightInCradle 根目录
            string mbDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, @"..\..\..\..\..\assets\hk\mb"));
            Directory.CreateDirectory(outRoot);

            var jobs = new[]
            {
                new Job
                {
                    TexPathId = 347,
                    CollPathId = 19578,
                    OutFolder = Path.Combine(outRoot, "knight_cast"),
                    Prefixes = new[] { "cast_v030", "cast_level_02" }
                },
                new Job
                {
                    TexPathId = 347,
                    CollPathId = 19578,
                    OutFolder = Path.GetFullPath(Path.Combine(outRoot, "..", "dive")),
                    Prefixes = new[] { "quake" }
                },
                new Job
                {
                    TexPathId = 217,
                    CollPathId = 19733,
                    OutFolder = Path.Combine(outRoot, "spell_effects"),
                    Prefixes = new[] { "fireball_v020", "blast_effect_v020", "fireball_wall_impact" }
                },
                new Job
                {
                    TexPathId = 372,
                    CollPathId = 22690,
                    OutFolder = Path.Combine(outRoot, "spell_effects_neutral"),
                    Prefixes = new[] { "fireball_spiral", "fireball_lvl_02_blast", "single_fireball", "shade_impact" }
                },
                // 深渊尖啸（Abyss Shriek）：骑士施法身体（scream_cast_lvl_02 020001~020008）
                new Job
                {
                    TexPathId = 347,
                    CollPathId = 19578,
                    OutFolder = Path.Combine(outRoot, "knight_cast"),
                    Prefixes = new[] { "scream_cast_lvl_02" }
                },
                // 深渊尖啸特效：上升冲击波（scream_blast_level_02）+ 地面爆发底座（scream_ground_blast_lvl_02）
                new Job
                {
                    TexPathId = 372,
                    CollPathId = 22690,
                    OutFolder = Path.Combine(outRoot, "spell_effects"),
                    Prefixes = new[] { "scream_blast_level_02", "scream_ground_blast_lvl_02" }
                }
            };

            var am = new AssetsManager();
            Console.WriteLine("开始加载: " + dataPath);
            am.LoadFolder(dataPath);
            Console.WriteLine("加载完成，序列化文件数: " + am.assetsFileList.Count);

            foreach (Job job in jobs)
            {
                Texture2D tex = null;
                foreach (SerializedFile sf in am.assetsFileList)
                {
                    foreach (AssetStudio.Object asset in sf.Objects)
                    {
                        if (asset is Texture2D t && t.m_PathID == job.TexPathId)
                        {
                            tex = t;
                            break;
                        }
                    }
                    if (tex != null) break;
                }
                if (tex == null)
                {
                    Console.WriteLine("错误：未找到纹理 pathID " + job.TexPathId);
                    continue;
                }
                string collPath = Path.Combine(mbDir,
                    "tk2dSpriteCollectionData__resources.assets__" + job.CollPathId + ".json");
                if (!File.Exists(collPath))
                {
                    Console.WriteLine("错误：缺少集合 JSON " + collPath);
                    continue;
                }
                using (var atlasImage = tex.ConvertToImage(true))
                {
                    Extract(atlasImage, collPath, job, outRoot);
                }
            }

            Console.WriteLine("完成。输出目录: " + outRoot);
            return 0;
        }

        private static void Extract(Image atlasImage, string collPath, Job job, string outRoot)
        {
            JObject coll = JObject.Parse(File.ReadAllText(collPath));
            JArray spriteDefs = (JArray)coll["spriteDefinitions"];
            int W = atlasImage.Width;
            int H = atlasImage.Height;
            Console.WriteLine("图集 {0}x{1}，精灵数 {2}，前缀 {3}",
                W, H, spriteDefs.Count, string.Join(",", job.Prefixes));

            Directory.CreateDirectory(job.OutFolder);
            int saved = 0;
            foreach (JToken s in spriteDefs)
            {
                string name = (string)s["name"];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (!job.Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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
                        // tk2d flipped = TexturePacker 顺时针旋转，解包要逆时针
                        ctx.Rotate(RotateMode.Rotate270);
                    }
                    // 与已有骑士帧一致：统一水平翻转（默认脸朝左）
                    ctx.Flip(FlipMode.Horizontal);
                }))
                {
                    crop.Save(Path.Combine(job.OutFolder, Sanitize(name) + ".png"));
                    saved++;
                }
            }
            Console.WriteLine("  已保存 {0} 帧 -> {1}", saved, job.OutFolder);
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }
}
