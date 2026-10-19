using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public sealed record AuditVerificationResult(
    bool IsValid,
    int TotalRecords,
    long? CompromisedSequence,
    string StatusMessage,
    DateTime VerifiedAtUtc);

/// <summary>
/// Service managing the tamper-evident audit log with cryptographic SHA-256 hash chaining.
/// This detects accidental or partial edits but does not replace externally anchored immutable storage.
/// </summary>
public sealed class AuditService
{
    private readonly LocalDatabase _database;
    private readonly object _lock = new();

    public AuditService(LocalDatabase database)
    {
        _database = database;
    }

    public void Log(
        Guid tenantId,
        string actor,
        string action,
        string entityType,
        string? entityId,
        string details,
        string? clientIp = null)
    {
        lock (_lock)
        {
            using var connection = _database.OpenConnection();
            using var tx = connection.BeginTransaction();

            // Fetch the last record hash for this tenant
            string previousHash = "GENESIS";
            using (var lastHashCmd = connection.CreateCommand())
            {
                lastHashCmd.Transaction = tx;
                lastHashCmd.CommandText = """
                    SELECT record_hash FROM audit_log
                    WHERE tenant_id = $tenantId
                    ORDER BY sequence DESC
                    LIMIT 1
                    """;
                lastHashCmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
                var result = lastHashCmd.ExecuteScalar();
                if (result is not null and not DBNull)
                {
                    previousHash = (string)result;
                }
            }

            var timestampIso = DateTime.UtcNow.ToString("O");
            var recordHash = ComputeHash(previousHash, tenantId, timestampIso, actor, action, entityType, entityId ?? string.Empty, details);

            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO audit_log
                        (tenant_id, timestamp_utc, actor, action, entity_type, entity_id, details, client_ip, previous_hash, record_hash)
                    VALUES
                        ($tenantId, $timestamp, $actor, $action, $entityType, $entityId, $details, $clientIp, $prevHash, $recHash)
                    """;
                insertCmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
                insertCmd.Parameters.AddWithValue("$timestamp", timestampIso);
                insertCmd.Parameters.AddWithValue("$actor", actor);
                insertCmd.Parameters.AddWithValue("$action", action);
                insertCmd.Parameters.AddWithValue("$entityType", entityType);
                insertCmd.Parameters.AddWithValue("$entityId", (object?)entityId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$details", details);
                insertCmd.Parameters.AddWithValue("$clientIp", (object?)clientIp ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$prevHash", previousHash);
                insertCmd.Parameters.AddWithValue("$recHash", recordHash);
                insertCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public List<AuditRecord> GetRecent(Guid tenantId, int take = 100)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sequence, tenant_id, timestamp_utc, actor, action, entity_type, entity_id, details, client_ip, previous_hash, record_hash
            FROM audit_log
            WHERE tenant_id = $tenantId
            ORDER BY sequence DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        cmd.Parameters.AddWithValue("$take", take);

        var list = new List<AuditRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AuditRecord
            {
                Sequence = reader.GetInt64(0),
                TenantId = Guid.Parse(reader.GetString(1)),
                TimestampUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                Actor = reader.GetString(3),
                Action = reader.GetString(4),
                EntityType = reader.GetString(5),
                EntityId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Details = reader.GetString(7),
                ClientIp = reader.IsDBNull(8) ? null : reader.GetString(8),
                PreviousHash = reader.GetString(9),
                RecordHash = reader.GetString(10)
            });
        }

        return list;
    }

    /// <summary>
    /// Performs cryptographic verification across the entire audit log chain for the tenant.
    /// Traverses from the genesis entry to the latest entry, re-hashing each record and validating
    /// that no record was inserted, modified, or deleted out of sequence.
    /// </summary>
    public AuditVerificationResult VerifyIntegrity(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sequence, tenant_id, timestamp_utc, actor, action, entity_type, entity_id, details, previous_hash, record_hash
            FROM audit_log
            WHERE tenant_id = $tenantId
            ORDER BY sequence ASC
            """;
        cmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());

        var expectedPrevHash = "GENESIS";
        var count = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            count++;
            var seq = reader.GetInt64(0);
            var recTenantId = Guid.Parse(reader.GetString(1));
            var timestampIso = reader.GetString(2);
            var actor = reader.GetString(3);
            var action = reader.GetString(4);
            var entityType = reader.GetString(5);
            var entityId = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var details = reader.GetString(7);
            var storedPrevHash = reader.GetString(8);
            var storedRecHash = reader.GetString(9);

            // 1. Verify link to previous block
            if (storedPrevHash != expectedPrevHash)
            {
                return new AuditVerificationResult(
                    IsValid: false,
                    TotalRecords: count,
                    CompromisedSequence: seq,
                    StatusMessage: $"Integrity breach detected at sequence {seq}: previous hash mismatch. Stored: {storedPrevHash[..Math.Min(8, storedPrevHash.Length)]}..., Expected: {expectedPrevHash[..Math.Min(8, expectedPrevHash.Length)]}...",
                    VerifiedAtUtc: DateTime.UtcNow);
            }

            // 2. Verify record hash against payload
            var calculatedHash = ComputeHash(storedPrevHash, recTenantId, timestampIso, actor, action, entityType, entityId, details);
            if (calculatedHash != storedRecHash)
            {
                return new AuditVerificationResult(
                    IsValid: false,
                    TotalRecords: count,
                    CompromisedSequence: seq,
                    StatusMessage: $"Integrity breach detected at sequence {seq}: payload modified. Calculated hash differs from stored record hash.",
                    VerifiedAtUtc: DateTime.UtcNow);
            }

            expectedPrevHash = storedRecHash;
        }

        return new AuditVerificationResult(
            IsValid: true,
            TotalRecords: count,
            CompromisedSequence: null,
            StatusMessage: count == 0
                ? "Audit log is empty (Genesis state)."
                : $"Verified {count} audit records. Cryptographic SHA-256 chain is complete and untampered.",
            VerifiedAtUtc: DateTime.UtcNow);
    }

    private static string ComputeHash(
        string previousHash,
        Guid tenantId,
        string timestampIso,
        string actor,
        string action,
        string entityType,
        string entityId,
        string details)
    {
        var raw = $"{previousHash}|{tenantId}|{timestampIso}|{actor}|{action}|{entityType}|{entityId}|{details}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
