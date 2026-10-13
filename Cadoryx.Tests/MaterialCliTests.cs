using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Cli;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class MaterialCliTests
{
    private static readonly byte[] Pixels = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 255, 255, 255, 0];

    [Fact]
    public async Task ContractAndInspectDiscoverCurrentIdentitiesWithoutCreatingOutput()
    {
        using var files = new TestFiles();
        var contract = await Run(["material-contract"]);
        Assert.Equal(0, contract.Code); Assert.Empty(contract.Error);
        using (var json = JsonDocument.Parse(contract.Output))
        {
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("texture_pixels", json.RootElement.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
        }
        var request = files.PathFor("request.json");
        await File.WriteAllTextAsync(request, "{\"action\":\"contract\"}");
        Assert.Equal(0, (await Run(["material", files.PathFor("missing.cadoryx"), "--request", request])).Code);
        var input = files.PathFor("empty.cadoryx"); var snapshot = DocumentSnapshot.Create("Read discovery");
        await new CadDocumentStorage().SaveAsync(snapshot, new MemoryAssetStore(), input);
        var before = await File.ReadAllBytesAsync(input);
        var result = await Request(input, request, new() { ["action"] = "inspect" });
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        using var inspected = JsonDocument.Parse(result.Output);
        Assert.Equal(snapshot.Id.ToString(), inspected.RootElement.GetProperty("documentId").GetString());
        Assert.Equal(snapshot.StateId.ToString(), inspected.RootElement.GetProperty("stateId").GetString());
        Assert.Equal(0, inspected.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(before, await File.ReadAllBytesAsync(input)); AssertNoScratch(input);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileWorkflowKeepsGeometryAndExactStateWhileAuthoringAssigningClearingAndDeleting(bool memory)
    {
        using var files = new TestFiles(); var assets = new MemoryAssetStore(); var storage = new CadDocumentStorage();
        var input = files.PathFor("input.cadoryx"); var request = files.PathFor("request.json");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Storage", "v2-box.cadoryx");
        DocumentSnapshot original; byte[] geometryBytes;
        using (var loaded = await storage.LoadAsync(fixture, assets))
        {
            original = loaded.Snapshot;
            using var geometry = assets.Acquire(Assert.Single(original.Bodies.Values).Geometry.AssetId);
            geometryBytes = geometry.Content.ToArray(); await storage.SaveAsync(original, assets, input);
        }
        Assert.Equal(0, assets.Count);
        var sourceBytes = await File.ReadAllBytesAsync(input); var body = Assert.Single(original.Bodies.Values);
        var identity = DocumentCommandExpectation.Capture(original); var step = 0;
        var discovery = await Request(input, request, new() { ["action"] = "inspect", ["collection"] = "bodies", ["limit"] = 1 }, memory: memory);
        Assert.Equal(0, discovery.Code);
        using (var inspected = JsonDocument.Parse(discovery.Output))
        {
            var found = Assert.Single(inspected.RootElement.GetProperty("bodies").EnumerateArray());
            Assert.Equal(body.Id.ToString(), found.GetProperty("id").GetString());
            Assert.Equal(body.Geometry.Revision.ToString(), found.GetProperty("revision").GetString());
            Assert.Equal(identity.StateId.ToString(), inspected.RootElement.GetProperty("stateId").GetString());
        }

        async Task<JsonObject> Apply(JsonObject payload)
        {
            payload["documentId"] = identity.DocumentId.ToString(); payload["stateId"] = identity.StateId.ToString();
            var output = files.PathFor($"step-{++step}.cadoryx");
            var result = await Request(input, request, payload, output, memory);
            Assert.Equal(0, result.Code); Assert.Empty(result.Error);
            var receipt = JsonNode.Parse(result.Output)!.AsObject();
            Assert.True(receipt["success"]!.GetValue<bool>()); Assert.True(receipt["changed"]!.GetValue<bool>());
            Assert.Equal(output, receipt["output"]!.GetValue<string>());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output))), receipt["outputSha256"]!.GetValue<string>());
            Assert.Equal(original.Id.ToString(), receipt["documentId"]!.GetValue<string>());
            var next = new DocumentCommandExpectation(original.Id, new(Guid.Parse(receipt["stateId"]!.GetValue<string>())));
            Assert.NotEqual(identity.StateId, next.StateId); identity = next;
            using (var loaded = await storage.LoadAsync(output, assets))
            {
                Assert.Equal(identity.StateId, loaded.Snapshot.StateId);
                Assert.Equal(body.Geometry, loaded.Snapshot.Bodies[body.Id].Geometry);
                Assert.Equal(original.Features.Keys.OrderBy(x => x.Value), loaded.Snapshot.Features.Keys.OrderBy(x => x.Value));
                foreach (var (key, feature) in original.Features)
                {
                    var actual = loaded.Snapshot.Features[key];
                    Assert.Equal(feature.Result, actual.Result); Assert.Equal(feature.OutputBodyId, actual.OutputBodyId);
                    Assert.Equal(feature.Inputs, actual.Inputs);
                    Assert.Equal(JsonSerializer.Serialize(feature.Recipe, CadJson.Options), JsonSerializer.Serialize(actual.Recipe, CadJson.Options));
                    if (actual.OutputMetadata is { } metadata)
                        Assert.Equal(loaded.Snapshot.Bodies[actual.OutputBodyId].MaterialId, metadata.Material);
                }
                using var originalLease = assets.Acquire(body.Geometry.AssetId);
                Assert.Equal(geometryBytes, originalLease.Content.ToArray());
            }
            Assert.Equal(0, assets.Count); AssertNoScratch(output); input = output; return receipt;
        }

        var created = await Apply(new() { ["action"] = "set", ["name"] = "Copper", ["densityKgPerM3"] = 8960,
            ["appearance"] = new JsonObject { ["shading"] = "Pbr", ["front"] = new JsonObject { ["colorArgb"] = 0xFF804020u, ["roughness"] = .3, ["metallic"] = .8 } } });
        var id = created["id"]!.GetValue<string>(); var materialId = new MaterialId(Guid.Parse(id));
        Assert.Equal(id, created["createdIds"]![0]!.GetValue<string>());
        await Apply(new() { ["action"] = "texture_pixels", ["id"] = id, ["width"] = 2, ["height"] = 2, ["rgbaBase64"] = Convert.ToBase64String(Pixels) });
        CadMaterial textured;
        using (var loaded = await storage.LoadAsync(input, assets))
        {
            textured = loaded.Snapshot.Materials[materialId];
            using var pixelLease = assets.Acquire(textured.Appearance!.Texture!.AssetId);
            Assert.Equal(Pixels, pixelLease.Content.Span[CadTexturePayload.HeaderBytes..].ToArray());
            Assert.Equal(.8, textured.Appearance.Front.Metallic);
        }
        Assert.Equal(0, assets.Count);
        await Apply(new() { ["action"] = "set", ["id"] = id, ["name"] = "Copper edited", ["densityKgPerM3"] = 8900 });
        using (var loaded = await storage.LoadAsync(input, assets))
        { Assert.Equal(textured.Appearance, loaded.Snapshot.Materials[materialId].Appearance); Assert.Equal(8.9e-6, loaded.Snapshot.Materials[materialId].DensityKgPerMm3, 12); }
        var staleOutput = files.PathFor("stale-revision.cadoryx"); var unassigned = await File.ReadAllBytesAsync(input);
        var stale = await Request(input, request, new() { ["action"] = "assign", ["bodyId"] = body.Id.ToString(),
            ["revision"] = Guid.NewGuid().ToString(), ["materialId"] = id,
            ["documentId"] = identity.DocumentId.ToString(), ["stateId"] = identity.StateId.ToString() }, staleOutput, memory);
        Assert.Equal(1, stale.Code); Assert.False(File.Exists(staleOutput)); Assert.Equal(unassigned, await File.ReadAllBytesAsync(input));
        await Apply(new() { ["action"] = "assign", ["bodyId"] = body.Id.ToString(), ["revision"] = body.Geometry.Revision.ToString(), ["materialId"] = id });
        using (var loaded = await storage.LoadAsync(input, assets)) Assert.Equal(materialId, loaded.Snapshot.Bodies[body.Id].MaterialId);
        var assignedBytes = await File.ReadAllBytesAsync(input);
        var denied = await Request(input, request, new() { ["action"] = "delete", ["id"] = id,
            ["documentId"] = identity.DocumentId.ToString(), ["stateId"] = identity.StateId.ToString() }, files.PathFor("referenced-delete.cadoryx"), memory);
        Assert.Equal(1, denied.Code); Assert.False(File.Exists(files.PathFor("referenced-delete.cadoryx")));
        Assert.Equal(assignedBytes, await File.ReadAllBytesAsync(input));
        await Apply(new() { ["action"] = "assign", ["bodyId"] = body.Id.ToString(), ["revision"] = body.Geometry.Revision.ToString(), ["materialId"] = null });
        await Apply(new() { ["action"] = "set", ["id"] = id, ["name"] = "Cleared", ["densityKgPerM3"] = 8900, ["appearance"] = null });
        using (var loaded = await storage.LoadAsync(input, assets))
        {
            Assert.Null(loaded.Snapshot.Materials[materialId].Appearance);
            Assert.DoesNotContain(textured.Appearance!.Texture!.AssetId, loaded.Snapshot.ReferencedAssets());
        }
        await Apply(new() { ["action"] = "delete", ["id"] = id });
        using (var loaded = await storage.LoadAsync(input, assets)) { Assert.Empty(loaded.Snapshot.Materials); Assert.Null(loaded.Snapshot.Bodies[body.Id].MaterialId); }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(files.PathFor("input.cadoryx"))); Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData("missing-document")]
    [InlineData("missing-state")]
    [InlineData("foreign-document")]
    [InlineData("stale-state")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-action")]
    [InlineData("duplicate-nested")]
    [InlineData("numeric-enum")]
    [InlineData("bad-color")]
    [InlineData("negative-scale")]
    [InlineData("unknown-nested")]
    [InlineData("density-underflow")]
    [InlineData("foreign-texture")]
    [InlineData("unknown-material")]
    [InlineData("incomplete-inspect-identity")]
    [InlineData("unknown-collection")]
    public async Task RejectedRequestsPreserveSourceExistingOutputAndProvideReadFeedback(string failure)
    {
        using var files = new TestFiles(); var snapshot = DocumentSnapshot.Create("Rejected CLI");
        var input = files.PathFor("input.cadoryx"); var output = files.PathFor("output.cadoryx"); var request = files.PathFor("request.json");
        await new CadDocumentStorage().SaveAsync(snapshot, new MemoryAssetStore(), input);
        var original = await File.ReadAllBytesAsync(input); byte[] marker = [12, 34, 56]; await File.WriteAllBytesAsync(output, marker);
        var payload = SetRequest(snapshot);
        switch (failure)
        {
            case "missing-document": payload.Remove("documentId"); break;
            case "missing-state": payload.Remove("stateId"); break;
            case "foreign-document": payload["documentId"] = Guid.NewGuid().ToString(); break;
            case "stale-state": payload["stateId"] = Guid.NewGuid().ToString(); break;
            case "unknown-field": payload["autoBind"] = true; break;
            case "numeric-enum": payload["appearance"] = new JsonObject { ["shading"] = 1 }; break;
            case "bad-color": payload["appearance"] = new JsonObject { ["front"] = new JsonObject { ["colorArgb"] = 0x00123456u } }; break;
            case "negative-scale": payload["appearance"] = new JsonObject { ["mapping"] = new JsonObject { ["scaleS"] = -1 } }; break;
            case "unknown-nested": payload["appearance"] = new JsonObject { ["front"] = new JsonObject { ["unknown"] = 1 } }; break;
            case "density-underflow": payload["densityKgPerM3"] = double.Epsilon; break;
            case "foreign-texture": payload["appearance"] = JsonSerializer.SerializeToNode(new CadMaterialAppearance
                { Texture = new(new(new string('A', 64)), 2, 2, CadTexturePayload.CurrentFormat) }, CadJson.Options); break;
            case "unknown-material": payload["id"] = Guid.NewGuid().ToString(); break;
            case "incomplete-inspect-identity": payload = new() { ["action"] = "inspect", ["documentId"] = snapshot.Id.ToString() }; break;
            case "unknown-collection": payload = new() { ["action"] = "inspect", ["collection"] = "guessed-bodies" }; break;
        }
        var text = payload.ToJsonString();
        if (failure == "duplicate-action") text = text.Insert(1, "\"action\":\"set\",");
        if (failure == "duplicate-nested") text = text[..^1] + ",\"appearance\":{\"front\":{\"metallic\":0,\"metallic\":1}}}";
        await File.WriteAllTextAsync(request, text);
        var result = failure is "incomplete-inspect-identity" or "unknown-collection"
            ? await Run(["material", input, "--request", request])
            : await Run(["material", input, "--request", request, "--output", output, "--overwrite"]);
        Assert.Equal(failure.StartsWith("duplicate-", StringComparison.Ordinal) ? 2 : 1, result.Code); Assert.NotEmpty(result.Error);
        if (result.Code == 1)
        {
            using var json = JsonDocument.Parse(result.Output); Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("inspect", json.RootElement.GetProperty("nextRead").GetProperty("request").GetProperty("action").GetString());
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(input)); Assert.Equal(marker, await File.ReadAllBytesAsync(output)); AssertNoScratch(output);
    }

    [Theory]
    [InlineData("input-format")]
    [InlineData("request-format")]
    [InlineData("output-format")]
    [InlineData("same-output")]
    [InlineData("same-request")]
    [InlineData("duplicate-flag")]
    [InlineData("unknown-flag")]
    [InlineData("missing-request")]
    [InlineData("missing-value")]
    [InlineData("extra-contract-flag")]
    public async Task InvalidOptionsFailBeforeTouchingDocuments(string failure)
    {
        using var files = new TestFiles(); var input = files.PathFor("input.cadoryx"); var request = files.PathFor("request.json");
        var output = files.PathFor("output.cadoryx"); byte[] marker = [51, 52]; await File.WriteAllBytesAsync(input, marker);
        string[] args = failure switch
        {
            "input-format" => ["material", files.PathFor("input.step"), "--request", request],
            "request-format" => ["material", input, "--request", files.PathFor("request.txt")],
            "output-format" => ["material", input, "--request", request, "--output", files.PathFor("output.step")],
            "same-output" => ["material", input, "--request", request, "--output", input, "--overwrite"],
            "same-request" => ["material", input, "--request", input],
            "duplicate-flag" => ["material", input, "--request", request, "--memory", "--memory"],
            "unknown-flag" => ["material", input, "--request", request, "--auto-bind"],
            "missing-request" => ["material", input, "--output", output],
            "missing-value" => ["material", input, "--request", "--memory"],
            "extra-contract-flag" => ["material-contract", "--memory"],
            _ => throw new ArgumentException("Unknown option case.")
        };
        var result = await Run(args); Assert.Equal(2, result.Code); Assert.NotEmpty(result.Error);
        Assert.Equal(marker, await File.ReadAllBytesAsync(input)); Assert.False(File.Exists(output)); AssertNoScratch(input);
    }

    [Theory]
    [InlineData("oversize-bytes")]
    [InlineData("oversize-chars")]
    [InlineData("deep")]
    [InlineData("invalid-utf8")]
    [InlineData("utf16")]
    [InlineData("array")]
    public async Task RequestBudgetsAndUtf8AreEnforcedBeforeAnyDocumentLoad(string failure)
    {
        using var files = new TestFiles(); var input = files.PathFor("missing.cadoryx"); var request = files.PathFor("request.json");
        byte[] bytes = failure switch
        {
            "oversize-bytes" => new byte[65537],
            "oversize-chars" => Encoding.UTF8.GetBytes("{\"action\":\"contract\"}" + new string(' ', 16385)),
            "deep" => Encoding.UTF8.GetBytes("{\"action\":\"set\",\"appearance\":" + new string('[', 40) + "0" + new string(']', 40) + "}"),
            "invalid-utf8" => [0xFF, 0xFE, 0xFE],
            "utf16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("{\"action\":\"contract\"}")],
            "array" => Encoding.UTF8.GetBytes("[]"),
            _ => throw new ArgumentException("Unknown budget case.")
        };
        await File.WriteAllBytesAsync(request, bytes);
        var result = await Run(["material", input, "--request", request]);
        Assert.Equal(2, result.Code); Assert.NotEmpty(result.Error); Assert.False(File.Exists(input));
    }

    [Fact]
    public async Task Utf8BomIsAcceptedForContractRequests()
    {
        using var files = new TestFiles(); var request = files.PathFor("bom.json");
        await File.WriteAllBytesAsync(request, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"action\":\"contract\"}")]);
        Assert.Equal(0, (await Run(["material", files.PathFor("missing.cadoryx"), "--request", request])).Code);
    }

    [Theory]
    [InlineData("existing-output")]
    [InlineData("missing-output")]
    [InlineData("inspect-output")]
    [InlineData("contract-output")]
    [InlineData("blocked-directory")]
    public async Task OutputFailuresLeaveSourceAndExistingFilesIntactAndRemoveTemporaryFiles(string failure)
    {
        using var files = new TestFiles(); var snapshot = DocumentSnapshot.Create("Output guards");
        var input = files.PathFor("input.cadoryx"); var output = files.PathFor("output.cadoryx"); var request = files.PathFor("request.json");
        await new CadDocumentStorage().SaveAsync(snapshot, new MemoryAssetStore(), input);
        byte[] marker = [22, 33]; await File.WriteAllBytesAsync(output, marker); var before = await File.ReadAllBytesAsync(input);
        var payload = failure switch { "inspect-output" => new JsonObject { ["action"] = "inspect" }, "contract-output" => new JsonObject { ["action"] = "contract" }, _ => SetRequest(snapshot) };
        if (failure == "blocked-directory") output = Path.Combine(output, "blocked.cadoryx");
        var result = await Request(input, request, payload, failure == "missing-output" ? null : output, overwrite: failure != "existing-output");
        Assert.Equal(1, result.Code); Assert.NotEmpty(result.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(input)); Assert.Equal(marker, await File.ReadAllBytesAsync(files.PathFor("output.cadoryx"))); AssertNoScratch(input);
    }

    [Fact]
    public async Task PreCancelledCliCannotOverwriteOutputOrReadMissingRequest()
    {
        using var files = new TestFiles(); var output = files.PathFor("output.cadoryx"); byte[] marker = [99, 98];
        await File.WriteAllBytesAsync(output, marker); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await Run(["material", files.PathFor("missing.cadoryx"), "--request", files.PathFor("missing.json"), "--output", output, "--overwrite"], cancel.Token);
        Assert.Equal(130, result.Code); Assert.Contains("Cancelled", result.Error); Assert.Equal(marker, await File.ReadAllBytesAsync(output)); AssertNoScratch(output);
        Assert.Equal(130, (await Run(["material-contract"], cancel.Token)).Code);
    }

    [Theory]
    [InlineData("foreign-request")]
    [InlineData("stale-request")]
    [InlineData("stale-expectation")]
    [InlineData("direct-budget")]
    [InlineData("direct-depth")]
    public async Task SharedServiceCannotBypassIdentitiesOrInputBudgetsAtItsPublicBoundary(string failure)
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Service boundary"), assets, kernel, new InlineSessionDispatcher()))
        {
            var before = session.Snapshot; var expected = DocumentCommandExpectation.Capture(before); var payload = SetRequest(before);
            if (failure == "foreign-request") payload["documentId"] = Guid.NewGuid().ToString();
            if (failure == "stale-request") payload["stateId"] = Guid.NewGuid().ToString();
            if (failure == "stale-expectation") expected = expected with { StateId = DocumentStateId.New() };
            var text = payload.ToJsonString();
            if (failure == "direct-budget") text = text[..^1] + ",\"name-padding\":\"" + new string('x', 16385) + "\"}";
            if (failure == "direct-depth") text = text[..^1] + ",\"nested\":" + new string('[', 40) + "0" + new string(']', 40) + "}";
            using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
            if (failure.StartsWith("direct-", StringComparison.Ordinal))
                await Assert.ThrowsAsync<ArgumentException>(() => CadMaterialAuthoringService.ApplyAsync(session, expected, parsed.RootElement, true, default));
            else await Assert.ThrowsAsync<StaleDocumentException>(() => CadMaterialAuthoringService.ApplyAsync(session, expected, parsed.RootElement, true, default));
            Assert.Same(before, session.Snapshot); Assert.False(session.CanUndo); Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
        }
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public async Task SharedServiceTextureOwnershipSurvivesHistoryCaptureAndCloseWithoutGeometryCalls()
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        var session = new CadDocumentSession(DocumentSnapshot.Create("Shared ownership"), assets, kernel, new InlineSessionDispatcher());
        try
        {
            using var set = CadMaterialAuthoringService.ParseRequest("{\"action\":\"set\",\"name\":\"Shared\",\"densityKgPerM3\":1000,\"appearance\":{}}");
            await CadMaterialAuthoringService.ApplyAsync(session, DocumentCommandExpectation.Capture(session.Snapshot), set.RootElement, false, default);
            var material = Assert.Single(session.Snapshot.Materials.Values); var before = session.Snapshot;
            using var texture = CadMaterialAuthoringService.ParseRequest(JsonSerializer.Serialize(new { action = "texture_pixels", id = material.Id.ToString(), width = 2, height = 2, rgbaBase64 = Convert.ToBase64String(Pixels) }));
            await CadMaterialAuthoringService.ApplyAsync(session, DocumentCommandExpectation.Capture(before), texture.RootElement, false, default);
            var after = session.Snapshot; var id = after.Materials[material.Id].Appearance!.Texture!.AssetId;
            await session.UndoAsync(); Assert.Same(before, session.Snapshot); await session.RedoAsync(); Assert.Same(after, session.Snapshot);
            using (var capture = session.Capture())
            {
                await session.DisposeAsync(); Assert.Equal(1, assets.Count);
                using var lease = assets.Acquire(id); Assert.Equal(Pixels, lease.Content.Span[CadTexturePayload.HeaderBytes..].ToArray());
                Assert.Equal(id, capture.Snapshot.Materials[material.Id].Appearance!.Texture!.AssetId);
            }
            Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
        }
        finally { await session.DisposeAsync(); }
    }

    private static JsonObject SetRequest(DocumentSnapshot snapshot) => new()
    { ["action"] = "set", ["documentId"] = snapshot.Id.ToString(), ["stateId"] = snapshot.StateId.ToString(), ["name"] = "Paint", ["densityKgPerM3"] = 1000 };

    private static async Task<(int Code, string Output, string Error)> Request(string input, string request, JsonObject payload,
        string? output = null, bool memory = false, bool overwrite = false)
    {
        await File.WriteAllTextAsync(request, payload.ToJsonString());
        var args = new List<string> { "material", input, "--request", request };
        if (output is not null) args.AddRange(["--output", output]);
        if (memory) args.Add("--memory"); if (overwrite) args.Add("--overwrite");
        return await Run(args.ToArray());
    }

    private static async Task<(int Code, string Output, string Error)> Run(string[] args, CancellationToken token = default)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await CadCommandLine.RunAsync(args, output, error, token); return (code, output.ToString(), error.ToString());
    }

    private static void AssertNoScratch(string path) => Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".cadoryx-*"));

    private sealed class NoGeometryKernel : IGeometryKernel
    {
        public int Calls { get; private set; }
        public string Version => "Material operations must not evaluate geometry";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Material edits cannot rebuild geometry."); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
