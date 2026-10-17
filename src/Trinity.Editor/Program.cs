using Trinity;
using Trinity.Editor;

// Trinity Editor. Opens the project given (a folder with trinity.json, or the file itself),
// otherwise the last project opened.
//
//   trinity-editor [path/to/project] [--open Scripts/Spin.cs[:LINE]] [--select NODE] [--screenshot out.png SECONDS]

var settings = Settings.Load("trinity-editor");
var recent = settings.Get("recent", EditorJson.Default.RecentProjects);
(string, double)? screenshot = null;
string? path = null, open = null, select = null;
for (var a = 0; a < args.Length; a++)
    if (args[a] == "--screenshot") screenshot = (Path.GetFullPath(args[++a]), double.Parse(args[++a]));
    else if (args[a] == "--open") open = args[++a];
    else if (args[a] == "--select") select = args[++a];
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
settings.Set("recent", recent, EditorJson.Default.RecentProjects);
settings.Save();

using var editor = new EditorApp(project) { Screenshot = screenshot };
if (open != null)
    editor.OpenScript(open);
if (select != null)
    editor.Select(select);
editor.Run();
return 0;
