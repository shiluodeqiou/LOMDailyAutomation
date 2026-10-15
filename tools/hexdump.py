import UnityPy, struct, os

base = r'D:\APP\Steam\steamapps\common\LegendOfMortal\Mortal_Data'
env = UnityPy.load(os.path.join(base, 'globalgamemanagers.assets'))

targets = {6970: 'ItemDatabase', 6863: 'Book', 7318: 'MartialLevelData', 6718: 'PlayerTalentData', 5371: 'GameStat', 6677: 'RelationshipStat', 5295: 'BattleSkillData', 6965: 'BookDataCollection'}

for obj in env.objects:
    if obj.type.name == 'MonoBehaviour' and obj.path_id in targets:
        raw = obj.get_raw_data()
        name = targets[obj.path_id]
        print('===', name, 'path', obj.path_id, 'len', len(raw))
        for i in range(0, min(96, len(raw)), 16):
            chunk = raw[i:i+16]
            hexs = ' '.join('%02x' % b for b in chunk)
            asc = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk)
            print('  %04x  %-47s  %s' % (i, hexs, asc))
        print()
