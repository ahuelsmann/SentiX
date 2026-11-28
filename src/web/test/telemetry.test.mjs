import assert from 'node:assert/strict'
import test from 'node:test'
import {
  decodeOrder,
  postKillSwitchAction,
  readTelemetry,
  resolveOrderExecution,
  resolvePaperSwitchArmed,
  resolveTradingState,
} from '../src/telemetry.ts'

const timestamp = '2026-06-04T00:00:00Z'
const quote = { symbol: 'AAPL', bid: 99, ask: 101, mid: 100, timestampUtc: timestamp }
const health = { status: 'ok', phase: 3, nativeScorerReady: true }
const overview = { phase: 3, nativeScorerReady: true, counts: { events: 2 } }
const debate = {
  id: 3,
  marketEventId: 'event-1',
  headline: 'Market update',
  source: 'SEC_EDGAR',
  createdAtUtc: timestamp,
  finalAction: 'HOLD',
  factJson: '{"isActionable":true}',
  bullJson: '{invalid json',
}

async function load(t, responses) {
  t.mock.method(globalThis, 'fetch', async (path, options) => {
    assert.equal(options.cache, 'no-store')
    const value = responses[path]
    if (value instanceof Error) throw value
    return value === undefined ? new Response(null, { status: 404 }) : Response.json(value)
  })
  return await readTelemetry(new AbortController().signal)
}

test('decodes wrapped debates and bare quotes, skipping malformed rows', async t => {
  const result = await load(t, {
    '/api/debates?limit=25': { items: [null, debate] },
    '/api/quotes': [quote, { ...quote, bid: 'bad' }, { ...quote, mid: 0 }],
    '/api/calendar': { items: [], databaseConfigured: false },
  })
  assert.equal(result.debates.state, 'ready')
  assert.equal(result.debates.value.dropped, 1)
  assert.deepEqual(result.debates.value.items[0].fact, { isActionable: true })
  assert.equal(result.debates.value.items[0].bull, '{invalid json')
  assert.equal(result.quotes.value.dropped, 1)
  assert.deepEqual(result.quotes.value.items.map(row => row.spreadBps), [200, null])
  assert.equal(result.calendar.value.databaseConfigured, false)
})

test('preserves explicit spread and the spec quote keys', async t => {
  const result = await load(t, { '/api/quotes': { items: [{
    symbol: 'AAPL', bidPrice: 99, askPrice: 101, midPrice: 100, spreadBps: 1.25, timestampUtc: timestamp,
  }] } })
  assert.equal(result.quotes.value.items[0].spreadBps, 1.25)
})

test('distinguishes Phase 2 health, 404, offline, and unexpected response shapes', async t => {
  const result = await load(t, {
    '/health': { status: 'ok', phase: 2, native: 'ready', tradingEnabled: false },
    '/api/quotes': new Error('offline'),
    '/api/calendar': { unexpected: [] },
  })
  assert.equal(result.health.value.phase, 2)
  assert.equal(result.health.value.tradingEnabled, false)
  assert.equal(result.overview.state, 'unavailable')
  assert.equal(result.orders.state, 'unavailable')
  assert.equal(result.debates.state, 'unavailable')
  assert.equal(result.quotes.state, 'offline')
  assert.equal(result.calendar.state, 'unavailable')
})

test('unknown or malformed order-execution flags never imply disabled', async t => {
  const result = await load(t, {
    '/health': { ...health, orderExecutionEnabled: 'false' },
    '/api/overview': overview,
  })
  assert.equal(result.health.value.tradingEnabled, null)
  assert.equal(resolveOrderExecution(result.health.value, result.overview.value), null)
  assert.equal(resolveOrderExecution(null, { ...result.overview.value, orderExecutionEnabled: false }), false)
})

test('reports enabled when the sources disagree about the safety state', async t => {
  const result = await load(t, {
    '/health': { ...health, orderExecutionEnabled: false },
    '/api/overview': { ...overview, orderExecutionEnabled: true },
  })
  assert.equal(resolveOrderExecution(result.health.value, result.overview.value), true)
})

test('decodes Phase 4 kill-switch state, paper flags, and orders while skipping malformed rows', async t => {
  const validOrder = {
    clientOrderId: 'SX-20260604-NVDA-BUY-1234abcd',
    alpacaOrderId: 'alp-1',
    proposalId: 'prop-1',
    ticker: 'NVDA',
    side: 'BUY',
    orderType: 'LIMIT',
    qty: 40,
    limitPrice: 125.0,
    stopLossPrice: 121.25,
    takeProfitPrice: 132.5,
    status: 'ACCEPTED',
    submittedAtUtc: timestamp,
    updatedAtUtc: timestamp,
  }
  const result = await load(t, {
    '/health': {
      status: 'ok',
      phase: 4,
      native: 'ready',
      tradingState: 'HALTED_MANUAL',
      paperSwitchArmed: true,
      paperExecutionConfigured: true,
      tradingEnabled: false,
      orderExecutionEnabled: false,
    },
    '/api/overview': {
      phase: 4,
      nativeStatus: 'ready',
      tradingState: 'HALTED_MANUAL',
      paperSwitchArmed: true,
      paperExecutionConfigured: true,
      orderExecutionEnabled: false,
      counts: { events: 5, ordersTotal: 2, ordersSubmitted: 1, ordersRejected: 1 },
    },
    '/api/orders?limit=25': {
      items: [validOrder, { ...validOrder, qty: 0 }, { clientOrderId: 'bad' }],
      databaseConfigured: true,
    },
  })

  assert.equal(result.health.state, 'ready')
  assert.equal(result.overview.state, 'ready')
  assert.equal(resolveTradingState(result.health.value, result.overview.value), 'HALTED_MANUAL')
  assert.equal(resolvePaperSwitchArmed(result.health.value, result.overview.value), true)
  assert.equal(resolveOrderExecution(result.health.value, result.overview.value), false)
  assert.equal(result.overview.value.counts.ordersTotal, 2)
  assert.equal(result.overview.value.counts.ordersSubmitted, 1)
  assert.equal(result.orders.state, 'ready')
  assert.equal(result.orders.value.items.length, 1)
  assert.equal(result.orders.value.dropped, 2)
  assert.equal(result.orders.value.items[0].symbol, 'NVDA')
  assert.equal(result.orders.value.items[0].limitPrice, 125)
  assert.equal(decodeOrder({ ...validOrder, qty: -1 }), null)
})

test('postKillSwitchAction sends mandatory X-SentiX-Confirm header and maps conflict errors', async t => {
  const seen = []
  t.mock.method(globalThis, 'fetch', async (path, options) => {
    seen.push({ path, method: options.method, headers: options.headers, cache: options.cache })
    if (path === '/api/system-state/halt') {
      return Response.json({ tradingState: 'HALTED_MANUAL', updatedAtUtc: timestamp, updatedBy: 'OPERATOR_UI' })
    }
    return new Response(
      JSON.stringify({ error: 'cannot_resume_state', tradingState: 'HALTED_DAILY_LOSS' }),
      { status: 409, headers: { 'content-type': 'application/json' } },
    )
  })

  const haltResult = await postKillSwitchAction('halt')
  assert.equal(haltResult.ok, true)
  assert.equal(haltResult.tradingState, 'HALTED_MANUAL')

  const resumeConflict = await postKillSwitchAction('resume')
  assert.equal(resumeConflict.ok, false)
  assert.equal(resumeConflict.status, 409)
  assert.equal(resumeConflict.error, 'cannot_resume_state')
  assert.equal(resumeConflict.tradingState, 'HALTED_DAILY_LOSS')

  assert.deepEqual(seen, [
    { path: '/api/system-state/halt', method: 'POST', headers: { 'X-SentiX-Confirm': 'HALT' }, cache: 'no-store' },
    { path: '/api/system-state/resume', method: 'POST', headers: { 'X-SentiX-Confirm': 'RESUME' }, cache: 'no-store' },
  ])
})
