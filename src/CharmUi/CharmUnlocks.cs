using System;
using m2d;
using nel;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 诺艾尔护符的解锁机制（条件见 `docs/护符效果描述.md` 的"未解锁"行）。
    /// 目前实现了第一条：**1 任性的指南针 = 在地图上开启 10 把椅子（长椅）**。
    ///
    /// 记录方式全部走游戏自己的存档字段 `COOK.setSF/getSF`，不用另做序列化：
    /// ・每个护符一条 `kic_charm_unlocked_&lt;id&gt;`；
    /// ・每把坐过的椅子一条 `kic_bench_visit_&lt;地图键&gt;_&lt;x&gt;_&lt;y&gt;`（去重用）；
    /// ・计数 `kic_bench_visit_count`。
    /// 判定在诺艾尔模式下每帧跑（`KnightInCradleBehaviour.TickNoelCharmEffects`）。
    /// </summary>
    public static class CharmUnlocks
    {
        private const string UnlockKeyPrefix = "kic_charm_unlocked_";
        private const string BenchCountKey = "kic_bench_visit_count";
        private const string BenchVisitPrefix = "kic_bench_visit_";

        /// <summary>1 任性的指南针的解锁条件：坐过/开启 10 把椅子。</summary>
        public const int CompassBenchNeed = 10;

        /// <summary>该护符在当前存档里是否已解锁。</summary>
        public static bool IsUnlocked(int id)
        {
            try
            {
                return COOK.getSF(UnlockKeyPrefix + id) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>解锁一个护符（幂等）。</summary>
        public static void Unlock(int id, string reason)
        {
            try
            {
                if (IsUnlocked(id))
                {
                    return;
                }
                COOK.setSF(UnlockKeyPrefix + id, 1);
                CharmData cd = CharmDatabase.Get(id);
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[KIC][护符解锁] 已解锁 " + id + " " + (cd != null ? cd.Name : "?") +
                    "（" + reason + "）");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>已坐过的椅子数量。</summary>
        public static int BenchVisitCount
        {
            get
            {
                try
                {
                    return COOK.getSF(BenchCountKey);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>诺艾尔模式下每帧调用：检查各种解锁条件。</summary>
        public static void Tick(PRNoel pr)
        {
            try
            {
                if (pr == null || !pr.is_alive || IsKnightModeSafe())
                {
                    return;
                }
                if (IsUnlocked(CharmEffects.CompassId))
                {
                    return;
                }
                if (!pr.isBenchState())
                {
                    return; // 只有坐在椅子上才算"开启椅子"
                }
                string benchKey = BenchVisitPrefix + BenchIdOf(pr);
                if (COOK.getSF(benchKey) != 0)
                {
                    return; // 这把椅子之前坐过
                }
                COOK.setSF(benchKey, 1);
                int n = BenchVisitCount + 1;
                COOK.setSF(BenchCountKey, n);
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[KIC][护符解锁] 椅子 " + n + "/" + CompassBenchNeed + "：" + benchKey);
                if (n >= CompassBenchNeed)
                {
                    Unlock(CharmEffects.CompassId, "开启椅子 " + n + " 把");
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool IsKnightModeSafe()
        {
            try
            {
                return CharmEffects.IsKnightMode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>椅子的唯一标识：地图键 + 取整坐标（椅子是固定摆件，同一把椅子坐标稳定）。</summary>
        private static string BenchIdOf(PRNoel pr)
        {
            string mapKey = "?";
            try
            {
                Map2d mp = pr.Mp;
                if (mp != null && !string.IsNullOrEmpty(mp.key))
                {
                    mapKey = mp.key;
                }
            }
            catch (Exception)
            {
            }
            return mapKey + "_" + (int)pr.x + "_" + (int)pr.y;
        }
    }
}
