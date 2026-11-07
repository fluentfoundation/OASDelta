#!/usr/bin/env python3
"""Compare `changelog -f json` between Go oracle and C# port over all fixture pairs, grouped by rule id."""
import json, subprocess, sys, collections, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
G = os.environ.get('GO', 'oasdiff')
C = os.environ.get('CS', os.path.join(ROOT, 'src/OasDelta/bin/Release/net10.0/oasdelta'))
D = os.path.join(ROOT, 'tests/fixtures')
stubbed = set()
stubs = os.path.join(ROOT, 'src/OasDelta/Checker/Generated/Stubs.g.cs')
pairs = subprocess.run([os.path.join(ROOT, 'tools/pairs.sh')], capture_output=True, text=True).stdout.split('\n')
missing = collections.Counter(); extra = collections.Counter(); mism = collections.defaultdict(list); errors = []
ok = 0; total = 0
for line in pairs:
    if not line.strip(): continue
    b, r = line.split()
    total += 1
    g = subprocess.run([G, 'changelog', b, r, '-f', 'json'], capture_output=True, text=True, cwd=D)
    c = subprocess.run([C, 'changelog', b, r, '-f', 'json'], capture_output=True, text=True, cwd=D)
    if g.returncode != c.returncode or (g.returncode != 0):
        if g.stdout.strip() != c.stdout.strip(): errors.append((b, g.returncode, c.returncode, g.stderr[:200], c.stderr[:300]))
        continue
    try:
        gj = json.loads(g.stdout); cj = json.loads(c.stdout)
    except Exception as e:
        errors.append((b, 'json', str(e), c.stdout[:200], c.stderr[:300])); continue
    if gj == cj: ok += 1; continue
    gi = collections.defaultdict(list); ci = collections.defaultdict(list)
    for x in gj: gi[x['id']].append(x)
    for x in cj: ci[x['id']].append(x)
    for k in set(gi) | set(ci):
        if k not in ci: missing[k] += 1
        elif k not in gi: extra[k] += 1
        elif sorted(map(json.dumps, gi[k])) != sorted(map(json.dumps, ci[k])): mism[k].append(b)
print(f'identical pairs: {ok}/{total}')
print('errors:', len(errors))
for e in errors[:8]: print('  ', e)
print('ids missing in C# (count of pairs):', len(missing))
print('  ', ' '.join(f'{k}:{v}' for k, v in sorted(missing.items())))
print('ids extra in C#:', dict(extra))
print('ids with mismatched entries:', len(mism))
for k, v in sorted(mism.items()): print('  ', k, v[:3])
