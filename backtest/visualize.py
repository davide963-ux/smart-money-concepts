import argparse
import os

import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots

from smartmoneyconcepts.smc import smc

from .data import load_ohlc_csv
from .engine import BacktestEngine
from .strategy import bos_choch_strategy

DEFAULT_CSV = os.path.join(
    os.path.dirname(__file__), "..", "tests", "test_data", "EURUSD", "EURUSD_15M.csv"
)


def _add_swing_highs_lows(fig, ohlc, swing):
    idx = np.where(~np.isnan(swing["HighLow"].values))[0]
    for a, b in zip(idx[:-1], idx[1:]):
        color = "rgba(239, 83, 80, 0.5)" if swing["HighLow"].iloc[a] == 1 else "rgba(38, 166, 154, 0.5)"
        fig.add_trace(
            go.Scatter(
                x=[ohlc.index[a], ohlc.index[b]],
                y=[swing["Level"].iloc[a], swing["Level"].iloc[b]],
                mode="lines",
                line=dict(color=color, width=1),
                showlegend=False,
            ),
            row=1,
            col=1,
        )


def _add_bos_choch(fig, ohlc, structure):
    for i in range(len(structure)):
        broken = structure["BrokenIndex"].iloc[i]
        if np.isnan(broken):
            continue
        j = int(broken)
        is_bos = not np.isnan(structure["BOS"].iloc[i])
        label = "BOS" if is_bos else "CHOCH"
        direction = structure["BOS"].iloc[i] if is_bos else structure["CHOCH"].iloc[i]
        color = "rgba(255, 165, 0, 0.7)" if is_bos else "rgba(66, 165, 245, 0.7)"
        level = structure["Level"].iloc[i]
        fig.add_trace(
            go.Scatter(
                x=[ohlc.index[i], ohlc.index[j]],
                y=[level, level],
                mode="lines+text",
                line=dict(color=color, width=1, dash="dot"),
                text=["", label],
                textposition="top center" if direction == 1 else "bottom center",
                textfont=dict(color=color, size=9),
                showlegend=False,
            ),
            row=1,
            col=1,
        )


def _add_trades(fig, trades):
    longs = [t for t in trades if t.direction == 1]
    shorts = [t for t in trades if t.direction == -1]
    wins = [t for t in trades if t.exit_price is not None and t.pnl > 0]
    losses = [t for t in trades if t.exit_price is not None and t.pnl <= 0]

    markers = [
        (longs, "entry_price", "triangle-up", "#26a69a", "Long entry"),
        (shorts, "entry_price", "triangle-down", "#ef5350", "Short entry"),
        (wins, "exit_price", "circle", "#26a69a", "Win exit"),
        (losses, "exit_price", "circle", "#ef5350", "Loss exit"),
    ]
    for group, price_attr, symbol, color, name in markers:
        if not group:
            continue
        time_attr = "entry_time" if price_attr == "entry_price" else "exit_time"
        fig.add_trace(
            go.Scatter(
                x=[getattr(t, time_attr) for t in group],
                y=[getattr(t, price_attr) for t in group],
                mode="markers",
                marker=dict(symbol=symbol, color=color, size=10, line=dict(width=1, color="white")),
                name=name,
            ),
            row=1,
            col=1,
        )


def build_chart(ohlc, swing_length, trades, equity_curve):
    # recomputed on just the displayed window for the overlay, so it can differ
    # slightly near the edges from the full-series signal the backtest actually traded on
    swing = smc.swing_highs_lows(ohlc, swing_length=swing_length)
    structure = smc.bos_choch(ohlc, swing)

    fig = make_subplots(
        rows=2,
        cols=1,
        shared_xaxes=True,
        row_heights=[0.72, 0.28],
        vertical_spacing=0.04,
        subplot_titles=("Price, structure & trades", "Equity curve"),
    )
    fig.add_trace(
        go.Candlestick(
            x=ohlc.index,
            open=ohlc["open"],
            high=ohlc["high"],
            low=ohlc["low"],
            close=ohlc["close"],
            increasing_line_color="#26a69a",
            decreasing_line_color="#ef5350",
            name="Price",
        ),
        row=1,
        col=1,
    )

    _add_swing_highs_lows(fig, ohlc, swing)
    _add_bos_choch(fig, ohlc, structure)
    _add_trades(fig, trades)

    fig.add_trace(
        go.Scatter(x=equity_curve.index, y=equity_curve.values, mode="lines", line=dict(color="#42a5f5"), name="Equity"),
        row=2,
        col=1,
    )

    fig.update_layout(
        template="plotly_dark",
        height=850,
        xaxis_rangeslider_visible=False,
        legend=dict(orientation="h", y=1.02),
    )
    return fig


def main():
    parser = argparse.ArgumentParser(
        description="Render an interactive HTML chart of the backtest: candles, structure, trade markers, equity curve"
    )
    parser.add_argument("--csv", default=DEFAULT_CSV)
    parser.add_argument("--swing-length", type=int, default=5)
    parser.add_argument("--initial-balance", type=float, default=10_000.0)
    parser.add_argument("--risk-pct", type=float, default=0.5, help="Equity %% risked per trade")
    parser.add_argument("--atr-period", type=int, default=14)
    parser.add_argument("--atr-multiplier", type=float, default=2.0, help="Stop distance = ATR * this")
    parser.add_argument("--reward-risk-ratio", type=float, default=None, help="Take profit = stop distance * this (unset = no TP, rely on signal flip)")
    parser.add_argument(
        "--bars", type=int, default=300, help="How many of the most recent bars to render (the backtest itself still runs on all data)"
    )
    parser.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "chart.html"))
    args = parser.parse_args()

    ohlc = load_ohlc_csv(args.csv)
    signal = bos_choch_strategy(ohlc, swing_length=args.swing_length)
    engine = BacktestEngine(
        ohlc,
        initial_balance=args.initial_balance,
        risk_pct=args.risk_pct,
        atr_period=args.atr_period,
        atr_multiplier=args.atr_multiplier,
        reward_risk_ratio=args.reward_risk_ratio,
    )
    result = engine.run(signal)

    window = ohlc.iloc[-args.bars :]
    window_start = window.index[0]
    window_trades = [t for t in result.trades if t.entry_time >= window_start]
    window_equity = result.equity_curve.loc[window.index]

    fig = build_chart(window, args.swing_length, window_trades, window_equity)
    fig.write_html(args.out)

    print(
        f"Chart saved to {args.out}  "
        f"({len(window)} bars shown, {len(window_trades)} trades in view, {result.stats['num_trades']} trades total)"
    )


if __name__ == "__main__":
    main()
