using System;

namespace KnightInCradle.CharmUi
{
    /// <summary>单个护符数据（由 护符.txt 生成）。</summary>
    public sealed class CharmData
    {
        public int Id;
        public string IconFile;
        public string Name;
        public string Desc;
        public int Cost;
    }

    /// <summary>41 个护符静态数据（40 虚空之心为固定护符，cost=0 不可卸下）。</summary>
    public static class CharmDatabase
    {
        public const int FixedCharmId = 40;
        public const int NotchCapacity = 11;
        /// <summary>寻神者模式选择器（自限机制，不占护符费用；仅能通过顶部 sign 装配）。</summary>
        public const int GgSelectorId = 42;
        public static readonly CharmData[] All = new CharmData[]
        {
            new CharmData { Id = 1, IconFile = "1_compass", Name = "任性的指南针", Desc = "允许持有者在地图上自由传送。", Cost = 1 },
            new CharmData { Id = 2, IconFile = "2_collector", Name = "蜂群集结", Desc = "小蜂群会为持有者收集魔力草中的所有灵魂。\n\n蜂群也会帮助持有者捡起周围的物品。\n\n适合那些无论多细小的东西都不愿意丢下的人。", Cost = 1 },
            new CharmData { Id = 3, IconFile = "3_sturdy", Name = "坚硬外壳", Desc = "增加受伤后的无敌时间。\n\n使其更容易在危险情形下逃脱。", Cost = 2 },
            new CharmData { Id = 4, IconFile = "4_soul_catcher", Name = "灵魂捕手", Desc = "萨满曾用它来从周围的世界中吸取更多灵魂。\n\n用骨钉劈砍敌人能获得更多灵魂", Cost = 2 },
            new CharmData { Id = 5, IconFile = "5_shaman", Name = "萨满之石", Desc = "据说包含前代萨满的学识。\n\n使造成的法术伤害提高。", Cost = 3 },
            new CharmData { Id = 6, IconFile = "6_soul_eater", Name = "噬魂者", Desc = "被遗忘的萨满神器，用来从活着的生物身上吸取灵魂。\n\n大大增加用骨钉劈砍敌人获得的灵魂。", Cost = 4 },
            new CharmData { Id = 7, IconFile = "7_rush_master", Name = "冲刺大师", Desc = "有着被称为“冲刺大师”的古怪虫子形象。\n\n持有者将能够更频繁地冲刺，也能向下冲刺。", Cost = 2 },
            new CharmData { Id = 8, IconFile = "8_runner", Name = "飞毛腿", Desc = "有着被称为“飞毛腿”的古怪虫子形象。\n\n提高持有者的移动速度。", Cost = 1 },
            new CharmData { Id = 9, IconFile = "9_little_bug", Name = "幼虫之歌", Desc = "包含被解救的幼虫的感激。\n\n受到伤害时，获得灵魂。", Cost = 1 },
            new CharmData { Id = 10, IconFile = "10_big_bug", Name = "蜕变挽歌", Desc = "包含将要步入生命的下一个阶段的幼虫的感激。\n\n当持有者处于满血状态时，能从骨钉中射出白热能量束，对前方一定距离的敌人造成伤害。", Cost = 3 },
            new CharmData { Id = 11, IconFile = "11_hard_heart", Name = "坚固心脏", Desc = "提高持有者的血量上限。\n\n这个护符很坚固，不会破碎。", Cost = 2 },
            new CharmData { Id = 12, IconFile = "12_hard_geo", Name = "坚固贪婪", Desc = "大幅提升背包上限，每持有4个物品，持有者造成的伤害降低0.75%。击杀敌人会掉落丰厚的资源。\n\n这个护符很坚固，不会破碎。", Cost = 2 },
            new CharmData { Id = 13, IconFile = "13_hard_power", Name = "坚固力量", Desc = "使骨钉的伤害提升。\n\n这个护符很坚固，不会破碎。", Cost = 3 },
            new CharmData { Id = 14, IconFile = "14_spell_twister", Name = "法术扭曲者", Desc = "反映了灵魂圣所掌握灵魂力量的欲望。\n\n法术的灵魂消耗降低。", Cost = 2 },
            new CharmData { Id = 15, IconFile = "15_stable", Name = "稳定之体", Desc = "攻击时不会产生后坐力。\n\n使持有者保持稳定，持续攻击。", Cost = 1 },
            new CharmData { Id = 16, IconFile = "16_heavy", Name = "沉重之击", Desc = "阵亡战士的骨钉形成，渴望再次被拾起。\n\n普通攻击有8%的概率直接击杀目标。", Cost = 2 },
            new CharmData { Id = 17, IconFile = "17_fast_slash", Name = "快速劈砍", Desc = "诞生于那些被融合的不完美的废弃骨钉。\n\n允许持有者更快的挥动骨钉。", Cost = 3 },
            new CharmData { Id = 18, IconFile = "18_long_nail", Name = "修长之钉", Desc = "允许打击更远处的敌人。\n\n增加骨钉攻击范围。", Cost = 2 },
            new CharmData { Id = 19, IconFile = "19_pride", Name = "骄傲印记", Desc = "由螳螂部落慷慨赠予他们尊敬的人。\n\n大大增加骨钉的攻击范围。", Cost = 3 },
            new CharmData { Id = 20, IconFile = "20_fury", Name = "亡者之怒", Desc = "体现了那些将死之人的愤怒和英勇。\n\n接近死亡时，使骨钉的伤害大幅提升。", Cost = 2 },
            new CharmData { Id = 21, IconFile = "21_thorns", Name = "苦痛荆棘", Desc = "感受持有者的痛苦并鞭打周围的世界。\n\n受到伤害时，对周围的敌人造成伤害。", Cost = 1 },
            new CharmData { Id = 22, IconFile = "22_baldur_shell", Name = "巴尔德之壳", Desc = "在凝聚灵魂时，产生硬壳保护它的持有者。\n\n凝聚时抵挡攻击，最多4次。", Cost = 2 },
            new CharmData { Id = 23, IconFile = "23_nest", Name = "吸虫之巢", Desc = "吸虫之母肠道诞生的活的护符。\n\n将复仇之魂法术变成一群不稳定的幼小吸虫。", Cost = 3 },
            new CharmData { Id = 24, IconFile = "24_shelter", Name = "防御者纹章", Desc = "圣巢国王赋予最忠诚的骑士的独特护符。虽然有些刮痕和污渍，但依旧保存得很好。\n\n这个护符汲取了森林中凶猛魔物的力量，小型魔物难以承受这么强大的魔力。", Cost = 1 },
            new CharmData { Id = 25, IconFile = "25_uterus", Name = "发光子宫", Desc = "从持有者的身上汲取灵魂，用来产生幼崽。幼崽会飞向敌人保护持有者。\n\n幼崽体内同时具有光和暗两股力量，极不稳定。", Cost = 2 },
            new CharmData { Id = 26, IconFile = "26_fast_gather", Name = "快速聚集", Desc = "包含水晶镜片的护符。\n\n降低凝聚所需时间。", Cost = 3 },
            new CharmData { Id = 27, IconFile = "27_deep_gather", Name = "深度聚集", Desc = "在水晶内长时间自然形成。从周围的空气中吸取灵魂。\n\n凝聚能获得更多血量，但延长凝聚所需时间。开启宝箱时，轮转速度降低75%", Cost = 4 },
            new CharmData { Id = 28, IconFile = "28_blue_heart_1", Name = "生命血之心", Desc = "包含一个活着的核心，浸出宝贵的生命血。\n\n在长椅上休息时回复2格生命血血量。", Cost = 2 },
            new CharmData { Id = 29, IconFile = "29_blue_heart_2", Name = "生命血核心", Desc = "包含一个活着的核心，流出宝贵的生命血。\n\n在长椅上休息时回复4格生命血血量。", Cost = 3 },
            new CharmData { Id = 30, IconFile = "30_Johnny", Name = "乔尼的祝福", Desc = "由仁慈的异教徒乔尼给予的祝福。使所有血量变成生命血，并提升血量上限。\n\n持有者无法聚集灵魂回复生命。", Cost = 4 },
            new CharmData { Id = 31, IconFile = "31_hive", Name = "蜂巢之血", Desc = "蜂巢中珍贵的金色硬化花蜜块。\n\n使持有者每10秒能够回复1血量", Cost = 4 },
            new CharmData { Id = 32, IconFile = "32_mushroom", Name = "蘑菇孢子", Desc = "由活的真菌物质组成。\n\n凝聚将释放出一片孢子云，对敌人持续造成伤害。", Cost = 1 },
            new CharmData { Id = 33, IconFile = "33_shadow", Name = "锋利之影", Desc = "含有被禁用的法术，能将影子转换为致命的武器。\n\n使用暗影冲刺穿过敌人会对其造成伤害。将暗影冲刺的速度提升40%。", Cost = 2 },
            new CharmData { Id = 34, IconFile = "34_wuen", Name = "乌恩之形", Desc = "让持有者展现出内在的乌恩形态\n\n凝聚时可以在地面上移动。", Cost = 2 },
            new CharmData { Id = 35, IconFile = "35_nail_master", Name = "骨钉大师的荣耀", Desc = "包含一个骨钉大师的激情、技能和遗憾。\n\n使骨钉技艺的蓄力时间由1.35秒降至0.75秒。", Cost = 1 },
            new CharmData { Id = 36, IconFile = "36_spider", Name = "编织者之歌", Desc = "一个缠丝的护符，蕴含着那些离开圣巢返回家乡的编织者所留下的离别之歌。\n\n召唤三只小小的编织者幼体攻击敌人。这些编织者在故乡的灾难中幸存，拥有更强的力量。", Cost = 2 },
            new CharmData { Id = 37, IconFile = "37_dream", Name = "舞梦者", Desc = "专门给挥动梦之钉和收集精华的人准备的护符。用梦之钉击中敌人获得的灵魂增加，同时使用梦之钉攻击速度加快。\n\n这里的生物虽使用魔力，但仍然具有灵魂和鲜活的梦境。", Cost = 1 },
            new CharmData { Id = 38, IconFile = "38_dream_protecter", Name = "梦之盾", Desc = "生成一面缓慢围绕持有者旋转的盾牌，对敌人造成与当前骨钉相等的接触伤害。\n\n这个护符蕴含了伟大战士的精神力，在持有者凝聚时会尽力保护持有者。", Cost = 3 },
            new CharmData { Id = 39, IconFile = "39_Grimm", Name = "格林之子", Desc = "一场完成的仪式的标志。包含着一团跳动的猩红之火。\n\n火焰必须燃烧，梦魇终将再临。", Cost = 2 },
            new CharmData { Id = 40, IconFile = "40_VOID", Name = "虚空之心", Desc = "隐藏在内部的空虚，现在不再受到约束。使虚空在持有者的意志下联合起来。\n\n这个护符是持有者的一部分，不能卸下。", Cost = 0 },
            new CharmData { Id = 41, IconFile = "41_KING", Name = "国王之魂", Desc = "象征着高等生灵相互结合的圣洁护符。\n\n持有者能缓慢吸收其中无限的灵魂。", Cost = 5 },
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
    }
}
