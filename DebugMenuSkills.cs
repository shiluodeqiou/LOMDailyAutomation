using System;
using System.Reflection;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LOMDailyAutomation
{
    // 天赋（技能）等级读写小工具：医术/战术等天赋的访问，供下面两个作弊界面扩展共用。
    internal static class SkillTalentUtil
    {
        public static PlayerTalentData Get(string talentId)
        {
            PlayerStatManagerData manager = PlayerStatManagerData.Instance;
            if (manager == null)
            {
                return null;
            }

            PlayerTalentDataCollection talents = manager.Talents;
            return talents == null ? null : talents.Get(talentId);
        }

        // 返回调整后的等级；delta 由 AddLevel 内部钳制到 [0, 满级]。天赋不存在时返回 -1。
        public static int Adjust(string talentId, int delta)
        {
            PlayerTalentData talent = Get(talentId);
            if (talent == null)
            {
                return -1;
            }

            if (delta != 0)
            {
                PlayerStatManagerData manager = PlayerStatManagerData.Instance;
                if (manager != null && manager.Talents != null)
                {
                    manager.Talents.Add(talentId, delta);
                }
            }

            PlayerTalentData updated = Get(talentId);
            return updated != null ? updated.Level : talent.Level;
        }
    }

    // F9 游戏内置测试面板里克隆出来的“技能行”：显示等级、±1 调整。
    public sealed class DailyAutomationSkillRow : MonoBehaviour
    {
        private Text _valueText;
        private string _talentId;
        private int _lastLevel = int.MinValue;

        public void Init(Text valueText, string talentId)
        {
            _valueText = valueText;
            _talentId = talentId;
            _lastLevel = int.MinValue;
        }

        public void Adjust(int delta)
        {
            int level = SkillTalentUtil.Adjust(_talentId, delta);
            if (level >= 0)
            {
                _lastLevel = level;
                if (_valueText != null)
                {
                    _valueText.text = level.ToString();
                }
            }
        }

        private void Update()
        {
            PlayerTalentData talent = SkillTalentUtil.Get(_talentId);
            int level = talent != null ? talent.Level : -1;
            if (level != _lastLevel)
            {
                _lastLevel = level;
                if (_valueText != null)
                {
                    _valueText.text = level >= 0 ? level.ToString() : "?";
                }
            }
        }
    }

    // 技能目标表：Label 显示名、TalentId 天赋编号（见 STATUS 的功法数据解析）。
    // Sentinel 仅用于 F1「活俠傳作弊測試選單」把条目塞进它的 GameStatType 下拉（枚举空洞值）。
    public sealed partial class DailyAutomationPlugin
    {
        private sealed class SkillEntry
        {
            public string Label;
            public string TalentId;
            public GameStatType Sentinel;
        }

        private static readonly SkillEntry[] SkillEntries =
        {
            new SkillEntry { Label = "★医术", TalentId = "2101", Sentinel = (GameStatType)199 },
            new SkillEntry { Label = "★战术", TalentId = "9100", Sentinel = (GameStatType)198 }
        };

        // ---------- F9 游戏自带测试面板（活侠传助手绑的热键，配置项 DebugPanelKey） ----------
        // 面板行是场景静态对象（TestGameStatPanel 各自引用一个 GameStat 资产），没法改场景，
        // 改为运行期克隆一行做“技能行”：干掉原组件（其 ±5 监听目标消失即失效）、重接按钮为 ±1。

        private static readonly FieldInfo _statPanelNameField = AccessTools.Field(typeof(TestGameStatPanel), "_nameText");
        private static readonly FieldInfo _statPanelValueField = AccessTools.Field(typeof(TestGameStatPanel), "_valueText");
        private static readonly FieldInfo _statPanelAddField = AccessTools.Field(typeof(TestGameStatPanel), "_addText");
        private static readonly FieldInfo _statPanelStatField = AccessTools.Field(typeof(TestGameStatPanel), "_stat");

        // “心上人”数值 = lover 编号（剧本里 SetPlayer("lover", N) → 显示 M_Lover_0N）。
        // 名字取自游戏本地化（sharedassets0 的 M_Lover_00..08），简中文本。
        private static readonly string[] LoverNames =
        {
            "唐默铃", "瑞杏", "叶云裳", "虞小梅", "上官萤", "夏侯兰", "郁竹", "魏菊", "龙湘"
        };

        // F9 面板“角色数值”里的心上人行：数字后面标出对应人名（名字(编号)）。
        [HarmonyPatch(typeof(TestGameStatPanel), "UpdateValue")]
        private static class TestGameStatPanelValuePatch
        {
            private static void Postfix(TestGameStatPanel __instance)
            {
                if (_instance == null || __instance == null || _statPanelStatField == null || _statPanelValueField == null)
                {
                    return;
                }

                try
                {
                    GameStat stat = _statPanelStatField.GetValue(__instance) as GameStat;
                    Text valueText = _statPanelValueField.GetValue(__instance) as Text;
                    if (stat == null || valueText == null || stat.StatType != GameStatType.愛人)
                    {
                        return;
                    }

                    int id = stat.FinalValue;
                    string name = id >= 0 && id < LoverNames.Length ? LoverNames[id] : "未知";
                    valueText.text = name + "(" + id + ")";
                }
                catch
                {
                }
            }
        }
        private static readonly GameObject[] _createdSkillRows = new GameObject[2];

        [HarmonyPatch(typeof(TestGameStatPanel), "Start")]
        private static class TestGameStatPanelStartPatch
        {
            private static void Postfix(TestGameStatPanel __instance)
            {
                if (_instance != null)
                {
                    _instance.EnsureSkillRows(__instance);
                }
            }
        }

        private void EnsureSkillRows(TestGameStatPanel template)
        {
            if (template == null)
            {
                return;
            }

            bool allCreated = true;
            for (int i = 0; i < _createdSkillRows.Length; i++)
            {
                if (_createdSkillRows[i] == null)
                {
                    allCreated = false;
                    break;
                }
            }
            if (allCreated)
            {
                return;
            }

            try
            {
                GameObject templateGo = template.gameObject;
                Transform container = templateGo.transform.parent;
                if (container == null)
                {
                    return;
                }

                for (int i = 0; i < SkillEntries.Length && i < _createdSkillRows.Length; i++)
                {
                    if (_createdSkillRows[i] != null)
                    {
                        continue;
                    }

                    GameObject clone = Instantiate(templateGo, container);
                    clone.name = "DailySkill_" + SkillEntries[i].TalentId;

                    TestGameStatPanel clonePanel = clone.GetComponent<TestGameStatPanel>();
                    Text nameText = null;
                    Text valueText = null;
                    Text addText = null;
                    if (clonePanel != null && _statPanelNameField != null && _statPanelValueField != null)
                    {
                        nameText = _statPanelNameField.GetValue(clonePanel) as Text;
                        valueText = _statPanelValueField.GetValue(clonePanel) as Text;
                        if (_statPanelAddField != null)
                        {
                            addText = _statPanelAddField.GetValue(clonePanel) as Text;
                        }
                        Destroy(clonePanel);
                    }

                    if (nameText != null)
                    {
                        nameText.text = SkillEntries[i].Label;
                    }
                    if (addText != null)
                    {
                        addText.text = string.Empty;
                    }

                    DailyAutomationSkillRow row = clone.AddComponent<DailyAutomationSkillRow>();
                    row.Init(valueText, SkillEntries[i].TalentId);

                    foreach (Button button in clone.GetComponentsInChildren<Button>(true))
                    {
                        Text label = button.GetComponentInChildren<Text>(true);
                        bool minus = label != null && label.text.TrimStart().StartsWith("-");
                        button.onClick.RemoveAllListeners();
                        int delta = minus ? -1 : 1;
                        button.onClick.AddListener(delegate { row.Adjust(delta); });
                        if (label != null)
                        {
                            label.text = minus ? "-1" : "+1";
                        }
                    }

                    _createdSkillRows[i] = clone;
                }

                Log("F9 测试面板技能行已创建：" + SkillEntries[0].Label + " / " + SkillEntries[1].Label);
            }
            catch (Exception ex)
            {
                Logger.LogError("F9 测试面板技能行创建失败：" + ex);
            }
        }

        // ---------- F1「活俠傳作弊測試選單」（DebugMenu.dll，独立小窗口）的下拉列表扩展 ----------
        // 该插件可能没有安装：找不到 DebugMenu.Plugin 类型时静默跳过，不打补丁、不报错。

        private static FieldInfo _debugMenuItemsField;
        private static FieldInfo _debugMenuStatTypesField;
        private static FieldInfo _debugMenuSelectedField;
        private static FieldInfo _debugMenuCurrentValueField;

        private void TryPatchDebugMenuSkills()
        {
            Type pluginType = AccessTools.TypeByName("DebugMenu.Plugin");
            if (pluginType == null)
            {
                Log("未安装“活俠傳作弊測試選單”（F1 菜单），跳过其下拉列表技能扩展。");
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
                Log("F1 作弊选单技能扩展已应用：下拉列表末尾会追加“★医术 / ★战术”。");
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
                    statTypes[statTypes.Length - 1] == SkillEntries[SkillEntries.Length - 1].Sentinel)
                {
                    return; // 已经扩展过
                }

                string[] newItems = new string[items.Length + SkillEntries.Length];
                Array.Copy(items, newItems, items.Length);
                GameStatType[] newTypes = new GameStatType[statTypes.Length + SkillEntries.Length];
                Array.Copy(statTypes, newTypes, statTypes.Length);
                for (int i = 0; i < SkillEntries.Length; i++)
                {
                    newItems[items.Length + i] = SkillEntries[i].Label;
                    newTypes[statTypes.Length + i] = SkillEntries[i].Sentinel;
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

                SkillEntry entry = FindSkillEntry(statTypes[selected]);
                if (entry == null)
                {
                    return true; // 普通属性走原逻辑
                }

                PlayerTalentData talent = SkillTalentUtil.Get(entry.TalentId);
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
            SkillEntry entry = FindSkillEntry(__0);
            if (entry == null)
            {
                return true;
            }

            try
            {
                int oldLevel = -1;
                PlayerTalentData talent = SkillTalentUtil.Get(entry.TalentId);
                if (talent != null)
                {
                    oldLevel = talent.Level;
                }

                int newLevel = SkillTalentUtil.Adjust(entry.TalentId, __1 - oldLevel);
                if (_instance != null && newLevel >= 0)
                {
                    _instance.Log("作弊选单技能：" + entry.Label.TrimStart('★') + " 等级 " +
                        oldLevel + " → " + newLevel + "（输入 " + __1 + "）");
                }
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

        private static SkillEntry FindSkillEntry(GameStatType type)
        {
            for (int i = 0; i < SkillEntries.Length; i++)
            {
                if (SkillEntries[i].Sentinel == type)
                {
                    return SkillEntries[i];
                }
            }

            return null;
        }
    }
}
