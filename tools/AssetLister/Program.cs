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

namespace AssetLister
{
    /// <summary>
    /// 资源枚举工具：列出指定程序集内名字匹配关键字的对象（类型 + 名字 + pathID），
    /// 用于定位特效/贴图类资源（如防御者纹章的臭气云）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dataPath = args.Length > 0
                ? args[0]
                : @"D:\Documents\Knight In Cradle\Hollow Knight\hollow_knight_Data";
            string keyword = args.Length > 1 ? args[1] : "stink";

            var am = new AssetsManager();
            Console.WriteLine("加载: " + dataPath);
            am.LoadFolder(dataPath);

            int count = 0;
            foreach (SerializedFile sf in am.assetsFileList)
            {
                foreach (AssetStudio.Object asset in sf.Objects)
                {
                    string name = GetName(asset);
                    if (string.IsNullOrEmpty(name) ||
                        name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    Console.WriteLine(sf.fileName + " | " + asset.GetType().Name +
                        " | pathID=" + asset.m_PathID + " | " + name);
                    count++;
                }
            }
            Console.WriteLine("匹配总数: " + count);
            return 0;
        }

        private static string GetName(AssetStudio.Object asset)
        {
            try
            {
                if (asset is Texture2D t) { return t.m_Name; }
                if (asset is Sprite s) { return s.m_Name; }
                if (asset is Material m) { return m.m_Name; }
                if (asset is GameObject g) { return g.m_Name; }
                if (asset is AudioClip a) { return a.m_Name; }
                if (asset is Mesh mh) { return mh.m_Name; }
                if (asset is Shader sh) { return sh.m_Name; }
                if (asset is TextAsset tx) { return tx.m_Name; }
                if (asset is AssetStudio.MonoBehaviour mb) { return mb.m_Name; }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
