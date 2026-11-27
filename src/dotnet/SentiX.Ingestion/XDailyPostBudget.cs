namespace SentiX.Ingestion;

public sealed class XDailyPostBudget(int dailyLimit = XOptions.DefaultDailyPostCap)
{
    public int DailyLimit { get; } = dailyLimit >= 0
        ? dailyLimit
        : throw new ArgumentOutOfRangeException(nameof(dailyLimit));

    public DateTimeOffset StartOfDayUtc { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ResetsAtUtc => StartOfDayUtc.AddDays(1);
    public bool IsExhausted => PostsSeen >= DailyLimit;
    public int PostsSeen { get; private set; }

    public void StartDay(DateTimeOffset utcNow, int alreadyIngested)
    {
        StartOfDayUtc = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);
        PostsSeen = alreadyIngested;
    }

    public void CountPost()
    {
        if (!IsExhausted)
        {
            PostsSeen++;
        }
    }
}
