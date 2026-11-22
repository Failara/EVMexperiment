import csv
import hashlib
import json
import math
import statistics
from pathlib import Path

study = Path('E:/EVMexperiment-study')
root = study / 'results/raw/pilot_max_unpaced_20261026/PILOT_MAX/W100k_p001_noUpdates_unpaced'
out = Path(__file__).resolve().parent
rows = []
input_hashes = {}
environments = set()
for path in sorted(root.glob('*/run_summary.json')):
    s = json.loads(path.read_text(encoding='utf-8-sig'))
    if s['method'] not in ('M1', 'M2', 'M3'):
        continue
    c = json.loads((path.parent / 'config.json').read_text(encoding='utf-8-sig'))
    e = json.loads((path.parent / 'environment.json').read_text(encoding='utf-8-sig'))
    assert (path.parent / 'SUCCESS').is_file() and s['status'] == 'SUCCESS'
    assert s['fn_count'] == s['final_fp_count'] == s['update_count'] == 0
    assert c['wSize'] == s['w_size'] == 100000
    assert c['threads'] == s['threads'] == 1
    assert c['targetHitRate'] == s['actual_hit_rate'] == .01
    assert c['warmupSeconds'] == s['warmup_seconds'] == 30
    assert c['measureSeconds'] == s['measure_seconds'] == 30
    assert c['replayPolicy'] == s['replay_policy'] == 'unpaced'
    assert c['seed'] == s['seed'] in (241101, 241102, 241103)
    throughput = s['query_count'] * 1e9 / s['elapsed_ns']
    assert math.isclose(throughput, s['throughput_per_second'], rel_tol=1e-12)
    intervals = list(csv.DictReader((path.parent / 'interval_metrics.csv').open(encoding='utf-8-sig')))
    assert sum(int(x['query_count']) for x in intervals) == s['query_count']
    assert sum(int(x['fn_count']) for x in intervals) == 0
    assert sum(int(x['final_fp_count']) for x in intervals) == 0
    for key, skey in [('dataset', 'dataset'), ('watchlist', 'watchlist'), ('updates', 'updates')]:
        inp = study / c[key + 'Path']
        if str(inp) not in input_hashes:
            h = hashlib.sha256()
            with inp.open('rb') as f:
                for chunk in iter(lambda: f.read(1024*1024), b''):
                    h.update(chunk)
            input_hashes[str(inp)] = h.hexdigest()
        assert input_hashes[str(inp)] == c[key + 'Sha256'] == s[skey + '_sha256']
    environments.add((e['framework'], e['os'], e['architecture'], e['logical_cores'], e['server_gc'], e['code_sha256'], e['runner_sha256']))
    rows.append({k: s[k] for k in ('method', 'replicate', 'seed', 'throughput_per_second', 'elapsed_ns', 'query_count', 'fn_count', 'final_fp_count', 'dataset_passes')})
assert len(rows) == 9 and len(environments) == 1
medians = {}
for method in ('M1', 'M2', 'M3'):
    selected = [r for r in rows if r['method'] == method]
    assert len(selected) == 3 and {r['seed'] for r in selected} == {241101, 241102, 241103}
    for seed in (241101, 241102, 241103):
        paths = []
        for m in ('M1', 'M2', 'M3'):
            p = next(root.glob(f'*_{m}_r*_s{seed}/config.json'))
            conf = json.loads(p.read_text(encoding='utf-8-sig'))
            paths.append((conf['datasetSha256'], conf['watchlistSha256'], conf['updatesSha256']))
        assert len(set(paths)) == 1
    medians[method] = statistics.median(r['throughput_per_second'] for r in selected)
ratios = {'M3_vs_M1_percent': (medians['M3']/medians['M1']-1)*100, 'M3_vs_M2_percent': (medians['M3']/medians['M2']-1)*100, 'M2_vs_M1_percent': (medians['M2']/medians['M1']-1)*100}
report = {'source_directory': str(root), 'runs': rows, 'medians_per_second': medians, 'relative_differences_percent': ratios, 'verified_input_sha256': input_hashes, 'environment': list(next(iter(environments))), 'checks': '9 SUCCESS; parameters, seeds, zero FN/finalFP, interval query totals, throughput arithmetic, common environment, paired input SHA256 and actual file hashes verified'}
(out / 'raw_audit.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
with (out / 'raw_runs.csv').open('w', newline='', encoding='utf-8-sig') as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0])); w.writeheader(); w.writerows(rows)
print(json.dumps({'runs': rows, 'medians_millions': {k:v/1e6 for k,v in medians.items()}, 'relative_differences_percent': ratios, 'environment': report['environment'], 'input_files_verified': len(input_hashes)}, ensure_ascii=False, indent=2))
