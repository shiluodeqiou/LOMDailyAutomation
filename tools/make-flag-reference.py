# -*- coding: utf-8 -*-
"""生成 旗标(FlagData) → 剧情脚本 的对照表。

数据源：logs/game-scripts/*.lua（从 resources.assets 提取的 2684 个剧本）。
输出：logs/flag-reference.md
"""
import os
import re
from collections import defaultdict

here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
scripts_dir = os.path.join(here, 'logs', 'game-scripts')
out_path = os.path.join(here, 'logs', 'flag-reference.md')

# 旗标相关的调用形态（剧本里出现过的）：SetFlag/AddFlag/GetFlag/Condition/Switch/Flag
call_re = re.compile(r'(?:SetFlag|AddFlag|GetFlag|Condition|Switch|Flag|Position)\s*\(\s*"([^"]+)"')

flag_scripts = defaultdict(list)

files = [f for f in os.listdir(scripts_dir) if f.endswith('.lua')]
for fn in sorted(files):
    try:
        text = open(os.path.join(scripts_dir, fn), encoding='utf-8', errors='ignore').read()
    except Exception:
        continue
    seen = set()
    for m in call_re.finditer(text):
        flag = m.group(1)
        if flag not in seen:
            seen.add(flag)
            flag_scripts[flag].append(fn)

# 去掉明显不是旗标的键（本地化文本键等一般形如 O_xxx / N_xxx / D_xxx，保留也无妨，单列出来）
def looks_like_flag(name):
    return re.match(r'^[A-Za-z]{1,4}[_0-9][A-Za-z0-9_]*$', name) is not None

def base(script_name):
    return script_name.replace('.1.lua', '').replace('.lua', '')

lines = []
lines.append('# 旗标对照表（旗标 → 出现它的剧情脚本）')
lines.append('')
lines.append('> 自动生成：`tools/make-flag-reference.py`。旗标名取自剧本里的 SetFlag/AddFlag/GetFlag/Condition/Switch/Flag/Position 调用。')
lines.append('> 想查某个旗标是什么事件：在 `logs/game-scripts/` 里打开下面列出的脚本，搜索旗标名即可看到上下文。')
lines.append('')

flags = sorted(flag_scripts.keys())
flag_like = [f for f in flags if looks_like_flag(f)]

# 前缀分组统计
groups = defaultdict(list)
for f in flag_like:
    m = re.match(r'^([A-Za-z]+)', f)
    groups[m.group(1) if m else '?'].append(f)

lines.append('## 前缀分组')
lines.append('')
lines.append('| 前缀 | 数量 | 示例 |')
lines.append('| --- | --- | --- |')
for g in sorted(groups.keys(), key=lambda k: (-len(groups[k]), k)):
    sample = ', '.join(groups[g][:6])
    lines.append('| %s | %d | %s |' % (g, len(groups[g]), sample))
lines.append('')

lines.append('## 全部旗标')
lines.append('')
lines.append('| 旗标 | 出现脚本（去重，最多 10 个） |')
lines.append('| --- | --- |')
for f in flags:
    scripts = sorted(set(base(s) for s in flag_scripts[f]))
    shown = ', '.join(scripts[:10])
    if len(scripts) > 10:
        shown += ' …(+%d)' % (len(scripts) - 10)
    lines.append('| `%s` | %s |' % (f, shown))
lines.append('')

open(out_path, 'w', encoding='utf-8').write('\n'.join(lines))
print('flags total:', len(flags), '| flag-like:', len(flag_like))
print('written ->', out_path)
