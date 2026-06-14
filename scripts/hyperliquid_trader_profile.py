#!/usr/bin/env python3
"""
Build a Hyperliquid trader profile from official public API data.

Inputs:
- one --address
- or --address-file with one address per line
- or --leaderboard-csv exported by hyperliquid_leaderboard_export.py

Outputs per trader:
- fills.csv
- coin_summary.csv
- active_positions.csv
- open_orders.csv
- portfolio_history.csv
- summary.json
- scan_progress.json in the run directory

The script is intentionally CSV-first so we can inspect whether a trader is
actually copyable before wiring it into the web UI.
"""

from __future__ import annotations

import argparse
import bisect
import csv
import json
import time
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from okx_symbol_universe import is_okx_copyable, load_okx_usdt_swap_symbols


INFO_URL = "https://api.hyperliquid.xyz/info"
MAX_FILLS_PER_REQUEST = 2000
REQUEST_DELAY_SECONDS = 0.05
MAX_REQUESTS_PER_ADDRESS = 350
OKX_COPYABLE_SYMBOLS: frozenset[str] = frozenset()

POSITION_EPSILON = Decimal("0.00000001")


@dataclass
class ApiStats:
    requests: int = 0
    split_ranges: int = 0
    retries: int = 0


def dec(value: Any) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0"))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def number(value: Decimal) -> str:
    return f"{value.normalize():f}"


def pct(value: Decimal) -> str:
    return f"{(value * Decimal('100')).quantize(Decimal('0.01'))}"


def iso_ms(ms: int) -> str:
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).isoformat()


def now_ms() -> int:
    return int(time.time() * 1000)


def normalize_address(value: str) -> str:
    address = value.strip().lower()
    if not address.startswith("0x") or len(address) != 42:
        raise ValueError(f"Invalid address: {value}")
    int(address[2:], 16)
    return address


def is_okx_tradable(coin: str) -> bool:
    return is_okx_copyable(coin, OKX_COPYABLE_SYMBOLS)


def post_info(
    payload: dict[str, Any],
    stats: ApiStats,
    sleep_seconds: float | None = None,
    max_retries: int = 6,
) -> Any:
    body = json.dumps(payload).encode("utf-8")
    for attempt in range(max_retries + 1):
        request = Request(
            INFO_URL,
            data=body,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json",
                "User-Agent": "WhaleTracker/1.0",
            },
        )
        try:
            with urlopen(request, timeout=60) as response:
                stats.requests += 1
                delay = REQUEST_DELAY_SECONDS if sleep_seconds is None else sleep_seconds
                if delay > 0:
                    time.sleep(delay)
                return json.loads(response.read().decode("utf-8"))
        except HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            if exc.code == 429 and attempt < max_retries:
                wait = min(90, (2 ** attempt) * 5)
                stats.retries += 1
                print(f"Hyperliquid 429 for {payload.get('type')}; retry in {wait}s", flush=True)
                time.sleep(wait)
                continue
            raise RuntimeError(f"Hyperliquid HTTP {exc.code}: {detail}") from exc
        except URLError as exc:
            if attempt < max_retries:
                wait = min(60, (2 ** attempt) * 3)
                stats.retries += 1
                print(f"Hyperliquid network error for {payload.get('type')}; retry in {wait}s: {exc}", flush=True)
                time.sleep(wait)
                continue
            raise RuntimeError(f"Hyperliquid network error: {exc}") from exc
    raise RuntimeError("Hyperliquid request failed after retries.")


def fetch_fills_range(
    address: str,
    start_ms: int,
    end_ms: int,
    stats: ApiStats,
    min_split_ms: int,
) -> list[dict[str, Any]]:
    if end_ms <= start_ms:
        return []
    if MAX_REQUESTS_PER_ADDRESS and stats.requests + stats.retries >= MAX_REQUESTS_PER_ADDRESS:
        raise RuntimeError(
            f"Address request budget exceeded ({MAX_REQUESTS_PER_ADDRESS}); "
            "profile is too dense for this run."
        )
    fills = post_info(
        {
            "type": "userFillsByTime",
            "user": address,
            "startTime": start_ms,
            "endTime": end_ms,
        },
        stats,
    )
    if not isinstance(fills, list):
        return []
    if len(fills) < MAX_FILLS_PER_REQUEST or end_ms - start_ms <= min_split_ms:
        return fills

    mid = start_ms + ((end_ms - start_ms) // 2)
    stats.split_ranges += 1
    return (
        fetch_fills_range(address, start_ms, mid, stats, min_split_ms)
        + fetch_fills_range(address, mid + 1, end_ms, stats, min_split_ms)
    )


def fetch_fills(address: str, days: int, stats: ApiStats, min_split_hours: int) -> list[dict[str, Any]]:
    end_ms = now_ms()
    start_ms = end_ms - max(1, days) * 86_400_000
    raw = fetch_fills_range(address, start_ms, end_ms, stats, max(1, min_split_hours) * 3_600_000)
    unique: dict[str, dict[str, Any]] = {}
    for fill in raw:
        key = f"{fill.get('tid')}:{fill.get('hash')}:{fill.get('coin')}:{fill.get('time')}:{fill.get('side')}:{fill.get('sz')}"
        unique[key] = fill
    return sorted(unique.values(), key=lambda row: int(row.get("time") or 0))


def fill_rows(fills: list[dict[str, Any]]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for fill in fills:
        px = dec(fill.get("px"))
        size = dec(fill.get("sz"))
        notional = px * size
        fee = dec(fill.get("fee"))
        closed_pnl = dec(fill.get("closedPnl"))
        coin = str(fill.get("coin") or "")
        rows.append(
            {
                "time": iso_ms(int(fill.get("time") or 0)),
                "coin": coin,
                "side": str(fill.get("side") or ""),
                "direction": str(fill.get("dir") or ""),
                "price": number(px),
                "size": number(size),
                "notional_usd": money(notional),
                "closed_pnl_usd": money(closed_pnl),
                "fee_usd": money(fee),
                "net_closed_pnl_usd": money(closed_pnl - fee),
                "start_position": str(fill.get("startPosition") or ""),
                "crossed": str(fill.get("crossed")),
                "fee_token": str(fill.get("feeToken") or ""),
                "order_id": str(fill.get("oid") or ""),
                "trade_id": str(fill.get("tid") or ""),
                "hash": str(fill.get("hash") or ""),
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
            }
        )
    return rows


def sign(value: Decimal) -> int:
    if value > POSITION_EPSILON:
        return 1
    if value < -POSITION_EPSILON:
        return -1
    return 0


def market_type(coin: str) -> str:
    if is_okx_tradable(coin):
        return "PERP_OKX_TRADABLE"
    if coin.startswith("@") or coin.startswith("#") or ":" in coin:
        return "SPOT_OR_NON_STANDARD"
    return "PERP_OTHER"


def account_history_points(portfolio: list[Any]) -> list[tuple[int, Decimal]]:
    points: dict[int, Decimal] = {}
    for item in portfolio:
        if not isinstance(item, (list, tuple)) or len(item) != 2:
            continue
        _, data = item
        if not isinstance(data, dict):
            continue
        for ts, value in data.get("accountValueHistory", []):
            points[int(ts)] = dec(value)
    return sorted(points.items(), key=lambda row: row[0])


def nearest_account_value(points: list[tuple[int, Decimal]], timestamp_ms: int) -> Decimal:
    if not points:
        return Decimal("0")
    times = [point[0] for point in points]
    index = bisect.bisect_left(times, timestamp_ms)
    if index <= 0:
        return points[0][1]
    if index >= len(points):
        return points[-1][1]
    before = points[index - 1]
    after = points[index]
    return before[1] if timestamp_ms - before[0] <= after[0] - timestamp_ms else after[1]


def action_for_position(start_position: Decimal, end_position: Decimal) -> str:
    start_sign = sign(start_position)
    end_sign = sign(end_position)
    if start_sign == 0 and end_sign != 0:
        return "OPEN_LONG" if end_sign > 0 else "OPEN_SHORT"
    if start_sign != 0 and end_sign == 0:
        return "CLOSE_LONG" if start_sign > 0 else "CLOSE_SHORT"
    if start_sign != 0 and end_sign != 0 and start_sign != end_sign:
        return "FLIP_SHORT_TO_LONG" if end_sign > 0 else "FLIP_LONG_TO_SHORT"
    if abs(end_position) > abs(start_position):
        return "INCREASE_LONG" if end_sign > 0 else "INCREASE_SHORT"
    if abs(end_position) < abs(start_position):
        return "REDUCE_LONG" if start_sign > 0 else "REDUCE_SHORT"
    return "NO_POSITION_CHANGE"


def build_position_history(
    fills: list[dict[str, Any]],
    portfolio: list[Any],
) -> tuple[list[dict[str, str]], list[dict[str, str]]]:
    account_points = account_history_points(portfolio)
    events: list[dict[str, str]] = []
    closed_positions: list[dict[str, str]] = []
    open_rounds: dict[str, dict[str, Any]] = {}

    def start_round(coin: str, timestamp_ms: int, side: str, price: Decimal, qty: Decimal, notional: Decimal, balance_pct: Decimal) -> None:
        open_rounds[coin] = {
            "coin": coin,
            "side": side,
            "opened_at_ms": timestamp_ms,
            "entry_notional": notional,
            "entry_qty": abs(qty),
            "weighted_entry": price * abs(qty),
            "max_abs_qty": abs(qty),
            "max_notional": notional,
            "max_balance_pct": balance_pct,
            "closed_pnl": Decimal("0"),
            "fees": Decimal("0"),
            "fill_count": 1,
        }

    def increase_round(coin: str, price: Decimal, qty: Decimal, notional: Decimal, balance_pct: Decimal, fee: Decimal) -> None:
        item = open_rounds.get(coin)
        if not item:
            return
        abs_qty = abs(qty)
        item["entry_notional"] += notional
        item["entry_qty"] += abs_qty
        item["weighted_entry"] += price * abs_qty
        item["max_abs_qty"] = max(item["max_abs_qty"], abs_qty)
        item["max_notional"] = max(item["max_notional"], notional)
        item["max_balance_pct"] = max(item["max_balance_pct"], balance_pct)
        item["fees"] += fee
        item["fill_count"] += 1

    def close_round(
        coin: str,
        timestamp_ms: int,
        price: Decimal,
        qty: Decimal,
        notional: Decimal,
        closed_pnl: Decimal,
        fee: Decimal,
        end_position: Decimal,
        full_close: bool,
    ) -> None:
        item = open_rounds.get(coin)
        if not item:
            return
        item["closed_pnl"] += closed_pnl
        item["fees"] += fee
        item["fill_count"] += 1
        if not full_close:
            return

        entry_qty = item["entry_qty"]
        avg_entry = item["weighted_entry"] / entry_qty if entry_qty > 0 else Decimal("0")
        holding_hours = Decimal(timestamp_ms - item["opened_at_ms"]) / Decimal(3_600_000)
        net_pnl = item["closed_pnl"] - item["fees"]
        closed_positions.append(
            {
                "coin": coin,
                "market_type": market_type(coin),
                "side": item["side"],
                "opened_at": iso_ms(int(item["opened_at_ms"])),
                "closed_at": iso_ms(timestamp_ms),
                "holding_hours": money(holding_hours),
                "holding_days": money(holding_hours / Decimal("24")),
                "avg_entry_price": number(avg_entry),
                "avg_exit_price": number(price),
                "entry_notional_usd": money(item["entry_notional"]),
                "exit_notional_usd": money(notional),
                "max_single_fill_notional_usd": money(item["max_notional"]),
                "max_fill_balance_pct": pct(item["max_balance_pct"] / Decimal("100")),
                "closed_pnl_usd": money(item["closed_pnl"]),
                "fees_usd": money(item["fees"]),
                "net_pnl_usd": money(net_pnl),
                "return_on_entry_notional_pct": pct(net_pnl / item["entry_notional"] if item["entry_notional"] > 0 else Decimal("0")),
                "fill_count": str(item["fill_count"]),
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
                "end_position": number(end_position),
            }
        )
        del open_rounds[coin]

    for fill in fills:
        timestamp_ms = int(fill.get("time") or 0)
        coin = str(fill.get("coin") or "")
        px = dec(fill.get("px"))
        size = dec(fill.get("sz"))
        notional = px * size
        fee = dec(fill.get("fee"))
        closed_pnl = dec(fill.get("closedPnl"))
        start_position = dec(fill.get("startPosition"))
        delta = size if fill.get("side") == "B" else -size
        end_position = start_position + delta
        action = action_for_position(start_position, end_position)
        account_value = nearest_account_value(account_points, timestamp_ms)
        balance_pct = (notional / account_value * Decimal("100")) if account_value > 0 else Decimal("0")
        direction = str(fill.get("dir") or "")

        events.append(
            {
                "time": iso_ms(timestamp_ms),
                "coin": coin,
                "market_type": market_type(coin),
                "action": action,
                "hyperliquid_direction": direction,
                "side": str(fill.get("side") or ""),
                "price": number(px),
                "size": number(size),
                "notional_usd": money(notional),
                "account_value_usd": money(account_value),
                "fill_balance_pct": pct(balance_pct / Decimal("100")),
                "start_position": number(start_position),
                "end_position": number(end_position),
                "closed_pnl_usd": money(closed_pnl),
                "fee_usd": money(fee),
                "net_closed_pnl_usd": money(closed_pnl - fee),
                "order_id": str(fill.get("oid") or ""),
                "trade_id": str(fill.get("tid") or ""),
                "hash": str(fill.get("hash") or ""),
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
            }
        )

        start_s = sign(start_position)
        end_s = sign(end_position)
        if start_s == 0 and end_s != 0:
            start_round(coin, timestamp_ms, "LONG" if end_s > 0 else "SHORT", px, end_position, notional, balance_pct)
        elif start_s != 0 and end_s == 0:
            close_round(coin, timestamp_ms, px, size, notional, closed_pnl, fee, end_position, True)
        elif start_s != 0 and end_s != 0 and start_s != end_s:
            close_round(coin, timestamp_ms, px, size, notional, closed_pnl, fee, Decimal("0"), True)
            start_round(coin, timestamp_ms, "LONG" if end_s > 0 else "SHORT", px, end_position, px * abs(end_position), balance_pct)
        elif abs(end_position) > abs(start_position):
            if coin not in open_rounds:
                start_round(coin, timestamp_ms, "LONG" if end_s > 0 else "SHORT", px, end_position, notional, balance_pct)
            else:
                increase_round(coin, px, delta, notional, balance_pct, fee)
        elif abs(end_position) < abs(start_position):
            close_round(coin, timestamp_ms, px, size, notional, closed_pnl, fee, end_position, False)

    closed_positions.sort(key=lambda row: dec(row["net_pnl_usd"]), reverse=True)
    return events, closed_positions


def summarize_by_coin(fills: list[dict[str, Any]]) -> list[dict[str, str]]:
    stats: dict[str, dict[str, Decimal | int]] = defaultdict(
        lambda: {
            "fill_count": 0,
            "buy_count": 0,
            "sell_count": 0,
            "notional_usd": Decimal("0"),
            "closed_pnl_usd": Decimal("0"),
            "fee_usd": Decimal("0"),
            "winning_fills": 0,
            "losing_fills": 0,
        }
    )
    for fill in fills:
        coin = str(fill.get("coin") or "")
        item = stats[coin]
        pnl = dec(fill.get("closedPnl"))
        fee = dec(fill.get("fee"))
        notional = dec(fill.get("px")) * dec(fill.get("sz"))
        item["fill_count"] = int(item["fill_count"]) + 1
        item["buy_count"] = int(item["buy_count"]) + (1 if fill.get("side") == "B" else 0)
        item["sell_count"] = int(item["sell_count"]) + (1 if fill.get("side") == "A" else 0)
        item["notional_usd"] = dec(item["notional_usd"]) + notional
        item["closed_pnl_usd"] = dec(item["closed_pnl_usd"]) + pnl
        item["fee_usd"] = dec(item["fee_usd"]) + fee
        item["winning_fills"] = int(item["winning_fills"]) + (1 if pnl > 0 else 0)
        item["losing_fills"] = int(item["losing_fills"]) + (1 if pnl < 0 else 0)

    rows: list[dict[str, str]] = []
    for coin, item in stats.items():
        fills_count = int(item["fill_count"])
        pnl = dec(item["closed_pnl_usd"])
        fee = dec(item["fee_usd"])
        notional = dec(item["notional_usd"])
        rows.append(
            {
                "coin": coin,
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
                "fill_count": str(fills_count),
                "buy_count": str(item["buy_count"]),
                "sell_count": str(item["sell_count"]),
                "notional_usd": money(notional),
                "closed_pnl_usd": money(pnl),
                "fee_usd": money(fee),
                "net_closed_pnl_usd": money(pnl - fee),
                "pnl_per_notional_pct": pct((pnl - fee) / notional if notional > 0 else Decimal("0")),
                "win_rate_pct": pct(Decimal(int(item["winning_fills"])) / Decimal(fills_count) if fills_count else Decimal("0")),
            }
        )
    rows.sort(key=lambda row: dec(row["net_closed_pnl_usd"]), reverse=True)
    return rows


def active_position_rows(state: dict[str, Any]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for item in state.get("assetPositions") or []:
        position = item.get("position") or {}
        coin = str(position.get("coin") or "")
        size = dec(position.get("szi"))
        rows.append(
            {
                "coin": coin,
                "side": "LONG" if size > 0 else "SHORT" if size < 0 else "FLAT",
                "size": number(size),
                "entry_price": str(position.get("entryPx") or ""),
                "position_value_usd": money(dec(position.get("positionValue"))),
                "unrealized_pnl_usd": money(dec(position.get("unrealizedPnl"))),
                "roe_pct": pct(dec(position.get("returnOnEquity"))),
                "liquidation_price": str(position.get("liquidationPx") or ""),
                "leverage": str((position.get("leverage") or {}).get("value") or ""),
                "margin_type": str((position.get("leverage") or {}).get("type") or ""),
                "margin_used_usd": money(dec(position.get("marginUsed"))),
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
            }
        )
    rows.sort(key=lambda row: dec(row["position_value_usd"]), reverse=True)
    return rows


def open_order_rows(orders: list[dict[str, Any]]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for order in orders:
        coin = str(order.get("coin") or "")
        rows.append(
            {
                "coin": coin,
                "side": str(order.get("side") or ""),
                "limit_price": str(order.get("limitPx") or ""),
                "size": str(order.get("sz") or ""),
                "order_id": str(order.get("oid") or ""),
                "timestamp": iso_ms(int(order.get("timestamp") or 0)),
                "reduce_only": str(order.get("reduceOnly")),
                "order_type": str(order.get("orderType") or ""),
                "okx_tradable": "yes" if is_okx_tradable(coin) else "no",
            }
        )
    return rows


def portfolio_rows(portfolio: list[Any]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for window, data in portfolio:
        for ts, value in data.get("accountValueHistory", []):
            rows.append(
                {
                    "window": str(window),
                    "metric": "account_value",
                    "time": iso_ms(int(ts)),
                    "value_usd": money(dec(value)),
                }
            )
        for ts, value in data.get("pnlHistory", []):
            rows.append(
                {
                    "window": str(window),
                    "metric": "pnl",
                    "time": iso_ms(int(ts)),
                    "value_usd": money(dec(value)),
                }
            )
    return rows


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        fieldnames: list[str] = []
        for row in rows:
            for key in row.keys():
                if key not in fieldnames:
                    fieldnames.append(key)
        writer = csv.DictWriter(handle, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def load_existing_summary(root_out: Path, address: str) -> dict[str, Any] | None:
    path = root_out / address / "summary.json"
    if not path.exists():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None
    if isinstance(payload, dict):
        return payload
    return None


def write_run_outputs(
    root_out: Path,
    summaries: list[dict[str, Any]],
    progress: dict[str, Any],
) -> None:
    summary_rows = [
        {key: str(value) for key, value in summary.items() if not isinstance(value, (list, dict))}
        for summary in summaries
    ]
    write_csv(root_out / "trader_summaries.csv", summary_rows)
    write_json(root_out / "trader_summaries.json", summaries)
    write_json(root_out / "scan_progress.json", progress)


def update_progress(
    progress: dict[str, Any],
    *,
    completed: int | None = None,
    skipped_existing: int | None = None,
    errors: int | None = None,
    current_address: str | None = None,
    last_error: str | None = None,
) -> None:
    if completed is not None:
        progress["completed"] = completed
    if skipped_existing is not None:
        progress["skipped_existing"] = skipped_existing
    if errors is not None:
        progress["errors"] = errors
    progress["current_address"] = current_address or ""
    if last_error is not None:
        progress["last_error"] = last_error
    progress["updated_at"] = datetime.now(timezone.utc).isoformat()
    total = int(progress.get("total") or 0)
    done = int(progress.get("completed") or 0) + int(progress.get("skipped_existing") or 0) + int(progress.get("errors") or 0)
    progress["percent"] = round((done / total * 100), 2) if total else 0


def load_addresses(args: argparse.Namespace) -> list[str]:
    addresses: list[str] = []
    for address in args.address:
        addresses.append(normalize_address(address))
    if args.address_file:
        for line in Path(args.address_file).read_text(encoding="utf-8").splitlines():
            raw = line.strip()
            if raw and not raw.startswith("#"):
                addresses.append(normalize_address(raw))
    if args.leaderboard_csv:
        with Path(args.leaderboard_csv).open("r", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                if dec(row.get("pnl_usd")) >= dec(args.min_leaderboard_pnl_usd):
                    if dec(row.get("account_value_usd")) >= dec(args.min_leaderboard_account_usd):
                        if dec(row.get("volume_usd")) >= dec(args.min_leaderboard_volume_usd):
                            addresses.append(normalize_address(str(row["address"])))
                if args.max_addresses and len(addresses) >= args.max_addresses:
                    break
    return sorted(set(addresses))


def build_summary(
    address: str,
    state: dict[str, Any],
    fills: list[dict[str, Any]],
    position_rows: list[dict[str, str]],
    coin_rows: list[dict[str, str]],
    closed_position_rows: list[dict[str, str]],
    api_stats: ApiStats,
    days: int,
) -> dict[str, Any]:
    margin = state.get("marginSummary") or {}
    account_value = dec(margin.get("accountValue"))
    total_notional = dec(margin.get("totalNtlPos"))
    margin_used = dec(margin.get("totalMarginUsed"))
    closed_pnl = sum((dec(fill.get("closedPnl")) for fill in fills), Decimal("0"))
    fees = sum((dec(fill.get("fee")) for fill in fills), Decimal("0"))
    notional = sum((dec(fill.get("px")) * dec(fill.get("sz")) for fill in fills), Decimal("0"))
    copyable_notional = sum(
        (
            dec(fill.get("px")) * dec(fill.get("sz"))
            for fill in fills
            if is_okx_tradable(str(fill.get("coin") or ""))
        ),
        Decimal("0"),
    )
    copyable_ratio = copyable_notional / notional if notional > 0 else Decimal("0")
    active_copyable_positions = sum(1 for row in position_rows if row["okx_tradable"] == "yes")
    active_positions = len(position_rows)
    closed_positions = len(closed_position_rows)
    winning_positions = sum(1 for row in closed_position_rows if dec(row.get("net_pnl_usd")) > 0)
    copyable_closed_positions = [row for row in closed_position_rows if row.get("okx_tradable") == "yes"]
    copyable_net_position_pnl = sum((dec(row.get("net_pnl_usd")) for row in copyable_closed_positions), Decimal("0"))
    avg_holding_hours = (
        sum((dec(row.get("holding_hours")) for row in closed_position_rows), Decimal("0")) / Decimal(closed_positions)
        if closed_positions
        else Decimal("0")
    )
    largest_balance_pct = max((dec(row.get("max_fill_balance_pct")) for row in closed_position_rows), default=Decimal("0"))
    return {
        "address": address,
        "days": days,
        "account_value_usd": money(account_value),
        "withdrawable_usd": money(dec(state.get("withdrawable"))),
        "total_position_notional_usd": money(total_notional),
        "margin_used_usd": money(margin_used),
        "active_positions": active_positions,
        "active_copyable_positions": active_copyable_positions,
        "fill_count": len(fills),
        "coin_count": len(coin_rows),
        "closed_position_count": closed_positions,
        "winning_position_count": winning_positions,
        "position_win_rate_pct": pct(Decimal(winning_positions) / Decimal(closed_positions) if closed_positions else Decimal("0")),
        "average_holding_hours": money(avg_holding_hours),
        "largest_position_fill_balance_pct": number(largest_balance_pct),
        "closed_pnl_usd": money(closed_pnl),
        "fees_usd": money(fees),
        "net_closed_pnl_usd": money(closed_pnl - fees),
        "copyable_position_net_pnl_usd": money(copyable_net_position_pnl),
        "copyable_closed_position_count": len(copyable_closed_positions),
        "fill_notional_usd": money(notional),
        "okx_tradable_notional_usd": money(copyable_notional),
        "okx_tradable_ratio_pct": pct(copyable_ratio),
        "okx_symbol_universe_size": len(OKX_COPYABLE_SYMBOLS),
        "api_requests": api_stats.requests,
        "split_ranges": api_stats.split_ranges,
        "retries": api_stats.retries,
        "verdict": trader_verdict(account_value, fills, closed_pnl - fees, copyable_ratio, active_positions),
    }


def trader_verdict(
    account_value: Decimal,
    fills: list[dict[str, Any]],
    net_pnl: Decimal,
    copyable_ratio: Decimal,
    active_positions: int,
) -> str:
    if not fills:
        return "NO_RECENT_FILLS"
    if account_value <= 0 and active_positions == 0:
        return "INACTIVE_OR_WITHDRAWN"
    if copyable_ratio < Decimal("0.5"):
        return "LOW_OKX_TRADABLE_RATIO"
    if net_pnl <= 0:
        return "NOT_PROFITABLE_IN_WINDOW"
    return "REVIEW_COPYABLE"


def profile_address(address: str, args: argparse.Namespace, root_out: Path) -> dict[str, Any]:
    stats = ApiStats()
    print(f"Profiling {address} for {args.days}d", flush=True)
    state = post_info({"type": "clearinghouseState", "user": address}, stats)
    portfolio = post_info({"type": "portfolio", "user": address}, stats)
    open_orders = post_info({"type": "openOrders", "user": address}, stats)
    fills = fetch_fills(address, args.days, stats, args.min_split_hours)

    fills_out = fill_rows(fills)
    coin_out = summarize_by_coin(fills)
    positions_out = active_position_rows(state if isinstance(state, dict) else {})
    orders_out = open_order_rows(open_orders if isinstance(open_orders, list) else [])
    portfolio_out = portfolio_rows(portfolio if isinstance(portfolio, list) else [])
    position_events_out, closed_positions_out = build_position_history(
        fills,
        portfolio if isinstance(portfolio, list) else [],
    )
    summary = build_summary(
        address,
        state if isinstance(state, dict) else {},
        fills,
        positions_out,
        coin_out,
        closed_positions_out,
        stats,
        args.days,
    )

    out_dir = root_out / address
    write_csv(out_dir / "fills.csv", fills_out)
    write_csv(out_dir / "position_events.csv", position_events_out)
    write_csv(out_dir / "closed_positions.csv", closed_positions_out)
    write_csv(out_dir / "coin_summary.csv", coin_out)
    write_csv(out_dir / "active_positions.csv", positions_out)
    write_csv(out_dir / "open_orders.csv", orders_out)
    write_csv(out_dir / "portfolio_history.csv", portfolio_out)
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    (out_dir / "raw_state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
    print(
        f"{address}: fills={summary['fill_count']} netPnL=${summary['net_closed_pnl_usd']} "
        f"account=${summary['account_value_usd']} positions={summary['active_positions']} "
        f"verdict={summary['verdict']}",
        flush=True,
    )
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Hyperliquid trader profile reports.")
    parser.add_argument("--address", action="append", default=[], help="Trader address. Can be repeated.")
    parser.add_argument("--address-file", help="Text file with one address per line.")
    parser.add_argument("--leaderboard-csv", help="CSV exported by hyperliquid_leaderboard_export.py.")
    parser.add_argument("--max-addresses", type=int, default=0, help="Maximum addresses from inputs. 0 means all.")
    parser.add_argument("--days", type=int, default=30, help="Fill lookback days. Default: 30.")
    parser.add_argument("--min-split-hours", type=int, default=1, help="Minimum recursive fill split interval. Default: 1.")
    parser.add_argument("--address-delay", type=float, default=1.0, help="Seconds to wait between addresses. Default: 1.")
    parser.add_argument("--request-delay", type=float, default=0.05, help="Seconds to wait after each Hyperliquid API request. Default: 0.05.")
    parser.add_argument("--max-requests-per-address", type=int, default=350, help="Skip a trader after this many API requests/retries. 0 disables the cap.")
    parser.add_argument("--min-leaderboard-pnl-usd", default="0")
    parser.add_argument("--min-leaderboard-account-usd", default="0")
    parser.add_argument("--min-leaderboard-volume-usd", default="0")
    parser.add_argument("--out-dir", default="data/reports/hyperliquid_profiles")
    parser.add_argument("--run-id", default="", help="Stable output run id. Defaults to UTC timestamp.")
    parser.add_argument("--skip-existing", action="store_true", help="Skip addresses with an existing summary.json in the run directory.")
    parser.add_argument("--progress-every", type=int, default=1, help="Write progress after this many processed addresses. Default: 1.")
    parser.add_argument("--refresh-okx-symbols", action="store_true")
    args = parser.parse_args()
    global REQUEST_DELAY_SECONDS
    global MAX_REQUESTS_PER_ADDRESS
    REQUEST_DELAY_SECONDS = max(0, args.request_delay)
    MAX_REQUESTS_PER_ADDRESS = max(0, args.max_requests_per_address)

    addresses = load_addresses(args)
    if not addresses:
        raise SystemExit("No addresses supplied.")
    if args.max_addresses:
        addresses = addresses[: args.max_addresses]

    root = Path(__file__).resolve().parents[1]
    run_id = args.run_id.strip() or datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    root_out = root / args.out_dir / run_id
    root_out.mkdir(parents=True, exist_ok=True)
    global OKX_COPYABLE_SYMBOLS
    OKX_COPYABLE_SYMBOLS = load_okx_usdt_swap_symbols(
        str(root_out / "okx_usdt_swap_symbols.json"),
        args.refresh_okx_symbols,
    )

    summaries: list[dict[str, Any]] = []
    progress: dict[str, Any] = {
        "run_id": run_id,
        "days": args.days,
        "total": len(addresses),
        "completed": 0,
        "skipped_existing": 0,
        "errors": 0,
        "percent": 0,
        "current_address": "",
        "started_at": datetime.now(timezone.utc).isoformat(),
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "output_directory": str(root_out),
    }
    write_json(root_out / "input_addresses.json", addresses)
    write_run_outputs(root_out, summaries, progress)

    completed = 0
    skipped_existing = 0
    errors = 0
    for index, address in enumerate(addresses, start=1):
        try:
            update_progress(progress, current_address=address)
            write_json(root_out / "scan_progress.json", progress)
            existing = load_existing_summary(root_out, address) if args.skip_existing else None
            if existing is not None:
                existing["skipped_existing"] = "yes"
                summaries.append(existing)
                skipped_existing += 1
                print(f"{address}: SKIP existing summary", flush=True)
            else:
                summaries.append(profile_address(address, args, root_out))
                completed += 1
            if args.address_delay > 0:
                time.sleep(args.address_delay)
        except Exception as exc:
            error = {"address": address, "error": str(exc)}
            summaries.append(error)
            errors += 1
            print(f"{address}: ERROR {exc}", flush=True)
        update_progress(
            progress,
            completed=completed,
            skipped_existing=skipped_existing,
            errors=errors,
            current_address=address,
            last_error=str(summaries[-1].get("error", "")),
        )
        if index % max(1, args.progress_every) == 0 or index == len(addresses):
            write_run_outputs(root_out, summaries, progress)
            print(
                f"Progress {progress['percent']}% "
                f"completed={completed} skipped={skipped_existing} errors={errors}/{len(addresses)}",
                flush=True,
            )

    update_progress(progress, completed=completed, skipped_existing=skipped_existing, errors=errors)
    write_run_outputs(root_out, summaries, progress)
    print(f"Report directory: {root_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
