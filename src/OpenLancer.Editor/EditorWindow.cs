using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Dialogs;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Shaders;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Strings;

namespace OpenLancer.Editor;

/// <summary>
/// The OpenLancer editor window: a menu bar, the workspace panels (Hierarchy, Scene, Inspector, and Database and
/// Console at the bottom) and a status bar. While no profile is open, the Welcome screen replaces the panels.
/// </summary>
/// <remarks>The edits of the active mod and their menus are in <c>EditorWindow.Editing.cs</c>.</remarks>
internal sealed partial class EditorWindow : Game
{
    private const string AppTitle = "OpenLancer Editor";
    private const string DefaultSystem = "li01";

    /// <summary>The Console source of the messages of the editor itself.</summary>
    private const string EditorSource = "Editor";
    private static readonly FileDialogFilters ProfileFilters = new(new FileFilter("OpenLancer profile", "json"));
    private static readonly float[] UiScales = [1, 1.25f, 1.5f, 1.75f, 2];

    private readonly string? startProfile;
    private ImGuiHelper gui = null!;
    private EditorLayout layout = null!;
    private ConsolePanel console = null!;
    private HierarchyPanel hierarchy = null!;
    private InspectorPanel inspector = null!;
    private DatabasePanel database = null!;
    private WelcomeScreen welcome = null!;
    private StatusBar statusBar = null!;

    // The time since the last frame. The Scene panel reads it while the layout draws.
    private double frameElapsed;

    // The lists of the File menu. They come from the disk, so they are read when their submenu opens, not each frame.
    private List<string> menuProfiles = [];
    private HashSet<string> missingRecentProfiles = [];

    /// <param name="startProfile">The profile file to open at the start, or null.</param>
    public EditorWindow(string? startProfile) : base(1600, 900, true)
    {
        this.startProfile = startProfile;
        Edits = new EditController(this);
        // The window gets this title when it is made. The shaders compile before the first frame, and the window
        // must not show the engine name during that time.
        Title = AppTitle;
    }

    public EditorSession? Session { get; private set; }
    public SceneView Scene { get; private set; } = null!;

    public EditorSettings Settings { get; } = EditorSettings.Load();

    /// <summary>The messages of the Console.</summary>
    public ConsoleLog Log { get; } = new();

    /// <summary>
    /// The selected item: a system object, zone, light, ship, equipment, system, or a section of an item file
    /// (<see cref="SectionRef"/>).
    /// </summary>
    public object? Selection { get; set; }

    /// <summary>The edits of the open profile, the saves and what follows a save.</summary>
    public EditController Edits { get; }

    /// <summary>The dialog for the questions of the editor, for example Save, Discard or Cancel.</summary>
    public ChoiceDialog Choice { get; } = new();

    /// <summary>
    /// False while the game data loads or loads again after a save. The load uses the session resources, so the
    /// profile cannot close or change until the load ends.
    /// </summary>
    public bool CanChangeProfile => Session is not { IsBusy: true };

    /// <summary>The error of the last profile that did not open, or null. The Welcome screen shows it.</summary>
    public string? OpenError { get; private set; }

    protected override void Load()
    {
        // The engine warnings and errors go to the Console from the start. FLLog gives each line to this window on the
        // UI thread. A later EngineLogCapture gives its lines back to this listener when it ends.
        FLLog.UIThread = this;
        FLLog.AppendLine = Log.AddEngineLine;
        AllShaders.Compile(RenderContext);
        gui = new ImGuiHelper(this, Math.Clamp(Settings.UiScale, UiScales[0], UiScales[^1]));
        // ImGui reads its state before the first frame. A settings file from a first start or from an older editor
        // has no state, and ImGui cannot read null text.
        if (!string.IsNullOrEmpty(Settings.ImGuiState))
        {
            ImGui.LoadIniSettingsFromMemory(Settings.ImGuiState);
        }

        var fonts = new FontManager();
        fonts.ConstructDefaultFonts();
        Services.Add(fonts);
        Services.Add(new CommandBuffer(RenderContext));
        Services.Add(new Billboards(RenderContext));
        Services.Add(new EditorRenderSettings());
        Scene = new SceneView(this);
        hierarchy = new HierarchyPanel(this);
        inspector = new InspectorPanel(this);
        database = new DatabasePanel(this);
        console = new ConsolePanel(this);
        layout = new EditorLayout(Settings,
            new EditorPanel("hierarchy", "Hierarchy", EditorIcons.List, hierarchy.Draw, hierarchy.Toolbar),
            new EditorPanel("scene", "Scene", EditorIcons.Globe, () => Scene.Draw(frameElapsed), Scene.Toolbar,
                padding: false),
            new EditorPanel("inspector", "Inspector", EditorIcons.InfoCircle, () => inspector.Draw(Selection),
                padding: false),
            new EditorPanelGroup("bottom",
            [
                new EditorPanel("database", "Database", EditorIcons.Database, () => database.Draw(Session?.GameData),
                    database.Toolbar, padding: false),
                new EditorPanel("console", "Console", EditorIcons.Terminal, console.Draw, console.Toolbar,
                    padding: false, badges: console.Badges),
            ]));
        statusBar = new StatusBar(this);
        welcome = new WelcomeScreen(this);

        // The profile of the command line opens first, then the newest recent profile. Without either, the Welcome
        // screen shows.
        var newest = Settings.RecentProfiles.Count > 0 ? Settings.RecentProfiles[0].Path : null;
        var path = startProfile ?? (File.Exists(newest) ? newest : null);
        if (path != null)
        {
            OpenProfile(path);
        }
    }

    protected override void Cleanup()
    {
        // After a crash in Load, the ImGui context can be missing.
        if (gui != null)
        {
            Settings.ImGuiState = ImGui.SaveIniSettingsToMemory();
        }

        Settings.Save();
        // Without local patch 0017, the window closes without a question. So the unsaved edits go into the recovery
        // file, and the next open of the mod offers to restore them (decision D8). An open field edit ends first, so
        // that the file keeps it. When no unsaved edits remain, for example after an undo, the write deletes the file.
        if (Session is { } session)
        {
            inspector.EndEdit();
            if (session.RecoveryOutdated)
            {
                Edits.WriteRecovery(session);
            }
        }

        Edits.Dispose();
        Scene.Dispose();
        Session?.Dispose();
    }

    /// <summary>Asks for a profile file and opens it.</summary>
    public void OpenProfileDialog()
    {
        FileDialog.Open(path =>
        {
            if (path != null)
            {
                OpenProfile(path);
            }
        }, ProfileFilters);
    }

    /// <summary>Hides the error of the last open on the Welcome screen.</summary>
    public void DismissOpenError() => OpenError = null;

    /// <summary>Sets the Hierarchy search, for example "t:zone" to show the zones of the open system.</summary>
    public void ShowInHierarchy(string search) => hierarchy.SetSearch(search);

    /// <summary>
    /// Shows the Console tab and selects an entry in it. The Console is part of the workspace, which shows only while
    /// a profile is open. Without a profile, this does nothing.
    /// </summary>
    public void ShowConsole(ConsoleEntry entry)
    {
        if (Session == null)
        {
            return;
        }

        console.Select(entry);
        layout.Show("console");
    }

    /// <summary>
    /// Closes the open profile and opens another one. The engine data loads in the background. While the data of the
    /// open profile loads, this does nothing (see <see cref="CanChangeProfile"/>). With unsaved edits, a dialog asks
    /// first. When the profile does not open, <see cref="OpenError"/> tells why.
    /// </summary>
    public void OpenProfile(string path)
    {
        if (CanChangeProfile)
        {
            Edits.Leave("closing the profile", () => OpenNow(path));
        }
    }

    /// <summary>
    /// Opens a profile with the active mod of the settings: the mod that the user selected for it last time, or its
    /// last mod. The game data loads from the edit view: the base data with the mods up to the active mod (decision
    /// D2).
    /// </summary>
    private void OpenNow(string path)
    {
        if (!CanChangeProfile)
        {
            return;
        }

        CloseNow();
        OpenError = null;
        try
        {
            var full = Path.GetFullPath(path);
            var profile = ModProfile.Load(full);
            var active = Settings.ActiveModOf(full, profile.Mods.Select(m => m.Id).ToList());
            var edits = EditSession.Open(profile, active, DllStringResourceWriter.Instance);
            foreach (var d in edits.OpenProblems.Concat(edits.EditView.Diagnostics))
            {
                Log.Add(d);
            }

            if (edits.EditView.HasErrors)
            {
                OpenError = $"The profile {profile.Name} has merge errors. It did not open. The Console shows the " +
                            "errors.";
                Log.Add(ConsoleSeverity.Error, EditorSource, OpenError);
                return;
            }

            Settings.AddRecentProfile(full, DateTime.UtcNow);
            Settings.Save();
            var fileSystem = edits.EditView.CreateFileSystem();
            var repo = edits.ActiveMod == null ? null : RepoCheck.RepoRoot(edits.ActiveMod.Folder);
            var session = new EditorSession(edits, new GameResourceManager(this, fileSystem), repo);
            Session = session;
            Log.Add(ConsoleSeverity.Info, EditorSource,
                $"Opened profile {profile.Name} ({profile.Mods.Count} mods). " +
                (edits.ActiveMod == null ? "No mod is active, so the profile is read-only. " : $"Active mod: {edits.ActiveMod.Id}. ") +
                "Loading game data...");
            ModWarnings(session);
            Edits.OfferRecovery();
            Task.Run(() =>
            {
                try
                {
                    var data = new GameDataManager(new GameItemDb(fileSystem), session.Resources);
                    data.LoadData(this);
                    QueueUIThread(() => DataLoaded(session, data));
                }
                catch (Exception e)
                {
                    QueueUIThread(() =>
                    {
                        session.LoadEnded(null);
                        Log.Add(ConsoleSeverity.Error, EditorSource, $"The game data did not load: {e.Message}");
                    });
                }
            });
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            OpenError = e.Message;
            Log.Add(ConsoleSeverity.Error, EditorSource, e.Message);
        }
    }

    /// <summary>
    /// Closes the open profile and its system. While the data loads, this does nothing (see
    /// <see cref="CanChangeProfile"/>). With unsaved edits, a dialog asks first.
    /// </summary>
    public void CloseProfile()
    {
        if (Session != null && CanChangeProfile)
        {
            Edits.Leave("closing the profile", CloseNow);
        }
    }

    private void CloseNow()
    {
        if (Session == null || !CanChangeProfile)
        {
            return;
        }

        Edits.Stop();
        // The panels forget the game data of the profile, so that it does not stay in memory while no profile is
        // open. The panels do not draw while the Welcome screen shows, so they cannot do this themselves.
        Scene.Close();
        hierarchy.Reset();
        inspector.Reset();
        database.Reset();
        Selection = null;
        Session.Dispose();
        Log.Add(ConsoleSeverity.Info, EditorSource, $"Closed profile {Session.Profile.Name}.");
        Session = null;
    }

    public void OpenSystem(StarSystem system)
    {
        if (Session?.GameData is not { } data)
        {
            return;
        }

        // A model load reads the material map, which a reload makes again (review finding F5).
        if (Session.IsReloading)
        {
            Log.Add(ConsoleSeverity.Info, EditorSource, "The game data loads again after a save. Open the system after the load.");
            return;
        }

        Selection = null;
        Scene.Open(system, data, Session.Resources);
        Log.Add(ConsoleSeverity.Info, EditorSource,
            $"Opened system {Session.Name(system.IdsName)} ({system.Nickname}).");
    }

    private void DataLoaded(EditorSession session, GameDataManager data)
    {
        session.LoadEnded(data);
        if (Session != session)
        {
            return;
        }

        Log.Add(ConsoleSeverity.Info, EditorSource,
            $"Game data loaded: {data.Items.Systems.Count()} systems, {data.Items.Ships.Count()} ships, " +
            $"{data.Items.Equipment.Count()} equipment.");
        if (data.Items.Systems.Get(DefaultSystem) is { } system)
        {
            OpenSystem(system);
        }
    }

    protected override void Update(double elapsed) => Scene.Update(elapsed);

    protected override void Draw(double elapsed)
    {
        RenderContext.ReplaceViewport(0, 0, Width, Height);
        RenderContext.ClearColor = EditorTheme.Backdrop;
        RenderContext.ClearAll();
        gui.NewFrame(elapsed);
        EditorTheme.ApplyIfScaleChanged();
        ImGui.PushFont(ImGuiHelper.Roboto, 0);

        var menuHeight = MainMenu();
        var statusHeight = MathF.Truncate(StatusBar.Height * ImGuiHelper.Scale);
        frameElapsed = elapsed;
        var position = new Vector2(0, menuHeight);
        var size = new Vector2(Width, Height - menuHeight - statusHeight);
        if (Session == null)
        {
            welcome.Draw(position, size);
        }
        else if (layout.Draw(position, size))
        {
            Settings.Save();
        }

        statusBar.Draw(new Vector2(0, Height - statusHeight), Width);
        DrawDialogs();
        Shortcuts();
        Edits.Update(inspector.IsEditing);

        ImGui.PopFont();
        gui.Render(RenderContext);

        // ImGui has no .ini file here, so it asks the editor to save its state, a few seconds after a change.
        var io = ImGui.GetIO();
        if (io.WantSaveIniSettings)
        {
            Settings.ImGuiState = ImGui.SaveIniSettingsToMemory();
            io.WantSaveIniSettings = false;
            Settings.Save();
        }

        // The title names the profile and the system. Set it only when it changes.
        var title = WindowTitle();
        if (title != Title)
        {
            Title = title;
        }
    }

    /// <summary>
    /// "OpenLancer Editor - sample [proof]* - New York (Li01)", without the parts that are not open. The brackets name
    /// the active mod, and the star shows unsaved edits.
    /// </summary>
    private string WindowTitle()
    {
        var title = AppTitle;
        if (Session != null)
        {
            var mod = Session.Edits.ActiveMod?.Id ?? "read-only";
            title += $" - {Session.Profile.Name} [{mod}]{(Session.Edits.IsDirty ? "*" : "")}";
        }

        if (Scene.System is { } system)
        {
            var name = Session?.Name(system.IdsName);
            title += string.IsNullOrEmpty(name) ? $" - {system.Nickname}" : $" - {name} ({system.Nickname})";
        }

        return title;
    }

    private float MainMenu()
    {
        ImGui.BeginMainMenuBar();
        EditorTheme.PushMenuColors();
        if (ImGui.BeginMenu("File"))
        {
            FileMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            EditMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Mod"))
        {
            ModMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            ViewMenu();
            ImGui.EndMenu();
        }

        EditorTheme.PopMenuColors();
        var height = ImGui.GetWindowHeight();
        ImGui.EndMainMenuBar();
        return height;
    }

    private void FileMenu()
    {
        var loading = !CanChangeProfile;
        const string loadingTip = "The game data of the open profile is loading.";
        if (Theme.BeginIconMenu(EditorIcons.Open, "Open profile"))
        {
            if (ImGui.IsWindowAppearing())
            {
                menuProfiles = ProfileFiles();
            }

            foreach (var path in menuProfiles)
            {
                if (Theme.IconMenuItem(EditorIcons.LayerGroup, Path.GetFileNameWithoutExtension(path), !loading))
                {
                    OpenProfile(path);
                }

                ImGui.SetItemTooltip(loading ? loadingTip : path);
            }

            if (menuProfiles.Count == 0)
            {
                ImGui.MenuItem("No profiles in GameData\\profiles", false);
            }

            ImGui.EndMenu();
        }

        if (Theme.BeginIconMenu(EditorIcons.Folder, "Open recent"))
        {
            if (ImGui.IsWindowAppearing())
            {
                missingRecentProfiles = Settings.RecentProfiles.Where(r => !File.Exists(r.Path)).Select(r => r.Path)
                    .ToHashSet();
            }

            foreach (var recent in Settings.RecentProfiles)
            {
                var exists = !missingRecentProfiles.Contains(recent.Path);
                if (Theme.IconMenuItem(EditorIcons.LayerGroup, Path.GetFileNameWithoutExtension(recent.Path),
                        exists && !loading))
                {
                    OpenProfile(recent.Path);
                }

                ImGui.SetItemTooltip(loading ? loadingTip
                    : exists ? recent.Path
                    : $"The file is not found.\n{recent.Path}");
            }

            if (Settings.RecentProfiles.Count == 0)
            {
                ImGui.MenuItem("No recent profiles", false);
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(EditorIcons.FileImport, "Open profile file...", !loading))
        {
            OpenProfileDialog();
        }

        if (loading)
        {
            ImGui.SetItemTooltip(loadingTip);
        }

        if (Theme.IconMenuItem(EditorIcons.X, "Close profile", Session != null && !loading))
        {
            CloseProfile();
        }

        if (loading)
        {
            ImGui.SetItemTooltip(loadingTip);
        }

        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.DoorOpen, "Exit", true))
        {
            Edits.Leave("closing the editor", Exit);
        }
    }

    private void ViewMenu()
    {
        if (Theme.BeginIconMenu(EditorIcons.MagnifyingGlass, "UI scale"))
        {
            foreach (var scale in UiScales)
            {
                if (ImGui.MenuItem($"{scale * 100:0}%", "", ImGuiHelper.UserScale == scale))
                {
                    // ImGuiHelper applies the new scale at the start of the next frame. Then EditorTheme writes
                    // the style again.
                    ImGuiHelper.UserScale = scale;
                    Settings.UiScale = scale;
                    Settings.Save();
                }
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(EditorIcons.Table, "Reset layout", true))
        {
            Settings.HierarchyWidth = EditorSettings.DefaultHierarchyWidth;
            Settings.InspectorWidth = EditorSettings.DefaultInspectorWidth;
            Settings.BottomHeight = EditorSettings.DefaultBottomHeight;
            Settings.DatabaseCategoryWidth = EditorSettings.DefaultDatabaseCategoryWidth;
            Settings.ConsoleDetailHeight = EditorSettings.DefaultConsoleDetailHeight;
            Settings.Save();
        }

        ImGui.SetItemTooltip("Sets the panel sizes to their defaults.");
    }

    /// <summary>
    /// The profile files in the workspace's GameData\profiles folder, found from the editor's folder.
    /// </summary>
    internal static List<string> ProfileFiles()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var profiles = Path.Combine(dir.FullName, "GameData", "profiles");
            if (Directory.Exists(profiles))
            {
                return Directory.EnumerateFiles(profiles, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        return [];
    }
}
