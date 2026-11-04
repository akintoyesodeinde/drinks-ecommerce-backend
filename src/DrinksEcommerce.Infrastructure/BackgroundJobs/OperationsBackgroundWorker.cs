using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Payments;
using DrinksEcommerce.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DrinksEcommerce.Infrastructure.BackgroundJobs;

public sealed class OperationsBackgroundWorker(
    IServiceProvider serviceProvider,
    ILogger<OperationsBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<PaymentOrchestratorService>();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var notifier = scope.ServiceProvider.GetRequiredService<NotificationService>();

                // 1. Release expired stock reservations
                var released = await orchestrator.CleanupExpiredReservationsAsync(stoppingToken);
                if (released > 0)
                {
                    logger.LogInformation("Background worker released {Count} expired stock reservation(s).", released);
                }

                // 2. Retry unprocessed durable webhook events
                var unprocessedWebhooks = await db.PaymentWebhookEvents
                    .AsNoTracking()
                    .Where(e => !e.IsProcessed && e.AttemptCount < 5)
                    .OrderBy(e => e.CreatedAt)
                    .Select(e => e.Id)
                    .Take(10)
                    .ToListAsync(stoppingToken);

                foreach (var eventId in unprocessedWebhooks)
                {
                    await orchestrator.ProcessStoredWebhookEventAsync(eventId, stoppingToken);
                }

                // 3. Dispatch pending notification emails
                await notifier.DispatchPendingEmailsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in OperationsBackgroundWorker cycle.");
            }
        }
    }
}
