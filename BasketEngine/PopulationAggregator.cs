using System;
using System.Collections.Generic;
using System.Linq;
using MarketDataFramework.Core.Models;
using MarketDataFramework.Extrapolation;

namespace MarketDataFramework.BasketEngine
{
    /// <summary>
    /// Aggregates a population window into a flat map of ISIN -> representative price.
    /// 
    /// For each instrument in the basket:
    ///   - If ticks are present: compute arithmetic mean of all intra-window mid prices.
    ///   - If ticks are absent (incomplete population): apply extrapolation chain.
    ///   - If extrapolation also fails: instrument is flagged as unavailable.
    /// 
    /// Maintains a rolling history of ticks for extrapolation lookback.
    /// </summary>
    public class PopulationAggregator
    {
        private readonly ExtrapolationContext          _extrapolation;
        private readonly WeightedAverageCalculator     _calculator;

        // Rolling history: ISIN -> recent ticks (bounded ring buffer per instrument)
        private readonly Dictionary<string, Queue<MarketDataTick>> _history;
        private readonly int _maxHistoryPerInstrument;

        public PopulationAggregator()
            : this(new ExtrapolationContext(), new WeightedAverageCalculator(), 200)
        { }

        public PopulationAggregator(ExtrapolationContext extrapolation,
                                    WeightedAverageCalculator calculator,
                                    int maxHistoryPerInstrument)
        {
            _extrapolation          = extrapolation;
            _calculator             = calculator;
            _maxHistoryPerInstrument = maxHistoryPerInstrument;
            _history                = new Dictionary<string, Queue<MarketDataTick>>(
                                          StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Aggregation result returned for each population window.
        /// </summary>
        public class AggregationResult
        {
            public Dictionary<string, double> InstrumentPrices  { get; set; }
            public List<string>               ExtrapolatedIsins { get; set; }
            public List<string>               UnavailableIsins  { get; set; }

            public AggregationResult()
            {
                InstrumentPrices  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                ExtrapolatedIsins = new List<string>();
                UnavailableIsins  = new List<string>();
            }
        }

        /// <summary>
        /// Processes a closed population window and returns one price per instrument.
        /// Also updates the rolling tick history for future extrapolation.
        /// </summary>
        public AggregationResult Aggregate(PopulationWindow window,
                                           IEnumerable<string> basketIsins)
        {
            if (window == null)    throw new ArgumentNullException("window");
            if (!window.IsClosed)  throw new ArgumentException("Window must be closed before aggregation.");

            var result = new AggregationResult();
            DateTime windowCloseTime = DateTime.UtcNow;

            foreach (string isin in basketIsins)
            {
                IReadOnlyList<MarketDataTick> windowTicks = window.GetTicksForInstrument(isin);

                if (windowTicks.Count > 0)
                {
                    // Pass 1: arithmetic mean of all intra-window ticks
                    double mean = _calculator.ArithmeticMean(
                                      windowTicks.Select(t => t.MidPrice));
                    result.InstrumentPrices[isin] = mean;

                    // Update rolling history
                    UpdateHistory(windowTicks);
                }
                else
                {
                    // Missing instrument: attempt extrapolation
                    IReadOnlyList<MarketDataTick> history = GetHistory(isin);
                    MarketDataTick extrapolated = _extrapolation.TryExtrapolate(
                                                     isin, windowCloseTime, history);

                    if (extrapolated != null)
                    {
                        result.InstrumentPrices[isin]  = extrapolated.MidPrice;
                        result.ExtrapolatedIsins.Add(isin);
                    }
                    else
                    {
                        result.UnavailableIsins.Add(isin);
                    }
                }
            }

            return result;
        }

        // ── History management ────────────────────────────────────────────────

        private void UpdateHistory(IReadOnlyList<MarketDataTick> ticks)
        {
            foreach (var tick in ticks)
            {
                Queue<MarketDataTick> queue;
                if (!_history.TryGetValue(tick.InstrumentIsin, out queue))
                {
                    queue = new Queue<MarketDataTick>();
                    _history[tick.InstrumentIsin] = queue;
                }

                queue.Enqueue(tick);

                // Bounded ring buffer: evict oldest when full
                while (queue.Count > _maxHistoryPerInstrument)
                    queue.Dequeue();
            }
        }

        private IReadOnlyList<MarketDataTick> GetHistory(string isin)
        {
            Queue<MarketDataTick> queue;
            return _history.TryGetValue(isin, out queue)
                ? queue.ToList().AsReadOnly()
                : new List<MarketDataTick>().AsReadOnly();
        }

        /// <summary>Clears all rolling history (e.g. on market close).</summary>
        public void ClearHistory()
        {
            _history.Clear();
        }
    }
}
