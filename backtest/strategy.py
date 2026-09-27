import numpy as np
import pandas as pd

from smartmoneyconcepts.smc import smc


def bos_choch_strategy(ohlc: pd.DataFrame, swing_length: int = 5, close_break: bool = True) -> pd.Series:
    """Always-in-market long/short flip: go long/short when a BOS or CHOCH is confirmed."""
    swing = smc.swing_highs_lows(ohlc, swing_length=swing_length)
    structure = smc.bos_choch(ohlc, swing, close_break=close_break)

    signal = pd.Series(np.nan, index=ohlc.index, dtype=float)

    # smc marks BOS/CHOCH at the swing bar itself, but that bar only becomes valid
    # once price later breaks the level (BrokenIndex). Acting on the swing bar would
    # be lookahead, so the tradeable event is BrokenIndex - the bar where the break
    # actually happens in real time.
    confirmed = structure.dropna(subset=["BrokenIndex"])
    for _, row in confirmed.iterrows():
        j = int(row["BrokenIndex"])
        direction = row["BOS"] if not np.isnan(row["BOS"]) else row["CHOCH"]
        signal.iloc[j] = direction

    return signal.ffill().fillna(0)
