using System;
using System.Collections.Generic;
using System.Threading;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.StochasticClock
{
    /// <summary>
    /// Implements the adaptive stochastic clock model described in the CIR.
    /// 
    /// The clock drives population window creation. Window duration is not fixed:
    /// it is computed from a distribution whose parameters depend on the current
    /// market session (Asian, European, American overlap).
    /// 
    /// Key constraint: any given window must not stay open beyond
    /// <see cref="MaxWindowDurationMs"/> (default 100ms) regardless of market activity.
    /// 
    /// Known limitation (per CIR): the model degrades for ~60-90 minutes after
    /// the US market opens (14h00 CET) due to a sudden surge in contribution rate
    /// that the current distribution parameters do not capture accurately.
    /// </summary>
    public class StochasticClockService : IStochasticClock, IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<PopulationWindow> WindowOpened;
        public event EventHandler<PopulationWindow> WindowClosed;

        // ── Configuration ─────────────────────────────────────────────────────
        /// <summary>Hard ceiling on window duration (ms). Default: 100ms.</summary>
        public int MaxWindowDurationMs { get; set; }

        /// <summary>Minimum window duration (ms) to avoid degenerate empty windows.</summary>
        public int MinWindowDurationMs { get; set; }

        // ── State ─────────────────────────────────────────────────────────────
        private PopulationWindow    _currentWindow;
        private IEnumerable<string> _expectedIsins;
        private Timer               _windowTimer;
        private readonly object     _lock = new object();
        private bool                _running;
        private readonly MarketSessionProvider _sessionProvider;

        public PopulationWindow CurrentWindow
        {
            get { lock (_lock) { return _currentWindow; } }
        }

        public StochasticClockService()
            : this(new MarketSessionProvider())
        { }

        public StochasticClockService(MarketSessionProvider sessionProvider)
        {
            _sessionProvider   = sessionProvider;
            MaxWindowDurationMs = 100;
            MinWindowDurationMs = 10;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start(IEnumerable<string> expectedIsins)
        {
            lock (_lock)
            {
                if (_running)
                    throw new InvalidOperationException("Stochastic clock is already running.");

                _expectedIsins = expectedIsins;
                _running       = true;
            }

            OpenNewWindow();
        }

        public void Stop()
        {
            lock (_lock)
            {
                _running = false;
                if (_windowTimer != null)
                {
                    _windowTimer.Dispose();
                    _windowTimer = null;
                }
                if (_currentWindow != null && !_currentWindow.IsClosed)
                    _currentWindow.Close();
            }
        }

        public void ForceClose()
        {
            CloseCurrentWindowAndOpenNew();
        }

        // ── Window management ─────────────────────────────────────────────────

        /// <summary>
        /// Adds a tick to the current population window.
        /// Called by the feed aggregator on every incoming tick.
        /// </summary>
        public void OnTickReceived(object sender, MarketDataTick tick)
        {
            lock (_lock)
            {
                if (_currentWindow != null && !_currentWindow.IsClosed)
                    _currentWindow.AddTick(tick);
            }
        }

        private void OpenNewWindow()
        {
            PopulationWindow newWindow;
            lock (_lock)
            {
                newWindow       = new PopulationWindow(_expectedIsins);
                _currentWindow  = newWindow;
            }

            RaiseWindowOpened(newWindow);

            // Schedule the window close based on the adaptive duration
            int durationMs = ComputeWindowDuration();
            _windowTimer   = new Timer(_ => CloseCurrentWindowAndOpenNew(),
                                       null, durationMs, Timeout.Infinite);
        }

        private void CloseCurrentWindowAndOpenNew()
        {
            PopulationWindow closedWindow;
            lock (_lock)
            {
                if (!_running) return;
                if (_currentWindow == null || _currentWindow.IsClosed) return;

                _currentWindow.Close();
                closedWindow = _currentWindow;

                if (_windowTimer != null)
                {
                    _windowTimer.Dispose();
                    _windowTimer = null;
                }
            }

            RaiseWindowClosed(closedWindow);

            // Immediately open the next window
            OpenNewWindow();
        }

        // ── Adaptive duration computation ─────────────────────────────────────

        /// <summary>
        /// Computes the duration of the next population window in milliseconds.
        /// 
        /// The duration is drawn from a distribution whose parameters are
        /// conditioned on the current market session. This is the "horloge
        /// stochastique" per CIR: a succession of i.i.d. clocks each with
        /// their own distribution function.
        /// 
        /// Simplified model used here:
        ///   - BaseRate  = session.TickRatePerSecond  (expected ticks/second)
        ///   - Duration  = Clamp(Normal(mean, sigma), min, max)
        ///   - mean      = 1000 / BaseRate            (ms per tick cycle)
        ///   - sigma     = mean * 0.3                 (30% variance)
        /// </summary>
        private int ComputeWindowDuration()
        {
            MarketSession session = _sessionProvider.GetCurrentSession();
            double mean           = session.TickRatePerSecond > 0
                                    ? 1000.0 / session.TickRatePerSecond
                                    : MaxWindowDurationMs;

            double sigma    = mean * 0.30;
            double sample   = SampleNormal(mean, sigma);
            int    duration = (int)Math.Round(
                                  Math.Max(MinWindowDurationMs,
                                           Math.Min(MaxWindowDurationMs, sample)));
            return duration;
        }

        /// <summary>
        /// Box-Muller transform for normal sampling.
        /// Using a thread-local Random to avoid contention.
        /// </summary>
        private static readonly ThreadLocal<Random> _rng =
            new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

        private static double SampleNormal(double mean, double sigma)
        {
            Random rng  = _rng.Value;
            double u1   = 1.0 - rng.NextDouble();
            double u2   = 1.0 - rng.NextDouble();
            double z    = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + sigma * z;
        }

        // ── Event helpers ─────────────────────────────────────────────────────

        private void RaiseWindowOpened(PopulationWindow window)
        {
            var handler = WindowOpened;
            if (handler != null) handler(this, window);
        }

        private void RaiseWindowClosed(PopulationWindow window)
        {
            var handler = WindowClosed;
            if (handler != null) handler(this, window);
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            Stop();
        }
    }
}
