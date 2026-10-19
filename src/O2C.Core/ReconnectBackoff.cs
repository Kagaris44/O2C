namespace O2C.Core;

/// <summary>Задержки между попытками переподключения: 5, 10, 20, 40, 60, 60... секунд.</summary>
public sealed class ReconnectBackoff
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    /// <summary>Сколько нужно продержаться в подключении, чтобы счётчик сбросился.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private int _attempt;

    public int Attempt => _attempt;

    public TimeSpan NextDelay()
    {
        var delay = TimeSpan.FromTicks(Initial.Ticks * (1L << Math.Min(_attempt, 10)));
        _attempt++;
        return delay > Max ? Max : delay;
    }

    public void Reset() => _attempt = 0;
}
