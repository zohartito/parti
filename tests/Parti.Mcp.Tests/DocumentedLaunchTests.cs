// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Parti.Mcp.Tests;

public class DocumentedLaunchTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Parti.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Parti.sln");
    }

    private static string Server => Path.Combine(AppContext.BaseDirectory, "Parti.Mcp.dll");

    [Fact]
    public async Task DocumentedClientEnvironment_StartsStubSubprocess()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Parti.Mcp", "README.md"));
        var config = Regex.Match(readme, "```(?:json)?\\n(\\{.*?\\})\\n```", RegexOptions.Singleline);
        var env = JObject.Parse(config.Groups[1].Value)["mcpServers"]!["parti"]!["env"] as JObject;
        // Explicitly ignore the test host's roll-forward setting: clients need their own.
        var variables = new System.Collections.Generic.Dictionary<string, string?> { ["DOTNET_ROLL_FORWARD"] = null };
        if (env is not null)
            foreach (var item in env.Properties()) variables[item.Name] = (string?)item.Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = new[] { Server, "--dispatcher", "stub" },
            EnvironmentVariables = variables,
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "gh_add_component");
    }

    [Theory]
    [InlineData("--dispatcher", "live")]
    [InlineData("--help", "")]
    public async Task UnsupportedLiveMode_DoesNotAdvertiseNonexistentServer(string argument, string value)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Server);
        start.ArgumentList.Add(argument);
        if (value.Length > 0) start.ArgumentList.Add(value);
        start.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(value == "live" ? 1 : 0, process.ExitCode);
        Assert.Contains("not implemented", await error);
        Assert.DoesNotContain("in-process", await error);
    }
}
