using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Intraday variant of SmcBosChochBot.cs - same swing_highs_lows + bos_choch
    // pattern logic (smartmoneyconcepts/smc.py) and the same ATR-based risk
    // stack (position sizing, breakeven/trailing/giveback ratchet, ATR ratio /
    // chop / overextension / loss-cooldown entry filters), carried over as-is
    // because that machinery is timeframe-agnostic by design (ratios and bar
    // counts, not absolute price levels).
    //
    // What's different from the H4 file: this is meant to run on an M5 or M15
    // chart and actually day-trade, so it adds a trading-session window (see
    // SessionStartHour/SessionEndHour) that both blocks new entries and forces
    // an existing position flat once the session ends - never carrying
    // anything overnight - and swaps the H4 file's day-scale MaxHoldDays for a
    // minute-scale MaxHoldMinutes safety net.
    //
    // IMPORTANT: SwingLength, the ATR/chop/overextension/cooldown thresholds,
    // and the session hours below are reasoned STARTING DEFAULTS carried over
    // from H4 tuning or picked for a first test - none of them have been
    // backtested on M5/M15 data. Expect to recalibrate every one of them from
    // real logs, the same measure-first process used on the H4 file.
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SmcBosChochBotIntraday : Robot
    {
        [Parameter("Swing Length", DefaultValue = 8, MinValue = 2, Group = "Structure")]
        public int SwingLength { get; set; }

        [Parameter("Close Break", DefaultValue = true, Group = "Structure")]
        public bool CloseBreak { get; set; }

        [Parameter("Long Only", DefaultValue = false, Group = "Structure")]
        public bool LongOnly { get; set; }

        // Untested on M5/M15: an hour with no overlap between two sessions (or
        // one that wraps midnight) behaves differently than on H4, since a lot
        // more bars fall inside/outside the window per day. Set both hours
        // equal to disable session gating entirely (always-in-session).
        [Parameter("Session Start Hour (UTC, 0-23)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC, 0-23)", DefaultValue = 20, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int SessionEndHour { get; set; }

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

        // Minute-scale, not day-scale like the H4 file's MaxHoldDays - this is
        // a safety net for a stuck trade WITHIN the session; the session
        // window above is what actually guarantees no overnight carry.
        [Parameter("Max Hold (minutes, 0 = off)", DefaultValue = 240, MinValue = 0, Group = "Risk")]
        public int MaxHoldMinutes { get; set; }

        [Parameter("Use Trend Filter", DefaultValue = false, Group = "Trend Filter")]
        public bool UseTrendFilter { get; set; }

        [Parameter("Trend Timeframe", DefaultValue = "Hour4", Group = "Trend Filter")]
        public TimeFrame TrendTimeFrame { get; set; }

        [Parameter("Trend EMA Period", DefaultValue = 50, MinValue = 2, Group = "Trend Filter")]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Use ATR Volatility Filter", DefaultValue = true, Group = "Chop/Volatility Filter")]
        public bool UseAtrFilter { get; set; }

        [Parameter("ATR Baseline Period", DefaultValue = 50, MinValue = 5, Group = "Chop/Volatility Filter")]
        public int AtrBaselinePeriod { get; set; }

        [Parameter("Min ATR Ratio vs baseline (0 = off)", DefaultValue = 0.65, MinValue = 0, Group = "Chop/Volatility Filter")]
        public double MinAtrRatio { get; set; }

        [Parameter("Max ATR Ratio vs baseline (0 = off)", DefaultValue = 1.8, MinValue = 0, Group = "Chop/Volatility Filter")]
        public double MaxAtrRatio { get; set; }

        [Parameter("Use Chop/Trend-Structure Filter", DefaultValue = true, Group = "Chop/Volatility Filter")]
        public bool UseChopFilter { get; set; }

        [Parameter("Chop Filter Swing Count", DefaultValue = 3, MinValue = 2, Group = "Chop/Volatility Filter")]
        public int ChopFilterSwingCount { get; set; }

        // Off by default on the H4 file (confirmed net-negative there); left
        // off here too until it's re-tested on intraday data.
        [Parameter("Use Overextension Filter", DefaultValue = false, Group = "Chop/Volatility Filter")]
        public bool UseOverextensionFilter { get; set; }

        [Parameter("Overextension MA Period", DefaultValue = 50, MinValue = 5, Group = "Chop/Volatility Filter")]
        public int OverextensionMaPeriod { get; set; }

        [Parameter("Max Overextension (ATR multiples, 0 = off)", DefaultValue = 3.0, MinValue = 0, Group = "Chop/Volatility Filter")]
        public double MaxOverextensionAtr { get; set; }

        [Parameter("Use Loss Cooldown Filter", DefaultValue = true, Group = "Chop/Volatility Filter")]
        public bool UseLossCooldownFilter { get; set; }

        [Parameter("Consecutive Losses to Trigger Cooldown", DefaultValue = 3, MinValue = 1, Group = "Chop/Volatility Filter")]
        public int CooldownLossThreshold { get; set; }

        [Parameter("Cooldown Duration (bars)", DefaultValue = 10, MinValue = 1, Group = "Chop/Volatility Filter")]
        public int CooldownBars { get; set; }

        // Distinct from the H4 bot's "SmcBosChoch" label so SyncPosition below
        // can never adopt a position opened by the other bot if both are ever
        // run on the same account/symbol.
        private const string BotLabel = "SmcBosChochIntraday";
        private AverageTrueRange _atr;
        private MovingAverage _atrBaseline;
        private MovingAverage _overextensionMa;
        private Bars _trendBars;
        private MovingAverage _trendEma;
        private double _peakProfit;
        private double _lockedProfitUsd = double.NegativeInfinity; // highest profit level the SL currently guarantees

        // Per-direction repeated-failure tracking, independent of market
        // condition - see OverextensionAgrees/TrendStructureAgrees comments
        // on the H4 file for why this exists alongside the structure filters.
        private readonly Dictionary<TradeType, int> _consecutiveLosses = new Dictionary<TradeType, int> { { TradeType.Buy, 0 }, { TradeType.Sell, 0 } };
        private readonly Dictionary<TradeType, int> _cooldownUntilBar = new Dictionary<TradeType, int> { { TradeType.Buy, -1 }, { TradeType.Sell, -1 } };

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
            if (UseOverextensionFilter)
                _overextensionMa = Indicators.MovingAverage(Bars.ClosePrices, OverextensionMaPeriod, MovingAverageType.Simple);
            if (UseTrendFilter)
            {
                _trendBars = MarketData.GetBars(TrendTimeFrame);
                _trendEma = Indicators.MovingAverage(_trendBars.ClosePrices, TrendEmaPeriod, MovingAverageType.Exponential);
            }
            Bars.BarOpened += OnBarOpened;
            Positions.Closed += OnPositionClosed;
        }

        // True inside [SessionStartHour, SessionEndHour) on the robot's UTC
        // clock; handles a window that wraps past midnight (e.g. 22-04) the
        // same way as same-day one. Equal start/end hours disables gating.
        private bool WithinTradingSession(DateTime time)
        {
            if (SessionStartHour == SessionEndHour) return true;
            int hour = time.Hour;
            return SessionStartHour < SessionEndHour
                ? hour >= SessionStartHour && hour < SessionEndHour
                : hour >= SessionStartHour || hour < SessionEndHour;
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
        // ratio is always returned (even when the filter is off or has no
        // baseline yet, as NaN) so callers can log what was actually measured,
        // not just whether it passed.
        private bool AtrFilterAgrees(double atrValue, int barIndex, out double ratio)
        {
            ratio = double.NaN;
            if (!UseAtrFilter) return true;
            double baseline = _atrBaseline.Result[barIndex];
            if (double.IsNaN(baseline) || baseline <= 0) return true; // not enough history yet

            ratio = atrValue / baseline;
            if (MinAtrRatio > 0 && ratio < MinAtrRatio) return false;
            if (MaxAtrRatio > 0 && ratio > MaxAtrRatio) return false;
            return true;
        }

        // Requires the last ChopFilterSwingCount swing highs AND swing lows to be
        // strictly monotonic in the trade's direction (higher highs + higher lows
        // for a buy, lower highs + lower lows for a sell).
        // detail always describes the swing values actually evaluated (even on a
        // pass) so entries can be calibrated from real logs.
        private bool TrendStructureAgrees(int direction, out string detail)
        {
            if (!UseChopFilter) { detail = "filter off"; return true; }

            var highs = new List<double>();
            var lows = new List<double>();
            for (int i = _swings.Count - 1; i >= 0 && (highs.Count < ChopFilterSwingCount || lows.Count < ChopFilterSwingCount); i--)
            {
                var s = _swings[i];
                if (s.Type == SwingType.High && highs.Count < ChopFilterSwingCount) highs.Add(s.Level);
                else if (s.Type == SwingType.Low && lows.Count < ChopFilterSwingCount) lows.Add(s.Level);
            }
            if (highs.Count < ChopFilterSwingCount || lows.Count < ChopFilterSwingCount)
            {
                detail = "insufficient swing history";
                return true; // not enough swing history yet, don't block
            }

            highs.Reverse(); // collected newest-first, put back in chronological order
            lows.Reverse();

            bool agrees = direction == 1
                ? IsStrictlyIncreasing(highs) && IsStrictlyIncreasing(lows)
                : IsStrictlyDecreasing(highs) && IsStrictlyDecreasing(lows);

            detail = string.Format(
                "highs=[{0}] lows=[{1}]",
                string.Join(",", highs.Select(h => h.ToString("F2"))),
                string.Join(",", lows.Select(l => l.ToString("F2"))));
            return agrees;
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

        // How far price has already run from a longer-period moving average, in
        // ATR multiples, in the trade's own direction (above the MA for a buy,
        // below it for a sell). distanceAtr is always returned (even on a pass)
        // for calibration from real logs.
        private bool OverextensionAgrees(TradeType type, double atrValue, int barIndex, out double distanceAtr)
        {
            distanceAtr = double.NaN;
            if (!UseOverextensionFilter) return true;
            double ma = _overextensionMa.Result[barIndex];
            if (double.IsNaN(ma) || atrValue <= 0) return true; // not enough history yet

            double price = Bars.ClosePrices[barIndex];
            distanceAtr = type == TradeType.Buy ? (price - ma) / atrValue : (ma - price) / atrValue;

            if (MaxOverextensionAtr > 0 && distanceAtr > MaxOverextensionAtr) return false;
            return true;
        }

        // Checked every tick (not just per-bar close) so a profit spike mid-bar
        // isn't missed before it reverses. Combines three rules into one ratchet:
        // breakeven once profit crosses BreakEvenTriggerUsd (locks $0); a trailing
        // lock of TrailingLockPercent of the highest profit ever seen; and a
        // giveback stop that only engages once peak profit passes
        // GivebackMinProfitUsd, then caps how much of that peak can be given
        // back (GivebackMaxPercent). Whichever rule currently guarantees the
        // most is applied; the stop only ever moves to lock in MORE profit,
        // never less - this ignores spread, so a fill right at the locked
        // level can still cost a few points net.
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
        // session-end flatten, SL, TP, or a margin stop-out), so this is the
        // single place that logs the real outcome of every trade.
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            Print(
                "EXIT #{0}: {1} {2} closed | Reason={3} | NetProfit=${4:F2} | Entry={5} Exit-SL={6} Exit-TP={7} Volume={8}",
                p.Id, p.TradeType, SymbolName, args.Reason, p.NetProfit, p.EntryPrice, p.StopLoss, p.TakeProfit, p.VolumeInUnits);

            if (_currentPosition != null && p.Id == _currentPosition.Id)
                _currentPosition = null;

            // -0.5 threshold matches how wins/losses/breakeven are classified
            // everywhere else - a scratch/breakeven exit isn't a "failure" and
            // resets the streak same as a win would.
            if (p.NetProfit < -0.5)
            {
                _consecutiveLosses[p.TradeType]++;
                if (UseLossCooldownFilter && _consecutiveLosses[p.TradeType] >= CooldownLossThreshold)
                {
                    _cooldownUntilBar[p.TradeType] = Bars.Count + CooldownBars;
                    Print("LossCooldown: {0} hit {1} consecutive losses, pausing new {0} entries until bar {2}",
                        p.TradeType, _consecutiveLosses[p.TradeType], _cooldownUntilBar[p.TradeType]);
                }
            }
            else
            {
                _consecutiveLosses[p.TradeType] = 0;
            }
        }

        // barsRemaining always reports how much cooldown is left (0 when clear)
        // so it can be logged on every entry attempt.
        private bool CooldownAgrees(TradeType type, out int barsRemaining)
        {
            barsRemaining = 0;
            if (!UseLossCooldownFilter) return true;
            int until = _cooldownUntilBar[type];
            if (until < 0) return true;
            int remaining = until - Bars.Count;
            if (remaining <= 0) return true;
            barsRemaining = remaining;
            return false;
        }

        // Defensive reconciliation against the broker's actual position list,
        // run every bar: drops tracking if our position is gone (however that
        // happened) and adopts any stray same-label position we lost track of.
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

        // Safety net for a trade stuck open WITHIN the session - the session
        // window itself (via ExecuteSignal below) is what guarantees no
        // overnight carry, independent of this.
        private void CheckMaxHold()
        {
            if (_currentPosition == null || MaxHoldMinutes <= 0) return;

            if (Server.Time - _currentPosition.EntryTime >= TimeSpan.FromMinutes(MaxHoldMinutes))
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
        // "live" at a time.
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
        // execution) instead of re-querying Positions by symbol/label/direction.
        private void ExecuteSignal()
        {
            SyncPosition();
            CheckMaxHold();
            TradeType? desiredType = _desiredDirection == 1 ? TradeType.Buy : _desiredDirection == -1 ? TradeType.Sell : (TradeType?)null;

            // blocks new short entries only - an existing short still exits
            // normally on the next opposite (bullish) signal below.
            if (LongOnly && desiredType == TradeType.Sell)
                desiredType = null;

            // Outside the trading session: nulls desiredType, which both
            // blocks a new entry AND (via the close check below) forces an
            // existing position flat once the session ends - day-trading
            // means never carrying a position into/past the session close,
            // not just refusing new ones.
            if (desiredType.HasValue && !WithinTradingSession(Server.Time))
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

            if (!WithinTradingSession(Server.Time))
                return; // redundant with the ExecuteSignal gate, but cheap insurance if OpenTrade is ever called from elsewhere

            bool atrOk = AtrFilterAgrees(atrValue, Bars.Count - 2, out double atrRatio);
            if (!atrOk)
            {
                Print("SkipEntry: {0} blocked by ATR filter | ATR={1:F5} ratio={2:F2}", type, atrValue, atrRatio);
                return;
            }

            bool chopOk = TrendStructureAgrees(type == TradeType.Buy ? 1 : -1, out string chopDetail);
            if (!chopOk)
            {
                Print("SkipEntry: {0} blocked by chop filter | {1}", type, chopDetail);
                return;
            }

            bool overextensionOk = OverextensionAgrees(type, atrValue, Bars.Count - 2, out double overextensionDistance);
            if (!overextensionOk)
            {
                Print("SkipEntry: {0} blocked by overextension filter | distance={1:F2}x ATR from {2}-period MA", type, overextensionDistance, OverextensionMaPeriod);
                return;
            }

            bool cooldownOk = CooldownAgrees(type, out int cooldownBarsRemaining);
            if (!cooldownOk)
            {
                Print("SkipEntry: {0} blocked by loss cooldown | {1} consecutive losses, {2} bars remaining", type, _consecutiveLosses[type], cooldownBarsRemaining);
                return;
            }

            Print("EntryFilters: {0} passed | ATR={1:F5} ratio={2:F2} | {3} | overextension={4:F2}x ATR | consecutiveLosses={5}", type, atrValue, atrRatio, chopDetail, overextensionDistance, _consecutiveLosses[type]);

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
