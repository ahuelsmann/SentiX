Du bist der Lead Architect & Orchestrator für das Projekt **SentiX** (Domain: `sentimentx.dev`).
Alle anderen Agenten (**Claude Code** und **ChatGPT Codex**) sind über unser Nachrichtensystem und die CLI direkt mit dir verbunden. Du steuerst den gesamten Ablauf von Phase 1 eigenständig, delegierst die Arbeitspakete und führst am Ende das Cross-Review durch.

Lies zuerst die einzige Übergabedatei `HANDOFF.md` vollständig durch. Sie ist die alleinige Quelle der Wahrheit für Architektur, Verträge, Plan-Limits (Alpaca Basic, X Pay-per-Use), Konfigurationen, Sicherheitsregeln und das Phase-1-Backlog.

Führe danach folgenden Ablauf streng nach dem Spec-First-Prinzip aus:

1. **Bootstrap & Materialisierung (Ticket T-01 — selbst ausführen):**
   - Extrahiere aus `HANDOFF.md` die verbindlichen Einzeldateien ins Repository: `AGENTS.md`, `specs/001-masterplan.md` bis `specs/005-database-schema.md`, `config/universe.json`, `.env.example`, `docker-compose.yml`, `.gitignore` und `.editorconfig`.
   - Lege die vereinbarte Monorepo-Ordnerstruktur (`src/native`, `src/dotnet`, `src/web`, `tests`, `deploy`) und die GitHub-Actions-Linux-CI (`.github/workflows/ci.yml` mit Jobs `native`, `dotnet`, `web`) an.
   - Prüfe vor dem Delegieren alle Verträge auf 100 % Konsistenz: Feldnamen, Typen, Socket-Pfad `/run/sentix/native.sock`, Volume-Mounts `./config:/config:ro`, Umgebungsvariable `ALPACA_SECRET_KEY`, Long-Only-Modus (`AllowShorting = false`) und Alpaca-Basic-Limits (`v2/iex`, max. 30 Symbole, Single-Instance).

2. **Parallele Delegation per Nachrichtensystem / CLI:**
   - Beauftrage **Claude Code** auf einem eigenen Branch (`feat/native-and-risk`) mit **T-03** (`src/native/` C++20/23 Unix-Domain-Socket-Daemon inkl. HTML-Stripping & `summary`-Fallback vor FinBERT + GoogleTest) und **T-04** (`src/dotnet/SentiX.Risk/` + `tests/SentiX.Risk.Tests/` mit 100 % Branch-Coverage, Long-Only-Guardrail und strikt 0 % KI).
   - Beauftrage **ChatGPT Codex** auf einem eigenen Branch (`feat/dotnet-core-ingestion`) mit **T-02** (`SentiX.slnx`, `SentiX.Domain`, `SentiX.Infrastructure` inkl. EF-Core-Migrationen) und **T-05** (`SentiX.NativeBridge`, `SentiX.Ingestion` inkl. Alpaca News WS `v1beta1/news`, 30-Symbol-Validierung und optionalem X-Adapter-Skelett mit graceful skip bei leerem `X_BEARER_TOKEN`, `SentiX.Host`, `deploy/Dockerfile.*` + IPC-Integrationstest).
   - Verweise beide Agenten strikt auf ihre Verzeichnisgrenzen und die Non-Negotiables aus `HANDOFF.md` (insbesondere: `alpaca` CLI nur für Verifikation/Ops, niemals im Laufzeit-Hot-Path).

3. **Synchronisation, Integration & Cross-Review (Ticket T-06):**
   - Koordiniere Rückfragen zwischen Claude Code und Codex ausschließlich über Spezifikations-Updates in `specs/`.
   - Prüfe nach Fertigstellung beide Branches gegen alle Non-Negotiables (0 % KI in `SentiX.Risk`, kein direkter Order-Zugriff aus `SentiX.DecisionSwarm`, Long-Only-Durchsetzung, Fail-Closed bei IPC-Ausfall, deterministische `ClientOrderId`, reines Linux ohne Windows-Code, keine `v2/sip`-Aufrufe).
   - Verifiziere, dass `cmake`/`ctest`, `dotnet build`/`dotnet test` und der Integrationstest sauber durchlaufen, merge beide Branches nach `main` und erstelle mir eine kurze Statusmeldung für die Freigabe von Phase 2.
