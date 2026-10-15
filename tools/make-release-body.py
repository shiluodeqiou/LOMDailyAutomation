# -*- coding: utf-8 -*-
"""生成 GitHub Release 的请求体（release-body.json）。"""
import hashlib
import json
import os
from urllib.parse import quote

here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
zip_path = os.path.join(here, 'dist', 'LOMDailyAutomation-v2026.10.16.zip')
sha = hashlib.sha256(open(zip_path, 'rb').read()).hexdigest()

# 发布页顶部的面板截图（仓库 main 分支里的展示图片）
shot = ('https://raw.githubusercontent.com/shiluodeqiou/LOMDailyAutomation/main/'
        + quote('展示图片/日常自动化1.png'))

body = """**《活侠传》自由行动日常自动化：F8 一按，日常自己跑。**

![F7 面板](%s)

## 安装

**推荐**：下载下面的 `LOMDailyAutomation-v2026.10.16.zip`，解压到**任意位置**，双击「点我安装.bat」即可 —— 安装包已自带 BepInEx 运行时，之前什么 mod 都没装过也能直接玩，不必先去别处下载。

**手工安装**：把压缩包里的内容复制到游戏根目录（`Mortal.exe` 所在文件夹）。装好后游戏根目录应有 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\\core\\`、`BepInEx\\plugins\\LOMDailyAutomation\\`。注意别用会新建子文件夹的解压方式。

确认装上了：启动一次游戏，`BepInEx\\LogOutput.log` 里会出现「活侠传日常自动化已加载。按 F8 开始/停止，按 F7 显示/隐藏面板。」

## 用法：记两个键就够

| 按键 | 作用 |
| --- | --- |
| **F8** | **开始 / 停止自动化**（在自由行动界面按一下就开始） |
| **F7** | **显示 / 隐藏自动化面板**（流程顺序、修炼目标、快进开关都在这个面板里） |

按一下 **F8**，剩下的自动完成：地点照面板里的流程顺序循环跑，点进去后的工作/自由、练功、打铁炼丹加点、睡觉过天、大门轮换都会自动处理；遇到没读过的剧情或需要选择的地方会自动停下来，处理完再按 **F8** 继续。

## 功能一览

- **地点循环**：按面板顺序执行正心堂/校场/炼丹房/锻冶场/后山/弟子房/讲经堂/伙房/大门…，行动点耗尽自动收工
- **子选项自动选择**：默认贡献优先（个人贡献度未到 60 先干工作，达标后转自由），面板可调
- **武学面板自动修炼**：关注目标（含「医术」）优先；目标加成放在满级那一级的书也能正确识别；被前置条件卡住时自动先修炼能补前置的书
- **打铁 / 炼丹**：升级面板自动加点，点满自动进化解锁下一项，处理完自动关闭
- **全部加满 → 自动睡觉 → 下一天**：夜晚菜单按心性收益自动选择
- **大门轮换**：每进一次大门按列表顺序选下一项（闹市/外堡/茶肆/药材铺/…），所有出行与随机事件都会轮到、不会错过；进度持久化，面板可重置
- **常态化快进**（固定游戏原生 10x）：已读文本继续快进，未读文本与选项自动暂停
- **暴死自动回档**：突然死亡时自动读取自动存档回到上一操作前并继续；同一处连死 3 次会停下等人工
- **手动工具**：团体战「友方获胜 / 敌方获胜 / 时间到」、对决「直接获胜 / 投降」，随时可点

---
版本 `v2026.10.16`（首个公开发布）· MIT 许可 · 源码与说明见仓库主页 · 安装包 SHA256：`%s`
""" % (shot, sha)

out = {
    'tag_name': 'v2026.10.16',
    'target_commitish': 'main',
    'name': 'v2026.10.16',
    'body': body,
    'draft': False,
    'prerelease': False,
}
open(os.path.join(here, 'release-body.json'), 'w', encoding='utf-8').write(json.dumps(out, ensure_ascii=False))
print('release-body.json written, zip sha256 =', sha)
