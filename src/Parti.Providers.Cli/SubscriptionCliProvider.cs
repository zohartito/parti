// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Providers.Cli;

/// <summary>
/// Runs Parti on the user's own subscription through a vendor CLI (<c>claude</c> or <c>codex</c>)
/// that is already signed in. Each provider call is one CLI process with the CLI's own tools
/// switched off, started in a fresh empty temp directory: Parti's system prompt and tool list go in
/// a file, the transcript on standard input, and the reply comes back as
/// <c>{"text", "tool_calls"}</c> JSON enforced by the CLI's schema flag (see
/// <see cref="CliReplyContract"/>). Parti's session still runs every tool call through its guards,
/// policies and plan review. The Parti process itself makes no HTTP calls with this provider; the
/// CLI talks to its vendor. No credential passes through Parti.
/// </summary>
public sealed class SubscriptionCliProvider : IAIProvider
{
    /// <summary>Note appended to standard input for the one retry after an unreadable reply.</summary>
    public const string RetryNotePrefix = "Your previous reply could not be used: ";

    private static readonly Regex SafeCallId = new("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly SubscriptionCliOptions options;
    private readonly ICliBackend backend;
    private readonly ICliProcessRunner runner;
    private readonly CliExecutableResolver resolver;
    private readonly HashSet<string> issuedIds = new(StringComparer.Ordinal);
    private readonly object idGate = new();
    private int nextGeneratedId;

    /// <summary>Creates a provider that runs the real CLI.</summary>
    public SubscriptionCliProvider(SubscriptionCliOptions options)
        : this(options, CliProcessRunner.Instance, CliExecutableResolver.Default)
    {
    }

    /// <summary>
    /// Creates a provider over an injected process runner and executable resolver (tests).
    /// Throws <see cref="ArgumentException"/> when <paramref name="options"/> are invalid.
    /// </summary>
    public SubscriptionCliProvider(SubscriptionCliOptions options, ICliProcessRunner runner, CliExecutableResolver resolver)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        this.options = options.Clone();
        this.options.Validate();
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        backend = this.options.Backend == CliBackendKind.Codex ? new CodexCliBackend() : new ClaudeCliBackend();
    }

    /// <summary>A copy of the options this provider was built with.</summary>
    public SubscriptionCliOptions Options => options.Clone();

    /// <summary>The backend (argv and output reading) for the configured CLI.</summary>
    public ICliBackend Backend => backend;

    /// <inheritdoc />
    public string Name => options.Backend == CliBackendKind.Codex ? "Subscription (Codex CLI)" : "Subscription (Claude CLI)";

    /// <summary>
    /// A placeholder, never dialled: the CLI chooses its own endpoint. Kept so the session's request
    /// has a base URL like every other provider's.
    /// </summary>
    public Uri DefaultServerUrl { get; } = new("cli://local");

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request.BaseUrl ??= DefaultServerUrl;
        request.Endpoint = string.Empty;
        request.Authentication = "none";
        return request;
    }

    /// <summary>
    /// For diagnostics: the system file and the transcript this provider would send (the transcript
    /// with a fixed <c>NONCE</c> marker).
    /// </summary>
    public string CreateRequestBody(AIRequestCall request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return new JObject
        {
            ["system"] = CliPromptRenderer.RenderSystem(request),
            ["stdin"] = CliPromptRenderer.RenderTranscript(request.Interactions, "NONCE"),
        }.ToString(Formatting.None);
    }

    /// <summary>Decodes a contract reply (<c>{"text", "tool_calls"}</c>) into interactions.</summary>
    public List<IAIInteraction> Decode(JObject response)
    {
        if (!CliReplyContract.TryParse(response, out var reply, out _))
        {
            return new List<IAIInteraction>();
        }
        return ToInteractions(reply!, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction) =>
        interaction is null ? string.Empty : CliPromptRenderer.RenderBlock(interaction, "NONCE") ?? string.Empty;

    /// <inheritdoc />
    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request = PreCall(request);
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return Error(request, $"{backend.DisplayName}: no model is configured.");
        }
        if (!SubscriptionCliOptions.IsSafeModelName(model))
        {
            return Error(request, $"{backend.DisplayName}: \"{CliText.OneLine(model)}\" is not a valid model id.");
        }

        var executableName = SubscriptionCliOptions.ExecutableName(options.Backend);
        var executable = resolver.Resolve(executableName, options.ExecutablePath);
        if (executable is null)
        {
            return Error(request, string.IsNullOrWhiteSpace(options.ExecutablePath)
                ? $"{backend.DisplayName} not found. Install {executableName} and sign in, or set its path in Settings."
                : $"{backend.DisplayName} not found at the path set in Settings.");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "parti-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            CreatePrivateDirectory(workingDirectory);
            var invocation = new CliInvocation(
                model,
                options.Effort,
                workingDirectory,
                Path.Combine(workingDirectory, "sys.txt"),
                Path.Combine(workingDirectory, "schema.json"),
                Path.Combine(workingDirectory, "last.json"),
                options.MaxOutputBytes);
            WritePrivateFile(invocation.SystemPromptPath, CliPromptRenderer.RenderSystem(request));
            WritePrivateFile(invocation.SchemaPath, CliReplyContract.SchemaJson);

            var arguments = backend.BuildArguments(invocation);
            var transcript = CliPromptRenderer.RenderTranscript(request.Interactions, NewNonce());
            var input = 0;
            var output = 0;
            string? problem = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (File.Exists(invocation.LastMessagePath))
                {
                    File.Delete(invocation.LastMessagePath);
                }
                WritePrivateFile(invocation.LastMessagePath, string.Empty);

                var stdin = attempt == 0 ? transcript : transcript + $"\n{RetryNotePrefix}{problem}. Reply again with exactly one JSON object in the reply format.\n";
                CliProcessResult process;
                try
                {
                    process = await runner.RunAsync(
                        new CliProcessRequest(executable, arguments, workingDirectory, stdin, options.Timeout, options.MaxOutputBytes),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Error(request, $"{backend.DisplayName} call cancelled.", input, output);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Error(request, $"{backend.DisplayName} could not be started ({CliText.OneLine(ex.Message)}).", input, output);
                }

                if (process.TimedOut)
                {
                    return Error(request, $"{backend.DisplayName} timed out after {options.Timeout.TotalSeconds:0} s.", input, output);
                }
                if (process.OutputLimitExceeded)
                {
                    return Error(request, $"{backend.DisplayName} output exceeded {CodexCliBackend.Megabytes(options.MaxOutputBytes)}; the reply was discarded.", input, output);
                }

                var result = backend.ParseResult(process, invocation);
                input += result.InputTokens;
                output += result.OutputTokens;
                if (result.Error is not null)
                {
                    return Error(request, result.Error, input, output);
                }

                if (result.Reply is null)
                {
                    problem = result.ParseProblem ?? "no reply was found";
                    continue;
                }
                if (CliReplyContract.TryParse(result.Reply, out var reply, out var contractProblem))
                {
                    return Success(request, reply!, input, output);
                }
                problem = contractProblem;
            }

            return Error(request, $"{backend.DisplayName}: the reply could not be read after one retry ({problem}).", input, output);
        }
        catch (IOException ex)
        {
            return Error(request, $"{backend.DisplayName}: could not prepare its temp folder ({CliText.OneLine(ex.Message)}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error(request, $"{backend.DisplayName}: could not prepare its temp folder ({CliText.OneLine(ex.Message)}).");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// One CLI call, delivered as a text delta, tool-call start/complete chunks, and the Done chunk
    /// (the CLI does not stream).
    /// </summary>
    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var final = await CallAsync(request, cancellationToken).ConfigureAwait(false);
        if (final.Success)
        {
            foreach (var text in final.Interactions.OfType<AIInteractionText>())
            {
                yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: text.Content);
            }
            foreach (var call in final.Interactions.OfType<AIInteractionToolCall>())
            {
                yield return new AIStreamChunk(AIStreamChunkKind.ToolCallStart, ToolName: call.Name, ToolUseId: call.Id, ToolArgsPartial: string.Empty);
                yield return new AIStreamChunk(AIStreamChunkKind.ToolCallComplete, ToolName: call.Name, ToolUseId: call.Id,
                    ToolArgsPartial: call.Arguments.ToString(Formatting.None));
            }
        }
        yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: final, StopReason: final.StopReason);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException("No current user SID.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            // Supply the ACL at creation; children inherit only this owner's rule.
            new DirectoryInfo(path).Create(security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void WritePrivateFile(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    private AIReturn Success(AIRequestCall request, CliReply reply, int input, int output)
    {
        // Ids already in the conversation (and every id this provider issued) are taken: the
        // session's guard rejects a replayed id, and a model may reuse "call_1" in a later reply.
        var taken = new HashSet<string>(
            (request.Interactions ?? new List<IAIInteraction>()).OfType<AIInteractionToolCall>()
                .Select(c => c.Id).Where(id => !string.IsNullOrEmpty(id))!,
            StringComparer.Ordinal);
        var ret = new AIReturn
        {
            Request = request,
            InputTokens = input,
            OutputTokens = output,
            Interactions = ToInteractions(reply, taken),
        };
        var hasCalls = ret.Interactions.OfType<AIInteractionToolCall>().Any();
        ret.StopReason = hasCalls ? "tool_use" : "end_turn";
        ret.Status = hasCalls ? AICallStatus.CallingTools : AICallStatus.Finished;
        return ret;
    }

    private List<IAIInteraction> ToInteractions(CliReply reply, HashSet<string> taken)
    {
        var interactions = new List<IAIInteraction>();
        if (!string.IsNullOrEmpty(reply.Text))
        {
            interactions.Add(new AIInteractionText { Agent = AIAgent.Assistant, Content = reply.Text });
        }

        lock (idGate)
        {
            // The CLI is stateless, so a model restarts its ids ("call_1") every turn: an id already
            // used in the conversation is renamed. A duplicate WITHIN this reply is not renamed; both
            // calls keep the same id so the session's guard rejects the batch, as it would for any
            // provider.
            var renamedInReply = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var call in reply.ToolCalls)
            {
                var original = call.Id;
                string id;
                if (!string.IsNullOrEmpty(original) && renamedInReply.TryGetValue(original, out var first))
                {
                    id = first;
                }
                else
                {
                    id = original;
                    if (!SafeCallId.IsMatch(id) || taken.Contains(id) || issuedIds.Contains(id))
                    {
                        do
                        {
                            id = $"cli_{++nextGeneratedId}";
                        }
                        while (taken.Contains(id) || issuedIds.Contains(id));
                    }
                    taken.Add(id);
                    issuedIds.Add(id);
                    if (!string.IsNullOrEmpty(original))
                    {
                        renamedInReply[original] = id;
                    }
                }
                interactions.Add(new AIInteractionToolCall
                {
                    Agent = AIAgent.ToolCall,
                    Id = id,
                    Name = call.Name,
                    Arguments = call.Arguments,
                });
            }
        }
        return interactions;
    }

    private static AIReturn Error(AIRequestCall request, string message, int input = 0, int output = 0)
    {
        var ret = new AIReturn { Request = request, InputTokens = input, OutputTokens = output };
        ret.CreateError(message, request);
        return ret;
    }

    private static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return "parti-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless; the OS cleans the temp directory.
        }
    }
}
