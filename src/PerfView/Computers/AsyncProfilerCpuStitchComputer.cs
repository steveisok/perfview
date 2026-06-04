// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace PerfView.Computers
{
    /// <summary>
    /// Generates "CPU Stacks (with Async)": every CPU (PerfInfo) sample, but with the logical
    /// async continuation chain that the runtime async profiler captured for the sampled thread
    /// at the sample time grafted in as the caller context.
    ///
    /// For each sample we ask <see cref="AsyncProfilerStackIndex"/> which async chain was active
    /// on (process, thread) at the sample time.  When a chain is found and its leaf method can be
    /// unambiguously located in the native sampled stack, we splice: the stack becomes
    /// <c>Process → AsyncContext tag → async chain (root→leaf) → native frames strictly below the
    /// matched leaf</c> (the genuine sync callees where CPU is being spent).  When a chain exists
    /// but can't be confidently spliced (recursion, await-bridge leaf, no subsequence match), we
    /// fail open: the async chain is still prepended under an <c>[Async caller (unspliced)]</c>
    /// marker with the full native stack above it (honest duplication, nothing fabricated).  When
    /// no chain is active, the native stack is emitted unchanged, so this view is a superset of
    /// the plain CPU Stacks view.
    /// </summary>
    internal sealed class AsyncProfilerCpuStitchComputer
    {
        private readonly TraceLog _eventLog;
        private readonly MutableTraceEventStackSource _stackSource;
        private readonly StackSourceInterner _interner;

        // Buffered CPU samples.  We can't stitch online because the async interval covering a
        // sample at T is closed by a Suspend that arrives AFTER T; we therefore collect samples
        // during the single processing pass and splice once the index is complete.
        private struct CpuSample
        {
            public int ProcessId;
            public ulong ThreadId;
            public CallStackIndex NativeStack;
            public double TimeRelativeMSec;
        }

        private readonly List<CpuSample> _samples = new List<CpuSample>();

        public AsyncProfilerCpuStitchComputer(TraceLog eventLog, MutableTraceEventStackSource stackSource)
        {
            _eventLog = eventLog;
            _stackSource = stackSource;
            _interner = stackSource.Interner;
        }

        public void GenerateStacks()
        {
            var eventSource = _eventLog.Events.GetSource();
            var parser = new AsyncProfilerTraceEventParser(eventSource);
            var computer = new AsyncProfilerComputer(parser);
            var index = new AsyncProfilerStackIndex(computer, _eventLog);

            eventSource.Kernel.PerfInfoSample += delegate (SampledProfileTraceData data)
            {
                var csi = data.CallStackIndex();
                if (csi == CallStackIndex.Invalid)
                {
                    return;
                }

                _samples.Add(new CpuSample
                {
                    ProcessId = data.ProcessID,
                    ThreadId = (ulong)data.ThreadID,
                    NativeStack = csi,
                    TimeRelativeMSec = data.TimeStampRelativeMSec,
                });
            };

            eventSource.Process();
            index.FinalizeIndex();

            foreach (var s in _samples)
            {
                var stackIndex = BuildStitchedStack(s, index);
                if (stackIndex == StackSourceCallStackIndex.Invalid)
                {
                    continue;
                }

                var sample = new StackSourceSample(_stackSource)
                {
                    TimeRelativeMSec = s.TimeRelativeMSec,
                    Metric = 1,
                    StackIndex = stackIndex,
                    Count = 1,
                };
                _stackSource.AddSample(sample);
            }

            _stackSource.DoneAddingSamples();
        }

        private StackSourceCallStackIndex BuildStitchedStack(in CpuSample s, AsyncProfilerStackIndex index)
        {
            var process = _eventLog.Processes.GetProcess(s.ProcessId, s.TimeRelativeMSec);
            StackSourceCallStackIndex procStack = process != null
                ? _stackSource.GetCallStackForProcess(process)
                : StackSourceCallStackIndex.Invalid;

            // No async chain active here → plain native stack rooted at the process (superset).
            if (!index.TryGetActiveChainAt(s.ProcessId, s.ThreadId, s.TimeRelativeMSec, out var interval) ||
                !interval.Callstack.HasValue || interval.Callstack.Value.FrameCount == 0)
            {
                return _stackSource.GetCallStack(s.NativeStack, procStack);
            }

            var cs = interval.Callstack.Value;
            int m = cs.FrameCount;

            // Symbolize the async chain (payload order is leaf → root, innermost first) to
            // MethodIndex + stack-source frame.
            var asyncMethods = new int[m];
            var asyncFrames = new StackSourceFrameIndex[m];
            for (int i = 0; i < m; i++)
            {
                var f = cs.Frames[i];
                CodeAddressIndex cai = (f.NativeIP != 0 && interval.CallstackEventIndex != EventIndex.Invalid)
                    ? _eventLog.GetCodeAddressIndexAtEvent(f.NativeIP, interval.CallstackEventIndex)
                    : CodeAddressIndex.Invalid;
                asyncMethods[i] = cai != CodeAddressIndex.Invalid ? (int)_eventLog.CodeAddresses.MethodIndex(cai) : -1;
                asyncFrames[i] = AsyncFrameIndex(f, cai);
            }

            // Strip any leading AsyncHelpers.Await bridge frames; the first remaining frame is the
            // chain's current (innermost real) method, the rest are its logical callers.
            int real = 0;
            while (real < m && IsAwaitBridge(asyncMethods[real]))
            {
                real++;
            }

            // Native stack root → leaf (CallStacks walks leaf → root).
            var nativeCodeAddrs = new List<CodeAddressIndex>();
            for (CallStackIndex c = s.NativeStack; c != CallStackIndex.Invalid; c = _eventLog.CallStacks.Caller(c))
            {
                nativeCodeAddrs.Add(_eventLog.CallStacks.CodeAddressIndex(c));
            }
            nativeCodeAddrs.Reverse();

            var nativeMethods = new int[nativeCodeAddrs.Count];
            for (int i = 0; i < nativeCodeAddrs.Count; i++)
            {
                nativeMethods[i] = nativeCodeAddrs[i] != CodeAddressIndex.Invalid
                    ? (int)_eventLog.CodeAddresses.MethodIndex(nativeCodeAddrs[i])
                    : -1;
            }

            int anchor = -1;
            bool canSplice = real < m &&
                AsyncStackStitcher.TryFindAnchor(new ArraySegment<int>(asyncMethods, real, m - real), nativeMethods, out anchor) &&
                HasResumePlumbingAbove(nativeMethods, anchor);

            if (canSplice)
            {
                // Process → logical async callers (root → leaf) → current method (the native
                // anchor frame) → native sync callees below it.  The resume plumbing above the
                // anchor (threadpool dispatch / ContinuationWrapper / IL_STUB_AsyncResume) is
                // dropped in favor of the real logical caller chain.
                StackSourceCallStackIndex stack = procStack;
                for (int i = m - 1; i > real; i--) // callers, root-most first
                {
                    stack = _interner.CallStackIntern(asyncFrames[i], stack);
                }
                for (int j = anchor; j < nativeCodeAddrs.Count; j++) // current method + callees
                {
                    stack = _interner.CallStackIntern(NativeFrameIndex(nativeCodeAddrs[j]), stack);
                }
                return stack;
            }

            // Fail open: prepend the async chain (clearly marked) with the full native stack above.
            StackSourceCallStackIndex baseStack = BuildUnsplicedBase(procStack, interval, asyncFrames);
            return _stackSource.GetCallStack(s.NativeStack, baseStack);
        }

        /// <summary>
        /// Build the fail-open base when a chain exists but can't be confidently spliced:
        /// Process → AsyncContext tag → [Async caller (unspliced)] marker → async chain frames in
        /// root → leaf order.  The caller appends the full native stack on top of this.
        /// </summary>
        private StackSourceCallStackIndex BuildUnsplicedBase(StackSourceCallStackIndex procStack,
            AsyncChainInterval interval, StackSourceFrameIndex[] asyncFrames)
        {
            StackSourceCallStackIndex stack = _interner.CallStackIntern(
                _interner.FrameIntern($"AsyncContext {interval.AsyncContextId} TaskId 0x{interval.TaskId:x}"), procStack);

            stack = _interner.CallStackIntern(_interner.FrameIntern("[Async caller (unspliced)]"), stack);

            for (int i = asyncFrames.Length - 1; i >= 0; i--) // payload is leaf → root; emit root → leaf
            {
                stack = _interner.CallStackIntern(asyncFrames[i], stack);
            }

            return stack;
        }

        private bool IsAwaitBridge(int methodIndex)
        {
            if (methodIndex < 0)
            {
                return false; // unresolved — not a recognizable await bridge.
            }

            string name = _eventLog.CodeAddresses.Methods.FullMethodName((MethodIndex)methodIndex);
            return name != null &&
                name.IndexOf("AsyncHelpers", StringComparison.Ordinal) >= 0 &&
                name.IndexOf("Await", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Confirms the native frames above the splice anchor contain the runtime-async resume
        /// plumbing (IL_STUB_AsyncResume / ContinuationWrapper).  A genuine async continuation is
        /// always reached through this plumbing; requiring it prevents grafting logical callers
        /// onto an unrelated *synchronous* call of the same method (a stale-chain false positive).
        /// </summary>
        private bool HasResumePlumbingAbove(int[] nativeMethods, int anchor)
        {
            for (int i = 0; i < anchor; i++)
            {
                int mi = nativeMethods[i];
                if (mi < 0)
                {
                    continue;
                }

                string name = _eventLog.CodeAddresses.Methods.FullMethodName((MethodIndex)mi);
                if (name != null &&
                    (name.IndexOf("AsyncResume", StringComparison.Ordinal) >= 0 ||
                     name.IndexOf("ContinuationWrapper", StringComparison.Ordinal) >= 0))
                {
                    return true;
                }
            }

            return false;
        }

        private StackSourceFrameIndex AsyncFrameIndex(AsyncFrame frame, CodeAddressIndex cai)
        {
            if (cai != CodeAddressIndex.Invalid)
            {
                return _stackSource.GetFrameIndex(cai);
            }

            string name = frame.NativeIP == 0
                ? $"AsyncFrame Handrolled (state={frame.State})"
                : $"AsyncFrame 0x{frame.NativeIP:x} (state={frame.State})";
            return _interner.FrameIntern(name);
        }

        private StackSourceFrameIndex NativeFrameIndex(CodeAddressIndex cai)
        {
            return cai != CodeAddressIndex.Invalid
                ? _stackSource.GetFrameIndex(cai)
                : _interner.FrameIntern("?!?");
        }
    }
}
