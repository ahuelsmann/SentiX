export type Health = {
  status: string
  native: string
  phase: number
  tradingEnabled: boolean
}

export type Overview = {
  phase: number
  nativeStatus: string
  databaseConfigured: boolean
  decisionSwarmConfigured?: boolean
  counts: {
    events: number | null
    fastPathPassed: number | null
    debates: number | null
    riskEvaluated: number | null
    riskApproved: number | null
    cachedQuotes: number | null
    upcomingCalendar: number | null
  }
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

export type Items<T> = { items: T[]; databaseConfigured?: boolean }

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

function decodeHealth(value: unknown): Health | null {
  if (!isRecord(value)) return null
  const status = text(value.status)
  const native = nativeStatus(value)
  const phase = finite(value.phase)
  if (status === null || native === null || phase === null) return null
  return { status, native, phase, tradingEnabled: (value.orderExecutionEnabled ?? value.tradingEnabled) === true }
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
      cachedQuotes: finite(counts.cachedQuotes ?? counts.quotes),
      upcomingCalendar: finite(counts.upcomingCalendar ?? counts.scheduledEvents),
    },
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
    const decoded = items.map(decode)
    if (!decoded.every((item): item is T => item !== null)) return null
    return { items: decoded, databaseConfigured: envelope?.databaseConfigured !== false }
  }
}

export async function readTelemetry(signal: AbortSignal) {
  const [health, overview, debates, quotes, calendar] = await Promise.all([
    read('/health', signal, decodeHealth),
    read('/api/overview', signal, decodeOverview),
    read('/api/debates?limit=25', signal, itemDecoder(decodeDebate)),
    read('/api/quotes', signal, itemDecoder(decodeQuote)),
    read('/api/calendar', signal, itemDecoder(decodeCalendarEvent)),
  ])
  return { health, overview, debates, quotes, calendar }
}
