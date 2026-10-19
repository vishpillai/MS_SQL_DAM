using System.Data.Common;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Creates short-lived ADO.NET connections for each supported source engine.</summary>
public sealed class DatabaseConnectionFactory
{
    public DbConnection Create(DatabaseEngine engine, string connectionString) => engine switch
    {
        DatabaseEngine.SqlServer => new SqlConnection(connectionString),
        DatabaseEngine.PostgreSql => new NpgsqlConnection(connectionString),
        DatabaseEngine.MySql => new MySqlConnection(connectionString),
        DatabaseEngine.Db2 => new DB2Connection(connectionString),
        _ => throw new NotSupportedException($"Database engine '{engine}' is not supported.")
    };
}
