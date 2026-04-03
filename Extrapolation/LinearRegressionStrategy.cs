using System;
using System.Collections.Generic;
using System.Linq;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Extrapolation
{
    /// <summary>
    /// Extrapolation strategy: Ordinary Least Squares linear regression
    /// over a sliding window of historical ticks.
    /// 
    /// Per CIR (Incertitude 3, Hypothèse 2):
    ///   - Computationally acceptable for our latency budget.
    ///   - Values remain too far from market reality.
    ///   - Increasing the sample size improves accuracy but degrades performance.
    ///   - Under the assumption of a nearly-complete population (1 missing value),
    ///     performance was acceptable — but precision was still insufficient.
    /// 
    /// The CIR concluded that classical extrapolation methods are insufficient
    /// for sub-second market data in the high-frequency context. This class
    /// implements the method as studied, with its known limitations documented.
    /// </summary>
    public class LinearRegressionStrategy : IExtrapolationStrategy
    {
        public string StrategyName { get { return "LinearRegression"; } }

        /// <summary>
        /// Number of historical ticks used for the regression.
        /// Higher = more accurate but slower. CIR finding: accuracy gain
        /// plateaus beyond ~50 samples while performance degrades linearly.
        /// </summary>
        public int SampleSize { get; set; }

        /// <summary>
        /// Maximum age of the oldest sample to include.
        /// Prevents stale history from distorting the regression line.
        /// </summary>
        public TimeSpan HistoryWindow { get; set; }

        public LinearRegressionStrategy()
        {
            SampleSize    = 20;
            HistoryWindow = TimeSpan.FromSeconds(10);
        }

        public double? Extrapolate(string isin, DateTime atTime,
                                   IReadOnlyList<MarketDataTick> historicalTicks)
        {
            if (historicalTicks == null || historicalTicks.Count == 0)
                return null;

            DateTime cutoff = atTime - HistoryWindow;

            // Collect the N most recent ticks within the history window
            List<MarketDataTick> samples = historicalTicks
                .Where(t => string.Equals(t.InstrumentIsin, isin,
                                          StringComparison.OrdinalIgnoreCase)
                            && t.Timestamp >= cutoff
                            && t.Timestamp <= atTime)
                .OrderByDescending(t => t.Timestamp)
                .Take(SampleSize)
                .ToList();

            if (samples.Count < 2)
            {
                // Not enough data for regression — fall back silently
                return null;
            }

            // Express time as elapsed seconds from the oldest sample
            // to avoid floating-point precision issues with large timestamps
            DateTime origin    = samples.Min(t => t.Timestamp);
            double[] x         = samples.Select(t => (t.Timestamp - origin).TotalSeconds).ToArray();
            double[] y         = samples.Select(t => t.MidPrice).ToArray();

            RegressionResult reg = FitOls(x, y);

            // Project to the target time
            double xTarget = (atTime - origin).TotalSeconds;
            double estimate = reg.Intercept + reg.Slope * xTarget;

            return estimate;
        }

        // ── OLS fitting ───────────────────────────────────────────────────────

        private static RegressionResult FitOls(double[] x, double[] y)
        {
            int n      = x.Length;
            double sx  = x.Sum();
            double sy  = y.Sum();
            double sxx = x.Sum(xi => xi * xi);
            double sxy = x.Zip(y, (xi, yi) => xi * yi).Sum();

            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < double.Epsilon)
            {
                // Degenerate case: all x identical (constant time) — return mean
                return new RegressionResult { Slope = 0, Intercept = sy / n };
            }

            double slope     = (n * sxy - sx * sy) / denom;
            double intercept = (sy - slope * sx) / n;

            return new RegressionResult { Slope = slope, Intercept = intercept };
        }

        private struct RegressionResult
        {
            public double Slope;
            public double Intercept;
        }
    }
}
