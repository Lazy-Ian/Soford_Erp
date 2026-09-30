public class ThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ClientIsLockedAfterFiveFailures()
    {
        var clock = new Clock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("1.1.1.1", "boss");
        Assert.Null(throttle.LockedFor("1.1.1.1", "boss"));

        throttle.RecordFailure("1.1.1.1", "boss");
        Assert.NotNull(throttle.LockedFor("1.1.1.1"));

        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(throttle.LockedFor("1.1.1.1"));
    }

    [Fact]
    public void UsernameIsLockedEvenWhenTheAttackerChangesIp()
    {
        var throttle = new LoginThrottle(new Clock());
        for (var i = 0; i < 10; i++) throttle.RecordFailure($"10.0.0.{i}", "Boss");

        Assert.NotNull(throttle.LockedFor("10.0.0.99", "boss"));
        Assert.Null(throttle.LockedFor("10.0.0.99", "someone-else"));
    }

    [Fact]
    public void OldFailuresAreForgotten()
    {
        var clock = new Clock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("1.1.1.1");
        clock.Now += TimeSpan.FromHours(2);

        throttle.RecordFailure("1.1.1.1");
        Assert.Null(throttle.LockedFor("1.1.1.1"));
    }
}
