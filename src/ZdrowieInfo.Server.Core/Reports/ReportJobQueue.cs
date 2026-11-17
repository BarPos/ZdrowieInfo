using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZdrowieInfo.Server.Reports;

public enum ReportJobState { Queued, Running, Done, Failed }

/// <summary>Zlecenie przygotowania raportu z panelu (stan widoczny na stronie Panel → Raporty, tylko w pamięci).</summary>
public sealed class ReportJob(DateOnly from, DateOnly to, bool replace, string requestedBy, DateTimeOffset queuedAt)
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateOnly From { get; } = from;
    public DateOnly To { get; } = to;
    public bool Replace { get; } = replace;
    public string RequestedBy { get; } = requestedBy;
    public DateTimeOffset QueuedAt { get; } = queuedAt;
    public ReportJobState State { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public string? Message { get; internal set; }

    /// <summary>Gotowy raport (do linku).</summary>
    public ReportKey? Result { get; internal set; }

    public ReportKey Key => From == To ? ReportKey.Daily(To) : ReportKey.Period(From, To);
}

/// <summary>
/// Kolejka raportów zlecanych z panelu: po jednym naraz w tle (pobieranie archiwum list i analiza mogą trwać kilka minut,
/// a strona panelu nie musi być otwarta). Historia ostatnich 20 zleceń w pamięci.
/// </summary>
public sealed class ReportJobQueue(DailyReportService reports, TimeProvider time, ILogger<ReportJobQueue> logger) : BackgroundService
{
    private readonly Channel<ReportJob> _channel = Channel.CreateUnbounded<ReportJob>();
    private readonly LinkedList<ReportJob> _history = new();
    private readonly Lock _gate = new();

    /// <summary>Zmiana stanu któregokolwiek zlecenia (strona panelu odświeża listę).</summary>
    public event Action? Changed;

    public IReadOnlyList<ReportJob> Jobs
    {
        get { lock (_gate) return [.. _history]; }
    }

    public ReportJob Enqueue(DateOnly from, DateOnly to, bool replace, string requestedBy)
    {
        var job = new ReportJob(from, to, replace, requestedBy, time.GetUtcNow());
        lock (_gate)
        {
            _history.AddFirst(job);
            while (_history.Count > 20) _history.RemoveLast();
        }
        _channel.Writer.TryWrite(job);
        Changed?.Invoke();
        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            job.State = ReportJobState.Running;
            Changed?.Invoke();
            try
            {
                var result = await reports.GenerateForAsync(job.From, job.To, job.Replace, stoppingToken);
                (job.State, job.Message) = result.Outcome switch
                {
                    ReportRunOutcome.Created => (ReportJobState.Done, "Raport gotowy."),
                    ReportRunOutcome.AlreadyExists => (ReportJobState.Failed, "Taki raport już istnieje – zaznacz „Zastąp istniejący”."),
                    ReportRunOutcome.NotReplaced => (ReportJobState.Failed, $"Analiza się nie udała ({result.Error}) – poprzedni raport zostaje."),
                    ReportRunOutcome.RetryLater => (ReportJobState.Failed, $"Chwilowy błąd ({result.Error}) – spróbuj za kilka minut."),
                    _ => (ReportJobState.Failed, result.Error ?? "Nie udało się."),
                };
                if (result.Notes is { Count: > 0 } notes) job.Message += " Uwagi: " + string.Join(" ", notes);
                if (result.Outcome is ReportRunOutcome.Created or ReportRunOutcome.AlreadyExists or ReportRunOutcome.NotReplaced) job.Result = job.Key;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Zlecenie raportu {Key} przerwane błędem.", job.Key);
                (job.State, job.Message) = (ReportJobState.Failed, $"Błąd: {e.Message}");
            }
            job.FinishedAt = time.GetUtcNow();
            Changed?.Invoke();
        }
    }
}
