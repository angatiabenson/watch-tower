using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;
using WatchTower.Configuration;
using WatchTower.Services.Abstractions;

namespace WatchTower.Services;

public class EmailService : IEmailService
{
    private readonly IOptionsMonitor<EmailOptions> _options;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptionsMonitor<EmailOptions> options, ILogger<EmailService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task SendEmailAsync(string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(opts.SmtpHost))
        {
            _logger.LogWarning("SMTP host is not configured. Email will not be sent.");
            return;
        }

        if (opts.ToAddresses.Count == 0)
        {
            _logger.LogWarning("No recipient addresses configured. Email will not be sent.");
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(opts.FromName, opts.FromAddress));
        foreach (var to in opts.ToAddresses)
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(opts.SmtpHost, opts.SmtpPort, opts.UseSsl, cancellationToken);

            if (!string.IsNullOrWhiteSpace(opts.Username))
                await client.AuthenticateAsync(opts.Username, opts.Password, cancellationToken);

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email: {Subject}", subject);
        }
    }
}
