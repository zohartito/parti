// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Drawing;
using System.Linq;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Parti.Components.Chat;

namespace Parti.Components;

/// <summary>
/// Parti v0.5 chat entry point. Dropping this component on the Grasshopper canvas
/// opens a floating <see cref="ChatForm"/>; double-clicking the component brings
/// the form back to front.
///
/// The component itself is stateless — it owns no conversation history. Its only
/// outputs are a short status string describing whether the form is open.
/// </summary>
public sealed class PartiChatComponent : GH_Component
{
    public PartiChatComponent()
        : base(
            "Parti Chat",
            "Parti",
            "Opens the Parti AI chat window for the active Grasshopper document.",
            "Parti",
            "AI")
    {
    }

    public override Guid ComponentGuid => new("d7b3a1c4-2a0e-4e8e-9b4b-0000000000b1");

    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override Bitmap? Icon => null;

    /// <inheritdoc />
    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        // No inputs in v0.5 — API key / model selection are handled inside the form.
    }

    /// <inheritdoc />
    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Status",
            "S",
            "Current chat window status.",
            GH_ParamAccess.item);
    }

    /// <inheritdoc />
    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);

        // Open the window when the component first lands on the canvas. This mirrors
        // SmartHopper's AIChatComponent pattern (cheap-v1): no explicit "Run" toggle,
        // just spawn the form.
        TryOpenWindow();
    }

    /// <inheritdoc />
    public override void RemovedFromDocument(GH_Document document)
    {
        base.RemovedFromDocument(document);

        // When the last Parti Chat component leaves the document, close the
        // floating form so its disposed controller doesn't linger. If the user
        // re-adds a Parti Chat component, the form re-opens fresh.
        if (document is not null &&
            !document.Objects.OfType<PartiChatComponent>().Any(c => !ReferenceEquals(c, this)))
        {
            ChatForm.CloseIfOpen();
        }
    }

    /// <inheritdoc />
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var open = ChatForm.IsOpen;
        DA.SetData(0, open ? "Chat open" : "Chat closed");
    }

    /// <summary>
    /// Double-clicking the component brings the chat form back to front. Uses a
    /// custom attribute class to hook <see cref="GH_ComponentAttributes.RespondToMouseDoubleClick"/>.
    /// </summary>
    public override void CreateAttributes()
    {
        m_attributes = new PartiChatAttributes(this);
    }

    internal void HandleDoubleClick()
    {
        TryOpenWindow();
        ExpireSolution(true);
    }

    private static void TryOpenWindow()
    {
        try
        {
            ChatForm.ShowOrBringToFront();
        }
        catch
        {
            // Outside a live Rhino host (e.g. the build machine), Eto's Application
            // instance isn't initialised. Swallow — SolveInstance will still report
            // "Chat closed" and the user gets a proper error when they drop the
            // component in Rhino.
        }
    }

    /// <summary>
    /// Subclassed attributes so we can override
    /// <see cref="GH_ComponentAttributes.RespondToMouseDoubleClick"/>
    /// without touching the default rendering.
    /// </summary>
    private sealed class PartiChatAttributes : GH_ComponentAttributes
    {
        public PartiChatAttributes(PartiChatComponent owner) : base(owner) { }

        public override GH_ObjectResponse RespondToMouseDoubleClick(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (Owner is PartiChatComponent parti)
            {
                parti.HandleDoubleClick();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDoubleClick(sender, e);
        }
    }
}
