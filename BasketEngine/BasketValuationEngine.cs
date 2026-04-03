using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.BasketEngine
{
    /// <summary>
    /// Orchestrates basket valuation for each closed population window.
    /// 
    /// For each window close event:
    ///   1. Aggregate the window into one price per instrument (via PopulationAggregator).
    ///   2. For each registered basket:
    ///      a. Compute weighted average (via WeightedAverageCalculator).
    ///      b. Compute jump vs previous valuation.
    ///      c. Flag suspect jumps (> MaxJumpBps threshold).
    ///   3. Return all BasketValuation results.
    /// 
    /// Thread-safety: basket registration/unregistration is thread-safe.
    /// Valuation is single-threaded per window (called from the clock's window-close handler).
    /// </summary>
    public class BasketValuationEngine : IBasketEngine
    {
        private readonly ConcurrentDictionary<string, BasketDefinition> _baskets;
        private readonly PopulationAggregator                           _aggregator;
        private readonly WeightedAverageCalculator                      _calculator;

        // Last computed valuation per basket, used for jump detection
        private readonly Dictionary<string, double> _lastValuation;

        public BasketValuationEngine()
            : this(new PopulationAggregator(), new WeightedAverageCalculator())
        { }

        public BasketValuationEngine(PopulationAggregator aggregator,
                                     WeightedAverageCalculator calculator)
        {
            _baskets       = new ConcurrentDictionary<string, BasketDefinition>(
                                 StringComparer.OrdinalIgnoreCase);
            _aggregator    = aggregator;
            _calculator    = calculator;
            _lastValuation = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        // ── IBasketEngine ─────────────────────────────────────────────────────

        public void RegisterBasket(BasketDefinition basket)
        {
            if (basket == null) throw new ArgumentNullException("basket");
            _baskets[basket.BasketId] = basket;
        }

        public void UnregisterBasket(string basketId)
        {
            BasketDefinition removed;
            _baskets.TryRemove(basketId, out removed);
            _lastValuation.Remove(basketId);
        }

        public IEnumerable<BasketValuation> Valuate(PopulationWindow window)
        {
            if (window == null)   throw new ArgumentNullException("window");
            if (!window.IsClosed) throw new ArgumentException("Window must be closed before valuation.");

            var results = new List<BasketValuation>();

            // Collect all distinct ISINs across all baskets
            HashSet<string> allIsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var basket in _baskets.Values)
                foreach (string isin in basket.Instruments)
                    allIsins.Add(isin);

            // Single aggregation pass for all instruments
            PopulationAggregator.AggregationResult agg =
                _aggregator.Aggregate(window, allIsins);

            DateTime valuationTime = DateTime.UtcNow;

            foreach (var basket in _baskets.Values)
            {
                // Filter prices to this basket's instruments
                var basketPrices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                bool hasUnavailable = false;

                foreach (string isin in basket.Instruments)
                {
                    double price;
                    if (agg.InstrumentPrices.TryGetValue(isin, out price))
                        basketPrices[isin] = price;
                    else
                        hasUnavailable = true;
                }

                if (basketPrices.Count == 0)
                {
                    // No prices at all — skip this valuation cycle
                    continue;
                }

                double weightedAvg = _calculator.Compute(basketPrices, basket.Weights);

                // Jump detection
                double?  jumpBps      = null;
                bool     jumpSuspect  = false;
                double   prevVal;

                if (_lastValuation.TryGetValue(basket.BasketId, out prevVal))
                {
                    double jump = _calculator.ComputeJumpBps(prevVal, weightedAvg);
                    jumpBps     = jump;
                    jumpSuspect = jump > basket.MaxJumpBps;
                }

                _lastValuation[basket.BasketId] = weightedAvg;

                var valuation = new BasketValuation
                {
                    BasketId           = basket.BasketId,
                    ValuationTime      = valuationTime,
                    WeightedAverage    = weightedAvg,
                    PopulationWindowId = window.WindowId,
                    InstrumentPrices   = basketPrices,
                    ExtrapolatedIsins  = agg.ExtrapolatedIsins
                                           .Where(i => basket.Instruments.Contains(i))
                                           .ToList(),
                    JumpBps            = jumpBps,
                    IsJumpSuspect      = jumpSuspect,
                    CompletenessRatio  = window.CompletenessRatio
                };

                results.Add(valuation);
            }

            return results;
        }

        public int RegisteredBasketCount { get { return _baskets.Count; } }
    }
}
