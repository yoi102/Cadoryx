using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

// Observes the real production UI. It does not invoke commands, edit controls,
// seed document content, supply picker results or synthesize acceptance results.
public static class StartupHook
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string output = "";
    private static int sequence;
    private static string? previous;
    private static DispatcherTimer? timer;
    private static Window? observedWindow;
    private static readonly Dictionary<string,object> observedSessions = [];

    public static void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--import-worker") || args.Contains("--native-drawing-worker") || args.Contains("--native-thread-worker")) return;
        int index = Array.IndexOf(args, "--a5-material-desktop-smoke");
        if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Explicit private material desktop arguments required.");
        output = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(output);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 1200 && Application.Current is null; i++) await Task.Delay(50);
            if (Application.Current is not {} app) throw new InvalidOperationException("Actual WPF application required within 60 seconds.");
            _ = app.Dispatcher.BeginInvoke(new Action(Install));
        });
    }

    private static void Install()
    {
        var app = Application.Current;
        if (app.MainWindow is not { IsLoaded: true })
        {
            _ = app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Install));
            return;
        }
        try
        {
            observedWindow = app.MainWindow;
            var services = (IServiceProvider)app.GetType().GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
            object Service(string name) => services.GetService(Type.GetType(name, true)!) ?? throw new InvalidOperationException("Missing production service: " + name);
            var settings = Service("Cadoryx.ViewModels.Services.Platform.Settings.IApplicationSettingsStore, Cadoryx.ViewModels.Services");
            var settingPath = settings.GetType().GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Private settings store field missing.");
            // Only the disposable acceptance process is redirected. User settings,
            // model editing and the published application assemblies remain intact.
            settingPath.SetValue(settings, Path.Combine(output, "application-settings.json"));
            var recovery = Service("Cadoryx.Kernel.Abstractions.IRecoveryStore, Cadoryx.Kernel.Abstractions");
            var recoveryRoot = (string)recovery.GetType().GetField("root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery)!;
            if (!Path.GetFullPath(recoveryRoot).Equals(Path.Combine(output, "recovery"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Private diagnostic recovery root required.");
            Write("observer-ready.json", new { pid = Environment.ProcessId, settingsPath = settingPath.GetValue(settings), recoveryRoot,
                scope = "Read-only document/UI observation; private settings persistence destination only; actual UI actions remain external." });
            var observerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            timer = observerTimer;
            observerTimer.Tick += (_, _) => Observe();
            app.MainWindow.Closed += (_, _) => { Observe(); observerTimer.Stop(); Write("observer-closed.json", new { pid = Environment.ProcessId, observations = sequence }); };
            Observe(); observerTimer.Start();
        }
        catch (Exception error)
        {
            Write("observer-failure.json", new { error = error.ToString() });
            app.Shutdown(1);
        }
    }

    private static object? Get(object? value, string property) => value?.GetType().GetProperty(property)?.GetValue(value);
    private static object?[] Values(object? dictionary) => Get(dictionary, "Values") is IEnumerable values ? values.Cast<object?>().ToArray() : [];
    private static object? Resources(DependencyObject node)
    {
        if (node is FrameworkElement { IsVisible: true, DataContext: {} model } && model.GetType().FullName == "Cadoryx.ViewModels.DocumentResourcesViewModel") return model;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Resources(VisualTreeHelper.GetChild(node,i)) is {} found) return found;
        return null;
    }
    private static object? ActualResourceDialog()
    {
        var host = Type.GetType("MaterialDesignThemes.Wpf.DialogHost, MaterialDesignThemes.Wpf", true)!;
        var method = host.GetMethod("GetDialogSession", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: [typeof(object)], modifiers: null)
            ?? throw new InvalidOperationException("Production DialogHost session API missing.");
        var actualSession = method.Invoke(null, ["Root"]);
        return Get(Get(actualSession, "Content"), "DataContext");
    }
    private static object? PendingPixels(object? resources)
    {
        var pending = Get(Get(resources,"MaterialAppearance"),"PendingTexture");
        if (pending is null) return null;
        var bytes = (byte[])pending.GetType().GetMethod("CopyPixels")!.Invoke(pending,null)!;
        return new { width = Get(pending,"Width"), height = Get(pending,"Height"),
            bytes = bytes.Length, rgbaSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    }
    private static object? AppearanceDraft(object? resources)
    {
        var editor = Get(resources,"MaterialAppearance");
        if (editor is null) return null;
        // Project observed values only. Serializing the live MVVM object also
        // discovers unrelated generated properties with ref-struct contracts.
        var fields = new[] { "Enabled", "DistinguishSides", "OverrideSourceStyles", "Shading", "Culling",
            "AlphaMode", "AlphaCutoff", "Planar", "PlaneSA", "PlaneSB", "PlaneSC", "PlaneSD",
            "PlaneTA", "PlaneTB", "PlaneTC", "PlaneTD", "ScaleS", "ScaleT", "TranslationS", "TranslationT",
            "RotationDegrees", "Repeat", "Filter", "Anisotropy", "TextureSummary", "Texture" };
        var value = fields.ToDictionary(name => name, name => Get(editor,name));
        object? Face(string name)
        {
            var face = Get(editor,name);
            return face is null ? null : new[] { "ColorArgb", "Alpha", "Metallic", "Roughness", "IndexOfRefraction", "Emission" }
                .ToDictionary(field => field, field => Get(face,field));
        }
        value["Front"] = Face("Front"); value["Back"] = Face("Back");
        return value;
    }
    private static void Observe()
    {
        try
        {
            var window = observedWindow ?? throw new InvalidOperationException("Actual selected main window not retained.");
            var workspace = window.DataContext;
            var document = Get(workspace, "ActiveDocument");
            var session = Get(document, "Session");
            if (session is not null && Get(session,"SessionId") is {} id) observedSessions[id.ToString()!] = session;
            var snapshot = Get(session, "Snapshot");
            var assets = Get(session, "Assets");
            var general = Get(Get(workspace, "ApplicationSettings"), "General");
            var dialogModel = ActualResourceDialog();
            var resources = dialogModel?.GetType().FullName == "Cadoryx.ViewModels.DocumentResourcesViewModel"
                ? dialogModel : Resources(window);
            var value = new
            {
                pid = Environment.ProcessId, title = window.Title,
                culture = Get(workspace, "CurrentCultureLCID"), general,
                activeSession = Get(session, "SessionId"), documentId = Get(snapshot, "Id"), stateId = Get(snapshot, "StateId"),
                filePath = Get(session, "FilePath"), isDirty = Get(session, "IsDirty"),
                isDetached = Get(document, "IsDetached"), isClosing = Get(session, "IsClosing"),
                assetsCount = Get(assets, "Count"), assetsBytes = Get(assets, "SizeBytes"),
                observedSessions = observedSessions.Select(pair => new { sessionId = pair.Key, isClosing = Get(pair.Value,"IsClosing"),
                    assetsCount = Get(Get(pair.Value,"Assets"),"Count"), assetsBytes = Get(Get(pair.Value,"Assets"),"SizeBytes") }).ToArray(),
                materials = Values(Get(snapshot, "Materials")),
                materialDraft = resources is null ? null : new { name = Get(resources,"MaterialName"), density = Get(resources,"DensityKgPerM3"),
                    selected = Get(Get(resources,"SelectedMaterial"),"Id"), isBusy = Get(resources,"IsBusy"), appearance = AppearanceDraft(resources), pendingPixels = PendingPixels(resources), status = Get(resources,"Status") },
                bodies = Values(Get(snapshot, "Bodies")).Select(b => new { id = Get(b,"Id"), materialId = Get(b,"MaterialId"), geometry = Get(b,"Geometry") }).ToArray(),
                status = Get(workspace, "StatusText"), toolStatus = Get(document, "ToolStatus"), hasPreview = Get(document, "HasPreview")
            };
            var serialized = JsonSerializer.Serialize(value, Json);
            if (serialized == previous) return;
            previous = serialized;
            Write($"observation-{++sequence:D4}.json", new { recordedAt = DateTimeOffset.UtcNow, observation = JsonSerializer.Deserialize<JsonElement>(serialized) });
        }
        catch (Exception error)
        {
            timer?.Stop();
            Write($"observation-failure-{++sequence:D4}.json", new { recordedAt = DateTimeOffset.UtcNow, error = error.ToString() });
        }
    }

    private static void Write(string name, object value)
    {
        using var stream = new FileStream(Path.Combine(output,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(stream,value,Json);
    }
}
