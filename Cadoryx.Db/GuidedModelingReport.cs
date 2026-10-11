using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadoryx.Db;

public enum GuidedModelingOperation { Prism=0, CurvePreparation=1, Sweep=2, Loft=3, BooleanCut=4, Compound=5 }

/// <summary>Copied observations of one actual native call, not a geometric error
/// certificate or a source-to-result topology identity map.</summary>
public sealed record GuidedModelingStage(GuidedModelingOperation Operation,string Scope,
    Guid? Channel,int? Station,ImmutableArray<string> InputBrepSha256,string OutputBrepSha256,
    double? RequestedTolerance,double? RequestedBoundaryTolerance,double? RequestedAngularTolerance,int? MaximumSegments,int? MaximumDegree,
    double? NativeApproximationErrorEstimate,bool? NativeReady,bool? NativeDone,
    bool? NativeValid,int? NativeStatus,int? NativeContinuityLimit,string? NativeMessage,string? NativeOptionsJson)
{
    public void Validate()
    {
        if(!Enum.IsDefined(Operation)||string.IsNullOrWhiteSpace(Scope)||Scope.Length>256||
            Channel==Guid.Empty||Station is <0 or >127||InputBrepSha256.IsDefaultOrEmpty||InputBrepSha256.Length>128||
            InputBrepSha256.Any(h=>!GuidedModelingReport.IsHash(h))||!GuidedModelingReport.IsHash(OutputBrepSha256)||
            NativeMessage is {Length:>16384}||NativeOptionsJson is {Length:>65536})
            throw new CadValidationException("Invalid guided modeling stage identity, hashes or copied message.");
        if(RequestedTolerance is {} tolerance){CadGuard.Positive(tolerance);}
        if(RequestedBoundaryTolerance is {} boundary){CadGuard.Positive(boundary);}
        if(RequestedAngularTolerance is {} angular){CadGuard.Positive(angular);}
        if(MaximumSegments is <1 or >512||MaximumDegree is <2 or >25)
            throw new CadValidationException("Invalid recorded native approximation controls.");
        if(NativeApproximationErrorEstimate is {} error)
        {CadGuard.Finite(error);if(error<0)throw new CadValidationException("A native approximation error estimate cannot be negative.");}
        if(Operation is GuidedModelingOperation.Prism or GuidedModelingOperation.BooleanCut or GuidedModelingOperation.Compound)
        {
            if(RequestedTolerance is not null||RequestedBoundaryTolerance is not null||RequestedAngularTolerance is not null||MaximumSegments is not null||MaximumDegree is not null||
                NativeApproximationErrorEstimate is not null||NativeReady is not null||NativeDone is not null||NativeValid is not null||NativeStatus is not null||NativeContinuityLimit is not null||NativeMessage is not null||NativeOptionsJson is not null)
                throw new CadValidationException("This operation has no native guided approximation report.");
        }
        else
        {
            if(RequestedTolerance is null||NativeReady is null||NativeDone!=true||NativeValid!=true||NativeStatus is null||NativeMessage is null||NativeOptionsJson is null)
                throw new CadValidationException("A recorded guided native call must be accepted and retain its controls.");
            if(Operation==GuidedModelingOperation.CurvePreparation&&
                (MaximumSegments is null||MaximumDegree is null||NativeApproximationErrorEstimate is null||NativeApproximationErrorEstimate>RequestedTolerance))
                throw new CadValidationException("A recorded curve preparation lacks its bounded native estimate.");
            if(Operation==GuidedModelingOperation.Sweep&&
                (NativeApproximationErrorEstimate is null||NativeApproximationErrorEstimate>1e-7||
                    RequestedBoundaryTolerance is null||RequestedAngularTolerance is null||MaximumSegments is null||MaximumDegree is null))
                throw new CadValidationException("A recorded sweep lacks its accepted native surface estimate.");
            // The loft adapter may expose no estimate. Requested tolerance is
            // never substituted for an observed error or a stored zero.
            try
            {
                using var json=JsonDocument.Parse(NativeOptionsJson);var root=json.RootElement;
                if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()>64||
                    root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=root.EnumerateObject().Count())
                    throw new CadValidationException("Invalid copied native option object.");
                string toleranceName=Operation==GuidedModelingOperation.Sweep?"tolerance3d":"tolerance";
                if(!root.TryGetProperty(toleranceName,out var t)||t.ValueKind!=JsonValueKind.Number||!t.TryGetDouble(out double requested)||requested!=RequestedTolerance)
                    throw new CadValidationException("Copied native tolerance differs from the recorded call controls.");
                if(MaximumDegree is {} degree&&(!root.TryGetProperty("maximumDegree",out var d)||d.ValueKind!=JsonValueKind.Number||!d.TryGetInt32(out int copiedDegree)||copiedDegree!=degree))
                    throw new CadValidationException("Copied native degree differs from the recorded call controls.");
                if(MaximumSegments is {} segments&&(!root.TryGetProperty("maximumSegments",out var s)||s.ValueKind!=JsonValueKind.Number||!s.TryGetInt32(out int copiedSegments)||copiedSegments!=segments))
                    throw new CadValidationException("Copied native segment count differs from the recorded call controls.");
                if(Operation==GuidedModelingOperation.Sweep&&
                    (!root.TryGetProperty("toleranceBoundary",out var b)||b.ValueKind!=JsonValueKind.Number||!b.TryGetDouble(out double copiedBoundary)||copiedBoundary!=RequestedBoundaryTolerance||
                     !root.TryGetProperty("toleranceAngular",out var a)||a.ValueKind!=JsonValueKind.Number||!a.TryGetDouble(out double copiedAngular)||copiedAngular!=RequestedAngularTolerance))
                    throw new CadValidationException("Copied native sweep tolerances differ from the recorded call controls.");
            }
            catch(JsonException){throw new CadValidationException("Copied native options are not valid JSON data.");}
        }
    }
}

/// <summary>Historical observations tied to one generated geometry revision.
/// BuiltRecipe is the immutable author snapshot used for that evaluation; it need
/// not be the current live author graph after explicit freezing or cloning.</summary>
public sealed record GuidedModelingReport(int SchemaVersion,string AdapterVersion,
    string SdkVersion,string ManagedRuntimeVersion,string NativeBridgeVersion,string NativeAbiVersion,string OcctVersion,
    GeometryRecipe BuiltRecipe,string BuiltRecipeSha256,GeometryRevisionId ResultRevision,AssetId ResultAsset,
    ImmutableArray<GuidedModelingStage> Stages)
{
    public const string CurrentAdapterVersion="Cadoryx guided-modeling-report.1";
    public const int MaximumStages=16384;
    public static JsonSerializerOptions JsonOptions {get;}=CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,MaxDepth=128,
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver:true);return options;
    }
    public static bool IsHash(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string RecipeHash(GeometryRecipe recipe)=>Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(recipe,JsonOptions))));
    public void ValidateFor(GeometryAssetRef result)
    {
        if(SchemaVersion!=1||AdapterVersion!=CurrentAdapterVersion||BuiltRecipe is not (SweepRecipe or LoftRecipe)||
            !IsHash(BuiltRecipeSha256)||ResultRevision!=result.Revision||ResultAsset!=result.AssetId||
            Stages.IsDefaultOrEmpty||Stages.Length>MaximumStages||Stages.Any(stage=>stage is null))
            throw new CadValidationException("Invalid guided modeling report or generated-result context.");
        foreach(string version in new[]{SdkVersion,ManagedRuntimeVersion,NativeBridgeVersion,NativeAbiVersion,OcctVersion})
            if(string.IsNullOrWhiteSpace(version)||version.Length>256)throw new CadValidationException("Missing observed guided-modeling runtime version.");
        BuiltRecipe.Validate();
        if(RecipeHash(BuiltRecipe)!=BuiltRecipeSha256)throw new CadValidationException("Guided modeling author snapshot differs from its recorded hash.");
        foreach(var stage in Stages)stage.Validate();
        if(!Stages.Any(s=>s.Operation is GuidedModelingOperation.Prism or GuidedModelingOperation.Sweep or GuidedModelingOperation.Loft))
            throw new CadValidationException("A guided modeling report has no actual result-building stage.");
    }
}
