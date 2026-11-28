export type Health = {
  status: string
  native: string
  phase: number
  tradingEnabled: boolean | null
  tradingState: string | null
  paperSwitchArmed: boolean | null
  paperExecutionConfigured: boolean | null
}

export type Overview = {
  phase: number
  nativeStatus: string
  orderExecutionEnabled: boolean | null
  tradingState: string | null
  paperSwitchArmed: boolean | null
  paperExecutionConfigured: boolean | null
  databaseConfigured: boolean
  decisionSwarmConfigured?: boolean
  counts: {
    events: number | null
    fastPathPassed: number | null
    debates: number | null
    riskEvaluated: number | null
    riskApproved: number | null
    ordersTotal: number | null
    ordersSubmitted: number | null
    ordersRejected: number | null
    cachedQuotes: number | null
    upcomingCalendar: number | null
  }
}

export type Order = {
  clientOrderId: string
  alpacaOrderId: string | null
  proposalId: string | null
  symbol: string
  side: string
  orderType: string
  qty: number
  limitPrice: number | null
  stopLossPrice: number | null
  takeProfitPrice: number | null
  status: string
  filledAvgPrice: number | null
  submittedAtUtc: string | null
  updatedAtUtc: string
}

export type Debate = {
  proposalId: string
  eventId: string
  headline: string
  source: string
  publishedAtUtc: string
  ticker: string | null
  finalAction: string
  confidence: number | null
  fact?: unknown
  bull?: unknown
  bear?: unknown
  softRisk?: unknown
  judge?: unknown
  riskEvaluation?: {
    approved: boolean
    tradingState: string | null
    rejectionReason: string | null
    violations: unknown
    calculatedQty?: number | null
    clampedSlPct?: number | null
    clampedTpPct?: number | null
  } | null
}

export type Quote = {
  symbol: string
  bidPrice: number
  askPrice: number
  midPrice: number
  spreadBps: number | null
  timestampUtc: string
}

export type CalendarEvent = {
  id: string
  eventType: string
  ticker: string | null
  title: string
  session: string | null
  scheduledAtUtc: string
  source: string
}

export type Section<T> =
  | { state: 'offline' | 'unavailable' }
  | { state: 'ready'; value: T }

export type Items<T> = { items: T[]; databaseConfigured?: boolean; dropped: number }

export type KillSwitchActionResult =
  | { ok: true; tradingState: string; updatedAtUtc: string | null; updatedBy: string | null }
  | { ok: false; status: number | null; error: string; tradingState: string | null }

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function text(value: unknown): string | null {
  return typeof value === 'string' ? value : null
}

function finite(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function identifier(value: unknown): string | null {
  return text(value) ?? (finite(value) !== null ? String(value) : null)
}

function nativeStatus(value: Record<string, unknown>): string | null {
  return text(value.nativeStatus) ?? text(value.native) ??
    (typeof value.nativeScorerReady === 'boolean' ? (value.nativeScorerReady ? 'ready' : 'not ready') : null)
}

function readBoolean(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null
}

export function resolveOrderExecution(health?: Health | null, overview?: Overview | null): boolean | null {
  if (health?.tradingEnabled === true || overview?.orderExecutionEnabled === true) return true
  return health?.tradingEnabled ?? overview?.orderExecutionEnabled ?? null
}

export function resolveTradingState(health?: Health | null, overview?: Overview | null): string | null {
  return overview?.tradingState ?? health?.tradingState ?? null
}

export function resolvePaperSwitchArmed(health?: Health | null, overview?: Overview | null): boolean | null {
  if (health?.paperSwitchArmed === true || overview?.paperSwitchArmed === true) return true
  return overview?.paperSwitchArmed ?? health?.paperSwitchArmed ?? null
}

function decodeHealth(value: unknown): Health | null {
  if (!isRecord(value)) return null
  const status = text(value.status)
  const native = nativeStatus(value)
  const phase = finite(value.phase)
  if (status === null || native === null || phase === null) return null
  return {
    status,
    native,
    phase,
    tradingEnabled: readBoolean(value.orderExecutionEnabled ?? value.tradingEnabled),
    tradingState: text(value.tradingState),
    paperSwitchArmed: readBoolean(value.paperSwitchArmed),
    paperExecutionConfigured: readBoolean(value.paperExecutionConfigured),
  }
}

function decodeOverview(value: unknown): Overview | null {
  if (!isRecord(value) || !isRecord(value.counts)) return null
  const native = nativeStatus(value)
  const phase = finite(value.phase)
  if (native === null || phase === null) return null
  const counts = value.counts
  return {
    phase,
    nativeStatus: native,
    orderExecutionEnabled: readBoolean(value.orderExecutionEnabled ?? value.tradingEnabled),
    tradingState: text(value.tradingState),
    paperSwitchArmed: readBoolean(value.paperSwitchArmed),
    paperExecutionConfigured: readBoolean(value.paperExecutionConfigured),
    databaseConfigured: value.databaseConfigured !== false,
    decisionSwarmConfigured: typeof value.decisionSwarmConfigured === 'boolean' ? value.decisionSwarmConfigured : undefined,
    counts: {
      events: finite(counts.events),
      fastPathPassed: finite(counts.fastPathPassed),
      debates: finite(counts.debates),
      riskEvaluated: finite(counts.riskEvaluated) ??
        (typeof counts.shadowApproved === 'number' && typeof counts.shadowBlocked === 'number'
          ? counts.shadowApproved + counts.shadowBlocked : null),
      riskApproved: finite(counts.riskApproved ?? counts.shadowApproved),
      ordersTotal: finite(counts.ordersTotal),
      ordersSubmitted: finite(counts.ordersSubmitted),
      ordersRejected: finite(counts.ordersRejected),
      cachedQuotes: finite(counts.cachedQuotes ?? counts.quotes),
      upcomingCalendar: finite(counts.upcomingCalendar ?? counts.scheduledEvents),
    },
  }
}

export function decodeOrder(value: unknown): Order | null {
  if (!isRecord(value)) return null
  const clientOrderId = identifier(value.clientOrderId ?? value.id)
  const symbol = text(value.symbol ?? value.ticker)
  const side = text(value.side)
  const orderType = text(value.orderType ?? value.type) ?? 'LIMIT'
  const qty = finite(value.qty ?? value.quantity)
  const status = text(value.status)
  const updatedAtUtc = text(value.updatedAtUtc ?? value.submittedAtUtc ?? value.createdAtUtc)
  if (!clientOrderId || !symbol || !side || qty === null || qty < 1 || !status || !updatedAtUtc) return null

  return {
    clientOrderId,
    alpacaOrderId: text(value.alpacaOrderId),
    proposalId: identifier(value.proposalId),
    symbol,
    side,
    orderType,
    qty,
    limitPrice: finite(value.limitPrice),
    stopLossPrice: finite(value.stopLossPrice),
    takeProfitPrice: finite(value.takeProfitPrice),
    status,
    filledAvgPrice: finite(value.filledAvgPrice),
    submittedAtUtc: text(value.submittedAtUtc),
    updatedAtUtc,
  }
}

function parseStage(value: unknown): unknown {
  if (typeof value !== 'string') return value
  try {
    return JSON.parse(value) as unknown
  } catch {
    return value
  }
}

function decodeDebate(value: unknown): Debate | null {
  if (!isRecord(value)) return null
  const proposalId = identifier(value.proposalId ?? value.id)
  const eventId = identifier(value.eventId ?? value.marketEventId)
  const headline = text(value.headline)
  const source = text(value.source)
  const publishedAtUtc = text(value.publishedAtUtc ?? value.createdAtUtc)
  const finalAction = text(value.finalAction)
  if (proposalId === null || eventId === null || headline === null || source === null ||
      publishedAtUtc === null || finalAction === null) return null

  const risk = isRecord(value.riskEvaluation) ? value.riskEvaluation : null
  return {
    proposalId, eventId, headline, source, publishedAtUtc, finalAction,
    ticker: text(value.ticker ?? value.symbol),
    confidence: finite(value.confidence),
    fact: parseStage(value.fact ?? value.factJson),
    bull: parseStage(value.bull ?? value.bullJson),
    bear: parseStage(value.bear ?? value.bearJson),
    softRisk: parseStage(value.softRisk ?? value.softRiskJson),
    judge: parseStage(value.judge ?? value.proposalJson),
    riskEvaluation: risk && typeof risk.approved === 'boolean' ? {
      approved: risk.approved,
      tradingState: text(risk.tradingState),
      rejectionReason: text(risk.rejectionReason),
      violations: parseStage(risk.violations ?? risk.checksFailed),
      calculatedQty: finite(risk.calculatedQty),
    } : null,
  }
}

function decodeQuote(value: unknown): Quote | null {
  if (!isRecord(value)) return null
  const symbol = text(value.symbol)
  const timestampUtc = text(value.timestampUtc)
  const bidPrice = finite(value.bidPrice ?? value.bid)
  const askPrice = finite(value.askPrice ?? value.ask)
  const midPrice = finite(value.midPrice ?? value.mid)
  if (symbol === null || timestampUtc === null || bidPrice === null || askPrice === null || midPrice === null) return null
  const spreadBps = finite(value.spreadBps) ?? (midPrice > 0 ? (askPrice - bidPrice) * 10_000 / midPrice : null)
  return { symbol, timestampUtc, bidPrice, askPrice, midPrice, spreadBps }
}

function decodeCalendarEvent(value: unknown): CalendarEvent | null {
  if (!isRecord(value)) return null
  const id = identifier(value.id)
  const eventType = text(value.eventType)
  const scheduledAtUtc = text(value.scheduledAtUtc)
  const source = text(value.source)
  if (id === null || eventType === null || scheduledAtUtc === null || source === null) return null
  const details = parseStage(value.detailsJson)
  return {
    id, eventType, scheduledAtUtc, source,
    ticker: text(value.ticker),
    title: text(value.title) ?? (isRecord(details) ? text(details.title) : null) ?? eventType,
    session: text(value.session),
  }
}

async function read<T>(
  path: string,
  signal: AbortSignal,
  decode: (value: unknown) => T | null,
): Promise<Section<T>> {
  try {
    const response = await fetch(path, { signal, cache: 'no-store' })
    if (response.status === 404) return { state: 'unavailable' }
    if (!response.ok) return { state: 'offline' }
    const value = decode(await response.json())
    return value === null ? { state: 'unavailable' } : { state: 'ready', value }
  } catch {
    return { state: 'offline' }
  }
}

function itemDecoder<T>(decode: (value: unknown) => T | null) {
  return (value: unknown): Items<T> | null => {
    const envelope = isRecord(value) ? value : null
    const items: unknown = envelope ? envelope.items : value
    if (!Array.isArray(items)) return null
    const decoded = items.map(decode).filter((item): item is T => item !== null)
    return { items: decoded, databaseConfigured: envelope?.databaseConfigured !== false, dropped: items.length - decoded.length }
  }
}

export async function postKillSwitchAction(
  action: 'halt' | 'resume',
  signal?: AbortSignal,
): Promise<KillSwitchActionResult> {
  const confirmValue = action === 'halt' ? 'HALT' : 'RESUME'
  try {
    const response = await fetch(`/api/system-state/${action}`, {
      method: 'POST',
      cache: 'no-store',
      signal,
      headers: {
        'X-SentiX-Confirm': confirmValue,
      },
    })
    let payload: unknown = null
    try {
      payload = await response.json()
    } catch {
      payload = null
    }
    const record = isRecord(payload) ? payload : null
    if (response.ok) {
      return {
        ok: true,
        tradingState: text(record?.tradingState) ?? (action === 'halt' ? 'HALTED_MANUAL' : 'ACTIVE'),
        updatedAtUtc: text(record?.updatedAtUtc),
        updatedBy: text(record?.updatedBy),
      }
    }
    return {
      ok: false,
      status: response.status,
      error: text(record?.error) ?? `http_${response.status}`,
      tradingState: text(record?.tradingState),
    }
  } catch {
    return {
      ok: false,
      status: null,
      error: 'network_error',
      tradingState: null,
    }
  }
}

export async function readTelemetry(signal: AbortSignal) {
  const [health, overview, orders, debates, quotes, calendar] = await Promise.all([
    read('/health', signal, decodeHealth),
    read('/api/overview', signal, decodeOverview),
    read('/api/orders?limit=25', signal, itemDecoder(decodeOrder)),
    read('/api/debates?limit=25', signal, itemDecoder(decodeDebate)),
    read('/api/quotes', signal, itemDecoder(decodeQuote)),
    read('/api/calendar', signal, itemDecoder(decodeCalendarEvent)),
  ])
  return { health, overview, orders, debates, quotes, calendar }
}
