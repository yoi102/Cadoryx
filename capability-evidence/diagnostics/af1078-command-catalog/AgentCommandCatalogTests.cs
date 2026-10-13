using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentCommandCatalogTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("active")]
    [InlineData("detached")]
    public async Task StatusSeparatesDedicatedRoutesFromCallableCommandsWithoutChangingDocument(string state)
    {
        var kernel = new NoGeometryKernel();
        var assets = new MemoryAssetStore();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Catalog"), assets, kernel, new InlineSessionDispatcher());
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            if (state == "detached") document.Detach();
            var service = new CadCommandLineService();
            var tools = new CadAgentToolset(service, state == "none" ? null : document);
            var before = session.Snapshot;
            var history = session.HistoryUsage;
            using var status = JsonDocument.Parse(await tools.ExecuteAsync(new("status", "cad_status", "{}"), default));
            var value = status.RootElement;
            Assert.Equal(state == "active", value.GetProperty("active").GetBoolean());
            var catalog = value.GetProperty("commands").EnumerateArray().Select(r => r.GetString()).ToArray();
            var expected = service.Commands.Where(c => c.Name is not ("HISTORY" or "WORKSPACE")).Select(c => c.Syntax).ToArray();
            Assert.Equal(expected, catalog);
            var routes = value.GetProperty("commandTools");
            Assert.Equal("cad_history", routes.GetProperty("HISTORY").GetString());
            Assert.Equal("cad_workspace", routes.GetProperty("WORKSPACE").GetString());
            Assert.Equal("cad_workspace", routes.GetProperty("WS").GetString());
            if (state == "active")
            {
                Assert.Equal(before.Id.ToString(), value.GetProperty("documentId").GetString());
                Assert.Equal(before.StateId.ToString(), value.GetProperty("stateId").GetString());
            }
            Assert.Same(before, session.Snapshot);
            Assert.Equal(history, session.HistoryUsage);
            Assert.False(session.CanUndo);
            Assert.Equal(0, assets.Count);
        }
        finally { document.Detach(); }
    }

    [Theory]
    [InlineData("HISTORY END", "cad_history")]
    [InlineData("history END", "cad_history")]
    [InlineData("\"HISTORY\" END", "cad_history")]
    [InlineData("WORKSPACE LIST", "cad_workspace")]
    [InlineData("workspace LIST", "cad_workspace")]
    [InlineData("WS LIST", "cad_workspace")]
    [InlineData("ws LIST", "cad_workspace")]
    [InlineData("\"WS\" LIST", "cad_workspace")]
    public async Task DedicatedCommandsRemainRejectedBeforeDelegation(string command, string tool)
    {
        var commands = new CountingCommands();
        var tools = new CadAgentToolset(commands, null);
        using var result = JsonDocument.Parse(await tools.ExecuteAsync(new("guard", "cad_command", JsonSerializer.Serialize(new { command })), default));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(tool, result.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, commands.Executions);
    }

    [Fact]
    public async Task CatalogFilteringUsesExactCaseInsensitiveNamesAndDoesNotRemoveOtherCommands()
    {
        var commands = new CountingCommands();
        var tools = new CadAgentToolset(commands, null);
        using var status = JsonDocument.Parse(await tools.ExecuteAsync(new("status", "cad_status", "{}"), default));
        Assert.Equal(new[] { "HISTORY_HELP", "WSINFO" }, status.RootElement.GetProperty("commands").EnumerateArray().Select(v => v.GetString()));
        using var allowed = JsonDocument.Parse(await tools.ExecuteAsync(new("allowed", "cad_command", "{\"command\":\"WSINFO\"}"), default));
        Assert.True(allowed.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, commands.Executions);
        var original = new CadCommandLineService();
        Assert.Contains(original.Commands, c => c.Name == "HISTORY");
        Assert.Contains(original.Commands, c => c.Name == "WORKSPACE");
        Assert.Contains("HISTORY SUPPRESS", (await original.ExecuteAsync("HELP HISTORY", null)).Message);
        Assert.Contains("WORKSPACE LIST", (await original.ExecuteAsync("HELP WORKSPACE", null)).Message);
    }

    private sealed class CountingCommands : ICadCommandLineService
    {
        public IReadOnlyList<CadCommandDescriptor> Commands { get; } =
        [new("hIsToRy", "", "history END", ""), new("workspace", "WS", "workspace LIST", ""),
         new("wS", "", "ws LIST", ""), new("HISTORY_HELP", "", "HISTORY_HELP", ""), new("WSINFO", "", "WSINFO", "")];
        public int Executions { get; private set; }
        public IReadOnlyList<string> Complete(string prefix, int limit = 12) => [];
        public Task<CadCommandResult> ExecuteAsync(string input, ICadCommandContext? context, CancellationToken cancellationToken = default)
        { Executions++; return Task.FromResult(new CadCommandResult(true, input)); }
    }

    private sealed class NoGeometryKernel : IGeometryKernel
    {
        public string Version => "No geometry in command catalog tests";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected geometry evaluation.");
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
