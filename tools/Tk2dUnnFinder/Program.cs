using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AssetStudio;

namespace Tk2dUnnFinder
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\GAME\Hollow Knight\hollow_knight_Data";
            string filter = args.Length > 1 ? args[1] : "unn";

            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);
            int animCount = 0;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    if (!(asset is MonoBehaviour mb))
                    {
                        continue;
                    }
                    dynamic parsed = null;
                    try
                    {
                        parsed = mb.ToType();
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (parsed == null)
                    {
                        continue;
                    }
                    // tk2dSpriteAnimation：字段 clips（List<tk2dSpriteAnimationClip>，每个有 name/frames）
                    try
                    {
                        if (parsed == null)
                        {
                            continue;
                        }
                        dynamic clips = parsed.clips;
                        if (clips == null)
                        {
                            continue;
                        }
                        foreach (dynamic clip in clips)
                        {
                            string clipName = (string)clip.name;
                            if (string.IsNullOrEmpty(clipName))
                            {
                                continue;
                            }
                            if (clipName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Console.WriteLine(sf.fileName + " | anim pathID=" + mb.m_PathID +
                                    " | clip=" + clipName);
                            }
                        }
                        animCount++;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            Console.WriteLine("解析到的 tk2d 动画对象数: " + animCount);
            return 0;
        }
    }
}
