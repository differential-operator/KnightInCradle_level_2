using System;
using System.Collections.Generic;
using nel;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符装备存档：把当前装备列表写入 AIC 存档的 SF 字典（COOK.Osf），
    /// SF 字典会随 createBinary 序列化进存档文件、读档时随 readBinary 恢复。
    /// 槽位 key：kic_charm_slot0..slot10（最多 11 个容量槽，不含固定虚空之心）。
    /// </summary>
    public static class CharmSave
    {
        private const string KeyPrefix = "kic_charm_slot";
        private const int MaxSlots = CharmDatabase.NotchCapacity; // 11

        /// <summary>当前已装备列表（不含固定虚空之心）的独立快照：
        /// 护符控制器未创建时也能让护符效果立即生效（无需先打开护符 UI）。</summary>
        private static readonly List<int> _equipped = new List<int>();

        /// <summary>当前已装备快照（护符控制器创建时按此恢复）。</summary>
        public static IReadOnlyList<int> EquippedSnapshot => _equipped;

        /// <summary>护符 id 是否已装备（不含固定虚空之心）。</summary>
        public static bool HasEquipped(int id)
        {
            return id > 0 && _equipped.Contains(id);
        }

        /// <summary>从控制器同步当前装备到快照（装配/卸下后调用）。</summary>
        public static void SyncFromController()
        {
            try
            {
                _equipped.Clear();
                CharmUiController ctr = CharmUiController.Instance;
                if (ctr == null)
                {
                    return;
                }
                foreach (int id in ctr.EquippedIds)
                {
                    if (id != CharmDatabase.FixedCharmId && !_equipped.Contains(id))
                    {
                        _equipped.Add(id);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把当前装备列表写入 SF（随下一次存档序列化）。</summary>
        public static void WriteEquipped()
        {
            try
            {
                CharmUiController ctr = CharmUiController.Instance;
                if (ctr == null)
                {
                    return;
                }
                // 先清掉旧槽位，避免卸下后残留
                for (int i = 0; i < MaxSlots; i++)
                {
                    COOK.setSF(KeyPrefix + i, 0);
                }
                int slot = 0;
                foreach (int id in ctr.EquippedIds)
                {
                    if (id == CharmDatabase.FixedCharmId)
                    {
                        continue; // 虚空之心固定装备，不需要存档
                    }
                    if (slot >= MaxSlots)
                    {
                        break;
                    }
                    COOK.setSF(KeyPrefix + slot, id);
                    slot++;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>从 SF 读取装备列表（不含固定虚空之心，校验合法 id 并去重）。</summary>
        public static List<int> ReadEquipped()
        {
            var list = new List<int>();
            try
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    int id = COOK.getSF(KeyPrefix + i);
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

        /// <summary>读档/新游戏后调用：每次读档都从当前存档的 SF 字典恢复装备
        /// （每个存档独立记录，不会把上一存档的护符带到新存档），
        /// 控制器已存在则同步替换其列表。</summary>
        public static void RestoreAfterLoad()
        {
            try
            {
                // 束缚（自限）状态每次读档重置：四个束缚全部消失，不跨存档/会话保留
                CharmEffects.ResetGgRestrictionsOnLoad();
                List<int> saved = ReadEquipped();
                _equipped.Clear();
                _equipped.AddRange(saved);
                CharmUiController ctr = CharmUiController.Instance;
                if (ctr != null)
                {
                    ctr.ApplyEquippedFromSave(saved);
                }
                // 坚固贪婪：每次读档都把背包容量修正到与佩戴状态一致
                CharmEffects.FixGreedCapacityAfterLoad();
            }
            catch (Exception)
            {
            }
        }
    }
}
