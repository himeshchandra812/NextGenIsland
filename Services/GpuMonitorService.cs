using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NextGenIsland.Services;

public sealed record GpuMetrics(
    bool IsAvailable,
    string ModelName,
    double Utilization,
    double Temperature,
    double MemoryUsedMiB,
    double MemoryTotalMiB
)
{
    // Percent of dedicated VRAM used (values in MiB).
    public double MemoryPercent => MemoryTotalMiB > 0 ? Math.Clamp((MemoryUsedMiB / MemoryTotalMiB) * 100.0, 0, 100) : 0;

    // Display in GiB (1 GiB = 1024 MiB).
    public string MemoryDisplay => MemoryTotalMiB > 0
        ? $"{MemoryUsedMiB / 1024.0:0.1} GiB / {MemoryTotalMiB / 1024.0:0.1} GiB"
        : "—";

    public string TempDisplay => IsAvailable ? $"{Temperature:0}°C" : "—";
    public string UtilDisplay => IsAvailable ? $"{Utilization:0}%" : "—";
}

public sealed class GpuMonitorService : IDisposable
{
    private static class NvmlNative
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        public static extern int Init();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int GetHandle(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        public static extern int GetUtilization(IntPtr device, out Utilization util);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern int GetTemperature(IntPtr device, int sensorType, out uint temp);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        public static extern int GetMemory(IntPtr device, out Memory mem);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
        public static extern int GetName(IntPtr device, byte[] name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        public static extern int Shutdown();

        [StructLayout(LayoutKind.Sequential)]
        public struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Memory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }
    }

    private IntPtr _device = IntPtr.Zero;
    private bool _nvmlLoaded;
    private string _modelName = "NVIDIA RTX 5050 Laptop GPU";
    private bool _disposed;

    public GpuMonitorService()
    {
        try
        {
            if (NvmlNative.Init() == 0)
            {
                if (NvmlNative.GetHandle(0, out _device) == 0)
                {
                    _nvmlLoaded = true;
                    var nameBuffer = new byte[96];
                    if (NvmlNative.GetName(_device, nameBuffer, (uint)nameBuffer.Length) == 0)
                    {
                        var name = System.Text.Encoding.ASCII.GetString(nameBuffer).TrimEnd('\0').Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            _modelName = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("NVML init fallback to nvidia-smi: " + ex.Message);
            _nvmlLoaded = false;
        }
    }

    public async Task<GpuMetrics> ReadAsync()
    {
        return await Task.Run(() =>
        {
            if (_nvmlLoaded && _device != IntPtr.Zero)
            {
                try
                {
                    if (NvmlNative.GetUtilization(_device, out var util) == 0 &&
                        NvmlNative.GetTemperature(_device, 0, out var temp) == 0 &&
                        NvmlNative.GetMemory(_device, out var mem) == 0)
                    {
                        double usedMb = mem.Used / (1024.0 * 1024.0);
                        double totalMb = mem.Total / (1024.0 * 1024.0);
                        return new GpuMetrics(true, _modelName, util.Gpu, temp, usedMb, totalMb);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("NVML read error, falling back to nvidia-smi: " + ex.Message);
                }
            }

            return ReadFromNvidiaSmi();
        });
    }

    private GpuMetrics ReadFromNvidiaSmi()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,name --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);

            if (string.IsNullOrWhiteSpace(output))
                return new GpuMetrics(false, "NVIDIA GPU Unavailable", 0, 0, 0, 0);

            var parts = output.Trim().Split(',');
            if (parts.Length >= 4)
            {
                double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double util);
                double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double temp);
                double.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double memUsed);
                double.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double memTotal);
                string name = parts.Length >= 5 ? parts[4].Trim() : _modelName;

                return new GpuMetrics(true, name, util, temp, memUsed, memTotal);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("nvidia-smi error: " + ex.Message);
        }

        return new GpuMetrics(false, "NVIDIA GPU Unavailable", 0, 0, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_nvmlLoaded)
        {
            try { NvmlNative.Shutdown(); } catch { }
        }
    }
}
