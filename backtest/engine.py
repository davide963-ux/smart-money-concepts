from dataclasses import dataclass
from typing import List, Optional

import numpy as np
import pandas as pd


@dataclass
class Trade:
    direction: int
    entry_time: pd.Timestamp
    entry_price: float
    exit_time: Optional[pd.Timestamp] = None
    exit_price: Optional[float] = None
    exit_reason: str = ""
    size: float = 1.0

    def unrealized_pnl(self, price: float) -> float:
        return (price - self.entry_price) * self.direction * self.size

    @property
    def pnl(self) -> float:
        if self.exit_price is None:
            return 0.0
        return self.unrealized_pnl(self.exit_price)

    @property
    def pnl_pct(self) -> float:
        if self.exit_price is None or self.entry_price == 0:
            return 0.0
        return (self.exit_price - self.entry_price) / self.entry_price * self.direction * 100.0


@dataclass
class BacktestResult:
    trades: List[Trade]
    equity_curve: pd.Series
    stats: dict


class BacktestEngine:
    """
    Bar-by-bar long/flat/short simulator driven by a target-position signal.

    `signals` must hold {-1, 0, 1} per bar (desired position as of that bar's close).
    The engine acts on bar i-1's signal at bar i's open, so it never trades on
    information from the bar it is currently filling.
    """

    def __init__(
        self,
        ohlc: pd.DataFrame,
        initial_balance: float = 10_000.0,
        position_size: float = 1.0,
        stop_loss_pct: Optional[float] = None,
        take_profit_pct: Optional[float] = None,
    ):
        self.ohlc = ohlc
        self.initial_balance = initial_balance
        self.position_size = position_size
        self.stop_loss_pct = stop_loss_pct
        self.take_profit_pct = take_profit_pct

    def run(self, signals: pd.Series) -> BacktestResult:
        ohlc = self.ohlc
        n = len(ohlc)
        trades: List[Trade] = []

        position = 0
        open_trade: Optional[Trade] = None
        realized = self.initial_balance
        equity = np.empty(n, dtype=np.float64)
        equity[0] = realized

        use_sl_tp = bool(self.stop_loss_pct or self.take_profit_pct)

        for i in range(1, n):
            desired = int(signals.iloc[i - 1])
            o = ohlc["open"].iloc[i]
            h = ohlc["high"].iloc[i]
            l = ohlc["low"].iloc[i]
            c = ohlc["close"].iloc[i]
            t = ohlc.index[i]

            if open_trade is not None and use_sl_tp:
                hit_price, reason = self._check_sl_tp(open_trade, h, l)
                if hit_price is not None:
                    open_trade.exit_time = t
                    open_trade.exit_price = hit_price
                    open_trade.exit_reason = reason
                    realized += open_trade.pnl
                    trades.append(open_trade)
                    open_trade = None
                    position = 0

            if desired != position:
                if open_trade is not None:
                    open_trade.exit_time = t
                    open_trade.exit_price = o
                    open_trade.exit_reason = "signal"
                    realized += open_trade.pnl
                    trades.append(open_trade)
                    open_trade = None
                if desired != 0:
                    open_trade = Trade(direction=desired, entry_time=t, entry_price=o, size=self.position_size)
                position = desired

            equity[i] = realized + (open_trade.unrealized_pnl(c) if open_trade else 0.0)

        if open_trade is not None:
            open_trade.exit_time = ohlc.index[-1]
            open_trade.exit_price = ohlc["close"].iloc[-1]
            open_trade.exit_reason = "end_of_data"
            realized += open_trade.pnl
            trades.append(open_trade)
            equity[-1] = realized

        equity_curve = pd.Series(equity, index=ohlc.index, name="equity")
        stats = self._compute_stats(trades, equity_curve)
        return BacktestResult(trades=trades, equity_curve=equity_curve, stats=stats)

    def _sl_tp_levels(self, trade: Trade):
        sl = tp = None
        if self.stop_loss_pct:
            sl = trade.entry_price * (1 - self.stop_loss_pct / 100.0 * trade.direction)
        if self.take_profit_pct:
            tp = trade.entry_price * (1 + self.take_profit_pct / 100.0 * trade.direction)
        return sl, tp

    def _check_sl_tp(self, trade: Trade, high: float, low: float):
        sl, tp = self._sl_tp_levels(trade)
        if trade.direction == 1:
            sl_hit = sl is not None and low <= sl
            tp_hit = tp is not None and high >= tp
        else:
            sl_hit = sl is not None and high >= sl
            tp_hit = tp is not None and low <= tp
        # both could be true within the same bar (no intrabar path is known); assume
        # the stop fills first as the conservative case.
        if sl_hit:
            return sl, "stop_loss"
        if tp_hit:
            return tp, "take_profit"
        return None, ""

    def _compute_stats(self, trades: List[Trade], equity_curve: pd.Series) -> dict:
        n_trades = len(trades)
        pnls = np.array([t.pnl for t in trades], dtype=np.float64)
        wins = pnls[pnls > 0]
        losses = pnls[pnls < 0]
        gross_profit = wins.sum() if wins.size else 0.0
        gross_loss = -losses.sum() if losses.size else 0.0

        running_max = equity_curve.cummax()
        drawdown = (equity_curve - running_max) / running_max
        final_equity = equity_curve.iloc[-1]

        if gross_loss > 0:
            profit_factor = gross_profit / gross_loss
        else:
            profit_factor = float("inf") if gross_profit > 0 else 0.0

        return {
            "num_trades": n_trades,
            "win_rate_pct": (wins.size / n_trades * 100.0) if n_trades else 0.0,
            "total_return_pct": (final_equity - self.initial_balance) / self.initial_balance * 100.0,
            "final_equity": final_equity,
            "profit_factor": profit_factor,
            "max_drawdown_pct": drawdown.min() * 100.0,
            "avg_win": wins.mean() if wins.size else 0.0,
            "avg_loss": losses.mean() if losses.size else 0.0,
        }
