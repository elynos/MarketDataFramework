using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketDataFramework.Core.Models
{
    /// <summary>
    /// Represents a time-bounded population of market data ticks.
    /// 
    /// A population window is the core concept of the stochastic clock:
    /// instead of a fixed wall-clock interval, the window adapts its boundaries
    /// based on market activity (number of active participants, session overlap, etc.).
    /// 
    /// Within a 100ms tick slice, all contributions for each instrument are
    /// aggregated into a single representative price per instrument.
    /// Incomplete populations (missing instruments) are handled via
    /// the pluggable IExtrapolationStrategy.
    /// </summary>
    public class PopulationWindow
    {
        /// <summary>Unique identifier for this population window.</summary>
        public Guid WindowId { get; private set; }

        /// <summary>Wall-clock start of the window.</summary>
        public DateTime OpenedAt { get; private set; }

        /// <summary>Wall-clock end of the window (set when closed).</summary>
        public DateTime ClosedAt { get; private set; }

        /// <summary>Duration in milliseconds — variable under stochastic clock model.</summary>
        public double DurationMs { get { return (ClosedAt - OpenedAt).TotalMilliseconds; } }

        public bool IsClosed { get; private set; }

        /// <summary>
        /// All ticks received during this window, keyed by InstrumentIsin.
        /// One instrument can have multiple contributions within a single window.
        /// </summary>
        private readonly Dictionary<string, List<MarketDataTick>> _ticks;

        /// <summary>
        /// Set of ISINs expected in this population (i.e. basket constituents).
        /// </summary>
        private readonly HashSet<string> _expectedIsins;

        public PopulationWindow(IEnumerable<string> expectedIsins)
        {
            WindowId       = Guid.NewGuid();
            OpenedAt       = DateTime.UtcNow;
            IsClosed       = false;
            _ticks         = new Dictionary<string, List<MarketDataTick>>(StringComparer.OrdinalIgnoreCase);
            _expectedIsins = new HashSet<string>(expectedIsins, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds a tick to this population window.
        /// Thread-safe via locking on _ticks.
        /// </summary>
        public void AddTick(MarketDataTick tick)
        {
            if (tick == null) throw new ArgumentNullException("tick");
            if (IsClosed)
                throw new InvalidOperationException(
                    string.Format("Cannot add tick to closed window {0}.", WindowId));

            lock (_ticks)
            {
                List<MarketDataTick> list;
                if (!_ticks.TryGetValue(tick.InstrumentIsin, out list))
                {
                    list = new List<MarketDataTick>();
                    _ticks[tick.InstrumentIsin] = list;
                }
                list.Add(tick);
            }
        }

        /// <summary>Closes the window, recording the closing timestamp.</summary>
        public void Close()
        {
            ClosedAt = DateTime.UtcNow;
            IsClosed = true;
        }

        /// <summary>
        /// Returns the arithmetic mean of all tick prices for a given instrument
        /// within this window. Returns null if no ticks for that instrument.
        /// </summary>
        public double? GetMeanPrice(string isin)
        {
            List<MarketDataTick> list;
            lock (_ticks)
            {
                if (!_ticks.TryGetValue(isin, out list) || list.Count == 0)
                    return null;
            }
            return list.Average(t => t.MidPrice);
        }

        /// <summary>
        /// Returns all ISINs for which at least one tick was received.
        /// </summary>
        public IEnumerable<string> ContributedIsins
        {
            get
            {
                lock (_ticks) { return _ticks.Keys.ToList(); }
            }
        }

        /// <summary>
        /// ISINs expected but absent from this window (incomplete population).
        /// These will require extrapolation.
        /// </summary>
        public IEnumerable<string> MissingIsins
        {
            get
            {
                lock (_ticks)
                {
                    return _expectedIsins
                        .Where(isin => !_ticks.ContainsKey(isin))
                        .ToList();
                }
            }
        }

        /// <summary>
        /// True if all expected instruments contributed at least one tick.
        /// </summary>
        public bool IsComplete
        {
            get { return !MissingIsins.Any(); }
        }

        /// <summary>Population completeness ratio (0..1).</summary>
        public double CompletenessRatio
        {
            get
            {
                if (_expectedIsins.Count == 0) return 1.0;
                return (double)ContributedIsins.Count() / _expectedIsins.Count;
            }
        }

        public IReadOnlyList<MarketDataTick> GetTicksForInstrument(string isin)
        {
            List<MarketDataTick> list;
            lock (_ticks)
            {
                return _ticks.TryGetValue(isin, out list)
                    ? list.AsReadOnly()
                    : new List<MarketDataTick>().AsReadOnly();
            }
        }

        public override string ToString()
        {
            return string.Format("Window [{0:HH:mm:ss.fff} -> {1:HH:mm:ss.fff}] " +
                                 "{2}/{3} instruments, Complete={4}",
                OpenedAt, IsClosed ? ClosedAt : DateTime.UtcNow,
                ContributedIsins.Count(), _expectedIsins.Count, IsComplete);
        }
    }
}
