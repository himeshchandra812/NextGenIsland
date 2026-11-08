namespace NextGenIsland.Models;

public enum IslandCardCategory
{
    System,
    Productivity,
    Media,
    Connectivity,
    Gaming,
    Developer
}

public enum IslandCardStatus
{
    Available,
    Partial,
    Planned
}

public sealed record IslandCardDefinition(
    string Id,
    string Title,
    string Description,
    IslandCardCategory Category,
    int DefaultOrder,
    IslandCardStatus Status,
    bool EnabledByDefault
);