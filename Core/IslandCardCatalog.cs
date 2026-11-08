using System.Collections.ObjectModel;
using NextGenIsland.Models;

namespace NextGenIsland.Core;

/// <summary>
/// Central catalogue of all NextGen Island cards.
/// IDs are permanent identifiers and should not be changed after release.
/// </summary>
public static class IslandCardCatalog
{
    private static readonly ReadOnlyCollection<IslandCardDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            new IslandCardDefinition(
                "system.cpu", "CPU Monitor",
                "Processor utilization and system load.",
                IslandCardCategory.System, 10,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "system.memory", "Memory Monitor",
                "RAM utilization and memory usage.",
                IslandCardCategory.System, 20,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "system.gpu", "GPU Monitor",
                "GPU utilization, temperature and VRAM.",
                IslandCardCategory.System, 30,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "system.battery", "Battery",
                "Battery percentage and power status.",
                IslandCardCategory.System, 40,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "system.network", "Network",
                "Connection state and transfer speeds.",
                IslandCardCategory.Connectivity, 50,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "media.player", "Media Player",
                "Now playing information and playback controls.",
                IslandCardCategory.Media, 60,
                IslandCardStatus.Available, true),

            new IslandCardDefinition(
                "productivity.clipboard", "Clipboard",
                "Clipboard history, search and pinned items.",
                IslandCardCategory.Productivity, 70,
                IslandCardStatus.Partial, true),

            new IslandCardDefinition(
                "system.notifications", "Notifications",
                "Notification previews and notification history.",
                IslandCardCategory.Productivity, 80,
                IslandCardStatus.Partial, true),

            new IslandCardDefinition(
                "productivity.file-shelf", "File Shelf",
                "Temporary file holding area with drag and drop.",
                IslandCardCategory.Productivity, 90,
                IslandCardStatus.Planned, false),

            new IslandCardDefinition(
                "productivity.reminders", "Reminders",
                "Timers, reminders and scheduled alerts.",
                IslandCardCategory.Productivity, 100,
                IslandCardStatus.Planned, false),

            new IslandCardDefinition(
                "system.quick-controls", "Quick Controls",
                "Fast access to supported Windows controls.",
                IslandCardCategory.System, 110,
                IslandCardStatus.Planned, false),

            new IslandCardDefinition(
                "connectivity.phone", "Phone Connect",
                "Local phone connectivity and file transfer.",
                IslandCardCategory.Connectivity, 120,
                IslandCardStatus.Planned, false),

            new IslandCardDefinition(
                "gaming.performance", "Game Performance",
                "Gaming performance and hardware telemetry.",
                IslandCardCategory.Gaming, 130,
                IslandCardStatus.Planned, false),

            new IslandCardDefinition(
                "developer.ai-agents", "AI Agent Monitor",
                "Activity indicators for supported coding agents.",
                IslandCardCategory.Developer, 140,
                IslandCardStatus.Planned, false)
        });

    public static IReadOnlyList<IslandCardDefinition> All => Definitions;

    public static IslandCardDefinition? Find(string id) =>
        Definitions.FirstOrDefault(
            card => string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IslandCardDefinition> GetByCategory(
        IslandCardCategory category) =>
        Definitions
            .Where(card => card.Category == category)
            .OrderBy(card => card.DefaultOrder)
            .ToArray();

    public static IReadOnlyList<IslandCardDefinition> GetEnabledByDefault() =>
        Definitions
            .Where(card => card.EnabledByDefault)
            .OrderBy(card => card.DefaultOrder)
            .ToArray();
}