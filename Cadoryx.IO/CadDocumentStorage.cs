using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

/// <summary>Versioned ZIP container. Metadata reads never initialize OCCT.</summary>
public sealed class CadDocumentStorage(CadSectionMigrationRegistry? migrations=null,StorageLimits? limits=null,bool deferGeometryAssets=false,IDatumGeometryResolver? datumResolver=null,ISketchEdgeProjector? edgeProjector=null,IDrawingHoleResolver? holeResolver=null,IDrawingSizeResolver? sizeResolver=null,IDrawingTopologyHistoryResolver? drawingHistoryResolver=null,IDrawingPlanarSizeResolver? planarSizeResolver=null,IContourLocalResolver? contourResolver=null,IFaceEditResolver? faceEditResolver=null,IRepetitionPresentationVerifier? repetitionVerifier=null,IMetricThreadVerifier? threadVerifier=null,IFiniteSurfaceExtrusionResolver? surfaceExtrusionResolver=null,ISketchExpressionRegionVerifier? regionVerifier=null) : IDocumentStorage
{
    private readonly CadSectionMigrationRegistry registry=migrations??new();
    private readonly StorageLimits limits=limits??new();
    private static readonly HashSet<string> KnownSections=CadSectionMigrationRegistry.CurrentFormats.Keys.Append(GuidedModelingReportStorage.Kind).Append(OccurrenceReferenceSectionStorage.Kind).ToHashSet();
    private static readonly string[] Capabilities=["cadoryx.core.1","occt.brep.1","cadoryx.geometry-table.1","cadoryx.asset-catalog.1","cadoryx.sketches.1","cadoryx.sketch-point-origins.1","cadoryx.sketch-association.1","cadoryx.topology-references.1","cadoryx.local-box-edge.1","cadoryx.local-chamfer-two-distances.1","cadoryx.local-multi-edge.1","cadoryx.local-variable-radius.1","cadoryx.bound-variable-radius.1","cadoryx.topology-history.1","cadoryx.topology-history.2","cadoryx.topology-history.3","cadoryx.multicopy-history.1","cadoryx.multicopy-presentation.1","cadoryx.composed-drilling-history.1","cadoryx.history-queries.1","cadoryx.feature-bindings.1","cadoryx.exact-local-selection.1","cadoryx.bound-local-chamfer.1","cadoryx.circular-sketch-profile.1","cadoryx.circular-sketch-holes.1","cadoryx.sketch-angular-constraints.1","cadoryx.polygon-sketch-holes.1","cadoryx.assembly-constraints.1","cadoryx.external-parts.1","cadoryx.engineering-review.1","cadoryx.technical-drawings.1","cadoryx.analytical-revolve.1","cadoryx.sketch-projection.1","cadoryx.sketch-basic-relations.1","cadoryx.sketch-endpoint-tangency.1","cadoryx.sketch-parametric-tangency.1","cadoryx.sketch-arc-radius.1","cadoryx.sketch-circular-contact.1","cadoryx.sketch-endpoint-curvature.1","cadoryx.sketch-finite-parametric-contact.1","cadoryx.sketch-mixed-finite-continuity.1","cadoryx.sketch-sliding-contact-parameters.1","cadoryx.document-parameters.1","cadoryx.associated-datums.1","cadoryx.result-repetition.1","cadoryx.standard-holes.1","cadoryx.thread-hole-intent.1","cadoryx.metric-thread-solid.1","cadoryx.metric-thread-proof.1","cadoryx.advanced-extrusion.1","cadoryx.finite-surface-extrusion.1","cadoryx.guided-authoring.1","cadoryx.face-features.1","cadoryx.solid-offset.1","cadoryx.contour-local.1","cadoryx.face-editing.1","cadoryx.extended-datums.1","cadoryx.revolve-axis.1","cadoryx.hole-replay.1","cadoryx.extrusion-replay.1","cadoryx.assembly-patterns.1","cadoryx.assembly-mirrors.1","cadoryx.curved-region-revolve.1","cadoryx.exact-sketch-conics.1","cadoryx.native-edge-projection.1","cadoryx.exact-sketch-freeforms.1","cadoryx.exact-sketch-endpoints.1","cadoryx.exact-curve-parameters.1","cadoryx.periodic-native-intervals.1","cadoryx.section-hatching.1","cadoryx.section-hatch-rules.1","cadoryx.manufacturing-templates.1","cadoryx.manufacturing-annotations.1","cadoryx.drawing-template-library.1","cadoryx.current-hole-tables.1","cadoryx.hole-datum-coordinates.1","cadoryx.typed-manufacturing-datums.1","cadoryx.original-attachment-history.1","cadoryx.planar-manufacturing-sizes.1"];
    private static readonly string[] CurrentCapabilities=[..Capabilities,"cadoryx.exact-sketch-expressions.1","cadoryx.exact-expression-profiles.1","cadoryx.exact-expression-contacts.1","cadoryx.associative-expression-endpoints.1","cadoryx.associative-expression-regions.1","cadoryx.region-offset-parameters.1","cadoryx.region-offset-connections.1",GuidedModelingReportStorage.Capability,"cadoryx.native-reference-tables.1",OccurrenceReferenceSectionStorage.Capability];
    public async Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshot.Validate();using var hold=new DocumentAssetLease(snapshot,assets);
        ConfirmExpressionRegions(snapshot,cancellationToken);
        await ConfirmRepetitionPresentationAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmContoursAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmFaceEditsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmModeledThreadsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmFiniteSurfaceExtrusionsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmDatumsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmHoleTablesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmDrawingSizesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmDrawingPlanarSizesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        await ConfirmDrawingHistoryAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
        var full=Path.GetFullPath(path);var directory=Path.GetDirectoryName(full)!;Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,"."+Path.GetFileName(full)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await WriteAsync(snapshot,assets,temp,cancellationToken).ConfigureAwait(false);
            // Verify every recorded entry before replacing the old document.
            using(var stream=File.OpenRead(temp))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                var entries=ValidateEntries(zip);
                var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
                foreach(var section in manifest.Sections)await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false);
                foreach(var asset in manifest.Assets)await VerifyStream(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if(File.Exists(full))File.Replace(temp,full,null);else File.Move(temp,full);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token)
    {
        var sections=ImmutableArray.CreateBuilder<SectionEntry>();var catalog=AssetCatalog.Build(snapshot,assets,token);
        await using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
        {
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                async Task Section(SectionPayload payload)
                {
                    var bytes=payload.Bytes;var kind=payload.Format.Kind;
                    string entry="sections/"+kind+".msgpack";await WriteEntry(zip,entry,bytes,token);
                    sections.Add(new(kind,entry,payload.Format.Version,true,bytes.Length,Hash(bytes.Span),payload.Format.Encoding));
                }
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["document"],MessagePackSections.Encode("document",new DocumentSection(snapshot.Id,snapshot.StateId,snapshot.Name,snapshot.RootAssemblyId,snapshot.Settings,snapshot.AssemblyConstraints.Values.ToImmutableArray()))));
                foreach(var payload in MessagePackSections.SplitGeometry(new(snapshot.Definitions.Values.ToImmutableArray(),snapshot.Bodies.Values.ToImmutableArray()),new(snapshot.Features.Values.ToImmutableArray()),
                    datumGeometries:DatumConstructions.ExactSources(snapshot.Datums).Select(e=>e.Source).Concat(snapshot.Sketches.Values.SelectMany(s=>s.NativeProjections.Select(p=>p.Source)))))await Section(payload);
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["presentation"],MessagePackSections.Encode("presentation",new PresentationSection(snapshot.Layers.Values.ToImmutableArray(),snapshot.Materials.Values.ToImmutableArray()))));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["sketches"],MessagePackSections.EncodeSketches(snapshot.Sketches.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.EncodeMessagePack(snapshot.Sketches.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["sketch-regions"],SketchRegionSectionStorage.EncodeMessagePack(snapshot.Sketches.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["topology"],MessagePackSections.EncodeTopology(snapshot.TopologyReferences.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["history"],MessagePackSections.EncodeHistories(snapshot.Features.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["history-queries"],MessagePackSections.EncodeHistoryQueries(snapshot.HistoryQueries.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["feature-bindings"],MessagePackSections.EncodeFeatureBindings(snapshot.Features.Values)));
                bool hasGuidedReports=snapshot.Features.Values.Any(f=>f.GuidedModelingReport is not null);
                bool hasReferenceTables=snapshot.ReferencedGeometry().Any(g=>g.SerializedReferences is not null);
                bool hasOccurrenceTables=snapshot.ReferencedGeometry().Any(g=>g.SerializedOccurrences is not null);
                if(hasOccurrenceTables)await Section(new(OccurrenceReferenceSectionStorage.Format,OccurrenceReferenceSectionStorage.Encode(snapshot)));
                if(hasGuidedReports)await Section(new(GuidedModelingReportStorage.Format,GuidedModelingReportStorage.EncodeMessagePack(snapshot.Features.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["external-parts"],
                    MessagePackSections.EncodeExternalParts(snapshot.ExternalParts.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["engineering-review"],MessagePackSections.EncodeEngineeringReview(snapshot)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["drawings"],MessagePackSections.EncodeDrawings(snapshot)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["parameters"],MessagePackSections.EncodeParameters(snapshot.Parameters)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["datums"],MessagePackSections.EncodeDatums(snapshot.Datums)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["assembly-patterns"],MessagePackSections.EncodeAssemblyPatterns(snapshot.AssemblyPatterns.Values)));
                if(!snapshot.Extensions.IsDefault)
                    foreach(var extension in snapshot.Extensions)
                    {
                        if(KnownSections.Contains(extension.Kind))throw new InvalidDataException("Extension collides with a core section.");
                        using var lease=assets.Acquire(extension.PayloadAssetId);var bytes=lease.Content;
                        string entry="extensions/"+sections.Count+".bin";await WriteEntry(zip,entry,bytes,token);
                        sections.Add(new(extension.Kind,entry,extension.SchemaVersion,false,bytes.Length,Hash(bytes.Span),extension.Encoding));
                    }
                foreach(var asset in catalog)
                {
                    using var lease=assets.Acquire(asset.Id);await WriteEntry(zip,asset.Path,lease.Content,token);
                }
                var manifest=new CadManifest("Cadoryx",1,snapshot.Id,snapshot.StateId,hasOccurrenceTables?"0.7.9":"0.7.8",sections.ToImmutable(),catalog.ToImmutableArray(),
                    [..CurrentCapabilities.Where(c=>(c!=GuidedModelingReportStorage.Capability||hasGuidedReports)&&
                        (c!="cadoryx.native-reference-tables.1"||hasReferenceTables)&&
                        (c!=OccurrenceReferenceSectionStorage.Capability||hasOccurrenceTables))],1);
                await WriteEntry(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,CadJson.Options),token);
            }
            await stream.FlushAsync(token);stream.Flush(true);
        }
    }
    public async Task<LoadedDocument> LoadAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        using var archive=deferGeometryAssets&&assets is IDeferredAssetStore deferred?
            await Task.Run(()=>deferred.StageFile(path,cancellationToken),cancellationToken).ConfigureAwait(false):null;
        using var stream=archive?.OpenRead()??File.OpenRead(path);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        var entries=ValidateEntries(zip);var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
        var sectionValues=new Dictionary<string,(SectionEntry Entry,byte[] Bytes)>(StringComparer.Ordinal);
        foreach(var section in manifest.Sections)
        {
            if(!KnownSections.Contains(section.Kind)&&section.Required)throw new NotSupportedException($"Required section {section.Kind} is unsupported.");
            sectionValues.Add(section.Kind,(section,await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false)));
        }
        if(sectionValues.TryGetValue("sketches",out var sketchSection)&&sketchSection.Entry.Encoding=="messagepack"&&
           MessagePackSections.HasSlidingContact(sketchSection.Bytes)&&
           (!sectionValues.TryGetValue("document",out var authorDocument)||authorDocument.Entry.SchemaVersion<52))
            throw new InvalidDataException("Automatic contact authors cannot appear under a legacy document schema.");
        if(sectionValues.ContainsKey("sketch-expressions")&&
           (!sectionValues.TryGetValue("document",out var expressionDocument)||expressionDocument.Entry.SchemaVersion<53))
            throw new InvalidDataException("Exact expression authors cannot appear under a legacy document schema.");
        if(sectionValues.TryGetValue("sketch-expressions",out var expressionSection)&&
           (!sectionValues.TryGetValue("document",out var contactDocument)||contactDocument.Entry.SchemaVersion<55)&&
           SketchExpressionSectionStorage.HasContacts(expressionSection.Bytes,expressionSection.Entry.Encoding,expressionSection.Entry.SchemaVersion))
            throw new InvalidDataException("Exact expression contacts cannot appear under a legacy document schema.");
        if(sectionValues.TryGetValue("sketch-expressions",out var endpointSection)&&
           (!sectionValues.TryGetValue("document",out var endpointDocument)||endpointDocument.Entry.SchemaVersion<56)&&
           SketchExpressionSectionStorage.HasEndpoints(endpointSection.Bytes,endpointSection.Entry.Encoding,endpointSection.Entry.SchemaVersion))
            throw new InvalidDataException("Associative expression endpoints cannot appear under a legacy document schema.");
        if(sectionValues.TryGetValue("sketches",out var originSection)&&originSection.Entry.SchemaVersion>=22&&
           (!sectionValues.TryGetValue("document",out var originDocument)||originDocument.Entry.SchemaVersion<57))
            throw new InvalidDataException("Point origins cannot appear under a legacy document schema.");
        if(sectionValues.ContainsKey("sketch-regions")&&
           (!sectionValues.TryGetValue("document",out var regionDocument)||regionDocument.Entry.SchemaVersion<58))
            throw new InvalidDataException("Region author groups cannot appear under a legacy document schema.");
        if(sectionValues.TryGetValue("sketch-regions",out var connectionSection)&&
           (!sectionValues.TryGetValue("document",out var connectionDocument)||connectionDocument.Entry.SchemaVersion<60)&&
           SketchRegionSectionStorage.HasRecordedConnections(connectionSection.Bytes,connectionSection.Entry.Encoding,connectionSection.Entry.SchemaVersion))
            throw new InvalidDataException("Recorded region connection authors cannot appear under a legacy document schema.");
        if(sectionValues.TryGetValue("parameters",out var regionParameters)&&
           (!sectionValues.TryGetValue("document",out var parameterDocument)||parameterDocument.Entry.SchemaVersion<59)&&
           Malformed(()=>MessagePackSections.HasRegionParameterTargets(regionParameters.Bytes)))
            throw new InvalidDataException("Whole-region distance parameter targets cannot appear under a legacy document schema.");
        bool declaresGuidedReports=manifest.RequiredCapabilities.Contains(GuidedModelingReportStorage.Capability);
        bool includesGuidedReports=sectionValues.TryGetValue(GuidedModelingReportStorage.Kind,out var guidedReports);
        if(declaresGuidedReports!=includesGuidedReports||includesGuidedReports&&
            (!guidedReports.Entry.Required||!sectionValues.TryGetValue("document",out var reportDocument)||reportDocument.Entry.SchemaVersion<60))
            throw new InvalidDataException("Guided modeling reports require their declared capability, required section and supported author-document context.");
        bool hasReferenceDeclarations=sectionValues.TryGetValue("geometry",out var referenceGeometry)&&referenceGeometry.Entry.SchemaVersion>=2&&
            Malformed(()=>MessagePackSections.HasGeometryReferences(referenceGeometry.Bytes));
        if(hasReferenceDeclarations&&!manifest.RequiredCapabilities.Contains("cadoryx.native-reference-tables.1"))
            throw new InvalidDataException("Native reference tables require their declared reader capability.");
        bool declaresOccurrences=manifest.RequiredCapabilities.Contains(OccurrenceReferenceSectionStorage.Capability);
        bool includesOccurrences=sectionValues.TryGetValue(OccurrenceReferenceSectionStorage.Kind,out var occurrenceSection);
        if(declaresOccurrences!=includesOccurrences||includesOccurrences&&(!occurrenceSection.Entry.Required||
            !sectionValues.TryGetValue("document",out var occurrenceDocument)||occurrenceDocument.Entry.SchemaVersion<60||referenceGeometry.Entry is null||referenceGeometry.Entry.SchemaVersion<2))
            throw new InvalidDataException("Complete occurrence tables require their capability, required section and supported geometry/document context.");
        var occurrenceDeclarations=includesOccurrences?Malformed(()=>OccurrenceReferenceSectionStorage.Read(occurrenceSection.Bytes,occurrenceSection.Entry.Encoding,occurrenceSection.Entry.SchemaVersion)):[];
        var core=Migrate(sectionValues.Values.Where(v=>CadSectionMigrationRegistry.CurrentFormats.ContainsKey(v.Entry.Kind)).Select(v=>Payload(v.Entry,v.Bytes)),CadSectionMigrationRegistry.CurrentFormats,cancellationToken);
        if(Malformed(()=>MessagePackSections.HasGeometryReferences(core["geometry"].Bytes))&&
            (!hasReferenceDeclarations||!manifest.RequiredCapabilities.Contains("cadoryx.native-reference-tables.1")))
            throw new InvalidDataException("Legacy sections cannot manufacture stored native reference evidence.");
        var document=Decode<DocumentSection>(core["document"]);var presentation=Decode<PresentationSection>(core["presentation"]);
        if(document.Id!=manifest.DocumentId||document.StateId!=manifest.StateId)throw new InvalidDataException("Manifest/document identity mismatch.");
        var drawings=Malformed(()=>MessagePackSections.AttachDrawings(core["drawings"].Bytes,DocumentSnapshot.Create("Reading drawing assets") with{Id=document.Id}));
        var roles=new Dictionary<AssetId,string>(Malformed(()=>MessagePackSections.RequiredAssetRoles(core["geometry"])));
        if(includesOccurrences)Malformed(()=>{OccurrenceReferenceSectionStorage.AddAssetRoles(occurrenceDeclarations,core["geometry"].Bytes,roles);return true;});
        foreach(var id in drawings.DrawingHistoryAssets())
        {
            id.Validate();
            if(roles.TryGetValue(id,out var role)&&role!=AssetFormat.BRepMediaType)throw new InvalidDataException("Conflicting manufacturing history asset role.");
            roles[id]=AssetFormat.BRepMediaType;
        }
        bool hasExtensions=sectionValues.Keys.Any(k=>!KnownSections.Contains(k));
        if(!hasExtensions&&manifest.Assets.Any(a=>!roles.ContainsKey(a.Id)))throw new InvalidDataException("Unreferenced asset without an extension section.");
        var leases=new List<IAssetLease>();
        var formats=new Dictionary<AssetId,AssetFormat>();
        try
        {
            foreach(var asset in manifest.Assets)
            {
                if(archive is not null&&asset.Format is {} knownFormat&&roles.TryGetValue(asset.Id,out var knownRole))
                {
                    AssetFormatPolicy.RequireRole(knownFormat,knownRole);
                    if(asset.Length<=0||asset.Length>limits.MaxAssetBytes||entries[asset.Path].Length!=asset.Length)
                        throw new InvalidDataException("Invalid deferred payload length.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var backing=(IAssetFileLease)assets.Acquire(archive.Id);
                    try
                    {
                        var deferredLease=((IDeferredAssetStore)assets).StageDeferred(asset.Id,asset.Length,()=>
                        {
                            using var ownedStream=backing.OpenRead();using var ownedZip=new ZipArchive(ownedStream,ZipArchiveMode.Read);
                            var ownedEntry=ownedZip.GetEntry(asset.Path)??throw new InvalidDataException("Deferred payload missing.");
                            var bytes=ReadVerified(new(StringComparer.OrdinalIgnoreCase){{asset.Path,ownedEntry}},asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,CancellationToken.None).GetAwaiter().GetResult();
                            AssetFormatPolicy.VerifyPayload(knownFormat,bytes);return bytes;
                        },backing);
                        leases.Add(deferredLease);formats.Add(asset.Id,knownFormat);
                    }
                    catch{backing.Dispose();throw;}
                    continue;
                }
                var bytes=await ReadVerified(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
                bool required=roles.TryGetValue(asset.Id,out var role);
                var format=asset.Format??(required?AssetFormatPolicy.Inspect(bytes):AssetFormat.Opaque);
                if(required)
                {
                    AssetFormatPolicy.RequireRole(format,role!);
                    AssetFormatPolicy.VerifyPayload(format,bytes);
                }
                formats.Add(asset.Id,format);
                var lease=assets.Stage(bytes);leases.Add(lease);
                if(lease.Id!=asset.Id)throw new InvalidDataException("Asset identity mismatch.");
            }
            var (structure,features)=Join(core,formats);
            var extensions=ImmutableArray.CreateBuilder<PreservedSection>();
            foreach(var (kind,value) in sectionValues.Where(x=>!KnownSections.Contains(x.Key)))
            {
                var lease=assets.Stage(value.Bytes);leases.Add(lease);extensions.Add(new(kind,value.Entry.SchemaVersion,lease.Id,value.Entry.Encoding));
            }
            var snapshot=new DocumentSnapshot(document.Id,document.StateId,document.Name,document.RootAssemblyId,document.Settings,
                structure.Definitions.ToImmutableDictionary(x=>x.Id),structure.Bodies.ToImmutableDictionary(x=>x.Id),
                Malformed(()=>MessagePackSections.AttachFeatureBindings(core["feature-bindings"].Bytes,
                    MessagePackSections.AttachHistories(core["history"].Bytes,features.Features.ToImmutableDictionary(x=>x.Id)),document.Id)),presentation.Layers.ToImmutableDictionary(x=>x.Id),
                presentation.Materials.ToImmutableDictionary(x=>x.Id),extensions.ToImmutable(),extensions.Count>0?manifest.Assets.Select(x=>x.Id).ToImmutableArray():[],extensions.Count>0?formats.ToImmutableDictionary():null)
                {Parameters=Malformed(()=>MessagePackSections.DecodeParameters(core["parameters"].Bytes)),
                 Datums=Malformed(()=>MessagePackSections.DecodeDatums(core["datums"].Bytes,document.Id,formats)),
                 AssemblyPatterns=Malformed(()=>MessagePackSections.DecodeAssemblyPatterns(core["assembly-patterns"].Bytes)),
                 Sketches=Malformed(()=>SketchRegionSectionStorage.AttachMessagePack(core["sketch-regions"].Bytes,
                     SketchExpressionSectionStorage.AttachMessagePack(core["sketch-expressions"].Bytes,
                     MessagePackSections.DecodeSketches(core["sketches"].Bytes,formats)))),
                 TopologyReferences=Malformed(()=>MessagePackSections.DecodeTopology(core["topology"].Bytes)),
                 HistoryQueries=Malformed(()=>MessagePackSections.DecodeHistoryQueries(core["history-queries"].Bytes,document.Id)),
                 ExternalParts=Malformed(()=>MessagePackSections.DecodeExternalParts(core["external-parts"].Bytes)),
                 AssemblyConstraints=Malformed(()=>(document.AssemblyConstraints.IsDefault?[]:document.AssemblyConstraints)
                     .ToImmutableDictionary(c=>c.Id))};
            if(includesGuidedReports)snapshot=snapshot with{Features=Malformed(()=>GuidedModelingReportStorage.Attach(guidedReports.Bytes,guidedReports.Entry.Encoding,guidedReports.Entry.SchemaVersion,snapshot.Features))};
            snapshot=Malformed(()=>MessagePackSections.AttachEngineeringReview(core["engineering-review"].Bytes,snapshot));
            snapshot=snapshot with{DrawingSheets=drawings.DrawingSheets,DrawingTemplates=drawings.DrawingTemplates};
            if(includesOccurrences)snapshot=Malformed(()=>OccurrenceReferenceSectionStorage.Attach(snapshot,occurrenceDeclarations,formats));
            Malformed(()=>{snapshot.Validate();return true;});using var verify=new DocumentAssetLease(snapshot,assets);
            foreach(var geometry in snapshot.ReferencedGeometry().DistinctBy(g=>g.Revision))
            {
                SerializedReferenceAssetPolicy.VerifyGeometry(geometry,assets);
                SerializedOccurrenceAssetPolicy.VerifyGeometry(geometry,assets);
            }
            try
            {
                ConfirmExpressionRegions(snapshot,cancellationToken);
                await ConfirmRepetitionPresentationAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmContoursAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmFaceEditsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmModeledThreadsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmFiniteSurfaceExtrusionsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmDatumsAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmHoleTablesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmDrawingSizesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmDrawingPlanarSizesAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
                await ConfirmDrawingHistoryAsync(snapshot,assets,cancellationToken).ConfigureAwait(false);
            }
            catch(CadValidationException ex){throw new InvalidDataException("Exact contour, datum, edge projection or manufacturing cache differs from its stored native asset.",ex);}
            var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
            if(snapshot.Features.Values.Any(f=>f.Recipe is SweepRecipe or LoftRecipe&&f.GuidedModelingReport is null))
                diagnostics.Add(new("IO.GUIDED_REPORT_UNKNOWN","One or more stored guided results have no per-result native modeling observations. Current SDK controls are not substituted for their unknown historical modeling errors."));
            if(archive is not null)diagnostics.Add(new("IO.DEFERRED_GEOMETRY","Ordinary geometry payloads are decompressed and hash-verified on first use; structural sections and stored reference/BRep integrity envelopes were verified at open."));
            if(extensions.Count>0)diagnostics.Add(new("IO.READ_ONLY","Unknown optional sections and their asset formats are preserved; editing is disabled."));
            if(manifest.AssetCatalogVersion==0)diagnostics.Add(new("IO.LEGACY_ASSET_FORMAT","Legacy asset formats were recognized from their headers. Original kernel and writer versions are unknown."));
            if(manifest.Sections.Any(s=>CadSectionMigrationRegistry.CurrentFormats.ContainsKey(s.Kind)&&new SectionFormat(s.Kind,s.SchemaVersion,s.Encoding)!=CadSectionMigrationRegistry.CurrentFormats[s.Kind]))
                diagnostics.Add(new("IO.MIGRATED","Document sections were migrated in memory; IDs, state and geometry bytes are retained. Save writes the current format."));
            var loaded=new LoadedDocument(snapshot,leases,diagnostics.ToImmutable());
            leases.Clear();return loaded;
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    private void ConfirmExpressionRegions(DocumentSnapshot snapshot,CancellationToken token)
    {
        foreach(var sketch in snapshot.Sketches.Values.Where(s=>!s.RegionOffsets.IsEmpty))
        {
            token.ThrowIfCancellationRequested();
            var verifier=regionVerifier??datumResolver as ISketchExpressionRegionVerifier??
                throw new NotSupportedException("Opening or saving region author groups requires exact native replay verification.");
            verifier.ConfirmExpressionRegions(sketch,token);
        }
    }
    private async Task ConfirmFiniteSurfaceExtrusionsAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var features=snapshot.Features.Values.Where(f=>f.Recipe is ExtrudeRecipe {Options.Termination:ExtrusionTermination.FiniteSurface}).ToArray();
        if(features.Length==0)return;
        var resolver=surfaceExtrusionResolver??datumResolver as IFiniteSurfaceExtrusionResolver??
            throw new NotSupportedException("Opening or saving finite surface extrusions requires exact native author replay verification.");
        foreach(var feature in features)await resolver.ConfirmFiniteSurfaceExtrusionAsync((ExtrudeRecipe)feature.Recipe,feature.Result,assets,token).ConfigureAwait(false);
    }
    private async Task ConfirmModeledThreadsAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var features=snapshot.Features.Values.Where(f=>!f.IsStale&&f.Result.Kind!=BodyKind.Empty&&
            (f.Recipe is HoleRecipe {Thread.Representation:ThreadHoleRepresentation.Modeled} or
                HolePatternRecipe {Template.Thread.Representation:ThreadHoleRepresentation.Modeled})).ToArray();
        if(features.Length==0)return;
        var resolver=threadVerifier??datumResolver as IMetricThreadVerifier??holeResolver as IMetricThreadVerifier??
            throw new NotSupportedException("Opening or saving modeled thread results requires native author replay verification.");
        foreach(var feature in features)await resolver.ConfirmModeledThreadAsync(feature.Recipe,feature.Result,assets,token).ConfigureAwait(false);
    }
    private async Task ConfirmFaceEditsAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var recipes=snapshot.Features.Values.Select(f=>f.Recipe).Where(r=>r is PlanarPushPullRecipe or DefeatureRecipe).ToArray();
        if(recipes.Length==0)return;
        var resolver=faceEditResolver??datumResolver as IFaceEditResolver??throw new NotSupportedException("Opening or saving face-edit features requires an exact face resolver.");
        foreach(var recipe in recipes)await resolver.ConfirmFaceEditRecipeAsync(recipe,assets,token).ConfigureAwait(false);
    }
    private async Task ConfirmContoursAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var recipes=snapshot.Features.Values.Select(f=>f.Recipe).OfType<ContourLocalRecipe>().ToArray();
        if(recipes.Length==0)return;
        var resolver=contourResolver??datumResolver as IContourLocalResolver??throw new NotSupportedException("Opening or saving contour features requires an exact contour resolver.");
        // Frozen and suppressed recipes still bind their retained historical source, never today's indices.
        foreach(var recipe in recipes)await resolver.ConfirmContourRecipeAsync(recipe,assets,token).ConfigureAwait(false);
    }
    private async Task ConfirmDatumsAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var projections=snapshot.Sketches.Values.SelectMany(s=>s.NativeProjections.Select(p=>(Sketch:s,Projection:p))).ToArray();
        if(projections.Length>0)
        {
            var projector=edgeProjector??datumResolver as ISketchEdgeProjector??throw new NotSupportedException("Opening or saving native sketch projections requires an exact edge projector.");
            foreach(var (sketch,p) in projections)
            {
                token.ThrowIfCancellationRequested();ExactSketchCurve curve;
                if(sketch.Freeforms.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} freeform)
                    curve=new(freeform.Geometry.At(0),freeform.Geometry.At(1),Freeform:freeform.Geometry);
                else if(sketch.Ellipses.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} ellipse)
                    curve=new(ellipse.Geometry.At(0),ellipse.Geometry.At(1),Ellipse:ellipse.Geometry);
                else
                {
                    var line=sketch.Lines.Single(l=>l.Id==p.TargetEntity);
                    curve=new(sketch.Points.Single(v=>v.Id==line.Start).Position,sketch.Points.Single(v=>v.Id==line.End).Position);
                }
                // A frozen source is checked against its retained original BRep, without reattaching it.
                await projector.ConfirmEdgeProjectionAsync(new(sketch.PartId,p.Selection,p.Source,p.TargetPlane,curve),assets,token).ConfigureAwait(false);
            }
        }
        var evidence=DatumConstructions.ExactSources(snapshot.Datums).ToArray();if(evidence.Length==0)return;
        if(datumResolver is null)throw new NotSupportedException("Opening or saving exact datum sources requires an analytic geometry resolver.");
        foreach(var source in evidence)await datumResolver.ConfirmDatumGeometryAsync(source,assets,token).ConfigureAwait(false);
    }
    private async Task ConfirmHoleTablesAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var current=snapshot.DrawingSheets.Values.SelectMany(s=>s.HoleTables.Where(t=>t.StaleReason is null)
            .SelectMany(t=>t.Rows.Where(r=>r.StaleReason is null))).ToArray();
        if(current.Length==0)return;
        var resolver=holeResolver??datumResolver as IDrawingHoleResolver??
            throw new NotSupportedException("Opening or saving current manufacturing hole tables requires an exact hole resolver.");
        foreach(var row in current)
        {
            token.ThrowIfCancellationRequested();
            var actual=await resolver.ResolveDrawingHoleAsync(snapshot,row.Evidence.Source,assets,token).ConfigureAwait(false);
            if(!row.Evidence.SameProof(actual))throw new CadValidationException("Stored hole dimensions or original topology proof differ from the current solid.");
        }
    }
    private async Task ConfirmDrawingSizesAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var current=snapshot.DrawingSheets.Values.SelectMany(s=>s.DatumSymbols.Where(d=>d.StaleReason is null).Select(d=>d.SizeEvidence)
            .Concat(s.ControlFrames.Where(f=>f.StaleReason is null).Select(f=>f.SizeEvidence))).OfType<DrawingCylinderSizeEvidence>().ToArray();
        if(current.Length==0)return;
        var resolver=sizeResolver??datumResolver as IDrawingSizeResolver??holeResolver as IDrawingSizeResolver??
            throw new NotSupportedException("Opening or saving current cylindrical manufacturing annotations requires an original size resolver.");
        foreach(var evidence in current)
        {
            token.ThrowIfCancellationRequested();
            var actual=await resolver.ResolveDrawingCylinderSizeAsync(snapshot,evidence.Source,assets,token).ConfigureAwait(false);
            if(!evidence.SameProof(actual))throw new CadValidationException("Stored manufacturing size dimensions or original rings differ from the current solid.");
        }
    }
    private async Task ConfirmDrawingPlanarSizesAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var current=snapshot.DrawingSheets.Values.SelectMany(s=>s.DatumSymbols.Where(d=>d.StaleReason is null).Select(d=>d.PlanarSizeEvidence)
            .Concat(s.ControlFrames.Where(f=>f.StaleReason is null).Select(f=>f.PlanarSizeEvidence))).OfType<DrawingPlanarSizeEvidence>().ToArray();
        if(current.Length==0)return;
        var resolver=planarSizeResolver??datumResolver as IDrawingPlanarSizeResolver??sizeResolver as IDrawingPlanarSizeResolver??holeResolver as IDrawingPlanarSizeResolver??
            throw new NotSupportedException("Opening or saving current planar manufacturing annotations requires an original two-face size resolver.");
        foreach(var evidence in current)
        {
            token.ThrowIfCancellationRequested();
            var actual=await resolver.ResolveDrawingPlanarSizeAsync(snapshot,evidence.Source,evidence.OppositeSource,evidence.Internal,assets,token).ConfigureAwait(false);
            if(!evidence.SameProof(actual))throw new CadValidationException("Stored planar width or either finite original domain differs from the current solid.");
        }
    }
    private async Task ConfirmRepetitionPresentationAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var features=snapshot.Features.Values.Where(f=>f.RepetitionPresentation is not null).ToArray();
        if(features.Length==0)return;
        var resolver=repetitionVerifier??datumResolver as IRepetitionPresentationVerifier??
            throw new NotSupportedException("Opening or saving repetition presentation requires complete native replay verification.");
        foreach(var feature in features)
        {
            token.ThrowIfCancellationRequested();
            await resolver.ConfirmRepetitionPresentationAsync(feature,assets,token).ConfigureAwait(false);
        }
    }
    private async Task ConfirmDrawingHistoryAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var current=snapshot.DrawingSheets.Values.SelectMany(s=>s.DatumSymbols.Where(d=>d.StaleReason is null).Select(d=>(d.ViewId,d.Source))
            .Concat(s.ControlFrames.Where(f=>f.StaleReason is null).Select(f=>(f.ViewId,f.Source)))
            .Concat(s.DatumSymbols.Where(d=>d.StaleReason is null&&d.PlanarSizeEvidence is not null).Select(d=>(d.ViewId,Source:d.PlanarSizeEvidence!.OppositeSource)))
            .Concat(s.ControlFrames.Where(f=>f.StaleReason is null&&f.PlanarSizeEvidence is not null).Select(f=>(f.ViewId,Source:f.PlanarSizeEvidence!.OppositeSource)))
            .Concat(s.Balloons.Where(b=>b.StaleReason is null).Select(b=>(b.ViewId,b.Source)))
            .Concat(s.HoleTables.Where(t=>t.StaleReason is null).SelectMany(t=>t.Rows.Where(r=>r.StaleReason is null).Select(r=>(t.ViewId,Source:r.Evidence.Source))))
            .Where(p=>p.Source.HistoryOrigin is not null).Select(p=>(Sheet:s,Reference:p))).ToArray();
        if(current.Length==0)return;
        var resolver=drawingHistoryResolver??datumResolver as IDrawingTopologyHistoryResolver??holeResolver as IDrawingTopologyHistoryResolver??sizeResolver as IDrawingTopologyHistoryResolver??
            throw new NotSupportedException("Opening or saving continued manufacturing attachments requires original native history verification.");
        foreach(var (sheet,reference) in current)
        {
            token.ThrowIfCancellationRequested();
            var source=reference.Source;var target=sheet.Views.Single(v=>v.Id==reference.ViewId).Sources.Single(s=>s.Path.Equals(source.Path)&&s.Body==source.BodyId);
            var result=await resolver.ResolveDrawingHistoryAsync(snapshot,source.HistoryOrigin!,target,assets,token).ConfigureAwait(false);
            if(result.History.Status!=HistoryResolutionStatus.Resolved||result.Current!=source with{HistoryOrigin=null})
                throw new CadValidationException("Stored manufacturing attachment no longer has one proven original native history chain.");
        }
    }
    public async Task<DocumentSettings> ReadSettingsAsync(string path,CancellationToken cancellationToken=default)
    {
        using var stream=File.OpenRead(path);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        var entries=ValidateEntries(zip);var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
        var section=manifest.Sections.SingleOrDefault(x=>x.Kind=="document")??throw new InvalidDataException("Missing document section.");
        var bytes=await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false);
        var targets=new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}};
        var migrated=Migrate([Payload(section,bytes)],targets,cancellationToken);
        var dto=Decode<DocumentSection>(migrated["document"]);dto.Settings.Validate();return dto.Settings;
    }
    private static SectionPayload Payload(SectionEntry entry,byte[] bytes)=>new(new(entry.Kind,entry.SchemaVersion,entry.Encoding),bytes);
    private IReadOnlyDictionary<string,SectionPayload> Migrate(IEnumerable<SectionPayload> sections,IReadOnlyDictionary<string,SectionFormat> targets,CancellationToken token)
        =>Malformed(()=>registry.Migrate(sections,targets,limits.MaxJsonBytes,limits.MaxTotalBytes,token));
    private static T Decode<T>(SectionPayload payload)=>Malformed(()=>MessagePackSections.Decode<T>(payload.Format.Kind,payload.Bytes));
    private static (StructureSection Structure,FeaturesSection Features) Join(IReadOnlyDictionary<string,SectionPayload> sections,IReadOnlyDictionary<AssetId,AssetFormat> formats)
        =>Malformed(()=>MessagePackSections.JoinGeometry(sections,formats));
    private static T Malformed<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception ex) when(ex is MessagePack.MessagePackSerializationException or NullReferenceException or ArgumentException or JsonException)
        {throw new InvalidDataException("Malformed document sections.",ex);}
    }
    private Dictionary<string,ZipArchiveEntry> ValidateEntries(ZipArchive zip)
    {
        if(zip.Entries.Count>limits.MaxEntries)throw new InvalidDataException("Too many ZIP entries.");
        var result=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var entry in zip.Entries)
        {
            var name=entry.FullName;
            if(string.IsNullOrWhiteSpace(name)||name.StartsWith('/')||name.Contains('\\')||name.Contains(':')||
                name.Split('/').Any(x=>x is "" or "." or "..")||!result.TryAdd(name,entry))
                throw new InvalidDataException("Unsafe or duplicate archive path.");
            // Unix symlink file mode.
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Symbolic links are not supported.");
            if(entry.Length<0||entry.Length>limits.MaxAssetBytes)throw new InvalidDataException("Entry exceeds limit.");
            total=checked(total+entry.Length);if(total>limits.MaxTotalBytes)throw new InvalidDataException("Archive exceeds size limit.");
        }
        return result;
    }
    private async Task<CadManifest> ManifestAsync(Dictionary<string,ZipArchiveEntry> entries,CancellationToken token)
    {
        if(!entries.TryGetValue("manifest.json",out var entry))throw new InvalidDataException("Missing manifest.");
        var bytes=await ReadBounded(entry,limits.MaxManifestBytes,token).ConfigureAwait(false);
        var manifest=JsonSerializer.Deserialize<CadManifest>(bytes,CadJson.Options)??throw new InvalidDataException("Null manifest.");
        if(manifest.Format!="Cadoryx"||manifest.ContainerVersion!=1)throw new NotSupportedException("Unsupported document container.");
        CadGuard.Id(manifest.DocumentId);CadGuard.Id(manifest.StateId);
        if(manifest.AssetCatalogVersion is <0 or >1)throw new NotSupportedException("Unsupported asset catalog version.");
        if(manifest.RequiredCapabilities.IsDefault||manifest.RequiredCapabilities.Except(CurrentCapabilities).Any())throw new NotSupportedException("Required capabilities are unsupported.");
        if(manifest.Sections.IsDefault||manifest.Assets.IsDefault)throw new InvalidDataException("Missing manifest tables.");
        if(manifest.Sections.Select(x=>x.Kind).Distinct().Count()!=manifest.Sections.Length||
            manifest.Assets.Select(x=>x.Id).Distinct().Count()!=manifest.Assets.Length)throw new InvalidDataException("Duplicate manifest identity.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json"};
        foreach(var section in manifest.Sections)
        {
            if(section.SchemaVersion<1||string.IsNullOrWhiteSpace(section.Kind)||!paths.Add(section.Path)||!entries.ContainsKey(section.Path))throw new InvalidDataException("Invalid section descriptor.");
        }
        foreach(var asset in manifest.Assets)
        {
            if(manifest.AssetCatalogVersion==1&&asset.Format is null)throw new InvalidDataException("Missing asset format descriptor.");
            asset.Format?.Validate();
            asset.Id.Validate();if(asset.Id.Sha256!=asset.Sha256||!paths.Add(asset.Path)||!entries.ContainsKey(asset.Path))throw new InvalidDataException("Invalid asset descriptor.");
        }
        if(paths.Count!=entries.Count)throw new InvalidDataException("Archive contains unlisted payloads.");
        return manifest;
    }
    private static async Task<byte[]> ReadVerified(Dictionary<string,ZipArchiveEntry> entries,string path,long length,string hash,long limit,CancellationToken token)
    {
        if(!entries.TryGetValue(path,out var entry)||entry.Length!=length)throw new InvalidDataException("Entry length mismatch.");
        var bytes=await ReadBounded(entry,limit,token).ConfigureAwait(false);
        if(Hash(bytes)!=hash)throw new InvalidDataException("Entry SHA-256 mismatch: "+path);
        return bytes;
    }
    private static async Task<byte[]> ReadBounded(ZipArchiveEntry entry,long limit,CancellationToken token)
    {
        if(entry.Length>limit||entry.Length>int.MaxValue)throw new InvalidDataException("Entry exceeds read limit.");
        using var source=entry.Open();var bytes=new byte[(int)entry.Length];
        try{await source.ReadExactlyAsync(bytes,token).ConfigureAwait(false);}
        catch(EndOfStreamException ex){throw new InvalidDataException("Truncated entry.",ex);}
        if(await source.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0)throw new InvalidDataException("Expanded entry exceeds declared length.");
        return bytes;
    }
    private static async Task VerifyStream(Dictionary<string,ZipArchiveEntry> entries,string path,long length,string expected,long limit,CancellationToken token)
    {
        if(length<0||length>limit||!entries.TryGetValue(path,out var entry)||entry.Length!=length)throw new InvalidDataException("Invalid asset length.");
        using var source=entry.Open();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=System.Buffers.ArrayPool<byte>.Shared.Rent(65536);long total=0;
        try
        {
            int count;while((count=await source.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
            {
                total+=count;if(total>length)throw new InvalidDataException("Expanded asset exceeds declared length.");hash.AppendData(buffer,0,count);
            }
            if(total!=length||Convert.ToHexStringLower(hash.GetHashAndReset())!=expected)throw new InvalidDataException("Asset integrity check failed: "+path);
        }
        finally{System.Buffers.ArrayPool<byte>.Shared.Return(buffer);}
    }
    private static async Task WriteEntry(ZipArchive zip,string name,ReadOnlyMemory<byte> bytes,CancellationToken token)
    {
        var entry=zip.CreateEntry(name,CompressionLevel.Fastest);await using var output=entry.Open();await output.WriteAsync(bytes,token).ConfigureAwait(false);
    }
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
