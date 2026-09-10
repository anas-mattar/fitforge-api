namespace FitForge.Api.Hosting.Retention;

/// <summary>
/// Runs the retention purge daily. Decides <i>when</i>; <see cref="RetentionRunner"/>
/// decides <i>what</i>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BackgroundService"/> and nothing more: no package, no scheduler, no new
/// deployment surface (<c>plan.md</c> D9). FitForge has one host, and a cron entry or a
/// hosted-job service would be infrastructure to operate in exchange for a timer.
/// </para>
/// <para>
/// <b>A first pass runs shortly after startup, not a day later.</b> An instance restarted
/// every day would otherwise never purge anything — the timer would reset before it ever
/// fired, and invariant 10's window would quietly never close.
/// </para>
/// </remarks>
public sealed class RetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// Long enough that a purge never competes with startup, short enough that a daily
    /// restart cycle still gets one.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Not a failure, and not worth a log line that would appear on
            // every deployment.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The runner is scoped, because a DbContext is. Resolving one per pass rather
            // than holding one for the life of the host is what keeps a long-running
            // service from accumulating a change tracker full of removed entities.
            await using var scope = scopeFactory.CreateAsyncScope();

            await scope.ServiceProvider
                .GetRequiredService<RetentionRunner>()
                .RunAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed pass must not kill the service: the next one is tomorrow, and a
            // background service that dies silently on a transient database error would
            // stop honouring the retention window without anybody noticing. Logged
            // without member detail, per plan.md D12.
            logger.LogError(exception, "Retention pass failed. The next pass will retry.");
        }
    }
}
