using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Domain.Operations;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Text.Json;

namespace DrinksEcommerce.Infrastructure.Notifications;

public sealed class NotificationService(
    IApplicationDbContext db,
    IOptions<EmailOptions> emailOptions,
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task QueueAsync(
        Guid userId,
        string email,
        string type,
        string title,
        string message,
        Guid? orderId = null,
        CancellationToken cancellationToken = default)
    {
        var notification = new Notification(userId, email, type, title, message, orderId);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        await TryDispatchEmailAsync(notification, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DispatchPendingEmailsAsync(CancellationToken ct = default)
    {
        var pending = await db.Notifications
            .Where(n => !n.EmailDispatched && n.EmailAttemptCount < 5)
            .OrderBy(n => n.CreatedAt)
            .Take(25)
            .ToListAsync(ct);

        var count = 0;
        foreach (var n in pending)
        {
            if (await TryDispatchEmailAsync(n, ct))
            {
                count++;
            }
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return count;
    }

    private async Task<bool> TryDispatchEmailAsync(Notification notification, CancellationToken ct)
    {
        var opts = emailOptions.Value;
        try
        {
            if (string.Equals(opts.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
            {
                using var mime = new MimeMessage();
                mime.From.Add(new MailboxAddress(opts.FromName, opts.FromAddress));
                mime.To.Add(MailboxAddress.Parse(notification.Email));
                mime.Subject = notification.Title;
                mime.Body = new TextPart("plain") { Text = notification.Message };

                using var client = new SmtpClient();
                await client.ConnectAsync(opts.SmtpHost, opts.SmtpPort, opts.UseSsl, ct);
                if (!string.IsNullOrWhiteSpace(opts.Username))
                {
                    await client.AuthenticateAsync(opts.Username, opts.Password ?? string.Empty, ct);
                }
                await client.SendAsync(mime, ct);
                await client.DisconnectAsync(true, ct);
                notification.MarkEmailDispatched();
                return true;
            }

            // Safe development sink: logs structured notification metadata without falsely claiming live SMTP delivery
            logger.LogInformation(
                "[DevNotificationSink] Notification {NotificationId} ({Type}) queued for {Email} — Subject: {Title}",
                notification.Id,
                notification.Type,
                notification.Email,
                notification.Title);
            notification.MarkEmailDispatched();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification email delivery failed for Notification {NotificationId}", notification.Id);
            notification.RecordEmailFailure(ex.Message);
            return false;
        }
    }
}

public sealed class CatalogCacheService(
    IMemoryCache memoryCache,
    ILogger<CatalogCacheService> logger,
    IDistributedCache? distributedCache = null) : ICatalogCacheService
{
    private static int _version = 1;

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var versionedKey = $"{key}:v{_version}";
        if (memoryCache.TryGetValue(versionedKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        if (distributedCache is not null)
        {
            try
            {
                var json = await distributedCache.GetStringAsync(versionedKey, cancellationToken);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var fromRedis = JsonSerializer.Deserialize<T>(json);
                    if (fromRedis is not null)
                    {
                        memoryCache.Set(versionedKey, fromRedis, ttl);
                        return fromRedis;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis cache read failed for key {Key}; falling back to database.", versionedKey);
            }
        }

        var fresh = await factory(cancellationToken);
        memoryCache.Set(versionedKey, fresh, ttl);

        if (distributedCache is not null && fresh is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(fresh);
                await distributedCache.SetStringAsync(
                    versionedKey,
                    json,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis cache write failed for key {Key}; continuing safely.", versionedKey);
            }
        }

        return fresh;
    }

    public Task InvalidateCatalogAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _version);
        return Task.CompletedTask;
    }
}

public sealed class AuditService(
    IApplicationDbContext db,
    ICurrentUserService currentUser) : IAuditService
{
    public void Record(string action, string entityType, string? entityId, string? details = null)
    {
        var actor = currentUser.Email ?? currentUser.UserId?.ToString() ?? "System";
        db.AuditLogs.Add(new AuditLog(actor, action, entityType, entityId, details, currentUser.IpAddress));
    }
}
