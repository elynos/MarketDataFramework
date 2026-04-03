using System;
using System.Collections.Generic;
using System.Linq;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Extrapolation
{
    /// <summary>
    /// Extrapolation strategy: Last Known Value (LKV).
    /// 
    /// When an instrument has no contribution in the current window,
    /// this strategy returns the most recent tick price from history.
    /// 
    /// Per CIR (Incertitude 3, Hypothèse 1): this approach yields
    /// correct prices and exceptional performance (data copy is fast),
    /// but is not financially acceptable — prices stale by even 100ms
    /// are unusable for a govie desk managing multi-billion nominal positions.
    /// 
    /// Kept as a reference baseline and fallback of last resort when
    /// no other strategy can produce a value.
    /// </summary>
    public class LastKnownValueStrategy : IExtrapolationStrategy
    {
        public string StrategyName { get { return "LastKnownValue"; } }

        /// <summary>
        /// Maximum age of the last known value before we refuse to return it.
        /// Default: 3 seconds (the overall pipeline latency budget).
        /// </summary>
        public TimeSpan MaxStaleness { get; set; }

        public LastKnownValueStrategy()
        {
            MaxStaleness = TimeSpan.FromSeconds(3);
        }

        public double? Extrapolate(string isin, DateTime atTime,
                                   IReadOnlyList<MarketDataTick> historicalTicks)
        {
            if (historicalTicks == null || historicalTicks.Count == 0)
                return null;

            // Find the most recent tick for this instrument
            MarketDataTick latest = historicalTicks
                .Where(t => string.Equals(t.InstrumentIsin, isin,
                                          StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            if (latest == null)
                return null;

            // Reject if the last known value is too stale
            TimeSpan staleness = atTime - latest.Timestamp;
            if (staleness > MaxStaleness)
                return null;

            return latest.MidPrice;
        }
    }
}
