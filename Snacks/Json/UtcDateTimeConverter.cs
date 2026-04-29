namespace Snacks.Json;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Forces every <see cref="DateTime"/> serialised by the API and SignalR
///     pipelines to be emitted as UTC ISO-8601 with the <c>Z</c> suffix.
///
///     <para>SQLite stores <see cref="DateTime"/> as TEXT and EF Core hands
///     values back with <see cref="DateTimeKind.Unspecified"/>. The default
///     System.Text.Json output for those values has no timezone marker, so
///     <c>new Date()</c> in the browser interprets them as local time — the
///     dashboard's relative-time labels then drift by the user's timezone
///     offset (e.g. "-18000s ago" for a CDT/UTC five-hour gap).</para>
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc   => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc   => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }
}

/// <summary> Nullable-DateTime variant of <see cref="UtcDateTimeConverter"/>. </summary>
public sealed class NullableUtcDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc   => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        var v = value.Value;
        var utc = v.Kind switch
        {
            DateTimeKind.Utc   => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }
}
