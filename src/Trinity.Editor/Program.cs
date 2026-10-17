using Trinity;
using Trinity.Editor;

// Trinity Editor. Opens the project given (a folder with trinity.json, or the file itself),
// otherwise the last project opened.
//
//   trinity-editor [path/to/project] [--screenshot out.png SECONDS]

var settings = Settings.Load("trinity-editor");
var recent = settings.Get<RecentProjects>("recent");
(string, double)? screenshot = null;
string? path = null;
for (var a = 0; a < args.Length; a++)
    if (args[a] == "--screenshot") screenshot = (Path.GetFullPath(args[++a]), double.Parse(args[++a]));
    else path = args[a];
path ??= recent.Last;
if (path == null)
{
    Console.Error.WriteLine("Usage: trinity-editor <project folder or trinity.json>");
    return 1;
}

EditorProject project;
try
{
    project = EditorProject.Load(Path.GetFullPath(path));
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
recent.Last = project.Root;
settings.Set("recent", recent);
settings.Save();

using var editor = new EditorApp(project) { Screenshot = screenshot };
editor.Run();
return 0;

sealed class RecentProjects
{
    public string? Last { get; set; }
}
