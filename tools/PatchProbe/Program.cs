using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PatchProbe
{
    /// <summary>
    /// 离线「补丁体检」小工具：不需要启动游戏，直接加载指定版本的 AIC 程序集，
    /// 把一份“目标方法清单”里每一项都真跑一遍 Harmony 补丁，报告：
    ///   1) 方法是否还能找到（方法名/签名是否变了）；
    ///   2) Harmony 能不能挂上（是否出现 IL Compile Error 之类的异常，附完整堆栈）。
    ///
    /// 用法：
    ///   PatchProbe.exe &lt;Managed 目录&gt; &lt;目标清单.txt&gt;
    /// 清单格式（每行一个，`#` 开头为注释）：
    ///   类型全名;方法名            —— 任意重载里第一个匹配
    ///   类型全名;方法名;参数类型1,参数类型2   —— 精确指定参数类型（可省略命名空间前缀）
    /// </summary>
    internal static class Program
    {
        private static string _managed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length < 2)
            {
                Console.WriteLine("用法: PatchProbe.exe <Managed 目录> <目标清单.txt>");
                return 2;
            }
            _managed = Path.GetFullPath(args[0]);
            string listPath = Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromManaged;

            Console.WriteLine($"[探测] Managed = {_managed}");
            var loaded = new List<Assembly>();
            foreach (string name in new[] { "Assembly-CSharp.dll", "unsafeAssem.dll", "pixelliner.dll" })
            {
                string p = Path.Combine(_managed, name);
                if (File.Exists(p))
                {
                    try
                    {
                        loaded.Add(Assembly.LoadFrom(p));
                        Console.WriteLine($"[探测] 已加载 {name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[探测] 加载 {name} 失败: {ex.Message}");
                    }
                }
            }
            // 触发器程序集：UnityEngine 等，由 AssemblyResolve 惰性加载
            try
            {
                foreach (string dll in Directory.GetFiles(_managed, "UnityEngine*.dll"))
                {
                    Assembly.LoadFrom(dll);
                }
            }
            catch (Exception)
            {
            }

            var harmony = new Harmony("dev.KnightInCradle.probe");
            MethodInfo dummyPrefix = typeof(Program).GetMethod(nameof(DummyPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo dummyPostfix = typeof(Program).GetMethod(nameof(DummyPostfix), BindingFlags.Static | BindingFlags.NonPublic);

            int ok = 0, missing = 0, failed = 0;
            var failureDetails = new List<string>();
            foreach (string raw in File.ReadAllLines(listPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string[] parts = line.Split(';');
                string typeName = parts[0].Trim();
                string methodName = parts.Length > 1 ? parts[1].Trim() : "";
                string[] paramTypes = parts.Length > 2 && parts[2].Trim().Length > 0
                    ? parts[2].Split(',').Select(s => s.Trim()).ToArray()
                    : null;

                Type t = FindType(typeName);
                if (t == null)
                {
                    Console.WriteLine($"[缺类型] {typeName}");
                    missing++;
                    failureDetails.Add($"{typeName} : 类型不存在");
                    continue;
                }
                MethodBase m = FindMethod(t, methodName, paramTypes);
                if (m == null)
                {
                    Console.WriteLine($"[缺方法] {typeName}.{methodName}({(paramTypes == null ? "" : string.Join(",", paramTypes))})");
                    missing++;
                    failureDetails.Add($"{typeName}.{methodName} : 方法不存在");
                    continue;
                }

                string sig = Describe(m);
                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(dummyPrefix),
                        postfix: new HarmonyMethod(dummyPostfix));
                    Console.WriteLine($"[OK  ] {typeName}.{methodName}   {sig}");
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] {typeName}.{methodName}   {sig}");
                    Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
                    failed++;
                    failureDetails.Add($"{typeName}.{methodName}\n{ex}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"===== 汇总: OK={ok} 缺方法={missing} 挂载失败={failed} =====");
            if (failureDetails.Count > 0)
            {
                Console.WriteLine("----- 失败详情 -----");
                foreach (string d in failureDetails)
                {
                    Console.WriteLine(d);
                    Console.WriteLine();
                }
            }
            return failed > 0 || missing > 0 ? 1 : 0;
        }

        private static void DummyPrefix()
        {
        }

        private static void DummyPostfix()
        {
        }

        private static Assembly ResolveFromManaged(object sender, ResolveEventArgs e)
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name + ".dll";
                string p = Path.Combine(_managed, simple);
                if (File.Exists(p))
                {
                    return Assembly.LoadFrom(p);
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(name, false);
                    if (t != null)
                    {
                        return t;
                    }
                }
                catch (Exception)
                {
                }
            }
            // 退化：按简单名找
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in a.GetTypes())
                    {
                        if (t.Name == name || t.FullName == name)
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static MethodBase FindMethod(Type t, string name, string[] paramTypes)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = new List<MethodBase>();
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (name.StartsWith("get_") || name.StartsWith("set_"))
            {
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    MethodInfo acc = name.StartsWith("get_") ? p.GetGetMethod(true) : p.GetSetMethod(true);
                    if (acc != null && acc.Name == name)
                    {
                        methods.Add(acc);
                    }
                }
            }
            foreach (MethodInfo mi in t.GetMethods(flags))
            {
                if (mi.Name == name)
                {
                    methods.Add(mi);
                }
            }
            if (methods.Count == 0)
            {
                return null;
            }
            if (paramTypes == null || paramTypes.Length == 0)
            {
                return methods[0];
            }
            foreach (MethodBase mb in methods)
            {
                ParameterInfo[] ps = mb.GetParameters();
                if (ps.Length != paramTypes.Length)
                {
                    continue;
                }
                bool all = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    string want = paramTypes[i];
                    string have = ps[i].ParameterType.FullName ?? ps[i].ParameterType.Name;
                    string haveShort = ps[i].ParameterType.Name;
                    if (want != have && want != haveShort && !have.EndsWith("." + want, StringComparison.Ordinal))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return mb;
                }
            }
            return methods[0];
        }

        private static string Describe(MethodBase m)
        {
            string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            string ret = (m as MethodInfo)?.ReturnType.Name ?? "void";
            return $"({ps}) -> {ret}";
        }
    }
}
