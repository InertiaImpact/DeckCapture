using System.Security.Cryptography;
using System.Text.Json;
using DeckCapture.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using MimeKit;

namespace DeckCapture.Supervisor;

public sealed record OperationalAlert(string Id, string Severity, string Message, DateTimeOffset CreatedAt);
public sealed record NotificationMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Status { get; set; } = "Queued";
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class SecretStore(IDataProtectionProvider provider, string directory, DevelopmentDiagnostics? diagnostics = null)
{
    private readonly IDataProtector _protector = provider.CreateProtector("DeckCapture.SMTP.v1");
    private readonly object gate = new();
    private readonly Queue<string> knownSecrets = new();
    private string SecretPath => Path.Combine(directory, "smtp.secrets.json");
    public bool HasSmtpCredential => SmtpStatus().SecretConfigured;
    public void SaveSmtp(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096) throw new CommandException(400, "Password must contain 1–4096 characters and cannot be blank.");
        lock (gate)
        {
            Register(secret);
            Directory.CreateDirectory(directory);
            var temporary = SecretPath + ".new";
            File.WriteAllText(temporary, _protector.Protect(secret));
            File.Move(temporary, SecretPath, true);
        }
    }
    public string? ReadSmtp()
    {
        lock (gate)
        {
            var value = File.Exists(SecretPath) ? _protector.Unprotect(File.ReadAllText(SecretPath)) : null;
            if (value is not null) Register(value);
            return value;
        }
    }
    public SmtpSecretStatus SmtpStatus()
    {
        lock (gate)
        {
            if (!File.Exists(SecretPath)) return new(false, "Missing");
            try { return string.IsNullOrEmpty(ReadSmtp()) ? new(false, "Unavailable") : new(true, "Configured"); }
            catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { return new(false, "Unavailable"); }
        }
    }
    public SmtpSecretStatus ChangeSmtp(string? action, string? value)
    {
        // Old callers posted {value}; retain that meaning. Blank UI fields use explicit Keep.
        action ??= "Replace";
        lock (gate)
        {
            switch (action)
            {
                case "Keep":
                    if (value is not null) throw new CommandException(400, "Keep must not include a password value.");
                    break;
                case "Replace": SaveSmtp(value ?? ""); break;
                case "Clear":
                    if (value is not null) throw new CommandException(400, "Clear must not include a password value.");
                    // Register before deletion so later error/status text cannot echo the prior value.
                    try { _ = ReadSmtp(); } catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { }
                    File.Delete(SecretPath);
                    break;
                default: throw new CommandException(400, "Password action must be Keep, Replace or Clear.");
            }
            return SmtpStatus();
        }
    }
    internal string RedactError(string text)
    {
        lock (gate)
        {
            foreach (var secret in knownSecrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return diagnostics?.Redact(text) ?? text;
        }
    }
    private void Register(string value)
    {
        diagnostics?.RegisterSecret(value);
        if (!knownSecrets.Contains(value, StringComparer.Ordinal)) { knownSecrets.Enqueue(value); while (knownSecrets.Count > 16) knownSecrets.Dequeue(); }
    }
}
public sealed record SmtpSecretStatus(bool SecretConfigured, string Status);

internal static class SmtpTransport
{
    internal static SecureSocketOptions Security(string mode) => mode switch
    {
        "Relay" => SecureSocketOptions.None,
        "StartTls" => SecureSocketOptions.StartTls,
        "Tls" => SecureSocketOptions.SslOnConnect,
        _ => throw new InvalidOperationException("Unsupported SMTP connection security mode.")
    };
    internal static async Task ConnectAsync(SmtpClient client, NotificationSettings settings, SecretStore secrets, CancellationToken token)
    {
        var tls = Security(settings.TlsMode);
        await client.ConnectAsync(settings.Host, settings.Port, tls, token);
        if (!settings.UsesAuthentication) return;
        // Preserve the old inferred-login guard, while an explicit login choice is independent
        // of connection security (including an operator-selected internal plaintext relay).
        if (tls == SecureSocketOptions.None && settings.AuthenticationEnabled is not true) throw new InvalidOperationException("Credentials require an encrypted SMTP connection unless plaintext login is explicitly enabled.");
        if (string.IsNullOrWhiteSpace(settings.Username)) throw new InvalidOperationException("SMTP login needs a username.");
        await client.AuthenticateAsync(settings.Username, secrets.ReadSmtp() ?? throw new InvalidOperationException("SMTP password is not configured."), token);
    }
}

/// <summary>Persistent SMTP outbox; submission failure never participates in capture or delivery transactions.</summary>
public sealed class NotificationService(StateStore store, SecretStore secrets, ILogger<NotificationService> logger, Func<SmtpClient>? smtpClientFactory = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public List<NotificationMessage> Snapshot() => store.ReadAll<NotificationMessage>("notification").OrderByDescending(m => m.CreatedAt).Take(100).ToList();
    public void Queue(string subject, string body, string? stableId = null)
    {
        var id = stableId ?? Guid.NewGuid().ToString("N");
        if (store.ReadAll<NotificationMessage>("notification").Any(m => m.Id == id)) return;
        store.Save("notification", id, new NotificationMessage { Id = id, Subject = subject, Body = body });
    }
    public async Task ProcessAsync(NotificationSettings settings, CancellationToken token)
    {
        if (!settings.Enabled || !await _gate.WaitAsync(0, token)) return;
        try
        {
            foreach (var message in store.ReadAll<NotificationMessage>("notification").Where(m => m.Status != "Accepted" && m.NextAttemptAt <= DateTimeOffset.UtcNow).Take(5))
            {
                try
                {
                    var email = new MimeMessage();
                    email.From.Add(MailboxAddress.Parse(settings.Sender));
                    foreach (var recipient in settings.Recipients) email.To.Add(MailboxAddress.Parse(recipient));
                    email.MessageId = $"{message.Id}@deckcapture.local";
                    email.Subject = message.Subject; email.Body = new TextPart("plain") { Text = message.Body };
                    using var client = smtpClientFactory?.Invoke() ?? new SmtpClient { Timeout = 15_000 };
                    await SmtpTransport.ConnectAsync(client, settings, secrets, token);
                    await client.SendAsync(email, token);
                    message.Status = "Accepted"; message.Error = null; message.Attempts++;
                    // Relay acceptance is recorded before QUIT; a disconnect error must not enqueue a second accepted message.
                    store.Save("notification", message.Id, message);
                    try { await client.DisconnectAsync(true, token); } catch (Exception e) when (e is not OperationCanceledException) { logger.LogDebug(e, "SMTP disconnect after acceptance"); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    message.Attempts++; message.Status = "RetryScheduled"; message.Error = secrets.RedactError(e.GetType().Name + ": " + e.Message);
                    message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(message.Attempts, 8))));
                    store.Save("notification", message.Id, message);
                    logger.LogWarning("Notification {Id} failed ({Type}); retained for retry", message.Id, e.GetType().Name);
                }
            }
        }
        finally { _gate.Release(); }
    }
}
