# SentiX

**SentiX** (`sentimentx.dev`) ist ein 24/7 laufendes, automatisiertes Handelssystem für hochliquide US-Aktien und Sektor-ETFs über die **Alpaca Trading API** (Paper Trading, Long-Only zum Start). Das System wertet Finanznachrichten, Pflichtmitteilungen (SEC EDGAR) und kuratierte Social-Media-Quellen in Echtzeit aus, um **Event-Driven Post-News-Drift** zu handeln und sich täglich am **SPY-Buy-and-Hold-Benchmark** zu messen.

> **Status:** Phase 1 (Bootstrap & Fundament) — strikter **Paper-Trading- & Long-Only-Modus**. Die vollständige technische Spezifikation liegt in [`HANDOFF.md`](./HANDOFF.md).

---

## Architektur im Überblick

SentiX kombiniert eine latenzkritische **C++20/23-Engine** für das Rausch-Filtering mit einem **ASP.NET Core / .NET-10-Host** für Orchestrierung, Decision-Swarm, deterministisches Risikomanagement, Execution und Web-Dashboard. Beide Prozesse sind über einen **Unix Domain Socket** (`/run/sentix/native.sock` auf `tmpfs`) sauber isoliert.

```text
[Alpaca News WS / SEC EDGAR / X Allowlist]
                  │
                  ▼
      SentiX.Ingestion (.NET 10)
                  │  Unix Domain Socket (tmpfs)
                  ▼
     sentix-native (C++20/23 Fast-Path)
     HTML-Stripping · SHA-256 Dedup · ONNX-FinBERT (< 30 ms)
                  │  passToDecisionSwarm == true
                  ▼
     SentiX.DecisionSwarm (.NET 10)
     FactAnalyst → [Bull | Bear | SoftRisk] → JudgeAgent
                  │  TradeProposal (JSON)
                  ▼
     SentiX.Risk (.NET 10 — 0 % KI, 13 harte Regeln, < 1 ms)
                  │  Approved + deterministische ClientOrderId
                  ▼
     SentiX.Execution (Alpaca Paper API — Bracket-Orders)
                  │
                  ▼
     PostgreSQL 17 (Audit-Trail & SPY-Benchmark) + React Dashboard
```

### Kernprinzipien & Sicherheitsregeln

- **Zweistufiges Risikomodell:** Stufe 1 (`SoftRiskAgent`) bewertet Kontext, Widersprüche und anstehende Earnings/Makro-Events; Stufe 2 (`SentiX.Risk`) prüft 13 deterministische Regeln (**0 % KI**, 100 % Branch-Coverage) direkt vor jeder Order.
- **Fail-Closed:** Fällt `sentix-native` oder ein Agent im Decision-Swarm aus, werden keine Trades ausgelöst (`HOLD`).
- **Long-Only & No-Margin zum Start:** Leerverkäufe sind auf Konto- und Risk-Ebene deaktiviert (`AllowShorting = false`); Position Sizing basiert strikt auf ganzen Aktien für Alpaca-Bracket-Orders (Stop-Loss & Take-Profit).
- **Prompt-Injection-Schutz:** Alle externen Markt- und Social-Texte werden zwingend in `<untrusted_content>`-Tags gekapselt.
- **Privater Betrieb:** Das React-Dashboard bindet ausschließlich an `127.0.0.1:8080` (Zugriff über Tailscale oder SSH-Tunnel).

---

## Tech-Stack

| Komponente | Technologie |
| --- | --- |
| **Zielplattform** | Headless Linux (Debian 13 / Ubuntu 24.04 LTS, x86_64) |
| **Fast-Path Engine** | C++20/23, CMake, GoogleTest, ONNX Runtime (FinBERT `INT8`) |
| **Core & Orchestrierung** | .NET 10 (C#), ASP.NET Core, SignalR, EF Core / Npgsql |
| **IPC** | Unix Domain Socket (`AF_UNIX`, 4-Byte Little-Endian Framing, `tmpfs`) |
| **Datenbank** | PostgreSQL 17 (Append-Only Audit-Trail) |
| **Frontend** | React + TypeScript (Dashboard & Kill-Switch) |
| **Broker & Marktdaten** | Alpaca Paper Trading API (`v1beta1/news`, `v2/iex`, Basic Plan), Alpaca CLI (Ops/Diagnose) |
| **Optionale Feeds** | SEC EDGAR (8-K), X API v2 Filtered Stream (`SentiX2026`), Earnings-/Makro-Kalender (Finnhub / RSS) |
| **Deployment** | Docker Compose (Single-Instance) auf netcup RS 1000 G12.5 (Manassas, VA) |

---

## Projektstruktur (Zielbild nach Phase 1)

```text
SentiX/
├── AGENTS.md              # Multi-Agenten-Regeln & No-PR-Policy
├── HANDOFF.md             # Master-Spezifikation (Single Source of Truth)
├── SentiX.slnx            # .NET 10 Solution
├── docker-compose.yml     # PostgreSQL 17 + sentix-native + sentix-host
├── .env.example           # Vorlage für API-Keys & Secrets
├── config/universe.json   # Watchlist, Sektor-ETFs & Social-Allowlist
├── specs/                 # Technische Verträge (001–005)
├── src/
│   ├── native/            # C++20/23 Fast-Path Daemon + GoogleTest
│   ├── dotnet/            # Domain, Ingestion, NativeBridge, DecisionSwarm, Risk, Execution, Infra, Host
│   └── web/               # React + TypeScript Dashboard
├── tests/                 # Risk-, DecisionSwarm- & IPC-Integrationstests
└── deploy/                # Linux-Dockerfiles (native & host)
```

---

## Build, Test & Start (Linux)

```bash
# 1. Konfiguration anlegen (Secrets niemals committen)
cp .env.example .env

# 2. C++20/23 Fast-Path bauen & testen
cmake -S src/native -B build/native -DCMAKE_BUILD_TYPE=Release
cmake --build build/native -j
ctest --test-dir build/native --output-on-failure

# 3. .NET 10 Solution bauen & testen
dotnet build SentiX.slnx -c Release
dotnet test SentiX.slnx -c Release

# 4. Web-Dashboard bauen
npm --prefix src/web ci && npm --prefix src/web run build

# 5. Container-Stack starten (Single-Instance)
docker compose up -d --build
```

---

## Phasenplan & Workflow

- **Phase 1 (Aktuell):** Bootstrap, CI, PostgreSQL-Schema, C++ IPC-Daemon (HTML-Stripping + Mock-Scorer), Hard-Risk-Manager (100 % Coverage, Long-Only), NativeBridge & Alpaca News Collector (0 Trades).
- **Phase 2:** Lokales ONNX-FinBERT (`INT8`), SEC-EDGAR- & X-Filtered-Stream-Adapter, Earnings-/Makro-Kalender-Sync (`scheduled_events`).
- **Phase 3:** Decision-Swarm-Orchestrierung, Debatten-Persistenz & React-Dashboard im Shadow-Mode.
- **Phase 4:** Alpaca Paper Execution (Bracket-Orders), UI-Kill-Switch und 4–8 Wochen Live-Paper-Test gegen SPY.

**Git-Workflow:** Dieses Repository arbeitet strikt ohne GitHub Pull Requests (**No-PR-Policy**). Parallele Feature-Branches werden lokal geprüft, per `git merge --no-ff` in `main` integriert und direkt gepusht. Details siehe [`HANDOFF.md`](./HANDOFF.md).
