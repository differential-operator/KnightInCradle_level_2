using System;
using System.IO;
using System.Linq;
using AssetStudio;
using SixLabors.ImageSharp.Formats.Png;

namespace HKTextureDump
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
            string outDir = args.Length > 1
                ? args[1]
                : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\fx";

            Directory.CreateDirectory(outDir);

            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);
            Console.WriteLine("序列化文件数: " + am.assetsFileList.Count);

            string[] targets = { "shadow_ring", "blackorb", "black_orb", "shadowdash", "orb", "glow", "shade" };
            int found = 0;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (asset is Texture2D tex)
                    {
                        string nm = tex.m_Name ?? "";
                        if (targets.Any(t => nm.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Console.WriteLine("找到纹理: [" + nm + "] in " + sf.fileName +
                                              " pathID=" + tex.m_PathID + " 尺寸=" + tex.m_Width + "x" + tex.m_Height);
                            try
                            {
                                using (var img = tex.ConvertToImage(true))
                                {
                                    string safe = string.Concat(nm.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                                    using (var fs = File.Create(Path.Combine(outDir, safe + ".png")))
                                    {
                                        img.Save(fs, new PngEncoder());
                                    }
                                }
                                found++;
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("导出失败 [" + nm + "]: " + e.Message);
                            }
                        }
                    }
                }
            }

            // 通过材质解析 _MainTex，找到黑点/暗影粒子纹理
            foreach (SerializedFile sf in am.assetsFileList)
            {
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (asset is Material mat)
                    {
                        string nm = mat.m_Name ?? "";
                        if (nm.IndexOf("BlackOrb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nm.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine("材质: [" + nm + "] in " + sf.fileName + " pathID=" + mat.m_PathID);
                            foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                            {
                                var te = kv.Value;
                                Console.WriteLine("  TexEnv: " + kv.Key + " -> pathID " + te.m_Texture.m_PathID);
                                if (te.m_Texture.TryGet(out Texture tex))
                                {
                                    Console.WriteLine("    引用纹理: [" + (tex.m_Name ?? "?") + "]");
                                    if (tex is Texture2D tex2d)
                                    {
                                        try
                                        {
                                            using (var img = tex2d.ConvertToImage(true))
                                            {
                                                string safe = string.Concat((tex.m_Name ?? "tex").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                                                using (var fs = File.Create(Path.Combine(outDir, safe + ".png")))
                                                {
                                                    img.Save(fs, new PngEncoder());
                                                }
                                            }
                                            found++;
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine("    导出失败: " + e.Message);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            Console.WriteLine("导出完成，共 " + found + " 个。");
            return found > 0 ? 0 : 1;
        }
    }
}
