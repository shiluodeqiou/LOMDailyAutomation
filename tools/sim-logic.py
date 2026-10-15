import json, os

d = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'game-data.json'), encoding='utf-8'))
levels = d['levels']; books = d['books']; talents = d['talents']

def level_medical(lpid):
    lv = levels.get(str(lpid))
    if not lv: return False
    for st in lv['stats']:
        if st['type'] == 3:
            ti = talents.get(str(st['tal']))
            if ti and ti['id'] == '2101': return True
    for c in lv['cons']:
        for ci in c['items']:
            if ci['type'] == 3:
                ti = talents.get(str(ci['tal']))
                if ti and ti['id'] == '2101': return True
    return False

# user's owned books and assumed current level index (0-based), matching logged needs
owned = {
    '1000': 2,   # need 2
    '1010': 0,
    '4010': 3,   # need 6
    '4030': 0,
    '6001': 0,
    '8000': 0,
    '8005': 0,
    '8101': 0,
    '8102': 0,
}

# map book id -> level array (list of per-level point costs) and cond_fail flag
bookdata = {}
for pid, b in books.items():
    if b['id'] in owned:
        costs = [levels[str(p)]['points'] for p in b['mld']]
        med = [level_medical(p) for p in b['mld']]
        bookdata[b['id']] = dict(costs=costs, med=med, name=b['asset'])

bookdata['8100'] = dict(costs=[5]*10, med=[False]*9+[True], name='Book8100_溫陽活血符', cond_fail=True)

def find_focus(bid, lvl):
    bd = bookdata[bid]
    for i in range(lvl, len(bd['med'])):
        if bd['med'][i]:
            return i
    return -1

def simulate(mode, total_points, log_limit=40):
    state = {bid: [owned.get(bid, 0), 0] for bid in owned}
    points = total_points
    decisions = []
    gained_medical = 0
    rounds = 0
    while points > 0 and rounds < 500:
        rounds += 1
        best = None; best_need = 1 << 30; best_focus = 1 << 30; best_is_focus = False
        for bid in sorted(owned.keys()):
            lvl, cur = state[bid]
            bd = bookdata[bid]
            if lvl >= len(bd['costs']):
                continue  # max level
            need = bd['costs'][lvl] - cur
            if mode == 'new':
                fl = find_focus(bid, lvl)
                isf = fl >= 0
            else:
                isf = bd['med'][lvl] if mode == 'old' else False
                fl = lvl if isf else -1
            if mode == 'old':
                # old logic: focus only if current level grants medical
                if best is not None and ((best_is_focus and not isf) or (best_is_focus == isf and need >= best_need)):
                    continue
            else:
                better = (best is None or
                          (isf and not best_is_focus) or
                          (isf == best_is_focus and isf and (fl < best_focus or (fl == best_focus and need < best_need))) or
                          (isf == best_is_focus and not isf and need < best_need))
                if not better:
                    continue
            best, best_need, best_focus, best_is_focus = bid, need, fl, isf
        invest = min(best_need, points)
        lvl, cur = state[best]
        cur += invest
        points -= invest
        bd = bookdata[best]
        leveled = False
        if cur >= bd['costs'][lvl]:
            lvl += 1; cur = 0; leveled = True
            if bd['med'][lvl-1]:
                gained_medical += 1
        state[best] = [lvl, cur]
        if len(decisions) < log_limit:
            decisions.append('%s 投入%d点%s%s' % (bd['name'], invest, '（升级！）' if leveled else '',
                '（目标功法·到第%d级获得）' % (find_focus(best, state[best][0]-1 if leveled else state[best][0])+1) if (find_focus(best, max(0,state[best][0]-(1 if leveled else 0))) >= 0) else ''))
    return decisions, gained_medical, state

print('======== 旧逻辑（0.4.9-0.5.1）：只看当前级是否加医术 ========')
dec, med, state = simulate('old', 50)
for dline in dec: print('  ', dline)
print('  50 点投入后医术获得次数:', med)

print()
print('======== 新逻辑（0.5.2）：当前级及以后整本书判定 ========')
dec, med, state = simulate('new', 50)
for dline in dec: print('  ', dline)
print('  50 点投入后医术获得次数:', med)
print('  510100 等各书最终状态:', {k: v for k, v in sorted(state.items())})
