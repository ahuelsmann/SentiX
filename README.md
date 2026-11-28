# SentiX

**SentiX** (`sentimentx.dev`) ist ein 24/7 laufendes, automatisiertes Handelssystem für hochliquide US-Aktien und Sektor-ETFs über die **Alpaca Trading API** (Paper Trading, Long-Only zum Start). Das System wertet Finanznachrichten, Pflichtmitteilungen (SEC EDGAR) und kuratierte Social-Media-Quellen in Echtzeit aus, um **Event-Driven Post-News-Drift** zu handeln und sich täglich am **SPY-Buy-and-Hold-Benchmark** zu messen.

> **Status:** Phase 4 (Paper Execution & Kill-Switch bereit; Shadow-Modus standardmäßig aktiv) — strikter **Paper-Trading- & Long-Only-Modus** (`https://paper-api.alpaca.markets`, `ALPACA_TRADING_ENABLED=false` als sicherer Default). Die vollständige technische Spezifikation liegt in [`specs/`](./specs/) und [`AGENTS.md`](./AGENTS.md).

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
├── SentiX.slnx            # .NET 10 Solution
├── docker-compose.yml     # PostgreSQL 17 + sentix-native + sentix-host
├── .env.example           # Vorlage für API-Keys & Secrets
├── config/universe.json   # Watchlist, Sektor-ETFs & Social-Allowlist
├── specs/                 # Technische Verträge (001–008)
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

## Dashboard-Zugriff über SSH-Tunnel (http://localhost:18080/)

`sentix-host` exponiert das Cockpit niemals öffentlich im Internet: Der Container bindet auf dem Linux-Server strikt an `127.0.0.1:8080`, und `UFW` blockiert jeden externen Zugriff auf Port `8080` (lediglich `22/tcp` für SSH mit Ed25519-Schlüssel ist geöffnet).

1. **Lokale SSH-Konfiguration (`~/.ssh/config`):**
   ```sshconfig
   Host sentix-us
       HostName <SERVER_IP>
       User sentix
       IdentityFile ~/.ssh/id_ed25519
       LocalForward 18080 127.0.0.1:8080
   ```
2. **Tunnel starten:**
   ```bash
   ssh -N sentix-us
   ```
3. **Ablauf beim Öffnen von `http://localhost:18080/` im Browser (4 Schritte):**
   1. **Browser → `127.0.0.1:18080`:** Der lokale Browser sendet HTTP-Anfragen (inkl. `X-SentiX-Confirm` & Loopback-`Origin` bei Kill-Switch-Aktionen) ausschließlich an den lokalen Port `18080`.
   2. **`127.0.0.1:18080` → lokaler SSH-Client:** Der laufende `ssh -N sentix-us`-Prozess nimmt die Verbindung lokal entgegen.
   3. **Lokaler SSH-Client → verschlüsselter Port-22-Tunnel:** Der Datenstrom wird Ed25519-authentifiziert und Ende-zu-Ende-verschlüsselt über Port `22/tcp` an `sentix-us` übertragen.
   4. **`127.0.0.1:8080` auf `sentix-us` → `sentix-host`:** Der SSH-Daemon auf dem Server leitet die Anfrage intern über Loopback (`127.0.0.1:8080`) direkt an den `sentix-host`-Container weiter.

---

## Phasenplan & Workflow

- **Phase 1 (Abgeschlossen):** Bootstrap, CI, PostgreSQL-Schema, C++ IPC-Daemon (HTML-Stripping + Mock-Scorer), Hard-Risk-Manager (100 % Coverage, Long-Only), NativeBridge & Alpaca News Collector.
- **Phase 2 (Abgeschlossen):** Lokales ONNX-FinBERT (`INT8`), SEC-EDGAR- & X-Filtered-Stream-Adapter, Earnings-/Makro-Kalender-Sync (`scheduled_events`).
- **Phase 3 (Abgeschlossen):** Decision-Swarm-Orchestrierung, Debatten-Persistenz & React-Dashboard im Shadow-Mode.
- **Phase 4 (Aktuell — Paper Execution & Kill-Switch bereit; Shadow-Modus standardmäßig aktiv):** Alpaca Paper Execution (Bracket-Orders auf `paper-api.alpaca.markets`), persistenter PostgreSQL-Kill-Switch, Orders-Audit-Cockpit und 4–8 Wochen Live-Paper-Test gegen SPY.

**Git-Workflow:** Dieses Repository arbeitet strikt ohne GitHub Pull Requests (**No-PR-Policy**). Parallele Feature-Branches werden lokal geprüft, per `git merge --no-ff` in `main` integriert und direkt gepusht. Details siehe [`AGENTS.md`](./AGENTS.md).
