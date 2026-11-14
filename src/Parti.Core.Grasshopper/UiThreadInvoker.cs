// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Threading;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Runs work on the Rhino UI thread and waits for it. Grasshopper documents, their object lists
/// and their undo stacks may only be touched there, while the chat loop runs on the thread pool.
/// </summary>
/// <remarks>
/// On the UI thread the action runs inline, so the UI thread never waits on itself. From any
/// other thread the caller (a chat-loop worker) blocks until the UI thread has run the action;
/// the UI thread itself never waits on the chat loop.
/// </remarks>
internal interface IUiThreadInvoker
{
    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread and returns once it has finished. Returns
    /// <c>false</c> when the UI thread did not run it within the invoker's bound (the action may
    /// still run later). An exception thrown by the action is rethrown to the caller.
    /// </summary>
    bool Invoke(Action action);
}

/// <summary>Production <see cref="IUiThreadInvoker"/> over <see cref="RhinoApp.InvokeOnUiThread"/>.</summary>
internal sealed class RhinoUiThreadInvoker : IUiThreadInvoker
{
    /// <summary>How long a worker waits for the UI thread before giving up.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _timeout;

    internal RhinoUiThreadInvoker(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
    }

    public bool Invoke(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (!RhinoApp.InvokeRequired)
        {
            action();
            return true;
        }

        // Not disposed on purpose: after a timeout the queued callback may still run and Set it.
        var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        }));

        if (!done.Wait(_timeout))
        {
            return false;
        }
        if (failure is not null)
        {
            throw new InvalidOperationException($"UI-thread work failed: {failure.Message}", failure);
        }
        return true;
    }
}

/// <summary>Runs the action on the calling thread. Used by hermetic tests and hosts without a UI thread.</summary>
internal sealed class InlineUiThreadInvoker : IUiThreadInvoker
{
    internal static readonly InlineUiThreadInvoker Instance = new();

    public bool Invoke(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        action();
        return true;
    }
}
