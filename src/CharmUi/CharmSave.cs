using System;
using System.Collections.Generic;
using nel;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符归属：小骑士 / 诺艾尔。
    /// 两边的装备列表、存档命名空间、以及（第二部分的）护符效果互相独立。
    /// </summary>
    public enum CharmOwner
    {
        Knight = 0,
        Noel = 1,
    }

    /// <summary>
    /// 护符装备存档：把当前装备列表写入 AIC 存档的 SF 字典（COOK.Osf），
    /// SF 字典会随 createBinary 序列化进存档文件、读档时随 readBinary 恢复。
    ///
    /// 命名空间（两套互相独立）：
    ///   - 小骑士：kic_charm_slot0..slot10（历史格式，保持兼容，**不要改**）
    ///   - 诺艾尔：kic_noel_charm_slot0..slot10（"诺艾尔的护符"新增）
    /// 每套最多 CharmDatabase.NotchCapacity（11）个容量槽，不含固定虚空之心。
    /// </summary>
    public static class CharmSave
    {
        private const string KnightKeyPrefix = "kic_charm_slot";
        private const string NoelKeyPrefix = "kic_noel_charm_slot";
        private static int MaxSlots => CharmDatabase.NotchCapacity; // 11 + 炼金护符槽

        /// <summary>小骑士的已装备快照（不含固定虚空之心）：
        /// 护符控制器未创建时也能让护符效果立即生效（无需先打开护符 UI）。</summary>
        private static readonly List<int> _equipped = new List<int>();

        /// <summary>诺艾尔的已装备快照（不含固定虚空之心）。第一部分只做装配，
        /// 第二部分让诺艾尔的护符效果读这一份。</summary>
        private static readonly List<int> _equippedNoel = new List<int>();

        private static List<int> ListFor(CharmOwner owner)
        {
            return owner == CharmOwner.Noel ? _equippedNoel : _equipped;
        }

        private static string PrefixFor(CharmOwner owner)
        {
            return owner == CharmOwner.Noel ? NoelKeyPrefix : KnightKeyPrefix;
        }

        /// <summary>小骑士的当前已装备快照（护符控制器创建时按此恢复）。</summary>
        public static IReadOnlyList<int> EquippedSnapshot => _equipped;

        /// <summary>指定归属的当前已装备快照。</summary>
        public static IReadOnlyList<int> EquippedSnapshotFor(CharmOwner owner)
        {
            return ListFor(owner);
        }

        /// <summary>小骑士是否装备了某护符（不含固定虚空之心）。</summary>
        public static bool HasEquipped(int id)
        {
            return HasEquipped(CharmOwner.Knight, id);
        }

        /// <summary>指定归属是否装备了某护符（不含固定虚空之心）。</summary>
        public static bool HasEquipped(CharmOwner owner, int id)
        {
            return id > 0 && ListFor(owner).Contains(id);
        }

        /// <summary>从控制器同步当前装备到小骑士快照（装配/卸下后调用）。</summary>
        public static void SyncFromController()
        {
            SyncFromController(CharmOwner.Knight);
        }

        /// <summary>把控制器当前的装备列表同步到指定归属的快照。</summary>
        public static void SyncFromController(CharmOwner owner)
        {
            try
            {
                CharmUiController ctr = CharmUiController.Instance;
                if (ctr == null || ctr.Owner != owner)
                {
                    return; // 控制器正在编辑另一侧时不要动这一侧的快照
                }
                List<int> dst = ListFor(owner);
                dst.Clear();
                foreach (int id in ctr.EquippedIds)
                {
                    if (id != CharmDatabase.FixedCharmId && !dst.Contains(id))
                    {
                        dst.Add(id);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把当前装备列表写入小骑士的 SF 槽位（随下一次存档序列化）。</summary>
        public static void WriteEquipped()
        {
            WriteEquipped(CharmOwner.Knight);
        }

        /// <summary>把指定归属的当前装备列表写入它自己的 SF 槽位。</summary>
        public static void WriteEquipped(CharmOwner owner)
        {
            try
            {
                // 控制器正在编辑这一侧时以控制器为准；否则用该侧快照
                // （切换归属前的收尾写入就属于后者）。
                List<int> src;
                CharmUiController ctr = CharmUiController.Instance;
                if (ctr != null && ctr.Owner == owner)
                {
                    src = ctr.EquippedIds;
                }
                else
                {
                    src = ListFor(owner);
                }

                string prefix = PrefixFor(owner);
                // 先清掉旧槽位，避免卸下后残留
                for (int i = 0; i < MaxSlots; i++)
                {
                    COOK.setSF(prefix + i, 0);
                }
                int slot = 0;
                foreach (int id in src)
                {
                    if (id == CharmDatabase.FixedCharmId)
                    {
                        continue; // 虚空之心固定装备，不需要存档
                    }
                    if (slot >= MaxSlots)
                    {
                        break;
                    }
                    COOK.setSF(prefix + slot, id);
                    slot++;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>从小骑士的 SF 槽位读取装备列表（不含固定虚空之心，校验合法 id 并去重）。</summary>
        public static List<int> ReadEquipped()
        {
            return ReadEquipped(CharmOwner.Knight);
        }

        /// <summary>从指定归属的 SF 槽位读取装备列表。</summary>
        public static List<int> ReadEquipped(CharmOwner owner)
        {
            var list = new List<int>();
            try
            {
                string prefix = PrefixFor(owner);
                for (int i = 0; i < MaxSlots; i++)
                {
                    int id = COOK.getSF(prefix + i);
                    if (id <= 0 || id == CharmDatabase.FixedCharmId || list.Contains(id))
                    {
                        continue;
                    }
                    if (CharmDatabase.Get(id) != null)
                    {
                        list.Add(id);
                    }
                }
            }
            catch (Exception)
            {
            }
            return list;
        }

        /// <summary>读档/新游戏后调用：两套归属都从当前存档的 SF 字典恢复
        /// （每个存档独立记录，不会把上一存档的护符带到新存档），
        /// 控制器已存在且正停留在某一侧时，同步替换它的列表。</summary>
        public static void RestoreAfterLoad()
        {
            try
            {
                // 束缚（自限）状态每次读档重置：四个束缚全部消失，不跨存档/会话保留
                CharmEffects.ResetGgRestrictionsOnLoad();
                // 次数血（护符3 坚硬外壳·诺艾尔侧）同样重置会话状态：
                // 存档里的 maxhp 字段可能已经是"次数"，真实上限记在 SF 里，下一次 tick 会据此重新激活。
                CharmEffects.ResetNoelSturdyOnLoad();
                CharmEffects.ResetNoelHeartOnLoad();
                RestoreOwner(CharmOwner.Knight);
                RestoreOwner(CharmOwner.Noel);
                // 坚固贪婪：每次读档都把背包容量修正到与佩戴状态一致
                CharmEffects.FixGreedCapacityAfterLoad();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>恢复单个归属：刷新它的快照；控制器若正停留在该归属则同步替换其列表。</summary>
        private static void RestoreOwner(CharmOwner owner)
        {
            List<int> saved = ReadEquipped(owner);
            List<int> dst = ListFor(owner);
            dst.Clear();
            dst.AddRange(saved);

            CharmUiController ctr = CharmUiController.Instance;
            if (ctr != null && ctr.Owner == owner)
            {
                ctr.ApplyEquippedFromSave(saved);
            }
        }
    }
}
