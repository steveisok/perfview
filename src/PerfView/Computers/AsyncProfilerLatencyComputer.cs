// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace PerfView.Computers
{
    /// <summary>
    /// Generates "Async Profiler Stacks": one stack-source sample per async-context
    /// Resume → Suspend (or Completed) pair, with metric = elapsed ms and stack =
    /// the async callstack snapshot captured by the runtime profiler at the matching
    /// Resume.  The async callstack frames are emitted as hex IPs (e.g.
    /// <c>AsyncIP 0x7ffab1234000 (state=2)</c>) under a process node; symbolization
    /// of those IPs is a follow-up.
    /// </summary>
    internal sealed class AsyncProfilerLatencyComputer
    {
        private readonly TraceLog _eventLog;
        private readonly MutableTraceEventStackSource _stackSource;
        private readonly StackSourceInterner _interner;

        private struct ResumeRecord
        {
            public double TimeRelativeMSec;
            public StackSourceCallStackIndex StackIndex;
        }

        // Key = (processId, asyncContextId).  Resume → next Suspend/Complete pairing.
        private readonly Dictionary<(int Pid, uint Ctx), ResumeRecord> _pendingResumes
            = new Dictionary<(int, uint), ResumeRecord>();

        public AsyncProfilerLatencyComputer(TraceLog eventLog, MutableTraceEventStackSource stackSource)
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

            computer.AsyncContextResumed += OnResume;
            computer.AsyncContextSuspended += OnSuspendOrComplete;
            computer.AsyncContextCompleted += OnSuspendOrComplete;

            eventSource.Process();

            _stackSource.DoneAddingSamples();
        }

        private void OnResume(AsyncResumeInfo info)
        {
            var key = (info.ProcessID, info.AsyncContextId);
            _pendingResumes[key] = new ResumeRecord
            {
                TimeRelativeMSec = _eventLog.QPCTimeToRelMSec(info.ResolvedQpc),
                StackIndex = BuildAsyncStack(info),
            };
        }

        private void OnSuspendOrComplete(AsyncResumeInfo info)
        {
            var key = (info.ProcessID, info.AsyncContextId);
            if (!_pendingResumes.TryGetValue(key, out var resume))
            {
                return;
            }
            _pendingResumes.Remove(key);

            double suspendMSec = _eventLog.QPCTimeToRelMSec(info.ResolvedQpc);
            double durationMSec = suspendMSec - resume.TimeRelativeMSec;
            if (durationMSec < 0)
            {
                durationMSec = 0;
            }

            var sample = new StackSourceSample(_stackSource)
            {
                TimeRelativeMSec = resume.TimeRelativeMSec,
                Metric = (float)durationMSec,
                StackIndex = resume.StackIndex,
                Count = 1,
            };
            _stackSource.AddSample(sample);
        }

        /// <summary>
        /// Build a stack for an async-context Resume.  Layout (bottom → top):
        /// Process → AsyncContext/TaskId tag → OsThread tag → async frames in
        /// payload order (root-first → leaf-last, matching PerfView's
        /// callee-on-top convention).  If no callstack snapshot is available we
        /// emit just the tags so the user can still see the time slice.
        /// </summary>
        private StackSourceCallStackIndex BuildAsyncStack(AsyncResumeInfo info)
        {
            StackSourceCallStackIndex stack;
            var process = _eventLog.Processes.GetProcess(info.ProcessID, _eventLog.QPCTimeToRelMSec(info.ResolvedQpc));
            if (process != null)
            {
                stack = _stackSource.GetCallStackForProcess(process);
            }
            else
            {
                stack = StackSourceCallStackIndex.Invalid;
            }

            stack = _interner.CallStackIntern(
                _interner.FrameIntern($"AsyncContext {info.AsyncContextId} TaskId 0x{info.TaskId:x}"), stack);

            stack = _interner.CallStackIntern(
                _interner.FrameIntern($"OsThread ({info.OsThreadId})"), stack);

            if (info.Callstack.HasValue)
            {
                var cs = info.Callstack.Value;
                for (int i = 0; i < cs.FrameCount; i++)
                {
                    var f = cs.Frames[i];
                    string name = f.NativeIP == 0
                        ? $"AsyncFrame Handrolled (state={f.State})"
                        : $"AsyncFrame 0x{f.NativeIP:x} (state={f.State})";
                    stack = _interner.CallStackIntern(_interner.FrameIntern(name), stack);
                }
            }

            return stack;
        }
    }
}
