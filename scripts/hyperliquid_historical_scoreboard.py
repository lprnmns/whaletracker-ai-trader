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


MAJORS = {
    "BTC",
    "ETH",
    "SOL",
    "HYPE",
    "AVAX",
    "LINK",
    "WLD",
    "SUI",
    "AAVE",
    "UNI",
    "TIA",
    "PENDLE",
    "OP",
    "ARB",
    "XRP",
    "DOGE",
    "BNB",
}
TIER1 = {"BTC", "ETH", "SOL", "HYPE"}
PORTABLE_MAJORS = {"BTC", "ETH", "SOL"}


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


def build_score(summary: dict[str, str], positions: list[dict[str, str]], now: datetime) -> dict[str, str]:
    address = summary["address"].lower()
    account_value = dec(summary.get("account_value_usd"))
    equity_base = max(account_value, Decimal("30000"))
    active_positions = int(dec(summary.get("active_positions")))
    fill_count = int(dec(summary.get("fill_count")))
    closed_count = int(dec(summary.get("closed_position_count")))
    total_notional = dec(summary.get("total_position_notional_usd"))
    margin_used = dec(summary.get("margin_used_usd"))
    total_net_pnl = dec(summary.get("net_closed_pnl_usd"))
    copyable_notional_ratio = dec(summary.get("copyable_major_ratio_pct")) / Decimal("100")

    major_positions = [row for row in positions if row.get("coin") in MAJORS]
    portable_positions = [row for row in major_positions if row.get("coin") in PORTABLE_MAJORS]
    tier1_positions = [row for row in major_positions if row.get("coin") in TIER1]

    major_net = sum((dec(row.get("net_pnl_usd")) for row in major_positions), Decimal("0"))
    portable_net = sum((dec(row.get("net_pnl_usd")) for row in portable_positions), Decimal("0"))
    hype_net = sum((dec(row.get("net_pnl_usd")) for row in major_positions if row.get("coin") == "HYPE"), Decimal("0"))
    major_wins = sum(1 for row in major_positions if dec(row.get("net_pnl_usd")) > 0)
    major_losses = sum(1 for row in major_positions if dec(row.get("net_pnl_usd")) < 0)
    major_count = len(major_positions)
    portable_count = len(portable_positions)
    tier1_count = len(tier1_positions)
    major_win_rate = Decimal(major_wins) / Decimal(major_count) if major_count else Decimal("0")

    all_entry_notional = sum((dec(row.get("entry_notional_usd")) for row in positions), Decimal("0"))
    major_entry_notional = sum((dec(row.get("entry_notional_usd")) for row in major_positions), Decimal("0"))
    portable_entry_notional = sum((dec(row.get("entry_notional_usd")) for row in portable_positions), Decimal("0"))
    major_notional_ratio = major_entry_notional / all_entry_notional if all_entry_notional > 0 else Decimal("0")
    portable_notional_ratio = portable_entry_notional / major_entry_notional if major_entry_notional > 0 else Decimal("0")

    positive_major = [dec(row.get("net_pnl_usd")) for row in major_positions if dec(row.get("net_pnl_usd")) > 0]
    negative_major = [dec(row.get("net_pnl_usd")) for row in major_positions if dec(row.get("net_pnl_usd")) < 0]
    gross_major_profit = sum(positive_major, Decimal("0"))
    gross_major_loss = abs(sum(negative_major, Decimal("0")))
    profit_factor = gross_major_profit / gross_major_loss if gross_major_loss > 0 else (Decimal("9.99") if gross_major_profit > 0 else Decimal("0"))
    profit_factor_score = clamp(profit_factor / Decimal("2.5"))
    avg_win = average(positive_major)
    avg_loss = abs(average(negative_major))
    expectancy = (avg_win * major_win_rate) - (avg_loss * (Decimal("1") - major_win_rate))
    expectancy_score = score_return(expectancy / equity_base, Decimal("0.03"))

    max_major_trade = max((abs(dec(row.get("net_pnl_usd"))) for row in major_positions), default=Decimal("0"))
    one_trade_concentration = max_major_trade / abs(major_net) if major_net != 0 else Decimal("0")
    concentration_quality = score_inverse(one_trade_concentration, Decimal("0.25"), Decimal("0.70"))

    max_fill_balance_pct = max((dec(row.get("max_fill_balance_pct")) for row in positions), default=Decimal("0"))
    avg_holding_hours = average([dec(row.get("holding_hours")) for row in major_positions])
    fills_per_day = Decimal(fill_count) / Decimal(max(1, int(dec(summary.get("days")) or Decimal("30"))))
    active_days = distinct_active_days(positions)
    latest_close = max((parse_time(row.get("closed_at", "")) for row in positions), default=None)
    last_fill_age_days = Decimal("999")
    if latest_close:
        last_fill_age_days = Decimal((now - latest_close).total_seconds()) / Decimal("86400")

    data_truncated = fill_count >= 9500 or int(dec(summary.get("split_ranges"))) >= 100 or int(dec(summary.get("retries"))) >= 30
    no_recent_fills = fill_count == 0
    inactive_or_withdrawn = account_value <= 0 and active_positions == 0

    notional_to_equity = total_notional / equity_base if equity_base > 0 else Decimal("0")
    margin_to_equity = margin_used / equity_base if equity_base > 0 else Decimal("0")

    major_return = major_net / equity_base
    portable_return = portable_net / equity_base
    profitability = Decimal("100") * (
        Decimal("0.45") * score_return(major_return, Decimal("0.20"))
        + Decimal("0.25") * score_return(portable_return, Decimal("0.12"))
        + Decimal("0.20") * profit_factor_score
        + Decimal("0.10") * (Decimal("1") if major_net > 0 else Decimal("0"))
    )

    consistency = Decimal("100") * (
        Decimal("0.45") * major_win_rate
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
        Decimal("0.25") * clamp(major_notional_ratio)
        + Decimal("0.20") * clamp(portable_notional_ratio)
        + Decimal("0.15") * (Decimal("1") if major_count else Decimal("0"))
        + Decimal("0.15") * fill_density_score
        + Decimal("0.10") * holding_score
        + Decimal("0.10") * position_size_score
        + Decimal("0.05") * clamp(copyable_notional_ratio)
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
        + Decimal("0.25") * clamp(Decimal(major_count) / Decimal("15"))
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
        + Decimal("0.10") * (Decimal("1") if major_count else Decimal("0"))
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
    if major_notional_ratio < Decimal("0.50"):
        warnings.append("low_major_notional_ratio")
    if margin_to_equity > Decimal("0.85"):
        warnings.append("high_margin_usage")
    if notional_to_equity > Decimal("8"):
        warnings.append("high_notional_to_equity")
    if major_count < 4:
        warnings.append("low_major_sample")
    if closed_count < 8:
        warnings.append("low_closed_position_sample")

    gate_reasons: list[str] = []
    if account_value < Decimal("30000"):
        gate_reasons.append("account_lt_30k")
    if closed_count < 8:
        gate_reasons.append("closed_positions_lt_8")
    if major_count < 4:
        gate_reasons.append("major_positions_lt_4")
    if last_fill_age_days > Decimal("7") and active_positions == 0:
        gate_reasons.append("not_recently_active")
    if one_trade_concentration > Decimal("0.40"):
        gate_reasons.append("one_trade_concentration")
    if major_notional_ratio < Decimal("0.50"):
        gate_reasons.append("major_notional_ratio_lt_50pct")
    if data_truncated:
        gate_reasons.append("data_truncated")

    watchlist_eligible = not gate_reasons and hqs >= Decimal("60") and confidence >= Decimal("45")
    paper_watch_candidate = watchlist_eligible and hqs >= Decimal("70") and confidence >= Decimal("60")

    coin_pnl: dict[str, Decimal] = defaultdict(Decimal)
    for row in major_positions:
        coin_pnl[row.get("coin", "")] += dec(row.get("net_pnl_usd"))
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
        "major_closed_positions": str(major_count),
        "major_winning_positions": str(major_wins),
        "major_losing_positions": str(major_losses),
        "major_win_rate_pct": pct(major_win_rate),
        "major_net_pnl_usd": money(major_net),
        "portable_btc_eth_sol_net_pnl_usd": money(portable_net),
        "hype_net_pnl_usd": money(hype_net),
        "total_net_closed_pnl_usd": money(total_net_pnl),
        "major_return_on_current_equity_pct": pct(major_return),
        "portable_return_on_current_equity_pct": pct(portable_return),
        "major_notional_ratio_pct": pct(major_notional_ratio),
        "portable_notional_ratio_pct": pct(portable_notional_ratio),
        "one_trade_pnl_concentration_pct": pct(one_trade_concentration),
        "profit_factor": number(profit_factor),
        "avg_major_holding_hours": number(avg_holding_hours),
        "active_days": str(active_days),
        "last_fill_age_days": number(last_fill_age_days),
        "max_fill_balance_pct": number(max_fill_balance_pct),
        "notional_to_equity": number(notional_to_equity),
        "margin_to_equity_pct": pct(margin_to_equity),
        "data_truncated_or_dense": "yes" if data_truncated else "no",
        "tier1_closed_positions": str(tier1_count),
        "portable_closed_positions": str(portable_count),
        "top_major_coins": top_coins,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Build a Hyperliquid historical quality scoreboard.")
    parser.add_argument("run_dir", help="Hyperliquid profile run directory.")
    parser.add_argument("--out-dir", help="Output directory. Default: run_dir/historical_scoreboard")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    out_dir = Path(args.out_dir) if args.out_dir else run_dir / "historical_scoreboard"
    summaries = read_csv(run_dir / "trader_summaries.csv")
    positions_by_address = load_positions(run_dir)
    now = datetime.now(timezone.utc)

    rows: list[dict[str, str]] = []
    for summary in summaries:
        address = summary.get("address", "").lower()
        if not address:
            continue
        rows.append(build_score(summary, positions_by_address.get(address, []), now))

    rows.sort(
        key=lambda row: (
            Decimal("1") if row["watchlist_eligible"] == "yes" else Decimal("0"),
            dec(row["historical_quality_score"]),
            dec(row["confidence_score"]),
            dec(row["major_net_pnl_usd"]),
        ),
        reverse=True,
    )
    for index, row in enumerate(rows, 1):
        row["rank"] = str(index)

    write_csv(out_dir / "historical_scoreboard.csv", rows)
    (out_dir / "historical_scoreboard.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    eligible = sum(1 for row in rows if row["watchlist_eligible"] == "yes")
    print(f"Historical scoreboard: {out_dir}")
    print(f"traders={len(rows)} watchlist_eligible={eligible}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
