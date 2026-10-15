# -*- coding: utf-8 -*-
"""
修复“在等待选择时存档”导致的读档卡死（活侠传 + 活侠传助手增强存档）。

原理：助手的增强存档把每次 choose/say 记成 KV（key = MD5("<Say_|Choose_>" + 文本/选项串)）。
如果在菜单等待输入时存档，这次 choose 的 KV 还没写 → 读档重放时选项键缺失 →
游戏弹出菜单等待输入、加载遮罩不消失（表现为一直“读取中”）。

修复：向存档 .ss（JSON）的 KV 里补上缺少的选择键（值 = 选择序号，取最小可选项最安全）。

用法：
  python repair-save-menu-choose.py <Save_0XX.dat.ss> --kind door [--index 1]
  python repair-save-menu-choose.py <Save_0XX.dat.ss> --prefix "&walk+free+walk" [--index 1]

不传 --prefix 时默认按大门（door_001_01）的九项菜单暴力匹配条件组合后补键。
脚本会先备份（.bak-时间戳）再写入，并回读校验。
"""
import argparse, hashlib, itertools, json, os, shutil, datetime, sys

DOOR_OPTIONS = [
    'move+O_Door_001_001+0+0',   # 1 闹市（无条件）
    'move+O_Door_001_002+0+0',   # 2 外堡  (Facility_001)
    'move+O_Door_001_003+0+0',   # 3 茶肆  (Facility_002)
    'move+O_Door_001_004+0+0',   # 4 药铺  (Facility_003)
    'move+O_Door_001_008+0+0',   # 5 澡堂  (Facility_004)
    'move+O_Door_001_009+0+0',   # 6 农田  (Facility_005)
    'event+O_Door_001_005+0+0',  # 7 行商（无条件）
    'event+O_Door_001_007+0+0',  # 8 (S0604_01)
    'event+O_Door_001_010+0+0',  # 9 (Door_001_001)
]

def md5(s):
    return hashlib.md5(s.encode('utf-8')).hexdigest()

def door_candidates():
    """枚举条件组合：选项 1 与 7 无条件；其余 7 个条件位（Facility_001..005、S0604_01、Door_001_001）覆盖 0..6 位。"""
    conditional = [1, 2, 3, 4, 5, 7, 8]
    for mask in range(1 << len(conditional)):
        arr = []
        for i, opt in enumerate(DOOR_OPTIONS):
            if i in (0, 6):
                arr.append(opt)
            else:
                bit = conditional.index(i)
                arr.append(opt if (mask >> bit) & 1 else '~x')
        yield mask, 'Choose_' + ''.join('&' + x for x in arr)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('save')
    ap.add_argument('--kind', default='door', choices=['door'])
    ap.add_argument('--prefix', default=None,
                    help='直接给出 choose 选项串（不含 Choose_ 前缀，以 & 开头），如 "&walk+free+walk"')
    ap.add_argument('--index', type=int, default=1, help='注入的选择序号（1 起）')
    ap.add_argument('--match-hash', default=None, help='已知缺失哈希时直接注入该键')
    args = ap.parse_args()

    p = args.save
    d = json.load(open(p, encoding='utf-8-sig'))
    kv = d['KV']

    if args.match_hash:
        keys = [args.match_hash]
    elif args.prefix:
        keys = [md5('Choose_' + args.prefix)]
    else:
        # 找到“不在 KV 里的门菜单哈希”：条件组合中排除已存在的
        missing = [md5(s) for _, s in door_candidates() if md5(s) not in kv]
        keys = missing[:1]
        print('候选缺失门菜单键数:', len(missing))

    if not keys:
        print('没有需要注入的键（可能已存在）。')
        return

    ts = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
    bak = p + '.bak-' + ts
    shutil.copy2(p, bak)
    print('备份 ->', os.path.basename(bak))

    for k in keys:
        print('注入', k, '=', str(args.index))
        kv[k] = str(args.index)

    out = json.dumps(d, ensure_ascii=False, separators=(',', ':'))
    open(p, 'wb').write(b'\xef\xbb\xbf' + out.encode('utf-8'))
    d2 = json.load(open(p, encoding='utf-8-sig'))
    print('校验:', all(k in d2['KV'] for k in keys), '| KV count =', len(d2['KV']),
          '| LastScript =', d2.get('LastScript'))

if __name__ == '__main__':
    main()
