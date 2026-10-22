using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PCHealth.Core;

namespace PCHealth.Ledger;

/// <summary>
/// The durable local record of every mutating operation (spec §6.4, D-005): one SQLite file, written with full
/// synchronous commits so a recorded step survives a crash or restart. Fails closed: a file that is not a ledger, or
/// one written by a newer version, is refused rather than overwritten. A saved transaction's identity, reason,
/// operation, expected effect and recovery terms can never be rewritten; only its progress can be updated.
/// </summary>
public sealed class TransactionLedger : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly SqliteConnection connection;

    private TransactionLedger(SqliteConnection connection) => this.connection = connection;

    public static TransactionLedger Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException($"The ledger needs a full path: '{databasePath}'.", nameof(databasePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidDataException($"The ledger '{databasePath}' was written by a newer version (schema {version}).");
            }

            Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;");
            if (version == 0)
            {
                CreateSchema(connection);
            }
            else
            {
                EnsureSchema(connection, databasePath);
            }

            return new TransactionLedger(connection);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new InvalidDataException($"'{databasePath}' is not a readable ledger: {ex.Message}", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records a new transaction, which must start <see cref="TransactionState.Pending"/> before anything changes, or the
    /// progress of one already recorded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The update tries to change what was recorded about the transaction's purpose or terms.</exception>
    public void Save(LedgerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.EnsureValid();

        using var tx = connection.BeginTransaction();
        var existing = Find(transaction.Id, tx);
        if (existing is not null && !SameTerms(existing, transaction))
        {
            throw new InvalidOperationException($"Transaction {transaction.Id} is already recorded with different terms; history cannot be rewritten.");
        }

        var from = existing?.State;
        if (!CanMove(from, transaction.State))
        {
            throw new InvalidOperationException(
                $"Transaction {transaction.Id} cannot go from {from?.ToString() ?? "nothing"} to {transaction.State}; states run pending, applied, then verified or failed.");
        }

        Execute(
            """
            INSERT INTO transactions (id, created_utc, operation, relates_to, state, reason, checks, expected_bytes, observed_bytes,
                                      recovery_method, recovery_limitations, retention_deadline_utc, outcome)
            VALUES ($id, $created, $operation, $relates, $state, $reason, $checks, $expected, $observed,
                    $method, $limitations, $deadline, $outcome)
            ON CONFLICT (id) DO UPDATE SET state = excluded.state, checks = excluded.checks,
                observed_bytes = excluded.observed_bytes, retention_deadline_utc = excluded.retention_deadline_utc,
                outcome = excluded.outcome;
            DELETE FROM items WHERE transaction_id = $id;
            """,
            tx,
            ("$id", transaction.Id.ToString("D")),
            ("$created", Time(transaction.CreatedUtc)),
            ("$operation", transaction.Operation.ToString()),
            ("$relates", transaction.RelatesTo?.ToString("D")),
            ("$state", transaction.State.ToString()),
            ("$reason", transaction.Reason),
            ("$checks", JsonSerializer.Serialize(transaction.Checks, Json)),
            ("$expected", transaction.ExpectedBytes),
            ("$observed", transaction.ObservedBytes),
            ("$method", transaction.RecoveryMethod.ToString()),
            ("$limitations", transaction.RecoveryLimitations),
            ("$deadline", transaction.RetentionDeadlineUtc is { } d ? Time(d) : null),
            ("$outcome", transaction.Outcome));

        for (var i = 0; i < transaction.Items.Count; i++)
        {
            var item = transaction.Items[i];
            Execute(
                """
                INSERT INTO items (transaction_id, position, original_path, is_directory, before, after, recovery_location, status, error)
                VALUES ($id, $position, $path, $directory, $before, $after, $location, $status, $error);
                """,
                tx,
                ("$id", transaction.Id.ToString("D")),
                ("$position", i),
                ("$path", item.OriginalPath),
                ("$directory", item.IsDirectory ? 1 : 0),
                ("$before", JsonSerializer.Serialize(item.Before, Json)),
                ("$after", item.After is null ? null : JsonSerializer.Serialize(item.After, Json)),
                ("$location", item.RecoveryLocation),
                ("$status", item.Status.ToString()),
                ("$error", item.Error));
        }

        tx.Commit();
    }

    public LedgerTransaction? Find(Guid id) => Find(id, null);

    /// <summary>Every transaction, newest first.</summary>
    public IReadOnlyList<LedgerTransaction> All() => Query("SELECT * FROM transactions ORDER BY created_utc DESC, id;");

    /// <summary>Transactions that follow up <paramref name="id"/>, oldest first.</summary>
    public IReadOnlyList<LedgerTransaction> RelatedTo(Guid id) =>
        Query("SELECT * FROM transactions WHERE relates_to = $id ORDER BY created_utc, id;", ("$id", id.ToString("D")));

    public IReadOnlyList<LedgerTransaction> InState(TransactionState state) =>
        Query("SELECT * FROM transactions WHERE state = $state ORDER BY created_utc, id;", ("$state", state.ToString()));

    /// <summary>
    /// Erases the whole history at the user's request (spec §6.5) and compacts the file so nothing of it is left behind.
    /// Refused while a change is unfinished: that record is how an interruption is resolved.
    /// </summary>
    /// <returns>How many transactions were erased.</returns>
    public int Clear()
    {
        int erased;
        using (var tx = connection.BeginTransaction())
        {
            using var unfinished = Command(
                connection, "SELECT COUNT(*) FROM transactions WHERE state IN ($pending, $applied);", tx,
                ("$pending", nameof(TransactionState.Pending)), ("$applied", nameof(TransactionState.Applied)));
            if (Convert.ToInt64(unfinished.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException("A change is not finished yet; the history cannot be cleared until it is resolved.");
            }

            Execute(connection, "DELETE FROM items;", tx);
            using var delete = Command(connection, "DELETE FROM transactions;", tx);
            erased = delete.ExecuteNonQuery();
            tx.Commit();
        }

        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "VACUUM;");
        return erased;
    }

    public void Dispose() => connection.Dispose();

    private LedgerTransaction? Find(Guid id, SqliteTransaction? tx) =>
        Query("SELECT * FROM transactions WHERE id = $id;", tx, ("$id", id.ToString("D"))).SingleOrDefault();

    private IReadOnlyList<LedgerTransaction> Query(string sql, params (string Name, object? Value)[] parameters) => Query(sql, null, parameters);

    private IReadOnlyList<LedgerTransaction> Query(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        var rows = new List<(string Id, Func<IReadOnlyList<LedgerItem>, LedgerTransaction> Build)>();
        using (var command = Command(connection, sql, tx, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(reader.GetOrdinal("id"));
                var created = ParseTime(reader.GetString(reader.GetOrdinal("created_utc")));
                var operation = Parse<LedgerOperation>(reader.GetString(reader.GetOrdinal("operation")));
                var relatesTo = NullableString(reader, "relates_to") is { } r ? Guid.ParseExact(r, "D") : (Guid?)null;
                var state = Parse<TransactionState>(reader.GetString(reader.GetOrdinal("state")));
                var reason = reader.GetString(reader.GetOrdinal("reason"));
                var checks = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("checks")), Json) ?? [];
                var expected = NullableLong(reader, "expected_bytes");
                var observed = NullableLong(reader, "observed_bytes");
                var method = Parse<RecoveryMethod>(reader.GetString(reader.GetOrdinal("recovery_method")));
                var limitations = reader.GetString(reader.GetOrdinal("recovery_limitations"));
                var deadline = NullableString(reader, "retention_deadline_utc") is { } d ? ParseTime(d) : (DateTimeOffset?)null;
                var outcome = NullableString(reader, "outcome");
                rows.Add((id, items => new LedgerTransaction(
                    Guid.ParseExact(id, "D"), created, operation, state, reason, checks, items, expected, observed, method, limitations, deadline, outcome)
                {
                    RelatesTo = relatesTo,
                }));
            }
        }

        return rows.Select(r => r.Build(Items(r.Id, tx))).ToArray();
    }

    private IReadOnlyList<LedgerItem> Items(string transactionId, SqliteTransaction? tx)
    {
        var items = new List<LedgerItem>();
        using var command = Command(connection, "SELECT * FROM items WHERE transaction_id = $id ORDER BY position;", tx, ("$id", transactionId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new LedgerItem(
                reader.GetString(reader.GetOrdinal("original_path")),
                reader.GetInt64(reader.GetOrdinal("is_directory")) != 0,
                Metadata(reader.GetString(reader.GetOrdinal("before"))),
                NullableString(reader, "after") is { } after ? Metadata(after) : null,
                NullableString(reader, "recovery_location"),
                Parse<ItemStatus>(reader.GetString(reader.GetOrdinal("status"))),
                NullableString(reader, "error")));
        }

        return items;
    }

    /// <summary>Pending → applied → verified, with failed reachable from pending or applied. A state can repeat to record progress.</summary>
    private static bool CanMove(TransactionState? from, TransactionState to) => (from, to) switch
    {
        (null, TransactionState.Pending) => true,
        (null, _) => false,
        var (a, b) when a == b => true,
        (TransactionState.Pending, TransactionState.Applied or TransactionState.Failed) => true,
        (TransactionState.Applied, TransactionState.Verified or TransactionState.Failed) => true,
        _ => false,
    };

    private static bool SameTerms(LedgerTransaction a, LedgerTransaction b) =>
        a.CreatedUtc == b.CreatedUtc
        && a.Operation == b.Operation
        && a.RelatesTo == b.RelatesTo
        && a.Reason == b.Reason
        && a.ExpectedBytes == b.ExpectedBytes
        && a.RecoveryMethod == b.RecoveryMethod
        && a.RecoveryLimitations == b.RecoveryLimitations
        && a.Items.Select(i => (i.OriginalPath, i.IsDirectory, i.Before)).SequenceEqual(b.Items.Select(i => (i.OriginalPath, i.IsDirectory, i.Before)));

    private static void CreateSchema(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        Execute(
            connection,
            $"""
            CREATE TABLE transactions (
                id TEXT PRIMARY KEY NOT NULL,
                created_utc TEXT NOT NULL,
                operation TEXT NOT NULL,
                relates_to TEXT REFERENCES transactions (id),
                state TEXT NOT NULL,
                reason TEXT NOT NULL,
                checks TEXT NOT NULL,
                expected_bytes INTEGER,
                observed_bytes INTEGER,
                recovery_method TEXT NOT NULL,
                recovery_limitations TEXT NOT NULL,
                retention_deadline_utc TEXT,
                outcome TEXT
            );
            CREATE TABLE items (
                transaction_id TEXT NOT NULL REFERENCES transactions (id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                original_path TEXT NOT NULL,
                is_directory INTEGER NOT NULL,
                before TEXT NOT NULL,
                after TEXT,
                recovery_location TEXT,
                status TEXT NOT NULL,
                error TEXT,
                PRIMARY KEY (transaction_id, position)
            );
            CREATE INDEX transactions_state ON transactions (state);
            CREATE INDEX transactions_relates_to ON transactions (relates_to);
            PRAGMA user_version = {SchemaVersion};
            """,
            tx);
        tx.Commit();
    }

    private static void EnsureSchema(SqliteConnection connection, string databasePath)
    {
        var tables = Convert.ToInt64(
            Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('transactions', 'items');"),
            CultureInfo.InvariantCulture);
        if (tables != 2)
        {
            throw new InvalidDataException($"'{databasePath}' is not a ledger: its tables are missing.");
        }
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static T Parse<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"The ledger holds an unknown {typeof(T).Name} '{value}'.");

    private static ItemMetadata Metadata(string json) =>
        JsonSerializer.Deserialize<ItemMetadata>(json, Json) ?? throw new InvalidDataException("The ledger holds empty item metadata.");

    private static long? NullableLong(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt64(reader.GetOrdinal(column));

    private static string? NullableString(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql, null);
        return command.ExecuteScalar();
    }

    private void Execute(string sql, SqliteTransaction tx, params (string Name, object? Value)[] parameters) => Execute(connection, sql, tx, parameters);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, tx, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
