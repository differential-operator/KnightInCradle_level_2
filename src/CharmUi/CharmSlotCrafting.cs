using System;
using System.Reflection;
using HarmonyLib;
using nel;
using XX;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 炼金工坊里新增的三种「护符槽」物品与配方（需求 2026-09-26）：
    /// ・简易护符槽 = 铁矿 60 + 铬矿 60 + 石英 60（无金币）
    /// ・精致护符槽 = 金币 5000 + 铜矿 40 + 金矿 40 + 紫水晶 80
    /// ・坚固护符槽 = 紫水晶 100 + 托帕石 60（无金币，需已解锁 20 个护符才有制作条件；
    ///   未达成时**配方仍出现在炼金列表里，但不能制作**）
    ///
    /// 产物属于**重要物品**（`NelItem.is_precious` → 自动进 `StPrecious` 那一格），
    /// 并且**拿到就永久生效**：每种护符槽 +1 个护符槽上限（不做消耗品，不需要装备/使用）。
    ///
    /// 实现方式（尽量用游戏自己的加载器，少手工造对象）：
    /// ① 物品：`NelItem.readItemScript(string)` 后缀里用 `NelItem.CreateItemEntry` 注册 3 个物品，
    ///    名称/描述直接用代码里的委托给出（不动本地化文件），图标借用现有矿石物品的图标；
    /// ② 配方：`TX.getResource("Data/recipe", …)` 后缀里把三段配方文本追加到脚本尾部，
    ///    让 `RCP.initScript` 自己解析（费用 / 材料 / 产物 / 数量全部走原版格式）；
    /// ③ 条件：`RCP.Recipe.checkUseable(...)` 前缀拦住"坚固护符槽" —— 未解锁 20 个护符时返回不可制作。
    /// </summary>
    internal static class CharmSlotCrafting
    {
        public const string SimpleKey = "kic_charm_notch_simple";
        public const string FineKey = "kic_charm_notch_fine";
        public const string SolidKey = "kic_charm_notch_solid";
        /// <summary>坚固护符槽的制作条件：已解锁护符数。</summary>
        public const int SolidNeedUnlockedCharms = 20;

        private static readonly string[] AllKeys = { SimpleKey, FineKey, SolidKey };
        private static bool _itemsCreated;
        private static bool _recipeInjected;

        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo readItem = AccessTools.Method(typeof(NelItem), "readItemScript", new[] { typeof(string) });
                if (readItem != null)
                {
                    harmony.Patch(readItem, postfix: new HarmonyMethod(
                        typeof(CharmSlotCrafting).GetMethod(nameof(ItemScriptPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo txRes = AccessTools.Method(typeof(TX), "getResource",
                    new[] { typeof(string), typeof(string), typeof(bool) });
                if (txRes != null)
                {
                    harmony.Patch(txRes, postfix: new HarmonyMethod(
                        typeof(CharmSlotCrafting).GetMethod(nameof(RecipeResourcePostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo checkUseable = AccessTools.Method(typeof(RCP.Recipe), "checkUseable",
                    new[] { typeof(System.Collections.Generic.List<RCP.RecipeIngredient>), typeof(ItemStorage[]), typeof(int) });
                if (checkUseable != null)
                {
                    harmony.Patch(checkUseable, prefix: new HarmonyMethod(
                        typeof(CharmSlotCrafting).GetMethod(nameof(CheckUseablePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
            catch (Exception ex)
            {
                KnightInCradlePlugin.PluginLog?.LogWarning("[KIC][护符槽] 补丁挂载失败：" + ex.Message);
            }
        }

        /// <summary>物品脚本解析完之后注册三种护符槽物品。</summary>
        private static void ItemScriptPostfix()
        {
            try
            {
                if (_itemsCreated)
                {
                    return;
                }
                _itemsCreated = true;
                int icon = -1;
                NelItem src = NelItem.GetById("mtr_amethyst0", true);
                if (src != null)
                {
                    icon = src.specific_icon_id;
                }
                CreateItem(SimpleKey, "简易护符槽",
                    "即便是最简单的护符槽，也需要提炼最纯粹的金属。", 61790, icon);
                CreateItem(FineKey, "精致护符槽",
                    "利用稀有金属对护符槽进行了强化，能够更好的容纳护符中的力量。", 61791, icon);
                CreateItem(SolidKey, "坚固护符槽",
                    "凝聚了水晶力量的坚固护符槽。", 61792, icon);
            }
            catch (Exception ex)
            {
                KnightInCradlePlugin.PluginLog?.LogWarning("[KIC][护符槽] 物品注册失败：" + ex.Message);
            }
        }

        private static void CreateItem(string key, string name, string desc, int id, int icon)
        {
            if (NelItem.GetById(key, true) != null)
            {
                return;
            }
            var itm = new NelItem(key, 0, 0, 1);
            itm.category = NelItem.CATEG.SPECIAL; // SPECIAL = 重要物品（`is_precious` 就是看它）→ StPrecious
            if (icon != -1)
            {
                itm.specific_icon_id = icon;
            }
            itm.FnGetName = delegate (STB Stb, NelItem Itm, int grade)
            {
                Stb.Add(name);
            };
            itm.FnGetDesc = delegate (STB Stb, NelItem Itm, int grade)
            {
                Stb.Add(desc);
            };
            NelItem.CreateItemEntry(key, itm, id, false);
        }

        /// <summary>把三段配方追加到原版配方脚本末尾（`#ALCHEMY` 段）。</summary>
        private static void RecipeResourcePostfix(string path, ref string __result)
        {
            try
            {
                if (_recipeInjected || __result == null || path == null)
                {
                    return;
                }
                if (path != "Data/recipe")
                {
                    return;
                }
                _recipeInjected = true;
                __result += RecipeScript;
            }
            catch (Exception)
            {
            }
        }

        private const string RecipeScript = @"

#ALCHEMY

/* ___ kic_notch_recipe_simple ___ */
%RECIPE_PRICE 0
%COMPLETION kic_charm_notch_simple
%CREATE_COUNT 1
mtr_iron0 60
mtr_chrom0 60
mtr_quartz0 60

/* ___ kic_notch_recipe_fine ___ */
%RECIPE_PRICE 5000
%COMPLETION kic_charm_notch_fine
%CREATE_COUNT 1
mtr_copper 40
mtr_gold 40
mtr_amethyst0 80

/* ___ kic_notch_recipe_solid ___ */
%RECIPE_PRICE 0
%COMPLETION kic_charm_notch_solid
%CREATE_COUNT 1
mtr_amethyst0 100
mtr_topaz 60
";

        /// <summary>坚固护符槽：未解锁 20 个护符时不可制作（列表里仍然看得到）。</summary>
        private static bool CheckUseablePrefix(RCP.Recipe __instance, ref bool __result)
        {
            try
            {
                if (__instance == null || __instance.key != "kic_notch_recipe_solid")
                {
                    return true;
                }
                if (CharmDatabase.UnlockedCharmCount >= SolidNeedUnlockedCharms)
                {
                    return true;
                }
                __result = false;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>已获得的护符槽数量（每种 +1，最多 3）。</summary>
        public static int CraftedSlotBonus
        {
            get
            {
                try
                {
                    ItemStorage st = CharmEffects.GetPreciousStorage();
                    if (st == null)
                    {
                        return 0;
                    }
                    int n = 0;
                    for (int i = 0; i < AllKeys.Length; i++)
                    {
                        NelItem itm = NelItem.GetById(AllKeys[i], true);
                        if (itm != null && st.getCount(itm) > 0)
                        {
                            n++;
                        }
                    }
                    return n;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }
    }
}
