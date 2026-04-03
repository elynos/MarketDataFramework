using System;

namespace MarketDataFramework.Core.Models
{
    /// <summary>
    /// Represents a financial instrument that can be part of a basket.
    /// Covers government bonds, equities, rates products.
    /// </summary>
    public class Instrument
    {
        public string Isin         { get; set; }
        public string Ticker       { get; set; }
        public string Currency     { get; set; }
        public InstrumentType Type { get; set; }
        public string Exchange     { get; set; }

        /// <summary>
        /// Bloomberg or Reuters identifier used for feed subscription.
        /// </summary>
        public string FeedIdentifier { get; set; }

        public Instrument() { }

        public Instrument(string isin, string ticker, InstrumentType type, string currency)
        {
            if (string.IsNullOrWhiteSpace(isin))
                throw new ArgumentException("ISIN cannot be null or empty.", "isin");

            Isin     = isin;
            Ticker   = ticker;
            Type     = type;
            Currency = currency;
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} ({2})", Type, Ticker, Isin);
        }
    }

    public enum InstrumentType
    {
        GovernmentBond,
        CorporateBond,
        Equity,
        InterestRateSwap,
        CreditDefaultSwap,
        Commodity,
        Index
    }
}
