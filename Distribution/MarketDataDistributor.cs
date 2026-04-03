using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MarketDataFramework.Core.Interfaces;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Distribution
{
    // ─────────────────────────────────────────────────────────────────────────
    // SubscriberRegistry
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe registry of internal consumers.
    /// Each consumer registers a callback that receives each MarketDataSnapshot.
    /// </summary>
    public class SubscriberRegistry
    {
        private readonly ConcurrentDictionary<string, Action<MarketDataSnapshot>> _subscribers
            = new ConcurrentDictionary<string, Action<MarketDataSnapshot>>(
                  StringComparer.OrdinalIgnoreCase);

        public void Register(string subscriberId, Action<MarketDataSnapshot> handler)
        {
            if (string.IsNullOrWhiteSpace(subscriberId))
                throw new ArgumentException("Subscriber ID cannot be empty.", "subscriberId");
            if (handler == null)
                throw new ArgumentNullException("handler");

            _subscribers[subscriberId] = handler;
        }

        public bool Unregister(string subscriberId)
        {
            Action<MarketDataSnapshot> removed;
            return _subscribers.TryRemove(subscriberId, out removed);
        }

        public ICollection<Action<MarketDataSnapshot>> GetAllHandlers()
        {
            return _subscribers.Values;
        }

        public int Count { get { return _subscribers.Count; } }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MarketDataPublisher
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes snapshots to all registered subscribers asynchronously
    /// using the thread pool to avoid blocking the valuation pipeline.
    /// 
    /// Each subscriber handler is invoked independently so one slow consumer
    /// cannot delay others. Exceptions per subscriber are caught and logged
    /// rather than propagated.
    /// </summary>
    public class MarketDataPublisher
    {
        private readonly SubscriberRegistry _registry;

        public MarketDataPublisher(SubscriberRegistry registry)
        {
            _registry = registry;
        }

        public void Publish(MarketDataSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            snapshot.PublishedAt = DateTime.UtcNow;

            foreach (var handler in _registry.GetAllHandlers())
            {
                // Capture for closure
                var h = handler;
                var s = snapshot;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try   { h(s); }
                    catch (Exception ex)
                    {
                        // In production: replace with structured logging (log4net, NLog, etc.)
                        Console.Error.WriteLine(
                            "[MDF] Subscriber dispatch error: {0}", ex.Message);
                    }
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MarketDataDistributor
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level distributor: combines registry and publisher behind the
    /// IMarketDataDistributor interface. Entry point for all downstream
    /// consumers wishing to receive real-time basket valuations.
    /// 
    /// Typical consumers at a major international bank context:
    ///   - Trading system (real-time P&amp;L)
    ///   - Risk engine (intraday VaR)
    ///   - Reference data store (DMDS / Oracle)
    ///   - Market operations dashboard
    /// </summary>
    public class MarketDataDistributor : IMarketDataDistributor
    {
        private readonly SubscriberRegistry  _registry;
        private readonly MarketDataPublisher _publisher;

        public MarketDataDistributor()
        {
            _registry  = new SubscriberRegistry();
            _publisher = new MarketDataPublisher(_registry);
        }

        public void RegisterSubscriber(string subscriberId, Action<MarketDataSnapshot> handler)
        {
            _registry.Register(subscriberId, handler);
        }

        public void UnregisterSubscriber(string subscriberId)
        {
            _registry.Unregister(subscriberId);
        }

        public void Publish(MarketDataSnapshot snapshot)
        {
            _publisher.Publish(snapshot);
        }

        public int SubscriberCount { get { return _registry.Count; } }
    }
}
