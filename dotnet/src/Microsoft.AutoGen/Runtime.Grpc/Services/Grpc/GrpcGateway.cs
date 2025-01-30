// Copyright (c) Microsoft Corporation. All rights reserved.
// GrpcGateway.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using Grpc.Core;
using Microsoft.AutoGen.Contracts;
using Microsoft.AutoGen.Core.Grpc;
using Microsoft.AutoGen.Protobuf;
using Microsoft.AutoGen.Runtime.Grpc.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.AutoGen.Runtime.Grpc;

public sealed class GrpcGateway : BackgroundService, IGateway
{
    private static readonly TimeSpan s_agentResponseTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<GrpcGateway> _logger;
    private readonly IClusterClient _clusterClient;
    //private readonly ConcurrentDictionary<string, AgentState> _agentState = new();
    private readonly IRegistryGrain _gatewayRegistry;
    private readonly IGateway _reference;
    // The agents supported by each worker process.
    private readonly ConcurrentDictionary<string, List<GrpcWorkerConnection>> _supportedAgentTypes = [];
    public readonly ConcurrentDictionary<IConnection, IConnection> _workers = new();
    internal readonly ConcurrentDictionary<string, GrpcWorkerConnection> _workersByConnection = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptionsByAgentType = new();
    private readonly ConcurrentDictionary<string, List<string>> _subscriptionsByTopic = new();
    private readonly ISubscriptionsGrain _subscriptions;

    private IProtoSerializationRegistry SerializationRegistry { get; } = new ProtoSerializationRegistry();

    // The mapping from agent id to worker process.
    private readonly ConcurrentDictionary<(string Type, string Key), GrpcWorkerConnection> _agentDirectory = new();
    // RPC
    private readonly ConcurrentDictionary<(GrpcWorkerConnection, string), TaskCompletionSource<RpcResponse>> _pendingRequests = new();

    public GrpcGateway(IClusterClient clusterClient, ILogger<GrpcGateway> logger)
    {
        _logger = logger;
        _clusterClient = clusterClient;
        _reference = clusterClient.CreateObjectReference<IGateway>(this);
        _gatewayRegistry = clusterClient.GetGrain<IRegistryGrain>(0);
        _subscriptions = clusterClient.GetGrain<ISubscriptionsGrain>(0);
    }

    public async ValueTask<RpcResponse> InvokeRequestAsync(RpcRequest request, CancellationToken cancellationToken = default)
    {
        //if (string.IsNullOrWhiteSpace(request.Target.Type) && string.IsNullOrWhiteSpace(request.Target.Key))
        //{
        //    // Check if this is a request to the gateway itself.
        //    switch (request.Method)
        //    {
        //        case "RegisterAgentType":
        //            {
        //                if (!request.Payload.DataType.Equals(nameof(RegisterAgentTypeRequest)))
        //                {
        //                    return new(new RpcResponse { Error = "Invalid payload type." });
        //                }

        //                //return await TryServiceRegisterAgentType(request.RequestId, request.Payload, cancellationToken).ConfigureAwait(false);
        //            }
        //    }
        //}

        var agentId = (request.Target.Type, request.Target.Key);
        if (!_agentDirectory.TryGetValue(agentId, out var connection) || connection.Completion.IsCompleted == true)
        {
            // Activate the agent on a compatible worker process.
            if (_supportedAgentTypes.TryGetValue(request.Target.Type, out var workers))
            {
                connection = workers[Random.Shared.Next(workers.Count)];
                _agentDirectory[agentId] = connection;
            }
            else
            {
                return new(new RpcResponse { Error = "Agent not found." });
            }
        }
        // Proxy the request to the agent.
        var originalRequestId = request.RequestId;
        var newRequestId = Guid.NewGuid().ToString();
        var completion = _pendingRequests[(connection, newRequestId)] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        request.RequestId = newRequestId;
        await connection.ResponseStream.WriteAsync(new Message { Request = request }, cancellationToken).ConfigureAwait(false);
        // Wait for the response and send it back to the caller.
        var response = await completion.Task.WaitAsync(s_agentResponseTimeout);
        response.RequestId = originalRequestId;
        return response;
    }

    private async ValueTask StoreAsync(AgentState value, CancellationToken __ = default)
    {
        _ = value.AgentId ?? throw new ArgumentNullException(nameof(value.AgentId));
        var agentState = _clusterClient.GetGrain<IAgentGrain>($"{value.AgentId.Type}:{value.AgentId.Key}");
        await agentState.WriteStateAsync(value, value.ETag);
    }

    private async ValueTask<AgentState> ReadAsync(Protobuf.AgentId agentId, CancellationToken _ = default)
    {
        var agentState = _clusterClient.GetGrain<IAgentGrain>($"{agentId.Type}:{agentId.Key}");
        return await agentState.ReadStateAsync();
    }

    private async ValueTask<RegisterAgentTypeResponse> RegisterAgentTypeAsync(RegisterAgentTypeRequest request, CancellationToken _ = default)
    {
        string requestId = string.Empty;
        try
        {
            var connection = _workersByConnection[requestId];
            connection.AddSupportedType(request.Type);
            _supportedAgentTypes.GetOrAdd(request.Type, _ => []).Add(connection);

            await _gatewayRegistry.RegisterAgentTypeAsync(request, _reference).ConfigureAwait(true);
            return new RegisterAgentTypeResponse
            {
                //Success = true,
                //RequestId = request.RequestId
            };
        }
        catch (Exception)
        {
            return new RegisterAgentTypeResponse
            {
                //Success = false,
                //RequestId = request.RequestId,
                //Error = ex.Message
            };
        }
    }

    private async ValueTask RegisterAgentTypeAsync(string requestId, GrpcWorkerConnection connection, RegisterAgentTypeRequest msg)
    {
        connection.AddSupportedType(msg.Type);
        _supportedAgentTypes.GetOrAdd(msg.Type, _ => []).Add(connection);

        await _gatewayRegistry.RegisterAgentTypeAsync(msg, _reference).ConfigureAwait(true);

        Message response = new()
        {
            Response = new RpcResponse
            {
                RequestId = requestId,
                Error = "",
                //Success = true
            }
        };

        await connection.ResponseStream.WriteAsync(response).ConfigureAwait(false);
    }

    public async ValueTask<AddSubscriptionResponse> SubscribeAsync(AddSubscriptionRequest request, CancellationToken _ = default)
    {
        try
        {
            await _gatewayRegistry.SubscribeAsync(request).ConfigureAwait(true);
            return new AddSubscriptionResponse
            {
                //Success = true,
                //RequestId = request.RequestId
            };
        }
        catch (Exception)
        {
            return new AddSubscriptionResponse
            {
                //Success = false,
                //RequestId = request.RequestId,
                //Error = ex.Message
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _gatewayRegistry.AddWorkerAsync(_reference);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error adding worker to registry.");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        try
        {
            await _gatewayRegistry.RemoveWorkerAsync(_reference);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error removing worker from registry.");
        }
    }

    internal async Task ConnectToWorkerProcess(IAsyncStreamReader<Message> requestStream, IServerStreamWriter<Message> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Received new connection from {Peer}.", context.Peer);
        var workerProcess = new GrpcWorkerConnection(this, requestStream, responseStream, context);
        _workers.GetOrAdd(workerProcess, workerProcess);
        _workersByConnection.GetOrAdd(context.Peer, workerProcess);
        await workerProcess.Connect().ConfigureAwait(false);
    }

    internal async Task SendMessageAsync(GrpcWorkerConnection connection, CloudEvent cloudEvent, CancellationToken cancellationToken = default)
    {
        await connection.ResponseStream.WriteAsync(new Message { CloudEvent = cloudEvent }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task OnReceivedMessageAsync(GrpcWorkerConnection connection, Message message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Received message {Message} from connection {Connection}.", message, connection);
        switch (message.MessageCase)
        {
            case Message.MessageOneofCase.Request:
                await DispatchRequestAsync(connection, message.Request);
                break;
            case Message.MessageOneofCase.Response:
                DispatchResponse(connection, message.Response);
                break;
            case Message.MessageOneofCase.CloudEvent:
                await DispatchEventAsync(message.CloudEvent, cancellationToken);
                break;
            //case Message.MessageOneofCase.RegisterAgentTypeRequest:
            //    await RegisterAgentTypeAsync(connection, message.RegisterAgentTypeRequest);
            //    break;
            //case Message.MessageOneofCase.AddSubscriptionRequest:
            //    await AddSubscriptionAsync(connection, message.AddSubscriptionRequest);
            //    break;
            default:
                // if it wasn't recognized return bad request
                await RespondBadRequestAsync(connection, $"Unknown message type for message '{message}'.");
                break;
        };
    }

    private void DispatchResponse(GrpcWorkerConnection connection, RpcResponse response)
    {
        if (!_pendingRequests.TryRemove((connection, response.RequestId), out var completion))
        {
            _logger.LogWarning("Received response for unknown request id: {RequestId}.", response.RequestId);
            return;
        }
        // Complete the request.
        completion.SetResult(response);
    }

    private async ValueTask DispatchEventAsync(CloudEvent evt, CancellationToken cancellationToken = default)
    {
        var registry = _clusterClient.GetGrain<IRegistryGrain>(0);
        //intentionally blocking
        var targetAgentTypes = await registry.GetSubscribedAndHandlingAgentsAsync(topic: evt.Source, eventType: evt.Type).ConfigureAwait(true);
        if (targetAgentTypes is not null && targetAgentTypes.Count > 0)
        {
            targetAgentTypes = targetAgentTypes.Distinct().ToList();
            var tasks = new List<Task>(targetAgentTypes.Count);
            foreach (var agentType in targetAgentTypes)
            {
                if (_supportedAgentTypes.TryGetValue(agentType, out var connections))
                {
                    // if the connection is alive, add it to the set, if not remove the connection from the list
                    var activeConnections = connections.Where(c => c.Completion?.IsCompleted == false).ToList();
                    foreach (var connection in activeConnections)
                    {
                        tasks.Add(this.SendMessageAsync(connection, evt, cancellationToken));
                    }
                }
            }
        }
        else
        {
            // log that no agent types were found
            _logger.LogWarning("No agent types found for event type {EventType}.", evt.Type);
        }
    }

    private async ValueTask DispatchRequestAsync(GrpcWorkerConnection connection, RpcRequest request)
    {
        var requestId = request.RequestId;
        if (request.Target is null)
        {
            // If the gateway knows how to service this request, treat the target as the "Gateway"
            if (request.Method == "RegisterAgent" &&
                request.Payload is not null &&
                request.Payload.DataType == nameof(RegisterAgentTypeRequest) &&
                request.Payload.Data is not null)
            {
                object? payloadData = this.SerializationRegistry.PayloadToObject(request.Payload);
                if (payloadData is RegisterAgentTypeRequest registerAgentTypeRequest)
                {
                    await RegisterAgentTypeAsync(requestId, connection, registerAgentTypeRequest).ConfigureAwait(false);
                    return;
                }
                else
                {
                    await RespondBadRequestAsync(connection, $"Invalid payload type for \"RegisterAgent\" Expected: {nameof(RegisterAgentTypeRequest)}; got: {payloadData?.GetType().Name ?? "null"}.").ConfigureAwait(false);
                    return;
                }

                //await RegisterAgentTypeAsync(requestId, connection, request.Payload).ConfigureAwait(false);
                //return;
            }

            throw new InvalidOperationException($"Request message is missing a target. Message: '{request}'.");
        }
        await InvokeRequestDelegate(connection, request, async request =>
        {
            var (gateway, isPlacement) = await _gatewayRegistry.GetOrPlaceAgent(request.Target);
            if (gateway is null)
            {
                return new RpcResponse { Error = "Agent not found and no compatible gateways were found." };
            }
            if (isPlacement)
            {
                // TODO// Activate the worker: load state
            }
            // Forward the message to the gateway and return the result.
            return await gateway.InvokeRequestAsync(request).ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private static async Task InvokeRequestDelegate(GrpcWorkerConnection connection, RpcRequest request, Func<RpcRequest, Task<RpcResponse>> func)
    {
        try
        {
            var response = await func(request);
            response.RequestId = request.RequestId;
            await connection.ResponseStream.WriteAsync(new Message { Response = response }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await connection.ResponseStream.WriteAsync(new Message { Response = new RpcResponse { RequestId = request.RequestId, Error = ex.Message } }).ConfigureAwait(false);
        }
    }

    internal void OnRemoveWorkerProcess(GrpcWorkerConnection workerProcess)
    {
        _workers.TryRemove(workerProcess, out _);
        var types = workerProcess.GetSupportedTypes();
        foreach (var type in types)
        {
            if (_supportedAgentTypes.TryGetValue(type, out var supported))
            {
                supported.Remove(workerProcess);
            }
        }
        // Any agents activated on that worker are also gone.
        foreach (var pair in _agentDirectory)
        {
            if (pair.Value == workerProcess)
            {
                ((IDictionary<(string Type, string Key), GrpcWorkerConnection>)_agentDirectory).Remove(pair);
            }
        }
    }

    private static async ValueTask RespondBadRequestAsync(GrpcWorkerConnection connection, string error)
    {
        throw new RpcException(new Status(StatusCode.InvalidArgument, error));
    }

    private async ValueTask AddSubscriptionAsync(GrpcWorkerConnection connection, AddSubscriptionRequest request)
    {
        var topic = "";
        var agentType = "";
        if (request.Subscription.TypePrefixSubscription is not null)
        {
            topic = request.Subscription.TypePrefixSubscription.TopicTypePrefix;
            agentType = request.Subscription.TypePrefixSubscription.AgentType;
        }
        else if (request.Subscription.TypeSubscription is not null)
        {
            topic = request.Subscription.TypeSubscription.TopicType;
            agentType = request.Subscription.TypeSubscription.AgentType;
        }
        _subscriptionsByAgentType[agentType] = request.Subscription;
        _subscriptionsByTopic.GetOrAdd(topic, _ => []).Add(agentType);
        await _subscriptions.SubscribeAsync(topic, agentType);
        //var response = new SubscriptionResponse { RequestId = request.RequestId, Error = "", Success = true };
        Message response = new()
        {
            Response = new()
            {
                //RequestId = request.RequestId,
                Error = "",
                //Success = true
            }
        };
        await connection.ResponseStream.WriteAsync(response).ConfigureAwait(false);
    }

    private async ValueTask DispatchEventToAgentsAsync(IEnumerable<string> agentTypes, CloudEvent evt)
    {
        var tasks = new List<Task>(agentTypes.Count());
        foreach (var agentType in agentTypes)
        {
            if (_supportedAgentTypes.TryGetValue(agentType, out var connections))
            {
                foreach (var connection in connections)
                {
                    tasks.Add(this.SendMessageAsync(connection, evt));
                }
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async ValueTask BroadcastEventAsync(CloudEvent evt, CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>(_workers.Count);
        foreach (var (_, connection) in _supportedAgentTypes)
        {

            tasks.Add(this.SendMessageAsync((IConnection)connection[0], evt, default));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    Task IGateway.SendMessageAsync(IConnection connection, CloudEvent cloudEvent)
    {
        return this.SendMessageAsync(connection, cloudEvent, default);
    }

    public async Task SendMessageAsync(IConnection connection, CloudEvent cloudEvent, CancellationToken cancellationToken = default)
    {
        var queue = (GrpcWorkerConnection)connection;
        await queue.ResponseStream.WriteAsync(new Message { CloudEvent = cloudEvent }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RemoveSubscriptionResponse> UnsubscribeAsync(RemoveSubscriptionRequest request, CancellationToken _ = default)
    {
        try
        {
            await _gatewayRegistry.UnsubscribeAsync(request).ConfigureAwait(true);
            return new RemoveSubscriptionResponse
            {
                //Success = true,
            };
        }
        catch (Exception)
        {
            return new RemoveSubscriptionResponse
            {
                //Success = false,
                //Error = ex.Message
            };
        }
    }

    private ValueTask<List<Subscription>> GetSubscriptionsAsync(GetSubscriptionsRequest request, CancellationToken _ = default)
    {
        return _gatewayRegistry.GetSubscriptionsAsync(request);
    }

    async ValueTask<RpcResponse> IGateway.InvokeRequestAsync(RpcRequest request)
    {
        return await InvokeRequestAsync(request, default).ConfigureAwait(false);
    }

    async ValueTask IGateway.BroadcastEventAsync(CloudEvent evt)
    {
        await BroadcastEventAsync(evt, default).ConfigureAwait(false);
    }

    ValueTask IGateway.StoreAsync(AgentState value)
    {
        Debug.Fail("This method should not be called.");
        return StoreAsync(value, default);
    }

    ValueTask<AgentState> IGateway.ReadAsync(Protobuf.AgentId agentId)
    {
        Debug.Fail("This method should not be called.");
        return ReadAsync(agentId, default);
    }

    ValueTask<RegisterAgentTypeResponse> IGateway.RegisterAgentTypeAsync(string requestId, RegisterAgentTypeRequest request)
    {
        Debug.Fail("This method should not be called.");
        return RegisterAgentTypeAsync(request, default);
    }

    ValueTask<AddSubscriptionResponse> IGateway.SubscribeAsync(AddSubscriptionRequest request)
    {
        Debug.Fail("This method should not be called.");
        return SubscribeAsync(request, default);
    }

    ValueTask<RemoveSubscriptionResponse> IGateway.UnsubscribeAsync(RemoveSubscriptionRequest request)
    {
        Debug.Fail("This method should not be called.");
        return UnsubscribeAsync(request, default);
    }

    ValueTask<List<Subscription>> IGateway.GetSubscriptionsAsync(GetSubscriptionsRequest request)
    {
        Debug.Fail("This method should not be called.");
        return GetSubscriptionsAsync(request);
    }
}
