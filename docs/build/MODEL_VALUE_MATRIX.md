# OpenRouter best value models — 2026-10-08

Live data for 467 models (290 with Artificial Analysis scores and a usable endpoint).

**Method:** price = cheapest live endpoint *after* its `discount`; blended $/M = (3×input + output)/4; value = index ÷ blended $/M. `:free` excluded, `:batch` (async) excluded. Quality floor: Coding ≥ 50, Intelligence ≥ 30, Agentic ≥ 40. 'List value' uses the pre-discount price (what you get if the promo ends).


## Coding value (AA Coding Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4-flash-0731` | 69.1 | 0.004 / 0.013 | 90% | 0.007 | 10470 | 1047 | StreamLake |
| 2 | `inclusionai/ling-3.0-flash-vl` | 57.0 | 0.006 / 0.017 | 72% | 0.009 | 6535 | 1830 | Novita |
| 3 | `inclusionai/ling-3.0-flash` | 50.6 | 0.007 / 0.022 | 65% | 0.011 | 4590 | 1606 | Novita |
| 4 | `deepseek/deepseek-v4-flash` | 52.0 | 0.013 / 0.025 | 70% | 0.016 | 3302 | 990 | StreamLake |
| 5 | `inclusionai/ling-3.0-flash-fin` | 55.6 | 0.024 / 0.069 | 44% | 0.035 | 1594 | 892 | Novita |
| 6 | `z-ai/glm-5.3-flash` | 71.5 | 0.037 / 0.125 | 50% | 0.059 | 1204 | 602 | DeepInfra |
| 7 | `upstage/solar-pro4` | 52.7 | 0.027 / 0.108 | 70% | 0.047 | 1115 | 335 | Upstage |
| 8 | `xiaomi/mimo-v2.5` | 56.8 | 0.101 / 0.202 | 15% | 0.126 | 449 | 382 | GMICloud |
| 9 | `deepseek/deepseek-v4-flash-vision-exp` | 65.0 | 0.106 / 0.317 | 51% | 0.158 | 410 | 201 | DeepInfra |
| 10 | `z-ai/glm-5.3` | 74.8 | 0.126 / 0.396 | 70% | 0.194 | 387 | 116 | Novita |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (52.9), `thinkingmachines/inkling:free` (52.1), `nvidia/nemotron-3-ultra-550b-a55b:free` (49.3)

## Intelligence value (AA Intelligence Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4-flash-0731` | 34.3 | 0.004 / 0.013 | 90% | 0.007 | 5197 | 520 | StreamLake |
| 2 | `z-ai/glm-5.3-flash` | 41.8 | 0.037 / 0.125 | 50% | 0.059 | 704 | 352 | DeepInfra |
| 3 | `openai/gpt-6-luna` | 38.1 | 0.050 / 0.250 | 0% | 0.100 | 381 | 381 | OpenAI |
| 4 | `deepseek/deepseek-v4.1-flash` | 39.5 | 0.090 / 0.180 | 0% | 0.113 | 351 | 351 | Decart |
| 5 | `xiaomi/mimo-v2.6-flash` | 37.9 | 0.100 / 0.280 | 0% | 0.145 | 261 | 261 | Darkbloom |
| 6 | `z-ai/glm-5.3` | 44.8 | 0.126 / 0.396 | 70% | 0.194 | 232 | 69 | Novita |
| 7 | `deepseek/deepseek-v4-flash-vision-exp` | 34.8 | 0.106 / 0.317 | 51% | 0.158 | 220 | 108 | DeepInfra |
| 8 | `anthropic/claude-haiku-5.5` | 43.4 | 0.100 / 0.500 | 0% | 0.200 | 217 | 217 | Google |
| 9 | `openai/gpt-5.6-luna` | 37.3 | 0.100 / 0.600 | 0% | 0.225 | 166 | 166 | OpenAI |
| 10 | `z-ai/glm-5.2` | 33.7 | 0.120 / 0.740 | 57% | 0.275 | 123 | 53 | Decart |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (25.7), `thinkingmachines/inkling:free` (25.0), `nvidia/nemotron-3-ultra-550b-a55b:free` (22.9)

## Agentic value (AA Agentic Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4-flash-0731` | 41.0 | 0.004 / 0.013 | 90% | 0.007 | 6212 | 621 | StreamLake |
| 2 | `z-ai/glm-5.3-flash` | 50.9 | 0.037 / 0.125 | 50% | 0.059 | 857 | 429 | DeepInfra |
| 3 | `deepseek/deepseek-v4-flash-vision-exp` | 47.5 | 0.106 / 0.317 | 51% | 0.158 | 300 | 147 | DeepInfra |
| 4 | `z-ai/glm-5.3` | 53.1 | 0.126 / 0.396 | 70% | 0.194 | 274 | 82 | Novita |
| 5 | `openai/gpt-5.6-luna` | 42.1 | 0.100 / 0.600 | 0% | 0.225 | 187 | 187 | OpenAI |
| 6 | `google/gemini-3.8-flash` | 40.2 | 0.188 / 0.938 | 50% | 0.375 | 107 | 54 | Google AI Studio |
| 7 | `qwen/qwen3.8-27b` | 45.8 | 0.112 / 1.406 | 25% | 0.436 | 105 | 79 | Phala |
| 8 | `deepseek/deepseek-v4-pro-0813` | 41.3 | 0.330 / 0.990 | 50% | 0.495 | 83 | 42 | StreamLake |
| 9 | `openai/gpt-5.6-sol` | 50.2 | 0.500 / 2.500 | 50% | 1.000 | 50 | 25 | OpenAI |
| 10 | `meta/muse-spark-1.3` | 55.5 | 1.250 / 4.250 | 0% | 2.000 | 28 | 28 | Meta |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (23.5), `thinkingmachines/inkling:free` (22.5), `nvidia/nemotron-3-ultra-550b-a55b:free` (20.1)

## Tier picks (cheapest model that clears the tier's minimum scores; tool calling + ≥128k context)

| Tier | Use for | Minimums | Cheapest now (after discount) | Cheapest at list price (promo-proof) |
|--|--|--|--|--|
| Orchestrator | plans, reviews, judges | agent≥53, intel≥44, code≥74 | `z-ai/glm-5.3` ($0.194/M; C75 A53 I45) | `z-ai/glm-5.3` ($0.645/M; C75 A53 I45) |
| T3 hard / reviewer | processes, files, time, DB, auth, external limits; adversarial review | agent≥50, code≥74 | `z-ai/glm-5.3` ($0.194/M; C75 A53 I45) | `z-ai/glm-5.3` ($0.645/M; C75 A53 I45) |
| T2 standard worker | normal feature/test implementation in a worktree | agent≥45, code≥65 | `z-ai/glm-5.3-flash` ($0.059/M; C72 A51 I42) | `z-ai/glm-5.3-flash` ($0.119/M; C72 A51 I42) |
| T1 mechanical | port/transform a file, boilerplate tests, doc drafts (single-shot) | code≥50 | `deepseek/deepseek-v4-flash-0731` ($0.007/M; C69 A41 I34) | `inclusionai/ling-3.0-flash-vl` ($0.031/M; C57 A29 I25) |

(C = Coding Index, A = Agentic Index, I = Intelligence Index.)


**Caveats:** discounts are provider promos and can expire; check provider uptime and data terms; AA scores are third-party benchmarks, not your workload.
