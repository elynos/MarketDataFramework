using System;
using System.Collections.Generic;
using System.Timers;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Infrastructure.Feeds
{
    // ─────────────────────────────────────────────────────────────────────────
    // BloombergFeedAdapter
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adapter for Bloomberg B-PIPE / SAPI market data feed.
    /// 
    /// In the a major international bank MDF context, Bloomberg was the primary real-time source
    /// for government bond prices. This adapter wraps the Bloomberg API
    /// subscription model and translates Bloomberg field values into
    /// internal <see cref="MarketDataTick"/> objects.
    /// 
    /// NOTE: The actual Bloomberg API SDK (blpapi) is a proprietary dependency.
    /// This adapter compiles without it and simulates the connection for
    /// integration testing. Replace the simulation block with real
    /// Bloomberg API calls when building against the actual SDK.
    /// </summary>
    public class BloombergFeedAdapter : IMarketDataFeed, IDisposable
    {
        public string FeedName { get { return "Bloomberg"; } }
        public event EventHandler<MarketDataTick> TickReceived;

        private readonly HashSet<string> _subscribed;
        private bool  _connected;

        // Simulation timer (replace with Bloomberg session listener in production)
        private Timer _simulationTimer;
        private readonly Random _rng = new Random();

        public bool IsConnected { get { return _connected; } }

        public BloombergFeedAdapter()
        {
            _subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Connect()
        {
            // Production: initialise Bloomberg session, authenticate, open service
            // Session session = new Session(sessionOptions, eventHandler);
            // session.Start(); session.OpenService("//blp/mktdata");
            _connected = true;
            Console.WriteLine("[Bloomberg] Connected.");
        }

        public void Disconnect()
        {
            _connected = false;
            if (_simulationTimer != null)
            {
                _simulationTimer.Stop();
                _simulationTimer.Dispose();
                _simulationTimer = null;
            }
            Console.WriteLine("[Bloomberg] Disconnected.");
        }

        public void Subscribe(IEnumerable<string> isins)
        {
            if (!_connected)
                throw new InvalidOperationException("Feed not connected.");

            foreach (string isin in isins)
            {
                _subscribed.Add(isin);
                // Production: session.Subscribe(subscriptionList) with BID/ASK/LAST_PRICE fields
            }

            // Simulation: fire synthetic ticks on a timer
            StartSimulation();
        }

        public void Unsubscribe(string isin)
        {
            _subscribed.Remove(isin);
            // Production: session.Unsubscribe(subscriptionList)
        }

        private void StartSimulation()
        {
            if (_simulationTimer != null) return;

            _simulationTimer          = new Timer(50); // fire every 50ms
            _simulationTimer.Elapsed += OnSimulationTick;
            _simulationTimer.AutoReset = true;
            _simulationTimer.Start();
        }

        private void OnSimulationTick(object sender, ElapsedEventArgs e)
        {
            if (!_connected) return;

            // Simulate 60-80% of instruments contributing per window
            var isins = new List<string>(_subscribed);
            foreach (var isin in isins)
            {
                if (_rng.NextDouble() > 0.30) // 70% contribution probability
                {
                    double basePrice = 100.0 + _rng.NextDouble() * 10.0;
                    double spread    = 0.02 + _rng.NextDouble() * 0.05;

                    var tick = new MarketDataTick
                    {
                        InstrumentIsin = isin,
                        Bid            = basePrice - spread / 2,
                        Ask            = basePrice + spread / 2,
                        Price          = basePrice,
                        Timestamp      = DateTime.UtcNow,
                        FeedSource     = FeedName,
                        IsExtrapolated = false
                    };

                    RaiseTick(tick);
                }
            }
        }

        private void RaiseTick(MarketDataTick tick)
        {
            var handler = TickReceived;
            if (handler != null) handler(this, tick);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReutersFeedAdapter
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adapter for Reuters RMDS / Elektron market data feed.
    /// Secondary source used as a cross-validation feed and fallback.
    /// Same simulation pattern as BloombergFeedAdapter.
    /// </summary>
    public class ReutersFeedAdapter : IMarketDataFeed, IDisposable
    {
        public string FeedName { get { return "Reuters"; } }
        public event EventHandler<MarketDataTick> TickReceived;

        private readonly HashSet<string> _subscribed;
        private bool   _connected;
        private Timer  _simulationTimer;
        private readonly Random _rng = new Random(42); // different seed from Bloomberg

        public bool IsConnected { get { return _connected; } }

        public ReutersFeedAdapter()
        {
            _subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Connect()
        {
            _connected = true;
            Console.WriteLine("[Reuters] Connected.");
        }

        public void Disconnect()
        {
            _connected = false;
            if (_simulationTimer != null)
            {
                _simulationTimer.Stop();
                _simulationTimer.Dispose();
                _simulationTimer = null;
            }
            Console.WriteLine("[Reuters] Disconnected.");
        }

        public void Subscribe(IEnumerable<string> isins)
        {
            if (!_connected)
                throw new InvalidOperationException("Feed not connected.");

            foreach (string isin in isins)
                _subscribed.Add(isin);

            StartSimulation();
        }

        public void Unsubscribe(string isin)
        {
            _subscribed.Remove(isin);
        }

        private void StartSimulation()
        {
            if (_simulationTimer != null) return;
            _simulationTimer           = new Timer(75); // slightly offset from Bloomberg
            _simulationTimer.Elapsed  += OnSimulationTick;
            _simulationTimer.AutoReset = true;
            _simulationTimer.Start();
        }

        private void OnSimulationTick(object sender, ElapsedEventArgs e)
        {
            if (!_connected) return;

            var isins = new List<string>(_subscribed);
            foreach (var isin in isins)
            {
                if (_rng.NextDouble() > 0.50) // 50% contribution probability (lower than Bloomberg)
                {
                    double basePrice = 100.0 + _rng.NextDouble() * 10.0;
                    double spread    = 0.025 + _rng.NextDouble() * 0.06;

                    var tick = new MarketDataTick
                    {
                        InstrumentIsin = isin,
                        Bid            = basePrice - spread / 2,
                        Ask            = basePrice + spread / 2,
                        Price          = basePrice,
                        Timestamp      = DateTime.UtcNow,
                        FeedSource     = FeedName,
                        IsExtrapolated = false
                    };

                    var handler = TickReceived;
                    if (handler != null) handler(this, tick);
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FeedAggregator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates ticks from all registered feeds and dispatches them
    /// to the stochastic clock's current population window.
    /// 
    /// Deduplication: if the same ISIN contributes within 10ms from two feeds,
    /// only the first tick is forwarded (prefer Bloomberg over Reuters).
    /// This avoids artificial population inflation.
    /// </summary>
    public class FeedAggregator : IDisposable
    {
        private readonly List<IMarketDataFeed>          _feeds;
        private readonly Dictionary<string, DateTime>   _lastTickTime;
        private readonly TimeSpan                       _deduplicationWindow;

        public event EventHandler<MarketDataTick> TickAggregated;

        public FeedAggregator()
        {
            _feeds               = new List<IMarketDataFeed>();
            _lastTickTime        = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _deduplicationWindow = TimeSpan.FromMilliseconds(10);
        }

        public void AddFeed(IMarketDataFeed feed)
        {
            _feeds.Add(feed);
            feed.TickReceived += OnTickReceived;
        }

        public void ConnectAll(IEnumerable<string> isins)
        {
            foreach (var feed in _feeds)
            {
                feed.Connect();
                feed.Subscribe(isins);
            }
        }

        public void DisconnectAll()
        {
            foreach (var feed in _feeds)
                feed.Disconnect();
        }

        private void OnTickReceived(object sender, MarketDataTick tick)
        {
            lock (_lastTickTime)
            {
                DateTime lastTime;
                if (_lastTickTime.TryGetValue(tick.InstrumentIsin, out lastTime))
                {
                    if (tick.Timestamp - lastTime < _deduplicationWindow)
                        return; // deduplicated
                }
                _lastTickTime[tick.InstrumentIsin] = tick.Timestamp;
            }

            var handler = TickAggregated;
            if (handler != null) handler(this, tick);
        }

        public void Dispose()
        {
            DisconnectAll();
            foreach (var feed in _feeds)
            {
                var disposable = feed as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }
    }
}
