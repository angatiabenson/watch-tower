using System.ComponentModel.DataAnnotations;

namespace WatchTower.Configuration;

public class EmailOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "WatchTower";
    public List<string> ToAddresses { get; set; } = new();

    /// <summary>Per-operation SMTP timeout (connect/auth/send). Bounds how long a hung server can block a worker.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}