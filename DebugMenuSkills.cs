using System;
using System.Reflection;
using HarmonyLib;
using Mortal.Core;

namespace LOMDailyAutomation
{
    // 作弊选单（F9，“活俠傳作弊測試選單”）技能扩展：
    // 它的下拉列表按 GameStatType 枚举生成，所以缺“医术 / 战术”这类天赋目标。
    // 做法：Start 之后把它的 items / gameStatTypes 各追加 N 个槽位（用枚举空洞 199/198 当虚拟类型），
    // 读取挂 UpdateCurrentStatValue 前缀、写入挂 PlayerStatData.Set 前缀，桥接到天赋等级。
    // 该插件可能没有安装：找不到 DebugMenu.Plugin 类型时静默跳过，不打补丁、不报错。
    public sealed partial class DailyAutomationPlugin
    {
        private sealed class DebugMenuSkillEntry
        {
            public string Label;
            public string TalentId;
            public GameStatType Sentinel;
        }

        // 技能条目表：以后要再加技能，往这里加一行即可（TalentId 见 STATUS 的功法数据解析）。
        private static readonly DebugMenuSkillEntry[] DebugMenuSkills =
        {
            new DebugMenuSkillEntry { Label = "★医术", TalentId = "2101", Sentinel = (GameStatType)199 },
            new DebugMenuSkillEntry { Label = "★战术", TalentId = "9100", Sentinel = (GameStatType)198 }
        };

        private static FieldInfo _debugMenuItemsField;
        private static FieldInfo _debugMenuStatTypesField;
        private static FieldInfo _debugMenuSelectedField;
        private static FieldInfo _debugMenuCurrentValueField;

        private void TryPatchDebugMenuSkills()
        {
            Type pluginType = AccessTools.TypeByName("DebugMenu.Plugin");
            if (pluginType == null)
            {
                Log("未安装“活俠傳作弊測試選單”（F9 菜单），跳过技能扩展。");
                return;
            }

            try
            {
                _debugMenuItemsField = AccessTools.Field(pluginType, "items");
                _debugMenuStatTypesField = AccessTools.Field(pluginType, "gameStatTypes");
                _debugMenuSelectedField = AccessTools.Field(pluginType, "selectedItemIndex");
                _debugMenuCurrentValueField = AccessTools.Field(pluginType, "currentStatValue");

                MethodInfo start = AccessTools.Method(pluginType, "Start");
                MethodInfo updateValue = AccessTools.Method(pluginType, "UpdateCurrentStatValue");
                MethodInfo statSet = AccessTools.Method(typeof(PlayerStatData), "Set", new[] { typeof(GameStatType), typeof(int) });

                if (_debugMenuItemsField == null || _debugMenuStatTypesField == null ||
                    _debugMenuSelectedField == null || _debugMenuCurrentValueField == null ||
                    start == null || updateValue == null || statSet == null)
                {
                    Logger.LogWarning("作弊选单结构不匹配（可能版本不同），跳过技能扩展。");
                    return;
                }

                _harmony.Patch(start,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DailyAutomationPlugin), "DebugMenuStartPostfix")));
                _harmony.Patch(updateValue,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DailyAutomationPlugin), "DebugMenuUpdateValuePrefix")));
                _harmony.Patch(statSet,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DailyAutomationPlugin), "DebugMenuStatSetPrefix")));
                Log("F9 作弊选单技能扩展已应用：下拉列表末尾会追加“★医术 / ★战术”，选中后可直接设定等级。");
            }
            catch (Exception ex)
            {
                Logger.LogError("作弊选单技能扩展失败（不影响本插件其它功能）：" + ex);
            }
        }

        // Start 后置：把 items / gameStatTypes 各扩 N 个槽位；已扩过则跳过（幂等）。
        private static void DebugMenuStartPostfix(object __instance)
        {
            try
            {
                if (__instance == null || _debugMenuItemsField == null || _debugMenuStatTypesField == null)
                {
                    return;
                }

                string[] items = _debugMenuItemsField.GetValue(__instance) as string[];
                GameStatType[] statTypes = _debugMenuStatTypesField.GetValue(__instance) as GameStatType[];
                if (items == null || statTypes == null || items.Length != statTypes.Length)
                {
                    return;
                }

                if (statTypes.Length > 0 &&
                    statTypes[statTypes.Length - 1] == DebugMenuSkills[DebugMenuSkills.Length - 1].Sentinel)
                {
                    return; // 已经扩展过
                }

                string[] newItems = new string[items.Length + DebugMenuSkills.Length];
                Array.Copy(items, newItems, items.Length);
                GameStatType[] newTypes = new GameStatType[statTypes.Length + DebugMenuSkills.Length];
                Array.Copy(statTypes, newTypes, statTypes.Length);
                for (int i = 0; i < DebugMenuSkills.Length; i++)
                {
                    newItems[items.Length + i] = DebugMenuSkills[i].Label;
                    newTypes[statTypes.Length + i] = DebugMenuSkills[i].Sentinel;
                }

                _debugMenuItemsField.SetValue(__instance, newItems);
                _debugMenuStatTypesField.SetValue(__instance, newTypes);
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    _instance.Logger.LogWarning("作弊选单列表扩展失败：" + ex.Message);
                }
            }
        }

        // UpdateCurrentStatValue 前缀：选中技能条目时改读天赋等级，并跳过原 switch。
        private static bool DebugMenuUpdateValuePrefix(object __instance)
        {
            try
            {
                if (__instance == null || _debugMenuStatTypesField == null ||
                    _debugMenuSelectedField == null || _debugMenuCurrentValueField == null)
                {
                    return true;
                }

                GameStatType[] statTypes = _debugMenuStatTypesField.GetValue(__instance) as GameStatType[];
                int selected = (int)_debugMenuSelectedField.GetValue(__instance);
                if (statTypes == null || selected < 0 || selected >= statTypes.Length)
                {
                    return true;
                }

                DebugMenuSkillEntry entry = FindDebugMenuSkill(statTypes[selected]);
                if (entry == null)
                {
                    return true; // 普通属性走原逻辑
                }

                PlayerTalentData talent = GetSkillTalent(entry.TalentId);
                _debugMenuCurrentValueField.SetValue(__instance, talent == null ? string.Empty : talent.Level.ToString());
                return false;
            }
            catch
            {
                return true;
            }
        }

        // PlayerStatData.Set 前缀：技能条目带着哨兵枚举值进来，桥接到天赋等级；普通属性走原逻辑。
        private static bool DebugMenuStatSetPrefix(GameStatType __0, int __1)
        {
            DebugMenuSkillEntry entry = FindDebugMenuSkill(__0);
            if (entry == null)
            {
                return true;
            }

            try
            {
                PlayerTalentData talent = GetSkillTalent(entry.TalentId);
                if (talent == null || _instance == null)
                {
                    return false;
                }

                int oldLevel = talent.Level;
                int delta = __1 - oldLevel;
                if (delta != 0)
                {
                    PlayerStatManagerData manager = PlayerStatManagerData.Instance;
                    if (manager != null && manager.Talents != null)
                    {
                        // Add 内部钳制到 [0, 满级]，与升级用的是同一条。
                        manager.Talents.Add(entry.TalentId, delta);
                    }
                }

                PlayerTalentData updated = GetSkillTalent(entry.TalentId);
                int newLevel = updated != null ? updated.Level : oldLevel;
                _instance.Log("作弊选单技能：" + entry.Label.TrimStart('★') + " 等级 " +
                    oldLevel + " → " + newLevel + "（输入 " + __1 + "）");
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    _instance.Logger.LogError("作弊选单技能设定失败：" + ex);
                }
            }

            return false;
        }

        private static DebugMenuSkillEntry FindDebugMenuSkill(GameStatType type)
        {
            for (int i = 0; i < DebugMenuSkills.Length; i++)
            {
                if (DebugMenuSkills[i].Sentinel == type)
                {
                    return DebugMenuSkills[i];
                }
            }

            return null;
        }

        private static PlayerTalentData GetSkillTalent(string talentId)
        {
            PlayerStatManagerData manager = PlayerStatManagerData.Instance;
            if (manager == null)
            {
                return null;
            }

            PlayerTalentDataCollection talents = manager.Talents;
            return talents == null ? null : talents.Get(talentId);
        }
    }
}
