from dataclasses import dataclass
from typing import List, Optional

import numpy as np
import pandas as pd


@dataclass
class Trade:
    direction: int
    entry_time: pd.Timestamp
    entry_price: float
    size: float
    stop_price: float
    take_profit_price: Optional[float] = None
    exit_time: Optional[pd.Timestamp] = None
    exit_price: Optional[float] = None
    exit_reason: str = ""

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


def compute_atr(ohlc: pd.DataFrame, period: int) -> pd.Series:
    high, low, close = ohlc["high"], ohlc["low"], ohlc["close"]
    prev_close = close.shift(1)
    true_range = pd.concat(
        [high - low, (high - prev_close).abs(), (low - prev_close).abs()], axis=1
    ).max(axis=1)
    return true_range.ewm(alpha=1.0 / period, adjust=False, min_periods=period).mean()


class BacktestEngine:
    """
    Bar-by-bar long/flat/short simulator driven by a target-position signal.

    `signals` must hold {-1, 0, 1} per bar (desired position as of that bar's close).
    The engine acts on bar i-1's signal at bar i's open, so it never trades on
    information from the bar it is currently filling.

    Position size is derived per trade from `risk_pct` of current equity divided
    by an ATR-based stop distance, not a fixed lot - this is real risk management
    (fixed $ risk per trade) instead of a stop expressed as a % of price.
    """

    def __init__(
        self,
        ohlc: pd.DataFrame,
        initial_balance: float = 10_000.0,
        risk_pct: float = 0.5,
        atr_period: int = 14,
        atr_multiplier: float = 2.0,
        reward_risk_ratio: Optional[float] = None,
    ):
        self.ohlc = ohlc
        self.initial_balance = initial_balance
        self.risk_pct = risk_pct
        self.atr_period = atr_period
        self.atr_multiplier = atr_multiplier
        self.reward_risk_ratio = reward_risk_ratio

    def run(self, signals: pd.Series) -> BacktestResult:
        ohlc = self.ohlc
        n = len(ohlc)
        atr = compute_atr(ohlc, self.atr_period)
        trades: List[Trade] = []

        position = 0
        open_trade: Optional[Trade] = None
        realized = self.initial_balance
        equity = np.empty(n, dtype=np.float64)
        equity[0] = realized

        for i in range(1, n):
            o = ohlc["open"].iloc[i]
            h = ohlc["high"].iloc[i]
            l = ohlc["low"].iloc[i]
            c = ohlc["close"].iloc[i]
            t = ohlc.index[i]
            atr_value = atr.iloc[i - 1]

            if open_trade is not None:
                hit_price, reason = self._check_stop_tp(open_trade, h, l)
                if hit_price is not None:
                    open_trade.exit_time = t
                    open_trade.exit_price = hit_price
                    open_trade.exit_reason = reason
                    realized += open_trade.pnl
                    trades.append(open_trade)
                    open_trade = None
                    position = 0

            if pd.isna(atr_value):
                # not enough history yet to size a stop - hold flat
                equity[i] = realized + (open_trade.unrealized_pnl(c) if open_trade else 0.0)
                continue

            desired = int(signals.iloc[i - 1])
            if desired != position:
                if open_trade is not None:
                    open_trade.exit_time = t
                    open_trade.exit_price = o
                    open_trade.exit_reason = "signal"
                    realized += open_trade.pnl
                    trades.append(open_trade)
                    open_trade = None
                if desired != 0:
                    stop_distance = atr_value * self.atr_multiplier
                    risk_amount = realized * self.risk_pct / 100.0
                    size = risk_amount / stop_distance
                    stop_price = o - stop_distance * desired
                    take_profit_price = (
                        o + stop_distance * self.reward_risk_ratio * desired
                        if self.reward_risk_ratio
                        else None
                    )
                    open_trade = Trade(
                        direction=desired,
                        entry_time=t,
                        entry_price=o,
                        size=size,
                        stop_price=stop_price,
                        take_profit_price=take_profit_price,
                    )
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

    def _check_stop_tp(self, trade: Trade, high: float, low: float):
        if trade.direction == 1:
            sl_hit = low <= trade.stop_price
            tp_hit = trade.take_profit_price is not None and high >= trade.take_profit_price
        else:
            sl_hit = high >= trade.stop_price
            tp_hit = trade.take_profit_price is not None and low <= trade.take_profit_price
        # both could be true within the same bar (no intrabar path is known); assume
        # the stop fills first as the conservative case.
        if sl_hit:
            return trade.stop_price, "stop_loss"
        if tp_hit:
            return trade.take_profit_price, "take_profit"
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
