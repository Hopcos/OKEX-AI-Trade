using Microsoft.Data.Sqlite;
using Dvc.Core;

namespace Dvc.Persistence;

/// <summary>
/// SQLite 数据库门面：负责打开连接工厂、WAL 模式、建库建表。
/// 数据库文件物理隔离在局域网内（data/trading.db）。
/// </summary>
public sealed class AppDb : IDisposable
{
    private const string ConnectionStringTemplate =
        "Data Source={0};Mode=ReadWriteCreate;Cache=Shared;Default Timeout=30;Pooling=True";

    public string DbPath { get; }

    public string ConnectionString { get; }

    public AppDb(string dbFilePath)
    {
        DbPath = Path.GetFullPath(dbFilePath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        ConnectionString = string.Format(ConnectionStringTemplate, DbPath);
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>初始化：WAL 模式 + 核心表结构。</summary>
    public void Initialize()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS System_Config (
                    Key          TEXT PRIMARY KEY,
                    Value        TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Asset_Snapshot (
                    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    SnapshotAtUtc  TEXT NOT NULL,
                    Kind           TEXT NOT NULL,
                    InitialCapital REAL NOT NULL,
                    AvailableUsd   REAL NOT NULL,
                    EquityUsd      REAL NOT NULL,
                    RealizedPnl    REAL NOT NULL,
                    MaxOrderSize   REAL NOT NULL,
                    HoldingsJson   TEXT NOT NULL,
                    LastPricesJson TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Trade_Order (
                    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientOrderId    TEXT NOT NULL UNIQUE,
                    Symbol           TEXT NOT NULL,
                    Side             TEXT NOT NULL,
                    Price            REAL NOT NULL,
                    AmountUsd        REAL NOT NULL,
                    BaseAmount       REAL NOT NULL,
                    FilledBaseAmount REAL NOT NULL,
                    FilledAvgPrice   REAL NOT NULL,
                    Status           TEXT NOT NULL,
                    ExchangeOrderId  TEXT,
                    Reason           TEXT NOT NULL,
                    RealizedPnl      REAL NOT NULL,
                    CreatedAtUtc     TEXT NOT NULL,
                    UpdatedAtUtc     TEXT
                );

                CREATE TABLE IF NOT EXISTS Agent_Thought_Log (
                    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId     TEXT NOT NULL,
                    CreatedAtUtc  TEXT NOT NULL,
                    TriggerReason TEXT NOT NULL,
                    RequestJson   TEXT NOT NULL,
                    ThinkingText  TEXT NOT NULL,
                    DecisionJson  TEXT NOT NULL,
                    Diagnosis     TEXT NOT NULL,
                    ActionType    TEXT NOT NULL,
                    RiskVetoed    INTEGER NOT NULL,
                    RiskReason    TEXT NOT NULL,
                    EquityAfter   REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Risk_Intercept (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAtUtc    TEXT NOT NULL,
                    Kind            TEXT NOT NULL,
                    Symbol          TEXT NOT NULL,
                    RequestedAmount REAL NOT NULL,
                    AllowedAmount   REAL NOT NULL,
                    Detail          TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_Trade_Order_Symbol_Status ON Trade_Order(Symbol, Status);
                CREATE INDEX IF NOT EXISTS IX_Thought_CreatedAt ON Agent_Thought_Log(CreatedAtUtc);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() { }
}
