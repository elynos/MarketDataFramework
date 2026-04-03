using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketDataFramework.Core.Models
{
    /// <summary>
    /// Defines a basket of financial instruments with their respective weights.
    /// A basket is used to replicate or track an index.
    /// Example: a govie basket of 10 government bonds from different sovereigns.
    /// </summary>
    public class BasketDefinition
    {
        public string BasketId   { get; set; }
        public string BasketName { get; set; }
        public string Currency   { get; set; }

        /// <summary>
        /// Map of InstrumentIsin -> Quantity/Weight in the basket.
        /// Weight is expressed as the number of units held.
        /// </summary>
        public Dictionary<string, double> Weights { get; private set; }

        /// <summary>
        /// Reference index this basket is designed to replicate, if any.
        /// </summary>
        public string ReplicatedIndexId { get; set; }

        /// <summary>
        /// Maximum acceptable jump (in basis points) between two consecutive
        /// basket valuations. If exceeded, the valuation is flagged as suspect.
        /// Default: 2 bps — threshold accepted by govie desks per CIR.
        /// </summary>
        public double MaxJumpBps { get; set; }

        public BasketDefinition()
        {
            Weights    = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            MaxJumpBps = 2.0;
        }

        public BasketDefinition(string basketId, string basketName, string currency)
            : this()
        {
            BasketId   = basketId;
            BasketName = basketName;
            Currency   = currency;
        }

        public void AddInstrument(string isin, double weight)
        {
            if (string.IsNullOrWhiteSpace(isin))
                throw new ArgumentException("ISIN cannot be empty.", "isin");
            if (weight <= 0)
                throw new ArgumentOutOfRangeException("weight", "Weight must be positive.");

            Weights[isin] = weight;
        }

        public void RemoveInstrument(string isin)
        {
            Weights.Remove(isin);
        }

        /// <summary>Total weight (sum of all positions).</summary>
        public double TotalWeight
        {
            get { return Weights.Values.Sum(); }
        }

        /// <summary>Normalised weight for a given instrument (0..1).</summary>
        public double NormalisedWeight(string isin)
        {
            double total = TotalWeight;
            if (total == 0) return 0;
            double w;
            return Weights.TryGetValue(isin, out w) ? w / total : 0;
        }

        public IEnumerable<string> Instruments
        {
            get { return Weights.Keys; }
        }

        public override string ToString()
        {
            return string.Format("Basket '{0}' [{1} instruments, MaxJump={2}bps]",
                BasketName, Weights.Count, MaxJumpBps);
        }
    }
}
