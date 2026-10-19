using System.Globalization;
using System.Data;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Reads new rows from the Extended Events file target and turns them into
/// <see cref="ActivityEvent"/> records. Reading is done by file name/offset
/// bookmark so each poll only touches data written since the last poll,
/// keeping the work done on the monitored instance minimal.
/// </summary>
public sealed class ExtendedEventsReader
{
    public sealed record ReadResult(List<ActivityEvent> Events, string? LastFileName, long? LastFileOffset);

    public async Task<ReadResult> ReadNewEventsAsync(
        SqlConnection connection,
        Guid sourceId,
        string sourceName,
        string? lastFileName,
        long? lastFileOffset,
        CancellationToken cancellationToken,
        int maxRows = 500)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            DECLARE @errorLog nvarchar(260) = CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(260));
            DECLARE @logDir nvarchar(260) = LEFT(@errorLog, LEN(@errorLog) - CHARINDEX(N'\', REVERSE(@errorLog)) + 1);
            DECLARE @pattern nvarchar(400) = @logDir + N'{0}*.xel';

            SELECT TOP (@maxRows) CAST(event_data AS xml) AS event_data, file_name, file_offset
            FROM sys.fn_xe_file_target_read_file(@pattern, NULL, @initialFile, @initialOffset)
            ORDER BY file_name, file_offset;
            """.Replace("{0}", ExtendedEventsSessionManager.SessionName);

        command.Parameters.AddWithValue("@initialFile", (object?)lastFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@initialOffset", (object?)lastFileOffset ?? DBNull.Value);
        command.Parameters.Add("@maxRows", SqlDbType.Int).Value = Math.Clamp(maxRows, 1, 500);

        var events = new List<ActivityEvent>();
        string? newestFileName = lastFileName;
        long? newestFileOffset = lastFileOffset;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var xml = reader.GetString(0);
            var fileName = reader.GetString(1);
            var offset = reader.GetInt64(2);

            // fn_xe_file_target_read_file is inclusive of the bookmark row; skip it so
            // the same event is never processed twice.
            var isBookmarkRow = lastFileName == fileName && lastFileOffset == offset;
            if (!isBookmarkRow)
            {
                var parsed = TryParseXml(xml, sourceId, sourceName);
                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }

            newestFileName = fileName;
            newestFileOffset = offset;
        }

        return new ReadResult(events, newestFileName, newestFileOffset);
    }

    private static ActivityEvent? TryParseXml(string rawXml, Guid sourceId, string sourceName)
    {
        try
        {
            var element = XElement.Parse(rawXml);
            return TryParse(element, sourceId, sourceName, rawXml);
        }
        catch
        {
            // Do not crash the entire poll cycle if one XML payload is malformed
            return null;
        }
    }

    private static ActivityEvent? TryParse(XElement eventElement, Guid sourceId, string sourceName, string rawXml)
    {
        var eventName = eventElement.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(eventName))
        {
            return null;
        }

        var timestampText = eventElement.Attribute("timestamp")?.Value;
        var timestampUtc = DateTime.TryParse(
            timestampText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsedTimestamp)
            ? parsedTimestamp
            : DateTime.UtcNow;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in eventElement.Elements("data").Concat(eventElement.Elements("action")))
        {
            var name = node.Attribute("name")?.Value;
            // Map-valued fields (e.g. object_type) carry both a numeric <value> and a
            // friendly <text> (looked up from sys.dm_xe_map_values); prefer the text.
            var value = node.Element("text")?.Value ?? node.Element("value")?.Value;
            if (!string.IsNullOrEmpty(name) && value is not null)
            {
                fields[name] = value;
            }
        }

        fields.TryGetValue("server_principal_name", out var loginName);
        if (string.IsNullOrEmpty(loginName))
        {
            fields.TryGetValue("nt_username", out loginName);
        }

        fields.TryGetValue("client_hostname", out var clientHost);
        fields.TryGetValue("client_app_name", out var clientApp);
        fields.TryGetValue("database_name", out var databaseName);
        fields.TryGetValue("object_name", out var objectName);

        var (category, summary) = Classify(eventName, fields, loginName, objectName, databaseName);

        return new ActivityEvent
        {
            SourceId = sourceId,
            SourceName = sourceName,
            EventName = eventName,
            Category = category,
            TimestampUtc = timestampUtc,
            LoginName = loginName,
            ClientHostName = clientHost,
            ClientAppName = clientApp,
            DatabaseName = databaseName,
            ObjectName = objectName,
            Summary = summary,
            DetailXml = rawXml
        };
    }

    private static (ActivityCategory Category, string Summary) Classify(
        string eventName,
        IReadOnlyDictionary<string, string> fields,
        string? loginName,
        string? objectName,
        string? databaseName)
    {
        switch (eventName)
        {
            case "login":
                return (ActivityCategory.LoginSuccess, $"{loginName ?? "Unknown login"} connected");

            case "error_reported":
                fields.TryGetValue("error_number", out var errorNumber);
                fields.TryGetValue("message", out var message);
                if (errorNumber == "18456")
                {
                    return (ActivityCategory.LoginFailure, $"Login failed for {loginName ?? "unknown login"}");
                }

                var trimmedMessage = string.IsNullOrEmpty(message) ? "SQL Server reported an error" : Truncate(message, 160);
                return (ActivityCategory.Error, trimmedMessage);

            case "object_created":
            case "object_altered":
            case "object_deleted":
                fields.TryGetValue("object_type", out var objectType);
                var verb = eventName switch
                {
                    "object_created" => "Created",
                    "object_altered" => "Altered",
                    _ => "Dropped"
                };
                return (ActivityCategory.SchemaChange,
                    $"{verb} {objectType ?? "object"} '{objectName ?? "unknown"}' in {databaseName ?? "database"} by {loginName ?? "unknown login"}");

            default:
                return (ActivityCategory.Other, eventName);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
