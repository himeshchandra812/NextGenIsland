using System.Collections.ObjectModel;
using NextGenIsland.Models;

namespace NextGenIsland.Core;

/// <summary>
/// Central catalogue of NextGen Island cards. Each card is one tab in the island.
/// IDs are permanent identifiers (MainWindow.Modules.cs maps tabs to them).
/// </summary>
public static class IslandCardCatalog
{
    private static readonly ReadOnlyCollection<IslandCardDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            new IslandCardDefinition("media.player", "Media",
                "Now playing, controls, seeking and album-adaptive colours.",
                IslandCardCategory.Media, 10, IslandCardStatus.Available, true),

            new IslandCardDefinition("system.notifications", "Notifications",
                "Banner alerts and notification history.",
                IslandCardCategory.System, 20, IslandCardStatus.Available, true),

            new IslandCardDefinition("productivity.clipboard", "Clipboard",
                "Clipboard history with search and pinning.",
                IslandCardCategory.Productivity, 30, IslandCardStatus.Available, true),

            new IslandCardDefinition("system.monitor", "System Monitor",
                "CPU, RAM, GPU, battery and network.",
                IslandCardCategory.System, 40, IslandCardStatus.Available, true),

            new IslandCardDefinition("productivity.file-shelf", "File Shelf",
                "Drop files on the island to hold them; drag them out later.",
                IslandCardCategory.Productivity, 50, IslandCardStatus.Available, true),

            new IslandCardDefinition("productivity.reminders", "Reminders",
                "Timed reminders with a due time and repeat options.",
                IslandCardCategory.Productivity, 60, IslandCardStatus.Partial, true),

            new IslandCardDefinition("developer.ai-agents", "AI Agent Monitor",
                "Shows whether Claude, Codex, Antigravity and Hermes processes are running.",
                IslandCardCategory.Developer, 70, IslandCardStatus.Partial, true),

            new IslandCardDefinition("widgets.tools", "Widgets",
                "Calendar, focus timer, weather and CPU/RAM stats.",
                IslandCardCategory.Productivity, 80, IslandCardStatus.Partial, true),

            new IslandCardDefinition("web.drawer", "Web Drawer",
                "Pinned sites that open in your default browser.",
                IslandCardCategory.Productivity, 90, IslandCardStatus.Partial, true),

            new IslandCardDefinition("calls.tools", "Calls",
                "Mute your microphone, test speaker and mic, open the camera.",
                IslandCardCategory.Connectivity, 95, IslandCardStatus.Available, true),

            new IslandCardDefinition("stream.tools", "Stream Tools",
                "Streaming helpers.",
                IslandCardCategory.Developer, 110, IslandCardStatus.Planned, false),
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