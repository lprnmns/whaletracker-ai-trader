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

The script is intentionally CSV-first so we can inspect whether a trader is
actually copyable before wiring it into the web UI.
"""

from __future__ import annotations

import argparse
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


INFO_URL = "https://api.hyperliquid.xyz/info"
MAX_FILLS_PER_REQUEST = 2000
COPYABLE_MAJOR_COINS = {
    "BTC",
    "ETH",
    "SOL",
    "AVAX",
    "LINK",
    "HYPE",
    "DOGE",
    "BNB",
    "XRP",
    "WLD",
    "ARB",
    "OP",
    "SUI",
    "TIA",
    "AAVE",
    "PENDLE",
    "UNI",
}


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


def post_info(
    payload: dict[str, Any],
    stats: ApiStats,
    sleep_seconds: float = 0.05,
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
                if sleep_seconds > 0:
                    time.sleep(sleep_seconds)
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
                "copyable_major": "yes" if coin in COPYABLE_MAJOR_COINS else "no",
            }
        )
    return rows


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
                "copyable_major": "yes" if coin in COPYABLE_MAJOR_COINS else "no",
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
                "copyable_major": "yes" if coin in COPYABLE_MAJOR_COINS else "no",
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
                "copyable_major": "yes" if coin in COPYABLE_MAJOR_COINS else "no",
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
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


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
        (dec(fill.get("px")) * dec(fill.get("sz")) for fill in fills if str(fill.get("coin") or "") in COPYABLE_MAJOR_COINS),
        Decimal("0"),
    )
    copyable_ratio = copyable_notional / notional if notional > 0 else Decimal("0")
    active_copyable_positions = sum(1 for row in position_rows if row["copyable_major"] == "yes")
    active_positions = len(position_rows)
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
        "closed_pnl_usd": money(closed_pnl),
        "fees_usd": money(fees),
        "net_closed_pnl_usd": money(closed_pnl - fees),
        "fill_notional_usd": money(notional),
        "copyable_major_notional_usd": money(copyable_notional),
        "copyable_major_ratio_pct": pct(copyable_ratio),
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
        return "LOW_COPYABLE_MAJOR_RATIO"
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
    summary = build_summary(address, state if isinstance(state, dict) else {}, fills, positions_out, coin_out, stats, args.days)

    out_dir = root_out / address
    write_csv(out_dir / "fills.csv", fills_out)
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
    parser.add_argument("--min-leaderboard-pnl-usd", default="0")
    parser.add_argument("--min-leaderboard-account-usd", default="0")
    parser.add_argument("--min-leaderboard-volume-usd", default="0")
    parser.add_argument("--out-dir", default="data/reports/hyperliquid_profiles")
    args = parser.parse_args()

    addresses = load_addresses(args)
    if not addresses:
        raise SystemExit("No addresses supplied.")
    if args.max_addresses:
        addresses = addresses[: args.max_addresses]

    root = Path(__file__).resolve().parents[1]
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    root_out = root / args.out_dir / timestamp
    root_out.mkdir(parents=True, exist_ok=True)

    summaries: list[dict[str, Any]] = []
    for address in addresses:
        try:
            summaries.append(profile_address(address, args, root_out))
            if args.address_delay > 0:
                time.sleep(args.address_delay)
        except Exception as exc:
            error = {"address": address, "error": str(exc)}
            summaries.append(error)
            print(f"{address}: ERROR {exc}", flush=True)

    summary_rows = [
        {key: str(value) for key, value in summary.items() if not isinstance(value, (list, dict))}
        for summary in summaries
    ]
    write_csv(root_out / "trader_summaries.csv", summary_rows)
    (root_out / "trader_summaries.json").write_text(json.dumps(summaries, indent=2), encoding="utf-8")
    print(f"Report directory: {root_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
