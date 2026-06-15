namespace WatchTower.Services.Abstractions;

public interface IEmailService
{
    /// <summary>
    /// Sends an HTML email. Returns <c>true</c> only if the message was actually
    /// delivered to the SMTP server; <c>false</c> if sending was skipped (not
    /// configured) or failed. Callers must not treat <c>false</c> as success
    /// (e.g. must not purge data on the strength of a send that did not happen).
    /// </summary>
    Task<bool> SendEmailAsync(string subject, string htmlBody, CancellationToken cancellationToken = default);
}
