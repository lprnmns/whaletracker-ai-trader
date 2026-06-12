import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent } from 'react'
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
  GripVertical,
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

type TraderScan = {
  id: number
  startUtc: string
  endUtc: string
  minimumStartingValueUsd: number
  requestedTop: number
  evaluatedWalletCount: number
  qualifiedWalletCount: number
  state: string
  progressPercent: number
  currentStage: string
  statusMessage: string
  errorMessage: string
  progressLog: TraderDiscoveryProgress[]
  createdAt: string
}

type TraderCandidate = {
  id: number
  traderScanId: number
  walletAddress: string
  startingValueUsd: number
  endingValueUsd: number
  receivedExternalUsd: number
  sentExternalUsd: number
  totalFeesUsd: number
  adjustedProfitUsd: number
  adjustedReturnPercent: number
  realizedGainUsd: number
  score: number
  startPointUtc: string
  endPointUtc: string
  chartPeriod: string
}

type TraderDiscoveryRun = {
  id: number
  provider: string
  executionId: string
  state: string
  lookbackDays: number
  minimumActiveWeeks: number
  minimumMeaningfulSwaps: number
  minimumSwapUsd: number
  candidateLimit: number
  candidateCount: number
  progressPercent: number
  currentStage: string
  statusMessage: string
  errorMessage: string
  progressLog: TraderDiscoveryProgress[]
  startedAtUtc: string
  completedAtUtc: string
  createdAt: string
}

type TraderDiscoveryProgress = {
  percent: number
  stage: string
  state: string
  message: string
  executionId: string
  timestampUtc: string
}

type TraderDiscoveryCandidate = {
  id: number
  traderDiscoveryRunId: number
  walletAddress: string
  meaningfulSwapCount: number
  activeWeekCount: number
  approvedNotionalUsd: number
  averageSwapUsd: number
  maximumDailySwaps: number
  distinctMajorAssets: number
  copyabilityScore: number
  activeChainCount: number
  activeChains: string[]
  firstTradeUtc: string
  lastTradeUtc: string
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
  flowCreatedAt?: number
  flowExpiresAt?: number
}

type GraphLink = {
  source: string
  target: string
  color: string
  particles: number
  flowCreatedAt?: number
  flowExpiresAt?: number
}

type Tab = 'events' | 'wallets' | 'insider' | 'chat'

type ChatAiMeta = {
  provider: string
  model: string
  mode: string
  elapsedMs: number
  source: string
  sourceWallet: string
  positions: number
  usedGroq: boolean
}

type ChatLine = {
  role: 'user' | 'ai'
  text: string
  meta?: ChatAiMeta
}

const FLOW_LIFETIME_MS = 120_000
const FLOW_FADE_START_MS = 60_000
const animatedEventTypes = new Set(['WalletActivityDetected', 'AiDecisionCompleted', 'TradeSubmitted', 'TradeRejected', 'TradeSkipped'])

function isSkippedTrade(event: LiveEvent) {
  if (event.type === 'TradeSkipped') return true
  if (event.type !== 'TradeRejected') return false
  const payload = parsePayload(event)
  return event.summary.toLowerCase().startsWith('trade skipped:') ||
    String(payload?.decision || '').toUpperCase() === 'IGNORE'
}

function isManualExecutionProbe(event: LiveEvent) {
  return parsePayload(event)?.mode === 'live-execution-probe'
}

function eventStepLabel(event: LiveEvent) {
  const payload = parsePayload(event)
  if (event.type === 'WalletActivityDetected') {
    return `${event.symbol || 'Wallet'} movement ${formatUsd(event.usdValue)}`
  }
  if (event.type === 'AiDecisionCompleted') {
    const decision = payload?.decision || payload?.action || 'Decision'
    return `AI: ${decision} ${event.symbol || ''}`.trim()
  }
  if (event.type === 'TradeSubmitted') {
    return `OKX accepted ${event.symbol || ''}`.trim()
  }
  if (event.type === 'TradeRejected') {
    const request = payload?.request
    return request
      ? `OKX rejected ${request.side || ''} ${request.symbol || event.symbol || ''}`.trim()
      : `OKX rejected ${event.symbol || ''}`.trim()
  }
  if (event.type === 'TradeSkipped') {
    return `No trade: ${event.symbol || 'ignored'}`
  }
  return event.type
}

function flowOpacity(createdAt?: number, expiresAt?: number) {
  if (!createdAt || !expiresAt) return 0.42
  const now = Date.now()
  if (now >= expiresAt) return 0
  const age = now - createdAt
  if (age <= FLOW_FADE_START_MS) return 0.72
  return Math.max(0, 0.72 * (expiresAt - now) / (FLOW_LIFETIME_MS - FLOW_FADE_START_MS))
}

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
    let message = text || `HTTP ${response.status}`
    try {
      const payload = JSON.parse(text)
      message = payload.message || payload.error || message
    } catch {
      // Keep the raw response text when the error body is not JSON.
    }
    throw new Error(message)
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

function makeCanvasSprite(
  width: number,
  height: number,
  draw: (context: CanvasRenderingContext2D) => void,
  scale: [number, number],
) {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) return new THREE.Sprite()

  context.clearRect(0, 0, width, height)
  draw(context)
  const texture = new THREE.CanvasTexture(canvas)
  texture.needsUpdate = true
  texture.colorSpace = THREE.SRGBColorSpace

  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
    map: texture,
    transparent: true,
    depthTest: false,
    depthWrite: false,
  }))
  sprite.scale.set(scale[0], scale[1], 1)
  sprite.renderOrder = 30
  return sprite
}

function makeOkxBillboard(totalUsd?: number) {
  return makeCanvasSprite(640, 300, (context) => {
    context.fillStyle = '#ffffff'

    const scale = 0.62
    const offsetX = 88
    const offsetY = 50
    const rect = (x: number, y: number, width: number, height: number) => {
      context.fillRect(offsetX + (x - 166) * scale, offsetY + (y - 428) * scale, width * scale, height * scale)
    }

    rect(166, 428, 224, 224)
    context.save()
    context.globalCompositeOperation = 'destination-out'
    rect(241, 503, 75, 75)
    context.restore()

    rect(428, 428, 75, 224)
    rect(503, 503, 75, 75)
    rect(577, 428, 75, 75)
    rect(577, 577, 75, 75)
    rect(689, 428, 75, 75)
    rect(689, 577, 75, 75)
    rect(764, 503, 75, 75)
    rect(838, 428, 75, 75)
    rect(838, 577, 75, 75)

    context.fillStyle = '#fed7aa'
    context.font = '800 42px Arial, Helvetica, sans-serif'
    context.textAlign = 'center'
    context.textBaseline = 'middle'
    context.fillText(formatUsd(totalUsd), 320, 235)
  }, [30, 14])
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
        <dodecahedronGeometry args={[1.22, 0]} />
        <meshStandardMaterial color="#07111f" emissive={color} emissiveIntensity={0.62} roughness={0.18} metalness={0.74} />
      </mesh>
      <mesh rotation={[0.62, 0.12, 0.8]}>
        <torusGeometry args={[1.72, 0.018, 8, 96]} />
        <meshBasicMaterial color={color} transparent opacity={0.72} />
      </mesh>
      <mesh rotation={[1.35, 0.7, 0.15]}>
        <torusGeometry args={[1.38, 0.014, 8, 96]} />
        <meshBasicMaterial color="#e0f2fe" transparent opacity={0.46} />
      </mesh>
      <OrbitControls enableZoom={false} enablePan={false} autoRotate autoRotateSpeed={0.6} />
    </>
  )
}

function App() {
  const graphRef = useRef<any>(null)
  const aiCoreVisualRef = useRef<THREE.Group | null>(null)
  const panelResizeRef = useRef({ startX: 0, startWidth: 420 })
  const [wallets, setWallets] = useState<Wallet[]>([])
  const [events, setEvents] = useState<LiveEvent[]>([])
  const [aiState, setAiState] = useState<AiState>({ biasScore: 0, direction: 'NEUTRAL', summary: '', eventCount: 0 })
  const [operations, setOperations] = useState<OperationsSnapshot | null>(null)
  const [traderScans, setTraderScans] = useState<TraderScan[]>([])
  const [traderCandidates, setTraderCandidates] = useState<TraderCandidate[]>([])
  const [activeTraderScan, setActiveTraderScan] = useState<TraderScan | null>(null)
  const [discoveryRuns, setDiscoveryRuns] = useState<TraderDiscoveryRun[]>([])
  const [discoveryCandidates, setDiscoveryCandidates] = useState<TraderDiscoveryCandidate[]>([])
  const [activeDiscoveryRun, setActiveDiscoveryRun] = useState<TraderDiscoveryRun | null>(null)
  const [isDiscoveryRunning, setIsDiscoveryRunning] = useState(false)
  const [isTraderScanRunning, setIsTraderScanRunning] = useState(false)
  const [selected, setSelected] = useState<GraphNode | null>(null)
  const [activeTab, setActiveTab] = useState<Tab>('events')
  const [connectionState, setConnectionState] = useState('connecting')
  const [alert, setAlert] = useState('')
  const [chatQuestion, setChatQuestion] = useState('')
  const [chatLines, setChatLines] = useState<ChatLine[]>([])
  const [isChatThinking, setIsChatThinking] = useState(false)
  const [eventPulseRevision, setEventPulseRevision] = useState(0)
  const [panelWidth, setPanelWidth] = useState(() => {
    const stored = Number(window.localStorage.getItem('mission-control-panel-width'))
    return Number.isFinite(stored) && stored >= 340 ? stored : 420
  })
  const [isPanelResizing, setIsPanelResizing] = useState(false)
  const [traderForm, setTraderForm] = useState({
    startUtc: '',
    endUtc: '',
    minimumStartingValueUsd: 100000,
    top: 10,
    candidateWallets: '',
  })
  const [discoveryForm, setDiscoveryForm] = useState({
    lookbackDays: 28,
    minimumActiveWeeks: 3,
    minimumMeaningfulSwaps: 4,
    minimumSwapUsd: 1500,
    candidateLimit: 100,
  })

  const clampPanelWidth = useCallback((width: number) => {
    const maximum = Math.min(760, Math.max(420, window.innerWidth * 0.58))
    return Math.round(Math.min(maximum, Math.max(340, width)))
  }, [])

  const beginPanelResize = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (window.innerWidth <= 1120) return
    panelResizeRef.current = { startX: event.clientX, startWidth: panelWidth }
    setIsPanelResizing(true)
    event.currentTarget.setPointerCapture(event.pointerId)
    event.preventDefault()
  }

  const resizePanelWithKeyboard = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
    event.preventDefault()
    setPanelWidth((current) => clampPanelWidth(current + (event.key === 'ArrowLeft' ? 24 : -24)))
  }

  useEffect(() => {
    if (!isPanelResizing) return

    const handlePointerMove = (event: PointerEvent) => {
      const nextWidth = panelResizeRef.current.startWidth + panelResizeRef.current.startX - event.clientX
      setPanelWidth(clampPanelWidth(nextWidth))
    }
    const stopResizing = () => setIsPanelResizing(false)

    window.addEventListener('pointermove', handlePointerMove)
    window.addEventListener('pointerup', stopResizing, { once: true })
    window.addEventListener('pointercancel', stopResizing, { once: true })
    return () => {
      window.removeEventListener('pointermove', handlePointerMove)
      window.removeEventListener('pointerup', stopResizing)
      window.removeEventListener('pointercancel', stopResizing)
    }
  }, [clampPanelWidth, isPanelResizing])

  useEffect(() => {
    window.localStorage.setItem('mission-control-panel-width', String(panelWidth))
    const frame = window.requestAnimationFrame(() => window.dispatchEvent(new Event('resize')))
    return () => window.cancelAnimationFrame(frame)
  }, [panelWidth])

  useEffect(() => {
    let animationFrame = 0
    const clock = new THREE.Clock()

    const animateAiCore = () => {
      const visual = aiCoreVisualRef.current
      if (visual) {
        const elapsed = clock.getElapsedTime()
        const core = visual.getObjectByName('ai-core-mesh')
        const wire = visual.getObjectByName('ai-core-wire')
        const orbitA = visual.getObjectByName('ai-orbit-a')
        const orbitB = visual.getObjectByName('ai-orbit-b')
        const orbitC = visual.getObjectByName('ai-orbit-c')

        if (core) {
          core.rotation.x = 0.2 + elapsed * 0.18
          core.rotation.y = 0.55 + elapsed * 0.34
          const pulse = 1 + Math.sin(elapsed * 1.8) * 0.055
          core.scale.setScalar(pulse)
        }
        if (wire && core) {
          wire.rotation.copy(core.rotation)
          wire.scale.copy(core.scale)
        }
        if (orbitA) orbitA.rotation.z = elapsed * 0.42
        if (orbitB) orbitB.rotation.x = elapsed * -0.31
        if (orbitC) orbitC.rotation.y = elapsed * 0.24
      }

      graphRef.current?.scene()?.traverse((object: THREE.Object3D) => {
        const createdAt = Number(object.userData?.flowCreatedAt || 0)
        const expiresAt = Number(object.userData?.flowExpiresAt || 0)
        if (!createdAt || !expiresAt) return

        const opacity = flowOpacity(createdAt, expiresAt) / 0.72
        object.visible = opacity > 0
        object.traverse((child: THREE.Object3D) => {
          const material = (child as THREE.Mesh).material as THREE.Material | THREE.Material[] | undefined
          const materials = Array.isArray(material) ? material : material ? [material] : []
          materials.forEach((item) => {
            item.transparent = true
            item.opacity = opacity
          })
        })
      })

      animationFrame = requestAnimationFrame(animateAiCore)
    }

    animateAiCore()
    return () => cancelAnimationFrame(animationFrame)
  }, [])

  useEffect(() => {
    const now = Date.now()
    const transitions = events.flatMap((event) => {
      const createdAt = new Date(event.createdAt).getTime()
      return [createdAt + 12_000, createdAt + FLOW_LIFETIME_MS]
    })
    const nextExpiry = transitions
      .filter((transitionAt) => transitionAt > now)
      .sort((a, b) => a - b)[0]

    if (!nextExpiry) return

    const timeout = window.setTimeout(
      () => setEventPulseRevision((revision) => revision + 1),
      Math.max(0, nextExpiry - now + 50),
    )
    return () => window.clearTimeout(timeout)
  }, [events, eventPulseRevision])

  const loadMissionState = useCallback(async () => {
    try {
      const [walletList, eventList, state, ops, traderScanList, discoveryRunList] = await Promise.all([
        fetchJson<Wallet[]>('/api/tracked-wallets?includeInactive=true'),
        fetchJson<LiveEvent[]>('/api/live-events?count=120'),
        fetchJson<AiState>('/api/ai-memory/state'),
        fetchJson<OperationsSnapshot>('/api/operations/snapshot'),
        fetchJson<TraderScan[]>('/api/trader-finder/scans?count=10'),
        fetchJson<TraderDiscoveryRun[]>('/api/trader-finder/discovery-runs?count=10'),
      ])
      setWallets((current) => JSON.stringify(current) === JSON.stringify(walletList) ? current : walletList)
      setEvents((current) => JSON.stringify(current) === JSON.stringify(eventList) ? current : eventList)
      setAiState(state)
      setOperations(ops)
      setTraderScans((current) => JSON.stringify(current) === JSON.stringify(traderScanList) ? current : traderScanList)
      setDiscoveryRuns((current) => JSON.stringify(current) === JSON.stringify(discoveryRunList) ? current : discoveryRunList)
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

    connection.on('traderDiscoveryProgress', (run: TraderDiscoveryRun) => {
      setActiveDiscoveryRun(run)
      setDiscoveryRuns((current) => [run, ...current.filter((item) => item.id !== run.id)].slice(0, 10))
    })

    connection.on('traderPerformanceProgress', (scan: TraderScan) => {
      setActiveTraderScan(scan)
      setTraderScans((current) => [scan, ...current.filter((item) => item.id !== scan.id)].slice(0, 10))
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
    const now = Date.now()

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
      size: 15,
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
    })

    const flows = new Map<string, LiveEvent[]>()
    events.forEach((event) => {
      if (!animatedEventTypes.has(event.type) && event.type !== 'AiAwakened') return
      const key = isManualExecutionProbe(event)
        ? `probe:${event.id}`
        : event.txHash
        ? `${event.walletAddress.toLowerCase()}:${event.txHash.toLowerCase()}`
        : `event:${event.id}`
      flows.set(key, [...(flows.get(key) || []), event])
    })

    flows.forEach((flowEvents) => {
      const ordered = flowEvents.sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
      const visibleSteps = ordered.filter((event) => event.type !== 'AiAwakened')
      const latestAt = Math.max(...ordered.map((event) => new Date(event.createdAt).getTime()))
      const flowExpiresAt = latestAt + FLOW_LIFETIME_MS
      if (flowExpiresAt <= now) return

      const flowCreatedAt = Math.min(...ordered.map((event) => new Date(event.createdAt).getTime()))
      const activity = visibleSteps.find((event) => event.type === 'WalletActivityDetected')
      const decision = [...visibleSteps].reverse().find((event) => event.type === 'AiDecisionCompleted')
      const execution = [...visibleSteps].reverse().find((event) =>
        event.type === 'TradeSubmitted' || (event.type === 'TradeRejected' && !isSkippedTrade(event)))
      const manualProbe = execution ? isManualExecutionProbe(execution) : false
      const skipped = [...visibleSteps].reverse().find((event) => isSkippedTrade(event))
      const walletAddress = ordered.find((event) => event.walletAddress)?.walletAddress || ''
      const walletId = `wallet:${walletAddress}`
      let previousId = nodes.has(walletId) ? walletId : ''

      const addStep = (event: LiveEvent, color: string) => {
        const eventId = `event:${event.id}`
        const isFresh = now - new Date(event.createdAt).getTime() <= 12_000
        nodes.set(eventId, {
          id: eventId,
          name: eventStepLabel(event),
          kind: 'event',
          color,
          size: 4 + Math.min(8, Number(event.usdValue || 0) / 25000),
          event,
          flowCreatedAt,
          flowExpiresAt,
        })
        if (previousId) {
          links.push({
            source: previousId,
            target: eventId,
            color,
            particles: isFresh ? 3 : 0,
            flowCreatedAt,
            flowExpiresAt,
          })
        }
        previousId = eventId
      }

      if (activity) addStep(activity, '#38bdf8')
      if (!manualProbe && previousId) {
        links.push({
          source: previousId,
          target: 'ai',
          color: '#67e8f9',
          particles: now - latestAt <= 12_000 ? 4 : 0,
          flowCreatedAt,
          flowExpiresAt,
        })
      }
      if (!manualProbe) {
        previousId = 'ai'
        if (decision) addStep(decision, '#facc15')
        if (skipped) addStep(skipped, '#94a3b8')
      }
      if (execution) {
        addStep(execution, execution.type === 'TradeSubmitted' ? '#22c55e' : '#ef4444')
        links.push({
          source: previousId,
          target: 'okx',
          color: execution.type === 'TradeSubmitted' ? '#22c55e' : '#ef4444',
          particles: now - new Date(execution.createdAt).getTime() <= 12_000 ? 5 : 0,
          flowCreatedAt,
          flowExpiresAt,
        })
      }
    })

    return { nodes: Array.from(nodes.values()), links }
  }, [aiState.direction, eventPulseRevision, events, operations?.okx?.totalUsd, wallets])

  const nodeThreeObject = useCallback((node: GraphNode) => {
    const group = new THREE.Group()

    if (node.kind === 'ai') {
      const coreMaterial = new THREE.MeshStandardMaterial({
        color: '#07111f',
        emissive: node.color,
        emissiveIntensity: 0.54,
        roughness: 0.16,
        metalness: 0.78,
      })
      const coreGeometry = new THREE.DodecahedronGeometry(4.9, 0)
      const core = new THREE.Mesh(coreGeometry, coreMaterial)
      core.name = 'ai-core-mesh'
      core.rotation.set(0.2, 0.55, -0.12)
      group.add(core)

      const wire = new THREE.Mesh(
        coreGeometry,
        new THREE.MeshBasicMaterial({
          color: node.color,
          wireframe: true,
          transparent: true,
          opacity: 0.68,
        }),
      )
      wire.name = 'ai-core-wire'
      wire.rotation.copy(core.rotation)
      group.add(wire)

      const ringMaterial = new THREE.MeshBasicMaterial({
        color: node.color,
        transparent: true,
        opacity: 0.62,
      })
      const ringA = new THREE.Mesh(new THREE.TorusGeometry(7.0, 0.06, 10, 96), ringMaterial)
      const ringB = new THREE.Mesh(new THREE.TorusGeometry(5.8, 0.04, 10, 96), ringMaterial)
      const ringC = new THREE.Mesh(new THREE.TorusGeometry(8.2, 0.025, 10, 96), new THREE.MeshBasicMaterial({
        color: '#e0f2fe',
        transparent: true,
        opacity: 0.34,
      }))
      ringA.rotation.x = Math.PI / 2.6
      ringB.rotation.y = Math.PI / 2.8
      ringC.rotation.set(Math.PI / 2.2, 0.45, 0.85)

      const orbitA = new THREE.Group()
      const orbitB = new THREE.Group()
      const orbitC = new THREE.Group()
      orbitA.name = 'ai-orbit-a'
      orbitB.name = 'ai-orbit-b'
      orbitC.name = 'ai-orbit-c'
      orbitA.add(ringA)
      orbitB.add(ringB)
      orbitC.add(ringC)
      group.add(orbitA, orbitB, orbitC)

      const satelliteGeometry = new THREE.OctahedronGeometry(0.58, 0)
      const satelliteMaterial = new THREE.MeshBasicMaterial({ color: '#ecfeff', transparent: true, opacity: 0.86 })
      const satellites = [
        { orbit: orbitA, position: [7.0, 0, 0] },
        { orbit: orbitA, position: [-7.0, 0, 0] },
        { orbit: orbitB, position: [0, 5.8, 0] },
        { orbit: orbitC, position: [8.2, 0, 0] },
      ] as const
      satellites.forEach(({ orbit, position }) => {
        const satellite = new THREE.Mesh(satelliteGeometry, satelliteMaterial)
        satellite.position.set(position[0], position[1], position[2])
        orbit.add(satellite)
      })

      const label = new SpriteText('AI CORE')
      label.color = '#a5f3fc'
      label.textHeight = 2.45
      label.position.y = 9.5
      label.material.depthTest = false
      label.renderOrder = 20
      group.add(label)
      aiCoreVisualRef.current = group
      return group
    }

    if (node.kind === 'okx') {
      group.add(makeOkxBillboard(operations?.okx?.totalUsd))
      return group
    }

    const geometry = new THREE.SphereGeometry(node.size / 4, 24, 24)
    const material = new THREE.MeshStandardMaterial({
      color: node.color,
      emissive: node.color,
      emissiveIntensity: 0.28,
      roughness: 0.38,
      metalness: node.kind === 'event' ? 0.45 : 0.12,
    })
    group.add(new THREE.Mesh(geometry, material))

    const label = new SpriteText(node.name)
    label.color = '#dbeafe'
    label.textHeight = 2.4
    label.position.y = node.size / 3 + 3
    group.add(label)
    if (node.flowCreatedAt && node.flowExpiresAt) {
      group.userData.flowCreatedAt = node.flowCreatedAt
      group.userData.flowExpiresAt = node.flowExpiresAt
    }
    return group
  }, [operations?.okx?.totalUsd])

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

  const runTraderScan = async () => {
    if (!traderForm.startUtc || !traderForm.endUtc || isTraderScanRunning) return
    setIsTraderScanRunning(true)
    try {
      const candidateWallets = traderForm.candidateWallets
        .split(/[\s,;]+/)
        .map((value) => value.trim())
        .filter(Boolean)
      const scan = await fetchJson<TraderScan>('/api/trader-finder/scan', {
        method: 'POST',
        body: JSON.stringify({
          startUtc: new Date(traderForm.startUtc).toISOString(),
          endUtc: new Date(traderForm.endUtc).toISOString(),
          minimumStartingValueUsd: traderForm.minimumStartingValueUsd,
          top: traderForm.top,
          includeTrackedWallets: true,
          candidateWallets,
        }),
      })
      setActiveTraderScan(scan)
      setTraderCandidates([])
      await loadMissionState()
    } catch (error) {
      setAlert(error instanceof Error ? error.message : 'Trader scan failed')
      setIsTraderScanRunning(false)
    }
  }

  const runTraderDiscovery = async () => {
    if (isDiscoveryRunning) return
    setIsDiscoveryRunning(true)
    try {
      const run = await fetchJson<TraderDiscoveryRun>('/api/trader-finder/discover', {
        method: 'POST',
        body: JSON.stringify(discoveryForm),
      })
      setActiveDiscoveryRun(run)
      setDiscoveryCandidates([])
      await loadMissionState()
    } catch (error) {
      setAlert(error instanceof Error ? error.message : 'Dune discovery failed')
      setIsDiscoveryRunning(false)
    }
  }

  const retryTraderDiscovery = async () => {
    if (!activeDiscoveryRun || activeDiscoveryRun.state !== 'FAILED' || isDiscoveryRunning) return
    setIsDiscoveryRunning(true)
    try {
      const run = await fetchJson<TraderDiscoveryRun>(
        `/api/trader-finder/discovery-runs/${activeDiscoveryRun.id}/retry`,
        { method: 'POST' },
      )
      setActiveDiscoveryRun(run)
      setDiscoveryCandidates([])
      setAlert('')
      await loadMissionState()
    } catch (error) {
      setAlert(error instanceof Error ? error.message : 'Dune retry failed')
      setIsDiscoveryRunning(false)
    }
  }

  const loadDiscoveryCandidates = async (runId: number) => {
    const run = await fetchJson<TraderDiscoveryRun>(`/api/trader-finder/discovery-runs/${runId}`)
    const rows = await fetchJson<TraderDiscoveryCandidate[]>(
      `/api/trader-finder/discovery-runs/${runId}/candidates`,
    )
    setActiveDiscoveryRun(run)
    setDiscoveryCandidates(rows)
    setTraderForm((current) => ({
      ...current,
      candidateWallets: rows.map((candidate) => candidate.walletAddress).join('\n'),
    }))
  }

  useEffect(() => {
    if (!activeDiscoveryRun ||
        activeDiscoveryRun.state === 'COMPLETED' ||
        activeDiscoveryRun.state === 'FAILED') {
      setIsDiscoveryRunning(false)
      return
    }

    setIsDiscoveryRunning(true)
    const timer = window.setInterval(async () => {
      try {
        const run = await fetchJson<TraderDiscoveryRun>(
          `/api/trader-finder/discovery-runs/${activeDiscoveryRun.id}`,
        )
        setActiveDiscoveryRun(run)
        setDiscoveryRuns((current) => [run, ...current.filter((item) => item.id !== run.id)].slice(0, 10))
        if (run.state === 'COMPLETED') {
          await loadDiscoveryCandidates(run.id)
          setAlert(run.candidateCount === 0 ? 'Dune scan completed, but no wallet matched these filters.' : '')
        } else if (run.state === 'FAILED') {
          setAlert(run.errorMessage || 'Dune discovery failed')
        }
      } catch (error) {
        setAlert(error instanceof Error ? error.message : 'Discovery progress unavailable')
      }
    }, 1000)
    return () => window.clearInterval(timer)
  }, [activeDiscoveryRun?.id, activeDiscoveryRun?.state])

  const loadTraderCandidates = async (scanId: number) => {
    const scan = await fetchJson<TraderScan>(`/api/trader-finder/scans/${scanId}`)
    const rows = await fetchJson<TraderCandidate[]>(`/api/trader-finder/scans/${scanId}/candidates`)
    setActiveTraderScan(scan)
    setTraderCandidates(rows)
  }

  useEffect(() => {
    if (!activeTraderScan ||
        activeTraderScan.state === 'COMPLETED' ||
        activeTraderScan.state === 'FAILED') {
      setIsTraderScanRunning(false)
      return
    }

    setIsTraderScanRunning(true)
    const timer = window.setInterval(async () => {
      try {
        const scan = await fetchJson<TraderScan>(`/api/trader-finder/scans/${activeTraderScan.id}`)
        setActiveTraderScan(scan)
        setTraderScans((current) => [scan, ...current.filter((item) => item.id !== scan.id)].slice(0, 10))
        if (scan.state === 'COMPLETED') {
          await loadTraderCandidates(scan.id)
        } else if (scan.state === 'FAILED') {
          setAlert(scan.errorMessage || 'Performance verification failed')
        }
      } catch (error) {
        setAlert(error instanceof Error ? error.message : 'Performance progress unavailable')
      }
    }, 1000)
    return () => window.clearInterval(timer)
  }, [activeTraderScan?.id, activeTraderScan?.state])

  const trackTraderCandidate = async (candidate: TraderCandidate) => {
    await fetchJson(`/api/trader-finder/candidates/${candidate.id}/track`, { method: 'POST' })
    await loadMissionState()
  }

  const trackTopTraders = async (scanId: number) => {
    await fetchJson(`/api/trader-finder/scans/${scanId}/track-top?limit=10`, { method: 'POST' })
    await loadMissionState()
  }

  const sendChat = async () => {
    const question = chatQuestion.trim()
    if (!question || isChatThinking) return
    setChatQuestion('')
    setChatLines((lines) => [...lines, { role: 'user', text: question }])
    setIsChatThinking(true)
    try {
      const response = await fetchJson<{ answer: string; ai?: ChatAiMeta }>('/api/dashboard/chat', {
        method: 'POST',
        body: JSON.stringify({ question }),
      })
      setChatLines((lines) => [...lines, { role: 'ai', text: response.answer || 'No answer.', meta: response.ai }])
    } catch (error) {
      setChatLines((lines) => [...lines, { role: 'ai', text: error instanceof Error ? error.message : 'AI unavailable.' }])
    } finally {
      setIsChatThinking(false)
    }
  }

  const selectedPayload = parsePayload(selected?.event)

  return (
    <main
      className={`mission-shell${isPanelResizing ? ' panel-resizing' : ''}`}
      style={{ '--side-panel-width': `${panelWidth}px` } as CSSProperties}
    >
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
            linkOpacity={(link: GraphLink) => flowOpacity(link.flowCreatedAt, link.flowExpiresAt)}
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
        <div
          className="panel-resize-handle"
          role="separator"
          aria-label="Resize side panel"
          aria-orientation="vertical"
          aria-valuemin={340}
          aria-valuemax={760}
          aria-valuenow={panelWidth}
          tabIndex={0}
          onPointerDown={beginPanelResize}
          onKeyDown={resizePanelWithKeyboard}
        >
          <GripVertical size={16} />
        </div>
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
          <button className={activeTab === 'insider' ? 'active' : ''} onClick={() => setActiveTab('insider')}>Trader Finder</button>
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
              <div className="section-heading">
                <strong>Dune discovery</strong>
                <span>Find active, copyable-major traders across Ethereum and L2s.</span>
              </div>
              <div className="scan-grid">
                <label>Lookback days<input type="number" min="7" max="180" value={discoveryForm.lookbackDays} onChange={(e) => setDiscoveryForm({ ...discoveryForm, lookbackDays: Number(e.target.value) })} /></label>
                <label>Active weeks<input type="number" min="1" max="26" value={discoveryForm.minimumActiveWeeks} onChange={(e) => setDiscoveryForm({ ...discoveryForm, minimumActiveWeeks: Number(e.target.value) })} /></label>
                <label>Min swaps<input type="number" min="1" max="1000" value={discoveryForm.minimumMeaningfulSwaps} onChange={(e) => setDiscoveryForm({ ...discoveryForm, minimumMeaningfulSwaps: Number(e.target.value) })} /></label>
                <label>Min swap USD<input type="number" min="1" step="100" value={discoveryForm.minimumSwapUsd} onChange={(e) => setDiscoveryForm({ ...discoveryForm, minimumSwapUsd: Number(e.target.value) })} /></label>
                <label>Candidate limit<input type="number" min="1" max="500" value={discoveryForm.candidateLimit} onChange={(e) => setDiscoveryForm({ ...discoveryForm, candidateLimit: Number(e.target.value) })} /></label>
              </div>
              <button className="primary-action" disabled={isDiscoveryRunning} onClick={runTraderDiscovery}>
                <Database size={16} /> {isDiscoveryRunning ? 'Scanning Dune...' : 'Discover active traders'}
              </button>
              {activeDiscoveryRun && (
                <div className={`discovery-progress ${activeDiscoveryRun.state === 'FAILED' ? 'failed' : ''}`}>
                  <div className="progress-summary">
                    <strong>{activeDiscoveryRun.currentStage.replaceAll('_', ' ')}</strong>
                    <span>{activeDiscoveryRun.progressPercent}%</span>
                  </div>
                  <div
                    className="progress-track"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={activeDiscoveryRun.progressPercent}
                  >
                    <span style={{ width: `${activeDiscoveryRun.progressPercent}%` }} />
                  </div>
                  <p>{activeDiscoveryRun.errorMessage || activeDiscoveryRun.statusMessage}</p>
                  {activeDiscoveryRun.executionId && <small>Dune execution: {activeDiscoveryRun.executionId}</small>}
                  {activeDiscoveryRun.state === 'FAILED' && (
                    <button className="retry-action" onClick={retryTraderDiscovery}>
                      <RefreshCw size={14} /> Retry discovery
                    </button>
                  )}
                  <div className="progress-log">
                    {[...(activeDiscoveryRun.progressLog || [])].reverse().map((entry, index) => (
                      <div key={`${entry.timestampUtc}-${index}`} className={entry.state === 'FAILED' ? 'error' : ''}>
                        <time>{formatTime(entry.timestampUtc)}</time>
                        <span>{entry.stage.replaceAll('_', ' ')}</span>
                        <p>{entry.message}</p>
                      </div>
                    ))}
                  </div>
                </div>
              )}
              <div className="scan-list">
                {discoveryRuns.map((run) => (
                  <button key={run.id} onClick={() => loadDiscoveryCandidates(run.id)}>
                    Discovery #{run.id} · {run.progressPercent}% · {run.state} · {run.candidateCount} candidates
                  </button>
                ))}
              </div>
              <div className="candidate-list">
                {discoveryCandidates.map((candidate) => (
                  <div className="candidate-row" key={candidate.id}>
                    <div>
                      <strong>{shortAddress(candidate.walletAddress)}</strong>
                      <span>
                        copy {Number(candidate.copyabilityScore || 0).toFixed(1)} · {candidate.meaningfulSwapCount} swaps · max/day {candidate.maximumDailySwaps}
                      </span>
                      <small>
                        avg {formatUsd(candidate.averageSwapUsd)} · {candidate.distinctMajorAssets} majors · {candidate.activeChains.join(', ')} · last trade {formatTime(candidate.lastTradeUtc)}
                      </small>
                    </div>
                  </div>
                ))}
              </div>
              <div className="section-heading">
                <strong>Performance verification</strong>
                <span>Analyze discovered wallets before adding them to live tracking.</span>
              </div>
              <div className="scan-grid">
                <label>Start<input type="datetime-local" value={traderForm.startUtc} onChange={(e) => setTraderForm({ ...traderForm, startUtc: e.target.value })} /></label>
                <label>End<input type="datetime-local" value={traderForm.endUtc} onChange={(e) => setTraderForm({ ...traderForm, endUtc: e.target.value })} /></label>
                <label>Min portfolio USD<input type="number" min="0" step="10000" value={traderForm.minimumStartingValueUsd} onChange={(e) => setTraderForm({ ...traderForm, minimumStartingValueUsd: Number(e.target.value) })} /></label>
                <label>Top wallets<input type="number" min="1" max="100" value={traderForm.top} onChange={(e) => setTraderForm({ ...traderForm, top: Number(e.target.value) })} /></label>
              </div>
              <label className="wallet-seed-field">
                Candidate wallets
                <textarea
                  value={traderForm.candidateWallets}
                  onChange={(e) => setTraderForm({ ...traderForm, candidateWallets: e.target.value })}
                  placeholder="0x... addresses, one per line"
                />
              </label>
              <button className="primary-action" disabled={isTraderScanRunning} onClick={runTraderScan}>
                <Database size={16} /> {isTraderScanRunning ? 'Analyzing wallets...' : 'Find top traders'}
              </button>
              {activeTraderScan && (
                <div className={`discovery-progress ${activeTraderScan.state === 'FAILED' ? 'failed' : ''}`}>
                  <div className="progress-summary">
                    <strong>{activeTraderScan.currentStage.replaceAll('_', ' ')}</strong>
                    <span>{activeTraderScan.progressPercent}%</span>
                  </div>
                  <div
                    className="progress-track"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={activeTraderScan.progressPercent}
                  >
                    <span style={{ width: `${activeTraderScan.progressPercent}%` }} />
                  </div>
                  <p>{activeTraderScan.errorMessage || activeTraderScan.statusMessage}</p>
                  <small>
                    Evaluated {activeTraderScan.evaluatedWalletCount} · qualified {activeTraderScan.qualifiedWalletCount}
                  </small>
                  <div className="progress-log">
                    {[...(activeTraderScan.progressLog || [])].reverse().map((entry, index) => (
                      <div key={`${entry.timestampUtc}-${index}`} className={entry.stage === 'wallet_failed' || entry.state === 'FAILED' ? 'error' : ''}>
                        <time>{formatTime(entry.timestampUtc)}</time>
                        <span>{entry.stage.replaceAll('_', ' ')}</span>
                        <p>{entry.message}</p>
                      </div>
                    ))}
                  </div>
                </div>
              )}
              <div className="scan-list">
                {traderScans.map((scan) => (
                  <button key={scan.id} onClick={() => loadTraderCandidates(scan.id)}>
                    Scan #{scan.id} · {scan.progressPercent}% · {scan.state} · {scan.qualifiedWalletCount}/{scan.evaluatedWalletCount}
                  </button>
                ))}
              </div>
              <div className="candidate-list">
                {traderCandidates.length > 0 && (
                  <button className="secondary-action" onClick={() => trackTopTraders(traderCandidates[0].traderScanId)}>
                    <Plus size={16} /> Track top 10
                  </button>
                )}
                {traderCandidates.map((candidate) => (
                  <div className="candidate-row" key={candidate.id}>
                    <div>
                      <strong>{shortAddress(candidate.walletAddress)}</strong>
                      <span>
                        score {Number(candidate.score || 0).toFixed(1)} · profit {formatUsd(candidate.adjustedProfitUsd)} · return {Number(candidate.adjustedReturnPercent || 0).toFixed(2)}%
                      </span>
                      <small>
                        {formatUsd(candidate.startingValueUsd)} → {formatUsd(candidate.endingValueUsd)} · external in {formatUsd(candidate.receivedExternalUsd)} · out {formatUsd(candidate.sentExternalUsd)}
                      </small>
                    </div>
                    <button onClick={() => trackTraderCandidate(candidate)} aria-label="Track trader"><Plus size={16} /></button>
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
                  <div key={`${line.role}-${index}`} className={`chat-line ${line.role}`}>
                    <div>{line.text}</div>
                    {line.meta && (
                      <div className="chat-meta">
                        {line.meta.provider === 'groq' ? 'Groq' : 'Local'} · {line.meta.model} · {line.meta.mode} · {line.meta.elapsedMs}ms
                        <br />
                        Source: {line.meta.source} · {shortAddress(line.meta.sourceWallet)} · {line.meta.positions} positions
                      </div>
                    )}
                  </div>
                ))}
                {isChatThinking && (
                  <div className="chat-line ai thinking">
                    Groq llama-3.3-70b-versatile dusunuyor...
                  </div>
                )}
              </div>
              <div className="chat-input">
                <input value={chatQuestion} disabled={isChatThinking} onChange={(e) => setChatQuestion(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && sendChat()} placeholder="Piyasa biası ve son hareketler ne söylüyor?" />
                <button onClick={sendChat} disabled={isChatThinking} aria-label="Send chat"><Send size={17} /></button>
              </div>
            </div>
          )}
        </div>
      </aside>
    </main>
  )
}

export default App
