namespace OpsBridge.Application.Common;

public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset Now { get; }
    TimeZoneInfo LocalTimeZone { get; }
    DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo destinationTimeZone);
    DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo sourceTimeZone, TimeZoneInfo destinationTimeZone);
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset Now => DateTimeOffset.Now;
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    public DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo destinationTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, destinationTimeZone);
    }

    public DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo sourceTimeZone, TimeZoneInfo destinationTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, sourceTimeZone, destinationTimeZone);
    }
}

public sealed class FakeTimeProvider : ITimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset? initialTime = null)
    {
        _utcNow = initialTime ?? DateTimeOffset.UtcNow;
    }

    public DateTimeOffset UtcNow => _utcNow;
    public DateTimeOffset Now => _utcNow.ToLocalTime();
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan amount)
    {
        _utcNow = _utcNow.Add(amount);
    }

    public void SetTime(DateTimeOffset newTime)
    {
        _utcNow = newTime;
    }

    public DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo destinationTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, destinationTimeZone);
    }

    public DateTimeOffset ConvertTime(DateTime dateTime, TimeZoneInfo sourceTimeZone, TimeZoneInfo destinationTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, sourceTimeZone, destinationTimeZone);
    }
}