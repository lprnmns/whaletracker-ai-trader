import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import ForceGraph3D from 'react-force-graph-3d'
import SpriteText from 'three-spritetext'
import * as THREE from 'three'
import { Canvas, useFrame } from '@react-three/fiber'
import { OrbitControls, Stars } from '@react-three/drei'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import {
  Activity,
  Bot,
  BrainCircuit,
  CircleDollarSign,
  Database,
  Plus,
  Radar,
  RefreshCw,
  Send,
  ShieldCheck,
  Zap,
} from 'lucide-react'
import './App.css'

type Wallet = {
  id: number
  walletAddress: string
  label: string
  source: string
  isActive: boolean
  confidenceScore: number
  estimatedProfitUsd: number
  assetSymbol: string
  lastCheckedAt?: string
  lastSeenTxHash?: string
}

type LiveEvent = {
  id: number
  type: string
  severity: string
  walletAddress: string
  txHash: string
  symbol: string
  usdValue?: number
  summary: string
  payloadJson: string
  createdAt: string
}

type AiState = {
  biasScore: number
  direction: string
  summary: string
  eventCount: number
  lastEventAt?: string
}

type OperationsSnapshot = {
  checkedAt: string
  okx?: {
    available: boolean
    totalUsd: number
    positions: number
    mode: string
  }
  recentExecutions?: Array<{
    createdAt: string
    symbol: string
    action: string
    isSuccess: boolean
    marginUsdt: number
    confidence: number
  }>
}

type HistoricalScan = {
  id: number
  createdAt: string
  candidateCount: number
  scannedSwapCount: number
}

type Candidate = {
  id: number
  walletAddress: string
  assetSymbol: string
  estimatedProfitUsd: number
  insiderScore: number
  timingScore: number
  sizeScore: number
  profitScore: number
}

type GraphNode = {
  id: string
  name: string
  kind: 'ai' | 'wallet' | 'okx' | 'event'
  color: string
  size: number
  x?: number
  y?: number
  z?: number
  wallet?: Wallet
  event?: LiveEvent
}

type GraphLink = {
  source: string
  target: string
  color: string
  particles: number
}

type Tab = 'events' | 'wallets' | 'insider' | 'chat'

const tradeEventTypes = new Set(['TradeSubmitted', 'TradeRejected'])

async function fetchJson<T>(url: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(url, {
    credentials: 'include',
    ...options,
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...(options.headers || {}),
    },
  })

  if (response.status === 401 || response.redirected) {
    window.location.href = '/login.html'
    throw new Error('Login required')
  }

  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `HTTP ${response.status}`)
  }

  return response.json()
}

function formatUsd(value?: number) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return '--'
  return `$${Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 })}`
}

function shortAddress(value?: string) {
  if (!value) return '--'
  return `${value.slice(0, 6)}...${value.slice(-4)}`
}

function formatTime(value?: string) {
  if (!value) return '--'
  return new Date(value).toLocaleString()
}

function parsePayload(event?: LiveEvent) {
  if (!event?.payloadJson) return null
  try {
    return JSON.parse(event.payloadJson)
  } catch {
    return null
  }
}

function AiCoreOrb({ bias }: { bias: string }) {
  const mesh = useRef<THREE.Mesh>(null)
  const color = bias === 'BULLISH' ? '#22c55e' : bias === 'BEARISH' ? '#ef4444' : '#67e8f9'

  useFrame(({ clock }) => {
    if (!mesh.current) return
    const t = clock.getElapsedTime()
    mesh.current.rotation.y = t * 0.35
    mesh.current.rotation.x = Math.sin(t * 0.4) * 0.16
    const scale = 1 + Math.sin(t * 1.8) * 0.045
    mesh.current.scale.setScalar(scale)
  })

  return (
    <>
      <Stars radius={60} depth={24} count={550} factor={3} saturation={0} fade speed={0.35} />
      <ambientLight intensity={0.35} />
      <pointLight position={[4, 3, 5]} intensity={2.3} color={color} />
      <mesh ref={mesh}>
        <icosahedronGeometry args={[1.25, 5]} />
        <meshStandardMaterial color={color} emissive={color} emissiveIntensity={0.85} roughness={0.28} metalness={0.18} />
      </mesh>
      <mesh>
        <sphereGeometry args={[1.72, 48, 48]} />
        <meshBasicMaterial color={color} transparent opacity={0.08} wireframe />
      </mesh>
      <OrbitControls enableZoom={false} enablePan={false} autoRotate autoRotateSpeed={0.6} />
    </>
  )
}

function App() {
  const graphRef = useRef<any>(null)
  const [wallets, setWallets] = useState<Wallet[]>([])
  const [events, setEvents] = useState<LiveEvent[]>([])
  const [aiState, setAiState] = useState<AiState>({ biasScore: 0, direction: 'NEUTRAL', summary: '', eventCount: 0 })
  const [operations, setOperations] = useState<OperationsSnapshot | null>(null)
  const [scans, setScans] = useState<HistoricalScan[]>([])
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [selected, setSelected] = useState<GraphNode | null>(null)
  const [activeTab, setActiveTab] = useState<Tab>('events')
  const [connectionState, setConnectionState] = useState('connecting')
  const [alert, setAlert] = useState('')
  const [chatQuestion, setChatQuestion] = useState('')
  const [chatLines, setChatLines] = useState<Array<{ role: 'user' | 'ai'; text: string }>>([])
  const [scanForm, setScanForm] = useState({
    preCrashStartUtc: '',
    preCrashEndUtc: '',
    dipBuyStartUtc: '',
    dipBuyEndUtc: '',
    minimumProfitUsd: 1000,
  })

  const loadMissionState = useCallback(async () => {
    try {
      const [walletList, eventList, state, ops, scanList] = await Promise.all([
        fetchJson<Wallet[]>('/api/tracked-wallets?includeInactive=true'),
        fetchJson<LiveEvent[]>('/api/live-events?count=120'),
        fetchJson<AiState>('/api/ai-memory/state'),
        fetchJson<OperationsSnapshot>('/api/operations/snapshot'),
        fetchJson<HistoricalScan[]>('/api/historical-scans?count=10'),
      ])
      setWallets(walletList)
      setEvents(eventList)
      setAiState(state)
      setOperations(ops)
      setScans(scanList)
      setAlert('')
    } catch (error) {
      setAlert(error instanceof Error ? error.message : 'Mission state unavailable')
    }
  }, [])

  useEffect(() => {
    loadMissionState()
    const timer = window.setInterval(loadMissionState, 30000)
    return () => window.clearInterval(timer)
  }, [loadMissionState])

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/mission-control')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('liveEvent', (event: LiveEvent) => {
      setEvents((current) => [event, ...current.filter((item) => item.id !== event.id)].slice(0, 160))
      setSelected({
        id: `event:${event.id}`,
        name: event.summary || event.type,
        kind: 'event',
        color: event.severity === 'danger' ? '#ef4444' : event.severity === 'success' ? '#22c55e' : '#facc15',
        size: 5,
        event,
      })
    })

    connection.onreconnecting(() => setConnectionState('reconnecting'))
    connection.onreconnected(() => setConnectionState('live'))
    connection.onclose(() => setConnectionState('offline'))

    connection
      .start()
      .then(() => setConnectionState(connection.state === HubConnectionState.Connected ? 'live' : connection.state))
      .catch((error) => {
        if (String(error).includes('401') || String(error).includes('Unauthorized')) {
          window.location.href = '/login.html'
          return
        }

        setConnectionState('offline')
      })

    return () => {
      connection.stop()
    }
  }, [])

  const graphData = useMemo(() => {
    const nodes = new Map<string, GraphNode>()
    const links: GraphLink[] = []

    nodes.set('ai', {
      id: 'ai',
      name: `AI Core ${aiState.direction || 'NEUTRAL'}`,
      kind: 'ai',
      color: aiState.direction === 'BULLISH' ? '#22c55e' : aiState.direction === 'BEARISH' ? '#ef4444' : '#67e8f9',
      size: 18,
    })

    nodes.set('okx', {
      id: 'okx',
      name: `OKX ${formatUsd(operations?.okx?.totalUsd)}`,
      kind: 'okx',
      color: '#f97316',
      size: 11,
    })

    wallets.forEach((wallet) => {
      const id = `wallet:${wallet.walletAddress}`
      nodes.set(id, {
        id,
        name: wallet.label || shortAddress(wallet.walletAddress),
        kind: 'wallet',
        color: wallet.isActive ? '#a78bfa' : '#64748b',
        size: 6 + Math.min(10, Number(wallet.confidenceScore || 0) / 10),
        wallet,
      })
      links.push({ source: id, target: 'ai', color: wallet.isActive ? '#7dd3fc' : '#475569', particles: wallet.isActive ? 1 : 0 })
    })

    events.slice(0, 40).forEach((event) => {
      const eventId = `event:${event.id}`
      nodes.set(eventId, {
        id: eventId,
        name: event.type,
        kind: 'event',
        color: event.severity === 'danger' ? '#ef4444' : event.severity === 'success' ? '#22c55e' : '#facc15',
        size: 4 + Math.min(8, Number(event.usdValue || 0) / 25000),
        event,
      })

      const walletId = `wallet:${event.walletAddress}`
      if (event.walletAddress && nodes.has(walletId)) {
        links.push({ source: walletId, target: eventId, color: '#38bdf8', particles: 2 })
      }
      links.push({ source: eventId, target: 'ai', color: '#facc15', particles: 4 })
      if (tradeEventTypes.has(event.type)) {
        links.push({ source: 'ai', target: 'okx', color: event.severity === 'success' ? '#22c55e' : '#ef4444', particles: 5 })
      }
    })

    return { nodes: Array.from(nodes.values()), links }
  }, [aiState.direction, events, operations?.okx?.totalUsd, wallets])

  const nodeThreeObject = useCallback((node: GraphNode) => {
    const group = new THREE.Group()
    const geometry = node.kind === 'ai'
      ? new THREE.IcosahedronGeometry(node.size / 5, 3)
      : node.kind === 'okx'
        ? new THREE.OctahedronGeometry(node.size / 4, 1)
        : new THREE.SphereGeometry(node.size / 4, 24, 24)
    const material = new THREE.MeshStandardMaterial({
      color: node.color,
      emissive: node.color,
      emissiveIntensity: node.kind === 'ai' ? 0.65 : 0.28,
      roughness: 0.38,
      metalness: node.kind === 'event' ? 0.45 : 0.12,
    })
    group.add(new THREE.Mesh(geometry, material))

    const label = new SpriteText(node.name)
    label.color = '#dbeafe'
    label.textHeight = node.kind === 'ai' ? 4.2 : 2.4
    label.position.y = node.size / 3 + 3
    group.add(label)
    return group
  }, [])

  const handleNodeClick = useCallback((node: GraphNode) => {
    setSelected(node)
    if (graphRef.current) {
      const distance = 90
      const distRatio = 1 + distance / Math.hypot(node.x || 1, node.y || 1, node.z || 1)
      graphRef.current.cameraPosition(
        { x: (node.x || 0) * distRatio, y: (node.y || 0) * distRatio, z: (node.z || 0) * distRatio },
        node,
        900,
      )
    }
  }, [])

  const runScan = async () => {
    try {
      const result = await fetchJson<{ scanId: number }>('/api/historical-scans/uniswap-v3', {
        method: 'POST',
        body: JSON.stringify({
          ...scanForm,
          preCrashStartUtc: new Date(scanForm.preCrashStartUtc).toISOString(),
          preCrashEndUtc: new Date(scanForm.preCrashEndUtc).toISOString(),
          dipBuyStartUtc: new Date(scanForm.dipBuyStartUtc).toISOString(),
          dipBuyEndUtc: new Date(scanForm.dipBuyEndUtc).toISOString(),
        }),
      })
      await loadMissionState()
      await loadCandidates(result.scanId)
      setActiveTab('insider')
    } catch (error) {
      setAlert(error instanceof Error ? error.message : 'Historical scan failed')
    }
  }

  const loadCandidates = async (scanId: number) => {
    const rows = await fetchJson<Candidate[]>(`/api/historical-scans/${scanId}/candidates`)
    setCandidates(rows)
  }

  const promoteCandidate = async (candidate: Candidate) => {
    await fetchJson(`/api/tracked-wallets/from-candidate/${candidate.id}`, {
      method: 'POST',
      body: JSON.stringify({
        label: `insider-${candidate.assetSymbol}-${candidate.id}`,
        isActive: true,
        notes: 'Added from Universe Insider Lab.',
      }),
    })
    await loadMissionState()
  }

  const sendChat = async () => {
    const question = chatQuestion.trim()
    if (!question) return
    setChatQuestion('')
    setChatLines((lines) => [...lines, { role: 'user', text: question }])
    try {
      const response = await fetchJson<{ answer: string }>('/api/dashboard/chat', {
        method: 'POST',
        body: JSON.stringify({ question }),
      })
      setChatLines((lines) => [...lines, { role: 'ai', text: response.answer || 'No answer.' }])
    } catch (error) {
      setChatLines((lines) => [...lines, { role: 'ai', text: error instanceof Error ? error.message : 'AI unavailable.' }])
    }
  }

  const selectedPayload = parsePayload(selected?.event)

  return (
    <main className="mission-shell">
      <section className="universe-stage">
        <header className="topbar">
          <div>
            <p className="eyebrow">WhaleTracker Mission Control</p>
            <h1>Living AI Wallet Universe</h1>
          </div>
          <div className="status-cluster">
            <span className={`status-pill ${connectionState}`}>{connectionState}</span>
            <span className="status-pill"><ShieldCheck size={15} /> auth</span>
            <button className="icon-button" onClick={loadMissionState} aria-label="Refresh mission state"><RefreshCw size={17} /></button>
          </div>
        </header>

        {alert && <div className="alert-line">{alert}</div>}

        <div className="graph-wrap">
          <ForceGraph3D
            ref={graphRef}
            graphData={graphData}
            backgroundColor="rgba(0,0,0,0)"
            nodeThreeObject={nodeThreeObject}
            nodeRelSize={4}
            linkColor={(link: GraphLink) => link.color}
            linkOpacity={0.42}
            linkWidth={1.2}
            linkDirectionalParticles={(link: GraphLink) => link.particles}
            linkDirectionalParticleSpeed={0.012}
            linkDirectionalParticleWidth={2.5}
            onNodeClick={handleNodeClick}
            cooldownTicks={140}
          />
        </div>

        <section className="bottom-dock">
          <div className="metric-block">
            <BrainCircuit size={18} />
            <div>
              <span>AI Bias</span>
              <strong>{aiState.direction || 'NEUTRAL'} {Number(aiState.biasScore || 0).toFixed(1)}</strong>
            </div>
          </div>
          <div className="metric-block">
            <CircleDollarSign size={18} />
            <div>
              <span>OKX Equity</span>
              <strong>{formatUsd(operations?.okx?.totalUsd)}</strong>
            </div>
          </div>
          <div className="metric-block">
            <Radar size={18} />
            <div>
              <span>Tracked Wallets</span>
              <strong>{wallets.filter((wallet) => wallet.isActive).length} active</strong>
            </div>
          </div>
          <div className="metric-block">
            <Zap size={18} />
            <div>
              <span>Live Events</span>
              <strong>{events.length}</strong>
            </div>
          </div>
        </section>
      </section>

      <aside className="side-panel">
        <div className="ai-vitals">
          <Canvas camera={{ position: [0, 0, 5], fov: 45 }}>
            <AiCoreOrb bias={aiState.direction} />
          </Canvas>
        </div>

        <div className="panel-section">
          <div className="section-title"><Bot size={17} /> AI Core</div>
          <p className="summary">{aiState.summary || 'No AI memory summary recorded yet.'}</p>
        </div>

        <div className="panel-section selected-panel">
          <div className="section-title"><Activity size={17} /> Selection</div>
          {!selected && <p className="muted">Click a wallet, event, AI core, or OKX node.</p>}
          {selected?.wallet && (
            <div className="detail-grid">
              <span>Wallet</span><strong>{shortAddress(selected.wallet.walletAddress)}</strong>
              <span>Source</span><strong>{selected.wallet.source || '--'}</strong>
              <span>Confidence</span><strong>{Number(selected.wallet.confidenceScore || 0).toFixed(1)}</strong>
              <span>Profit</span><strong>{formatUsd(selected.wallet.estimatedProfitUsd)}</strong>
              <span>Last tx</span><strong>{shortAddress(selected.wallet.lastSeenTxHash)}</strong>
            </div>
          )}
          {selected?.event && (
            <div className="event-detail">
              <div className="event-kind">{selected.event.type}</div>
              <p>{selected.event.summary}</p>
              <div className="detail-grid">
                <span>Wallet</span><strong>{shortAddress(selected.event.walletAddress)}</strong>
                <span>Symbol</span><strong>{selected.event.symbol || '--'}</strong>
                <span>Value</span><strong>{formatUsd(selected.event.usdValue)}</strong>
                <span>Tx</span><strong>{shortAddress(selected.event.txHash)}</strong>
                <span>Time</span><strong>{formatTime(selected.event.createdAt)}</strong>
              </div>
              {selectedPayload && <pre>{JSON.stringify(selectedPayload, null, 2)}</pre>}
            </div>
          )}
          {selected?.kind === 'ai' && <p className="summary">{aiState.summary || 'AI is idle until a wallet event arrives.'}</p>}
          {selected?.kind === 'okx' && (
            <div className="detail-grid">
              <span>Mode</span><strong>{operations?.okx?.mode || '--'}</strong>
              <span>Available</span><strong>{operations?.okx?.available ? 'yes' : 'no'}</strong>
              <span>Equity</span><strong>{formatUsd(operations?.okx?.totalUsd)}</strong>
              <span>Positions</span><strong>{operations?.okx?.positions ?? 0}</strong>
            </div>
          )}
        </div>

        <nav className="tab-row">
          <button className={activeTab === 'events' ? 'active' : ''} onClick={() => setActiveTab('events')}>Events</button>
          <button className={activeTab === 'wallets' ? 'active' : ''} onClick={() => setActiveTab('wallets')}>Wallets</button>
          <button className={activeTab === 'insider' ? 'active' : ''} onClick={() => setActiveTab('insider')}>Insider Lab</button>
          <button className={activeTab === 'chat' ? 'active' : ''} onClick={() => setActiveTab('chat')}>Chat</button>
        </nav>

        <div className="tab-panel">
          {activeTab === 'events' && (
            <div className="timeline">
              {events.length === 0 && <p className="muted">No live events recorded yet.</p>}
              {events.slice(0, 40).map((event) => (
                <button key={event.id} className="timeline-row" onClick={() => setSelected({ id: `event:${event.id}`, name: event.type, kind: 'event', color: '#facc15', size: 5, event })}>
                  <span>{event.type}</span>
                  <strong>{event.summary}</strong>
                  <small>{formatTime(event.createdAt)}</small>
                </button>
              ))}
            </div>
          )}

          {activeTab === 'wallets' && (
            <div className="wallet-list">
              {wallets.length === 0 && <p className="muted">No tracked wallets yet.</p>}
              {wallets.map((wallet) => (
                <button key={wallet.id} className="wallet-row" onClick={() => setSelected({ id: `wallet:${wallet.walletAddress}`, name: wallet.label || wallet.walletAddress, kind: 'wallet', color: '#a78bfa', size: 8, wallet })}>
                  <span>{wallet.label || shortAddress(wallet.walletAddress)}</span>
                  <strong>{Number(wallet.confidenceScore || 0).toFixed(1)}</strong>
                  <small>{wallet.source || '--'} · {wallet.isActive ? 'active' : 'paused'}</small>
                </button>
              ))}
            </div>
          )}

          {activeTab === 'insider' && (
            <div className="insider-lab">
              <div className="scan-grid">
                <label>Pre-crash start<input type="datetime-local" value={scanForm.preCrashStartUtc} onChange={(e) => setScanForm({ ...scanForm, preCrashStartUtc: e.target.value })} /></label>
                <label>Pre-crash end<input type="datetime-local" value={scanForm.preCrashEndUtc} onChange={(e) => setScanForm({ ...scanForm, preCrashEndUtc: e.target.value })} /></label>
                <label>Dip-buy start<input type="datetime-local" value={scanForm.dipBuyStartUtc} onChange={(e) => setScanForm({ ...scanForm, dipBuyStartUtc: e.target.value })} /></label>
                <label>Dip-buy end<input type="datetime-local" value={scanForm.dipBuyEndUtc} onChange={(e) => setScanForm({ ...scanForm, dipBuyEndUtc: e.target.value })} /></label>
              </div>
              <button className="primary-action" onClick={runScan}><Database size={16} /> Run historical scan</button>
              <div className="scan-list">
                {scans.map((scan) => (
                  <button key={scan.id} onClick={() => loadCandidates(scan.id)}>
                    Scan #{scan.id} · {scan.candidateCount} candidates · {formatTime(scan.createdAt)}
                  </button>
                ))}
              </div>
              <div className="candidate-list">
                {candidates.map((candidate) => (
                  <div className="candidate-row" key={candidate.id}>
                    <div>
                      <strong>{shortAddress(candidate.walletAddress)}</strong>
                      <span>{candidate.assetSymbol} · score {Number(candidate.insiderScore || 0).toFixed(1)} · profit {formatUsd(candidate.estimatedProfitUsd)}</span>
                    </div>
                    <button onClick={() => promoteCandidate(candidate)} aria-label="Promote candidate"><Plus size={16} /></button>
                  </div>
                ))}
              </div>
            </div>
          )}

          {activeTab === 'chat' && (
            <div className="chat-panel">
              <div className="chat-lines">
                {chatLines.length === 0 && <p className="muted">Ask the AI about wallet bias, OKX exposure, or recent decisions.</p>}
                {chatLines.map((line, index) => (
                  <div key={`${line.role}-${index}`} className={`chat-line ${line.role}`}>{line.text}</div>
                ))}
              </div>
              <div className="chat-input">
                <input value={chatQuestion} onChange={(e) => setChatQuestion(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && sendChat()} placeholder="Piyasa biası ve son hareketler ne söylüyor?" />
                <button onClick={sendChat} aria-label="Send chat"><Send size={17} /></button>
              </div>
            </div>
          )}
        </div>
      </aside>
    </main>
  )
}

export default App
