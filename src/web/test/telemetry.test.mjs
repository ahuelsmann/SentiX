import assert from 'node:assert/strict'
import test from 'node:test'
import { readTelemetry, resolveOrderExecution } from '../src/telemetry.ts'

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
