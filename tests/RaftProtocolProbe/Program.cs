using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

Console.WriteLine("=== Raft IPersistentState Protocol Probe ===");
Console.WriteLine();

var endpoints = new IPEndPoint[]
{
    new(IPAddress.Loopback, 17031),
    new(IPAddress.Loopback, 17032),
    new(IPAddress.Loopback, 17033),
};

var loggerFactory = NullLoggerFactory.Instance;
var states = new InMemoryPersistentState[3];
for (var i = 0; i < 3; i++)
    states[i] = new InMemoryPersistentState(i);

var clusters = new RaftCluster[3];
for (var i = 0; i < 3; i++)
{
    var config = new RaftCluster.TcpConfiguration(endpoints[i])
    {
        ColdStart = i == 0,
        LowerElectionTimeout = 300,
        UpperElectionTimeout = 600,
        HeartbeatThreshold = 0.5,
        LoggerFactory = loggerFactory,
    };
    config.UseInMemoryConfigurationStorage();
    clusters[i] = new RaftCluster(config) { AuditTrail = states[i] };
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

Console.WriteLine("[1] Starting 3 nodes...");
for (var i = 0; i < 3; i++)
    await clusters[i].StartAsync(cts.Token);

await Task.Delay(5000);

var leaderIdx = -1;
for (var i = 0; i < 3; i++)
{
    if (clusters[i].Leader is not null && EndPoint.Equals(clusters[i].Leader, endpoints[i]))
    { leaderIdx = i; break; }
}
if (leaderIdx < 0)
{
    for (var i = 0; i < 3; i++)
    {
        if (clusters[i].Leader is not null)
        {
            for (var j = 0; j < 3; j++)
                if (EndPoint.Equals(endpoints[j], clusters[i].Leader)) { leaderIdx = j; break; }
            break;
        }
    }
}

Console.WriteLine("[2] Checking leaders...");
for (var i = 0; i < 3; i++)
{
    var leader = clusters[i].Leader;
    Console.WriteLine($"  N{i} Leader={leader}, Term={clusters[i].Term}");
    if (leader is not null)
    {
        for (var j = 0; j < 3; j++)
        {
            if (leaderIdx < 0 && EndPoint.Equals(endpoints[j], leader))
                leaderIdx = j;
        }
    }
}
if (leaderIdx < 0)
{
    Console.WriteLine("  Using first node with Leader as leaderIdx");
    for (var i = 0; i < 3; i++)
    {
        if (clusters[i].Leader is not null) { leaderIdx = i; break; }
    }
}
Console.WriteLine($"  -> Detected leaderIdx = {leaderIdx}");

if (leaderIdx < 0) goto Shutdown;

Console.WriteLine("[3] Adding peers...");
for (var i = 0; i < 3; i++)
{
    if (i == leaderIdx) continue;
    try { await clusters[leaderIdx].AddMemberAsync(endpoints[i], cts.Token); }
    catch (Exception ex) { Console.WriteLine($"  Node {i} failed: {ex.Message}"); }
}
await Task.Delay(3000);

Console.WriteLine();
Console.WriteLine("=== Phase 2: Replicate ===");
for (var i = 0; i < 5; i++)
{
    var entry = new ProbeLogEntry(Encoding.UTF8.GetBytes($"cmd-{i}"), clusters[leaderIdx].Term);
    var index = await clusters[leaderIdx].ReplicateAsync(entry, cts.Token);
    Console.WriteLine($"  Replicate entry {i} -> index={index}");
    await Task.Delay(200);
}
await Task.Delay(2000);

Console.WriteLine();
Console.WriteLine("=== Phase 3: Summary ===");
for (var i = 0; i < 3; i++)
{
    Console.WriteLine($"  N{i}: entries={states[i].EntryCount} committed={states[i].CommittedIndex} applied={states[i].AppliedIndex}");
}

Console.WriteLine();
Console.WriteLine("=== Phase 4: Call Counts ===");
for (var i = 0; i < 3; i++)
{
    Console.WriteLine($"  N{i}:");
    foreach (var kv in states[i].CallCounts.OrderBy(x => x.Key))
        Console.WriteLine($"    {kv.Key}: {kv.Value}");
}

Shutdown:
Console.WriteLine();
Console.WriteLine("[Shutdown]");
for (var i = 0; i < 3; i++) await clusters[i].StopAsync(CancellationToken.None);
for (var i = 0; i < 3; i++) clusters[i].Dispose();
Console.WriteLine("Done.");

// ═══════════════════════════════════════════════════════════════
// InMemoryPersistentState
// ═══════════════════════════════════════════════════════════════

internal sealed class InMemoryPersistentState : IPersistentState, IDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly int _nodeId;
    private long _term;
    private ClusterMemberId _votedFor;
    private long _lastEntryIndex;
    private long _lastCommittedEntryIndex;
    private long _lastAppliedEntryIndex;
    private readonly ConcurrentDictionary<long, (long Term, byte[] Payload)> _entries = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private TaskCompletionSource _applyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly Dictionary<string, int> CallCounts = new(StringComparer.Ordinal);
    public int EntryCount => _entries.Count;
    public long CommittedIndex => Volatile.Read(ref _lastCommittedEntryIndex);
    public long AppliedIndex => Volatile.Read(ref _lastAppliedEntryIndex);

    public InMemoryPersistentState(int nodeId) => _nodeId = nodeId;

    public void Dispose() => _applyLock.Dispose();

    private void Trace(string method, string detail)
    {
        Console.WriteLine($"    [{_sw.ElapsedMilliseconds,7}ms] N{_nodeId} {method}: {detail}");
        lock (CallCounts)
        {
            ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(CallCounts, method, out _);
            c++;
        }
    }

    // ═══ IPersistentState ═══

    long IPersistentState.Term
    {
        get { var t = Volatile.Read(ref _term); Trace("Term.get", $"-> {t}"); return t; }
    }

    bool IPersistentState.IsVotedFor(in ClusterMemberId id)
    {
        var r = _votedFor.Equals(id);
        Trace("IsVotedFor", $"-> {r}");
        return r;
    }

    ValueTask<long> IPersistentState.IncrementTermAsync(ClusterMemberId member, CancellationToken token)
    {
        var t = Interlocked.Increment(ref _term);
        _votedFor = member;
        Trace("IncrementTermAsync", $"member={member} -> term={t}");
        return new(t);
    }

    ValueTask IPersistentState.UpdateTermAsync(long term, bool resetLastVote, CancellationToken token)
    {
        if (term > Volatile.Read(ref _term)) Interlocked.Exchange(ref _term, term);
        if (resetLastVote) _votedFor = default;
        Trace("UpdateTermAsync", $"term={term} resetVote={resetLastVote}");
        return ValueTask.CompletedTask;
    }

    ValueTask IPersistentState.UpdateVotedForAsync(ClusterMemberId id, CancellationToken token)
    {
        _votedFor = id;
        Trace("UpdateVotedForAsync", $"id={id}");
        return ValueTask.CompletedTask;
    }

    // ═══ IAuditTrail properties ═══

    long IAuditTrail.LastEntryIndex
    {
        get { var v = Volatile.Read(ref _lastEntryIndex); Trace("LastEntryIndex", $"-> {v}"); return v; }
    }

    long IAuditTrail.LastCommittedEntryIndex
    {
        get { var v = Volatile.Read(ref _lastCommittedEntryIndex); Trace("LastCommittedEntryIndex", $"-> {v}"); return v; }
    }

    bool IAuditTrail.IsLogEntryLengthAlwaysPresented => true;

    // ═══ IAuditTrail init ═══

    Task IAuditTrail.InitializeAsync(CancellationToken token)
    {
        Trace("InitializeAsync", "noop (in-memory)");
        return Task.CompletedTask;
    }

    // ═══ IAuditTrail commit ═══

    async ValueTask<long> IAuditTrail.CommitAsync(long endIndex, CancellationToken token)
    {
        Trace("CommitAsync", $"end={endIndex} committed={Volatile.Read(ref _lastCommittedEntryIndex)} applied={Volatile.Read(ref _lastAppliedEntryIndex)}");
        Volatile.Write(ref _lastCommittedEntryIndex, endIndex);

        await _applyLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _lastAppliedEntryIndex) < endIndex)
            {
                var idx = Volatile.Read(ref _lastAppliedEntryIndex) + 1;
                if (_entries.TryGetValue(idx, out var e))
                    Trace("  Apply", $"index={idx} term={e.Term} len={e.Payload.Length}");
                Volatile.Write(ref _lastAppliedEntryIndex, idx);
            }
        }
        finally { _applyLock.Release(); }

        var old = Interlocked.Exchange(ref _applyTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();
        return Volatile.Read(ref _lastAppliedEntryIndex);
    }

    // ═══ IAuditTrail wait ═══

    ValueTask IAuditTrail.WaitForApplyAsync(CancellationToken token)
    {
        Trace("WaitForApplyAsync(all)", "");
        return new(Volatile.Read(ref _applyTcs).Task);
    }

    async ValueTask IAuditTrail.WaitForApplyAsync(long index, CancellationToken token)
    {
        Trace("WaitForApplyAsync", $"target={index} current={Volatile.Read(ref _lastAppliedEntryIndex)}");
        while (Volatile.Read(ref _lastAppliedEntryIndex) < index)
            await Task.Delay(1, token).ConfigureAwait(false);
    }

    // ═══ IAuditTrail<IRaftLogEntry>.ReadAsync<TResult> ═══

    ValueTask<TResult> IAuditTrail<IRaftLogEntry>.ReadAsync<TResult>(
        ILogEntryConsumer<IRaftLogEntry, TResult> reader, long startIndex, long endIndex, CancellationToken token)
    {
        Trace("ReadAsync", $"start={startIndex} end={endIndex}");
        var list = new List<IRaftLogEntry>();
        for (var i = startIndex; i <= endIndex; i++)
        {
            if (_entries.TryGetValue(i, out var e))
                list.Add(new ProbeLogEntry(e.Payload, e.Term));
        }
        return reader.ReadAsync<IRaftLogEntry, List<IRaftLogEntry>>(list, null, token);
    }

    // ═══ AppendAsync<TEntryImpl> (single -> returns index) ═══

    ValueTask<long> IAuditTrail<IRaftLogEntry>.AppendAsync<TEntryImpl>(TEntryImpl entry, CancellationToken token)
    {
        var index = Interlocked.Increment(ref _lastEntryIndex);
        SaveEntry(index, entry);
        Trace("AppendAsync(single)", $"index={index} term={entry.Term} cmdId={entry.CommandId} isSnap={entry.IsSnapshot}");
        return new(index);
    }

    // ═══ AppendAsync<TEntryImpl> (single at startIndex) ═══

    ValueTask IAuditTrail<IRaftLogEntry>.AppendAsync<TEntryImpl>(TEntryImpl entry, long startIndex, CancellationToken token)
    {
        Volatile.Write(ref _lastEntryIndex, startIndex);
        SaveEntry(startIndex, entry);
        Trace("AppendAsync@start", $"start={startIndex} term={entry.Term} isSnap={entry.IsSnapshot}");
        return ValueTask.CompletedTask;
    }

    // ═══ AppendAsync<TEntryImpl> (batch) ═══

    async ValueTask IAuditTrail<IRaftLogEntry>.AppendAsync<TEntryImpl>(
        ILogEntryProducer<TEntryImpl> entries, long startIndex, bool skipCommitted, CancellationToken token)
    {
        Trace("AppendAsync(batch)", $"start={startIndex} remaining={entries.RemainingCount} skip={skipCommitted}");
        var idx = startIndex;
        while (entries.RemainingCount > 0)
        {
            await entries.MoveNextAsync().ConfigureAwait(false);
            var entry = entries.Current;
            SaveEntry(idx, entry);
            Trace("  +entry", $"index={idx} term={entry.Term}");
            idx++;
        }
        Volatile.Write(ref _lastEntryIndex, idx - 1);
    }

    // ═══ AppendAndCommitAsync<TEntryImpl> ═══

    async ValueTask<long> IAuditTrail<IRaftLogEntry>.AppendAndCommitAsync<TEntryImpl>(
        ILogEntryProducer<TEntryImpl> entries, long startIndex, bool skipCommitted, long commitIndex, CancellationToken token)
    {
        Trace("AppendAndCommitAsync", $"start={startIndex} commit={commitIndex} remaining={entries.RemainingCount}");
        var idx = startIndex;
        while (entries.RemainingCount > 0)
        {
            await entries.MoveNextAsync().ConfigureAwait(false);
            var entry = entries.Current;
            SaveEntry(idx, entry);
            idx++;
        }
        Volatile.Write(ref _lastEntryIndex, idx - 1);
        return await ((IAuditTrail)this).CommitAsync(commitIndex, token).ConfigureAwait(false);
    }

    private void SaveEntry<T>(long index, T entry) where T : IRaftLogEntry
    {
        byte[] payload;
        if (entry.TryGetMemory(out var mem))
            payload = mem.ToArray();
        else
            payload = Array.Empty<byte>();
        _entries[index] = (entry.Term, payload);
    }
}

// ═══ ProbeLogEntry ═══

internal sealed class ProbeLogEntry(byte[] data, long entryTerm) : IRaftLogEntry
{
    long IRaftLogEntry.Term => entryTerm;
    int? IRaftLogEntry.CommandId => null;
    bool ILogEntry.IsSnapshot => false;
    DateTimeOffset ILogEntry.Timestamp => DateTimeOffset.UtcNow;
    long? IDataTransferObject.Length => data.Length;
    bool IDataTransferObject.IsReusable => true;

    bool IDataTransferObject.TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = data;
        return true;
    }

    ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        => writer.WriteAsync(data, null, token);

    ValueTask<TResult> IDataTransferObject.TransformAsync<TResult, TTransformation>(TTransformation transformation, CancellationToken token)
        => throw new NotSupportedException();
}
