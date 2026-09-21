using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MushUnnExtractor
{
    /// <summary>
    /// 蘑菇乌恩素材提取：读取 tk2dSpriteCollectionData JSON（20859，含 shroom_slug 帧 UV），
    /// 用 System.Drawing 从 atlas0.png 按 UV 矩形裁剪出 shroom_slug0000~0016，
    /// 输出到 sheets/mush_unn/sprites。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string assetsDir = args.Length > 0
                ? args[0]
                : @"D:\Documents\Knight In Cradle\KnightInCradle\模组源码\assets\hk\sheets\mush_unn";
            string collJson = args.Length > 1
                ? args[1]
                : @"D:\Documents\Knight In Cradle\KnightInCradle\模组源码\assets\hk\mb\tk2dSpriteCollectionData__resources.assets__20859.json";
            string atlasPng = Path.Combine(assetsDir, "atlas0.png");
            string outDir = Path.Combine(assetsDir, "sprites");
            Directory.CreateDirectory(outDir);

            JObject coll = JObject.Parse(File.ReadAllText(collJson));
            JArray spriteDefs = (JArray)coll["spriteDefinitions"];
            Console.WriteLine("精灵定义数: " + spriteDefs.Count);

            using (var atlas = new Bitmap(atlasPng))
            {
                int W = atlas.Width;
                int H = atlas.Height;
                Console.WriteLine("图集尺寸: " + W + "x" + H);

                int cutCount = 0;
                for (int si = 0; si < spriteDefs.Count; si++)
                {
                    JToken s = spriteDefs[si];
                    string name = (string)s["name"];
                    if (string.IsNullOrEmpty(name) ||
                        name.IndexOf("shroom_slug", StringComparison.OrdinalIgnoreCase) < 0)
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
                    var srcRect = new Rectangle(px, py, pw, ph);
                    srcRect.Intersect(new Rectangle(0, 0, W, H));
                    if (srcRect.Width <= 0 || srcRect.Height <= 0)
                    {
                        continue;
                    }
                    using (var crop = new Bitmap(srcRect.Width, srcRect.Height))
                    {
                        using (var g = Graphics.FromImage(crop))
                        {
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(atlas, new Rectangle(0, 0, srcRect.Width, srcRect.Height),
                                srcRect, GraphicsUnit.Pixel);
                        }
                        if (flipped == 1)
                        {
                            crop.RotateFlip(RotateFlipType.Rotate270FlipX);
                        }
                        crop.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
                        Console.WriteLine("切图: " + name + "  rect=" + srcRect + " flipped=" + flipped);
                        cutCount++;
                    }
                }
                Console.WriteLine("蘑菇乌恩帧已切图: " + cutCount);
            }
            return 0;
        }
    }
}
