using System;
using HarmonyLib;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;

namespace LOMDailyAutomation
{
    public sealed partial class DailyAutomationPlugin
    {
        private CombatManager _settlingDuel;

        private void DrawDuelControls()
        {
            GUILayout.Label("对决：");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("直接获胜", GUILayout.Width(190f)))
            {
                ResolveDuel(true);
            }
            if (GUILayout.Button("投降", GUILayout.Width(190f)))
            {
                ResolveDuel(false);
            }
            GUILayout.EndHorizontal();
        }

        private void ResolveDuel(bool win)
        {
            CombatManager combat = CombatManager.Instance;
            CombatUIManager ui = CombatUIManager.Instance;
            if (combat == null || ui == null || combat.CombatLevel == null)
            {
                _status = "当前没有对决";
                Log(_status);
                return;
            }
            if (_settlingDuel == combat)
            {
                _status = "对决已在结算中";
                Log(_status);
                return;
            }

            try
            {
                if (win)
                {
                    // 与游戏原生投降使用同一条结算通路，仅把 GameOver 的胜负参数改为 true。
                    CommonPanel menu = AccessTools.Field(typeof(CombatUIManager), "_menuPanel").GetValue(ui) as CommonPanel;
                    if (menu == null)
                    {
                        _status = "无法读取对决菜单，未执行获胜";
                        return;
                    }
                    combat.EnableHotKey = false;
                    menu.Show(false);
                    ui.StartCoroutine(combat.GameOver(true));
                }
                else
                {
                    ui.Giveup();
                }

                _settlingDuel = combat;
                _lastProgressAt = Time.unscaledTime;
                _status = win ? "已执行对决直接获胜" : "已执行对决投降";
                Log(_status);
            }
            catch (Exception ex)
            {
                _status = "对决结算失败";
                Logger.LogError("手动对决结算失败：" + ex);
            }
        }

        [HarmonyPatch(typeof(CombatManager), "GameOver")]
        private static class DuelGameOverPatch
        {
            private static void Postfix(CombatManager __instance)
            {
                if (_instance != null)
                {
                    // 包括原生结算及其他插件触发的结算，防止本面板重复执行奖励/结局。
                    _instance._settlingDuel = __instance;
                }
            }
        }

        [HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
        private static class DuelRoundPatch
        {
            private static void Prefix(CombatManager __instance)
            {
                if (_instance != null && _instance._settlingDuel == __instance)
                {
                    _instance._settlingDuel = null;
                }
            }
        }
    }
}
