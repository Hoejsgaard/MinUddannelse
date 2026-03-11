using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Supabase;
using System.Linq;
using System.Net;
using MinUddannelse.Agents;
using MinUddannelse.AI.Prompts;
using MinUddannelse.AI.Services;
using MinUddannelse.Configuration;
using MinUddannelse.Content.WeekLetters;
using MinUddannelse.GoogleCalendar;
using MinUddannelse.Client;
using MinUddannelse.Models;
using MinUddannelse.Repositories;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse.Scheduling;
using MinUddannelse.Security;
using Serilog;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace MinUddannelse;

public class Program
{
    public static async Task Main(string[] args)
    {
        var serviceProvider = ConfigureServices();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(nameof(Program));

        try
        {
            var config = serviceProvider.GetRequiredService<Config>();

            logger.LogInformation("Starting MinUddannelse");

            if (!await ValidateConfigurationAsync(serviceProvider, logger))
                return;

            await InitializeSupabaseAsync(serviceProvider, logger);

            var childAgents = await StartChildAgentsAsync(serviceProvider, logger);

            logger.LogInformation("MinUddannelse started");

            var cancellationTokenSource = new CancellationTokenSource();
            ConfigureGracefulShutdown(childAgents, cancellationTokenSource, logger);

            await RunApplicationAsync(logger, cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting MinUddannelse");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task<bool> ValidateConfigurationAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var config = serviceProvider.GetRequiredService<Config>();
        var configValidator = serviceProvider.GetRequiredService<IConfigurationValidator>();
        var validationResult = await configValidator.ValidateConfigurationAsync(config);

        if (!validationResult.IsValid)
        {
            foreach (var error in validationResult.Errors)
            {
                logger.LogError("Configuration error: {Error}", error);
            }
            logger.LogError("Application startup failed due to configuration errors");
            return false;
        }

        foreach (var warning in validationResult.Warnings)
        {
            logger.LogWarning("Configuration warning: {Warning}", warning);
        }

        return true;
    }

    private static async Task InitializeSupabaseAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var supabaseClient = serviceProvider.GetRequiredService<Supabase.Client>();

        try
        {
            await supabaseClient.InitializeAsync();
            logger.LogInformation("Supabase client initialized successfully");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Supabase client initialization failed - continuing without database features");
            return;
        }

        try
        {
            await supabaseClient.From<Reminder>().Limit(1).Get();
            logger.LogInformation("Supabase connection test successful");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Supabase connection test failed - continuing without database features");
        }
    }

    private static async Task<List<IChildAgent>> StartChildAgentsAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var config = serviceProvider.GetRequiredService<Config>();
        var schedulingService = serviceProvider.GetRequiredService<ISchedulingService>();
        var factory = serviceProvider.GetRequiredService<IChildAgentFactory>();

        await schedulingService.StartAsync();
        logger.LogInformation("SchedulingService started");

        var childAgents = new List<IChildAgent>();
        foreach (var child in config.MinUddannelse?.Children ?? new List<Child>())
        {
            logger.LogInformation("Starting agent for child: {ChildName}", child.FirstName);

            var childAgent = factory.CreateChildAgent(child, schedulingService);
            await childAgent.StartAsync();
            childAgents.Add(childAgent);
        }

        logger.LogInformation("Started {Count} child agents", childAgents.Count);
        return childAgents;
    }

    private static void ConfigureGracefulShutdown(List<IChildAgent> childAgents, CancellationTokenSource cancellationTokenSource, ILogger logger)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellationTokenSource.Cancel();
            logger.LogInformation("Shutdown requested");

            foreach (var agent in childAgents)
            {
                _ = agent.StopAsync();
            }
            logger.LogInformation("Stopping {Count} child agents", childAgents.Count);
        };
    }

    private static async Task RunApplicationAsync(ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Application shutting down gracefully");
        }
    }

    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(GetEmbeddedAppsettings())
            .Build();

        services.AddSingleton<Config>(provider =>
        {
            var config = new Config();
            configuration.Bind(config);
            return config;
        });

        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "minuddannelse-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50_000_000,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog();
        });

        services.AddMemoryCache();

        services.AddHttpClient("UniLogin", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.Timeout = TimeSpan.FromMinutes(2);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        services.AddSingleton<IChildAuditService, ChildAuditService>();
        services.AddSingleton<IChildRateLimiter, ChildRateLimiter>();
        services.AddSingleton<IWeekLetterService, WeekLetterService>();
        services.AddSingleton<IChildScheduler, ChildScheduler>();
        services.AddSingleton<IOpenAiService, OpenAiService>();

        services.AddSingleton<IChildAgentFactory, ChildAgentFactory>();

        services.AddScoped<WeekLetterCache>();
        services.AddScoped<IMinUddannelseClient, MinUddannelseClient>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddSingleton<IPromptSanitizer, PromptSanitizer>();


        services.AddSingleton<IGoogleCalendarService, GoogleCalendarService>();
        services.AddSingleton<IConversationManager, ConversationManager>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();
        services.AddSingleton<IAiToolsManager, AiToolsManager>();
        services.AddSingleton<IWeekLetterAiService>(provider =>
        {
            var config = provider.GetRequiredService<Config>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var aiToolsManager = provider.GetRequiredService<IAiToolsManager>();
            var conversationManager = provider.GetRequiredService<IConversationManager>();
            var promptBuilder = provider.GetRequiredService<IPromptBuilder>();
            return new WeekLetterAiService(config.OpenAi.ApiKey, loggerFactory, aiToolsManager, conversationManager, promptBuilder, config.OpenAi.Model, config.Scheduling.DefaultOnDateReminderTime);
        });

        services.AddSingleton<Supabase.Client>(provider =>
        {
            var config = provider.GetRequiredService<Config>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("SupabaseClient");

            logger.LogInformation("Creating Supabase client (will initialize on first use)");

            var options = new SupabaseOptions
            {
                AutoConnectRealtime = false,
                AutoRefreshToken = false
            };

            var client = new Supabase.Client(config.Supabase.Url, config.Supabase.ServiceRoleKey, options);

            logger.LogInformation("Supabase client created successfully");
            return client;
        });

        services.AddSingleton<IReminderRepository, ReminderRepository>();
        services.AddSingleton<IWeekLetterRepository, WeekLetterRepository>();
        services.AddSingleton<IAppStateRepository, AppStateRepository>();
        services.AddSingleton<IRetryTrackingRepository, RetryTrackingRepository>();
        services.AddSingleton<IScheduledTaskRepository, ScheduledTaskRepository>();

        services.AddSingleton<IConfigurationValidator, ConfigurationValidator>();

        services.AddSingleton<ISchedulingService>(provider =>
        {
            return new SchedulingService(
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<IReminderRepository>(),
                provider.GetRequiredService<IScheduledTaskRepository>(),
                provider.GetRequiredService<IWeekLetterRepository>(),
                provider.GetRequiredService<IRetryTrackingRepository>(),
                provider.GetRequiredService<IAppStateRepository>(),
                provider.GetRequiredService<IWeekLetterService>(),
                provider.GetRequiredService<IWeekLetterReminderService>(),
                provider.GetRequiredService<Config>());
        });

        services.AddSingleton<IWeekLetterReminderService>(provider =>
        {
            var config = provider.GetRequiredService<Config>();
            return new WeekLetterReminderService(
                provider.GetRequiredService<IOpenAiService>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<WeekLetterReminderService>(),
                provider.GetRequiredService<IWeekLetterRepository>(),
                provider.GetRequiredService<IReminderRepository>(),
                config.OpenAi.Model,
                TimeOnly.Parse(config.Scheduling.DefaultOnDateReminderTime));
        });

        return services.BuildServiceProvider();
    }

    private static Stream GetEmbeddedAppsettings()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = "MinUddannelse.appsettings.json";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        return stream;
    }

}
