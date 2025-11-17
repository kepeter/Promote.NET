using System.Resources;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Promote;

internal class Program
{
    static async Task Main(string[] args)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                Settings settings = context.Configuration.Get<Settings>() ?? new Settings();
                services.AddSingleton(settings);

                services.AddSingleton<Engine>();
                services.Configure<Settings>(context.Configuration);
            });

        IHost host = builder.Build();

        Engine engine = host.Services.CreateScope().ServiceProvider.GetRequiredService<Engine>();

        Console.WriteLine("Starting engine...");
        await engine.Start();

        await Task.Delay(2000);

        Console.WriteLine("Stopping engine...");
        await engine.Stop();

        await host.RunAsync();
    }
}
