using Trinity;
using Trinity.Editor;

// Trinity Editor. With a project (a folder with trinity.json, or the file itself) it opens that project;
// without one it opens the Project Manager. File > Quit to Project List goes back to it.
//
//   trinity-editor [path/to/project] [--scene Scenes/x.scene.json] [--open Scripts/Spin.cs[:LINE]] [--select NODE]
//                  [--screenshot out.png SECONDS]

var settings = Settings.Load("trinity-editor");
(string, double)? screenshot = null;
string? path = null, open = null, select = null, sceneFile = null;
for (var a = 0; a < args.Length; a++)
    if (args[a] == "--screenshot") screenshot = (Path.GetFullPath(args[++a]), double.Parse(args[++a]));
    else if (args[a] == "--open") open = args[++a];
    else if (args[a] == "--select") select = args[++a];
    else if (args[a] == "--scene") sceneFile = args[++a];
    else path = args[a];

EditorProject? project = null;
if (path != null)
{
    try
    {
        project = EditorProject.Load(Path.GetFullPath(path));
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

// The BRUTAL logo shows once, in whichever window opens first
var splash = true;
var first = true;
while (true)
{
    if (project == null)
    {
        using var manager = new ProjectManager(settings, splash) { Screenshot = first ? screenshot : null };
        splash = false;
        project = manager.Run();
        if (project == null || first && screenshot != null)
            return 0;
    }
    ProjectManager.Opened(settings, project.Root);

    bool back;
    using (var editor = new EditorApp(project, settings, splash) { Screenshot = first ? screenshot : null })
    {
        if (first)
        {
            if (sceneFile != null)
                editor.OpenScene(project.Absolute(sceneFile));
            if (open != null)
                editor.OpenScript(open);
            if (select != null)
                editor.Select(select);
        }
        editor.Run();
        back = editor.BackToProjects;
    }
    splash = first = false;
    if (!back)
        return 0;
    project = null;
}
