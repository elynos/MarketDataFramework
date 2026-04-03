using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketDataFramework.BasketEngine
{
    /// <summary>
    /// Computes weighted averages of instrument prices within a basket.
    /// 
    /// The basket valuation is a two-pass computation per CIR:
    ///   Pass 1: arithmetic mean of all ticks per instrument within the window.
    ///           → one representative price per instrument.
    ///   Pass 2: weighted average across instruments using basket weights.
    ///           → single basket price.
    /// 
    /// Precision note: government bond desks require accuracy within 1 bp (0.0001)
    /// in 99.99% of cases, with an absolute maximum of 2 bps (0.0002).
    /// All intermediate calculations use double precision (64-bit IEEE 754).
    /// </summary>
    public class WeightedAverageCalculator
    {
        /// <summary>
        /// Computes the weighted average price of a basket from per-instrument
        /// mean prices and their weights.
        /// </summary>
        /// <param name="instrumentMeanPrices">
        ///   Map of ISIN -> mean price (already aggregated from population window).
        /// </param>
        /// <param name="weights">
        ///   Map of ISIN -> basket weight (number of units held, not normalised).
        /// </param>
        /// <returns>Weighted average price of the basket.</returns>
        public double Compute(Dictionary<string, double> instrumentMeanPrices,
                              Dictionary<string, double> weights)
        {
            if (instrumentMeanPrices == null) throw new ArgumentNullException("instrumentMeanPrices");
            if (weights == null)              throw new ArgumentNullException("weights");
            if (instrumentMeanPrices.Count == 0)
                throw new ArgumentException("At least one instrument price is required.");

            double weightedSum = 0.0;
            double totalWeight = 0.0;

            foreach (var kvp in instrumentMeanPrices)
            {
                string isin  = kvp.Key;
                double price = kvp.Value;

                double weight;
                if (!weights.TryGetValue(isin, out weight) || weight <= 0)
                {
                    // Instrument present in prices but not in basket definition — skip
                    continue;
                }

                weightedSum += price * weight;
                totalWeight += weight;
            }

            if (totalWeight == 0)
                throw new InvalidOperationException(
                    "Total weight is zero — no matching instruments between prices and basket.");

            return weightedSum / totalWeight;
        }

        /// <summary>
        /// Computes the jump in basis points between two basket valuations.
        /// 1 bp = 0.0001 in price terms.
        /// </summary>
        public double ComputeJumpBps(double previousPrice, double currentPrice)
        {
            return Math.Abs(currentPrice - previousPrice) * 10000.0;
        }

        /// <summary>
        /// Arithmetic mean of a list of prices.
        /// Used in Pass 1 to reduce multiple intra-window ticks per instrument.
        /// </summary>
        public double ArithmeticMean(IEnumerable<double> prices)
        {
            if (prices == null) throw new ArgumentNullException("prices");
            var list = prices.ToList();
            if (list.Count == 0)
                throw new ArgumentException("Cannot compute mean of empty set.");
            return list.Sum() / list.Count;
        }
    }
}
