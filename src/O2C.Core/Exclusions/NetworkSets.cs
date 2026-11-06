namespace O2C.Core.Exclusions;

/// <summary>
/// Набор подсетей одной компании («@blizzard»): все сети, которые анонсирует её автономная система.
/// В отличие от «@ru», маршруты на весь набор ставятся сразу при подключении — до первого пакета,
/// поэтому игры не теряют соединение при смене адреса.
/// </summary>
/// <param name="Name">Имя после «@».</param>
/// <param name="Title">Название для окна.</param>
/// <param name="Description">Подсказка для окна.</param>
/// <param name="FileName">Файл списка: data\&lt;FileName&gt; рядом со службой и в релизе на GitHub.</param>
/// <param name="MinNetworks">Список короче — считается испорченным.</param>
public sealed record NetworkSet(string Name, string Title, string Description, string FileName, int MinNetworks)
{
    public string Entry => "@" + Name;
}

public static class NetworkSets
{
    /// <summary>Blizzard Entertainment, AS57976: Battle.net и серверы игр (Overwatch, Heroes of the Storm, WoW, Diablo…).</summary>
    public static readonly NetworkSet Blizzard = new(
        "blizzard", "Сети Blizzard",
        "Все сети Blizzard (AS57976): Battle.net и серверы игр. Маршруты ставятся сразу при подключении. "
        + "Серверы, арендованные у облачных провайдеров, сюда не входят.",
        "blizzard-networks.txt", 20);

    public static IReadOnlyList<NetworkSet> All { get; } = [Blizzard];

    public static NetworkSet? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
