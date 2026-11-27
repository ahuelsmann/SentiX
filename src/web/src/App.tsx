import { useEffect, useState } from 'react'

type NativeState = 'ready' | 'starting' | 'degraded' | 'unavailable'
type Health = {
  status: 'ok'
  native: NativeState
  phase: 1
  tradingEnabled: false
}

const nativeLabels: Record<NativeState, string> = {
  ready: 'Ready',
  starting: 'Starting',
  degraded: 'Degraded',
  unavailable: 'Unavailable',
}

export default function App() {
  const [health, setHealth] = useState<Health | null>()
  const [lastChecked, setLastChecked] = useState<Date | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let timer: number | undefined

    async function refresh() {
      try {
        const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])
        const response = await fetch('/health', { signal, cache: 'no-store' })
        if (!response.ok) throw new Error('Health request failed')
        const data = await response.json() as Health
        if (!controller.signal.aborted) setHealth(data)
      } catch {
        if (!controller.signal.aborted) setHealth(null)
      } finally {
        if (!controller.signal.aborted) {
          setLastChecked(new Date())
          timer = window.setTimeout(refresh, 15000)
        }
      }
    }

    void refresh()
    return () => {
      controller.abort()
      window.clearTimeout(timer)
    }
  }, [])

  const hostStatus = health === undefined ? 'Checking' : health ? 'Online' : 'Unreachable'
  const nativeStatus = health ? nativeLabels[health.native] : 'Unknown'

  return (
    <main>
      <header>
        <p className="product">SentiX</p>
        <h1>Ingestion cockpit</h1>
        <p className="subtitle">Phase 1 · Market news and native scoring</p>
      </header>

      <section aria-labelledby="system-status">
        <h2 id="system-status">System status</h2>
        <dl aria-live="polite">
          <div className="status">
            <dt>Host</dt>
            <dd className={health ? 'ready' : undefined}>{hostStatus}</dd>
          </div>
          <div className="status">
            <dt>Native scorer</dt>
            <dd className={health?.native === 'ready' ? 'ready' : undefined}>{nativeStatus}</dd>
          </div>
          <div className="status">
            <dt>Order execution</dt>
            <dd>Disabled</dd>
          </div>
        </dl>
        {lastChecked && (
          <p className="updated">
            Last checked <time dateTime={lastChecked.toISOString()}>{lastChecked.toLocaleTimeString()}</time>
          </p>
        )}
      </section>
    </main>
  )
}
