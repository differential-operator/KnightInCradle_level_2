using System;
using System.IO;
using AssetStudio;
using SixLabors.ImageSharp;

namespace TexDump
{
    /// <summary>
    /// 贴图导出工具：按 pathID（可选限定序列化文件）导出 Texture2D 为 PNG。
    /// 用法：TexDump.exe <数据目录> <pathID> [序列化文件名] <输出png>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\Knight In Cradle\Hollow Knight\hollow_knight_Data";
            long pathId = args.Length > 1 ? long.Parse(args[1]) : 315;
            string fileFilter = args.Length > 2 ? args[2] : null;
            string outPath = args.Length > 3
                ? args[3]
                : @"D:\Documents\Knight In Cradle\KnightInCradle\模组源码\assets\hk\sheets\shelter\smoke_" + pathId + ".png";

            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);

            foreach (SerializedFile sf in am.assetsFileList)
            {
                if (fileFilter != null &&
                    !sf.fileName.Equals(fileFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (!(asset is Texture2D tex) || tex.m_PathID != pathId)
                    {
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    using (var img = tex.ConvertToImage(true))
                    {
                        img.SaveAsPng(outPath);
                    }
                    Console.WriteLine("已导出: " + outPath + "（" + sf.fileName +
                        " pathID=" + pathId + " name=" + tex.m_Name + " " + tex.m_Width + "x" + tex.m_Height + "）");
                    return 0;
                }
            }
            Console.WriteLine("未找到 Texture2D pathID=" + pathId + (fileFilter != null ? " in " + fileFilter : ""));
            return 1;
        }
    }
}
