#!/usr/bin/env python3
"""Rank OpenRouter models by coding / intelligence value per dollar.

Data (live, no API key needed):
  - GET /api/v1/models            -> model list + Artificial Analysis scores
  - GET /api/v1/models/<id>/endpoints -> per-provider price and `discount`

Price = cheapest non-down endpoint AFTER its discount.
Blended $/M = (3*input + output) / 4.   Value = score / blended $/M.
"""
import argparse, concurrent.futures as cf, datetime, json, sys, urllib.request

BASE = "https://openrouter.ai/api/v1"
SPECS = {
    "code": ("Coding value (AA Coding Index per $)", "coding_index"),
    "intel": ("Intelligence value (AA Intelligence Index per $)", "intelligence_index"),
    "agent": ("Agentic value (AA Agentic Index per $)", "agentic_index"),
}

# Effort tiers for delegating work. Each picks the best-value model that clears
# every minimum score AND supports tool calling with >= 128k context.
TIERS = [
    ("Orchestrator", "plans, reviews, judges", dict(agent=53, intel=44, code=74)),
    ("T3 hard / reviewer", "processes, files, time, DB, auth, external limits; adversarial review", dict(agent=50, code=74)),
    ("T2 standard worker", "normal feature/test implementation in a worktree", dict(agent=45, code=65)),
    ("T1 mechanical", "port/transform a file, boilerplate tests, doc drafts (single-shot)", dict(code=50)),
]


def get_json(url, retries=3):
    err = None
    for _ in range(retries):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                return json.load(r)
        except Exception as e:  # network/HTTP error: retry
            err = e
    raise RuntimeError(f"{url}: {err}")


def aa(m):
    return (m.get("benchmarks") or {}).get("artificial_analysis") or {}


def best_endpoint(endpoints):
    """Cheapest usable endpoint by blended price after discount."""
    best = None
    for e in endpoints:
        p = e["pricing"]
        try:
            pi, po = float(p["prompt"]), float(p["completion"])
        except (KeyError, TypeError, ValueError):
            continue
        if (e.get("status") or 0) < 0:  # negative status = degraded/down
            continue
        dc = float(p.get("discount") or 0)
        blended = (3 * pi + po) / 4 * (1 - dc) * 1e6
        if best is None or blended < best["bl"]:
            best = dict(bl=blended, i=pi * (1 - dc) * 1e6, o=po * (1 - dc) * 1e6, dc=dc,
                        li=pi * 1e6, lo=po * 1e6, prov=e.get("provider_name", "?"))
    return best


def collect(workers):
    models = get_json(f"{BASE}/models")["data"]
    cand = [m for m in models if aa(m).get("intelligence_index") or aa(m).get("coding_index")]

    def one(m):
        try:
            return m, best_endpoint(get_json(f"{BASE}/models/{m['id']}/endpoints")["data"]["endpoints"])
        except Exception as e:
            print(f"warn: skipped {m['id']}: {e}", file=sys.stderr)
            return m, None

    rows = []
    with cf.ThreadPoolExecutor(workers) as ex:
        for m, b in ex.map(one, cand):
            if b:
                s = aa(m)
                rows.append(dict(id=m["id"], name=m["name"], code=s.get("coding_index"),
                                 intel=s.get("intelligence_index"), agent=s.get("agentic_index"),
                                 tools="tools" in (m.get("supported_parameters") or []),
                                 ctx=m.get("context_length") or 0, **b))
    return rows, len(models)


def rank(rows, key, floor, top, include_batch):
    out = []
    for r in rows:
        if not r[key] or r[key] < floor or r["bl"] <= 0 or r["id"].endswith(":free"):
            continue
        if r["id"].endswith(":batch") and not include_batch:
            continue
        list_bl = (3 * r["li"] + r["lo"]) / 4
        out.append({**r, "score": r[key], "value": r[key] / r["bl"], "list_value": r[key] / list_bl})
    return sorted(out, key=lambda r: -r["value"])[:top]


def tier_picks(rows, a):
    """Per effort tier: best effective-value and best list-price-value (promo-proof) model."""
    out = []
    for name, use, mins in TIERS:
        ok = [r for r in rows if r["bl"] > 0 and r["tools"] and r["ctx"] >= 128000
              and not r["id"].endswith((":free", ":batch"))
              and all((r[k] or 0) >= v for k, v in mins.items())]
        for r in ok:
            r["lbl"] = (3 * r["li"] + r["lo"]) / 4
        by_eff = sorted(ok, key=lambda r: r["bl"])[:1]
        by_list = sorted(ok, key=lambda r: r["lbl"])[:1]
        out.append((name, use, mins, by_eff, by_list))
    return out


def render(rows, n_models, a):
    L = [f"# OpenRouter best value models — {datetime.date.today()}\n",
         f"Live data for {n_models} models ({len(rows)} with Artificial Analysis scores and a usable endpoint).\n",
         "**Method:** price = cheapest live endpoint *after* its `discount`; blended $/M = (3×input + output)/4; "
         "value = index ÷ blended $/M. `:free` excluded"
         + ("" if a.include_batch else ", `:batch` (async) excluded")
         + f". Quality floor: Coding ≥ {a.code_floor}, Intelligence ≥ {a.intel_floor}, Agentic ≥ {a.agent_floor}. "
         "'List value' uses the pre-discount price (what you get if the promo ends).\n"]
    result = {}
    for k, (title, _) in SPECS.items():
        floor = {"code": a.code_floor, "intel": a.intel_floor, "agent": a.agent_floor}[k]
        top = rank(rows, k, floor, a.top, a.include_batch)
        result[k] = top
        L += [f"\n## {title} — top {a.top}\n",
              "| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |",
              "|--|--|--|--|--|--|--|--|--|"]
        for n, r in enumerate(top, 1):
            L.append(f"| {n} | `{r['id']}` | {r['score']:.1f} | {r['i']:.3f} / {r['o']:.3f} | {r['dc']*100:.0f}% "
                     f"| {r['bl']:.3f} | {r['value']:.0f} | {r['list_value']:.0f} | {r['prov']} |")
        free = sorted((r for r in rows if r[k] and r["bl"] == 0 and r["id"].endswith(":free")), key=lambda r: -r[k])[:3]
        if free:
            L.append("\nBest $0 options (`:free`, rate-limited): " + ", ".join(f"`{r['id']}` ({r[k]:.1f})" for r in free))
    L += ["\n## Tier picks (cheapest model that clears the tier's minimum scores; tool calling + ≥128k context)\n",
          "| Tier | Use for | Minimums | Cheapest now (after discount) | Cheapest at list price (promo-proof) |",
          "|--|--|--|--|--|"]
    picks = {}
    for name, use, mins, eff, lst in tier_picks(rows, a):
        def f(rs, price):
            return "—" if not rs else f"`{rs[0]['id']}` (${rs[0][price]:.3f}/M; C{rs[0]['code'] or 0:.0f} A{rs[0]['agent'] or 0:.0f} I{rs[0]['intel'] or 0:.0f})"
        L.append(f"| {name} | {use} | {', '.join(f'{k}≥{v}' for k, v in mins.items())} | {f(eff, 'bl')} | {f(lst, 'lbl')} |")
        picks[name] = dict(now=eff[0]["id"] if eff else None, list=lst[0]["id"] if lst else None)
    result["tiers"] = picks
    L.append("\n(C = Coding Index, A = Agentic Index, I = Intelligence Index.)")
    L.append("\n\n**Caveats:** discounts are provider promos and can expire; check provider uptime and data terms; "
             "AA scores are third-party benchmarks, not your workload.\n")
    return "\n".join(L), result


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--top", type=int, default=10)
    p.add_argument("--code-floor", type=float, default=50, help="min Coding Index (default 50)")
    p.add_argument("--intel-floor", type=float, default=30, help="min Intelligence Index (default 30)")
    p.add_argument("--agent-floor", type=float, default=40, help="min Agentic Index (default 40)")
    p.add_argument("--include-batch", action="store_true", help="include :batch (async) variants")
    p.add_argument("--workers", type=int, default=12)
    p.add_argument("--out", default="openrouter-value-rankings.md", help="markdown output path ('-' = stdout only)")
    p.add_argument("--json", help="also write rankings as JSON to this path")
    a = p.parse_args()

    rows, n = collect(a.workers)
    md, result = render(rows, n, a)
    print(md)
    if a.out != "-":
        open(a.out, "w").write(md)
        print(f"wrote {a.out}", file=sys.stderr)
    if a.json:
        json.dump(result, open(a.json, "w"), indent=1)


if __name__ == "__main__":
    main()
