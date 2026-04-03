using System;
using System.Collections.Generic;

namespace MarketDataFramework.Core.Models
{
    /// <summary>
    /// Result of a basket valuation for a single population window.
    /// Contains the weighted average price and per-instrument detail.
    /// </summary>
    public class BasketValuation
    {
        public string   BasketId        { get; set; }
        public DateTime ValuationTime   { get; set; }
        public double   WeightedAverage { get; set; }
        public Guid     PopulationWindowId { get; set; }

        /// <summary>Per-instrument mean price used in this valuation.</summary>
        public Dictionary<string, double> InstrumentPrices { get; set; }

        /// <summary>ISINs for which extrapolation was applied.</summary>
        public List<string> ExtrapolatedIsins { get; set; }

        /// <summary>
        /// Jump in basis points vs previous valuation.
        /// Null for the very first valuation.
        /// </summary>
        public double? JumpBps { get; set; }

        /// <summary>
        /// True if the jump exceeds the basket's MaxJumpBps threshold.
        /// Govie desk requires this to be within 2 bps in > 99.99% of cases.
        /// </summary>
        public bool IsJumpSuspect { get; set; }

        /// <summary>Population completeness ratio at time of valuation.</summary>
        public double CompletenessRatio { get; set; }

        public BasketValuation()
        {
            InstrumentPrices  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ExtrapolatedIsins = new List<string>();
        }

        public override string ToString()
        {
            return string.Format("Basket '{0}' @ {1:HH:mm:ss.fff} = {2:F6} " +
                                 "[{3} extrapolated, Jump={4}bps, Suspect={5}]",
                BasketId, ValuationTime, WeightedAverage,
                ExtrapolatedIsins.Count,
                JumpBps.HasValue ? JumpBps.Value.ToString("F4") : "n/a",
                IsJumpSuspect);
        }
    }

    /// <summary>
    /// Full market data snapshot: all basket valuations computed for a given
    /// population window. Distributed to all subscribers after each window close.
    /// </summary>
    public class MarketDataSnapshot
    {
        public Guid     SnapshotId     { get; private set; }
        public DateTime PublishedAt    { get; set; }
        public Guid     WindowId       { get; set; }

        public List<BasketValuation> Valuations { get; private set; }

        public MarketDataSnapshot()
        {
            SnapshotId = Guid.NewGuid();
            Valuations = new List<BasketValuation>();
        }

        public void AddValuation(BasketValuation valuation)
        {
            if (valuation == null) throw new ArgumentNullException("valuation");
            Valuations.Add(valuation);
        }

        public override string ToString()
        {
            return string.Format("Snapshot [{0}] @ {1:HH:mm:ss.fff}, {2} baskets",
                SnapshotId.ToString("N").Substring(0, 8),
                PublishedAt, Valuations.Count);
        }
    }
}
