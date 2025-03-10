// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.DevProxy.Plugins.Inspection;

using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

public class WebSocketServer
{
    private HttpListener? listener;
    private int port;
    private WebSocket? webSocket;

    public bool IsConnected => webSocket is not null;
    public event Action<string>? MessageReceived;

    public WebSocketServer(int port)
    {
        this.port = port;
    }

    private async Task HandleMessages(WebSocket ws)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(new byte[8192]);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("[WS] Tried to receive message while already reading one.");
        }
    }

    public async void Start()
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                webSocket = webSocketContext.WebSocket;
                _ = HandleMessages(webSocket);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    public async Task SendAsync<TMsg>(TMsg message)
    {
        if (webSocket is null)
        {
            return;
        }

        var messageString = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);
        await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
