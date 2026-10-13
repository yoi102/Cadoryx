using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class MaterialAuthoringTests
{
    private static readonly byte[] Pixels = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 255, 255, 255, 0];
    private static CadMaterial Material() => new(MaterialId.New(), "Paint", 1e-6,
        new() { Front = new(0xFF123456, .7, .6, .25, 1.7, .2), Back = new(0xFF654321), AlphaMode = CadMaterialAlphaMode.Blend,
            Mapping = new() { Planar = true, ScaleS = 2, ScaleT = 3, TranslationS = .2, TranslationT = .4, RotationDegrees = 37, Repeat = true } });

    [Fact]
    public async Task OneTextureEditOwnsCopiedPixelsThroughUndoRedoCaptureAndClose()
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        var session = new CadDocumentSession(DocumentSnapshot.Create("Texture history"), assets, kernel, new InlineSessionDispatcher());
        var before = session.Snapshot; var rgba = (byte[])Pixels.Clone(); var material = Material();
        var command = new SetMaterialTextureCommand(material, 2, 2, rgba, add: true); Array.Fill(rgba, (byte)17);
        await session.ExecuteAsync(command);
        var after = session.Snapshot; var texture = after.Materials[material.Id].Appearance!.Texture!;
        Assert.Equal(1, assets.Count); Assert.Single(after.ReferencedAssets());
        using (var lease = assets.Acquire(texture.AssetId)) Assert.Equal(Pixels, lease.Content.Span[CadTexturePayload.HeaderBytes..].ToArray());
        Assert.True(session.CanUndo); await session.UndoAsync(); Assert.Same(before, session.Snapshot);
        Assert.False(session.CanUndo); Assert.Equal(1, assets.Count); await session.RedoAsync(); Assert.Same(after, session.Snapshot);
        using (var capture = session.Capture())
        {
            await session.DisposeAsync(); Assert.Equal(1, assets.Count);
            Assert.Equal(texture, capture.Snapshot.Materials[material.Id].Appearance!.Texture);
        }
        Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("density")]
    [InlineData("canceled")]
    public async Task RejectedTextureEditRetainsStateAndReleasesStagedBytes(string failure)
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Rejected"), assets, kernel, new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(ResourceCommands.AddMaterial(new(MaterialId.New(), "Paint", 1e-6)));
            var before = session.Snapshot; var value = Material();
            if (failure == "density") value = value with { Name = "Other", DensityKgPerMm3 = double.NaN };
            var command = new SetMaterialTextureCommand(value, 2, 2, Pixels, add: true);
            using var cancel = new CancellationTokenSource(); if (failure == "canceled") cancel.Cancel();
            if (failure == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(command, cancel.Token));
            else await Assert.ThrowsAsync<CadValidationException>(() => session.ExecuteAsync(command));
            Assert.Same(before, session.Snapshot); Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
        }
        Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("reserved")]
    [InlineData("zero-width")]
    [InlineData("too-wide")]
    [InlineData("pixel-budget")]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("descriptor")]
    public async Task ExistingTextureReferenceCannotPublishInvalidBytesOrDimensions(string mutation)
    {
        var assets = new MemoryAssetStore(); var bytes = CadTexturePayload.Encode(2, 2, Pixels);
        if (mutation == "magic") bytes[0] = 0;
        if (mutation == "version") bytes[8] = 2;
        if (mutation == "reserved") bytes[20] = 1;
        if (mutation == "zero-width") Array.Clear(bytes, 12, 4);
        if (mutation == "too-wide") System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 16385);
        if (mutation == "pixel-budget")
        { System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 16384); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16384); }
        if (mutation == "truncated") bytes = bytes[..^1];
        if (mutation == "extra") bytes = [.. bytes, 0];
        using (var staged = assets.Stage(bytes))
        {
            await using var session = new CadDocumentSession(DocumentSnapshot.Create("Invalid texture"), assets, new NoGeometryKernel(), new InlineSessionDispatcher());
            var material = Material(); var texture = new CadTextureRef(staged.Id, mutation == "descriptor" ? 1 : 2, 2, CadTexturePayload.CurrentFormat);
            var value = material with { Appearance = material.Appearance! with { Texture = texture } }; var before = session.Snapshot;
            await Assert.ThrowsAsync<CadValidationException>(() => session.ExecuteAsync(ResourceCommands.AddMaterial(value)));
            Assert.Same(before, session.Snapshot); Assert.False(session.CanUndo); Assert.Equal(1, assets.Count);
        }
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public async Task DensityOnlyMaterialRetainsOriginalWriterAndPresentationAndNoAppearancePartition()
    {
        using var files = new TestFiles(); var assets = new MemoryAssetStore(); var material = new CadMaterial(MaterialId.New(), "Legacy steel", 7.85e-6);
        var snapshot = DocumentSnapshot.Create("Legacy") with { Materials = ImmutableDictionary<MaterialId, CadMaterial>.Empty.Add(material.Id, material) };
        var path = files.PathFor("legacy.cadoryx"); var storage = new CadDocumentStorage(); await storage.SaveAsync(snapshot, assets, path);
        var manifest = FormatEvolutionTests.Manifest(path); Assert.Equal("0.7.8", manifest.ApplicationVersion);
        Assert.Equal(2, manifest.Sections.Single(s => s.Kind == "presentation").SchemaVersion);
        Assert.DoesNotContain(manifest.Sections, s => s.Kind == MaterialAppearanceSectionStorage.Kind);
        Assert.DoesNotContain(MaterialAppearanceSectionStorage.Capability, manifest.RequiredCapabilities);
        using (var loaded = await storage.LoadAsync(path, assets)) Assert.Equal(material, loaded.Snapshot.Materials[material.Id]);
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public async Task CompleteMaterialRoundTripOwnsIndependentPartitionCatalogAndTexturePixels()
    {
        using var files = new TestFiles(); var assets = new MemoryAssetStore(); var material = Material(); var path = files.PathFor("appearance.cadoryx");
        var storage = new CadDocumentStorage(); CadMaterial saved;
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Material save"), assets, new NoGeometryKernel(), new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new SetMaterialTextureCommand(material, 2, 2, Pixels, add: true)); saved = session.Snapshot.Materials[material.Id];
            await storage.SaveAsync(session.Snapshot, assets, path);
        }
        Assert.Equal(0, assets.Count); var manifest = FormatEvolutionTests.Manifest(path);
        Assert.Equal("0.7.10", manifest.ApplicationVersion); Assert.Contains(MaterialAppearanceSectionStorage.Capability, manifest.RequiredCapabilities);
        Assert.True(manifest.Sections.Single(s => s.Kind == MaterialAppearanceSectionStorage.Kind).Required);
        Assert.Equal(CadTexturePayload.CurrentFormat, Assert.Single(manifest.Assets).Format);
        using (var loaded = await storage.LoadAsync(path, assets))
        {
            Assert.Equal(saved, loaded.Snapshot.Materials[material.Id]);
            var texture = saved.Appearance!.Texture!; Assert.Single(loaded.Snapshot.ReferencedAssets());
            using var lease = assets.Acquire(texture.AssetId); Assert.Equal(Pixels, lease.Content.Span[CadTexturePayload.HeaderBytes..].ToArray());
        }
        Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData("missing-capability")]
    [InlineData("optional-section")]
    [InlineData("future-section")]
    [InlineData("duplicate-material")]
    [InlineData("foreign-material")]
    [InlineData("trailing-section")]
    [InlineData("wrong-texture-dimensions")]
    [InlineData("wrong-texture-format")]
    public async Task MalformedMaterialFileRejectsBeforePublishingAndReleasesAllAssets(string mutation)
    {
        using var files = new TestFiles(); var assets = new MemoryAssetStore(); var material = Material(); var path = files.PathFor("bad.cadoryx");
        var storage = new CadDocumentStorage();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Corruption"), assets, new NoGeometryKernel(), new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new SetMaterialTextureCommand(material, 2, 2, Pixels, add: true));
            await storage.SaveAsync(session.Snapshot, assets, path);
        }
        Assert.Equal(0, assets.Count);
        if (mutation == "missing-capability") FormatEvolutionTests.RewriteManifest(path, m => m with { RequiredCapabilities = m.RequiredCapabilities.Remove(MaterialAppearanceSectionStorage.Capability) });
        else if (mutation is "optional-section" or "future-section") FormatEvolutionTests.RewriteManifest(path, m => m with
        { Sections = m.Sections.Select(s => s.Kind == MaterialAppearanceSectionStorage.Kind ? mutation == "optional-section" ? s with { Required = false } : s with { SchemaVersion = 2 } : s).ToImmutableArray() });
        else FormatEvolutionTests.RewriteSection(path, MaterialAppearanceSectionStorage.Kind, bytes =>
        {
            if (mutation == "trailing-section") return [.. bytes, 0];
            var pack = MessagePackSerializer.Deserialize<PackMaterialAppearances>(bytes); var entry = pack.Entries[0];
            if (mutation == "duplicate-material") return MessagePackSerializer.Serialize(pack with { Entries = [entry, entry] });
            if (mutation == "foreign-material") entry = entry with { Material = Guid.NewGuid() };
            else
            {
                var appearance = JsonSerializer.Deserialize<CadMaterialAppearance>(entry.AppearanceJson, CadJson.Options)!;
                var texture = appearance.Texture!;
                texture = mutation == "wrong-texture-dimensions" ? texture with { Width = 1 } : texture with { Format = texture.Format with { Encoding = "future" } };
                entry = entry with { AppearanceJson = JsonSerializer.SerializeToUtf8Bytes(appearance with { Texture = texture }, CadJson.Options) };
            }
            return MessagePackSerializer.Serialize(pack with { Entries = [entry] });
        });
        if(mutation=="future-section")await Assert.ThrowsAsync<NotSupportedException>(() => storage.LoadAsync(path, assets));
        else await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path, assets));
        Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SourceStylesAndOccurrenceOverrideHaveExplicitPriorityWithoutChangingGeometry(bool source, bool overrideSource, bool occurrenceColor)
    {
        var assets = new MemoryAssetStore(); var material = Material() with { Appearance = Material().Appearance! with { OverrideSourceStyles = overrideSource } };
        using (var bytes = assets.Stage("Scene test boundary"u8))
        {
            var doc = DocumentSnapshot.Create("Scene priority"); var part = DefinitionId.New(); var id = BodyId.New(); var slot = ComponentSlotId.New(); var layer = doc.Layers.Keys.Single();
            var geometry = new GeometryAssetRef(bytes.Id, GeometryRevisionId.New(), BodyKind.Solid, new(new(0, 0, 0), new(1, 1, 1)), 1,
                source ? new XdeSourceRef(bytes.Id, "0:1:1") : null);
            var body = new CadBody(id, part, "Body", geometry, null, layer, new(PreserveSourceStyles: source), MaterialId: material.Id);
            doc = doc with
            {
                Materials = doc.Materials.Add(material.Id, material), Bodies = doc.Bodies.Add(id, body),
                Definitions = doc.Definitions.Add(part, new PartDefinition(part, "Part", [id], [])).SetItem(doc.RootAssemblyId,
                    new AssemblyDefinition(doc.RootAssemblyId, "Root", [new(slot, part, "Part", RigidTransform3d.Identity,
                        AppearanceOverride: occurrenceColor ? new(0xFFFF0000) : null)]))
            };
            doc.Validate(); var item = Assert.Single(CadScene.FromDocument(doc).Items);
            Assert.Equal(geometry, item.Geometry);
            if (occurrenceColor) { Assert.Null(item.MaterialAppearance); Assert.False(item.PreserveSourceStyles); Assert.Equal(0xFFFF0000u, item.Argb); }
            else if (source && !overrideSource) { Assert.Null(item.MaterialAppearance); Assert.True(item.PreserveSourceStyles); }
            else { Assert.Equal(material.Appearance, item.MaterialAppearance); Assert.Equal(source, item.PreserveSourceStyles); }
        }
        Assert.Equal(0, assets.Count); await Task.CompletedTask;
    }

    [Fact]
    public async Task ActualCommandAndAgentMaterialEditsShareIdentityHistoryAndTextureOwnership()
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Material tools"), assets, kernel, new InlineSessionDispatcher()))
        {
            var vm = new CadDocumentViewModel(session, kernel, new CadMessageLog()); var commands = new CadCommandLineService();
            var tools = new CadAgentToolset(commands, vm);
            try
            {
                var request = JsonSerializer.Serialize(new { action = "set", documentId = session.Snapshot.Id.ToString(), stateId = session.Snapshot.StateId.ToString(),
                    name = "Tool material", densityKgPerM3 = 7850, appearance = new { front = new { colorArgb = 0xFF804020u, roughness = .3, metallic = .8 }, shading = "Pbr" } });
                using var created = JsonDocument.Parse(await tools.ExecuteAsync(new("create", "cad_material", request), default));
                Assert.True(created.RootElement.GetProperty("success").GetBoolean());
                var id = created.RootElement.GetProperty("id").GetString()!;
                Assert.Equal(session.Snapshot.StateId.ToString(), created.RootElement.GetProperty("stateId").GetString());
                var before = session.Snapshot;
                var textureJson = JsonSerializer.Serialize(new { action = "texture_pixels", id, width = 2, height = 2, rgbaBase64 = Convert.ToBase64String(Pixels) });
                var result = await commands.ExecuteAsync("MATERIAL " + textureJson, new CadCommandContext(vm)); Assert.True(result.Success, result.Message);
                var after = session.Snapshot; var material = Assert.Single(after.Materials.Values);
                Assert.Equal(7.85e-6, material.DensityKgPerMm3, 12); Assert.Equal(.8, material.Appearance!.Front.Metallic);
                Assert.NotNull(material.Appearance.Texture); Assert.Equal(1, assets.Count);
                await session.UndoAsync(); Assert.Same(before, session.Snapshot); await session.RedoAsync(); Assert.Same(after, session.Snapshot);
                using var repeated = JsonDocument.Parse(await tools.ExecuteAsync(new("stale", "cad_material", request), default));
                Assert.False(repeated.RootElement.GetProperty("success").GetBoolean()); Assert.Same(after, session.Snapshot);
                Assert.Equal("cad_status", repeated.RootElement.GetProperty("nextRead").GetProperty("tool").GetString());
                Assert.Equal(0, kernel.Calls);
            }
            finally { await vm.StopToolsAsync(); vm.Detach(); }
        }
        Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("foreign-document")]
    [InlineData("stale-state")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-nested")]
    [InlineData("numeric-enum")]
    [InlineData("bad-color")]
    public async Task InvalidMaterialToolInputsReturnReadFeedbackAndCannotCommit(string mutation)
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Tool guards"), assets, kernel, new InlineSessionDispatcher()))
        {
            var vm = new CadDocumentViewModel(session, kernel, new CadMessageLog()); var tools = new CadAgentToolset(new CadCommandLineService(), vm);
            try
            {
                var before = session.Snapshot;
                var fields = new Dictionary<string, object> { ["action"] = "set", ["documentId"] = before.Id.ToString(), ["stateId"] = before.StateId.ToString(),
                    ["name"] = "Invalid", ["densityKgPerM3"] = 1000, ["appearance"] = new { front = new { colorArgb = 0xFFFFFFFFu }, shading = "Pbr" } };
                if (mutation == "missing-state") fields.Remove("stateId");
                if (mutation == "foreign-document") fields["documentId"] = DocumentId.New().ToString();
                if (mutation == "stale-state") fields["stateId"] = DocumentStateId.New().ToString();
                if (mutation == "unknown-field") fields["filePath"] = "Unrequested image.png";
                if (mutation == "numeric-enum") fields["appearance"] = new { shading = 1 };
                if (mutation == "bad-color") fields["appearance"] = new { front = new { colorArgb = 0x00808080u } };
                var request = JsonSerializer.Serialize(fields);
                if (mutation == "duplicate-nested") request = request.Replace("\"colorArgb\":4294967295", "\"colorArgb\":4294967295,\"colorArgb\":4294967295");
                using var response = JsonDocument.Parse(await tools.ExecuteAsync(new("invalid", "cad_material", request), default));
                Assert.False(response.RootElement.GetProperty("success").GetBoolean()); Assert.Same(before, session.Snapshot);
                Assert.Equal("cad_status", response.RootElement.GetProperty("nextRead").GetProperty("tool").GetString());
                Assert.False(session.CanUndo); Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
            }
            finally { await vm.StopToolsAsync(); vm.Detach(); }
        }
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public async Task NativeCreationPreviewKeepsItsSignalColorAndConfirmRestoresAuthoredMaterialWithoutRebuilding()
    {
        var assets = new MemoryAssetStore(); var kernel = new OcctGeometryKernel(); var material = Material();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Material preview"), assets, kernel, new InlineSessionDispatcher()))
        {
            var vm = new CadDocumentViewModel(session, kernel, new CadMessageLog());
            try
            {
                await session.ExecuteAsync(ResourceCommands.AddMaterial(material)); vm.SelectedCreationMaterial = material.Id;
                await vm.PreviewCommand.ExecuteAsync(null); Assert.True(vm.HasPreview);
                var preview = Assert.Single(vm.PreviewScene!.Items); Assert.Null(preview.MaterialAppearance);
                Assert.Equal(0xFF32CCA0u, preview.Argb); var preparedGeometry = preview.Geometry;
                await vm.ConfirmCommand.ExecuteAsync(null);
                var body = Assert.Single(session.Snapshot.Bodies.Values); Assert.Equal(preparedGeometry, body.Geometry);
                Assert.Equal(material.Id, body.MaterialId); Assert.Equal(material.Appearance, Assert.Single(vm.Scene.Items).MaterialAppearance);
            }
            finally { await vm.StopToolsAsync(); vm.Detach(); }
        }
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public async Task DeferredReaderVerifiesOwnedTextureBeforePublishingAndCanReleaseItsArchiveBacking()
    {
        using var files = new TestFiles(); var originalAssets = new MemoryAssetStore(); var material = Material(); var path = files.PathFor("deferred.cadoryx");
        var storage = new CadDocumentStorage(); CadMaterial saved;
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Deferred texture"), originalAssets, new NoGeometryKernel(), new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new SetMaterialTextureCommand(material, 2, 2, Pixels, add: true)); saved = session.Snapshot.Materials[material.Id];
            await storage.SaveAsync(session.Snapshot, originalAssets, path);
        }
        Assert.Equal(0, originalAssets.Count);
        using var disk = new DiskAssetStore(files.PathFor("owned-cache"));
        using (var loaded = await new CadDocumentStorage(deferGeometryAssets: true).LoadAsync(path, disk))
        {
            Assert.Equal(saved, loaded.Snapshot.Materials[material.Id]);
            Assert.Equal(0, disk.DeferredCount); Assert.True(disk.ReadCount > 0); Assert.Equal(1, disk.Count);
            // The copied archive can be released after the texture has been verified and materialized.
            File.Delete(path);
            using var lease = disk.Acquire(saved.Appearance!.Texture!.AssetId);
            Assert.Equal(Pixels, lease.Content.Span[CadTexturePayload.HeaderBytes..].ToArray());
        }
        Assert.Equal(0, disk.Count); Assert.Equal(0, disk.SizeBytes);
    }

    [Fact]
    public async Task DeferredReaderCannotPublishATextureWithAValidHeaderButCorruptPixelBytesOrLeakBacking()
    {
        using var files = new TestFiles(); var originalAssets = new MemoryAssetStore(); var material = Material(); var path = files.PathFor("corrupt-deferred.cadoryx");
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Corrupt deferred texture"), originalAssets, new NoGeometryKernel(), new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new SetMaterialTextureCommand(material, 2, 2, Pixels, add: true));
            await new CadDocumentStorage().SaveAsync(session.Snapshot, originalAssets, path);
        }
        Assert.Equal(0, originalAssets.Count); var asset = Assert.Single(FormatEvolutionTests.Manifest(path).Assets);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(asset.Path)!; byte[] bytes;
            using (var input = entry.Open()) { using var buffer = new MemoryStream(); input.CopyTo(buffer); bytes = buffer.ToArray(); }
            bytes[^1] ^= 255; entry.Delete(); using var output = zip.CreateEntry(asset.Path).Open(); output.Write(bytes);
        }
        using var disk = new DiskAssetStore(files.PathFor("rejected-cache"));
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new CadDocumentStorage(deferGeometryAssets: true).LoadAsync(path, disk));
        Assert.StartsWith("Entry SHA-256 mismatch:", failure.Message); Assert.Contains(asset.Path, failure.Message);
        Assert.Equal(0, disk.DeferredCount); Assert.Equal(0, disk.Count); Assert.Equal(0, disk.SizeBytes);
    }

    [Fact]
    public async Task ClosingResourceEditorCancelsImageReadAndIgnoresAnUncooperativeLateResult()
    {
        var assets = new MemoryAssetStore(); var kernel = new NoGeometryKernel();
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Closed image editor"), assets, kernel, new InlineSessionDispatcher()))
        {
            var vm = new CadDocumentViewModel(session, kernel, new CadMessageLog()); var importer = new HeldTextureImporter();
            using var editor = new DocumentResourcesViewModel(vm, importer); var before = session.Snapshot;
            try
            {
                var operation = editor.ImportMaterialTextureCommand.ExecuteAsync(null); await importer.Started.Task;
                editor.Dispose(); Assert.True(importer.Token.IsCancellationRequested); Assert.False(editor.CanEdit);
                importer.Result.SetResult(new(2, 2, Pixels)); await operation;
                Assert.Null(editor.MaterialAppearance.PendingTexture); Assert.False(editor.MaterialAppearance.Enabled);
                Assert.Same(before, session.Snapshot); Assert.False(session.CanUndo); Assert.Equal(0, assets.Count); Assert.Equal(0, kernel.Calls);
            }
            finally { await vm.StopToolsAsync(); vm.Detach(); }
        }
        Assert.Equal(0, assets.Count);
    }

    [Theory]
    [InlineData("negative-s")]
    [InlineData("negative-t")]
    [InlineData("small-scale")]
    [InlineData("large-scale")]
    [InlineData("translation-s")]
    [InlineData("translation-t")]
    [InlineData("rotation")]
    [InlineData("large-plane")]
    [InlineData("tiny-independent-planes")]
    [InlineData("parallel-planes")]
    public async Task UnsupportedMappingCannotPublishAnUnrenderableMaterial(string failure)
    {
        var mapping = failure switch
        {
            "negative-s" => new CadTextureMapping { ScaleS = -1 },
            "negative-t" => new CadTextureMapping { ScaleT = -1 },
            "small-scale" => new CadTextureMapping { ScaleS = 1e-7 },
            "large-scale" => new CadTextureMapping { ScaleT = 1000001 },
            "translation-s" => new CadTextureMapping { TranslationS = -1000001 },
            "translation-t" => new CadTextureMapping { TranslationT = 1000001 },
            "rotation" => new CadTextureMapping { RotationDegrees = 360001 },
            "large-plane" => new CadTextureMapping { Planar = true, PlaneS = new(1000001,0,0,0) },
            "tiny-independent-planes" => new CadTextureMapping { Planar = true, PlaneS = new(1e-8,0,0,0), PlaneT = new(0,1e-8,0,0) },
            "parallel-planes" => new CadTextureMapping { Planar = true, PlaneS = new(1,0,0,0), PlaneT = new(2,0,0,0) },
            _ => throw new ArgumentException("Unknown mapping failure.")
        };
        var assets = new MemoryAssetStore();var kernel = new NoGeometryKernel();
        await using(var session = new CadDocumentSession(DocumentSnapshot.Create("Mapping guard"),assets,kernel,new InlineSessionDispatcher()))
        {
            var before = session.Snapshot;var material = Material();
            material = material with { Appearance = material.Appearance! with { Mapping = mapping } };
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(ResourceCommands.AddMaterial(material)));
            Assert.Same(before,session.Snapshot);Assert.False(session.CanUndo);Assert.Equal(0,assets.Count);Assert.Equal(0,kernel.Calls);
        }
        Assert.Equal(0,assets.Count);
    }

    private sealed class HeldTextureImporter : IMaterialTextureImportService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImportedMaterialTexture?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<ImportedMaterialTexture?> ImportAsync(CancellationToken token = default)
        { Token = token; Started.SetResult(); return Result.Task; }
    }

    private sealed class NoGeometryKernel : IGeometryKernel
    {
        public int Calls { get; private set; }
        public string Version => "No geometry allowed";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Material edits cannot rebuild geometry."); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
