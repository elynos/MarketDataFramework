# Market Data Framework (MDF)

**C# implementation — .NET Framework 4.8**  
Research project conducted at a major international bank, 2013 – 2015 S1  
CIR reference: *Market Data Framework — Distribution de market data et indices de référence*

---

## Context

Traders base all their work on market movements, price levels and volatility across equities, bonds and other financial products. The objective of the Market Data Framework was to build an internal system capable of distributing market data and reference indices bank-wide — without relying on third-party middleware.

The research focused on one specific challenge: **real-time index replication from a basket of asynchronously contributing instruments**, computed at high frequency.

Simplified statement of the problem:
> *Provide, in real time, a discrete moving average of asynchronous states across a set of financial objects.*

---

## Research Challenges (per CIR)

### Incertitude 1 — Continuous vs Discrete models in high-frequency context

Government bond pricing models (Black-Scholes for equity, Hull-White 1F/2F for swaptions, CIR for short-rate products) are continuous. High-frequency market data is discrete.

| Hypothesis | Result |
|---|---|
| H1: Apply continuous models directly (Quant library) | < 99.9% within target in >0.1% of cases — **rejected** (>5 bps for govie desk) |
| H2: Lévy jump-diffusion (commodities library adapted) | Convergence < 99% — **rejected** |
| H3: Discrete model with bounded jumps | 0.01% of cases > 1bp, max 2bps in 0.0001% — **accepted** |

### Incertitude 2 — Asynchronous population delimitation (Stochastic Clock)

Market data contributions are not synchronous. A fixed 100ms window produces incomplete populations during low-liquidity periods and oversized populations during high-volatility periods.

**Solution: stochastic clock** — a sequence of i.i.d. clocks, each characterised by its own distribution function parameterised by market session (Asian / European / EU-US overlap / US).

Known limitation: the model degrades for ~60–90 minutes after US market open (14h00 CET) due to a sudden surge in contribution rate that the current distribution parameters do not capture accurately.

### Incertitude 3 — Incomplete populations (missing instrument prices)

When one or more basket instruments have not contributed within a window:

| Hypothesis | Result |
|---|---|
| H1: Last Known Value | Fast but financially unacceptable (stale prices unusable for govie desk) |
| H2: Linear regression (OLS) | Computationally acceptable, values too far from reality. Sample size vs accuracy trade-off unresolved |
| H3 (transitional): OLS with bounded sample | In use pending a better extrapolation model |

---

## Architecture

```
MarketDataFramework/
├── Core/
│   ├── Interfaces/          # IMarketDataFeed, IStochasticClock, IExtrapolationStrategy,
│   │                        # IBasketEngine, IMarketDataDistributor
│   └── Models/              # Instrument, MarketDataTick, BasketDefinition,
│                            # PopulationWindow, BasketValuation, MarketDataSnapshot
├── StochasticClock/
│   ├── StochasticClockService.cs    # Adaptive window manager (Box-Muller sampling)
│   ├── MarketSession.cs             # Session boundaries + tick rate parameters
│   └── PopulationWindowBuilder.cs  # Fluent builder for testing / replay
├── BasketEngine/
│   ├── WeightedAverageCalculator.cs # Two-pass: arithmetic mean → weighted average
│   ├── PopulationAggregator.cs      # Tick → price map + extrapolation dispatch
│   └── BasketValuationEngine.cs     # Orchestrator + jump detection
├── Extrapolation/
│   ├── LastKnownValueStrategy.cs    # CIR H1 — baseline / last resort
│   ├── LinearRegressionStrategy.cs  # CIR H2 — OLS over rolling window
│   └── ExtrapolationContext.cs      # Strategy chain (Strategy pattern)
├── Distribution/
│   ├── SubscriberRegistry.cs        # Thread-safe consumer registry
│   ├── MarketDataPublisher.cs       # Async dispatch via ThreadPool
│   └── MarketDataDistributor.cs     # Top-level IMarketDataDistributor
├── Infrastructure/
│   ├── Feeds/
│   │   ├── BloombergFeedAdapter.cs  # B-PIPE / SAPI wrapper + simulation
│   │   ├── ReutersFeedAdapter.cs    # RMDS / Elektron wrapper + simulation
│   │   └── FeedAggregator.cs       # Multi-feed deduplication (10ms window)
│   └── Persistence/
│       ├── DmdsRepository.cs        # XML-based internal store (primary)
│       └── OracleRepository.cs      # Oracle UAT cross-validation store
├── MarketDataFrameworkHost.cs       # Pipeline wire-up + lifecycle management
├── UsageExample.cs                  # End-to-end usage demonstration
└── MarketDataFramework.csproj
```

---

## Pipeline

```
[Bloomberg Feed] ──┐
                   ├──► [FeedAggregator] ──► [StochasticClock]
[Reuters Feed]  ──┘       (dedup 10ms)       (adaptive windows)
                                                    │
                                          on WindowClosed
                                                    │
                                                    ▼
                                       [BasketValuationEngine]
                                          PopulationAggregator
                                            → mean per instrument
                                            → ExtrapolationContext (missing)
                                          WeightedAverageCalculator
                                            → weighted average per basket
                                          Jump detection (bps)
                                                    │
                                                    ▼
                                       [MarketDataDistributor]
                                          async dispatch (ThreadPool)
                                          ├── Trading System
                                          ├── Risk Engine
                                          └── Market Ops Dashboard
                                                    │
                                          [DmdsRepository]     (XML)
                                          [OracleRepository]   (UAT)
```

**Latency budget:** window close → subscriber delivery < 3 seconds.  
**In practice:** 100–300ms for 10-instrument baskets on a major international bank compute grid.

---

## Performance Targets (per CIR)

| Metric | Target | Achieved |
|---|---|---|
| End-to-end latency | < 3 seconds | ~100–300ms |
| Suspect jumps (> 2bps) | < 0.01% of valuations | 0.0001% |
| Model convergence (H3 discrete) | > 99.99% within 1bp | 99.99% |
| Population completeness | > 85% per window | ~70–90% depending on session |

---

## Key Design Decisions

**Stochastic clock over fixed timer**  
A fixed 100ms timer cannot adapt to market activity. During Asian pre-market (~20 ticks/s), a 100ms window is too wide and introduces unnecessary latency. During EU-US overlap (~500 ticks/s), it is too narrow and creates degenerate populations. The stochastic clock samples window duration from a session-parameterised distribution.

**Two-pass aggregation**  
Pass 1: arithmetic mean of all intra-window ticks per instrument (reduces N ticks → 1 representative price). Pass 2: weighted average across instruments. This matches the govie desk requirement: one basket price per 100ms cycle.

**Extrapolation chain (Strategy pattern)**  
Extrapolation strategies are pluggable and chained. The preferred strategy (linear regression) runs first; if it cannot produce a value (insufficient history), the last-known-value strategy acts as a fallback of last resort. A null result causes the instrument to be excluded from the basket price, which is then flagged as incomplete.

**Jump detection in basis points**  
Government bond desks work in basis points (1bp = 0.0001). Jumps exceeding 2bps between consecutive valuations are flagged as suspect and logged. This matches the CIR acceptance criterion: suspect rate must remain below 0.01%.

---

## Usage

```csharp
// Define basket
var basket = new BasketDefinition("GOVIE_EU_5", "EU Sovereign Basket", "EUR")
{
    MaxJumpBps = 2.0
};
basket.AddInstrument("FR0010070060", 50.0); // OAT France
basket.AddInstrument("DE0001102309", 60.0); // Bund Germany
// ...

// Build host
using (var host = MarketDataFrameworkHost.CreateDefault(@"C:\DMDS\MDF"))
{
    host.RegisterBasket(basket)
        .RegisterSubscriber("TradingSystem", snapshot =>
        {
            foreach (var v in snapshot.Valuations)
                Console.WriteLine("{0} → {1:F6}", v.BasketId, v.WeightedAverage);
        });

    host.Start();

    // ... run until shutdown signal ...

    host.Stop();
}
```

---

## Dependencies

| Dependency | Purpose | Notes |
|---|---|---|
| .NET Framework 4.8 | Runtime | No additional NuGet packages required for core logic |
| Bloomberg BLPAPI | Real-time feed | Proprietary — replace stub in `BloombergFeedAdapter` |
| Reuters RMDS API | Real-time feed | Proprietary — replace stub in `ReutersFeedAdapter` |
| Oracle.DataAccess (ODP.NET) | UAT persistence | Proprietary — replace stub in `OracleRepository` |

The framework compiles and runs without any proprietary dependency. Feed adapters include a built-in simulation mode that fires synthetic ticks, allowing full pipeline testing without Bloomberg or Reuters connectivity.

---

## Persistence

### DMDS (primary)
XML-based internal a major international bank datastore. One file per basket per trading day:
```
C:\DMDS\MDF\
  GOVIE_EU_5\
    DMDS_20140310.xml
    DMDS_20140311.xml
    ...
```

### Oracle (UAT cross-validation)
Two tables: `MDF_BASKET_VALUATION` (header) and `MDF_INSTRUMENT_PRICE` (per-instrument detail). Used for statistical reporting and model validation — not on the real-time critical path.

---

## Known Limitations

1. **EU-US overlap degradation**: The stochastic clock distribution parameters are not calibrated for the sudden tick rate surge after US market open (14h00 CET). The model underperforms for ~60–90 minutes. Active research area.

2. **Extrapolation quality**: No extrapolation method tested during the research period produced results acceptable for a govie desk in all market conditions. The OLS regression remains a transitional solution. Jump-diffusion models from the commodities domain were attempted but convergence below 99% was never achieved.

3. **Holiday calendar**: The stochastic clock does not adjust for public holidays across the 26 jurisdictions. A market with fewer active participants due to a local holiday is treated identically to a normal trading day.

4. **Single-threaded valuation**: Basket valuation is single-threaded per window. For a large number of baskets (>50) this could become a bottleneck. Parallelisation via `Parallel.ForEach` or PLINQ is a straightforward extension.

---

## Research References

- Black, F. & Scholes, M. (1973). *The Pricing of Options and Corporate Liabilities*
- Hull, J. & White, A. (1990). *Pricing Interest Rate Derivative Securities*
- Cox, J., Ingersoll, J. & Ross, S. (1985). *A Theory of the Term Structure of Interest Rates*
- Carr, P. & Wu, L. (2004). *Time-Changed Lévy Processes and Option Pricing*

---

*Vincent Louis — ELYNOS / a major international bank — CIR 2013, 2014, 2015 S1*
