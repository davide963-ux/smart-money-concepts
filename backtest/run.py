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
    parser.add_argument("--position-size", type=float, default=1.0)
    parser.add_argument("--stop-loss-pct", type=float, default=None)
    parser.add_argument("--take-profit-pct", type=float, default=None)
    parser.add_argument("--plot", action="store_true", help="Save an equity curve PNG (requires matplotlib)")
    args = parser.parse_args()

    ohlc = load_ohlc_csv(args.csv)
    signal = bos_choch_strategy(ohlc, swing_length=args.swing_length)

    engine = BacktestEngine(
        ohlc,
        initial_balance=args.initial_balance,
        position_size=args.position_size,
        stop_loss_pct=args.stop_loss_pct,
        take_profit_pct=args.take_profit_pct,
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
