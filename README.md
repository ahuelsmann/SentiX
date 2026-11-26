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
| **Ziel
