namespace Cadoryx.Db;

/// <summary>Resolves a concrete path, rather than guessing an instance from its shared definition.</summary>
public sealed record OccurrencePlacement(DefinitionId OwnerId, ComponentSlot Slot, int OwnerOccurrenceCount)
{
    public bool CanMoveIndependently => OwnerOccurrenceCount == 1;
    public static OccurrencePlacement Resolve(DocumentSnapshot document, OccurrencePath path)
    {
        if (path.DocumentId != document.Id || path.Slots.IsEmpty) throw new CadValidationException("Invalid instance path.");
        var owner = document.RootAssemblyId;
        for (int i = 0; i < path.Slots.Length; i++)
        {
            if (document.Definitions[owner] is not AssemblyDefinition assembly) throw new CadValidationException("Path crosses a part definition.");
            var slot = assembly.Children.SingleOrDefault(s => s.Id == path.Slots[i]) ?? throw new CadValidationException("Instance path no longer exists.");
            if (i == path.Slots.Length - 1)
                return new(owner, slot, owner == document.RootAssemblyId ? 1 : document.EnumerateOccurrences().Count(o => o.DefinitionId == owner));
            owner = slot.DefinitionId;
        }
        throw new CadValidationException("Invalid instance path.");
    }
}
