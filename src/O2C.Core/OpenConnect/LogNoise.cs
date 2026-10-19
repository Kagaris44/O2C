namespace O2C.Core.OpenConnect;

/// <summary>
/// Строки openconnect, которые не несут пользы в окне (пишутся только в файл журнала).
/// </summary>
public static class LogNoise
{
    private static readonly string[] Patterns =
    [
        // UDP (DTLS) не поднялся с первой попытки — openconnect сам продолжает по TCP и пробует снова.
        "DTLS handshake failed: Resource temporarily unavailable",
    ];

    public static bool IsNoise(string line)
    {
        foreach (var p in Patterns)
        {
            if (line.Contains(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
