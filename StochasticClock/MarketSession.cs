using System;

namespace MarketDataFramework.StochasticClock
{
    /// <summary>
    /// Describes a market session and its associated tick rate.
    /// The tick rate is the primary parameter driving window duration
    /// in the stochastic clock model.
    /// 
    /// Session overlaps (e.g. EU + US, 14h00-17h30 CET) produce much higher
    /// tick rates and therefore shorter windows. This is the period identified
    /// in the CIR where the model showed degraded accuracy (~1h after US open).
    /// </summary>
    public class MarketSession
    {
        public string Name              { get; set; }
        public TimeSpan OpenCet         { get; set; }
        public TimeSpan CloseCet        { get; set; }

        /// <summary>
        /// Expected market data contributions per second during this session.
        /// Drives window duration: higher rate = shorter windows.
        /// </summary>
        public double TickRatePerSecond { get; set; }

        public bool IsActive(TimeSpan cetTime)
        {
            if (OpenCet <= CloseCet)
                return cetTime >= OpenCet && cetTime < CloseCet;

            // overnight sessions that span midnight
            return cetTime >= OpenCet || cetTime < CloseCet;
        }

        public override string ToString()
        {
            return string.Format("{0} [{1:hh\\:mm}-{2:hh\\:mm} CET, {3} ticks/s]",
                Name, OpenCet, CloseCet, TickRatePerSecond);
        }
    }

    /// <summary>
    /// Provides the current active market session based on CET wall time.
    /// 
    /// Session boundaries (CET):
    ///   00:00 - 08:00  Pre-market / Asia late        ~  20 ticks/s
    ///   08:00 - 14:00  European session               ~ 150 ticks/s
    ///   14:00 - 17:30  EU + US overlap (high vol.)    ~ 500 ticks/s  ← known model weakness
    ///   17:30 - 22:00  US session                     ~ 200 ticks/s
    ///   22:00 - 00:00  Post-market                    ~  10 ticks/s
    /// </summary>
    public class MarketSessionProvider
    {
        private static readonly MarketSession[] Sessions = new[]
        {
            new MarketSession
            {
                Name              = "Pre-Market / Asia Late",
                OpenCet           = TimeSpan.FromHours(0),
                CloseCet          = TimeSpan.FromHours(8),
                TickRatePerSecond = 20.0
            },
            new MarketSession
            {
                Name              = "European Session",
                OpenCet           = TimeSpan.FromHours(8),
                CloseCet          = TimeSpan.FromHours(14),
                TickRatePerSecond = 150.0
            },
            new MarketSession
            {
                Name              = "EU-US Overlap",
                OpenCet           = TimeSpan.FromHours(14),
                CloseCet          = TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(30)),
                TickRatePerSecond = 500.0   // high-volatility overlap: known CIR weakness zone
            },
            new MarketSession
            {
                Name              = "US Session",
                OpenCet           = TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(30)),
                CloseCet          = TimeSpan.FromHours(22),
                TickRatePerSecond = 200.0
            },
            new MarketSession
            {
                Name              = "Post-Market",
                OpenCet           = TimeSpan.FromHours(22),
                CloseCet          = TimeSpan.FromHours(24),
                TickRatePerSecond = 10.0
            }
        };

        /// <summary>
        /// Returns the market session active at the current CET wall time.
        /// Falls back to the Pre-Market session if no session matches (should not happen).
        /// </summary>
        public MarketSession GetCurrentSession()
        {
            // Convert UTC to CET (UTC+1, ignoring DST for simplicity)
            DateTime cet     = DateTime.UtcNow.AddHours(1);
            TimeSpan cetTime = cet.TimeOfDay;

            foreach (var session in Sessions)
            {
                if (session.IsActive(cetTime))
                    return session;
            }

            return Sessions[0]; // fallback: pre-market
        }

        public MarketSession GetSessionAt(DateTime cetDateTime)
        {
            TimeSpan cetTime = cetDateTime.TimeOfDay;
            foreach (var session in Sessions)
            {
                if (session.IsActive(cetTime))
                    return session;
            }
            return Sessions[0];
        }
    }
}
