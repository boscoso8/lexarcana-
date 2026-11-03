using System.Text;
using LexArcana.App;
using LexArcana.Ui;

Console.OutputEncoding = Encoding.UTF8;

if (!Options.TryParse(args, out var options, out var error))
{
    await Console.Error.WriteLineAsync($"{error}\n\n{Options.Usage}");
    return 2;
}

var palette = new Palette(options.Color && Palette.ShouldUseColor());
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

return options.Mode switch
{
    Mode.Server => await DemiurgeConsole.RunAsync(options, palette, stop.Token),
    Mode.Client => await PlayerConsole.RunAsync(options, palette, stop.Token),
    _ => ShowUsage(),
};

static int ShowUsage()
{
    Console.WriteLine(Options.Usage);
    return 0;
}
