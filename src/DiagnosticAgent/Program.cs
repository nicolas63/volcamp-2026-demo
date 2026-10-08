using DiagnosticAgent;
using DiagnosticAgent.Guard;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: false);
builder.AddServiceDefaults();

builder.Services.AddSingleton<AgentTelemetry>();
builder.Services.AddOptions<AgentGuardOptions>()
    .Bind(builder.Configuration.GetSection(AgentGuardOptions.SectionName));
builder.Services.AddHttpClient(GlobalBudgetChecker.HttpClientName);
builder.Services.AddSingleton<GlobalBudgetChecker>();
builder.Services.AddSingleton<DiagnosticAgentRunner>();

var app = builder.Build();
app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    service = "diagnostic-agent",
    liveEndpoint = "POST /diagnose",
    replayEndpoint = "POST /diagnose?mode=replay",
    guardProfiles = "POST /diagnose?guardProfile=Default|Strict",
    sensitiveContentCaptured = false
}));

app.MapPost("/diagnose", async (
    DiagnosticRequest? request,
    string? mode,
    string? guardProfile,
    DiagnosticAgentRunner runner,
    CancellationToken cancellationToken) =>
{
    var question = string.IsNullOrWhiteSpace(request?.Question)
        ? "Pourquoi les commandes échouent-elles depuis quelques minutes ?"
        : request.Question;

    try
    {
        if (string.Equals(mode, "replay", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(await runner.RunReplayAsync(question, guardProfile, cancellationToken));
        }

        return Results.Ok(await runner.RunLiveAsync(question, guardProfile, cancellationToken));
    }
    catch (UnknownGuardProfileException exception)
    {
        return Results.Problem(
            title: "Profil de guard inconnu",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (GlobalBudgetExceededException exception)
    {
        return Results.Problem(
            title: "Guard déclenché : budget global GenAI dépassé",
            detail: $"{exception.Message} L'agent n'a pas été lancé. Utilisez POST /diagnose?mode=replay comme mode de secours.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["guardTriggered"] = true,
                ["guardReason"] = GuardReasons.GlobalBudget,
                ["costLastHourUsd"] = exception.Status.ObservedCostUsd,
                ["limitUsdPerHour"] = exception.Status.LimitUsd
            });
    }
    catch (AgentConfigurationException exception)
    {
        return Results.Problem(
            title: "Azure OpenAI n'est pas configuré",
            detail: $"{exception.Message} Utilisez POST /diagnose?mode=replay comme mode de secours déterministe.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "Une dépendance du diagnostic en direct a échoué",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ClientResultException exception)
    {
        return Results.Problem(
            title: "La requête Azure OpenAI a échoué",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

public partial class Program;
