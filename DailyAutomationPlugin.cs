using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Mortal.Battle;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;

namespace LOMDailyAutomation
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class DailyAutomationPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.codex.lom.daily-automation";
        private const string PluginName = "活侠传日常自动化";
        private const string PluginVersion = "2026.10.16";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _windowToggleKey;
        private ConfigEntry<bool> _showWindowAtStartup;
        private ConfigEntry<bool> _alwaysFastForward;
        private ConfigEntry<float> _fastForwardTimeScale;
        private ConfigEntry<bool> _autoWinGroupBattleDuringFastForward;
        private ConfigEntry<string> _actionOrder;
        private ConfigEntry<string> _innerActionPreference;
        private ConfigEntry<float> _timeoutSeconds;
        private ConfigEntry<string> _breakTempleOptionOrder;
        private ConfigEntry<bool> _stopOnMainMission;
        private ConfigEntry<bool> _stopOnSubMission;
        private ConfigEntry<bool> _diagnosticLogging;
        private ConfigEntry<int> _contributionTarget;
        private ConfigEntry<int> _doorOptionCursor;

        private readonly List<PositionType> _positions = new List<PositionType>();
        private static readonly PositionType[] KnownPositions =
        {
            PositionType.正心堂,
            PositionType.校場,
            PositionType.煉丹房,
            PositionType.鍛冶場,
            PositionType.後山,
            PositionType.弟子房,
            PositionType.大門,
            PositionType.講經堂,
            PositionType.伙房,
            PositionType.女弟子房
        };

        private static DailyAutomationPlugin _instance;
        private Harmony _harmony;
        private FieldInfo _positionField;
        private FieldInfo _positionActiveField;
        private MethodInfo _isActiveStatusMethod;
        private MethodInfo _hasActionPointMethod;
        private FieldInfo _storyAutoDialogField;
        private FieldInfo _storySkipDialogField;
        private FieldInfo _storyEnableSkipField;
        private bool _running;
        private bool _awaitingAction;
        private bool _storyInProgress;
        private int _sequenceIndex;
        private int _lastActionCount;
        private float _lastProgressAt;
        private float _nextAttemptAt;
        private float _nextDiagnosticAt;
        private string _status = "未启动";
        private bool _showWindow;
        private Rect _windowRect;
        private Vector2 _sequenceScroll;
        private float _sequenceRowHeight = 24f;
        private int _pendingRemoveIndex = -1;
        private int _pendingMoveFrom = -1;
        private int _pendingMoveTo = -1;
        private Vector2 _panelScroll;
        private bool _fastForwardPausedForUnread;
        private string _pausedUnreadKey;
        private bool _allowPausedUnreadOnce;
        private bool _resumeDailyAfterMenu;
        private bool _manualMenuSelected;
        private int _selectedSequenceIndex = -1;
        private bool _storyStateCaptured;
        private bool _previousAutoDialog;
        private bool _previousSkipDialog;
        private bool _previousEnableSkip;
        private float _previousTimeScale = 1f;
        private float _nextFastForwardMeasureAt;
        private float _lastFastForwardMeasureReal = -1f;
        private float _lastFastForwardMeasureScaled = -1f;
        private StoryManager _fastForwardManager;
        private Fungus.MenuDialog _pendingPositionMenu;
        private readonly List<string> _positionOptionTexts = new List<string>();
        private readonly List<bool> _positionOptionInteractable = new List<bool>();
        private float _positionMenuOpenedAt;
        private float _positionMenuLastOptionAt;
        private bool _positionMenuSelectionAttempted;
        private GameLevelManager _autoGroupBattleManager;
        private bool _autoGroupBattleHandled;

        private void Awake()
        {
            _instance = this;
            _enabled = Config.Bind(
                "DailyAutomation",
                "EnableAutomation",
                true,
                "是否加载日常自动化逻辑。false=停用，但插件仍会加载。");
            _toggleKey = Config.Bind(
                "DailyAutomation",
                "ToggleKey",
                KeyCode.F8,
                "开始/停止自动日常的热键。建议使用 F8，None 表示不绑定热键。");
            _windowToggleKey = Config.Bind(
                "DailyAutomation",
                "WindowToggleKey",
                KeyCode.F7,
                "显示/隐藏自动化面板的热键。None 表示不绑定热键。");
            _showWindowAtStartup = Config.Bind(
                "DailyAutomation",
                "ShowWindowAtStartup",
                true,
                "是否在游戏启动时显示自动化面板。");
            _alwaysFastForward = Config.Bind(
                "DailyAutomation",
                "AlwaysFastForward",
                true,
                "独立的常态化快进开关。已读文本继续快进；未读文本暂停快进与日常自动化。手动处理剧情选项后，已读分支会接续之前暂停的日常。");
            _fastForwardTimeScale = Config.Bind(
                "DailyAutomation",
                "FastForwardTimeScale",
                10f,
                "常态化快进倍率。原生倍率为 10；可调范围 10-1000，数值越大越快。过高可能导致动画或转场跳过。");
            _autoWinGroupBattleDuringFastForward = Config.Bind(
                "DailyAutomation",
                "AutoWinGroupBattleDuringFastForward",
                true,
                "常态化快进时遇到团体战，是否自动选择友方获胜。团体战面板按钮仍可独立手动使用。");
            _actionOrder = Config.Bind(
                "DailyAutomation",
                "ActionOrder",
                "正心堂,校场,煉丹房,鍛冶場,後山,弟子房,講經堂,伙房",
                "自由行动地点顺序，逗号分隔；会循环寻找当前可用地点。支持中文名称、英文别名或枚举数字。");
            _innerActionPreference = Config.Bind(
                "DailyAutomation",
                "InnerActionPreference",
                "自由",
                "进入地点后的固定优先子选项（ContributionTarget=0 时生效）：工作 或 自由（英文别名 work/free 也可）。菜单中没有该类别时自动改选另一类别。");
            _contributionTarget = Config.Bind(
                "DailyAutomation",
                "ContributionTarget",
                60,
                "贡献度优先策略：个人贡献度低于该值时优先选“工作”（工作给贡献），达到后优先选“自由”。0 表示关闭，改用 InnerActionPreference 固定偏好。");
            _doorOptionCursor = Config.Bind(
                "DailyAutomation",
                "DoorOptionCursor",
                0,
                "大门菜单轮换进度（从 0 开始的选项序号）：每次进入大门按列表顺序选下一项，用于轮流触发大门的所有事件；面板“大门轮换重置”可清零。");
            _breakTempleOptionOrder = Config.Bind(
                "DailyAutomation",
                "BreakTempleOptionOrder",
                "修炼武功,打工赚钱,无所事事,饮食,休息",
                "破庙养成菜单的优先顺序。支持：修炼武功、打工赚钱、无所事事、饮食、休息；下一级具体菜单没有配置项时选第一个可用项。");

            InitPanelAutomation();
            _timeoutSeconds = Config.Bind(
                "DailyAutomation",
                "ActionTimeoutSeconds",
                90f,
                "单次行动等待下一次自由行动场景的最长秒数。超时会自动停止，范围 15-600。");
            _stopOnMainMission = Config.Bind(
                "DailyAutomation",
                "StopOnMainMission",
                true,
                "检测到主线行动提示时停止，避免自动化替用户推进主线。");
            _stopOnSubMission = Config.Bind(
                "DailyAutomation",
                "StopOnSubMission",
                false,
                "检测到支线行动提示时停止，避免跳过支线事件。");
            _diagnosticLogging = Config.Bind(
                "DailyAutomation",
                "DiagnosticLogging",
                true,
                "自动化等待期间每秒输出一条诊断日志，用于排查卡住原因。正常使用可关闭。");

            _positionField = typeof(PositionController).GetField(
                "_position",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _positionActiveField = typeof(PositionController).GetField(
                "_active",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _isActiveStatusMethod = typeof(PositionController).GetMethod(
                "IsActiveStatus",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _hasActionPointMethod = typeof(PositionController).GetMethod(
                "HasActionPoint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _storyAutoDialogField = typeof(StoryManager).GetField(
                "_autoDialog",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _storySkipDialogField = typeof(StoryManager).GetField(
                "_skipDialog",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _storyEnableSkipField = typeof(StoryManager).GetField(
                "_enableSkip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_positionField == null)
            {
                Logger.LogError("找不到 PositionController._position，插件不会执行自动化。");
            }

            _windowRect = new Rect(20f, 20f, 430f, 560f);
            _showWindow = _showWindowAtStartup.Value;
            TryParseActionOrder();

            try
            {
                Logger.LogInfo("剧情检测补丁初始化中。");
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(DailyAutomationPlugin).Assembly);
                Logger.LogInfo("剧情检测补丁已应用：未读文本与新剧情选项会暂停自动化。");
            }
            catch (Exception ex)
            {
                Logger.LogError("剧情检测补丁加载失败，自动化仍可执行地点流程：" + ex);
            }

            Logger.LogInfo("地点菜单自动选择已启用：优先“" + GetPreferredInnerAction() + "”，缺失时自动改选另一档；团体战与对决按钮为独立手动工具。");
            Logger.LogInfo("活侠传日常自动化已加载。按 " + _toggleKey.Value + " 开始/停止，按 " + _windowToggleKey.Value + " 显示/隐藏面板。");
        }

        private void Update()
        {
            try
            {
                if (_toggleKey.Value != KeyCode.None && Input.GetKeyDown(_toggleKey.Value))
                {
                    if (_running)
                    {
                        Stop("手动停止");
                    }
                    else
                    {
                        StartAutomation();
                    }
                }

                if (_windowToggleKey.Value != KeyCode.None && Input.GetKeyDown(_windowToggleKey.Value))
                {
                    _showWindow = !_showWindow;
                }

                if (!_enabled.Value)
                {
                    if (_running)
                    {
                        Stop("配置已停用");
                    }
                    return;
                }

                if (ShouldAlwaysFastForward() && !_fastForwardPausedForUnread)
                {
                    UpdateStoryFastForward(GetFreeManager());
                }
                else
                {
                    RestoreStoryFastForward();
                }

                // 常态化快进的团体战处理与日常地点流程相互独立；即使日常自动化已停止，
                // 只要仍开启常态快进并勾选自动友方获胜，就继续处理团体战。
                ProcessAutomaticGroupBattleWin();

                if (!_running && _resumeDailyAfterMenu && _manualMenuSelected && GetFreeManager() != null)
                {
                    ResumeDailyAfterSelection();
                }

                if (_running)
                {
                    ProcessPendingPositionMenu();
                    TickPanels();
                    TickAutomation();
                }
            }
            catch (Exception ex)
            {
                Stop("运行时异常");
                Logger.LogError("日常自动化已停止：" + ex);
            }
        }

        private void OnDestroy()
        {
            RestoreStoryFastForward();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }

            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }

        private void StartAutomation()
        {
            if (!_enabled.Value)
            {
                Log("配置 EnableAutomation=false，未启动。");
                return;
            }

            FreeManager manager = GetFreeManager();
            PlayerStatManagerData stats = GetStats();
            MartialPanel openMartialPanel = FindOpenMartialPanel();
            UpgradePanel openUpgradePanel = FindOpenUpgradePanel();
            bool panelStart = openMartialPanel != null || openUpgradePanel != null;
            bool unreadStoryResume = _fastForwardPausedForUnread && GetStoryManager() != null;
            if (stats == null || (!panelStart && manager == null && !unreadStoryResume))
            {
                Log("请先进入自由行动界面，或先打开武学/升级面板，再按热键启动。");
                return;
            }

            if (unreadStoryResume)
            {
                _running = true;
                _autoGroupBattleManager = null;
                _autoGroupBattleHandled = false;
                _resumeDailyAfterMenu = false;
                _manualMenuSelected = false;
                _storyInProgress = true;
                _awaitingAction = false;
                _allowPausedUnreadOnce = true;
                _lastActionCount = stats.CurrentActionCount;
                _lastProgressAt = Time.unscaledTime;
                _nextAttemptAt = Time.unscaledTime + 0.35f;
                _status = "已从未读文本暂停处继续，请先读完当前文本";
                Log(_status);
                return;
            }

            if (!panelStart && stats.CurrentActionCount <= 0)
            {
                Log("当前没有可用行动点，未启动。");
                return;
            }

            if (!TryParseActionOrder() && !panelStart)
            {
                Log("ActionOrder 无有效地点，请在 LOMDailyAutomation.cfg 中配置可识别的地点。");
                return;
            }

            string missionMessage;
            if (!panelStart && ShouldStopForMission(out missionMessage))
            {
                Log(missionMessage);
                return;
            }

            _running = true;
            _autoGroupBattleManager = null;
            _autoGroupBattleHandled = false;
            _resumeDailyAfterMenu = false;
            _manualMenuSelected = false;
            _fastForwardPausedForUnread = false;
            _pausedUnreadKey = null;
            _allowPausedUnreadOnce = false;
            _awaitingAction = false;
            _storyInProgress = panelStart;
            ClearPositionMenuTracking();
            if (_sequenceIndex >= _positions.Count)
            {
                _sequenceIndex = 0;
            }

            _lastActionCount = stats.CurrentActionCount;
            _lastProgressAt = Time.unscaledTime;
            _nextAttemptAt = Time.unscaledTime + 0.2f;
            _nextDiagnosticAt = 0f;
            _status = "运行中";
            Log("已启动：行动点=" + _lastActionCount + "，顺序=" + FormatPositionList() +
                (_sequenceIndex > 0 ? "，从第 " + (_sequenceIndex + 1) + " 项继续" : ""));
            if (openMartialPanel != null)
            {
                _openMartialPanel = openMartialPanel;
                _openUpgradePanel = null;
                _martialPanelAttempts = 0;
                _nextPanelActionAt = Time.unscaledTime + 0.8f;
                Log("从武学面板启动，准备自动修炼。");
            }
            else if (openUpgradePanel != null)
            {
                _openUpgradePanel = openUpgradePanel;
                _openMartialPanel = null;
                _nextPanelActionAt = Time.unscaledTime + 0.1f;
                Log("从升级面板启动，准备自动加点。");
            }
            MaybeLogDiagnostics(manager, stats, null);
        }

        private void TickAutomation()
        {
            FreeManager manager = GetFreeManager();
            PlayerStatManagerData stats = GetStats();
            if (stats == null)
            {
                Stop("找不到玩家状态，已停止");
                return;
            }

            if (_openMartialPanel != null || _openUpgradePanel != null)
            {
                return;
            }

            bool leftFreeScene = manager == null;

            if (_storyInProgress)
            {
                if (!leftFreeScene && !manager.IsProcessing && !_storyStateCaptured)
                {
                    _storyInProgress = false;
                    ClearPositionMenuTracking();
                    _nextAttemptAt = Time.unscaledTime + 0.35f;
                    _lastProgressAt = Time.unscaledTime;
                    Log("自由行动界面已返回，继续执行");
                    if (_dayEndRequested)
                    {
                        _dayEndRequested = false;
                        Log("新的一天开始，继续日常。");
                    }

                    if (stats.CurrentActionCount <= 0)
                    {
                        Stop("行动点已耗尽");
                    }
                }
                else if (Time.unscaledTime - _lastProgressAt > GetTimeoutSeconds())
                {
                    Stop("等待剧情或自由行动返回超时（" + GetTimeoutSeconds().ToString("0") + " 秒）");
                }

                if (_storyInProgress)
                {
                    MaybeLogDiagnostics(manager, stats, null);
                    return;
                }
            }

            if (_awaitingAction)
            {
                int currentCount = stats.CurrentActionCount;
                if (currentCount < _lastActionCount)
                {
                    _lastActionCount = currentCount;
                    _awaitingAction = false;
                    _storyInProgress = true;
                    _lastProgressAt = Time.unscaledTime;
                    _nextAttemptAt = Time.unscaledTime + 0.35f;
                    _status = "行动完成，剩余行动点=" + currentCount;
                    Log("行动完成，剩余行动点=" + currentCount);
                    _lastReloadCause = null;
                    _reloadStreak = 0;
                    return;
                }

                if (leftFreeScene || manager.IsProcessing)
                {
                    _awaitingAction = false;
                    _storyInProgress = true;
                    _lastProgressAt = Time.unscaledTime;
                    _nextAttemptAt = Time.unscaledTime + 0.35f;
                    _status = "地点行动已开始，等待返回";
                    Log("地点行动已开始（场景切换或处理中）");
                    return;
                }

                if (Time.unscaledTime - _lastProgressAt > GetTimeoutSeconds())
                {
                    Stop("等待行动结果超时（" + GetTimeoutSeconds().ToString("0") + " 秒）");
                    return;
                }

                if (Time.unscaledTime - _lastProgressAt > 6f)
                {
                    _awaitingAction = false;
                    _nextAttemptAt = Time.unscaledTime + 0.5f;
                    _status = "地点点击未生效，准备重试";
                    Log("地点点击未生效（6 秒内无行动点减少、无场景切换、无处理标志），将重试。");
                    return;
                }

                MaybeLogDiagnostics(manager, stats, null);
                return;
            }

            if (leftFreeScene)
            {
                Stop("已离开自由行动界面");
                return;
            }

            if (Time.unscaledTime < _nextAttemptAt)
            {
                return;
            }

            if (stats.CurrentActionCount <= 0)
            {
                Stop("行动点已耗尽");
                return;
            }

            string missionMessage;
            if (ShouldStopForMission(out missionMessage))
            {
                Stop(missionMessage);
                return;
            }

            if (!_dayEndRequested && AreAllPanelsExhausted())
            {
                _dayEndRequested = true;
                Log("武学/毒药/兵器已无可加点项，准备结束当天（前往弟子房休息）。");
            }

            if (_dayEndRequested)
            {
                PositionController rest = FindPosition(PositionType.弟子房);
                if (rest == null)
                {
                    Stop("已无可加点项，但弟子房当前不可用，已停止自动化");
                    return;
                }

                int restBefore = stats.CurrentActionCount;
                _awaitingAction = true;
                _lastActionCount = restBefore;
                _lastProgressAt = Time.unscaledTime;
                _status = "结束当天：前往弟子房休息";
                Log("结束当天：点击弟子房休息，行动点=" + restBefore);
                ClearPositionMenuTracking();
                rest.OnPositionClick();
                return;
            }

            string searchDiagnostic;
            PositionController target = FindNextPosition(out PositionType targetType, out searchDiagnostic);
            if (target == null)
            {
                _nextAttemptAt = Time.unscaledTime + 0.35f;
                _status = manager.IsProcessing ? "等待中：游戏处理中" : "等待中：暂无可用地点";
                if (Time.unscaledTime - _lastProgressAt > GetTimeoutSeconds())
                {
                    Stop("等待可执行地点超时（" + GetTimeoutSeconds().ToString("0") + " 秒）：" + searchDiagnostic);
                    return;
                }

                MaybeLogDiagnostics(manager, stats, searchDiagnostic);
                return;
            }

            int before = stats.CurrentActionCount;
            _awaitingAction = true;
            _lastActionCount = before;
            _lastProgressAt = Time.unscaledTime;
            _status = "执行 " + targetType;
            Log("执行地点=" + targetType + "，行动点=" + before);
            ClearPositionMenuTracking();
            target.OnPositionClick();
        }

        private void Stop(string reason)
        {
            if (!_running && reason == "手动停止")
            {
                return;
            }

            bool wasRunning = _running;
            _running = false;
            _autoGroupBattleManager = null;
            _autoGroupBattleHandled = false;
            _resumeDailyAfterMenu = false;
            _manualMenuSelected = false;
            if (reason == "手动停止")
            {
                _fastForwardPausedForUnread = false;
                _pausedUnreadKey = null;
                _allowPausedUnreadOnce = false;
            }
            _awaitingAction = false;
            _storyInProgress = false;
            _openMartialPanel = null;
            _openUpgradePanel = null;
            ClearPositionMenuTracking();
            _status = reason;
            RestoreStoryFastForward();
            if (wasRunning || reason != "手动停止")
            {
                Log("已停止：" + reason);
            }
        }

        private FreeManager GetFreeManager()
        {
            try
            {
                return FreeManager.Instance;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取 FreeManager.Instance 失败：" + ex.Message);
                return null;
            }
        }

        private PlayerStatManagerData GetStats()
        {
            try
            {
                return PlayerStatManagerData.Instance;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取 PlayerStatManagerData.Instance 失败：" + ex.Message);
                return null;
            }
        }

        private StoryManager GetStoryManager()
        {
            try
            {
                return StoryManager.Instance;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取 StoryManager.Instance 失败：" + ex.Message);
                return null;
            }
        }

        private void UpdateStoryFastForward(FreeManager freeManager)
        {
            StoryManager storyManager = GetStoryManager();
            if (storyManager != null && freeManager == null)
            {
                if (_storyStateCaptured && _fastForwardManager == storyManager)
                {
                    if (Time.timeScale > 0f)
                    {
                        Time.timeScale = GetFastForwardTimeScale();
                    }

                    // 游戏在出现选项/转场时会自行关闭快进标志，这里持续重申，
                    // 保证点击选项后已读剧情继续快进。
                    try
                    {
                        storyManager.AutoDialog(true);
                        storyManager.EnableSkip(true);
                        storyManager.SkipDialog(true);
                    }
                    catch
                    {
                    }
                    MeasureFastForwardRate();
                    return;
                }

                RestoreStoryFastForward();
                _fastForwardManager = storyManager;
                _storyStateCaptured = true;
                _previousAutoDialog = ReadBoolField(_storyAutoDialogField, storyManager);
                _previousSkipDialog = ReadBoolField(_storySkipDialogField, storyManager);
                _previousEnableSkip = ReadBoolField(_storyEnableSkipField, storyManager);
                _previousTimeScale = Time.timeScale;

                storyManager.AutoDialog(true);
                storyManager.EnableSkip(true);
                storyManager.SkipDialog(true);
                Time.timeScale = GetFastForwardTimeScale();
                _lastFastForwardMeasureReal = Time.unscaledTime;
                _lastFastForwardMeasureScaled = Time.time;
                _nextFastForwardMeasureAt = Time.unscaledTime + 2f;
                _status = "剧情快进中，等待未读文本或新选项";
                Log("进入剧情场景，已开启快进；遇到未读文本或新选项会暂停。");
                return;
            }

            if (_storyStateCaptured)
            {
                RestoreStoryFastForward();
            }
        }

        private void RestoreStoryFastForward()
        {
            if (!_storyStateCaptured)
            {
                return;
            }

            StoryManager storyManager = _fastForwardManager;
            try
            {
                if (storyManager != null)
                {
                    storyManager.AutoDialog(_previousAutoDialog);
                    storyManager.EnableSkip(_previousEnableSkip);
                    storyManager.SkipDialog(_previousSkipDialog);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("恢复剧情播放状态失败：" + ex.Message);
            }

            Time.timeScale = _previousTimeScale;
            _fastForwardManager = null;
            _storyStateCaptured = false;
            _lastFastForwardMeasureReal = -1f;
            _lastFastForwardMeasureScaled = -1f;
            _nextFastForwardMeasureAt = 0f;
            Log("已还原剧情播放状态。");
        }

        private float GetFastForwardTimeScale()
        {
            // 实测：游戏原生快进固定 10 倍；设置 >=20 倍会被游戏自身逻辑取消（有效速度反而约 1x），
            // 因此这里固定返回 10。若以后找到绕过方式再放开。
            return 10f;
        }

        private void SetFastForwardTimeScale(float value)
        {
            if (_fastForwardTimeScale == null)
            {
                return;
            }

            _fastForwardTimeScale.Value = 10f;
            Config.Save();
            _status = "快进倍率设为 " + _fastForwardTimeScale.Value.ToString("0") + "x";
            if (_storyStateCaptured && Time.timeScale > 0f)
            {
                Time.timeScale = GetFastForwardTimeScale();
            }
        }

        private void MeasureFastForwardRate()
        {
            if (!_storyStateCaptured || Time.unscaledTime < _nextFastForwardMeasureAt)
            {
                return;
            }

            float realNow = Time.unscaledTime;
            float scaledNow = Time.time;
            float realDelta = realNow - _lastFastForwardMeasureReal;
            float scaledDelta = scaledNow - _lastFastForwardMeasureScaled;
            if (realDelta > 0.01f)
            {
                Log("[快进测速] 请求=" + GetFastForwardTimeScale().ToString("0") +
                    "x，Time.timeScale=" + Time.timeScale.ToString("0.0") +
                    "，Time.time/真实时间=" + (scaledDelta / realDelta).ToString("0.0") + "x");
            }

            _lastFastForwardMeasureReal = realNow;
            _lastFastForwardMeasureScaled = scaledNow;
            _nextFastForwardMeasureAt = realNow + 2f;
        }

        private static bool ReadBoolField(FieldInfo field, object instance)
        {
            if (field == null || instance == null)
            {
                return false;
            }

            try
            {
                object value = field.GetValue(instance);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private void OnStoryTextRequested(string key, bool isUnread)
        {
            if (isUnread && _allowPausedUnreadOnce &&
                string.Equals(key, _pausedUnreadKey, StringComparison.Ordinal))
            {
                isUnread = false;
                _allowPausedUnreadOnce = false;
                _pausedUnreadKey = null;
            }

            if (!isUnread)
            {
                _fastForwardPausedForUnread = false;
                if (_resumeDailyAfterMenu && _manualMenuSelected && ShouldAlwaysFastForward())
                {
                    ResumeDailyAfterSelection();
                }
                if (ShouldAlwaysFastForward())
                {
                    UpdateStoryFastForward(GetFreeManager());
                }
                return;
            }

            string reason = "遇到未读文本，已暂停：" + (string.IsNullOrEmpty(key) ? "未知文本" : key);
            Stop(reason);
            _resumeDailyAfterMenu = ShouldAlwaysFastForward();
            _fastForwardPausedForUnread = true;
            _pausedUnreadKey = key;
            _allowPausedUnreadOnce = false;
            StoryManager story = GetStoryManager();
            if (story != null)
            {
                story.AutoDialog(false);
                story.SkipDialog(false);
            }
        }

        private bool ShouldAlwaysFastForward()
        {
            return _enabled != null && _enabled.Value && _alwaysFastForward != null && _alwaysFastForward.Value;
        }

        private void OnManualMenuSelected()
        {
            if (_resumeDailyAfterMenu)
            {
                _manualMenuSelected = true;
            }
        }

        private void ResumeDailyAfterSelection()
        {
            _resumeDailyAfterMenu = false;
            _manualMenuSelected = false;
            _running = true;
            _awaitingAction = false;
            _storyInProgress = true;
            _lastProgressAt = Time.unscaledTime;
            _nextAttemptAt = Time.unscaledTime + 0.35f;
            _status = "选项已手动处理，继续已读剧情与日常";
            Log(_status);
        }

        private void ClearPositionMenuTracking()
        {
            _pendingPositionMenu = null;
            _positionOptionTexts.Clear();
            _positionOptionInteractable.Clear();
            _positionMenuOpenedAt = 0f;
            _positionMenuLastOptionAt = 0f;
            _positionMenuSelectionAttempted = false;
        }

        private void OnMenuOptionAdded(Fungus.MenuDialog menu, string text, bool interactable)
        {
            if (!_running || menu == null)
            {
                return;
            }

            bool sameSession = _pendingPositionMenu == menu &&
                !_positionMenuSelectionAttempted &&
                Time.unscaledTime - _positionMenuLastOptionAt <= 3f;
            if (!sameSession)
            {
                _positionOptionTexts.Clear();
                _positionOptionInteractable.Clear();
                _positionMenuSelectionAttempted = false;
                _positionMenuOpenedAt = Time.unscaledTime;
            }

            _pendingPositionMenu = menu;
            _positionMenuLastOptionAt = Time.unscaledTime;
            _positionOptionTexts.Add(text ?? string.Empty);
            _positionOptionInteractable.Add(interactable);
            _status = "识别菜单选项：" + _positionOptionTexts.Count + " 个";
        }

        private void OnPositionMenuOpened(Fungus.MenuDialog menu)
        {
            if (!_running || menu == null)
            {
                return;
            }

            if (_pendingPositionMenu != menu ||
                _positionMenuSelectionAttempted ||
                Time.unscaledTime - _positionMenuLastOptionAt > 3f)
            {
                _positionOptionTexts.Clear();
                _positionOptionInteractable.Clear();
                _positionMenuSelectionAttempted = false;
                _positionMenuOpenedAt = Time.unscaledTime;
            }

            _pendingPositionMenu = menu;
            if (_positionMenuOpenedAt <= 0f)
            {
                _positionMenuOpenedAt = Time.unscaledTime;
            }

            _status = "地点菜单已打开，等待自动选择：" + GetPreferredInnerAction();
        }

        private void ProcessPendingPositionMenu()
        {
            if (!_running || _pendingPositionMenu == null || _positionMenuSelectionAttempted)
            {
                return;
            }

            if (_positionOptionTexts.Count == 0)
            {
                // 面板已打开但选项尚未添加：稍等；长时间没有选项则放弃跟踪。
                if (_positionMenuOpenedAt > 0f && Time.unscaledTime - _positionMenuOpenedAt > 10f)
                {
                    ClearPositionMenuTracking();
                }
                return;
            }

            // 等选项全部到位：首选项后 0.2 秒、且最后一个选项后 0.15 秒。
            if (Time.unscaledTime < _positionMenuOpenedAt + 0.2f ||
                Time.unscaledTime < _positionMenuLastOptionAt + 0.15f)
            {
                return;
            }

            string preferred = GetPreferredInnerAction();
            int contributionTarget = GetContributionTarget();
            int contribution = -1;
            if (contributionTarget > 0)
            {
                contribution = ReadContribution();
                if (contribution >= 0)
                {
                    // 贡献度优先策略：未达标先刷“工作”，达标后转“自由”。
                    preferred = contribution < contributionTarget ? "工作" : "自由";
                }
            }

            // 打铁房仍然照常进入；只有在当前确实没有可执行的设备升级时，
            // 才把“自由”改成“工作”，避免进入升级面板后空转。
            if (IsForgePositionMenu() && !HasCurrentWeaponUpgradeOpportunity())
            {
                preferred = "工作";
                _status = "打铁房没有可执行设备升级，准备选择工作";
            }

            string fallback = preferred == "工作" ? "自由" : "工作";

            // 捕获顺序 == 按钮槽位顺序（AddOption 每次调用顺序占用一个槽位），
            // 因此捕获下标 i 对应 CachedButtons[i]，非交互选项占用槽位但不参与选择。
            int breakTemplePickIndex;
            bool breakTempleMenu = TryPickBreakTempleOption(out breakTemplePickIndex);
            int pickIndex = breakTempleMenu ? breakTemplePickIndex : -1;

            // 大门菜单（DoorMenuDialog）：选项是出行/事件（move/event 类别，无工作/自由），
            // 按列表顺序轮换选择并持久化进度，保证每次换一项、不会错过随机事件。
            bool doorMenu = !breakTempleMenu && pickIndex < 0 && IsDoorMenu(_pendingPositionMenu);
            if (doorMenu)
            {
                pickIndex = PickDoorRotatingIndex();
            }

            bool hasInteractable = false;
            for (int i = 0; !breakTempleMenu && !(doorMenu && pickIndex >= 0) && i < _positionOptionTexts.Count; i++)
            {
                if (!_positionOptionInteractable[i])
                {
                    continue;
                }

                hasInteractable = true;
                string category = NormalizeInnerAction(_positionOptionTexts[i]);
                if (category.Length == 0)
                {
                    continue;
                }

                if (category == preferred)
                {
                    pickIndex = i;
                    break;
                }

                if (category == fallback && pickIndex < 0)
                {
                    pickIndex = i;
                }
            }

            if (pickIndex >= 0)
            {
                Button[] buttons;
                try
                {
                    buttons = _pendingPositionMenu.CachedButtons;
                }
                catch (Exception ex)
                {
                    Stop("读取地点菜单按钮失败，已停止自动化");
                    Logger.LogError("读取地点菜单按钮失败：" + ex);
                    return;
                }

                if (buttons == null || pickIndex >= buttons.Length)
                {
                    Stop("地点菜单按钮与选项数量不匹配，已停止自动化");
                    return;
                }

                Button button = buttons[pickIndex];
                if (button == null || !button.isActiveAndEnabled || !button.interactable)
                {
                    if (Time.unscaledTime - _positionMenuOpenedAt > 4f)
                    {
                        Stop("地点菜单按钮不可点击，已停止自动化");
                    }
                    return;
                }

                _positionMenuSelectionAttempted = true;
                try
                {
                    string picked = _positionOptionTexts[pickIndex];
                    if (doorMenu)
                    {
                        int optionCount = _positionOptionTexts.Count;
                        int nextCursor = optionCount > 0 ? (pickIndex + 1) % optionCount : 0;
                        Log("大门菜单轮换选择=第 " + (pickIndex + 1) + "/" + optionCount + " 项：" + picked +
                            "（下次从第 " + (nextCursor + 1) + " 项开始）");
                        if (_doorOptionCursor != null)
                        {
                            _doorOptionCursor.Value = nextCursor;
                            Config.Save();
                        }
                    }
                    else
                    {
                        Log("地点菜单选项=" + string.Join(" | ", _positionOptionTexts.ToArray()) +
                            "；按钮数=" + buttons.Length + "，选中槽位=" + pickIndex +
                            "；贡献度=" + (contribution >= 0 ? contribution.ToString() : "?") + "，优先=" + preferred);
                    }

                    button.onClick.Invoke();
                    if (doorMenu)
                    {
                        _status = "大门菜单轮换选择：" + picked;
                    }
                    else
                    {
                        _status = "已自动选择地点子选项：" + picked;
                        Log("地点子选项自动选择=" + picked);
                    }
                    ClearPositionMenuTracking();
                }
                catch (Exception ex)
                {
                    Stop("执行地点子选项失败，已停止自动化");
                    Logger.LogError("执行地点子选项失败：" + ex);
                }
                return;
            }

            // 没有工作/自由类选项：先看夜晚菜单（结束当天流程），否则按剧情/对话菜单处理。
            Log("地点菜单选项=" + string.Join(" | ", _positionOptionTexts.ToArray()));
            if (_dayEndRequested)
            {
                int nightIndex;
                if (TryPickNightOption(_positionOptionTexts.ToArray(), out nightIndex))
                {
                    Button[] nightButtons;
                    try
                    {
                        nightButtons = _pendingPositionMenu.CachedButtons;
                    }
                    catch (Exception ex)
                    {
                        Stop("读取夜晚菜单按钮失败，已停止自动化");
                        Logger.LogError("读取夜晚菜单按钮失败：" + ex);
                        return;
                    }

                    if (nightButtons != null && nightIndex < nightButtons.Length)
                    {
                        Button nightButton = nightButtons[nightIndex];
                        if (nightButton != null && nightButton.isActiveAndEnabled && nightButton.interactable)
                        {
                            _positionMenuSelectionAttempted = true;
                            try
                            {
                                string nightPicked = _positionOptionTexts[nightIndex];
                                nightButton.onClick.Invoke();
                                _status = "夜晚菜单自动选择：" + nightPicked;
                                Log("夜晚菜单自动选择=" + nightPicked);
                                ClearPositionMenuTracking();
                            }
                            catch (Exception ex)
                            {
                                Stop("执行夜晚菜单选项失败，已停止自动化");
                                Logger.LogError("执行夜晚菜单选项失败：" + ex);
                            }
                            return;
                        }
                    }
                }
            }

            if (hasInteractable)
            {
                Stop("出现剧情或对话选项，已暂停自动化");
                _resumeDailyAfterMenu = ShouldAlwaysFastForward();
            }
            else
            {
                Stop("菜单没有可自动选择的选项，已停止自动化");
            }
            ClearPositionMenuTracking();
        }

        // 大门菜单识别：door_001 的菜单对象是 DoorMenuDialog（CustomMenuDialog 子类）。
        private static bool IsDoorMenu(Fungus.MenuDialog menu)
        {
            if (menu == null)
            {
                return false;
            }

            try
            {
                string menuName = menu.name ?? string.Empty;
                return menuName.IndexOf("DoorMenuDialog", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        // 大门轮换：从上次进度开始按列表顺序找第一个可交互选项，找不到则绕回开头。
        private int PickDoorRotatingIndex()
        {
            int count = _positionOptionTexts.Count;
            if (count == 0)
            {
                return -1;
            }

            int cursor = 0;
            if (_doorOptionCursor != null)
            {
                cursor = _doorOptionCursor.Value % count;
                if (cursor < 0)
                {
                    cursor = 0;
                }
            }

            for (int step = 0; step < count; step++)
            {
                int i = (cursor + step) % count;
                if (_positionOptionInteractable[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private int GetDoorCursor()
        {
            if (_doorOptionCursor == null)
            {
                return 0;
            }

            int value = _doorOptionCursor.Value;
            return value < 0 ? 0 : value;
        }

        private static string NormalizeInnerAction(string value)
        {
            string key = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                return string.Empty;
            }

            // 地点菜单选项格式：活动id+类别+脚本，例如 incense+work+incense（上香·工作）。
            int firstPlus = key.IndexOf('+');
            if (firstPlus >= 0)
            {
                int secondPlus = key.IndexOf('+', firstPlus + 1);
                if (secondPlus > firstPlus + 1)
                {
                    string category = key.Substring(firstPlus + 1, secondPlus - firstPlus - 1).Trim();
                    if (category == "work" || category == "工作")
                    {
                        return "工作";
                    }
                    if (category == "free" || category == "自由")
                    {
                        return "自由";
                    }
                }

                return string.Empty;
            }

            if (key.Contains("工作") || key == "work" || key == "labour" || key == "labor")
            {
                return "工作";
            }
            // 旧配置值“修炼”及练功类词汇归入自由度类别。
            if (key.Contains("自由") || key == "free" || key == "rest" ||
                key.Contains("修炼") || key.Contains("修煉") || key.Contains("练功") || key.Contains("練功") ||
                key == "cultivate" || key == "training")
            {
                return "自由";
            }

            return string.Empty;
        }

        private bool TryPickBreakTempleOption(out int pickIndex)
        {
            pickIndex = -1;
            bool recognized = false;
            for (int i = 0; i < _positionOptionTexts.Count; i++)
            {
                if (!_positionOptionInteractable[i])
                {
                    continue;
                }

                if (GetBreakTempleOptionLabel(_positionOptionTexts[i]).Length > 0)
                {
                    recognized = true;
                    break;
                }
            }

            if (!recognized)
            {
                return false;
            }

            string[] order = (_breakTempleOptionOrder == null ? string.Empty : _breakTempleOptionOrder.Value ?? string.Empty).Split(
                new[] { ',', '，', ';', '；', '|', '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int rank = 0; rank < order.Length; rank++)
            {
                string wanted = NormalizeBreakTempleLabel(order[rank]);
                if (wanted.Length == 0)
                {
                    continue;
                }

                for (int i = 0; i < _positionOptionTexts.Count; i++)
                {
                    if (!_positionOptionInteractable[i] ||
                        !string.Equals(GetBreakTempleOptionLabel(_positionOptionTexts[i]), wanted, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    pickIndex = i;
                    return true;
                }
            }

            // 主菜单或下一级菜单中没有配置的名称时，按脚本提供的第一个可用项继续，
            // 避免破庙养成被误判成剧情选项而停止。
            for (int i = 0; i < _positionOptionTexts.Count; i++)
            {
                if (_positionOptionInteractable[i] && GetBreakTempleOptionLabel(_positionOptionTexts[i]).Length > 0)
                {
                    pickIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static string GetBreakTempleOptionLabel(string optionText)
        {
            string[] parts = (optionText ?? string.Empty).Split('+');
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            string id = (parts[1] ?? string.Empty).Trim().ToUpperInvariant();
            if (!id.StartsWith("O_2_4_3_BREAK_01_", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (id.EndsWith("_001", StringComparison.Ordinal)) return "饮食";
            if (id.EndsWith("_002", StringComparison.Ordinal)) return "无所事事";
            if (id.EndsWith("_003", StringComparison.Ordinal)) return "钓鱼";
            if (id.EndsWith("_004", StringComparison.Ordinal)) return "闹市闲游";
            if (id.EndsWith("_005", StringComparison.Ordinal)) return "流连赌坊";
            if (id.EndsWith("_006", StringComparison.Ordinal)) return "客栈饮茶";
            if (id.EndsWith("_008", StringComparison.Ordinal)) return "修炼武功";
            if (id.EndsWith("_009", StringComparison.Ordinal)) return "打工赚钱";
            if (id.EndsWith("_010", StringComparison.Ordinal)) return "义田";
            if (id.EndsWith("_011", StringComparison.Ordinal)) return "铁铺帮活";
            if (id.EndsWith("_012", StringComparison.Ordinal)) return "休息";
            if (id.EndsWith("_013", StringComparison.Ordinal)) return "大宋闲汉赵活";
            return string.Empty;
        }

        private static string NormalizeBreakTempleLabel(string value)
        {
            string key = (value ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return string.Empty;
            }

            if (key.Contains("饮食") || key.Contains("飲食") || key.Contains("吃饭") || key.Contains("吃飯")) return "饮食";
            if (key.Contains("无所事事") || key.Contains("無所事事")) return "无所事事";
            if (key.Contains("修炼") || key.Contains("修練") || key.Contains("修练") || key.Contains("修煉")) return "修炼武功";
            if (key.Contains("打工") || key.Contains("賺錢") || key.Contains("赚钱")) return "打工赚钱";
            if (key.Contains("休息")) return "休息";
            if (key.Contains("钓鱼") || key.Contains("釣魚")) return "钓鱼";
            if (key.Contains("闹市") || key.Contains("鬧市")) return "闹市闲游";
            if (key.Contains("赌坊") || key.Contains("賭坊")) return "流连赌坊";
            if (key.Contains("客栈") || key.Contains("客棧")) return "客栈饮茶";
            if (key.Contains("义田") || key.Contains("義田")) return "义田";
            if (key.Contains("铁铺") || key.Contains("鐵舖") || key.Contains("铁舖")) return "铁铺帮活";
            if (key.Contains("闲汉") || key.Contains("閒漢")) return "大宋闲汉赵活";
            return string.Empty;
        }

        private bool IsForgePositionMenu()
        {
            for (int i = 0; i < _positionOptionTexts.Count; i++)
            {
                string value = (_positionOptionTexts[i] ?? string.Empty).Trim().ToLowerInvariant();
                if (value.StartsWith("forge+") || value.Contains("forge_001") ||
                    value.Contains("鍛冶") || value.Contains("锻冶"))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetPreferredInnerAction()
        {
            string normalized = NormalizeInnerAction(_innerActionPreference == null ? null : _innerActionPreference.Value);
            return string.IsNullOrEmpty(normalized) ? "自由" : normalized;
        }

        private int GetContributionTarget()
        {
            return _contributionTarget == null ? 0 : Mathf.Max(0, _contributionTarget.Value);
        }

        private int ReadContribution()
        {
            try
            {
                PlayerStatManagerData stats = GetStats();
                if (stats == null)
                {
                    return -1;
                }

                return stats.Stats.Get(GameStatType.個人貢獻度).FinalValue;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取贡献度失败：" + ex.Message);
                return -1;
            }
        }

        private void SetInnerActionPreference(string value)
        {
            string normalized = NormalizeInnerAction(value);
            if (string.IsNullOrEmpty(normalized))
            {
                normalized = "自由";
            }

            _innerActionPreference.Value = normalized;
            Config.Save();
            _status = "地点子选项设为：" + normalized;
        }

        private GameLevelManager GetGroupBattleManager()
        {
            try
            {
                return GameLevelManager.Instance;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取 GameLevelManager.Instance 失败：" + ex.Message);
                return null;
            }
        }

        private static bool TryParseGroupBattleOutcome(string value, out GameOverType type, out string label)
        {
            string key = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (key == "友方获胜" || key == "友方勝利" || key == "友方胜利" ||
                key == "friendwin" || key == "friendly" || key == "win" || key == "2")
            {
                type = GameOverType.FriendWin;
                label = "友方获胜";
                return true;
            }
            if (key == "敌方获胜" || key == "敵方勝利" || key == "敌方胜利" ||
                key == "enemywin" || key == "enemy" || key == "lose" || key == "3")
            {
                type = GameOverType.EnemyWin;
                label = "敌方获胜";
                return true;
            }
            if (key == "时间到" || key == "時間到" || key == "timeout" || key == "time" || key == "4")
            {
                type = GameOverType.Timeout;
                label = "时间到";
                return true;
            }

            type = GameOverType.FriendWin;
            label = "友方获胜";
            return false;
        }

        private bool TryResolveCurrentGroupBattle(string value)
        {
            GameLevelManager battle = GetGroupBattleManager();
            if (battle == null)
            {
                _status = "当前没有检测到团体战";
                return false;
            }

            if (battle.IsGameOver)
            {
                _status = "当前团体战已经结算";
                return false;
            }

            if (!battle.IsReady)
            {
                _status = "团体战尚未进入可结算阶段";
                return false;
            }

            GameOverType type;
            string label;
            bool recognized = TryParseGroupBattleOutcome(value, out type, out label);
            if (!recognized)
            {
                return false;
            }

            try
            {
                battle.ShowGameOver(type, true);
                _lastProgressAt = Time.unscaledTime;
                _status = "团体战已处理：" + label;
                Log("团体战结果=" + label);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("处理团体战结果失败：" + ex);
                _status = "处理团体战结果失败";
                return false;
            }
        }

        private void ProcessAutomaticGroupBattleWin()
        {
            if (!ShouldAlwaysFastForward() ||
                _autoWinGroupBattleDuringFastForward == null ||
                !_autoWinGroupBattleDuringFastForward.Value)
            {
                return;
            }

            GameLevelManager battle = GetGroupBattleManager();
            if (battle == null)
            {
                return;
            }

            if (_autoGroupBattleManager != battle)
            {
                _autoGroupBattleManager = battle;
                _autoGroupBattleHandled = false;
            }

            if (battle.IsGameOver)
            {
                // 同一个 GameLevelManager 会复用于下一场团体战；上一场结算后允许重新处理。
                _autoGroupBattleHandled = false;
                return;
            }

            if (_autoGroupBattleHandled || !battle.IsReady)
            {
                return;
            }

            try
            {
                battle.ShowGameOver(GameOverType.FriendWin, true);
                _autoGroupBattleHandled = true;
                _status = "常态快进遇到团体战，已自动选择友方获胜";
                Log(_status);
            }
            catch (Exception ex)
            {
                Logger.LogError("常态快进自动处理团体战失败：" + ex);
            }
        }

        private float GetTimeoutSeconds()
        {
            return Mathf.Clamp(_timeoutSeconds.Value, 15f, 600f);
        }

        private bool ShouldStopForMission(out string message)
        {
            message = null;
            try
            {
                MissionManagerData missions = MissionManagerData.Instance;
                if (missions == null)
                {
                    return false;
                }

                if (_stopOnMainMission.Value && missions.MainMissionStart)
                {
                    message = "检测到主线行动提示（地点=" + missions.MainMissionPosition + "），请手动处理后再启动";
                    return true;
                }

                if (_stopOnSubMission.Value && HasPendingSubMission(missions))
                {
                    message = "检测到支线行动提示，请手动处理后再启动";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("读取任务状态失败，为安全起见停止：" + ex.Message);
                message = "读取任务状态失败，已停止自动化";
                return true;
            }

            return false;
        }

        private static bool HasPendingSubMission(MissionManagerData missions)
        {
            Dictionary<string, MissionCheckData> active = missions.ActiveSubMissions;
            if (active == null || active.Count == 0)
            {
                return false;
            }

            foreach (KeyValuePair<string, MissionCheckData> pair in active)
            {
                MissionCheckData mission = pair.Value;
                if (mission != null && !string.IsNullOrEmpty(mission.StartScript))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryParseActionOrder()
        {
            _positions.Clear();
            string raw = _actionOrder.Value ?? string.Empty;
            string[] tokens = raw.Split(
                new[] { ',', '，', ';', '；', '|', '\r', '\n', '\t', ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < tokens.Length; i++)
            {
                PositionType position;
                if (!TryParsePosition(tokens[i], out position))
                {
                    Logger.LogWarning("忽略无法识别的 ActionOrder 项：" + tokens[i]);
                    continue;
                }

                _positions.Add(position);
            }

            return _positions.Count > 0;
        }

        private static bool TryParsePosition(string value, out PositionType position)
        {
            string key = (value ?? string.Empty).Trim();
            switch (key)
            {
                case "正心堂":
                case "ZhengXinTang":
                case "MindHall":
                    position = PositionType.正心堂;
                    return true;
                case "校场":
                case "校場":
                case "校场/校場":
                case "School":
                case "TrainingGround":
                    position = PositionType.校場;
                    return true;
                case "炼丹房":
                case "煉丹房":
                case "Alchemy":
                    position = PositionType.煉丹房;
                    return true;
                case "锻冶场":
                case "鍛冶場":
                case "Forge":
                    position = PositionType.鍛冶場;
                    return true;
                case "后山":
                case "後山":
                case "BackMountain":
                    position = PositionType.後山;
                    return true;
                case "弟子房":
                case "DiscipleRoom":
                    position = PositionType.弟子房;
                    return true;
                case "大门":
                case "大門":
                case "Gate":
                    position = PositionType.大門;
                    return true;
                case "讲经堂":
                case "講經堂":
                case "LectureHall":
                    position = PositionType.講經堂;
                    return true;
                case "伙房":
                case "Kitchen":
                    position = PositionType.伙房;
                    return true;
                case "女弟子房":
                case "FemaleDiscipleRoom":
                    position = PositionType.女弟子房;
                    return true;
                case "神秘房子":
                case "MysteryHouse":
                    position = PositionType.神秘房子;
                    return true;
            }

            int number;
            if (int.TryParse(key, out number) && Enum.IsDefined(typeof(PositionType), number))
            {
                position = (PositionType)number;
                return position != PositionType.無;
            }

            if (Enum.TryParse<PositionType>(key, true, out position))
            {
                return position != PositionType.無;
            }

            position = PositionType.無;
            return false;
        }

        private PositionController FindNextPosition(out PositionType position, out string diagnostic)
        {
            position = PositionType.無;
            diagnostic = string.Empty;
            if (_positionField == null || _positions.Count == 0)
            {
                diagnostic = "_position 字段或顺序为空";
                return null;
            }

            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(typeof(PositionController));
            string detail = "控制器=" + objects.Length + " ";
            for (int offset = 0; offset < _positions.Count; offset++)
            {
                int index = (_sequenceIndex + offset) % _positions.Count;
                PositionType wanted = _positions[index];
                bool found = false;
                string state = "未找到";
                for (int i = 0; i < objects.Length; i++)
                {
                    PositionController controller = objects[i] as PositionController;
                    if (controller == null || ReadPosition(controller) != wanted)
                    {
                        continue;
                    }

                    found = true;
                    bool clickable;
                    bool hasActionPoint;
                    try
                    {
                        clickable = CallBool(_isActiveStatusMethod, controller);
                        hasActionPoint = CallBool(_hasActionPointMethod, controller);
                    }
                    catch (Exception ex)
                    {
                        state = "读取失败:" + ex.GetType().Name;
                        Logger.LogWarning("读取地点状态失败（" + wanted + "）：" + ex.Message);
                        continue;
                    }

                    state = "_active=" + (ReadActive(controller) ? "T" : "F") +
                            " 可点=" + (clickable ? "T" : "F") +
                            " 行动点=" + (hasActionPoint ? "T" : "F");
                    if (!clickable || !hasActionPoint)
                    {
                        continue;
                    }

                    _sequenceIndex = (index + 1) % _positions.Count;
                    position = wanted;
                    diagnostic = detail;
                    return controller;
                }

                detail += wanted + "[" + (found ? state : "未找到") + "] ";
            }

            diagnostic = detail;
            return null;
        }

        private bool ReadActive(PositionController controller)
        {
            if (_positionActiveField == null)
            {
                return false;
            }

            try
            {
                object value = _positionActiveField.GetValue(controller);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private void MaybeLogDiagnostics(FreeManager manager, PlayerStatManagerData stats, string extra)
        {
            if (_diagnosticLogging == null || !_diagnosticLogging.Value)
            {
                return;
            }

            if (Time.unscaledTime < _nextDiagnosticAt)
            {
                return;
            }

            _nextDiagnosticAt = Time.unscaledTime + 1f;
            string message = "[诊断] free=" + (manager != null ? "T" : "F") +
                " processing=" + (manager != null && manager.IsProcessing ? "T" : "F") +
                " action=" + stats.CurrentActionCount + "/" + stats.MaxActionCount +
                " awaiting=" + (_awaitingAction ? "T" : "F") +
                " story=" + (_storyInProgress ? "T" : "F") +
                " storyCaptured=" + (_storyStateCaptured ? "T" : "F") +
                " 距上次进度=" + (Time.unscaledTime - _lastProgressAt).ToString("0.0") + "s";
            if (!string.IsNullOrEmpty(extra))
            {
                message += " | " + extra;
            }

            Log(message);
        }

        private void OnGUI()
        {
            if (!_showWindow)
            {
                return;
            }

            _windowRect = GUI.Window(
                481620,
                _windowRect,
                DrawAutomationWindow,
                "活侠传日常自动化");
        }

        private void DrawAutomationWindow(int windowId)
        {
            _windowRect.height = Mathf.Min(720f, Mathf.Max(300f, Screen.height - 40f));
            _panelScroll = GUILayout.BeginScrollView(_panelScroll, false, true, GUILayout.Height(_windowRect.height - 45f));
            GUILayout.BeginVertical();
            GUILayout.Label("状态：" + (_running ? "运行中" : "已停止") + "｜" + _status);

            string toggleText = _running ? "停止自动化" : "开始自动化";
            if (GUILayout.Button(toggleText, GUILayout.Height(32f)))
            {
                if (_running)
                {
                    Stop("手动停止");
                }
                else
                {
                    StartAutomation();
                }
            }

            PlayerStatManagerData stats = GetStats();
            if (stats != null)
            {
                GUILayout.Label("当前行动点：" + stats.CurrentActionCount + " / " + stats.MaxActionCount);
            }

            bool alwaysFastForward = GUILayout.Toggle(
                _alwaysFastForward == null || _alwaysFastForward.Value,
                "常态化快进（已读文本继续，未读文本暂停）");
            if (_alwaysFastForward != null && alwaysFastForward != _alwaysFastForward.Value)
            {
                _alwaysFastForward.Value = alwaysFastForward;
                _fastForwardPausedForUnread = false;
                if (!alwaysFastForward)
                {
                    _resumeDailyAfterMenu = false;
                    _manualMenuSelected = false;
                }
                Config.Save();
                if (!alwaysFastForward)
                {
                    RestoreStoryFastForward();
                }
                _status = alwaysFastForward ? "常态化快进已开启" : "常态化快进已关闭";
            }
            GUILayout.Label("快进倍率：10x（游戏原生上限；更高倍率会被游戏取消，实测反而约 1x）");
            

            int panelContributionTarget = GetContributionTarget();
            if (panelContributionTarget > 0)
            {
                int panelContribution = ReadContribution();
                GUILayout.Label("地点子选项：“贡献优先”——贡献度 " + (panelContribution >= 0 ? panelContribution.ToString() : "?") +
                    " / 目标 " + panelContributionTarget + "（未达标优先工作，达标后优先自由）");
            }
            else
            {
                GUILayout.Label("地点子选项（固定偏好，缺失时自动改选另一档）：“" + GetPreferredInnerAction() + "”");
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("工作", GUILayout.Width(190f)))
            {
                SetInnerActionPreference("工作");
            }
            if (GUILayout.Button("自由", GUILayout.Width(190f)))
            {
                SetInnerActionPreference("自由");
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("大门轮换：下次从第 " + (GetDoorCursor() + 1) + " 项开始", GUILayout.Width(220f));
            if (GUILayout.Button("大门轮换重置", GUILayout.Width(125f)))
            {
                if (_doorOptionCursor != null)
                {
                    _doorOptionCursor.Value = 0;
                    Config.Save();
                }
                _status = "大门轮换进度已重置";
            }
            GUILayout.EndHorizontal();

            DrawDuelControls();
            GUILayout.Label("团体战（手动使用，默认按钮为友方获胜）");
            bool autoGroupWin = GUILayout.Toggle(
                _autoWinGroupBattleDuringFastForward == null || _autoWinGroupBattleDuringFastForward.Value,
                "常态快进遇到团体战时自动友方获胜");
            if (_autoWinGroupBattleDuringFastForward != null && autoGroupWin != _autoWinGroupBattleDuringFastForward.Value)
            {
                _autoWinGroupBattleDuringFastForward.Value = autoGroupWin;
                _autoGroupBattleHandled = false;
                Config.Save();
            }
            bool previousEnabled = GUI.enabled;
            GameLevelManager groupBattle = GetGroupBattleManager();
            GUILayout.Label(groupBattle == null
                ? "团体战状态：未检测到战斗管理器"
                : "团体战状态：" + (groupBattle.IsGameOver ? "已结算" : (groupBattle.IsReady ? "可结算" : "准备中")));
            // 按钮不因管理器暂时未就绪而变灰；点击后会在状态栏说明原因，
            // 方便在团体战载入、倒计时和结算边界判断实际状态。
            GUI.enabled = previousEnabled;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("友方获胜", GUILayout.Width(125f)))
            {
                TryResolveCurrentGroupBattle("友方获胜");
            }
            if (GUILayout.Button("敌方获胜", GUILayout.Width(125f)))
            {
                TryResolveCurrentGroupBattle("敌方获胜");
            }
            if (GUILayout.Button("时间到", GUILayout.Width(125f)))
            {
                TryResolveCurrentGroupBattle("时间到");
            }
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;

            DrawPanelAutomationSection();

            GUILayout.Label("流程顺序（上移/下移调整，重复地点表示重复执行）");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("流程顶部", GUILayout.Width(95f)))
            {
                _sequenceScroll.y = 0f;
                _panelScroll.y = 0f;
            }
            if (GUILayout.Button("流程底部", GUILayout.Width(95f)))
            {
                _sequenceScroll.y = 100000f;
                _panelScroll.y = 100000f;
            }
            if (GUILayout.Button("保存顺序", GUILayout.Width(95f)))
            {
                PersistActionOrder();
                _status = "已保存流程顺序";
            }
            if (GUILayout.Button("恢复默认", GUILayout.Width(95f)))
            {
                ResetSequence();
            }
            GUILayout.EndHorizontal();
            // 高度随条目数自适应：条目少时不留大片空白（此前固定 280px 会挡住下面的"添加地点"区块）。
            float sequenceHeight = _positions.Count == 0
                ? 42f
                : Math.Min(280f, _positions.Count * (_sequenceRowHeight + 4f) + 8f);
            _sequenceScroll = GUILayout.BeginScrollView(_sequenceScroll, false, false, GUILayout.Height(sequenceHeight));
            if (_positions.Count == 0)
            {
                GUILayout.Label("暂无任务，请在下方添加地点。");
            }
            else
            {
                for (int i = 0; i < _positions.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    string label = (i + 1) + ". " + _positions[i];
                    if (GUILayout.Button(label, GUILayout.Width(170f)))
                    {
                        _selectedSequenceIndex = i;
                    }

                    GUI.enabled = i > 0;
                    if (GUILayout.Button("上移", GUILayout.Width(48f)))
                    {
                        _pendingMoveFrom = i;
                        _pendingMoveTo = i - 1;
                    }

                    GUI.enabled = i + 1 < _positions.Count;
                    if (GUILayout.Button("下移", GUILayout.Width(48f)))
                    {
                        _pendingMoveFrom = i;
                        _pendingMoveTo = i + 1;
                    }

                    GUI.enabled = true;
                    if (GUILayout.Button("删除", GUILayout.Width(48f)))
                    {
                        _pendingRemoveIndex = i;
                    }
                    GUILayout.EndHorizontal();

                    float measuredRowHeight = GUILayoutUtility.GetLastRect().height;
                    if (measuredRowHeight > 8f && measuredRowHeight < 80f)
                    {
                        _sequenceRowHeight = measuredRowHeight;
                    }
                }
            }
            GUILayout.EndScrollView();

            // 列表结构变更延后到本帧布局完成后执行，避免 IMGUI 布局错位导致残留空白。
            if (_pendingRemoveIndex >= 0)
            {
                int removeIndex = _pendingRemoveIndex;
                _pendingRemoveIndex = -1;
                RemoveSequenceItem(removeIndex);
            }
            if (_pendingMoveFrom >= 0)
            {
                int moveFrom = _pendingMoveFrom;
                int moveTo = _pendingMoveTo;
                _pendingMoveFrom = -1;
                _pendingMoveTo = -1;
                MoveSequenceItem(moveFrom, moveTo);
            }

            GUILayout.Label("添加地点");
            for (int i = 0; i < KnownPositions.Length; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 3 && j < KnownPositions.Length; j++)
                {
                    PositionType position = KnownPositions[j];
                    if (GUILayout.Button(position.ToString(), GUILayout.Width(125f)))
                    {
                        AddSequenceItem(position);
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("F8：快速开始/停止　F7：显示/隐藏面板");
            GUILayout.Label("剧情中默认快进，遇到未读文本或新选项会暂停。");
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void AddSequenceItem(PositionType position)
        {
            _positions.Add(position);
            _selectedSequenceIndex = _positions.Count - 1;
            PersistActionOrder();
        }

        private void RemoveSequenceItem(int index)
        {
            if (index < 0 || index >= _positions.Count)
            {
                return;
            }

            _positions.RemoveAt(index);
            if (_selectedSequenceIndex >= _positions.Count)
            {
                _selectedSequenceIndex = _positions.Count - 1;
            }
            PersistActionOrder();
        }

        private void MoveSequenceItem(int from, int to)
        {
            if (from < 0 || from >= _positions.Count || to < 0 || to >= _positions.Count)
            {
                return;
            }

            PositionType item = _positions[from];
            _positions[from] = _positions[to];
            _positions[to] = item;
            _selectedSequenceIndex = to;
            PersistActionOrder();
        }

        private void ResetSequence()
        {
            _positions.Clear();
            _positions.AddRange(KnownPositions);
            _selectedSequenceIndex = -1;
            PersistActionOrder();
        }

        private void PersistActionOrder()
        {
            // 顺序被编辑（增删/移动/恢复默认）时从头开始执行。
            _sequenceIndex = 0;
            _actionOrder.Value = FormatPositionList();
            Config.Save();
        }

        private PositionType ReadPosition(PositionController controller)
        {
            try
            {
                return (PositionType)_positionField.GetValue(controller);
            }
            catch
            {
                return PositionType.無;
            }
        }

        private static bool CallBool(MethodInfo method, object instance)
        {
            if (method == null)
            {
                return false;
            }

            object result = method.Invoke(instance, null);
            return result is bool && (bool)result;
        }

        private string FormatPositionList()
        {
            return string.Join(",", _positions.ToArray());
        }

        private void Log(string message)
        {
            Logger.LogInfo("[DailyAutomation] " + message);
        }

        [HarmonyPatch(typeof(StoryManager), "SpeedTimeScale")]
        private static class StorySpeedTimeScalePatch
        {
            private static void Postfix()
            {
                if (_instance != null && _instance.ShouldAlwaysFastForward())
                {
                    // 游戏原生 SpeedTimeScale() 每次只写回 10；在插件开启常态快进时，
                    // 立即用用户设置覆盖，避免下一帧又被原生逻辑降回 10 倍。
                    Time.timeScale = _instance.GetFastForwardTimeScale();
                }
            }
        }

        [HarmonyPatch]
        private static class StoryTextPatch
        {
            private static MethodBase TargetMethod()
            {
                MethodInfo[] methods = typeof(LuaManager).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == "GetStoryText")
                    {
                        ParameterInfo[] parameters = methods[i].GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                        {
                            return methods[i];
                        }
                    }
                }
                return null;
            }

            private static void Prefix(string __0, ref object __state)
            {
                __state = null;
                if (_instance == null || (!_instance._running && !_instance.ShouldAlwaysFastForward()))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(__0) && __0.StartsWith("__LOMS__", StringComparison.Ordinal))
                {
                    // “活侠传助手”插件把内部 KV/哈希服务借道 GetStoryText 调用
                    //（例如菜单选择记录 LOMS_Hash("Choose_...")），并非剧情文本，直接忽略。
                    return;
                }

                try
                {
                    bool previousSkipUnread = SystemSettings.SkipUnreadStory;
                    __state = previousSkipUnread;
                    SystemSettings.SetSkipUnreadStory(false);

                    ReadStorySystem readStory = ReadStorySystem.Instance;
                    bool isUnread = readStory == null || !readStory.ExistKey(__0);
                    _instance.OnStoryTextRequested(__0, isUnread);
                }
                catch (Exception ex)
                {
                    _instance.Stop("无法判断文本是否已读，已停止自动化");
                    _instance.Logger.LogError("未读文本检测失败：" + ex);
                }
            }

            private static void Postfix(object __state)
            {
                if (__state is bool)
                {
                    SystemSettings.SetSkipUnreadStory((bool)__state);
                }
            }
        }

        [HarmonyPatch]
        private static class MenuOptionPatch
        {
            private static MethodBase TargetMethod()
            {
                // 菜单按钮的唯一创建入口：AddOption(string, bool, bool, UnityAction)。
                // 它是非虚方法；CustomMenuDialog 的闭包版（覆写基类虚方法）与故事菜单最终都会
                // 经由基类闭包版调用它。每次调用按顺序占用一个按钮槽位
                //（cachedButtons[nextOptionIndex] 后自增），因此捕获顺序 == 按钮槽位顺序。
                MethodInfo[] methods = typeof(Fungus.MenuDialog).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "AddOption")
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 4 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(bool) &&
                        parameters[3].ParameterType.Name == "UnityAction")
                    {
                        return method;
                    }
                }

                return null;
            }

            private static void Prefix(ref UnityEngine.Events.UnityAction __3)
            {
                if (_instance == null || __3 == null)
                {
                    return;
                }
                UnityEngine.Events.UnityAction original = __3;
                __3 = delegate
                {
                    if (_instance != null)
                    {
                        _instance.OnManualMenuSelected();
                    }
                    original();
                };
            }

            private static void Postfix(string __0, bool __1, bool __result, object __instance)
            {
                Fungus.MenuDialog menu = __instance as Fungus.MenuDialog;
                if (!__result || menu == null || _instance == null || !_instance._running)
                {
                    return;
                }

                _instance.OnMenuOptionAdded(menu, __0, __1);
            }
        }

        [HarmonyPatch(typeof(CustomMenuDialog), "OnPanelOpen")]
        private static class PositionMenuOpenPatch
        {
            private static void Postfix(CustomMenuDialog __instance)
            {
                if (_instance != null && _instance._running && __instance != null)
                {
                    _instance.OnPositionMenuOpened(__instance);
                }
            }
        }
    }
}
