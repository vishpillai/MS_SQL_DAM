using System.Net.Mail;
using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Manages local DAM role assignments for accounts authenticated by OIDC.</summary>
public sealed class DamUserService
{
    private static readonly HashSet<string> AssignableRoles =
        [DamRoles.Admin, DamRoles.Auditor, DamRoles.SuperAdmin];

    private readonly LocalDatabase _database;
    private readonly AuditService _auditService;

    public DamUserService(LocalDatabase database, AuditService auditService)
    {
        _database = database;
        _auditService = auditService;
    }

    public List<DamUserAssignment> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, email, display_name, role, created_at_utc, updated_at_utc
            FROM dam_users
            WHERE is_active = 1
            ORDER BY display_name COLLATE NOCASE, email COLLATE NOCASE
            """;

        var assignments = new List<DamUserAssignment>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) assignments.Add(Map(reader));
        return assignments;
    }

    public DamUserAssignment? FindActiveByEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, email, display_name, role, created_at_utc, updated_at_utc
            FROM dam_users
            WHERE email = $email COLLATE NOCASE AND is_active = 1
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$email", email.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void AssignRole(Guid tenantId, string email, string displayName, string role, string actor)
    {
        (email, displayName, role) = NormalizeAndValidate(email, displayName, role);

        var existing = FindActiveByEmail(email);
        if (existing?.Role == DamRoles.SuperAdmin && role != DamRoles.SuperAdmin && CountSuperAdmins() <= 1)
        {
            throw new InvalidOperationException("The last assigned Super Admin cannot be demoted. Assign another Super Admin first.");
        }

        var now = DateTime.UtcNow.ToString("O");
        var id = existing?.Id ?? Guid.NewGuid();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dam_users (id, email, display_name, role, is_active, created_at_utc, updated_at_utc)
            VALUES ($id, $email, $displayName, $role, 1, $now, $now)
            ON CONFLICT(email) DO UPDATE SET
                display_name = excluded.display_name,
                role = excluded.role,
                is_active = 1,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        var action = existing is null ? "DAM_USER_ROLE_ASSIGNED" : "DAM_USER_ROLE_UPDATED";
        _auditService.Log(tenantId, actor, action, "DamUser", email,
            $"Assigned DAM role '{role}' to '{email}'.");
    }

    public void CreateUser(Guid tenantId, string email, string displayName, string role, string actor)
    {
        (email, displayName, role) = NormalizeAndValidate(email, displayName, role);

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dam_users (id, email, display_name, role, is_active, created_at_utc, updated_at_utc)
            VALUES ($id, $email, $displayName, $role, 1, $now, $now)
            ON CONFLICT(email) DO UPDATE SET
                display_name = excluded.display_name,
                role = excluded.role,
                is_active = 1,
                updated_at_utc = excluded.updated_at_utc
            WHERE dam_users.is_active = 0
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$now", now);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"A DAM user for '{email}' already exists. Edit that user to change their role.");
        }

        _auditService.Log(tenantId, actor, "DAM_USER_CREATED", "DamUser", email,
            $"Created DAM user '{email}' with role '{role}'.");
    }

    /// <summary>Assigns the installer's initial Super Admin once, without reapplying it on later service starts.</summary>
    public bool BootstrapSuperAdmin(string email)
    {
        var normalized = NormalizeAndValidate(email, email, DamRoles.SuperAdmin);
        email = normalized.Email;
        var displayName = normalized.DisplayName;
        var role = normalized.Role;
        var now = DateTime.UtcNow.ToString("O");
        var bootstrapped = false;

        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "SELECT setting_value FROM app_setup WHERE setting_key = 'initial_super_admin_complete'";
                if (string.Equals(state.ExecuteScalar() as string, "1", StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return false;
                }
            }

            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM dam_users WHERE role = $role AND is_active = 1";
                count.Parameters.AddWithValue("$role", DamRoles.SuperAdmin);
                if (Convert.ToInt32(count.ExecuteScalar()) == 0)
                {
                    using var upsert = connection.CreateCommand();
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO dam_users (id, email, display_name, role, is_active, created_at_utc, updated_at_utc)
                        VALUES ($id, $email, $displayName, $role, 1, $now, $now)
                        ON CONFLICT(email) DO UPDATE SET
                            display_name = excluded.display_name,
                            role = excluded.role,
                            is_active = 1,
                            updated_at_utc = excluded.updated_at_utc
                        """;
                    upsert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                    upsert.Parameters.AddWithValue("$email", email);
                    upsert.Parameters.AddWithValue("$displayName", displayName);
                    upsert.Parameters.AddWithValue("$role", role);
                    upsert.Parameters.AddWithValue("$now", now);
                    bootstrapped = upsert.ExecuteNonQuery() > 0;
                }
            }

            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    INSERT INTO app_setup (setting_key, setting_value)
                    VALUES ('initial_super_admin_complete', '1')
                    ON CONFLICT(setting_key) DO UPDATE SET setting_value = '1'
                    """;
                state.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (bootstrapped)
        {
            _auditService.Log(TenantConstants.DefaultTenantId, "installer", "DAM_INITIAL_SUPERADMIN_CREATED",
                "DamUser", email, $"Bootstrapped the initial Super Admin account '{email}' during installation.");
        }

        return bootstrapped;
    }

    public void RemoveRole(Guid tenantId, string email, string actor)
    {
        var existing = FindActiveByEmail(email);
        if (existing is null) return;

        if (existing.Role == DamRoles.SuperAdmin && CountSuperAdmins() <= 1)
        {
            throw new InvalidOperationException("The last assigned Super Admin cannot be removed. Assign another Super Admin first.");
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dam_users SET is_active = 0, updated_at_utc = $updated WHERE email = $email COLLATE NOCASE AND is_active = 1";
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$email", existing.Email);
        command.ExecuteNonQuery();

        _auditService.Log(tenantId, actor, "DAM_USER_ROLE_REMOVED", "DamUser", existing.Email,
            $"Removed the DAM role assignment for '{existing.Email}'.");
    }

    private int CountSuperAdmins()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dam_users WHERE role = $role AND is_active = 1";
        command.Parameters.AddWithValue("$role", DamRoles.SuperAdmin);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static (string Email, string DisplayName, string Role) NormalizeAndValidate(string email, string displayName, string role)
    {
        email = email.Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim();
        role = role.Trim();

        if (!MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a valid account email address.", nameof(email));
        }

        if (!AssignableRoles.Contains(role))
        {
            throw new ArgumentException("Choose Admin, Auditor (read only), or Super Admin.", nameof(role));
        }

        return (email, displayName, role);
    }

    private static DamUserAssignment Map(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
        DateTime.Parse(reader.GetString(5)).ToUniversalTime());
}
