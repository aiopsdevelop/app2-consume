using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using System.Net;
using System.Diagnostics.Metrics;

namespace core6
{
    public class Program
    {

        private static readonly ActivitySource Activity = new(nameof(Program));
        private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

        private static IConfiguration _configuration;
        private static ILogger<Program> _logger;

        public static void Main(string[] args)
        {
            try
            {
                var builder = WebApplication.CreateBuilder(args);

                // ====================================================================================================
                //                                 SETUP (Configuration , Logger)
                // ----------------------------------------------------------------------------------------------------

                SetupConfiguration();
                SetupLogger();

                // ====================================================================================================
                //                                      TRACE , METRIC , LOGS
                // ----------------------------------------------------------------------------------------------------

                var serviceName = Environment.GetEnvironmentVariable("PROJECT_NAME") ?? builder.Configuration.GetValue<string>("Otlp:ServiceName");
                string serviceVersion = Environment.GetEnvironmentVariable("PROJECT_VERSION") ?? builder.Configuration.GetValue<string>("Otlp:ServiceVersion");

                string JIRA_PROJECT_ID = Environment.GetEnvironmentVariable("JIRA_PROJECT_ID") ?? "1";
                string IMAGE = Environment.GetEnvironmentVariable("IMAGE") ?? "localhost";
                string TEMPLATE_NAME = Environment.GetEnvironmentVariable("TEMPLATE_NAME") ?? "dotnetcore6";
                string STAGE = Environment.GetEnvironmentVariable("STAGE") ?? "production";
                string TEAM_NAME = Environment.GetEnvironmentVariable("TEAM_NAME") ?? "web_backend"; // or TEAM_NAME=logic,web_front,devops,it,pm,po,mobile,qa,database,creep,...
                string ContainerName = Dns.GetHostName();
                string HOST_ID = Environment.GetEnvironmentVariable("HOST_ID") ?? "localhostId";
                string HOST_NAME = Environment.GetEnvironmentVariable("HOST_NAME") ?? "localhost";
                string SUBDOMAIN = Environment.GetEnvironmentVariable("SUBDOMAIN") ?? "localhost";
                string HOST_TYPE = Environment.GetEnvironmentVariable("HOST_TYPE") ?? "arm64";
                string OS_NAME = Environment.GetEnvironmentVariable("OS_NAME") ?? "windows";
                string OS_VERSION = Environment.GetEnvironmentVariable("OS_VERSION") ?? "2010";
                string CRM_KEY = Environment.GetEnvironmentVariable("CRM_KEY") ?? "HW-511";
                string SERVICE_NAMESPACE = Environment.GetEnvironmentVariable("SERVICE_NAMESPACE") ?? "devops";

                // https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/resource/semantic_conventions/README.md
                Action<ResourceBuilder> configureResource = r =>
                {
                    r.AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: Environment.MachineName);
                    //r.AddService("Redis", serviceVersion: "1.0.0", serviceInstanceId: Environment.MachineName);
                    r.AddAttributes(new Dictionary<string, object>
                    {
                        ["environment.name"] = STAGE,
                        ["deployment.environment"] = STAGE, // staging
                        ["team.name"] = TEAM_NAME,
                        ["team.user"] = Environment.UserName,
                        ["host.id"] = HOST_ID,
                        ["host.name"] = HOST_NAME,
                        ["host.hostname"] = SUBDOMAIN,
                        ["host.type"] = HOST_TYPE,
                        ["os.name"] = OS_NAME,
                        ["os.version"] = OS_VERSION,
                        ["issue.project.id"] = JIRA_PROJECT_ID,
                        ["issue.crm.key"] = CRM_KEY,
                        ["service.namespace"] = SERVICE_NAMESPACE,
                        ["telemetry.sdk.language"] = "dotnet",
                        ["telemetry.sdk.name"] = "opentelemetry",
                        ["container.runtime"] = "docker",
                        ["container.name"] = ContainerName,
                        ["container.image.name"] = IMAGE,
                        ["container.image.tag"] = serviceVersion,
                        ["service.template"] = TEMPLATE_NAME
                    });
                };

                // -----------------------------------------------
                //                     TRACE
                // -----------------------------------------------

                var tracingExporter = builder.Configuration.GetValue<string>("UseTracingExporter").ToLowerInvariant();

                builder.Services.AddHttpClient();
                Sdk.CreateTracerProviderBuilder()
                    .AddHttpClientInstrumentation()
                    .ConfigureResource(configureResource)
                    .AddSource(nameof(Program))

                        .AddOtlpExporter(otlpOptions =>
                        {
                            otlpOptions.Endpoint = new Uri(builder.Configuration.GetValue<string>("Otlp:Endpoint"));
                            otlpOptions.Protocol = OtlpExportProtocol.Grpc;
                        })
                    .Build();


                /*builder.Services.AddOpenTelemetryTracing(options =>
                {
                    options
                        //.AddConsoleExporter()
                        .ConfigureResource(configureResource)
                        .SetSampler(new AlwaysOnSampler())
                        .AddSource(nameof(Program))
                        .AddHttpClientInstrumentation()
                        .AddAspNetCoreInstrumentation();

                    switch (tracingExporter)
                    {
                        case "otlp":
                            options.AddOtlpExporter(otlpOptions =>
                            {
                                otlpOptions.Endpoint = new Uri(builder.Configuration.GetValue<string>("Otlp:Endpoint"));
                            });
                            break;

                        default:
                            options.AddConsoleExporter();
                            break;
                    }
                });*/

                // -----------------------------------------------
                //                     LOG
                // -----------------------------------------------
                // For options which can be bound from IConfiguration.

                builder.Services.Configure<AspNetCoreInstrumentationOptions>(builder.Configuration.GetSection("AspNetCoreInstrumentation"));

                builder.Logging.ClearProviders();

                builder.Logging.AddOpenTelemetry(options =>
                {
                    options.ConfigureResource(configureResource);

                    // Switch between Console/OTLP by setting UseLogExporter in appsettings.json.
                    var logExporter = builder.Configuration.GetValue<string>("UseLogExporter").ToLowerInvariant();
                    switch (logExporter)
                    {
                        case "otlp":
                            options.AddOtlpExporter(otlpOptions =>
                            {
                                otlpOptions.Endpoint = new Uri(builder.Configuration.GetValue<string>("Otlp:Endpoint"));
                                otlpOptions.Protocol = OtlpExportProtocol.Grpc;
                            });
                            break;
                        default:
                            options.AddConsoleExporter();
                            break;
                    }
                });

                builder.Services.Configure<OpenTelemetryLoggerOptions>(opt =>
                {
                    opt.IncludeScopes = true;
                    opt.ParseStateValues = true;
                    opt.IncludeFormattedMessage = true;
                });

                // -----------------------------------------------
                //                     Metrics
                // -----------------------------------------------
                // Switch between Prometheus/OTLP/Console by setting UseMetricsExporter in appsettings.json.

                var metricsExporter = builder.Configuration.GetValue<string>("UseMetricsExporter").ToLowerInvariant();

                var meter = new Meter(serviceName);
                builder.Services.AddOpenTelemetryMetrics(options =>
                {
                    options.ConfigureResource(configureResource)
                        .AddMeter(meter.Name)
                        .AddRuntimeInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddAspNetCoreInstrumentation();

                    switch (metricsExporter)
                    {
                        case "otlp":
                            options.AddOtlpExporter(otlpOptions =>
                            {
                                otlpOptions.Endpoint = new Uri(builder.Configuration.GetValue<string>("Otlp:Endpoint"));
                                otlpOptions.Protocol = OtlpExportProtocol.Grpc;
                            });
                            break;
                        default:
                            options.AddConsoleExporter();
                            break;
                    }
                });

                // Add services to the container.
                builder.Services.AddControllers();
                // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
                builder.Services.AddEndpointsApiExplorer();


                // ====================================================================================================
                //                                           CONSUME TEST
                // ----------------------------------------------------------------------------------------------------

                DoWork();

                // ====================================================================================================
                //                                           HOLD CONSOLE
                // ----------------------------------------------------------------------------------------------------

                System.Console.WriteLine(" Press [enter] to exit.");
                System.Console.ReadLine();

            }
            catch (Exception e)
            {
                System.Console.WriteLine(e);
                throw;
            }
        }

        public static void DoWork()
        {
            var factory = new ConnectionFactory() { HostName = _configuration["RabbitMq:Host"] , UserName = _configuration["RabbitMq:Username"], Password = _configuration["RabbitMq:Password"], DispatchConsumersAsync = true };

            var rabbitMqConnection = factory.CreateConnection();
            var rabbitMqChannel = rabbitMqConnection.CreateModel();
            var httpClient = new HttpClient { BaseAddress = new Uri(_configuration["App3UriEndpoint"]) };

            rabbitMqChannel.QueueDeclare(queue: "sample",
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            rabbitMqChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

            var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
            consumer.Received += async (model, ea) =>
            {
                await ProcessMessage(ea,
                    httpClient,
                    rabbitMqChannel);
            };

            rabbitMqChannel.BasicConsume(queue: "sample",
                autoAck: false,
                consumer: consumer);

        }

        private static async Task ProcessMessage(BasicDeliverEventArgs ea,
            HttpClient httpClient,
            IModel rabbitMqChannel)
        {
            try
            {
                var parentContext = Propagator.Extract(default, ea.BasicProperties, ExtractTraceContextFromBasicProperties);
                Baggage.Current = parentContext.Baggage;

                using (var activity = Activity.StartActivity("Process Message", ActivityKind.Consumer, parentContext.ActivityContext))
                {

                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    AddActivityTags(activity);

                    _logger.LogInformation("Message Received: " + message + " , url app3 : " + _configuration["App3UriEndpoint"]);

                    _ = await httpClient.PostAsync("/sql-to-event",
                        new StringContent(JsonSerializer.Serialize(message),
                            Encoding.UTF8,
                            "application/json"));

                    rabbitMqChannel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError($"There was an error processing the message: {ex} ");
            }
        }


        private static IEnumerable<string> ExtractTraceContextFromBasicProperties(IBasicProperties props, string key)
        {
            try
            {
                if (props.Headers.TryGetValue(key, out var value))
                {
                    var bytes = value as byte[];
                    return new[] { Encoding.UTF8.GetString(bytes) };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to extract trace context: {ex}");
            }

            return Enumerable.Empty<string>();
        }

        private static void AddActivityTags(Activity activity)
        {
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.rabbitmq.queue", "sample");
        }


        private static void SetupConfiguration()
        {

            //setup config
            var configFiles = Directory
                .GetFiles(Path.Combine(Directory.GetCurrentDirectory()),
                    "appsettings.json").ToList();

            if (!configFiles.Any())
                throw new Exception("Cannot read config file");

            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configFiles[0], true, false)
                .AddEnvironmentVariables()
                .Build();
        }

        private static void SetupLogger()
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("LoggingConsoleApp.Program", LogLevel.Debug)
                    .AddConsole();
            });

            _logger = loggerFactory.CreateLogger<Program>();
        }

    }

}