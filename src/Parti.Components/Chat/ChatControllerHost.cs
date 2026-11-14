// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;

namespace Parti.Components.Chat;

/// <summary>What <see cref="ChatControllerHost.Apply"/> did.</summary>
public enum ApplyOutcome
{
    /// <summary>The configuration equals the current one: nothing was rebuilt; history, brief, questions and locks stay.</summary>
    Unchanged,

    /// <summary>The first controller was built.</summary>
    Created,

    /// <summary>A new controller replaced the old one: the conversation restarts, the locks are kept.</summary>
    Replaced,

    /// <summary>A loop is running; nothing changed.</summary>
    Busy,

    /// <summary>The new controller could not be built; the previous one stays (see <see cref="ChatControllerHost.LastError"/>).</summary>
    Failed,
}

/// <summary>A controller built for one provider configuration, with the provider behind it.</summary>
public sealed record ChatControllerBuild(ChatController Controller, ProviderHandle Handle);

/// <summary>
/// Owns the chat window's controller across settings changes, without Eto so it can be tested.
/// <list type="bullet">
///   <item>One <see cref="ComponentLockRegistry"/> for the window's lifetime, handed to every
///   controller it builds, so a provider or model switch never drops the user's locks.</item>
///   <item>Applying an unchanged configuration rebuilds nothing: history, the design brief, a
///   pending <c>ask_user</c> and the locks all stay.</item>
///   <item>A real change is transactional: while a loop runs, or when the new controller cannot
///   be built, the previous controller and configuration stay in place.</item>
/// </list>
/// </summary>
public sealed class ChatControllerHost
{
    private readonly Func<ProviderConfiguration, ComponentLockRegistry, ChatControllerBuild> _build;

    /// <summary>Creates a host that builds controllers with <paramref name="build"/>.</summary>
    /// <param name="build">Builds a controller for a configuration over the host's lock registry.</param>
    public ChatControllerHost(Func<ProviderConfiguration, ComponentLockRegistry, ChatControllerBuild> build)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
    }

    /// <summary>The lock registry every controller of this host shares.</summary>
    public ComponentLockRegistry Locks { get; } = new();

    /// <summary>The configuration of <see cref="Controller"/>, or null before the first build.</summary>
    public ProviderConfiguration? Configuration { get; private set; }

    /// <summary>The live controller, or null before the first successful build.</summary>
    public ChatController? Controller { get; private set; }

    /// <summary>The provider behind <see cref="Controller"/>.</summary>
    public ProviderHandle? Handle { get; private set; }

    /// <summary>Why the last <see cref="Apply"/> returned <see cref="ApplyOutcome.Failed"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Makes <paramref name="next"/> the live configuration. UI thread only (it is the only caller
    /// that swaps controllers).
    /// </summary>
    public ApplyOutcome Apply(ProviderConfiguration next)
    {
        if (next is null)
        {
            throw new ArgumentNullException(nameof(next));
        }

        LastError = null;
        var current = Controller;
        if (current?.IsBusy == true)
        {
            // Replacing the controller would dispose its document lease while a dispatch settles.
            return ApplyOutcome.Busy;
        }
        if (current is not null && next == Configuration)
        {
            return ApplyOutcome.Unchanged;
        }

        ChatControllerBuild built;
        try
        {
            built = _build(next, Locks) ?? throw new InvalidOperationException("The controller factory returned nothing.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return ApplyOutcome.Failed;
        }

        if (current?.IsBusy == true)
        {
            // A loop started while the replacement was being built: keep the running one.
            DisposeQuietly(built.Controller);
            DisposeQuietly(built.Handle.Provider as IDisposable);
            return ApplyOutcome.Busy;
        }

        var oldHandle = Handle;
        DisposeQuietly(current);
        Controller = built.Controller;
        Handle = built.Handle;
        Configuration = next;

        // The old controller is idle, so its provider (and its HTTP client) can go too.
        if (!ReferenceEquals(oldHandle?.Provider, built.Handle.Provider))
        {
            DisposeQuietly(oldHandle?.Provider as IDisposable);
        }
        return current is null ? ApplyOutcome.Created : ApplyOutcome.Replaced;
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Defensive: a failing dispose must not strand the swap half done.
        }
    }
}
