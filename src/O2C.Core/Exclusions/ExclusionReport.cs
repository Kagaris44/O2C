namespace O2C.Core.Exclusions;

/// <summary>Состояние одной записи списка исключений.</summary>
public sealed record ExclusionItemStatus
{
    public required string Entry { get; init; }

    /// <summary>Сети, для которых сейчас действует маршрут в обход туннеля.</summary>
    public IReadOnlyList<string> Routes { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>Пояснение без ошибки, например «ждёт обращений к домену».</summary>
    public string? Note { get; init; }
}

/// <summary>Что сейчас применено из списка исключений. Отправляется в GUI.</summary>
public sealed record ExclusionReport
{
    /// <summary>Исключения действуют только при активном подключении.</summary>
    public bool Active { get; init; }

    /// <summary>Общее пояснение: например, «нет подключения» или «не найден основной шлюз».</summary>
    public string? Message { get; init; }

    public IReadOnlyList<ExclusionItemStatus> Items { get; init; } = [];

    /// <summary>Ошибки разбора текста (строки, которые не удалось распознать).</summary>
    public IReadOnlyList<ExclusionError> ParseErrors { get; init; } = [];
}
