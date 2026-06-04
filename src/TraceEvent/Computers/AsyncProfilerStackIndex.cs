// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// A single time interval during which one async continuation chain was the
    /// innermost-active chain on a particular OS thread.  Produced by
    /// <see cref="AsyncProfilerStackIndex"/>.  Intervals for a given thread are
    /// non-overlapping and time-ordered.
    /// </summary>
    public sealed class AsyncChainInterval
    {
        /// <summary>Inclusive start of the interval, in session-relative milliseconds.</summary>
        public double StartRelativeMSec;
        /// <summary>Exclusive end of the interval, in session-relative milliseconds.</summary>
        public double EndRelativeMSec;
        /// <summary>The async context that was running during this interval.</summary>
        public uint AsyncContextId;
        /// <summary>The logical task id associated with the async context.</summary>
        public ulong TaskId;
        /// <summary>
        /// The most recent full async callstack snapshot for the context, captured at the
        /// Resume that opened this interval.  May be <c>null</c> if no callstack was observed.
        /// </summary>
        public CallstackPayload? Callstack;
        /// <summary>
        /// EventIndex of the AsyncEvents event whose buffer registered <see cref="Callstack"/>'s
        /// frame IPs as code addresses.  Pass to <c>TraceLog.GetCodeAddressIndexAtEvent</c> to
        /// symbolize the frames.  <see cref="EventIndex.Invalid"/> when no callstack is available.
        /// </summary>
        public EventIndex CallstackEventIndex;
    }

    /// <summary>
    /// Builds a time index of async continuation chains keyed by OS thread, so that a consumer
    /// (e.g. a CPU-sample stack source) can ask "which async chain was active on thread Y at
    /// time T?".  Subscribe a fresh instance to an <see cref="AsyncProfilerComputer"/> before
    /// processing, then call <see cref="TryGetActiveChainAt"/> after processing completes.
    ///
    /// The index models nested Resume/Suspend brackets on a single thread with a per-thread
    /// stack: a Resume that arrives while another context is still active closes the current
    /// segment and starts a new one for the inner context; the matching Suspend reopens a
    /// segment for the outer context.  The result is a set of non-overlapping segments per
    /// thread, each tagged with the chain that was actually executing.
    /// </summary>
    public sealed class AsyncProfilerStackIndex
    {
        private readonly TraceLog _log;

        // Finalized, time-ordered, non-overlapping segments per (pid, osThread).
        private readonly Dictionary<(int Pid, ulong Thread), List<AsyncChainInterval>> _finalized
            = new Dictionary<(int, ulong), List<AsyncChainInterval>>();

        // In-progress per-thread state (open-context stack + current segment start).
        private readonly Dictionary<(int Pid, ulong Thread), ThreadState> _open
            = new Dictionary<(int, ulong), ThreadState>();

        private readonly double _traceEndMSec;
        private bool _finalizedDone;

        /// <summary>
        /// Create the index and subscribe to the computer's high-level events.  Construct this
        /// BEFORE calling <c>eventSource.Process()</c> so it observes every Resume/Suspend.
        /// </summary>
        public AsyncProfilerStackIndex(AsyncProfilerComputer computer, TraceLog log)
        {
            if (computer == null) throw new ArgumentNullException(nameof(computer));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _traceEndMSec = log.SessionEndTimeRelativeMSec;

            computer.AsyncContextResumed += OnResume;
            computer.AsyncContextSuspended += OnSuspendOrComplete;
            computer.AsyncContextCompleted += OnSuspendOrComplete;
        }

        /// <summary>
        /// Test-only constructor: builds an index whose state machine can be driven directly via
        /// <see cref="OnResumeCore"/> / <see cref="OnSuspendCore"/> without a TraceLog or computer.
        /// </summary>
        internal AsyncProfilerStackIndex(double traceEndMSec)
        {
            _log = null;
            _traceEndMSec = traceEndMSec;
        }

        private void OnResume(AsyncResumeInfo info)
        {
            OnResumeCore(info.ProcessID, info.OsThreadId, info.AsyncContextId, info.TaskId,
                _log.QPCTimeToRelMSec(info.ResolvedQpc), info.Callstack, info.CallstackEventIndex);
        }

        private void OnSuspendOrComplete(AsyncResumeInfo info)
        {
            OnSuspendCore(info.ProcessID, info.OsThreadId, info.AsyncContextId,
                _log.QPCTimeToRelMSec(info.ResolvedQpc));
        }

        // --- Core state-machine, independent of the computer so it can be unit-tested. ---

        internal void OnResumeCore(int pid, ulong osThread, uint ctx, ulong taskId,
            double relMSec, CallstackPayload? callstack, EventIndex callstackEventIndex)
        {
            var key = (pid, osThread);
            if (!_open.TryGetValue(key, out var ts))
            {
                ts = new ThreadState();
                _open[key] = ts;
            }

            // A context was already running here: close its segment so the inner context owns
            // the time from here on.
            if (ts.Stack.Count > 0)
            {
                Emit(key, ts.SegmentStart, relMSec, ts.Stack.Peek());
            }

            ts.Stack.Push(new OpenContext
            {
                ContextId = ctx,
                TaskId = taskId,
                Callstack = callstack,
                CallstackEventIndex = callstackEventIndex,
            });
            ts.SegmentStart = relMSec;
        }

        internal void OnSuspendCore(int pid, ulong osThread, uint ctx, double relMSec)
        {
            var key = (pid, osThread);
            if (!_open.TryGetValue(key, out var ts) || ts.Stack.Count == 0)
            {
                return;
            }

            // The innermost (top) context owns the time up to this suspend, regardless of which
            // context id the suspend names (out-of-order closes are rare but tolerated).
            Emit(key, ts.SegmentStart, relMSec, ts.Stack.Peek());

            if (ts.Stack.Peek().ContextId == ctx)
            {
                ts.Stack.Pop();
            }
            else
            {
                RemoveFromStack(ts.Stack, ctx);
            }

            ts.SegmentStart = relMSec; // whatever (if anything) is now on top resumes here.
        }

        /// <summary>
        /// Close out any still-open segments at the end of the trace.  Called automatically by
        /// the first query, but exposed for explicit use.
        /// </summary>
        public void FinalizeIndex()
        {
            if (_finalizedDone)
            {
                return;
            }

            foreach (var kv in _open)
            {
                var ts = kv.Value;
                if (ts.Stack.Count > 0)
                {
                    Emit(kv.Key, ts.SegmentStart, _traceEndMSec, ts.Stack.Peek());
                }
            }

            _finalizedDone = true;
        }

        /// <summary>
        /// Find the async chain that was active on (<paramref name="pid"/>,
        /// <paramref name="osThread"/>) at <paramref name="relMSec"/>.  Returns false when no
        /// chain was active (the thread was running synchronous / non-async code, or no async
        /// profiling data covers that moment).
        /// </summary>
        public bool TryGetActiveChainAt(int pid, ulong osThread, double relMSec, out AsyncChainInterval interval)
        {
            if (!_finalizedDone)
            {
                FinalizeIndex();
            }

            interval = null;
            if (!_finalized.TryGetValue((pid, osThread), out var list) || list.Count == 0)
            {
                return false;
            }

            // Segments are non-overlapping and sorted by start; the rightmost start <= relMSec
            // is the only candidate that can contain relMSec.
            int lo = 0, hi = list.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (list[mid].StartRelativeMSec <= relMSec)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found >= 0 && list[found].EndRelativeMSec > relMSec)
            {
                interval = list[found];
                return true;
            }

            return false;
        }

        private void Emit(in (int Pid, ulong Thread) key, double start, double end, OpenContext ctx)
        {
            if (end <= start)
            {
                return; // zero / negative-length segment (same-QPC resume+suspend) — skip.
            }

            if (!_finalized.TryGetValue(key, out var list))
            {
                list = new List<AsyncChainInterval>();
                _finalized[key] = list;
            }

            list.Add(new AsyncChainInterval
            {
                StartRelativeMSec = start,
                EndRelativeMSec = end,
                AsyncContextId = ctx.ContextId,
                TaskId = ctx.TaskId,
                Callstack = ctx.Callstack,
                CallstackEventIndex = ctx.CallstackEventIndex,
            });
        }

        private static void RemoveFromStack(Stack<OpenContext> stack, uint ctx)
        {
            // Rare out-of-order close: rebuild the stack without the named context.
            var keep = new List<OpenContext>(stack.Count);
            bool removed = false;
            foreach (var e in stack) // enumerates top → bottom
            {
                if (!removed && e.ContextId == ctx)
                {
                    removed = true;
                    continue;
                }
                keep.Add(e);
            }
            stack.Clear();
            for (int i = keep.Count - 1; i >= 0; i--) // restore original top → bottom order
            {
                stack.Push(keep[i]);
            }
        }

        private sealed class ThreadState
        {
            public readonly Stack<OpenContext> Stack = new Stack<OpenContext>();
            public double SegmentStart;
        }

        private struct OpenContext
        {
            public uint ContextId;
            public ulong TaskId;
            public CallstackPayload? Callstack;
            public EventIndex CallstackEventIndex;
        }
    }
}
