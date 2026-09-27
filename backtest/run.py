import argparse
import os

from .data import load_ohlc_csv
from .engine import BacktestEngine
from .strategy import bos_choch_strategy

DEFAULT_CSV = os.path.join(
    os.path.dirname(__file__), "..", "tests", "test_data", "EURUSD", "EURUSD_15M.csv"
)


def main():
    parser = argparse.ArgumentParser(description="Basic bar-by-bar backtest harness for smc.py signals")
    parser.add_argument("--csv", default=DEFAULT_CSV, help="OHLCV CSV with a Date column")
    parser.add_argument("--swing-length", type=int, default=5)
    parser.add_argument("--initial-balance", type=float, default=10_000.0)
    parser.add_argument("--risk-pct", type=float, default=0.5, help="Equity %% risked per trade")
    parser.add_argument("--atr-period", type=int, default=14)
    parser.add_argument("--atr-multiplier", type=float, default=2.0, help="Stop distance = ATR * this")
    parser.add_argument("--reward-risk-ratio", type=float, default=None, help="Take profit = stop distance * this (unset = no TP, rely on signal flip)")
    parser.add_argument("--plot", action="store_true", help="Save an equity curve PNG (requires matplotlib)")
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

    print(f"Bars: {len(ohlc)}   Trades: {result.stats['num_trades']}")
    for key, value in result.stats.items():
        if key == "num_trades":
            continue
        print(f"{key:>18}: {value:,.2f}")

    if args.plot:
        try:
            import matplotlib.pyplot as plt
        except ImportError:
            print("matplotlib not installed; skipping plot (pip install matplotlib)")
        else:
            result.equity_curve.plot(title="Equity Curve")
            out_path = os.path.join(os.path.dirname(__file__), "equity_curve.png")
            plt.savefig(out_path)
            print(f"Saved equity curve to {out_path}")


if __name__ == "__main__":
    main()
