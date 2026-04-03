using System;
using System.Threading;
using MarketDataFramework.Core.Models;
using MarketDataFramework.Infrastructure.Feeds;

namespace MarketDataFramework
{
    /// <summary>
    /// End-to-end usage example demonstrating the Market Data Framework API.
    /// 
    /// Scenario: a major international bank Government Bond desk
    ///   - 1 basket of 5 sovereign bonds (France, Germany, Italy, Spain, UK)
    ///   - Bloomberg + Reuters feeds
    ///   - Stochastic clock driving 100ms population windows
    ///   - Valuations distributed to a trading system subscriber
    ///   - Results persisted to DMDS (XML)
    /// </summary>
    public static class UsageExample
    {
        public static void Run()
        {
            Console.WriteLine("=== Market Data Framework — Usage Example ===");
            Console.WriteLine("a major international bank Government Bond Desk | 2013-2015 Research");
            Console.WriteLine();

            // ── 1. Define the basket ──────────────────────────────────────────
            var govieBasket = new BasketDefinition("GOVIE_EU_5", "EU Sovereign Basket", "EUR")
            {
                ReplicatedIndexId = "EFFAS_EU_5Y",
                MaxJumpBps        = 2.0   // govie desk threshold per CIR
            };

            // 5 sovereign bonds — weights = nominal held (in millions)
            govieBasket.AddInstrument("FR0010070060", 50.0); // OAT France 5Y
            govieBasket.AddInstrument("DE0001102309", 60.0); // Bund Germany 5Y
            govieBasket.AddInstrument("IT0004380546", 30.0); // BTP Italy 5Y
            govieBasket.AddInstrument("ES0000012726", 25.0); // Bono Spain 5Y
            govieBasket.AddInstrument("GB0002404191", 40.0); // Gilt UK 5Y

            Console.WriteLine("Basket: {0}", govieBasket);
            Console.WriteLine();

            // ── 2. Build and configure the host ──────────────────────────────
            using (var host = MarketDataFrameworkHost.CreateDefault(@"C:\DMDS\MDF"))
            {
                host.RegisterBasket(govieBasket);

                // ── 3. Register subscribers ───────────────────────────────────
                host.RegisterSubscriber("TradingSystem", snapshot =>
                {
                    foreach (var v in snapshot.Valuations)
                    {
                        Console.WriteLine(
                            "[Trading] {0} | WAV={1:F6} | Jump={2} | " +
                            "Extrapolated={3} | Completeness={4:P1}",
                            v.BasketId,
                            v.WeightedAverage,
                            v.JumpBps.HasValue ? v.JumpBps.Value.ToString("F4") + "bps" : "n/a",
                            v.ExtrapolatedIsins.Count,
                            v.CompletenessRatio);

                        if (v.IsJumpSuspect)
                            Console.ForegroundColor = ConsoleColor.Yellow;
                        if (v.IsJumpSuspect)
                        {
                            Console.WriteLine(
                                "  *** SUSPECT JUMP: {0:F4}bps (threshold={1}bps) ***",
                                v.JumpBps.Value, govieBasket.MaxJumpBps);
                            Console.ResetColor();
                        }
                    }
                });

                host.RegisterSubscriber("RiskEngine", snapshot =>
                {
                    // Risk engine receives the same snapshot asynchronously
                    // on a separate thread pool item — slow risk calculations
                    // cannot delay the trading system's receipt.
                    foreach (var v in snapshot.Valuations)
                    {
                        // Production: feed into intraday VaR engine
                        _ = v.WeightedAverage; // suppress unused warning
                    }
                });

                // ── 4. Start the pipeline ─────────────────────────────────────
                host.Start();

                Console.WriteLine("Pipeline running. Press Enter to stop...");
                Console.WriteLine("(Feeds simulating ticks every 50-75ms)");
                Console.WriteLine();

                // Run for demonstration — in production this blocks until shutdown signal
                Thread.Sleep(5000);

                // ── 5. Print stats ────────────────────────────────────────────
                Console.WriteLine();
                Console.WriteLine("=== Pipeline Statistics ===");
                Console.WriteLine("Windows processed : {0}", host.WindowsProcessed);
                Console.WriteLine("Valuations produced: {0}", host.ValuationsProduced);
                Console.WriteLine("Suspect jumps     : {0}", host.SuspectJumps);

                if (host.ValuationsProduced > 0)
                {
                    double suspectRatio = (double)host.SuspectJumps / host.ValuationsProduced;
                    Console.WriteLine("Suspect jump ratio: {0:P4} (target < 0.01%)", suspectRatio);

                    if (suspectRatio < 0.0001)
                        Console.WriteLine("✓ Within CIR target threshold.");
                    else
                        Console.WriteLine("✗ Exceeds CIR target threshold — model review required.");
                }

                // ── 6. Stop cleanly ───────────────────────────────────────────
                host.Stop();
            }

            Console.WriteLine("Done.");
        }
    }
}
