using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.AI.Contracts;
using Cadoryx.ViewModels.Toolboxes;
using Microsoft.Win32;

namespace Cadoryx.wpf.Views.Toolboxes;

public partial class AiAssistantToolboxView : UserControl
{
    private INotifyCollectionChanged? messages;
    private bool scrollPending;

    public AiAssistantToolboxView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as AiAssistantToolboxViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, e) => Attach(IsLoaded ? e.NewValue as AiAssistantToolboxViewModel : null);
    }

    private void Attach(AiAssistantToolboxViewModel? model)
    {
        if (messages is not null) messages.CollectionChanged -= OnMessagesChanged;
        messages = model?.Messages;
        if (messages is not null) messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (scrollPending) return;
        scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            scrollPending = false;
            if (IsLoaded && ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
        }, DispatcherPriority.Background);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantToolboxViewModel model || model.IsBusy) return;
        new AiAssistantSettingsWindow { Owner = Window.GetWindow(this), DataContext = model }.ShowDialog();
    }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantToolboxViewModel model || model.IsBusy) return;
        var picker = new OpenFileDialog
        {
            Filter = "Supported files|*.png;*.jpg;*.jpeg;*.txt;*.md;*.csv;*.json|Images|*.png;*.jpg;*.jpeg|Text|*.txt;*.md;*.csv;*.json",
            Multiselect = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await AttachFilesAsync(model, picker.FileNames);
    }

    private static async Task AttachFilesAsync(AiAssistantToolboxViewModel model, IEnumerable<string> files)
    {
        foreach (var file in files.Take(5))
        {
            try
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var isImage = extension is ".png" or ".jpg" or ".jpeg";
                if (!isImage && extension is not (".txt" or ".md" or ".csv" or ".json"))
                    throw new InvalidDataException("Unsupported attachment type.");
                var maxBytes = isImage ? 8_000_000 : 1_000_000;
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > maxBytes)
                    throw new InvalidDataException($"Attachment must be between 1 byte and {maxBytes / 1_000_000} MB.");
                byte[] bytes = await File.ReadAllBytesAsync(file);
                if (bytes.Length > maxBytes) throw new InvalidDataException("Attachment changed while loading.");
                var name = Path.GetFileName(file);
                AiChatContentPart part = isImage
                    ? AiChatContentPart.Image($"data:{(extension == ".png" ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(bytes)}")
                    : AiChatContentPart.FileText(name, "text/plain", new System.Text.UTF8Encoding(false, true).GetString(bytes));
                if (!model.AddAttachment(new CadChatAttachment(name, part)))
                {
                    model.ReportAttachmentError("Only five attachments are allowed, and files cannot be added while a request is running.");
                    break;
                }
            }
            catch (Exception exception) { model.ReportAttachmentError($"{Path.GetFileName(file)}: {exception.Message}"); }
        }
    }

    private async void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
            DataContext is AiAssistantToolboxViewModel pasteModel && !pasteModel.IsBusy)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if (paths.Length > 0)
                    {
                        e.Handled = true;
                        await AttachFilesAsync(pasteModel, paths);
                        return;
                    }
                }
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    e.Handled = true;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = new MemoryStream();
                    encoder.Save(output);
                    if (output.Length > 8_000_000)
                        throw new InvalidDataException("Pasted image exceeds 8 MB.");
                    var part = AiChatContentPart.Image($"data:image/png;base64,{Convert.ToBase64String(output.ToArray())}");
                    if (!pasteModel.AddAttachment(new CadChatAttachment("clipboard.png", part)))
                        pasteModel.ReportAttachmentError("Only five attachments are allowed.");
                    return;
                }
            }
            catch (Exception exception)
            {
                e.Handled = true;
                pasteModel.ReportAttachmentError($"Clipboard: {exception.Message}");
                return;
            }
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is AiAssistantToolboxViewModel model && model.SendCommand.CanExecute(null))
            model.SendCommand.Execute(null);
        e.Handled = true;
    }
}
