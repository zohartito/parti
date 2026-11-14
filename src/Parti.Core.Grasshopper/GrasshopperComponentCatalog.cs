// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Grasshopper;
using Parti.Core.Tools;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Live <see cref="IComponentCatalog"/> over <c>Grasshopper.Instances.ComponentServer</c>
/// (<c>ObjectProxies</c> and <c>Libraries</c>). The snapshot is taken lazily, on the UI thread, on
/// first use, and rebuilt whenever the proxy or library count changes (a plugin finished loading).
/// Strings are cleaned by <see cref="ComponentCatalogSnapshot"/>; library locations are never read.
/// </summary>
public sealed class GrasshopperComponentCatalog : IComponentCatalog
{
    private readonly Func<int> _probe;
    private readonly Func<(IReadOnlyList<ComponentCatalogEntry> Entries, IReadOnlyList<LibraryEntry> Libraries)> _build;
    private readonly Action<Action> _runOnUiThread;
    private readonly object _gate = new();
    private ComponentCatalogSnapshot? _snapshot;
    private int _snapshotProbe = int.MinValue;

    /// <summary>Creates the live catalog. Nothing is read until the first member access.</summary>
    public GrasshopperComponentCatalog()
        : this(LiveProbe, LiveBuild, RunOnUiThread)
    {
    }

    /// <summary>Test seam: the probe, the builder, and the UI-thread marshaller.</summary>
    internal GrasshopperComponentCatalog(
        Func<int> probe,
        Func<(IReadOnlyList<ComponentCatalogEntry> Entries, IReadOnlyList<LibraryEntry> Libraries)> build,
        Action<Action> runOnUiThread)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));
    }

    /// <inheritdoc />
    public IReadOnlyList<ComponentCatalogEntry> All => Current().All;

    /// <inheritdoc />
    public bool TryGet(Guid componentGuid, [MaybeNullWhen(false)] out ComponentCatalogEntry entry) =>
        Current().TryGet(componentGuid, out entry);

    /// <inheritdoc />
    public IReadOnlyList<LibraryEntry> Libraries() => Current().Libraries();

    private ComponentCatalogSnapshot Current()
    {
        ComponentCatalogSnapshot? current = null;
        _runOnUiThread(() =>
        {
            var probe = _probe();
            lock (_gate)
            {
                if (_snapshot is null || probe != _snapshotProbe)
                {
                    var (entries, libraries) = _build();
                    _snapshot = new ComponentCatalogSnapshot(entries, libraries);
                    // A negative probe means the component server is not up yet: serve the empty
                    // snapshot but rebuild on the next access.
                    _snapshotProbe = probe < 0 ? int.MinValue : probe;
                }
                current = _snapshot;
            }
        });
        return current ?? throw new InvalidOperationException("The Grasshopper component catalog could not be read on the UI thread.");
    }

    private static void RunOnUiThread(Action action)
    {
        if (RhinoApp.InvokeRequired)
        {
            RhinoApp.InvokeAndWait(action);
        }
        else
        {
            action();
        }
    }

    private static int LiveProbe()
    {
        var server = Instances.ComponentServer;
        if (server is null)
        {
            return -1;
        }
        return unchecked((server.ObjectProxies.Count * 397) ^ server.Libraries.Count);
    }

    private static (IReadOnlyList<ComponentCatalogEntry>, IReadOnlyList<LibraryEntry>) LiveBuild()
    {
        var server = Instances.ComponentServer;
        if (server is null)
        {
            return (Array.Empty<ComponentCatalogEntry>(), Array.Empty<LibraryEntry>());
        }

        var libraryNames = new Dictionary<Guid, string>();
        var libraries = new List<LibraryEntry>();
        foreach (var library in server.Libraries)
        {
            if (library is null)
            {
                continue;
            }
            libraryNames[library.Id] = library.Name ?? string.Empty;
            libraries.Add(new LibraryEntry(
                library.Name ?? string.Empty,
                library.Version ?? string.Empty,
                library.AuthorName ?? string.Empty,
                library.IsCoreLibrary));
        }

        var entries = new List<ComponentCatalogEntry>(server.ObjectProxies.Count);
        foreach (var proxy in server.ObjectProxies)
        {
            if (proxy?.Desc is null)
            {
                continue;
            }
            if (!libraryNames.TryGetValue(proxy.LibraryGuid, out var libraryName))
            {
                libraryName = server.FindAssembly(proxy.LibraryGuid)?.Name ?? string.Empty;
            }
            entries.Add(new ComponentCatalogEntry(
                proxy.Guid,
                proxy.Desc.Name ?? string.Empty,
                proxy.Desc.NickName ?? string.Empty,
                proxy.Desc.Category ?? string.Empty,
                proxy.Desc.SubCategory ?? string.Empty,
                libraryName,
                proxy.Obsolete,
                proxy.Exposure.ToString(),
                proxy.Desc.Description ?? string.Empty));
        }
        return (entries, libraries);
    }
}
