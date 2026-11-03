using System.Reflection;
using System.Runtime.InteropServices;

namespace LexArcana;

/// <summary>Le versioni del gioco e del runtime .NET con cui gira (incluso nell'eseguibile, se è autosufficiente).</summary>
public static class Versions
{
    public static string Game =>
        typeof(Versions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string Describe(string program) =>
        $"{program} {Game} · {Runtime} · {RuntimeInformation.RuntimeIdentifier}";
}
