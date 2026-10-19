using System.Globalization;
using Microsoft.JSInterop;

namespace SqlDam.Web.Services;

public static class BrowserDateTimeConversion
{
    public static async ValueTask<DateTime?> ToUtcAsync(IJSRuntime javascript, DateTime? localDateTime)
    {
        if (!localDateTime.HasValue) return null;

        var localValue = DateTime.SpecifyKind(localDateTime.Value, DateTimeKind.Unspecified)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var unixMilliseconds = await javascript.InvokeAsync<long?>(
            "sqlDamBrowserDateTime.toUtcMilliseconds", localValue);
        return unixMilliseconds.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds.Value).UtcDateTime
            : null;
    }
}