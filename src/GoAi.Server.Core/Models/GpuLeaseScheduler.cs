using GoAi.Server.Core.Data;
using GoAi.Server.Core.Runtime;

namespace GoAi.Server.Core.Models;

public sealed class GpuLeaseScheduler : IDisposable
{
    // LM Studio workloads share one guarded lane. Heavy profiles such as the
    // coding model acquire that entire lane. The resident speech stack runs in
    // a separate, bounded lane so Whisper, ECAPA and Supertonic remain usable
    // while the selected coding model owns LM Studio.
    private const int SharedCapacity = 3;
    private const int SpeechCapacity = 3;
    private readonly SemaphoreSlim _slots = new(SharedCapacity, SharedCapacity);
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly SemaphoreSlim _speechSlots = new(SpeechCapacity, SpeechCapacity);
    private readonly SemaphoreSlim _speechAdmissionGate = new(1, 1);
    private readonly GoAiDatabase _database;
    private readonly ServerRuntimeState _runtime;
    private readonly object _activeGate = new();
    private readonly Dictionary<string, GpuLeaseActivity> _activeLeases = new(StringComparer.Ordinal);
    private int _queueLength;

    public GpuLeaseScheduler(GoAiDatabase database, ServerRuntimeState runtime)
    {
        _database = database;
        _runtime = runtime;
    }

    public int QueueLength => Volatile.Read(ref _queueLength);

    public string? ActiveLease
    {
        get
        {
            lock (_activeGate)
            {
                return _activeLeases.Count == 0
                    ? null
                    : string.Join(",", _activeLeases.Keys.Order(StringComparer.Ordinal));
            }
        }
    }

    public IReadOnlyList<GpuLeaseActivity> ActiveActivities
    {
        get
        {
            lock (_activeGate)
            {
                return _activeLeases.Values
                    .OrderBy(static activity => activity.StartedAt)
                    .ToArray();
            }
        }
    }

    public void Dispose()
    {
        _speechAdmissionGate.Dispose();
        _speechSlots.Dispose();
        _admissionGate.Dispose();
        _slots.Dispose();
    }

    public async Task RecoverInterruptedLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE gpu_leases
            SET state = 'interrupted', released_at = $now
            WHERE state IN ('queued', 'active');
            """;
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<GpuLease> AcquireAsync(
        string workload,
        string? runId,
        CancellationToken cancellationToken) =>
        AcquireAsync(workload, runId, GpuLeaseMode.Exclusive, cancellationToken);

    public async Task<GpuLease> AcquireAsync(
        string workload,
        string? runId,
        GpuLeaseMode mode = GpuLeaseMode.Exclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        var leaseId = $"lease-{Guid.NewGuid():N}";
        await RecordAsync(leaseId, runId, workload, "queued", cancellationToken).ConfigureAwait(false);
        _ = Interlocked.Increment(ref _queueLength);
        var speechLane = mode == GpuLeaseMode.Speech;
        var slots = speechLane ? _speechSlots : _slots;
        var admissionGate = speechLane ? _speechAdmissionGate : _admissionGate;
        var requiredSlots = mode switch
        {
            GpuLeaseMode.Shared or GpuLeaseMode.Speech => 1,
            _ => SharedCapacity,
        };
        var acquiredSlots = 0;
        try
        {
            await admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (acquiredSlots < requiredSlots)
                {
                    await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquiredSlots++;
                }
            }
            finally
            {
                admissionGate.Release();
            }
        }
        catch
        {
            if (acquiredSlots > 0)
            {
                slots.Release(acquiredSlots);
            }
            _ = Interlocked.Decrement(ref _queueLength);
            await MarkAsync(leaseId, "cancelled", false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _ = Interlocked.Decrement(ref _queueLength);
        var startedAt = DateTimeOffset.UtcNow;
        lock (_activeGate)
        {
            _activeLeases.Add(leaseId, new GpuLeaseActivity(
                leaseId,
                workload,
                runId,
                mode,
                startedAt));
        }
        await MarkAsync(leaseId, "active", true, cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog("Information", "gpu.lease.acquired", $"GPU-Lane für {workload} belegt ({mode}, {leaseId}).");
        return new GpuLease(this, leaseId, mode, requiredSlots);
    }

    private async Task ReleaseAsync(string leaseId, GpuLeaseMode mode, int slotCount)
    {
        lock (_activeGate)
        {
            if (!_activeLeases.Remove(leaseId))
            {
                return;
            }
        }

        try
        {
            await MarkAsync(leaseId, "released", false, CancellationToken.None).ConfigureAwait(false);
            _runtime.WriteLog("Information", "gpu.lease.released", $"GPU-Lane freigegeben ({leaseId}).");
        }
        finally
        {
            var slots = mode == GpuLeaseMode.Speech ? _speechSlots : _slots;
            slots.Release(slotCount);
        }
    }

    private async Task RecordAsync(
        string leaseId,
        string? runId,
        string workload,
        string state,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gpu_leases(lease_id, run_id, workload, state, created_at)
            VALUES($id, $run, $workload, $state, $created);
            """;
        command.Parameters.AddWithValue("$id", leaseId);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workload", workload);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkAsync(
        string leaseId,
        string state,
        bool acquired,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = acquired
            ? "UPDATE gpu_leases SET state = $state, acquired_at = $now WHERE lease_id = $id;"
            : "UPDATE gpu_leases SET state = $state, released_at = $now WHERE lease_id = $id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", leaseId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public sealed class GpuLease : IAsyncDisposable
    {
        private GpuLeaseScheduler? _owner;
        private readonly GpuLeaseMode _mode;
        private readonly int _slotCount;

        internal GpuLease(
            GpuLeaseScheduler owner,
            string leaseId,
            GpuLeaseMode mode,
            int slotCount)
        {
            _owner = owner;
            _mode = mode;
            _slotCount = slotCount;
            LeaseId = leaseId;
        }

        public string LeaseId { get; }

        public async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                await owner.ReleaseAsync(LeaseId, _mode, _slotCount).ConfigureAwait(false);
            }
        }
    }
}

public enum GpuLeaseMode
{
    Shared,
    Speech,
    Exclusive,
}

public sealed record GpuLeaseActivity(
    string LeaseId,
    string Workload,
    string? RunId,
    GpuLeaseMode Mode,
    DateTimeOffset StartedAt);
