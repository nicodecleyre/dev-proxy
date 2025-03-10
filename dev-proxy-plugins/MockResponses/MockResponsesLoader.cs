﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.DevProxy.Abstractions;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.MockResponses;

internal class MockResponsesLoader : IDisposable {
    private readonly ILogger _logger;
    private readonly MockResponseConfiguration _configuration;

    public MockResponsesLoader(ILogger logger, MockResponseConfiguration configuration) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    private string _responsesFilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.MocksFile);
    private FileSystemWatcher? _watcher;

    public void LoadResponses() {
        if (!File.Exists(_responsesFilePath)) {
            _logger.LogWarn($"File {_configuration.MocksFile} not found. No mocks will be provided");
            _configuration.Responses = Array.Empty<MockResponse>();
            return;
        }

        try {
            using (FileStream stream = new FileStream(_responsesFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                using (StreamReader reader = new StreamReader(stream)) {
                    var responsesString = reader.ReadToEnd();
                    var responsesConfig = JsonSerializer.Deserialize<MockResponseConfiguration>(responsesString);
                    IEnumerable<MockResponse>? configResponses = responsesConfig?.Responses;
                    if (configResponses is not null) {
                        _configuration.Responses = configResponses;
                        _logger.LogInfo($"Mock responses for {configResponses.Count()} url patterns loaded from {_configuration.MocksFile}");
                    }
                }
            }
        }
        catch (Exception ex) {
            _logger.LogError($"An error has occurred while reading {_configuration.MocksFile}:");
            _logger.LogError(ex.Message);
        }
    }

    public void InitResponsesWatcher() {
        if (_watcher is not null) {
            return;
        }

        string path = Path.GetDirectoryName(_responsesFilePath) ?? throw new InvalidOperationException($"{_responsesFilePath} is an invalid path");
        if (!File.Exists(_responsesFilePath)) {
            _logger.LogWarn($"File {_configuration.MocksFile} not found. No mocks will be provided");
            _configuration.Responses = Array.Empty<MockResponse>();
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path));
        _watcher.NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size;
        _watcher.Filter = Path.GetFileName(_responsesFilePath);
        _watcher.Changed += ResponsesFile_Changed;
        _watcher.Created += ResponsesFile_Changed;
        _watcher.Deleted += ResponsesFile_Changed;
        _watcher.Renamed += ResponsesFile_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadResponses();
    }

    private void ResponsesFile_Changed(object sender, FileSystemEventArgs e) {
        LoadResponses();
    }

    public void Dispose() {
        _watcher?.Dispose();
    }
}
