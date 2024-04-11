﻿// Copyright (c) Microsoft. All rights reserved.
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Microsoft.SemanticKernel.Agents;

/// <summary>
/// A an <see cref="AgentChat"/> that supports multi-turn interactions.
/// </summary>
public sealed class AgentGroupChat : AgentChat
{
    private readonly HashSet<string> _agentIds; // Efficient existence test
    private readonly List<Agent> _agents; // Maintain order

    /// <summary>
    /// Indicates if completion criteria has been met.  If set, no further
    /// agent interactions will occur.  Clear to enable more agent interactions.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Settings for defining chat behavior.
    /// </summary>
    public ChatExecutionSettings ExecutionSettings { get; set; } = new ChatExecutionSettings();

    /// <summary>
    /// The agents participating in the chat.
    /// </summary>
    public IReadOnlyList<Agent> Agents => this._agents.AsReadOnly();

    /// <summary>
    /// Add a <see cref="Agent"/> to the chat.
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
    /// Process a series of interactions between the <see cref="AgentGroupChat.Agents"/> that have joined this <see cref="AgentGroupChat"/>.
    /// The interactions will proceed according to the <see cref="SelectionStrategy"/> and the <see cref="TerminationStrategy"/>
    /// defined via <see cref="AgentGroupChat.ExecutionSettings"/>.
    /// In the absence of an <see cref="ChatExecutionSettings.SelectionStrategy"/>, this method will not invoke any agents.
    /// Any agent may be explicitly selected by calling <see cref="AgentGroupChat.InvokeAsync(Agent, bool, CancellationToken)"/>.
    /// </summary>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public async IAsyncEnumerable<ChatMessageContent> InvokeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (this.IsComplete)
        {
            yield break;
        }

        // Unable to assume selection in the absence of a strategy.  This is the default.
        // For explicit selection, AgentGroupChat.InvokeAsync(Agent, CancellationToken) is available.
        if (this.ExecutionSettings.SelectionStrategy == null)
        {
            yield break;
        }

        for (int index = 0; index < this.ExecutionSettings.TerminationStrategy.MaximumIterations; index++)
        {
            // Identify next agent using strategy
            var agent = await this.ExecutionSettings.SelectionStrategy.NextAsync(this.Agents, this.History, cancellationToken).ConfigureAwait(false);
            if (agent == null)
            {
                yield break;
            }

            await foreach (var message in base.InvokeAgentAsync(agent, cancellationToken))
            {
                yield return message;

                if (message.Role == AuthorRole.Assistant)
                {
                    var task = this.ExecutionSettings.TerminationStrategy.ShouldTerminateAsync(agent, this.History, cancellationToken);
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
        }
    }

    /// <summary>
    /// Process a single interaction between a given <see cref="Agent"/> an a <see cref="AgentGroupChat"/>.
    /// </summary>
    /// <param name="agent">The agent actively interacting with the chat.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    /// <remark>
    /// Specified agent joins the chat.
    /// </remark>>
    public IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        Agent agent,
        CancellationToken cancellationToken = default) =>
        this.InvokeAsync(agent, isJoining: true, cancellationToken);

    /// <summary>
    /// Process a single interaction between a given <see cref="KernelAgent"/> an a <see cref="AgentGroupChat"/>.
    /// </summary>
    /// <param name="agent">The agent actively interacting with the chat.</param>
    /// <param name="isJoining">Optional flag to control if agent is joining the chat.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public async IAsyncEnumerable<ChatMessageContent> InvokeAsync(
        Agent agent,
        bool isJoining,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (isJoining)
        {
            this.AddAgent(agent);
        }

        await foreach (var message in base.InvokeAgentAsync(agent, cancellationToken))
        {
            yield return message;

            if (message.Role == AuthorRole.Assistant)
            {
                var task = this.ExecutionSettings.TerminationStrategy.ShouldTerminateAsync(agent, this.History, cancellationToken);
                this.IsComplete = await task.ConfigureAwait(false);
            }

            if (this.IsComplete)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentGroupChat"/> class.
    /// </summary>
    /// <param name="agents">The agents initially participating in the chat.</param>
    public AgentGroupChat(params Agent[] agents)
    {
        this._agents = new(agents);
        this._agentIds = new(this._agents.Select(a => a.Id));
    }
}