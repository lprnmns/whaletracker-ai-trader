"use strict";

const alertBox = document.getElementById("adminAlert");
const adminStatus = document.getElementById("adminStatus");
const adminHeartbeat = document.getElementById("adminHeartbeat");
const adminLogs = document.getElementById("adminLogs");
const logoutBtn = document.getElementById("logoutBtn");
const scanRows = document.getElementById("scanRows");
const candidateRows = document.getElementById("candidateRows");
const candidateScanTag = document.getElementById("candidateScanTag");
const trackedWalletRows = document.getElementById("trackedWalletRows");
const runHistoricalScanBtn = document.getElementById("runHistoricalScan");
const refreshScansBtn = document.getElementById("refreshScans");
const refreshWalletsBtn = document.getElementById("refreshWallets");
const addManualWalletBtn = document.getElementById("addManualWallet");
const refreshAiMemoryBtn = document.getElementById("refreshAiMemory");
const aiBiasDirection = document.getElementById("aiBiasDirection");
const aiBiasScore = document.getElementById("aiBiasScore");
const aiBiasSummary = document.getElementById("aiBiasSummary");
const aiMemoryRows = document.getElementById("aiMemoryRows");

async function fetchJson(url, options = {}) {
  const headers = options.body
    ? { "Content-Type": "application/json", ...(options.headers || {}) }
    : options.headers;

  const resp = await fetch(url, {
    credentials: "include",
    ...options,
    headers,
  });

  if (!resp.ok) {
    const text = await resp.text();
    throw new Error(text || `HTTP ${resp.status}`);
  }

  if (resp.status === 204) {
    return null;
  }

  return resp.json();
}

function showAlert(message, isError = true) {
  alertBox.textContent = message;
  alertBox.classList.toggle("alert-danger", isError);
  alertBox.classList.toggle("alert-success", !isError);
  alertBox.classList.remove("d-none");
}

function clearAlert() {
  alertBox.classList.add("d-none");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function shortAddress(address) {
  if (!address || address.length < 12) {
    return address || "--";
  }

  return `${address.slice(0, 6)}...${address.slice(-4)}`;
}

function formatDate(value) {
  return value ? new Date(value).toLocaleString() : "--";
}

function formatUsd(value) {
  const number = Number(value || 0);
  return number.toLocaleString(undefined, {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 2,
  });
}

function toIsoFromLocalInput(id) {
  const value = document.getElementById(id)?.value;
  if (!value) {
    return null;
  }

  return new Date(value).toISOString();
}

async function loadStatus() {
  try {
    const data = await fetchJson("/api/dashboard/status");
    clearAlert();
    const active = data.program?.active;
    adminStatus.textContent = active ? "Program Active" : "Program Passive";
    adminHeartbeat.textContent = data.program?.lastHeartbeat
      ? `Heartbeat: ${new Date(data.program.lastHeartbeat).toLocaleString()}`
      : "Heartbeat: --";
  } catch {
    alertBox.textContent = "Unable to load status. Check backend.";
    alertBox.classList.remove("d-none");
  }
}

async function loadLogs() {
  try {
    const data = await fetchJson("/api/dashboard/logs");
    const logs = Array.isArray(data.logs) ? data.logs : [];
    if (!logs.length) {
      adminLogs.innerHTML = `<tr><td colspan="3" class="text-muted">No logs.</td></tr>`;
      return;
    }
    adminLogs.innerHTML = logs
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
  } catch {
    adminLogs.innerHTML = `<tr><td colspan="3" class="text-muted">Login required.</td></tr>`;
  }
}

async function loadScans() {
  try {
    const scans = await fetchJson("/api/historical-scans?count=20");
    if (!Array.isArray(scans) || !scans.length) {
      scanRows.innerHTML = `<tr><td colspan="4" class="text-muted">No scans.</td></tr>`;
      return;
    }

    scanRows.innerHTML = scans
      .map((scan) => `
        <tr>
          <td>
            <button class="btn btn-outline-light btn-sm" data-scan-id="${scan.id}">#${scan.id}</button>
          </td>
          <td>
            <div class="mono">${formatDate(scan.preCrashStartUtc)}</div>
            <div class="subtle">${formatDate(scan.dipBuyEndUtc)}</div>
          </td>
          <td>${scan.scannedSwapCount ?? 0}</td>
          <td>${scan.candidateCount ?? 0}</td>
        </tr>`)
      .join("");
  } catch (err) {
    scanRows.innerHTML = `<tr><td colspan="4" class="text-muted">Unable to load scans.</td></tr>`;
  }
}

async function loadCandidates(scanId) {
  candidateScanTag.textContent = `Scan #${scanId}`;
  candidateRows.innerHTML = `<tr><td colspan="5" class="text-muted">Loading...</td></tr>`;

  try {
    const candidates = await fetchJson(`/api/historical-scans/${scanId}/candidates`);
    if (!Array.isArray(candidates) || !candidates.length) {
      candidateRows.innerHTML = `<tr><td colspan="5" class="text-muted">No candidates.</td></tr>`;
      return;
    }

    candidateRows.innerHTML = candidates
      .map((candidate) => `
        <tr>
          <td>
            <span class="mono" title="${escapeHtml(candidate.walletAddress)}">${shortAddress(candidate.walletAddress)}</span>
          </td>
          <td>${escapeHtml(candidate.assetSymbol || "--")}</td>
          <td>${formatUsd(candidate.estimatedProfitUsd)}</td>
          <td>${Number(candidate.insiderScore || 0).toFixed(1)}</td>
          <td class="text-end">
            <button class="btn btn-primary btn-sm" data-promote-id="${candidate.id}">Track</button>
          </td>
        </tr>`)
      .join("");
  } catch {
    candidateRows.innerHTML = `<tr><td colspan="5" class="text-muted">Unable to load candidates.</td></tr>`;
  }
}

async function loadTrackedWallets() {
  try {
    const wallets = await fetchJson("/api/tracked-wallets?includeInactive=true");
    if (!Array.isArray(wallets) || !wallets.length) {
      trackedWalletRows.innerHTML = `<tr><td colspan="6" class="text-muted">No tracked wallets.</td></tr>`;
      return;
    }

    trackedWalletRows.innerHTML = wallets
      .map((wallet) => `
        <tr class="${wallet.isActive ? "" : "wallet-inactive"}">
          <td>
            <div class="mono" title="${escapeHtml(wallet.walletAddress)}">${shortAddress(wallet.walletAddress)}</div>
            <div class="subtle">${escapeHtml(wallet.label || "--")}</div>
          </td>
          <td>${escapeHtml(wallet.source || "--")}</td>
          <td>${Number(wallet.confidenceScore || 0).toFixed(1)}</td>
          <td>${formatUsd(wallet.estimatedProfitUsd)}</td>
          <td>${formatDate(wallet.lastCheckedAt)}</td>
          <td class="text-end">
            <button class="btn btn-outline-light btn-sm" data-toggle-wallet="${wallet.id}" data-active="${wallet.isActive}">
              ${wallet.isActive ? "Pause" : "Resume"}
            </button>
          </td>
        </tr>`)
      .join("");
  } catch {
    trackedWalletRows.innerHTML = `<tr><td colspan="6" class="text-muted">Unable to load tracked wallets.</td></tr>`;
  }
}

async function loadAiMemory() {
  try {
    const [state, events] = await Promise.all([
      fetchJson("/api/ai-memory/state"),
      fetchJson("/api/ai-memory/events?count=30"),
    ]);

    aiBiasDirection.textContent = state.direction || "NEUTRAL";
    aiBiasScore.textContent = `Score: ${Number(state.biasScore || 0).toFixed(1)} / 100`;
    aiBiasSummary.textContent = state.summary || "No memory events recorded.";

    if (!Array.isArray(events) || !events.length) {
      aiMemoryRows.innerHTML = `<tr><td colspan="5" class="text-muted">No AI memory events.</td></tr>`;
      return;
    }

    aiMemoryRows.innerHTML = events
      .map((event) => `
        <tr>
          <td>${formatDate(event.createdAt)}</td>
          <td><span class="mono" title="${escapeHtml(event.walletAddress)}">${shortAddress(event.walletAddress)}</span></td>
          <td>
            <div>${escapeHtml(event.movementType || "--")} ${escapeHtml(event.symbol || "--")}</div>
            <div class="subtle">${formatUsd(event.movementUsd)}</div>
          </td>
          <td>
            <div>${escapeHtml(event.action || "--")}</div>
            <div class="subtle">${escapeHtml(event.ignoredReason || "")}</div>
          </td>
          <td>${Number(event.biasDelta || 0).toFixed(1)}</td>
        </tr>`)
      .join("");
  } catch {
    aiMemoryRows.innerHTML = `<tr><td colspan="5" class="text-muted">Unable to load AI memory.</td></tr>`;
  }
}

async function runHistoricalScan() {
  const request = {
    preCrashStartUtc: toIsoFromLocalInput("preCrashStart"),
    preCrashEndUtc: toIsoFromLocalInput("preCrashEnd"),
    dipBuyStartUtc: toIsoFromLocalInput("dipBuyStart"),
    dipBuyEndUtc: toIsoFromLocalInput("dipBuyEnd"),
    minimumProfitUsd: Number(document.getElementById("minimumProfit")?.value || 0),
  };

  if (!request.preCrashStartUtc || !request.preCrashEndUtc || !request.dipBuyStartUtc || !request.dipBuyEndUtc) {
    showAlert("All scan windows are required.");
    return;
  }

  runHistoricalScanBtn.disabled = true;
  runHistoricalScanBtn.textContent = "Scanning...";

  try {
    const result = await fetchJson("/api/historical-scans/uniswap-v3?persist=true", {
      method: "POST",
      body: JSON.stringify(request),
    });

    showAlert(`Scan #${result.scanId} finished: ${result.candidateCount} candidates from ${result.scannedSwapCount} swaps.`, false);
    await loadScans();
    if (result.scanId) {
      await loadCandidates(result.scanId);
    }
  } catch (err) {
    showAlert(`Scan failed: ${err.message}`);
  } finally {
    runHistoricalScanBtn.disabled = false;
    runHistoricalScanBtn.textContent = "Run Scan";
  }
}

async function addManualWallet() {
  const walletAddress = document.getElementById("manualWalletAddress")?.value?.trim();
  if (!walletAddress) {
    showAlert("Wallet address is required.");
    return;
  }

  try {
    await fetchJson("/api/tracked-wallets", {
      method: "POST",
      body: JSON.stringify({
        walletAddress,
        label: document.getElementById("manualWalletLabel")?.value || "",
        confidenceScore: Number(document.getElementById("manualWalletConfidence")?.value || 0),
        source: "manual",
        isActive: true,
      }),
    });

    showAlert("Wallet added to tracking list.", false);
    await loadTrackedWallets();
  } catch (err) {
    showAlert(`Wallet add failed: ${err.message}`);
  }
}

logoutBtn?.addEventListener("click", async () => {
  await fetch("/api/auth/logout", { method: "POST", credentials: "include" });
  window.location.href = "/login.html";
});

scanRows?.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-scan-id]");
  if (!button) {
    return;
  }

  await loadCandidates(button.dataset.scanId);
});

candidateRows?.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-promote-id]");
  if (!button) {
    return;
  }

  button.disabled = true;
  try {
    await fetchJson(`/api/tracked-wallets/from-candidate/${button.dataset.promoteId}`, {
      method: "POST",
      body: JSON.stringify({ isActive: true }),
    });
    showAlert("Candidate added to tracking list.", false);
    await loadTrackedWallets();
  } catch (err) {
    showAlert(`Track failed: ${err.message}`);
  } finally {
    button.disabled = false;
  }
});

trackedWalletRows?.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-toggle-wallet]");
  if (!button) {
    return;
  }

  const isActive = button.dataset.active === "true";
  button.disabled = true;

  try {
    await fetchJson(`/api/tracked-wallets/${button.dataset.toggleWallet}`, {
      method: "PATCH",
      body: JSON.stringify({ isActive: !isActive }),
    });
    await loadTrackedWallets();
  } catch (err) {
    showAlert(`Wallet update failed: ${err.message}`);
  } finally {
    button.disabled = false;
  }
});

runHistoricalScanBtn?.addEventListener("click", runHistoricalScan);
refreshScansBtn?.addEventListener("click", loadScans);
refreshWalletsBtn?.addEventListener("click", loadTrackedWallets);
addManualWalletBtn?.addEventListener("click", addManualWallet);
refreshAiMemoryBtn?.addEventListener("click", loadAiMemory);

document.addEventListener("DOMContentLoaded", () => {
  loadStatus();
  loadLogs();
  loadScans();
  loadTrackedWallets();
  loadAiMemory();
  setInterval(loadStatus, 15000);
  setInterval(loadLogs, 20000);
  setInterval(loadTrackedWallets, 30000);
  setInterval(loadAiMemory, 30000);
});
