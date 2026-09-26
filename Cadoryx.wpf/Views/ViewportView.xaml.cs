using System.ComponentModel;
using System.Windows;
using Cadoryx.ViewModels;
namespace Cadoryx.wpf.Views;

public partial class ViewportView
{
    private CadDocumentViewModel? document;
    public ViewportView()
    {
        InitializeComponent();Loaded+=(_,_)=>Attach();Unloaded+=(_,_)=>Detach();
        DataContextChanged+=(_,_)=>{if(IsLoaded){Detach();Attach();}};
    }
    private void Attach()
    {
        if(document is not null||DataContext is not CadDocumentViewModel vm||vm.IsDetached)return;
        document=vm;vm.Review.PropertyChanged+=OnReview;vm.Detaching+=OnDetaching;RefreshSplit();
    }
    private void OnDetaching(object? sender,EventArgs e)=>Detach();
    private void Detach()
    {
        if(document is {} vm){vm.Review.PropertyChanged-=OnReview;vm.Detaching-=OnDetaching;}
        SecondPane.Content=null;document=null;
    }
    private void OnReview(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(DocumentReviewViewModel.SplitView))RefreshSplit();}
    private void RefreshSplit()
    {
        bool split=document?.Review.SplitView==true;
        SecondColumn.Width=split?new GridLength(1,GridUnitType.Star):new GridLength(0);
        SplitColumn.Width=new(split?5:0);
        if(split)SecondPane.Content??=new ViewportPane{IsSecondary=true,DataContext=document};
        else SecondPane.Content=null;
    }
}
