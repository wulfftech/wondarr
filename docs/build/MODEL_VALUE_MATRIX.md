# OpenRouter best value models — 2026-10-08

Live data for 468 models (287 with Artificial Analysis scores and a usable endpoint).

**Method:** price = cheapest live endpoint *after* its `discount`; blended $/M = (3×input + output)/4; value = index ÷ blended $/M. `:free` excluded, `:batch` (async) excluded. Quality floor: Coding ≥ 50, Intelligence ≥ 30, Agentic ≥ 40. 'List value' uses the pre-discount price (what you get if the promo ends).


## Coding value (AA Coding Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4-flash-0731` | 69.1 | 0.004 / 0.013 | 90% | 0.007 | 10470 | 1047 | StreamLake |
| 2 | `inclusionai/ling-3.0-flash-vl` | 57.0 | 0.006 / 0.017 | 72% | 0.009 | 6535 | 1830 | Novita |
| 3 | `inclusionai/ling-3.0-flash` | 50.6 | 0.007 / 0.022 | 65% | 0.011 | 4590 | 1606 | Novita |
| 4 | `inclusionai/ling-3.0-flash-fin` | 55.6 | 0.024 / 0.069 | 44% | 0.035 | 1594 | 892 | Novita |
| 5 | `z-ai/glm-5.3-flash` | 71.5 | 0.037 / 0.125 | 50% | 0.059 | 1204 | 602 | DeepInfra |
| 6 | `upstage/solar-pro4` | 52.7 | 0.027 / 0.108 | 70% | 0.047 | 1115 | 335 | Upstage |
| 7 | `deepseek/deepseek-v4-pro` | 59.4 | 0.049 / 0.098 | 83% | 0.061 | 968 | 163 | StreamLake |
| 8 | `deepseek/deepseek-v4-flash` | 52.0 | 0.057 / 0.114 | 36% | 0.071 | 730 | 466 | StreamLake |
| 9 | `xiaomi/mimo-v2.5` | 56.8 | 0.101 / 0.202 | 15% | 0.126 | 449 | 382 | GMICloud |
| 10 | `deepseek/deepseek-v4-flash-vision-exp` | 65.0 | 0.106 / 0.317 | 51% | 0.158 | 410 | 201 | DeepInfra |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (52.9), `thinkingmachines/inkling:free` (52.1), `google/gemma-4-31b-it:free` (43.4)

## Intelligence value (AA Intelligence Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4.1-flash` | 39.5 | 0.004 / 0.017 | 88% | 0.007 | 5403 | 638 | Baidu |
| 2 | `deepseek/deepseek-v4-flash-0731` | 34.3 | 0.004 / 0.013 | 90% | 0.007 | 5197 | 520 | StreamLake |
| 3 | `z-ai/glm-5.3-flash` | 41.8 | 0.037 / 0.125 | 50% | 0.059 | 704 | 352 | DeepInfra |
| 4 | `deepseek/deepseek-v4-pro` | 30.4 | 0.049 / 0.098 | 83% | 0.061 | 495 | 83 | StreamLake |
| 5 | `deepseek/deepseek-v4-flash-vision-exp` | 34.8 | 0.106 / 0.317 | 51% | 0.158 | 220 | 108 | DeepInfra |
| 6 | `anthropic/claude-haiku-5.5` | 43.4 | 0.100 / 0.500 | 0% | 0.200 | 217 | 217 | Google |
| 7 | `xiaomi/mimo-v2.6-flash` | 37.9 | 0.140 / 0.280 | 0% | 0.175 | 217 | 217 | Novita |
| 8 | `openai/gpt-6-luna` | 38.1 | 0.100 / 0.500 | 0% | 0.200 | 191 | 190 | OpenAI |
| 9 | `openai/gpt-5.6-luna` | 37.3 | 0.100 / 0.600 | 0% | 0.225 | 166 | 166 | OpenAI |
| 10 | `z-ai/glm-5.2` | 33.7 | 0.140 / 0.489 | 75% | 0.227 | 149 | 37 | Baidu |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (25.7), `thinkingmachines/inkling:free` (25.0), `google/gemma-4-26b-a4b-it:free` (16.7)

## Agentic value (AA Agentic Index per $) — top 10

| # | Model | Score | In/Out $/M (effective) | Discount | Blended $/M | Value | List value | Provider |
|--|--|--|--|--|--|--|--|--|
| 1 | `deepseek/deepseek-v4-flash-0731` | 41.0 | 0.004 / 0.013 | 90% | 0.007 | 6212 | 621 | StreamLake |
| 2 | `z-ai/glm-5.3-flash` | 50.9 | 0.037 / 0.125 | 50% | 0.059 | 857 | 429 | DeepInfra |
| 3 | `deepseek/deepseek-v4-flash-vision-exp` | 47.5 | 0.106 / 0.317 | 51% | 0.158 | 300 | 147 | DeepInfra |
| 4 | `openai/gpt-5.6-luna` | 42.1 | 0.100 / 0.600 | 0% | 0.225 | 187 | 187 | OpenAI |
| 5 | `qwen/qwen3.8-27b` | 45.8 | 0.012 / 1.087 | 50% | 0.281 | 163 | 82 | Reka |
| 6 | `google/gemini-3.8-flash` | 40.2 | 0.188 / 0.938 | 50% | 0.375 | 107 | 54 | Google AI Studio |
| 7 | `z-ai/glm-5.3` | 53.1 | 0.350 / 1.100 | 50% | 0.537 | 99 | 49 | Novita |
| 8 | `deepseek/deepseek-v4-pro-0813` | 41.3 | 0.329 / 0.986 | 50% | 0.493 | 84 | 42 | Baidu |
| 9 | `openai/gpt-5.6-sol` | 50.2 | 0.500 / 2.500 | 50% | 1.000 | 50 | 25 | OpenAI |
| 10 | `meta/muse-spark-1.3` | 55.5 | 1.250 / 4.250 | 0% | 2.000 | 28 | 28 | Meta |

Best $0 options (`:free`, rate-limited): `thinkingmachines/inkling-small:free` (23.5), `thinkingmachines/inkling:free` (22.5), `google/gemma-4-31b-it:free` (4.2)

## Tier picks (cheapest model that clears the tier's minimum scores; tool calling + ≥128k context)

| Tier | Use for | Minimums | Cheapest now (after discount) | Cheapest at list price (promo-proof) |
|--|--|--|--|--|
| Orchestrator | plans, reviews, judges | agent≥53, intel≥44, code≥74 | `z-ai/glm-5.3` ($0.537/M; C75 A53 I45) | `z-ai/glm-5.3` ($1.075/M; C75 A53 I45) |
| T3 hard / reviewer | processes, files, time, DB, auth, external limits; adversarial review | agent≥50, code≥74 | `z-ai/glm-5.3` ($0.537/M; C75 A53 I45) | `z-ai/glm-5.3` ($1.075/M; C75 A53 I45) |
| T2 standard worker | normal feature/test implementation in a worktree | agent≥45, code≥65 | `z-ai/glm-5.3-flash` ($0.059/M; C72 A51 I42) | `z-ai/glm-5.3-flash` ($0.119/M; C72 A51 I42) |
| T1 mechanical | port/transform a file, boilerplate tests, doc drafts (single-shot) | code≥50 | `deepseek/deepseek-v4-flash-0731` ($0.007/M; C69 A41 I34) | `inclusionai/ling-3.0-flash-vl` ($0.031/M; C57 A29 I25) |

(C = Coding Index, A = Agentic Index, I = Intelligence Index.)


**Caveats:** discounts are provider promos and can expire; check provider uptime and data terms; AA scores are third-party benchmarks, not your workload.
