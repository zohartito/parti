// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Parti.Core.Grasshopper;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for the runtime-message part of the canvas snapshot (F4). Live R3 showed the
/// model claiming a circle was built while Circle CNR warned "Input parameter C failed to collect
/// data": the snapshot now carries each node's runtime messages, bounded and sanitized.
/// </summary>
public class CanvasSnapshotTests
{
    [Fact]
    public void SelectMessages_KeepsAtMostThree_InGivenOrder_AndCountsTheRest()
    {
        var messages = new List<(string, string)>
        {
            ("error", "Solution exception: boom"),
            ("warning", "Input parameter C failed to collect data"),
            ("warning", "second warning"),
            ("remark", "a remark"),
            ("remark", "another remark"),
        };

        var kept = CanvasSnapshot.SelectMessages(messages, out var omitted);

        Assert.Equal(CanvasSnapshot.MaxMessagesPerNode, kept.Count);
        Assert.Equal(2, omitted);
        Assert.Equal("error", kept[0]!["lvl"]!.GetValue<string>());
        Assert.Equal("Input parameter C failed to collect data", kept[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SelectMessages_NoMessages_IsEmpty()
    {
        var kept = CanvasSnapshot.SelectMessages(new List<(string, string)>(), out var omitted);
        Assert.Empty(kept);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void SanitizeMessage_StripsControlAndFormatCharacters_AndCollapsesWhitespace()
    {
        var text = "  line one\r\n\tline\u0007 two\u202Eevil\u001b[31m  ";
        Assert.Equal("line one line two evil [31m", CanvasSnapshot.SanitizeMessage(text));
    }

    [Fact]
    public void SanitizeMessage_TruncatesLongText()
    {
        var sanitized = CanvasSnapshot.SanitizeMessage(new string('x', 1000));
        Assert.Equal(CanvasSnapshot.MaxMessageLength, sanitized.Length);
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void SanitizeMessage_NullOrEmpty_IsEmpty()
    {
        Assert.Equal(string.Empty, CanvasSnapshot.SanitizeMessage(null));
        Assert.Equal(string.Empty, CanvasSnapshot.SanitizeMessage(""));
    }

    // ---- R5: the snapshot is bounded ----

    private static List<IGH_DocumentObject> Params(int count, string? nickName = null)
    {
        var list = new List<IGH_DocumentObject>();
        for (var i = 0; i < count; i++)
        {
            var param = new Param_Number();
            param.NewInstanceGuid();
            if (nickName != null)
            {
                param.NickName = nickName;
            }
            list.Add(param);
        }
        return list;
    }

    [Fact]
    public void GetAll_LargeCanvas_ResultIsBounded()
    {
        var objects = Params(500);

        var snapshot = CanvasSnapshot.Build(objects, "doc-1");
        // Measured on what the model receives: the adapter's Newtonsoft form.
        var bytes = Encoding.UTF8.GetByteCount(
            Parti.Core.Tools.JsonBridge.ToNewtonsoft(snapshot).ToString(Newtonsoft.Json.Formatting.None));

        Assert.True(bytes <= CanvasSnapshot.MaxSnapshotBytes, $"snapshot is {bytes} bytes");
        Assert.Equal(500, snapshot["count"]!.GetValue<int>());
        var shown = snapshot["nodes"]!.AsArray().Count;
        Assert.True(shown is > 0 and <= CanvasSnapshot.MaxNodes, $"shown {shown}");
        Assert.True(snapshot["truncated"]!.GetValue<bool>());
        Assert.Equal(shown, snapshot["shown"]!.GetValue<int>());
        Assert.Equal(500 - shown, snapshot["omitted"]!.GetValue<int>());
        Assert.Contains("gh_get_selected", snapshot["hint"]!.GetValue<string>());
        Assert.Contains("gh_describe", snapshot["hint"]!.GetValue<string>());

        // Deterministic: document order, same cut every time.
        var again = CanvasSnapshot.Build(objects, "doc-1");
        Assert.Equal(snapshot.ToJsonString(), again.ToJsonString());
        Assert.Equal(objects[0].InstanceGuid.ToString(), snapshot["nodes"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void GetAll_HugeNicknames_AreCutAndTheByteBudgetHolds()
    {
        var objects = Params(200, nickName: new string('n', 5000) + "\u0007");

        var snapshot = CanvasSnapshot.Build(objects, "doc-1");

        Assert.True(CanvasSnapshot.SerializedLength(snapshot) <= CanvasSnapshot.MaxSnapshotBytes);
        var nick = snapshot["nodes"]![0]!["nick"]!.GetValue<string>();
        Assert.Equal(CanvasSnapshot.MaxLabelLength, nick.Length);
        Assert.True(snapshot["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void SerializedLength_MeasuresTheNewtonsoftFormTheModelReceives()
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["pos"] = new System.Text.Json.Nodes.JsonArray { 1e10f, 1e10f },
            ["n"] = "caf\u00e9",
        };
        var expected = Encoding.UTF8.GetByteCount(
            Parti.Core.Tools.JsonBridge.ToNewtonsoft(node).ToString(Newtonsoft.Json.Formatting.None));

        Assert.Equal(expected, CanvasSnapshot.SerializedLength(node));
    }

    [Fact]
    public void GetAll_SmallCanvas_IsNotMarkedTruncated()
    {
        var snapshot = CanvasSnapshot.Build(Params(5), "doc-1");

        Assert.Equal(5, snapshot["nodes"]!.AsArray().Count);
        Assert.Equal(5, snapshot["count"]!.GetValue<int>());
        Assert.False(snapshot.ContainsKey("truncated"));
        Assert.False(snapshot.ContainsKey("omitted"));
    }
}
