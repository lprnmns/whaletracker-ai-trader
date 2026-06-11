import json
import os
from datetime import datetime, timedelta, timezone

import pandas as pd
import yfinance as yf
from flask import Flask, jsonify, request
from flask_cors import CORS

APP = Flask(__name__)
CORS(APP)

DEFAULT_RATE = 0.48
DEFAULT_INITIAL = 250.0


def parse_date(value: str) -> datetime | None:
    if not value:
        return None
    try:
        if value.isdigit():
            return datetime.fromtimestamp(int(value), tz=timezone.utc)
    except ValueError:
        pass
    try:
        if value.endswith("Z"):
            return datetime.fromisoformat(value.replace("Z", "+00:00"))
        return datetime.fromisoformat(value)
    except ValueError:
        return None


def normalize_index(series: pd.Series, normalize: bool = True) -> pd.Series:
    if series.empty:
        return series
    idx = pd.to_datetime(series.index)
    if getattr(idx, "tz", None) is not None:
        idx = idx.tz_convert(None)
    if normalize:
        idx = idx.normalize()
    series.index = idx
    return series


def download_series(
    ticker: str,
    start_date: datetime,
    end_date: datetime,
    interval: str = "1d",
    clip_range: bool = True,
) -> pd.Series:
    if interval == "1d":
        data = yf.download(
            ticker,
            start=start_date.strftime("%Y-%m-%d"),
            end=(end_date + timedelta(days=1)).strftime("%Y-%m-%d"),
            interval=interval,
            progress=False,
        )
    else:
        data = yf.download(
            ticker,
            period="7d",
            interval=interval,
            progress=False,
        )
    if data.empty:
        return pd.Series(dtype=float)
    if "Close" in data.columns:
        close = data["Close"]
        if isinstance(close, pd.DataFrame):
            close = close.iloc[:, 0]
        series = normalize_index(close.dropna(), normalize=(interval == "1d"))
    elif "Adj Close" in data.columns:
        close = data["Adj Close"]
        if isinstance(close, pd.DataFrame):
            close = close.iloc[:, 0]
        series = normalize_index(close.dropna(), normalize=(interval == "1d"))
    else:
        return pd.Series(dtype=float)

    if interval != "1d" and clip_range:
        start_naive = start_date.replace(tzinfo=None)
        end_naive = end_date.replace(tzinfo=None)
        series = series.loc[(series.index >= start_naive) & (series.index <= end_naive)]
    return series


def percent_series(values: pd.Series) -> pd.Series:
    if values.empty:
        return values
    start_value = float(values.iloc[0])
    if start_value == 0:
        return pd.Series(dtype=float)
    return ((values - start_value) / start_value) * 100.0


def to_points(series: pd.Series) -> list[dict]:
    points = []
    for idx, value in series.items():
        if pd.isna(value):
            continue
        ts = idx.to_pydatetime().replace(tzinfo=timezone.utc).isoformat()
        points.append({"date": ts, "value": round(float(value), 4)})
    return points


def load_bot_series(
    start_date: datetime,
    end_date: datetime,
    timeline: pd.DatetimeIndex | None = None,
) -> list[dict]:
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    csv_path = os.path.join(base_dir, "data", "benchmarks", "okx_balance.csv")
    if not os.path.exists(csv_path):
        return []
    rows = []
    with open(csv_path, "r", encoding="utf-8") as handle:
        for line in handle:
            raw = line.strip()
            if not raw or raw.lower().startswith("date"):
                continue
            parts = raw.split(",", 1)
            if len(parts) != 2:
                continue
            try:
                ts = datetime.fromisoformat(parts[0]).replace(tzinfo=timezone.utc)
            except ValueError:
                try:
                    ts = datetime.strptime(parts[0], "%Y-%m-%d").replace(tzinfo=timezone.utc)
                except ValueError:
                    continue
            if ts < start_date or ts > end_date:
                continue
            try:
                value = float(parts[1])
            except ValueError:
                continue
            rows.append((ts, value))
    if len(rows) < 2:
        return []
    rows.sort(key=lambda item: item[0])
    start_value = rows[0][1]
    if start_value == 0:
        return []
    if timeline is None:
        points = []
        for ts, value in rows:
            pct = ((value - start_value) / start_value) * 100.0
            points.append({"date": ts.isoformat(), "value": round(pct, 4)})
        return points

    points = []
    idx = 0
    current_value = None
    for t in timeline:
        ts = t.to_pydatetime().replace(tzinfo=timezone.utc)
        while idx < len(rows) and rows[idx][0] <= ts:
            current_value = rows[idx][1]
            idx += 1
        if current_value is None:
            continue
        pct = ((current_value - start_value) / start_value) * 100.0
        points.append({"date": ts.isoformat(), "value": round(pct, 4)})
    return points


@APP.get("/api/market-comparison")
def market_comparison():
    bot_start = parse_date(request.args.get("bot_start_date", ""))
    if not bot_start:
        return jsonify({"status": "error", "message": "bot_start_date is required"}), 400

    initial_usd = float(request.args.get("initial_usd", DEFAULT_INITIAL))
    range_key = request.args.get("range", "1w")
    range_days = {"1d": 1, "1w": 7, "1m": 30, "1y": 365}
    range_minutes = {"15m": 15}

    now = datetime.now(timezone.utc)
    if range_key in range_minutes:
        min_age = timedelta(minutes=range_minutes[range_key])
        if now - bot_start < min_age:
            return jsonify(
                {
                    "status": "no_data",
                    "message": "Insufficient history for selected range.",
                    "range": range_key,
                }
            )
        start_date = max(bot_start, now - min_age)
        interval = "15m"
        intraday = True
    elif range_key in range_days:
        min_age = timedelta(days=range_days[range_key])
        if now - bot_start < min_age:
            return jsonify(
                {
                    "status": "no_data",
                    "message": "Insufficient history for selected range.",
                    "range": range_key,
                }
            )
        start_date = max(bot_start, now - min_age)
        interval = "1d"
        intraday = False
    else:
        start_date = bot_start
        interval = "1d"
        intraday = False

    end_date = now

    range_start = start_date
    range_end = end_date
    base_index = None
    if intraday:
        end_floor = end_date.replace(tzinfo=None)
        end_floor -= timedelta(
            minutes=end_floor.minute % 15,
            seconds=end_floor.second,
            microseconds=end_floor.microsecond,
        )
        start_floor = end_floor - timedelta(minutes=15)
        base_index = pd.date_range(start=start_floor, end=end_floor, freq="15min")
        range_start = base_index[0].to_pydatetime().replace(tzinfo=timezone.utc)
        range_end = base_index[-1].to_pydatetime().replace(tzinfo=timezone.utc)

    warnings: list[str] = []
    bist = pd.Series(dtype=float)
    usdtry = pd.Series(dtype=float)

    gold_raw = download_series("GC=F", range_start, range_end, interval=interval, clip_range=False)
    gold = gold_raw
    if interval != "1d":
        start_naive = range_start.replace(tzinfo=None)
        end_naive = range_end.replace(tzinfo=None)
        if not gold_raw.empty:
            gold = gold_raw.loc[(gold_raw.index >= start_naive) & (gold_raw.index <= end_naive)]
        else:
            gold_daily = download_series("GC=F", range_start, range_end, interval="1d")
            if not gold_daily.empty:
                last_value = float(gold_daily.iloc[-1])
                gold = pd.Series([last_value, last_value], index=[start_naive, end_naive])
                warnings.append("Gold intraday missing; using daily close.")
    if gold.empty and not gold_raw.empty:
        last_value = float(gold_raw.iloc[-1])
        gold = pd.Series([last_value, last_value], index=[range_start.replace(tzinfo=None), range_end.replace(tzinfo=None)])
        warnings.append("Gold market closed; using last close for 15m range.")
    if gold.empty:
        warnings.append("Gold intraday data unavailable for selected range.")

    if not intraday:
        bist = download_series("XU100.IS", start_date, end_date, interval=interval)
        usdtry = download_series("TRY=X", start_date, end_date, interval=interval)
        if usdtry.empty:
            return jsonify({"status": "no_data", "message": "USD/TRY data unavailable."})
        if bist.empty:
            warnings.append("BIST data unavailable for selected range.")
    else:
        warnings.append("BIST/FX intraday data not available; hidden for 15m range.")

    if not intraday:
        gold_try = (gold * usdtry).dropna()
        bist = bist.reindex(gold_try.index).dropna()
        usdtry = usdtry.reindex(gold_try.index).dropna()
        gold_try = gold_try.reindex(bist.index).dropna()
        base_index = gold_try.index.intersection(bist.index)
        if base_index.empty:
            return jsonify({"status": "no_data", "message": "Insufficient market data."})

    if intraday:
        if not gold.empty:
            gold_aligned = gold.reindex(base_index, method="ffill").dropna()
            gold_pct = percent_series(gold_aligned)
        else:
            gold_pct = pd.Series(dtype=float)
        start_usd = initial_usd
        step_rate = (1 + DEFAULT_RATE) ** (1 / (365 * 24 * 4)) - 1
        deposit_values = []
        for idx in base_index:
            minutes = (idx.to_pydatetime().replace(tzinfo=timezone.utc) - base_index[0].to_pydatetime().replace(tzinfo=timezone.utc)).total_seconds() / 60
            steps = max(int(minutes / 15), 0)
            value = start_usd * ((1 + step_rate) ** steps)
            deposit_values.append(value)
        deposit_series = pd.Series(deposit_values, index=base_index)
        deposit_pct = percent_series(deposit_series)
        bist_pct = pd.Series(dtype=float)
    else:
        gold_try = gold_try.reindex(base_index).dropna()
        bist = bist.reindex(base_index).dropna()
        usdtry = usdtry.reindex(base_index).dropna()

        gold_pct = percent_series(gold_try)
        bist_pct = percent_series(bist)

        start_tl = initial_usd * float(usdtry.iloc[0])
        daily_rate = (1 + DEFAULT_RATE) ** (1 / 365) - 1
        deposit_values = []
        for idx in base_index:
            days = (idx.to_pydatetime().replace(tzinfo=timezone.utc) - base_index[0].to_pydatetime().replace(tzinfo=timezone.utc)).days
            value = start_tl * ((1 + daily_rate) ** days)
            deposit_values.append(value)
        deposit_series = pd.Series(deposit_values, index=base_index)
        deposit_pct = percent_series(deposit_series)

    bot_series = load_bot_series(range_start, range_end, timeline=base_index)

    return jsonify(
        {
            "status": "ok",
            "range": range_key,
            "start_date": range_start.isoformat(),
            "end_date": range_end.isoformat(),
            "warnings": warnings,
            "series": {
                "bot": bot_series,
                "gold": to_points(gold_pct),
                "bist100": to_points(bist_pct),
                "deposit": to_points(deposit_pct),
            },
        }
    )


if __name__ == "__main__":
    APP.run(host="0.0.0.0", port=8001, debug=False)
