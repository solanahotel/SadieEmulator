using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sadie.API;
using Sadie.Shared;
using SadieEmulator;
using Serilog;

namespace Sadie.Console;

internal static class Program
{
    private static IServer? _server;

    private static async Task Main()
    {
        // The wire protocol uses '.' as the decimal separator (the client parses
        // numbers with parseFloat). Force invariant culture so doubles like furniture
        // Z heights serialize as "0.90" not "0,90" under locales such as nl_NL —
        // otherwise the client reads "0,90" as 0 and stacked items drop to the floor.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        SetEventHandlers();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, collection) => ServerServiceCollection.AddServices(collection, context.Configuration))
            .UseSerilog((hostContext, _, logger) => 
                logger.ReadFrom.Configuration(hostContext.Configuration))
            .Build();
        
        WriteHeaderToConsole();
        
        _server = host.Services.GetRequiredService<IServer>();
        await _server.RunAsync();
        await host.RunAsync();
    }
    
    private static void WriteHeaderToConsole()
    {
        System.Console.ForegroundColor = ConsoleColor.Magenta;

        System.Console.WriteLine(@"");
        System.Console.WriteLine(@"   $$$$$$\                  $$\ $$\           ");
        System.Console.WriteLine(@"  $$  __$$\                 $$ |\__|          ");
        System.Console.WriteLine(@"  $$ /  \__| $$$$$$\   $$$$$$$ |$$\  $$$$$$\  ");
        System.Console.WriteLine(@"  \$$$$$$\   \____$$\ $$  __$$ |$$ |$$  __$$\ ");
        System.Console.WriteLine(@"   \____$$\  $$$$$$$ |$$ /  $$ |$$ |$$$$$$$$ |");
        System.Console.WriteLine(@"  $$\   $$ |$$  __$$ |$$ |  $$ |$$ |$$   ____|");
        System.Console.WriteLine(@"  \$$$$$$  |\$$$$$$$ |\$$$$$$$ |$$ |\$$$$$$$\ ");
        System.Console.WriteLine(@"   \______/  \_______| \_______|\__| \_______|");
        System.Console.WriteLine(@"");

        System.Console.ForegroundColor = ConsoleColor.White;

        System.Console.WriteLine($"         You're running version {GlobalState.Version}");
        System.Console.WriteLine("");
    }
    
    private static void SetEventHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;
        AppDomain.CurrentDomain.ProcessExit += OnClose;
        
        System.Console.CancelKeyPress += OnClose;
    }

    private static async void OnClose(object? sender, EventArgs e)
    {
        if (_server == null)
        {
            return;
        }

        await _server.DisposeAsync();
        _server = null;
    }

    private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Logger.Error(e.ExceptionObject.ToString());
    }
}