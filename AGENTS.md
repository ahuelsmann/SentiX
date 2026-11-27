# AGENTS.md — SentiX Multi-Agent Operating Rules

> **Sole Source of Truth:** [`HANDOFF.md`](./HANDOFF.md) and the extracted specifications in [`specs/`](./specs/).
> In case of any conflict between code and `specs/`, the specification wins and MUST be updated first (Spec-First).

## 1. Git Workflow (Strict No-PR Policy)

- **Pull Requests are disabled** in `ahuelsmann/SentiX`. NEVER run `gh pr create`.
- Parallel feature work runs on dedicated branches (`feat/...`) in isolated worktrees.
- Before integrating a feature branch into `main`:
  1. Verify all directory ownership boundaries below.
  2. Run full build, unit tests, coverage gates, and non-negotiable audits locally / on Linux CI.
  3. Cross-review by another agent family against `HANDOFF.md` and `specs/`.
  4. Merge locally using `git merge --no-ff <branch>` into `main`, push `origin main`, and delete both the local (`git branch -d`) and remote feature branches.
- Always use LF (`\n`) line endings (`core.autocrlf = false`, `core.eol = lf`). Target runtime is exclusively **Headless Linux** (Debian 13 / Ubuntu 24.04 LTS, x86_64).

## 2. Dev-Swarm Directory Ownership

| Agent | Role & Branch | Exclusive Directories / Files |
| --- | --- | --- |
| **Antigravity (Gemini / Opus)** | Lead Architect, Specs, ADRs, CI, Integration (`main`, `chore/...`) | `AGENTS.md`, `specs/**`, `.github/**`, `.gitignore`, `.editorconfig`, `docker-compose.yml`, `.env.example`, `config/universe.json`, integration glue (`SentiX.slnx` merge wiring in T-06) |
| **Claude Code** | C++20/23 Native Daemon + Deterministic Hard-Risk-Manager (`feat/native-and-risk`) | `src/native/**`, `src/dotnet/SentiX.Risk/**`, `tests/SentiX.Risk.Tests/**` |
| **ChatGPT Codex** | .NET 10 Solution, Domain, Infra, Bridge, Ingestion, Host, Web, Dockerfiles (`feat/dotnet-core-ingestion`) | `SentiX.slnx`, `src/dotnet/SentiX.Domain/**`, `src/dotnet/SentiX.Infrastructure/**`, `src/dotnet/SentiX.NativeBridge/**`, `src/dotnet/SentiX.Ingestion/**`, `src/dotnet/SentiX.DecisionSwarm/**`, `src/dotnet/SentiX.Execution/**`, `src/dotnet/SentiX.Host/**`, `src/web/**`, `deploy/**`, `tests/SentiX.Integration.Tests/**` |

- **Coordination Rule:** Agents MUST NOT edit files outside their assigned directories. Any interface or contract question MUST be reported back to the Lead Architect to update `specs/001`–`005` first.

## 3. Non-Negotiables (Zero Exceptions)

1. **0 % AI in `SentiX.Risk`:** `SentiX.Risk` MUST have zero references to LLM SDKs, `SentiX.DecisionSwarm`, HTTP clients, `System.Net.Http`, file/network I/O, or `DateTime.UtcNow` / `DateTimeOffset.UtcNow`. Pure deterministic logic with **100 % branch coverage**.
2. **No Direct Order Access from `SentiX.DecisionSwarm`:** `SentiX.DecisionSwarm` produces `TradeProposal` DTOs only and MUST NEVER reference or call `SentiX.Execution` or Alpaca order APIs.
3. **Long-Only Enforcement:** While `RiskPolicy.AllowShorting == false` (default in Phase 1–4 start), `SELL` MUST ONLY close an existing Long position and MUST NEVER open a short position.
4. **Fail-Closed:** If `sentix-native` is offline, timed out, or `DEGRADED`, or if a Decision-Swarm agent fails after 1 retry, ZERO trades may be triggered (fallback: `HOLD` / `PassToDecisionSwarm = false`).
5. **Prompt-Injection Protection:** All external news and social text passed to Decision-Swarm LLMs MUST be wrapped in `<untrusted_content>...</untrusted_content>`.
6. **Allowlist Only:** No open-ended social search; tickers, sector ETFs, and X handles MUST come exclusively from `config/universe.json`.
7. **Deterministic Idempotency:** Every Alpaca order MUST use deterministic `ClientOrderId`: `SX-{yyyyMMdd}-{TICKER}-{SIDE}-{SHA256(eventId + proposalId)[0..8]}` (max 48 chars).
8. **Secrets Protection:** NEVER commit keys, tokens, or passwords (`X.txt`, `.env`). Use `.env` / environment variables only (`ALPACA_API_KEY`, `ALPACA_SECRET_KEY`, `X_BEARER_TOKEN`, `FINNHUB_API_KEY`).
9. **Spec-First:** Document every interface/contract change in `specs/` before writing or updating code.
10. **Linux-Only & Plan Compliance:** Zero Windows APIs (`#ifdef _WIN32`, Named Pipes, C++/CLI are forbidden). Use `wss://stream.data.alpaca.markets/v2/iex` (NEVER `v2/sip`), max 30 equities symbols, Single-Instance WebSockets. `alpaca` CLI is restricted to host-side Ops/verification and is **FORBIDDEN** in the runtime hot-path of `SentiX.Ingestion` and `SentiX.Execution`.

## 4. Standard Linux Build & Test Commands (CI)

```bash
cmake -S src/native -B build/native -DCMAKE_BUILD_TYPE=Release && cmake --build build/native -j && ctest --test-dir build/native --output-on-failure
dotnet build SentiX.slnx -c Release && dotnet test SentiX.slnx -c Release
npm --prefix src/web ci && npm --prefix src/web run build
```
