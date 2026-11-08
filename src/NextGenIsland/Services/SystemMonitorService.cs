using System.Diagnostics;
namespace NextGenIsland.Services;
public sealed record Metrics(double Cpu, double Ram);
public sealed class SystemMonitorService
{
    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total");
    private readonly PerformanceCounter _ram = new("Memory", "% Committed Bytes In Use");
    public SystemMonitorService() { try { _cpu.NextValue(); _ram.NextValue(); } catch { } }
    public async Task<Metrics> ReadAsync()
    {
        await Task.Delay(100);
        double cpu = 0, ram = 0;
        try { cpu = Math.Clamp(_cpu.NextValue(), 0, 100); } catch { }
        try { ram = Math.Clamp(_ram.NextValue(), 0, 100); } catch { }
        return new(cpu, ram);
    }
}
