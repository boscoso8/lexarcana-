// Genera Red3D/Red3D.cs: tutto il codice di rete e 3D di Lex Arcana in un solo file C#.
//
//   dotnet run tools/GeneraRed3D.cs
//
// Prende i sorgenti di src/LexArcana.Core (protocollo, server, client, mappe, campagne,
// personaggi, combattimento) e di src/LexArcana.Visione (il client 3D), raccoglie le direttive
// using in cima, trasforma i namespace "a file" in blocchi e mette per prime le istruzioni di
// primo livello del client 3D, come vuole C#. Il risultato compila da solo con Raylib-cs:
// vedi Red3D/Red3D.csproj.

using System.Text;
using System.Text.RegularExpressions;

var root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("Lancia il generatore dalla cartella del repository.");
string[] folders = ["src/LexArcana.Core", "src/LexArcana.Visione"];

var usings = new SortedSet<string>(StringComparer.Ordinal);
var topLevel = new StringBuilder();
var body = new StringBuilder();
int count = 0;

foreach (var folder in folders)
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                 .Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var lines = File.ReadAllLines(file);
        var kept = new List<string>();
        string? ns = null;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^using [A-Za-z0-9_.]+;\s*$")) { usings.Add(line.Trim()); continue; }
            var m = Regex.Match(line, @"^namespace ([A-Za-z0-9_.]+);\s*$");
            if (m.Success) { ns = m.Groups[1].Value; continue; }
            kept.Add(line);
        }
        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        count++;

        if (ns is null)
        {
            // Solo il Program.cs del client 3D ha istruzioni di primo livello: vanno prima di ogni namespace.
            topLevel.AppendLine($"// ===== {relative} =====").AppendLine(string.Join('\n', kept)).AppendLine();
            continue;
        }
        body.AppendLine($"// ===== {relative} =====");
        body.AppendLine($"namespace {ns}").AppendLine("{");
        foreach (var line in kept) body.AppendLine(line.Length == 0 ? "" : "    " + line);
        body.AppendLine("}").AppendLine();
    }

var output = new StringBuilder();
output.AppendLine("// Red3D.cs — Lex Arcana: rete e 3D in un solo file.");
output.AppendLine("// Generato da tools/GeneraRed3D.cs: non modificarlo a mano, modifica i sorgenti in src/ e rigeneralo.");
output.AppendLine($"// Contiene {count} file sorgente. Compila con Red3D.csproj (net10.0, C# 14, Raylib-cs).");
output.AppendLine("//");
output.AppendLine("// Avvio del client 3D: dotnet run --project Red3D -- --personaggio aurelia.json --host <indirizzo del Demiurgo>");
output.AppendLine("// Il server e gli strumenti (personaggi, campagne) sono nel progetto completo: src/LexArcana.");
output.AppendLine();
foreach (var u in usings) output.AppendLine(u);
output.AppendLine();
output.Append(topLevel);
output.Append(body);

var target = Path.Combine(root, "Red3D", "Red3D.cs");
Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllText(target, output.ToString().ReplaceLineEndings("\n"));
Console.WriteLine($"Scritto {Path.GetRelativePath(root, target)}: {count} file, {output.Length / 1024} KB.");

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "LexArcana.slnx"))) return dir.FullName;
    return null;
}
