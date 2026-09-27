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

        [Parameter("Reward:Risk ratio (0 = no TP, rely on flip)", DefaultValue = 0.0, MinValue = 0, Group = "Risk")]
        public double RewardRiskRatio { get; set; }

        private const string BotLabel = "SmcBosChoch";
        private AverageTrueRange _atr;

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

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            Bars.BarOpened += OnBarOpened;
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

        // Closes every stray/wrong-direction position under our label and opens
        // the desired one if it isn't already held - can't pyramid, since it
        // reconciles against the broker's actual position list every bar
        // instead of trusting a direction-filtered lookup.
        private void ExecuteSignal()
        {
            var positions = Positions.Where(x => x.SymbolName == SymbolName && x.Label == BotLabel).ToList();
            TradeType? desiredType = _desiredDirection == 1 ? TradeType.Buy : _desiredDirection == -1 ? TradeType.Sell : (TradeType?)null;

            bool alreadyCorrect = false;
            foreach (var p in positions)
            {
                if (desiredType.HasValue && p.TradeType == desiredType.Value && !alreadyCorrect)
                {
                    alreadyCorrect = true;
                    continue;
                }
                ClosePosition(p);
            }

            if (desiredType.HasValue && !alreadyCorrect)
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

            double stopDistance = atrValue * AtrMultiplier;
            double riskAmount = Account.Balance * RiskPercent / 100.0;
            double volume = Symbol.NormalizeVolumeInUnits(riskAmount / stopDistance);

            double slPips = stopDistance / Symbol.PipSize;
            double? tpPips = RewardRiskRatio > 0 ? stopDistance * RewardRiskRatio / Symbol.PipSize : (double?)null;

            ExecuteMarketOrder(type, SymbolName, volume, BotLabel, slPips, tpPips);
        }
    }
}
