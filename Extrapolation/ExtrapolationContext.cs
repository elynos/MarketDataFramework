using System;
using System.Collections.Generic;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Extrapolation
{
    /// <summary>
    /// Context class that selects and applies the appropriate extrapolation
    /// strategy for a missing instrument.
    /// 
    /// Strategy chain (in order of preference):
    ///   1. LinearRegressionStrategy  — preferred, per CIR H2 of Incertitude 3
    ///   2. LastKnownValueStrategy    — fallback when regression lacks data
    ///   3. null (no value)           — population flagged as incomplete, valuation skipped
    /// 
    /// All extrapolated values are tagged with IsExtrapolated=true on the
    /// synthetic tick so consumers can filter if needed.
    /// </summary>
    public class ExtrapolationContext
    {
        private readonly List<IExtrapolationStrategy> _strategies;

        public ExtrapolationContext()
        {
            _strategies = new List<IExtrapolationStrategy>
            {
                new LinearRegressionStrategy(),
                new LastKnownValueStrategy()
            };
        }

        public ExtrapolationContext(IEnumerable<IExtrapolationStrategy> strategies)
        {
            _strategies = new List<IExtrapolationStrategy>(strategies);
        }

        /// <summary>
        /// Attempts to produce an extrapolated tick for <paramref name="isin"/>.
        /// Walks the strategy chain until one returns a value.
        /// Returns null if no strategy can produce an estimate.
        /// </summary>
        public MarketDataTick TryExtrapolate(string isin, DateTime atTime,
                                             IReadOnlyList<MarketDataTick> history)
        {
            foreach (var strategy in _strategies)
            {
                double? price = strategy.Extrapolate(isin, atTime, history);
                if (price.HasValue)
                {
                    return new MarketDataTick
                    {
                        InstrumentIsin = isin,
                        Price          = price.Value,
                        Bid            = price.Value,
                        Ask            = price.Value,
                        Timestamp      = atTime,
                        FeedSource     = string.Format("Extrapolated:{0}", strategy.StrategyName),
                        IsExtrapolated = true
                    };
                }
            }

            return null;
        }
    }
}
