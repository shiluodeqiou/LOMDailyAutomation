using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mortal.Core;
using Mortal.Free;
using UnityEngine;
using UnityEngine.UI;

namespace LOMDailyAutomation
{
    // 面板自动化：武学面板（功法修炼）、毒药/兵器升级面板（UpgradePanel 外层 + DevelopPanel 子面板），
    // 以及“全部加满 → 弟子房休息 → 下一天”的闭环。
    public sealed partial class DailyAutomationPlugin
    {
        private ConfigEntry<string> _focusStat;
        private ConfigEntry<string> _nightOptionOrder;

        private MartialPanel _openMartialPanel;
        private UpgradePanel _openUpgradePanel;
        private bool _dayEndRequested;
        private float _nextPanelActionAt;
        private int _martialPanelAttempts;

        private static readonly UpgradeItemType[] PoisonItemTypes =
        {
            UpgradeItemType.毒術,
            UpgradeItemType.煉丹
        };

        private static readonly UpgradeItemType[] WeaponItemTypes =
        {
            UpgradeItemType.平射,
            UpgradeItemType.拋射,
            UpgradeItemType.陷阱,
            UpgradeItemType.武器,
            UpgradeItemType.防具
        };

        // 武学面板“关注目标”可选列表（属性与武功类别）。
        private static readonly GameStatType[] FocusStatChoices =
        {
            GameStatType.體力,
            GameStatType.內力,
            GameStatType.輕功,
            GameStatType.魅力,
            GameStatType.學問,
            GameStatType.心理衛生,
            GameStatType.性情,
            GameStatType.處世,
            GameStatType.修養,
            GameStatType.道德,
            GameStatType.嘴力,
            GameStatType.鍛造,
            GameStatType.毒藥,
            GameStatType.武功刀劍,
            GameStatType.武功暗器,
            GameStatType.武功拳掌,
            GameStatType.武功腿法,
            GameStatType.武功奇門,
            GameStatType.武功軟兵器,
            GameStatType.武功槍棍,
            GameStatType.武功內功
        };

        private const string MedicalFocusValue = "医术";

        private static FieldInfo _martialStatRefStatField;
        private static FieldInfo _martialStatRefTalentField;
        private static FieldInfo _upgradePointDataField;
        private static FieldInfo _upgradeMoneyDataField;
        private static FieldInfo _upgradeCurrentItemField;
        private static MethodInfo _martialPanelCloseMethod;
        private static MethodInfo _upgradePanelCloseMethod;

        private void InitPanelAutomation()
        {
            _focusStat = Config.Bind(
                "DailyAutomation",
                "FocusStat",
                "医术",
                "功法修炼的优先目标（GameStatType 名称，如 医术、輕功、武功刀劍）。武学面板会自动修炼能提升该目标的功法；留空表示不限制。");
            _nightOptionOrder = Config.Bind(
                "DailyAutomation",
                "NightOptionOrder",
                "折纸,休息,饮食,赌博",
                "弟子房睡觉后续菜单 [休息/饮食/折纸/赌博] 的优先顺序（按对“心性”的收益从高到低，逗号分隔）。");

            _martialStatRefStatField = typeof(MartialStatReference).GetField(
                "_stat",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _martialStatRefTalentField = typeof(MartialStatReference).GetField(
                "_talent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _upgradePointDataField = typeof(UpgradePanel).GetField(
                "_pointData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _upgradeMoneyDataField = typeof(UpgradePanel).GetField(
                "_moneyData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _upgradeCurrentItemField = typeof(UpgradePanel).GetField(
                "_currentSelectedItem",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _martialPanelCloseMethod = AccessTools.Method(typeof(MartialPanel), "CloseButtonClick");
            _upgradePanelCloseMethod = AccessTools.Method(typeof(UpgradePanel), "CloseButtonClick");

            InitDeathReload();
        }

        private void OnMartialPanelOpened(MartialPanel panel)
        {
            if (_running && panel != null)
            {
                _openMartialPanel = panel;
                _martialPanelAttempts = 0;
                _nextPanelActionAt = Time.unscaledTime + 0.8f;
                Log("武学面板已打开，开始自动修炼。");
            }
        }

        private void OnUpgradePanelOpened(UpgradePanel panel)
        {
            if (_running && panel != null)
            {
                _openUpgradePanel = panel;
                _nextPanelActionAt = Time.unscaledTime + 0.3f;
                Log("升级面板已打开，开始自动加点。");
            }
        }

        // ---------- 暴死自动回档（0.5.0） ----------

        private ConfigEntry<bool> _autoReloadOnDeath;
        private string _lastReloadCause;
        private int _reloadStreak;
        private bool _reloadPending;
        private float _reloadAt;

        private void InitDeathReload()
        {
            _autoReloadOnDeath = Config.Bind(
                "DailyAutomation",
                "AutoReloadOnDeath",
                true,
                "遇到暴死/结局时自动读取自动存档回到上一操作前并继续（基于游戏自身的自动存档 auto_free/auto/auto_battle）。同一处连续自动回档 3 次会停止自动化，避免固定死局无限重试。");
        }

        private void OnGameOverShown()
        {
            if (_running && !_reloadPending)
            {
                RequestDeathReload("暴死（GameOver 场景）");
            }
        }

        private void OnEndingShown(string endingId)
        {
            if (_running && !_reloadPending)
            {
                RequestDeathReload("结局/暴死：" + (string.IsNullOrEmpty(endingId) ? "未知" : endingId));
            }
        }

        private void RequestDeathReload(string cause)
        {
            if (_autoReloadOnDeath == null || !_autoReloadOnDeath.Value)
            {
                Stop("检测到" + cause + "（自动回档已关闭）");
                return;
            }

            if (string.Equals(_lastReloadCause, cause, StringComparison.Ordinal))
            {
                _reloadStreak++;
            }
            else
            {
                _lastReloadCause = cause;
                _reloadStreak = 1;
            }

            if (_reloadStreak > 3)
            {
                Stop("同一处连续暴死 " + _reloadStreak + " 次（" + cause + "），已停止自动化，请手动处理");
                return;
            }

            _reloadPending = true;
            _reloadAt = Time.unscaledTime + 1.0f;
            _status = "检测到" + cause + "，准备自动回档";
            Log("检测到" + cause + "，将在 1 秒后自动读取自动存档（第 " + _reloadStreak + " 次）。");
        }

        private void TickDeathReload()
        {
            if (!_reloadPending || Time.unscaledTime < _reloadAt)
            {
                return;
            }

            _reloadPending = false;
            try
            {
                SaveSystem save = SaveSystem.Instance;
                if (save == null)
                {
                    Stop("自动回档失败：SaveSystem 不可用");
                    return;
                }

                string used = null;
                string[] candidates = { "auto_free", "auto", "auto_battle" };
                for (int i = 0; i < candidates.Length; i++)
                {
                    AutoGameSave data = null;
                    try
                    {
                        data = save.GetAutoSaveData(candidates[i]);
                    }
                    catch
                    {
                    }

                    if (data != null && data.GameSave != null)
                    {
                        used = candidates[i];
                        break;
                    }
                }

                if (used != null)
                {
                    save.AutoLoadGameData(used);
                    Log("自动回档：已读取 " + used + " 自动存档，回到上一操作前。");
                }
                else
                {
                    save.LoadGameData();
                    Log("自动回档：未找到自动存档，改用游戏读档入口（手动存档槽）。");
                }

                try
                {
                    MissionManagerData missions = MissionManagerData.Instance;
                    if (missions != null)
                    {
                        missions.UpdateCheckMissions();
                    }
                }
                catch
                {
                }

                try
                {
                    SceneController scenes = SceneController.Instance;
                    if (scenes != null)
                    {
                        scenes.LoadCurrentScene();
                    }
                }
                catch
                {
                }

                _awaitingAction = false;
                _storyInProgress = true;
                _lastProgressAt = Time.unscaledTime;
                _nextAttemptAt = Time.unscaledTime + 1f;
                _status = "已自动回档，继续日常";
            }
            catch (Exception ex)
            {
                Logger.LogError("自动回档失败：" + ex);
                Stop("自动回档失败，已停止自动化");
            }
        }

        [HarmonyPatch(typeof(GameOverController), "Awake")]
        private static class GameOverAwakePatch
        {
            private static void Postfix()
            {
                if (_instance != null)
                {
                    _instance.OnGameOverShown();
                }
            }
        }

        [HarmonyPatch(typeof(EndGamePanel), "Open")]
        private static class EndGameOpenPatch
        {
            private static void Prefix(string __0)
            {
                if (_instance != null)
                {
                    _instance.OnEndingShown(__0);
                }
            }
        }
        private void TickPanels()
        {
            if (!_running)
            {
                return;
            }

            TickDeathReload();

            if (_openMartialPanel != null)
            {
                if (Time.unscaledTime < _nextPanelActionAt)
                {
                    return;
                }

                MartialPanel panel = _openMartialPanel;
                _openMartialPanel = null;
                int beforePoints = ReadStat(GameStatType.武學點數) == null ? 0 : ReadStat(GameStatType.武學點數).FinalValue;
                int martialActions = 0;
                try
                {
                    martialActions = DriveMartialPanel(panel);
                }
                catch (Exception ex)
                {
                    Logger.LogError("武学面板自动修炼失败：" + ex);
                }

                if (martialActions == 0 && beforePoints > 0 && _martialPanelAttempts < 8)
                {
                    _martialPanelAttempts++;
                    _openMartialPanel = panel;
                    _nextPanelActionAt = Time.unscaledTime + 0.5f;
                    Log("武学面板尚未准备好或暂时没有可用候选，重试第 " + _martialPanelAttempts + " 次。");
                    return;
                }

                ClosePanel(panel, _martialPanelCloseMethod, "武学面板");
                MarkPanelFlowWaiting();
                return;
            }

            if (_openUpgradePanel != null)
            {
                if (Time.unscaledTime < _nextPanelActionAt)
                {
                    return;
                }

                UpgradePanel panel = _openUpgradePanel;
                _openUpgradePanel = null;
                try
                {
                    DriveUpgradePanel(panel);
                }
                catch (Exception ex)
                {
                    Logger.LogError("升级面板自动加点失败：" + ex);
                }

                ClosePanel(panel, _upgradePanelCloseMethod, "升级面板");
                MarkPanelFlowWaiting();
            }
        }

        private void MarkPanelFlowWaiting()
        {
            _awaitingAction = false;
            _storyInProgress = true;
            _lastProgressAt = Time.unscaledTime;
            _nextAttemptAt = Time.unscaledTime + 0.35f;
            _status = "面板处理完成，等待返回自由行动";
            Log("面板处理完成，等待返回自由行动。");
        }

        private MartialPanel FindOpenMartialPanel()
        {
            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(typeof(MartialPanel));
                for (int i = 0; i < objects.Length; i++)
                {
                    MartialPanel panel = objects[i] as MartialPanel;
                    if (panel != null && panel.isActiveAndEnabled && panel.gameObject.activeInHierarchy)
                    {
                        return panel;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("查找武学面板失败：" + ex.Message);
            }

            return null;
        }

        private UpgradePanel FindOpenUpgradePanel()
        {
            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(typeof(UpgradePanel));
                for (int i = 0; i < objects.Length; i++)
                {
                    UpgradePanel panel = objects[i] as UpgradePanel;
                    if (panel != null && panel.isActiveAndEnabled && panel.gameObject.activeInHierarchy)
                    {
                        return panel;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("查找升级面板失败：" + ex.Message);
            }

            return null;
        }

        private void ClosePanel(Component panel, MethodInfo closeMethod, string label)
        {
            try
            {
                if (closeMethod != null)
                {
                    closeMethod.Invoke(panel, null);
                }
                else
                {
                    Logger.LogWarning(label + "缺少关闭方法，跳过关闭。");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(label + "关闭失败：" + ex.Message);
            }
        }

        private GameStat ReadStat(GameStatType type)
        {
            try
            {
                PlayerStatManagerData stats = PlayerStatManagerData.Instance;
                return stats == null ? null : stats.Stats.Get(type);
            }
            catch
            {
                return null;
            }
        }

        // ---------- 武学面板 ----------

        private int DriveMartialPanel(MartialPanel panel)
        {
            GameStat martialPoint = ReadStat(GameStatType.武學點數);
            if (martialPoint == null)
            {
                Logger.LogWarning("读取武学点数失败，跳过武学面板。");
                return 0;
            }

            GameStatType focusType;
            bool hasFocus = TryGetFocusStat(out focusType);
            bool medicalFocus = IsMedicalFocus();
            if (medicalFocus)
            {
                hasFocus = true;
            }

            Log("武学面板开始：可用点数=" + martialPoint.FinalValue +
                "，目标=" + (hasFocus ? GetFocusStatLabel() : "未设置"));

            int actions = 0;
            bool firstPassLogged = false;
            bool prereqLogged = false;
            while (actions < 300)
            {
                int available = martialPoint.FinalValue;
                if (available <= 0)
                {
                    break;
                }

                // 目标功法被前置条件卡住时，收集"缺什么属性"，优先修炼能补这些属性的功法。
                List<FocusPrerequisite> focusPrerequisites = medicalFocus || hasFocus
                    ? CollectFocusPrerequisites(focusType, medicalFocus)
                    : NoFocusPrerequisites;
                if (focusPrerequisites.Count > 0 && !prereqLogged)
                {
                    prereqLogged = true;
                    LogFocusPrerequisites(focusPrerequisites);
                }

                Book best = null;
                int bestNeed = int.MaxValue;
                int bestFocusLevel = int.MaxValue;
                int bestRank = int.MaxValue;
                int bestLevelKey = 0;
                bool bestIsFocus = false;
                bool bestIsPrereq = false;
                string bestPrereqLabel = null;
                string candidates = string.Empty;
                foreach (Book book in EnumerateOwnedMartialBooks())
                {
                    string name = book.GetNameKey();
                    if (book.IsMaxLevel)
                    {
                        candidates += " | " + name + "(满级)";
                        continue;
                    }

                    MartialLevelData data;
                    try
                    {
                        data = book.GetCurrentMartialData();
                    }
                    catch
                    {
                        candidates += " | " + name + "(数据异常)";
                        continue;
                    }

                    if (data == null)
                    {
                        candidates += " | " + name + "(无当前级数据)";
                        continue;
                    }

                    if (!data.Check())
                    {
                        candidates += " | " + name + "(条件不满足)";
                        continue;
                    }

                    int need = book.GetNextLevelRequirePoints();
                    if (need <= 0)
                    {
                        // 少数功法当前级需求可能为 0（数据特殊），逐点投入以安全推进、避免浪费。
                        need = 1;
                    }

                    // 目标判定覆盖当前级及以后的整本书：多数功法（尤其医书）把目标加成放在满级那一级，
                    // 只看当前级会漏判，导致医书不被优先。
                    int focusLevel = medicalFocus || hasFocus
                        ? FindFocusUpgradeLevel(book, focusType, medicalFocus)
                        : -1;
                    bool isFocus = focusLevel >= 0;
                    string focusTag = string.Empty;
                    if (isFocus)
                    {
                        focusTag = focusLevel == book.CurrentMartialLevel
                            ? " 目标"
                            : " 目标@第" + (focusLevel + 1) + "级";
                    }

                    // 非目标书：若它能提升目标功法缺失的前置属性，作为"前置辅助"次优先修炼。
                    int prereqLevel = -1;
                    string prereqLabel = null;
                    if (!isFocus && focusPrerequisites.Count > 0)
                    {
                        prereqLevel = FindPrerequisiteUpgradeLevel(book, focusPrerequisites, out prereqLabel);
                    }

                    bool isPrereq = prereqLevel >= 0;
                    int rank = isFocus ? 0 : (isPrereq ? 1 : 2);
                    int levelKey = isFocus ? focusLevel : (isPrereq ? prereqLevel : 0);
                    string tag = focusTag;
                    if (!isFocus && isPrereq)
                    {
                        tag = " 助前置";
                    }

                    candidates += " | " + name + "(可点 需求=" + need + tag + ")";
                    bool better;
                    if (best == null)
                    {
                        better = true;
                    }
                    else if (rank != bestRank)
                    {
                        better = rank < bestRank;
                    }
                    else if (rank < 2)
                    {
                        better = levelKey < bestLevelKey ||
                            levelKey == bestLevelKey && need < bestNeed;
                    }
                    else
                    {
                        better = need < bestNeed;
                    }

                    if (!better)
                    {
                        continue;
                    }

                    best = book;
                    bestNeed = need;
                    bestFocusLevel = focusLevel;
                    bestRank = rank;
                    bestLevelKey = levelKey;
                    bestIsFocus = isFocus;
                    bestIsPrereq = isPrereq;
                    bestPrereqLabel = prereqLabel;
                }

                if (!firstPassLogged)
                {
                    firstPassLogged = true;
                    Log("武学候选：" + (candidates.Length > 0 ? candidates : "（无可枚举功法）"));
                }

                if (best == null)
                {
                    break;
                }

                int invest = Math.Min(bestNeed, available);
                string focusNote = bestIsFocus
                    ? (bestFocusLevel > best.CurrentMartialLevel
                        ? "（目标功法·到第" + (bestFocusLevel + 1) + "级获得）"
                        : "（目标功法）")
                    : bestIsPrereq
                        ? "（前置功法·提升" + (bestPrereqLabel ?? "前置属性") + "）"
                        : string.Empty;
                bool leveled;
                try
                {
                    leveled = best.Learn(invest);
                    martialPoint.AddValue(-invest, true);
                }
                catch (Exception ex)
                {
                    Logger.LogError("学习功法失败：" + ex);
                    break;
                }

                actions++;
                Log("武学修炼：" + best.GetNameKey() + " 投入 " + invest + " 点" +
                    (leveled ? "（升级！）" : "") + focusNote);
            }

            Log("武学面板处理完成，共投入 " + actions + " 次，剩余武学点数=" + martialPoint.FinalValue +
                (hasFocus ? "，目标=" + GetFocusStatLabel() : "，未设置目标"));
            return actions;
        }

        private IEnumerable<Book> EnumerateOwnedMartialBooks()
        {
            List<Book> result = new List<Book>();
            try
            {
                ItemDatabase database = ItemDatabase.Instance;
                if (database == null || database.Books == null || database.Books.List == null)
                {
                    return result;
                }

                List<ItemData> items = database.Books.List;
                for (int i = 0; i < items.Count; i++)
                {
                    Book book = items[i] as Book;
                    if (book == null || !book.IsMartial || book.CurrentCount <= 0)
                    {
                        continue;
                    }

                    result.Add(book);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("枚举功法书失败：" + ex.Message);
            }

            return result;
        }

        // 在“当前级及以后”的等级数据里找最早能提升目标的一级；返回等级下标，找不到返回 -1。
        // 书的升级逐级进行、已过去的等级不会再给加成，所以从当前级开始向后找。
        private int FindFocusUpgradeLevel(Book book, GameStatType focusType, bool medical)
        {
            if (book == null)
            {
                return -1;
            }

            MartialLevelData[] all;
            try
            {
                all = book.LevelData;
            }
            catch
            {
                return -1;
            }

            if (all == null || all.Length == 0)
            {
                return -1;
            }

            int from = book.CurrentMartialLevel;
            if (from < 0)
            {
                from = 0;
            }

            for (int i = from; i < all.Length; i++)
            {
                bool match = medical
                    ? LevelRaisesMedical(all[i])
                    : LevelRaisesFocusStat(all[i], focusType);
                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool LevelRaisesFocusStat(MartialLevelData data, GameStatType focusType)
        {
            if (data == null || data.LevelUpStat == null || _martialStatRefStatField == null)
            {
                return false;
            }

            List<MartialModifyStat> stats = data.LevelUpStat;
            for (int i = 0; i < stats.Count; i++)
            {
                MartialModifyStat modify = stats[i];
                if (modify == null || modify.Stat == null)
                {
                    continue;
                }

                if (modify.Stat.StatType != MartialLevelupStatType.角色數值)
                {
                    continue;
                }

                try
                {
                    GameStat stat = _martialStatRefStatField.GetValue(modify.Stat) as GameStat;
                    if (stat != null && stat.StatType == focusType)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool LevelRaisesMedical(MartialLevelData data)
        {
            if (data == null)
            {
                return false;
            }

            if (ModifyStatsRaiseMedical(data.LevelUpStat))
            {
                return true;
            }

            List<MartialLearnConsumable> consumables = data.ConsumableList;
            if (consumables != null)
            {
                for (int i = 0; i < consumables.Count; i++)
                {
                    MartialLearnConsumable consumable = consumables[i];
                    if (consumable != null && ModifyStatsRaiseMedical(consumable.ConsumableList))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool ModifyStatsRaiseMedical(List<MartialModifyStat> stats)
        {
            if (stats == null)
            {
                return false;
            }

            for (int i = 0; i < stats.Count; i++)
            {
                MartialModifyStat modify = stats[i];
                MartialStatReference reference = modify == null ? null : modify.Stat;
                if (reference == null)
                {
                    continue;
                }

                try
                {
                    string nameKey = reference.GetNameKey() ?? string.Empty;
                    if (nameKey.IndexOf("G002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nameKey.IndexOf("医术", StringComparison.Ordinal) >= 0 ||
                        nameKey.IndexOf("醫術", StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }

                if (_martialStatRefTalentField == null ||
                    reference.StatType != MartialLevelupStatType.天賦)
                {
                    continue;
                }

                try
                {
                    PlayerTalentData talent = _martialStatRefTalentField.GetValue(reference) as PlayerTalentData;
                    if (talent == null || !talent.CombatSkill)
                    {
                        continue;
                    }

                    string combatKey = talent.CombatSkillKey ?? string.Empty;
                    if (combatKey.IndexOf("G002", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        // ---------- 目标功法前置缺口（0.5.3） ----------

        private sealed class FocusPrerequisite
        {
            public string Key;
            public string Label;
            public bool NeedHigher;
            public bool NeedLower;
            public int RequiredValue;
            public int CurrentValue;
        }

        private static readonly List<FocusPrerequisite> NoFocusPrerequisites = new List<FocusPrerequisite>();

        // 从所有“目标功法”里收集当前（或后续等级）被前置卡住的属性缺口。
        private List<FocusPrerequisite> CollectFocusPrerequisites(GameStatType focusType, bool medicalFocus)
        {
            List<FocusPrerequisite> result = new List<FocusPrerequisite>();
            foreach (Book book in EnumerateOwnedMartialBooks())
            {
                if (book.IsMaxLevel)
                {
                    continue;
                }

                if (FindFocusUpgradeLevel(book, focusType, medicalFocus) < 0)
                {
                    continue;
                }

                MartialLevelData[] all;
                try
                {
                    all = book.LevelData;
                }
                catch
                {
                    continue;
                }

                if (all == null)
                {
                    continue;
                }

                // 找当前级及以后的第一个不满足的等级（可能是当前级，也可能是若干级之后的门槛）。
                for (int i = Math.Max(0, book.CurrentMartialLevel); i < all.Length; i++)
                {
                    MartialLevelData level = all[i];
                    if (level == null)
                    {
                        continue;
                    }

                    bool blocked;
                    try
                    {
                        blocked = !level.Check();
                    }
                    catch
                    {
                        blocked = false;
                    }

                    if (!blocked)
                    {
                        continue;
                    }

                    CollectFailingItems(level, result);
                    break;
                }
            }

            return result;
        }

        private void CollectFailingItems(MartialLevelData level, List<FocusPrerequisite> result)
        {
            MartialLearnCondition condition = level.Condition;
            if (condition == null || condition.List == null)
            {
                return;
            }

            for (int g = 0; g < condition.List.Count; g++)
            {
                MartialLearnCompareGroup group = condition.List[g];
                if (group == null || group.ItemList == null)
                {
                    continue;
                }

                for (int i = 0; i < group.ItemList.Length; i++)
                {
                    MartialLearnCompareItem item = group.ItemList[i];
                    if (item == null || item.Stat == null)
                    {
                        continue;
                    }

                    bool fails;
                    try
                    {
                        fails = !item.Check();
                    }
                    catch
                    {
                        fails = false;
                    }

                    if (!fails)
                    {
                        continue;
                    }

                    string key = SafeGetNameKey(item.Stat);
                    if (key.Length == 0 || ContainsPrerequisite(result, key))
                    {
                        continue;
                    }

                    int current = 0;
                    try
                    {
                        current = item.Stat.GetValue();
                    }
                    catch
                    {
                    }

                    StatCompareType op = item.CompareOp;
                    bool needHigher = op == StatCompareType.Greater || op == StatCompareType.GreaterEqual ||
                        op == StatCompareType.NotEqual || (op == StatCompareType.Equal && current < item.Value);
                    bool needLower = op == StatCompareType.Less || op == StatCompareType.LessEqual ||
                        op == StatCompareType.NotEqual || (op == StatCompareType.Equal && current > item.Value);

                    result.Add(new FocusPrerequisite
                    {
                        Key = key,
                        Label = StatReferenceLabel(item.Stat, key),
                        NeedHigher = needHigher,
                        NeedLower = needLower,
                        RequiredValue = item.Value,
                        CurrentValue = current,
                    });
                }
            }
        }

        private void LogFocusPrerequisites(List<FocusPrerequisite> prerequisites)
        {
            string detail = string.Empty;
            for (int i = 0; i < prerequisites.Count; i++)
            {
                FocusPrerequisite prerequisite = prerequisites[i];
                if (detail.Length > 0)
                {
                    detail += "、";
                }

                detail += prerequisite.Label + "（当前 " + prerequisite.CurrentValue + "，需要 " +
                    (prerequisite.NeedLower && !prerequisite.NeedHigher ? "≤" : "≥") +
                    prerequisite.RequiredValue + "）";
            }

            bool anyHelper = false;
            foreach (Book book in EnumerateOwnedMartialBooks())
            {
                if (book.IsMaxLevel)
                {
                    continue;
                }

                MartialLevelData data;
                try
                {
                    data = book.GetCurrentMartialData();
                }
                catch
                {
                    continue;
                }

                if (data == null)
                {
                    continue;
                }

                bool learnable;
                try
                {
                    learnable = data.Check();
                }
                catch
                {
                    learnable = false;
                }

                if (!learnable)
                {
                    continue;
                }

                string ignored;
                if (FindPrerequisiteUpgradeLevel(book, prerequisites, out ignored) >= 0)
                {
                    anyHelper = true;
                    break;
                }
            }

            Log("目标功法前置未满足：" + detail +
                (anyHelper ? "；将优先修炼能提升前置的功法。" : "；未拥有可提升这些属性且当前可修的功法。"));
        }

        private int FindPrerequisiteUpgradeLevel(Book book, List<FocusPrerequisite> prerequisites, out string matchedLabel)
        {
            matchedLabel = null;
            if (book == null || prerequisites == null || prerequisites.Count == 0)
            {
                return -1;
            }

            MartialLevelData[] all;
            try
            {
                all = book.LevelData;
            }
            catch
            {
                return -1;
            }

            if (all == null || all.Length == 0)
            {
                return -1;
            }

            int from = Math.Max(0, book.CurrentMartialLevel);
            for (int i = from; i < all.Length; i++)
            {
                if (LevelGrantsPrerequisite(all[i], prerequisites, out matchedLabel))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool LevelGrantsPrerequisite(MartialLevelData data, List<FocusPrerequisite> prerequisites, out string matchedLabel)
        {
            matchedLabel = null;
            if (data == null)
            {
                return false;
            }

            if (ModifyStatsGrantPrerequisite(data.LevelUpStat, prerequisites, out matchedLabel))
            {
                return true;
            }

            List<MartialLearnConsumable> consumables = data.ConsumableList;
            if (consumables != null)
            {
                for (int i = 0; i < consumables.Count; i++)
                {
                    MartialLearnConsumable consumable = consumables[i];
                    if (consumable != null &&
                        ModifyStatsGrantPrerequisite(consumable.ConsumableList, prerequisites, out matchedLabel))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool ModifyStatsGrantPrerequisite(List<MartialModifyStat> stats, List<FocusPrerequisite> prerequisites, out string matchedLabel)
        {
            matchedLabel = null;
            if (stats == null)
            {
                return false;
            }

            for (int i = 0; i < stats.Count; i++)
            {
                MartialModifyStat modify = stats[i];
                MartialStatReference reference = modify == null ? null : modify.Stat;
                if (reference == null)
                {
                    continue;
                }

                string key = SafeGetNameKey(reference);
                if (key.Length == 0)
                {
                    continue;
                }

                for (int j = 0; j < prerequisites.Count; j++)
                {
                    FocusPrerequisite prerequisite = prerequisites[j];
                    if (prerequisite.Key != key || !ModifyStatHelps(modify, prerequisite))
                    {
                        continue;
                    }

                    matchedLabel = prerequisite.Label;
                    return true;
                }
            }

            return false;
        }

        private static bool ModifyStatHelps(MartialModifyStat modify, FocusPrerequisite prerequisite)
        {
            int rawValue = modify.ModifyValue;
            if (!modify.Center)
            {
                return prerequisite.NeedHigher && rawValue > 0 ||
                    prerequisite.NeedLower && rawValue < 0;
            }

            // _center：数值向 50 靠拢，最多 |_value| 点，方向由当前值决定。
            int current;
            try
            {
                current = modify.GetValue();
            }
            catch
            {
                return false;
            }

            if (current < 50)
            {
                return prerequisite.NeedHigher;
            }

            if (current > 50)
            {
                return prerequisite.NeedLower;
            }

            return false;
        }

        private static bool ContainsPrerequisite(List<FocusPrerequisite> list, string key)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Key == key)
                {
                    return true;
                }
            }

            return false;
        }

        private static string SafeGetNameKey(MartialStatReference reference)
        {
            try
            {
                return reference.GetNameKey() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string StatReferenceLabel(MartialStatReference reference, string key)
        {
            if (reference.StatType == MartialLevelupStatType.角色數值 && _martialStatRefStatField != null)
            {
                try
                {
                    GameStat stat = _martialStatRefStatField.GetValue(reference) as GameStat;
                    if (stat != null)
                    {
                        return stat.StatType.ToString();
                    }
                }
                catch
                {
                }
            }

            return key;
        }

        private bool TryGetFocusStat(out GameStatType focusType)
        {
            focusType = GameStatType.體力;
            if (_focusStat == null)
            {
                return false;
            }

            string value = (_focusStat.Value ?? string.Empty).Trim();
            if (value.Length == 0 || value == "無" || value == "无")
            {
                return false;
            }

            return Enum.TryParse(value, out focusType);
        }

        private bool IsMedicalFocus()
        {
            if (_focusStat == null)
            {
                return false;
            }

            string value = (_focusStat.Value ?? string.Empty).Trim();
            return value == MedicalFocusValue || value == "醫術" ||
                value.Equals("medical", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("medicine", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("G002", StringComparison.OrdinalIgnoreCase);
        }

        private string GetFocusStatLabel()
        {
            if (IsMedicalFocus())
            {
                return MedicalFocusValue;
            }

            GameStatType focusType;
            return TryGetFocusStat(out focusType) ? focusType.ToString() : "未设置";
        }

        // ---------- 毒药 / 兵器升级面板（UpgradePanel 外层 + DevelopPanel 子面板） ----------

        private void DriveUpgradePanel(UpgradePanel panel)
        {
            GameStat pointStat = ReadPanelStat(panel, _upgradePointDataField, "升级点数");
            GameStat moneyStat = ReadPanelStat(panel, _upgradeMoneyDataField, "银两");
            if (pointStat == null)
            {
                Logger.LogWarning("读取升级点数失败，跳过升级面板。");
                return;
            }

            bool poisonPanel = IsPoisonPanel(panel, pointStat);
            UpgradeItemType[] types = poisonPanel ? PoisonItemTypes : WeaponItemTypes;
            Log("升级面板归属：" + (poisonPanel ? "毒药" : "兵器") + "，点数池=" + pointStat.StatType);
            ProcessUpgradeFamily(types, pointStat, moneyStat, poisonPanel ? "毒药" : "兵器");
        }

        private bool IsPoisonPanel(UpgradePanel panel, GameStat pointStat)
        {
            // 优先按当前选中的升级项判断。
            try
            {
                object selected = _upgradeCurrentItemField == null
                    ? null
                    : _upgradeCurrentItemField.GetValue(panel);
                UpgradeItemButton button = selected as UpgradeItemButton;
                if (button != null && button.Data != null)
                {
                    return IsPoisonType(button.Data.ItemType);
                }
            }
            catch
            {
            }

            // 退化判断：按面板绑定的点数池类型。
            GameStatType type = pointStat.StatType;
            return type == GameStatType.毒藥 || type == GameStatType.全毒藥點數;
        }

        private void ProcessUpgradeFamily(UpgradeItemType[] types, GameStat pointStat, GameStat moneyStat, string family)
        {
            int actions = 0;
            while (actions < 400)
            {
                bool acted = false;

                // 1) 点满后进化解锁下一项：消耗累计点数与银两（约90%）后切换到下一项。
                for (int t = 0; t < types.Length && !acted; t++)
                {
                    UpgradeItemData item = GetUpgradeItem(types[t]);
                    if (item == null)
                    {
                        continue;
                    }

                    List<UpgradeDevelopItem> evolutions;
                    try
                    {
                        evolutions = item.GetCurrentEvolutionItems();
                    }
                    catch
                    {
                        continue;
                    }

                    if (evolutions == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < evolutions.Count; i++)
                    {
                        UpgradeDevelopItem entry = evolutions[i];
                        if (entry == null || entry.NextItem == null)
                        {
                            continue;
                        }

                        bool conditions;
                        try
                        {
                            conditions = entry.CheckConditions();
                        }
                        catch
                        {
                            continue;
                        }

                        if (!conditions || item.CurrentLevel < entry.RequiredLevel)
                        {
                            continue;
                        }

                        GameStat sectLevel = ReadStat(GameStatType.門派規模);
                        int requiredSectLevel = entry.NextItem.RequiredSectLevel;
                        if (sectLevel != null && requiredSectLevel > sectLevel.FinalValue)
                        {
                            continue;
                        }

                        int costMoney = item.GetTotalReturnCostMoney();
                        int costPoints = item.GetTotalReturnCostPoints();
                        if (pointStat.FinalValue < costPoints)
                        {
                            continue;
                        }

                        if (moneyStat != null && moneyStat.FinalValue < costMoney)
                        {
                            continue;
                        }

                        try
                        {
                            if (moneyStat != null)
                            {
                                moneyStat.AddValue(-costMoney, false);
                            }

                            pointStat.AddValue(-costPoints, false);
                            item.ResetDevelop();
                            PlayerStatManagerData manager = PlayerStatManagerData.Instance;
                            manager.SetCurrentUpgradeItem(types[t], entry.NextItem.Key);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("进化升级项失败：" + ex);
                            continue;
                        }

                        actions++;
                        acted = true;
                        Log("升级面板：已进化 " + types[t] + " → " + entry.NextItem.Key +
                            "（消耗点数 " + costPoints + "，银两 " + costMoney + "）");
                        break;
                    }
                }

                if (acted)
                {
                    continue;
                }

                // 2) 常规加点：优先投入“下一级需求点数最少”的可加项。
                UpgradeItemData bestItem = null;
                int bestNeed = int.MaxValue;
                for (int t = 0; t < types.Length; t++)
                {
                    UpgradeItemData item = GetUpgradeItem(types[t]);
                    if (item == null || item.IsMaxLevel)
                    {
                        continue;
                    }

                    int need;
                    try
                    {
                        need = item.GetNextLevelRequirePoints();
                    }
                    catch
                    {
                        continue;
                    }

                    if (need <= 0 || need >= bestNeed)
                    {
                        continue;
                    }

                    bestItem = item;
                    bestNeed = need;
                }

                if (bestItem == null)
                {
                    break;
                }

                int available = pointStat.FinalValue;
                if (available <= 0)
                {
                    break;
                }

                int invest = Math.Min(bestNeed, available);
                bool leveled;
                try
                {
                    leveled = bestItem.Upgrade(invest);
                    pointStat.AddValue(-invest, false);
                }
                catch (Exception ex)
                {
                    Logger.LogError("升级投入失败：" + ex);
                    break;
                }

                actions++;
                Log("升级面板：" + bestItem.Key + " 投入 " + invest + " 点" + (leveled ? "（升级！）" : "") +
                    "，剩余点数=" + pointStat.FinalValue);
            }

            Log("升级面板处理完成（" + family + "），共投入 " + actions + " 次。");
        }

        private GameStat ReadPanelStat(Component panel, FieldInfo field, string label)
        {
            if (panel == null || field == null)
            {
                return null;
            }

            try
            {
                return field.GetValue(panel) as GameStat;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取面板" + label + "属性失败：" + ex.Message);
                return null;
            }
        }

        private static bool IsPoisonType(UpgradeItemType type)
        {
            return type == UpgradeItemType.毒術 || type == UpgradeItemType.煉丹;
        }

        private UpgradeItemData GetUpgradeItem(UpgradeItemType type)
        {
            try
            {
                PlayerStatManagerData manager = PlayerStatManagerData.Instance;
                return manager == null ? null : manager.GetUpgradeItem(type);
            }
            catch
            {
                return null;
            }
        }

        // ---------- 通用地点查找 ----------

        private PositionController FindPosition(PositionType wanted)
        {
            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(typeof(PositionController));
                for (int i = 0; i < objects.Length; i++)
                {
                    PositionController controller = objects[i] as PositionController;
                    if (controller == null || ReadPosition(controller) != wanted)
                    {
                        continue;
                    }

                    if (!CallBool(_isActiveStatusMethod, controller) ||
                        !CallBool(_hasActionPointMethod, controller))
                    {
                        continue;
                    }

                    return controller;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("查找地点失败（" + wanted + "）：" + ex.Message);
            }

            return null;
        }

        // ---------- 全部加满判定与结束当天 ----------

        private bool AreAllPanelsExhausted()
        {
            try
            {
                if (!IsMartialExhausted())
                {
                    return false;
                }

                if (!IsDevelopFamilyExhausted(PoisonItemTypes))
                {
                    return false;
                }

                if (!IsDevelopFamilyExhausted(WeaponItemTypes))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("检查加点进度失败：" + ex.Message);
                return false;
            }
        }

        private bool IsMartialExhausted()
        {
            foreach (Book book in EnumerateOwnedMartialBooks())
            {
                if (book.IsMaxLevel)
                {
                    continue;
                }

                MartialLevelData data = book.GetCurrentMartialData();
                if (data == null)
                {
                    continue;
                }

                if (!data.Check())
                {
                    continue;
                }

                // 还有可修炼（哪怕点数不够，靠日常获取后仍可加）→ 未加满。
                return false;
            }

            return true;
        }

        private bool IsDevelopFamilyExhausted(UpgradeItemType[] types)
        {
            for (int t = 0; t < types.Length; t++)
            {
                UpgradeItemData item = GetUpgradeItem(types[t]);
                if (item == null)
                {
                    continue;
                }

                if (!item.IsMaxLevel)
                {
                    return false;
                }

                // 已满级但可能可以进化解锁下一项。
                List<UpgradeDevelopItem> evolutions = item.GetCurrentEvolutionItems();
                if (evolutions != null)
                {
                    for (int i = 0; i < evolutions.Count; i++)
                    {
                        UpgradeDevelopItem entry = evolutions[i];
                        if (entry == null || entry.NextItem == null)
                        {
                            continue;
                        }

                        if (entry.CheckConditions() && item.CurrentLevel >= entry.RequiredLevel)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        // 判断升级面板当前是否至少还有一项能实际投入点数或完成进化。
        // 只检查当前资源，不把“以后可能获得点数”误认为现在可点。
        private bool HasCurrentUpgradeOpportunity(UpgradeItemType[] types, GameStat pointStat)
        {
            if (types == null || pointStat == null || pointStat.FinalValue <= 0)
            {
                return false;
            }

            GameStat moneyStat = ReadStat(GameStatType.銀兩);
            for (int t = 0; t < types.Length; t++)
            {
                UpgradeItemData item = GetUpgradeItem(types[t]);
                if (item == null)
                {
                    continue;
                }

                if (!item.IsMaxLevel)
                {
                    try
                    {
                        if (item.GetNextLevelRequirePoints() > 0)
                        {
                            // Upgrade() 支持先投入部分点数，因此不要求一次攒够整级。
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }

                List<UpgradeDevelopItem> evolutions;
                try
                {
                    evolutions = item.GetCurrentEvolutionItems();
                }
                catch
                {
                    continue;
                }

                if (evolutions == null)
                {
                    continue;
                }

                for (int i = 0; i < evolutions.Count; i++)
                {
                    UpgradeDevelopItem entry = evolutions[i];
                    if (entry == null || entry.NextItem == null || item.CurrentLevel < entry.RequiredLevel)
                    {
                        continue;
                    }

                    bool conditions;
                    try
                    {
                        conditions = entry.CheckConditions();
                    }
                    catch
                    {
                        continue;
                    }

                    if (!conditions)
                    {
                        continue;
                    }

                    GameStat sectLevel = ReadStat(GameStatType.門派規模);
                    if (sectLevel != null && entry.NextItem.RequiredSectLevel > sectLevel.FinalValue)
                    {
                        continue;
                    }

                    int costPoints = item.GetTotalReturnCostPoints();
                    int costMoney = item.GetTotalReturnCostMoney();
                    if (pointStat.FinalValue < costPoints ||
                        (moneyStat != null && moneyStat.FinalValue < costMoney))
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        private bool HasCurrentWeaponUpgradeOpportunity()
        {
            GameStat pointStat = ReadStat(GameStatType.全鍛造點數);
            if (pointStat == null)
            {
                pointStat = ReadStat(GameStatType.鍛造);
            }

            return HasCurrentUpgradeOpportunity(WeaponItemTypes, pointStat);
        }

        // 夜晚菜单（弟子房睡觉后的 [休息/饮食/折纸/赌博]）：按配置顺序选对“心性”收益最高的可用项。
        private bool TryPickNightOption(string[] optionTexts, out int pickIndex)
        {
            pickIndex = -1;
            if (_nightOptionOrder == null)
            {
                return false;
            }

            string[] order = (_nightOptionOrder.Value ?? string.Empty).Split(
                new[] { ',', '，', ';', '；', '|', '\r', '\n', '\t', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            if (order.Length == 0)
            {
                return false;
            }

            for (int rank = 0; rank < order.Length; rank++)
            {
                string wanted = order[rank].Trim();
                if (wanted.Length == 0)
                {
                    continue;
                }

                for (int i = 0; i < optionTexts.Length; i++)
                {
                    if (IsNightOption(optionTexts[i], wanted))
                    {
                        pickIndex = i;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNightOption(string optionText, string label)
        {
            if (string.IsNullOrEmpty(optionText))
            {
                return false;
            }

            string[] parts = optionText.Split('+');
            if (parts.Length < 3)
            {
                return false;
            }

            // 夜晚菜单形如 exit+休息+0+0 / default+饮食+0+0，第二段为中文名。
            return string.Equals(parts[1].Trim(), label, StringComparison.Ordinal);
        }

        // ---------- 面板（F7）设置区 ----------

        private void DrawPanelAutomationSection()
        {
            GUILayout.Label("功法修炼目标（武学面板）：“" + GetFocusStatLabel() + "”");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("切换目标", GUILayout.Width(125f)))
            {
                CycleFocusStat();
            }
            if (GUILayout.Button("清除目标", GUILayout.Width(125f)))
            {
                if (_focusStat != null)
                {
                    _focusStat.Value = "無";
                    Config.Save();
                    _status = "功法目标已清除";
                }
            }
            GUILayout.Label("加满后自动去弟子房休息进下一天", GUILayout.Width(240f));
            GUILayout.EndHorizontal();
        }

        private void CycleFocusStat()
        {
            if (_focusStat == null)
            {
                return;
            }

            GameStatType current;
            bool hasFocus = TryGetFocusStat(out current);
            int index = IsMedicalFocus() ? FocusStatChoices.Length : -1;
            if (hasFocus && index < 0)
            {
                for (int i = 0; i < FocusStatChoices.Length; i++)
                {
                    if (FocusStatChoices[i] == current)
                    {
                        index = i;
                        break;
                    }
                }
            }

            int next = (index + 1) % (FocusStatChoices.Length + 1);
            if (next == FocusStatChoices.Length)
            {
                _focusStat.Value = MedicalFocusValue;
            }
            else
            {
                _focusStat.Value = FocusStatChoices[next].ToString();
            }
            Config.Save();
            _status = "功法目标设为：" + GetFocusStatLabel();
        }

        // ---------- 面板打开补丁 ----------

        [HarmonyPatch(typeof(MartialPanel), "OnPanelOpen")]
        private static class MartialPanelOpenPatch
        {
            private static void Postfix(MartialPanel __instance)
            {
                if (_instance != null)
                {
                    _instance.OnMartialPanelOpened(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(UpgradePanel), "OnPanelOpen")]
        private static class UpgradePanelOpenPatch
        {
            private static void Postfix(UpgradePanel __instance)
            {
                if (_instance != null)
                {
                    _instance.OnUpgradePanelOpened(__instance);
                }
            }
        }
    }
}
