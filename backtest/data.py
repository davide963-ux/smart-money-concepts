import pandas as pd


def load_ohlc_csv(path: str, date_column: str = "Date") -> pd.DataFrame:
    """Load a CSV with open/high/low/close(/volume) columns plus a date column into a DatetimeIndex DataFrame."""
    ohlc = pd.read_csv(path)
    ohlc = ohlc.set_index(date_column)
    ohlc.index = pd.to_datetime(ohlc.index)
    ohlc = ohlc.rename(columns={c: c.lower() for c in ohlc.columns})
    return ohlc
