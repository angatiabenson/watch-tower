using System.Text;
using WatchTower.Models;

namespace WatchTower.Templates;

public static class EmailTemplates
{
    private static string GetStatusColor(double percent, double warning, double critical) =>
        percent >= critical ? "#e74c3c"
        : percent >= warning ? "#f39c12"
        : "#27ae60";

    private static string GetCpuStatusLabel(double percent) =>
        percent >= 90 ? "CRITICAL"
        : percent >= 70 ? "HIGH"
        : percent >= 40 ? "MODERATE"
        : "LOW";

    private static string GetRamStatusLabel(double percent) =>
        percent >= 85 ? "CRITICAL"
        : percent >= 70 ? "HIGH"
        : percent >= 40 ? "MODERATE"
        : "LOW";

    public static string RenderReport(ServerReport report, Incident? emergencyIncident)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
</head>
<body style='margin:0;padding:0;background-color:#f4f6f9;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='background-color:#f4f6f9;'>
<tr><td align='center' style='padding:20px 10px;'>
<table role='presentation' width='640' cellpadding='0' cellspacing='0' style='background:#ffffff;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,0.08);overflow:hidden;'>"
        );

        // Header bar
        sb.AppendLine(
            @"<tr><td style='background:linear-gradient(135deg,#1a237e,#0d47a1);padding:24px 30px;'>
<table width='100%' cellpadding='0' cellspacing='0'><tr>
<td><span style='font-family:Segoe UI,Arial,sans-serif;font-size:24px;font-weight:700;color:#ffffff;letter-spacing:0.5px;'>WatchTower</span>
<br><span style='font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:rgba(255,255,255,0.7);'>Server Monitoring Report</span></td>"
        );
        sb.AppendLine(
            $@"<td align='right' style='font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:rgba(255,255,255,0.7);vertical-align:top;'>
{report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC<br>Period: {FormatDuration(report.ReportPeriod)}</td>"
        );
        sb.AppendLine("</tr></table></td></tr>");

        // Emergency banner
        if (emergencyIncident != null)
        {
            sb.AppendLine(
                $@"<tr><td style='padding:0;'>
<table width='100%' cellpadding='0' cellspacing='0' style='background:#fef0f0;border-left:4px solid #e74c3c;'>
<tr><td style='padding:20px 26px;'>
<table width='100%' cellpadding='0' cellspacing='0'><tr>
<td style='width:40px;vertical-align:top;'><span style='font-size:28px;'>&#9888;</span></td>
<td style='font-family:Segoe UI,Arial,sans-serif;'>
<span style='font-size:16px;font-weight:700;color:#c0392b;'>EMERGENCY ALERT</span><br>
<span style='font-size:14px;font-weight:600;color:#333;'>{emergencyIncident.Type}</span><br>
<span style='font-size:13px;color:#555;'>{emergencyIncident.Description}</span><br>
<span style='font-size:11px;color:#999;'>Detected at {emergencyIncident.Timestamp:HH:mm:ss} UTC</span>
</td></tr></table>"
            );

            if (emergencyIncident.TopProcesses is { Count: > 0 })
            {
                sb.AppendLine(
                    @"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:12px;background:#fff;border-radius:4px;border:1px solid #f5c6cb;'>
<tr style='background:#fce4e4;'>
<td style='padding:6px 10px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#7b2c2c;'>PROCESS</td>
<td align='right' style='padding:6px 10px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#7b2c2c;'>PID</td>
<td align='right' style='padding:6px 10px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#7b2c2c;'>CPU TIME</td>
<td align='right' style='padding:6px 10px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#7b2c2c;'>MEMORY</td></tr>"
                );
                foreach (var p in emergencyIncident.TopProcesses)
                {
                    sb.AppendLine(
                        $@"<tr>
<td style='padding:6px 10px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#333;border-top:1px solid #f5e0e0;'>{p.Name}</td>
<td align='right' style='padding:6px 10px;font-family:monospace;font-size:12px;color:#666;border-top:1px solid #f5e0e0;'>{p.Pid}</td>
<td align='right' style='padding:6px 10px;font-family:monospace;font-size:12px;color:#666;border-top:1px solid #f5e0e0;'>{p.CpuSeconds:F1}s</td>
<td align='right' style='padding:6px 10px;font-family:monospace;font-size:12px;color:#666;border-top:1px solid #f5e0e0;'>{p.MemoryMb:F0} MB</td></tr>"
                    );
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</td></tr></table></td></tr>");
        }

        // Content area
        sb.AppendLine("<tr><td style='padding:24px 30px;'>");

        // CPU & RAM side-by-side summary cards
        var cpuColor = GetStatusColor(report.CpuAvg, 70, 90);
        var ramColor = GetStatusColor(report.RamAvg, 70, 85);

        sb.AppendLine(
            $@"<table width='100%' cellpadding='0' cellspacing='0'>
<tr>
<td width='48%' style='vertical-align:top;'>
<table width='100%' cellpadding='0' cellspacing='0' style='border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>CPU Usage</td></tr>
<tr><td style='padding:16px 14px;text-align:center;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:36px;font-weight:700;color:{cpuColor};'>{report.CpuAvg:F0}%</span>
<br><span style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>{GetCpuStatusLabel(report.CpuAvg)}</span>
<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:10px;'>
<tr>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>MIN<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.CpuMin:F1}%</span></td>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>AVG<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.CpuAvg:F1}%</span></td>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>MAX<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.CpuMax:F1}%</span></td>
</tr></table>
</td></tr></table>
</td>
<td width='4%'></td>
<td width='48%' style='vertical-align:top;'>
<table width='100%' cellpadding='0' cellspacing='0' style='border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>RAM Usage</td></tr>
<tr><td style='padding:16px 14px;text-align:center;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:36px;font-weight:700;color:{ramColor};'>{report.RamAvg:F0}%</span>
<br><span style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>{GetRamStatusLabel(report.RamAvg)}</span>
<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:10px;'>
<tr>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>MIN<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.RamMin:F1}%</span></td>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>AVG<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.RamAvg:F1}%</span></td>
<td align='center' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#999;'>MAX<br><span style='font-size:14px;font-weight:600;color:#555;'>{report.RamMax:F1}%</span></td>
</tr></table>
</td></tr></table>
</td>
</tr></table>"
        );

        // Disk Queue & Network Output side-by-side cards
        sb.AppendLine(@"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:20px;'>
<tr>
<td width='48%' style='vertical-align:top;'>
<table width='100%' cellpadding='0' cellspacing='0' style='border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>Network Output Queue</td></tr>
<tr><td style='padding:12px 14px;'>");

        if (report.NetworkSnapshots.Count > 0)
        {
            sb.AppendLine("<table width='100%' cellpadding='0' cellspacing='0'>");
            foreach (var net in report.NetworkSnapshots)
            {
                var netColor = net.OutputQueueLength > 0 ? "#e74c3c" : "#27ae60";
                var statusText = net.OutputQueueLength > 0 ? "SATURATED" : "OK";
                sb.AppendLine($@"<tr>
<td style='padding:6px 0;font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#333;'>{net.InterfaceName}</td>
<td align='right' style='padding:6px 0;font-family:monospace;font-size:12px;color:{netColor};font-weight:600;'>{net.OutputQueueLength:F0}</td>
<td align='right' style='padding:6px 0;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:{netColor};width:60px;'>{statusText}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        else
        {
            sb.AppendLine("<span style='font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#999;'>No data</span>");
        }

        sb.AppendLine(@"</td></tr>
<tr><td style='padding:8px 14px 12px;border-top:1px solid #f0f2f5;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#8896a8;line-height:1.4;'>Packets waiting to be sent by the NIC — any value above 0 means the network adapter is saturated and may cause API timeouts.</span>
</td></tr></table>
</td>
<td width='4%'></td>
<td width='48%' style='vertical-align:top;'>
<table width='100%' cellpadding='0' cellspacing='0' style='border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>Disk Queue Length</td></tr>
<tr><td style='padding:12px 14px;'>");

        if (report.DiskQueueSnapshots.Count > 0)
        {
            sb.AppendLine("<table width='100%' cellpadding='0' cellspacing='0'>");
            foreach (var queue in report.DiskQueueSnapshots)
            {
                var queueColor = queue.QueueLength > 2 ? "#e74c3c" : queue.QueueLength > 1 ? "#f39c12" : "#27ae60";
                sb.AppendLine($@"<tr>
<td style='padding:6px 0;font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#333;'>{queue.InstanceName}</td>
<td align='right' style='padding:6px 0;font-family:monospace;font-size:13px;font-weight:600;color:{queueColor};'>{queue.QueueLength:F1}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        else
        {
            sb.AppendLine("<span style='font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#999;'>No data</span>");
        }

        sb.AppendLine(@"</td></tr>
<tr><td style='padding:8px 14px 12px;border-top:1px solid #f0f2f5;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#8896a8;line-height:1.4;'>I/O requests waiting for the disk — values above 2 indicate the disk cannot keep up with read/write demand from SQL, IIS, or other services.</span>
</td></tr></table>
</td>
</tr></table>");

        // Disk Usage section
        if (report.DiskSnapshots.Count > 0)
        {
            sb.AppendLine(
                @"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:20px;border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>Disk Usage</td></tr>
<tr><td style='padding:0;'>
<table width='100%' cellpadding='0' cellspacing='0'>
<tr style='background:#f8f9fb;'>
<td style='padding:8px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#8896a8;'>DRIVE</td>
<td align='right' style='padding:8px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#8896a8;'>TOTAL</td>
<td align='right' style='padding:8px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#8896a8;'>FREE</td>
<td align='right' style='padding:8px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#8896a8;'>USED</td>
<td width='35%' style='padding:8px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:11px;font-weight:600;color:#8896a8;'></td></tr>"
            );

            foreach (var disk in report.DiskSnapshots)
            {
                var color = GetStatusColor(disk.UsagePercent, 70, 85);
                var barWidth = Math.Min(100, Math.Max(0, (int)disk.UsagePercent));
                sb.AppendLine(
                    $@"<tr style='border-top:1px solid #f0f2f5;'>
<td style='padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:13px;font-weight:600;color:#333;'>{disk.DriveName}</td>
<td align='right' style='padding:10px 14px;font-family:monospace;font-size:12px;color:#666;'>{disk.TotalGb:F1} GB</td>
<td align='right' style='padding:10px 14px;font-family:monospace;font-size:12px;color:#666;'>{disk.FreeGb:F1} GB</td>
<td align='right' style='padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:13px;font-weight:700;color:{color};'>{disk.UsagePercent:F1}%</td>
<td style='padding:10px 14px;'>
<table width='100%' cellpadding='0' cellspacing='0' style='background:#eef0f4;border-radius:10px;height:8px;'><tr>
<td width='{barWidth}%' style='background:{color};border-radius:10px;height:8px;'></td>
<td></td></tr></table>
</td></tr>"
                );
            }
            sb.AppendLine("</table></td></tr></table>");
        }

        // Services section
        if (report.ServiceSnapshots.Count > 0)
        {
            sb.AppendLine(
                @"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:20px;border:1px solid #e8ecf1;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f8f9fb;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#5a6a85;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #e8ecf1;'>Watched Services</td></tr>
<tr><td style='padding:12px 14px;'>
<table width='100%' cellpadding='0' cellspacing='0'>"
            );

            foreach (var svc in report.ServiceSnapshots)
            {
                var dotColor = svc.IsRunning ? "#27ae60" : "#e74c3c";
                var statusText = svc.IsRunning ? "Running" : svc.Status;
                sb.AppendLine(
                    $@"<tr>
<td style='padding:6px 0;font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#333;'>
<span style='display:inline-block;width:10px;height:10px;border-radius:50%;background:{dotColor};margin-right:8px;vertical-align:middle;'></span>{svc.DisplayName}</td>
<td align='right' style='padding:6px 0;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:{dotColor};'>{statusText}</td></tr>"
                );
            }
            sb.AppendLine("</table></td></tr></table>");
        }

        // Incidents section
        if (report.Incidents.Count > 0)
        {
            sb.AppendLine(
                @"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:20px;border:1px solid #f5c6cb;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#fef0f0;padding:10px 14px;font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#7b2c2c;text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid #f5c6cb;'>Incidents</td></tr>
<tr><td style='padding:0;'>
<table width='100%' cellpadding='0' cellspacing='0'>"
            );

            foreach (var incident in report.Incidents)
            {
                sb.AppendLine(
                    $@"<tr style='border-top:1px solid #fce4e4;'>
<td style='padding:10px 14px;width:70px;vertical-align:top;font-family:monospace;font-size:12px;color:#999;white-space:nowrap;'>{incident.Timestamp:HH:mm:ss}</td>
<td style='padding:10px 14px;vertical-align:top;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:12px;font-weight:600;color:#c0392b;'>{FormatIncidentType(incident.Type)}</span><br>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#555;'>{incident.Description}</span></td></tr>"
                );
            }
            sb.AppendLine("</table></td></tr></table>");
        }

        // No incidents message
        if (report.Incidents.Count == 0 && emergencyIncident == null)
        {
            sb.AppendLine(
                @"<table width='100%' cellpadding='0' cellspacing='0' style='margin-top:20px;border:1px solid #c3e6cb;border-radius:6px;overflow:hidden;'>
<tr><td style='background:#f0faf0;padding:14px 14px;text-align:center;'>
<span style='font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#27ae60;font-weight:600;'>&#10003; No incidents during this reporting period</span>
</td></tr></table>"
            );
        }

        // Footer
        sb.AppendLine(
            $@"</td></tr>
<tr><td style='background:#f8f9fb;padding:16px 30px;border-top:1px solid #e8ecf1;'>
<table width='100%' cellpadding='0' cellspacing='0'><tr>
<td style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#aaa;'>WatchTower Monitoring Service</td>
<td align='right' style='font-family:Segoe UI,Arial,sans-serif;font-size:11px;color:#aaa;'>{Environment.MachineName}</td>
</tr></table>
</td></tr>
</table>
</td></tr></table>
</body></html>"
        );

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{span.TotalHours:F1} hours";
        return $"{span.TotalMinutes:F0} minutes";
    }

    private static string FormatIncidentType(IncidentType type) =>
        type switch
        {
            IncidentType.CpuThresholdBreached => "CPU Threshold Breached",
            IncidentType.RamThresholdBreached => "RAM Threshold Breached",
            IncidentType.DiskCritical => "Disk Critical",
            IncidentType.DiskQueueHigh => "Disk Queue High",
            IncidentType.NetworkQueueSaturated => "Network Saturated",
            IncidentType.ServiceDown => "Service Down",
            IncidentType.ServiceRestartFailed => "Service Restart Failed",
            _ => type.ToString(),
        };
}
