// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// State-machine over the runtime async profiler stream
    /// (<see cref="AsyncProfilerTraceEventParser"/>).  Tracks per-async-context callstacks,
    /// per-OS-thread active context, and the QPC ↔ UTC clock published by the provider's
    /// metadata / sync sub-events.  Consumers (e.g. PerfView's stack-source generators)
    /// subscribe to the higher-level events below; raw sub-event access remains available
    /// via the underlying parser.
    /// </summary>
    public sealed class AsyncProfilerComputer : IAsyncProfilerSubEventSink
    {
        private readonly AsyncProfilerTraceEventParser _parser;

        // Per-async-context state.  Async context ids are 32-bit and process-wide; we key
        // on (processId, asyncContextId) to be safe across multi-process traces.
        private readonly Dictionary<ContextKey, AsyncContextState> _contexts =
            new Dictionary<ContextKey, AsyncContextState>();

        // Per-OS-thread state: which async context is currently active on the thread.
        private readonly Dictionary<ThreadKey, ulong> _threadCurrentTask =
            new Dictionary<ThreadKey, ulong>();

        // Clock state: most recent QPC ↔ UTC sync point.  Populated by AsyncProfilerMetadata,
        // updated by AsyncProfilerSyncClock.  QpcFrequency is set once by metadata.
        private ulong _qpcFrequency;
        private ulong _qpcSync;
        private ulong _utcSync;
        private bool _haveClock;

        public AsyncProfilerComputer(AsyncProfilerTraceEventParser parser)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            Subscribe();
        }

        /// <summary>
        /// Construct a computer without a parser binding.  Intended for unit tests and
        /// scenarios where sub-events are produced via
        /// <see cref="AsyncProfilerTraceEventParser.ParseBuffer"/> directly into the
        /// computer (which implements <see cref="IAsyncProfilerSubEventSink"/>).
        /// </summary>
        public AsyncProfilerComputer() { _parser = null; }

        /// <summary>
        /// True once an <c>AsyncProfilerMetadata</c> sub-event has been observed and the
        /// QPC ↔ UTC mapping is usable.
        /// </summary>
        public bool ClockKnown => _haveClock;

        /// <summary>QPC frequency (Hz) reported by the most recent metadata sub-event.</summary>
        public ulong QpcFrequency => _qpcFrequency;

        /// <summary>Most recent QPC value paired with <see cref="UtcSync"/>.</summary>
        public ulong QpcSync => _qpcSync;

        /// <summary>FILETIME (100ns ticks since 1601-01-01 UTC) paired with <see cref="QpcSync"/>.</summary>
        public ulong UtcSync => _utcSync;

        /// <summary>
        /// Number of continuation-wrapper slots reported by the most recent metadata
        /// sub-event.  0 until metadata arrives.
        /// </summary>
        public byte WrapperCount { get; private set; }

        /// <summary>
        /// Convert an absolute QPC to UTC using the current clock sync.  Returns
        /// <c>null</c> until <see cref="ClockKnown"/> is true.
        /// </summary>
        public DateTime? QpcToDateTime(long qpc) =>
            AsyncProfilerTraceEventParser.QpcToDateTime(qpc, _qpcFrequency, _qpcSync, _utcSync);

        /// <summary>
        /// Returns the most recent async callstack observed for the given (process, async
        /// context), or <c>null</c> if no callstack has been recorded yet.  Cached
        /// callstacks are treated as repeats of the last non-cached one — the returned
        /// payload always carries the full frame list.
        /// </summary>
        public CallstackPayload? GetCurrentAsyncStack(int processId, uint asyncContextId)
        {
            if (_contexts.TryGetValue(new ContextKey(processId, asyncContextId), out var s))
                return s.LastFullCallstack;
            return null;
        }

        /// <summary>
        /// Returns the async context currently active on the given OS thread, or 0 if no
        /// context has been observed there.
        /// </summary>
        public ulong GetCurrentTaskId(int processId, ulong osThreadId) =>
            _threadCurrentTask.TryGetValue(new ThreadKey(processId, osThreadId), out var id) ? id : 0;

        // -----------------------------------------------------------------------
        // High-level events for consumers (PerfView stack sources, diagnostic UI).
        // Each carries the resolved QPC plus the most-recent full callstack for the
        // async context, so stack-source generators don't need to re-walk the buffer.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Fired when an async context resumes execution on a thread.  Carries the most
        /// recent full callstack for the context if any has been observed.
        /// </summary>
        public event Action<AsyncResumeInfo> AsyncContextResumed;

        /// <summary>
        /// Fired when an async context suspends.  Carries the most recent full callstack
        /// for the context.  Together with <see cref="AsyncContextResumed"/>, suitable for
        /// "time spent" sample emission keyed off the async callstack.
        /// </summary>
        public event Action<AsyncResumeInfo> AsyncContextSuspended;

        /// <summary>
        /// Fired when an async context completes.  After this point the computer drops
        /// its per-context state for the (process, context) pair.
        /// </summary>
        public event Action<AsyncResumeInfo> AsyncContextCompleted;

        /// <summary>
        /// Fired the first time metadata is observed, and again whenever a sync-clock
        /// update arrives.
        /// </summary>
        public event Action<AsyncProfilerComputer> ClockUpdated;

        private void Subscribe()
        {
            _parser.CreateAsyncContext += OnCreateAsyncContext;
            _parser.ResumeAsyncContext += OnResumeAsyncContext;
            _parser.SuspendAsyncContext += OnSuspendAsyncContext;
            _parser.CompleteAsyncContext += OnCompleteAsyncContext;
            _parser.CreateAsyncCallstack += OnCreateAsyncCallstack;
            _parser.ResumeAsyncCallstack += OnResumeAsyncCallstack;
            _parser.SuspendAsyncCallstack += OnSuspendAsyncCallstack;
            _parser.ResetAsyncThreadContext += OnResetAsyncThreadContext;
            _parser.AsyncProfilerMetadata += OnAsyncProfilerMetadata;
            _parser.AsyncProfilerSyncClock += OnAsyncProfilerSyncClock;
        }

        private AsyncContextState GetOrCreate(int processId, uint asyncContextId, ulong taskId)
        {
            var key = new ContextKey(processId, asyncContextId);
            if (!_contexts.TryGetValue(key, out var s))
            {
                s = new AsyncContextState { TaskId = taskId };
                _contexts[key] = s;
            }
            else if (taskId != 0)
            {
                s.TaskId = taskId;
            }
            return s;
        }

        public void OnCreateAsyncContext(CreateAsyncContextTraceData e)
        {
            GetOrCreate(e.ProcessID, e.AsyncThreadContextId, e.TaskId);
            _threadCurrentTask[new ThreadKey(e.ProcessID, e.OsThreadId)] = e.TaskId;
        }

        public void OnResumeAsyncContext(ResumeAsyncContextTraceData e)
        {
            var s = GetOrCreate(e.ProcessID, e.AsyncThreadContextId, e.TaskId);
            _threadCurrentTask[new ThreadKey(e.ProcessID, e.OsThreadId)] = e.TaskId;
            AsyncContextResumed?.Invoke(new AsyncResumeInfo(e.RawEvent, e.ProcessID, e.AsyncThreadContextId,
                e.OsThreadId, e.TaskId, e.ResolvedQpc, s.LastFullCallstack));
        }

        public void OnSuspendAsyncContext(SuspendAsyncContextTraceData e)
        {
            var s = GetOrCreate(e.ProcessID, e.AsyncThreadContextId, e.TaskId);
            AsyncContextSuspended?.Invoke(new AsyncResumeInfo(e.RawEvent, e.ProcessID, e.AsyncThreadContextId,
                e.OsThreadId, e.TaskId, e.ResolvedQpc, s.LastFullCallstack));
        }

        public void OnCompleteAsyncContext(CompleteAsyncContextTraceData e)
        {
            var key = new ContextKey(e.ProcessID, e.AsyncThreadContextId);
            _contexts.TryGetValue(key, out var s);
            AsyncContextCompleted?.Invoke(new AsyncResumeInfo(e.RawEvent, e.ProcessID, e.AsyncThreadContextId,
                e.OsThreadId, e.TaskId, e.ResolvedQpc, s?.LastFullCallstack));
            _contexts.Remove(key);
        }

        public void OnCreateAsyncCallstack(AsyncCallstackTraceData e) => HandleCallstack(e);
        public void OnResumeAsyncCallstack(AsyncCallstackTraceData e) => HandleCallstack(e);
        public void OnSuspendAsyncCallstack(AsyncCallstackTraceData e) => HandleCallstack(e);

        private void HandleCallstack(AsyncCallstackTraceData e)
        {
            var s = GetOrCreate(e.ProcessID, e.AsyncThreadContextId, e.TaskId);
            // Cached callstacks repeat a previously-emitted full stack; the runtime ships
            // them whenever the profiler determines the stack is logically unchanged.  We
            // therefore only refresh LastFullCallstack on non-cached payloads.
            if (!e.Cached)
            {
                s.LastFullCallstack = e.Payload;
            }
        }

        public void OnResetAsyncThreadContext(ResetAsyncTraceData e)
        {
            _threadCurrentTask[new ThreadKey(e.ProcessID, e.OsThreadId)] = 0;
        }

        // Sub-events with no state-machine impact in the computer.  Present for interface
        // satisfaction; consumers can subscribe to the parser directly for these.
        public void OnUnwindAsyncException(UnwindAsyncExceptionTraceData e) { }
        public void OnResumeAsyncMethod(AsyncMethodTraceData e) { }
        public void OnCompleteAsyncMethod(AsyncMethodTraceData e) { }
        public void OnResetAsyncContinuationWrapperIndex(ResetAsyncTraceData e) { }
        public void OnParseError(AsyncProfilerParseError e) { }

        public void OnAsyncProfilerMetadata(AsyncProfilerMetadataTraceData e)
        {
            _qpcFrequency = e.QpcFrequency;
            _qpcSync = e.QpcSync;
            _utcSync = e.UtcSync;
            WrapperCount = e.WrapperCount;
            _haveClock = true;
            ClockUpdated?.Invoke(this);
        }

        public void OnAsyncProfilerSyncClock(AsyncProfilerSyncClockTraceData e)
        {
            // Sync-clock refines the most recent (qpcSync, utcSync) pair but keeps the
            // frequency.  Don't mark ClockKnown=true here — frequency is required and only
            // ships in metadata.
            _qpcSync = e.QpcSync;
            _utcSync = e.UtcSync;
            if (_qpcFrequency != 0) ClockUpdated?.Invoke(this);
        }

        private sealed class AsyncContextState
        {
            public ulong TaskId;
            public CallstackPayload? LastFullCallstack;
        }

        private readonly struct ContextKey : IEquatable<ContextKey>
        {
            public ContextKey(int pid, uint ctxId) { Pid = pid; ContextId = ctxId; }
            public int Pid { get; }
            public uint ContextId { get; }
            public bool Equals(ContextKey other) => Pid == other.Pid && ContextId == other.ContextId;
            public override bool Equals(object obj) => obj is ContextKey o && Equals(o);
            public override int GetHashCode() => (Pid * 397) ^ (int)ContextId;
        }

        private readonly struct ThreadKey : IEquatable<ThreadKey>
        {
            public ThreadKey(int pid, ulong osThreadId) { Pid = pid; OsThreadId = osThreadId; }
            public int Pid { get; }
            public ulong OsThreadId { get; }
            public bool Equals(ThreadKey other) => Pid == other.Pid && OsThreadId == other.OsThreadId;
            public override bool Equals(object obj) => obj is ThreadKey o && Equals(o);
            public override int GetHashCode() => (Pid * 397) ^ OsThreadId.GetHashCode();
        }
    }

    /// <summary>
    /// Snapshot delivered with <see cref="AsyncProfilerComputer.AsyncContextResumed"/> and
    /// related events.  Holds the resolved QPC and the most recent full callstack for the
    /// context.  Use the computer's clock helpers to convert QPC to UTC.
    /// </summary>
    public sealed class AsyncResumeInfo
    {
        public AsyncResumeInfo(AsyncEventsTraceData rawEvent, int processId, uint asyncContextId,
            ulong osThreadId, ulong taskId, long resolvedQpc, CallstackPayload? callstack)
        {
            RawEvent = rawEvent;
            ProcessID = processId;
            AsyncContextId = asyncContextId;
            OsThreadId = osThreadId;
            TaskId = taskId;
            ResolvedQpc = resolvedQpc;
            Callstack = callstack;
        }

        public AsyncEventsTraceData RawEvent { get; }
        public int ProcessID { get; }
        public uint AsyncContextId { get; }
        public ulong OsThreadId { get; }
        public ulong TaskId { get; }
        public long ResolvedQpc { get; }
        public CallstackPayload? Callstack { get; }
    }
}
