﻿// Copyright (c) Microsoft. All rights reserved.
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Experimental.Agents.Agents;
using Microsoft.SemanticKernel.Experimental.Agents.Strategy;

namespace Microsoft.SemanticKernel.Experimental.Agents;

/// <summary>
/// A an <see cref="AgentNexus"/> that supports multiple interactions
/// between a set of agents.
/// </summary>
public sealed class AgentChat : AgentNexus
{
    private readonly HashSet<string> _agentIds; // Efficient existence test
    private readonly List<Agent> _agents; // Maintain order (fwiw)

    /// <summary>
    /// Indicates if completion criteria has been met.  If set, no further
    /// agent interactions will occur.  Clear to enable more agent interactions.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Settings for controlling chat behaviors.
    /// </summary>
    public NexusExecutionSettings ExecutionSettings { get; set; } = NexusExecutionSettings.Default;

    /// <summary>
    /// The agents participating in the nexus.
    /// </summary>
    public IReadOnlyList<Agent> Agents => this._agents.AsReadOnly();

    /// <summary>
    /// Implicitly convert a <see cref="AgentNexus"/> to a <see cref="NexusInvocationCallback"/>
    /// when create a <see cref="NexusAgent"/>.
    /// </summary>
    public static implicit operator NexusInvocationCallback(AgentChat nexus) => nexus.InvokeAsync;

    /// <summary>
    /// Add a <see cref="Agent"/> to the nexus.
    /// </summary>
    /// <param name="agent">The <see cref="KernelAgent"/> to add.</param>
    public void AddAgent(Agent agent)
    {
        if (this._agentIds.Add(agent.Id))
        {
            this._agents.Add(agent);
        }
    }

    /// <summary>
    /// Process a single interaction between a given <see cref="KernelAgent"/> an a <see cref="AgentNexus"/>.
    /// </summary>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        CancellationToken cancellationToken = default) =>
            this.InvokeAsync(default(ChatMessageContent), cancellationToken);

    /// <summary>
    /// Process a discrete incremental interaction between a single <see cref="KernelAgent"/> an a <see cref="AgentNexus"/>.
    /// </summary>
    /// <param name="input">Optional user input.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        string? input = null,
        CancellationToken cancellationToken = default) =>
            this.InvokeAsync(CreateUserMessage(input), cancellationToken);

    /// <summary>
    /// Process a discrete incremental interaction between a single <see cref="KernelAgent"/> an a <see cref="AgentNexus"/>.
    /// </summary>
    /// <param name="input">Optional user input.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public async IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        ChatMessageContent? input = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (this.IsComplete)
        {
            yield break;
        }

        // Use the the count, if defined and positive, otherwise:
        // - Default to a maximum limit of 99 when only a criteria is defined.
        // - Default to a single iteration if no count and no criteria is defined.
        var maximumIterations =
            this.ExecutionSettings.MaximumIterations > 0 ?
                this.ExecutionSettings.MaximumIterations :
                this.ExecutionSettings.CompletionCriteria == null ? 1 : 99;

        var selectionStrategy = this.ExecutionSettings.SelectionStrategy ?? SelectionStrategy.None;
        selectionStrategy.Bind(this);

        for (int index = 0; index < maximumIterations; index++)
        {
            // Identify next agent using strategy
            var agent = await selectionStrategy.NextAgentAsync(cancellationToken).ConfigureAwait(false);
            if (agent == null)
            {
                yield break;
            }

            await foreach (var message in base.InvokeAgentAsync(agent, input, cancellationToken))
            {
                yield return message;

                if (message.Role == AuthorRole.Assistant)
                {
                    var task = this.ExecutionSettings.CompletionCriteria?.Invoke(this.History, cancellationToken) ?? Task.FromResult(false);
                    this.IsComplete = await task.ConfigureAwait(false);
                }

                if (this.IsComplete)
                {
                    break;
                }
            }

            if (this.IsComplete)
            {
                break;
            }

            input = null;
        }
    }

    /// <summary>
    /// Process a single interaction between a given <see cref="KernelAgent"/> an a <see cref="AgentNexus"/>.
    /// </summary>
    /// <param name="agent">The agent actively interacting with the nexus.</param>
    /// <param name="input">Optional user input.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        Agent agent,
        string? input = null,
        CancellationToken cancellationToken = default) =>
        this.InvokeAgentAsync(agent, CreateUserMessage(input), cancellationToken);

    /// <summary>
    /// Process a single interaction between a given <see cref="KernelAgent"/> an a <see cref="AgentNexus"/>.
    /// </summary>
    /// <param name="agent">The agent actively interacting with the nexus.</param>
    /// <param name="input">Optional user input.</param>
    /// <param name="isJoining">Optional flag to control if agent is joining the nexus.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public async IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        Agent agent,
        ChatMessageContent? input = null,
        bool isJoining = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (isJoining)
        {
            this.AddAgent(agent);
        }

        await foreach (var message in base.InvokeAgentAsync(agent, input, cancellationToken))
        {
            yield return message;

            if (message.Role == AuthorRole.Assistant)
            {
                var task = this.ExecutionSettings.CompletionCriteria?.Invoke(this.History, cancellationToken) ?? Task.FromResult(false);
                this.IsComplete = await task.ConfigureAwait(false);
            }

            if (this.IsComplete)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentChat"/> class.
    /// </summary>
    /// <param name="agents">The agents initially participating in the nexus.</param>
    public AgentChat(params Agent[] agents)
    {
        this._agents = [.. agents];
        this._agentIds = [.. this._agents.Select(a => a.Id)];
    }
}