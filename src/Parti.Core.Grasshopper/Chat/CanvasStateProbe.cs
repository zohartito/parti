// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Linq;
using Grasshopper;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// Reports an opaque token for the active canvas and its undo history. The chat controller
/// compares the token taken after a Parti loop with the one taken before the next user message;
/// a difference means the canvas changed outside Parti (Undo/Redo, a manual edit, another
/// document), so the model is told to re-read it.
/// </summary>
public interface ICanvasStateProbe
{
    /// <summary>
    /// The current token, or null when no canvas is available. The text before the first
    /// <c>|</c> identifies the document (the controller uses it to spot a document switch during a
    /// loop). Safe to call from any thread; must not throw.
    /// </summary>
    string? Capture();
}

/// <summary>
/// Production <see cref="ICanvasStateProbe"/>: the active document's identity plus its undo and
/// redo record lists, read on the Rhino UI thread.
/// </summary>
public sealed class GrasshopperCanvasStateProbe : ICanvasStateProbe
{
    private readonly IUiThreadInvoker _ui;

    /// <summary>Creates the probe over the active Grasshopper canvas.</summary>
    public GrasshopperCanvasStateProbe()
    {
        // Short bound: this runs at the end of every loop, and a missing token only skips the note.
        _ui = new RhinoUiThreadInvoker(TimeSpan.FromSeconds(10));
    }

    /// <inheritdoc />
    public string? Capture()
    {
        string? token = null;
        try
        {
            string? read = null;
            if (_ui.Invoke(() => read = ReadOnUiThread()))
            {
                token = read;
            }
        }
        catch
        {
            // Rhino closing or no canvas: no token, so no note.
        }
        return token;
    }

    // UI thread only.
    private static string? ReadOnUiThread()
    {
        var document = Instances.ActiveCanvas?.Document;
        if (document is null)
        {
            return null;
        }

        var undo = document.UndoServer;
        return string.Join(
            "|",
            GrasshopperDocumentCapability.DocumentId(document),
            undo.UndoCount,
            undo.RedoCount,
            string.Join(",", undo.UndoGuids.Select(id => id.ToString("N"))),
            string.Join(",", undo.RedoGuids.Select(id => id.ToString("N"))));
    }
}
