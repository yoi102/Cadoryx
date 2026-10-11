using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Db;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadModelContract
{
    internal static readonly string[] Types=["extrude","revolve","boolean","hole","repetition","hole_pattern","extrusion_pattern","sweep","loft","shell","draft","solid_offset","planar_push_pull","defeature","contour_local","assembly_pattern"];
    internal static string Read(string type,int offset=0,int limit=1)
    {
        if(offset is <0 or >100000||limit is <1 or >50)throw new ArgumentException("Invalid contract page budget.");
        if(!Types.Contains(type,StringComparer.Ordinal))throw new ArgumentException("Choose one exact model type: "+string.Join(", ",Types));
        var defs=new Dictionary<string,object>();
        object Pure<T>()=>CadModelJson.TypeSchema(typeof(T),defs);
        object Ref(string name)=>new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
        object Number()=>new{type="number",minimum=-1000000,maximum=1000000};
        object Id()=>new{type="string",format="uuid",minLength=36,maxLength=36};
        object Enum<T>() where T:struct,System.Enum=>new Dictionary<string,object>{["type"]="string",["enum"]=System.Enum.GetNames<T>()};
        object Shape(Dictionary<string,object> p,params string[] required)=>new{type="object",properties=p,required,additionalProperties=false};
        object List(object item,int minimum,int maximum)=>new{type="array",items=item,minItems=minimum,maxItems=maximum};
        defs["vector"]=List(Number(),3,3);
        defs["frame"]=Shape(new(){["origin"]=Ref("vector"),["normal"]=Ref("vector"),["u"]=Ref("vector")},"origin","normal","u");
        defs["plane"]=new{oneOf=new[]{Ref("frame"),Shape(new(){["datumId"]=Id(),["revision"]=Id()},"datumId","revision")}};
        defs["input"]=Shape(new(){["featureId"]=Id(),["revision"]=Id()},"featureId","revision");
        defs["selection"]=Shape(new(){["featureId"]=Id(),["revision"]=Id(),["asset"]=new{type="string",pattern="^[0-9a-f]{64}$"},
            ["fingerprint"]=new{type="string",pattern="^[0-9a-fA-F]{64}$"},["index"]=new{type="integer",minimum=0,maximum=100000},
            ["kind"]=new{@const="Face"},["adapterVersion"]=new{type="string",minLength=1,maxLength=128}},"featureId","revision","asset","fingerprint","index","kind","adapterVersion");
        defs["edge_selection"]=Shape(new(){["featureId"]=Id(),["revision"]=Id(),["asset"]=new{type="string",pattern="^[0-9a-f]{64}$"},
            ["fingerprint"]=new{type="string",pattern="^[0-9a-fA-F]{64}$"},["index"]=new{type="integer",minimum=0,maximum=100000},
            ["kind"]=new{@const="Edge"},["adapterVersion"]=new{type="string",minLength=1,maxLength=128}},"featureId","revision","asset","fingerprint","index","kind","adapterVersion");
        defs["constant_radius"]=Shape(new(){["kind"]=new{@const="constant"},["value"]=Number()},"kind","value");
        defs["linear_radius"]=Shape(new(){["kind"]=new{@const="linear"},["first"]=Number(),["last"]=Number()},"kind","first","last");
        defs["smooth_radius"]=Shape(new(){["kind"]=new{@const="smooth"},["first"]=Number(),["last"]=Number(),["firstDerivative"]=Number(),["lastDerivative"]=Number()},"kind","first","last");
        defs["sampled_radius"]=Shape(new(){["kind"]=new{@const="sampled"},["samples"]=List(Pure<ContourRadiusSample>(),2,256)},"kind","samples");
        defs["spline_radius"]=Shape(new(){["kind"]=new{@const="spline"},["poles"]=List(Pure<ContourRadiusPole>(),2,256),["knots"]=List(Number(),2,256),
            ["multiplicities"]=List(new{type="integer",minimum=1,maximum=9},2,256),["degree"]=new{type="integer",minimum=1,maximum=8}},"kind","poles","knots","multiplicities","degree");
        defs["radius_law"]=new{oneOf=new[]{Ref("constant_radius"),Ref("linear_radius"),Ref("smooth_radius"),Ref("sampled_radius"),Ref("spline_radius")}};
        defs["fillet_program"]=Shape(new(){["id"]=Id(),["seed"]=Ref("edge_selection"),["radius"]=Ref("radius_law")},"seed","radius");
        defs["chamfer_program"]=Shape(new(){["id"]=Id(),["seed"]=Ref("edge_selection"),["supportFace"]=Ref("selection"),["distance"]=Number(),["secondDistance"]=Number()},"seed","supportFace","distance");
        defs["section"]=new{oneOf=new[]{Shape(new(){["source"]=Pure<SketchProfileReference>()},"source"),
            Shape(new(){["profile"]=Pure<SketchProfile>(),["frame"]=Ref("frame")},"profile","frame")}};
        defs["layout"]=new{oneOf=new[]{false,true}.Select(associated=>
        {
            var p=new Dictionary<string,object>{["kind"]=Enum<RepetitionKind>(),["count"]=new{type="integer",minimum=1,maximum=256},["step"]=Number(),
                ["copyIds"]=List(Id(),1,256),["output"]=Enum<RepetitionOutput>(),["includeSource"]=new{type="boolean"},["fullCircle"]=new{type="boolean"}};
            var r=new List<string>{"kind","count"};
            if(associated){p["datumId"]=Id();p["datumRevision"]=Id();r.AddRange(["datumId","datumRevision"]);}
            else{p["frame"]=Ref("frame");r.Add("frame");}return Shape(p,r.ToArray());
        }).ToArray()};
        var properties=new Dictionary<string,object>{["type"]=new{@const=type}};var required=new List<string>{"type"};
        void Add(string name,object schema,bool must=false){properties[name]=schema;if(must)required.Add(name);}
        switch(type)
        {
            case "extrude":
                Add("section",Ref("section"),true);Add("distance",Number());Add("operation",Enum<ExtrusionOperation>());Add("side",Enum<ExtrusionSide>());
                Add("backward",Number());Add("termination",Enum<ExtrusionTermination>());Add("input",Ref("input"));Add("limit",Ref("plane"));Add("limitFace",Ref("selection"));
                Add("supportFace",Ref("selection"));Add("surfaceBranch",Enum<ExtrusionSurfaceBranch>());break;
            case "revolve":
                Add("section",Ref("section"),true);Add("angle",Number(),true);
                Add("axis",new{oneOf=new[]{Shape(new(){["point"]=Ref("vector"),["direction"]=Ref("vector")},"point","direction"),
                    Shape(new(){["datumId"]=Id(),["revision"]=Id()},"datumId","revision")}});break;
            case "boolean":Add("operation",Enum<BooleanOperation>(),true);Add("inputs",List(Ref("input"),2,50),true);break;
            case "hole":
                Add("input",Ref("input"),true);Add("plane",Ref("plane"),true);Add("position",Pure<Point2d>(),true);Add("diameter",Number(),true);
                Add("through",new{type="boolean"});Add("depth",Number());Add("head",Enum<HoleHead>());Add("headDiameter",Number());Add("headDepth",Number());Add("headAngle",Number());Add("standard",Pure<MetricClearanceSpec>());Add("thread",Pure<MetricThreadSpec>());break;
            case "repetition":case "hole_pattern":case "extrusion_pattern":Add("input",Ref("input"),true);Add("layout",Ref("layout"),true);break;
            case "sweep":
                Add("section",Ref("section"),true);Add("path",new{oneOf=new[]{Shape(new(){["source"]=Pure<SketchPathReference>()},"source"),
                    Shape(new(){["curves"]=Pure<ImmutableArray<SketchBoundaryCurve>>(),["frame"]=Ref("frame")},"curves","frame")}},true);
                Add("output",Enum<GuidedOutput>());Add("corner",Enum<SweepCorner>());break;
            case "loft":
                Add("stations",List(Ref("frame"),2,128),true);Add("channels",List(Shape(new(){["id"]=Id(),["parent"]=new{anyOf=new[]{Id(),new{type="null"}}},
                    ["sections"]=List(Ref("section"),2,128)},"sections"),1,64),true);Add("output",Enum<GuidedOutput>());Add("ruled",new{type="boolean"});break;
            case "shell":Add("input",Ref("input"),true);Add("faces",List(Ref("selection"),1,256),true);Add("thickness",Number(),true);Add("outward",new{type="boolean"});break;
            case "solid_offset":Add("input",Ref("input"),true);Add("distance",Number(),true);break;
            case "planar_push_pull":Add("input",Ref("input"),true);Add("face",Ref("selection"),true);Add("distance",Number(),true);Add("normal",Ref("vector"));break;
            case "defeature":Add("input",Ref("input"),true);Add("faces",List(Ref("selection"),1,256),true);break;
            case "contour_local":
                Add("input",Ref("input"),true);Add("operation",Enum<LocalFeatureOperation>(),true);
                Add("programs",List(new{oneOf=new[]{Ref("fillet_program"),Ref("chamfer_program")}},1,256),true);break;
            case "draft":
                Add("input",Ref("input"),true);Add("programs",List(Shape(new(){["id"]=Id(),["face"]=Ref("selection"),["angle"]=Number(),["plane"]=Ref("plane"),
                    ["pull"]=Ref("vector"),["propagation"]=Enum<FaceDraftPropagation>()},"face","angle","plane","pull"),1,256),true);break;
            case "assembly_pattern":
                Add("kind",Enum<RepetitionKind>(),true);Add("frame",Ref("frame"));Add("datumId",Id());Add("datumRevision",Id());
                Add("count",new{type="integer",minimum=2,maximum=256},true);Add("step",Number());Add("copySlots",List(Id(),1,255));Add("fullCircle",new{type="boolean"});break;
        }
        // Drop definitions unreachable from this recipe, keeping discovery responses small.
        var root=Shape(properties,required.ToArray());var reachable=new HashSet<string>();
        void Visit(JsonElement node)
        {
            if(node.ValueKind==JsonValueKind.Object)
                foreach(var p in node.EnumerateObject())
                    if(p.Name=="$ref")
                    {var name=p.Value.GetString()!["#/$defs/".Length..];if(reachable.Add(name))Visit(JsonSerializer.SerializeToElement(defs[name]));}
                    else Visit(p.Value);
            else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())Visit(item);
        }
        Visit(JsonSerializer.SerializeToElement(root));
        var names=reachable.Order(StringComparer.Ordinal).ToArray();var selected=names.Skip(offset).Take(limit).ToList();
        string Serialize()
        {
            var schema=JsonSerializer.SerializeToElement(root).EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone());schema["$defs"]=selected.ToDictionary(name=>name,name=>defs[name]);
            return JsonSerializer.Serialize(new{success=true,type,offset,limit,total=names.Length,returned=selected.Count,nextOffset=offset+selected.Count,hasMore=(long)offset+selected.Count<names.Length,
                schemaComplete=offset==0&&selected.Count==names.Length,definitionNames=names,schema,units=new{length="mm",angle="rad"},coordinateSpace=type=="assembly_pattern"?"parent_local":"part_local",
                contract="Merge $defs from successive pages into the same root schema. Every response stays below the normal tool-result limit. References may resolve in other pages. Source branches accept exact IDs/revisions only; production geometry and dependency constraints still apply.",
                defaults="Extrude NewBody/Positive/Distance, backward 0. Hole through=true/Plain, depth/head dimensions 0. Repetition Compound/includeSource=true/step=0/fullCircle=false. Guided Solid/Transformed/ruled=false.",
                constraints="Extrude Fuse/Cut requires input, NewBody forbids it; Plane needs limit, PlanarFace needs limitFace, other companions forbidden. Planar push/pull uses the finite face including holes and signed outward distance; native normal is discovered, optional normal must agree. Defeature requires every selected face removed and one complete healed solid. Loft sections match ordered station frames. Edited channel/program IDs are explicit; omitted copy IDs preserve survivors. Use explicit detach actions."},CadAuthoringInspection.JsonOptions);
        }
        var json=Serialize();while(json.Length>5500&&selected.Count>1){selected.RemoveAt(selected.Count-1);json=Serialize();}
        if(json.Length>5500)throw new NotSupportedException("This author contract definition exceeds the tool-result page budget.");return json;
    }
}
