using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Ports the same always-in-market BOS/CHOCH flip used by
    // backtest/strategy.py's bos_choch_strategy(), driven by the same
    // swing_highs_lows + bos_choch pattern logic as smartmoneyconcepts/smc.py.
    // Position sizing and stop distance mirror backtest/engine.py: risk a fixed
    // % of account balance per trade, stop distance = ATR * multiplier - not an
    // arbitrary % of price.
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SmcBosChochBot : Robot
    {
        [Parameter("Swing Length", DefaultValue = 5, MinValue = 2, Group = "Structure")]
        public int SwingLength { get; set; }

        [Parameter("Close Break", DefaultValue = true, Group = "Structure")]
        public bool CloseBreak { get; set; }

        [Parameter("Risk % per trade", DefaultValue = 0.5, MinValue = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Multiplier (stop distance)", DefaultValue = 2.0, MinValue = 0.1, Group = "Risk")]
        public double AtrMultiplier { get; set; }

        [Parameter("Reward:Risk ratio (0 = no TP, rely on flip)", DefaultValue = 3.0, MinValue = 0, Group = "Risk")]
        public double RewardRiskRatio { get; set; }

        [Parameter("Breakeven Trigger ($ profit, 0 = off)", DefaultValue = 500, MinValue = 0, Group = "Risk")]
        public double BreakEvenTriggerUsd { get; set; }

        [Parameter("Trailing Lock % of peak profit (0 = off)", DefaultValue = 10.0, MinValue = 0, Group = "Risk")]
        public double TrailingLockPercent { get; set; }

        [Parameter("Giveback Min Profit ($, 0 = off)", DefaultValue = 2000, MinValue = 0, Group = "Risk")]
        public double GivebackMinProfitUsd { get; set; }

        [Parameter("Giveback Max %", DefaultValue = 40.0, MinValue = 0, MaxValue = 100, Group = "Risk")]
        public double GivebackMaxPercent { get; set; }

        [Parameter("Max Hold (days, 0 = off)", DefaultValue = 5, MinValue = 0, Group = "Risk")]
        public int MaxHoldDays { get; set; }

        [Parameter("Long Only", DefaultValue = false, Group = "Structure")]
        public bool LongOnly { get; set; }

        [Parameter("Use Trend Filter", DefaultValue = false, Group = "Trend Filter")]
        public bool UseTrendFilter { get; set; }

        [Parameter("Trend Timeframe", DefaultValue = "Daily", Group = "Trend Filter")]
        public TimeFrame TrendTimeFrame { get; set; }

        [Parameter("Trend EMA Period", DefaultValue = 50, MinValue = 2, Group = "Trend Filter")]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Use ATR Volatility Filter", DefaultValue = true, Group = "Chop/Volatility Filter")]
        public bool UseAtrFilter { get; set; }

        [Parameter("ATR Baseline Period", DefaultValue = 50, MinValue = 5, Group = "Chop/Volatility Filter")]
        public int AtrBaselinePeriod { get; set; }

        [Parameter("Min ATR Ratio vs baseline (0 = off)", DefaultValue = 0.5, MinValue = 0, Group = "Chop/Volatility Filter")]
        public double MinAtrRatio { get; set; }

        [Parameter("Max ATR Ratio vs baseline (0 = off)", DefaultValue = 2.5, MinValue = 0, Group = "Chop/Volatility Filter")]
        public double MaxAtrRatio { get; set; }

        [Parameter("Use Chop/Trend-Structure Filter", DefaultValue = true, Group = "Chop/Volatility Filter")]
        public bool UseChopFilter { get; set; }

        [Parameter("Chop Filter Swing Count", DefaultValue = 3, MinValue = 2, Group = "Chop/Volatility Filter")]
        public int ChopFilterSwingCount { get; set; }

        private const string BotLabel = "SmcBosChoch";
        private AverageTrueRange _atr;
        private MovingAverage _atrBaseline;
        private Bars _trendBars;
        private MovingAverage _trendEma;
        private double _peakProfit;
        private double _lockedProfitUsd = double.NegativeInfinity; // highest profit level the SL currently guarantees

        private enum SwingType { High = 1, Low = -1 }

        private class SwingPoint
        {
            public int Index;
            public SwingType Type;
            public double Level;
        }

        private class PendingStructure
        {
            public int Direction; // 1 = bullish, -1 = bearish
            public double Level;
        }

        private readonly List<SwingPoint> _swings = new List<SwingPoint>();
        private PendingStructure _pending;
        private int _desiredDirection; // -1, 0, 1
        private Position _currentPosition; // the exact position this bot opened, or null

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            if (UseAtrFilter)
                _atrBaseline = Indicators.MovingAverage(_atr.Result, AtrBaselinePeriod, MovingAverageType.Simple);
            if (UseTrendFilter)
            {
                _trendBars = MarketData.GetBars(TrendTimeFrame);
                _trendEma = Indicators.MovingAverage(_trendBars.ClosePrices, TrendEmaPeriod, MovingAverageType.Exponential);
            }
            Bars.BarOpened += OnBarOpened;
            Positions.Closed += OnPositionClosed;
        }

        // Higher-timeframe EMA trend filter: only allows entries in the
        // direction price is trading relative to the EMA on TrendTimeFrame.
        // Uses the last fully closed higher-TF bar (Count-2), not the still-
        // forming one, to avoid checking against a value that can still change.
        private bool MatchesTrend(TradeType type)
        {
            if (!UseTrendFilter) return true;
            int idx = _trendBars.Count - 2;
            if (idx < 0) return true;
            double ma = _trendEma.Result[idx];
            if (double.IsNaN(ma)) return true; // not enough higher-TF history yet
            double price = _trendBars.ClosePrices[idx];
            return type == TradeType.Buy ? price > ma : price < ma;
        }

        // Ratio of current ATR to its own AtrBaselinePeriod moving average, not an
        // absolute price-scale threshold - the same ratio band works on XAUUSD
        // (ATR in whole dollars) and EURUSD (ATR in pips) without retuning.
        // Log analysis of the long-only backtest showed the two worst losing
        // streaks sat at the ATR extremes: a flat-chop streak at ~0.4x baseline
        // and a volatility-shock streak at ~3x baseline.
        private bool AtrFilterAgrees(double atrValue, int barIndex)
        {
            if (!UseAtrFilter) return true;
            double baseline = _atrBaseline.Result[barIndex];
            if (double.IsNaN(baseline) || baseline <= 0) return true; // not enough history yet

            double ratio = atrValue / baseline;
            if (MinAtrRatio > 0 && ratio < MinAtrRatio) return false;
            if (MaxAtrRatio > 0 && ratio > MaxAtrRatio) return false;
            return true;
        }

        // Requires the last ChopFilterSwingCount swing highs AND swing lows to be
        // strictly monotonic in the trade's direction (higher highs + higher lows
        // for a buy, lower highs + lower lows for a sell). Most losing streaks in
        // the long-only backtest happened at completely ordinary ATR - the common
        // thread was sideways/whipsaw structure, which this catches independently
        // of volatility.
        private bool TrendStructureAgrees(int direction)
        {
            if (!UseChopFilter) return true;

            var highs = new List<double>();
            var lows = new List<double>();
            for (int i = _swings.Count - 1; i >= 0 && (highs.Count < ChopFilterSwingCount || lows.Count < ChopFilterSwingCount); i--)
            {
                var s = _swings[i];
                if (s.Type == SwingType.High && highs.Count < ChopFilterSwingCount) highs.Add(s.Level);
                else if (s.Type == SwingType.Low && lows.Count < ChopFilterSwingCount) lows.Add(s.Level);
            }
            if (highs.Count < ChopFilterSwingCount || lows.Count < ChopFilterSwingCount)
                return true; // not enough swing history yet, don't block

            highs.Reverse(); // collected newest-first, put back in chronological order
            lows.Reverse();

            return direction == 1
                ? IsStrictlyIncreasing(highs) && IsStrictlyIncreasing(lows)
                : IsStrictlyDecreasing(highs) && IsStrictlyDecreasing(lows);
        }

        private static bool IsStrictlyIncreasing(List<double> values)
        {
            for (int i = 1; i < values.Count; i++)
                if (values[i] <= values[i - 1]) return false;
            return true;
        }

        private static bool IsStrictlyDecreasing(List<double> values)
        {
            for (int i = 1; i < values.Count; i++)
                if (values[i] >= values[i - 1]) return false;
            return true;
        }

        // Checked every tick (not just per-bar close) so a profit spike mid-bar
        // isn't missed before it reverses. Combines three rules into one ratchet:
        // breakeven once profit crosses BreakEvenTriggerUsd (locks $0); a trailing
        // lock of TrailingLockPercent of the highest profit ever seen (locks
        // progressively as profit grows); and a giveback stop that only engages
        // once peak profit passes GivebackMinProfitUsd, then caps how much of
        // that peak can be given back (GivebackMaxPercent) - unlike the trailing
        // lock, small/medium winners are left completely untouched below that
        // floor, so it only targets the "ran to $12k, round-tripped to $0" case
        // without clipping every winner early. Whichever rule currently
        // guarantees the most is applied; the stop only ever moves to lock in
        // MORE profit, never less - this ignores spread, so a fill right at the
        // locked level can still cost a few points net.
        protected override void OnTick()
        {
            if (_currentPosition == null) return;

            double profit = _currentPosition.NetProfit;
            _peakProfit = Math.Max(_peakProfit, profit);

            double desiredLock = double.NegativeInfinity;
            if (BreakEvenTriggerUsd > 0 && profit >= BreakEvenTriggerUsd)
                desiredLock = Math.Max(desiredLock, 0.0);
            if (TrailingLockPercent > 0 && _peakProfit > 0)
                desiredLock = Math.Max(desiredLock, _peakProfit * TrailingLockPercent / 100.0);
            if (GivebackMinProfitUsd > 0 && _peakProfit >= GivebackMinProfitUsd)
                desiredLock = Math.Max(desiredLock, _peakProfit * (1.0 - GivebackMaxPercent / 100.0));

            if (double.IsNegativeInfinity(desiredLock) || desiredLock <= _lockedProfitUsd)
                return; // nothing more protective to apply yet

            int direction = _currentPosition.TradeType == TradeType.Buy ? 1 : -1;
            double lockPrice = _currentPosition.EntryPrice + (desiredLock / _currentPosition.VolumeInUnits) * direction;

            var result = _currentPosition.ModifyStopLossPrice(lockPrice);
            if (result.IsSuccessful)
            {
                _lockedProfitUsd = desiredLock;
                Print("ProfitLock: SL moved to lock ${0:F2} (peak profit ${1:F2}) for position {2}", desiredLock, _peakProfit, _currentPosition.Id);
            }
        }

        // Fires for every close regardless of cause (our own signal-flip close,
        // SL, TP, or a margin stop-out), so this is the single place that logs
        // the real outcome of every trade - args.Reason says why it closed,
        // Position.NetProfit is the actual realized $ result. Also catches
        // SL/TP-triggered closes (not initiated by our own ClosePosition call)
        // so _currentPosition never goes stale and causes a duplicate open.
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            Print(
                "EXIT #{0}: {1} {2} closed | Reason={3} | NetProfit=${4:F2} | Entry={5} Exit-SL={6} Exit-TP={7} Volume={8}",
                p.Id, p.TradeType, SymbolName, args.Reason, p.NetProfit, p.EntryPrice, p.StopLoss, p.TakeProfit, p.VolumeInUnits);

            if (_currentPosition != null && p.Id == _currentPosition.Id)
                _currentPosition = null;
        }

        // Defensive reconciliation against the broker's actual position list,
        // run every bar: drops tracking if our position is gone (however that
        // happened) and adopts any stray same-label position we lost track of.
        // Two earlier fixes (direction-filtered lookup, then identity tracking
        // alone) both still produced duplicate same-direction positions in live
        // backtests, so this no longer trusts internal state alone.
        private void SyncPosition()
        {
            if (_currentPosition != null && !Positions.Any(p => p.Id == _currentPosition.Id))
            {
                Print("SyncPosition: tracked position {0} no longer exists, clearing", _currentPosition.Id);
                _currentPosition = null;
            }
            if (_currentPosition == null)
            {
                var adopted = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == BotLabel);
                if (adopted != null)
                {
                    Print("SyncPosition: adopting untracked position {0} ({1})", adopted.Id, adopted.TradeType);
                    _currentPosition = adopted;
                    _peakProfit = 0;
                    _lockedProfitUsd = double.NegativeInfinity;
                }
            }
        }

        // Turns this into a day-trading bot: no position survives past
        // MaxHoldDays regardless of what the signal is doing, win or lose.
        private void CheckMaxHold()
        {
            if (_currentPosition == null || MaxHoldDays <= 0) return;

            if (Server.Time - _currentPosition.EntryTime >= TimeSpan.FromDays(MaxHoldDays))
            {
                Print("MaxHold: closing position {0}, held since {1}", _currentPosition.Id, _currentPosition.EntryTime);
                ClosePosition(_currentPosition);
                _currentPosition = null;
            }
        }

        private void OnBarOpened(BarOpenedEventArgs args)
        {
            int closedIndex = Bars.Count - 2;
            int candidate = closedIndex - SwingLength;
            if (candidate - SwingLength + 1 < 0)
                return;

            DetectSwing(candidate);
            CheckPendingBreak(closedIndex);
            ExecuteSignal();
        }

        // A bar is a swing high/low if its high/low is the most extreme within
        // SwingLength bars on either side - same window smc.swing_highs_lows uses.
        private void DetectSwing(int i)
        {
            double high = Bars.HighPrices[i];
            double low = Bars.LowPrices[i];
            bool isHigh = true;
            bool isLow = true;

            for (int k = i - SwingLength + 1; k <= i + SwingLength; k++)
            {
                if (k == i) continue;
                if (Bars.HighPrices[k] > high) isHigh = false;
                if (Bars.LowPrices[k] < low) isLow = false;
            }

            SwingType type;
            double level;
            if (isHigh) { type = SwingType.High; level = high; }
            else if (isLow) { type = SwingType.Low; level = low; }
            else return;

            // collapse consecutive same-type swings into the more extreme one,
            // same de-duplication rule as smc.swing_highs_lows
            if (_swings.Count > 0 && _swings[_swings.Count - 1].Type == type)
            {
                var last = _swings[_swings.Count - 1];
                bool moreExtreme = type == SwingType.High ? level > last.Level : level < last.Level;
                if (moreExtreme)
                {
                    last.Index = i;
                    last.Level = level;
                }
                return;
            }

            _swings.Add(new SwingPoint { Index = i, Type = type, Level = level });
            if (_swings.Count >= 4)
                EvaluateStructure();
        }

        // Same 4-point pattern checks as smc.bos_choch. A newer confirmed pattern
        // replaces whatever was still pending, so only one structure is ever
        // "live" at a time - a deliberate simplification of smc.bos_choch's
        // batch invalidation pass for streaming/live use.
        private void EvaluateStructure()
        {
            int n = _swings.Count;
            var p = new[] { _swings[n - 4], _swings[n - 3], _swings[n - 2], _swings[n - 1] };
            var lv = p.Select(x => x.Level).ToArray();

            bool bullishPattern = p[0].Type == SwingType.Low && p[1].Type == SwingType.High
                && p[2].Type == SwingType.Low && p[3].Type == SwingType.High;
            bool bearishPattern = p[0].Type == SwingType.High && p[1].Type == SwingType.Low
                && p[2].Type == SwingType.High && p[3].Type == SwingType.Low;

            int? direction = null;

            if (bullishPattern)
            {
                if (lv[0] < lv[2] && lv[2] < lv[1] && lv[1] < lv[3]) direction = 1;        // BOS
                else if (lv[3] > lv[1] && lv[1] > lv[0] && lv[0] > lv[2]) direction = 1;   // CHOCH
            }
            else if (bearishPattern)
            {
                if (lv[0] > lv[2] && lv[2] > lv[1] && lv[1] > lv[3]) direction = -1;       // BOS
                else if (lv[3] < lv[1] && lv[1] < lv[0] && lv[0] < lv[2]) direction = -1;  // CHOCH
            }

            if (direction != null)
                _pending = new PendingStructure { Direction = direction.Value, Level = lv[1] };
        }

        private void CheckPendingBreak(int closedIndex)
        {
            if (_pending == null) return;

            double price = CloseBreak
                ? Bars.ClosePrices[closedIndex]
                : (_pending.Direction == 1 ? Bars.HighPrices[closedIndex] : Bars.LowPrices[closedIndex]);

            bool broke = _pending.Direction == 1 ? price > _pending.Level : price < _pending.Level;
            if (!broke) return;

            _desiredDirection = _pending.Direction;
            _pending = null;
        }

        // Uses _currentPosition (the exact object handed back by our own order
        // execution) instead of re-querying Positions by symbol/label/direction -
        // a live run showed that lookup missing an existing position and
        // pyramiding into duplicates. Object identity can't produce a false miss.
        private void ExecuteSignal()
        {
            SyncPosition();
            CheckMaxHold();
            TradeType? desiredType = _desiredDirection == 1 ? TradeType.Buy : _desiredDirection == -1 ? TradeType.Sell : (TradeType?)null;

            // blocks new short entries only - an existing short still exits
            // normally on the next opposite (bullish) signal below.
            if (LongOnly && desiredType == TradeType.Sell)
                desiredType = null;

            // trend filter only blocks NEW entries against the higher-TF trend;
            // an existing position still exits on the opposite signal below, it
            // just won't be replaced by a counter-trend one.
            if (desiredType.HasValue && !MatchesTrend(desiredType.Value))
                desiredType = null;

            Print("ExecuteSignal: desired={0} current={1}", _desiredDirection, _currentPosition != null ? _currentPosition.Id + "/" + _currentPosition.TradeType : "none");

            if (_currentPosition != null && (!desiredType.HasValue || _currentPosition.TradeType != desiredType.Value))
            {
                ClosePosition(_currentPosition);
                _currentPosition = null;
            }

            if (desiredType.HasValue && _currentPosition == null)
                OpenTrade(desiredType.Value);
        }

        // Stop distance = ATR * multiplier, position size = (balance * risk%) / stop
        // distance - fixed dollar risk per trade regardless of current volatility,
        // instead of a stop expressed as a % of price.
        private void OpenTrade(TradeType type)
        {
            double atrValue = _atr.Result[Bars.Count - 2];
            if (double.IsNaN(atrValue) || atrValue <= 0)
                return; // not enough history yet to size a stop

            if (!AtrFilterAgrees(atrValue, Bars.Count - 2))
            {
                Print("SkipEntry: {0} blocked by ATR filter | ATR={1:F5} baseline={2:F5}", type, atrValue, _atrBaseline.Result[Bars.Count - 2]);
                return;
            }

            if (!TrendStructureAgrees(type == TradeType.Buy ? 1 : -1))
            {
                Print("SkipEntry: {0} blocked by chop filter (no consistent higher-high/higher-low or lower-high/lower-low swing structure)", type);
                return;
            }

            double stopDistance = atrValue * AtrMultiplier;
            double riskAmount = Account.Balance * RiskPercent / 100.0;
            double volume = Symbol.NormalizeVolumeInUnits(riskAmount / stopDistance);

            double slPips = stopDistance / Symbol.PipSize;
            double? tpPips = RewardRiskRatio > 0 ? stopDistance * RewardRiskRatio / Symbol.PipSize : (double?)null;

            var result = ExecuteMarketOrder(type, SymbolName, volume, BotLabel, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _currentPosition = result.Position;
                _peakProfit = 0;
                _lockedProfitUsd = double.NegativeInfinity;

                Print(
                    "ENTRY #{0}: {1} {2} {3} units @ {4} | SL={5} TP={6} | ATR={7:F5} StopDist={8:F2} RiskAmt=${9:F2} Balance=${10:F2}",
                    result.Position.Id, type, SymbolName, volume, result.Position.EntryPrice,
                    result.Position.StopLoss, result.Position.TakeProfit, atrValue, stopDistance, riskAmount, Account.Balance);
            }
        }
    }
}
