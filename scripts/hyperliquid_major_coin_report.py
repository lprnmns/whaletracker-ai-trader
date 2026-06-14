#!/usr/bin/env python3
"""Aggregate Hyperliquid profiles by live OKX-tradable perpetual coins."""

from __future__ import annotations

import argparse
import csv
from collections import defaultdict
from decimal import Decimal, InvalidOperation
from pathlib import Path

from okx_symbol_universe import (
    is_okx_copyable,
    load_okx_usdt_swap_symbols,
    normalize_hyperliquid_symbol,
)


def dec(value: object) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0").replace(",", ""))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def main() -> int:
    parser = argparse.ArgumentParser(description="Build OKX-tradable PnL reports from Hyperliquid profiles.")
    parser.add_argument("run_dir", help="Hyperliquid profile run directory.")
    parser.add_argument("--out-dir", help="Output directory. Default: run_dir/okx_coin_report")
    parser.add_argument("--refresh-okx-symbols", action="store_true")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    out_dir = Path(args.out_dir) if args.out_dir else run_dir / "okx_coin_report"
    okx_symbols = load_okx_usdt_swap_symbols(
        str(out_dir / "okx_usdt_swap_symbols.json"),
        args.refresh_okx_symbols,
    )
    coin_stats: dict[str, dict[str, object]] = defaultdict(
        lambda: {
            "net": Decimal("0"),
            "fees": Decimal("0"),
            "positions": 0,
            "wins": 0,
            "traders": set(),
        }
    )
    trader_stats: dict[str, dict[str, object]] = defaultdict(
        lambda: {
            "okx_net": Decimal("0"),
            "positions": 0,
            "wins": 0,
            "coins": defaultdict(Decimal),
        }
    )
    positions: list[dict[str, str]] = []

    for csv_path in sorted(run_dir.glob("0x*/closed_positions.csv")):
        address = csv_path.parent.name
        with csv_path.open("r", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                source_coin = row.get("coin", "")
                if not is_okx_copyable(source_coin, okx_symbols):
                    continue
                coin = normalize_hyperliquid_symbol(source_coin)
                net = dec(row.get("net_pnl_usd"))
                row = dict(row)
                row["address"] = address
                row["source_coin"] = source_coin
                row["coin"] = coin
                positions.append(row)

                coin_stats[coin]["net"] = dec(coin_stats[coin]["net"]) + net
                coin_stats[coin]["fees"] = dec(coin_stats[coin]["fees"]) + dec(row.get("fees_usd"))
                coin_stats[coin]["positions"] = int(coin_stats[coin]["positions"]) + 1
                coin_stats[coin]["wins"] = int(coin_stats[coin]["wins"]) + (1 if net > 0 else 0)
                coin_stats[coin]["traders"].add(address)  # type: ignore[union-attr]

                trader_stats[address]["okx_net"] = dec(trader_stats[address]["okx_net"]) + net
                trader_stats[address]["positions"] = int(trader_stats[address]["positions"]) + 1
                trader_stats[address]["wins"] = int(trader_stats[address]["wins"]) + (1 if net > 0 else 0)
                trader_stats[address]["coins"][coin] += net  # type: ignore[index]

    coin_rows = []
    for coin, item in coin_stats.items():
        positions_count = int(item["positions"])
        coin_rows.append(
            {
                "coin": coin,
                "net_pnl_usd": money(dec(item["net"])),
                "fees_usd": money(dec(item["fees"])),
                "closed_positions": str(positions_count),
                "winning_positions": str(item["wins"]),
                "win_rate_pct": money(Decimal(int(item["wins"])) / Decimal(positions_count) * Decimal("100") if positions_count else Decimal("0")),
                "trader_count": str(len(item["traders"])),
            }
        )
    coin_rows.sort(key=lambda row: dec(row["net_pnl_usd"]), reverse=True)

    trader_rows = []
    for address, item in trader_stats.items():
        positions_count = int(item["positions"])
        top_coins = sorted(item["coins"].items(), key=lambda pair: pair[1], reverse=True)  # type: ignore[union-attr]
        trader_rows.append(
            {
                "address": address,
                "okx_tradable_net_pnl_usd": money(dec(item["okx_net"])),
                "closed_positions": str(positions_count),
                "winning_positions": str(item["wins"]),
                "win_rate_pct": money(Decimal(int(item["wins"])) / Decimal(positions_count) * Decimal("100") if positions_count else Decimal("0")),
                "top_coins": "; ".join(f"{coin}:{money(value)}" for coin, value in top_coins[:6]),
            }
        )
    trader_rows.sort(key=lambda row: dec(row["okx_tradable_net_pnl_usd"]), reverse=True)

    positions.sort(key=lambda row: dec(row.get("net_pnl_usd")), reverse=True)
    write_csv(out_dir / "okx_coin_summary.csv", coin_rows)
    write_csv(out_dir / "okx_trader_summary.csv", trader_rows)
    write_csv(out_dir / "okx_closed_positions.csv", positions)
    print(f"OKX-tradable coin report: {out_dir}")
    print(
        f"okx_symbols={len(okx_symbols)} coins={len(coin_rows)} "
        f"traders={len(trader_rows)} positions={len(positions)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
