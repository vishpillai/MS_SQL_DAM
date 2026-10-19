namespace SqlDam.Web.Services;

/// <summary>
/// Shared T-SQL fragment that derives the SQL Server default log directory from
/// SERVERPROPERTY('ErrorLogFileName'). Used to locate both the Extended Events
/// file target and the SQL Server Audit file target, which are written there
/// when no explicit path is given.
/// </summary>
internal static class SqlServerLogPath
{
    public const string DeclareLogDirectorySql = """
        DECLARE @errorLog nvarchar(260) = CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(260));
        DECLARE @logDir nvarchar(260) = LEFT(@errorLog, LEN(@errorLog) - CHARINDEX(N'\', REVERSE(@errorLog)) + 1);
        """;
}
