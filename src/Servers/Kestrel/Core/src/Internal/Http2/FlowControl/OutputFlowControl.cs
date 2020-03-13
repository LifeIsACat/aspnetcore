// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.FlowControl
{
    internal class OutputFlowControl
    {
        private FlowControl _flow;
        private ReusableQueue<ManualResetValueTaskSource<object>> _awaitableQueue;

        public OutputFlowControl(uint initialWindowSize)
        {
            _flow = new FlowControl(initialWindowSize);
        }

        public int Available => _flow.Available;
        public bool IsAborted => _flow.IsAborted;

        public ManualResetValueTaskSource<object> AvailabilityAwaitable
        {
            get
            {
                Debug.Assert(!_flow.IsAborted, $"({nameof(AvailabilityAwaitable)} accessed after abort.");
                Debug.Assert(_flow.Available <= 0, $"({nameof(AvailabilityAwaitable)} accessed with {Available} bytes available.");

                if (_awaitableQueue == null)
                {
                    _awaitableQueue = new ReusableQueue<ManualResetValueTaskSource<object>>();
                }

                // First attempt to reuse an existing awaitable in the queue
                // to save allocating a new instance.
                if (_awaitableQueue.TryEnqueueExisting(out var awaitable))
                {
                    awaitable.Reset();
                }
                else
                {
                    awaitable = new ManualResetValueTaskSource<object>();
                    _awaitableQueue.Enqueue(awaitable);
                }

                return awaitable;
            }
        }

        public void Reset(uint initialWindowSize)
        {
            // When output flow control is reused the client window size needs to be reset.
            // The client might have changed the window size before the stream is reused.
            _flow = new FlowControl(initialWindowSize);
            Debug.Assert(_awaitableQueue.Count == 0, "Queue should have been emptied by the previous stream.");
        }

        public void Advance(int bytes)
        {
            _flow.Advance(bytes);
        }

        // bytes can be negative when SETTINGS_INITIAL_WINDOW_SIZE decreases mid-connection.
        // This can also cause Available to become negative which MUST be allowed.
        // https://httpwg.org/specs/rfc7540.html#rfc.section.6.9.2
        public bool TryUpdateWindow(int bytes)
        {
            if (_flow.TryUpdateWindow(bytes))
            {
                while (_flow.Available > 0 && _awaitableQueue?.Count >= 0)
                {
                    _awaitableQueue.Dequeue().TrySetResult(null);
                }

                return true;
            }

            return false;
        }

        public void Abort()
        {
            // Make sure to set the aborted flag before running any continuations.
            _flow.Abort();

            while (_awaitableQueue?.Count > 0)
            {
                _awaitableQueue.Dequeue().TrySetResult(null);
            }
        }
    }
}
