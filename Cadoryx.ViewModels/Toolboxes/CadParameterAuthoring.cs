using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadParameterAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document, DocumentCommandExpectation expectation,
        CadParameterEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (document.IsDetached || document.IsClosingRequested || document.IsReadOnly)
            throw new InvalidOperationException("The active document cannot be edited.");
        var snapshot = document.Session.Snapshot;
        expectation.Validate(snapshot);
        var book = snapshot.Parameters;
        Guid? resultId = request.Id;
        var created = false;
        switch (request.Action)
        {
            case "set_expression":
            case "set_reference":
                if (request.ParameterId is not null || request.Name is null || !ParameterExpressions.IsName(request.Name) ||
                    request.Dimension is null || request.Action == "set_expression" && (request.Target is not null || request.Expression is not { Length: > 0 and <= 2048 }) ||
                    request.Action == "set_reference" && (request.Expression is not null || request.Target is null))
                    throw new ArgumentException("Invalid parameter author fields.");
                var dimension = ExactEnum<ParameterDimension>(request.Dimension);
                created = request.Id is null;
                resultId = request.Id ?? Guid.NewGuid();
                if (resultId == Guid.Empty) throw new ArgumentException("Empty parameter identity.");
                var target = request.Target is null ? null : Target(request.Target);
                if (target is not null) _ = ParameterTargets.Read(snapshot, target);
                book = ParameterBookEdits.Set(book, new(resultId.Value, request.Name, dimension, request.Expression ?? "", target), created);
                break;
            case "delete":
            case "unbind":
                RequireOnlyId(request);
                book = request.Action == "delete" ? ParameterBookEdits.Delete(book, request.Id!.Value) : ParameterBookEdits.Unbind(book, request.Id!.Value);
                break;
            case "bind":
                if (request.Id is not null || request.Name is not null || request.Dimension is not null || request.Expression is not null ||
                    request.ParameterId is null || request.ParameterId == Guid.Empty || request.Target is null)
                    throw new ArgumentException("A binding requires exactly parameterId and target.");
                book = ParameterBookEdits.Bind(snapshot, request.ParameterId.Value, Target(request.Target), out var bindingId, out created);
                resultId = bindingId;
                break;
            case "recompute":
                if (request.Id is not null || request.Name is not null || request.Dimension is not null || request.Expression is not null ||
                    request.ParameterId is not null || request.Target is not null)
                    throw new ArgumentException("Recompute accepts no author fields.");
                break;
            default: throw new ArgumentException("Unknown parameter action.");
        }
        await document.Session.ExecuteAsync(new SetParametersCommand(book), token, expectation);
        var after = document.Session.Snapshot;
        return JsonSerializer.Serialize(new { success = true, action = request.Action, id = resultId?.ToString("D"),
            changed = after.StateId != snapshot.StateId, createdIds = created && after.StateId != snapshot.StateId ? new[] { resultId!.Value.ToString("D") } : [],
            documentId = after.Id.ToString(), stateId = after.StateId.ToString() });
    }

    internal static ParameterTarget Target(CadParameterTarget target)
    {
        if (target.Owner == Guid.Empty || target.Property is not { Length: > 0 and <= 64 } ||
            target.Constraint == Guid.Empty || target.Curve == Guid.Empty || target.Index is < 0 or > 100000)
            throw new ArgumentException("Invalid target author identity.");
        var kind = ExactEnum<ParameterTargetKind>(target.Kind);
        if (kind is ParameterTargetKind.Feature or ParameterTargetKind.Datum or ParameterTargetKind.AssemblyPattern &&
            (target.Constraint is not null || target.Curve is not null || target.Index is not null) ||
            kind == ParameterTargetKind.SketchConstraint && (target.Constraint is null || target.Curve is not null || target.Index is not null) ||
            kind == ParameterTargetKind.ExactSketchCurve && (target.Curve is null || target.Constraint is not null) ||
            kind == ParameterTargetKind.SketchRegion && (target.Curve is null || target.Constraint is not null || target.Index is not null || target.Property != "Distance"))
            throw new ArgumentException("Missing or unexpected target companion identity.");
        return new(kind, target.Owner, target.Constraint, target.Property, target.Curve, target.Index);
    }

    private static T ExactEnum<T>(string text) where T : struct, Enum =>
        Enum.GetNames<T>().Contains(text, StringComparer.Ordinal) ? Enum.Parse<T>(text) : throw new ArgumentException("Unknown " + typeof(T).Name + ".");
    private static void RequireOnlyId(CadParameterEditRequest request)
    {
        if (request.Id is null || request.Id == Guid.Empty || request.Name is not null || request.Dimension is not null ||
            request.Expression is not null || request.ParameterId is not null || request.Target is not null)
            throw new ArgumentException("This action requires exactly an ID.");
    }
}
