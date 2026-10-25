using System.Security.Claims;
using ClaudeAgentPipelines;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Pipelines;
using ClaudeAgentPipelines.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));

builder.Services.AddSingleton<ClaudeCli>();
builder.Services.AddSingleton<ClaudeAccountService>();
builder.Services.AddSingleton<PipelineStore>();
builder.Services.AddSingleton<RunRepository>();
builder.Services.AddSingleton<PipelineRunner>();
builder.Services.AddSingleton<RunManager>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.Cookie.Name = "ClaudeAgentPipelines.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // The app session is only valid while Claude Code on this computer is still signed in to the same account.
        options.Events.OnValidatePrincipal = async context =>
        {
            var account = context.HttpContext.RequestServices.GetRequiredService<ClaudeAccountService>();
            var status = await account.GetStatusAsync(cancellationToken: context.HttpContext.RequestAborted);
            var signedInAs = context.Principal?.FindFirstValue(ClaudeAccountService.AccountClaim);
            if (!status.LoggedIn || !string.Equals(status.Account, signedInAs, StringComparison.OrdinalIgnoreCase))
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
});

var storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
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
    pattern: "{controller=Pipelines}/{action=Index}/{id?}");

await app.Services.GetRequiredService<PipelineStore>().EnsureSamplePipelineAsync();

app.Run();
