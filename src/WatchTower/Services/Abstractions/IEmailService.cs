namespace WatchTower.Services.Abstractions;

public interface IEmailService
{
    Task SendEmailAsync(string subject, string htmlBody, CancellationToken cancellationToken = default);
}
