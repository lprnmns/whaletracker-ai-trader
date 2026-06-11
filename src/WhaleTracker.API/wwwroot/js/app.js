"use strict";

const state = {
  chart: null,
  range: "1d",
  startedAt: null,
  startOkxBalance: null,
  initialUsd: 250
};

const elements = {
  programStatus: document.getElementById("programStatus"),
  dataAlert: document.getElementById("dataAlert"),
  startTime: document.getElementById("startTime"),
  lastHeartbeat: document.getElementById("lastHeartbeat"),
  okxBalance: document.getElementById("okxBalance"),
  okxPnl: document.getElementById("okxPnl"),
  whaleBalance: document.getElementById("whaleBalance"),
  lastEventShort: document.getElementById("lastEventShort"),
  okxPositions: document.getElementById("okxPositions"),
  whalePositions: document.getElementById("whalePositions"),
  startOkxBalance: document.getElementById("startOkxBalance"),
  okxTableBody: document.getElementById("okxTableBody"),
  whaleTableBody: document.getElementById("whaleTableBody"),
  logTableBody: document.getElementById("logTableBody"),
  benchmarkMessage: document.getElementById("benchmarkMessage"),
  chatForm: document.getElementById("chatForm"),
  chatInput: document.getElementById("chatInput"),
  chatMessages: document.getElementById("chatMessages"),
  chatSendBtn: document.getElementById("chatSendBtn")
};

function getAddressParam() {
  const params = new URLSearchParams(window.location.search);
  const queryAddress = params.get("address");
  const dataAddress = document.body.getAttribute("data-default-address");
  return queryAddress || dataAddress || "";
}

function buildUrl(path) {
  const address = getAddressParam();
  if (!address) {
    return path;
  }
  const separator = path.includes("?") ? "&" : "?";
  return `${path}${separator}address=${encodeURIComponent(address)}`;
}

function getMarketUrl() {
  const attr = document.body.getAttribute("data-market-url");
  if (attr) {
    return attr;
  }
  const host = window.location.hostname;
  if (host === "localhost" || host === "127.0.0.1") {
    return "http://localhost:8001/api/market-comparison";
  }
  return "/api/market-comparison";
}

async function fetchJson(url, options = {}) {
  const resp = await fetch(url, { credentials: "include", ...options });
  if (!resp.ok) {
    throw new Error(`HTTP ${resp.status}`);
  }
  return resp.json();
}

function formatUsd(value) {
  if (value === null || value === undefined) return "--";
  return `$${Number(value).toFixed(2)}`;
}

function formatPct(value) {
  if (value === null || value === undefined) return "--";
  const sign = Number(value) >= 0 ? "+" : "";
  return `${sign}${Number(value).toFixed(2)}%`;
}

function setStatus(active) {
  if (!elements.programStatus) return;
  elements.programStatus.textContent = active ? "Program: Active" : "Program: Passive";
  elements.programStatus.classList.toggle("inactive", !active);
}

function updateStatus(data) {
  const program = data.program || {};
  const okx = data.okx || {};
  const whale = data.whale || {};

  state.startedAt = program.startedAt || null;
  state.startOkxBalance = program.startOkxBalance ?? null;
  if (state.startOkxBalance != null) {
    state.initialUsd = Number(state.startOkxBalance);
  }

  setStatus(program.active);
  elements.startTime.textContent = program.startedAt ? new Date(program.startedAt).toLocaleString() : "--";
  elements.startOkxBalance.textContent = program.startOkxBalance != null
    ? `Start OKX: ${formatUsd(program.startOkxBalance)}`
    : "Start OKX: --";
  elements.lastHeartbeat.textContent = program.lastHeartbeat
    ? `Heartbeat: ${new Date(program.lastHeartbeat).toLocaleTimeString()}`
    : "Heartbeat: --";

  elements.okxBalance.textContent = formatUsd(okx.balance);
  const okxPnlText = okx.pnlUsd != null ? `${formatUsd(okx.pnlUsd)} (${formatPct(okx.pnlPct)})` : "--";
  elements.okxPnl.textContent = `PnL: ${okxPnlText}`;

  elements.whaleBalance.textContent = formatUsd(whale.balance);
  elements.lastEventShort.textContent = whale.lastEvent
    ? `Last Event: ${whale.lastEvent.split("\n")[0]}`
    : "Last Event: --";

  elements.okxPositions.textContent = `OKX: ${okx.openPositions ?? "--"}`;
  elements.whalePositions.textContent = `Whale: ${whale.positions ?? "--"}`;

  if (program.active === false) {
    elements.dataAlert.textContent = "Program passive or data stale.";
    elements.dataAlert.classList.remove("d-none");
  } else {
    elements.dataAlert.classList.add("d-none");
  }
}

function renderPositions(data) {
  const okxRows = Array.isArray(data.okxPositions) ? data.okxPositions : [];
  const whaleRows = Array.isArray(data.whalePositions) ? data.whalePositions : [];

  elements.okxTableBody.innerHTML = okxRows.length
    ? okxRows.map(renderOkxRow).join("")
    : `<tr><td colspan="4" class="text-muted">No data</td></tr>`;

  elements.whaleTableBody.innerHTML = whaleRows.length
    ? whaleRows.map(renderWhaleRow).join("")
    : `<tr><td colspan="4" class="text-muted">No data</td></tr>`;
}

function renderOkxRow(row) {
  const pnlClass = row.pnlUsd >= 0 ? "pnl-positive" : "pnl-negative";
  const pnlText = `${formatUsd(row.pnlUsd)} (${formatPct(row.pnlPct)})`;
  return `
    <tr>
      <td>${row.symbol}</td>
      <td>${row.side}</td>
      <td>${formatUsd(row.marginUsd)}</td>
      <td class="${pnlClass}">${pnlText}</td>
    </tr>`;
}

function renderWhaleRow(row) {
  const pnlClass = row.pnlUsd >= 0 ? "pnl-positive" : "pnl-negative";
  const pnlText = row.pnlUsd != null ? `${formatUsd(row.pnlUsd)} (${formatPct(row.pnlPct)})` : "--";
  return `
    <tr>
      <td>${row.symbol}</td>
      <td>${Number(row.amount ?? 0).toFixed(4)}</td>
      <td>${formatUsd(row.valueUsd)}</td>
      <td class="${pnlClass}">${pnlText}</td>
    </tr>`;
}

function renderLogs(data) {
  if (!elements.logTableBody) return;
  const logs = Array.isArray(data.logs) ? data.logs : [];
  if (!logs.length) {
    elements.logTableBody.innerHTML = `<tr><td colspan="3" class="text-muted">No logs.</td></tr>`;
    return;
  }
  elements.logTableBody.innerHTML = logs
    .map((entry) => {
      const time = entry.timestamp ? new Date(entry.timestamp).toLocaleString() : "--";
      return `
        <tr>
          <td>${time}</td>
          <td>${entry.level || "--"}</td>
          <td>${entry.message || "--"}</td>
        </tr>`;
    })
    .join("");
}

function setBenchmarkMessage(message) {
  if (!elements.benchmarkMessage) return;
  if (!message) {
    elements.benchmarkMessage.classList.add("d-none");
    elements.benchmarkMessage.textContent = "";
    return;
  }
  elements.benchmarkMessage.textContent = message;
  elements.benchmarkMessage.classList.remove("d-none");
}

function clearChart() {
  if (!state.chart) return;
  state.chart.data.labels = [];
  state.chart.data.datasets.forEach((dataset) => {
    dataset.data = [];
  });
  state.chart.update();
}

function appendChatMessage(role, text) {
  if (!elements.chatMessages) return;
  const wrapper = document.createElement("div");
  wrapper.className = `chat-message ${role === "user" ? "chat-message-user" : "chat-message-ai"}`;
  wrapper.textContent = text;
  elements.chatMessages.appendChild(wrapper);
  elements.chatMessages.scrollTop = elements.chatMessages.scrollHeight;
}

function setChatBusy(busy) {
  if (elements.chatSendBtn) {
    elements.chatSendBtn.disabled = busy;
  }
  if (elements.chatInput) {
    elements.chatInput.disabled = busy;
  }
}

async function sendChat(question) {
  if (!question) return;
  appendChatMessage("user", question);
  setChatBusy(true);
  try {
    const resp = await fetchJson(buildUrl("/api/dashboard/chat"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question })
    });
    const answer = resp.answer || "No response.";
    appendChatMessage("ai", answer.trim());
  } catch (err) {
    appendChatMessage("ai", "AI service unavailable.");
  } finally {
    setChatBusy(false);
  }
}

function bindChat() {
  if (!elements.chatForm || !elements.chatInput) return;
  elements.chatForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const question = elements.chatInput.value.trim();
    if (!question) return;
    elements.chatInput.value = "";
    sendChat(question);
  });
}

function initChart() {
  const ctx = document.getElementById("benchmarkChart");
  if (!ctx) return;
  state.chart = new Chart(ctx, {
    type: "line",
    data: {
      labels: [],
      datasets: [
        { label: "Bot PnL %", data: [], borderColor: "#22c55e", tension: 0.3 },
        { label: "BIST 100 %", data: [], borderColor: "#6366f1", tension: 0.3 },
        { label: "Gold %", data: [], borderColor: "#facc15", tension: 0.3 },
        { label: "TL Deposit %", data: [], borderColor: "#94a3b8", tension: 0.3 }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: {
          labels: { color: "#cbd5f5" }
        }
      },
      scales: {
        x: { ticks: { color: "#94a3b8" }, grid: { color: "rgba(255,255,255,0.05)" } },
        y: { ticks: { color: "#94a3b8" }, grid: { color: "rgba(255,255,255,0.05)" } }
      }
    }
  });
}

function updateChart(data) {
  if (!state.chart) return;
  const series = data.series || {};
  const toMap = (points) => {
    const map = new Map();
    (points || []).forEach((point) => {
      if (!point || !point.date) return;
      const ts = Date.parse(point.date);
      if (!Number.isNaN(ts)) {
        map.set(ts, point.value);
      }
    });
    return map;
  };

  const botMap = toMap(series.bot);
  const bistMap = toMap(series.bist100);
  const goldMap = toMap(series.gold);
  const depositMap = toMap(series.deposit);
  const timestamps = Array.from(
    new Set([...botMap.keys(), ...bistMap.keys(), ...goldMap.keys(), ...depositMap.keys()])
  ).sort((a, b) => a - b);

  const labels = timestamps.map((ts) => {
    const date = new Date(ts);
    if (state.range === "15m") {
      return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    }
    return date.toLocaleDateString();
  });
  const buildSeries = (map) => timestamps.map((ts) => (map.has(ts) ? map.get(ts) : null));

  state.chart.data.labels = labels;
  state.chart.data.datasets[0].data = buildSeries(botMap);
  state.chart.data.datasets[1].data = buildSeries(bistMap);
  state.chart.data.datasets[2].data = buildSeries(goldMap);
  state.chart.data.datasets[3].data = buildSeries(depositMap);
  state.chart.update();
}

async function refreshStatus() {
  try {
    const data = await fetchJson(buildUrl("/api/dashboard/status"));
    updateStatus(data);
  } catch {
    elements.dataAlert.classList.remove("d-none");
    setStatus(false);
  }
}

async function refreshPositions() {
  try {
    const data = await fetchJson(buildUrl("/api/dashboard/positions"));
    renderPositions(data);
  } catch {
    elements.okxTableBody.innerHTML = `<tr><td colspan="4" class="text-muted">No data</td></tr>`;
    elements.whaleTableBody.innerHTML = `<tr><td colspan="4" class="text-muted">No data</td></tr>`;
  }
}

async function refreshLogs() {
  try {
    const data = await fetchJson(buildUrl("/api/dashboard/logs"));
    renderLogs(data);
  } catch {
    elements.logTableBody.innerHTML = `<tr><td colspan="3" class="text-muted">Login required to view logs.</td></tr>`;
  }
}

async function refreshBenchmarks() {
  if (!state.startedAt) {
    setBenchmarkMessage("Start time not available yet.");
    clearChart();
    return;
  }

  const url = new URL(getMarketUrl());
  url.searchParams.set("bot_start_date", state.startedAt);
  url.searchParams.set("range", state.range);
  if (Number.isFinite(state.initialUsd)) {
    url.searchParams.set("initial_usd", state.initialUsd.toFixed(2));
  }

  try {
    const data = await fetchJson(url.toString(), { credentials: "omit" });
    if (data.status && data.status !== "ok") {
      setBenchmarkMessage(data.message || "No data yet for selected range.");
      clearChart();
      return;
    }
    if (Array.isArray(data.warnings) && data.warnings.length) {
      setBenchmarkMessage(data.warnings.join(" "));
    } else {
      setBenchmarkMessage("");
    }
    updateChart(data);
  } catch {
    setBenchmarkMessage("Market comparison service unavailable.");
    clearChart();
  }
}

function bindRangeButtons() {
  document.querySelectorAll("[data-range]").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll("[data-range]").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      state.range = btn.getAttribute("data-range") || "1d";
      refreshBenchmarks();
    });
  });
}

async function start() {
  initChart();
  bindRangeButtons();
  bindChat();
  await refreshStatus();
  refreshPositions();
  refreshLogs();
  refreshBenchmarks();

  setInterval(refreshStatus, 10000);
  setInterval(refreshPositions, 15000);
  setInterval(refreshLogs, 15000);
  setInterval(refreshBenchmarks, 300000);
}

document.addEventListener("DOMContentLoaded", start);
