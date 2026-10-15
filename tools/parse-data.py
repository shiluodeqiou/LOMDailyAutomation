# Parse Mortal.Core ScriptableObject data from globalgamemanagers.assets
import UnityPy, struct, os, json, sys

base = r'D:\APP\Steam\steamapps\common\LegendOfMortal\Mortal_Data'
env = UnityPy.load(os.path.join(base, 'globalgamemanagers.assets'))

class R:
    def __init__(self, raw):
        self.raw = raw
        self.o = 0
    def i(self):
        v = struct.unpack_from('<i', self.raw, self.o)[0]; self.o += 4; return v
    def b(self):
        v = self.raw[self.o] != 0; self.o += 4; return v
    def f(self):
        v = struct.unpack_from('<f', self.raw, self.o)[0]; self.o += 4; return v
    def s(self):
        n = struct.unpack_from('<i', self.raw, self.o)[0]; self.o += 4
        if n < 0 or self.o + n > len(self.raw):
            raise ValueError('bad string len %d at %d' % (n, self.o))
        v = self.raw[self.o:self.o+n].decode('utf-8', 'replace'); self.o += n
        self.o = (self.o + 3) & ~3
        return v
    def p(self):
        fid = struct.unpack_from('<i', self.raw, self.o)[0]
        pid = struct.unpack_from('<q', self.raw, self.o+4)[0]
        self.o += 12
        return (fid, pid)

obj_raw = {}
mono_scripts = {}
path_type = {}
for obj in env.objects:
    path_type[obj.path_id] = obj.type.name
    if obj.type.name == 'MonoScript':
        try:
            d = obj.read()
            mono_scripts[obj.path_id] = d.m_ClassName or ''
        except Exception:
            pass

def load_raw(pid):
    if pid not in obj_raw:
        for obj in env.objects:
            if obj.path_id == pid:
                obj_raw[pid] = obj.get_raw_data()
                break
    return obj_raw.get(pid)

def class_of(pid):
    raw = load_raw(pid)
    if raw is None or len(raw) < 28:
        return None
    fid, spid = struct.unpack_from('<i', raw, 16)[0], struct.unpack_from('<q', raw, 20)[0]
    if fid == 0:
        return mono_scripts.get(spid)
    return None

# collect by class
by_class = {}
for obj in env.objects:
    if obj.type.name != 'MonoBehaviour':
        continue
    c = class_of(obj.path_id)
    if c:
        by_class.setdefault(c, []).append(obj.path_id)

print('== class counts ==')
for c in ['Book','MartialLevelData','PlayerTalentData','GameStat','RelationshipStat','MartialLearnCondition','BookLevelStatConvertData','BattleSkillData']:
    print('  %-28s %d' % (c, len(by_class.get(c, []))))

def hdr(r):
    r.p()  # m_GameObject
    r.b()  # m_Enabled
    r.p()  # m_Script
    return r.s()  # m_Name

def parse_ptr_arr(r):
    n = r.i()
    return [r.p() for _ in range(n)]

def parse_talents():
    out = {}
    for pid in by_class.get('PlayerTalentData', []):
        raw = load_raw(pid)
        r = R(raw)
        try:
            name = hdr(r)
            tid = r.s()
            combat = r.b()
            ckey = r.s()
            battle = r.b()
            level = r.i()
            disp = r.b()
            maxl = r.i()
            prio = r.i()
            lows = parse_ptr_arr(r)
            if r.o != len(raw):
                print('  ! talent end mismatch %s: %d vs %d' % (tid, r.o, len(raw)))
        except Exception as e:
            print('  ! talent parse error pid=%s: %s' % (pid, e))
            continue
        out[pid] = dict(id=tid, asset=name, combat=combat, ckey=ckey, battle=battle, lvl=level, disp=disp, max=maxl, prio=prio, low=[p[1] for p in lows])
    return out

def parse_gamestats():
    out = {}
    for pid in by_class.get('GameStat', []):
        raw = load_raw(pid)
        r = R(raw)
        try:
            name = hdr(r)
            stype = r.i()
            prop = r.i()
            sname = r.s()
            val = r.i()
            mn = r.i()
            mx = r.i()
            lim = r.i()
            dflt = r.i()
            tot = r.p()
            lvltext = r.i()
            for _ in range(lvltext): r.s()
            add = parse_ptr_arr(r)
            if r.o != len(raw):
                print('  ! gamestat end mismatch %s: %d vs %d' % (sname, r.o, len(raw)))
        except Exception as e:
            print('  ! gamestat parse error pid=%s: %s' % (pid, e))
            continue
        out[pid] = dict(type=stype, prop=prop, name=sname, val=val, min=mn, max=mx, asset=name)
    return out

def parse_relationships():
    out = {}
    for pid in by_class.get('RelationshipStat', []):
        raw = load_raw(pid)
        r = R(raw)
        try:
            name = hdr(r)
            rtype = r.i()
            note = r.s()
            val = r.i()
            dflt = r.i()
            mx = r.i()
            act = r.b()
            icon = r.p()
            head = r.p()
            # AssetReferenceCache: skip - dump remaining
        except Exception as e:
            print('  ! rel parse error pid=%s: %s' % (pid, e))
            continue
        out[pid] = dict(type=rtype, note=note, name=name)
    return out

def parse_level_data():
    out = {}
    for pid in by_class.get('MartialLevelData', []):
        raw = load_raw(pid)
        r = R(raw)
        try:
            name = hdr(r)
            points = r.i()
            cond = r.p()
            n = r.i()
            stats = []
            for _ in range(n):
                stype = r.i()
                pstat = r.p(); prel = r.p(); pbook = r.p(); pmisc = r.p(); pspec = r.p(); ptal = r.p(); pcol = r.p()
                val = r.i()
                center = r.b()
                stats.append(dict(type=stype, stat=pstat[1], rel=prel[1], book=pbook[1], misc=pmisc[1], spec=pspec[1], tal=ptal[1], col=pcol[1], val=val, center=center))
            cons = []
            cn = r.i()
            for _ in range(cn):
                lt = r.i()
                inn = r.i()
                cl = []
                for _ in range(inn):
                    stype = r.i()
                    pstat = r.p(); prel = r.p(); pbook = r.p(); pmisc = r.p(); pspec = r.p(); ptal = r.p(); pcol = r.p()
                    val = r.i()
                    center = r.b()
                    cl.append(dict(type=stype, tal=ptal[1], stat=pstat[1], val=val))
                cons.append(dict(logic=lt, items=cl))
            if r.o != len(raw):
                print('  ! leveldata end mismatch %s: %d vs %d' % (name, r.o, len(raw)))
        except Exception as e:
            print('  ! leveldata parse error pid=%s: %s' % (pid, e))
            continue
        out[pid] = dict(asset=name, points=points, cond=cond[1], stats=stats, cons=cons)
    return out

def parse_books():
    out = {}
    for pid in by_class.get('Book', []):
        raw = load_raw(pid)
        r = R(raw)
        try:
            name = hdr(r)
            dev = r.s()
            bid = r.s()
            img = r.p()
            cost = r.i()
            cnt = r.i()
            apn = r.i()
            if apn != 0:
                print('  ! book %s has %d applyMissionStat (skip parse)' % (bid, apn))
                continue
            martial = r.b()
            complete = r.b()
            parts = r.i()
            for _ in range(parts): r.s()
            compid = r.s()
            evptr = r.p()
            mtype = r.i()
            facn = r.i()
            facs = [r.i() for _ in range(facn)]
            mlevel = r.i()
            cmlevel = r.i()
            clp = r.i()
            mld = parse_ptr_arr(r)
            rsc = parse_ptr_arr(r)
            if r.o != len(raw):
                print('  ! book end mismatch %s: %d vs %d' % (bid, r.o, len(raw)))
        except Exception as e:
            print('  ! book parse error pid=%s: %s' % (pid, e))
            continue
        out[pid] = dict(asset=name, dev=dev, id=bid, martial=martial, complete=complete,
                        parts=parts, compid=compid, mtype=mtype, facs=facs, mlevel=mlevel,
                        cmlevel=cmlevel, clp=clp, mld=[p[1] for p in mld], rsc=[p[1] for p in rsc])
    return out

talents = parse_talents()
gamestats = parse_gamestats()
rels = parse_relationships()
levels = parse_level_data()
books = parse_books()

print()
print('== PlayerTalentData: combatSkill=true ==')
for pid, t in sorted(talents.items(), key=lambda kv: kv[1]['id']):
    if t['combat']:
        print('  %-14s key=%-14s battle=%s lvl=%d max=%d asset=%s' % (t['id'], t['ckey'], t['battle'], t['lvl'], t['max'], t['asset']))

print()
print('== PlayerTalentData: all ids (first 160) ==')
ids = sorted(set(t['id'] for t in talents.values()))
print('  ' + ', '.join(ids))

out = dict(talents={str(k): v for k, v in talents.items()},
           gamestats={str(k): v for k, v in gamestats.items()},
           rels={str(k): v for k, v in rels.items()},
           levels={str(k): v for k, v in levels.items()},
           books={str(k): v for k, v in books.items()})
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'game-data.json'), 'w', encoding='utf-8') as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
print()
print('saved game-data.json')
