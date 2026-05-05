using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

public static class Program
{
	private static readonly JsonSerializerOptions OutputJsonOptions = new()
	{
		WriteIndented = true,
	};

	public static async Task<int> Main()
	{
		try
		{
			var projectEndpoint = GetRequiredEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT");
			var modelDeploymentName = GetRequiredEnvironmentVariable("MODEL_DEPLOYMENT_NAME");
			var cosmosEndpoint = GetRequiredEnvironmentVariable("COSMOS_ENDPOINT");
			var cosmosKey = GetRequiredEnvironmentVariable("COSMOS_KEY");
			var cosmosDatabaseName = GetRequiredEnvironmentVariable("COSMOS_DATABASE_NAME");

			var services = new ServiceCollection();
			services.AddLogging(builder =>
			{
				builder.ClearProviders();
				builder.AddSimpleConsole(options =>
				{
					options.SingleLine = true;
					options.TimestampFormat = "HH:mm:ss ";
				});
				builder.SetMinimumLevel(LogLevel.Information);
			});

			services.AddSingleton(_ => new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential()));
			services.AddSingleton<IFaultMappingService, FaultMappingService>();
			services.AddSingleton(sp =>
				new CosmosDbService(
					cosmosEndpoint,
					cosmosKey,
					cosmosDatabaseName,
					sp.GetRequiredService<ILogger<CosmosDbService>>()));
			services.AddSingleton(sp =>
				new RepairPlannerAgent(
					sp.GetRequiredService<AIProjectClient>(),
					sp.GetRequiredService<CosmosDbService>(),
					sp.GetRequiredService<IFaultMappingService>(),
					modelDeploymentName,
					sp.GetRequiredService<ILogger<RepairPlannerAgent>>()));

			// await using - like Python's "async with"
			await using var serviceProvider = services.BuildServiceProvider();

			var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
			var repairPlannerAgent = serviceProvider.GetRequiredService<RepairPlannerAgent>();
			var sampleFault = CreateSampleFault();

			await repairPlannerAgent.EnsureAgentVersionAsync();
			var workOrder = await repairPlannerAgent.PlanAndCreateWorkOrderAsync(sampleFault);

			logger.LogInformation(
				"Saved work order {WorkOrderNumber} (id={WorkOrderId}, status={Status}, assignedTo={AssignedTo})",
				workOrder.WorkOrderNumber,
				workOrder.Id,
				workOrder.Status,
				workOrder.AssignedTo ?? "unassigned");

			Console.WriteLine();
			Console.WriteLine(JsonSerializer.Serialize(workOrder, OutputJsonOptions));
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Repair Planner startup failed: {ex.Message}");
			return 1;
		}
	}

	private static DiagnosedFault CreateSampleFault()
	{
		return new DiagnosedFault
		{
			Id = $"fault-{DateTime.UtcNow:yyyyMMddHHmmss}",
			MachineId = "machine-001",
			FaultType = "curing_temperature_excessive",
			RootCause = "Failed mold heating element causing temperature overshoot.",
			Severity = "High",
			DetectedAt = DateTimeOffset.UtcNow,
			Metadata = new Dictionary<string, object?>
			{
				["MostLikelyRootCauses"] = new[]
				{
					"Heating element degradation",
					"Temperature sensor drift",
					"PLC control loop misconfiguration"
				},
				["ObservedMetric"] = "mold_temperature",
				["ObservedValue"] = 212.4,
				["Threshold"] = 205.0,
			}
		};
	}

	private static string GetRequiredEnvironmentVariable(string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidOperationException($"Missing required environment variable '{name}'.");
		}

		return value;
	}
}
