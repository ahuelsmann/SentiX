import { useCallback, useEffect, useState, type ReactNode } from 'react'
import {
  postKillSwitchAction,
  readTelemetry,
  resolveOrderExecution,
  resolvePaperExecutionConfigured,
  resolvePaperSwitchArmed,
  resolveTradingState,
  type Debate,
  type Items,
  type Order,
  type Section,
} from './telemetry'

type Telemetry = Awaited<ReturnType<typeof readTelemetry>>

const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 })
const price = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 })
const date = new Intl.DateTimeFormat('en-GB', {
  day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'UTC',
})

function readableStatus(value: string) {
  return value.replaceAll('_', ' ').toLowerCase().replace(/^./, ch => ch.toUpperCase())
}

function formatTradingStateLabel(state: string | null) {
  switch (state) {
    case 'ACTIVE': return 'Active'
    case 'HALTED_MANUAL': return 'Halted Manual'
    case 'HALTED_DAILY_LOSS': return 'Halted Daily Loss'
    case 'HALTED_SYSTEM_ERROR': return 'Halted System Error'
    default: return 'Unavailable'
  }
}

function orderStatusClass(status: string) {
  const normalized = status.toUpperCase()
  if (normalized.startsWith('REJECTED') || normalized.startsWith('FAILED')) {
    return 'tag danger'
  }
  if (normalized === 'PENDING_SUBMIT' || normalized === 'DUPLICATE') {
    return 'tag warning'
  }
  if (normalized === 'EXPIRED' || normalized === 'CANCELED' || normalized === 'CANCELLED') {
    return 'tag'
  }
  if (
    normalized === 'SUBMITTED' ||
    normalized === 'NEW' ||
    normalized === 'ACCEPTED' ||
    normalized === 'PARTIALLY_FILLED' ||
    normalized === 'FILLED'
  ) {
    return 'tag ready'
  }
  return 'tag'
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
          <h3>HardRiskManager · Verdict</h3>
          {risk ? (
            <>
              <p>{risk.approved ? 'Risk approved' : 'Risk rejected'}{risk.tradingState ? ' · ' + readableStatus(risk.tradingState) : ''}</p>
              {risk.rejectionReason && <p>{risk.rejectionReason}</p>}
              {risk.calculatedQty != null && <p>Calculated quantity {number.format(risk.calculatedQty)}</p>}
              {Array.isArray(risk.violations) && risk.violations.length > 0 ? (
                <ul>{risk.violations.map((violation: unknown, index) => (
                  <li key={index}>{typeof violation === 'string' ? violation : JSON.stringify(violation)}</li>
                ))}</ul>
              ) : <p className="muted">No violations reported.</p>}
            </>
          ) : <p className="muted">No verdict recorded.</p>}
        </div>
      </div>
    </details>
  )
}

function OrdersTable({ items }: { items: Order[] }) {
  return (
    <div className="table-scroll" tabIndex={0}>
      <table>
        <thead>
          <tr>
            {['Symbol', 'Side', 'Qty', 'Limit', 'Stop Loss', 'Take Profit', 'Status', 'Client Order ID', 'Updated'].map(label => (
              <th key={label} scope="col">{label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map(order => (
            <tr key={order.clientOrderId}>
              <th scope="row">{order.symbol}</th>
              <td>{order.side}</td>
              <td>{number.format(order.qty)}</td>
              <td>{order.limitPrice == null ? '—' : price.format(order.limitPrice)}</td>
              <td>{order.stopLossPrice == null ? '—' : price.format(order.stopLossPrice)}</td>
              <td>{order.takeProfitPrice == null ? '—' : price.format(order.takeProfitPrice)}</td>
              <td><span className={orderStatusClass(order.status)}>{readableStatus(order.status)}</span></td>
              <td><code className="order-id">{order.clientOrderId}</code></td>
              <td><UtcTime value={order.updatedAtUtc} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function App() {
  const [telemetry, setTelemetry] = useState<Telemetry>()
  const [lastChecked, setLastChecked] = useState<Date>()
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [confirmingResume, setConfirmingResume] = useState(false)
  const [actionPending, setActionPending] = useState<'halt' | 'resume' | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  const refreshNow = useCallback(async (parentSignal?: AbortSignal) => {
    const signal = parentSignal
      ? AbortSignal.any([parentSignal, AbortSignal.timeout(5000)])
      : AbortSignal.timeout(5000)
    const data = await readTelemetry(signal)
    if (parentSignal?.aborted) return
    setTelemetry(data)
    setLastChecked(new Date())
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    let timer: number | undefined

    async function poll() {
      await refreshNow(controller.signal)
      if (!controller.signal.aborted && autoRefresh) {
        timer = window.setTimeout(poll, 15000)
      }
    }

    if (autoRefresh) void poll()
    return () => {
      controller.abort()
      window.clearTimeout(timer)
    }
  }, [autoRefresh, refreshNow])

  const health = telemetry?.health.state === 'ready' ? telemetry.health.value : null
  const overview = telemetry?.overview.state === 'ready' ? telemetry.overview.value : null
  const nativeState = overview?.nativeStatus ?? health?.native
  const systemStatus = !telemetry ? 'Checking host' : health ? 'Host online' : 'Host unreachable'
  const counts = overview?.counts
  const orderExecution = resolveOrderExecution(health, overview)
  const tradingState = resolveTradingState(health, overview)
  const paperSwitchArmed = resolvePaperSwitchArmed(health, overview)
  const paperExecutionConfigured = resolvePaperExecutionConfigured(health, overview)
  const isHalted = Boolean(tradingState?.startsWith('HALTED'))
  const databaseUnreachable = Boolean(health) && (tradingState === 'UNAVAILABLE' || health?.databaseReachable === false)
  const controlsAvailable =
    Boolean(health) &&
    overview?.databaseConfigured !== false &&
    tradingState !== 'UNAVAILABLE' &&
    health?.databaseReachable !== false

  useEffect(() => {
    if (tradingState !== 'HALTED_MANUAL') {
      setConfirmingResume(false)
    }
  }, [tradingState])

  async function handleKillSwitch(action: 'halt' | 'resume') {
    setActionPending(action)
    setActionError(null)
    try {
      const result = await postKillSwitchAction(action, AbortSignal.timeout(5000))
      setConfirmingResume(false)
      if (!result.ok) {
        const message = result.error === 'cannot_resume_state'
          ? `Manual resume blocked while trading state is ${readableStatus(result.tradingState ?? 'halted')}.`
          : result.error === 'database_not_configured'
            ? 'PostgreSQL is not configured on the host.'
            : `Kill-switch ${action} failed (${readableStatus(result.error)}).`
        setActionError(message)
      }
      await refreshNow()
    } finally {
      setActionPending(null)
    }
  }

  let executionBadgeClass = 'tag'
  let executionBadgeText = 'Disabled (Shadow Only)'
  if (paperSwitchArmed === true && paperExecutionConfigured === false) {
    executionBadgeClass = 'tag danger'
    executionBadgeText = 'Paper Switch Armed · Credentials Missing (Fail-Closed)'
  } else if (paperSwitchArmed === true && tradingState === 'ACTIVE') {
    executionBadgeClass = 'tag warning'
    executionBadgeText = 'Paper Armed (paper-api.alpaca.markets)'
  } else if (paperSwitchArmed === true) {
    executionBadgeClass = 'tag danger'
    executionBadgeText = 'Paper Switch Armed · Halted'
  } else if (paperSwitchArmed === null && orderExecution === true) {
    executionBadgeClass = 'tag warning'
    executionBadgeText = 'Paper Armed (paper-api.alpaca.markets)'
  } else if (paperSwitchArmed === null && orderExecution === null) {
    executionBadgeClass = 'tag danger'
    executionBadgeText = 'Unverified'
  }

  const tradingStateClass = tradingState === 'ACTIVE'
    ? 'tag ready'
    : isHalted
      ? 'tag danger'
      : 'tag'

  return (
    <main>
      <header className="header">
        <div>
          <p className="product">SentiX</p>
          <h1>Paper &amp; shadow cockpit</h1>
          <p className="muted">Phase {overview?.phase ?? health?.phase ?? '4'} · Paper-only execution &amp; read-only telemetry</p>
        </div>
        <div className="toolbar">
          <span className={health ? 'tag ready' : 'tag'}>{systemStatus}</span>
          <label className="refresh-toggle">
            <input type="checkbox" checked={autoRefresh} onChange={event => setAutoRefresh(event.target.checked)} />
            Auto-refresh
          </label>
        </div>
      </header>

      <section aria-labelledby="kill-switch">
        <div className="section-heading">
          <h2 id="kill-switch">Kill-switch &amp; execution mode</h2>
          {lastChecked && <p className="muted">Last checked <UtcTime value={lastChecked.toISOString()} /></p>}
        </div>

        <div className="kill-switch-panel">
          <dl className="system-grid">
            <div>
              <dt>Trading State</dt>
              <dd><span className={tradingStateClass}>{formatTradingStateLabel(tradingState)}</span></dd>
            </div>
            <div>
              <dt>Execution Mode</dt>
              <dd><span className={executionBadgeClass}>{executionBadgeText}</span></dd>
            </div>
            <div>
              <dt>Native Scorer</dt>
              <dd>{nativeState ? readableStatus(nativeState) : 'Unknown'}</dd>
            </div>
            <div>
              <dt>Decision Swarm</dt>
              <dd>{overview?.decisionSwarmConfigured === false ? 'Not configured' : counts?.debates != null ? number.format(counts.debates) + ' debates' : 'No telemetry'}</dd>
            </div>
          </dl>

          {databaseUnreachable && (
            <p className="safety-alert" role="alert">
              PostgreSQL is unreachable — trading state cannot be verified and paper execution is blocked fail-closed.
            </p>
          )}

          {isHalted && tradingState && (
            <div className="safety-alert" role="alert">
              <strong>Kill-Switch Engaged · {readableStatus(tradingState)}</strong>
              <span>All paper order submissions are blocked fail-closed until trading state returns to Active.</span>
            </div>
          )}

          {paperSwitchArmed === true && paperExecutionConfigured !== false && tradingState === 'ACTIVE' && (
            <p className="paper-armed-notice" role="status">
              Paper Armed (paper-api.alpaca.markets) — HardRiskManager-approved proposals dispatch Long-Only whole-share bracket orders to Alpaca Paper API.
            </p>
          )}

          {paperSwitchArmed === null && orderExecution === null && (
            <p className="safety-alert" role="alert">
              The host has not confirmed execution mode status.
            </p>
          )}

          <div className="operator-controls">
            {tradingState === 'ACTIVE' && (
              <button
                type="button"
                className="btn danger"
                disabled={!controlsAvailable || actionPending !== null}
                onClick={() => void handleKillSwitch('halt')}
              >
                {actionPending === 'halt' ? 'Engaging kill-switch…' : 'Engage Kill-Switch (HALT)'}
              </button>
            )}

            {tradingState === 'HALTED_MANUAL' && !confirmingResume && (
              <button
                type="button"
                className="btn"
                disabled={!controlsAvailable || actionPending !== null}
                onClick={() => {
                  setActionError(null)
                  setConfirmingResume(true)
                }}
              >
                Resume to Active
              </button>
            )}

            {tradingState === 'HALTED_MANUAL' && confirmingResume && (
              <div className="confirm-bar" role="group" aria-label="Confirm manual resume">
                <span className="muted">Re-arm trading state to Active?</span>
                <button
                  type="button"
                  className="btn warning"
                  disabled={!controlsAvailable || actionPending !== null}
                  onClick={() => void handleKillSwitch('resume')}
                >
                  {actionPending === 'resume' ? 'Resuming…' : 'Confirm Resume'}
                </button>
                <button
                  type="button"
                  className="btn"
                  disabled={actionPending !== null}
                  onClick={() => setConfirmingResume(false)}
                >
                  Cancel
                </button>
              </div>
            )}

            {(tradingState === 'HALTED_DAILY_LOSS' || tradingState === 'HALTED_SYSTEM_ERROR') && (
              <p className="circuit-lock-note">
                Manual UI resume is locked by the hard-risk circuit breaker (database recovery required).
              </p>
            )}

            {!controlsAvailable && (
              <p className="muted">
                {overview?.databaseConfigured === false
                  ? 'Operator kill-switch controls require PostgreSQL to be configured.'
                  : 'Connect to a live host with PostgreSQL to operate the persisted kill-switch.'}
              </p>
            )}
          </div>

          {actionError && (
            <p className="safety-alert" role="alert">{actionError}</p>
          )}
        </div>

        <dl className="metrics">
          <div><dt>Events</dt><dd>{counts?.events != null ? number.format(counts.events) : '—'}</dd></div>
          <div><dt>Fast-path passes</dt><dd>{counts?.fastPathPassed != null ? number.format(counts.fastPathPassed) : '—'}</dd></div>
          <div><dt>Risk approvals</dt><dd>{counts?.riskApproved != null ? number.format(counts.riskApproved) : '—'}</dd></div>
          <div>
            <dt>Paper orders</dt>
            <dd>
              {counts?.ordersSubmitted != null && counts?.ordersTotal != null
                ? `${number.format(counts.ordersSubmitted)} / ${number.format(counts.ordersTotal)}`
                : '—'}
            </dd>
          </div>
          <div><dt>Cached quotes</dt><dd>{counts?.cachedQuotes != null ? number.format(counts.cachedQuotes) : '—'}</dd></div>
        </dl>
      </section>

      <section aria-labelledby="orders">
        <div className="section-heading"><h2 id="orders">Paper orders audit</h2></div>
        <DataList section={telemetry?.orders} empty="No paper orders have been recorded.">
          {items => <OrdersTable items={items} />}
        </DataList>
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
