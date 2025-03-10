// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.DevProxy.Abstractions;

namespace Microsoft.DevProxy.Plugins.MockResponses;

public class GraphMockResponsePlugin : MockResponsePlugin
{
    public override string Name => nameof(GraphMockResponsePlugin);

    protected override async Task OnRequest(object? sender, ProxyRequestArgs e)
    {
        if (!ProxyUtils.IsGraphBatchUrl(e.Session.HttpClient.Request.RequestUri))
        {
            // not a batch request, use the basic mock functionality
            await base.OnRequest(sender, e);
            return;
        }

        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(e.Session.HttpClient.Request.BodyString);
        if (batch == null)
        {
            await base.OnRequest(sender, e);
            return;
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            GraphBatchResponsePayloadResponse? response = null;
            var requestId = Guid.NewGuid().ToString();
            var requestDate = DateTime.Now.ToString();
            var headers = ProxyUtils
                .BuildGraphResponseHeaders(e.Session.HttpClient.Request, requestId, requestDate)
                .ToDictionary(h => h.Name, h => h.Value);

            var mockResponse = GetMatchingMockResponse(request, e.Session.HttpClient.Request.RequestUri);
            if (mockResponse == null)
            {
                response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)HttpStatusCode.BadGateway,
                    Headers = headers,
                    Body = new GraphBatchResponsePayloadResponseBody
                    {
                        Error = new GraphBatchResponsePayloadResponseBodyError
                        {
                            Code = "BadGateway",
                            Message = "No mock response found for this request"
                        }
                    }
                };

                _logger?.LogRequest(new[] { $"502 {request.Url}" }, MessageType.Mocked, new LoggingContext(e.Session));
            }
            else
            {
                dynamic? body = null;
                var statusCode = HttpStatusCode.OK;
                if (mockResponse.ResponseCode is not null)
                {
                    statusCode = (HttpStatusCode)mockResponse.ResponseCode;
                }

                if (mockResponse.ResponseHeaders is not null)
                {
                    foreach (var key in mockResponse.ResponseHeaders.Keys)
                    {
                        headers[key] = mockResponse.ResponseHeaders[key];
                    }
                }
                // default the content type to application/json unless set in the mock response
                if (!headers.Any(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                {
                    headers.Add("content-type", "application/json");
                }

                if (mockResponse.ResponseBody is not null)
                {
                    var bodyString = JsonSerializer.Serialize(mockResponse.ResponseBody) as string;
                    // we get a JSON string so need to start with the opening quote
                    if (bodyString?.StartsWith("\"@") ?? false)
                    {
                        // we've got a mock body starting with @-token which means we're sending
                        // a response from a file on disk
                        // if we can read the file, we can immediately send the response and
                        // skip the rest of the logic in this method
                        // remove the surrounding quotes and the @-token
                        var filePath = Path.Combine(Path.GetDirectoryName(_configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(bodyString.Trim('"').Substring(1)));
                        if (!File.Exists(filePath))
                        {
                            _logger?.LogError($"File {filePath} not found. Serving file path in the mock response");
                            body = bodyString;
                        }
                        else
                        {
                            var bodyBytes = File.ReadAllBytes(filePath);
                            body = Convert.ToBase64String(bodyBytes);
                        }
                    }
                    else
                    {
                        body = mockResponse.ResponseBody;
                    }
                }
                response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)statusCode,
                    Headers = headers,
                    Body = body
                };

                _logger?.LogRequest(new[] { $"{mockResponse.ResponseCode ?? 200} {mockResponse.Url}" }, MessageType.Mocked, new LoggingContext(e.Session));
            }

            responses.Add(response);
        }

        var batchRequestId = Guid.NewGuid().ToString();
        var batchRequestDate = DateTime.Now.ToString();
        var batchHeaders = ProxyUtils.BuildGraphResponseHeaders(e.Session.HttpClient.Request, batchRequestId, batchRequestDate);
        var batchResponse = new GraphBatchResponsePayload
        {
            Responses = responses.ToArray()
        };
        e.Session.GenericResponse(JsonSerializer.Serialize(batchResponse), HttpStatusCode.OK, batchHeaders);
    }

    protected MockResponse? GetMatchingMockResponse(GraphBatchRequestPayloadRequest request, Uri batchRequestUri)
    {
        if (_configuration.NoMocks ||
            _configuration.Responses is null ||
            !_configuration.Responses.Any())
        {
            return null;
        }

        var mockResponse = _configuration.Responses.FirstOrDefault(mockResponse =>
        {
            if (mockResponse.Method != request.Method) return false;
            // URLs in batch are relative to Graph version number so we need
            // to make them absolute using the batch request URL
            var absoluteRequestFromBatchUrl = ProxyUtils
                .GetAbsoluteRequestUrlFromBatch(batchRequestUri, request.Url)
                .ToString();
            if (mockResponse.Url == absoluteRequestFromBatchUrl)
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!mockResponse.Url.Contains('*'))
            {
                return false;
            }

            //turn mock URL with wildcard into a regex and match against the request URL
            var mockResponseUrlRegex = Regex.Escape(mockResponse.Url).Replace("\\*", ".*");
            return Regex.IsMatch(absoluteRequestFromBatchUrl, $"^{mockResponseUrlRegex}$");
        });
        return mockResponse;
    }
}