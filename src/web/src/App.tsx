import { useEffect, useState, type ReactNode } from 'react'
import { readTelemetry, resolveOrderExecution, type Debate, type Items, type Section } from './telemetry'

type Telemetry = Awaited<ReturnType<typeof readTelemetry>>

const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 })
const date = new Intl.DateTimeFormat('en-GB', {
  day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'UTC',
})

function readableStatus(value: string) {
  return value.replaceAll('_', ' ').toLowerCase().replace(/^./, ch => ch.toUpperCase())
}

function UtcTime({ value }: { value: string }) {
  const timestamp = new Date(value)
  return Number.isNaN(timestamp.getTime()) ? 'Unknown' : (
    <time dateTime={value}>{date.format(timestamp)} UTC</time>
  )
}

function DataList<T>({
  section, empty, children,
}: {
  section: Section<Items<T>> | undefined
  empty: string
  children: (items: T[]) => ReactNode
}) {
  if (!section) return <p className="empty">Loading telemetry…</p>
  if (section.state !== 'ready') {
    return <p className="empty">{section.state === 'offline' ? 'Telemetry is temporarily offline.' : 'This host does not expose this telemetry.'}</p>
  }
  if (section.value.databaseConfigured === false) return <p className="empty">PostgreSQL is not configured.</p>
  const { items, dropped } = section.value
  return (
    <>
      {dropped > 0 && <p className="data-warning">{dropped} invalid {dropped === 1 ? 'row skipped' : 'rows skipped'}.</p>}
      {items.length === 0 ? <p className="empty">{dropped > 0 ? 'No valid rows received.' : empty}</p> : children(items)}
    </>
  )
}

function Stage({ name, data }: { name: string; data: unknown }) {
  return (
    <div className="stage">
      <h3>{name}</h3>
      {data == null ? <p className="muted">No result recorded.</p> : (
        <pre>{typeof data === 'string' ? data : JSON.stringify(data, null, 2)}</pre>
      )}
    </div>
  )
}

function DebateCard({ debate }: { debate: Debate }) {
  const risk = debate.riskEvaluation
  return (
    <details className="debate">
      <summary>
        <span className="debate-title">
          <strong>{debate.headline}</strong>
          <span className="muted">{debate.source} · <UtcTime value={debate.publishedAtUtc} /></span>
        </span>
        <span className="tag">{debate.ticker ? debate.ticker + ' · ' : ''}{readableStatus(debate.finalAction)}</span>
      </summary>
      <div className="debate-body">
        <Stage name="FactAnalyst" data={debate.fact} />
        <div className="parallel" aria-label="Parallel agents">
          <Stage name="BullAgent" data={debate.bull} />
          <Stage name="BearAgent" data={debate.bear} />
          <Stage name="SoftRiskAgent" data={debate.softRisk} />
        </div>
        <Stage name="JudgeAgent" data={debate.judge} />
        <div className="stage">
          <h3>HardRiskManager · Shadow verdict</h3>
          {risk ? (
            <>
              <p>{risk.approved ? 'Shadow approved' : 'Shadow rejected'}{risk.tradingState ? ' · ' + readableStatus(risk.tradingState) : ''}</p>
              {risk.rejectionReason && <p>{risk.rejectionReason}</p>}
              {risk.calculatedQty != null && <p>Calculated quantity {number.format(risk.calculatedQty)}</p>}
              {Array.isArray(risk.violations) && risk.violations.length > 0 ? (
                <ul>{risk.violations.map((violation: unknown, index) => (
                  <li key={index}>{typeof violation === 'string' ? violation : JSON.stringify(violation)}</li>
                ))}</ul>
              ) : <p className="muted">No violations reported.</p>}
            </>
          ) : <p className="muted">No shadow verdict recorded.</p>}
        </div>
      </div>
    </details>
  )
}

export default function App() {
  const [telemetry, setTelemetry] = useState<Telemetry>()
  const [lastChecked, setLastChecked] = useState<Date>()
  const [autoRefresh, setAutoRefresh] = useState(true)

  useEffect(() => {
    const controller = new AbortController()
    let timer: number | undefined

    async function refresh() {
      const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])
      const data = await readTelemetry(signal)
      if (controller.signal.aborted) return
      setTelemetry(data)
      setLastChecked(new Date())
      if (autoRefresh) timer = window.setTimeout(refresh, 15000)
    }

    if (autoRefresh) void refresh()
    return () => {
      controller.abort()
      window.clearTimeout(timer)
    }
  }, [autoRefresh])

  const health = telemetry?.health.state === 'ready' ? telemetry.health.value : null
  const overview = telemetry?.overview.state === 'ready' ? telemetry.overview.value : null
  const nativeState = overview?.nativeStatus ?? health?.native
  const systemStatus = !telemetry ? 'Checking host' : health ? 'Host online' : 'Host unreachable'
  const counts = overview?.counts
  const orderExecution = resolveOrderExecution(health, overview)

  return (
    <main>
      <header className="header">
        <div>
          <p className="product">SentiX</p>
          <h1>Shadow cockpit</h1>
          <p className="muted">Phase {overview?.phase ?? health?.phase ?? '3'} · Read-only market telemetry</p>
        </div>
        <div className="toolbar">
          <span className={health ? 'tag ready' : 'tag'}>{systemStatus}</span>
          <label className="refresh-toggle">
            <input type="checkbox" checked={autoRefresh} onChange={event => setAutoRefresh(event.target.checked)} />
            Auto-refresh
          </label>
        </div>
      </header>

      <section aria-labelledby="overview">
        <div className="section-heading">
          <h2 id="overview">System &amp; pipeline overview</h2>
          {lastChecked && <p className="muted">Last checked <UtcTime value={lastChecked.toISOString()} /></p>}
        </div>
        <dl className="system-grid">
          <div><dt>Native Scorer</dt><dd>{nativeState ? readableStatus(nativeState) : 'Unknown'}</dd></div>
          <div><dt>Decision Swarm</dt><dd>{overview?.decisionSwarmConfigured === false ? 'Not configured' : counts?.debates != null ? number.format(counts.debates) + ' debates' : 'No telemetry'}</dd></div>
          <div><dt>Hard Risk Shadow</dt><dd>{counts?.riskEvaluated != null ? number.format(counts.riskEvaluated) + ' evaluated' : 'No telemetry'}</dd></div>
          <div>
            <dt>Order Execution</dt>
            <dd><span className={orderExecution === false ? 'tag' : 'tag danger'}>
              {orderExecution === false ? 'Disabled (Shadow Only)' : orderExecution === true ? 'Unexpectedly enabled' : 'Unverified'}
            </span></dd>
          </div>
        </dl>
        {orderExecution !== false && (
          <p className="safety-alert" role="alert">
            {orderExecution === true ? 'Host reports that order execution is enabled.' : 'The host has not confirmed that order execution is disabled.'}
          </p>
        )}
        <dl className="metrics">
          <div><dt>Events</dt><dd>{counts?.events != null ? number.format(counts.events) : '—'}</dd></div>
          <div><dt>Fast-path passes</dt><dd>{counts?.fastPathPassed != null ? number.format(counts.fastPathPassed) : '—'}</dd></div>
          <div><dt>Shadow approvals</dt><dd>{counts?.riskApproved != null ? number.format(counts.riskApproved) : '—'}</dd></div>
          <div><dt>Cached quotes</dt><dd>{counts?.cachedQuotes != null ? number.format(counts.cachedQuotes) : '—'}</dd></div>
        </dl>
      </section>

      <section aria-labelledby="debates">
        <div className="section-heading"><h2 id="debates">Live debates</h2></div>
        <DataList section={telemetry?.debates} empty="No debates have been recorded.">
          {items => items.map(debate => <DebateCard key={debate.proposalId} debate={debate} />)}
        </DataList>
      </section>

      <div className="tables">
        <section aria-labelledby="quotes">
          <div className="section-heading"><h2 id="quotes">Live IEX quotes</h2></div>
          <DataList section={telemetry?.quotes} empty="No IEX quotes are cached.">
            {items => (
              <div className="table-scroll" tabIndex={0}>
                <table>
                  <thead><tr>{['Symbol', 'Bid', 'Ask', 'Mid', 'Spread (bps)', 'As of'].map(label => <th key={label} scope="col">{label}</th>)}</tr></thead>
                  <tbody>{items.map(quote => (
                    <tr key={quote.symbol}>
                      <th scope="row">{quote.symbol}</th>
                      <td>{number.format(quote.bidPrice)}</td><td>{number.format(quote.askPrice)}</td>
                      <td>{number.format(quote.midPrice)}</td><td>{quote.spreadBps == null ? '—' : number.format(quote.spreadBps)}</td>
                      <td><UtcTime value={quote.timestampUtc} /></td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
            )}
          </DataList>
        </section>

        <section aria-labelledby="calendar">
          <div className="section-heading"><h2 id="calendar">14-day calendar</h2></div>
          <DataList section={telemetry?.calendar} empty="No events are scheduled in the next 14 days.">
            {items => (
              <div className="table-scroll" tabIndex={0}>
                <table>
                  <thead><tr>{['Event', 'Type', 'Symbol', 'Scheduled', 'Session', 'Source'].map(label => <th key={label} scope="col">{label}</th>)}</tr></thead>
                  <tbody>{items.map(event => (
                    <tr key={event.id}>
                      <th scope="row">{event.title}</th><td>{event.eventType}</td><td>{event.ticker ?? '—'}</td>
                      <td><UtcTime value={event.scheduledAtUtc} /></td><td>{event.session ?? '—'}</td><td>{event.source}</td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
            )}
          </DataList>
        </section>
      </div>
    </main>
  )
}
