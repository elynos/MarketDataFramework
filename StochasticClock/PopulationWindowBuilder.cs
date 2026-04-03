using System.Collections.Generic;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.StochasticClock
{
    /// <summary>
    /// Fluent builder for <see cref="PopulationWindow"/> — useful for
    /// testing and for replaying historical tick data against the engine.
    /// </summary>
    public class PopulationWindowBuilder
    {
        private readonly List<string>         _expectedIsins = new List<string>();
        private readonly List<MarketDataTick> _ticks         = new List<MarketDataTick>();

        public PopulationWindowBuilder Expect(string isin)
        {
            _expectedIsins.Add(isin);
            return this;
        }

        public PopulationWindowBuilder ExpectAll(IEnumerable<string> isins)
        {
            _expectedIsins.AddRange(isins);
            return this;
        }

        public PopulationWindowBuilder WithTick(MarketDataTick tick)
        {
            _ticks.Add(tick);
            return this;
        }

        public PopulationWindow Build()
        {
            var window = new PopulationWindow(_expectedIsins);
            foreach (var tick in _ticks)
                window.AddTick(tick);
            window.Close();
            return window;
        }
    }
}
