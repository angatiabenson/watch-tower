namespace WatchTower.Models;

public class ServerReport
{
    public DateTime GeneratedAt { get; set; }
    public TimeSpan ReportPeriod { get; set; }

    public double CpuMin { get; set; }
    public double CpuAvg { get; set; }
    public double CpuMax { get; set; }

    public double RamMin { get; set; }
    public double RamAvg { get; set; }
    public double RamMax { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }

    public List<DiskMetric> DiskSnapshots { get; set; } = new();
    public List<DiskQueueMetric> DiskQueueSnapshots { get; set; } = new();
    public List<NetworkMetric> NetworkSnapshots { get; set; } = new();
    public List<ServiceStatus> ServiceSnapshots { get; set; } = new();
    public List<Incident> Incidents { get; set; } = new();
}
