using System;
using System.Collections.Generic;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Core.Interfaces
{
    // ─────────────────────────────────────────────────────────────────────────
    // IMarketDataFeed
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Abstraction over an upstream market data feed (Bloomberg, Reuters, etc.).
    /// Implementations receive raw ticks and push them into the pipeline.
    /// </summary>
    public interface IMarketDataFeed
    {
        string FeedName { get; }

        /// <summary>Fired when a new tick is received from the feed.</summary>
        event EventHandler<MarketDataTick> TickReceived;

        void Subscribe(IEnumerable<string> isins);
        void Unsubscribe(string isin);
        void Connect();
        void Disconnect();
        bool IsConnected { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IStochasticClock
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manages the adaptive time windows used to delimit market data populations.
    /// 
    /// The stochastic clock does not tick at fixed intervals: it adapts to market
    /// activity. During low-liquidity periods (e.g. early Asian session), windows
    /// are longer; during high-liquidity periods (US open, macro releases), shorter.
    /// 
    /// Implemented as a sequence of i.i.d. clocks, each characterised by its own
    /// distribution function (cf. CIR nomenclature: "successions d'horloges
    /// indépendantes et identiquement distribuées").
    /// </summary>
    public interface IStochasticClock
    {
        /// <summary>Fired when a new population window opens.</summary>
        event EventHandler<PopulationWindow> WindowOpened;

        /// <summary>Fired when the current window closes and is ready for valuation.</summary>
        event EventHandler<PopulationWindow> WindowClosed;

        void Start(IEnumerable<string> expectedIsins);
        void Stop();

        /// <summary>Force-closes the current window (e.g. on a market event trigger).</summary>
        void ForceClose();

        PopulationWindow CurrentWindow { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IExtrapolationStrategy
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strategy to estimate a price for an instrument that has not contributed
    /// within the current population window.
    /// 
    /// Implementations:
    ///   - LastKnownValueStrategy  : uses the most recent tick (fastest, least accurate)
    ///   - LinearRegressionStrategy: fits a line through recent history (slower)
    /// </summary>
    public interface IExtrapolationStrategy
    {
        string StrategyName { get; }

        /// <summary>
        /// Estimates the price for <paramref name="isin"/> at <paramref name="atTime"/>,
        /// given the available historical ticks.
        /// Returns null if insufficient data to extrapolate.
        /// </summary>
        double? Extrapolate(string isin, DateTime atTime,
                            IReadOnlyList<MarketDataTick> historicalTicks);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IBasketEngine
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes basket valuations from a closed population window.
    /// Applies extrapolation for missing instruments and produces
    /// a <see cref="BasketValuation"/> for each registered basket.
    /// </summary>
    public interface IBasketEngine
    {
        void RegisterBasket(BasketDefinition basket);
        void UnregisterBasket(string basketId);

        /// <summary>
        /// Valuates all registered baskets against the given population window.
        /// Must complete within the latency budget (< 3 seconds end-to-end).
        /// </summary>
        IEnumerable<BasketValuation> Valuate(PopulationWindow window);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IMarketDataDistributor
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Distributes computed <see cref="MarketDataSnapshot"/> to all registered
    /// internal consumers (trading systems, risk engines, reporting).
    /// </summary>
    public interface IMarketDataDistributor
    {
        void RegisterSubscriber(string subscriberId, Action<MarketDataSnapshot> handler);
        void UnregisterSubscriber(string subscriberId);
        void Publish(MarketDataSnapshot snapshot);
    }
}
