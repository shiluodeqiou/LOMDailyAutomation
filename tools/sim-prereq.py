# Simulate 0.5.3 martial-panel selection incl. prerequisite tier (model, not the real code).
import json, os

d = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'game-data.json'), encoding='utf-8'))
levels = d['levels']; books = d['books']; talents = d['talents']; gamestats = d['gamestats']

MEDICAL_TALENT = '2101'
INTERNAL_STAT_TYPE = 28  # 陰陽內功

def level_grants_medical(lpid):
    lv = levels.get(str(lpid))
    if not lv: return False
    for st in lv['stats']:
        if st['type'] == 3:
            ti = talents.get(str(st['tal']))
            if ti and ti['id'] == MEDICAL_TALENT: return True
    return False

def level_grants_internal(lpid):
    lv = levels.get(str(lpid))
    if not lv: return 0
    total = 0
    for st in lv['stats']:
        if st['type'] == 1:
            gs = gamestats.get(str(st['stat']))
            if gs and gs['type'] == INTERNAL_STAT_TYPE and st['val'] > 0:
                total += st['val']
    return total

def load_book(bid):
    b = next(bb for bb in books.values() if bb['id'] == bid)
    return dict(
        id=bid, name=b['asset'],
        costs=[levels[str(p)]['points'] for p in b['mld']],
        med=[level_grants_medical(p) for p in b['mld']],
        internal=[level_grants_internal(p) for p in b['mld']],
        maxlevel=len(b['mld']),
    )

USER_BOOKS = ['1000', '1010', '4010', '4030', '6001', '8000', '8005', '8100', '8101', '8102']
HELPER = '7010'  # 雪山心法 +2 陰陽內功/级

def simulate(owned_ids, start_levels, internal_value, total_points, label):
    print('==== %s ====' % label)
    bookdata = {bid: load_book(bid) for bid in owned_ids}
    # 8100's condition: 陰陽內功 >= 60 (on every level)
    def learnable(bid, lvl):
        if bid == '8100':
            return internal >= 60
        return True

    state = {bid: [start_levels.get(bid, 0), 0] for bid in owned_ids}
    points = total_points
    internal = internal_value
    med_total = 0
    steps = 0
    while points > 0 and steps < 400:
        steps += 1
        # 前置辅助只在"有被卡住的目标书"时生效（与插件一致）
        prereq_active = ('8100' in owned_ids and state['8100'][0] < bookdata['8100']['maxlevel']
                         and internal < 60)
        candidates = []
        for bid in sorted(owned_ids):
            bd = bookdata[bid]
            lvl, cur = state[bid]
            if lvl >= bd['maxlevel']:
                continue
            if not learnable(bid, lvl):
                continue
            need = bd['costs'][lvl] - cur
            focus_level = next((i for i in range(lvl, bd['maxlevel']) if bd['med'][i]), -1)
            is_focus = focus_level >= 0
            prereq_level = -1
            if not is_focus and prereq_active:
                prereq_level = next((i for i in range(lvl, bd['maxlevel']) if bd['internal'][i] > 0), -1)
            rank = 0 if is_focus else (1 if prereq_level >= 0 else 2)
            level_key = focus_level if is_focus else (prereq_level if prereq_level >= 0 else 0)
            candidates.append((rank, level_key, need, bid))
        if not candidates:
            print('  (无候选，停止)'); break
        # pick: rank asc, level_key asc, need asc, id asc
        rank, level_key, need, bid = sorted(candidates)[0]
        bd = bookdata[bid]
        lvl, cur = state[bid]
        invest = min(need, points)
        cur += invest; points -= invest
        leveled = cur >= bd['costs'][lvl]
        tag = '（目标功法）' if rank == 0 else ('（前置功法·提升陰陽內功）' if rank == 1 else '')
        if leveled:
            if bd['med'][lvl]: med_total += 1
            internal += bd['internal'][lvl]
            state[bid] = [lvl + 1, 0]
            print('  %s 投入%d点（升级！）→ 陰陽內功=%d%s' % (bd['name'], invest, internal, tag))
            if bid == '8100' and lvl == bd['maxlevel'] - 1:
                pass
        else:
            state[bid] = [lvl, cur]
            print('  %s 投入%d点%s' % (bd['name'], invest, tag))
    print('  >> 消耗 %d 点，医术总计 +%d，最终陰陽內功=%d，剩 %d 点' % (total_points - points, med_total, internal, points))
    print()

# A. 用户当前书单，没有能加陰陽內功的书
simulate(USER_BOOKS, {}, 20, 120, 'A. 当前书单（无陰陽內功来源）：预期只修 8101/8102')

# B. 拥有 7010 雪山心法（+2/级），陰陽內功从 55 开始
simulate(USER_BOOKS + [HELPER], {}, 55, 250, 'B. 拥有雪山心法、陰陽內功=55：预期 8101/8102 满级后修 7010 → 过 60 后转修 8100')
