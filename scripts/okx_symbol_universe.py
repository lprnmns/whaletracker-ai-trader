#!/usr/bin/env python3
"""Load the live OKX USDT perpetual symbol universe."""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


OKX_INSTRUMENTS_URL = "https://www.okx.com/api/v5/public/instruments?instType=SWAP"
HYPERLIQUID_SYMBOL_ALIASES = {
    "kBONK": "BONK",
    "kFLOKI": "FLOKI",
    "kNEIRO": "NEIRO",
    "kPEPE": "PEPE",
    "kSHIB": "SHIB",
}


def normalize_hyperliquid_symbol(symbol: str) -> str:
    raw = str(symbol or "").strip()
    if not raw:
        return ""
    return HYPERLIQUID_SYMBOL_ALIASES.get(raw, raw.upper())


def _parse_symbols(payload: object) -> set[str]:
    if not isinstance(payload, dict):
        return set()
    rows = payload.get("data")
    if not isinstance(rows, list):
        return set()

    symbols: set[str] = set()
    for row in rows:
        if not isinstance(row, dict):
            continue
        if str(row.get("instType") or "").upper() != "SWAP":
            continue
        if str(row.get("settleCcy") or "").upper() != "USDT":
            continue
        if str(row.get("state") or "").lower() != "live":
            continue
        instrument_id = str(row.get("instId") or "")
        base = instrument_id.split("-", 1)[0].strip().upper()
        if base:
            symbols.add(base)
    return symbols


def _read_cache(path: Path) -> set[str]:
    if not path.exists():
        return set()
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return set()

    if isinstance(payload, list):
        return {str(value).strip().upper() for value in payload if str(value).strip()}
    if isinstance(payload, dict):
        values = payload.get("symbols")
        if isinstance(values, list):
            return {str(value).strip().upper() for value in values if str(value).strip()}
    return set()


def _write_cache(path: Path, symbols: set[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps({"source": OKX_INSTRUMENTS_URL, "symbols": sorted(symbols)}, indent=2),
        encoding="utf-8",
    )


@lru_cache(maxsize=8)
def load_okx_usdt_swap_symbols(cache_path: str = "", force_refresh: bool = False) -> frozenset[str]:
    cache = Path(cache_path) if cache_path else None
    if cache and not force_refresh:
        cached = _read_cache(cache)
        if cached:
            return frozenset(cached)

    try:
        request = Request(OKX_INSTRUMENTS_URL, headers={"User-Agent": "WhaleTracker/1.0"})
        with urlopen(request, timeout=20) as response:
            payload = json.loads(response.read().decode("utf-8"))
        symbols = _parse_symbols(payload)
        if not symbols:
            raise RuntimeError("OKX returned no live USDT perpetual instruments.")
        if cache:
            _write_cache(cache, symbols)
        return frozenset(symbols)
    except (HTTPError, URLError, TimeoutError, OSError, json.JSONDecodeError, RuntimeError):
        if cache:
            cached = _read_cache(cache)
            if cached:
                return frozenset(cached)
        raise


def is_okx_copyable(symbol: str, okx_symbols: set[str] | frozenset[str]) -> bool:
    return normalize_hyperliquid_symbol(symbol) in okx_symbols
