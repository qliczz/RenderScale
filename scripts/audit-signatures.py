"""Read-only offline audit of imported native signatures. Requires pefile.
Does not validate graphics behavior, live task lists or third-party hook chains.
"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path
import pefile

parser = argparse.ArgumentParser()
parser.add_argument('--game', required=True, type=Path)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
exe = args.game
pe = pefile.PE(str(exe))
sec = next(s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text')
data = sec.get_data()
results = []
paths = [root/'src/RenderScale/States/GameSizeState.cs', root/'vendor/FloppyUtils/FloppyUtils/Graphics/RenderEvents.cs']
for path in paths:
    text = path.read_text(encoding='utf-8-sig')
    patterns = []
    for m in re.finditer(r'"([0-9A-Fa-f? |()*]+)"', text):
        if len(m.group(1)) > 30:
            patterns.append((text.count('\n', 0, m.start())+1, '', m.group(1)))
    for m in re.finditer(r'new GhidraCode\(@"(.*?)"\)', text, re.S):
        tokens = []
        for line in m.group(1).splitlines():
            line = re.sub(r'^1[0-9a-f]{8}\s+', '', line.strip())
            match = re.match(r'(?:[()*]*[0-9a-f?]{2}[()*]*(?:\s+|$))+', line, re.I)
            if match: tokens.extend(match.group().split())
        names = re.findall(r'public static readonly \w+ (\w+) = ', text[:m.start()])
        patterns.append((text.count('\n',0,m.start())+1, names[-1], ' '.join(tokens)))
    for line, name, pat in patterns:
        tokens = pat.translate(str.maketrans('', '', '|()*')).split()
        assert len(tokens) >= 10, (line, 'parser produced an invalid short pattern')
        regex = b''.join(b'.' if '?' in t else re.escape(bytes.fromhex(t)) for t in tokens)
        matches = list(re.finditer(regex, data, re.S))
        marks, offset, star = [], 0, None
        for token in re.findall(r'[()*]|[0-9a-f?]{2}', pat, re.I):
            if token == '*': star = offset
            elif token == ')':
                assert star is not None and offset-star == 4
                marks.append((star,offset))
                star = None
            elif token != '(': offset += 1
        targets = [[sec.VirtualAddress+m.start()+end+struct.unpack_from('<i',data,m.start()+pos)[0] for pos,end in marks] for m in matches]
        results.append(dict(file=path.relative_to(root).as_posix(), line=line, name=name, length=len(tokens), count=len(matches), rvas=[sec.VirtualAddress+m.start() for m in matches], relative_targets=targets))

# These two patterns are deliberately applied relative to task-list functions,
# rather than globally scanned. Verify their identities through the manager / render target.
by_name = {r['name']: r for r in results if r['name']}
manager, device, post_tick = by_name['DevicePostTickCall']['relative_targets'][0]
real_render = by_name['TaskRenderGraphicsRenderReal']['rvas'][0]
for name in ['TaskRenderGraphicsRenderEntry','TaskUpdateGraphicsRenderEntry']:
    result = by_name[name]
    filtered = [(rva, targets) for rva,targets in zip(result['rvas'],result['relative_targets'])
                if targets[0] == manager and (name != 'TaskRenderGraphicsRenderEntry' or targets[1] == real_render)]
    result['resolved_candidates'] = [rva for rva,_ in filtered]
    # Update may have multiple equivalent entrypoints; the live task list chooses one.
    result['passed'] = len(filtered) >= 1
for result in results:
    if 'passed' not in result: result['passed'] = result['count'] == 1
report = dict(game_version=exe.with_name('ffxivgame.ver').read_text().strip(),
    sha256=hashlib.sha256(exe.read_bytes()).hexdigest(), passed=all(r['passed'] for r in results),
    limitation='Static signature and relative-target check only; not a live render or HUD test.', signatures=results)
serialized = json.dumps(report, ensure_ascii=False, indent=2)
if args.output: args.output.write_text(serialized+'\n',encoding='utf-8')
print(f"{'PASS' if report['passed'] else 'FAIL'}: {sum(r['passed'] for r in results)}/{len(results)} signature checks; {report['game_version']}")
for r in results:
    if not r['passed']: print(f"FAIL {r['file']}:{r['line']} ({r['count']} matches)")
raise SystemExit(0 if report['passed'] else 1)
