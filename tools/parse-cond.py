import UnityPy, struct, os, json

base = r'D:\APP\Steam\steamapps\common\LegendOfMortal\Mortal_Data'
env = UnityPy.load(os.path.join(base, 'globalgamemanagers.assets'))

mono_scripts = {}
for obj in env.objects:
    if obj.type.name == 'MonoScript':
        try:
            d = obj.read()
            mono_scripts[obj.path_id] = d.m_ClassName or ''
        except Exception:
            pass

class R:
    def __init__(self, raw): self.raw = raw; self.o = 0
    def i(self):
        v = struct.unpack_from('<i', self.raw, self.o)[0]; self.o += 4; return v
    def b(self):
        v = self.raw[self.o] != 0; self.o += 4; return v
    def s(self):
        n = struct.unpack_from('<i', self.raw, self.o)[0]; self.o += 4
        if n < 0 or self.o + n > len(self.raw): raise ValueError('bad str %d' % n)
        v = self.raw[self.o:self.o+n].decode('utf-8', 'replace'); self.o += n; self.o = (self.o+3) & ~3
        return v
    def p(self):
        fid = struct.unpack_from('<i', self.raw, self.o)[0]; pid = struct.unpack_from('<q', self.raw, self.o+4)[0]; self.o += 12
        return (fid, pid)

raws = {}
for obj in env.objects:
    raws[obj.path_id] = obj.get_raw_data()

def class_of(pid):
    raw = raws.get(pid)
    if raw is None or len(raw) < 28: return None
    fid, spid = struct.unpack_from('<i', raw, 16)[0], struct.unpack_from('<q', raw, 20)[0]
    return mono_scripts.get(spid) if fid == 0 else None

data = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'game-data.json'), encoding='utf-8'))
levels = data['levels']; books = data['books']; gamestats = data['gamestats']
talents = data['talents']
rels = data['rels']

GST = {0:'體力',1:'內力',2:'輕功',3:'銀兩',4:'魅力',5:'學問',6:'心理衛生',7:'命運',8:'性情',9:'處世',10:'修養',11:'道德',12:'嘴力',13:'門派規模',14:'門派名聲',15:'門派人數',16:'向心力',17:'鍛造',18:'毒藥',19:'行動次數',20:'個人貢獻度',21:'廚藝',22:'抗毒',23:'抗麻',24:'稱號',25:'愛人',26:'額外行動次數_1',27:'娘化',28:'陰陽內功',29:'防禦',30:'變心',31:'武學點數',32:'全武學點數',33:'門派資產',34:'門派貢獻',36:'全鍛造點數',37:'全毒藥點數',38:'全貢獻度',39:'額外行動次數_2',40:'額外行動次數_3',100:'武功刀劍',101:'武功暗器',102:'武功拳掌',103:'武功腿法',104:'武功奇門',105:'武功軟兵器',106:'武功槍棍',107:'武功內功',201:'儒學',202:'道學',203:'佛學',301:'戰役預設血量',401:'攻擊爆擊',402:'暗器爆擊'}
OPS = {0:'==',1:'!=',2:'<',3:'<=',4:'>',5:'>='}

def describe_ref(st):
    t = st[0]
    if t == 1:
        gs = gamestats.get(str(st[1]))
        nm = GST.get(gs['type'], '?') if gs else 'stat?' + str(st[1])
        return '角色數值[' + nm + ']'
    if t == 2:
        r = rels.get(str(st[1]))
        return '好感度[' + (r['note'] if r else str(st[1])) + ']'
    if t == 3:
        ti = talents.get(str(st[1]))
        return '天賦[' + (ti['asset'] if ti else str(st[1])) + ']'
    return 'type%d[%s]' % (t, st[1])

def parse_condition(pid):
    raw = raws.get(pid)
    if raw is None: return None
    r = R(raw)
    r.p(); r.b(); r.p(); r.s()  # header
    groups = []
    n = r.i()
    for _ in range(n):
        dev = r.s()
        cnt = r.i()
        items = []
        for _ in range(cnt):
            op = r.i()
            stype = r.i()
            ps = r.p(); pr = r.p(); pb = r.p(); pm = r.p(); psp = r.p(); ptl = r.p(); pc = r.p()
            val = r.i()
            items.append((op, stype, ps[1], pr[1], pb[1], pm[1], psp[1], ptl[1], val))
        groups.append((dev, items))
    return groups

# 8100's level conditions + 1000 (对照)
for bid in ['8100', '8101', '8102', '1000']:
    b = next(bb for bb in books.values() if bb['id'] == bid)
    print('=== Book', bid, b['asset'])
    for lp in b['mld']:
        lv = levels.get(str(lp))
        cond = lv.get('cond')
        lvlname = lv['asset'] if lv else lp
        if not cond:
            print('   %s : 无条件' % lvlname)
            continue
        g = parse_condition(cond)
        if g is None:
            print('   %s : cond pid=%s 解析失败' % (lvlname, cond)); continue
        txt = []
        for dev, items in g:
            for (op, stype, ps, pr, pb, pm, psp, ptl, val) in items:
                ref = describe_ref((stype, ptl if stype == 3 else (ps if stype == 1 else (pr if stype == 2 else pb))))
                txt.append('%s %s %d' % (ref, OPS.get(op, '?'), val))
        print('   %s : %s' % (lvlname, ' 且 '.join(txt) if txt else '(空条件组)'))
