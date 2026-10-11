using Cadoryx.Db;

namespace Cadoryx.Commands;

public static class DrawingTemplateCommands
{
    public static ICadDocumentCommand Import(DrawingTemplateDefinition entry)=>new EditDocumentCommand("Import drawing template",document=>
    {
        entry.Validate();var detached=entry with{Id=Guid.NewGuid()};
        return document with{DrawingTemplates=document.DrawingTemplates.Add(detached.Id,detached)};
    });
    public static ICadDocumentCommand Save(Guid sheet,Guid id,string name,bool replace=false)=>
        new EditDocumentCommand("Save drawing template",document=>
        {
            if(document.DrawingTemplates.ContainsKey(id)!=replace)
                throw new CadValidationException("Explicitly select an existing template to replace it, or save with a new ID.");
            var source=document.DrawingSheets.GetValueOrDefault(sheet)??throw new CadValidationException("Drawing sheet is missing.");
            var entry=DrawingTemplateDefinition.Capture(source,id,name?.Trim()??"");entry.Validate();
            return document with{DrawingTemplates=document.DrawingTemplates.SetItem(id,entry)};
        });
    public static ICadDocumentCommand Apply(Guid sheet,Guid template)=>new EditDocumentCommand("Apply drawing template",document=>
    {
        var entry=document.DrawingTemplates.GetValueOrDefault(template)??throw new CadValidationException("Drawing template is missing.");
        var current=document.DrawingSheets.GetValueOrDefault(sheet)??throw new CadValidationException("Drawing sheet is missing.");
        entry.Validate();var updated=entry.Apply(current);updated.Validate(document.Id);
        return document with{DrawingSheets=document.DrawingSheets.SetItem(sheet,updated)};
    });
    public static ICadDocumentCommand Delete(Guid template)=>new EditDocumentCommand("Delete drawing template",document=>
    {
        if(!document.DrawingTemplates.ContainsKey(template))throw new CadValidationException("Drawing template is missing.");
        return document with{DrawingTemplates=document.DrawingTemplates.Remove(template)};
    });
    public static ICadDocumentCommand PutField(Guid sheet,DrawingCustomField field,bool replace=false)=>
        Edit(sheet,"Edit custom drawing field",template=>
        {
            if(template.CustomFields.Any(f=>f.Id==field.Id)!=replace)throw new CadValidationException("Explicit field replacement is required.");
            return template with{CustomFields=[..template.CustomFields.Where(f=>f.Id!=field.Id),field]};
        });
    public static ICadDocumentCommand PutNote(Guid sheet,DrawingManufacturingNote note,bool replace=false)=>
        Edit(sheet,"Edit manufacturing note",template=>
        {
            if(template.Notes.Any(n=>n.Id==note.Id)!=replace)throw new CadValidationException("Explicit note replacement is required.");
            return template with{Notes=[..template.Notes.Where(n=>n.Id!=note.Id),note]};
        });
    public static ICadDocumentCommand DeleteContent(Guid sheet,Guid id)=>Edit(sheet,"Delete custom sheet content",template=>
    {
        if(!template.CustomFields.Any(f=>f.Id==id)&&!template.Notes.Any(n=>n.Id==id))throw new CadValidationException("Sheet content is missing.");
        return template with{CustomFields=[..template.CustomFields.Where(f=>f.Id!=id)],Notes=[..template.Notes.Where(n=>n.Id!=id)]};
    });
    public static ICadDocumentCommand SetZones(Guid sheet,DrawingFrameZones? zones)=>
        Edit(sheet,"Edit drawing frame zones",template=>template with{Zones=zones});
    private static ICadDocumentCommand Edit(Guid sheet,string name,Func<DrawingSheetTemplate,DrawingSheetTemplate> edit)=>
        new EditDocumentCommand(name,document=>
        {
            var current=document.DrawingSheets.GetValueOrDefault(sheet)??throw new CadValidationException("Drawing sheet is missing.");
            var template=current.Template??throw new CadValidationException("Sheet content requires a manufacturing frame.");
            var updated=current with{Template=edit(template)};updated.Validate(document.Id);
            return document with{DrawingSheets=document.DrawingSheets.SetItem(sheet,updated)};
        });
}
