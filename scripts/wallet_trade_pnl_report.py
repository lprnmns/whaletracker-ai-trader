#!/usr/bin/env python3
"""
Standalone Dune-based trade/PnL report for candidate wallets.

It pulls recent DEX trades for selected wallets, converts them into buy/sell
legs for copyable major assets, and calculates realized PnL with FIFO lots.
The output is intentionally CSV-first so it can be inspected like exchange
history without running the WhaleTracker API.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation, getcontext
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

getcontext().prec = 38

DEFAULT_WALLETS = [
    "0x16c60d3e294f609bd9aeb10f3ef4b5c901f4bd23",
    "0xc00fc2ec5099db4850e4035d1537f757d1744485",
    "0x6f5a30d1c6f73b2ef8caad35e9a73732d100f750",
    "0xcd5adcb3a1f63180f8d4f75d8f12de8883f7c641",
    "0xabb8e869913e759dcbd532494beeb0252a5e99af",
    "0xf14aa9ed0b6d90b7a49b782f6a9846bea8f0b333",
]

MAJOR_ASSETS = {"BTC", "WBTC", "CBBTC", "ETH", "WETH", "SOL", "LINK", "AVAX"}
STABLE_ASSETS = {"USDT", "USDC", "DAI", "USDE"}
COPYABLE_ASSETS = MAJOR_ASSETS | STABLE_ASSETS


@dataclass
class Lot:
    qty: Decimal
    cost_usd: Decimal
    acquired_at: datetime
    tx_hash: str


def load_env(path: Path) -> None:
    if not path.exists():
        return
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            raw = line.strip()
            if not raw or raw.startswith("#") or "=" not in raw:
                continue
            key, value = raw.split("=", 1)
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            if key and key not in os.environ:
                os.environ[key] = value


def dec(value: Any) -> Decimal:
    if value is None or value == "":
        return Decimal("0")
    try:
        return Decimal(str(value))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def dt(value: str) -> datetime:
    value = value.strip()
    if value.endswith(" UTC"):
        value = value[:-4] + "+00:00"
    if value.endswith("Z"):
        value = value[:-1] + "+00:00"
    parsed = datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def number(value: Decimal) -> str:
    return f"{value.normalize():f}"


def normalize_wallet(value: str) -> str:
    wallet = value.strip().lower()
    if not wallet.startswith("0x") or len(wallet) != 42:
        raise ValueError(f"Invalid EVM wallet address: {value}")
    int(wallet[2:], 16)
    return wallet


def dune_request(method: str, path: str, api_key: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    request = Request(
        f"https://api.dune.com/api/v1/{path.lstrip('/')}",
        data=body,
        method=method,
        headers={
            "X-Dune-Api-Key": api_key,
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )
    try:
        with urlopen(request, timeout=60) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Dune HTTP {exc.code}: {detail}") from exc
    except URLError as exc:
        raise RuntimeError(f"Dune network error: {exc}") from exc


def build_sql(wallets: list[str], days: int, min_trade_usd: Decimal) -> str:
    wallet_literals = ", ".join(wallets)
    min_trade = str(min_trade_usd)
    return f"""
WITH selected_wallets(wallet) AS (
    VALUES {", ".join(f"({wallet})" for wallet in wallet_literals.split(", "))}
),
raw AS (
    SELECT
        t.block_time,
        t.blockchain,
        concat('0x', lower(to_hex(t.tx_hash))) AS tx_hash,
        concat('0x', lower(to_hex(t.tx_from))) AS wallet,
        upper(coalesce(t.token_bought_symbol, '')) AS bought_symbol,
        upper(coalesce(t.token_sold_symbol, '')) AS sold_symbol,
        t.token_bought_amount AS bought_amount,
        t.token_sold_amount AS sold_amount,
        t.amount_usd
    FROM dex.trades t
    JOIN selected_wallets w ON w.wallet = t.tx_from
    WHERE t.block_date >= current_date - interval '{days}' day
      AND t.block_time >= now() - interval '{days}' day
      AND t.blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
      AND t.amount_usd >= {min_trade}
      AND upper(coalesce(t.token_bought_symbol, '')) IN (
          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
          'USDT', 'USDC', 'DAI', 'USDE'
      )
      AND upper(coalesce(t.token_sold_symbol, '')) IN (
          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
          'USDT', 'USDC', 'DAI', 'USDE'
      )
),
dedup AS (
    SELECT
        block_time,
        blockchain,
        tx_hash,
        wallet,
        bought_symbol,
        sold_symbol,
        bought_amount,
        sold_amount,
        amount_usd,
        row_number() OVER (
            PARTITION BY blockchain, tx_hash, wallet, bought_symbol, sold_symbol
            ORDER BY amount_usd DESC
        ) AS rn
    FROM raw
)
SELECT
    block_time,
    blockchain,
    tx_hash,
    wallet,
    bought_symbol,
    sold_symbol,
    bought_amount,
    sold_amount,
    amount_usd
FROM dedup
WHERE rn = 1
ORDER BY block_time ASC
"""


def build_discovery_sql(
    days: int,
    min_trade_usd: Decimal,
    min_active_weeks: int,
    min_swaps: int,
    candidate_limit: int,
    min_balance_usd: Decimal,
    max_balance_usd: Decimal,
) -> str:
    min_trade = str(min_trade_usd)
    min_balance = str(min_balance_usd)
    max_balance = str(max_balance_usd)
    lookback_weeks = max(Decimal("1"), Decimal(days) / Decimal("7"))
    return f"""
WITH raw AS (
    SELECT
        t.blockchain,
        t.tx_hash,
        t.tx_from AS wallet,
        t.block_time,
        try_cast(t.amount_usd AS double) AS amount_usd,
        upper(coalesce(t.token_bought_symbol, '')) AS bought_symbol,
        upper(coalesce(t.token_sold_symbol, '')) AS sold_symbol
    FROM dex.trades t
    WHERE t.block_date >= current_date - interval '{days}' day
      AND t.block_time >= now() - interval '{days}' day
      AND t.blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
      AND t.tx_from IS NOT NULL
      AND try_cast(t.amount_usd AS double) BETWEEN {min_trade} AND 5000000
      AND upper(coalesce(t.token_bought_symbol, '')) IN ('BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX', 'USDT', 'USDC', 'DAI', 'USDE')
      AND upper(coalesce(t.token_sold_symbol, '')) IN ('BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX', 'USDT', 'USDC', 'DAI', 'USDE')
      AND (
          upper(coalesce(t.token_bought_symbol, '')) NOT IN ('USDT', 'USDC', 'DAI', 'USDE')
          OR upper(coalesce(t.token_sold_symbol, '')) NOT IN ('USDT', 'USDC', 'DAI', 'USDE')
      )
),
sandwich_wallets AS (
    SELECT DISTINCT blockchain, tx_from AS wallet
    FROM dex.sandwiches
    WHERE block_month >= date_trunc('month', current_date - interval '{days}' day)
      AND block_time >= now() - interval '{days}' day
      AND blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
      AND tx_from IS NOT NULL
),
eligible_swap_legs AS (
    SELECT s.*
    FROM raw s
    WHERE NOT EXISTS (
          SELECT 1
          FROM labels.addresses l
          WHERE l.blockchain = s.blockchain
            AND l.address = s.wallet
            AND lower(l.category) IN ('cex', 'bridge', 'mev', 'bot', 'contract', 'token_contract', 'oracle')
      )
      AND NOT EXISTS (
          SELECT 1
          FROM sandwich_wallets mev
          WHERE mev.blockchain = s.blockchain
            AND mev.wallet = s.wallet
      )
),
transaction_swaps AS (
    SELECT
        blockchain,
        tx_hash,
        wallet,
        min(block_time) AS block_time,
        max(amount_usd) AS amount_usd
    FROM eligible_swap_legs
    GROUP BY blockchain, tx_hash, wallet
),
wallet_stats AS (
    SELECT
        wallet,
        count(*) AS meaningful_swap_count,
        count(DISTINCT date_trunc('week', block_time)) AS active_week_count,
        sum(amount_usd) AS approved_notional_usd,
        avg(amount_usd) AS average_swap_usd,
        max(daily_swaps) AS maximum_daily_swaps,
        count(DISTINCT blockchain) AS active_chain_count,
        array_agg(DISTINCT blockchain) AS active_chains,
        min(block_time) AS first_trade_utc,
        max(block_time) AS last_trade_utc
    FROM (
        SELECT
            t.*,
            count(*) OVER (PARTITION BY wallet, date(block_time)) AS daily_swaps
        FROM transaction_swaps t
    )
    GROUP BY wallet
    HAVING count(*) >= {min_swaps}
       AND count(DISTINCT date_trunc('week', block_time)) >= {min_active_weeks}
       AND count(*) <= {days * 12}
       AND max(daily_swaps) <= 50
)
SELECT
    concat('0x', lower(to_hex(wallet))) AS wallet_address,
    meaningful_swap_count,
    active_week_count,
    approved_notional_usd,
    average_swap_usd,
    maximum_daily_swaps,
    cast(null AS bigint) AS distinct_major_assets,
    cast(null AS double) AS copyability_score,
    cast(null AS double) AS current_copyable_value_usd,
    active_chain_count,
    active_chains,
    first_trade_utc,
    last_trade_utc
FROM wallet_stats
ORDER BY active_week_count DESC, meaningful_swap_count DESC, approved_notional_usd DESC
LIMIT {candidate_limit}
"""


def execute_dune_sql(api_key: str, sql: str, timeout_seconds: int, poll_seconds: int) -> list[dict[str, Any]]:
    submitted = dune_request("POST", "sql/execute", api_key, {"sql": sql})
    execution_id = submitted.get("execution_id")
    if not execution_id:
        raise RuntimeError(f"Dune did not return execution_id: {submitted}")
    print(f"Dune execution: {execution_id}", flush=True)

    deadline = time.time() + timeout_seconds
    last_state = ""
    while time.time() < deadline:
        result = dune_request("GET", f"execution/{execution_id}/results?limit=100000", api_key)
        state = str(result.get("state", ""))
        if state != last_state:
            print(f"Dune state: {state}", flush=True)
            last_state = state
        if state == "QUERY_STATE_COMPLETED":
            rows = result.get("result", {}).get("rows", [])
            if not isinstance(rows, list):
                raise RuntimeError(f"Unexpected Dune rows payload: {result}")
            return rows
        if state in {"QUERY_STATE_FAILED", "QUERY_STATE_CANCELLED", "QUERY_STATE_EXPIRED"}:
            raise RuntimeError(f"Dune execution ended as {state}: {result.get('error')}")
        time.sleep(poll_seconds)

    raise TimeoutError(f"Dune execution {execution_id} did not finish in {timeout_seconds}s")


def trade_legs(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    legs: list[dict[str, Any]] = []
    for row in rows:
        bought = str(row.get("bought_symbol") or "").upper()
        sold = str(row.get("sold_symbol") or "").upper()
        amount_usd = dec(row.get("amount_usd"))
        base = {
            "time": dt(str(row["block_time"])),
            "wallet": str(row["wallet"]).lower(),
            "chain": row.get("blockchain", ""),
            "tx_hash": row.get("tx_hash", ""),
            "pair": f"{sold}->{bought}",
        }
        if bought in MAJOR_ASSETS and dec(row.get("bought_amount")) > 0:
            legs.append(
                {
                    **base,
                    "side": "BUY",
                    "asset": bought,
                    "qty": dec(row.get("bought_amount")),
                    "usd": amount_usd,
                }
            )
        if sold in MAJOR_ASSETS and dec(row.get("sold_amount")) > 0:
            legs.append(
                {
                    **base,
                    "side": "SELL",
                    "asset": sold,
                    "qty": dec(row.get("sold_amount")),
                    "usd": amount_usd,
                }
            )
    legs.sort(key=lambda item: (item["time"], item["wallet"], item["tx_hash"], item["side"]))
    return legs


def calculate_fifo(
    legs: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    lots: dict[tuple[str, str], list[Lot]] = {}
    closed: list[dict[str, Any]] = []
    trade_rows: list[dict[str, Any]] = []

    for leg in legs:
        wallet = leg["wallet"]
        asset = leg["asset"]
        side = leg["side"]
        qty = leg["qty"]
        usd = leg["usd"]
        price = usd / qty if qty > 0 else Decimal("0")
        key = (wallet, asset)

        trade_rows.append(
            {
                "time": leg["time"].isoformat(),
                "wallet": wallet,
                "asset": asset,
                "side": side,
                "qty": number(qty),
                "usd": money(usd),
                "avg_price": money(price),
                "pair": leg["pair"],
                "chain": leg["chain"],
                "tx_hash": leg["tx_hash"],
            }
        )

        if side == "BUY":
            lots.setdefault(key, []).append(Lot(qty=qty, cost_usd=usd, acquired_at=leg["time"], tx_hash=leg["tx_hash"]))
            continue

        remaining = qty
        proceeds_remaining = usd
        consumed_qty = Decimal("0")
        cost_basis = Decimal("0")
        weighted_hold_days = Decimal("0")
        source_lots: list[str] = []

        wallet_lots = lots.setdefault(key, [])
        while remaining > 0 and wallet_lots:
            lot = wallet_lots[0]
            take = min(remaining, lot.qty)
            take_ratio = take / qty if qty > 0 else Decimal("0")
            lot_ratio = take / lot.qty if lot.qty > 0 else Decimal("0")
            lot_cost = lot.cost_usd * lot_ratio
            hold_days = Decimal(str((leg["time"] - lot.acquired_at).total_seconds() / 86400))

            consumed_qty += take
            cost_basis += lot_cost
            weighted_hold_days += hold_days * take
            source_lots.append(lot.tx_hash)

            lot.qty -= take
            lot.cost_usd -= lot_cost
            remaining -= take
            proceeds_remaining -= usd * take_ratio
            if lot.qty <= Decimal("0.000000000000000001"):
                wallet_lots.pop(0)

        if consumed_qty <= 0:
            closed.append(
                {
                    "time": leg["time"].isoformat(),
                    "wallet": wallet,
                    "asset": asset,
                    "qty_closed": "0",
                    "sell_qty_without_basis": number(qty),
                    "proceeds_usd": money(usd),
                    "cost_basis_usd": "",
                    "realized_pnl_usd": "",
                    "realized_pnl_pct": "",
                    "holding_days": "",
                    "sell_price": money(price),
                    "tx_hash": leg["tx_hash"],
                    "basis_lot_txs": "",
                    "note": "SELL_WITHOUT_PRIOR_BUY_IN_WINDOW",
                }
            )
            continue

        proceeds = usd - proceeds_remaining
        pnl = proceeds - cost_basis
        pnl_pct = (pnl / cost_basis * Decimal("100")) if cost_basis > 0 else Decimal("0")
        holding_days = weighted_hold_days / consumed_qty if consumed_qty > 0 else Decimal("0")
        closed.append(
            {
                "time": leg["time"].isoformat(),
                "wallet": wallet,
                "asset": asset,
                "qty_closed": number(consumed_qty),
                "sell_qty_without_basis": number(remaining),
                "proceeds_usd": money(proceeds),
                "cost_basis_usd": money(cost_basis),
                "realized_pnl_usd": money(pnl),
                "realized_pnl_pct": money(pnl_pct),
                "holding_days": money(holding_days),
                "sell_price": money(price),
                "tx_hash": leg["tx_hash"],
                "basis_lot_txs": "|".join(source_lots),
                "note": "PARTIAL_NO_BASIS" if remaining > 0 else "",
            }
        )

    summary: dict[tuple[str, str], dict[str, Decimal | str | int]] = {}
    for row in trade_rows:
        key = (row["wallet"], row["asset"])
        item = summary.setdefault(
            key,
            {
                "wallet": row["wallet"],
                "asset": row["asset"],
                "buy_usd": Decimal("0"),
                "sell_usd": Decimal("0"),
                "buy_count": 0,
                "sell_count": 0,
                "realized_pnl_usd": Decimal("0"),
                "closed_count": 0,
            },
        )
        if row["side"] == "BUY":
            item["buy_usd"] = item["buy_usd"] + dec(row["usd"])  # type: ignore[operator]
            item["buy_count"] = int(item["buy_count"]) + 1
        else:
            item["sell_usd"] = item["sell_usd"] + dec(row["usd"])  # type: ignore[operator]
            item["sell_count"] = int(item["sell_count"]) + 1

    for row in closed:
        if row["realized_pnl_usd"] == "":
            continue
        key = (row["wallet"], row["asset"])
        item = summary.setdefault(
            key,
            {
                "wallet": row["wallet"],
                "asset": row["asset"],
                "buy_usd": Decimal("0"),
                "sell_usd": Decimal("0"),
                "buy_count": 0,
                "sell_count": 0,
                "realized_pnl_usd": Decimal("0"),
                "closed_count": 0,
            },
        )
        item["realized_pnl_usd"] = item["realized_pnl_usd"] + dec(row["realized_pnl_usd"])  # type: ignore[operator]
        item["closed_count"] = int(item["closed_count"]) + 1

    for (wallet, asset), wallet_lots in lots.items():
        item = summary.setdefault(
            (wallet, asset),
            {
                "wallet": wallet,
                "asset": asset,
                "buy_usd": Decimal("0"),
                "sell_usd": Decimal("0"),
                "buy_count": 0,
                "sell_count": 0,
                "realized_pnl_usd": Decimal("0"),
                "closed_count": 0,
            },
        )
        item["open_qty"] = sum((lot.qty for lot in wallet_lots), Decimal("0"))
        item["open_cost_usd"] = sum((lot.cost_usd for lot in wallet_lots), Decimal("0"))

    summary_rows: list[dict[str, str]] = []
    for item in summary.values():
        buy_usd = dec(item.get("buy_usd"))
        sell_usd = dec(item.get("sell_usd"))
        pnl = dec(item.get("realized_pnl_usd"))
        summary_rows.append(
            {
                "wallet": str(item["wallet"]),
                "asset": str(item["asset"]),
                "buy_count": str(item.get("buy_count", 0)),
                "sell_count": str(item.get("sell_count", 0)),
                "closed_count": str(item.get("closed_count", 0)),
                "buy_usd": money(buy_usd),
                "sell_usd": money(sell_usd),
                "realized_pnl_usd": money(pnl),
                "realized_pnl_pct_on_buys": money((pnl / buy_usd * Decimal("100")) if buy_usd > 0 else Decimal("0")),
                "open_qty": number(dec(item.get("open_qty", "0"))),
                "open_cost_usd": money(dec(item.get("open_cost_usd", "0"))),
            }
        )

    summary_rows.sort(key=lambda row: (row["wallet"], row["asset"]))

    wallet_totals: dict[str, dict[str, Decimal | int | str]] = {}
    for row in summary_rows:
        wallet = row["wallet"]
        item = wallet_totals.setdefault(
            wallet,
            {
                "wallet": wallet,
                "buy_usd": Decimal("0"),
                "sell_usd": Decimal("0"),
                "realized_pnl_usd": Decimal("0"),
                "open_cost_usd": Decimal("0"),
                "buy_count": 0,
                "sell_count": 0,
                "closed_count": 0,
            },
        )
        item["buy_usd"] = item["buy_usd"] + dec(row["buy_usd"])  # type: ignore[operator]
        item["sell_usd"] = item["sell_usd"] + dec(row["sell_usd"])  # type: ignore[operator]
        item["realized_pnl_usd"] = item["realized_pnl_usd"] + dec(row["realized_pnl_usd"])  # type: ignore[operator]
        item["open_cost_usd"] = item["open_cost_usd"] + dec(row["open_cost_usd"])  # type: ignore[operator]
        item["buy_count"] = int(item["buy_count"]) + int(row["buy_count"])
        item["sell_count"] = int(item["sell_count"]) + int(row["sell_count"])
        item["closed_count"] = int(item["closed_count"]) + int(row["closed_count"])

    no_basis_usd: dict[str, Decimal] = {}
    for row in closed:
        if row["note"] in {"SELL_WITHOUT_PRIOR_BUY_IN_WINDOW", "PARTIAL_NO_BASIS"}:
            no_basis_usd[row["wallet"]] = no_basis_usd.get(row["wallet"], Decimal("0")) + dec(row["proceeds_usd"])

    wallet_rows: list[dict[str, str]] = []
    for item in wallet_totals.values():
        buy_usd = dec(item["buy_usd"])
        pnl = dec(item["realized_pnl_usd"])
        wallet = str(item["wallet"])
        wallet_rows.append(
            {
                "wallet": wallet,
                "buy_count": str(item["buy_count"]),
                "sell_count": str(item["sell_count"]),
                "closed_count_with_basis": str(item["closed_count"]),
                "buy_usd": money(buy_usd),
                "sell_usd": money(dec(item["sell_usd"])),
                "realized_pnl_usd": money(pnl),
                "realized_pnl_pct_on_buys": money((pnl / buy_usd * Decimal("100")) if buy_usd > 0 else Decimal("0")),
                "open_cost_usd": money(dec(item["open_cost_usd"])),
                "sell_usd_without_basis": money(no_basis_usd.get(wallet, Decimal("0"))),
            }
        )
    wallet_rows.sort(key=lambda row: dec(row["realized_pnl_usd"]), reverse=True)
    return trade_rows, closed, summary_rows, wallet_rows


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def chunked(items: list[str], size: int) -> list[list[str]]:
    return [items[index : index + size] for index in range(0, len(items), size)]


def main() -> int:
    parser = argparse.ArgumentParser(description="Discover copyable wallets and analyze realized DEX trade PnL with Dune.")
    parser.add_argument("--wallet", action="append", default=[], help="Wallet address. Can be repeated.")
    parser.add_argument("--wallet-file", help="Text file with one wallet per line.")
    parser.add_argument("--discover", action="store_true", help="Discover wallets first, then run PnL verification.")
    parser.add_argument("--days", type=int, default=90, help="Lookback window in days. Default: 90.")
    parser.add_argument("--min-trade-usd", default="100", help="Minimum DEX trade size to include. Default: 100.")
    parser.add_argument("--discover-min-trade-usd", default="1000", help="Minimum trade USD for discovery. Default: 1000.")
    parser.add_argument("--discover-limit", type=int, default=100, help="Discovery candidate limit. Default: 100.")
    parser.add_argument("--min-active-weeks", type=int, default=2, help="Discovery active weeks threshold. Default: 2.")
    parser.add_argument("--min-swaps", type=int, default=3, help="Discovery meaningful swap threshold. Default: 3.")
    parser.add_argument("--min-balance-usd", default="50000", help="Discovery current copyable balance minimum. Default: 50000.")
    parser.add_argument("--max-balance-usd", default="100000000", help="Discovery current copyable balance maximum. Default: 100000000.")
    parser.add_argument("--pnl-chunk-size", type=int, default=25, help="Wallets per PnL Dune query. Default: 25.")
    parser.add_argument("--timeout", type=int, default=900, help="Dune timeout seconds. Default: 900.")
    parser.add_argument("--poll", type=int, default=5, help="Dune polling interval seconds. Default: 5.")
    parser.add_argument("--out-dir", default="data/reports/wallet_pnl", help="Output directory.")
    parser.add_argument("--env", default=".env", help="Env file path. Default: .env")
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    load_env(root / args.env)
    api_key = os.environ.get("DUNE_API_KEY", "").strip()
    if not api_key:
        print("DUNE_API_KEY is missing. Put it in .env or export it.", file=sys.stderr)
        return 2

    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    out_dir = root / args.out_dir / timestamp
    out_dir.mkdir(parents=True, exist_ok=True)

    wallets = [normalize_wallet(wallet) for wallet in args.wallet]
    if args.wallet_file:
        with Path(args.wallet_file).open("r", encoding="utf-8") as handle:
            wallets.extend(normalize_wallet(line) for line in handle if line.strip() and not line.startswith("#"))
    discovered_rows: list[dict[str, Any]] = []
    discovery_sql = ""
    if args.discover:
        discovery_sql = build_discovery_sql(
            max(1, min(args.days, 365)),
            dec(args.discover_min_trade_usd),
            max(1, args.min_active_weeks),
            max(1, args.min_swaps),
            max(1, min(args.discover_limit, 500)),
            dec(args.min_balance_usd),
            dec(args.max_balance_usd),
        )
        print(
            "Discovery: "
            f"days={args.days}, limit={args.discover_limit}, "
            f"balance=${args.min_balance_usd}-${args.max_balance_usd}, "
            f"active_weeks>={args.min_active_weeks}, swaps>={args.min_swaps}",
            flush=True,
        )
        discovered_rows = execute_dune_sql(api_key, discovery_sql, args.timeout, args.poll)
        write_csv(out_dir / "discovered_candidates.csv", discovered_rows)
        (out_dir / "discovery.sql").write_text(discovery_sql, encoding="utf-8")
        wallets = [normalize_wallet(str(row["wallet_address"])) for row in discovered_rows]
        print(f"Discovered wallets: {len(wallets)}", flush=True)

    if not wallets:
        wallets = DEFAULT_WALLETS
    wallets = sorted(set(wallets))

    print(f"Wallets: {len(wallets)} | days: {args.days} | min trade USD: {args.min_trade_usd}", flush=True)
    rows: list[dict[str, Any]] = []
    pnl_sql_parts: list[str] = []
    chunks = chunked(wallets, max(1, args.pnl_chunk_size))
    for index, wallet_chunk in enumerate(chunks, start=1):
        print(f"PnL chunk {index}/{len(chunks)}: {len(wallet_chunk)} wallets", flush=True)
        sql = build_sql(wallet_chunk, max(1, min(args.days, 365)), dec(args.min_trade_usd))
        pnl_sql_parts.append(f"-- chunk {index}\n{sql}")
        try:
            rows.extend(execute_dune_sql(api_key, sql, args.timeout, args.poll))
            (out_dir / "raw_rows_partial.json").write_text(json.dumps(rows, indent=2, default=str), encoding="utf-8")
            (out_dir / "pnl_queries.sql").write_text("\n\n".join(pnl_sql_parts), encoding="utf-8")
        except Exception as exc:
            message = f"PnL chunk {index}/{len(chunks)} failed: {exc}"
            print(message, file=sys.stderr, flush=True)
            (out_dir / "error.txt").write_text(message + "\n", encoding="utf-8")
            if not rows:
                raise
            break
    print(f"Dune rows: {len(rows)}", flush=True)

    legs = trade_legs(rows)
    trade_rows, closed_rows, summary_rows, wallet_rows = calculate_fifo(legs)

    write_csv(out_dir / "trades.csv", trade_rows)
    write_csv(out_dir / "closed_positions.csv", closed_rows)
    write_csv(out_dir / "summary.csv", summary_rows)
    write_csv(out_dir / "wallet_summary.csv", wallet_rows)
    (out_dir / "pnl_queries.sql").write_text("\n\n".join(pnl_sql_parts), encoding="utf-8")
    (out_dir / "raw_rows.json").write_text(json.dumps(rows, indent=2, default=str), encoding="utf-8")

    positive_wallets = [
        row
        for row in wallet_rows
        if dec(row["realized_pnl_usd"]) > 0 and dec(row["buy_usd"]) > 0 and int(row["closed_count_with_basis"]) > 0
    ]
    write_csv(out_dir / "positive_wallets.csv", positive_wallets)

    print(f"Report directory: {out_dir}")
    print("\nTop realized PnL rows:")
    ranked = sorted(summary_rows, key=lambda row: dec(row["realized_pnl_usd"]), reverse=True)
    for row in ranked[:20]:
        print(
            f"{row['wallet']} {row['asset']}: pnl ${row['realized_pnl_usd']} "
            f"({row['realized_pnl_pct_on_buys']}% on buys), buys ${row['buy_usd']}, sells ${row['sell_usd']}, "
            f"open cost ${row['open_cost_usd']}"
        )
    print("\nWallet totals:")
    for row in wallet_rows:
        print(
            f"{row['wallet']}: pnl ${row['realized_pnl_usd']} "
            f"({row['realized_pnl_pct_on_buys']}% on buys), buys ${row['buy_usd']}, "
            f"sells ${row['sell_usd']}, open cost ${row['open_cost_usd']}, "
            f"no-basis sells ${row['sell_usd_without_basis']}, closed fills {row['closed_count_with_basis']}"
        )
    print(f"\nPositive realized-PnL wallets: {len(positive_wallets)}")
    for row in positive_wallets[:25]:
        print(
            f"{row['wallet']}: pnl ${row['realized_pnl_usd']} "
            f"({row['realized_pnl_pct_on_buys']}% on buys), buys ${row['buy_usd']}, sells ${row['sell_usd']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
