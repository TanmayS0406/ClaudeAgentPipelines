using System.Security.Claims;
using ClaudeAgentPipelines;
using ClaudeAgentPipelines.Azure;
using ClaudeAgentPipelines.Billing;
using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Data;
using ClaudeAgentPipelines.Pipelines;
using ClaudeAgentPipelines.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));
builder.Services.Configure<AzureOptions>(builder.Configuration.GetSection(AzureOptions.Section));
builder.Services.Configure<BoardOptions>(builder.Configuration.GetSection(BoardOptions.Section));
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.Section));
builder.Services.Configure<CashfreeOptions>(builder.Configuration.GetSection(CashfreeOptions.Section));

var storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not set in appsettings.json.")));

builder.Services.AddSingleton<ClaudeCli>();
builder.Services.AddSingleton<ClaudeAccountService>();
builder.Services.AddSingleton<PipelineStore>();
builder.Services.AddSingleton<RunRepository>();
builder.Services.AddSingleton<PipelineRunner>();
builder.Services.AddSingleton<RunManager>();
builder.Services.AddSingleton<AzureCli>();
builder.Services.AddSingleton<AzureAccountService>();
builder.Services.AddSingleton<BoardStore>();
builder.Services.AddSingleton<BoardNotifier>();
builder.Services.AddSingleton<FeatureOrchestrator>();
builder.Services.AddSingleton<FeatureManager>();
builder.Services.AddSingleton<BoardOrchestrator>();

builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddHttpClient(CashfreeClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<CashfreeClient>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<BillingService>();
builder.Services.AddHostedService<BillingWorker>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.Cookie.Name = "ClaudeAgentPipelines.Auth";
        options.Cookie.HttpOnly = true;
        // Lax, not Strict: Cashfree sends the customer back from its own site, and that visit must stay signed in.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // A user who was removed or disabled is signed out on their next request.
        options.Events.OnValidatePrincipal = async context =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await users.IsActiveAsync(userId, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AuthorizeFilter());
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<RequireSubscriptionFilter>();
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = storage.MaxFileBytes * storage.MaxFilesPerAgent + 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = storage.MaxFileBytes * storage.MaxFilesPerAgent + 1024 * 1024;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Pipelines run on the Claude account signed in on this computer, so only local browsers may use the app.
app.UseMiddleware<LocalClientsOnlyMiddleware>();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Boards}/{action=Index}/{id?}");

await DatabaseInitializer.InitializeAsync(
    app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(), storage, app.Environment.ContentRootPath,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
await app.Services.GetRequiredService<UserService>().EnsureSeedUserAsync();
await app.Services.GetRequiredService<PipelineStore>().EnsureSamplePipelineAsync();
var featureManager = app.Services.GetRequiredService<FeatureManager>();
await featureManager.RecoverAsync();
// Autopilot boards carry on with the features that are ready.
foreach (var project in await app.Services.GetRequiredService<BoardStore>().ListProjectsAsync())
{
    if (project.Autopilot) await featureManager.ScheduleAsync(project.Id);
}

app.Run();
