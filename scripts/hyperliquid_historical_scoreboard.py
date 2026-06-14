#!/usr/bin/env python3
"""Build a historical copy-quality scoreboard from Hyperliquid profile reports."""

from __future__ import annotations

import argparse
import csv
import json
from collections import defaultdict
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path

from okx_symbol_universe import (
    is_okx_copyable,
    load_okx_usdt_swap_symbols,
    normalize_hyperliquid_symbol,
)


REFERENCE_COINS = {"BTC", "ETH", "SOL"}


def dec(value: object) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0").replace(",", ""))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def number(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.0001'))}"


def pct(value: Decimal) -> str:
    return money(value * Decimal("100"))


def clamp(value: Decimal, low: Decimal = Decimal("0"), high: Decimal = Decimal("1")) -> Decimal:
    return min(high, max(low, value))


def score_return(value: Decimal, target: Decimal) -> Decimal:
    if target <= 0:
        return Decimal("0")
    if value >= 0:
        return clamp(Decimal("0.50") + Decimal("0.50") * value / target)
    return clamp(Decimal("0.50") + Decimal("0.50") * value / target)


def score_inverse(value: Decimal, good: Decimal, bad: Decimal) -> Decimal:
    if value <= good:
        return Decimal("1")
    if value >= bad:
        return Decimal("0")
    return clamp((bad - value) / (bad - good))


def read_csv(path: Path) -> list[dict[str, str]]:
    if not path.exists() or path.stat().st_size == 0:
        return []
    with path.open("r", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    fields: list[str] = []
    for row in rows:
        for key in row:
            if key not in fields:
                fields.append(key)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def parse_time(value: str) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def distinct_active_days(rows: list[dict[str, str]]) -> int:
    days = set()
    for row in rows:
        opened = parse_time(row.get("opened_at", ""))
        closed = parse_time(row.get("closed_at", ""))
        if opened:
            days.add(opened.date().isoformat())
        if closed:
            days.add(closed.date().isoformat())
    return len(days)


def average(values: list[Decimal]) -> Decimal:
    return sum(values, Decimal("0")) / Decimal(len(values)) if values else Decimal("0")


def load_positions(run_dir: Path) -> dict[str, list[dict[str, str]]]:
    positions: dict[str, list[dict[str, str]]] = {}
    for path in sorted(run_dir.glob("0x*/closed_positions.csv")):
        address = path.parent.name.lower()
        positions[address] = read_csv(path)
    return positions


def build_score(
    summary: dict[str, str],
    positions: list[dict[str, str]],
    now: datetime,
    okx_symbols: set[str] | frozenset[str],
) -> dict[str, str]:
    address = summary["address"].lower()
    account_value = dec(summary.get("account_value_usd"))
    equity_base = max(account_value, Decimal("30000"))
    active_positions = int(dec(summary.get("active_positions")))
    fill_count = int(dec(summary.get("fill_count")))
    closed_count = int(dec(summary.get("closed_position_count")))
    total_notional = dec(summary.get("total_position_notional_usd"))
    margin_used = dec(summary.get("margin_used_usd"))
    total_net_pnl = dec(summary.get("net_closed_pnl_usd"))
    okx_positions = [
        row for row in positions if is_okx_copyable(row.get("coin", ""), okx_symbols)
    ]
    reference_positions = [
        row
        for row in okx_positions
        if normalize_hyperliquid_symbol(row.get("coin", "")) in REFERENCE_COINS
    ]

    okx_net = sum((dec(row.get("net_pnl_usd")) for row in okx_positions), Decimal("0"))
    reference_net = sum((dec(row.get("net_pnl_usd")) for row in reference_positions), Decimal("0"))
    okx_wins = sum(1 for row in okx_positions if dec(row.get("net_pnl_usd")) > 0)
    okx_losses = sum(1 for row in okx_positions if dec(row.get("net_pnl_usd")) < 0)
    okx_count = len(okx_positions)
    okx_win_rate = Decimal(okx_wins) / Decimal(okx_count) if okx_count else Decimal("0")

    all_entry_notional = sum((dec(row.get("entry_notional_usd")) for row in positions), Decimal("0"))
    okx_entry_notional = sum((dec(row.get("entry_notional_usd")) for row in okx_positions), Decimal("0"))
    okx_notional_ratio = okx_entry_notional / all_entry_notional if all_entry_notional > 0 else Decimal("0")

    positive_okx = [dec(row.get("net_pnl_usd")) for row in okx_positions if dec(row.get("net_pnl_usd")) > 0]
    negative_okx = [dec(row.get("net_pnl_usd")) for row in okx_positions if dec(row.get("net_pnl_usd")) < 0]
    gross_okx_profit = sum(positive_okx, Decimal("0"))
    gross_okx_loss = abs(sum(negative_okx, Decimal("0")))
    profit_factor = gross_okx_profit / gross_okx_loss if gross_okx_loss > 0 else (Decimal("9.99") if gross_okx_profit > 0 else Decimal("0"))
    profit_factor_score = clamp(profit_factor / Decimal("2.5"))
    avg_win = average(positive_okx)
    avg_loss = abs(average(negative_okx))
    expectancy = (avg_win * okx_win_rate) - (avg_loss * (Decimal("1") - okx_win_rate))
    expectancy_score = score_return(expectancy / equity_base, Decimal("0.03"))

    max_okx_trade = max((abs(dec(row.get("net_pnl_usd"))) for row in okx_positions), default=Decimal("0"))
    one_trade_concentration = max_okx_trade / abs(okx_net) if okx_net != 0 else Decimal("0")
    concentration_quality = score_inverse(one_trade_concentration, Decimal("0.25"), Decimal("0.70"))

    max_fill_balance_pct = max((dec(row.get("max_fill_balance_pct")) for row in positions), default=Decimal("0"))
    avg_holding_hours = average([dec(row.get("holding_hours")) for row in okx_positions])
    days = max(1, int(dec(summary.get("days")) or Decimal("30")))
    fills_per_day = Decimal(fill_count) / Decimal(days)
    active_days = distinct_active_days(okx_positions)
    latest_close = max((parse_time(row.get("closed_at", "")) for row in okx_positions), default=None)
    last_fill_age_days = Decimal("999")
    if latest_close:
        last_fill_age_days = Decimal((now - latest_close).total_seconds()) / Decimal("86400")

    data_truncated = fill_count >= 9500 or int(dec(summary.get("split_ranges"))) >= 100 or int(dec(summary.get("retries"))) >= 30
    no_recent_fills = fill_count == 0
    inactive_or_withdrawn = account_value <= 0 and active_positions == 0

    notional_to_equity = total_notional / equity_base if equity_base > 0 else Decimal("0")
    margin_to_equity = margin_used / equity_base if equity_base > 0 else Decimal("0")

    okx_return = okx_net / equity_base
    target_return = Decimal("0.03") if days <= 45 else Decimal("0.10")
    profitability = Decimal("100") * (
        Decimal("0.55") * score_return(okx_return, target_return)
        + Decimal("0.30") * profit_factor_score
        + Decimal("0.15") * (Decimal("1") if okx_net > 0 else Decimal("0"))
    )

    consistency = Decimal("100") * (
        Decimal("0.45") * okx_win_rate
        + Decimal("0.25") * expectancy_score
        + Decimal("0.30") * concentration_quality
    )

    fill_density_score = Decimal("1")
    if fills_per_day > 500:
        fill_density_score = Decimal("0.15")
    elif fills_per_day > 250:
        fill_density_score = Decimal("0.45")
    elif fills_per_day < 1 and fill_count > 0:
        fill_density_score = Decimal("0.55")

    holding_score = Decimal("1")
    if avg_holding_hours and avg_holding_hours < Decimal("0.25"):
        holding_score = Decimal("0.25")
    elif avg_holding_hours and avg_holding_hours < Decimal("2"):
        holding_score = Decimal("0.65")
    elif avg_holding_hours > Decimal("336"):
        holding_score = Decimal("0.65")

    position_size_score = score_inverse(max_fill_balance_pct, Decimal("20"), Decimal("85"))
    copyability = Decimal("100") * (
        Decimal("0.45") * clamp(okx_notional_ratio)
        + Decimal("0.15") * (Decimal("1") if okx_count else Decimal("0"))
        + Decimal("0.15") * fill_density_score
        + Decimal("0.10") * holding_score
        + Decimal("0.15") * position_size_score
    )

    leverage_proxy_score = score_inverse(notional_to_equity, Decimal("4"), Decimal("12"))
    margin_score = score_inverse(margin_to_equity, Decimal("0.65"), Decimal("1.05"))
    risk_control = Decimal("100") * (
        Decimal("0.30") * Decimal("0.50")
        + Decimal("0.20") * Decimal("1")
        + Decimal("0.20") * leverage_proxy_score
        + Decimal("0.15") * margin_score
        + Decimal("0.15") * concentration_quality
    )

    recent_fill_score = score_inverse(last_fill_age_days, Decimal("1"), Decimal("14")) if fill_count else Decimal("0")
    activity_recency = Decimal("100") * (
        Decimal("0.35") * (Decimal("1") if account_value >= Decimal("30000") else Decimal("0"))
        + Decimal("0.25") * recent_fill_score
        + Decimal("0.20") * (Decimal("1") if active_positions > 0 else Decimal("0"))
        + Decimal("0.20") * clamp(Decimal(active_days) / Decimal("15"))
    )

    sizing_discipline = Decimal("100") * (
        Decimal("0.45") * position_size_score
        + Decimal("0.35") * leverage_proxy_score
        + Decimal("0.20") * margin_score
    )

    data_completeness = Decimal("0.45") if data_truncated else Decimal("1")
    sample_quality = Decimal("100") * (
        Decimal("0.40") * clamp(Decimal(closed_count) / Decimal("30"))
        + Decimal("0.25") * clamp(Decimal(okx_count) / Decimal("15"))
        + Decimal("0.20") * clamp(Decimal(active_days) / Decimal("15"))
        + Decimal("0.15") * data_completeness
    )

    horizon_robustness = Decimal("45")

    hqs = (
        Decimal("0.22") * profitability
        + Decimal("0.16") * consistency
        + Decimal("0.18") * copyability
        + Decimal("0.16") * risk_control
        + Decimal("0.10") * activity_recency
        + Decimal("0.08") * sizing_discipline
        + Decimal("0.06") * sample_quality
        + Decimal("0.04") * horizon_robustness
    )

    confidence = Decimal("100") * (
        Decimal("0.25") * data_completeness
        + Decimal("0.20") * clamp(Decimal(closed_count) / Decimal("30"))
        + Decimal("0.15") * Decimal("0.35")
        + Decimal("0.15") * Decimal("0")
        + Decimal("0.10") * Decimal("0.70")
        + Decimal("0.10") * (Decimal("1") if okx_count else Decimal("0"))
        + Decimal("0.05") * Decimal("0.50")
    )

    warnings: list[str] = []
    if inactive_or_withdrawn:
        warnings.append("inactive_or_withdrawn")
    if no_recent_fills:
        warnings.append("no_recent_fills")
    if data_truncated:
        warnings.append("data_truncated_or_dense")
        confidence = min(confidence, Decimal("50"))
    if one_trade_concentration > Decimal("0.40"):
        warnings.append("one_trade_concentration_gt_40pct")
        confidence = min(confidence, Decimal("65"))
    if fills_per_day > Decimal("500"):
        warnings.append("very_dense_fills")
    if okx_notional_ratio < Decimal("0.50"):
        warnings.append("low_okx_tradable_notional_ratio")
    if margin_to_equity > Decimal("0.85"):
        warnings.append("high_margin_usage")
    if notional_to_equity > Decimal("8"):
        warnings.append("high_notional_to_equity")
    if okx_count < 4:
        warnings.append("low_okx_tradable_sample")
    if closed_count < 8:
        warnings.append("low_closed_position_sample")

    gate_reasons: list[str] = []
    if account_value < Decimal("30000"):
        gate_reasons.append("account_lt_30k")
    if closed_count < 8:
        gate_reasons.append("closed_positions_lt_8")
    if okx_count < 4:
        gate_reasons.append("okx_tradable_positions_lt_4")
    if last_fill_age_days > Decimal("7") and active_positions == 0:
        gate_reasons.append("not_recently_active")
    if one_trade_concentration > Decimal("0.40"):
        gate_reasons.append("one_trade_concentration")
    if okx_notional_ratio < Decimal("0.50"):
        gate_reasons.append("okx_tradable_notional_ratio_lt_50pct")
    if data_truncated:
        gate_reasons.append("data_truncated")

    watchlist_eligible = not gate_reasons and hqs >= Decimal("60") and confidence >= Decimal("45")
    paper_watch_candidate = watchlist_eligible and hqs >= Decimal("70") and confidence >= Decimal("60")

    coin_pnl: dict[str, Decimal] = defaultdict(Decimal)
    for row in okx_positions:
        coin_pnl[normalize_hyperliquid_symbol(row.get("coin", ""))] += dec(row.get("net_pnl_usd"))
    top_coins = "; ".join(f"{coin}:{money(value)}" for coin, value in sorted(coin_pnl.items(), key=lambda item: item[1], reverse=True)[:6])

    return {
        "address": address,
        "historical_quality_score": number(hqs),
        "confidence_score": number(confidence),
        "watchlist_eligible": "yes" if watchlist_eligible else "no",
        "paper_watch_candidate": "yes" if paper_watch_candidate else "no",
        "live_required_before_copy": "yes",
        "verdict": "WATCHLIST" if watchlist_eligible else "REVIEW_ONLY",
        "gate_reasons": "; ".join(gate_reasons),
        "warnings": "; ".join(warnings),
        "profitability": number(profitability),
        "consistency": number(consistency),
        "copyability": number(copyability),
        "risk_control": number(risk_control),
        "activity_recency": number(activity_recency),
        "sizing_discipline": number(sizing_discipline),
        "sample_quality": number(sample_quality),
        "horizon_robustness": number(horizon_robustness),
        "account_value_usd": money(account_value),
        "active_positions": str(active_positions),
        "fill_count": str(fill_count),
        "fills_per_day": number(fills_per_day),
        "closed_positions": str(closed_count),
        "okx_tradable_closed_positions": str(okx_count),
        "okx_tradable_winning_positions": str(okx_wins),
        "okx_tradable_losing_positions": str(okx_losses),
        "okx_tradable_win_rate_pct": pct(okx_win_rate),
        "okx_tradable_net_pnl_usd": money(okx_net),
        "btc_eth_sol_net_pnl_usd": money(reference_net),
        "total_net_closed_pnl_usd": money(total_net_pnl),
        "okx_tradable_return_on_current_equity_pct": pct(okx_return),
        "okx_tradable_notional_ratio_pct": pct(okx_notional_ratio),
        "one_trade_pnl_concentration_pct": pct(one_trade_concentration),
        "profit_factor": number(profit_factor),
        "avg_okx_tradable_holding_hours": number(avg_holding_hours),
        "active_days": str(active_days),
        "last_fill_age_days": number(last_fill_age_days),
        "max_fill_balance_pct": number(max_fill_balance_pct),
        "notional_to_equity": number(notional_to_equity),
        "margin_to_equity_pct": pct(margin_to_equity),
        "data_truncated_or_dense": "yes" if data_truncated else "no",
        "okx_symbol_universe_size": str(len(okx_symbols)),
        "top_okx_tradable_coins": top_coins,
        # Temporary aliases keep older UI/API consumers readable.
        "major_closed_positions": str(okx_count),
        "major_winning_positions": str(okx_wins),
        "major_losing_positions": str(okx_losses),
        "major_win_rate_pct": pct(okx_win_rate),
        "major_net_pnl_usd": money(okx_net),
        "major_return_on_current_equity_pct": pct(okx_return),
        "major_notional_ratio_pct": pct(okx_notional_ratio),
        "avg_major_holding_hours": number(avg_holding_hours),
        "top_major_coins": top_coins,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Build a Hyperliquid historical quality scoreboard.")
    parser.add_argument("run_dir", help="Hyperliquid profile run directory.")
    parser.add_argument("--out-dir", help="Output directory. Default: run_dir/historical_scoreboard")
    parser.add_argument("--refresh-okx-symbols", action="store_true")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    out_dir = Path(args.out_dir) if args.out_dir else run_dir / "historical_scoreboard"
    okx_cache = out_dir / "okx_usdt_swap_symbols.json"
    okx_symbols = load_okx_usdt_swap_symbols(str(okx_cache), args.refresh_okx_symbols)
    summaries = read_csv(run_dir / "trader_summaries.csv")
    positions_by_address = load_positions(run_dir)
    now = datetime.now(timezone.utc)

    rows: list[dict[str, str]] = []
    for summary in summaries:
        address = summary.get("address", "").lower()
        if not address:
            continue
        rows.append(build_score(summary, positions_by_address.get(address, []), now, okx_symbols))

    rows.sort(
        key=lambda row: (
            Decimal("1") if row["watchlist_eligible"] == "yes" else Decimal("0"),
            dec(row["historical_quality_score"]),
            dec(row["confidence_score"]),
            dec(row["okx_tradable_net_pnl_usd"]),
        ),
        reverse=True,
    )
    for index, row in enumerate(rows, 1):
        row["rank"] = str(index)

    write_csv(out_dir / "historical_scoreboard.csv", rows)
    (out_dir / "historical_scoreboard.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    eligible = sum(1 for row in rows if row["watchlist_eligible"] == "yes")
    print(f"Historical scoreboard: {out_dir}")
    print(f"traders={len(rows)} watchlist_eligible={eligible} okx_symbols={len(okx_symbols)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
