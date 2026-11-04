namespace WatchTower.Services.Abstractions;

public interface IEmailService
{
    /// <summary>
    /// <c>true</c> when the SMTP host, sender and at least one recipient are configured, i.e. a
    /// send could possibly succeed. Callers use this to skip building email content (e.g. the
    /// 24h emergency report) that would only be thrown away.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends an HTML email. Returns <c>true</c> only if the message was actually
    /// delivered to the SMTP server; <c>false</c> if sending was skipped (not
    /// configured) or failed. Callers must not treat <c>false</c> as success
    /// (e.g. must not purge data on the strength of a send that did not happen).
    /// </summary>
    Task<bool> SendEmailAsync(string subject, string htmlBody, CancellationToken cancellationToken = default);
}