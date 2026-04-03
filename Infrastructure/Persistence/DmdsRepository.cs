using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Infrastructure.Persistence
{
    /// <summary>
    /// Repository for the DMDS (internal a major international bank XML-based market data store).
    /// 
    /// DMDS was the primary persistence layer used during the CIR research period.
    /// It stores basket valuations as XML documents, one file per trading day,
    /// partitioned by basket ID.
    /// 
    /// Schema (per valuation record):
    ///   &lt;Valuation&gt;
    ///     &lt;BasketId /&gt;
    ///     &lt;Time /&gt;
    ///     &lt;WeightedAverage /&gt;
    ///     &lt;JumpBps /&gt;
    ///     &lt;IsJumpSuspect /&gt;
    ///     &lt;CompletenessRatio /&gt;
    ///     &lt;ExtrapolatedIsins&gt;
    ///       &lt;Isin /&gt; ...
    ///     &lt;/ExtrapolatedIsins&gt;
    ///     &lt;InstrumentPrices&gt;
    ///       &lt;Price isin="..." value="..." /&gt; ...
    ///     &lt;/InstrumentPrices&gt;
    ///   &lt;/Valuation&gt;
    /// </summary>
    public class DmdsRepository
    {
        private readonly string _rootPath;
        private readonly object _fileLock = new object();

        /// <summary>
        /// Initialises the DMDS repository.
        /// </summary>
        /// <param name="rootPath">
        ///   Root directory for XML files.
        ///   Each basket gets a subdirectory; each trading day gets one XML file.
        /// </param>
        public DmdsRepository(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", "rootPath");

            _rootPath = rootPath;
        }

        // ── Write ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Persists a basket valuation to DMDS.
        /// Appends to the current day's XML file for the given basket.
        /// Thread-safe (file-level lock).
        /// </summary>
        public void Save(BasketValuation valuation)
        {
            if (valuation == null) throw new ArgumentNullException("valuation");

            string filePath = GetFilePath(valuation.BasketId, valuation.ValuationTime);
            XElement record = ToXml(valuation);

            lock (_fileLock)
            {
                XDocument doc = LoadOrCreate(filePath);
                doc.Root.Add(record);
                doc.Save(filePath);
            }
        }

        /// <summary>Bulk save — more efficient than calling Save() in a loop.</summary>
        public void SaveAll(IEnumerable<BasketValuation> valuations)
        {
            if (valuations == null) throw new ArgumentNullException("valuations");

            // Group by (basketId, date) to minimise file open/close cycles
            var groups = valuations.GroupBy(v =>
                new { v.BasketId, Date = v.ValuationTime.Date });

            lock (_fileLock)
            {
                foreach (var group in groups)
                {
                    string    filePath = GetFilePath(group.Key.BasketId,
                                                     group.Key.Date);
                    XDocument doc      = LoadOrCreate(filePath);

                    foreach (var v in group)
                        doc.Root.Add(ToXml(v));

                    doc.Save(filePath);
                }
            }
        }

        // ── Read ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all valuations for a basket on a given trading date.
        /// </summary>
        public IReadOnlyList<BasketValuation> LoadByDate(string basketId, DateTime date)
        {
            string filePath = GetFilePath(basketId, date);
            if (!File.Exists(filePath))
                return new List<BasketValuation>().AsReadOnly();

            XDocument doc;
            lock (_fileLock)
            {
                doc = XDocument.Load(filePath);
            }

            return doc.Root
                      .Elements("Valuation")
                      .Select(FromXml)
                      .ToList()
                      .AsReadOnly();
        }

        /// <summary>
        /// Returns valuations for a basket within a time range.
        /// May span multiple daily files.
        /// </summary>
        public IReadOnlyList<BasketValuation> LoadByRange(string basketId,
                                                           DateTime from,
                                                           DateTime to)
        {
            var results = new List<BasketValuation>();
            for (DateTime date = from.Date; date <= to.Date; date = date.AddDays(1))
            {
                var dayResults = LoadByDate(basketId, date);
                results.AddRange(
                    dayResults.Where(v => v.ValuationTime >= from
                                       && v.ValuationTime <= to));
            }
            return results.AsReadOnly();
        }

        /// <summary>
        /// Returns the last N valuations for a basket (most recent first).
        /// Reads today's file only — for historical lookback use LoadByRange.
        /// </summary>
        public IReadOnlyList<BasketValuation> LoadLatest(string basketId, int count)
        {
            var today = LoadByDate(basketId, DateTime.UtcNow.Date);
            return today
                .OrderByDescending(v => v.ValuationTime)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }

        // ── Maintenance ───────────────────────────────────────────────────────

        /// <summary>
        /// Purges XML files older than <paramref name="retentionDays"/> days.
        /// Should be scheduled as an end-of-day batch job.
        /// </summary>
        public int Purge(int retentionDays)
        {
            DateTime cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
            int      count  = 0;

            if (!Directory.Exists(_rootPath)) return 0;

            foreach (string file in Directory.GetFiles(_rootPath, "*.xml",
                                                        SearchOption.AllDirectories))
            {
                // File naming convention: DMDS_YYYYMMDD.xml
                string name = Path.GetFileNameWithoutExtension(file);
                string[] parts = name.Split('_');
                if (parts.Length < 2) continue;

                DateTime fileDate;
                if (DateTime.TryParseExact(parts[parts.Length - 1], "yyyyMMdd",
                                           null,
                                           System.Globalization.DateTimeStyles.None,
                                           out fileDate))
                {
                    if (fileDate < cutoff)
                    {
                        File.Delete(file);
                        count++;
                    }
                }
            }

            return count;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string GetFilePath(string basketId, DateTime date)
        {
            string safeId  = MakeSafeFileName(basketId);
            string dir     = Path.Combine(_rootPath, safeId);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, string.Format("DMDS_{0:yyyyMMdd}.xml", date));
        }

        private static XDocument LoadOrCreate(string filePath)
        {
            if (File.Exists(filePath))
                return XDocument.Load(filePath);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Valuations"));
        }

        private static XElement ToXml(BasketValuation v)
        {
            var el = new XElement("Valuation",
                new XElement("BasketId",          v.BasketId),
                new XElement("Time",              v.ValuationTime.ToString("o")),
                new XElement("WeightedAverage",   v.WeightedAverage.ToString("R")),
                new XElement("JumpBps",           v.JumpBps.HasValue
                                                  ? v.JumpBps.Value.ToString("R")
                                                  : string.Empty),
                new XElement("IsJumpSuspect",     v.IsJumpSuspect),
                new XElement("CompletenessRatio", v.CompletenessRatio.ToString("R")),
                new XElement("WindowId",          v.PopulationWindowId),
                new XElement("ExtrapolatedIsins",
                    v.ExtrapolatedIsins.Select(i => new XElement("Isin", i))),
                new XElement("InstrumentPrices",
                    v.InstrumentPrices.Select(kvp =>
                        new XElement("Price",
                            new XAttribute("isin",  kvp.Key),
                            new XAttribute("value", kvp.Value.ToString("R")))))
            );
            return el;
        }

        private static BasketValuation FromXml(XElement el)
        {
            var v = new BasketValuation
            {
                BasketId           = (string)el.Element("BasketId"),
                ValuationTime      = DateTime.Parse((string)el.Element("Time")),
                WeightedAverage    = double.Parse((string)el.Element("WeightedAverage")),
                IsJumpSuspect      = bool.Parse((string)el.Element("IsJumpSuspect")),
                CompletenessRatio  = double.Parse((string)el.Element("CompletenessRatio")),
                PopulationWindowId = Guid.Parse((string)el.Element("WindowId"))
            };

            string jumpStr = (string)el.Element("JumpBps");
            if (!string.IsNullOrEmpty(jumpStr))
                v.JumpBps = double.Parse(jumpStr);

            v.ExtrapolatedIsins = el.Element("ExtrapolatedIsins")
                                    ?.Elements("Isin")
                                    .Select(i => (string)i)
                                    .ToList()
                                 ?? new List<string>();

            v.InstrumentPrices = el.Element("InstrumentPrices")
                                   ?.Elements("Price")
                                   .ToDictionary(
                                       p => (string)p.Attribute("isin"),
                                       p => double.Parse((string)p.Attribute("value")))
                               ?? new Dictionary<string, double>();

            return v;
        }

        private static string MakeSafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
