﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft.DevProxy.Plugins.RandomErrors;
internal enum GraphRandomErrorFailMode {
    Random,
    PassThru
}

public class GraphRandomErrorConfiguration {
    public List<int> AllowedErrors { get; set; } = new();
}

public class GraphRandomErrorPlugin : BaseProxyPlugin {
    private readonly Option<IEnumerable<int>> _allowedErrors;
    private readonly GraphRandomErrorConfiguration _configuration = new();
    private IProxyConfiguration? _proxyConfiguration;

    public override string Name => nameof(GraphRandomErrorPlugin);

    private const int retryAfterInSeconds = 5;
    private readonly Dictionary<string, HttpStatusCode[]> _methodStatusCode = new()
    {
        {
            "GET", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "POST", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PUT", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PATCH", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "DELETE", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        }
    };
    private readonly Random _random;

    public GraphRandomErrorPlugin() {
        _allowedErrors = new Option<IEnumerable<int>>("--allowed-errors", "List of errors that Dev Proxy may produce");
        _allowedErrors.AddAlias("-a");
        _allowedErrors.ArgumentHelpName = "allowed errors";
        _allowedErrors.AllowMultipleArgumentsPerToken = true;

        _random = new Random();
    }

    // uses config to determine if a request should be failed
    private GraphRandomErrorFailMode ShouldFail(ProxyRequestArgs e) => _random.Next(1, 100) <= _proxyConfiguration?.Rate ? GraphRandomErrorFailMode.Random : GraphRandomErrorFailMode.PassThru;

    private void FailResponse(ProxyRequestArgs e) {
        // pick a random error response for the current request method
        var methodStatusCodes = _methodStatusCode[e.Session.HttpClient.Request.Method];
        var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];
        UpdateProxyResponse(e, errorStatus);
    }

    private void FailBatch(ProxyRequestArgs e) {
        var batchResponse = new GraphBatchResponsePayload();

        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(e.Session.HttpClient.Request.BodyString);
        if (batch == null) {
            UpdateProxyBatchResponse(e, batchResponse);
            return;
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            try {
                // pick a random error response for the current request method
                var methodStatusCodes = _methodStatusCode[request.Method];
                var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];

                var response = new GraphBatchResponsePayloadResponse {
                    Id = request.Id,
                    Status = (int)errorStatus,
                    Body = new GraphBatchResponsePayloadResponseBody {
                        Error = new GraphBatchResponsePayloadResponseBodyError {
                            Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                            Message = "Some error was generated by the proxy.",
                        }
                    }
                };

                if (errorStatus == HttpStatusCode.TooManyRequests) {
                    var retryAfterDate = DateTime.Now.AddSeconds(retryAfterInSeconds);
                    var requestUrl = ProxyUtils.GetAbsoluteRequestUrlFromBatch(e.Session.HttpClient.Request.RequestUri, request.Url);
                    e.ThrottledRequests.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(requestUrl), ShouldThrottle, retryAfterDate));
                    response.Headers = new Dictionary<string, string>{
                        { "Retry-After", retryAfterInSeconds.ToString() }
                    };
                }

                responses.Add(response);
            }
            catch {}
        }
        batchResponse.Responses = responses.ToArray();

        UpdateProxyBatchResponse(e, batchResponse);
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey) {
        var throttleKeyForRequest = GraphUtils.BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? retryAfterInSeconds : 0, "Retry-After");
    }

    private void UpdateProxyResponse(ProxyRequestArgs ev, HttpStatusCode errorStatus) {
        SessionEventArgs session = ev.Session;
        string requestId = Guid.NewGuid().ToString();
        string requestDate = DateTime.Now.ToString();
        Request request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);
        if (errorStatus == HttpStatusCode.TooManyRequests) {
            var retryAfterDate = DateTime.Now.AddSeconds(retryAfterInSeconds);
            ev.ThrottledRequests.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(request), ShouldThrottle, retryAfterDate));
            headers.Add(new HttpHeader("Retry-After", retryAfterInSeconds.ToString()));
        }

        string body = JsonSerializer.Serialize(new GraphErrorResponseBody(
            new GraphErrorResponseError {
                Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                Message = BuildApiErrorMessage(request),
                InnerError = new GraphErrorResponseInnerError {
                    RequestId = requestId,
                    Date = requestDate
                }
            })
        );
        _logger?.LogRequest(new[] { $"{(int)errorStatus} {errorStatus.ToString()}" }, MessageType.Chaos, new LoggingContext(ev.Session));
        session.GenericResponse(body ?? string.Empty, errorStatus, headers);
    }

    private void UpdateProxyBatchResponse(ProxyRequestArgs ev, GraphBatchResponsePayload response) {
        // failed batch uses a fixed 424 error status code
        var errorStatus = HttpStatusCode.FailedDependency;

        SessionEventArgs session = ev.Session;
        string requestId = Guid.NewGuid().ToString();
        string requestDate = DateTime.Now.ToString();
        Request request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);

        var options = new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        string body = JsonSerializer.Serialize(response, options);
        _logger?.LogRequest(new[] { $"{(int)errorStatus} {errorStatus.ToString()}" }, MessageType.Chaos, new LoggingContext(ev.Session));
        session.GenericResponse(body, errorStatus, headers);
    }

    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : String.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";

    public override void Register(IPluginEvents pluginEvents,
                         IProxyContext context,
                         ISet<UrlToWatch> urlsToWatch,
                         IConfigurationSection? configSection = null) {
        base.Register(pluginEvents, context, urlsToWatch, configSection);

        configSection?.Bind(_configuration);
        pluginEvents.Init += OnInit;
        pluginEvents.OptionsLoaded += OnOptionsLoaded;
        pluginEvents.BeforeRequest += OnRequest;

        // needed to get the failure rate configuration
        // must keep reference of the whole config rather than just rate
        // because rate is an int and can be set through command line args
        // which is done after plugins have been registered
        _proxyConfiguration = context.Configuration;
    }

    private void OnInit(object? sender, InitArgs e) {
        e.RootCommand.AddOption(_allowedErrors);
    }

    private void OnOptionsLoaded(object? sender, OptionsLoadedArgs e) {
        InvocationContext context = e.Context;

        // Configure the allowed errors
        IEnumerable<int>? allowedErrors = context.ParseResult.GetValueForOption(_allowedErrors);
        if (allowedErrors?.Any() ?? false)
            _configuration.AllowedErrors = allowedErrors.ToList();

        if (_configuration.AllowedErrors.Any()) {
            foreach (string k in _methodStatusCode.Keys) {
                _methodStatusCode[k] = _methodStatusCode[k].Where(e => _configuration.AllowedErrors.Any(a => (int)e == a)).ToArray();
            }
        }
    }

    private async Task OnRequest(object? sender, ProxyRequestArgs e) {
        var session = e.Session;
        var state = e.ResponseState;
        if (!e.ResponseState.HasBeenSet
            && _urlsToWatch is not null
            && e.ShouldExecute(_urlsToWatch)) {
            var failMode = ShouldFail(e);

            if (failMode == GraphRandomErrorFailMode.PassThru && _proxyConfiguration?.Rate != 100) {
                return;
            }
            if (ProxyUtils.IsGraphBatchUrl(e.Session.HttpClient.Request.RequestUri)) {
                FailBatch(e);
            }
            else {
                FailResponse(e);
            }
            state.HasBeenSet = true;
        }
    }
}
