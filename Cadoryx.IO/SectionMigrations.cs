using System.Collections.Immutable;
using System.Text.Json;

namespace Cadoryx.IO;

/// <summary>Deterministic migrations over declared section contracts, committed as an atomic section set.</summary>
public sealed partial class CadSectionMigrationRegistry
{
    public const int MaximumMigrationSteps=128;
    private readonly List<Migration> sectionMigrations=[];
    private readonly object gate=new();
    public static ImmutableDictionary<string,SectionFormat> CurrentFormats {get;}=new[]
    {
        new SectionFormat("document",60,"messagepack"),new("assembly-patterns",2,"messagepack"),new("datums",2,"messagepack"),new("parameters",3,"messagepack"),new("structure",3,"messagepack"),new("features",43,"messagepack"),new("engineering-review",1,"messagepack"),new("drawings",14,"messagepack"),
        new("presentation",2,"messagepack"),new("geometry",2,"messagepack"),new("sketches",22,"messagepack"),new("sketch-expressions",4,"messagepack"),new("sketch-regions",3,"messagepack"),new("topology",1,"messagepack"),new("history",4,"messagepack"),new("history-queries",1,"messagepack"),new("feature-bindings",2,"messagepack"),new("external-parts",1,"messagepack")
    }.ToImmutableDictionary(x=>x.Kind,StringComparer.Ordinal);

    public CadSectionMigrationRegistry(bool includeBuiltIns=true)
    {
        if(!includeBuiltIns)return;
        RegisterStep("introduce-native-reference-tables",[new("geometry",1,"messagepack")],[CurrentFormats["geometry"]],input=>
            [new(CurrentFormats["geometry"],MessagePackSections.UpgradeGeometryReferences(input["geometry"].Bytes))]);
        foreach(string kind in new[]{"document","structure","features","presentation"})
        {
            string k=kind;
            RegisterStep("json-to-messagepack-"+k,[new(k,1,"json")],[new(k,2,"messagepack")],input=>
            {
                object dto=k switch
                {
                    "document"=>Json<DocumentSection>(input[k]),"structure"=>Json<StructureSection>(input[k]),
                    "features"=>Json<FeaturesSection>(input[k]),"presentation"=>Json<PresentationSection>(input[k]),_=>throw new NotSupportedException(k)
                };
                return [new(new(k,2,"messagepack"),MessagePackSections.Encode(k,dto))];
            });
        }
        RegisterStep("extract-geometry-table",[new("structure",2,"messagepack"),new("features",2,"messagepack")],
            [CurrentFormats["structure"],new("features",3,"messagepack"),CurrentFormats["geometry"]],input=>
                MessagePackSections.SplitGeometry(MessagePackSections.Decode<StructureSection>("structure",input["structure"].Bytes),
                    MessagePackSections.Decode<FeaturesSection>("features",input["features"].Bytes),featureVersion:3));
        // Document v3 requires an explicit sketch registry, including when empty. Old documents had none.
        RegisterStep("introduce-sketch-registry",[new("document",2,"messagepack")],[new("document",3,"messagepack"),new("sketches",1,"messagepack")],input=>
            [new(new("document",3,"messagepack"),input["document"].Bytes),new(new("sketches",1,"messagepack"),MessagePackSections.EncodeSketches([]))]);
        RegisterStep("introduce-topology-references",[new("document",3,"messagepack")],[new("document",4,"messagepack"),CurrentFormats["topology"]],input=>
            [new(new("document",4,"messagepack"),input["document"].Bytes),new(CurrentFormats["topology"],MessagePackSections.EncodeTopology([]))]);
        RegisterStep("introduce-topology-history",[new("document",4,"messagepack")],[new("document",5,"messagepack"),new("history",1,"messagepack")],input=>
            [new(new("document",5,"messagepack"),input["document"].Bytes),new(new("history",1,"messagepack"),MessagePackSections.EncodeHistories([]))]);
        RegisterStep("introduce-history-queries",[new("document",5,"messagepack")],[new("document",6,"messagepack"),CurrentFormats["history-queries"]],input=>
            [new(new("document",6,"messagepack"),input["document"].Bytes),new(CurrentFormats["history-queries"],MessagePackSections.EncodeHistoryQueries([]))]);
        RegisterStep("introduce-feature-bindings",[new("document",6,"messagepack")],[new("document",7,"messagepack"),CurrentFormats["feature-bindings"]],input=>
            [new(new("document",7,"messagepack"),input["document"].Bytes),new(CurrentFormats["feature-bindings"],MessagePackSections.EncodeFeatureBindings([]))]);
        RegisterStep("document-grid-settings",[new("document",7,"messagepack")],[new("document",8,"messagepack")],input=>
            [new(new("document",8,"messagepack"),MessagePackSections.UpgradeDocumentGrid(input["document"].Bytes))]);
        RegisterStep("document-background-color",[new("document",8,"messagepack")],[new("document",10,"messagepack")],input=>
            [new(new("document",10,"messagepack"),MessagePackSections.UpgradeDocumentBackground(input["document"].Bytes))]);
        RegisterStep("document-solid-background-to-gradient",[new("document",9,"messagepack")],[new("document",10,"messagepack")],input=>
            [new(new("document",10,"messagepack"),MessagePackSections.UpgradeSolidDocumentBackground(input["document"].Bytes))]);
        RegisterStep("document-origin-axes",[new("document",10,"messagepack")],[new("document",11,"messagepack")],input=>
            [new(new("document",11,"messagepack"),MessagePackSections.UpgradeDocumentOrigin(input["document"].Bytes))]);
        RegisterStep("document-work-plane",[new("document",11,"messagepack")],[new("document",12,"messagepack")],input=>
            [new(new("document",12,"messagepack"),MessagePackSections.UpgradeDocumentWorkPlane(input["document"].Bytes))]);
        RegisterStep("document-assembly-constraints",[new("document",12,"messagepack")],[new("document",13,"messagepack")],input=>
            [new(new("document",13,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyConstraints(input["document"].Bytes))]);
        RegisterStep("document-assembly-axes",[new("document",13,"messagepack")],[new("document",14,"messagepack")],input=>
            [new(new("document",14,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyAxes(input["document"].Bytes))]);
        RegisterStep("introduce-external-parts",[new("document",14,"messagepack")],
            [new("document",15,"messagepack"),CurrentFormats["external-parts"]],input=>
            [new(new("document",15,"messagepack"),input["document"].Bytes),
                new(CurrentFormats["external-parts"],MessagePackSections.EncodeExternalParts([]))]);
        RegisterStep("document-assembly-angle-and-plane",[new("document",15,"messagepack")],
            [new("document",16,"messagepack")],input=>
            [new(new("document",16,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyAngles(input["document"].Bytes))]);
        RegisterStep("introduce-engineering-review",[new("document",16,"messagepack")],
            [new("document",17,"messagepack"),CurrentFormats["engineering-review"]],input=>
            [new(new("document",17,"messagepack"),input["document"].Bytes),new(CurrentFormats["engineering-review"],
                MessagePackSections.EncodeEngineeringReview(Cadoryx.Db.DocumentSnapshot.Create("Empty review")))]);
        RegisterStep("custom-document-work-plane",[new("document",17,"messagepack")],[new("document",18,"messagepack")],input=>
            [new(new("document",18,"messagepack"),MessagePackSections.UpgradeCustomDocumentWorkPlane(input["document"].Bytes))]);
        RegisterStep("assembly-analytic-datums",[new("document",18,"messagepack")],[new("document",19,"messagepack")],input=>
            [new(new("document",19,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyDatums(input["document"].Bytes))]);
        RegisterStep("introduce-technical-drawings",[new("document",19,"messagepack")],
            [new("document",20,"messagepack"),CurrentFormats["drawings"]],input=>
            [new(new("document",20,"messagepack"),input["document"].Bytes),
                new(CurrentFormats["drawings"],MessagePackSections.EncodeDrawings(Cadoryx.Db.DocumentSnapshot.Create("Empty drawing book")))]);
        RegisterStep("introduce-document-parameters",[new("document",20,"messagepack")],
            [new("document",21,"messagepack"),CurrentFormats["parameters"]],input=>
            [new(new("document",21,"messagepack"),input["document"].Bytes),new(CurrentFormats["parameters"],MessagePackSections.EncodeParameters(Cadoryx.Db.ParameterBook.Empty))]);
        RegisterStep("introduce-associated-datums",[new("document",21,"messagepack")],
            [new("document",22,"messagepack"),new("datums",1,"messagepack")],input=>
            [new(new("document",22,"messagepack"),input["document"].Bytes),new(new("datums",1,"messagepack"),MessagePackSections.EmptyLegacyDatums())]);
        RegisterStep("introduce-standard-holes",[new("document",22,"messagepack")],[new("document",23,"messagepack")],input=>
            [new(new("document",23,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-advanced-extrusion",[new("document",23,"messagepack")],[new("document",24,"messagepack")],input=>
            [new(new("document",24,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-guided-authoring",[new("document",24,"messagepack")],[new("document",25,"messagepack")],input=>
            [new(new("document",25,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-face-features",[new("document",25,"messagepack")],[new("document",26,"messagepack")],input=>
            [new(new("document",26,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-extended-datums",[new("document",26,"messagepack")],[new("document",27,"messagepack")],input=>
            [new(new("document",27,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-revolve-axis",[new("document",27,"messagepack")],[new("document",28,"messagepack")],input=>
            [new(new("document",28,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-hole-replay",[new("document",28,"messagepack")],[new("document",29,"messagepack")],input=>
            [new(new("document",29,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-extrusion-replay",[new("document",29,"messagepack")],[new("document",30,"messagepack")],input=>
            [new(new("document",30,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-assembly-patterns",[new("document",30,"messagepack")],[new("document",31,"messagepack"),new("assembly-patterns",1,"messagepack")],input=>
            [new(new("document",31,"messagepack"),input["document"].Bytes),new(new("assembly-patterns",1,"messagepack"),MessagePackSections.EncodeAssemblyPatterns([]))]);
        RegisterStep("introduce-assembly-mirrors",[new("document",31,"messagepack"),new("assembly-patterns",1,"messagepack")],
            [new("document",32,"messagepack"),CurrentFormats["assembly-patterns"]],input=>
            [new(new("document",32,"messagepack"),input["document"].Bytes),
                new(CurrentFormats["assembly-patterns"],MessagePackSections.UpgradeAssemblyPatterns(input["assembly-patterns"].Bytes))]);
        RegisterStep("introduce-curved-region-revolve",[new("document",32,"messagepack")],[new("document",33,"messagepack")],input=>
            [new(new("document",33,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-author-curves",[new("document",33,"messagepack")],[new("document",34,"messagepack")],input=>
            [new(new("document",34,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-ellipses",[new("document",34,"messagepack")],[new("document",35,"messagepack")],input=>
            [new(new("document",35,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-freeforms",[new("document",35,"messagepack")],[new("document",36,"messagepack")],input=>
            [new(new("document",36,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-endpoints",[new("document",36,"messagepack")],[new("document",37,"messagepack")],input=>
            [new(new("document",37,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-curve-parameters",[new("document",37,"messagepack")],[new("document",38,"messagepack")],input=>
            [new(new("document",38,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-periodic-native-intervals",[new("document",38,"messagepack")],[new("document",39,"messagepack")],input=>
            [new(new("document",39,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-solid-offset",[new("document",39,"messagepack")],[new("document",40,"messagepack")],input=>
            [new(new("document",40,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-contour-local",[new("document",40,"messagepack")],[new("document",41,"messagepack")],input=>
            [new(new("document",41,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-face-editing",[new("document",41,"messagepack")],[new("document",42,"messagepack")],input=>
            [new(new("document",42,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-circular-contact",[new("document",42,"messagepack")],[new("document",43,"messagepack")],input=>
            [new(new("document",43,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-multicopy-history",[new("document",43,"messagepack")],[new("document",44,"messagepack")],input=>
            [new(new("document",44,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-multicopy-presentation",[new("document",44,"messagepack")],[new("document",45,"messagepack")],input=>
            [new(new("document",45,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-endpoint-curvature",[new("document",45,"messagepack")],[new("document",46,"messagepack")],input=>
            [new(new("document",46,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-thread-hole-intent",[new("document",46,"messagepack")],[new("document",47,"messagepack")],input=>
            [new(new("document",47,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-modeled-thread",[new("document",47,"messagepack")],[new("document",48,"messagepack")],input=>
            [new(new("document",48,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-finite-parametric-contact",[new("document",48,"messagepack")],[new("document",49,"messagepack")],input=>
            [new(new("document",49,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-finite-surface-extrusion",[new("document",49,"messagepack")],[new("document",50,"messagepack")],input=>
            [new(new("document",50,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-mixed-finite-continuity",[new("document",50,"messagepack")],[new("document",51,"messagepack")],input=>
            [new(new("document",51,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-sliding-contact-parameters",[new("document",51,"messagepack")],[new("document",52,"messagepack")],input=>
            [new(new("document",52,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-owned-sketch-expressions",[new("document",52,"messagepack")],
            [new("document",53,"messagepack"),CurrentFormats["sketch-expressions"]],input=>
            [new(new("document",53,"messagepack"),input["document"].Bytes),new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.EncodeMessagePack([]))]);
        RegisterStep("introduce-exact-expression-profiles",[new("document",53,"messagepack")],[new("document",54,"messagepack")],input=>
            [new(new("document",54,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-exact-expression-contacts",[new("document",54,"messagepack")],[new("document",55,"messagepack")],input=>
            [new(new("document",55,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-associative-expression-endpoints",[new("document",55,"messagepack")],[new("document",56,"messagepack")],input=>
            [new(new("document",56,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-sketch-point-origins",[new("document",56,"messagepack")],[new("document",57,"messagepack")],input=>
            [new(new("document",57,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-sketch-region-authors",[new("document",57,"messagepack")],
            [new("document",58,"messagepack"),new("sketch-regions",2,"messagepack")],input=>
            [new(new("document",58,"messagepack"),input["document"].Bytes),new(new("sketch-regions",2,"messagepack"),SketchRegionSectionStorage.EncodeLegacyEmptyMessagePack())]);
        RegisterStep("introduce-region-distance-parameters",[new("document",58,"messagepack")],[new("document",59,"messagepack")],input=>
            [new(new("document",59,"messagepack"),input["document"].Bytes)]);
        RegisterStep("introduce-region-connection-authors",[new("document",59,"messagepack")],[CurrentFormats["document"]],input=>
            [new(CurrentFormats["document"],input["document"].Bytes)]);
        RegisterStep("json-sketch-region-authors",[new("sketch-regions",1,"json")],[CurrentFormats["sketch-regions"]],input=>
            [new(CurrentFormats["sketch-regions"],SketchRegionSectionStorage.JsonToMessagePack(input["sketch-regions"].Bytes,1))]);
        RegisterStep("json-sketch-region-connections",[new("sketch-regions",2,"json")],[CurrentFormats["sketch-regions"]],input=>
            [new(CurrentFormats["sketch-regions"],SketchRegionSectionStorage.JsonToMessagePack(input["sketch-regions"].Bytes))]);
        RegisterStep("legacy-sketch-region-connections",[new("sketch-regions",2,"messagepack")],[CurrentFormats["sketch-regions"]],input=>
            [new(CurrentFormats["sketch-regions"],SketchRegionSectionStorage.UpgradeMessagePack(input["sketch-regions"].Bytes))]);
        RegisterStep("json-owned-sketch-expressions",[new("sketch-expressions",1,"json")],[CurrentFormats["sketch-expressions"]],input=>
            [new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.JsonToMessagePack(input["sketch-expressions"].Bytes,1))]);
        RegisterStep("json-exact-expression-contacts",[new("sketch-expressions",2,"json")],[CurrentFormats["sketch-expressions"]],input=>
            [new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.JsonToMessagePack(input["sketch-expressions"].Bytes,2))]);
        RegisterStep("json-associative-expression-endpoints",[new("sketch-expressions",3,"json")],[CurrentFormats["sketch-expressions"]],input=>
            [new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.JsonToMessagePack(input["sketch-expressions"].Bytes))]);
        RegisterStep("exact-expression-contacts",[new("sketch-expressions",2,"messagepack")],[CurrentFormats["sketch-expressions"]],input=>
            [new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.UpgradeMessagePack(input["sketch-expressions"].Bytes))]);
        RegisterStep("associative-expression-endpoints",[new("sketch-expressions",3,"messagepack")],[CurrentFormats["sketch-expressions"]],input=>
            [new(CurrentFormats["sketch-expressions"],SketchExpressionSectionStorage.UpgradeMessagePack(input["sketch-expressions"].Bytes,3))]);
        RegisterStep("exact-curve-parameter-targets",[new("parameters",1,"messagepack")],[new("parameters",2,"messagepack")],input=>
            [new(new("parameters",2,"messagepack"),MessagePackSections.UpgradeCurveParameters(input["parameters"].Bytes))]);
        RegisterStep("whole-region-parameter-targets",[new("parameters",2,"messagepack")],[CurrentFormats["parameters"]],input=>
            [new(CurrentFormats["parameters"],MessagePackSections.UpgradeRegionParameters(input["parameters"].Bytes))]);
        RegisterStep("extended-datums",[new("datums",1,"messagepack")],[CurrentFormats["datums"]],input=>
            [new(CurrentFormats["datums"],MessagePackSections.UpgradeExtendedDatums(input["datums"].Bytes))]);
        RegisterStep("drawing-standards-detail-and-tolerance",[new("drawings",1,"messagepack")],
            [new("drawings",2,"messagepack")],input=>
            [new(new("drawings",2,"messagepack"),MessagePackSections.UpgradeDrawingStandards(input["drawings"].Bytes))]);
        RegisterStep("drawing-exact-section-hatching",[new("drawings",2,"messagepack")],
            [new("drawings",3,"messagepack")],input=>
            [new(new("drawings",3,"messagepack"),MessagePackSections.UpgradeSectionHatching(input["drawings"].Bytes))]);
        RegisterStep("drawing-section-hatch-rules",[new("drawings",3,"messagepack")],
            [new("drawings",4,"messagepack")],input=>
            [new(new("drawings",4,"messagepack"),MessagePackSections.UpgradeSectionHatchRules(input["drawings"].Bytes))]);
        RegisterStep("drawing-manufacturing-templates",[new("drawings",4,"messagepack")],
            [new("drawings",5,"messagepack")],input=>
            [new(new("drawings",5,"messagepack"),MessagePackSections.UpgradeManufacturingTemplates(input["drawings"].Bytes))]);
        RegisterStep("drawing-associated-annotations",[new("drawings",5,"messagepack")],
            [new("drawings",6,"messagepack")],input=>
            [new(new("drawings",6,"messagepack"),MessagePackSections.UpgradeDrawingAnnotations(input["drawings"].Bytes))]);
        RegisterStep("drawing-template-library",[new("drawings",6,"messagepack")],
            [new("drawings",7,"messagepack")],input=>
            [new(new("drawings",7,"messagepack"),MessagePackSections.UpgradeDrawingTemplateLibrary(input["drawings"].Bytes))]);
        RegisterStep("drawing-current-hole-tables",[new("drawings",7,"messagepack")],
            [new("drawings",8,"messagepack")],input=>
            [new(new("drawings",8,"messagepack"),MessagePackSections.UpgradeDrawingHoleTables(input["drawings"].Bytes))]);
        RegisterStep("drawing-associated-hole-coordinates",[new("drawings",8,"messagepack")],
            [new("drawings",9,"messagepack")],input=>
            [new(new("drawings",9,"messagepack"),MessagePackSections.UpgradeDrawingHoleCoordinates(input["drawings"].Bytes))]);
        RegisterStep("drawing-typed-datum-systems",[new("drawings",9,"messagepack")],
            [new("drawings",10,"messagepack")],input=>
            [new(new("drawings",10,"messagepack"),MessagePackSections.UpgradeDrawingDatumSystems(input["drawings"].Bytes))]);
        RegisterStep("drawing-original-attachment-history",[new("drawings",10,"messagepack")],
            [new("drawings",11,"messagepack")],input=>
            [new(new("drawings",11,"messagepack"),MessagePackSections.UpgradeDrawingHistoryOrigins(input["drawings"].Bytes))]);
        RegisterStep("drawing-planar-size-features",[new("drawings",11,"messagepack")],
            [new("drawings",12,"messagepack")],input=>
            [new(new("drawings",12,"messagepack"),MessagePackSections.UpgradeDrawingPlanarSizes(input["drawings"].Bytes))]);
        RegisterStep("drawing-thread-hole-proof",[new("drawings",12,"messagepack")],
            [new("drawings",13,"messagepack")],input=>
            [new(new("drawings",13,"messagepack"),MessagePackSections.UpgradeThreadHoleDrawings(input["drawings"].Bytes))]);
        RegisterStep("drawing-modeled-thread-proof",[new("drawings",13,"messagepack")],
            [CurrentFormats["drawings"]],input=>
            [new(CurrentFormats["drawings"],MessagePackSections.UpgradeModeledThreadDrawings(input["drawings"].Bytes))]);
        RegisterStep("feature-stale-state",[new("features",5,"messagepack")],[new("features",6,"messagepack")],input=>
            [new(new("features",6,"messagepack"),input["features"].Bytes)]);
        RegisterStep("local-chamfer-two-distances",[new("features",6,"messagepack")],[new("features",7,"messagepack")],input=>
            [new(new("features",7,"messagepack"),MessagePackSections.UpgradeLocalChamferTwoDistances(input["features"].Bytes))]);
        RegisterStep("local-multi-edge-and-variable-radius",[new("features",7,"messagepack")],[new("features",8,"messagepack")],input=>
            [new(new("features",8,"messagepack"),MessagePackSections.UpgradeLocalMultiEdgeAndVariableRadius(input["features"].Bytes))]);
        RegisterStep("bound-variable-radius",[new("features",8,"messagepack")],[new("features",9,"messagepack")],input=>
            [new(new("features",9,"messagepack"),MessagePackSections.UpgradeBoundVariableRadius(input["features"].Bytes))]);
        RegisterStep("bound-local-chamfer",[new("features",9,"messagepack")],[new("features",10,"messagepack")],input=>
            [new(new("features",10,"messagepack"),input["features"].Bytes)]);
        RegisterStep("circular-sketch-profiles",[new("features",10,"messagepack")],[new("features",11,"messagepack")],input=>
            [new(new("features",11,"messagepack"),MessagePackSections.UpgradeCircularSketchProfiles(input["features"].Bytes))]);
        RegisterStep("circular-sketch-holes",[new("features",11,"messagepack")],[new("features",12,"messagepack")],input=>
            [new(new("features",12,"messagepack"),MessagePackSections.UpgradeCircularHoles(input["features"].Bytes))]);
        RegisterStep("polygon-sketch-holes",[new("features",12,"messagepack")],[new("features",13,"messagepack")],input=>
            [new(new("features",13,"messagepack"),MessagePackSections.UpgradePolygonHoles(input["features"].Bytes))]);
        RegisterStep("arc-segment-features",[new("features",13,"messagepack")],[new("features",14,"messagepack")],input=>
            [new(new("features",14,"messagepack"),MessagePackSections.UpgradeArcSegmentFeatures(input["features"].Bytes))]);
        RegisterStep("mixed-curve-features",[new("features",14,"messagepack")],[new("features",15,"messagepack")],input=>
            [new(new("features",15,"messagepack"),MessagePackSections.UpgradeMixedCurveFeatures(input["features"].Bytes))]);
        RegisterStep("expanded-mixed-features",[new("features",15,"messagepack")],[new("features",16,"messagepack")],input=>
            [new(new("features",16,"messagepack"),MessagePackSections.UpgradeExpandedMixedFeatures(input["features"].Bytes))]);
        RegisterStep("single-level-islands",[new("features",16,"messagepack")],[new("features",17,"messagepack")],input=>
            [new(new("features",17,"messagepack"),MessagePackSections.UpgradeIslandFeatures(input["features"].Bytes))]);
        RegisterStep("quadratic-bezier-features",[new("features",17,"messagepack")],[new("features",18,"messagepack")],input=>
            [new(new("features",18,"messagepack"),MessagePackSections.UpgradeBezierFeatures(input["features"].Bytes))]);
        RegisterStep("cubic-spline-features",[new("features",18,"messagepack")],[new("features",19,"messagepack")],input=>
            [new(new("features",19,"messagepack"),MessagePackSections.UpgradeSplineFeatures(input["features"].Bytes))]);
        RegisterStep("spline-profile-holes",[new("features",19,"messagepack")],[new("features",20,"messagepack")],input=>
            [new(new("features",20,"messagepack"),MessagePackSections.UpgradeSplineHoleFeatures(input["features"].Bytes))]);
        RegisterStep("feature-suppression",[new("features",20,"messagepack")],[new("features",21,"messagepack")],input=>
            [new(new("features",21,"messagepack"),MessagePackSections.UpgradeFeatureSuppression(input["features"].Bytes))]);
        RegisterStep("analytical-revolve",[new("features",21,"messagepack")],[new("features",22,"messagepack")],input=>
            [new(new("features",22,"messagepack"),MessagePackSections.UpgradeAnalyticalRevolve(input["features"].Bytes))]);
        RegisterStep("result-repetition",[new("features",22,"messagepack")],[new("features",23,"messagepack")],input=>
            [new(new("features",23,"messagepack"),MessagePackSections.UpgradeRepetition(input["features"].Bytes))]);
        RegisterStep("standard-holes",[new("features",23,"messagepack")],[new("features",24,"messagepack")],input=>
            [new(new("features",24,"messagepack"),MessagePackSections.UpgradeHoles(input["features"].Bytes))]);
        RegisterStep("advanced-extrusion",[new("features",24,"messagepack")],[new("features",25,"messagepack")],input=>
            [new(new("features",25,"messagepack"),MessagePackSections.UpgradeAdvancedExtrusion(input["features"].Bytes))]);
        RegisterStep("guided-authoring",[new("features",25,"messagepack")],[new("features",26,"messagepack")],input=>
            [new(new("features",26,"messagepack"),MessagePackSections.UpgradeGuidedAuthoring(input["features"].Bytes))]);
        RegisterStep("face-features",[new("features",26,"messagepack")],[new("features",27,"messagepack")],input=>
            [new(new("features",27,"messagepack"),MessagePackSections.UpgradeFaceFeatures(input["features"].Bytes))]);
        RegisterStep("revolve-axis",[new("features",27,"messagepack")],[new("features",28,"messagepack")],input=>
            [new(new("features",28,"messagepack"),MessagePackSections.UpgradeRevolveAxes(input["features"].Bytes))]);
        RegisterStep("hole-replay",[new("features",28,"messagepack")],[new("features",29,"messagepack")],input=>
            [new(new("features",29,"messagepack"),MessagePackSections.UpgradeHolePatterns(input["features"].Bytes))]);
        RegisterStep("extrusion-replay",[new("features",29,"messagepack")],[new("features",30,"messagepack")],input=>
            [new(new("features",30,"messagepack"),MessagePackSections.UpgradeExtrusionPatterns(input["features"].Bytes))]);
        RegisterStep("curved-region-revolve",[new("features",30,"messagepack")],[new("features",31,"messagepack")],input=>
            [new(new("features",31,"messagepack"),MessagePackSections.UpgradeCurvedRevolves(input["features"].Bytes))]);
        RegisterStep("exact-author-curve-features",[new("features",31,"messagepack")],[new("features",32,"messagepack")],input=>
            [new(new("features",32,"messagepack"),MessagePackSections.UpgradeExactCurveFeatures(input["features"].Bytes))]);
        RegisterStep("exact-ellipse-features",[new("features",32,"messagepack")],[new("features",33,"messagepack")],input=>
            [new(new("features",33,"messagepack"),MessagePackSections.UpgradeExactCurveFeatures(input["features"].Bytes,true))]);
        RegisterStep("exact-freeform-features",[new("features",33,"messagepack")],[new("features",34,"messagepack")],input=>
            [new(new("features",34,"messagepack"),MessagePackSections.UpgradeExactCurveFeatures(input["features"].Bytes,freeformsOnly:true))]);
        RegisterStep("periodic-native-feature-intervals",[new("features",34,"messagepack")],[new("features",35,"messagepack")],input=>
            [new(new("features",35,"messagepack"),MessagePackSections.UpgradeExactCurveFeatures(input["features"].Bytes,periodicIntervalsOnly:true))]);
        RegisterStep("solid-offset-features",[new("features",35,"messagepack")],[new("features",36,"messagepack")],input=>
            [new(new("features",36,"messagepack"),MessagePackSections.UpgradeSolidOffsetFeatures(input["features"].Bytes))]);
        RegisterStep("contour-local-features",[new("features",36,"messagepack")],[new("features",37,"messagepack")],input=>
            [new(new("features",37,"messagepack"),MessagePackSections.UpgradeContourLocalFeatures(input["features"].Bytes))]);
        RegisterStep("face-edit-features",[new("features",37,"messagepack")],[new("features",38,"messagepack")],input=>
            [new(new("features",38,"messagepack"),MessagePackSections.UpgradeFaceEditFeatures(input["features"].Bytes))]);
        RegisterStep("multicopy-presentation-features",[new("features",38,"messagepack")],[new("features",39,"messagepack")],input=>
            [new(new("features",39,"messagepack"),MessagePackSections.UpgradeRepetitionPresentation(input["features"].Bytes))]);
        RegisterStep("thread-hole-intent-features",[new("features",39,"messagepack")],[new("features",40,"messagepack")],input=>
            [new(new("features",40,"messagepack"),MessagePackSections.UpgradeThreadHoleFeatures(input["features"].Bytes))]);
        RegisterStep("modeled-thread-features",[new("features",40,"messagepack")],[new("features",41,"messagepack")],input=>
            [new(new("features",41,"messagepack"),MessagePackSections.UpgradeModeledThreadFeatures(input["features"].Bytes))]);
        RegisterStep("finite-surface-extrusion",[new("features",41,"messagepack")],[new("features",42,"messagepack")],input=>
            [new(new("features",42,"messagepack"),MessagePackSections.UpgradeFiniteSurfaceExtrusion(input["features"].Bytes))]);
        RegisterStep("exact-expression-profile-features",[new("features",42,"messagepack")],[CurrentFormats["features"]],input=>
            [new(CurrentFormats["features"],MessagePackSections.UpgradeExpressionProfileFeatures(input["features"].Bytes))]);
        RegisterStep("exact-local-bindings",[new("feature-bindings",1,"messagepack")],[CurrentFormats["feature-bindings"]],input=>
            [new(CurrentFormats["feature-bindings"],MessagePackSections.UpgradeExactFeatureBindings(input["feature-bindings"].Bytes))]);
        RegisterStep("history-source-arguments",[new("history",1,"messagepack")],[new("history",2,"messagepack")],input=>
            [new(new("history",2,"messagepack"),MessagePackSections.UpgradeHistorySources(input["history"].Bytes))]);
        RegisterStep("composed-drilling-history",[new("history",2,"messagepack")],[new("history",3,"messagepack")],input=>
            [new(new("history",3,"messagepack"),MessagePackSections.UpgradeComposedHistory(input["history"].Bytes))]);
        RegisterStep("multicopy-history",[new("history",3,"messagepack")],[CurrentFormats["history"]],input=>
            [new(CurrentFormats["history"],MessagePackSections.UpgradeMulticopyHistory(input["history"].Bytes))]);
        RegisterStep("sketch-revisions",[new("sketches",1,"messagepack")],[new("sketches",2,"messagepack")],input=>
            [new(new("sketches",2,"messagepack"),MessagePackSections.UpgradeSketchRevisions(input["sketches"].Bytes))]);
        RegisterStep("sketch-angular-constraints",[new("sketches",2,"messagepack")],[new("sketches",3,"messagepack")],input=>
            [new(new("sketches",3,"messagepack"),MessagePackSections.UpgradeAngularSketchConstraints(input["sketches"].Bytes))]);
        RegisterStep("three-point-sketch-arcs",[new("sketches",3,"messagepack")],[new("sketches",4,"messagepack")],input=>
            [new(new("sketches",4,"messagepack"),MessagePackSections.UpgradeSketchArcs(input["sketches"].Bytes))]);
        RegisterStep("quadratic-sketch-beziers",[new("sketches",4,"messagepack")],[new("sketches",5,"messagepack")],input=>
            [new(new("sketches",5,"messagepack"),MessagePackSections.UpgradeSketchBeziers(input["sketches"].Bytes))]);
        RegisterStep("cubic-sketch-splines",[new("sketches",5,"messagepack")],[new("sketches",6,"messagepack")],input=>
            [new(new("sketches",6,"messagepack"),MessagePackSections.UpgradeSketchSplines(input["sketches"].Bytes))]);
        RegisterStep("linked-sketch-projections",[new("sketches",6,"messagepack")],[new("sketches",7,"messagepack")],input=>
            [new(new("sketches",7,"messagepack"),MessagePackSections.UpgradeSketchProjections(input["sketches"].Bytes))]);
        RegisterStep("sketch-basic-relations",[new("sketches",7,"messagepack")],[new("sketches",8,"messagepack")],input=>
            [new(new("sketches",8,"messagepack"),MessagePackSections.UpgradeSketchBasicRelations(input["sketches"].Bytes))]);
        RegisterStep("sketch-endpoint-tangency",[new("sketches",8,"messagepack")],[new("sketches",9,"messagepack")],input=>
            [new(new("sketches",9,"messagepack"),MessagePackSections.UpgradeSketchEndpointTangency(input["sketches"].Bytes))]);
        RegisterStep("sketch-parametric-tangency",[new("sketches",9,"messagepack")],[new("sketches",10,"messagepack")],input=>
            [new(new("sketches",10,"messagepack"),MessagePackSections.UpgradeSketchParametricTangency(input["sketches"].Bytes))]);
        RegisterStep("sketch-arc-radius",[new("sketches",10,"messagepack")],[new("sketches",11,"messagepack")],input=>
            [new(new("sketches",11,"messagepack"),MessagePackSections.UpgradeSketchArcRadius(input["sketches"].Bytes))]);
        RegisterStep("exact-author-curve-sketches",[new("sketches",11,"messagepack")],[new("sketches",12,"messagepack")],input=>
            [new(new("sketches",12,"messagepack"),MessagePackSections.UpgradeExactCurveSketches(input["sketches"].Bytes))]);
        RegisterStep("exact-ellipse-sketches",[new("sketches",12,"messagepack")],[new("sketches",13,"messagepack")],input=>
            [new(new("sketches",13,"messagepack"),MessagePackSections.UpgradeEllipseSketches(input["sketches"].Bytes))]);
        RegisterStep("exact-freeform-sketches",[new("sketches",13,"messagepack")],[new("sketches",14,"messagepack")],input=>
            [new(new("sketches",14,"messagepack"),MessagePackSections.UpgradeFreeformSketches(input["sketches"].Bytes))]);
        RegisterStep("exact-endpoint-sketches",[new("sketches",14,"messagepack")],[new("sketches",15,"messagepack")],input=>
            [new(new("sketches",15,"messagepack"),MessagePackSections.UpgradeExactEndpointSketches(input["sketches"].Bytes))]);
        RegisterStep("periodic-native-sketch-intervals",[new("sketches",15,"messagepack")],[new("sketches",16,"messagepack")],input=>
            [new(new("sketches",16,"messagepack"),MessagePackSections.UpgradePeriodicSketchIntervals(input["sketches"].Bytes))]);
        RegisterStep("circular-contact-sketches",[new("sketches",16,"messagepack")],[new("sketches",17,"messagepack")],input=>
            [new(new("sketches",17,"messagepack"),MessagePackSections.UpgradeSketchCircularContact(input["sketches"].Bytes))]);
        RegisterStep("endpoint-curvature-sketches",[new("sketches",17,"messagepack")],[new("sketches",18,"messagepack")],input=>
            [new(new("sketches",18,"messagepack"),MessagePackSections.UpgradeSketchEndpointCurvature(input["sketches"].Bytes))]);
        RegisterStep("finite-parametric-contact-sketches",[new("sketches",18,"messagepack")],[new("sketches",19,"messagepack")],input=>
            [new(new("sketches",19,"messagepack"),MessagePackSections.UpgradeSketchFiniteContact(input["sketches"].Bytes))]);
        RegisterStep("mixed-finite-continuity-sketches",[new("sketches",19,"messagepack")],[new("sketches",20,"messagepack")],input=>
            [new(new("sketches",20,"messagepack"),MessagePackSections.UpgradeSketchMixedFiniteContinuity(input["sketches"].Bytes))]);
        RegisterStep("sliding-contact-parameter-sketches",[new("sketches",20,"messagepack")],[new("sketches",21,"messagepack")],input=>
            [new(new("sketches",21,"messagepack"),MessagePackSections.UpgradeSketchSlidingContact(input["sketches"].Bytes))]);
        RegisterStep("sketch-point-origins",[new("sketches",21,"messagepack")],[CurrentFormats["sketches"]],input=>
            [new(CurrentFormats["sketches"],MessagePackSections.UpgradeSketchPointOrigins(input["sketches"].Bytes))]);
        RegisterStep("sketch-feature-references",[new("features",3,"messagepack")],[new("features",4,"messagepack")],input=>
            [new(new("features",4,"messagepack"),MessagePackSections.UpgradeSketchFeatureReferences(input["features"].Bytes))]);
        RegisterStep("local-box-edge-recipes",[new("features",4,"messagepack")],[new("features",6,"messagepack")],input=>
            [new(new("features",6,"messagepack"),MessagePackSections.UpgradeLocalFeatures(input["features"].Bytes))]);
    }
    private static T Json<T>(SectionPayload input)=>JsonSerializer.Deserialize<T>(input.Bytes.Span,CadJson.Options)??throw new InvalidDataException("Null JSON section.");

    public void RegisterStep(string name,IEnumerable<SectionFormat> inputs,IEnumerable<SectionFormat> outputs,
        Func<IReadOnlyDictionary<string,SectionPayload>,IReadOnlyList<SectionPayload>> migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);ArgumentNullException.ThrowIfNull(migrate);
        var from=inputs.ToImmutableArray();var to=outputs.ToImmutableArray();
        if(from.IsEmpty||to.IsEmpty||from.Select(f=>f.Kind).Distinct().Count()!=from.Length||to.Select(f=>f.Kind).Distinct().Count()!=to.Length||
            from.Concat(to).Any(f=>string.IsNullOrWhiteSpace(f.Kind)||f.Version<1||string.IsNullOrWhiteSpace(f.Encoding)))
            throw new ArgumentException("Invalid migration contracts.");
        foreach(var target in to)
            if(from.FirstOrDefault(f=>f.Kind==target.Kind) is {Kind:not null} source&&target.Version<=source.Version)
                throw new ArgumentException("A migrated section must advance its schema version.");
        lock(gate)
        {
            if(sectionMigrations.Any(m=>m.Name==name||m.Inputs.Intersect(from).Any()))
                throw new ArgumentException("Ambiguous or duplicate migration source contract.");
            sectionMigrations.Add(new(name,from,to,migrate));
        }
    }

    public IReadOnlyDictionary<string,SectionPayload> Migrate(IEnumerable<SectionPayload> sections,
        IReadOnlyDictionary<string,SectionFormat> targets,int maxSectionBytes=32*1024*1024,long maxTotalBytes=128L*1024*1024,
        CancellationToken cancellationToken=default)
    {
        Migration[] rules;lock(gate)rules=sectionMigrations.ToArray();
        var values=sections.ToImmutableDictionary(p=>p.Format.Kind,StringComparer.Ordinal);
        ValidateBudget(values.Values);
        for(int iteration=0;iteration<=MaximumMigrationSteps;iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(targets.All(t=>values.TryGetValue(t.Key,out var value)&&value.Format==t.Value))return values;
            if(iteration==MaximumMigrationSteps)break;
            var applicable=rules.Where(m=>m.Inputs.All(f=>values.TryGetValue(f.Kind,out var p)&&p.Format==f)).ToArray();
            if(applicable.Length==0)throw new NotSupportedException("Missing migration path for: "+string.Join(", ",targets.Values.Where(t=>!values.TryGetValue(t.Kind,out var p)||p.Format!=t)));
            var rule=applicable[0];
            if(rule.Outputs.Any(f=>values.ContainsKey(f.Kind)&&!rule.Inputs.Any(i=>i.Kind==f.Kind)))
                throw new InvalidDataException("Migration output collides with an existing section.");
            var input=rule.Inputs.ToImmutableDictionary(f=>f.Kind,f=>values[f.Kind],StringComparer.Ordinal);
            var result=rule.Apply(input)??throw new InvalidDataException("Migration returned null.");
            cancellationToken.ThrowIfCancellationRequested();
            if(result.Count!=rule.Outputs.Length||!result.Select(p=>p.Format).ToHashSet().SetEquals(rule.Outputs))
                throw new InvalidDataException("Migration violated its output contract: "+rule.Name);
            var staged=values.RemoveRange(rule.Inputs.Select(f=>f.Kind)).AddRange(result.Select(p=>KeyValuePair.Create(p.Format.Kind,p)));
            ValidateBudget(staged.Values);values=staged;
        }
        throw new NotSupportedException("Migration step limit exceeded.");
        void ValidateBudget(IEnumerable<SectionPayload> payloads)
        {
            long total=0;
            foreach(var p in payloads)
            {
                if(p.Bytes.Length>maxSectionBytes)throw new InvalidDataException("Migrated section exceeds its limit.");
                total=checked(total+p.Bytes.Length);if(total>maxTotalBytes)throw new InvalidDataException("Migrated sections exceed their total limit.");
            }
        }
    }
    private sealed record Migration(string Name,ImmutableArray<SectionFormat> Inputs,ImmutableArray<SectionFormat> Outputs,
        Func<IReadOnlyDictionary<string,SectionPayload>,IReadOnlyList<SectionPayload>> Apply);
}
