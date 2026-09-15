using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vestrom.Api.Configuration;
using Vestrom.Api.Models;

namespace Vestrom.Api.Services;

public sealed class SystemMetricsService(IConfiguration configuration, VestromOptions options)
{
    private readonly string _host = options.PublicHostName;

    public async Task<(ServerInfo Server, ResourceBundle Resources, BatteryInfo Battery, double? TemperatureC)> CollectAsync(CancellationToken ct = default)
    {
        var cpu = await ReadCpuAsync(ct);
        var memory = ReadMemory();
        var storage = ReadStorage();
        var uptime = ReadUptime();
        var battery = await ReadBatteryAsync(ct);
        var temperature = battery.TemperatureC ?? ReadThermalTemperature();

        var services = new[]
        {
            new ServiceInfo("Vestrom API", "online", Environment.Version.ToString(), uptime, "ASP.NET Core"),
            new ServiceInfo("PostgreSQL", "unknown", null, null, "Checked separately by PostgreSQL service"),
            new ServiceInfo("SSH", "unknown", null, null, "Host service status is environment-dependent")
        };

        var server = new ServerInfo(
            _host,
            DetectOs(),
            RuntimeInformation.OSArchitecture.ToString(),
            DetectCpuDescription(),
            $"{memory.UsedGb:0.##} / {memory.TotalGb:0.##} GB",
            $"{storage.UsedGb:0.##} / {storage.TotalGb:0.##} GB",
            uptime,
            services);

        var resourceBundle = new ResourceBundle(
            new ResourceMetric(Math.Round(cpu, 1), "%", Math.Round(cpu, 1), new[] { Math.Round(cpu, 1) }),
            new ResourceMetric(Math.Round(memory.UsedGb, 2), "GB", Math.Round(memory.Percent, 1), new[] { Math.Round(memory.UsedGb, 2) }),
            new ResourceMetric(Math.Round(storage.UsedGb, 2), "GB", Math.Round(storage.Percent, 1), new[] { Math.Round(storage.UsedGb, 2) }));

        return (server, resourceBundle, battery, temperature);
    }

    private static string DetectOs()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var pretty = File.ReadLines("/etc/os-release").FirstOrDefault(x => x.StartsWith("PRETTY_NAME="));
                if (pretty is not null) return pretty.Split('=', 2)[1].Trim('"');
            }
        }
        catch { }
        return RuntimeInformation.OSDescription;
    }

    private static string DetectCpuDescription()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (model is not null) return model.Split(':', 2).Last().Trim();
                var hardware = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase));
                if (hardware is not null) return hardware.Split(':', 2).Last().Trim();
            }
        }
        catch { }
        return $"{Environment.ProcessorCount} logical CPU(s)";
    }

    private static string ReadUptime()
    {
        try
        {
            if (File.Exists("/proc/uptime") && double.TryParse(File.ReadAllText("/proc/uptime").Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                var span = TimeSpan.FromSeconds(seconds);
                return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m" : $"{span.Hours}h {span.Minutes}m";
            }
        }
        catch { }
        return "Unknown";
    }

    private static (double TotalGb, double UsedGb, double Percent) ReadMemory()
    {
        try
        {
            var values = File.ReadLines("/proc/meminfo").Select(x => x.Split(':', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1].Trim());
            double kb(string key) => double.Parse(values[key].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
            var total = kb("MemTotal");
            var available = values.ContainsKey("MemAvailable") ? kb("MemAvailable") : kb("MemFree");
            var used = Math.Max(0, total - available);
            return (total / 1024 / 1024, used / 1024 / 1024, used / total * 100);
        }
        catch { return (0, 0, 0); }
    }

    private static (double TotalGb, double UsedGb, double Percent) ReadStorage()
    {
        try
        {
            var root = new DriveInfo(Path.DirectorySeparatorChar.ToString());
            var total = root.TotalSize / 1024d / 1024 / 1024;
            var free = root.AvailableFreeSpace / 1024d / 1024 / 1024;
            var used = Math.Max(0, total - free);
            return (total, used, total > 0 ? used / total * 100 : 0);
        }
        catch { return (0, 0, 0); }
    }

    private static async Task<double> ReadCpuAsync(CancellationToken ct)
    {
        try
        {
            var a = ReadCpuTicks();
            await Task.Delay(250, ct);
            var b = ReadCpuTicks();
            var total = b.total - a.total;
            var idle = b.idle - a.idle;
            return total <= 0 ? 0 : Math.Clamp((total - idle) / total * 100, 0, 100);
        }
        catch { return 0; }
    }

    private static (ulong total, ulong idle) ReadCpuTicks()
    {
        var line = File.ReadLines("/proc/stat").First(x => x.StartsWith("cpu "));
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
        var idle = p.Length > 3 ? p[3] + (p.Length > 4 ? p[4] : 0) : 0;
        return (p.Aggregate(0UL, (a, b) => a + b), idle);
    }

    private async Task<BatteryInfo> ReadBatteryAsync(CancellationToken ct)
    {
        try
        {
            var dirs = Directory.Exists("/sys/class/power_supply") ? Directory.GetDirectories("/sys/class/power_supply") : Array.Empty<string>();
            var battery = dirs.FirstOrDefault(x => Path.GetFileName(x).StartsWith("BAT", StringComparison.OrdinalIgnoreCase));
            if (battery is not null)
            {
                int? percent = int.TryParse(File.ReadAllText(Path.Combine(battery, "capacity")).Trim(), out var p) ? p : null;
                var status = File.Exists(Path.Combine(battery, "status")) ? File.ReadAllText(Path.Combine(battery, "status")).Trim() : "Unknown";
                var temp = ReadSysfsTemp(Path.Combine(battery, "temp"));
                return new BatteryInfo(percent, status, temp);
            }

            if (File.Exists(options.TermuxBatteryCommand))
            {
                using var process = Process.Start(new ProcessStartInfo { FileName = options.TermuxBatteryCommand, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (process is not null)
                {
                    var json = await process.StandardOutput.ReadToEndAsync(ct);
                    await process.WaitForExitAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var percent = root.TryGetProperty("percentage", out var pe) ? pe.GetInt32() : (int?)null;
                    var status = root.TryGetProperty("status", out var se) ? se.GetString() ?? "Unknown" : "Unknown";
                    var temp = root.TryGetProperty("temperature", out var te) ? te.GetDouble() : (double?)null;
                    return new BatteryInfo(percent, status, temp);
                }
            }
        }
        catch { }
        return new BatteryInfo(null, "Unknown", null);
    }

    private static double? ReadThermalTemperature()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                var value = ReadSysfsTemp(Path.Combine(dir, "temp"));
                if (value is not null && value > 0 && value < 120) return value;
            }
        }
        catch { }
        return null;
    }

    private static double? ReadSysfsTemp(string path)
    {
        if (!File.Exists(path)) return null;
        if (!double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return value > 1000 ? value / 1000 : value;
    }
}
