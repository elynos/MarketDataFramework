using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using MarketDataFramework.Core.Models;

namespace MarketDataFramework.Infrastructure.Persistence
{
    /// <summary>
    /// Repository targeting the Oracle UAT database used during the CIR research.
    /// 
    /// Per CIR: "une base oracle qui était par contre une base d'UAT donc moins puissante".
    /// This was a secondary persistence target, used for cross-validation and reporting
    /// rather than as the primary real-time store (DMDS filled that role).
    /// 
    /// Schema (tables created by the DBA, not by this code):
    /// 
    ///   MDF_BASKET_VALUATION
    ///     VALUATION_ID       VARCHAR2(36)   PK
    ///     BASKET_ID          VARCHAR2(50)
    ///     VALUATION_TIME     TIMESTAMP
    ///     WEIGHTED_AVG       NUMBER(20,8)
    ///     JUMP_BPS           NUMBER(20,8)
    ///     IS_JUMP_SUSPECT    NUMBER(1)       -- 0/1 boolean
    ///     COMPLETENESS_RATIO NUMBER(5,4)
    ///     WINDOW_ID          VARCHAR2(36)
    ///     EXTRAPOLATED_COUNT NUMBER(5)
    ///     CREATED_AT         TIMESTAMP DEFAULT SYSDATE
    /// 
    ///   MDF_INSTRUMENT_PRICE
    ///     VALUATION_ID       VARCHAR2(36)   FK -> MDF_BASKET_VALUATION
    ///     ISIN               VARCHAR2(12)
    ///     PRICE              NUMBER(20,8)
    ///     IS_EXTRAPOLATED    NUMBER(1)
    /// 
    /// NOTE: ODP.NET (Oracle.DataAccess) is a proprietary dependency not included here.
    /// The repository is written against System.Data interfaces (IDbConnection,
    /// IDbCommand) so it can be adapted to any ADO.NET provider.
    /// Replace the connection factory with your actual ODP.NET / OracleConnection call.
    /// </summary>
    public class OracleRepository
    {
        private readonly string _connectionString;
        private readonly IDbConnectionFactory _connectionFactory;

        public OracleRepository(string connectionString)
            : this(connectionString, new DefaultConnectionFactory())
        { }

        internal OracleRepository(string connectionString, IDbConnectionFactory factory)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.",
                                            "connectionString");
            _connectionString  = connectionString;
            _connectionFactory = factory;
        }

        // ── Write ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Inserts a basket valuation (header + instrument prices) in a single transaction.
        /// </summary>
        public void Save(BasketValuation valuation)
        {
            if (valuation == null) throw new ArgumentNullException("valuation");

            using (IDbConnection conn = _connectionFactory.Create(_connectionString))
            {
                conn.Open();
                using (IDbTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        string valuationId = Guid.NewGuid().ToString();
                        InsertHeader(conn, tx, valuationId, valuation);
                        InsertPrices(conn, tx, valuationId, valuation);
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Bulk insert using a single transaction — significantly faster than
        /// individual Save() calls for batch end-of-window persistence.
        /// </summary>
        public void SaveAll(IEnumerable<BasketValuation> valuations)
        {
            if (valuations == null) throw new ArgumentNullException("valuations");

            using (IDbConnection conn = _connectionFactory.Create(_connectionString))
            {
                conn.Open();
                using (IDbTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        foreach (var v in valuations)
                        {
                            string vid = Guid.NewGuid().ToString();
                            InsertHeader(conn, tx, vid, v);
                            InsertPrices(conn, tx, vid, v);
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        // ── Read ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieves all valuations for a basket between two timestamps.
        /// </summary>
        public IReadOnlyList<BasketValuation> LoadByRange(string basketId,
                                                           DateTime from,
                                                           DateTime to)
        {
            const string sql = @"
                SELECT VALUATION_ID, BASKET_ID, VALUATION_TIME, WEIGHTED_AVG,
                       JUMP_BPS, IS_JUMP_SUSPECT, COMPLETENESS_RATIO, WINDOW_ID,
                       EXTRAPOLATED_COUNT
                FROM   MDF_BASKET_VALUATION
                WHERE  BASKET_ID = :p_basket_id
                  AND  VALUATION_TIME BETWEEN :p_from AND :p_to
                ORDER  BY VALUATION_TIME ASC";

            var results = new List<BasketValuation>();

            using (IDbConnection conn = _connectionFactory.Create(_connectionString))
            {
                conn.Open();
                using (IDbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    AddParameter(cmd, "p_basket_id", basketId);
                    AddParameter(cmd, "p_from",      from);
                    AddParameter(cmd, "p_to",        to);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(MapHeader(reader));
                        }
                    }
                }
            }

            return results.AsReadOnly();
        }

        /// <summary>
        /// Returns the most recent valuation for a basket.
        /// Used for startup state recovery (last known basket price).
        /// </summary>
        public BasketValuation LoadLatest(string basketId)
        {
            const string sql = @"
                SELECT VALUATION_ID, BASKET_ID, VALUATION_TIME, WEIGHTED_AVG,
                       JUMP_BPS, IS_JUMP_SUSPECT, COMPLETENESS_RATIO, WINDOW_ID,
                       EXTRAPOLATED_COUNT
                FROM   MDF_BASKET_VALUATION
                WHERE  BASKET_ID = :p_basket_id
                  AND  ROWNUM    = 1
                ORDER  BY VALUATION_TIME DESC";

            using (IDbConnection conn = _connectionFactory.Create(_connectionString))
            {
                conn.Open();
                using (IDbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    AddParameter(cmd, "p_basket_id", basketId);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        return reader.Read() ? MapHeader(reader) : null;
                    }
                }
            }
        }

        // ── Statistics ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the percentage of suspect jumps for a basket on a given date.
        /// Used to validate model performance against the CIR threshold:
        /// suspect jumps must remain below 0.01% (1 in 10,000 valuations).
        /// </summary>
        public double GetSuspectJumpRatio(string basketId, DateTime date)
        {
            const string sql = @"
                SELECT COUNT(*),
                       SUM(IS_JUMP_SUSPECT)
                FROM   MDF_BASKET_VALUATION
                WHERE  BASKET_ID      = :p_basket_id
                  AND  TRUNC(VALUATION_TIME) = TRUNC(:p_date)";

            using (IDbConnection conn = _connectionFactory.Create(_connectionString))
            {
                conn.Open();
                using (IDbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    AddParameter(cmd, "p_basket_id", basketId);
                    AddParameter(cmd, "p_date",      date);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return 0.0;
                        double total   = reader.IsDBNull(0) ? 0 : Convert.ToDouble(reader[0]);
                        double suspect = reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader[1]);
                        return total == 0 ? 0.0 : suspect / total;
                    }
                }
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void InsertHeader(IDbConnection conn, IDbTransaction tx,
                                         string valuationId, BasketValuation v)
        {
            const string sql = @"
                INSERT INTO MDF_BASKET_VALUATION
                    (VALUATION_ID, BASKET_ID, VALUATION_TIME, WEIGHTED_AVG,
                     JUMP_BPS, IS_JUMP_SUSPECT, COMPLETENESS_RATIO,
                     WINDOW_ID, EXTRAPOLATED_COUNT)
                VALUES
                    (:p_id, :p_basket, :p_time, :p_wavg,
                     :p_jump, :p_suspect, :p_ratio,
                     :p_window, :p_extrap)";

            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                AddParameter(cmd, "p_id",      valuationId);
                AddParameter(cmd, "p_basket",  v.BasketId);
                AddParameter(cmd, "p_time",    v.ValuationTime);
                AddParameter(cmd, "p_wavg",    v.WeightedAverage);
                AddParameter(cmd, "p_jump",    v.JumpBps.HasValue
                                               ? (object)v.JumpBps.Value : DBNull.Value);
                AddParameter(cmd, "p_suspect", v.IsJumpSuspect ? 1 : 0);
                AddParameter(cmd, "p_ratio",   v.CompletenessRatio);
                AddParameter(cmd, "p_window",  v.PopulationWindowId.ToString());
                AddParameter(cmd, "p_extrap",  v.ExtrapolatedIsins.Count);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertPrices(IDbConnection conn, IDbTransaction tx,
                                          string valuationId, BasketValuation v)
        {
            const string sql = @"
                INSERT INTO MDF_INSTRUMENT_PRICE
                    (VALUATION_ID, ISIN, PRICE, IS_EXTRAPOLATED)
                VALUES
                    (:p_vid, :p_isin, :p_price, :p_extrap)";

            foreach (var kvp in v.InstrumentPrices)
            {
                using (IDbCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    AddParameter(cmd, "p_vid",    valuationId);
                    AddParameter(cmd, "p_isin",   kvp.Key);
                    AddParameter(cmd, "p_price",  kvp.Value);
                    AddParameter(cmd, "p_extrap", v.ExtrapolatedIsins.Contains(kvp.Key) ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static BasketValuation MapHeader(IDataReader reader)
        {
            var v = new BasketValuation
            {
                BasketId           = reader["BASKET_ID"].ToString(),
                ValuationTime      = Convert.ToDateTime(reader["VALUATION_TIME"]),
                WeightedAverage    = Convert.ToDouble(reader["WEIGHTED_AVG"]),
                IsJumpSuspect      = Convert.ToInt32(reader["IS_JUMP_SUSPECT"]) == 1,
                CompletenessRatio  = Convert.ToDouble(reader["COMPLETENESS_RATIO"]),
                PopulationWindowId = Guid.Parse(reader["WINDOW_ID"].ToString())
            };

            if (reader["JUMP_BPS"] != DBNull.Value)
                v.JumpBps = Convert.ToDouble(reader["JUMP_BPS"]);

            return v;
        }

        private static void AddParameter(IDbCommand cmd, string name, object value)
        {
            var p   = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value         = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        // ── Connection factory (testable abstraction) ─────────────────────────

        public interface IDbConnectionFactory
        {
            IDbConnection Create(string connectionString);
        }

        /// <summary>
        /// Default factory — returns a stub connection.
        /// Replace with OracleConnectionFactory when ODP.NET is available.
        /// </summary>
        private class DefaultConnectionFactory : IDbConnectionFactory
        {
            public IDbConnection Create(string connectionString)
            {
                // Production replacement:
                // return new Oracle.DataAccess.Client.OracleConnection(connectionString);
                throw new NotImplementedException(
                    "Replace DefaultConnectionFactory with OracleConnectionFactory " +
                    "backed by Oracle.DataAccess.Client.OracleConnection.");
            }
        }
    }
}
