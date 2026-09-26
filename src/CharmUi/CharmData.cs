using System;
using nel;
using UnityEngine;

namespace KnightInCradle.CharmUi
{
    /// <summary>单个护符数据（由 护符.txt 生成）。</summary>
    public sealed class CharmData
    {
        public int Id;
        public string IconFile;
        public string Name;
        /// <summary>
        /// 未解锁时面板上"未解锁：…"那一行的正文（`docs/护符效果描述.md` 的"未解锁"行）。
        /// 空 = 这条护符没有未解锁文案（例如虚空之心这种"持有者的一部分"）。
        /// </summary>
        public string LockedText;
        /// <summary>解锁后"解锁：…"那一行的正文（含 `\n\n` 分隔的风味文本）。</summary>
        public string Desc;
        public int Cost;
    }

    /// <summary>41 个护符静态数据（40 虚空之心为固定护符，cost=0 不可卸下）。</summary>
    public static class CharmDatabase
    {
        public const int FixedCharmId = 40;
        /// <summary>
        /// 护符槽上限：11 = 初始 3 + 开宝箱最多 8（这两部分等解锁/宝箱系统上线后再做成动态），
        /// 再**加上**炼金做出来的护符槽（三种各 +1，见 `CharmSlotCrafting.CraftedSlotBonus`）。
        /// </summary>
        /// <summary>护符槽上限（含基础 3、开箱最多 +8、预留 +3 = 14）。</summary>
        public const int MaxNotchCapacity = 14;
        public static int NotchCapacity => 3 + Mathf.Min(MaxNotchCapacity - 3, OpenedChestCount / 4);

        /// <summary>
        /// 已开启的宝箱总数（游戏自己的成就计数 `ACHIVE.MENT.treasure_total_obtain`，
        /// 就是"所有地图上开过的宝箱数"）。
        /// </summary>
        public static int OpenedChestCount
        {
            get
            {
                try
                {
                    return (int)COOK.CurAchive.Get(ACHIVE.MENT.treasure_total_obtain);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// 已解锁的护符数量（"坚固护符槽"的制作条件用它）。
        /// ⚠ 真正的解锁机制还没做：现在按"全部已解锁（36 个真正可用的护符）"计，
        /// 等解锁系统落地后改成读 `CharmSave` 里的解锁集合即可。
        /// </summary>
        public static int UnlockedCharmCount
        {
            get
            {
                if (KnightInCradlePlugin.PreviewLockedCharmText)
                {
                    return 0;
                }
                int n = 0;
                for (int i = 0; i < All.Length; i++)
                {
                    if (All[i].Id >= 1 && All[i].Id <= 36 && !IsLocked(All[i].Id))
                    {
                        n++;
                    }
                }
                return n;
            }
        }
        /// <summary>寻神者模式选择器（自限机制，不占护符费用；仅能通过顶部 sign 装配）。</summary>
        public const int GgSelectorId = 42;
        public static readonly CharmData[] All = new CharmData[]
        {
            // 诺艾尔侧的文本与费用按 `docs/护符效果描述.md` 重写：
            //   LockedText = 面板上"未解锁：…"那一行；Desc = "解锁：…"那一行（含 \n\n 后的风味文本）。
            new CharmData { Id = 1, IconFile = "1_compass", Name = "任性的指南针", Cost = 1,
                LockedText = "开启10个椅子。",
                Desc = "允许漫游者在地图上随意移动。" },
            new CharmData { Id = 2, IconFile = "2_collector", Name = "蜂群集结", Cost = 1,
                LockedText = "在一间蜂巢中取得胜利。",
                Desc = "自动收集周围散落的物品。\n\n适合那些无论多细小的东西都不愿意丢下的人。" },
            new CharmData { Id = 3, IconFile = "3_sturdy", Name = "坚硬外壳", Cost = 3,
                LockedText = "完成普莉梅拉的教学。",
                Desc = "使持有者具有更强的韧性，使持有者获得一定不受伤害的时间。\n\n使其更容易在危险情形下逃脱。" },
            new CharmData { Id = 4, IconFile = "4_soul_catcher", Name = "灵魂捕手", Cost = 2,
                LockedText = "MP达到250.",
                Desc = "传说蜗牛萨满曾用它来从周围的世界中吸取灵魂。\n\n用法术攻击敌人时获得额外魔力。" },
            new CharmData { Id = 5, IconFile = "5_shaman", Name = "萨满之石", Cost = 4,
                LockedText = "累计使用100次法术。",
                Desc = "传说包含了萨满学识的神秘护符。\n\n提高法术的威力，对敌人造成更多伤害。" },
            new CharmData { Id = 6, IconFile = "6_soul_eater", Name = "噬魂者", Cost = 4,
                LockedText = "MP达到500.",
                Desc = "被遗忘的萨满神器，用来从活着的生物身上吸取灵魂。\n\n大大增加用法术攻击敌人时获得的魔力数量。" },
            new CharmData { Id = 7, IconFile = "7_rush_master", Name = "冲刺大师", Cost = 2,
                LockedText = "同一房间内朝一个方向冲刺且不被打断的时间达到7秒。",
                Desc = "在童话里被称为“冲刺大师”的古怪虫子形象。\n\n使持有者能够持续冲刺，但冲刺的速度略微降低。" },
            new CharmData { Id = 8, IconFile = "8_runner", Name = "飞毛腿", Cost = 1,
                LockedText = "击败10只幼犬。",
                Desc = "在童话里被称为“飞毛腿”的古怪虫子形象。\n\n增加持有者的移动速度，使其能避开危险或追上敌人。" },
            new CharmData { Id = 9, IconFile = "9_little_bug", Name = "幼虫之歌", Cost = 1,
                LockedText = "击败10只蚂蝗。",
                Desc = "在童话里由幼虫的感激凝聚而成的护符。\n\n受到伤害时获得灵魂。" },
            new CharmData { Id = 10, IconFile = "10_big_bug", Name = "蜕变挽歌", Cost = 4,
                LockedText = "直面曾经的阴影。",
                Desc = "由直面恐惧的勇气凝聚而成，为武器灌输神圣的力量。\n\n使持有者的攻击能够向前发射白热能量束。" },
            new CharmData { Id = 11, IconFile = "11_hard_heart", Name = "坚固心脏", Cost = 2,
                LockedText = "HP达到300.",
                Desc = "增加持有者的生命值，承受更多伤害。\n\n因为怕疼就全点生命了。" },
            new CharmData { Id = 12, IconFile = "12_hard_geo", Name = "坚固贪婪", Cost = 2,
                LockedText = "累计获得2000金币。",
                Desc = "大大提高持有者的背包上限，击杀敌人时能获取更加丰富的物资。\n\n用未知的神秘技术改造了背包，目前没发现风险......应该吧。" },
            new CharmData { Id = 13, IconFile = "13_hard_power", Name = "坚固力量", Cost = 4,
                LockedText = "获得50场战斗的胜利",
                Desc = "使持有者的近战攻击造成更多伤害。\n\n从一场又一场战斗中积累起来的经验。" },
            new CharmData { Id = 14, IconFile = "14_spell_twister", Name = "法术扭曲者", Cost = 3,
                LockedText = "累计使用400次法术",
                Desc = "从周围的空气中凝聚魔力，从而减少法术的消耗。\n\n十分可怕的护符，当然，对于被攻击的魔物来说。" },
            new CharmData { Id = 15, IconFile = "15_stable", Name = "稳定之体", Cost = 1,
                LockedText = "累计摔倒10次",
                Desc = "使持有者碰到魔物不会摔倒。\n\n能够保持稳定，持续攻击。" },
            new CharmData { Id = 16, IconFile = "16_heavy", Name = "沉重之击", Cost = 2,
                LockedText = "单次攻击造成超过2000伤害。",
                Desc = "连续对魔物造成伤害时，攻击力得到提升。\n\n真的很重。" },
            new CharmData { Id = 17, IconFile = "17_fast_slash", Name = "快速劈砍", Cost = 3,
                LockedText = "在10秒内击杀5只魔物。",
                Desc = "允许持有者更快地挥动法杖。\n\n熟能生巧。" },
            new CharmData { Id = 18, IconFile = "18_long_nail", Name = "修长之钉", Cost = 2,
                LockedText = "利用突进冲击击杀5只魔物。",
                Desc = "增加持有者法杖的攻击范围，允许打击更远处的敌人。\n\n有时候就差一点点。" },
            new CharmData { Id = 19, IconFile = "19_pride", Name = "骄傲印记", Cost = 3,
                LockedText = "在雷雨天，危险度为160的情况下获得一场战斗的胜利。",
                Desc = "大大增加持有者法杖的攻击范围，使其能够从更远处打击敌人。\n\n应得的荣誉。" },
            new CharmData { Id = 20, IconFile = "20_fury", Name = "亡者之怒", Cost = 4,
                LockedText = "？？？",
                Desc = "由苦痛、不甘与愤怒凝聚而成。\n\n当接近死亡时，持有者的力量会全面爆发，但仍然会达到身体的极限。" },
            new CharmData { Id = 21, IconFile = "21_thorns", Name = "苦痛荆棘", Cost = 2,
                LockedText = "受到单次超过100的伤害。",
                Desc = "感受持有者的痛苦并鞭打周围的世界。\n\n当受到伤害时，对附近的敌人双倍奉还。" },
            new CharmData { Id = 22, IconFile = "22_baldur_shell", Name = "巴尔德之壳", Cost = 3,
                LockedText = "利用护盾抵挡5次魔物的攻击。",
                Desc = "在咏唱魔法时，产生硬壳保护它的持有者。\n\n外壳不是无法破坏的，吸收太多的伤害后将会破碎。一定时间后才能恢复。" },
            new CharmData { Id = 23, IconFile = "23_nest", Name = "吸虫之巢", Cost = 3,
                LockedText = "在危险度大于等于90时战胜匣中恶魔。",
                Desc = "将纯白之箭和聚能火球法术变成一群不稳定的幼小吸虫。" },
            new CharmData { Id = 24, IconFile = "24_shelter", Name = "防御者纹章", Cost = 1,
                LockedText = "在危险度大于等于90时战胜妖狐。",
                Desc = "仿照森林中最强大的魔物制成的小法阵。\n\n法阵富含魔力，但对于饥饿的魔物来说有些过量了。" },
            new CharmData { Id = 25, IconFile = "25_uterus", Name = "发光子宫", Cost = 2,
                LockedText = "击败一只巢厄。",
                Desc = "巢厄体内用来繁殖小剑山的器官。\n\n经过人为干预后，这些剑山没有了进食的意愿，并会牺牲自己来保护持有者。" },
            new CharmData { Id = 26, IconFile = "26_fast_gather", Name = "快速聚集", Cost = 3,
                LockedText = "？？？",
                Desc = "能够提升法术咏唱的速度。" },
            new CharmData { Id = 27, IconFile = "27_deep_gather", Name = "深度聚集", Cost = 4,
                LockedText = "给与水晶护符更纯粹的矿物。",
                Desc = "对姐姐给的水晶护符进行了强化。持有者咏唱魔法时速度减半，但在咏唱时回复生命值。" },
            new CharmData { Id = 28, IconFile = "28_blue_heart_1", Name = "生命血之心", Cost = 2,
                LockedText = "获得所有满级的水果。",
                Desc = "异界的护符，能够将少部分血量置换为魔力。\n\n感觉护符里有东西在跳动..." },
            new CharmData { Id = 29, IconFile = "29_blue_heart_2", Name = "生命血核心", Cost = 3,
                LockedText = "制作20道不同的食物。",
                Desc = "异界的护符，能够将部分血量置换为魔力。\n\n感觉护符里有东西在看自己..." },
            new CharmData { Id = 30, IconFile = "30_Johnny", Name = "乔尼的祝福", Cost = 4,
                LockedText = "？？？",
                Desc = "异界的护符，能够将部分全部血量置换为魔力。\n\n感觉全身的器官都被控制，但似乎还不错？" },
            new CharmData { Id = 31, IconFile = "31_hive", Name = "蜂巢之血", Cost = 4,
                LockedText = "让蜂巢对你产生敬畏。",
                Desc = "蜂巢中珍贵的金色硬化花蜜块。\n\n使持有者能够缓慢恢复生命。" },
            new CharmData { Id = 32, IconFile = "32_mushroom", Name = "蘑菇孢子", Cost = 1,
                LockedText = "在危险度大于等于90时战胜菌丝之王。",
                Desc = "由活的真菌物质组成，证明了你在蘑菇中的地位。\n\n可以和蘑菇们愉快的玩耍了！" },
            new CharmData { Id = 33, IconFile = "33_shadow", Name = "锋利之影", Cost = 4,
                LockedText = "习得幻影闪避。",
                Desc = "含有被禁用的法术，短暂蓄力后能将影子转换为致命的武器。\n\n你无法使用护盾或者闪避。" },
            new CharmData { Id = 34, IconFile = "34_wuen", Name = "乌恩之形", Cost = 2,
                LockedText = "从魔物手中成功挣脱5次。",
                Desc = "让持有者展现出内在的乌恩形态，在蹲下或爬行时缓慢恢复生命，同时使持有者不会被魔物攻击。\n\n被抓到的话就完了吧..." },
            new CharmData { Id = 35, IconFile = "35_nail_master", Name = "骨钉大师的荣耀", Cost = 5,
                LockedText = "获得所有满级法杖。",
                Desc = "以抛弃魔法为代价，加强普通攻击的力量，并能在短暂蓄力后使用强力技能。\n\n童话里的骨钉大师，然而现实里没有骨钉，只能做法杖大师了。" },
            new CharmData { Id = 36, IconFile = "36_spider", Name = "编织者之歌", Cost = 2,
                LockedText = "击败山蜘蛛。",
                Desc = "从洞穴里找到的小蜘蛛，跟随并保护救出它们的持有者。\n\n它们没有魔物的那种器官，是从哪里来的呢？" },
            // 37〜39（+41 国王之魂）按 `docs/护符效果描述.md`：**可选中但无法佩戴**，只给"未解锁"文案。
            new CharmData { Id = 37, IconFile = "37_dream", Name = "舞梦者", Cost = 1,
                LockedText = "童话里的传说护符，难以仿制。",
                Desc = "专门给挥动梦之钉和收集精华的人准备的护符。用梦之钉击中敌人获得的灵魂增加，同时使用梦之钉攻击速度加快。\n\n这里的生物虽使用魔力，但仍然具有灵魂和鲜活的梦境。" },
            new CharmData { Id = 38, IconFile = "38_dream_protecter", Name = "梦之盾", Cost = 3,
                LockedText = "童话里的传说武器，难以仿制。",
                Desc = "生成一面缓慢围绕持有者旋转的盾牌，对敌人造成与当前骨钉相等的接触伤害。\n\n这个护符蕴含了伟大战士的精神力，在持有者凝聚时会尽力保护持有者。" },
            new CharmData { Id = 39, IconFile = "39_Grimm", Name = "格林之子", Cost = 2,
                LockedText = "童话里带来梦魇的恶魔，很多小孩子都喜欢这个恐怖又优雅的角色。",
                Desc = "一场完成的仪式的标志。包含着一团跳动的猩红之火。\n\n火焰必须燃烧，梦魇终将再临。" },
            new CharmData { Id = 40, IconFile = "40_VOID", Name = "虚空之心", Desc = "隐藏在内部的空虚，现在不再受到约束。使虚空在持有者的意志下联合起来。\n\n这个护符是持有者的一部分，不能卸下。", Cost = 0 },
            new CharmData { Id = 41, IconFile = "41_KING", Name = "国王之魂", Cost = 5,
                LockedText = "童话里的苍白之王，他的王国万世长存。",
                Desc = "象征着高等生灵相互结合的圣洁护符。\n\n持有者能缓慢吸收其中无限的灵魂。" },
            new CharmData { Id = 43, IconFile = "42_tune", Name = "无忧旋律", Desc = "纪念一份友谊建立的信物。包含一首可能使持有者免受伤害的守护之歌。", Cost = 3 },
            new CharmData { Id = 42, IconFile = "gg_godseeker_mode_selector", Name = "束缚", Desc = "", Cost = 0 },
        };

        public static CharmData Get(int id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        /// <summary>
        /// 这条护符当前是否"未解锁"（面板据此显示 `未解锁：…` / `解锁：…` 两种文案）。
        /// ⚠ 真正的解锁机制（任务/条件解锁、护符槽数量）还没做：现在只有配置项
        /// `[CharmUi] PreviewLockedText` 的预览开关，用来在游戏里核对文案；
        /// 等解锁系统落地后，这里改成读 `CharmSave` 里的解锁集合即可，UI 侧不用再动。
        /// 虚空之心（固定护符）与"束缚"（GG 选择器）永远不算未解锁。
        /// </summary>
        public static bool IsLocked(int id)
        {
            return IsLocked(id, CharmOwner.Noel);
        }

        /// <summary>同上，但区分是哪一套护符：小骑士那套暂时永远不算"未解锁"。</summary>
        public static bool IsLocked(int id, CharmOwner owner)
        {
            if (id == FixedCharmId || id == GgSelectorId)
            {
                return false;
            }
            if (KnightInCradlePlugin.PreviewLockedCharmText)
            {
                return true;
            }
            if (owner == CharmOwner.Knight)
            {
                return false; // 小骑士那套的解锁机制还没做
            }
            return !CharmUnlocks.IsUnlocked(id);
        }
    }
}
