using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;
public partial class DocumentReviewViewModel
{
    public event EventHandler? CaptureCamerasRequested;
    public event EventHandler? RestoreCamerasRequested;
    [ObservableProperty] private string bookmarkName=R("ReviewBookmark");
    [ObservableProperty] private ReviewBookmark? selectedBookmark;
    public ObservableCollection<ReviewBookmark> BookmarkRows {get;}=[];
    private static ReviewCamera Persist(CadCamera c)=>new(c.Eye,c.Target,c.Up,c.Aspect,c.Scale,c.FieldOfViewY,c.NearPlane,c.FarPlane,c.Perspective,c.AutoFitDepth);
    private static CadCamera Camera(ReviewCamera c)=>new(c.Eye,c.Target,c.Up,c.Aspect,c.Scale,c.FieldOfViewY,c.NearPlane,c.FarPlane,c.Perspective,c.AutoFitDepth);
    private void RefreshBookmarks()
    {
        var id=SelectedBookmark?.Id;BookmarkRows.Clear();
        foreach(var b in document.Session.Snapshot.ReviewBookmarks.Values.OrderBy(b=>b.Name))BookmarkRows.Add(b);
        SelectedBookmark=BookmarkRows.FirstOrDefault(b=>b.Id==id);
    }
    [RelayCommand] private async Task SaveBookmarkAsync()
    {
        try
        {
            CaptureCamerasRequested?.Invoke(this,EventArgs.Empty);
            if(document.Camera is not {} c)throw new CadValidationException("Viewport camera is unavailable.");
            var b=new ReviewBookmark(Guid.NewGuid(),BookmarkName,Persist(c),SecondaryCamera is {} second?Persist(second):null,
                SplitView,Section.Enabled,(int)Section.Axis,Section.OffsetMm,Section.Reverse,Section.SlabThicknessMm,visibility.CaptureHidden(),visibility.CaptureIsolated());
            await ReviewEditAsync(new EditDocumentCommand(R("SaveBookmark"),s=>s with{ReviewBookmarks=s.ReviewBookmarks.Add(b.Id,b)}));
            SelectedBookmark=BookmarkRows.FirstOrDefault(x=>x.Id==b.Id);
        }
        catch(Exception ex){ReviewStatus=ex.Message;document.Report(ex);}
    }
    [RelayCommand] private void RestoreBookmark()
    {
        if(SelectedBookmark is not {} b)return;
        document.Camera=Camera(b.Primary);SecondaryCamera=b.Secondary is {} c?Camera(c):null;SplitView=b.SplitView;
        SectionEnabled=b.SectionEnabled;SectionAxis=(SectionAxis)b.SectionAxis;SectionOffsetMm=b.SectionOffsetMm;SectionReverse=b.SectionReverse;
        SlabEnabled=b.SlabThicknessMm.HasValue;SlabThicknessMm=b.SlabThicknessMm??10;
        visibility.Restore(b);ApplySection();RestoreCamerasRequested?.Invoke(this,EventArgs.Empty);
    }
    [RelayCommand] private async Task DeleteBookmarkAsync()
    {if(SelectedBookmark is {} b)await ReviewEditAsync(new EditDocumentCommand(R("Delete"),s=>s with{ReviewBookmarks=s.ReviewBookmarks.Remove(b.Id)}));}
}
