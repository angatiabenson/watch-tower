using MailKit.Net.Smtp;
using MailKit.Security;

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

    public bool IsConfigured
    {
        get
        {
            var opts = _options.CurrentValue;
            return !string.IsNullOrWhiteSpace(opts.SmtpHost) &&
                   !string.IsNullOrWhiteSpace(opts.FromAddress) &&
                   opts.ToAddresses.Any(a => !string.IsNullOrWhiteSpace(a));
        }
    }

    public async Task<bool> SendEmailAsync(
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default
    )
    {
        var opts = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(opts.SmtpHost))
        {
            _logger.LogWarning("SMTP host is not configured. Email will not be sent.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(opts.FromAddress))
        {
            _logger.LogWarning("From address is not configured. Email will not be sent.");
            return false;
        }

        if (opts.ToAddresses.Count == 0)
        {
            _logger.LogWarning("No recipient addresses configured. Email will not be sent.");
            return false;
        }

        var message = new MimeMessage();
        try
        {
            message.From.Add(new MailboxAddress(opts.FromName, opts.FromAddress));
            foreach (var to in opts.ToAddresses)
            {
                if (MailboxAddress.TryParse(to, out var recipient))
                    message.To.Add(recipient);
                else
                    _logger.LogWarning("Skipping malformed recipient address: {Address}", to);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid sender/recipient configuration; email not sent: {Subject}", subject);
            return false;
        }

        if (message.To.Count == 0)
        {
            _logger.LogWarning("No valid recipient addresses after parsing. Email will not be sent.");
            return false;
        }

        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient
        {
            // Bound each SMTP operation so a hung server cannot block a worker
            // loop for MailKit's ~120s-per-stage default.
            Timeout = (int)TimeSpan.FromSeconds(opts.TimeoutSeconds).TotalMilliseconds,
        };
        try
        {
            var secureOption = opts.Security?.ToLowerInvariant() switch
            {
                "none" => SecureSocketOptions.None,
                "ssl" or "implicit" => SecureSocketOptions.SslOnConnect,
                "starttls" => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.Auto,
            };
            await client.ConnectAsync(
                opts.SmtpHost,
                opts.SmtpPort,
                secureOption,
                cancellationToken
            );

            if (!string.IsNullOrWhiteSpace(opts.Username))
                await client.AuthenticateAsync(opts.Username, opts.Password, cancellationToken);

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Email sent: {Subject}", subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email: {Subject}", subject);
            return false;
        }
    }
}