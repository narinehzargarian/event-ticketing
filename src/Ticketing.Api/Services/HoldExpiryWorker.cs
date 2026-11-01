namespace Ticketing.Api.Services;

/// <summary>
/// Releases lapsed holds and tells watching clients. Correctness doesn't depend on it, because new
/// holds already treat a lapsed seat as free. This is what makes the seat map update on its own.
/// </summary>
public class HoldExpiryWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<HoldExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Holds:SweepSeconds", 5.0));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SeatService>().ExpireDueHoldsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // One failed sweep must not end the worker. The next sweep tries again.
                logger.LogError(error, "Hold expiry sweep failed");
            }
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
