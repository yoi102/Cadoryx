using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Identity-based edits of the author table. SetParametersCommand validates and recomputes the candidate atomically.</summary>
public static class ParameterBookEdits
{
    public static ParameterBook Set(ParameterBook book, CadParameter value, bool create)
    {
        var index = Index(book.Parameters, p => p.Id == value.Id);
        if (create)
        {
            if (index >= 0) throw new CadValidationException("Parameter identity already exists.");
            return book with { Parameters = book.Parameters.Add(value) };
        }
        if (index < 0) throw new CadValidationException("Exact parameter ID is missing. Inspect parameters again.");
        return book.Parameters[index] == value ? book : book with { Parameters = book.Parameters.SetItem(index, value) };
    }

    public static ParameterBook Delete(ParameterBook book, Guid id)
    {
        var index = Index(book.Parameters, p => p.Id == id);
        if (index < 0) throw new CadValidationException("Exact parameter ID is missing. Inspect parameters again.");
        if (book.Bindings.Any(b => b.Parameter == id))
            throw new CadValidationException("Detach this parameter's driving bindings before deleting it.");
        var name = book.Parameters[index].Name;
        if (book.Parameters.Any(p => p.Id != id && p.Reference is null &&
            ParameterExpressions.Dependencies(p.Expression).Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new CadValidationException("Other expressions depend on this parameter. Edit them before deleting it.");
        return book with { Parameters = book.Parameters.RemoveAt(index) };
    }

    public static ParameterBook Bind(DocumentSnapshot snapshot, Guid parameter, ParameterTarget target, out Guid id, out bool created)
    {
        var book = snapshot.Parameters;
        if (!book.Parameters.Any(p => p.Id == parameter))
            throw new CadValidationException("Exact parameter ID is missing. Inspect parameters again.");
        // All lists writable author fields, including companions and indices, and omits stale or suppressed features.
        if (!ParameterTargets.All(snapshot).Contains(target))
            throw new CadValidationException("Target is not a current writable author field. Inspect parameter_targets again.");
        var old = book.Bindings.SingleOrDefault(b => b.Target == target);
        if (old is not null)
        {
            if (old.Parameter != parameter) throw new CadValidationException("The target already has a driver. Detach its binding first.");
            id = old.Id; created = false; return book;
        }
        id = Guid.NewGuid(); created = true;
        return book with { Bindings = book.Bindings.Add(new(id, parameter, target)) };
    }

    public static ParameterBook Unbind(ParameterBook book, Guid id)
    {
        var index = Index(book.Bindings, b => b.Id == id);
        if (index < 0) throw new CadValidationException("Exact binding ID is missing. Inspect parameter_bindings again.");
        return book with { Bindings = book.Bindings.RemoveAt(index) };
    }

    private static int Index<T>(ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (var i = 0; i < values.Length; i++) if (predicate(values[i])) return i;
        return -1;
    }
}
