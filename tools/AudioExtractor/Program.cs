using System;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AudioExtractor
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
                : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\audio";

            Directory.CreateDirectory(outDir);
            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);
            Console.WriteLine("序列化文件数: " + am.assetsFileList.Count);

            int count = 0;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (!(asset is AudioClip clip))
                    {
                        continue;
                    }
                    count++;
                    Console.WriteLine(
                        $"AudioClip: name={clip.m_Name} fmt={clip.m_Format} comp={clip.m_CompressionFormat} " +
                        $"ch={clip.m_Channels} freq={clip.m_Frequency} bits={clip.m_BitsPerSample} " +
                        $"len={clip.m_Length:F2}s size={clip.m_AudioData?.Size}");
                    bool want = clip.m_Name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("fury", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("rage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("fluke", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("swing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("slash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("hit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("nail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("sword", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("damage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("wings", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("death", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                clip.m_Name.IndexOf("greatslash", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (want)
                    {
                        string safe = string.Concat(clip.m_Name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
                        string path = Path.Combine(outDir, safe + ".raw");
                        try
                        {
                            clip.m_AudioData?.WriteData(path);
                            Console.WriteLine("  已导出原始数据 -> " + path);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("  导出失败: " + e.Message);
                        }
                    }
                }
            }
            Console.WriteLine("共 " + count + " 个 AudioClip");
            return 0;
        }
    }
}
